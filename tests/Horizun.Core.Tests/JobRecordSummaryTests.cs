// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The job-record folding the ribbon's Jobs dialog stands on. The input is the
// hostile kind: a JSONL file a killed process may have left mid-append. The
// rules under proof are the honest ones - "running" is never claimed from a
// file alone, and a broken line is a skipped line, not an exception.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class JobRecordSummaryTests
    {
        [Fact]
        public void A_record_with_no_running_event_is_queued()
        {
            var summary = JobRecordSummary.FromLines(new[]
            {
                "{\"event\":\"start\",\"at\":1}"
            });
            Assert.Equal("queued", summary.State);
            Assert.Null(summary.FinishStatus);
            Assert.False(summary.Failed);
        }

        [Fact]
        public void Running_without_finish_is_running_or_died_never_plain_running()
        {
            var summary = JobRecordSummary.FromLines(new[]
            {
                "{\"event\":\"start\",\"at\":1}",
                "{\"event\":\"running\",\"at\":2}",
                "{\"event\":\"checkpoint\",\"at\":3}"
            });
            Assert.Equal("running_or_died", summary.State);
        }

        [Fact]
        public void A_finish_event_carries_its_status_and_decides_failure()
        {
            var ok = JobRecordSummary.FromLines(new[]
            {
                "{\"event\":\"running\",\"at\":1}",
                "{\"event\":\"finish\",\"status\":\"ok\"}"
            });
            Assert.Equal("finished", ok.State);
            Assert.False(ok.Failed);

            var failed = JobRecordSummary.FromLines(new[]
            {
                "{\"event\":\"running\",\"at\":1}",
                "{\"event\":\"finish\",\"status\":\"error\"}"
            });
            Assert.Equal("finished", failed.State);
            Assert.True(failed.Failed);
        }

        [Fact]
        public void The_half_written_last_line_of_a_killed_process_is_skipped_not_fatal()
        {
            var summary = JobRecordSummary.FromLines(new[]
            {
                "{\"event\":\"running\",\"at\":1}",
                "{\"event\":\"chec"   // the process died here
            });
            Assert.Equal("running_or_died", summary.State);
        }

        [Fact]
        public void Empty_and_null_inputs_are_the_queued_state_not_an_exception()
        {
            Assert.Equal("queued", JobRecordSummary.FromLines(new string[0]).State);
            Assert.Equal("queued", JobRecordSummary.FromLines(null).State);
            Assert.Equal("queued", JobRecordSummary.FromLines(new[] { "", "  ", "not json at all" }).State);
        }

        [Fact]
        public void A_finish_after_running_wins_and_a_second_finish_updates_the_status()
        {
            // A retried job may append twice; the LAST finish is the record's word.
            var summary = JobRecordSummary.FromLines(new[]
            {
                "{\"event\":\"running\"}",
                "{\"event\":\"finish\",\"status\":\"error\"}",
                "{\"event\":\"finish\",\"status\":\"ok\"}"
            });
            Assert.Equal("finished", summary.State);
            Assert.False(summary.Failed);
        }

        // ---- the liveness-aware fold: the ambiguity resolves only when it CAN ----

        private static readonly string[] RunningWithPid =
        {
            "{\"event\":\"start\",\"tool\":\"t\",\"pid\":4242}",
            "{\"event\":\"running\"}"
        };

        [Fact]
        public void A_running_record_whose_process_is_alive_reads_running()
        {
            var summary = JobRecordSummary.FromLines(RunningWithPid, pid => pid == 4242);
            Assert.Equal("running", summary.State);
        }

        [Fact]
        public void A_running_record_whose_process_died_reads_interrupted()
        {
            var summary = JobRecordSummary.FromLines(RunningWithPid, pid => false);
            Assert.Equal("interrupted", summary.State);
        }

        [Fact]
        public void No_pid_keeps_the_honest_ambiguity_even_with_a_delegate()
        {
            var summary = JobRecordSummary.FromLines(new[]
            {
                "{\"event\":\"start\",\"tool\":\"t\"}",
                "{\"event\":\"running\"}"
            }, pid => true);
            Assert.Equal("running_or_died", summary.State);
        }

        [Fact]
        public void A_throwing_os_check_keeps_the_ambiguity_rather_than_guessing()
        {
            var summary = JobRecordSummary.FromLines(RunningWithPid,
                pid => throw new System.InvalidOperationException("os said no"));
            Assert.Equal("running_or_died", summary.State);
        }

        [Fact]
        public void A_finished_record_never_consults_the_os()
        {
            bool asked = false;
            var summary = JobRecordSummary.FromLines(new[]
            {
                "{\"event\":\"start\",\"tool\":\"t\",\"pid\":1}",
                "{\"event\":\"running\"}",
                "{\"event\":\"finish\",\"status\":\"ok\"}"
            }, pid => { asked = true; return false; });
            Assert.Equal("finished", summary.State);
            Assert.False(asked);
        }
    }
}
