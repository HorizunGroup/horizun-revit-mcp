// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L8: steel in a member of a model group placed more than once. MEASURED by
// an external pipeline on real models: rebar created in a member of such a group
// disappeared at the commit with no error (one group placed 9 times, groups nested);
// only a re-read of every created id caught it. Refused before any write, by name.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GroupHostRuleTests
    {
        [Fact]
        public void A_host_in_a_group_placed_several_times_is_refused_with_the_count() =>
            Assert.Contains("9", GroupHostRule.Refusal(1234, "Typical bay", 9));

        [Fact]
        public void A_host_in_a_group_placed_once_is_not_refused_here() =>
            Assert.Null(GroupHostRule.Refusal(1234, "Typical bay", 1));

        [Fact]
        public void A_host_in_no_group_is_not_refused() =>
            Assert.Null(GroupHostRule.Refusal(-1, null, 0));

        [Fact]
        public void The_refusal_names_the_way_out() =>
            Assert.Contains("ungroup", GroupHostRule.Refusal(1234, "Typical bay", 2));
    }
}
