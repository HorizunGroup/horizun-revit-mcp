// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// horizun_apply_ifc_plan — build the plan horizun_plan_from_ifc produced, but only
// if it still means what it meant when it was made.
//
// THIS COMMAND CREATES NOTHING ITSELF. It resolves and calls the real
// horizun_create_elements, exactly as horizun_apply_cad_plan does, so every
// rehearsal, confirmation token, transaction and post-commit re-read is the one
// this bridge already has. A second creation path for IFC would double the
// surface that has to be trusted and halve the evidence for it.
//
// WHAT IT ADDS, and none of it could be got any other way:
//
//   THE BINDING. A plan is made against a file at a moment: those bytes, that
//   unit scale, those resolved types and levels. Between the plan and the apply
//   somebody can receive a new issue of the IFC, rename a wall type, delete the
//   level the plan resolved, or edit the actions in transit. All four aim the
//   plan at a different building and all four are silent. This re-hashes the
//   file, re-reads every resolved id by name, re-fingerprints the actions, and
//   refuses stale_plan naming WHICH one moved — before writing anything.
//
//   THE HOST RESOLUTION. An opening cannot name the wall it is cut into, and a
//   door cannot name the wall it hangs in, because those elements do not exist
//   until the stage before them commits. The plan carries host_global_id; this
//   substitutes the real host_id from what it has just created — or, on a second
//   run, from the provenance an earlier run left in the model.
//
//   THE PROVENANCE. Every created element is stamped with the IFC GlobalId, the
//   class, the representation used and the file's hash. Without it the second run
//   has no way to know what the first one built, and "update the model from the
//   new IFC" is not a thing anyone can do — a re-run would produce a second
//   building perfectly aligned with the first.
//
//   THE HONEST PARTIAL. Stages commit separately, because a four-thousand element
//   import in one transaction is an import nobody can recover from. So this
//   reports `partial` with exactly which stages landed and never claims an
//   atomicity it does not have.
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
    public sealed class ApplyIfcPlanCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        public ApplyIfcPlanCommand(Func<string, ICommand> resolve) { _resolve = resolve; }

        public string Name => "horizun_apply_ifc_plan";

        public string Description =>
            "Build a plan produced by horizun_plan_from_ifc, through the same typed create_elements this bridge " +
            "already verifies, adding three things only this command can: it RE-HASHES the IFC and re-reads every " +
            "resolved type and level, refusing stale_plan naming which moved; it resolves each opening and each " +
            "hosted family onto the wall built in an earlier stage, which is the only moment that id exists; and " +
            "it records the IFC GlobalId on every element it creates, which is what makes a second run an update " +
            "rather than a second building. Stages commit separately, so a partial result says which landed.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject binding = request["apply_binding"] as JObject;
            if (binding == null)
                return CommandResult.Fail(
                    "apply_binding is required — copy it verbatim from the horizun_plan_from_ifc reply. It names " +
                    "the file, its bytes, its unit scale and every type and level the plan resolved, and without " +
                    "it there is nothing to check the model against before writing.");

            string expectedSha = binding.Value<string>("source_sha256");
            string sourcePath = binding.Value<string>("source_path");
            string expectedActions = binding.Value<string>("actions_fingerprint");
            string expectedTarget = binding.Value<string>("target_document");
            string expectedRevit = binding.Value<string>("revit_version");
            long expectedBytes = binding.Value<long?>("source_bytes") ?? -1;
            double? plannedScale = binding.Value<double?>("length_scale_mm");
            if (string.IsNullOrWhiteSpace(expectedSha) || string.IsNullOrWhiteSpace(sourcePath) ||
                string.IsNullOrWhiteSpace(expectedActions))
                return CommandResult.Fail(
                    "apply_binding needs source_sha256, source_path and actions_fingerprint. A binding missing " +
                    "any of them came from a build whose plans could not be checked against the file they were " +
                    "made from; re-run horizun_plan_from_ifc.");

            // ---- RE-MEASURE. This is the whole reason this command exists. ------------
            var drift = new JArray();

            long nowBytes;
            string hashProblem;
            string nowSha = IfcSourceIdentity.Sha256(sourcePath, out nowBytes, out hashProblem);
            if (nowSha == null)
                return CommandResult.Fail(
                    "the IFC this plan was made from could not be re-read at '" + sourcePath + "': " +
                    hashProblem + ". NOTHING WAS WRITTEN. A plan that cannot be checked against its source is a " +
                    "plan aimed at a file nobody can see.");
            if (!string.Equals(nowSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                // THE SIZE IS THERE TO MAKE THIS READABLE. Two opaque hashes tell a person
                // nothing about what happened; "it grew by 4.2 MB" tells them which
                // re-export they are looking at. The hash is what DECIDES; the size is what
                // lets somebody act on the decision.
                string size = expectedBytes < 0
                    ? "the plan recorded no size"
                    : expectedBytes == nowBytes
                        ? "the size is unchanged at " + nowBytes + " bytes, so this is a re-export of the same " +
                          "model rather than a different file"
                        : "the size went from " + expectedBytes + " to " + nowBytes + " bytes";
                drift.Add(new JObject
                {
                    ["what"] = "the IFC file",
                    ["planned_against"] = expectedSha,
                    ["now"] = nowSha,
                    ["planned_bytes"] = expectedBytes < 0 ? (JToken)JValue.CreateNull() : expectedBytes,
                    ["now_bytes"] = nowBytes,
                    ["means"] = "the bytes changed since the plan was made — a new issue, a re-export, or a " +
                                "different file at the same path (" + size + "). The plan describes a different " +
                                "building."
                });
            }

            foreach (JObject resolved in (binding["resolved_names"] as JArray ?? new JArray()).OfType<JObject>())
            {
                long id = resolved.Value<long?>("id") ?? -1;
                string was = resolved.Value<string>("name");
                string what = resolved.Value<string>("what") ?? "element";
                Element now = null;
                try { if (Rid.CanRepresent(id)) now = doc.GetElement(Rid.Make(id)); } catch { }
                string nowName = null;
                try { nowName = now == null ? null : now.Name; } catch { }
                if (now != null && string.Equals(nowName, was, StringComparison.Ordinal)) continue;

                // THE RESOLVED IDS, checked by NAME and not only by number. Between a plan
                // and its apply somebody can delete the level the plan resolved, and Revit
                // hands its id to the next element created — so the actions are unchanged,
                // the fingerprint agrees, and every wall lands on whatever inherited the
                // number.
                drift.Add(new JObject
                {
                    ["what"] = "the resolved " + what,
                    ["planned_against"] = (resolved.Value<string>("label") ?? was) + " (id " + id + ")",
                    ["now"] = now == null ? "id " + id + " no longer exists" : nowName + " (id " + id + ")",
                    ["means"] = now == null
                        ? "the " + what + " this plan resolved has been deleted. NOTHING was written."
                        : "id " + id + " now names something else. The actions are unchanged — which is exactly " +
                          "why this is checked separately. NOTHING was written."
                });
            }

            JToken actionsToken = request["actions"];
            JArray submitted = actionsToken as JArray;
            if (actionsToken != null && actionsToken.Type != JTokenType.Null && submitted == null)
                return CommandResult.Fail(
                    "actions_not_a_list: 'actions' arrived as " + actionsToken.Type.ToString().ToLowerInvariant() +
                    ", and it must be the LIST the plan emitted — copy execute_plan_request.actions across " +
                    "unchanged. NOTHING was written. If your client serialises a one-element list as a single " +
                    "object, wrap it: [ { ... } ]. This is not stale_plan: no comparison was made against the " +
                    "file or the model.");

            string nowActions = IfcPlanFingerprint.OfActions(submitted ?? new JArray());
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
                    ["means"] = "this plan was made against a different model. Applying it here would build one " +
                                "model's IFC into another."
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
                    " moved between the plan and this apply. NOTHING WAS WRITTEN. Re-run horizun_plan_from_ifc " +
                    "and review the new plan; a plan aimed at a file that has since changed is a plan aimed at a " +
                    "different building. Drift: " + drift.ToString(Formatting.None));

            // ---- the actions ----------------------------------------------------------
            JArray actions = submitted;
            if (actions == null || actions.Count == 0)
                return CommandResult.Fail("actions is required: the execute_plan_request.actions the plan produced.");

            ICommand create = _resolve == null ? null : _resolve("horizun_create_elements");
            if (create == null)
                return CommandResult.Fail("horizun_create_elements is not available in this build; this command " +
                                          "creates nothing itself and has nothing to delegate to.");

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string idempotencyKey = request.Value<string>("idempotency_key");
            string target = request.Value<string>("target_document") ?? SafeTitle(doc);

            Dictionary<string, List<JObject>> candidates = ReadCandidateIndex(request["candidate_index"] as JArray);

            // WHAT ALREADY EXISTS, so a second run can hang a door on a wall the first run
            // built. Without this an incremental import could only ever add openings to
            // walls created in the same call.
            Dictionary<string, long> builtByGlobalId = IfcProvenanceStore.Index(doc);
            int preexisting = builtByGlobalId.Count;

            var stageResults = new JArray();
            var provenanceRows = new JArray();
            int created = 0, failedStages = 0, provenanceWritten = 0, provenanceRefused = 0, updatesLanded = 0;
            bool stopped = false;
            string stoppedBecause = null;

            var ordered = actions.OfType<JObject>()
                                 .Select((a, i) => new { Action = a, Index = i })
                                 .OrderBy(x => x.Action.Value<int?>("stage") ?? 99)
                                 .ThenBy(x => x.Index)
                                 .ToList();

            foreach (var entry in ordered)
            {
                if (stopped) break;
                JObject action = entry.Action;
                string key = action.Value<string>("key") ?? "(unnamed)";
                JObject args = action["arguments"] as JObject;
                if (args == null)
                {
                    stageResults.Add(new JObject
                    {
                        ["key"] = key,
                        ["state"] = "malformed",
                        ["error"] = "the action carries no arguments object"
                    });
                    failedStages++;
                    stopped = true;
                    stoppedBecause = "an action was malformed";
                    break;
                }

                var callArgs = (JObject)args.DeepClone();
                List<JObject> rowCandidates;
                if (!candidates.TryGetValue(key, out rowCandidates)) rowCandidates = new List<JObject>();

                // ---- the host substitution --------------------------------------------
                string hostProblem;
                if (!ResolveHosts(callArgs, rowCandidates, builtByGlobalId, out hostProblem))
                {
                    stageResults.Add(new JObject
                    {
                        ["key"] = key,
                        ["state"] = "unresolved_host",
                        ["error"] = hostProblem
                    });
                    failedStages++;
                    stopped = true;
                    stoppedBecause = "a row names a host that is not in this model and was not built by an " +
                                     "earlier stage";
                    break;
                }

                callArgs["target_document"] = target;
                callArgs["dry_run"] = dryRun;
                if (!dryRun)
                {
                    string token = action.Value<string>("confirmation_token") ??
                                   request.Value<string>("confirmation_token");
                    if (!string.IsNullOrWhiteSpace(token)) callArgs["confirmation_token"] = token;
                    if (!string.IsNullOrWhiteSpace(idempotencyKey))
                        callArgs["idempotency_key"] = idempotencyKey + "-" + key;
                }

                // TWO KINDS OF ACTION, DISPATCHED BY WHAT THE PLAN SAID IT WAS. An update
                // acts on an element that already exists and goes nowhere near
                // create_elements, whose whole job is to make new ones.
                bool isUpdate = string.Equals(action.Value<string>("tool"), "ifc_update_in_place",
                                              StringComparison.Ordinal);
                CommandResult result = isUpdate
                    ? ApplyUpdates(doc, callArgs, dryRun)
                    : create.Execute(app, callArgs.ToString(Formatting.None));
                var row = new JObject
                {
                    ["key"] = key,
                    ["stage"] = action.Value<int?>("stage"),
                    ["ok"] = result.Success
                };

                if (!result.Success)
                {
                    row["state"] = "failed";
                    row["error"] = result.Error;
                    failedStages++;
                    stopped = true;
                    stoppedBecause = "stage '" + key + "' failed, and the stages after it depend on it";
                    stageResults.Add(row);
                    break;
                }

                JObject data = result.Data as JObject;
                int madeHere = data == null ? 0 : (data.Value<int?>("created_verified") ?? 0);
                int invalid = data == null ? 0 : (data.Value<int?>("invalid") ?? 0);
                int valid = data == null ? 0 : (data.Value<int?>("valid") ?? 0);
                created += madeHere;
                if (isUpdate && data != null) updatesLanded += data.Value<int?>("updated") ?? 0;

                // A REHEARSAL THAT COULD NOT PLAN A SINGLE ROW IS NOT A CLEAN REHEARSAL.
                // create_elements answers a dry run with valid/invalid counts and does NOT
                // fail the call: the request was well formed and the answer is "none of
                // these can be built". Grading on Success alone would send a caller to an
                // apply that commits the earlier stages and refuses this one whole.
                if (dryRun && invalid > 0)
                {
                    row["ok"] = false;
                    row["state"] = valid > 0 ? "rehearsed_partial" : "rehearsed_nothing";
                    row["valid"] = valid;
                    row["invalid"] = invalid;
                    row["invalid_rows"] = data == null ? null : data["errors"];
                    row["means"] = valid > 0
                        ? invalid + " of " + (valid + invalid) + " row(s) in this stage cannot be built and are " +
                          "named above. create_elements refuses a batch WHOLE when any row is invalid, so " +
                          "applying builds NONE of this stage — not the other " + valid + "."
                        : "NOTHING in this stage can be built: all " + invalid + " row(s) were refused, each " +
                          "with its reason. This is not a rehearsal that passed.";
                    failedStages++;
                }
                else if (isUpdate && data != null && data.Value<bool?>("coverage_complete") != true)
                {
                    // AN UPDATE STAGE THAT DID NOT COVER ITS ELEMENTS IS NOT "applied". ApplyUpdates
                    // answers Ok even when every element was gone, lost its identity or was
                    // refused; grading on Success alone made that stage - and the whole plan -
                    // read "applied" with nothing written. Its own counts decide now.
                    int updatedHere = data.Value<int?>("updated") ?? 0;
                    int unchangedHere = data.Value<int?>("unchanged") ?? 0;
                    row["ok"] = false;
                    row["state"] = (dryRun ? "rehearsed" : "applied") +
                                   (updatedHere + unchangedHere > 0 ? "_partial" : "_nothing");
                    row["updated"] = updatedHere;
                    row["unchanged"] = unchangedHere;
                    row["refused"] = data["refused"];
                    row["skipped"] = data["skipped"];
                    row["partially_updated"] = data["partially_updated"];
                    row["means"] = "coverage_complete is false: not every element of this update stage was " +
                                   "updated or confirmed unchanged. See the stage's element rows.";
                    failedStages++;
                }
                else
                {
                    row["state"] = dryRun ? "rehearsed" : "applied";
                }
                row["created_verified"] = madeHere;
                if (data != null && data["confirmation_token"] != null)
                    row["confirmation_token"] = data["confirmation_token"];

                if (!dryRun && data != null && data["rows"] is JArray createdRows)
                {
                    int written, refused;
                    RecordProvenance(doc, createdRows, rowCandidates, binding, sourcePath, nowSha,
                                     builtByGlobalId, provenanceRows, out written, out refused);
                    provenanceWritten += written;
                    provenanceRefused += refused;
                    row["provenance_written"] = written;
                    if (refused > 0) row["provenance_refused"] = refused;
                }

                stageResults.Add(row);
            }

            string state = failedStages > 0
                ? (created + updatesLanded > 0 ? "partial" : "failed")
                : (dryRun ? "rehearsed" : "applied");

            var payload = new JObject
            {
                ["document"] = SafeTitle(doc),
                ["ifc_path"] = sourcePath,
                ["ifc_sha256"] = nowSha,
                ["ifc_bytes"] = nowBytes,
                ["plan_was_made_with"] = new JObject
                {
                    ["length_scale_mm"] = plannedScale.HasValue
                        ? (JToken)plannedScale.Value : JValue.CreateNull(),
                    ["means"] = "every coordinate in this plan is in millimetres, derived with that many " +
                                "millimetres per IFC length unit. This command did NOT re-derive it: it hashes " +
                                "the file rather than re-parsing it, and an identical hash is what guarantees " +
                                "the scale has not moved."
                },
                ["state"] = state,
                ["dry_run"] = dryRun,
                ["stages"] = stageResults,
                ["created_verified"] = created,
                ["stages_failed"] = failedStages,
                ["stopped_because"] = stoppedBecause,
                ["provenance"] = new JObject
                {
                    ["elements_stamped"] = provenanceWritten,
                    ["elements_refused"] = provenanceRefused,
                    ["already_in_model_before_this_run"] = preexisting,
                    ["rows"] = provenanceRows,
                    ["means"] = provenanceRefused > 0
                        ? "some elements were BUILT and not stamped. They are in the model with no recorded IFC " +
                          "origin, so the next run will plan them again and build them twice. The rows above " +
                          "name them and say what Revit refused."
                        : "every created element carries the IFC GlobalId it came from, which is what makes the " +
                          "next run an update rather than a second building."
                },
                ["atomicity"] =
                    "Stages commit SEPARATELY. This is not one transaction and does not claim to be: an import " +
                    "of thousands of elements in one transaction is one nobody can recover from. A partial " +
                    "result names exactly which stages landed.",
                ["not_verified_by_this_command"] =
                    "The counts above come from create_elements, which re-reads every element after its own " +
                    "commit. This command verified the BINDING — the file, the resolved types and levels, the " +
                    "actions — and the host resolution. It did not re-measure the geometry against the IFC."
            };

            return failedStages > 0 && created == 0
                ? CommandResult.FailWithDetail("ifc_apply_failed: " +
                      (stoppedBecause ?? "no stage could be applied") + ". See stages for the reason each gave.",
                      payload)
                : CommandResult.Ok(payload);
        }

        // =====================================================================
        // Hosts
        // =====================================================================

        /// <summary>
        /// Put the real host_id on every row whose candidate names a host_global_id.
        ///
        /// This is the one thing the plan could not do. An opening's host is a wall that
        /// does not exist until the wall stage commits, so the plan names the IFC entity
        /// and this names the element. A host that cannot be resolved STOPS the stage
        /// rather than building an opening in whatever wall happens to be there.
        /// </summary>
        private static bool ResolveHosts(JObject args, List<JObject> candidates,
                                         Dictionary<string, long> builtByGlobalId, out string problem)
        {
            problem = null;
            var rows = args["elements"] as JArray;
            if (rows == null) return true;

            for (int index = 0; index < rows.Count; index++)
            {
                JObject row = rows[index] as JObject;
                if (row == null) continue;

                JObject candidate = candidates.FirstOrDefault(x => x.Value<int?>("element_index") == index);
                string hostGlobalId = candidate == null ? null : candidate.Value<string>("host_global_id");
                if (string.IsNullOrWhiteSpace(hostGlobalId)) continue;

                long hostId;
                if (!builtByGlobalId.TryGetValue(hostGlobalId, out hostId))
                {
                    problem = "row " + index + " of this stage is hosted on the IFC entity " + hostGlobalId +
                              ", and no element in this model carries that GlobalId. Either its wall stage did " +
                              "not run, or its wall was skipped by the plan. NOTHING was written for this stage " +
                              "or the ones after it: an opening cut into whatever wall happened to be there is " +
                              "worse than an opening nobody cut.";
                    return false;
                }
                row["host_id"] = hostId;
            }
            return true;
        }

        // =====================================================================
        // Provenance
        // =====================================================================

        /// <summary>
        /// Update the elements a re-issued file describes differently.
        ///
        /// ONE TRANSACTION FOR THE BATCH, and a dry run is the same work rolled back. Per
        /// element the reply carries every field with its outcome - updated, unchanged,
        /// unsupported or refused - because a field that cannot be written in place is a
        /// fact about this build that the reader needs, not something to drop.
        /// </summary>
        private static CommandResult ApplyUpdates(Document doc, JObject args, bool dryRun)
        {
            if (doc == null) return CommandResult.Fail("no active document for the update stage.");
            JArray elements = args?["elements"] as JArray;
            if (elements == null || elements.Count == 0)
                return CommandResult.Fail("the update action carries no elements.");

            Dictionary<string, IfcProvenance> provenance;
            try { provenance = IfcProvenanceStore.Records(doc); }
            catch (Exception ex)
            {
                return CommandResult.Fail("the IFC provenance could not be read (" + ex.Message +
                                          "), and an update that cannot check identity must not write.");
            }

            var rows = new JArray();
            int updated = 0, unchanged = 0, refused = 0, unsupported = 0, skipped = 0;

            string name = args.Value<string>("transaction_name") ?? "Horizun: IFC update";
            using (var tx = new Transaction(doc, name))
            {
                if (tx.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("the update transaction would not start.");

                foreach (JObject element in elements.OfType<JObject>())
                {
                    string globalId = element.Value<string>("global_id");
                    long elementId = element.Value<long?>("element_id") ?? -1;
                    JObject row = element["row"] as JObject;

                    var record = new JObject
                    {
                        ["global_id"] = globalId,
                        ["element_id"] = elementId,
                        ["ifc_class"] = element.Value<string>("ifc_class"),
                        ["name"] = element.Value<string>("name")
                    };

                    Element target = elementId < 0 ? null : doc.GetElement(Rid.Make(elementId));
                    if (target == null)
                    {
                        record["state"] = "gone";
                        record["means"] =
                            "the element this update was planned for is no longer in the document. It was " +
                            "deleted between the plan and the apply. Nothing was written; re-plan to have it " +
                            "created again if it should exist.";
                        rows.Add(record);
                        skipped++;
                        continue;
                    }

                    // IDENTITY. Revit reuses ids, so the id alone proves nothing.
                    IfcProvenance stored;
                    if (globalId == null || !provenance.TryGetValue(globalId, out stored) ||
                        stored.ElementId != elementId)
                    {
                        record["state"] = "identity_lost";
                        record["means"] =
                            "element " + elementId + " no longer carries the IFC provenance for " + globalId +
                            ". Revit reuses element ids, so this id may now name something else entirely. " +
                            "Nothing was written: an IFC geometry written into whatever holds an id is the " +
                            "worst outcome on this path.";
                        rows.Add(record);
                        skipped++;
                        continue;
                    }

                    string fatal;
                    List<IfcFieldOutcome> outcomes = IfcUpdate.ApplyRow(doc, target, row, out fatal);
                    if (fatal != null)
                    {
                        record["state"] = "refused";
                        record["means"] = fatal;
                        rows.Add(record);
                        refused++;
                        continue;
                    }

                    int wrote = outcomes.Count(o => o.State == IfcUpdate.Updated);
                    int no = outcomes.Count(o => o.State == IfcUpdate.Refused);
                    int cannot = outcomes.Count(o => o.State == IfcUpdate.Unsupported);

                    record["fields"] = new JArray(outcomes.Select(o => (JToken)o.Json()));
                    record["state"] = no > 0 ? "partial" : cannot > 0 ? "partial" : wrote > 0 ? "updated" : "unchanged";

                    // THE BASELINE MOVES ONLY ON A CLEAN UPDATE. Anything else keeps the old
                    // fingerprint so the next run detects the same difference again, which
                    // is correct: the difference is still there.
                    bool clean = no == 0 && cannot == 0;
                    record["baseline_moved"] = clean && !dryRun;
                    record["baseline_means"] = clean
                        ? "every field this file describes now matches the model, so the row fingerprint " +
                          "becomes the new baseline and the next import will call this element unchanged."
                        : "at least one field could not be written, so the OLD fingerprint is kept and the " +
                          "next import will report this element as changed again. That is deliberate: a " +
                          "difference that still exists must not disappear from the report.";

                    if (clean && !dryRun)
                    {
                        stored.RowFingerprint = element.Value<string>("row_fingerprint");
                        string storeError;
                        bool stored_ok;
                        try { stored_ok = IfcProvenanceStore.Write(target, stored, out storeError); }
                        catch (Exception ex) { stored_ok = false; storeError = ex.Message; }

                        if (!stored_ok)
                        {
                            record["baseline_moved"] = false;
                            record["baseline_means"] =
                                "the update verified and the new baseline could NOT be stored (" +
                                (storeError ?? "no reason given") + "), so the next import will report this " +
                                "element as changed again and try the same update. That is the safe direction: " +
                                "the alternative is a baseline that claims a state the store never recorded.";
                        }
                    }

                    rows.Add(record);
                    if (record.Value<string>("state") == "updated") updated++;
                    else if (record.Value<string>("state") == "unchanged") unchanged++;
                    else if (no > 0) refused++;
                    else unsupported++;
                }

                if (dryRun)
                {
                    // THE REHEARSAL. Everything above happened; none of it stays.
                    Guard.RollBack(tx);
                }
                else if (tx.Commit() != TransactionStatus.Committed)
                {
                    return CommandResult.Fail("the update transaction did not commit; nothing was written.");
                }
            }

            // ---- the second pass: closed profiles ----------------------------------
            // OUTSIDE the transaction above, because a SketchEditScope opens its own
            // transaction context and Revit does not nest them. Each profile is replaced
            // on its own, so one refusal costs only that element.
            int profilesUpdated = 0, profilesRefused = 0;
            foreach (JObject element in elements.OfType<JObject>())
            {
                JObject row = element["row"] as JObject;
                JArray loop = IfcUpdate.ProfileLoop(row);
                if (loop == null) continue;

                long elementId = element.Value<long?>("element_id") ?? -1;
                JObject record = rows.OfType<JObject>().FirstOrDefault(
                    r => (r.Value<long?>("element_id") ?? -2) == elementId);
                if (record == null) continue;

                IfcFieldOutcome outcome;
                if (dryRun)
                {
                    // A REHEARSAL CANNOT SHOW THIS. The rehearsal is a transaction that gets
                    // rolled back and a sketch scope cannot open inside one either, so the
                    // only honest answer is that it was not rehearsed - never a prediction
                    // dressed as one.
                    outcome = new IfcFieldOutcome
                    {
                        Field = "profile",
                        State = IfcUpdate.Deferred,
                        Reason = "NOT REHEARSED. A profile is replaced through a SketchEditScope, which cannot " +
                                 "open inside the rehearsal transaction - the same Revit constraint that makes " +
                                 "it a second pass on the apply. What Revit will make of this boundary is not " +
                                 "predicted here, because a prediction about its geometry engine is the thing " +
                                 "rehearsing exists to replace."
                    };
                }
                else
                {
                    Element target = elementId < 0 ? null : doc.GetElement(Rid.Make(elementId));
                    outcome = IfcProfileUpdate.Replace(doc, target, loop);
                    if (outcome.State == IfcUpdate.Updated) profilesUpdated++;
                    else if (outcome.State == IfcUpdate.Refused) profilesRefused++;
                }

                var fields = record["fields"] as JArray ?? new JArray();
                fields.Add(outcome.Json());
                record["fields"] = fields;

                // A PROFILE THAT DID NOT LAND KEEPS THE ELEMENT OFF THE NEW BASELINE, for
                // the same reason as every other field: the difference is still there.
                if (outcome.State != IfcUpdate.Updated && outcome.State != IfcUpdate.Unchanged)
                {
                    record["baseline_moved"] = false;
                    record["baseline_means"] =
                        "the profile could not be replaced, so the OLD row fingerprint is kept and the next " +
                        "import will report this element as changed again.";
                }
            }

            var payload = new JObject
            {
                ["dry_run"] = dryRun,
                ["requested"] = elements.Count,
                ["updated"] = updated,
                ["unchanged"] = unchanged,
                ["partially_updated"] = unsupported,
                ["refused"] = refused,
                ["skipped"] = skipped,
                ["profiles_updated"] = profilesUpdated,
                ["profiles_refused"] = profilesRefused,
                ["coverage_complete"] = refused == 0 && unsupported == 0 && skipped == 0 &&
                                        profilesRefused == 0,
                ["elements"] = rows,
                ["means"] = dryRun
                    ? "REHEARSED AND ROLLED BACK. Every update above was performed against the model and read " +
                      "back before being undone, so these outcomes are what Revit actually did rather than " +
                      "what this build predicted it would do."
                    : "each field was written and RE-READ from the model after the write. A field reported as " +
                      "updated was read back with the new value; one reported as refused was read back with " +
                      "the old one."
            };
            return CommandResult.Ok(payload);
        }

        private static void RecordProvenance(Document doc, JArray createdRows, List<JObject> candidates,
                                             JObject binding, string sourcePath, string sourceSha,
                                             Dictionary<string, long> builtByGlobalId, JArray report,
                                             out int written, out int refused)
        {
            written = 0;
            refused = 0;
            string planFingerprint = binding.Value<string>("plan_fingerprint");
            string stamp = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

            using (var transaction = new Transaction(doc, "Horizun: record IFC provenance"))
            {
                try { transaction.Start(); }
                catch (Exception ex)
                {
                    report.Add(new JObject
                    {
                        ["error"] = "the provenance transaction could not be started: " + ex.Message,
                        ["means"] = "the elements are BUILT and unstamped; the next run will plan them again."
                    });
                    refused += createdRows.Count;
                    return;
                }

                foreach (JObject made in createdRows.OfType<JObject>())
                {
                    // KEY OFF THE ROW'S OWN INDEX, never off its position in the reply.
                    // create_elements reports the index of the element in the REQUEST, and a
                    // reply that omitted or reordered a row would otherwise stamp every
                    // element after it with somebody else's origin — which is worse than
                    // stamping none, because an audit would then confidently trace a wall to
                    // the wrong IFC entity.
                    int? index = made.Value<int?>("index");
                    if (!index.HasValue) continue;

                    JObject candidate = candidates.FirstOrDefault(x => x.Value<int?>("element_index") == index.Value);
                    if (candidate == null) continue;

                    long id = made.Value<long?>("element_id") ?? -1;
                    if (id < 0 || !Rid.CanRepresent(id)) continue;
                    Element element = doc.GetElement(Rid.Make(id));
                    if (element == null) continue;

                    var record = new IfcProvenance
                    {
                        GlobalId = candidate.Value<string>("global_id"),
                        IfcClass = candidate.Value<string>("ifc_class"),
                        Representation = candidate.Value<string>("representation_used"),
                        SourceSha256 = sourceSha,
                        SourcePath = sourcePath,
                        PlanFingerprint = planFingerprint,
                        // THE ROW'S OWN HASH, carried from the plan. It is what the NEXT plan
                        // compares against to tell "already built and unchanged" from "already
                        // built and this file describes it differently" - the distinction that
                        // makes a re-run an update instead of a no-op.
                        RowFingerprint = candidate.Value<string>("row_fingerprint"),
                        WrittenUtc = stamp
                    };

                    string lastError;
                    if (IfcProvenanceStore.Write(element, record, out lastError))
                    {
                        written++;
                        if (!string.IsNullOrWhiteSpace(record.GlobalId))
                            builtByGlobalId[record.GlobalId] = id;
                        report.Add(new JObject
                        {
                            ["element_id"] = id,
                            ["global_id"] = record.GlobalId,
                            ["ifc_class"] = record.IfcClass,
                            ["representation_used"] = record.Representation
                        });
                    }
                    else
                    {
                        refused++;
                        report.Add(new JObject
                        {
                            ["element_id"] = id,
                            ["global_id"] = record.GlobalId,
                            ["error"] = lastError,
                            ["means"] = "this element is BUILT and carries no IFC origin. The next run will plan " +
                                        "it again and build a second copy in the same place."
                        });
                    }
                }

                try { transaction.Commit(); }
                catch (Exception ex)
                {
                    refused += written;
                    written = 0;
                    report.Add(new JObject
                    {
                        ["error"] = "the provenance transaction would not commit: " + ex.Message,
                        ["means"] = "the elements are BUILT and unstamped; the next run will plan them again."
                    });
                }
            }
        }

        // =====================================================================

        private static Dictionary<string, List<JObject>> ReadCandidateIndex(JArray index)
        {
            var byKey = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
            foreach (JObject entry in (index ?? new JArray()).OfType<JObject>())
            {
                string key = entry.Value<string>("key");
                if (string.IsNullOrWhiteSpace(key)) continue;
                List<JObject> rows;
                if (!byKey.TryGetValue(key, out rows)) byKey[key] = rows = new List<JObject>();
                rows.Add(entry);
            }
            return byKey;
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc == null ? null : doc.Title; } catch { return null; }
        }

        private static string SafeVersion(UIApplication app)
        {
            try { return app == null || app.Application == null ? null : app.Application.VersionBuild; }
            catch { return null; }
        }
    }

    /// <summary>
    /// The fingerprint over a plan's actions, in ONE place so the two halves cannot
    /// disagree about what they are hashing. The keys that say HOW a request is made —
    /// the token, the dry run, the idempotency key — are stripped, because they are not
    /// part of what gets built.
    /// </summary>
    public static class IfcPlanFingerprint
    {
        private static readonly HashSet<string> NotWhatIsBuilt = new HashSet<string>(StringComparer.Ordinal)
        {
            "confirmation_token", "dry_run", "idempotency_key", "target_document"
        };

        public static string OfActions(JToken actions) =>
            "ifcacts:" + RequestFingerprint.Sha256Hex(
                RequestFingerprint.Canonical(Normalise(actions ?? new JArray()))).Substring(0, 32);

        /// <summary>
        /// Strip the how-it-is-called keys, and ONLY at the two levels where they mean
        /// that: the action itself and its top-level arguments. A key of the same name
        /// deeper down — on an element row — is left alone, because there it is part of
        /// what gets built.
        /// </summary>
        private static JToken Normalise(JToken actions)
        {
            var list = actions as JArray;
            if (list == null) return actions;

            var output = new JArray();
            foreach (JToken item in list)
            {
                var action = item as JObject;
                if (action == null) { output.Add(item); continue; }

                var copy = new JObject();
                foreach (JProperty property in action.Properties())
                {
                    if (NotWhatIsBuilt.Contains(property.Name)) continue;
                    var arguments = property.Value as JObject;
                    if (property.Name != "arguments" || arguments == null)
                    {
                        copy[property.Name] = property.Value;
                        continue;
                    }
                    var argumentCopy = new JObject();
                    foreach (JProperty argument in arguments.Properties())
                        if (!NotWhatIsBuilt.Contains(argument.Name)) argumentCopy[argument.Name] = argument.Value;
                    copy[property.Name] = argumentCopy;
                }
                output.Add(copy);
            }
            return output;
        }
    }
}
