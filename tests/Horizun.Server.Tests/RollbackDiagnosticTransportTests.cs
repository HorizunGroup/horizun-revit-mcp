// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE ROLLBACK DIAGNOSTIC, over the whole hop, as values.
//
// A failed atomic plan used to return the fixed prose "EVERY action was rolled
// back" with no structure at all - a live probe could only parse the sentence,
// and a refusal that never reached the group left the model count unchanged and
// could pass as "rolled back". The product now carries transaction_group_started,
// rollback_status, rollback_confirmed and the per-action execution_trace as data.
// This proves that data survives the real envelope and reaches structuredContent
// at the top level, which is what the live probe asserts against.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class RollbackDiagnosticTransportTests
    {
        private static JObject Hop(CommandResult result)
            => McpResult.FromPluginReply(PipeEnvelope.Of("req-1", result), null);

        private static JArray Trace() => new JArray
        {
            new JObject { ["index"] = 0, ["key"] = "w",    ["tool"] = "horizun_create_elements",   ["success"] = true,  ["error"] = null },
            new JObject { ["index"] = 1, ["key"] = "boom", ["tool"] = "horizun_transform_elements", ["success"] = false, ["error"] = "cannot succeed" }
        };

        private static CommandResult RolledBackPlan()
        {
            JObject d = PlanFailure.Diagnostic(
                transactionGroupStarted: true, transactionGroupStatus: "RolledBack",
                rollbackAttempted: true, rollbackStatus: "RolledBack",
                executionTrace: Trace(), error: "action 'boom' failed");
            return CommandResult.FailWithDetail(PlanFailure.Message(d), d);
        }

        [Fact]
        public void The_rollback_diagnostic_reaches_structuredContent_at_the_top_level()
        {
            JObject mcp = Hop(RolledBackPlan());

            Assert.True((bool)mcp["isError"]);
            JObject s = (JObject)mcp["structuredContent"];
            Assert.NotNull(s);

            // The exact fields a live probe branches on - flat, not nested under "detail".
            Assert.True((bool)s["transaction_group_started"]);
            Assert.Equal("RolledBack", (string)s["rollback_status"]);
            Assert.True((bool)s["rollback_confirmed"]);

            JArray trace = (JArray)s["execution_trace"];
            Assert.Equal(2, trace.Count);
            Assert.True((bool)trace[0]["success"]);
            Assert.Equal("w", (string)trace[0]["key"]);
            Assert.False((bool)trace[1]["success"]);
            Assert.Equal("boom", (string)trace[1]["key"]);
        }

        [Fact]
        public void An_uncertain_rollback_does_not_claim_a_clean_model_anywhere()
        {
            JObject d = PlanFailure.Diagnostic(
                transactionGroupStarted: true, transactionGroupStatus: "Error",
                rollbackAttempted: true, rollbackStatus: "Error",
                executionTrace: Trace(), error: "boom");
            JObject mcp = Hop(CommandResult.FailWithDetail(PlanFailure.Message(d), d));

            JObject s = (JObject)mcp["structuredContent"];
            Assert.False((bool)s["rollback_confirmed"]);
            // Neither the data nor the prose may promise the model is clean.
            string text = (string)mcp["content"][0]["text"];
            Assert.Contains("UNCERTAIN", text);
        }

        [Fact]
        public void A_plain_plan_failure_with_no_diagnostic_stays_plain_text()
        {
            JObject mcp = Hop(CommandResult.Fail("Invalid atomic plan; nothing ran."));
            Assert.True((bool)mcp["isError"]);
            Assert.Null(mcp["structuredContent"]);
        }

        /// <summary>
        /// THE MISTAKE THIS GUARDS, second occurrence of a known shape. Every test above
        /// goes through McpResult.FromPluginReply - and the running server does NOT call
        /// it: its forwarder in Program.cs builds the error itself, passed fallback and
        /// capability_gaps onward, and dropped `detail` on the floor. Every unit test
        /// passed and the LIVE rollback probe reported "the failed plan carried no
        /// structured rollback diagnostic" against a build that computed one. Exactly how
        /// the success path lost the fallback verdict before it (see
        /// DryRunFallbackTests.The_servers_success_forwarder_passes_the_verdict_on).
        ///
        /// Program.cs is not linked by this project, so the guard is over its source: the
        /// error branch must hand reply["detail"] onward.
        /// </summary>
        [Fact]
        public void The_servers_error_forwarder_passes_the_diagnostic_on()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(
                       System.IO.Path.Combine(d.FullName, "src", "Horizun.Server", "Program.cs")))
                d = d.Parent;
            Assert.True(d != null, "could not locate src/Horizun.Server/Program.cs");

            string text = System.IO.File.ReadAllText(
                System.IO.Path.Combine(d.FullName, "src", "Horizun.Server", "Program.cs"));

            Assert.Contains("reply[\"detail\"] as JObject", text);
            // And the local helper must not silently narrow back to three arguments.
            Assert.Contains("JArray capabilityGaps,", text);
            Assert.Contains("JObject detail", text);
        }
    }
}
