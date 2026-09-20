// -----------------------------------------------------------------------------
// Horizun MCP server - server/discover. Original Horizun code.
//
// 2026-07-28 replaced the initialize handshake with a request a client MAY send
// before anything else, and which a server MUST implement. It answers three
// questions in one round trip: which protocol revisions this server speaks, what
// it can do, and what it is.
//
// It is also the stdio backward-compatibility probe. A dual-era client sends it
// first: a DiscoverResult (or a recognised modern error) means modern; anything
// else means fall back to initialize. That is why the refusal for a request
// missing its protocol field carries the version list in its `data` - see
// RequestEnvelope. A probe that learns nothing has not probed.
//
// WHAT HORIZUN ADDS, AND WHERE. Everything the spec defines lives at the top
// level. Everything of ours - the provenance stamp, the discipline map, the
// measured discovery cost - lives under an `io.horizunhub/` key in `_meta`,
// because the io.modelcontextprotocol/ prefix is reserved and a server that
// invents fields in reserved space is a server clients learn to distrust.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal static class DiscoverHandler
    {
        /// <summary>
        /// Answer server/discover. The caller stamps resultType and the caching hints
        /// through ResultEnvelope, exactly as it does for every other modern result -
        /// discover is not a special case in that respect and must not become one.
        /// </summary>
        public static JObject Handle(RequestEnvelope envelope)
        {
            var supported = new JArray();
            foreach (string r in McpRevision.All) supported.Add(r);

            return new JObject
            {
                ["supportedVersions"] = supported,
                ["capabilities"] = Capabilities(McpEra.Modern),
                ["instructions"] = ServerInstructions.Text,
                ["_meta"] = new JObject
                {
                    ["io.horizunhub/provenance"] = ProvenanceStamp.Current(),
                    ["io.horizunhub/discovery"] = Discovery(envelope)
                }
            };
        }

        /// <summary>
        /// The capability block, per era.
        ///
        /// Two differences, and both are the spec rather than taste:
        ///   - `logging` is not advertised to a modern client. logging/setLevel was
        ///     removed; the level is a per-request `_meta` field now, and a server that
        ///     still claimed the capability would be claiming a method it does not have.
        ///   - `extensions` exists only in the modern schema, and carries only what
        ///     ExtensionRegistry says is finished.
        /// </summary>
        public static JObject Capabilities(McpEra era)
        {
            // THE listChanged FLAGS COME FROM THE ONE SET THAT DECIDES WHAT IS EMITTED.
            // Writing them as literals here is how a server comes to advertise a change
            // notification it has no producer for - and, worse, to acknowledge a
            // subscription to it. SubscriptionStream.Emits is that set.
            var capabilities = new JObject
            {
                ["tools"] = new JObject
                {
                    ["listChanged"] = SubscriptionStream.Emits(SubscriptionStream.ToolsListChanged)
                },
                ["resources"] = new JObject
                {
                    ["subscribe"] = false,
                    ["listChanged"] = SubscriptionStream.Emits(SubscriptionStream.ResourcesListChanged)
                },
                ["prompts"] = new JObject
                {
                    ["listChanged"] = SubscriptionStream.Emits(SubscriptionStream.PromptsListChanged)
                },
                ["completions"] = new JObject()
            };

            if (era == McpEra.Legacy)
            {
                capabilities["logging"] = new JObject();
                return capabilities;
            }

            capabilities["extensions"] = ExtensionRegistry.Advertised();
            return capabilities;
        }

        /// <summary>
        /// G03's half of discovery: what the packs are, and what the selection in force
        /// is costing. A client cannot change the selection from here - that is an owner
        /// decision in settings - but it can see which disciplines exist and tell its
        /// user that this session is scoped.
        /// </summary>
        private static JObject Discovery(RequestEnvelope envelope)
        {
            bool tasks = envelope != null && envelope.Declares(ExtensionRegistry.Tasks);
            return new JObject
            {
                ["disciplines"] = DiscoveryCost.Disciplines(),
                ["cost"] = DiscoveryCost.Measure(advertiseTaskSupport: tasks),
                ["means"] =
                    "tool packs scope this session to the disciplines its owner selected. The cost block is " +
                    "measured by serialising the same tools/list builder the server answers with, at the same " +
                    "permission profile, with and without the pack restriction."
            };
        }
    }
}
