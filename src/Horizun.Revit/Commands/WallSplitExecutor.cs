// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// TURNING ONE COMPOUND WALL INTO ITS LAYERS, atomically, and PROVING it.
//
// The strategy in one sentence: the original wall is never deleted. It becomes
// the layer of the core that carries the doors, and only the OTHER layers are
// created. Everything that follows from that is the point:
//
//   * the ElementId and UniqueId of the wall survive, so tags, dimensions,
//     schedules keyed by id and every external federation keep pointing at it;
//   * the doors and windows keep THEIR ids too, because they are never
//     unhosted - the thing they are hosted by simply becomes thinner and moves
//     to where its layer always was;
//   * openings, sweeps, reveals, embedded curtain walls and hosted rebar need no
//     reconstruction at all, because the element they hang off still exists.
//
// The list of things that must be rebuilt shrinks to: the walls for the other
// layers, and their cuts.
//
// TWO DISCIPLINES RUN THROUGH THIS FILE.
//
// MEASURE, DO NOT PREDICT. Where a sign convention could be wrong - which way
// the exterior normal points, whether changing the location line moves the wall
// or moves the curve - nothing is assumed. The wall is moved, regenerated,
// RE-READ, and corrected against what Revit actually did. A wrong assumption
// therefore produces a REFUSAL, never a committed wrong building.
//
// ONE WALL IS ONE ATOM. Each wall runs in its own SubTransaction and every
// invariant is re-read from the model before it is committed. A wall either
// comes out fully converted and verified, or it comes out exactly as it went in.
// There is no path on which layers exist beside an intact original, and no path
// on which a door is lost and the reply says all_verified.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>The caller's policies, already validated against their closed sets.</summary>
    public sealed class WallSplitOptions
    {
        public string CoreCarrierPolicy = "structural_in_core_then_thickest";
        public string ParameterCopyPolicy = "safe_compatible";
        public string FailurePolicy = "rollback_wall";
        public bool AllowArcWalls = true;
        public string OriginGroupParam;
        public string DocumentKey;

        /// <summary>
        /// The document-wide reads the dry run used, carried through so the apply-time
        /// revalidation asks the SAME question. Omitting them made the stale check compare
        /// a census WITH annotations against a census WITHOUT them, so any wall carrying a
        /// dimension or a tag refused as stale_plan and could never be converted at all.
        /// </summary>
        public WallReverseCensus Reverse;
        public WallProvenanceIndex Provenance;
    }

    /// <summary>What became of one layer. Every field the naming rule requires to be reported.</summary>
    public sealed class LayerOutcome
    {
        public int LayerIndex;
        public int LayerNumber;
        public string LayerNumberText;
        public string SourceWallTypeName;
        public string MaterialName;
        /// <summary>What the PLAN named, before any variant was chosen.</summary>
        public string PlannedTypeName;

        /// <summary>What the apply resolved to - the plan's name, or its deterministic variant.</summary>
        public string ExpectedTypeName;

        public string ActualTypeName;
        public bool TypeReused;
        public bool TypeCreated;
        public string TypeFingerprint;
        public long ResultingWallId;
        public string ResultingWallUniqueId;
        public bool IsCoreCarrier;
        public bool NamingVerified;
        public bool Materialised;
        public string NotMaterialisedReason;
        public string Role;
        public double WidthMm;
        public double ExpectedOffsetMm;
        public double ObservedOffsetMm;
        public double DeviationMm;
        public bool GeometryVerified;
        public bool SingleLayerVerified;
        public bool JoinVerified;

        /// <summary>
        /// null means NOT PROBED - see WallLayerRules.CutClaim. It is nullable so that
        /// "nothing was measured" cannot be published as "it passed".
        /// </summary>
        public bool? CutVerified;
        public bool CutProbed;
        public string CutNotProbedReason;

        /// <summary>
        /// Set when the wall rolled back. Everything this row measured was measured
        /// inside a SubTransaction that has since been undone, so every verification
        /// flag is withdrawn rather than published - a claim about a wall that no longer
        /// exists cannot be checked by anybody and reads as though it could.
        /// </summary>
        public bool ClaimsWithdrawn;

        public JObject ToJson() => new JObject
        {
            ["layer_index"] = LayerIndex,
            ["layer_number"] = LayerNumber,
            ["source_wall_type_name"] = SourceWallTypeName,
            ["material_name"] = MaterialName,
            ["planned_type_name"] = PlannedTypeName,
            ["expected_type_name"] = ExpectedTypeName,
            ["type_name_is_variant"] = PlannedTypeName != null &&
                                       !string.Equals(PlannedTypeName, ExpectedTypeName, StringComparison.Ordinal),
            ["actual_type_name"] = ActualTypeName,
            ["type_reused"] = TypeReused,
            ["type_created"] = TypeCreated,
            ["type_fingerprint"] = TypeFingerprint,
            ["resulting_wall_id"] = ResultingWallId == 0 ? (JToken)JValue.CreateNull() : ResultingWallId,
            ["resulting_wall_unique_id"] = ResultingWallUniqueId,
            ["is_core_carrier"] = IsCoreCarrier,
            ["naming_verified"] = ClaimsWithdrawn ? (JToken)JValue.CreateNull() : NamingVerified,
            ["materialised"] = Materialised,
            ["not_materialised_reason"] = NotMaterialisedReason,
            ["role"] = Role,
            ["width_mm"] = Math.Round(WidthMm, 3),
            ["expected_offset_mm"] = Math.Round(ExpectedOffsetMm, 3),
            ["observed_offset_mm"] = Materialised ? (JToken)Math.Round(ObservedOffsetMm, 3) : JValue.CreateNull(),
            ["deviation_mm"] = Materialised ? (JToken)Math.Round(DeviationMm, 3) : JValue.CreateNull(),
            ["geometry_verified"] = ClaimsWithdrawn ? (JToken)JValue.CreateNull() : GeometryVerified,
            ["single_layer_verified"] = ClaimsWithdrawn ? (JToken)JValue.CreateNull() : SingleLayerVerified,
            ["join_verified"] = ClaimsWithdrawn ? (JToken)JValue.CreateNull() : JoinVerified,
            ["cut_probed"] = !ClaimsWithdrawn && CutProbed,
            ["cut_verified"] = (ClaimsWithdrawn || !CutVerified.HasValue)
                ? (JToken)JValue.CreateNull() : CutVerified.Value,
            ["cut_not_probed_reason"] = ClaimsWithdrawn
                ? "this wall was rolled back: every check below it was made inside a SubTransaction that no " +
                  "longer exists, so nothing here is a claim about the model"
                : (CutNotProbedReason == null ? (JToken)JValue.CreateNull() : CutNotProbedReason),
            ["claims_withdrawn"] = ClaimsWithdrawn
        };
    }

    /// <summary>What became of one wall.</summary>
    public sealed class WallSplitOutcome
    {
        public long SourceWallId;
        public string SourceWallUniqueId;
        public bool Applied;
        public string Code;
        public string Message;
        public string RollbackStatus;

        /// <summary>
        /// True ONLY when Revit answered RolledBack. Null-ish false covers Pending, Error
        /// and a rollback that threw - none of which mean the wall is as it was.
        /// </summary>
        public bool RollbackConfirmed;

        public int WallsProduced;
        public int WallsExpected;
        public List<LayerOutcome> Layers = new List<LayerOutcome>();
        public JArray Dependencies = new JArray();

        /// <summary>The full verifier report from inside the SubTransaction, where it could still roll back.</summary>
        public JObject PreCommitVerification;

        /// <summary>The same verifier, re-run on the committed document. Reports; cannot undo.</summary>
        public JObject PostCommitVerification;

        /// <summary>Kept so the command can re-run the verifier after the outer commit.</summary>
        public WallSplitExpectation Expectation;
        public string CoreCarrierSelectionReason;
        public int CoreFirstLayerIndex = -1;
        public int CoreLastLayerIndex = -1;
        public int CoreCarrierLayerIndex = -1;
        public string OriginalLocationLine;
        public double OriginalCoreCenterOffsetMm;
        public string NormalSource;
        public JArray ParameterReport = new JArray();
        public JArray GeneratedCutIds = new JArray();

        /// <summary>The join chain as it was RE-READ from the model, one key per edge.</summary>
        public JArray JoinGraph = new JArray();

        public JObject ToJson() => new JObject
        {
            ["source_wall_id"] = SourceWallId,
            ["source_wall_unique_id"] = SourceWallUniqueId,
            ["applied"] = Applied,
            ["code"] = Code,
            ["message"] = Message,
            ["rollback_status"] = RollbackStatus,
            ["rollback_confirmed"] = Applied ? (JToken)JValue.CreateNull() : RollbackConfirmed,
            ["walls_expected"] = WallsExpected,
            ["walls_produced"] = WallsProduced,
            ["core_first_layer_index"] = CoreFirstLayerIndex,
            ["core_last_layer_index"] = CoreLastLayerIndex,
            ["core_carrier_layer_index"] = CoreCarrierLayerIndex,
            ["core_carrier_selection_reason"] = CoreCarrierSelectionReason,
            ["original_location_line"] = OriginalLocationLine,
            ["original_core_center_offset_mm"] = Math.Round(OriginalCoreCenterOffsetMm, 3),
            ["exterior_normal_source"] = NormalSource,
            ["tolerance_mm"] = WallLayerRules.ToleranceMm,
            ["join_graph"] = JoinGraph,
            ["layers"] = new JArray(Layers.Select(l => l.ToJson())),
            ["dependency_ledger"] = Dependencies,
            ["verification_before_subtransaction_commit"] = PreCommitVerification,
            ["verification_after_outer_commit"] = PostCommitVerification,
            ["parameters"] = ParameterReport,
            ["generated_cut_ids"] = GeneratedCutIds
        };
    }

    public static class WallSplitExecutor
    {
        /// <summary>
        /// Convert ONE wall, in its own SubTransaction, and either commit it verified or
        /// leave it exactly as it was. Never throws for a wall-level problem: the failure
        /// is the outcome, so one bad wall does not take a batch down and does not leave
        /// the batch half-written either.
        /// </summary>
        public static WallSplitOutcome Execute(Document doc, WallSplitSubject approved, WallSplitOptions options)
        {
            var outcome = new WallSplitOutcome
            {
                SourceWallId = approved.ElementId,
                SourceWallUniqueId = approved.UniqueId,
                WallsExpected = approved.Plan.WouldProduceWalls,
                CoreCarrierSelectionReason = approved.Plan.CoreCarrierSelectionReason,
                CoreFirstLayerIndex = approved.Plan.CoreFirstLayerIndex,
                CoreLastLayerIndex = approved.Plan.CoreLastLayerIndex,
                CoreCarrierLayerIndex = approved.Plan.CoreCarrierLayerIndex,
                OriginalLocationLine = approved.Plan.OriginalLocationLine,
                OriginalCoreCenterOffsetMm = WallLayerRules.FeetToMm(approved.Plan.OriginalCoreCenterOffsetFeet),
                NormalSource = approved.NormalSource
            };

            var sub = new SubTransaction(doc);
            bool started = false;
            try
            {
                sub.Start();
                started = true;

                Fail failure = Convert(doc, approved, options, outcome);
                if (failure == null)
                {
                    Guard.Commit(sub, "split wall " + approved.ElementId);
                    outcome.Applied = true;
                    outcome.Code = null;
                    outcome.Message = "converted and verified: " + outcome.WallsProduced + " single-layer walls, " +
                                      "the original kept as the core carrier.";
                    return outcome;
                }

                outcome.Applied = false;
                outcome.Code = failure.Code;
                outcome.Message = failure.Message;

                // WITHDRAW THE CLAIMS. FillLayerOutcomes already ran inside Convert, so
                // these rows exist and carry whatever the verifier measured before the
                // failure. The SubTransaction is about to be rolled back and everything
                // they describe will stop existing; publishing them as verified would be
                // describing a model nobody can open.
                foreach (LayerOutcome layer in outcome.Layers) layer.ClaimsWithdrawn = true;

                // A rollback REPORTS a status; it does not promise one. Revit can answer
                // Pending or Error, and "the wall is exactly as it was" is only true when it
                // answered RolledBack. Anything else keeps its uncertainty here rather than
                // being smoothed into a clean model nobody saw.
                Guard.RollbackResult rollback = Guard.RollBack(sub);
                outcome.RollbackStatus = rollback.StatusName;
                outcome.RollbackConfirmed = rollback.Confirmed;
                if (!rollback.Confirmed)
                    outcome.Message += " AND THE ROLLBACK DID NOT CONFIRM - Revit answered " +
                                       rollback.StatusName + " rather than RolledBack, so this wall may hold a " +
                                       "partial conversion. Inspect element " + approved.ElementId +
                                       " before running anything that builds on this.";
                started = false;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Applied = false;
                outcome.Code = outcome.Code ?? WallSplitCodes.CarrierConversionFailed;
                outcome.Message = "the conversion threw and this wall was rolled back whole: " + ex.Message;
                if (started)
                {
                    try
                    {
                        Guard.RollbackResult rollback = Guard.RollBack(sub);
                        outcome.RollbackStatus = rollback.StatusName;
                        outcome.RollbackConfirmed = rollback.Confirmed;
                        if (!rollback.Confirmed)
                            outcome.Message += " AND THE ROLLBACK DID NOT CONFIRM (" + rollback.StatusName +
                                               ") - this wall may hold a partial conversion.";
                    }
                    catch (Exception rollbackFailure)
                    {
                        // A rollback that itself fails is the one state worth shouting about:
                        // the model may now hold a partial conversion. Say so exactly.
                        outcome.RollbackStatus = "ROLLBACK_FAILED: " + rollbackFailure.Message;
                        outcome.Message += " AND THE ROLLBACK ITSELF FAILED - inspect wall " +
                                           approved.ElementId + " before running anything that builds on this.";
                    }
                }
                return outcome;
            }
        }

        private sealed class Fail
        {
            public readonly string Code;
            public readonly string Message;
            public Fail(string code, string message) { Code = code; Message = message; }
        }

        // ---- the conversion -------------------------------------------------------

        private static Fail Convert(Document doc, WallSplitSubject approved, WallSplitOptions options,
                                    WallSplitOutcome outcome)
        {
            Wall carrier = approved.Wall;

            // ---- 0. the plan must still describe this wall --------------------------
            // The SAME inputs the approved read used. A revalidation that reads less than
            // the read it is compared against does not detect drift - it manufactures it.
            WallSplitSubject now = WallSplitFacts.Read(doc, carrier, options.DocumentKey,
                                                       options.CoreCarrierPolicy, options.AllowArcWalls,
                                                       options.Reverse, options.Provenance);
            if (!now.Eligible)
                return new Fail(now.Rejection?.Code ?? WallSplitCodes.StalePlan,
                    "between the dry run and this apply the wall stopped being eligible: " +
                    (now.Rejection?.Message ?? "no reason given") + ". Nothing was written.");

            if (!string.Equals(now.PlanFingerprint, approved.PlanFingerprint, StringComparison.Ordinal))
                return new Fail(WallSplitCodes.StalePlan,
                    "this wall, its type, its compound structure, its position or its dependencies changed since " +
                    "the dry run that was approved. The approved plan no longer describes it, so nothing was " +
                    "written - run the dry run again.");

            // A wall that already carries provenance never gets here: WallSplitFacts.Read
            // reads the stamp FIRST and refuses it before a plan is even computed, which is
            // what makes already_split reachable at all (a converted carrier is single-layer,
            // so planning it would refuse it as single_layer and the stamp would never be
            // consulted). This assertion exists so that ordering cannot silently regress.
            if (WallSplitProvenance.ReadStamp(carrier).Present)
                return new Fail(WallSplitCodes.ExistingPlanConflict,
                    "this wall already carries a provenance stamp and should have been refused before any " +
                    "transaction was opened. Nothing was written.");

            WallSplitPlan plan = approved.Plan;
            XYZ normal = approved.ExteriorNormal;
            WallLayerPlan carrierLayer = plan.Layers.First(l => l.IsCoreCarrier);

            // ---- 1. the snapshot everything is verified against ---------------------
            Curve originalCurve = approved.LocationCurve;
            outcome.Dependencies = new JArray(approved.Dependencies.Select(d => d.ToJson()));

            // BUILT FROM THE APPROVED STATE, NOT THE RE-READ ONE.
            //
            // `now` exists for exactly one purpose: to prove, by fingerprint, that nothing
            // drifted since the plan was approved. Once that holds the two are equivalent -
            // but building what we VERIFY AGAINST out of the freshly-read state would mean
            // that if the fingerprint ever missed something, the conversion would be checked
            // against the changed model instead of the approved one, and agree with itself.
            var expectation = new WallSplitExpectation
            {
                CarrierId = approved.ElementId,
                CarrierUniqueId = approved.UniqueId,
                Plan = plan,
                OriginalCurve = originalCurve,
                Normal = normal,
                ArcSign = approved.ArcSign,
                Dependencies = approved.Dependencies.Where(d => d.Snapshot != null)
                                                    .Select(d => d.Snapshot).ToList(),
                Joins = approved.Joins,
                PlanFingerprint = approved.PlanFingerprint,
                SourceWallUniqueId = approved.UniqueId,
                OriginalWallTypeId = approved.Assembly.WallTypeUniqueId,
                CarrierOffsetFeet = carrierLayer.ExpectedOffsetFeet
            };

            foreach (WallLayerFacts layer in approved.Assembly.Layers)
            {
                expectation.SourceFunctions[layer.Index] = layer.Function ?? "";
                expectation.SourceMaterialUniqueIds[layer.Index] = layer.MaterialUniqueId;
            }

            bool wasPinned = carrier.Pinned;
            var parameterReport = new JArray();

            // ---- 2. resolve every type FIRST ---------------------------------------
            //
            // Before the carrier is touched. A type that cannot be built costs nothing
            // here and would cost a half-converted wall later.
            var types = new Dictionary<int, TypeResolution>();
            foreach (WallLayerPlan layer in plan.Layers.Where(l => l.Materialised))
            {
                TypeResolution resolved = WallSplitTypes.Resolve(doc, carrier.WallType, approved.Assembly, layer);
                if (resolved.Failure != null)
                    return new Fail(WallSplitCodes.TypeCreationFailed,
                        "layer " + layer.LayerNumberText + " (" + layer.MaterialName + "): " + resolved.Failure);
                types[layer.LayerIndex] = resolved;
                expectation.TypeNameByLayer[layer.LayerIndex] = resolved.Name;
            }

            // ---- 2b. EVERY curve, computed before anything is written ---------------
            //
            // Fail-early, and for a measured reason: these are derived from the ORIGINAL
            // location curve, and converting the carrier replaces that curve. Computing the
            // secondary layers' curves afterwards read a stale wrapper and produced null for
            // every one of them - after the carrier had already been converted. Rollback
            // caught it, but a wall that cannot be built should be refused before the first
            // write, not rolled back after it.
            var targetCurves = new Dictionary<int, Curve>();
            foreach (WallLayerPlan layer in plan.Layers.Where(l => l.Materialised))
            {
                Curve target = OffsetCurve(originalCurve, layer.ExpectedOffsetFeet, normal, approved.ArcSign);
                if (target == null)
                    return new Fail(WallSplitCodes.UnsupportedCurve,
                        "layer " + layer.LayerNumberText + "'s curve could not be built from a " +
                        approved.CurveClass + " at offset " + Math.Round(layer.ExpectedOffsetMm, 2) +
                        " mm. Nothing was written.");
                targetCurves[layer.LayerIndex] = target;
            }

            // ---- 3. the carrier becomes its layer -----------------------------------
            if (wasPinned)
            {
                try { carrier.Pinned = false; }
                catch (Exception ex)
                {
                    return new Fail(WallSplitCodes.CarrierConversionFailed,
                        "this wall is pinned and could not be unpinned (" + ex.Message + ").");
                }
            }

            Curve carrierTarget = targetCurves[carrierLayer.LayerIndex];

            try
            {
                carrier.ChangeTypeId(types[carrierLayer.LayerIndex].TypeId);
                Parameter locationLine = carrier.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (locationLine != null && !locationLine.IsReadOnly)
                    locationLine.Set((int)WallLocationLine.WallCenterline);
                doc.Regenerate();
            }
            catch (Exception ex)
            {
                return new Fail(WallSplitCodes.CarrierConversionFailed,
                    "the original wall could not be converted to its single-layer core type: " + ex.Message);
            }

            // MEASURE, THEN CORRECT. Whether setting the location line moved the wall or
            // moved the curve does not have to be known: the target position is asserted
            // and then re-read, twice at most.
            Fail placed = PlaceCarrier(doc, carrier, carrierTarget);
            if (placed != null) return placed;

            expectation.WallIdByLayer[carrierLayer.LayerIndex] = Rid.Value(carrier.Id);

            // ---- 4. the other layers ------------------------------------------------
            ElementId levelId = carrier.LevelId;
            double creationHeight = ReadCreationHeight(carrier);
            var created = new Dictionary<int, Wall>();

            foreach (WallLayerPlan layer in plan.Layers.Where(l => l.Materialised && !l.IsCoreCarrier))
            {
                Curve layerCurve = targetCurves[layer.LayerIndex];

                Wall made;
                try
                {
                    made = Wall.Create(doc, layerCurve, types[layer.LayerIndex].TypeId, levelId,
                                       creationHeight, 0.0, false, false);
                }
                catch (Exception ex)
                {
                    return new Fail(WallSplitCodes.CarrierConversionFailed,
                        "layer " + layer.LayerNumberText + " (" + layer.MaterialName + ") could not be created: " +
                        ex.Message + ". The whole wall was rolled back rather than left partly decomposed.");
                }

                created[layer.LayerIndex] = made;
                expectation.WallIdByLayer[layer.LayerIndex] = Rid.Value(made.Id);

                Parameter madeLocationLine = made.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (madeLocationLine != null && !madeLocationLine.IsReadOnly)
                    madeLocationLine.Set((int)WallLocationLine.WallCenterline);

                CopyConstraints(carrier, made);
                parameterReport.Add(CopyInstanceParameters(carrier, made, layer, options));
            }

            doc.Regenerate();

            // The layer walls must face the same way the original did. Measured, not
            // assumed: a wall created with flip=false is not guaranteed to agree with a
            // wall the user flipped, and the finishes ending up on the wrong side is a
            // mistake that looks plausible.
            foreach (KeyValuePair<int, Wall> pair in created)
            {
                XYZ made = MeasuredNormal(pair.Value);
                if (made != null && made.DotProduct(normal) < 0)
                {
                    try { pair.Value.Flip(); } catch { /* verified below either way */ }
                }
            }
            doc.Regenerate();

            // Placement can drift when Revit joins or flips; assert each layer's curve.
            foreach (KeyValuePair<int, Wall> pair in created)
            {
                WallLayerPlan layer = plan.Layers.First(l => l.LayerIndex == pair.Key);
                Fail settled = PlaceLayer(doc, pair.Value, targetCurves[layer.LayerIndex], layer);
                if (settled != null) return settled;
            }

            // ---- 5. cuts and joins --------------------------------------------------
            //
            // A CHAIN between neighbouring layers, not a star to the carrier.
            //
            // The join is what carries the carrier's openings through to the other layers
            // - measured, not assumed: on four identical seven-layer walls with a real
            // door, no joins at all left every secondary layer holding EXACTLY its own
            // thickness of material, while both a star and a chain cut all of them. The
            // cut is transitive along the chain.
            //
            // The star, though, joins the carrier to layers it does not touch. On that
            // wall it produced two joins across gaps of 94.5 mm and 19.5 mm, and those are
            // precisely the pairs Revit records "joined but do not intersect" about,
            // permanently, in the delivered model. The chain reaches every layer without
            // ever joining two walls that are apart, so the warning stops being produced
            // rather than being excused.
            //
            // These layers are still NOT joined to the carrier's own neighbours in the
            // model: inheriting the end joins would make each layer meet a neighbour whose
            // layers may not exist yet, and the result would depend on the batch order.
            // That was the reason for the original comment here and it still holds - it
            // was never a reason to join a wall to something it does not touch.
            // THE CARRIER IS ONE OF THE LAYERS, and `created` holds only the walls this
            // step made - the carrier is the ORIGINAL wall and was never added to it. The
            // first version of this chain looked layers up in `created` alone and refused
            // its own wall with "the chain needs layers 04 and 05 and one of them has no
            // wall". Measured live; the wall rolled back, confirmed.
            var wallsByLayer = new Dictionary<int, Wall>(created);
            wallsByLayer[approved.Plan.CoreCarrierLayerIndex] = carrier;

            var chainOrder = plan.Layers
                .Where(l => l.Materialised)
                .OrderBy(l => l.LayerIndex)
                .ToList();
            List<int[]> chain = WallLayerRules.ChainEdges(chainOrder.Select(l => l.LayerIndex).ToList());
            var expectedEdges = new HashSet<string>(StringComparer.Ordinal);

            foreach (int[] edge in chain)
            {
                WallLayerPlan a = plan.Layers.First(l => l.LayerIndex == edge[0]);
                WallLayerPlan b = plan.Layers.First(l => l.LayerIndex == edge[1]);

                // CONTACT FIRST. A join between walls that do not touch is the defect this
                // topology exists to remove, so it is refused rather than made.
                if (!WallLayerRules.LayersTouch(a.ExpectedOffsetFeet, a.WidthFeet,
                                                b.ExpectedOffsetFeet, b.WidthFeet))
                    return new Fail(WallSplitCodes.VerifyJoinDisjoint,
                        "layers " + a.LayerNumberText + " and " + b.LayerNumberText + " are consecutive in the " +
                        "chain but do not touch: their centres are " +
                        WallLayerRules.FeetToMm(Math.Abs(a.ExpectedOffsetFeet - b.ExpectedOffsetFeet)).ToString("F2") +
                        " mm apart and half their widths add to " +
                        WallLayerRules.FeetToMm((a.WidthFeet + b.WidthFeet) / 2.0).ToString("F2") +
                        " mm. Nothing was joined.");

                if (!wallsByLayer.TryGetValue(edge[0], out Wall wa) || !wallsByLayer.TryGetValue(edge[1], out Wall wb))
                    return new Fail(WallSplitCodes.VerifyJoinMissing,
                        "the chain needs layers " + a.LayerNumberText + " and " + b.LayerNumberText +
                        " and one of them has no wall.");

                try
                {
                    if (!JoinGeometryUtils.AreElementsJoined(doc, wa, wb))
                        JoinGeometryUtils.JoinGeometry(doc, wa, wb);
                    expectedEdges.Add(WallLayerRules.EdgeKey(Rid.Value(wa.Id), Rid.Value(wb.Id)));
                }
                catch (Exception ex)
                {
                    return new Fail(WallSplitCodes.VerifyJoinMissing,
                        "layers " + a.LayerNumberText + " and " + b.LayerNumberText + " could not be joined (" +
                        ex.Message + "), so the openings would not be cut through the chain.");
                }
            }

            doc.Regenerate();

            // A native rectangular Opening remains hosted by the carrier, but unlike
            // doors and windows its cut does not propagate through a joined wall chain
            // on Revit 2026. That was measured live: the identity verifier passed while
            // layer 01 still contained material. Keep the original Opening (and its id)
            // on the carrier, then create only the geometric cuts required by each
            // secondary layer. The ray probes below remain the authority: a successful
            // NewOpening call is not accepted as proof that the cut exists.
            Fail openingCuts = ReplicateRectangularOpenings(doc, approved.Dependencies, created.Values,
                                                            outcome.GeneratedCutIds);
            if (openingCuts != null) return openingCuts;
            doc.Regenerate();

            // ---- RE-READ THE GRAPH, and hold it to the chain exactly -----------------
            //
            // Not "did the calls succeed" - what the model actually holds now. Revit can
            // join walls on its own, and a graph that grew an edge nobody asked for is a
            // graph nobody has verified.
            var siblingIds = new HashSet<long>(wallsByLayer.Values.Select(w => Rid.Value(w.Id)));
            var seenEdges = new HashSet<string>(StringComparer.Ordinal);
            foreach (Wall w in wallsByLayer.Values)
            {
                long id = Rid.Value(w.Id);
                ICollection<ElementId> joinedTo;
                try { joinedTo = JoinGeometryUtils.GetJoinedElements(doc, w); }
                catch (Exception ex)
                {
                    return new Fail(WallSplitCodes.VerifyJoinMissing,
                        "the joins of layer wall " + id + " could not be re-read (" + ex.Message +
                        "), so the graph cannot be verified.");
                }

                foreach (ElementId otherId in joinedTo)
                {
                    long other = Rid.Value(otherId);
                    string key = WallLayerRules.EdgeKey(id, other);

                    if (!siblingIds.Contains(other))
                    {
                        // A join reaching outside the sibling set. The carrier legitimately
                        // keeps its ORIGINAL neighbours, restored below; anything else on a
                        // freshly created layer wall is not ours and was not asked for.
                        if (id == Rid.Value(carrier.Id)) continue;
                        return new Fail(WallSplitCodes.VerifyJoinUnexpected,
                            "layer wall " + id + " is joined to element " + other + ", which is not one of the " +
                            "walls this conversion produced. Nothing asked for that join.");
                    }

                    if (!expectedEdges.Contains(key))
                        return new Fail(WallSplitCodes.VerifyJoinUnexpected,
                            "walls " + id + " and " + other + " are joined, and the chain does not call for it.");

                    seenEdges.Add(key);
                }
            }

            foreach (string key in expectedEdges)
                if (!seenEdges.Contains(key))
                    return new Fail(WallSplitCodes.VerifyJoinMissing,
                        "the chain edge " + key + " was made and is not in the model when re-read.");

            outcome.JoinGraph = new JArray(expectedEdges.OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => (JToken)k));

            // And the carrier's ORIGINAL joins are restored. Moving a wall to its layer's
            // position can break a join to a neighbour it no longer touches, and the first
            // version of this file captured those joins and never used them - which is the
            // same silent loss it replaced.
            Fail joins = RestoreJoins(doc, carrier, approved.Joins);
            if (joins != null) return joins;

            doc.Regenerate();

            // ---- 6. provenance, BEFORE the verification that checks it ---------------
            string stamped = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var siblings = new List<string> { WallSplitFacts.SafeUniqueId(carrier) };
            siblings.AddRange(created.Values.Select(WallSplitFacts.SafeUniqueId));
            expectation.SiblingUniqueIds = siblings;

            // The record has to carry enough for a LATER call to prove the set is complete:
            // how many walls, which layer indices, and which role each index should have.
            // Without those, a sibling check can only count, and counting cannot see a role
            // that is valid but wrong or an index that is missing.
            var materialised = plan.Layers.Where(l => l.Materialised).OrderBy(l => l.LayerIndex).ToList();
            string expectedIndices = string.Join(";", materialised.Select(l => l.LayerIndex));
            string expectedRoles = string.Join(";", materialised.Select(l => l.LayerIndex + "=" + l.Role));
            int expectedWallCount = materialised.Count;

            string stampFailure = WallSplitProvenance.WriteVerified(
                carrier, approved.PlanFingerprint, now.UniqueId, now.Assembly.WallTypeUniqueId,
                carrierLayer.LayerIndex, LayerRole.CoreCarrier, siblings, stamped,
                expectedWallCount, expectedIndices, expectedRoles,
                carrierLayer.TypeFingerprint, types[carrierLayer.LayerIndex].Name);
            if (stampFailure != null)
                return new Fail(WallSplitCodes.ProvenanceVerificationFailed, stampFailure);

            foreach (KeyValuePair<int, Wall> pair in created)
            {
                WallLayerPlan layer = plan.Layers.First(l => l.LayerIndex == pair.Key);
                string failure = WallSplitProvenance.WriteVerified(
                    pair.Value, approved.PlanFingerprint, now.UniqueId, now.Assembly.WallTypeUniqueId,
                    layer.LayerIndex, layer.Role, siblings, stamped,
                    expectedWallCount, expectedIndices, expectedRoles,
                    layer.TypeFingerprint, types[layer.LayerIndex].Name);
                if (failure != null)
                    return new Fail(WallSplitCodes.ProvenanceVerificationFailed, failure);
            }

            doc.Regenerate();

            // ---- 7. VERIFY, from the model ------------------------------------------
            VerificationReport verdict = WallSplitVerifier.Run(doc, expectation,
                                                               VerificationPhase.BeforeSubTransactionCommit);
            outcome.PreCommitVerification = verdict.ToJson();
            outcome.Expectation = expectation;
            FillLayerOutcomes(outcome, plan, types, expectation, verdict);

            if (!verdict.Passed) return new Fail(verdict.Code, verdict.Message);

            // ---- 8. the pin ----------------------------------------------------------
            if (wasPinned)
            {
                try { carrier.Pinned = true; } catch { }
                foreach (Wall layerWall in created.Values) { try { layerWall.Pinned = true; } catch { } }
            }

            outcome.ParameterReport = parameterReport;
            outcome.WallsProduced = 1 + created.Count;
            return null;
        }

        /// <summary>
        /// Put back every join the carrier had before it moved, and its end-join flags.
        /// A join that will not go back is reported by the verifier as a refusal - it cannot
        /// be predicted in the preflight, because whether two walls still touch depends on
        /// where this one ended up, which is not known until it is moved.
        /// </summary>
        private static Fail RestoreJoins(Document doc, Wall carrier, WallJoinFacts before)
        {
            foreach (long id in before.GeometricJoinIds)
            {
                Element other = doc.GetElement(Rid.Make(id));
                if (other == null || !other.IsValidObject) continue;

                try
                {
                    if (!JoinGeometryUtils.AreElementsJoined(doc, carrier, other))
                        JoinGeometryUtils.JoinGeometry(doc, carrier, other);

                    // Which one cuts which is part of the relationship, not a detail: a
                    // restored join with the cut order swapped changes what the model looks
                    // like at the junction.
                    if (before.CutByOther.TryGetValue(id, out bool carrierWasCutting))
                    {
                        bool cuttingNow = JoinGeometryUtils.IsCuttingElementInJoin(doc, carrier, other);
                        if (cuttingNow != carrierWasCutting)
                            JoinGeometryUtils.SwitchJoinOrder(doc, carrier, other);
                    }
                }
                catch (Exception ex)
                {
                    return new Fail(WallSplitCodes.VerifyJoinNotRestored,
                        "the carrier's original join with element " + id + " could not be restored (" + ex.Message +
                        "). A join that cannot be put back with demonstrable equivalence is a refusal.");
                }
            }

            if (before.EndFlagsRead)
            {
                try
                {
                    Apply(carrier, 0, before.JoinAllowedAtEnd0);
                    Apply(carrier, 1, before.JoinAllowedAtEnd1);
                }
                catch (Exception ex)
                {
                    return new Fail(WallSplitCodes.VerifyJoinNotRestored,
                        "whether the carrier may be joined at its ends could not be restored: " + ex.Message);
                }
            }

            return null;
        }

        private static void Apply(Wall wall, int end, bool allowed)
        {
            if (allowed) WallUtils.AllowWallJoinAtEnd(wall, end);
            else WallUtils.DisallowWallJoinAtEnd(wall, end);
        }

        /// <summary>
        /// Turn the verifier's per-layer findings into the reported rows, so the naming rule's
        /// required fields come from what was MEASURED rather than from what was requested.
        /// </summary>
        private static void FillLayerOutcomes(WallSplitOutcome outcome, WallSplitPlan plan,
                                              Dictionary<int, TypeResolution> types,
                                              WallSplitExpectation expectation, VerificationReport verdict)
        {
            outcome.Layers.Clear();

            foreach (WallLayerPlan layer in plan.Layers)
            {
                JObject measured = verdict.LayerChecks.Children<JObject>()
                    .FirstOrDefault(c => c.Value<int>("layer_index") == layer.LayerIndex);

                types.TryGetValue(layer.LayerIndex, out TypeResolution resolution);
                expectation.WallIdByLayer.TryGetValue(layer.LayerIndex, out long wallId);

                // COUNTED, not assumed. layerChecks is how many probes belong to THIS
                // layer and layerClear how many came back clear. Zero of the first is the
                // case that used to publish a pass: .All() over an empty sequence is true,
                // so a wall with no insert reported cut_verified on every layer having
                // cast no rays at all.
                var layerRows = verdict.CutChecks.Children<JObject>()
                    .Where(c => c.Value<int>("layer_number") == layer.LayerNumber).ToList();
                int layerChecks = layerRows.Count;
                int layerClear = layerRows.Count(c => c.Value<bool>("cut_verified"));
                bool coverageProbed = verdict.CutCoverage != null &&
                                      (verdict.CutCoverage.Value<bool?>("probed") ?? false);

                outcome.Layers.Add(new LayerOutcome
                {
                    LayerIndex = layer.LayerIndex,
                    LayerNumber = layer.LayerNumber,
                    LayerNumberText = layer.LayerNumberText,
                    SourceWallTypeName = layer.SourceWallTypeName,
                    MaterialName = layer.MaterialName,
                    PlannedTypeName = layer.ExpectedTypeName,
                    ExpectedTypeName = resolution?.Name ?? layer.ExpectedTypeName,
                    ActualTypeName = measured?.Value<string>("actual_type_name"),
                    TypeReused = resolution?.Reused ?? false,
                    TypeCreated = resolution?.Created ?? false,
                    TypeFingerprint = layer.TypeFingerprint,
                    ResultingWallId = wallId,
                    IsCoreCarrier = layer.IsCoreCarrier,
                    NamingVerified = measured?.Value<bool?>("naming_verified") ?? false,
                    Materialised = layer.Materialised,
                    NotMaterialisedReason = layer.NotMaterialisedReason,
                    Role = layer.Role,
                    WidthMm = WallLayerRules.FeetToMm(layer.WidthFeet),
                    ExpectedOffsetMm = layer.ExpectedOffsetMm,
                    ObservedOffsetMm = measured?.Value<double?>("observed_offset_mm") ?? double.NaN,
                    DeviationMm = measured?.Value<double?>("deviation_mm") ?? double.NaN,
                    GeometryVerified = measured?.Value<bool?>("geometry_verified") ?? false,
                    SingleLayerVerified = measured?.Value<bool?>("single_layer_verified") ?? false,
                    JoinVerified = verdict.JoinCheck.Value<bool?>("all_original_joins_restored") ?? false,
                    CutProbed = layerChecks > 0,
                    CutVerified = WallLayerRules.CutClaim(layer.IsCoreCarrier, layer.Materialised,
                                                          coverageProbed, layerChecks, layerClear),
                    CutNotProbedReason = WallLayerRules.CutNotProbedReason(
                        layer.IsCoreCarrier, layer.Materialised, coverageProbed, layerChecks)
                });
            }
        }

        // ---- placement ------------------------------------------------------------

        private static Fail PlaceCarrier(Document doc, Wall carrier, Curve target)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var location = carrier.Location as LocationCurve;
                    if (location == null)
                        return new Fail(WallSplitCodes.CarrierConversionFailed,
                            "the carrier lost its location curve during conversion.");
                    location.Curve = target;
                    doc.Regenerate();
                }
                catch (Exception ex)
                {
                    return new Fail(WallSplitCodes.CarrierConversionFailed,
                        "the carrier could not be moved to its layer's position: " + ex.Message);
                }

                if (CurveMatches(((LocationCurve)carrier.Location).Curve, target)) return null;
            }

            double off = Deviation(((LocationCurve)carrier.Location).Curve, target);
            return new Fail(WallSplitCodes.VerifyLayerGeometry,
                "the carrier did not settle at its layer's position: it is " + off.ToString("F2") +
                " mm away after two attempts, and the tolerance is " + WallLayerRules.ToleranceMm +
                " mm. Rolled back whole.");
        }

        private static Fail PlaceLayer(Document doc, Wall wall, Curve target, WallLayerPlan layer)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var location = wall.Location as LocationCurve;
                if (location == null)
                    return new Fail(WallSplitCodes.VerifyLayerGeometry,
                        "layer " + layer.LayerNumberText + " has no location curve.");
                if (CurveMatches(location.Curve, target)) return null;
                try { location.Curve = target; doc.Regenerate(); }
                catch (Exception ex)
                {
                    return new Fail(WallSplitCodes.VerifyLayerGeometry,
                        "layer " + layer.LayerNumberText + " could not be placed: " + ex.Message);
                }
            }
            return CurveMatches(((LocationCurve)wall.Location).Curve, target)
                ? null
                : new Fail(WallSplitCodes.VerifyLayerGeometry,
                    "layer " + layer.LayerNumberText + " did not settle at its position (" +
                    Deviation(((LocationCurve)wall.Location).Curve, target).ToString("F2") + " mm off).");
        }

        // ---- geometry helpers -----------------------------------------------------

        /// <summary>
        /// A layer's curve. A line is translated along the exterior normal; an arc keeps
        /// its centre, its plane, its angles and its sense, and only its RADIUS changes -
        /// so length, direction and curvature all follow from the wall it came from
        /// rather than from two endpoints.
        /// </summary>
        public static Curve OffsetCurve(Curve source, double offsetFeet, XYZ normal, int arcSign)
        {
            try
            {
                if (source is Line line)
                {
                    XYZ shift = normal.Multiply(offsetFeet);
                    return Line.CreateBound(line.GetEndPoint(0).Add(shift), line.GetEndPoint(1).Add(shift));
                }

                if (source is Arc arc)
                {
                    double radius = arc.Radius + arcSign * offsetFeet;
                    if (radius <= WallLayerRules.ToleranceFeet) return null;

                    XYZ start = arc.GetEndPoint(0), end = arc.GetEndPoint(1);
                    XYZ mid = arc.Evaluate(0.5, true);
                    return Arc.Create(Scale(arc.Center, start, radius),
                                      Scale(arc.Center, end, radius),
                                      Scale(arc.Center, mid, radius));
                }
            }
            catch { }
            return null;
        }

        /// <summary>Move a point radially to a new radius about a centre, keeping its Z.</summary>
        private static XYZ Scale(XYZ center, XYZ point, double radius)
        {
            var radial = new XYZ(point.X - center.X, point.Y - center.Y, 0);
            double length = radial.GetLength();
            if (length < 1e-9) return point;
            XYZ scaled = radial.Multiply(radius / length);
            return new XYZ(center.X + scaled.X, center.Y + scaled.Y, point.Z);
        }

        public static XYZ DisplacePoint(XYZ point, double offsetFeet, XYZ normal, int arcSign, Curve curve)
        {
            if (curve is Arc arc)
            {
                var radial = new XYZ(point.X - arc.Center.X, point.Y - arc.Center.Y, 0);
                double length = radial.GetLength();
                if (length < 1e-9) return point;
                XYZ scaled = radial.Multiply((length + arcSign * offsetFeet) / length);
                return new XYZ(arc.Center.X + scaled.X, arc.Center.Y + scaled.Y, point.Z);
            }
            return point.Add(normal.Multiply(offsetFeet));
        }

        /// <summary>How far apart two curves are, in millimetres, sampled rather than assumed.</summary>
        public static double Deviation(Curve a, Curve b)
        {
            if (a == null || b == null) return double.NaN;
            double worst = 0.0;
            foreach (double t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                try
                {
                    double distance = a.Evaluate(t, true).DistanceTo(b.Evaluate(t, true));
                    if (distance > worst) worst = distance;
                }
                catch { return double.NaN; }
            }
            return WallLayerRules.FeetToMm(worst);
        }

        private static bool CurveMatches(Curve a, Curve b)
        {
            double deviation = Deviation(a, b);
            return !double.IsNaN(deviation) && deviation <= WallLayerRules.ToleranceMm;
        }

        /// <summary>
        /// Where a resulting wall actually sits, relative to the ORIGINAL curve. Public
        /// because the VERIFIER measures it - this used to be a private helper here with no
        /// caller at all, while LayerOutcome.ObservedOffsetMm was reported to callers as a
        /// measurement and was in fact always 0.0.
        /// </summary>
        public static double ObservedOffsetMm(Curve original, Curve actual, XYZ normal, int arcSign)
        {
            if (original == null || actual == null) return double.NaN;
            try
            {
                if (original is Arc originalArc && actual is Arc actualArc)
                    return WallLayerRules.FeetToMm(arcSign * (actualArc.Radius - originalArc.Radius));

                XYZ from = original.Evaluate(0.5, true);
                XYZ to = actual.Evaluate(0.5, true);
                return WallLayerRules.FeetToMm(to.Subtract(from).DotProduct(normal));
            }
            catch { return double.NaN; }
        }

        private static XYZ MeasuredNormal(Wall wall)
        {
            try
            {
                XYZ orientation = wall.Orientation;
                if (orientation == null || orientation.IsZeroLength()) return null;
                var flat = new XYZ(orientation.X, orientation.Y, 0);
                return flat.GetLength() < 1e-6 ? null : flat.Normalize();
            }
            catch { return null; }
        }

        /// <summary>
        /// Does a ray along the wall normal, through this point, meet any material of this
        /// wall? The opening either goes through or it does not, and this measures which -
        /// rather than inferring it from the existence of a join, which is D-24.
        /// </summary>
        private static bool PassesThrough(Wall wall, XYZ probe, XYZ normal, double spanFeet)
        {
            try
            {
                double span = Math.Max(spanFeet, 1.0) * 2.0;
                Line ray = Line.CreateBound(probe.Subtract(normal.Multiply(span)),
                                            probe.Add(normal.Multiply(span)));

                var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                GeometryElement geometry = wall.get_Geometry(options);
                if (geometry == null) return true;

                foreach (GeometryObject item in geometry)
                {
                    if (!(item is Solid solid) || solid.Volume <= 0) continue;
                    SolidCurveIntersection hit = solid.IntersectWithCurve(ray, new SolidCurveIntersectionOptions());
                    if (hit == null) continue;

                    double inside = 0.0;
                    for (int i = 0; i < hit.SegmentCount; i++) inside += hit.GetCurveSegment(i).Length;
                    if (inside > WallLayerRules.ToleranceFeet) return false;
                }
                return true;
            }
            catch
            {
                // An unmeasurable cut is not a verified cut.
                return false;
            }
        }

        private static Fail ReplicateRectangularOpenings(Document doc,
                                                         IEnumerable<WallDependency> dependencies,
                                                         IEnumerable<Wall> secondaryWalls,
                                                         JArray generatedIds)
        {
            foreach (WallDependency dependency in dependencies ?? Enumerable.Empty<WallDependency>())
            {
                DependencySnapshot snapshot = dependency == null ? null : dependency.Snapshot;
                if (snapshot == null || dependency.Kind != DependencyKinds.Opening) continue;
                if (!snapshot.OpeningIsRectangular) continue;
                if (snapshot.OpeningBoundaryPoints == null || snapshot.OpeningBoundaryPoints.Count != 2)
                    return new Fail(WallSplitCodes.VerifyOpeningMissing,
                        "rectangular opening " + dependency.ElementId +
                        " did not expose its two boundary corners, so its secondary-layer cuts cannot be rebuilt.");

                foreach (Wall layerWall in secondaryWalls ?? Enumerable.Empty<Wall>())
                {
                    try
                    {
                        Opening made = doc.Create.NewOpening(layerWall, snapshot.OpeningBoundaryPoints[0],
                                                            snapshot.OpeningBoundaryPoints[1]);
                        if (made == null)
                            return new Fail(WallSplitCodes.VerifyOpeningMissing,
                                "Revit returned no opening for secondary wall " + Rid.Value(layerWall.Id) + ".");
                        generatedIds?.Add(Rid.Value(made.Id));
                    }
                    catch (Exception ex)
                    {
                        return new Fail(WallSplitCodes.VerifyOpeningMissing,
                            "rectangular opening " + dependency.ElementId + " could not be cut through secondary wall " +
                            Rid.Value(layerWall.Id) + " (" + ex.Message + ").");
                    }
                }
            }
            return null;
        }

        // ---- parameters -----------------------------------------------------------

        private static double ReadCreationHeight(Wall wall)
        {
            double height = 0.0;
            try
            {
                Parameter p = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (p != null && p.HasValue) height = p.AsDouble();
            }
            catch { }
            return height > WallLayerRules.ToleranceFeet ? height : 10.0;
        }

        /// <summary>
        /// The constraints, copied EXPLICITLY and first. A wall created with an unconnected
        /// height and then left alone is a wall that stopped following its level, which is
        /// what the previous implementation did to every top-constrained wall it touched.
        /// </summary>
        private static void CopyConstraints(Wall source, Wall target)
        {
            var constraints = new[]
            {
                BuiltInParameter.WALL_BASE_CONSTRAINT,
                BuiltInParameter.WALL_BASE_OFFSET,
                BuiltInParameter.WALL_HEIGHT_TYPE,
                BuiltInParameter.WALL_TOP_OFFSET,
                BuiltInParameter.WALL_USER_HEIGHT_PARAM,
                BuiltInParameter.WALL_ATTR_ROOM_BOUNDING,
                BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT,
                BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM,
                BuiltInParameter.PHASE_CREATED,
                BuiltInParameter.PHASE_DEMOLISHED,
                BuiltInParameter.ELEM_PARTITION_PARAM
            };

            foreach (BuiltInParameter id in constraints)
            {
                try
                {
                    Parameter from = source.get_Parameter(id);
                    Parameter to = target.get_Parameter(id);
                    if (from == null || to == null || to.IsReadOnly || !from.HasValue) continue;
                    Assign(from, to);
                }
                catch { /* reported by the post-commit comparison, not swallowed as success */ }
            }
        }

        /// <summary>
        /// Copy the rest by STABLE identifier - BuiltInParameter, then shared GUID - and
        /// report every parameter's disposition, including the ones deliberately skipped.
        /// A parameter that is silently not copied is a parameter somebody finds missing
        /// three weeks later.
        /// </summary>
        private static JObject CopyInstanceParameters(Wall source, Wall target, WallLayerPlan layer,
                                                      WallSplitOptions options)
        {
            var copied = new JArray();
            var readOnly = new JArray();
            var skipped = new JArray();
            var incompatible = new JArray();

            var byKey = new Dictionary<string, Parameter>(StringComparer.Ordinal);
            try
            {
                foreach (Parameter parameter in source.Parameters)
                {
                    string key = WallSplitFacts.StableParameterKey(parameter);
                    if (key != null && !byKey.ContainsKey(key)) byKey[key] = parameter;
                }
            }
            catch { }

            try
            {
                foreach (Parameter parameter in target.Parameters)
                {
                    string key = WallSplitFacts.StableParameterKey(parameter);
                    if (key == null) continue;

                    // ONE POLICY, shared with the verifier. These used to be two tables
                    // that disagreed, and the disagreement rolled back every wall with a door.
                    if (!WallLayerRules.ShouldCopy(key)) { skipped.Add(key); continue; }
                    if (parameter.IsReadOnly) { readOnly.Add(key); continue; }
                    if (!byKey.TryGetValue(key, out Parameter from) || !from.HasValue) continue;
                    if (from.StorageType != parameter.StorageType) { incompatible.Add(key); continue; }

                    try { if (Assign(from, parameter)) copied.Add(key); else incompatible.Add(key); }
                    catch { incompatible.Add(key); }
                }
            }
            catch { }

            // The caller's own origin parameter, named rather than assumed - the button this
            // came from had one organisation's convention compiled in.
            //
            // Its disposition is REPORTED, whatever it is. Silently doing nothing when the
            // parameter is absent, read-only or not text is how a caller who asked for it to
            // be carried finds out three weeks later that it was not.
            if (!string.IsNullOrWhiteSpace(options.OriginGroupParam))
            {
                string key = "origin_group_param:" + options.OriginGroupParam;
                try
                {
                    Parameter from = source.LookupParameter(options.OriginGroupParam);
                    Parameter to = target.LookupParameter(options.OriginGroupParam);

                    if (from == null || to == null) skipped.Add(key + " (absent on " +
                        (from == null ? "the source wall" : "the layer wall") + ")");
                    else if (to.IsReadOnly) readOnly.Add(key);
                    else if (from.StorageType != StorageType.String || to.StorageType != StorageType.String)
                        incompatible.Add(key + " (not a text parameter)");
                    else if (!to.Set(from.AsString() ?? "")) incompatible.Add(key + " (the write was refused)");
                    else copied.Add(key);
                }
                catch (Exception ex) { incompatible.Add(key + " (" + ex.Message + ")"); }
            }

            return new JObject
            {
                ["layer_number"] = layer.LayerNumber,
                ["resulting_wall_id"] = Rid.Value(target.Id),
                ["policy"] = options.ParameterCopyPolicy,
                ["copied"] = copied,
                ["preserved_by_identity"] = new JArray(),
                ["read_only"] = readOnly,
                ["skipped_intentionally"] = skipped,
                ["incompatible"] = incompatible
            };
        }

        private static bool Assign(Parameter from, Parameter to)
        {
            switch (from.StorageType)
            {
                case StorageType.Double: return to.Set(from.AsDouble());
                case StorageType.Integer: return to.Set(from.AsInteger());
                case StorageType.String: return to.Set(from.AsString() ?? "");
                case StorageType.ElementId: return to.Set(from.AsElementId());
                default: return false;
            }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch { return false; }
        }
    }
}
