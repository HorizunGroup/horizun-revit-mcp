// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// PROVING that a wall came apart without losing anything.
//
// This file exists because of a specific gap the director found: the contract
// asserted that openings, sweeps, reveals, embedded curtain walls, dimensions
// and tags were "preserved by identity", and the only thing anybody re-read was
// the family instances. An assertion with nothing behind it is the same failure
// as the implementation this capability replaced, moved one level up.
//
// So the rule is closed and mechanical:
//
//     A dependency may be called preserved_by_identity ONLY IF a verifier here
//     re-reads it. DependencyKinds.DispositionFor is the single place that
//     decides, and a kind with no verifier is unsupported_blocking - the wall is
//     refused before a transaction exists.
//
// Adding a class to DependencyKinds.WithVerifier without adding its verifier
// below fails a test.
//
// RUN TWICE, ON PURPOSE. The same verifier runs:
//
//   * PRE-COMMIT, inside the wall's SubTransaction, where a failure rolls that
//     wall back whole and it comes out exactly as it went in;
//   * POST-COMMIT, on the committed document, where a failure can no longer roll
//     anything back and is REPORTED as such.
//
// The second pass is not decoration. The first one runs before Revit has
// finished with the document; the second asks the model everybody else will see.
// What it CANNOT do is undo, and the reply says so rather than implying the
// second reading had teeth it does not have.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>Which pass is running, and therefore what a failure can still do about it.</summary>
    public enum VerificationPhase
    {
        /// <summary>Inside the wall's SubTransaction. A failure rolls the wall back.</summary>
        BeforeSubTransactionCommit,

        /// <summary>On the committed document. A failure is reported; nothing can be undone.</summary>
        AfterOuterCommit
    }

    /// <summary>
    /// What the verifier compares the model against. Captured before anything moved.
    ///
    /// OriginalCurve is an INDEPENDENT COPY taken by the collector, not the live
    /// LocationCurve.Curve: converting the carrier replaces that curve, and everything here
    /// is measured against the original AFTER that has happened.
    /// </summary>
    public sealed class WallSplitExpectation
    {
        public long CarrierId;
        public string CarrierUniqueId;
        public WallSplitPlan Plan;

        public Curve OriginalCurve;
        public XYZ Normal;
        public int ArcSign;

        /// <summary>layer index -> the wall that layer became.</summary>
        public Dictionary<int, long> WallIdByLayer = new Dictionary<int, long>();

        /// <summary>layer index -> the type name that layer's wall must actually carry.</summary>
        public Dictionary<int, string> TypeNameByLayer = new Dictionary<int, string>();

        public List<DependencySnapshot> Dependencies = new List<DependencySnapshot>();
        public WallJoinFacts Joins = new WallJoinFacts();

        public string PlanFingerprint;
        public string SourceWallUniqueId;
        public string OriginalWallTypeId;
        public List<string> SiblingUniqueIds = new List<string>();

        /// <summary>How far the carrier - and everything hosted in it - was moved.</summary>
        public double CarrierOffsetFeet;

        /// <summary>layer index -> the SOURCE layer's function, so the resulting type can be held to it.</summary>
        public Dictionary<int, string> SourceFunctions = new Dictionary<int, string>();

        /// <summary>layer index -> the SOURCE layer's material UniqueId, compared by identity not by name.</summary>
        public Dictionary<int, string> SourceMaterialUniqueIds = new Dictionary<int, string>();
    }

    /// <summary>The verdict, and everything it was reached from.</summary>
    public sealed class VerificationReport
    {
        public bool Passed = true;
        public string Code;
        public string Message;
        public VerificationPhase Phase;

        public JArray LayerChecks = new JArray();
        public JArray DependencyChecks = new JArray();
        public JArray CutChecks = new JArray();

        /// <summary>
        /// WHAT WAS PROBED, beside the probes themselves. An empty CutChecks used to be
        /// indistinguishable from "every hole verified"; this says which it is.
        /// </summary>
        public JObject CutCoverage = new JObject();
        public JObject JoinCheck = new JObject();
        public JObject ProvenanceCheck = new JObject();

        public void Fail(string code, string message)
        {
            if (!Passed) return;   // the first failure is the one that explains the rollback
            Passed = false;
            Code = code;
            Message = message;
        }

        public JObject ToJson() => new JObject
        {
            ["phase"] = Phase == VerificationPhase.BeforeSubTransactionCommit
                ? "before_subtransaction_commit"
                : "after_outer_commit",
            ["passed"] = Passed,
            ["code"] = Code,
            ["message"] = Message,
            ["can_roll_back"] = Phase == VerificationPhase.BeforeSubTransactionCommit,
            ["layers"] = LayerChecks,
            ["dependencies"] = DependencyChecks,
            ["cuts"] = CutChecks,
            ["cut_coverage"] = CutCoverage,
            ["joins"] = JoinCheck,
            ["provenance"] = ProvenanceCheck,
            ["tolerance_mm"] = WallLayerRules.ToleranceMm
        };
    }

    public static class WallSplitVerifier
    {
        /// <summary>
        /// Re-read the model and hold it against the expectation. Identical in both phases -
        /// which is the point: the post-commit pass is not a weaker "does it exist" check,
        /// it is this one, run again on the document everybody else will open.
        /// </summary>
        public static VerificationReport Run(Document doc, WallSplitExpectation expected, VerificationPhase phase)
        {
            var report = new VerificationReport { Phase = phase };

            Element carrierElement = doc.GetElement(Rid.Make(expected.CarrierId));
            var carrier = carrierElement as Wall;
            if (carrier == null || !carrier.IsValidObject)
            {
                report.Fail(WallSplitCodes.VerifyCarrierIdentity,
                    "the original wall " + expected.CarrierId + " is no longer a valid wall.");
                return report;
            }

            if (!string.Equals(WallSplitFacts.SafeUniqueId(carrier), expected.CarrierUniqueId, StringComparison.Ordinal))
            {
                report.Fail(WallSplitCodes.VerifyCarrierIdentity,
                    "the original wall kept its ElementId but changed UniqueId, so it was replaced rather than " +
                    "converted.");
                return report;
            }

            VerifyLayers(doc, expected, carrier, report);
            if (!report.Passed) return report;

            VerifyDependencies(doc, expected, carrier, report);
            if (!report.Passed) return report;

            VerifyCuts(doc, expected, report);
            if (!report.Passed) return report;

            VerifyJoins(doc, expected, carrier, report);
            if (!report.Passed) return report;

            VerifyProvenance(doc, expected, carrier, report);
            return report;
        }

        // ---- I0 + I2 + the naming rule --------------------------------------------

        private static void VerifyLayers(Document doc, WallSplitExpectation expected, Wall carrier,
                                         VerificationReport report)
        {
            int materialised = 0;

            foreach (WallLayerPlan layer in expected.Plan.Layers)
            {
                var check = new JObject
                {
                    ["layer_index"] = layer.LayerIndex,
                    ["layer_number"] = layer.LayerNumber,
                    ["material_name"] = layer.MaterialName,
                    ["materialised"] = layer.Materialised,
                    ["expected_type_name"] = expected.TypeNameByLayer.TryGetValue(layer.LayerIndex, out string wanted)
                        ? wanted : layer.ExpectedTypeName
                };

                if (!layer.Materialised)
                {
                    // A zero-width membrane keeps its NUMBER and makes no wall. Both halves
                    // are checked, because dropping it was how every offset behind it moved.
                    check["not_materialised_reason"] = layer.NotMaterialisedReason;
                    check["number_preserved"] = layer.LayerNumber == layer.LayerIndex + 1;
                    check["verified"] = true;
                    report.LayerChecks.Add(check);
                    continue;
                }

                materialised++;

                long wallId = expected.WallIdByLayer.TryGetValue(layer.LayerIndex, out long id) ? id : 0;
                check["resulting_wall_id"] = wallId;

                var wall = doc.GetElement(Rid.Make(wallId)) as Wall;
                if (wall == null || !wall.IsValidObject)
                {
                    check["verified"] = false;
                    report.LayerChecks.Add(check);
                    report.Fail(WallSplitCodes.VerifyLayerGeometry,
                        "layer " + layer.LayerNumberText + " has no wall in the model.");
                    return;
                }

                // The TYPE, re-read: its name, and that it really is single-layer with this
                // layer's material, width and function.
                string actualName = WallSplitFacts.SafeName(wall.WallType);
                check["actual_type_name"] = actualName;
                check["naming_verified"] = string.Equals(actualName, check.Value<string>("expected_type_name"),
                                                         StringComparison.Ordinal);

                CompoundStructure structure = wall.WallType == null ? null : wall.WallType.GetCompoundStructure();
                IList<CompoundStructureLayer> layers = structure == null ? null : structure.GetLayers();
                int count = layers == null ? -1 : layers.Count;
                check["layer_count"] = count;
                check["single_layer_verified"] = count == 1;

                if (count == 1)
                {
                    check["width_mm"] = Math.Round(WallLayerRules.FeetToMm(layers[0].Width), 3);
                    check["function"] = layers[0].Function.ToString();
                    check["width_matches"] = WallLayerRules.WithinTolerance(layer.WidthFeet, layers[0].Width);
                    check["function_matches"] = string.Equals(layers[0].Function.ToString(),
                                                              SourceFunction(expected, layer.LayerIndex),
                                                              StringComparison.Ordinal);
                    check["material_matches"] = MaterialMatches(doc, layers[0], expected, layer.LayerIndex);
                }

                if (check.Value<bool>("naming_verified") == false || count != 1 ||
                    check.Value<bool?>("width_matches") != true ||
                    check.Value<bool?>("function_matches") != true ||
                    check.Value<bool?>("material_matches") != true)
                {
                    check["verified"] = false;
                    report.LayerChecks.Add(check);
                    report.Fail(WallSplitCodes.VerifyTypeMismatch,
                        "layer " + layer.LayerNumberText + " is on type '" + actualName + "' with " + count +
                        " layer(s); the plan named '" + check.Value<string>("expected_type_name") +
                        "' as a single layer of " + layer.MaterialName + ".");
                    return;
                }

                // WHERE IT IS, measured against the planned offset.
                Curve target = WallSplitExecutor.OffsetCurve(expected.OriginalCurve, layer.ExpectedOffsetFeet,
                                                             expected.Normal, expected.ArcSign);
                Curve actual = (wall.Location as LocationCurve)?.Curve;
                double deviation = WallSplitExecutor.Deviation(actual, target);

                double observed = WallSplitExecutor.ObservedOffsetMm(expected.OriginalCurve, actual,
                                                                    expected.Normal, expected.ArcSign);
                check["expected_offset_mm"] = Math.Round(layer.ExpectedOffsetMm, 3);
                check["observed_offset_mm"] = double.IsNaN(observed)
                    ? (JToken)JValue.CreateNull() : Math.Round(observed, 3);
                check["deviation_mm"] = double.IsNaN(deviation) ? (JToken)JValue.CreateNull() : Math.Round(deviation, 3);
                check["geometry_verified"] = !double.IsNaN(deviation) && deviation <= WallLayerRules.ToleranceMm;

                if (check.Value<bool>("geometry_verified") == false)
                {
                    check["verified"] = false;
                    report.LayerChecks.Add(check);
                    report.Fail(WallSplitCodes.VerifyLayerGeometry,
                        "layer " + layer.LayerNumberText + " sits " +
                        (double.IsNaN(deviation) ? "an unmeasurable distance" : deviation.ToString("F2") + " mm") +
                        " from where the plan puts it (tolerance " + WallLayerRules.ToleranceMm + " mm).");
                    return;
                }

                // WHICH WAY IT FACES. The executor flips a layer wall to agree with the
                // carrier and used to say "verified below either way" while nothing below
                // re-read any orientation. A single-layer wall is symmetric, so this moves
                // no geometry - but it decides which face is exterior for every later edit
                // and for room bounding, and a claim of verification with nothing behind it
                // is the defect this whole capability is a response to.
                if (!layer.IsCoreCarrier)
                {
                    XYZ layerNormal = MeasuredWallNormal(wall);
                    XYZ carrierNormal = MeasuredWallNormal(carrier);
                    bool facingOk = layerNormal != null && carrierNormal != null &&
                                    layerNormal.DotProduct(carrierNormal) > 0;
                    check["faces_same_way_as_carrier"] = facingOk;

                    if (!facingOk)
                    {
                        check["verified"] = false;
                        report.LayerChecks.Add(check);
                        report.Fail(WallSplitCodes.VerifyLayerGeometry,
                            "layer " + layer.LayerNumberText + " faces the opposite way to the carrier, so its " +
                            "exterior side is the carrier's interior side.");
                        return;
                    }
                }

                check["verified"] = true;
                report.LayerChecks.Add(check);
            }

            // I0: exactly N walls for N layers with volume.
            if (materialised != expected.Plan.WouldProduceWalls)
            {
                report.Fail(WallSplitCodes.VerifyLayerGeometry,
                    "the plan called for " + expected.Plan.WouldProduceWalls + " walls and " + materialised +
                    " were checked.");
            }
        }

        private static string SourceFunction(WallSplitExpectation expected, int layerIndex)
            => expected.SourceFunctions.TryGetValue(layerIndex, out string function) ? function : "";

        private static bool MaterialMatches(Document doc, CompoundStructureLayer actual,
                                            WallSplitExpectation expected, int layerIndex)
        {
            if (!expected.SourceMaterialUniqueIds.TryGetValue(layerIndex, out string wanted)) return false;
            try
            {
                Element material = Rid.Value(actual.MaterialId) > 0 ? doc.GetElement(actual.MaterialId) : null;
                string has = material == null ? null : material.UniqueId;
                return string.Equals(wanted ?? "", has ?? "", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        // ---- every dependency, by kind --------------------------------------------

        private static void VerifyDependencies(Document doc, WallSplitExpectation expected, Wall carrier,
                                               VerificationReport report)
        {
            foreach (DependencySnapshot before in expected.Dependencies)
            {
                var check = new JObject
                {
                    ["element_id"] = before.ElementId,
                    ["unique_id"] = before.UniqueId,
                    ["kind"] = before.Kind
                };

                Element after = doc.GetElement(Rid.Make(before.ElementId));
                if (after == null || !after.IsValidObject)
                {
                    check["present"] = false;
                    check["verified"] = false;
                    report.DependencyChecks.Add(check);
                    report.Fail(WallSplitCodes.VerifyDependencyIdentity,
                        "the " + before.Kind + " " + before.ElementId + " no longer exists.");
                    return;
                }

                check["present"] = true;

                // Identity, common to every kind.
                bool uniqueOk = string.Equals(WallSplitFacts.SafeUniqueId(after), before.UniqueId, StringComparison.Ordinal);
                bool typeOk = Rid.Value(after.GetTypeId()) == before.TypeId;
                bool categoryOk = (after.Category == null ? 0 : Rid.Value(after.Category.Id)) == before.CategoryId;

                check["unique_id_preserved"] = uniqueOk;
                check["type_preserved"] = typeOk;
                check["category_preserved"] = categoryOk;

                if (!uniqueOk || !typeOk || !categoryOk)
                {
                    check["verified"] = false;
                    report.DependencyChecks.Add(check);
                    report.Fail(before.Kind == DependencyKinds.FamilyInstance
                                    ? WallSplitCodes.VerifyInsertIdentity
                                    : WallSplitCodes.VerifyDependencyIdentity,
                        "the " + before.Kind + " " + before.ElementId + " changed " +
                        (!uniqueOk ? "UniqueId" : !typeOk ? "type" : "category") + ".");
                    return;
                }

                string failure = null;
                switch (before.Kind)
                {
                    case DependencyKinds.FamilyInstance:
                        failure = VerifyFamilyInstance(doc, before, (FamilyInstance)after, carrier, expected, check);
                        break;
                    case DependencyKinds.Opening:
                        failure = VerifyOpening(before, (Opening)after, carrier, expected, check);
                        break;
                    case DependencyKinds.WallSweep:
                    case DependencyKinds.Reveal:
                        failure = VerifySweep(before, (WallSweep)after, carrier, expected, check);
                        break;
                    case DependencyKinds.EmbeddedWall:
                        failure = VerifyEmbeddedWall(doc, before, (Wall)after, carrier, check);
                        break;
                    case DependencyKinds.Dimension:
                        failure = VerifyDimension(doc, before, (Dimension)after, check);
                        break;
                    case DependencyKinds.Tag:
                        failure = VerifyTag(doc, before, (IndependentTag)after, carrier, check);
                        break;

                    case DependencyKinds.WallFoundation:
                        failure = VerifyFoundation(before, (WallFoundation)after, carrier, expected, check);
                        break;

                    case DependencyKinds.Rebar:
                        failure = VerifyRebar(doc, before, (Rebar)after, carrier, expected, check);
                        break;

                    case DependencyKinds.RebarContainer:
                    case DependencyKinds.AreaReinforcement:
                    case DependencyKinds.PathReinforcement:
                    case DependencyKinds.FabricArea:
                    case DependencyKinds.FabricSheet:
                        failure = VerifyReinforcementSystem(doc, before, after, carrier, check);
                        break;
                    default:
                        // Unreachable: a kind with no verifier never reaches a snapshot.
                        failure = "no verifier is registered for kind '" + before.Kind + "', so preservation " +
                                  "cannot be proved.";
                        break;
                }

                // AND ITS PARAMETERS, whatever kind it is. They are captured for all seven
                // kinds and used to be compared for exactly one - so an opening, a sweep, a
                // dimension or a tag could come back with every writable parameter changed
                // and pass. The per-kind verifier above checks what is SPECIFIC to the kind;
                // this checks what every element has.
                if (failure == null) failure = CompareParameters(after, before, check);

                check["verified"] = failure == null;
                report.DependencyChecks.Add(check);

                if (failure != null)
                {
                    // A verifier may name its OWN code as "code|message" when a specific one
                    // exists; otherwise the per-kind default applies. Four published codes
                    // stopped being emitted when everything was routed through the default,
                    // and a code no path emits is a promise to a client that can never be
                    // kept - it branches on a value it will never receive.
                    string code = KindFailureCode(before.Kind);
                    string message = failure;
                    int bar = failure.IndexOf('|');
                    if (bar > 0 && WallSplitCodes.All.Contains(failure.Substring(0, bar), StringComparer.Ordinal))
                    {
                        code = failure.Substring(0, bar);
                        message = failure.Substring(bar + 1);
                    }

                    report.Fail(code, message);
                    return;
                }
            }
        }

        private static XYZ MeasuredWallNormal(Wall wall)
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

        private static string KindFailureCode(string kind)
        {
            switch (kind)
            {
                case DependencyKinds.FamilyInstance: return WallSplitCodes.VerifyInsertPlacement;
                case DependencyKinds.Opening: return WallSplitCodes.VerifyDependencyGeometry;
                case DependencyKinds.WallSweep:
                case DependencyKinds.Reveal: return WallSplitCodes.VerifyDependencyRelation;
                case DependencyKinds.EmbeddedWall: return WallSplitCodes.VerifyDependencyRelation;
                case DependencyKinds.Dimension: return WallSplitCodes.VerifyDependencyRelation;
                case DependencyKinds.Tag: return WallSplitCodes.VerifyDependencyRelation;
                case DependencyKinds.WallFoundation: return WallSplitCodes.VerifyFoundationRelation;
                case DependencyKinds.Rebar: return WallSplitCodes.VerifyRebarIdentity;
                case DependencyKinds.RebarContainer:
                case DependencyKinds.AreaReinforcement:
                case DependencyKinds.PathReinforcement:
                case DependencyKinds.FabricArea:
                case DependencyKinds.FabricSheet: return WallSplitCodes.VerifyReinforcementMembers;
                default: return WallSplitCodes.VerifyDependencyIdentity;
            }
        }

        // ---- family instances ------------------------------------------------------

        /// <summary>
        /// The parameters allowed to differ afterwards, each with the reason it is allowed.
        /// It is four entries rather than a category sweep on purpose: a broad ignore list is
        /// how a door comes back with a changed sill height and nobody notices.
        /// </summary>
        private static string VerifyFamilyInstance(Document doc, DependencySnapshot snapshot, FamilyInstance after,
                                                   Wall carrier, WallSplitExpectation expected, JObject check)
        {
            InsertSnapshot before = snapshot.Insert;
            if (before == null) return "no insert snapshot was taken, so nothing can be compared.";

            long hostId = Safe(() => after.Host == null ? 0 : Rid.Value(after.Host.Id), 0L);
            check["host_id"] = hostId;
            check["host_is_carrier"] = hostId == Rid.Value(carrier.Id);
            if (hostId != Rid.Value(carrier.Id))
                return WallSplitCodes.VerifyInsertHost + "|it is hosted by " + hostId + " and the carrier is " +
                       Rid.Value(carrier.Id) + ".";

            long symbolId = Safe(() => after.Symbol == null ? 0 : Rid.Value(after.Symbol.Id), 0L);
            check["symbol_preserved"] = symbolId == before.SymbolId;
            if (symbolId != before.SymbolId)
                return WallSplitCodes.VerifyInsertIdentity + "|its type changed during the conversion.";

            // WHERE IT IS. The insert MOVED, and it was supposed to: its host is the core
            // layer, which sits where the core always sat. So the expectation is the old
            // position displaced by exactly the carrier's displacement - no more, no less.
            // "It is still there" would pass a wall that dragged its doors sideways.
            XYZ nowPoint = Safe(() => (after.Location as LocationPoint)?.Point, (XYZ)null);
            if (before.Point != null && nowPoint != null)
            {
                XYZ target = WallSplitExecutor.DisplacePoint(before.Point, expected.CarrierOffsetFeet,
                                                             expected.Normal, expected.ArcSign, expected.OriginalCurve);
                double deviation = WallLayerRules.FeetToMm(target.DistanceTo(nowPoint));
                check["placement_deviation_mm"] = Math.Round(deviation, 3);
                check["placement_verified"] = deviation <= WallLayerRules.ToleranceMm;
                if (deviation > WallLayerRules.ToleranceMm)
                    return "it is " + deviation.ToString("F2") + " mm from where the carrier's displacement of " +
                           Math.Round(WallLayerRules.FeetToMm(expected.CarrierOffsetFeet), 2) + " mm puts it.";
            }

            string scalar = CompareScalars(after, before, check);
            if (scalar != null) return scalar;

            string subs = CompareSubComponents(doc, after, before, check);
            if (subs != null) return subs;

            string bounds = CompareBounds(after, before, expected, check);
            if (bounds != null) return bounds;

            // Parameters are compared once, for every kind, by the caller.
            return null;
        }

        private static string CompareScalars(FamilyInstance after, InsertSnapshot before, JObject check)
        {
            var mismatches = new List<string>();

            bool handFlipped = Safe(() => after.HandFlipped, false);
            bool facingFlipped = Safe(() => after.FacingFlipped, false);
            bool mirrored = Safe(() => after.Mirrored, false);
            long level = Safe(() => Rid.Value(after.LevelId), 0L);
            long phaseCreated = Safe(() => Rid.Value(after.CreatedPhaseId), 0L);
            long phaseDemolished = Safe(() => Rid.Value(after.DemolishedPhaseId), 0L);
            int workset = Safe(() => after.WorksetId == null ? -1 : after.WorksetId.IntegerValue, -1);
            long designOption = Safe(() => after.DesignOption == null ? 0 : Rid.Value(after.DesignOption.Id), 0L);
            bool pinned = Safe(() => after.Pinned, false);

            check["hand_flipped_preserved"] = handFlipped == before.HandFlipped;
            check["facing_flipped_preserved"] = facingFlipped == before.FacingFlipped;
            check["mirrored_preserved"] = mirrored == before.Mirrored;
            check["level_preserved"] = level == before.LevelId;
            check["phase_created_preserved"] = phaseCreated == before.PhaseCreated;
            check["phase_demolished_preserved"] = phaseDemolished == before.PhaseDemolished;
            check["workset_preserved"] = workset == before.WorksetId;
            check["design_option_preserved"] = designOption == before.DesignOptionId;
            check["pinned_preserved"] = pinned == before.Pinned;

            if (handFlipped != before.HandFlipped) mismatches.Add("hand flip");
            if (facingFlipped != before.FacingFlipped) mismatches.Add("facing flip");
            if (mirrored != before.Mirrored) mismatches.Add("mirrored");
            if (level != before.LevelId) mismatches.Add("level");
            if (phaseCreated != before.PhaseCreated) mismatches.Add("creation phase");
            if (phaseDemolished != before.PhaseDemolished) mismatches.Add("demolition phase");
            if (workset != before.WorksetId) mismatches.Add("workset");
            if (designOption != before.DesignOptionId) mismatches.Add("design option");
            if (pinned != before.Pinned) mismatches.Add("pinned state");

            // Rotation and facing direction: measured, with a tolerance, because they are
            // geometry and not flags.
            if (before.RotationRead)
            {
                var point = Safe(() => after.Location as LocationPoint, (LocationPoint)null);
                double rotation = point == null ? double.NaN : Safe(() => point.Rotation, double.NaN);
                check["rotation_before"] = Math.Round(before.Rotation, 9);
                check["rotation_after"] = double.IsNaN(rotation) ? (JToken)JValue.CreateNull() : Math.Round(rotation, 9);
                bool rotationOk = !double.IsNaN(rotation) && Math.Abs(rotation - before.Rotation) <= 1e-6;
                check["rotation_preserved"] = rotationOk;
                if (!rotationOk) mismatches.Add("rotation");
            }

            if (before.FacingOrientation != null)
            {
                XYZ facing = Safe(() => after.FacingOrientation, (XYZ)null);
                bool facingOk = facing != null && facing.IsAlmostEqualTo(before.FacingOrientation, 1e-6);
                check["facing_orientation_preserved"] = facingOk;
                if (!facingOk) mismatches.Add("facing orientation");
            }

            return mismatches.Count == 0
                ? null
                : "it came out with a different " + string.Join(", ", mismatches) + ".";
        }

        private static string CompareSubComponents(Document doc, FamilyInstance after, InsertSnapshot before,
                                                   JObject check)
        {
            var uniqueIds = new List<string>();
            var symbolIds = new List<long>();
            int count = 0;

            try
            {
                ICollection<ElementId> subs = after.GetSubComponentIds();
                count = subs == null ? 0 : subs.Count;
                foreach (ElementId id in subs ?? new List<ElementId>())
                {
                    Element sub = doc.GetElement(id);
                    if (sub == null) continue;
                    uniqueIds.Add(WallSplitFacts.SafeUniqueId(sub));
                    if (sub is FamilyInstance nested && nested.Symbol != null)
                        symbolIds.Add(Rid.Value(nested.Symbol.Id));
                }
            }
            catch { }

            check["subcomponents_before"] = before.SubComponentCount;
            check["subcomponents_after"] = count;

            bool countOk = count == before.SubComponentCount;
            bool identityOk = before.SubComponentUniqueIds.All(uniqueIds.Contains);
            // The SYMBOLS too, not only the ids: a nested shared instance that survived as an
            // element but came back on a different symbol is not the same component.
            bool symbolsOk = before.SubComponentSymbolIds.OrderBy(x => x)
                                   .SequenceEqual(symbolIds.OrderBy(x => x));

            check["subcomponent_count_preserved"] = countOk;
            check["subcomponent_identity_preserved"] = identityOk;
            check["subcomponent_symbols_preserved"] = symbolsOk;

            if (countOk && identityOk && symbolsOk) return null;
            return WallSplitCodes.VerifyInsertSubcomponents + "|its nested components changed: " +
                   before.SubComponentCount + " before and " + count + " after" +
                   (identityOk ? "" : ", and not the same instances") +
                   (symbolsOk ? "" : ", and not the same symbols") + ".";
        }

        /// <summary>
        /// The bounding box, compared ACROSS THE WALL ONLY where it is meaningful.
        ///
        /// A door's extent THROUGH the wall legitimately changes - the carrier is thinner
        /// than the compound wall was, and the frame follows it. So the normal component is
        /// excluded, by name, with that reason; the along-wall and vertical extents must
        /// still land exactly where the carrier's displacement puts them.
        /// </summary>
        private static string CompareBounds(FamilyInstance after, InsertSnapshot before,
                                            WallSplitExpectation expected, JObject check)
        {
            if (before.Bounds == null) return null;
            BoundingBoxXYZ now = Safe(() => after.get_BoundingBox(null), (BoundingBoxXYZ)null);
            if (now == null)
            {
                check["bounds_comparable"] = false;
                return "its bounding box could not be read after the conversion, so its extent cannot be verified.";
            }

            XYZ along = new XYZ(-expected.Normal.Y, expected.Normal.X, 0);
            if (along.GetLength() < 1e-9) along = XYZ.BasisX;
            along = along.Normalize();

            XYZ beforeCentre = (before.Bounds.Min + before.Bounds.Max) * 0.5;
            XYZ nowCentre = (now.Min + now.Max) * 0.5;
            XYZ expectedCentre = WallSplitExecutor.DisplacePoint(beforeCentre, expected.CarrierOffsetFeet,
                                                                 expected.Normal, expected.ArcSign,
                                                                 expected.OriginalCurve);

            double alongDelta = Math.Abs(nowCentre.Subtract(expectedCentre).DotProduct(along));
            double upDelta = Math.Abs(nowCentre.Z - expectedCentre.Z);

            double beforeAlong = Math.Abs(before.Bounds.Max.Subtract(before.Bounds.Min).DotProduct(along));
            double nowAlong = Math.Abs(now.Max.Subtract(now.Min).DotProduct(along));
            double beforeUp = before.Bounds.Max.Z - before.Bounds.Min.Z;
            double nowUp = now.Max.Z - now.Min.Z;

            check["bounds_comparable"] = true;
            check["bounds_centre_along_deviation_mm"] = Math.Round(WallLayerRules.FeetToMm(alongDelta), 3);
            check["bounds_centre_vertical_deviation_mm"] = Math.Round(WallLayerRules.FeetToMm(upDelta), 3);
            check["bounds_extent_along_deviation_mm"] =
                Math.Round(WallLayerRules.FeetToMm(Math.Abs(nowAlong - beforeAlong)), 3);
            check["bounds_extent_vertical_deviation_mm"] =
                Math.Round(WallLayerRules.FeetToMm(Math.Abs(nowUp - beforeUp)), 3);
            check["bounds_normal_component_excluded_because"] =
                "the carrier is thinner than the compound wall, so the insert's extent THROUGH the wall changes by " +
                "design; only the along-wall and vertical extents are held to the tolerance.";

            double worst = new[]
            {
                WallLayerRules.FeetToMm(alongDelta),
                WallLayerRules.FeetToMm(upDelta),
                WallLayerRules.FeetToMm(Math.Abs(nowAlong - beforeAlong)),
                WallLayerRules.FeetToMm(Math.Abs(nowUp - beforeUp))
            }.Max();

            check["bounds_verified"] = worst <= WallLayerRules.ToleranceMm;
            return worst <= WallLayerRules.ToleranceMm
                ? null
                : "its extent along the wall or in height moved by " + worst.ToString("F2") + " mm.";
        }

        private static string CompareParameters(Element after, DependencySnapshot snapshot, JObject check)
        {
            var changed = new JArray();
            var allowed = new JArray();

            try
            {
                foreach (Parameter parameter in after.Parameters)
                {
                    string key = WallSplitFacts.StableParameterKey(parameter);
                    if (key == null || !snapshot.Parameters.TryGetValue(key, out string was)) continue;

                    string isNow = WallSplitFacts.RenderParameter(parameter);
                    if (string.Equals(was, isNow, StringComparison.Ordinal)) continue;

                    var row = new JObject
                    {
                        ["parameter"] = key,
                        ["before"] = was,
                        ["after"] = isNow
                    };

                    // THE SAME POLICY THE COPIER READS. Two tables used to answer this,
                    // and they disagreed about bip:HOST_AREA_COMPUTED - which Revit
                    // recomputes because this operation deliberately makes the carrier one
                    // layer thick. Every wall with a door rolled back on it.
                    if (WallLayerRules.MayChangeWithoutExplanation(key))
                    {
                        row["allowed_because"] = WallLayerRules.ParameterReason(key);
                        row["parameter_kind"] = WallLayerRules.KindOf(key).ToString();
                        allowed.Add(row);
                    }
                    else if (IsVerifiedRebarShapeParameter(after, parameter, check))
                    {
                        // Revit owns the dimensional parameters named by the active
                        // RebarShapeDefinition. They may change only after the stronger
                        // geometric verifier has proved that every centreline point still
                        // satisfies one of the permitted carrier/face constraints. A
                        // random GUID parameter is still authored and still fails.
                        row["allowed_because"] =
                            "the active RebarShapeDefinition owns this dimension and the rebar centreline was independently verified against its permitted carrier constraints";
                        row["parameter_kind"] = "RebarShapeDimensionVerified";
                        allowed.Add(row);
                    }
                    else
                    {
                        row["parameter_kind"] = WallLayerRules.KindOf(key).ToString();
                        changed.Add(row);
                    }
                }
            }
            catch
            {
                check["parameters_comparable"] = false;
                return "its parameters could not be re-read, so they cannot be verified.";
            }

            // Named explicitly because they are the ones a person asks about first.
            check["sill_height"] = ParameterRow(snapshot, after, "bip:INSTANCE_SILL_HEIGHT_PARAM");
            check["head_height"] = ParameterRow(snapshot, after, "bip:INSTANCE_HEAD_HEIGHT_PARAM");

            check["parameters_comparable"] = true;
            check["parameters_changed_unexpectedly"] = changed;
            check["parameters_changed_by_design"] = allowed;

            return changed.Count == 0
                ? null
                : WallSplitCodes.VerifyParameterMismatch + "|it came out with " + changed.Count +
                  " parameter(s) this conversion has no reason to change: " +
                  string.Join(", ", changed.Children<JObject>().Select(c => c.Value<string>("parameter")));
        }

        private static bool IsVerifiedRebarShapeParameter(Element after, Parameter parameter, JObject check)
        {
            if (!(after is Rebar rebar) || parameter == null) return false;
            if (check.Value<bool?>("centreline_constraint_preserved") != true) return false;

            try
            {
                RebarShape shape = after.Document.GetElement(rebar.GetShapeId()) as RebarShape;
                if (shape == null) return false;

                using (RebarShapeDefinition definition = shape.GetRebarShapeDefinition())
                {
                    if (definition == null) return false;
                    long parameterId = Rid.Value(parameter.Id);
                    return definition.GetParameters().Any(id => Rid.Value(id) == parameterId);
                }
            }
            catch
            {
                // Failure to prove ownership is not permission to excuse the change.
                return false;
            }
        }

        private static JObject ParameterRow(DependencySnapshot snapshot, Element after, string key)
        {
            snapshot.Parameters.TryGetValue(key, out string was);
            string isNow = null;
            try
            {
                foreach (Parameter parameter in after.Parameters)
                {
                    if (WallSplitFacts.StableParameterKey(parameter) != key) continue;
                    isNow = WallSplitFacts.RenderParameter(parameter);
                    break;
                }
            }
            catch { }

            return new JObject
            {
                ["present"] = was != null,
                ["before"] = was,
                ["after"] = isNow,
                ["preserved"] = was == null || string.Equals(was, isNow, StringComparison.Ordinal)
            };
        }

        // ---- openings ---------------------------------------------------------------

        private static string VerifyOpening(DependencySnapshot before, Opening after, Wall carrier,
                                            WallSplitExpectation expected, JObject check)
        {
            long hostId = Safe(() => after.Host == null ? 0 : Rid.Value(after.Host.Id), 0L);
            check["host_id"] = hostId;
            check["host_is_carrier"] = hostId == Rid.Value(carrier.Id);
            if (hostId != Rid.Value(carrier.Id))
                return WallSplitCodes.VerifyInsertHost + "|it is hosted by " + hostId + " and the carrier is " +
                       Rid.Value(carrier.Id) + ".";

            bool rectangular = Safe(() => after.IsRectBoundary, false);
            check["rectangular_preserved"] = rectangular == before.OpeningIsRectangular;
            if (rectangular != before.OpeningIsRectangular)
                return "it changed between a rectangular and a profiled opening.";

            var points = new List<XYZ>();
            double length = 0.0;
            int count = 0;
            try
            {
                if (rectangular)
                {
                    IList<XYZ> rect = after.BoundaryRect;
                    if (rect != null) foreach (XYZ point in rect) points.Add(point);
                    count = points.Count;
                }
                else
                {
                    foreach (Curve curve in after.BoundaryCurves)
                    {
                        if (curve == null) continue;
                        count++;
                        length += curve.Length;
                        points.Add(curve.GetEndPoint(0));
                    }
                }
            }
            catch
            {
                check["boundary_readable"] = false;
                return "its boundary could not be re-read, so the profile cannot be verified.";
            }

            check["boundary_readable"] = true;
            check["curve_count_before"] = before.OpeningCurveCount;
            check["curve_count_after"] = count;
            if (count != before.OpeningCurveCount)
                return "its boundary went from " + before.OpeningCurveCount + " to " + count + " curves.";

            if (!rectangular)
            {
                double delta = WallLayerRules.FeetToMm(Math.Abs(length - before.OpeningBoundaryLengthFeet));
                check["boundary_length_deviation_mm"] = Math.Round(delta, 3);
                if (delta > WallLayerRules.ToleranceMm)
                    return "its boundary length moved by " + delta.ToString("F2") + " mm.";
            }

            // The boundary MOVED with the carrier, exactly as the carrier moved.
            double worst = 0.0;
            for (int i = 0; i < Math.Min(points.Count, before.OpeningBoundaryPoints.Count); i++)
            {
                XYZ target = WallSplitExecutor.DisplacePoint(before.OpeningBoundaryPoints[i],
                                                             expected.CarrierOffsetFeet, expected.Normal,
                                                             expected.ArcSign, expected.OriginalCurve);
                worst = Math.Max(worst, WallLayerRules.FeetToMm(target.DistanceTo(points[i])));
            }

            check["boundary_deviation_mm"] = Math.Round(worst, 3);
            check["boundary_verified"] = worst <= WallLayerRules.ToleranceMm;
            return worst <= WallLayerRules.ToleranceMm
                ? null
                : "its boundary is " + worst.ToString("F2") + " mm from where the carrier's displacement puts it.";
        }

        // ---- sweeps and reveals ------------------------------------------------------

        private static string VerifySweep(DependencySnapshot before, WallSweep after, Wall carrier,
                                          WallSplitExpectation expected, JObject check)
        {
            var hosts = new List<long>();
            try { foreach (ElementId id in after.GetHostIds()) hosts.Add(Rid.Value(id)); } catch { }
            hosts.Sort();

            check["host_ids"] = new JArray(hosts);
            check["still_on_carrier"] = hosts.Contains(Rid.Value(carrier.Id));
            if (!hosts.Contains(Rid.Value(carrier.Id)))
                return "it is no longer attached to the carrier.";

            check["host_set_preserved"] = hosts.SequenceEqual(before.SweepHostIds);
            if (!hosts.SequenceEqual(before.SweepHostIds))
                return "the set of walls it runs on changed.";

            WallSweepInfo info = Safe(() => after.GetWallSweepInfo(), (WallSweepInfo)null);
            if (info == null)
            {
                check["info_readable"] = false;
                return "its sweep information could not be re-read, so its profile and position cannot be verified.";
            }

            check["info_readable"] = true;
            check["sweep_type"] = info.WallSweepType.ToString();
            check["sweep_type_preserved"] = string.Equals(info.WallSweepType.ToString(), before.SweepType,
                                                          StringComparison.Ordinal);
            check["profile_preserved"] = Rid.Value(info.ProfileId) == before.SweepProfileId;
            check["vertical_preserved"] = Safe(() => info.IsVertical, false) == before.SweepIsVertical;
            string wallSide = Safe(() => info.WallSide.ToString(), (string)null);
            check["wall_side"] = wallSide;
            check["wall_side_preserved"] = string.Equals(wallSide, before.SweepWallSide,
                                                          StringComparison.Ordinal);

            double distanceDelta = WallLayerRules.FeetToMm(Math.Abs(Safe(() => info.Distance, double.NaN) -
                                                                    before.SweepDistanceFeet));
            double offsetDelta = WallLayerRules.FeetToMm(Math.Abs(Safe(() => info.WallOffset, double.NaN) -
                                                                  before.SweepWallOffsetFeet));
            check["distance_deviation_mm"] = double.IsNaN(distanceDelta) ? (JToken)JValue.CreateNull()
                                                                          : Math.Round(distanceDelta, 3);
            check["wall_offset_deviation_mm"] = double.IsNaN(offsetDelta) ? (JToken)JValue.CreateNull()
                                                                          : Math.Round(offsetDelta, 3);

            if (check.Value<bool>("sweep_type_preserved") == false) return "its sweep type changed.";
            if (check.Value<bool>("profile_preserved") == false) return "its profile changed.";
            if (check.Value<bool>("vertical_preserved") == false) return "it changed between vertical and horizontal.";
            if (check.Value<bool>("wall_side_preserved") == false) return "it changed wall side.";
            if (double.IsNaN(distanceDelta) || distanceDelta > WallLayerRules.ToleranceMm)
                return "its distance along the wall moved.";
            if (double.IsNaN(offsetDelta) || offsetDelta > WallLayerRules.ToleranceMm)
                return "its offset from the wall moved.";

            // Distance and WallOffset are measured FROM THE HOST, so they are unchanged by
            // construction when the host is re-typed and moved - the branches above cannot
            // fail for that reason alone. Where the sweep actually IS in the model is the
            // check with something behind it: it must have moved exactly as the carrier did.
            if (before.SweepBounds != null)
            {
                BoundingBoxXYZ now = Safe(() => after.get_BoundingBox(null), (BoundingBoxXYZ)null);
                check["bounds_comparable"] = now != null;
                if (now == null)
                    return "its position in the model could not be read, so whether it followed the carrier is " +
                           "unknown - and its host-relative distance and offset cannot show that on their own.";

                XYZ beforeCentre = (before.SweepBounds.Min + before.SweepBounds.Max) * 0.5;
                XYZ nowCentre = (now.Min + now.Max) * 0.5;
                // A sweep follows the HOST FACE, not only the location curve. Converting a
                // 350 mm compound wall to a 152 mm carrier moves that face another 99 mm.
                // Revit did exactly that in the live fixture; the old verifier called the
                // correct movement a defect because it modelled only the curve displacement.
                double carrierWidth = Safe(() => carrier.Width, double.NaN);
                double oldWidth = expected.Plan == null ? double.NaN : expected.Plan.TotalWidthFeet;
                double sideSign = string.Equals(wallSide, "Exterior", StringComparison.OrdinalIgnoreCase) ? 1.0 :
                                  string.Equals(wallSide, "Interior", StringComparison.OrdinalIgnoreCase) ? -1.0 :
                                  double.NaN;
                if (double.IsNaN(carrierWidth) || double.IsNaN(oldWidth) || double.IsNaN(sideSign))
                    return "its host-face displacement could not be computed from the wall widths and wall side.";
                double faceWidthChangeFeet = sideSign * (carrierWidth - oldWidth) * 0.5;
                double expectedSweepOffsetFeet = expected.CarrierOffsetFeet + faceWidthChangeFeet;
                check["carrier_curve_displacement_mm"] =
                    Math.Round(WallLayerRules.FeetToMm(expected.CarrierOffsetFeet), 3);
                check["host_face_width_change_mm"] =
                    Math.Round(WallLayerRules.FeetToMm(faceWidthChangeFeet), 3);
                check["expected_sweep_displacement_mm"] =
                    Math.Round(WallLayerRules.FeetToMm(expectedSweepOffsetFeet), 3);
                XYZ target = WallSplitExecutor.DisplacePoint(beforeCentre, expectedSweepOffsetFeet,
                                                             expected.Normal, expected.ArcSign,
                                                             expected.OriginalCurve);

                double movedDeviation = WallLayerRules.FeetToMm(target.DistanceTo(nowCentre));
                double stationaryDeviation = WallLayerRules.FeetToMm(beforeCentre.DistanceTo(nowCentre));
                double deviation = Math.Min(movedDeviation, stationaryDeviation);
                check["position_deviation_mm"] = Math.Round(deviation, 3);
                check["position_if_host_moved_deviation_mm"] = Math.Round(movedDeviation, 3);
                check["position_if_world_stationary_deviation_mm"] = Math.Round(stationaryDeviation, 3);
                check["position_mode"] = movedDeviation <= stationaryDeviation
                    ? "followed_carrier_location_curve" : "kept_world_geometry_while_host_face_changed";
                check["position_verified"] = deviation <= WallLayerRules.ToleranceMm;
                if (deviation > WallLayerRules.ToleranceMm)
                    return "it is neither where it was nor where the carrier's displacement puts it; the nearest " +
                           "verified interpretation is still " + deviation.ToString("F2") + " mm away.";
            }

            return null;
        }

        // ---- embedded and dependent walls --------------------------------------------

        private static string VerifyEmbeddedWall(Document doc, DependencySnapshot before, Wall after, Wall carrier,
                                                 JObject check)
        {
            // FIRST: is it still related to the carrier at all? Every other dependency
            // verifier checks that (host_is_carrier, still_on_carrier); this one did not
            // even take the carrier as an argument, so the contract's "stays embedded in
            // the carrier" was an assertion with nothing behind it.
            bool relatedToCarrier = false;
            try
            {
                foreach (ElementId id in carrier.FindInserts(true, true, true, true))
                    if (Rid.Value(id) == Rid.Value(after.Id)) { relatedToCarrier = true; break; }
            }
            catch { }

            if (!relatedToCarrier)
            {
                try { relatedToCarrier = JoinGeometryUtils.AreElementsJoined(doc, carrier, after); }
                catch { }
            }

            check["still_related_to_carrier"] = relatedToCarrier;
            if (!relatedToCarrier)
                return "it is no longer embedded in, nor joined to, the carrier - the wall it used to sit inside.";

            check["is_curtain"] = Safe(() => after.WallType != null && after.WallType.Kind == WallKind.Curtain, false);
            check["curtain_preserved"] = check.Value<bool>("is_curtain") == before.WallIsCurtain;
            if (check.Value<bool>("curtain_preserved") == false) return "it stopped being a curtain wall.";

            long baseLevel = Safe(() => Rid.Value(after.LevelId), 0L);
            check["base_level_preserved"] = baseLevel == before.WallBaseLevelId;
            if (baseLevel != before.WallBaseLevelId) return "its base level changed.";

            long topLevel = 0;
            double baseOffset = 0, topOffset = 0;
            try
            {
                Parameter top = after.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                topLevel = top == null || !top.HasValue ? 0 : Rid.Value(top.AsElementId());
                Parameter b = after.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                if (b != null && b.HasValue) baseOffset = b.AsDouble();
                Parameter t = after.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                if (t != null && t.HasValue) topOffset = t.AsDouble();
            }
            catch { }

            check["top_level_preserved"] = topLevel == before.WallTopLevelId;
            check["base_offset_deviation_mm"] =
                Math.Round(WallLayerRules.FeetToMm(Math.Abs(baseOffset - before.WallBaseOffsetFeet)), 3);
            check["top_offset_deviation_mm"] =
                Math.Round(WallLayerRules.FeetToMm(Math.Abs(topOffset - before.WallTopOffsetFeet)), 3);

            if (topLevel != before.WallTopLevelId) return "its top constraint changed.";
            if (check.Value<double>("base_offset_deviation_mm") > WallLayerRules.ToleranceMm) return "its base offset moved.";
            if (check.Value<double>("top_offset_deviation_mm") > WallLayerRules.ToleranceMm) return "its top offset moved.";

            string digest = WallSplitFacts.CurveDigest(Safe(() => (after.Location as LocationCurve)?.Curve, (Curve)null));
            check["curve_preserved"] = string.Equals(digest ?? "", before.WallCurveDigest ?? "", StringComparison.Ordinal);
            return check.Value<bool>("curve_preserved") ? null : "its own curve moved.";
        }

        // ---- dimensions ---------------------------------------------------------------

        private static string VerifyDimension(Document doc, DependencySnapshot before, Dimension after, JObject check)
        {
            int count = 0;
            var representations = new List<string>();
            try
            {
                ReferenceArray references = after.References;
                count = references == null ? 0 : references.Size;
                if (references != null)
                {
                    foreach (Reference reference in references)
                    {
                        if (reference == null) continue;
                        try { representations.Add(reference.ConvertToStableRepresentation(doc)); }
                        catch { representations.Add("<unreadable>"); }
                    }
                }
            }
            catch
            {
                check["references_readable"] = false;
                return "its references could not be re-read, so it cannot be shown to still measure anything.";
            }

            check["references_readable"] = true;
            check["reference_count_before"] = before.ReferenceCount;
            check["reference_count_after"] = count;
            check["reference_count_preserved"] = count == before.ReferenceCount;

            // A dimension with fewer references than it had is a dimension that lost a
            // witness line: orphaned in every sense that matters, even if Revit kept the
            // element alive.
            if (count != before.ReferenceCount)
                return "it went from " + before.ReferenceCount + " references to " + count +
                       ", so it lost at least one of the things it was measuring.";
            if (count == 0)
                return "it has no references left at all.";

            var lostRepresentations = before.ReferenceRepresentations
                .Where(r => r != "<unreadable>" && !representations.Contains(r))
                .ToList();
            check["references_changed"] = new JArray(lostRepresentations);
            check["references_preserved"] = lostRepresentations.Count == 0;

            if (before.DimensionValueRead)
            {
                double? value = Safe(() => after.Value, (double?)null);
                check["value_readable"] = value.HasValue;
                if (!value.HasValue) return "its value could no longer be read.";
                double delta = WallLayerRules.FeetToMm(Math.Abs(value.Value - before.DimensionValueFeet));
                check["value_deviation_mm"] = Math.Round(delta, 3);
                // The value CAN legitimately move: it may measure to a face of the wall,
                // and the carrier is thinner. What may not happen is losing a reference,
                // which is checked above. The measured delta is reported either way.
                check["value_change_note"] =
                    "a dimension that measured to a FACE of the compound wall legitimately reads differently once " +
                    "the carrier is a single layer; what it may not do is lose a reference, which is checked " +
                    "separately and is what makes it orphaned.";
            }

            return lostRepresentations.Count == 0
                ? null
                : "it lost " + lostRepresentations.Count + " of the references it was measuring.";
        }

        // ---- tags ----------------------------------------------------------------------

        private static string VerifyTag(Document doc, DependencySnapshot before, IndependentTag after, Wall carrier,
                                        JObject check)
        {
            var ids = new List<long>();
            var uniqueIds = new List<string>();
            int referenceCount;
            bool nonLocal;

            try
            {
                foreach (ElementId id in after.GetTaggedLocalElementIds())
                {
                    long raw = Rid.Value(id);
                    if (raw <= 0) continue;
                    ids.Add(raw);
                    Element element = doc.GetElement(id);
                    uniqueIds.Add(element == null ? "" : WallSplitFacts.SafeUniqueId(element));
                }
            }
            catch
            {
                check["tagged_readable"] = false;
                return "what it tags could not be re-read.";
            }

            try
            {
                IList<Reference> references = after.GetTaggedReferences();
                referenceCount = references == null ? 0 : references.Count;
                nonLocal = referenceCount > ids.Count;
            }
            catch
            {
                referenceCount = ids.Count;
                nonLocal = before.TagHasNonLocalReference;
            }

            check["tagged_readable"] = true;
            check["tagged_element_ids"] = new JArray(ids);
            check["tagged_count_before"] = before.TaggedElementIds.Count;
            check["tagged_count_after"] = ids.Count;
            check["reference_count_before"] = before.TaggedReferenceCount;
            check["reference_count_after"] = referenceCount;
            check["has_non_local_reference"] = nonLocal;

            // THE WHOLE SET, in order. A tag can point at several elements, and keeping only
            // the first - which the first version of this did - let a multi-reference tag
            // lose every reference but one and still verify.
            bool idsOk = ids.SequenceEqual(before.TaggedElementIds);
            bool uniqueOk = uniqueIds.SequenceEqual(before.TaggedUniqueIds);
            bool countOk = referenceCount == before.TaggedReferenceCount;

            check["tagged_set_preserved"] = idsOk && uniqueOk;
            check["reference_count_preserved"] = countOk;

            if (!idsOk || !uniqueOk)
                return "the set of elements it tags changed: " +
                       string.Join(",", before.TaggedElementIds) + " before, " + string.Join(",", ids) + " after.";

            if (!countOk)
                return "it had " + before.TaggedReferenceCount + " references and now has " + referenceCount +
                       ", so at least one reference this capability cannot resolve to a local element was lost.";

            long ownerView = Safe(() => Rid.Value(after.OwnerViewId), 0L);
            check["owner_view_preserved"] = ownerView == before.OwnerViewId;
            if (ownerView != before.OwnerViewId) return "it moved to a different view.";

            if (before.TagHeadPosition != null)
            {
                XYZ head = Safe(() => after.TagHeadPosition, (XYZ)null);
                double delta = head == null ? double.NaN
                                            : WallLayerRules.FeetToMm(head.DistanceTo(before.TagHeadPosition));
                check["head_deviation_mm"] = double.IsNaN(delta) ? (JToken)JValue.CreateNull() : Math.Round(delta, 3);
                check["head_position_preserved"] = !double.IsNaN(delta) && delta <= WallLayerRules.ToleranceMm;
                // The head is annotation, not model geometry: it does not follow the carrier's
                // displacement, so it must not move at all.
                if (check.Value<bool>("head_position_preserved") == false)
                    return "its head moved " + (double.IsNaN(delta) ? "an unmeasurable distance"
                                                                    : delta.ToString("F2") + " mm") + ".";
            }

            return null;
        }

        // ---- structural: the footing ---------------------------------------------------

        /// <summary>
        /// A continuous footing. It hangs off ONE wall - WallFoundation.WallId - and the
        /// contract's promise is that it still hangs off the SAME one afterwards, not off
        /// one of the finish layers, and that it moved exactly as the carrier moved.
        /// </summary>
        private static string VerifyFoundation(DependencySnapshot before, WallFoundation after, Wall carrier,
                                               WallSplitExpectation expected, JObject check)
        {
            long wallId = Safe(() => Rid.Value(after.WallId), 0L);
            check["wall_id"] = wallId;
            check["wall_is_carrier"] = wallId == Rid.Value(carrier.Id);

            if (wallId != Rid.Value(carrier.Id))
                return WallSplitCodes.VerifyFoundationRelation + "|it now belongs to wall " + wallId +
                       " and the carrier is " + Rid.Value(carrier.Id) + ". A footing that moved onto a finish " +
                       "layer is a footing under the wrong wall.";

            long level = Safe(() => Rid.Value(after.LevelId), 0L);
            check["level_preserved"] = level == before.FoundationLevelId;
            if (level != before.FoundationLevelId)
                return WallSplitCodes.VerifyFoundationRelation + "|it changed level.";

            // WHERE IT IS. The footing follows the wall, so it must have moved by exactly the
            // carrier's displacement - no more, and not zero either.
            if (before.FoundationBounds != null)
            {
                BoundingBoxXYZ now = Safe(() => after.get_BoundingBox(null), (BoundingBoxXYZ)null);
                check["bounds_comparable"] = now != null;
                if (now == null)
                    return WallSplitCodes.VerifyFoundationGeometry +
                           "|its extent could not be read, so whether it followed the carrier is unknown.";

                XYZ beforeCentre = (before.FoundationBounds.Min + before.FoundationBounds.Max) * 0.5;
                XYZ nowCentre = (now.Min + now.Max) * 0.5;
                XYZ target = WallSplitExecutor.DisplacePoint(beforeCentre, expected.CarrierOffsetFeet,
                                                             expected.Normal, expected.ArcSign,
                                                             expected.OriginalCurve);

                double deviation = WallLayerRules.FeetToMm(target.DistanceTo(nowCentre));
                check["position_deviation_mm"] = Math.Round(deviation, 3);
                check["position_verified"] = deviation <= WallLayerRules.ToleranceMm;
                if (deviation > WallLayerRules.ToleranceMm)
                    return WallSplitCodes.VerifyFoundationGeometry + "|it is " + deviation.ToString("F2") +
                           " mm from where the carrier's displacement puts it, so its alignment with the wall " +
                           "it supports was not preserved.";
            }

            // Its own curve, on the same grid as everything else.
            string digest = WallSplitFacts.CurveDigest(Safe(() => (after.Location as LocationCurve)?.Curve, (Curve)null));
            check["curve_digest_before"] = before.FoundationCurveDigest;
            check["curve_digest_after"] = digest;
            return null;
        }

        // ---- structural: one bar set ----------------------------------------------------

        /// <summary>
        /// A bar set. Identity, host, type, shape, layout, quantity, every position, and -
        /// the check with real teeth - whether every position is still INSIDE the solid of
        /// the single-layer core wall.
        ///
        /// A bar that was inside a 350 mm compound wall can easily be outside a 150 mm core.
        /// It is NOT moved to fit: relocating reinforcement is a structural decision that
        /// belongs to somebody else, so the wall rolls back and says which positions fell out.
        /// </summary>
        private static string VerifyRebar(Document doc, DependencySnapshot before, Rebar after, Wall carrier,
                                          WallSplitExpectation expected, JObject check)
        {
            JObject described = null;
            try { described = RebarFacts.Describe(doc, after, includePositions: true); } catch { }

            if (described == null)
                return WallSplitCodes.VerifyRebarIdentity +
                       "|this bar set could not be re-read after the conversion, so nothing about it can be verified.";

            long hostId = described["host"]?.Value<long?>("id") ?? 0;
            check["host_id"] = hostId;
            check["host_is_carrier"] = hostId == Rid.Value(carrier.Id);
            if (hostId != Rid.Value(carrier.Id))
                return WallSplitCodes.VerifyRebarIdentity + "|it is hosted by " + hostId + " and the carrier is " +
                       Rid.Value(carrier.Id) + ".";

            long barType = described["bar_type"]?.Value<long?>("id") ?? 0;
            long shape = described["shape"]?.Value<long?>("id") ?? 0;
            check["bar_type_preserved"] = barType == before.RebarBarTypeId;
            check["shape_preserved"] = shape == before.RebarShapeId;
            if (barType != before.RebarBarTypeId) return WallSplitCodes.VerifyRebarIdentity + "|its bar type changed.";
            if (shape != before.RebarShapeId) return WallSplitCodes.VerifyRebarIdentity + "|its shape changed.";

            JToken layout = described["layout"];
            string rule = layout?.Value<string>("rule");
            int positions = layout?.Value<int?>("number_of_bar_positions") ?? 0;
            double quantity = layout?.Value<double?>("quantity") ?? 0.0;

            check["layout_rule_before"] = before.RebarLayoutRule;
            check["layout_rule_after"] = rule;
            check["positions_before"] = before.RebarNumberOfPositions;
            check["positions_after"] = positions;
            check["quantity_before"] = before.RebarQuantity;
            check["quantity_after"] = quantity;

            if (!string.Equals(rule ?? "", before.RebarLayoutRule ?? "", StringComparison.Ordinal))
                return WallSplitCodes.VerifyRebarLayout + "|its layout rule changed from '" +
                       before.RebarLayoutRule + "' to '" + rule + "'.";
            if (positions != before.RebarNumberOfPositions)
                return WallSplitCodes.VerifyRebarLayout + "|it went from " + before.RebarNumberOfPositions +
                       " bar positions to " + positions + ".";
            if (Math.Abs(quantity - before.RebarQuantity) > 1e-6)
                return WallSplitCodes.VerifyRebarLayout + "|its quantity changed.";

            // EVERY layout position, read through the same extractor as the approved
            // snapshot. These are offsets from bar zero; the next block separately proves
            // that bar zero did NOT move in model space. Rehosting or relocating steel is a
            // structural decision, never a side effect of decomposing a wall.
            List<string> nowPositions = WallSplitFacts.ReadRebarPositionDigests(described);

            check["position_count_before"] = before.RebarPositionDigests.Count;
            check["position_count_after"] = nowPositions.Count;
            if (nowPositions.Count != before.RebarPositionDigests.Count)
                return WallSplitCodes.VerifyRebarLayout + "|it has " + nowPositions.Count +
                       " readable positions and had " + before.RebarPositionDigests.Count + ".";
            bool positionOffsetsPreserved = nowPositions.SequenceEqual(before.RebarPositionDigests);
            check["position_offsets_preserved"] = positionOffsetsPreserved;
            if (!positionOffsetsPreserved)
                return WallSplitCodes.VerifyRebarLayout +
                       "|one or more bar-position offsets or existence flags changed.";

            IList<double[]> centreline;
            try { centreline = RebarFacts.CentrelinePointsMm(after, asDeclared: false); }
            catch { centreline = null; }

            check["centreline_point_count_before"] = before.RebarCentrelinePointsMm.Count;
            check["centreline_point_count_after"] = centreline == null ? 0 : centreline.Count;
            if (centreline == null || centreline.Count != before.RebarCentrelinePointsMm.Count ||
                centreline.Count == 0)
                return WallSplitCodes.VerifyRebarLayout +
                       "|the drawn centreline could not be compared point by point after the conversion.";

            // Revit may preserve a bar in model space, follow the carrier location curve,
            // or preserve its cover constraint to either host face. Those are four exact
            // transformations, not a loose tolerance. Every point must agree with ONE of
            // them; containment in the new core is still checked immediately afterwards.
            double carrierWidth = Safe(() => carrier.Width, double.NaN);
            double sourceWidth = expected.Plan == null ? double.NaN : expected.Plan.TotalWidthFeet;
            if (double.IsNaN(carrierWidth) || double.IsNaN(sourceWidth))
                return WallSplitCodes.VerifyRebarLayout +
                       "|the old and new host widths could not be read, so cover-constrained movement cannot be verified.";
            double faceWidthChangeFeet = (carrierWidth - sourceWidth) * 0.5;
            var modes = new[]
            {
                new { Name = "kept_world_position", Offset = 0.0 },
                new { Name = "followed_carrier_curve", Offset = expected.CarrierOffsetFeet },
                new { Name = "followed_exterior_face", Offset = expected.CarrierOffsetFeet + faceWidthChangeFeet },
                new { Name = "followed_interior_face", Offset = expected.CarrierOffsetFeet - faceWidthChangeFeet }
            };
            var worstByMode = modes.ToDictionary(m => m.Name, m => 0.0, StringComparer.Ordinal);
            var selectedModeCounts = modes.ToDictionary(m => m.Name, m => 0, StringComparer.Ordinal);
            double worstCentrelineMm = 0.0;
            for (int i = 0; i < centreline.Count; i++)
            {
                double[] was = before.RebarCentrelinePointsMm[i];
                double[] now = centreline[i];
                if (was == null || now == null || was.Length < 3 || now.Length < 3)
                    return WallSplitCodes.VerifyRebarLayout +
                           "|centreline point " + i + " was not a complete XYZ coordinate.";
                XYZ approvedPoint = new XYZ(was[0] / RebarFacts.FtToMm, was[1] / RebarFacts.FtToMm,
                                            was[2] / RebarFacts.FtToMm);
                XYZ actual = new XYZ(now[0] / RebarFacts.FtToMm, now[1] / RebarFacts.FtToMm,
                                     now[2] / RebarFacts.FtToMm);
                var deviations = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var mode in modes)
                {
                    XYZ target = WallSplitExecutor.DisplacePoint(approvedPoint, mode.Offset,
                                                                 expected.Normal, expected.ArcSign,
                                                                 expected.OriginalCurve);
                    double deviation = WallLayerRules.FeetToMm(target.DistanceTo(actual));
                    deviations[mode.Name] = deviation;
                    worstByMode[mode.Name] = Math.Max(worstByMode[mode.Name], deviation);
                }
                var pointMode = deviations.OrderBy(kv => kv.Value).First();
                selectedModeCounts[pointMode.Key]++;
                worstCentrelineMm = Math.Max(worstCentrelineMm, pointMode.Value);
            }
            check["centreline_point_modes"] = JObject.FromObject(selectedModeCounts);
            check["centreline_rigid_mode_deviations_mm"] = JObject.FromObject(
                worstByMode.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)));
            check["centreline_worst_deviation_mm"] = Math.Round(worstCentrelineMm, 3);
            check["centreline_constraint_preserved"] = worstCentrelineMm <= WallLayerRules.ToleranceMm;
            if (worstCentrelineMm > WallLayerRules.ToleranceMm)
                return WallSplitCodes.VerifyRebarLayout + "|the drawn bar centreline is " +
                       worstCentrelineMm.ToString("F2") +
                       " mm from every permitted constraint-preserving position (world, carrier curve, exterior " +
                       "face or interior face). Steel is not relocated merely to make it fit.";

            // ---- CONTAINMENT: the check the whole thing exists for -------------------
            string why;
            HostMesh mesh = HostSolidMesh.Usable(carrier, out why);
            check["carrier_mesh_readable"] = mesh != null;

            if (mesh == null)
                return WallSplitCodes.RebarOutsideCoreCarrier +
                       "|the solid of the core carrier could not be measured (" + (why ?? "no reason given") +
                       "), so whether this reinforcement still sits inside it is unknown. Unknown is not inside.";

            double radiusMm = 0.0;
            try { radiusMm = (described["bar_type"]?.Value<double?>("model_diameter_mm") ?? 0.0) / 2.0; } catch { }

            var offsets = new List<double>();
            double[] normal = null;
            try
            {
                JToken offsetToken = described["layout"]?["offset_from_first_bar_mm"];
                if (offsetToken is JArray offsetArray)
                    foreach (JToken value in offsetArray) offsets.Add(value.Value<double>());
                JToken normalToken = described["layout"]?["normal"];
                if (normalToken != null)
                    normal = new[]
                    {
                        normalToken.Value<double?>("x") ?? 0.0,
                        normalToken.Value<double?>("y") ?? 0.0,
                        normalToken.Value<double?>("z") ?? 0.0
                    };
            }
            catch { }
            if (offsets.Count == 0) offsets.Add(0.0);

            SetContainment containment = RebarContainment.Check(
                mesh, centreline, offsets, normal, radiusMm, null,
                WallLayerRules.ToleranceMm, 25.0);

            check["containment_before"] = before.RebarContainmentBefore;
            check["containment_after"] = containment.Word;
            check["containment_measured"] = containment.Measured;
            check["worst_outside_mm"] = Math.Round(containment.WorstOutsideMm, 3);
            check["worst_position"] = containment.WorstPosition;

            bool inside = containment.Measured &&
                          string.Equals(containment.Word, SolidContainment.Inside, StringComparison.Ordinal);
            check["inside_core_carrier"] = inside;

            if (!inside)
                return WallSplitCodes.RebarOutsideCoreCarrier +
                       "|this reinforcement is '" + containment.Word + "' with respect to the core carrier" +
                       (containment.WorstPosition >= 0
                            ? " - position " + containment.WorstPosition + " is " +
                              containment.WorstOutsideMm.ToString("F1") + " mm outside"
                            : "") +
                       ". It is hosted by the compound wall and does not fit the core layer. It has NOT been moved to " +
                       "fit: relocating reinforcement is a structural decision, not a side effect of splitting a " +
                       "wall, so the whole wall was rolled back.";

            return null;
        }

        // ---- structural: area, path, fabric and container systems -----------------------

        /// <summary>
        /// A reinforcement system. Its host, its type, its boundary and ITS MEMBERS - a
        /// system that lost three of its bars is still a system, and "it still exists" would
        /// pass it.
        /// </summary>
        private static string VerifyReinforcementSystem(Document doc, DependencySnapshot before, Element after,
                                                        Wall carrier, JObject check)
        {
            long hostId = 0;
            var members = new List<long>();
            var boundary = new List<long>();
            string layers = null;
            bool readable = true;

            switch (before.Kind)
            {
                case DependencyKinds.AreaReinforcement:
                    var area = after as AreaReinforcement;
                    if (area == null) { readable = false; break; }
                    try { hostId = Rid.Value(area.GetHostId()); } catch { readable = false; }
                    try { foreach (ElementId id in area.GetRebarInSystemIds()) members.Add(Rid.Value(id)); } catch { readable = false; }
                    try { foreach (ElementId id in area.GetBoundaryCurveIds()) boundary.Add(Rid.Value(id)); } catch { readable = false; }
                    try
                    {
                        var rows = new List<string>();
                        foreach (AreaReinforcementLayerType layer in Enum.GetValues(typeof(AreaReinforcementLayerType)))
                        {
                            bool active = area.IsLayerActive(layer);
                            rows.Add(layer + "=" + (active ? "1" : "0") + ":" + (active ? area.GetNumberOfLines(layer) : 0));
                        }
                        layers = string.Join(";", rows);
                    }
                    catch { readable = false; }
                    break;

                case DependencyKinds.PathReinforcement:
                    var path = after as PathReinforcement;
                    if (path == null) { readable = false; break; }
                    try { hostId = Rid.Value(path.GetHostId()); } catch { readable = false; }
                    try { foreach (ElementId id in path.GetRebarInSystemIds()) members.Add(Rid.Value(id)); } catch { readable = false; }
                    try { foreach (ElementId id in path.GetCurveElementIds()) boundary.Add(Rid.Value(id)); } catch { readable = false; }
                    break;

                case DependencyKinds.RebarContainer:
                    var container = after as RebarContainer;
                    if (container == null) { readable = false; break; }
                    try { hostId = Rid.Value(container.GetHostId()); } catch { readable = false; }
                    try { foreach (RebarContainerItem item in container) members.Add(item.ItemIndex); } catch { readable = false; }
                    break;

                default:
                    try
                    {
                        Parameter host = after.get_Parameter(BuiltInParameter.HOST_ID_PARAM);
                        if (host != null && host.HasValue) hostId = Rid.Value(host.AsElementId());
                    }
                    catch { readable = false; }
                    if (after is FabricArea fabric)
                    {
                        try { foreach (ElementId id in fabric.GetFabricSheetElementIds()) members.Add(Rid.Value(id)); } catch { readable = false; }
                        try { foreach (ElementId id in fabric.GetBoundaryCurveIds()) boundary.Add(Rid.Value(id)); } catch { readable = false; }
                    }
                    break;
            }

            members.Sort();
            boundary.Sort();

            check["readable"] = readable;
            check["host_id"] = hostId;
            check["members_before"] = before.SystemMemberIds.Count;
            check["members_after"] = members.Count;

            if (!readable)
                return WallSplitCodes.UnsupportedReinforcementKind +
                       "|this " + before.Kind + " could not be fully re-read after the conversion, so its members " +
                       "cannot be shown to have survived. A system this capability cannot verify completely is a " +
                       "refusal, not a warning.";

            // The host may be the carrier, or it may be a host this system shares with the
            // wall; what may not happen is it moving onto one of the finish layers.
            check["host_is_carrier"] = hostId == Rid.Value(carrier.Id);
            if (before.SystemHostId == Rid.Value(carrier.Id) && hostId != Rid.Value(carrier.Id))
                return WallSplitCodes.VerifyReinforcementMembers + "|it was hosted by the wall and is now hosted " +
                       "by " + hostId + ".";

            var lost = before.SystemMemberIds.Where(id => !members.Contains(id)).ToList();
            var gained = members.Where(id => !before.SystemMemberIds.Contains(id)).ToList();
            check["members_lost"] = new JArray(lost);
            check["members_gained"] = new JArray(gained);

            if (lost.Count > 0 || gained.Count > 0)
                return WallSplitCodes.VerifyReinforcementMembers + "|its members changed: " + lost.Count +
                       " lost, " + gained.Count + " gained. A system that lost bars is still a system, which is " +
                       "why the members are counted rather than the system's existence.";

            check["boundary_preserved"] = boundary.SequenceEqual(before.SystemBoundaryIds);
            if (!boundary.SequenceEqual(before.SystemBoundaryIds))
                return WallSplitCodes.VerifyReinforcementMembers + "|its boundary or path curves changed.";

            if (layers != null)
            {
                check["layers_before"] = before.SystemLayersDigest;
                check["layers_after"] = layers;
                if (!string.Equals(layers, before.SystemLayersDigest, StringComparison.Ordinal))
                    return WallSplitCodes.VerifyReinforcementMembers + "|its layers or their line counts changed.";
            }

            return null;
        }

        // ---- the cuts -------------------------------------------------------------------

        /// <summary>
        /// Does each insert's opening actually pass through each secondary layer? Measured at
        /// FIVE points, not one.
        ///
        /// A single ray through the centre of the bounding box proves the middle of the hole
        /// and nothing else: a cut that came out half-height, or offset along the wall, or
        /// present on one side only, passes that test. The four inner points sit at a quarter
        /// of the opening's extent from its centre along the wall and in height, so a partial
        /// cut fails. A point that cannot be measured is a FAILURE, never a pass.
        /// </summary>
        private static void VerifyCuts(Document doc, WallSplitExpectation expected, VerificationReport report)
        {
            // EVERY insert, not only the family instances.
            //
            // The first version filtered to `Kind == FamilyInstance && Insert?.Bounds != null`
            // and returned early when nothing matched. Three ways that passed vacuously:
            // an Opening element is a first-class insert and was never probed at all; an
            // embedded curtain wall likewise; and a family instance whose bounding box came
            // back null was dropped with no row, no note and no failure - so a wall whose
            // every insert was unreadable reported cut_verified: true having measured nothing.
            var subjects = new List<CutSubject>();
            foreach (DependencySnapshot dependency in expected.Dependencies)
            {
                switch (dependency.Kind)
                {
                    case DependencyKinds.FamilyInstance:
                        subjects.Add(new CutSubject
                        {
                            Id = dependency.ElementId,
                            Kind = dependency.Kind,
                            Bounds = dependency.Insert == null ? null : dependency.Insert.Bounds
                        });
                        break;

                    case DependencyKinds.Opening:
                        subjects.Add(new CutSubject
                        {
                            Id = dependency.ElementId,
                            Kind = dependency.Kind,
                            Bounds = BoundsOf(dependency.OpeningBoundaryPoints)
                        });
                        break;

                    case DependencyKinds.EmbeddedWall:
                        // An embedded wall makes its own hole in its host. The secondary
                        // layers have to carry that hole too, or the assembly is solid where
                        // the host is not.
                        subjects.Add(new CutSubject
                        {
                            Id = dependency.ElementId,
                            Kind = dependency.Kind,
                            Bounds = BoundsOfElement(doc, dependency.ElementId)
                        });
                        break;
                }
            }

            var layers = expected.Plan.Layers.Where(l => l.Materialised && !l.IsCoreCarrier).ToList();
            report.CutCoverage = new JObject
            {
                ["inserts_in_ledger"] = subjects.Count,
                ["inserts_probeable"] = subjects.Count(x => x.Bounds != null),
                ["inserts_unprobeable"] = new JArray(subjects.Where(x => x.Bounds == null)
                                                             .Select(x => new JObject
                                                             {
                                                                 ["element_id"] = x.Id,
                                                                 ["kind"] = x.Kind
                                                             })),
                ["secondary_layers"] = layers.Count
            };

            // An insert nobody could measure is NOT a cut nobody has to prove.
            CutSubject unmeasurable = subjects.FirstOrDefault(x => x.Bounds == null);
            if (unmeasurable != null && layers.Count > 0)
            {
                report.Fail(WallSplitCodes.VerifyOpeningMissing,
                    "the extent of " + unmeasurable.Kind + " " + unmeasurable.Id + " could not be read, so whether " +
                    "the secondary layers are cut where it passes through them cannot be measured. An unmeasurable " +
                    "cut is not a verified cut.");
                return;
            }

            if (subjects.Count == 0 || layers.Count == 0)
            {
                // Nothing to prove, and the report SAYS nothing was proved rather than
                // leaving cut_verified reading like a measurement.
                report.CutCoverage["probed"] = false;
                report.CutCoverage["note"] = subjects.Count == 0
                    ? "this wall carries no insert, opening or embedded wall, so there is no hole any layer has to " +
                      "reproduce. No probe was run and none is claimed."
                    : "this wall produced no secondary layer, so there is nothing for a hole to pass through.";
                return;
            }

            report.CutCoverage["probed"] = true;

            XYZ along = new XYZ(-expected.Normal.Y, expected.Normal.X, 0);
            along = along.GetLength() < 1e-9 ? XYZ.BasisX : along.Normalize();

            foreach (WallLayerPlan layer in layers)
            {
                long wallId = expected.WallIdByLayer.TryGetValue(layer.LayerIndex, out long id) ? id : 0;
                var wall = doc.GetElement(Rid.Make(wallId)) as Wall;
                if (wall == null)
                {
                    report.Fail(WallSplitCodes.VerifyOpeningMissing,
                        "layer " + layer.LayerNumberText + " has no wall to probe.");
                    return;
                }

                foreach (CutSubject subject in subjects)
                {
                    var probes = ProbePoints(subject.Bounds, expected, along);
                    var rows = new JArray();
                    bool allClear = true;

                    foreach (XYZ probe in probes)
                    {
                        double inside;
                        bool measured = MaterialAlongRay(wall, probe, expected.Normal,
                                                         expected.Plan.TotalWidthFeet, out inside);
                        bool clear = measured && inside <= WallLayerRules.ToleranceFeet;
                        allClear &= clear;

                        rows.Add(new JObject
                        {
                            ["x"] = Math.Round(probe.X, 6),
                            ["y"] = Math.Round(probe.Y, 6),
                            ["z"] = Math.Round(probe.Z, 6),
                            ["measured"] = measured,
                            ["material_along_ray_mm"] = measured
                                ? (JToken)Math.Round(WallLayerRules.FeetToMm(inside), 3)
                                : JValue.CreateNull(),
                            ["clear"] = clear
                        });
                    }

                    report.CutChecks.Add(new JObject
                    {
                        ["layer_number"] = layer.LayerNumber,
                        ["wall_id"] = wallId,
                        ["insert_id"] = subject.Id,
                        ["insert_kind"] = subject.Kind,
                        ["points_checked"] = probes.Count,
                        ["tolerance_mm"] = WallLayerRules.ToleranceMm,
                        ["probes"] = rows,
                        ["cut_verified"] = allClear
                    });

                    if (!allClear)
                    {
                        report.Fail(WallSplitCodes.VerifyOpeningMissing,
                            "layer " + layer.LayerNumberText + " still has material where " + subject.Kind + " " +
                            subject.Id + " passes through it, or the geometry could not be measured. The opening " +
                            "is not cut all the way through.");
                        return;
                    }
                }
            }
        }

        /// <summary>One thing that has to punch through every secondary layer.</summary>
        private sealed class CutSubject
        {
            public long Id;
            public string Kind;
            public BoundingBoxXYZ Bounds;
        }

        /// <summary>A box round captured boundary points. Null when there are too few to bound.</summary>
        private static BoundingBoxXYZ BoundsOf(List<XYZ> points)
        {
            if (points == null || points.Count < 2) return null;
            try
            {
                double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
                double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
                double minZ = points.Min(p => p.Z), maxZ = points.Max(p => p.Z);
                if (maxZ - minZ < WallLayerRules.ToleranceFeet) return null;
                return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
            }
            catch { return null; }
        }

        private static BoundingBoxXYZ BoundsOfElement(Document doc, long elementId)
        {
            try { return doc.GetElement(Rid.Make(elementId))?.get_BoundingBox(null); }
            catch { return null; }
        }

        private static List<XYZ> ProbePoints(BoundingBoxXYZ bounds, WallSplitExpectation expected, XYZ along)
        {
            XYZ centre = WallSplitExecutor.DisplacePoint((bounds.Min + bounds.Max) * 0.5,
                                                         expected.CarrierOffsetFeet, expected.Normal,
                                                         expected.ArcSign, expected.OriginalCurve);

            double halfAlong = Math.Abs(bounds.Max.Subtract(bounds.Min).DotProduct(along)) / 2.0;
            double halfUp = (bounds.Max.Z - bounds.Min.Z) / 2.0;

            // Half of the half-extent: a quarter of the way out from the centre, comfortably
            // inside the opening even for a family whose bounding box is larger than its hole.
            double insetAlong = halfAlong * 0.5;
            double insetUp = halfUp * 0.5;

            return new List<XYZ>
            {
                centre,
                centre.Add(along.Multiply(insetAlong)).Add(new XYZ(0, 0, insetUp)),
                centre.Add(along.Multiply(-insetAlong)).Add(new XYZ(0, 0, insetUp)),
                centre.Add(along.Multiply(insetAlong)).Add(new XYZ(0, 0, -insetUp)),
                centre.Add(along.Multiply(-insetAlong)).Add(new XYZ(0, 0, -insetUp))
            };
        }

        /// <summary>
        /// How much of this wall's material a ray through the point meets. Returns false when
        /// the geometry could not be read at all - which the caller treats as a failure,
        /// because an unmeasurable cut is not a verified cut.
        /// </summary>
        private static bool MaterialAlongRay(Wall wall, XYZ probe, XYZ normal, double spanFeet, out double insideFeet)
        {
            insideFeet = 0.0;
            try
            {
                double span = Math.Max(spanFeet, 1.0) * 2.0;
                Line ray = Line.CreateBound(probe.Subtract(normal.Multiply(span)), probe.Add(normal.Multiply(span)));

                var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                GeometryElement geometry = wall.get_Geometry(options);
                if (geometry == null) return false;

                bool sawSolid = false;
                foreach (GeometryObject item in geometry)
                {
                    if (!(item is Solid solid) || solid.Volume <= 0) continue;
                    sawSolid = true;

                    SolidCurveIntersection hit = solid.IntersectWithCurve(ray, new SolidCurveIntersectionOptions());
                    if (hit == null) continue;
                    for (int i = 0; i < hit.SegmentCount; i++) insideFeet += hit.GetCurveSegment(i).Length;
                }

                return sawSolid;
            }
            catch { return false; }
        }

        // ---- joins -----------------------------------------------------------------------

        /// <summary>
        /// The joins, compared against EVERY field the collector captured.
        ///
        /// The first version captured ElementsAtEnd0/1 and the cut order and then compared
        /// neither - the same "captured and never used" shape that lost the end joins in the
        /// implementation this replaces, one level up. Every field below is read back and
        /// held against its snapshot, and a fact that could not be READ is reported as
        /// unreadable rather than as verified.
        /// </summary>
        private static void VerifyJoins(Document doc, WallSplitExpectation expected, Wall carrier,
                                        VerificationReport report)
        {
            WallJoinFacts before = expected.Joins;

            // ---- the geometric joins, and which element cuts which --------------------
            var joined = new List<long>();
            var cutByOther = new Dictionary<long, bool>();
            var unreadableCutOrder = new List<long>();

            try
            {
                foreach (ElementId id in JoinGeometryUtils.GetJoinedElements(doc, carrier))
                {
                    long raw = Rid.Value(id);
                    if (raw <= 0) continue;
                    joined.Add(raw);

                    Element other = doc.GetElement(id);
                    if (other == null) { unreadableCutOrder.Add(raw); continue; }
                    try { cutByOther[raw] = JoinGeometryUtils.IsCuttingElementInJoin(doc, carrier, other); }
                    catch { unreadableCutOrder.Add(raw); }
                }
            }
            catch { }
            joined.Sort();

            var layerWallIds = expected.WallIdByLayer.Values.Where(v => v != expected.CarrierId).ToList();
            var lost = before.GeometricJoinIds.Where(o => !joined.Contains(o)).ToList();

            report.JoinCheck["original_join_ids"] = new JArray(before.GeometricJoinIds);
            report.JoinCheck["current_join_ids"] = new JArray(joined);
            report.JoinCheck["restored"] = new JArray(before.GeometricJoinIds.Where(joined.Contains));
            report.JoinCheck["lost"] = new JArray(lost);
            report.JoinCheck["all_original_joins_restored"] = lost.Count == 0;
            report.JoinCheck["layer_walls_joined_to_carrier"] = new JArray(layerWallIds.Where(joined.Contains));
            report.JoinCheck["secondary_wall_join_policy"] =
                "the layer walls are joined to the CARRIER only, and not to the carrier's neighbours. That is the " +
                "explicit decision: inheriting the end joins would make each layer meet a neighbour whose own " +
                "layers do not exist yet, and the result depends on the order the batch happened to run in.";

            // ---- the cut order, per neighbour ------------------------------------------
            var cutOrderChanged = new JArray();
            foreach (KeyValuePair<long, bool> pair in before.CutByOther)
            {
                if (!joined.Contains(pair.Key)) continue;          // reported as lost above
                if (!cutByOther.TryGetValue(pair.Key, out bool now))
                {
                    unreadableCutOrder.Add(pair.Key);
                    continue;
                }
                if (now != pair.Value)
                    cutOrderChanged.Add(new JObject
                    {
                        ["other_id"] = pair.Key,
                        ["carrier_was_cutting"] = pair.Value,
                        ["carrier_is_cutting"] = now
                    });
            }

            report.JoinCheck["cut_order_changed"] = cutOrderChanged;
            report.JoinCheck["cut_order_unreadable"] = new JArray(unreadableCutOrder.Distinct());
            report.JoinCheck["cut_order_preserved"] = cutOrderChanged.Count == 0 && unreadableCutOrder.Count == 0;

            // ---- the end flags -----------------------------------------------------------
            bool endFlagsOk = true;
            if (before.EndFlagsRead)
            {
                bool end0 = true, end1 = true, read = true;
                try
                {
                    end0 = WallUtils.IsWallJoinAllowedAtEnd(carrier, 0);
                    end1 = WallUtils.IsWallJoinAllowedAtEnd(carrier, 1);
                }
                catch { read = false; }

                report.JoinCheck["end_flags_readable"] = read;
                report.JoinCheck["join_allowed_at_end_0"] = end0;
                report.JoinCheck["join_allowed_at_end_1"] = end1;
                endFlagsOk = read && end0 == before.JoinAllowedAtEnd0 && end1 == before.JoinAllowedAtEnd1;
                report.JoinCheck["end_flags_preserved"] = endFlagsOk;
            }
            else
            {
                report.JoinCheck["end_flags_preserved"] = JValue.CreateNull();
                report.JoinCheck["end_flags_note"] =
                    "Revit would not report whether joining is allowed at this wall's ends before the conversion, " +
                    "so the flags are recorded as unread rather than assumed to be the default - and nothing here " +
                    "claims they were preserved.";
            }

            // ---- WHO MEETS THE WALL AT EACH END, in order --------------------------------
            //
            // ORDER MATTERS here and is compared: get_ElementsAtJoin returns the elements in
            // join order, and a junction whose order changed is a junction that looks
            // different. This is the comparison the first version captured and never made.
            var end0Now = new List<long>();
            var end1Now = new List<long>();
            bool endElementsRead = true;
            try
            {
                var location = carrier.Location as LocationCurve;
                if (location == null) endElementsRead = false;
                else
                {
                    foreach (Element element in location.get_ElementsAtJoin(0))
                        if (element != null) end0Now.Add(Rid.Value(element.Id));
                    foreach (Element element in location.get_ElementsAtJoin(1))
                        if (element != null) end1Now.Add(Rid.Value(element.Id));
                }
            }
            catch { endElementsRead = false; }

            report.JoinCheck["elements_at_end_0_before"] = new JArray(before.ElementsAtEnd0);
            report.JoinCheck["elements_at_end_1_before"] = new JArray(before.ElementsAtEnd1);
            report.JoinCheck["elements_at_end_0_after"] = new JArray(end0Now);
            report.JoinCheck["elements_at_end_1_after"] = new JArray(end1Now);
            report.JoinCheck["elements_at_join_readable_before"] = before.ElementsAtJoinRead;
            report.JoinCheck["elements_at_join_readable_after"] = endElementsRead;

            bool endElementsOk;
            if (!before.ElementsAtJoinRead || !endElementsRead)
            {
                // Not comparable. It is NOT reported as preserved, and it blocks: the
                // conservative reading of "I could not look" is that the junction may have
                // changed, and this capability refuses rather than guesses.
                report.JoinCheck["elements_at_join_preserved"] = JValue.CreateNull();
                report.JoinCheck["elements_at_join_note"] =
                    "the elements meeting this wall at its ends could not be read " +
                    (before.ElementsAtJoinRead ? "after" : "before") + " the conversion, so whether the junction " +
                    "survived is unknown. Unknown is not verified, and this wall is refused rather than reported " +
                    "as preserved.";
                endElementsOk = false;
            }
            else
            {
                endElementsOk = end0Now.SequenceEqual(before.ElementsAtEnd0) &&
                                end1Now.SequenceEqual(before.ElementsAtEnd1);
                report.JoinCheck["elements_at_join_preserved"] = endElementsOk;
            }

            // ---- the verdict ---------------------------------------------------------------
            if (lost.Count > 0)
            {
                report.Fail(WallSplitCodes.VerifyJoinNotRestored,
                    "the carrier no longer meets " + lost.Count + " element(s) it was joined to before the " +
                    "conversion (" + string.Join(", ", lost) + "). A join that cannot be restored with " +
                    "demonstrable equivalence is a refusal, not a footnote.");
                return;
            }

            if (cutOrderChanged.Count > 0)
            {
                report.Fail(WallSplitCodes.VerifyJoinNotRestored,
                    "the carrier is joined to everything it was, but " + cutOrderChanged.Count +
                    " junction(s) came out with the cut order reversed, which changes what the model looks like " +
                    "where the walls meet.");
                return;
            }

            if (unreadableCutOrder.Count > 0)
            {
                report.Fail(WallSplitCodes.VerifyJoinNotRestored,
                    "the cut order of " + unreadableCutOrder.Distinct().Count() + " junction(s) could not be read " +
                    "back, so it cannot be shown to be what it was.");
                return;
            }

            if (!endElementsOk)
            {
                report.Fail(WallSplitCodes.VerifyJoinNotRestored,
                    "the elements meeting the carrier at its ends are not the ones that met it before, or could " +
                    "not be read on one side of the conversion.");
                return;
            }

            if (!endFlagsOk)
            {
                report.Fail(WallSplitCodes.VerifyJoinNotRestored,
                    "whether the carrier may be joined at its ends is not what it was before the conversion.");
                return;
            }

            // ---- THE CHAIN, not a star ------------------------------------------
            //
            // This used to require every layer wall to be joined to the CARRIER, which is
            // what produced joins across gaps of 94.5 mm and 19.5 mm on a seven-layer wall
            // and left two permanent "joined but do not intersect" warnings in the model.
            // The expectation is now the same chain the executor builds, computed from the
            // same core rule so the two cannot describe different graphs.
            var ordered = expected.Plan.Layers
                .Where(l => l.Materialised)
                .OrderBy(l => l.LayerIndex)
                .ToList();

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (int[] edge in WallLayerRules.ChainEdges(ordered.Select(l => l.LayerIndex).ToList()))
            {
                if (!expected.WallIdByLayer.TryGetValue(edge[0], out long ea) ||
                    !expected.WallIdByLayer.TryGetValue(edge[1], out long eb))
                {
                    report.Fail(WallSplitCodes.VerifyJoinMissing,
                        "the chain needs layers " + (edge[0] + 1) + " and " + (edge[1] + 1) +
                        " and one of them has no wall to join.");
                    return;
                }
                wanted.Add(WallLayerRules.EdgeKey(ea, eb));
            }

            var siblingIds = new HashSet<long>(expected.WallIdByLayer.Values);
            var found = new HashSet<string>(StringComparer.Ordinal);
            var foreignEdges = new List<string>();
            var extraEdges = new List<string>();
            var disjointEdges = new List<string>();

            foreach (long wid in siblingIds)
            {
                var w = doc.GetElement(Rid.Make(wid)) as Wall;
                if (w == null) continue;
                ICollection<ElementId> joins;
                try { joins = JoinGeometryUtils.GetJoinedElements(doc, w); }
                catch
                {
                    report.Fail(WallSplitCodes.VerifyJoinMissing,
                        "the joins of layer wall " + wid + " could not be read, so the chain cannot be verified.");
                    return;
                }
                foreach (ElementId oid in joins)
                {
                    long other = Rid.Value(oid);
                    string key = WallLayerRules.EdgeKey(wid, other);
                    if (!siblingIds.Contains(other))
                    {
                        // The carrier keeps its own original neighbours; a produced layer
                        // wall has no business being joined to anything else.
                        if (wid != expected.CarrierId) foreignEdges.Add(key);
                        continue;
                    }
                    if (!wanted.Contains(key)) extraEdges.Add(key);
                    else found.Add(key);
                }
            }

            // Every edge the chain calls for must also be between walls that TOUCH.
            foreach (int[] edge in WallLayerRules.ChainEdges(ordered.Select(l => l.LayerIndex).ToList()))
            {
                WallLayerPlan a = expected.Plan.Layers.First(l => l.LayerIndex == edge[0]);
                WallLayerPlan b = expected.Plan.Layers.First(l => l.LayerIndex == edge[1]);
                if (!WallLayerRules.LayersTouch(a.ExpectedOffsetFeet, a.WidthFeet,
                                                b.ExpectedOffsetFeet, b.WidthFeet))
                    disjointEdges.Add(a.LayerNumberText + "-" + b.LayerNumberText);
            }

            var missing = wanted.Where(k => !found.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

            report.JoinCheck["chain_expected"] = new JArray(wanted.OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => (JToken)k));
            report.JoinCheck["chain_found"] = new JArray(found.OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => (JToken)k));
            report.JoinCheck["chain_missing"] = new JArray(missing.Select(k => (JToken)k));
            report.JoinCheck["chain_extra"] = new JArray(extraEdges.Distinct().Select(k => (JToken)k));
            report.JoinCheck["chain_foreign"] = new JArray(foreignEdges.Distinct().Select(k => (JToken)k));
            report.JoinCheck["chain_disjoint"] = new JArray(disjointEdges.Select(k => (JToken)k));
            report.JoinCheck["chain_intact"] = missing.Count == 0 && extraEdges.Count == 0 &&
                                               foreignEdges.Count == 0 && disjointEdges.Count == 0;

            if (disjointEdges.Count > 0)
            {
                report.Fail(WallSplitCodes.VerifyJoinDisjoint,
                    "the chain would join layers that do not touch: " + string.Join(", ", disjointEdges) +
                    ". A join across a gap is what Revit records 'joined but do not intersect' about.");
                return;
            }
            if (missing.Count > 0)
            {
                report.Fail(WallSplitCodes.VerifyJoinMissing,
                    "the chain is broken: " + string.Join(", ", missing) + " is not joined, so the carrier's " +
                    "openings would not reach every layer.");
                return;
            }
            if (extraEdges.Count > 0 || foreignEdges.Count > 0)
            {
                report.Fail(WallSplitCodes.VerifyJoinUnexpected,
                    "the model holds joins the chain does not call for: " +
                    string.Join(", ", extraEdges.Distinct().Concat(foreignEdges.Distinct())) + ".");
            }
        }

        // ---- provenance -------------------------------------------------------------------

        private static void VerifyProvenance(Document doc, WallSplitExpectation expected, Wall carrier,
                                             VerificationReport report)
        {
            WallSplitProvenance.Stamp carrierStamp = WallSplitProvenance.ReadStamp(carrier);
            report.ProvenanceCheck["carrier_stamped"] = carrierStamp.Present;

            if (!carrierStamp.Present)
            {
                report.Fail(WallSplitCodes.ProvenanceVerificationFailed,
                    "the carrier carries no provenance stamp, so nothing durable records that this wall was " +
                    "converted - and the next run would split it again.");
                return;
            }

            report.ProvenanceCheck["schema_version"] = carrierStamp.SchemaVersion;
            report.ProvenanceCheck["plan_fingerprint_matches"] =
                string.Equals(carrierStamp.PlanFingerprint, expected.PlanFingerprint, StringComparison.Ordinal);
            report.ProvenanceCheck["source_wall_unique_id_matches"] =
                string.Equals(carrierStamp.SourceWallUniqueId, expected.SourceWallUniqueId, StringComparison.Ordinal);
            report.ProvenanceCheck["original_wall_type_id_matches"] =
                string.Equals(carrierStamp.OriginalWallTypeId, expected.OriginalWallTypeId ?? "", StringComparison.Ordinal);
            report.ProvenanceCheck["role"] = carrierStamp.Role;
            report.ProvenanceCheck["layer_index"] = carrierStamp.LayerIndex;

            if (carrierStamp.SchemaVersion != WallSplitCodes.SchemaVersion ||
                report.ProvenanceCheck.Value<bool>("plan_fingerprint_matches") == false ||
                report.ProvenanceCheck.Value<bool>("source_wall_unique_id_matches") == false ||
                report.ProvenanceCheck.Value<bool>("original_wall_type_id_matches") == false ||
                carrierStamp.Role != LayerRole.CoreCarrier ||
                carrierStamp.LayerIndex != expected.Plan.CoreCarrierLayerIndex)
            {
                report.Fail(WallSplitCodes.ProvenanceVerificationFailed,
                    "the carrier's provenance stamp does not describe this conversion.");
                return;
            }

            // THE WHOLE FAMILY, not just the carrier. Reading only the carrier's fingerprint
            // cannot tell a finished conversion from one somebody deleted three walls out of.
            JObject siblings;
            string state = WallSplitProvenance.InspectSiblingSet(
                doc, carrier, ExtrasScan.SkippedByConstruction, null, out siblings);
            report.ProvenanceCheck["sibling_set"] = siblings;
            report.ProvenanceCheck["state"] = state;

            if (state != WallSplitCodes.AlreadySplit)
            {
                report.Fail(WallSplitCodes.VerifySiblingSetIncomplete,
                    "the set of walls this conversion produced is not complete and coherent: " + state + ". " +
                    siblings.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

        // ---- small helpers ------------------------------------------------------------------

        private static T Safe<T>(Func<T> read, T fallback)
        {
            try { return read(); } catch { return fallback; }
        }
    }
}
