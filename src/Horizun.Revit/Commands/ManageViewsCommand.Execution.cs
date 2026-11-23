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
    // Shared execution and response path for the view and sheet batch.
    public sealed partial class ManageViewsCommand
    {
        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : ParseVerbatim(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            JArray input = request["actions"] as JArray;
            if (input == null || input.Count == 0 || input.Count > 500)
                return CommandResult.Fail("actions must contain 1..500 entries.");
            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale))
                return CommandResult.Fail("units must be mm, m or feet.");
            // hide_outside_crop is resolved HERE into an ordinary permanent hide_elements
            // over the ids it found, so validation, the bound plan, the write and the
            // re-read are hide_elements' own. The same model yields the same ids on the
            // dry run and on the apply; if it does not, the token's fingerprint refuses.
            var expansions = new JArray();
            for (int i = 0; i < input.Count; i++)
            {
                if (!(input[i] is JObject h) || h.Value<string>("operation") != "hide_outside_crop") continue;
                string why = ExpandHideOutsideCrop(doc, h, scale, out JArray found);
                if (why != null) return CommandResult.Fail("actions[" + i + "] hide_outside_crop: " + why + " Nothing was changed.");
                expansions.Add(new JObject { ["index"] = i, ["view_id"] = h["view_id"], ["element_ids"] = found.DeepClone() });
                h["operation"] = "hide_elements"; h["element_ids"] = found; h["permanent"] = true; h.Remove("margin");
            }

            var knownKeys = new Dictionary<string, Type>(StringComparer.Ordinal);
            var errors = new JArray();
            var plans = new List<JObject>();
            var assumptions = new List<JObject>();
            // Every action's outcome, so the fallback is decided once over the whole
            // batch rather than granted because one entry was uncovered.
            var outcomes = new List<ActionOutcome>();
            var scaleByKey = new Dictionary<string, KeyValuePair<int, int>>(StringComparer.Ordinal);   // key -> (index, view_scale)
            return ViewArgumentRules.PlanBatch(input, (i, a) =>
            {
                string error = Validate(doc, a, knownKeys, out string reason) ?? TemplateScaleConflict(doc, a, scaleByKey);
                if (error != null) return (error, reason);
                if (a["view_scale"] != null && !string.IsNullOrWhiteSpace(a.Value<string>("key")))
                    scaleByKey[a.Value<string>("key")] = new KeyValuePair<int, int>(i, a.Value<int>("view_scale"));
                JObject assumption = DocumentationDefaults.ViewAction(a, i);
                if (assumption != null) assumptions.Add(assumption);
                string key = a.Value<string>("key");
                string operation = a.Value<string>("operation");
                if (!string.IsNullOrWhiteSpace(key)) knownKeys.Add(key, ResultType(operation));
                var planRow = new JObject { ["index"] = i, ["operation"] = operation, ["key"] = key };
                if (string.Equals(operation, "explain_graphics", StringComparison.OrdinalIgnoreCase) &&
                    a["view_id"] != null && doc.GetElement(Rid.Make(a.Value<long>("view_id"))) is View explained)
                    planRow["report"] = ExplainGraphics(doc, explained, ReadElementIds(doc, a, "element_ids"));
                if (string.Equals(operation, "sheet_set_list", StringComparison.OrdinalIgnoreCase))
                    planRow["report"] = SheetSetCensus(doc);
                if (IsRenumberOperation(operation)) planRow["renumber"] = RenumberPreview(doc, a);
                if (IsPerspectiveOperation(operation)) planRow["perspective"] = PerspectivePreview(a, scale);
                if (IsSunStudyOperation(operation)) planRow["sun"] = SunStudyPreview(doc, a);
                if (IsGraphicDisplayOperation(operation)) planRow["graphic_display"] = GraphicDisplayPreview(doc, a);
                if (IsViewPropertiesOperation(operation)) planRow["view_properties"] = ViewPropertiesPreview(doc, a);
                if (IsSelectionFilterOperation(operation)) planRow["selection_filter"] = SelectionFilterPreview(a);
                if (IsStartingViewOperation(operation)) planRow["starting_view"] = StartingViewPreview(doc);
                if (IsGridExtentOperation(operation)) planRow["grid_extents"] = GridExtentPreview(doc, a);
                plans.Add(planRow);
                return (null, null);
            },
                () => new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(view => !(view is ViewSheet)).Select(view => view.Name),
                PerspectiveViewNames, errors, outcomes,
                () => ExecutePlanned(app, gate, request, input, scale, expansions, errors, plans, outcomes, assumptions));
        }

        private CommandResult ExecutePlanned(UIApplication app, GateResult gate, JObject request, JArray input,
            double scale, JArray expansions, JArray errors, List<JObject> plans, List<ActionOutcome> outcomes, List<JObject> assumptions)
        {
            Document doc = gate.Document;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "actions");

            // ---- The MATERIALISED plan: what the batch's ID REFERENCES resolve to. ------
            // planHash binds the graph as written. But this batch builds documentation on
            // top of existing elements it names by id - a source view to duplicate, a
            // template to apply, a titleblock, a level for a new plan - and an id is a
            // pointer, not a meaning. Between rehearsal and apply the template can be
            // edited, the source view cropped, the titleblock swapped for another type
            // under the same id family. The plan records each referenced element's
            // UniqueId AND NAME as resolved now: the name is what the person read when
            // they approved, so a renamed reference refuses as stale even though the id
            // still resolves. In-batch aliases (keys) are not ambient state and carry no
            // drift; they are deliberately absent.
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
                if (a == null) continue;
                string op = a.Value<string>("operation") ?? "";
                var row = new PlannedElement
                {
                    UniqueId = "action:" + i,
                    Category = op,
                    // apply_template and place_* CHANGE an existing object; the rest mint
                    // new ones. The distinction keeps the counts a person reads honest.
                    Action = ModifyingOperations.Contains(op) ? PlannedAction.Modify : PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>()
                };
                row.ProposedValues = new Dictionary<string,string> { { "specification", a.ToString(Formatting.None) } };
                if (IsRenumberOperation(op)) BindRenumber(doc, row.BeforeValues);
                if (IsSunStudyOperation(op)) BindSunStudy(doc, a, row.BeforeValues);
                if (IsGraphicDisplayOperation(op) && !errors.Any(e => (int)e["index"] == i)) BindGraphicDisplay(doc, a, row.BeforeValues);
                if (IsStartingViewOperation(op)) BindStartingView(doc, row.BeforeValues);
                if (IsGridExtentOperation(op)) BindGridExtent(doc, a, row.BeforeValues);
                if (op == "apply_template" && a["view_id"] != null)
                {
                    View targetView = doc.GetElement(Rid.Make(a.Value<long>("view_id"))) as View;
                    if (targetView != null)
                    {
                        row.ElementId = Rid.Value(targetView.Id);
                        row.BeforeValues["template_view_id"] = Rid.Value(targetView.ViewTemplateId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        row.ProposedValues["template_view_id"] = a.Value<string>("template_view_id");
                    }
                }
                if (IsViewPropertiesOperation(op) && a["view_ids"] is JArray boundViews) row.BeforeValues["view_ids"] = PlanListBinding(doc, boundViews);
                if (IsSelectionFilterOperation(op) && a["element_ids"] is JArray boundMembers) row.BeforeValues["element_ids"] = PlanListBinding(doc, boundMembers);
                foreach (string field in RefIdFields)
                {
                    long? id = a.Value<long?>(field);
                    if (id == null) continue;
                    row.BeforeValues[field] = SafePlanRef(doc, id.Value);
                }
                resolvedPlan.Elements.Add(row);
            }

            if (dryRun)
            {
                // Operations with construction checks are rehearsed inside Revit: the
                // batch is applied, verified and rolled back, so what a person approves is a batch Revit built.
                JObject rehearsal = null;
                if (errors.Count == 0 && input.OfType<JObject>().Any(x => IsRehearsedOperation(x.Value<string>("operation") ?? "")))
                {
                    CommandResult failed = RehearseBatch(doc, input, scale, out rehearsal);
                    if (failed != null) return AssumptionNotes.MarkFailure(failed,
                        "SUPUESTO: the rehearsal relied on default documentation values. ", assumptions);
                }
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = rehearsal == null ? "not_started" : "RolledBack", ["actions"] = input.Count,
                    ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors, ["plan"] = new JArray(plans),
                    ["note"] = "Nothing was created or changed. Aliases are resolved in action order."
                };
                if (expansions.Count > 0) result["hide_outside_crop"] = expansions;
                if (rehearsal != null)
                {
                    result["validation_level"] = "revit_construction_rolled_back"; result["api_rehearsal"] = rehearsal;
                    result["note"] = "Nothing was created or changed: the batch was applied, verified and rolled back inside Revit. Aliases are resolved in action order.";
                }
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                // Invalid entries make this a partial rehearsal, not a clean one: the token
                // below is already withheld for them, and a plan must read the same fact.
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0
                        ? "the token binds the ordered dependency graph AND what every referenced id resolves to " +
                          "right now, by identity and by name - a template edited into a different template, a " +
                          "renamed source view or a swapped titleblock refuses as a stale plan."
                        : "no usable token is issued while an action is invalid");
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so this
                // is the first call a caller makes; without the block here they got
                // success=true with invalid rows and no way to tell a capability gap
                // from a typo except by sending an apply they had no reason to send.
                return FallbackDecision.Attach(
                    AssumptionNotes.StampResult(CommandResult.Ok(result), assumptions),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
                return FallbackDecision.Refuse(
                    "Invalid action graph; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            // Recomputed by THIS call's own resolution of the same references.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: manage views and sheets";
            var aliases = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            var applied = new List<Applied>();
            bool committed = false;
            string transactionError = null;
            try
            {
                using (var tx = new Transaction(doc, txName))
                {
                    tx.Start();
                    try
                    {
                        RunBatch(doc, input, scale, aliases, applied);
                        Guard.Commit(tx, txName);
                        committed = true;
                    }
                    catch (Exception ex)
                    {
                        bool attempted = false; string rb = PlanFailure.NotAttempted;
                        if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                        return AssumptionNotes.MarkFailure(CommandResult.Fail("Atomic views/sheets batch failed: " + ex.Message + ". " +
                            PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing in it was kept")),
                            "SUPUESTO: the batch relied on default documentation values. ", assumptions);
                    }
                }
            }
            catch (Exception ex) when (committed)
            {
                // A disposal failure cannot undo a commit that Revit already confirmed.
                transactionError = ex.Message;
            }

            var rows = new JArray(); int verified = 0; var numbersMoved = new JArray();
            string readError = null;
            foreach (Applied a in applied)
            {
                var row = new JObject
                {
                    ["index"] = a.Index, ["operation"] = a.Operation,
                    ["element_id"] = a.Id == null ? JValue.CreateNull() : new JValue(Rid.Value(a.Id))
                };
                rows.Add(row);
                try
                {
                    Element e = a.Id == null ? null : doc.GetElement(a.Id);
                    row["present_after_commit"] = e != null;
                    row["actual_class"] = e?.GetType().Name;
                    bool ok = Verify(doc, a, e);
                    row["verified"] = ok;
                    if (ok) verified++;
                    ReadCommittedActionDetails(doc, a, e, row, numbersMoved);
                }
                catch (Exception ex)
                {
                    if (row["verified"] == null)
                    {
                        row["verified"] = false;
                        row["verification_error"] = ex.Message;
                    }
                    // Keep a completed verification even when a later detail read fails.
                    else row["detail_error"] = ex.Message;
                    if (readError == null) readError = ex.Message;
                }
            }
            if (verified != applied.Count || readError != null || transactionError != null)
            {
                var failure = new JObject
                {
                    ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                    ["write_started"] = true, ["changes_applied"] = true,
                    ["actions_verified"] = verified, ["rows"] = rows
                };
                if (transactionError != null) failure["transaction_cleanup_error"] = transactionError;
                return AssumptionNotes.MarkFailure(CommandResult.FailCoded(ErrorCodes.VerificacionFallida,
                    "The transaction committed. " + verified + " of " + applied.Count +
                    " actions passed post-commit verification. Inspect the model and the verification rows." +
                    (readError == null ? "" : " Read error: " + readError) +
                    (transactionError == null ? "" : " Transaction cleanup error: " + transactionError), extra: failure),
                    "SUPUESTO: the batch relied on default documentation values. ", assumptions);
            }
            var mvResult = new JObject
            {
                ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["actions_verified"] = verified,
                ["aliases"] = new JObject(aliases.Select(kv => new JProperty(kv.Key, Rid.Value(kv.Value)))),
                ["rows"] = rows
            };
            if (expansions.Count > 0) mvResult["hide_outside_crop"] = expansions;
            if (numbersMoved.Count > 0) mvResult["detail_numbers_moved"] = numbersMoved;
            // Reached only when every applied action passed its post-commit check.
            ApplicationOutcome.StampApplied(mvResult, ApplicationOutcome.Committed,
                                            applied.Count, verified, verified, 0, 0, 0);
            return AssumptionNotes.StampResult(CommandResult.Ok(mvResult), assumptions);
        }

        private static void ReadCommittedActionDetails(Document doc, Applied a, Element e, JObject row, JArray numbersMoved)
        {
            if (a.Action["view_scale"] != null && e is View scaled)
            {
                row["view_scale_requested"] = a.Action.Value<int>("view_scale");
                int reread = scaled.Scale;
                row["view_scale"] = reread;
                row["view_scale_verified"] = reread == a.Action.Value<int>("view_scale");
            }
            if (e is Viewport placed && string.Equals(a.Operation, "place_view", StringComparison.OrdinalIgnoreCase))
            {
                row["sheet_id"] = Rid.Value(placed.SheetId);
                row["view_id"] = Rid.Value(placed.ViewId);
                if (a.Renumbered != null)
                {
                    row["detail_number_renumbered"] = a.Renumbered;
                    numbersMoved.Add(a.Renumbered);
                }
                XYZ center = placed.GetBoxCenter();
                row["actual_center_internal_feet"] = new JArray(center.X, center.Y, center.Z);
                row["center_error_on_sheet_internal_feet"] = SheetDistance(center, Point(a.Action["point"]) * a.Scale);
                row["detail_number"] = DetailNumberOf(placed);
            }
            // Report the colour legend and whether a hide was temporary or permanent.
            JObject detail = GraphicsDetail(doc, a.Action, a.Operation.ToLowerInvariant(), e)
                             ?? ControlDetail(a.Action, a.Operation.ToLowerInvariant());
            if (detail != null) row["graphics"] = detail;
            if ((string.Equals(a.Operation, "create_callout", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(a.Operation, "create_section", StringComparison.OrdinalIgnoreCase)) && e is View detailView)
                row["detail_view"] = NewDetailViewFacts(doc, detailView, a.Action);
            if (string.Equals(a.Operation, "set_crop", StringComparison.OrdinalIgnoreCase) && e is View croppedView)
                row["crop"] = CropDetail(doc, a, croppedView);
            if (IsRenumberOperation(a.Operation)) row["renumber"] = RenumberDetail(a.Action);
            if (IsPerspectiveOperation(a.Operation)) row["perspective"] = PerspectiveDetail(a.Action);
            if (IsSunStudyOperation(a.Operation)) row["sun"] = SunStudyDetail(a.Action);
            if (IsGraphicDisplayOperation(a.Operation)) row["graphic_display"] = GraphicDisplayDetail(a.Action);
            if (IsViewPropertiesOperation(a.Operation)) row["view_properties"] = ViewPropertiesDetail(a.Action);
            if (IsSelectionFilterOperation(a.Operation)) row["selection_filter"] = SelectionFilterDetail(a.Action);
            if (IsStartingViewOperation(a.Operation)) row["starting_view"] = StartingViewDetail(a.Action);
            if (IsGridExtentOperation(a.Operation)) row["grid_extents"] = GridExtentDetail(a.Action);
            if ((string.Equals(a.Operation, "create_3d", StringComparison.OrdinalIgnoreCase) || IsRoomIsometricOperation(a.Operation)) && View3DDetail(a.Action) != null) row["view_3d"] = View3DDetail(a.Action);
        }

        private static bool IsCreateSheet(string op) => string.Equals(op, "create_sheet", StringComparison.OrdinalIgnoreCase);

        private static bool IsRehearsedOperation(string op) =>
            IsCreateSheet(op) || IsViewPropertiesOperation(op) || IsSelectionFilterOperation(op);

        /// <summary>
        /// Run the same construction and checks as apply, on a copy of the request.
        /// Issue a token only after the rehearsal transaction is confirmed rolled back.
        /// </summary>
        private static CommandResult RehearseBatch(Document doc, JArray input, double scale, out JObject report)
        {
            report = null;
            JArray copy = (JArray)input.DeepClone();
            var aliases = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            var applied = new List<Applied>();
            using (var tx = new Transaction(doc, "Horizun: rehearse views and sheets"))
            {
                TransactionStatus started = tx.Start();
                if (started != TransactionStatus.Started)
                    return CommandResult.Fail("The rehearsal transaction did not start (" + started + "). No confirmation token is issued.");
                try { RunBatch(doc, copy, scale, aliases, applied); }
                catch (Exception ex)
                {
                    TransactionStatus statusAfterFailure = tx.GetStatus();
                    if (statusAfterFailure == TransactionStatus.Started) statusAfterFailure = Guard.RollBack(tx).Status;
                    string outcome = statusAfterFailure == TransactionStatus.RolledBack
                        ? "The rehearsal rolled back and kept no changes."
                        : "Rehearsal rollback was not confirmed (" + statusAfterFailure + "). Inspect the model.";
                    return CommandResult.Fail("Rehearsal inside Revit failed: " + ex.Message + " " + outcome +
                        " No confirmation token is issued.");
                }
                var rows = new JArray();
                foreach (Applied x in applied)
                    rows.Add(new JObject { ["index"] = x.Index, ["operation"] = x.Operation, ["verified_in_transaction"] = true });
                var createdIds = applied.Where(x => x.Id != null && (IsCreateSheet(x.Operation) || IsSelectionFilterOperation(x.Operation)))
                    .Select(x => x.Id).ToList();
                TransactionStatus status = Guard.RollBack(tx).Status;
                bool confirmed = status == TransactionStatus.RolledBack && createdIds.All(id => doc.GetElement(id) == null);
                if (!confirmed)
                    return CommandResult.Fail("Rehearsal rollback was not confirmed (" + status + "); inspect the model before applying anything.");
                report = new JObject
                {
                    ["rehearsed"] = true, ["transaction_status"] = status.ToString(), ["changes_applied"] = false,
                    ["actions"] = rows,
                    ["note"] = "The whole batch was applied and verified inside Revit and rolled back; the rollback was confirmed."
                };
                if (createdIds.Count > 0)
                {
                    report["provisional_elements_absent"] = true;
                    report["provisional_elements_absence_scope"] = "create_sheet_and_selection_filter_result_ids";
                }
            }
            return null;
        }

        /// <summary>
        /// Every action in order, then one regeneration and the in-transaction verification of each result.
        /// Shared by the apply and by the rehearsal of the new operations, so the rehearsal is the apply's
        /// own code run to the point of its commit.
        /// </summary>
        private static void RunBatch(Document doc, JArray input, double scale, Dictionary<string, ElementId> aliases, List<Applied> applied)
        {
            for (int i = 0; i < input.Count; i++)
            {
                JObject action = (JObject)input[i];
                // Placements are centred in bulk; anything else that follows them in
                // the batch (align_viewports reads outlines) sees them centred first.
                if (!string.Equals(action.Value<string>("operation"), "place_view", StringComparison.OrdinalIgnoreCase))
                    CenterPlacedViewports(doc, applied);
                Element result = Apply(doc, action, aliases, scale, applied);
                if (action["view_scale"] != null)
                {
                    if (!(result is View scaleView))
                        throw new InvalidOperationException("operation '" + action.Value<string>("operation") +
                            "' asked for view_scale but produced a " + (result?.GetType().Name ?? "null") + ", not a view.");
                    ApplyViewScale(doc, scaleView, action.Value<int>("view_scale"), action.Value<string>("operation"));
                }
                if (result == null)
                    throw new InvalidOperationException(
                        "operation '" + action.Value<string>("operation") + "' returned no element. " +
                        "The whole batch is being rolled back before commit.");
                string key = action.Value<string>("key");
                if (!string.IsNullOrWhiteSpace(key)) aliases[key] = result.Id;
                applied.Add(new Applied { Index = i, Operation = action.Value<string>("operation"), Id = result.Id,
                    TargetId = TargetId(doc, action, aliases), Action = action, Scale = scale,
                    Batch = input,
                    SheetId = action.Value<string>("operation") == "place_view" || action.Value<string>("operation") == "place_schedule"
                        ? Resolve<ViewSheet>(doc, action, "sheet_id", "sheet_key", aliases).Id : null,
                    ViewId = action.Value<string>("operation") == "place_view"
                        ? Resolve<View>(doc, action, "view_id", "view_key", aliases).Id : null });
                // E8 (field report, 2026-10-18): 27 place_view took ~9 min with no sign of life.
                // Under horizun_submit_job each action now reports progress that
                // horizun_job_status shows. Provisional: the batch is still one transaction.
                ReportProgress("action " + (i + 1) + " (" + action.Value<string>("operation") +
                               ") applied; provisional until commit", i + 1, input.Count);
            }
            CenterPlacedViewports(doc, applied);
            ReportProgress("regenerating and verifying " + applied.Count + " result(s) before commit",
                           input.Count, input.Count);
            doc.Regenerate();
            foreach (Applied action in applied)
            {
                Element reread = doc.GetElement(action.Id);
                if (!Verify(doc, action, reread))
                    throw new InvalidOperationException(
                        "action " + action.Index + " ('" + action.Operation + "') failed verification while the transaction " +
                        "was still reversible. The whole batch is being rolled back before commit.");
            }
        }

        private static Element Apply(Document doc, JObject a, Dictionary<string, ElementId> aliases, double scale, List<Applied> applied)
        {
            string op = a.Value<string>("operation").ToLowerInvariant();
            if (op == "create_drafting")
            {
                ViewDrafting view = ViewDrafting.Create(doc, ResolveVft(doc, a, ViewFamily.Drafting).Id);
                SetName(view, a);
                return view;
            }
            if (op == "create_sheet")
            {
                ElementId title = ElementId.InvalidElementId;
                if (a["title_block_type_id"] != null) title = Need<FamilySymbol>(doc, a, "title_block_type_id").Id;
                ViewSheet sheet = ViewSheet.Create(doc, title);
                if (!string.IsNullOrWhiteSpace(a.Value<string>("name"))) sheet.Name = a.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(a.Value<string>("number"))) sheet.SheetNumber = a.Value<string>("number");
                return sheet;
            }
            return ApplyOther(doc, a, aliases, scale, applied, op);
        }

        private static bool Verify(Document doc, Applied a, Element e)
        {
            if (e == null) return false;
            if (a.Action["view_scale"] != null && (!(e is View scaleView) || scaleView.Scale != a.Action.Value<int>("view_scale"))) return false;
            if (NamedOps.Contains(a.Operation) && !NameMatches(e, a.Action)) return false;
            string op = a.Operation.ToLowerInvariant();
            switch (op)
            {
                case "create_drafting":
                    return e is ViewDrafting drafting && drafting.ViewType == ViewType.DraftingView;
                case "create_sheet":
                    return e is ViewSheet sheet && NumberMatches(sheet, a.Action) &&
                           TitleBlockMatches(doc, sheet, a.Action.Value<long?>("title_block_type_id"));
                default:
                    return VerifyOther(doc, a, e, op);
            }
        }

        private static bool TitleBlockMatches(Document doc, ViewSheet sheet, long? expectedType)
        {
            // Sheet ownership includes title blocks hidden by the view's visibility settings.
            var titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().OwnedByView(sheet.Id);
            return expectedType.HasValue
                ? titleBlocks.Any(block => Rid.Value(block.GetTypeId()) == expectedType.Value)
                : !titleBlocks.Any();
        }

        /// <summary>
        /// A requested name is only a promise when the request GAVE one - a duplicate
        /// with no explicit name inherits Revit's own "Copy of ..." naming, which is
        /// not something this command chose and so not something it re-reads here.
        /// </summary>
        private static bool NameMatches(Element e, JObject action)
        {
            string want = action.Value<string>("name");
            if (string.IsNullOrWhiteSpace(want)) return true;
            string got;
            try { got = (e as View)?.Name; } catch { return false; }
            return got != null && string.Equals(got, want, StringComparison.Ordinal);
        }

        private static bool NumberMatches(ViewSheet sheet, JObject action)
        {
            string wanted = action.Value<string>("number");
            if (string.IsNullOrWhiteSpace(wanted)) return true;
            try { return string.Equals(sheet.SheetNumber, wanted, StringComparison.Ordinal); }
            catch { return false; }
        }

        private static void SetName(View view, JObject a) { string name = a.Value<string>("name"); if (!string.IsNullOrWhiteSpace(name)) view.Name = name; }

        private sealed class Applied
        {
            public int Index; public string Operation; public ElementId Id, TargetId, SheetId, ViewId;
            public JObject Action; public double Scale;
            public bool Centered { get; set; }
            /// <summary>5.2: a viewport of this batch Revit auto-numbered with a number a later place_view asked for, moved to a free one.</summary>
            public JObject Renumbered { get; set; }
            /// <summary>The whole batch, so a verifier can see what LATER actions did to its subject.</summary>
            public JArray Batch;
        }
    }
}
