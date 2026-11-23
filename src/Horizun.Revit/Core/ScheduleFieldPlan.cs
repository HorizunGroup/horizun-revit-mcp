// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_create_schedule: WHAT the schedule will carry, decided BEFORE the token is issued.
//
// Review found three defects in the first "build with what exists" version:
//   1. the rehearsal did not say which fields would be left out (it resolved nothing), so the token
//      authorised an apply nobody had previewed;
//   2. the apply compared the committed schedule against what IT had added, so a schedule missing
//      half the requested columns was declared verified in full;
//   3. a substitute that lands on a column already requested (FAMILY_LEVEL_PARAM -> Level, with Level
//      also requested) called AddField twice for one column.
//
// The command resolves each requested name against the fields Revit offers for the category (inside
// a transaction that is always rolled back, in the rehearsal and again in the apply) and hands the
// result here as plain facts: requested name, the token that resolved (or null) and the column's
// identity (field type + parameter id). This file decides, per request, one of
//   as_requested  the name resolved as written
//   substituted   the name did not resolve; its schedulable twin did (ScheduleFieldRescue.AliasesFor)
//   duplicate     it resolved to a column already in the schedule (requested earlier, or added by
//                 Revit itself); it is NOT added again
//   omitted       nothing resolved; the schedule is built without it (SUPUESTO)
// and the fingerprint of that decision, which is bound into the confirmation token.
//
// No Revit types: linked into the Core tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What resolving one requested name found. Identity null = nothing resolved.</summary>
    public sealed class ScheduleFieldResolution
    {
        public string Requested;
        /// <summary>The token that resolved: the requested name itself, or the alias that did.</summary>
        public string Used;
        /// <summary>The column's identity ("&lt;field type&gt;:&lt;parameter id&gt;"); null when nothing resolved.</summary>
        public string Identity;
    }

    public sealed class ScheduleFieldPlanEntry
    {
        public string Requested;
        public string Status;      // as_requested | substituted | duplicate | omitted
        public string Used;        // the token that resolved (null when omitted)
        public string Identity;
        public string DuplicateOf; // the requested name (or "added by Revit") already holding that column
        public bool Adds => Status == ScheduleFieldPlan.AsRequested || Status == ScheduleFieldPlan.Substituted;
    }

    public sealed class ScheduleFieldPlan
    {
        public const string AsRequested = "as_requested";
        public const string Substituted = "substituted";
        public const string Duplicate = "duplicate";
        public const string Omitted = "omitted";
        public const string AddedByRevit = "added by Revit";

        public List<ScheduleFieldPlanEntry> Entries = new List<ScheduleFieldPlanEntry>();

        public IEnumerable<ScheduleFieldPlanEntry> ToAdd => Entries.Where(e => e.Adds);
        public List<string> OmittedNames => Entries.Where(e => e.Status == Omitted).Select(e => e.Requested).ToList();
        public List<ScheduleFieldPlanEntry> Duplicates => Entries.Where(e => e.Status == Duplicate).ToList();
        public List<KeyValuePair<string, string>> SubstitutedPairs =>
            Entries.Where(e => e.Status == Substituted).Select(e => new KeyValuePair<string, string>(e.Requested, e.Used)).ToList();

        /// <summary>
        /// The plan for <paramref name="resolutions"/>, in request order. <paramref name="baseIdentities"/> are the
        /// columns Revit puts in the schedule by itself (a key schedule's Key Name): a request that resolves to one
        /// of them is a duplicate, never added twice.
        /// </summary>
        public static ScheduleFieldPlan Build(IEnumerable<ScheduleFieldResolution> resolutions, IEnumerable<string> baseIdentities = null)
        {
            var plan = new ScheduleFieldPlan();
            var holder = new Dictionary<string, string>(StringComparer.Ordinal);
            if (baseIdentities != null)
                foreach (string b in baseIdentities)
                    if (!string.IsNullOrEmpty(b) && !holder.ContainsKey(b)) holder[b] = AddedByRevit;
            foreach (ScheduleFieldResolution r in resolutions ?? new ScheduleFieldResolution[0])
            {
                var e = new ScheduleFieldPlanEntry { Requested = r.Requested, Used = r.Identity == null ? null : r.Used, Identity = r.Identity };
                if (r.Identity == null) e.Status = Omitted;
                else if (holder.TryGetValue(r.Identity, out string first)) { e.Status = Duplicate; e.DuplicateOf = first; }
                else
                {
                    holder[r.Identity] = r.Requested;
                    e.Status = string.Equals(r.Used, r.Requested, StringComparison.OrdinalIgnoreCase) ? AsRequested : Substituted;
                }
                plan.Entries.Add(e);
            }
            return plan;
        }

        /// <summary>True when fields were requested and not one of them resolved: there is no schedule to build.</summary>
        public bool NothingResolved => Entries.Count > 0 && Entries.All(e => e.Status == Omitted);

        /// <summary>
        /// The bound form of the decision: one token per request, in order. Two plans with the same fingerprint add
        /// the same columns, in the same order, and leave out the same names.
        /// </summary>
        public string Fingerprint()
            => string.Join(";", Entries.Select(e => e.Requested + "=" + e.Status + (e.Identity == null ? "" : ":" + e.Identity) +
                                                   (e.DuplicateOf == null ? "" : "<" + e.DuplicateOf)));

        /// <summary>
        /// Null when every group_by name is a column the schedule will have; otherwise the refusal. A name that was
        /// left out is refused as such: the column is missing because Revit does not offer it, not because the
        /// caller forgot it. A duplicate is refused naming the request that holds its column.
        /// </summary>
        public string GroupByProblem(string categoryName, IEnumerable<string> groupBy)
        {
            if (groupBy == null) return null;
            foreach (string raw in groupBy)
            {
                string name = (raw ?? "").Trim();
                ScheduleFieldPlanEntry e = Entries.FirstOrDefault(x => string.Equals(x.Requested, name, StringComparison.OrdinalIgnoreCase));
                if (e == null) continue;   // ScheduleGroupingRules.ValidateGroupBy answers names that were never requested
                if (e.Status == Omitted)
                    return "group_by names '" + name + "', which was left out of the schedule because Revit does not offer it as a field for " +
                           categoryName + ". A schedule cannot group by a column it does not have: group by a field Revit offers (see the " +
                           "suggestions for '" + name + "' in the rehearsal) or remove it from group_by. Nothing was changed.";
                if (e.Status == Duplicate)
                    return "group_by names '" + name + "', which resolves to the same column as '" + e.DuplicateOf + "' and is not added a second time. " +
                           "Group by '" + e.DuplicateOf + "' instead. Nothing was changed.";
            }
            return null;
        }

        public JArray ToJson()
        {
            var a = new JArray();
            foreach (ScheduleFieldPlanEntry e in Entries)
            {
                var o = new JObject { ["requested"] = e.Requested, ["status"] = e.Status };
                if (e.Used != null) o["used"] = e.Used;
                if (e.DuplicateOf != null) o["same_column_as"] = e.DuplicateOf;
                a.Add(o);
            }
            return a;
        }

        /// <summary>
        /// The REHEARSAL's declaration. It is always a clean one (one schedule, everything RESOLVED: a field Revit does
        /// not offer resolved to "left out", which is a SUPUESTO, not a refusal - product rule: a missing datum does
        /// not block). A direct call therefore succeeds and gets its token. When fields were left out the payload
        /// also carries PartialApplyNotice (AnnouncePartialApply): the apply will declare partial (Tally), and a plan step (execute_plan,
        /// apply_corrections) is not applied partially without being said. The SUPUESTO rows themselves are stamped
        /// by the command (FieldPlanAssumptions), with how to replace each field.
        /// </summary>
        public static JObject RehearsalDeclaration() => ApplicationOutcome.DeclareRehearsal(1, 0, 0, 0);

        /// <summary>The apply transaction is still unopened. The separate field probe was rolled back.</summary>
        public void StampRehearsal(JObject rehearsal)
        {
            rehearsal["transaction_status"] = ApplicationOutcome.NotStarted;
            rehearsal["transaction_status_scope"] = "apply";
            rehearsal["field_probe_transaction_status"] = ApplicationOutcome.RolledBackStatus;
            ApplicationOutcome.Stamp(rehearsal, RehearsalDeclaration());
            // The resolved work is fully rehearsed. Omitted fields affect the apply outcome.
            AnnouncePartialApply(rehearsal);
        }

        public static CommandResult WriteFailure(string error, long? id, string status, ApplicationState state,
                                                 string stage, bool scheduleReread, JObject postcondition,
                                                 IEnumerable<JObject> assumptions)
        {
            JObject detail = ApplicationOutcome.FailureAfterWrite(
                "schedule_id", id, stage, status, state, scheduleReread, postcondition);
            return WithAssumptions(error, detail, assumptions);
        }

        public static CommandResult RollbackFailure(string cause, string stage, long? id, string status,
                                                    bool? scheduleStillThere, IEnumerable<JObject> assumptions)
        {
            bool clean = PlanFailure.IsConfirmedRollback(status) && scheduleStillThere == false;
            JObject detail = ApplicationOutcome.FailureAfterWrite("schedule_id", id, stage, status,
                clean ? ApplicationState.RolledBack : ApplicationState.Uncertain, scheduleStillThere == true);
            detail["rollback_confirmed"] = PlanFailure.IsConfirmedRollback(status);
            detail[PlanFailure.ModelMayHaveChangedKey] = !clean;
            detail["schedule_still_in_model"] = scheduleStillThere.HasValue ? (JToken)scheduleStillThere.Value : JValue.CreateNull();
            string observation = scheduleStillThere == true ? "The schedule is still in the model."
                : scheduleStillThere == false ? "The schedule was not found when re-read."
                : "The schedule could not be re-read.";
            string outcome = clean ? "Revit confirmed the rollback. Nothing was changed."
                : "Rollback status: " + status + ". " + observation + " The model may have changed; inspect the schedules before retrying.";
            return WithAssumptions(cause + " " + outcome, detail, assumptions);
        }

        private static CommandResult WithAssumptions(string error, JObject detail, IEnumerable<JObject> assumptions)
            => AssumptionNotes.MarkFailure(CommandResult.FailWithDetail(error, detail),
                "The schedule used assumed field choices (SUPUESTO). ", assumptions);

        /// <summary>The PartialApplyNotice half of the rehearsal (see RehearsalDeclaration); nothing when no field was left out.</summary>
        public void AnnouncePartialApply(JObject rehearsal)
        {
            if (rehearsal == null) return;
            List<string> omitted = OmittedNames;
            if (omitted.Count > 0)
                PartialApplyNotice.Stamp(rehearsal,
                    omitted.Count + " of the " + Entries.Count + " requested fields are not offered by Revit for this category (" +
                    string.Join(", ", omitted) + "); the schedule is built without them and the apply declares partial");
        }

        /// <summary>
        /// The identity of one column: "&lt;field type&gt;:&lt;parameter id&gt;". Within ONE field type a valid
        /// parameter id names one column (material takeoffs included: the element's Area is Instance, the
        /// material's Area is MaterialQuantity, so the type tells them apart). The id is NOT unique when it is
        /// the invalid id (-1): custom fields, combined parameters, formulas and percentages all carry it, so
        /// there the field name is appended to tell two of them apart.
        /// </summary>
        public static string Identity(string fieldType, long parameterId, string name)
            => parameterId == -1
                ? (fieldType ?? "") + ":-1:" + (name ?? "")
                : (fieldType ?? "") + ":" + parameterId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// The apply's declaration when some requested fields were left out, counted against what was REQUESTED:
        /// requested = every requested name, verified = the ones the schedule carries (added, or sharing an added
        /// column), unresolved = the omitted ones. Partial by construction (ApplicationOutcome.Applied), never
        /// verified_applied. When the post-commit check did not hold, nothing is counted as verified.
        /// </summary>
        public static JObject Tally(string commitStatus, int requested, int omittedCount, bool postconditionVerified)
        {
            int present = postconditionVerified ? requested - omittedCount : 0;
            return ApplicationOutcome.DeclareApplied(commitStatus, requested, present, present, omittedCount,
                                                     postconditionVerified ? 0 : requested - omittedCount, 0);
        }

        /// <summary>The SUPUESTO row for requests that resolved to a column already in the schedule.</summary>
        public static JObject DuplicateRow(IList<ScheduleFieldPlanEntry> duplicates)
            => AssumptionNotes.Row(
                "fields not added twice: " + string.Join(", ", duplicates.Select(d => d.Requested + " (same column as " + d.DuplicateOf + ")")),
                "each resolves to a column the schedule already has, so it was added once.",
                "name each column once.");
    }

    /// <summary>How far the rolled-back "ask Revit for the fields" transaction got before it failed.</summary>
    public enum ScheduleProbeStage
    {
        /// <summary>The transaction never started (read-only document, a transaction already open, Start refused).</summary>
        NotStarted,
        /// <summary>Started; creating the schedule to ask failed, so no schedule existed. Rolled back.</summary>
        StartedNothingCreated,
        /// <summary>A schedule was created to ask, then rolled back.</summary>
        CreatedRolledBack,
        /// <summary>Rolling back failed or was not confirmed: the provisional schedule may still be in the model.</summary>
        RollbackFailed,
        /// <summary>Rolled back, yet the provisional schedule is still in the model.</summary>
        LeftInModel,
        /// <summary>Rollback was confirmed, but the presence of the provisional schedule could not be checked.</summary>
        PresenceUnverified
    }

    /// <summary>
    /// The messages of the field probe. A failure must say what really happened: "a schedule was created and
    /// rolled back" is only true when one was created and the rollback was confirmed.
    /// </summary>
    public static class ScheduleFieldProbe
    {
        /// <summary>
        /// Null when the probe transaction can start; otherwise the refusal, said BEFORE anything is tried. A
        /// read-only document (opened read-only, a linked or family document) and a document with a transaction
        /// already open (IsModifiable) cannot start one.
        /// </summary>
        public static string Precondition(bool isReadOnly, bool isModifiable, string categoryName)
        {
            if (isReadOnly)
                return "The document is read-only, so no transaction can start to ask Revit which fields it offers for " + categoryName +
                       ", and the schedule could not be created either. No schedule was created. Nothing was changed.";
            if (isModifiable)
                return "A transaction is already open in this document (another command or an edit mode is running), so the transaction " +
                       "used to ask Revit which fields it offers for " + categoryName + " cannot start. Finish or cancel it and try again. " +
                       "No schedule was created. Nothing was changed.";
            return null;
        }

        /// <summary>The refusal when the probe failed at <paramref name="stage"/> with <paramref name="error"/>.</summary>
        public static string Failure(ScheduleProbeStage stage, string categoryName, string error)
        {
            string head = "The fields could not be resolved against what Revit offers for " + categoryName + ": " + (error ?? "unknown error");
            switch (stage)
            {
                case ScheduleProbeStage.NotStarted:
                    return head + " (the transaction used to ask did not start; no schedule was created). Nothing was changed.";
                case ScheduleProbeStage.StartedNothingCreated:
                    return head + " (creating the schedule to ask failed, so none existed; the transaction was rolled back). Nothing was changed.";
                case ScheduleProbeStage.CreatedRolledBack:
                    return head + " (a schedule was created and rolled back to ask; nothing was kept). Nothing was changed.";
                case ScheduleProbeStage.LeftInModel:
                    return head + " (the transaction was rolled back, but the schedule created to ask is STILL in the model). " +
                           "The model may have changed: check the schedules before writing again.";
                case ScheduleProbeStage.PresenceUnverified:
                    return head + " (the transaction rolled back, but the schedule's presence could not be checked). " +
                           "The model may have changed: check the schedules before writing again.";
                default:
                    return head + " (rolling back the transaction used to ask did NOT complete, so the schedule created to ask may still " +
                           "be in the model). The model may have changed: check the schedules before writing again.";
            }
        }

        public static CommandResult FailureResult(ScheduleProbeStage stage, string categoryName, string error)
        {
            string message = Failure(stage, categoryName, error);
            if (ModelUntouched(stage)) return CommandResult.Fail(message);
            return CommandResult.FailWithDetail(message, new JObject
            {
                ["stage"] = "field_probe",
                ["probe_outcome"] = stage == ScheduleProbeStage.LeftInModel ? "schedule_left_in_model"
                    : stage == ScheduleProbeStage.PresenceUnverified ? "schedule_presence_unverified" : "rollback_unconfirmed",
                ["write_started"] = true,
                [PlanFailure.ModelMayHaveChangedKey] = true,
                [ApplicationOutcome.Key] = ApplicationOutcome.DeclareRehearsal(1, 0, 0, 1)
            });
        }

        /// <summary>
        /// The exception to throw when a rollback after <paramref name="original"/> also failed: the ORIGINAL
        /// cause is kept (message first, and as the inner exception) and the rollback's is added, never the
        /// other way round.
        /// </summary>
        public static Exception KeepOriginal(Exception original, Exception rollback)
        {
            if (original == null) return rollback;
            if (rollback == null) return original;
            return new InvalidOperationException(original.Message + "; rolling back afterwards also failed: " + rollback.Message, original);
        }

        /// <summary>
        /// The stage once the probe has been rolled back after a failure (or after its normal end). A rollback that
        /// threw or that Revit did not confirm (Guard.RollBack answers a status, it does not throw) is RollbackFailed;
        /// a confirmed rollback with the provisional schedule still present is LeftInModel. A failed re-read leaves
        /// its presence unverified. Only a confirmed rollback with the schedule gone can retain the original stage.
        /// </summary>
        public static ScheduleProbeStage AfterRollback(ScheduleProbeStage reached, bool rollbackConfirmed, bool? provisionalStillThere)
        {
            if (!rollbackConfirmed) return ScheduleProbeStage.RollbackFailed;
            if (!provisionalStillThere.HasValue) return ScheduleProbeStage.PresenceUnverified;
            return provisionalStillThere.Value ? ScheduleProbeStage.LeftInModel : reached;
        }

        /// <summary>True for the stages after which the model is known untouched.</summary>
        public static bool ModelUntouched(ScheduleProbeStage stage)
            => stage == ScheduleProbeStage.NotStarted || stage == ScheduleProbeStage.StartedNothingCreated ||
               stage == ScheduleProbeStage.CreatedRolledBack;

        /// <summary>
        /// What runs before the probe transaction, in its one order: whether the document can start a transaction,
        /// then - on an apply only - <paramref name="tokenRefusal"/> (token alive, of this command, on this document;
        /// spends nothing). Null to go on; otherwise the refusal. <paramref name="tokenRefusal"/> is never called on a
        /// dry run nor for a document that cannot start a transaction. <paramref name="tokenRefused"/> says which
        /// of the two refused.
        /// </summary>
        public static string BeforeProbe(bool isReadOnly, bool isModifiable, string categoryName, bool dryRun,
                                         Func<string> tokenRefusal, out bool tokenRefused)
        {
            tokenRefused = false;
            string cannotStart = Precondition(isReadOnly, isModifiable, categoryName);
            if (cannotStart != null) return cannotStart;
            if (dryRun || tokenRefusal == null) return null;
            string refused = tokenRefusal();
            if (refused == null) return null;
            tokenRefused = true;
            return "The confirmation cannot be used here: " + refused + ". Run the dry run again and apply with its token. " +
                   "No schedule was created (the fields were not even asked for). Nothing was changed.";
        }
    }
}
