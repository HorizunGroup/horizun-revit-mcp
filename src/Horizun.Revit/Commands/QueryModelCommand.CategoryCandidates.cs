using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryModelCommand
    {
        internal static IEnumerable<Element> CollectCategoryCandidates(Document source, ElementId viewId,
            ISet<long> categoryIds, bool includeTypes, out List<ElementFilter> markFilters)
        {
            FilteredElementCollector collector = viewId == null
                ? new FilteredElementCollector(source)
                : new FilteredElementCollector(source, viewId);
            // Native quick filtering avoids materialising every category in managed
            // code. Keep the TextNoteType supplement below for its special category.
            var markerIds = SectionMarkCategories.BuiltInNames.Select(BuiltInCategoryId)
                .Where(id => categoryIds?.Contains(id) == true).ToList();
            markFilters = markerIds.Select(id => (ElementFilter)new ElementCategoryFilter(Rid.Make(id))).ToList();
            if (categoryIds != null && categoryIds.Count > 0)
            {
                // Use the same native category filters as section_mark_diagnosis for internal marks.
                var categoryFilters = new List<ElementFilter>(markFilters);
                List<ElementId> standardIds = categoryIds.Except(markerIds).Select(Rid.Make).ToList();
                if (standardIds.Count > 0) categoryFilters.Add(new ElementMulticategoryFilter(standardIds));
                collector.WherePasses(categoryFilters.Count == 1 ? categoryFilters[0] : new LogicalOrFilter(categoryFilters));
            }
            // Revit refuses extraction from a collector with no native filter, even
            // though the LINQ Cast/Where compiles. Apply a real ElementFilter before
            // iteration. The OR is an explicit pass over types + instances when the
            // caller requested both.
            return includeTypes
                ? collector.WherePasses(new LogicalOrFilter(
                    new ElementIsElementTypeFilter(false),
                    new ElementIsElementTypeFilter(true))).Cast<Element>()
                : collector.WhereElementIsNotElementType().Cast<Element>();
        }

        private static long BuiltInCategoryId(string name) => (long)(BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), name, true);
    }
}
