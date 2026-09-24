// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_model_diff: explain, record_quality, quality_trend.
//
//   explain         counts the active model by category, inferred discipline and
//                   level, reads links, worksets and phases, and the last quality
//                   run recorded for the project, then narrates only those facts
//                   (Core/ModelExplainRules). The ISO 19650 gaps are added by the
//                   SERVER from project-context.json when project_context_path is
//                   given - the same validator horizun_project_context runs.
//   record_quality  runs horizun_model_scan or horizun_audit_model IN PROCESS
//                   (both read-only), flattens the totals they reported and
//                   appends one line to quality-history\<project>.jsonl, then
//                   re-reads the file to prove the line is there.
//   quality_trend   the series, wide, as rows for horizun_power_bi_push + a CSV.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ModelDiffCommand
    {
        private CommandResult Explain(UIApplication app, JObject request)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is active. Nothing was explained.");
            CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
            if (wrong != null) return wrong;

            var byCategory = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var byDiscipline = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var byLevel = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var levelNames = new Dictionary<long, string>();
            long total = 0, unreadable = 0;
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                try
                {
                    Category cat = e.Category;
                    if (cat == null || cat.CategoryType != CategoryType.Model || e.ViewSpecific) continue;
                    total++;
                    Bump(byCategory, cat.Name);
                    Bump(byDiscipline, ModelDiffRules.DisciplineOf(BicName(cat)));
                    Bump(byLevel, NameOf(doc, e.LevelId, levelNames) ?? "(no level)");
                }
                catch { unreadable++; }
            }

            var facts = new JObject
            {
                ["title"] = Safe(() => doc.Title),
                ["document"] = DocumentFacts(doc, app.Application.VersionNumber, "active_document"),
                ["model_elements"] = total,
                ["unreadable_elements"] = unreadable,
                ["by_discipline"] = ToJson(byDiscipline),
                ["by_category"] = ToJson(byCategory),
                ["by_level"] = ToJson(byLevel),
                ["links"] = Links(doc),
                ["worksets"] = Worksets(doc),
                ["phases"] = new JArray(doc.Phases.Cast<Phase>().Select(p => Safe(() => p.Name)))
            };
            string project = request.Value<string>("project") ?? Safe(() => doc.Title);
            facts["last_quality"] = ModelExplainRules.LastQuality(QualityHistory.Read(QualityHistory.PathFor(HorizunPaths.DataRoot(), project)));
            facts["narrative"] = ModelExplainRules.Narrate(facts);
            facts["iso19650"] = string.IsNullOrWhiteSpace(request.Value<string>("project_context_path"))
                ? new JObject { ["status"] = "not_requested", ["why"] = "pass project_context_path to have the gaps evaluated." }
                : new JObject { ["status"] = "evaluated_by_server" };
            facts["operation"] = "explain";
            facts["discipline_note"] = "discipline is inferred from the Revit category; Revit declares none per element.";
            return CommandResult.Ok(facts);
        }

        private static JObject Links(Document doc)
        {
            long total = 0, loaded = 0, instances = 0;
            foreach (RevitLinkType t in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                total++;
                try { if (RevitLinkType.IsLoaded(doc, t.Id)) loaded++; } catch { }
            }
            try { instances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount(); } catch { }
            return new JObject { ["total"] = total, ["loaded"] = loaded, ["instances"] = instances };
        }

        private static JObject Worksets(Document doc)
        {
            bool shared;
            try { shared = doc.IsWorkshared; } catch { return new JObject { ["workshared"] = JValue.CreateNull() }; }
            if (!shared) return new JObject { ["workshared"] = false };
            var list = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets();
            return new JObject
            {
                ["workshared"] = true, ["user"] = list.Count, ["open"] = list.Count(w => w.IsOpen),
                ["closed"] = new JArray(list.Where(w => !w.IsOpen).Select(w => w.Name))
            };
        }

        // ---- quality history -------------------------------------------------------------

        private CommandResult RecordQuality(UIApplication app, JObject request)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is active. Nothing was recorded.");
            CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
            if (wrong != null) return wrong;
            string source = request.Value<string>("source") ?? "model_scan";
            string tool = source == "audit_model" ? "horizun_audit_model" : source == "model_scan" ? "horizun_model_scan" : null;
            if (tool == null) return CommandResult.Fail("source must be model_scan or audit_model. Nothing was recorded.");
            ICommand child = _resolve?.Invoke(tool);
            if (child == null) return CommandResult.Fail(tool + " is not registered in this add-in. Nothing was recorded.");

            string title = Safe(() => doc.Title);
            var args = tool == "horizun_model_scan"
                ? new JObject { ["target_document_title"] = title, ["response_mode"] = "summary" }
                : new JObject { ["target_document"] = title };
            CommandResult run = child.Execute(app, args.ToString());
            if (!run.Success)
                return CommandResult.Fail(tool + " did not complete, so nothing was recorded: " + run.Error);
            JObject data = run.Data as JObject ?? JObject.FromObject(run.Data);

            bool complete; JArray failed;
            JObject metrics = tool == "horizun_model_scan"
                ? QualityHistory.SummarizeScan(data, out complete, out failed)
                : QualityHistory.SummarizeAudit(data, out complete, out failed);
            JObject docFacts = DocumentFacts(doc, app.Application.VersionNumber, "active_document");
            docFacts.Remove("path");
            string project = request.Value<string>("project") ?? title;
            JObject record = QualityHistory.Record(DateTime.UtcNow, project, docFacts, source, complete, failed, metrics);

            string path = QualityHistory.PathFor(HorizunPaths.DataRoot(), project);
            int beforeCount = QualityHistory.Read(path).Records.Count;
            QualityHistory.Append(path, record);
            QualityReadResult reread = QualityHistory.Read(path);
            bool appended = reread.Records.Count == beforeCount + 1 &&
                            JToken.DeepEquals(reread.Records.Last(), record);
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "record_quality", ["project"] = record["project"], ["path"] = path,
                ["appended_verified"] = appended, ["records_in_history"] = reread.Records.Count,
                ["malformed_lines"] = reread.MalformedLines, ["record"] = record,
                ["note"] = complete ? null : "This run did NOT see the whole model; its counts are lower bounds and a trend must say so."
            });
        }

        private CommandResult QualityTrend(UIApplication app, JObject request)
        {
            string project = request.Value<string>("project") ?? Safe(() => app.ActiveUIDocument?.Document?.Title);
            if (string.IsNullOrWhiteSpace(project))
                return CommandResult.Fail("quality_trend needs 'project' (or an active document whose title names it).");
            string path = QualityHistory.PathFor(HorizunPaths.DataRoot(), project);
            QualityReadResult history = QualityHistory.Read(path);
            var filter = (request["metrics"] as JArray)?.Select(x => (string)x).ToList();
            List<string> columns;
            List<JObject> rows = QualityHistory.Rows(history.Records, filter, out columns);
            int limit = Math.Max(1, Math.Min(request.Value<int?>("limit") ?? 500, 5000));
            if (rows.Count > limit) rows = rows.Skip(rows.Count - limit).ToList();
            string csvPath = Path.ChangeExtension(path, ".trend.csv");
            if (history.FileExists)
                File.WriteAllText(csvPath, QualityHistory.Csv(rows, columns), new System.Text.UTF8Encoding(true));
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "quality_trend", ["project"] = QualityHistory.ProjectKey(project), ["path"] = path,
                ["history_exists"] = history.FileExists, ["records"] = history.Records.Count,
                ["malformed_lines"] = history.MalformedLines, ["returned"] = rows.Count,
                ["columns"] = new JArray(columns), ["rows"] = new JArray(rows),
                ["csv"] = history.FileExists ? csvPath : null,
                ["power_bi"] = "pass 'rows' as horizun_power_bi_push rows; incomplete runs carry complete=false."
            });
        }

        private static void Bump(SortedDictionary<string, long> d, string key)
        {
            key = key ?? "(none)";
            long n; d.TryGetValue(key, out n); d[key] = n + 1;
        }

        private static JObject ToJson(SortedDictionary<string, long> d)
        {
            var o = new JObject();
            foreach (var kv in d) o[kv.Key] = kv.Value;
            return o;
        }
    }
}
