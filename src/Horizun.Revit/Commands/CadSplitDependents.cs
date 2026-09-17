// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE DEPENDENTS OF A WALL THE UPDATE SPLITS OR SHORTENS, read from the model.
//
// Revit offers no way to move a hosted family instance to another host, so an
// instance whose stretch of wall becomes a NEW piece is SUBSTITUTED: an instance of
// the same type is created on the new piece at the same point and on the same side,
// its writable instance values are carried, its CAD identity is carried, and only
// then is the old one deleted. Every substitution is reported old id -> new id.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class CadSplitDependents
    {
        /// <summary>The key a row uses to name a host the same apply creates.</summary>
        public const string CreatedFor = "$created_for";

        public static bool LineOf(Wall w, out XYZ a, out XYZ u, out double lengthMm)
        {
            a = u = null;
            lengthMm = 0;
            var line = (w?.Location as LocationCurve)?.Curve as Line;
            if (line == null) return false;
            a = line.GetEndPoint(0);
            XYZ b = line.GetEndPoint(1);
            XYZ d = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (d.GetLength() < 1e-9) return false;
            u = d.Normalize();
            lengthMm = d.GetLength() * 304.8;
            return true;
        }

        public static double AlongMm(XYZ a, XYZ u, double xMm, double yMm) =>
            (xMm - a.X * 304.8) * u.X + (yMm - a.Y * 304.8) * u.Y;

        public static List<FamilyInstance> HostedOn(Document doc, Wall w)
        {
            var list = new List<FamilyInstance>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                                                                          .Cast<FamilyInstance>())
            {
                ElementId host;
                try { host = fi.Host?.Id; } catch { continue; }
                if (host != null && host == w.Id) list.Add(fi);
            }
            return list;
        }

        /// <summary>Each hosted instance with its extent along the wall's current line.</summary>
        public static List<CadSplitDependent> Read(Document doc, Wall w, Dictionary<long, FamilyInstance> byId)
        {
            var result = new List<CadSplitDependent>();
            XYZ a, u;
            double len;
            if (!LineOf(w, out a, out u, out len)) return result;
            foreach (FamilyInstance fi in HostedOn(doc, w))
            {
                var alongs = new List<double>();
                BoundingBoxXYZ box = null;
                try { box = fi.get_BoundingBox(null); } catch { }
                if (box != null)
                    foreach (XYZ c in new[] { box.Min, box.Max, new XYZ(box.Min.X, box.Max.Y, 0), new XYZ(box.Max.X, box.Min.Y, 0) })
                        alongs.Add(AlongMm(a, u, c.X * 304.8, c.Y * 304.8));
                XYZ at = (fi.Location as LocationPoint)?.Point;
                if (at != null) alongs.Add(AlongMm(a, u, at.X * 304.8, at.Y * 304.8));
                if (alongs.Count == 0) continue;
                long id = Rid.Value(fi.Id);
                byId[id] = fi;
                string category = fi.Category?.Name ?? "(no category)";
                bool opening = fi.Category != null &&
                               (Rid.Value(fi.Category.Id) == (long)BuiltInCategory.OST_Doors ||
                                Rid.Value(fi.Category.Id) == (long)BuiltInCategory.OST_Windows);
                string mark = null;
                try { mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(); } catch { }
                result.Add(new CadSplitDependent
                {
                    ElementId = id,
                    Category = category + (string.IsNullOrEmpty(mark) ? "" : " (Mark '" + mark + "')"),
                    Lo = alongs.Min(),
                    Hi = alongs.Max(),
                    // A door or window is part of its wall's opening, and a MARKED instance would
                    // duplicate its Mark while both exist: neither is re-created by this build.
                    Recreatable = !opening && at != null && string.IsNullOrEmpty(mark)
                });
            }
            return result;
        }

        /// <summary>The instance values a substitution carries: writable, set, not governed by the placement.</summary>
        public static JObject CarriedParameters(FamilyInstance fi)
        {
            var carried = new JObject();
            foreach (Parameter p in fi.Parameters)
            {
                try
                {
                    if (p.IsReadOnly || !p.HasValue || p.StorageType == StorageType.ElementId ||
                        p.StorageType == StorageType.None) continue;
                    var internalDef = p.Definition as InternalDefinition;
                    bool builtIn = internalDef != null && internalDef.BuiltInParameter != BuiltInParameter.INVALID;
                    if (builtIn && internalDef.BuiltInParameter != BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) continue;
                    string name = p.Definition.Name;
                    if (carried[name] != null) continue;
                    JToken value = ManageSystemTypesCommand.Read(p);
                    if (value == null || value.Type == JTokenType.Null) continue;
                    if (value.Type == JTokenType.String && string.IsNullOrEmpty((string)value)) continue;
                    carried[name] = value;
                }
                catch { }
            }
            return carried;
        }

        /// <summary>A create row that re-makes this instance on another host (a concrete id or a placeholder).</summary>
        public static JObject SubstitutionRow(Document doc, FamilyInstance fi, JToken host, CadRequirementSet set,
                                              JObject carried)
        {
            XYZ at = ((LocationPoint)fi.Location).Point;
            Wall oldHost = fi.Host as Wall;
            var row = new JObject
            {
                ["kind"] = "family_instance",
                ["coordinate_mode"] = "absolute",
                ["point"] = new JArray(Math.Round(at.X * 304.8, 3), Math.Round(at.Y * 304.8, 3), Math.Round(at.Z * 304.8, 3)),
                ["type_id"] = Rid.Value(fi.GetTypeId()),
                ["host_id"] = host,
                ["face_allowance_mm"] = set.FaceProjectionMm,
                ["side_dead_band_mm"] = set.PointToleranceMm
            };
            if (oldHost != null && oldHost.LevelId != ElementId.InvalidElementId)
                row["level_id"] = Rid.Value(oldHost.LevelId);
            // THE SIDE IT LOOKS OUT OF, as the model holds it now.
            XYZ look = null;
            try { look = fi.HostFace != null ? fi.GetTotalTransform().BasisZ : fi.FacingOrientation; } catch { }
            if (look != null && Math.Abs(look.Z) < 0.5)
                row["facing_degrees"] = Math.Round(Math.Atan2(look.Y, look.X) * 180.0 / Math.PI, 6);
            try
            {
                XYZ hand = fi.HandOrientation;
                if (hand != null && Math.Abs(hand.Z) < 0.5)
                    row["rotation_degrees"] = Math.Round(Math.Atan2(hand.Y, hand.X) * 180.0 / Math.PI, 6);
            }
            catch { }
            try { if (fi.Mirrored) row["flip"] = true; } catch { }
            if (carried != null && carried.Count > 0) row["parameters"] = carried;
            return row;
        }

        /// <summary>The candidate-index entry that gives the substitute the old instance's CAD identity.</summary>
        public static JObject IndexEntry(string key, FamilyInstance fi, JObject carried)
        {
            string problem;
            CadProvenance p = CadProvenanceStore.Read(fi, out problem);
            return new JObject
            {
                ["key"] = key,
                ["element_index"] = 0,
                ["candidate_id"] = p?.CandidateId,
                ["semantic_id"] = p?.SemanticId,
                ["geometry_id"] = p?.GeometryId,
                ["rule_id"] = p?.RuleId,
                ["layer"] = p?.Layer,
                ["confidence"] = p?.Confidence ?? 0,
                ["source_entities"] = new JArray(),
                ["replaces_element_id"] = Rid.Value(fi.Id),
                ["carried_parameters"] = new JArray((carried ?? new JObject()).Properties().Select(x => x.Name)),
                ["carried_identity"] = p != null
            };
        }

        /// <summary>
        /// Replace every {"$created_for": candidate} in an action's arguments with the element this
        /// apply created for that candidate - or, in a rehearsal, with the stand-in the row names.
        /// Returns the first placeholder that cannot be resolved, or null.
        /// </summary>
        public static string Resolve(JToken token, Func<string, long?> createdFor, bool rehearsal)
        {
            if (token is JObject o)
            {
                foreach (JProperty prop in o.Properties().ToList())
                {
                    if (prop.Value is JObject holder && holder[CreatedFor] != null)
                    {
                        string cid = (string)holder[CreatedFor];
                        long? id = rehearsal ? holder.Value<long?>("rehearse_with") : createdFor(cid);
                        if (!id.HasValue)
                            return "no element was created in this apply for candidate '" + cid + "'";
                        prop.Value = id.Value;
                        continue;
                    }
                    string inner = Resolve(prop.Value, createdFor, rehearsal);
                    if (inner != null) return inner;
                }
            }
            else if (token is JArray arr)
            {
                foreach (JToken t in arr)
                {
                    string inner = Resolve(t, createdFor, rehearsal);
                    if (inner != null) return inner;
                }
            }
            return null;
        }

        public static string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
