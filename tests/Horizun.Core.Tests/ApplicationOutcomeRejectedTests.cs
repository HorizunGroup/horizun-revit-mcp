using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Field 2026-11-09: a set_curve rehearsal refused whole (0 applied) declared "partial: some of what was requested
    /// landed". A dry run that refuses is rejected: nothing written, no confirmation.
    /// </summary>
    public class ApplicationOutcomeRejectedTests
    {
        [Theory]
        [InlineData(1, 0)]
        [InlineData(0, 1)]
        [InlineData(3, 3)]
        public void A_refused_rehearsal_is_rejected_not_partial(int unresolved, int failed)
        {
            Assert.Equal(ApplicationState.Rejected, ApplicationOutcome.Rehearsal(unresolved, failed, 0));
            var block = ApplicationOutcome.DeclareRehearsal(1, unresolved, failed, 0);
            Assert.Equal("rejected", (string)block["state"]);
            Assert.Equal(0, (int)block["applied"]);
            Assert.False((bool)block["fully_applied"]);
            Assert.DoesNotContain("landed", (string)block["state_means"]);
            Assert.Contains("NOTHING was written", (string)block["state_means"]);
        }

        [Fact]
        public void Rejected_reads_back_and_opens_no_gate()
        {
            var payload = new Newtonsoft.Json.Linq.JObject();
            ApplicationOutcome.StampRehearsal(payload, 1, 1, 0, 0);
            Assert.Equal(ApplicationState.Rejected, ApplicationOutcome.Read(payload));
            Assert.False(ApplicationOutcome.IsFullyApplied(ApplicationState.Rejected));
            Assert.False(ApplicationOutcome.IsValidRehearsal(ApplicationState.Rejected));
            Assert.True(ApplicationOutcome.TryParse("rejected", out ApplicationState parsed));
            Assert.Equal(ApplicationState.Rejected, parsed);
        }

        [Fact]
        public void A_clean_rehearsal_and_an_unmeasured_one_are_unchanged()
        {
            Assert.Equal(ApplicationState.Rehearsed, ApplicationOutcome.Rehearsal(0, 0, 0));
            Assert.Equal(ApplicationState.Uncertain, ApplicationOutcome.Rehearsal(1, 1, 1));
        }
    }
}
