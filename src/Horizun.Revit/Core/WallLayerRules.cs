// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHERE EACH LAYER OF A COMPOUND WALL GOES, decided as arithmetic.
//
// horizun_split_multilayer_walls turns one compound wall into one wall per
// material layer. Doing that needs a live Revit; deciding it does not, and the
// deciding is the half that has to be provably right, because every one of these
// rules guards a specific way of committing a wrong building and calling it
// verified. The implementation this replaces got four of them wrong at once:
//
//   * THE CORE IS NOT "THE FIRST STRUCTURE LAYER". MaterialFunctionAssignment
//     .Structure, the core boundaries from GetFirst/LastCoreLayerIndex, the
//     geometric centre of the core and the centre of the whole wall are four
//     different things. The old code fell back to layer 0 - the outermost
//     finish - whenever no layer was Function=Structure, and hosted the doors
//     there. Here a wall with no valid core is REFUSED by name.
//
//   * THE LOCATION CURVE IS NOT THE CENTRELINE. WallLocationLine has six values.
//     The old arithmetic (start at total/2 and walk inwards) is exactly the
//     WallCenterline case, applied to all six - so a wall drawn on its exterior
//     finish face came out displaced by half its thickness, committed and
//     "verified". The equation below is that special case generalised.
//
//   * A LAYER OF ZERO WIDTH IS NOT ABSENT. Dropping membranes before summing
//     moved every offset after them. Here they keep their index, keep their
//     NUMBER, and are reported as not materialised - a membrane cannot be a
//     wall, and it cannot vanish from the assembly in silence either.
//
//   * A TYPE IS NOT ITS NAME. Reusing the first WallType whose name matched put
//     somebody else's compound structure on real geometry. Identity here is a
//     digest over material identity, width, function and core membership; the
//     name is for people, and it is checked against the digest before reuse.
//
// Revit-free on purpose: no `using Autodesk`. The command reads the facts off
// the live wall and hands this file plain strings, doubles and ints; the tests
// prove the rules without a building. Sign conventions that CANNOT be settled at
// a desk - which way the exterior normal points - are deliberately not decided
// here: the command measures them off the solid and feeds the answer in.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// The closed vocabulary this capability answers in. A caller branches on these,
    /// so they are constants rather than sentences: the sentence can be improved, the
    /// code cannot change without being a contract change.
    /// </summary>
    public static class WallSplitCodes
    {
        public const string SchemaVersion = "wall_split_v1";

        // ---- eligibility -------------------------------------------------------
        public const string NotAWall = "not_a_wall";
        public const string NotBasicWall = "not_basic_wall";
        public const string NoCompoundStructure = "no_compound_structure";
        public const string SingleLayer = "single_layer";
        public const string NoValidCore = "no_valid_core";
        public const string UnsupportedLocationLine = "unsupported_location_line";
        public const string UnsupportedCurve = "unsupported_curve";
        public const string DegenerateLayerRadius = "degenerate_layer_radius";
        public const string UnsupportedCrossSection = "unsupported_cross_section";
        public const string UnsupportedEditedProfile = "unsupported_edited_profile";
        public const string UnsupportedAttachedWall = "unsupported_attached_wall";
        public const string UnsupportedGroupMember = "unsupported_group_member";
        public const string UnsupportedDesignOption = "unsupported_design_option";
        public const string UnsupportedStackedWall = "unsupported_stacked_wall";
        public const string ElementNotEditable = "element_not_editable";
        public const string UnsupportedDependency = "unsupported_dependency";

        // ---- plan --------------------------------------------------------------
        public const string StalePlan = "stale_plan";
        public const string AlreadySplit = "already_split";
        public const string ExistingPlanConflict = "existing_plan_conflict";
        public const string RepairablePartialState = "repairable_partial_state";
        public const string NotSplit = "not_split";
        public const string ProvenanceInvalid = "provenance_invalid";

        // ---- execution and verification ---------------------------------------
        public const string TypeCreationFailed = "type_creation_failed";
        public const string CarrierConversionFailed = "carrier_conversion_failed";
        public const string VerifyCarrierIdentity = "verify_carrier_identity";
        public const string VerifyTypeMismatch = "verify_type_mismatch";
        public const string VerifyLayerGeometry = "verify_layer_geometry";
        public const string VerifyInsertIdentity = "verify_insert_identity";
        public const string VerifyInsertHost = "verify_insert_host";
        public const string VerifyInsertPlacement = "verify_insert_placement";
        public const string VerifyInsertSubcomponents = "verify_insert_subcomponents";
        public const string VerifyOpeningMissing = "verify_opening_missing";
        public const string VerifyJoinMissing = "verify_join_missing";

        /// <summary>An edge was made between two layer walls that do not touch.</summary>
        public const string VerifyJoinDisjoint = "verify_join_disjoint";

        /// <summary>The graph carries an edge the chain does not call for - an extra
        /// one between siblings, or one to an element outside the set.</summary>
        public const string VerifyJoinUnexpected = "verify_join_unexpected";
        public const string VerifyParameterMismatch = "verify_parameter_mismatch";
        public const string VerifyUnexpectedWarning = "verify_unexpected_warning";
        public const string VerifyDependencyIdentity = "verify_dependency_identity";
        public const string VerifyDependencyRelation = "verify_dependency_relation";
        public const string VerifyDependencyGeometry = "verify_dependency_geometry";
        public const string VerifyJoinNotRestored = "verify_join_not_restored";
        public const string ProvenanceVerificationFailed = "provenance_verification_failed";
        public const string VerifySiblingSetIncomplete = "verify_sibling_set_incomplete";

        // ---- structural dependencies ------------------------------------------
        public const string VerifyFoundationRelation = "verify_foundation_relation";
        public const string VerifyFoundationGeometry = "verify_foundation_geometry";
        public const string VerifyRebarIdentity = "verify_rebar_identity";
        public const string VerifyRebarLayout = "verify_rebar_layout";

        /// <summary>
        /// The bar was inside the compound wall and is outside the single-layer core.
        /// Named by the mandate, and deliberately NOT a "we moved it to fit": relocating
        /// reinforcement is a structural decision, not a side effect of splitting a wall.
        /// </summary>
        public const string RebarOutsideCoreCarrier = "rebar_outside_core_carrier";

        public const string VerifyReinforcementMembers = "verify_reinforcement_members";

        // NOTE. There is no verify_cover_changed. The reinforcement cover is part of the
        // WALL's own state fingerprint, so a cover edited between the dry run and the apply
        // comes back as stale_plan before anything is written - and a code that no path can
        // emit is a promise to a client that branches on a value it will never receive.
        public const string UnsupportedReinforcementKind = "unsupported_reinforcement_kind";

        /// <summary>Every code above, so a test can prove nothing is emitted off-vocabulary.</summary>
        public static readonly string[] All =
        {
            NotAWall, NotBasicWall, NoCompoundStructure, SingleLayer, NoValidCore,
            UnsupportedLocationLine,
            UnsupportedCurve, DegenerateLayerRadius, UnsupportedCrossSection,
            UnsupportedEditedProfile, UnsupportedAttachedWall, UnsupportedGroupMember,
            UnsupportedDesignOption, UnsupportedStackedWall, ElementNotEditable,
            UnsupportedDependency,
            StalePlan, AlreadySplit, ExistingPlanConflict,
            RepairablePartialState, NotSplit, ProvenanceInvalid,
            TypeCreationFailed, CarrierConversionFailed, VerifyCarrierIdentity,
            VerifyTypeMismatch, VerifyLayerGeometry, VerifyInsertIdentity,
            VerifyInsertHost, VerifyInsertPlacement, VerifyInsertSubcomponents,
            VerifyOpeningMissing, VerifyJoinMissing, VerifyJoinDisjoint, VerifyJoinUnexpected, VerifyParameterMismatch,
            VerifyUnexpectedWarning, VerifyDependencyIdentity, VerifyDependencyRelation,
            VerifyDependencyGeometry, VerifyJoinNotRestored, ProvenanceVerificationFailed,
            VerifySiblingSetIncomplete,
            VerifyFoundationRelation, VerifyFoundationGeometry, VerifyRebarIdentity, VerifyRebarLayout,
            RebarOutsideCoreCarrier, VerifyReinforcementMembers,
            UnsupportedReinforcementKind
        };
    }

    /// <summary>
    /// How one dependency of the wall survives, in the closed vocabulary the mandate
    /// fixes. There is deliberately no "warning" member: a potential loss is
    /// <see cref="UnsupportedBlocking"/> or it is nothing, and a generic warning is
    /// how the previous implementation described losing somebody's doors.
    /// </summary>
    public static class DependencyDisposition
    {
        /// <summary>Still hangs off the original element, which is never deleted. Verified anyway.</summary>
        public const string PreservedByIdentity = "preserved_by_identity";

        /// <summary>Recreated and re-read from the model (the secondary layers' cuts).</summary>
        public const string ReconstructableAndVerified = "reconstructable_and_verified";

        /// <summary>Its reference was re-pointed and re-read.</summary>
        public const string ReferenceReboundAndVerified = "reference_rebound_and_verified";

        /// <summary>Equivalence cannot be guaranteed. The wall is refused before any transaction.</summary>
        public const string UnsupportedBlocking = "unsupported_blocking";

        /// <summary>Looked for, and absent. NOT the same as "not looked at".</summary>
        public const string NotApplicable = "not_applicable";

        public static readonly string[] All =
        {
            PreservedByIdentity, ReconstructableAndVerified, ReferenceReboundAndVerified,
            UnsupportedBlocking, NotApplicable
        };

        public static bool IsKnown(string value) => All.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>
    /// WHAT KIND OF THING hangs off the wall, and - the point of this type - whether a
    /// host-side VERIFIER exists for it.
    ///
    /// The rule is closed and it is the whole reason this exists: a dependency may only be
    /// called <see cref="DependencyDisposition.PreservedByIdentity"/> if something is going
    /// to re-read it after the transformation and prove it. The first version of this
    /// capability classified openings, sweeps, reveals, embedded curtain walls, dimensions,
    /// tags and "everything else" as preserved by identity, and then verified only the
    /// family instances - so the contract asserted preservation for six classes nobody
    /// looked at, which is the same failure as the implementation it replaced, one level up.
    ///
    /// A kind with no verifier is <see cref="DependencyDisposition.UnsupportedBlocking"/>,
    /// and the wall is refused before a transaction exists. Adding a class to this list
    /// without adding its verifier fails a test.
    /// </summary>
    public static class DependencyKinds
    {
        public const string FamilyInstance = "family_instance";
        public const string Opening = "opening";
        public const string WallSweep = "wall_sweep";
        public const string Reveal = "reveal";
        public const string EmbeddedWall = "embedded_wall";
        public const string Dimension = "dimension";
        public const string Tag = "tag";

        // ---- structural ---------------------------------------------------------
        //
        // Each gets its OWN kind rather than one generic "structural" bucket, because each
        // needs a different snapshot, a different verifier and a different failure code. A
        // continuous footing is not a bar set and neither is a fabric sheet; collapsing them
        // would mean verifying all three the way the weakest one can be verified.
        public const string WallFoundation = "wall_foundation";
        public const string Rebar = "rebar";
        public const string RebarContainer = "rebar_container";
        public const string AreaReinforcement = "area_reinforcement";
        public const string PathReinforcement = "path_reinforcement";
        public const string FabricArea = "fabric_area";
        public const string FabricSheet = "fabric_sheet";

        /// <summary>Inspected, and structurally incapable of being lost: sketches, types.</summary>
        public const string Structural = "structural_not_an_instance";

        /// <summary>Anything this capability does not recognise. Always blocking.</summary>
        public const string Unrecognised = "unrecognised";

        /// <summary>
        /// The kinds a host-side verifier exists for. Order is the order they are reported.
        /// </summary>
        public static readonly string[] WithVerifier =
        {
            FamilyInstance, Opening, WallSweep, Reveal, EmbeddedWall, Dimension, Tag,
            WallFoundation, Rebar, RebarContainer,
            AreaReinforcement, PathReinforcement, FabricArea, FabricSheet
        };

        public static readonly string[] All =
        {
            FamilyInstance, Opening, WallSweep, Reveal, EmbeddedWall, Dimension, Tag,
            WallFoundation, Rebar, RebarContainer,
            AreaReinforcement, PathReinforcement, FabricArea, FabricSheet,
            Structural, Unrecognised
        };

        /// <summary>
        /// The reinforcement kinds, for the rules that apply to all of them and to nothing
        /// else - containment inside the carrier, member-set preservation, cover.
        /// </summary>
        public static readonly string[] Reinforcement =
        {
            Rebar, RebarContainer, AreaReinforcement, PathReinforcement, FabricArea, FabricSheet
        };

        public static bool IsReinforcement(string kind)
            => kind != null && Reinforcement.Contains(kind, StringComparer.Ordinal);

        public static bool HasVerifier(string kind)
            => kind != null && WithVerifier.Contains(kind, StringComparer.Ordinal);

        /// <summary>
        /// The ONLY way a disposition is chosen. There is no branch anywhere that hands out
        /// preserved_by_identity directly, which is what makes the coverage rule hold.
        /// </summary>
        public static string DispositionFor(string kind)
        {
            if (string.Equals(kind, Structural, StringComparison.Ordinal))
                return DependencyDisposition.NotApplicable;
            return HasVerifier(kind)
                ? DependencyDisposition.PreservedByIdentity
                : DependencyDisposition.UnsupportedBlocking;
        }
    }

    /// <summary>What a resulting wall is, for provenance and for the reader.</summary>
    public static class LayerRole
    {
        public const string CoreCarrier = "core_carrier";
        public const string CoreSecondary = "core_secondary";
        public const string Shell = "shell";
        public const string Finish = "finish";

        public static readonly string[] All = { CoreCarrier, CoreSecondary, Shell, Finish };
    }

    /// <summary>
    /// One layer of the source wall's CompoundStructure, as plain facts. The command
    /// reads these off the live layer; nothing here knows what a Revit type is.
    /// </summary>
    public sealed class WallLayerFacts
    {
        /// <summary>Position in the CompoundStructure, 0-based, exterior first.</summary>
        public int Index { get; set; }

        /// <summary>Layer thickness in internal feet. Zero is legal: it is a membrane.</summary>
        public double WidthFeet { get; set; }

        /// <summary>
        /// The material's UniqueId - the stable identity. Null or empty means the layer
        /// carries no material, or one that was deleted.
        /// </summary>
        public string MaterialUniqueId { get; set; }

        /// <summary>The material's name, VERBATIM. Never translated, shortened or reworded.</summary>
        public string MaterialName { get; set; }

        /// <summary>MaterialFunctionAssignment rendered as its enum name.</summary>
        public string Function { get; set; }

        /// <summary>True when CompoundStructure marks this layer as the variable-width one.</summary>
        public bool IsVariableWidth { get; set; }

        /// <summary>Deck profile UniqueId when the layer carries one; null otherwise.</summary>
        public string DeckProfileUniqueId { get; set; }

        /// <summary>Deck embedding type rendered as its enum name; null when not a deck.</summary>
        public string DeckEmbeddingType { get; set; }

        /// <summary>Whether this layer's width counts as real volume - i.e. it can become a wall.</summary>
        public bool HasVolume => WidthFeet > WallLayerRules.ToleranceFeet;
    }

    /// <summary>
    /// The source wall's assembly, as plain facts. `CoreFirstIndex` and `CoreLastIndex`
    /// come from CompoundStructure.GetFirstCoreLayerIndex()/GetLastCoreLayerIndex() -
    /// they are NOT "the first Structure layer", and conflating the two is the defect
    /// this type exists to make impossible to repeat.
    /// </summary>
    public sealed class WallAssemblyFacts
    {
        public string WallTypeName { get; set; }
        public string WallTypeUniqueId { get; set; }

        /// <summary>WallKind rendered as its enum name: Basic, Curtain, Stacked, Unknown.</summary>
        public string WallKind { get; set; }

        /// <summary>WallLocationLine rendered as its enum name.</summary>
        public string LocationLine { get; set; }

        public int CoreFirstIndex { get; set; }
        public int CoreLastIndex { get; set; }

        public List<WallLayerFacts> Layers { get; set; } = new List<WallLayerFacts>();

        public string OpeningWrapping { get; set; }
        public string EndCap { get; set; }
    }

    /// <summary>Where one layer goes, what it is called, and what it is.</summary>
    public sealed class WallLayerPlan
    {
        /// <summary>0-based position in the CompoundStructure.</summary>
        public int LayerIndex { get; set; }

        /// <summary>1-based, exterior is always 1. Zero-width layers keep their number.</summary>
        public int LayerNumber { get; set; }

        /// <summary>The number as it appears in a type name: at least two digits.</summary>
        public string LayerNumberText { get; set; }

        public double WidthFeet { get; set; }

        /// <summary>Centre of this layer on the `u` axis (0 at the exterior face).</summary>
        public double CenterUFeet { get; set; }

        /// <summary>
        /// Signed distance from the wall's LocationCurve to this layer's centre, measured
        /// ALONG THE EXTERIOR NORMAL. Positive means towards the exterior.
        /// </summary>
        public double ExpectedOffsetFeet { get; set; }

        public double ExpectedOffsetMm => WallLayerRules.FeetToMm(ExpectedOffsetFeet);

        public bool IsCore { get; set; }
        public bool IsCoreCarrier { get; set; }

        /// <summary>False for a zero-width membrane: it cannot be a wall.</summary>
        public bool Materialised { get; set; }

        /// <summary>Why it is not materialised. Null when it is.</summary>
        public string NotMaterialisedReason { get; set; }

        public string Role { get; set; }

        /// <summary>The material name after resolution: MATERIAL_SIN_ASIGNAR when there is none.</summary>
        public string MaterialName { get; set; }

        public string SourceWallTypeName { get; set; }

        /// <summary>[TIPO ORIGINAL] - [MATERIAL] - [NN]</summary>
        public string ExpectedTypeName { get; set; }

        /// <summary>The deterministic fallback when that name exists with a different composition.</summary>
        public string VariantTypeName { get; set; }

        public string TypeFingerprint { get; set; }
        public string ShortDigest { get; set; }
    }

    /// <summary>Why a wall is not being split. Always carries a code from the closed set.</summary>
    public sealed class WallSplitRejection
    {
        public string Code { get; }
        public string Message { get; }

        public WallSplitRejection(string code, string message)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("A rejection must name its code - a client branches on it.", nameof(code));
            Code = code;
            Message = message ?? "";
        }
    }

    /// <summary>
    /// The whole decision for one wall: refused, or a layer plan with a named carrier.
    /// Exactly one of <see cref="Rejection"/> and <see cref="Layers"/> is meaningful.
    /// </summary>
    public sealed class WallSplitPlan
    {
        public WallSplitRejection Rejection { get; set; }
        public bool Eligible => Rejection == null;

        public List<WallLayerPlan> Layers { get; set; } = new List<WallLayerPlan>();

        public int CoreFirstLayerIndex { get; set; }
        public int CoreLastLayerIndex { get; set; }
        public int CoreCarrierLayerIndex { get; set; }
        public string CoreCarrierSelectionReason { get; set; }

        public string OriginalLocationLine { get; set; }

        /// <summary>Signed offset from the LocationCurve to the CORE's geometric centre.</summary>
        public double OriginalCoreCenterOffsetFeet { get; set; }

        public double TotalWidthFeet { get; set; }

        /// <summary>Position of the LocationCurve on the `u` axis.</summary>
        public double LocationUFeet { get; set; }

        /// <summary>How many independent walls this produces. Layers with volume, no more, no fewer.</summary>
        public int WouldProduceWalls => Layers.Count(l => l.Materialised);
    }

    /// <summary>
    /// A deterministic digest over named facts. Every fingerprint in this capability is
    /// built here so that all of them share one set of properties, and so those properties
    /// can be PROVED rather than assumed per call site:
    ///
    ///   * ORDER-FREE. Facts are keyed and sorted ordinally, so the order a caller happens
    ///     to add them in - or the order a Dictionary happens to enumerate in - cannot
    ///     change the digest. A fingerprint that moved when a collector re-ordered would
    ///     refuse every honest apply.
    ///
    ///   * QUANTISED. Every length rides the 0.1 mm grid, so regeneration jitter keeps the
    ///     identity while a real 0.2 mm move changes it.
    ///
    ///   * FAIL-CLOSED. A duplicate key throws instead of silently shadowing, and a NaN or
    ///     infinity throws instead of fingerprinting a number nobody measured. Both are
    ///     ways a digest can look stable while meaning nothing.
    ///
    ///   * EXPLICIT ABOUT ORDER WHERE IT MATTERS. A list is added either as ordered (the
    ///     elements meeting a wall at one end, where the order is the join order) or as
    ///     unordered (a set of dependency ids, where it is not). The caller says which.
    /// </summary>
    public sealed class FactBook
    {
        private readonly SortedDictionary<string, string> _facts =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        public FactBook(string schema = null)
        {
            Add("schema", schema ?? WallSplitCodes.SchemaVersion);
        }

        public FactBook Add(string name, string value) => Store(name, "s:" + (value ?? ""));

        public FactBook Add(string name, long value)
            => Store(name, "i:" + value.ToString(CultureInfo.InvariantCulture));

        public FactBook Add(string name, int value) => Add(name, (long)value);

        public FactBook Add(string name, bool value) => Store(name, value ? "b:1" : "b:0");

        /// <summary>A length in internal feet, quantised on the 0.1 mm grid.</summary>
        public FactBook AddFeet(string name, double feet)
        {
            if (double.IsNaN(feet) || double.IsInfinity(feet))
                throw new ArgumentException(
                    "Fact '" + name + "' is not a finite number; a fingerprint over it would be an identity for " +
                    "something nobody measured.", nameof(feet));
            return Store(name, "q:" + WallLayerRules.QuantizeFeet(feet).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>An angle in radians, quantised finely enough that a real rotation shows.</summary>
        public FactBook AddAngle(string name, double radians)
        {
            if (double.IsNaN(radians) || double.IsInfinity(radians))
                throw new ArgumentException("Fact '" + name + "' is not a finite angle.", nameof(radians));
            long ticks = (long)Math.Round(radians * 1e6, MidpointRounding.AwayFromZero);
            return Store(name, "a:" + ticks.ToString(CultureInfo.InvariantCulture));
        }

        public FactBook AddPoint(string name, double xFeet, double yFeet, double zFeet)
        {
            AddFeet(name + ".x", xFeet);
            AddFeet(name + ".y", yFeet);
            AddFeet(name + ".z", zFeet);
            return this;
        }

        /// <summary>
        /// A list of values. `ordered` is not a convenience: the elements meeting a wall at
        /// one end carry an order that is part of the fact, and a set of dependency ids does
        /// not. Sorting the first would hide a real change; not sorting the second would
        /// invent one.
        /// </summary>
        public FactBook AddList(string name, IEnumerable<string> values, bool ordered)
        {
            List<string> items = (values ?? Enumerable.Empty<string>()).Select(v => v ?? "").ToList();
            if (!ordered) items.Sort(StringComparer.Ordinal);
            Add(name + ".count", items.Count);
            return Store(name, "l:" + string.Join("", items));
        }

        public FactBook AddList(string name, IEnumerable<long> values, bool ordered)
            => AddList(name, (values ?? Enumerable.Empty<long>())
                             .Select(v => v.ToString(CultureInfo.InvariantCulture)), ordered);

        /// <summary>
        /// A map of names to values. Always order-free: a Dictionary's enumeration order is
        /// not a fact about the model.
        /// </summary>
        public FactBook AddMap(string name, IEnumerable<KeyValuePair<string, string>> entries)
        {
            var items = (entries ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .Select(e => (e.Key ?? "") + "" + (e.Value ?? ""))
                .OrderBy(e => e, StringComparer.Ordinal)
                .ToList();
            Add(name + ".count", items.Count);
            return Store(name, "m:" + string.Join("", items));
        }

        /// <summary>A nested digest, so a compound fact is one fact here.</summary>
        public FactBook AddDigest(string name, string digest) => Store(name, "d:" + (digest ?? ""));

        private FactBook Store(string name, string rendered)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A fact needs a name.", nameof(name));
            if (_facts.ContainsKey(name))
                throw new ArgumentException(
                    "Fact '" + name + "' was added twice; the second value would silently shadow the first inside " +
                    "the fingerprint.", nameof(name));
            _facts[name] = rendered;
            return this;
        }

        /// <summary>The names in this book, sorted. For tests that pin what a fingerprint covers.</summary>
        public IReadOnlyList<string> Names => _facts.Keys.ToList();

        public string Digest()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in _facts)
                sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }
    }

    public static class WallLayerRules
    {
        // ---- units and tolerance --------------------------------------------------
        //
        // Revit's internal unit is the decimal foot. The 0.1 mm quantisation grid is
        // the one every other fingerprint in this codebase already uses, so identity
        // survives regeneration jitter and a real move changes it.
        public const double MmPerFoot = 304.8;
        public const double TicksPerFoot = 3048.0;

        /// <summary>
        /// The one geometric tolerance. Reported to callers in millimetres so nobody has
        /// to know what an internal foot is. Nothing in this file compares doubles for
        /// equality; every acceptance is a measured distance against this.
        /// </summary>
        public const double ToleranceMm = 0.5;

        public const double ToleranceFeet = ToleranceMm / MmPerFoot;

        /// <summary>Used when the layer's material is absent, deleted, or has no usable name.</summary>
        public const string UnassignedMaterialName = "MATERIAL_SIN_ASIGNAR";

        /// <summary>The separator the naming rule fixes: space, hyphen, space.</summary>
        public const string NameSeparator = " - ";

        /// <summary>
        /// The characters Revit refuses in an element name. Applied to the original type
        /// name and to the material name, and to NOTHING else: the rule is "clean only
        /// what Revit prohibits", not "normalise the name".
        /// </summary>
        public static readonly char[] ForbiddenNameCharacters =
            { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        public static double FeetToMm(double feet) => feet * MmPerFoot;
        public static double MmToFeet(double mm) => mm / MmPerFoot;

        public static long QuantizeFeet(double feet)
            => (long)Math.Round(feet * TicksPerFoot, MidpointRounding.AwayFromZero);

        /// <summary>
        /// The deviation between what was planned and what the model actually shows,
        /// in millimetres. The sign is dropped on purpose: a caller asks "how far off",
        /// and "which side" is already in the two offsets it can print.
        /// </summary>
        public static double DeviationMm(double expectedFeet, double observedFeet)
            => Math.Abs(FeetToMm(observedFeet - expectedFeet));

        public static bool WithinTolerance(double expectedFeet, double observedFeet)
            => DeviationMm(expectedFeet, observedFeet) <= ToleranceMm;

        // ---- the `u` axis ---------------------------------------------------------

        /// <summary>
        /// The centre of each layer on the `u` axis, which runs from 0 at the EXTERIOR
        /// face to T at the interior face. Zero-width layers get a centre too - they
        /// occupy a position even though they occupy no volume - so the layers behind
        /// them are not shifted by their absence.
        /// </summary>
        public static double LayerCenterU(IList<WallLayerFacts> layers, int index)
        {
            if (layers == null) throw new ArgumentNullException(nameof(layers));
            if (index < 0 || index >= layers.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            double before = 0.0;
            for (int i = 0; i < index; i++) before += layers[i].WidthFeet;
            return before + layers[index].WidthFeet / 2.0;
        }

        public static double TotalWidth(IList<WallLayerFacts> layers)
            => layers == null ? 0.0 : layers.Sum(l => l.WidthFeet);

        /// <summary>The `u` coordinate of the core's exterior boundary.</summary>
        public static double CoreExteriorU(IList<WallLayerFacts> layers, int coreFirstIndex)
        {
            double u = 0.0;
            for (int i = 0; i < coreFirstIndex && i < layers.Count; i++) u += layers[i].WidthFeet;
            return u;
        }

        /// <summary>The `u` coordinate of the core's interior boundary.</summary>
        public static double CoreInteriorU(IList<WallLayerFacts> layers, int coreLastIndex)
        {
            double u = 0.0;
            for (int i = 0; i <= coreLastIndex && i < layers.Count; i++) u += layers[i].WidthFeet;
            return u;
        }

        /// <summary>
        /// Where the LocationCurve sits on the `u` axis, from WallLocationLine alone.
        ///
        /// This is the whole of the fix for the old arithmetic. Every one of the six
        /// answers is determined by widths this file already has, so nothing here
        /// depends on the sign convention of CompoundStructure.GetOffsetForLocationLine -
        /// which the mandate rightly requires to be validated by measurement rather than
        /// trusted from documentation. That API is read by the command and reported as a
        /// CONTRAST; it is never the source.
        /// </summary>
        public static bool TryLocationU(string locationLine, IList<WallLayerFacts> layers,
                                        int coreFirstIndex, int coreLastIndex,
                                        out double u, out string error)
        {
            u = 0.0;
            error = null;
            if (layers == null || layers.Count == 0)
            {
                error = "a wall with no layers has no `u` axis.";
                return false;
            }

            double total = TotalWidth(layers);
            double coreExt = CoreExteriorU(layers, coreFirstIndex);
            double coreInt = CoreInteriorU(layers, coreLastIndex);

            switch (locationLine)
            {
                case "WallCenterline": u = total / 2.0; return true;
                case "CoreCenterline": u = (coreExt + coreInt) / 2.0; return true;
                case "FinishFaceExterior": u = 0.0; return true;
                case "FinishFaceInterior": u = total; return true;
                case "CoreExterior": u = coreExt; return true;
                case "CoreInterior": u = coreInt; return true;
                default:
                    error = "wall location line '" + (locationLine ?? "<null>") + "' is not one of the six this " +
                            "capability understands (WallCenterline, CoreCenterline, FinishFaceExterior, " +
                            "FinishFaceInterior, CoreExterior, CoreInterior). Assuming the centreline is exactly " +
                            "the defect that displaced every layer by half a wall.";
                    return false;
            }
        }

        /// <summary>
        /// The signed offset from the LocationCurve to a layer's centre, measured along
        /// the EXTERIOR normal. Positive is towards the exterior.
        ///
        ///     offset_i = u_loc - c_i
        ///
        /// Sanity: with u_loc = T/2 this reduces exactly to the old implementation's
        /// walk from total/2 inwards - i.e. the old code was this equation with
        /// WallCenterline hard-coded into all six cases.
        /// </summary>
        public static double OffsetForLayer(double locationU, double layerCenterU)
            => locationU - layerCenterU;

        // ---- core carrier ---------------------------------------------------------

        /// <summary>
        /// Is this a usable core? A core needs a valid index range AND at least one layer
        /// inside it with real volume: a "core" made of two membranes cannot carry a door.
        /// </summary>
        public static bool HasValidCore(IList<WallLayerFacts> layers, int first, int last)
        {
            if (layers == null || layers.Count == 0) return false;
            if (first < 0 || last < 0) return false;
            if (first > last) return false;
            if (first >= layers.Count || last >= layers.Count) return false;
            for (int i = first; i <= last; i++)
                if (layers[i].HasVolume) return true;
            return false;
        }

        /// <summary>
        /// Which layer the ORIGINAL wall becomes, and why - the mandate's order, exactly,
        /// with every branch naming itself. There is deliberately no fallback to layer 0:
        /// a wall without a valid core is refused by <see cref="Plan"/> before this runs.
        ///
        /// Only layers WITH VOLUME are candidates. A zero-width membrane inside the core
        /// cannot be the element that carries the doors.
        /// </summary>
        public static int SelectCoreCarrier(IList<WallLayerFacts> layers, int first, int last, out string reason)
        {
            if (!HasValidCore(layers, first, last))
                throw new InvalidOperationException(
                    "SelectCoreCarrier was asked about a wall with no valid core. That is a refusal " +
                    "(" + WallSplitCodes.NoValidCore + "), not a selection - falling back to layer 0 is the " +
                    "defect this method exists to prevent.");

            var candidates = new List<WallLayerFacts>();
            for (int i = first; i <= last; i++)
                if (layers[i].HasVolume) candidates.Add(layers[i]);

            var structural = candidates
                .Where(l => string.Equals(l.Function, "Structure", StringComparison.Ordinal))
                .ToList();

            if (structural.Count == 1)
            {
                reason = "single_structural_layer_in_core";
                return structural[0].Index;
            }

            if (structural.Count > 1)
            {
                WallLayerFacts pick = Thickest(structural, out bool tied);
                reason = tied ? "thickest_structural_layer_in_core_tie_lowest_index"
                              : "thickest_structural_layer_in_core";
                return pick.Index;
            }

            WallLayerFacts fallback = Thickest(candidates, out bool coreTied);
            reason = coreTied ? "thickest_core_layer_no_structural_tie_lowest_index"
                              : "thickest_core_layer_no_structural";
            return fallback.Index;
        }

        /// <summary>
        /// Thickest wins; on a tie the LOWEST ORIGINAL INDEX wins. "Tie" is decided on the
        /// 0.1 mm grid rather than by double equality, so two layers a nanometre apart are
        /// the tie a human would call it.
        /// </summary>
        private static WallLayerFacts Thickest(IList<WallLayerFacts> candidates, out bool tied)
        {
            WallLayerFacts best = candidates[0];
            long bestTicks = QuantizeFeet(best.WidthFeet);
            tied = false;

            for (int i = 1; i < candidates.Count; i++)
            {
                long ticks = QuantizeFeet(candidates[i].WidthFeet);
                if (ticks > bestTicks)
                {
                    best = candidates[i];
                    bestTicks = ticks;
                    tied = false;
                }
                else if (ticks == bestTicks && candidates[i].Index != best.Index)
                {
                    // Same thickness as the incumbent. The incumbent already has the lower
                    // index because the list is walked in index order, so it stays - but the
                    // caller is told the decision came down to the tie-break.
                    tied = true;
                }
            }
            return best;
        }

        // ---- roles ----------------------------------------------------------------

        /// <summary>
        /// What a resulting wall IS. Core layers are the carrier or core_secondary; outside
        /// the core, a Finish1/Finish2 layer is a finish and everything else is shell.
        /// </summary>
        public static string RoleFor(WallLayerFacts layer, bool isCore, bool isCarrier)
        {
            if (isCarrier) return LayerRole.CoreCarrier;
            if (isCore) return LayerRole.CoreSecondary;
            return string.Equals(layer.Function, "Finish1", StringComparison.Ordinal) ||
                   string.Equals(layer.Function, "Finish2", StringComparison.Ordinal)
                ? LayerRole.Finish
                : LayerRole.Shell;
        }

        // ---- naming ---------------------------------------------------------------

        /// <summary>
        /// Remove ONLY what Revit prohibits in a name, and trim the ends. Nothing else is
        /// touched: the original type name arrives complete and the material name arrives
        /// verbatim, because both are somebody's convention and neither is ours to
        /// normalise.
        /// </summary>
        public static string SanitizeNamePart(string value)
        {
            if (value == null) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(Array.IndexOf(ForbiddenNameCharacters, c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// The material name as it enters a type name. Absent, deleted, empty or
        /// whitespace-only all become MATERIAL_SIN_ASIGNAR - one answer, so a reader
        /// never has to wonder whether a blank meant "none" or "unread".
        /// </summary>
        public static string ResolveMaterialName(string rawName)
        {
            string cleaned = SanitizeNamePart(rawName);
            return cleaned.Length == 0 ? UnassignedMaterialName : cleaned;
        }

        /// <summary>
        /// The layer number as it appears in a name: 1-based, exterior is always 01, at
        /// least two digits. "At least" rather than "exactly": an assembly with more than
        /// 99 layers is absurd but it must not silently truncate to two.
        /// </summary>
        public static string FormatLayerNumber(int layerNumber)
        {
            if (layerNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(layerNumber),
                    "Layer numbers are 1-based; the exterior layer is 01.");
            return layerNumber.ToString("D2", CultureInfo.InvariantCulture);
        }

        /// <summary>[NOMBRE DEL TIPO ORIGINAL] - [NOMBRE DEL MATERIAL] - [NN]</summary>
        /// <summary>
        /// Whether this layer's opening cut may be CLAIMED, given what was actually probed.
        ///
        /// Three values, because there are three states and only two of them are a verdict:
        ///   true  - probed, and every ray came back clear
        ///   false - probed, and at least one ray still found material
        ///   null  - NOT PROBED. Not a pass and not a failure; nothing was measured.
        ///
        /// The null is the whole point. This used to be a plain bool computed as an
        /// unguarded .All() over the checks belonging to this layer, and .All() over an
        /// empty sequence is true - so a wall carrying no insert published
        /// `cut_verified: true` for every layer while `cut_coverage.probed` said false one
        /// level down, and so did every zero-width membrane on a wall that DID have a
        /// door. Measured on Revit 2026: seven layers, seven trues, zero rays cast.
        ///
        /// The carrier is null rather than true as well. It keeps its own inserts
        /// natively - there is no hole for it to reproduce - so "verified" would be
        /// describing a test that does not apply to it, which is the same lie in a
        /// smaller font.
        /// </summary>
        public static bool? CutClaim(bool isCoreCarrier, bool materialised,
                                     bool coverageProbed, int checksForLayer, int checksClear)
        {
            if (!materialised) return null;      // no wall exists to probe
            if (isCoreCarrier) return null;      // hosts the inserts; nothing to reproduce
            if (!coverageProbed) return null;    // the probe never ran
            if (checksForLayer <= 0) return null; // it ran, but not on this layer
            return checksClear >= checksForLayer;
        }

        /// <summary>Why no claim is made, in the caller's words. Null when one is.</summary>
        public static string CutNotProbedReason(bool isCoreCarrier, bool materialised,
                                                bool coverageProbed, int checksForLayer)
        {
            if (!materialised)
                return "this layer has no volume, so no wall exists for a hole to pass through";
            if (isCoreCarrier)
                return "this layer IS the carrier: it keeps the original inserts, so there is no hole for it to reproduce";
            if (!coverageProbed)
                return "no cut probe ran on this wall, so nothing is claimed about its holes";
            if (checksForLayer <= 0)
                return "the cut probe ran on this wall but produced no check for this layer";
            return null;
        }

        /// <summary>
        /// What kind of thing a parameter is, which is what decides both whether this
        /// operation may copy it and whether its change needs explaining.
        /// </summary>
        public enum ParameterKind
        {
            /// <summary>The user's own data. Copy it, and a change is a defect.</summary>
            Authored,

            /// <summary>Revit derives it from geometry. Never copy; a change is expected.</summary>
            ComputedByRevit,

            /// <summary>The type owns it. Never copy; on an element whose type did not
            /// change, a change still needs explaining.</summary>
            ControlledByType,

            /// <summary>This operation sets it through a dedicated path, so the generic
            /// copier must not also touch it.</summary>
            SetExplicitly,

            /// <summary>What the element IS. Never copy, and never excuse a change.</summary>
            Identity,

            /// <summary>Derived from surrounding context - which room an opening resolves
            /// into. A thinner carrier legitimately changes it.</summary>
            ContextDerived,
        }

        /// <summary>
        /// THE SINGLE SOURCE. The copier asks ShouldCopy, the verifier asks
        /// MayChangeWithoutExplanation, and both read this one table - so the two can no
        /// longer drift apart the way NeverCopied and AllowedToChange did.
        ///
        /// A parameter that is NOT in this table is treated as Authored: copied, and its
        /// change reported. That default is deliberate and it fails LOUDLY - an unlisted
        /// computed parameter surfaces as a named mismatch, which is exactly how
        /// HOST_AREA_COMPUTED was found. Guessing more names, or excusing anything
        /// read-only, would have hidden it instead.
        /// </summary>
        private static readonly Dictionary<string, ParameterKind> ParameterKinds =
            new Dictionary<string, ParameterKind>(StringComparer.Ordinal)
            {
                // --- Revit computes these from geometry -------------------------------
                // The carrier goes from the full compound thickness to one layer, so the
                // areas and volumes it derives necessarily change. MEASURED: this is the
                // one that rolled back every wall with a door.
                ["bip:HOST_AREA_COMPUTED"] = ParameterKind.ComputedByRevit,
                ["bip:HOST_VOLUME_COMPUTED"] = ParameterKind.ComputedByRevit,
                ["bip:HOST_PERIMETER_COMPUTED"] = ParameterKind.ComputedByRevit,
                ["bip:LAYER_ELEM_AREA_COMPUTED"] = ParameterKind.ComputedByRevit,
                ["bip:LAYER_ELEM_VOLUME_COMPUTED"] = ParameterKind.ComputedByRevit,

                // A bar whose valid face constraints follow a thinner carrier changes
                // these values because Revit recomputes them from the new centreline.
                // They are not authored schedule data and the generic copier must not
                // attempt to restore stale geometry-derived values.
                ["bip:REINFORCEMENT_VOLUME"] = ParameterKind.ComputedByRevit,
                ["bip:REIN_EST_BAR_VOLUME"] = ParameterKind.ComputedByRevit,
                ["bip:REBAR_MIN_LENGTH"] = ParameterKind.ComputedByRevit,
                ["bip:REBAR_MAX_LENGTH"] = ParameterKind.ComputedByRevit,
                ["bip:REBAR_ELEM_LENGTH"] = ParameterKind.ComputedByRevit,
                ["bip:REBAR_ELEM_TOTAL_LENGTH"] = ParameterKind.ComputedByRevit,

                // --- the type owns them ------------------------------------------------
                ["bip:WALL_ATTR_WIDTH_PARAM"] = ParameterKind.ControlledByType,

                // --- this operation sets them through its own path ---------------------
                ["bip:WALL_KEY_REF_PARAM"] = ParameterKind.SetExplicitly,
                ["bip:WALL_BASE_CONSTRAINT"] = ParameterKind.SetExplicitly,
                ["bip:WALL_BASE_OFFSET"] = ParameterKind.SetExplicitly,
                ["bip:WALL_HEIGHT_TYPE"] = ParameterKind.SetExplicitly,
                ["bip:WALL_TOP_OFFSET"] = ParameterKind.SetExplicitly,
                ["bip:WALL_USER_HEIGHT_PARAM"] = ParameterKind.SetExplicitly,

                // --- what the element IS -----------------------------------------------
                ["bip:ELEM_TYPE_PARAM"] = ParameterKind.Identity,
                ["bip:ELEM_FAMILY_PARAM"] = ParameterKind.Identity,
                ["bip:ELEM_FAMILY_AND_TYPE_PARAM"] = ParameterKind.Identity,
                ["bip:ELEM_CATEGORY_PARAM"] = ParameterKind.Identity,
                ["bip:ELEM_CATEGORY_PARAM_MT"] = ParameterKind.Identity,

                // --- resolved against the surrounding model ----------------------------
                // The carrier is thinner and sits at its layer's position, so the room an
                // opening resolves into can differ. The two names the old verifier table
                // used here - FROM_ROOM_MODULE and TO_ROOM_MODULE - are not BuiltInParameter
                // members at all, so they never matched anything; from/to room are
                // FamilyInstance properties. These three are the real ones.
                ["bip:ELEM_ROOM_ID"] = ParameterKind.ContextDerived,
                ["bip:ELEM_ROOM_NUMBER"] = ParameterKind.ContextDerived,
                ["bip:ELEM_ROOM_NAME"] = ParameterKind.ContextDerived,
            };

        /// <summary>What kind of parameter this is. Unlisted means Authored.</summary>
        public static ParameterKind KindOf(string stableKey)
        {
            if (stableKey != null && ParameterKinds.TryGetValue(stableKey, out ParameterKind kind))
                return kind;
            return ParameterKind.Authored;
        }

        /// <summary>
        /// Whether the generic parameter copier may write this one. Only the user's own
        /// data: everything else is either Revit's to compute, the type's to own, or set
        /// by a dedicated path that the copier would fight with.
        /// </summary>
        public static bool ShouldCopy(string stableKey) => KindOf(stableKey) == ParameterKind.Authored;

        /// <summary>
        /// Whether a CHANGE in this parameter needs explaining. Only two kinds may change
        /// silently: what Revit computes from geometry this operation deliberately alters,
        /// and what is resolved from surrounding context.
        ///
        /// Identity and ControlledByType are NOT excused. A door whose family or type
        /// changed is the failure this verification exists to catch, and excusing it
        /// because "we do not copy it" would confuse two different questions - the copier
        /// not writing something is no reason for it to change by itself.
        /// </summary>
        public static bool MayChangeWithoutExplanation(string stableKey)
        {
            ParameterKind kind = KindOf(stableKey);
            return kind == ParameterKind.ComputedByRevit || kind == ParameterKind.ContextDerived;
        }

        /// <summary>Why, in the reply's own words. Null for an ordinary authored one.</summary>
        public static string ParameterReason(string stableKey)
        {
            switch (KindOf(stableKey))
            {
                case ParameterKind.ComputedByRevit:
                    return "Revit computes this from geometry, and this conversion changes the geometry " +
                           "it is computed from: the carrier keeps its identity but becomes one layer thick";
                case ParameterKind.ControlledByType:
                    return "the type owns this value, and each layer wall has its own single-layer type";
                case ParameterKind.SetExplicitly:
                    return "this operation sets it through its own path, so the generic copier leaves it alone";
                case ParameterKind.Identity:
                    return "this is what the element IS; copying it would make one element claim to be another";
                case ParameterKind.ContextDerived:
                    return "resolved against the surrounding model, which a thinner carrier can change";
                default:
                    return null;
            }
        }

        /// <summary>Every key the policy names. For tests, and for the reply's own report.</summary>
        public static IReadOnlyCollection<string> ClassifiedParameterKeys => ParameterKinds.Keys;

        /// <summary>
        /// Whether two layers actually TOUCH, from their own offsets and widths.
        ///
        /// This is the check the star topology never made. Layers tile contiguously along
        /// the wall normal, so two of them touch exactly when the distance between their
        /// centres equals half of each width added together. MEASURED on a seven-layer
        /// wall whose carrier was layer 05: the carrier touched 04 and 07 and was 94.5 mm
        /// and 19.5 mm away from 01 and 02 - and 01 and 02 are precisely the two Revit
        /// recorded a permanent "joined but do not intersect" about.
        ///
        /// Note that SHARED VOLUME does not answer this. Every pair of parallel layer
        /// walls has zero shared volume, the touching ones included; measured, 4 of 4
        /// joined pairs read zero in both topologies. The gap is what separates them.
        /// </summary>
        public static bool LayersTouch(double offsetAFeet, double widthAFeet,
                                       double offsetBFeet, double widthBFeet)
        {
            double centres = Math.Abs(offsetAFeet - offsetBFeet);
            double touching = (widthAFeet + widthBFeet) / 2.0;
            return Math.Abs(centres - touching) <= ToleranceFeet;
        }

        /// <summary>
        /// The chain: each materialised layer joined to the NEXT materialised one, and to
        /// nothing else. Given layer indices in order, returns the edges as ordered pairs.
        ///
        /// Why a chain rather than a star to the carrier. MEASURED in Revit 2026, on four
        /// identical seven-layer walls with a real door in each:
        ///
        ///   star (carrier joined to every layer)  - every layer cut, 2 GAPPED joins
        ///   chain (neighbour to neighbour)        - every layer cut, 0 gapped joins
        ///   no joins at all                       - NO layer cut; each kept exactly its
        ///                                           own thickness of material
        ///
        /// So the join is what carries the carrier's opening through, the cut IS
        /// transitive along a chain, and the chain delivers the same result without ever
        /// joining two walls that do not touch. The warning goes away because the
        /// construction stops producing it, not because anybody stopped listening.
        ///
        /// The zero-width membranes are not in this list: they produce no wall, so layer
        /// 02 and layer 04 are neighbours even though 03 sits between them in the type.
        /// </summary>
        public static List<int[]> ChainEdges(IReadOnlyList<int> orderedMaterialisedLayerIndices)
        {
            var edges = new List<int[]>();
            if (orderedMaterialisedLayerIndices == null) return edges;
            for (int i = 0; i + 1 < orderedMaterialisedLayerIndices.Count; i++)
                edges.Add(new[] { orderedMaterialisedLayerIndices[i], orderedMaterialisedLayerIndices[i + 1] });
            return edges;
        }

        /// <summary>A stable, order-free key for one undirected edge.</summary>
        public static string EdgeKey(long a, long b) =>
            a <= b ? a + "-" + b : b + "-" + a;

        public static string ComposeTypeName(string sourceTypeName, string materialName, int layerNumber)
            => SanitizeNamePart(sourceTypeName) + NameSeparator +
               ResolveMaterialName(materialName) + NameSeparator +
               FormatLayerNumber(layerNumber);

        /// <summary>
        /// The deterministic variant used when the expected name already exists carrying a
        /// DIFFERENT composition. Deterministic on purpose: the same layer always produces
        /// the same variant name, so re-running does not pile up types.
        /// </summary>
        public static string ComposeVariantTypeName(string sourceTypeName, string materialName,
                                                    int layerNumber, string shortDigest)
            => ComposeTypeName(sourceTypeName, materialName, layerNumber) + NameSeparator + shortDigest;

        // ---- type identity --------------------------------------------------------

        /// <summary>
        /// What makes two single-layer wall types THE SAME type.
        ///
        /// EVERY FACT HERE IS ONE THE BUILDER ACTUALLY APPLIES AND THE MATCHER ACTUALLY
        /// RE-READS. That alignment is the property: a fingerprint richer than the check
        /// accepts types it never compared, and a check richer than the fingerprint
        /// rebuilds types it already had. Both were true of the first version of this file.
        ///
        /// Material identity is the UniqueId, never the name: two materials can share a
        /// name, and a name can be edited without the material changing. Width rides the
        /// 0.1 mm grid like every other geometric fact in this codebase.
        ///
        /// DELIBERATELY EXCLUDED, each because it cannot apply to the thing being built:
        ///
        ///   * the variable-layer flag - a single-layer compound structure has no variable
        ///     layer to nominate, so the property has no value to carry and none to check;
        ///   * deck profile and deck embedding - structural-deck properties of a floor or
        ///     roof layer. A Basic wall layer never carries them, so putting them in the
        ///     digest would distinguish types by a field that is null on both sides;
        ///   * core membership - in a single-layer structure the one layer IS the core.
        ///     It is a fact about the SOURCE assembly, not about the resulting type, and
        ///     two layers that differ only by it legitimately share a type.
        /// </summary>
        public static string LayerTypeFingerprint(WallLayerFacts layer, string baseWallKind,
                                                  string openingWrapping, string endCap)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));

            var facts = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["schema"] = WallSplitCodes.SchemaVersion,
                ["material_unique_id"] = layer.MaterialUniqueId ?? "",
                ["width_ticks"] = QuantizeFeet(layer.WidthFeet).ToString(CultureInfo.InvariantCulture),
                ["function"] = layer.Function ?? "",
                ["base_wall_kind"] = baseWallKind ?? "",
                ["opening_wrapping"] = openingWrapping ?? "",
                ["end_cap"] = endCap ?? ""
            };

            return Digest(facts);
        }

        /// <summary>
        /// The facts the fingerprint is made of, named, so a test can hold the builder and
        /// the matcher against the same list instead of against each other's memory.
        /// </summary>
        public static readonly string[] TypeIdentityFacts =
        {
            "material_unique_id", "width_ticks", "function", "base_wall_kind",
            "opening_wrapping", "end_cap"
        };

        /// <summary>Why each excluded property is excluded, so the omission is a decision.</summary>
        public static readonly IReadOnlyDictionary<string, string> TypeIdentityExclusions =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["is_variable_width"] = "a single-layer compound structure has no variable layer to nominate",
                ["deck_profile"] = "a structural-deck property of floor and roof layers; a Basic wall layer never carries one",
                ["deck_embedding"] = "same as deck_profile",
                ["is_core"] = "in a single-layer structure the one layer IS the core; it is a fact about the source assembly, not about the resulting type"
            };

        /// <summary>The first eight hex characters of a fingerprint, for the variant name.</summary>
        public static string ShortDigestOf(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
                throw new ArgumentException("A short digest needs a fingerprint.", nameof(fingerprint));
            return fingerprint.Substring(0, Math.Min(8, fingerprint.Length));
        }

        // ---- the plan -------------------------------------------------------------

        /// <summary>
        /// Decide everything about one wall that can be decided without Revit: whether it
        /// is eligible, where each layer goes, which layer the original becomes, and what
        /// each resulting type is called.
        ///
        /// The facts arrive already read off the model. What this returns is either a
        /// rejection carrying a code from the closed set, or a plan in which the number of
        /// materialised layers IS the number of walls the apply will produce - no more, and
        /// never fewer.
        /// </summary>
        public static WallSplitPlan Plan(WallAssemblyFacts facts)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            var plan = new WallSplitPlan { OriginalLocationLine = facts.LocationLine };

            if (!string.Equals(facts.WallKind, "Basic", StringComparison.Ordinal))
            {
                plan.Rejection = new WallSplitRejection(
                    string.Equals(facts.WallKind, "Stacked", StringComparison.Ordinal)
                        ? WallSplitCodes.UnsupportedStackedWall
                        : WallSplitCodes.NotBasicWall,
                    string.Equals(facts.WallKind, "Stacked", StringComparison.Ordinal)
                        ? "a stacked wall has no CompoundStructure of its own, and its root - which is where " +
                          "Revit hosts its doors and windows - cannot become a single-layer carrier while keeping " +
                          "its identity. Refused rather than converted: the previous implementation accepted these " +
                          "and deleted the root, which deleted the doors with it and reported success."
                        : "this is a '" + (facts.WallKind ?? "<unknown>") + "' wall. Only a Basic wall has the " +
                          "CompoundStructure this capability decomposes.");
                return plan;
            }

            List<WallLayerFacts> layers = facts.Layers;
            if (layers == null || layers.Count == 0)
            {
                plan.Rejection = new WallSplitRejection(WallSplitCodes.NoCompoundStructure,
                    "this wall type has no compound structure to decompose.");
                return plan;
            }

            int withVolume = layers.Count(l => l.HasVolume);
            if (withVolume <= 1)
            {
                plan.Rejection = new WallSplitRejection(WallSplitCodes.SingleLayer,
                    withVolume == 1
                        ? "this wall already has a single layer with volume - there is nothing to split."
                        : "no layer of this wall has any volume, so there is no wall to produce.");
                return plan;
            }

            if (!HasValidCore(layers, facts.CoreFirstIndex, facts.CoreLastIndex))
            {
                plan.Rejection = new WallSplitRejection(WallSplitCodes.NoValidCore,
                    "the compound structure reports core boundaries [" + facts.CoreFirstIndex + ".." +
                    facts.CoreLastIndex + "] over " + layers.Count + " layers, and that range contains no layer " +
                    "with volume. There is no layer that can carry the hosted elements. Refused rather than " +
                    "falling back to layer 0 - the outermost finish - which is where the previous implementation " +
                    "put the doors.");
                return plan;
            }

            double locationU;
            string locationError;
            if (!TryLocationU(facts.LocationLine, layers, facts.CoreFirstIndex, facts.CoreLastIndex,
                              out locationU, out locationError))
            {
                plan.Rejection = new WallSplitRejection(WallSplitCodes.UnsupportedLocationLine, locationError);
                return plan;
            }

            string carrierReason;
            int carrierIndex = SelectCoreCarrier(layers, facts.CoreFirstIndex, facts.CoreLastIndex, out carrierReason);

            plan.CoreFirstLayerIndex = facts.CoreFirstIndex;
            plan.CoreLastLayerIndex = facts.CoreLastIndex;
            plan.CoreCarrierLayerIndex = carrierIndex;
            plan.CoreCarrierSelectionReason = carrierReason;
            plan.TotalWidthFeet = TotalWidth(layers);
            plan.LocationUFeet = locationU;

            double coreCenterU = (CoreExteriorU(layers, facts.CoreFirstIndex) +
                                  CoreInteriorU(layers, facts.CoreLastIndex)) / 2.0;
            plan.OriginalCoreCenterOffsetFeet = OffsetForLayer(locationU, coreCenterU);

            for (int i = 0; i < layers.Count; i++)
            {
                WallLayerFacts layer = layers[i];
                bool isCore = i >= facts.CoreFirstIndex && i <= facts.CoreLastIndex;
                bool isCarrier = i == carrierIndex;
                double centerU = LayerCenterU(layers, i);

                string material = ResolveMaterialName(layer.MaterialName);
                string fingerprint = LayerTypeFingerprint(layer, facts.WallKind,
                                                           facts.OpeningWrapping, facts.EndCap);
                string shortDigest = ShortDigestOf(fingerprint);
                int number = i + 1;

                var entry = new WallLayerPlan
                {
                    LayerIndex = i,
                    LayerNumber = number,
                    LayerNumberText = FormatLayerNumber(number),
                    WidthFeet = layer.WidthFeet,
                    CenterUFeet = centerU,
                    ExpectedOffsetFeet = OffsetForLayer(locationU, centerU),
                    IsCore = isCore,
                    IsCoreCarrier = isCarrier,
                    Materialised = layer.HasVolume,
                    NotMaterialisedReason = layer.HasVolume ? null : "zero_width_membrane",
                    Role = RoleFor(layer, isCore, isCarrier),
                    MaterialName = material,
                    SourceWallTypeName = facts.WallTypeName,
                    ExpectedTypeName = ComposeTypeName(facts.WallTypeName, layer.MaterialName, number),
                    VariantTypeName = ComposeVariantTypeName(facts.WallTypeName, layer.MaterialName, number, shortDigest),
                    TypeFingerprint = fingerprint,
                    ShortDigest = shortDigest
                };
                plan.Layers.Add(entry);
            }

            return plan;
        }

        // ---- the plan fingerprint -------------------------------------------------

        /// <summary>
        /// What a confirmation token is bound to, for ONE wall.
        ///
        /// The first version of this bound the wall's own state plus THE LIST OF UNIQUE IDS
        /// of its dependencies. That detects a door appearing or disappearing and nothing
        /// else: a door MOVED, re-typed, re-phased, re-hosted or re-parameterised between
        /// the dry run and the apply left the fingerprint identical, and the apply went
        /// ahead against a model that was no longer the one somebody approved.
        ///
        /// So the dependencies now arrive as FINGERPRINTS OF THEIR WHOLE STATE, the joins
        /// arrive as a fingerprint of theirs, and the wall's own constraints, phases,
        /// workset, pinning and room-bounding arrive as a third. Anything a person could
        /// change about this wall or about what hangs off it moves this number.
        /// </summary>
        public static string WallPlanFingerprint(
            string documentKey, string wallUniqueId, long wallElementId,
            WallAssemblyFacts facts, WallSplitPlan plan,
            bool flipped, IEnumerable<double> curveFactsFeet,
            IEnumerable<string> dependencyFingerprints,
            string joinFingerprint,
            string wallStateFingerprint,
            string coreCarrierPolicy)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var book = new FactBook()
                .Add("document", documentKey)
                .Add("wall_unique_id", wallUniqueId)
                .Add("wall_element_id", wallElementId)
                .Add("wall_type_unique_id", facts.WallTypeUniqueId)
                .Add("wall_type_name", facts.WallTypeName)
                .Add("wall_kind", facts.WallKind)
                .Add("location_line", facts.LocationLine)
                .Add("core_first", facts.CoreFirstIndex)
                .Add("core_last", facts.CoreLastIndex)
                .Add("opening_wrapping", facts.OpeningWrapping)
                .Add("end_cap", facts.EndCap)
                .Add("flipped", flipped)
                .Add("core_carrier_policy", coreCarrierPolicy)
                .Add("carrier_layer_index", plan.CoreCarrierLayerIndex)
                .Add("carrier_reason", plan.CoreCarrierSelectionReason)
                .AddDigest("joins", joinFingerprint)
                .AddDigest("wall_state", wallStateFingerprint);

            var compound = new List<string>();
            foreach (WallLayerFacts layer in facts.Layers)
                compound.Add(layer.Index + ":" + QuantizeFeet(layer.WidthFeet) + ":" +
                             (layer.MaterialUniqueId ?? "") + ":" + (layer.Function ?? "") + ":" +
                             (layer.IsVariableWidth ? "1" : "0"));
            book.AddList("compound_structure", compound, ordered: true);

            var layerPlans = new List<string>();
            foreach (WallLayerPlan p in plan.Layers)
                layerPlans.Add(p.LayerNumberText + ":" + QuantizeFeet(p.ExpectedOffsetFeet) + ":" +
                               (p.Materialised ? "1" : "0") + ":" + p.TypeFingerprint + ":" +
                               (p.ExpectedTypeName ?? "") + ":" + (p.Role ?? ""));
            book.AddList("layer_plan", layerPlans, ordered: true);

            var curve = new List<string>();
            if (curveFactsFeet != null)
                foreach (double value in curveFactsFeet)
                {
                    if (double.IsNaN(value) || double.IsInfinity(value))
                        throw new ArgumentException(
                            "A curve fact is not a finite number; a fingerprint over it would be an identity for " +
                            "geometry nobody measured.", nameof(curveFactsFeet));
                    curve.Add(QuantizeFeet(value).ToString(CultureInfo.InvariantCulture));
                }
            book.AddList("curve", curve, ordered: true);

            // Unordered: which things hang off the wall is a SET. Their individual state is
            // already inside each fingerprint, so a collector that returns them in a
            // different order must not refuse an honest apply.
            book.AddList("dependencies", dependencyFingerprints, ordered: false);

            return book.Digest();
        }

        /// <summary>
        /// Everything the token binds, named, so the report can list it and a test can pin
        /// it. The director asked for this list explicitly; it is generated from the same
        /// place the fingerprint is built rather than written out twice.
        /// </summary>
        public static readonly string[] PlanFingerprintCovers =
        {
            "document", "wall_unique_id", "wall_element_id", "wall_type_unique_id", "wall_type_name",
            "wall_kind", "location_line", "core_first", "core_last", "opening_wrapping", "end_cap",
            "flipped", "core_carrier_policy", "carrier_layer_index", "carrier_reason",
            "compound_structure", "layer_plan", "curve", "dependencies", "joins", "wall_state"
        };

        // ---- shared digest --------------------------------------------------------

        private static string Digest(SortedDictionary<string, string> facts)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in facts)
                sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }
    }
}
