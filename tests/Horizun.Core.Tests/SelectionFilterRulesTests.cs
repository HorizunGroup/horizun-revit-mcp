// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// create_selection_filter (horizun_manage_views): an empty set selects nothing, a repeated
// id hides a mistake, overrides with no view have nowhere to go, and a re-read whose members
// differ from the request is a failed write.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SelectionFilterRulesTests
    {
        [Theory]
        [InlineData(null)] [InlineData("")] [InlineData("  ")]
        public void A_filter_needs_a_name(string name) => Assert.NotNull(SelectionFilterRules.NameError(name));

        [Fact]
        public void A_name_with_a_forbidden_character_or_padding_is_refused_before_revit_does()
        {
            Assert.NotNull(SelectionFilterRules.NameError("Walls: east"));
            Assert.NotNull(SelectionFilterRules.NameError(" padded"));
            Assert.Null(SelectionFilterRules.NameError("Muros este"));
            Assert.Contains("255", SelectionFilterRules.NameError(new string('a', 256)));
        }

        [Fact]
        public void The_member_set_must_be_non_empty_unique_and_bounded()
        {
            Assert.Null(SelectionFilterRules.MembersError(new List<long> { 5, 6 }));
            Assert.Contains("at least one", SelectionFilterRules.MembersError(null));
            Assert.Contains("at least one", SelectionFilterRules.MembersError(new List<long>()));
            Assert.Contains("repeats", SelectionFilterRules.MembersError(new List<long> { 5, 5 }));
            var many = new List<long>(); for (long i = 1; i <= SelectionFilterRules.MaxElements + 1; i++) many.Add(i);
            Assert.Contains("more than", SelectionFilterRules.MembersError(many));
        }

        [Fact]
        public void Overrides_or_visibility_without_a_view_are_refused_not_ignored()
        {
            Assert.Null(SelectionFilterRules.OrphanFieldError(true, true, true));
            Assert.Null(SelectionFilterRules.OrphanFieldError(false, false, false));
            Assert.Contains("send view_id", SelectionFilterRules.OrphanFieldError(false, true, false));
            Assert.Contains("send view_id", SelectionFilterRules.OrphanFieldError(false, false, true));
        }

        [Fact]
        public void The_reread_must_hold_exactly_the_requested_members()
        {
            Assert.True(SelectionFilterRules.SameMembers(new long[] { 1, 2, 3 }, new long[] { 3, 2, 1 }));
            Assert.False(SelectionFilterRules.SameMembers(new long[] { 1, 2, 3 }, new long[] { 1, 2 }));
            Assert.False(SelectionFilterRules.SameMembers(new long[] { 1, 2 }, new long[] { 1, 2, 9 }));
            Assert.False(SelectionFilterRules.SameMembers(null, new long[] { 1 }));
        }
    }
}
