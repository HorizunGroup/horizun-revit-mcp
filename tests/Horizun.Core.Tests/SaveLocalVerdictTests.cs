using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// save_document save_local: the outcome from the measurements. Field report: a cloud
    /// workshared model answered verification_unknown on every save; IsModified true -> false
    /// is now evidence when the bytes cannot be measured.
    /// </summary>
    public sealed class SaveLocalVerdictTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Changed_bytes_are_saved_verified(bool cloud)
        {
            SaveLocalOutcome v = SaveLocalVerdict.Decide(true, true, false, cloud);
            Assert.Equal(SaveLocalVerdict.SavedVerified, v.Outcome);
            Assert.True(v.Saved);
            Assert.Equal("bytes", v.EvidenceStrength);
            Assert.NotNull(v.FlagEvidence);   // the flag agrees and is said
        }

        [Fact]
        public void Cloud_model_without_a_measurable_copy_and_the_flag_cleared_is_saved_by_revit_state()
        {
            SaveLocalOutcome v = SaveLocalVerdict.Decide(null, true, false, cloud: true);
            Assert.Equal(SaveLocalVerdict.SavedByRevitState, v.Outcome);
            Assert.True(v.Saved);
            Assert.Equal("revit_flag", v.EvidenceStrength);
            Assert.Contains("cloud model", v.Means);
            Assert.Contains("weaker than a byte comparison", v.Means);
            Assert.Contains("never a synchronize", v.Means);
        }

        [Theory]
        [InlineData(true, true)]     // flag stayed set
        [InlineData(null, false)]    // before unreadable
        [InlineData(true, null)]     // after unreadable
        [InlineData(false, false)]   // nothing was pending: still unmeasured
        public void Without_bytes_and_without_a_cleared_flag_it_stays_unknown(bool? before, bool? after)
        {
            SaveLocalOutcome v = SaveLocalVerdict.Decide(null, before, after, cloud: true);
            Assert.Equal(SaveLocalVerdict.VerificationUnknown, v.Outcome);
            Assert.False(v.Saved);
            Assert.Equal("none", v.EvidenceStrength);
        }

        [Fact]
        public void Unchanged_bytes_with_nothing_pending_is_nothing_to_save()
        {
            SaveLocalOutcome v = SaveLocalVerdict.Decide(false, false, false, cloud: false);
            Assert.Equal(SaveLocalVerdict.NothingToSave, v.Outcome);
            Assert.False(v.Saved);
        }

        [Fact]
        public void Unchanged_local_file_with_pending_changes_is_a_failure_even_if_the_flag_cleared()
        {
            Assert.Equal(SaveLocalVerdict.VerificationFailed, SaveLocalVerdict.Decide(false, true, false, cloud: false).Outcome);
            Assert.Equal(SaveLocalVerdict.VerificationFailed, SaveLocalVerdict.Decide(false, true, true, cloud: false).Outcome);
        }

        [Fact]
        public void Cloud_cache_copy_unchanged_while_the_flag_cleared_is_conflicting_and_unknown()
        {
            SaveLocalOutcome v = SaveLocalVerdict.Decide(false, true, false, cloud: true);
            Assert.Equal(SaveLocalVerdict.VerificationUnknown, v.Outcome);
            Assert.False(v.Saved);
            Assert.Contains("Conflicting evidence", v.Means);
            // Flag stayed set and the copy did not move: a failure, as for a local file.
            Assert.Equal(SaveLocalVerdict.VerificationFailed, SaveLocalVerdict.Decide(false, true, true, cloud: true).Outcome);
        }
    }
}
