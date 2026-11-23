// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// CT-P1-1b: A LOAN OF EXACTLY THE ELEMENTS YOU WILL TOUCH.
//
// In a shared model the usual ways of getting ownership are blunt: a workset is taken
// whole (renaming one took ownership of 14,697 elements) or an edit borrows whatever it
// happens to reach. operation=borrow asks the central for the ids the caller names and
// nothing else, and the loan is easy to give back: horizun_save_document
// operation=sync_with_central relinquish=borrowed_only returns exactly the elements
// borrowed one by one and keeps the worksets, and relinquish=all returns everything.
//
// The decisions are here, Revit-free: which of the named elements can be asked for, and
// whether what the central answered re-reads as the loan the plan promised. An element
// somebody else holds is never forced, and an element whose status would not read is
// unknown - not free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class BorrowRules
    {
        public const int MaxIds = 500;

        public const string Missing = "missing";
        public const string AlreadyMine = "already_mine";
        public const string Borrowable = "borrowable";
        public const string HeldByOther = "held_by_other";
        public const string Unreadable = "unreadable";

        public const string ReturnsAt =
            "Borrowed elements go back with horizun_save_document operation=sync_with_central relinquish=borrowed_only " +
            "(only these, worksets stay) or relinquish=all, or with operation=relinquish_own. Until then they are held " +
            "against everyone else.";

        /// <summary>Null when the request is acceptable, else why not.</summary>
        public static string Validate(IList<long> ids)
        {
            if (ids == null || ids.Count == 0) return "operation=borrow needs element_ids: the elements you will touch, and only those.";
            if (ids.Count > MaxIds) return "borrow takes at most " + MaxIds + " ids per call (" + ids.Count + " given); split them.";
            if (ids.Any(i => i <= 0)) return "element ids must be positive.";
            return null;
        }

        /// <summary>The class of one element, from whether it exists and its CheckoutState.</summary>
        public static string Classify(bool exists, string checkoutState)
        {
            if (!exists) return Missing;
            switch (checkoutState)
            {
                case CheckoutState.Me: return AlreadyMine;
                case CheckoutState.NoOne: return Borrowable;
                case CheckoutState.Others: return HeldByOther;
                default: return Unreadable;
            }
        }

        /// <summary>
        /// Did the loan re-read as promised? <paramref name="asked"/> are the ids sent to the central,
        /// <paramref name="returned"/> the ids it said it checked out, <paramref name="stateAfter"/> the
        /// CheckoutState read back for each asked id. The central's answer alone proves nothing: the
        /// re-read decides. An asked id that does not read as owned is NOT borrowed (the central refuses
        /// an element that is out of date or that somebody requested first), reported with that fact.
        /// </summary>
        public static BorrowVerdict Verify(ICollection<long> asked, ICollection<long> returned,
                                           IDictionary<long, string> stateAfter)
        {
            var v = new BorrowVerdict();
            var ret = new HashSet<long>(returned ?? new long[0]);
            foreach (long id in (asked ?? new long[0]).OrderBy(x => x))
            {
                string st;
                if (stateAfter == null || !stateAfter.TryGetValue(id, out st)) st = CheckoutState.Unreadable;
                if (st == CheckoutState.Me) v.Borrowed.Add(id);
                else if (st == CheckoutState.Unreadable) v.Unmeasured.Add(id);
                else v.NotBorrowed.Add(id);
                if (ret.Contains(id) && st != CheckoutState.Me && st != CheckoutState.Unreadable)
                    v.Problems.Add("element " + id + " was reported checked out but re-reads as " + st);
            }
            if (v.Unmeasured.Count > 0)
                v.Problems.Add(v.Unmeasured.Count + " element(s) would not report a checkout status after the call: unknown, not borrowed");
            v.Verified = v.Problems.Count == 0 && v.Unmeasured.Count == 0;
            return v;
        }
    }

    public sealed class BorrowVerdict
    {
        public readonly List<long> Borrowed = new List<long>();
        public readonly List<long> NotBorrowed = new List<long>();
        public readonly List<long> Unmeasured = new List<long>();
        public readonly List<string> Problems = new List<string>();
        public bool Verified;
    }
}
