using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Pure decisions shared by the commands and their response tests.
    public static class ReliabilityDefaults
    {
        public static JObject BatchRow(JObject model, string host, bool workshared)
        {
            var row = new JObject { ["file_path"] = model["file_path"].DeepClone(), ["success"] = false };
            var assumptions = new List<JObject>();
            if (model["expected_version"] == null) assumptions.Add(AssumptionNotes.Row("expected_version=" + host, "Use the host year; the file year is still checked.", "Set expected_version on this model."));
            if (model["close"] == null) assumptions.Add(AssumptionNotes.Row("close=discard", "Preserve the source unless saving is explicit.", "Set close on this model."));
            AssumptionNotes.Stamp(row, assumptions);
            StampBatchDetach(row, model, workshared);
            return row;
        }

        public static void StampBatchDetach(JObject row, JObject model, bool workshared)
        {
            if (workshared && model["detach"] == null)
                AssumptionNotes.Stamp(row, new[] { AssumptionNotes.Row("detach=true", "Central and local workshared files must not borrow from central.", "Set detach=true; detach=false is refused for workshared models.") });
        }

        public static IEnumerable<JObject> Mesh(JObject r)
        {
            if (r["categories"] == null) yield return AssumptionNotes.Row("categories=solid model categories", "Exclude spatial elements, lines and link containers.", "Set categories to OST_ names.");
            if (r["include_links"] == null) yield return AssumptionNotes.Row("include_links=false", "Host geometry is the default scope.", "Set include_links.");
            if (r["skip_directshape"] == null) yield return AssumptionNotes.Row("skip_directshape=true", "Exclude imported comparison meshes.", "Set skip_directshape.");
        }

        public static IEnumerable<JObject> Graphics(JObject r)
        {
            if (r.Value<string>("operation") == "copy_view_graphics" && r["include"] == null)
                yield return AssumptionNotes.Row("include=filters,categories", "Element overrides require an explicit selection.", "Set include (and element_ids for elements).");
        }

        public static IEnumerable<JObject> Curves(JObject r)
        {
            if (!(r["operations"] is JArray operations)) yield break;
            for (int index = 0; index < operations.Count; index++)
            {
                if (operations[index] is JObject op && op["operation"]?.Type == JTokenType.String &&
                    string.Equals((string)op["operation"], "set_curve", StringComparison.OrdinalIgnoreCase) &&
                    op["release_end_joins"] == null)
                    yield return AssumptionNotes.Row("operations[" + index + "].release_end_joins=false", "Preserve joins unless releasing them was requested.", "Set release_end_joins on this operation.");
            }
        }

        // Groups expose their members; stairs/railings expose their subelements.
        public static bool ExportLeaf(bool link, bool group) => !link && !group;
        public static bool MeshContainer(string type) => type == "Stairs" || type == "Railing";
        public static bool MeshSubelement(string parent, string child) => parent == "Stairs"
            ? new[] { "StairsRun", "StairsLanding", "StairsSupport" }.Contains(child)
            : parent == "Railing" && new[] { "TopRail", "HandRail", "RailingSupport" }.Contains(child);
        public static int MeshCategoryOrder(string category) => category == "OST_Stairs" ? 0 : category == "OST_StairsRailing" ? 1 : 2;
        public static bool SolidCategory(string name) => !new[] { "OST_RvtLinks", "OST_Rooms", "OST_MEPSpaces", "OST_Areas", "OST_Lines", "OST_Curves", "OST_Grids", "OST_Levels" }.Contains(name)
            && name != null && !name.Contains("Lines") && !name.Contains("Tags") && !name.Contains("Separation");
        public static bool SlopePreserved(double before, double after, double tolerance = 0.0005) =>
            !double.IsNaN(before) && !double.IsNaN(after) && !double.IsInfinity(before) && !double.IsInfinity(after)
            && (before == 0 || after == 0 ? before == after : Math.Sign(before) == Math.Sign(after)) && Math.Abs(before - after) <= tolerance;
    }
}
