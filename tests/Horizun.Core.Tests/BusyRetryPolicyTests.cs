using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class BusyRetryPolicyTests
    {
        [Theory]
        [InlineData(1, 250)]
        [InlineData(2, 500)]
        [InlineData(3, 1000)]
        [InlineData(4, 2000)]
        [InlineData(5, 2000)]
        public void Backoff_is_exponential_and_capped(int attempt, int expected)
        {
            Assert.Equal(expected, BusyRetryPolicy.DelayForAttempt(attempt, 250, 2000));
        }

        [Fact]
        public void The_cap_applies_to_the_first_delay_too()
        {
            Assert.Equal(25, BusyRetryPolicy.DelayForAttempt(1, 5000, 25));
        }

        [Fact]
        public void Retry_budget_and_attempt_cap_are_both_enforced()
        {
            Assert.True(BusyRetryPolicy.TryGetNextDelay(0, 0, 2, 250, 2000, 750, out int first));
            Assert.Equal(250, first);
            Assert.True(BusyRetryPolicy.TryGetNextDelay(1, 250, 2, 250, 2000, 750, out int second));
            Assert.Equal(500, second);
            Assert.False(BusyRetryPolicy.TryGetNextDelay(2, 750, 2, 250, 2000, 750, out _));
            Assert.False(BusyRetryPolicy.TryGetNextDelay(0, 0, 4, 250, 2000, 200, out _));
        }

        [Fact]
        public void Disabled_or_invalid_elapsed_budget_does_not_schedule()
        {
            Assert.False(BusyRetryPolicy.TryGetNextDelay(0, 0, 0, 250, 2000, 5000, out _));
            Assert.False(BusyRetryPolicy.TryGetNextDelay(0, -1, 4, 250, 2000, 5000, out _));
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public void Resignal_requires_an_unstarted_request_and_no_modal(bool started, bool modal, bool expected)
        {
            Assert.Equal(expected, BusyRetryPolicy.CanResignal(started, modal));
        }
    }
}
