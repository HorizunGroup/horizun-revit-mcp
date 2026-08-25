// -----------------------------------------------------------------------------
// Horizun MCP server — standard MCP Prompts.
//
// Prompts contain product operating policy, never an organisation's standards.
// Company catalogues and delivery rules remain arguments/resources supplied by the
// caller, preserving the bridge's organisation-neutral contract.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpPrompts
    {
        public static JObject List(JObject prms)
        {
            RejectCursor(prms);
            return new JObject
            {
                ["prompts"] = new JArray
                {
                    Prompt("health-first", "Start safely in Revit",
                        "Identify the reachable Revit and active document before any operation."),
                    Prompt("verified-change", "Plan a verified model change",
                        "Turn an objective, target set and acceptance criterion into a dry-run-first typed operation.",
                        Arg("objective", "What outcome is required in the model.", true),
                        Arg("applies_to", "Which elements/documents the change applies to.", true),
                        Arg("correct_when", "How the result will be recognised as correct.", true)),
                    Prompt("read-only-audit", "Audit the active model without writes",
                        "Run a coverage-honest audit of the active model and report uncertainty explicitly.",
                        Arg("focus", "Optional audit focus such as health, links, quantities or naming.", false)),
                    Prompt("planimetry-review", "Review planimetry directly from Revit",
                        "Audit sheets and documentation from the model, capture the actual sheets and judge visual quality without exporting a PDF.",
                        Arg("scope", "Optional sheet numbers, sheet ids or discipline; omit for every non-placeholder sheet.", false))
                }
            };
        }

        public static JObject Get(JObject prms)
        {
            string name = RequiredString(prms, "name");
            JObject args = prms?["arguments"] as JObject ?? new JObject();
            if (prms?["arguments"] != null && prms["arguments"].Type != JTokenType.Object)
                throw new McpError(-32602, "Invalid params: prompts/get 'arguments' must be an object of strings.");

            string description;
            string body;
            switch (name)
            {
                case "health-first":
                    description = "Start every Revit task against a measured target.";
                    body =
                        "Call horizun_health first. Confirm the reachable Revit year, process and active document. " +
                        "If more than one Revit is reachable, use horizun_target instead of guessing. Do not write " +
                        "until the objective, target elements and acceptance evidence are unambiguous.";
                    break;
                case "verified-change":
                    string objective = Argument(args, "objective", true);
                    string applies = Argument(args, "applies_to", true);
                    string correct = Argument(args, "correct_when", true);
                    description = "Prepare a typed, dry-run-first and postcondition-verified model change.";
                    body =
                        "Objective: " + objective + "\nApplies to: " + applies + "\nCorrect when: " + correct +
                        "\n\nCall horizun_health first. Choose the narrowest typed Horizun tool that covers the " +
                        "whole objective. Run its default dry-run, inspect the resolved plan and warnings, then use " +
                        "the returned single-use confirmation token without changing the request. After execution, " +
                        "require measured postconditions. Do not retry an uncertain/partial write. Use Python only " +
                        "when fallback.allowed=true and the machine owner has temporarily enabled it in Revit.";
                    break;
                case "read-only-audit":
                    string focus = Argument(args, "focus", false);
                    description = "Audit without changing the model or hiding incomplete coverage.";
                    body =
                        "Call horizun_health, then use read-only typed tools such as horizun_model_scan, " +
                        "horizun_audit_model, horizun_quantities and horizun_clash. " +
                        (string.IsNullOrWhiteSpace(focus) ? "Cover the whole model." : "Focus: " + focus + ".") +
                        " Report closed worksets, unloaded links, truncation and unreadable sections as uncertainty; " +
                        "never interpret absence under incomplete coverage as proof that a problem does not exist.";
                    break;
                case "planimetry-review":
                    string scope = Argument(args, "scope", false);
                    description = "Audit and visually review Revit planimetry without a PDF intermediary.";
                    body =
                        "Review planimetry DIRECTLY FROM THE ACTIVE REVIT MODEL; do not export or inspect a PDF. " +
                        "Call horizun_health first. Use horizun_query_planimetry mode=inventory, then sheets, " +
                        "placements and annotations with complete pagination for " +
                        (string.IsNullOrWhiteSpace(scope) ? "every non-placeholder sheet" : "this scope: " + scope) +
                        ". Run horizun_audit_planimetry with the approved inline requirement_set when one exists. " +
                        "For every sheet in scope call horizun_capture_view by sheet view_id and actually inspect " +
                        "the attached PNG. Judge hierarchy, density, alignment, balance, clipping, whitespace, " +
                        "legibility, collisions, orphan marks, missing tags/dimensions and consistency across the " +
                        "set. Cross-reference every visual suspicion with model rows; label subjective findings " +
                        "visual and database findings measured. A failed capture, truncated page or unreadable fact " +
                        "is UNKNOWN, never clean. Return findings by sheet with severity, evidence and element/view " +
                        "ids. Use the narrowest typed correction only after approval and its dry run.";
                    break;
                default: throw new McpError(-32602, "Unknown Horizun prompt: '" + name + "'.");
            }

            return new JObject
            {
                ["description"] = description,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JObject { ["type"] = "text", ["text"] = body }
                    }
                }
            };
        }

        private static JObject Prompt(string name, string title, string description, params JObject[] args)
        {
            var result = new JObject { ["name"] = name, ["title"] = title, ["description"] = description };
            if (args != null && args.Length > 0) result["arguments"] = new JArray(args);
            return result;
        }

        private static JObject Arg(string name, string description, bool required) => new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["required"] = required
        };

        private static string RequiredString(JObject o, string key)
        {
            JToken t = o?[key];
            if (t == null || t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))
                throw new McpError(-32602, "Invalid params: prompts/get requires a non-empty string '" + key + "'.");
            return (string)t;
        }

        private static string Argument(JObject args, string name, bool required)
        {
            JToken t = args?[name];
            if (t == null)
            {
                if (required) throw new McpError(-32602, "Missing required prompt argument '" + name + "'.");
                return null;
            }
            if (t.Type != JTokenType.String)
                throw new McpError(-32602, "Prompt argument '" + name + "' must be a string.");
            string value = (string)t;
            if (required && string.IsNullOrWhiteSpace(value))
                throw new McpError(-32602, "Prompt argument '" + name + "' cannot be empty.");
            return value;
        }

        private static void RejectCursor(JObject prms)
        {
            JToken cursor = prms?["cursor"];
            if (cursor != null && cursor.Type != JTokenType.Null)
                throw new McpError(-32602,
                    "prompts/list has one bounded page and does not accept a cursor. Omit params.cursor.");
        }
    }
}
