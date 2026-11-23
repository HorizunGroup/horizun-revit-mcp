using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TransportProgressTests
    {
        [Fact]
        public void Cooperative_cancellation_points_to_the_job_without_claiming_completion()
        {
            var status = new JObject { ["state"] = "executing", ["progress"] = new JObject
                { ["job_id"] = "job-7", ["last_checkpoint"] = "clash:sweep", ["checkpoint_count"] = 3 } };
            var notice = TransportProgress.CancellationNotice(status);
            Assert.Equal("cancel_requested", (string)notice["status"]);
            Assert.Equal("job-7", (string)notice["progress"]["job_id"]);
            Assert.False((bool)notice["host_verified"]);
            Assert.Contains("job-7", TransportProgress.Sentence(notice));
            Assert.Contains("terminal state", (string)notice["means"]);
        }

        [Fact] public void Timeout_keeps_progress_without_claiming_commits()
        {
            var observed = JObject.Parse(@"{'state':'executing','progress':{'job_id':'job-a','checkpoint_count':2,'last_checkpoint':'step 2','host_verified':false}}");
            var result = TransportProgress.Notice(observed,false);
            Assert.Equal("job-a",(string)result["progress"]["job_id"]);
            Assert.False((bool)result["host_verified"]);
            Assert.Contains("step 2",TransportProgress.Sentence(result));
            observed["progress"]["checkpoint_count"] = 3;
            Assert.Equal(2,(int)result["progress"]["checkpoint_count"]);
        }
        [Fact] public void A_cancelled_queued_request_cannot_inherit_progress()
        {
            var result=TransportProgress.Notice(JObject.Parse(@"{'state':'queued','progress':{'job_id':'other'}}"),true);
            Assert.Null((object)result["progress"]?.Value<string>());
            Assert.Contains("never started",(string)result["means"]);
        }
        [Fact] public void Unavailable_progress_is_explicit() => Assert.Contains("No progress measurement",TransportProgress.Sentence(TransportProgress.Notice(null,false)));
            [Fact] public void Timeout_notice_names_the_job_how_to_poll_and_the_elapsed_time()
        {
            var observed = JObject.Parse(@"{'state':'executing','progress':{'job_id':'job-9','checkpoint_count':4,'last_checkpoint':'model 4','last_done':4,'last_total':10}}");
            var n = TransportProgress.Notice(observed, false, 600000);
            Assert.Equal("job-9", (string)n["job_id"]);
            Assert.Equal("horizun_job_status", (string)n["poll_with"]["tool"]);
            Assert.Equal("job-9", (string)n["poll_with"]["arguments"]["job_id"]);
            Assert.Equal(600, (int)n["elapsed_s"]);
            Assert.Equal(900, (int)n["estimate"]["remaining_s_approx"]);   // 600 s for 4 of 10 => 900 s for the other 6
            string sentence = TransportProgress.Sentence(n);
            Assert.Contains("horizun_job_status job_id=job-9", sentence);
            Assert.Contains("elapsed 600 s", sentence);
            Assert.Contains("about 900 s left", sentence);
        }

        [Fact] public void Timeout_notice_without_done_and_total_gives_no_estimate_rather_than_a_guess()
        {
            var observed = JObject.Parse(@"{'state':'executing','progress':{'job_id':'job-9','checkpoint_count':4,'last_checkpoint':'x','last_done':null,'last_total':null}}");
            var n = TransportProgress.Notice(observed, false, 60000);
            Assert.Null(n["estimate"]);
            Assert.Equal("job-9", (string)n["job_id"]);
            Assert.DoesNotContain("estimate", TransportProgress.Sentence(n));
        }

        [Theory]
        [InlineData(100, 0, 10)]     // nothing done yet
        [InlineData(100, 5, 0)]      // no total
        [InlineData(100, 12, 10)]    // done beyond total
        [InlineData(0, 5, 10)]       // no elapsed time
        public void Estimate_refuses_inputs_it_cannot_extrapolate(double elapsed, double done, double total)
            => Assert.Null(TransportProgress.Estimate(elapsed, done, total));

        [Fact] public void A_request_that_never_started_carries_no_job_or_poll_instruction()
        {
            var n = TransportProgress.Notice(JObject.Parse(@"{'state':'queued','progress':{'job_id':'other'}}"), true, 5000);
            Assert.Null(n["job_id"]); Assert.Null(n["poll_with"]); Assert.Null(n["elapsed_s"]);
        }
    }
}
