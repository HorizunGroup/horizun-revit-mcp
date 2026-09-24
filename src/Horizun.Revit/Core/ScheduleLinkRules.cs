// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// 4D: Primavera XER, element-to-activity matching and status at a date.
//
// MATCHING NEVER PICKS. An element whose key names two activities (two tasks
// sharing a WBS code) or that two rules assign differently is AMBIGUOUS and is
// linked to neither - a 4D model coloured by a guess is indistinguishable from
// one coloured by the schedule.
//
// "LATE" NEEDS PROGRESS. With percent complete or actual dates the status is
// done / in_progress / future / late. Without them it is the PLANNED status and
// late is not decidable: nothing in a plan says whether the work happened.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static partial class ScheduleImport
    {
        /// <summary>Primavera P6 XER: TASK rows, WBS codes resolved through PROJWBS.</summary>
        public static ScheduleImportResult ParseXer(string text)
        {
            var r = new ScheduleImportResult { Format = "xer" };
            var tables = new Dictionary<string, (List<string> fields, List<(int line, List<string> cells)> rows)>(StringComparer.OrdinalIgnoreCase);
            string current = null;
            string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string[] c = lines[i].Split('\t');
                if (c[0] == "%T" && c.Length > 1) { current = c[1].Trim(); tables[current] = (new List<string>(), new List<(int, List<string>)>()); }
                else if (c[0] == "%F" && current != null) tables[current].fields.AddRange(c.Skip(1).Select(x => x.Trim()));
                else if (c[0] == "%R" && current != null) tables[current].rows.Add((i + 1, c.Skip(1).ToList()));
            }
            if (!tables.ContainsKey("TASK")) throw new ScheduleImportException("Not a readable XER: no %T TASK table.");

            // WBS: wbs_id -> dotted path of short names.
            var wbsShort = new Dictionary<string, string>();
            var wbsParent = new Dictionary<string, string>();
            if (tables.TryGetValue("PROJWBS", out var w))
                foreach (var row in w.rows)
                {
                    string Wv(string f) { int k = w.fields.IndexOf(f); return k >= 0 && k < row.cells.Count ? row.cells[k] : null; }
                    string id = Wv("wbs_id");
                    if (id == null) continue;
                    wbsShort[id] = Wv("wbs_short_name");
                    wbsParent[id] = Wv("parent_wbs_id");
                }
            string WbsPath(string id)
            {
                var parts = new List<string>();
                var guard = new HashSet<string>();
                while (id != null && wbsShort.ContainsKey(id) && guard.Add(id))
                {
                    // The project node has no parent in PROJWBS: its code is not part of the path.
                    if (wbsParent.TryGetValue(id, out string p) && p != null && wbsShort.ContainsKey(p)) parts.Insert(0, wbsShort[id]);
                    id = wbsParent.TryGetValue(id, out string next) ? next : null;
                }
                return parts.Count == 0 ? null : string.Join(".", parts);
            }

            var t = tables["TASK"];
            foreach (var row in t.rows)
            {
                string V(string f) { int k = t.fields.IndexOf(f); return k >= 0 && k < row.cells.Count ? row.cells[k].Trim() : null; }
                if (V("task_type") == "TT_WBS") { r.SummariesSkipped++; continue; }
                var a = new ScheduleActivity { Id = V("task_code"), Name = V("task_name"), Wbs = WbsPath(V("wbs_id")), Line = row.line };
                string start = V("target_start_date") ?? V("early_start_date");
                string finish = V("target_end_date") ?? V("early_end_date");
                if (!Dates(r, row.line, a, start, finish, V("act_start_date"), V("act_end_date"))) continue;
                if (double.TryParse(V("phys_complete_pct"), NumberStyles.Float, CultureInfo.InvariantCulture, out double pc)) a.PercentComplete = pc;
                r.Activities.Add(a);
            }
            return r;
        }
    }

    /// <summary>One element as the Revit side read it, for matching.</summary>
    public sealed class ScheduleElementFact
    {
        public long Id;
        public string CategoryToken;
        public string CategoryName;
        public string Level;
        public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ScheduleMatch
    {
        public Dictionary<long, ScheduleActivity> Links = new Dictionary<long, ScheduleActivity>();
        public List<JObject> Ambiguous = new List<JObject>();
        public List<long> WithoutActivity = new List<long>();
        public List<ScheduleActivity> ActivitiesWithoutElements = new List<ScheduleActivity>();
    }

    public static class ScheduleLinkRules
    {
        public static readonly string[] Statuses = { "done", "in_progress", "future", "late" };

        /// <summary>Validate the match spec; null when valid, else the refusal.</summary>
        public static string ValidateSpec(JObject spec, IList<ScheduleActivity> activities)
        {
            if (spec == null) return "match is required: {parameter, key: id|wbs} or {rules:[...]}.";
            bool byParam = spec["parameter"] != null, byRules = spec["rules"] != null;
            if (byParam == byRules) return "match needs exactly one of parameter or rules.";
            if (byParam)
            {
                string key = spec.Value<string>("key") ?? "id";
                return key == "id" || key == "wbs" ? null : "match.key must be id or wbs.";
            }
            var ids = new HashSet<string>(activities.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            foreach (JObject rule in (spec["rules"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string act = rule.Value<string>("activity");
                if (act == null || !ids.Contains(act)) return "match rule names activity '" + act + "', which the schedule does not carry.";
                if (string.IsNullOrWhiteSpace(rule.Value<string>("category")))
                    return "every match rule needs a category: a rule without one would claim the whole model.";
            }
            return (spec["rules"] as JArray)?.Count > 0 ? null : "match.rules is empty.";
        }

        public static bool CategoryIs(string wanted, ScheduleElementFact e) =>
            string.Equals(wanted, e.CategoryToken, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(wanted, e.CategoryName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Link elements to activities. Candidates: elements carrying the parameter, or in a rule's category.</summary>
        public static ScheduleMatch Match(JObject spec, IList<ScheduleActivity> activities, IList<ScheduleElementFact> elements)
        {
            var m = new ScheduleMatch();
            if (spec["parameter"] != null)
            {
                string param = spec.Value<string>("parameter");
                bool byWbs = (spec.Value<string>("key") ?? "id") == "wbs";
                var index = activities.Where(a => (byWbs ? a.Wbs : a.Id) != null)
                    .GroupBy(a => (byWbs ? a.Wbs : a.Id).Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (ScheduleElementFact e in elements)
                {
                    if (!e.Params.TryGetValue(param, out string v)) continue;       // not a candidate
                    if (string.IsNullOrWhiteSpace(v) || !index.TryGetValue(v.Trim(), out var hits)) { m.WithoutActivity.Add(e.Id); continue; }
                    if (hits.Count > 1)
                    {
                        m.Ambiguous.Add(new JObject { ["element_id"] = e.Id, ["value"] = v, ["activities"] = new JArray(hits.Select(h => h.Id)) });
                        continue;
                    }
                    m.Links[e.Id] = hits[0];
                }
            }
            else
            {
                var rules = ((JArray)spec["rules"]).OfType<JObject>().ToList();
                var byId = activities.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
                foreach (ScheduleElementFact e in elements)
                {
                    bool candidate = false;
                    var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JObject rule in rules)
                    {
                        if (!CategoryIs(rule.Value<string>("category"), e)) continue;
                        candidate = true;
                        string level = rule.Value<string>("level");
                        if (level != null && !string.Equals(level, e.Level, StringComparison.OrdinalIgnoreCase)) continue;
                        string p = rule.Value<string>("parameter");
                        if (p != null)
                        {
                            string want = rule["value"]?.ToString();
                            if (!e.Params.TryGetValue(p, out string have) ||
                                !string.Equals((have ?? "").Trim(), (want ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        assigned.Add(rule.Value<string>("activity"));
                    }
                    if (!candidate) continue;
                    if (assigned.Count == 0) m.WithoutActivity.Add(e.Id);
                    else if (assigned.Count > 1)
                        m.Ambiguous.Add(new JObject { ["element_id"] = e.Id, ["activities"] = new JArray(assigned.OrderBy(x => x)) });
                    else m.Links[e.Id] = byId[assigned.First()];
                }
            }
            var used = new HashSet<string>(m.Links.Values.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            m.ActivitiesWithoutElements = activities.Where(a => !used.Contains(a.Id)).ToList();
            return m;
        }

        /// <summary>done | in_progress | future | late, and whether progress data decided it.</summary>
        public static string Status(ScheduleActivity a, DateTime asOf, out bool fromProgress)
        {
            DateTime day = asOf.Date;
            fromProgress = a.HasProgress;
            if (fromProgress)
            {
                bool done = (a.PercentComplete ?? 0) >= 100 || (a.ActualFinish != null && a.ActualFinish <= day);
                if (done) return "done";
                bool started = (a.PercentComplete ?? 0) > 0 || (a.ActualStart != null && a.ActualStart <= day);
                if (a.Finish != null && a.Finish < day) return "late";
                if (!started && a.Start != null && a.Start < day) return "late";
                if (started) return "in_progress";
                return "future";
            }
            if (a.Finish != null && a.Finish <= day) return "done";
            if (a.Start != null && a.Start <= day) return "in_progress";
            return "future";
        }
    }
}
