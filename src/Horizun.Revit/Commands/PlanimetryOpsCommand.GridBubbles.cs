using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class PlanimetryOpsCommand
    {
        private sealed class BubbleHead
        {
            public Grid Grid;
            public DatumEnds End;
            public XYZ Before, Direction, Target;
            public bool Movable;
        }

        private static ArchModelEdit StaggerBubbles(Document doc, JObject request)
        {
            View view = Need<View>(doc, request.Value<long>("view_id"), "view");
            if (view.IsTemplate || !(view is ViewPlan || view is ViewSection))
                throw new ArgumentException("stagger_grid_bubbles requires a plan, section or elevation view. Nothing was written.");
            var requested = request["grid_ids"] is JArray ids ? new HashSet<long>(ids.Values<long>()) : null;
            if (requested != null) foreach (long id in requested) Need<Grid>(doc, id, "grid");
            string ends = request.Value<string>("ends") ?? "both";
            double diameter = (request.Value<double?>("bubble_diameter_mm") ?? 8) * Mm * view.Scale;
            double gap = (request.Value<double?>("gap_mm") ?? 2) * Mm * view.Scale;
            double maxOffset = (request.Value<double?>("max_offset_mm") ?? 200) * Mm * view.Scale;
            var heads = new List<BubbleHead>();
            var curves = new Dictionary<long, Line>();
            foreach (Grid grid in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => Rid.Value(g.Id)))
            {
                if (!grid.CanBeVisibleInView(view)) continue;
                var curve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault();
                bool selected = requested == null || requested.Contains(Rid.Value(grid.Id));
                if (!(curve is Line line))
                    throw new ArgumentException("Grid " + Rid.Value(grid.Id) + " is curved: stagger_grid_bubbles currently requires straight grids in the view. Nothing was written.");
                curves[Rid.Value(grid.Id)] = line;
                foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                {
                    if (!grid.HasBubbleInView(end, view) || !grid.IsBubbleVisibleInView(end, view)) continue;
                    if (grid.GetLeader(end, view) != null)
                        throw new ArgumentException("Grid " + Rid.Value(grid.Id) + " already has a leader at " + end + "; its bubble cannot be measured from the 2D curve endpoint. Nothing was written.");
                    int index = end == DatumEnds.End0 ? 0 : 1;
                    heads.Add(new BubbleHead { Grid = grid, End = end, Before = line.GetEndPoint(index),
                        Direction = index == 0 ? -line.Direction : line.Direction,
                        Movable = selected && (ends == "both" || ends == "end0" && index == 0 || ends == "end1" && index == 1) });
                }
            }
            double[] Project(XYZ point) => new[] { point.DotProduct(view.RightDirection), point.DotProduct(view.UpDirection) };
            var centers = heads.Select(h => Project(h.Before)).ToList();
            var directions = heads.Select(h => Project(h.Direction)).ToList();
            double[] shifts = PlanimetryOpsRules.BubbleOffsets(centers, directions, heads.Select(h => h.Movable).ToList(), diameter, gap, maxOffset);
            int before = PlanimetryOpsRules.BubbleOverlaps(centers, diameter);
            var targets = new List<double[]>();
            for (int i = 0; i < heads.Count; i++) { heads[i].Target = heads[i].Before + heads[i].Direction * shifts[i]; targets.Add(Project(heads[i].Target)); }
            if (PlanimetryOpsRules.BubbleOverlaps(targets, diameter) != 0)
                throw new ArgumentException("Fixed grid bubbles still overlap; include their grid_ids and ends in the operation. Nothing was written.");
            var evidence = new JObject
            {
                ["measurement_basis"] = "view_projected_curve_endpoints_with_declared_circular_bubble_envelope",
                ["bubble_diameter_mm"] = diameter / (Mm * view.Scale), ["gap_mm"] = gap / (Mm * view.Scale),
                ["visual_symbol_size_measured"] = false, ["overlaps_before"] = before,
                ["heads_measured"] = heads.Count, ["heads_moved"] = shifts.Count(s => s > 0),
                ["note"] = "The caller declares the printed symbol diameter; this measures circular envelope overlaps, not rendered glyph pixels. Only visible straight grids without leaders are supported."
            };
            if (shifts.All(s => s == 0))
            {
                evidence["no_op"] = true; evidence["overlaps_after"] = before;
                return new ArchModelEdit { ReadResult = evidence };
            }
            var edit = new ArchModelEdit { Evidence = evidence };
            edit.Summary = (JObject)evidence.DeepClone();
            edit.Summary["operation"] = "stagger_grid_bubbles";
            edit.Summary["planned_heads"] = new JArray(heads.Select(h => new JObject { ["grid_id"] = Rid.Value(h.Grid.Id), ["end"] = h.End.ToString(), ["offset_paper_mm"] = h.Target.DistanceTo(h.Before) / (Mm * view.Scale) }));
            var plannedView = ModelEditRunner.Planned(view, PlannedAction.Modify, request);
            plannedView.BeforeValues["view_scale"] = view.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
            plannedView.BeforeValues["view_axes"] = new JArray(new JArray(view.RightDirection.X, view.RightDirection.Y, view.RightDirection.Z), new JArray(view.UpDirection.X, view.UpDirection.Y, view.UpDirection.Z)).ToString(Newtonsoft.Json.Formatting.None);
            edit.Planned.Add(plannedView);
            foreach (Grid grid in heads.Select(h => h.Grid).Distinct())
            {
                var plannedGrid = ModelEditRunner.Planned(grid, PlannedAction.Modify, request);
                plannedGrid.BeforeValues["visible_heads"] = new JArray(heads.Where(h => h.Grid.Id == grid.Id).Select(h =>
                    new JObject { ["end"] = h.End.ToString(), ["point"] = new JArray(h.Before.X, h.Before.Y, h.Before.Z), ["movable"] = h.Movable })).ToString(Newtonsoft.Json.Formatting.None);
                edit.Planned.Add(plannedGrid);
            }
            Dictionary<long, double[]> labelWatch = LabelOffsets(doc, new[] { view.Id });
            edit.Apply = d =>
            {
                foreach (var group in heads.Where(h => h.Target.DistanceTo(h.Before) > 1e-9).GroupBy(h => h.Grid))
                {
                    Grid grid = group.Key;
                    Line original = curves[Rid.Value(grid.Id)];
                    XYZ start = heads.FirstOrDefault(h => h.Grid.Id == grid.Id && h.End == DatumEnds.End0)?.Target ?? original.GetEndPoint(0);
                    XYZ end = heads.FirstOrDefault(h => h.Grid.Id == grid.Id && h.End == DatumEnds.End1)?.Target ?? original.GetEndPoint(1);
                    grid.SetDatumExtentType(DatumEnds.End0, view, DatumExtentType.ViewSpecific);
                    grid.SetDatumExtentType(DatumEnds.End1, view, DatumExtentType.ViewSpecific);
                    grid.SetCurveInView(DatumExtentType.ViewSpecific, view, Line.CreateBound(start, end));
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(new[] { "head_positions", "bubble_visibility", "overlaps_after" });
                var actual = new List<double[]>();
                int matching = 0, visible = 0;
                var readback = new JArray();
                foreach (BubbleHead head in heads)
                {
                    var grid = Need<Grid>(d, Rid.Value(head.Grid.Id), "grid");
                    Curve curve = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).First();
                    XYZ point = curve.GetEndPoint(head.End == DatumEnds.End0 ? 0 : 1);
                    actual.Add(Project(point));
                    if (point.DistanceTo(head.Target) <= Mm) matching++;
                    if (grid.IsBubbleVisibleInView(head.End, view)) visible++;
                    readback.Add(new JObject { ["grid_id"] = Rid.Value(grid.Id), ["end"] = head.End.ToString(), ["center_view_feet"] = new JArray(Project(point)), ["error_mm"] = point.DistanceTo(head.Target) / Mm });
                }
                int after = PlanimetryOpsRules.BubbleOverlaps(actual, diameter);
                evidence["overlaps_after"] = after; evidence["read_back_heads"] = readback;
                WarnLabelRewrites(d, labelWatch, evidence);
                check.Compare("head_positions", heads.Count, matching);
                check.Compare("bubble_visibility", heads.Count, visible);
                check.Compare("overlaps_after", 0, after);
                return check;
            };
            return edit;
        }
    }
}
