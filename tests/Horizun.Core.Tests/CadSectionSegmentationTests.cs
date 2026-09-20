// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ONE DRAWN RUN, TWO SIZES: where it is cut, and where it is not.
//
// Measured on a real corridor plan (campaign 7): straight single-line mains of 5-9 m carry
// "8X8" and further along "8X6", with a drawn 11 in transition piece at each end and, on one
// of them, a branch tapping the main. These pin that a cut is made only at a drawn event, that
// both ends are determined by their nearest labels, that the stretch between labels without an
// event stays unsized, and that pieces are named by what anchors them - never by their order.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadSectionSegmentationTests
    {
        private static CadSectionRule Rule(bool split = true) => new CadSectionRule
        {
            LabelLayers = { "M-TEXT" }, LabelUnits = "inch", LabelOrder = "width_x_height",
            MaxDistanceMm = 600, LeaderToleranceMm = 300, SplitAtSectionChanges = split, TapToleranceMm = 80
        };

        private static CadRunSection Run(string id, double x1, double y1, double x2, double y2) =>
            new CadRunSection { RunId = id, SemanticId = "s-" + id, Start = new CadPoint(x1, y1), End = new CadPoint(x2, y2) };

        private static CadLabel Label(string id, string text, double x, double y) =>
            new CadLabel { Id = id, Text = text, Layer = "M-TEXT", At = new CadPoint(x, y), RotationRadians = 0 };

        private static CadSectionReading Read(List<CadRunSection> runs, params CadLabel[] labels) =>
            CadDuctSections.Assign(runs, labels.ToList(), new List<CadLeaderLine>(), Rule(), 25.4);

        private static void Contiguous(IEnumerable<CadRunSection> pieces, double x0, double x1)
        {
            var xs = pieces.Select(p => new[] { System.Math.Min(p.Start.X, p.End.X), System.Math.Max(p.Start.X, p.End.X) })
                           .OrderBy(a => a[0]).ToList();
            Assert.Equal(x0, xs.First()[0], 3);
            Assert.Equal(x1, xs.Last()[1], 3);
            for (int i = 1; i < xs.Count; i++) Assert.Equal(xs[i - 1][1], xs[i][0], 3);   // no gap, no overlap
        }

        [Fact]
        public void Two_sizes_and_nothing_drawn_between_them_give_two_determined_ends_and_one_unlocated_stretch()
        {
            var r = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0) },
                         Label("A", "8X8", 2000, 200), Label("B", "8X6", 7000, 200));
            var p = r.Runs.Where(x => x.ParentRunId == "m").ToList();
            Assert.Equal(3, p.Count);
            Contiguous(p, 0, 10000);
            var lo = p.Single(x => x.PieceKey == "end:lo");
            var hi = p.Single(x => x.PieceKey == "end:hi");
            var mid = p.Single(x => x.PieceKey == "between:A|B");
            Assert.Equal("documented", lo.State); Assert.Equal(203.2, lo.WidthMm.Value, 3); Assert.Equal(203.2, lo.HeightMm.Value, 3);
            Assert.Equal("documented", hi.State); Assert.Equal(152.4, hi.HeightMm.Value, 3);
            Assert.Equal("change_unlocated", mid.State); Assert.Null(mid.WidthMm);
            Assert.Equal(2000, mid.Start.X, 3); Assert.Equal(7000, mid.End.X, 3);    // bounded by the labels, never a midpoint
            Assert.Contains("draws nothing between them", mid.Reason);
        }

        [Fact]
        public void A_single_branch_between_the_labels_is_where_the_run_is_cut()
        {
            var r = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0), Run("br", 4000, -60, 4000, -2000) },
                         Label("A", "8X8", 2000, 200), Label("B", "8X6", 7000, 200));
            var p = r.Runs.Where(x => x.ParentRunId == "m").ToList();
            Assert.Equal(2, p.Count);
            Contiguous(p, 0, 10000);
            Assert.Equal(4000, p.Single(x => x.PieceKey == "end:lo").End.X, 3);
            Assert.Contains("the branch br tapping the run", p.Single(x => x.PieceKey == "end:lo").CutReason);
            Assert.DoesNotContain(r.Runs, x => x.State == "change_unlocated");
        }

        [Fact]
        public void Two_branches_between_the_labels_leave_the_change_unlocated()
        {
            var r = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0), Run("b1", 3000, -60, 3000, -2000),
                                                   Run("b2", 5000, -60, 5000, -2000) },
                         Label("A", "8X8", 2000, 200), Label("B", "8X6", 7000, 200));
            var mid = r.Runs.Single(x => x.State == "change_unlocated");
            Assert.Contains("2 branches tap between them", mid.Reason);
        }

        [Fact]
        public void A_branch_outside_the_labels_does_not_place_the_change()
        {
            // M106 polyline 375: the tap lies beyond both labels - it cannot be the change between them.
            var r = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0), Run("br", 8800, -60, 8800, -2000) },
                         Label("A", "10X8", 3500, 200), Label("B", "10X6", 7900, 200));
            Assert.Single(r.Runs, x => x.State == "change_unlocated");
            Assert.Equal(10000, r.Runs.Single(x => x.PieceKey == "end:hi").End.X, 3);
        }

        [Fact]
        public void Reversing_the_drawn_direction_names_the_same_pieces_with_the_same_lines()
        {
            var fwd = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0) }, Label("A", "8X8", 2000, 200), Label("B", "8X6", 7000, 200));
            var rev = Read(new List<CadRunSection> { Run("m", 10000, 0, 0, 0) }, Label("A", "8X8", 2000, 200), Label("B", "8X6", 7000, 200));
            foreach (string key in new[] { "end:lo", "end:hi", "between:A|B" })
            {
                var a = fwd.Runs.Single(x => x.PieceKey == key);
                var b = rev.Runs.Single(x => x.PieceKey == key);
                Assert.Equal(System.Math.Min(a.Start.X, a.End.X), System.Math.Min(b.Start.X, b.End.X), 3);
                Assert.Equal(System.Math.Max(a.Start.X, a.End.X), System.Math.Max(b.Start.X, b.End.X), 3);
                Assert.Equal(a.WidthMm, b.WidthMm);
            }
        }

        [Fact]
        public void A_label_moved_along_keeps_the_piece_names_and_moves_only_the_bound()
        {
            var before = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0) }, Label("A", "8X8", 2000, 200), Label("B", "8X6", 7000, 200));
            var after = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0) }, Label("A", "8X8", 2600, 200), Label("B", "8X6", 7000, 200));
            Assert.Equal(before.Runs.Select(x => x.PieceKey).OrderBy(k => k), after.Runs.Select(x => x.PieceKey).OrderBy(k => k));
            Assert.Equal(2600, after.Runs.Single(x => x.PieceKey == "end:lo").End.X, 3);
        }

        [Fact]
        public void A_text_change_resizes_a_piece_and_a_revision_adds_or_removes_the_division()
        {
            var one = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0) }, Label("A", "8X8", 2000, 200), Label("B", "8X8", 7000, 200));
            Assert.Single(one.Runs);
            Assert.Equal("documented", one.Runs[0].State);
            Assert.Null(one.Runs[0].ParentRunId);                          // one size: not cut
            var two = Read(new List<CadRunSection> { Run("m", 0, 0, 10000, 0) }, Label("A", "8X8", 2000, 200), Label("B", "10X6", 7000, 200));
            Assert.Equal(3, two.Runs.Count);                               // the revision added a division
            Assert.Equal(254.0, two.Runs.Single(x => x.PieceKey == "end:hi").WidthMm.Value, 3);
        }

        [Fact]
        public void Without_the_declaration_nothing_is_cut()
        {
            var r = CadDuctSections.Assign(new List<CadRunSection> { Run("m", 0, 0, 10000, 0) },
                new List<CadLabel> { Label("A", "8X8", 2000, 200), Label("B", "8X6", 7000, 200) },
                new List<CadLeaderLine>(), Rule(split: false), 25.4);
            Assert.Single(r.Runs);
            Assert.Equal("contradictory", r.Runs[0].State);
        }

        [Fact]
        public void The_transition_is_the_piece_straight_at_both_ends_whichever_side_the_loop_meets_first()
        {
            // MEASURED on M106 (campaign 7): 8X6 run, 243 mm drawn transition, 290 mm unlabelled run, elbow,
            // 6X6 labelled run. Propagating in loop order gave the transition 8X6.
            foreach (bool reversed in new[] { false, true })
            {
                var runs = new List<CadRunSection>
                {
                    Run("a", 0, 0, 5000, 0), Run("t", 5000, 0, 5243, 0), Run("m", 5243, 0, 5533, 0),
                    Run("c", 5533, 0, 5533, 3000)
                };
                if (reversed) runs.Reverse();
                var r = Read(runs, Label("A", "8X6", 2500, 200), Label("C", "6X6", 5733, 1500));
                Assert.Equal("transition", r.Runs.Single(x => x.RunId == "t").State);
                Assert.NotNull(r.Runs.Single(x => x.RunId == "t").TransitionEnds);
                Assert.Equal("propagated", r.Runs.Single(x => x.RunId == "m").State);
                Assert.Equal(152.4, r.Runs.Single(x => x.RunId == "m").WidthMm.Value, 3);
            }
        }

        [Fact]
        public void Two_straight_pieces_between_two_sizes_leave_the_whole_chain_unsized()
        {
            var runs = new List<CadRunSection>
            {
                Run("a", 0, 0, 5000, 0), Run("p", 5000, 0, 5250, 0), Run("q", 5250, 0, 5500, 0), Run("b", 5500, 0, 9000, 0)
            };
            var r = Read(runs, Label("A", "12X8", 2500, 200), Label("B", "8X8", 7000, 200));
            Assert.Equal("missing", r.Runs.Single(x => x.RunId == "p").State);
            Assert.Equal("missing", r.Runs.Single(x => x.RunId == "q").State);
            Assert.Contains("2 straight pieces could carry it", r.Runs.Single(x => x.RunId == "p").Reason);
        }

        [Fact]
        public void The_drawn_transitions_at_both_ends_get_both_sizes()
        {
            // 12X8 main, 280 mm drawn transition, the two-size run, 250 mm transition, 6X6.
            var runs = new List<CadRunSection>
            {
                Run("a", 0, 0, 5000, 0), Run("t1", 5000, 0, 5280, 0), Run("m", 5280, 0, 15000, 0),
                Run("t2", 15000, 0, 15250, 0), Run("c", 15250, 0, 20000, 0)
            };
            var r = Read(runs, Label("L0", "12X8", 2500, 200), Label("A", "8X8", 6000, 200), Label("B", "8X6", 12000, 200),
                         Label("L3", "6X6", 17000, 200));
            var t1 = r.Runs.Single(x => x.RunId == "t1");
            var t2 = r.Runs.Single(x => x.RunId == "t2");
            Assert.Equal("transition", t1.State);
            Assert.Equal("transition", t2.State);
            Assert.NotNull(t1.TransitionEnds);
            Assert.NotNull(t2.TransitionEnds);
            var sizes1 = new[] { t1.TransitionEnds["a"].Value<double>("width_mm"), t1.TransitionEnds["b"].Value<double>("width_mm") }.OrderBy(v => v).ToArray();
            Assert.Equal(new[] { 203.2, 304.8 }, sizes1);
            var h2 = new[] { t2.TransitionEnds["a"].Value<double>("height_mm"), t2.TransitionEnds["b"].Value<double>("height_mm") };
            Assert.All(h2, h => Assert.Equal(152.4, h, 3));
            Assert.Equal(280.0, t1.TransitionEnds.Value<double>("drawn_length_mm"), 1);
        }
    }
}
