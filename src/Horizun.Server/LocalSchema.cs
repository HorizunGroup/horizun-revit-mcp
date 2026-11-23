using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    // A discovery projection only. Dispatch still validates the original contract.
    internal static class LocalSchema
    {
        private static readonly Dictionary<string, string> Fields = new Dictionary<string, string>
        {
            ["horizun_health"] = "",
            ["get_document_info"] = "operation offset max_rows",
            ["horizun_list_elements"] = "category include_links offset max_rows",
            ["horizun_query_model"] = "categories element_ids family type name level include_links response_mode max_rows cursor target_document",
            ["horizun_capture_view"] = "view_id view_name pixel_size",
            ["horizun_create_elements"] = "elements units target_document dry_run confirmation_token idempotency_key",
            ["horizun_write_params_verified"] = "writes target_document dry_run confirmation_token idempotency_key",
            ["horizun_delete_verified"] = "mode ids protect_ids target_document dry_run confirmation_token idempotency_key"
        };

        private static readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>
        {
            ["horizun_health"] = "Check Revit connection and active document first. Verifica conexión y documento activo.",
            ["get_document_info"] = "operation=context: list niveles (id,name,elevation_m) and vistas (id,name,type,level_id,level_name). Query IDs before capture or sheets. Paginate with offset/max_rows. Default: document facts.",
            ["horizun_list_elements"] = "Count/list elements. Contar muros: category=OST_Walls; puertas=OST_Doors. Totals ignore max_rows; use max_rows=1 for counts. include_links=false: host only.",
            ["horizun_query_model"] = "Filter model elements. Consultar por categoría, nivel, familia o nombre. response_mode=summary counts without rows; compact returns lean rows.",
            ["horizun_capture_view"] = "Capture vista/planta as image. First query view_id with get_document_info operation=context; never invent names or IDs. Advanced arguments: horizun_describe_tool.",
            ["horizun_create_elements"] = "Create elements / crear elementos. Describe this tool with variant=wall (or another kind) first. Rehearse with dry_run=true; apply needs token. Writes are re-read.",
            ["horizun_write_params_verified"] = "Write parameters / editar parámetros, then re-read. Rehearse first; apply with returned confirmation_token, dry_run=false and new idempotency_key.",
            ["horizun_delete_verified"] = "Delete explicit ids / borrar elementos, then re-read. mode=ids. Rehearse first; apply needs confirmation_token and new idempotency_key. Other modes: describe_tool.",
            [SchemaFacade.ListToolsName] = "Search tools in English or Spanish: query='contar muros'. Buscar herramientas. Returns names and one line; respects permissions and toolsets.",
            [SchemaFacade.DescribeToolName] = "Get the full argument schema, including advanced options. Esquema completo. For create_elements use variant=wall, family_instance, etc. before calling.",
            [SchemaFacade.CallToolName] = "Call a discovered tool by name and arguments. Ejecutar herramienta. Same permissions, validation, rehearsal and verification as a direct call."
        };

        internal static bool Includes(string name) => Fields.ContainsKey(name);

        public static JArray List(bool tasks, IEnumerable<JObject> metaTools)
        {
            var result = new JArray();
            foreach (var tool in Tools.Advertisable().Where(t => Fields.ContainsKey(t.Name)))
            {
                // Publish may attach the cached compact schema on its first use.
                // Never trim that cache: full discovery must be independent of call order.
                var def = (JObject)Tools.Publish(tool, tasks).DeepClone();
                var schema = (JObject)def["inputSchema"];
                SelectFields(schema, Fields[tool.Name]);
                if (tool.Name == "horizun_create_elements")
                {
                    var items = (JObject)schema["properties"]["elements"]["items"];
                    SelectFields(items, "kind type_id level_id start end point height name elevation parameters");
                    items.Remove("oneOf");
                    items["properties"]["kind"]["enum"] = new JArray("wall", "family_instance", "level");
                }
                if (tool.Name == "horizun_delete_verified")
                    schema["properties"]["mode"]["enum"] = new JArray("ids");
                Compact(def);
                result.Add(def);
            }
            foreach (var meta in metaTools)
            {
                if (tasks) meta["execution"] = new JObject
                {
                    ["taskSupport"] = (string)meta["name"] == SchemaFacade.CallToolName ? "optional" : "forbidden"
                };
                Compact(meta);
                result.Add(meta);
            }
            return result;
        }

        private static void SelectFields(JObject schema, string fields)
        {
            var keep = new HashSet<string>(fields.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            // Never discard a universally required argument.
            foreach (var required in (schema["required"] as JArray ?? new JArray())) keep.Add((string)required);
            foreach (var p in ((JObject)schema["properties"]).Properties().ToList())
                if (!keep.Contains(p.Name)) p.Remove();
        }

        private static void Compact(JObject def)
        {
            def["description"] = Descriptions[(string)def["name"]];
            foreach (var p in ((JObject)def["inputSchema"]).Descendants().OfType<JProperty>()
                         .Where(p => p.Name == "description").ToList())
                p.Value = SchemaFacade.Summary((string)p.Value);
        }
    }
}
