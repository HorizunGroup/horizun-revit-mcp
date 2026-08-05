// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT A FAILED ATOMIC PLAN IS ALLOWED TO CLAIM, as data rather than as a
// sentence a person has to trust.
//
// THE DEFECT THIS EXISTS TO FIX, found in review. ExecutePlan's catch called
// group.RollBack() and returned the fixed prose "EVERY action was rolled back" -
// WITHOUT looking at what RollBack() returned. TransactionGroup.RollBack() hands
// back a TransactionStatus; a value other than RolledBack (Pending, Error) means
// the model's state is UNCERTAIN, not clean. The old message asserted the clean
// case unconditionally, which is the same family of lie as counting Delete()
// calls instead of re-reading the model (see Guard). A live probe that only
// compared a count before and after could pass on a refusal that never reached
// the group at all - a stale token, a confirmation miss, a first-action failure -
// none of which prove a rollback.
//
// THE RULE. A rollback may be CLAIMED complete only when the group's FINAL status
// is RolledBack. Everything else keeps its uncertainty and says so. And every
// reached action reports its own index/key/tool/success/error, so a caller reads
// where the graph got to instead of a summary that could be wrong.
//
// Revit-free on purpose: the classification and the wording are the part whose
// honesty has to be provable in CI without loading Autodesk assemblies. The Revit
// side (Guard.RollBack) converts a TransactionStatus to its name and hands it
// here; the same "is this a confirmed rollback?" question is answered in one place
// for both.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PlanFailure
    {
        /// <summary>The one status name that proves a rollback landed. Anything else is uncertainty.</summary>
        public const string ConfirmedStatus = "RolledBack";

        /// <summary>Marker for "we never called RollBack" - distinct from a RollBack that returned something.</summary>
        public const string NotAttempted = "not_attempted";

        /// <summary>
        /// Is this the status that lets a caller treat the model as clean? Only "RolledBack".
        /// A typo, a Pending, an Error - none of them, and none of them silently.
        /// </summary>
        public static bool IsConfirmedRollback(string statusName)
            => string.Equals(statusName, ConfirmedStatus, StringComparison.Ordinal);

        /// <summary>
        /// The structured diagnostic a failed plan carries. Every input is a primitive or a
        /// JArray the command already built, so this whole shape is unit-testable Revit-free.
        ///
        /// rollback_confirmed is computed from the group's FINAL status, not from the fact
        /// that RollBack was called - what matters to a caller is where the group ended, and
        /// a RollBack that returned Error left the model in a state no one may assume clean.
        /// </summary>
        public static JObject Diagnostic(
            bool transactionGroupStarted,
            string transactionGroupStatus,
            bool rollbackAttempted,
            string rollbackStatus,
            JArray executionTrace,
            string error)
        {
            bool confirmed = IsConfirmedRollback(transactionGroupStatus);
            return new JObject
            {
                ["transaction_group_started"] = transactionGroupStarted,
                ["transaction_group_status"] = transactionGroupStatus,
                ["rollback_attempted"] = rollbackAttempted,
                ["rollback_status"] = rollbackStatus,
                ["rollback_confirmed"] = confirmed,
                ["execution_trace"] = executionTrace ?? new JArray(),
                ["error"] = error
            };
        }

        /// <summary>
        /// The human sentence, honest about which of three worlds this is. A caller branches
        /// on the structured block above; a person reads this, and it must never promise a
        /// clean model it did not see.
        /// </summary>
        public static string Message(JObject diagnostic)
        {
            bool started = Bool(diagnostic, "transaction_group_started");
            bool confirmed = Bool(diagnostic, "rollback_confirmed");
            bool attempted = Bool(diagnostic, "rollback_attempted");
            string status = (string)diagnostic["rollback_status"];
            string groupStatus = (string)diagnostic["transaction_group_status"];
            string error = (string)diagnostic["error"];

            if (!started)
                return "Atomic plan failed before the TransactionGroup began, so no write was started: " + error +
                       " Nothing was committed and nothing was rolled back.";

            if (confirmed)
                return "Atomic plan failed and the TransactionGroup rolled back (Revit reported '" + ConfirmedStatus +
                       "'), so nothing was retained: " + error;

            return "Atomic plan failed and the rollback is UNCERTAIN: after the failure the TransactionGroup's status " +
                   "was '" + groupStatus + "'" +
                   (attempted ? " and RollBack() returned '" + status + "'" : " and RollBack() was not attempted") +
                   ", not '" + ConfirmedStatus + "'. DO NOT assume the model is clean - re-read its real state before " +
                   "any retry: " + error;
        }

        /// <summary>
        /// The honest one-line account of a SINGLE transaction's rollback, for the ordinary
        /// commands that own one Transaction rather than a graph.
        ///
        /// THE DEFECT THIS EXISTS TO FIX, in nine places at once. Every one of them read:
        ///
        ///     if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
        ///     return CommandResult.Fail("... was rolled back; nothing was written: " + ex.Message);
        ///
        /// The status RollBack() returns was discarded and the clean case asserted anyway -
        /// the same lie ExecutePlan told, spelled nine more times. One sentence, built here,
        /// so the wording cannot drift apart again and can be proved without Revit.
        ///
        /// `nothingKept` is the command's own words for what did not survive ("nothing was
        /// written", "nothing was purged"), and it is only ever stated when the rollback is
        /// CONFIRMED.
        /// </summary>
        public static string SingleTransactionOutcome(bool attempted, string statusName, string nothingKept)
        {
            if (!attempted)
                return "The transaction was not open, so no rollback was attempted and " + nothingKept + ".";

            if (IsConfirmedRollback(statusName))
                return "The transaction rolled back (Revit reported " + ConfirmedStatus + "), so " + nothingKept + ".";

            return "The rollback is UNCERTAIN: RollBack() returned '" + statusName + "', not " + ConfirmedStatus +
                   ". DO NOT assume " + nothingKept + " - re-read the real state of the model before any retry.";
        }

        private static bool Bool(JObject o, string key)
            => o != null && o[key] != null && o[key].Type == JTokenType.Boolean && (bool)o[key];
    }
}
