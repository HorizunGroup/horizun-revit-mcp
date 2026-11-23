using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class AuditModelCommand
    {
        internal static readonly BuiltInCategory[] LevelSpanCategories = {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_Ramps, BuiltInCategory.OST_GenericModel
        };

        private static JObject LevelSpan(Document doc, int top)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => new { Element = l, Elevation = l.ProjectElevation * 304.8 })
                .OrderBy(l => l.Elevation).ToList();
            var categories = new HashSet<long>(LevelSpanCategories.Select(c => (long)c));
            var hits = new List<JObject>();
            var exceptions = new List<JObject>();
            int unreadable = 0;
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (e.Category == null || !categories.Contains(Rid.Value(e.Category.Id))) continue;
                try
                {
                    BoundingBoxXYZ box = e.get_BoundingBox(null);
                    if (box == null) { unreadable++; continue; }
                    double[] bounds = LevelSpanBounds.Read(box);
                    double min = bounds[0] * 304.8, max = bounds[1] * 304.8;
                    var crossed = levels.Where(l => LevelSpanRules.Crossed(min, max,
                        new[] { l.Elevation }).Count != 0).ToList();
                    if (crossed.Count == 0) continue;
                    var row = new JObject {
                        ["element_id"] = Rid.Value(e.Id), ["category"] = e.Category.Name, ["built_in_category"] = CategoryKey.Of(e),
                        ["bottom_mm"] = Math.Round(min, 1), ["top_mm"] = Math.Round(max, 1),
                        ["crossed_levels"] = new JArray(crossed.Select(l => new JObject {
                            ["element_id"] = Rid.Value(l.Element.Id), ["name"] = l.Element.Name,
                            ["elevation_mm"] = Math.Round(l.Elevation, 1) })),
                        ["suggestion"] = "Split at each crossed level and assign every resulting piece to one storey; " +
                            "if design requires continuity, set HZ_Excepcion_Nivel to the specific reason."
                    };
                    string reason = e.LookupParameter("HZ_Excepcion_Nivel")?.AsString();
                    if (!string.IsNullOrWhiteSpace(reason)) {
                        row["exception_reason"] = reason.Trim(); exceptions.Add(row);
                    } else hits.Add(row);
                }
                catch { unreadable++; }
            }
            var finding = Finding(AuditCheckNames.LevelSpan, hits.Count != 0, hits.Count,
                hits.Count + " element(s) cross another level by more than 50 mm without an exception; " +
                exceptions.Count + " documented exception(s).", new JArray(hits.Take(top)), hits.Count,
                unreadable);
            finding["tolerance_mm"] = LevelSpanRules.DefaultToleranceMm;
            finding["exceptions_total"] = exceptions.Count;
            finding["exceptions_shown"] = Math.Min(top, exceptions.Count);
            finding["exceptions"] = new JArray(exceptions.Take(top));
            return finding;
        }
    }
}
