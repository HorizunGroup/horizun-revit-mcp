// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Draw order of 2D detail (horizun_detail_2d operation=draw_order). The judgement a re-read of
// the stack (back to front, the whole detail set of the view) must pass: "in front" is a
// statement about the OTHER elements, an arrange move never shuffles the ones it did not move,
// and a move that changed nothing is legitimate only when the elements already sat at that end.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DrawOrderRulesTests
    {
        private static readonly long[] Stack = { 1, 2, 3, 4, 5 };

        [Theory]
        [InlineData("front", "front")] [InlineData(" BACK ", "back")] [InlineData("Forward", "forward")] [InlineData("backward", "backward")]
        public void The_four_moves_are_parsed(string word, string order) => Assert.Equal(order, DrawOrderRules.Parse(word));

        [Theory]
        [InlineData("top")] [InlineData("")] [InlineData(null)]
        public void Any_other_word_is_not_a_move(string word) => Assert.Null(DrawOrderRules.Parse(word));

        [Fact]
        public void The_members_must_be_non_empty_unique_and_bounded()
        {
            Assert.Null(DrawOrderRules.MembersError(new List<long> { 1 }));
            Assert.NotNull(DrawOrderRules.MembersError(new List<long>()));
            Assert.Contains("repeats", DrawOrderRules.MembersError(new List<long> { 1, 1 }));
        }

        [Fact]
        public void Bring_to_front_puts_the_moved_last_and_keeps_everyone_elses_order()
            => Assert.Null(DrawOrderRules.Verify("front", Stack, new long[] { 1, 3, 4, 5, 2 }, new HashSet<long> { 2 }));

        [Fact]
        public void Bring_to_front_fails_when_the_moved_element_is_not_last()
            => Assert.Contains("frontmost", DrawOrderRules.Verify("front", Stack, new long[] { 1, 2, 3, 5, 4 }, new HashSet<long> { 2 }));

        [Fact]
        public void Send_to_back_puts_the_moved_first_and_fails_otherwise()
        {
            Assert.Null(DrawOrderRules.Verify("back", Stack, new long[] { 4, 1, 2, 3, 5 }, new HashSet<long> { 4 }));
            Assert.Contains("rearmost", DrawOrderRules.Verify("back", Stack, new long[] { 1, 4, 2, 3, 5 }, new HashSet<long> { 4 }));
        }

        [Fact]
        public void Two_moved_elements_keep_their_own_order_at_the_front()
        {
            Assert.Null(DrawOrderRules.Verify("front", Stack, new long[] { 2, 4, 5, 1, 3 }, new HashSet<long> { 1, 3 }));
            Assert.Contains("own relative order", DrawOrderRules.Verify("front", Stack, new long[] { 2, 4, 5, 3, 1 }, new HashSet<long> { 1, 3 }));
        }

        [Fact]
        public void Elements_that_were_not_moved_must_not_be_shuffled()
            => Assert.Contains("not moved", DrawOrderRules.Verify("front", Stack, new long[] { 3, 1, 4, 5, 2 }, new HashSet<long> { 2 }));

        [Fact]
        public void Bring_forward_moves_toward_the_front_and_never_back()
        {
            Assert.Null(DrawOrderRules.Verify("forward", Stack, new long[] { 1, 3, 2, 4, 5 }, new HashSet<long> { 2 }));
            Assert.Contains("toward the front", DrawOrderRules.Verify("forward", Stack, new long[] { 2, 1, 3, 4, 5 }, new HashSet<long> { 2 }));
        }

        [Fact]
        public void Send_backward_moves_toward_the_back_and_never_forward()
        {
            Assert.Null(DrawOrderRules.Verify("backward", Stack, new long[] { 1, 2, 4, 3, 5 }, new HashSet<long> { 4 }));
            Assert.Contains("toward the back", DrawOrderRules.Verify("backward", Stack, new long[] { 1, 2, 3, 5, 4 }, new HashSet<long> { 4 }));
        }

        [Fact]
        public void A_move_that_changed_nothing_is_legitimate_only_when_already_at_that_end()
        {
            Assert.Null(DrawOrderRules.Verify("front", Stack, Stack, new HashSet<long> { 5 }));
            Assert.Null(DrawOrderRules.Verify("forward", Stack, Stack, new HashSet<long> { 5 }));
            Assert.Null(DrawOrderRules.Verify("backward", Stack, Stack, new HashSet<long> { 1 }));
            Assert.NotNull(DrawOrderRules.Verify("forward", Stack, Stack, new HashSet<long> { 2 }));
            Assert.NotNull(DrawOrderRules.Verify("backward", Stack, Stack, new HashSet<long> { 3 }));
            Assert.NotNull(DrawOrderRules.Verify("front", Stack, Stack, new HashSet<long> { 2 }));
        }

        [Fact]
        public void A_stack_whose_membership_changed_cannot_be_judged_ok()
        {
            Assert.Contains("changed during the move", DrawOrderRules.Verify("front", Stack, new long[] { 1, 2, 3, 4 }, new HashSet<long> { 2 }));
            Assert.Contains("changed during the move", DrawOrderRules.Verify("front", Stack, new long[] { 1, 2, 3, 4, 9 }, new HashSet<long> { 2 }));
            Assert.Contains("could not be read", DrawOrderRules.Verify("front", null, Stack, new HashSet<long> { 2 }));
        }

        [Fact]
        public void An_unknown_order_is_refused_and_changed_reports_a_difference()
        {
            Assert.NotNull(DrawOrderRules.Verify("sideways", Stack, Stack, new HashSet<long> { 2 }));
            Assert.False(DrawOrderRules.Changed(Stack, Stack));
            Assert.True(DrawOrderRules.Changed(Stack, new long[] { 2, 1, 3, 4, 5 }));
        }
    }
}
