using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WorksetConfigurationEvidenceTests
    {
        private static WorksetOpenObservation W(string name, bool open)
            => new WorksetOpenObservation { Name = name, IsOpen = open };

        [Fact]
        public void Requested_closed_workset_and_every_other_open_is_verified()
        {
            var v = WorksetConfigurationEvidence.Verify(new[] { "Shell" }, false,
                new[] { W("Shell", false), W("Interiors", true) });

            Assert.True(v.Requested);
            Assert.True(v.Applied);
            Assert.Null(v.Error);
            Assert.Equal(new[] { "Shell" }, v.ClosedNames);
        }

        [Fact]
        public void Echoing_the_request_cannot_hide_that_requested_workset_is_open()
        {
            var v = WorksetConfigurationEvidence.Verify(new[] { "Shell" }, false,
                new[] { W("Shell", true), W("Interiors", true) });

            Assert.False(v.Applied);
            Assert.Contains("is OPEN", v.Error);
        }

        [Fact]
        public void A_second_unrequested_closed_workset_means_the_plan_did_not_land_exactly()
        {
            var v = WorksetConfigurationEvidence.Verify(new[] { "Shell" }, false,
                new[] { W("Shell", false), W("Interiors", false) });

            Assert.False(v.Applied);
            Assert.Contains("unrequested", v.Error);
        }

        [Fact]
        public void Open_all_is_proved_from_observations_not_from_the_flag()
        {
            var bad = WorksetConfigurationEvidence.Verify(new List<string>(), true,
                new[] { W("Shell", true), W("Interiors", false) });
            var good = WorksetConfigurationEvidence.Verify(new List<string>(), true,
                new[] { W("Shell", true), W("Interiors", true) });

            Assert.False(bad.Applied);
            Assert.Contains("still closed", bad.Error);
            Assert.True(good.Applied);
        }

        [Fact]
        public void Unreadable_measurement_never_becomes_applied()
        {
            var v = WorksetConfigurationEvidence.Unreadable(true, "collector failed");

            Assert.True(v.Requested);
            Assert.False(v.Applied);
            Assert.Contains("collector failed", v.Error);
        }
    }
}
