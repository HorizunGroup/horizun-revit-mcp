using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Defaults for omitted documentation inputs.</summary>
    public static class DocumentationDefaults
    {
        public static int Scale(JObject request, int current, ICollection<JObject> assumptions)
        {
            if (request["scale"] != null) return request.Value<int>("scale");
            assumptions.Add(AssumptionNotes.Row("scale=" + current, "scale omitted; retain the current view scale", "send scale"));
            return current;
        }

        public static bool BoxAnchor(JObject request, bool cropReadable, string why, ICollection<JObject> assumptions)
        {
            if (request["anchor"] != null) return false;
            assumptions.Add(AssumptionNotes.Row(cropReadable ? "anchor=crop" : "anchor=box",
                cropReadable ? "anchor omitted; use the view crop" : why, "send anchor"));
            return !cropReadable;
        }

        public static JObject ViewAction(JObject action, int index)
        {
            string op = action.Value<string>("operation")?.ToLowerInvariant();
            string path = "actions[" + index + "].";
            switch (op)
            {
                case "create_sheet" when action["title_block_type_id"] == null:
                    return AssumptionNotes.Row(path + "title_block_type_id=none",
                        "title block omitted; create a sheet without a title block", "send title_block_type_id");
                case "create_section":
                case "create_callout":
                    return action["template_view_id"] != null ? null : AssumptionNotes.Row(path + "detail_level=Revit default",
                        "no view template supplied; retain Revit's initial detail level",
                        "send template_view_id, or use set_view_properties with detail_level afterwards");
                case "create_floor_plan":
                case "create_ceiling_plan":
                case "create_structural_plan":
                case "create_area_plan":
                case "create_elevation":
                case "create_3d":
                case "create_drafting":
                    return AssumptionNotes.Row(path + "detail_level=Revit default",
                        "retain Revit's initial detail level for the new view",
                        "use set_view_properties with detail_level afterwards");
                default:
                    return null;
            }
        }
    }
}
