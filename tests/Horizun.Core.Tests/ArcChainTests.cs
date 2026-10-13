using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ArcChainTests
    {
        // A circular hoop as the anonymized regression case plan details it (HOOP-A: circle d .32 closed by a .25 overlap, 135 degree hooks):
        // three arcs 0 -> 180 -> 360 degrees and on round the lap. An earlier generator built exactly that chain with
        // Rebar.CreateFromCurves on Revit 2024 (128 hoops, read back). It is OPEN - the lap runs past its own start -
        // and it was refused here by a rule written from closed circles.
        [Fact]
        public void An_open_chain_of_arcs_lapping_a_circle_is_not_refused()
        {
            Assert.Null(RebarArcs.ChainRefusal(closed: false, vertexCount: 4, arcCount: 3));
        }

        [Fact]
        public void A_single_arc_between_two_points_is_not_refused()
        {
            Assert.Null(RebarArcs.ChainRefusal(closed: false, vertexCount: 2, arcCount: 1));
        }

        [Fact]
        public void A_chain_without_arcs_is_none_of_this_rules_business()
        {
            Assert.Null(RebarArcs.ChainRefusal(closed: true, vertexCount: 4, arcCount: 0));
            Assert.Null(RebarArcs.ChainRefusal(closed: false, vertexCount: 6, arcCount: 0));
        }

        [Fact]
        public void A_closed_chain_with_an_arc_is_still_refused_by_name()
        {
            string why = RebarArcs.ChainRefusal(closed: true, vertexCount: 2, arcCount: 2);
            Assert.NotNull(why);
            Assert.Contains("closed", why);
        }
    }
}
