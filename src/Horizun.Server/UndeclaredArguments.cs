// -----------------------------------------------------------------------------
// Horizun MCP server - undeclared arguments are refused before dispatch.
//
// Almost every input schema in the contract says additionalProperties:false, and
// until this file nothing enforced it: ContractSchemaCheck only advises AFTER a
// failure, and the add-in reads the keys it knows and ignores the rest. So a call
// with `description` instead of `purpose`, or `dry_run` on a tool that has no
// rehearsal, ran - and answered a different question from the one the caller
// asked. Measured on the failure bench (October 2026, cases F1 and F4): both
// passed silently on Revit 2024 and 2025.
//
// The rule is the schema's own, applied at the top level of the arguments:
//
//   * additionalProperties:false - every key must be declared in `properties`, or
//     in the `properties` of a top-level oneOf/anyOf/allOf/then/else branch (a
//     variant's fields are top-level arguments too). Anything else is refused by
//     name, with the contract URI, before quarantine, task creation or routing to
//     Revit: nothing runs and nothing is written.
//   * a schema that leaves extras open (additionalProperties missing, true or a
//     schema, or patternProperties) is PASSTHROUGH and stays so. The 23 tools in
//     that state are pinned by a test, so a new one is a decision, not an accident.
//
// Kept on purpose, everywhere:
//   * `_meta`: MCP's reserved key. A client that puts request metadata inside
//     `arguments` (some do) is not making a mistake about this tool.
//   * `expected_document` and `target_document_title` on a tool that declares
//     target_document: DocumentGate accepts both as aliases, and three tool
//     descriptions promise it ("Aliases accepted for compatibility").
//
// Nested objects (a row inside `actions`, a `what_if` block) are not judged here:
// the per-command parsers and ToolInputRules already refuse unknown fields where it
// matters, and SchemaHelp names the exact pointer when a call fails.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class UndeclaredArguments
    {
        /// <summary>Accepted on every tool: MCP reserves it for client metadata.</summary>
        internal static readonly string[] ClientKeys = { "_meta" };

        /// <summary>Accepted wherever target_document is declared: DocumentGate's aliases.</summary>
        internal static readonly string[] DocumentAliases = { "expected_document", "target_document_title", "document", "doc" };

        private const int MaxListedNames = 40;

        /// <summary>{tool, sent name, declared name}: the same value under the name the schema declares.</summary>
        internal static readonly string[][] SameValueRenames =
        {
            new[] { "horizun_navigate", "action", "operation" },
            new[] { "horizun_model_diff", "action", "operation" },
            new[] { "horizun_query_structure", "operation", "mode" },
            new[] { "horizun_export", "sheet_ids", "view_ids" },
            new[] { "horizun_execute_python", "file_path", "code_path" },
        };

        /// <summary>The names a closed schema admits at the top level, or null when the
        /// schema leaves extra arguments open (passthrough).</summary>
        internal static HashSet<string> Declared(JToken schemaToken)
        {
            if (!(schemaToken is JObject schema)) return null;
            if (!(schema["additionalProperties"] is JValue ap) || ap.Type != JTokenType.Boolean || (bool)ap) return null;
            if (schema["patternProperties"] != null) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            Collect(schema, names);
            if (names.Contains("target_document"))
                foreach (string alias in DocumentAliases) names.Add(alias);
            return names;
        }

        private static void Collect(JToken node, HashSet<string> names)
        {
            if (!(node is JObject o)) return;
            if (o["properties"] is JObject props)
                foreach (JProperty p in props.Properties()) names.Add(p.Name);
            foreach (string k in new[] { "oneOf", "anyOf", "allOf" })
                if (o[k] is JArray branches)
                    foreach (JToken b in branches) Collect(b, names);
            Collect(o["then"], names);
            Collect(o["else"], names);
        }

        /// <summary>The argument names this call carries that the tool's schema does not
        /// admit, in the order sent. Empty for an open schema or an unknown tool.</summary>
        internal static List<string> Find(string tool, JObject args)
        {
            var found = new List<string>();
            if (args == null || string.IsNullOrEmpty(tool)) return found;
            JToken schema = Contract.Find(tool)?.InputSchema;
            HashSet<string> declared = Declared(schema);
            bool identity = ElementIdentityGuard.Covers(tool);
            if (declared == null)
            {
                // An OPEN schema lets unknown arguments through, but never the element-identity fields on a tool that does not verify
                // them: they would be ignored while the caller believes the ids were checked (review of R2R, m-1).
                if (schema != null && !identity)
                {
                    HashSet<string> own = ElementIdentityGuard.SchemaNames(schema);
                    foreach (JProperty p in args.Properties())
                        if (ElementIdentityGuard.IsIdentityName(p.Name) && !own.Contains(p.Name) && !found.Contains(p.Name)) found.Add(p.Name);
                }
                return found;
            }
            // A covered writer accepts the element-identity fields wherever its schema is closed and does not repeat them
            // (tools/list has no room): the add-in verifies them before the command runs. Any other tool still refuses them.
            foreach (JProperty p in args.Properties())
                if (!declared.Contains(p.Name) && Array.IndexOf(ClientKeys, p.Name) < 0 && !(identity && ElementIdentityGuard.IsIdentityName(p.Name)) &&
                    !found.Contains(p.Name)) found.Add(p.Name);
            return found;
        }

        internal static JObject CreationRowRefusal(JObject args)
        {
            // Multi-row validation in Revit reports every index and mixed capability gaps.
            if (!(args["elements"] is JArray elements) || elements.Count != 1 ||
                !(elements[0] is JObject item) || item["kind"]?.Type != JTokenType.String) return null;
            string kind = item.Value<string>("kind").ToLowerInvariant();
            var issue = ToolInputRules.ForeignCreationField(item, kind, ElementIdentityGuard.IsIdentityName);
            return issue == null ? null : CreationFieldErrors.Describe(kind, 0, issue);
        }

        /// <summary>Normalizes only unambiguous, value-preserving aliases. Conflicts and type conversions
        /// that could change scope remain refusals. The returned warnings contain field names only.</summary>
        internal static JObject NormalizeAliases(string tool, JObject args, out List<string> warnings, out string refusal)
        {
            warnings = new List<string>();
            refusal = null;
            JObject copy = args == null ? new JObject() : (JObject)args.DeepClone();
            HashSet<string> declared = Declared(Contract.Find(tool)?.InputSchema);
            if (declared == null) return copy;

            if (tool == "horizun_document_session" && copy["action"] != null)
                if (!MoveAlias(copy, "action", "operation", warnings, out refusal)) return copy;

            // 5.0.6 (telemetry 2026-11-13): names that still reached esquema_invalido on 5.0.5 and mean exactly one
            // declared argument of that tool, with the same value. Anything that would compose or convert stays a refusal.
            foreach (string[] rename in SameValueRenames)
                if (tool == rename[0] && copy[rename[1]] != null && declared.Contains(rename[2]))
                    if (!MoveAlias(copy, rename[1], rename[2], warnings, out refusal)) return copy;

            if (tool == "horizun_document_session" &&
                string.Equals((string)copy["operation"], "close", StringComparison.OrdinalIgnoreCase) &&
                copy["discard_changes"] != null)
                if (!MoveAlias(copy, "discard_changes", "discard_unsaved", warnings, out refusal)) return copy;

            foreach (string alias in new[] { "document", "doc" })
                if (copy[alias] != null && declared.Contains("target_document"))
                    if (!MoveAlias(copy, alias, "target_document", warnings, out refusal)) return copy;

            if (tool == "horizun_open_document" || tool == "horizun_document_session")
            {
                string operation = (string)copy["operation"];
                bool sessionFileOperation = tool != "horizun_document_session" || operation == "open" || operation == "inspect";
                if (!sessionFileOperation && tool == "horizun_document_session" && (copy["path"] != null || copy["model_path"] != null))
                {
                    refusal = "path and model_path are aliases for file_path only with document_session operation=open or inspect. For save/close use target_document; for save_as use save_as_path. Nothing was run.";
                    return copy;
                }
                string canonical = !sessionFileOperation ? null : tool == "horizun_open_document" ? "path" : "file_path";
                if (canonical != null)
                    foreach (string alias in new[] { "file_path", "model_path", "path" })
                        if (alias != canonical && copy[alias] != null)
                            if (!MoveAlias(copy, alias, canonical, warnings, out refusal)) return copy;
            }
            if ((tool == "horizun_query_model" || tool == "horizun_list_elements") && declared.Contains("max_rows") && copy["limit"] != null)
                if (!MoveAlias(copy, "limit", "max_rows", warnings, out refusal)) return copy;

            if (tool == "horizun_query_model" && copy["category"]?.Type == JTokenType.String)
            {
                if (copy["categories"] != null)
                {
                    refusal = "Send either category or categories, not both; the intended category filter is ambiguous. Nothing was read.";
                    return copy;
                }
                copy["categories"] = new JArray((string)copy["category"]);
                copy.Remove("category");
                warnings.Add("category accepted as categories (one value)");
            }

            if (tool == "horizun_execute_python" && copy["dry_run"] != null)
            {
                if (copy["dry_run"].Type != JTokenType.Boolean || !(bool)copy["dry_run"])
                {
                    refusal = "horizun_execute_python has no dry_run mode. Use preflight=true to validate without executing, or remove dry_run and send an execution request explicitly. Nothing was run.";
                    return copy;
                }
                if (copy["preflight"] != null && !JToken.DeepEquals(copy["preflight"], copy["dry_run"]))
                {
                    refusal = "Send either dry_run or preflight, not conflicting values. Nothing was run.";
                    return copy;
                }
                copy.Remove("dry_run");
                copy["preflight"] = true;
                warnings.Add("dry_run=true accepted as preflight=true; script will not execute");
            }

            if (tool == "horizun_query_model" && copy["ids"]?.Type == JTokenType.String)
            {
                // 5.0.4: "123" or "1, 2, 3" - every piece a non-negative integer, or nothing is converted.
                string[] parts = ((string)copy["ids"]).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var ids = new JArray();
                foreach (string part in parts)
                {
                    if (!long.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id))
                    {
                        refusal = "ids must be non-negative integers (\"123\" or \"1, 2, 3\") when used as a compatibility alias for element_ids; '" +
                                  part + "' is not one. Send element_ids as an integer array; nothing was read.";
                        return copy;
                    }
                    ids.Add(id);
                }
                if (ids.Count == 0)
                {
                    refusal = "ids is empty. Send element_ids as an integer array; nothing was read.";
                    return copy;
                }
                if (copy["element_ids"] != null)
                {
                    refusal = "Send either ids or element_ids, not both; the intended element set is ambiguous. Nothing was read.";
                    return copy;
                }
                copy.Remove("ids");
                copy["element_ids"] = ids;
                warnings.Add("ids converted to element_ids (" + ids.Count + (ids.Count == 1 ? " integer)" : " integers)"));
            }

            // 5.0.4 (field 2026-11): mode=save_local sent instead of operation=save_local.
            if (tool == "horizun_save_document" && copy["mode"]?.Type == JTokenType.String)
            {
                string mode = (string)copy["mode"];
                if (mode != "save_local" && mode != "sync_with_central" && mode != "relinquish_own")
                {
                    refusal = "horizun_save_document takes operation (save_local, sync_with_central or relinquish_own), not mode; '" + mode +
                              "' is none of them. Nothing was saved.";
                    return copy;
                }
                if (!MoveAlias(copy, "mode", "operation", warnings, out refusal)) return copy;
            }

            // 5.0.4 (field 2026-11): the script's arguments sent as args, often as a JSON string.
            if (tool == "horizun_execute_python" && copy["args"] != null)
            {
                JToken scriptArgs = copy["args"];
                if (scriptArgs.Type == JTokenType.String)
                {
                    try { scriptArgs = JObject.Parse((string)scriptArgs); }
                    catch (Newtonsoft.Json.JsonException)
                    {
                        refusal = "args is not a JSON object. Send the script's arguments as arguments, an object the script reads with json.loads(HORIZUN_ARGS_JSON). Nothing was run.";
                        return copy;
                    }
                }
                if (scriptArgs.Type != JTokenType.Object)
                {
                    refusal = "args must be an object. Send the script's arguments as arguments, an object the script reads with json.loads(HORIZUN_ARGS_JSON). Nothing was run.";
                    return copy;
                }
                copy["args"] = scriptArgs;
                if (!MoveAlias(copy, "args", "arguments", warnings, out refusal)) return copy;
            }
            return copy;
        }

        private static bool MoveAlias(JObject args, string alias, string canonical, List<string> warnings, out string refusal)
        {
            refusal = null;
            JToken from = args[alias], to = args[canonical];
            if (to != null && !JToken.DeepEquals(from, to))
            {
                refusal = "Send either " + alias + " or " + canonical + "; the values conflict and the intended scope is ambiguous. Nothing was run or written.";
                return false;
            }
            args.Remove(alias);
            if (to == null) args[canonical] = from;
            warnings.Add(alias + " accepted as " + canonical);
            return true;
        }

        /// <summary>The refusal: text and a structured detail (error_code esquema_invalido,
        /// the names, the declared list, suggestions, write_started=false).</summary>
        internal static JObject Refusal(string tool, IList<string> undeclared, JObject args = null)
        {
            JToken schemaOfTool = Contract.Find(tool)?.InputSchema;
            HashSet<string> declared = Declared(schemaOfTool) ?? ElementIdentityGuard.SchemaNames(schemaOfTool);
            bool acceptsIdentity = ElementIdentityGuard.Covers(tool);
            if (acceptsIdentity) foreach (string name in ElementIdentityGuard.AcceptedNames) declared.Add(name);
            List<string> listed = declared.Where(n => Array.IndexOf(DocumentAliases, n) < 0)
                                          .OrderBy(n => n, StringComparer.Ordinal).ToList();
            string uri = McpResources.ContractToolPrefix + tool;

            var suggestions = new JObject();
            var named = new List<string>();
            foreach (string name in undeclared)
            {
                string guess = tool == "horizun_execute_python" && name == "mode"
                    ? "read_only"
                    : tool == "horizun_execute_python" && name == "timeout_ms" ? "timeout_s"
                    : tool == "horizun_execute_python" && name == "dry_run" ? "preflight" : Suggest(name, listed);
                if (guess != null) suggestions[name] = guess;
                named.Add("'" + name + "'" + (guess != null ? " (did you mean '" + guess + "'?)" : ""));
            }

            string declaredText = string.Join(", ", listed.Take(MaxListedNames)) +
                                  (listed.Count > MaxListedNames ? ", ... (+" + (listed.Count - MaxListedNames) + " more)" : "");
            string text =
                "Error: '" + tool + "' does not take the argument" + (undeclared.Count == 1 ? " " : "s ") +
                string.Join(", ", named) + ". Nothing was run and nothing was written. The tool's schema does not " +
                "declare " + (undeclared.Count == 1 ? "it" : "them") + ", and an argument the tool does not read would " +
                "be silently ignored - the call would answer a different question from the one you asked. Send only " +
                "the declared arguments: " + declaredText + ". Exact schema: " + uri + ".";
            if (acceptsIdentity)
                text += " This tool also accepts the element-identity fields (" + string.Join(", ", ElementIdentityGuard.AcceptedNames) +
                        "), spelled exactly so, on any node that holds element ids; they are not repeated in tools/list. " + uri + " lists them.";
            if (tool == "horizun_execute_python" && undeclared.Contains("timeout_ms"))
                text += " did you mean timeout_s (seconds)?";
            if (tool == "horizun_export" && undeclared.Any(n => n == "output_folder" || n == "file_name" || n == "output_dir"))
                text += " export takes ONE output_path: the absolute file, extension matching format (a folder only for rfa, or pdf with pdf_combine=false; name the files with pdf_name_pattern).";
            if (tool == "horizun_job_status" && undeclared.Contains("wait_ms"))
                text += " Use wait_s (seconds, up to 60) with the job_id to wait for a state change.";
            if (tool == "horizun_open_document" && undeclared.Any(n => n == "operation" || n == "dry_run" || n == "target_document"))
                text += " to list or manage open documents use horizun_document_session.";

            var detail = Horizun.Revit.Core.ErrorCodes.Detail(Horizun.Revit.Core.ErrorCodes.EsquemaInvalido, extra: new JObject
            {
                ["undeclared_arguments"] = new JArray(undeclared.ToArray()),
                ["declared_arguments"] = new JArray(listed.ToArray()),
                ["contract_uri"] = uri,
                ["schema_rejection"] = new JObject
                {
                    ["path"] = "/" + undeclared.FirstOrDefault(),
                    ["rule"] = "additional_property"
                },
                ["accepts_identity_fields"] = acceptsIdentity,
                ["write_started"] = false,
                ["changes_applied"] = false
            });
            JObject suggestedCall = SuggestedCall(tool, undeclared);
            if (suggestedCall == null)
                suggestedCall = new JObject { ["tool"] = tool, ["contract_uri"] = uri };
            if (args != null && (string)suggestedCall["tool"] == tool)
            {
                var corrected = (JObject)args.DeepClone();
                foreach (string name in undeclared) corrected.Remove(name);
                suggestedCall["arguments"] = corrected;
            }
            if (suggestedCall != null) detail["suggested_call"] = suggestedCall;
            if (suggestions.Count > 0) detail["suggestions"] = suggestions;
            JObject help = ArgumentHelp(tool, undeclared);
            if (help != null)
            {
                detail["argument_help"] = help;
                text += " " + (string)help["reason"] + " Example: " +
                        (string)help["tool"] + " " + help["arguments"].ToString(Newtonsoft.Json.Formatting.None) + ".";
            }
            JObject minimal = SchemaHelp.ContractExample(tool);
            if (minimal != null)
            {
                detail["minimal_valid_call"] = new JObject { ["tool"] = tool, ["arguments"] = minimal };
                text += " Minimal valid example: " + tool + " " + minimal.ToString(Newtonsoft.Json.Formatting.None) + ".";
            }
            Log.SchemaRejection(tool, "/" + undeclared.FirstOrDefault(), "additional_property");
            return new JObject { ["text"] = text, ["detail"] = detail };
        }

        private static JObject SuggestedCall(string tool, IList<string> undeclared)
        {
            string destination = tool, argument = null;
            if (tool == "horizun_open_document" && (undeclared.Contains("operation") || undeclared.Contains("dry_run")))
            { destination = "horizun_document_session"; argument = undeclared.Contains("operation") ? "operation" : "dry_run"; }
            else if (tool == "horizun_query_model" && undeclared.Contains("category")) argument = "categories";
            else if (tool == "horizun_execute_python" && undeclared.Contains("timeout_ms")) argument = "timeout_s";
            if (argument == null || !(Contract.Find(destination)?.InputSchema?["properties"] is JObject properties) || properties[argument] == null) return null;
            return new JObject { ["tool"] = destination, ["argument"] = argument,
                ["contract_uri"] = McpResources.ContractToolPrefix + destination };
        }

        // Fixed, public examples only: never echo the caller's family, model or path.
        // These are suggestions, not argument rewrites or implicit model actions.
        private static JObject ArgumentHelp(string tool, IList<string> undeclared)
        {
            string next = null, reason = null;
            JObject example = null;
            if (tool == "horizun_execute_python" && undeclared.Contains("mode"))
            {
                next = tool;
                example = new JObject { ["target_document"] = "Example", ["purpose"] = "Read model", ["code"] = "__output__ = {}", ["read_only"] = true };
                reason = "execute_python has no mode field. For read-only execution remove mode and use the boolean read_only: true; supply the actual document and script.";
            }
            else if (tool == "horizun_list_elements" && undeclared.Contains("element_ids"))
            {
                next = "horizun_query_model";
                example = new JObject { ["element_ids"] = new JArray(123), ["include_links"] = false };
                reason = "list_elements lists a category; query_model accepts element_ids as an array of integers, not a string. Replace 123 with an id read from this document.";
            }
            else if ((tool == "horizun_list_elements" && undeclared.Contains("family")) ||
                     (tool == "horizun_query_model" && undeclared.Contains("category")))
            {
                next = "horizun_query_model";
                example = new JObject { ["categories"] = new JArray("OST_Walls"), ["family"] = "Example", ["include_links"] = false };
                reason = "Use query_model for a family filter. Its category filter is categories: an array of BuiltInCategory tokens or localized names.";
            }
            else if (tool == "horizun_manage_views" && undeclared.Contains("operation"))
            {
                next = "horizun_query_planimetry";
                example = new JObject { ["mode"] = "views" };
                reason = "To list views use query_planimetry mode=views. manage_views changes views through actions; it has no top-level operation=list.";
            }
            else if (tool == "horizun_navigate" && undeclared.Contains("action"))
            {
                next = tool;
                example = new JObject { ["operation"] = "select", ["element_ids"] = new JArray(123) };
                reason = "The field is operation, not action. Choose select, clear_selection, zoom, select_and_zoom or open_view explicitly; replace 123 with an id read from this document.";
            }
            else if (tool == "horizun_navigate" && undeclared.Contains("view_name"))
            {
                next = "get_document_info";
                example = new JObject { ["operation"] = "context" };
                reason = "open_view needs view_id (integer), not view_name. Read the view ids with get_document_info operation=context, then call navigate with operation=open_view and the chosen view_id.";
            }
            else if (tool == "horizun_export" && undeclared.Contains("response_mode"))
            {
                next = tool;
                example = new JObject { ["format"] = "pdf", ["dry_run"] = true,
                    ["view_ids"] = new JArray(123), ["output_path"] = "D:\\Exports\\example.pdf", ["target_document"] = "Example" };
                reason = "export has no response_mode argument. Remove that field. For PDF, supply view_ids as an integer array and an absolute output_path ending in .pdf; rehearse before applying. Replace the example id, destination and document with values read for this task.";
            }
            else if (tool == "horizun_model_diff" && undeclared.Contains("baseline_id"))
            {
                next = tool;
                example = new JObject { ["operation"] = "compare", ["before"] = "SNAPSHOT_ID", ["after"] = "active", ["target_document"] = "Example" };
                reason = "compare uses before for the snapshot id, not baseline_id. Obtain an id from snapshot or list and compare it with the intended active document; identity and scope are checked and reported.";
            }
            return example == null ? null : new JObject { ["tool"] = next, ["arguments"] = example, ["reason"] = reason };
        }

        // The confusions the field logs show, by meaning rather than spelling. Each
        // applies only when the tool declares the target and not the source.
        private static readonly Dictionary<string, string[]> Meaning = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["description"] = new[] { "purpose", "comment", "note" },
            ["dry_run"] = new[] { "preflight" },
            ["file_path"] = new[] { "code_path", "path", "file_paths" },
            ["path"] = new[] { "file_path", "code_path" },
            ["script"] = new[] { "code" },
            ["source"] = new[] { "code" },
            ["document"] = new[] { "target_document" },
            ["target"] = new[] { "target_document" },
            ["ids"] = new[] { "element_ids" },
            ["element_id"] = new[] { "element_ids" },
            ["limit"] = new[] { "max_rows", "top" },
            // Live 5.0.4: query_model with max_results got no hint (edit distance 4 from max_rows).
            ["max_results"] = new[] { "max_rows", "top", "limit" },
            ["max_items"] = new[] { "max_rows", "top", "limit" },
            ["max_count"] = new[] { "max_rows", "top", "limit" },
            ["page_size"] = new[] { "max_rows", "top", "limit" }
        };

        /// <summary>The declared name a caller most plausibly meant, or null.</summary>
        internal static string Suggest(string sent, IList<string> declared)
        {
            if (string.IsNullOrEmpty(sent) || declared == null || declared.Count == 0) return null;
            string exactCase = declared.FirstOrDefault(d => string.Equals(d, sent, StringComparison.OrdinalIgnoreCase));
            if (exactCase != null) return exactCase;
            if (Meaning.TryGetValue(sent, out string[] meant))
                foreach (string m in meant)
                    if (declared.Contains(m)) return m;
            string best = null;
            int bestDistance = int.MaxValue;
            int limit = Math.Max(1, Math.Min(3, sent.Length / 3));
            foreach (string d in declared)
            {
                int distance = Distance(sent.ToLowerInvariant(), d.ToLowerInvariant());
                if (distance <= limit && distance < bestDistance) { best = d; bestDistance = distance; }
            }
            return best;
        }

        private static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                int[] t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
