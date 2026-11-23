// horizun_planimetry_ops without Revit. field cycle (video 2026-10-24, P2/P6/P8/P9): grids half-way up the
// elevations, viewports out of the frame, tags on top of each other, a legend without the real families.
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryOpsRulesTests
    {
        private static JObject Req(string op) => new JObject { ["target_document"] = "ARQ", ["operation"] = op };

        [Fact]
        public void legend_reports_only_explicit_regenerations()
        {
            Assert.Equal(0, (int)PlanimetryOpsRules.LegendRegenerationEvidence()["regenerations"]);
            Assert.Contains("explicit", (string)PlanimetryOpsRules.LegendRegenerationEvidence()["regeneration_scope"]);
        }
        [Fact]
        public void Each_operation_takes_its_own_fields()
        {
            var r = Req("datum_extents"); r["view_ids"] = new JArray(1); r["bubbles"] = "both";
            Assert.Null(PlanimetryOpsRules.Validate(r));
            r["bubbles"] = "top";
            Assert.Contains("end0, end1, both or none", PlanimetryOpsRules.Validate(r));
            r = Req("fit_viewport");
            Assert.Contains("viewport_ids", PlanimetryOpsRules.Validate(r));
            r = Req("resolve_overlaps"); r["view_id"] = 3; r["margin_mm"] = 5;
            Assert.Contains("margin_mm", PlanimetryOpsRules.Validate(r));
            r = Req("legend_from_families"); r["name"] = "Convenciones"; r["categories"] = new JArray("OST_ElectricalFixtures");
            Assert.Contains("cannot create a legend from nothing", PlanimetryOpsRules.Validate(r));
            r["source_legend_id"] = 9; r["label"] = "description";
            Assert.Contains("label", PlanimetryOpsRules.Validate(r));
        }

        [Fact]
        public void A_datum_range_spans_the_crop_plus_the_margin()
        {
            double[] range = PlanimetryOpsRules.Range(new List<double> { 3, -1, 7, 2 }, 0.5);
            Assert.Equal(-1.5, range[0], 9);
            Assert.Equal(7.5, range[1], 9);
        }

        [Fact]
        public void A_viewport_moves_by_the_smallest_shift_or_says_the_factor_it_needs()
        {
            double[] usable = { 0, 0, 800, 500 };
            Assert.Equal(new[] { -50.0, 0.0 }, PlanimetryOpsRules.FitShift(new double[] { 600, 100, 850, 300 }, usable));
            Assert.Equal(new[] { 10.0, -20.0 }, PlanimetryOpsRules.FitShift(new double[] { -10, 300, 200, 520 }, usable));
            Assert.Equal(new[] { 0.0, 0.0 }, PlanimetryOpsRules.FitShift(new double[] { 10, 10, 20, 20 }, usable));
            Assert.Null(PlanimetryOpsRules.FitShift(new double[] { 0, 0, 1000, 100 }, usable));
            Assert.Equal(0.8, PlanimetryOpsRules.FitFactor(new double[] { 0, 0, 1000, 100 }, usable), 9);
        }

        [Fact]
        public void An_overlapping_tag_moves_off_by_the_smaller_axis_plus_the_gap()
        {
            double[] a = { 0, 0, 10, 4 };
            Assert.Equal(new[] { 0.0, 3.0 }, PlanimetryOpsRules.Separate(a, new double[] { 2, 3, 12, 7 }, 2));   // up 1 + gap 2
            Assert.Equal(new[] { 3.0, 0.0 }, PlanimetryOpsRules.Separate(a, new double[] { 9, -10, 19, 10 }, 2)); // right 1 + gap 2
            Assert.Equal(new[] { 0.0, 0.0 }, PlanimetryOpsRules.Separate(a, new double[] { 20, 20, 30, 24 }, 2));
            Assert.False(PlanimetryOpsRules.Overlap(a, new double[] { 10, 0, 20, 4 }));   // touching is not overlapping
        }
    }
}
