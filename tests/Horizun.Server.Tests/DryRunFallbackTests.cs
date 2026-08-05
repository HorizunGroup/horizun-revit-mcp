// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE FALLBACK HAS TO REACH THE FIRST NORMAL CALL.
//
// dry_run defaults to TRUE on every plan/apply command. So the first thing an LLM
// sends for "create a sprinkler head" is a rehearsal - and the rehearsal used to
// come back success=true, invalid=1, with NO fallback block anywhere. The verdict
// was computed only on the apply path, which a caller reaches by deliberately
// sending dry_run=false. To learn that Python was the way forward, the client had
// to first guess it.
//
// The live probe missed this because it forced dry_run=false: it exercised the
// path where the code already worked and reported the guarantee as proven. A test
// that confirms the implementation instead of the requirement is worse than no
// test, because it is counted.
//
// So these assert the DEFAULT path, end to end, as values: a planning result that
// carries capability gaps must carry the verdict too, through the pipe envelope
// and into structuredContent, on a SUCCESSFUL reply.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class DryRunFallbackTests
    {
        /// <summary>
        /// What a command's dry-run branch hands back: a SUCCESS carrying its own
        /// error rows. The shape is create_elements', and the others differ only in
        /// wording.
        /// </summary>
        private static JObject RehearsalPayload(int valid, JArray errors) => new JObject
        {
            ["dry_run"] = true,
            ["transaction_status"] = "not_started",
            ["requested"] = valid + errors.Count,
            ["valid"] = valid,
            ["invalid"] = errors.Count,
            ["errors"] = errors,
            ["note"] = "Nothing was created and no transaction was opened."
        };

        private static JArray Row(int index, string error) =>
            new JArray { new JObject { ["index"] = index, ["error"] = error } };

        /// <summary>The whole hop, exactly as the running system does it.</summary>
        private static JObject Hop(CommandResult result)
            => McpResult.FromPluginReply(PipeEnvelope.Of("req-1", result), null);

        private static ActionOutcome Gap(int i) => new ActionOutcome
        {
            Index = i,
            Error = "unsupported kind 'sprinkler_head'",
            UnsupportedReason = FallbackSignal.ReasonUnsupportedKind
        };

        private static ActionOutcome Invalid(int i) => new ActionOutcome
        {
            Index = i,
            Error = "profile needs at least three points"
        };

        /// <summary>
        /// THE DEFECT. A rehearsal whose only failure is a capability gap must publish
        /// the grant. Nothing about this call says dry_run=false - that is the point.
        /// </summary>
        [Fact]
        public void A_default_rehearsal_with_only_capability_gaps_publishes_the_grant()
        {
            FallbackVerdict verdict = FallbackDecision.Decide(new[] { Gap(0) }, writeStarted: false);
            CommandResult rehearsal = FallbackDecision.Attach(
                CommandResult.Ok(RehearsalPayload(0, Row(0, "unsupported kind 'sprinkler_head'"))), verdict);

            JObject mcp = Hop(rehearsal);

            // Still a success: nothing failed, nothing was written, the rehearsal ran.
            Assert.False((bool)mcp["isError"]);

            JObject structured = (JObject)mcp["structuredContent"];
            Assert.NotNull(structured);

            // The payload the caller asked for is untouched...
            Assert.True((bool)structured["dry_run"]);
            Assert.Equal(1, (int)structured["invalid"]);

            // ...and the verdict rides with it, on the FIRST call.
            JObject fallback = (JObject)structured["fallback"];
            Assert.NotNull(fallback);
            Assert.True((bool)fallback["allowed"]);
            Assert.False((bool)fallback["write_started"]);
            Assert.Equal("unsupported_kind", (string)fallback["reason"]);
            Assert.Equal("horizun_execute_python", (string)fallback["recommended_tool"]);

            Assert.Single((JArray)structured["capability_gaps"]);
            Assert.Equal(0, (int)structured["capability_gaps"][0]["index"]);
        }

        /// <summary>
        /// The mixed batch must be blocked from the SAME default route. Otherwise the
        /// only way to learn a batch is mixed is to send an apply.
        /// </summary>
        [Fact]
        public void A_default_rehearsal_with_a_mixed_batch_publishes_a_refusal_and_the_gaps()
        {
            var errors = new JArray
            {
                new JObject { ["index"] = 0, ["error"] = "unsupported kind 'sprinkler_head'" },
                new JObject { ["index"] = 1, ["error"] = "profile needs at least three points" }
            };
            FallbackVerdict verdict = FallbackDecision.Decide(new[] { Gap(0), Invalid(1) }, writeStarted: false);
            CommandResult rehearsal = FallbackDecision.Attach(
                CommandResult.Ok(RehearsalPayload(0, errors)), verdict);

            JObject structured = (JObject)Hop(rehearsal)["structuredContent"];

            Assert.False((bool)structured["fallback"]["allowed"]);
            Assert.Equal("mixed_capability_and_invalid_input", (string)structured["fallback"]["reason"]);
            // The map still arrives - the caller has to know which index has no typed path.
            Assert.Single((JArray)structured["capability_gaps"]);
            Assert.Equal(0, (int)structured["capability_gaps"][0]["index"]);
        }

        /// <summary>
        /// A rehearsal whose failures are all fixable must publish NOTHING. Absence is
        /// the answer, and a block here would send a client to write a script around
        /// its own typo.
        /// </summary>
        [Fact]
        public void A_default_rehearsal_with_only_fixable_errors_publishes_no_block()
        {
            FallbackVerdict verdict = FallbackDecision.Decide(new[] { Invalid(0) }, writeStarted: false);
            CommandResult rehearsal = FallbackDecision.Attach(
                CommandResult.Ok(RehearsalPayload(0, Row(0, "profile needs at least three points"))), verdict);

            JObject structured = (JObject)Hop(rehearsal)["structuredContent"];

            Assert.Null(structured["fallback"]);
            Assert.Null(structured["capability_gaps"]);
            // ...and the rehearsal itself is unchanged.
            Assert.Equal(1, (int)structured["invalid"]);
        }

        [Fact]
        public void A_clean_rehearsal_publishes_no_block_either()
        {
            FallbackVerdict verdict = FallbackDecision.Decide(
                new[] { new ActionOutcome { Index = 0 } }, writeStarted: false);
            CommandResult rehearsal = FallbackDecision.Attach(
                CommandResult.Ok(RehearsalPayload(1, new JArray())), verdict);

            JObject structured = (JObject)Hop(rehearsal)["structuredContent"];

            Assert.Null(structured["fallback"]);
            Assert.Null(structured["capability_gaps"]);
            Assert.Equal(0, (int)structured["invalid"]);
        }

        /// <summary>
        /// Attaching must never turn a success into a failure, nor rewrite the payload
        /// the caller asked for. The verdict is additional information, not a verdict
        /// on the rehearsal.
        /// </summary>
        [Fact]
        public void Attaching_a_verdict_preserves_the_rehearsal_result()
        {
            JObject payload = RehearsalPayload(2, Row(2, "unsupported kind 'x'"));
            CommandResult attached = FallbackDecision.Attach(
                CommandResult.Ok(payload), FallbackDecision.Decide(new[] { Gap(2) }, writeStarted: false));

            Assert.True(attached.Success);
            Assert.Null(attached.Error);
            JObject data = (JObject)Hop(attached)["structuredContent"];
            Assert.Equal(2, (int)data["valid"]);
            Assert.Equal("not_started", (string)data["transaction_status"]);
        }

        /// <summary>
        /// THE MISTAKE THIS GUARDS. Every test above goes through
        /// McpResult.FromPluginReply - and the running server does NOT call it on the
        /// success path. It calls WithImageIfAny, which built the result through the
        /// two-argument Structured() and dropped the verdict on the floor. Every unit
        /// test passed and the live probe failed, because the tests exercised a helper
        /// production did not use.
        ///
        /// The forwarder is in Program.cs, which this project does not link, so the
        /// guard is over its source: the success branch must hand the fallback and the
        /// gaps onward. If someone reverts that, this fails here rather than in Revit.
        /// </summary>
        [Fact]
        public void The_servers_success_forwarder_passes_the_verdict_on()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(
                       System.IO.Path.Combine(d.FullName, "src", "Horizun.Server", "Program.cs")))
                d = d.Parent;
            Assert.True(d != null, "could not locate src/Horizun.Server/Program.cs");

            string program = System.IO.File.ReadAllText(
                System.IO.Path.Combine(d.FullName, "src", "Horizun.Server", "Program.cs"));

            int success = program.IndexOf("JToken data = reply[\"data\"];", System.StringComparison.Ordinal);
            Assert.True(success >= 0, "the success branch of the forwarder moved; this guard needs updating");

            string branch = program.Substring(success, System.Math.Min(400, program.Length - success));
            Assert.Contains("reply[\"fallback\"]", branch, System.StringComparison.Ordinal);
            Assert.Contains("reply[\"capability_gaps\"]", branch, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The same helper is used for refusals, so one rule serves both routes. A
        /// typed refusal keeps behaving exactly as before.
        /// </summary>
        [Fact]
        public void The_same_helper_still_serves_a_typed_refusal()
        {
            CommandResult refusal = FallbackDecision.Refuse(
                "1 element plan(s) are invalid. Nothing was created.",
                FallbackDecision.Decide(new[] { Gap(0) }, writeStarted: false));

            JObject mcp = Hop(refusal);
            Assert.True((bool)mcp["isError"]);
            Assert.True((bool)mcp["structuredContent"]["fallback"]["allowed"]);
        }
    }
}
