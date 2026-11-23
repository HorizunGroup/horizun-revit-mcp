using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class TemplateKitTool
    {
        // Product files are supplied explicitly in a kit. This resolver never downloads,
        // authors, or opens an RFA and never assumes product metadata proves geometry quality.
        private static IEnumerable<JObject> FindEntries(JObject kit) =>
            (kit["catalog"] as JArray ?? new JArray()).OfType<JObject>()
                .Concat((kit["product_families"] as JArray ?? new JArray()).OfType<JObject>());

        private static bool IsProductFamily(JObject entry) => entry["product"] is JObject ||
            (entry["product_family"]?.Type == JTokenType.Boolean && (bool)entry["product_family"]);

        private static void AddProductFamilyRoute(JObject entry)
        {
            if (!IsProductFamily(entry)) return;
            // A catalog marker describes an existing model family. It need not have an
            // external RFA: keep its existing resolve/placement recipe and the marker.
            if (!(entry["product"] is JObject) && entry["source_path"] == null) return;
            string path = (string)entry["source_path"];
            string problem = LocalPath.Problem(path, "source_path");
            if (problem == null && !string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
                problem = "source_path must name a user-supplied .rfa file.";
            bool exists = problem == null && File.Exists(path);
            string family = (string)entry["family"], type = (string)entry["type"];
            bool named = !string.IsNullOrWhiteSpace(family) && !string.IsNullOrWhiteSpace(type);
            entry["product_family"] = new JObject
            {
                ["source"] = "user_supplied", ["source_path"] = path,
                ["file_exists"] = problem == null ? (JToken)exists : JValue.CreateNull(),
                ["status"] = problem != null ? "invalid_source_path" : !exists ? "file_missing" : !named ? "family_or_type_missing" : "available",
                ["problem"] = problem ?? (!named ? "Explicit family and type names are required before suggesting a load." : null),
                ["geometry_quality"] = "not_measured",
                ["load"] = null,
                ["resolve_after_load"] = exists && named ? new JObject
                {
                    ["tool"] = "horizun_query_model", ["arguments"] = new JObject
                    {
                        ["include_types"] = true, ["include_links"] = false, ["family"] = family, ["type"] = type
                    }
                } : null,
                ["next"] = "Loading a family through the bridge is in the Pro edition: load it in Revit, then re-read the loaded family/type with resolve_after_load and inspect geometry_quality and connectors/host before placing or replacing instances."
            };
        }

        internal static JObject FindProductFamilies(JObject args, CancellationToken ct)
        {
            // catalog_path here names the same .hzkit.json read by template_kit;
            // leaf/search still accept only their original text-table formats.
            var request = new JObject
            {
                ["operation"] = "find", ["path"] = args?["catalog_path"], ["product_only"] = true,
                ["text"] = args?["query"], ["category"] = args?["category"], ["code"] = args?["code"],
                ["limit"] = args?["max_results"] ?? new JValue(10)
            };
            JObject reply = Handle(request, ct);
            reply["operation"] = "products";
            reply["catalog_path"] = args?["catalog_path"];
            reply["note"] = "Only product entries supplied in this kit are suggested. File availability is checked; product geometry, Revit version, host and connector compatibility are checked by the load rehearsal and live geometry audit.";
            return reply;
        }
    }
}
