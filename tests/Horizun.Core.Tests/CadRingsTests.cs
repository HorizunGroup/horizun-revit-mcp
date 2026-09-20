// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A CLOSED LOOP IS DECIDED BY WHAT THE RULE DECLARES, NEVER BY ITS SHAPE.
//
// MEASURED (campaign 7, real mechanical plan M102): a 24 x 24 in square on the
// duct layer - a CLOSED polyline with both diagonals as separate LINEs, and a
// supply duct ending on its left edge. Revit handed the square over as ONE
// PolyLine, TL,TR,BR,BL,TL,BR - one diagonal appended - and the other diagonal
// as a Line.
//
// The first reading excluded any loop that had a chord. A chord is a property
// of the LINE WORK: a ring main with a legitimate interior connection has one
// too, and two circuits sharing an edge look the same to the topology. So
// nothing is excluded unless the CALLER declares what a figure is in their
// drawings (geometry.closed_loops + closed_figure); everything else is proposed
// and held for review, and a rule that declares these loops are runs grants
// them no size and joins nothing.
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

        /// <summary>The M102 layer as the harvest delivers it: the PolyLine, the other diagonal, and the duct feeding the box.</summary>
        private static List<CadSegment> M102(int firstIndex = 17)
        {
            var segs = CadRings.PolylineSegments(new[] { TL, TR, BR, BL, TL, BR }, "M-DUCT", firstIndex);
            segs.Add(new CadSegment(BL, TR, "M-DUCT"));                                                    // LINE 2226
            segs.Add(new CadSegment(new CadPoint(39111.0, 29064.3), new CadPoint(37452.3, 29064.3), "M-DUCT")); // 21D4, a real run
            return segs;
        }

        /// <summary>A rectangle drawn as four loose lines.</summary>
        private static List<CadSegment> LooseLoop(double x, double y, double w, double h, string layer = "M-DUCT")
        {
            var a = new CadPoint(x, y); var b = new CadPoint(x + w, y);
            var c = new CadPoint(x + w, y + h); var d = new CadPoint(x, y + h);
            return new List<CadSegment>
            {
                new CadSegment(a, b, layer), new CadSegment(b, c, layer),
                new CadSegment(c, d, layer), new CadSegment(d, a, layer)
            };
        }

        /// <summary>A duct rule over M-DUCT. geometry is whatever the case declares; nothing is declared by default.</summary>
        private static CadRequirementSet Set(string geometryExtra = "") => CadRequirementSet.Load(JObject.Parse((@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 's', 'version': '1.0.0', 'title': 't' }, 'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25.4, 'gap_mm': 25.4, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [ { 'id': 'd', 'precedence': 10, 'layers': ['M-DUCT'], 'produces': 'duct', 'family_type': 'Rectangular Duct: X',
                 'system_type': 'Supply Air', 'level': 'Level 1', 'offset_mm': 2743.2,
                 'geometry': { 'from': 'single_lines', 'min_length_mm': 100, 'merge_collinear': false" +
                 geometryExtra + " } } ] }").Replace('\'', '"')));

        /// <summary>What M102 declares a figure is: a small box, crossed, drawn as one closed polyline.</summary>
        private const string FigureRule =
            ", 'closed_loops': 'figures_by_rule', 'closed_figure': { 'max_perimeter_mm': 3000, 'min_chords': 2, " +
            "'max_edges': 6, 'one_closed_polyline': true }";

        private const string RunsRule = ", 'closed_loops': 'runs'";

        private static List<CadCandidate> Ducts(CadInterpretation r) =>
            r.Candidates.Where(c => c.ProposedKind == "duct").ToList();

        // ------------------------------------------------------------------ the loop reader

        [Fact]
        public void A_loop_is_found_wherever_the_polyline_returns_to_its_own_vertex()
        {
            Assert.Equal(new[] { 0, 0, 0, 0, -1 }, Loops(TL, TR, BR, BL, TL, BR));   // as Revit handed M102 over
            Assert.Equal(new[] { 0, 0, 0, 0 }, Loops(TL, TR, BR, BL, TL));            // the closed square alone
            Assert.Equal(new[] { -1, 0, 0, 0 }, Loops(BR, TL, TR, BL, TL));           // a tail, then the loop
            Assert.Equal(new[] { 0, 0, 0 }, Loops(TL, TR, BR, TL));                   // a closed triangle is a loop
            Assert.Equal(new[] { -1, -1, -1 }, Loops(TL, TR, BR, BL));                // open: no loop
            Assert.Equal(new[] { -1, -1 }, Loops(TL, TR, TL));                        // there and back is not a loop
            var far = new CadPoint(50000, 29392.395);
            var farB = new CadPoint(50000, 28782.795);
            var farC = new CadPoint(51000, 28782.795);
            Assert.Equal(new[] { 0, 0, 0, 0, -1, 1, 1, 1 },                           // two loops joined by a tail
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
        }

        [Fact]
        public void The_same_line_work_reads_the_same_drawn_as_loose_lines_or_as_one_polyline()
        {
            var loose = LooseLoop(0, 0, 610, 610);
            loose.Add(new CadSegment(new CadPoint(0, 0), new CadPoint(610, 610), "M-DUCT"));      // its diagonals
            loose.Add(new CadSegment(new CadPoint(0, 610), new CadPoint(610, 0), "M-DUCT"));
            var drawn = CadRings.PolylineSegments(new[]
            {
                new CadPoint(0, 0), new CadPoint(610, 0), new CadPoint(610, 610), new CadPoint(0, 610), new CadPoint(0, 0)
            }, "M-DUCT", 0);
            drawn.Add(new CadSegment(new CadPoint(0, 0), new CadPoint(610, 610), "M-DUCT"));
            drawn.Add(new CadSegment(new CadPoint(0, 610), new CadPoint(610, 0), "M-DUCT"));
            foreach (var layer in new[] { loose, drawn })
            {
                CadRings.CadLoop loop = Assert.Single(CadRings.Loops(layer, 25.4));
                Assert.Equal(4, loop.Boundary.Count);
                Assert.Equal(2, loop.Chords.Count);
                Assert.Equal(0, loop.AttachedRuns);
                Assert.True(loop.HasChords);
            }
            Assert.True(CadRings.Loops(drawn, 25.4)[0].OneClosedPolyline);
            Assert.False(CadRings.Loops(loose, 25.4)[0].OneClosedPolyline);   // the same reading, said honestly
            // a rule that asks only about size and chords excludes BOTH; one that asks for the polyline tells them apart
            string sizeOnly = ", 'closed_loops': 'figures_by_rule', 'closed_figure': { 'max_perimeter_mm': 3000, 'min_chords': 2 }";
            Assert.Empty(Ducts(CadInterpretationRules.Interpret(loose, Set(sizeOnly), "h")));
            Assert.Empty(Ducts(CadInterpretationRules.Interpret(drawn, Set(sizeOnly), "h")));
            Assert.Equal(6, Ducts(CadInterpretationRules.Interpret(loose, Set(FigureRule), "h")).Count);   // held, not built
            Assert.All(Ducts(CadInterpretationRules.Interpret(loose, Set(FigureRule), "h")),
                       c => Assert.False(c.EligibleForAutomaticApply));
        }

        // ------------------------------------------------------- the adversarial cases (frozen spec)

        [Fact]
        public void A_chord_alone_excludes_nothing_when_no_rule_declares_what_a_figure_is()
        {
            // case 4 of the frozen spec, WITHOUT a declared rule: the box that used to disappear
            CadInterpretation read = CadInterpretationRules.Interpret(M102(), Set(), "h");
            List<CadCandidate> ducts = Ducts(read);
            Assert.Equal(7, ducts.Count);                                        // nothing was deleted
            Assert.Single(ducts.Where(c => c.EligibleForAutomaticApply));        // only the duct that feeds it
            Assert.Equal(6, ducts.Count(c => !c.EligibleForAutomaticApply));     // 4 edges + 2 chords, held
            Assert.Contains(read.Unclaimed, u => u.Reason == "closed_loop_held_for_review");
            Assert.DoesNotContain(read.Unclaimed, u => u.Reason == "closed_figure_by_declared_rule");
        }

        [Fact]
        public void A_ring_main_with_an_interior_connection_is_not_excluded_by_its_chord()
        {
            // case 1: a closed circuit 12 x 8 m with a legitimate interior connection corner to corner
            List<CadSegment> layer = LooseLoop(0, 0, 12000, 8000);
            layer.Add(new CadSegment(new CadPoint(0, 0), new CadPoint(12000, 8000), "M-DUCT"));
            CadRings.CadLoop loop = Assert.Single(CadRings.Loops(layer, 25.4));
            Assert.True(loop.HasChords);                                          // the geometry is the same as a box's
            foreach (string extra in new[] { "", FigureRule })                    // with and without the M102 rule
            {
                CadInterpretation read = CadInterpretationRules.Interpret(layer, Set(extra), "h");
                Assert.Equal(5, Ducts(read).Count);                               // nothing excluded
                // the four edges AND the interior connection are held: if the loop is a figure the chord belongs to
                // it, and if it is a circuit the chord is a run. Neither is settled, so neither is built.
                Assert.Equal(5, Ducts(read).Count(c => !c.EligibleForAutomaticApply));
                Assert.Contains(read.Unclaimed, u => u.Reason == "closed_loop_held_for_review");
                Assert.DoesNotContain(read.Unclaimed, u => u.Reason == "closed_figure_by_declared_rule");
            }
        }

        [Fact]
        public void Two_circuits_that_share_an_edge_are_not_excluded_either()
        {
            // case 2: two 8 x 6 m rectangles sharing their middle edge. The topology cannot tell that shared edge
            // from a diagonal - which is exactly why a chord may not decide anything.
            var layer = new List<CadSegment>();
            layer.AddRange(LooseLoop(0, 0, 8000, 6000));
            layer.Add(new CadSegment(new CadPoint(8000, 0), new CadPoint(16000, 0), "M-DUCT"));
            layer.Add(new CadSegment(new CadPoint(16000, 0), new CadPoint(16000, 6000), "M-DUCT"));
            layer.Add(new CadSegment(new CadPoint(16000, 6000), new CadPoint(8000, 6000), "M-DUCT"));
            CadInterpretation read = CadInterpretationRules.Interpret(layer, Set(FigureRule), "h");
            Assert.Equal(7, Ducts(read).Count);
            Assert.DoesNotContain(read.Unclaimed, u => u.Reason == "closed_figure_by_declared_rule");
            Assert.Contains(read.Unclaimed, u => u.Reason == "closed_loop_held_for_review");
        }

        [Fact]
        public void A_circuit_with_a_diagonal_and_branches_keeps_its_branches_and_holds_the_circuit()
        {
            // case 3
            List<CadSegment> layer = LooseLoop(0, 0, 12000, 8000);
            layer.Add(new CadSegment(new CadPoint(0, 0), new CadPoint(12000, 8000), "M-DUCT"));   // the diagonal
            layer.Add(new CadSegment(new CadPoint(12000, 0), new CadPoint(18000, 0), "M-DUCT"));  // a branch
            layer.Add(new CadSegment(new CadPoint(0, 8000), new CadPoint(0, 14000), "M-DUCT"));   // another
            CadRings.CadLoop loop = Assert.Single(CadRings.Loops(layer, 25.4));
            Assert.Equal(2, loop.AttachedRuns);
            CadInterpretation read = CadInterpretationRules.Interpret(layer, Set(FigureRule), "h");
            Assert.Equal(7, Ducts(read).Count);
            Assert.Equal(2, Ducts(read).Count(c => c.EligibleForAutomaticApply));   // the two branches, built
            Assert.Equal(5, Ducts(read).Count(c => !c.EligibleForAutomaticApply));  // the circuit and its diagonal, held
        }

        [Fact]
        public void The_declared_figure_rule_is_what_excludes_the_M102_box_and_it_says_why()
        {
            // case 4 WITH the rule the project declares
            CadInterpretation read = CadInterpretationRules.Interpret(M102(), Set(FigureRule), "h");
            CadCandidate duct = Assert.Single(Ducts(read));
            Assert.True(duct.EligibleForAutomaticApply);
            Assert.Equal(37452.3, duct.Geometry.Min(p => p.X), 1);
            CadUnclaimed row = Assert.Single(read.Unclaimed, u => u.Reason == "closed_figure_by_declared_rule");
            Assert.Equal(6, row.EntityCount);
            Assert.Contains("perimeter at most 3000", row.Means);
            Assert.Contains("at least 2 chord(s)", row.Means);
            JObject loop = (JObject)((JArray)read.ClosedLoops[0]["loops"])[0];
            Assert.Equal("figure_by_declared_rule", loop.Value<string>("reading"));
            Assert.Equal("declared_rule", loop.Value<string>("settled_by"));
            Assert.True(loop["against_the_rule"].Value<bool>("matches"));
            Assert.Equal(2438.4, loop["against_the_rule"].Value<double>("perimeter_mm"), 1);
        }

        [Fact]
        public void A_loop_that_misses_one_declared_threshold_is_held_and_the_reply_names_the_one_it_missed()
        {
            // the M102 box drawn as loose lines: the rule asks for one closed polyline, so it does NOT match
            var loose = LooseLoop(0, 0, 610, 610);
            loose.Add(new CadSegment(new CadPoint(0, 0), new CadPoint(610, 610), "M-DUCT"));
            loose.Add(new CadSegment(new CadPoint(0, 610), new CadPoint(610, 0), "M-DUCT"));
            CadInterpretation read = CadInterpretationRules.Interpret(loose, Set(FigureRule), "h");
            Assert.DoesNotContain(read.Unclaimed, u => u.Reason == "closed_figure_by_declared_rule");
            JObject loop = (JObject)((JArray)read.ClosedLoops[0]["loops"])[0];
            Assert.Equal("held_for_review", loop.Value<string>("reading"));
            Assert.Contains("does not match", loop.Value<string>("settled_by"));
            Assert.False(loop["against_the_rule"].Value<bool>("matches"));
            Assert.True(loop["against_the_rule"].Value<bool>("perimeter_within"));      // it passed this one
            Assert.False(loop["against_the_rule"].Value<bool>("polyline_as_declared")); // and failed this one
        }

        // ------------------------------------------------- declaring runs grants nothing else

        [Fact]
        public void Declaring_that_these_loops_are_runs_gives_them_no_size()
        {
            List<CadSegment> layer = LooseLoop(0, 0, 12000, 8000);
            CadInterpretation read = CadInterpretationRules.Interpret(layer, Set(RunsRule), "h");
            List<CadCandidate> ducts = Ducts(read);
            Assert.Equal(4, ducts.Count);
            Assert.All(ducts, c => Assert.True(c.EligibleForAutomaticApply));
            Assert.All(ducts, c => Assert.Contains("size:", string.Join(" ", c.UnresolvedFacts)));
            JObject loop = (JObject)((JArray)read.ClosedLoops[0]["loops"])[0];
            Assert.Equal("run", loop.Value<string>("reading"));
            Assert.Contains("gives them no size", loop.Value<string>("and_still"));

            // and the section reading gives them none: no label beside any edge
            var runs = ducts.Select((c, i) => new CadRunSection
            {
                RunId = "r" + i, SemanticId = c.SemanticId, Start = c.Geometry[0], End = c.Geometry[1]
            }).ToList();
            var rule = new CadSectionRule
            {
                LabelLayers = { "M-TEXT" }, LabelUnits = "inch", LabelOrder = "width_x_height",
                MaxDistanceMm = 600, LeaderToleranceMm = 300
            };
            CadSectionReading section = CadDuctSections.Assign(runs, new List<CadLabel>(), new List<CadLeaderLine>(), rule, 25.4);
            Assert.All(section.Runs, r => Assert.Equal("missing", r.State));
        }

        [Fact]
        public void A_network_read_leaves_out_exactly_what_a_declared_figure_rule_leaves_out()
        {
            var ir = new CadIr();
            ir.Entities.Add(new CadIrEntity { Id = "e1", Layer = "M-DUCT", Kind = CadEntityKind.Polyline, Closed = true,
                                              Points = { TL, TR, BR, BL, TL } });
            ir.Entities.Add(new CadIrEntity { Id = "e2", Layer = "M-DUCT", Kind = CadEntityKind.Line, Points = { TL, BR } });
            ir.Entities.Add(new CadIrEntity { Id = "e3", Layer = "M-DUCT", Kind = CadEntityKind.Line, Points = { BL, TR } });
            ir.Entities.Add(new CadIrEntity { Id = "e4", Layer = "M-DUCT", Kind = CadEntityKind.Line,
                                              Points = { new CadPoint(39111.0, 29064.3), new CadPoint(37452.3, 29064.3) } });
            List<CadSegment> segs = ir.ToSegments();
            int leftOut, rings, chords;
            // no declared rule: the network keeps everything, exactly as the conversion proposes everything
            CadRings.WithoutOutlines(segs, Set(), out leftOut, out rings, out chords);
            Assert.Equal(0, leftOut);
            // with the rule: the figure and its chords go, the run stays
            List<CadSegment> kept = CadRings.WithoutOutlines(segs, Set(FigureRule), out leftOut, out rings, out chords);
            Assert.Equal(6, leftOut);
            Assert.Equal(1, rings);
            Assert.Equal(2, chords);
            CadSegment run = Assert.Single(kept);
            Assert.Equal(37452.3, run.B.X, 1);
        }

        // ------------------------------------------------------------------ the declaration itself

        [Fact]
        public void The_declaration_is_checked_and_refused_whole()
        {
            Assert.Throws<CadRequirementSetException>(() => Set(", 'closed_loops': 'symbols'"));
            Assert.Throws<CadRequirementSetException>(() => Set(", 'closed_loops': 'figures_by_rule'"));   // no closed_figure
            Assert.Throws<CadRequirementSetException>(() => Set(", 'closed_figure': { 'min_chords': 1 }")); // no policy
            Assert.Throws<CadRequirementSetException>(() =>
                Set(", 'closed_loops': 'figures_by_rule', 'closed_figure': { }"));                          // declares nothing
            Assert.Throws<CadRequirementSetException>(() =>
                Set(", 'closed_loops': 'figures_by_rule', 'closed_figure': { 'max_perimeter_mm': 0 }"));
            Assert.Throws<CadRequirementSetException>(() =>
                Set(", 'closed_loops': 'figures_by_rule', 'closed_figure': { 'nose': 1 }"));                // unknown key
            Assert.Throws<CadRequirementSetException>(() =>
                Set(", 'include_closed_polylines': true, 'closed_loops': 'review'"));                       // two answers
            // the old flag still means what it meant
            CadRequirementSet legacy = Set(", 'include_closed_polylines': true");
            Assert.Equal(CadClosedLoopPolicy.Runs, legacy.Rules[0].Geometry.ClosedLoops);
        }
    }
}
