// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_code_check, the readers. Original Horizun code.
// Every reader returns "could not read" as a fact rather than a zero.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CodeCheckCommand
    {
        private const double FeetToMm = 304.8;
        private const double SqFtToM2 = 0.09290304;

        private static CheckedElement Fact(Document doc, Element e, HashSet<(string name, string unit)> paramKeys, HashSet<string> measures)
        {
            var f = new CheckedElement
            {
                Id = Rid.Value(e.Id),
                CategoryToken = CategoryToken(e.Category),
                CategoryName = SafeCatName(e.Category),
                Name = SafeName(e),
                Level = LevelName(doc, e)
            };
            try { f.TypeName = doc.GetElement(e.GetTypeId())?.Name; } catch { }
            if (e is Level) f.Level = f.Name;
            foreach (var key in paramKeys) f.Params[CheckedElement.ParamKey(key.name, key.unit)] = ReadParam(doc, e, key.name, key.unit);

            string token = f.CategoryToken;
            if (token == "OST_Doors" && measures.Contains("door_clear_width_mm"))
            {
                double? w = LengthMm(e, BuiltInParameter.DOOR_WIDTH) ?? LengthMm(e, BuiltInParameter.GENERIC_WIDTH)
                            ?? TypeLengthMm(doc, e, BuiltInParameter.DOOR_WIDTH) ?? TypeLengthMm(doc, e, BuiltInParameter.GENERIC_WIDTH);
                f.Measures["door_clear_width_mm"] = w == null
                    ? MeasuredValue.None("the door reports no width parameter.")
                    : new MeasuredValue { Value = w, Bound = "upper",
                        Basis = "NOMINAL leaf width; the clear width subtracts the stop and the leaf, so it is at most this." };
            }
            if (token == "OST_Ramps" && measures.Any(m => m.StartsWith("ramp_", StringComparison.Ordinal)))
                foreach (var kv in CodeCheckRules.RampMeasures(PlanarFaces(e))) f.Measures[kv.Key] = kv.Value;
            if (token == "OST_Stairs" && measures.Any(m => m.StartsWith("stair_", StringComparison.Ordinal)))
                StairMeasures(e as Stairs, f);
            if (token == "OST_MEPSpaces" && measures.Contains("space_illuminance_lx"))
                f.Measures["space_illuminance_lx"] = Illuminance(e);
            if (token == "OST_Rooms" && e is SpatialElement room)
                try { f.AreaM2 = room.Area * SqFtToM2; } catch { }
            return f;
        }

        private static void StairMeasures(Stairs stair, CheckedElement f)
        {
            if (stair == null)
            {
                foreach (string k in new[] { "stair_riser_mm", "stair_tread_mm", "stair_2r_plus_t_mm", "stair_run_width_mm" })
                    f.Measures[k] = MeasuredValue.None("not a component stair (legacy sketch stair or unreadable).");
                return;
            }
            double? riser = null, tread = null, width = null;
            try { riser = stair.ActualRiserHeight * FeetToMm; } catch { }
            try { tread = stair.ActualTreadDepth * FeetToMm; } catch { }
            try
            {
                foreach (ElementId id in stair.GetStairsRuns())
                    if (stair.Document.GetElement(id) is StairsRun run)
                    {
                        double w = run.ActualRunWidth * FeetToMm;
                        if (width == null || w < width) width = w;
                    }
            }
            catch { }
            f.Measures["stair_riser_mm"] = riser > 0 ? MeasuredValue.Exact(riser.Value, "Revit's actual riser height.") : MeasuredValue.None("actual riser not readable.");
            f.Measures["stair_tread_mm"] = tread > 0 ? MeasuredValue.Exact(tread.Value, "Revit's actual tread depth.") : MeasuredValue.None("actual tread not readable.");
            f.Measures["stair_2r_plus_t_mm"] = riser > 0 && tread > 0
                ? MeasuredValue.Exact(2 * riser.Value + tread.Value, "2 x actual riser + actual tread.")
                : MeasuredValue.None("riser or tread not readable.");
            f.Measures["stair_run_width_mm"] = width > 0 ? MeasuredValue.Exact(width.Value, "narrowest run's actual width; handrails not deducted.")
                : MeasuredValue.None("no run width readable.");
        }

        private static MeasuredValue Illuminance(Element space)
        {
            try
            {
                Parameter p = space.get_Parameter(BuiltInParameter.RBS_ELEC_ROOM_AVERAGE_ILLUMINATION);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double)
                    return MeasuredValue.None("the space carries no Average Estimated Illumination.");
                double lx = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Lux);
                if (lx <= 0)
                    return MeasuredValue.None("Average Estimated Illumination is 0: Revit computed none (no lighting fixtures with photometric data in the space).");
                return MeasuredValue.Exact(lx, "Revit's Average Estimated Illumination (lumen method, not a lighting calculation).");
            }
            catch (Exception ex) { return MeasuredValue.None("illuminance could not be read: " + ex.Message); }
        }

        private static List<PlanarFaceFact> PlanarFaces(Element e)
        {
            var faces = new List<PlanarFaceFact>();
            GeometryElement geo;
            try { geo = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }); } catch { return faces; }
            if (geo != null) Walk(geo, Transform.Identity, faces);
            return faces;
        }

        private static void Walk(GeometryElement geo, Transform t, List<PlanarFaceFact> faces)
        {
            foreach (GeometryObject o in geo)
            {
                if (o is GeometryInstance gi) { Walk(gi.GetSymbolGeometry(), t.Multiply(gi.Transform), faces); continue; }
                if (!(o is Solid s) || s.Faces.Size == 0) continue;
                foreach (Face face in s.Faces)
                {
                    if (!(face is PlanarFace pf)) continue;
                    XYZ n = t.OfVector(pf.FaceNormal);
                    var fact = new PlanarFaceFact { Nx = n.X, Ny = n.Y, Nz = n.Z };
                    try
                    {
                        foreach (XYZ v in pf.Triangulate().Vertices)
                        {
                            XYZ w = t.OfPoint(v);
                            fact.Points.Add(new[] { w.X * FeetToMm, w.Y * FeetToMm, w.Z * FeetToMm });
                        }
                    }
                    catch { continue; }
                    faces.Add(fact);
                }
            }
        }

        /// <summary>Levels get their placed rooms and doors, for exit_count_minus_required.</summary>
        private static void AttachLevelContents(Document doc, List<CheckedElement> facts, RequirementSet set, JObject coverage)
        {
            var keys = new HashSet<(string name, string unit)>();
            foreach (Requirement r in set.Rules.Where(x => x.Config != null))
            {
                string lp = r.Config.Value<string>("occupant_load_parameter");
                if (lp != null) keys.Add((lp, null));
                string gp = r.Config.Value<string>("occupancy_parameter");
                if (gp != null) keys.Add((gp, null));
                string dp = (r.Config["exit_door"] as JObject)?.Value<string>("parameter");
                if (dp != null) keys.Add((dp, null));
            }
            var none = new HashSet<string>();
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .Select(e => { var f = Fact(doc, e, keys, none); f.Level = LevelName(doc, e); return f; }).ToList();
            var doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType()
                .Select(e => { var f = Fact(doc, e, keys, none); f.Mark = Text(e, BuiltInParameter.ALL_MODEL_MARK); return f; }).ToList();
            foreach (CheckedElement level in facts.Where(f => f.CategoryToken == "OST_Levels"))
            {
                level.Rooms = rooms.Where(r => r.Level == level.Name).ToList();
                level.Doors = doors.Where(d => d.Level == level.Name).ToList();
            }
            coverage["rooms_read"] = rooms.Count;
            coverage["doors_read"] = doors.Count;
        }
    }
}
