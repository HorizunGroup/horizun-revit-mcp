// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A BOX DRAWN ON A LAYER WHOSE LINES ARE RUNS.
//
// MEASURED (campaign 7, real mechanical plan M102): a 24 x 24 in square on the
// duct layer - a CLOSED polyline (flag 70 = 1, five vertices, the last repeating
// the first) with both diagonals as separate LINEs, and a supply duct ending on
// its left edge. Revit handed the square over as ONE PolyLine, TL,TR,BR,BL,TL,BR
// - one diagonal appended - and the other diagonal as a Line. The first fix
// (7a746f9) read a ring only when a PolyLine's first and last coordinates met,
// and its test fed segments already named ring:, so it passed while the plan
// still read six duct runs. These tests build the segments through
// CadRings.PolylineSegments, the function the harvest calls.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadRingsTests
    {
        // the square as Revit placed it, in mm
        private static readonly CadPoint TL = new CadPoint(39111.115, 29392.395);
        private static readonly CadPoint TR = new CadPoint(39720.715, 29392.395);
        private static readonly CadPoint BR = new CadPoint(39720.715, 28782.795);
        private static readonly CadPoint BL = new CadPoint(39111.115, 28782.795);

        private static int[] Loops(params CadPoint[] pts) => CadRings.LoopOfSegment(pts, CadRings.VertexToleranceMm);

        /// <summary>The layer as the harvest delivers it: the PolyLine, the other diagonal, and the duct feeding the box.</summary>
        private static List<CadSegment> M102(int firstIndex = 17)
        {
            var segs = CadRings.PolylineSegments(new[] { TL, TR, BR, BL, TL, BR }, "M-DUCT", firstIndex);
            segs.Add(new CadSegment(BL, TR, "M-DUCT"));                                                    // LINE 2226
            segs.Add(new CadSegment(new CadPoint(39111.0, 29064.3), new CadPoint(37452.3, 29064.3), "M-DUCT")); // 21D4, a real run
            return segs;
        }

        private static CadRequirementSet Set(bool includeClosed = false) => CadRequirementSet.Load(JObject.Parse((@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 's', 'version': '1.0.0', 'title': 't' }, 'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25.4, 'gap_mm': 25.4, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [ { 'id': 'd', 'precedence': 10, 'layers': ['M-DUCT'], 'produces': 'duct', 'family_type': 'Rectangular Duct: X',
                 'system_type': 'Supply Air', 'level': 'Level 1', 'offset_mm': 2743.2,
                 'geometry': { 'from': 'single_lines', 'min_length_mm': 100, 'merge_collinear': false" +
                 (includeClosed ? ", 'include_closed_polylines': true" : "") + " } } ] }").Replace('\'', '"')));

        [Fact]
        public void A_loop_is_found_wherever_the_polyline_returns_to_its_own_vertex()
        {
            Assert.Equal(new[] { 0, 0, 0, 0, -1 }, Loops(TL, TR, BR, BL, TL, BR));   // as Revit handed M102 over
            Assert.Equal(new[] { 0, 0, 0, 0 }, Loops(TL, TR, BR, BL, TL));            // the closed square alone
            Assert.Equal(new[] { -1, 0, 0, 0 }, Loops(BR, TL, TR, BL, TL));           // a tail, then the loop
            Assert.Equal(new[] { 0, 0, 0 }, Loops(TL, TR, BR, TL));                   // a closed triangle is a ring
            Assert.Equal(new[] { -1, -1, -1 }, Loops(TL, TR, BR, BL));                // open: no ring
            Assert.Equal(new[] { -1, -1 }, Loops(TL, TR, TL));                        // there and back is not a loop
            var far = new CadPoint(50000, 29392.395);
            var farB = new CadPoint(50000, 28782.795);
            var farC = new CadPoint(51000, 28782.795);
            Assert.Equal(new[] { 0, 0, 0, 0, -1, 1, 1, 1 },                          // two loops joined by a tail
                         Loops(TL, TR, BR, BL, TL, far, farB, farC, far));
        }

        [Fact]
        public void The_harvest_names_each_loop_once_and_leaves_the_tail_ordinary()
        {
            List<CadSegment> segs = CadRings.PolylineSegments(new[] { TL, TR, BR, BL, TL, BR }, "M-DUCT", 40);
            Assert.Equal(5, segs.Count);
            Assert.All(segs.Take(4), s => { Assert.True(s.ClosedRing); Assert.Equal("ring:40", s.SourceCurveId); });
            Assert.False(segs[4].ClosedRing);
            Assert.Null(segs[4].SourceCurveId);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, segs.Select(s => s.SourceIndex));
            Assert.All(segs, s => Assert.Equal(CadCurveKind.Polyline, s.SourceKind));
        }

        [Fact]
        public void The_figure_is_the_ring_and_its_chords_and_not_the_run_that_feeds_it()
        {
            List<CadSegment> layer = M102();
            int rings, chords;
            HashSet<CadSegment> figure = CadRings.Figure(layer, 25.4, out rings, out chords);
            Assert.Equal(1, rings);
            Assert.Equal(2, chords);                    // the appended diagonal and the separate Line
            Assert.Equal(6, figure.Count);
            Assert.DoesNotContain(layer[6], figure);    // the duct ending on the box's edge stays a run
        }

        [Fact]
        public void A_line_that_only_touches_a_corner_or_doubles_an_edge_is_not_a_chord()
        {
            var layer = CadRings.PolylineSegments(new[] { TL, TR, BR, BL, TL }, "M-DUCT", 0);
            var fromCorner = new CadSegment(TR, new CadPoint(45000, 35000), "M-DUCT");   // leaves the ring outward
            var alongEdge = new CadSegment(TL, TR, "M-DUCT");                            // an edge drawn twice
            var otherRing = CadRings.PolylineSegments(new[]
            {
                new CadPoint(60000, 0), new CadPoint(61000, 0), new CadPoint(61000, 1000), new CadPoint(60000, 1000), new CadPoint(60000, 0)
            }, "M-DUCT", 10);
            var betweenRings = new CadSegment(BR, new CadPoint(60000, 1000), "M-DUCT");  // corner to corner of two rings
            layer.AddRange(new[] { fromCorner, alongEdge, betweenRings });
            layer.AddRange(otherRing);
            int rings, chords;
            HashSet<CadSegment> figure = CadRings.Figure(layer, 25.4, out rings, out chords);
            Assert.Equal(2, rings);
            Assert.Equal(0, chords);
            Assert.DoesNotContain(fromCorner, figure);
            Assert.DoesNotContain(alongEdge, figure);
            Assert.DoesNotContain(betweenRings, figure);
        }

        [Fact]
        public void The_plan_reads_one_duct_where_M102_drew_a_box_with_its_diagonals_and_one_duct()
        {
            CadInterpretation read = CadInterpretationRules.Interpret(M102(), Set(), "h");
            List<CadCandidate> ducts = read.Candidates.Where(c => c.ProposedKind == "duct").ToList();
            Assert.Single(ducts);
            Assert.Equal(37452.3, ducts[0].Geometry.Min(p => p.X), 1);
            CadUnclaimed row = Assert.Single(read.Unclaimed, u => u.Reason == "closed_outline_not_a_run");
            Assert.Equal(6, row.EntityCount);
            Assert.Equal("M-DUCT", row.Layer);
            Assert.Contains("1 closed outline(s)", row.Means);
            Assert.Contains("4 edge(s), and 2 chord(s)", row.Means);
            Assert.Contains("include_closed_polylines", row.Means);

            // a rule whose runs ARE closed loops says so, and then every drawn line is a run again
            CadInterpretation all = CadInterpretationRules.Interpret(M102(), Set(includeClosed: true), "h");
            Assert.Equal(7, all.Candidates.Count(c => c.ProposedKind == "duct"));
            Assert.DoesNotContain(all.Unclaimed, u => u.Reason == "closed_outline_not_a_run");
        }

        [Fact]
        public void A_network_read_from_an_IR_leaves_out_what_the_conversion_leaves_out()
        {
            // cad_networks reads the IR's segments, where a ring is an entity marked closed - not a ring: id
            var ir = new CadIr();
            ir.Entities.Add(new CadIrEntity { Id = "e1", Layer = "M-DUCT", Kind = CadEntityKind.Polyline, Closed = true,
                                              Points = { TL, TR, BR, BL, TL } });
            ir.Entities.Add(new CadIrEntity { Id = "e2", Layer = "M-DUCT", Kind = CadEntityKind.Line, Points = { TL, BR } });
            ir.Entities.Add(new CadIrEntity { Id = "e3", Layer = "M-DUCT", Kind = CadEntityKind.Line,
                                              Points = { new CadPoint(39111.0, 29064.3), new CadPoint(37452.3, 29064.3) } });
            List<CadSegment> segs = ir.ToSegments();
            Assert.Equal(4, segs.Count(s => s.ClosedRing));
            int leftOut, rings, chords;
            List<CadSegment> kept = CadRings.WithoutOutlines(segs, Set(), out leftOut, out rings, out chords);
            Assert.Equal(5, leftOut);
            Assert.Equal(1, rings);
            Assert.Equal(1, chords);
            CadSegment run = Assert.Single(kept);
            Assert.Equal(37452.3, run.B.X, 1);
            Assert.Equal(6, CadRings.WithoutOutlines(segs, Set(includeClosed: true), out leftOut, out rings, out chords).Count);
            Assert.Equal(0, leftOut);
        }
    }
}
