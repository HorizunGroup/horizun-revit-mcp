// -----------------------------------------------------------------------------
// Horizun Revit MCP - the schedule DEFINITION as a typed, verified surface.
//
// horizun_manage_schedules edits what a schedule IS - its fields, filters,
// sorting, grouping, totals, headings - and creates the kinds of schedule
// horizun_create_schedule does not (material takeoffs, sheet/view lists,
// revision schedules, keynote legends). Placement on sheets stays with
// horizun_manage_views and horizun_pack_sheets; reading rows stays with
// horizun_get_schedule_data. One command per concern, no overlaps.
//
// The shape is the manage_views shape: one atomic batch, aliases for objects
// created earlier in the same batch, dry-run that validates and issues the
// token, apply that verifies each action while the transaction can still roll
// back and again after commit. What is schedule-specific:
//
//   * every action that EDITS binds the target schedule's definition
//     fingerprint into the plan, so a colleague's edit between rehearsal and
//     apply refuses as stale rather than being silently overwritten;
//   * fields resolve by stable identity (parameter id) or unambiguous name,
//     with the two-Comments-columns refusal proved in ScheduleEditRules;
//   * set_filters and set_sorting DECLARE the whole list - running the same
//     batch twice produces the same schedule, not doubled filters;
//   * the reply carries the canonical definition before and after, and the
//     exact sections that changed, so review is a diff rather than a memory.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ManageSchedulesCommand : ICommand
    {
        public string Name => "horizun_manage_schedules";
        public string Description =>
            "Create material takeoffs, sheet/view lists, revision schedules and keynote legends, and edit any " +
            "schedule's definition - fields, filters, sorting, totals, headings - in one verified atomic batch.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray input = request["actions"] as JArray;
            if (input == null || input.Count == 0 || input.Count > 100)
                return CommandResult.Fail("actions must contain 1..100 entries.");

            // ---- validate every action, and while doing so bind each TARGET's ------
            // current definition into the plan.
            var errors = new JArray();
            var outcomes = new List<ActionOutcome>();
            var knownKeys = new HashSet<string>(StringComparer.Ordinal);
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            for (int i = 0; i < input.Count; i++)
            {
                JObject a = input[i] as JObject;
                string reason = null;
                string error = Validate(doc, a, knownKeys, out reason);
                if (error != null)
                {
                    errors.Add(new JObject { ["index"] = i, ["error"] = error });
                    outcomes.Add(new ActionOutcome { Index = i, Error = error, UnsupportedReason = reason });
                    continue;
                }
                string key = a.Value<string>("key");
                if (!string.IsNullOrWhiteSpace(key)) knownKeys.Add(key);

                string op = a.Value<string>("operation");
                var row = new PlannedElement
                {
                    UniqueId = "action:" + i,
                    Category = op,
                    Action = op == ScheduleEditRules.OpCreate || op == ScheduleEditRules.OpDuplicate
                        ? PlannedAction.Create : PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string>()
                };
                ViewSchedule target = TryTargetById(doc, a);
                if (target != null)
                {
                    // The whole definition, fingerprinted: this is what "the schedule I
                    // approved editing" means, and what a concurrent edit trips over.
                    row.BeforeValues["schedule"] = SafeUid(target) + "|" + SafeName(target);
                    row.BeforeValues["definition_fingerprint"] =
                        ScheduleEditRules.DefinitionFingerprint(Canonical(doc, target));
                }
                resolvedPlan.Elements.Add(row);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "actions");

            if (dryRun)
            {
                var dryResult = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started",
                    ["actions"] = input.Count, ["valid"] = input.Count - errors.Count,
                    ["invalid"] = errors.Count, ["errors"] = errors,
                    ["definitions"] = CurrentDefinitions(doc, input),
                    ["note"] = "Nothing was created or changed. definitions shows each named target's CURRENT " +
                               "canonical definition - the state the token binds."
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                ApplicationOutcome.StampRehearsal(dryResult, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(dryResult, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0
                        ? "the token binds the ordered actions AND each target schedule's whole definition " +
                          "fingerprint - a filter added by somebody else refuses as a stale plan rather than " +
                          "being overwritten."
                        : "no usable token is issued while an action is invalid");
                return FallbackDecision.Attach(CommandResult.Ok(dryResult),
                                               FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
                return FallbackDecision.Refuse("Invalid actions; nothing ran: " + errors.ToString(Formatting.None),
                                               FallbackDecision.Decide(outcomes, writeStarted: false));
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: manage schedules";
            var aliases = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            var applied = new List<Applied>();
            var before = new Dictionary<long, string>();
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                var opts = tx.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(opts);
                try
                {
                    for (int i = 0; i < input.Count; i++)
                    {
                        JObject action = (JObject)input[i];
                        ViewSchedule result = Apply(doc, action, aliases, before);
                        if (result == null)
                            throw new InvalidOperationException("operation '" + action.Value<string>("operation") +
                                "' produced no schedule; the whole batch is rolling back.");
                        string key = action.Value<string>("key");
                        if (!string.IsNullOrWhiteSpace(key)) aliases[key] = result.Id;
                        applied.Add(new Applied { Index = i, Action = action, Id = result.Id });
                    }
                    doc.Regenerate();
                    foreach (Applied a in applied)
                    {
                        string why;
                        if (!Verify(doc, a, out why))
                            throw new InvalidOperationException("action " + a.Index + " ('" +
                                a.Action.Value<string>("operation") + "') failed verification while the " +
                                "transaction was still reversible (" + why + "). The whole batch is rolling back.");
                    }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Atomic schedule batch failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing in it was kept"));
                }
            }

            // ---- after the commit: the same checks again, plus the diff -------------
            var rows = new JArray(); int verified = 0;
            foreach (Applied a in applied)
            {
                string why;
                bool ok = Verify(doc, a, out why);
                if (ok) verified++;
                var schedule = doc.GetElement(a.Id) as ViewSchedule;
                string canonicalAfter = schedule == null ? null : Canonical(doc, schedule);
                string canonicalBefore;
                before.TryGetValue(Rid.Value(a.Id), out canonicalBefore);
                rows.Add(new JObject
                {
                    ["index"] = a.Index,
                    ["operation"] = a.Action.Value<string>("operation"),
                    ["schedule_id"] = Rid.Value(a.Id),
                    ["schedule_name"] = schedule == null ? null : SafeName(schedule),
                    ["verified"] = ok,
                    ["why_not"] = ok ? (JToken)JValue.CreateNull() : new JValue(why),
                    ["definition_fingerprint_before"] = canonicalBefore == null
                        ? (JToken)JValue.CreateNull()
                        : new JValue(ScheduleEditRules.DefinitionFingerprint(canonicalBefore)),
                    ["definition_fingerprint_after"] = canonicalAfter == null
                        ? (JToken)JValue.CreateNull()
                        : new JValue(ScheduleEditRules.DefinitionFingerprint(canonicalAfter)),
                    ["changed_sections"] = canonicalBefore == null || canonicalAfter == null
                        ? (JToken)JValue.CreateNull()
                        : new JArray(ScheduleEditRules.ChangedSections(canonicalBefore, canonicalAfter))
                });
            }
            if (verified != applied.Count)
                return CommandResult.Fail("The transaction committed, but only " + verified + " of " +
                    applied.Count + " actions passed post-commit verification. Inspect the model: " +
                    rows.ToString(Formatting.None));

            var result2 = new JObject
            {
                ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["actions_verified"] = verified,
                ["aliases"] = new JObject(aliases.Select(kv => new JProperty(kv.Key, Rid.Value(kv.Value)))),
                ["rows"] = rows
            };
            ApplicationOutcome.StampApplied(result2, ApplicationOutcome.Committed,
                                            applied.Count, verified, verified, 0, 0, 0);
            return CommandResult.Ok(result2);
        }

        // =====================================================================
        // Validation
        // =====================================================================

        private static string Validate(Document doc, JObject a, HashSet<string> knownKeys, out string unsupportedReason)
        {
            unsupportedReason = null;
            if (a == null) return "action is not an object";
            string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
            string vocabulary = ScheduleEditRules.ValidateOperation(op);
            if (vocabulary != null)
            {
                unsupportedReason = FallbackSignal.ReasonUnsupportedOperation;
                return vocabulary;
            }
            string key = a.Value<string>("key");
            if (!string.IsNullOrWhiteSpace(key) && knownKeys.Contains(key)) return "key '" + key + "' is duplicated";

            try
            {
                if (op == ScheduleEditRules.OpCreate)
                {
                    string kind = (a.Value<string>("kind") ?? "").ToLowerInvariant();
                    string kindError = ScheduleEditRules.ValidateCreateKind(kind);
                    if (kindError != null) return kindError;
                    string name = a.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name)) return "create requires a name.";
                    if (ScheduleNameTaken(doc, name))
                        return "a view named '" + name + "' already exists; an existing name is refused, never " +
                               "overwritten.";
                    if (ScheduleEditRules.KindNeedsCategory(kind))
                    {
                        string categoryText = a.Value<string>("category");
                        if (string.IsNullOrWhiteSpace(categoryText))
                            return "kind '" + kind + "' requires a category (OST_ token or display name).";
                        if (ResolveCategory(doc, categoryText) == null)
                            return "category '" + categoryText + "' was not found in the active document.";
                    }
                    return null;
                }
                if (op == ScheduleEditRules.OpDuplicate)
                {
                    ViewSchedule source = NeedSchedule(doc, a, "schedule_id");
                    if (!source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                        return "schedule " + Rid.Value(source.Id) + " cannot be duplicated (Revit's own answer).";
                    string name = a.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(name) && ScheduleNameTaken(doc, name))
                        return "a view named '" + name + "' already exists.";
                    return null;
                }

                // Every remaining operation edits ONE existing (or batch-created) schedule.
                string targetError = ValidateTarget(doc, a, knownKeys);
                if (targetError != null) return targetError;
                ViewSchedule target = TryTargetById(doc, a); // null when the target is a batch key

                switch (op)
                {
                    case ScheduleEditRules.OpRename:
                    {
                        string name = a.Value<string>("name");
                        if (string.IsNullOrWhiteSpace(name)) return "rename requires a name.";
                        if (ScheduleNameTaken(doc, name)) return "a view named '" + name + "' already exists.";
                        return null;
                    }
                    case ScheduleEditRules.OpAddFields:
                    {
                        JArray fields = a["fields"] as JArray;
                        if (fields == null || fields.Count == 0)
                            return "add_fields requires fields: an array of { parameter_id | name } entries.";
                        foreach (JToken t in fields)
                        {
                            var entry = t as JObject;
                            if (entry == null || (entry["parameter_id"] == null && entry["name"] == null))
                                return "each fields entry needs parameter_id or name.";
                        }
                        // Existence/ambiguity against the SCHEDULABLE fields is checked at
                        // apply for batch-created targets; for an id target it is checked
                        // here so the dry run refuses early.
                        if (target != null)
                        {
                            foreach (JToken t in fields)
                            {
                                var entry = (JObject)t;
                                string resolveError = ResolveSchedulable(target, entry.Value<long?>("parameter_id"),
                                                                          entry.Value<string>("name"), out _);
                                if (resolveError != null) return resolveError;
                            }
                        }
                        return null;
                    }
                    case ScheduleEditRules.OpRemoveFields:
                    case ScheduleEditRules.OpSetField:
                    {
                        JArray fields = op == ScheduleEditRules.OpRemoveFields ? a["fields"] as JArray
                                        : new JArray(a);
                        if (op == ScheduleEditRules.OpRemoveFields && (fields == null || fields.Count == 0))
                            return "remove_fields requires fields: an array of { parameter_id | name | field_index }.";
                        if (op == ScheduleEditRules.OpSetField &&
                            a["heading"] == null && a["hidden"] == null)
                            return "set_field changes nothing: pass heading and/or hidden.";
                        if (target != null)
                        {
                            List<ScheduleFieldFacts> facts = FieldFacts(target);
                            foreach (JToken t in fields)
                            {
                                var entry = t as JObject;
                                if (entry == null) return "field entries must be objects.";
                                if (entry["field_index"] != null) continue; // positional: checked at apply
                                ScheduleFieldFacts resolved;
                                string resolveError = ScheduleEditRules.ResolveField(facts,
                                    entry.Value<long?>("parameter_id"), entry.Value<string>("name"), out resolved);
                                if (resolveError != null) return resolveError;
                            }
                        }
                        return null;
                    }
                    case ScheduleEditRules.OpSetFilters:
                    {
                        JArray filters = a["filters"] as JArray;
                        if (filters == null)
                            return "set_filters requires filters (an array; empty CLEARS every filter - " +
                                   "declaring the whole list is what makes a replay idempotent).";
                        if (filters.Count > 30) return "filters is limited to 30 entries (Revit's own cap is lower).";
                        foreach (JToken t in filters)
                        {
                            var f = t as JObject;
                            if (f == null) return "filters entries must be objects.";
                            string shapeError = ScheduleEditRules.ValidateFilter(
                                (f.Value<string>("operator") ?? "").ToLowerInvariant(),
                                f["value"] != null, f["number_value"] != null);
                            if (shapeError != null) return shapeError;
                            if (f["parameter_id"] == null && f["field"] == null)
                                return "each filter names its field by parameter_id or field (name).";
                        }
                        return null;
                    }
                    case ScheduleEditRules.OpSetSorting:
                    {
                        JArray sorting = a["sorting"] as JArray;
                        if (sorting == null)
                            return "set_sorting requires sorting (an array; empty CLEARS all sort/group fields).";
                        foreach (JToken t in sorting)
                        {
                            var entry = t as JObject;
                            if (entry == null) return "sorting entries must be objects.";
                            string directionError = ScheduleEditRules.ValidateSortDirection(
                                (entry.Value<string>("direction") ?? "ascending").ToLowerInvariant());
                            if (directionError != null) return directionError;
                            if (entry["parameter_id"] == null && entry["field"] == null)
                                return "each sorting entry names its field by parameter_id or field (name).";
                        }
                        return null;
                    }
                    case ScheduleEditRules.OpSetOptions:
                        if (a["itemized"] == null && a["grand_total"] == null && a["headers"] == null)
                            return "set_options changes nothing: pass itemized, grand_total and/or headers.";
                        return null;
                    default:
                        unsupportedReason = FallbackSignal.ReasonUnsupportedOperation;
                        return "unsupported operation '" + op + "'.";
                }
            }
            catch (Exception ex)
            {
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return ex.Message;
            }
        }

        private static string ValidateTarget(Document doc, JObject a, HashSet<string> knownKeys)
        {
            string targetKey = a.Value<string>("schedule_key");
            if (!string.IsNullOrWhiteSpace(targetKey))
                return knownKeys.Contains(targetKey) ? null
                    : "schedule_key references unknown/prior key '" + targetKey + "'.";
            long id = a.Value<long?>("schedule_id") ?? -1;
            if (!Rid.CanRepresent(id) || !(doc.GetElement(Rid.Make(id)) is ViewSchedule))
                return "schedule_id must identify a ViewSchedule (or pass schedule_key for one created in this batch).";
            return null;
        }

        // =====================================================================
        // Apply
        // =====================================================================

        private static ViewSchedule Apply(Document doc, JObject a, Dictionary<string, ElementId> aliases,
                                          Dictionary<long, string> before)
        {
            string op = a.Value<string>("operation").ToLowerInvariant();
            if (op == ScheduleEditRules.OpCreate)
            {
                ViewSchedule created;
                switch ((a.Value<string>("kind") ?? "").ToLowerInvariant())
                {
                    case ScheduleEditRules.KindMaterialTakeoff:
                        created = ViewSchedule.CreateMaterialTakeoff(doc,
                            ResolveCategory(doc, a.Value<string>("category")).Id);
                        break;
                    case ScheduleEditRules.KindSheetList: created = ViewSchedule.CreateSheetList(doc); break;
                    case ScheduleEditRules.KindViewList: created = ViewSchedule.CreateViewList(doc); break;
                    case ScheduleEditRules.KindRevisionSchedule: created = ViewSchedule.CreateRevisionSchedule(doc); break;
                    default: created = ViewSchedule.CreateKeynoteLegend(doc); break;
                }
                created.Name = a.Value<string>("name");
                Snapshot(doc, created, before);
                return created;
            }
            if (op == ScheduleEditRules.OpDuplicate)
            {
                var source = (ViewSchedule)doc.GetElement(Rid.Make(a.Value<long>("schedule_id")));
                var copy = (ViewSchedule)doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate));
                if (!string.IsNullOrWhiteSpace(a.Value<string>("name"))) copy.Name = a.Value<string>("name");
                Snapshot(doc, copy, before);
                return copy;
            }

            ViewSchedule target = ResolveTarget(doc, a, aliases);
            Snapshot(doc, target, before);
            ScheduleDefinition definition = target.Definition;
            switch (op)
            {
                case ScheduleEditRules.OpRename:
                    target.Name = a.Value<string>("name");
                    return target;
                case ScheduleEditRules.OpAddFields:
                    foreach (JToken t in (JArray)a["fields"])
                    {
                        var entry = (JObject)t;
                        SchedulableField schedulable;
                        string error = ResolveSchedulable(target, entry.Value<long?>("parameter_id"),
                                                          entry.Value<string>("name"), out schedulable);
                        if (error != null) throw new InvalidOperationException(error);
                        definition.AddField(schedulable);
                    }
                    return target;
                case ScheduleEditRules.OpRemoveFields:
                    foreach (JToken t in (JArray)a["fields"])
                        definition.RemoveField(ResolveFieldId(target, (JObject)t));
                    return target;
                case ScheduleEditRules.OpSetField:
                {
                    ScheduleField field = definition.GetField(ResolveFieldId(target, a));
                    if (a["heading"] != null) field.ColumnHeading = a.Value<string>("heading");
                    if (a["hidden"] != null) field.IsHidden = a.Value<bool>("hidden");
                    return target;
                }
                case ScheduleEditRules.OpSetFilters:
                {
                    definition.ClearFilters();
                    foreach (JToken t in (JArray)a["filters"])
                        definition.AddFilter(BuildFilter(target, (JObject)t));
                    return target;
                }
                case ScheduleEditRules.OpSetSorting:
                {
                    definition.ClearSortGroupFields();
                    foreach (JToken t in (JArray)a["sorting"])
                    {
                        var entry = (JObject)t;
                        var sort = new ScheduleSortGroupField(ResolveFieldId(target, entry),
                            (entry.Value<string>("direction") ?? "ascending").ToLowerInvariant() == "descending"
                                ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending);
                        if (entry["header"] != null) sort.ShowHeader = entry.Value<bool>("header");
                        if (entry["footer"] != null) sort.ShowFooter = entry.Value<bool>("footer");
                        if (entry["blank_line"] != null) sort.ShowBlankLine = entry.Value<bool>("blank_line");
                        definition.AddSortGroupField(sort);
                    }
                    return target;
                }
                case ScheduleEditRules.OpSetOptions:
                    if (a["itemized"] != null) definition.IsItemized = a.Value<bool>("itemized");
                    if (a["grand_total"] != null) definition.ShowGrandTotal = a.Value<bool>("grand_total");
                    if (a["headers"] != null) definition.ShowHeaders = a.Value<bool>("headers");
                    return target;
                default:
                    throw new InvalidOperationException("unsupported operation '" + op + "'");
            }
        }

        // =====================================================================
        // Verification - every promise re-read
        // =====================================================================

        private static bool Verify(Document doc, Applied a, out string why)
        {
            why = null;
            var schedule = doc.GetElement(a.Id) as ViewSchedule;
            if (schedule == null) { why = "the schedule no longer exists"; return false; }
            string op = a.Action.Value<string>("operation").ToLowerInvariant();
            ScheduleDefinition definition = schedule.Definition;
            switch (op)
            {
                case ScheduleEditRules.OpCreate:
                {
                    string kind = (a.Action.Value<string>("kind") ?? "").ToLowerInvariant();
                    if (!string.Equals(SafeName(schedule), a.Action.Value<string>("name"), StringComparison.Ordinal))
                    { why = "the name did not stick"; return false; }
                    if (kind == ScheduleEditRules.KindMaterialTakeoff && !definition.IsMaterialTakeoff)
                    { why = "the created schedule is not a material takeoff"; return false; }
                    return true;
                }
                case ScheduleEditRules.OpDuplicate:
                {
                    string wanted = a.Action.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(wanted) &&
                        !string.Equals(SafeName(schedule), wanted, StringComparison.Ordinal))
                    { why = "the duplicate's name did not stick"; return false; }
                    return Rid.Value(schedule.Id) != a.Action.Value<long>("schedule_id");
                }
                case ScheduleEditRules.OpRename:
                    if (!string.Equals(SafeName(schedule), a.Action.Value<string>("name"), StringComparison.Ordinal))
                    { why = "the name did not stick"; return false; }
                    return true;
                case ScheduleEditRules.OpAddFields:
                {
                    List<ScheduleFieldFacts> facts = FieldFacts(schedule);
                    foreach (JToken t in (JArray)a.Action["fields"])
                    {
                        var entry = (JObject)t;
                        long? parameterId = entry.Value<long?>("parameter_id");
                        string name = entry.Value<string>("name");
                        bool present = parameterId != null
                            ? facts.Any(f => f.ParameterId == parameterId.Value)
                            : facts.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (!present) { why = "field " + (name ?? parameterId.ToString()) + " is not present"; return false; }
                    }
                    return true;
                }
                case ScheduleEditRules.OpRemoveFields:
                {
                    List<ScheduleFieldFacts> facts = FieldFacts(schedule);
                    foreach (JToken t in (JArray)a.Action["fields"])
                    {
                        var entry = (JObject)t;
                        long? parameterId = entry.Value<long?>("parameter_id");
                        string name = entry.Value<string>("name");
                        bool present = parameterId != null
                            ? facts.Any(f => f.ParameterId == parameterId.Value)
                            : name != null && facts.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (present) { why = "field " + (name ?? parameterId.ToString()) + " is still present"; return false; }
                    }
                    return true;
                }
                case ScheduleEditRules.OpSetField:
                {
                    ScheduleField field;
                    try { field = definition.GetField(ResolveFieldId(schedule, a.Action)); }
                    catch (Exception ex) { why = "the field could not be re-read: " + ex.Message; return false; }
                    if (a.Action["heading"] != null &&
                        !string.Equals(field.ColumnHeading, a.Action.Value<string>("heading"), StringComparison.Ordinal))
                    { why = "the heading did not stick"; return false; }
                    if (a.Action["hidden"] != null && field.IsHidden != a.Action.Value<bool>("hidden"))
                    { why = "the visibility did not stick"; return false; }
                    return true;
                }
                case ScheduleEditRules.OpSetFilters:
                {
                    JArray wanted = (JArray)a.Action["filters"];
                    IList<ScheduleFilter> got;
                    try { got = definition.GetFilters(); }
                    catch (Exception ex) { why = "filters could not be re-read: " + ex.Message; return false; }
                    if (got.Count != wanted.Count)
                    { why = "filter count is " + got.Count + ", wanted " + wanted.Count; return false; }
                    for (int i = 0; i < wanted.Count; i++)
                    {
                        var entry = (JObject)wanted[i];
                        ScheduleFilter filter = got[i];
                        if (filter.FilterType != MapFilterType((entry.Value<string>("operator") ?? "").ToLowerInvariant()))
                        { why = "filter " + i + " has type " + filter.FilterType; return false; }
                        if (entry["value"] != null)
                        {
                            string text; try { text = filter.GetStringValue(); } catch { text = null; }
                            if (!string.Equals(text, entry.Value<string>("value"), StringComparison.Ordinal))
                            { why = "filter " + i + "'s text value did not stick"; return false; }
                        }
                        if (entry["number_value"] != null)
                        {
                            double got2; try { got2 = filter.GetDoubleValue(); }
                            catch { try { got2 = filter.GetIntegerValue(); } catch { why = "filter " + i + "'s number could not be re-read"; return false; } }
                            if (Math.Abs(got2 - entry.Value<double>("number_value")) > 1e-9)
                            { why = "filter " + i + "'s number value did not stick"; return false; }
                        }
                    }
                    return true;
                }
                case ScheduleEditRules.OpSetSorting:
                {
                    JArray wanted = (JArray)a.Action["sorting"];
                    IList<ScheduleSortGroupField> got;
                    try { got = definition.GetSortGroupFields(); }
                    catch (Exception ex) { why = "sorting could not be re-read: " + ex.Message; return false; }
                    if (got.Count != wanted.Count)
                    { why = "sort/group count is " + got.Count + ", wanted " + wanted.Count; return false; }
                    for (int i = 0; i < wanted.Count; i++)
                    {
                        var entry = (JObject)wanted[i];
                        ScheduleSortGroupField sort = got[i];
                        bool descending = (entry.Value<string>("direction") ?? "ascending").ToLowerInvariant() == "descending";
                        if ((sort.SortOrder == ScheduleSortOrder.Descending) != descending)
                        { why = "sorting " + i + "'s direction did not stick"; return false; }
                        if (entry["header"] != null && sort.ShowHeader != entry.Value<bool>("header"))
                        { why = "sorting " + i + "'s header flag did not stick"; return false; }
                        if (entry["footer"] != null && sort.ShowFooter != entry.Value<bool>("footer"))
                        { why = "sorting " + i + "'s footer flag did not stick"; return false; }
                        if (entry["blank_line"] != null && sort.ShowBlankLine != entry.Value<bool>("blank_line"))
                        { why = "sorting " + i + "'s blank-line flag did not stick"; return false; }
                    }
                    return true;
                }
                case ScheduleEditRules.OpSetOptions:
                    if (a.Action["itemized"] != null && definition.IsItemized != a.Action.Value<bool>("itemized"))
                    { why = "itemized did not stick"; return false; }
                    if (a.Action["grand_total"] != null && definition.ShowGrandTotal != a.Action.Value<bool>("grand_total"))
                    { why = "grand_total did not stick"; return false; }
                    if (a.Action["headers"] != null && definition.ShowHeaders != a.Action.Value<bool>("headers"))
                    { why = "headers did not stick"; return false; }
                    return true;
                default:
                    why = "no verifier for '" + op + "'";
                    return false;
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static ViewSchedule ResolveTarget(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            string key = a.Value<string>("schedule_key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                ElementId id;
                if (!aliases.TryGetValue(key, out id) || !(doc.GetElement(id) is ViewSchedule byKey))
                    throw new InvalidOperationException("schedule_key '" + key + "' did not resolve to a schedule");
                return byKey;
            }
            return (ViewSchedule)doc.GetElement(Rid.Make(a.Value<long>("schedule_id")));
        }

        private static ViewSchedule TryTargetById(Document doc, JObject a)
        {
            long id = a?.Value<long?>("schedule_id") ?? -1;
            if (!Rid.CanRepresent(id)) return null;
            return doc.GetElement(Rid.Make(id)) as ViewSchedule;
        }

        private static ViewSchedule NeedSchedule(Document doc, JObject a, string field)
        {
            long id = a.Value<long?>(field) ?? -1;
            if (!Rid.CanRepresent(id) || !(doc.GetElement(Rid.Make(id)) is ViewSchedule schedule))
                throw new ArgumentException(field + " must identify a ViewSchedule");
            return schedule;
        }

        private static List<ScheduleFieldFacts> FieldFacts(ViewSchedule schedule)
        {
            var result = new List<ScheduleFieldFacts>();
            ScheduleDefinition definition = schedule.Definition;
            IList<ScheduleFieldId> order = definition.GetFieldOrder();
            for (int i = 0; i < order.Count; i++)
            {
                ScheduleField field = definition.GetField(order[i]);
                string name; try { name = field.GetName(); } catch { name = null; }
                string heading; try { heading = field.ColumnHeading; } catch { heading = null; }
                result.Add(new ScheduleFieldFacts
                {
                    Index = i,
                    ParameterId = Rid.Value(field.ParameterId),
                    Name = name,
                    Heading = heading,
                    Hidden = field.IsHidden,
                    FieldType = field.FieldType.ToString()
                });
            }
            return result;
        }

        /// <summary>
        /// A field reference on an EXISTING schedule, as a ScheduleFieldId. field_index
        /// addresses by position (the escape hatch the ambiguity refusal points at when
        /// even parameter ids collide); parameter_id and name go through the proved
        /// resolution rules.
        /// </summary>
        private static ScheduleFieldId ResolveFieldId(ViewSchedule schedule, JObject entry)
        {
            ScheduleDefinition definition = schedule.Definition;
            IList<ScheduleFieldId> order = definition.GetFieldOrder();
            int? index = entry.Value<int?>("field_index");
            if (index != null)
            {
                if (index.Value < 0 || index.Value >= order.Count)
                    throw new InvalidOperationException("field_index " + index.Value + " is out of range; the " +
                                                        "schedule has " + order.Count + " fields.");
                return order[index.Value];
            }
            ScheduleFieldFacts resolved;
            string error = ScheduleEditRules.ResolveField(FieldFacts(schedule), entry.Value<long?>("parameter_id"),
                                                          entry.Value<string>("name"), out resolved);
            if (error != null) throw new InvalidOperationException(error);
            return order[resolved.Index];
        }

        /// <summary>
        /// One caller-named SCHEDULABLE field (a field that could be added), resolved
        /// with the same ambiguity discipline as existing fields.
        /// </summary>
        private static string ResolveSchedulable(ViewSchedule schedule, long? parameterId, string name,
                                                 out SchedulableField resolved)
        {
            resolved = null;
            IList<SchedulableField> all = schedule.Definition.GetSchedulableFields();
            var matches = new List<SchedulableField>();
            foreach (SchedulableField candidate in all)
            {
                if (parameterId.HasValue)
                {
                    if (Rid.Value(candidate.ParameterId) == parameterId.Value) matches.Add(candidate);
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    string candidateName;
                    try { candidateName = candidate.GetName(schedule.Document); } catch { candidateName = null; }
                    if (string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
                }
            }
            if (matches.Count == 1) { resolved = matches[0]; return null; }
            string wanted = parameterId.HasValue ? "parameter id " + parameterId.Value : "'" + name + "'";
            if (matches.Count == 0)
                return "no schedulable field of this schedule matches " + wanted + ". horizun_get_schedule_data " +
                       "shows the current fields; the schedulable set is what Revit offers in the Fields dialog.";
            return wanted + " matches " + matches.Count + " schedulable fields (parameter ids " +
                   string.Join(", ", matches.Select(m => Rid.Value(m.ParameterId))) + "); resolve by parameter_id.";
        }

        private static ScheduleFilter BuildFilter(ViewSchedule schedule, JObject entry)
        {
            ScheduleFieldId fieldId = ResolveFieldId(schedule, FilterFieldRef(entry));
            ScheduleFilterType type = MapFilterType((entry.Value<string>("operator") ?? "").ToLowerInvariant());
            if (entry["value"] != null) return new ScheduleFilter(fieldId, type, entry.Value<string>("value"));
            if (entry["number_value"] != null)
            {
                double number = entry.Value<double>("number_value");
                // An integral value may target an integer parameter; Revit's typed
                // constructors care. Try double first - the common case for lengths -
                // and fall back to the integer form when Revit refuses the shape.
                try { return new ScheduleFilter(fieldId, type, number); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) when (number == Math.Floor(number))
                { return new ScheduleFilter(fieldId, type, (int)number); }
            }
            return new ScheduleFilter(fieldId, type);
        }

        private static JObject FilterFieldRef(JObject entry) => new JObject
        {
            ["parameter_id"] = entry["parameter_id"]?.DeepClone(),
            ["name"] = entry["field"]?.DeepClone()
        };

        private static ScheduleFilterType MapFilterType(string op)
        {
            switch (op)
            {
                case "equal": return ScheduleFilterType.Equal;
                case "not_equal": return ScheduleFilterType.NotEqual;
                case "greater_than": return ScheduleFilterType.GreaterThan;
                case "greater_than_or_equal": return ScheduleFilterType.GreaterThanOrEqual;
                case "less_than": return ScheduleFilterType.LessThan;
                case "less_than_or_equal": return ScheduleFilterType.LessThanOrEqual;
                case "contains": return ScheduleFilterType.Contains;
                case "not_contains": return ScheduleFilterType.NotContains;
                case "begins_with": return ScheduleFilterType.BeginsWith;
                case "not_begins_with": return ScheduleFilterType.NotBeginsWith;
                case "ends_with": return ScheduleFilterType.EndsWith;
                case "not_ends_with": return ScheduleFilterType.NotEndsWith;
                case "has_value": return ScheduleFilterType.HasValue;
                case "has_no_value": return ScheduleFilterType.HasNoValue;
                default: throw new InvalidOperationException("unmapped filter operator '" + op + "'");
            }
        }

        private static string Canonical(Document doc, ViewSchedule schedule)
        {
            ScheduleDefinition definition = schedule.Definition;
            var filterLines = new List<string>();
            try
            {
                foreach (ScheduleFilter filter in definition.GetFilters())
                {
                    string value = "";
                    try { value = filter.GetStringValue(); } catch { }
                    if (string.IsNullOrEmpty(value))
                        try { value = filter.GetDoubleValue().ToString(CultureInfo.InvariantCulture); } catch { }
                    if (string.IsNullOrEmpty(value))
                        try { value = filter.GetIntegerValue().ToString(CultureInfo.InvariantCulture); } catch { }
                    filterLines.Add(Rid.Value(filter.FieldId != null ? definition.GetField(filter.FieldId).ParameterId
                                              : ElementId.InvalidElementId) + " " + filter.FilterType + " " + value);
                }
            }
            catch { filterLines.Add("(unreadable)"); }
            var sortLines = new List<string>();
            try
            {
                foreach (ScheduleSortGroupField sort in definition.GetSortGroupFields())
                    sortLines.Add(Rid.Value(definition.GetField(sort.FieldId).ParameterId) + " " + sort.SortOrder +
                                  " header=" + sort.ShowHeader + " footer=" + sort.ShowFooter +
                                  " blank=" + sort.ShowBlankLine);
            }
            catch { sortLines.Add("(unreadable)"); }
            return ScheduleEditRules.CanonicalDefinition(FieldFacts(schedule), filterLines, sortLines,
                                                         definition.IsItemized, definition.ShowGrandTotal,
                                                         definition.ShowHeaders);
        }

        private static JObject CurrentDefinitions(Document doc, JArray input)
        {
            var result = new JObject();
            foreach (JToken t in input)
            {
                var a = t as JObject;
                ViewSchedule target = TryTargetById(doc, a);
                if (target == null) continue;
                string idText = Rid.Value(target.Id).ToString(CultureInfo.InvariantCulture);
                if (result[idText] != null) continue;
                string canonical = Canonical(doc, target);
                result[idText] = new JObject
                {
                    ["name"] = SafeName(target),
                    ["definition_fingerprint"] = ScheduleEditRules.DefinitionFingerprint(canonical),
                    ["definition"] = canonical
                };
            }
            return result;
        }

        private static void Snapshot(Document doc, ViewSchedule schedule, Dictionary<long, string> before)
        {
            long id = Rid.Value(schedule.Id);
            if (!before.ContainsKey(id))
                try { before[id] = Canonical(doc, schedule); } catch { before[id] = null; }
        }

        private static bool ScheduleNameTaken(Document doc, string name)
        {
            foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                try { if (!view.IsTemplate && string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            return false;
        }

        private static Category ResolveCategory(Document doc, string text)
        {
            BuiltInCategory bic;
            if (Enum.TryParse(text, true, out bic))
                try { Category byToken = Category.GetCategory(doc, bic); if (byToken != null) return byToken; } catch { }
            try
            {
                foreach (Category category in doc.Settings.Categories)
                    if (string.Equals(category.Name, text, StringComparison.OrdinalIgnoreCase)) return category;
            }
            catch { }
            return null;
        }

        private static string SafeUid(Element e) { try { return e.UniqueId; } catch { return null; } }
        private static string SafeName(Element e) { try { return e.Name; } catch { return null; } }

        private sealed class Applied { public int Index; public JObject Action; public ElementId Id; }
    }
}
