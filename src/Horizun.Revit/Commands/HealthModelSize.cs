using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class HealthModelSize
    {
        // Failed individual measurements remain unknown, rather than looking like zero.
        internal static object Measure(Document doc)
        {
            int? elements = null, links = null, worksets = null;
            long? fileBytes = null;
            try { elements = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(); } catch { }
            try { links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount(); } catch { }
            try
            {
                if (!doc.IsWorkshared) worksets = 0;
                else
                {
                    int count = 0;
                    var iterator = new FilteredWorksetCollector(doc).GetWorksetIterator();
                    while (iterator.MoveNext()) count++;
                    worksets = count;
                }
            }
            catch { }
            try { if (!string.IsNullOrEmpty(doc.PathName)) fileBytes = new System.IO.FileInfo(doc.PathName).Length; } catch { }
            return new
            {
                element_count = elements, file_size_bytes = fileBytes,
                link_instances = links, workset_count = worksets,
                large_model = LargeModelRules.IsLarge(elements),
                large_model_threshold = new { element_count = LargeModelRules.ElementThreshold, comparison = ">=" },
                note = "Element count excludes element types. File size is the on-disk file, if accessible; null means unknown."
            };
        }

    }
}
