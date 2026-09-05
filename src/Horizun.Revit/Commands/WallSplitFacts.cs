// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// READING a compound wall: the half of the decomposition that needs a Revit.
//
// WallLayerRules decides where every layer goes, what carries the doors and what
// each resulting type is called - and it does all of that without a Document, so
// it can be argued with at a desk. This file is the other half: it READS the
// live wall, MEASURES the things whose answer no documentation can settle, and
// takes the census of everything hanging off the wall before a transaction is
// opened.
//
// Two readings here are measurements rather than conventions, deliberately:
//
//   * THE EXTERIOR DIRECTION. Wall.Orientation is a documented convention, it is
//     not constant along an arc, and the previous implementation combined it with
//     wall.Flipped so the two corrections could cancel. Here the exterior normal
//     is taken from the wall's own EXTERIOR SHELL FACE - a normal computed off
//     the solid Revit actually built. It already contains the flip, because the
//     flip is what decides which face that is.
//
//   * WHAT THE WALL CARRIES. FindInserts plus GetDependentElements, and every
//     entry classified in the closed vocabulary. The previous implementation
//     looked only at FamilyInstances whose Host was the wall, which is why
//     sweeps, reveals, embedded curtain walls and hosted rebar went out with the
//     deletion without appearing in any report.
//
// Nothing here writes. The command opens the transaction.
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
    /// <summary>One thing that hangs off the wall, and how it is going to survive.</summary>
    public sealed class WallDependency
    {
        public long ElementId;
        public string UniqueId;
        public string Category;

        /// <summary>A member of DependencyKinds. Decides the disposition AND the verifier.</summary>
        public string Kind;

        public string Disposition;       // DependencyDisposition.*
        public string Note;

        /// <summary>
        /// What this dependency looked like before the wall was touched. Present for every
        /// kind with a verifier, which - by the coverage rule - is every kind that may be
        /// called preserved_by_identity.
        /// </summary>
        public DependencySnapshot Snapshot;

        public JObject ToJson() => new JObject
        {
            ["element_id"] = ElementId,
            ["unique_id"] = UniqueId,
            ["category"] = Category,
            ["kind"] = Kind,
            ["disposition"] = Disposition,
            ["has_verifier"] = DependencyKinds.HasVerifier(Kind),
            ["snapshot_taken"] = Snapshot != null,
            ["note"] = Note
        };
    }

    /// <summary>
    /// The BEFORE state of one dependency, whatever kind it is. One type rather than seven
    /// because the executor iterates a single list and dispatches on Kind; the fields that
    /// do not apply to a kind stay null and its verifier never reads them.
    /// </summary>
    public sealed class DependencySnapshot
    {
        public string Kind;
        public long ElementId;
        public string UniqueId;
        public long CategoryId;
        public long TypeId;
        public string TypeName;
        public long HostId;
        public long OwnerViewId;
        public Dictionary<string, string> Parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Family instances: the full insert snapshot.</summary>
        public InsertSnapshot Insert;

        // ---- Opening ----
        public bool OpeningIsRectangular;
        public int OpeningCurveCount;
        public double OpeningBoundaryLengthFeet;
        public List<XYZ> OpeningBoundaryPoints = new List<XYZ>();

        // ---- WallSweep / Reveal ----
        public string SweepType;
        public long SweepProfileId;
        public double SweepDistanceFeet;
        public double SweepWallOffsetFeet;
        public bool SweepIsVertical;
        public string SweepWallSide;
        public List<long> SweepHostIds = new List<long>();

        /// <summary>
        /// WHERE IT IS, in model space. Distance and WallOffset are measured from the host,
        /// so they cannot show that a sweep failed to follow a host that moved.
        /// </summary>
        public BoundingBoxXYZ SweepBounds;

        // ---- embedded / dependent wall ----
        public long WallBaseLevelId;
        public long WallTopLevelId;
        public double WallBaseOffsetFeet;
        public double WallTopOffsetFeet;
        public string WallCurveDigest;
        public bool WallIsCurtain;

        // ---- Dimension ----
        public int ReferenceCount;
        public List<string> ReferenceRepresentations = new List<string>();
        public double DimensionValueFeet;
        public bool DimensionValueRead;

        // ---- WallFoundation ----
        public long FoundationWallId;
        public long FoundationLevelId;
        public double FoundationOffsetFeet;
        public string FoundationCurveDigest;
        public BoundingBoxXYZ FoundationBounds;

        // ---- Rebar ----
        //
        // The FACTS come from RebarFacts.Describe, which is the reading algorithm this
        // bridge already has and which is not reimplemented here. What is stored is the
        // subset that has to be COMPARED, extracted from that reply.
        public JObject RebarDescription;
        public long RebarHostId;
        public long RebarBarTypeId;
        public long RebarShapeId;
        public string RebarStyle;
        public string RebarLayoutRule;
        public int RebarNumberOfPositions;
        public double RebarQuantity;
        public double RebarArrayLengthMm;
        public string RebarNormalDigest;
        public List<string> RebarPositionDigests = new List<string>();
        public List<double[]> RebarCentrelinePointsMm = new List<double[]>();
        public string RebarTerminationsDigest;
        public bool RebarIncludeFirst;
        public bool RebarIncludeLast;

        /// <summary>Whether every bar position sat inside the HOST before the conversion.</summary>
        public string RebarContainmentBefore;

        // ---- reinforcement systems (area, path, fabric, container) ----
        public long SystemHostId;
        public long SystemTypeId;
        public List<long> SystemMemberIds = new List<long>();
        public List<string> SystemMemberUniqueIds = new List<string>();
        public List<long> SystemBoundaryIds = new List<long>();
        public int SystemNumberOfLines;
        public string SystemDirectionDigest;
        public string SystemLayersDigest;

        // ---- Tag ----
        //
        // A tag can point at SEVERAL elements, and at elements in a LINK. Keeping only the
        // first local id - which is what the first version did - meant a multi-reference tag
        // could lose every reference but one and still verify.
        public List<long> TaggedElementIds = new List<long>();
        public List<string> TaggedUniqueIds = new List<string>();
        public int TaggedReferenceCount;
        public bool TagHasNonLocalReference;
        public XYZ TagHeadPosition;
    }

    /// <summary>
    /// What one hosted instance looked like BEFORE the wall was touched. Every field is
    /// re-read after the commit and compared; the previous implementation captured five
    /// of these and recreated the door from them, which is how sill heights, phases,
    /// worksets and every project parameter were lost without a word.
    /// </summary>
    public sealed class InsertSnapshot
    {
        public long ElementId;
        public string UniqueId;
        public long SymbolId;
        public long HostId;
        public long LevelId;
        public XYZ Point;
        public bool HandFlipped;
        public bool FacingFlipped;
        public bool Mirrored;
        public XYZ FacingOrientation;
        public long PhaseCreated;
        public long PhaseDemolished;
        public int WorksetId;
        public long DesignOptionId;
        public bool Pinned;
        public double Rotation;
        public bool RotationRead;
        public int SubComponentCount;
        public List<long> SubComponentSymbolIds = new List<long>();
        public List<string> SubComponentUniqueIds = new List<string>();
        public BoundingBoxXYZ Bounds;

        // NOTE. There is deliberately NO Parameters dictionary here. The enclosing
        // DependencySnapshot already captures every parameter by stable key, and that is
        // the copy both the fingerprint and the verifier read. A second copy on this type
        // was populated and never read by anything - dead duplicated state that the next
        // person would reasonably have believed was the compared set.
    }

    /// <summary>Everything read off one live wall, plus the plan the pure rules produced.</summary>
    public sealed class WallSplitSubject
    {
        public Wall Wall;
        public long ElementId;
        public string UniqueId;

        public WallAssemblyFacts Assembly;
        public WallSplitPlan Plan;

        public Curve LocationCurve;
        public string CurveClass;
        public bool Flipped;

        /// <summary>Measured off the exterior shell face. Points away from the exterior side.</summary>
        public XYZ ExteriorNormal;
        public string NormalSource;

        /// <summary>
        /// Whether the SECOND, independent source - Wall.Orientation - agrees with the face
        /// measurement. It has to be checked here, because nothing downstream can: the
        /// executor places the layers along this vector and the verifier measures them along
        /// the SAME vector, so a flipped normal would build the wall inside-out and verify
        /// it as correct.
        /// </summary>
        public bool NormalCorroborated;
        public double NormalAgreement;

        /// <summary>Arc walls only: +1 when the exterior side is radially outward, -1 when inward.</summary>
        public int ArcSign;

        public List<WallDependency> Dependencies = new List<WallDependency>();

        /// <summary>Everything about how this wall meets its neighbours, captured to be restored.</summary>
        public WallJoinFacts Joins = new WallJoinFacts();

        public bool Pinned;
        public string BaseConstraint;
        public string TopConstraint;

        /// <summary>Contrast only: what CompoundStructure.GetOffsetForLocationLine reports.</summary>
        public double ReportedLocationOffsetFeet;
        public bool ReportedLocationOffsetRead;

        public WallSplitRejection Rejection;
        public string PlanFingerprint;

        /// <summary>
        /// Set when this wall already came out of a split: already_split,
        /// repairable_partial_state, existing_plan_conflict or provenance_invalid. It is
        /// read BEFORE anything is planned, because a converted carrier is single-layer and
        /// planning it first would refuse it as single_layer and never look at the stamp.
        /// </summary>
        public string ProvenanceState;

        /// <summary>The full sibling-set inspection behind <see cref="ProvenanceState"/>.</summary>
        public JObject ProvenanceReport;

        /// <summary>The carrier of the conversion this wall belongs to, found from any member.</summary>
        public long CarrierElementId;

        /// <summary>True when the caller named a SECONDARY layer wall rather than the carrier.</summary>
        public bool SelectedSecondarySibling;

        public bool AlreadyConverted => ProvenanceState != null;

        public bool Eligible => Rejection == null && !AlreadyConverted && Plan != null && Plan.Eligible;
    }

    /// <summary>
    /// How a wall meets its neighbours, in enough detail to RESTORE it and then prove the
    /// restoration. The first version of this capability captured a list of joined ids and
    /// never used it, which meant the end joins were silently lost exactly as they were in
    /// the implementation it replaced.
    /// </summary>
    public sealed class WallJoinFacts
    {
        /// <summary>Everything JoinGeometryUtils reports as joined to this wall, sorted.</summary>
        public List<long> GeometricJoinIds = new List<long>();

        /// <summary>Whether the wall is CUT BY each of them, so the relationship is restored the same way round.</summary>
        public Dictionary<long, bool> CutByOther = new Dictionary<long, bool>();

        /// <summary>Per end (0 and 1): whether Revit is allowed to join the wall there at all.</summary>
        public bool JoinAllowedAtEnd0 = true;
        public bool JoinAllowedAtEnd1 = true;
        public bool EndFlagsRead;

        /// <summary>Per end: the elements Revit reports meeting the wall there, in order.</summary>
        public List<long> ElementsAtEnd0 = new List<long>();
        public List<long> ElementsAtEnd1 = new List<long>();
        public bool ElementsAtJoinRead;

        public JObject ToJson() => new JObject
        {
            ["geometric_join_ids"] = new JArray(GeometricJoinIds),
            ["join_allowed_at_end_0"] = JoinAllowedAtEnd0,
            ["join_allowed_at_end_1"] = JoinAllowedAtEnd1,
            ["end_flags_read"] = EndFlagsRead,
            ["elements_at_end_0"] = new JArray(ElementsAtEnd0),
            ["elements_at_end_1"] = new JArray(ElementsAtEnd1),
            ["elements_at_join_read"] = ElementsAtJoinRead
        };
    }

    /// <summary>
    /// WHO POINTS AT THIS WALL, found by asking the annotations rather than by asking the
    /// wall.
    ///
    /// Wall.FindInserts and GetDependentElements answer "what does Revit consider dependent
    /// on this element", and that is not the same question as "what would break if this
    /// element changed". A dimension witnessing a wall face and a tag pointing at it do not
    /// reliably come back from either, so relying on them alone leaves exactly the two
    /// classes whose loss is invisible until somebody opens a sheet.
    ///
    /// Built ONCE per command invocation and shared across every wall in the batch: it is a
    /// document-wide scan, and doing it per wall would turn a fifty-wall batch into fifty
    /// scans of the same two collectors.
    /// </summary>
    public sealed class WallReverseCensus
    {
        private readonly Dictionary<long, List<ElementId>> _byWall = new Dictionary<long, List<ElementId>>();

        /// <summary>References this capability could not interpret at all, by owner id.</summary>
        private readonly Dictionary<long, List<string>> _uninterpretable = new Dictionary<long, List<string>>();

        public bool ScanRan { get; private set; }
        public string ScanFailure { get; private set; }

        public static WallReverseCensus Build(Document doc)
        {
            var census = new WallReverseCensus();
            try
            {
                foreach (Dimension dimension in new FilteredElementCollector(doc)
                             .OfClass(typeof(Dimension)).Cast<Dimension>())
                    census.IndexDimension(dimension);

                foreach (IndependentTag tag in new FilteredElementCollector(doc)
                             .OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                    census.IndexTag(tag);

                census.ScanRan = true;
            }
            catch (Exception ex)
            {
                // A scan that could not run is NOT a scan that found nothing, and the
                // difference reaches the caller rather than being flattened into an empty
                // result.
                census.ScanRan = false;
                census.ScanFailure = ex.Message;
            }
            return census;
        }

        private void IndexDimension(Dimension dimension)
        {
            try
            {
                ReferenceArray references = dimension.References;
                if (references == null) return;
                foreach (Reference reference in references)
                {
                    if (reference == null) continue;
                    long owner = OwnerOf(reference);
                    if (owner > 0) Add(owner, dimension.Id);
                    else Uninterpretable(dimension.Id, "a dimension reference whose owning element cannot be read");
                }
            }
            catch { Uninterpretable(dimension.Id, "the dimension's references could not be read"); }
        }

        private void IndexTag(IndependentTag tag)
        {
            bool sawAny = false;
            try
            {
                foreach (ElementId id in tag.GetTaggedLocalElementIds())
                {
                    long raw = Rid.Value(id);
                    if (raw <= 0) continue;
                    sawAny = true;
                    Add(raw, tag.Id);
                }
            }
            catch { Uninterpretable(tag.Id, "the tag's tagged elements could not be read"); return; }

            try
            {
                IList<Reference> references = tag.GetTaggedReferences();
                int count = references == null ? 0 : references.Count;
                if (count > 0 && !sawAny)
                    Uninterpretable(tag.Id, "the tag has " + count + " reference(s) and none resolves to a local " +
                                            "element - it points into a link or at something this capability " +
                                            "cannot follow");
            }
            catch { }
        }

        private static long OwnerOf(Reference reference)
        {
            try
            {
                // A reference into a LINK names the link instance locally and the real
                // element inside it; neither is this wall, and pretending otherwise would
                // attach somebody else's dimension to it.
                if (Rid.Value(reference.LinkedElementId) > 0) return 0;
                return Rid.Value(reference.ElementId);
            }
            catch { return 0; }
        }

        private void Add(long wallId, ElementId dependent)
        {
            if (!_byWall.TryGetValue(wallId, out List<ElementId> list))
            {
                list = new List<ElementId>();
                _byWall[wallId] = list;
            }
            if (!list.Contains(dependent)) list.Add(dependent);
        }

        private void Uninterpretable(ElementId owner, string why)
        {
            long raw = Rid.Value(owner);
            if (!_uninterpretable.TryGetValue(raw, out List<string> list))
            {
                list = new List<string>();
                _uninterpretable[raw] = list;
            }
            if (!list.Contains(why)) list.Add(why);
        }

        public IEnumerable<ElementId> For(long wallId)
            => _byWall.TryGetValue(wallId, out List<ElementId> list) ? list : Enumerable.Empty<ElementId>();

        public IReadOnlyList<string> WhyUninterpretable(long elementId)
            => _uninterpretable.TryGetValue(elementId, out List<string> list) ? list : new List<string>();

        public bool IsUninterpretable(long elementId) => _uninterpretable.ContainsKey(elementId);
    }

    /// <summary>Reads walls. Writes nothing.</summary>
    public static class WallSplitFacts
    {
        /// <summary>
        /// Read one wall end to end: its assembly, its geometry, its census, and whether
        /// anything about it makes the decomposition unsafe. A rejection here is final and
        /// happens BEFORE any transaction exists, which is the whole point of the phase.
        /// </summary>
        public static WallSplitSubject Read(Document doc, Wall wall, string documentKey, string coreCarrierPolicy,
                                            bool allowArcWalls, WallReverseCensus reverse = null,
                                            WallProvenanceIndex provenance = null)
        {
            var subject = new WallSplitSubject
            {
                Wall = wall,
                ElementId = Rid.Value(wall.Id),
                UniqueId = SafeUniqueId(wall)
            };

            // ---- 0. HAS THIS ALREADY BEEN SPLIT? -------------------------------------
            //
            // FIRST, before eligibility and before planning, and the order is the whole
            // point. After a conversion the carrier is a SINGLE-LAYER wall, so a second
            // call that planned first would refuse it as `single_layer` and never reach the
            // provenance check at all - which is exactly what made the contract's promise of
            // `already_split` unreachable from the public flow.
            //
            // It answers for any member of the set, not only the carrier: somebody who
            // selects a finish layer gets the same diagnosis as somebody who selects the
            // core, because the carrier is found from the siblings list.
            if (ReadProvenanceState(doc, wall, subject, provenance)) return subject;

            // ---- 1. eligibility that needs Revit, before anything is computed ----------
            subject.Rejection = ReadBlockingConditions(doc, wall, allowArcWalls, subject);
            if (subject.Rejection != null) return subject;

            // ---- 2. the assembly, handed to the pure rules -----------------------------
            subject.Assembly = ReadAssembly(doc, wall, subject);
            subject.Plan = WallLayerRules.Plan(subject.Assembly);
            if (!subject.Plan.Eligible)
            {
                subject.Rejection = subject.Plan.Rejection;
                return subject;
            }

            // ---- 3. geometry: the exterior direction is MEASURED -----------------------
            subject.ExteriorNormal = MeasureExteriorNormal(wall, subject.LocationCurve, out string source);
            subject.NormalSource = source;
            if (subject.ExteriorNormal == null)
            {
                subject.Rejection = new WallSplitRejection(WallSplitCodes.UnsupportedCurve,
                    "the exterior direction of this wall could not be measured off its solid and Wall.Orientation " +
                    "did not answer either. Every layer offset is measured along that direction, so guessing it " +
                    "would place the layers on a side nobody checked.");
                return subject;
            }

            // ---- 3b. CORROBORATE IT ---------------------------------------------------
            //
            // I2 cannot catch a wrong normal: the layers are PLACED along it and MEASURED
            // along it, so a flip would agree with itself. Wall.Orientation is a second,
            // independent source; when the two disagree, which side is outside is simply not
            // established and the wall is refused rather than built from a coin toss.
            if (!CorroborateNormal(wall, subject))
            {
                subject.Rejection = new WallSplitRejection(WallSplitCodes.UnsupportedCurve,
                    "the wall's exterior shell face and Wall.Orientation disagree about which side is outside " +
                    "(agreement " + subject.NormalAgreement.ToString("F3") + "). Every layer offset is measured " +
                    "along that direction, and it is measured along the SAME direction afterwards - so a wrong " +
                    "answer here would build the wall inside-out and verify it as correct. Refused instead.");
                return subject;
            }

            if (subject.LocationCurve is Arc arc)
            {
                XYZ mid = arc.Evaluate(0.5, true);
                XYZ radial = mid.Subtract(arc.Center);
                double dot = radial.DotProduct(subject.ExteriorNormal);
                if (Math.Abs(dot) < 1e-9)
                {
                    subject.Rejection = new WallSplitRejection(WallSplitCodes.UnsupportedCurve,
                        "this arc wall's exterior normal is perpendicular to its radius at the midpoint, so which " +
                        "side is outside cannot be decided. Refused rather than guessed.");
                    return subject;
                }
                subject.ArcSign = dot > 0 ? 1 : -1;

                // A layer whose radius would collapse through the centre is not a wall.
                foreach (WallLayerPlan layer in subject.Plan.Layers.Where(l => l.Materialised))
                {
                    double radius = arc.Radius + subject.ArcSign * layer.ExpectedOffsetFeet;
                    if (radius <= WallLayerRules.ToleranceFeet)
                    {
                        subject.Rejection = new WallSplitRejection(WallSplitCodes.DegenerateLayerRadius,
                            "layer " + layer.LayerNumberText + " (" + layer.MaterialName + ") would need radius " +
                            WallLayerRules.FeetToMm(radius).ToString("F1") + " mm on an arc of radius " +
                            WallLayerRules.FeetToMm(arc.Radius).ToString("F1") + " mm - it would collapse through " +
                            "the centre. Refused; nothing was written.");
                        return subject;
                    }
                }
            }

            // ---- 4. the census ---------------------------------------------------------
            subject.Dependencies = TakeCensus(doc, wall, reverse);
            WallDependency blocking = subject.Dependencies
                .FirstOrDefault(d => d.Disposition == DependencyDisposition.UnsupportedBlocking);
            if (blocking != null)
            {
                subject.Rejection = new WallSplitRejection(WallSplitCodes.UnsupportedDependency,
                    "this wall carries " + blocking.Kind + " " + blocking.ElementId + " (" + blocking.Category +
                    "), and equivalence for it cannot be guaranteed: " + blocking.Note + ". The wall is refused " +
                    "before any transaction is opened rather than converted with a known loss.");
                return subject;
            }

            subject.Joins = ReadJoins(doc, wall);
            subject.PlanFingerprint = Fingerprint(documentKey, subject, coreCarrierPolicy);
            return subject;
        }

        /// <summary>
        /// Read the durable mark this capability leaves, and decide what a second call
        /// should say. Returns true when the wall is already part of a conversion and
        /// nothing further should be planned or written for it.
        /// </summary>
        private static bool ReadProvenanceState(Document doc, Wall wall, WallSplitSubject subject,
                                                WallProvenanceIndex provenance)
        {
            WallSplitProvenance.Stamp stamp = WallSplitProvenance.ReadStamp(wall);
            if (!stamp.Present)
            {
                subject.ProvenanceState = null;   // not_split: carry on and plan it
                return false;
            }

            subject.SelectedSecondarySibling =
                !string.Equals(stamp.Role, LayerRole.CoreCarrier, StringComparison.Ordinal);

            // Any member answers for the whole conversion, so the diagnosis is taken from
            // the carrier however the caller reached it.
            Element carrier = WallSplitProvenance.FindCarrier(doc, wall);
            if (carrier == null)
            {
                subject.CarrierElementId = 0;
                subject.ProvenanceState = WallSplitCodes.RepairablePartialState;
                subject.ProvenanceReport = new JObject
                {
                    ["state"] = WallSplitCodes.RepairablePartialState,
                    ["queried_element_id"] = subject.ElementId,
                    ["queried_role"] = stamp.Role,
                    ["problem"] = "this wall carries the stamp of a conversion whose core carrier is not in the " +
                                  "model any more, so the set cannot be inspected from here."
                };
                subject.Rejection = new WallSplitRejection(WallSplitCodes.RepairablePartialState,
                    subject.ProvenanceReport.Value<string>("problem"));
                return true;
            }

            subject.CarrierElementId = Rid.Value(carrier.Id);

            JObject report;
            // A SECOND CALL is exactly where the "is there another wall carrying this plan"
            // question has a real answer, so it is asked here - from the index built once
            // for the whole invocation.
            string state = WallSplitProvenance.InspectSiblingSet(
                doc, carrier, ExtrasScan.Indexed, provenance, out report);
            subject.ProvenanceState = state;
            subject.ProvenanceReport = report;

            string message;
            switch (state)
            {
                case WallSplitCodes.AlreadySplit:
                    message = subject.SelectedSecondarySibling
                        ? "this is layer " + stamp.LayerIndex + " of a wall that has already been split. Its core " +
                          "carrier is element " + subject.CarrierElementId + " and the whole set is present and " +
                          "coherent, so nothing was planned and nothing would be written."
                        : "this wall is already the core carrier of a completed split, and every wall the " +
                          "conversion produced is present and coherent. Nothing was planned and nothing would be " +
                          "written - a second call does not duplicate it.";
                    break;

                case WallSplitCodes.RepairablePartialState:
                    message = "this wall belongs to a split whose set of walls is NOT complete and coherent. " +
                              "Repairing it is not something this call may decide: it needs its own dry run. See " +
                              "the provenance report for exactly which sibling is missing, unstamped, duplicated, " +
                              "wrongly roled, re-typed, or belongs to another conversion.";
                    break;

                case WallSplitCodes.ProvenanceInvalid:
                    message = "this wall carries a provenance stamp this build cannot act on. It is not a partial " +
                              "state to repair; it is a record nobody can interpret.";
                    break;

                default:
                    message = "this wall carries the provenance of a different plan. Replacing that is not " +
                              "something this call may decide.";
                    state = WallSplitCodes.ExistingPlanConflict;
                    subject.ProvenanceState = state;
                    break;
            }

            subject.Rejection = new WallSplitRejection(state, message);
            return true;
        }

        // ---- eligibility ----------------------------------------------------------

        private static WallSplitRejection ReadBlockingConditions(Document doc, Wall wall, bool allowArcWalls,
                                                                 WallSplitSubject subject)
        {
            // Worksharing: an element somebody else owns cannot be converted, and finding
            // that out from a failed Delete halfway through is how duplicates were left
            // behind.
            if (doc.IsWorkshared)
            {
                try
                {
                    CheckoutStatus status = WorksharingUtils.GetCheckoutStatus(doc, wall.Id);
                    if (status == CheckoutStatus.OwnedByOtherUser)
                        return new WallSplitRejection(WallSplitCodes.ElementNotEditable,
                            "this wall is owned by another user in the central model. Nothing was attempted.");
                }
                catch (Exception ex)
                {
                    return new WallSplitRejection(WallSplitCodes.ElementNotEditable,
                        "the checkout status of this wall could not be read (" + ex.Message + "), so whether it is " +
                        "editable is unknown. Refused rather than found out halfway through a conversion.");
                }
            }

            if (wall.GroupId != null && Rid.Value(wall.GroupId) > 0)
                return new WallSplitRejection(WallSplitCodes.UnsupportedGroupMember,
                    "this wall belongs to group " + Rid.Value(wall.GroupId) + ". New walls cannot be added to a " +
                    "group definition from here, so the layers would land outside the group and the group would " +
                    "no longer describe the assembly.");

            try
            {
                DesignOption option = wall.DesignOption;
                if (option != null)
                    return new WallSplitRejection(WallSplitCodes.UnsupportedDesignOption,
                        "this wall lives in design option '" + option.Name + "'. The layer walls would be created " +
                        "in whatever option is active, which is not necessarily this one.");
            }
            catch { /* no design options in this model */ }

            // Slanted and tapered walls are not described by a plan offset at all.
            //
            // READ THE TYPED PROPERTY, NEVER THE RAW INTEGER. This was `cross != 0` against
            // BuiltInParameter.WALL_CROSS_SECTION, on the assumption that 0 meant vertical.
            // Measured on Revit 2023 through 2027, the enum is SingleSlanted=0, Vertical=1,
            // Tapered=2 - so that test refused EVERY ordinary wall as "slanted" and, far
            // worse, would have ACCEPTED a genuinely slanted one, which is the case the
            // refusal exists to prevent. The live campaign found it on its first wall.
            WallCrossSection? section = null;
            try { section = wall.CrossSection; } catch { section = null; }

            if (section == null)
                return new WallSplitRejection(WallSplitCodes.UnsupportedCrossSection,
                    "this wall's cross section could not be read, so whether it is vertical is unknown. Unknown is " +
                    "not vertical: a slanted or tapered wall's layers are not described by an offset in plan.");

            if (section.Value != WallCrossSection.Vertical)
                return new WallSplitRejection(WallSplitCodes.UnsupportedCrossSection,
                    "this wall's cross section is " + section.Value + ". Its layers are not described by an offset " +
                    "in plan, so decomposing it with one would rebuild it as a vertical wall and report success.");

            if (ReadInt(wall, BuiltInParameter.WALL_TOP_IS_ATTACHED, 0) != 0 ||
                ReadInt(wall, BuiltInParameter.WALL_BOTTOM_IS_ATTACHED, 0) != 0)
                return new WallSplitRejection(WallSplitCodes.UnsupportedAttachedWall,
                    "this wall is attached at its top or base. There is no public API to attach the layer walls to " +
                    "the same targets, so they would be created unattached while the carrier stayed attached.");

            try
            {
                ElementId sketch = wall.SketchId;
                if (sketch != null && Rid.Value(sketch) > 0)
                    return new WallSplitRejection(WallSplitCodes.UnsupportedEditedProfile,
                        "this wall has an edited elevation profile. The carrier would keep it by identity, but the " +
                        "layer walls would be plain rectangles - an assembly that does not describe the building.");
            }
            catch { /* SketchId unavailable: fall through, the geometry checks still apply */ }

            var location = wall.Location as LocationCurve;
            if (location == null || location.Curve == null)
                return new WallSplitRejection(WallSplitCodes.UnsupportedCurve,
                    "this wall has no location curve, so there is nothing to offset.");

            // AN INDEPENDENT COPY, not the live reference.
            //
            // LocationCurve.Curve hands back a wrapper over geometry Revit owns. The moment
            // the wall's location curve is REPLACED - which is exactly what converting the
            // carrier does - that wrapper is stale, and every later read off it throws. The
            // executor computes the carrier's target curve BEFORE the conversion and the
            // secondary layers' curves AFTER it, so with a live reference the first worked
            // and every one of the others failed with "layer NN's curve could not be built".
            // Measured on Revit 2026: eleven live cases, all of them.
            // AND THERE IS NO FALLBACK TO THE LIVE ONE. The first version of this fix
            // caught the failure and assigned location.Curve - the very reference whose
            // staleness is the defect being fixed. A fallback that reinstates the bug is
            // worse than no fallback: it makes the failure rare, and a rare version of this
            // failure is one that reaches somebody's model instead of a test.
            //
            // If Revit will not hand over an independent copy, the wall is REFUSED here,
            // before a transaction exists.
            Curve detached = null;
            string detachFailure = null;
            try { detached = location.Curve.CreateTransformed(Transform.Identity); }
            catch (Exception ex) { detachFailure = ex.Message; }

            if (detached == null)
                return new WallSplitRejection(WallSplitCodes.UnsupportedCurve,
                    "an independent copy of this wall's location curve could not be made" +
                    (detachFailure == null ? "" : " (" + detachFailure + ")") +
                    ". Every layer's position is derived from that curve AFTER the carrier has been converted, and " +
                    "converting the carrier replaces the live one - so working from the live reference is the " +
                    "defect this refusal exists to prevent. Nothing was written.");

            // EVERY FACT BELOW COMES OFF `detached`, NEVER OFF `location.Curve` AGAIN.
            // The single read above is the only one this method is allowed: it exists to
            // produce the copy. Reading the live wrapper afterwards is not a crash today -
            // nothing has been written at this point - but it is the same habit that caused
            // the defect, and a habit that survives in four places is a defect waiting for
            // the fifth. The class, the length and the curve KIND all decide whether this
            // wall is accepted, so they must describe the object the executor will actually
            // use. If the two ever disagreed, the checks would be vouching for a curve that
            // is not the one being split.
            subject.LocationCurve = detached;
            subject.CurveClass = detached.GetType().Name;
            subject.Flipped = wall.Flipped;
            subject.Pinned = wall.Pinned;
            subject.BaseConstraint = ReadIdText(wall, BuiltInParameter.WALL_BASE_CONSTRAINT);
            subject.TopConstraint = ReadIdText(wall, BuiltInParameter.WALL_HEIGHT_TYPE);

            if (detached.Length < WallLayerRules.ToleranceFeet)
                return new WallSplitRejection(WallSplitCodes.UnsupportedCurve,
                    "this wall's location curve is shorter than the tolerance - it is degenerate geometry.");

            if (detached is Line) return null;

            if (detached is Arc)
            {
                if (!allowArcWalls)
                    return new WallSplitRejection(WallSplitCodes.UnsupportedCurve,
                        "this is an arc wall and allow_arc_walls was set to false.");
                return null;
            }

            return new WallSplitRejection(WallSplitCodes.UnsupportedCurve,
                "this wall's centreline is a " + subject.CurveClass + ". Only straight walls and circular arcs " +
                "have an offset this capability can build exactly; a spline or an ellipse would have to be " +
                "approximated, and an approximated wall is a moved wall. Refused, never straightened.");
        }

        // ---- the assembly ---------------------------------------------------------

        private static WallAssemblyFacts ReadAssembly(Document doc, Wall wall, WallSplitSubject subject)
        {
            WallType type = wall.WallType;
            var facts = new WallAssemblyFacts
            {
                WallTypeName = SafeName(type),
                WallTypeUniqueId = SafeUniqueId(type),
                WallKind = type == null ? "Unknown" : type.Kind.ToString(),
                LocationLine = ReadLocationLine(wall),
                CoreFirstIndex = -1,
                CoreLastIndex = -1
            };

            CompoundStructure cs = type == null ? null : type.GetCompoundStructure();
            if (cs == null) return facts;

            try { facts.CoreFirstIndex = cs.GetFirstCoreLayerIndex(); } catch { facts.CoreFirstIndex = -1; }
            try { facts.CoreLastIndex = cs.GetLastCoreLayerIndex(); } catch { facts.CoreLastIndex = -1; }
            try { facts.OpeningWrapping = cs.OpeningWrapping.ToString(); } catch { }
            try { facts.EndCap = cs.EndCap.ToString(); } catch { }

            int variableIndex = -1;
            try { variableIndex = cs.VariableLayerIndex; } catch { }

            IList<CompoundStructureLayer> layers = cs.GetLayers();
            for (int i = 0; i < layers.Count; i++)
            {
                CompoundStructureLayer layer = layers[i];
                Element material = layer.MaterialId != null && Rid.Value(layer.MaterialId) > 0
                    ? doc.GetElement(layer.MaterialId)
                    : null;

                facts.Layers.Add(new WallLayerFacts
                {
                    Index = i,
                    WidthFeet = layer.Width,
                    MaterialUniqueId = material == null ? null : SafeUniqueId(material),
                    MaterialName = material == null ? null : SafeName(material),
                    Function = layer.Function.ToString(),
                    IsVariableWidth = i == variableIndex,
                    DeckProfileUniqueId = ReadDeckProfile(doc, layer),
                    DeckEmbeddingType = ReadDeckEmbedding(layer)
                });
            }

            // Contrast only: what Revit itself reports for this location line. It is
            // recorded and never used as the source, because its sign convention is
            // exactly the thing the mandate asks to be settled by measurement.
            try
            {
                var line = (WallLocationLine)ReadInt(wall, BuiltInParameter.WALL_KEY_REF_PARAM, 0);
                subject.ReportedLocationOffsetFeet = cs.GetOffsetForLocationLine(line);
                subject.ReportedLocationOffsetRead = true;
            }
            catch { subject.ReportedLocationOffsetRead = false; }

            return facts;
        }

        private static string ReadLocationLine(Wall wall)
        {
            try { return ((WallLocationLine)ReadInt(wall, BuiltInParameter.WALL_KEY_REF_PARAM, -1)).ToString(); }
            catch { return null; }
        }

        private static string ReadDeckProfile(Document doc, CompoundStructureLayer layer)
        {
            try
            {
                if (layer.DeckProfileId == null || Rid.Value(layer.DeckProfileId) <= 0) return null;
                Element profile = doc.GetElement(layer.DeckProfileId);
                return profile == null ? null : SafeUniqueId(profile);
            }
            catch { return null; }
        }

        private static string ReadDeckEmbedding(CompoundStructureLayer layer)
        {
            try { return layer.DeckEmbeddingType.ToString(); } catch { return null; }
        }

        // ---- the measured exterior normal -----------------------------------------

        /// <summary>
        /// The direction "outwards from the exterior side", taken from the wall's own
        /// exterior shell face. A face normal computed off the solid Revit built is a
        /// measurement; Wall.Orientation combined with wall.Flipped is two conventions
        /// that can cancel, which is how a wall came out inside-out.
        ///
        /// Falls back to Wall.Orientation and SAYS SO, so a reader can tell which of the
        /// two answered.
        /// </summary>
        private static XYZ MeasureExteriorNormal(Wall wall, Curve curve, out string source)
        {
            source = null;
            try
            {
                IList<Reference> faces = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior);
                XYZ probe = curve == null ? null : curve.Evaluate(0.5, true);

                foreach (Reference reference in faces ?? new List<Reference>())
                {
                    if (!(wall.GetGeometryObjectFromReference(reference) is Face face)) continue;

                    UV uv = null;
                    if (probe != null)
                    {
                        IntersectionResult hit = face.Project(probe);
                        if (hit != null) uv = hit.UVPoint;
                    }
                    if (uv == null)
                    {
                        BoundingBoxUV box = face.GetBoundingBox();
                        if (box == null) continue;
                        uv = new UV((box.Min.U + box.Max.U) / 2.0, (box.Min.V + box.Max.V) / 2.0);
                    }

                    XYZ normal = face.ComputeNormal(uv);
                    if (normal == null || normal.IsZeroLength()) continue;

                    // The wall stands vertically: only the horizontal part is the side
                    // direction, and a top or bottom face would otherwise answer.
                    var flat = new XYZ(normal.X, normal.Y, 0);
                    if (flat.GetLength() < 1e-6) continue;

                    source = "exterior_shell_face";
                    return flat.Normalize();
                }
            }
            catch { /* fall through to the convention, reported as such */ }

            try
            {
                XYZ orientation = wall.Orientation;
                if (orientation != null && !orientation.IsZeroLength())
                {
                    var flat = new XYZ(orientation.X, orientation.Y, 0);
                    if (flat.GetLength() >= 1e-6)
                    {
                        source = "orientation_fallback";
                        return flat.Normalize();
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Do the two independent sources agree? Returns false when they demonstrably do not.
        ///
        /// When the face measurement was unavailable and Orientation WAS the source, there is
        /// only one source and nothing to corroborate; that is recorded rather than dressed
        /// up as agreement.
        /// </summary>
        private static bool CorroborateNormal(Wall wall, WallSplitSubject subject)
        {
            subject.NormalCorroborated = false;
            subject.NormalAgreement = double.NaN;

            if (subject.NormalSource == "orientation_fallback")
            {
                // One source. Not corroborated, and it says so - but not a refusal either,
                // because Orientation is the documented answer and the verifier still measures
                // the result. What is refused is DISAGREEMENT, not single-sourcing.
                return true;
            }

            try
            {
                XYZ orientation = wall.Orientation;
                if (orientation == null || orientation.IsZeroLength()) return true;

                var flat = new XYZ(orientation.X, orientation.Y, 0);
                if (flat.GetLength() < 1e-6) return true;

                subject.NormalAgreement = flat.Normalize().DotProduct(subject.ExteriorNormal);

                // A straight wall should agree almost exactly; a curved one varies along its
                // length, so the test is which SIDE, not which angle.
                subject.NormalCorroborated = subject.NormalAgreement > 0;
                return subject.NormalCorroborated;
            }
            catch
            {
                // Could not ask the second source. One source, recorded as such.
                return true;
            }
        }

        // ---- the census -----------------------------------------------------------

        /// <summary>
        /// Everything hanging off the wall, classified. The carrier is never deleted, so
        /// almost everything here is preserved_by_identity - which is the point of the
        /// strategy, not an excuse to skip the check: every entry is re-read after the
        /// commit regardless of how it is classified.
        /// </summary>
        private static List<WallDependency> TakeCensus(Document doc, Wall wall, WallReverseCensus reverse)
        {
            var seen = new HashSet<long>();
            var census = new List<WallDependency>();

            void Consider(ElementId id)
            {
                if (id == null) return;
                long raw = Rid.Value(id);
                if (raw <= 0 || !seen.Add(raw)) return;

                Element element = doc.GetElement(id);
                if (element == null || raw == Rid.Value(wall.Id)) return;

                census.Add(Classify(doc, wall, element));
            }

            try { foreach (ElementId id in wall.FindInserts(true, true, true, true)) Consider(id); }
            catch { /* reported through the dependent pass below */ }

            try { foreach (ElementId id in wall.GetDependentElements(null)) Consider(id); }
            catch { }

            // ---- and the ones only the STRUCTURAL model knows about ----------------
            //
            // GetDependentElements does not reliably return reinforcement, and a bar set
            // that never reaches the ledger is a bar set nothing verifies. RebarHostData is
            // the API's own answer to "what reinforces this element"; it is asked directly
            // rather than inferred, and a host that cannot be asked BLOCKS rather than
            // reporting an empty result.
            AddStructural(doc, wall, census, Consider);

            // ---- and the ones that only the ANNOTATIONS know about ------------------
            //
            // Deduplicated against everything above by the same `seen` set, so an element
            // that both passes found appears once.
            if (reverse != null)
            {
                foreach (ElementId id in reverse.For(Rid.Value(wall.Id))) Consider(id);

                // A reference this capability cannot follow is not a reference it may
                // ignore. If the scan could not run at all, that blocks too: an unscanned
                // document is not a document with no dimensions in it.
                if (!reverse.ScanRan)
                    census.Add(new WallDependency
                    {
                        ElementId = 0,
                        UniqueId = null,
                        Category = null,
                        Kind = DependencyKinds.Unrecognised,
                        Disposition = DependencyDisposition.UnsupportedBlocking,
                        Note = "the reverse census over dimensions and tags could not run (" +
                               (reverse.ScanFailure ?? "no reason given") + "), so whether anything annotates this " +
                               "wall is unknown. Unknown is not empty."
                    });

                foreach (WallDependency entry in census.ToList())
                {
                    if (entry.ElementId <= 0 || !reverse.IsUninterpretable(entry.ElementId)) continue;
                    entry.Disposition = DependencyDisposition.UnsupportedBlocking;
                    entry.Snapshot = null;
                    entry.Note = "this element references the wall in a way this capability cannot interpret: " +
                                 string.Join("; ", reverse.WhyUninterpretable(entry.ElementId)) +
                                 ". It is blocking rather than assumed harmless.";
                }
            }

            return census;
        }

        /// <summary>
        /// Everything reinforcing this wall, asked of RebarHostData. Deduplicated against
        /// the two passes above by the caller's own `seen` set.
        /// </summary>
        private static void AddStructural(Document doc, Wall wall, List<WallDependency> census,
                                          Action<ElementId> consider)
        {
            RebarHostData host = null;
            try { host = RebarHostData.GetRebarHostData(wall); }
            catch { host = null; }

            if (host == null)
            {
                // Not a valid reinforcement host. That is an ANSWER - most walls are not -
                // and it is recorded as one rather than left as an absence.
                census.Add(new WallDependency
                {
                    ElementId = 0,
                    Category = null,
                    Kind = DependencyKinds.Structural,
                    Disposition = DependencyDisposition.NotApplicable,
                    Note = "this wall is not a reinforcement host, so there is no reinforcement to preserve. " +
                           "Asked and answered, not assumed."
                });
                return;
            }

            bool asked = true;
            void Ask(Func<IList<ElementId>> read)
            {
                try { foreach (ElementId id in read() ?? new List<ElementId>()) consider(id); }
                catch { asked = false; }
            }

            Ask(() => host.GetRebarsInHost()?.Select(r => r.Id).ToList());
            Ask(() => host.GetAreaReinforcementsInHost()?.Select(r => r.Id).ToList());
            Ask(() => host.GetPathReinforcementsInHost()?.Select(r => r.Id).ToList());
            Ask(() => host.GetFabricAreasInHost()?.Select(r => r.Id).ToList());
            Ask(() => host.GetFabricSheetsInHost()?.Select(r => r.Id).ToList());
            Ask(() => host.GetRebarContainersInHost()?.Select(r => r.Id).ToList());

            if (!asked)
                census.Add(new WallDependency
                {
                    ElementId = 0,
                    Kind = DependencyKinds.Unrecognised,
                    Disposition = DependencyDisposition.UnsupportedBlocking,
                    Note = "one of this wall's reinforcement collections could not be read, so what reinforces it " +
                           "is unknown. Unknown is not empty, and a wall whose reinforcement cannot be enumerated " +
                           "is refused rather than converted."
                });
        }

        /// <summary>
        /// What kind of thing this is, and - following from that and ONLY from that - how it
        /// is going to survive. The disposition is never chosen here: it comes from
        /// DependencyKinds.DispositionFor, so a class without a verifier is blocking by
        /// construction rather than by somebody remembering.
        /// </summary>
        private static WallDependency Classify(Document doc, Wall wall, Element element)
        {
            string kind = KindOf(doc, wall, element);
            var entry = new WallDependency
            {
                ElementId = Rid.Value(element.Id),
                UniqueId = SafeUniqueId(element),
                Category = element.Category == null ? null : element.Category.Name,
                Kind = kind,
                Disposition = DependencyKinds.DispositionFor(kind)
            };

            if (entry.Disposition == DependencyDisposition.UnsupportedBlocking)
            {
                entry.Note = "this capability has no host-side verifier for a " + kind + ", so it cannot prove the " +
                             "element survived the transformation. Calling it preserved would be an assertion with " +
                             "nothing behind it, so the wall is refused instead.";
                return entry;
            }

            if (entry.Disposition == DependencyDisposition.NotApplicable)
            {
                entry.Note = "not an instance that can be lost (a sketch or a type).";
                return entry;
            }

            entry.Snapshot = SnapshotDependency(doc, element, kind);
            entry.Note = "hangs off the original element, which becomes the carrier and is never deleted; re-read " +
                         "and compared after the transformation by the " + kind + " verifier.";
            return entry;
        }

        private static string KindOf(Document doc, Wall wall, Element element)
        {
            if (element is FamilyInstance) return DependencyKinds.FamilyInstance;
            if (element is Opening) return DependencyKinds.Opening;

            if (element is WallSweep sweep)
            {
                try
                {
                    WallSweepInfo info = sweep.GetWallSweepInfo();
                    if (info != null && info.WallSweepType == WallSweepType.Reveal) return DependencyKinds.Reveal;
                }
                catch { }
                return DependencyKinds.WallSweep;
            }

            // Structural, each by its own class. A continuous footing is not a bar set and
            // neither is a fabric sheet; one generic bucket would mean verifying all of them
            // the way the weakest one can be verified.
            if (element is WallFoundation) return DependencyKinds.WallFoundation;
            if (element is Rebar) return DependencyKinds.Rebar;
            if (element is RebarContainer) return DependencyKinds.RebarContainer;
            if (element is AreaReinforcement) return DependencyKinds.AreaReinforcement;
            if (element is PathReinforcement) return DependencyKinds.PathReinforcement;
            if (element is FabricArea) return DependencyKinds.FabricArea;
            if (element is FabricSheet) return DependencyKinds.FabricSheet;

            // A RebarInSystem belongs to its system and is verified through it: verifying it
            // twice would report one loss as two, and its own host is the system.
            if (element is RebarInSystem) return DependencyKinds.Structural;

            // Revit 2026 inserts one private implementation node between a reinforced
            // host and every ordinary Rebar. It is exposed only as the base DB.Element:
            // no category, no type, no name; its dependants are itself and the real bars.
            // Treating that node as an unknown dependency refused every reinforced wall
            // before RebarHostData's authoritative bar census could run. It is not ignored
            // by shape alone: every non-self dependant must be a Rebar whose host is THIS
            // wall. The bars themselves enter the ledger through AddStructural and are
            // fingerprinted and verified in full.
            if (IsInternalRebarNode(doc, wall, element)) return DependencyKinds.Structural;

            if (element is Wall) return DependencyKinds.EmbeddedWall;
            if (element is Dimension) return DependencyKinds.Dimension;
            if (element is IndependentTag) return DependencyKinds.Tag;
            if (element is Sketch || element is ElementType) return DependencyKinds.Structural;

            return DependencyKinds.Unrecognised;
        }

        private static bool IsInternalRebarNode(Document doc, Wall wall, Element element)
        {
            if (doc == null || wall == null || element == null || element.GetType() != typeof(Element)) return false;
            try { if (element.Category != null || Rid.Value(element.GetTypeId()) > 0) return false; }
            catch { return false; }

            var children = new List<Element>();
            try
            {
                foreach (ElementId id in element.GetDependentElements(null))
                {
                    if (Rid.Value(id) == Rid.Value(element.Id)) continue;
                    Element child = doc.GetElement(id);
                    if (child != null) children.Add(child);
                }
            }
            catch { return false; }

            if (children.Count == 0) return false;
            foreach (Element child in children)
            {
                var bar = child as Rebar;
                if (bar == null) return false;
                try { if (Rid.Value(bar.GetHostId()) != Rid.Value(wall.Id)) return false; }
                catch { return false; }
            }
            return true;
        }

        /// <summary>
        /// The BEFORE state, per kind. Everything captured here is compared afterwards - a
        /// field captured and never read would be exactly the gap this rewrite closes.
        /// </summary>
        private static DependencySnapshot SnapshotDependency(Document doc, Element element, string kind)
        {
            var snapshot = new DependencySnapshot
            {
                Kind = kind,
                ElementId = Rid.Value(element.Id),
                UniqueId = SafeUniqueId(element)
            };

            try { snapshot.CategoryId = element.Category == null ? 0 : Rid.Value(element.Category.Id); } catch { }
            try { snapshot.TypeId = Rid.Value(element.GetTypeId()); } catch { }
            try
            {
                Element type = Rid.Value(element.GetTypeId()) > 0 ? doc.GetElement(element.GetTypeId()) : null;
                snapshot.TypeName = type == null ? null : SafeName(type);
            }
            catch { }
            try { snapshot.OwnerViewId = Rid.Value(element.OwnerViewId); } catch { }

            try
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    string key = StableParameterKey(parameter);
                    if (key == null || snapshot.Parameters.ContainsKey(key)) continue;
                    snapshot.Parameters[key] = RenderParameter(parameter);
                }
            }
            catch { }

            switch (kind)
            {
                case DependencyKinds.FamilyInstance:
                    snapshot.Insert = Snapshot(doc, (FamilyInstance)element);
                    snapshot.HostId = snapshot.Insert.HostId;
                    break;

                case DependencyKinds.Opening:
                    SnapshotOpening((Opening)element, snapshot);
                    break;

                case DependencyKinds.WallSweep:
                case DependencyKinds.Reveal:
                    SnapshotSweep((WallSweep)element, snapshot);
                    break;

                case DependencyKinds.EmbeddedWall:
                    SnapshotWall((Wall)element, snapshot);
                    break;

                case DependencyKinds.Dimension:
                    SnapshotDimension(doc, (Dimension)element, snapshot);
                    break;

                case DependencyKinds.Tag:
                    SnapshotTag(doc, (IndependentTag)element, snapshot);
                    break;

                case DependencyKinds.WallFoundation:
                    SnapshotFoundation((WallFoundation)element, snapshot);
                    break;

                case DependencyKinds.Rebar:
                    SnapshotRebar(doc, (Rebar)element, snapshot);
                    break;

                case DependencyKinds.RebarContainer:
                case DependencyKinds.AreaReinforcement:
                case DependencyKinds.PathReinforcement:
                case DependencyKinds.FabricArea:
                case DependencyKinds.FabricSheet:
                    SnapshotReinforcementSystem(doc, element, kind, snapshot);
                    break;
            }

            return snapshot;
        }

        private static void SnapshotOpening(Opening opening, DependencySnapshot snapshot)
        {
            try { snapshot.HostId = opening.Host == null ? 0 : Rid.Value(opening.Host.Id); } catch { }
            try { snapshot.OpeningIsRectangular = opening.IsRectBoundary; } catch { }

            try
            {
                if (snapshot.OpeningIsRectangular)
                {
                    IList<XYZ> rect = opening.BoundaryRect;
                    if (rect != null) foreach (XYZ point in rect) snapshot.OpeningBoundaryPoints.Add(point);
                    snapshot.OpeningCurveCount = snapshot.OpeningBoundaryPoints.Count;
                }
                else
                {
                    double length = 0.0;
                    int count = 0;
                    foreach (Curve curve in opening.BoundaryCurves)
                    {
                        if (curve == null) continue;
                        count++;
                        length += curve.Length;
                        snapshot.OpeningBoundaryPoints.Add(curve.GetEndPoint(0));
                    }
                    snapshot.OpeningCurveCount = count;
                    snapshot.OpeningBoundaryLengthFeet = length;
                }
            }
            catch { }
        }

        private static void SnapshotSweep(WallSweep sweep, DependencySnapshot snapshot)
        {
            try { snapshot.SweepBounds = sweep.get_BoundingBox(null); } catch { }

            try
            {
                foreach (ElementId id in sweep.GetHostIds()) snapshot.SweepHostIds.Add(Rid.Value(id));
                snapshot.SweepHostIds.Sort();
            }
            catch { }

            try
            {
                WallSweepInfo info = sweep.GetWallSweepInfo();
                if (info == null) return;
                snapshot.SweepType = info.WallSweepType.ToString();
                snapshot.SweepProfileId = Rid.Value(info.ProfileId);
                snapshot.SweepDistanceFeet = info.Distance;
                snapshot.SweepWallOffsetFeet = info.WallOffset;
                snapshot.SweepIsVertical = info.IsVertical;
                snapshot.SweepWallSide = info.WallSide.ToString();
            }
            catch { }
        }

        private static void SnapshotWall(Wall wall, DependencySnapshot snapshot)
        {
            try { snapshot.WallIsCurtain = wall.WallType != null && wall.WallType.Kind == WallKind.Curtain; } catch { }
            try { snapshot.WallBaseLevelId = Rid.Value(wall.LevelId); } catch { }
            try
            {
                Parameter top = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                snapshot.WallTopLevelId = top == null || !top.HasValue ? 0 : Rid.Value(top.AsElementId());
            }
            catch { }
            try
            {
                Parameter baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                if (baseOffset != null && baseOffset.HasValue) snapshot.WallBaseOffsetFeet = baseOffset.AsDouble();
                Parameter topOffset = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                if (topOffset != null && topOffset.HasValue) snapshot.WallTopOffsetFeet = topOffset.AsDouble();
            }
            catch { }
            try { snapshot.WallCurveDigest = CurveDigest((wall.Location as LocationCurve)?.Curve); }
            catch { }
        }

        private static void SnapshotDimension(Document doc, Dimension dimension, DependencySnapshot snapshot)
        {
            try
            {
                ReferenceArray references = dimension.References;
                snapshot.ReferenceCount = references == null ? 0 : references.Size;
                if (references != null)
                {
                    foreach (Reference reference in references)
                    {
                        if (reference == null) continue;
                        try { snapshot.ReferenceRepresentations.Add(reference.ConvertToStableRepresentation(doc)); }
                        catch { snapshot.ReferenceRepresentations.Add("<unreadable>"); }
                    }
                }
            }
            catch { }

            try
            {
                // A multi-segment dimension has no single value. That is a state, not a gap.
                if (dimension.Value.HasValue)
                {
                    snapshot.DimensionValueFeet = dimension.Value.Value;
                    snapshot.DimensionValueRead = true;
                }
            }
            catch { }
        }

        private static void SnapshotTag(Document doc, IndependentTag tag, DependencySnapshot snapshot)
        {
            try
            {
                foreach (ElementId id in tag.GetTaggedLocalElementIds())
                {
                    long raw = Rid.Value(id);
                    if (raw <= 0) continue;
                    snapshot.TaggedElementIds.Add(raw);
                    Element element = doc.GetElement(id);
                    snapshot.TaggedUniqueIds.Add(element == null ? "" : SafeUniqueId(element));
                }
            }
            catch { }

            // The REFERENCE count, not the local-element count: a tag pointing into a link
            // has references that resolve to no local element at all, and the difference is
            // exactly what says "this tag is not fully accounted for".
            try
            {
                IList<Reference> references = tag.GetTaggedReferences();
                snapshot.TaggedReferenceCount = references == null ? 0 : references.Count;
                snapshot.TagHasNonLocalReference =
                    snapshot.TaggedReferenceCount > snapshot.TaggedElementIds.Count;
            }
            catch
            {
                snapshot.TaggedReferenceCount = snapshot.TaggedElementIds.Count;
            }

            try { snapshot.TagHeadPosition = tag.TagHeadPosition; } catch { }
        }

        /// <summary>
        /// A fingerprint of one dependency's WHOLE STATE.
        ///
        /// This is what the confirmation token binds, and it is the fix for a real hole: the
        /// token used to carry the LIST OF UNIQUE IDS of the dependencies, which detects a
        /// door appearing or disappearing and nothing else. A door that was moved, re-typed,
        /// re-phased, re-hosted or re-parameterised between the dry run and the apply left
        /// the number identical, and the apply proceeded against a model nobody approved.
        /// </summary>
        public static string FingerprintOf(DependencySnapshot snapshot)
        {
            if (snapshot == null) return "";

            var book = new FactBook()
                .Add("kind", snapshot.Kind)
                .Add("element_id", snapshot.ElementId)
                .Add("unique_id", snapshot.UniqueId)
                .Add("category_id", snapshot.CategoryId)
                .Add("type_id", snapshot.TypeId)
                .Add("type_name", snapshot.TypeName)
                .Add("host_id", snapshot.HostId)
                .Add("owner_view_id", snapshot.OwnerViewId)
                .AddMap("parameters", snapshot.Parameters);

            // ---- family instances -------------------------------------------------
            InsertSnapshot insert = snapshot.Insert;
            book.Add("has_insert", insert != null);
            if (insert != null)
            {
                book.Add("insert.symbol_id", insert.SymbolId)
                    .Add("insert.level_id", insert.LevelId)
                    .Add("insert.hand_flipped", insert.HandFlipped)
                    .Add("insert.facing_flipped", insert.FacingFlipped)
                    .Add("insert.mirrored", insert.Mirrored)
                    .Add("insert.phase_created", insert.PhaseCreated)
                    .Add("insert.phase_demolished", insert.PhaseDemolished)
                    .Add("insert.workset", insert.WorksetId)
                    .Add("insert.design_option", insert.DesignOptionId)
                    .Add("insert.pinned", insert.Pinned)
                    .Add("insert.subcomponent_count", insert.SubComponentCount)
                    .AddList("insert.subcomponent_unique_ids", insert.SubComponentUniqueIds, ordered: false)
                    .AddList("insert.subcomponent_symbol_ids", insert.SubComponentSymbolIds, ordered: false);

                AddPoint(book, "insert.point", insert.Point);
                AddVector(book, "insert.facing", insert.FacingOrientation);
                book.Add("insert.rotation_read", insert.RotationRead);
                if (insert.RotationRead) book.AddAngle("insert.rotation", insert.Rotation);
                AddBounds(book, "insert.bounds", insert.Bounds);
            }

            // ---- openings ----------------------------------------------------------
            book.Add("opening.rectangular", snapshot.OpeningIsRectangular)
                .Add("opening.curve_count", snapshot.OpeningCurveCount)
                .AddFeet("opening.boundary_length", snapshot.OpeningBoundaryLengthFeet);
            var openingPoints = new List<string>();
            foreach (XYZ point in snapshot.OpeningBoundaryPoints)
                openingPoints.Add(Quantised(point));
            book.AddList("opening.boundary_points", openingPoints, ordered: true);

            // ---- sweeps and reveals -------------------------------------------------
            book.Add("sweep.type", snapshot.SweepType)
                .Add("sweep.profile_id", snapshot.SweepProfileId)
                .AddFeet("sweep.distance", snapshot.SweepDistanceFeet)
                .AddFeet("sweep.wall_offset", snapshot.SweepWallOffsetFeet)
                .Add("sweep.vertical", snapshot.SweepIsVertical)
                .Add("sweep.wall_side", snapshot.SweepWallSide)
                .AddList("sweep.host_ids", snapshot.SweepHostIds, ordered: false);
            AddBounds(book, "sweep.bounds", snapshot.SweepBounds);

            // ---- embedded walls ------------------------------------------------------
            book.Add("wall.base_level", snapshot.WallBaseLevelId)
                .Add("wall.top_level", snapshot.WallTopLevelId)
                .AddFeet("wall.base_offset", snapshot.WallBaseOffsetFeet)
                .AddFeet("wall.top_offset", snapshot.WallTopOffsetFeet)
                .Add("wall.curve_digest", snapshot.WallCurveDigest)
                .Add("wall.is_curtain", snapshot.WallIsCurtain);

            // ---- dimensions -----------------------------------------------------------
            book.Add("dimension.reference_count", snapshot.ReferenceCount)
                .AddList("dimension.references", snapshot.ReferenceRepresentations, ordered: true)
                .Add("dimension.value_read", snapshot.DimensionValueRead);
            if (snapshot.DimensionValueRead) book.AddFeet("dimension.value", snapshot.DimensionValueFeet);

            // ---- tags -------------------------------------------------------------------
            book.AddList("tag.element_ids", snapshot.TaggedElementIds, ordered: true)
                .AddList("tag.unique_ids", snapshot.TaggedUniqueIds, ordered: true)
                .Add("tag.reference_count", snapshot.TaggedReferenceCount)
                .Add("tag.has_non_local_reference", snapshot.TagHasNonLocalReference);
            AddPoint(book, "tag.head", snapshot.TagHeadPosition);

            // ---- WallFoundation --------------------------------------------------
            book.Add("foundation.wall_id", snapshot.FoundationWallId)
                .Add("foundation.level_id", snapshot.FoundationLevelId)
                .AddFeet("foundation.offset", snapshot.FoundationOffsetFeet)
                .Add("foundation.curve_digest", snapshot.FoundationCurveDigest);
            AddBounds(book, "foundation.bounds", snapshot.FoundationBounds);

            // ---- Rebar ------------------------------------------------------------
            book.Add("rebar.host_id", snapshot.RebarHostId)
                .Add("rebar.bar_type_id", snapshot.RebarBarTypeId)
                .Add("rebar.shape_id", snapshot.RebarShapeId)
                .Add("rebar.style", snapshot.RebarStyle)
                .Add("rebar.layout_rule", snapshot.RebarLayoutRule)
                .Add("rebar.positions", snapshot.RebarNumberOfPositions)
                .Add("rebar.quantity", snapshot.RebarQuantity.ToString("F4", CultureInfo.InvariantCulture))
                .Add("rebar.array_length_mm", snapshot.RebarArrayLengthMm.ToString("F3", CultureInfo.InvariantCulture))
                .Add("rebar.normal", snapshot.RebarNormalDigest)
                .Add("rebar.terminations", snapshot.RebarTerminationsDigest)
                .Add("rebar.include_first", snapshot.RebarIncludeFirst)
                .Add("rebar.include_last", snapshot.RebarIncludeLast)
                .Add("rebar.containment_before", snapshot.RebarContainmentBefore)
                // ORDERED: a bar set is a sequence, and the third bar moving is not the same
                // set with its members shuffled.
                .AddList("rebar.position_digests", snapshot.RebarPositionDigests, ordered: true)
                // The position transforms are relative to bar zero. This second ordered list
                // anchors bar zero in model space, so moving the complete set after a dry run
                // changes the fingerprint too.
                .AddList("rebar.centreline_points", snapshot.RebarCentrelinePointsMm
                    .Select(PointDigest).ToList(), ordered: true);

            // ---- reinforcement systems ---------------------------------------------
            book.Add("system.host_id", snapshot.SystemHostId)
                .Add("system.type_id", snapshot.SystemTypeId)
                .Add("system.number_of_lines", snapshot.SystemNumberOfLines)
                .Add("system.direction", snapshot.SystemDirectionDigest)
                .Add("system.layers", snapshot.SystemLayersDigest)
                .AddList("system.member_ids", snapshot.SystemMemberIds, ordered: true)
                .AddList("system.member_unique_ids", snapshot.SystemMemberUniqueIds, ordered: true)
                .AddList("system.boundary_ids", snapshot.SystemBoundaryIds, ordered: true);

            return book.Digest();
        }

        /// <summary>A fingerprint of how the wall meets its neighbours, order where it counts.</summary>
        public static string FingerprintOf(WallJoinFacts joins)
        {
            if (joins == null) return "";

            var cutOrder = new List<string>();
            foreach (KeyValuePair<long, bool> pair in joins.CutByOther)
                cutOrder.Add(pair.Key + ":" + (pair.Value ? "1" : "0"));

            return new FactBook()
                .AddList("geometric_join_ids", joins.GeometricJoinIds, ordered: false)
                // The cut order is a MAP: which of the two elements cuts the other, per
                // neighbour. Unordered, because the dictionary's enumeration order is not
                // a fact about the model - but its CONTENT is, and it changes the digest.
                .AddList("cut_by_other", cutOrder, ordered: false)
                .Add("join_allowed_at_end_0", joins.JoinAllowedAtEnd0)
                .Add("join_allowed_at_end_1", joins.JoinAllowedAtEnd1)
                .Add("end_flags_read", joins.EndFlagsRead)
                // ORDERED: the elements meeting a wall at one end come back in join order,
                // and that order is part of what the junction looks like.
                .AddList("elements_at_end_0", joins.ElementsAtEnd0, ordered: true)
                .AddList("elements_at_end_1", joins.ElementsAtEnd1, ordered: true)
                .Add("elements_at_join_read", joins.ElementsAtJoinRead)
                .Digest();
        }

        /// <summary>
        /// A fingerprint of the wall's OWN state - everything a person could change about it
        /// that is not its layers or its curve. Constraints, phases, workset, pinning and
        /// room bounding all decide what the conversion produces, and none of them were in
        /// the token before.
        /// </summary>
        public static string WallStateFingerprint(Wall wall)
        {
            var book = new FactBook();
            var parameters = new[]
            {
                BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.WALL_BASE_OFFSET,
                BuiltInParameter.WALL_HEIGHT_TYPE, BuiltInParameter.WALL_TOP_OFFSET,
                BuiltInParameter.WALL_USER_HEIGHT_PARAM, BuiltInParameter.WALL_ATTR_ROOM_BOUNDING,
                BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT, BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM,
                BuiltInParameter.PHASE_CREATED, BuiltInParameter.PHASE_DEMOLISHED,
                BuiltInParameter.ELEM_PARTITION_PARAM, BuiltInParameter.WALL_KEY_REF_PARAM,
                BuiltInParameter.WALL_CROSS_SECTION, BuiltInParameter.WALL_TOP_IS_ATTACHED,
                BuiltInParameter.WALL_BOTTOM_IS_ATTACHED
            };

            foreach (BuiltInParameter id in parameters)
            {
                try
                {
                    Parameter parameter = wall.get_Parameter(id);
                    book.Add(id.ToString(), parameter == null ? "<absent>" : RenderParameter(parameter));
                }
                catch { book.Add(id.ToString(), "<unreadable>"); }
            }

            try { book.Add("pinned", wall.Pinned); } catch { book.Add("pinned", "<unreadable>"); }
            try { book.Add("group_id", Rid.Value(wall.GroupId)); } catch { book.Add("group_id", -1L); }
            try { book.Add("design_option_id", wall.DesignOption == null ? 0 : Rid.Value(wall.DesignOption.Id)); }
            catch { book.Add("design_option_id", -1L); }
            try { book.Add("level_id", Rid.Value(wall.LevelId)); } catch { book.Add("level_id", -1L); }
            try { book.Add("sketch_id", Rid.Value(wall.SketchId)); } catch { book.Add("sketch_id", -1L); }

            // THE COVER. It is a property of the HOST that decides where every bar sits, so
            // a cover changed between the dry run and the apply is a different wall to
            // reinforce - and the token has to see it.
            AddCover(book, wall);

            return book.Digest();
        }

        /// <summary>
        /// The reinforcement cover, common and per exposed face. Tri-state: a host that
        /// cannot be asked is recorded as unread, never as "no cover".
        /// </summary>
        private static void AddCover(FactBook book, Wall wall)
        {
            RebarHostData host = null;
            try { host = RebarHostData.GetRebarHostData(wall); } catch { }

            if (host == null)
            {
                book.Add("cover.host", "not_a_reinforcement_host");
                return;
            }

            try
            {
                RebarCoverType common = host.GetCommonCoverType();
                book.Add("cover.common_id", common == null ? -1L : Rid.Value(common.Id));
            }
            catch { book.Add("cover.common_id", "<unreadable>"); }

            try
            {
                var faces = new List<string>();
                foreach (Reference face in host.GetExposedFaces() ?? new List<Reference>())
                {
                    RebarCoverType cover = null;
                    try { cover = host.GetCoverType(face); } catch { }
                    faces.Add((cover == null ? "-1" : Rid.Value(cover.Id).ToString(CultureInfo.InvariantCulture)));
                }
                // UNORDERED: which faces are exposed is a set; the order Revit returns them
                // in is not a fact about the wall.
                book.AddList("cover.by_face", faces, ordered: false);
            }
            catch { book.Add("cover.by_face_error", "<unreadable>"); }
        }

        private static void AddPoint(FactBook book, string name, XYZ point)
        {
            book.Add(name + ".present", point != null);
            if (point != null) book.AddPoint(name, point.X, point.Y, point.Z);
        }

        private static void AddVector(FactBook book, string name, XYZ vector)
        {
            book.Add(name + ".present", vector != null);
            if (vector != null) book.AddPoint(name, vector.X, vector.Y, vector.Z);
        }

        private static void AddBounds(FactBook book, string name, BoundingBoxXYZ bounds)
        {
            book.Add(name + ".present", bounds != null);
            if (bounds == null) return;
            try
            {
                book.AddPoint(name + ".min", bounds.Min.X, bounds.Min.Y, bounds.Min.Z);
                book.AddPoint(name + ".max", bounds.Max.X, bounds.Max.Y, bounds.Max.Z);
            }
            catch { }
        }

        private static string Quantised(XYZ point)
            => point == null
                ? ""
                : WallLayerRules.QuantizeFeet(point.X) + "," + WallLayerRules.QuantizeFeet(point.Y) + "," +
                  WallLayerRules.QuantizeFeet(point.Z);

        private static void SnapshotFoundation(WallFoundation foundation, DependencySnapshot snapshot)
        {
            try { snapshot.FoundationWallId = Rid.Value(foundation.WallId); } catch { }
            try { snapshot.HostId = snapshot.FoundationWallId; } catch { }
            try { snapshot.FoundationLevelId = Rid.Value(foundation.LevelId); } catch { }
            try { snapshot.FoundationBounds = foundation.get_BoundingBox(null); } catch { }
            try
            {
                // A wall foundation has no single documented "offset" parameter across
                // years, so the elevation it actually sits at is READ FROM ITS GEOMETRY -
                // measured, not looked up under a name that may not exist.
                BoundingBoxXYZ box = snapshot.FoundationBounds;
                if (box != null) snapshot.FoundationOffsetFeet = box.Min.Z;
            }
            catch { }
            try { snapshot.FoundationCurveDigest = CurveDigest((foundation.Location as LocationCurve)?.Curve); }
            catch { }
        }

        /// <summary>
        /// One bar set. The READING is RebarFacts.Describe - this bridge's existing algorithm,
        /// deliberately not reimplemented - and what is kept here is the subset that has to be
        /// compared afterwards, lifted out of that reply.
        /// </summary>
        private static void SnapshotRebar(Document doc, Rebar bar, DependencySnapshot snapshot)
        {
            JObject described = null;
            try { described = RebarFacts.Describe(doc, bar, includePositions: true); } catch { }
            snapshot.RebarDescription = described;
            if (described == null) return;

            snapshot.RebarHostId = described["host"]?.Value<long?>("id") ?? 0;
            snapshot.RebarBarTypeId = described["bar_type"]?.Value<long?>("id") ?? 0;
            snapshot.RebarShapeId = described["shape"]?.Value<long?>("id") ?? 0;
            snapshot.RebarStyle = described.Value<string>("style_horizun");
            snapshot.HostId = snapshot.RebarHostId;

            JToken layout = described["layout"];
            if (layout != null)
            {
                snapshot.RebarLayoutRule = layout.Value<string>("rule");
                snapshot.RebarNumberOfPositions = layout.Value<int?>("number_of_bar_positions") ?? 0;
                snapshot.RebarQuantity = layout.Value<double?>("quantity") ?? 0.0;
                snapshot.RebarArrayLengthMm = layout.Value<double?>("array_length_mm") ?? 0.0;
                snapshot.RebarIncludeFirst = layout.Value<bool?>("include_first_bar") ?? true;
                snapshot.RebarIncludeLast = layout.Value<bool?>("include_last_bar") ?? true;
                snapshot.RebarNormalDigest = Canonical(layout["normal"]);
            }

            snapshot.RebarTerminationsDigest = Canonical(described["terminations"]);

            // Every POSITION, not only the set. RebarFacts publishes these at the TOP LEVEL;
            // they are transforms from bar zero, not geometry children. The old reader looked
            // under geometry, found nothing, and silently substituted centreline POINTS. Its
            // verifier then used the same wrong path without the fallback and every real bar
            // failed as "0 readable positions". One extractor is now used on both sides.
            snapshot.RebarPositionDigests.AddRange(ReadRebarPositionDigests(described));

            try
            {
                foreach (double[] point in RebarFacts.CentrelinePointsMm(bar, asDeclared: false) ??
                                          new List<double[]>())
                    snapshot.RebarCentrelinePointsMm.Add((double[])point.Clone());
            }
            catch { }
        }

        internal static List<string> ReadRebarPositionDigests(JObject described)
        {
            var result = new List<string>();
            try
            {
                if (described?["bar_positions"] is JArray positions)
                    foreach (JToken position in positions) result.Add(Canonical(position));
            }
            catch { }
            return result;
        }

        private static string PointDigest(double[] point)
        {
            return point == null
                ? "null"
                : string.Join(",", point.Select(v => Math.Round(v, 3)
                    .ToString("F3", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// An area, path, fabric or container system: its host, its type, its boundary and
        /// ITS MEMBERS. The members are the point - a system that lost three of its bars is
        /// still a system, and "it still exists" would pass it.
        /// </summary>
        private static void SnapshotReinforcementSystem(Document doc, Element element, string kind,
                                                        DependencySnapshot snapshot)
        {
            try { snapshot.SystemTypeId = Rid.Value(element.GetTypeId()); } catch { }

            switch (kind)
            {
                case DependencyKinds.AreaReinforcement:
                    var area = (AreaReinforcement)element;
                    try { snapshot.SystemHostId = Rid.Value(area.GetHostId()); } catch { }
                    try { foreach (ElementId id in area.GetRebarInSystemIds()) snapshot.SystemMemberIds.Add(Rid.Value(id)); } catch { }
                    try { foreach (ElementId id in area.GetBoundaryCurveIds()) snapshot.SystemBoundaryIds.Add(Rid.Value(id)); } catch { }
                    try { snapshot.SystemDirectionDigest = VectorDigest(area.Direction); } catch { }
                    try
                    {
                        // Every layer, active or not, with its line count and direction. A
                        // system that lost a layer is not the system that was approved.
                        var layers = new List<string>();
                        int lines = 0;
                        foreach (AreaReinforcementLayerType layer in
                                 Enum.GetValues(typeof(AreaReinforcementLayerType)))
                        {
                            bool active = area.IsLayerActive(layer);
                            int count = active ? area.GetNumberOfLines(layer) : 0;
                            lines += count;
                            layers.Add(layer + "=" + (active ? "1" : "0") + ":" + count + ":" +
                                       VectorDigest(active ? SafeLayerDirection(area, layer) : null));
                        }
                        snapshot.SystemLayersDigest = string.Join(";", layers);
                        snapshot.SystemNumberOfLines = lines;
                    }
                    catch { }
                    break;

                case DependencyKinds.PathReinforcement:
                    var path = (PathReinforcement)element;
                    try { snapshot.SystemHostId = Rid.Value(path.GetHostId()); } catch { }
                    try { foreach (ElementId id in path.GetRebarInSystemIds()) snapshot.SystemMemberIds.Add(Rid.Value(id)); } catch { }
                    try { foreach (ElementId id in path.GetCurveElementIds()) snapshot.SystemBoundaryIds.Add(Rid.Value(id)); } catch { }
                    try
                    {
                        snapshot.SystemLayersDigest =
                            "primary_shape=" + Rid.Value(path.PrimaryBarShapeId) +
                            ";alternating_shape=" + Rid.Value(path.AlternatingBarShapeId) +
                            ";alternating=" + (path.IsAlternatingLayerEnabled() ? "1" : "0") +
                            ";primary_orientation=" + path.PrimaryBarOrientation +
                            ";alternating_orientation=" + path.AlternatingBarOrientation;
                    }
                    catch { }
                    break;

                case DependencyKinds.RebarContainer:
                    var container = (RebarContainer)element;
                    try { snapshot.SystemHostId = Rid.Value(container.GetHostId()); } catch { }
                    try { foreach (RebarContainerItem item in container) snapshot.SystemMemberIds.Add(item.ItemIndex); } catch { }
                    try { snapshot.SystemNumberOfLines = container.ItemsCount; } catch { }
                    break;

                default:   // fabric area and fabric sheet
                    try
                    {
                        Parameter host = element.get_Parameter(BuiltInParameter.HOST_ID_PARAM);
                        if (host != null && host.HasValue) snapshot.SystemHostId = Rid.Value(host.AsElementId());
                    }
                    catch { }
                    if (element is FabricArea fabric)
                    {
                        try { foreach (ElementId id in fabric.GetFabricSheetElementIds()) snapshot.SystemMemberIds.Add(Rid.Value(id)); } catch { }
                        try { foreach (ElementId id in fabric.GetBoundaryCurveIds()) snapshot.SystemBoundaryIds.Add(Rid.Value(id)); } catch { }
                        try { snapshot.SystemDirectionDigest = VectorDigest(fabric.Direction); } catch { }
                    }
                    break;
            }

            snapshot.SystemMemberIds.Sort();
            foreach (long id in snapshot.SystemMemberIds)
            {
                Element member = null;
                try { member = doc.GetElement(Rid.Make(id)); } catch { }
                snapshot.SystemMemberUniqueIds.Add(member == null ? "" : SafeUniqueId(member));
            }
        }

        private static XYZ SafeLayerDirection(AreaReinforcement area, AreaReinforcementLayerType layer)
        {
            try { return area.GetLayerDirection(layer); } catch { return null; }
        }

        private static string VectorDigest(XYZ v)
            => v == null ? null
                         : WallLayerRules.QuantizeFeet(v.X) + "," + WallLayerRules.QuantizeFeet(v.Y) + "," +
                           WallLayerRules.QuantizeFeet(v.Z);

        /// <summary>A JSON fragment as one stable string, so a digest can be taken over it.</summary>
        private static string Canonical(JToken token)
        {
            try { return token == null ? null : token.ToString(Newtonsoft.Json.Formatting.None); }
            catch { return null; }
        }

        /// <summary>A curve reduced to a comparable string on the 0.1 mm grid.</summary>
        public static string CurveDigest(Curve curve)
        {
            if (curve == null) return null;
            try
            {
                var parts = new List<string>();
                foreach (double t in new[] { 0.0, 0.5, 1.0 })
                {
                    XYZ point = curve.Evaluate(t, true);
                    parts.Add(WallLayerRules.QuantizeFeet(point.X) + "," +
                              WallLayerRules.QuantizeFeet(point.Y) + "," +
                              WallLayerRules.QuantizeFeet(point.Z));
                }
                return string.Join(";", parts);
            }
            catch { return null; }
        }

        /// <summary>
        /// Everything about one insert that the post-commit check compares. It is long on
        /// purpose: the previous implementation captured five facts and rebuilt the door
        /// from them, so a window came back at its family's default sill height with every
        /// project parameter blank, and nothing in the reply said so.
        /// </summary>
        public static InsertSnapshot Snapshot(Document doc, FamilyInstance instance)
        {
            var snapshot = new InsertSnapshot
            {
                ElementId = Rid.Value(instance.Id),
                UniqueId = SafeUniqueId(instance)
            };

            try { snapshot.SymbolId = instance.Symbol == null ? 0 : Rid.Value(instance.Symbol.Id); } catch { }
            try { snapshot.HostId = instance.Host == null ? 0 : Rid.Value(instance.Host.Id); } catch { }
            try { snapshot.LevelId = instance.LevelId == null ? 0 : Rid.Value(instance.LevelId); } catch { }
            try { snapshot.Point = (instance.Location as LocationPoint)?.Point; } catch { }
            try { snapshot.HandFlipped = instance.HandFlipped; } catch { }
            try { snapshot.FacingFlipped = instance.FacingFlipped; } catch { }
            try { snapshot.Mirrored = instance.Mirrored; } catch { }
            try { snapshot.FacingOrientation = instance.FacingOrientation; } catch { }
            try { snapshot.PhaseCreated = Rid.Value(instance.CreatedPhaseId); } catch { }
            try { snapshot.PhaseDemolished = Rid.Value(instance.DemolishedPhaseId); } catch { }
            try { snapshot.WorksetId = instance.WorksetId == null ? -1 : instance.WorksetId.IntegerValue; } catch { }
            try { snapshot.DesignOptionId = instance.DesignOption == null ? 0 : Rid.Value(instance.DesignOption.Id); } catch { }
            try { snapshot.Pinned = instance.Pinned; } catch { }
            try
            {
                var point = instance.Location as LocationPoint;
                if (point != null) { snapshot.Rotation = point.Rotation; snapshot.RotationRead = true; }
            }
            catch { snapshot.RotationRead = false; }
            try { snapshot.Bounds = instance.get_BoundingBox(null); } catch { }

            try
            {
                ICollection<ElementId> subs = instance.GetSubComponentIds();
                snapshot.SubComponentCount = subs == null ? 0 : subs.Count;
                foreach (ElementId id in subs ?? new List<ElementId>())
                {
                    Element sub = doc.GetElement(id);
                    if (sub == null) continue;
                    snapshot.SubComponentUniqueIds.Add(SafeUniqueId(sub));
                    if (sub is FamilyInstance nested && nested.Symbol != null)
                        snapshot.SubComponentSymbolIds.Add(Rid.Value(nested.Symbol.Id));
                }
            }
            catch { }

            return snapshot;
        }

        /// <summary>
        /// A parameter's identity that survives a language change. Display names are what
        /// the previous implementation would have had to match on, and a Spanish Revit
        /// spells every one of them differently.
        /// </summary>
        public static string StableParameterKey(Parameter parameter)
        {
            try
            {
                if (parameter == null || parameter.Definition == null) return null;
                if (parameter.IsShared) return "guid:" + parameter.GUID.ToString("N");
                if (parameter.Definition is InternalDefinition internalDefinition)
                {
                    BuiltInParameter bip = internalDefinition.BuiltInParameter;
                    if (bip != BuiltInParameter.INVALID) return "bip:" + bip;
                }
                return "def:" + parameter.Definition.Name;
            }
            catch { return null; }
        }

        public static string RenderParameter(Parameter parameter)
        {
            try
            {
                if (parameter == null || !parameter.HasValue) return "<none>";
                switch (parameter.StorageType)
                {
                    case StorageType.Double: return "d:" + WallLayerRules.QuantizeFeet(parameter.AsDouble());
                    case StorageType.Integer: return "i:" + parameter.AsInteger();
                    case StorageType.String: return "s:" + (parameter.AsString() ?? "");
                    case StorageType.ElementId: return "e:" + Rid.Value(parameter.AsElementId());
                    default: return "<none>";
                }
            }
            catch { return "<unreadable>"; }
        }

        /// <summary>
        /// Capture the joins so they can be RESTORED and then proved. Every reader is
        /// tri-state: a fact that could not be read is marked unread rather than defaulted,
        /// because "Revit would not tell me whether this end may be joined" and "this end
        /// may be joined" are different answers and only one of them is safe to act on.
        /// </summary>
        private static WallJoinFacts ReadJoins(Document doc, Wall wall)
        {
            var facts = new WallJoinFacts();

            try
            {
                foreach (ElementId id in JoinGeometryUtils.GetJoinedElements(doc, wall))
                {
                    long raw = Rid.Value(id);
                    if (raw <= 0) continue;
                    facts.GeometricJoinIds.Add(raw);

                    Element other = doc.GetElement(id);
                    if (other == null) continue;
                    try { facts.CutByOther[raw] = JoinGeometryUtils.IsCuttingElementInJoin(doc, wall, other); }
                    catch { }
                }
                facts.GeometricJoinIds.Sort();
            }
            catch { }

            try
            {
                facts.JoinAllowedAtEnd0 = WallUtils.IsWallJoinAllowedAtEnd(wall, 0);
                facts.JoinAllowedAtEnd1 = WallUtils.IsWallJoinAllowedAtEnd(wall, 1);
                facts.EndFlagsRead = true;
            }
            catch { facts.EndFlagsRead = false; }

            try
            {
                var location = wall.Location as LocationCurve;
                if (location != null)
                {
                    foreach (Element element in location.get_ElementsAtJoin(0))
                        if (element != null) facts.ElementsAtEnd0.Add(Rid.Value(element.Id));
                    foreach (Element element in location.get_ElementsAtJoin(1))
                        if (element != null) facts.ElementsAtEnd1.Add(Rid.Value(element.Id));
                    facts.ElementsAtJoinRead = true;
                }
            }
            catch { facts.ElementsAtJoinRead = false; }

            return facts;
        }

        // ---- the fingerprint ------------------------------------------------------

        private static string Fingerprint(string documentKey, WallSplitSubject subject, string coreCarrierPolicy)
        {
            var curveFacts = new List<double>();
            Curve curve = subject.LocationCurve;
            try
            {
                XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
                curveFacts.AddRange(new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z });
                if (curve is Arc arc)
                {
                    curveFacts.AddRange(new[] { arc.Center.X, arc.Center.Y, arc.Center.Z, arc.Radius });
                    XYZ mid = arc.Evaluate(0.5, true);
                    curveFacts.AddRange(new[] { mid.X, mid.Y, mid.Z });
                }
            }
            catch { }

            return WallLayerRules.WallPlanFingerprint(
                documentKey, subject.UniqueId, subject.ElementId, subject.Assembly, subject.Plan,
                subject.Flipped, curveFacts,
                // WHOLE-STATE fingerprints, not bare ids: a door that moved between the dry
                // run and the apply has to change this number, and a list of UniqueIds
                // cannot see that.
                subject.Dependencies.Where(d => d.Snapshot != null).Select(d => FingerprintOf(d.Snapshot)),
                FingerprintOf(subject.Joins),
                WallStateFingerprint(subject.Wall),
                coreCarrierPolicy);
        }

        // ---- small readers --------------------------------------------------------

        public static int ReadInt(Element element, BuiltInParameter parameter, int fallback)
        {
            try
            {
                Parameter p = element.get_Parameter(parameter);
                return p == null || !p.HasValue ? fallback : p.AsInteger();
            }
            catch { return fallback; }
        }

        private static string ReadIdText(Element element, BuiltInParameter parameter)
        {
            try
            {
                Parameter p = element.get_Parameter(parameter);
                return p == null || !p.HasValue ? "" : Rid.Value(p.AsElementId()).ToString();
            }
            catch { return ""; }
        }

        public static string SafeName(Element element)
        {
            try { return element == null ? "" : element.Name ?? ""; } catch { return ""; }
        }

        public static string SafeUniqueId(Element element)
        {
            try { return element == null ? null : element.UniqueId; } catch { return null; }
        }
    }
}
