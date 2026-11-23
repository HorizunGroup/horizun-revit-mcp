using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RailingHostLevelTests
    {
        private static System.Func<string, LevelProbe> Fake(Dictionary<string, LevelProbe> answers) =>
            source => answers.TryGetValue(source, out LevelProbe p) ? p : LevelProbe.Absent();

        [Fact]
        public void A_railing_on_a_stair_takes_its_floor_from_the_host_instead_of_no_level()
        {
            // Field case: stair-hosted railings read Base Level -1, so Element.LevelId and every
            // parameter named no level and 110 railings counted as "(no level)".
            var answers = new Dictionary<string, LevelProbe>
            {
                [LevelResolutionRules.ElementLevelId] = LevelProbe.NoLevel(),
                ["LEVEL_PARAM"] = LevelProbe.NoLevel(),
                [LevelResolutionRules.RailingHostElement] = LevelProbe.Found("PISO-02")
            };
            LevelResolution r = LevelResolutionRules.Resolve(Fake(answers));
            Assert.True(r.Resolved);
            Assert.Equal("PISO-02", r.LevelName);
            Assert.Equal(LevelResolutionRules.RailingHostElement, r.Source);
        }

        [Fact]
        public void The_railings_own_level_still_wins_when_it_has_one()
        {
            var answers = new Dictionary<string, LevelProbe>
            {
                [LevelResolutionRules.ElementLevelId] = LevelProbe.Found("PISO-01"),
                [LevelResolutionRules.RailingHostElement] = LevelProbe.Found("PISO-02")
            };
            Assert.Equal("PISO-01", LevelResolutionRules.Resolve(Fake(answers)).LevelName);
        }

        [Fact]
        public void The_host_source_is_asked_after_the_placement_sources_and_before_the_reporting_levels()
        {
            var order = LevelResolutionRules.Sources.ToList();
            Assert.Contains(LevelResolutionRules.RailingHostElement, order);
            Assert.True(order.IndexOf("INSTANCE_REFERENCE_LEVEL_PARAM") < order.IndexOf(LevelResolutionRules.RailingHostElement));
            Assert.True(order.IndexOf(LevelResolutionRules.HostLevel) < order.IndexOf(LevelResolutionRules.RailingHostElement));
            Assert.True(order.IndexOf(LevelResolutionRules.RailingHostElement) < order.IndexOf("SCHEDULE_LEVEL_PARAM"));
            Assert.Equal(order.Count, order.Distinct().Count());
        }

        [Fact]
        public void A_railing_with_no_host_and_no_level_is_still_reported_as_no_level()
        {
            var answers = new Dictionary<string, LevelProbe>
            {
                [LevelResolutionRules.ElementLevelId] = LevelProbe.NoLevel(),
                [LevelResolutionRules.RailingHostElement] = LevelProbe.Absent()
            };
            LevelResolution r = LevelResolutionRules.Resolve(Fake(answers));
            Assert.False(r.Resolved);
            Assert.Equal(LevelResolutionRules.NoSourceLabel, LevelResolutionRules.SourceKey(r));
        }
    }
}
