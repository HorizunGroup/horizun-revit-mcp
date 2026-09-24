// -----------------------------------------------------------------------------
// Horizun Revit MCP - the order of a view's filters, as plain ids.
// Original Horizun code. Pure: no Revit types, so the rule is tested without Revit.
//
// horizun_manage_views order_filters takes the WHOLE list of the view's filters
// (a partial list would have to guess where the rest go). Two measured facts
// shape the rules here:
//
//   * A duplicated view carries the source view's filters (Revit copies them):
//     MEASURED on 2026, an own duplicated view held five filters, not the two it
//     was given. Reorder builds the whole list from the current one, moving only
//     the named filters among the slots they already occupy.
//   * A batch verifies every action AFTER the last one ran. MEASURED on 2023: a
//     later apply_filter on one of the reordered filters made order_filters fail
//     its own verification. KeepsRelativeOrder is what the end-of-batch check
//     can still require: the reordered filters stand in the requested relative
//     order, whatever a later action appended.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class FilterOrderRules
    {
        /// <summary>
        /// True when every id of <paramref name="wanted"/> is in <paramref name="final"/> and,
        /// restricted to those ids, <paramref name="final"/> reads exactly as <paramref name="wanted"/>.
        /// </summary>
        public static bool KeepsRelativeOrder(IList<long> final, IList<long> wanted)
        {
            if (final == null || wanted == null || wanted.Count == 0) return false;
            var set = new HashSet<long>(wanted);
            if (set.Count != wanted.Count) return false;
            return final.Where(set.Contains).SequenceEqual(wanted);
        }

        /// <summary>
        /// The whole new order: <paramref name="current"/> with the ids of <paramref name="moved"/>
        /// rearranged, in that order, among the slots they already occupy. Null when a moved id is
        /// not on the view or is repeated.
        /// </summary>
        public static List<long> Reorder(IList<long> current, IList<long> moved)
        {
            if (current == null || moved == null) return null;
            var set = new HashSet<long>(moved);
            if (set.Count != moved.Count || moved.Any(id => !current.Contains(id))) return null;
            var result = new List<long>(current.Count);
            int next = 0;
            foreach (long id in current) result.Add(set.Contains(id) ? moved[next++] : id);
            return result;
        }
    }
}
