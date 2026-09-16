// -----------------------------------------------------------------------------
// Horizun MCP server - the revision table. Original Horizun code.
//
// EVERY MCP revision this server implements, and which ERA each one belongs to.
//
// 2026-07-28 removed the initialize handshake. That is not a field rename: a
// server built around "nothing is answered until initialize has been answered"
// and a client that never sends one do not fail loudly, they hang. So the era is
// a first-class fact here rather than a comparison against a string somewhere in
// the message loop, and every decision downstream - whether a result gets a
// resultType, whether tasks/result exists, whether logging/setLevel is a method
// or a per-request field - asks this table instead of re-deriving it.
//
//   LEGACY  (2024-11-05 .. 2025-11-25): initialize, session state, no resultType,
//           no per-request _meta protocol fields, core tasks/result + tasks/list.
//   MODERN  (2026-07-28 and later): stateless, per-request _meta carries the
//           version and the client's capabilities, every result carries
//           resultType, tasks live in an extension.
//
// THE LEGACY SIDE IS FROZEN, DELIBERATELY. Adding modern behaviour must not
// change one byte of what a 2025-11-25 client sees, because every client
// installed today is one of those. That is a property, and ResultEnvelope is
// where it is enforced.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Server.Protocol
{
    /// <summary>Which protocol era a request belongs to. See the file header.</summary>
    internal enum McpEra
    {
        /// <summary>initialize handshake, session state. 2025-11-25 and earlier.</summary>
        Legacy,

        /// <summary>Stateless, per-request _meta. 2026-07-28 and later.</summary>
        Modern
    }

    internal static class McpRevision
    {
        /// <summary>The newest revision this server implements, in any era.</summary>
        public const string Latest = "2026-07-28";

        /// <summary>
        /// The newest LEGACY revision. This is what an initialize handshake that asked
        /// for something we do not have gets answered with - answering a modern version
        /// to a client that sent initialize would be answering in a dialect that has no
        /// initialize in it.
        /// </summary>
        public const string LatestLegacy = "2025-11-25";

        /// <summary>Every revision, newest first. Order is part of the discover answer.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            "2026-07-28",
            "2025-11-25",
            "2025-06-18",
            "2025-03-26",
            "2024-11-05"
        };

        private static readonly HashSet<string> Known = new HashSet<string>(All, StringComparer.Ordinal);

        private static readonly HashSet<string> ModernRevisions =
            new HashSet<string>(StringComparer.Ordinal) { "2026-07-28" };

        public static bool IsSupported(string revision) =>
            revision != null && Known.Contains(revision);

        public static bool IsModern(string revision) =>
            revision != null && ModernRevisions.Contains(revision);

        /// <summary>
        /// The era of a revision this server supports. Callers must have checked
        /// <see cref="IsSupported"/> first: an unknown revision has no era, it has an
        /// UnsupportedProtocolVersionError.
        /// </summary>
        public static McpEra EraOf(string revision) =>
            IsModern(revision) ? McpEra.Modern : McpEra.Legacy;

        /// <summary>
        /// The LEGACY negotiation rule, unchanged: answer what was asked for if we have
        /// it, otherwise the newest legacy revision. A modern revision is deliberately
        /// not reachable from here - a client that sent initialize cannot speak one.
        /// </summary>
        public static string AnswerInitialize(string requested)
        {
            if (requested != null && Known.Contains(requested) && !IsModern(requested)) return requested;
            return LatestLegacy;
        }

        /// <summary>Every legacy revision, for the initialize path and its golden tests.</summary>
        public static IEnumerable<string> LegacyRevisions
        {
            get
            {
                foreach (string r in All)
                    if (!IsModern(r)) yield return r;
            }
        }
    }
}
