// -----------------------------------------------------------------------------
// Horizun Revit MCP — create a native Revit schedule, including linked models.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class CreateScheduleCommand : ICommand
    {
        public string Name => "horizun_create_schedule";

        public string Description =>
            "Create one native Revit schedule for one category, optionally including elements from loaded RVT links. " +
            "Dry-run is the default. The apply pass adds the requested fields by their Revit display names; when " +
            "itemized=false it groups by the IDENTITY fields only (never by a length, area, volume, count or other " +
            "number) and totals the quantity fields, or groups exactly by group_by when given. It commits once, then " +
            "re-reads the schedule, its fields, sort/group fields, totals, IncludeLinkedFiles flag and body row count. " +
            "A category with zero host elements is valid: linked elements are exactly why this command exists.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string categoryText = request.Value<string>("category");
            string scheduleName = request.Value<string>("name");
            if (string.IsNullOrWhiteSpace(categoryText)) return CommandResult.Fail("category is required.");
            if (string.IsNullOrWhiteSpace(scheduleName)) return CommandResult.Fail("name is required.");

            // MULTI-CATEGORY and KEY schedules. Field use forced 19 separate schedules
            // because this command took one category only; the API takes
            // InvalidElementId for multi-category on every year 2023-2027 (there is no
            // OST_MultiCategory in BuiltInCategory - the token is accepted here as the
            // name people use for it). A key schedule is CreateKeySchedule over one
            // category and its rows are KEY ELEMENTS, re-read after the commit.
            bool multi = string.Equals(categoryText, "OST_MultiCategory", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(categoryText, "multi_category", StringComparison.OrdinalIgnoreCase);
            bool key = request.Value<bool?>("key_schedule") ?? false;
            int keyRows = request.Value<int?>("key_rows") ?? 0;
            if (multi && key) return CommandResult.Fail("A key schedule belongs to ONE category; it cannot be multi-category. Nothing was changed.");
            if (!key && request["key_rows"] != null) return CommandResult.Fail("key_rows applies to key_schedule=true only. Nothing was changed.");
            if (keyRows < 0 || keyRows > 500) return CommandResult.Fail("key_rows must be 0..500.");
            if (key && request.Value<bool?>("include_links") == true)
                return CommandResult.Fail("A key schedule lists key elements of this document; include_links does not apply. Nothing was changed.");
            if (key && request["itemized"] != null)
                return CommandResult.Fail("A key schedule is always one row per key; itemized does not apply. Nothing was changed.");
            if (key && request["group_by"] != null)
                return CommandResult.Fail("A key schedule is always one row per key; group_by does not apply. Nothing was changed.");
            if (request["group_by"] != null && !(request["group_by"] is JArray groupByArray && groupByArray.All(t => t.Type == JTokenType.String)))
                return CommandResult.Fail("group_by must be an array of field names (strings). Nothing was changed.");

            Category category = multi ? null : ResolveCategory(doc, categoryText);
            if (category == null && !multi)
                return CommandResult.Fail("Category '" + categoryText + "' was not found. Use a BuiltInCategory token such as OST_Walls, OST_MultiCategory, or the category display name.");
            ElementId categoryId = multi ? ElementId.InvalidElementId : category.Id;
            string categoryName = multi ? "Multi-Category" : category.Name;

            if (key ? !ViewSchedule.IsValidCategoryForKeySchedule(categoryId)
                    : !multi && !ViewSchedule.GetValidCategoriesForSchedule().Any(id => id == categoryId))
                return CommandResult.Fail("Category '" + categoryName + "' is not valid for a " + (key ? "key" : "regular") + " Revit schedule.");

            if (new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Any(v => string.Equals(v.Name, scheduleName, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail("A schedule named '" + scheduleName + "' already exists; nothing was changed.");

            bool includeLinks = !key && (request["include_links"] == null || request.Value<bool>("include_links"));
            bool itemized = request.Value<bool?>("itemized") ?? false;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string kind = key ? "key" : multi ? "multi_category" : "regular";
            List<string> requestedFields = ReadFields(request["fields"] as JArray);
            if (requestedFields.Count == 0 && !key)
                requestedFields.AddRange(new[] { "Count", "Family", "Type" });
            // group_by is honoured EXACTLY when given (an empty array = group by nothing);
            // absent, the grouping is derived from what each field IS - see
            // ScheduleGroupingRules. Checked against the request now, so the rehearsal
            // refuses exactly what the apply would.
            List<string> groupBy = request["group_by"] == null ? null : ReadFields(request["group_by"] as JArray);
            string groupByError = ScheduleGroupingRules.ValidateGroupBy(requestedFields, groupBy);
            if (groupByError != null) return CommandResult.Fail(groupByError);

            string planHash = DocumentGate.PlanHash(request, "category", "name", "fields", "include_links", "itemized", "key_schedule", "key_rows", "group_by");

            // ---- WHICH FIELDS the schedule will carry, decided now - in the rehearsal AND again in the
            // apply - by creating the schedule inside a transaction that is ALWAYS rolled back and asking
            // Revit which fields it offers. The rehearsal shows what will be substituted, added once or
            // left out, and that decision is bound into the token (ScheduleFieldPlan).
            //
            // Before that transaction: what does not depend on the plan. A document that cannot start a
            // transaction is said for what it is, and an apply whose token is missing, spent, expired or
            // unknown - or whose document moved - is refused HERE, so no schedule is created and rolled
            // back on behalf of an approval that cannot be spent. The plan itself is compared later, by
            // RequireConfirmation, against the plan this call resolves. The order is ScheduleFieldProbe.BeforeProbe.
            bool docReadOnly = false, docModifiable = false;
            try { docReadOnly = doc.IsReadOnly; } catch (Exception) { }
            try { docModifiable = doc.IsModifiable; } catch (Exception) { }
            bool tokenRefused;
            string beforeProbe = ScheduleFieldProbe.BeforeProbe(docReadOnly, docModifiable, categoryName, dryRun,
                () => ApplyTokenRefusal(gate, request), out tokenRefused);
            if (beforeProbe != null)
                return !tokenRefused
                    ? CommandResult.Fail(beforeProbe)
                    : CommandResult.FailWithDetail(beforeProbe, new JObject
                    {
                        ["error_code"] = string.IsNullOrWhiteSpace(request.Value<string>("confirmation_token"))
                            ? ErrorCodes.ConfirmacionRequerida : ErrorCodes.TokenInvalidoOVencido,
                        ["state"] = "refused",
                        ["checked_before"] = "field_probe"
                    });
            if (!dryRun)
            {
                CommandResult moved = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
                if (moved != null) return moved;
            }

            ScheduleFieldPlan fieldPlan;
            List<string> offered;
            var probeProgress = new ProbeProgress();
            try { fieldPlan = ResolveFieldPlan(doc, categoryId, key, includeLinks, itemized, requestedFields, probeProgress, out offered); }
            catch (Exception ex)
            {
                // The message says how far the probe got: "created and rolled back" only when it was.
                return ScheduleFieldProbe.FailureResult(probeProgress.Stage, categoryName, ex.Message);
            }
            // ---- The MATERIALISED plan. One creation, but two ambient facts decide what
            // it produces, and neither is in the request: WHICH category the name resolved
            // to (a localized display name is a lookup, not an identity), and the
            // name-collision check - "no schedule called this exists" is a fact about the
            // model NOW, and if somebody creates one in between, the polite refusal above
            // must win over a race. The elevation of the whole check into the plan means
            // the apply re-runs both resolutions and refuses on drift.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolvedPlan.Elements.Add(new PlannedElement
            {
                UniqueId = "schedule:" + scheduleName,
                Category = multi ? categoryName : SafePlanCatName(category),
                Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                {
                    { "category_id", Rid.Value(categoryId).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "kind", kind }, { "key_rows", keyRows.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "fields", string.Join(",", requestedFields) },
                    { "include_links", includeLinks ? "1" : "0" },
                    { "itemized", itemized ? "1" : "0" },
                    { "group_by", groupBy == null ? "<derived>" : string.Join(",", groupBy) },
                    // What Revit offers decides what is added: a change there between rehearsal and apply is drift.
                    { "fields_resolved", fieldPlan.Fingerprint() }
                }
            });

            // The outer plan checked this before opening its group. An earlier action
            // can since have changed the available fields. The probe has rolled back;
            // compare its result before starting the actual schedule transaction.
            if (!dryRun && DocumentGate.InConfirmedAtomicPlan)
            {
                CommandResult drift = resolvedPlan.RefuseChangedChild(
                    request.Value<string>(PlanLedger.ExpectedChildPlanField), "schedule creation");
                if (drift != null) return drift;
            }
            if (fieldPlan.NothingResolved)
                return CommandResult.Fail(ScheduleFieldRescue.NothingResolved(categoryName, requestedFields, offered));
            string planGroupByError = fieldPlan.GroupByProblem(categoryName, groupBy);
            if (planGroupByError != null) return CommandResult.Fail(planGroupByError);
            List<JObject> assumptions = FieldPlanAssumptions(fieldPlan, categoryName, offered);
            List<string> omitted = fieldPlan.OmittedNames;

            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["dry_run"] = true,
                    ["category"] = categoryName,
                    ["category_id"] = Rid.Value(categoryId),
                    ["kind"] = kind,
                    ["key_rows"] = keyRows,
                    ["name"] = scheduleName,
                    ["fields_requested"] = new JArray(requestedFields),
                    ["include_links"] = includeLinks,
                    ["itemized"] = itemized,
                    ["group_by"] = key ? JValue.CreateNull()
                        : groupBy != null ? new JArray(groupBy)
                        : (JToken)(itemized ? "none (itemized)" : "derived after creation: identity fields only; quantity fields are totalled"),
                    ["host_element_count"] = multi ? (JToken)JValue.CreateNull() : new FilteredElementCollector(doc).OfCategoryId(categoryId)
                        .WhereElementIsNotElementType().GetElementCount(),
                    // Resolved against the fields Revit offers (a schedule created and rolled back to ask).
                    ["field_plan"] = fieldPlan.ToJson(),
                    ["fields_to_add"] = new JArray(fieldPlan.ToAdd.Select(e => (JToken)e.Requested)),
                    ["fields_omitted"] = new JArray(omitted),
                    ["fields_substituted"] = new JArray(fieldPlan.SubstitutedPairs.Select(p => new JObject { ["requested"] = p.Key, ["used"] = p.Value })),
                    ["note"] = "The apply transaction has not started. The separate field probe created a temporary schedule, " +
                               "rolled back and checked that the schedule was gone. If fields_omitted has entries, the apply " +
                               "leaves them out and reports a partial result. Linked rows are verified after creation."
                };
                AssumptionNotes.Stamp(rehearsal, assumptions);
                fieldPlan.StampRehearsal(rehearsal);
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(rehearsal, gate, Name, planHash, true,
                    "the token is bound to this category AS RESOLVED (not just the text you typed), the schedule " +
                    "name, field list AS RESOLVED (substituted, added once, left out) and link/itemization settings. " +
                    "A category text or a field that starts resolving differently refuses as a stale plan.");
                return CommandResult.Ok(rehearsal);
            }

            // Recomputed by THIS call - including the name-collision refusal above, which
            // ran again before this line and wins over any race.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                    resolvedPlan, null);
            if (refusal != null) return refusal;

            ElementId createdId = ElementId.InvalidElementId;
            // The status the commit RETURNED. Revit's Transaction.Commit() answers
            // RolledBack or Pending without throwing - that is the whole reason Guard
            // exists - so this is read and carried, never assumed. Uninitialized is the
            // value that means "no commit has been read yet", and it is the one state that
            // must never survive to a declaration.
            TransactionStatus commitStatus = TransactionStatus.Uninitialized;
            Exception applyFailure = null;
            string rollbackStatus = "unknown";
            string writeStage = "transaction_apply";
            var addedNames = new List<string>();
            var expectedFields = new List<FieldIdentity>();
            ScheduleGroupingPlan grouping = null;
            var groupingFacts = new List<ScheduleGroupingField>();
            var expectedSortIds = new List<int>();
            var expectedTotalIds = new List<int>();
            using (var tx = new Transaction(doc, "Horizun: create schedule"))
            {
                TransactionStatus started = tx.Start();
                if (started != TransactionStatus.Started)
                    return CommandResult.Fail("The transaction to create the schedule did not start (Revit answered " + started +
                        "). No schedule was created. Nothing was changed.");
                try
                {
                    ViewSchedule schedule = key ? ViewSchedule.CreateKeySchedule(doc, categoryId)
                                                : ViewSchedule.CreateSchedule(doc, categoryId);
                    createdId = schedule.Id;
                    schedule.Name = scheduleName;
                    ScheduleDefinition definition = schedule.Definition;
                    if (!key)
                    {
                        definition.IncludeLinkedFiles = includeLinks;
                        definition.IsItemized = itemized;
                    }
                    // Revit adds fields of its own to some kinds (a key schedule's Key
                    // Name): they are part of what the re-read must find, in order.
                    foreach (ScheduleFieldId id in definition.GetFieldOrder())
                        expectedFields.Add(FieldIdentity.From(doc, definition.GetField(id)));

                    var available = definition.GetSchedulableFields().ToList();

                    var added = new List<ScheduleField>();
                    // Exactly the columns the confirmed plan adds, once each, in request order. Each is
                    // resolved again here and must land on the identity the rehearsal found.
                    foreach (ScheduleFieldPlanEntry entry in fieldPlan.ToAdd)
                    {
                        SchedulableField schedulable = ResolveField(doc, available, entry.Used);
                        string now = schedulable == null ? null : IdentityOf(doc, schedulable);
                        if (!string.Equals(now, entry.Identity, StringComparison.Ordinal))
                        {
                            writeStage = "field_identity";
                            throw new InvalidOperationException("Field '" + entry.Requested + "' resolved to " + (entry.Identity ?? "nothing") +
                                " when the plan was confirmed and to " + (now ?? "nothing") + " now. Rehearse again.");
                        }
                        ScheduleField field = definition.AddField(schedulable);
                        added.Add(field);
                        addedNames.Add(entry.Requested);
                        expectedFields.Add(FieldIdentity.From(doc, field));
                    }

                    if (key)
                    {
                        // A key schedule's rows ARE key elements; inserting a body row
                        // creates one. CanInsertRow is asked first so a refusal names
                        // the row rather than surfacing as an opaque API exception.
                        TableSectionData body = schedule.GetTableData().GetSectionData(SectionType.Body);
                        for (int r = 0; r < keyRows; r++)
                        {
                            int at = body.FirstRowNumber;
                            if (!body.CanInsertRow(at))
                                throw new InvalidOperationException("Revit refused key row " + (r + 1) + " (TableSectionData.CanInsertRow is false).");
                            body.InsertRow(at);
                        }
                    }
                    else
                    {
                        writeStage = "grouping";
                        // GROUP BY WHAT A FIELD IS, NOT BY WHERE IT SITS IN THE LIST. Grouping
                        // by every non-Count field put Length/Area/Volume in the sort: 119
                        // rows instead of 8 on a real model. Revit says what each field is;
                        // ScheduleGroupingRules decides.
                        for (int i = 0; i < added.Count; i++)
                            groupingFacts.Add(GroupingFacts(addedNames[i], added[i]));
                        grouping = ScheduleGroupingRules.Plan(groupingFacts, itemized, groupBy);
                        if (grouping.Error != null)
                            throw new InvalidOperationException(grouping.Error);
                        foreach (int i in grouping.SortGroup)
                        {
                            definition.AddSortGroupField(new ScheduleSortGroupField(added[i].FieldId));
                            expectedSortIds.Add(added[i].FieldId.IntegerValue);
                        }
                        foreach (int i in grouping.Totals)
                        {
                            added[i].DisplayType = ScheduleFieldDisplayType.Totals;
                            expectedTotalIds.Add(added[i].FieldId.IntegerValue);
                        }
                    }

                    // Guard refuses RolledBack and Pending. Only after it returns can this
                    // command enter a success/postcondition path, so the declaration never
                    // treats an in-flight or silently rolled-back transaction as applied.
                    writeStage = "transaction_commit";
                    try { commitStatus = Guard.Commit(tx, "create schedule"); }
                    catch (SilentRollbackException ex) { commitStatus = ex.Status; }
                }
                catch (Exception original)
                {
                    applyFailure = original;
                    try { rollbackStatus = RollBackKeeping(tx, original).StatusName; }
                    catch (Exception rollbackFailure) { applyFailure = rollbackFailure; }
                }
            }

            if (applyFailure != null)
                return ScheduleFieldPlan.RollbackFailure(applyFailure.Message, writeStage, ScheduleId(createdId),
                    rollbackStatus, ReadSchedulePresence(doc, createdId), assumptions);

            if (commitStatus != TransactionStatus.Committed)
            {
                ApplicationState state = commitStatus == TransactionStatus.RolledBack
                    ? ApplicationState.RolledBack
                    : ApplicationState.Uncertain;
                return ScheduleFieldPlan.WriteFailure(
                    "The schedule commit returned " + commitStatus + ". " +
                    (state == ApplicationState.RolledBack
                        ? "Revit confirmed rollback."
                        : "The model state is uncertain."),
                    ScheduleId(createdId), commitStatus.ToString(), state, "transaction_commit", false, null, assumptions);
            }

            ViewSchedule verified;
            try { verified = doc.GetElement(createdId) as ViewSchedule; }
            catch (Exception ex)
            {
                return ScheduleFieldPlan.WriteFailure(
                    "The transaction committed, but the schedule could not be re-read: " + ex.Message,
                    ScheduleId(createdId), commitStatus.ToString(), ApplicationState.Uncertain,
                    "schedule_reread", false, null, assumptions);
            }
            if (verified == null)
                return ScheduleFieldPlan.WriteFailure(
                    "The transaction committed but the schedule could not be re-read from the model.",
                    ScheduleId(createdId), commitStatus.ToString(), ApplicationState.Uncertain,
                    "schedule_reread", false, null, assumptions);

            // ---- EVERY property the request named, re-read from the COMMITTED schedule.
            //
            // The three that used to be checked here were fields, include_links and
            // itemized. The request also carries a NAME and a CATEGORY, and neither was
            // ever re-read - the reply reported `category` off the Category object resolved
            // before the commit, which is the request talking back rather than the model.
            // A schedule that committed under a different name, or against a category Revit
            // resolved differently, was reported as fully verified.
            //
            // Nothing below is compared against a value kept from before the commit: every
            // `found` side comes off `verified`, which is a fresh GetElement of the id.
            // The five properties this request carries, named ONCE, here. A check deleted
            // from below, or one key recorded twice while another is dropped, now fails
            // coverage instead of passing on the checks that happen to remain.
            // A key schedule carries neither links nor itemization; it carries its KIND
            // and its key rows instead. Regular and multi-category keep the five.
            var postcondition = key
                ? new PostconditionCheck("name", "category", "fields", "kind", "key_rows")
                : new PostconditionCheck("name", "category", "fields",
                                         "include_links", "itemized", "sort_group", "totals");

            // (a) NAME.
            try { postcondition.Compare("name", scheduleName, verified.Name); }
            catch (Exception ex) { postcondition.Unreadable("name", scheduleName, "the committed schedule's name could not be read: " + ex.Message); }

            // (b) CATEGORY, as the committed definition reports it - not as the text
            // resolved before the commit. Compared by id, because two categories can share
            // a display name across disciplines.
            try
            {
                ElementId actualCategoryId = verified.Definition.CategoryId;
                postcondition.Record("category",
                    new JObject { ["id"] = Rid.Value(categoryId), ["name"] = categoryName },
                    new JObject { ["id"] = Rid.Value(actualCategoryId), ["name"] = actualCategoryId == ElementId.InvalidElementId ? "Multi-Category" : SafePlanCatName(Category.GetCategory(doc, actualCategoryId)) },
                    multi ? actualCategoryId == ElementId.InvalidElementId : actualCategoryId == category.Id);
            }
            catch (Exception ex)
            {
                postcondition.Unreadable("category", Rid.Value(categoryId),
                    "the committed schedule's category could not be read: " + ex.Message);
            }

            // (c) FIELDS, in order and by identity.
            List<ScheduleField> actualFields;
            JArray verifiedFields;
            try
            {
                actualFields = verified.Definition.GetFieldOrder()
                    .Select(id => verified.Definition.GetField(id)).ToList();
                verifiedFields = new JArray(actualFields.Select(field => (JToken)field.GetName()));
                bool fieldsMatch = actualFields.Count == expectedFields.Count &&
                    actualFields.Select(f => FieldIdentity.From(doc, f)).SequenceEqual(expectedFields);
                // Requested vs found: the omitted names are named here, and the declaration below
                // counts them as unresolved, so a schedule missing a requested column is never "verified".
                postcondition.Record("fields", new JObject
                {
                    ["requested"] = new JArray(requestedFields),
                    ["added"] = new JArray(addedNames),
                    ["omitted"] = new JArray(omitted),
                    ["not_added_twice"] = new JArray(fieldPlan.Duplicates.Select(d => (JToken)d.Requested))
                }, verifiedFields, fieldsMatch);
            }
            catch (Exception ex)
            {
                actualFields = new List<ScheduleField>();
                verifiedFields = new JArray();
                postcondition.Unreadable("fields", new JArray(requestedFields.Select(f => (JToken)f)),
                                         "the committed schedule's fields could not be read: " + ex.Message);
            }

            if (key)
            {
                // (d) KIND, read off the committed definition. Multi-category needs no
                // separate kind: it IS the invalid category id compared above.
                try { postcondition.Compare("kind", kind, verified.Definition.IsKeySchedule ? "key" : "regular"); }
                catch (Exception ex) { postcondition.Unreadable("kind", kind, "could not be read: " + ex.Message); }

                // KEY ROWS as key ELEMENTS owned by the schedule, counted after the commit.
                try { postcondition.Compare("key_rows", keyRows, new FilteredElementCollector(doc, verified.Id).GetElementCount()); }
                catch (Exception ex) { postcondition.Unreadable("key_rows", keyRows, "could not be read: " + ex.Message); }
            }
            else
            {
                // (e) INCLUDE_LINKS and (f) ITEMIZED.
                try { postcondition.Compare("include_links", includeLinks, verified.Definition.IncludeLinkedFiles); }
                catch (Exception ex) { postcondition.Unreadable("include_links", includeLinks, "could not be read: " + ex.Message); }

                try { postcondition.Compare("itemized", itemized, verified.Definition.IsItemized); }
                catch (Exception ex) { postcondition.Unreadable("itemized", itemized, "could not be read: " + ex.Message); }

                // (g) SORT/GROUP fields, in order, and (h) which fields TOTAL - both read off
                // the committed definition. A schedule that groups by a quantity is the
                // defect this pair exists to catch, so neither is taken from the plan.
                try
                {
                    ScheduleDefinition def = verified.Definition;
                    var sortIds = new List<int>();
                    for (int i = 0; i < def.GetSortGroupFieldCount(); i++)
                        sortIds.Add(def.GetSortGroupField(i).FieldId.IntegerValue);
                    postcondition.Record("sort_group", FieldNames(verified, expectedSortIds), FieldNames(verified, sortIds),
                                         sortIds.SequenceEqual(expectedSortIds));
                }
                catch (Exception ex) { postcondition.Unreadable("sort_group", new JArray(expectedSortIds), "could not be read: " + ex.Message); }

                try
                {
                    ScheduleDefinition def = verified.Definition;
                    var totalIds = def.GetFieldOrder().Select(id => def.GetField(id))
                        .Where(f => f.DisplayType == ScheduleFieldDisplayType.Totals)
                        .Select(f => f.FieldId.IntegerValue).OrderBy(i => i).ToList();
                    var wantTotals = expectedTotalIds.OrderBy(i => i).ToList();
                    postcondition.Record("totals", FieldNames(verified, wantTotals), FieldNames(verified, totalIds),
                                         totalIds.SequenceEqual(wantTotals));
                }
                catch (Exception ex) { postcondition.Unreadable("totals", new JArray(expectedTotalIds), "could not be read: " + ex.Message); }
            }

            bool postconditionVerified = postcondition.AllVerified;
            if (!postconditionVerified)
            {
                ApplicationState state = postcondition.AllMeasured
                    ? ApplicationState.Partial
                    : ApplicationState.Uncertain;
                JObject evidence = postcondition.ToJson();
                return ScheduleFieldPlan.WriteFailure(
                    "The transaction committed, but post-commit verification did not match the request. " +
                    "The schedule EXISTS and may require correction; these are the comparisons: " +
                    evidence.ToString(Newtonsoft.Json.Formatting.None),
                    ScheduleId(createdId), commitStatus.ToString(), state, "postcondition", true, evidence, assumptions);
            }

            try
            {
                int bodyRows = verified.GetTableData().GetSectionData(SectionType.Body).NumberOfRows;

                var csResult = new JObject
                {
                    ["created"] = true,
                    ["schedule_id"] = Rid.Value(verified.Id),
                    // Read off the committed schedule, never echoed from the request.
                    ["name"] = verified.Name,
                    ["category"] = verified.Definition.CategoryId == ElementId.InvalidElementId ? "Multi-Category" : SafePlanCatName(Category.GetCategory(doc, verified.Definition.CategoryId)),
                    ["kind"] = kind,
                    ["category_id"] = Rid.Value(verified.Definition.CategoryId),
                    ["include_links_verified"] = verified.Definition.IncludeLinkedFiles,
                    ["itemized_verified"] = verified.Definition.IsItemized,
                    ["fields_verified"] = verifiedFields,
                    ["postcondition"] = postcondition.ToJson(),
                    ["fields_requested"] = new JArray(requestedFields),
                    ["fields_omitted"] = new JArray(omitted),
                    ["fields_missing"] = new JArray(omitted),
                    ["fields_substituted"] = new JArray(fieldPlan.SubstitutedPairs.Select(p => new JObject { ["requested"] = p.Key, ["used"] = p.Value })),
                    ["fields_not_added_twice"] = new JArray(fieldPlan.Duplicates.Select(d => new JObject { ["requested"] = d.Requested, ["same_column_as"] = d.DuplicateOf })),
                    ["field_plan"] = fieldPlan.ToJson(),
                    ["grouping"] = GroupingJson(grouping, groupingFacts),
                    ["body_rows"] = bodyRows,
                    ["has_body_rows"] = bodyRows > 0,
                    ["federated_coverage"] = FederatedVisibility.Measure(doc, includeLinks)
                };
                // Against what was REQUESTED: a field left out is unresolved, so the state is partial,
                // never verified_applied (a requested name that shares a column counts as present).
                ApplicationOutcome.Stamp(csResult, omitted.Count == 0
                    ? WriteTally.OneObject(commitStatus.ToString(), postconditionVerified)
                    : ScheduleFieldPlan.Tally(commitStatus.ToString(), requestedFields.Count, omitted.Count, postconditionVerified));
                AssumptionNotes.Stamp(csResult, assumptions);
                return CommandResult.Ok(csResult);
            }
            catch (Exception ex)
            {
                return ScheduleFieldPlan.WriteFailure(
                    "The schedule committed and its requested properties matched, but a later result read failed: " +
                    ex.Message + ". The write is not hidden behind this reporting failure.",
                    ScheduleId(createdId), commitStatus.ToString(), ApplicationState.Uncertain,
                    "result_read", true, postcondition.ToJson(), assumptions);
            }
        }

        private static long? ScheduleId(ElementId id)
            => id == null || id == ElementId.InvalidElementId ? (long?)null : Rid.Value(id);

        /// <summary>
        /// The identity of a column, the same spelling for a SchedulableField and a ScheduleField: field type and
        /// parameter id, plus the field name when the id is the invalid one (custom fields, combined parameters,
        /// formulas and percentages share it) - see ScheduleFieldPlan.Identity. This command creates regular, key
        /// and multi-category schedules, never a material takeoff; in a material takeoff the element's and the
        /// material's quantities differ by field type (Instance vs MaterialQuantity), so the type tells them apart.
        /// </summary>
        private static string IdentityOf(Document doc, SchedulableField f) =>
            ScheduleFieldPlan.Identity(f.FieldType.ToString(), Rid.Value(f.ParameterId), SafeName(() => f.GetName(doc)));
        /// <summary>
        /// A schedule column, spelt from ITS schedulable field (ScheduleField.GetSchedulableField), so the plan, the
        /// apply and the post-commit re-read all take the name from the same source (SchedulableField.GetName). Only
        /// when that cannot be read does it fall back to the column's own name.
        /// </summary>
        private static string IdentityOf(Document doc, ScheduleField f)
        {
            SchedulableField schedulable = null;
            try { schedulable = f.GetSchedulableField(); } catch (Exception) { }
            if (schedulable != null) return IdentityOf(doc, schedulable);
            return ScheduleFieldPlan.Identity(f.FieldType.ToString(), Rid.Value(f.ParameterId), SafeName(() => f.GetName()));
        }

        private static string SafeName(Func<string> read)
        {
            try { return read(); } catch (Exception) { return null; }
        }

        /// <summary>How far the probe transaction got; read by the caller when the probe throws.</summary>
        private sealed class ProbeProgress
        {
            public ScheduleProbeStage Stage = ScheduleProbeStage.NotStarted;
        }

        /// <summary>
        /// Rolls <paramref name="t"/> back after <paramref name="original"/> and returns what Revit ANSWERED
        /// (Guard.RollBack reports a status, it does not throw on a rollback that did not happen). When the rollback
        /// itself throws, the ORIGINAL cause is kept and the rollback's added (ScheduleFieldProbe.KeepOriginal). A
        /// transaction that is no longer started counts as rolled back only when Revit explicitly says RolledBack;
        /// every other status remains unconfirmed.
        /// </summary>
        private static Guard.RollbackResult RollBackKeeping(Transaction t, Exception original)
        {
            try
            {
                TransactionStatus status = t.GetStatus();
                if (status == TransactionStatus.Started) return Guard.RollBack(t);
                return new Guard.RollbackResult(status);
            }
            catch (Exception rollback)
            {
                throw ScheduleFieldProbe.KeepOriginal(original, rollback);
            }
        }

        /// <summary>Whether the schedule is still in the model. Null means the lookup could not be completed.</summary>
        private static bool? ReadSchedulePresence(Document doc, ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId) return false;
            try { return doc.GetElement(id) != null; } catch (Exception) { return null; }
        }

        /// <summary>
        /// The apply's token check that does not depend on the plan, made BEFORE the probe transaction and spending
        /// nothing: a token was sent, is still spendable (known, unused, not expired) and was issued for THIS
        /// command on THIS document (ConfirmationStore.Precheck). Inside a confirmed execute_plan the outer token
        /// binds the graph: nothing to check here. Null to go on; otherwise why not.
        /// </summary>
        private string ApplyTokenRefusal(GateResult gate, JObject request)
        {
            if (DocumentGate.InConfirmedAtomicPlan) return null;
            string token = request.Value<string>("confirmation_token");
            if (string.IsNullOrWhiteSpace(token))
                return "no confirmation_token was sent (run this command with dry_run=true first; it returns the plan and a token for exactly that plan)";
            return DocumentGate.Confirmations.Precheck(token, Name, gate.Fingerprint, DateTime.UtcNow);
        }

        /// <summary>
        /// Resolve every requested name against the fields Revit offers for the category, by creating the schedule
        /// in a transaction that is ALWAYS rolled back (and checked to be gone). Revit's own columns (a key
        /// schedule's Key Name) are passed as base identities so a request for one is not added twice.
        /// </summary>
        private static ScheduleFieldPlan ResolveFieldPlan(Document doc, ElementId categoryId, bool key, bool includeLinks, bool itemized,
                                                          List<string> requestedFields, ProbeProgress progress, out List<string> offered)
        {
            offered = new List<string>();
            var resolutions = new List<ScheduleFieldResolution>();
            var baseIds = new List<string>();
            ElementId provisional = ElementId.InvalidElementId;
            progress.Stage = ScheduleProbeStage.NotStarted;
            using (var t = new Transaction(doc, "Horizun: rehearse schedule fields"))
            {
                TransactionStatus started = t.Start();
                if (started != TransactionStatus.Started)
                    throw new InvalidOperationException("Revit answered " + started + " to starting the transaction used to ask for the fields");
                progress.Stage = ScheduleProbeStage.StartedNothingCreated;
                try
                {
                    ViewSchedule probe = key ? ViewSchedule.CreateKeySchedule(doc, categoryId) : ViewSchedule.CreateSchedule(doc, categoryId);
                    progress.Stage = ScheduleProbeStage.CreatedRolledBack;
                    provisional = probe.Id;
                    ScheduleDefinition definition = probe.Definition;
                    if (!key)
                    {
                        definition.IncludeLinkedFiles = includeLinks;
                        definition.IsItemized = itemized;
                    }
                    foreach (ScheduleFieldId id in definition.GetFieldOrder())
                        baseIds.Add(IdentityOf(doc, definition.GetField(id)));
                    var available = definition.GetSchedulableFields().ToList();
                    foreach (SchedulableField f in available)
                    {
                        try { offered.Add(f.GetName(doc)); } catch (Exception) { }
                    }
                    foreach (string fieldName in requestedFields)
                    {
                        string used = fieldName;
                        SchedulableField schedulable = ResolveField(doc, available, fieldName);
                        if (schedulable == null)
                            foreach (string alias in ScheduleFieldRescue.AliasesFor(fieldName))
                            {
                                schedulable = ResolveField(doc, available, alias);
                                if (schedulable != null) { used = alias; break; }
                            }
                        resolutions.Add(new ScheduleFieldResolution
                        {
                            Requested = fieldName,
                            Used = schedulable == null ? null : used,
                            Identity = schedulable == null ? null : IdentityOf(doc, schedulable)
                        });
                    }
                }
                catch (Exception original)
                {
                    // Not a finally: a rollback that throws there would replace WHY the probe failed. And the
                    // rollback's ANSWER is read: an unconfirmed one, or a schedule still in the model, is said.
                    Guard.RollbackResult undone;
                    try { undone = RollBackKeeping(t, original); }
                    catch (Exception) { progress.Stage = ScheduleProbeStage.RollbackFailed; throw; }
                    progress.Stage = ScheduleFieldProbe.AfterRollback(progress.Stage, undone.Confirmed,
                                                                      ReadSchedulePresence(doc, provisional));
                    if (!ScheduleFieldProbe.ModelUntouched(progress.Stage))
                        throw ScheduleFieldProbe.KeepOriginal(original,
                            new InvalidOperationException(ProbeRollbackProblem(progress.Stage, undone.StatusName)));
                    throw;
                }
                Guard.RollbackResult rolled;
                try { rolled = Guard.RollBack(t); }
                catch (Exception) { progress.Stage = ScheduleProbeStage.RollbackFailed; throw; }
                progress.Stage = ScheduleFieldProbe.AfterRollback(progress.Stage, rolled.Confirmed,
                                                                  ReadSchedulePresence(doc, provisional));
                if (!ScheduleFieldProbe.ModelUntouched(progress.Stage))
                    throw new InvalidOperationException(ProbeRollbackProblem(progress.Stage, rolled.StatusName));
            }
            return ScheduleFieldPlan.Build(resolutions, baseIds);
        }

        private static string ProbeRollbackProblem(ScheduleProbeStage stage, string status)
        {
            if (stage == ScheduleProbeStage.LeftInModel)
                return "the schedule created to ask for the fields is still in the model after the rollback";
            if (stage == ScheduleProbeStage.PresenceUnverified)
                return "the schedule created to ask for the fields could not be re-read after the rollback";
            return "Revit answered " + status + " to rolling back the transaction used to ask for the fields";
        }

        /// <summary>The SUPUESTO rows of a field plan: substitutions, columns not added twice, fields left out.</summary>
        private static List<JObject> FieldPlanAssumptions(ScheduleFieldPlan plan, string categoryName, List<string> offered)
        {
            var rows = new List<JObject>();
            var substituted = plan.SubstitutedPairs;
            if (substituted.Count > 0) rows.Add(ScheduleFieldRescue.SubstitutedRow(substituted));
            var duplicates = plan.Duplicates;
            if (duplicates.Count > 0) rows.Add(ScheduleFieldPlan.DuplicateRow(duplicates));
            var omitted = plan.OmittedNames;
            if (omitted.Count > 0)
            {
                var suggestions = new Dictionary<string, IReadOnlyList<string>>();
                foreach (string m in omitted) suggestions[m] = ScheduleFieldRescue.Suggest(m, offered);
                rows.Add(ScheduleFieldRescue.SkippedRow(categoryName, omitted, suggestions));
            }
            return rows;
        }

        /// <summary>What Revit says the field IS - each answer guarded, an unreadable one left null.</summary>
        private static ScheduleGroupingField GroupingFacts(string requestedName, ScheduleField field)
        {
            var facts = new ScheduleGroupingField { RequestedName = requestedName };
            try { facts.FieldType = field.FieldType.ToString(); } catch { }
            try
            {
                ForgeTypeId spec = field.GetSpecTypeId();
                facts.SpecTypeId = spec?.TypeId ?? "";
                facts.IsMeasurable = spec != null && !string.IsNullOrEmpty(spec.TypeId) && UnitUtils.IsMeasurableSpec(spec);
            }
            catch { }
            try { facts.CanTotal = field.CanTotal(); } catch { }
            return facts;
        }

        private static JArray FieldNames(ViewSchedule schedule, IEnumerable<int> fieldIds)
        {
            var names = new JArray();
            ScheduleDefinition def = schedule.Definition;
            foreach (int id in fieldIds)
            {
                string name;
                try { name = def.GetField(new ScheduleFieldId(id)).GetName(); } catch { name = "<field " + id + ">"; }
                names.Add(name);
            }
            return names;
        }

        private static JToken GroupingJson(ScheduleGroupingPlan plan, List<ScheduleGroupingField> facts)
        {
            if (plan == null) return JValue.CreateNull();
            var fields = new JArray();
            for (int i = 0; i < facts.Count; i++)
                fields.Add(new JObject
                {
                    ["field"] = facts[i].RequestedName,
                    ["role"] = plan.Roles[i] == ScheduleFieldRole.Quantity ? "quantity" : "identity",
                    ["why"] = plan.Reasons[i],
                    ["sort_group"] = plan.SortGroup.Contains(i),
                    ["totals"] = plan.Totals.Contains(i)
                });
            return new JObject
            {
                ["source"] = plan.Source,
                ["sort_group"] = new JArray(plan.SortGroup.Select(i => (JToken)facts[i].RequestedName)),
                ["totals"] = new JArray(plan.Totals.Select(i => (JToken)facts[i].RequestedName)),
                ["fields"] = fields
            };
        }

        private static List<string> ReadFields(JArray array)
        {
            if (array == null) return new List<string>();
            return array.Where(t => t.Type == JTokenType.String)
                .Select(t => t.Value<string>().Trim()).Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static SchedulableField ResolveField(Document doc, IList<SchedulableField> available, string requested)
        {
            SchedulableField byDisplayName = available.FirstOrDefault(field =>
                string.Equals(field.GetName(doc), requested, StringComparison.OrdinalIgnoreCase));
            if (byDisplayName != null) return byDisplayName;

            string key = requested.Trim().ToLowerInvariant();
            if (key == "count" || key == "cantidad" || key == "recuento")
                return available.FirstOrDefault(field => field.FieldType == ScheduleFieldType.Count);

            BuiltInParameter parameter;
            BuiltInParameter[] candidates;
            if (key == "family" || key == "familia")
                candidates = new[] { BuiltInParameter.ELEM_FAMILY_PARAM, BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM };
            else if (key == "type" || key == "tipo")
                candidates = new[] { BuiltInParameter.ELEM_TYPE_PARAM, BuiltInParameter.ELEM_TYPE_LABEL };
            else if (key == "category" || key == "categoria" || key == "categoría")
                candidates = new[] { BuiltInParameter.ELEM_CATEGORY_PARAM, BuiltInParameter.ELEM_CATEGORY_PARAM_MT };
            else if (Enum.TryParse(requested, true, out parameter)) candidates = new[] { parameter };
            else return null;

            var ids = new HashSet<long>(candidates.Select(candidate => (long)candidate));
            return available.FirstOrDefault(field => ids.Contains(Rid.Value(field.ParameterId)));
        }

        /// <summary>Guarded: a plan must never fail while MEASURING.</summary>
        private static string SafePlanCatName(Category c)
        {
            try { return c == null ? null : c.Name; } catch { return "<unreadable>"; }
        }

        private static Category ResolveCategory(Document doc, string text)
        {
            BuiltInCategory bic;
            if (Enum.TryParse(text, true, out bic))
            {
                try { return Category.GetCategory(doc, bic); } catch { }
            }
            foreach (Category category in doc.Settings.Categories)
                if (string.Equals(category.Name, text, StringComparison.OrdinalIgnoreCase)) return category;
            return null;
        }

        /// <summary>A column as the post-commit comparison sees it: the same identity as the plan (IdentityOf).</summary>
        private sealed class FieldIdentity : IEquatable<FieldIdentity>
        {
            private readonly string _identity;

            private FieldIdentity(string identity) { _identity = identity ?? ""; }

            public static FieldIdentity From(Document doc, ScheduleField field) => new FieldIdentity(IdentityOf(doc, field));

            public bool Equals(FieldIdentity other) => other != null && string.Equals(_identity, other._identity, StringComparison.Ordinal);
            public override bool Equals(object obj) => Equals(obj as FieldIdentity);
            public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_identity);
        }
    }
}
