// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHO IS HOLDING WHAT, WITHOUT TAKING ANYTHING.
//
// Until now the only way this bridge could say how many elements were borrowed
// was to RELINQUISH them - the one call that answers the question also changes
// the model, and changes it for everyone on the team. A question you cannot ask
// without altering the answer is not a diagnosis.
//
// GetCheckoutStatus is read-only, and this is the arithmetic over it. Four
// states, and the invariant that they add up:
//
//     owned_by_me + owned_by_others + not_owned + unreadable == scanned
//
// That equation is not decoration. It is the only thing standing between a
// census and the failure mode where an element whose status threw silently
// becomes a "not owned" - which reads as "free to edit" about an element
// somebody else has open.
//
// AND A NON-WORKSHARED DOCUMENT IS NOT A WORKSHARING PROBLEM. It has no
// ownership at all, so ownership is null with a reason rather than four zeros -
// four zeros are a census that ran and found nothing, which is a different
// claim about a different file.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CheckoutState
    {
        public const string Me = "owned_by_me";
        public const string Others = "owned_by_others";
        public const string NoOne = "not_owned";
        public const string Unreadable = "unreadable";
    }

    /// <summary>The counts, and the ids a reader would need to go and look.</summary>
    public sealed class OwnershipTally
    {
        public long Scanned;
        public long OwnedByMe;
        public long OwnedByOthers;
        public long NotOwned;
        /// <summary>The status read threw. NOT free, NOT owned: unknown.</summary>
        public long Unreadable;

        /// <summary>Owner name to how many elements they hold. Others only.</summary>
        public Dictionary<string, long> ByOwner = new Dictionary<string, long>(StringComparer.Ordinal);
        public List<long> OwnedByOthersIds = new List<long>();

        /// <summary>Records one element. Scanned is incremented HERE, so it can never drift.</summary>
        public void Count(string state, string owner, long id)
        {
            Scanned++;
            if (state == CheckoutState.Me) { OwnedByMe++; return; }
            if (state == CheckoutState.NoOne) { NotOwned++; return; }
            if (state == CheckoutState.Unreadable) { Unreadable++; return; }

            OwnedByOthers++;
            OwnedByOthersIds.Add(id);
            // An element owned by somebody whose name would not read is STILL
            // owned. Dropping it here would lose it from the by-owner breakdown
            // while it stayed in the total, and the two would stop agreeing.
            string key = string.IsNullOrWhiteSpace(owner) ? "(owner name unreadable)" : owner;
            long had;
            ByOwner[key] = ByOwner.TryGetValue(key, out had) ? had + 1 : 1;
        }
    }

    public static class OwnershipCensus
    {
        public const string Means =
            "a census taken WITHOUT relinquishing anything - GetCheckoutStatus reads, it does not take. An " +
            "element owned by somebody else is a fact about who is working, not a defect: no standard was " +
            "supplied that makes borrowing wrong, and a colleague with a wall open at 11am has broken nothing.";

        /// <summary>The four states must account for every element scanned.</summary>
        public static bool Balances(OwnershipTally t)
        {
            if (t == null) return false;
            return t.OwnedByMe + t.OwnedByOthers + t.NotOwned + t.Unreadable == t.Scanned;
        }

        /// <summary>
        /// Null when nothing was scanned - never 0. A census with nothing in it has
        /// not found a model where nobody is holding anything.
        /// </summary>
        public static double? ShareOwnedByOthers(OwnershipTally t)
        {
            if (t == null || t.Scanned <= 0) return null;
            return Math.Round(t.OwnedByOthers * 100.0 / t.Scanned, 4);
        }

        public static string Note(OwnershipTally t)
        {
            if (t == null) return "no ownership census was taken.";
            if (t.Scanned == 0)
                return "no element was scanned, so who owns what is UNKNOWN. This is not a model where " +
                       "nothing is borrowed.";

            string s = t.OwnedByOthers == 0
                ? ("none of the " + t.Scanned + " element(s) scanned is held by another user.")
                : (t.OwnedByOthers + " of " + t.Scanned + " element(s) scanned are held by " +
                   t.ByOwner.Count + " other user(s).");

            if (t.Unreadable > 0)
                s += " " + t.Unreadable + " element(s) would not report a checkout status; they are counted " +
                     "apart and are NOT free to edit - unknown is not unowned - so every count here is a " +
                     "LOWER BOUND.";
            return s;
        }

        /// <summary>
        /// The answer for a document that was never workshared. Ownership is null
        /// with a reason, because four zeros are a census that ran, and this one
        /// could not run at all.
        /// </summary>
        public static JObject NotApplicable(string reason)
        {
            return new JObject
            {
                ["status"] = "not_applicable",
                ["reason"] = reason,
                ["means"] = "there is no ownership to report, which is different from an ownership census " +
                            "that found nothing. No count here is zero; every count here is absent."
            };
        }

        /// <summary>Owners largest first, ties by name so two runs of one model agree.</summary>
        public static List<KeyValuePair<string, long>> OwnersRanked(OwnershipTally t)
        {
            var rows = new List<KeyValuePair<string, long>>();
            if (t == null || t.ByOwner == null) return rows;
            rows.AddRange(t.ByOwner);
            rows.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            return rows;
        }

        public static JObject ToJson(OwnershipTally t)
        {
            if (t == null) return null;
            var owners = new JArray();
            foreach (KeyValuePair<string, long> kv in OwnersRanked(t))
                owners.Add(new JObject { ["owner"] = kv.Key, ["elements"] = kv.Value });

            return new JObject
            {
                ["status"] = "ok",
                ["elements_scanned"] = t.Scanned,
                ["elements_owned_by_me"] = t.OwnedByMe,
                ["elements_owned_by_others"] = t.OwnedByOthers,
                ["elements_not_owned"] = t.NotOwned,
                ["elements_unreadable"] = t.Unreadable,
                // Published, not merely asserted in a comment. If this is ever false
                // the counts disagree with themselves and the reader must know.
                ["counts_balance"] = Balances(t),
                ["share_owned_by_others_percent"] = ShareOwnedByOthers(t),
                ["counts_are_exact"] = t.Unreadable == 0,
                ["by_owner"] = owners,
                ["note"] = Note(t),
                ["means"] = Means
            };
        }
    }
}
