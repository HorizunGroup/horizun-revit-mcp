// Kit recipes are data. Inspect their contract; never execute them during validation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class TemplateKitTool
    {
        private static IEnumerable<(string path, string tool, JToken args)> Recipes(JObject kit)
        {
            foreach (string section in new[] { "playbook", "checks", "ifc", "resolve" })
                foreach (var r in WalkRecipes(kit[section], section)) yield return r;
            foreach (string section in new[] { "catalog", "product_families" })
            {
                var entries = kit[section] as JArray ?? new JArray();
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!(entries[i] is JObject e)) continue;
                    string path = section + "[" + i + "]";
                    var placementInfo = e["placement"] as JObject;
                    JToken placement = placementInfo?["recipe"];
                    if (placement is JObject p && p["tool"] == null)
                    {
                        string tool = (string)placementInfo["tool"] ?? "horizun_create_elements";
                        var args = tool == "horizun_create_elements"
                            ? new JObject { ["elements"] = new JArray(p.DeepClone()) }
                            : (JObject)p.DeepClone();
                        // Only element fragments need an elements envelope. Other tools
                        // receive their declared arguments, with supported request context.
                        var properties = RecipeSchema(tool)?["properties"] as JObject;
                        if (properties?["target_document"] != null && args["target_document"] == null) args["target_document"] = "<active model>";
                        if (properties?["units"] != null && args["units"] == null) args["units"] = "mm";
                        yield return (path + ".placement.recipe", tool, args);
                    }
                    else foreach (var r in WalkRecipes(placement, path + ".placement.recipe")) yield return r;
                    foreach (var r in WalkRecipes(e["new_size"], path + ".new_size")) yield return r;
                    foreach (string extra in new[] { "ifc", "resolve" })
                        foreach (var r in WalkRecipes(e[extra], path + "." + extra)) yield return r;
                    if (placementInfo != null)
                        foreach (JProperty extra in placementInfo.Properties().Where(x => x.Name != "recipe" && x.Name != "tool"))
                            foreach (var r in WalkRecipes(extra.Value, path + ".placement." + extra.Name)) yield return r;
                }
            }
        }

        private static IEnumerable<(string path, string tool, JToken args)> WalkRecipes(JToken node, string path)
        {
            if (node is JObject o)
            {
                if (o["tool"]?.Type == JTokenType.String)
                    yield return (path, (string)o["tool"], o["args"] ?? new JObject());
                foreach (JProperty p in o.Properties())
                    if (p.Name != "args")
                        foreach (var r in WalkRecipes(p.Value, path + "." + p.Name)) yield return r;
            }
            else if (node is JArray a)
                for (int i = 0; i < a.Count; i++)
                    foreach (var r in WalkRecipes(a[i], path + "[" + i + "]")) yield return r;
        }

        private static JArray ValidateRecipes(JObject kit)
        {
            var problems = new JArray();
            foreach (var r in Recipes(kit))
            {
                JToken schema = RecipeSchema(r.tool);
                var errors = schema == null ? new List<string> { "unknown tool" }
                    : ContractSchemaCheck.ValidateRecipe(r.args, schema);
                if (errors.Count > 0) problems.Add(new JObject
                {
                    ["path"] = r.path, ["tool"] = r.tool, ["errors"] = new JArray(errors)
                });
            }
            return problems;
        }

        private static IEnumerable<JObject> DiscoveryDefinitions() => new[]
        {
            ToolVisibility.ToolsetsDef(), SchemaFacade.DescribeToolDef(), SchemaFacade.CallToolDef(), SchemaFacade.ListToolsDef()
        };

        private static JToken RecipeSchema(string tool) => Contract.Find(tool)?.InputSchema
            ?? DiscoveryDefinitions().FirstOrDefault(d => (string)d["name"] == tool)?["inputSchema"];

        private static JArray UnavailableTools(JObject kit)
        {
            var used = new HashSet<string>(Recipes(kit).Select(r => r.tool), StringComparer.Ordinal);
            foreach (JToken t in kit["mcp"]?["tools_used"] as JArray ?? new JArray())
                if (t.Type == JTokenType.String) used.Add((string)t);
            var reachable = new HashSet<string>(Tools.Advertisable().Select(t => t.Name), StringComparer.Ordinal);
            var listed = new HashSet<string>(SchemaFacade.List(SchemaMode.Current, false).OfType<JObject>().Select(t => (string)t["name"]), StringComparer.Ordinal);
            var groups = ToolVisibility.ActiveGroups();
            return new JArray(used.OrderBy(t => t, StringComparer.Ordinal)
                .Where(t => !listed.Contains(t))
                .Select(t => new JObject
                {
                    ["tool"] = t, ["group"] = ToolGroupCatalog.GroupOf(t) ?? (DiscoveryDefinitions().Any(d => (string)d["name"] == t) ? ToolGroupCatalog.Core : null),
                    ["reason"] = RecipeSchema(t) == null ? "unknown_tool"
                        : !ToolVisibility.IsListed(t, groups) ? "inactive_group"
                        : !reachable.Contains(t) && !DiscoveryDefinitions().Any(d => (string)d["name"] == t)
                            ? "not_exposed_by_permissions_or_bridge" : "not_listed_on_schema_surface"
                }));
        }

        // File integrity and API compatibility answer different questions. Never compare
        // an RVT's bytes with an RTE hash, even when model_path was used to locate a kit.
        private static void AddTemplateHash(JObject result, JObject kit, string kitPath, string modelPath)
        {
            string file = (string)(kit["template"] as JObject)?["file"];
            string path = modelPath;
            if (path == null && !string.IsNullOrWhiteSpace(file))
                path = Path.IsPathRooted(file) ? file : Path.Combine(Path.GetDirectoryName(kitPath), file);
            if (path == null) return;
            string want = (string)(kit["template"] as JObject)?["rte_sha256"];
            string have = null, note;
            if (!path.EndsWith(".rte", StringComparison.OrdinalIgnoreCase)) note = "model_path is not an RTE; template identity cannot be inferred from an RVT";
            else if (!File.Exists(path)) note = "template file is not available";
            else
            {
                try
                {
                    using (var stream = File.OpenRead(path))
                    using (var hash = SHA256.Create())
                        have = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    note = want == null ? "kit does not record rte_sha256" : string.Equals(want, have, StringComparison.OrdinalIgnoreCase) ? "template bytes match" : "template bytes differ";
                }
                catch (IOException) { note = "template file could not be read"; }
                catch (UnauthorizedAccessException) { note = "template file access denied"; }
            }
            result["template_hash_match"] = new JObject
            {
                ["match"] = want == null || have == null ? (JToken)JValue.CreateNull() : string.Equals(want, have, StringComparison.OrdinalIgnoreCase),
                ["kit_hash"] = want, ["template_hash"] = have, ["path"] = path, ["note"] = note
            };
        }
    }
}
