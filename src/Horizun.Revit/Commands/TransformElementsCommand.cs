// -----------------------------------------------------------------------------
// Horizun Revit MCP - generic transforms with post-commit location verification.
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
    public sealed partial class TransformElementsCommand : ICommand
    {
        public string Name => "horizun_transform_elements";
        public string Description => "Move, copy, rotate, pin, change type, attach walls to a roof/floor/ceiling or edit a wall profile; atomic, verified from the committed model.";

        private CommandResult ExecuteTransform(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray input = request["operations"] as JArray;
            if (input == null || input.Count == 0) return CommandResult.Fail("operations is required and must be non-empty.");
            if (input.Count > 500) return CommandResult.Fail("operations exceeds 500 entries.");
            int estimatedTargets = input.OfType<JObject>().Sum(o => (o["element_ids"] as JArray)?.Count ?? 1);
            if (!AnnotationMoveRules.WithinBudget(input.Count, estimatedTargets))
                return CommandResult.FailWithDetail(TransformFailure.BudgetMessage(string.Join(", ", input.OfType<JObject>().SelectMany(o => (o["element_ids"] as JArray ?? new JArray()).Select(id => id.ToString(Formatting.None))))),
                    new JObject { ["code"] = "rehearsal_budget_exceeded", ["error_code"] = ErrorCodes.PrecondicionNoCumplida, ["estimated_seconds"] = AnnotationMoveRules.EstimatedSeconds(input.Count, estimatedTargets),
                        ["budget_seconds"] = 15, ["write_started"] = false });
            // rename_level rehearses and reports Copy/Monitor alerts for the WHOLE
            // transaction (RevitErrorRecorder has no per-operation scope), so mixing it
            // with anything else would blur which operation raised what.
            if (input.Count != 1 && input.OfType<JObject>().Any(x => (x.Value<string>("operation") ?? "").ToLowerInvariant() == "rename_level"))
                return CommandResult.Fail("rename_level must be the sole operation in its batch: the Copy/Monitor " +
                    "rehearsal and the view census read the whole transaction, and mixing operations would blur " +
                    "which one raised them.");

            // realign_wall_sketch takes its own path (see TransformElementsCommand.RealignWallSketch.cs):
            // it opens a SketchEditScope, which cannot nest inside the single Transaction every other
            // operation below shares, and its dry_run is a REAL rehearsal rather than a read-only one.
            // Mixing it with another operation in one call is refused rather than silently split.
            bool anyRealign = input.Any(t => string.Equals((t as JObject)?.Value<string>("operation"), RealignOp, StringComparison.OrdinalIgnoreCase));
            if (anyRealign)
            {
                if (!input.All(t => string.Equals((t as JObject)?.Value<string>("operation"), RealignOp, StringComparison.OrdinalIgnoreCase)))
                    return CommandResult.Fail(RealignOp + " cannot be mixed with other operations in one call: it opens its own " +
                        "SketchEditScope per wall and its dry_run actually rehearses the move, unlike every other operation " +
                        "here. Send it alone.");
                return ExecuteRealignWallSketch(app, gate, request, input);
            }
            // edit_sketch: the same SketchEditScope path, one element per call (TransformElementsCommand.EditSketch.cs).
            if (input.Any(t => string.Equals((t as JObject)?.Value<string>("operation"), EditSketchOp, StringComparison.OrdinalIgnoreCase)))
                return ExecuteEditSketch(app, gate, request, input);
            // column_attach / column_detach: a real rehearsal of its own (TransformElementsCommand.ColumnAttach.cs).
            if (input.Any(t => ColumnAttachRules.IsColumnOperation((t as JObject)?.Value<string>("operation"))))
                return ExecuteColumnAttach(app, gate, request, input);

            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            // A GROUP MEMBER IS REFUSED BEFORE ANYTHING ELSE. Measured (Revit 2025): MoveElement on a
            // member of a multi-instance group returns without error and moves nothing, then the
            // commit fails. Refused here with its own code and the ways through, never left to a
            // post-commit mismatch or, worse, a Python retry that hits the same wall.
            CommandResult memberRefusal = RefuseGroupMembers(doc, input);
            if (memberRefusal != null) return memberRefusal;

            var plans = new List<Plan>();
            var errors = new JArray();
            var claimed = new HashSet<long>();
            // Every action's outcome, so the fallback is decided once over the whole
            // batch rather than granted because one entry was uncovered.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < input.Count; i++)
            {
                string error = null, reason = null;
                Plan plan = PlanOperation(doc, i, input[i] as JObject, scale, claimed, out error, out reason);
                if (plan == null)
                {
                    string message = error ?? "entry is not an object";
                    errors.Add(TransformFailure.Planning(i, message));
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else plans.Add(plan);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "operations");

            // ---- The MATERIALISED plan: the ELEMENTS this resolves to, right now. ----
            // planHash above binds the REQUEST - the operations as written. It cannot see
            // that the model moved: an operation naming a filter, or a type whose instances
            // changed, resolves to a different set of elements without a single character of
            // the request changing. Built identically here on both paths, so the dry run's
            // fingerprint rides in the token and the apply recomputes it.
            //
            // CONTRIBUTING points new commands at this file as the shape to copy, which is
            // exactly why the plan belongs here: whatever this command does, the next ten
            // will do.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Plan p in plans)
            {
                resolvedPlan.Elements.AddRange(p.JoinNeighbors.Values);
                foreach (ElementId id in (p.Ids ?? new List<ElementId>()))
                {
                    Element e = doc.GetElement(id);
                    if (e == null) continue;
                    var planned = new PlannedElement
                    {
                        UniqueId = SafePlanUniqueId(e),
                        ElementId = Rid.Value(e.Id),
                        Category = e.Category == null ? null : e.Category.Name,
                        TypeName = SafePlanTypeName(doc, e),
                        // change_type REPLACES the type; move/rotate/copy do not. Both are
                        // modifications of this element, and 'copy' additionally creates -
                        // but the created ids do not exist yet at plan time, so what is
                        // fingerprinted is the SOURCE set and the operation, which is what
                        // the caller actually approved.
                        Action = PlannedAction.Modify,
                        // The geometry the operation is ABOUT. A move approved against an
                        // element somebody has since moved is a different move, and only a
                        // position read can show that.
                        GeometryFingerprint = SafePlanGeometry(e),
                        BeforeValues = new Dictionary<string, string>
                        {
                            { "operation", p.Operation ?? "" },
                            { "type_id", Rid.Value(e.GetTypeId()).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                            { "pinned", e.Pinned.ToString() },
                            { "wall_join_allowed", p.Operation == "wall_join" ? WallUtils.IsWallJoinAllowedAtEnd((Wall)e, p.JoinEnd).ToString() : "" },
                            { "released_end_joins", p.EndJoins.TryGetValue(Rid.Value(e.Id), out bool[] joins) ? string.Join(",", joins) : "" },
                            { "attachment_target_uid", p.AttachmentTarget == null ? "" : SafePlanUniqueId(p.AttachmentTarget) },
                            { "attachment_target_geometry", p.AttachmentTarget == null ? "" : SafePlanGeometry(p.AttachmentTarget) },
                            { "floor_sketch", p.FloorCopies.TryGetValue(Rid.Value(id), out FloorCopySnapshot floorPlan) ? floorPlan.Fingerprint : "" },
                            { "region_contours", p.RegionContours.TryGetValue(Rid.Value(id), out RegionSnapshot regionPlan) ? regionPlan.Fingerprint : "" },
                        }
                    };
                    planned.ProposedValues = new Dictionary<string, string> {
                        { "operation", p.Operation },
                        { "specification", input[p.Index].ToString(Formatting.None) }
                    };
                    if (p.Operation == "pin" || p.Operation == "unpin") planned.ProposedValues["pinned"] = (p.Operation == "pin").ToString();
                    if (p.Operation == "move_preserve_pin") planned.ProposedValues["pinned"] = true.ToString();
                    if (p.JoinPeers.TryGetValue(Rid.Value(id), out string[][] peers))
                        BindEndJoinPeers(planned, peers);
                    if (p.TypeId != null) planned.ProposedValues["type_id"] = Rid.Value(p.TypeId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (p.Operation == "change_type_by_rule" && p.RuleTargets.TryGetValue(Rid.Value(id), out ElementId ruleType))
                        planned.ProposedValues["type_id"] = Rid.Value(ruleType).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    resolvedPlan.Elements.Add(planned);
                }
            }

            // rename_level: the dry run must show BOTH which plan views will rename
            // (a deterministic read - Revit only renames a plan view whose name exactly
            // matches the level's, before the rename) and which Copy/Monitor alerts the
            // rename would raise (NOT deterministic: Revit's coordination engine is the
            // only source of truth for those). The second half needs an actual rename,
            // so this performs one inside its OWN transaction and rolls it back -
            // "no transaction was opened" below is still true of the real dry-run path;
            // this is a separate, self-contained rehearsal.
            if (dryRun && errors.Count == 0)
            {
                Plan renamePlan = plans.FirstOrDefault(p => p.Operation == "rename_level");
                if (renamePlan != null)
                {
                    JObject rehearsal = RehearseRenameLevel(doc, renamePlan);
                    if (rehearsal.Value<bool>("rollback_confirmed") != true)
                        return CommandResult.FailWithDetail(
                            "Level rename rehearsal rollback was not confirmed; model state is uncertain.",
                            new JObject { ["state"] = "uncertain", ["rehearsal"] = rehearsal, ["write_started"] = true });
                    renamePlan.Summary["level_rename_rehearsal"] = rehearsal;
                }
            }

            if (dryRun)
            {
                // THE REHEARSAL IS THE APPLY, ROLLED BACK. Every operation runs the same path as the apply inside a
                // transaction group (apply, host guard, pre-commit checks, commit, floor guard, post-commit
                // verification) and the group is rolled back whatever the verdict. Invalid entries do not stop the
                // rest from being rehearsed: the rows that resolved get their verdict and the token stays withheld.
                JObject rehearsedBlock = null;
                if (plans.Count > 0)
                {
                    CommandResult rehearsal = ApplyTransformBatch(doc, request, plans, claimed.Count, true, errors);
                    if (!rehearsal.Success) return rehearsal;
                    rehearsedBlock = rehearsal.Data as JObject;
                    // The dry-run reply carries its own declaration below; the nested one would only repeat it.
                    rehearsedBlock?.Remove(ApplicationOutcome.Key);
                }
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = rehearsedBlock == null ? "not_started" : "RolledBack",
                    ["operations"] = input.Count,
                    ["targets"] = claimed.Count, ["valid_operations"] = plans.Count, ["invalid_operations"] = errors.Count,
                    ["errors"] = errors, ["plan"] = new JArray(plans.Select(p => p.Summary)),
                    ["validation_level"] = rehearsedBlock == null ? "arguments_and_references" : TransformRehearsalRules.ValidationLevel,
                    ["revit_rehearsal"] = rehearsedBlock,
                    ["note"] = TransformRehearsalRules.Note(rehearsedBlock != null, errors.Count)
                };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                // Invalid entries make this a partial rehearsal, not a clean one: the token
                // below is already withheld for them, and a plan must read the same fact.
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds the ordered operations, targets, vectors, axes and types" :
                    "no usable confirmation is issued while any operation is invalid");
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so this
                // is the first call a caller makes; without the block here they got
                // success=true with invalid rows and no way to tell a capability gap
                // from a typo except by sending an apply they had no reason to send.
                return FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
                return FallbackDecision.Refuse(
                    "Invalid operations; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            if (DocumentGate.InConfirmedAtomicPlan && plans.Any(p => p.ReleaseEndJoins))
            {
                var changedChild = resolvedPlan.RefuseChangedChild(request.Value<string>(PlanLedger.ExpectedChildPlanField), "wall end joins");
                if (changedChild != null) return changedChild;
            }
            // The rehearsed fingerprint travels in the token; the rehearsed PLAN does not,
            // so a stale refusal names the drift generically rather than per element.
            // Still refused, still nothing written.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                    resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            return ApplyTransformBatch(doc, request, plans, claimed.Count, false, null);
        }

        /// <summary>
        /// THE APPLICATION PATH of a transform batch, shared by the apply and by the rehearsal.
        /// rehearsal=true runs exactly the same steps - every Apply, the host guard, the pre-commit checks, the
        /// commit, the floor guard and the post-commit verification - inside a TransactionGroup, records nothing
        /// for horizun_undo and rolls the whole group back, whatever the verdict. A failure keeps the error code the
        /// apply gives; what the rehearsal adds is carried in the detail (TransformRehearsalRules.RefusalDetail).
        /// </summary>
        private CommandResult ApplyTransformBatch(Document doc, JObject request, List<Plan> plans, int claimedCount,
                                                  bool rehearsal, JArray invalidRows)
        {
            // Keep copies reversible across the commit-time regeneration too.
            // A failed committed readback rolls back the group, rather than leaving
            // an error reply with copies already present in the document.
            bool guardFinishFloors = plans.Any(p => p.Operation != "pin" && p.Operation != "unpin" &&
                p.Ids.Any(id => doc.GetElement(id) is Wall || doc.GetElement(id) is Floor));
            // The group the APPLY itself needs (copies, finish floors). A rehearsal always has a group to roll back,
            // but takes the apply's non-group decisions (no assimilation, no undo record) so its verdict is the apply's.
            bool realGroup = plans.Any(p => p.Operation == "copy") || guardFinishFloors;
            using (var copyGroup = rehearsal || realGroup ? new TransactionGroup(doc, rehearsal ? "Horizun: rehearse transform" : "Horizun: verified copy") : null)
            {
            if (copyGroup != null && copyGroup.Start() != TransactionStatus.Started)
                return CommandResult.Fail("Could not start the verified-copy transaction group.");
            string phase = TransformRehearsalRules.PhaseApply;
            int? reached = null;
            var culprits = new List<int>();
            RevitErrorRecorder revitSaid = null;
            CommandResult Done(CommandResult r) => rehearsal
                ? FinishRehearsal(doc, copyGroup, plans, r, phase, reached, culprits, invalidRows, revitSaid)
                : r;
            // horizun_undo: the state each target is in BEFORE the commit.
            var undoBefore = rehearsal ? null : plans.ToDictionary(p => p, p => UndoCapture.States(doc, p.Ids.Select(Rid.Value)));
            var floorBefore = FloorWallOverlapGuard.Capture(doc, guardFinishFloors
                ? new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
                    .Where(f => FloorWallOverlapGuard.IsFinishFloor(doc, f)).Select(f => f.Id)
                : Enumerable.Empty<ElementId>());
            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: transform elements";
            // rename_level is the sole operation whenever it appears (refused above
            // otherwise), so capturing warnings for the whole transaction is exactly
            // capturing them for that one operation. A rehearsal also reports what Revit warned.
            bool captureLevelWarnings = rehearsal || plans.Any(p => p.Operation == "rename_level");
            // set_curve on MEP runs right after a delete meets connectivity Revit has not settled: the first
            // transaction is refused ("can no longer keep the connectivity") and the same call passes in a new
            // request (field cycle, 2026-10-17). It is tried ONCE more here, after a confirmed rollback and a regenerate.
            bool mepSetCurve = plans.Count > 0 && plans.All(p => p.Operation == "set_curve" &&
                p.Ids.All(id => doc.GetElement(id) is MEPCurve));
            string firstAttemptFailure = null;
            for (int attempt = 1; ; attempt++)
            {
            phase = TransformRehearsalRules.PhaseApply; reached = null; culprits.Clear();
            using (var tx = new Transaction(doc, txName))
            {
                revitSaid = RevitErrorRecorder.On(tx, captureWarnings: captureLevelWarnings);
                tx.Start();
                try
                {
                    if (mepSetCurve) doc.Regenerate();
                    foreach (Plan p in plans) { reached = p.Index; Apply(doc, p); }
                    // A HOSTED ELEMENT STAYS ON ITS HOST, OR NOTHING IS WRITTEN.
                    // MEASURED on a face-hosted receptacle: moved 300 mm along its wall
                    // it left the face, Revit kept it with NO host, and the point
                    // check verified the move; moved 200 mm across the wall it kept
                    // its host and stood 38 mm out of the far face. A device that must
                    // change host or face is re-placed, not moved, so the whole
                    // transform is rolled back.
                    if (plans.Any(p => p.HostBefore.Count > 0))
                    {
                        phase = TransformRehearsalRules.PhaseHosts;
                        doc.Regenerate();
                        foreach (Plan p in plans) { reached = p.Index; GuardHosts(doc, p); }
                    }
                    // A batch of moves can change constraints on another target in
                    // the same batch. Revit may accept every MoveElements call but
                    // leave some targets short of their requested positions. Check
                    // the final batch state while rollback is still possible.
                    if (plans.All(p => p.Operation == "move" || p.Operation == "move_preserve_pin"))
                    {
                        phase = TransformRehearsalRules.PhasePrecommit;
                        doc.Regenerate();
                        var precommitFailures = new JArray();
                        foreach (Plan p in plans)
                        {
                            if (Verify(doc, p, out JObject detail)) continue;
                            detail["index"] = p.Index;
                            precommitFailures.Add(detail);
                            culprits.Add(p.Index);
                        }
                        if (precommitFailures.Count > 0)
                            throw new InvalidOperationException("Move batch did not hold before commit; rolling back " +
                                precommitFailures.Count + " operation(s): " + precommitFailures.ToString(Formatting.None));
                    }
                    // Floor copy cannot rely on a Location sample. Its sketch,
                    // hosted openings, type and elevation must hold while the
                    // transaction can still be rolled back.
                    if (plans.Any(p => p.Operation == "copy"))
                    {
                        phase = TransformRehearsalRules.PhasePrecommit;
                        doc.Regenerate();
                        foreach (Plan p in plans.Where(p => p.Operation == "copy"))
                            if (!Verify(doc, p, out JObject floorCopyDetail))
                            {
                                culprits.Add(p.Index);
                                throw new InvalidOperationException("Copy did not hold before commit; rolling back: " +
                                    floorCopyDetail.ToString(Formatting.None));
                            }
                    }
                    phase = TransformRehearsalRules.PhaseCommit; reached = null;
                    doc.Regenerate();
                    if (guardFinishFloors)
                        FloorWallOverlapGuard.ThrowIfIncreased(doc, floorBefore, plans.SelectMany(p => p.Ids).Concat(plans.SelectMany(p => p.Created ?? new List<ElementId>())));
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted; bool rolledBack = false;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; var rolled = Guard.RollBack(tx); rb = rolled.StatusName; rolledBack = rolled.Confirmed; }
                    // Guard.Commit throws on a commit Revit itself rolled back: that is a confirmed rollback too.
                    bool nothingWritten = rolledBack || (!attempted && tx.GetStatus() == TransactionStatus.RolledBack);
                    if (MepSetCurveRetryRules.ShouldRetry(attempt, plans.Select(p => p.Operation), mepSetCurve, nothingWritten))
                    {
                        firstAttemptFailure = ex.Message + revitSaid.Said();
                        continue;
                    }
                    // An exception while applying or guarding row k names row k; the pre-commit checks named theirs above.
                    if (culprits.Count == 0 && reached.HasValue &&
                        (phase == TransformRehearsalRules.PhaseApply || phase == TransformRehearsalRules.PhaseHosts))
                        culprits.Add(reached.Value);
                    return Done(CommandResult.FailWithDetail(TransformFailure.AtomicMessage(
                        string.Join(", ", plans.SelectMany(p => p.Ids).Select(id => Rid.Value(id).ToString())), ex.Message + revitSaid.Said() +
                        RevitFailureHints.For(ex.Message + revitSaid.Said()) + " " +
                        (firstAttemptFailure != null ? "(second attempt; the first was rolled back: " + firstAttemptFailure + ") " : "") +
                        "", PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing was transformed")),
                        TransformFailure.AtomicDetail(ex, nothingWritten)));
                }
            }
            break;
            }

            phase = TransformRehearsalRules.PhasePostcommit; reached = null;
            if (guardFinishFloors)
            {
                try { FloorWallOverlapGuard.ThrowIfIncreased(doc, floorBefore, plans.SelectMany(p => p.Ids).Concat(plans.SelectMany(p => p.Created ?? new List<ElementId>()))); }
                catch (Exception ex)
                {
                    var rollback = Guard.RollBack(copyGroup);
                    return Done(CommandResult.FailWithDetail(TransformFailure.AtomicMessage(
                        string.Join(", ", plans.SelectMany(p => p.Ids).Select(id => Rid.Value(id).ToString())), ex.Message,
                        PlanFailure.SingleTransactionOutcome(true, rollback.StatusName, "nothing was transformed")),
                        TransformFailure.RevertedDetail(rollback.StatusName, rollback.Confirmed)));
                }
            }

            // THE INVERSE IS RECORDED AS SOON AS THE COMMIT LANDED, before any verdict can
            // return: a committed batch that then failed its check was left with no undo
            // (audit 2026-10-08, 6.8). A rehearsal keeps none: what it did is rolled back.
            JObject undo = null;
            if (!realGroup && !rehearsal)
            {
                try { undo = RecordUndo(doc, plans, undoBefore); }
                catch (Exception ex) { undo = new JObject { ["recorded"] = false, ["error"] = ex.Message }; }
            }

            var verification = new JArray();
            int verified = 0;
            foreach (Plan p in plans)
            {
                bool ok = Verify(doc, p, out JObject detail);
                detail["index"] = p.Index; detail["operation"] = p.Operation; detail["verified"] = ok;
                if (p.Operation == "rename_level")
                    detail["copy_monitor_alerts"] = new JArray(revitSaid?.Warnings ?? new List<string>());
                verification.Add(detail); if (ok) verified++;
            }
            if (realGroup && (plans.Count == 0 || verified != plans.Count))
            {
                culprits.AddRange(TransformRehearsalRules.FailedRows(verification));
                Guard.RollbackResult rollback = Guard.RollBack(copyGroup);
                return Done(CommandResult.FailWithDetail("Copy verification failed after commit-time regeneration; the complete copy batch was rolled back.",
                    new JObject { ["transaction_status"] = rollback.StatusName, ["rollback_confirmed"] = rollback.Confirmed,
                        ["rows"] = verification, ["error_code"] = rollback.Confirmed ? ErrorCodes.TransaccionRevertida : ErrorCodes.VerificacionFallida }));
            }
            if (plans.Count == 0 || verified != plans.Count)
            {
                culprits.AddRange(TransformRehearsalRules.FailedRows(verification));
                return Done(CommandResult.FailWithDetail("The transaction committed, but " + (plans.Count - verified) +
                    " operation(s) failed post-commit verification. Inspect the model and the undo record for available reversal. " +
                    verification.ToString(Formatting.None),
                    new JObject { ["error_code"] = ErrorCodes.VerificacionFallida, ["transaction_status"] = "Committed", ["rows"] = verification, ["undo"] = undo }));
            }
            // A rehearsal that reached here passed the whole path; it does not assimilate or record an undo.
            if (rehearsal)
            {
                var passed = new JObject { ["rows"] = verification };
                ApplicationOutcome.StampRehearsal(passed, plans.Count, 0, 0, 0);
                return Done(CommandResult.Ok(passed));
            }
            if (realGroup)
            {
                Guard.Assimilate(copyGroup, "Horizun: verified copy");
                try { undo = RecordUndo(doc, plans, undoBefore); }
                catch (Exception ex) { undo = new JObject { ["recorded"] = false, ["error"] = ex.Message }; }
            }
            var trResult = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["operations_verified"] = verified, ["targets"] = claimedCount, ["rows"] = verification
            };
            // Reached only when every planned operation passed its post-commit check.
            ApplicationOutcome.StampApplied(trResult, ApplicationOutcome.Committed,
                                            plans.Count, verified, verified, 0, 0, 0);
            trResult["undo"] = undo;
            if (firstAttemptFailure != null)
            {
                trResult["attempts"] = 2;
                trResult["first_attempt_rolled_back"] = firstAttemptFailure;
                bool? conn = MepSetCurveRetryRules.IsConnectivityRefusal(firstAttemptFailure);
                trResult["first_attempt_connectivity_refusal"] = conn.HasValue ? (JToken)conn.Value : null;
                trResult["note"] = MepSetCurveRetryRules.RetryNote(firstAttemptFailure);
            }
            return CommandResult.Ok(trResult);
            }
        }

        /// <summary>
        /// Closes a rehearsal: rolls the group back, CONFIRMS the rollback and that nothing the run created is left,
        /// and turns the apply's own result into the dry run's. The model state is judged by
        /// TransformRehearsalRules.Decide; an unconfirmed rollback is reported as uncertain with write_started true.
        /// </summary>
        private static CommandResult FinishRehearsal(Document doc, TransactionGroup group, List<Plan> plans, CommandResult applyResult,
            string phase, int? reached, List<int> culprits, JArray invalidRows, RevitErrorRecorder revitSaid)
        {
            string status; bool confirmed; string rollbackError = null;
            try
            {
                TransactionStatus s = group == null ? TransactionStatus.RolledBack : group.GetStatus();
                if (s == TransactionStatus.Started) { Guard.RollbackResult rb = Guard.RollBack(group); status = rb.StatusName; confirmed = rb.Confirmed; }
                else { status = s.ToString(); confirmed = s == TransactionStatus.RolledBack; }
            }
            catch (Exception ex) { status = "exception"; confirmed = false; rollbackError = ex.Message; }
            bool createdAbsent;
            // Only what the rehearsal created: an operation's own targets survive any rollback.
            try { createdAbsent = plans.SelectMany(p => (p.Created ?? new List<ElementId>()).Where(id => !p.Ids.Contains(id))).All(id => doc.GetElement(id) == null); }
            catch { createdAbsent = false; }

            string state = TransformRehearsalRules.Decide(applyResult.Success, confirmed, createdAbsent);
            if (state == TransformRehearsalRules.StateUncertain)
                return CommandResult.FailWithDetail(TransformRehearsalRules.UncertainMessage,
                    TransformRehearsalRules.UncertainDetail(status, rollbackError, createdAbsent),
                    FallbackSignal.NotAllowed(ErrorCodes.ErrorInterno, true), null);

            var rows = plans.Select(p => new TransformRehearsalRules.RowRef { Index = p.Index, Operation = p.Operation }).ToList();
            if (state == TransformRehearsalRules.StateFailed)
            {
                JObject applyDetail = applyResult.Detail;
                string code = applyDetail?.Value<string>("error_code") ?? ErrorCodes.ErrorInterno;
                JArray rowVerdicts = TransformRehearsalRules.RowsForFailure(rows, culprits, reached, phase, code, applyResult.Error);
                return CommandResult.FailWithDetail(applyResult.Error,
                    TransformRehearsalRules.RefusalDetail(applyDetail, phase, rowVerdicts, status, invalidRows,
                        plans.Any(p => p.Created != null && p.Created.Count > 0)),
                    FallbackSignal.NotAllowed(code, false), applyResult.CapabilityGaps);
            }
            JObject block = TransformRehearsalRules.CleanBlock(rows, status, revitSaid?.Warnings);
            if ((applyResult.Data as JObject)?["rows"] is JArray verification) block["verification"] = TransformRehearsalRules.SanitizeRows(verification);
            ApplicationOutcome.StampRehearsal(block, rows.Count, 0, 0, 0);
            return CommandResult.Ok(block);
        }

        private static CommandResult RefuseGroupMembers(Document doc, JArray input)
        {
            var targets = new List<GroupMemberTarget>();
            foreach (JObject o in input.OfType<JObject>())
            {
                string op = (o.Value<string>("operation") ?? "").ToLowerInvariant();
                if (!GroupMemberGuard.IsGeometric(op)) continue;
                foreach (JToken t in (o["element_ids"] as JArray) ?? new JArray())
                {
                    if (t.Type != JTokenType.Integer) continue;
                    long raw = t.Value<long>();
                    Element e = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) : null;
                    if (e == null || e.GroupId == ElementId.InvalidElementId) continue;
                    Group g = doc.GetElement(e.GroupId) as Group;
                    int instances = 0;
                    try { instances = g?.GroupType?.Groups?.Size ?? 0; } catch { }
                    string typeName = null;
                    try { typeName = g?.GroupType?.Name; } catch { }
                    targets.Add(new GroupMemberTarget { ElementId = raw, GroupId = Rid.Value(e.GroupId), GroupTypeName = typeName, TypeInstances = instances, Operation = op });
                }
            }
            if (targets.Count == 0) return null;
            var detail = ErrorCodes.Detail(GroupMemberGuard.Code, null, null, new JObject
            {
                ["state"] = "refused", ["write_started"] = false,
                ["group_members"] = new JArray(targets.Select(t => new JObject { ["element_id"] = t.ElementId, ["group_id"] = t.GroupId,
                    ["group_type"] = t.GroupTypeName, ["type_instances"] = t.TypeInstances, ["operation"] = t.Operation })),
                ["alternatives"] = new JArray("horizun_manage_groups operation=edit_type_members scope=type", "horizun_manage_groups operation=ungroup")
            });
            return CommandResult.FailWithDetail(GroupMemberGuard.Message(targets), detail, FallbackSignal.NotAllowed(GroupMemberGuard.Code, false), null);
        }

        /// <summary>The batch's inverse for horizun_undo. Ops without a recorded inverse make the batch not undoable, by name.</summary>
        private JObject RecordUndo(Document doc, List<Plan> plans, Dictionary<Plan, JObject> before)
        {
            var entries = new List<UndoEntry>(); string blocked = null;
            foreach (Plan p in plans)
            {
                IEnumerable<long> ids = p.Ids.Select(Rid.Value);
                switch (p.Operation)
                {
                    case "move":
                        if (p.NoteVectors.Count > 0) { blocked = "text note move has no recorded inverse"; break; }
                        entries.Add(UndoCapture.Entry(doc, "move", ids, before[p], new JObject { ["vector"] = new JArray(p.Vector.X, p.Vector.Y, p.Vector.Z) })); break;
                    case "move_preserve_pin": entries.Add(UndoCapture.Entry(doc, "move_preserve_pin", ids, before[p], new JObject { ["vector"] = new JArray(p.Vector.X, p.Vector.Y, p.Vector.Z) })); break;
                    case "copy": entries.Add(UndoCapture.Entry(doc, "created", (p.Created ?? new List<ElementId>()).Select(Rid.Value), new JObject(), new JObject())); break;
                    case "rotate":
                        entries.Add(UndoCapture.Entry(doc, "rotate", ids, before[p], new JObject
                        {
                            ["axis_start"] = new JArray(p.Axis.GetEndPoint(0).X, p.Axis.GetEndPoint(0).Y, p.Axis.GetEndPoint(0).Z),
                            ["axis_end"] = new JArray(p.Axis.GetEndPoint(1).X, p.Axis.GetEndPoint(1).Y, p.Axis.GetEndPoint(1).Z),
                            ["angle"] = p.Angle
                        })); break;
                    case "mirror":
                        entries.Add(UndoCapture.Entry(doc, "mirror", ids, before[p], new JObject
                        {
                            ["plane_origin"] = new JArray(p.MirrorPlane.Origin.X, p.MirrorPlane.Origin.Y, p.MirrorPlane.Origin.Z),
                            ["plane_normal"] = new JArray(p.MirrorPlane.Normal.X, p.MirrorPlane.Normal.Y, p.MirrorPlane.Normal.Z)
                        })); break;
                    case "pin": case "unpin": entries.Add(UndoCapture.Entry(doc, "pin", ids, before[p], null)); break;
                    case "change_type": entries.Add(UndoCapture.Entry(doc, "type", ids, before[p], null)); break;
                    case "change_type_by_rule": entries.Add(UndoCapture.Entry(doc, "type", ids, before[p], null)); break;
                    case "set_curve":
                        if (before[p].Properties().Any(x => x.Value["line"]?.Value<bool>() != true)) blocked = "set_curve replaced a non-line curve";
                        else entries.Add(UndoCapture.Entry(doc, "curve", ids, before[p], null));
                        break;
                    case "move_tag_head": entries.Add(UndoCapture.Entry(doc, "tag_head", ids, before[p], null)); break;
                    default: blocked = p.Operation + " has no recorded inverse"; break;
                }
            }
            return UndoCapture.Record(doc, Name, entries, blocked);
        }

        private static Plan PlanOperation(Document doc, int index, JObject o, double scale, HashSet<long> claimed,
                                          out string error, out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            if (o == null) { error = "entry is not an object"; return null; }
            try
            {
                string op = (o.Value<string>("operation") ?? "").ToLowerInvariant();
                if (op != "move" && op != "move_preserve_pin" && op != "copy" && op != "rotate" && op != "mirror" && op != "pin" && op != "unpin" &&
                    op != "change_type" && op != "change_type_by_rule" && op != "set_curve" && op != "move_tag_head" &&
                    op != "set_tag_leader" && op != "wall_join" && op != "wall_attach_top" && op != "wall_attach_base" &&
                    op != "array_linear" && op != "array_radial" && op != "rename_level")
                    throw new UnsupportedCapability(
                        "unsupported operation '" + op + "' - horizun_transform_elements does move, move_preserve_pin, copy, " +
                        "rotate, mirror, pin, unpin, change_type, change_type_by_rule, set_curve, move_tag_head, " +
                        "set_tag_leader, array_linear, array_radial and rename_level only (realign_wall_sketch is a separate call, " +
                        "not mixable with these). Nothing was written.",
                        FallbackSignal.ReasonUnsupportedOperation);
                var allowed = new HashSet<string>(new[] { "operation", "element_ids" });
                if(op=="rename_level") allowed.Add("name");
                if(op=="move" || op=="move_preserve_pin" || op=="copy") allowed.Add("vector");
                if(op=="rotate") allowed.UnionWith(new[] { "axis_start", "axis_end", "angle_degrees" });
                if(op=="mirror") allowed.UnionWith(new[] { "plane_origin", "plane_normal" });
                if(op=="change_type") allowed.Add("type_id");
                if(op=="change_type_by_rule") allowed.Add("rule");
                if(op=="wall_join") allowed.UnionWith(new[] { "join_end", "allow" });
                if(op=="wall_attach_top" || op=="wall_attach_base")
                    allowed.UnionWith(new[] { "attachment_target_id", "expected_end_elevations", "elevation_tolerance_mm" });
                if(op=="set_curve") allowed.UnionWith(new[] { "start", "end", "release_end_joins" });
                if(op=="array_linear") allowed.UnionWith(new[] { "vector", "count", "anchor", "group" });
                if(op=="array_radial") allowed.UnionWith(new[] { "axis_start", "axis_end", "angle_degrees", "count", "anchor", "group" });
                if(op=="move_tag_head") allowed.UnionWith(new[] { "point", "vector" });
                if(op=="set_tag_leader") allowed.UnionWith(new[] { "has_leader", "leader_end_condition", "leader_end", "leader_elbow", "leader_visible" });
                foreach(var field in o.Properties()) if(!allowed.Contains(field.Name)) throw new ArgumentException(field.Name+" is not applicable to "+op);
                JArray ids = o["element_ids"] as JArray;
                if (ids == null || ids.Count == 0 || ids.Count > 2000) throw new ArgumentException("element_ids must contain 1..2000 ids");
                var p = new Plan { Index = index, Operation = op, Ids = new List<ElementId>(), Samples = new Dictionary<long, List<XYZ>>() };
                foreach (JToken token in ids)
                {
                    long raw = token.Value<long>();
                    if (!Rid.CanRepresent(raw)) throw new ArgumentException("ElementId " + raw + " is outside the supported range");
                    Element element = doc.GetElement(Rid.Make(raw));
                    if (element == null) throw new ArgumentException("ElementId " + raw + " does not exist in the host document");
                    if (!claimed.Add(raw)) throw new ArgumentException("ElementId " + raw + " appears in more than one operation; sequential transforms are ambiguous, combine them deliberately first");
                    p.Ids.Add(element.Id);
                    if ((op == "move" || op == "rotate" || op == "mirror") && element.Pinned)
                        throw new ArgumentException("ElementId " + raw + " is pinned and cannot be transformed; unpin it deliberately first or use move_preserve_pin for a move.");
                    if (op == "rotate" || op == "mirror")
                    {
                        var hostedInstance = element as FamilyInstance;
                        Element hostOfInstance = null;
                        try { hostOfInstance = hostedInstance?.Host; } catch { hostOfInstance = null; }
                        string hostedRefusal = TransformHostedRules.Refusal(op, raw, hostedInstance != null, hostOfInstance is Wall);
                        if (hostedRefusal != null) throw new ArgumentException(hostedRefusal);
                    }
                    if (op == "move_tag_head" || op == "set_tag_leader")
                    {
                        // The 8B tag block. IndependentTag only: room/space tags carry a head
                        // but a different leader API, and a rule that half-applies is worse
                        // than a named limit.
                        var tag = element as IndependentTag;
                        if (tag == null)
                            throw new ArgumentException("ElementId " + raw + " is a " + element.GetType().Name + ", not an " +
                                "IndependentTag; " + op + " edits independent tags only (room, space and area tags are not " +
                                "covered by this operation - a documented limit, not a guess).");
                        bool pinnedTag;
                        try { pinnedTag = tag.Pinned; } catch { pinnedTag = false; }
                        if (pinnedTag)
                            throw new ArgumentException("ElementId " + raw + " is PINNED; unpin it deliberately first.");
                        XYZ head;
                        try { head = tag.TagHeadPosition; }
                        catch (Exception ex) { throw new ArgumentException("the head position of tag " + raw + " could not be read (" + ex.Message + ")"); }
                        p.HeadBefore[raw] = head;
                        if (op == "set_tag_leader")
                        {
                            IList<Reference> refs;
                            try { refs = tag.GetTaggedReferences(); }
                            catch (Exception ex) { throw new ArgumentException("the tagged references of tag " + raw + " could not be read (" + ex.Message + ")"); }
                            if (refs == null || refs.Count != 1)
                                throw new ArgumentException("tag " + raw + " tags " + (refs == null ? 0 : refs.Count) + " references; " +
                                    "set_tag_leader addresses the leader of exactly one tagged reference, so a multi-reference tag is refused rather than guessed.");
                            p.TagRefs[raw] = refs[0];
                        }
                    }
                    if (op == "move" || op == "move_preserve_pin" || op == "rotate" || op == "copy" || op == "mirror" || op == "array_linear" || op == "array_radial")
                    {
                        if (element is Level)
                            throw new ArgumentException("ElementId " + raw + " is a Level. Set its elevation with horizun_write_params_verified on parameter Elevation (include units); transform_elements cannot verify a level through LocationPoint/LocationCurve.");
                        // E9 (FIELD FEEDBACK): a viewport has no LocationPoint; it lives on paper.
                        if (element is Viewport)
                            throw new ArgumentException("ElementId " + raw + " is a viewport: it has no LocationPoint and lives on the sheet, " +
                                "not in the model. Move it with horizun_sheet_layout operation=move_viewport (center or corner in paper mm, " +
                                "anchored on the view's crop) or lay the sheet out with operation=auto_layout.");
                        List<XYZ> sample = Samples(element);
                        if ((op == "move" || op == "move_preserve_pin") && element is FilledRegion region)
                        {
                            p.RegionContours[raw] = RegionSnapshot.Read(doc, region);
                            sample = new List<XYZ> { XYZ.Zero }; // verified by contours below, never by the box
                        }
                        // A railing (and any element with no location) cannot be sampled by a point or a line, which
                        // used to force delete-and-recreate to move it. A pure TRANSLATION moves its bounding box by
                        // exactly the vector, so for move the two corners of the box are the sample - and they are
                        // re-read the same way after the commit. Rotation and mirroring stay refused: a turned
                        // axis-aligned box is not the turned corners.
                        if (sample.Count == 0 && (op == "move" || op == "move_preserve_pin") && !(element is Floor))
                        {
                            // FilledRegion is view-owned and frequently has no model-space
                            // Location or bounding box. Its owner-view box is a valid rigid
                            // translation witness, provided the same view is used on readback.
                            sample = Samples(element, true);
                            if (sample.Count > 0) p.BoxSampled.Add(raw);
                        }
                        if (op == "copy" && element is Floor floor)
                            p.FloorCopies[raw] = FloorCopySnapshot.Read(doc, floor);
                        else if (sample.Count == 0)
                            throw new ArgumentException("ElementId " + raw + " has no readable location or bounding box, so " + op + " cannot be verified; inspect the element and its owner view before choosing another operation.");
                        p.Samples[raw] = sample;
                        if (op == "copy")
                        {
                            p.CopyGeometry[raw] = CopySignatures.Signature(doc, element);
                            CopySignature authoredWall = CopySignatures.WallAuthoredSignature(doc, element);
                            if (authoredWall != null) p.CopyWalls[raw] = authoredWall;
                        }
                        if (op == "move_preserve_pin" && !element.Pinned)
                            throw new ArgumentException("ElementId " + raw + " is not pinned; use move for an unpinned element.");
                        // A TURN ABOUT AN AXIS THROUGH THE ELEMENT'S OWN POINT MOVES NO
                        // POINT. Measured on a face-hosted device: the point samples
                        // alone would verify a rotation that turned nothing, so an
                        // instance's axes are sampled too and must turn as asked.
                        if (op == "rotate" || op == "mirror") p.Axes[raw] = Axes(element);
                        long? host = HostOf(element);
                        if (host.HasValue) p.HostBefore[raw] = host.Value;
                    }
                    if (op == "set_curve")
                    {
                        // A curve belongs to ONE element. Giving the same line to
                        // several would stack them on top of each other, which is
                        // never what anybody meant and is hard to see afterwards.
                        if (ids.Count != 1)
                            throw new ArgumentException(
                                "set_curve takes exactly one element_id: the line is that element's own. " +
                                "Sending " + ids.Count + " ids with one line would stack them on top of each " +
                                "other. Use one operation per element.");
                        var lc = element.Location as LocationCurve;
                        if (lc == null || lc.Curve == null)
                            throw new ArgumentException(
                                "ElementId " + raw + " is a " + element.GetType().Name + " with no LocationCurve, " +
                                "so it has no line to set. Walls, beams, pipes and ducts have one; a family " +
                                "instance placed at a point does not.");
                        bool pinned;
                        try { pinned = element.Pinned; } catch { pinned = false; }
                        if (pinned)
                            throw new ArgumentException(
                                "ElementId " + raw + " is PINNED. Revit silently refuses to move a pinned " +
                                "element, so this would report a failed verification rather than a refusal. " +
                                "Unpin it deliberately first.");
                        p.Samples[raw] = Samples(element);
                        if (element is Wall wallBefore) p.WallBaseBefore[raw] = WallBase(wallBefore);
                    }
                }
                if (op == "wall_attach_top" || op == "wall_attach_base")
                {
#if REVIT2025 || REVIT2026 || REVIT2027
                    long targetRaw = o.Value<long?>("attachment_target_id") ?? -1;
                    if (!Rid.CanRepresent(targetRaw) || !(doc.GetElement(Rid.Make(targetRaw)) is Element target) ||
                        !Wall.IsValidTargetAttachment(doc, target.Id))
                        throw new ArgumentException("attachment_target_id must identify a valid roof, floor, ceiling, toposolid or wall target.");
                    p.AttachmentTarget = target;
                    p.AttachmentPosition = op == "wall_attach_top" ? AttachmentLocation.Top : AttachmentLocation.Base;
                    JArray elevations = o["expected_end_elevations"] as JArray;
                    if (elevations == null || elevations.Count != 2)
                        throw new ArgumentException("expected_end_elevations needs two absolute Z values, at each wall location-curve end.");
                    p.ExpectedEndElevations = elevations.Select(v => Finite(v.Value<double>(), "expected_end_elevations") * scale).ToArray();
                    double tol = Finite(o.Value<double?>("elevation_tolerance_mm") ?? 5, "elevation_tolerance_mm");
                    if (tol <= 0 || tol > 100) throw new ArgumentException("elevation_tolerance_mm must be >0 and <=100.");
                    p.AttachmentToleranceFeet = tol / 304.8;
                    foreach (ElementId id in p.Ids)
                    {
                        Wall wall = doc.GetElement(id) as Wall;
                        if (wall == null || !(wall.Location is LocationCurve lc) || !(lc.Curve is Line))
                            throw new ArgumentException("wall_attach_top/base requires straight walls with a readable location line.");
                        if (wall.Pinned) throw new ArgumentException("wall_attach_top/base cannot change pinned wall " + Rid.Value(id) + ".");
                        ICollection<ElementId> current = wall.GetAttachmentIds(p.AttachmentPosition);
                        // Already attached to THIS target: nothing to add, only to verify. That
                        // makes the call idempotent and lets a wall whose earlier verification
                        // failed be re-verified, since an attachment cannot be undone here.
                        if (current.Contains(target.Id)) { p.AlreadyAttached.Add(Rid.Value(id)); }
                        else if (current.Any())
                            throw new ArgumentException("wall " + Rid.Value(id) + " already has an attachment at this end; detach it deliberately first.");
                        if (wall.Id == target.Id) throw new ArgumentException("A wall cannot attach to itself.");
                    }
#else
                    throw new UnsupportedCapability("wall_attach_top/base requires Revit 2025 or later; this Revit API has no wall attachment methods. For a new wall, use horizun_create_elements kind=wall_profile with an explicit vertical profile; it does not attach an existing wall to the roof.", FallbackSignal.ReasonUnsupportedCapability);
#endif
                }
                if (op == "rename_level")
                {
                    if (p.Ids.Count != 1)
                        throw new ArgumentException("rename_level takes exactly one element_id: the level being renamed.");
                    Element el = doc.GetElement(p.Ids[0]);
                    var level = el as Level;
                    if (level == null)
                        throw new ArgumentException("ElementId " + Rid.Value(p.Ids[0]) + " is a " + el.GetType().Name +
                            ", not a Level; rename_level only renames levels.");
                    string newName = (o.Value<string>("name") ?? "").Trim();
                    if (string.IsNullOrEmpty(newName))
                        throw new ArgumentException("rename_level requires a non-empty name.");
                    if (string.Equals(newName, level.Name, StringComparison.Ordinal))
                        throw new ArgumentException("level " + Rid.Value(level.Id) + " is already named '" + newName + "'; a no-op is refused.");
                    bool collision = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .Any(l => l.Id != level.Id && string.Equals(l.Name, newName, StringComparison.OrdinalIgnoreCase));
                    if (collision)
                        throw new ArgumentException("the name '" + newName + "' is already used by another level (Revit level names must be unique).");
                    p.OldLevelName = level.Name;
                    p.NewLevelName = newName;
                    // WHICH plan views Revit will rename SOLA - a deterministic read, not a
                    // guess: Revit only renames a plan view whose name exactly matches the
                    // level's, and only non-template ones (a template has no GenLevel to
                    // match with in the first place).
                    foreach (ViewPlan vp in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
                    {
                        if (vp.IsTemplate) continue;
                        Level gl = null;
                        try { gl = vp.GenLevel; } catch { }
                        if (gl != null && gl.Id == level.Id && string.Equals(vp.Name, level.Name, StringComparison.Ordinal))
                            p.LevelCandidateViewIds.Add(Rid.Value(vp.Id));
                    }
                }
                if(op=="wall_join")
                {
                    if(o["join_end"]?.Type!=JTokenType.Integer || (o.Value<int>("join_end")!=0 && o.Value<int>("join_end")!=1) || o["allow"]?.Type!=JTokenType.Boolean)
                        throw new ArgumentException("wall_join requires join_end=0 or 1 and boolean allow.");
                    if(p.Ids.Any(id=>!(doc.GetElement(id) is Wall))) throw new ArgumentException("wall_join targets must all be walls.");
                    p.JoinEnd=o.Value<int>("join_end"); p.JoinAllowed=o.Value<bool>("allow");
                }
                if (op == "move" || op == "move_preserve_pin" || op == "copy") p.Vector = Point(o["vector"], scale, "vector");
                if (op == "move") foreach (ElementId id in p.Ids)
                    if (doc.GetElement(id) is TextNote note)
                    {
                        View view = doc.GetElement(note.OwnerViewId) as View;
                        if (view == null) throw new ArgumentException("TextNote " + Rid.Value(id) + " has no readable owner view.");
                        double[] v = AnnotationMoveRules.ProjectToPlane(D3(p.Vector), D3(view.ViewDirection));
                        p.NoteVectors[Rid.Value(id)] = new XYZ(v[0], v[1], v[2]);
                    }
                if (op == "array_linear" || op == "array_radial") PlanArray(doc, o, scale, p);
                if (op == "rotate")
                {
                    XYZ a = Point(o["axis_start"], scale, "axis_start"), b = Point(o["axis_end"], scale, "axis_end");
                    if (a.DistanceTo(b) < 1e-9) throw new ArgumentException("rotation axis endpoints must differ");
                    p.Axis = Line.CreateBound(a, b);
                    if (o["angle_degrees"] == null) throw new ArgumentException("angle_degrees is required for rotate");
                    p.Angle = o.Value<double>("angle_degrees") * Math.PI / 180.0;
                    p.Rotation = Transform.CreateRotationAtPoint((b - a).Normalize(), p.Angle, a);
                }
                if (op == "mirror")
                {
                    // IN PLACE, not a mirrored copy: the element keeps its id, its
                    // parameters and whatever is hosted on it.
                    XYZ origin = Point(o["plane_origin"], scale, "plane_origin");
                    XYZ normal = Point(o["plane_normal"], 1.0, "plane_normal");
                    if (normal.GetLength() < 1e-9) throw new ArgumentException("plane_normal must not be zero");
                    normal = normal.Normalize();
                    p.MirrorPlane = Plane.CreateByNormalAndOrigin(normal, origin);
                    p.Rotation = Transform.CreateReflection(p.MirrorPlane);
                }
                if (op == "set_curve")
                {
                    if (o["release_end_joins"] != null && o["release_end_joins"].Type != JTokenType.Boolean) throw new ArgumentException("release_end_joins must be boolean.");
                    p.ReleaseEndJoins = o.Value<bool?>("release_end_joins") ?? false;
                    if (p.ReleaseEndJoins)
                        foreach (ElementId id in p.Ids)
                        {
                            if (!(doc.GetElement(id) is Wall wall)) throw new ArgumentException("release_end_joins currently applies to walls only; MEP connector networks are not wall joins.");
                            p.EndJoins[Rid.Value(id)] = new[] { WallUtils.IsWallJoinAllowedAtEnd(wall, 0), WallUtils.IsWallJoinAllowedAtEnd(wall, 1) };
                        }
                    XYZ a = Point(o["start"], scale, "start"), b = Point(o["end"], scale, "end");
                    if (a.DistanceTo(b) < 1e-9)
                        throw new ArgumentException("start and end must differ: a zero-length curve is not a line");
                    p.Curve = Line.CreateBound(a, b);
                    if (p.ReleaseEndJoins) CaptureJoinNeighbors(doc, p);
                    foreach (ElementId id in p.Ids)
                    {
                        if (!(doc.GetElement(id) is Wall wall) || !(wall.Location is LocationCurve wallLine) || wallLine.Curve == null) continue;
                        string refusal = WallCurveElevationRules.Refusal(Rid.Value(id), wallLine.Curve.GetEndPoint(0).Z, a.Z, b.Z,
                            WallBaseOffset(wall), 0.5 / 304.8);
                        if (refusal != null) throw new ArgumentException(refusal);
                    }
                }
                if (op == "change_type")
                {
                    long typeId = o.Value<long?>("type_id") ?? -1;
                    if (!Rid.CanRepresent(typeId) || !(doc.GetElement(Rid.Make(typeId)) is ElementType))
                        throw new ArgumentException("type_id must identify an ElementType");
                    p.TypeId = Rid.Make(typeId);
                    foreach (ElementId id in p.Ids)
                        if (!doc.GetElement(id).IsValidType(p.TypeId))
                            throw new ArgumentException("type_id " + typeId + " is not valid for ElementId " + Rid.Value(id));
                }
                if (op == "change_type_by_rule")
                {
                    TypeChangeRuleSet ruleSet = TypeChangeRuleRules.Parse(o["rule"] as JArray);
                    if (!ruleSet.Ok) throw new ArgumentException(ruleSet.Error);
                    foreach (TypeChangeRule r in ruleSet.Rules)
                        if (!Rid.CanRepresent(r.TypeId) || !(doc.GetElement(Rid.Make(r.TypeId)) is ElementType))
                            throw new ArgumentException("rule[" + r.Index + "].type_id " + r.TypeId + " must identify an ElementType");
                    p.RuleSet = ruleSet;
                    foreach (ElementId id in p.Ids)
                    {
                        Element e = doc.GetElement(id);
                        Dictionary<string, double> measured = MeasureInstance(e, out string whyUnmeasured);
                        p.RuleMeasured[Rid.Value(id)] = measured;
                        TypeChangeMatch match = TypeChangeRuleRules.Evaluate(ruleSet, measured);
                        if (!match.Matched)
                            // match.Reason distinguishes "unmeasured" (rejected even when an 'else' rule exists -
                            // see TypeChangeRuleRules.Evaluate) from "no rule matched and no else was declared".
                            throw new ArgumentException("ElementId " + Rid.Value(id) + ": " + match.Reason +
                                " (measured: " + (measured.Count == 0 ? "nothing - " + (whyUnmeasured ?? "this element's face " +
                                "could not be measured") : string.Join(", ", measured.Select(kv => kv.Key + "=" + kv.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)))) + ").");
                        ElementId typeId2 = Rid.Make(match.TypeId);
                        if (!e.IsValidType(typeId2))
                            throw new ArgumentException("rule[" + match.RuleIndex + "] matched type_id " + match.TypeId +
                                " for ElementId " + Rid.Value(id) + ", which is not a valid type for it.");
                        p.RuleTargets[Rid.Value(id)] = typeId2;
                        p.RuleMatches[Rid.Value(id)] = match;
                    }
                }
                if (op == "move_tag_head")
                {
                    bool hasPoint = o["point"] != null, hasVector = o["vector"] != null;
                    if (hasPoint == hasVector)
                        throw new ArgumentException("move_tag_head takes exactly one of point (an absolute head position, one tag only) or vector (a displacement applied to every tag listed).");
                    if (hasPoint)
                    {
                        if (ids.Count != 1) throw new ArgumentException("move_tag_head with point takes exactly one element_id: one absolute position belongs to one head.");
                        p.HeadPoint = Point(o["point"], scale, "point");
                    }
                    else p.Vector = Point(o["vector"], scale, "vector");
                }
                if (op == "set_tag_leader")
                {
                    int named = 0;
                    if (o["has_leader"] != null)
                    {
                        if (o["has_leader"].Type != JTokenType.Boolean) throw new ArgumentException("has_leader must be a boolean");
                        p.HasLeader = o.Value<bool>("has_leader"); named++;
                    }
                    if (o["leader_end_condition"] != null)
                    {
                        string c = (o.Value<string>("leader_end_condition") ?? "").ToLowerInvariant();
                        if (c != "attached" && c != "free") throw new ArgumentException("leader_end_condition must be attached or free");
                        p.EndCondition = c == "free" ? LeaderEndCondition.Free : LeaderEndCondition.Attached; named++;
                    }
                    if (o["leader_visible"] != null)
                    {
                        if (o["leader_visible"].Type != JTokenType.Boolean) throw new ArgumentException("leader_visible must be a boolean");
                        p.LeaderVisible = o.Value<bool>("leader_visible"); named++;
                    }
                    if (o["leader_end"] != null) { p.LeaderEnd = Point(o["leader_end"], scale, "leader_end"); named++; }
                    if (o["leader_elbow"] != null) { p.LeaderElbow = Point(o["leader_elbow"], scale, "leader_elbow"); named++; }
                    if (named == 0)
                        throw new ArgumentException("set_tag_leader names no edit: give at least one of has_leader, leader_end_condition, leader_end, leader_elbow, leader_visible.");
                    foreach (ElementId id in p.Ids)
                    {
                        var tag = (IndependentTag)doc.GetElement(id);
                        long raw = Rid.Value(id);
                        bool hasLeaderNow; try { hasLeaderNow = tag.HasLeader; } catch { hasLeaderNow = false; }
                        bool willHaveLeader = p.HasLeader ?? hasLeaderNow;
                        LeaderEndCondition condNow; try { condNow = tag.LeaderEndCondition; } catch { condNow = LeaderEndCondition.Attached; }
                        LeaderEndCondition willBe = p.EndCondition ?? condNow;
                        if (p.EndCondition.HasValue)
                        {
                            bool assignable; try { assignable = tag.CanLeaderEndConditionBeAssigned(p.EndCondition.Value); } catch { assignable = false; }
                            if (!assignable)
                                throw new ArgumentException("tag " + raw + " cannot take leader_end_condition=" + (p.EndCondition.Value == LeaderEndCondition.Free ? "free" : "attached") +
                                    " (CanLeaderEndConditionBeAssigned is false: its tagged element or category does not allow it). Nothing was written.");
                        }
                        if ((p.LeaderEnd != null || p.LeaderElbow != null || p.LeaderVisible.HasValue) && !willHaveLeader)
                            throw new ArgumentException("tag " + raw + " has no leader and this operation does not set has_leader=true, so leader_end/leader_elbow/leader_visible have nothing to act on.");
                        if (p.LeaderEnd != null && willBe != LeaderEndCondition.Free)
                            throw new ArgumentException("tag " + raw + ": leader_end applies only to a FREE leader end; this leader is attached and the operation does not set leader_end_condition=free. Revit ignores a free end on an attached leader, so the request is refused rather than dropped.");
                    }
                }
                p.Summary = new JObject { ["index"] = index, ["operation"] = op, ["targets"] = p.Ids.Count, ["verifiable"] = true };
                if (op == "set_curve")
                {
                    p.Summary["release_end_joins"] = p.ReleaseEndJoins;
                    p.Summary["release_end_joins_assumed"] = o["release_end_joins"] == null;
                }
                if (p.NoteVectors.Count > 0)
                {
                    p.Summary["text_note_moves"] = new JArray(p.NoteVectors.Select(kv => (JToken)new JObject {
                        ["element_id"] = kv.Key, ["applied_vector_feet"] = Arr(kv.Value),
                        ["ignored_vector_feet"] = Arr(p.Vector - kv.Value), ["position_tolerance_feet"] = AnnotationMoveRules.PositionToleranceFeet }));
                    p.Summary["undo"] = new JObject { ["supported"] = false, ["reason"] = "Text note moves have no recorded inverse; use Revit Undo." };
                }
                if (op == "move" || op == "move_preserve_pin")
                    p.Summary["verification_methods"] = new JArray(p.Ids.Select(id => (JToken)new JObject
                    {
                        ["element_id"] = Rid.Value(id),
                        ["method"] = p.BoxSampled.Contains(Rid.Value(id))
                            ? "owner_view_bounding_box"
                            : (doc.GetElement(id) is TextNote ? "text_note_coordinate" : "location_point_or_curve")
                    }));
                if (op == "copy" && p.FloorCopies.Count > 0)
                    p.Summary["floors"] = new JArray(p.FloorCopies.Select(kv => (JToken)new JObject
                    {
                        ["element_id"] = kv.Key,
                        ["sketch_loops"] = kv.Value.LoopCount,
                        ["hosted_openings"] = kv.Value.HostedOpeningCount,
                        ["type_id"] = Rid.Value(kv.Value.TypeId),
                        ["source_elevation_feet"] = kv.Value.AbsoluteElevation,
                        ["expected_copy_elevation_feet"] = kv.Value.AbsoluteElevation + p.Vector.Z
                    }));
                if (p.AttachmentTarget != null)
                {
                    p.Summary["attachment_target_id"] = Rid.Value(p.AttachmentTarget.Id);
                    p.Summary["expected_end_elevations_feet"] = new JArray(p.ExpectedEndElevations);
                    p.Summary["elevation_tolerance_mm"] = p.AttachmentToleranceFeet * 304.8;
#if REVIT2025 || REVIT2026 || REVIT2027
                    // WHAT THE TARGET ITSELF SAYS: each location-line end projected vertically
                    // onto the target's facing planar faces. No roof "base convention" is
                    // assumed - the roof geometry answers - so expected_end_elevations can be
                    // derived without a calibration attach.
                    p.Summary["predicted_end_elevations_feet"] = new JArray(p.Ids.Select(id =>
                        (JToken)new JObject { ["wall_id"] = Rid.Value(id),
                            ["already_attached"] = p.AlreadyAttached.Contains(Rid.Value(id)),
                            ["z"] = new JArray(PredictAttachedEnds(doc.GetElement(id) as Wall, p.AttachmentTarget,
                                p.AttachmentPosition == AttachmentLocation.Top).Select(v => v.HasValue ? (JToken)v.Value : JValue.CreateNull())) }));
#endif
                    p.Summary["undo"] = new JObject { ["supported"] = false,
                        ["reason"] = "horizun_undo records no inverse for wall_attach_top/base: once applied, only a manual detach in Revit reverts it. Check predicted_end_elevations_feet against expected_end_elevations before applying." };
                }
                if (op == "rename_level")
                {
                    p.Summary["from"] = p.OldLevelName;
                    p.Summary["to"] = p.NewLevelName;
                    p.Summary["views_expected_to_rename"] = new JArray(p.LevelCandidateViewIds.Select(id => (JToken)id));
                    // level_rename_rehearsal (Copy/Monitor alerts) is added by the caller,
                    // which alone knows whether this is the dry-run path.
                }
                if (op == "change_type_by_rule")
                {
                    p.Summary["by_type"] = new JArray(p.RuleTargets.Values.GroupBy(t => Rid.Value(t))
                        .Select(g => (JToken)new JObject { ["type_id"] = g.Key, ["instances"] = g.Count() }));
                    // WHICH INSTANCE GOES TO WHICH TYPE, AND WHY - the field ask this exists for:
                    // a caller reviewing 1,000 rows before committing to any of them.
                    p.Summary["instances"] = new JArray(p.Ids.Select(id => (JToken)new JObject
                    {
                        ["element_id"] = Rid.Value(id),
                        ["type_id"] = Rid.Value(p.RuleTargets[Rid.Value(id)]),
                        ["matched_rule_index"] = p.RuleMatches[Rid.Value(id)].RuleIndex,
                        ["reason"] = p.RuleMatches[Rid.Value(id)].Reason,
                        ["measured"] = JObject.FromObject(p.RuleMeasured[Rid.Value(id)])
                    }));
                }
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }

        private static void Apply(Document doc, Plan p)
        {
            switch (p.Operation)
            {
                case "move":
                    var ordinary = p.Ids.Where(id => !p.NoteVectors.ContainsKey(Rid.Value(id))).ToList();
                    if (ordinary.Count > 0) ElementTransformUtils.MoveElements(doc, ordinary, p.Vector);
                    foreach (ElementId id in p.Ids.Where(id => p.NoteVectors.ContainsKey(Rid.Value(id))))
                        if (p.NoteVectors[Rid.Value(id)].GetLength() > 1e-12)
                            ElementTransformUtils.MoveElement(doc, id, p.NoteVectors[Rid.Value(id)]);
                    break;
                case "move_preserve_pin":
                    foreach (ElementId id in p.Ids) doc.GetElement(id).Pinned = false;
                    ElementTransformUtils.MoveElements(doc, p.Ids, p.Vector);
                    foreach (ElementId id in p.Ids) doc.GetElement(id).Pinned = true;
                    break;
                case "copy": p.Created = ElementTransformUtils.CopyElements(doc, p.Ids, p.Vector).ToList(); break;
                case "array_linear": case "array_radial": ApplyArray(doc, p); break;
                case "rotate": ElementTransformUtils.RotateElements(doc, p.Ids, p.Axis, p.Angle); break;
                case "mirror": ElementTransformUtils.MirrorElements(doc, p.Ids, p.MirrorPlane, false); break;
                case "pin": foreach (ElementId id in p.Ids) doc.GetElement(id).Pinned = true; break;
                case "unpin": foreach (ElementId id in p.Ids) doc.GetElement(id).Pinned = false; break;
                case "move_tag_head":
                    foreach (ElementId id in p.Ids)
                    {
                        var tag = (IndependentTag)doc.GetElement(id);
                        tag.TagHeadPosition = p.HeadPoint ?? p.HeadBefore[Rid.Value(id)].Add(p.Vector);
                    }
                    break;
                case "set_tag_leader":
                    foreach (ElementId id in p.Ids)
                    {
                        var tag = (IndependentTag)doc.GetElement(id);
                        Reference r = p.TagRefs[Rid.Value(id)];
                        // Order matters: a leader must exist before its condition, end,
                        // elbow or visibility can be addressed.
                        if (p.HasLeader.HasValue) tag.HasLeader = p.HasLeader.Value;
                        if (p.EndCondition.HasValue) tag.LeaderEndCondition = p.EndCondition.Value;
                        if (p.LeaderEnd != null) tag.SetLeaderEnd(r, p.LeaderEnd);
                        if (p.LeaderElbow != null) tag.SetLeaderElbow(r, p.LeaderElbow);
                        if (p.LeaderVisible.HasValue) tag.SetIsLeaderVisible(r, p.LeaderVisible.Value);
                    }
                    break;
                case "change_type": foreach (ElementId id in p.Ids) doc.GetElement(id).ChangeTypeId(p.TypeId); break;
                case "rename_level": ((Level)doc.GetElement(p.Ids[0])).Name = p.NewLevelName; break;
                case "change_type_by_rule":
                    foreach (ElementId id in p.Ids) doc.GetElement(id).ChangeTypeId(p.RuleTargets[Rid.Value(id)]);
                    break;
                case "set_curve":
                    foreach (ElementId id in p.Ids)
                    {
                        Element el = doc.GetElement(id);
                        var lc = el.Location as LocationCurve;
                        if (lc == null) continue;
                        // A RUN WHOSE OTHER END IS IN A NETWORK moves only the end that changes, by its connector:
                        // replacing the curve makes Revit re-evaluate the held end and ask to disconnect it - a modal
                        // question nobody answers, and the write rolls back. Measured 2026-10-16: carrying conduit ends
                        // to their devices, every run with a fitting at its far end refused.
                        if (el is MEPCurve mc && MoveOneEndByConnector(mc, lc.Curve, p.Curve)) continue;
                        if (p.ReleaseEndJoins)
                        {
                            WallUtils.DisallowWallJoinAtEnd((Wall)el, 0);
                            WallUtils.DisallowWallJoinAtEnd((Wall)el, 1);
                            doc.Regenerate();
                        }
                        lc.Curve = p.Curve;
                        if (p.ReleaseEndJoins)
                        {
                            doc.Regenerate();
                            bool[] original = p.EndJoins[Rid.Value(id)];
                            for (int end = 0; end < 2; end++) if (original[end]) WallUtils.AllowWallJoinAtEnd((Wall)el, end);
                            doc.Regenerate();
                        }
                    }
                    break;
                case "wall_join":
                    foreach(var id in p.Ids) { if(p.JoinAllowed) WallUtils.AllowWallJoinAtEnd((Wall)doc.GetElement(id),p.JoinEnd); else WallUtils.DisallowWallJoinAtEnd((Wall)doc.GetElement(id),p.JoinEnd); }
                    break;
                case "wall_attach_top": case "wall_attach_base":
#if REVIT2025 || REVIT2026 || REVIT2027
                    foreach (ElementId id in p.Ids)
                        if (!p.AlreadyAttached.Contains(Rid.Value(id)))
                            ((Wall)doc.GetElement(id)).AddAttachment(p.AttachmentTarget.Id, p.AttachmentPosition);
#endif
                    break;
            }
        }

        /// <summary>
        /// One end of an MEP run moved by its own connector, when exactly one end changes and the other is connected.
        /// False (nothing done) otherwise, and the curve is replaced as before.
        /// </summary>
        private static bool MoveOneEndByConnector(MEPCurve run, Curve was, Curve want)
        {
            const double same = 0.5 / 304.8;
            XYZ a0 = was.GetEndPoint(0), a1 = was.GetEndPoint(1), b0 = want.GetEndPoint(0), b1 = want.GetEndPoint(1);
            XYZ stay = null, from = null, to = null;
            if (a0.DistanceTo(b0) <= same && a1.DistanceTo(b1) > same) { stay = a0; from = a1; to = b1; }
            else if (a1.DistanceTo(b1) <= same && a0.DistanceTo(b0) > same) { stay = a1; from = a0; to = b0; }
            else if (a0.DistanceTo(b1) <= same && a1.DistanceTo(b0) > same) { stay = a0; from = a1; to = b0; }
            else if (a1.DistanceTo(b0) <= same && a0.DistanceTo(b1) > same) { stay = a1; from = a0; to = b1; }
            if (stay == null) return false;
            Connector held = null, moving = null;
            foreach (Connector c in run.ConnectorManager.Connectors)
            {
                if (c.ConnectorType != ConnectorType.End) continue;
                if (c.Origin.DistanceTo(stay) <= same) held = c;
                else if (c.Origin.DistanceTo(from) <= same) moving = c;
            }
            if (held == null || moving == null || !ReallyConnected(held) || ReallyConnected(moving)) return false;
            moving.Origin = to;
            return true;
        }

        /// <summary>
        /// Connected to a LIVE physical connector of another element. Right after a delete, a connector can still
        /// answer IsConnected for the fitting that is gone; that end is free, and moving it by its connector (not by
        /// replacing the curve, which re-evaluates the held end) is the order that keeps the network.
        /// </summary>
        private static bool ReallyConnected(Connector c)
        {
            if (!c.IsConnected) return false;
            try
            {
                foreach (Connector other in c.AllRefs)
                {
                    if (other == null || other.ConnectorType == ConnectorType.Logical) continue;
                    Element owner = other.Owner;
                    if (owner == null || !owner.IsValidObject || owner.Id == c.Owner.Id) continue;
                    return true;
                }
                return false;
            }
            catch { return true; }
        }

        private static bool Verify(Document doc, Plan p, out JObject detail)
        {
            detail = new JObject { ["targets"] = p.Ids.Count };
            if (p.Operation == "array_linear" || p.Operation == "array_radial") return VerifyArray(doc, p, detail);
            if (p.Operation == "copy")
            {
                int present = p.Created == null ? 0 : p.Created.Count(id => doc.GetElement(id) != null);
                detail["copies_expected"] = p.Ids.Count;
                detail["created_ids"] = new JArray((p.Created ?? new List<ElementId>()).Select(id => (JToken)Rid.Value(id)));
                var remaining=(p.Created ?? new List<ElementId>()).Select(doc.GetElement).Where(e=>e!=null).ToList();
                int matched=0, floorsMatched=0;
                var copies = new List<long>();
                var sourceToCopy = new JArray();
                var hostProblems = new JArray();
                var floorDiagnostics = new JArray();
                foreach(var sourceId in p.Ids)
                {
                    var source=doc.GetElement(sourceId);
                    bool floorCopy = p.FloorCopies.TryGetValue(Rid.Value(sourceId), out FloorCopySnapshot floorBefore);
                    List<XYZ> before = floorCopy ? null : p.Samples[Rid.Value(sourceId)];
                    CopySignature expectedBody = CopyRepresentationRules.Translate(p.CopyGeometry[Rid.Value(sourceId)], p.Vector.X, p.Vector.Y, p.Vector.Z);
                    CopySignature expectedWall = p.CopyWalls.TryGetValue(Rid.Value(sourceId), out CopySignature wallBefore)
                        ? CopyRepresentationRules.Translate(wallBefore, p.Vector.X, p.Vector.Y, p.Vector.Z) : null;
                    Func<Element, bool> placed = e => floorCopy ? FloorCopySnapshot.Matches(doc, floorBefore, e as Floor, p.Vector) :
                        Samples(e).Count==before.Count && (MatchesSamples(before,Samples(e),p,false) || (before.Count==2 && MatchesSamples(before,Samples(e),p,true)));
                    string basis = "translated_geometry";
                    int index=remaining.FindIndex(e=>e.GetTypeId()==source.GetTypeId() && e.GetType()==source.GetType() &&
                        CopyRepresentationRules.Map(new[] { expectedBody }, new[] { CopySignatures.Signature(doc, e) }, null).Count == 1 &&
                        placed(e));
                    // A wall copied without a joined partner, or onto walls it joined at the destination, has other end
                    // faces; it is recognised by its authored axis, height, type and build-up only after no exact copy exists.
                    if (index < 0 && expectedWall != null)
                    {
                        index = remaining.FindIndex(e => e.GetTypeId() == source.GetTypeId() && e.GetType() == source.GetType() &&
                            CopyRepresentationRules.JoinChangedWallBasis(expectedWall, CopySignatures.WallAuthoredSignature(doc, e)) != null &&
                            placed(e));
                        if (index >= 0) basis = "authored_wall_join_changed";
                    }
                    if(index<0)
                    {
                        if (floorCopy)
                            foreach (Element candidate in remaining)
                                floorDiagnostics.Add(FloorCopySnapshot.Diagnose(doc, floorBefore, candidate, p.Vector));
                        continue;
                    }
                    matched++;
                    if (floorCopy) floorsMatched++;
                    copies.Add(Rid.Value(remaining[index].Id));
                    sourceToCopy.Add(new JObject { ["source_id"] = Rid.Value(sourceId), ["copy_id"] = Rid.Value(remaining[index].Id), ["geometry_verified"] = true, ["basis"] = basis });
                    string hostProblem = HostedCopyProblem(p, Rid.Value(sourceId), remaining[index]);
                    if (hostProblem != null) hostProblems.Add(hostProblem);
                    remaining.RemoveAt(index);
                }
                // Inserts copied with their hosts are dependents, not extra copies (audit 2026-10-08, 6.8).
                int dependents = Dependents(remaining, copies, detail);
                detail["copies_present"] = present - dependents;
                detail["source_to_copy"] = sourceToCopy;
                detail["copy_id_origin"] = "ElementTransformUtils.CopyElements returned IDs; matched one-to-one by translated geometry and type (a wall whose joins changed: by its translated authored axis, height, type and build-up)";
                detail["copies_geometry_verified"]=matched;
                detail["floor_copies_expected"] = p.FloorCopies.Count;
                detail["floor_copies_verified"] = floorsMatched;
                if (floorDiagnostics.Count > 0) detail["floor_copy_diagnostics"] = floorDiagnostics;
                if (hostProblems.Count > 0) detail["hosted_copies_off_host"] = hostProblems;
                // An operation over no source proves nothing: 0 == 0 is not a copy.
                return p.Ids.Count > 0 && present - dependents == p.Ids.Count && matched==p.Ids.Count &&
                       hostProblems.Count == 0 && detail["unexplained_created_ids"] == null;
            }
            if (p.Operation == "rename_level")
            {
                Level level = doc.GetElement(p.Ids[0]) as Level;
                string nowName = null; try { nowName = level?.Name; } catch { }
                var renamedViews = new JArray();
                int viewsRenamed = 0;
                foreach (long vid in p.LevelCandidateViewIds)
                {
                    View v = doc.GetElement(Rid.Make(vid)) as View;
                    string nowViewName = null; try { nowViewName = v?.Name; } catch { }
                    bool renamed = nowViewName == p.NewLevelName;
                    if (renamed) viewsRenamed++;
                    renamedViews.Add(new JObject { ["view_id"] = vid, ["from"] = p.OldLevelName, ["to"] = nowViewName, ["renamed"] = renamed });
                }
                detail["level_name"] = nowName;
                detail["views_candidate"] = p.LevelCandidateViewIds.Count;
                detail["views_renamed"] = renamedViews;
                detail["views_renamed_count"] = viewsRenamed;
                return nowName == p.NewLevelName;
            }
            int good = 0;
            var tagRows = new JArray();
            foreach (ElementId id in p.Ids)
            {
                Element e = doc.GetElement(id); if (e == null) continue;
                if (p.Operation == "move_tag_head" || p.Operation == "set_tag_leader")
                {
                    var tag = e as IndependentTag;
                    var row = new JObject { ["element_id"] = Rid.Value(id) };
                    // THE TAG'S CHECKLIST, typed. The requested properties are declared up
                    // front, so a property nobody compared cannot pass by omission, and a
                    // property that could not be re-read is UNMEASURED: it used to be
                    // reported as the opposite of the request ("read": !requested), a
                    // measurement nobody took.
                    var check = new PostconditionCheck(TagProperties(p).ToArray());
                    if (tag == null)
                    {
                        foreach (string property in TagProperties(p))
                            check.Unreadable(property, JValue.CreateNull(), "the element is no longer an IndependentTag");
                    }
                    else if (p.Operation == "move_tag_head")
                    {
                        XYZ expected = p.HeadPoint ?? p.HeadBefore[Rid.Value(id)].Add(p.Vector);
                        XYZ actual = null; string why = null;
                        try { actual = tag.TagHeadPosition; } catch (Exception ex) { why = ex.Message; }
                        bool m = actual != null && actual.DistanceTo(expected) <= TagPositionToleranceFeet;
                        row["head_before_feet"] = Arr(p.HeadBefore[Rid.Value(id)]);
                        row["head_expected_feet"] = Arr(expected);
                        row["head_read_feet"] = actual == null ? (JToken)JValue.CreateNull() : Arr(actual);
                        row["tolerance_feet"] = TagPositionToleranceFeet;
                        if (actual == null) check.Unreadable("head_position", Arr(expected), why ?? "TagHeadPosition returned null");
                        else check.Record("head_position", Arr(expected), Arr(actual), m);
                    }
                    else
                    {
                        Reference r = p.TagRefs[Rid.Value(id)];
                        if (p.HasLeader.HasValue)
                        {
                            bool? v = null; string why = null;
                            try { v = tag.HasLeader; } catch (Exception ex) { why = ex.Message; }
                            row["has_leader"] = new JObject { ["requested"] = p.HasLeader.Value, ["read"] = v.HasValue ? (JToken)v.Value : JValue.CreateNull() };
                            if (v.HasValue) check.Compare("has_leader", p.HasLeader.Value, v.Value);
                            else check.Unreadable("has_leader", p.HasLeader.Value, why);
                        }
                        if (p.EndCondition.HasValue)
                        {
                            LeaderEndCondition c = LeaderEndCondition.Attached; bool readable = true; string why = null;
                            try { c = tag.LeaderEndCondition; } catch (Exception ex) { readable = false; why = ex.Message; }
                            string requested = p.EndCondition.Value.ToString().ToLowerInvariant();
                            row["leader_end_condition"] = new JObject { ["requested"] = requested, ["read"] = readable ? (JToken)c.ToString().ToLowerInvariant() : JValue.CreateNull() };
                            if (readable) check.Compare("leader_end_condition", requested, c.ToString().ToLowerInvariant());
                            else check.Unreadable("leader_end_condition", requested, why);
                        }
                        if (p.LeaderEnd != null)
                        {
                            XYZ v = null; string why = null;
                            try { v = tag.GetLeaderEnd(r); } catch (Exception ex) { why = ex.Message; }
                            bool m = v != null && v.DistanceTo(p.LeaderEnd) <= TagPositionToleranceFeet;
                            row["leader_end"] = new JObject { ["requested_feet"] = Arr(p.LeaderEnd), ["read_feet"] = v == null ? (JToken)JValue.CreateNull() : Arr(v), ["tolerance_feet"] = TagPositionToleranceFeet, ["match"] = m };
                            if (v == null) check.Unreadable("leader_end", Arr(p.LeaderEnd), why ?? "GetLeaderEnd returned null");
                            else check.Record("leader_end", Arr(p.LeaderEnd), Arr(v), m);
                        }
                        if (p.LeaderElbow != null)
                        {
                            XYZ v = null; string why = null;
                            try { v = tag.GetLeaderElbow(r); } catch (Exception ex) { why = ex.Message; }
                            bool m = v != null && v.DistanceTo(p.LeaderElbow) <= TagPositionToleranceFeet;
                            row["leader_elbow"] = new JObject { ["requested_feet"] = Arr(p.LeaderElbow), ["read_feet"] = v == null ? (JToken)JValue.CreateNull() : Arr(v), ["tolerance_feet"] = TagPositionToleranceFeet, ["match"] = m };
                            if (v == null) check.Unreadable("leader_elbow", Arr(p.LeaderElbow), why ?? "GetLeaderElbow returned null");
                            else check.Record("leader_elbow", Arr(p.LeaderElbow), Arr(v), m);
                        }
                        if (p.LeaderVisible.HasValue)
                        {
                            bool? v = null; string why = null;
                            try { v = tag.IsLeaderVisible(r); } catch (Exception ex) { why = ex.Message; }
                            row["leader_visible"] = new JObject { ["requested"] = p.LeaderVisible.Value, ["read"] = v.HasValue ? (JToken)v.Value : JValue.CreateNull() };
                            if (v.HasValue) check.Compare("leader_visible", p.LeaderVisible.Value, v.Value);
                            else check.Unreadable("leader_visible", p.LeaderVisible.Value, why);
                        }
                    }
                    bool ok = check.AllVerified;
                    row["postconditions"] = check.ToJson();
                    row["verified"] = ok;
                    tagRows.Add(row);
                    if (ok) good++;
                    continue;
                }
                if (p.Operation == "pin" && e.Pinned) good++;
                else if (p.Operation == "wall_attach_top" || p.Operation == "wall_attach_base")
                {
#if REVIT2025 || REVIT2026 || REVIT2027
                    Wall wall = e as Wall;
                    bool attached = wall != null && wall.GetAttachmentIds(p.AttachmentPosition).Contains(p.AttachmentTarget.Id);
                    double?[] actual = wall == null ? new double?[2] : ReadWallEndElevations(wall, p.AttachmentPosition == AttachmentLocation.Top);
                    bool heights = actual[0].HasValue && actual[1].HasValue &&
                        Math.Abs(actual[0].Value - p.ExpectedEndElevations[0]) <= p.AttachmentToleranceFeet &&
                        Math.Abs(actual[1].Value - p.ExpectedEndElevations[1]) <= p.AttachmentToleranceFeet;
                    var rows = detail["wall_attachments"] as JArray ?? new JArray();
                    rows.Add(new JObject { ["wall_id"] = Rid.Value(id), ["target_id"] = Rid.Value(p.AttachmentTarget.Id),
                        ["attached"] = attached,
                        ["expected_end_elevations_feet"] = new JArray(p.ExpectedEndElevations),
                        ["read_end_elevations_feet"] = new JArray(actual.Select(v => v.HasValue ? (JToken)new JValue(v.Value) : JValue.CreateNull())),
                        ["tolerance_feet"] = p.AttachmentToleranceFeet, ["verified"] = attached && heights });
                    detail["wall_attachments"] = rows;
                    if (attached && heights) good++;
#endif
                }
                else if (p.Operation == "unpin" && !e.Pinned) good++;
                else if (p.Operation == "change_type" && e.GetTypeId() == p.TypeId) good++;
                else if (p.Operation == "change_type_by_rule" && p.RuleTargets.TryGetValue(Rid.Value(id), out ElementId wantType) && e.GetTypeId() == wantType) good++;
                else if (p.Operation == "set_curve")
                {
                    // RE-READ THE CURVE, and accept what a JOIN legitimately does
                    // to it. Revit trims a wall's location curve back to where the
                    // centrelines of the walls it meets cross, so the endpoints
                    // after a commit are not the endpoints that were asked for -
                    // by up to half the joined wall's thickness. Demanding an
                    // exact match would report every corner as a failure; ignoring
                    // the difference would report a wall placed anywhere on the
                    // right line as a success. The line itself is what was set, so
                    // the line itself is what is checked: same direction, and both
                    // endpoints ON it.
                    var lc = e.Location as LocationCurve;
                    if (lc == null || lc.Curve == null) continue;
                    XYZ want0 = p.Curve.GetEndPoint(0), want1 = p.Curve.GetEndPoint(1);
                    XYZ got0 = lc.Curve.GetEndPoint(0), got1 = lc.Curve.GetEndPoint(1);
                    // 0.5 mm in feet: the same tolerance the rest of this file uses
                    // for "the model came back where we put it".
                    const double onTheLine = 0.5 / 304.8;
                    // ON THE LINE IS NOT ENOUGH. A curve Revit left exactly where it was is also
                    // "on the line" whenever the request was collinear with it (extend, shorten):
                    // that used to verify a set_curve that never happened. The ends it had BEFORE
                    // are compared too, and an unchanged curve is a failure.
                    List<XYZ> was = p.Samples.TryGetValue(Rid.Value(id), out List<XYZ> s0) ? s0 : null;
                    string verdict = was == null || was.Count < 2
                        ? CurveChangeRules.Judge(new[] { double.NaN, 0, 0 }, new[] { double.NaN, 0, 0 }, D3(want0), D3(want1), D3(got0), D3(got1), onTheLine)
                        : CurveChangeRules.Judge(D3(was[0]), D3(was[was.Count - 1]), D3(want0), D3(want1), D3(got0), D3(got1), onTheLine);
                    // THE LINE IS NOT THE SOLID. A wall's line lies at its base level's elevation and Revit adds the base offset
                    // on top (measured on Revit 2025, 2026-11-09): field case, a line sent at z=3.0 on a wall with a 3.0 offset
                    // floated it to 6.0 while its LocationCurve read back as requested. The rehearsal refuses a changed z; this
                    // re-reads the solid's base (line z + offset), which must not have moved.
                    string elevation = null;
                    if (verdict == CurveChangeRules.Verified && e is Wall wallAfter && p.WallBaseBefore.TryGetValue(Rid.Value(id), out double baseBefore))
                    {
                        double baseAfter = WallBase(wallAfter);
                        elevation = WallCurveElevationRules.Judge(baseBefore, baseAfter, onTheLine);
                        if (elevation != null)
                        {
                            var rows = detail["wall_elevation_mismatches"] as JArray ?? new JArray();
                            rows.Add(new JObject { ["element_id"] = Rid.Value(id), ["code"] = WallCurveElevationRules.Code,
                                ["base_before_feet"] = double.IsNaN(baseBefore) ? null : (JToken)baseBefore,
                                ["base_after_feet"] = double.IsNaN(baseAfter) ? null : (JToken)baseAfter,
                                ["requested_feet"] = new JArray(Arr(want0), Arr(want1)), ["means"] = elevation });
                            detail["wall_elevation_mismatches"] = rows;
                        }
                    }
                    bool joinsRestored = !p.ReleaseEndJoins || JoinsRestored(doc, p, (Wall)e, detail);
                    if (!joinsRestored) detail["end_joins_not_restored"] = Rid.Value(id);
                    if (verdict == CurveChangeRules.Verified && elevation == null && joinsRestored) good++;
                    else if (elevation == null && verdict != CurveChangeRules.Verified)
                    {
                        var rows = detail["curve_mismatches"] as JArray ?? new JArray();
                        rows.Add(new JObject { ["element_id"] = Rid.Value(id), ["verdict"] = verdict,
                            ["before_feet"] = was == null ? null : new JArray(was.Select(Arr)),
                            ["requested_feet"] = new JArray(Arr(want0), Arr(want1)), ["actual_feet"] = new JArray(Arr(got0), Arr(got1)),
                            ["means"] = verdict == CurveChangeRules.Unchanged
                                ? "Revit kept the curve where it was: nothing changed, whatever the call returned (a group member, a pin or a constraint)."
                                : "The curve moved, but not onto the requested line." });
                        detail["curve_mismatches"] = rows;
                    }
                }
                else if(p.Operation=="wall_join" && WallUtils.IsWallJoinAllowedAtEnd((Wall)e,p.JoinEnd)==p.JoinAllowed) good++;
                else if (p.Operation == "move" || p.Operation == "move_preserve_pin" || p.Operation == "rotate" || p.Operation == "mirror")
                {
                    if (p.RegionContours.TryGetValue(Rid.Value(id), out RegionSnapshot regionBefore))
                    {
                        bool regionOk = e is FilledRegion regionAfter && regionBefore.Matches(doc, regionAfter, p.Vector);
                        if (regionOk && (p.Operation != "move_preserve_pin" || e.Pinned)) good++;
                        else detail["region_contours_mismatch"] = Rid.Value(id);
                        continue;
                    }
                    List<XYZ> after = Samples(e, p.BoxSampled.Contains(Rid.Value(id))), before = p.Samples[Rid.Value(id)];
                    if (p.NoteVectors.TryGetValue(Rid.Value(id), out XYZ noteVector))
                    {
                        bool noteOk = after.Count == 1 && before.Count == 1 && AnnotationMoveRules.PointMoved(D3(before[0]), D3(after[0]), D3(noteVector), AnnotationMoveRules.PositionToleranceFeet);
                        var notes = detail["text_note_moves"] as JArray ?? new JArray();
                        notes.Add(new JObject { ["element_id"] = Rid.Value(id), ["before_feet"] = Arr(before[0]),
                            ["expected_feet"] = Arr(before[0] + noteVector), ["actual_feet"] = after.Count == 1 ? Arr(after[0]) : null,
                            ["applied_vector_feet"] = Arr(noteVector), ["ignored_vector_feet"] = Arr(p.Vector - noteVector),
                            ["position_tolerance_feet"] = AnnotationMoveRules.PositionToleranceFeet, ["verified"] = noteOk });
                        detail["text_note_moves"] = notes;
                        if (noteOk) good++;
                        continue;
                    }
                    if (after.Count != before.Count) continue;
                    bool same = MatchesSamples(before, after, p, false);
                    // Revit may normalize a LocationCurve by reversing its endpoint
                    // order. That is the same committed geometry, not a failed move.
                    if (!same && before.Count == 2) same = MatchesSamples(before, after, p, true);
                    if (!same)
                    {
                        var mismatches = detail["sample_mismatches"] as JArray ?? new JArray();
                        mismatches.Add(new JObject
                        {
                            ["element_id"] = Rid.Value(id),
                            ["before_feet"] = new JArray(before.Select(Arr)),
                            ["expected_feet"] = new JArray(before.Select(x => Arr(
                                p.Operation == "move" || p.Operation == "move_preserve_pin" ? x + p.Vector : p.Rotation.OfPoint(x)))),
                            ["actual_feet"] = new JArray(after.Select(Arr)),
                            ["tolerance_feet"] = 1e-6,
                            ["means"] = "Revit did not retain the requested transform. A hosted instance may only move along its host; use exact placement coordinates and include host-induced movement before requesting another move."
                        });
                        detail["sample_mismatches"] = mismatches;
                    }
                    XYZ[] axesBefore;
                    if (p.Operation == "mirror" && p.Axes.TryGetValue(Rid.Value(id), out axesBefore) && axesBefore != null)
                    {
                        // Reported, not judged: how Revit writes a reflected instance's
                        // axes is what the first measurement is for.
                        XYZ[] mirrored = Axes(e);
                        var seen = detail["mirror_axes"] as JArray ?? new JArray();
                        seen.Add(new JObject
                        {
                            ["element_id"] = Rid.Value(id),
                            ["reflected_basis_x"] = Arr(p.Rotation.OfVector(axesBefore[0])),
                            ["reflected_basis_z"] = Arr(p.Rotation.OfVector(axesBefore[1])),
                            ["basis_x"] = mirrored == null ? null : Arr(mirrored[0]),
                            ["basis_z"] = mirrored == null ? null : Arr(mirrored[1]),
                            ["mirrored_flag"] = (e as FamilyInstance)?.Mirrored
                        });
                        detail["mirror_axes"] = seen;
                    }
                    if (same && p.Operation == "rotate" && p.Axes.TryGetValue(Rid.Value(id), out axesBefore) &&
                        axesBefore != null)
                    {
                        XYZ[] axesAfter = Axes(e);
                        bool turned = axesAfter != null &&
                                      p.Rotation.OfVector(axesBefore[0]).IsAlmostEqualTo(axesAfter[0], 1e-6) &&
                                      p.Rotation.OfVector(axesBefore[1]).IsAlmostEqualTo(axesAfter[1], 1e-6);
                        if (!turned)
                        {
                            same = false;
                            var why = detail["orientation_not_turned"] as JArray ?? new JArray();
                            why.Add(new JObject
                            {
                                ["element_id"] = Rid.Value(id),
                                ["expected_basis_x"] = Arr(p.Rotation.OfVector(axesBefore[0])),
                                ["expected_basis_z"] = Arr(p.Rotation.OfVector(axesBefore[1])),
                                ["basis_x"] = axesAfter == null ? null : Arr(axesAfter[0]),
                                ["basis_z"] = axesAfter == null ? null : Arr(axesAfter[1]),
                                ["means"] = "the element's point is where the turn puts it, and its axes are not: " +
                                            "Revit did not turn it as asked (a hosted instance keeps the " +
                                            "orientation its host allows)."
                            });
                            detail["orientation_not_turned"] = why;
                        }
                    }
                    if (same && (p.Operation != "move_preserve_pin" || e.Pinned)) good++;
                }
            }
            detail["targets_verified"] = good;
            if (tagRows.Count > 0) detail["tags"] = tagRows;
            // An operation that targeted nothing verified nothing: good == Ids.Count over an
            // empty list is the vacuous pass PostconditionCheck exists to refuse.
            return p.Ids.Count > 0 && good == p.Ids.Count;
        }

        private static double?[] ReadWallEndElevations(Wall wall, bool top)
        {
            try
            {
                Line line = ((LocationCurve)wall.Location).Curve as Line;
                if (line == null) return new double?[2];
                // BOTH side faces, as segments, interpolated on the location line (Core
                // WallAttachmentRules.EndElevationsOnLocation). The old reading took the
                // highest exterior vertex within a wall width of each end and measured
                // 6.7-13.3 mm high on a 5-degree roof (live, 2026-10-17).
                var faces = new List<IReadOnlyList<double[]>>();
                foreach (ShellLayerType side in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
                    foreach (Reference reference in HostObjectUtils.GetSideFaces(wall, side))
                    {
                        Face face = wall.GetGeometryObjectFromReference(reference) as Face;
                        if (face == null) continue;
                        var segments = new List<double[]>();
                        foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
                            foreach (Curve edge in loop)
                            {
                                IList<XYZ> pts = edge.Tessellate();
                                for (int i = 1; i < pts.Count; i++)
                                    segments.Add(new[] { pts[i - 1].X, pts[i - 1].Y, pts[i - 1].Z, pts[i].X, pts[i].Y, pts[i].Z });
                            }
                        faces.Add(segments);
                    }
                XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                return WallAttachmentRules.EndElevationsOnLocation(faces, new[] { a.X, a.Y }, new[] { b.X, b.Y }, top);
            }
            catch { return new double?[2]; }
        }

#if REVIT2025 || REVIT2026 || REVIT2027
        /// <summary>
        /// Where each location-line end of a wall will stop once attached: the target's
        /// planar faces facing the wall (down for a top attach, up for a base), the plane
        /// evaluated vertically above/below the end, kept only where that point lies on
        /// the face (Face.Project within 1 mm); the lowest such face for a top, the
        /// highest for a base. Null when no face covers the end.
        /// </summary>
        private static double?[] PredictAttachedEnds(Wall wall, Element target, bool top)
        {
            var result = new double?[2];
            try
            {
                Line line = ((LocationCurve)wall.Location).Curve as Line;
                GeometryElement geometry = target.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine });
                var planes = new List<PlanarFace>();
                foreach (GeometryObject obj in geometry)
                {
                    IEnumerable<GeometryObject> items = obj is GeometryInstance gi ? gi.GetInstanceGeometry().Cast<GeometryObject>() : new[] { obj };
                    foreach (GeometryObject item in items)
                        if (item is Solid solid)
                            foreach (Face f in solid.Faces)
                                if (f is PlanarFace pf && (top ? pf.FaceNormal.Z < -0.1 : pf.FaceNormal.Z > 0.1)) planes.Add(pf);
                }
                for (int end = 0; end < 2; end++)
                {
                    XYZ e = line.GetEndPoint(end);
                    foreach (PlanarFace pf in planes)
                    {
                        double? z = WallAttachmentRules.PlaneZ(new[] { pf.Origin.X, pf.Origin.Y, pf.Origin.Z },
                            new[] { pf.FaceNormal.X, pf.FaceNormal.Y, pf.FaceNormal.Z }, e.X, e.Y);
                        if (!z.HasValue) continue;
                        IntersectionResult on = pf.Project(new XYZ(e.X, e.Y, z.Value));
                        if (on == null || on.Distance > 1.0 / 304.8) continue;
                        if (!result[end].HasValue || (top ? z.Value < result[end].Value : z.Value > result[end].Value)) result[end] = z.Value;
                    }
                }
            }
            catch { }
            return result;
        }
#endif

        private static double Finite(double value, string field)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite.");
            return value;
        }

        /// <summary>The properties a tag operation asked for - the tag checklist's required set.</summary>
        private static List<string> TagProperties(Plan p)
        {
            var required = new List<string>();
            if (p.Operation == "move_tag_head") { required.Add("head_position"); return required; }
            if (p.HasLeader.HasValue) required.Add("has_leader");
            if (p.EndCondition.HasValue) required.Add("leader_end_condition");
            if (p.LeaderEnd != null) required.Add("leader_end");
            if (p.LeaderElbow != null) required.Add("leader_elbow");
            if (p.LeaderVisible.HasValue) required.Add("leader_visible");
            return required;
        }

        /// <summary>0.003 mm: under anything a drawing shows, over floating-point noise.</summary>
        private const double TagPositionToleranceFeet = 1e-5;

        private static JArray Arr(XYZ p) => new JArray(p.X, p.Y, p.Z);
        private static double[] D3(XYZ p) => new[] { p.X, p.Y, p.Z };

        private static bool MatchesSamples(List<XYZ> before, List<XYZ> after, Plan p, bool reverse)
        {
            for (int i = 0; i < before.Count; i++)
            {
                XYZ expected = p.Operation == "move" || p.Operation == "move_preserve_pin" || p.Operation == "copy" ? before[i] + p.Vector : p.Rotation.OfPoint(before[i]);
                int actualIndex = reverse ? after.Count - 1 - i : i;
                if (p.Operation == "move" || p.Operation == "move_preserve_pin" || p.Operation == "copy")
                {
                    if (!AnnotationMoveRules.PointMoved(D3(before[i]), D3(after[actualIndex]), D3(p.Vector), 1e-6)) return false;
                }
                else if (expected.DistanceTo(after[actualIndex]) > 1e-6) return false;
            }
            return true;
        }

        /// <summary>How far a face-hosted element's point is from the plane-bounded face it is hosted on.</summary>
        private const double FaceToleranceMm = 0.5;

        private static double? OffItsFaceMm(Element e)
        {
            try
            {
                var fi = e as FamilyInstance;
                Reference faceRef = fi?.HostFace;
                var point = (fi?.Location as LocationPoint)?.Point;
                if (faceRef == null || point == null) return null;
                var face = e.Document.GetElement(faceRef)?.GetGeometryObjectFromReference(faceRef) as Face;
                if (face == null) return null;
                IntersectionResult projection = face.Project(point);
                if (projection == null) return double.MaxValue;   // it projects onto no part of the face
                return projection.Distance * 304.8;
            }
            catch { return null; }
        }

        /// <summary>
        /// Created elements left over after matching copies: dependents (hosted, transitively,
        /// on a copy) are counted apart and named; anything else is unexplained and named.
        /// Returns the number of dependents.
        /// </summary>
        private static int Dependents(List<Element> leftOver, IEnumerable<long> copies, JObject detail)
        {
            var byId = new Dictionary<long, Element>();
            foreach (Element e in leftOver) byId[Rid.Value(e.Id)] = e;
            var dependentOwners = new Dictionary<long, long>();
            foreach (long copy in copies)
            {
                Element root = leftOver.FirstOrDefault()?.Document.GetElement(Rid.Make(copy));
                if (root == null) continue;
                foreach (ElementId id in root.GetDependentElements(null))
                    if (byId.TryGetValue(Rid.Value(id), out Element dependent) && TransformCopyRules.KnownOwnedDependent(dependent.GetType().Name))
                        dependentOwners[Rid.Value(id)] = copy;
            }
            List<long> dependents, unexplained;
            TransformCopyRules.Classify(byId.Keys, copies, id => { Element e; return dependentOwners.TryGetValue(id, out long owner) ? owner : byId.TryGetValue(id, out e) ? HostOf(e) : null; },
                                        out dependents, out unexplained);
            if (dependents.Count > 0) detail["dependent_created_ids"] = new JArray(dependents);
            if (unexplained.Count > 0) detail["unexplained_created_ids"] = new JArray(unexplained);
            return dependents.Count;
        }

        /// <summary>The array member's source: the one of the plan's ids with the same type.</summary>
        private static long? SourceOf(Plan p, Document doc, Element member)
        {
            foreach (ElementId id in p.Ids)
            {
                Element source = doc.GetElement(id);
                if (source != null && source.GetTypeId() == member.GetTypeId() && source.GetType() == member.GetType())
                    return Rid.Value(id);
            }
            return null;
        }

        /// <summary>
        /// A COPY OF A HOSTED ELEMENT MUST BE HOSTED TOO, and on its host's face. GuardHosts
        /// checks the sources, which a copy does not move; the copies were never looked at
        /// (audit 2026-10-08, 6.8). Null when the source was not hosted or the copy is fine.
        /// </summary>
        private static string HostedCopyProblem(Plan p, long sourceId, Element copy)
        {
            if (!p.HostBefore.ContainsKey(sourceId)) return null;
            long? host = HostOf(copy);
            if (!host.HasValue)
                return "the copy " + Rid.Value(copy.Id) + " of hosted element " + sourceId + " has no host";
            double? off = OffItsFaceMm(copy);
            if (off.HasValue && off.Value > FaceToleranceMm)
                return "the copy " + Rid.Value(copy.Id) + " of hosted element " + sourceId + " is " +
                       off.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                       " mm off the face of its host " + host.Value;
            return null;
        }

        private static long? HostOf(Element e)
        {
            try
            {
                Element host = (e as FamilyInstance)?.Host ?? (e as Opening)?.Host;
                return host == null ? (long?)null : Rid.Value(host.Id);
            }
            catch { return null; }
        }

        private static void GuardHosts(Document doc, Plan p)
        {
            foreach (KeyValuePair<long, long> kv in p.HostBefore)
            {
                Element element = doc.GetElement(Rid.Make(kv.Key));
                long? now = HostOf(element);
                if (now == kv.Value)
                {
                    // ON ITS FACE, NOT MERELY OWNED BY ITS WALL. MEASURED: a face-hosted
                    // receptacle moved 200 mm across its wall kept the same host and
                    // ended up 38 mm out of the far face, and the point check verified it.
                    double? off = OffItsFaceMm(element);
                    if (off.HasValue && off.Value > FaceToleranceMm)
                        throw new InvalidOperationException(
                            "host_changed: " + p.Operation + " would leave element " + kv.Key + " " +
                            off.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                            " mm off the face of its host (element " + kv.Value + "). A face-hosted element moves " +
                            "along its face; one that belongs on another face is placed again there, which this " +
                            "command does not do");
                    continue;
                }
                throw new InvalidOperationException(
                    "host_changed: " + p.Operation + " would take element " + kv.Key + " off its host (element " +
                    kv.Value + ")" + (now.HasValue ? " onto element " + now.Value : ", leaving it with no host") +
                    ". A hosted element moves or turns only where its host carries it; one that must change host " +
                    "is placed again on the new one, which this command does not do");
            }
        }

        /// <summary>An instance's X and Z axes as Revit stores them; null for anything that has none.</summary>
        private static XYZ[] Axes(Element e)
        {
            var fi = e as FamilyInstance;
            if (fi == null) return null;
            try
            {
                Transform t = fi.GetTotalTransform();
                return new[] { t.BasisX, t.BasisZ };
            }
            catch { return null; }
        }

        /// <summary>
        /// change_type_by_rule's measures: short_side_mm/long_side_mm/area_m2, read from a face's
        /// OWN UV bounding box (see Core/TypeChangeRuleRules.ShortLong) - which for a PlanarFace is
        /// metric along the face's own orthonormal basis, so this is the real width and length, not
        /// an axis-aligned proxy for them. The face is the wall's own exterior side face for a Wall,
        /// or the largest top face for any other HostObject (Floor, RoofBase, Ceiling); anything else
        /// is left unmeasured rather than approximated from a bounding box, so a rule requiring these
        /// measures is refused for it by name rather than silently mismeasured. The same holds for a
        /// face that IS planar but not a plain rectangle (a hole, an L-shape, a chamfer): its bounding
        /// box would report a short/long side that is not the element's real geometry, so
        /// TypeChangeRuleRules.IsRectangularFace gates it and an unmeasured instance stays unmeasured
        /// rather than silently mismeasured - see its own doc comment for the three checks.
        /// </summary>
        private static Dictionary<string, double> MeasureInstance(Element e, out string whyUnmeasured)
        {
            var m = new Dictionary<string, double>(StringComparer.Ordinal);
            whyUnmeasured = null;
            try
            {
                Face primary = PrimaryFace(e);
                PlanarFace face = primary as PlanarFace;
                if (primary == null) whyUnmeasured = "no exterior side face (wall) or top face (floor, roof, ceiling) to measure";
                else if (face == null) whyUnmeasured = "its face is not planar";
                else
                {
                    BoundingBoxUV bb = face.GetBoundingBox();
                    double uExtentFt = bb.Max.U - bb.Min.U, vExtentFt = bb.Max.V - bb.Min.V;
                    double areaFt2 = face.Area;
                    EdgeArrayArray loops = face.EdgeLoops;
                    int loopCount = loops?.Size ?? 0;
                    int outerLoopEdgeCount = loopCount == 1 ? loops.get_Item(0).Size : -1;
                    whyUnmeasured = TypeChangeRuleRules.WhyNotRectangular(loopCount, outerLoopEdgeCount, areaFt2, uExtentFt, vExtentFt);
                    if (whyUnmeasured == null)
                    {
                        Tuple<double, double> sl = TypeChangeRuleRules.ShortLong(uExtentFt * 304.8, vExtentFt * 304.8);
                        m["short_side_mm"] = sl.Item1;
                        m["long_side_mm"] = sl.Item2;
                        m["area_m2"] = areaFt2 * 0.09290304;
                    }
                    // else: not a plain rectangle - left unmeasured on purpose, see the doc comment above.
                }
            }
            catch (Exception ex) { whyUnmeasured = "reading its face threw: " + ex.Message; }
            return m;
        }

        private static Face PrimaryFace(Element e)
        {
            IList<Reference> refs = null;
            var wall = e as Wall;
            if (wall != null) { try { refs = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior); } catch { } }
            else
            {
                var host = e as HostObject;
                if (host != null) { try { refs = HostObjectUtils.GetTopFaces(host); } catch { } }
            }
            if (refs == null || refs.Count == 0) return null;
            Face best = null; double bestArea = -1;
            foreach (Reference r in refs)
            {
                Face f;
                try { f = e.GetGeometryObjectFromReference(r) as Face; } catch { f = null; }
                if (f == null) continue;
                double a; try { a = f.Area; } catch { a = 0; }
                if (a > bestArea) { bestArea = a; best = f; }
            }
            return best;
        }

        private static List<XYZ> Samples(Element e, bool boundingBox = false)
        {
            var result = new List<XYZ>();
            if (boundingBox)
            {
                BoundingBoxXYZ box = null;
                // FilledRegion belongs to its view, so request its bounds in that
                // coordinate frame directly; get_BoundingBox(null) may be absent or
                // may describe a different frame for a view-specific element.
                if (e is FilledRegion && e.OwnerViewId != ElementId.InvalidElementId)
                    try { box = e.get_BoundingBox(e.Document.GetElement(e.OwnerViewId) as View); } catch { }
                if (box == null) try { box = e.get_BoundingBox(null); } catch { }
                // A view-specific element (a filled region, a detail item without a location)
                // has no model box: its box exists only in its owner view, in model coordinates.
                if (box == null)
                    try
                    {
                        if (e.OwnerViewId != ElementId.InvalidElementId)
                            box = e.get_BoundingBox(e.Document.GetElement(e.OwnerViewId) as View);
                    }
                    catch { }
                if (box != null) { result.Add(box.Min); result.Add(box.Max); }
                return result;
            }
            // A text note has no Location at all (field: move was refused for TextNote); its
            // anchor point Coord moves by exactly the vector and is re-read the same way.
            if (e is TextNote note)
            {
                try { result.Add(note.Coord); } catch { }
                return result;
            }
            LocationPoint point = e.Location as LocationPoint;
            if (point != null) { result.Add(point.Point); return result; }
            LocationCurve curve = e.Location as LocationCurve;
            if (curve != null && curve.Curve != null)
            { result.Add(curve.Curve.GetEndPoint(0)); result.Add(curve.Curve.GetEndPoint(1)); return result; }
            // A RevitLinkInstance exposes NO sampleable Location (measured live,
            // 2026-08-26) - yet moving a link is an everyday coordination operation,
            // and its verification story is as sound as any location's: the total
            // transform's origin is where the placement stands, and a committed move
            // must show that origin displaced by exactly the requested vector.
            // The same holds for an ImportInstance - a placed DWG - whose move is
            // what an incremental update has to detect (measured 2026-09-03: the
            // typed move refused it as unsampleable). Instance is the base of both.
            var instance = e as Instance;
            if (instance != null)
            {
                try
                {
                    Transform total = instance.GetTotalTransform();
                    if (total != null) result.Add(total.Origin);
                }
                catch { /* no sample stays no sample, and the refusal names it */ }
            }
            return result;
        }

        private sealed class RegionSnapshot
        {
            private long ownerView;
            private List<List<AnnotationMoveRules.Curve>> loops;
            public string Fingerprint;

            public static RegionSnapshot Read(Document doc, FilledRegion region)
            {
                if (region.OwnerViewId == ElementId.InvalidElementId || !(doc.GetElement(region.OwnerViewId) is View))
                    throw new ArgumentException("Filled region " + Rid.Value(region.Id) + " has no readable owner view; its contours cannot be verified. Check the owner view and retry.");
                var snapshot = new RegionSnapshot { ownerView = Rid.Value(region.OwnerViewId), loops = new List<List<AnnotationMoveRules.Curve>>() };
                foreach (CurveLoop loop in region.GetBoundaries())
                {
                    var curves = new List<AnnotationMoveRules.Curve>();
                    foreach (Autodesk.Revit.DB.Curve curve in loop)
                    {
                        var points = curve.Tessellate();
                        if (points == null || points.Count < 2) throw new ArgumentException("Filled region has an unreadable boundary curve.");
                        curves.Add(new AnnotationMoveRules.Curve { Kind = curve.GetType().FullName,
                            Points = points.Select(x => new[] { x.X, x.Y, x.Z }).ToList() });
                    }
                    if (curves.Count == 0) throw new ArgumentException("Filled region has an empty boundary loop.");
                    snapshot.loops.Add(curves);
                }
                if (snapshot.loops.Count == 0) throw new ArgumentException("Filled region has no readable boundaries.");
                snapshot.Fingerprint = JsonConvert.SerializeObject(new { snapshot.ownerView, snapshot.loops });
                return snapshot;
            }

            public bool Matches(Document doc, FilledRegion region, XYZ vector)
            {
                try
                {
                    RegionSnapshot after = Read(doc, region);
                    return ownerView == after.ownerView && AnnotationMoveRules.ContoursMoved(loops, after.loops, new[] { vector.X, vector.Y, vector.Z });
                }
                catch { return false; }
            }
        }

        // A floor has no LocationPoint/LocationCurve. Its own sketch is the copy
        // postcondition: every boundary loop (including openings) must translate,
        // while the level plus height offset supplies an independent elevation read.
        private sealed class FloorCopySnapshot
        {
            private sealed class CurveSample
            {
                public string Kind;
                public List<XYZ> Points;
            }

            public ElementId TypeId;
            public long LevelId;
            public double LevelElevation, Offset;
            public double AbsoluteElevation;
            public XYZ PhysicalMin, PhysicalMax;
            public double SolidVolume;
            private List<List<CurveSample>> Loops;
            private List<List<List<CurveSample>>> HostedOpenings;
            public string Fingerprint;
            public int LoopCount => Loops.Count;
            public int HostedOpeningCount => HostedOpenings.Count;

            public static FloorCopySnapshot Read(Document doc, Floor floor)
            {
                if (floor == null) throw new ArgumentException("Floor copy requires a floor.");
                GuardSimpleFloor(doc, floor);
                Sketch sketch = floor.SketchId == ElementId.InvalidElementId ? null : doc.GetElement(floor.SketchId) as Sketch;
                if (sketch?.Profile == null)
                    throw new ArgumentException("Floor " + Rid.Value(floor.Id) + " has no readable sketch; its copy cannot be verified.");
                Level level = doc.GetElement(floor.LevelId) as Level;
                Parameter offset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (level == null || offset == null || offset.StorageType != StorageType.Double)
                    throw new ArgumentException("Floor " + Rid.Value(floor.Id) + " has no readable level and height offset; its copy elevation cannot be verified.");
                List<List<CurveSample>> loops = ReadLoops(sketch, Rid.Value(floor.Id));
                BoundingBoxXYZ physicalBox = floor.get_BoundingBox(null);
                GeometryElement geometry = floor.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                double solidVolume = geometry == null ? 0 : geometry.OfType<Solid>()
                    .Where(s => s != null && s.Faces.Size > 0 && s.Volume > 0).Sum(s => s.Volume);
                if (physicalBox == null || solidVolume <= 1e-9)
                    throw new ArgumentException("Floor " + Rid.Value(floor.Id) +
                        " has no readable physical bounding box or positive solid volume; its copied body cannot be verified.");
                var openings = new List<List<List<CurveSample>>>();
                foreach (Opening opening in new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>())
                {
                    if (opening.Host?.Id != floor.Id) continue;
                    Sketch openingSketch = opening.SketchId == ElementId.InvalidElementId ? null : doc.GetElement(opening.SketchId) as Sketch;
                    if (openingSketch?.Profile == null)
                        throw new ArgumentException("Floor " + Rid.Value(floor.Id) + " has hosted opening " + Rid.Value(opening.Id) +
                            " without a readable sketch; copying it cannot be verified.");
                    openings.Add(ReadLoops(openingSketch, Rid.Value(opening.Id)));
                }
                var snapshot = new FloorCopySnapshot
                {
                    TypeId = floor.GetTypeId(),
                    LevelId = Rid.Value(level.Id),
                    LevelElevation = level.Elevation,
                    Offset = offset.AsDouble(),
                    AbsoluteElevation = level.Elevation + offset.AsDouble(),
                    PhysicalMin = physicalBox.Min,
                    PhysicalMax = physicalBox.Max,
                    SolidVolume = solidVolume,
                    Loops = loops,
                    HostedOpenings = openings
                };
                // The dry-run fingerprint detects sketch edits, including opening
                // edits, even if the slab's bounding box has not changed.
                snapshot.Fingerprint = JsonConvert.SerializeObject(new
                {
                    type = Rid.Value(snapshot.TypeId),
                    elevation = Math.Round(snapshot.AbsoluteElevation, 7),
                    physical_min = new[] { Math.Round(snapshot.PhysicalMin.X, 7), Math.Round(snapshot.PhysicalMin.Y, 7), Math.Round(snapshot.PhysicalMin.Z, 7) },
                    physical_max = new[] { Math.Round(snapshot.PhysicalMax.X, 7), Math.Round(snapshot.PhysicalMax.Y, 7), Math.Round(snapshot.PhysicalMax.Z, 7) },
                    volume = Math.Round(snapshot.SolidVolume, 7),
                    loops = FingerprintLoops(loops),
                    hosted_openings = openings.Select(FingerprintLoops)
                });
                return snapshot;
            }

            private static void GuardSimpleFloor(Document doc, Floor floor)
            {
#if REVIT2023
                SlabShapeEditor shape = floor.SlabShapeEditor;
#else
                SlabShapeEditor shape = floor.GetSlabShapeEditor();
#endif
                if (shape == null || shape.IsEnabled)
                    throw new ArgumentException("Floor " + Rid.Value(floor.Id) +
                        " has slab shape editing enabled or its shape state is unreadable; copy cannot verify vertices and creases.");
                FloorType type = doc.GetElement(floor.GetTypeId()) as FloorType;
                CompoundStructure structure = type?.GetCompoundStructure();
                if (structure == null || structure.VariableLayerIndex >= 0)
                    throw new ArgumentException("Floor " + Rid.Value(floor.Id) +
                        " has a variable thickness layer or its layer structure is unreadable; copy cannot verify thickness.");
                IList<Reference> topFaces = HostObjectUtils.GetTopFaces(floor);
                if (topFaces == null || topFaces.Count == 0)
                    throw new ArgumentException("Floor " + Rid.Value(floor.Id) +
                        " has no readable top face; copy cannot verify slope.");
                foreach (Reference faceRef in topFaces)
                {
                    PlanarFace face = floor.GetGeometryObjectFromReference(faceRef) as PlanarFace;
                    if (face == null || face.FaceNormal.DotProduct(XYZ.BasisZ) < 1 - 1e-6)
                        throw new ArgumentException("Floor " + Rid.Value(floor.Id) +
                            " has a sloped or nonplanar top face; copy cannot verify its slope from the sketch and level offset.");
                }
            }

            private static List<List<CurveSample>> ReadLoops(Sketch sketch, long ownerId)
            {
                var loops = new List<List<CurveSample>>();
                foreach (CurveArray profile in sketch.Profile)
                {
                    var loop = new List<CurveSample>();
                    foreach (Curve curve in profile)
                    {
                        IList<XYZ> points = curve.Tessellate();
                        if (points == null || points.Count < 2)
                            throw new ArgumentException("Element " + ownerId + " has a sketch curve that cannot be sampled.");
                        loop.Add(new CurveSample { Kind = curve.GetType().FullName, Points = points.ToList() });
                    }
                    if (loop.Count == 0) throw new ArgumentException("Element " + ownerId + " has an empty sketch loop.");
                    loops.Add(loop);
                }
                if (loops.Count == 0) throw new ArgumentException("Element " + ownerId + " has no sketch loops.");
                return loops;
            }

            private static object FingerprintLoops(List<List<CurveSample>> loops)
            {
                return loops.Select(loop => loop.Select(c => new
                {
                    kind = c.Kind,
                    points = c.Points.Select(x => new[] { Math.Round(x.X, 7), Math.Round(x.Y, 7), Math.Round(x.Z, 7) })
                }));
            }

            public static bool Matches(Document doc, FloorCopySnapshot source, Floor copy, XYZ vector)
            {
                if (copy == null || copy.GetTypeId() != source.TypeId) return false;
                FloorCopySnapshot target;
                try { target = Read(doc, copy); }
                catch { return false; }
                if (Math.Abs(target.AbsoluteElevation - source.AbsoluteElevation - vector.Z) > 1e-5 ||
                    !PhysicalBodyMatches(source, target, vector)) return false;
                // Revit may keep Sketch.Profile in its source-level plane and
                // move the floor by changing FLOOR_HEIGHTABOVELEVEL_PARAM. The
                // physical body and absolute elevation must still translate.
                XYZ xyOnly = new XYZ(vector.X, vector.Y, 0);
                bool profile = SameLoops(source.Loops, target.Loops, vector) ||
                               SameLoops(source.Loops, target.Loops, xyOnly);
                bool openings = SameOpeningSets(source.HostedOpenings, target.HostedOpenings, vector) ||
                                SameOpeningSets(source.HostedOpenings, target.HostedOpenings, xyOnly);
                // A level reassignment can put the profile in another local Z plane.
                // Normalize only that plane; the independent body/elevation checks above
                // still require the complete requested physical translation.
                XYZ profilePlane = new XYZ(vector.X, vector.Y, FirstZ(target.Loops) - FirstZ(source.Loops));
                profile |= SameLoops(source.Loops, target.Loops, profilePlane);
                if (!openings && source.HostedOpenings.Count == target.HostedOpenings.Count)
                    openings = SameOpeningSets(source.HostedOpenings, target.HostedOpenings, profilePlane);
                return profile && openings;
            }

            private static double FirstZ(List<List<CurveSample>> loops) => loops[0][0].Points[0].Z;

            private static bool PhysicalBodyMatches(FloorCopySnapshot source, FloorCopySnapshot target, XYZ vector)
            {
                const double positionTolerance = 1e-5;
                double volumeTolerance = Math.Max(1e-6, source.SolidVolume * 1e-5);
                return (source.PhysicalMin + vector).DistanceTo(target.PhysicalMin) <= positionTolerance &&
                       (source.PhysicalMax + vector).DistanceTo(target.PhysicalMax) <= positionTolerance &&
                       Math.Abs(source.SolidVolume - target.SolidVolume) <= volumeTolerance;
            }

            public static JObject Diagnose(Document doc, FloorCopySnapshot source, Element candidate, XYZ vector)
            {
                var result = new JObject
                {
                    ["candidate_id"] = Rid.Value(candidate.Id),
                    ["candidate_class"] = candidate.GetType().Name,
                    ["source_type_id"] = Rid.Value(source.TypeId),
                    ["candidate_type_id"] = Rid.Value(candidate.GetTypeId()),
                    ["source_level_id"] = source.LevelId,
                    ["source_level_elevation_feet"] = source.LevelElevation,
                    ["source_offset_feet"] = source.Offset,
                    ["source_absolute_elevation_feet"] = source.AbsoluteElevation,
                    ["expected_absolute_elevation_feet"] = source.AbsoluteElevation + vector.Z,
                    ["source_sketch_loops"] = source.Loops.Count,
                    ["source_hosted_openings"] = source.HostedOpenings.Count
                };
                if (!(candidate is Floor floor)) return result;
                FloorCopySnapshot target;
                try { target = Read(doc, floor); }
                catch (Exception ex) { result["candidate_read_error"] = ex.Message; return result; }
                result["candidate_level_id"] = target.LevelId;
                result["candidate_level_elevation_feet"] = target.LevelElevation;
                result["candidate_offset_feet"] = target.Offset;
                result["candidate_absolute_elevation_feet"] = target.AbsoluteElevation;
                result["elevation_delta_feet"] = target.AbsoluteElevation - source.AbsoluteElevation;
                result["candidate_sketch_loops"] = target.Loops.Count;
                result["candidate_hosted_openings"] = target.HostedOpenings.Count;
                result["sketch_matches_full_vector"] = SameLoops(source.Loops, target.Loops, vector);
                result["sketch_matches_xy_without_z_shift"] = SameLoops(source.Loops, target.Loops, new XYZ(vector.X, vector.Y, 0));
                result["source_sketch_z_range_feet"] = ZRange(source.Loops);
                result["candidate_sketch_z_range_feet"] = ZRange(target.Loops);
                result["hosted_openings_match_full_vector"] = SameOpeningSets(source.HostedOpenings, target.HostedOpenings, vector);
                result["hosted_openings_match_xy_without_z_shift"] = SameOpeningSets(source.HostedOpenings, target.HostedOpenings, new XYZ(vector.X, vector.Y, 0));
                result["source_physical_min_feet"] = Arr(source.PhysicalMin);
                result["source_physical_max_feet"] = Arr(source.PhysicalMax);
                result["candidate_physical_min_feet"] = Arr(target.PhysicalMin);
                result["candidate_physical_max_feet"] = Arr(target.PhysicalMax);
                result["physical_body_translated"] = PhysicalBodyMatches(source, target, vector);
                result["source_solid_volume_cubic_feet"] = source.SolidVolume;
                result["candidate_solid_volume_cubic_feet"] = target.SolidVolume;
                return result;
            }

            private static JArray ZRange(List<List<CurveSample>> loops)
            {
                var z = loops.SelectMany(loop => loop).SelectMany(curve => curve.Points).Select(point => point.Z).ToList();
                return new JArray(z.Min(), z.Max());
            }

            private static bool SameOpeningSets(List<List<List<CurveSample>>> source,
                                                List<List<List<CurveSample>>> target, XYZ vector)
            {
                if (source.Count != target.Count) return false;
                var remaining = new List<List<List<CurveSample>>>(target);
                foreach (List<List<CurveSample>> opening in source)
                {
                    int hit = remaining.FindIndex(candidate => SameLoops(opening, candidate, vector));
                    if (hit < 0) return false;
                    remaining.RemoveAt(hit);
                }
                return remaining.Count == 0;
            }

            private static bool SameLoops(List<List<CurveSample>> source, List<List<CurveSample>> target, XYZ vector)
            {
                if (source.Count != target.Count) return false;
                var unmatched = new List<List<CurveSample>>(target);
                foreach (List<CurveSample> loop in source)
                {
                    int hit = unmatched.FindIndex(candidate => SameLoop(loop, candidate, vector));
                    if (hit < 0) return false;
                    unmatched.RemoveAt(hit);
                }
                return unmatched.Count == 0;
            }

            private static bool SameLoop(List<CurveSample> source, List<CurveSample> target, XYZ vector)
            {
                if (source.Count != target.Count) return false;
                var unmatched = new List<CurveSample>(target);
                foreach (CurveSample curve in source)
                {
                    int hit = unmatched.FindIndex(candidate => SameCurve(curve, candidate, vector));
                    if (hit < 0) return false;
                    unmatched.RemoveAt(hit);
                }
                return unmatched.Count == 0;
            }

            private static bool SameCurve(CurveSample source, CurveSample target, XYZ vector)
            {
                if (source.Kind != target.Kind || source.Points.Count != target.Points.Count) return false;
                bool forward = true, reverse = true;
                int count = source.Points.Count;
                for (int i = 0; i < count; i++)
                {
                    XYZ expected = source.Points[i] + vector;
                    forward &= expected.DistanceTo(target.Points[i]) <= 1e-5;
                    reverse &= expected.DistanceTo(target.Points[count - 1 - i]) <= 1e-5;
                }
                return forward || reverse;
            }
        }

        private static XYZ Point(JToken token, double scale, string name)
        {
            JArray a = token as JArray;
            if (a == null || a.Count != 3) throw new ArgumentException(name + " must contain three coordinates");
            return new XYZ(a[0].Value<double>() * scale, a[1].Value<double>() * scale, a[2].Value<double>() * scale);
        }
        private static bool Scale(string units, out double scale)
        { if (units == "feet") { scale = 1; return true; } if (units == "m") { scale = 1 / 0.3048; return true; } if (units == "mm") { scale = 1 / 304.8; return true; } scale = 0; return false; }

        /// <summary>
        /// Identity for the plan. Every read here is guarded: a plan that throws while
        /// being MEASURED would turn a safety feature into a new way to fail.
        /// </summary>
        private static string SafePlanUniqueId(Element e)
        {
            try { return e.UniqueId; } catch { return null; }
        }

        private static string SafePlanTypeName(Document doc, Element e)
        {
            try
            {
                ElementId tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) return null;
                Element t = doc.GetElement(tid);
                return t == null ? null : t.Name;
            }
            catch { return null; }
        }

        /// <summary>
        /// A rounded bounding box: enough to notice that an element moved or changed shape
        /// between the rehearsal and the apply, cheap enough to take for every element in a
        /// batch. Rounded to a millimetre because Revit's own regeneration jitters the last
        /// digits, and a fingerprint that changes on its own would refuse every apply.
        /// </summary>
        private static string SafePlanGeometry(Element e)
        {
            try
            {
                if (e is TextNote note)
                {
                    View view = e.Document.GetElement(e.OwnerViewId) as View;
                    if (view == null) return null;
                    return string.Join(",", D3(note.Coord).Concat(D3(view.ViewDirection)).Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).ToArray());
                }
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                if (b == null) return null;
                const double mm = 304.8;   // feet -> mm
                return string.Join(",", new[]
                {
                    Math.Round(b.Min.X * mm), Math.Round(b.Min.Y * mm), Math.Round(b.Min.Z * mm),
                    Math.Round(b.Max.X * mm), Math.Round(b.Max.Y * mm), Math.Round(b.Max.Z * mm)
                }.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
            }
            catch { return null; }
        }

        // ---- arrays -------------------------------------------------------------------
        // LinearArray / RadialArray, with or without association (group=true keeps the
        // Revit array element). Every copy is re-read and matched against the formula:
        // member k of each source sits at the source's samples moved by k*step (linear)
        // or turned by k*step angle about the axis (radial). anchor=last divides the
        // vector/angle over count-1 steps, a full turn over count
        // (ArchitecturalEditRules.ArrayStepVector / ArrayStepAngle).

        private static void PlanArray(Document doc, JObject o, double scale, Plan p)
        {
            bool radial = p.Operation == "array_radial";
            if (o["count"]?.Type != JTokenType.Integer) throw new ArgumentException(p.Operation + " requires an integer count (members including the original).");
            p.Count = o.Value<int>("count");
            string countError = ArchitecturalEditRules.ValidateArrayCount(radial, p.Count);
            if (countError != null) throw new ArgumentException(countError);
            string anchor = (o.Value<string>("anchor") ?? "second").ToLowerInvariant();
            if (anchor != "second" && anchor != "last") throw new ArgumentException("anchor must be second or last.");
            p.AnchorLast = anchor == "last";
            if (o["group"] != null && o["group"].Type != JTokenType.Boolean) throw new ArgumentException("group must be a boolean.");
            p.Grouped = o.Value<bool?>("group") ?? false;
            View view = doc.ActiveView;
            if (view == null || view is ViewSheet || view is ViewSchedule)
                throw new ArgumentException("arrays are created in the ACTIVE view, and the active view is " + (view == null ? "none" : "a " + view.GetType().Name) + "; activate a plan, section or 3D view.");
            foreach (ElementId id in p.Ids)
                if (!LinearArray.IsElementArrayable(doc, id))
                    throw new ArgumentException("ElementId " + Rid.Value(id) + " cannot be arrayed (LinearArray.IsElementArrayable is false).");
            if (!radial)
            {
                XYZ v = Point(o["vector"], scale, "vector");
                if (v.GetLength() < 1e-9) throw new ArgumentException("vector must not be zero");
                double[] s = ArchitecturalEditRules.ArrayStepVector(new[] { v.X, v.Y, v.Z }, p.Count, p.AnchorLast);
                p.Vector = v; p.Step = new XYZ(s[0], s[1], s[2]);
            }
            else
            {
                XYZ a = Point(o["axis_start"], scale, "axis_start"), b = Point(o["axis_end"], scale, "axis_end");
                if (a.DistanceTo(b) < 1e-9) throw new ArgumentException("rotation axis endpoints must differ");
                if (o["angle_degrees"] == null) throw new ArgumentException("angle_degrees is required for array_radial");
                p.Axis = Line.CreateBound(a, b); p.AxisOrigin = a; p.AxisDirection = (b - a).Normalize();
                p.Angle = o.Value<double>("angle_degrees") * Math.PI / 180.0;
                if (Math.Abs(p.Angle) < 1e-9) throw new ArgumentException("angle_degrees must not be zero");
                p.StepAngle = ArchitecturalEditRules.ArrayStepAngle(p.Angle, p.Count, p.AnchorLast);
            }
        }

        private static void ApplyArray(Document doc, Plan p)
        {
            View view = doc.ActiveView;
            ArrayAnchorMember anchor = p.AnchorLast ? ArrayAnchorMember.Last : ArrayAnchorMember.Second;
            ICollection<ElementId> made;
            if (p.Operation == "array_linear")
            {
                if (p.Grouped)
                {
                    LinearArray array = LinearArray.Create(doc, view, p.Ids, p.Count, p.Vector, anchor);
                    p.ArrayId = Rid.Value(array.Id); made = array.GetCopiedMemberIds();
                }
                else made = LinearArray.ArrayElementsWithoutAssociation(doc, view, p.Ids, p.Count, p.Vector, anchor);
            }
            else
            {
                if (p.Grouped)
                {
                    RadialArray array = RadialArray.Create(doc, view, p.Ids, p.Count, p.Axis, p.Angle, anchor);
                    p.ArrayId = Rid.Value(array.Id); made = array.GetCopiedMemberIds();
                }
                else made = RadialArray.ArrayElementsWithoutAssociation(doc, view, p.Ids, p.Count, p.Axis, p.Angle, anchor);
            }
            // ArrayElementsWithoutAssociation can return the ORIGINAL elements beside the copies: they
            // are not created, and a rehearsal that counted them would find them still present after
            // its rollback and call a clean rollback uncertain (MEASURED 2026-11-21, release gate).
            var sourceIds = new HashSet<long>(p.Ids.Select(Rid.Value));
            p.Created = (made ?? new List<ElementId>()).Where(id => !sourceIds.Contains(Rid.Value(id))).ToList();
        }

        private static bool VerifyArray(Document doc, Plan p, JObject detail)
        {
            bool grouped = p.Grouped;
            var required = new List<string> { "copies_present", "copies_at_formula", "hosted_copies_on_host" };
            if (grouped) required.Add("array_element");
            var check = new PostconditionCheck(required.ToArray());
            // Leaves: a member that Revit wrapped in a group is judged by what it holds.
            var leaves = new List<Element>();
            foreach (ElementId id in p.Created ?? new List<ElementId>())
            {
                Element e = doc.GetElement(id);
                if (e is Group g) leaves.AddRange(g.GetMemberIds().Select(doc.GetElement).Where(x => x != null));
                else if (e != null) leaves.Add(e);
            }
            var sources = new HashSet<long>(p.Ids.Select(Rid.Value));
            leaves = leaves.Where(e => !sources.Contains(Rid.Value(e.Id))).ToList();
            int expected = p.Ids.Count * (p.Count - 1);
            int matched = 0;
            var misses = new JArray();
            var remaining = new List<Element>(leaves);
            foreach (ElementId sourceId in p.Ids)
            {
                Element source = doc.GetElement(sourceId);
                List<XYZ> before = p.Samples[Rid.Value(sourceId)];
                for (int k = 1; k < p.Count; k++)
                {
                    Func<XYZ, XYZ> member = p.Operation == "array_linear"
                        ? (Func<XYZ, XYZ>)(x => x + p.Step * k)
                        : (x => Transform.CreateRotationAtPoint(p.AxisDirection, p.StepAngle * k, p.AxisOrigin).OfPoint(x));
                    var want = before.Select(member).ToList();
                    int hit = remaining.FindIndex(e => source != null && e.GetTypeId() == source.GetTypeId() && e.GetType() == source.GetType() &&
                        SamplesNear(want, Samples(e)));
                    if (hit < 0) { misses.Add(new JObject { ["source_id"] = Rid.Value(sourceId), ["member"] = k, ["expected_feet"] = new JArray(want.Select(Arr)) }); continue; }
                    matched++; remaining.RemoveAt(hit);
                }
            }
            check.Compare("copies_at_formula", expected, matched);
            // Inserts copied with their hosts are dependents, not extra members (audit 2026-10-08, 6.8).
            var members = leaves.Except(remaining).ToList();
            int dependents = Dependents(remaining, members.Select(e => Rid.Value(e.Id)), detail);
            check.Compare("copies_present", expected, leaves.Count - dependents);
            var hostProblems = new JArray();
            foreach (Element member in members)
            {
                long? sourceOf = SourceOf(p, doc, member);
                string problem = sourceOf.HasValue ? HostedCopyProblem(p, sourceOf.Value, member) : null;
                if (problem != null) hostProblems.Add(problem);
            }
            if (hostProblems.Count > 0) detail["hosted_copies_off_host"] = hostProblems;
            check.Compare("hosted_copies_on_host", 0, hostProblems.Count);
            if (grouped)
                check.Compare("array_element", true, p.ArrayId.HasValue && doc.GetElement(Rid.Make(p.ArrayId.Value)) is BaseArray);
            detail["created_ids"] = new JArray(leaves.Select(e => (JToken)Rid.Value(e.Id)));
            detail["array_id"] = p.ArrayId.HasValue ? (JToken)p.ArrayId.Value : JValue.CreateNull();
            detail["step_feet"] = p.Step == null ? null : Arr(p.Step);
            if (p.Operation == "array_radial") detail["step_degrees"] = p.StepAngle * 180 / Math.PI;
            if (misses.Count > 0) detail["members_not_at_formula"] = misses;
            detail["postconditions"] = check.ToJson();
            return check.AllVerified;
        }

        /// <summary>Same samples within 1e-5 ft, in order or (for a curve) reversed.</summary>
        private static bool SamplesNear(List<XYZ> want, List<XYZ> got)
        {
            if (want.Count != got.Count || want.Count == 0) return false;
            bool fwd = true, rev = true;
            for (int i = 0; i < want.Count; i++)
            {
                fwd &= want[i].DistanceTo(got[i]) <= 1e-5;
                rev &= want[i].DistanceTo(got[got.Count - 1 - i]) <= 1e-5;
            }
            return fwd || (want.Count == 2 && rev);
        }

        /// <summary>A wall's base offset in feet; NaN when it cannot be read.</summary>
        private static double WallBaseOffset(Wall wall)
        {
            try
            {
                Parameter offset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                return offset == null || !offset.HasValue ? double.NaN : offset.AsDouble();
            }
            catch { return double.NaN; }
        }

        /// <summary>A wall's base as measured on Revit 2025: its line's z (the base level's elevation) plus its base offset; NaN unreadable.</summary>
        private static double WallBase(Wall wall)
        {
            try
            {
                Curve line = (wall.Location as LocationCurve)?.Curve;
                return line == null ? double.NaN : (line.GetEndPoint(0).Z + line.GetEndPoint(1).Z) / 2 + WallBaseOffset(wall);
            }
            catch { return double.NaN; }
        }

        private sealed class Plan
        {
            public int JoinEnd; public bool JoinAllowed;
            public Element AttachmentTarget = null;
            public HashSet<long> AlreadyAttached = new HashSet<long>();
#if REVIT2025 || REVIT2026 || REVIT2027
            public AttachmentLocation AttachmentPosition;
#endif
            public double[] ExpectedEndElevations = Array.Empty<double>(); public double AttachmentToleranceFeet = 0;
            public readonly Dictionary<long, CopySignature> CopyGeometry = new Dictionary<long, CopySignature>();
            public int Index; public string Operation; public List<ElementId> Ids, Created; public XYZ Vector;
            public Line Axis; public double Angle; public Transform Rotation; public ElementId TypeId;
            /// <summary>change_type_by_rule: the rule set, and per-instance the type it resolved to,
            /// the measures it was resolved from, and the match itself (for the plan/reply).</summary>
            public TypeChangeRuleSet RuleSet;
            public readonly Dictionary<long, ElementId> RuleTargets = new Dictionary<long, ElementId>();
            public readonly Dictionary<long, Dictionary<string, double>> RuleMeasured = new Dictionary<long, Dictionary<string, double>>();
            public readonly Dictionary<long, TypeChangeMatch> RuleMatches = new Dictionary<long, TypeChangeMatch>();
            /// <summary>mirror: the plane reflected about. Rotation then holds the reflection, for verification.</summary>
            public Plane MirrorPlane;
            /// <summary>set_curve: the line this element's LocationCurve is being set to.</summary>
            public Line Curve;
            public bool ReleaseEndJoins;
            public readonly Dictionary<long, bool[]> EndJoins = new Dictionary<long, bool[]>();
            public readonly Dictionary<long, string[][]> JoinPeers = new Dictionary<long, string[][]>();
            public readonly Dictionary<string, PlannedElement> JoinNeighbors = new Dictionary<string, PlannedElement>();
            public Dictionary<long, List<XYZ>> Samples; public JObject Summary;
            /// <summary>set_curve on a wall: its base level elevation plus base offset before the write (feet; NaN unreadable).</summary>
            public readonly Dictionary<long, double> WallBaseBefore = new Dictionary<long, double>();
            /// <summary>Elements verified by the corners of their bounding box (no location to sample); move only.</summary>
            public readonly Dictionary<long, XYZ> NoteVectors = new Dictionary<long, XYZ>();
            public readonly HashSet<long> BoxSampled = new HashSet<long>();
            public readonly Dictionary<long, RegionSnapshot> RegionContours = new Dictionary<long, RegionSnapshot>();
            public readonly Dictionary<long, FloorCopySnapshot> FloorCopies = new Dictionary<long, FloorCopySnapshot>();
            public readonly Dictionary<long, CopySignature> CopyWalls = new Dictionary<long, CopySignature>();
            /// <summary>rotate: each instance's X and Z axes before the turn.</summary>
            public readonly Dictionary<long, XYZ[]> Axes = new Dictionary<long, XYZ[]>();
            /// <summary>move/rotate: the host each hosted instance had before, which it must keep.</summary>
            public readonly Dictionary<long, long> HostBefore = new Dictionary<long, long>();
            // move_tag_head / set_tag_leader
            public XYZ HeadPoint;
            public readonly Dictionary<long, XYZ> HeadBefore = new Dictionary<long, XYZ>();
            public readonly Dictionary<long, Reference> TagRefs = new Dictionary<long, Reference>();
            public bool? HasLeader, LeaderVisible;
            public LeaderEndCondition? EndCondition;
            public XYZ LeaderEnd, LeaderElbow;
            // array_linear / array_radial
            public int Count; public bool AnchorLast, Grouped; public XYZ Step; public double StepAngle; public XYZ AxisDirection, AxisOrigin;
            public long? ArrayId;
            // rename_level
            public string OldLevelName, NewLevelName;
            public readonly List<long> LevelCandidateViewIds = new List<long>();
        }

        /// <summary>
        /// The ONLY way to know which Copy/Monitor alerts a level rename raises: perform
        /// the rename inside a transaction whose failure preprocessor captures every
        /// message (errors AND warnings - Copy/Monitor alerts are warnings), regenerate
        /// so Revit's coordination engine actually runs, then roll back. Which plan views
        /// will rename is NOT measured here - that is a deterministic read done once at
        /// plan time (p.LevelCandidateViewIds), because Revit's rule (exact name match)
        /// needs no rehearsal.
        /// </summary>
        private static JObject RehearseRenameLevel(Document doc, Plan p)
        {
            var views = new JArray();
            RevitErrorRecorder log = null;
            Guard.RollbackResult? rb = null; string rbError = null; string error = null;
            using (var tx = new Transaction(doc, "Horizun: rehearse rename_level"))
            {
                try
                {
                    log = RevitErrorRecorder.On(tx, captureWarnings: true);
                    tx.Start();
                    Level level = doc.GetElement(p.Ids[0]) as Level;
                    level.Name = p.NewLevelName;
                    doc.Regenerate();
                    foreach (long vid in p.LevelCandidateViewIds)
                    {
                        View v = doc.GetElement(Rid.Make(vid)) as View;
                        string now = null; try { now = v?.Name; } catch { }
                        views.Add(new JObject { ["view_id"] = vid, ["from"] = p.OldLevelName, ["to"] = now, ["will_rename"] = now == p.NewLevelName });
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                try { rb = Guard.RollBack(tx); } catch (Exception ex) { rbError = ex.Message; }
            }
            return new JObject
            {
                ["level_id"] = Rid.Value(p.Ids[0]),
                ["from"] = p.OldLevelName,
                ["to"] = p.NewLevelName,
                ["views_expected_to_rename"] = views,
                ["copy_monitor_alerts"] = new JArray(log?.Warnings ?? new List<string>()),
                ["errors_raised"] = new JArray(log?.Errors ?? new List<string>()),
                ["error"] = error,
                ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "exception: " + rbError,
                ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed,
                ["means"] = "views_expected_to_rename is a deterministic read: Revit renames a plan view only " +
                            "when its name exactly matches the level's, before the rename. copy_monitor_alerts " +
                            "and errors_raised come from an ACTUAL rename performed inside this rolled-back " +
                            "transaction, because Revit's Copy/Monitor coordination engine is the only source of " +
                            "truth for what it will raise."
            };
        }
    }
}
