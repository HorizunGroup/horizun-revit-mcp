// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The layout arithmetic the planimetry auditor stands on. Every case here is one
// a wrong answer would turn into a finding somebody has to chase:
//
//   * two viewports whose edges MEET are a layout somebody chose. Reporting
//     that as a collision is how an auditor loses its reader, so contact -
//     exact and within tolerance - must not be an overlap;
//   * a box that could not be read defaults to (0,0)-(0,0) in every naive
//     implementation, and that rectangle sits inside every sheet and overlaps
//     nothing. It is the shape of a clean result, produced by a failed read;
//   * two rectangles apart on BOTH axes are further apart than either axis gap
//     says. A minimum-gap rule built on max(dx, dy) passes layouts it should
//     fail;
//   * a negative margin must not invert a rectangle: an inverted rectangle
//     contains nothing and overlaps nothing, which is - again - the shape of a
//     clean result.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryGeometryTests
    {
        private const double Tol = PlanimetryGeometry.TouchToleranceFeet;

        private static PlanBox Box(double x1, double y1, double x2, double y2)
        {
            return PlanBox.FromCorners(x1, y1, x2, y2);
        }

        // ---- separated, touching, and touching within tolerance ----------------

        [Fact]
        public void Separated_boxes_do_not_overlap_and_report_their_gap()
        {
            PlanBox a = Box(0, 0, 1, 1);
            PlanBox b = Box(2, 0, 3, 1);
            Assert.False(PlanimetryGeometry.Overlaps(a, b, Tol));
            Assert.Equal(0.0, PlanimetryGeometry.OverlapX(a, b));
            Assert.Equal(1.0, PlanimetryGeometry.Separation(a, b), 9);
        }

        [Fact]
        public void Exact_contact_is_not_an_overlap()
        {
            PlanBox a = Box(0, 0, 1, 1);
            PlanBox b = Box(1, 0, 2, 1);
            Assert.False(PlanimetryGeometry.Overlaps(a, b, Tol));
            Assert.Equal(0.0, PlanimetryGeometry.Separation(a, b), 9);
            // They touch, so they are NOT disjoint either - the "completely outside"
            // predicate has to be stricter than !Overlaps or a grazing placement would be
            // reported as entirely off the sheet.
            Assert.False(PlanimetryGeometry.Disjoint(a, b, Tol));
        }

        [Fact]
        public void Contact_within_the_tolerance_is_not_an_overlap()
        {
            PlanBox a = Box(0, 0, 1, 1);
            PlanBox b = Box(1 - Tol / 2, 0, 2, 1);
            Assert.True(PlanimetryGeometry.OverlapX(a, b) > 0, "they do share a sliver");
            Assert.False(PlanimetryGeometry.Overlaps(a, b, Tol));
        }

        [Fact]
        public void An_overlap_just_past_the_tolerance_is_reported()
        {
            PlanBox a = Box(0, 0, 1, 1);
            PlanBox b = Box(1 - Tol * 4, 0, 2, 1);
            Assert.True(PlanimetryGeometry.Overlaps(a, b, Tol));
            Assert.Equal(Tol * 4, PlanimetryGeometry.OverlapX(a, b), 12);
        }

        [Fact]
        public void Overlap_must_exceed_the_tolerance_on_BOTH_axes()
        {
            // A wide sliver: a whole unit of shared X, but only a hair of shared Y. Two
            // sheets stacked edge to edge look exactly like this, and they do not collide.
            PlanBox a = Box(0, 0, 10, 1);
            PlanBox b = Box(0, 1 - Tol / 2, 10, 3);
            Assert.True(PlanimetryGeometry.OverlapX(a, b) > 1);
            Assert.False(PlanimetryGeometry.Overlaps(a, b, Tol));
        }

        // ---- real overlap, containment, area -----------------------------------

        [Fact]
        public void Partial_overlap_reports_x_y_and_area()
        {
            PlanBox a = Box(0, 0, 10, 10);
            PlanBox b = Box(8, 6, 20, 20);
            Assert.True(PlanimetryGeometry.Overlaps(a, b, Tol));
            Assert.Equal(2.0, PlanimetryGeometry.OverlapX(a, b), 9);
            Assert.Equal(4.0, PlanimetryGeometry.OverlapY(a, b), 9);
            Assert.Equal(8.0, PlanimetryGeometry.OverlapArea(a, b), 9);
            Assert.Equal(0.0, PlanimetryGeometry.Separation(a, b), 9);
        }

        [Fact]
        public void Containment_is_an_overlap_and_a_containment()
        {
            PlanBox outer = Box(0, 0, 10, 10);
            PlanBox inner = Box(2, 2, 4, 4);
            Assert.True(PlanimetryGeometry.Overlaps(outer, inner, Tol));
            Assert.True(PlanimetryGeometry.Contains(outer, inner, Tol));
            Assert.False(PlanimetryGeometry.Contains(inner, outer, Tol));
        }

        [Fact]
        public void Containment_allows_the_tolerance_on_every_edge()
        {
            PlanBox outer = Box(0, 0, 10, 10);
            PlanBox flush = Box(0 - Tol / 2, 0, 10 + Tol / 2, 10);
            Assert.True(PlanimetryGeometry.Contains(outer, flush, Tol));
            PlanBox past = Box(-Tol * 4, 0, 10, 10);
            Assert.False(PlanimetryGeometry.Contains(outer, past, Tol));
        }

        // ---- separation on both axes -------------------------------------------

        [Fact]
        public void Diagonal_separation_is_the_corner_distance_not_the_larger_axis_gap()
        {
            PlanBox a = Box(0, 0, 1, 1);
            PlanBox b = Box(4, 3, 5, 4);   // dx = 3, dy = 2
            Assert.Equal(Math.Sqrt(13.0), PlanimetryGeometry.Separation(a, b), 9);
            Assert.True(PlanimetryGeometry.Separation(a, b) > 3.0,
                "a rule using max(dx, dy) would pass a layout that is genuinely further apart");
        }

        [Fact]
        public void Separation_along_one_axis_only_is_that_axis_gap()
        {
            PlanBox a = Box(0, 0, 1, 10);
            PlanBox b = Box(3, 2, 4, 8);
            Assert.Equal(2.0, PlanimetryGeometry.Separation(a, b), 9);
        }

        // ---- unreadable is contagious, never a clean answer ---------------------

        [Fact]
        public void An_unreadable_box_overlaps_nothing_and_contains_nothing_and_has_no_separation()
        {
            PlanBox real = Box(0, 0, 10, 10);
            PlanBox gone = PlanBox.Unreadable;
            Assert.False(gone.Valid);
            Assert.False(PlanimetryGeometry.Overlaps(real, gone, Tol));
            Assert.False(PlanimetryGeometry.Contains(real, gone, Tol));
            Assert.False(PlanimetryGeometry.Contains(gone, real, Tol));
            Assert.False(PlanimetryGeometry.Disjoint(real, gone, Tol));
            Assert.True(double.IsNaN(PlanimetryGeometry.Separation(real, gone)));
            Assert.Null(PlanimetryGeometry.ToDisplayArray(gone, 304.8));
            Assert.Equal("unreadable", PlanimetryGeometry.Signature(gone));
        }

        [Fact]
        public void A_non_finite_corner_produces_an_unreadable_box_not_a_NaN_rectangle()
        {
            Assert.False(PlanBox.FromCorners(0, 0, double.NaN, 1).Valid);
            Assert.False(PlanBox.FromCorners(0, double.PositiveInfinity, 1, 1).Valid);
        }

        [Fact]
        public void An_unreadable_box_is_not_an_empty_box_at_the_origin()
        {
            // The whole point: a default rectangle sits inside every sheet and collides
            // with nothing, which is exactly what a clean result looks like.
            PlanBox sheet = Box(0, 0, 100, 100);
            PlanBox empty = Box(0, 0, 0, 0);
            Assert.True(PlanimetryGeometry.Contains(sheet, empty, Tol));
            Assert.False(PlanimetryGeometry.Contains(sheet, PlanBox.Unreadable, Tol));
        }

        // ---- expand, union ------------------------------------------------------

        [Fact]
        public void Expand_grows_and_shrinks_and_never_inverts()
        {
            PlanBox a = Box(0, 0, 10, 10);
            PlanBox grown = PlanimetryGeometry.Expand(a, 1);
            Assert.Equal(-1, grown.MinX, 9);
            Assert.Equal(11, grown.MaxY, 9);

            PlanBox shrunk = PlanimetryGeometry.Expand(a, -2);
            Assert.Equal(2, shrunk.MinX, 9);
            Assert.Equal(8, shrunk.MaxX, 9);

            // Shrinking past zero must give an EMPTY box at the centre, not an inverted
            // one - an inverted rectangle contains nothing and would read as a pass.
            PlanBox collapsed = PlanimetryGeometry.Expand(a, -50);
            Assert.True(collapsed.Valid);
            Assert.Equal(5, collapsed.MinX, 9);
            Assert.Equal(5, collapsed.MaxX, 9);
            Assert.Equal(0, collapsed.Width, 9);
            Assert.False(PlanimetryGeometry.Contains(collapsed, a, Tol));
        }

        [Fact]
        public void Union_of_a_readable_and_an_unreadable_box_is_unreadable()
        {
            // Understating an extent is the dangerous direction: the label is exactly the
            // part a neighbour collides with that the view box does not contain.
            Assert.False(PlanimetryGeometry.Union(Box(0, 0, 1, 1), PlanBox.Unreadable).Valid);
        }

        [Fact]
        public void UnionOptional_treats_an_absent_second_box_as_absent_not_as_a_failed_read()
        {
            // A schedule placement has no label at all. "There is no label" is not "the
            // label could not be read", so it keeps its own box.
            PlanBox only = Box(0, 0, 1, 1);
            PlanBox result = PlanimetryGeometry.UnionOptional(only, PlanBox.Unreadable);
            Assert.True(result.Valid);
            Assert.Equal(1, result.MaxX, 9);
            Assert.False(PlanimetryGeometry.UnionOptional(PlanBox.Unreadable, only).Valid);
        }

        [Fact]
        public void Union_covers_the_label_that_sits_outside_the_view_box()
        {
            PlanBox view = Box(0, 0, 10, 10);
            PlanBox label = Box(0, -2, 6, -0.5);
            PlanBox extent = PlanimetryGeometry.UnionOptional(view, label);
            Assert.Equal(-2, extent.MinY, 9);

            // A neighbour that clears the VIEW box but not the label is a real collision,
            // and this is the case a box-only auditor misses.
            PlanBox neighbour = Box(2, -3, 8, -1);
            Assert.False(PlanimetryGeometry.Overlaps(view, neighbour, Tol));
            Assert.True(PlanimetryGeometry.Overlaps(extent, neighbour, Tol));
        }

        // ---- negative coordinates ----------------------------------------------

        [Fact]
        public void Negative_coordinates_behave_exactly_like_positive_ones()
        {
            PlanBox a = Box(-10, -10, -5, -5);
            PlanBox b = Box(-6, -6, -1, -1);
            Assert.True(PlanimetryGeometry.Overlaps(a, b, Tol));
            Assert.Equal(1.0, PlanimetryGeometry.OverlapX(a, b), 9);
            Assert.Equal(1.0, PlanimetryGeometry.OverlapArea(a, b), 9);

            PlanBox c = Box(-100, -100, -90, -90);
            Assert.True(PlanimetryGeometry.Disjoint(a, c, Tol));
        }

        [Fact]
        public void Corners_may_arrive_in_any_order()
        {
            PlanBox a = Box(10, 10, 0, 0);
            Assert.Equal(0, a.MinX, 9);
            Assert.Equal(10, a.MaxY, 9);
            Assert.Equal(5, a.CenterX, 9);
        }

        // ---- units --------------------------------------------------------------

        [Theory]
        [InlineData("mm", 304.8)]
        [InlineData("m", 0.3048)]
        [InlineData("feet", 1.0)]
        public void Known_units_convert_from_internal_feet(string units, double expected)
        {
            double scale;
            Assert.True(PlanimetryGeometry.TryScaleFromFeet(units, out scale));
            Assert.Equal(expected, scale, 9);
            Assert.Equal(expected, PlanimetryGeometry.Display(1.0, scale), 6);
        }

        [Fact]
        public void An_unknown_unit_is_refused_rather_than_defaulted()
        {
            double scale;
            Assert.False(PlanimetryGeometry.TryScaleFromFeet("inches", out scale));
            Assert.False(PlanimetryGeometry.TryScaleFromFeet(null, out scale));
        }

        [Fact]
        public void The_round_trip_through_display_units_returns_the_same_length()
        {
            double toFeet;
            Assert.True(PlanimetryGeometry.TryScaleToFeet("mm", out toFeet));
            Assert.Equal(1.0, 304.8 * toFeet, 9);
            // 5 mm expressed in feet and back is 5 mm.
            double fiveMillimetresInFeet = 5.0 * toFeet;
            Assert.Equal(5.0, PlanimetryGeometry.Display(fiveMillimetresInFeet, 304.8), 6);
        }

        [Fact]
        public void The_touch_tolerance_is_a_tenth_of_a_millimetre_on_paper()
        {
            Assert.Equal(0.1, PlanimetryGeometry.Display(PlanimetryGeometry.TouchToleranceFeet, 304.8), 6);
        }

        [Fact]
        public void Display_rounds_so_two_runs_over_an_unchanged_model_emit_the_same_bytes()
        {
            double a = PlanimetryGeometry.Display(1.0 / 3.0, 304.8);
            double b = PlanimetryGeometry.Display(1.0 / 3.0, 304.8);
            Assert.Equal(a, b);
            Assert.Equal(a.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                         b.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        // ---- signatures ---------------------------------------------------------

        [Fact]
        public void The_signature_is_stable_on_the_tenth_millimetre_grid()
        {
            PlanBox a = Box(0, 0, 1, 1);
            PlanBox jittered = Box(1e-9, 0, 1 - 1e-9, 1);
            Assert.Equal(PlanimetryGeometry.Signature(a), PlanimetryGeometry.Signature(jittered));

            PlanBox moved = Box(0, 0, 1 + 1.0 / 304.8, 1);   // a whole millimetre
            Assert.NotEqual(PlanimetryGeometry.Signature(a), PlanimetryGeometry.Signature(moved));
        }

        // ---- the two collisions the auditor names separately --------------------

        [Fact]
        public void A_label_collision_and_a_schedule_collision_are_both_ordinary_overlaps()
        {
            PlanBox viewportExtent = PlanimetryGeometry.UnionOptional(Box(0, 0, 10, 10), Box(0, -2, 6, -0.5));
            PlanBox schedule = Box(5, -1.5, 12, 4);
            Assert.True(PlanimetryGeometry.Overlaps(viewportExtent, schedule, Tol));

            PlanBox clear = Box(11, 11, 20, 20);
            Assert.False(PlanimetryGeometry.Overlaps(viewportExtent, clear, Tol));
            Assert.True(PlanimetryGeometry.Disjoint(viewportExtent, clear, Tol));
        }
    }
}
