// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Dimensions held against geometry (item 2.3): coordinates along the line,
// segment verification at 1 mm, compound-structure boundaries, chain crossings,
// crop containment and the value_override flag.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DimensionGeometryRulesTests
    {
        private const double Mm = 1.0 / 304.8;
        private static readonly double[] X = { 1, 0, 0 };

        [Fact]
        public void Sheet_vectors_cover_four_turns_and_unknown_rotation_is_refused()
        {
            var expected = new[] { new[] { 3.0, -7.0 }, new[] { 7.0, 3.0 }, new[] { -3.0, 7.0 }, new[] { -7.0, -3.0 } };
            for (int q = 0; q < 4; q++)
            {
                var view = NoteLeaderRules.SheetToView(3, -7, q);
                Assert.Equal(expected[q], view);
                Assert.Equal(new[] { 3.0, -7.0 }, NoteLeaderRules.ViewToSheet(view[0], view[1], q));
            }
            Assert.Throws<System.ArgumentException>(() => NoteLeaderRules.QuarterTurns("unrecognized"));
        }

        private static RefGeometry FaceAtX(double xMm, double sign = 1)
            => RefGeometry.Plane(new[] { xMm * Mm, 5.0, 0.0 }, new[] { sign, 0.0, 0.0 });

        [Fact]
        public void Plane_facing_along_the_line_gives_its_offset_whatever_its_sign()
        {
            string why;
            Assert.Equal(200 * Mm, DimensionGeometryRules.Coordinate(FaceAtX(200, -1), X, out why).Value, 9);
            Assert.Null(why);
        }

        [Fact]
        public void Plane_not_facing_the_line_is_unavailable_not_guessed()
        {
            string why;
            var side = RefGeometry.Plane(new[] { 0.0, 0, 0 }, new[] { 0.0, 1, 0 });
            Assert.Null(DimensionGeometryRules.Coordinate(side, X, out why));
            Assert.Equal(DimensionGeometryRules.CodeNotPerpendicular, why);
        }

        [Fact]
        public void Grid_line_perpendicular_to_the_dimension_line_is_measurable()
        {
            string why;
            var grid = RefGeometry.Line(new[] { 3000 * Mm, -10.0, 0 }, new[] { 0.0, 1, 0 });
            Assert.Equal(3000 * Mm, DimensionGeometryRules.Coordinate(grid, X, out why).Value, 9);
            var oblique = RefGeometry.Line(new[] { 0.0, 0, 0 }, new[] { 1.0, 1, 0 });
            Assert.Null(DimensionGeometryRules.Coordinate(oblique, X, out why));
        }

        [Fact]
        public void Level_plane_is_measured_on_a_vertical_line()
        {
            string why;
            var level = RefGeometry.Plane(new[] { 0.0, 0, 3000 * Mm }, new[] { 0.0, 0, 1 });
            Assert.Equal(-3000 * Mm, DimensionGeometryRules.Coordinate(level, new[] { 0.0, 0, -2 }, out why).Value, 9);
        }

        [Fact]
        public void Two_reference_dimension_within_1mm_verifies()
        {
            var v = DimensionGeometryRules.Verify(new[] { FaceAtX(0), FaceAtX(250) }, X, 250.6 * Mm, null,
                                                  DimensionGeometryRules.DefaultToleranceFeet);
            Assert.Equal(DimensionGeometryRules.StatusVerified, v.Status);
            Assert.Equal(0.6 * Mm, v.Segments[0].DeviationFeet.Value, 9);
        }

        [Fact]
        public void Value_off_by_more_than_1mm_fails()
        {
            var v = DimensionGeometryRules.Verify(new[] { FaceAtX(0), FaceAtX(250) }, X, 251.2 * Mm, null,
                                                  DimensionGeometryRules.DefaultToleranceFeet);
            Assert.True(v.Failed);
            Assert.Equal("value_differs_from_geometry", v.Segments[0].Reason);
        }

        [Fact]
        public void Chain_segments_are_compared_one_by_one()
        {
            var refs = new[] { FaceAtX(0), FaceAtX(100), FaceAtX(400) };
            var ok = DimensionGeometryRules.Verify(refs, X, null, new List<double> { 100 * Mm, 300 * Mm }, Mm);
            Assert.Equal(DimensionGeometryRules.StatusVerified, ok.Status);
            var bad = DimensionGeometryRules.Verify(refs, X, null, new List<double> { 100 * Mm, 302 * Mm }, Mm);
            Assert.True(bad.Failed);
            Assert.Equal(DimensionGeometryRules.StatusVerified, bad.Segments[0].Status);
            Assert.Equal(DimensionGeometryRules.StatusFailed, bad.Segments[1].Status);
        }

        [Fact]
        public void Stored_order_differing_from_line_order_is_rechecked_sorted()
        {
            var refs = new[] { FaceAtX(100), FaceAtX(0), FaceAtX(400) };
            var segs = new List<double> { 100 * Mm, 300 * Mm };
            Assert.True(DimensionGeometryRules.Verify(refs, X, null, segs, Mm).Failed);
            var v = DimensionGeometryRules.VerifyAnyOrder(refs, X, null, segs, Mm);
            Assert.Equal(DimensionGeometryRules.StatusVerified, v.Status);
            Assert.True(v.Reordered);
            Assert.True(DimensionGeometryRules.VerifyAnyOrder(refs, X, null, new List<double> { 100 * Mm, 305 * Mm }, Mm).Failed);
        }

        [Fact]
        public void Unmeasurable_reference_is_partial_or_unavailable_never_verified()
        {
            var family = RefGeometry.None(DimensionGeometryRules.CodeFamilyReference);
            var partial = DimensionGeometryRules.Verify(new[] { FaceAtX(0), FaceAtX(100), family }, X, null,
                new List<double> { 100 * Mm, 999 * Mm }, Mm);
            Assert.Equal(DimensionGeometryRules.StatusPartial, partial.Status);
            Assert.Equal(DimensionGeometryRules.CodeFamilyReference, partial.Segments[1].Reason);
            var none = DimensionGeometryRules.Verify(new[] { family, FaceAtX(100) }, X, 100 * Mm, null, Mm);
            Assert.Equal(DimensionGeometryRules.StatusUnavailable, none.Status);
            Assert.False(none.Failed);
        }

        [Fact]
        public void Missing_segment_value_fails_rather_than_passing()
        {
            var v = DimensionGeometryRules.Verify(new[] { FaceAtX(0), FaceAtX(100), FaceAtX(200) }, X, null,
                new List<double> { 100 * Mm }, Mm);
            Assert.True(v.Failed);
        }

        [Fact]
        public void Expected_total_is_first_to_last_and_refuses_gaps_and_reversals()
        {
            string why;
            Assert.Equal(400 * Mm, DimensionGeometryRules.ExpectedTotal(new[] { FaceAtX(0), FaceAtX(100), FaceAtX(400) }, X, out why).Value, 9);
            Assert.Null(DimensionGeometryRules.ExpectedTotal(new[] { FaceAtX(0), RefGeometry.None("x") }, X, out why));
            Assert.Equal("x", why);
            Assert.Null(DimensionGeometryRules.ExpectedTotal(new[] { FaceAtX(0), FaceAtX(300), FaceAtX(100) }, X, out why));
            Assert.Equal("references_not_monotonic_along_line", why);
        }

        [Fact]
        public void Core_boundaries_follow_the_layer_widths()
        {
            // finish 15 | core 200 | finish 12 (exterior to interior)
            var widths = new[] { 15 * Mm, 200 * Mm, 12 * Mm };
            string code;
            Assert.Equal(15 * Mm, DimensionGeometryRules.CoreBoundaryOffset(widths, 1, 1, false, out code).Value, 9);
            Assert.Equal(215 * Mm, DimensionGeometryRules.CoreBoundaryOffset(widths, 1, 1, true, out code).Value, 9);
            Assert.Null(DimensionGeometryRules.CoreBoundaryOffset(widths, -1, -1, true, out code));
            Assert.Equal(DimensionGeometryRules.CodeNoCoreLayers, code);
            Assert.Null(DimensionGeometryRules.CoreBoundaryOffset(new double[0], 0, 0, true, out code));
            Assert.Equal(DimensionGeometryRules.CodeNoCompoundStructure, code);
            var b = DimensionGeometryRules.LayerBoundaries(widths);
            Assert.Equal(4, b.Count);
            Assert.Equal(227 * Mm, b[3], 9);
        }

        [Fact]
        public void Faces_are_classified_by_inward_offset_from_the_exterior_face()
        {
            double[] ext = { 0, 0, 0 }, n = { 0, -1, 0 };            // exterior faces -Y
            double? off = DimensionGeometryRules.InwardOffset(ext, n, new[] { 4.0, 15 * Mm, 1 }, new[] { 0.0, 1, 0 });
            Assert.Equal(15 * Mm, off.Value, 9);
            var boundaries = DimensionGeometryRules.LayerBoundaries(new[] { 15 * Mm, 200 * Mm, 12 * Mm });
            Assert.Equal(1, DimensionGeometryRules.MatchBoundary(off.Value + 0.05 * Mm, boundaries, 0.1 * Mm));
            Assert.Equal(-1, DimensionGeometryRules.MatchBoundary(100 * Mm, boundaries, 0.1 * Mm));
            Assert.Null(DimensionGeometryRules.InwardOffset(ext, n, new[] { 0.0, 0, 0 }, new[] { 1.0, 0, 0 }));
        }

        [Fact]
        public void Diagonal_line_crosses_planes_facing_along_it()
        {
            double s = System.Math.Sqrt(0.5);
            double[] a = { 0, 0, 0 }, b = { 10, 10, 0 };
            double? t = DimensionGeometryRules.PlaneCrossing(a, b, new[] { 5.0, 5, 0 }, new[] { s, s, 0 });
            Assert.Equal(0.5, t.Value, 9);
            Assert.Null(DimensionGeometryRules.PlaneCrossing(a, b, new[] { 5.0, 5, 0 }, new[] { 1.0, 0, 0 }));
            Assert.Null(DimensionGeometryRules.PlaneCrossing(a, b, new[] { 20.0, 20, 0 }, new[] { s, s, 0 }));
        }

        [Fact]
        public void Chain_hits_are_ordered_and_coincident_planes_collapse_deterministically()
        {
            var hits = new[]
            {
                new ChainHit { Stable = "b", ElementId = 9, Coordinate = 2.0 },
                new ChainHit { Stable = "a", ElementId = 3, Coordinate = 2.0 + 0.05 * Mm },
                new ChainHit { Stable = "c", ElementId = 1, Coordinate = 1.0 }
            };
            List<ChainHit> collapsed;
            var chain = DimensionGeometryRules.OrderChain(hits, 0.1 * Mm, out collapsed);
            Assert.Equal(new[] { "c", "a" }, chain.ConvertAll(h => h.Stable).ToArray());
            Assert.Single(collapsed);
            Assert.Equal("b", collapsed[0].Stable);
        }

        [Fact]
        public void Crop_containment_has_tolerance()
        {
            Assert.True(DimensionGeometryRules.InsideCrop(10, 0, 0, 0, 10, 5, 1e-6));
            Assert.False(DimensionGeometryRules.InsideCrop(10.01, 0, 0, 0, 10, 5, 1e-6));
        }

        [Fact]
        public void Modes_delegate_to_existing_operations_where_one_exists()
        {
            Assert.Equal("auto_dimension_openings", DimensionModeRules.DelegateOperation("opening_width"));
            Assert.Equal("auto_dimension_room", DimensionModeRules.DelegateOperation("room_clear_width"));
            Assert.Equal("auto_dimension_grids", DimensionModeRules.DelegateOperation("grid_to_grid"));
            Assert.Equal("auto_dimension_levels", DimensionModeRules.DelegateOperation("level_heights"));
            Assert.Null(DimensionModeRules.DelegateOperation("wall_thickness"));
            Assert.Null(DimensionModeRules.DelegateOperation("chain_along_line"));
        }

        [Fact]
        public void Mode_requirements_are_refused_not_guessed()
        {
            Assert.NotNull(DimensionModeRules.Validate("diagonal", false, 0, false, false));
            Assert.NotNull(DimensionModeRules.Validate("chain_along_line", true, 3, false, false));
            Assert.NotNull(DimensionModeRules.Validate("chain_along_line", false, 0, true, false));
            Assert.Null(DimensionModeRules.Validate("chain_along_line", true, 3, true, false));
            Assert.NotNull(DimensionModeRules.Validate("wall_thickness", false, 0, false, false));
            Assert.NotNull(DimensionModeRules.Validate("room_clear_width", false, 0, false, false));
            Assert.Null(DimensionModeRules.Validate("grid_to_grid", false, 0, false, false));
        }

        [Fact]
        public void Value_override_needs_the_explicit_flag()
        {
            Assert.NotNull(DimensionGeometryRules.ValueOverrideError(true, false));
            Assert.Null(DimensionGeometryRules.ValueOverrideError(true, true));
            Assert.Null(DimensionGeometryRules.ValueOverrideError(false, false));
        }
    }
}
