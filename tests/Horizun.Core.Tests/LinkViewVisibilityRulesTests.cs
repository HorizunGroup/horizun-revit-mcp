using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LinkViewVisibilityRulesTests
    {
        [Fact]
        public void UnhidingALinkTypeKeepsOtherHiddenInstancesHidden()
        {
            // Type T has instances 10, 11, 12; the caller asks to show 10; 11 was hidden (by type), 12 was visible.
            var keep = LinkViewVisibilityRules.InstancesToKeepHidden(
                new long[] { 10, 11, 12 }, new HashSet<long> { 10 }, new HashSet<long> { 10, 11 });
            Assert.Equal(new long[] { 11 }, keep);
        }

        [Fact]
        public void EveryRequestedInstanceIsShownEvenIfHiddenBefore()
        {
            var keep = LinkViewVisibilityRules.InstancesToKeepHidden(
                new long[] { 10, 11 }, new HashSet<long> { 10, 11 }, new HashSet<long> { 10, 11 });
            Assert.Empty(keep);
        }

        [Fact]
        public void NoInstancesNothingToKeepHidden()
        {
            Assert.Empty(LinkViewVisibilityRules.InstancesToKeepHidden(null, null, null));
        }

        [Theory]
        [InlineData(0, 50, 1)]     // 0 over a category transparency: Revit's nearest is 1
        [InlineData(0, 0, null)]   // nothing inherited: 0 is written as asked
        [InlineData(30, 50, null)] // any non-zero value overrides the category as asked
        [InlineData(1, 50, null)]
        public void TransparencyZeroOverACategoryTransparencyIsWrittenAsOne(int asked, int inherited, int? written)
        {
            Assert.Equal(written, LinkViewVisibilityRules.OpaqueOverInheritedTransparency(asked, inherited));
        }
    }
}
