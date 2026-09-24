// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_link_schedule (4D). Original Horizun code.
//
// import and match READ; write and status_view WRITE, dry run first, with a token
// bound to the schedule file's SHA-256 and the match spec, and re-read after the
// commit. The schedule file is re-read on every call - nothing is cached between
// the rehearsal and the apply, so an edited file refuses as a stale plan.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class LinkScheduleCommand : ICommand
    {
        public string Name => "horizun_link_schedule";

        public string Description =>
            "4D: import a schedule (MSPDI/CSV/XER), match it to elements, write activity parameters, colour a status view.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string operation = request.Value<string>("operation");
            if (operation != "import" && operation != "match" && operation != "write" && operation != "status_view")
                return CommandResult.Fail("operation must be import, match, write or status_view.");

            string path = request.Value<string>("schedule_path");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
                return CommandResult.Fail("schedule_path must be an absolute path to an existing file: " + path);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { return CommandResult.Fail("schedule_path could not be read: " + ex.Message); }
            string fileSha;
            using (SHA256 sha = SHA256.Create()) fileSha = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

            ScheduleImportResult schedule;
            try { schedule = ScheduleImport.Parse(path, DecodeText(bytes)); }
            catch (ScheduleImportException ex) { return CommandResult.Fail("The schedule was refused: " + ex.Message); }
            int maxRows = Math.Max(1, request.Value<int?>("max_rows") ?? 200);

            var head = new JObject
            {
                ["operation"] = operation,
                ["schedule"] = new JObject
                {
                    ["path"] = path, ["sha256"] = fileSha, ["format"] = schedule.Format,
                    ["activities"] = schedule.Activities.Count, ["summaries_skipped"] = schedule.SummariesSkipped,
                    ["rejected_rows"] = schedule.Rejected.Count,
                    ["rejected"] = new JArray(schedule.Rejected.Take(maxRows)),
                    ["with_progress"] = schedule.Activities.Count(a => a.HasProgress)
                }
            };

            if (operation == "import")
            {
                // Pure file work: no document is needed, and none is read.
                head["activities"] = new JArray(schedule.Activities.Take(maxRows).Select(a => a.ToJson()));
                head["activities_listed"] = Math.Min(maxRows, schedule.Activities.Count);
                return CommandResult.Ok(head);
            }

            JObject spec = request["match"] as JObject;
            string specError = ScheduleLinkRules.ValidateSpec(spec, schedule.Activities);
            if (specError != null) return CommandResult.Fail(specError);

            if (operation == "match")
            {
                Document doc = app?.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("No active Revit document.");
                CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
                if (wrong != null) return wrong;
                ScheduleMatch m = ScheduleLinkRules.Match(spec, schedule.Activities, Facts(doc, spec, null));
                head["document"] = doc.Title;
                MatchJson(head, m, maxRows);
                return CommandResult.Ok(head);
            }
            return operation == "write"
                ? Write(app, request, schedule, spec, fileSha, head, maxRows)
                : StatusView(app, request, schedule, spec, fileSha, head, maxRows);
        }

        private static string DecodeText(byte[] bytes)
        {
            // UTF-8 (with or without BOM) is the default; UTF-16 is what Excel's "Unicode text" writes.
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        }

        private static void MatchJson(JObject o, ScheduleMatch m, int maxRows)
        {
            o["linked_elements"] = m.Links.Count;
            o["links"] = new JArray(m.Links.Take(maxRows).Select(kv => new JObject { ["element_id"] = kv.Key, ["activity"] = kv.Value.Id }));
            o["ambiguous_elements"] = m.Ambiguous.Count;
            o["ambiguous"] = new JArray(m.Ambiguous.Take(maxRows));
            o["elements_without_activity"] = m.WithoutActivity.Count;
            o["elements_without_activity_ids"] = new JArray(m.WithoutActivity.Take(maxRows));
            o["activities_without_elements"] = m.ActivitiesWithoutElements.Count;
            o["activities_without_elements_ids"] = new JArray(m.ActivitiesWithoutElements.Take(maxRows).Select(a => a.Id));
            o["ambiguous_means"] = "an element whose key names two activities, or that two rules assign differently, is linked to NEITHER.";
        }

        /// <summary>
        /// Candidates for matching: every model element carrying the match parameter, or every element in a
        /// rule's category. `restrictTo` limits the read to known ids (the apply re-reads only the plan).
        /// </summary>
        private static List<ScheduleElementFact> Facts(Document doc, JObject spec, ICollection<long> restrictTo)
        {
            var names = new List<string>();
            IEnumerable<Element> source;
            if (spec["parameter"] != null)
            {
                names.Add(spec.Value<string>("parameter"));
                source = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model);
            }
            else
            {
                var rules = ((JArray)spec["rules"]).OfType<JObject>().ToList();
                names.AddRange(rules.Select(r => r.Value<string>("parameter")).Where(p => p != null).Distinct());
                var cats = rules.Select(r => CodeCheckCommand.ResolveCategory(doc, r.Value<string>("category")))
                    .Where(c => c != null).GroupBy(c => Rid.Value(c.Id)).Select(g => g.First());
                source = cats.SelectMany(c => new FilteredElementCollector(doc).OfCategoryId(c.Id).WhereElementIsNotElementType());
            }
            var facts = new List<ScheduleElementFact>();
            foreach (Element e in source)
            {
                long id = Rid.Value(e.Id);
                if (restrictTo != null && !restrictTo.Contains(id)) continue;
                var f = new ScheduleElementFact
                {
                    Id = id,
                    CategoryToken = CodeCheckCommand.CategoryToken(e.Category),
                    CategoryName = CodeCheckCommand.SafeCatName(e.Category),
                    Level = CodeCheckCommand.LevelName(doc, e)
                };
                foreach (string n in names)
                {
                    ParamFact p = CodeCheckCommand.ReadParam(doc, e, n, null);
                    if (p.Exists) f.Params[n] = p.Text ?? "";
                }
                facts.Add(f);
            }
            return facts;
        }

        private static string PlanHash(JObject request, string fileSha, string op)
        {
            var scope = new JObject
            {
                ["op"] = op, ["file"] = fileSha, ["match"] = request["match"], ["write"] = request["write"],
                ["view_id"] = request["view_id"], ["as_of"] = request["as_of"]
            };
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(scope.ToString(Formatting.None)))).Replace("-", "");
        }
    }
}
