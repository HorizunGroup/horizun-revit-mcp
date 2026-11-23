using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CommandDeadlinePolicyTests
    {
        [Theory]
        [InlineData("horizun_health", 5000)]
        [InlineData("horizun_navigate", 15000)]
        [InlineData("horizun_open_document", 600000)]
        [InlineData("horizun_execute_python", 600000)]
        public void Diagnostic_and_navigation_waits_are_bounded_independently(string command, int expected)
            => Assert.Equal(expected, CommandDeadlinePolicy.WaitMs(command, 600000));

        [Fact]
        public void Opening_a_file_has_a_short_queue_wait_without_truncating_its_real_work()
        {
            Assert.Equal(1, CommandDeadlinePolicy.RemainingMs("horizun_open_document", 600000, 19999, false));
            Assert.Equal(0, CommandDeadlinePolicy.RemainingMs("horizun_open_document", 600000, 20000, false));
            Assert.Equal(580000, CommandDeadlinePolicy.RemainingMs("horizun_open_document", 600000, 20000, true));
        }

        [Fact]
        public void A_running_python_script_is_handed_off_at_45_seconds_but_not_while_it_waits_in_the_queue()
        {
            Assert.Equal(45000, CommandDeadlinePolicy.RemainingMs("horizun_execute_python", 600000, 0, true));
            Assert.Equal(0, CommandDeadlinePolicy.RemainingMs("horizun_execute_python", 600000, 45000, true));
            Assert.Equal(560000, CommandDeadlinePolicy.RemainingMs("horizun_execute_python", 600000, 40000, false));
            Assert.Equal(10000, CommandDeadlinePolicy.RemainingMs("horizun_execute_python", 10000, 0, true));
            Assert.Equal(555000, CommandDeadlinePolicy.RemainingMs("horizun_query_model", 600000, 45000, true));
            Assert.True(CommandDeadlinePolicy.HandsOff("horizun_execute_python"));
            Assert.False(CommandDeadlinePolicy.HandsOff("horizun_submit_job"));
        }

        [Fact]
        public void A_running_export_is_handed_off_at_45_seconds_and_says_it_is_an_export()
        {
            // Field 2026-11 (5.0.2): a 16-sheet combined PDF held its client the whole 600 s.
            Assert.True(CommandDeadlinePolicy.HandsOff("horizun_export"));
            Assert.Equal(0, CommandDeadlinePolicy.RemainingMs("horizun_export", 600000, 45000, true));
            Assert.Equal(560000, CommandDeadlinePolicy.RemainingMs("horizun_export", 600000, 40000, false));
            Assert.False(CommandDeadlinePolicy.HandsOff("horizun_manage_groups"));
            var p = PythonHandoff.Payload("job-2", 45001, 0, null, null, "horizun_export");
            Assert.Equal("running", (string)p["state"]);
            Assert.False((bool)p["completed"]);
            Assert.Equal("horizun_job_status job_id=job-2", (string)p["follow_with"]);
            Assert.StartsWith("The export is STILL RUNNING", (string)p["what_this_means"]);
            Assert.Contains("Do NOT send the export again", (string)p["what_this_means"]);
            Assert.DoesNotContain("script", (string)p["what_this_means"]);
        }

        [Fact]
        public void The_handoff_says_it_is_not_the_result_and_names_the_job_to_follow()
        {
            var p = PythonHandoff.Payload("job-1", 45012, 3, "modelo 2 de 5", null);
            Assert.Equal("running", (string)p["state"]);
            Assert.False((bool)p["completed"]);
            Assert.False((bool)p["host_verified"]);
            Assert.Equal("job-1", (string)p["job_id"]);
            Assert.Equal("horizun_job_status job_id=job-1", (string)p["follow_with"]);
            Assert.Equal("modelo 2 de 5", (string)p["last_checkpoint"]);
            Assert.Contains("Do NOT send the script again", (string)p["what_this_means"]);
            Assert.Null(p["modal_dialog"]);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, PythonHandoff.Payload("j", 1, 0, null, null)["last_checkpoint"].Type);
        }

        [Fact]
        public void A_smaller_callers_budget_is_preserved()
        {
            Assert.Equal(1000, CommandDeadlinePolicy.WaitMs("horizun_health", 1000));
            Assert.Equal(0, CommandDeadlinePolicy.RemainingMs("horizun_navigate", 1000, 1001, false));
        }

        [Fact]
        public void Health_reserves_time_for_the_addin_diagnosis_to_arrive()
        {
            Assert.Equal(4000, CommandDeadlinePolicy.DispatchWaitMs("horizun_health", 600000));
            Assert.Equal(5000, CommandDeadlinePolicy.WaitMs("horizun_health", 600000));
            Assert.Equal(1000, CommandDeadlinePolicy.DispatchWaitMs("horizun_health", 1000));
            Assert.Equal(15000, CommandDeadlinePolicy.DispatchWaitMs("horizun_navigate", 600000));
        }
    }
}
