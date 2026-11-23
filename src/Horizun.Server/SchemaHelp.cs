// -----------------------------------------------------------------------------
// Horizun MCP server - schema_help on a failed call.
//
// tools/list advertises an abridged copy of each schema (Tools.CompactSchema). When
// a call fails AND its arguments violate the FULL contract, the reply carries the
// failing JSON pointers and the exact branch of the variant the caller was trying to
// build, so a model corrects itself in one step instead of guessing from a
// shortened description.
//
// ADVICE ONLY. This runs after the verdict: it never turns a success into a failure
// or a failure into a success, never touches the arguments the add-in received, and
// attaches nothing when the contract finds nothing (a semantic refusal needs no
// schema). The validator is ContractSchemaCheck, the same one the example payloads
// are tested with; the real validation still lives where it always did (ToolInputRules
// in the add-in, the per-command parsers, the host-tool refusals).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;

namespace Horizun.Server
{
    internal static class SchemaHelp
    {
        internal const int MaxViolations = 20;
        internal const int MaxViolationChars = 512;
        internal const int MaxInlineSchemas = 3;
        internal const int MaxInlineSchemaBytes = 12288;
        internal const int MaxBytes = 16384;
        private const int MaxVariantEntries = 32;

        /// <summary>The result, with structuredContent.schema_help when the call failed
        /// (isError, or a rehearsal that counted invalid rows) and its arguments violate
        /// the contract; an error reply with no structuredContent gains one text line
        /// instead. Pure and never throws: any failure returns the result unchanged.</summary>
        internal static JToken Attach(JToken result, string tool, JToken arguments)
            => Attach(result, tool, arguments, ContractSchemaCheck.Validate);

        internal static JToken Attach(JToken result, string tool, JToken arguments,
                                      Func<JToken, JToken, List<string>> validate)
        {
            try { return AttachCore(result, tool, arguments, validate); }
            catch { return result; }
        }

        private static JToken AttachCore(JToken result, string tool, JToken arguments,
                                         Func<JToken, JToken, List<string>> validate)
        {
            // A task handle is not a tool result; the task's own result passes through
            // here when it completes (McpTasks runs CallTool).
            if (!(result is JObject r) || !(r["content"] is JArray) || r["task"] != null) return result;
            bool isError = r["isError"]?.Type == JTokenType.Boolean && (bool)r["isError"];
            JToken sc = r["structuredContent"];
            if (sc != null && sc.Type != JTokenType.Object) return result;
            bool invalidRows = !isError && sc is JObject s && IsPositive(s["invalid"]);
            if (!isError && !invalidRows) return result;
            if (string.IsNullOrEmpty(tool)) return result;
            JToken schema = IdentitySchema.For(tool);
            if (schema == null) return result;

            JToken args = arguments == null || arguments.Type == JTokenType.Null ? new JObject() : arguments;
            List<string> violations = validate(args, schema);
            if (violations == null || violations.Count == 0) return result;

            JObject help = Build(tool, args, violations);
            string telemetryViolation = MostSpecificViolation(violations);
            Log.SchemaRejection(tool, FailurePath(telemetryViolation), FailureRule(telemetryViolation));

            // Copy before writing: the caller's object may be shared (a cached or replayed
            // reply), and a failure below must leave it exactly as it was.
            var copy = (JObject)r.DeepClone();
            var structured = copy["structuredContent"] as JObject;

            // An error that carried no structure keeps none: the advice travels as one line
            // of its text instead. Creating structuredContent there changed what readers of
            // the envelope see (review 2026-09-26): ProcedureRun judges a step by its
            // structuredContent, and a client that forwards structuredContent in place of
            // the text blocks would show the model the advice and hide the error itself
            // ("no Revit is reachable"). A success with invalid rows always has structure.
            if (structured != null) structured["schema_help"] = help;

            // A SUCCESS keeps its text exactly the payload - clients parse it as one JSON
            // document (the same rule McpResult.Structured follows for the fallback block).
            // An error is prose already, so it gains one line pointing at the structure.
            if (isError)
            {
                JObject block = ((JArray)copy["content"]).OfType<JObject>()
                    .FirstOrDefault(b => (string)b["type"] == "text" && b["text"]?.Type == JTokenType.String);
                if (block != null)
                    block["text"] = (string)block["text"] + Environment.NewLine + Environment.NewLine +
                                    ErrorLine(help, violations, structured != null);
            }
            return copy;
        }

        /// <summary>The one line an error gains. It names the URI of the schema that holds
        /// the FIRST violation: a variant's when the violation lies inside that variant's
        /// instance, the whole tool's otherwise - a missing top-level argument is not in any
        /// variant's schema, and pointing there sent the reader to a page without it.</summary>
        internal static string ErrorLine(JObject help, IList<string> violations, bool structured)
        {
            string first = violations[0];
            int colon = first.IndexOf(": ", StringComparison.Ordinal);
            string pointer = colon > 0 ? first.Substring(0, colon) : "/";
            string uri = VariantUriHolding(help, pointer) ?? (string)help["contract_uri"];
            if (structured)
                return "Arguments violate the contract at " + pointer +
                       "; exact schema in structuredContent.schema_help (also " + uri + ").";
            string message = first.Length <= MaxViolationChars ? first : first.Substring(0, MaxViolationChars) + "...";
            message = message.Replace("\r", " ").Replace("\n", " ");
            string example = help["minimal_valid_call"] is JObject call
                ? " Minimal valid example: " + (string)call["tool"] + " " + call["arguments"].ToString(Formatting.None) + "."
                : "";
            return "Arguments violate the contract at " + message +
                   (violations.Count > 1 ? " (+" + (violations.Count - 1) + " more)" : "") +
                   ". Exact schema: " + uri + "." + example;
        }

        private static string VariantUriHolding(JObject help, string pointer)
        {
            string p = pointer == "/" ? "" : pointer;
            foreach (JObject v in (help["variants"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string vp = (string)v["pointer"], uri = (string)v["uri"];
                if (vp == null || uri == null) continue;
                if (vp.Length == 0 || p == vp || p.StartsWith(vp + "/", StringComparison.Ordinal)) return uri;
            }
            return null;
        }

        private static bool IsPositive(JToken t)
            => t != null && ((t.Type == JTokenType.Integer && (long)t > 0) ||
                             (t.Type == JTokenType.Float && (double)t > 0));

        private static JObject Build(string tool, JToken args, List<string> violations)
        {
            var help = new JObject
            {
                ["contract_uri"] = McpResources.ContractToolPrefix + tool,
                ["violations"] = Violations(violations, MaxViolations)
            };

            var variants = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int inline = 0, inlineBytes = 0;
            foreach (VariantSite site in ContractVariants.For(tool))
                foreach (KeyValuePair<string, JToken> at in ContractVariants.InstancesAt(args, site.Segments))
                {
                    if (variants.Count >= MaxVariantEntries) break;
                    if (!(at.Value is JObject o) || !o.TryGetValue(site.Discriminator, out JToken d)) continue;
                    if (!seen.Add(site.Pointer + "\u0000" + d.ToString(Formatting.None))) continue;
                    if (d.Type == JTokenType.String && site.BranchIndex.ContainsKey((string)d))
                    {
                        string value = (string)d;
                        var entry = new JObject
                        {
                            ["discriminator"] = site.Discriminator,
                            ["value"] = value,
                            ["uri"] = site.Uri(value),
                            ["pointer"] = at.Key
                        };
                        JObject branch = site.Branch(value);
                        int bytes = Encoding.UTF8.GetByteCount(branch.ToString(Formatting.None));
                        // Beyond three kinds, or 12 KB of branches, the uri is the answer:
                        // one reply must not become a second copy of the contract.
                        if (inline < MaxInlineSchemas && inlineBytes + bytes <= MaxInlineSchemaBytes)
                        {
                            entry["schema"] = branch.DeepClone();
                            inline++;
                            inlineBytes += bytes;
                        }
                        variants.Add(entry);
                    }
                    else
                        variants.Add(new JObject
                        {
                            ["discriminator"] = site.Discriminator,
                            ["value"] = d.DeepClone(),
                            ["pointer"] = at.Key,
                            ["valid_values"] = new JArray(site.Values)
                        });
                }
            if (variants.Count > 0) help["variants"] = variants;
            JObject minimal = ContractExample(tool);
            if (minimal != null) help["minimal_valid_call"] = new JObject
            {
                ["tool"] = tool,
                ["arguments"] = minimal
            };
            string telemetryViolation = MostSpecificViolation(violations);
            help["failure_path"] = FailurePath(telemetryViolation);
            help["failure_rule"] = FailureRule(telemetryViolation);
            help["schema_rejection"] = new JObject
            {
                ["path"] = FailurePath(telemetryViolation),
                ["rule"] = FailureRule(telemetryViolation)
            };

            // The whole block is bounded too. Shed in order of least use: inline schemas
            // (their uri stays), then violations beyond the first, then the variant list.
            int keep = Math.Min(MaxViolations, violations.Count);
            while (Size(help) > MaxBytes)
            {
                JObject withSchema = (help["variants"] as JArray)?.OfType<JObject>().LastOrDefault(v => v["schema"] != null);
                if (withSchema != null) { withSchema.Remove("schema"); continue; }
                if (keep > 1) { keep--; help["violations"] = Violations(violations, keep); continue; }
                if (help["variants"] != null) { help.Remove("variants"); continue; }
                break;
            }
            return help;
        }

        internal static JObject ContractExample(string tool)
        {
            JToken schema = Contract.Find(tool)?.InputSchema;
            if (schema == null) return null;
            JObject candidate = BuildObject(schema);
            if (IsValid(candidate, schema)) return candidate;
            if (schema["oneOf"] is JArray one)
                foreach (JToken branch in one)
                {
                    JObject alternative = BuildObject(schema);
                    JObject branchValue = BuildObject(branch);
                    if (alternative == null || branchValue == null) continue;
                    foreach (JProperty property in branchValue.Properties()) alternative[property.Name] = property.Value.DeepClone();
                    if (IsValid(alternative, schema)) return alternative;
                }
            return null;
        }

        private static bool IsValid(JObject value, JToken schema)
            => value != null && ContractSchemaCheck.Validate(value, schema).Count == 0;

        private static JObject BuildObject(JToken schemaToken)
        {
            var schema = schemaToken as JObject;
            if (schema == null) return null;
            var result = new JObject();
            JObject props = schema["properties"] as JObject;
            if (props != null && schema["required"] is JArray required)
                foreach (JToken name in required)
                {
                    string key = (string)name;
                    if (props[key] == null) continue;
                    JToken value = MinimalValue(props[key]);
                    if (value == null) return null;
                    result[key] = value;
                }
            foreach (JToken branch in (schema["allOf"] as JArray) ?? new JArray())
                AddConditionalRequirements(branch, result, props);
            AddConditionalRequirements(schema, result, props);
            return result;
        }

        private static void AddConditionalRequirements(JToken token, JObject result, JObject rootProperties)
        {
            var condition = token as JObject;
            if (condition == null || !(condition["if"] is JObject test) || !(condition["then"] is JObject then)) return;
            if (ContractSchemaCheck.Validate(result, test).Count != 0 || !(then["required"] is JArray required)) return;
            JObject local = then["properties"] as JObject ?? rootProperties;
            if (local == null) return;
            foreach (JToken name in required)
            {
                string key = (string)name;
                if (result[key] != null || local[key] == null) continue;
                JToken value = MinimalValue(local[key]);
                if (value != null) result[key] = value;
            }
        }

        private static JToken MinimalValue(JToken token)
        {
            var schema = token as JObject;
            if (schema == null) return null;
            if (schema["const"] != null) return schema["const"].DeepClone();
            if (schema["enum"] is JArray choices && choices.Count > 0) return choices[0].DeepClone();
            foreach (string keyword in new[] { "oneOf", "anyOf" })
                if (schema[keyword] is JArray branches)
                    foreach (JToken branch in branches)
                    {
                        JToken value = MinimalValue(branch);
                        if (value != null) return value;
                    }
            string type = (string)schema["type"];
            if (type == "object") return BuildObject(schema);
            if (type == "array")
            {
                var arr = new JArray();
                for (int i = 0, count = (int?)schema["minItems"] ?? 0; i < count; i++)
                {
                    JToken item = MinimalValue(schema["items"]);
                    if (item == null) return null;
                    arr.Add(item);
                }
                return arr;
            }
            if (type == "string") return new JValue(new string('x', (int?)schema["minLength"] ?? 1));
            if (type == "integer" || type == "number")
                return new JValue((double?)schema["minimum"] ?? (double?)schema["exclusiveMinimum"] ?? 0d);
            if (type == "boolean") return new JValue(false);
            return null;
        }

        private static string FailurePath(string violation)
        {
            if (string.IsNullOrEmpty(violation)) return "/";
            int colon = violation.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 ? violation.Substring(0, colon) : "/";
        }

        private static string MostSpecificViolation(List<string> violations)
        {
            return violations.OrderByDescending(v =>
            {
                string path = FailurePath(v);
                return path == "/" ? 0 : path.Count(c => c == '/');
            }).FirstOrDefault() ?? "/: invalid";
        }

        private static string FailureRule(string violation)
        {
            if (violation.IndexOf("is required", StringComparison.Ordinal) >= 0) return "required";
            if (violation.IndexOf("must be", StringComparison.Ordinal) >= 0) return "type_or_value";
            if (violation.IndexOf("does not match", StringComparison.Ordinal) >= 0) return "pattern";
            if (violation.IndexOf("additional", StringComparison.OrdinalIgnoreCase) >= 0 ||
                violation.IndexOf("not allowed", StringComparison.OrdinalIgnoreCase) >= 0) return "additional_property";
            if (violation.IndexOf("minItems", StringComparison.Ordinal) >= 0) return "min_items";
            if (violation.IndexOf("maxItems", StringComparison.Ordinal) >= 0) return "max_items";
            return "constraint";
        }

        private static JArray Violations(List<string> all, int keep)
        {
            var arr = new JArray();
            foreach (string v in all.Take(keep))
                arr.Add(v.Length <= MaxViolationChars ? v : v.Substring(0, MaxViolationChars) + "...");
            if (all.Count > keep) arr.Add("... " + (all.Count - keep) + " more");
            return arr;
        }

        private static int Size(JToken t) => Encoding.UTF8.GetByteCount(t.ToString(Formatting.None));
    }
}
