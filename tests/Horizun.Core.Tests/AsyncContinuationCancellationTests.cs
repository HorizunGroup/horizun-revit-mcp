using Horizun.Revit.Core;
using Xunit;
using System.Threading.Tasks;

namespace Horizun.Core.Tests
{
    [Collection("AsyncQueue")]
    public class AsyncContinuationCancellationTests
    {
        [Fact]
        public void Cooperative_job_without_id_is_not_admitted()
        {
            Assert.False(AsyncQueue.TryAdd(new AsyncWork { CooperativeCancellation = true }, out string reason));
            Assert.Contains("id", reason);
        }

        [Fact]
        public void Active_cooperative_cancellation_survives_until_terminal_decision()
        {
            while (AsyncQueue.Take() != null) { }
            var work = new AsyncWork { JobId = "active-cancel", CooperativeCancellation = true };
            Assert.True(AsyncQueue.TryAdd(work, out _));
            Assert.Same(work, AsyncQueue.Take());
            work.HasStarted = true;
            var cancellation = AsyncQueue.CancelWaiting(work.JobId);
            Assert.True(cancellation.Requested);
            Assert.False(cancellation.Cancelled);
            Assert.Equal("cancel_requested", cancellation.State);
            Assert.True(work.CancellationRequested);
            Assert.False(AsyncQueue.TryRequeueContinuation(work, out _));
            Assert.True(AsyncQueue.CompleteCooperative(work));
            Assert.Equal("not_in_queue", AsyncQueue.CancelWaiting(work.JobId).State);
        }

        [Fact]
        public void Yield_transfers_cancellation_ownership_back_to_the_queue()
        {
            while (AsyncQueue.Take() != null) { }
            var work = new AsyncWork { JobId = "yield-cancel", CooperativeCancellation = true };
            Assert.True(AsyncQueue.TryAdd(work, out _));
            Assert.Same(work, AsyncQueue.Take());
            work.HasStarted = true;
            Assert.True(AsyncQueue.TryRequeueContinuation(work, out _));
            var cancellation = AsyncQueue.CancelWaiting(work.JobId);
            Assert.Same(work, cancellation.Work);
            Assert.Equal("cancelled_after_start", cancellation.State);
            Assert.False(cancellation.Requested);
        }

        [Fact]
        public async Task Cancellation_racing_a_yield_is_never_lost()
        {
            while (AsyncQueue.Take() != null) { }
            for (int i = 0; i < 100; i++)
            {
                var work = new AsyncWork { JobId = "race-" + i, CooperativeCancellation = true };
                Assert.True(AsyncQueue.TryAdd(work, out _));
                Assert.Same(work, AsyncQueue.Take());
                work.HasStarted = true;
                var cancel = Task.Run(() => AsyncQueue.CancelWaiting(work.JobId));
                bool requeued = AsyncQueue.TryRequeueContinuation(work, out _);
                var answer = await cancel;
                if (requeued)
                {
                    Assert.Same(work, answer.Work);
                    Assert.False(answer.Requested);
                }
                else
                {
                    Assert.True(answer.Requested);
                    Assert.True(AsyncQueue.CompleteCooperative(work));
                }
            }
        }

        [Fact]
        public void A_started_continuation_can_yield_to_a_full_queue_once()
        {
            while (AsyncQueue.Take() != null) { }
            var work = new AsyncWork { JobId = "yielded", HasStarted = true };
            for (int i = 0; i < AsyncQueue.MaxDepth; i++)
                Assert.True(AsyncQueue.TryAdd(new AsyncWork { JobId = "waiting-" + i }, out _));
            Assert.True(AsyncQueue.TryRequeueContinuation(work, out _));
            Assert.False(AsyncQueue.TryRequeueContinuation(work, out _));
            Assert.False(AsyncQueue.TryAdd(new AsyncWork { JobId = "new" }, out _));
            for (int i = 0; i < AsyncQueue.MaxDepth; i++)
                Assert.Equal("waiting-" + i, AsyncQueue.Take().JobId);
            Assert.Same(work, AsyncQueue.Take());
            Assert.Null(AsyncQueue.Take());
        }

        [Fact]
        public void Removing_a_yielded_job_does_not_claim_it_never_started()
        {
            while (AsyncQueue.Take() != null) { }
            var work = new AsyncWork { JobId = "yielded", HasStarted = true };
            Assert.True(AsyncQueue.TryAdd(work, out _));
            var outcome = AsyncQueue.CancelWaiting("yielded");
            Assert.True(outcome.Cancelled);
            Assert.False(outcome.BeforeStart);
            Assert.Equal("cancelled_after_start", outcome.State);
            Assert.Null(AsyncQueue.Take());
        }
    }
}
