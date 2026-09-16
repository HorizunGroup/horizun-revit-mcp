// -----------------------------------------------------------------------------
// Horizun MCP server - subscriptions/listen over stdio. Original Horizun code.
//
// 2026-07-28 replaced the HTTP GET endpoint and resources/subscribe with a single
// long-lived request: the client asks to listen with a NOTIFICATION FILTER, the
// server acknowledges the subset it will honour, and the response to the request
// arrives at the end, when the stream closes.
//
// WRITTEN AGAINST THE PUBLISHED CONTRACT, not against what was here before. The
// first version of this file invented `params.types` as an array of names. That
// is not the protocol: the protocol is `params.notifications`, an object whose
// fields are `toolsListChanged`, `promptsListChanged`, `resourcesListChanged` and
// `resourceSubscriptions`, all optional, where omitting a field means not
// subscribing to it. A conforming client's request would have been REFUSED by the
// old reader, which is the worst kind of incompatibility: the client is right and
// gets an error that reads as its own fault.
//
// THE THREE RULES THIS FILE HOLDS, each of which the spec states and each of
// which was missing:
//
//   1. The acknowledgement is the FIRST message on the subscription.
//      `notifications/subscriptions/acknowledged` MUST arrive before any
//      notification belonging to that subscription, and it carries the subset the
//      server agreed to honour. A client that never receives it cannot tell a
//      working subscription from a silent one.
//
//   2. The server MUST NOT send notification types the client did not request.
//      Not "should not": a filter that is ignored makes the filter a decoration.
//
//   3. The subscription id IS THE JSON-RPC ID OF THE LISTEN REQUEST, with the
//      type the client sent. The previous version published this server's internal
//      dedup key - "Int64:1" - so a client that sent `"id": 1` and looked for `1`
//      would never have matched a single notification. The internal key still
//      exists, for the in-flight table; it no longer leaves the process.
//
// ORDERING IS ENFORCED WITH A LOCK, NOT WITH HOPE. Acknowledge and Publish both
// write while holding the same gate, so no notification can overtake the
// acknowledgement of the subscription it belongs to. The writes are small and the
// writer serialises internally anyway; the alternative - set a flag, then write -
// leaves exactly the window the spec forbids.
//
// WHAT IS NOT HONOURED HERE, and why it is OMITTED rather than refused.
// `resourceSubscriptions` asks for per-resource updates. This server's resources
// are derived from the compiled contract and from local settings, so an individual
// resource cannot change under a running process - a per-resource subscription
// would stay silent forever, which is indistinguishable from one that works. The
// spec's own mechanism for this is the acknowledgement: "Notification types the
// server does not support are omitted." So it is omitted, and the reason travels
// in a vendor-prefixed _meta key where a human will find it. Refusing the whole
// request - which is what this file used to do - rejects a conforming client for
// asking a legal question.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    /// <summary>
    /// The notification filter of one subscriptions/listen request.
    ///
    /// Field names are the protocol's, deliberately: this object is read straight from
    /// `params.notifications` and written straight back into the acknowledgement, and a
    /// translation layer between two identical shapes is a place for them to diverge.
    /// </summary>
    internal sealed class SubscriptionFilter
    {
        public bool ToolsListChanged;
        public bool PromptsListChanged;
        public bool ResourcesListChanged;

        /// <summary>Resource URIs the client wants updates for. Never honoured here; see the header.</summary>
        public readonly List<string> ResourceSubscriptions = new List<string>();

        public bool Wants(string type)
        {
            switch (type)
            {
                case SubscriptionStream.ToolsListChanged: return ToolsListChanged;
                case SubscriptionStream.PromptsListChanged: return PromptsListChanged;
                case SubscriptionStream.ResourcesListChanged: return ResourcesListChanged;
                default: return false;
            }
        }

        public bool Empty =>
            !ToolsListChanged && !PromptsListChanged && !ResourcesListChanged &&
            ResourceSubscriptions.Count == 0;

        /// <summary>
        /// The filter as the protocol writes it: ONLY the fields that are on.
        ///
        /// "Omitting a field is equivalent to not subscribing to that notification type",
        /// so a false field and an absent one mean the same thing - and the acknowledgement
        /// is read as the subset that WILL be delivered. Writing `"toolsListChanged": false`
        /// into it would be technically equivalent and practically misleading.
        /// </summary>
        public JObject Json()
        {
            var json = new JObject();
            if (ToolsListChanged) json[SubscriptionStream.ToolsListChanged] = true;
            if (PromptsListChanged) json[SubscriptionStream.PromptsListChanged] = true;
            if (ResourcesListChanged) json[SubscriptionStream.ResourcesListChanged] = true;
            if (ResourceSubscriptions.Count > 0)
                json[SubscriptionStream.ResourceSubscriptions] = new JArray(ResourceSubscriptions);
            return json;
        }
    }

    /// <summary>One open subscriptions/listen request.</summary>
    internal sealed class Subscription
    {
        /// <summary>This server's internal dedup key. NEVER leaves the process.</summary>
        public string Key;

        /// <summary>
        /// The JSON-RPC id of the listen request, with the type the client sent it as.
        ///
        /// This is the subscription id the protocol defines, and it is what every
        /// notification on this stream carries. 1 and "1" are different ids in JSON-RPC
        /// and the client gets back exactly what it sent.
        /// </summary>
        public JToken Id;

        public SubscriptionFilter Requested;
        public SubscriptionFilter Honoured;

        /// <summary>
        /// False until the acknowledgement has been written. Nothing is delivered before
        /// it: the spec requires the acknowledgement to be the first message on this
        /// subscription, and a notification that overtakes it is a client that starts
        /// reading a stream it has not been told the shape of.
        /// </summary>
        public bool Acknowledged;

        public int Delivered;
        public DateTimeOffset OpenedAt;

        /// <summary>
        /// WHY this subscription ended, recorded when it is decided rather than read off a
        /// process-wide flag when the listener wakes up.
        ///
        /// The difference is not theoretical. A client cancels; a hundred milliseconds
        /// later the process begins shutting down; the listener thread is scheduled after
        /// both. Reading a global at that point reports a server teardown for a
        /// subscription the client closed - and the two have OPPOSITE obligations: one
        /// sends nothing, the other MUST send notifications/cancelled and SHOULD send a
        /// final response.
        /// </summary>
        public string TerminationCause;
    }

    internal static class SubscriptionStream
    {
        // The protocol's own field names for the notification filter.
        public const string ToolsListChanged = "toolsListChanged";
        public const string PromptsListChanged = "promptsListChanged";
        public const string ResourcesListChanged = "resourcesListChanged";
        public const string ResourceSubscriptions = "resourceSubscriptions";

        public const string AcknowledgedMethod = "notifications/subscriptions/acknowledged";
        public const string CancelledMethod = "notifications/cancelled";

        /// <summary>The client sent notifications/cancelled for this subscription.</summary>
        public const string ClientCancelled = "client_cancelled";

        /// <summary>This server tore the stream down - shutdown, or a policy decision.</summary>
        public const string ServerTornDown = "server_torn_down";

        /// <summary>The channel is gone. Nothing can be written and nothing is claimed.</summary>
        public const string TransportLost = "transport_lost";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Subscription> Open =
            new Dictionary<string, Subscription>(StringComparer.Ordinal);

        /// <summary>The notification method each subscribable type maps to.</summary>
        private static readonly Dictionary<string, string> Methods =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ToolsListChanged] = "notifications/tools/list_changed",
                [PromptsListChanged] = "notifications/prompts/list_changed",
                [ResourcesListChanged] = "notifications/resources/list_changed"
            };

        /// <summary>
        /// The notification types this server actually PRODUCES.
        ///
        /// Having a method name for a type is not the same as having something that emits
        /// it. The only producer in this process is the tool-list monitor: nothing watches
        /// the prompt list or the resource list, because both are derived from the compiled
        /// contract and cannot change under a running process.
        ///
        /// THIS SET IS THE SINGLE SOURCE for two things that must agree: the `listChanged`
        /// flags in the capability block, and the filter the acknowledgement honours. When
        /// they disagree, a client is told in one message that a list never changes and in
        /// the next that it is subscribed to changes in it.
        /// </summary>
        private static readonly HashSet<string> Emitted =
            new HashSet<string>(StringComparer.Ordinal) { ToolsListChanged };

        /// <summary>Does this server ever produce this notification type?</summary>
        public static bool Emits(string type) => type != null && Emitted.Contains(type);

        /// <summary>Every notification type this server can actually deliver.</summary>
        public static IEnumerable<string> Deliverable => Emitted;

        // =====================================================================
        // Reading the request
        // =====================================================================

        /// <summary>
        /// Read `params.notifications` into a filter.
        ///
        /// Every field is optional, so almost nothing here is an error: a filter that asks
        /// for nothing is a legal request for a stream that delivers nothing, and the
        /// acknowledgement says so. What IS an error is a field of the wrong type, because
        /// silently ignoring `"toolsListChanged": "yes"` produces a subscription the client
        /// believes in and the server never feeds.
        /// </summary>
        public static SubscriptionFilter Read(JObject prms)
        {
            JToken notifications = prms?["notifications"];

            if (notifications == null && prms?["types"] != null)
                // THE OLD, NON-STANDARD SPELLING THIS FILE USED TO REQUIRE. Answered by
                // name rather than as "notifications is missing", which would send the
                // reader looking for a field they did write - under the wrong key.
                throw new McpError(McpErrorCodes.InvalidParams,
                    "Invalid params: subscriptions/listen takes 'notifications', an object with the optional " +
                    "fields " + string.Join(", ", Methods.Keys) + " and " + ResourceSubscriptions + ". It does " +
                    "not take a 'types' array - that was never part of this protocol. Nothing was opened.");

            if (notifications == null) return new SubscriptionFilter();

            if (notifications.Type != JTokenType.Object)
                throw new McpError(McpErrorCodes.InvalidParams,
                    "Invalid params: 'notifications' must be an object whose fields are the notification types " +
                    "you want, not " + notifications.Type + ". Nothing was opened.");

            var filter = new SubscriptionFilter();
            var body = (JObject)notifications;

            filter.ToolsListChanged = ReadFlag(body, ToolsListChanged);
            filter.PromptsListChanged = ReadFlag(body, PromptsListChanged);
            filter.ResourcesListChanged = ReadFlag(body, ResourcesListChanged);

            JToken resources = body[ResourceSubscriptions];
            if (resources != null && resources.Type != JTokenType.Null)
            {
                if (resources.Type != JTokenType.Array)
                    throw new McpError(McpErrorCodes.InvalidParams,
                        "Invalid params: '" + ResourceSubscriptions + "' must be an array of resource URIs, not " +
                        resources.Type + ". Nothing was opened.");
                foreach (JToken uri in (JArray)resources)
                {
                    if (uri.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)uri))
                        throw new McpError(McpErrorCodes.InvalidParams,
                            "Invalid params: every entry of '" + ResourceSubscriptions + "' must be a non-empty " +
                            "resource URI. Nothing was opened.");
                    filter.ResourceSubscriptions.Add((string)uri);
                }
            }

            // AN UNKNOWN FIELD IS NOT AN ERROR. A later revision may add a notification
            // type, and a server that refuses the whole request over a field it does not
            // recognise cannot be spoken to by a newer client. It is simply not honoured,
            // and the acknowledgement - which lists only what IS honoured - says so.
            return filter;
        }

        private static bool ReadFlag(JObject body, string field)
        {
            JToken token = body[field];
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type != JTokenType.Boolean)
                throw new McpError(McpErrorCodes.InvalidParams,
                    "Invalid params: 'notifications." + field + "' must be true or false, not " + token.Type +
                    ". Nothing was opened.");
            return (bool)token;
        }

        /// <summary>
        /// What this server will actually deliver out of what was asked for.
        ///
        /// The difference between the two is the whole content of the acknowledgement, and
        /// it is the mechanism the spec provides instead of refusing unsupported types.
        /// </summary>
        public static SubscriptionFilter Honour(SubscriptionFilter requested)
        {
            var honoured = new SubscriptionFilter();
            if (requested == null) return honoured;

            // ONLY WHAT SOMETHING EMITS. A type with a method name and no producer would be
            // acknowledged and then never delivered, which is indistinguishable from a
            // subscription that is working - and it would contradict the listChanged flags
            // this same server publishes in its capabilities.
            honoured.ToolsListChanged = requested.ToolsListChanged && Emits(ToolsListChanged);
            honoured.PromptsListChanged = requested.PromptsListChanged && Emits(PromptsListChanged);
            honoured.ResourcesListChanged = requested.ResourcesListChanged && Emits(ResourcesListChanged);
            // resourceSubscriptions is deliberately not carried across. See the header.
            return honoured;
        }

        /// <summary>What was asked for and will not be delivered, in words, or null.</summary>
        public static string NotHonoured(SubscriptionFilter requested, SubscriptionFilter honoured)
        {
            if (requested == null) return null;
            var reasons = new List<string>();

            if (requested.PromptsListChanged && !Emits(PromptsListChanged))
                reasons.Add(
                    PromptsListChanged + " is not delivered: this server's prompt list is compiled into the " +
                    "binary and nothing can change it while the process runs, so it publishes " +
                    "prompts.listChanged = false and honours no subscription to it");
            if (requested.ResourcesListChanged && !Emits(ResourcesListChanged))
                reasons.Add(
                    ResourcesListChanged + " is not delivered: this server's resource list is derived from the " +
                    "compiled contract, so it publishes resources.listChanged = false and honours no " +
                    "subscription to it");

            if (requested.ResourceSubscriptions.Count > 0)
                reasons.Add(
                    ResourceSubscriptions + " (" + requested.ResourceSubscriptions.Count + " URI(s)) is not " +
                    "delivered by this server: its resources are derived from the compiled contract and from " +
                    "local settings, so an individual resource cannot change under a running process. A " +
                    "subscription to one would stay silent forever, which reads exactly like one that is " +
                    "working. It is omitted from the acknowledged filter rather than refused, which is what " +
                    "the protocol asks a server to do with a type it does not support");
            if (honoured != null && honoured.Empty)
                reasons.Add(
                    "nothing in this filter is deliverable, so this stream will stay open and send nothing " +
                    "until it is cancelled");
            return reasons.Count == 0 ? null : string.Join("; ", reasons);
        }

        // =====================================================================
        // The life of a subscription
        // =====================================================================

        public static Subscription Register(string key, JToken id, SubscriptionFilter requested)
        {
            var subscription = new Subscription
            {
                Key = key,
                Id = id == null ? JValue.CreateNull() : id.DeepClone(),
                Requested = requested ?? new SubscriptionFilter(),
                Honoured = Honour(requested),
                Acknowledged = false,
                OpenedAt = DateTimeOffset.UtcNow
            };
            lock (Gate) Open[key] = subscription;
            return subscription;
        }

        /// <summary>
        /// Send the acknowledgement, and only then let notifications flow to it.
        ///
        /// WRITTEN UNDER THE GATE. Publish takes the same lock to write, so no
        /// notification for this subscription can be interleaved before this line. Setting
        /// a flag and writing afterwards would leave precisely the window the spec forbids,
        /// and it would be a window nobody could reproduce on purpose.
        ///
        /// A change that occurs between Register and here is NOT replayed. The client's
        /// baseline is the state at acknowledgement, which is the state it will read when
        /// it lists; a replay would deliver a change the client cannot tell it already has.
        /// </summary>
        public static void Acknowledge(Subscription subscription, Action<string, JObject> notify)
        {
            if (subscription == null || notify == null) return;

            var meta = new JObject
            {
                [RequestEnvelope.SubscriptionIdKey] = subscription.Id
            };
            string unmet = NotHonoured(subscription.Requested, subscription.Honoured);
            if (unmet != null)
                // A VENDOR-PREFIXED KEY, because io.modelcontextprotocol/ is reserved for
                // the specification and this sentence is ours. It exists because "omitted"
                // is correct and silent: a person reading the acknowledgement should not
                // have to diff it against their own request to find out what was dropped.
                meta["io.horizunhub/notHonoured"] = unmet;

            var body = new JObject
            {
                ["_meta"] = meta,
                ["notifications"] = subscription.Honoured.Json()
            };

            lock (Gate)
            {
                notify(AcknowledgedMethod, body);
                subscription.Acknowledged = true;
            }
        }

        public static void Release(string key)
        {
            if (key == null) return;
            lock (Gate) Open.Remove(key);
        }

        /// <summary>
        /// Record why a subscription is ending. FIRST WRITE WINS.
        ///
        /// That is the whole point: whoever decides first owns the reason. A shutdown that
        /// arrives after a client cancellation must not overwrite it, or the subscription
        /// reports the last thing that happened rather than the thing that ended it.
        /// </summary>
        public static void MarkTermination(string key, string cause)
        {
            if (key == null || cause == null) return;
            lock (Gate)
            {
                Subscription s;
                if (Open.TryGetValue(key, out s) && s.TerminationCause == null)
                    s.TerminationCause = cause;
            }
        }

        /// <summary>Record a cause for every open subscription that has none yet.</summary>
        public static void MarkAllTermination(string cause)
        {
            if (cause == null) return;
            lock (Gate)
                foreach (Subscription s in Open.Values)
                    if (s.TerminationCause == null) s.TerminationCause = cause;
        }

        /// <summary>The recorded cause, or null when nothing recorded one.</summary>
        public static string CauseOf(string key)
        {
            if (key == null) return null;
            lock (Gate)
            {
                Subscription s;
                return Open.TryGetValue(key, out s) ? s.TerminationCause : null;
            }
        }

        /// <summary>
        /// The notifications/cancelled a server MUST send when IT tears a stream down.
        ///
        /// "Servers MUST NOT send notifications/cancelled for any other purpose", so this
        /// method exists only on this path and takes a Subscription rather than a request
        /// id - there is no way to call it for anything else.
        /// </summary>
        public static void AnnounceTeardown(Subscription subscription, string reason,
                                            Action<string, JObject> notify)
        {
            if (subscription == null || notify == null) return;
            notify(CancelledMethod, new JObject
            {
                ["requestId"] = subscription.Id.DeepClone(),
                ["reason"] = reason,
                ["_meta"] = new JObject
                {
                    [RequestEnvelope.SubscriptionIdKey] = subscription.Id.DeepClone()
                }
            });
        }

        public static int OpenCount
        {
            get { lock (Gate) return Open.Count; }
        }

        /// <summary>The subscription type a change notification belongs to, or null.</summary>
        public static string TypeForMethod(string method)
        {
            foreach (KeyValuePair<string, string> pair in Methods)
                if (pair.Value == method) return pair.Key;
            return null;
        }

        /// <summary>
        /// Deliver a change notification to every subscriber that ASKED for it.
        ///
        /// Returns how many it reached. The caller no longer needs that number to decide
        /// whether to broadcast - the legacy broadcast is decided by whether a legacy
        /// handshake happened, not by whether anybody is subscribed - but it is the only
        /// evidence that a change was delivered at all.
        ///
        /// WRITES UNDER THE GATE, for the ordering property described on Acknowledge.
        /// </summary>
        public static int Publish(string type, JObject payload, Action<string, JObject> notify)
        {
            string method;
            if (!Methods.TryGetValue(type ?? "", out method) || notify == null) return 0;

            int reached = 0;
            lock (Gate)
            {
                foreach (Subscription s in Open.Values)
                {
                    // NOT ACKNOWLEDGED YET means not yet delivering: the acknowledgement is
                    // required to be the first message this subscription sees.
                    if (!s.Acknowledged) continue;
                    if (!s.Honoured.Wants(type)) continue;
                    // A type nothing emits cannot be published; Honour never lets one into a
                    // honoured filter, and this is the second lock on the same door.
                    if (!Emits(type)) continue;

                    var body = payload != null ? (JObject)payload.DeepClone() : new JObject();
                    JObject meta = body["_meta"] as JObject;
                    if (meta == null) { meta = new JObject(); body["_meta"] = meta; }
                    meta[RequestEnvelope.SubscriptionIdKey] = s.Id.DeepClone();
                    notify(method, body);
                    s.Delivered++;
                    reached++;
                }
            }
            return reached;
        }

        /// <summary>
        /// The result the listen request answers with when its stream ends.
        ///
        /// SHAPED AS THE SPEC SHAPES IT: the result carries no method-specific data beyond
        /// the standard result fields and the subscription metadata. Everything Horizun
        /// wants to say about the stream - how long it was open, how much it delivered,
        /// why it ended - lives under a vendor-prefixed key inside `_meta`, which is where
        /// a server is allowed to say things. The previous version put five fields at the
        /// top level of the result, which is a different type from the one a conforming
        /// client parses.
        /// </summary>
        public static JObject Closed(Subscription subscription, string reason)
        {
            var meta = new JObject
            {
                [RequestEnvelope.SubscriptionIdKey] =
                    subscription == null ? JValue.CreateNull() : subscription.Id.DeepClone(),
                ["io.horizunhub/subscription"] = new JObject
                {
                    ["notifications"] = subscription == null
                        ? new JObject() : subscription.Honoured.Json(),
                    ["delivered"] = subscription?.Delivered ?? 0,
                    ["openedAt"] = subscription?.OpenedAt.ToString("O"),
                    ["closedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["reason"] = reason,
                    ["means"] =
                        "the stream is over and nothing further will arrive under this subscription id. This " +
                        "response IS the graceful close the protocol describes: a stream that ends without " +
                        "one ended because the transport dropped."
                }
            };
            return new JObject { ["_meta"] = meta };
        }
    }
}
