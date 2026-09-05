// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE SWEEP RUNNER NEEDS A REVIT, SO ITS INVARIANTS ARE GUARDED IN THE SOURCE.
//
// Dispatcher.RunSequence holds Revit's UI thread and opens real documents;
// nothing here can execute it. What CAN be checked without Revit is that the
// four decisions it is built on are still in the code, because each of them is
// a line an ordinary refactor would remove without any test noticing:
//
//   the start of a step is recorded BEFORE the step runs - remove it and a
//   ten-minute cloud open is indistinguishable from a stuck job;
//
//   execution stops at the first failure and the rest are SETTLED to not_run -
//   remove it and a sweep that stopped at model three returns three steps and
//   reads as a three-model sweep that worked;
//
//   the closes of a stopped sweep still RUN - remove it and a failed model
//   leaves a document open in somebody's Revit, which is exactly where an
//   implementation is tempted to swallow the exception and move on;
//
//   the sequence path does not disturb the single-call path.
//
// A source guard is a weak test and it is the only one available here. It is
// written to fail loudly when the line it names disappears rather than to prove
// the behaviour, and it says so.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SequenceRunnerWiringTests
    {
        private static DirectoryInfo Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d;
        }

        private static string Source(string relative)
        {
            return File.ReadAllText(Path.Combine(Root().FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string Dispatcher()
        {
            return Source("src/Horizun.Revit/Core/Dispatcher.cs");
        }

        /// <summary>The body of one method, from its declaration to the next one.</summary>
        private static string Body(string source, string declaration)
        {
            int i = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(i >= 0, "not found: " + declaration);
            int next = source.IndexOf("\n        private ", i + declaration.Length, StringComparison.Ordinal);
            if (next < 0) next = source.Length;
            return source.Substring(i, next - i);
        }

        [Fact]
        public void The_sweep_runner_exists_and_is_reached_from_the_async_pump()
        {
            string src = Dispatcher();
            Assert.Contains("private void RunSequence(UIApplication app, AsyncWork work)", src);

            // The single-call path is left exactly as it was: the sequence branch is
            // an early return, not a second meaning threaded through every line below.
            Assert.Matches(
                new Regex(@"if \(work\.Sequence != null && work\.Sequence\.Count > 0\) \{ RunSequence\(app, work\); return; \}"),
                src);
        }

        [Fact]
        public void A_step_records_its_start_before_it_runs()
        {
            string body = Body(Dispatcher(), "private void RunSequence(UIApplication app, AsyncWork work)");

            int recorded = body.IndexOf("work.Record.Step(step.Key, step.Tool, StepStatus.Running", StringComparison.Ordinal);
            int ran = body.IndexOf("RunOneSequenceStep(app, step)", StringComparison.Ordinal);

            Assert.True(recorded >= 0, "the running state is no longer written to the job record");
            Assert.True(ran >= 0, "the step is no longer executed");
            // A cloud open takes minutes. A start written only on completion makes a
            // working job look stuck for the whole time it is working.
            Assert.True(recorded < ran,
                "the step's start must be recorded BEFORE it runs, and it now happens after.");
        }

        [Fact]
        public void Execution_stops_at_the_first_failure_and_the_rest_are_settled_to_not_run()
        {
            string body = Body(Dispatcher(), "private void RunSequence(UIApplication app, AsyncWork work)");

            Assert.Contains("if (step.Status == StepStatus.Failed) break;", body);
            // SettleAfterStop is the shared rule, not a re-implementation here: the
            // same call the tests exercise directly.
            Assert.Contains("JobSequenceRules.SettleAfterStop(steps,", body);
            // And the settled steps are written to the record, so a poller sees them.
            Assert.Contains("if (s.Status == StepStatus.NotRun)", body);
        }

        [Fact]
        public void The_closes_of_a_stopped_sweep_still_run()
        {
            string src = Dispatcher();
            string body = Body(src, "private void RunSequence(UIApplication app, AsyncWork work)");

            // The call, and the condition that reaches it: ANY stop, from any cause.
            // Keying this on an index once meant a sweep stopped before its first
            // step skipped its closes, because "nothing stopped" and "stopped at
            // step zero" shared the value -1.
            Assert.Contains("if (stopped) RunPendingCloses(app, work, steps, stoppedAt);", body);

            // And it runs ONLY closes. Retrying reads over a Revit in an unknown state
            // is how one bad model becomes twelve bad results.
            string closes = Body(src, "private void RunPendingCloses(UIApplication app, AsyncWork work, List<SequenceEntry> steps, int stoppedAt)");
            Assert.Contains("if (step.Tool != \"horizun_document_session\") continue;", closes);

            // And only the closes with something to close: a sweep that stopped before
            // anything opened must not report "a document may be left open" about a
            // document that never existed.
            // THE NEAREST PRECEDING OPEN, not any open anywhere. The first version
            // asked whether SOME open had succeeded, which is true in a twelve-model
            // sweep that stopped at model three - so the cleanup aimed closes at nine
            // documents this sweep never opened, and a close that finds one of them
            // open because the USER has it open closes the user's document.
            Assert.Contains("if (!ownOpenSucceeded) continue;", closes);
            Assert.Contains("for (int j = i - 1; j >= 0; j--)", closes);
            Assert.Contains("ownOpenSucceeded = steps[j].Status == StepStatus.Succeeded;", closes);
            Assert.DoesNotContain("somethingOpened", closes);
        }

        [Fact]
        public void The_terminal_status_of_a_sweep_comes_from_the_shared_rule()
        {
            string body = Body(Dispatcher(), "private void RunSequence(UIApplication app, AsyncWork work)");
            // Eleven succeeded steps and one failed is not a successful sweep, and the
            // rule that says so lives in one place.
            Assert.Contains("JobSequenceRules.TerminalStatus(steps)", body);
            Assert.Contains("work.Record.Finish(terminal, failure)", body);
        }

        [Fact]
        public void Permissions_are_checked_again_when_a_step_runs()
        {
            string body = Body(Dispatcher(),
                "private CommandResult RunOneSequenceStep(UIApplication app, SequenceEntry step)");
            // A sequence can sit in the queue while the machine owner revokes something.
            Assert.Contains("Settings.IsToolAllowed(contract, out permissionReason)", body);
        }

        [Fact]
        public void The_submission_path_expands_a_model_list_through_the_shared_rules()
        {
            string src = Source("src/Horizun.Revit/Commands/SubmitJobCommand.cs");

            // The sweep is planned and expanded by the SAME functions the tests drive,
            // rather than by a second expansion that has to be kept in step by hand.
            Assert.Contains("BatchAuditRules.Plan(list, options)", src);
            Assert.Contains("BatchAuditRules.ToSequence(plan, options)", src);
            // And it is admitted by the same allowlist as any other sequence.
            Assert.Contains("JobSequenceRules.Admit(sequence, hasToolShape)", src);
        }

        [Fact]
        public void A_refused_model_list_queues_nothing()
        {
            string src = Source("src/Horizun.Revit/Commands/SubmitJobCommand.cs");

            // CONTROL FLOW, not source position. The two live in different methods,
            // so comparing where they appear in the file would prove nothing - the
            // first draft of this guard did exactly that and failed for the wrong
            // reason. What matters is that a refused plan returns before its caller
            // can reach the submission path at all.
            Assert.Contains("refusal = CommandResult.Fail(plan.Code", src);
            Assert.Contains("sequence = ExpandModels(models, request[\"batch\"] as JObject, out expansionRefusal);", src);
            Assert.Contains("if (sequence == null) return expansionRefusal;", src);

            // And the expansion returns null on every refusal it can produce.
            int expand = src.IndexOf("private static JArray ExpandModels", StringComparison.Ordinal);
            Assert.True(expand >= 0);
            string body = src.Substring(expand);
            Assert.Contains("if (!plan.Ok)", body);
        }
    }
}
