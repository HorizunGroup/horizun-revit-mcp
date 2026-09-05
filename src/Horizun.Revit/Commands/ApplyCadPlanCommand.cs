// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_apply_cad_plan — build the plan, but only if it still means what it
// meant when it was made.
//
// THIS COMMAND DOES NOT CREATE ELEMENTS. It resolves and calls the real
// horizun_create_elements, exactly as horizun_execute_plan does, so every
// rehearsal, confirmation token, transaction and post-commit re-read is the one
// this bridge already has. Writing a second creation path for CAD would double
// the surface that has to be trusted and halve the evidence for it.
//
// What it ADDS, and could not be got any other way:
//
//   THE BINDING. A plan is made against a drawing at a moment: those bytes, that
//   transform, that requirement set. Between the plan and the apply, someone can
//   reload the link, receive a new issue of the DWG, nudge the import, or edit a
//   rule. All four produce a plan aimed at a different building, and all four
//   are silent. This re-measures the source, the transform and the set hash, and
//   refuses stale_plan naming WHICH one moved.
//
//   THE ORDER. Stages run in dependency order, and a stage that fails stops the
//   ones after it rather than building doors for walls that never landed.
//
//   THE PROVENANCE. Every element created is stamped, in Extensible Storage,
//   with the CAD entity it came from, the rule that decided it, the requirement
//   set's identity and the plan's fingerprint. Without that, the second run has
//   no way to know what the first one built, and "update the model from the new
//   drawing" is not a thing anyone can do.
//
//   THE HONEST PARTIAL. Stages commit separately - a 4000-element conversion in
//   one transaction is a conversion nobody can recover from - so this reports
//   `partial` with exactly which stages landed, and never claims atomicity it
//   does not have.
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
    public sealed class ApplyCadPlanCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        public ApplyCadPlanCommand(Func<string, ICommand> resolve) { _resolve = resolve; }

        public string Name => "horizun_apply_cad_plan";

        public string Description =>
            "Build a plan produced by horizun_plan_from_cad, through the same typed create_elements this bridge " +
            "already verifies, adding three things only this command can: it RE-MEASURES the drawing, its " +
            "transform and the requirement set hash and refuses stale_plan naming which moved; it runs stages in " +
            "dependency order and stops at the first failed stage; and it records provenance in Extensible " +
            "Storage on every element it creates, which is what makes an incremental update and an audit possible " +
            "at all. Stages commit separately, so a partial result says exactly which stages landed.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            // ---- what the plan claims it was made against -------------------------
            JObject binding = request["apply_binding"] as JObject;
            if (binding == null)
                return CommandResult.Fail(
                    "apply_binding is required - copy it verbatim from the horizun_plan_from_cad reply. It names " +
                    "the drawing, the transform and the requirement set this plan was made against, and without " +
                    "it there is nothing to check the model against before writing.");
            string expectedPlan = binding.Value<string>("plan_fingerprint");
            string expectedActions = binding.Value<string>("actions_fingerprint");
            string expectedSource = binding.Value<string>("source_fingerprint");
            string expectedSetSha = binding.Value<string>("requirement_set_sha256");
            string expectedTarget = binding.Value<string>("target_document");
            string expectedRevit = binding.Value<string>("revit_version");
            if (string.IsNullOrWhiteSpace(expectedPlan) || string.IsNullOrWhiteSpace(expectedSource) ||
                string.IsNullOrWhiteSpace(expectedSetSha) || string.IsNullOrWhiteSpace(expectedActions))
                return CommandResult.Fail(
                    "apply_binding needs plan_fingerprint, actions_fingerprint, source_fingerprint and " +
                    "requirement_set_sha256. A binding missing actions_fingerprint came from a build whose plans " +
                    "did not cover what they were about to build; re-run horizun_plan_from_cad.");

            long instanceId = request.Value<long?>("instance_id") ?? -1;
            if (instanceId < 0 || !Rid.CanRepresent(instanceId))
                return CommandResult.Fail("instance_id is required: the CAD instance the plan was read from.");

            JObject setJson = request["requirement_set"] as JObject;
            if (setJson == null)
                return CommandResult.Fail("requirement_set is required - the same artefact the plan was made from.");
            CadRequirementSet set;
            try { set = CadRequirementSet.Load(setJson); }
            catch (CadRequirementSetException ex) { return CommandResult.Fail("requirement_set refused: " + ex.Message); }

            // ---- RE-MEASURE. This is the whole reason this command exists. --------
            List<JObject> unreadable;
            CadInstanceFacts facts = CadFacts.Collect(doc, out unreadable)
                                             .FirstOrDefault(f => f.ElementId == instanceId);
            if (facts == null)
                return CommandResult.Fail("CAD instance " + instanceId + " is no longer readable in '" +
                                          SafeTitle(doc) + "'. Nothing was written.");

            var drift = new JArray();
            string nowSource = CadFacts.SourceFingerprint(facts);
            if (!string.Equals(nowSource, expectedSource, StringComparison.Ordinal))
                drift.Add(new JObject
                {
                    ["what"] = "the drawing",
                    ["planned_against"] = expectedSource,
                    ["now"] = nowSource,
                    ["means"] = "the file, its bytes, its path, its load state, its declared units or its " +
                                "transform changed since the plan was made. The plan describes a different drawing."
                });
            if (!string.Equals(set.Sha256, expectedSetSha, StringComparison.Ordinal))
                drift.Add(new JObject
                {
                    ["what"] = "the requirement set",
                    ["planned_against"] = expectedSetSha,
                    ["now"] = set.Sha256,
                    ["means"] = "the rules that decided what this drawing means are not the rules the plan used."
                });

            // THE RESOLVED IDS. A fingerprint over the actions cannot see this
            // one: the same number can point at a different thing. Between a plan
            // and its apply somebody can delete the level the plan resolved and
            // Revit will hand its id to the next element created - so the actions
            // are byte-identical, the binding agrees, and the walls land on
            // whatever inherited the number.
            foreach (JObject r in (binding["resolved_names"] as JArray ?? new JArray()).OfType<JObject>())
            {
                long id = r.Value<long?>("id") ?? -1;
                string was = r.Value<string>("name");
                string what = r.Value<string>("what") ?? "element";
                Element now = null;
                try { if (Rid.CanRepresent(id)) now = doc.GetElement(Rid.Make(id)); } catch { }
                string nowName = null;
                try { nowName = now?.Name; } catch { }
                if (now != null && string.Equals(nowName, was, StringComparison.Ordinal)) continue;
                // The LABEL when the plan recorded one, because "HZ_DOOR: HZ_DOOR"
                // tells a person which family, and "name" is only what this
                // comparison needs.
                string shown = r.Value<string>("label") ?? was;
                drift.Add(new JObject
                {
                    ["what"] = "the resolved " + what,
                    ["planned_against"] = shown + " (id " + id + ")",
                    ["now"] = now == null ? "id " + id + " no longer exists" : nowName + " (id " + id + ")",
                    ["means"] = now == null
                        ? "the " + what + " this plan resolved has been deleted. NOTHING was written."
                        : "id " + id + " now names something else. The actions are unchanged - that is exactly " +
                          "why this is checked separately. NOTHING was written."
                });
            }

            // THE ACTIONS. Everything above checks the WORLD; this checks the
            // REQUEST. Without it a caller could take a legitimate binding from a
            // real plan and send different coordinates, a different family type,
            // extra elements or fewer, and every other check would pass - because
            // nothing verified covered the thing about to be built.
            JToken actionsToken = request["actions"];
            JArray submitted = actionsToken as JArray;

            // SAY WHAT IS WRONG WITH THE REQUEST BEFORE MEASURING DRIFT AGAINST IT.
            //
            // A caller whose client flattened a one-element array into a single
            // object used to get "the actions moved between the plan and this
            // apply", fingerprinted against an EMPTY list - a drift report that
            // sends the reader looking at the drawing and the model for a change
            // neither of them made. The shape of the argument is not drift.
            if (actionsToken != null && actionsToken.Type != JTokenType.Null && submitted == null)
                return CommandResult.Fail(
                    "actions_not_a_list: 'actions' arrived as " + actionsToken.Type.ToString().ToLowerInvariant() +
                    ", and it must be the LIST this plan emitted - copy execute_plan_request.actions across " +
                    "unchanged. NOTHING was written. If your client serialises a one-element list as a single " +
                    "object, wrap it: [ { ... } ]. This is not stale_plan: the plan and the model may well still " +
                    "agree, and no comparison was made against them.");

            string nowActions = CadConversionPlanRules.ActionsFingerprint(submitted ?? new JArray());
            if (!string.Equals(nowActions, expectedActions, StringComparison.Ordinal))
                drift.Add(new JObject
                {
                    ["what"] = "the actions",
                    ["planned_against"] = expectedActions,
                    ["now"] = nowActions,
                    ["means"] = "the actions submitted are not the actions the plan emitted. A coordinate, a " +
                                "type, an element or the stage order differs. NOTHING was written."
                });

            if (!string.IsNullOrWhiteSpace(expectedTarget) &&
                !string.Equals(expectedTarget, SafeTitle(doc), StringComparison.Ordinal))
                drift.Add(new JObject
                {
                    ["what"] = "the target document",
                    ["planned_against"] = expectedTarget,
                    ["now"] = SafeTitle(doc),
                    ["means"] = "this plan was rehearsed against a different model. Applying it here would " +
                                "build one model's drawing into another."
                });

            string nowRevit = SafeVersion(app);
            if (!string.IsNullOrWhiteSpace(expectedRevit) && !string.IsNullOrWhiteSpace(nowRevit) &&
                !string.Equals(expectedRevit, nowRevit, StringComparison.Ordinal))
                drift.Add(new JObject
                {
                    ["what"] = "the Revit build",
                    ["planned_against"] = expectedRevit,
                    ["now"] = nowRevit,
                    ["means"] = "the plan was made against a different Revit; type and level resolution are not " +
                                "guaranteed to mean the same thing across builds."
                });

            if (drift.Count > 0)
                return CommandResult.Fail(
                    "stale_plan: " + string.Join(" and ", drift.Select(d => (string)d["what"])) +
                    " moved between the plan and this apply. NOTHING WAS WRITTEN. Re-run horizun_plan_from_cad " +
                    "and review the new plan; a plan aimed at a drawing that has since changed is a plan aimed " +
                    "at a different building. Drift: " + drift.ToString(Formatting.None));

            // ---- the actions, exactly as the plan produced them -------------------
            JArray actions = submitted;
            if (actions == null || actions.Count == 0)
                return CommandResult.Fail("actions is required: the execute_plan_request.actions the plan produced.");
            if (actions.Count > 200)
                return CommandResult.Fail("actions holds " + actions.Count + " entries; the bound is 200. " +
                                          "Split the conversion by stage or by layer rather than sending one " +
                                          "batch nobody can recover from.");

            ICommand create = _resolve != null ? _resolve("horizun_create_elements") : null;
            if (create == null)
                return CommandResult.Fail("horizun_create_elements is not available in this build; this command " +
                                          "creates nothing itself and has nothing to delegate to.");

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string idempotencyKey = request.Value<string>("idempotency_key");
            string target = request.Value<string>("target_document") ?? SafeTitle(doc);

            var stageResults = new JArray();
            var provenanceRows = new JArray();
            int created = 0, verified = 0, failedStages = 0;
            bool stopped = false;
            string stoppedBecause = null;

            // The candidate id per action, so provenance can be written per element.
            var candidateByStageBatch = ReadCandidateIndex(request["candidate_index"] as JArray);

            var ordered = actions.OfType<JObject>()
                                 .Select((a, i) => new { Action = a, Index = i })
                                 .OrderBy(x => StageOfAction(x.Action))
                                 .ThenBy(x => x.Index)
                                 .ToList();

            foreach (var entry in ordered)
            {
                if (stopped) break;
                JObject action = entry.Action;
                JObject args = action["arguments"] as JObject;
                if (args == null)
                {
                    stageResults.Add(new JObject { ["key"] = (string)action["key"], ["state"] = "malformed",
                                                   ["error"] = "the action carries no arguments object" });
                    failedStages++;
                    stopped = true;
                    stoppedBecause = "an action was malformed";
                    break;
                }

                var callArgs = (JObject)args.DeepClone();

                // THE PARAMETERS COME OFF THE CREATE ROWS FIRST.
                //
                // They travelled with the row so the two could not drift apart,
                // and they are NOT applied by create_elements: they go to
                // horizun_write_params_verified, which is the one writer in this
                // bridge that coerces a value to a storage type, parses units and
                // refuses what it cannot. A second writer would be a second set
                // of rules about what "3000" means.
                var parameterRows = new Dictionary<int, JArray>();
                if (callArgs["elements"] is JArray elementRows)
                {
                    for (int i = 0; i < elementRows.Count; i++)
                    {
                        var elementRow = elementRows[i] as JObject;
                        if (elementRow?["parameters"] is JArray declared && declared.Count > 0)
                            parameterRows[i] = (JArray)declared.DeepClone();
                        elementRow?.Remove("parameters");
                    }
                }

                callArgs["target_document"] = target;
                callArgs["dry_run"] = dryRun;
                if (!dryRun)
                {
                    string token = action.Value<string>("confirmation_token") ?? request.Value<string>("confirmation_token");
                    if (!string.IsNullOrWhiteSpace(token)) callArgs["confirmation_token"] = token;
                    if (!string.IsNullOrWhiteSpace(idempotencyKey))
                        callArgs["idempotency_key"] = idempotencyKey + "-" + (string)action["key"];
                }
                callArgs.Remove("stage");
                callArgs.Remove("batch_of_stage");

                CommandResult r = create.Execute(app, callArgs.ToString(Formatting.None));
                var row = new JObject
                {
                    ["key"] = (string)action["key"],
                    ["stage"] = StageOfAction(action),
                    ["ok"] = r.Success
                };
                if (!r.Success)
                {
                    row["state"] = "failed";
                    row["error"] = r.Error;
                    failedStages++;
                    stopped = true;
                    stoppedBecause = "stage " + StageOfAction(action) + " failed, and the stages after it depend on it";
                }
                else
                {
                    JObject data = r.Data as JObject;
                    int c = data?.Value<int?>("created_verified") ?? 0;
                    created += c;
                    verified += c;

                    // A REHEARSAL THAT COULD NOT PLAN A SINGLE ROW IS NOT A CLEAN
                    // REHEARSAL.
                    //
                    // create_elements answers a dry run with valid/invalid counts
                    // and an errors array, and it does NOT fail the call - the
                    // request was well formed and the answer is "none of these can
                    // be built". This graded the stage on r.Success alone, so a
                    // conversion whose every opening was refused for reaching past
                    // the end of its wall came back ok:true, state:rehearsed,
                    // stages_failed:0. A caller reading that would send the apply.
                    int invalid = data?.Value<int?>("invalid") ?? 0;
                    int valid = data?.Value<int?>("valid") ?? 0;
                    if (dryRun && invalid > 0)
                    {
                        // NOT ok, WHATEVER the mix. horizun_create_elements refuses
                        // a batch WHOLE when any row in it is invalid - it does not
                        // build the good ones and leave the rest - so a rehearsal
                        // saying "applying builds the rest" would send a caller to
                        // an apply that commits the earlier stages, refuses this one
                        // entirely, and abandons every stage after it.
                        row["ok"] = false;
                        row["state"] = valid > 0 ? "rehearsed_partial" : "rehearsed_nothing";
                        row["invalid"] = invalid;
                        row["valid"] = valid;
                        row["invalid_rows"] = data?["errors"];
                        row["means"] = valid > 0
                            ? invalid + " of " + (valid + invalid) + " row(s) in this stage cannot be built and " +
                              "are named above. create_elements refuses a batch WHOLE when any row is invalid, " +
                              "so applying this plan builds NONE of this stage - not the other " + valid + ". " +
                              "Fix the rows named above, or take them out of the drawing's layer, and rehearse " +
                              "again."
                            : "NOTHING in this stage can be built - all " + invalid + " row(s) were refused, and " +
                              "each says why above. This is not a rehearsal that passed.";
                        failedStages++;
                    }
                    else
                    {
                        row["state"] = dryRun ? "rehearsed" : "applied";
                    }
                    row["created_verified"] = c;
                    if (data != null && data["confirmation_token"] != null)
                        row["confirmation_token"] = data["confirmation_token"];

                    // WHAT WAS VERIFIED, not only HOW MANY.
                    //
                    // create_elements re-reads every element after the commit and
                    // says what it found: the id, whether it is there, and - for a
                    // hosted element - whether Revit really put it in the host that
                    // was asked for. Summarising all of that to a count throws away
                    // the distinction that matters most for a door: created and
                    // hosted are not the same claim, and an unhosted door is a
                    // door-shaped object standing beside its own opening.
                    //
                    // MEASURED: a door and a window both came back created=1 with
                    // no way for the caller to see host_verified at all. So the
                    // rows travel, trimmed to the verification - never the whole
                    // create reply, which would bury the answer again.
                    if (data?["rows"] is JArray verifiedRows)
                    {
                        var carried = new JArray();
                        foreach (JObject vr in verifiedRows.OfType<JObject>())
                        {
                            var slim = new JObject();
                            foreach (string field in VerificationFields)
                                if (vr[field] != null) slim[field] = vr[field];
                            if (slim.Count > 0) carried.Add(slim);
                        }
                        if (carried.Count > 0) row["rows"] = carried;
                    }

                    if (!dryRun && data?["rows"] is JArray rows)
                        WriteProvenance(doc, rows, action, candidateByStageBatch, set, facts,
                                        expectedPlan, provenanceRows);

                    // AND THEN THE PARAMETERS, in their own transaction.
                    //
                    // NOT atomic with the creation, and this does not pretend
                    // otherwise: Revit commits the create before the ids exist to
                    // write against. So a failure here leaves elements that were
                    // built and not annotated, and the reply says exactly that
                    // rather than reporting the stage as clean. The created ids
                    // are kept so a retry writes parameters instead of building a
                    // second copy.
                    if (parameterRows.Count > 0)
                    {
                        JObject parameterOutcome = ApplyParameters(
                            app, target, data?["rows"] as JArray, parameterRows, dryRun,
                            idempotencyKey, (string)action["key"]);
                        row["parameters"] = parameterOutcome;
                        // STOPPING IS ABOUT THE REQUIRED ONES. all_written stays the
                        // honest report of whether everything landed; whether that
                        // is a reason to abandon the rest of the conversion is a
                        // different question, and `required: false` is how a set
                        // answers it.
                        if (!dryRun && parameterOutcome.Value<bool?>("all_written") == false &&
                            (parameterOutcome.Value<int?>("required_missing") ?? 1) > 0)
                        {
                            row["state"] = "applied_without_parameters";
                            failedStages++;
                            stopped = true;
                            stoppedBecause = "the elements of stage " + StageOfAction(action) +
                                             " were created and their parameters were not";
                        }
                    }
                }
                stageResults.Add(row);
            }

            var result = new JObject
            {
                ["document"] = SafeTitle(doc),
                ["instance_id"] = instanceId,
                ["dry_run"] = dryRun,
                ["binding_verified"] = new JObject
                {
                    ["source_fingerprint"] = nowSource,
                    ["requirement_set_sha256"] = set.Sha256,
                    ["plan_fingerprint"] = expectedPlan,
                    ["means"] = "the drawing, its transform and the rules are the ones this plan was made against"
                },
                ["stages"] = stageResults,
                ["stages_attempted"] = stageResults.Count,
                ["stages_failed"] = failedStages,
                ["created_verified"] = created,
                ["provenance_written"] = provenanceRows.Count(r => (bool?)r["written"] == true),
                ["elements_left_anonymous"] = provenanceRows.Count(r => (bool?)r["written"] != true),
                ["provenance"] = provenanceRows,
                ["stopped_early"] = stopped,
                ["stopped_because"] = stoppedBecause,
                ["atomicity"] = "PER STAGE, not whole. Each create_elements call is atomic and verified in " +
                                "itself; the stages commit separately, because one transaction over a whole " +
                                "conversion is one nobody can recover from. A partial result names exactly " +
                                "which stages landed.",
                ["state"] = failedStages == 0
                    ? (dryRun ? "rehearsed" : (created > 0 ? "applied" : "applied_nothing"))
                    : "partial"
            };
            // THE REHEARSAL RETURNS HERE, and it returns something the caller can
            // act on: every confirmation token the delegated rehearsals issued,
            // in the order the stages must run. Falling through from a rehearsal
            // into the write is the failure this branch exists to make
            // structurally impossible rather than merely unlikely.
            if (dryRun)
            {
                result["rehearsal"] = new JObject
                {
                    ["means"] = "NOTHING WAS WRITTEN. Each stage was rehearsed by horizun_create_elements, which " +
                                "resolved its types, levels and geometry and issued a token bound to that exact " +
                                "plan.",
                    ["tokens_by_key"] = new JObject(stageResults.OfType<JObject>()
                        .Where(r => r["confirmation_token"] != null)
                        .Select(r => new JProperty((string)r["key"], (string)r["confirmation_token"]))),
                    ["next"] = "send the SAME actions and apply_binding again with dry_run=false, an " +
                               "idempotency_key, and each action carrying its confirmation_token from " +
                               "tokens_by_key. The binding is re-measured on that call too: a drawing that moved " +
                               "in between still refuses."
                };
                return CommandResult.Ok(result);
            }

            if (failedStages > 0)
                result["partial_means"] = "some stages committed and some did not. What landed IS in the model " +
                                          "and carries its provenance. RE-RUNNING IS NOT YET SAFE: nothing in " +
                                          "this build reads existing provenance back when planning, so a second " +
                                          "apply would build the landed stages AGAIN, exactly coincident. Delete " +
                                          "what landed, or apply only the stages that did not - their keys are " +
                                          "listed above.";
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// Hand the rule's parameters to the one command that writes them.
        ///
        /// The ids come from what the create ACTUALLY made, matched by the row
        /// index create_elements reports - never by position in the reply, which
        /// a skipped row would shift.
        ///
        /// A `type` scope write goes to the element's TYPE, and that changes every
        /// instance of that type in the model, including ones this conversion did
        /// not create. That is what the caller asked for; it is recorded so
        /// nobody has to deduce it from the count afterwards.
        /// </summary>
        private static JObject ApplyParameters(UIApplication app, string target, JArray createdRows,
                                               Dictionary<int, JArray> parameterRows, bool dryRun,
                                               string idempotencyKey, string actionKey)
        {
            var writes = new JArray();
            var skipped = new JArray();
            // WHAT THE RULES DECLARED, which is the number all_written has to be
            // measured against. It used to be measured against the writes that
            // survived the skip loop, so every declaration that fell out on the way
            // quietly shrank the denominator and a partial loss produced exactly
            // the same verdict as a clean stage.
            int declared = 0;
            foreach (var pending in parameterRows) declared += pending.Value.OfType<JObject>().Count();
            // AND WHICH OF THEM WERE REQUIRED. `required: false` is parsed, carried
            // on the plan row, and acted on by the audit; the apply read it
            // nowhere, so a nice-to-have that could not be written stopped the whole
            // conversion - the opposite of what the key means and of what this
            // bridge's own documentation promises.
            var requiredNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pending in parameterRows)
                foreach (JObject w in pending.Value.OfType<JObject>())
                    if ((w.Value<bool?>("required") ?? true) && !string.IsNullOrWhiteSpace(w.Value<string>("parameter")))
                        requiredNames.Add(w.Value<string>("parameter"));
            var byIndex = new Dictionary<int, JObject>();
            foreach (JObject made in (createdRows ?? new JArray()).OfType<JObject>())
            {
                int? index = made.Value<int?>("index");
                if (index.HasValue) byIndex[index.Value] = made;
            }

            Document doc = app.ActiveUIDocument?.Document;
            foreach (var kv in parameterRows.OrderBy(x => x.Key))
            {
                JObject created;
                if (!byIndex.TryGetValue(kv.Key, out created))
                {
                    skipped.Add(new JObject
                    {
                        ["element_index"] = kv.Key,
                        ["why"] = "the create reported no row at this index, so there is nothing to write on"
                    });
                    continue;
                }
                long elementId = created.Value<long?>("element_id") ?? -1;
                if (elementId <= 0)
                {
                    skipped.Add(new JObject
                    {
                        ["element_index"] = kv.Key,
                        ["why"] = "the created row carries no element id"
                    });
                    continue;
                }

                foreach (JObject write in kv.Value.OfType<JObject>())
                {
                    long targetId = elementId;
                    if (write.Value<string>("scope") == "type")
                    {
                        long typeId = -1;
                        try
                        {
                            Element e = doc?.GetElement(Rid.Make(elementId));
                            ElementId t = e?.GetTypeId();
                            if (t != null) typeId = Rid.Value(t);
                        }
                        catch { }
                        if (typeId <= 0)
                        {
                            skipped.Add(new JObject
                            {
                                ["element_index"] = kv.Key,
                                ["parameter"] = write.Value<string>("parameter"),
                                ["why"] = "scope is 'type' and this element has no readable type"
                            });
                            continue;
                        }
                        targetId = typeId;
                    }
                    writes.Add(new JObject
                    {
                        ["target_id"] = targetId,
                        ["parameter"] = write.Value<string>("parameter"),
                        ["value"] = write["value"]
                    });
                }
            }

            var outcome = new JObject
            {
                ["declared"] = declared,
                ["requested"] = writes.Count,
                ["required"] = requiredNames.Count,
                ["skipped"] = skipped,
                ["written_by"] = "horizun_write_params_verified",
                ["atomic_with_creation"] = false,
                ["atomicity_means"] =
                    "Revit commits the create before the ids exist to write against, so parameters are a " +
                    "SECOND transaction. A failure here leaves elements built and not annotated, and the " +
                    "stage says applied_without_parameters rather than reporting itself clean. The ids are " +
                    "kept, so the fix is to write the parameters - never to build the elements again."
            };
            if (writes.Count == 0)
            {
                // A REHEARSAL HAS NO IDS TO WRITE AGAINST, and that is not a
                // success. create_elements rehearses its creates and returns no
                // rows - there are no elements yet - so every declared parameter
                // lands in `skipped` and writes.Count is zero. This used to answer
                // all_written: true, which is a rehearsal reporting success over
                // something it never measured, and it made the writer's own
                // rehearsal below unreachable in the only mode that would have
                // used it.
                if (dryRun)
                {
                    var wouldWrite = new JArray();
                    foreach (var pending in parameterRows.OrderBy(x => x.Key))
                        foreach (JObject write in pending.Value.OfType<JObject>())
                            wouldWrite.Add(new JObject
                            {
                                ["element_index"] = pending.Key,
                                ["parameter"] = write.Value<string>("parameter"),
                                ["value"] = write["value"],
                                ["scope"] = write.Value<string>("scope") ?? "instance"
                            });
                    foreach (JProperty prop in CadParameterOutcome.NotRehearsed(wouldWrite, parameterRows.Count).Properties())
                        outcome[prop.Name] = prop.Value;
                    return outcome;
                }
                // AN APPLY REACHES HERE ONLY WHEN EVERY DECLARED PARAMETER WAS
                // DROPPED. ApplyParameters is called only for rows that declared
                // one, so writes.Count == 0 on a real apply means each of them
                // landed in `skipped` - no created row at that index, no element
                // id, or a type that could not be read. Answering all_written:true
                // told somebody the conversion was clean over elements carrying
                // none of the values they asked for.
                outcome["all_written"] = false;
                outcome["landed"] = 0;
                outcome["required_missing"] = requiredNames.Count;
                outcome["why"] =
                    "every declared parameter was dropped before the writer was called - see `skipped` for " +
                    "the reason on each. The elements exist and carry none of the declared values. The ids " +
                    "are kept, so the fix is to write the parameters, never to build the elements again.";
                return outcome;
            }

            var writer = new WriteParamsCommand();
            var callArgs = new JObject
            {
                ["target_document"] = target,
                ["writes"] = writes,
                ["dry_run"] = true
            };

            // THE WRITER'S OWN REHEARSAL, RUN HERE, because it will not write
            // without one.
            //
            // This called it straight through with dry_run=false and got back
            // "No such confirmation token" - every time, for every set that
            // declared a parameter. The capability existed on paper from the day
            // it was written: the plan carried the values, the apply handed them
            // over, and the writer refused the handover.
            //
            // Rehearsing and spending in the same call is not a way around that
            // gate. The gate exists so the plan you confirm is the plan you
            // rehearsed, and these two calls carry the SAME writes object; the
            // person's confirmation was taken one level up, by the apply, over
            // the conversion this belongs to.
            CommandResult rehearsed = writer.Execute(app, callArgs.ToString(Formatting.None));
            if (dryRun)
            {
                outcome["ok"] = rehearsed.Success;
                if (!rehearsed.Success) { outcome["error"] = rehearsed.Error; outcome["all_written"] = false; }
                else outcome["rehearsed"] = true;
                return outcome;
            }
            if (!rehearsed.Success)
            {
                outcome["ok"] = false;
                outcome["error"] = rehearsed.Error;
                outcome["all_written"] = false;
                return outcome;
            }

            string token = null;
            try { token = (rehearsed.Data as JObject)?.Value<string>("confirmation_token"); } catch { }
            if (string.IsNullOrWhiteSpace(token))
            {
                outcome["ok"] = false;
                outcome["all_written"] = false;
                outcome["error"] = "the parameter writer rehearsed these values and issued no confirmation token, " +
                                   "so they cannot be written. The elements exist and carry none of them.";
                return outcome;
            }

            callArgs["dry_run"] = false;
            callArgs["confirmation_token"] = token;
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                callArgs["idempotency_key"] = idempotencyKey + "-params-" + actionKey;

            CommandResult written = writer.Execute(app, callArgs.ToString(Formatting.None));
            outcome["ok"] = written.Success;
            if (!written.Success)
            {
                outcome["error"] = written.Error;
                outcome["all_written"] = false;
                return outcome;
            }

            var data = written.Data as JObject;

            // READ THE KEYS THE WRITER ACTUALLY EMITS.
            //
            // This read data["verified"] and data["written"], and the writer emits
            // neither: its answer is writes_confirmed_against_your_value beside a
            // verification block. Both lookups came back null, null became zero,
            // and every successful parameter write was reported as all_written
            // false - which drove the whole stage to applied_without_parameters
            // on a model that was, in fact, correctly annotated. A false negative
            // in a verification report costs the same trust as a false positive:
            // the next person cannot tell which of the two they are looking at.
            // THE ARITHMETIC IS IN CORE, because it was got wrong twice in
            // opposite directions and neither mistake was reproducible without a
            // Revit: first by reading keys the writer does not emit, then by
            // demanding evidence a legitimate write cannot produce.
            bool readBackAgreed = false;
            try { readBackAgreed = (data?["verification"] as JObject)?.Value<bool?>("verified") == true; }
            catch { }

            // AGAINST WHAT WAS DECLARED, not against what was sent.
            JObject folded = CadParameterOutcome.Summarise(data, declared);
            foreach (JProperty prop in folded.Properties()) outcome[prop.Name] = prop.Value;
            outcome["verification_agreed_against_your_values"] = readBackAgreed;

            // WHICH OF THE MISSING ONES WERE REQUIRED. A fire rating that did not
            // land stops the conversion; a comment that did not is reported and
            // does not. That distinction is the whole point of the key, and the
            // apply used to treat every unwritten parameter the same.
            var landedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject r2 in (data?["rows"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string name = r2.Value<string>("parameter");
                // The writer's own three-way split: confirmed | not_written | unknown.
                // Only the first is a value that landed; unknown is deliberately
                // NOT counted as one, because "could not re-read what I set" is the
                // answer this bridge exists not to round up.
                bool ok2 = string.Equals(r2.Value<string>("outcome"), "confirmed", StringComparison.Ordinal);
                if (ok2 && !string.IsNullOrWhiteSpace(name)) landedNames.Add(name);
            }
            var requiredMissing = requiredNames.Where(n => !landedNames.Contains(n)).ToList();
            outcome["required_missing"] = requiredMissing.Count;
            outcome["required_missing_names"] = new JArray(requiredMissing.Cast<object>().ToArray());
            outcome["required_means"] =
                "a parameter declared required: false may fail to land without stopping the conversion - that " +
                "is what the key is for. required_missing counts only the ones that may not.";
            if (data != null) outcome["result"] = data;
            return outcome;
        }

        /// <summary>
        /// What create_elements re-read after the commit, and nothing else. A
        /// count says a row came back; these say WHAT came back - which for a
        /// hosted element is the whole question.
        /// </summary>
        private static readonly string[] VerificationFields =
        {
            "index", "kind", "element_id", "element_ids", "elements_created",
            "elements_created_means", "present_after_commit", "verified",
            "kind_verified", "type_verified", "host_verified", "curve_verified",
            "structural_verified", "identity_verified", "structural_type_verified",
            "diameter_verified", "mep_system",
            "connectors_verified", "inline_connections", "actual_class", "actual_category"
        };

        // A LIST THAT HAS TO BE ADDED TO IS A LIST THAT WILL BE FORGOTTEN.
        //
        // structural_verified was added to create_elements and not to this list,
        // and the apply reported a load-bearing wall as merely created - the
        // exact distinction the field exists to draw. So the rule is stated: a
        // field create_elements emits as evidence of what it RE-READ belongs
        // here, and the test in CadApplyVerificationTests checks that every
        // *_verified key the create command can emit is carried.

        private static int StageOfAction(JObject action)
        {
            JObject args = action["arguments"] as JObject;
            return args?.Value<int?>("stage") ?? action.Value<int?>("stage") ?? 0;
        }

        /// <summary>
        /// candidate_index maps an action key plus a row index to the CAD entity
        /// it came from. The plan produces it; without it, elements can still be
        /// created but they cannot remember where they came from, and the reply
        /// says so rather than pretending provenance was written.
        /// </summary>
        private static Dictionary<string, List<JObject>> ReadCandidateIndex(JArray index)
        {
            var map = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
            if (index == null) return map;
            foreach (JObject entry in index.OfType<JObject>())
            {
                string key = entry.Value<string>("key");
                if (string.IsNullOrEmpty(key)) continue;
                List<JObject> bucket;
                if (!map.TryGetValue(key, out bucket)) map[key] = bucket = new List<JObject>();
                foreach (JObject c in (entry["candidates"] as JArray ?? new JArray()).OfType<JObject>())
                    bucket.Add(c);
            }
            return map;
        }

        private static void WriteProvenance(Document doc, JArray rows, JObject action,
                                            Dictionary<string, List<JObject>> candidateIndex,
                                            CadRequirementSet set, CadInstanceFacts facts,
                                            string planFingerprint, JArray provenanceRows)
        {
            string key = action.Value<string>("key") ?? "";
            List<JObject> candidates;
            if (!candidateIndex.TryGetValue(key, out candidates)) candidates = new List<JObject>();

            using (var t = new Transaction(doc, "Horizun: record CAD provenance"))
            {
                t.Start();
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i] as JObject;
                    long id = row?.Value<long?>("element_id") ?? -1;
                    if (id < 0 || !Rid.CanRepresent(id)) continue;
                    Element created = doc.GetElement(Rid.Make(id));
                    if (created == null) continue;

                    // AND EVERY SIBLING THE SAME ROW MADE. A chain of curves is one
                    // separator and several elements; stamping only the first leaves
                    // the rest with no origin at all, which the audit reports as
                    // bim_without_source and no incremental update will ever act on.
                    var alsoRaw = row["element_ids"] as JArray;
                    var siblings = new List<Element>();
                    foreach (JToken t2 in alsoRaw ?? new JArray())
                    {
                        long extra = t2?.Value<long>() ?? -1;
                        if (extra < 0 || extra == id || !Rid.CanRepresent(extra)) continue;
                        Element also = doc.GetElement(Rid.Make(extra));
                        if (also != null) siblings.Add(also);
                    }

                    // KEY OFF THE ROW'S OWN INDEX, never off its position in the
                    // reply. create_elements reports the index of the element in
                    // the REQUEST, and a reply that omits or reorders a row would
                    // otherwise stamp every element after it with somebody else's
                    // origin - which is worse than stamping none, because an audit
                    // would then confidently trace a wall to the wrong drawing.
                    int? elementIndex = row.Value<int?>("index");
                    JObject c = null;
                    if (elementIndex.HasValue)
                        c = candidates.FirstOrDefault(x => x.Value<int?>("element_index") == elementIndex.Value);
                    if (c == null && elementIndex.HasValue && elementIndex.Value < candidates.Count &&
                        candidates.All(x => x["element_index"] == null))
                        c = candidates[elementIndex.Value];   // a plan from an older build, positionally indexed
                    var p = new CadProvenance
                    {
                        SchemaVersion = CadProvenanceStore.CurrentVersion,
                        CandidateId = c?.Value<string>("candidate_id"),
                        GeometryId = c?.Value<string>("geometry_id"),
                        SemanticId = c?.Value<string>("semantic_id"),
                        RuleId = c?.Value<string>("rule_id"),
                        Layer = c?.Value<string>("layer"),
                        Confidence = c?.Value<double?>("confidence") ?? 0,
                        RequirementSetId = set.Id,
                        RequirementSetVersion = set.Version,
                        RequirementSetSha256 = set.Sha256,
                        SourceFingerprint = CadFacts.SourceFingerprint(facts),
                        SourceFileSha256 = facts.FileSha256,
                        PlanFingerprint = planFingerprint,
                        WrittenUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        // PROVENANCE v2: the placement kept APART from the file, so
                        // an update can tell this placement from another of the
                        // same drawing and can measure whether it has moved since.
                        PlacementId = facts.UniqueId,
                        PlacementTransform = facts.TransformFingerprint,
                        PlacementOrigin = CadPlacementRules.EncodeOrigin(facts.TransformOrigin),
                        PlacementBasis = CadPlacementRules.EncodeBasis(facts.TransformBasisX, facts.TransformBasisY,
                                                                       facts.TransformScale ?? 1.0),
                        SourcePath = string.IsNullOrWhiteSpace(facts.ExternalPath) ? null : facts.ExternalPath
                    };
                    // An element with no candidate is an element nothing can trace.
                    // Writing an EMPTY provenance record would be worse than
                    // writing none: an audit would find a record and believe it.
                    if (c == null)
                    {
                        provenanceRows.Add(new JObject
                        {
                            ["element_id"] = id,
                            ["element_index"] = elementIndex,
                            ["written"] = false,
                            ["means"] = "no candidate in candidate_index matches this row's request index, so " +
                                        "NOTHING was stamped. The element exists and is ANONYMOUS: an incremental " +
                                        "run will not recognise it and an audit will report it as bim_without_source. " +
                                        "An empty provenance record would be worse - a reader would find one and believe it."
                        });
                        continue;
                    }

                    // WHAT IT WAS BUILT WITH, read off the element that was just
                    // committed rather than off the request - a wall Revit trimmed
                    // at a join was built with the trimmed line, and recording the
                    // request instead would make every joined corner look like
                    // somebody had moved it the next time this drawing is updated.
                    p.BuiltGeometry = CadUpdateRules.Encode(PlanGeometry(created));

                    // THE SIBLINGS GET THE SAME ORIGIN, and each is reported on
                    // its own row: a count that hid them would be the same silence
                    // this loop exists to prevent.
                    foreach (Element sibling in siblings)
                    {
                        string siblingWhy;
                        bool siblingOk = CadProvenanceStore.Write(sibling, p, out siblingWhy);
                        provenanceRows.Add(new JObject
                        {
                            ["element_id"] = Rid.Value(sibling.Id),
                            ["element_index"] = elementIndex,
                            ["candidate_id"] = p.CandidateId,
                            ["semantic_id"] = p.SemanticId,
                            ["rule_id"] = p.RuleId,
                            ["written"] = siblingOk,
                            ["made_alongside"] = id,
                            ["means"] = siblingOk
                                ? "one row asked for one thing and Revit made several; this is one of the " +
                                  "others, and it remembers the same CAD entity and the same rule"
                                : "a sibling of element " + id + " did not take its provenance, so it is " +
                                  "ANONYMOUS while its siblings are not. Revit said: " +
                                  (string.IsNullOrWhiteSpace(siblingWhy) ? "(nothing)" : siblingWhy)
                        });
                    }

                    string why;
                    bool ok = CadProvenanceStore.Write(created, p, out why);
                    provenanceRows.Add(new JObject
                    {
                        ["element_id"] = id,
                        ["element_index"] = elementIndex,
                        ["candidate_id"] = p.CandidateId,
                        ["semantic_id"] = p.SemanticId,
                        ["rule_id"] = p.RuleId,
                        ["written"] = ok,
                        ["means"] = ok
                            ? "this element remembers which CAD entity and which rule produced it"
                            : "the provenance entity did not land, so this element is created but ANONYMOUS " +
                              "and an incremental run will not recognise it. Revit said: " +
                              (string.IsNullOrWhiteSpace(why) ? "(nothing)" : why)
                    });
                }
                t.Commit();
            }
        }

        /// <summary>The element's own plan geometry, in mm, as committed.</summary>
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
        private static string SafeVersion(UIApplication a)
        { try { return a?.Application?.VersionNumber + "." + a?.Application?.VersionBuild; } catch { return null; } }
    }
}
