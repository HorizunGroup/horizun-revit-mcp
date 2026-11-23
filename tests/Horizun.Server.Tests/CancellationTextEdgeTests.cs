using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>Second review of int/4.2, m-6: the cancellation sentences in the edge cases say what is true.</summary>
    public class CancellationTextEdgeTests
    {
        private static PipeClient.CancelAttempt Attempt(string state) => new PipeClient.CancelAttempt { State = state };

        [Fact]
        public void A_cancelled_script_is_not_called_a_cooperative_job()
        {
            string py = PipeClient.CancellationUncertainty("horizun_execute_python", Attempt("cancel_requested"));
            Assert.DoesNotContain("cooperative", py);
            Assert.Contains("script", py);
            Assert.Contains("checkpoint()", py);
            string plan = PipeClient.CancellationUncertainty("horizun_execute_plan", Attempt("cancel_requested"));
            Assert.DoesNotContain("cooperative", plan);
            // the real cooperative job keeps its sentence
        }

        [Fact]
        public void A_script_that_already_ended_gets_the_finished_sentence()
        {
            string text = PipeClient.CancellationUncertainty("horizun_execute_python", Attempt("not_found_or_finished"));
            Assert.DoesNotContain("cooperative", text);
            Assert.Contains("finished", text);
        }

        [Fact]
        public void A_job_that_continues_without_a_job_record_cannot_be_followed_or_cancelled()
        {
            string withId = PipeClient.CancellationUncertainty("horizun_clash", Attempt("job_continues"), followable: true);
            Assert.Contains("horizun_job_status", withId);
            string noId = PipeClient.CancellationUncertainty("horizun_clash", Attempt("job_continues"), followable: false);
            Assert.DoesNotContain("by its job_id with horizun_job_status", noId);
            Assert.Contains("job_id unavailable", noId);
            Assert.Contains("cannot be followed", noId);
            Assert.Contains("cancelled from here", noId);
        }
    }
}
