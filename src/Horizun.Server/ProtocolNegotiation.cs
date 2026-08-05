// -----------------------------------------------------------------------------
// Horizun MCP server - protocol negotiation as a rule, not a line in a switch.
// Original Horizun code.
//
// The first slice of isolating the protocol layer (backlog 5.8): the one decision
// that changes when the MCP spec revs - WHICH version to answer - extracted where
// it can be golden-tested. The full adapter comes later; this is the piece that
// must not be wrong in the meantime, because a bad negotiation answer breaks
// every client at the first message.
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
        /// Every revision this server implements. 2026-07-28 is deliberately absent: it is
        /// still RC upstream and changes discovery and negotiation - it gets adopted behind
        /// the full adapter, not by adding a string here.
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
