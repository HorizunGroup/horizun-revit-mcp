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
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
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
        public string UniqueId;
        public string CategoryToken;
        public string CategoryName;
        public string Level;
        public Dictionary<string, string> Params = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> ParameterIssues = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> InstanceParameterIdentities = new Dictionary<string, string>(StringComparer.Ordinal);

        internal void ReadParameter<T>(string name, Func<IList<T>> instance, Func<IList<T>> type,
            Func<T, ParamFact> read, Func<T, string> identity) where T : class
        {
            try
            {
                string issue = ScheduleLinkRules.ResolveParameterName(name, instance(), out T parameter);
                bool fromInstance = parameter != null;
                if (issue == null && !fromInstance)
                    issue = ScheduleLinkRules.ResolveParameterName(name, type(), out parameter);
                if (issue != null)
                {
                    ParameterIssues[name] = issue;
                    return;
                }
                if (parameter == null) return;
                ParamFact fact = read(parameter);
                if (!fact.Unreadable)
                {
                    if (fromInstance) InstanceParameterIdentities[name] = identity(parameter);
                    Params[name] = fact.Text ?? "";
                    return;
                }
            }
            catch (Exception ex)
            {
                ParameterIssues[name] = "could not read match parameter '" + name + "': " + ex.Message;
                return;
            }
            ParameterIssues[name] = "could not read match parameter '" + name + "'.";
        }
    }

    public sealed class ScheduleMatch
    {
        public Dictionary<long, ScheduleActivity> Links = new Dictionary<long, ScheduleActivity>();
        internal readonly Dictionary<long, string> LinkedUniqueIds = new Dictionary<long, string>();
        internal readonly Dictionary<long, HashSet<string>> MatchParameterIdentities = new Dictionary<long, HashSet<string>>();
        public List<JObject> Ambiguous = new List<JObject>();
        public List<long> WithoutActivity = new List<long>();
        public List<ScheduleActivity> ActivitiesWithoutElements = new List<ScheduleActivity>();

        internal void Link(ScheduleElementFact element, ScheduleActivity activity, IEnumerable<string> matchParameters)
        {
            Links[element.Id] = activity;
            LinkedUniqueIds[element.Id] = element.UniqueId;
            MatchParameterIdentities[element.Id] = new HashSet<string>(matchParameters
                .Where(element.InstanceParameterIdentities.ContainsKey)
                .Select(p => element.InstanceParameterIdentities[p]), StringComparer.Ordinal);
        }
    }

    public static class ScheduleLinkRules
    {
        public static readonly string[] Statuses = { "done", "in_progress", "future", "late" };

        public const string DefaultActivityField = "name";

        internal static string ResolveParameterName<T>(string name, IList<T> candidates, out T parameter) where T : class
        {
            parameter = candidates.Count == 1 ? candidates[0] : null;
            return candidates.Count > 1
                ? "parameter name '" + name + "' is ambiguous (" + candidates.Count +
                    " parameters share it); use a unique parameter name or a BuiltInParameter token for a built-in."
                : null;
        }

        internal static string PlanHash(JObject request, string fileSha, string operation)
        {
            var scope = new JObject
            {
                ["op"] = operation, ["file"] = fileSha, ["match"] = request["match"], ["write"] = request["write"],
                ["view_id"] = request["view_id"], ["as_of"] = request["as_of"],
                ["include_as_of_day"] = request["include_as_of_day"]
            };
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(scope.ToString(Formatting.None)))).Replace("-", "");
        }

        internal static CommandResult RehearseTargets(JObject head, int unresolved, ConfirmationStore confirmations,
            DocIdentity document, string planHash, ResolvedPlan plan, string limitNote, IEnumerable<JObject> assumptions)
        {
            head["dry_run"] = true;
            int ready = plan.Elements.Count;
            int requested = ready + unresolved;
            bool canConfirm = ready > 0;
            if (canConfirm && unresolved > 0)
                ApplicationOutcome.Stamp(head, ApplicationOutcome.Declare(ApplicationState.PartiallyRehearsed,
                    ApplicationOutcome.NotStarted, requested, 0, 0, unresolved, 0, 0));
            else if (requested == 0)
                ApplicationOutcome.StampApplied(head, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
            else
                ApplicationOutcome.StampRehearsal(head, requested, unresolved, 0, 0);

            confirmations.StampResponse(head, document, plan.Command, planHash, canConfirm, canConfirm ? plan : null, limitNote);
            var result = CommandResult.Ok(head);
            return result.Success ? AssumptionNotes.StampResult(result, assumptions)
                : AssumptionNotes.MarkFailure(result, "", assumptions);
        }

        public static Dictionary<string, string> WriteValues(ScheduleActivity activity, string field,
            string activityParameter, string startParameter, string finishParameter)
        {
            string value = (field ?? DefaultActivityField) switch
            {
                "name" => activity.Name,
                "id" => activity.Id,
                "wbs" => activity.Wbs,
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            if (string.IsNullOrWhiteSpace(value)) return null;
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { [activityParameter] = value };
            if (startParameter != null) values.Add(startParameter, ScheduleImport.Day(activity.Start) ?? "");
            if (finishParameter != null) values.Add(finishParameter, ScheduleImport.Day(activity.Finish) ?? "");
            return values;
        }

        public static JObject StatusCutoffAssumption(bool? includeAsOfDay) =>
            includeAsOfDay.HasValue ? null :
                AssumptionNotes.Row("The status includes the as_of day.", "include_as_of_day was omitted.",
                    "Set include_as_of_day to false to stop at the end of the previous day.");

        public static DateTime? LastIncludedDay(DateTime asOf, bool? includeAsOfDay)
        {
            DateTime day = asOf.Date;
            if (includeAsOfDay != false) return day;
            return day == DateTime.MinValue ? (DateTime?)null : day.AddDays(-1);
        }

        /// <summary>Validate the match spec; null when valid, else the refusal.</summary>
        public static string ValidateSpec(JObject spec, ScheduleImportResult schedule) =>
            ValidateSpec(spec, schedule.Activities.Concat(schedule.DuplicateActivities), schedule.Format == "mspdi");

        public static string ValidateSpec(JObject spec, IEnumerable<ScheduleActivity> activities) =>
            ValidateSpec(spec, activities, false);

        private static string ValidateSpec(JObject spec, IEnumerable<ScheduleActivity> activities, bool mspdi)
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
                string id = mspdi ? ScheduleImport.NormalizeMspdiId(act) : act;
                if (id == null || !ids.Contains(id)) return "match rule names activity '" + act + "', which the schedule does not carry.";
                if (string.IsNullOrWhiteSpace(rule.Value<string>("category")))
                    return "every match rule needs a category: a rule without one would claim the whole model.";
            }
            return (spec["rules"] as JArray)?.Count > 0 ? null : "match.rules is empty.";
        }

        public static bool CategoryIs(string wanted, ScheduleElementFact e) =>
            string.Equals(wanted, e.CategoryToken, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(wanted, e.CategoryName, StringComparison.OrdinalIgnoreCase);

        private static JObject ParameterIssue(ScheduleElementFact e, string parameter, string reason) =>
            new JObject { ["element_id"] = e.Id, ["unique_id"] = e.UniqueId, ["parameter"] = parameter, ["reason"] = reason };

        private static JObject DuplicateActivityDetail(IGrouping<string, ScheduleActivity> rows) =>
            new JObject { ["activity"] = rows.Key, ["schedule_lines"] = new JArray(rows.Select(a => a.Line).OrderBy(line => line)) };

        private static JObject ActivityMatchIssue(ScheduleElementFact e, IEnumerable<string> activityIds,
            IReadOnlyDictionary<string, IGrouping<string, ScheduleActivity>> duplicates, string conflictReason)
        {
            var ids = activityIds.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            var issue = new JObject { ["element_id"] = e.Id, ["unique_id"] = e.UniqueId };
            if (ids.Count == 1)
            {
                var rows = duplicates[ids[0]];
                issue.Merge(DuplicateActivityDetail(rows));
                issue["reason"] = "schedule activity id '" + rows.Key + "' is duplicated; give each activity a unique id.";
            }
            else
            {
                issue["reason"] = conflictReason;
                issue["activities"] = new JArray(ids);
                var duplicated = new JArray(ids.Where(duplicates.ContainsKey).Select(id => DuplicateActivityDetail(duplicates[id])));
                if (duplicated.Count > 0) issue["duplicate_activities"] = duplicated;
            }
            return issue;
        }

        /// <summary>Link elements to activities. Candidates: elements carrying the parameter, or in a rule's category.</summary>
        public static ScheduleMatch Match(JObject spec, ScheduleImportResult schedule, IList<ScheduleElementFact> elements) =>
            Match(spec, schedule.Activities.Concat(schedule.DuplicateActivities), elements, schedule.Format == "mspdi");

        public static ScheduleMatch Match(JObject spec, IEnumerable<ScheduleActivity> activities, IList<ScheduleElementFact> elements) =>
            Match(spec, activities, elements, false);

        private static ScheduleMatch Match(JObject spec, IEnumerable<ScheduleActivity> activities, IList<ScheduleElementFact> elements, bool mspdi)
        {
            var m = new ScheduleMatch();
            var byId = activities.ToLookup(a => a.Id, StringComparer.OrdinalIgnoreCase);
            var duplicates = byId.Where(g => g.Skip(1).Any()).ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase);
            if (spec["parameter"] != null)
            {
                string param = spec.Value<string>("parameter");
                bool byWbs = (spec.Value<string>("key") ?? "id") == "wbs";
                var index = byId.SelectMany(g => g).Where(a => (byWbs ? a.Wbs : a.Id) != null)
                    .GroupBy(a => (byWbs ? a.Wbs : a.Id).Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (ScheduleElementFact e in elements)
                {
                    if (e.ParameterIssues.TryGetValue(param, out string issue))
                    {
                        m.Ambiguous.Add(ParameterIssue(e, param, issue));
                        continue;
                    }
                    if (!e.Params.TryGetValue(param, out string v)) continue;       // not a candidate
                    string key = mspdi && !byWbs ? ScheduleImport.NormalizeMspdiId(v) : v?.Trim();
                    if (string.IsNullOrEmpty(key) || !index.TryGetValue(key, out var hits)) { m.WithoutActivity.Add(e.Id); continue; }
                    if (hits.Count > 1 || duplicates.ContainsKey(hits[0].Id))
                    {
                        JObject row = ActivityMatchIssue(e, hits.Select(h => h.Id), duplicates,
                            "the match key names more than one schedule activity.");
                        row["parameter"] = param;
                        row["value"] = v;
                        m.Ambiguous.Add(row);
                        continue;
                    }
                    m.Link(e, hits[0], new[] { param });
                }
            }
            else
            {
                var rules = ((JArray)spec["rules"]).OfType<JObject>().ToList();
                foreach (ScheduleElementFact e in elements)
                {
                    bool candidate = false;
                    var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var matchParameters = new HashSet<string>(StringComparer.Ordinal);
                    JObject parameterIssue = null;
                    foreach (JObject rule in rules)
                    {
                        if (!CategoryIs(rule.Value<string>("category"), e)) continue;
                        candidate = true;
                        string level = rule.Value<string>("level");
                        if (level != null && !string.Equals(level, e.Level, StringComparison.OrdinalIgnoreCase)) continue;
                        string p = rule.Value<string>("parameter");
                        if (p != null)
                        {
                            if (e.ParameterIssues.TryGetValue(p, out string issue))
                            {
                                parameterIssue = ParameterIssue(e, p, issue);
                                break;
                            }
                            matchParameters.Add(p);
                            string want = rule["value"]?.ToString();
                            if (!e.Params.TryGetValue(p, out string have) ||
                                !string.Equals((have ?? "").Trim(), (want ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        string id = rule.Value<string>("activity");
                        assigned.Add(mspdi ? ScheduleImport.NormalizeMspdiId(id) : id);
                    }
                    if (!candidate) continue;
                    if (parameterIssue != null) m.Ambiguous.Add(parameterIssue);
                    else if (assigned.Count == 0) m.WithoutActivity.Add(e.Id);
                    else if (assigned.Count > 1 || duplicates.ContainsKey(assigned.Single()))
                        m.Ambiguous.Add(ActivityMatchIssue(e, assigned, duplicates, "the match rules assign more than one activity."));
                    else m.Link(e, byId[assigned.Single()].Single(), matchParameters);
                }
            }
            var used = new HashSet<string>(m.Links.Values.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            m.ActivitiesWithoutElements = byId.Where(g => !used.Contains(g.Key) && !duplicates.ContainsKey(g.Key))
                .Select(g => g.Single()).ToList();
            return m;
        }

        /// <summary>done | in_progress | future | late, and whether progress data decided it.</summary>
        public static string Status(ScheduleActivity a, DateTime asOf, out bool fromProgress, bool? includeAsOfDay = null) =>
            Status(a, LastIncludedDay(asOf, includeAsOfDay), out fromProgress, out _);

        private static string Status(ScheduleActivity a, DateTime? lastIncludedDay, out bool fromProgress,
            out bool doneFromUndatedProgress)
        {
            bool Reached(DateTime? date) => date?.Date <= lastIncludedDay;
            bool Past(DateTime? date) => date?.Date < lastIncludedDay;
            fromProgress = a.HasProgress;
            doneFromUndatedProgress = false;
            if (fromProgress)
            {
                doneFromUndatedProgress = !a.ActualFinish.HasValue && (a.PercentComplete ?? 0) >= 100 &&
                    (!a.ActualStart.HasValue || Reached(a.ActualStart));
                if (Reached(a.ActualFinish) || doneFromUndatedProgress) return "done";
                bool started = a.ActualStart.HasValue ? Reached(a.ActualStart) : (a.PercentComplete ?? 0) > 0;
                if (Past(a.Finish) || (!started && Past(a.Start))) return "late";
                if (started) return "in_progress";
                return "future";
            }
            if (Reached(a.Finish)) return "done";
            if (Reached(a.Start)) return "in_progress";
            return "future";
        }

        public static JObject StatusBasis(ScheduleMatch match, DateTime asOf, bool? includeAsOfDay, int maxRows,
            out Dictionary<long, string> status)
        {
            status = new Dictionary<long, string>();
            int fromProgress = 0, zeroAsPlanned = 0;
            var undated = new SortedSet<string>(StringComparer.Ordinal);
            DateTime? lastIncludedDay = LastIncludedDay(asOf, includeAsOfDay);
            foreach (var link in match.Links)
            {
                ScheduleActivity activity = link.Value;
                status[link.Key] = Status(activity, lastIncludedDay, out bool progress, out bool doneFromUndated);
                if (progress) fromProgress++;
                else if (activity.ZeroWithoutActuals) zeroAsPlanned++;
                if (doneFromUndated) undated.Add(activity.Id);
            }
            return new JObject
            {
                ["from_progress"] = fromProgress, ["planned_only"] = status.Count - fromProgress,
                ["zero_percent_as_planned"] = zeroAsPlanned,
                ["done_from_undated_progress_activity_count"] = undated.Count,
                ["done_from_undated_progress_activity_ids"] = new JArray(undated.Take(maxRows)),
                ["means"] = "Planned-only elements use planned dates and are never late. Zero percent without actual dates counts as planned-only. " +
                    "The listed activity IDs are done based on 100 percent in the schedule snapshot, with no actual finish date."
            };
        }

        internal static void BindStatusViewPlan(ResolvedPlan plan, ScheduleMatch match, Dictionary<long, string> status,
            long sourceViewId, string sourceViewUniqueId, string name)
        {
            if (status.Count == 0) return;
            plan.Elements.Add(new PlannedElement
            {
                UniqueId = sourceViewUniqueId, ElementId = sourceViewId, Action = PlannedAction.Create,
                ProposedValues = new Dictionary<string, string> { ["view_name"] = name }
            });
            foreach (var target in status.OrderBy(s => s.Key))
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = match.LinkedUniqueIds[target.Key], ElementId = target.Key, Action = PlannedAction.Modify,
                    ProposedValues = new Dictionary<string, string> { { "status", target.Value } }
                });
        }

        internal static CommandResult NoStatusViewTargets(JObject head, ScheduleMatch match, IEnumerable<JObject> assumptions)
        {
            head["dry_run"] = false;
            head["write_started"] = false;
            ApplicationOutcome.Stamp(head, WriteTally.PerTarget(ApplicationOutcome.NotStarted, 0, match.Ambiguous.Count, 0, 0));
            return AssumptionNotes.MarkFailure(CommandResult.FailWithDetail(
                "No element is linked to an activity; there is nothing to colour. Nothing was changed.", head), "", assumptions);
        }

        internal static CommandResult FailAfterRollback(string error, Func<string> rollback, JObject head,
            int resolved, int unresolved, IEnumerable<JObject> assumptions, Func<string> getTransactionStatus = null)
        {
            string status;
            bool attempted = false;
            try
            {
                status = getTransactionStatus == null ? "Started" : getTransactionStatus();
                if (status == "Started")
                {
                    attempted = true;
                    status = rollback();
                }
            }
            catch (Exception ex)
            {
                status = "unknown";
                head[attempted ? "rollback_error" : "transaction_status_error"] = ex.Message;
            }
            return FailTransaction(error, status, attempted, head, resolved, unresolved, assumptions);
        }

        private static CommandResult FailTransaction(string error, string status, bool rollbackAttempted, JObject head,
            int resolved, int unresolved, IEnumerable<JObject> assumptions)
        {
            bool confirmed = PlanFailure.IsConfirmedRollback(status);
            head["dry_run"] = false;
            head["write_started"] = true;
            head["transaction_status"] = status;
            head["rollback_attempted"] = rollbackAttempted;
            head["rollback_status"] = rollbackAttempted ? status : PlanFailure.NotAttempted;
            head["rollback_confirmed"] = confirmed;
            head[PlanFailure.ModelMayHaveChangedKey] = !confirmed;
            bool committed = status == ApplicationOutcome.Committed;
            if (committed) head["changes_applied"] = true;
            ApplicationOutcome.Stamp(head, WriteTally.PerTarget(status, resolved, unresolved, 0, confirmed ? resolved : 0));
            string diagnostic = rollbackAttempted ? " Rollback: " + status + "." :
                " Transaction status: " + status + ". Rollback was not attempted.";
            string outcome = confirmed ? " Nothing was changed." : committed ? " Changes were committed." : " The model may have changed.";
            return AssumptionNotes.MarkFailure(CommandResult.FailWithDetail(error + diagnostic + outcome, head), "", assumptions);
        }

        internal static CommandResult ReportCommittedCleanupFailure(CommandResult result, string cleanupError)
        {
            if (cleanupError == null) return result;

            // Revit confirmed the commit before Dispose threw. Keep the read-back evidence
            // and let the sync ledger record the failed command against the committed changes.
            JObject detail = result.Success ? (JObject)result.Data : result.Detail;
            detail["transaction_status"] = ApplicationOutcome.Committed;
            detail["transaction_cleanup_error"] = cleanupError;
            detail["write_started"] = true;
            detail["changes_applied"] = true;
            string outcome = result.Success ? "Revit committed the changes and the post-commit read completed." : result.Error;
            return CommandResult.FailCoded(ErrorCodes.VerificacionFallida,
                outcome + " Transaction cleanup error: " + cleanupError, extra: detail);
        }

        internal static CommandResult CompleteStatusView(JObject head, ScheduleMatch match, string commitStatus,
            bool viewVerified, bool viewMeasured, int overridesVerified, int overridesRefused,
            int overridesMismatched, int overridesUnverified, IEnumerable<JObject> assumptions)
        {
            if (commitStatus != ApplicationOutcome.Committed)
                return FailTransaction("Revit could not confirm the status view commit.", commitStatus, false,
                    head, match.Links.Count + 1, match.Ambiguous.Count, assumptions);

            head["dry_run"] = false;
            ApplicationOutcome.Stamp(head, WriteTally.PerTarget(commitStatus, match.Links.Count + 1, match.Ambiguous.Count,
                overridesVerified + (viewVerified ? 1 : 0),
                overridesRefused + overridesMismatched + (viewMeasured && !viewVerified ? 1 : 0)));
            if (viewVerified && overridesRefused == 0 && overridesUnverified == 0)
                return AssumptionNotes.StampResult(CommandResult.Ok(head), assumptions);

            // The view was duplicated in a committed transaction, so a failed re-read must block sync.
            head["changes_applied"] = true;
            return AssumptionNotes.MarkFailure(CommandResult.FailWithDetail("The status view did not fully verify: commit " + commitStatus + ", " +
                overridesRefused + " override(s) refused, " + overridesUnverified +
                " not re-read as requested. Changes were committed.", head), "", assumptions);
        }
    }
}
