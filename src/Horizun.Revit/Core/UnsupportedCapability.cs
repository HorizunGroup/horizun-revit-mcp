// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// "This bridge cannot do that at all" - as a TYPE, so it survives the trip out.
//
// The typed commands validate an action by throwing, catching, and keeping the
// message. That flattens two very different refusals into one string: "your
// argument is wrong, fix it and retry typed" and "no typed capability exists for
// this, Python is the way". A client separating those by reading English is the
// fragile arrangement FallbackSignal exists to remove, so the distinction has to
// survive the catch - which means it has to be a type, not a sentence.
//
// Throw it ONLY where the command has decided it cannot do the job at all, and
// only before any transaction is open. Everything a caller could fix by sending
// different arguments stays an ordinary ArgumentException.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public sealed class UnsupportedCapability : Exception
    {
        /// <summary>One of the FallbackSignal.Reason* constants.</summary>
        public string Reason { get; }

        public UnsupportedCapability(string message, string reason) : base(message)
        {
            // An unspecified reason defaults to the generic capability gap - that is a
            // legitimate "I don't have a more specific word". But a NON-EMPTY reason that is
            // not in the closed set is a typo or an invented category, and it must fail here,
            // at the throw site, rather than travel as a string no client can branch on.
            if (string.IsNullOrWhiteSpace(reason))
            {
                Reason = FallbackSignal.ReasonUnsupportedCapability;
                return;
            }
            if (!FallbackSignal.IsKnownGapReason(reason))
                throw new ArgumentException(
                    "'" + reason + "' is not a known capability-gap reason. Use one of FallbackSignal's Reason* " +
                    "constants, or pass null for the generic gap.", nameof(reason));
            Reason = reason;
        }

        /// <summary>
        /// The reason carried by this exception, or null when it is any other kind of
        /// failure. Written once here so no command has to remember the cast.
        /// </summary>
        public static string ReasonOf(Exception ex) => (ex as UnsupportedCapability)?.Reason;
    }
}
