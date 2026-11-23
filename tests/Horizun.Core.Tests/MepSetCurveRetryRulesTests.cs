// horizun_transform_elements set_curve on MEP runs: when a rolled-back attempt is tried once more.
// Field report: extending a branch right after its elbow was deleted failed now and then with
// "can no longer keep the connectivity"; the same call in a new request passed.
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MepSetCurveRetryRulesTests
    {
        private static readonly string[] SetCurve = { "set_curve" };

        [Fact]
        public void A_rolled_back_set_curve_on_mep_runs_is_tried_once_more()
            => Assert.True(MepSetCurveRetryRules.ShouldRetry(1, SetCurve, allTargetsAreMepCurves: true, rolledBack: true));

        [Fact]
        public void Only_once()
            => Assert.False(MepSetCurveRetryRules.ShouldRetry(2, SetCurve, true, true));

        [Fact]
        public void Never_when_the_rollback_is_not_confirmed()
            // a second write over a model whose first write may have landed is a second write
            => Assert.False(MepSetCurveRetryRules.ShouldRetry(1, SetCurve, true, rolledBack: false));

        [Fact]
        public void Never_for_walls_or_other_operations()
        {
            Assert.False(MepSetCurveRetryRules.ShouldRetry(1, SetCurve, allTargetsAreMepCurves: false, rolledBack: true));
            Assert.False(MepSetCurveRetryRules.ShouldRetry(1, new[] { "set_curve", "move" }, true, true));
            Assert.False(MepSetCurveRetryRules.ShouldRetry(1, new string[0], true, true));
        }

        [Theory]
        [InlineData("The family is connected in a network and can no longer keep the connectivity.", true)]
        [InlineData("Atomic transform failed: Revit returned RolledBack", false)]
        [InlineData("La familia está conectada en una red y ya no puede mantener la conectividad.", true)]
        public void The_connectivity_refusal_is_recognised_by_its_words(string text, bool expected)
            => Assert.Equal(expected, MepSetCurveRetryRules.IsConnectivityRefusal(text));

        [Theory]
        [InlineData(null)]
        [InlineData("Die Familie ist in einem Netzwerk verbunden und kann die Konnektivität nicht mehr halten.")]
        public void A_text_in_another_language_or_none_is_unknown_not_false(string text)
            => Assert.Null(MepSetCurveRetryRules.IsConnectivityRefusal(text));

        [Fact]
        public void A_retried_success_says_what_the_first_attempt_met()
        {
            var note = MepSetCurveRetryRules.RetryNote("can no longer keep the connectivity");
            Assert.Contains("second attempt", note);
            Assert.Contains("keep the connectivity", note);
        }
    }
}
