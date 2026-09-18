// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHERE THE DEPENDENTS OF A SPLIT WALL GO.
//
// MEASURED (revision C, campaign 4): wall 858827 comes back as a 161.9 mm stretch and a
// 177.8 mm stretch; it hosts one device on the first and three on the second. The split
// was held because a device stood outside the piece that would keep the id. These pin
// the classification that lets an unambiguous case go ahead and keeps the rest held.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadSplitRulesTests
    {
        private static List<CadSplitPiece> Pieces(bool keepSecond) => new List<CadSplitPiece>
        {
            new CadSplitPiece { CandidateId = "A", Lo = 0, Hi = 337, ThicknessMm = 161.9, KeepsTheElement = !keepSecond },
            new CadSplitPiece { CandidateId = "B", Lo = 337, Hi = 1362, ThicknessMm = 177.8, KeepsTheElement = keepSecond }
        };

        private static CadSplitDependent Dep(long id, double lo, double hi, bool recreatable = true) =>
            new CadSplitDependent { ElementId = id, Category = "Data Devices", Lo = lo, Hi = hi, Recreatable = recreatable };

        [Fact]
        public void Each_dependent_goes_to_the_one_piece_that_holds_it()
        {
            var deps = new List<CadSplitDependent> { Dep(1, 200, 240), Dep(2, 400, 440), Dep(3, 900, 940) };
            CadSplitRules.Classify(Pieces(true), deps, 1.0);
            Assert.Equal(CadSplitRules.MovesTo, deps[0].Class);
            Assert.Equal("A", deps[0].TargetCandidateId);
            Assert.Equal(CadSplitRules.Stays, deps[1].Class);
            Assert.Equal(CadSplitRules.Stays, deps[2].Class);
        }

        [Fact]
        public void A_dependent_across_a_boundary_or_in_the_removed_stretch_is_held_with_alternatives()
        {
            var pieces = Pieces(true);
            pieces[1].Lo = 400;          // a gap from 337 to 400
            var deps = new List<CadSplitDependent> { Dep(1, 320, 360), Dep(2, 350, 390), Dep(3, 380, 420) };
            CadSplitRules.Classify(pieces, deps, 1.0);
            Assert.Equal(CadSplitRules.Ambiguous, deps[0].Class);      // across A's end into the gap
            Assert.Equal(CadSplitRules.InGap, deps[1].Class);
            Assert.Contains(deps[1].Alternatives, a => a.StartsWith("delete it"));
            Assert.Equal(CadSplitRules.Ambiguous, deps[2].Class);      // across B's start
            Assert.All(deps, d => Assert.NotEmpty(d.Alternatives));
        }

        [Fact]
        public void A_class_this_build_cannot_recreate_is_held_when_it_would_have_to_move()
        {
            var deps = new List<CadSplitDependent> { Dep(1, 200, 240, recreatable: false), Dep(2, 900, 940, false) };
            CadSplitRules.Classify(Pieces(true), deps, 1.0);
            Assert.Equal(CadSplitRules.Unsupported, deps[0].Class);
            Assert.Equal(CadSplitRules.Stays, deps[1].Class);          // it does not move: nothing to re-create
        }

        // ---- decisions on held dependents (campaign 5) --------------------------------
        private static List<CadSplitPiece> Gapped() => new List<CadSplitPiece>
        {
            new CadSplitPiece { CandidateId = "K", Lo = 0, Hi = 1000, KeepsTheElement = true },
            new CadSplitPiece { CandidateId = "N", Lo = 1500, Hi = 3000 }
        };

        [Fact]
        public void A_held_dependent_is_completed_only_by_a_decision_that_quotes_its_key()
        {
            var deps = new List<CadSplitDependent> { Dep(1, 1100, 1200), Dep(2, 980, 1520), Dep(3, 2000, 2040), Dep(4, 200, 240) };
            List<CadSplitPiece> pieces = Gapped();
            CadSplitRules.Classify(pieces, deps, 1.0);
            Assert.Equal(CadSplitRules.InGap, deps[0].Class);
            Assert.Equal(CadSplitRules.Ambiguous, deps[1].Class);
            // first pass, no decision: every HELD dependent gets its key, the others none
            Assert.Empty(CadSplitRules.ApplyDecisions(pieces, deps, null, "DOC|set:1", 77, 1.0));
            Assert.NotNull(deps[0].DecisionKey);
            Assert.Null(deps[2].DecisionKey);
            Assert.Null(deps[3].DecisionKey);

            var fresh = new List<CadSplitDependent> { Dep(1, 1100, 1200), Dep(2, 900, 960), Dep(5, 1100, 1300) };
            CadSplitRules.Classify(pieces, fresh, 1.0);
            string k1 = CadSplitRules.DecisionKey("DOC|set:1", 77, fresh[0], pieces);
            string k5 = CadSplitRules.DecisionKey("DOC|set:1", 77, fresh[2], pieces);
            var decisions = new List<CadDependentDecision>
            {
                new CadDependentDecision { ElementId = 1, Decision = "move_to", Piece = "N", Key = k1 },
                new CadDependentDecision { ElementId = 5, Decision = "stay", Key = k5 }
            };
            Assert.Empty(CadSplitRules.ApplyDecisions(pieces, fresh, decisions, "DOC|set:1", 77, 1.0));
            // onto the new piece, clamped, moved as little as that takes: centre 1150 -> 1551
            Assert.Equal(CadSplitRules.MovesTo, fresh[0].Class);
            Assert.Equal("N", fresh[0].TargetCandidateId);
            Assert.Equal(401.0, fresh[0].MoveAlongMm.Value, 1);
            Assert.Equal("in_gap", fresh[0].DecidedFrom);
            // stay: onto the KEPT piece, clamped back inside it
            Assert.True(fresh[2].TargetIsKept);
            Assert.Equal(-301.0, fresh[2].MoveAlongMm.Value, 1);
        }

        [Fact]
        public void A_decision_for_another_plan_or_another_piece_is_refused_not_skipped()
        {
            List<CadSplitPiece> pieces = Gapped();
            var deps = new List<CadSplitDependent> { Dep(1, 1100, 1200), Dep(2, 1100, 1200) };
            CadSplitRules.Classify(pieces, deps, 1.0);
            string otherDoc = CadSplitRules.DecisionKey("OTHER|set:1", 77, deps[0], pieces);
            string ok2 = CadSplitRules.DecisionKey("DOC|set:1", 77, deps[1], pieces);
            List<string> problems = CadSplitRules.ApplyDecisions(pieces, deps, new List<CadDependentDecision>
            {
                new CadDependentDecision { ElementId = 1, Decision = "delete", Key = otherDoc },
                new CadDependentDecision { ElementId = 2, Decision = "move_to", Piece = "Z", Key = ok2 }
            }, "DOC|set:1", 77, 1.0);
            Assert.Contains(problems, p => p.StartsWith("stale_decision: element 1"));
            Assert.Contains(problems, p => p.StartsWith("unknown_piece: element 2"));
            Assert.Equal(CadSplitRules.InGap, deps[0].Class);          // nothing applied to it

            // a revision that moved a piece gives the same dependent another key
            var moved = Gapped();
            moved[1].Lo = 1600;
            Assert.NotEqual(CadSplitRules.DecisionKey("DOC|set:1", 77, deps[1], pieces),
                            CadSplitRules.DecisionKey("DOC|set:1", 77, deps[1], moved));
        }

        [Fact]
        public void Delete_is_a_decision_and_a_piece_too_short_is_refused()
        {
            var pieces = new List<CadSplitPiece>
            {
                new CadSplitPiece { CandidateId = "K", Lo = 0, Hi = 1000, KeepsTheElement = true },
                new CadSplitPiece { CandidateId = "S", Lo = 1500, Hi = 1530 }
            };
            var deps = new List<CadSplitDependent> { Dep(1, 1100, 1200), Dep(2, 1250, 1350) };
            CadSplitRules.Classify(pieces, deps, 1.0);
            List<string> problems = CadSplitRules.ApplyDecisions(pieces, deps, new List<CadDependentDecision>
            {
                new CadDependentDecision { ElementId = 1, Decision = "delete", Key = CadSplitRules.DecisionKey("D", 9, deps[0], pieces) },
                new CadDependentDecision { ElementId = 2, Decision = "move_to", Piece = "S", Key = CadSplitRules.DecisionKey("D", 9, deps[1], pieces) }
            }, "D", 9, 1.0);
            Assert.True(deps[0].Delete);
            Assert.Contains(problems, p => p.StartsWith("piece_too_short: element 2"));
        }

        [Fact]
        public void The_piece_with_most_dependents_keeps_the_element_then_its_width_then_its_length()
        {
            var deps = new List<CadSplitDependent> { Dep(1, 200, 240), Dep(2, 400, 440), Dep(3, 900, 940), Dep(4, 1000, 1040) };
            Assert.Equal("B", CadSplitRules.RecommendKeep(Pieces(true), deps, 161.9, 3.2, 1.0).CandidateId);
            // no dependents: the element's own width wins over length
            Assert.Equal("A", CadSplitRules.RecommendKeep(Pieces(true), new List<CadSplitDependent>(), 161.9, 3.2, 1.0).CandidateId);
            // no width known either: the longest
            var unknown = Pieces(true);
            unknown.ForEach(p => p.ThicknessMm = null);
            Assert.Equal("B", CadSplitRules.RecommendKeep(unknown, new List<CadSplitDependent>(), 161.9, 3.2, 1.0).CandidateId);
        }
    }
}
