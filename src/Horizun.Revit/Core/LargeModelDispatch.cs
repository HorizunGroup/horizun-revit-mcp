using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // A conservative census, before the requested command. Unreadable sources are
    // deliberately above the limit; a missing count must never mean a small scope.
    internal static class LargeModelDispatch
    {
        internal static CommandResult Execute(UIApplication app, ICommand command, string json)
        {
            if (!LargeModelToolGate.Applies(command.Name)) return command.Execute(app, json);
            JObject args;
            try { args = string.IsNullOrWhiteSpace(json) ? new JObject() : JsonText.ParseObject(json); }
            catch { return command.Execute(app, json); }
            var confirm = args["confirm_large_operation"];
            if (confirm != null && confirm.Type != JTokenType.Boolean)
                return CommandResult.Fail("confirm_large_operation must be a boolean. Nothing ran.");
            if (confirm != null && (bool)confirm) return command.Execute(app, json);
            Document host = app?.ActiveUIDocument?.Document;
            if (host == null) return command.Execute(app, json);
            string wanted = ReadDocumentRules.Target(args);
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var refusal = DocumentGate.ReadGuard(host, args, command.Name);
                if (refusal != null) return refusal;
            }

            // Summaries contain no element rows and remain the inexpensive first inquiry.
            if (command.Name == "horizun_query_model" && (string)args["response_mode"] == "summary")
                return command.Execute(app, json);

            var docs = new List<Document> { host };
            bool complete = true;
            bool federated = command.Name == "horizun_query_model" || command.Name == "horizun_list_elements";
            bool includeLinks = federated ? args["include_links"]?.Type != JTokenType.Boolean || (bool)args["include_links"]
                : command.Name == "horizun_quantities" && args["include_links"]?.Type == JTokenType.Boolean && (bool)args["include_links"];
            if (includeLinks)
            {
                try
                {
                    foreach (RevitLinkInstance link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)))
                    {
                        try { var linked = link.GetLinkDocument(); if (linked == null) complete = false; else docs.Add(linked); }
                        catch { complete = false; }
                    }
                }
                catch { complete = false; }
            }
            var categories = Categories(command.Name, args);
            var ids = args["element_ids"] as JArray;
            bool byIds = (command.Name == "horizun_query_model" || command.Name == "horizun_quantities") && ids != null && ids.Count > 0;
            long totalModel = 0;
            var counts = new Dictionary<string, long>();
            for (int i = 0; i < docs.Count; i++)
            {
                try
                {
                    int all = Count(docs[i], null);
                    totalModel += new FilteredElementCollector(docs[i]).WhereElementIsNotElementType().GetElementCount();
                    long scope;
                    if (byIds) scope = ids.Count;
                    else if (categories != null)
                    {
                        scope = 0;
                        foreach (BuiltInCategory category in categories)
                            scope += Count(docs[i], category);
                    }
                    else scope = all;
                    counts[i == 0 ? "host" : "link_" + i] = scope;
                }
                catch { complete = false; }
            }
            var measuredCounts = new Dictionary<string, long>(counts);
            if (!complete) counts["unknown_scope_guard"] = LargeModelToolGate.ScopeThreshold + 1;
            int? modelSize = complete ? (int?)Math.Min(int.MaxValue, totalModel) : null;
            var verdict = LargeModelToolGate.Run(command.Name, modelSize, counts, false, () => command.Execute(app, json));
            if (verdict.Executed) return verdict.Value;
            var detail = new JObject
            {
                ["code"] = "large_model_confirmation_required",
                ["operation_started"] = false,
                ["estimate"] = new JObject {
                    ["large_model"] = verdict.Estimate.LargeModel.HasValue ? (JToken)new JValue(verdict.Estimate.LargeModel.Value) : JValue.CreateNull(),
                    ["scope_upper_bound"] = complete ? (JToken)new JValue(verdict.Estimate.ScopedElements) : JValue.CreateNull(),
                    ["counts_by_source"] = JObject.FromObject(measuredCounts),
                    ["coverage_complete"] = complete,
                    ["scope_threshold"] = LargeModelToolGate.ScopeThreshold,
                    ["estimated_seconds"] = JValue.CreateNull()
                },
                ["scope_proposal"] = "Narrow categories or element_ids where supported, or exclude links if appropriate; otherwise explicitly set confirm_large_operation=true.",
                ["suggested_call"] = LargeModelToolGate.SuggestedCall(command.Name, args),
                ["confirmation_argument"] = "confirm_large_operation"
            };
            if (command.Name == "horizun_query_model") detail["error_code"] = ErrorCodes.ConfirmacionRequerida;
            return CommandResult.FailWithDetail("Large or unknown model scope requires confirm_large_operation=true. Nothing ran.", detail);
        }

        private static int Count(Document doc, BuiltInCategory? category)
        {
            var instances = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            var types = new FilteredElementCollector(doc).WhereElementIsElementType();
            if (category.HasValue) { instances.OfCategory(category.Value); types.OfCategory(category.Value); }
            return checked(instances.GetElementCount() + types.GetElementCount());
        }

        internal static HashSet<BuiltInCategory> Categories(string tool, JObject args)
        {
            var tokens = new List<JToken>();
            if (tool == "horizun_list_elements" || tool == "horizun_quantities")
            {
                if (args["category"] != null) tokens.Add(args["category"]);
                if (args["categories"] is JArray array) tokens.AddRange(array);
            }
            else if (tool == "horizun_query_model") { if (args["categories"] is JArray array) tokens.AddRange(array); }
            if (tokens.Count == 0) return null;
            var result = new HashSet<BuiltInCategory>();
            foreach (JToken token in tokens)
            {
                BuiltInCategory category;
                if (token.Type != JTokenType.String || !Enum.TryParse((string)token, true, out category) || !Enum.IsDefined(typeof(BuiltInCategory), category)) return null;
                result.Add(category);
            }
            return result;
        }
    }
}
