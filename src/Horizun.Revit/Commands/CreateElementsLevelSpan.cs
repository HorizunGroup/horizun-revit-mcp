using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private static string LevelSpanCreationError(Document doc, Plan p, string batchException = null)
        {
            double bottom, top;
            switch (p.Kind)
            {
                case "wall":
                    bottom = p.Level.ProjectElevation + p.Offset;
                    top = p.TopLevel == null ? bottom + p.Height : p.TopLevel.ProjectElevation + p.TopOffset;
                    break;
                case "structural_column":
                case "family_instance":
                    if (p.TopLevel == null && p.Input["height"] == null) return null;
                    bottom = p.Start.Z;
                    top = p.TopLevel == null ? bottom + p.Input.Value<double>("height") * p.Scale :
                        p.TopLevel.ProjectElevation + p.TopOffset;
                    break;
                case "pipe": case "duct": case "conduit": case "cable_tray":
                    bottom = Math.Min(p.Start.Z, p.End.Z);
                    top = Math.Max(p.Start.Z, p.End.Z);
                    break;
                default: return null;
            }
            if (top < bottom) return "level_span: top is below base; nothing was created.";
            var crossed = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => LevelSpanRules.Crossed(bottom * 304.8, top * 304.8,
                    new[] { l.ProjectElevation * 304.8 }).Count != 0).ToList();
            if (crossed.Count == 0) return null;
            // THE EXCEPTION AS AN ARGUMENT. level_span_exception carries the designer's reason in
            // the call itself - no HZ_Excepcion_Nivel parameter has to exist in the model - and it
            // comes back in the reply (declared_exceptions) and in the action history receipt.
            // parameters.HZ_Excepcion_Nivel is still honoured for models that carry that parameter.
            string declared = batchException;
            string reason = !string.IsNullOrWhiteSpace(declared) ? declared.Trim()
                : (p.Input["parameters"] as JObject)?.Value<string>("HZ_Excepcion_Nivel");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                p.LevelSpanException = reason.Trim();
                p.LevelSpanSource = !string.IsNullOrWhiteSpace(declared) ? "level_span_exception" : "parameters.HZ_Excepcion_Nivel";
                p.LevelsCrossed = crossed.Select(l => l.Name).ToList();
                if (p.Summary != null) p.Summary["level_span_exception"] = DeclaredException(p);
                return null;
            }
            return "level_span: this " + p.Kind + " crosses " +
                string.Join(", ", crossed.Select(l => l.Name)) +
                " by more than 50 mm. Split it at those levels, or declare the reason in the same call with " +
                "level_span_exception (\"why it must be continuous\"; no model parameter needed). Nothing was created.";
        }

        private static JObject DeclaredException(Plan p) => new JObject
        {
            ["index"] = p.Index, ["kind"] = p.Kind, ["exception"] = "level_span",
            ["reason"] = p.LevelSpanException, ["source"] = p.LevelSpanSource,
            ["levels_crossed"] = new JArray(p.LevelsCrossed ?? new List<string>())
        };

        /// <summary>Every level-span exception the batch declared, for the reply and the receipt.</summary>
        private static JArray DeclaredExceptions(IEnumerable<Plan> plans)
            => new JArray(plans.Where(p => p.LevelSpanException != null).Select(DeclaredException));

        /// <summary>Whether this dry run must build and roll back in Revit (see GeometryRehearsalRules).</summary>
        private static bool RehearsesGeometryChecks(Document doc, List<Plan> plans)
        {
            // roof, reference_plane and a sloped floor are always built in Revit and rolled back: their API calls (per-edge
            // overhang/offset, the plane's cut vector, the slope arrow) are only measured on Revit's own geometry.
            if (plans.Any(p => p.Kind == "roof" || p.Kind == "reference_plane" || (p.Kind == "floor" && p.CeilingArrow != null))) return true;
            // Revit resolves point-room occupancy for its effective level and phase at commit.
            if (plans.Any(p => p.Kind == "room" && !p.Enclosed)) return true;
            bool stairs = plans.Any(p => p.Kind == "stairs");
            bool newFloor = plans.Any(p => p.Kind == "floor");
            bool newWall = plans.Any(p => p.Kind == "wall" || p.Kind == "wall_profile");
            if (!stairs && !newFloor && !newWall) return false;
            bool modelHasWalls = newFloor && new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement() != null;
            bool modelHasFinishFloors = newWall && new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
                .Any(f => FloorWallOverlapGuard.IsFinishFloor(doc, f));
            return GeometryRehearsalRules.ShouldRehearse(plans.Count, stairs, newFloor, newWall, modelHasWalls, modelHasFinishFloors);
        }
    }
}
