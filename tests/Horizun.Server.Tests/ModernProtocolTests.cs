// -----------------------------------------------------------------------------
// Horizun Server tests - the 2026-07-28 adapter. Original Horizun code.
//
// NOT EXECUTED YET. Written during the competitive-gap campaign of 2026-09-15,
// which is an implementation phase only: nothing in this file has been run, and
// no claim anywhere should say these behaviours are verified.
//
// What they are FOR: the adapter's whole risk is that it changes what an existing
// client sees. Every MCP client installed today is legacy, and the failure mode
// is silent from this side - a client that dislikes an unexpected field simply
// disconnects. So the first and largest group below asserts the negative: that a
// legacy exchange is byte for byte what it was.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Server.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ModernProtocolTests
    {
        // ---- the revision table ---------------------------------------------------

        [Fact]
        public void Every_revision_has_an_era_and_only_the_newest_is_modern()
        {
            Assert.Contains("2026-07-28", McpRevision.All);
            Assert.True(McpRevision.IsModern("2026-07-28"));

            foreach (string legacy in new[] { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" })
            {
                Assert.True(McpRevision.IsSupported(legacy));
                Assert.False(McpRevision.IsModern(legacy));
                Assert.Equal(McpEra.Legacy, McpRevision.EraOf(legacy));
            }
        }

        [Fact]
        public void An_unknown_revision_is_not_supported_rather_than_coerced()
        {
            Assert.False(McpRevision.IsSupported("1900-01-01"));
            Assert.False(McpRevision.IsSupported(null));
        }

        // ---- reading the per-request metadata --------------------------------------

        private static JObject ModernMeta(string version = "2026-07-28", JObject capabilities = null)
            => new JObject
            {
                ["_meta"] = new JObject
                {
                    [RequestEnvelope.ProtocolVersionKey] = version,
                    [RequestEnvelope.ClientCapabilitiesKey] = capabilities ?? new JObject()
                }
            };

        [Fact]
        public void A_request_with_no_meta_is_legacy_and_is_not_an_error()
        {
            RequestEnvelope env = RequestEnvelope.Read("tools/list", new JObject());
            Assert.Equal(McpEra.Legacy, env.Era);
            Assert.False(env.CarriesModernMeta);
            Assert.Null(env.DeclaredVersion);
        }

        [Fact]
        public void Initialize_is_legacy_even_when_it_carries_a_modern_looking_meta()
        {
            RequestEnvelope env = RequestEnvelope.Read("initialize", ModernMeta());
            Assert.Equal(McpEra.Legacy, env.Era);
        }

        [Fact]
        public void A_modern_request_declares_its_version_and_capabilities()
        {
            RequestEnvelope env = RequestEnvelope.Read("tools/list", ModernMeta());
            Assert.Equal(McpEra.Modern, env.Era);
            Assert.Equal("2026-07-28", env.DeclaredVersion);
            Assert.NotNull(env.ClientCapabilities);
        }

        [Fact]
        public void An_unsupported_version_is_refused_with_the_list_to_retry_with()
        {
            var error = Assert.Throws<McpDataError>(() =>
                RequestEnvelope.Read("tools/list", ModernMeta("1900-01-01")));

            Assert.Equal(McpErrorCodes.UnsupportedProtocolVersion, error.Code);
            Assert.Equal("1900-01-01", (string)error.ErrorData["requested"]);

            var supported = new List<string>();
            foreach (JToken t in (JArray)error.ErrorData["supported"]) supported.Add((string)t);
            Assert.Contains("2026-07-28", supported);
            Assert.Contains("2025-11-25", supported);
        }

        [Fact]
        public void A_modern_request_without_client_capabilities_is_invalid_params()
        {
            var prms = new JObject
            {
                ["_meta"] = new JObject { [RequestEnvelope.ProtocolVersionKey] = "2026-07-28" }
            };
            var error = Assert.Throws<McpDataError>(() => RequestEnvelope.Read("tools/list", prms));
            Assert.Equal(McpErrorCodes.InvalidParams, error.Code);
        }

        [Fact]
        public void Discover_without_a_declared_version_still_hands_back_the_version_list()
        {
            // The stdio backward-compatibility probe. Refusing it is correct - every
            // request must declare its version - but a refusal that teaches the client
            // nothing has defeated the probe.
            var error = Assert.Throws<McpDataError>(() => RequestEnvelope.Read("server/discover", new JObject()));
            Assert.Equal(McpErrorCodes.InvalidParams, error.Code);
            Assert.NotNull(error.ErrorData["supported"]);
        }

        [Fact]
        public void Declared_extensions_are_read_from_the_capabilities()
        {
            JObject prms = ModernMeta(capabilities: new JObject
            {
                ["extensions"] = new JObject { [ExtensionRegistry.Tasks] = new JObject() }
            });
            RequestEnvelope env = RequestEnvelope.Read("tools/call", prms);
            Assert.True(env.Declares(ExtensionRegistry.Tasks));
            Assert.False(env.Declares(ExtensionRegistry.Ui));
        }

        [Fact]
        public void A_missing_extension_is_refused_with_a_ClientCapabilities_object_to_merge()
        {
            // schema.ts: data: { requiredCapabilities: ClientCapabilities }, and
            // ClientCapabilities.extensions is { [id]: settings }. NOT an array, and not a
            // dotted path inside a string: the client is expected to MERGE this into its
            // own capabilities and resend, which it cannot do with a string it has to parse.
            RequestEnvelope env = RequestEnvelope.Read("tools/call", ModernMeta());
            var error = Assert.Throws<McpDataError>(() =>
                env.RequireExtension(ExtensionRegistry.Tasks, "because."));

            Assert.Equal(McpErrorCodes.MissingRequiredClientCapability, error.Code);

            var required = error.ErrorData["requiredCapabilities"] as JObject;
            Assert.NotNull(required);

            var extensions = required["extensions"] as JObject;
            Assert.NotNull(extensions);

            // The extension id is a KEY, and its value is that extension's settings object.
            Assert.NotNull(extensions[ExtensionRegistry.Tasks]);
            Assert.Equal(JTokenType.Object, extensions[ExtensionRegistry.Tasks].Type);

            // And the shape a client would actually send: merging this in produces valid
            // capabilities that satisfy the very check that refused it.
            JObject prms = ModernMeta();
            ((JObject)prms["_meta"][RequestEnvelope.ClientCapabilitiesKey])["extensions"] =
                extensions.DeepClone();
            RequestEnvelope retried = RequestEnvelope.Read("tools/call", prms);
            retried.RequireExtension(ExtensionRegistry.Tasks, "because.");
        }

        [Fact]
        public void A_missing_metadata_field_is_malformed_rather_than_a_missing_capability()
        {
            // The spec: "A request missing any required field is malformed; the server MUST
            // reject it with -32602." The client's move is to SEND THE FIELD, not to declare
            // a capability - so this error must not carry a requiredCapabilities block that
            // sends it to fix something that is not broken.
            var prms = new JObject
            {
                ["_meta"] = new JObject
                {
                    [RequestEnvelope.ProtocolVersionKey] = McpRevision.Latest
                }
            };
            var error = Assert.Throws<McpDataError>(() => RequestEnvelope.Read("tools/list", prms));
            Assert.Equal(McpErrorCodes.InvalidParams, error.Code);
            Assert.Null(error.ErrorData["requiredCapabilities"]);
            Assert.Equal(RequestEnvelope.ClientCapabilitiesKey, (string)error.ErrorData["field"]);
        }

        // ---- the property that matters most ----------------------------------------

        [Fact]
        public void A_legacy_result_leaves_the_envelope_byte_for_byte_unchanged()
        {
            var original = new JObject
            {
                ["tools"] = new JArray(new JObject { ["name"] = "horizun_health" })
            };
            string before = original.ToString(Formatting.None);

            JToken after = ResultEnvelope.Stamp(original, McpEra.Legacy, "tools/list", new JObject());

            Assert.Equal(before, after.ToString(Formatting.None));
            Assert.Null(after["resultType"]);
            Assert.Null(after["_meta"]);
            Assert.Null(after["ttlMs"]);
            Assert.Null(after["cacheScope"]);
        }

        [Fact]
        public void A_modern_result_carries_result_type_server_info_and_provenance()
        {
            var result = new JObject { ["tools"] = new JArray() };
            var stamped = (JObject)ResultEnvelope.Stamp(result, McpEra.Modern, "tools/list", new JObject());

            Assert.Equal(ResultEnvelope.Complete, (string)stamped["resultType"]);
            Assert.NotNull(stamped["_meta"][RequestEnvelope.ServerInfoKey]);
            Assert.Equal("horizun-mcp", (string)stamped["_meta"][RequestEnvelope.ServerInfoKey]["name"]);
            Assert.NotNull(stamped["_meta"]["io.horizunhub/provenance"]);
        }

        [Fact]
        public void A_modern_cacheable_result_carries_a_non_negative_ttl_and_a_scope()
        {
            foreach (string method in new[] { "server/discover", "tools/list", "prompts/list",
                                              "resources/list", "resources/templates/list" })
            {
                var stamped = (JObject)ResultEnvelope.Stamp(new JObject(), McpEra.Modern, method, new JObject());
                Assert.True((long)stamped["ttlMs"] >= 0, method + " must carry ttlMs >= 0");
                string scope = (string)stamped["cacheScope"];
                Assert.True(scope == CacheHints.Public || scope == CacheHints.Private, method + " scope");
            }
        }

        [Fact]
        public void Tools_list_is_never_advertised_as_publicly_cacheable()
        {
            // It is filtered by the machine owner's permission profile and tool packs.
            // "public" would let a shared cache hand one operator's authorization posture
            // to another.
            var stamped = (JObject)ResultEnvelope.Stamp(new JObject(), McpEra.Modern, "tools/list", new JObject());
            Assert.Equal(CacheHints.Private, (string)stamped["cacheScope"]);
        }

        [Fact]
        public void An_interim_result_is_not_cacheable()
        {
            var stamped = (JObject)ResultEnvelope.Stamp(
                new JObject(), McpEra.Modern, "tools/list", new JObject(), ResultEnvelope.InputRequired);
            Assert.Equal(ResultEnvelope.InputRequired, (string)stamped["resultType"]);
            Assert.Null(stamped["ttlMs"]);
            Assert.Null(stamped["cacheScope"]);
        }

        [Fact]
        public void A_tools_call_is_never_stamped_with_caching_hints()
        {
            var stamped = (JObject)ResultEnvelope.Stamp(
                new JObject { ["content"] = new JArray() }, McpEra.Modern, "tools/call", new JObject());
            Assert.Equal(ResultEnvelope.Complete, (string)stamped["resultType"]);
            Assert.Null(stamped["ttlMs"]);
        }

        // ---- discovery --------------------------------------------------------------

        [Fact]
        public void Discover_reports_every_revision_and_identifies_the_server()
        {
            RequestEnvelope env = RequestEnvelope.Read("server/discover", ModernMeta());
            JObject result = DiscoverHandler.Handle(env);

            var versions = new List<string>();
            foreach (JToken t in (JArray)result["supportedVersions"]) versions.Add((string)t);
            Assert.Equal(new List<string>(McpRevision.All), versions);

            Assert.NotNull(result["capabilities"]);
            Assert.False(string.IsNullOrWhiteSpace((string)result["instructions"]));
            Assert.NotNull(result["_meta"]["io.horizunhub/provenance"]);
        }

        [Fact]
        public void Only_finished_extensions_are_advertised()
        {
            JObject advertised = ExtensionRegistry.Advertised();
            Assert.NotNull(advertised[ExtensionRegistry.Tasks]);

            // G05 IS built now, so it is announced - and an announcement is only allowed
            // with something behind it: every mime type it advertises is the mime type
            // of a resource this server lists AND serves. A client that renders the
            // panel finds the app, not a blank.
            JObject ui = Assert.IsType<JObject>(advertised[ExtensionRegistry.Ui]);
            Assert.True(ExtensionRegistry.IsAdvertised(ExtensionRegistry.Ui));
            JArray listed = (JArray)McpResources.List(null)["resources"];
            foreach (JToken mime in (JArray)ui["mimeTypes"])
            {
                JObject app = listed.OfType<JObject>().Single(r => (string)r["mimeType"] == (string)mime);
                JObject read = McpResources.Read(new JObject { ["uri"] = app["uri"] });
                Assert.Equal((string)mime, (string)read["contents"][0]["mimeType"]);
                Assert.False(string.IsNullOrWhiteSpace((string)read["contents"][0]["text"]));
            }
            foreach (McpExtension e in ExtensionRegistry.All)
                Assert.Equal(e.Implemented, advertised[e.Id] != null);
        }

        [Fact]
        public void Every_unadvertised_extension_says_what_is_missing()
        {
            foreach (McpExtension e in ExtensionRegistry.All)
                if (!e.Implemented)
                    Assert.False(string.IsNullOrWhiteSpace(e.Pending),
                                 e.Id + " is withheld without saying why");
        }

        [Fact]
        public void The_legacy_capability_block_still_advertises_logging()
        {
            // logging/setLevel exists in every legacy revision. Dropping the capability
            // there would be removing a method those clients may legitimately call.
            JObject legacy = DiscoverHandler.Capabilities(McpEra.Legacy);
            Assert.NotNull(legacy["logging"]);
            Assert.Null(legacy["extensions"]);

            JObject modern = DiscoverHandler.Capabilities(McpEra.Modern);
            Assert.Null(modern["logging"]);
            Assert.NotNull(modern["extensions"]);
        }

        // ---- the lifecycle gate, per era -------------------------------------------

        [Fact]
        public void A_modern_request_needs_no_handshake()
        {
            var session = new McpSession();
            string error;
            Assert.True(session.Allows("tools/list", false, McpEra.Modern, out error));
            Assert.Null(error);
        }

        [Fact]
        public void A_legacy_request_is_still_gated_exactly_as_before()
        {
            var session = new McpSession();
            string error;
            Assert.False(session.Allows("tools/list", false, McpEra.Legacy, out error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void A_modern_request_may_not_ask_for_the_handshake()
        {
            var session = new McpSession();
            string error;
            Assert.False(session.Allows("initialize", false, McpEra.Modern, out error));
            Assert.Contains("removed the initialization handshake", error);
        }

        [Fact]
        public void Discover_is_admitted_in_any_phase_and_any_era()
        {
            var session = new McpSession();
            string error;
            Assert.True(session.Allows("server/discover", false, McpEra.Modern, out error));
            Assert.True(session.Allows("server/discover", false, McpEra.Legacy, out error));
        }

        // ---- subscriptions -----------------------------------------------------------

        [Fact]
        public void The_filter_is_params_notifications_with_optional_boolean_fields()
        {
            // The contract: params.notifications, an object whose fields are
            // toolsListChanged, promptsListChanged, resourcesListChanged and
            // resourceSubscriptions. All optional; omitting one means not subscribing.
            SubscriptionFilter filter = SubscriptionStream.Read(new JObject
            {
                ["notifications"] = new JObject
                {
                    [SubscriptionStream.ToolsListChanged] = true,
                    [SubscriptionStream.ResourceSubscriptions] =
                        new JArray("file:///project/config.json")
                }
            });

            Assert.True(filter.ToolsListChanged);
            Assert.False(filter.PromptsListChanged);
            Assert.False(filter.ResourcesListChanged);
            Assert.Single(filter.ResourceSubscriptions);
        }

        [Fact]
        public void An_absent_filter_is_legal_and_asks_for_nothing()
        {
            // Every field is optional, so a request with no filter is a legal request for
            // a stream that delivers nothing. Refusing it would reject a conforming client.
            SubscriptionFilter filter = SubscriptionStream.Read(new JObject());
            Assert.True(filter.Empty);
        }

        [Fact]
        public void A_field_of_the_wrong_type_is_refused_rather_than_ignored()
        {
            // Ignoring "toolsListChanged": "yes" produces a subscription the client believes
            // in and the server never feeds, which is the failure mode the filter exists to
            // prevent.
            Assert.Throws<McpError>(() => SubscriptionStream.Read(new JObject
            {
                ["notifications"] = new JObject { [SubscriptionStream.ToolsListChanged] = "yes" }
            }));
        }

        [Fact]
        public void The_old_non_standard_types_array_is_refused_by_name()
        {
            // It was never part of this protocol. Answering "notifications is missing" would
            // send the reader looking for a field they did write, under the wrong key.
            var error = Assert.Throws<McpError>(() => SubscriptionStream.Read(
                new JObject { ["types"] = new JArray(SubscriptionStream.ToolsListChanged) }));
            Assert.Contains("notifications", error.Message);
            Assert.Contains("types", error.Message);
        }

        [Fact]
        public void An_unsupported_type_is_omitted_from_the_acknowledgement_rather_than_refused()
        {
            // "Notification types the server does not support are omitted." That is the
            // protocol's own mechanism, and refusing the whole request instead rejects a
            // conforming client for asking a legal question.
            SubscriptionFilter requested = SubscriptionStream.Read(new JObject
            {
                ["notifications"] = new JObject
                {
                    [SubscriptionStream.ToolsListChanged] = true,
                    [SubscriptionStream.ResourceSubscriptions] = new JArray("file:///a.json")
                }
            });
            SubscriptionFilter honoured = SubscriptionStream.Honour(requested);

            Assert.True(honoured.ToolsListChanged);
            Assert.Empty(honoured.ResourceSubscriptions);
            Assert.Null(honoured.Json()[SubscriptionStream.ResourceSubscriptions]);
            Assert.NotNull(SubscriptionStream.NotHonoured(requested, honoured));
        }

        [Fact]
        public void The_acknowledged_filter_lists_only_what_will_be_delivered()
        {
            // A false field and an absent one mean the same thing, and the acknowledgement
            // is read as the subset that WILL arrive - so writing false into it is
            // technically equivalent and practically misleading.
            var filter = new SubscriptionFilter { ToolsListChanged = true };
            JObject json = filter.Json();
            Assert.True((bool)json[SubscriptionStream.ToolsListChanged]);
            Assert.Null(json[SubscriptionStream.PromptsListChanged]);
        }

        [Fact]
        public void Nothing_is_delivered_before_the_acknowledgement()
        {
            // "The server MUST send notifications/subscriptions/acknowledged as the first
            // message ... and MUST NOT send any notification on the subscription before it."
            var delivered = new List<string>();
            Subscription s = SubscriptionStream.Register(
                "Int64:1", new JValue(1), new SubscriptionFilter { ToolsListChanged = true });
            try
            {
                int early = SubscriptionStream.Publish(
                    SubscriptionStream.ToolsListChanged, new JObject(),
                    (method, body) => delivered.Add(method));
                Assert.Equal(0, early);
                Assert.Empty(delivered);

                SubscriptionStream.Acknowledge(s, (method, body) => delivered.Add(method));
                Assert.Equal(SubscriptionStream.AcknowledgedMethod, delivered[0]);

                int after = SubscriptionStream.Publish(
                    SubscriptionStream.ToolsListChanged, new JObject(),
                    (method, body) => delivered.Add(method));
                Assert.Equal(1, after);
                Assert.Equal("notifications/tools/list_changed", delivered[1]);
            }
            finally { SubscriptionStream.Release("Int64:1"); }
        }

        [Fact]
        public void The_subscription_id_is_the_json_rpc_id_of_the_listen_request()
        {
            // "The value is the JSON-RPC ID of the subscriptions/listen request." A client
            // that sent 1 and looks for 1 must match; publishing this server's internal
            // dedup key ("Int64:1") matched nothing, and looked correct in the log.
            var delivered = new List<JObject>();
            Subscription s = SubscriptionStream.Register(
                "Int64:7", new JValue(7), new SubscriptionFilter { ToolsListChanged = true });
            try
            {
                SubscriptionStream.Acknowledge(s, (method, body) => delivered.Add(body));
                SubscriptionStream.Publish(SubscriptionStream.ToolsListChanged, new JObject(),
                                           (method, body) => delivered.Add(body));

                foreach (JObject message in delivered)
                {
                    JToken id = message["_meta"][RequestEnvelope.SubscriptionIdKey];
                    Assert.Equal(JTokenType.Integer, id.Type);
                    Assert.Equal(7, (int)id);
                }
            }
            finally { SubscriptionStream.Release("Int64:7"); }
        }

        [Fact]
        public void A_type_that_was_not_requested_is_never_delivered()
        {
            // "The server MUST NOT send notification types the client has not explicitly
            // requested." A filter that is not enforced is a decoration.
            Subscription s = SubscriptionStream.Register(
                "Int64:3", new JValue(3), new SubscriptionFilter { ToolsListChanged = true });
            try
            {
                SubscriptionStream.Acknowledge(s, (m, b) => { });
                int reached = SubscriptionStream.Publish(
                    SubscriptionStream.PromptsListChanged, new JObject(), (m, b) => { });
                Assert.Equal(0, reached);
            }
            finally { SubscriptionStream.Release("Int64:3"); }
        }

        [Fact]
        public void A_released_subscription_receives_nothing()
        {
            Subscription s = SubscriptionStream.Register(
                "Int64:2", new JValue(2), new SubscriptionFilter { ToolsListChanged = true });
            SubscriptionStream.Acknowledge(s, (m, b) => { });
            SubscriptionStream.Release("Int64:2");
            int reached = SubscriptionStream.Publish(
                SubscriptionStream.ToolsListChanged, new JObject(), (m, b) => { });
            Assert.Equal(0, reached);
        }

        [Fact]
        public void The_closing_result_carries_the_subscription_id_in_meta_and_no_invented_top_level_fields()
        {
            // The graceful-closure result "carries no method-specific data beyond the
            // standard result fields and subscription metadata". Five top-level fields is a
            // different type from the one a conforming client parses.
            Subscription s = SubscriptionStream.Register(
                "Int64:9", new JValue(9), new SubscriptionFilter { ToolsListChanged = true });
            try
            {
                JObject closed = SubscriptionStream.Closed(s, "the client cancelled it");
                Assert.Equal(9, (int)closed["_meta"][RequestEnvelope.SubscriptionIdKey]);

                foreach (JProperty property in closed.Properties())
                    Assert.Equal("_meta", property.Name);
            }
            finally { SubscriptionStream.Release("Int64:9"); }
        }

        // ---- how a subscription ends -------------------------------------------------

        [Fact]
        public void The_first_cause_recorded_wins()
        {
            // THE DEFECT THIS REPLACES: the cause was read off a process-wide flag when the
            // listener thread woke up. A client cancels, the process starts shutting down a
            // moment later, the thread is scheduled after both - and the subscription
            // reported a server teardown for a stream the client had closed. The two have
            // OPPOSITE obligations, so the wrong one is not a cosmetic error.
            Subscription s = SubscriptionStream.Register(
                "Int64:11", new JValue(11), new SubscriptionFilter { ToolsListChanged = true });
            try
            {
                SubscriptionStream.MarkTermination("Int64:11", SubscriptionStream.ClientCancelled);
                SubscriptionStream.MarkAllTermination(SubscriptionStream.ServerTornDown);

                Assert.Equal(SubscriptionStream.ClientCancelled, SubscriptionStream.CauseOf("Int64:11"));
                Assert.Equal(SubscriptionStream.ClientCancelled, s.TerminationCause);
            }
            finally { SubscriptionStream.Release("Int64:11"); }
        }

        [Fact]
        public void A_subscription_with_no_recorded_cause_reports_none()
        {
            // Not "the client cancelled". Nothing is known, and the cheerful default is how
            // a client learns to distrust the field.
            Subscription s = SubscriptionStream.Register(
                "Int64:12", new JValue(12), new SubscriptionFilter());
            try { Assert.Null(SubscriptionStream.CauseOf("Int64:12")); }
            finally { SubscriptionStream.Release("Int64:12"); }
        }

        [Fact]
        public void A_server_teardown_announces_itself_with_the_request_id()
        {
            // "A server MUST send notifications/cancelled referencing a subscriptions/listen
            // request ID when it tears down that subscription stream."
            var sent = new List<JObject>();
            var methods = new List<string>();
            Subscription s = SubscriptionStream.Register(
                "Int64:13", new JValue(13), new SubscriptionFilter { ToolsListChanged = true });
            try
            {
                SubscriptionStream.AnnounceTeardown(s, "the server is shutting down",
                                                    (method, body) => { methods.Add(method); sent.Add(body); });

                Assert.Single(sent);
                Assert.Equal(SubscriptionStream.CancelledMethod, methods[0]);
                Assert.Equal(13, (int)sent[0]["requestId"]);
                Assert.Equal(13, (int)sent[0]["_meta"][RequestEnvelope.SubscriptionIdKey]);
            }
            finally { SubscriptionStream.Release("Int64:13"); }
        }

        [Fact]
        public void Marking_an_unknown_key_changes_nothing()
        {
            // Cancellation is fire-and-forget and its notification may name anything. A
            // request id that is not a subscription must not throw and must not invent one.
            SubscriptionStream.MarkTermination("Int64:404", SubscriptionStream.ClientCancelled);
            Assert.Null(SubscriptionStream.CauseOf("Int64:404"));
        }

        // ---- provenance (G04) --------------------------------------------------------

        [Fact]
        public void Provenance_never_reports_an_unreadable_build_as_a_clean_one()
        {
            JObject stamp = ProvenanceStamp.Current();
            string commit = (string)stamp["commit"];
            bool clean = (bool)stamp["built_from_clean_tree"];
            if (commit == "unknown" || commit.EndsWith("-dirty"))
                Assert.False(clean);
        }

        [Fact]
        public void Provenance_carries_the_contract_hash_the_two_halves_compare()
        {
            JObject stamp = ProvenanceStamp.Current();
            Assert.Equal(Horizun.Contracts.Contract.Hash, (string)stamp["contract_hash"]);
            Assert.NotNull(stamp["assembly"]);
        }
    }
}
