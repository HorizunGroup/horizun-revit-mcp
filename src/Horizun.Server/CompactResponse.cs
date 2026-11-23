using System;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class CompactResponse
    {
        internal static bool Enabled
        {
            get
            {
                string response = Environment.GetEnvironmentVariable("HORIZUN_RESPONSE")?.Trim();
                if (string.Equals(response, "full", StringComparison.OrdinalIgnoreCase)) return false;
                return string.Equals(response, "compact", StringComparison.OrdinalIgnoreCase) || SchemaMode.Current == SchemaSurface.Local;
            }
        }

        // Runs AFTER sanitization. Never reshape writes, errors, schema descriptions or task handles.
        internal static JToken Apply(JToken result, string name)
        {
            if (!Enabled || !(result is JObject original) || (bool?)original["isError"] == true ||
                !(original["content"] is JArray) || !(original["structuredContent"] is JObject)) return result;
            if (name != SchemaFacade.ListToolsName && Tools.Find(name)?.Effect != ToolEffect.ReadOnly) return result;
            var copy = (JObject)original.DeepClone();
            var data = (JObject)copy["structuredContent"];
            // Only bridge-owned top-level telemetry: never remove a model parameter with this name.
            data.Remove("bridge_queue");
            data.Remove("queue");
            if (data[ContentSafety.PayloadKey] is JObject safety)
            {
                data.Remove(ContentSafety.PayloadKey);
                data["untrusted_content"] = true;
                if ((int?)safety["neutralized_characters"] > 0 || (int?)safety["suspected_instructions"] > 0)
                    data["safety_findings"] = new JObject
                    {
                        ["neutralized_characters"] = safety["neutralized_characters"],
                        ["suspected_instructions"] = safety["suspected_instructions"]
                    };
            }
            string summary;
            if (name == "horizun_list_elements" && data["total"] != null)
                summary = (string)data["category"] + ": " + data["total"];
            else if (name == "horizun_query_model" && (data["matched_total"] ?? data["total"]) is JToken total)
                summary = "Elementos: " + total;
            else if (name == SchemaFacade.ListToolsName && data["tools"] is JObject listed)
                summary = "Herramientas: " + listed.Count;
            else if (name == "horizun_health") summary = "Revit: " + ((string)data["status"] ?? "estado disponible");
            else summary = name + ": datos disponibles.";
            if ((bool?)data["coverage_complete"] == false || (bool?)data["partial"] == true ||
                (bool?)(data["cooperative"] as JObject)?["complete"] == false)
                summary += " (parcial; consulta cobertura)";
            if ((bool?)data["truncated"] == true) summary += " (filas paginadas)";
            // Image/resource blocks are kept byte-for-byte. The JSON is present once, in structuredContent.
            var content = (JArray)copy["content"];
            foreach (var block in content.OfType<JObject>().Where(b => (string)b["type"] == "text").ToList()) block.Remove();
            content.Insert(0, new JObject { ["type"] = "text", ["text"] = summary });
            return copy;
        }
    }
}
