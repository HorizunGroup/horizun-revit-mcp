// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_model_diff operation=compare: before vs after, each a stored snapshot
// id or 'active' (read now, with the SAME scope the other side was taken with, so
// a category filter on one side is not reported as mass deletion on the other).
// The comparison is Core/ModelDiffRules.Compare; this file only resolves the two
// sides, pages the rows and writes the CSV/JSON export under the data root.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ModelDiffCommand
    {
        internal const string Active = "active";

        private CommandResult Compare(UIApplication app, JObject request)
        {
            string beforeId = request.Value<string>("before");
            string afterId = request.Value<string>("after") ?? Active;
            if (string.IsNullOrWhiteSpace(beforeId))
                return CommandResult.Fail("compare needs 'before': a snapshot id (operation=list shows them). 'after' " +
                                          "defaults to 'active'. Nothing was compared.");

            DiffSnapshot before, after;
            CommandResult failure = ResolvePair(app, request, beforeId, afterId, out before, out after);
            if (failure != null) return failure;

            DiffOptions options = Options(request);
            DiffResult result = ModelDiffRules.Compare(before, after, options);
            JObject summary = ModelDiffRules.Summary(result);

            string compareId = ModelDiffRules.NewId(DateTime.UtcNow, before.Id + "|" + after.Id);
            Directory.CreateDirectory(ExportDir());
            string csvPath = Path.Combine(ExportDir(), compareId + ".csv");
            string jsonPath = Path.Combine(ExportDir(), compareId + ".json");
            File.WriteAllText(csvPath, ModelDiffRules.Csv(result), new System.Text.UTF8Encoding(true));
            var export = new JObject
            {
                ["schema"] = "horizun.model-diff-comparison/1",
                ["before"] = Side(before), ["after"] = Side(after), ["summary"] = summary,
                ["rows"] = ModelDiffRules.Page(result.Rows, 0, Math.Max(1, result.Rows.Count))["rows"],
                ["type_rows"] = ModelDiffRules.Page(result.TypeRows, 0, Math.Max(1, result.TypeRows.Count))["rows"]
            };
            File.WriteAllText(jsonPath, export.ToString(Formatting.None));

            int offset = request.Value<int?>("offset") ?? 0;
            int limit = Math.Max(1, Math.Min(request.Value<int?>("limit") ?? 100, 1000));
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "compare",
                ["comparison_id"] = compareId,
                ["before"] = Side(before),
                ["after"] = Side(after),
                ["identity"] = "unique_id",
                ["tolerances"] = new JObject
                {
                    ["move_mm"] = Math.Round(options.MoveToleranceFeet * 304.8, 3),
                    ["value_internal_units"] = options.ValueTolerance,
                    ["heuristic_match"] = options.HeuristicMatch,
                    ["match_mm"] = Math.Round(options.MatchToleranceFeet * 304.8, 3)
                },
                ["summary"] = summary,
                ["discipline_note"] = "discipline is inferred from the Revit category; Revit declares none per element.",
                ["detail"] = ModelDiffRules.Page(result.Rows, offset, limit),
                ["type_changes"] = ModelDiffRules.Page(result.TypeRows, 0, Math.Min(limit, 200)),
                ["exports"] = new JObject { ["csv"] = csvPath, ["json"] = jsonPath },
                ["coverage"] = Coverage(before, after)
            });
        }

        internal CommandResult ResolvePair(UIApplication app, JObject request, string beforeId, string afterId,
                                           out DiffSnapshot before, out DiffSnapshot after)
        {
            before = after = null;
            string refusal;
            DiffSnapshot stored = null;
            if (!IsActive(beforeId)) { stored = before = Load(beforeId, out refusal); if (before == null) return CommandResult.Fail(refusal); }
            if (!IsActive(afterId))
            {
                after = Load(afterId, out refusal);
                if (after == null) return CommandResult.Fail(refusal);
                stored = stored ?? after;
            }
            if (IsActive(beforeId) && IsActive(afterId))
                return CommandResult.Fail("before and after cannot both be 'active'. Nothing was compared.");
            if (before == null || after == null)
            {
                Document doc = app.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("'active' was named but no document is active. Nothing was compared.");
                CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
                if (wrong != null) return wrong;
                // The SAME scope as the stored side: otherwise a filter is reported as deletion.
                DiffSnapshot live = ReadDocument(doc, app.Application.VersionNumber, Scope(new JObject(), stored.Scope), "active_document");
                live.Id = Active;
                if (before == null) before = live; else after = live;
            }
            return null;
        }

        private static bool IsActive(string id) => string.Equals(id, Active, StringComparison.OrdinalIgnoreCase);

        internal static DiffOptions Options(JObject request) => new DiffOptions
        {
            MoveToleranceFeet = Math.Max(0, request.Value<double?>("move_tolerance_mm") ?? 1.0) / 304.8,
            HeuristicMatch = request.Value<bool?>("heuristic_match") ?? false,
            MatchToleranceFeet = Math.Max(0, request.Value<double?>("match_tolerance_mm") ?? 50.0) / 304.8
        };

        private static JObject Side(DiffSnapshot s) => new JObject
        {
            ["snapshot_id"] = s.Id, ["taken_utc"] = s.TakenUtc, ["title"] = s.Document["title"],
            ["version_guid"] = s.Document["version_guid"], ["elements"] = s.Elements.Count, ["truncated"] = s.Truncated
        };

        private static JObject Coverage(DiffSnapshot a, DiffSnapshot b)
        {
            bool complete = !a.Truncated && !b.Truncated && a.Unreadable == 0 && b.Unreadable == 0;
            return new JObject
            {
                ["complete"] = complete,
                ["unreadable_before"] = a.Unreadable, ["unreadable_after"] = b.Unreadable,
                ["note"] = complete ? null
                    : "At least one side is truncated or had unreadable elements: an element missing from it reads as " +
                      "added or deleted here without being so. Re-take the snapshot with a larger max_elements or a category filter."
            };
        }
    }
}
