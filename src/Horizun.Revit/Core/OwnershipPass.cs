// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// CT-P1-1a: AN OWNERSHIP COUNT THAT FINISHES.
//
// The Revit API has no bulk "elements checked out to me" query: the count is one
// WorksharingUtils.GetCheckoutStatus call per element. horizun_health must stay
// quick, so it used to stop at 500 ms and publish a lower bound that, on a large
// model, never became anything else - every call restarted from the first element
// and stopped in the same place.
//
// This keeps a cursor per document instead. Each call resumes after the last element
// id it examined, adds to the same tally and, when the ids run out, closes the pass:
// complete=true, with the totals of THAT pass. The next call starts a new pass and
// the closed one stays published as last_complete with its time, because a number
// that is complete and old is still a different claim from one that is current.
//
// An element whose status threw is counted apart (unreadable), never as not owned:
// unknown is not free. Revit-free, so every rule here is an ordinary unit test.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class OwnershipPass
    {
        public string Key;
        public long LastId = long.MinValue;
        public long Examined;
        public long Owned;
        public long Unreadable;
        public bool Complete;
        public int Calls;
        public DateTime StartedUtc;
        public DateTime? FinishedUtc;
        /// <summary>The previous finished pass, kept so a new pass never erases a complete answer.</summary>
        public OwnershipPass LastComplete;
    }

    public static class OwnershipPassRules
    {
        /// <summary>status: 1 owned by me, 0 not owned by me, -1 unreadable.</summary>
        public delegate int StatusOf(long id);

        /// <summary>
        /// Continues <paramref name="prior"/> (or starts a pass) over <paramref name="sortedIds"/>,
        /// ascending. <paramref name="outOfTime"/> is polled before every element, so a budget that
        /// is already spent examines nothing and reports exactly that.
        /// </summary>
        public static OwnershipPass Advance(OwnershipPass prior, string key, IReadOnlyList<long> sortedIds,
                                            StatusOf statusOf, Func<bool> outOfTime, DateTime nowUtc)
        {
            OwnershipPass pass;
            if (prior == null || prior.Complete || prior.Key != key)
            {
                OwnershipPass closed = prior != null && prior.Key == key
                    ? (prior.Complete ? prior : prior.LastComplete)
                    : null;
                pass = new OwnershipPass { Key = key, StartedUtc = nowUtc, LastComplete = closed };
                if (closed != null) closed.LastComplete = null;
            }
            else pass = prior;
            pass.Calls++;

            IReadOnlyList<long> ids = sortedIds ?? new long[0];
            int i = FirstAfter(ids, pass.LastId);
            for (; i < ids.Count; i++)
            {
                if (outOfTime != null && outOfTime()) break;
                int st;
                try { st = statusOf(ids[i]); } catch { st = -1; }
                pass.Examined++;
                if (st == 1) pass.Owned++;
                else if (st < 0) pass.Unreadable++;
                pass.LastId = ids[i];
            }
            if (i >= ids.Count)
            {
                pass.Complete = true;
                pass.FinishedUtc = nowUtc;
            }
            return pass;
        }

        /// <summary>Index of the first id greater than <paramref name="last"/> (ids ascending).</summary>
        public static int FirstAfter(IReadOnlyList<long> ids, long last)
        {
            int lo = 0, hi = ids.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (ids[mid] <= last) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        public static string Note(OwnershipPass p)
        {
            if (p == null) return "no ownership pass was run.";
            if (p.Complete)
                return "Every non-type element was checked (" + p.Examined + "), over " + p.Calls + " call(s) to this answer; " +
                       "each element was read once, at the moment its turn came" +
                       (p.Unreadable > 0 ? ", and " + p.Unreadable + " whose status would not read are counted apart (NOT free)." : ".");
            return "Partial: " + p.Examined + " element(s) examined so far and " + p.Owned + " of them owned by the current user. " +
                   "owned_by_current_user_count is a LOWER BOUND. Call horizun_health again: it resumes after the last element " +
                   "examined and the pass closes with complete=true when the ids run out.";
        }
    }

    /// <summary>The cursor per document, held for the session. Never persisted.</summary>
    public static class OwnershipPassStore
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, OwnershipPass> Passes = new Dictionary<string, OwnershipPass>(StringComparer.Ordinal);

        public static OwnershipPass Get(string key)
        {
            lock (Gate) return Passes.TryGetValue(key ?? "", out OwnershipPass p) ? p : null;
        }

        public static void Put(string key, OwnershipPass p)
        {
            lock (Gate) Passes[key ?? ""] = p;
        }

        public static void ClearForTests() { lock (Gate) Passes.Clear(); }
    }
}
