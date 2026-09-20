// -----------------------------------------------------------------------------
// Horizun MCP server - the one place a result is shaped for its era.
// Original Horizun code.
//
// 2026-07-28 requires every result to carry `resultType`, asks servers to
// identify themselves in each result's `_meta`, and requires caching hints on six
// list/read methods. 2025-11-25 requires none of that and has clients that will
// see anything new as noise at best.
//
// THE PROPERTY THIS FILE EXISTS TO HOLD: a legacy result leaves here byte for
// byte as the handler produced it. Not "equivalent", not "the same plus a field
// clients ignore" - identical. Every MCP client installed today is legacy, so a
// regression here is a regression for everybody, and it would be invisible from
// this side because the shape would still be valid JSON.
//
// So Stamp() takes the era and branches on it once, at the top, and the legacy
// branch is a return.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal static class ResultEnvelope
    {
        public const string Complete = "complete";
        public const string InputRequired = "input_required";

        /// <summary>The Tasks extension's own result type. See ModernTasks.</summary>
        public const string Task = "task";

        private const string ServerName = "horizun-mcp";

        /// <summary>
        /// Shape a handler's result for the era of the request that produced it.
        ///
        /// <paramref name="resultType"/> defaults to "complete"; a handler that is
        /// returning a task handle passes <see cref="Task"/>, and one that needs more
        /// input passes <see cref="InputRequired"/>. Interim results are NOT cacheable
        /// and get no hints, which the spec states and this enforces rather than trusts.
        /// </summary>
        public static JToken Stamp(JToken result, McpEra era, string method, JObject requestParams,
                                   string resultType = Complete)
        {
            // ---- the legacy path. Nothing happens here, on purpose. ----
            if (era != McpEra.Modern) return result;

            // A handler that returned something other than an object (nothing does today)
            // cannot carry protocol fields, and inventing a wrapper would change what the
            // client asked for into something else. Pass it through untouched.
            JObject obj = result as JObject;
            if (obj == null) return result;

            obj["resultType"] = resultType;

            JObject meta = obj["_meta"] as JObject;
            if (meta == null)
            {
                meta = new JObject();
                obj["_meta"] = meta;
            }
            meta[RequestEnvelope.ServerInfoKey] = new JObject
            {
                ["name"] = ServerName,
                ["version"] = (string)ProvenanceStamp.Current()["version"]
            };

            // G04: the bytes that produced this answer, not just the number on them.
            // Under a vendor-prefixed key, because the io.modelcontextprotocol/ prefix is
            // reserved for the spec and this is ours.
            meta["io.horizunhub/provenance"] = ProvenanceStamp.Compact();

            if (resultType == Complete) CacheHints.Apply(obj, method, requestParams);

            return obj;
        }

        /// <summary>
        /// The serverInfo block on its own, for the one result that is all protocol -
        /// server/discover, where it is the point rather than an annotation.
        /// </summary>
        public static JObject ServerInfo() => new JObject
        {
            ["name"] = ServerName,
            ["version"] = (string)ProvenanceStamp.Current()["version"]
        };
    }
}
