using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Presentation only: the measured verdict and its global coverage stay intact.
    // Every shortened array is named, with its actual returned (not model) count.
    public static class ProgressiveResponse
    {
        public static JObject Scan(JObject result, JObject request)
        {
            if ((request.Value<string>("response_mode") ?? "full") == "full") return result;
            var copy = (JObject)result.DeepClone();
            var omissions = new JArray();
            if (copy["sections"] != null) Shorten(copy["sections"], "/sections", omissions);
            copy["response_mode"] = "summary";
            copy["response_omissions"] = omissions;
            copy["response_detail_complete"] = omissions.Count == 0 &&
                !(copy["sections"] as JObject ?? new JObject()).DescendantsAndSelf().OfType<JObject>()
                    .Any(o => o["items"] is JArray a && (o.Value<bool?>("truncated") == true ||
                        o.Value<bool?>("total_is_exact") == false || (o.Value<long?>("total") ?? a.Count) > a.Count));
            copy["paged_describes"] = "Original measured page budgets before summary sampling; response_omissions names hidden rows.";
            var expand = (JObject)request.DeepClone(); expand["response_mode"] = "full";
            copy["expand"] = new JObject { ["tool"] = "horizun_model_scan", ["arguments"] = expand,
                ["note"] = "Reruns the requested sections against the current model. Follow bucket cursors for details beyond their existing budgets. Array totals here count returned items, not all model elements." };
            return copy;
        }
        static void Shorten(JToken token, string pointer, JArray omissions)
        {
            if (token is JObject obj)
            {
                // Only shorten inventories. A geometry vector, polygon, action list,
                // or nested row value is an atomic value, never a sample of rows.
                if (obj["items"] is JArray items && obj["total"] != null && obj["returned"] != null)
                {
                    int original = items.Count;
                    if (original > 3)
                    {
                        while (items.Count > 3) items.RemoveAt(items.Count - 1);
                        obj["returned"] = items.Count;
                        obj["truncated"] = true;
                        obj["summary_omitted"] = original - items.Count;
                        // A cursor for the original page would skip the hidden rows.
                        obj.Remove("next_cursor");
                        omissions.Add(new JObject { ["json_pointer"] = pointer + "/items", ["returned_items_before_summary"] = original,
                            ["shown"] = items.Count, ["omitted"] = original - items.Count });
                    }
                    return;
                }
                foreach (var p in obj.Properties().ToArray())
                    Shorten(p.Value, pointer + "/" + p.Name.Replace("~", "~0").Replace("/", "~1"), omissions);
            }
            else if (token is JArray array)
            {
                for (int i = 0; i < array.Count; i++) Shorten(array[i], pointer + "/" + i, omissions);
            }
        }
    }
}
