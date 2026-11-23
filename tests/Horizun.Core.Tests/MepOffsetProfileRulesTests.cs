using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MepOffsetProfileRulesTests
    {
        [Fact]
        public void Offset_preserves_each_anchor_and_parallel_spacing_with_reversed_input()
        {
            double[][] Plan(double x, double y, bool reverse)
            {
                var low = new[] { x, y, 100d };
                var high = new[] { x, y, 1000d };
                var plan = MepOffsetProfileRules.Plan(reverse ? high : low, reverse ? low : high, 100,
                    500, -300, 400, 250, 1, 3, out string error);
                Assert.Null(error);
                return plan;
            }
            var first = Plan(10, 20, true);
            var second = Plan(70, 90, false);
            Assert.Equal(new[] { 10d, 20d, 100d }, first[0]);
            Assert.Equal(new[] { 10d, 20d, 600d }, first[1]);
            Assert.Equal(new[] { -290d, 420d, 600d }, first[2]);
            Assert.Equal(new[] { -290d, 420d, 350d }, first[3]);
            for (int i = 0; i < first.Length; i++)
                Assert.Equal(new[] { 60d, 70d, 0d }, new[] {
                    second[i][0] - first[i][0], second[i][1] - first[i][1], second[i][2] - first[i][2] });
        }

        [Theory]
        [InlineData(20, 500, 100)]
        [InlineData(0, 0, 100)]
        [InlineData(0, 500, 200)]
        public void Offset_rejects_nonvertical_short_and_wrong_anchor(double dx, double offset, double anchor)
        {
            Assert.Null(MepOffsetProfileRules.Plan(new[] { 0d, 0d, 100d }, new[] { dx, 0d, 900d },
                anchor, 500, offset, 0, 200, 1, 3, out string error));
            Assert.NotEmpty(error);
        }

        [Theory]
        [InlineData(33050.594, 33000, 0, 0, true, true)]
        [InlineData(33050.594, 33020, 0, 0, true, false)]
        [InlineData(33050.594, 33000, 0.2, 0, true, false)]
        [InlineData(33050.594, 33000, 0, 0.2, true, false)]
        [InlineData(33050.594, 33000, 0, 0, false, false)]
        [InlineData(36000, 33000, 0, 0, true, false)]
        public void Conduit_inherits_25_4_mm_and_only_accepts_the_measured_union_trim(double physicalX,
            double oppositeX, double offAxis, double gap, bool joined, bool accepted)
        {
            const double junction = 33000, free = 36000;
            var sizes = new Dictionary<int, double?> { [1] = 25.4 };
            MepDiameterRules.InheritConnected(new[] { 0 }, _ => sizes[1],
                _ => throw new InvalidOperationException("the connected run already has a diameter"),
                (row, size, connected) => { Assert.True(connected); sizes[row] = size; });
            Assert.Equal(25.4, sizes[0]);
            Assert.Equal(accepted, MepOffsetProfileRules.InlineFittingCoversEnd(
                physicalX - junction, offAxis, free - junction, oppositeX - junction, 0, gap, joined, 0.1));
        }

        [Theory]
        [InlineData(900, 1200, 600, -1, null)]
        [InlineData(420, 1200, 600, 0, "423.24 mm")]
        [InlineData(1, 1200, 600, 0, "423.24 mm")]
        [InlineData(900, 600, 600, 1, "602.54 mm")]
        [InlineData(900, 1200, 420, 2, "423.24 mm")]
        public void Duct_300_by_200_profile_checks_measured_elbow_takeouts_before_apply(
            double rise, double run, double fall, int expectedLeg, string minimum)
        {
            var points = MepOffsetProfileRules.Plan(new[] { 40000d, 30000d, 0d }, new[] { 40000d, 30000d, 2000d },
                0, rise, run, 0, fall, 1, 2.54, out string geometryError, measureDuctClearance: true);
            Assert.Null(geometryError);
            Assert.Equal(40000 + run, points[3][0]);
            var lengths = new[] { rise, run, fall };
            var startTrims = new[] { 0d, 300d, 900d - 479.3 };
            var endTrims = new[] { 900d - 479.3, 300d, 0d };
            for (int leg = 0; leg < 3; leg++)
            {
                string error = MepOffsetProfileRules.LegClearanceError(leg, lengths[leg] / 304.8,
                    startTrims[leg] / 304.8, endTrims[leg] / 304.8, 2.54 / 304.8);
                if (leg == expectedLeg) Assert.Contains(minimum, error);
                else Assert.Null(error);
            }
        }

        [Theory]
        [InlineData(0, 1, 90)]
        [InlineData(0, -1, -90)]
        [InlineData(-1, 0, 0)]
        [InlineData(-0.5, 0.8660254037844386, -60)]
        public void Rectangular_roll_aligns_the_width_axis_without_turning_the_section_half_a_revolution(
            double cosine, double sine, double degrees)
        {
            double roll = MepOffsetProfileRules.RectangularRoll(cosine, sine);
            Assert.Equal(degrees, roll * 180 / Math.PI, 8);
            Assert.Equal(1, Math.Abs(Math.Cos(roll) * cosine + Math.Sin(roll) * sine), 8);
        }

        [Theory]
        [InlineData(300, 200, -1, 0, true)]
        [InlineData(300, 200, 0, -1, false)]
        [InlineData(200, 300, 0, -1, true)]
        [InlineData(200, 300, -1, 0, false)]
        [InlineData(300, 200, 0.70710678, 0.70710678, false)]
        [InlineData(300, 201, 1, 0, false)]
        public void Rectangular_300_by_200_ports_must_match_dimensions_in_world_axes(
            double elbowWidth, double elbowHeight, double widthDot, double crossDot, bool mates)
        {
            Assert.Equal(mates, MepOffsetProfileRules.RectangularSectionsMate(300, 200, elbowWidth, elbowHeight,
                widthDot, crossDot, MepRoutingRules.SizeToleranceFeet * 304.8));
        }

        [Theory]
        [InlineData(1500, 3000, 1000)]
        [InlineData(900, 1200, 600)]
        public void Field_profiles_preserve_the_duct_pipe_and_conduit_anchors(double rise, double run, double fall)
        {
            double[] offsets = { 0, 800, 1400 };
            double[] sizes = { 300, 50, 27 };
            for (int i = 0; i < offsets.Length; i++)
            {
                double y = 30000 + offsets[i];
                var points = MepOffsetProfileRules.Plan(new[] { 40000d, y, 0d }, new[] { 40000d, y, 2000d },
                    0, rise, run, 0, fall, 1, 2.54, out string error, measureDuctClearance: i == 0);
                Assert.Null(error);
                Assert.Equal(new[] { 40000d, y, 0d }, points[0]);
                Assert.Equal(new[] { 40000d + run, y, rise - fall }, points[3]);
                var size = MepOffsetProfileRules.SectionMeasurement(sizes[i] / 304.8, sizes[i] / 304.8, out bool verified);
                Assert.True(verified);
                Assert.Equal(sizes[i], (double)size["measured_mm"], 8);
            }
        }

        [Fact]
        public void Leg_section_evidence_keeps_the_actual_size_and_an_unreadable_size_distinct()
        {
            double expected = 25 * MepRoutingRules.FeetPerMm;
            double withinTolerance = expected + MepRoutingRules.SizeToleranceFeet / 2;
            var close = MepOffsetProfileRules.SectionMeasurement(expected, withinTolerance, out bool verified);
            Assert.True(verified);
            Assert.Equal(withinTolerance * 304.8, (double)close["measured_mm"], 6);

            var different = MepOffsetProfileRules.SectionMeasurement(expected, expected * 2, out verified);
            Assert.False(verified);
            Assert.Equal(25, (double)different["expected_mm"], 6);
            Assert.Equal(50, (double)different["measured_mm"], 6);

            var unreadable = MepOffsetProfileRules.SectionMeasurement(expected, null, out verified);
            Assert.False(verified);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, unreadable["measured_mm"].Type);
        }

        [Fact]
        public void Conduit_prefers_explicit_then_connected_then_type_then_revit_default()
        {
            Assert.Equal(1d, MepDiameterRules.Resolve(1, 2, 3, out string source));
            Assert.Null(source);
            Assert.Equal(2d, MepDiameterRules.Resolve(null, 2, 3, out source));
            Assert.Equal("connected run", source);
            Assert.Equal(3d, MepDiameterRules.Resolve(null, null, 3, out source));
            Assert.Equal("conduit type", source);
            Assert.Null(MepDiameterRules.Resolve(null, null, null, out source));
            Assert.Contains("default", source);
        }

        [Theory]
        [InlineData(25, 50)]
        [InlineData(50, 25)]
        public void Unmeasured_defaults_seed_each_connected_component_once(double firstDefault, double otherDefault)
        {
            var sizes = new Dictionary<int, double?>();
            var probes = new List<int>();
            MepDiameterRules.InheritConnected(new[] { 1, 2, 3, 4 },
                i => sizes.TryGetValue(i - 1, out double? size) && i != 4 ? size : null,
                i => { probes.Add(i); return i == 1 ? firstDefault : otherDefault; },
                (i, size, connected) =>
                {
                    Assert.Equal(i == 2 || i == 3, connected);
                    sizes[i] = size;
                });
            Assert.Equal(new[] { 1, 4 }, probes);
            Assert.Equal(firstDefault, sizes[3]);
            Assert.Equal(otherDefault, sizes[4]);
        }

        [Fact]
        public void Inheritance_uses_the_connected_run_before_probing_a_default()
        {
            var sizes = new Dictionary<int, double?> { [3] = 50 };
            MepDiameterRules.InheritConnected(new[] { 1, 2 },
                i => sizes.TryGetValue(i + 1, out double? size) ? size : null,
                _ => throw new InvalidOperationException("default must not be probed"),
                (i, size, connected) => { Assert.True(connected); sizes[i] = size; });
            Assert.Equal(50d, sizes[1]);
        }

        [Fact]
        public void Same_parametric_fitting_can_join_equal_sizes_and_also_reduce()
        {
            var parametricFamily = new object();
            double a = 25 * MepRoutingRules.FeetPerMm, b = 50 * MepRoutingRules.FeetPerMm;
            Assert.Same(parametricFamily, MepDiameterRules.ConstructReducer(a, b, () => parametricFamily, _ => true));
            Assert.Same(parametricFamily, MepDiameterRules.ConstructReducer(a, a, () => parametricFamily, _ => true));
            var wrongPorts = Assert.Throws<InvalidOperationException>(
                () => MepDiameterRules.ConstructReducer(a, b, () => parametricFamily, _ => false));
            Assert.Contains("25 mm y 50 mm", wrongPorts.Message);
            var refused = Assert.Throws<InvalidOperationException>(() => MepDiameterRules.ConstructReducer<object>(
                a, b, () => throw new InvalidOperationException("no reducer"), _ => true));
            Assert.Contains("25 mm y 50 mm", refused.Message);
            Assert.Contains("no reducer", refused.Message);
        }

        [Theory]
        [InlineData("union", 25, 50, "transition")]
        [InlineData("union", 25, 25, "union")]
        [InlineData("elbow", 25, 50, "elbow")]
        [InlineData("transition", 25, 50, "transition")]
        public void Planned_fitting_names_the_effective_accessory(string requested, int a, int b, string effective)
        {
            Assert.Equal(effective, MepDiameterRules.FittingFor(requested,
                a * MepRoutingRules.FeetPerMm, b * MepRoutingRules.FeetPerMm));
        }

        [Fact]
        public void Diameter_mismatch_names_both_sizes_unless_reduction_is_admitted()
        {
            double a = 25 * MepRoutingRules.FeetPerMm, b = 50 * MepRoutingRules.FeetPerMm;
            Assert.Equal("diámetros distintos: 25 mm y 50 mm", MepDiameterRules.Mismatch(a, b, false));
            Assert.Null(MepDiameterRules.Mismatch(a, b, true));
            Assert.Null(MepDiameterRules.Mismatch(a, a + 1e-7, false));
        }
    }
}
