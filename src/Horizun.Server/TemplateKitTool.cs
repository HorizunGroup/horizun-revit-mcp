// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_template_kit: an AI-ready template explains itself.
//
// A template is a box of types. An agent handed a generic one spends most of its
// calls guessing: which type, where its insertion point sits, which host it wants,
// which parameter drives what, and how to prove the result. A Horizun template kit is
// a manifest written FROM the live template by the MCP itself (the type catalog with
// codes, units, measured footprints and placement, selection rules, task playbooks
// bound to THIS server's tools, quality gates and known limits). This tool reads it
// for the agent - host-resident, no Revit, read-only - so one call briefs the agent
// and every later lookup is a search, not an experiment.
//
// Schema "horizun.template-kit/1": {schema, template{}, mcp{}, conventions{}, catalog[],
// rules[], playbook{}, checks[], limitations[]}. Everything in it was written by people
// or by a generator; it is data for the agent, never an instruction to the server.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class TemplateKitTool
    {
        internal const string Schema = "horizun.template-kit/1";
        private const long MaxBytes = 32L * 1024 * 1024;

        internal static JObject Handle(JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string op = (string)args?["operation"] ?? "load";
            if (op == "list") return List(KitsDir());
            if (op == "evidence_schema") return DeclaredEvidenceSchema();
            JObject kit = Load(args, ct, out string path, out string sha, out bool modelParameter);
            switch (op)
            {
                case "load": return Brief(kit, path, sha, args);
                case "find":
                    JObject found = Find(kit, args);
                    found["path"] = path; found["sha256"] = sha;
                    return found;
                case "type": return TypeEntry(kit, args);
                case "rules": return Section(kit, "rules", args, "topic");
                case "playbook": return Playbook(kit, args);
                case "checks":
                    var checks = new JObject { ["checks"] = kit["checks"] ?? new JArray() };
                    if (modelParameter) checks["kit_resolved_by"] = "model_parameter";
                    return checks;
                case "validate": return Validate(kit, path, sha, args);
                case "audit":
                    JObject audited = Audit(kit, args, ct, path);
                    AddKitVersion(audited, kit);
                    if (modelParameter) audited["kit_resolved_by"] = "model_parameter";
                    return audited;
                default: throw new ArgumentException("Unknown operation '" + op + "'. Use list, load, find, type, rules, playbook, checks, validate or audit.");
            }
        }

        // ---- locating and reading the kit -------------------------------------------------
        private static JObject Load(JObject args, CancellationToken ct, out string path, out string sha, out bool modelParameter)
        {
            modelParameter = false;
            path = (string)args?["path"];
            string model = (string)args?["model_path"];
            string name = (string)args?["kit_name"];
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(name)) path = Installed(name);
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(model)) path = Locate(model);
            if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(name) &&
                ((string)args["operation"] == "audit" || (string)args["operation"] == "checks"))
            {
                if (Invoker == null) throw new ArgumentException("No kit beside the model: open the model or pass kit_name.");
                JObject documentInfo = Call("get_document_info", new JObject { ["target_document"] = model }, ct, null, null);
                string openPath = (string)documentInfo?["path"];
                if (string.IsNullOrWhiteSpace(openPath) || !Path.IsPathRooted(model) || !Path.IsPathRooted(openPath) ||
                    !string.Equals(Path.GetFullPath(model), Path.GetFullPath(openPath), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The model_path is not the active open model; open the model or pass kit_name.");
                JObject project = Call("horizun_manage_units", new JObject { ["operation"] = "project_information", ["target_document"] = openPath }, ct, null, null);
                var matches = (project?["parameters"] as JArray ?? new JArray()).OfType<JObject>()
                    .Where(p => (string)p["name"] == "HZ_AI_Kit").ToList();
                if (matches.Count != 1 || string.IsNullOrWhiteSpace((string)matches[0]["value"]))
                    throw new ArgumentException("HZ_AI_Kit could not be read uniquely from the open model; pass kit_name.");
                string modelKit = (string)matches[0]["value"];
                if (Path.GetFileName(modelKit) != modelKit) throw new ArgumentException("HZ_AI_Kit must be an installed kit name; pass kit_name.");
                path = Installed(modelKit);
                if (path == null) throw new ArgumentException("The kit named by HZ_AI_Kit is not installed: " + modelKit + "; pass kit_name.");
                modelParameter = true;
            }
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("No kit found: pass path (the .hzkit.json), kit_name (installed in <data root>/kits; the model's Project Information " +
                                            "parameter HZ_AI_Kit names it) or model_path (kit beside the model/template). operation=list shows the installed kits.");
            if (!Path.IsPathRooted(path)) throw new ArgumentException("path must be absolute.");
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("path must be a .json kit manifest.");
            if (!File.Exists(path)) throw new FileNotFoundException("Kit manifest not found: " + path);
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) throw new ArgumentException("Kit manifest is larger than 32 MiB.");
            byte[] bytes = File.ReadAllBytes(path);
            using (var h = System.Security.Cryptography.SHA256.Create())
                sha = BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            JObject kit;
            try { kit = JObject.Parse(Encoding.UTF8.GetString(bytes)); }
            catch (Exception ex) { throw new ArgumentException("Kit manifest is not valid JSON: " + ex.Message); }
            if ((string)kit["schema"] != Schema) throw new ArgumentException("Unsupported kit schema '" + (string)kit["schema"] + "'; this server reads " + Schema + ".");
            return kit;
        }

        internal static string KitsDir() => Path.Combine(Horizun.Revit.Core.HorizunPaths.DataRoot(), "kits");

        private static string Installed(string name)
        {
            string dir = KitsDir();
            if (!Directory.Exists(dir)) return null;
            string stem = name.EndsWith(".hzkit.json", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 11) : name;
            string exact = Path.Combine(dir, stem + ".hzkit.json");
            return File.Exists(exact) ? exact : null;
        }

        internal static JObject List(string dir)
        {
            var kits = new JArray();
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*.hzkit.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var row = new JObject { ["kit_name"] = Path.GetFileName(f).Replace(".hzkit.json", ""), ["path"] = f };
                    AddKitVersion(row, null);
                    try
                    {
                        JObject k = JObject.Parse(File.ReadAllText(f));
                        AddKitVersion(row, k);
                        row["template"] = k["template"]?["name"]; row["generated_utc"] = k["template"]?["generated_utc"];
                        row["types"] = (k["catalog"] as JArray)?.Count; row["contract_match"] = ContractMatch(k);
                        row["revit_year"] = k["template"]?["revit_year"];
                        AddTemplateHash(row, k, f, null);
                    }
                    catch (Exception ex) { row["error"] = ex.Message; }
                    kits.Add(row);
                }
            return new JObject { ["kits_dir"] = dir, ["kits"] = kits };
        }

        private static string Locate(string model)
        {
            string dir = Directory.Exists(model) ? model : Path.GetDirectoryName(model);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            string stem = Directory.Exists(model) ? null : Path.GetFileNameWithoutExtension(model);
            if (stem != null)
            {
                string exact = Path.Combine(dir, stem + ".hzkit.json");
                if (File.Exists(exact)) return exact;
            }
            string[] any = Directory.GetFiles(dir, "*.hzkit.json");
            return any.Length == 1 ? any[0] : null;
        }

        // ---- operations ------------------------------------------------------------------
        private static void AddKitVersion(JObject result, JObject kit)
        {
            JToken version = kit?["kit_version"];
            bool present = version?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)version);
            result["kit_version"] = present ? version.DeepClone() : JValue.CreateNull();
            if (present) result.Remove("kit_version_reason");
            else result["kit_version_reason"] = kit == null ? "kit could not be read" : "kit does not declare a non-empty kit_version";
        }

        private static JObject Brief(JObject kit, string path, string sha, JObject args)
        {
            var catalog = kit["catalog"] as JArray ?? new JArray();
            var byCategory = catalog.OfType<JObject>().GroupBy(e => (string)e["category"] ?? "?")
                .OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count());
            var byHost = catalog.OfType<JObject>().GroupBy(e => (string)e["placement"]?["host"] ?? (string)e["placement"]?["route_kind"] ?? "none")
                .ToDictionary(g => g.Key, g => g.Count());
            var playbook = kit["playbook"] as JObject ?? new JObject();
            var result = new JObject
            {
                ["path"] = path, ["sha256"] = sha, ["schema"] = Schema,
                ["template"] = kit["template"], ["mcp"] = kit["mcp"],
                ["contract_match"] = ContractMatch(kit),
                ["catalog_summary"] = new JObject { ["types"] = catalog.Count, ["by_category"] = JObject.FromObject(byCategory), ["by_host"] = JObject.FromObject(byHost) },
                ["conventions"] = kit["conventions"],
                ["selection_policy"] = kit["conventions"]?["selection_policy"],
                ["unavailable_tools"] = UnavailableTools(kit),
                ["product_families"] = (kit["product_families"] as JArray ?? new JArray()).Count,
                ["playbook_tasks"] = new JArray(playbook.Properties().Select(p => new JObject { ["task"] = p.Name, ["goal"] = p.Value?["goal"] })),
                ["rule_topics"] = new JArray((kit["rules"] as JArray ?? new JArray()).OfType<JObject>().Select(r => r["topic"])),
                ["checks"] = kit["checks"], ["limitations"] = kit["limitations"],
                ["next"] = "Find types with operation=find (text, category, code, host, dims); every hit carries its code, unit, footprint and the exact horizun_create_elements recipe. " +
                           "Follow operation=playbook task=<name> for the order of work and verify with operation=checks before reporting."
            };
            AddKitVersion(result, kit);
            AddTemplateHash(result, kit, path, (string)args["model_path"]);
            return result;
        }

        private static JObject ContractMatch(JObject kit)
        {
            string want = (string)(kit["mcp"] as JObject)?["contract_hash"];
            string have = Horizun.Contracts.Contract.Hash;
            return new JObject
            {
                ["kit_hash"] = want, ["server_hash"] = have,
                ["match"] = want == null ? (JToken)JValue.CreateNull() : want == have,
                ["note"] = want == null ? "the kit does not record the contract it was generated against"
                    : want == have ? "the kit was generated against this exact contract"
                    : "the kit was generated against another contract: recipes may name arguments this server changed; re-generate the kit or validate each call with a dry run"
            };
        }

        // Words that carry no meaning in a search (es/en): "Mesón DE cocina" must not match everything with "de".
        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.Ordinal)
        { "de", "del", "la", "el", "los", "las", "en", "y", "con", "para", "por", "un", "una", "a", "al", "of", "the", "and", "with", "for", "in", "on", "to" };

        // Specialised kinds that must be ASKED for: a plain "sanitario" is not the accessible one.
        private static readonly Dictionary<string, string[]> Special = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["accessible"] = new[] { "accesible", "accessible", "discapacidad", "discapacitados", "pmr" },
            ["health"] = new[] { "hospital", "hospitalaria", "clinica", "camilla", "examen", "health" },
            ["education"] = new[] { "aula", "docente", "colegio", "escolar", "pupitre", "school", "teacher" },
            ["retail"] = new[] { "comercio", "tienda", "gondola", "caja", "retail", "checkout" },
        };

        private static JObject Find(JObject kit, JObject args)
        {
            string text = (string)args["text"];
            string category = (string)args["category"];
            string code = (string)args["code"];
            string host = (string)args["host"];
            string policy = (string)args["policy"] ?? "existing_first";
            if (policy != "existing_first" && policy != "existing_only" && policy != "allow_create")
                throw new ArgumentException("policy must be existing_first, existing_only or allow_create.");
            int limit = Math.Max(1, Math.Min(50, (int?)args["limit"] ?? 10));
            JObject dims = args["dims"] as JObject;
            JObject near = args["near"] as JObject;
            string[] tokens = Tokens(text).Where(t => !Stop.Contains(t)).ToArray();
            string phrase = string.Join(" ", tokens);
            var hits = new List<Tuple<double, double, JObject>>();
            foreach (JObject e in FindEntries(kit))
            {
                if ((bool?)e["usable"] == false && (bool?)args["include_unusable"] != true) continue;
                bool product = IsProductFamily(e);
                if ((bool?)args["product_only"] == true && !product) continue;
                if (category != null && Norm((string)e["category"]).IndexOf(Norm(category), StringComparison.Ordinal) < 0) continue;
                if (code != null && !((string)e["code"] ?? "").StartsWith(code, StringComparison.Ordinal)) continue;
                if (host != null && !string.Equals((string)e["placement"]?["host"] ?? "none", host, StringComparison.OrdinalIgnoreCase)) continue;
                if (dims != null && !DimsMatch(e, dims)) continue;
                double score = 1;
                if (tokens.Length > 0)
                {
                    string names = string.Join(" ", new[] { (string)e["name_es"], (string)e["name_en"] });
                    string hay = Norm(string.Join(" ", new[] { (string)e["family"], (string)e["type"], (string)e["category"], (string)e["code"], names,
                        string.Join(" ", (e["tags"] as JArray ?? new JArray()).Select(t => (string)t)) }));
                    var words = new HashSet<string>(Tokens(hay));
                    foreach (JProperty p in (SearchDimensions(e) ?? new JObject()).Properties())
                        if (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float)
                            words.Add(((double)p.Value).ToString("0.###", CultureInfo.InvariantCulture));
                    int hit = tokens.Count(t => words.Contains(t) || words.Any(w => w.StartsWith(t, StringComparison.Ordinal) && t.Length >= 3));
                    if (hit == 0) continue;
                    score = (double)hit / tokens.Length;
                    // the object's own name, word for word: "Ducha" is the shower, not the shower screen
                    foreach (string n in new[] { (string)e["name_es"], (string)e["name_en"] })
                    {
                        string nn = string.Join(" ", Tokens(n).Where(t => !Stop.Contains(t)));
                        if (nn.Length > 0 && nn == phrase) score += 1.0;
                        else if (nn.Length > 0 && nn.StartsWith(phrase + " ", StringComparison.Ordinal)) score += 0.3;
                    }
                    // a specialised kind the query did not ask for ranks below the ordinary one
                    foreach (JToken tag in (e["tags"] as JArray ?? new JArray()))
                        if (Special.TryGetValue((string)tag, out string[] ask) && !tokens.Any(t => ask.Contains(t))) score *= 0.5;
                }
                double distance = NearDistance(e, near);
                if (policy == "existing_only" && near != null && !DimsMatch(e, near)) continue;
                hits.Add(Tuple.Create(score, distance, e));
            }
            var ranked = hits.OrderByDescending(h => near == null || DimsMatch(h.Item3, near)).ThenByDescending(h => h.Item1).ThenBy(h => h.Item2).ThenByDescending(h => IsProductFamily(h.Item3))
                .ThenBy(h => (string)h.Item3["key"], StringComparer.Ordinal).ToList();
            JObject best = ranked.FirstOrDefault()?.Item3;
            bool exactSize = near == null || (best != null && DimsMatch(best, near));
            var reply = new JObject
            {
                ["total"] = ranked.Count, ["shown"] = Math.Min(limit, ranked.Count),
                ["policy"] = policy, ["status"] = best == null ? "no_match" : "matched",
                ["hits"] = new JArray(ranked.Take(limit).Select(h => { var o = (JObject)h.Item3.DeepClone(); o["score"] = Math.Round(h.Item1, 3);
                    o["dimensions"] = new JObject { ["search_basis"] = o["nominal"] is JObject ? "nominal" : "dims", ["nominal_mm"] = o["nominal"], ["envelope_mm"] = o["dims"] };
                    if (near != null) o["near_distance_mm"] = Math.Round(h.Item2, 1);
                    AddProductFamilyRoute(o); return o; })),
                ["how_to_place"] = "Resolve type_id with the hit's resolve call, then send hit.placement.recipe to horizun_create_elements with your coordinates (dry run first, then apply with the token)."
            };
            if (policy == "existing_only" && best == null) reply.Remove("how_to_place");
            if (policy != "existing_only" && near != null && best != null && !exactSize)
                reply["size_missing"] = new JObject
                {
                    ["note"] = best["new_size"] is JObject && (bool?)best["resize_supported"] != false
                        ? "No exact size. Create a size only if the request permits it, using this new_size recipe."
                        : "No exact size and no executable resize recipe. Resolve the missing size before placement.",
                    ["from_key"] = best["key"], ["new_size"] = best["new_size"]
                };
            return reply;
        }

        private static double NearDistance(JObject e, JObject near)
        {
            if (near == null) return 0;
            JObject have = SearchDimensions(e); double d = 0;
            foreach (JProperty p in near.Properties())
            {
                double? v = (double?)have?[p.Name];
                d += v == null ? 1e6 : Math.Abs(v.Value - (double)p.Value);
            }
            return d;
        }

        private static bool DimsMatch(JObject e, JObject dims)
        {
            JObject have = SearchDimensions(e);
            foreach (JProperty p in dims.Properties())
            {
                double? v = (double?)have?[p.Name];
                if (v == null) return false;
                if (p.Value is JArray range && range.Count == 2)
                {
                    if (range[0].Type != JTokenType.Null && v < (double)range[0]) return false;
                    if (range[1].Type != JTokenType.Null && v > (double)range[1]) return false;
                }
                else if (p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float)
                {
                    if (Math.Abs(v.Value - (double)p.Value) > 0.5) return false;
                }
            }
            return true;
        }

        private static JObject TypeEntry(JObject kit, JObject args)
        {
            string key = (string)args["key"];
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required (family|type, as returned by find).");
            JObject e = FindEntries(kit).FirstOrDefault(x => (string)x["key"] == key);
            if (e == null) throw new ArgumentException("No catalog entry with key '" + key + "'.");
            var entry = (JObject)e.DeepClone();
            AddProductFamilyRoute(entry);
            return new JObject { ["entry"] = entry };
        }

        private static JObject Section(JObject kit, string name, JObject args, string field)
        {
            var all = kit[name] as JArray ?? new JArray();
            string want = (string)args[field];
            if (want == null) return new JObject { [name] = all };
            return new JObject { [name] = new JArray(all.OfType<JObject>().Where(r => Norm((string)r[field]).Contains(Norm(want)))) };
        }

        private static JObject Playbook(JObject kit, JObject args)
        {
            var pb = kit["playbook"] as JObject ?? new JObject();
            string task = (string)args["task"];
            if (task == null) return new JObject { ["tasks"] = new JArray(pb.Properties().Select(p => new JObject { ["task"] = p.Name, ["goal"] = p.Value?["goal"] })) };
            JProperty hit = pb.Properties().FirstOrDefault(p => string.Equals(p.Name, task, StringComparison.OrdinalIgnoreCase));
            if (hit == null) throw new ArgumentException("No playbook task '" + task + "'. Tasks: " + string.Join(", ", pb.Properties().Select(p => p.Name)));
            return new JObject { ["task"] = hit.Name, ["playbook"] = hit.Value };
        }

        private static JObject Validate(JObject kit, string path, string sha, JObject args)
        {
            var problems = new JArray();
            foreach (string req in new[] { "template", "mcp", "catalog", "playbook", "checks" })
            {
                if (kit[req] == null) problems.Add("missing section '" + req + "'");
                else if ((req == "catalog" || req == "checks") ? !(kit[req] is JArray) : !(kit[req] is JObject))
                    problems.Add("invalid section type '" + req + "'");
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string section in new[] { "catalog", "checks", "product_families" })
                if (kit[section] is JArray items && items.Any(e => !(e is JObject))) problems.Add("'" + section + "' must contain objects");
            int n = 0;
            foreach (JObject e in (kit["catalog"] as JArray ?? new JArray()).OfType<JObject>())
            {
                n++;
                string key = (string)e["key"];
                if (string.IsNullOrWhiteSpace(key)) { problems.Add("catalog entry " + n + " has no key"); continue; }
                if (!keys.Add(key)) problems.Add("duplicate key '" + key + "'");
                bool component = (string)(e["placement"] as JObject)?["route_kind"] == "component";
                if (!component && string.IsNullOrWhiteSpace((string)e["code"])) problems.Add("'" + key + "' has no code");
                if (!(e["placement"] is JObject)) problems.Add("'" + key + "' has no valid placement");
                foreach (string field in new[] { "dims", "nominal" })
                    if (e[field] != null && e[field].Type != JTokenType.Null && !(e[field] is JObject)) problems.Add("'" + key + "' has invalid " + field);
                if (e["usable"] != null && e["usable"].Type != JTokenType.Boolean) problems.Add("'" + key + "' usable must be boolean");
            }
            foreach (JObject e in (kit["product_families"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string key = (string)e["key"];
                if (string.IsNullOrWhiteSpace(key)) problems.Add("product family has no key");
                else if (!keys.Add(key)) problems.Add("duplicate key '" + key + "'");
                if (!IsProductFamily(e)) problems.Add("'" + key + "' has no product metadata");
                JObject probe = (JObject)e.DeepClone();
                if (IsProductFamily(probe))
                {
                    AddProductFamilyRoute(probe);
                    string status = (string)probe["product_family"]?["status"];
                    if (status == "invalid_source_path" || status == "family_or_type_missing")
                        problems.Add("'" + key + "': " + (string)probe["product_family"]?["problem"]);
                }
            }
            JArray recipes = ValidateRecipes(kit);
            var result = new JObject { ["path"] = path, ["sha256"] = sha, ["entries"] = n, ["valid"] = problems.Count == 0 && recipes.Count == 0,
                ["kit_valid"] = problems.Count == 0, ["recipes_valid"] = recipes.Count == 0, ["recipes_checked"] = Recipes(kit).Count(), ["recipe_problems"] = recipes, ["problems"] = problems,
                ["contract_match"] = ContractMatch(kit) };
            AddKitVersion(result, kit);
            AddTemplateHash(result, kit, path, (string)args["model_path"]);
            return result;
        }

        private static JObject SearchDimensions(JObject entry) => entry["nominal"] as JObject ?? entry["dims"] as JObject;

        // ---- text helpers ----------------------------------------------------------------
        internal static string Norm(string s) => TextFold.Fold(s);

        private static string[] Tokens(string s) =>
            Norm(s).Split(new[] { ' ', '_', '-', '|', ',', ';', '/', '(', ')', '.', ':' }, StringSplitOptions.RemoveEmptyEntries)
                   .Where(t => t.Length >= 2).Distinct().ToArray();
    }
}
