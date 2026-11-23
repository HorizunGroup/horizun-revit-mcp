// Horizun Revit MCP - Revit-free schedule write preparation, execution and verification.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal sealed class ScheduleWriteOptions
    {
        public string ActivityParameter;
        public string StartParameter;
        public string FinishParameter;
        public string RequestedField;
        public bool AllowUnmarked;
        public string ActivityField => RequestedField ?? ScheduleLinkRules.DefaultActivityField;

        public static string Read(JObject request, out ScheduleWriteOptions options)
        {
            options = null;
            var w = request["write"] as JObject;
            string activity = w?.Value<string>("activity_parameter");
            if (string.IsNullOrWhiteSpace(activity))
                return "write.activity_parameter is required (a text instance parameter).";
            var parsed = new ScheduleWriteOptions
            {
                ActivityParameter = activity,
                StartParameter = w.Value<string>("start_parameter"),
                FinishParameter = w.Value<string>("finish_parameter"),
                RequestedField = w.Value<string>("activity_field")
            };
            if (parsed.ActivityField != "name" && parsed.ActivityField != "id" && parsed.ActivityField != "wbs")
                return "write.activity_field must be name, id or wbs.";
            string[] names = new[] { activity, parsed.StartParameter, parsed.FinishParameter }.Where(p => p != null).ToArray();
            if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                return "write parameter names must be nonblank and distinct.";
            JToken allowUnmarked = w["allow_unmarked"];
            if (allowUnmarked != null && allowUnmarked.Type != JTokenType.Boolean)
                return "write.allow_unmarked must be a boolean.";
            parsed.AllowUnmarked = (bool?)allowUnmarked == true;
            if (parsed.AllowUnmarked && names.Any(p => !p.StartsWith("HRZ_", StringComparison.OrdinalIgnoreCase)))
                return "write.allow_unmarked=true requires HRZ_* output parameters. Nothing was written.";
            options = parsed;
            return null;
        }
    }

    internal sealed class ScheduleWriteField
    {
        public string Identity;
        public string Before;
    }

    internal sealed class ScheduleWriteRow
    {
        public long Id;
        public string UniqueId;
        public string Activity;
        public Dictionary<string, string> Values;
        public string Unresolved;
        public bool? HasMark;
        public bool UsesUnmarkedOverride;
        internal readonly Dictionary<string, ScheduleWriteField> Fields = new Dictionary<string, ScheduleWriteField>(StringComparer.Ordinal);

        public PlannedElement PlanElement() => new PlannedElement
        {
            UniqueId = UniqueId, ElementId = Id, Action = PlannedAction.Modify,
            BeforeValues = Fields.ToDictionary(p => p.Key,
                p => new JArray(p.Value.Identity, p.Value.Before).ToString(Formatting.None), StringComparer.Ordinal),
            ProposedValues = Values
        };

        public bool Verify<T>(Func<string, T> resolve, Func<T, string, string> read, out JObject actual,
            out JObject readErrors, out bool confirmedChange) where T : class
        {
            T element = null;
            JObject errors = null;
            try { element = resolve(UniqueId); }
            catch (Exception ex) { errors = new JObject { ["element_read_error"] = ex.Message }; }

            string ReadParameter(string name)
            {
                if (element == null) return null;
                try { return read(element, Fields[name].Identity); }
                catch (Exception ex)
                {
                    errors ??= new JObject();
                    var parameterErrors = (JObject)(errors["parameter_read_errors"] ??= new JObject());
                    parameterErrors[name] = ex.Message;
                    return null;
                }
            }

            actual = new JObject();
            confirmedChange = false;
            bool verified = true;
            foreach (var expected in Values)
            {
                string value = ReadParameter(expected.Key);
                actual[expected.Key] = value == null ? JValue.CreateNull() : new JValue(value);
                bool matches = string.Equals(expected.Value, value, StringComparison.Ordinal);
                verified &= matches;
                confirmedChange |= matches && !string.Equals(value, Fields[expected.Key].Before, StringComparison.Ordinal);
            }
            readErrors = errors;
            return verified;
        }
    }

    internal static class ScheduleLinkWriteRules
    {
        internal delegate string Inspect<T>(T element, string name, out ScheduleWriteField field);

        private const string UnmarkedRefusal =
            "the matched element has no Horizun ownership mark and was not created by Horizun in this session. " +
            "Rehearse with write.allow_unmarked=true and HRZ_* output parameters, then apply with that token.";

        public static JObject ActivityFieldAssumption(string requestedField, string activityParameter) =>
            requestedField != null ? null :
                AssumptionNotes.Row("The activity name is written to " + activityParameter + ".", "write.activity_field was omitted.",
                    "Set write.activity_field to name, id or wbs.");

        public static List<ScheduleWriteRow> Prepare<T>(ScheduleMatch match, ScheduleWriteOptions options,
            Func<string, T> resolve, Func<T, bool> isOwned, Inspect<T> inspect, Func<T, bool?> hasMark) where T : class
        {
            var rows = new List<ScheduleWriteRow>();
            foreach (var link in match.Links.OrderBy(p => p.Key))
            {
                match.LinkedUniqueIds.TryGetValue(link.Key, out string uniqueId);
                var row = new ScheduleWriteRow { Id = link.Key, UniqueId = uniqueId, Activity = link.Value.Id };
                rows.Add(row);
                T element = string.IsNullOrEmpty(uniqueId) ? null : resolve(uniqueId);
                bool owned = element != null && isOwned(element);
                row.HasMark = element == null ? null : hasMark(element);
                row.Unresolved = TargetRefusal(element, owned, row.HasMark, options.AllowUnmarked);
                if (row.Unresolved != null) continue;
                row.UsesUnmarkedOverride = !owned;

                row.Values = ScheduleLinkRules.WriteValues(link.Value, options.RequestedField, options.ActivityParameter,
                    options.StartParameter, options.FinishParameter);
                if (row.Values == null)
                {
                    row.Unresolved = "activity '" + row.Activity + "' has no " +
                        options.ActivityField + " value.";
                    continue;
                }

                var identities = new HashSet<string>(StringComparer.Ordinal);
                match.MatchParameterIdentities.TryGetValue(row.Id, out var matchParameters);
                foreach (string name in row.Values.Keys)
                {
                    row.Unresolved = inspect(element, name, out ScheduleWriteField field);
                    if (row.Unresolved != null) break;
                    if (matchParameters?.Contains(field.Identity) == true)
                    {
                        row.Unresolved = "output parameter '" + name + "' is also a match parameter for this element; use a different output parameter.";
                        break;
                    }
                    if (!identities.Add(field.Identity))
                    {
                        row.Unresolved = "multiple output fields resolve to the same instance parameter.";
                        break;
                    }
                    row.Fields.Add(name, field);
                }
            }
            foreach (JObject issue in match.Ambiguous)
                rows.Add(new ScheduleWriteRow { Id = (long)issue["element_id"], UniqueId = (string)issue["unique_id"],
                    Unresolved = (string)issue["reason"] });
            return rows;
        }

        private static string TargetRefusal<T>(T element, bool owned, bool? hasMark, bool allowUnmarked) where T : class
        {
            if (element == null) return "the matched element no longer resolves by UniqueId.";
            if (owned) return null;
            if (hasMark == false) return allowUnmarked ? null : UnmarkedRefusal;
            return hasMark == true
                ? "the matched element has an untrusted Horizun ownership mark for this user; write.allow_unmarked applies only to elements without a mark."
                : "the absence of a Horizun ownership mark could not be verified; write.allow_unmarked requires a confirmed absence.";
        }

        public static void StampOwnership(JObject head, IReadOnlyList<ScheduleWriteRow> rows)
        {
            head["rows_unmarked"] = rows.Count(r => r.HasMark == false);
            head["rows_unmarked_ready"] = rows.Count(r => r.HasMark == false && r.Unresolved == null);
            head["rows_unmarked_override_ready"] = rows.Count(r => r.UsesUnmarkedOverride && r.Unresolved == null);
            if (rows.Any(r => r.Unresolved == UnmarkedRefusal))
                head["unmarked_note"] = UnmarkedRefusal;
        }

        public static int StampPreparation(JObject head, IReadOnlyList<ScheduleWriteRow> rows,
            IReadOnlyList<ScheduleWriteRow> ready, int maxRows, bool dryRun)
        {
            int unresolved = rows.Count - ready.Count;
            head["dry_run"] = dryRun;
            head["rows_ready"] = ready.Count;
            head["rows_unresolved"] = unresolved;
            StampOwnership(head, rows);
            head["unresolved"] = new JArray(rows.Where(r => r.Unresolved != null).Take(maxRows)
                .Select(r => new JObject { ["element_id"] = r.Id, ["unique_id"] = r.UniqueId, ["reason"] = r.Unresolved }));
            return unresolved;
        }

        public static CommandResult Rehearse(JObject head, IReadOnlyList<ScheduleWriteRow> rows,
            IReadOnlyList<ScheduleWriteRow> ready, int maxRows, ConfirmationStore confirmations,
            DocIdentity document, string planHash, ResolvedPlan plan, IEnumerable<JObject> assumptions)
        {
            int unresolved = StampPreparation(head, rows, ready, maxRows, dryRun: true);
            head["preview"] = new JArray(ready.Take(maxRows).Select(r => new JObject
                { ["element_id"] = r.Id, ["unique_id"] = r.UniqueId, ["activity"] = r.Activity,
                    ["allow_unmarked_used"] = r.UsesUnmarkedOverride, ["values"] = JObject.FromObject(r.Values) }));
            return ScheduleLinkRules.RehearseTargets(head, unresolved, confirmations, document, planHash, plan,
                "the token binds the schedule SHA-256, match and write options, resolved identities and values, and the unmarked elements allowed through the ownership gate.",
                assumptions);
        }

        public static CommandResult NothingToWrite(JObject head, int unresolved, IEnumerable<JObject> assumptions)
        {
            if (unresolved > 0)
                ApplicationOutcome.Stamp(head, WriteTally.PerTarget(ApplicationOutcome.NotStarted, 0, unresolved, 0, 0));
            return AssumptionNotes.MarkFailure(CommandResult.FailWithDetail(
                "Nothing to write: no row passed the match, ownership, activity value and writable text parameter checks. " +
                ((string)head["unmarked_note"] is string reason ? reason + " " : "") + "Nothing was changed.",
                head), "", assumptions);
        }

        public static void BindPlan(ResolvedPlan plan, IReadOnlyList<ScheduleWriteRow> ready)
        {
            plan.Elements.AddRange(ready.Select(r => r.PlanElement()));
            // The same targets and values can be ready under different ownership. Bind the
            // specific elements the person allowed through the override to this rehearsal.
            plan.ContextFingerprint = new JArray(ready.Where(r => r.UsesUnmarkedOverride)
                .Select(r => r.UniqueId).OrderBy(uid => uid, StringComparer.Ordinal)).ToString(Formatting.None);
        }

        // Returns null when every value was set or already matched and the caller can attempt to commit.
        public static CommandResult ApplyValues<T>(IReadOnlyList<ScheduleWriteRow> rows,
            Func<string, T> resolve, Func<T, bool> isOwned, Func<T, string, string, bool> set, Func<string> rollback,
            JObject head, int unresolved, IEnumerable<JObject> assumptions, Func<T, bool?> hasMark,
            Func<string> getTransactionStatus = null) where T : class
        {
            foreach (ScheduleWriteRow row in rows)
            {
                string failure;
                try
                {
                    T element = resolve(row.UniqueId);
                    bool owned = element != null && isOwned(element);
                    failure = TargetRefusal(element, owned, element == null || owned ? null : hasMark(element), row.UsesUnmarkedOverride);
                    if (failure == null)
                        foreach (var value in row.Values)
                        {
                            if (set(element, row.Fields[value.Key].Identity, value.Value)) continue;
                            failure = "Revit refused to set '" + value.Key + "' or its resolved parameter is unavailable.";
                            break;
                        }
                }
                catch (Exception ex)
                {
                    failure = "Revit could not write the schedule parameters: " + ex.Message;
                }
                if (failure != null)
                    return ScheduleLinkRules.FailAfterRollback("Element " + row.Id + ": " + failure, rollback,
                        head, rows.Count, unresolved, assumptions, getTransactionStatus);
            }
            return null;
        }

        public static CommandResult CompleteAfterCommit<T>(string status, IReadOnlyList<ScheduleWriteRow> rows,
            Func<string, T> resolve, Func<T, string, string> read, JObject head, int unresolved, int maxRows,
            IEnumerable<JObject> assumptions) where T : class
        {
            bool committed = status == ApplicationOutcome.Committed;
            int applied = 0, verified = 0, mismatched = 0;
            var verification = new JArray();
            foreach (ScheduleWriteRow row in rows)
            {
                bool ok = row.Verify(uid => committed ? resolve(uid) : null, read, out JObject values,
                    out JObject readErrors, out bool confirmedChange);
                if (ok || confirmedChange) applied++;
                if (ok) verified++;
                else if (committed && readErrors == null) mismatched++;
                if (!ok || verification.Count < maxRows)
                {
                    var evidence = readErrors ?? new JObject();
                    evidence["element_id"] = row.Id;
                    evidence["unique_id"] = row.UniqueId;
                    evidence["activity"] = row.Activity;
                    evidence["expected"] = JObject.FromObject(row.Values);
                    evidence["values"] = values;
                    evidence["verified"] = ok;
                    verification.Add(evidence);
                }
            }
            int unverified = rows.Count - verified;
            head["dry_run"] = false;
            head["verification"] = verification;
            head["rows_verified"] = verified;
            ApplicationOutcome.StampApplied(head, status, requested: rows.Count + unresolved, applied: applied,
                verified: verified, unresolved: unresolved, failed: mismatched, unknown: unverified - mismatched);
            if (committed && unverified == 0)
                return AssumptionNotes.StampResult(CommandResult.Ok(head), assumptions);

            if (committed)
                head["changes_applied"] = true;
            else if (!PlanFailure.IsConfirmedRollback(status))
                head[PlanFailure.ModelMayHaveChangedKey] = true;
            return AssumptionNotes.MarkFailure(CommandResult.FailWithDetail("The write did not verify: commit " + status +
                ", " + unverified + " row(s) could not be confirmed by re-reading their values." +
                (committed ? " Changes were committed." : PlanFailure.ModelMayHaveChanged(head) ? " The model may have changed." : ""),
                head), "", assumptions);
        }
    }
}
