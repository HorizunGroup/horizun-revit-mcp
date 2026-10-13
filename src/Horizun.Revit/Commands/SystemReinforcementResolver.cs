// -----------------------------------------------------------------------------
// Horizun Revit MCP - resolving area, path and fabric rules (acero 3.0, L4b).
// Original Horizun code.
//
// The same discipline as ReinforcementResolver, and used by the same three
// commands, so the rehearsal a person reads is the thing that runs: one host by
// id, every type by its exact name (two types with one name is a review, never a
// choice), the outline held to a face of the host's own boundary, and every
// refusal a code from a closed set, before any transaction opens.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ResolvedSystemRow
    {
        /// <summary>area, path or fabric.</summary>
        public string Kind;
        public string RuleId;
        public bool Required = true;
        public string Mark;
        public StructuralAreaRule Area;
        public StructuralPathRule Path;
        public StructuralFabricRule Fabric;
        public Element Host;
        /// <summary>slab or wall, read off the host's class.</summary>
        public string HostFamily;
        /// <summary>AreaReinforcementType, PathReinforcementType or FabricAreaType.</summary>
        public ElementType SystemType;
        public FabricSheetType SheetType;
        /// <summary>Area: layer name -> bar type. Path: "primary" -> bar type.</summary>
        public Dictionary<string, RebarBarType> BarTypes = new Dictionary<string, RebarBarType>(StringComparer.Ordinal);
        /// <summary>The outward normal of the host face the outline or path lies on.</summary>
        public double[] FaceNormal;
        public List<long> UnresolvedHostIds = new List<long>();
        public string Code;
        public string Why;
        public bool Ok { get { return Code == null; } }

        public List<double[]> PointsMm
        {
            get { return Area != null ? Area.BoundaryMm : Path != null ? Path.PathMm : Fabric != null ? Fabric.BoundaryMm : null; }
        }
    }

    public static class SystemReinforcementResolver
    {
        public const double FtToMm = 304.8;
        public const double MmToFt = 1.0 / 304.8;

        public const string CodeAreaTypeNotFound = "area_reinforcement_type_not_found";
        public const string CodeAreaTypeAmbiguous = "area_reinforcement_type_ambiguous";
        public const string CodePathTypeNotFound = "path_reinforcement_type_not_found";
        public const string CodePathTypeAmbiguous = "path_reinforcement_type_ambiguous";
        public const string CodeFabricAreaTypeNotFound = "fabric_area_type_not_found";
        public const string CodeFabricAreaTypeAmbiguous = "fabric_area_type_ambiguous";
        public const string CodeFabricSheetTypeNotFound = "fabric_sheet_type_not_found";
        public const string CodeFabricSheetTypeAmbiguous = "fabric_sheet_type_ambiguous";

        /// <summary>Every code a system row can carry. Published beside the rebar codes in refusal_codes.</summary>
        public static readonly string[] AllCodes = new[]
        {
            ReinforcementResolver.CodeHostNotFound, ReinforcementResolver.CodeHostIneligible,
            ReinforcementResolver.CodeHostUnreadable, ReinforcementResolver.CodeHostNotMeasured,
            ReinforcementResolver.CodeBarTypeNotFound, ReinforcementResolver.CodeBarTypeAmbiguous,
            ReinforcementResolver.CodeAlreadyBuilt,
            CodeAreaTypeNotFound, CodeAreaTypeAmbiguous, CodePathTypeNotFound, CodePathTypeAmbiguous,
            CodeFabricAreaTypeNotFound, CodeFabricAreaTypeAmbiguous, CodeFabricSheetTypeNotFound,
            CodeFabricSheetTypeAmbiguous
        }.Concat(SystemReinforcementRules.AllCodes).ToArray();

        /// <summary>
        /// Every area, path and fabric rule against the model. `refuseAlreadyBuilt` is the
        /// write path's question, exactly as for rebar: the plan and the apply ask it, the
        /// audit does not, because what is already built is what the audit came to check.
        /// </summary>
        public static List<ResolvedSystemRow> Resolve(Document doc, StructuralRequirementSet set, IList<long> narrowTo,
                                                      bool refuseAlreadyBuilt)
        {
            var rows = new List<ResolvedSystemRow>();
            Dictionary<long, StructuralProvenance> already = refuseAlreadyBuilt
                ? StructuralProvenanceStore.Index(doc)
                : new Dictionary<long, StructuralProvenance>();

            foreach (StructuralAreaRule r in set.AreaRules)
            {
                var row = new ResolvedSystemRow
                {
                    Kind = SystemReinforcementRules.KindArea, RuleId = r.Id, Required = r.Required, Mark = r.Mark, Area = r
                };
                if (Common(doc, set, r.Host, narrowTo, row, already)) continue;
                if (row.Ok && row.HostFamily != r.LayerFamily)
                    Refuse(row, SystemReinforcementRules.CodeLayerNotOnThisHost,
                        "rule '" + r.Id + "' declares " + r.LayerFamily + " layers (" +
                        string.Join(", ", r.Layers.Select(l => l.Name)) + ") and its host " + Rid.Value(row.Host.Id) +
                        " is a " + (row.HostFamily == SystemReinforcementRules.FamilyWall ? "wall" : "floor") +
                        ". A floor has top and bottom layers, a wall exterior and interior ones; the words are not " +
                        "translated from one to the other.");
                if (row.Ok)
                {
                    row.SystemType = Named<AreaReinforcementType>(doc, r.AreaTypeName, "area reinforcement type",
                                                                   CodeAreaTypeNotFound, CodeAreaTypeAmbiguous, row);
                }
                if (row.Ok)
                    foreach (StructuralSystemLayer l in r.Layers)
                    {
                        RebarBarType bt = Bar(doc, set, l.BarTypeId, row);
                        if (bt == null) break;
                        row.BarTypes[l.Name] = bt;
                    }
                if (row.Ok) OnFace(row, r.BoundaryMm, r.MajorDirectionMm, null, set);
                rows.Add(row);
            }

            foreach (StructuralPathRule r in set.PathRules)
            {
                var row = new ResolvedSystemRow
                {
                    Kind = SystemReinforcementRules.KindPath, RuleId = r.Id, Required = r.Required, Mark = r.Mark, Path = r
                };
                if (Common(doc, set, r.Host, narrowTo, row, already)) continue;
                if (row.Ok)
                    row.SystemType = Named<PathReinforcementType>(doc, r.PathTypeName, "path reinforcement type",
                                                                   CodePathTypeNotFound, CodePathTypeAmbiguous, row);
                if (row.Ok)
                {
                    RebarBarType bt = Bar(doc, set, r.PrimaryBarTypeId, row);
                    if (bt != null) row.BarTypes["primary"] = bt;
                }
                if (row.Ok) OnFace(row, r.PathMm, null, null, set);
                rows.Add(row);
            }

            foreach (StructuralFabricRule r in set.FabricRules)
            {
                var row = new ResolvedSystemRow
                {
                    Kind = SystemReinforcementRules.KindFabric, RuleId = r.Id, Required = r.Required, Mark = r.Mark, Fabric = r
                };
                if (Common(doc, set, r.Host, narrowTo, row, already)) continue;
                if (row.Ok)
                    row.SystemType = Named<FabricAreaType>(doc, r.FabricAreaTypeName, "fabric area type",
                                                            CodeFabricAreaTypeNotFound, CodeFabricAreaTypeAmbiguous, row);
                if (row.Ok)
                    row.SheetType = Named<FabricSheetType>(doc, r.FabricSheetTypeName, "fabric sheet type",
                                                            CodeFabricSheetTypeNotFound, CodeFabricSheetTypeAmbiguous, row);
                if (row.Ok) OnFace(row, r.BoundaryMm, r.MajorDirectionMm, r.MajorDirectionOriginMm, set);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Host, eligibility and the already-built guard, shared by the three kinds. Returns
        /// true when the rule is out of host_ids scope and contributes no row at all.
        /// </summary>
        private static bool Common(Document doc, StructuralRequirementSet set, StructuralHostSelector sel,
                                   IList<long> narrowTo, ResolvedSystemRow row,
                                   Dictionary<long, StructuralProvenance> already)
        {
            List<long> unresolved;
            List<Element> hosts = ReinforcementResolver.Hosts(doc, sel, narrowTo, out unresolved);
            if (ReinforcementResolver.Scope(doc, sel, narrowTo, hosts.Count) == HostNarrowing.Outcome.SkipOutsideHostIds)
                return true;
            row.UnresolvedHostIds = unresolved;
            if (hosts.Count == 0)
            {
                Refuse(row, ReinforcementResolver.CodeHostNotFound,
                    "no element in this document matches the host of rule '" + row.RuleId + "'" +
                    (unresolved.Count > 0 ? " (element " + string.Join(", ", unresolved) + " does not exist)" : "") + ".");
                return false;
            }
            Element host = hosts[0];
            row.Host = host;
            row.HostFamily = host is Wall ? SystemReinforcementRules.FamilyWall
                           : host is Floor ? SystemReinforcementRules.FamilySlab : null;
            if (row.HostFamily == null)
            {
                Refuse(row, SystemReinforcementRules.CodeHostNotFloorOrWall,
                    "element " + Rid.Value(host.Id) + " is a " + SafeCategory(host) + ", and an area, a path or a " +
                    "fabric area is hosted by a structural floor or wall. A Part made from one of their layers is a " +
                    "valid Revit host too, and this bridge does not resolve one.");
                return false;
            }

            bool eligible = false;
            try { eligible = RebarHostData.IsValidHost(host); }
            catch (Exception ex)
            {
                Refuse(row, ReinforcementResolver.CodeHostUnreadable,
                    "Revit would not say whether element " + Rid.Value(host.Id) + " can host reinforcement (" +
                    ex.Message + "). Unknown is not permission.");
                return false;
            }
            if (!eligible)
            {
                Refuse(row, ReinforcementResolver.CodeHostIneligible,
                    "Revit does not accept element " + Rid.Value(host.Id) + " as a reinforcement host. A floor or " +
                    "wall that is not structural is the usual reason.");
                return false;
            }

            long hostId = Rid.Value(host.Id);
            KeyValuePair<long, StructuralProvenance> prior = already.FirstOrDefault(kv =>
                kv.Value != null && string.Equals(kv.Value.RuleId, row.RuleId, StringComparison.Ordinal) &&
                kv.Value.HostElementId == hostId);
            if (prior.Value != null)
                Refuse(row, ReinforcementResolver.CodeAlreadyBuilt,
                    "element " + prior.Key + " on this host already records rule '" + row.RuleId + "' from requirement " +
                    "set '" + (prior.Value.RequirementSetId ?? "?") + "'. Building again would lay a second, coincident " +
                    "system over the first. Delete the existing one, or give this rule a different id if a second is " +
                    "intended.");
            return false;
        }

        private static T Named<T>(Document doc, string name, string what, string notFound, string ambiguous,
                                  ResolvedSystemRow row) where T : Element
        {
            int matches;
            T t = ReinforcementResolver.ByName<T>(doc, name, out matches);
            if (t == null)
                Refuse(row, matches > 1 ? ambiguous : notFound,
                    matches > 1
                        ? matches + " " + what + "s are named '" + name + "'. Which one is meant cannot be read from " +
                          "the set, and they need not carry the same settings."
                        : SystemReinforcementRules.TypeNotFoundWhy(what, name,
                              new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<Element>().Select(e => e.Name)));
            return t;
        }

        private static RebarBarType Bar(Document doc, StructuralRequirementSet set, string barTypeId, ResolvedSystemRow row)
        {
            string name = set.BarTypes[barTypeId].TypeName;
            int matches;
            RebarBarType bt = ReinforcementResolver.ByName<RebarBarType>(doc, name, out matches);
            if (bt == null)
                Refuse(row, matches > 1 ? ReinforcementResolver.CodeBarTypeAmbiguous : ReinforcementResolver.CodeBarTypeNotFound,
                    matches > 1
                        ? matches + " bar types are named '" + name + "', and they carry different diameters."
                        : "no RebarBarType in this document is named '" + name + "'. This bridge does not create bar types.");
            return bt;
        }

        /// <summary>
        /// The outline or path on ONE face of the host's own boundary; the major direction in
        /// that face; a fabric's origin in its plane.
        /// </summary>
        private static void OnFace(ResolvedSystemRow row, List<double[]> pts, double[] major, double[] origin,
                                   StructuralRequirementSet set)
        {
            string why;
            HostMesh mesh = ReinforcementResolver.MeshFor(row.Host, out why);
            if (mesh == null)
            {
                Refuse(row, ReinforcementResolver.CodeHostNotMeasured,
                    "the boundary of element " + Rid.Value(row.Host.Id) + " is what the outline is held to, and it is " +
                    "not available: " + why);
                return;
            }
            FaceFit fit = SystemReinforcementRules.OnHostFace(pts, mesh, set.Tolerances.LengthMm, set.Tolerances.AngleDegrees);
            if (!fit.Ok) { Refuse(row, fit.Code, fit.Why); return; }
            row.FaceNormal = fit.NormalMm;
            if (major != null && !SystemReinforcementRules.InPlane(major, fit.NormalMm, set.Tolerances.AngleDegrees))
            {
                Refuse(row, SystemReinforcementRules.CodeMajorDirectionNotInFace,
                    "the major direction does not lie in the host face the outline is on (outward normal [" +
                    string.Join(", ", fit.NormalMm.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) + "]).");
                return;
            }
            if (origin != null)
            {
                double off = 0;
                for (int i = 0; i < 3; i++) off += origin[i] * fit.NormalMm[i];
                if (Math.Abs(off - fit.OffsetMm) > set.Tolerances.LengthMm)
                    Refuse(row, SystemReinforcementRules.CodeNotOnHostFace,
                        "major_direction_origin_mm is " + Math.Abs(off - fit.OffsetMm).ToString("0.###", CultureInfo.InvariantCulture) +
                        " mm off the host face the outline is on.");
            }
        }

        private static void Refuse(ResolvedSystemRow row, string code, string why)
        {
            if (row.Code != null) return;
            row.Code = code;
            row.Why = why;
        }

        private static string SafeCategory(Element e)
        {
            try { return e.Category?.Name ?? e.GetType().Name; } catch { return e.GetType().Name; }
        }

        private static string SafeName(Element e)
        {
            try { return e?.Name; } catch { return null; }
        }

        // ------------------------------------------------------------ describe

        public static JObject Describe(ResolvedSystemRow r, int index)
        {
            var o = new JObject
            {
                ["index"] = index,
                ["kind"] = r.Kind,
                ["rule_id"] = r.RuleId,
                ["required"] = r.Required,
                ["host_id"] = r.Host == null ? -1 : Rid.Value(r.Host.Id),
                ["resolved"] = r.Ok
            };
            if (!r.Ok) { o["code"] = r.Code; o["why"] = r.Why; }
            if (r.UnresolvedHostIds.Count > 0) o["unresolved_host_ids"] = new JArray(r.UnresolvedHostIds);
            if (r.HostFamily != null) o["host_family"] = r.HostFamily;
            if (r.SystemType != null)
                o["system_type"] = new JObject { ["id"] = Rid.Value(r.SystemType.Id), ["name"] = SafeName(r.SystemType) };
            if (r.SheetType != null)
                o["fabric_sheet_type"] = new JObject { ["id"] = Rid.Value(r.SheetType.Id), ["name"] = SafeName(r.SheetType) };
            if (r.FaceNormal != null) o["host_face_normal"] = new JArray(r.FaceNormal.Select(x => Math.Round(x, 6)));
            if (r.Mark != null) o["mark"] = r.Mark;

            if (r.Area != null)
            {
                o["boundary_mm"] = ReinforcementResolver.PointArray(r.Area.BoundaryMm);
                o["major_direction"] = new JArray(r.Area.MajorDirectionMm);
                o["area_type_name"] = r.Area.AreaTypeName;
                var layers = new JArray();
                foreach (string slot in SystemReinforcementRules.LayersOf(r.Area.LayerFamily))
                {
                    StructuralSystemLayer l = r.Area.Layers.FirstOrDefault(x => x.Name == slot);
                    RebarBarType bt;
                    r.BarTypes.TryGetValue(slot, out bt);
                    layers.Add(l == null
                        ? new JObject { ["name"] = slot, ["active"] = false }
                        : new JObject
                        {
                            ["name"] = slot, ["active"] = true, ["bar_type"] = l.BarTypeId,
                            ["bar_type_id"] = bt == null ? -1 : Rid.Value(bt.Id), ["bar_type_name"] = SafeName(bt),
                            ["spacing_mm"] = l.SpacingMm, ["hooks"] = "none"
                        });
                }
                o["layers"] = layers;
                o["layers_mean"] = "every layer of the host's family: the declared ones built active with their bar " +
                                   "type and spacing and no hook, the others built inactive.";
            }
            if (r.Path != null)
            {
                RebarBarType bt;
                r.BarTypes.TryGetValue("primary", out bt);
                o["path_mm"] = ReinforcementResolver.PointArray(r.Path.PathMm);
                o["path_type_name"] = r.Path.PathTypeName;
                o["flip"] = r.Path.Flip;
                o["primary"] = new JObject
                {
                    ["bar_type"] = r.Path.PrimaryBarTypeId, ["bar_type_id"] = bt == null ? -1 : Rid.Value(bt.Id),
                    ["bar_type_name"] = SafeName(bt), ["spacing_mm"] = r.Path.PrimarySpacingMm,
                    ["length_mm"] = r.Path.PrimaryLengthMm, ["hooks"] = "none"
                };
                o["alternating_bars"] = false;
            }
            if (r.Fabric != null)
            {
                o["boundary_mm"] = ReinforcementResolver.PointArray(r.Fabric.BoundaryMm);
                o["major_direction"] = new JArray(r.Fabric.MajorDirectionMm);
                o["major_direction_origin_mm"] = new JArray(r.Fabric.MajorDirectionOriginMm);
                o["fabric_area_type_name"] = r.Fabric.FabricAreaTypeName;
                o["fabric_sheet_type_name"] = r.Fabric.FabricSheetTypeName;
                o["cover_offset_mm"] = r.Fabric.CoverOffsetMm.HasValue ? (JToken)r.Fabric.CoverOffsetMm.Value : JValue.CreateNull();
            }
            o["geometry_means"] =
                "the outline's corners (or the path's points) are held to ONE face of the host's own boundary and the " +
                "major direction to that face. Where Revit lays each bar or sheet from them is not predicted here.";
            return o;
        }
    }
}
