using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private static CommandResult RehearseCreation(Document doc, JObject request, CreationBatch batch, out JArray provisionalGeometry)
        {
            provisionalGeometry = null;
            var plans = batch.Plans;
            var errors = batch.Errors;
            string validationMode = batch.ValidationMode;
            var mepGraphWarnings = batch.MepGraphWarnings;
            CommandResult apiRehearsal = null;
            // Footing elevation depends on Revit's host/type geometry. Measure a
            // provisional creation and prove it was removed before offering a preview.
            // 5.0.5, measured on a course model: the floor/wall overlap guard and the stairs level span are
            // measured on Revit's own geometry, so a rehearsal that only checked arguments said "valid" and the
            // apply then refused, writing nothing. Those batches are rehearsed in Revit and rolled back, so the
            // dry run refuses with the same reason the apply would.
            bool measuredOnApply = RehearsesGeometryChecks(doc, plans);
            if (errors.Count == 0 && (validationMode == "revit_rollback" || measuredOnApply ||
                plans.Any(p => p.Kind == "wall_foundation" ||
                    MayBuildObliqueWye(p) || RehearsesRunDiameter(p) ||
                    (p.Kind == "family_instance" && p.Host is Wall && p.Type is FamilySymbol symbol &&
                     (Rid.Value(symbol.Category?.Id) == (long)BuiltInCategory.OST_Doors ||
                      Rid.Value(symbol.Category?.Id) == (long)BuiltInCategory.OST_Windows)))))
            {
                apiRehearsal = ApplyPlans(doc, request, plans, batch.InputCount, true);
                if (!apiRehearsal.Success)
                {
                    if (apiRehearsal.Detail is JObject failed)
                    {
                        failed["dry_run"] = true;
                        failed["refused_by"] = "revit_rehearsal";
                        failed["apply_would_refuse"] = true;
                    }
                    return CreationResponse(apiRehearsal, plans, batch.Architecture, "SUPUESTO: rehearsal used assumed values. ");
                }
            }
            var rehearsalData = apiRehearsal?.Data as JObject;
            string transactionStatus = apiRehearsal == null ? ApplicationOutcome.NotStarted : rehearsalData.Value<string>("transaction_status");
            var result = new JObject
            {
                ["dry_run"] = true, ["tabular"] = batch.Tabular, ["enclosed"] = batch.Enclosed,
                ["transaction_status"] = transactionStatus, ["requested"] = batch.InputCount,
                ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors,
                ["plan"] = new JArray(plans.Select(p => p.Summary)),
                ["declared_exceptions"] = DeclaredExceptions(plans),
                ["validation_level"] = apiRehearsal == null ? "arguments_and_references" : "revit_construction_rolled_back",
                ["api_rehearsal"] = rehearsalData,
                ["note"] = apiRehearsal == null ? "No transaction opened; API construction and instance parameters are not rehearsed. Correct every invalid row before apply." : "Provisional elements were verified after an inner commit and the entire group was rolled back. No provisional elements remain."
            };

            CreationFieldErrors.StampBatch(result, errors);
            if (mepGraphWarnings.Count > 0) result["mep_graph_warnings"] = mepGraphWarnings;
            if (apiRehearsal == null && errors.Count == 0 && plans.Count > GeometryRehearsalRules.MaxAutomaticElements &&
                plans.Any(p => p.Kind == "wall" || p.Kind == "floor"))
                result["geometry_checks_note"] = GeometryRehearsalRules.SkippedNote;
            // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so
            // this is the first thing a caller sends; without the block here they
            // got success=true, invalid=1 and no way to tell a capability gap from
            // a typo except by sending an apply they had no reason to send.
            // Invalid entries make this a partial rehearsal, not a clean one: the token
            // below is already withheld for them, and a plan must read the same fact.
            ApplicationOutcome.Stamp(result, ApplicationOutcome.Declare(ApplicationOutcome.Rehearsal(errors.Count, 0, 0),
                transactionStatus, batch.InputCount, 0, 0, errors.Count, 0, 0));
            // Stamp before constructing CommandResult. The previous order happened to
            // work only because Ok retained the mutable JObject reference.
            CommandResult rehearsal = FallbackDecision.Attach(
                CommandResult.Ok(result),
                FallbackDecision.Decide(batch.Outcomes, writeStarted: false));
            provisionalGeometry = (apiRehearsal?.Data as JObject)?["provisional_geometry"] as JArray;
            return CreationResponse(rehearsal, plans, batch.Architecture);
        }

        private static bool MayBuildObliqueWye(Plan plan)
        {
            if (plan.FittingSubtype != "tee" || plan.FittingMembers.Any(m => m.Owner != null && !(m.Owner is Pipe)))
                return false;
            ConnectorFact run = plan.FittingMembers[0].Fact, branch = plan.FittingMembers[2].Fact;
            // Connectors for earlier entries of this batch are only available during construction.
            if (run == null || branch == null) return true;
            double angle = ObliquePipeTeeRules.AcuteBranchAngleDegrees(
                new[] { run.DirX, run.DirY, run.DirZ }, new[] { branch.DirX, branch.DirY, branch.DirZ });
            return ObliquePipeTeeRules.NeedsObliqueWorkaround(angle);
        }

        private static CommandResult InvalidCreation(CreationBatch batch)
        {
            string why = batch.Errors.Count + " element plan(s) are invalid. Nothing was created: " +
                         batch.Errors.ToString(Formatting.None);
            var refusal = FallbackDecision.Refuse(why, FallbackDecision.Decide(batch.Outcomes, writeStarted: false));
            var detail = new JObject { ["errors"] = batch.Errors };
            return CreationResponse(CreationFieldErrors.StampBatch(detail, batch.Errors) ? refusal.CarryDetails(detail) : refusal,
                batch.Plans, batch.Architecture);
        }

        private static CommandResult ApplyCreation(Document doc, JObject request, CreationBatch batch, CreationConfirmation confirmation)
        {
            var applied = ApplyPlans(doc, request, batch.Plans, batch.InputCount, confirmation: confirmation);
            if (applied.Data is JObject appliedData)
            {
                appliedData["tabular"] = batch.Tabular;
                JArray declared = DeclaredExceptions(batch.Plans);
                if (declared.Count > 0) appliedData["declared_exceptions"] = declared;
                if (batch.Enclosed != null) appliedData["enclosed"] = batch.Enclosed;
            }
            return CreationResponse(applied, batch.Plans, batch.Architecture);
        }

        private static CommandResult ApplyPlans(Document doc, JObject request, List<Plan> plans, int requested,
            bool rehearsal = false, CreationConfirmation confirmation = null)
        {
            if (plans.Count == 1 && plans[0].Kind == "stairs") return ApplyStairs(doc, plans[0], rehearsal);
            string name = request.Value<string>("transaction_name") ?? "Horizun: create elements";
            var created = new List<Created>(); var rows = new JArray(); var provisionalGeometry = new JArray(); bool started = false;
            int index = -1; string phase = "start";
            JObject roomConflict = null;
            var failureCapture = new BatchFailureCapture();
            // New walls may reshape an existing finish-floor sketch during regeneration.
            // Measure the existing finish floors before creation, including their other walls.
            bool guardFinishFloors = plans.Any(p => p.Kind == "wall" || p.Kind == "floor");
            var floorBefore = FloorWallOverlapGuard.Capture(doc, guardFinishFloors
                ? new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
                    .Where(f => FloorWallOverlapGuard.IsFinishFloor(doc, f)).Select(f => f.Id)
                : Enumerable.Empty<ElementId>());
            var overlapWarnings = new JArray(floorBefore.Warnings.Select(w => w.DeepClone()));
            using (var group = new TransactionGroup(doc, name))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Transaction group did not start.");
                    using (var tx = new Transaction(doc, name))
                    {
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("Transaction did not start.");
                        // Records what Revit says fails and changes nothing: it always answers Continue, so
                        // Revit's own handling of every warning and error is exactly what it was.
                        FailureHandlingOptions failureOptions = tx.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(failureCapture);
                        tx.SetFailureHandlingOptions(failureOptions);
                        started = true; phase = "create";
                        foreach (Plan plan in plans)
                        {
                            index = plan.Index;
                            Element element = Create(doc, plan, created);
                            if (element == null) throw new InvalidOperationException("Creation returned no element.");
                            RecordCreated(doc, plan, element, created);
                            ApplyInstanceParameters(element, plan);
                            if (plan.Input["source_reference"] is JObject trace) SourceTraceStorage.Write(element, trace);
                        }
                        doc.Regenerate();
                        AppendOverlapDiagnostics(overlapWarnings, FloorWallOverlapGuard.ThrowIfIncreased(doc, floorBefore, created.Select(c => c.Id)));
                        phase = "commit"; Guard.Commit(tx, name);
                    }
                    AppendOverlapDiagnostics(overlapWarnings, FloorWallOverlapGuard.ThrowIfIncreased(doc, floorBefore, created.Select(c => c.Id)));
                    phase = "postcondition";
                    var newRooms = created.Where(c => c.Kind == "room" && !c.Plan.Enclosed).ToDictionary(c => Rid.Value(c.Id));
                    if (newRooms.Count > 0)
                    {
                        // Revit diagnoses coincident plan regions in the new room's level and phase.
                        // A 3D probe can miss the region when the existing room's base offset moves.
                        long? redundant = ArchitectureCreationRules.RedundantCreatedRoom(newRooms.Keys, failureCapture.RoomRegionIds)
                            ?? ArchitectureCreationRules.RedundantCreatedRoom(newRooms.Keys, doc.GetWarnings()
                                .Where(w => IsRoomRegionWarning(w.GetFailureDefinitionId().Guid))
                                .SelectMany(w => w.GetFailingElements().Concat(w.GetAdditionalElements()))
                                .Select(Rid.Value));
                        if (redundant.HasValue)
                        {
                            Created conflicting = newRooms[redundant.Value];
                            index = conflicting.Index;
                            roomConflict = new JObject
                            {
                                ["room_status"] = "redundant",
                                ["level_id"] = Rid.Value(conflicting.Plan.Level.Id),
                                ["phase_id"] = PhaseOf(doc.GetElement(conflicting.Id))
                            };
                            throw new InvalidOperationException("Revit reported another room in the same enclosed region on level " +
                                roomConflict["level_id"] + " in phase " + roomConflict["phase_id"] + ".");
                        }
                    }
                    foreach (Created made in created)
                    {
                        index = made.Index; var row = VerifyCreatedWithReducer(doc, made); rows.Add(row);
                        if (row.Value<bool>("verified") != true)
                        {
                            // WHICH PROPERTY, NOT JUST THAT ONE FAILED.
                            //
                            // This message used to say only that the committed
                            // element disagreed with the request, and the whole
                            // batch rolled back around it - so the one fact a
                            // reader needs, and the only one this code has, was
                            // thrown away. Naming the properties turns a retry
                            // into a diagnosis.
                            // The production checklist (type, section, the declared ARC) and the read errors
                            // live beside the row's own postconditions: all of them are named (5.0.4, measured on
                            // curved scan walls refused with no property and no value).
                            throw new InvalidOperationException(row.Value<string>("reducer_ports_error") ?? CreatedRowMismatch.Message(row));
                        }
                        if (rehearsal && made.Kind == "wall_foundation")
                            provisionalGeometry.Add(new JObject
                            {
                                ["index"] = made.Index,
                                ["values"] = MeasureWallFoundation(doc, made)
                            });
                    }
                    if (rehearsal)
                    {
                        phase = "rehearsal_rollback";
                        var rolled = Guard.RollBack(group);
                        bool absent = rolled.Confirmed && created.All(x => doc.GetElement(x.Id) == null);
                        if (!absent) throw new InvalidOperationException("Rehearsal rollback could not be verified.");
                        var rehearsed = new JObject
                        {
                            ["dry_run"] = true,
                            ["transaction_status"] = rolled.StatusName,
                            ["changes_applied"] = false,
                            ["provisional_elements_absent"] = true,
                            ["provisional_verification"] = rows,
                            ["provisional_geometry"] = provisionalGeometry,
                            ["warnings"] = overlapWarnings,
                            ["revit_warnings"] = RevitWarnings(failureCapture)
                        };
                        AssumptionNotes.Stamp(rehearsed, plans.SelectMany(pl => pl.Assumptions.OfType<JObject>()));
                        return CommandResult.Ok(rehearsed);
                    }
                    string drift = confirmation?.BuiltPlanMismatch();
                    if (drift != null)
                    {
                        phase = "confirmation_postcondition";
                        throw new InvalidOperationException(drift);
                    }
                    phase = "assimilate"; Guard.Assimilate(group, name);
                }
                catch (Exception ex)
                {
                    string rollback = "not_attempted", rollbackError = null;
                    try { if (group.GetStatus() == TransactionStatus.Started) rollback = Guard.RollBack(group).StatusName; }
                    catch (Exception rb) { rollback = "failed"; rollbackError = rb.Message; }
                    bool? changed = started ? (bool?)null : false;
                    if (rollback == "RolledBack")
                    {
                        try { changed = created.Any(x => doc.GetElement(x.Id) != null) ? (bool?)null : false; }
                        catch { changed = null; }
                    }
                    // Which ITEM the failures name. A commit-time error ("Can't cut instance out of Wall") used to
                    // be reported at the last item processed; Revit's own failure messages say which created
                    // element is at fault, and this maps them back to the batch's numbering.
                    var createdIndex = new Dictionary<long, int>();
                    foreach (Created made in created) createdIndex[Rid.Value(made.Id)] = made.Index;
                    JArray attributed = FailureAttributionRules.Attribute(failureCapture.Facts, createdIndex);
                    // Batch numbering is the expanded one; these name the rows the caller sent.
                    var callerIndex = plans.GroupBy(pl => pl.Index).ToDictionary(g => g.Key,
                        g => Horizun.Revit.Core.CreationBatch<Plan>.SourceIndex(g.First().Input) ?? g.Key);
                    string attributionSentence = FailureAttributionRules.Sentence(attributed);
                    var detail = new JObject
                    {
                        ["failure_items"] = attributed,
                        ["culprit_items"] = new JArray(FailureAttributionRules.Culprits(attributed).Cast<object>().ToArray()),
                        ["code"] = phase == "postcondition" ? "geometry_postcondition_failed" : "revit_creation_failed",
                        ["tool"] = "horizun_create_elements",
                        ["operation"] = "create",
                        ["index"] = index,
                        ["elements_index"] = callerIndex.TryGetValue(index, out int callerRow) ? callerRow : index,
                        ["culprit_elements_index"] = new JArray(FailureAttributionRules.Culprits(attributed)
                            .Select(c => callerIndex.TryGetValue(c, out int row) ? row : c).Distinct().Cast<object>().ToArray()),
                        ["phase"] = phase,
                        ["exception_type"] = ExceptionTypeLogged(ex),
                        ["exception_message"] = ex.Message,
                        ["write_started"] = started,
                        ["changes_applied"] = changed,
                        ["transaction_status"] = group.GetStatus().ToString(),
                        ["rollback_status"] = rollback,
                        ["rollback_error"] = rollbackError,
                        ["verification"] = rows,
                        ["error_code"] = GeometryRehearsalRules.ConflictErrorCode(ex.Message)
                    };
                    if (roomConflict != null)
                    {
                        detail.Merge(roomConflict);
                        detail["error_code"] = ErrorCodes.PrecondicionNoCumplida;
                    }
                    if (ex is ReducerPortVerificationFailed || rows.Any(r => r["reducer_ports_error"] != null))
                        detail["error_code"] = ErrorCodes.VerificacionFallida;
                    if (phase == "confirmation_postcondition")
                    {
                        detail["state"] = "stale_plan";
                        detail["error_code"] = ErrorCodes.TokenInvalidoOVencido;
                    }
                    return CommandResult.FailWithDetail("Atomic creation failed at item " + index + ": " + ex.Message +
                        attributionSentence + RevitFailureHints.For(ex.Message + attributionSentence), detail);
                }
            }
            // Fresh reads after the outermost commit too. Failure here is reported as
            // committed/unverified: it cannot be erased by pretending rollback is possible.
            rows = new JArray(created.Select(x => VerifyCreatedWithReducer(doc, x)));
            int verified = rows.Count(r => r.Value<bool>("verified"));
            if (verified != created.Count) return CommandResult.FailWithDetail("Post-assimilation verification failed; inspect the model.", new JObject
            {
                ["code"] = "postcommit_verification_failed",
                ["error_code"] = ErrorCodes.VerificacionFallida,
                ["write_started"] = true,
                ["changes_applied"] = true,
                ["transaction_status"] = "Committed",
                ["verification"] = rows
            });
            var result = new JObject
            {
                ["dry_run"] = false,
                ["transaction_status"] = "Committed",
                ["transaction_name"] = name,
                ["requested"] = requested,
                ["created_verified"] = verified,
                ["warnings"] = overlapWarnings,
                ["revit_warnings"] = RevitWarnings(failureCapture),
                ["rows"] = rows,
                ["verification"] = new JObject { ["intended"] = requested, ["actual"] = verified, ["verified"] = verified == requested }
            };
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, requested, verified, verified, 0, 0, 0);
            // horizun_undo: created elements are deleted by the inverse.
            result["undo"] = UndoCapture.Record(doc, "horizun_create_elements", new List<UndoEntry>
            {
                UndoCapture.Entry(doc, "created", created.Select(x => Rid.Value(x.Id)), new JObject(), new JObject())
            });
            return CommandResult.Ok(result);
        }

        private static void AppendOverlapDiagnostics(JArray destination, JArray incoming)
        {
            if (destination == null || incoming == null) return;
            foreach (JObject item in incoming.OfType<JObject>())
            {
                bool exists = destination.OfType<JObject>().Any(old =>
                    old.Value<string>("reason_code") == item.Value<string>("reason_code") &&
                    old.Value<long?>("floor_id") == item.Value<long?>("floor_id") &&
                    old.Value<long?>("wall_id") == item.Value<long?>("wall_id") &&
                    old.Value<string>("measurement_method") == item.Value<string>("measurement_method"));
                if (!exists) destination.Add(item.DeepClone());
            }
        }

        /// <summary>
        /// The warnings Revit raised while the batch was created (review 2026-11-20, M5): they were captured and
        /// dropped on success, except the floor/wall overlap. Grouped by text, with the elements Revit named.
        /// </summary>
        private static JArray RevitWarnings(BatchFailureCapture capture)
        {
            return new JArray(capture.Facts.Where(f => !f.IsError && !string.IsNullOrWhiteSpace(f.Description))
                .GroupBy(f => f.Description)
                .Take(30)
                .Select(g => (JToken)new JObject
                {
                    ["text"] = g.Key,
                    ["count"] = g.Count(),
                    ["element_ids"] = new JArray(g.SelectMany(f => f.ElementIds).Distinct().Take(20).Cast<object>().ToArray())
                }));
        }

        /// <summary>The stack trace goes to the local log, not to the reply: it cost tokens and named internals.</summary>
        private static string ExceptionTypeLogged(Exception ex)
        {
            try { Log.Warn("create_elements failed: " + ex.GetType().FullName + ": " + ex.Message + Environment.NewLine + ex.StackTrace); } catch { }
            return ex.GetType().FullName;
        }
    }
}
