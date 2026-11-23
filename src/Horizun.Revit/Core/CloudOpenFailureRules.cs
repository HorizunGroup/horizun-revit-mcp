using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CloudOpenFailureRules
    {
        public static JObject Describe(string error, string activeDocument, string region, string project, string model,
            IList<Interruption> raised, int since, bool observed)
        {
            JArray records = RaisedRecord.Window(raised, since);
            var dialogs = new JArray(records.OfType<JObject>().Where(r => (string)r["kind"] == "dialog"));
            var failures = new JArray(records.OfType<JObject>().Where(r => (string)r["kind"] != "dialog"));
            string detail = string.Join(" | ", dialogs.OfType<JObject>().Take(5).Select(d => (string)d["description"] + " -> " + (string)d["answered"]));
            return new JObject
            {
                ["message"] = error + " Nothing is confirmed open." + (detail.Length == 0 ? "" : " Revit raised while opening: " + detail),
                ["source"] = "cloud", ["opened"] = JValue.CreateNull(), ["confirmed_active"] = false,
                ["active_document"] = activeDocument, ["cloud_region"] = region,
                ["cloud_project_guid"] = project, ["cloud_model_guid"] = model,
                ["dialogs"] = dialogs, ["failures"] = failures, ["revit_raised_observed"] = observed,
                ["note"] = observed ? "Review the recorded open-time dialogs and the active document before retrying."
                    : "Open-time dialogs were not fully observed; an empty list does not prove that Revit raised none."
            };
        }
    }
}
