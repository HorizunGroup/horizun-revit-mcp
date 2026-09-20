// -----------------------------------------------------------------------------
// Horizun Server tests - the message writer, doubled. Original Horizun code.
//
// NOT EXECUTED. Written during an implementation-only phase.
//
// WHY A DOUBLE AT ALL. Ten protocol requirements had no test because checking
// them means watching what the server WRITES: that a modern client receives no
// untagged notification, that a cancelled request receives nothing at all, that a
// log line goes out for the request that asked and for no other. None of that is
// visible from a return value.
//
// `RecordedWriter` is that seam. It is the same `Action<string, JObject>` the
// server hands to every notifier, so the code under test is the shipping code -
// not a re-implementation of it that could agree with the test and disagree with
// the server.
//
// DETERMINISTIC, WITH NO WAITING. Every case below completes on the calling
// thread. A test that sleeps to let a notification arrive is a test that fails on
// a loaded build machine and passes on a laptop, which is worse than no test:
// it teaches people to re-run the suite until it is green.
//
// WHAT THIS FILE CANNOT REACH, said plainly rather than faked: `NotifyChange` and
// the subscription listener live inside `Program` as private statics. The
// LEGACY-vs-MODERN routing decision is made there, and reaching it needs the
// server's read loop - an integration test, pending authorisation. What IS
// reachable is every rule those two depend on, and that is what is here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Server.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// Everything the server tried to write, in order.
    ///
    /// ORDER IS THE POINT for half of these cases: "the acknowledgement is the first
    /// message" and "nothing is written after a cancellation" are both statements about
    /// sequence, and a double that only counted messages could not express either.
    /// </summary>
    internal sealed class RecordedWriter
    {
        public readonly List<(string Method, JObject Body)> Written = new List<(string, JObject)>();

        public Action<string, JObject> Notify => (method, body) => Written.Add((method, body));

        public IEnumerable<string> Methods => Written.Select(w => w.Method);

        public JObject First(string method) =>
            Written.FirstOrDefault(w => w.Method == method).Body;

        public int Count(string method) => Written.Count(w => w.Method == method);
    }

    public class ProtocolWriterTests
    {
        // ---- capabilities are what the server actually emits -------------------------

        [Fact]
        public void Advertised_listChanged_matches_what_the_server_can_emit()
        {
            // THE FAILURE THIS PREVENTS: a client told `prompts.listChanged: true` subscribes
            // to prompt changes and waits forever, because nothing in this process produces
            // one. The capability block and the honoured filter read the same set.
            JObject modern = DiscoverHandler.Capabilities(McpEra.Modern);

            Assert.Equal(SubscriptionStream.Emits(SubscriptionStream.ToolsListChanged),
                         (bool)modern["tools"]["listChanged"]);
            Assert.Equal(SubscriptionStream.Emits(SubscriptionStream.PromptsListChanged),
                         (bool)modern["prompts"]["listChanged"]);
            Assert.Equal(SubscriptionStream.Emits(SubscriptionStream.ResourcesListChanged),
                         (bool)modern["resources"]["listChanged"]);
        }

        [Fact]
        public void A_type_the_server_cannot_emit_is_never_honoured()
        {
            // The other half of the same invariant, from the subscription side.
            var requested = new SubscriptionFilter
            {
                ToolsListChanged = true,
                PromptsListChanged = true,
                ResourcesListChanged = true
            };
            SubscriptionFilter honoured = SubscriptionStream.Honour(requested);

            Assert.Equal(SubscriptionStream.Emits(SubscriptionStream.PromptsListChanged),
                         honoured.PromptsListChanged);
            Assert.Equal(SubscriptionStream.Emits(SubscriptionStream.ResourcesListChanged),
                         honoured.ResourcesListChanged);
            Assert.NotNull(SubscriptionStream.NotHonoured(requested, honoured));
        }

        [Fact]
        public void Logging_is_advertised_to_legacy_and_not_to_modern()
        {
            // logging/setLevel was removed in 2026-07-28. A server still claiming the
            // capability would be claiming a method it answers with -32601.
            Assert.NotNull(DiscoverHandler.Capabilities(McpEra.Legacy)["logging"]);
            Assert.Null(DiscoverHandler.Capabilities(McpEra.Modern)["logging"]);
        }

        // ---- modern and legacy are separated -----------------------------------------

        [Fact]
        public void A_legacy_result_is_returned_byte_for_byte()
        {
            var original = new JObject { ["tools"] = new JArray(), ["anything"] = 7 };
            string before = original.ToString(Newtonsoft.Json.Formatting.None);

            JToken stamped = ResultEnvelope.Stamp(original, McpEra.Legacy, "tools/list", null);

            Assert.Equal(before, stamped.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Null(stamped["resultType"]);
            Assert.Null(stamped["_meta"]);
        }

        [Fact]
        public void A_modern_result_carries_resultType_and_serverInfo()
        {
            JToken stamped = ResultEnvelope.Stamp(new JObject(), McpEra.Modern, "tools/list", null);

            Assert.Equal("complete", (string)stamped["resultType"]);
            Assert.NotNull(stamped["_meta"][RequestEnvelope.ServerInfoKey]);
        }

        [Fact]
        public void Resource_not_found_never_uses_the_removed_code_on_the_modern_path()
        {
            // "Implementations of this protocol version MUST NOT emit -32002."
            Assert.Equal(McpErrorCodes.InvalidParams, McpErrorCodes.ResourceNotFound(McpEra.Modern));
            Assert.Equal(McpErrorCodes.LegacyResourceNotFound, McpErrorCodes.ResourceNotFound(McpEra.Legacy));
        }

        // ---- no duplicates, and nothing after the end --------------------------------

        [Fact]
        public void One_subscriber_receives_exactly_one_copy()
        {
            var writer = new RecordedWriter();
            Subscription s = SubscriptionStream.Register(
                "Int64:21", new JValue(21), new SubscriptionFilter { ToolsListChanged = true });
            try
            {
                SubscriptionStream.Acknowledge(s, writer.Notify);
                SubscriptionStream.Publish(SubscriptionStream.ToolsListChanged, new JObject(), writer.Notify);

                Assert.Equal(1, writer.Count("notifications/tools/list_changed"));
                Assert.Equal(1, writer.Count(SubscriptionStream.AcknowledgedMethod));
                // And the acknowledgement came FIRST.
                Assert.Equal(SubscriptionStream.AcknowledgedMethod, writer.Methods.First());
            }
            finally { SubscriptionStream.Release("Int64:21"); }
        }

        [Fact]
        public void Nothing_is_written_to_a_subscription_after_it_ends()
        {
            var writer = new RecordedWriter();
            Subscription s = SubscriptionStream.Register(
                "Int64:22", new JValue(22), new SubscriptionFilter { ToolsListChanged = true });
            SubscriptionStream.Acknowledge(s, writer.Notify);
            SubscriptionStream.MarkTermination("Int64:22", SubscriptionStream.ClientCancelled);
            SubscriptionStream.Release("Int64:22");

            int before = writer.Written.Count;
            SubscriptionStream.Publish(SubscriptionStream.ToolsListChanged, new JObject(), writer.Notify);

            Assert.Equal(before, writer.Written.Count);
        }

        [Fact]
        public void A_transport_loss_writes_nothing_and_keeps_its_cause()
        {
            // There is nowhere to write to. What must survive is the CAUSE, so the listener
            // does not announce a graceful close down a channel that is gone.
            Subscription s = SubscriptionStream.Register(
                "Int64:23", new JValue(23), new SubscriptionFilter { ToolsListChanged = true });
            try
            {
                SubscriptionStream.MarkAllTermination(SubscriptionStream.TransportLost);
                Assert.Equal(SubscriptionStream.TransportLost, SubscriptionStream.CauseOf("Int64:23"));
            }
            finally { SubscriptionStream.Release("Int64:23"); }
        }

        // ---- the per-request log level ------------------------------------------------

        [Fact]
        public void A_modern_request_that_named_no_level_gets_no_logs()
        {
            // "no field, no logs" is the revision's own default, and it is the behaviour a
            // client relies on to not be flooded.
            var writer = new RecordedWriter();
            McpLogging.EmitForRequest("info", new JObject { ["event"] = "x" }, writer.Notify,
                                      requestLevel: null, requestId: 1);
            Assert.Empty(writer.Written);
        }

        [Fact]
        public void A_modern_request_gets_the_severities_it_asked_for_and_no_others()
        {
            var writer = new RecordedWriter();
            var data = new JObject { ["event"] = "x" };

            McpLogging.EmitForRequest("debug", data, writer.Notify, "warning", 1);   // below
            McpLogging.EmitForRequest("error", data, writer.Notify, "warning", 1);   // at or above

            Assert.Single(writer.Written);
            Assert.Equal("notifications/message", writer.Written[0].Method);
            Assert.Equal("error", (string)writer.Written[0].Body["level"]);
        }

        [Fact]
        public void A_per_request_log_carries_the_request_it_belongs_to()
        {
            // The revision defines no correlation field for logs, so this travels under a
            // VENDOR key - and a client running several requests at once cannot otherwise
            // tell whose log it is reading.
            var writer = new RecordedWriter();
            McpLogging.EmitForRequest("error", new JObject { ["event"] = "x" }, writer.Notify, "debug", 42);

            JObject body = writer.First("notifications/message");
            Assert.Equal(42, (int)body["_meta"]["io.horizunhub/requestId"]);
        }

        // ---- caching -------------------------------------------------------------------

        [Fact]
        public void The_six_cacheable_operations_are_exactly_the_ones_the_spec_lists()
        {
            foreach (string method in new[] { "server/discover", "tools/list", "prompts/list",
                                              "resources/list", "resources/templates/list", "resources/read" })
                Assert.True(CacheHints.IsCacheable(method), method + " must carry caching hints");

            foreach (string method in new[] { "tools/call", "initialize", "completion/complete",
                                              "subscriptions/listen", "tasks/get" })
                Assert.False(CacheHints.IsCacheable(method), method + " must not");
        }

        [Fact]
        public void A_cacheable_result_carries_a_non_negative_ttl_and_a_scope()
        {
            var result = new JObject();
            CacheHints.Apply(result, "tools/list", null);

            Assert.True((long)result["ttlMs"] >= 0);
            Assert.Contains((string)result["cacheScope"], new[] { CacheHints.Public, CacheHints.Private });
        }

        [Fact]
        public void A_multi_round_trip_retry_is_not_given_caching_hints()
        {
            // "Results produced by retrying a request through the MRTR mechanism - requests
            // carrying inputResponses or requestState - MUST NOT be cached." Omitting the
            // hints is what tells a client the result is immediately stale.
            foreach (string field in new[] { "inputResponses", "requestState" })
            {
                var result = new JObject();
                CacheHints.Apply(result, "resources/read",
                                 new JObject { [field] = new JArray(), ["uri"] = "horizun://build/identity" });

                Assert.Null(result["ttlMs"]);
                Assert.Null(result["cacheScope"]);
            }
        }

        [Fact]
        public void A_result_about_this_machine_is_never_public()
        {
            // A shared cache serving one operator's authorisation posture to another is the
            // failure `cacheScope` exists to prevent.
            foreach (string uri in new[] { "horizun://security/current-profile", "horizun://build/identity",
                                           "horizun://contract/tools" })
            {
                var result = new JObject();
                CacheHints.Apply(result, "resources/read", new JObject { ["uri"] = uri });
                Assert.Equal(CacheHints.Private, (string)result["cacheScope"]);
            }
        }

        [Fact]
        public void An_interim_result_carries_no_hints()
        {
            JToken stamped = ResultEnvelope.Stamp(new JObject(), McpEra.Modern, "tools/list", null,
                                                  ResultEnvelope.InputRequired);
            Assert.Equal(ResultEnvelope.InputRequired, (string)stamped["resultType"]);
            Assert.Null(stamped["ttlMs"]);
        }
    }
}
