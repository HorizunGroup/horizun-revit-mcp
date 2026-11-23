// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// categories=['filled_regions'] ANSWERED ZERO (field cycle, 2026-10-21).
//
// MEASURED: horizun_query_detail_2d with categories=['filled_regions'] returned no
// rows for a view that held filled regions; without the filter they appeared. The
// filter compared the element's CATEGORY id with OST_FilledRegion, but a placed
// FilledRegion reports another category (its graphics live under Detail Items), so
// the category test could never match the one element class it names.
//
// A region is what it IS: the class FilledRegion. This file decides, without Revit,
// whether an element passes a categories filter, treating the 'filled_regions' token
// as a class test for regions and the other tokens as category tests.
// -----------------------------------------------------------------------------
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class Detail2DQueryRules
    {
        /// <summary>
        /// Does an element pass the categories filter? A null/empty filter passes everything.
        /// `filledRegionToken` is the id the 'filled_regions' token maps to (OST_FilledRegion).
        /// </summary>
        public static bool PassesCategoryFilter(ISet<long> filter, long elementCategoryId, bool isFilledRegion, long filledRegionToken)
        {
            if (filter == null || filter.Count == 0) return true;
            if (isFilledRegion && filter.Contains(filledRegionToken)) return true;
            return filter.Contains(elementCategoryId);
        }
    }
}
