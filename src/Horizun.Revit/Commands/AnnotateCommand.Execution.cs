using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        public CommandResult Execute(UIApplication app, string paramsJson)
            => Execute(app, paramsJson, new AnnotationBudget(abandoned: CooperativeRead.Abandoned));

        internal CommandResult Execute(UIApplication app, string paramsJson, AnnotationBudget budget)
        {
            try { return ExecuteBatch(app, paramsJson, budget); }
            catch (AnnotationBudgetExceeded ex)
            {
                var detail = new JObject { ["state"] = DimensionPlanRules.StateRefused, ["write_started"] = false };
                ex.AddDetail(detail);
                return CommandResult.FailWithDetail(ex.Message + " Nothing was written.", detail);
            }
        }

        private CommandResult ExecuteBatch(UIApplication app, string paramsJson, AnnotationBudget budget)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            if (request["load_tag_family"] != null)
            {
                if (request["actions"] != null || request["on_failure"] != null || request["keep_active_view"] != null)
                    return CommandResult.Fail("load_tag_family is its own call: send it without actions, on_failure or keep_active_view, " +
                        "then annotate once the family is loaded. Nothing was changed.");
                return LoadTagFamilyEdit.Run(app, paramsJson, Name);
            }
            string onFailureError;
            string onFailure = AnnotationNoteRules.ParseOnFailure(request["on_failure"], out onFailureError);
            if (onFailure == null) return CommandResult.Fail(onFailureError + " Nothing was changed.");
            bool bestEffort = onFailure == AnnotationNoteRules.BestEffort;
            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document; JArray actions = request["actions"] as JArray;
            // The rehearsal itself opens a transaction, so a read-only document must be
            // refused in prose HERE - otherwise the very first dry run answers with a raw
            // Revit exception out of tx.Start() instead of a sentence about the document.
            bool docReadOnly; try { docReadOnly = doc.IsReadOnly; } catch { docReadOnly = false; }
            if (docReadOnly)
                return CommandResult.Fail("The active document is READ-ONLY, so nothing can be annotated in it - " +
                    "not even the dry run's provisional rehearsal, which creates and rolls back inside a " +
                    "transaction. Open the document writable and call again. Nothing was changed.");
            if (actions == null || actions.Count == 0 || actions.Count > 1000) return CommandResult.Fail("actions must contain 1..1000 entries.");
            double scale; if (!DimensionPlanRules.UnitScale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale)) return CommandResult.Fail("units must be mm, m or feet.");

            // Creation and rehearsal use the requested view. The active graphical view
            // only informs post-commit verification, where Revit may return false/0
            // for a dimension in a view that has never been displayed.
            long? activeGraphicalViewId = null;
            try
            {
                View agv = app?.ActiveUIDocument?.ActiveGraphicalView;
                if (agv != null) activeGraphicalViewId = Rid.Value(agv.Id);
            }
            catch { activeGraphicalViewId = null; }

            var plans = new List<Plan>(); var errors = new JArray();
            // Every action's outcome, so the fallback is decided once over the whole
            // batch: one uncovered operation must not grant permission for a request that
            // also contains input the caller should fix. See FallbackDecision.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < actions.Count; i++)
            {
                budget.Check();
                var action = actions[i] as JObject;
                var operation = action?["operation"];
                var slopeRefusal = operation?.Type == JTokenType.String
                    ? DimensionPlanRules.SpotSlopeRefusal((string)operation, i) : null;
                if (slopeRefusal != null)
                {
                    errors.Add(new JObject { ["index"] = i, ["error"] = slopeRefusal.Error });
                    outcomes.Add(slopeRefusal);
                    continue;
                }
                string error = null, reason = null;
                Plan p = PlanAction(doc, i, action, scale, activeGraphicalViewId, out error, out reason, out string errorCode);
                if (p == null)
                {
                    string message = error ?? "entry is not an object";
                    var row = new JObject { ["index"] = i, ["error"] = message };
                    if (errorCode != null) row["code"] = errorCode;
                    errors.Add(row);
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else { p.Budget = budget; p.LayoutBatch = plans; plans.Add(p); }
            }
            var datumRefusal = errors.OfType<JObject>().FirstOrDefault(row =>
                row.Value<string>("code") == DimensionReferenceRules.CodeLinkedDatumRejected);
            if (datumRefusal != null)
                return CommandResult.FailWithDetail(
                    CommandResult.WithNothingWritten("Annotation action " + datumRefusal.Value<int>("index") +
                        ": " + datumRefusal.Value<string>("error")),
                    new JObject { ["state"] = DimensionPlanRules.StateRefused,
                        ["code"] = DimensionReferenceRules.CodeLinkedDatumRejected, ["errors"] = errors,
                        ["write_started"] = false, ["changes_applied"] = false },
                    FallbackSignal.NotAllowed(DimensionReferenceRules.CodeLinkedDatumRejected, false), null);
            foreach (var familyGroup in plans.Where(p => p.RoomTagFamilyPath != null)
                .GroupBy(p => Path.GetFileNameWithoutExtension(p.RoomTagFamilyPath), StringComparer.OrdinalIgnoreCase))
                if (familyGroup.Select(p => p.RoomTagFamilyHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                {
                    string message = "The batch names more than one RoomTag RFA content for family '" + familyGroup.Key + "'.";
                    errors.Add(new JObject { ["index"] = familyGroup.First().Index, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = familyGroup.First().Index, Error = message });
                }
            var uidoc = app?.ActiveUIDocument;
            View originalView = uidoc?.ActiveView;
            long? viewId = DimensionPlanRules.TemporaryLinkedDimensionView(
                request.Value<bool?>("keep_active_view") == true,
                originalView == null ? (long?)null : Rid.Value(originalView.Id),
                plans.Where(p => p.Operation == DimensionPlanRules.OpDimension && p.RefLinks.Any(link => link != null))
                    .Select(p => Rid.Value(p.View.Id)));
            View temporaryView = errors.Count == 0 && uidoc?.Document.Equals(doc) == true && viewId.HasValue
                ? doc.GetElement(Rid.Make(viewId.Value)) as View : null;
            CommandResult result = null;
            bool activated = false;
            JObject restoration = null;
            JObject viewAssumption = temporaryView == null ? null : AssumptionNotes.Row(
                "Use view " + viewId + " temporarily for the linked dimension.",
                "Revit may reject linked references when the dimension view is inactive.",
                "Set keep_active_view=true to leave the active view untouched.");
            try
            {
                if (temporaryView != null)
                {
                    try
                    {
                        uidoc.ActiveView = temporaryView;
                        activated = uidoc.ActiveView?.Id == temporaryView.Id;
                        if (!activated)
                            throw new InvalidOperationException("Revit did not make the requested view active during this callback.");
                        foreach (Plan p in plans) p.TargetViewIsActive = p.View.Id == temporaryView.Id;
                    }
                    catch (Exception ex)
                    {
                        result = CommandResult.FailWithDetail("Could not activate the linked dimension view: " +
                            ex.Message + " Open that view and rehearse again.", new JObject { ["write_started"] = false });
                    }
                }
                if (result == null)
                    result = ExecutePlanned(app, request, gate, actions, plans, errors, outcomes, onFailure, bestEffort, budget);
            }
            finally
            {
                if (temporaryView != null)
                {
                    restoration = DimensionPlanRules.RestoreTemporaryView(Rid.Value(originalView.Id), viewId.Value, activated,
                        () => uidoc.ActiveView?.Id == originalView.Id,
                        () => uidoc.ActiveView = originalView,
                        () => uidoc.RequestViewChange(originalView));
                }
            }

            if (restoration != null)
                result = WithTemporaryViewRestoration(result, restoration);
            var notes = plans.Where(p => p.TagLayoutFallback).Select(p => AssumptionNotes.Row(
                "Use the least conflicting measured tag position for action " + p.Index + ".",
                "No clear position was confirmed within the displacement and probe limit.",
                "Adjust the point, layout_clearance or layout_max_displacement and rehearse again."));
            var assumptions = new[] { viewAssumption }.Concat(notes);
            return result.Success ? AssumptionNotes.StampResult(result, assumptions)
                : AssumptionNotes.MarkFailure(result, "SUPUESTO: ", assumptions);
        }

        internal static CommandResult WithTemporaryViewRestoration(CommandResult result, JObject restoration)
        {
            JObject payload = result.Data as JObject ?? result.Detail ?? new JObject();
            payload["temporary_view"] = restoration;
            if (!restoration.Value<bool>("restored"))
            {
                payload.Remove("confirmation_token");
                if (!result.Success)
                {
                    payload["annotation_error"] = result.Error;
                    if (payload.TryGetValue("error_code", out JToken annotationCode))
                    {
                        payload.Remove("error_code");
                        payload["annotation_error_code"] = annotationCode;
                    }
                }
                return CommandResult.FailCoded(ErrorCodes.VerificacionFallida,
                    "Could not confirm restoration of the original view. See temporary_view and the annotation outcome before retrying.",
                    extra: payload);
            }

            return !result.Success && result.Detail == null
                ? CommandResult.FailWithDetail(result.Error, payload, result.Fallback, result.CapabilityGaps)
                : result;
        }

        private CommandResult ExecutePlanned(UIApplication app, JObject request, GateResult gate, JArray actions,
            List<Plan> plans, JArray errors, List<ActionOutcome> outcomes, string onFailure, bool bestEffort, AnnotationBudget budget)
        {
            Document doc = gate.Document;
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string hash = request["keep_active_view"] == null
                ? DocumentGate.PlanHash(request, "units", "actions")
                : DocumentGate.PlanHash(request, "units", "actions", "keep_active_view");

            // ---- THE REHEARSAL: provisional creation, measurement, mandatory rollback. --
            // Run on BOTH paths whenever every action planned cleanly. On the dry run it
            // is the evidence a person approves; on the apply it recomputes the measured
            // values the materialised plan is fingerprinted over, so a model that moved
            // since the approved rehearsal refuses as stale instead of being written to.
            // Never run over a batch with invalid entries: rehearsing half a request
            // would publish constructibility about a batch that cannot be applied as-is,
            // and it would open a transaction on a call whose refusal claims none was.
            Rehearsal rehearsal = null;
            if (errors.Count == 0)
            {
                budget.Check();
                rehearsal = Rehearse(doc, plans, bestEffort, budget);
                if (rehearsal.BudgetFailure != null)
                {
                    var detail = new JObject
                    {
                        ["state"] = rehearsal.RollbackConfirmed ? DimensionPlanRules.StateRolledBack : DimensionPlanRules.StateUncertain,
                        ["rehearsal_rollback_status"] = rehearsal.RollbackStatus,
                        ["rollback_confirmed"] = rehearsal.RollbackConfirmed,
                        ["write_started"] = true
                    };
                    rehearsal.BudgetFailure.AddDetail(detail);
                    return CommandResult.FailWithDetail(rehearsal.BudgetFailure.Message +
                        (rehearsal.RollbackConfirmed
                            ? " The rehearsal rolled back. Nothing was committed."
                            : " Revit did not confirm rehearsal rollback: " + rehearsal.RollbackStatus + "."), detail);
                }
                if (!rehearsal.RollbackConfirmed)
                {
                    // The one outcome that may not be smoothed: provisional annotations
                    // were created and Revit did not confirm their removal. No token, no
                    // claim of a clean model, and the state says so.
                    return CommandResult.FailWithDetail(
                        "The rehearsal transaction could not be rolled back: Revit reported '" +
                        rehearsal.RollbackStatus + "', not RolledBack. The model may still carry the provisional " +
                        "annotations, so the state of this call is UNCERTAIN - no confirmation token is issued and " +
                        "nothing is claimed clean. Re-read the model before anything else.",
                        new JObject
                        {
                            ["state"] = DimensionPlanRules.StateUncertain,
                            ["rehearsal_rollback_status"] = rehearsal.RollbackStatus,
                            ["write_started"] = true,
                            ["rehearsal"] = rehearsal.ToJson()
                        });
                }
            }

            // ---- The MATERIALISED plan: the VIEW and TARGET each annotation lands on. ---
            // hash binds the actions as written. An annotation is ABOUT something: a tag
            // points at a target element, a dimension hangs off references measured from
            // one, and everything lands on a view. A tag approved against "Bomba 5" that
            // gets applied after somebody swaps that element is a label telling a reader
            // the wrong thing in print - the quietest wrong answer a model can produce.
            // So each row records the view and the target as resolved now, by identity
            // and by name; a dimension row additionally binds the EFFECTIVE type, every
            // reference (reserialized, with its owner, kind and geometry fingerprint at
            // 0.1 mm), the requested geometry, and the value the rehearsal measured.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            // best_effort applies only the rows its own rehearsal proved constructible; that
            // SET is bound into the hash and the resolved plan, so a token approved for rows
            // {0,2,5} can never create a different set.
            List<Plan> applyPlans = bestEffort && rehearsal != null
                ? plans.Where(p => p.Constructible).ToList()
                : plans;
            hash = AnnotationNoteRules.BindApplySet(hash, onFailure, applyPlans.Select(p => p.Index));
            foreach (Plan planned in applyPlans) resolvedPlan.Elements.Add(PlannedRow(planned));

            if (dry)
            {
                bool constructible = rehearsal != null &&
                    (bestEffort ? applyPlans.Count > 0 : rehearsal.AllConstructible);
                var result = new JObject { ["dry_run"] = true, ["valid"] = plans.Count, ["invalid"] = errors.Count,
                    ["errors"] = errors, ["plan"] = new JArray(plans.Select(p => PlanRow(p))) };
                if (bestEffort)
                {
                    result["on_failure"] = onFailure;
                    List<int> willApply, willSkip;
                    AnnotationNoteRules.ApplySets(actions.Count, errors.Select(e => e.Value<int>("index")),
                        rehearsal == null ? null : applyPlans.Select(p => p.Index), out willApply, out willSkip);
                    result["will_apply"] = new JArray(willApply);
                    result["will_skip"] = new JArray(willSkip);
                    if (errors.Count > 0)
                        result["will_skip_note"] = "the batch is rejected (" + errors.Count + " invalid action(s), see errors): nothing would be applied and " +
                            "no token is issued, so will_skip names every index; the valid ones were not rehearsed.";
                }
                result["rehearsal"] = rehearsal == null
                    ? (JToken)JValue.CreateNull()
                    : rehearsal.ToJson();
                if (rehearsal == null)
                    result["rehearsal_note"] = "The batch was NOT rehearsed: " + errors.Count + " action(s) are " +
                        "invalid, so no transaction was opened and nothing was provisionally created.";
                if (errors.Count == 0 && constructible) DocumentGate.RecordResolvedPlan(resolvedPlan);
                // Invalid or non-constructible entries make this a partial rehearsal, not
                // a clean one: the token below is already withheld for them, and the plan
                // must read the same fact.
                // best_effort with a token: the batch IS approvable (the token binds will_apply), so the state is "rehearsed" and the
                // rows that would be skipped are counted apart (application.skipped), not called failed - a "rejected" state beside a
                // token contradicted itself. Anything else (invalid actions, or all-or-nothing with a row that cannot be built) stays
                // "rejected" exactly as before.
                int skippedRows = rehearsal == null ? 0 : rehearsal.NotConstructibleCount;
                if (bestEffort && errors.Count == 0 && constructible)
                {
                    ApplicationOutcome.StampRehearsal(result, plans.Count, 0, 0, 0);
                    var application = result[ApplicationOutcome.Key] as JObject;
                    if (application != null)
                    {
                        application["skipped"] = skippedRows;
                        if (skippedRows > 0)
                            application["state_note"] = "best_effort: the token covers only the rows in will_apply; the " + skippedRows +
                                " row(s) in will_skip would not be applied (they are skipped, not failed).";
                    }
                    if (skippedRows > 0)
                        result["will_skip_note"] = "best_effort: the confirmation token covers only will_apply; the rows in will_skip (" + skippedRows +
                            ") cannot be built and would not be applied - see their reasons in rehearsal.actions.";
                }
                else
                    ApplicationOutcome.StampRehearsal(result, plans.Count + errors.Count, errors.Count, skippedRows, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, errors.Count == 0 && constructible,
                    errors.Count == 0 && constructible
                        ? (bestEffort ? "best_effort: the token binds the exact set of rows in will_apply; " : "") +
                          "the token binds views, geometry, text, the identity of every view and target, and - for " +
                          "dimensions - the EFFECTIVE dimension type, every reference's reserialized identity, owner " +
                          "and 0.1 mm geometry fingerprint, and the measured value of this rehearsal. A model that " +
                          "moves before you spend it refuses as a stale plan rather than dimensioning something else."
                        : errors.Count > 0
                            ? "no usable token while invalid"
                            : "no usable token: the rehearsal could not construct every annotation - see rehearsal " +
                              "rows for Revit's own reason per action");
                // StampConfirmation only writes a note when it ISSUES a token, so the two
                // withheld cases must say so themselves - an absent token with no sentence
                // reads like a bug in the caller's parsing, not like a decision.
                if (!(errors.Count == 0 && constructible))
                    result["confirmation_note"] = errors.Count > 0
                        ? "NO token was issued: " + errors.Count + " action(s) are invalid. Fix them and re-run " +
                          "the dry run; a partial batch is never approvable."
                        : "NO token was issued: the rehearsal could not construct every annotation against the " +
                          "current model - see the rehearsal rows for Revit's own reason per action.";
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so this
                // is the first call a caller makes; without the block here they got
                // success=true with invalid rows and no way to tell a capability gap
                // from a typo except by sending an apply they had no reason to send.
                // writeStarted stays false: the provisional-creation transaction only ever
                // runs when EVERY action planned cleanly, so on any path where a fallback
                // could be granted (some action failed) no transaction was opened.
                return FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
            {
                // NOTHING RAN - no transaction was opened - so the decision is only about
                // what failed, and it is made centrally.
                return FallbackDecision.Refuse(
                    "Invalid annotations; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (bestEffort && applyPlans.Count == 0)
                return CommandResult.FailWithDetail(
                    "Refused: on_failure=best_effort, but none of the " + plans.Count + " annotation(s) is constructible " +
                    "against the current model - see the rehearsal rows for Revit's reason per action. Nothing was committed.",
                    new JObject { ["state"] = DimensionPlanRules.StateRefused, ["rehearsal"] = rehearsal.ToJson() });
            if (!bestEffort && !rehearsal.AllConstructible)
            {
                // The apply's own rehearsal could not build the batch against the model as
                // it stands NOW. Refusing here is what keeps the real transaction from
                // discovering the same failure half-way through a write.
                return CommandResult.FailWithDetail(
                    "Refused: " + rehearsal.NotConstructibleCount + " of " + plans.Count + " annotation(s) are not " +
                    "constructible against the current model - see the rehearsal rows for Revit's reason per action. " +
                    "Nothing was committed: the rehearsal transaction rolled back (Revit reported '" +
                    rehearsal.RollbackStatus + "').",
                    new JObject
                    {
                        ["state"] = DimensionPlanRules.StateRefused,
                        ["rehearsal"] = rehearsal.ToJson()
                    });
            }
            // Recomputed by THIS call's own PlanAction resolution AND its own rehearsal:
            // the fingerprint carries the measured values, so the stale check compares
            // what the model would build now against what was approved.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name); if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name"); if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: annotate";
            budget.Check();
            return ApplyConfirmed(doc, txName, plans, bestEffort, applyPlans, rehearsal, budget);
        }

        // ---------------------------------------------------------------------
        // The rehearsal: provisional creation, measurement, MANDATORY rollback.
        // ---------------------------------------------------------------------
        private Rehearsal Rehearse(Document doc, List<Plan> plans, bool perRow, AnnotationBudget budget)
        {
            var re = new Rehearsal { PerRow = perRow };
            int failedIndex = -1; string failedWhy = null, regenWhy = null;
            var attempted = new HashSet<int>();
            // on_failure=best_effort: each row is created inside its own SubTransaction, so
            // a row Revit refuses (a tag with no tag family loaded for its category) is
            // rolled back ALONE and the others are still created and measured. The outer
            // transaction is rolled back at the end exactly as in the atomic rehearsal.
            var rowFailures = new Dictionary<int, string>();
            using (var tx = new Transaction(doc, "Horizun: rehearse annotate"))
            {
                tx.Start();
                var opts = tx.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new SilenceWarnings());
                opts.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(opts);
                try
                {
                    RunRehearsal();
                }
                catch (AnnotationBudgetExceeded ex)
                {
                    re.BudgetFailure = ex;
                }
                finally
                {
                    try
                    {
                        Guard.RollbackResult rb = Guard.RollBack(tx);
                        re.RollbackStatus = rb.StatusName;
                        re.RollbackConfirmed = rb.Confirmed;
                    }
                    catch (Exception rex)
                    {
                        re.RollbackStatus = "RollBack threw: " + rex.GetType().Name + ": " + rex.Message;
                        re.RollbackConfirmed = false;
                    }
                }
            }
            // Ids from a rolled-back transaction must never leak into a later phase.
            foreach (Plan p in plans) p.Created = null;
            // A rehearsal can run out of time (or lose its caller) during rollback.
            // Keep the rollback evidence and refuse confirmation even in that case.
            if (re.BudgetFailure == null)
            {
                try { budget.Check(); }
                catch (AnnotationBudgetExceeded ex) { re.BudgetFailure = ex; }
            }
            re.AllConstructible = re.NotConstructibleCount == 0;
            return re;

            void RunRehearsal()
            {
                // Paper calibration of every text type in the batch (a one-line and a two-line probe, created and
                // rolled back in their own sub-transaction BEFORE any row), so the printed lines can be counted.
                CalibrateText(doc, plans);
                budget.Check();

                foreach (Plan p in plans)
                {
                    budget.StartRow(p.Index);
                    attempted.Add(p.Index);
                    p.Constructible = false;
                    if (perRow)
                    {
                        using (var st = new SubTransaction(doc))
                        {
                            st.Start();
                            try
                            {
                                Element e = Create(doc, p);
                                if (e == null) throw new InvalidOperationException("Revit returned no element");
                                p.Created = e.Id;
                                if (p.Operation == "tag" && p.RoomTagFamilyPath == null) p.EffectiveTagType = doc.GetElement(e.GetTypeId()) as ElementType;
                                Dimension d = e as Dimension; if (d != null) Decorate(d, p);
                                Guard.Commit(st, "rehearse row " + p.Index);
                            }
                            catch (Exception ex)
                            {
                                p.Created = null; p.EffectiveTagType = null; p.TagCoverage = null;
                                rowFailures[p.Index] = p.Operation == DimensionPlanRules.OpSpotElevation
                                    ? SpotElevationRules.FailureHint(ex.Message) : ex.Message;
                                // The outer rehearsal transaction is rolled back regardless; an
                                // unconfirmed row rollback is still named in that row's reason.
                                try
                                {
                                    if (st.GetStatus() == TransactionStatus.Started)
                                    {
                                        Guard.RollbackResult rbRow = Guard.RollBack(st);
                                        if (!rbRow.Confirmed) rowFailures[p.Index] += " (row rollback reported '" + rbRow.StatusName + "')";
                                    }
                                }
                                catch { }
                                if (ex is AnnotationBudgetExceeded) throw;
                            }
                        }
                        continue;
                    }
                    try
                    {
                        Element e;
                        if (p.LinkedElement != null)
                        {
                            // A tag on a linked element can make Revit throw InternalException from inside
                            // the call; inside a SubTransaction the failure is rolled back alone and the
                            // rehearsal transaction stays healthy enough to report it.
                            using (var lst = new SubTransaction(doc))
                            {
                                lst.Start();
                                try { e = Create(doc, p); lst.Commit(); }
                                catch { try { if (lst.GetStatus() == TransactionStatus.Started) Guard.RollBack(lst); } catch { } throw; }
                            }
                        }
                        else e = Create(doc, p);
                        if (e == null) { failedIndex = p.Index; failedWhy = "Revit returned no element"; break; }
                        p.Created = e.Id;
                        if (p.Operation == "tag" && p.RoomTagFamilyPath == null) p.EffectiveTagType = doc.GetElement(e.GetTypeId()) as ElementType;
                        Dimension d = e as Dimension; if (d != null) Decorate(d, p);
                    }
                    catch (Exception ex) when (!(ex is AnnotationBudgetExceeded))
                    {
                        failedIndex = p.Index;
                        failedWhy = p.Operation == DimensionPlanRules.OpSpotElevation
                            ? SpotElevationRules.FailureHint(ex.Message) : ex.Message;
                        break;
                    }
                }
                if (failedIndex < 0)
                {
                    budget.Check();
                    try { doc.Regenerate(); } catch (Exception ex) when (!(ex is AnnotationBudgetExceeded)) { regenWhy = ex.Message; }
                }
                // Measure INSIDE the transaction - the mandatory rollback below erases
                // the evidence a moment later.
                foreach (Plan p in plans)
                {
                    budget.Check();
                    JObject row;
                    bool constructible = false;
                    if (perRow && rowFailures.ContainsKey(p.Index) && regenWhy == null)
                    {
                        row = new JObject
                        {
                            ["index"] = p.Index, ["operation"] = p.Operation,
                            ["constructible"] = false,
                            ["reason"] = rowFailures[p.Index]
                        };
                    }
                    else if (failedIndex >= 0 || regenWhy != null)
                    {
                        string reason = p.Index == failedIndex
                            ? failedWhy
                            : regenWhy != null && attempted.Contains(p.Index)
                                ? "the rehearsal could not regenerate after creating the batch: " + regenWhy
                                : attempted.Contains(p.Index)
                                    ? "created, then action " + failedIndex + " failed before the batch could be " +
                                      "measured; the shared rehearsal transaction was rolled back"
                                    : "not attempted: action " + failedIndex + " failed first and the shared " +
                                      "rehearsal transaction was rolled back";
                        row = new JObject
                        {
                            ["index"] = p.Index, ["operation"] = p.Operation,
                            ["constructible"] = p.Index == failedIndex || regenWhy != null
                                ? (JToken)false
                                : JValue.CreateNull(),
                            ["reason"] = reason
                        };
                    }
                    else if (DimensionPlanRules.IsDimensionOperation(p.Operation))
                    {
                        Element e = p.Created == null ? null : doc.GetElement(p.Created);
                        Measured m = Measure(doc, p, e);
                        p.Rehearsed = m;
                        JObject verification = DimensionPlanRules.RehearseDimension(
                            stage => BuildChecks(p, m, m, stage, out _), out constructible);
                        row = new JObject
                        {
                            ["index"] = p.Index, ["operation"] = p.Operation,
                            ["constructible"] = constructible,
                            ["reason"] = constructible
                                ? null
                                : (m.Unreadable ?? m.DroppedMessage ??
                                   (p.RequirePrintable ? DimensionPrintRules.RequirePrintableError(p.Index, PrintVerdict(m, false), false) : null) ??
                                   CommandResult.MismatchReason(verification)),
                            ["will_print"] = DimensionPaper.VerdictJson(PrintVerdict(m, false)),
                            ["pattern_resolutions"] = p.PatternResolutions == null ? null : p.PatternResolutions.DeepClone(),
                            ["evidence"] = Evidence(m),
                            ["verification"] = verification
                        };
                        // The expected_value postcondition is deliberately NOT part of
                        // constructibility: it belongs to the APPLY, where its failure
                        // rolls the real batch back. Here it is previewed as evidence.
                        if (p.ExpectedValueFeet.HasValue)
                        {
                            double? total = m.Total;
                            bool materialised = m.ReferencesAvailable == true && total.HasValue;
                            row["expected_value_preview"] = new JObject
                            {
                                // The *_internal_feet names hold RADIANS for an angular
                                // dimension - Revit's internal angle unit - so the unit is
                                // said out loud instead of letting the field name lie.
                                ["unit"] = p.Operation == DimensionPlanRules.OpAngular ? "radians" : "feet",
                                ["expected_internal_feet"] = p.ExpectedValueFeet.Value,
                                ["tolerance_internal_feet"] = p.ExpectedToleranceFeet,
                                ["measured_internal_feet"] = materialised ? (JToken)total.Value : JValue.CreateNull(),
                                // Null, not false, when Revit has not materialised the
                                // value yet: pre-commit a correct dimension measures 0,
                                // and previewing that as "would fail" reads like a
                                // verdict. The postcondition itself runs post-commit.
                                ["would_pass"] = materialised
                                    ? (JToken)(Math.Abs(total.Value - p.ExpectedValueFeet.Value) <= p.ExpectedToleranceFeet)
                                    : JValue.CreateNull(),
                                ["note"] = materialised
                                    ? null
                                    : "Revit materialises dimension values at commit; the postcondition is " +
                                      "checked post-commit inside the still-open TransactionGroup."
                            };
                        }
                    }
                    else
                    {
                        Element e = p.Created == null ? null : doc.GetElement(p.Created);
                        string why;
                        constructible = Verify(p, e, out why);
                        if (constructible && e is IndependentTag measuredTag)
                            p.TagRehearsal = AnnotationLayout.Evidence(measuredTag,p.View);
                        row = new JObject
                        {
                            ["index"] = p.Index, ["operation"] = p.Operation,
                            ["constructible"] = constructible,
                            ["reason"] = constructible ? null : (why ?? "created, but the annotation would not verify " +
                                                                "(see this command's text/tag verification)")
                        };
                        // The text Revit gave the provisional tag: what a person approves is what it will read.
                        if (p.Operation == "tag")
                        {
                            try { row["tag_text"] = (e as IndependentTag)?.TagText ?? (e as SpatialElementTag)?.TagText; }
                            catch { row["tag_text"] = null; }
                            MarkEmptyTagText(row, p, row.Value<string>("tag_text"), row["tag_text"] != null && row["tag_text"].Type != JTokenType.Null);
                        }
                        if (p.TagCoverage != null && p.TagCoverage.HasValues)
                            row["annotation_coverage"] = p.TagCoverage.DeepClone();
                        AddTextEvidence(row, p);
                    }
                    if (!(row["constructible"] is JValue jc && jc.Type == JTokenType.Boolean && (bool)jc.Value))
                        re.NotConstructibleCount++;
                    else p.Constructible = true;
                    re.Rows.Add(row);
                }
                budget.Check();
            }
        }

        // ---------------------------------------------------------------------
        // The materialised plan rows and the dry-run plan rows.
        // ---------------------------------------------------------------------
        private PlannedElement PlannedRow(Plan planned)
        {
            if (!DimensionPlanRules.IsDimensionOperation(planned.Operation))
            {
                var row = new PlannedElement
                {
                    UniqueId = "action:" + planned.Index,
                    Category = planned.Operation,
                    Action = PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "view", SafePlanIdName(planned.View) },
                        { "target", SafePlanIdName(planned.Target) },
                        { "linked_element", planned.LinkedElement == null ? "" : planned.LinkedElement.Id.ToString() + "@" + (planned.LinkInstance == null ? "" : planned.LinkInstance.GetLinkDocument()?.Title) },
                        { "type", SafePlanIdName(planned.Operation == "tag" ? (planned.Type ?? planned.EffectiveTagType) : planned.Type) },
                        { "existing_tags_for_target_in_view", planned.ExistingTagCount.ToString(CultureInfo.InvariantCulture) },
                        { "tag_rehearsal", planned.TagRehearsal?.ToString(Formatting.None) ?? "" },
                        { "room_tag_family_path", planned.RoomTagFamilyPath ?? "" },
                        { "room_tag_family_sha256", planned.RoomTagFamilyHash ?? "" },
                        { "room_tag_family_name", planned.RoomTagFamilyName ?? "" },
                        { "room_tag_type_name", planned.RoomTagTypeName ?? "" },
                        { "references", planned.References == null ? "" :
                              planned.References.Size.ToString(CultureInfo.InvariantCulture) }
                    }
                };
                BindTextPlan(planned, row.BeforeValues);
                if (planned.TagLayoutFallback) row.BeforeValues["tag_layout_fallback"] = "true";
                // Only when present, so a plain tag's binding stays byte-identical: the identity of
                // every further element the tag labels (a swap under the same id refuses as stale).
                if (planned.ExtraTargets != null)
                    row.BeforeValues["also_tags"] = string.Join(";", planned.ExtraTargets.Select(SafePlanIdName));
                return row;
            }
            var before = new Dictionary<string, string>
            {
                { "view", SafePlanIdName(planned.View) },
                { "type", SafePlanIdName(planned.EffectiveType) },
                { "type_source", planned.TypeFromDefault ? "default" : "explicit" },
                { "references", planned.RefList.Count.ToString(CultureInfo.InvariantCulture) }
            };
            for (int i = 0; i < planned.RefList.Count; i++)
            {
                before["ref." + i.ToString(CultureInfo.InvariantCulture)] =
                    planned.RefReserialized[i] + "|" + planned.RefOwnerUids[i] + "|" +
                    planned.RefTypes[i] + "|" + planned.RefFingerprints[i];
                // The link facts are a SEPARATE key rather than more text on the line
                // above, so a host reference's binding is byte-identical to what it was
                // before links were supported and no existing plan changes shape.
                LinkBinding link = planned.RefLinks[i];
                if (link != null)
                    before["ref." + i.ToString(CultureInfo.InvariantCulture) + ".link"] =
                        link.InstanceUniqueId + "|" + link.DocumentIdentity + "|" +
                        link.LinkedElementUniqueId + "|" + link.TransformFingerprint;
            }
            if (planned.Line != null)
                before["line"] = Canon(planned.Line.GetEndPoint(0)) + ";" + Canon(planned.Line.GetEndPoint(1));
            if (planned.ArcCenter != null)
                before["arc"] = Canon(planned.ArcCenter) + ";" + DimensionPlanRules.CanonicalFeet(planned.ArcRadius);
            if (planned.SpotOrigin != null)
            {
                before["point"] = Canon(planned.SpotOrigin);
                before["bend"] = Canon(planned.SpotBend);
                before["end"] = Canon(planned.SpotEnd);
                before["leader"] = planned.SpotLeader ? "true" : "false";
            }
            before["measured"] = MeasuredCanonical(planned.Rehearsed);
            return new PlannedElement
            {
                UniqueId = "action:" + planned.Index,
                Category = planned.Operation,
                Action = PlannedAction.Create,
                GeometryFingerprint = DimensionPlanRules.CombineFingerprints(planned.RefFingerprints),
                BeforeValues = before
            };
        }

        /// <summary>
        /// What the rehearsal measured, canonically. It goes INTO the plan fingerprint,
        /// so the apply's own rehearsal must reproduce it or the token refuses stale -
        /// which is exactly the property "the model moved" needs.
        /// </summary>
        private static string MeasuredCanonical(Measured m)
        {
            if (m == null) return "";
            var parts = new List<string>();
            double? total = m.Total;
            parts.Add(total.HasValue ? DimensionPlanRules.CanonicalFeet(total.Value) : "");
            if (m.SegmentValues != null)
                foreach (double v in m.SegmentValues) parts.Add(DimensionPlanRules.CanonicalFeet(v));
            if (m.SpotOrigin != null)
                parts.Add(DimensionPlanRules.CanonicalPoint(m.SpotOrigin[0], m.SpotOrigin[1], m.SpotOrigin[2]));
            return string.Join("|", parts);
        }

        private sealed class Rehearsal
        {
            public bool RollbackConfirmed;
            public AnnotationBudgetExceeded BudgetFailure;
            public string RollbackStatus = PlanFailure.NotAttempted;
            public bool AllConstructible;
            public bool PerRow;
            public int NotConstructibleCount;
            public JArray Rows = new JArray();

            public JObject ToJson() => new JObject
            {
                ["transaction_status"] = RollbackStatus,
                ["rolled_back_confirmed"] = RollbackConfirmed,
                ["all_constructible"] = AllConstructible,
                ["not_constructible"] = NotConstructibleCount,
                ["note"] = (PerRow ? "on_failure=best_effort: each row was tried in its own sub-transaction, so one row " +
                                     "that failed did not stop the others. " : "") +
                           "Every annotation was created PROVISIONALLY inside one transaction, measured, and the " +
                           "transaction was rolled back; transaction_status is what Revit's RollBack() returned. " +
                           "Nothing from the rehearsal remains in the model when rolled_back_confirmed is true.",
                ["actions"] = Rows
            };
        }

    }
}
