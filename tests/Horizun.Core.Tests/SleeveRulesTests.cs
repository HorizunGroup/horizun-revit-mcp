// Horizun Revit MCP - original Horizun code. Pure geometry for
// horizun_resolve_clash's propose_opening/apply_opening (sleeves and structural
// openings) - crossing, size-with-clearance and host-kind/route decisions.
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SleeveRulesTests
    {
        [Fact]
        public void LineBoxIntersect_finds_entry_and_exit_for_a_run_straight_through_a_wall()
        {
            // A wall box 200mm thick along X, a run crossing it perpendicular to X at its centre.
            var wall = new ResolveBox(0, -1000, 0, 200, 1000, 500);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, 100, 250 }, new[] { 500.0, 100, 250 }, wall,
                out double[] entry, out double[] exit, out string code);
            Assert.True(ok);
            Assert.Null(code);
            Assert.Equal(0, entry[0], 3);
            Assert.Equal(200, exit[0], 3);
            double[] mid = SleeveRules.Midpoint(entry, exit);
            Assert.Equal(100, mid[0], 3);
        }

        [Fact]
        public void LineBoxIntersect_reports_no_crossing_when_the_run_misses_the_host_sideways()
        {
            // Constant in Y (parallel to the box's X/Z faces) and entirely outside the box's Y
            // range: caught by the axis-aligned "outside and parallel" branch.
            var wall = new ResolveBox(0, -1000, 0, 200, 1000, 500);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, 5000, 250 }, new[] { 500.0, 5000, 250 }, wall,
                out double[] entry, out double[] exit, out string code);
            Assert.False(ok);
            Assert.Equal(SleeveRules.CodeParallel, code);
            Assert.Null(entry); Assert.Null(exit);
        }

        [Fact]
        public void LineBoxIntersect_reports_no_crossing_when_the_run_passes_diagonally_beside_the_host()
        {
            // A run that moves in every axis but still slips past the box's Z range - only the
            // X/Y interval check can catch this one, so tmin/tmax must actually cross to false.
            var wall = new ResolveBox(-100, -100, 0, 100, 100, 50);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, -500, 1000.0 }, new[] { 500.0, 500, 1100.0 }, wall,
                out double[] entry, out double[] exit, out string code);
            Assert.False(ok);
            Assert.Equal(SleeveRules.CodeNoCrossing, code);
            Assert.Null(entry); Assert.Null(exit);
        }

        [Fact]
        public void LineBoxIntersect_reports_no_crossing_when_the_run_stops_short_of_the_host()
        {
            // The segment ends BEFORE reaching the box along X: clipped to [0,1] of the segment
            // itself, so it must not extrapolate past the run's own two endpoints.
            var wall = new ResolveBox(1000, -1000, 0, 1200, 1000, 500);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, 0, 250 }, new[] { 500.0, 0, 250 }, wall,
                out double[] entry, out double[] exit, out string code);
            Assert.False(ok);
            Assert.Equal(SleeveRules.CodeNoCrossing, code);
        }

        [Fact]
        public void LineBoxIntersect_reports_parallel_when_the_run_lies_outside_a_flat_host()
        {
            // The run runs along X at Z=1000, entirely above a thin floor box at Z in [0,300].
            var floor = new ResolveBox(-1000, -1000, 0, 1000, 1000, 300);
            bool ok = SleeveRules.LineBoxIntersect(new[] { -500.0, 0, 1000 }, new[] { 500.0, 0, 1000 }, floor,
                out double[] entry, out double[] exit, out string code);
            Assert.False(ok);
            Assert.Equal(SleeveRules.CodeParallel, code);
        }

        [Fact]
        public void OpeningSize_adds_clearance_and_keeps_round_square()
        {
            SleeveRules.OpeningSize(150, 150, 50, "round", out double w, out double h, out string shape);
            Assert.Equal(SleeveRules.ShapeRound, shape);
            Assert.Equal(200, w, 3);
            Assert.Equal(200, h, 3);
        }

        [Fact]
        public void OpeningSize_keeps_width_and_height_independent_for_a_rectangular_duct()
        {
            SleeveRules.OpeningSize(400, 200, 50, "rectangular", out double w, out double h, out string shape);
            Assert.Equal(SleeveRules.ShapeRect, shape);
            Assert.Equal(450, w, 3);
            Assert.Equal(250, h, 3);
        }

        [Theory]
        [InlineData("OST_Walls", SleeveRules.HostWall, SleeveRules.RouteWallOpening)]
        [InlineData("OST_Floors", SleeveRules.HostFloor, SleeveRules.RouteFloorOpening)]
        [InlineData("OST_Roofs", SleeveRules.HostRoof, SleeveRules.RouteFloorOpening)]
        [InlineData("OST_Ceilings", SleeveRules.HostCeiling, SleeveRules.RouteFloorOpening)]
        [InlineData("OST_StructuralFraming", SleeveRules.HostFramingOrColumn, SleeveRules.RouteSleeveOnly)]
        [InlineData("OST_StructuralColumns", SleeveRules.HostFramingOrColumn, SleeveRules.RouteSleeveOnly)]
        [InlineData("OST_Furniture", SleeveRules.HostUnsupported, SleeveRules.RouteRefused)]
        public void HostKindOf_and_RouteFor_map_every_named_host(string bic, string expectedKind, string expectedRoute)
        {
            string kind = SleeveRules.HostKindOf(bic);
            Assert.Equal(expectedKind, kind);
            Assert.Equal(expectedRoute, SleeveRules.RouteFor(kind));
        }

        [Fact]
        public void FootprintHalfExtent_is_the_bare_section_for_a_square_on_crossing()
        {
            // A 114.3 mm OD pipe along Y through a 200 mm wall whose normal is Y; opening axes X (along the wall) and Z.
            double[] u = { 0, 1, 0 }, n = { 0, 1, 0 };
            SleeveRules.DefaultSectionAxes(u, out double[] e1, out double[] e2);
            Assert.Equal(57.15, SleeveRules.FootprintHalfExtent(u, n, new double[] { 1, 0, 0 }, e1, e2, true, 57.15, 200), 6);
            Assert.Equal(57.15, SleeveRules.FootprintHalfExtent(u, n, new double[] { 0, 0, 1 }, e1, e2, true, 57.15, 200), 6);
        }

        [Fact]
        public void FootprintHalfExtent_widens_a_skewed_crossing_by_section_over_cos_plus_thickness_drift()
        {
            // The same pipe at 45 degrees in plan through a 200 mm wall: along the wall the section
            // spreads to r*sqrt(2) and the centre drifts 200 mm across the thickness (half each side).
            double s = System.Math.Sqrt(0.5);
            double[] u = { s, s, 0 }, n = { 0, 1, 0 };
            SleeveRules.DefaultSectionAxes(u, out double[] e1, out double[] e2);
            Assert.Equal(50 * System.Math.Sqrt(2) + 100, SleeveRules.FootprintHalfExtent(u, n, new double[] { 1, 0, 0 }, e1, e2, true, 50, 200), 6);
            // Vertically nothing changes: the run is horizontal.
            Assert.Equal(50, SleeveRules.FootprintHalfExtent(u, n, new double[] { 0, 0, 1 }, e1, e2, true, 50, 200), 6);
            // Nearly parallel to the face: refused as NaN rather than sized to infinity.
            Assert.True(double.IsNaN(SleeveRules.FootprintHalfExtent(new double[] { 1, 0.1, 0 }, n, new double[] { 1, 0, 0 }, e1, e2, true, 50, 200)));
        }

        [Fact]
        public void FootprintHalfExtent_sizes_a_rectangle_on_its_axes_and_circumscribes_it_without_them()
        {
            double[] up = { 0, 0, 1 }, n = { 0, 0, 1 };
            // A vertical 400x400 (half 200) duct turned 45 degrees in plan: on known axes the X extent is 200*sqrt(2).
            double s = System.Math.Sqrt(0.5);
            Assert.Equal(200 * System.Math.Sqrt(2), SleeveRules.FootprintHalfExtent(up, n, new double[] { 1, 0, 0 }, new[] { s, s, 0 }, new[] { -s, s, 0 }, false, 200, 250), 6);
            // Axes unknown: the circumscribed circle, safe for every rotation.
            Assert.Equal(200 * System.Math.Sqrt(2), SleeveRules.FootprintHalfExtent(up, n, new double[] { 1, 0, 0 }, null, null, false, 200, 250), 6);
            // Axis-aligned it is exactly the half side.
            Assert.Equal(200, SleeveRules.FootprintHalfExtent(up, n, new double[] { 1, 0, 0 }, new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, false, 200, 250), 6);
        }

        [Fact]
        public void DirectionRefusal_gates_steep_runs_out_of_walls_and_flat_runs_out_of_floors()
        {
            Assert.Null(SleeveRules.DirectionRefusal(SleeveRules.RouteWallOpening, new double[] { 1, 0, 0.1 }));
            Assert.Equal(SleeveRules.CodeTooSteepForWall, SleeveRules.DirectionRefusal(SleeveRules.RouteWallOpening, new double[] { 0.1, 0, 1 }));
            Assert.Null(SleeveRules.DirectionRefusal(SleeveRules.RouteFloorOpening, new double[] { 0, 0, 1 }));
            Assert.Equal(SleeveRules.CodeTooFlatForFloor, SleeveRules.DirectionRefusal(SleeveRules.RouteFloorOpening, new double[] { 1, 0, 0.02 }));
            Assert.Null(SleeveRules.DirectionRefusal(SleeveRules.RouteSleeveOnly, new double[] { 1, 0, 0 }));
        }

        [Fact]
        public void SameGeometry_refuses_a_drift_beyond_one_millimetre_or_a_shape_change()
        {
            double[] c = { 1000, 2000, 3000 };
            Assert.True(SleeveRules.SameGeometry(c, 150, 150, "round", new double[] { 1000.5, 2000, 3000 }, 150.4, 150, "round"));
            Assert.False(SleeveRules.SameGeometry(c, 150, 150, "round", new double[] { 1002, 2000, 3000 }, 150, 150, "round"));
            Assert.False(SleeveRules.SameGeometry(c, 150, 150, "round", c, 250, 250, "round"));
            Assert.False(SleeveRules.SameGeometry(c, 150, 150, "round", c, 150, 150, "rect"));
            Assert.False(SleeveRules.SameGeometry(null, 150, 150, "round", c, 150, 150, "round"));
        }

        [Fact]
        public void UnintendedNewPairs_drops_only_the_sleeve_inside_its_own_host()
        {
            var before = new[] { ClashResolveRules.PairKey(10, 20) };
            var after = new[] { ClashResolveRules.PairKey(10, 20), ClashResolveRules.PairKey(99, 20), ClashResolveRules.PairKey(99, 10), ClashResolveRules.PairKey(10, 30) };
            var fresh = SleeveRules.UnintendedNewPairs(before, after, createdId: 99, hostId: 20);
            Assert.Equal(new[] { ClashResolveRules.PairKey(99, 10), ClashResolveRules.PairKey(10, 30) }, fresh);
        }

        [Fact]
        public void A_foundation_is_a_structural_host()
        {
            // audit 2026-10-08, 6.14: a footing needs the structural opt-in like a beam or a column.
            Assert.True(SleeveRules.IsStructuralCategory("OST_StructuralFoundation"));
            Assert.True(SleeveRules.IsStructuralCategory("OST_StructuralFraming"));
            Assert.True(SleeveRules.IsStructuralCategory("OST_StructuralColumns"));
            Assert.False(SleeveRules.IsStructuralCategory("OST_Ceilings"));
        }
    }
}
