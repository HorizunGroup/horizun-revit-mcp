// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// The DWG geometry layer, pinned. These tests are the reason any of the CAD
// reasoning can be believed: the algorithms decide what a drawing MEANS, and a
// meaning that changes between runs is not a meaning.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadUnitsTests
    {
        [Theory]
        [InlineData("Millimeter", 1.0)]
        [InlineData("centimetre", 10.0)]
        [InlineData("METER", 1000.0)]
        [InlineData("Inch", 25.4)]
        [InlineData("Foot", 304.8)]
        [InlineData("Decimeter", 100.0)]
        public void A_declared_unit_resolves_to_millimetres(string unit, double expected)
        {
            Assert.Equal(expected, CadUnits.MillimetresPer(unit).Value, 9);
        }

        [Theory]
        [InlineData("Default")]
        [InlineData("Custom")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("cubits")]
        public void An_undeclared_unit_is_null_and_never_a_plausible_guess(string unit)
        {
            // The whole point: a drawing whose walls are "0.2" apart is either
            // metres or a mistake, and this refuses to decide which.
            Assert.Null(CadUnits.MillimetresPer(unit));
        }

        [Fact]
        public void The_us_survey_foot_is_not_the_international_foot()
        {
            double us = CadUnits.MillimetresPer("USSurveyFoot").Value;
            Assert.NotEqual(CadUnits.MillimetresPerFoot, us);
            Assert.Equal(304.8006096, us, 6);
        }
    }

    public class CadPointTests
    {
        [Fact]
        public void Quantizing_snaps_near_coincident_points_onto_one_node()
        {
            var a = new CadPoint(1000.0, 500.0);
            var b = new CadPoint(1000.4, 499.7);
            Assert.NotEqual(a.Key(1.0), b.Key(0.1));
            Assert.Equal(a.Key(1.0), b.Key(1.0));
        }

        [Fact]
        public void Quantizing_never_produces_negative_zero()
        {
            var p = new CadPoint(-0.2, -0.1, -0.4);
            CadPoint q = p.Quantize(1.0);
            Assert.Equal(0.0, q.X);
            Assert.False(double.IsNegative(q.X), "a negative zero would key differently from a positive zero");
            Assert.Equal(q.Key(1.0), new CadPoint(0, 0, 0).Key(1.0));
        }

        [Fact]
        public void Quantizing_is_symmetric_about_the_origin()
        {
            // Away-from-zero rounding: a drawing must not creep toward 0,0.
            Assert.Equal(1.0, new CadPoint(0.5, 0).Quantize(1.0).X);
            Assert.Equal(-1.0, new CadPoint(-0.5, 0).Quantize(1.0).X);
        }

        [Fact]
        public void Large_coordinates_still_quantize_exactly()
        {
            // A site drawn on state-plane coordinates is the normal hostile case.
            var p = new CadPoint(1234567.891, -9876543.219);
            CadPoint q = p.Quantize(1.0);
            Assert.Equal(1234568.0, q.X, 6);
            Assert.Equal(-9876543.0, q.Y, 6);
        }
    }

    public class CadIdentityTests
    {
        private static readonly List<CadPoint> Line =
            new List<CadPoint> { new CadPoint(0, 0), new CadPoint(3000, 0) };

        [Fact]
        public void The_same_geometry_gets_the_same_surrogate()
        {
            string a = CadIdentity.Surrogate("srchash", "A-WALL", "root", CadCurveKind.Line, Line, 1.0);
            string b = CadIdentity.Surrogate("srchash", "A-WALL", "root", CadCurveKind.Line,
                new List<CadPoint> { new CadPoint(0, 0), new CadPoint(3000, 0) }, 1.0);
            Assert.Equal(a, b);
        }

        [Fact]
        public void A_sub_tolerance_move_does_not_change_identity()
        {
            string a = CadIdentity.Surrogate("h", "A-WALL", "root", CadCurveKind.Line, Line, 1.0);
            string b = CadIdentity.Surrogate("h", "A-WALL", "root", CadCurveKind.Line,
                new List<CadPoint> { new CadPoint(0.2, -0.3), new CadPoint(3000.4, 0.1) }, 1.0);
            Assert.Equal(a, b);
        }

        [Fact]
        public void A_real_move_does_change_identity()
        {
            string a = CadIdentity.Surrogate("h", "A-WALL", "root", CadCurveKind.Line, Line, 1.0);
            string b = CadIdentity.Surrogate("h", "A-WALL", "root", CadCurveKind.Line,
                new List<CadPoint> { new CadPoint(0, 0), new CadPoint(3050, 0) }, 1.0);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void The_same_line_on_a_different_layer_is_a_different_thing()
        {
            string wall = CadIdentity.Surrogate("h", "A-WALL", "root", CadCurveKind.Line, Line, 1.0);
            string furniture = CadIdentity.Surrogate("h", "A-FURN", "root", CadCurveKind.Line, Line, 1.0);
            Assert.NotEqual(wall, furniture);
        }

        [Fact]
        public void A_new_issue_of_the_drawing_is_a_different_source()
        {
            string first = CadIdentity.Surrogate("hash-rev-a", "A-WALL", "root", CadCurveKind.Line, Line, 1.0);
            string second = CadIdentity.Surrogate("hash-rev-b", "A-WALL", "root", CadCurveKind.Line, Line, 1.0);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Drawing_direction_does_not_change_an_undirected_surrogate()
        {
            var forward = new List<CadPoint> { new CadPoint(0, 0), new CadPoint(3000, 0) };
            var backward = new List<CadPoint> { new CadPoint(3000, 0), new CadPoint(0, 0) };
            Assert.Equal(
                CadIdentity.SurrogateUndirected("h", "A-WALL", "root", CadCurveKind.Line, forward, 1.0),
                CadIdentity.SurrogateUndirected("h", "A-WALL", "root", CadCurveKind.Line, backward, 1.0));
            // ...while the DIRECTED one still tells them apart, for anything that cares.
            Assert.NotEqual(
                CadIdentity.Surrogate("h", "A-WALL", "root", CadCurveKind.Line, forward, 1.0),
                CadIdentity.Surrogate("h", "A-WALL", "root", CadCurveKind.Line, backward, 1.0));
        }

        [Fact]
        public void Nesting_is_part_of_identity()
        {
            Assert.NotEqual(
                CadIdentity.Surrogate("h", "A-WALL", "root", CadCurveKind.Line, Line, 1.0),
                CadIdentity.Surrogate("h", "A-WALL", "root/BLOCK-A#2", CadCurveKind.Line, Line, 1.0));
        }

        [Fact]
        public void The_set_fingerprint_ignores_enumeration_order()
        {
            var one = new[] { "cad:a", "cad:b", "cad:c" };
            var other = new[] { "cad:c", "cad:a", "cad:b" };
            Assert.Equal(CadIdentity.SetFingerprint(one), CadIdentity.SetFingerprint(other));
            Assert.NotEqual(CadIdentity.SetFingerprint(one), CadIdentity.SetFingerprint(new[] { "cad:a", "cad:b" }));
        }

        [Fact]
        public void The_set_fingerprint_states_how_many_it_covered()
        {
            Assert.EndsWith(":3", CadIdentity.SetFingerprint(new[] { "cad:a", "cad:b", "cad:c" }));
        }

        [Fact]
        public void Two_different_inputs_cannot_concatenate_into_one_string()
        {
            // Without a separator that cannot occur in the parts, layer "A" with
            // source "BC" and layer "AB" with source "C" would hash the same.
            Assert.NotEqual(
                CadIdentity.Surrogate("BC", "A", "root", CadCurveKind.Line, Line, 1.0),
                CadIdentity.Surrogate("C", "AB", "root", CadCurveKind.Line, Line, 1.0));
        }
    }

    public class CadCurveTests
    {
        [Fact]
        public void An_arc_is_chorded_to_a_declared_sagitta_not_a_fixed_count()
        {
            var centre = new CadPoint(0, 0);
            List<CadPoint> coarse = CadCurves.ChordArc(centre, 1000, 0, Math.PI, 50);
            List<CadPoint> fine = CadCurves.ChordArc(centre, 1000, 0, Math.PI, 1);
            Assert.True(fine.Count > coarse.Count,
                "a tighter sagitta must produce more chords, or the tolerance means nothing");
        }

        [Fact]
        public void Every_chord_respects_the_sagitta_it_was_given()
        {
            var centre = new CadPoint(0, 0);
            double radius = 2000, sagitta = 5;
            List<CadPoint> pts = CadCurves.ChordArc(centre, radius, 0, Math.PI * 1.5, sagitta);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var mid = new CadPoint((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
                double error = radius - mid.PlanDistanceTo(centre);
                Assert.True(error <= sagitta + 1e-6, $"chord {i} departs by {error:0.###} mm, over the declared {sagitta}");
            }
        }

        [Fact]
        public void A_full_circle_closes_on_itself()
        {
            List<CadPoint> pts = CadCurves.ChordArc(new CadPoint(100, 100), 500, 0, Math.PI * 2, 2);
            Assert.True(pts[0].PlanDistanceTo(pts[pts.Count - 1]) < 1e-6);
        }

        [Fact]
        public void A_degenerate_arc_still_yields_a_straight_chord_rather_than_nothing()
        {
            List<CadPoint> pts = CadCurves.ChordArc(new CadPoint(0, 0), 0, 0, 0, 1);
            Assert.Equal(2, pts.Count);
        }

        [Fact]
        public void Zero_length_segments_are_dropped_and_counted()
        {
            var segs = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(1000, 0)),
                new CadSegment(new CadPoint(5, 5), new CadPoint(5, 5)),
                new CadSegment(new CadPoint(5, 5), new CadPoint(5.05, 5)),
            };
            List<CadSegment> kept = CadCurves.DropDegenerate(segs, 0.5, out int dropped);
            Assert.Single(kept);
            Assert.Equal(2, dropped);
        }

        [Fact]
        public void A_line_drawn_twice_is_one_line_and_the_duplicate_is_reported()
        {
            var segs = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(3000, 0), "A-WALL"),
                new CadSegment(new CadPoint(3000, 0), new CadPoint(0, 0), "A-WALL"),   // same, drawn backwards
                new CadSegment(new CadPoint(0, 0), new CadPoint(3000, 0), "A-GRID"),   // same, other layer
            };
            List<CadSegment> kept = CadCurves.Deduplicate(segs, 1.0, out List<List<CadSegment>> dupes);
            Assert.Equal(2, kept.Count);
            Assert.Single(dupes);
            Assert.Equal(2, dupes[0].Count);
        }
    }

    public class CadTopologyTests
    {
        private static CadSegment Seg(double x1, double y1, double x2, double y2, string layer = "A-WALL") =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer);

        [Fact]
        public void Endpoints_within_tolerance_become_one_node()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 1000, 0), Seg(1000.4, 0, 2000, 0) };
            Assert.Equal(4, CadTopologyRules.BuildNodes(segs, 0.1).Count);
            Assert.Equal(3, CadTopologyRules.BuildNodes(segs, 1.0).Count);
        }

        [Fact]
        public void A_polyline_drawn_in_pieces_merges_back_into_one_run()
        {
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 1000, 0), Seg(1000, 0, 2000, 0), Seg(2000, 0, 3000, 0)
            };
            List<CadSegment> merged = CadTopologyRules.MergeCollinear(segs, 1.0, 1.0, out int mergedAway);
            Assert.Single(merged);
            Assert.Equal(2, mergedAway);
            Assert.Equal(3000, merged[0].PlanLength, 6);
        }

        [Fact]
        public void A_corner_is_not_merged_away()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 3000, 0), Seg(3000, 0, 3000, 2000) };
            List<CadSegment> merged = CadTopologyRules.MergeCollinear(segs, 1.0, 1.0, out int mergedAway);
            Assert.Equal(2, merged.Count);
            Assert.Equal(0, mergedAway);
        }

        [Fact]
        public void Collinear_runs_on_different_layers_stay_apart()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 1000, 0, "A-WALL"), Seg(1000, 0, 2000, 0, "A-GRID") };
            List<CadSegment> merged = CadTopologyRules.MergeCollinear(segs, 1.0, 1.0, out int mergedAway);
            Assert.Equal(2, merged.Count);
            Assert.Equal(0, mergedAway);
        }

        [Fact]
        public void A_closed_rectangle_reads_as_a_loop_with_its_true_area()
        {
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 4000, 0), Seg(4000, 0, 4000, 3000),
                Seg(4000, 3000, 0, 3000), Seg(0, 3000, 0, 0)
            };
            List<CadLoop> loops = CadTopologyRules.FindLoops(segs, 1.0, out List<IList<CadPoint>> open);
            Assert.Single(loops);
            Assert.Empty(open);
            Assert.Equal(12_000_000, loops[0].Area, 3);
            Assert.Equal(4, loops[0].Points.Count);
        }

        [Fact]
        public void A_rectangle_with_a_small_gap_still_closes_and_says_how_big_the_gap_was()
        {
            // The single most common thing in a real CAD plan.
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 4000, 0), Seg(4000, 0, 4000, 3000),
                Seg(4000, 3000, 0, 3000), Seg(0, 3000, 0, 3)   // 3 mm short
            };
            List<CadLoop> loops = CadTopologyRules.FindLoops(segs, 5.0, out List<IList<CadPoint>> open);
            Assert.Single(loops);
            Assert.True(loops[0].LargestClosedGapMm > 0, "the loop must admit it was not actually closed");
            Assert.True(loops[0].LargestClosedGapMm <= 5.0);
        }

        [Fact]
        public void A_gap_larger_than_the_tolerance_is_NOT_closed_silently()
        {
            var segs = new List<CadSegment>
            {
                Seg(0, 0, 4000, 0), Seg(4000, 0, 4000, 3000),
                Seg(4000, 3000, 0, 3000), Seg(0, 3000, 0, 400)   // 400 mm short: a doorway, not a gap
            };
            List<CadLoop> loops = CadTopologyRules.FindLoops(segs, 5.0, out List<IList<CadPoint>> open);
            Assert.Empty(loops);
            Assert.Single(open);
        }

        [Fact]
        public void Winding_is_reported_and_can_be_normalised()
        {
            var clockwise = new List<CadPoint>
            {
                new CadPoint(0, 0), new CadPoint(0, 3000), new CadPoint(4000, 3000), new CadPoint(4000, 0)
            };
            var loop = new CadLoop(clockwise, "A-ROOM", 0, null);
            Assert.False(loop.IsCounterClockwise);
            Assert.True(loop.AsCounterClockwise().IsCounterClockwise);
            Assert.Equal(loop.Area, loop.AsCounterClockwise().Area, 6);
        }

        [Fact]
        public void Containment_is_answered_in_plan()
        {
            var rect = new List<CadPoint>
            {
                new CadPoint(0, 0), new CadPoint(4000, 0), new CadPoint(4000, 3000), new CadPoint(0, 3000)
            };
            Assert.True(CadTopologyRules.ContainsPoint(rect, new CadPoint(2000, 1500)));
            Assert.False(CadTopologyRules.ContainsPoint(rect, new CadPoint(5000, 1500)));
            Assert.False(CadTopologyRules.ContainsPoint(rect, new CadPoint(2000, -10)));
        }

        [Fact]
        public void Two_parallel_lines_read_as_a_wall_with_its_measured_thickness()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0), Seg(0, 200, 6000, 200) };
            List<CadDoubleLine> walls = CadTopologyRules.FindDoubleLines(segs, 50, 600, 2.0, 300, 0.5);
            Assert.Single(walls);
            Assert.Equal(200, walls[0].ThicknessMm, 6);
            Assert.Equal(100, walls[0].Start.Y, 6);      // the centreline sits between the faces
            Assert.Equal(6000, walls[0].LengthMm, 6);
            Assert.Equal(1.0, walls[0].OverlapFraction, 6);
        }

        [Fact]
        public void A_pair_too_far_apart_to_be_a_wall_is_not_a_wall()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0), Seg(0, 2500, 6000, 2500) };
            Assert.Empty(CadTopologyRules.FindDoubleLines(segs, 50, 600, 2.0, 300, 0.5));
        }

        [Fact]
        public void A_short_stub_beside_a_long_wall_does_not_pair_with_it()
        {
            // The overlap FRACTION is what stops this, and it is why the fraction exists.
            var segs = new List<CadSegment> { Seg(0, 0, 12000, 0), Seg(0, 200, 300, 200) };
            Assert.Empty(CadTopologyRules.FindDoubleLines(segs, 50, 600, 2.0, 500, 0.5));
        }

        [Fact]
        public void Lines_that_are_parallel_but_do_not_overlap_are_not_a_wall()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 3000, 0), Seg(9000, 200, 12000, 200) };
            Assert.Empty(CadTopologyRules.FindDoubleLines(segs, 50, 600, 2.0, 300, 0.5));
        }

        [Fact]
        public void A_nearly_parallel_pair_is_admitted_and_its_deviation_is_reported()
        {
            // 6000 long, 20 mm of drift: about 0.19 degrees. A real drawing.
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0), Seg(0, 200, 6000, 220) };
            List<CadDoubleLine> walls = CadTopologyRules.FindDoubleLines(segs, 50, 600, 2.0, 300, 0.5);
            Assert.Single(walls);
            Assert.True(walls[0].AngleDeviationDegrees > 0, "the deviation must be measured, not rounded away");
            Assert.True(walls[0].AngleDeviationDegrees < 2.0);
        }

        [Fact]
        public void Wall_faces_on_different_layers_pair_only_when_the_caller_allows_it()
        {
            var segs = new List<CadSegment> { Seg(0, 0, 6000, 0, "A-WALL-1"), Seg(0, 200, 6000, 200, "A-WALL-2") };
            Assert.Empty(CadTopologyRules.FindDoubleLines(segs, 50, 600, 2.0, 300, 0.5, sameLayerOnly: true));
            Assert.Single(CadTopologyRules.FindDoubleLines(segs, 50, 600, 2.0, 300, 0.5, sameLayerOnly: false));
        }

        [Fact]
        public void A_crossing_is_found_and_a_parallel_pair_is_not()
        {
            Assert.True(CadTopologyRules.Intersect(Seg(0, 0, 4000, 0), Seg(2000, -1000, 2000, 1000), 1.0, out CadPoint at));
            Assert.Equal(2000, at.X, 6);
            Assert.Equal(0, at.Y, 6);
            Assert.False(CadTopologyRules.Intersect(Seg(0, 0, 4000, 0), Seg(0, 500, 4000, 500), 1.0, out _));
        }

        [Fact]
        public void A_T_junction_counts_as_an_intersection()
        {
            // A partition meeting a facade touches at an endpoint. Losing that
            // loses the junction, and the junction is the point.
            Assert.True(CadTopologyRules.Intersect(Seg(0, 0, 4000, 0), Seg(2000, 0, 2000, 3000), 1.0, out _));
        }

        [Fact]
        public void Segments_that_would_cross_if_extended_do_not_count_as_crossing()
        {
            Assert.False(CadTopologyRules.Intersect(Seg(0, 0, 1000, 0), Seg(3000, -500, 3000, 500), 1.0, out _));
        }

        [Fact]
        public void A_chain_of_near_points_becomes_one_cluster()
        {
            var pts = new List<CadPoint>
            {
                new CadPoint(0, 0), new CadPoint(40, 0), new CadPoint(80, 0),   // chained within 50
                new CadPoint(5000, 0)
            };
            List<List<int>> clusters = CadTopologyRules.ClusterPoints(pts, 50);
            Assert.Equal(2, clusters.Count);
            Assert.Equal(3, clusters.First(c => c.Count > 1).Count);
        }

        [Fact]
        public void The_bounding_box_of_nothing_is_null_not_a_box_at_the_origin()
        {
            Assert.Null(CadTopologyRules.BoundingBox(new List<CadPoint>()));
        }
    }
}
