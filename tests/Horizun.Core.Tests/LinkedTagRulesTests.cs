using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LinkedTagRulesTests
    {
        [Fact]
        public void A_host_element_alone_is_a_local_tag()
        {
            Assert.Null(LinkedTagRules.Check(true, false, false, out bool linked));
            Assert.False(linked);
        }

        [Fact]
        public void The_link_pair_alone_is_a_linked_tag()
        {
            Assert.Null(LinkedTagRules.Check(false, true, true, out bool linked));
            Assert.True(linked);
        }

        [Fact]
        public void Both_targets_at_once_are_refused()
        {
            string error = LinkedTagRules.Check(true, true, true, out bool linked);
            Assert.True(linked);
            Assert.Contains("two targets", error);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Half_of_the_link_pair_is_refused_naming_the_missing_half(bool hasInstance, bool hasLinked)
        {
            string error = LinkedTagRules.Check(false, hasInstance, hasLinked, out bool linked);
            Assert.True(linked);
            Assert.Contains("BOTH", error);
            Assert.Contains(hasInstance ? "only link_instance_id" : "only linked_element_id", error);
        }

        [Fact]
        public void No_target_at_all_is_refused()
        {
            string error = LinkedTagRules.Check(false, false, false, out bool linked);
            Assert.False(linked);
            Assert.Contains("element_id", error);
        }

        [Fact]
        public void The_committed_tag_must_point_at_exactly_the_asked_pair()
        {
            var one = new[] { new KeyValuePair<long, long>(10, 99) };
            Assert.True(LinkedTagRules.PointsAt(one, 10, 99));
            Assert.True(LinkedTagRules.PointsOnlyAt(one, 10, 99));
            Assert.False(LinkedTagRules.PointsOnlyAt(one, 10, 98));
            Assert.False(LinkedTagRules.PointsOnlyAt(one, 11, 99));
            var two = new[] { new KeyValuePair<long, long>(10, 99), new KeyValuePair<long, long>(10, 100) };
            Assert.True(LinkedTagRules.PointsAt(two, 10, 100));
            Assert.False(LinkedTagRules.PointsOnlyAt(two, 10, 99));
            Assert.False(LinkedTagRules.PointsAt(null, 10, 99));
            Assert.False(LinkedTagRules.PointsOnlyAt(null, 10, 99));
        }

        [Fact]
        public void The_plan_block_names_the_link_target()
        {
            var block = LinkedTagRules.Describe(10, "ARQ.rvt", 99, "LP3_ARQ", "Doors");
            Assert.Equal("link", (string)block["source"]);
            Assert.Equal(10, (long)block["link_instance_id"]);
            Assert.Equal(99, (long)block["linked_element_id"]);
            Assert.Equal("LP3_ARQ", (string)block["linked_document"]);
        }
    }
}
