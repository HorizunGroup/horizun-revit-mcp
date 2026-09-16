// -----------------------------------------------------------------------------
// Horizun MCP server - one reading of a request's protocol metadata.
// Original Horizun code.
//
// 2026-07-28 made MCP stateless: there is no initialize, and every request carries
// its own protocol version and the client's capabilities in `_meta`. This is the
// single place that reads them, decides which ERA the request belongs to, and
// says no - with the right code and the right data - when the metadata is not
// usable.
//
// WHY ONE PLACE. The three failure codes this can produce are the ones a client
// is expected to ACT on: -32022 tells it which versions to retry with, -32021
// tells it which capability to declare, -32602 tells it the request was malformed
// and nothing ran. Spreading those across handlers is how one path ends up
// answering -32601 "method not found" for a request that was merely missing a
// field, and the client concludes the server cannot do the thing at all.
//
// WHAT THIS DOES NOT DO. It does not decide whether a method exists, it does not
// touch Revit, and it never changes the legacy path: a request with no modern
// `_meta` reads exactly as it did before this file existed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    /// <summary>
    /// The protocol metadata of one request: which revision it declares, which era
    /// that puts it in, who the client says it is, and what it says it can do.
    /// </summary>
    internal sealed class RequestEnvelope
    {
        public const string ProtocolVersionKey = "io.modelcontextprotocol/protocolVersion";
        public const string ClientInfoKey = "io.modelcontextprotocol/clientInfo";
        public const string ClientCapabilitiesKey = "io.modelcontextprotocol/clientCapabilities";
        public const string LogLevelKey = "io.modelcontextprotocol/logLevel";
        public const string SubscriptionIdKey = "io.modelcontextprotocol/subscriptionId";
        public const string ServerInfoKey = "io.modelcontextprotocol/serverInfo";

        /// <summary>The revision the request declared, or null when it declared none.</summary>
        public string DeclaredVersion { get; private set; }

        /// <summary>Legacy unless the request declared a modern revision in its `_meta`.</summary>
        public McpEra Era { get; private set; }

        /// <summary>True when the request carried modern per-request protocol metadata.</summary>
        public bool CarriesModernMeta { get; private set; }

        public JObject ClientInfo { get; private set; }
        public JObject ClientCapabilities { get; private set; }

        /// <summary>The minimum log level for THIS request, or null: no field, no logs.</summary>
        public string LogLevel { get; private set; }

        public JToken ProgressToken { get; private set; }

        /// <summary>Extension identifiers the client declared, e.g. io.modelcontextprotocol/tasks.</summary>
        public IReadOnlyCollection<string> ClientExtensions { get; private set; }

        private RequestEnvelope()
        {
            Era = McpEra.Legacy;
            ClientExtensions = new string[0];
        }

        /// <summary>Did the client declare this extension on this request?</summary>
        public bool Declares(string extensionId)
        {
            if (string.IsNullOrEmpty(extensionId)) return false;
            foreach (string e in ClientExtensions)
                if (string.Equals(e, extensionId, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// Read a request's protocol metadata.
        ///
        /// Throws <see cref="McpDataError"/> when the metadata is present but unusable,
        /// which is the only way a caller can be told what to retry with. A request with
        /// NO modern metadata is never an error here: it is a legacy request, and the
        /// session's own lifecycle rules decide whether it is allowed.
        /// </summary>
        public static RequestEnvelope Read(string method, JObject prms)
        {
            var env = new RequestEnvelope();
            JObject meta = prms?["_meta"] as JObject;

            if (meta != null) env.ProgressToken = meta["progressToken"];

            // initialize is the legacy handshake by definition. A client sending it has
            // told us its era more clearly than any field could, so nothing below runs.
            if (method == "initialize")
            {
                env.Era = McpEra.Legacy;
                return env;
            }

            JToken versionToken = meta?[ProtocolVersionKey];

            // server/discover exists to be asked BEFORE anything is known, including by a
            // client probing whether this server is modern at all. It is still required
            // to carry the field - the spec says every request is - but the refusal has to
            // hand back the version list, or the probe has learned nothing and the client
            // has no way forward.
            if (versionToken == null)
            {
                if (method == "server/discover")
                    // -32602, because a required field is absent - and with data that does
                    // NOT impersonate an UnsupportedProtocolVersionError, whose `requested`
                    // is typed as a string and would be null here. The version list is what
                    // the probe needs, so it travels under its own name.
                    throw new McpDataError(McpErrorCodes.InvalidParams,
                        "Invalid params: server/discover must carry '" + ProtocolVersionKey + "' in _meta, as every " +
                        "request in this revision does. Nothing was done. The versions this server implements are " +
                        "listed in this error's data; send the request again declaring one of them.",
                        McpErrorCodes.MissingProtocolVersionData(ProtocolVersionKey));

                // No modern metadata at all: an ordinary legacy request.
                return env;
            }

            if (versionToken.Type != JTokenType.String)
                throw new McpDataError(McpErrorCodes.InvalidParams,
                    "Invalid params: '" + ProtocolVersionKey + "' must be a string, not " + versionToken.Type +
                    ". Nothing was done.",
                    McpErrorCodes.UnsupportedProtocolVersionData(null));

            string version = (string)versionToken;
            if (!McpRevision.IsSupported(version))
                throw new McpDataError(McpErrorCodes.UnsupportedProtocolVersion,
                    "Unsupported protocol version: this server does not implement '" + version + "'. Nothing was " +
                    "done. Choose one of the versions in this error's data and send the request again.",
                    McpErrorCodes.UnsupportedProtocolVersionData(version));

            env.DeclaredVersion = version;
            env.CarriesModernMeta = true;
            env.Era = McpRevision.EraOf(version);

            // A LEGACY revision named in a per-request field is a contradiction - that
            // field does not exist in those revisions - but it is a harmless one, and
            // refusing it would break a client that is merely being explicit. It is
            // served with legacy shaping: whatever it is, it is not a 2026-07-28 client.
            //
            // WITH ONE EXCEPTION, AND IT IS NOT A STYLE CHOICE. server/discover is
            // defined by the modern revision. Answering it under a legacy one produces a
            // result that conforms to nothing: ResultEnvelope correctly leaves legacy
            // results untouched, so it would carry no resultType and none of the caching
            // hints the spec requires on EVERY complete server/discover result. The
            // refusal below is the one the spec designs for this - it names the versions
            // that do implement the method, so a client that picked an old one off our
            // own supportedVersions list learns what to retry with.
            if (env.Era == McpEra.Legacy)
            {
                if (method == "server/discover")
                    throw new McpDataError(McpErrorCodes.UnsupportedProtocolVersion,
                        "Unsupported protocol version: server/discover is defined by " + McpRevision.Latest +
                        " and this request declared '" + version + "', which has no such method. Nothing was " +
                        "done. This server does implement '" + version + "' - through the initialize handshake " +
                        "that revision specifies - so either send initialize, or send this request again " +
                        "declaring " + McpRevision.Latest + ".",
                        McpErrorCodes.UnsupportedProtocolVersionData(version));
                return env;
            }

            // From here on the request IS modern, and the modern requirements apply.
            // A MISSING FIELD IS A MALFORMED REQUEST, NOT A MISSING CAPABILITY, and the two
            // now carry different data. The client's next move here is to send the field -
            // not to go and declare something in it.
            JToken capabilitiesToken = meta[ClientCapabilitiesKey];
            if (capabilitiesToken == null)
                throw new McpDataError(McpErrorCodes.InvalidParams,
                    "Invalid params: a " + version + " request must carry '" + ClientCapabilitiesKey +
                    "' in _meta, even when empty ({}). It is how this server knows what it may return to you " +
                    "without guessing. Nothing was done.",
                    McpErrorCodes.MalformedMetadata(ClientCapabilitiesKey, "a ClientCapabilities object, {} when empty"));
            if (capabilitiesToken.Type != JTokenType.Object)
                throw new McpDataError(McpErrorCodes.InvalidParams,
                    "Invalid params: '" + ClientCapabilitiesKey + "' must be an object, not " +
                    capabilitiesToken.Type + ". Nothing was done.",
                    McpErrorCodes.MalformedMetadata(ClientCapabilitiesKey, "a ClientCapabilities object"));
            env.ClientCapabilities = (JObject)capabilitiesToken;

            JToken infoToken = meta[ClientInfoKey];
            if (infoToken != null && infoToken.Type == JTokenType.Object) env.ClientInfo = (JObject)infoToken;

            JToken levelToken = meta[LogLevelKey];
            if (levelToken != null && levelToken.Type == JTokenType.String) env.LogLevel = (string)levelToken;

            env.ClientExtensions = ReadExtensions(env.ClientCapabilities);
            return env;
        }

        private static IReadOnlyCollection<string> ReadExtensions(JObject capabilities)
        {
            var found = new List<string>();
            JObject extensions = capabilities?["extensions"] as JObject;
            if (extensions == null) return found;
            foreach (JProperty p in extensions.Properties())
                if (!string.IsNullOrEmpty(p.Name)) found.Add(p.Name);
            return found;
        }

        /// <summary>
        /// Refuse a modern request that needs a capability the client did not declare.
        /// Never call this on a legacy request: those declare capabilities once, at
        /// initialize, and the session remembers them.
        /// </summary>
        public void RequireExtension(string extensionId, string why)
        {
            if (Declares(extensionId)) return;
            throw new McpDataError(McpErrorCodes.MissingRequiredClientCapability,
                "This request needs the '" + extensionId + "' extension, and your capabilities for this request " +
                "did not declare it. " + why + " Nothing was done. Declare it under " +
                "_meta['" + ClientCapabilitiesKey + "'].extensions and send the request again - the object in " +
                "this error's data is exactly what to merge into your capabilities.",
                McpErrorCodes.RequiredExtension(extensionId));
        }
    }
}
