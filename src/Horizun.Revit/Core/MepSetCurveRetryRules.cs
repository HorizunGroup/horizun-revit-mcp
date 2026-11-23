// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// set_curve on MEP runs, tried once more inside the same call.
//
// MEASURED (electrical tower, 2026-10-17): extending a branch to the
// main's axis right after deleting its elbow failed now and then with "the
// family is connected in a network and can no longer keep the connectivity";
// the same arguments in a NEW request passed. The first transaction after a
// delete meets connectivity Revit has not settled yet. Inside the operation the
// write is rolled back, the document regenerated, and the same plan applied once
// more - only when the rollback is confirmed (nothing was written), only for a
// batch that is nothing but set_curve on MEP curves, and only once.
//
// Kept apart so the decision is tested without Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class MepSetCurveRetryRules
    {
        public const int MaxAttempts = 2;

        /// <summary>Try the batch again: attempt (1-based) just failed, its rollback is confirmed, and it is set_curve on MEP runs only.</summary>
        public static bool ShouldRetry(int attempt, IEnumerable<string> operations, bool allTargetsAreMepCurves, bool rolledBack)
        {
            var ops = (operations ?? Enumerable.Empty<string>()).ToList();
            return attempt < MaxAttempts && rolledBack && allTargetsAreMepCurves && ops.Count > 0 &&
                   ops.All(o => string.Equals(o, "set_curve", StringComparison.Ordinal));
        }

        /// <summary>
        /// Revit's own words for a run that cannot keep its connections, in English or Spanish. Null when the text
        /// is in neither: the reason is then UNKNOWN, not "not a connectivity refusal" (review M2: a third language
        /// read as false). It only labels the report; the retry decision never reads it.
        /// </summary>
        public static bool? IsConnectivityRefusal(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (text.IndexOf("keep the connectivity", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("conectividad", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return LooksEnglishOrSpanish(text) ? false : (bool?)null;
        }

        private static bool LooksEnglishOrSpanish(string text)
        {
            string t = " " + text.ToLowerInvariant() + " ";
            string[] words = { " the ", " cannot ", " can't ", " not ", " failed", " returned ", " rolled", " el ", " la ", " no se ", " puede ", " falló" };
            foreach (string w in words) if (t.Contains(w)) return true;
            return false;
        }

        public static string RetryNote(string firstFailure)
            => "The first attempt was rolled back by Revit (" + (firstFailure ?? "no reason given") + "); after regenerating, " +
               "the second attempt committed and is the one verified here.";
    }
}
