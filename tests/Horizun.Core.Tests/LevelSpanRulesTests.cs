using System;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LevelSpanRulesTests
    {
        [Fact]
        public void CrossesOnlyWhenBothSidesExceedTolerance()
        {
            var levels = new[] { 0d, 3000d, 6000d };
            Assert.Empty(LevelSpanRules.Crossed(0, 3050, levels));
            Assert.Equal(new[] { 3000d }, LevelSpanRules.Crossed(0, 3050.1, levels));
            Assert.Equal(new[] { 3000d, 6000d }, LevelSpanRules.Crossed(0, 6100, levels));
            Assert.Empty(LevelSpanRules.Crossed(3000, 6000, levels));
        }

        [Fact]
        public void RefusesInvalidIntervals()
        {
            Assert.Throws<ArgumentException>(() => LevelSpanRules.Crossed(3, 2, new[] { 1d }));
            Assert.Throws<ArgumentException>(() => LevelSpanRules.Crossed(0, 2, new[] { 1d }, -1));
        }
    }
}
