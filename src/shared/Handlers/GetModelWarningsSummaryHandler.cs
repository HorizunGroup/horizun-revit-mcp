using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class GetModelWarningsSummaryHandler : IRevitCommand
    {
        public string Name => "get_model_warnings_summary";
        public string Description => "Return a grouped summary of doc.GetWarnings(): per warning type, count + optional example failing element ids.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""include_examples"":{""type"":""boolean"",""default"":true},""max_examples_per_type"":{""type"":""integer"",""default"":5}}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            var req = JObject.Parse(paramsJson ?? "{}");
            var includeExamples = req.Value<bool?>("include_examples") ?? true;
            var maxExamples = req.Value<int?>("max_examples_per_type") ?? 5;

            var warnings = doc.GetWarnings();
            var grouped = warnings
                .GroupBy(w => w.GetDescriptionText())
                .OrderByDescending(g => g.Count())
                .Select(g => new
                {
                    description = g.Key,
                    count = g.Count(),
                    severity = g.First().GetSeverity().ToString(),
                    examples = includeExamples
                        ? g.Take(maxExamples).Select(w => new
                        {
                            failing_element_ids = w.GetFailingElements().Select(RevitCompat.GetId).ToList()
                        }).ToList<object>()
                        : null
                })
                .ToList();

            return CommandResult.Ok(new
            {
                total_warnings = warnings.Count,
                unique_descriptions = grouped.Count,
                warnings = grouped
            });
        }
    }
}
