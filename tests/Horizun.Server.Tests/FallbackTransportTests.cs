// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE WHOLE HOP, as values rather than as source text.
//
// A structured signal that is dropped in serialization is indistinguishable from
// one that was never granted, and the client then silently stops falling back to
// Python - a failure that presents as "the bridge just says no" and gets
// diagnosed as a model problem. Asserting that PipeServer.cs *contains a line*
// would not catch it; building a CommandResult, putting it through the real
// envelope, and reading the real MCP result does.
//
// Both halves of the hop are the SHIPPED code: PipeEnvelope is what the add-in
// writes to the pipe, McpResult is what the server hands the client.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class FallbackTransportTests
    {
        /// <summary>The real hop: command result -> pipe envelope -> MCP tool result.</summary>
        private static JObject Hop(CommandResult result)
            => McpResult.FromPluginReply(PipeEnvelope.Of("req-1", result), null);

        private static CommandResult GrantedBatch() => FallbackDecision.Refuse(
            "1 element plan(s) are invalid. Nothing was created.",
            FallbackDecision.Decide(new[]
            {
                new ActionOutcome { Index = 0 },
                new ActionOutcome { Index = 1, Error = "unsupported kind 'sprinkler_head'",
                                    UnsupportedReason = FallbackSignal.ReasonUnsupportedKind }
            }, writeStarted: false));

        private static CommandResult MixedBatch() => FallbackDecision.Refuse(
            "2 element plan(s) are invalid. Nothing was created.",
            FallbackDecision.Decide(new[]
            {
                new ActionOutcome { Index = 1, Error = "unsupported kind 'sprinkler_head'",
                                    UnsupportedReason = FallbackSignal.ReasonUnsupportedKind },
                new ActionOutcome { Index = 2, Error = "profile needs at least three points" }
            }, writeStarted: false));

        // ---- the pipe half ------------------------------------------------------

        [Fact]
        public void The_envelope_carries_the_signal_beside_the_error()
        {
            JObject envelope = PipeEnvelope.Of("req-1", GrantedBatch());

            Assert.False((bool)envelope["success"]);
            Assert.NotNull(envelope["fallback"]);
            Assert.True((bool)envelope["fallback"]["allowed"]);
            Assert.False((bool)envelope["fallback"]["write_started"]);
            Assert.Equal("horizun_execute_python", (string)envelope["fallback"]["recommended_tool"]);
            Assert.NotNull(envelope["capability_gaps"]);
            // Structure beside the human message, never inside it.
            Assert.DoesNotContain("recommended_tool", (string)envelope["error"]);
        }

        [Fact]
        public void An_ordinary_failure_carries_no_fallback_on_the_wire()
        {
            JObject envelope = PipeEnvelope.Of("req-1", CommandResult.Fail("units must be mm, m or feet."));

            Assert.Null(envelope["fallback"]);
            Assert.Null(envelope["capability_gaps"]);
        }

        [Fact]
        public void A_successful_command_carries_no_fallback_either()
        {
            JObject envelope = PipeEnvelope.Of("req-1", CommandResult.Ok(new JObject { ["created"] = 3 }));

            Assert.True((bool)envelope["success"]);
            Assert.Null(envelope["fallback"]);
            Assert.Null(envelope["capability_gaps"]);
        }

        // ---- the whole hop ------------------------------------------------------

        [Fact]
        public void A_granted_fallback_reaches_structuredContent_intact()
        {
            JObject mcp = Hop(GrantedBatch());

            Assert.True((bool)mcp["isError"]);
            JObject structured = (JObject)mcp["structuredContent"];
            Assert.NotNull(structured);

            JObject fallback = (JObject)structured["fallback"];
            Assert.True((bool)fallback["allowed"]);
            Assert.Equal("unsupported_kind", (string)fallback["reason"]);
            Assert.False((bool)fallback["write_started"]);
            Assert.Equal("horizun_execute_python", (string)fallback["recommended_tool"]);

            // A client can decide on that field alone. The prose is for the human.
            Assert.Single((JArray)structured["capability_gaps"]);
            Assert.Equal(1, (int)structured["capability_gaps"][0]["index"]);
        }

        /// <summary>
        /// THE MIXED BATCH over the wire: the gaps must arrive, the permission must not.
        /// A client reading only `allowed` gets the right answer, and a client that wants
        /// detail can see exactly which action has no typed path.
        /// </summary>
        [Fact]
        public void A_mixed_batch_transports_the_gaps_without_transporting_permission()
        {
            JObject mcp = Hop(MixedBatch());

            JObject structured = (JObject)mcp["structuredContent"];
            Assert.False((bool)structured["fallback"]["allowed"]);
            Assert.Equal("mixed_capability_and_invalid_input", (string)structured["fallback"]["reason"]);

            JArray gaps = (JArray)structured["capability_gaps"];
            Assert.Single(gaps);
            Assert.Equal(1, (int)gaps[0]["index"]);
        }

        [Fact]
        public void A_write_that_may_have_started_transports_an_explicit_refusal()
        {
            CommandResult afterWrite = FallbackDecision.Refuse(
                "Committed, but verification failed.",
                FallbackDecision.Decide(new[]
                {
                    new ActionOutcome { Index = 0, Error = "unsupported kind 'x'",
                                        UnsupportedReason = FallbackSignal.ReasonUnsupportedKind }
                }, writeStarted: true));

            JObject structured = (JObject)Hop(afterWrite)["structuredContent"];

            Assert.False((bool)structured["fallback"]["allowed"]);
            Assert.True((bool)structured["fallback"]["write_started"]);
            Assert.Contains("SECOND write", (string)structured["fallback"]["what_this_means"]);
        }

        [Fact]
        public void An_ordinary_failure_reaches_the_client_as_plain_text_with_no_structure()
        {
            JObject mcp = Hop(CommandResult.Fail("units must be mm, m or feet."));

            Assert.True((bool)mcp["isError"]);
            // Absence is the answer: a client that finds no block must not fall back.
            Assert.Null(mcp["structuredContent"]);
            Assert.Contains("units must be mm", (string)mcp["content"][0]["text"]);
        }

        [Fact]
        public void The_human_message_survives_alongside_the_structure()
        {
            string text = (string)Hop(GrantedBatch())["content"][0]["text"];

            Assert.Contains("Nothing was created", text);
            // The block is repeated in the text so a human reading a log sees it too.
            Assert.Contains("--- fallback ---", text);
            Assert.Contains("capability_gaps", text);
        }

        /// <summary>
        /// The success path must not acquire a fallback by accident, and its payload
        /// must arrive unchanged - that payload is where the Python evidence fields live.
        /// </summary>
        [Fact]
        public void A_python_result_payload_crosses_the_hop_unchanged()
        {
            var payload = new JObject
            {
                ["mode"] = "sync",
                ["executed"] = true,
                ["evidence_status"] = "self_reported_verified",
                ["script_reported_status"] = "verified",
                ["host_verified"] = false
            };

            JObject mcp = Hop(CommandResult.Ok(payload));
            JObject structured = (JObject)mcp["structuredContent"];

            Assert.False((bool)mcp["isError"]);
            Assert.Equal("self_reported_verified", (string)structured["evidence_status"]);
            Assert.False((bool)structured["host_verified"]);
            // The word the typed commands own must not appear as this path's state.
            Assert.NotEqual("verified", (string)structured["evidence_status"]);
            Assert.Null(structured["fallback"]);
        }
    }
}
