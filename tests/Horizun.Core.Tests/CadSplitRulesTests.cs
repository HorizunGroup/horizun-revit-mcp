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
