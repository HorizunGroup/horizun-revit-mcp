using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RehearsalCycle3ArqVerificationTests
    {
        [Fact]
        public void Window_solid_crossing_below_its_level_is_not_within_storey()
        {
            // C3 GFX40_2: solid 5.219–15.833 ft, P1 base 8.5 ft.
            Assert.False(LevelSpanRules.WithinStorey(5.219 * 304.8, 15.833 * 304.8,
                8.5 * 304.8, 20 * 304.8));
            Assert.True(LevelSpanRules.WithinStorey(8.5 * 304.8, 15.833 * 304.8,
                8.5 * 304.8, 20 * 304.8));
        }

        [Fact]
        public void Warning_cause_must_be_classified_per_occurrence_not_union()
        {
            ElementBoxFact Box(long id, double x) => new ElementBoxFact
            {
                Id = id, Category = "Walls", TypeId = 7,
                MinX = x, MaxX = x + 1000, MinY = 0, MaxY = 200, MinZ = 0, MaxZ = 3000
            };
            var pair1 = new[] { Box(1, 0), Box(2, 0) };
            var pair2 = new[] { Box(3, 2000), Box(4, 2000) };
            Assert.Equal(WarningRootCauseCauses.Unclassified,
                WarningRootCauseRules.Classify(pair1.Concat(pair2).ToList()).Cause);
            Assert.Equal(2, new[] { pair1, pair2 }.Count(pair =>
                WarningRootCauseRules.Classify(pair).Cause == WarningRootCauseCauses.ExactDuplicate));
        }
    }
}
