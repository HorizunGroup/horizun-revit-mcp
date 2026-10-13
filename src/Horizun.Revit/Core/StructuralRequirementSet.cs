// -----------------------------------------------------------------------------
// Horizun Revit MCP - the neutral artefact that says what reinforcement is wanted.
// Original Horizun code. No Revit types.
//
// THE POINT OF THIS FILE IS WHAT IT REFUSES TO DECIDE.
//
// A bridge that models reinforcement is one small step from designing it, and
// that step is not ours to take. Diameter, spacing, cover, grade, hook angle,
// lap length, ratio: every one of those is a number somebody is answerable for.
// They arrive HERE, declared, from a person or from a code that person chose.
// Nothing in this bridge invents one, and nothing here carries a default that
// would quietly become one.
//
// So this parser is strict in a specific direction. A missing value is never
// filled in - it is a refusal that names the field. A value that this layout
// would not use is a refusal too, because somebody wrote it meaning something.
// The only defaults that exist are ones that cannot change a bar: whether an
// end bar is included, which is Revit's own default and is echoed back so it
// shows in the plan.
//
// A malformed set is refused ENTIRELY. Half a requirement set is worse than
// none: it builds some of somebody's reinforcement and leaves them believing
// they asked for what arrived.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class StructuralBarTypeRef
    {
        public string Id;
        public string TypeName;
        public double? NominalDiameterMm;
    }

    public sealed class StructuralHookTypeRef
    {
        public string Id;
        public string TypeName;
        /// <summary>Null means "no hook". A hook type of null and a missing hook block are the same thing.</summary>
        public bool None;
        /// <summary>
        /// The straight extension of THIS hook on each declared bar type, in mm, by bar_types id
        /// (lengths_mm). Revit keeps it on the BAR TYPE, per hook type, so declaring one changes a
        /// type: every bar of that type with this hook. Empty when the set leaves it to the model.
        /// </summary>
        public Dictionary<string, double> LengthsMm = new Dictionary<string, double>(StringComparer.Ordinal);
    }

    /// <summary>
    /// One declared (bar type, hook type) hook length, with the names it resolves by. The apply
    /// writes it to the bar type; the plan and the audit compare the model's against it.
    /// </summary>
    public sealed class DeclaredHookLength
    {
        public string HookTypeId;
        public string HookTypeName;
        public string BarTypeId;
        public string BarTypeName;
        public double LengthMm;
        /// <summary>Where it was declared, as a path a refusal or a finding can name.</summary>
        public string Path { get { return StructuralRequirementSet.HookLengthPath(HookTypeId, BarTypeId); } }
    }

    public sealed class StructuralHostSelector
    {
        public string Category;
        public string TypeName;
        public List<long> ElementIds = new List<long>();
        /// <summary>The element's Mark (ALL_MODEL_MARK), exactly - how a schedule names a member.</summary>
        public string Mark;
        /// <summary>The name of the level the element is on (its reference or base level), exactly.</summary>
        public string Level;
        /// <summary>
        /// PROVENANCE, never matched on: the selector this rule had before a tool narrowed it to
        /// element_ids (RequirementSetChunks.Split), so an audit knows where its ids came from.
        /// </summary>
        public Newtonsoft.Json.Linq.JObject SourceSelector;
        public bool Any { get { return Category != null || TypeName != null || ElementIds.Count > 0 || Mark != null || Level != null; } }
    }

    public sealed class StructuralCoverRule
    {
        public const string FaceCommon = "common", FaceNormal = "normal";
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        /// <summary>
        /// common, or normal: the exposed faces whose OUTWARD normal is <see cref="FaceNormalMm"/>
        /// (acero 3.0, L7). A face is never named by a word like 'top' - a slab has two faces and
        /// a sloped one is neither - and never by a Revit reference, which does not survive a re-issue.
        /// </summary>
        public string Face;
        /// <summary>The declared outward normal, unit length. Null for the common cover.</summary>
        public double[] FaceNormalMm;
        public string CoverTypeName;
        public double? DistanceMm;
        public bool Required = true;
    }

    /// <summary>
    /// Which exposed faces a per-face cover rule means (acero 3.0, L7): every one whose outward
    /// normal is the declared vector within the angle tolerance. A face whose normal could not
    /// be read never matches.
    /// </summary>
    public static class CoverFaceRules
    {
        public const string CodeFaceNotFound = "cover_face_not_found";

        public static bool Matches(double[] faceNormal, double[] declared, double angleToleranceDegrees)
        {
            double[] a = RebarContainment.Unit(faceNormal), b = RebarContainment.Unit(declared);
            if (a == null || b == null) return false;
            double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
            return dot >= Math.Cos(Math.Max(0, angleToleranceDegrees) * Math.PI / 180.0) - 1e-12;
        }

        public static string Describe(double[] n) =>
            n == null ? "common" : "the faces whose outward normal is [" +
                string.Join(", ", n.Select(v => Math.Round(v, 4).ToString(CultureInfo.InvariantCulture))) + "]";
    }

    public sealed class StructuralTermination
    {
        /// <summary>The id of a hook type declared in hook_types, or null for no hook.</summary>
        public string HookTypeId;
        public string Orientation = RebarApiVocabulary.OrientationLeft;
    }

    /// <summary>
    /// How far a bar must run inside one of the elements it was declared into (acero 3.0, L1):
    /// a dowel's embedment in the footing, a beam bar's anchorage in the column. Declared - the
    /// length is the caller's, from whatever code the caller chose - and MEASURED along the bar's
    /// centreline inside that element's solid, by the plan, after the commit and by the audit.
    /// </summary>
    public sealed class StructuralAnchorage
    {
        public const string EndStart = "start", EndEnd = "end";
        public long ElementId;
        public double MinLengthMm;
        /// <summary>Section layers only: which end of the layer is anchored, so its piece carries the check.</summary>
        public string End;
    }

    /// <summary>
    /// How a stirrup zone rule learns the cover Revit will clamp its array to.
    ///
    /// MEASURED (ADR-003 item 7): Revit shifts a hosted array in by the host's
    /// cover plus the bar's radius at each end, whatever the declaration says. A
    /// zone rule that does not know the cover declares stations Revit then moves,
    /// and the apply correctly reports a model that does not carry what was asked
    /// for. This block is how the zone planner is TOLD the cover - read from the
    /// host at resolve time, or declared - so that what it predicts is what Revit
    /// draws. Absent, the zones are laid out in model coordinates exactly as
    /// declared, which is the behaviour every existing set was written against.
    /// </summary>
    public sealed class StructuralStirrupZoneCover
    {
        public const string SourceHost = "host";
        public const string SourceDeclared = "declared";
        public static readonly string[] Sources = { SourceHost, SourceDeclared };

        public string Source;
        /// <summary>Required when Source is declared; refused beside host, because two statements of one number disagree.</summary>
        public double? DistanceMm;
    }

    /// <summary>
    /// What a mat does about the openings in its host. Every word here is a
    /// design decision, which is why none of them is defaulted: a mat rule over a
    /// host with a hole and no block is refused BY NAME as soon as a bar would
    /// cross the hole, naming the openings found and the three policies.
    /// </summary>
    public sealed class StructuralMatOpenings
    {
        public const string PolicyOmit = "omit";
        public const string PolicyTrim = "trim";
        public const string PolicyIgnore = "ignore";
        public static readonly string[] Policies = { PolicyOmit, PolicyTrim, PolicyIgnore };

        public string Policy;
        /// <summary>An opening whose largest dimension is below this is ignored. Declared, never defaulted.</summary>
        public double MinimumSizeMm;
        /// <summary>trim only: how far short of the opening a trimmed bar stops. Refused beside omit or ignore.</summary>
        public double? ClearanceMm;
    }

    /// <summary>
    /// Stirrups declared the way a drawing declares them: a profile, a direction,
    /// and zones along it. This is NOT a fourth kind of thing in the model - it
    /// expands into ordinary reinforcement rules, one per zone, so containment,
    /// the point-by-point audit, provenance and idempotency need to know nothing
    /// about it.
    /// </summary>
    public sealed class StructuralStirrupZoneRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        public string BarTypeId;
        public string ShapeName;
        public string Style = StructuralStyle.StirrupTie;
        /// <summary>The stirrup outline at the START of the span, in model coordinates.</summary>
        public List<double[]> ProfileMm = new List<double[]>();
        public bool Closed = true;
        /// <summary>The direction the zones run in. Same role as a rebar rule's normal.</summary>
        public double[] AlongMm;
        /// <summary>How long the run is. Null means "measure the host", which only the resolver can do.</summary>
        public double? SpanMm;
        public bool SpanFromHost;
        public double StartOffsetMm;
        public double EndOffsetMm;
        public bool Symmetric;
        public double? MinimumClearBetweenZonesMm;
        /// <summary>Null means "no cover block": model coordinates as declared. See StructuralStirrupZoneCover.</summary>
        public StructuralStirrupZoneCover Cover;
        public List<StirrupZoneRequest> Zones = new List<StirrupZoneRequest>();
        public StructuralTermination Start = new StructuralTermination();
        public StructuralTermination End = new StructuralTermination();
        public string Mark;
        public bool Required = true;
        public bool AllowNewShape;
        /// <summary>Arcs in the profile (L2), as on a reinforcement rule: a circular hoop is two.</summary>
        public Dictionary<int, double[]> ProfileArcThrough = new Dictionary<int, double[]>();
        /// <summary>A section rule's stirrups placed from a rebar shape (L7): the declaration. Null otherwise.</summary>
        public StructuralShapePlacement ShapePlacement;
        /// <summary>Set ONLY by a section expansion (overlapping_bar): the declared rule, the cage and its part in it. Never parsed.</summary>
        public string OriginRuleId;
        public string CageId;
        public string CageRole;
        /// <summary>
        /// Where the placed hoop or spiral goes in ONE host, at the start of the span: set by the
        /// circular section expansion, moved to each station by the zone expansion. Null otherwise.
        /// </summary>
        public PlacedShapeGeometry PlacementTemplate;
        public JObject Raw;

        /// <summary>
        /// This rule for one host, with the profile, direction and run a section rule
        /// measured off that host. The zones, terminations and cover are shared, read-only.
        /// </summary>
        public StructuralStirrupZoneRule CloneFor(string id, StructuralHostSelector host, List<double[]> profileMm,
                                                  double[] alongMm, double spanMm)
        {
            var c = (StructuralStirrupZoneRule)MemberwiseClone();
            c.Id = id; c.Host = host; c.ProfileMm = profileMm; c.AlongMm = alongMm;
            c.SpanMm = spanMm; c.SpanFromHost = false; c.Closed = true;
            c.ProfileArcThrough = new Dictionary<int, double[]>();
            c.PlacementTemplate = null;
            return c;
        }

        /// <summary>The same, open or closed: a crosstie is a two-point open profile (L2).</summary>
        public StructuralStirrupZoneRule CloneFor(string id, StructuralHostSelector host, List<double[]> profileMm,
                                                  double[] alongMm, double spanMm, bool closed)
        {
            StructuralStirrupZoneRule c = CloneFor(id, host, profileMm, alongMm, spanMm);
            c.Closed = closed;
            return c;
        }
    }

    /// <summary>
    /// A slab or wall mat: "top X at 150, top Y at 200". Expands into ordinary
    /// reinforcement rules, one per component, with the centrelines derived from
    /// the host's own boundary rather than typed out by hand.
    /// </summary>
    public sealed class StructuralMatRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        /// <summary>Points OUT of the face the mat sits under. Declared, never inferred.</summary>
        public double[] FaceNormalMm;
        public List<MatComponentRequest> Components = new List<MatComponentRequest>();
        /// <summary>Null means "no openings block": a bar that would cross a hole refuses the rule by name.</summary>
        public StructuralMatOpenings Openings;
        /// <summary>
        /// The OTHER elements the face continues into (acero 3.0, a face a mat follows): a roof of two
        /// floors meeting at a break. Ids only; read only by components that follow the face, whose
        /// face is then the outermost face of the host and these together. Empty: the host alone.
        /// </summary>
        public List<long> Into = new List<long>();
        public string Mark;
        public bool Required = true;
        public JObject Raw;
    }

    public sealed class StructuralRebarRule
    {
        /// <summary>
        /// The same rule, its centreline (and arc points) replaced by a crossing_order's displacement (etapa 1c). The
        /// id, host, layout and terminations are the declared ones: the audit finds the set by the same rule id.
        /// </summary>
        public StructuralRebarRule CloneMoved(List<double[]> curvesMm, Dictionary<int, double[]> arcThrough)
        {
            var c = (StructuralRebarRule)MemberwiseClone();
            c.CurvesMm = curvesMm;
            c.ArcThrough = arcThrough ?? new Dictionary<int, double[]>();
            return c;
        }

        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        public string BarTypeId;
        public string ShapeName;
        /// <summary>standard or stirrup_tie. Declared, never inferred from the curves.</summary>
        public string Style = StructuralStyle.Standard;
        /// <summary>Explicit centreline in millimetres, model coordinates: [[x,y,z], ...].</summary>
        public List<double[]> CurvesMm = new List<double[]>();
        public bool Closed;
        public double[] NormalMm;
        public RebarLayoutRequest Layout = new RebarLayoutRequest();
        public StructuralTermination Start = new StructuralTermination();
        public StructuralTermination End = new StructuralTermination();
        public string Mark;
        public bool Required = true;
        /// <summary>
        /// May Revit CREATE a new rebar shape family to hold this bar? False by
        /// default and never inferred: a shape appears in the project browser, in
        /// schedules and in everybody else's model, and adding one silently is a
        /// change nobody asked for.
        /// </summary>
        public bool AllowNewShape;
        /// <summary>Which side of the bar the set marches to. Revit's own default is true.</summary>
        public bool BarsOnNormalSide = true;
        /// <summary>
        /// Set ONLY by a cover-aware stirrup zone expansion: what the planner
        /// predicted from the cover, so the apply can hold the model to it. Never
        /// parsed from a set.
        /// </summary>
        public StirrupCoverPrediction CoverPrediction;
        /// <summary>
        /// Set ONLY by a mat expansion over a host with openings: the openings the
        /// bars were planned around, so the apply can check the drawn bars against
        /// the same regions. Never parsed from a set.
        /// </summary>
        public MatOpeningContext OpeningContext;
        /// <summary>
        /// Set ONLY by a section layer spliced with laps (F2.1): the overlap this piece must
        /// make with the next one, in mm, and that piece's rule id, so the audit can measure
        /// the lap the model has. Never parsed from a set.
        /// </summary>
        public double? LapNextMm;
        public string LapNextRuleId;
        /// <summary>How far across the next piece sits, centre to centre (D4: one bar diameter, in contact).</summary>
        public double? LapNextAcrossMm;
        /// <summary>
        /// The OTHER elements these bars run into (acero 3.0, L1). Containment is measured
        /// against the union of the host and these, and a face two of them share is concrete,
        /// not air. Empty: the bars stay in their host, as before.
        /// </summary>
        public List<long> Into = new List<long>();
        /// <summary>The anchored length these bars need in elements named in Into.</summary>
        public List<StructuralAnchorage> Anchorage = new List<StructuralAnchorage>();
        /// <summary>
        /// Set ONLY by a section or wall boundary expansion: the smallest cover the section declares for
        /// its host, in mm. A bar declared into other elements is held to the smallest cover any of them
        /// declares, and the host's is declared HERE, not in cover_rules. Never parsed.
        /// </summary>
        public double? SectionCoverMm;
        /// <summary>
        /// Set ONLY by a mat whose component follows its face: what the bars were laid on (the facets,
        /// the polyline of the run's first bar, the stations it carries), so the plan can say it. Never parsed.
        /// </summary>
        public MatFollowContext FollowFace;
        /// <summary>
        /// Set ONLY by a mat expansion: whether this rule is in a narrowed run was decided by the mat's own
        /// host. A bar a followed face hosts in an into element must not be dropped for not being the mat's host.
        /// </summary>
        public bool ScopedByMat;
        /// <summary>
        /// Arcs (L2): segment index -> the point the arc passes through. Segment i joins vertex i to
        /// vertex i+1 (closed: the last segment joins the last vertex to the first). Absent: a line.
        /// </summary>
        public Dictionary<int, double[]> ArcThrough = new Dictionary<int, double[]>();
        /// <summary>
        /// Set ONLY by a zone expansion of placed stirrups (L7): this bar is a hoop or a spiral
        /// placed from a rebar shape, not drawn from its curves. Never parsed from a set.
        /// </summary>
        public PlacedShapeGeometry Placement;
        /// <summary>
        /// Set ONLY by a section layer spliced with a coupler (L7): the coupler type joining the
        /// END of this piece to the START of the next, and that piece's rule id. Never parsed.
        /// </summary>
        public string CouplerNextTypeName;
        public string CouplerNextRuleId;
        /// <summary>Set ONLY by a section layer spliced with a coupler (L7): this piece's START is coupled.</summary>
        public bool CoupledAtStart;
        /// <summary>The end of THIS piece the coupler to the next one sits at: 1, or 0 when the piece is declared from the station back (L7).</summary>
        public int CouplerNextEnd = 1;
        /// <summary>The rule's own hold_ends, or null to follow the set's (stage 1b).</summary>
        public bool? HoldEnds;
        /// <summary>
        /// Set ONLY on a section row that touches the stirrup's hooks' corner (acero 3.0, L10): how far, in
        /// mm, Revit may seat it from the plan - half a stirrup. Which leg the hooks' second pass lies along
        /// is Revit's tie-break (measured, 2024 and 2026), and the corner bar sits in that pass's bend.
        /// </summary>
        public double? HookCornerSeatMm;
        /// <summary>
        /// Set ONLY by an expansion (overlapping_bar): the id of the rule the SET declared - the section,
        /// wall boundary, mat or stirrup zone rule this one came from. Null: this rule was declared as itself.
        /// </summary>
        public string OriginRuleId;
        /// <summary>
        /// Set ONLY by an expansion: the one layout this rule is part of - the zones of one stirrup zone rule,
        /// the pieces of one mat component around openings. Bars of one layout are spaced by it and are not
        /// compared with each other. Null: the rule is its own layout.
        /// </summary>
        public string LayoutGroupId;
        /// <summary>
        /// Set ONLY by a section or wall boundary expansion: the cage (section rule x host) and this rule's
        /// part in it - longitudinal, stirrup or tie. A cage's stirrups and ties are laid against and around
        /// its own bars by design. Never parsed.
        /// </summary>
        public string CageId;
        public string CageRole;
        public JObject Raw;
    }

    /// <summary>One layer of an area reinforcement system (acero 3.0, L4b): a bar type and a spacing.</summary>
    public sealed class StructuralSystemLayer
    {
        /// <summary>top_major ... bottom_minor on a floor; exterior_major ... interior_minor on a wall.</summary>
        public string Name;
        public string BarTypeId;
        public double SpacingMm;
    }

    /// <summary>
    /// Area reinforcement (L4b): a closed outline on ONE face of ONE floor or wall, a major
    /// direction in that face, an area reinforcement type and the layers that are active.
    /// Revit lays the bars; every number they are laid by is declared here.
    /// </summary>
    public sealed class StructuralAreaRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        public List<double[]> BoundaryMm = new List<double[]>();
        public double[] MajorDirectionMm;
        public string AreaTypeName;
        /// <summary>The declared layers, in Revit's order. Every layer not here is built INACTIVE.</summary>
        public List<StructuralSystemLayer> Layers = new List<StructuralSystemLayer>();
        /// <summary>slab (top/bottom words) or wall (exterior/interior words), read off the layer names.</summary>
        public string LayerFamily;
        public string Mark;
        public bool Required = true;
        public JObject Raw;
    }

    /// <summary>Path reinforcement (L4b): an open polyline on one face, a side, and the primary bars.</summary>
    public sealed class StructuralPathRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        public List<double[]> PathMm = new List<double[]>();
        public string PathTypeName;
        /// <summary>Which side of the path the bars go. Declared: the geometry does not say.</summary>
        public bool Flip;
        public string PrimaryBarTypeId;
        public double PrimarySpacingMm;
        public double PrimaryLengthMm;
        public string Mark;
        public bool Required = true;
        public JObject Raw;
    }

    /// <summary>Fabric (L4b): a fabric area over a closed outline on one face, with its sheet type.</summary>
    public sealed class StructuralFabricRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        public List<double[]> BoundaryMm = new List<double[]>();
        public double[] MajorDirectionMm;
        /// <summary>Where the major direction line passes: the sheets are laid out from it. Declared.</summary>
        public double[] MajorDirectionOriginMm;
        public string FabricAreaTypeName;
        public string FabricSheetTypeName;
        /// <summary>Null means not declared: Revit's own value stands, and is reported, not compared.</summary>
        public double? CoverOffsetMm;
        public string Mark;
        public bool Required = true;
        public JObject Raw;
    }

    public static class StructuralStyle
    {
        public const string Standard = "standard";
        public const string StirrupTie = "stirrup_tie";
        public static readonly string[] All = { Standard, StirrupTie };
        public static bool IsKnown(string s)
        {
            return s == Standard || s == StirrupTie;
        }
    }

    /// <summary>
    /// The orientation words, repeated here so Core can validate them without
    /// referencing the Revit-touching shim that maps them.
    /// </summary>
    public static class RebarApiVocabulary
    {
        public const string OrientationLeft = "left";
        public const string OrientationRight = "right";
        public static readonly string[] All = { OrientationLeft, OrientationRight };
        public static bool IsKnown(string s)
        {
            return s == OrientationLeft || s == OrientationRight;
        }
    }

    public sealed class StructuralTolerances
    {
        public double LengthMm = 2.0;
        public double SpacingMm = 2.0;
        public double CoverMm = 1.0;
        public double AngleDegrees = 1.0;
    }

    /// <summary>One declared pair of rules (by their DECLARED ids) and the clear distance their bars keep alongside.</summary>
    public sealed class StructuralClearancePair
    {
        public string RuleA, RuleB;
        public double MinClearMm;
    }

    /// <summary>
    /// clearances (acero 3.0, overlapping_bar): how far apart the SURFACES of bars of different rules must stay.
    /// Declared, never compiled in - the bridge is neutral. between_rules_mm holds bars that run alongside each
    /// other (within RebarOverlap.AlongsideDegrees); crossing_mm holds bars that cross, 0 unless declared, because
    /// crossing bars are tied in contact. A pair overrides between_rules_mm for two declared rules. Nothing
    /// declared: only bars that pass THROUGH each other are reported, which Revit would move.
    /// </summary>
    public sealed class StructuralClearances
    {
        public double? BetweenRulesMm;
        public double CrossingMm;
        public bool CrossingDeclared;
        public List<StructuralClearancePair> Pairs = new List<StructuralClearancePair>();
        public bool Declared => BetweenRulesMm.HasValue || CrossingDeclared || Pairs.Count > 0;

        /// <summary>The clear distance two declared rules' bars keep alongside: the last pair naming them, else between_rules_mm, else 0.</summary>
        public double AlongsideMm(string declaredA, string declaredB)
        {
            for (int i = Pairs.Count - 1; i >= 0; i--)
            {
                StructuralClearancePair p = Pairs[i];
                if ((p.RuleA == declaredA && p.RuleB == declaredB) || (p.RuleA == declaredB && p.RuleB == declaredA)) return p.MinClearMm;
            }
            return BetweenRulesMm ?? 0;
        }

        /// <summary>The largest clear distance anything here can ask for: how far the broad phase must look.</summary>
        public double LargestMm()
        {
            double m = Math.Max(BetweenRulesMm ?? 0, CrossingMm);
            foreach (StructuralClearancePair p in Pairs) m = Math.Max(m, p.MinClearMm);
            return m;
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["between_rules_mm"] = BetweenRulesMm.HasValue ? (JToken)BetweenRulesMm.Value : JValue.CreateNull(),
                ["crossing_mm"] = CrossingMm,
                ["crossing_declared"] = CrossingDeclared,
                ["pairs"] = new JArray(Pairs.Select(p => (object)new JObject
                {
                    ["rules"] = new JArray(p.RuleA, p.RuleB),
                    ["min_clear_mm"] = p.MinClearMm
                }).ToArray())
            };
        }
    }

    public sealed class StructuralRequirementSet
    {
        public const string SchemaName = "horizun.structural-requirements/1";

        public string Id;
        public string Version;
        public string Title;
        public StructuralTolerances Tolerances = new StructuralTolerances();
        /// <summary>
        /// hold_ends (stage 1b): pin every bar's ends where the plan put them, undoing Revit pulling them onto
        /// the cover of an element they run into. OFF unless asked; a reinforcement rule may override it.
        /// </summary>
        public bool HoldEnds;
        /// <summary>Whether a rule's bars are held: its own hold_ends, else the set's.</summary>
        public bool HoldsEnds(StructuralRebarRule r) => r?.HoldEnds ?? HoldEnds;
        public StructuralClearances Clearances = new StructuralClearances();
        /// <summary>
        /// crossing_order (etapa 1c): which of two crossing sets moves, which way and how far. Null when the set
        /// declares none - then bars that pass through each other are refused, as before.
        /// </summary>
        public StructuralCrossingOrder CrossingOrder;
        public Dictionary<string, StructuralBarTypeRef> BarTypes =
            new Dictionary<string, StructuralBarTypeRef>(StringComparer.Ordinal);
        public Dictionary<string, StructuralHookTypeRef> HookTypes =
            new Dictionary<string, StructuralHookTypeRef>(StringComparer.Ordinal);
        public List<StructuralCoverRule> CoverRules = new List<StructuralCoverRule>();
        public List<StructuralRebarRule> RebarRules = new List<StructuralRebarRule>();
        public List<StructuralStirrupZoneRule> StirrupZoneRules = new List<StructuralStirrupZoneRule>();
        public List<StructuralMatRule> MatRules = new List<StructuralMatRule>();
        /// <summary>Section-relative recipes (F1.1). Expanded per host by the resolver; see SectionRules.</summary>
        public List<StructuralSectionRule> SectionRules = new List<StructuralSectionRule>();
        /// <summary>Boundary elements of structural walls (acero 3.0, L5): a section rule over a wall's end region; see WallBoundaryRules.</summary>
        public List<StructuralWallBoundaryRule> WallBoundaryRules = new List<StructuralWallBoundaryRule>();
        /// <summary>Area reinforcement, path reinforcement and fabric (acero 3.0, L4b).</summary>
        public List<StructuralAreaRule> AreaRules = new List<StructuralAreaRule>();
        public List<StructuralPathRule> PathRules = new List<StructuralPathRule>();
        public List<StructuralFabricRule> FabricRules = new List<StructuralFabricRule>();

        public string Error;
        public string Code;
        public bool Ok { get { return Error == null; } }

        // Refusal codes, closed.
        public const string CodeSchema = "schema_not_recognised";
        public const string CodeMissing = "required_field_missing";
        public const string CodeUnknownValue = "value_not_in_vocabulary";
        public const string CodeDuplicateId = "duplicate_id";
        public const string CodeUnresolvedReference = "reference_not_declared";
        public const string CodeGeometry = "geometry_not_usable";
        public const string CodeLayout = "layout_not_usable";
        public const string CodeNoRules = "no_rules";
        public const string CodeUnits = "units_not_millimetres";
        public const string CodeNotFinite = "value_is_not_a_finite_number";
        public const string CodeNotANumber = "value_is_not_a_number";
        public const string CodeUnknownKey = "unknown_key";

        /// <summary>
        /// The exact field the refusal is about, as a path - "stirrup_zone_rules['B1'].zones[0].lenght_mm" -
        /// so a caller can locate it by machine rather than by reading the message. Null when the
        /// refusal is about the set as a whole.
        /// </summary>
        public string Path;

        /// <summary>
        /// For an unknown key: every key that IS admitted at that place, in ordinal order, so the
        /// caller can pick the right one. Empty for every other refusal.
        /// </summary>
        public List<string> Allowed = new List<string>();

        /// <summary>
        /// For an unknown key: the admitted keys that look like what was typed, closest first.
        /// A misspelling lands here; an invented key does not.
        /// </summary>
        public List<string> DidYouMean = new List<string>();

        // EVERY OBJECT SHAPE IN THIS SCHEMA NAMES ITS KEYS, and a key outside the
        // list refuses the whole set. A misspelt key used to be read as an absent
        // one: `lenght_mm` on a zone silently became "the rest of the span",
        // `start_offset` silently became 0, `matt_rules` silently became a set with
        // no mats - and the plan that came back looked complete. The three sibling
        // parsers in this folder refuse unknown keys; this one now does too.
        public static readonly HashSet<string> RootKeys = Keys(
            "schema", "units", "requirement_set", "tolerances", "clearances", "bar_types", "hook_types",
            "cover_rules", "reinforcement_rules", "stirrup_zone_rules", "mat_rules", "section_rules",
            "wall_boundary_rules", "area_rules", "path_rules", "fabric_rules", "hold_ends", "crossing_order");
        public static readonly HashSet<string> AreaRuleKeys = Keys(
            "id", "host", "boundary_mm", "major_direction", "area_type_name", "layers", "mark", "required");
        public static readonly HashSet<string> AreaLayersKeys = Keys(
            "top_major", "top_minor", "bottom_major", "bottom_minor",
            "exterior_major", "exterior_minor", "interior_major", "interior_minor");
        public static readonly HashSet<string> SystemLayerKeys = Keys("bar_type", "spacing_mm");
        public static readonly HashSet<string> PathRuleKeys = Keys(
            "id", "host", "path_mm", "path_type_name", "flip", "primary", "mark", "required");
        public static readonly HashSet<string> PathPrimaryKeys = Keys("bar_type", "spacing_mm", "length_mm");
        public static readonly HashSet<string> FabricRuleKeys = Keys(
            "id", "host", "boundary_mm", "major_direction", "major_direction_origin_mm", "fabric_area_type_name",
            "fabric_sheet_type_name", "cover_offset_mm", "mark", "required");
        public static readonly HashSet<string> HeaderKeys = Keys("id", "version", "title");
        public static readonly HashSet<string> ToleranceKeys = Keys("length_mm", "spacing_mm", "cover_mm", "angle_degrees");
        public static readonly HashSet<string> ClearanceKeys = Keys("between_rules_mm", "crossing_mm", "pairs");
        public static readonly HashSet<string> ClearancePairKeys = Keys("rules", "min_clear_mm");
        public static readonly HashSet<string> CrossingOrderKeys = Keys("offset", "orders");
        public static readonly HashSet<string> CrossingEntryKeys = Keys("id", "stays", "moves", "move", "direction", "legs", "offset");
        public static readonly HashSet<string> CrossingOffsetKeys = Keys("mm");
        public static readonly HashSet<string> BarTypeKeys = Keys("id", "type_name", "nominal_diameter_mm");
        public static readonly HashSet<string> HookTypeKeys = Keys("id", "type_name", "none", "lengths_mm");
        public static readonly HashSet<string> CoverRuleKeys = Keys(
            "id", "host", "face", "cover_type_name", "distance_mm", "required");
        public static readonly HashSet<string> HostKeys = Keys("category", "type_name", "element_ids", "mark", "level", "source_selector");
        public static readonly HashSet<string> RebarRuleKeys = Keys(
            "id", "host", "bar_type", "shape", "allow_new_shape", "style", "curve_mm", "closed", "normal",
            "layout", "start", "end", "mark", "required", "into", "anchorage", "hold_ends");
        public static readonly HashSet<string> AnchorageKeys = Keys("element_id", "min_length_mm");
        public static readonly HashSet<string> LayerAnchorageKeys = Keys("element_id", "min_length_mm", "end");
        public static readonly HashSet<string> LayoutKeys = Keys(
            "rule", "number", "spacing_mm", "array_length_mm", "include_first_bar", "include_last_bar",
            "bars_on_normal_side");
        public static readonly HashSet<string> TerminationKeys = Keys("hook_type", "orientation");
        public static readonly HashSet<string> StirrupZoneRuleKeys = Keys(
            "id", "host", "bar_type", "shape", "allow_new_shape", "style", "profile_mm", "closed", "along",
            "span_mm", "span", "start_offset_mm", "end_offset_mm", "symmetric",
            "minimum_clear_between_zones_mm", "cover", "zones", "start", "end", "mark", "required");
        public static readonly HashSet<string> ZoneKeys = Keys("name", "length_mm", "mark", "layout");
        public static readonly HashSet<string> ZoneCoverKeys = Keys("source", "distance_mm");
        public static readonly HashSet<string> MatRuleKeys = Keys(
            "id", "host", "face_normal", "components", "openings", "mark", "required", "into");
        public static readonly HashSet<string> SectionRuleKeys = Keys(
            "id", "host", "cover", "layers", "stirrups", "mark", "required", "ties", "axis");
        public static readonly HashSet<string> WallBoundaryRuleKeys = Keys(
            "id", "host", "end", "length_mm", "cover", "layers", "stirrups", "mark", "required", "ties");
        public static readonly HashSet<string> SectionTieKeys = Keys(
            "name", "bar_type", "layer", "shape", "allow_new_shape", "start", "end", "mark");
        public static readonly HashSet<string> CrankKeys = Keys("at_mm", "run_mm", "inward_mm");
        public static readonly HashSet<string> SectionCoverKeys = Keys(
            "source", "distance_mm", "top_mm", "bottom_mm", "left_mm", "right_mm", "end_mm");
        public static readonly HashSet<string> SectionLayerKeys = Keys(
            "name", "face", "count", "bar_type", "shape", "allow_new_shape", "extend", "start", "end", "mark", "laps",
            "into", "anchorage", "crank", "between");
        public static readonly HashSet<string> SectionLapKeys = Keys("at_mm", "at_ratio", "length_mm", "length_db", "toward", "coupler_type_name");
        public static readonly HashSet<string> SectionExtentKeys = Keys("from_mm", "to_mm", "from", "run_mm", "run_ratio", "axis", "toward");
        public static readonly HashSet<string> AxisExtentKeys = Keys("axis", "toward", "from_mm", "to_mm");
        public static readonly HashSet<string> SectionStirrupKeys = Keys(
            "bar_type", "shape", "allow_new_shape", "start_offset_mm", "end_offset_mm", "symmetric",
            "minimum_clear_between_zones_mm", "cover", "zones", "start", "end", "mark", "shape_placement", "extent");
        public static readonly HashSet<string> ShapePlacementKeys = Keys(
            "shape", "kind", "pitch_mm", "base_finishing_turns", "top_finishing_turns");
        public static readonly HashSet<string> CoverFaceKeys = Keys("normal");
        public static readonly HashSet<string> MatOpeningsKeys = Keys("policy", "minimum_size_mm", "clearance_mm");
        public static readonly HashSet<string> MatComponentKeys = Keys(
            "name", "mark", "shape", "allow_new_shape", "bar_type", "direction", "offset_from_face_mm",
            "end_cover_mm", "side_cover_mm", "layout", "start_hook", "end_hook", "run", "spread", "follow_face");
        public static readonly HashSet<string> MatWindowKeys = Keys("from_mm", "to_mm");
        public static readonly HashSet<string> FollowFaceKeys = Keys("planarity_mm", "min_facet_mm");

        private static HashSet<string> Keys(params string[] names)
        {
            return new HashSet<string>(names, StringComparer.Ordinal);
        }

        /// <summary>
        /// The largest tolerance this accepts. A tolerance is the width of the band
        /// in which two measurements count as the same, and one of a metre means
        /// nothing can ever disagree - the mirror image of the zero the parser
        /// already refuses, and the more dangerous of the two, because it makes an
        /// audit report agreement rather than failure.
        /// </summary>
        public const double MaxToleranceMm = 500.0;

        /// <summary>
        /// The machine-readable half of a refusal, for the three commands that load a set:
        /// code, schema, the exact field path when there is one, and for an unknown key
        /// the admitted alternatives. One shape, so a caller locates the field the same way
        /// whether it planned, applied or audited.
        /// </summary>
        public static JObject RefusalDetail(StructuralRequirementSet set)
        {
            var d = new JObject
            {
                ["code"] = set.Code,
                ["schema"] = SchemaName,
                ["path"] = set.Path
            };
            if (set.Allowed.Count > 0) d["allowed"] = new JArray(set.Allowed);
            if (set.DidYouMean.Count > 0) d["did_you_mean"] = new JArray(set.DidYouMean);
            return d;
        }

        /// <summary>Parse and validate. Returns a set whose Ok is false rather than throwing.</summary>
        public static StructuralRequirementSet Load(JObject doc)
        {
            var s = new StructuralRequirementSet();
            if (doc == null) return Fail(s, CodeSchema, "the requirement set is not a JSON object.");
            if (UnknownKey(s, doc, RootKeys, null) != null) return s;

            string schema = doc.Value<string>("schema");
            if (!string.Equals(schema, SchemaName, StringComparison.Ordinal))
                return Fail(s, CodeSchema,
                    "schema must be '" + SchemaName + "' - got " + Show(schema) + ". The version is part of the " +
                    "contract: a set written for another schema may mean something different by the same words.");

            // MILLIMETRES, and only millimetres. Accepting a units field with more
            // than one legal value would mean every number in every rule had to be
            // read together with it, and a set that lost the field on the way would
            // be read as feet without anything looking wrong.
            string units = doc.Value<string>("units");
            if (units != null && !string.Equals(units, "millimeter", StringComparison.Ordinal))
                return Fail(s, CodeUnits,
                    "units must be 'millimeter' or absent - got " + Show(units) + ". Every length in this " +
                    "schema is millimetres by definition.");

            JObject header = doc["requirement_set"] as JObject;
            if (header == null) return Fail(s, CodeMissing, "requirement_set is required and must be an object.");
            if (UnknownKey(s, header, HeaderKeys, "requirement_set") != null) return s;
            s.Id = header.Value<string>("id");
            s.Version = header.Value<string>("version");
            s.Title = header.Value<string>("title");
            if (string.IsNullOrWhiteSpace(s.Id)) return Fail(s, CodeMissing, "requirement_set.id is required.");
            if (string.IsNullOrWhiteSpace(s.Version)) return Fail(s, CodeMissing, "requirement_set.version is required.");

            JToken hold = doc["hold_ends"];
            if (hold != null)
            {
                if (hold.Type != JTokenType.Boolean)
                    return Fail(s, CodeUnknownValue, "hold_ends must be true or false.", "hold_ends");
                s.HoldEnds = (bool)hold;
            }

            JObject tol = doc["tolerances"] as JObject;
            if (tol != null)
            {
                if (UnknownKey(s, tol, ToleranceKeys, "tolerances") != null) return s;
                s.Tolerances.LengthMm = tol.Value<double?>("length_mm") ?? s.Tolerances.LengthMm;
                s.Tolerances.SpacingMm = tol.Value<double?>("spacing_mm") ?? s.Tolerances.SpacingMm;
                s.Tolerances.CoverMm = tol.Value<double?>("cover_mm") ?? s.Tolerances.CoverMm;
                s.Tolerances.AngleDegrees = tol.Value<double?>("angle_degrees") ?? s.Tolerances.AngleDegrees;
                foreach (var pair in new[]
                         {
                             new { n = "length_mm", v = s.Tolerances.LengthMm },
                             new { n = "spacing_mm", v = s.Tolerances.SpacingMm },
                             new { n = "cover_mm", v = s.Tolerances.CoverMm },
                             new { n = "angle_degrees", v = s.Tolerances.AngleDegrees }
                         })
                {
                    if (!RebarLayoutRules.IsFinite(pair.v))
                        return Fail(s, CodeNotFinite,
                            "tolerances." + pair.n + " is not a finite number. Every comparison against NaN is " +
                            "false, so a tolerance like this makes every measurement agree with every other one " +
                            "and an audit can never report a difference again.");
                    if (pair.v <= 0)
                        return Fail(s, CodeUnknownValue,
                            "tolerances." + pair.n + " must be greater than zero - a tolerance of zero means no " +
                            "measurement can ever agree, which is not what anybody means by it.");
                    if (pair.v > MaxToleranceMm)
                        return Fail(s, CodeUnknownValue,
                            "tolerances." + pair.n + " is " + pair.v.ToString("0.###", CultureInfo.InvariantCulture) +
                            ", and the limit is " + MaxToleranceMm + ". A tolerance that wide is not a tolerance: " +
                            "nothing measured could ever disagree with anything declared.");
                }
            }

            // ------------------------------------------------------- bar types
            foreach (JToken t in doc["bar_types"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) return Fail(s, CodeSchema, "every entry in bar_types must be an object.");
                string id = o.Value<string>("id");
                if (UnknownKey(s, o, BarTypeKeys, "bar_types[" + s.BarTypes.Count + "]") != null) return s;
                if (string.IsNullOrWhiteSpace(id)) return Fail(s, CodeMissing, "every bar_types entry needs an id.");
                if (s.BarTypes.ContainsKey(id)) return Fail(s, CodeDuplicateId, "bar_types has two entries with id '" + id + "'.");
                string typeName = o.Value<string>("type_name");
                if (string.IsNullOrWhiteSpace(typeName))
                    return Fail(s, CodeMissing,
                        "bar_types['" + id + "'] needs type_name: the name of a RebarBarType that exists in the " +
                        "model. This bridge does not create bar types, because a bar type carries a diameter, a " +
                        "bend radius and a grade, and inventing those is designing.");
                double? dia = o.Value<double?>("nominal_diameter_mm");
                if (dia.HasValue && (!RebarLayoutRules.IsFinite(dia.Value) || dia.Value <= 0))
                    return Fail(s, CodeUnknownValue,
                        "bar_types['" + id + "'].nominal_diameter_mm must be a finite number greater than zero.");
                s.BarTypes[id] = new StructuralBarTypeRef
                {
                    Id = id,
                    TypeName = typeName,
                    NominalDiameterMm = dia
                };
            }

            // ------------------------------------------------------ hook types
            foreach (JToken t in doc["hook_types"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) return Fail(s, CodeSchema, "every entry in hook_types must be an object.");
                string id = o.Value<string>("id");
                if (UnknownKey(s, o, HookTypeKeys, "hook_types[" + s.HookTypes.Count + "]") != null) return s;
                if (string.IsNullOrWhiteSpace(id)) return Fail(s, CodeMissing, "every hook_types entry needs an id.");
                if (s.HookTypes.ContainsKey(id)) return Fail(s, CodeDuplicateId, "hook_types has two entries with id '" + id + "'.");
                string typeName = o.Value<string>("type_name");
                bool none = o.Value<bool?>("none") ?? false;
                if (!none && string.IsNullOrWhiteSpace(typeName))
                    return Fail(s, CodeMissing,
                        "hook_types['" + id + "'] needs type_name, or none: true to mean an explicitly straight end.");
                var href = new StructuralHookTypeRef { Id = id, TypeName = typeName, None = none };
                if (ReadHookLengths(s, o["lengths_mm"], href) != null) return s;
                s.HookTypes[id] = href;
            }

            // ----------------------------------------------------- cover rules
            var coverIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken t in doc["cover_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) return Fail(s, CodeSchema, "every entry in cover_rules must be an object.");
                var r = new StructuralCoverRule { Id = o.Value<string>("id") };
                if (UnknownKey(s, o, CoverRuleKeys, "cover_rules[" + s.CoverRules.Count + "]") != null) return s;
                if (string.IsNullOrWhiteSpace(r.Id)) return Fail(s, CodeMissing, "every cover rule needs an id.");
                if (!coverIds.Add(r.Id)) return Fail(s, CodeDuplicateId, "two cover rules share the id '" + r.Id + "'.");
                if (UnknownKey(s, o["host"] as JObject, HostKeys, "cover_rules['" + r.Id + "'].host") != null) return s;
                string err = ReadSelector(o["host"] as JObject, r.Host, "cover_rules['" + r.Id + "'].host");
                if (err != null) return Fail(s, CodeMissing, err, "cover_rules['" + r.Id + "'].host");
                // THE FACE: 'common', or { normal: [x, y, z] } - every exposed face whose OUTWARD
                // normal is that vector (L7). A word like 'top' is refused: a slab has two faces, a
                // sloped one is neither, and a Revit reference does not survive a re-issue. The
                // normal does, and it is what the mats already name their face by.
                string fat = "cover_rules['" + r.Id + "'].face";
                JToken faceTok = o["face"];
                if (faceTok == null || faceTok.Type == JTokenType.Null) r.Face = StructuralCoverRule.FaceCommon;
                else if (faceTok is JObject fo)
                {
                    if (UnknownKey(s, fo, CoverFaceKeys, fat) != null) return s;
                    double[] fn;
                    string ferr = ReadDirection(fo["normal"] as JArray, fat + ".normal", out fn);
                    if (ferr != null)
                        return Fail(s, CodeGeometry, ferr + " It is the OUTWARD normal of the faces this cover goes on - " +
                                    "[0,0,-1] for the underside of a footing.", fat + ".normal");
                    double fl = Math.Sqrt(fn[0] * fn[0] + fn[1] * fn[1] + fn[2] * fn[2]);
                    r.Face = StructuralCoverRule.FaceNormal;
                    r.FaceNormalMm = new[] { fn[0] / fl, fn[1] / fl, fn[2] / fl };
                }
                else if (faceTok.Type == JTokenType.String && (faceTok.Value<string>() ?? "").Trim() == StructuralCoverRule.FaceCommon)
                    r.Face = StructuralCoverRule.FaceCommon;
                else
                    return Fail(s, CodeUnknownValue,
                        fat + " is " + faceTok.ToString(Newtonsoft.Json.Formatting.None) + ". A face is 'common', or " +
                        "{ \"normal\": [x, y, z] }: every exposed face whose outward normal that is. It is never a word " +
                        "like 'top' - a slab has two faces and a sloped one is neither - and never a Revit reference, " +
                        "which does not survive a re-issue.", fat);
                r.CoverTypeName = o.Value<string>("cover_type_name");
                r.DistanceMm = o.Value<double?>("distance_mm");
                if (string.IsNullOrWhiteSpace(r.CoverTypeName) && !r.DistanceMm.HasValue)
                    return Fail(s, CodeMissing,
                        "cover_rules['" + r.Id + "'] must name a cover_type_name, a distance_mm, or both. A cover " +
                        "this bridge picked would be a design decision.");
                if (r.DistanceMm.HasValue && !RebarLayoutRules.IsFinite(r.DistanceMm.Value))
                    return Fail(s, CodeNotFinite, "cover_rules['" + r.Id + "'].distance_mm is not a finite number.");
                if (r.DistanceMm.HasValue && r.DistanceMm.Value <= 0)
                    return Fail(s, CodeUnknownValue, "cover_rules['" + r.Id + "'].distance_mm must be greater than zero.");
                r.Required = o.Value<bool?>("required") ?? true;
                s.CoverRules.Add(r);
            }

            // ----------------------------------------------------- rebar rules
            var ruleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken t in doc["reinforcement_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) return Fail(s, CodeSchema, "every entry in reinforcement_rules must be an object.");
                var r = new StructuralRebarRule { Id = o.Value<string>("id"), Raw = o };
                if (UnknownKey(s, o, RebarRuleKeys, "reinforcement_rules[" + s.RebarRules.Count + "]") != null) return s;
                if (string.IsNullOrWhiteSpace(r.Id)) return Fail(s, CodeMissing, "every reinforcement rule needs an id.");
                if (!ruleIds.Add(r.Id)) return Fail(s, CodeDuplicateId, "two reinforcement rules share the id '" + r.Id + "'.");

                if (UnknownKey(s, o["host"] as JObject, HostKeys, "reinforcement_rules['" + r.Id + "'].host") != null) return s;
                string err = ReadSelector(o["host"] as JObject, r.Host, "reinforcement_rules['" + r.Id + "'].host");
                if (err != null) return Fail(s, CodeMissing, err, "reinforcement_rules['" + r.Id + "'].host");

                r.BarTypeId = o.Value<string>("bar_type");
                if (string.IsNullOrWhiteSpace(r.BarTypeId))
                    return Fail(s, CodeMissing, "reinforcement_rules['" + r.Id + "'] needs bar_type.");
                if (!s.BarTypes.ContainsKey(r.BarTypeId))
                    return Fail(s, CodeUnresolvedReference,
                        "reinforcement_rules['" + r.Id + "'].bar_type is '" + r.BarTypeId +
                        "', which no bar_types entry declares.");

                r.ShapeName = o.Value<string>("shape");
                // DECLARED, not defaulted. It used to fall back to `standard`,
                // which is a design decision taken in silence - and the
                // requirement-set documentation lists this field as declared, so the
                // code and the document disagreed about it.
                if (o["style"] == null)
                    return Fail(s, CodeMissing,
                        "reinforcement_rules['" + r.Id + "'] needs style: " +
                        string.Join(" or ", StructuralStyle.All) +
                        ". It is not defaulted - a closed rectangle is a stirrup in a beam and an edge bar in a " +
                        "slab, and the geometry does not say which.");
                r.Style = (o.Value<string>("style") ?? "").Trim();
                if (!StructuralStyle.IsKnown(r.Style))
                    return Fail(s, CodeUnknownValue,
                        "reinforcement_rules['" + r.Id + "'].style must be " + string.Join(" or ", StructuralStyle.All) +
                        " - got " + Show(r.Style) + ". The style is declared and never inferred from the shape of " +
                        "the curves: a closed rectangle is a stirrup in a beam and a slab edge bar in a slab, and " +
                        "the geometry does not say which.");

                string gcode;
                string gerr = ReadCurves(o, r, out gcode);
                if (gerr != null) return Fail(s, gcode, gerr);

                JArray normal = o["normal"] as JArray;
                if (normal == null)
                    return Fail(s, CodeMissing,
                        "reinforcement_rules['" + r.Id + "'] needs normal: the direction the SET marches in, as " +
                        "[x, y, z]. It is not derivable from one bar's curves - the same bar distributes along a " +
                        "beam or up a column depending on it - so it is declared.");
                if (normal.Count != 3)
                    return Fail(s, CodeGeometry, "reinforcement_rules['" + r.Id + "'].normal must have three numbers.");
                var nrm = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (normal[i].Type != JTokenType.Integer && normal[i].Type != JTokenType.Float)
                        return Fail(s, CodeNotANumber, "reinforcement_rules['" + r.Id + "'].normal carries " +
                            normal[i].ToString(Newtonsoft.Json.Formatting.None) + ", which is not a number.");
                    nrm[i] = normal[i].Value<double>();
                    // A NaN normal passes the zero-vector test - Math.Abs(NaN) is NaN
                    // and NaN < 1e-9 is false - and then collapses every projection
                    // downstream to a span of nothing.
                    if (!RebarLayoutRules.IsFinite(nrm[i]))
                        return Fail(s, CodeNotFinite,
                            "reinforcement_rules['" + r.Id + "'].normal carries a value that is not finite.");
                }
                r.NormalMm = nrm;
                if (Math.Abs(r.NormalMm[0]) + Math.Abs(r.NormalMm[1]) + Math.Abs(r.NormalMm[2]) < 1e-9)
                    return Fail(s, CodeGeometry, "reinforcement_rules['" + r.Id + "'].normal is the zero vector.");

                string rat = "reinforcement_rules['" + r.Id + "']";
                if (UnknownKey(s, o["layout"] as JObject, LayoutKeys, rat + ".layout") != null) return s;
                string lerr = ReadLayout(o["layout"] as JObject, r, s);
                if (lerr != null) return Fail(s, CodeLayout, lerr, rat + ".layout");

                if (UnknownKey(s, o["start"] as JObject, TerminationKeys, rat + ".start") != null) return s;
                if (UnknownKey(s, o["end"] as JObject, TerminationKeys, rat + ".end") != null) return s;
                string terr = ReadTermination(o["start"] as JObject, r.Start, s, r.Id, "start");
                if (terr != null) return Fail(s, CodeUnknownValue, terr, rat + ".start");
                terr = ReadTermination(o["end"] as JObject, r.End, s, r.Id, "end");
                if (terr != null) return Fail(s, CodeUnknownValue, terr, rat + ".end");

                if (ReadInto(s, o["into"], r.Into, rat) != null) return s;
                if (ReadAnchorage(s, o["anchorage"], r.Into, r.Anchorage, rat, false) != null) return s;

                r.Mark = o.Value<string>("mark");
                r.Required = o.Value<bool?>("required") ?? true;
                JToken rhold = o["hold_ends"];
                if (rhold != null)
                {
                    if (rhold.Type != JTokenType.Boolean)
                        return Fail(s, CodeUnknownValue, rat + ".hold_ends must be true or false.", rat + ".hold_ends");
                    r.HoldEnds = (bool)rhold;
                }
                r.AllowNewShape = o.Value<bool?>("allow_new_shape") ?? false;
                if (r.AllowNewShape && !string.IsNullOrWhiteSpace(r.ShapeName))
                    return Fail(s, CodeUnknownValue,
                        "reinforcement_rules['" + r.Id + "'] names a shape AND sets allow_new_shape. Those are " +
                        "two different instructions - use the shape that exists, or let Revit make one - and " +
                        "which was meant cannot be read from the set.");
                s.RebarRules.Add(r);
            }

            string zerr = ReadStirrupZoneRules(doc, s, ruleIds);
            if (zerr != null) return s;

            string merr = ReadMatRules(doc, s, ruleIds);
            if (merr != null) return s;

            string serr = ReadSectionRules(doc, s, ruleIds);
            if (serr != null) return s;

            string werr = ReadWallBoundaryRules(doc, s, ruleIds);
            if (werr != null) return s;
            if (ReadAreaRules(doc, s, ruleIds) != null) return s;
            if (ReadPathRules(doc, s, ruleIds) != null) return s;
            if (ReadFabricRules(doc, s, ruleIds) != null) return s;
            if (ReadClearances(doc, s, ruleIds) != null) return s;
            if (ReadCrossingOrder(doc["crossing_order"], s, ruleIds) != null) return s;

            if (s.CoverRules.Count == 0 && s.RebarRules.Count == 0 && s.StirrupZoneRules.Count == 0 &&
                s.MatRules.Count == 0 && s.SectionRules.Count == 0 && s.WallBoundaryRules.Count == 0 &&
                s.AreaRules.Count == 0 && s.PathRules.Count == 0 && s.FabricRules.Count == 0)
                return Fail(s, CodeNoRules,
                    "the set declares no cover_rules, no reinforcement_rules, no stirrup_zone_rules, no " +
                    "mat_rules, no section_rules, no wall_boundary_rules, no area_rules, no path_rules and no " +
                    "fabric_rules, so it asks for nothing.");
            return s;
        }

        /// <summary>
        /// stirrup_zone_rules. Returns null on success, or a message when the set is
        /// already failed - the failure itself is recorded on `s`.
        /// </summary>
        private static string ReadStirrupZoneRules(JObject doc, StructuralRequirementSet s,
                                                   HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["stirrup_zone_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in stirrup_zone_rules must be an object."); return "x"; }
                StructuralStirrupZoneRule z;
                if (ReadOneZoneRule(o, s, ruleIds, "stirrup_zone_rules", out z) != null) return "x";
                s.StirrupZoneRules.Add(z);
            }
            return null;
        }

        /// <summary>
        /// One stirrup zone rule. Shared by stirrup_zone_rules and by the stirrups of a
        /// section rule, so the two read zones, cover, offsets and hooks by one set of
        /// rules. Returns null on success; the failure is recorded on `s`.
        /// </summary>
        private static string ReadOneZoneRule(JObject o, StructuralRequirementSet s, HashSet<string> ruleIds,
                                              string collection, out StructuralStirrupZoneRule z)
        {
            z = new StructuralStirrupZoneRule { Id = o.Value<string>("id"), Raw = o };
            {
                string at = collection + "['" + z.Id + "']";
                if (UnknownKey(s, o, StirrupZoneRuleKeys, collection + "[" + s.StirrupZoneRules.Count + "]") != null) return "x";
                if (string.IsNullOrWhiteSpace(z.Id)) { Fail(s, CodeMissing, "every stirrup zone rule needs an id."); return "x"; }
                if (!ruleIds.Add(z.Id))
                {
                    Fail(s, CodeDuplicateId,
                        "'" + z.Id + "' is used by more than one rule. A stirrup zone rule expands into one " +
                        "reinforcement rule per zone, named <id>#<zone>, so its id shares the namespace.");
                    return "x";
                }

                if (UnknownKey(s, o["host"] as JObject, HostKeys, at + ".host") != null) return "x";
                string err = ReadSelector(o["host"] as JObject, z.Host, at + ".host");
                if (err != null) { Fail(s, CodeMissing, err, at + ".host"); return "x"; }

                z.BarTypeId = o.Value<string>("bar_type");
                if (string.IsNullOrWhiteSpace(z.BarTypeId)) { Fail(s, CodeMissing, at + " needs bar_type."); return "x"; }
                if (!s.BarTypes.ContainsKey(z.BarTypeId))
                {
                    Fail(s, CodeUnresolvedReference,
                        at + ".bar_type is '" + z.BarTypeId + "', which no bar_types entry declares.");
                    return "x";
                }
                z.ShapeName = o.Value<string>("shape");
                z.AllowNewShape = o.Value<bool?>("allow_new_shape") ?? false;
                if (z.AllowNewShape && !string.IsNullOrWhiteSpace(z.ShapeName))
                {
                    Fail(s, CodeUnknownValue,
                        at + " names a shape AND sets allow_new_shape. Those are two different instructions.");
                    return "x";
                }

                // Style DEFAULTS to stirrup_tie here and only here, because the rule
                // is called stirrup_zone_rules. A rule that wants standard bars in
                // zones says so, and that is a different declaration from silence.
                if (o["style"] != null)
                {
                    z.Style = (o.Value<string>("style") ?? "").Trim();
                    if (!StructuralStyle.IsKnown(z.Style))
                    {
                        Fail(s, CodeUnknownValue, at + ".style must be " + string.Join(" or ", StructuralStyle.All));
                        return "x";
                    }
                }

                string perr = ReadPointList(o["profile_mm"] as JArray, at + ".profile_mm", z.ProfileMm);
                if (perr != null) { Fail(s, CodeGeometry, perr); return "x"; }
                if (z.ProfileMm.Count < 2)
                {
                    Fail(s, CodeGeometry,
                        at + ".profile_mm needs at least two points: it is the stirrup outline at the START of " +
                        "the run, in model coordinates, and every zone is that outline moved along the beam.");
                    return "x";
                }
                z.Closed = o.Value<bool?>("closed") ?? true;

                // THE SAME GUARDS THE REINFORCEMENT PATH HAS. profile_mm was read by
                // a generic point-list reader that checks only that the numbers are
                // numbers, so a zero-length segment - and a closed profile that also
                // repeats its first point - reached Revit, which refused it as
                // curve_degenerate at APPLY time. By then the rehearsal had already
                // issued a confirmation token for it. Measured live on 2026-08-28:
                // three zones, three curve_degenerate refusals, after a plan that
                // reported the rule resolved.
                for (int i = 1; i < z.ProfileMm.Count; i++)
                    if (Distance(z.ProfileMm[i - 1], z.ProfileMm[i]) < 1e-6)
                    {
                        Fail(s, CodeGeometry,
                            at + ".profile_mm has two identical consecutive points at index " + (i - 1) +
                            " and " + i + ", which is a segment of zero length.");
                        return "x";
                    }
                if (z.Closed && z.ProfileMm.Count > 1 &&
                    Distance(z.ProfileMm[z.ProfileMm.Count - 1], z.ProfileMm[0]) < 1e-6)
                {
                    Fail(s, CodeGeometry,
                        at + " is closed and also repeats its first point at the end. Declare the corners " +
                        "once; closed adds the last segment.");
                    return "x";
                }

                string aerr = ReadDirection(o["along"] as JArray, at + ".along", out z.AlongMm);
                if (aerr != null) { Fail(s, CodeGeometry, aerr); return "x"; }

                // The span. Declared, or measured from the host by the resolver -
                // which is the only one of the two that can see the model.
                JToken span = o["span_mm"];
                string spanWord = o.Value<string>("span");
                if (span != null && spanWord != null)
                {
                    Fail(s, CodeUnknownValue, at + " declares both span_mm and span. State one.");
                    return "x";
                }
                if (span != null)
                {
                    if (span.Type != JTokenType.Integer && span.Type != JTokenType.Float)
                    { Fail(s, CodeNotANumber, at + ".span_mm is not a number."); return "x"; }
                    double v = span.Value<double>();
                    if (!RebarLayoutRules.IsFinite(v) || v <= 0)
                    { Fail(s, CodeNotFinite, at + ".span_mm must be a positive finite length."); return "x"; }
                    z.SpanMm = v;
                }
                else if (spanWord == "host_length") z.SpanFromHost = true;
                else if (spanWord != null)
                {
                    Fail(s, CodeUnknownValue,
                        at + ".span is '" + spanWord + "'. The only word is host_length; otherwise state span_mm.");
                    return "x";
                }
                else
                {
                    Fail(s, CodeMissing,
                        at + " needs span_mm, or span: host_length to measure the host's location curve. " +
                        "The zones have to run along something and this bridge does not guess which.");
                    return "x";
                }

                z.StartOffsetMm = ReadNonNegative(o["start_offset_mm"], 0);
                z.EndOffsetMm = ReadNonNegative(o["end_offset_mm"], 0);
                if (double.IsNaN(z.StartOffsetMm) || double.IsNaN(z.EndOffsetMm))
                { Fail(s, CodeNotFinite, at + " has an offset that is not a finite, non-negative number."); return "x"; }
                z.Symmetric = o.Value<bool?>("symmetric") ?? false;

                JToken minClear = o["minimum_clear_between_zones_mm"];
                if (minClear != null && minClear.Type != JTokenType.Null)
                {
                    if (minClear.Type != JTokenType.Integer && minClear.Type != JTokenType.Float)
                    { Fail(s, CodeNotANumber, at + ".minimum_clear_between_zones_mm is not a number."); return "x"; }
                    double v = minClear.Value<double>();
                    if (!RebarLayoutRules.IsFinite(v) || v < 0)
                    { Fail(s, CodeNotFinite, at + ".minimum_clear_between_zones_mm must be finite and not negative."); return "x"; }
                    z.MinimumClearBetweenZonesMm = v;
                }

                // THE COVER BLOCK. Declared in full or absent in full: a source
                // with no distance, or a distance beside source: host, are two
                // statements that could disagree, and the parser will not pick.
                JToken coverTok = o["cover"];
                if (coverTok != null && coverTok.Type != JTokenType.Null)
                {
                    var co = coverTok as JObject;
                    if (co == null)
                    {
                        Fail(s, CodeSchema, at + ".cover must be an object: { source: host | declared, distance_mm }.", at + ".cover");
                        return "x";
                    }
                    if (UnknownKey(s, co, ZoneCoverKeys, at + ".cover") != null) return "x";
                    var cover = new StructuralStirrupZoneCover { Source = (co.Value<string>("source") ?? "").Trim() };
                    if (Array.IndexOf(StructuralStirrupZoneCover.Sources, cover.Source) < 0)
                    {
                        Fail(s, CodeUnknownValue,
                            at + ".cover.source must be " + string.Join(" or ", StructuralStirrupZoneCover.Sources) +
                            " - got " + Show(co.Value<string>("source")) + ". 'host' reads the host's common cover " +
                            "when the rule is resolved; 'declared' takes distance_mm. Revit clamps a hosted array " +
                            "to the host's cover plus the bar radius at each end, and the zone planner has to be " +
                            "told which number to predict with.", at + ".cover.source");
                        return "x";
                    }
                    string derr;
                    double? dist = ReadNumber(co["distance_mm"], at + ".cover.distance_mm", out derr);
                    if (derr != null) { Fail(s, CodeNotANumber, derr, at + ".cover.distance_mm"); return "x"; }
                    if (cover.Source == StructuralStirrupZoneCover.SourceDeclared)
                    {
                        if (!dist.HasValue)
                        {
                            Fail(s, CodeMissing,
                                at + ".cover declares source: declared and no distance_mm. The distance is the " +
                                "whole declaration; nothing here supplies one.", at + ".cover.distance_mm");
                            return "x";
                        }
                        if (dist.Value < 0)
                        {
                            Fail(s, CodeUnknownValue, at + ".cover.distance_mm must be zero or more.", at + ".cover.distance_mm");
                            return "x";
                        }
                        cover.DistanceMm = dist;
                    }
                    else if (dist.HasValue)
                    {
                        Fail(s, CodeUnknownValue,
                            at + ".cover declares source: host AND a distance_mm. The host's cover is read from " +
                            "the model when the rule is resolved, and a number beside it could disagree with " +
                            "what is read; state one or the other.", at + ".cover.distance_mm");
                        return "x";
                    }
                    z.Cover = cover;
                }

                var zonesArr = o["zones"] as JArray;
                if (zonesArr == null || zonesArr.Count == 0)
                {
                    Fail(s, CodeMissing,
                        at + " needs zones: a list of {name, length_mm, layout}. One of them may leave length_mm " +
                        "out, and that one is the rest of the span.");
                    return "x";
                }
                foreach (JToken zt in zonesArr)
                {
                    var zo = zt as JObject;
                    if (zo == null) { Fail(s, CodeSchema, at + ".zones must contain objects."); return "x"; }
                    string zat = at + ".zones[" + z.Zones.Count + "]";
                    if (UnknownKey(s, zo, ZoneKeys, zat) != null) return "x";
                    if (UnknownKey(s, zo["layout"] as JObject, LayoutKeys, zat + ".layout") != null) return "x";
                    var req = new StirrupZoneRequest { Name = zo.Value<string>("name"), Mark = zo.Value<string>("mark") };
                    JToken len = zo["length_mm"];
                    if (len != null && len.Type != JTokenType.Null)
                    {
                        if (len.Type != JTokenType.Integer && len.Type != JTokenType.Float)
                        { Fail(s, CodeNotANumber, at + ".zones length_mm is not a number."); return "x"; }
                        req.LengthMm = len.Value<double>();
                    }
                    // ReadLayout looks the bar type up to seed the diameter that
                    // minimum_clear_spacing counts with, so the holder carries it.
                    var holder = new StructuralRebarRule { Id = z.Id, BarTypeId = z.BarTypeId };
                    string lerr = ReadLayout(zo["layout"] as JObject, holder, s, false,
                                             at + ".zones['" + (req.Name ?? "?") + "']");
                    if (lerr != null) { Fail(s, CodeLayout, lerr); return "x"; }
                    req.Layout = holder.Layout;

                    // THE EXPANDED ID IS RESERVED TOO. The refusal above promises
                    // that a zone rule expands into <id>#<zone> and that its id
                    // shares the namespace - and only the top-level id was ever
                    // registered, so a reinforcement rule literally called
                    // "B1#start" parsed happily beside a zone rule that expands to
                    // exactly that. Two sets, one rule id, and provenance keys on it.
                    string expandedId = z.Id + "#" + (req.Name ?? ("zone" + (z.Zones.Count + 1)));
                    if (!ruleIds.Add(expandedId))
                    {
                        Fail(s, CodeDuplicateId,
                            "'" + expandedId + "' is used by more than one rule. " + at + " expands into it.");
                        return "x";
                    }
                    z.Zones.Add(req);
                }

                if (UnknownKey(s, o["start"] as JObject, TerminationKeys, at + ".start") != null) return "x";
                if (UnknownKey(s, o["end"] as JObject, TerminationKeys, at + ".end") != null) return "x";
                string terr = ReadTermination(o["start"] as JObject, z.Start, s, z.Id, "start");
                if (terr != null) { Fail(s, CodeUnknownValue, terr, at + ".start"); return "x"; }
                terr = ReadTermination(o["end"] as JObject, z.End, s, z.Id, "end");
                if (terr != null) { Fail(s, CodeUnknownValue, terr, at + ".end"); return "x"; }

                z.Mark = o.Value<string>("mark");
                z.Required = o.Value<bool?>("required") ?? true;
            }
            return null;
        }

        /// <summary>
        /// { axis, toward, from_mm, to_mm }: stations from the grid named axis, positive toward the grid
        /// named toward. Both names are required and different, from_mm &lt; to_mm; either may be
        /// negative - beyond the axis, away from toward. Whether the host reaches them is the plan's.
        /// </summary>
        private static string ReadAxisExtent(StructuralRequirementSet s, JObject o, string at, out string axis, out string toward, out double from, out double to)
        {
            axis = o["axis"]?.Type == JTokenType.String ? (string)o["axis"] : null;
            toward = o["toward"]?.Type == JTokenType.String ? (string)o["toward"] : null;
            from = to = 0;
            if (string.IsNullOrWhiteSpace(axis) || string.IsNullOrWhiteSpace(toward) || axis == toward)
            {
                Fail(s, CodeMissing, at + " needs axis and toward: the names of two different grids, as the model names them.", at);
                return "x";
            }
            string e1, e2;
            double? f = ReadNumber(o["from_mm"], at + ".from_mm", out e1), t = ReadNumber(o["to_mm"], at + ".to_mm", out e2);
            if (e1 != null || e2 != null || !f.HasValue || !t.HasValue || t.Value <= f.Value)
            {
                Fail(s, CodeGeometry, e1 ?? e2 ?? (at + " needs from_mm and to_mm with from_mm < to_mm, measured from axis '" + axis + "' toward '" + toward + "'."), at);
                return "x";
            }
            from = f.Value; to = t.Value;
            return null;
        }

        /// <summary>
        /// section_rules (F1.1): bars and stirrups declared relative to the host's section.
        /// Returns null on success; the failure is recorded on `s`. Every value is checked
        /// here; what depends on the host - its frame, its cover - is checked when the
        /// resolver expands the rule for each host.
        /// </summary>
        private static string ReadSectionRules(JObject doc, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["section_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in section_rules must be an object."); return "x"; }
                StructuralSectionRule r;
                if (ReadOneSectionRule(o, s, ruleIds, "section_rules", SectionRuleKeys, s.SectionRules.Count, out r) != null) return "x";
                s.SectionRules.Add(r);
            }
            return null;
        }

        /// <summary>
        /// One section rule - shared by section_rules and by wall_boundary_rules, whose section
        /// is a wall's end region, so the two read cover, layers, stirrups and ties by one set of
        /// rules. `collection` names the paths; `keys` are the keys admitted at the rule's level.
        /// Returns null on success; the failure is recorded on `s`.
        /// </summary>
        private static string ReadOneSectionRule(JObject o, StructuralRequirementSet s, HashSet<string> ruleIds, string collection,
                                                 HashSet<string> keys, int index, out StructuralSectionRule r)
        {
            r = new StructuralSectionRule { Id = o.Value<string>("id"), Raw = o };
            {
                string at = collection + "['" + r.Id + "']";
                if (UnknownKey(s, o, keys, collection + "[" + index + "]") != null) return "x";
                if (string.IsNullOrWhiteSpace(r.Id)) { Fail(s, CodeMissing, "every section rule needs an id."); return "x"; }
                if (r.Id.Contains("#"))
                {
                    Fail(s, CodeUnknownValue, at + ": a section rule id may not contain '#', which separates the " +
                         "ids it expands into (<id>#h<element>#<layer>).", at + ".id");
                    return "x";
                }
                if (!ruleIds.Add(r.Id))
                {
                    Fail(s, CodeDuplicateId, "'" + r.Id + "' is used by more than one rule.");
                    return "x";
                }
                if (UnknownKey(s, o["host"] as JObject, HostKeys, at + ".host") != null) return "x";
                string err = ReadSelector(o["host"] as JObject, r.Host, at + ".host");
                if (err != null) { Fail(s, CodeMissing, err, at + ".host"); return "x"; }

                if (o["axis"] != null)
                {
                    r.Axis = o.Value<string>("axis");
                    if (r.Axis != "vertical")
                    { Fail(s, CodeUnknownValue, at + ".axis must be vertical - got " + Show(r.Axis) + ". It frames a point-placed foundation (a pile) as a vertical member.", at + ".axis"); return "x"; }
                }

                // THE COVER: from the host, one distance, or all five faces - never a mix.
                var co = o["cover"] as JObject;
                if (co == null)
                {
                    Fail(s, CodeMissing, at + " needs cover: { source: host }, { distance_mm }, or every one of " +
                         "top_mm, bottom_mm, left_mm, right_mm and end_mm. Where the steel sits is the cover.", at + ".cover");
                    return "x";
                }
                if (UnknownKey(s, co, SectionCoverKeys, at + ".cover") != null) return "x";
                string source = co.Value<string>("source");
                var faces = new[] { "top_mm", "bottom_mm", "left_mm", "right_mm", "end_mm" };
                int facesGiven = faces.Count(k => co[k] != null);
                bool distanceGiven = co["distance_mm"] != null;
                int forms = (source != null ? 1 : 0) + (distanceGiven ? 1 : 0) + (facesGiven > 0 ? 1 : 0);
                if (forms != 1)
                {
                    Fail(s, CodeUnknownValue, at + ".cover states " + (forms == 0 ? "nothing" : "more than one form") +
                         ". Use exactly one: source: host, distance_mm, or the five face values.", at + ".cover");
                    return "x";
                }
                if (source != null)
                {
                    if (source != "host")
                    { Fail(s, CodeUnknownValue, at + ".cover.source must be host - got " + Show(source) + ".", at + ".cover.source"); return "x"; }
                    r.Cover.FromHost = true;
                }
                else if (distanceGiven)
                {
                    string derr;
                    double? d = ReadNumber(co["distance_mm"], at + ".cover.distance_mm", out derr);
                    if (derr != null || !d.HasValue || d.Value < 0)
                    { Fail(s, CodeNotANumber, derr ?? at + ".cover.distance_mm must be zero or more.", at + ".cover.distance_mm"); return "x"; }
                    r.Cover.CommonMm = d;
                }
                else
                {
                    if (facesGiven != faces.Length)
                    {
                        Fail(s, CodeMissing, at + ".cover gives " + facesGiven + " of the five faces. Per-face cover is all " +
                             "five - top_mm, bottom_mm, left_mm, right_mm, end_mm - or none.", at + ".cover");
                        return "x";
                    }
                    var v = new double[5];
                    for (int i = 0; i < 5; i++)
                    {
                        string derr;
                        double? d = ReadNumber(co[faces[i]], at + ".cover." + faces[i], out derr);
                        if (derr != null || !d.HasValue || d.Value < 0)
                        { Fail(s, CodeNotANumber, derr ?? at + ".cover." + faces[i] + " must be zero or more.", at + ".cover." + faces[i]); return "x"; }
                        v[i] = d.Value;
                    }
                    r.Cover.TopMm = v[0]; r.Cover.BottomMm = v[1]; r.Cover.LeftMm = v[2]; r.Cover.RightMm = v[3]; r.Cover.EndMm = v[4];
                }

                // THE LAYERS.
                var layersArr = o["layers"] as JArray;
                if ((layersArr == null || layersArr.Count == 0) && o["stirrups"] == null)
                {
                    Fail(s, CodeMissing, at + " declares no layers and no stirrups, so it asks for nothing.", at + ".layers");
                    return "x";
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken lt in layersArr ?? new JArray())
                {
                    var lo = lt as JObject;
                    if (lo == null) { Fail(s, CodeSchema, at + ".layers must contain objects."); return "x"; }
                    string lat = at + ".layers[" + names.Count + "]";
                    if (UnknownKey(s, lo, SectionLayerKeys, lat) != null) return "x";
                    var l = new StructuralSectionLayer { Name = lo.Value<string>("name"), Face = lo.Value<string>("face") };
                    if (Array.IndexOf(StructuralSectionLayer.Faces, l.Face) < 0)
                    {
                        Fail(s, CodeUnknownValue, lat + ".face must be one of " + string.Join(", ", StructuralSectionLayer.Faces) +
                             " - got " + Show(l.Face) + ".", lat + ".face");
                        return "x";
                    }
                    string key = l.Name ?? l.Face;
                    if (key.Contains("#") || !names.Add(key))
                    {
                        Fail(s, CodeDuplicateId, lat + " is named '" + key + "', which " + (key.Contains("#") ? "contains '#'" :
                             "another layer of this rule already uses") + ". Name the layers; two rows on one face need two names.", lat + ".name");
                        return "x";
                    }
                    JToken ct = lo["count"];
                    if (ct == null || ct.Type != JTokenType.Integer || ct.Value<int>() < 1)
                    { Fail(s, CodeMissing, lat + ".count must be a whole number of bars, one or more.", lat + ".count"); return "x"; }
                    l.Count = ct.Value<int>();
                    if (l.Face == StructuralSectionLayer.FaceRing && l.Count < 3)
                    { Fail(s, CodeUnknownValue, lat + " is a ring of " + l.Count + " bar(s); a ring is three or more.", lat + ".count"); return "x"; }
                    if (l.Face == StructuralSectionLayer.FaceRing && (lo["laps"] != null || lo["crank"] != null))
                    { Fail(s, CodeUnknownValue, lat + ": a ring is laid bar by bar; laps and cranks on a ring are not implemented.", lat); return "x"; }
                    l.BarTypeId = lo.Value<string>("bar_type");
                    if (string.IsNullOrWhiteSpace(l.BarTypeId) || !s.BarTypes.ContainsKey(l.BarTypeId))
                    {
                        Fail(s, CodeUnresolvedReference, lat + ".bar_type is " + Show(l.BarTypeId) + ", which no bar_types entry declares.", lat + ".bar_type");
                        return "x";
                    }
                    // INTO FIRST: whether the extent may leave the host depends on it.
                    if (ReadInto(s, lo["into"], l.Into, lat) != null) return "x";
                    if (ReadAnchorage(s, lo["anchorage"], l.Into, l.Anchorage, lat, true) != null) return "x";
                    l.ShapeName = lo.Value<string>("shape");
                    l.AllowNewShape = lo.Value<bool?>("allow_new_shape") ?? false;
                    if (l.AllowNewShape && !string.IsNullOrWhiteSpace(l.ShapeName))
                    { Fail(s, CodeUnknownValue, lat + " names a shape AND sets allow_new_shape.", lat); return "x"; }
                    JToken ex = lo["extend"];
                    if (ex == null)
                    {
                        Fail(s, CodeMissing, lat + " needs extend: host_length, clear_span, or { from_mm, to_mm } measured from the " +
                             "host's start face. How far a bar runs is a design decision.", lat + ".extend");
                        return "x";
                    }
                    if (ex.Type == JTokenType.String)
                    {
                        l.Extend = ex.Value<string>();
                        if (l.Extend != StructuralSectionLayer.ExtendHostLength && l.Extend != StructuralSectionLayer.ExtendClearSpan)
                        { Fail(s, CodeUnknownValue, lat + ".extend must be host_length, clear_span or { from_mm, to_mm }.", lat + ".extend"); return "x"; }
                    }
                    else if (ex is JObject eo)
                    {
                        if (UnknownKey(s, eo, SectionExtentKeys, lat + ".extend") != null) return "x";
                        if (eo["axis"] != null || eo["toward"] != null)
                        {
                            // FROM A NAMED AXIS (a schedule card's own reference): the plan finds both grids.
                            if (UnknownKey(s, eo, AxisExtentKeys, lat + ".extend") != null) return "x";
                            if (ReadAxisExtent(s, eo, lat + ".extend", out string axisName, out string towardName, out double af, out double at2) != null) return "x";
                            l.Extend = StructuralSectionLayer.ExtendAxes; l.Axis = axisName; l.Toward = towardName; l.FromMm = af; l.ToMm = at2;
                            goto extendRead;
                        }
                        if (eo["from"] != null)
                        {
                            // F2.2: { from: start | end, run_mm | run_ratio } - one run, never both.
                            string fromEnd = eo.Value<string>("from");
                            if (fromEnd != "start" && fromEnd != "end")
                            { Fail(s, CodeUnknownValue, lat + ".extend.from must be start or end.", lat + ".extend.from"); return "x"; }
                            if (eo["from_mm"] != null || eo["to_mm"] != null)
                            { Fail(s, CodeUnknownValue, lat + ".extend mixes from with from_mm/to_mm; use one form.", lat + ".extend"); return "x"; }
                            string r1, r2;
                            double? runMm = ReadNumber(eo["run_mm"], lat + ".extend.run_mm", out r1);
                            double? ratio = ReadNumber(eo["run_ratio"], lat + ".extend.run_ratio", out r2);
                            if (r1 != null || r2 != null) { Fail(s, CodeNotANumber, r1 ?? r2, lat + ".extend"); return "x"; }
                            if (runMm.HasValue == ratio.HasValue)
                            {
                                Fail(s, CodeMissing, lat + ".extend from " + fromEnd + " needs exactly one of run_mm or run_ratio " +
                                     "(0.25 = a quarter of the member's measured length).", lat + ".extend");
                                return "x";
                            }
                            if ((runMm.HasValue && runMm.Value <= 0) || (ratio.HasValue && (ratio.Value <= 0 || ratio.Value > 1)))
                            { Fail(s, CodeGeometry, lat + ".extend run must be positive (run_ratio at most 1).", lat + ".extend"); return "x"; }
                            l.Extend = StructuralSectionLayer.ExtendFromEnd; l.FromEnd = fromEnd; l.RunMm = runMm; l.RunRatio = ratio;
                            goto extendRead;
                        }
                        string e1, e2;
                        double? from = ReadNumber(eo["from_mm"], lat + ".extend.from_mm", out e1);
                        double? to = ReadNumber(eo["to_mm"], lat + ".extend.to_mm", out e2);
                        // BELOW THE START FACE only for a layer declared into another element: a
                        // dowel down into the footing. Past the END face is the expansion's check,
                        // because only the host's measured length says where that face is.
                        if (e1 != null || e2 != null || !from.HasValue || !to.HasValue || to.Value <= from.Value ||
                            (from.Value < 0 && l.Into.Count == 0))
                        {
                            Fail(s, CodeGeometry, e1 ?? e2 ?? (lat + ".extend needs from_mm and to_mm, with from_mm < to_mm, " +
                                 "and from_mm at or after the host's start face (0) unless the layer declares into: the " +
                                 "other elements its bars run into."), lat + ".extend");
                            return "x";
                        }
                        l.Extend = StructuralSectionLayer.ExtendExplicit; l.FromMm = from; l.ToMm = to;
                    }
                    else { Fail(s, CodeUnknownValue, lat + ".extend must be a word or an object.", lat + ".extend"); return "x"; }
                    extendRead:

                    if (UnknownKey(s, lo["start"] as JObject, TerminationKeys, lat + ".start") != null) return "x";
                    if (UnknownKey(s, lo["end"] as JObject, TerminationKeys, lat + ".end") != null) return "x";
                    string terr = ReadTermination(lo["start"] as JObject, l.Start, s, r.Id, "layers['" + key + "'].start");
                    if (terr != null) { Fail(s, CodeUnknownValue, terr, lat + ".start"); return "x"; }
                    terr = ReadTermination(lo["end"] as JObject, l.End, s, r.Id, "layers['" + key + "'].end");
                    if (terr != null) { Fail(s, CodeUnknownValue, terr, lat + ".end"); return "x"; }
                    l.Mark = lo.Value<string>("mark");
                    if (lo["laps"] != null && ReadLaps(s, lo["laps"], l, lat) != null) return "x";
                    if (lo["crank"] != null)
                    {
                        // A BAYONETA (L2): every number declared - where it starts, how long the slope
                        // runs, how far in it moves. Nothing here knows a code's 1:6.
                        var co2 = lo["crank"] as JObject;
                        if (co2 == null) { Fail(s, CodeSchema, lat + ".crank must be an object.", lat + ".crank"); return "x"; }
                        if (UnknownKey(s, co2, CrankKeys, lat + ".crank") != null) return "x";
                        var cv = new double[3];
                        string[] ck = { "at_mm", "run_mm", "inward_mm" };
                        for (int i = 0; i < 3; i++)
                        {
                            if (co2[ck[i]] == null)
                            { Fail(s, CodeMissing, lat + ".crank needs at_mm, run_mm and inward_mm.", lat + ".crank." + ck[i]); return "x"; }
                            string cerr;
                            double? cvv = ReadNumber(co2[ck[i]], lat + ".crank." + ck[i], out cerr);
                            if (cerr != null || !cvv.HasValue || cvv.Value <= 0)
                            { Fail(s, CodeGeometry, cerr ?? lat + ".crank." + ck[i] + " must be greater than zero.", lat + ".crank." + ck[i]); return "x"; }
                            cv[i] = cvv.Value;
                        }
                        if (l.Face == StructuralSectionLayer.FacePerimeter)
                        { Fail(s, CodeUnknownValue, lat + ": a crank moves a layer toward the inside of ITS face; the perimeter is four faces. Declare the faces.", lat + ".crank"); return "x"; }
                        l.Crank = new StructuralCrank { AtMm = cv[0], RunMm = cv[1], InwardMm = cv[2] };
                    }
                    // A LAP ON AN AXIS LAYER is a station from that same axis (at_mm); a share of the host's
                    // length is not one, and a crank is a station from the host's start face - not mixed.
                    if (l.Extend == StructuralSectionLayer.ExtendAxes && (l.Crank != null || l.Laps.Any(q => q.AtRatio.HasValue)))
                    {
                        Fail(s, CodeUnknownValue, lat + " runs from axis '" + l.Axis + "' and declares " + (l.Crank != null ? "a crank, a station from the host's start face" :
                             "a lap at_ratio, a share of the host's length") + ". On a layer from an axis, a lap is at_mm from that axis; a crank is declared with from_mm/to_mm.", lat);
                        return "x";
                    }
                    if (lo["between"] != null)
                    {
                        // BETWEEN THE BARS OF AN EARLIER LAYER: one bar in each of its gaps, on its face.
                        l.Between = lo["between"].Type == JTokenType.String ? (string)lo["between"] : null;
                        StructuralSectionLayer held = l.Between == null ? null : r.Layers.FirstOrDefault(q => (q.Name ?? q.Face) == l.Between);
                        if (held == null)
                        {
                            Fail(s, CodeUnresolvedReference, lat + ".between is " + Show(l.Between) + ", which names no earlier layer of this rule. " +
                                 "A layer laid between bars names the layer whose gaps it fills, declared before it.", lat + ".between");
                            return "x";
                        }
                        if ((l.Face != StructuralSectionLayer.FaceTop && l.Face != StructuralSectionLayer.FaceBottom) || held.Face != l.Face)
                        {
                            Fail(s, CodeUnknownValue, lat + " is on the " + l.Face + " face and is laid between '" + l.Between + "' on the " + held.Face +
                                 " face. Between is a top or bottom layer in the gaps of a layer on that same face.", lat + ".between");
                            return "x";
                        }
                        if (l.Laps.Count > 0 || l.Crank != null || held.Laps.Count > 0 || held.Crank != null)
                        {
                            Fail(s, CodeUnknownValue, lat + " is laid between '" + l.Between + "' and one of them is lapped or cranked: " +
                                 "a lapped piece moves across, and what then lies in which gap is a placement this build does not lay.", lat + ".between");
                            return "x";
                        }
                    }
                    r.Layers.Add(l);
                }

                // THE STIRRUPS: read as a stirrup zone rule, so zones, cover, offsets and
                // hooks follow the same rules everywhere. The profile and the run are the
                // host's, measured at expansion; the placeholders here are never built.
                if (o["stirrups"] != null)
                {
                    var so = o["stirrups"] as JObject;
                    if (so == null) { Fail(s, CodeSchema, at + ".stirrups must be an object.", at + ".stirrups"); return "x"; }
                    if (UnknownKey(s, so, SectionStirrupKeys, at + ".stirrups") != null) return "x";
                    var zo = (JObject)so.DeepClone();
                    // THE EXTENT FROM A NAMED AXIS is read on its own: the zone rule's offsets are the
                    // resolver's to set once it has found the grids. Not both.
                    JToken extentTok = zo["extent"];
                    zo.Remove("extent");
                    if (extentTok != null)
                    {
                        var xo = extentTok as JObject;
                        if (xo == null) { Fail(s, CodeSchema, at + ".stirrups.extent must be { axis, toward, from_mm, to_mm }.", at + ".stirrups.extent"); return "x"; }
                        if (UnknownKey(s, xo, AxisExtentKeys, at + ".stirrups.extent") != null) return "x";
                        if (zo["start_offset_mm"] != null || zo["end_offset_mm"] != null)
                        { Fail(s, CodeUnknownValue, at + ".stirrups declares extent AND start/end offsets; the extent places the zones, so declare one.", at + ".stirrups"); return "x"; }
                        if (ReadAxisExtent(s, xo, at + ".stirrups.extent", out string sa, out string stw, out double sf, out double st2) != null) return "x";
                        r.StirrupsAxis = sa; r.StirrupsToward = stw; r.StirrupsFromMm = sf; r.StirrupsToMm = st2;
                    }
                    // THE PLACEMENT (L7) is read on its own: it is not a zone rule key.
                    JToken placementTok = zo["shape_placement"];
                    zo.Remove("shape_placement");
                    zo["id"] = r.Id + "#stirrups";
                    zo["host"] = o["host"].DeepClone();
                    zo["style"] = StructuralStyle.StirrupTie;
                    zo["profile_mm"] = new JArray(new JArray(0, 0, 0), new JArray(1, 0, 0), new JArray(1, 1, 0), new JArray(0, 1, 0));
                    zo["closed"] = true;
                    zo["along"] = new JArray(1, 0, 0);
                    zo["span_mm"] = 1.0;
                    zo["required"] = o.Value<bool?>("required") ?? true;
                    StructuralStirrupZoneRule z;
                    if (ReadOneZoneRule(zo, s, ruleIds, collection, out z) != null) return "x";
                    if (placementTok != null && ReadPlacement(s, placementTok, so, z, at + ".stirrups") != null) return "x";
                    r.Stirrups = z;
                }

                // CROSSTIES (L2): they follow the stirrups' zones, so a rule without stirrups has none.
                if (o["ties"] != null)
                {
                    var ta = o["ties"] as JArray;
                    if (ta == null) { Fail(s, CodeSchema, at + ".ties must be an array.", at + ".ties"); return "x"; }
                    if (r.Stirrups == null)
                    { Fail(s, CodeMissing, at + " declares ties and no stirrups. Ties are laid at the stirrups' stations, beside each stirrup.", at + ".ties"); return "x"; }
                    var tieNames = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < ta.Count; i++)
                    {
                        string tat = at + ".ties[" + i + "]";
                        var to = ta[i] as JObject;
                        if (to == null) { Fail(s, CodeSchema, tat + " must be an object.", tat); return "x"; }
                        if (UnknownKey(s, to, SectionTieKeys, tat) != null) return "x";
                        var tie = new StructuralSectionTie
                        {
                            Name = to.Value<string>("name"), BarTypeId = to.Value<string>("bar_type"), LayerName = to.Value<string>("layer"),
                            ShapeName = to.Value<string>("shape"), AllowNewShape = to.Value<bool?>("allow_new_shape") ?? false,
                            Mark = to.Value<string>("mark")
                        };
                        if (string.IsNullOrWhiteSpace(tie.Name) || tie.Name.Contains("#") || !tieNames.Add(tie.Name))
                        { Fail(s, CodeDuplicateId, tat + ".name is required, unique in the rule and without '#'.", tat + ".name"); return "x"; }
                        if (string.IsNullOrWhiteSpace(tie.BarTypeId) || !s.BarTypes.ContainsKey(tie.BarTypeId))
                        { Fail(s, CodeUnresolvedReference, tat + ".bar_type is " + Show(tie.BarTypeId) + ", which no bar_types entry declares.", tat + ".bar_type"); return "x"; }
                        StructuralSectionLayer tl = r.Layers.FirstOrDefault(x => (x.Name ?? x.Face) == tie.LayerName);
                        if (tl == null)
                        { Fail(s, CodeUnresolvedReference, tat + ".layer is " + Show(tie.LayerName) + ", which is not a layer of this rule.", tat + ".layer"); return "x"; }
                        if (tl.Face == StructuralSectionLayer.FacePerimeter)
                        { Fail(s, CodeUnknownValue, tat + ".layer '" + tie.LayerName + "' is the perimeter: four corner bars, none of them intermediate. Tie a top, bottom, left or right layer.", tat + ".layer"); return "x"; }
                        if (UnknownKey(s, to["start"] as JObject, TerminationKeys, tat + ".start") != null) return "x";
                        if (UnknownKey(s, to["end"] as JObject, TerminationKeys, tat + ".end") != null) return "x";
                        string terr2 = ReadTermination(to["start"] as JObject, tie.Start, s, r.Id, "ties['" + tie.Name + "'].start");
                        if (terr2 != null) { Fail(s, CodeUnknownValue, terr2, tat + ".start"); return "x"; }
                        terr2 = ReadTermination(to["end"] as JObject, tie.End, s, r.Id, "ties['" + tie.Name + "'].end");
                        if (terr2 != null) { Fail(s, CodeUnknownValue, terr2, tat + ".end"); return "x"; }
                        if (tie.AllowNewShape && !string.IsNullOrWhiteSpace(tie.ShapeName))
                        { Fail(s, CodeUnknownValue, tat + " names a shape AND sets allow_new_shape.", tat); return "x"; }
                        r.Ties.Add(tie);
                    }
                }

                r.Mark = o.Value<string>("mark");
                r.Required = o.Value<bool?>("required") ?? true;
            }
            return null;
        }

        /// <summary>
        /// wall_boundary_rules (acero 3.0, L5): the boundary element at one end of ONE wall - a
        /// section rule over the wall's end region, plus which end and how long. Returns null on
        /// success; the failure is recorded on `s`. Whether the region fits the wall is the
        /// expansion's question, because only the wall's measured length answers it.
        /// </summary>
        private static string ReadWallBoundaryRules(JObject doc, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["wall_boundary_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in wall_boundary_rules must be an object."); return "x"; }
                StructuralSectionRule section;
                if (ReadOneSectionRule(o, s, ruleIds, "wall_boundary_rules", WallBoundaryRuleKeys, s.WallBoundaryRules.Count, out section) != null)
                    return "x";
                var b = new StructuralWallBoundaryRule { Id = section.Id, Section = section, Raw = o };
                string at = "wall_boundary_rules['" + b.Id + "']";

                // ONE WALL, BY ITS ID. The region is measured from that wall's ends, and a
                // selector that could match several walls - or another wall tomorrow - would
                // put the same cage at the end of each.
                StructuralHostSelector h = section.Host;
                if (h.ElementIds.Count != 1 || h.Category != null || h.TypeName != null || h.Mark != null || h.Level != null)
                {
                    Fail(s, CodeUnknownValue, at + ".host must be { element_ids: [ one wall ] } and nothing else: a boundary element " +
                         "is measured from ONE wall's end. Write one rule per wall end.", at + ".host.element_ids");
                    return "x";
                }

                if (o["end"] == null)
                {
                    Fail(s, CodeMissing, at + " needs end: start or end - the end of the wall's location line the boundary " +
                         "element is at.", at + ".end");
                    return "x";
                }
                b.End = o["end"].Type == JTokenType.String ? o.Value<string>("end") : null;
                if (Array.IndexOf(StructuralWallBoundaryRule.Ends, b.End) < 0)
                {
                    Fail(s, CodeUnknownValue, at + ".end must be start or end - got " + o["end"].ToString(Newtonsoft.Json.Formatting.None) + ".", at + ".end");
                    return "x";
                }

                if (o["length_mm"] == null)
                {
                    Fail(s, CodeMissing, at + " needs length_mm: how far the boundary element runs along the wall from its " +
                         "end. It is declared, never derived from the wall.", at + ".length_mm");
                    return "x";
                }
                string lerr;
                double? len = ReadNumber(o["length_mm"], at + ".length_mm", out lerr);
                if (lerr != null || !len.HasValue) { Fail(s, CodeNotANumber, lerr ?? at + ".length_mm must be a number.", at + ".length_mm"); return "x"; }
                if (len.Value <= 0)
                {
                    Fail(s, CodeGeometry, at + ".length_mm must be greater than zero - got " +
                         len.Value.ToString(CultureInfo.InvariantCulture) + ".", at + ".length_mm");
                    return "x";
                }
                b.LengthMm = len.Value;
                s.WallBoundaryRules.Add(b);
            }
            return null;
        }

        /// <summary>mat_rules. Returns null on success; the failure is recorded on `s`.</summary>
        private static string ReadMatRules(JObject doc, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["mat_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in mat_rules must be an object."); return "x"; }
                var m = new StructuralMatRule { Id = o.Value<string>("id"), Raw = o };
                string at = "mat_rules['" + m.Id + "']";
                if (UnknownKey(s, o, MatRuleKeys, "mat_rules[" + s.MatRules.Count + "]") != null) return "x";
                if (string.IsNullOrWhiteSpace(m.Id)) { Fail(s, CodeMissing, "every mat rule needs an id."); return "x"; }
                if (!ruleIds.Add(m.Id))
                {
                    Fail(s, CodeDuplicateId,
                        "'" + m.Id + "' is used by more than one rule. A mat rule expands into one " +
                        "reinforcement rule per component, named <id>#<component>, so its id shares the namespace.");
                    return "x";
                }

                if (UnknownKey(s, o["host"] as JObject, HostKeys, at + ".host") != null) return "x";
                string err = ReadSelector(o["host"] as JObject, m.Host, at + ".host");
                if (err != null) { Fail(s, CodeMissing, err, at + ".host"); return "x"; }

                string nerr = ReadDirection(o["face_normal"] as JArray, at + ".face_normal", out m.FaceNormalMm);
                if (nerr != null)
                {
                    Fail(s, CodeGeometry, nerr +
                        " It points OUT of the face the mat sits under - [0,0,1] for the top of a slab, " +
                        "[0,0,-1] for the bottom, and the wall's own normal for a wall. It is declared because " +
                        "a slab has two faces and the geometry does not say which one was meant.");
                    return "x";
                }

                m.Mark = o.Value<string>("mark");
                m.Required = o.Value<bool?>("required") ?? true;
                // INTO (acero 3.0): the other elements a followed face continues into - ids only, never the host.
                if (ReadInto(s, o["into"], m.Into, at) != null) return "x";
                foreach (long iid in m.Into)
                    if (m.Host.ElementIds.Contains(iid))
                    {
                        Fail(s, CodeUnknownValue, at + ".into names element " + iid + ", which is the mat's own host. into lists " +
                             "the OTHER elements the face continues into; the host is always part of it.", at + ".into");
                        return "x";
                    }

                // THE OPENINGS BLOCK. Three declared numbers and words, none of
                // them defaulted: which policy, below what size an opening is
                // ignored, and how far a trimmed bar stops from the edge. A value a
                // policy would not use is refused, on the same principle as a layout
                // that names a spacing it does not use.
                JToken opTok = o["openings"];
                if (opTok != null && opTok.Type != JTokenType.Null)
                {
                    var oo = opTok as JObject;
                    if (oo == null)
                    {
                        Fail(s, CodeSchema, at + ".openings must be an object: { policy, minimum_size_mm, clearance_mm }.", at + ".openings");
                        return "x";
                    }
                    if (UnknownKey(s, oo, MatOpeningsKeys, at + ".openings") != null) return "x";
                    var op = new StructuralMatOpenings { Policy = (oo.Value<string>("policy") ?? "").Trim() };
                    if (Array.IndexOf(StructuralMatOpenings.Policies, op.Policy) < 0)
                    {
                        Fail(s, CodeUnknownValue,
                            at + ".openings.policy must be " + string.Join(", ", StructuralMatOpenings.Policies) +
                            " - got " + Show(oo.Value<string>("policy")) + ". omit drops every bar that would " +
                            "cross an opening; trim stops the bar short of it on each side; ignore builds the " +
                            "bars as declared and reports the crossings.", at + ".openings.policy");
                        return "x";
                    }
                    string oerr;
                    double? min = ReadNumber(oo["minimum_size_mm"], at + ".openings.minimum_size_mm", out oerr);
                    if (oerr != null) { Fail(s, CodeNotANumber, oerr, at + ".openings.minimum_size_mm"); return "x"; }
                    if (!min.HasValue)
                    {
                        Fail(s, CodeMissing,
                            at + ".openings needs minimum_size_mm: openings whose largest dimension is below it " +
                            "are ignored. It is declared, not defaulted - a sleeve a bar may run past and a " +
                            "shaft it may not are the same shape at different sizes, and where the line sits " +
                            "is a decision.", at + ".openings.minimum_size_mm");
                        return "x";
                    }
                    if (min.Value < 0)
                    {
                        Fail(s, CodeUnknownValue, at + ".openings.minimum_size_mm must be zero or more.", at + ".openings.minimum_size_mm");
                        return "x";
                    }
                    op.MinimumSizeMm = min.Value;
                    double? clear = ReadNumber(oo["clearance_mm"], at + ".openings.clearance_mm", out oerr);
                    if (oerr != null) { Fail(s, CodeNotANumber, oerr, at + ".openings.clearance_mm"); return "x"; }
                    if (op.Policy == StructuralMatOpenings.PolicyTrim)
                    {
                        if (!clear.HasValue)
                        {
                            Fail(s, CodeMissing,
                                at + ".openings declares policy: trim and no clearance_mm - how far short of the " +
                                "opening a trimmed bar stops. Nothing here supplies one.", at + ".openings.clearance_mm");
                            return "x";
                        }
                        if (clear.Value < 0)
                        {
                            Fail(s, CodeUnknownValue, at + ".openings.clearance_mm must be zero or more.", at + ".openings.clearance_mm");
                            return "x";
                        }
                        op.ClearanceMm = clear;
                    }
                    else if (clear.HasValue)
                    {
                        Fail(s, CodeUnknownValue,
                            at + ".openings declares policy: " + op.Policy + " and a clearance_mm beside it. Only " +
                            "trim stops a bar short of an opening, so the number would be ignored - and somebody " +
                            "wrote it meaning something.", at + ".openings.clearance_mm");
                        return "x";
                    }
                    m.Openings = op;
                }

                var comps = o["components"] as JArray;
                if (comps == null || comps.Count == 0)
                {
                    Fail(s, CodeMissing,
                        at + " needs components: a list of {name, direction, bar_type, offset_from_face_mm, " +
                        "layout}. Nothing here invents a mat.");
                    return "x";
                }
                foreach (JToken ct in comps)
                {
                    var co = ct as JObject;
                    if (co == null) { Fail(s, CodeSchema, at + ".components must contain objects."); return "x"; }
                    string cpath = at + ".components[" + m.Components.Count + "]";
                    if (UnknownKey(s, co, MatComponentKeys, cpath) != null) return "x";
                    if (UnknownKey(s, co["layout"] as JObject, LayoutKeys, cpath + ".layout") != null) return "x";
                    var c = new MatComponentRequest
                    {
                        Name = co.Value<string>("name"),
                        Mark = co.Value<string>("mark"),
                        ShapeName = co.Value<string>("shape"),
                        AllowNewShape = co.Value<bool?>("allow_new_shape") ?? false
                    };
                    string cat = at + ".components['" + (c.Name ?? "?") + "']";

                    c.BarTypeId = co.Value<string>("bar_type");
                    if (string.IsNullOrWhiteSpace(c.BarTypeId)) { Fail(s, CodeMissing, cat + " needs bar_type."); return "x"; }
                    if (!s.BarTypes.ContainsKey(c.BarTypeId))
                    {
                        Fail(s, CodeUnresolvedReference,
                            cat + ".bar_type is '" + c.BarTypeId + "', which no bar_types entry declares.");
                        return "x";
                    }

                    string derr = ReadDirection(co["direction"] as JArray, cat + ".direction", out c.DirectionMm);
                    if (derr != null) { Fail(s, CodeGeometry, derr); return "x"; }

                    JToken off = co["offset_from_face_mm"];
                    if (off == null || off.Type == JTokenType.Null)
                    {
                        Fail(s, CodeMissing,
                            cat + " needs offset_from_face_mm: how deep this layer's centreline sits under the " +
                            "face. It is declared rather than derived from a cover, because the second layer of " +
                            "a mat sits under the first by an amount that is a decision.");
                        return "x";
                    }
                    c.OffsetFromFaceMm = ReadNonNegative(off, double.NaN);
                    c.EndCoverMm = ReadNonNegative(co["end_cover_mm"], 0);
                    c.SideCoverMm = ReadNonNegative(co["side_cover_mm"], 0);
                    if (double.IsNaN(c.OffsetFromFaceMm) || double.IsNaN(c.EndCoverMm) || double.IsNaN(c.SideCoverMm))
                    {
                        Fail(s, CodeNotFinite, cat + " has an offset or cover that is not a finite distance of zero or more.");
                        return "x";
                    }

                    // A WINDOW (L4): where the bars run (run, along them) and where the array marches
                    // (spread, across them), in mm from the host's own extents - top bars over a support.
                    foreach (string wk in new[] { "run", "spread" })
                    {
                        if (co[wk] == null) continue;
                        var wo = co[wk] as JObject;
                        if (wo == null) { Fail(s, CodeSchema, cat + "." + wk + " must be an object { from_mm, to_mm }.", cat + "." + wk); return "x"; }
                        if (UnknownKey(s, wo, MatWindowKeys, cat + "." + wk) != null) return "x";
                        string w1, w2;
                        double? wf = ReadNumber(wo["from_mm"], cat + "." + wk + ".from_mm", out w1);
                        double? wt = ReadNumber(wo["to_mm"], cat + "." + wk + ".to_mm", out w2);
                        if (w1 != null || w2 != null || !wf.HasValue || !wt.HasValue || wf.Value < 0 || wt.Value <= wf.Value)
                        { Fail(s, CodeGeometry, w1 ?? w2 ?? (cat + "." + wk + " needs from_mm and to_mm, 0 <= from_mm < to_mm."), cat + "." + wk); return "x"; }
                        if (wk == "run") { c.RunFromMm = wf; c.RunToMm = wt; } else { c.SpreadFromMm = wf; c.SpreadToMm = wt; }
                    }

                    // A FACE THE BARS FOLLOW (acero 3.0): each bar laid on the face's own section, offset along
                    // each facet's normal, bent only where the face really breaks. Both numbers are declared:
                    // how far from one plane a facet may stray and still be one, and how short a facet under a
                    // bar may be before following it would bend the bar at a tessellation rather than a break.
                    JToken fft = co["follow_face"];
                    if (fft != null && fft.Type != JTokenType.Null)
                    {
                        var ffo = fft as JObject;
                        if (ffo == null)
                        {
                            Fail(s, CodeSchema, cpath + ".follow_face must be an object: { planarity_mm, min_facet_mm }.", cpath + ".follow_face");
                            return "x";
                        }
                        if (UnknownKey(s, ffo, FollowFaceKeys, cpath + ".follow_face") != null) return "x";
                        var ff = new StructuralFollowFace();
                        foreach (string fk in new[] { "planarity_mm", "min_facet_mm" })
                        {
                            string ferr;
                            double? fv = ReadNumber(ffo[fk], cpath + ".follow_face." + fk, out ferr);
                            if (ferr != null) { Fail(s, CodeNotANumber, ferr, cpath + ".follow_face." + fk); return "x"; }
                            if (!fv.HasValue)
                            {
                                Fail(s, CodeMissing, cpath + ".follow_face needs " + fk + (fk == "planarity_mm"
                                    ? ": how far, in mm, a facet's triangles may stray from one plane and still be one facet. " +
                                      "It is the tolerance the face is treated as planar within, so it is declared, never defaulted."
                                    : ": the shortest stretch of a facet, in mm, a bar may be bent onto. A curved face arrives as " +
                                      "many short facets, and bending a bar at each of them follows the tessellation, not the face."),
                                     cpath + ".follow_face." + fk);
                                return "x";
                            }
                            if (!(fv.Value > 0))
                            {
                                Fail(s, CodeUnknownValue, cpath + ".follow_face." + fk + " must be greater than zero.", cpath + ".follow_face." + fk);
                                return "x";
                            }
                            if (fk == "planarity_mm") ff.PlanarityMm = fv.Value; else ff.MinFacetMm = fv.Value;
                        }
                        c.FollowFace = ff;
                    }

                    var holder = new StructuralRebarRule { Id = m.Id, BarTypeId = c.BarTypeId };
                    string lerr = ReadLayout(co["layout"] as JObject, holder, s, false, cat);
                    if (lerr != null) { Fail(s, CodeLayout, lerr); return "x"; }
                    c.Layout = holder.Layout;

                    // HOOKS AT THE BARS' ENDS (acero 3.0, L5): a wall's horizontals returned at the
                    // wall's ends. Declared as on any rule - a hook type and an orientation - and
                    // carried only to the ends at the host's edges, never to an end an opening cut.
                    foreach (string e in new[] { "start_hook", "end_hook" })
                    {
                        JToken et = co[e];
                        if (et == null || et.Type == JTokenType.Null) continue;
                        var eo = et as JObject;
                        if (eo == null) { Fail(s, CodeSchema, cpath + "." + e + " must be an object: { hook_type, orientation }.", cpath + "." + e); return "x"; }
                        if (UnknownKey(s, eo, TerminationKeys, cpath + "." + e) != null) return "x";
                        string hid = eo.Value<string>("hook_type");
                        if (hid != null && !s.HookTypes.ContainsKey(hid))
                        {
                            Fail(s, CodeUnresolvedReference, cpath + "." + e + ".hook_type is '" + hid + "', which no hook_types entry declares.",
                                 cpath + "." + e + ".hook_type");
                            return "x";
                        }
                        string terr = ReadTermination(eo, e == "start_hook" ? c.Start : c.End, s, m.Id, "components['" + (c.Name ?? "?") + "']." + e);
                        if (terr != null) { Fail(s, CodeUnknownValue, terr, cpath + "." + e + ".orientation"); return "x"; }
                    }

                    string expandedId = m.Id + "#" + (c.Name ?? ("component" + (m.Components.Count + 1)));
                    if (!ruleIds.Add(expandedId))
                    {
                        Fail(s, CodeDuplicateId,
                            "'" + expandedId + "' is used by more than one rule. " + at + " expands into it.");
                        return "x";
                    }
                    m.Components.Add(c);
                }
                s.MatRules.Add(m);
            }
            return null;
        }

        /// <summary>
        /// The first key of `o` that `known` does not admit, or null. When one is found the
        /// set is failed with the exact path, the admitted keys and the closest matches, and
        /// the failed set is returned so the caller can `return`.
        /// </summary>
        /// <summary>
        /// clearances (acero 3.0, overlapping_bar): the minimum CLEAR distance between the surfaces of bars of
        /// different rules, declared - no code value is compiled in. Absent, only bars that pass through
        /// each other are reported. Read after every rule, because a pair names declared rule ids.
        /// </summary>
        private static string ReadClearances(JObject doc, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            JToken token = doc["clearances"];
            if (token == null || token.Type == JTokenType.Null) return null;
            var o = token as JObject;
            if (o == null) { Fail(s, CodeSchema, "clearances must be an object.", "clearances"); return "x"; }
            if (UnknownKey(s, o, ClearanceKeys, "clearances") != null) return "x";
            double v;
            string err = ClearanceNumber(s, o["between_rules_mm"], "clearances.between_rules_mm", out v);
            if (err == "x") return err;
            if (err == null) s.Clearances.BetweenRulesMm = v;
            err = ClearanceNumber(s, o["crossing_mm"], "clearances.crossing_mm", out v);
            if (err == "x") return err;
            if (err == null) { s.Clearances.CrossingMm = v; s.Clearances.CrossingDeclared = true; }

            JToken pairsToken = o["pairs"];
            if (pairsToken == null || pairsToken.Type == JTokenType.Null) return null;
            var pairs = pairsToken as JArray;
            if (pairs == null) { Fail(s, CodeSchema, "clearances.pairs must be an array.", "clearances.pairs"); return "x"; }
            for (int i = 0; i < pairs.Count; i++)
            {
                string at = "clearances.pairs[" + i + "]";
                var po = pairs[i] as JObject;
                if (po == null) { Fail(s, CodeSchema, at + " must be an object.", at); return "x"; }
                if (UnknownKey(s, po, ClearancePairKeys, at) != null) return "x";
                var rules = po["rules"] as JArray;
                if (rules == null || rules.Count != 2 || rules.Any(r => r.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)r)))
                {
                    Fail(s, CodeUnknownValue, at + ".rules must name exactly two declared rule ids (the same id twice means two " +
                         "sets of that one rule).", at + ".rules");
                    return "x";
                }
                foreach (JToken r in rules)
                    if (!ruleIds.Contains((string)r))
                    {
                        Fail(s, CodeUnresolvedReference, at + ".rules names '" + (string)r + "', which is not the id of a rule in " +
                             "this set (reinforcement_rules, stirrup_zone_rules, mat_rules, section_rules or wall_boundary_rules). " +
                             "A pair for a rule nobody declared would never apply, and nothing would say so.", at + ".rules");
                        return "x";
                    }
                if (po["min_clear_mm"] == null || po["min_clear_mm"].Type == JTokenType.Null)
                {
                    Fail(s, CodeMissing, at + ".min_clear_mm is required: the clear distance between the surfaces of those " +
                         "two rules' bars where they run alongside each other.", at + ".min_clear_mm");
                    return "x";
                }
                err = ClearanceNumber(s, po["min_clear_mm"], at + ".min_clear_mm", out v);
                if (err == "x") return err;
                s.Clearances.Pairs.Add(new StructuralClearancePair { RuleA = (string)rules[0], RuleB = (string)rules[1], MinClearMm = v });
            }
            return null;
        }

        /// <summary>
        /// crossing_order (etapa 1c): which of two clashing sets moves, which way and how far - declared, never
        /// compiled in (see RebarCrossing). Read after every rule, because its refs name declared rule ids: the part
        /// of a ref before its first '#' must match one (`*` and `?` allowed), or the set is refused. With
        /// `ruleIds` null (ParseCrossingOrder), refs are not resolved.
        /// </summary>
        private static string ReadCrossingOrder(JToken token, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            const string root = "crossing_order";
            if (token == null || token.Type == JTokenType.Null) return null;
            var o = token as JObject;
            if (o == null) { Fail(s, CodeSchema, "crossing_order must be an object: { offset, orders }.", root); return "x"; }
            if (UnknownKey(s, o, CrossingOrderKeys, root) != null) return "x";
            var order = new StructuralCrossingOrder();
            if (o["offset"] == null || o["offset"].Type == JTokenType.Null)
            {
                Fail(s, CodeMissing, "crossing_order.offset is required: how far a moving set goes - 'diameter' (its own model diameter), " +
                     "'contact' (the two bars' radii: they end touching) or { \"mm\": n }. Nothing is assumed.", root + ".offset");
                return "x";
            }
            order.Offset = ReadCrossingOffset(o["offset"], s, root + ".offset");
            if (order.Offset == null) return "x";

            JToken orders = o["orders"];
            if (orders == null || orders.Type == JTokenType.Null || (orders is JArray emptyArr && emptyArr.Count == 0))
            {
                Fail(s, CodeMissing, "crossing_order.orders is required and may not be empty: an order that names nothing moves nothing.", root + ".orders");
                return "x";
            }
            var arr = orders as JArray;
            if (arr == null) { Fail(s, CodeSchema, "crossing_order.orders must be an array.", root + ".orders"); return "x"; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < arr.Count; i++)
            {
                string idx = root + ".orders[" + i + "]";
                var eo = arr[i] as JObject;
                if (eo == null) { Fail(s, CodeSchema, idx + " must be an object.", idx); return "x"; }
                if (UnknownKey(s, eo, CrossingEntryKeys, idx) != null) return "x";
                string id = eo["id"] != null && eo["id"].Type == JTokenType.String ? (string)eo["id"] : null;
                if (string.IsNullOrWhiteSpace(id)) { Fail(s, CodeMissing, idx + ".id is required.", idx + ".id"); return "x"; }
                string at = root + ".orders['" + id + "']";
                if (!ids.Add(id)) { Fail(s, CodeDuplicateId, "two crossing orders share the id '" + id + "'.", at + ".id"); return "x"; }
                var e = new CrossingOrderEntry { Id = id };
                foreach (string side in new[] { "stays", "moves" })
                {
                    JToken st = eo[side];
                    if (st == null || st.Type == JTokenType.Null || (st is JArray sa0 && sa0.Count == 0))
                    {
                        Fail(s, CodeMissing, at + "." + side + " is required and may not be empty: the sets that " +
                             (side == "stays" ? "keep their declared place" : "give way") + ", by rule id.", at + "." + side);
                        return "x";
                    }
                    var sa = st as JArray;
                    if (sa == null || sa.Any(r => r.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)r)))
                    {
                        Fail(s, CodeUnknownValue, at + "." + side + " must be an array of rule ids (a declared id, or a row's id after its '#'; " +
                             "'*' and '?' allowed).", at + "." + side);
                        return "x";
                    }
                    foreach (JToken r in sa)
                    {
                        string rf = (string)r;
                        if (ruleIds != null && !RebarCrossing.RefDeclared(rf, ruleIds))
                        {
                            Fail(s, CodeUnresolvedReference, at + "." + side + " names '" + rf + "', which names no rule this set declares " +
                                 "(the part before its first '#' must match the id of a reinforcement, stirrup zone, mat, section or wall " +
                                 "boundary rule). An order for a rule nobody declared would never apply, and nothing would say so.", at + "." + side);
                            return "x";
                        }
                        (side == "stays" ? e.Stays : e.Moves).Add(rf);
                    }
                }
                string both = e.Moves.FirstOrDefault(m => e.Stays.Contains(m));
                if (both != null)
                {
                    Fail(s, CodeUnknownValue, at + " names '" + both + "' both in stays and in moves: whether it gives way cannot be read.", at + ".moves");
                    return "x";
                }
                JToken mv = eo["move"];
                if (mv == null || mv.Type == JTokenType.Null)
                {
                    Fail(s, CodeMissing, at + ".move is required: 'translate' (the whole set) or 'legs' (its end legs, inward along the run).", at + ".move");
                    return "x";
                }
                e.Move = mv.Type == JTokenType.String ? (string)mv : null;
                if (!RebarCrossing.Modes.Contains(e.Move))
                {
                    Fail(s, CodeUnknownValue, at + ".move must be 'translate' or 'legs'.", at + ".move");
                    return "x";
                }
                JToken dir = eo["direction"], legs = eo["legs"];
                bool hasDir = dir != null && dir.Type != JTokenType.Null, hasLegs = legs != null && legs.Type != JTokenType.Null;
                if (e.Move == RebarCrossing.ModeTranslate)
                {
                    if (hasLegs)
                    {
                        Fail(s, CodeUnknownValue, at + ".legs belongs to move 'legs'; a translation moves the whole set.", at + ".legs");
                        return "x";
                    }
                    if (!hasDir)
                    {
                        Fail(s, CodeMissing, at + ".direction is required for a translation: a vector [x, y, z], or 'toward_host_core' (the " +
                             "common perpendicular of the two bars, to the side of the moving set's host centre).", at + ".direction");
                        return "x";
                    }
                    if (dir.Type == JTokenType.String)
                    {
                        if ((string)dir != RebarCrossing.TowardHostCore)
                        {
                            Fail(s, CodeUnknownValue, at + ".direction must be a vector [x, y, z] or 'toward_host_core'.", at + ".direction");
                            return "x";
                        }
                        e.TowardHostCore = true;
                    }
                    else
                    {
                        var da = dir as JArray;
                        double[] v = null;
                        if (da != null && da.Count == 3 && da.All(x => x.Type == JTokenType.Integer || x.Type == JTokenType.Float))
                            v = da.Select(x => x.Value<double>()).ToArray();
                        if (v == null || v.Any(x => !RebarLayoutRules.IsFinite(x)) || Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) < 1e-9)
                        {
                            Fail(s, CodeGeometry, at + ".direction must be three finite numbers that are not all zero.", at + ".direction");
                            return "x";
                        }
                        e.Direction = v;
                    }
                }
                else
                {
                    if (hasDir)
                    {
                        Fail(s, CodeUnknownValue, at + ".direction belongs to move 'translate'; legs move inward along the run.", at + ".direction");
                        return "x";
                    }
                    if (!hasLegs)
                    {
                        Fail(s, CodeMissing, at + ".legs is required for move 'legs': 'both', 'start' or 'end'.", at + ".legs");
                        return "x";
                    }
                    e.Legs = legs.Type == JTokenType.String ? (string)legs : null;
                    if (!RebarCrossing.LegChoices.Contains(e.Legs))
                    {
                        Fail(s, CodeUnknownValue, at + ".legs must be 'both', 'start' or 'end'.", at + ".legs");
                        return "x";
                    }
                }
                if (eo["offset"] != null && eo["offset"].Type != JTokenType.Null)
                {
                    e.Offset = ReadCrossingOffset(eo["offset"], s, at + ".offset");
                    if (e.Offset == null) return "x";
                }
                order.Orders.Add(e);
            }
            s.CrossingOrder = order;
            return null;
        }

        private static CrossingOffset ReadCrossingOffset(JToken t, StructuralRequirementSet s, string path)
        {
            if (t.Type == JTokenType.String)
            {
                string k = (string)t;
                if (k == CrossingOffset.KindDiameter || k == CrossingOffset.KindContact) return new CrossingOffset { Kind = k };
                Fail(s, CodeUnknownValue, path + " must be 'diameter', 'contact' or { \"mm\": n }.", path);
                return null;
            }
            var o = t as JObject;
            if (o == null)
            {
                Fail(s, CodeUnknownValue, path + " must be 'diameter', 'contact' or { \"mm\": n }.", path);
                return null;
            }
            if (UnknownKey(s, o, CrossingOffsetKeys, path) != null) return null;
            double v;
            string err = ClearanceNumber(s, o["mm"], path + ".mm", out v);
            if (err == "x") return null;
            if (err == "absent") { Fail(s, CodeMissing, path + ".mm is required.", path + ".mm"); return null; }
            if (v <= 0)
            {
                Fail(s, CodeUnknownValue, path + ".mm must be greater than zero: a displacement of nothing moves nothing.", path + ".mm");
                return null;
            }
            return new CrossingOffset { Kind = CrossingOffset.KindMm, Mm = v };
        }

        /// <summary>A crossing_order read on its own, with no rule ids to resolve its refs against. Throws on a refusal.</summary>
        public static StructuralCrossingOrder ParseCrossingOrder(JObject o)
        {
            var s = new StructuralRequirementSet();
            if (ReadCrossingOrder(o, s, null) != null) throw new ArgumentException(s.Code + " at " + s.Path + ": " + s.Error);
            return s.CrossingOrder;
        }

        /// <summary>A clearance in mm: absent ("absent"), a refusal ("x"), or a finite number from 0 to MaxToleranceMm (null).</summary>
        private static string ClearanceNumber(StructuralRequirementSet s, JToken t, string path, out double value)
        {
            value = 0;
            if (t == null || t.Type == JTokenType.Null) return "absent";
            if (t.Type == JTokenType.String)
            {
                double parsed;
                if (!double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                { Fail(s, CodeNotANumber, path + " must be a number of millimetres.", path); return "x"; }
                value = parsed;
            }
            else if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) value = t.Value<double>();
            else { Fail(s, CodeNotANumber, path + " must be a number of millimetres.", path); return "x"; }
            if (!RebarLayoutRules.IsFinite(value))
            {
                Fail(s, CodeNotFinite, path + " is not a finite number: every comparison against it would be false, and no bars " +
                     "would ever be reported too close.", path);
                return "x";
            }
            if (value < 0)
            {
                Fail(s, CodeUnknownValue, path + " must be zero or more. A negative clearance would excuse bars that pass " +
                     "through each other, and Revit moves those whatever is declared.", path);
                return "x";
            }
            if (value > MaxToleranceMm)
            {
                Fail(s, CodeUnknownValue, path + " is " + value.ToString("0.###", CultureInfo.InvariantCulture) + ", and the limit is " +
                     MaxToleranceMm + " mm: a clear distance that wide is not a spacing between bars.", path);
                return "x";
            }
            return null;
        }

        private static StructuralRequirementSet UnknownKey(StructuralRequirementSet s, JObject o,
                                                           HashSet<string> known, string at)
        {
            if (o == null) return null;
            foreach (JProperty prop in o.Properties())
            {
                if (known.Contains(prop.Name)) continue;
                string path = string.IsNullOrEmpty(at) ? prop.Name : at + "." + prop.Name;
                var allowed = known.OrderBy(x => x, StringComparer.Ordinal).ToList();
                var near = allowed
                    .Select(k => new { k, d = EditDistance(prop.Name, k) })
                    .Where(x => x.d <= Math.Max(2, prop.Name.Length / 3) || Related(prop.Name, x.k))
                    .OrderBy(x => x.d).ThenBy(x => x.k, StringComparer.Ordinal)
                    .Select(x => x.k).ToList();
                string where = string.IsNullOrEmpty(at) ? "the root of the set" : at;
                Fail(s, CodeUnknownKey,
                     "'" + prop.Name + "' is not a key this schema admits at " + where + "." +
                     (near.Count > 0 ? " Did you mean " + string.Join(" or ", near.Select(k => "'" + k + "'")) + "?" : "") +
                     " Admitted here: " + string.Join(", ", allowed) + ". An unknown key is refused rather " +
                     "than skipped: read as absent it would silently become a default, and the plan would " +
                     "look complete while carrying less than was asked for.",
                     path);
                s.Allowed = allowed;
                s.DidYouMean = near;
                return s;
            }
            return null;
        }

        /// <summary>
        /// A key that is the typed one with a unit suffix added or dropped - `spacing`
        /// for `spacing_mm`, `curve` for `curve_mm` - is a near miss even when the edit
        /// distance says otherwise.
        /// </summary>
        private static bool Related(string typed, string known)
        {
            string a = (typed ?? "").ToLowerInvariant(), b = (known ?? "").ToLowerInvariant();
            if (a.Length < 3 || b.Length < 3) return false;
            return b.StartsWith(a + "_", StringComparison.Ordinal) || a.StartsWith(b + "_", StringComparison.Ordinal);
        }

        /// <summary>Levenshtein distance, case-insensitive, for the did-you-mean list only.</summary>
        internal static int EditDistance(string a, string b)
        {
            a = (a ?? "").ToLowerInvariant();
            b = (b ?? "").ToLowerInvariant();
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }

        /// <summary>A number that really is one, or a refusal naming where it was.</summary>
        private static double? ReadNumber(JToken t, string at, out string error)
        {
            error = null;
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
            {
                error = at + " is " + t.ToString(Newtonsoft.Json.Formatting.None) + ", which is not a number. " +
                        "It is refused rather than converted: a string throws, and a boolean becomes 1.";
                return null;
            }
            double v = t.Value<double>();
            if (!RebarLayoutRules.IsFinite(v)) { error = at + " is not a finite number."; return null; }
            return v;
        }

        /// <summary>A whole number that really is one. 2.6 is refused, not rounded to 3.</summary>
        private static int? ReadInt(JToken t, string at, out string error)
        {
            error = null;
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer)
            {
                error = at + " is " + t.ToString(Newtonsoft.Json.Formatting.None) + ", which is not a whole " +
                        "number. It is refused rather than rounded: 2.6 bars is not 3 bars, it is a mistake.";
                return null;
            }
            long v = t.Value<long>();
            if (v < int.MinValue || v > int.MaxValue) { error = at + " is outside the range of a bar count."; return null; }
            return (int)v;
        }

        private static double ReadNonNegative(JToken t, double fallback)
        {
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) return double.NaN;
            double v = t.Value<double>();
            if (!RebarLayoutRules.IsFinite(v) || v < 0) return double.NaN;
            return v;
        }

        /// <summary>A [x, y, z] direction that is finite and not the zero vector.</summary>
        private static string ReadDirection(JArray a, string at, out double[] v)
        {
            v = null;
            if (a == null)
                return at + " is required: the direction the zones run in, as [x, y, z]. The same profile " +
                       "distributes along a beam or up a column depending on it, and the geometry does not say " +
                       "which.";
            if (a.Count != 3) return at + " must have three numbers.";
            var d = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (a[i].Type != JTokenType.Integer && a[i].Type != JTokenType.Float)
                    return at + " carries a value that is not a number.";
                d[i] = a[i].Value<double>();
                if (!RebarLayoutRules.IsFinite(d[i])) return at + " carries a value that is not finite.";
            }
            if (Math.Abs(d[0]) + Math.Abs(d[1]) + Math.Abs(d[2]) < 1e-9) return at + " is the zero vector.";
            v = d;
            return null;
        }

        /// <summary>A list of [x, y, z] points, all finite.</summary>
        private static string ReadPointList(JArray a, string at, List<double[]> into)
        {
            if (a == null) return at + " is required.";
            foreach (JToken t in a)
            {
                var p = t as JArray;
                if (p == null || p.Count != 3) return at + " must be a list of [x, y, z] points.";
                var v = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (p[i].Type != JTokenType.Integer && p[i].Type != JTokenType.Float)
                        return at + " carries a value that is not a number.";
                    v[i] = p[i].Value<double>();
                    if (!RebarLayoutRules.IsFinite(v[i])) return at + " carries a value that is not finite.";
                }
                into.Add(v);
            }
            return null;
        }

        // ------------------------------------------------------------ pieces

        private static string ReadSelector(JObject o, StructuralHostSelector sel, string where)
        {
            if (o == null) return where + " is required: a rule must say WHICH elements it applies to.";
            sel.Category = o.Value<string>("category");
            sel.TypeName = o.Value<string>("type_name");
            sel.Mark = o.Value<string>("mark");
            sel.Level = o.Value<string>("level");
            if (o["source_selector"] != null)
            {
                var src = o["source_selector"] as JObject;
                if (src == null) return where + ".source_selector must be an object: the selector this rule had before it was narrowed.";
                foreach (JProperty p in src.Properties())
                    if (p.Name == "source_selector" || !HostKeys.Contains(p.Name))
                        return where + ".source_selector carries '" + p.Name + "', which is not a host selector key (provenance does not nest).";
                sel.SourceSelector = (JObject)src.DeepClone();
            }
            foreach (JToken t in o["element_ids"] as JArray ?? new JArray())
            {
                // A WHOLE NUMBER, and checked as one. Value<long?>() ROUNDS: 1.5
                // arrived as element 2, which is a different element, silently.
                if (t.Type != JTokenType.Integer)
                    return where + ".element_ids carries " + t.ToString(Newtonsoft.Json.Formatting.None) +
                           ", which is not a whole number. An element id is an integer, and rounding one " +
                           "silently names a different element.";
                long? v = t.Value<long?>();
                if (!v.HasValue) return where + ".element_ids carries something that is not an integer.";
                sel.ElementIds.Add(v.Value);
            }
            if (!sel.Any)
                return where + " selects nothing. Give a category, a type_name, a mark, a level or element_ids - an empty selector " +
                       "would match every element in the model, which is never what a reinforcement rule means.";
            return null;
        }

        /// <summary>
        /// ABSENT AND UNUSABLE ARE DIFFERENT REFUSALS, and they were once the same
        /// one: a rule with no curve_mm at all came back as geometry_not_usable,
        /// which sends a caller looking at coordinates that were never sent. The
        /// code says which, and the message says what to do about it.
        /// </summary>
        private static string ReadCurves(JObject o, StructuralRebarRule r, out string code)
        {
            code = CodeGeometry;
            JArray curves = o["curve_mm"] as JArray;
            if (curves == null)
            {
                code = CodeMissing;
                return "reinforcement_rules['" + r.Id + "'] needs curve_mm: the bar centreline as a list of " +
                       "[x, y, z] points in millimetres, model coordinates. This bridge does not derive a bar " +
                       "from a host - where the steel goes inside a member is a design decision.";
            }
            double[] pendingThrough = null;
            foreach (JToken t in curves)
            {
                // AN ARC (L2): { "through": [x, y, z] } between the two points it joins.
                if (t is JObject arcObj)
                {
                    if (arcObj.Properties().Any(x => x.Name != "through") || !(arcObj["through"] is JArray thr) || thr.Count != 3)
                        return "reinforcement_rules['" + r.Id + "'].curve_mm: an arc is written { \"through\": [x, y, z] } and nothing else.";
                    if (r.CurvesMm.Count == 0 || pendingThrough != null)
                        return "reinforcement_rules['" + r.Id + "'].curve_mm: a through point must stand between two points - after a " +
                               "point, and never twice in a row.";
                    var tp = new double[3];
                    for (int i = 0; i < 3; i++)
                    {
                        if (thr[i].Type != JTokenType.Integer && thr[i].Type != JTokenType.Float)
                            return "reinforcement_rules['" + r.Id + "'].curve_mm carries a through value that is not a number.";
                        tp[i] = thr[i].Value<double>();
                        if (!RebarLayoutRules.IsFinite(tp[i]))
                            return "reinforcement_rules['" + r.Id + "'].curve_mm carries a through value that is not finite.";
                    }
                    pendingThrough = tp;
                    continue;
                }
                var p = t as JArray;
                if (p == null || p.Count != 3)
                    return "reinforcement_rules['" + r.Id + "'].curve_mm must be a list of [x, y, z] triples.";
                // NUMBERS, and finite ones. Value<double>() THROWS on a string, out
                // of a method whose contract is to return a refusal rather than
                // throw; and it happily returns NaN for the token NaN, which then
                // passes every guard downstream.
                var pt = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (p[i].Type != JTokenType.Integer && p[i].Type != JTokenType.Float)
                        return "reinforcement_rules['" + r.Id + "'].curve_mm carries " +
                               p[i].ToString(Newtonsoft.Json.Formatting.None) + ", which is not a number.";
                    pt[i] = p[i].Value<double>();
                    if (!RebarLayoutRules.IsFinite(pt[i]))
                        return "reinforcement_rules['" + r.Id + "'].curve_mm carries a value that is not finite.";
                }
                if (pendingThrough != null)
                {
                    r.ArcThrough[r.CurvesMm.Count - 1] = pendingThrough;
                    pendingThrough = null;
                }
                r.CurvesMm.Add(pt);
            }
            r.Closed = o.Value<bool?>("closed") ?? false;
            if (pendingThrough != null)
            {
                // A TRAILING through point closes the bar with an arc - so the bar must be closed.
                if (!r.Closed)
                    return "reinforcement_rules['" + r.Id + "'].curve_mm ends with a through point, which arcs back to the first " +
                           "point - only a closed bar has that segment.";
                r.ArcThrough[r.CurvesMm.Count - 1] = pendingThrough;
            }
            foreach (KeyValuePair<int, double[]> arc in r.ArcThrough)
            {
                double[] a0 = r.CurvesMm[arc.Key], a1 = r.CurvesMm[(arc.Key + 1) % r.CurvesMm.Count];
                if (RebarArcs.Sagitta(a0, arc.Value, a1) < 1e-3 || RebarArcs.ArcPoints(a0, arc.Value, a1, RebarArcs.DefaultChordMm) == null)
                    return "reinforcement_rules['" + r.Id + "'].curve_mm: the through point of segment " + arc.Key + " lies on the " +
                           "line between its two points, so no arc passes through the three.";
            }
            if (r.CurvesMm.Count < 2)
                return "reinforcement_rules['" + r.Id + "'].curve_mm needs at least two points.";
            if (r.Closed && r.CurvesMm.Count < 3 && r.ArcThrough.Count == 0)
                return "reinforcement_rules['" + r.Id + "'] is closed and has fewer than three points, which " +
                       "encloses nothing.";
            // A ZERO-LENGTH SEGMENT is a degenerate curve Revit will refuse deep
            // inside its own geometry engine, with a message about nothing in
            // particular. Refusing here names the point.
            for (int i = 1; i < r.CurvesMm.Count; i++)
                if (Distance(r.CurvesMm[i - 1], r.CurvesMm[i]) < 1e-6)
                    return "reinforcement_rules['" + r.Id + "'].curve_mm has two identical consecutive points at " +
                           "index " + (i - 1) + " and " + i + ", which is a segment of zero length.";
            if (r.Closed && Distance(r.CurvesMm[r.CurvesMm.Count - 1], r.CurvesMm[0]) < 1e-6)
                return "reinforcement_rules['" + r.Id + "'] is closed and also repeats its first point at the " +
                       "end. Declare the corners once; closed adds the last segment.";
            return null;
        }

        /// <summary>
        /// Read a layout block, and by default resolve it as a check.
        ///
        /// A ZONE's layout is not resolvable here. maximum_spacing needs an array
        /// length, and a zone's array length is the ZONE's length - which the zone
        /// planner works out, and which for the remainder zone is not known until
        /// every other zone has been measured. So a zone reads its layout with
        /// `resolveNow` false: the vocabulary is still checked here, and the
        /// arithmetic is checked by StirrupZoneRules.Plan against the real length,
        /// which is the only place it can be checked truthfully.
        /// </summary>
        private static string ReadLayout(JObject o, StructuralRebarRule r, StructuralRequirementSet s,
                                         bool resolveNow = true, string at = null)
        {
            at = at ?? "reinforcement_rules['" + r.Id + "']";
            if (o == null)
                return at + " needs a layout block.";
            r.Layout.Layout = (o.Value<string>("rule") ?? "").Trim();

            // Value<int?>() and Value<double?>() are not readers, they are
            // CONVERTERS. A string throws FormatException out of a method whose
            // contract is to return a refusal rather than throw; a boolean true
            // becomes 1, so spacing_mm: true is a one-millimetre pitch and 901
            // stirrups; and 2.6 ROUNDS to 3, which is a different number of bars.
            // ReadSelector refuses exactly this three hundred lines above, by name.
            string nerr;
            r.Layout.Number = ReadInt(o["number"], at + ".layout.number", out nerr);
            if (nerr != null) return nerr;
            r.Layout.SpacingMm = ReadNumber(o["spacing_mm"], at + ".layout.spacing_mm", out nerr);
            if (nerr != null) return nerr;
            r.Layout.ArrayLengthMm = ReadNumber(o["array_length_mm"], at + ".layout.array_length_mm", out nerr);
            if (nerr != null) return nerr;
            // The ONLY defaults in this file, and they are Revit's own. They are
            // echoed into every plan so they are visible rather than assumed.
            r.Layout.IncludeFirstBar = o.Value<bool?>("include_first_bar") ?? true;
            r.Layout.IncludeLastBar = o.Value<bool?>("include_last_bar") ?? true;
            r.BarsOnNormalSide = o.Value<bool?>("bars_on_normal_side") ?? true;
            StructuralBarTypeRef bt;
            if (s.BarTypes.TryGetValue(r.BarTypeId, out bt)) r.Layout.BarDiameterMm = bt.NominalDiameterMm;

            // THE DIAMETER IS THE MODEL'S TO SUPPLY, and nominal_diameter_mm is an
            // optional convenience. Probing with the declared one and refusing when
            // it is absent made minimum_clear_spacing unloadable unless the set
            // repeated a number the bar type already carries - and the resolver then
            // recomputes the whole layout with the MODEL diameter read from Revit
            // anyway, which is the one Revit counts with.
            //
            // So the probe here validates everything EXCEPT the count, using a
            // stand-in diameter when none was declared. The real count is resolved
            // against the model.
            RebarLayoutRequest probeRequest = r.Layout;
            if (r.Layout.Layout == RebarLayout.MinimumClearSpacing && !r.Layout.BarDiameterMm.HasValue)
            {
                probeRequest = new RebarLayoutRequest
                {
                    Layout = r.Layout.Layout,
                    Number = r.Layout.Number,
                    SpacingMm = r.Layout.SpacingMm,
                    ArrayLengthMm = r.Layout.ArrayLengthMm,
                    IncludeFirstBar = r.Layout.IncludeFirstBar,
                    IncludeLastBar = r.Layout.IncludeLastBar,
                    BarDiameterMm = 1.0
                };
            }
            if (!resolveNow)
            {
                // The one thing that IS checkable without a length: the word.
                if (!RebarLayout.IsKnown(r.Layout.Layout))
                    return at + ".layout.rule must be one of " + string.Join(", ", RebarLayout.All) +
                           " - got " + Show(r.Layout.Layout) + ".";
                return null;
            }

            RebarLayoutPlan probe = RebarLayoutRules.Resolve(probeRequest);
            if (!probe.Ok)
                return at + ".layout: " + probe.Error + " (" + probe.Code + ")";
            return null;
        }

        /// <summary>
        /// F2.1: laps: [{ at_mm | at_ratio, length_mm | length_db }]. The station is measured
        /// along the host from its start face, as from_mm is; the length is the caller's - in
        /// mm, or a multiple of the bar's diameter whose multiplier the caller chose. No code's
        /// lap length is compiled in. Staggering is refused by the key check, not ignored.
        /// </summary>
        private static string ReadLaps(StructuralRequirementSet s, JToken token, StructuralSectionLayer l, string lat)
        {
            var arr = token as JArray;
            if (arr == null) { Fail(s, CodeSchema, lat + ".laps must be an array.", lat + ".laps"); return "x"; }
            for (int i = 0; i < arr.Count; i++)
            {
                string at = lat + ".laps[" + i + "]";
                var o = arr[i] as JObject;
                if (o == null) { Fail(s, CodeSchema, at + " must be an object.", at); return "x"; }
                if (UnknownKey(s, o, SectionLapKeys, at) != null) return "x";
                string e1, e2, e3, e4;
                double? atMm = ReadNumber(o["at_mm"], at + ".at_mm", out e1);
                double? atRatio = ReadNumber(o["at_ratio"], at + ".at_ratio", out e2);
                double? lenMm = ReadNumber(o["length_mm"], at + ".length_mm", out e3);
                double? lenDb = ReadNumber(o["length_db"], at + ".length_db", out e4);
                string bad = e1 ?? e2 ?? e3 ?? e4;
                if (bad != null) { Fail(s, CodeNotANumber, bad, at); return "x"; }
                if (atMm.HasValue == atRatio.HasValue)
                { Fail(s, CodeMissing, at + " needs exactly one station: at_mm (from the host's start face) or at_ratio.", at); return "x"; }
                // A SPLICE IS AN OVERLAP OR A COUPLER (L7): length_mm, length_db, or coupler_type_name -
                // exactly one. A coupler joins two collinear pieces end to end at the station.
                JToken couplerTok = o["coupler_type_name"];
                bool coupler = couplerTok != null && couplerTok.Type != JTokenType.Null;
                if ((lenMm.HasValue ? 1 : 0) + (lenDb.HasValue ? 1 : 0) + (coupler ? 1 : 0) != 1)
                {
                    Fail(s, CodeMissing, at + " needs exactly one of length_mm, length_db (in bar diameters) or coupler_type_name: " +
                         "a splice is an overlap of a declared length, or a coupler of a declared type, never both and never neither.", at);
                    return "x";
                }
                string couplerType = null;
                if (coupler)
                {
                    couplerType = couplerTok.Type == JTokenType.String ? couplerTok.Value<string>() : null;
                    if (string.IsNullOrWhiteSpace(couplerType))
                    {
                        Fail(s, CodeMissing, at + ".coupler_type_name must name a coupler type the model carries.", at + ".coupler_type_name");
                        return "x";
                    }
                    if (o["toward"] != null)
                    {
                        Fail(s, CodeUnknownValue, at + ".toward moves an overlapping piece across; a coupler joins two collinear " +
                             "pieces, so there is nothing to move. Leave toward out.", at + ".toward");
                        return "x";
                    }
                }
                if (l.Laps.Count > 0 && (l.Laps[0].CouplerTypeName != null) != coupler)
                {
                    Fail(s, CodeUnknownValue, at + " mixes coupler and overlap splices in one layer. A coupled layer is collinear and a " +
                         "lapped one alternates across, so one layer is spliced one way. Declare the other splices on their own layer.", at);
                    return "x";
                }
                // On a layer from a named axis the station is from that axis, and may lie before it.
                if ((atMm.HasValue && atMm.Value <= 0 && l.Extend != StructuralSectionLayer.ExtendAxes) || (atRatio.HasValue && (atRatio.Value <= 0 || atRatio.Value >= 1)) ||
                    (lenMm.HasValue && lenMm.Value <= 0) || (lenDb.HasValue && lenDb.Value <= 0))
                { Fail(s, CodeGeometry, at + ": the station must lie inside the member (0 < at_ratio < 1) and the length be positive.", at); return "x"; }
                // D4: the next piece sits one diameter across, in contact - toward the inside of the
                // section unless the caller asks for the outside. One layer alternates around one
                // line, so its laps name one direction.
                string toward = o["toward"] == null ? StructuralLap.TowardInside : (string)o["toward"];
                if (toward != StructuralLap.TowardInside && toward != StructuralLap.TowardOutside)
                { Fail(s, CodeSchema, at + ".toward must be 'inside' or 'outside'.", at + ".toward"); return "x"; }
                if (l.Laps.Count > 0 && l.Laps[0].Toward != toward)
                { Fail(s, CodeGeometry, at + ".toward is '" + toward + "' and an earlier lap of this layer is '" + l.Laps[0].Toward +
                       "': the pieces of one layer alternate around one line, so its laps name one direction.", at + ".toward"); return "x"; }
                l.Laps.Add(new StructuralLap { AtMm = atMm, AtRatio = atRatio, LengthMm = lenMm, LengthDb = lenDb, Toward = toward,
                                               CouplerTypeName = couplerType });
            }
            return null;
        }

        /// <summary>
        /// stirrups.shape_placement (acero 3.0, L7): the hoop or spiral is PLACED from a rebar shape
        /// the model carries, because Rebar.CreateFromCurves fails on every chain with an arc in it
        /// (measured on Revit 2024). The shape is named, its kind declared and - for a spiral - the
        /// pitch and finishing turns too; nothing is defaulted. Its definition is read and judged by
        /// the resolver. Returns null on success; the failure is recorded on `s`.
        /// </summary>
        private static string ReadPlacement(StructuralRequirementSet s, JToken token, JObject stirrups,
                                            StructuralStirrupZoneRule z, string sat)
        {
            string at = sat + ".shape_placement";
            var o = token as JObject;
            if (o == null) { Fail(s, CodeSchema, at + " must be an object { shape, kind, ... }.", at); return "x"; }
            if (UnknownKey(s, o, ShapePlacementKeys, at) != null) return "x";
            var p = new StructuralShapePlacement { Shape = o["shape"]?.Type == JTokenType.String ? o.Value<string>("shape") : null };
            if (string.IsNullOrWhiteSpace(p.Shape))
            {
                Fail(s, CodeMissing, at + ".shape must name a rebar shape the model carries. Which shape is a hoop or a spiral " +
                     "is read from its definition, never guessed from a name.", at + ".shape");
                return "x";
            }
            if (o["kind"] == null)
            {
                Fail(s, CodeMissing, at + ".kind is required: " + string.Join(" or ", StructuralShapePlacement.Kinds) + ".", at + ".kind");
                return "x";
            }
            p.Kind = o["kind"].Type == JTokenType.String ? o.Value<string>("kind") : null;
            if (Array.IndexOf(StructuralShapePlacement.Kinds, p.Kind) < 0)
            {
                Fail(s, CodeUnknownValue, at + ".kind must be " + string.Join(" or ", StructuralShapePlacement.Kinds) + " - got " +
                     o["kind"].ToString(Newtonsoft.Json.Formatting.None) + ".", at + ".kind");
                return "x";
            }
            string[] spiralOnly = { "pitch_mm", "base_finishing_turns", "top_finishing_turns" };
            if (p.Kind == StructuralShapePlacement.KindLappedCircle)
            {
                foreach (string k in spiralOnly)
                    if (o[k] != null)
                    {
                        Fail(s, CodeUnknownValue, at + "." + k + " belongs to a spiral; a lapped circle would not use it, and a value " +
                             "that would not be used is refused rather than ignored.", at + "." + k);
                        return "x";
                    }
            }
            else
            {
                foreach (string k in spiralOnly)
                    if (o[k] == null)
                    {
                        Fail(s, CodeMissing, at + " is a spiral and needs " + k + ". The pitch and the finishing turns are " +
                             "declared; none of them is defaulted.", at + "." + k);
                        return "x";
                    }
                string e1;
                double? pitch = ReadNumber(o["pitch_mm"], at + ".pitch_mm", out e1);
                if (e1 != null) { Fail(s, CodeNotANumber, e1, at + ".pitch_mm"); return "x"; }
                if (!pitch.HasValue || pitch.Value <= 0)
                { Fail(s, CodeGeometry, at + ".pitch_mm must be greater than zero.", at + ".pitch_mm"); return "x"; }
                p.PitchMm = pitch;
                foreach (string k in new[] { "base_finishing_turns", "top_finishing_turns" })
                {
                    string e2;
                    int? turns = ReadInt(o[k], at + "." + k, out e2);
                    if (e2 != null) { Fail(s, CodeNotANumber, e2, at + "." + k); return "x"; }
                    if (!turns.HasValue || turns.Value < 0)
                    { Fail(s, CodeGeometry, at + "." + k + " must be a whole number of turns, zero or more.", at + "." + k); return "x"; }
                    if (k == "base_finishing_turns") p.BaseFinishingTurns = turns; else p.TopFinishingTurns = turns;
                }
                // ONE SPIRAL OVER ONE ZONE. Its pitch is the placement's, so a zone layout with a spacing
                // would be a second statement about the same thing; and two spirals meeting in one plane
                // would put two finishing turns on top of each other.
                if (z.Zones.Count != 1 || z.Zones[0].Layout == null || z.Zones[0].Layout.Layout != RebarLayout.Single)
                {
                    Fail(s, CodeUnknownValue, sat + " is a spiral: it is one zone laid out single - { rule: single } - and the " +
                         "spiral runs over that zone's length at the declared pitch. " +
                         (z.Zones.Count != 1 ? "It declares " + z.Zones.Count + " zones." : "Its zone is laid out '" +
                         z.Zones[0].Layout?.Layout + "'."), sat + ".zones");
                    return "x";
                }
            }
            // ONE INSTRUCTION ABOUT THE SHAPE. A stirrup shape or permission to create one beside a
            // placement would be a second, different one.
            if (stirrups["shape"] != null || stirrups["allow_new_shape"] != null)
            {
                Fail(s, CodeUnknownValue, sat + " declares shape_placement AND " + (stirrups["shape"] != null ? "shape" : "allow_new_shape") +
                     ". The placement names the shape; there is nothing else to say about it.", sat);
                return "x";
            }
            // NO HOOKS. A placed hoop is a closed lapped circle and a spiral ends in finishing turns;
            // Revit assigns hooks to a placed shape ARBITRARILY, so the apply clears both ends, and a
            // declared hook would be one this build does not put there.
            foreach (StructuralTermination t in new[] { z.Start, z.End })
                if (t != null && !string.IsNullOrWhiteSpace(t.HookTypeId) && !s.HookTypes[t.HookTypeId].None)
                {
                    Fail(s, CodeUnknownValue, sat + " places a " + (p.Kind == StructuralShapePlacement.KindSpiral ? "spiral" : "lapped hoop") +
                         " and declares hook '" + t.HookTypeId + "'. Hooks on a placed shape are not implemented: the apply builds it " +
                         "with none at either end. Leave start and end out, or declare a hook type with none: true.", sat);
                    return "x";
                }
            z.ShapePlacement = p;
            z.ShapeName = p.Shape;
            return null;
        }

        /// <summary>
        /// into: [element ids] - the other elements a rule's bars run into (acero 3.0, L1). Ids
        /// only: which element a dowel is embedded in is declared, never searched for.
        /// </summary>
        private static string ReadInto(StructuralRequirementSet s, JToken token, List<long> into, string at)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var arr = token as JArray;
            if (arr == null) { Fail(s, CodeSchema, at + ".into must be an array of element ids.", at + ".into"); return "x"; }
            if (arr.Count == 0)
            {
                Fail(s, CodeMissing, at + ".into is empty. Name the elements the bars run into, or leave into out.", at + ".into");
                return "x";
            }
            for (int i = 0; i < arr.Count; i++)
            {
                string p = at + ".into[" + i + "]";
                if (arr[i].Type != JTokenType.Integer || arr[i].Value<long>() <= 0)
                {
                    Fail(s, CodeNotANumber, p + " is " + arr[i].ToString(Newtonsoft.Json.Formatting.None) +
                         ", which is not an element id.", p);
                    return "x";
                }
                long id = arr[i].Value<long>();
                if (into.Contains(id))
                {
                    Fail(s, CodeDuplicateId, p + " names element " + id + " a second time.", p);
                    return "x";
                }
                into.Add(id);
            }
            return null;
        }

        /// <summary>
        /// anchorage: [{ element_id, min_length_mm }] - and, on a section layer, end: start | end,
        /// because a lapped layer is several pieces and only the one at that end is anchored there.
        /// The element must be one the rule runs into; the length is the caller's.
        /// </summary>
        private static string ReadAnchorage(StructuralRequirementSet s, JToken token, List<long> into,
                                            List<StructuralAnchorage> outp, string at, bool layer)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            var arr = token as JArray;
            if (arr == null) { Fail(s, CodeSchema, at + ".anchorage must be an array.", at + ".anchorage"); return "x"; }
            for (int i = 0; i < arr.Count; i++)
            {
                string p = at + ".anchorage[" + i + "]";
                var o = arr[i] as JObject;
                if (o == null) { Fail(s, CodeSchema, p + " must be an object.", p); return "x"; }
                if (UnknownKey(s, o, layer ? LayerAnchorageKeys : AnchorageKeys, p) != null) return "x";
                JToken idt = o["element_id"];
                if (idt == null || idt.Type != JTokenType.Integer)
                {
                    Fail(s, idt == null ? CodeMissing : CodeNotANumber, p + ".element_id must be the id of an element named in into.", p + ".element_id");
                    return "x";
                }
                long id = idt.Value<long>();
                if (!into.Contains(id))
                {
                    Fail(s, CodeUnresolvedReference, p + ".element_id is " + id + ", which " +
                         (into.Count == 0 ? "cannot be anchored in: the rule declares no into." : "is not one of the elements in into."),
                         p + ".element_id");
                    return "x";
                }
                if (o["min_length_mm"] == null)
                {
                    Fail(s, CodeMissing, p + " needs min_length_mm: the anchored length is declared, never defaulted.", p + ".min_length_mm");
                    return "x";
                }
                string err;
                double? len = ReadNumber(o["min_length_mm"], p + ".min_length_mm", out err);
                if (err != null) { Fail(s, CodeNotANumber, err, p + ".min_length_mm"); return "x"; }
                if (!len.HasValue || len.Value <= 0)
                {
                    Fail(s, CodeUnknownValue, p + ".min_length_mm must be greater than zero.", p + ".min_length_mm");
                    return "x";
                }
                var a = new StructuralAnchorage { ElementId = id, MinLengthMm = len.Value };
                if (layer)
                {
                    a.End = o.Value<string>("end");
                    if (a.End == null)
                    {
                        Fail(s, CodeMissing, p + " needs end: start or end - which end of the layer is anchored in element " +
                             id + ". A lapped layer is several pieces, and only one of them reaches it.", p + ".end");
                        return "x";
                    }
                    if (a.End != StructuralAnchorage.EndStart && a.End != StructuralAnchorage.EndEnd)
                    {
                        Fail(s, CodeUnknownValue, p + ".end must be start or end - got " + Show(a.End) + ".", p + ".end");
                        return "x";
                    }
                }
                outp.Add(a);
            }
            return null;
        }

        public static string HookLengthPath(string hookTypeId, string barTypeId)
        {
            return "hook_types['" + hookTypeId + "'].lengths_mm" + (barTypeId == null ? "" : "['" + barTypeId + "']");
        }

        /// <summary>
        /// lengths_mm on one hook_types entry: { "&lt;bar_types id&gt;": mm &gt; 0 }. Null on success;
        /// the refusal is recorded on `s` with its path.
        /// </summary>
        private static string ReadHookLengths(StructuralRequirementSet s, JToken t, StructuralHookTypeRef href)
        {
            if (t == null) return null;
            string at = HookLengthPath(href.Id, null);
            if (href.None)
            {
                Fail(s, CodeUnknownValue,
                    at + " is declared on a hook that is none: true. A straight end has no hook, so it has no " +
                    "hook length to declare.", at);
                return "x";
            }
            var o = t as JObject;
            if (o == null || !o.Properties().Any())
            {
                Fail(s, o == null ? CodeSchema : CodeMissing,
                    at + " must be an object naming at least one bar_types id, each with the straight extension " +
                    "of this hook on that bar type in millimetres - { \"<bar_types id>\": 100 }.", at);
                return "x";
            }
            foreach (JProperty p in o.Properties())
            {
                string pat = HookLengthPath(href.Id, p.Name);
                StructuralBarTypeRef bar;
                if (!s.BarTypes.TryGetValue(p.Name, out bar))
                {
                    Fail(s, CodeUnresolvedReference,
                        pat + " names bar type '" + p.Name + "', which no bar_types entry declares. A hook length " +
                        "is stored on a bar type, so it can only be declared for one the set names.", pat);
                    return "x";
                }
                double mm;
                if (ReadPositiveMm(s, p.Value, pat, out mm) != null) return "x";
                // ONE PAIR OF TYPES, ONE NUMBER. Two hook_types entries naming the same RebarHookType
                // and giving one bar type two lengths would leave the model carrying whichever was
                // written last, and nobody declared that order.
                foreach (StructuralHookTypeRef other in s.HookTypes.Values.Concat(new[] { href }))
                {
                    if (other.None || !string.Equals(other.TypeName, href.TypeName, StringComparison.Ordinal)) continue;
                    foreach (KeyValuePair<string, double> ol in other.LengthsMm)
                    {
                        if (!string.Equals(s.BarTypes[ol.Key].TypeName, bar.TypeName, StringComparison.Ordinal)) continue;
                        if (Math.Abs(ol.Value - mm) <= 1e-9) continue;
                        Fail(s, CodeUnknownValue,
                            pat + " is " + mm.ToString("0.###", CultureInfo.InvariantCulture) + " mm and " +
                            HookLengthPath(other.Id, ol.Key) + " is " + ol.Value.ToString("0.###", CultureInfo.InvariantCulture) +
                            " mm - two lengths for hook type '" + href.TypeName + "' on bar type '" + bar.TypeName +
                            "'. Revit keeps one per pair, so one of them would be overwritten.", pat);
                        return "x";
                    }
                }
                href.LengthsMm[p.Name] = mm;
            }
            return null;
        }

        /// <summary>
        /// Every declared (bar type, hook type) hook length, once per pair of TYPE NAMES - two
        /// entries that agree on a pair are one declaration - ordered by hook id, then bar id.
        /// </summary>
        public List<DeclaredHookLength> DeclaredHookLengths()
        {
            var outp = new List<DeclaredHookLength>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (StructuralHookTypeRef h in HookTypes.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                if (h.None) continue;
                foreach (KeyValuePair<string, double> l in h.LengthsMm.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    StructuralBarTypeRef bar = BarTypes[l.Key];
                    if (!seen.Add(bar.TypeName + "|" + h.TypeName)) continue;
                    outp.Add(new DeclaredHookLength
                    {
                        HookTypeId = h.Id, HookTypeName = h.TypeName,
                        BarTypeId = bar.Id, BarTypeName = bar.TypeName, LengthMm = l.Value
                    });
                }
            }
            return outp;
        }

        private static string ReadTermination(JObject o, StructuralTermination t, StructuralRequirementSet s,
                                              string ruleId, string which)
        {
            if (o == null) return null;   // no block means no hook, which is a straight end
            t.HookTypeId = o.Value<string>("hook_type");
            if (t.HookTypeId != null && !s.HookTypes.ContainsKey(t.HookTypeId))
                return "reinforcement_rules['" + ruleId + "']." + which + ".hook_type is '" + t.HookTypeId +
                       "', which no hook_types entry declares.";
            string orientation = o.Value<string>("orientation");
            if (orientation != null)
            {
                if (!RebarApiVocabulary.IsKnown(orientation))
                    return "reinforcement_rules['" + ruleId + "']." + which + ".orientation must be " +
                           string.Join(" or ", RebarApiVocabulary.All) + " - got " + Show(orientation) + ".";
                t.Orientation = orientation;
            }
            return null;
        }

        // ------------------------------------- area, path and fabric (acero 3.0, L4b)

        /// <summary>
        /// The host of a system rule: ONE element, by id. An area, a path or a fabric area is
        /// drawn on a face of one floor or wall, and its outline is that host's coordinates -
        /// the same outline on a second host would be somewhere else on it, or off it.
        /// </summary>
        private static string ReadSystemHost(StructuralRequirementSet s, JObject o, StructuralHostSelector sel, string at)
        {
            if (UnknownKey(s, o["host"] as JObject, HostKeys, at + ".host") != null) return "x";
            string err = ReadSelector(o["host"] as JObject, sel, at + ".host");
            if (err != null) { Fail(s, CodeMissing, err, at + ".host"); return "x"; }
            if (sel.ElementIds.Count != 1)
            {
                Fail(s, CodeUnknownValue,
                    at + ".host must name exactly ONE element in element_ids (it names " + sel.ElementIds.Count + "). " +
                    "The outline is drawn on a face of one floor or wall, in model coordinates, and the same outline " +
                    "on another host would land somewhere else on it - or off it.", at + ".host");
                return "x";
            }
            return null;
        }

        /// <summary>A number greater than zero, declared. Null on success; the failure is recorded on `s`.</summary>
        private static string ReadPositiveMm(StructuralRequirementSet s, JToken t, string at, out double value)
        {
            value = 0;
            if (t == null || t.Type == JTokenType.Null) { Fail(s, CodeMissing, at + " is required.", at); return "x"; }
            string err;
            double? v = ReadNumber(t, at, out err);
            if (err != null)
            {
                Fail(s, t.Type == JTokenType.Integer || t.Type == JTokenType.Float ? CodeNotFinite : CodeNotANumber, err, at);
                return "x";
            }
            if (v.Value <= 0) { Fail(s, CodeUnknownValue, at + " must be greater than zero.", at); return "x"; }
            value = v.Value;
            return null;
        }

        /// <summary>A boolean that really is one. "yes" is refused, not read as true.</summary>
        private static string ReadStrictBool(StructuralRequirementSet s, JToken t, string at, bool? fallback, out bool value)
        {
            value = fallback ?? false;
            if (t == null || t.Type == JTokenType.Null)
            {
                if (fallback.HasValue) return null;
                Fail(s, CodeMissing, at + " is required: true or false. It is not defaulted.", at);
                return "x";
            }
            if (t.Type != JTokenType.Boolean)
            {
                Fail(s, CodeUnknownValue, at + " is " + t.ToString(Newtonsoft.Json.Formatting.None) +
                    ", which is not true or false.", at);
                return "x";
            }
            value = t.Value<bool>();
            return null;
        }

        private static string ReadName(StructuralRequirementSet s, JObject o, string key, string at, string what, out string name)
        {
            name = o[key] != null && o[key].Type == JTokenType.String ? o.Value<string>(key) : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                Fail(s, CodeMissing,
                    at + "." + key + " is required: the name of " + what + " that exists in the model. This bridge " +
                    "does not create it and does not pick one.", at + "." + key);
                return "x";
            }
            return null;
        }

        private static string ReadBarTypeRef(StructuralRequirementSet s, JObject o, string at, out string id)
        {
            id = o.Value<string>("bar_type");
            if (string.IsNullOrWhiteSpace(id)) { Fail(s, CodeMissing, at + " needs bar_type.", at + ".bar_type"); return "x"; }
            if (!s.BarTypes.ContainsKey(id))
            {
                Fail(s, CodeUnresolvedReference, at + ".bar_type is '" + id + "', which no bar_types entry declares.",
                     at + ".bar_type");
                return "x";
            }
            return null;
        }

        private static string ReadPoints(StructuralRequirementSet s, JObject o, string key, string at, List<double[]> into)
        {
            var a = o[key] as JArray;
            if (a == null)
            {
                Fail(s, CodeMissing, at + "." + key + " is required: a list of [x, y, z] points in millimetres, " +
                     "model coordinates, on a face of the host.", at + "." + key);
                return "x";
            }
            string err = ReadPointList(a, at + "." + key, into);
            if (err != null) { Fail(s, CodeGeometry, err, at + "." + key); return "x"; }
            return null;
        }

        private static string ReadSystemDirection(StructuralRequirementSet s, JObject o, string at, double[] planeNormal,
                                                  out double[] dir)
        {
            dir = null;
            if (o["major_direction"] == null)
            {
                Fail(s, CodeMissing, at + ".major_direction is required: the direction the major bars run, as " +
                     "[x, y, z] in the plane of the outline. The outline does not say which way they run.",
                     at + ".major_direction");
                return "x";
            }
            string err = ReadDirection(o["major_direction"] as JArray, at + ".major_direction", out dir);
            if (err != null) { Fail(s, CodeGeometry, err, at + ".major_direction"); return "x"; }
            if (!SystemReinforcementRules.InPlane(dir, planeNormal, s.Tolerances.AngleDegrees))
            {
                Fail(s, CodeGeometry,
                    at + ".major_direction does not lie in the plane of the outline (within " +
                    s.Tolerances.AngleDegrees.ToString("0.###", CultureInfo.InvariantCulture) + " degrees). The " +
                    "major bars run IN the face the outline is drawn on.", at + ".major_direction");
                return "x";
            }
            return null;
        }

        private static string ReadMarkAndRequired(StructuralRequirementSet s, JObject o, string at, out string mark, out bool required)
        {
            mark = o.Value<string>("mark");
            return ReadStrictBool(s, o["required"], at + ".required", true, out required);
        }

        private static bool ClaimId(StructuralRequirementSet s, HashSet<string> ruleIds, string id, string array)
        {
            if (string.IsNullOrWhiteSpace(id)) { Fail(s, CodeMissing, "every entry in " + array + " needs an id."); return false; }
            if (!ruleIds.Add(id))
            {
                Fail(s, CodeDuplicateId,
                    "'" + id + "' is used by more than one rule. Every rule of a set - reinforcement, zone, mat, " +
                    "section, area, path and fabric - shares one namespace, because provenance records the id alone.");
                return false;
            }
            return true;
        }

        private static string ReadAreaRules(JObject doc, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["area_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in area_rules must be an object."); return "x"; }
                if (UnknownKey(s, o, AreaRuleKeys, "area_rules[" + s.AreaRules.Count + "]") != null) return "x";
                var r = new StructuralAreaRule { Id = o.Value<string>("id"), Raw = o };
                string at = "area_rules['" + r.Id + "']";
                if (!ClaimId(s, ruleIds, r.Id, "area_rules")) return "x";
                if (ReadSystemHost(s, o, r.Host, at) != null) return "x";

                if (ReadPoints(s, o, "boundary_mm", at, r.BoundaryMm) != null) return "x";
                string gerr = SystemReinforcementRules.CheckPolygon(r.BoundaryMm, s.Tolerances.LengthMm, at + ".boundary_mm");
                if (gerr != null) { Fail(s, CodeGeometry, gerr, at + ".boundary_mm"); return "x"; }
                if (ReadSystemDirection(s, o, at, SystemReinforcementRules.NewellNormal(r.BoundaryMm), out r.MajorDirectionMm) != null)
                    return "x";
                if (ReadName(s, o, "area_type_name", at, "an area reinforcement type", out r.AreaTypeName) != null) return "x";

                var layers = o["layers"] as JObject;
                if (layers == null || !layers.Properties().Any())
                {
                    Fail(s, CodeMissing,
                        at + ".layers is required and names at least one layer: top_major, top_minor, bottom_major, " +
                        "bottom_minor on a floor; exterior_major, exterior_minor, interior_major, interior_minor on a " +
                        "wall. Every layer not named is built inactive.", at + ".layers");
                    return "x";
                }
                if (UnknownKey(s, layers, AreaLayersKeys, at + ".layers") != null) return "x";
                // IN REVIT'S ORDER, so two sets naming the same layers in another order plan the same.
                foreach (string name in SystemReinforcementRules.SlabLayers.Concat(SystemReinforcementRules.WallLayers))
                {
                    if (layers[name] == null) continue;
                    string lat = at + ".layers." + name;
                    var lo = layers[name] as JObject;
                    if (lo == null) { Fail(s, CodeSchema, lat + " must be an object { bar_type, spacing_mm }.", lat); return "x"; }
                    if (UnknownKey(s, lo, SystemLayerKeys, lat) != null) return "x";
                    var layer = new StructuralSystemLayer { Name = name };
                    if (ReadBarTypeRef(s, lo, lat, out layer.BarTypeId) != null) return "x";
                    if (ReadPositiveMm(s, lo["spacing_mm"], lat + ".spacing_mm", out layer.SpacingMm) != null) return "x";
                    r.Layers.Add(layer);
                }
                var families = r.Layers.Select(l => SystemReinforcementRules.FamilyOfLayer(l.Name)).Distinct().ToList();
                if (families.Count != 1)
                {
                    Fail(s, CodeUnknownValue,
                        at + ".layers mixes floor words (top, bottom) with wall words (exterior, interior): " +
                        string.Join(", ", r.Layers.Select(l => l.Name)) + ". A floor has top and bottom layers and a " +
                        "wall has exterior and interior ones; which face of a wall is its top is not something this " +
                        "bridge decides.", at + ".layers");
                    return "x";
                }
                r.LayerFamily = families[0];
                if (ReadMarkAndRequired(s, o, at, out r.Mark, out r.Required) != null) return "x";
                s.AreaRules.Add(r);
            }
            return null;
        }

        private static string ReadPathRules(JObject doc, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["path_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in path_rules must be an object."); return "x"; }
                if (UnknownKey(s, o, PathRuleKeys, "path_rules[" + s.PathRules.Count + "]") != null) return "x";
                var r = new StructuralPathRule { Id = o.Value<string>("id"), Raw = o };
                string at = "path_rules['" + r.Id + "']";
                if (!ClaimId(s, ruleIds, r.Id, "path_rules")) return "x";
                if (ReadSystemHost(s, o, r.Host, at) != null) return "x";

                if (ReadPoints(s, o, "path_mm", at, r.PathMm) != null) return "x";
                string gerr = SystemReinforcementRules.CheckPolyline(r.PathMm, s.Tolerances.LengthMm, at + ".path_mm");
                if (gerr != null) { Fail(s, CodeGeometry, gerr, at + ".path_mm"); return "x"; }
                if (ReadName(s, o, "path_type_name", at, "a path reinforcement type", out r.PathTypeName) != null) return "x";
                // DECLARED: which side of the path the bars go is the designer's, and the
                // polyline does not say.
                if (ReadStrictBool(s, o["flip"], at + ".flip", null, out r.Flip) != null) return "x";

                var p = o["primary"] as JObject;
                if (p == null)
                {
                    Fail(s, CodeMissing, at + ".primary is required: { bar_type, spacing_mm, length_mm } of the primary bars.",
                         at + ".primary");
                    return "x";
                }
                string pat = at + ".primary";
                if (UnknownKey(s, p, PathPrimaryKeys, pat) != null) return "x";
                if (ReadBarTypeRef(s, p, pat, out r.PrimaryBarTypeId) != null) return "x";
                if (ReadPositiveMm(s, p["spacing_mm"], pat + ".spacing_mm", out r.PrimarySpacingMm) != null) return "x";
                if (ReadPositiveMm(s, p["length_mm"], pat + ".length_mm", out r.PrimaryLengthMm) != null) return "x";
                if (ReadMarkAndRequired(s, o, at, out r.Mark, out r.Required) != null) return "x";
                s.PathRules.Add(r);
            }
            return null;
        }

        private static string ReadFabricRules(JObject doc, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["fabric_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in fabric_rules must be an object."); return "x"; }
                if (UnknownKey(s, o, FabricRuleKeys, "fabric_rules[" + s.FabricRules.Count + "]") != null) return "x";
                var r = new StructuralFabricRule { Id = o.Value<string>("id"), Raw = o };
                string at = "fabric_rules['" + r.Id + "']";
                if (!ClaimId(s, ruleIds, r.Id, "fabric_rules")) return "x";
                if (ReadSystemHost(s, o, r.Host, at) != null) return "x";

                if (ReadPoints(s, o, "boundary_mm", at, r.BoundaryMm) != null) return "x";
                string gerr = SystemReinforcementRules.CheckPolygon(r.BoundaryMm, s.Tolerances.LengthMm, at + ".boundary_mm");
                if (gerr != null) { Fail(s, CodeGeometry, gerr, at + ".boundary_mm"); return "x"; }
                double[] n = SystemReinforcementRules.NewellNormal(r.BoundaryMm);
                if (ReadSystemDirection(s, o, at, n, out r.MajorDirectionMm) != null) return "x";

                // THE ORIGIN OF THE MAJOR DIRECTION LINE, declared: the sheets are laid out
                // from it, and where the first sheet starts is a layout decision.
                string oat = at + ".major_direction_origin_mm";
                if (o["major_direction_origin_mm"] == null)
                {
                    Fail(s, CodeMissing, oat + " is required: [x, y, z], the point the major direction line passes " +
                         "through. Revit lays the sheets out from it, and where the first sheet starts is not ours.", oat);
                    return "x";
                }
                var originList = new List<double[]>();
                var oa = o["major_direction_origin_mm"] as JArray;
                var wrapped = new JArray();
                if (oa != null) wrapped.Add(oa.DeepClone());
                string perr = oa == null ? oat + " must be one [x, y, z] point."
                                         : ReadPointList(wrapped, oat, originList);
                if (perr != null) { Fail(s, CodeGeometry, perr, oat); return "x"; }
                r.MajorDirectionOriginMm = originList[0];
                double off = 0;
                for (int i = 0; i < 3; i++) off += (r.MajorDirectionOriginMm[i] - r.BoundaryMm[0][i]) * n[i];
                if (Math.Abs(off) > s.Tolerances.LengthMm)
                {
                    Fail(s, CodeGeometry,
                        oat + " is " + Math.Abs(off).ToString("0.###", CultureInfo.InvariantCulture) + " mm off the " +
                        "plane of the outline. The major direction line lies in the face the fabric is drawn on.", oat);
                    return "x";
                }

                if (ReadName(s, o, "fabric_area_type_name", at, "a fabric area type", out r.FabricAreaTypeName) != null) return "x";
                if (ReadName(s, o, "fabric_sheet_type_name", at, "a fabric sheet type", out r.FabricSheetTypeName) != null) return "x";

                JToken cov = o["cover_offset_mm"];
                if (cov != null && cov.Type != JTokenType.Null)
                {
                    string cat = at + ".cover_offset_mm";
                    string cerr;
                    double? v = ReadNumber(cov, cat, out cerr);
                    if (cerr != null)
                    {
                        Fail(s, cov.Type == JTokenType.Integer || cov.Type == JTokenType.Float ? CodeNotFinite : CodeNotANumber, cerr, cat);
                        return "x";
                    }
                    if (v.Value < 0) { Fail(s, CodeUnknownValue, cat + " must not be below zero.", cat); return "x"; }
                    r.CoverOffsetMm = v;
                }
                if (ReadMarkAndRequired(s, o, at, out r.Mark, out r.Required) != null) return "x";
                s.FabricRules.Add(r);
            }
            return null;
        }

        private static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static StructuralRequirementSet Fail(StructuralRequirementSet s, string code, string message)
        {
            return Fail(s, code, message, null);
        }

        private static StructuralRequirementSet Fail(StructuralRequirementSet s, string code, string message,
                                                     string path)
        {
            s.Code = code;
            s.Error = message;
            s.Path = path;
            // A REFUSED SET CARRIES NO RULES. A caller that reads RebarRules without
            // checking Ok must not find a plausible half of somebody's reinforcement.
            s.CoverRules.Clear();
            s.StirrupZoneRules.Clear();
            s.MatRules.Clear();
            s.RebarRules.Clear();
            s.SectionRules.Clear();
            s.WallBoundaryRules.Clear();
            s.AreaRules.Clear();
            s.PathRules.Clear();
            s.FabricRules.Clear();
            s.Clearances = new StructuralClearances();
            s.CrossingOrder = null;
            return s;
        }

        private static string Show(string s)
        {
            return s == null ? "null" : "'" + s + "'";
        }

        /// <summary>
        /// The same document with every object's properties in ordinal order.
        /// ARRAYS ARE LEFT ALONE: the order of rules and of curve points is part of
        /// what the set says, and sorting them would make two different documents
        /// hash the same.
        /// </summary>
        private static JToken Canonical(JToken t)
        {
            var o = t as JObject;
            if (o != null)
            {
                var sorted = new JObject();
                foreach (JProperty prop in o.Properties().OrderBy(x => x.Name, StringComparer.Ordinal))
                    sorted[prop.Name] = Canonical(prop.Value);
                return sorted;
            }
            var a = t as JArray;
            if (a != null)
            {
                var copy = new JArray();
                foreach (JToken item in a) copy.Add(Canonical(item));
                return copy;
            }
            return t;
        }

        /// <summary>
        /// A stable digest of the whole artefact, for provenance and for the audit.
        ///
        /// CANONICAL, because the digest decides whether a bar is reported as built
        /// from a DIFFERENT version of the set. Hashing the document as written made
        /// that a function of property ORDER: reordering two keys in an editor, with
        /// no change to a single number, marked every bar in the model stale.
        /// </summary>
        public static string Sha256Of(JObject doc)
        {
            string canonical = doc == null ? "" : Canonical(doc).ToString(Newtonsoft.Json.Formatting.None);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
                var sb = new System.Text.StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
