// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_apply_cad_update - carry out an incremental plan AND remember it.
//
// The update could be sent through horizun_execute_plan, and for one run it would
// work. The second run is the problem: elements created that way carry no
// provenance, so the next update reads them as things the drawing asks for and
// nothing has built, and creates them AGAIN. Measured live, 2026-08-27 - two
// walls where the drawing shows one.
//
// So this command exists for the same reason horizun_apply_cad_plan does. It
// creates nothing itself: creates go through the typed horizun_create_elements
// and re-shapes through the typed horizun_transform_elements, both of which
// rehearse, confirm and re-read their own work. What it adds is the memory - a
// provenance stamp on everything it touches, carrying the entity, the rule, the
// requirement set, and the geometry AS BUILT, read back off the element after
// the commit rather than off the request.
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
    public sealed class ApplyCadUpdateCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        public ApplyCadUpdateCommand(Func<string, ICommand> resolve) { _resolve = resolve; }

        public string Name => "horizun_apply_cad_update";

        public string Description =>
            "Carry out an incremental CAD plan through the typed commands, and stamp what it touched.";

        public CommandResult Execute(UIApplication uiApp, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("The arguments are not valid JSON: " + ex.Message); }

            // THE SHARED GATE, not a hand-rolled title check.
            //
            // A repo guard caught this one: every command that opens a transaction
            // must resolve its document through DocumentGate.ForMutation, or it
            // writes to whatever window is in front. The hand-rolled version here
            // compared titles and looked equivalent - it is not, because the gate
            // is where the required target_document, the active-document rule and
            // the wording of the refusal all live, in ONE place that stays right
            // when any of them changes.
            GateResult gate = DocumentGate.ForMutation(uiApp, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = uiApp?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            string title = SafeTitle(doc);

            JToken actionsToken = request["actions"];
            JArray actions = actionsToken as JArray;
            if (actionsToken != null && actionsToken.Type != JTokenType.Null && actions == null)
                return CommandResult.Fail(
                    "actions_not_a_list: 'actions' arrived as " + actionsToken.Type.ToString().ToLowerInvariant() +
                    " and it must be the LIST horizun_plan_cad_update emitted. Nothing was written.");
            JArray index = request["candidate_index"] as JArray ?? new JArray();
            List<JObject> restampRows = index.OfType<JObject>()
                .Where(x => string.Equals(x.Value<string>("key"), RestampKey, StringComparison.Ordinal))
                .ToList();
            actions = actions ?? new JArray();
            // A plan may carry NOTHING to build and still have work: v1 records to
            // migrate, or elements to re-stamp under a moved placement. That is a
            // write with a visible count, not "nothing automatic".
            // NOTHING AUTOMATIC IS AN ANSWER, NOT A SUCCESS: said as such, with nothing written,
            // so a procedure can move on to the decisions a person owes instead of stopping.
            if (actions.Count == 0 && restampRows.Count == 0)
                return CommandResult.Ok(new JObject
                {
                    ["document"] = request.Value<string>("target_document"),
                    ["dry_run"] = request["dry_run"] == null || request.Value<bool>("dry_run"),
                    ["state"] = "nothing_to_apply",
                    ["applied"] = new JArray(),
                    ["stages_failed"] = 0,
                    ["actions_attempted"] = 0,
                    ["actions_failed"] = 0,
                    ["written"] = 0,
                    ["means"] = "the update plan carried no automatic action and nothing to re-stamp: what it " +
                                "found is waiting for a person (see its held rows), or nothing changed. Nothing " +
                                "was written, and this reply claims nothing was."
                });

            JObject provenanceTemplate = request["provenance"] as JObject;
            if (provenanceTemplate == null)
                return CommandResult.Fail(
                    "provenance is required: which drawing, which rules, which plan. Without it this command " +
                    "would create elements that remember nothing, and the NEXT update would build them again. " +
                    "Copy it from horizun_plan_cad_update's reply.");

            // THE PLACEMENT HALF OF THE STAMP, from the plan. A plan made under an
            // accepted placement move is a plan that re-shapes elements to follow
            // the drawing; the WRITE is where that consent has to be said again,
            // because the plan was read-only and this is not.
            JObject placementTemplate = provenanceTemplate["placement"] as JObject;
            bool planUnderMove = provenanceTemplate.Value<bool?>("placement_move_accepted") ?? false;
            bool acceptMove = request.Value<bool?>("accept_placement_move") ?? false;
            if (planUnderMove && !acceptMove)
                return CommandResult.Fail(
                    "placement_moved: this plan was re-derived under a placement that MOVED, and applying it " +
                    "re-shapes elements to follow the drawing. Send accept_placement_move=true here as well - " +
                    "the plan was read-only, this is the write. Nothing was written.");

            string plannedReading = provenanceTemplate.Value<string>("interpretation_version");
            if (!string.IsNullOrWhiteSpace(plannedReading) &&
                !string.Equals(plannedReading, CadInterpretationRules.InterpretationVersion, StringComparison.Ordinal))
                return CommandResult.Fail(
                    "stale_plan: the reading moved between the plan and this apply - the plan was made by a build " +
                    "that reads '" + plannedReading + "' and this build reads '" +
                    CadInterpretationRules.InterpretationVersion + "'. The same drawing may now mean other " +
                    "elements. Nothing was written; re-run horizun_plan_cad_update.");

            // ---- WHAT MUST STILL BE TRUE, BEFORE ANYTHING IS WRITTEN --------------
            //
            // An update deletes fittings, re-shapes runs somebody may have touched and rewrites the record
            // of where elements came from. Until now it checked none of that: it read 'actions' and
            // 'provenance' and wrote. The plan emitted an apply_binding and NOTHING consumed it.
            //
            // The same guard horizun_apply_cad_plan uses, called here - see Core/CadApplyGuard.cs. Two
            // copies of a rule this important is how one of them quietly stops being true.
            JObject binding = request["apply_binding"] as JObject;
            if (binding == null)
                return CommandResult.Fail(
                    "apply_binding is required - copy it verbatim from the horizun_plan_cad_update reply. It " +
                    "names the drawing, its references, the rules, the reading, the actions and the elements " +
                    "they act on, and this command re-measures every one of them before writing. NOTHING was " +
                    "written.");

            // WHICH CAD INSTANCE, from the binding: this command takes no instance_id, and the plan that
            // made the binding read exactly one placement.
            long instanceIdArg = binding.Value<long?>("instance_id") ?? -1;
            List<JObject> unreadableNow;
            CadInstanceFacts factsNow = CadFacts.Collect(doc, out unreadableNow)
                                                .FirstOrDefault(f => f.ElementId == instanceIdArg);
            Element instanceNow = null;
            try { if (Rid.CanRepresent(instanceIdArg)) instanceNow = doc.GetElement(Rid.Make(instanceIdArg)); }
            catch { }

            // A CONTINUATION IS NOT HELD AGAINST ITS OWN CONFIRMED WORK. The binding records what the
            // elements looked like when the plan was made; an action that has since landed moved one ON
            // PURPOSE. MEASURED: filtering only what gets re-read left the binding's list intact, so the
            // element came back as "it could not be re-read" - the operation refusing itself in a
            // different sentence. The LIST is what has to be filtered; every element this operation did
            // not touch is still measured exactly as before. See Core/CadUpdateOperations.cs.
            JObject guardBinding = binding;
            if (request.Value<string>("continue_operation") != null)
            {
                guardBinding = (JObject)binding.DeepClone();
                guardBinding["touched_elements"] = CadUpdateOperations.ExceptWhatItDidItself(
                    binding["touched_elements"] as JArray,
                    CadUpdateOperations.Read(request.Value<string>("continue_operation")));
            }

            var now = new CadApplyNow
            {
                ActionsFingerprint = CadConversionPlanRules.ActionsFingerprint(actions),
                SourceFingerprint = factsNow == null ? null : CadFacts.SourceFingerprint(factsNow),
                SourceSetSha256 = factsNow == null ? null
                    : CadDwgCache.SourceSetSha256(factsNow.ExternalPath, factsNow.FileSha256),
                RequirementSetSha256 = (request["requirement_set"] as JObject) == null ? null
                    : SetShaOf(request["requirement_set"] as JObject),
                InterpretationVersion = CadInterpretationRules.InterpretationVersion,
                TargetDocument = title,
                RevitVersion = RevitBuild(uiApp),
                LinkGeometryFingerprint = CadSourceCoherence.GeometryFingerprint(doc, instanceNow),
                Touched = CadElementPrint.ReadAgain(doc, guardBinding["touched_elements"] as JArray)
            };

            JArray drift = CadApplyGuard.Drift(guardBinding, now);
            if (drift.Count > 0)
            {
                // BEFORE CALLING IT STALE, ASK WHETHER THIS CALLER IS THE ONE WHO MOVED IT.
                //
                // MEASURED: a caller whose apply committed and whose ANSWER was lost sends the same call
                // again. The elements its own confirmed actions re-shaped no longer match the binding, so
                // the guard answered "the drawing changed since the plan - plan again". That is safe and
                // it is not true: nothing of anybody else's moved, and re-planning is not what this
                // caller needs. What they asked was "did my call land?", and the record knows.
                string keyNow = request.Value<string>("idempotency_key");
                JObject mine = CadUpdateOperations.Read(keyNow);
                string minePlan = (mine?["binding"] as JObject)?.Value<string>("actions_fingerprint");
                if (mine != null && string.Equals(minePlan, binding.Value<string>("actions_fingerprint"),
                                                  StringComparison.Ordinal))
                {
                    var left = CadUpdateOperations.PendingKeys(mine);
                    string how = left.Count == 0
                        ? "Every action of it is confirmed: the work LANDED and there is nothing to send again."
                        : "Part of it is still to do (" + string.Join(", ", left) + "). Send this same call " +
                          "with continue_operation='" + mine.Value<string>("operation_id") + "' and only the " +
                          "pending actions will run.";
                    return CommandResult.FailWithDetail(
                        "already_applied_under_this_key: idempotency_key '" + keyNow + "' already ran " +
                        "these exact actions on this machine, and what moved since is what THOSE actions did - " +
                        "not somebody else's change. NOTHING was written now. " + how,
                        new JObject
                        {
                            ["refused"] = "already_applied_under_this_key",
                            ["operation"] = CadUpdateOperations.Describe(mine, "recognised"),
                            ["drift_explained_by_this_operation"] = drift,
                            ["means"] = "this is the answer to a lost reply: the record says what the earlier " +
                                        "call did. It is NOT a stale plan - re-planning here would build a " +
                                        "second copy of work that is already in the model."
                        });
                }
                return CommandResult.FailWithDetail(CadApplyGuard.StalePlanMessage(drift),
                                                    new JObject { ["refused"] = "stale_plan", ["drift"] = drift });
            }

            JObject coherenceNow = CadSourceCoherence.Evaluate(doc, instanceNow, factsNow, false);
            string coherenceMessage;
            JObject notApplicable = CadApplyGuard.CoherenceRefusal(binding, coherenceNow, out coherenceMessage);
            if (notApplicable != null)
                return CommandResult.FailWithDetail(coherenceMessage, notApplicable);

            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            string idempotencyKey = request.Value<string>("idempotency_key");
            string actionsFingerprint = CadConversionPlanRules.ActionsFingerprint(actions) + ":" +
                                        CadIdentity.Sha256Hex(new JArray(restampRows).ToString(Formatting.None)).Substring(0, 16);
            string placementId = placementTemplate?.Value<string>("id");

            // A RETRY IS NOT A SECOND RUN. The same key over the same actions
            // replays what was recorded; the same key over different actions is
            // refused. Rehearsals are not recorded: a dry run writes nothing, so
            // there is nothing a replay could hide.
            // ---- WHICH OF THE THREE THINGS CALLED "RETRY" THIS IS --------------------
            //
            //   REPEAT    same key, same actions, already finished -> the ledger replays the reply
            //             below and NOTHING runs again.
            //   CONTINUE  continue_operation names an operation an earlier call left half done. The
            //             actions it confirmed are skipped; the pending ones run, under the decisions
            //             that call was given, against the world the guard above has just re-measured.
            //   A NEW PLAN  the world moved. The guard refuses this call and the caller plans again;
            //             the old decisions do not carry, because the question changed.
            //
            // The ledger lives in one Revit process. "Save, close, open a new session and continue" is
            // a case a caller actually has, so the operation record is durable on disk instead - see
            // Core/CadUpdateOperations.cs.
            string continueId = request.Value<string>("continue_operation");
            string operationId = continueId ?? idempotencyKey ??
                                 ("op:" + actionsFingerprint + ":" + (placementId ?? title));
            JObject operation = CadUpdateOperations.Read(operationId);
            string operationShape = continueId != null ? "continued" : "fresh";
            if (continueId != null)
            {
                if (operation == null)
                    return CommandResult.Fail(
                        "no_such_operation: nothing is recorded under '" + continueId + "' on this machine. A " +
                        "continuation carries out what an earlier call left pending, and without its record " +
                        "there is nothing to carry out - plan again from the current drawing and apply that. " +
                        "NOTHING was written.");
                string recorded = (operation["binding"] as JObject)?.Value<string>("actions_fingerprint");
                if (!string.IsNullOrWhiteSpace(recorded) &&
                    !string.Equals(recorded, binding.Value<string>("actions_fingerprint"), StringComparison.Ordinal))
                    return CommandResult.FailWithDetail(
                        "operation_is_for_other_actions: '" + continueId + "' was opened for a different plan. A " +
                        "continuation may only carry out the work it was opened for; applying other actions under " +
                        "its id would spend decisions somebody gave about one question on a different one. " +
                        "NOTHING was written.",
                        new JObject { ["refused"] = "operation_is_for_other_actions",
                                      ["operation_was_opened_for"] = recorded,
                                      ["this_call_carries"] = binding["actions_fingerprint"] });
            }
            var alreadyConfirmed = new HashSet<string>(
                (operation?["actions"] as JArray ?? new JArray()).OfType<JObject>()
                    .Where(r => r.Value<string>("state") == CadUpdateOperations.Confirmed)
                    .Select(r => r.Value<string>("key") ?? ""), StringComparer.Ordinal);
            if (continueId != null && alreadyConfirmed.Count ==
                (operation["actions"] as JArray ?? new JArray()).Count)
                return CommandResult.Ok(new JObject
                {
                    ["document"] = title,
                    ["wrote_nothing"] = true,
                    ["operation"] = CadUpdateOperations.Describe(operation, "already_finished"),
                    ["means"] = "every action of '" + continueId + "' is already confirmed. NOTHING ran. " +
                                "There is nothing left of this operation to continue."
                });

            // A CONTINUATION DOES NOT GO THROUGH THE LEDGER. The ledger answers "this key already ran"
            // with the reply that run produced - which is right for a repeat and wrong here, because the
            // caller is asking for the part that did NOT run.
            if (!dryRun && continueId == null)
            {
                CadUpdateLedgerDecision decision = CadUpdateLedger.Decide(idempotencyKey, actionsFingerprint);
                if (decision.Outcome == "refuse") return CommandResult.Fail(decision.Refusal);
                if (decision.Outcome == "replay")
                {
                    JObject replay = (JObject)(decision.Entry.Reply ?? new JObject()).DeepClone();
                    replay["replayed"] = true;
                    replay["replay_count"] = decision.Entry.ReplayCount;
                    replay["replay_means"] = "idempotency_key '" + idempotencyKey + "' was already applied in " +
                                             "this Revit session with these same actions, at " +
                                             decision.Entry.RecordedUtc.ToString("o") + ". NOTHING ran again; " +
                                             "this is the reply that run produced. The ledger is per session " +
                                             "and bounded, so a key from another session or a very old one runs afresh.";
                    return CommandResult.Ok(replay);
                }
            }
            JObject previousPartial = CadUpdateLedger.Describe(CadUpdateLedger.LastPartialFor(placementId));

            // ------------------------------------------------------- rehearse
            var rehearsal = new JArray();
            var tokens = new JObject();
            bool clean = true;
            foreach (JObject action in actions.OfType<JObject>())
            {
                string key = action.Value<string>("key") ?? "";
                // WHAT IS ALREADY IN THE MODEL IS NOT REHEARSED EITHER: a confirmed create rehearsed
                // again would ask to build a second one, and a confirmed re-shape would be rehearsed
                // against an element that has already moved.
                if (alreadyConfirmed.Contains(key))
                {
                    rehearsal.Add(new JObject { ["key"] = key, ["tool"] = action["tool"], ["ok"] = true,
                        ["skipped"] = true, ["means"] = "confirmed by an earlier call of this operation" });
                    continue;
                }
                string tool = action.Value<string>("tool");
                ICommand child = tool == null ? null : _resolve(tool);
                if (child == null)
                {
                    rehearsal.Add(new JObject { ["key"] = key, ["tool"] = tool, ["ok"] = false,
                                                ["error"] = "no such command" });
                    clean = false;
                    continue;
                }
                JObject args = (JObject)(action["arguments"] ?? new JObject()).DeepClone();
                args["target_document"] = title;
                args["dry_run"] = true;
                string standIn = CadSplitDependents.Resolve(args, cid => null, true);
                if (standIn != null)
                {
                    rehearsal.Add(new JObject { ["key"] = key, ["tool"] = tool, ["ok"] = false, ["error"] = standIn });
                    clean = false;
                    continue;
                }
                CommandResult r = child.Execute(uiApp, args.ToString(Formatting.None));
                rehearsal.Add(new JObject
                {
                    ["key"] = key, ["tool"] = tool, ["ok"] = r.Success,
                    ["error"] = r.Success ? null : r.Error
                });
                if (!r.Success) { clean = false; continue; }
                string t = (r.Data as JObject)?.Value<string>("confirmation_token");
                if (t != null) tokens[key] = t;
            }

            // AN EXPLICIT BRANCH ON dry_run THAT RETURNS. A repo guard reads for
            // exactly this shape, because a rehearsal that falls through into the
            // write is the failure nobody notices until it has happened.
            if (dryRun || !clean)
            {
                var rehearsed = new JObject
                {
                    ["document"] = title,
                    ["dry_run"] = true,
                    ["wrote_nothing"] = true,
                    ["rehearsed_cleanly"] = clean,
                    ["rehearsal"] = rehearsal,
                    ["tokens_by_key"] = tokens,
                    ["restamp_pending"] = restampRows.Count,
                    ["previous_partial"] = previousPartial,
                    ["operation"] = CadUpdateOperations.Describe(operation, operationShape + "_rehearsal"),
                    ["means"] = clean
                        ? "every action rehearsed cleanly and nothing was written. Send the same actions with " +
                          "dry_run=false and each action's token from tokens_by_key."
                        : "at least one action would not rehearse, so NOTHING was written and no token was " +
                          "issued for the ones that did. Fix the failure and rehearse again."
                };
                if (dryRun) return CommandResult.Ok(rehearsed);
                return CommandResult.FailWithDetail(
                    "rehearsal_failed: at least one action in this update would not rehearse, so nothing " +
                    "was written. A partly-applied incremental update is the worst of both worlds: the " +
                    "model matches neither revision.", rehearsed);
            }

            // ---------------------------------------------------------- apply
            // OPENED BEFORE THE FIRST WRITE, so a process that dies mid-update leaves a record naming
            // what was confirmed and what was not. Opening an id that exists returns it as it stands.
            operation = CadUpdateOperations.Begin(operationId, title, placementId, binding,
                                                  binding["decisions_authorised"] as JObject, actions);
            var applied = new JArray();
            var touched = new List<Touched>();
            var keptInPlace = new JArray();
            int failures = 0;
            bool wroteAlready = false;
            foreach (JObject action in actions.OfType<JObject>())
            {
                string key = action.Value<string>("key") ?? "";
                // ALREADY DONE IS NOT DONE AGAIN. On a continuation the actions that landed are in the
                // model; running them a second time is how a retry builds the revision twice.
                if (alreadyConfirmed.Contains(key))
                {
                    applied.Add(new JObject { ["key"] = key, ["tool"] = action["tool"], ["ok"] = true,
                        ["skipped"] = true,
                        ["means"] = "confirmed by an earlier call of this operation; nothing ran." });
                    continue;
                }
                string tool = action.Value<string>("tool");
                ICommand child = _resolve(tool);
                JObject args = (JObject)(action["arguments"] ?? new JObject()).DeepClone();
                args["target_document"] = title;
                args["dry_run"] = false;
                if (tokens[key] != null) args["confirmation_token"] = tokens[key];
                args["idempotency_key"] = (request.Value<string>("idempotency_key") ?? "cad-update") + "-" + key;

                // A HOST THIS APPLY CREATED, named by its candidate. The rehearsal ran against a stand-in,
                // so the resolved call is rehearsed again for a token that matches it.
                // A DEPENDENT'S ACTION IS REHEARSED AGAIN just before it runs: the actions before it
                // changed what it acts on (MEASURED: the old instance lost its host when its wall was
                // shortened, and the token issued at the start no longer matched).
                // (MEASURED again: a retype after a re-shape of the same wall.) So every action after the
                // first write is rehearsed again, in place - the whole list rehearsed cleanly first.
                bool hadPlaceholder = args.ToString(Formatting.None).Contains(CadSplitDependents.CreatedFor) ||
                                      wroteAlready;
                string unresolved = CadSplitDependents.Resolve(args, cid => CreatedFor(touched, index, cid), false);
                if (unresolved == null && hadPlaceholder)
                {
                    var again = (JObject)args.DeepClone();
                    again["dry_run"] = true;
                    again.Remove("confirmation_token");
                    again.Remove("idempotency_key");
                    CommandResult rehearsed = child.Execute(uiApp, again.ToString(Formatting.None));
                    string fresh = rehearsed.Success ? (rehearsed.Data as JObject)?.Value<string>("confirmation_token") : null;
                    if (fresh != null) args["confirmation_token"] = fresh;
                    else unresolved = "the resolved call did not rehearse: " + (rehearsed.Error ?? "no token");
                }
                CommandResult r;
                if (unresolved != null)
                    r = CommandResult.Fail("host_not_created: " + unresolved + ". Nothing was sent for this action.");
                // A FAULT SEAM FOR TESTS, off unless the Revit process was started with it: the named
                // action is reported failed WITHOUT running, to measure what a failure mid-update leaves.
                else if (FaultInjected(key))
                    r = CommandResult.Fail("fault_injected: HORIZUN_TEST_FAIL_ACTION names '" + key + "'. Nothing was sent.");
                else
                {
                    // WHAT A RE-SHAPED WALL HOSTS STAYS WHERE IT STANDS. MEASURED (campaign 4): set_curve
                    // on a wall whose start moved carried every face-hosted device along by the same
                    // distance, silently. Their points are read before and put back after.
                    Dictionary<long, Tuple<XYZ, long>> before = HostedPoints(doc, args);
                    r = child.Execute(uiApp, args.ToString(Formatting.None));
                    if (r.Success && before.Count > 0)
                    {
                        var except = new HashSet<long>((action["hosted_to_substitute"] as JArray ?? new JArray())
                                                          .Select(x => (long)x));
                        foreach (JObject kept in KeepInPlace(uiApp, doc, before, except, title,
                                                             (request.Value<string>("idempotency_key") ?? "cad-update") + "-" + key))
                        {
                            keptInPlace.Add(kept);
                            if ((bool?)kept["restored"] == false) { r = CommandResult.Fail(
                                "a hosted instance moved with its wall and could not be put back: " +
                                kept.ToString(Formatting.None)); break; }
                        }
                    }
                }
                wroteAlready = true;
                var row = new JObject
                {
                    ["key"] = key, ["tool"] = tool, ["ok"] = r.Success,
                    ["error"] = r.Success ? null : r.Error
                };
                if (r.Success)
                {
                    // THE ROW INDEX, not the position in the reply. A create that
                    // skipped a row would otherwise stamp every following element
                    // with somebody else's origin - the same reasoning as
                    // horizun_apply_cad_plan, and the same cost if it is wrong.
                    foreach (Touched t in CreatedElements(r.Data, key)) touched.Add(t);
                    foreach (long id in TargetIds(args))
                        touched.Add(new Touched { Key = key, ElementId = id, RowIndex = null });
                    row["elements"] = new JArray(touched.Where(x => x.Key == key)
                                                        .Select(x => (JToken)x.ElementId));
                }
                else failures++;
                applied.Add(row);
                // WRITTEN THE MOMENT THE ACTION LANDS, not at the end. A crash between here and the
                // reply is exactly the case this record exists for: what it says then is the truth.
                CadUpdateOperations.MarkAction(operationId, key,
                    r.Success ? CadUpdateOperations.Confirmed : CadUpdateOperations.Failed,
                    touched.Where(x => x.Key == key).Select(x => x.ElementId),
                    string.Equals(tool, "horizun_delete_verified", StringComparison.Ordinal)
                        ? TargetIds(args) : null,
                    r.Success ? null : r.Error);
                if (!r.Success) break;   // stop at the first failure; a half-updated model is nobody's revision
            }

            // READ BACK FROM THE MODEL, after the writes and before the reply is built.
            JArray openJunctions = CadOpenJunctions.Find(doc, touched.Select(x => x.ElementId).Distinct());

            // ----------------------------------------------------- provenance
            var stamps = new JArray();
            var restamps = new JArray();
            int written = 0, anonymous = 0, restamped = 0, migrated = 0, restampFailed = 0;
            using (var t = new Transaction(doc, "Horizun: record CAD update provenance"))
            {
                t.Start();
                foreach (Touched pair in touched)
                {
                    Element e = null;
                    try { if (Rid.CanRepresent(pair.ElementId)) e = doc.GetElement(Rid.Make(pair.ElementId)); } catch { }
                    if (e == null) continue;

                    JObject entry = index.OfType<JObject>().FirstOrDefault(x =>
                        string.Equals(x.Value<string>("key"), pair.Key, StringComparison.Ordinal) &&
                        (pair.RowIndex.HasValue
                            ? x.Value<int?>("element_index") == pair.RowIndex.Value
                            : x.Value<long?>("element_id") == pair.ElementId));
                    if (entry == null)
                    {
                        anonymous++;
                        stamps.Add(new JObject
                        {
                            ["element_id"] = pair.ElementId, ["key"] = pair.Key, ["written"] = false,
                            ["means"] = "candidate_index has no entry for this action, so nothing could be " +
                                        "stamped. The element exists and is ANONYMOUS: the next update will " +
                                        "build it again."
                        });
                        continue;
                    }
                    // A SUBSTITUTE FOR AN INSTANCE THAT HAD NO CAD IDENTITY gets none: stamping it
                    // would give the next update an element that claims a drawing it never came from.
                    if (entry.Value<bool?>("carried_identity") == false)
                    {
                        stamps.Add(new JObject
                        {
                            ["element_id"] = pair.ElementId, ["key"] = pair.Key, ["written"] = false,
                            ["means"] = "a re-created instance whose original carried no CAD provenance: none is written"
                        });
                        continue;
                    }

                    var p = new CadProvenance
                    {
                        SchemaVersion = CadProvenanceStore.CurrentVersion,
                        CandidateId = entry.Value<string>("candidate_id"),
                        GeometryId = entry.Value<string>("geometry_id"),
                        SemanticId = entry.Value<string>("semantic_id"),
                        RuleId = entry.Value<string>("rule_id"),
                        Layer = entry.Value<string>("layer"),
                        RequirementSetId = entry.Value<string>("requirement_set_id") ??
                                           provenanceTemplate.Value<string>("requirement_set_id"),
                        RequirementSetVersion = entry.Value<string>("requirement_set_version") ??
                                                provenanceTemplate.Value<string>("requirement_set_version"),
                        RequirementSetSha256 = entry.Value<string>("requirement_set_sha256") ??
                                               provenanceTemplate.Value<string>("requirement_set_sha256"),
                        SourceFingerprint = provenanceTemplate.Value<string>("source_fingerprint"),
                        SourceFileSha256 = provenanceTemplate.Value<string>("source_file_sha256"),
                        SourceSetSha256 = provenanceTemplate.Value<string>("source_set_sha256"),
                        PlanFingerprint = provenanceTemplate.Value<string>("plan_fingerprint"),
                        SourcePath = provenanceTemplate.Value<string>("source_path"),
                        Confidence = entry.Value<double?>("confidence") ?? 0,
                        WrittenUtc = DateTime.UtcNow.ToString("o"),
                        BuiltGeometry = CadUpdateRules.Encode(PlanGeometry(e)),
                        InterpretationVersion = CadInterpretationRules.InterpretationVersion,
                        SourceEntities = ApplyCadPlanCommand.Entities(entry["source_entities"] as JArray)
                    };
                    StampPlacement(p, placementTemplate);

                    string why;
                    bool ok = CadProvenanceStore.Write(e, p, out why);
                    if (ok) written++; else anonymous++;
                    stamps.Add(new JObject
                    {
                        ["element_id"] = pair.ElementId,
                        ["key"] = pair.Key,
                        ["candidate_id"] = p.CandidateId,
                        ["semantic_id"] = p.SemanticId,
                        ["geometry_id"] = p.GeometryId,
                        ["written"] = ok,
                        ["means"] = ok
                            ? "this element now remembers the entity in THIS revision that it stands for, so the " +
                              "next update recognises it instead of building it again"
                            : "the provenance entity did not land, so this element is ANONYMOUS and the next " +
                              "update will build it again. Revit said: " + (why ?? "(nothing)")
                    });
                }

                // RE-STAMPS: no geometry touched, the record rewritten. A v1
                // record becomes v2 with the placement it was claimed under; an
                // element left in place under an accepted move takes the
                // transform it now sits under. Everything else in the record is
                // kept as read - the entity, the rule, the set, the as-built
                // line - because none of that changed.
                foreach (JObject rowJson in restampRows)
                {
                    long id = rowJson.Value<long?>("element_id") ?? -1;
                    string reason = rowJson.Value<string>("reason") ?? "(unstated)";
                    // NEWER RULES ARE CLAIMED ONLY FOR A WHOLE UPDATE. With an action
                    // failed, the model is not what those rules ask for.
                    if (reason == CadPlacementRules.RestampRulesSuperseded && failures > 0)
                    {
                        restamps.Add(new JObject
                        {
                            ["element_id"] = id, ["reason"] = reason, ["written"] = false,
                            ["means"] = "not re-stamped: an action of this update failed, so the element keeps " +
                                        "naming the rules it was built under"
                        });
                        continue;
                    }
                    Element e = null;
                    try { if (Rid.CanRepresent(id)) e = doc.GetElement(Rid.Make(id)); } catch { }
                    string problem;
                    CadProvenance existing = e == null ? null : CadProvenanceStore.Read(e, out problem);
                    if (existing == null)
                    {
                        restampFailed++;
                        restamps.Add(new JObject
                        {
                            ["element_id"] = id, ["reason"] = reason, ["written"] = false,
                            ["means"] = e == null ? "no such element" : "the element carries no readable provenance to rewrite"
                        });
                        continue;
                    }
                    bool wasV1 = existing.IsV1;
                    string wasVersion = wasV1 ? "v1" : existing.SchemaVersion >= 4 ? "v4" : existing.SchemaVersion >= 3 ? "v3" : "v2";
                    CadProvenance p = existing.Clone();
                    p.SchemaVersion = CadProvenanceStore.CurrentVersion;
                    // KEPT BY A PERSON is a decision taken against THIS revision, so it
                    // cites this revision too. MEASURED: once held changes stopped being
                    // carried, a kept device still named revision A and the audit called
                    // it "built from another drawing".
                    if (reason == CadPlacementRules.RestampCarried || reason == CadPlacementRules.RestampAccepted)
                    {
                        // The entity it stands for, as revision B names it. The
                        // as-built geometry is kept: it is still where it was built,
                        // and every later comparison measures from there.
                        p.CandidateId = rowJson.Value<string>("candidate_id") ?? p.CandidateId;
                        p.SemanticId = rowJson.Value<string>("semantic_id") ?? p.SemanticId;
                        p.GeometryId = rowJson.Value<string>("geometry_id") ?? p.GeometryId;
                        p.SourceFileSha256 = provenanceTemplate.Value<string>("source_file_sha256") ?? p.SourceFileSha256;
                        p.SourceSetSha256 = provenanceTemplate.Value<string>("source_set_sha256");
                        p.SourceFingerprint = provenanceTemplate.Value<string>("source_fingerprint") ?? p.SourceFingerprint;
                        p.PlanFingerprint = provenanceTemplate.Value<string>("plan_fingerprint") ?? p.PlanFingerprint;
                        p.SourcePath = provenanceTemplate.Value<string>("source_path") ?? p.SourcePath;
                        // recognised by THIS reading as that entity
                        p.InterpretationVersion = CadInterpretationRules.InterpretationVersion;
                    }
                    // THE NEWER RULES ARE NAMED ONLY WHEN THE WHOLE UPDATE LANDED. MEASURED (campaign 4): a
                    // partial apply re-stamped the carried elements under the new rules, and the next plan
                    // could no longer claim what the old rules had built.
                    if (failures == 0 &&
                        (reason == CadPlacementRules.RestampCarried || reason == CadPlacementRules.RestampAccepted ||
                         reason == CadPlacementRules.RestampRulesSuperseded))
                    {
                        // the rules this element now stands under
                        p.RequirementSetId = provenanceTemplate.Value<string>("requirement_set_id") ?? p.RequirementSetId;
                        p.RequirementSetVersion = provenanceTemplate.Value<string>("requirement_set_version") ??
                                                  p.RequirementSetVersion;
                        p.RequirementSetSha256 = provenanceTemplate.Value<string>("requirement_set_sha256") ??
                                                 p.RequirementSetSha256;
                    }
                    if (reason == CadPlacementRules.RestampAccepted)
                    {
                        // KEPT BY A PERSON: where it stands is now where it was built.
                        p.BuiltGeometry = CadUpdateRules.Encode(PlanGeometry(e));
                    }
                    if (string.IsNullOrEmpty(p.GeometryId)) p.GeometryId = rowJson.Value<string>("geometry_id");
                    if (string.IsNullOrEmpty(p.SourcePath)) p.SourcePath = provenanceTemplate.Value<string>("source_path");
                    p.WrittenUtc = DateTime.UtcNow.ToString("o");
                    StampPlacement(p, placementTemplate);

                    string why;
                    bool ok = CadProvenanceStore.Write(e, p, out why);
                    if (ok)
                    {
                        restamped++;
                        if (wasV1 && reason == CadPlacementRules.RestampMigrated) migrated++;
                    }
                    else restampFailed++;
                    restamps.Add(new JObject
                    {
                        ["element_id"] = id, ["reason"] = reason, ["written"] = ok,
                        ["was_version"] = wasVersion,
                        ["means"] = ok
                            ? (reason == CadPlacementRules.RestampCarried
                                   ? "carried to this revision: the update matched this element to the same entity " +
                                     "in the new drawing, so it now cites that drawing; its as-built geometry is kept"
                                   : reason == CadPlacementRules.RestampRulesSuperseded
                                   ? "left as it stands under the newer rules the caller declared: it now names them"
                                   : reason == CadPlacementRules.RestampAccepted
                                   ? "kept as it stands by a decision: it now cites this drawing, and where it stands " +
                                     "is recorded as where it was built"
                                   : wasV1 ? "migrated from v1: this element now names the placement that built it"
                                           : "re-stamped with the placement's current transform")
                            : "the rewrite did not land and the element keeps the record it had. Revit said: " +
                              (why ?? "(nothing)")
                    });
                }
                // WHAT MOVED BECAUSE ITS HOST MOVED. A wall this update re-shaped
                // carries its hosted devices with it; their as-built record still
                // names the old position, so the next update would call each of
                // them "moved by a person". They were moved by THIS run, so their
                // record is brought to where they now stand - and said so.
                var reshapedHosts = new HashSet<long>(touched.Where(x => !x.RowIndex.HasValue).Select(x => x.ElementId));
                // A DEPENDENT THE UPDATE MEANT TO RE-CREATE keeps the record of where it was built: re-stamping
                // it where its wall carried it would hide the move from the next plan (MEASURED, campaign 4).
                var toSubstitute = new HashSet<long>(actions.OfType<JObject>()
                    .SelectMany(x => (x["hosted_to_substitute"] as JArray ?? new JArray()).Select(v => (long)v)));
                if (reshapedHosts.Count > 0)
                {
                    foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                                                                                  .Cast<FamilyInstance>())
                    {
                        long hostId;
                        try { hostId = fi.Host == null ? -1 : Rid.Value(fi.Host.Id); } catch { continue; }
                        if (!reshapedHosts.Contains(hostId)) continue;
                        long id = Rid.Value(fi.Id);
                        if (toSubstitute.Contains(id)) continue;
                        if (touched.Any(x => x.ElementId == id)) continue;
                        string problem;
                        CadProvenance existing = CadProvenanceStore.Read(fi, out problem);
                        if (existing == null) continue;
                        CadProvenance p = existing.Clone();
                        string was = p.BuiltGeometry;
                        // A DISPLACEMENT THIS UPDATE DID NOT MAKE IS NOT LAUNDERED. MEASURED (campaign 5, copy G): an
                        // apply stopped after re-shaping W1 left a device carried 338 mm; the resume's retype of W1
                        // re-stamped it where it stood, and no later plan could see it had moved. A device whose
                        // recorded point its host no longer carries keeps its record, for the re-home to act on.
                        List<CadPoint> builtAt = CadUpdateRules.AsBuiltOf(existing);
                        var hostWall = fi.Host as Wall;
                        if (hostWall != null && builtAt != null && builtAt.Count == 1 &&
                            !CadHostResolver.CarriesPoint(hostWall, new XYZ(builtAt[0].X / 304.8, builtAt[0].Y / 304.8,
                                                                            ((fi.Location as LocationPoint)?.Point ?? XYZ.Zero).Z)))
                        {
                            restamps.Add(new JObject
                            {
                                ["element_id"] = id, ["reason"] = "host_reshaped_left_displaced", ["written"] = false,
                                ["host_id"] = hostId, ["was_built_at_mm"] = was,
                                ["means"] = "its host no longer carries the point it was built at: it was carried off it " +
                                            "before this update. Its record is kept so the next plan re-homes it."
                            });
                            continue;
                        }
                        p.BuiltGeometry = CadUpdateRules.Encode(PlanGeometry(fi));
                        if (string.Equals(was, p.BuiltGeometry, StringComparison.Ordinal)) continue;
                        p.WrittenUtc = DateTime.UtcNow.ToString("o");
                        string why;
                        bool ok = CadProvenanceStore.Write(fi, p, out why);
                        if (ok) restamped++; else restampFailed++;
                        restamps.Add(new JObject
                        {
                            ["element_id"] = id, ["reason"] = "host_reshaped_by_this_update", ["written"] = ok,
                            ["host_id"] = hostId, ["was_built_at_mm"] = was, ["now_at_mm"] = p.BuiltGeometry,
                            ["means"] = ok
                                ? "this element moved because this update re-shaped the wall it is hosted in; its " +
                                  "as-built record now names where it stands, so the next update does not read the " +
                                  "move as somebody's edit"
                                : "the rewrite did not land. Revit said: " + (why ?? "(nothing)")
                        });
                    }
                }
                t.Commit();
            }

            JArray fittingsAfterResize = FittingsAfterResize(doc, actions);
            var result = new JObject
            {
                ["document"] = title,
                ["dry_run"] = false,
                ["actions_attempted"] = applied.Count,
                ["actions_failed"] = failures,
                // the same count under the name every CAD apply uses, so a reader need not know which apply it was
                ["stages_failed"] = failures,
                ["actions"] = applied,
                ["elements_touched"] = touched.Count,
                ["provenance_written"] = written,
                ["elements_left_anonymous"] = anonymous,
                ["provenance"] = stamps,
                ["provenance_rewritten"] = restamped,
                ["migrated_from_v1"] = migrated,
                ["restamp_failed"] = restampFailed,
                ["restamps"] = restamps,
                ["hosted_kept_in_place"] = keptInPlace,
                ["fittings_after_resize"] = fittingsAfterResize,
                ["substitutions"] = new JArray(touched.Where(x => x.RowIndex.HasValue).Select(x => new
                    {
                        t = x,
                        e = index.OfType<JObject>().FirstOrDefault(i =>
                            string.Equals(i.Value<string>("key"), x.Key, StringComparison.Ordinal) &&
                            i.Value<int?>("element_index") == x.RowIndex.Value && i["replaces_element_id"] != null)
                    })
                    .Where(z => z.e != null)
                    .Select(z => new JObject
                    {
                        ["old_element_id"] = z.e["replaces_element_id"],
                        ["new_element_id"] = z.t.ElementId,
                        ["carried_parameters"] = z.e["carried_parameters"],
                        ["carried_cad_identity"] = z.e["carried_identity"],
                        ["old_deleted"] = applied.OfType<JObject>().Any(a =>
                            (string)a["key"] == z.t.Key + "-delete" && (bool?)a["ok"] == true)
                    })),
                ["restamp_means"] = "provenance_rewritten counts records rewritten WITHOUT touching geometry: " +
                                    "migrated_from_v1 of them were v1 records that now name their placement; the " +
                                    "rest were re-stamped under the placement's current transform.",
                ["placement_move_accepted"] = planUnderMove,
                ["previous_partial"] = previousPartial,
                // WHAT THIS CALL BUILT AND DID NOT JOIN. See Core/CadOpenJunctions.cs: an update writes
                // geometry, joining is its own consented step, and a division built by an update leaves
                // two ends at the same point holding nothing. Every count of elements, sizes and
                // positions calls that correct.
                ["ends_that_meet_and_are_not_joined"] = openJunctions,
                ["ends_that_meet_and_are_not_joined_means"] = CadOpenJunctions.Means(openJunctions),
                ["operation"] = CadUpdateOperations.Describe(CadUpdateOperations.Read(operationId), operationShape),
                ["replayed"] = false,
                ["state"] = failures == 0 ? "applied" : "partial",
                ["atomicity"] = "PER ACTION, not whole. Each typed command is atomic and verified in itself; the " +
                                "actions commit separately, and the run STOPS at the first failure rather than " +
                                "carrying on into a model that matches neither revision.",
                ["partial_means"] = failures == 0
                    ? null
                    : "an action failed and the ones after it did not run. What landed IS in the model and is " +
                      "stamped. There are two ways on, and they are not the same: CONTINUE this operation - " +
                      "send the same actions and apply_binding with continue_operation='" + operationId + "', " +
                      "which carries out only what is still pending and only while nothing has moved - or, if " +
                      "the drawing or the model HAS moved since, plan again from the current drawing, because " +
                      "the decisions in this plan were answers to a question that has changed.",
                ["re_plan_note"] = "the elements stamped above now carry THIS revision, so planning the next " +
                                   "update against this drawing will read them as built rather than missing."
            };
            // RECORDED AFTER THE WRITE, so a retry replays what actually happened
            // - partial included. A partial run also leaves its note against the
            // placement, and the next plan applied there carries it.
            CadUpdateLedger.Record(idempotencyKey, actionsFingerprint, placementId,
                                   failures == 0 ? "applied" : "partial", result);
            CadUpdateOperations.Finish(operationId, failures == 0 ? "finished" : "partial");
            return CommandResult.Ok(result);
        }

        private const string RestampKey = "cad-update-restamp";

        /// <summary>The points of what the walls of a set_curve action host, before it runs.</summary>
        private static Dictionary<long, Tuple<XYZ, long>> HostedPoints(Document doc, JObject args)
        {
            var points = new Dictionary<long, Tuple<XYZ, long>>();
            foreach (JObject op in (args["operations"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (!string.Equals(op.Value<string>("operation"), "set_curve", StringComparison.Ordinal)) continue;
                foreach (JToken id in op["element_ids"] as JArray ?? new JArray())
                {
                    var wall = doc.GetElement(Rid.Make((long)id)) as Wall;
                    if (wall == null) continue;
                    foreach (FamilyInstance fi in CadSplitDependents.HostedOn(doc, wall))
                        if (fi.Location is LocationPoint lp) points[Rid.Value(fi.Id)] = Tuple.Create(lp.Point, Rid.Value(wall.Id));
                }
            }
            return points;
        }

        /// <summary>Move back every instance the action displaced, through the typed move; report each.</summary>
        private IEnumerable<JObject> KeepInPlace(UIApplication uiApp, Document doc, Dictionary<long, Tuple<XYZ, long>> before,
                                                 HashSet<long> except, string title, string keyStem)
        {
            ICommand move = _resolve("horizun_transform_elements");
            foreach (KeyValuePair<long, Tuple<XYZ, long>> kv in before)
            {
                if (except.Contains(kv.Key)) continue;
                var fi = doc.GetElement(Rid.Make(kv.Key)) as FamilyInstance;
                if (fi == null || !(fi.Location is LocationPoint lp)) continue;
                // ALONG THE WALL ONLY. Across, the instance follows its face - which is where the new
                // line puts the face - and a move off the face is refused by Revit (MEASURED).
                var wall = doc.GetElement(Rid.Make(kv.Value.Item2)) as Wall;
                XYZ o, u;
                double len;
                if (wall == null || !CadSplitDependents.LineOf(wall, out o, out u, out len)) continue;
                XYZ raw = kv.Value.Item1 - lp.Point;
                XYZ d = u.Multiply(raw.X * u.X + raw.Y * u.Y);
                if (d.GetLength() * 304.8 < 0.5) continue;
                var args = new JObject
                {
                    ["target_document"] = title, ["units"] = "mm",
                    ["operations"] = new JArray(new JObject
                    {
                        ["operation"] = "move", ["element_ids"] = new JArray(kv.Key),
                        ["vector"] = new JArray(Math.Round(d.X * 304.8, 4), Math.Round(d.Y * 304.8, 4), Math.Round(d.Z * 304.8, 4))
                    }),
                    ["dry_run"] = true
                };
                CommandResult dry = move.Execute(uiApp, args.ToString(Formatting.None));
                string token = dry.Success ? (dry.Data as JObject)?.Value<string>("confirmation_token") : null;
                CommandResult done = null;
                if (token != null)
                {
                    args["dry_run"] = false;
                    args["confirmation_token"] = token;
                    args["idempotency_key"] = keyStem + "-keep-" + kv.Key;
                    done = move.Execute(uiApp, args.ToString(Formatting.None));
                }
                XYZ now = (doc.GetElement(Rid.Make(kv.Key)) as FamilyInstance)?.Location is LocationPoint after ? after.Point : null;
                yield return new JObject
                {
                    ["element_id"] = kv.Key,
                    ["displaced_mm"] = Math.Round(d.GetLength() * 304.8, 1),
                    ["across_followed_the_face_mm"] = Math.Round((raw - d).GetLength() * 304.8, 1),
                    ["restored"] = done != null && done.Success && now != null &&
                                   Math.Abs(((kv.Value.Item1 - now).X * u.X + (kv.Value.Item1 - now).Y * u.Y)) * 304.8 < 0.5,
                    ["error"] = done == null ? (dry.Error ?? "no token") : (done.Success ? null : done.Error)
                };
            }
        }

        /// <summary>The requirement set's hash, or null when it cannot be loaded - the guard skips what it
        /// cannot measure rather than refusing a plan over a value it never had.</summary>
        /// <summary>
        /// EXACTLY WHAT THE PLANNER RECORDS, or the two disagree over nothing. Measured on the first live
        /// case: the plan wrote "2026.20250408_1515" and this wrote "2026", so every apply reported the
        /// Revit build as drift - a refusal that is correct in form and false in fact, which is the kind
        /// that teaches people to ignore refusals.
        /// </summary>
        private static string RevitBuild(UIApplication app)
        {
            try { return app?.Application?.VersionNumber + "." + app?.Application?.VersionBuild; }
            catch { return null; }
        }

        private static string SetShaOf(JObject setJson)
        {
            try { return CadRequirementSet.Load(setJson).Sha256; }
            catch { return null; }
        }

        private static bool FaultInjected(string key)
        {
            string named = Environment.GetEnvironmentVariable("HORIZUN_TEST_FAIL_ACTION");
            if (string.IsNullOrWhiteSpace(named)) return false;
            named = named.Trim();
            // A TRAILING * MATCHES A PREFIX. The seam is armed when Revit STARTS and an action's key
            // carries the stage number the plan happened to emit - which a harness cannot know before
            // it has planned. Naming "cad-update-create*" is how a test says "the create, whichever
            // stage it lands in" without guessing, and an exact name still means exactly that name.
            if (named.EndsWith("*", StringComparison.Ordinal))
                return key != null && key.StartsWith(named.Substring(0, named.Length - 1), StringComparison.Ordinal);
            return string.Equals(named, key, StringComparison.Ordinal);
        }

        /// <summary>The element this apply created for a candidate, through the candidate index.</summary>
        private static long? CreatedFor(List<Touched> touched, JArray index, string candidateId)
        {
            foreach (Touched t in touched.Where(x => x.RowIndex.HasValue))
            {
                JObject entry = index.OfType<JObject>().FirstOrDefault(x =>
                    string.Equals(x.Value<string>("key"), t.Key, StringComparison.Ordinal) &&
                    x.Value<int?>("element_index") == t.RowIndex.Value);
                if (entry != null && string.Equals(entry.Value<string>("candidate_id"), candidateId, StringComparison.Ordinal))
                    return t.ElementId;
            }
            return null;
        }

        /// <summary>The placement half of a stamp, copied from the plan's provenance block.</summary>
        private static void StampPlacement(CadProvenance p, JObject placement)
        {
            if (p == null || placement == null) return;
            p.PlacementId = placement.Value<string>("id");
            p.PlacementTransform = placement.Value<string>("transform");
            p.PlacementOrigin = placement.Value<string>("origin_mm");
            p.PlacementBasis = placement.Value<string>("basis");
            if (string.IsNullOrEmpty(p.SourcePath)) p.SourcePath = placement.Value<string>("external_path");
        }

        /// <summary>One element this run touched, and how to find the candidate that explains it.</summary>
        private sealed class Touched
        {
            public string Key;
            public long ElementId;
            /// <summary>The row index inside the create request; null for an element that already existed.</summary>
            public int? RowIndex;
        }

        /// <summary>
        /// What horizun_create_elements says it created: 'rows', each carrying the
        /// INDEX of the request row it came from. The first version read a
        /// 'created' array that does not exist, so nothing was ever stamped and
        /// the reply reported zero elements touched while the walls stood there.
        /// </summary>
        private static IEnumerable<Touched> CreatedElements(object data, string key)
        {
            var o = data as JObject;
            JArray rows = o?["rows"] as JArray;
            if (rows == null) yield break;
            foreach (JObject row in rows.OfType<JObject>())
            {
                long? id = row.Value<long?>("element_id");
                if (!id.HasValue) continue;
                yield return new Touched { Key = key, ElementId = id.Value, RowIndex = row.Value<int?>("index") };
            }
        }

        /// <summary>
        /// WHAT A RESIZE DID TO THE FITTINGS ON A DUCT'S ENDS, re-read from the model. Revit decides
        /// this itself - it may resize a fitting, keep it and insert a transition, or refuse - and a
        /// verified width and height on the duct says nothing about any of it. MEASURED: two legs of an
        /// elbow resized one after the other kept the elbow and gained a transition each.
        /// </summary>
        private static bool BehindInserted(Element fitting, IEnumerable<long> inserted)
        {
            var ids = new HashSet<long>(inserted);
            try
            {
                var cm = (fitting as FamilyInstance)?.MEPModel?.ConnectorManager;
                if (cm == null) return false;
                foreach (Connector c in cm.Connectors)
                    foreach (Connector o in c.AllRefs)
                        if (o.Owner != null && ids.Contains(Rid.Value(o.Owner.Id))) return true;
            }
            catch { }
            return false;
        }

        private static JArray FittingsAfterResize(Document doc, JArray actions)
        {
            var report = new JArray();
            if (doc == null || actions == null) return report;
            foreach (JObject action in actions.OfType<JObject>())
            {
                var before = action["fittings_before"] as JObject;
                if (before == null) continue;
                foreach (JProperty prop in before.Properties())
                {
                    long ductId;
                    if (!long.TryParse(prop.Name, out ductId)) continue;
                    var was = new HashSet<long>((prop.Value as JArray ?? new JArray()).Select(t => (long)t));
                    var now = new List<long>();
                    var duct = doc.GetElement(Rid.Make(ductId)) as MEPCurve;
                    try
                    {
                        if (duct?.ConnectorManager != null)
                            foreach (Connector c in duct.ConnectorManager.Connectors)
                                foreach (Connector o in c.AllRefs)
                                    if (o.Owner is FamilyInstance fi && fi.Id != duct.Id && !now.Contains(Rid.Value(fi.Id)))
                                        now.Add(Rid.Value(fi.Id));
                    }
                    catch { }
                    var rows = new JArray();
                    foreach (long id in was.Union(now))
                    {
                        Element f = doc.GetElement(Rid.Make(id));
                        var sizes = new JArray();
                        try
                        {
                            var cm = (f as FamilyInstance)?.MEPModel?.ConnectorManager;
                            if (cm != null)
                                foreach (Connector c in cm.Connectors)
                                    sizes.Add(c.Shape == ConnectorProfileType.Round
                                        ? (JToken)Math.Round(c.Radius * 2 * 304.8, 1)
                                        : new JArray(Math.Round(c.Width * 304.8, 1), Math.Round(c.Height * 304.8, 1)));
                        }
                        catch { }
                        rows.Add(new JObject
                        {
                            ["element_id"] = id,
                            ["state"] = f == null ? "gone" : !was.Contains(id) ? "inserted_by_revit" : now.Contains(id) ? "still_attached"
                                      : BehindInserted(f, now.Where(n => !was.Contains(n))) ? "kept_behind_inserted_fitting" : "detached",
                            ["name"] = f == null ? null : (f as FamilyInstance)?.Symbol?.FamilyName + ": " + f.Name,
                            ["connector_sizes_mm"] = sizes
                        });
                    }
                    report.Add(new JObject { ["duct"] = ductId, ["fittings"] = rows });
                }
            }
            return report;
        }

        /// <summary>The elements a transform aimed at: a set_curve re-shapes one that already exists.</summary>
        private static IEnumerable<long> TargetIds(JObject args)
        {
            // A RESOLVED SECTION is written as parameters: the duct it resized is re-stamped too.
            var written = new HashSet<long>();
            // ...and a refit's runs, whose section the refit wrote.
            foreach (JObject rf in (args["refit"] as JArray ?? new JArray()).OfType<JObject>())
                foreach (JToken t in rf["runs"] as JArray ?? new JArray())
                {
                    long value;
                    if (long.TryParse(t.ToString(), out value) && written.Add(value)) yield return value;
                }
            foreach (JObject w in (args["writes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                long value;
                if (long.TryParse((w["target_id"] ?? "").ToString(), out value) && written.Add(value)) yield return value;
            }
            JArray ops = args["operations"] as JArray;
            if (ops == null) yield break;
            foreach (JObject op in ops.OfType<JObject>())
            {
                string operation = op.Value<string>("operation");
                // Every operation an update emits changes what the element IS or where it
                // stands, so each one is re-stamped: a resolved retype or turn included.
                if (!string.Equals(operation, "set_curve", StringComparison.Ordinal) &&
                    !string.Equals(operation, "move", StringComparison.Ordinal) &&
                    !string.Equals(operation, "rotate", StringComparison.Ordinal) &&
                    !string.Equals(operation, "change_type", StringComparison.Ordinal)) continue;
                foreach (JToken id in op["element_ids"] as JArray ?? new JArray())
                {
                    long value;
                    if (long.TryParse(id.ToString(), out value)) yield return value;
                }
            }
        }

        private static List<CadPoint> PlanGeometry(Element e)
        {
            var points = new List<CadPoint>();
            try
            {
                var curve = e.Location as LocationCurve;
                if (curve?.Curve != null)
                {
                    points.Add(Mm(curve.Curve.GetEndPoint(0)));
                    points.Add(Mm(curve.Curve.GetEndPoint(1)));
                    return points;
                }
                var point = e.Location as LocationPoint;
                if (point?.Point != null) points.Add(Mm(point.Point));
            }
            catch { }
            return points;
        }

        private static CadPoint Mm(XYZ p) =>
            new CadPoint(CadUnits.FeetToMm(p.X), CadUnits.FeetToMm(p.Y), CadUnits.FeetToMm(p.Z));

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
    }
}
