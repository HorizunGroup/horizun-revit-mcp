// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The Revit-free rules behind horizun_detail_2d and horizun_query_detail_2d.
// Each block pins a way the commands could quietly lie:
//
//   * a signature that changes when Revit hands a curve back reversed, a loop
//     rotated, or the boundary traversed the other way would refuse CORRECT
//     work at verification time - the same drawn figure must be one string;
//   * a signature that does NOT change when the geometry really moved would
//     approve drift - identity lives on the 0.1 mm grid, and a real move
//     crosses it;
//   * two different figures inside the same bounding box must not share an
//     identity - the bbox is a summary, not the shape;
//   * a bowtie boundary, a hole outside its region, two overlapping holes: all
//     of these Revit either rejects with its least helpful sentence or accepts
//     as garbage. The refusal must happen here, BEFORE any transaction, with
//     the exact points and loop indices in the message;
//   * validators must fail closed on malformed input - NaN, a missing
//     coordinate, a forged separator inside a caller-supplied signature - and
//     never hash or approve something that was never measured.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class Detail2DRulesTests
    {
        private static double[] P(double x, double y, double z = 0) => new[] { x, y, z };

        /// <summary>The direction-free edge signatures of a closed vertex ring.</summary>
        private static string[] EdgeSigs(double[][] pts)
        {
            var sigs = new string[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                sigs[i] = Detail2DRules.CanonicalLineSignature(pts[i], pts[(i + 1) % pts.Length]);
            return sigs;
        }

        private static double[][] Rotate(double[][] pts, int start)
        {
            var r = new double[pts.Length][];
            for (int i = 0; i < pts.Length; i++) r[i] = pts[(start + i) % pts.Length];
            return r;
        }

        private static double[][] Reverse(double[][] pts)
        {
            var r = (double[][])pts.Clone();
            Array.Reverse(r);
            return r;
        }

        private static readonly double[][] Square10 =
            { new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 0 }, new[] { 10.0, 10, 0 }, new[] { 0.0, 10, 0 } };

        private static readonly double[][] HoleInside =
            { new[] { 2.0, 2, 0 }, new[] { 4.0, 2, 0 }, new[] { 4.0, 4, 0 }, new[] { 2.0, 4, 0 } };

        // ---- line signatures -------------------------------------------------

        [Fact]
        public void A_line_and_its_reverse_share_one_signature()
        {
            string ab = Detail2DRules.CanonicalLineSignature(P(1, 2), P(3, 4));
            string ba = Detail2DRules.CanonicalLineSignature(P(3, 4), P(1, 2));
            Assert.NotNull(ab);
            Assert.Equal(ab, ba);

            // The ordering is lexicographic over (x, y, z), so a pair differing
            // only in y must still canonicalise.
            string cd = Detail2DRules.CanonicalLineSignature(P(0, 5), P(0, 1));
            string dc = Detail2DRules.CanonicalLineSignature(P(0, 1), P(0, 5));
            Assert.Equal(cd, dc);
        }

        [Fact]
        public void Line_signature_survives_sub_quantum_jitter_and_moves_with_a_real_move()
        {
            string baseline = Detail2DRules.CanonicalLineSignature(P(1, 2), P(3, 4));
            // 1e-7 ft and 1e-5 ft are both far below the 0.1 mm grid step
            // (~3.28e-4 ft): regeneration jitter keeps the identity.
            Assert.Equal(baseline, Detail2DRules.CanonicalLineSignature(P(1 + 1e-7, 2, 0), P(3, 4 - 1e-7, 0)));
            Assert.Equal(baseline, Detail2DRules.CanonicalLineSignature(P(1 + 1e-5, 2, 0), P(3, 4, 0)));
            // 0.001 ft = 0.3048 mm crosses the grid: a real move changes it.
            Assert.NotEqual(baseline, Detail2DRules.CanonicalLineSignature(P(1.001, 2, 0), P(3, 4, 0)));
        }

        [Fact]
        public void Negative_zero_never_reaches_a_signature()
        {
            string sig = Detail2DRules.CanonicalLineSignature(P(-1e-9, -1e-12), P(1, 1));
            Assert.NotNull(sig);
            Assert.DoesNotContain("-0.0", sig);
            Assert.Equal(Detail2DRules.CanonicalLineSignature(P(0, 0), P(1, 1)), sig);
        }

        [Fact]
        public void Malformed_points_refuse_a_line_signature()
        {
            Assert.Null(Detail2DRules.CanonicalLineSignature(null, P(1, 1)));
            Assert.Null(Detail2DRules.CanonicalLineSignature(P(0, 0), new[] { 1.0, 2.0 }));
            Assert.Null(Detail2DRules.CanonicalLineSignature(P(double.NaN, 0), P(1, 1)));
            Assert.Null(Detail2DRules.CanonicalLineSignature(P(0, 0), P(double.PositiveInfinity, 1)));
            Assert.Null(Detail2DRules.CanonicalLineSignature(P(2e9, 0), P(1, 1)));
        }

        // ---- arc signatures --------------------------------------------------

        [Fact]
        public void An_arc_with_swapped_endpoints_shares_one_signature()
        {
            string se = Detail2DRules.CanonicalArcSignature(P(0, 0), 5.0, P(5, 0), P(0, 5));
            string es = Detail2DRules.CanonicalArcSignature(P(0, 0), 5.0, P(0, 5), P(5, 0));
            Assert.NotNull(se);
            Assert.Equal(se, es);
        }

        [Fact]
        public void Arc_signature_quantizes_radius_on_the_same_grid_as_coordinates()
        {
            string baseline = Detail2DRules.CanonicalArcSignature(P(0, 0), 1.0, P(1, 0), P(0, 1));
            Assert.Equal(baseline, Detail2DRules.CanonicalArcSignature(P(0, 0), 1.0 + 1e-7, P(1, 0), P(0, 1)));
            Assert.Equal(baseline, Detail2DRules.CanonicalArcSignature(P(0, 0), 1.0 + 1e-5, P(1, 0), P(0, 1)));
            Assert.NotEqual(baseline, Detail2DRules.CanonicalArcSignature(P(0, 0), 1.001, P(1, 0), P(0, 1)));
        }

        [Fact]
        public void Arc_signature_refuses_nonpositive_or_malformed_input()
        {
            Assert.Null(Detail2DRules.CanonicalArcSignature(P(0, 0), 0.0, P(1, 0), P(0, 1)));
            Assert.Null(Detail2DRules.CanonicalArcSignature(P(0, 0), -1.0, P(1, 0), P(0, 1)));
            Assert.Null(Detail2DRules.CanonicalArcSignature(P(0, 0), double.NaN, P(1, 0), P(0, 1)));
            Assert.Null(Detail2DRules.CanonicalArcSignature(null, 1.0, P(1, 0), P(0, 1)));
            Assert.Null(Detail2DRules.CanonicalArcSignature(P(0, 0), 1.0, P(double.NaN, 0), P(0, 1)));
        }

        [Fact]
        public void A_line_and_an_arc_over_the_same_points_never_share_a_signature()
        {
            string line = Detail2DRules.CanonicalLineSignature(P(0, 0), P(4, 0));
            string arc = Detail2DRules.CanonicalArcSignature(P(2, 0), 2.0, P(0, 0), P(4, 0));
            Assert.NotNull(line);
            Assert.NotNull(arc);
            Assert.NotEqual(line, arc);
        }

        // ---- loop signatures -------------------------------------------------

        [Fact]
        public void Loop_signature_is_invariant_to_start_vertex_and_traversal_direction()
        {
            var square = new[] { P(0, 0), P(4, 0), P(4, 4), P(0, 4) };
            string baseline = Detail2DRules.LoopSignature(EdgeSigs(square));
            Assert.NotNull(baseline);

            for (int start = 0; start < square.Length; start++)
            {
                var rotated = Rotate(square, start);
                Assert.Equal(baseline, Detail2DRules.LoopSignature(EdgeSigs(rotated)));
                Assert.Equal(baseline, Detail2DRules.LoopSignature(EdgeSigs(Reverse(rotated))));
            }
        }

        [Fact]
        public void Loop_signature_distinguishes_orders_outside_the_rotation_reflection_orbit()
        {
            // Same four entries; (a,c,b,d) is not a rotation of (a,b,c,d) in
            // either direction, so it is a DIFFERENT loop and must not collide.
            string abcd = Detail2DRules.LoopSignature(new[] { "a", "b", "c", "d" });
            string acbd = Detail2DRules.LoopSignature(new[] { "a", "c", "b", "d" });
            Assert.NotNull(abcd);
            Assert.NotEqual(abcd, acbd);
        }

        [Fact]
        public void Loop_signature_fails_closed_on_missing_or_forged_entries()
        {
            Assert.Null(Detail2DRules.LoopSignature(null));
            Assert.Null(Detail2DRules.LoopSignature(new string[0]));
            Assert.Null(Detail2DRules.LoopSignature(new string[] { null }));
            Assert.Null(Detail2DRules.LoopSignature(new[] { "" }));
            // An entry carrying a separator control character (record or group
            // separator) could make one forged entry hash like two honest ones -
            // refused, never disambiguated by guesswork.
            Assert.Null(Detail2DRules.LoopSignature(new[] { "a" + (char)30 + "b" }));
            Assert.Null(Detail2DRules.LoopSignature(new[] { "a" + (char)29 + "b" }));
            // A single honest curve is a valid (degenerate-boundary) loop list.
            Assert.NotNull(Detail2DRules.LoopSignature(new[] { "a" }));
        }

        [Fact]
        public void Different_geometry_with_the_same_bounding_box_gets_different_signatures()
        {
            var square = new[] { P(0, 0), P(4, 0), P(4, 4), P(0, 4) };
            var diamond = new[] { P(2, 0), P(4, 2), P(2, 4), P(0, 2) };
            string sq = Detail2DRules.LoopSignature(EdgeSigs(square));
            string di = Detail2DRules.LoopSignature(EdgeSigs(diamond));
            Assert.NotNull(sq);
            Assert.NotNull(di);
            Assert.NotEqual(sq, di);
        }

        [Fact]
        public void Loop_signature_survives_jitter_and_moves_with_the_loop()
        {
            var square = new[] { P(0, 0), P(4, 0), P(4, 4), P(0, 4) };
            var jittered = new[] { P(1e-7, -1e-7), P(4 + 1e-7, 0), P(4, 4 - 1e-7), P(-1e-7, 4) };
            var moved = new[] { P(0.002, 0), P(4.002, 0), P(4.002, 4), P(0.002, 4) };

            string baseline = Detail2DRules.LoopSignature(EdgeSigs(square));
            Assert.Equal(baseline, Detail2DRules.LoopSignature(EdgeSigs(jittered)));
            Assert.NotEqual(baseline, Detail2DRules.LoopSignature(EdgeSigs(moved)));
        }

        // ---- region signatures -----------------------------------------------

        [Fact]
        public void Region_signature_orders_holes_and_distinguishes_the_exterior()
        {
            string h1h2 = Detail2DRules.RegionSignature("outer", new[] { "h1", "h2" });
            string h2h1 = Detail2DRules.RegionSignature("outer", new[] { "h2", "h1" });
            Assert.NotNull(h1h2);
            Assert.Equal(h1h2, h2h1); // Revit's enumeration order is not identity

            // The exterior is DISTINGUISHED: A-with-hole-B is not B-with-hole-A.
            Assert.NotEqual(
                Detail2DRules.RegionSignature("A", new[] { "B" }),
                Detail2DRules.RegionSignature("B", new[] { "A" }));

            // No holes: null and empty read the same, and differ from one hole.
            Assert.Equal(
                Detail2DRules.RegionSignature("outer", null),
                Detail2DRules.RegionSignature("outer", new string[0]));
            Assert.NotEqual(h1h2, Detail2DRules.RegionSignature("outer", new[] { "h1" }));
        }

        [Fact]
        public void Region_signature_fails_closed_on_missing_or_forged_parts()
        {
            Assert.Null(Detail2DRules.RegionSignature(null, new[] { "h" }));
            Assert.Null(Detail2DRules.RegionSignature("", new[] { "h" }));
            Assert.Null(Detail2DRules.RegionSignature("outer", new string[] { null }));
            Assert.Null(Detail2DRules.RegionSignature("outer", new[] { "" }));
            Assert.Null(Detail2DRules.RegionSignature("out" + (char)29 + "er", new[] { "h" }));
            Assert.Null(Detail2DRules.RegionSignature("outer", new[] { "h" + (char)29 }));
        }

        // ---- Sha256Hex -------------------------------------------------------

        [Fact]
        public void Sha256Hex_is_the_bridge_wide_hash_and_is_deterministic()
        {
            string h = Detail2DRules.Sha256Hex("canonical");
            Assert.Equal(RequestFingerprint.Sha256Hex("canonical"), h);
            Assert.Matches("^[0-9a-f]{64}$", h);
            Assert.Equal(h, Detail2DRules.Sha256Hex("canonical"));
            Assert.NotEqual(h, Detail2DRules.Sha256Hex("canonicaL"));
            Assert.Equal(Detail2DRules.Sha256Hex(""), Detail2DRules.Sha256Hex(null));
        }

        // ---- segment validation ----------------------------------------------

        [Fact]
        public void A_drawable_segment_passes_and_a_degenerate_one_is_refused_with_its_code()
        {
            Assert.Null(Detail2DRules.ValidateSegment(P(0, 0), P(1, 0)));

            string same = Detail2DRules.ValidateSegment(P(1, 2), P(1, 2));
            Assert.NotNull(same);
            Assert.StartsWith(Detail2DRules.CodeDegenerateCurve + ":", same, StringComparison.Ordinal);
        }

        [Fact]
        public void Segment_degeneracy_is_judged_at_the_central_tolerance()
        {
            // 1e-7 ft is under the 1e-6 ft tolerance: a point, refused.
            Assert.NotNull(Detail2DRules.ValidateSegment(P(0, 0), P(1e-7, 0)));
            // 1e-5 ft is over it: short, but a line.
            Assert.Null(Detail2DRules.ValidateSegment(P(0, 0), P(1e-5, 0)));
        }

        [Fact]
        public void Malformed_segment_input_is_refused_as_invalid_geometry_never_approved()
        {
            string missing = Detail2DRules.ValidateSegment(null, P(1, 0));
            Assert.StartsWith(Detail2DRules.CodeInvalidGeometry + ":", missing, StringComparison.Ordinal);

            string arity = Detail2DRules.ValidateSegment(new[] { 1.0, 2.0 }, P(1, 0));
            Assert.StartsWith(Detail2DRules.CodeInvalidGeometry + ":", arity, StringComparison.Ordinal);

            string nan = Detail2DRules.ValidateSegment(P(double.NaN, 0), P(1, 0));
            Assert.StartsWith(Detail2DRules.CodeInvalidGeometry + ":", nan, StringComparison.Ordinal);
            Assert.Contains("NaN", nan);
        }

        // ---- arc by three points ---------------------------------------------

        [Fact]
        public void Arc_by_three_points_solves_the_circumcentre()
        {
            double[] center;
            double radius;
            string err = Detail2DRules.ValidateArcByThreePoints(P(0, 0), P(2, 0), P(1, 1), out center, out radius);

            Assert.Null(err);
            Assert.NotNull(center);
            Assert.Equal(1.0, center[0], 9);
            Assert.Equal(0.0, center[1], 9);
            Assert.Equal(0.0, center[2], 9);
            Assert.Equal(1.0, radius, 9);
        }

        [Fact]
        public void Collinear_points_are_refused_naming_all_three()
        {
            double[] center;
            double radius;
            string err = Detail2DRules.ValidateArcByThreePoints(P(0, 0), P(2, 0), P(1, 0), out center, out radius);

            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeDegenerateCurve + ":", err, StringComparison.Ordinal);
            Assert.Contains("collinear", err);
            // The three points travel in the message, canonically (mm on the grid):
            // 2 ft = 609.6 mm, 1 ft = 304.8 mm.
            Assert.Contains("609.6", err);
            Assert.Contains("304.8", err);
            // No half answer beside a refusal.
            Assert.Null(center);
            Assert.Equal(0.0, radius);
        }

        [Fact]
        public void Coincident_points_and_split_heights_are_refused_before_any_solve()
        {
            double[] center;
            double radius;

            string coincide = Detail2DRules.ValidateArcByThreePoints(P(1, 1), P(1, 1), P(2, 2), out center, out radius);
            Assert.StartsWith(Detail2DRules.CodeDegenerateCurve + ":", coincide, StringComparison.Ordinal);
            Assert.Contains("start and end", coincide);
            Assert.Null(center);

            string heights = Detail2DRules.ValidateArcByThreePoints(P(0, 0, 0), P(2, 0, 0), P(1, 1, 0.5), out center, out radius);
            Assert.StartsWith(Detail2DRules.CodeNonCoplanar + ":", heights, StringComparison.Ordinal);
            Assert.Null(center);
        }

        [Fact]
        public void Near_collinearity_is_judged_at_the_central_tolerance()
        {
            double[] center;
            double radius;

            // 1e-7 ft off the chord: collinear at tolerance, refused.
            Assert.NotNull(Detail2DRules.ValidateArcByThreePoints(P(0, 0), P(2, 0), P(1, 1e-7), out center, out radius));
            Assert.Null(center);

            // 1e-5 ft off: a real (huge) circle, solved and finite.
            string err = Detail2DRules.ValidateArcByThreePoints(P(0, 0), P(2, 0), P(1, 1e-5), out center, out radius);
            Assert.Null(err);
            Assert.NotNull(center);
            Assert.True(radius > 0 && !double.IsInfinity(radius));
        }

        // ---- polyline validation ---------------------------------------------

        [Fact]
        public void Polyline_minimum_counts_hold_open_and_closed()
        {
            Assert.NotNull(Detail2DRules.ValidatePolyline(null, closed: false));
            Assert.NotNull(Detail2DRules.ValidatePolyline(new[] { P(0, 0) }, closed: false));
            Assert.Null(Detail2DRules.ValidatePolyline(new[] { P(0, 0), P(1, 0) }, closed: false));

            string twoClosed = Detail2DRules.ValidatePolyline(new[] { P(0, 0), P(1, 0) }, closed: true);
            Assert.StartsWith(Detail2DRules.CodeOpenLoop + ":", twoClosed, StringComparison.Ordinal);

            Assert.Null(Detail2DRules.ValidatePolyline(new[] { P(0, 0), P(1, 0), P(1, 1) }, closed: true));
        }

        [Fact]
        public void A_repeated_consecutive_vertex_is_refused_naming_both_indices()
        {
            // 1e-5 ft apart: the grid cannot tell them apart, so the segment
            // between them cannot be drawn.
            string err = Detail2DRules.ValidatePolyline(new[] { P(0, 0), P(1e-5, 0), P(4, 0) }, closed: false);
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeDegenerateCurve + ":", err, StringComparison.Ordinal);
            Assert.Contains("points[0]", err);
            Assert.Contains("points[1]", err);
        }

        [Fact]
        public void A_closed_polyline_must_not_repeat_its_first_vertex()
        {
            string err = Detail2DRules.ValidatePolyline(
                new[] { P(0, 0), P(4, 0), P(4, 4), P(0, 0) }, closed: true);
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeDegenerateCurve + ":", err, StringComparison.Ordinal);
            Assert.Contains("implicit", err);
        }

        [Fact]
        public void An_open_polyline_may_cross_itself_only_a_loop_may_not()
        {
            // A zigzag that crosses its own earlier segment is a legitimate
            // DRAWING; the same vertices as a region boundary are not.
            var zigzag = new[] { P(0, 0), P(4, 4), P(4, 0), P(0, 4) };
            Assert.Null(Detail2DRules.ValidatePolyline(zigzag, closed: false));
            Assert.NotNull(Detail2DRules.ValidateLoop(zigzag));
        }

        [Fact]
        public void Polyline_and_loop_sizes_are_bounded_with_the_limit_in_the_message()
        {
            var many = new double[201][];
            for (int i = 0; i < many.Length; i++) many[i] = P(i, 0);

            string poly = Detail2DRules.ValidatePolyline(many, closed: false);
            Assert.NotNull(poly);
            Assert.Contains("200", poly);

            string loop = Detail2DRules.ValidateLoop(many);
            Assert.NotNull(loop);
            Assert.Contains("200", loop);
        }

        [Fact]
        public void Malformed_polyline_points_are_named_by_index()
        {
            string err = Detail2DRules.ValidatePolyline(new[] { P(0, 0), P(double.NaN, 1), P(2, 2) }, closed: false);
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeInvalidGeometry + ":", err, StringComparison.Ordinal);
            Assert.Contains("points[1]", err);
        }

        // ---- loop validation -------------------------------------------------

        [Fact]
        public void A_loop_needs_three_vertices_and_a_triangle_passes()
        {
            string open = Detail2DRules.ValidateLoop(new[] { P(0, 0), P(4, 0) });
            Assert.StartsWith(Detail2DRules.CodeOpenLoop + ":", open, StringComparison.Ordinal);

            Assert.Null(Detail2DRules.ValidateLoop(new[] { P(0, 0), P(4, 0), P(2, 3) }));
            Assert.Null(Detail2DRules.ValidateLoop(Square10));
        }

        [Fact]
        public void A_bowtie_is_refused_as_self_intersection_naming_the_segments()
        {
            string err = Detail2DRules.ValidateLoop(new[] { P(0, 0), P(2, 2), P(2, 0), P(0, 2) });
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeSelfIntersection + ":", err, StringComparison.Ordinal);
            Assert.Contains("segment", err);
        }

        [Fact]
        public void Three_collinear_vertices_cannot_bound_a_region()
        {
            // Zero area: the closing segment doubles back over the others.
            string err = Detail2DRules.ValidateLoop(new[] { P(0, 0), P(2, 0), P(5, 0) });
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeSelfIntersection + ":", err, StringComparison.Ordinal);
        }

        [Fact]
        public void A_redundant_straight_through_vertex_is_allowed()
        {
            // (2,0) sits mid-edge but the boundary never doubles back: a valid
            // (if wasteful) simple polygon, and refusing it would reject real
            // Revit output re-read at verification time.
            Assert.Null(Detail2DRules.ValidateLoop(new[] { P(0, 0), P(2, 0), P(4, 0), P(4, 4), P(0, 4) }));
        }

        [Fact]
        public void Touching_a_non_consecutive_vertex_or_edge_is_an_intersection()
        {
            // Vertex (2,0) of segment 2/3 lands ON segment 0 - consecutive
            // segments may share a vertex, anything else touching is a defect.
            string err = Detail2DRules.ValidateLoop(new[] { P(0, 0), P(4, 0), P(4, 4), P(2, 0), P(0, 4) });
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeSelfIntersection + ":", err, StringComparison.Ordinal);
        }

        [Fact]
        public void A_pinched_loop_reusing_a_vertex_is_an_intersection()
        {
            // A figure-eight through one shared vertex: the vertex repeats
            // non-consecutively, which the pairwise scan reads as contact.
            string err = Detail2DRules.ValidateLoop(
                new[] { P(0, 0), P(2, 0), P(2, 2), P(0, 0), P(-2, 0), P(-2, -2) });
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeSelfIntersection + ":", err, StringComparison.Ordinal);
        }

        [Fact]
        public void A_loop_lives_in_one_view_plane()
        {
            string err = Detail2DRules.ValidateLoop(new[] { P(0, 0, 0), P(4, 0, 0), P(2, 3, 1) });
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeNonCoplanar + ":", err, StringComparison.Ordinal);
            Assert.Contains("points[2]", err);
        }

        [Fact]
        public void Loop_vertices_the_grid_cannot_separate_are_refused()
        {
            string err = Detail2DRules.ValidateLoop(new[] { P(0, 0), P(1e-5, 0), P(4, 0), P(4, 4) });
            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeDegenerateCurve + ":", err, StringComparison.Ordinal);

            string wrap = Detail2DRules.ValidateLoop(new[] { P(0, 0), P(4, 0), P(4, 4), P(1e-5, 0) });
            Assert.NotNull(wrap);
            Assert.StartsWith(Detail2DRules.CodeDegenerateCurve + ":", wrap, StringComparison.Ordinal);
            Assert.Contains("implicit", wrap);
        }

        // ---- containment -----------------------------------------------------

        [Fact]
        public void LoopContains_answers_strict_containment_only()
        {
            Assert.True(Detail2DRules.LoopContains(Square10, HoleInside));

            // Winding direction is not identity: the same hole traversed the
            // other way is still inside.
            Assert.True(Detail2DRules.LoopContains(Square10, Reverse((double[][])HoleInside.Clone())));

            // Outside, crossing, touching, identical: all false.
            Assert.False(Detail2DRules.LoopContains(Square10,
                new[] { P(12, 2), P(14, 2), P(14, 4), P(12, 4) }));
            Assert.False(Detail2DRules.LoopContains(Square10,
                new[] { P(8, 2), P(12, 2), P(12, 4), P(8, 4) }));
            Assert.False(Detail2DRules.LoopContains(Square10,
                new[] { P(0, 5), P(2, 3), P(2, 7) })); // vertex ON the boundary
            Assert.False(Detail2DRules.LoopContains(Square10, Square10));
            Assert.False(Detail2DRules.LoopContains(HoleInside, Square10)); // never symmetric
        }

        [Fact]
        public void LoopContains_fails_closed_on_malformed_input()
        {
            Assert.False(Detail2DRules.LoopContains(null, HoleInside));
            Assert.False(Detail2DRules.LoopContains(Square10, null));
            Assert.False(Detail2DRules.LoopContains(Square10, new[] { P(1, 1), P(2, 2) }));
            Assert.False(Detail2DRules.LoopContains(Square10, new[] { P(1, 1), P(2, 1), P(double.NaN, 2) }));
        }

        // ---- region hierarchy ------------------------------------------------

        [Fact]
        public void A_single_loop_is_its_own_exterior()
        {
            int outer;
            Assert.Null(Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[] { Square10 }, out outer));
            Assert.Equal(0, outer);
        }

        [Fact]
        public void The_exterior_is_detected_wherever_the_caller_put_it()
        {
            int outer;
            Assert.Null(Detail2DRules.ValidateRegionLoops(
                new IReadOnlyList<double[]>[] { Square10, HoleInside }, out outer));
            Assert.Equal(0, outer);

            // Hole first: the hierarchy is DETECTED, never assumed from order.
            Assert.Null(Detail2DRules.ValidateRegionLoops(
                new IReadOnlyList<double[]>[] { HoleInside, Square10 }, out outer));
            Assert.Equal(1, outer);
        }

        [Fact]
        public void A_hole_outside_the_exterior_is_refused_naming_the_indices()
        {
            int outer;
            string err = Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[]
            {
                Square10,
                new[] { P(12, 2), P(14, 2), P(14, 4), P(12, 4) }
            }, out outer);

            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeLoopHierarchy + ":", err, StringComparison.Ordinal);
            Assert.Contains("loops[1]", err);
            Assert.Contains("lies outside", err);
            Assert.Equal(-1, outer);
        }

        [Fact]
        public void A_hole_touching_the_exterior_boundary_is_refused_as_a_touch()
        {
            int outer;
            string err = Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[]
            {
                Square10,
                new[] { P(0, 5), P(2, 3), P(2, 7) } // one vertex on the left edge
            }, out outer);

            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeLoopHierarchy + ":", err, StringComparison.Ordinal);
            Assert.Contains("touches or crosses", err);
            Assert.Equal(-1, outer);
        }

        [Fact]
        public void A_hole_inside_another_hole_is_refused_naming_the_pair()
        {
            int outer;
            string err = Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[]
            {
                Square10,
                new[] { P(2, 2), P(8, 2), P(8, 8), P(2, 8) },
                new[] { P(4, 4), P(6, 4), P(6, 6), P(4, 6) }
            }, out outer);

            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeLoopHierarchy + ":", err, StringComparison.Ordinal);
            Assert.Contains("loops[1] contains loops[2]", err);
            Assert.Equal(-1, outer);
        }

        [Fact]
        public void Overlapping_holes_are_refused_naming_the_pair()
        {
            int outer;
            string err = Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[]
            {
                Square10,
                new[] { P(2, 2), P(6, 2), P(6, 6), P(2, 6) },
                new[] { P(4, 4), P(8, 4), P(8, 8), P(4, 8) }
            }, out outer);

            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeLoopHierarchy + ":", err, StringComparison.Ordinal);
            Assert.Contains("loops[1]", err);
            Assert.Contains("loops[2]", err);
            Assert.Equal(-1, outer);
        }

        [Fact]
        public void An_invalid_member_loop_is_reported_under_its_index()
        {
            int outer;
            string err = Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[]
            {
                Square10,
                new[] { P(2, 2), P(4, 4) } // open
            }, out outer);

            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeOpenLoop + ": loops[1]:", err, StringComparison.Ordinal);
            Assert.Equal(-1, outer);

            string bowtie = Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[]
            {
                new[] { P(0, 0), P(2, 2), P(2, 0), P(0, 2) }
            }, out outer);
            Assert.StartsWith(Detail2DRules.CodeSelfIntersection + ": loops[0]:", bowtie, StringComparison.Ordinal);
            Assert.Equal(-1, outer);
        }

        [Fact]
        public void Region_loops_must_share_one_view_plane()
        {
            int outer;
            string err = Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[]
            {
                Square10,
                new[] { P(2, 2, 1), P(4, 2, 1), P(4, 4, 1), P(2, 4, 1) }
            }, out outer);

            Assert.NotNull(err);
            Assert.StartsWith(Detail2DRules.CodeNonCoplanar + ":", err, StringComparison.Ordinal);
            Assert.Contains("loops[1]", err);
            Assert.Equal(-1, outer);
        }

        [Fact]
        public void Region_loop_counts_are_bounded_and_an_empty_region_is_refused()
        {
            int outer;
            Assert.NotNull(Detail2DRules.ValidateRegionLoops(null, out outer));
            Assert.Equal(-1, outer);
            Assert.NotNull(Detail2DRules.ValidateRegionLoops(new IReadOnlyList<double[]>[0], out outer));
            Assert.Equal(-1, outer);

            var tooMany = new IReadOnlyList<double[]>[33];
            for (int i = 0; i < tooMany.Length; i++)
                tooMany[i] = new[] { P(3 * i, 0), P(3 * i + 2, 0), P(3 * i + 2, 2), P(3 * i, 2) };
            string err = Detail2DRules.ValidateRegionLoops(tooMany, out outer);
            Assert.NotNull(err);
            Assert.Contains("32", err);
            Assert.Equal(-1, outer);
        }

        // ---- the codes are the contract --------------------------------------

        [Fact]
        public void Error_codes_are_pinned_to_their_published_spellings()
        {
            // A client branches on these strings; changing one is a contract
            // change, not a rename.
            Assert.Equal("open_loop", Detail2DRules.CodeOpenLoop);
            Assert.Equal("self_intersection", Detail2DRules.CodeSelfIntersection);
            Assert.Equal("degenerate_curve", Detail2DRules.CodeDegenerateCurve);
            Assert.Equal("non_coplanar_geometry", Detail2DRules.CodeNonCoplanar);
            Assert.Equal("ambiguous_resource", Detail2DRules.CodeAmbiguousResource);
            Assert.Equal("invalid_line_style", Detail2DRules.CodeInvalidLineStyle);
            Assert.Equal("masking_mismatch", Detail2DRules.CodeMaskingMismatch);
            Assert.Equal("invalid_geometry", Detail2DRules.CodeInvalidGeometry);
            Assert.Equal("invalid_loop_hierarchy", Detail2DRules.CodeLoopHierarchy);
            Assert.Equal("view_not_found", Detail2DRules.CodeViewNotFound);
            Assert.Equal("incompatible_view", Detail2DRules.CodeIncompatibleView);
            Assert.Equal("invalid_family_symbol", Detail2DRules.CodeInvalidFamilySymbol);
            Assert.Equal("invalid_placement_type", Detail2DRules.CodeInvalidPlacementType);
        }

        [Fact]
        public void Tolerances_and_limits_are_pinned()
        {
            Assert.Equal(1e-6, Detail2DRules.CurveToleranceFeet);
            Assert.Equal(0.1 / 304.8, Detail2DRules.QuantumFeet);
            Assert.Equal(500, Detail2DRules.MaxActions);
            Assert.Equal(200, Detail2DRules.MaxPolylinePoints);
            Assert.Equal(32, Detail2DRules.MaxLoopsPerRegion);
            Assert.Equal(200, Detail2DRules.MaxCurvesPerLoop);
            Assert.Equal("line", Detail2DRules.KindLine);
            Assert.Equal("arc", Detail2DRules.KindArc);
        }
    }
}
