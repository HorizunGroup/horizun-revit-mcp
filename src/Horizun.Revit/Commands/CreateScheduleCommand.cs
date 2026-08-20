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
            "Dry-run is the default. The apply pass adds the requested fields by their Revit display names, groups by " +
            "the requested non-Count fields when itemized=false, commits once, then re-reads the schedule, its fields, " +
            "IncludeLinkedFiles flag and body row count. A category with zero host elements is valid: linked elements " +
            "are exactly why this command exists.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string categoryText = request.Value<string>("category");
            string scheduleName = request.Value<string>("name");
            if (string.IsNullOrWhiteSpace(categoryText)) return CommandResult.Fail("category is required.");
            if (string.IsNullOrWhiteSpace(scheduleName)) return CommandResult.Fail("name is required.");

            Category category = ResolveCategory(doc, categoryText);
            if (category == null)
                return CommandResult.Fail("Category '" + categoryText + "' was not found. Use a BuiltInCategory token such as OST_Walls or the category display name.");

            var valid = ViewSchedule.GetValidCategoriesForSchedule();
            if (!valid.Any(id => id == category.Id))
                return CommandResult.Fail("Category '" + category.Name + "' is not valid for a regular Revit schedule.");

            if (new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Any(v => string.Equals(v.Name, scheduleName, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail("A schedule named '" + scheduleName + "' already exists; nothing was changed.");

            bool includeLinks = request["include_links"] == null || request.Value<bool>("include_links");
            bool itemized = request.Value<bool?>("itemized") ?? false;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            List<string> requestedFields = ReadFields(request["fields"] as JArray);
            if (requestedFields.Count == 0)
                requestedFields.AddRange(new[] { "Count", "Family", "Type" });

            string planHash = DocumentGate.PlanHash(request, "category", "name", "fields", "include_links", "itemized");

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
                Category = SafePlanCatName(category),
                Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                {
                    { "category_id", Rid.Value(category.Id).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "fields", string.Join(",", requestedFields) },
                    { "include_links", includeLinks ? "1" : "0" },
                    { "itemized", itemized ? "1" : "0" }
                }
            });

            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["dry_run"] = true,
                    ["transaction_status"] = "not_started",
                    ["category"] = category.Name,
                    ["category_id"] = Rid.Value(category.Id),
                    ["name"] = scheduleName,
                    ["fields_requested"] = new JArray(requestedFields),
                    ["include_links"] = includeLinks,
                    ["itemized"] = itemized,
                    ["host_element_count"] = new FilteredElementCollector(doc).OfCategoryId(category.Id)
                        .WhereElementIsNotElementType().GetElementCount(),
                    ["note"] = "Nothing was written. Field availability and linked rows are verified after creation, not guessed from host elements."
                };
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(rehearsal, gate, Name, planHash, true,
                    "the token is bound to this category AS RESOLVED (not just the text you typed), the schedule " +
                    "name, field list and link/itemization settings. A category text that starts resolving to a " +
                    "different category refuses as a stale plan.");
                // One schedule, resolved end to end or this line is not reached.
                ApplicationOutcome.StampRehearsal(rehearsal, 1, 0, 0, 0);
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
            SilentRollbackException commitFailure = null;
            var missing = new List<string>();
            var expectedFields = new List<FieldIdentity>();
            using (var tx = new Transaction(doc, "Horizun: create schedule"))
            {
                tx.Start();
                try
                {
                    ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, category.Id);
                    schedule.Name = scheduleName;
                    ScheduleDefinition definition = schedule.Definition;
                    definition.IncludeLinkedFiles = includeLinks;
                    definition.IsItemized = itemized;

                    var available = definition.GetSchedulableFields().ToList();

                    var added = new List<ScheduleField>();
                    foreach (string fieldName in requestedFields)
                    {
                        SchedulableField schedulable = ResolveField(doc, available, fieldName);
                        if (schedulable == null)
                        {
                            missing.Add(fieldName);
                            continue;
                        }
                        ScheduleField field = definition.AddField(schedulable);
                        added.Add(field);
                        expectedFields.Add(FieldIdentity.From(field));
                    }

                    if (missing.Count > 0)
                    {
                        Guard.RollBack(tx);
                        return CommandResult.Fail("The schedule was not created because these requested fields are not schedulable for " +
                            category.Name + ": " + string.Join(", ", missing) + ". Use a displayed field name, Count/Family/Type, " +
                            "or a BuiltInParameter token such as ELEM_TYPE_PARAM.");
                    }

                    if (!itemized)
                    {
                        foreach (ScheduleField field in added.Where(f => f.FieldType != ScheduleFieldType.Count))
                            definition.AddSortGroupField(new ScheduleSortGroupField(field.FieldId));
                    }

                    createdId = schedule.Id;
                    // Guard refuses RolledBack and Pending. Only after it returns can this
                    // command enter a success/postcondition path, so the declaration never
                    // treats an in-flight or silently rolled-back transaction as applied.
                    try { commitStatus = Guard.Commit(tx, "create schedule"); }
                    catch (SilentRollbackException ex)
                    {
                        commitStatus = ex.Status;
                        commitFailure = ex;
                    }
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                    throw;
                }
            }

            if (commitStatus != TransactionStatus.Committed)
            {
                ApplicationState state = commitStatus == TransactionStatus.RolledBack
                    ? ApplicationState.RolledBack
                    : ApplicationState.Uncertain;
                JObject detail = ScheduleFailureDetail(createdId, commitStatus, state,
                    "transaction_commit", scheduleReread: false, postcondition: null);
                return CommandResult.FailWithDetail(
                    (commitFailure == null ? "The schedule transaction did not commit." : commitFailure.Message) +
                    " The exact status was " + commitStatus + "; " +
                    (state == ApplicationState.RolledBack
                        ? "Revit confirmed rollback."
                        : "this is not a terminal committed/rolled-back state, so model state is uncertain."),
                    detail);
            }

            ViewSchedule verified;
            try { verified = doc.GetElement(createdId) as ViewSchedule; }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail(
                    "The transaction committed, but the schedule could not be re-read: " + ex.Message,
                    ScheduleFailureDetail(createdId, commitStatus, ApplicationState.Uncertain,
                        "schedule_reread", scheduleReread: false, postcondition: null));
            }
            if (verified == null)
                return CommandResult.FailWithDetail(
                    "The transaction committed but the schedule could not be re-read from the model.",
                    ScheduleFailureDetail(createdId, commitStatus, ApplicationState.Uncertain,
                        "schedule_reread", scheduleReread: false, postcondition: null));

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
            var postcondition = new PostconditionCheck("name", "category", "fields",
                                                       "include_links", "itemized");

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
                    new JObject { ["id"] = Rid.Value(category.Id), ["name"] = category.Name },
                    new JObject { ["id"] = Rid.Value(actualCategoryId), ["name"] = SafePlanCatName(Category.GetCategory(doc, actualCategoryId)) },
                    actualCategoryId == category.Id);
            }
            catch (Exception ex)
            {
                postcondition.Unreadable("category", Rid.Value(category.Id),
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
                    actualFields.Select(FieldIdentity.From).SequenceEqual(expectedFields);
                postcondition.Record("fields", new JArray(requestedFields.Select(f => (JToken)f)),
                                     verifiedFields, fieldsMatch);
            }
            catch (Exception ex)
            {
                actualFields = new List<ScheduleField>();
                verifiedFields = new JArray();
                postcondition.Unreadable("fields", new JArray(requestedFields.Select(f => (JToken)f)),
                                         "the committed schedule's fields could not be read: " + ex.Message);
            }

            // (d) INCLUDE_LINKS and (e) ITEMIZED.
            try { postcondition.Compare("include_links", includeLinks, verified.Definition.IncludeLinkedFiles); }
            catch (Exception ex) { postcondition.Unreadable("include_links", includeLinks, "could not be read: " + ex.Message); }

            try { postcondition.Compare("itemized", itemized, verified.Definition.IsItemized); }
            catch (Exception ex) { postcondition.Unreadable("itemized", itemized, "could not be read: " + ex.Message); }

            bool postconditionVerified = postcondition.AllVerified;
            if (!postconditionVerified)
            {
                ApplicationState state = postcondition.AllMeasured
                    ? ApplicationState.Partial
                    : ApplicationState.Uncertain;
                JObject evidence = postcondition.ToJson();
                return CommandResult.FailWithDetail(
                    "The transaction committed, but post-commit verification did not match the request. " +
                    "The schedule EXISTS and may require correction; these are the comparisons: " +
                    evidence.ToString(Newtonsoft.Json.Formatting.None),
                    ScheduleFailureDetail(createdId, commitStatus, state, "postcondition",
                        scheduleReread: true, postcondition: evidence));
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
                    ["category"] = SafePlanCatName(Category.GetCategory(doc, verified.Definition.CategoryId)),
                    ["category_id"] = Rid.Value(verified.Definition.CategoryId),
                    ["include_links_verified"] = verified.Definition.IncludeLinkedFiles,
                    ["itemized_verified"] = verified.Definition.IsItemized,
                    ["fields_verified"] = verifiedFields,
                    ["postcondition"] = postcondition.ToJson(),
                    ["fields_missing"] = new JArray(missing),
                    ["body_rows"] = bodyRows,
                    ["has_body_rows"] = bodyRows > 0,
                    ["federated_coverage"] = FederatedVisibility.Measure(doc, includeLinks)
                };
                ApplicationOutcome.Stamp(csResult, WriteTally.OneObject(commitStatus.ToString(), postconditionVerified));
                return CommandResult.Ok(csResult);
            }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail(
                    "The schedule committed and its requested properties matched, but a later result read failed: " +
                    ex.Message + ". The write is not hidden behind this reporting failure.",
                    ScheduleFailureDetail(createdId, commitStatus, ApplicationState.Uncertain,
                        "result_read", scheduleReread: true, postcondition: postcondition.ToJson()));
            }
        }

        private static JObject ScheduleFailureDetail(ElementId scheduleId, TransactionStatus status,
                                                      ApplicationState state, string stage,
                                                      bool scheduleReread, JObject postcondition)
        {
            long? id = scheduleId == null || scheduleId == ElementId.InvalidElementId
                ? (long?)null
                : Rid.Value(scheduleId);
            return ApplicationOutcome.FailureAfterWrite(
                "schedule_id", id, stage, status.ToString(), state, scheduleReread, postcondition);
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

        private sealed class FieldIdentity : IEquatable<FieldIdentity>
        {
            private readonly ScheduleFieldType _type;
            private readonly long _parameterId;

            private FieldIdentity(ScheduleFieldType type, long parameterId)
            {
                _type = type;
                _parameterId = parameterId;
            }

            public static FieldIdentity From(ScheduleField field) =>
                new FieldIdentity(field.FieldType, Rid.Value(field.ParameterId));

            public bool Equals(FieldIdentity other) => other != null && _type == other._type && _parameterId == other._parameterId;
            public override bool Equals(object obj) => Equals(obj as FieldIdentity);
            public override int GetHashCode() => ((int)_type * 397) ^ _parameterId.GetHashCode();
        }
    }
}
