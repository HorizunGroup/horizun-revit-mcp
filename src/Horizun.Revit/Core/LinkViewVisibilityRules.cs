using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class LinkViewVisibilityRules
    {
        /// <summary>
        /// A view filter (ParameterFilterElement) acts ONLY on the categories it was built for; its rules alone say nothing
        /// about an element of another category (a "Type Name not equal to" rule is true for any element without that
        /// parameter). Measured on a 2026 model: judging by the rules alone flagged 35 linked elements as hidden when the
        /// filter was for Levels. Null or empty category set: the filter hides nothing.
        /// </summary>
        public static bool FilterCoversCategory(IEnumerable<long> filterCategories, long elementCategory) =>
            filterCategories != null && filterCategories.Contains(elementCategory);

        /// <summary>
        /// Does a hidden view filter hide this element? Only on its own categories; a filter with NO rules (Revit hands back no element
        /// filter) hides every element of those categories - it is not "unreadable". Otherwise its rules decide (<paramref name="passes"/>).
        /// </summary>
        public static bool HiddenByFilter(bool coversCategory, bool hasRules, Func<bool> passes) =>
            coversCategory && (!hasRules || (passes != null && passes()));

        public static bool OverlapsZ(double elementMin, double elementMax, double viewMin, double viewMax)
        {
            return !double.IsNaN(elementMin) && !double.IsNaN(elementMax) &&
                !double.IsNaN(viewMin) && !double.IsNaN(viewMax) &&
                elementMin <= elementMax && viewMin <= viewMax &&
                elementMax >= viewMin && elementMin <= viewMax;
        }

        /// <summary>
        /// A view hides a link instance either by itself or through its RevitLinkType (measured on
        /// Revit 2025: a 3D view created from a family type whose default template hides the link type
        /// reports the instance hidden, and UnhideElements on the instance alone changes nothing).
        /// Showing a link therefore unhides its type too, which would also show every OTHER instance
        /// of that type that was hidden only through the type; those are hidden again by instance so
        /// nothing the caller did not name changes. Returns the instance ids to hide again.
        /// </summary>
        public static IReadOnlyList<long> InstancesToKeepHidden(
            IEnumerable<long> instancesOfType, ISet<long> requested, ISet<long> hiddenBeforeByInstanceOrType)
        {
            return (instancesOfType ?? Enumerable.Empty<long>())
                .Where(id => (requested == null || !requested.Contains(id)) &&
                             hiddenBeforeByInstanceOrType != null && hiddenBeforeByInstanceOrType.Contains(id))
                .Distinct().OrderBy(id => id).ToList();
        }

        /// <summary>
        /// Revit stores "no transparency override" as 0, so an element override of 0 cannot beat a
        /// category override above 0: the element keeps the category's transparency. The nearest value
        /// Revit can hold is 1. Null: write what was asked.
        /// </summary>
        public static int? OpaqueOverInheritedTransparency(int requested, int inherited) =>
            requested == 0 && inherited > 0 ? 1 : (int?)null;

        public const string LinkColourNotDrawn =
            "Revit keeps the colour override on the link instance but does not draw its surface fill or line colour on a linked model " +
            "(measured on Revit 2025 in a shaded 3D view and its image export; the same override draws on host elements; only halftone reaches the link). " +
            "The link shows in its own materials. To colour a linked model, use category or view-filter overrides in the host view (they reach linked elements, and host elements of those categories too).";
    }
}
