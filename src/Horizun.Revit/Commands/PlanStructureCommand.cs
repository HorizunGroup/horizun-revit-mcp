// -----------------------------------------------------------------------------
// Horizun Revit MCP - deterministic, read-only STRUCTURAL layout planning.
//
// horizun_plan_structure answers "column this grid, frame these bays" the way
// plan_views answers room production: MEASURE the grids and what already
// stands, DECIDE with rules proved in Core/StructuralLayoutRules.cs, and
// return a complete horizun_create_elements request the caller inspects,
// rehearses and applies. This command writes NOTHING - create_elements stays
// the single rehearsed, confirmed and re-read write path.
//
// The account is the point: every crossing found, every crossing an existing
// column already occupies (measured by DISTANCE, never by name), every span a
// beam already covers, every span too short to be real - each with its code,
// and a coverage verdict that is never optimistic.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class PlanStructureCommand : ICommand
    {
        public string Name => "horizun_plan_structure";
        public string Description =>
            "Plan structural columns on grid intersections and framing between consecutive intersections, " +
            "deduplicated against what already stands, and return a ready horizun_create_elements dry-run " +
            "request. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();
            if (operation != "columns_on_grid_intersections" && operation != "beams_along_grids")
                return CommandResult.Fail("operation must be columns_on_grid_intersections or beams_along_grids.");

            // ---- shared resolution ------------------------------------------------
            long levelId = request.Value<long?>("level_id") ?? -1;
            Level level = Rid.CanRepresent(levelId) ? doc.GetElement(Rid.Make(levelId)) as Level : null;
            if (level == null) return CommandResult.Fail("level_id must identify a Level; placements land on it.");

            long typeId = request.Value<long?>("type_id") ?? -1;
            FamilySymbol symbol = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as FamilySymbol : null;
            BuiltInCategory needed = operation == "columns_on_grid_intersections"
                ? BuiltInCategory.OST_StructuralColumns : BuiltInCategory.OST_StructuralFraming;
            if (symbol == null || symbol.Category == null || Rid.Value(symbol.Category.Id) != (long)needed)
                return CommandResult.Fail("type_id must identify a FamilySymbol in " +
                    (needed == BuiltInCategory.OST_StructuralColumns ? "OST_StructuralColumns" : "OST_StructuralFraming") + ".");

            List<GridSegment> segments;
            JArray unplannable = new JArray();
            string gridError = CollectGrids(doc, request, segments: out segments, unplannable);
            if (gridError != null) return CommandResult.Fail(gridError);
            if (segments.Count < 2)
                return CommandResult.Fail("fewer than two usable straight grids were found (" + segments.Count +
                    "); nothing can cross. " + (unplannable.Count > 0 ? "Unusable: " + unplannable.ToString() : ""));

            List<GridIntersection> crossings = StructuralLayoutRules.Intersections(segments);
            var result = new JObject
            {
                ["operation"] = operation,
                ["level"] = new JObject { ["id"] = Rid.Value(level.Id), ["name"] = level.Name },
                ["type"] = new JObject { ["id"] = Rid.Value(symbol.Id), ["name"] = SafeName(symbol) },
                ["grids_used"] = segments.Count,
                ["grids_unusable"] = unplannable,
                ["intersections_found"] = crossings.Count
            };
            if (crossings.Count == 0)
            {
                result["coverage"] = "nothing_found";
                result["note"] = "the selected grids do not cross within their drawn extents; nothing was planned.";
                return CommandResult.Ok(result);
            }

            if (operation == "columns_on_grid_intersections")
                return PlanColumns(doc, request, level, symbol, crossings, result);
            return PlanBeams(doc, request, level, symbol, segments, crossings, result);
        }

        // ------------------------------------------------------------------ columns
        private CommandResult PlanColumns(Document doc, JObject request, Level level, FamilySymbol symbol,
                                          List<GridIntersection> crossings, JObject result)
        {
            // Existing structural columns, by measured position at this level's storey.
            var existing = new List<double[]>();
            foreach (FamilyInstance column in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType()
                         .OfType<FamilyInstance>())
            {
                try
                {
                    if (column.Location is LocationPoint location)
                        existing.Add(new[] { location.Point.X, location.Point.Y });
                }
                catch { }
            }

            List<GridIntersection> toPlace, alreadyPresent;
            StructuralLayoutRules.DedupColumns(crossings, existing, out toPlace, out alreadyPresent);

            var elements = new JArray();
            var planned = new JArray();
            foreach (GridIntersection crossing in toPlace)
            {
                planned.Add(new JObject
                {
                    ["at"] = StructuralLayoutRules.Describe(crossing),
                    ["point_mm"] = new JArray(Math.Round(crossing.X * 304.8, 1), Math.Round(crossing.Y * 304.8, 1))
                });
                elements.Add(new JObject
                {
                    ["kind"] = "structural_column",
                    ["type_id"] = Rid.Value(symbol.Id),
                    ["level_id"] = Rid.Value(level.Id),
                    ["point"] = new JArray(Math.Round(crossing.X * 304.8, 1), Math.Round(crossing.Y * 304.8, 1), 0)
                });
            }
            var omitted = new JArray(alreadyPresent.Select(crossing => new JObject
            {
                ["at"] = StructuralLayoutRules.Describe(crossing),
                ["code"] = StructuralLayoutRules.CodeAlreadyPresent,
                ["reason"] = "a structural column already stands within " +
                             Math.Round(StructuralLayoutRules.SamePlaceToleranceFeet * 304.8, 1) +
                             " mm of this crossing (or an earlier crossing of this plan claims the same spot)."
            }));

            result["planned"] = planned;
            result["planned_count"] = planned.Count;
            result["omitted"] = omitted;
            result["coverage"] = planned.Count == 0 ? "none"
                : (omitted.Count == 0 ? "complete" : "partial");
            result["existing_columns_measured"] = ((JArray)result["grids_unusable"]).Count >= 0 ? existing.Count : existing.Count;
            AttachNextArguments(doc, result, elements);
            return CommandResult.Ok(result);
        }

        // ------------------------------------------------------------------- beams
        private CommandResult PlanBeams(Document doc, JObject request, Level level, FamilySymbol symbol,
                                        List<GridSegment> segments, List<GridIntersection> crossings, JObject result)
        {
            double minSpanMm = request.Value<double?>("min_span_mm") ?? 300.0;
            if (minSpanMm < 0 || minSpanMm > 20000)
                return CommandResult.Fail("min_span_mm must be between 0 and 20000.");

            var existingMid = new List<double[]>();
            foreach (FamilyInstance framing in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType()
                         .OfType<FamilyInstance>())
            {
                try
                {
                    if (framing.Location is LocationCurve location && location.Curve != null)
                    {
                        XYZ mid = location.Curve.Evaluate(0.5, true);
                        existingMid.Add(new[] { mid.X, mid.Y });
                    }
                }
                catch { }
            }

            var elements = new JArray();
            var planned = new JArray();
            int suppressedExisting = 0, suppressedShort = 0;
            foreach (GridSegment segment in segments)
            {
                List<BeamSpan> spans; int existingHere, shortHere;
                StructuralLayoutRules.BeamSpans(crossings, segment.Name, segment.ElementId, existingMid,
                                                minSpanMm / 304.8, out spans, out existingHere, out shortHere);
                suppressedExisting += existingHere; suppressedShort += shortHere;
                foreach (BeamSpan span in spans)
                {
                    planned.Add(new JObject
                    {
                        ["grid"] = span.Grid,
                        ["from"] = span.FromCrossing, ["to"] = span.ToCrossing,
                        ["length_mm"] = Math.Round(Math.Sqrt(
                            Math.Pow(span.X2 - span.X1, 2) + Math.Pow(span.Y2 - span.Y1, 2)) * 304.8, 1)
                    });
                    elements.Add(new JObject
                    {
                        ["kind"] = "structural_framing",
                        ["type_id"] = Rid.Value(symbol.Id),
                        ["level_id"] = Rid.Value(level.Id),
                        ["structural_type"] = "Beam",
                        ["start"] = new JArray(Math.Round(span.X1 * 304.8, 1), Math.Round(span.Y1 * 304.8, 1), 0),
                        ["end"] = new JArray(Math.Round(span.X2 * 304.8, 1), Math.Round(span.Y2 * 304.8, 1), 0)
                    });
                }
            }
            result["planned"] = planned;
            result["planned_count"] = planned.Count;
            result["suppressed_existing_beams"] = suppressedExisting;
            result["suppressed_short_spans"] = suppressedShort;
            result["coverage"] = planned.Count == 0 ? "none"
                : (suppressedExisting + suppressedShort == 0 ? "complete" : "partial");
            AttachNextArguments(doc, result, elements);
            return CommandResult.Ok(result);
        }

        // ------------------------------------------------------------------ shared
        private static string CollectGrids(Document doc, JObject request, out List<GridSegment> segments,
                                           JArray unplannable)
        {
            segments = new List<GridSegment>();
            var wanted = new HashSet<long>();
            if (request["grid_ids"] is JArray ids)
                foreach (JToken token in ids)
                {
                    long raw = token.Value<long?>() ?? -1;
                    if (!Rid.CanRepresent(raw)) return "grid_ids contains a value outside ElementId range.";
                    wanted.Add(raw);
                }
            foreach (Grid grid in new FilteredElementCollector(doc).OfClass(typeof(Grid)).OfType<Grid>())
            {
                long id = Rid.Value(grid.Id);
                if (wanted.Count > 0 && !wanted.Contains(id)) continue;
                Curve curve = null;
                try { curve = grid.Curve; } catch { }
                if (curve is Line line)
                {
                    XYZ start = line.GetEndPoint(0), end = line.GetEndPoint(1);
                    segments.Add(new GridSegment
                    {
                        Name = SafeName(grid), ElementId = id,
                        X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y
                    });
                }
                else
                {
                    unplannable.Add(new JObject
                    {
                        ["grid_id"] = id, ["name"] = SafeName(grid),
                        ["reason"] = curve == null ? "its curve could not be read"
                            : "it is not a straight line; arc grids are not planned - name straight grids explicitly"
                    });
                }
            }
            if (wanted.Count > 0 && segments.Count + unplannable.Count < wanted.Count)
                return "grid_ids named " + wanted.Count + " grid(s) but only " + (segments.Count + unplannable.Count) +
                       " resolved as grids in this document.";
            return null;
        }

        private static void AttachNextArguments(Document doc, JObject result, JArray elements)
        {
            if (elements.Count == 0) return;
            result["next_arguments"] = new JObject
            {
                ["tool"] = "horizun_create_elements",
                ["arguments"] = new JObject
                {
                    ["target_document"] = doc.Title,
                    ["units"] = "mm",
                    ["elements"] = elements
                }
            };
        }

        private static string SafeName(Element element)
        {
            try { return element?.Name; } catch { return null; }
        }
    }
}
