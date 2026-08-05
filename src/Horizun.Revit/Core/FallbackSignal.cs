// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHEN A CLIENT MAY GENERATE PYTHON, as data rather than as advice.
//
// The fallback policy shipped first as prose in the server's instructions. That
// tells an obedient model what to do and gives a program nothing to branch on -
// the client had to separate four failures by reading English:
//
//   1. this bridge has no typed capability for what you asked   -> Python is right
//   2. your arguments are wrong                                 -> fix them, stay typed
//   3. Revit refused / threw                                    -> report it
//   4. a typed write failed part-way through                    -> DO NOT retry
//
// Case 1 is the only one where writing a script is the correct next move, and
// case 4 is the one where it is actively dangerous: the model may already carry
// half the change, and "re-do it in Python" is a second write dressed as a
// recovery. Telling 1 from 4 by matching error strings is exactly the fragile
// thing this exists to remove.
//
// THE INVARIANT: allowed=true requires write_started=false. It is enforced in the
// constructor rather than trusted, because the expensive mistake here is not a
// missing hint - it is a granted one after a partial write.
//
// This does not make the bridge generate Python. It cannot: there is no model on
// this side to turn an intent into a script. It makes the CLIENT's decision
// reliable, and leaves the generating to the client.
//
// Revit-free: a small immutable record, provable in CI.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class FallbackSignal
    {
        /// <summary>The tool a client should reach for when Allowed. Always the Python one.</summary>
        public const string RecommendedTool = "horizun_execute_python";

        // The reasons a capability gap can take. Named constants rather than free text
        // so a client can switch on them and a typo fails to compile here instead of
        // silently producing a reason nobody matches.
        public const string ReasonUnsupportedCapability = "unsupported_capability";
        public const string ReasonUnsupportedOperation  = "unsupported_operation";
        public const string ReasonUnsupportedKind       = "unsupported_kind";
        public const string ReasonUnsupportedCategory   = "unsupported_category";
        public const string ReasonOutOfContract         = "out_of_contract_combination";

        // The CLOSED set of reasons a capability gap may name. A grant carries one of these
        // and nothing else: a client switches on them, and a reason nobody matches is a
        // silent dead end. Kept as a set so an unknown reason is rejected at the source
        // instead of travelling as an unbranchable string - the same discipline as refusing
        // to report a count the model never confirmed.
        private static readonly System.Collections.Generic.HashSet<string> KnownGapReasons =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                ReasonUnsupportedCapability, ReasonUnsupportedOperation, ReasonUnsupportedKind,
                ReasonUnsupportedCategory, ReasonOutOfContract
            };

        /// <summary>True only for one of the closed-set capability-gap reasons.</summary>
        public static bool IsKnownGapReason(string reason) => reason != null && KnownGapReasons.Contains(reason);

        /// <summary>True only for a pre-write capability gap.</summary>
        public bool IsAllowed { get; private set; }

        /// <summary>Which kind of gap. One of the Reason* constants.</summary>
        public string Reason { get; private set; }

        /// <summary>
        /// Whether anything may already have been written when this failed. ALWAYS false
        /// on an allowed signal - a refusal that reached a transaction is not a capability
        /// gap, it is an outcome the caller has to read.
        /// </summary>
        public bool WriteStarted { get; private set; }

        private FallbackSignal() { }

        /// <summary>
        /// Grant the fallback. Only for a refusal issued before any write, transaction or
        /// document change.
        /// </summary>
        public static FallbackSignal Allowed(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException(
                    "A fallback signal must name its reason - a client is expected to branch on it, and an " +
                    "empty reason is an unbranchable permission.", nameof(reason));

            if (!IsKnownGapReason(reason))
                throw new ArgumentException(
                    "'" + reason + "' is not one of the closed set of capability-gap reasons (" +
                    string.Join(", ", KnownGapReasons) + "). A grant that names a reason no client can match is " +
                    "an unbranchable permission; add the reason to the set deliberately or use an existing one.",
                    nameof(reason));

            return new FallbackSignal { IsAllowed = true, Reason = reason, WriteStarted = false };
        }

        /// <summary>
        /// Refuse the fallback explicitly, saying whether a write may already have landed.
        /// Worth emitting rather than omitting when a caller might otherwise assume a
        /// capability gap - above all after a write that failed part-way.
        /// </summary>
        public static FallbackSignal NotAllowed(string reason, bool writeStarted)
            => new FallbackSignal { IsAllowed = false, Reason = reason, WriteStarted = writeStarted };

        public JObject ToJson()
        {
            return new JObject
            {
                ["recommended_tool"] = RecommendedTool,
                ["allowed"] = IsAllowed,
                ["reason"] = Reason,
                ["write_started"] = WriteStarted,
                ["what_this_means"] = IsAllowed
                    ? "This bridge has NO typed capability for what was asked and refused BEFORE writing " +
                      "anything, so generating minimal Revit Python and running it through " + RecommendedTool +
                      " is the correct next step - do it in this same task rather than answering 'not " +
                      "supported'. Verify from inside the script and return the structured __output__; what " +
                      "comes back is self-reported, not host-verified."
                    : "DO NOT fall back to Python for this failure." + (WriteStarted
                        ? " A write may already have started, so re-doing the operation in Python would be a " +
                          "SECOND write, not a recovery. Read the real state of the model and report it."
                        : " This is not a capability gap - it is something the caller can address with typed " +
                          "arguments, or an outcome to report. Fix the request and stay on the typed command.")
            };
        }
    }
}
