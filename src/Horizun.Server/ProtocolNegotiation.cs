// -----------------------------------------------------------------------------
// Horizun MCP server - protocol negotiation as a rule, not a line in a switch.
// Original Horizun code.
//
// The first slice of isolating the protocol layer (backlog 5.8): the one decision
// that changes when the MCP spec revs - WHICH version to answer - extracted where
// it can be golden-tested, because a bad negotiation answer breaks every client at
// the first message.
//
// THIS IS NOW THE LEGACY HALF, AND ONLY THAT. The full adapter it was waiting for
// arrived in Protocol/: McpRevision holds every revision and its era, and
// 2026-07-28 is served statelessly with no handshake at all. What remains here is
// the initialize answer - the negotiation that only exists in the revisions that
// still have an initialize - and it is deliberately unchanged, because every MCP
// client installed today goes through it.
//
// The rule, from the spec: if the client requests a version the server supports,
// answer THAT version; otherwise answer the latest the server supports, and the
// client decides whether it can live with it. Answering something the client
// never asked for and the server does not support is the failure mode.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Server
{
    internal static class ProtocolNegotiation
    {
        public const string Latest = "2025-11-25";

        /// <summary>
        /// Every revision reachable through an initialize handshake. 2026-07-28 is
        /// deliberately absent and must stay absent: that revision REMOVED initialize, so
        /// answering it to a client that sent one would name a dialect in which the
        /// client's own opening message does not exist. It is served by the modern path -
        /// see Protocol/McpRevision.cs, which holds the complete list for server/discover.
        /// </summary>
        public static readonly IReadOnlyCollection<string> Supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"
        };

        /// <summary>The negotiation rule. Pure, total, and golden-tested.</summary>
        public static string Answer(string requested)
        {
            if (requested != null && ((HashSet<string>)Supported).Contains(requested)) return requested;
            return Latest;
        }
    }
}
