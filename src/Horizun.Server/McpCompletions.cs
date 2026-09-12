// -----------------------------------------------------------------------------
// Horizun MCP server — bounded prompt argument completions.
//
// Completion is deliberately limited to public, static vocabulary. It never walks
// models, files or settings: an interactive completion request must not become a
// side channel for project names or local paths, nor touch Revit's UI thread.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpCompletions
    {
        private const int MaxValues = 100;

        private static readonly string[] AuditFocus =
        {
            "whole model", "health and performance", "warnings and hygiene",
            "links and worksets", "quantities", "naming and standards",
            "documentation", "coordination and clashes"
        };

        public static JObject Complete(JObject prms)
        {
            JObject reference = RequiredObject(prms, "ref");
            JObject argument = RequiredObject(prms, "argument");
            string type = RequiredString(reference, "type", "ref");
            string argumentName = RequiredString(argument, "name", "argument");
            string value = RequiredString(argument, "value", "argument", allowEmpty: true);

            JToken context = prms?["context"];
            if (context != null && context.Type != JTokenType.Null && context.Type != JTokenType.Object)
                throw new McpError(-32602, "Invalid params: completion context must be an object.");
            JToken contextArguments = context?["arguments"];
            if (contextArguments != null && contextArguments.Type != JTokenType.Null &&
                contextArguments.Type != JTokenType.Object)
                throw new McpError(-32602,
                    "Invalid params: completion context.arguments must be an object of strings.");
            if (contextArguments is JObject values &&
                values.Properties().Any(p => p.Value.Type != JTokenType.String))
                throw new McpError(-32602,
                    "Invalid params: every completion context.arguments value must be a string.");

            IEnumerable<string> candidates;
            if (type == "ref/prompt")
            {
                string prompt = RequiredString(reference, "name", "ref");
                switch (prompt)
                {
                    case "read-only-audit" when argumentName == "focus":
                        candidates = AuditFocus;
                        break;
                    case "model-health-audit" when argumentName == "scope":
                    case "sheet-qaqc" when argumentName == "scope":
                    case "family-qaqc" when argumentName == "scope":
                    case "room-area-audit" when argumentName == "scope":
                    case "parameter-compliance" when argumentName == "standard":
                    case "quantity-export-pack" when argumentName == "scope":
                    case "dwg-to-bim-review" when argumentName == "requirement_set":
                    case "safe-batch-parameter-update" when argumentName == "updates":
                    case "architecture-structure-coordination" when argumentName == "scope":
                    case "mep-coordination-review" when argumentName == "scope":
                    case "western-forms-concrete-review" when argumentName == "requirement_set":
                    case "qaqc-report-export" when argumentName == "workbook_path":
                    case "qaqc-report-export" when argumentName == "evidence_source":
                        // These describe project-specific content. Completion must not
                        // guess a standard, target set or destination from public text.
                        candidates = Array.Empty<string>();
                        break;
                    case "health-first":
                        throw new McpError(-32602, "Prompt 'health-first' has no arguments to complete.");
                    case "verified-change" when argumentName == "objective" ||
                                                        argumentName == "applies_to" ||
                                                        argumentName == "correct_when":
                        // These fields describe the user's actual intent. Suggesting a
                        // canned answer would invite the model to invent that intent.
                        candidates = Array.Empty<string>();
                        break;
                    case "read-only-audit":
                    case "verified-change":
                    case "model-health-audit":
                    case "sheet-qaqc":
                    case "family-qaqc":
                    case "parameter-compliance":
                    case "room-area-audit":
                    case "quantity-export-pack":
                    case "dwg-to-bim-review":
                    case "safe-batch-parameter-update":
                    case "architecture-structure-coordination":
                    case "mep-coordination-review":
                    case "western-forms-concrete-review":
                    case "qaqc-report-export":
                        throw new McpError(-32602,
                            "Prompt '" + prompt + "' has no argument named '" + argumentName + "'.");
                    case "planimetry-review" when argumentName == "scope":
                        candidates = Array.Empty<string>();
                        break;
                    case "planimetry-review":
                        throw new McpError(-32602,
                            "Prompt 'planimetry-review' has no argument named '" + argumentName + "'.");
                    default:
                        throw new McpError(-32602, "Unknown Horizun prompt: '" + prompt + "'.");
                }
            }
            else if (type == "ref/resource")
            {
                RequiredString(reference, "uri", "ref");
                throw new McpError(-32602,
                    "Horizun exposes no parameterised resource templates, so ref/resource completion is unavailable.");
            }
            else
                throw new McpError(-32602,
                    "Invalid params: completion ref.type must be 'ref/prompt' or 'ref/resource'.");

            string prefix = value ?? "";
            List<string> matches = candidates
                .Where(c => c.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool more = matches.Count > MaxValues;
            return new JObject
            {
                ["completion"] = new JObject
                {
                    ["values"] = new JArray(matches.Take(MaxValues)),
                    ["total"] = matches.Count,
                    ["hasMore"] = more
                }
            };
        }

        private static JObject RequiredObject(JObject parent, string name)
        {
            JToken value = parent?[name];
            if (value == null || value.Type != JTokenType.Object)
                throw new McpError(-32602, "Invalid params: completion requires object '" + name + "'.");
            return (JObject)value;
        }

        private static string RequiredString(
            JObject parent, string name, string owner, bool allowEmpty = false)
        {
            JToken token = parent?[name];
            if (token == null || token.Type != JTokenType.String ||
                (!allowEmpty && string.IsNullOrWhiteSpace((string)token)))
                throw new McpError(-32602,
                    "Invalid params: completion " + owner + "." + name + " must be " +
                    (allowEmpty ? "a string" : "a non-empty string") + ".");
            return (string)token;
        }
    }
}
