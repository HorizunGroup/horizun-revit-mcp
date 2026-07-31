using System;
using System.Threading;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// Shutdown drains on a deadline it DERIVES from the work outstanding, not on a
    /// number somebody picked.
    ///
    /// The first version of the drain waited a flat 120 seconds after stdin closed.
    /// That number was wrong in both directions at once: far too long for a
    /// host-resident call that answered in 3 ms, and too SHORT for a model scan
    /// entitled to the full ten-minute command budget - so the arbitrary constant
    /// could discard exactly the answer the drain existed to protect. Each request
    /// now carries the instant by which it must have answered, and the drain runs
    /// until the latest of those, then stops for a reason.
    /// </summary>
    public class ShutdownDrainTests
    {
        private static string Ignore;

        [Fact]
        public void Nothing_outstanding_means_no_deadline_and_no_wait()
        {
            var f = new InFlight();
            Assert.Null(f.DrainDeadlineUtc());
        }

        [Fact]
        public void The_deadline_is_the_LATEST_of_the_outstanding_requests()
        {
            var f = new InFlight();
            var shortCts = new CancellationTokenSource();
            var longCts = new CancellationTokenSource();

            Assert.True(f.TryStart("a", "horizun_target", shortCts, out Ignore, 1000));
            Assert.True(f.TryStart("b", "horizun_model_scan", longCts, out Ignore, 600000));

            DateTime? deadline = f.DrainDeadlineUtc();
            Assert.True(deadline.HasValue);

            // Draining to the SHORT one would abandon the scan mid-flight. The wait has
            // to cover the longest thing still owed an answer.
            double secondsOut = (deadline.Value - DateTime.UtcNow).TotalSeconds;
            Assert.True(secondsOut > 300,
                "The drain deadline must cover the LONGEST outstanding request, not the shortest. " +
                "Got " + (int)secondsOut + " s out.");
        }

        [Fact]
        public void Finishing_the_long_one_pulls_the_deadline_back_in()
        {
            var f = new InFlight();
            Assert.True(f.TryStart("a", "horizun_target", new CancellationTokenSource(), out Ignore, 1000));
            Assert.True(f.TryStart("b", "horizun_model_scan", new CancellationTokenSource(), out Ignore, 600000));

            f.Finish("b");

            DateTime? deadline = f.DrainDeadlineUtc();
            Assert.True(deadline.HasValue);

            // With the scan answered, there is no reason to keep the process alive for
            // its budget - which is why the drain loop re-reads this rather than holding
            // the first answer it got.
            double secondsOut = (deadline.Value - DateTime.UtcNow).TotalSeconds;
            Assert.True(secondsOut < 60,
                "Once the long request has answered the deadline must come back in. Got " +
                (int)secondsOut + " s out.");
        }

        [Fact]
        public void A_request_with_no_stated_deadline_does_not_hold_shutdown_open()
        {
            var f = new InFlight();
            Assert.True(f.TryStart("a", "something", new CancellationTokenSource(), out Ignore));

            DateTime? deadline = f.DrainDeadlineUtc();
            Assert.True(deadline.HasValue);
            Assert.True(deadline.Value <= DateTime.UtcNow.AddSeconds(1),
                "A request that never stated a deadline must not be treated as entitled to an unbounded one.");
        }
    }
}
