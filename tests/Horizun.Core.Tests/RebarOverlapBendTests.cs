// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// MEASURED, Revit 2024 (anonymized regression case, etapa 1c): a foundation beam's corner bars (#4) declared in the corner of its
// #3 stirrups, at cover + stirrup + half a bar from both faces - touching the stirrup's sharp declared corner. The
// plan's overlap check, on the DECLARED centrelines (sharp corners), found nothing; the audit, on the bars Revit
// DREW, reported overlapping_bar at -10.29 mm clear for every corner bar against every stirrup zone: Revit draws
// a stirrup's corner as a bend of its stirrup/tie bend diameter, and that bend runs through the corner bar.
// The plan now measures a declared corner as Revit draws it: a bend whose centreline radius is half the bar type's
// bend diameter (stirrup/tie or standard, by the row's style) plus half the model diameter. A row that does not
// carry its bend diameters is measured sharp, as before.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarOverlapBendTests
    {
        private static JArray Pts(IEnumerable<double[]> p) => new JArray(p.Select(x => (object)new JArray(x[0], x[1], x[2])).ToArray());

        // The shape ReinforcementResolver.DescribeRebarRow publishes, bar type included as it reads it.
        private static JObject Row(string id, string style, double modelDia, double? stdBend, double? tieBend, IEnumerable<double[]> curve,
                                   bool closed, double[] normal, double[] positions)
        {
            var bt = new JObject { ["id"] = 300, ["name"] = "B" + modelDia, ["nominal_diameter_mm"] = modelDia, ["model_diameter_mm"] = modelDia };
            if (stdBend.HasValue) bt["standard_bend_diameter_mm"] = stdBend.Value;
            if (tieBend.HasValue) bt["stirrup_tie_bend_diameter_mm"] = tieBend.Value;
            return new JObject
            {
                ["index"] = 0, ["rule_id"] = id, ["host_id"] = 5001, ["will_build"] = true, ["required"] = true,
                ["bar_type"] = bt, ["style"] = style, ["closed"] = closed, ["curve_mm"] = Pts(curve),
                ["normal"] = new JObject { ["x"] = normal[0], ["y"] = normal[1], ["z"] = normal[2] },
                ["layout"] = new JObject
                {
                    ["rule"] = positions.Length == 1 ? "single" : "number_with_spacing", ["number_of_bar_positions"] = positions.Length,
                    ["include_first_bar"] = true, ["include_last_bar"] = true, ["bars_on_normal_side"] = true,
                    ["positions_mm"] = new JArray(positions.Cast<object>().ToArray()), ["signed_positions_mm"] = new JArray(positions.Cast<object>().ToArray())
                },
                ["belongs_to"] = new JObject { ["declared_rule_id"] = id }
            };
        }

        // An anonymized BEAM-A: 250 x 400, cover 50, #3 stirrups 150 x 300 out to out, #4 corner bar. Section in
        // the plane x = const (y across, z up), the beam along +X. Section centre (500, -400): the numbers are the
        // section's, the frame is the beam's.
        private const double D3 = 9.525, D4 = 12.7, Bw = 250, H = 400, Cover = 50;
        private static List<double[]> Stirrup(double x)
        {
            double w = 150 - D3, h = 300 - D3, yc = 500, zc = -400;
            return new List<double[]> { new[] { x, yc - w / 2, zc + h / 2 }, new[] { x, yc + w / 2, zc + h / 2 }, new[] { x, yc + w / 2, zc - h / 2 }, new[] { x, yc - w / 2, zc - h / 2 } };
        }
        private static List<double[]> CornerBar()
        {
            double y = 500 - (Bw / 2 - Cover - D3 - D4 / 2), z = -400 - (H / 2 - Cover - D3 - D4 / 2);
            return new List<double[]> { new[] { 300.0, y, z }, new[] { 2000.0, y, z } };
        }

        private static OverlapReport Check(JObject a, JObject b)
        {
            OverlapMember ma = RebarOverlap.FromPlanRow(a); ma.Tag = 0;
            OverlapMember mb = RebarOverlap.FromPlanRow(b); mb.Tag = 1;
            return RebarOverlap.Check(new List<OverlapMember> { ma, mb }, new StructuralClearances(), 2.0);
        }

        [Fact]
        public void A_corner_bar_in_a_stirrups_declared_corner_is_cut_by_the_bend_Revit_draws_there()
        {
            JObject st = Row("BEAM-A-T1", "stirrup_tie", D3, 57.0, 57.0, Stirrup(1000), true, new double[] { 1, 0, 0 }, new double[] { 0, 80, 160 });
            JObject bar = Row("BEAM-A-INF-L", "standard", D4, 76.2, 57.0, CornerBar(), false, new double[] { 0, 1, 0 }, new double[] { 0 });
            OverlapReport r = Check(st, bar);
            OverlapPair p = Assert.Single(r.Pairs);
            Assert.Equal(RebarOverlap.KindIntersection, p.Kind);
            Assert.InRange(p.ClearMm, -11.0, -8.0);   // measured in the model: -10.29
        }

        [Fact]
        public void A_row_without_its_bend_diameters_is_measured_sharp_as_before()
        {
            JObject st = Row("BEAM-A-T1", "stirrup_tie", D3, null, null, Stirrup(1000), true, new double[] { 1, 0, 0 }, new double[] { 0, 80, 160 });
            JObject bar = Row("BEAM-A-INF-L", "standard", D4, null, null, CornerBar(), false, new double[] { 0, 1, 0 }, new double[] { 0 });
            Assert.Empty(Check(st, bar).Pairs);
        }

        [Fact]
        public void The_bend_follows_the_style_stirrup_or_standard()
        {
            List<double[]> sq = Stirrup(1000);
            var open = new List<double[]> { sq[0], sq[1], sq[2] };
            List<double[]> tie = RebarOverlap.DrawnCorners(open, false, 57.0 / 2 + D3 / 2);
            List<double[]> sharp = RebarOverlap.DrawnCorners(open, false, 0);
            Assert.Equal(3, sharp.Count);
            Assert.True(tie.Count > 3);
            // the bend's middle sits (sqrt2 - 1) R inside the sharp corner
            double R = 57.0 / 2 + D3 / 2;
            double[] c = sq[1];
            double nearest = tie.Min(q => System.Math.Sqrt((q[0] - c[0]) * (q[0] - c[0]) + (q[1] - c[1]) * (q[1] - c[1]) + (q[2] - c[2]) * (q[2] - c[2])));
            Assert.InRange(nearest, (System.Math.Sqrt(2) - 1) * R - 0.05, (System.Math.Sqrt(2) - 1) * R + 0.05);
        }
    }
}
