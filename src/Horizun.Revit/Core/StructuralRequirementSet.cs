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

        public StructuralMatRule ShallowCopy() => (StructuralMatRule)MemberwiseClone();
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
}
