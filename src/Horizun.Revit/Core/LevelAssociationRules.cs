// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// HOW MUCH OF THE MODEL KNOWS WHICH FLOOR IT IS ON. A census, not a verdict.
//
// The temptation here is to call every element without a level a defect. It is
// not one. Whole categories are legitimately level-free - a mass, a topography,
// a link instance - and a tool that reports them as errors trains its reader to
// ignore the number. So this publishes the BREAKDOWN BY CATEGORY beside the
// total, because that is the thing that separates "the walls lost their level"
// from "the site model has no floors", and it grades neither: no standard was
// supplied, and an element without a level has broken no rule this bridge was
// given.
//
// The arithmetic is the other trap. Three counts, not two: an element whose
// level could not be READ is neither associated nor unassociated, and folding it
// into either one invents a fact. It is excluded from the percentage and named
// separately, so the percentage stays a statement about elements somebody
// actually measured.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>One element that reports no level, kept so the reader can go and look.</summary>
    public sealed class UnassociatedElement
    {
        public long ElementId;
        public string Category;
        public string Name;
    }

    /// <summary>What the walk found. Filled by the command; judged here.</summary>
    public sealed class LevelAssociationFacts
    {
        /// <summary>Elements the walk actually looked at. NOT the model's element count.</summary>
        public long Examined;
        public long WithLevel;
        public long WithoutLevel;
        /// <summary>The level read threw. Neither associated nor unassociated - unknown.</summary>
        public long Unreadable;

        /// <summary>Category name to how many of its elements report no level.</summary>
        public Dictionary<string, long> WithoutByCategory =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Level element id to how many elements name it. Feeds LevelFact.ElementCount.</summary>
        public Dictionary<long, long> CountByLevel = new Dictionary<long, long>();

        public List<UnassociatedElement> Unassociated = new List<UnassociatedElement>();
    }

    public static class LevelAssociationRules
    {
        /// <summary>
        /// The share of MEASURED elements that report a level, or null when nothing
        /// was measured.
        ///
        /// Null, never 0 and never 100. A census that examined nothing has not found
        /// a model with no levels; it has found nothing, and both numbers would be
        /// read as a result. This is the single distinction the whole census exists
        /// to preserve.
        ///
        /// Unreadable elements are NOT in the denominator. An element whose level
        /// could not be read has not told us it lacks one, so counting it as a miss
        /// would report a defect the model never showed us.
        /// </summary>
        public static double? PercentWithLevel(long withLevel, long withoutLevel)
        {
            long known = withLevel + withoutLevel;
            if (known <= 0) return null;
            return Math.Round(withLevel * 100.0 / known, 4);
        }

        /// <summary>True when nothing was unreadable, so the counts are exact rather than bounds.</summary>
        public static bool IsExact(long unreadable) { return unreadable == 0; }

        public static string Note(LevelAssociationFacts f)
        {
            if (f == null) return "no census was taken, so nothing is known about level association.";
            if (f.Examined == 0)
                return "no element was examined, so the share associated to a level is UNKNOWN. " +
                       "This is not a model without levels; it is a census with nothing in it.";

            double? pct = PercentWithLevel(f.WithLevel, f.WithoutLevel);
            string s = pct.HasValue
                ? (CoordinateRules.Fmt(pct.Value) + "% of the " + (f.WithLevel + f.WithoutLevel) +
                   " element(s) whose level could be read report one.")
                : "no element's level could be read, so the share is UNKNOWN.";

            if (f.Unreadable > 0)
                s += " " + f.Unreadable + " element(s) would not report a level at all; they are excluded " +
                     "from that percentage rather than counted as misses, so both counts are LOWER BOUNDS.";
            return s;
        }

        /// <summary>
        /// The sentence that stops the count being read as a defect list, published
        /// beside the number rather than left in documentation nobody opens.
        /// </summary>
        public const string CensusMeans =
            "a census, not a finding. Whole categories are legitimately without a level - masses, topography, " +
            "link instances - so a non-zero count is not by itself a problem and no standard was supplied to " +
            "make it one. The per-category breakdown is the part worth reading: it separates a category that " +
            "never has a level from one that has just lost it.";

        /// <summary>
        /// The categories, largest first, so a reader sees where the unassociated
        /// elements actually are instead of a single total.
        /// </summary>
        public static List<KeyValuePair<string, long>> WithoutByCategoryRanked(LevelAssociationFacts f)
        {
            var rows = new List<KeyValuePair<string, long>>();
            if (f == null || f.WithoutByCategory == null) return rows;
            foreach (KeyValuePair<string, long> kv in f.WithoutByCategory) rows.Add(kv);
            rows.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                // Ties broken by name so two runs of the same model agree. An
                // unstable order makes a diff between snapshots unreadable.
                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
            });
            return rows;
        }
    }
}
