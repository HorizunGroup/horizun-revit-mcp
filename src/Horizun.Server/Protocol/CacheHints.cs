// -----------------------------------------------------------------------------
// Horizun MCP server - freshness and scope hints for cacheable results.
// Original Horizun code.
//
// 2026-07-28 requires `ttlMs` and `cacheScope` on every complete result of
// server/discover, tools/list, prompts/list, resources/list,
// resources/templates/list and resources/read.
//
// THE SCOPE FIELD IS A PERMISSIONS DECISION, NOT A PERFORMANCE ONE. "public"
// means any shared cache or gateway may serve this answer to anybody. What this
// server publishes is filtered by the machine owner's permission profile, by the
// selected tool packs, and by whether that owner has granted Python - so the tool
// list of THIS machine is a statement about what its owner authorized. Marking it
// public would let a shared intermediary hand one operator's authorization
// posture to another. Everything that varies with local policy is "private".
//
// THE TTL IS SHORT FOR THE SAME REASON. Settings are re-read on every call, on
// purpose: switching a tool off takes effect on the next call, not the next
// restart. A long freshness hint would quietly reintroduce the restart. The
// listChanged notification still fires and invalidates immediately; the TTL only
// bounds how long a client that missed it can be wrong.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal static class CacheHints
    {
        public const string Public = "public";
        public const string Private = "private";

        /// <summary>
        /// Local policy can change between two calls, and the owner expects that to be
        /// felt on the next call. One minute is long enough to spare a client re-asking
        /// inside one turn and short enough that "I turned that off" is never a
        /// restart-shaped problem.
        /// </summary>
        public const long LocalPolicyTtlMs = 60L * 1000;

        /// <summary>
        /// Text compiled into the binary. It cannot change without the process changing,
        /// so an hour is honest rather than optimistic.
        /// </summary>
        public const long BuildFixedTtlMs = 60L * 60 * 1000;

        /// <summary>Is this method one the spec requires caching hints on?</summary>
        public static bool IsCacheable(string method)
        {
            switch (method)
            {
                case "server/discover":
                case "tools/list":
                case "prompts/list":
                case "resources/list":
                case "resources/templates/list":
                case "resources/read":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A request that is a MULTI ROUND-TRIP RETRY, carrying the client's answers or
        /// the state handle from an earlier interim result.
        ///
        /// Its result depends on those inputs, and they are NOT part of the cache key -
        /// which is the method plus the parameters that affect the result. So two
        /// requests that a cache cannot tell apart can have different right answers, and
        /// the spec says such a result MUST NOT be cached.
        /// </summary>
        public static bool IsRoundTripRetry(JObject requestParams) =>
            requestParams != null &&
            (requestParams["inputResponses"] != null || requestParams["requestState"] != null);

        /// <summary>
        /// Stamp `ttlMs` and `cacheScope` onto a complete result. Silently does nothing
        /// for a method that is not cacheable, so a caller cannot accidentally promise
        /// freshness semantics for a tools/call.
        /// </summary>
        public static void Apply(JObject result, string method, JObject requestParams)
        {
            if (result == null || !IsCacheable(method)) return;

            // A HINT IS AN INVITATION. Omitting the two fields is not a missing feature
            // here: a client that sees no ttlMs treats the result as immediately stale,
            // which is exactly the required behaviour for a round-trip retry. Stamping
            // one would invite the client to serve this answer to a later request whose
            // inputs were different and whose cache key looks identical.
            if (IsRoundTripRetry(requestParams)) return;

            long ttl;
            string scope;
            switch (method)
            {
                case "resources/read":
                    ResourceHint(requestParams?.Value<string>("uri"), out ttl, out scope);
                    break;

                case "prompts/list":
                case "resources/list":
                case "resources/templates/list":
                    // These are derived from the compiled contract, but the tool surface
                    // they describe is filtered by local policy, so they travel together.
                    ttl = LocalPolicyTtlMs;
                    scope = Private;
                    break;

                default: // server/discover, tools/list
                    ttl = LocalPolicyTtlMs;
                    scope = Private;
                    break;
            }

            // The spec allows a server to choose any ttl >= 0 and requires it to be >= 0.
            // Clamping here rather than trusting each call site means a future hint that
            // computes a negative value is a slow answer, never an invalid result.
            result["ttlMs"] = Math.Max(0, ttl);
            result["cacheScope"] = scope;
        }

        private static void ResourceHint(string uri, out long ttl, out string scope)
        {
            switch (uri)
            {
                // Compiled text. Identical for every caller of this build.
                // The MCP App is here too: it is one HTML file baked into the binary,
                // it fetches nothing, and it says nothing about this machine.
                case "ui://horizun/clash-viewer":
                case "horizun://guidance/typed-first":
                case "horizun://workflows/bim-production":
                    ttl = BuildFixedTtlMs;
                    scope = Public;
                    return;

                // The contract is fixed by the build, but it is the full installed
                // surface - including tools this machine's owner has switched off, which
                // is a fact about this machine. Fixed in time, private in scope.
                case "horizun://contract/tools":
                    ttl = BuildFixedTtlMs;
                    scope = Private;
                    return;

                // Both of these answer questions about the machine and the Revit attached
                // to it right now. Nothing about them is shareable or durable.
                case "horizun://security/current-profile":
                case "horizun://build/identity":
                    ttl = 0;
                    scope = Private;
                    return;

                default:
                    // An unknown URI never reaches here (Read throws first). If one ever
                    // did, the safe answer is "do not cache this, and do not share it".
                    ttl = 0;
                    scope = Private;
                    return;
            }
        }
    }
}
