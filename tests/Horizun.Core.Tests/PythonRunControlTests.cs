using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>execute_python: time limit and cooperative cancellation (4.2-d).</summary>
    public sealed class PythonRunControlTests
    {
        private sealed class Clock
        {
            public DateTime Now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            public DateTime Read() => Now;
        }

        [Fact]
        public void Without_limit_or_cancel_the_script_may_go_on()
        {
            var clock = new Clock();
            var control = new PythonRunControl(null, clock.Read);
            clock.Now = clock.Now.AddHours(30);
            Assert.Null(control.Poll());
            Assert.Null(control.Kind);
        }

        [Fact]
        public void The_deadline_trips_once_and_stays_tripped()
        {
            var clock = new Clock();
            var control = new PythonRunControl(10, clock.Read);
            clock.Now = clock.Now.AddSeconds(9.9);
            Assert.Null(control.Poll());
            clock.Now = clock.Now.AddSeconds(0.2);
            Assert.Equal(PythonRunControl.TimedOut, control.Poll());
            Assert.False(control.Cancel("late"));
            Assert.Equal(PythonRunControl.TimedOut, control.Kind);
            Assert.Equal(10, control.LimitSeconds);
            Assert.True(control.ElapsedSeconds >= 10);
        }

        [Fact]
        public void Cancel_is_seen_at_the_next_poll_and_keeps_its_reason()
        {
            var control = new PythonRunControl(null, new Clock().Read);
            Assert.True(control.Cancel("user asked"));
            Assert.Equal(PythonRunControl.Cancelled, control.Poll());
            Assert.Equal("user asked", control.Reason);
            Assert.False(control.Cancel("again"));
            Assert.Equal("user asked", control.Reason);
        }

        [Fact]
        public void An_explicit_cancel_wins_over_a_deadline_that_also_passed()
        {
            var clock = new Clock();
            var control = new PythonRunControl(1, clock.Read);
            control.Cancel("stop");
            clock.Now = clock.Now.AddMinutes(5);
            Assert.Equal(PythonRunControl.Cancelled, control.Poll());
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(0.5)]
        [InlineData(86401.0)]
        public void Out_of_range_limits_are_refused(double value)
        {
            double? limit;
            string error;
            Assert.False(PythonRunControl.TryParseLimit(new JValue(value), out limit, out error));
            Assert.Contains("timeout_s", error);
        }

        [Fact]
        public void Limits_must_be_numbers_and_absent_means_no_limit()
        {
            double? limit; string error;
            Assert.True(PythonRunControl.TryParseLimit(null, out limit, out error));
            Assert.Null(limit);
            Assert.True(PythonRunControl.TryParseLimit(JValue.CreateNull(), out limit, out error));
            Assert.Null(limit);
            Assert.True(PythonRunControl.TryParseLimit(new JValue(90), out limit, out error));
            Assert.Equal(90, limit);
            Assert.False(PythonRunControl.TryParseLimit(new JValue("90"), out limit, out error));
            Assert.False(PythonRunControl.TryParseLimit(new JValue(true), out limit, out error));
        }

        [Fact]
        public void The_registry_reaches_a_running_job_and_forgets_a_finished_one()
        {
            var control = new PythonRunControl(null, new Clock().Read);
            Assert.Equal("not_running", PythonRunRegistry.Cancel("job-x", "r"));
            PythonRunRegistry.Register("job-x", control);
            Assert.Equal("interrupt_requested", PythonRunRegistry.Cancel("job-x", "r"));
            Assert.Equal(PythonRunControl.Cancelled, control.Poll());
            Assert.Equal("already_interrupted", PythonRunRegistry.Cancel("job-x", "r2"));
            PythonRunRegistry.Unregister("job-x");
            Assert.Equal("not_running", PythonRunRegistry.Cancel("job-x", "r"));
        }

        [Fact]
        public void The_terminal_status_comes_from_the_interruption_the_command_declared()
        {
            Assert.Equal("ok", PythonRunControl.TerminalStatus(true, null));
            Assert.Equal("failed", PythonRunControl.TerminalStatus(false, null));
            Assert.Equal("failed", PythonRunControl.TerminalStatus(false, new JObject { ["code"] = "x" }));
            Assert.Equal("timed_out", PythonRunControl.TerminalStatus(false,
                new JObject { ["interrupted"] = new JObject { ["kind"] = "timed_out" } }));
            Assert.Equal("cancelled_running", PythonRunControl.TerminalStatus(false,
                new JObject { ["interrupted"] = new JObject { ["kind"] = "cancelled" } }));
        }

        [Fact]
        public void The_interruption_block_declares_rollback_and_what_it_cannot_undo()
        {
            var clock = new Clock();
            var control = new PythonRunControl(5, clock.Read);
            clock.Now = clock.Now.AddSeconds(6);
            control.Poll();
            JObject block = control.Describe("chunk 3 of 9", "rolled_back", 2);
            Assert.Equal("timed_out", (string)block["kind"]);
            Assert.Equal(5, (double)block["timeout_s"]);
            Assert.Equal("chunk 3 of 9", (string)block["interrupted_at"]);
            Assert.Equal("rolled_back", (string)block["open_transaction"]);
            Assert.Equal(2, (int)block["transactions_rolled_back"]);
            Assert.Contains("committed", (string)block["not_undone"]);
        }
    }
}
