// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_split_multilayer_walls - the typed command.
//
// This tool used to be a Python recipe ported from a pyRevit button, and the
// port kept the button's shape: create a wall per layer, re-create the doors on
// the structural one, delete the original. The host around it was honest - it
// owned the transaction, it required a token, it re-read counts after the commit
// - but the two counts it re-read were "how many walls exist" and "is the
// original gone", and neither of those is the question. all_verified:true was
// compatible with every door in the wall having been destroyed and rebuilt
// without its parameters, and with the whole stack sitting half a wall away from
// where it belonged.
//
// The strategy is inverted here. The original wall is NOT deleted: it becomes
// the single-layer wall of the core, so it keeps its ElementId, its UniqueId and
// everything hosted in it. Only the other layers are created. And nothing is
// reported that was not re-read from the model: the position of every layer, the
// name and composition of every type, the identity, host, placement, phase,
// subcomponents and parameters of every insert, and whether the opening actually
// passes through each layer.
//
// A wall that cannot be converted with all of that intact is REFUSED, by name,
// before a transaction exists - or rolled back alone if the model surprises us
// after one is open. See docs/WALL-LAYER-DECOMPOSITION.md for the invariants and
// for the audit of what the previous implementation did instead.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed class SplitMultilayerWallsCommand : ICommand
    {
        public string Name => "horizun_split_multilayer_walls";

        public string Description =>
            "Split compound walls into ONE SINGLE-LAYER WALL PER MATERIAL LAYER, keeping the ORIGINAL element as " +
            "the wall of the core - so its ElementId, its UniqueId and every door, window, opening, sweep and " +
            "reveal hosted in it are preserved rather than rebuilt.";

        private static readonly string[] ScopeFields =
        {
            "element_ids", "view_id", "origin_group_param",
            "core_carrier_policy", "parameter_copy_policy", "allow_arc_walls", "failure_policy"
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            bool dryRun;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                // Default TRUE. This rewrites geometry somebody is going to build from.
                dryRun = request.Value<bool?>("dry_run") ?? true;
            }
            catch (JsonException ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            WallSplitOptions options;
            string optionError;
            if (!ReadOptions(request, gate, out options, out optionError))
                return CommandResult.Fail(Name + ": " + optionError);

            // ---- scope ---------------------------------------------------------------
            //
            // AN EMPTY element_ids IS NOT A REQUEST TO CONVERT THE WHOLE MODEL.
            //
            // Omitting the field means "no scope given, use the default", and the default
            // is documented: the view, or failing that every wall in the document. But an
            // array that is PRESENT AND EMPTY is what a caller sends when its own filter
            // matched nothing - a selection that came back empty, a query that found no
            // wall on a level. Reading those two as the same thing turns "I selected
            // nothing" into "convert everything", which is the widest possible reading of
            // the narrowest possible request.
            //
            // The two costs are not comparable. Refusing costs one clear error to a caller
            // that meant the whole model and can say so by omitting the field; accepting
            // costs a whole-model conversion nobody asked for. So an empty array is a
            // refusal, and it names the two ways to say what was meant.
            JArray declaredIds = request["element_ids"] as JArray;
            if (declaredIds != null && declaredIds.Count == 0)
                return CommandResult.Fail(Name + ": element_ids was given as an EMPTY array, and " +
                    "an empty selection is not a request to convert every wall in the document. " +
                    "This is what a caller sends when its own filter matched nothing. To convert " +
                    "specific walls, pass their ids; to convert a view or the whole model " +
                    "deliberately, OMIT element_ids (and pass view_id to limit it to one view). " +
                    "Nothing was read and nothing was written.");

            var missing = new JArray();
            var wrongType = new JArray();
            List<Wall> walls = ResolveScope(doc, request, missing, wrongType);

            // ---- read every wall, before anything is opened --------------------------
            //
            // The reverse census is a document-wide scan of dimensions and tags, so it is
            // built ONCE and shared: fifty walls must not mean fifty scans of the same two
            // collectors.
            WallReverseCensus reverse = WallReverseCensus.Build(doc);
            WallProvenanceIndex provenance = WallProvenanceIndex.Build(doc);

            // Carried into the executor so the apply-time revalidation reads the wall with
            // exactly the same inputs this pass did.
            options.Reverse = reverse;
            options.Provenance = provenance;
            var subjects = new List<WallSplitSubject>();
            foreach (Wall wall in walls)
            {
                try { subjects.Add(WallSplitFacts.Read(doc, wall, options.DocumentKey, options.CoreCarrierPolicy, options.AllowArcWalls, reverse, provenance)); }
                catch (Exception ex)
                {
                    subjects.Add(new WallSplitSubject
                    {
                        Wall = wall,
                        ElementId = Rid.Value(wall.Id),
                        UniqueId = WallSplitFacts.SafeUniqueId(wall),
                        Rejection = new WallSplitRejection(WallSplitCodes.NotAWall,
                            "this wall could not be read: " + ex.Message)
                    });
                }
            }

            // Three buckets, not two. A batch legitimately contains walls to convert, walls
            // that were ALREADY converted, and walls refused for some other reason - and
            // folding the second into the third would make "you already did this" read as a
            // failure, which is how somebody talks themselves into running it again.
            List<WallSplitSubject> eligible = subjects.Where(s => s.Eligible).ToList();
            List<WallSplitSubject> alreadyConverted = subjects.Where(s => s.AlreadyConverted).ToList();
            List<WallSplitSubject> rejected = subjects.Where(s => !s.Eligible && !s.AlreadyConverted).ToList();

            var scope = new JObject
            {
                ["resolved"] = walls.Count,
                ["missing_ids"] = missing,
                ["wrong_type_ids"] = wrongType
            };

            string planHash = DocumentGate.PlanHash(request, ScopeFields);
            ResolvedPlan resolvedPlan = BuildResolvedPlan(gate, app, eligible);

            // ---- dry run --------------------------------------------------------------
            if (dryRun)
            {
                var result = new JObject
                {
                    ["tool"] = Name,
                    ["schema_version"] = WallSplitCodes.SchemaVersion,
                    ["dry_run"] = true,
                    ["tolerance_mm"] = WallLayerRules.ToleranceMm,
                    ["scope"] = scope,
                    ["reverse_census_ran"] = reverse.ScanRan,
                    ["provenance_index_ran"] = provenance.ScanRan,
                    ["policies"] = Policies(options),
                    ["eligible"] = new JArray(eligible.Select(Preflight)),
                    ["already_converted"] = new JArray(alreadyConverted.Select(Converted)),
                    ["rejected"] = new JArray(rejected.Select(Refusal)),
                    ["would_convert_walls"] = eligible.Count,
                    ["already_split_walls"] = alreadyConverted.Count(w =>
                        w.ProvenanceState == WallSplitCodes.AlreadySplit),
                    ["partial_state_walls"] = alreadyConverted.Count(w =>
                        w.ProvenanceState == WallSplitCodes.RepairablePartialState),
                    ["would_produce_walls"] = eligible.Sum(s => s.Plan.WouldProduceWalls),
                    ["applied"] = JValue.CreateNull(),
                    ["verified"] = JValue.CreateNull(),
                    ["note"] =
                        "DRY RUN: no transaction was opened and NOTHING was written. Every entry under 'eligible' " +
                        "carries the layer plan, the core range, the chosen carrier and why, the dependency ledger " +
                        "and the checks that will run. THE ORIGINAL WALL IS NOT DELETED by this tool - it becomes " +
                        "the single-layer wall of the core, which is how its inserts keep their own ElementIds. " +
                        "Walls under 'already_converted' came out of a previous run: they are reported with the " +
                        "state of their WHOLE sibling set and no transaction is ever opened for them, whether you " +
                        "named the carrier or one of its layer walls. Call again with dry_run=false and the " +
                        "confirmation_token to apply."
                };

                DocumentGate.RecordResolvedPlan(resolvedPlan);
                ApplicationOutcome.StampRehearsal(result, subjects.Count,
                                                  rejected.Count + alreadyConverted.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, true,
                    "the token binds EACH WALL INDIVIDUALLY: its UniqueId, its type, its whole compound structure, " +
                    "its location line, its curve on a 0.1 mm grid, its flip, its constraints and the identity of " +
                    "every dependency it carries. A wall that moved, was re-typed, or gained a door since the dry " +
                    "run refuses as stale rather than being written to.");
                return CommandResult.Ok(result);
            }

            // ---- apply ----------------------------------------------------------------
            if (eligible.Count == 0)
                return CommandResult.Fail(Name + ": no wall in scope is eligible, so there is nothing to apply. " +
                    (alreadyConverted.Count > 0
                        ? alreadyConverted.Count + " wall(s) in scope have ALREADY been split and were not touched: " +
                          new JArray(alreadyConverted.Select(Converted)).ToString(Formatting.None) + ". "
                        : "") +
                    "The other reasons are per wall: " + new JArray(rejected.Select(Refusal)).ToString(Formatting.None));

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                    resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            const string transactionName = "Horizun: split compound walls into layers";
            var outcomes = new List<WallSplitOutcome>();
            var warnings = new WallOverlapPreprocessor();

            using (var transaction = new Transaction(doc, transactionName))
            {
                FailureHandlingOptions handling = transaction.GetFailureHandlingOptions();
                handling.SetFailuresPreprocessor(warnings);
                handling.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(handling);

                if (transaction.Start() != TransactionStatus.Started)
                    return CommandResult.Fail(Name + ": the transaction would not start. Nothing was written.");

                try
                {
                    // One SubTransaction per wall, inside the executor. A wall that fails
                    // rolls back ALONE and the ones already verified keep their conversion.
                    foreach (WallSplitSubject subject in eligible)
                        outcomes.Add(WallSplitExecutor.Execute(doc, subject, options));

                    Guard.Commit(transaction, transactionName);
                }
                catch (Exception ex)
                {
                    bool attempted = false;
                    string rollback = PlanFailure.NotAttempted;
                    if (transaction.GetStatus() == TransactionStatus.Started)
                    {
                        attempted = true;
                        rollback = Guard.RollBack(transaction).StatusName;
                    }
                    return CommandResult.Fail(Name + " failed: " + ex.Message + ". " +
                        PlanFailure.SingleTransactionOutcome(attempted, rollback, "no wall was converted"));
                }
            }

            // ---- what the model holds now ---------------------------------------------
            List<WallSplitOutcome> applied = outcomes.Where(o => o.Applied).ToList();
            List<WallSplitOutcome> rolledBack = outcomes.Where(o => !o.Applied).ToList();

            // ---- the SAME verifier, re-run on the COMMITTED document ------------------
            //
            // Not "does the element exist" - that was the whole complaint. This is the
            // identical pass that ran inside each SubTransaction, run again against the
            // document everybody else will open: layer count, single-layer types, names,
            // materials, widths, functions, offsets, carrier identity, every dependency by
            // its own verifier, the cuts at five points each, the joins, and the provenance
            // of the whole sibling set.
            //
            // WHAT IT CANNOT DO IS UNDO. The outer transaction is closed by the time it
            // runs, so a failure here is REPORTED and takes all_verified down; it cannot
            // roll a wall back. That is a real limit of the transaction architecture and it
            // is stated rather than papered over.
            var verification = new JArray();
            int agreed = 0;
            var postCommitFailures = new JArray();

            foreach (WallSplitOutcome outcome in applied)
            {
                VerificationReport again = outcome.Expectation == null
                    ? null
                    : WallSplitVerifier.Run(doc, outcome.Expectation, VerificationPhase.AfterOuterCommit);

                outcome.PostCommitVerification = again?.ToJson();

                var block = JObject.FromObject(Guard.Verify(
                    "single-layer walls for wall " + outcome.SourceWallId,
                    outcome.WallsExpected,
                    again != null && again.Passed ? outcome.WallsProduced : -1));
                block["source_wall_id"] = outcome.SourceWallId;
                block["post_commit_passed"] = again?.Passed;
                block["post_commit_code"] = again?.Code;
                block["post_commit_can_roll_back"] = false;

                if (again == null || !again.Passed)
                    postCommitFailures.Add(new JObject
                    {
                        ["source_wall_id"] = outcome.SourceWallId,
                        ["code"] = again?.Code ?? "expectation_missing",
                        ["message"] = again?.Message ??
                            "no expectation was recorded for this wall, so the committed document could not be " +
                            "held against anything."
                    });

                if (block.Value<bool?>("verified") == true && again != null && again.Passed) agreed++;
                verification.Add(block);
            }

            // An unexpected warning is not a footnote. It cannot roll one wall back - it
            // arrives at the OUTER commit, after each SubTransaction already committed -
            // but it can and does stop this run calling itself verified.
            // How many of the converted walls actually had their cuts probed. A wall with
            // no insert has nothing to cut, so its layers are not evidence about holes -
            // and the reply must not read as though they were.
            int wallsWithCutProof = applied.Count(o =>
                o.PostCommitVerification != null &&
                (o.PostCommitVerification["cut_coverage"]?["probed"]?.Value<bool>() ?? false));

            List<string> unexpectedWarnings = warnings.Unexpected.Distinct().ToList();
            bool allVerified = agreed == applied.Count && rolledBack.Count == 0 &&
                               unexpectedWarnings.Count == 0 && postCommitFailures.Count == 0;

            var reply = new JObject
            {
                ["tool"] = Name,
                ["schema_version"] = WallSplitCodes.SchemaVersion,
                ["dry_run"] = false,
                ["transaction_status"] = ApplicationOutcome.Committed,
                ["tolerance_mm"] = WallLayerRules.ToleranceMm,
                ["scope"] = scope,
                ["policies"] = Policies(options),
                ["walls_converted"] = applied.Count,
                ["walls_rolled_back"] = rolledBack.Count,
                ["walls_produced"] = applied.Sum(o => o.WallsProduced),
                ["originals_deleted"] = 0,
                ["walls"] = new JArray(outcomes.Select(o => o.ToJson())),
                ["already_converted"] = new JArray(alreadyConverted.Select(Converted)),
                ["rejected"] = new JArray(rejected.Select(Refusal)),
                ["verification"] = verification,
                ["post_commit_failures"] = postCommitFailures,
                ["post_commit_limitation"] =
                    "The full verifier runs twice. Inside each wall's SubTransaction a failure ROLLS THAT WALL " +
                    "BACK. After the outer commit the transaction is closed, so a failure there is reported and " +
                    "takes all_verified down but cannot undo anything - inspect the named walls.",
                ["unexpected_warnings"] = new JArray(unexpectedWarnings),
                ["unexpected_warning_code"] = unexpectedWarnings.Count > 0
                    ? (JToken)WallSplitCodes.VerifyUnexpectedWarning : JValue.CreateNull(),
                ["all_verified"] = allVerified,
                ["walls_with_cut_proof"] = wallsWithCutProof,
                ["verification_note"] = allVerified
                    ? "Every wall below was re-read from the committed model: each layer's position was MEASURED " +
                      "against its planned offset, each resulting type was re-read and confirmed single-layer and " +
                      "correctly named, every insert was checked by ElementId, UniqueId, host, placement, flips, " +
                      "level, phase, subcomponents and parameters. " +
                      // THE CUT CLAUSE IS CONDITIONAL, because the cut probe only runs on a
                      // wall that carries something. This sentence used to assert the
                      // ray-cast unconditionally, so an insert-free wall was described as
                      // having had its layers ray-cast when not one ray was fired.
                      (wallsWithCutProof == applied.Count
                          ? "Every one of them carried an insert, and each secondary layer was ray-cast to prove " +
                            "the opening passes through it. "
                          : wallsWithCutProof == 0
                          ? "NO cut was probed: not one of these walls carries an insert, opening or embedded wall, " +
                            "so nothing here is evidence about holes. Read cut_probed on each layer. "
                          : wallsWithCutProof + " of " + applied.Count + " carried an insert and had every secondary " +
                            "layer ray-cast; on the rest NO cut was probed and none is claimed. Read cut_probed on " +
                            "each layer. ") +
                      "originals_deleted is 0 BY DESIGN - the original wall is the " +
                      "carrier."
                    : postCommitFailures.Count > 0
                    ? "The walls below passed verification inside their own SubTransaction, but re-running the " +
                      "SAME verifier on the COMMITTED document found " + postCommitFailures.Count + " that no " +
                      "longer hold. Nothing can be rolled back at this point - the transaction is closed. Read " +
                      "'post_commit_failures' and inspect those walls before building on this."
                    : unexpectedWarnings.Count > 0 && rolledBack.Count == 0
                    ? "The walls below were verified against the model, BUT Revit raised " +
                      unexpectedWarnings.Count + " warning(s) this operation does not expect. They are listed " +
                      "under 'unexpected_warnings' and they are why all_verified is false: only the walls-overlap " +
                      "warning is produced by construction here, so anything else is the model telling you " +
                      "something. Look before you build on this."
                    : "AT LEAST ONE WALL DID NOT CONVERT. Every one of those was rolled back ALONE and is exactly " +
                      "as it was; the ones reported as converted were verified against the model. Read 'code' and " +
                      "'message' on each entry under 'walls' before running anything that builds on this."
            };

            ApplicationOutcome.StampApplied(reply, ApplicationOutcome.Committed,
                                            eligible.Count, applied.Count, agreed,
                                            rejected.Count + alreadyConverted.Count,
                                            rolledBack.Count, 0);
            DocumentGate.StampConfirmation(reply, gate, Name, planHash, false);
            return CommandResult.Ok(reply);
        }

        // ---- options ---------------------------------------------------------------

        private static bool ReadOptions(JObject request, GateResult gate, out WallSplitOptions options, out string error)
        {
            options = new WallSplitOptions
            {
                OriginGroupParam = request.Value<string>("origin_group_param"),
                DocumentKey = gate.Fingerprint
            };
            error = null;

            string carrier = request.Value<string>("core_carrier_policy");
            if (!string.IsNullOrWhiteSpace(carrier))
            {
                if (carrier != "structural_in_core_then_thickest")
                {
                    error = "core_carrier_policy must be 'structural_in_core_then_thickest'. It is an argument " +
                            "rather than an assumption so that a future policy is a contract change, not a " +
                            "silent behaviour change.";
                    return false;
                }
                options.CoreCarrierPolicy = carrier;
            }

            string parameters = request.Value<string>("parameter_copy_policy");
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                if (parameters != "safe_compatible")
                {
                    error = "parameter_copy_policy must be 'safe_compatible'.";
                    return false;
                }
                options.ParameterCopyPolicy = parameters;
            }

            string failure = request.Value<string>("failure_policy");
            if (!string.IsNullOrWhiteSpace(failure))
            {
                if (failure != "rollback_wall")
                {
                    error = "failure_policy must be 'rollback_wall'. There is deliberately no mode that accepts " +
                            "the loss of a hosted object: a wall that cannot be converted intact is rolled back.";
                    return false;
                }
                options.FailurePolicy = failure;
            }

            options.AllowArcWalls = request.Value<bool?>("allow_arc_walls") ?? true;
            return true;
        }

        private static JObject Policies(WallSplitOptions options) => new JObject
        {
            ["core_carrier_policy"] = options.CoreCarrierPolicy,
            ["parameter_copy_policy"] = options.ParameterCopyPolicy,
            ["failure_policy"] = options.FailurePolicy,
            ["allow_arc_walls"] = options.AllowArcWalls,
            ["origin_group_param"] = options.OriginGroupParam
        };

        // ---- scope -----------------------------------------------------------------

        /// <summary>
        /// element_ids first and exactly; then view_id; then the whole model. An id that
        /// resolves to nothing and an id that resolves to something else are DIFFERENT
        /// answers, and neither is silently dropped.
        ///
        /// A PRESENT BUT EMPTY element_ids never reaches here: Execute refuses it, because
        /// falling through to the whole model would read a caller's empty selection as a
        /// request to convert the entire document.
        /// </summary>
        private static List<Wall> ResolveScope(Document doc, JObject request, JArray missing, JArray wrongType)
        {
            var walls = new List<Wall>();
            var seen = new HashSet<long>();

            JArray ids = request["element_ids"] as JArray;
            if (ids != null && ids.Count > 0)
            {
                foreach (JToken token in ids)
                {
                    long raw;
                    try { raw = token.Value<long>(); } catch { wrongType.Add(token); continue; }
                    if (!Rid.CanRepresent(raw)) { missing.Add(raw); continue; }

                    Element element = doc.GetElement(Rid.Make(raw));
                    if (element == null) { missing.Add(raw); continue; }
                    if (!(element is Wall wall)) { wrongType.Add(raw); continue; }
                    if (seen.Add(raw)) walls.Add(wall);
                }
                return walls;
            }

            long? viewId = request.Value<long?>("view_id");
            FilteredElementCollector collector = viewId.HasValue && Rid.CanRepresent(viewId.Value)
                ? new FilteredElementCollector(doc, Rid.Make(viewId.Value))
                : new FilteredElementCollector(doc).WhereElementIsNotElementType();

            foreach (Wall wall in collector.OfClass(typeof(Wall)).Cast<Wall>())
                if (seen.Add(Rid.Value(wall.Id))) walls.Add(wall);

            return walls;
        }

        // ---- reporting -------------------------------------------------------------

        private static JObject Preflight(WallSplitSubject subject)
        {
            WallSplitPlan plan = subject.Plan;
            return new JObject
            {
                ["wall_id"] = subject.ElementId,
                ["wall_unique_id"] = subject.UniqueId,
                ["wall_type_name"] = subject.Assembly.WallTypeName,
                ["geometry_class"] = subject.CurveClass,
                ["flipped"] = subject.Flipped,
                ["exterior_normal_source"] = subject.NormalSource,
                ["exterior_normal_corroborated"] = subject.NormalCorroborated,
                ["exterior_normal_agreement"] = double.IsNaN(subject.NormalAgreement)
                    ? (JToken)JValue.CreateNull() : Math.Round(subject.NormalAgreement, 4),
                ["exterior_normal_note"] =
                    "measured off the wall's exterior shell face and CORROBORATED against Wall.Orientation. The " +
                    "two are checked against each other because nothing downstream can: the layers are placed " +
                    "along this vector and measured along the same one, so a wrong answer would agree with itself.",
                ["original_location_line"] = plan.OriginalLocationLine,
                ["core_first_layer_index"] = plan.CoreFirstLayerIndex,
                ["core_last_layer_index"] = plan.CoreLastLayerIndex,
                ["core_carrier_layer_index"] = plan.CoreCarrierLayerIndex,
                ["core_carrier_selection_reason"] = plan.CoreCarrierSelectionReason,
                ["original_core_center_offset_mm"] =
                    Math.Round(WallLayerRules.FeetToMm(plan.OriginalCoreCenterOffsetFeet), 3),
                ["total_width_mm"] = Math.Round(WallLayerRules.FeetToMm(plan.TotalWidthFeet), 3),
                ["reported_location_offset_mm"] = subject.ReportedLocationOffsetRead
                    ? (JToken)Math.Round(WallLayerRules.FeetToMm(subject.ReportedLocationOffsetFeet), 3)
                    : JValue.CreateNull(),
                ["reported_location_offset_note"] =
                    "CompoundStructure.GetOffsetForLocationLine, recorded as a CONTRAST only. The layer offsets " +
                    "are computed from the layer widths and the location line, so no API sign convention is " +
                    "trusted for them.",
                ["would_produce_walls"] = plan.WouldProduceWalls,
                ["layer_plan"] = new JArray(plan.Layers.Select(l => new JObject
                {
                    ["layer_index"] = l.LayerIndex,
                    ["layer_number"] = l.LayerNumber,
                    ["material_name"] = l.MaterialName,
                    ["role"] = l.Role,
                    ["is_core"] = l.IsCore,
                    ["is_core_carrier"] = l.IsCoreCarrier,
                    ["width_mm"] = Math.Round(WallLayerRules.FeetToMm(l.WidthFeet), 3),
                    ["expected_offset_mm"] = Math.Round(l.ExpectedOffsetMm, 3),
                    ["materialised"] = l.Materialised,
                    ["not_materialised_reason"] = l.NotMaterialisedReason,
                    ["source_wall_type_name"] = l.SourceWallTypeName,
                    ["planned_type_name"] = l.ExpectedTypeName,
                    ["variant_type_name_if_taken"] = l.VariantTypeName,
                    ["type_fingerprint"] = l.TypeFingerprint
                })),
                ["dependency_ledger"] = new JArray(subject.Dependencies.Select(d => d.ToJson())),

                // Four separate numbers, because they answer four different questions. One
                // of them used to be "objects requiring reconstruction" and was fed the
                // count of secondary WALLS, which is not a number of dependent objects at
                // all - a wall with nine doors and a wall with none reported the same.
                ["secondary_walls_to_create"] = plan.WouldProduceWalls - 1,
                ["dependencies_preserved_by_identity"] =
                    subject.Dependencies.Count(d => d.Disposition == DependencyDisposition.PreservedByIdentity),
                ["dependencies_requiring_reconstruction"] =
                    subject.Dependencies.Count(d => d.Disposition == DependencyDisposition.ReconstructableAndVerified ||
                                                    d.Disposition == DependencyDisposition.ReferenceReboundAndVerified),
                ["dependencies_blocking"] =
                    subject.Dependencies.Count(d => d.Disposition == DependencyDisposition.UnsupportedBlocking),
                ["dependencies_not_applicable"] =
                    subject.Dependencies.Count(d => d.Disposition == DependencyDisposition.NotApplicable),
                ["dependency_kinds_with_verifier"] = new JArray(DependencyKinds.WithVerifier),
                ["joins"] = subject.Joins.ToJson(),
                ["plan_fingerprint"] = subject.PlanFingerprint,
                ["checks_that_will_run"] = new JArray(
                    "carrier keeps its ElementId and UniqueId",
                    "every resulting wall is single-layer and carries its expected type NAME, re-read",
                    "every layer's position measured against its planned offset, tolerance " +
                        WallLayerRules.ToleranceMm + " mm",
                    "every insert: id, UniqueId, host, symbol, placement, rotation, flips, mirrored, level, " +
                        "phase created and demolished, workset, design option, pinned, bounding box, " +
                        "subcomponents by id AND symbol, and every parameter by stable key",
                    "every opening, sweep, reveal, embedded wall, dimension and tag re-read by its own verifier",
                    "every secondary layer ray-cast at FIVE points per insert to prove the opening passes through - ONLY on a wall that carries an insert; on a wall that carries none, no ray is cast and cut_verified is null rather than true",
                    "each secondary layer joined to the carrier, and each of the carrier's ORIGINAL joins restored",
                    "the provenance stamp written and read straight back, on the carrier and on every sibling",
                    "the whole verifier run again AFTER the outer commit, on the document everybody else opens")
            };
        }

        /// <summary>
        /// A wall that came out of a previous run. It is NOT a refusal: it is the tool
        /// answering "you already did this, here is the state of the whole set", and no
        /// transaction is opened for it either way.
        /// </summary>
        private static JObject Converted(WallSplitSubject subject) => new JObject
        {
            ["wall_id"] = subject.ElementId,
            ["wall_unique_id"] = subject.UniqueId,
            ["state"] = subject.ProvenanceState,
            ["selected_secondary_sibling"] = subject.SelectedSecondarySibling,
            ["core_carrier_id"] = subject.CarrierElementId == 0
                ? (JToken)JValue.CreateNull() : subject.CarrierElementId,
            ["message"] = subject.Rejection?.Message,
            ["sibling_set"] = subject.ProvenanceReport,
            ["changed"] = false,
            ["transaction_opened"] = false
        };

        private static JObject Refusal(WallSplitSubject subject) => new JObject
        {
            ["wall_id"] = subject.ElementId,
            ["wall_unique_id"] = subject.UniqueId,
            ["wall_type_name"] = subject.Assembly?.WallTypeName,
            ["geometry_class"] = subject.CurveClass,
            ["reason_code"] = subject.Rejection?.Code,
            ["reason"] = subject.Rejection?.Message,
            ["changed"] = false
        };

        private static ResolvedPlan BuildResolvedPlan(GateResult gate, UIApplication app,
                                                      List<WallSplitSubject> eligible)
        {
            string version;
            try { version = app?.Application?.VersionNumber; } catch { version = null; }

            var plan = new ResolvedPlan
            {
                Command = "horizun_split_multilayer_walls",
                DocumentKey = gate.Fingerprint,
                RevitVersion = version,
                DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ContextFingerprint = WallSplitCodes.SchemaVersion
            };

            // PER-ELEMENT binding. The previous token bound the recipe's sha plus the
            // intended counts, which accepted a different set of walls whose totals
            // happened to match, and turned an unknown count into -1 that matched every
            // other unknown.
            foreach (WallSplitSubject subject in eligible)
            {
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = subject.UniqueId,
                    Category = "Walls",
                    TypeName = subject.Assembly.WallTypeName,
                    Action = PlannedAction.Modify,
                    GeometryFingerprint = subject.PlanFingerprint,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "location_line", subject.Plan.OriginalLocationLine ?? "" },
                        { "carrier_layer_index", subject.Plan.CoreCarrierLayerIndex.ToString() },
                        { "would_produce_walls", subject.Plan.WouldProduceWalls.ToString() }
                    }
                });
            }

            return plan;
        }
    }

    /// <summary>
    /// Layer walls overlap the carrier BY CONSTRUCTION, so Revit's walls-overlap warning
    /// is expected rather than informative and is dismissed.
    ///
    /// It is matched by FailureDefinitionId, never by the words in its description. The
    /// previous implementation tested `"overlap" in text and "wall" in text`, which in a
    /// Spanish, French or German Revit never matches - so the warning was not dismissed,
    /// became a modal, and held Revit's UI thread until the caller timed out. It also
    /// deleted any OTHER warning whose text happened to contain both words.
    ///
    /// EVERY OTHER WARNING IS KEPT AND COUNTED. Revit hands failures to a preprocessor on
    /// the OUTER transaction, at its commit - after each wall's SubTransaction has already
    /// committed - so an unexpected warning cannot roll a single wall back. What it can
    /// do, and does, is travel back with the reply and take `all_verified` down with it,
    /// rather than being swallowed on the way out.
    /// </summary>
    public sealed class WallOverlapPreprocessor : IFailuresPreprocessor
    {
        /// <summary>
        /// The only warning this operation produces by construction. Deliberately short:
        /// a join that Revit says it cannot keep is EVIDENCE, not noise, and suppressing
        /// it would hide the exact thing the cut verification exists to catch.
        ///
        /// WHAT IS DELIBERATELY *NOT* HERE, and why it will stay out.
        ///
        /// Converting a 7-layer wall whose carrier is layer 05 leaves two STANDING Revit
        /// warnings - "Highlighted elements are joined but do not intersect", between the
        /// carrier and layers 01 and 02, which are separated from it by the layers in
        /// between. Those joins are made by this operation, in WallSplitExecutor step 5.
        ///
        /// Adding those failure ids here was tried and REVERTED. It made all_verified go
        /// true again, and it fixed nothing: the join between two walls that do not touch
        /// is still there and is still geometrically meaningless. Silencing the complaint
        /// removes the only evidence that the construction is wrong.
        ///
        /// It is worse than merely useless, because of WHAT WAS NOT MEASURED. The wall
        /// that produced those warnings had no door, no window and no opening, so nothing
        /// in that run showed a cut being transmitted to anything. The stated reason for
        /// the join is that the carrier's openings must cut through each layer. With the
        /// warning suppressed, a wall with no inserts passes every check - and an empty
        /// canary passing would have been read as proof that the joins work.
        ///
        /// So the rule is: this warning stays UNEXPECTED until the construction stops
        /// producing it. The fix belongs in the executor's topology, not in this set.
        private static readonly HashSet<FailureDefinitionId> Expected = new HashSet<FailureDefinitionId>
        {
            BuiltInFailures.OverlapFailures.WallsOverlap
        };

        private readonly List<string> _unexpected = new List<string>();

        /// <summary>Warnings Revit raised that this operation does not expect, in order.</summary>
        public IReadOnlyList<string> Unexpected => _unexpected;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() != FailureSeverity.Warning) continue;

                if (Expected.Contains(failure.GetFailureDefinitionId()))
                {
                    accessor.DeleteWarning(failure);
                    continue;
                }

                try { _unexpected.Add(failure.GetDescriptionText()); }
                catch { _unexpected.Add("<a warning whose text could not be read>"); }
            }
            return FailureProcessingResult.Continue;
        }
    }
}
