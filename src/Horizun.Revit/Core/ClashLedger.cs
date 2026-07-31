// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Who was actually checked, which pairs were actually tested, and once each.
//
// horizun_clash returns a number that gets read as "coordinated". Every element
// that quietly fell out of the check makes that number smaller and no less
// confident. The collector had three ways to drop one without a trace:
//
//     catch { continue; }                  // a whole category, gone
//     try { bb = e.get_BoundingBox(null); } catch { continue; }
//     if (bb == null) continue;            // no box, no record
//
// The counts then reported the SURVIVORS as the candidates, and coverage.complete
// stayed true. A clean report and a report that never looked are the same shape.
//
// Two more, both of which produce wrong geometry rather than missing geometry:
//
//   * The solid cache was keyed on the link's NAME plus the element id. Two
//     instances of the same link - the normal way a repeated block is placed -
//     share a name and have different transforms, so the second instance got the
//     first one's solids, positioned where the first one is.
//
//   * With overlapping category sets, pair (X,Y) and pair (Y,X) were both tested
//     and both reported.
//
// This file holds the accounting: a canonical pair key, a per-side ledger that
// records every drop with its reason, and a completeness rule that only says
// "complete" when nothing was dropped and no pair was left unresolved. Revit-free
// so it can be proved without a model.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// One element's fate on one side of the check. An element we chose not to check
    /// and one we FAILED to check are both holes, and neither is an element that was
    /// checked and found clean.
    /// </summary>
    public enum ClashInclusion
    {
        /// <summary>Collected, boxed, and available to the broad phase.</summary>
        Included,

        /// <summary>Deliberately not checked, for a stated reason.</summary>
        Excluded,

        /// <summary>A read failed. We do not know what this element would have hit.</summary>
        Failed
    }

    public sealed class SideLedger
    {
        public string Side { get; }
        public int Candidates { get; private set; }
        public int Included { get; private set; }
        public int Excluded { get; private set; }
        public int Failed { get; private set; }

        private readonly Dictionary<string, int> _reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _examples = new List<string>();

        public SideLedger(string side) { Side = side; }

        public void Add(ClashInclusion how, string reason = null, string example = null)
        {
            Candidates++;
            switch (how)
            {
                case ClashInclusion.Included: Included++; return;
                case ClashInclusion.Excluded: Excluded++; break;
                default: Failed++; break;
            }

            string key = reason ?? "(no reason recorded)";
            _reasons[key] = _reasons.TryGetValue(key, out int n) ? n + 1 : 1;
            if (example != null && _examples.Count < 10) _examples.Add(example);
        }

        /// <summary>Reasons with counts, most frequent first. Never empty when anything dropped.</summary>
        public IEnumerable<KeyValuePair<string, int>> Reasons =>
            _reasons.OrderByDescending(kv => kv.Value);

        public IEnumerable<string> Examples => _examples;

        /// <summary>Every candidate reached the broad phase.</summary>
        public bool Complete => Excluded == 0 && Failed == 0;

        public string Describe()
        {
            if (Candidates == 0) return Side + ": no elements matched the requested categories.";
            if (Complete) return Side + ": all " + Candidates + " element(s) were checked.";
            return Side + ": " + Included + " of " + Candidates + " element(s) were checked; " +
                   Excluded + " excluded and " + Failed + " could not be read. Anything they would have hit " +
                   "is UNKNOWN, not absent.";
        }
    }

    /// <summary>
    /// Pair bookkeeping: dedup by a canonical key, and a record of every pair whose
    /// outcome is not known.
    /// </summary>
    public sealed class PairLedger
    {
        // A separator that cannot occur in a Revit name or element id. A space would
        // NOT do: link source names legitimately contain them - for example
        // "MOD_STRC-REF_A.rvt : 859 : location <Not Shared>" - so ("a b","c") and
        // ("a","b c") would build the same key and one element would inherit another's
        // cached solids. That is the exact class of bug this file exists to remove, so
        // it must not be reintroduced by the fix.
        private static readonly string Sep = ((char)1).ToString();

        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public int Tested { get; private set; }
        public int Duplicates { get; private set; }
        public int Unresolved { get; private set; }
        public int SkippedNoSolids { get; private set; }

        /// <summary>
        /// Claim a pair for testing. Returns false when this pair has already been tested
        /// in the other order - which happens whenever the two category sets overlap.
        /// </summary>
        public bool Claim(string sourceA, string idA, string sourceB, string idB)
        {
            string key = PairKey(sourceA, idA, sourceB, idB);
            if (!_seen.Add(key)) { Duplicates++; return false; }
            Tested++;
            return true;
        }

        /// <summary>A pair whose outcome we do not know. Never counts as clean.</summary>
        public void MarkUnresolved() => Unresolved++;

        /// <summary>
        /// A pair one side of which had no usable solid. Not a clash and not a clean
        /// result: nothing was actually intersected.
        /// </summary>
        public void MarkNoSolids() => SkippedNoSolids++;

        public bool Complete => Unresolved == 0 && SkippedNoSolids == 0;

        /// <summary>
        /// Order-independent identity for a pair. Sorting the two endpoints means (X,Y)
        /// and (Y,X) collapse to one key, so overlapping category sets cannot report the
        /// same collision twice.
        /// </summary>
        public static string PairKey(string sourceA, string idA, string sourceB, string idB)
        {
            string a = ElementKey(sourceA, null, idA);
            string b = ElementKey(sourceB, null, idB);
            return string.CompareOrdinal(a, b) <= 0 ? a + Sep + Sep + b : b + Sep + Sep + a;
        }

        /// <summary>
        /// Identity of ONE element for caching its solids. The link INSTANCE id is part of
        /// it: two instances of the same link share a name and a link-document element id
        /// but sit at different transforms, so keying on the name alone hands the second
        /// instance the first one's geometry, positioned where the first one is.
        /// </summary>
        public static string ElementKey(string sourceName, string instanceId, string elementId)
        {
            return (sourceName ?? "") + Sep + (instanceId ?? "-") + Sep + (elementId ?? "");
        }
    }
}
