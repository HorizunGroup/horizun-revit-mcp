// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// FROM LINES TO A NETWORK: the part that makes MEP from a DWG worth doing.
//
// A pipe created from A to B and another created from B to C are two pipes that
// happen to touch. Revit does not think they are connected, no system flows
// through them, nothing sizes, nothing schedules, and a coordination model built
// that way is a 3D picture of a 2D drawing. The difference between that and a
// model is entirely in this file: WHICH ends meet, WHAT fitting belongs there,
// and — the half that is usually skipped — WHICH apparent meetings are not
// meetings at all.
//
// FOUR THINGS IT REFUSES TO GUESS.
//
//   A CROSSING IS NOT A CONNECTION. Two lines crossing in a plan with no shared
//   endpoint are, almost always, two services at different elevations. Joining
//   them produces a tee nobody drew, in a model that then routes waste through a
//   water main. Every crossing is reported, and NONE is connected.
//
//   A GAP IS EITHER CLOSED OR IT IS A QUESTION. Ends inside the connect
//   tolerance are one node - that is what the tolerance is for, and the caller
//   declared it. Ends beyond it are an unresolved gap with the distance
//   measured, never quietly bridged, because the distance between "sloppy
//   drafting" and "these are deliberately separate systems" is a judgement about
//   the building.
//
//   ELEVATION IS NOT ZERO, IT IS UNKNOWN. A plan is drawn flat. Two runs meeting
//   at a node in plan are at the same place only if something said so. When the
//   caller declares elevations and they differ, this is a RISER or a mistake,
//   and either way it is not an elbow.
//
//   A JUNCTION REVIT CANNOT BUILD IS NOT ROUNDED DOWN. Five runs at one point is
//   not a cross with one ignored. It is refused, named, and left for a person.
//
// WHAT COMES OUT is a network of straight RUNS - one per MEP curve Revit will
// create - the JUNCTIONS between them, and the CONNECTION INTENTS that say which
// connector meets which and through what fitting. Nothing here touches Revit;
// the intents are carried out by the typed commands, which rehearse and re-read.
//
// Revit-free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a junction IS, from the closed list of things Revit can build there.</summary>
    public static class CadJunctionKind
    {
        /// <summary>One run ends here and nothing continues. A fixture, a piece of equipment, or a drawing that stops.</summary>
        public const string Terminal = "terminal";
        /// <summary>Two runs at an angle. An elbow.</summary>
        public const string Elbow = "elbow";
        /// <summary>Two runs continuing straight. They should have been ONE run; the drawing split them.</summary>
        public const string Collinear = "collinear_pair";
        /// <summary>Three runs: two through, one branching. A tee.</summary>
        public const string Tee = "tee";
        /// <summary>Four runs in two straight pairs. A cross.</summary>
        public const string Cross = "cross";
        /// <summary>Three or four runs in an arrangement that is none of the above. A person decides.</summary>
        public const string Irregular = "irregular";
        /// <summary>More runs than any fitting takes. Refused rather than rounded down.</summary>
        public const string Unsupported = "unsupported_degree";
        /// <summary>The runs meeting here were declared at different elevations. A riser, or a mistake.</summary>
        public const string ElevationChange = "elevation_change";

        public static readonly string[] All =
        {
            Terminal, Elbow, Collinear, Tee, Cross, Irregular, Unsupported, ElevationChange
        };
    }

    /// <summary>One straight length of service: exactly what Revit will make one MEP curve from.</summary>
    public sealed class CadRun
    {
        public string Id;
        public string Layer;
        public CadPoint Start;
        public CadPoint End;

        /// <summary>The IR entities this run consumed, so provenance can name what it was built from.</summary>
        public List<string> SourceEntityIds = new List<string>();

        /// <summary>How many drawn segments were merged into this one straight run.</summary>
        public int MergedSegments = 1;

        /// <summary>What the drawn thing was: a line, or a chord of an arc or spline.</summary>
        public CadCurveKind SourceKind = CadCurveKind.Line;

        /// <summary>
        /// True when this run is a CHORD of a curve rather than something drawn
        /// straight.
        ///
        /// A run that IS a curve - one whose arc was handed to this reading - is
        /// not a chord of one, and this is false for it.
        ///
        /// It stays true for a chord whose arc nobody supplied, and it matters:
        /// a curve chorded to a declared sagitta turns at every chord, so the
        /// reading gives a curved duct as N straight runs with N-1 elbows between
        /// them - within tolerance geometrically, and a piece of ductwork nobody
        /// would fabricate. The reader keeps the arcs beside the chords; pass them
        /// to <see cref="CadNetworkRules.Build(IList{CadSegment}, CadNetworkOptions,
        /// Func{string, CadNetworkRules.CadRunDeclaration}, IList{CadArcFact})"/>
        /// and the curve becomes one run.
        /// </summary>
        public bool IsChordOfACurve =>
            Arc == null && (SourceKind == CadCurveKind.Arc || SourceKind == CadCurveKind.Spline);

        /// <summary>
        /// WHAT THIS RUN IS, in the same scheme the conversion stamps on what it
        /// builds - so a run and the element built from it have the same name.
        ///
        /// CadInterpretationRules.FromSingleLines merges collinear segments and
        /// names the result with CadIdentity.SemanticId(layer, "root", kind,
        /// {A, B}, tolerance). This does the same merge; running the same
        /// identity over it is what turns "which element is this run" from a
        /// research problem into a dictionary lookup against the provenance the
        /// apply already wrote.
        ///
        /// IT ONLY AGREES WHEN THE TOLERANCES AGREE, which is why the tolerance
        /// travels with the id and the network says whether it matched the
        /// requirement set's.
        /// </summary>
        public string GeometryId;
        public string SemanticId;

        /// <summary>The tolerance the two ids above were computed at.</summary>
        public double IdentityToleranceMm;

        /// <summary>
        /// The ARC this run is, when it is one. Null for something drawn straight,
        /// and null is not "radius zero" - it means the run is a line.
        ///
        /// Start and End are still the two ends, so everything that only needs
        /// endpoints keeps working; this is what lets a curved run be ONE run
        /// rather than one per chord.
        /// </summary>
        public CadArcFact Arc;

        /// <summary>The node key at each end, after snapping.</summary>
        public string StartNode;
        public string EndNode;

        /// <summary>The bore or size the requirement set declared for this layer. Null when nobody said.</summary>
        public double? DiameterMm;

        /// <summary>The system the requirement set declared for this layer. Null when nobody said.</summary>
        public string SystemType;

        /// <summary>
        /// The fall the requirement set declared, as a percentage. Null when it
        /// declared none - which is NOT zero: a drain laid level is a drain that
        /// does not drain, and the fall walk stops at an undeclared run rather
        /// than assuming one.
        /// </summary>
        public double? SlopePercent;

        /// <summary>The elevation the requirement set declared, in mm. Null when nobody said - NOT zero.</summary>
        public double? ElevationMm;

        /// <summary>
        /// The length of the run. For a curve that is the ARC LENGTH, not the
        /// chord: a quarter-circle of radius 300 is 471 mm of pipe and its chord is
        /// 424 mm, and a schedule built on the chord is short by eleven per cent.
        /// </summary>
        public double LengthMm =>
            Arc != null ? Arc.RadiusMm * Arc.SweepRadians : Start.PlanDistanceTo(End);

        public CadPoint DirectionAt(string nodeKey)
        {
            bool atStart = nodeKey == StartNode;

            // ON A CURVE THE DIRECTION IS THE TANGENT, not the chord.
            //
            // A junction between a curve and a straight run is classified by the
            // angle between them, and the chord of a quarter-circle points 45
            // degrees away from where the pipe actually leaves. Reading the chord
            // turns a tangential meeting into an elbow and an elbow into a tee's
            // branch.
            if (Arc != null)
            {
                CadPoint at = atStart ? Arc.Start : Arc.End;
                double rx = at.X - Arc.Centre.X, ry = at.Y - Arc.Centre.Y;
                double radius = Math.Sqrt(rx * rx + ry * ry);
                if (radius > 0)
                {
                    // The tangent is perpendicular to the radius; which of the two
                    // perpendiculars points AWAY from this end along the arc
                    // depends on the sweep direction and on which end we are at.
                    double tx = -ry / radius, ty = rx / radius;
                    double sign = Arc.Clockwise ? -1.0 : 1.0;
                    if (!atStart) sign = -sign;
                    return new CadPoint(tx * sign, ty * sign, 0);
                }
            }

            // The direction pointing AWAY from the given end, unit length in plan.
            CadPoint from = atStart ? Start : End;
            CadPoint to = atStart ? End : Start;
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return new CadPoint(0, 0, 0);
            return new CadPoint(dx / len, dy / len, 0);
        }

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["layer"] = Layer,
            ["start"] = new JArray(R(Start.X), R(Start.Y), R(Start.Z)),
            ["end"] = new JArray(R(End.X), R(End.Y), R(End.Z)),
            ["start_node"] = StartNode,
            ["end_node"] = EndNode,
            ["length_mm"] = R(LengthMm),
            ["length_is"] = Arc != null
                ? "the ARC length, not the chord - a quarter-circle of radius 300 is 471 mm of pipe and its " +
                  "chord is 424 mm"
                : "the straight distance between the two ends",
            ["arc"] = Arc == null ? (JToken)JValue.CreateNull() : new JObject
            {
                ["centre"] = new JArray(R(Arc.Centre.X), R(Arc.Centre.Y)),
                ["radius_mm"] = R(Arc.RadiusMm),
                ["clockwise"] = Arc.Clockwise,
                ["sweep_degrees"] = R(Arc.SweepRadians * 180.0 / Math.PI)
            },
            ["merged_segments"] = MergedSegments,
            ["source_kind"] = SourceKind.ToString().ToLowerInvariant(),
            ["is_chord_of_a_curve"] = IsChordOfACurve,
            ["geometry_id"] = GeometryId,
            ["semantic_id"] = SemanticId,
            ["identity_tolerance_mm"] = R(IdentityToleranceMm),
            ["diameter_mm"] = DiameterMm.HasValue ? (JToken)R(DiameterMm.Value) : JValue.CreateNull(),
            ["system_type"] = SystemType,
            ["elevation_mm"] = ElevationMm.HasValue ? (JToken)R(ElevationMm.Value) : JValue.CreateNull(),
            ["slope_percent"] = SlopePercent.HasValue ? (JToken)R(SlopePercent.Value) : JValue.CreateNull(),
            ["source_entities"] = new JArray(SourceEntityIds.Select(s => (JToken)s))
        };

        private static double R(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>One run arriving at one junction, and from which direction.</summary>
    public sealed class CadIncidence
    {
        public string RunId;
        /// <summary>start | end - which of the run's two ends is at this junction.</summary>
        public string AtEnd;
        /// <summary>Bearing in degrees of the direction pointing away from the junction along this run.</summary>
        public double BearingDegrees;

        public JObject ToJson() => new JObject
        {
            ["run_id"] = RunId,
            ["at_end"] = AtEnd,
            ["bearing_degrees"] = Math.Round(BearingDegrees, 3, MidpointRounding.AwayFromZero)
        };
    }

    /// <summary>A place where runs meet, what it is, and whether anything may be built there unattended.</summary>
    public sealed class CadJunction
    {
        public string NodeKey;
        public CadPoint Point;
        public string Kind = CadJunctionKind.Irregular;
        public List<CadIncidence> Incident = new List<CadIncidence>();

        /// <summary>For a tee: the run that branches off the through pair.</summary>
        public string BranchRunId;
        /// <summary>For a tee or a cross: the runs that go straight through.</summary>
        public List<string> ThroughRunIds = new List<string>();

        /// <summary>False when a person must look before anything is built here.</summary>
        public bool Automatic;

        /// <summary>The sentence a reviewer reads. Never empty on a junction this file produces.</summary>
        public string Says;

        public int Degree => Incident.Count;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["node"] = NodeKey,
                ["point"] = new JArray(Math.Round(Point.X, 4, MidpointRounding.AwayFromZero),
                                       Math.Round(Point.Y, 4, MidpointRounding.AwayFromZero),
                                       Math.Round(Point.Z, 4, MidpointRounding.AwayFromZero)),
                ["kind"] = Kind,
                ["degree"] = Degree,
                ["automatic"] = Automatic,
                ["says"] = Says,
                ["incident"] = new JArray(Incident.Select(i => (JToken)i.ToJson()))
            };
            if (BranchRunId != null) o["branch_run"] = BranchRunId;
            if (ThroughRunIds.Count > 0) o["through_runs"] = new JArray(ThroughRunIds.Select(s => (JToken)s));
            return o;
        }
    }

    /// <summary>
    /// TWO LINES THAT CROSS AND ARE NOT JOINED.
    ///
    /// Emitted for every crossing found, so that a reviewer sees the ones that
    /// SHOULD have been junctions. Nothing is ever connected from one of these:
    /// in a plan, a crossing is the normal way of drawing two services at
    /// different heights.
    /// </summary>
    public sealed class CadCrossing
    {
        public string RunA;
        public string RunB;
        public CadPoint At;
        public bool SameLayer;
        public double? ElevationGapMm;

        public JObject ToJson() => new JObject
        {
            ["run_a"] = RunA,
            ["run_b"] = RunB,
            ["at"] = new JArray(Math.Round(At.X, 4, MidpointRounding.AwayFromZero),
                                Math.Round(At.Y, 4, MidpointRounding.AwayFromZero),
                                Math.Round(At.Z, 4, MidpointRounding.AwayFromZero)),
            ["same_layer"] = SameLayer,
            ["declared_elevation_gap_mm"] = ElevationGapMm.HasValue
                ? (JToken)Math.Round(ElevationGapMm.Value, 3, MidpointRounding.AwayFromZero)
                : JValue.CreateNull(),
            ["means"] = SameLayer
                ? "two runs on the SAME layer cross without sharing an end. In a plan that is usually two " +
                  "services passing at different heights, and occasionally a junction the draughtsman drew " +
                  "without breaking the line. NOTHING was connected here. If it should be a tee, the drawing " +
                  "has to say so - by breaking the line, or by the caller declaring it."
                : "two runs on different layers cross. Nothing was connected, and nothing about this is " +
                  "unusual: services cross."
        };
    }

    /// <summary>Two ends that nearly meet, and by how much they miss.</summary>
    public sealed class CadGap
    {
        public string RunA, RunB;
        public string EndA, EndB;
        public CadPoint AtA, AtB;
        public double DistanceMm;

        public JObject ToJson() => new JObject
        {
            ["run_a"] = RunA,
            ["end_a"] = EndA,
            ["run_b"] = RunB,
            ["end_b"] = EndB,
            ["distance_mm"] = Math.Round(DistanceMm, 3, MidpointRounding.AwayFromZero),
            ["at_a"] = new JArray(Math.Round(AtA.X, 4, MidpointRounding.AwayFromZero),
                                  Math.Round(AtA.Y, 4, MidpointRounding.AwayFromZero)),
            ["at_b"] = new JArray(Math.Round(AtB.X, 4, MidpointRounding.AwayFromZero),
                                  Math.Round(AtB.Y, 4, MidpointRounding.AwayFromZero)),
            ["means"] = "these two ends are further apart than the connect tolerance and closer than the " +
                        "review distance. They were NOT joined. Raising the connect tolerance would join " +
                        "them and would also join every other pair this close - which is the decision, and " +
                        "it belongs to whoever knows the drawing."
        };
    }

    /// <summary>
    /// ONE CONNECTION TO MAKE: which two run ends meet, and through what.
    ///
    /// This is an INTENT, not a result. Carrying it out means finding the two
    /// elements' connectors in the model and joining them, which is Revit's job
    /// and happens in the command that has the document open.
    /// </summary>
    public sealed class CadConnectionIntent
    {
        public string JunctionNode;
        /// <summary>direct | elbow | tee | cross - what Revit is asked to place, if anything.</summary>
        public string Fitting;
        public List<CadIncidence> Participants = new List<CadIncidence>();
        public bool Automatic;
        public string Says;

        /// <summary>Where the junction is, in millimetres. What tells one end of a run from the other.</summary>
        public CadPoint At;

        /// <summary>
        /// The participating runs' SEMANTIC IDS, in the order the fitting takes
        /// them: for a tee, the two through-run members first and then the branch.
        ///
        /// This is what makes the reading's output the command's input. Without it
        /// somebody has to translate between two replies by hand, on the one step
        /// where a mistake joins the wrong two pipes invisibly.
        /// </summary>
        public List<string> MemberSemanticIds = new List<string>();

        public JObject ToJson()
        {
            var o = new JObject
            {
                // `id` and `elements` are the shape horizun_cad_connect consumes, so
                // this array can be sent to it unchanged.
                ["id"] = JunctionNode,
                ["junction_node"] = JunctionNode,
                ["fitting"] = Fitting,
                ["automatic"] = Automatic,
                ["says"] = Says,
                ["at"] = new JArray(Math.Round(At.X, 4, MidpointRounding.AwayFromZero),
                                    Math.Round(At.Y, 4, MidpointRounding.AwayFromZero),
                                    Math.Round(At.Z, 4, MidpointRounding.AwayFromZero)),
                ["elements"] = new JArray(MemberSemanticIds
                    .Select(id => (JToken)new JObject { ["semantic_id"] = id })),
                ["participants"] = new JArray(Participants.Select(p => (JToken)p.ToJson()))
            };
            o["sendable"] = MemberSemanticIds.Count == Participants.Count &&
                            MemberSemanticIds.All(x => !string.IsNullOrEmpty(x));
            o["sendable_means"] = (bool)o["sendable"]
                ? "this entry can be sent to horizun_cad_connect unchanged, provided the network reading " +
                  "reported run_identity.matches_the_conversion - otherwise these ids name nothing in the model."
                : "a participating run has no semantic id, so this entry cannot be sent as it stands.";
            return o;
        }
    }

    /// <summary>A connected set of runs: one network as the drawing shows it.</summary>
    public sealed class CadNetworkComponent
    {
        public string Id;
        public List<string> RunIds = new List<string>();
        public List<string> TerminalNodes = new List<string>();
        public double TotalLengthMm;
        /// <summary>Distinct system types declared across this component. More than one is a conflict.</summary>
        public List<string> DeclaredSystems = new List<string>();

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["run_count"] = RunIds.Count,
            ["runs"] = new JArray(RunIds.Select(s => (JToken)s)),
            ["terminal_nodes"] = new JArray(TerminalNodes.Select(s => (JToken)s)),
            ["terminal_count"] = TerminalNodes.Count,
            ["total_length_mm"] = Math.Round(TotalLengthMm, 3, MidpointRounding.AwayFromZero),
            ["declared_systems"] = new JArray(DeclaredSystems.Select(s => (JToken)s)),
            ["system_conflict"] = DeclaredSystems.Count > 1,
            ["means"] = DeclaredSystems.Count > 1
                ? "runs in ONE connected network were declared as different systems. Revit cannot carry two " +
                  "systems through one connected run, so either the rules are wrong or the drawing joins two " +
                  "things that are separate. NOT resolved here."
                : TerminalNodes.Count == 0
                    ? "this network has no open end at all - it closes on itself. A ring main is legitimate; " +
                      "so is a drafting loop that is not a service. Nothing was assumed."
                    : "a connected network with " +
                      TerminalNodes.Count.ToString(CultureInfo.InvariantCulture) + " open ends"
        };
    }

    /// <summary>The tolerances a network reading runs under. Every one is the caller's, none is chosen here.</summary>
    public sealed class CadNetworkOptions
    {
        /// <summary>Ends this close become ONE node. The declared meaning of "these are joined".</summary>
        public double ConnectToleranceMm = 1.0;

        /// <summary>
        /// Ends further apart than the connect tolerance but closer than this are
        /// reported as gaps for review. Beyond it, two ends are simply two ends.
        /// </summary>
        public double GapReviewDistanceMm = 50.0;

        /// <summary>Direction change below this is "straight". Above it, an elbow.</summary>
        public double CollinearToleranceDegrees = 2.0;

        /// <summary>
        /// At a tee, how close to opposite the through pair must be before the
        /// odd one out is called the branch. A junction that fails this is
        /// irregular rather than a tee with a guessed branch.
        /// </summary>
        public double ThroughToleranceDegrees = 15.0;

        /// <summary>Do not look for crossings once the run count passes this - the check is quadratic.</summary>
        public int CrossingCheckLimit = 4000;

        /// <summary>
        /// The most segments-times-runs of work source attribution may cost before
        /// it is skipped and SAID to be skipped.
        ///
        /// Attribution is what lets a converted element name the drawn entities it
        /// came from, and it is worth real time - but not unbounded time, and a
        /// network reading that takes ten minutes on a permit drawing is one nobody
        /// runs twice.
        /// </summary>
        public long AttributionWorkLimit = 50000000L;

        /// <summary>
        /// The tolerance the run identities are computed at. 0 means "the connect
        /// tolerance", which is right when nothing else has an opinion.
        ///
        /// It is separate from the connect tolerance because the identity has to
        /// match the CONVERSION's point tolerance to be useful, and a caller may
        /// legitimately want to snap ends harder than the conversion does while
        /// still naming runs the way the conversion will.
        /// </summary>
        public double IdentityToleranceMm;

        public double IdentityTolerance => IdentityToleranceMm > 0 ? IdentityToleranceMm : ConnectToleranceMm;

        public JObject ToJson() => new JObject
        {
            ["connect_tolerance_mm"] = ConnectToleranceMm,
            ["gap_review_distance_mm"] = GapReviewDistanceMm,
            ["collinear_tolerance_degrees"] = CollinearToleranceDegrees,
            ["through_tolerance_degrees"] = ThroughToleranceDegrees,
            ["crossing_check_limit"] = CrossingCheckLimit,
            ["attribution_work_limit"] = AttributionWorkLimit,
            ["identity_tolerance_mm"] = IdentityTolerance
        };
    }

    /// <summary>Everything one network reading found, including what it refused to decide.</summary>
    public sealed class CadNetwork
    {
        public List<CadRun> Runs = new List<CadRun>();
        public List<CadJunction> Junctions = new List<CadJunction>();
        public List<CadConnectionIntent> Connections = new List<CadConnectionIntent>();
        public List<CadCrossing> Crossings = new List<CadCrossing>();
        public List<CadGap> Gaps = new List<CadGap>();
        public List<CadNetworkComponent> Components = new List<CadNetworkComponent>();
        public CadNetworkOptions Options = new CadNetworkOptions();

        /// <summary>How many drawn segments were handed to this reading.</summary>
        public int SegmentsGiven;

        /// <summary>
        /// How many of them the collinear merge absorbed into a neighbour.
        ///
        /// It is the difference between what the drawing contains and what anyone
        /// would build from it: a straight main drawn in forty pieces is one pipe,
        /// and thirty-nine of those pieces are an artefact of drafting.
        /// </summary>
        public int SegmentsMergedAway;

        /// <summary>True when the crossing check was skipped because there were too many runs.</summary>
        public bool CrossingCheckSkipped;

        /// <summary>True when source attribution was skipped because the work exceeded its stated bound.</summary>
        public bool AttributionSkipped;

        /// <summary>
        /// Merged segments too short to be runs, with where each one was.
        ///
        /// Recorded rather than dropped: a run that disappears takes a degree off
        /// the node it would have reached, and a tee that reads as an elbow is a
        /// fitting nobody drew.
        /// </summary>
        public List<JObject> DroppedShortRuns = new List<JObject>();

        public IEnumerable<CadJunction> Terminals =>
            Junctions.Where(j => j.Kind == CadJunctionKind.Terminal);

        public IEnumerable<CadJunction> NeedingReview =>
            Junctions.Where(j => !j.Automatic);

        public JObject ToJson() => new JObject
        {
            ["options"] = Options.ToJson(),
            ["run_count"] = Runs.Count,
            ["runs"] = new JArray(Runs.Select(r => (JToken)r.ToJson())),
            ["junctions"] = new JArray(Junctions.Select(j => (JToken)j.ToJson())),
            ["connections"] = new JArray(Connections.Select(c => (JToken)c.ToJson())),
            ["crossings"] = new JArray(Crossings.Select(c => (JToken)c.ToJson())),
            ["gaps"] = new JArray(Gaps.Select(g => (JToken)g.ToJson())),
            ["components"] = new JArray(Components.Select(c => (JToken)c.ToJson())),
            ["dropped_short_runs"] = new JArray(DroppedShortRuns),
            ["summary"] = SummaryJson()
        };

        public JObject SummaryJson()
        {
            var byKind = new JObject();
            foreach (var g in Junctions.GroupBy(j => j.Kind, StringComparer.Ordinal)
                                       .OrderByDescending(g => g.Count()))
                byKind[g.Key] = g.Count();

            int auto = Connections.Count(c => c.Automatic);
            return new JObject
            {
                ["runs"] = Runs.Count,
                ["segments_given"] = SegmentsGiven,
                ["segments_merged_away"] = SegmentsMergedAway,
                ["merge_means"] = SegmentsMergedAway == 0
                    ? "no segment continued straight through a degree-two node; every run is one drawn piece"
                    : "a straight main drawn in forty pieces is ONE pipe, and thirty-nine of those pieces " +
                      "are an artefact of drafting rather than a fact about the building. This is how many " +
                      "of them were absorbed.",
                ["total_length_mm"] = Math.Round(Runs.Sum(r => r.LengthMm), 3, MidpointRounding.AwayFromZero),
                ["junctions_by_kind"] = byKind,
                ["connections_total"] = Connections.Count,
                ["connections_automatic"] = auto,
                ["connections_needing_review"] = Connections.Count - auto,
                ["curved_runs"] = Runs.Count(r => r.Arc != null),
                ["runs_that_are_chords_of_a_curve"] = Runs.Count(r => r.IsChordOfACurve),
                ["chords_mean"] = Runs.Any(r => r.IsChordOfACurve)
                    ? "THESE RUNS ARE CHORDS OF A CURVE WHOSE ARC NOBODY SUPPLIED, so this reading gives it " +
                      "as a chain of straight runs with elbows between them - within tolerance " +
                      "geometrically, and a piece of pipework nobody would fabricate. Pass the arcs the " +
                      "reader kept beside the chords and each curve becomes ONE run, measured along its arc."
                    : Runs.Any(r => r.Arc != null)
                        ? "every curve in this reading is one run carrying its arc, measured along the arc " +
                          "rather than across its chord"
                        : "nothing in this reading is a curve or a chord of one; every run was drawn straight",
                ["crossings_not_connected"] = Crossings.Count,
                ["crossing_check_skipped"] = CrossingCheckSkipped,
                ["dropped_short_runs"] = DroppedShortRuns.Count,
                ["dropped_short_runs_mean"] = DroppedShortRuns.Count == 0
                    ? "nothing the merge produced was too short to be a run"
                    : "merged segments shorter than the connect tolerance. Each one takes a degree off the " +
                      "node it would have reached, so a junction near one of these may be classified with " +
                      "one fewer run than the drawing shows - which turns a tee into an elbow.",
                ["source_attribution_skipped"] = AttributionSkipped,
                ["source_attribution_means"] = AttributionSkipped
                    ? "matching drawn entities to runs was SKIPPED: segments times runs exceeded the stated " +
                      "bound. Every run's source_entities is empty and merged_segments is zero because " +
                      "nothing looked, not because nothing was found."
                    : "every run names the drawn entities it was merged from",
                ["unresolved_gaps"] = Gaps.Count,
                ["components"] = Components.Count,
                ["components_with_system_conflict"] = Components.Count(c => c.DeclaredSystems.Count > 1),
                ["means"] =
                    "connections_automatic is what an unattended run would build. Everything in " +
                    "connections_needing_review, crossings_not_connected and unresolved_gaps is deliberately " +
                    "NOT built: each one is a place where two readings of the drawing are both defensible."
            };
        }
    }

    public static class CadNetworkRules
    {
        /// <summary>
        /// Read a set of segments as a network of straight runs and the junctions
        /// between them.
        ///
        /// <paramref name="declare"/> is asked, per layer, for the system, bore
        /// and elevation the requirement set gives that layer. It may return
        /// nulls, and null travels: a run with no declared elevation is a run
        /// whose height nobody stated, which is different from one at zero.
        /// </summary>
        public static CadNetwork Build(IList<CadSegment> segments, CadNetworkOptions options,
                                       Func<string, CadRunDeclaration> declare = null)
            => Build(segments, options, declare, null);

        /// <summary>
        /// <paramref name="arcs"/> are the curves the reader kept AS curves, beside
        /// the chords it also produced. Their chords are taken out of the straight
        /// merge and each arc becomes ONE run - otherwise a curve chorded to the
        /// declared sagitta turns at every chord and comes out as N straight runs
        /// with N-1 elbows between them, which is a piece of pipework nobody would
        /// fabricate.
        ///
        /// Passing null reads the chords as straight runs and SAYS so, which is the
        /// honest reading for a caller who has no arcs to give.
        /// </summary>
        public static CadNetwork Build(IList<CadSegment> segments, CadNetworkOptions options,
                                       Func<string, CadRunDeclaration> declare,
                                       IList<CadArcFact> arcs)
        {
            var net = new CadNetwork { Options = options ?? new CadNetworkOptions() };
            if (segments == null || segments.Count == 0) return net;
            CadNetworkOptions opt = net.Options;

            // ---- 1. straight runs ------------------------------------------------
            //
            // A polyline drawn as forty collinear pieces is ONE pipe. A polyline
            // that turns is several pipes and an elbow at each turn - which is
            // exactly what MergeCollinear leaves behind, because it merges
            // THROUGH a degree-two node only while the direction holds.
            // THE CHORDS OF AN ARC ARE NOT STRAIGHT RUNS. They are one curve, and
            // they come out of the merge before it starts.
            var arcByCurve = new Dictionary<string, CadArcFact>(StringComparer.Ordinal);
            foreach (CadArcFact a in arcs ?? new List<CadArcFact>())
                if (a != null && a.CurveId != null) arcByCurve[a.CurveId] = a;

            List<CadSegment> lineWork = arcByCurve.Count == 0
                ? new List<CadSegment>(segments)
                : segments.Where(x => x == null || x.SourceCurveId == null ||
                                      !arcByCurve.ContainsKey(x.SourceCurveId)).ToList();

            int mergedAway;
            List<CadSegment> straight = CadTopologyRules.MergeCollinear(
                lineWork, opt.ConnectToleranceMm, opt.CollinearToleranceDegrees, out mergedAway);

            // WHICH DRAWN ENTITIES EACH RUN CAME FROM, attributed by CONTAINMENT
            // rather than by matching endpoints.
            //
            // The first version of this keyed the original segments by their two
            // ends and looked the merged run up by ITS two ends. A run that was
            // merged from forty pieces has the ends of the CHAIN, which are the
            // ends of the first and last piece and of no single segment - so the
            // lookup missed on every run that merging actually helped, and the
            // provenance of the long runs, the ones most worth tracing, came back
            // empty while the short ones looked fine.
            var byLayer = new Dictionary<string, List<CadSegment>>(StringComparer.OrdinalIgnoreCase);
            foreach (CadSegment s in segments)
            {
                if (s == null || s.SourceCurveId == null) continue;
                string layerKey = s.Layer ?? "";
                List<CadSegment> bucket;
                if (!byLayer.TryGetValue(layerKey, out bucket))
                    byLayer[layerKey] = bucket = new List<CadSegment>();
                bucket.Add(s);
            }

            // A STATED BOUND ON ATTRIBUTION. Matching every drawn segment to the run
            // that contains it is work proportional to segments times runs, and on a
            // permit drawing both are large. Past the bound it is SKIPPED and said,
            // rather than quietly turning a network reading into a ten-minute one.
            net.SegmentsGiven = segments.Count;
            net.SegmentsMergedAway = mergedAway;

            long attributionWork = (long)lineWork.Count * Math.Max(1, straight.Count);
            bool attribute = attributionWork <= opt.AttributionWorkLimit;
            net.AttributionSkipped = !attribute;

            for (int i = 0; i < straight.Count; i++)
            {
                CadSegment s = straight[i];
                if (s == null) continue;

                // A RUN SHORTER THAN THE CONNECT TOLERANCE IS NOT NOTHING.
                //
                // It used to be skipped silently, and the cost is not the run: the
                // node it would have reached loses a degree, so a tee reads as an
                // elbow and the reading proposes a fitting the drawing does not
                // show - with nothing saying a run went missing.
                if (s.PlanLength <= opt.ConnectToleranceMm)
                {
                    net.DroppedShortRuns.Add(new JObject
                    {
                        ["layer"] = s.Layer,
                        ["length_mm"] = Math.Round(s.PlanLength, 4, MidpointRounding.AwayFromZero),
                        ["at"] = new JArray(Math.Round(s.A.X, 4, MidpointRounding.AwayFromZero),
                                            Math.Round(s.A.Y, 4, MidpointRounding.AwayFromZero)),
                        ["means"] = "shorter than the connect tolerance, so its two ends are ONE node and it " +
                                    "cannot be a run. It is not built and it does not count toward any " +
                                    "junction's degree - which can turn a tee into an elbow, so the drop is " +
                                    "recorded rather than assumed harmless."
                    });
                    continue;
                }
                var run = new CadRun
                {
                    Id = "r" + (net.Runs.Count + 1).ToString(CultureInfo.InvariantCulture),
                    Layer = s.Layer,
                    Start = s.A,
                    End = s.B,
                    SourceKind = s.SourceKind,
                    IdentityToleranceMm = opt.IdentityTolerance
                };

                // THE SAME NAME THE CONVERSION WILL GIVE IT. "root" is the instance
                // path the interpreter uses for model-level geometry, and passing
                // anything else here would make two names for one thing.
                var ends = new List<CadPoint> { s.A, s.B };
                run.GeometryId = CadIdentity.GeometryId(s.SourceKind, ends, opt.IdentityTolerance);
                run.SemanticId = CadIdentity.SemanticId(s.Layer, "root", s.SourceKind, ends,
                                                        opt.IdentityTolerance);
                if (attribute)
                {
                    int counted;
                    run.SourceEntityIds.AddRange(
                        SourcesOn(run, byLayer, opt.ConnectToleranceMm, out counted));
                    // THE MEASURED COUNT, INCLUDING ZERO. Forcing it to at least one
                    // made an unattributed run look attributed.
                    run.MergedSegments = counted;
                }
                else
                {
                    run.MergedSegments = 0;
                }

                CadRunDeclaration d = declare == null ? null : declare(s.Layer);
                if (d != null)
                {
                    run.SystemType = d.SystemType;
                    run.DiameterMm = d.DiameterMm;
                    run.ElevationMm = d.ElevationMm;
                    run.SlopePercent = d.SlopePercent;
                }
                net.Runs.Add(run);
            }

            // ---- one run per arc -------------------------------------------------
            foreach (var kv in arcByCurve.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                CadArcFact a = kv.Value;
                if (a.RadiusMm <= 0) continue;

                var run = new CadRun
                {
                    Id = "r" + (net.Runs.Count + 1).ToString(CultureInfo.InvariantCulture),
                    Layer = a.Layer,
                    Start = a.Start,
                    End = a.End,
                    Arc = a,
                    SourceKind = CadCurveKind.Arc,
                    IdentityToleranceMm = opt.IdentityTolerance,
                    MergedSegments = a.ChordCount
                };

                // THE IDENTITY MUST KNOW IT IS AN ARC. Two arcs can share both ends
                // - a minor and a major arc of one chord, or two different radii -
                // and an id taken over the endpoints collides between them, so an
                // audit would match an element to the wrong drawing entity. This is
                // the same function CadInterpretationRules uses for a curved wall.
                run.GeometryId = CadIdentity.ArcGeometryId(a.Centre, a.RadiusMm, a.Start, a.End,
                                                           a.Clockwise, opt.IdentityTolerance);
                run.SemanticId = CadIdentity.SemanticIdOf(a.Layer, "root", run.GeometryId);
                run.SourceEntityIds.Add(a.CurveId);

                CadRunDeclaration declared = declare == null ? null : declare(a.Layer);
                if (declared != null)
                {
                    run.SystemType = declared.SystemType;
                    run.DiameterMm = declared.DiameterMm;
                    run.ElevationMm = declared.ElevationMm;
                    run.SlopePercent = declared.SlopePercent;
                }
                net.Runs.Add(run);
            }

            if (net.Runs.Count == 0) return net;

            // ---- 2. the node graph over the RUN ends -----------------------------
            var index = new CadNodeIndex(opt.ConnectToleranceMm);
            for (int i = 0; i < net.Runs.Count; i++)
            {
                net.Runs[i].StartNode = index.Add(net.Runs[i].Start, i).Key;
                net.Runs[i].EndNode = index.Add(net.Runs[i].End, i).Key;
            }

            var incidenceByNode = new Dictionary<string, List<CadIncidence>>(StringComparer.Ordinal);
            var pointByNode = new Dictionary<string, CadPoint>(StringComparer.Ordinal);
            foreach (CadNode node in index.Nodes) pointByNode[node.Key] = node.Point;

            foreach (CadRun r in net.Runs)
            {
                Add(incidenceByNode, r.StartNode, new CadIncidence
                {
                    RunId = r.Id,
                    AtEnd = "start",
                    BearingDegrees = Bearing(r.DirectionAt(r.StartNode))
                });
                Add(incidenceByNode, r.EndNode, new CadIncidence
                {
                    RunId = r.Id,
                    AtEnd = "end",
                    BearingDegrees = Bearing(r.DirectionAt(r.EndNode))
                });
            }

            var runById = net.Runs.ToDictionary(r => r.Id, StringComparer.Ordinal);

            // ---- 3. classify every junction --------------------------------------
            foreach (var kv in incidenceByNode.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                CadPoint at;
                pointByNode.TryGetValue(kv.Key, out at);
                CadJunction j = Classify(kv.Key, at, kv.Value, runById, opt);
                net.Junctions.Add(j);

                CadConnectionIntent intent = IntentFor(j, runById);
                if (intent != null) net.Connections.Add(intent);
            }

            // ---- 4. the gaps that were NOT closed --------------------------------
            net.Gaps.AddRange(FindGaps(net.Runs, index, opt));

            // ---- 5. the crossings that were NOT connected ------------------------
            if (net.Runs.Count > opt.CrossingCheckLimit) net.CrossingCheckSkipped = true;
            else net.Crossings.AddRange(FindCrossings(net.Runs, opt));

            // ---- 6. connected components -----------------------------------------
            net.Components.AddRange(Components(net, incidenceByNode));
            return net;
        }

        /// <summary>What a requirement set says about one layer's runs. Every field may be null.</summary>
        public sealed class CadRunDeclaration
        {
            public string SystemType;
            public double? DiameterMm;
            public double? ElevationMm;

            /// <summary>
            /// The fall the rule declares, as a percentage. Null when it declares
            /// none - which is different from zero, and the difference decides
            /// whether a fall walk stops at this run or lays it level.
            /// </summary>
            public double? SlopePercent;

            /// <summary>Where this declaration came from, so a disagreement can name both sides.</summary>
            public string Source;

            /// <summary>The rule that produced it, when it came from a requirement set.</summary>
            public string RuleId;

            public bool SameAs(CadRunDeclaration other)
            {
                if (other == null) return false;
                return string.Equals(SystemType ?? "", other.SystemType ?? "", StringComparison.Ordinal)
                    && Near(DiameterMm, other.DiameterMm)
                    && Near(ElevationMm, other.ElevationMm)
                    && Near(SlopePercent, other.SlopePercent);
            }

            private static bool Near(double? a, double? b)
            {
                if (!a.HasValue && !b.HasValue) return true;
                if (!a.HasValue || !b.HasValue) return false;
                return Math.Abs(a.Value - b.Value) <= 1e-6;
            }

            public JObject ToJson() => new JObject
            {
                ["system_type"] = SystemType,
                ["diameter_mm"] = DiameterMm.HasValue ? (JToken)DiameterMm.Value : JValue.CreateNull(),
                ["elevation_mm"] = ElevationMm.HasValue ? (JToken)ElevationMm.Value : JValue.CreateNull(),
                ["slope_percent"] = SlopePercent.HasValue ? (JToken)SlopePercent.Value : JValue.CreateNull(),
                ["source"] = Source,
                ["rule_id"] = RuleId
            };
        }

        /// <summary>The kinds of thing a network reading is about.</summary>
        private static readonly HashSet<string> MepProduces =
            new HashSet<string>(StringComparer.Ordinal) { "pipe", "duct", "conduit", "cable_tray" };

        /// <summary>
        /// WHAT EACH LAYER IS, DERIVED FROM THE REQUIREMENT SET the conversion will
        /// use — so the network reading and the plan cannot disagree about a layer.
        ///
        /// Two statements about one layer is how a run gets BUILT at one height and
        /// CONNECTED at another, with both replies looking correct because each is
        /// consistent with its own input. The requirement set already carries all
        /// three facts: the system, the bore, and OffsetMm, which is the height
        /// above the storey.
        ///
        /// Precedence is the set's own: higher wins, exactly as the interpreter
        /// resolves competing rules, so a layer matched by two rules resolves the
        /// same way here as it will there.
        /// </summary>
        public static Func<string, CadRunDeclaration> DeclarationsFrom(CadRequirementSet set) =>
            DeclarationsFrom(set, null);

        /// <summary>
        /// <paramref name="ties"/> collects the layers where two MEP rules claim the
        /// layer at EQUAL precedence. Those layers get no declaration: the set's own
        /// resolver returns a tie as a tie rather than a silent winner, and taking
        /// the first would give the run whichever system sorted first by id, in a
        /// reply that looked completely ordinary.
        /// </summary>
        public static Func<string, CadRunDeclaration> DeclarationsFrom(
            CadRequirementSet set, IList<JObject> ties)
        {
            if (set == null) return null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return layer =>
            {
                if (layer == null) return null;

                // The set's OWN matcher and the set's OWN ordering, so a layer
                // resolves here exactly as it will when the conversion runs.
                List<CadRule> matching = set.RulesFor(layer)
                    .Where(r => r != null && r.Produces != null && MepProduces.Contains(r.Produces))
                    .ToList();
                if (matching.Count == 0) return null;

                if (matching.Count > 1 && matching[0].Precedence == matching[1].Precedence)
                {
                    if (ties != null && seen.Add(layer))
                        ties.Add(new JObject
                        {
                            ["layer"] = layer,
                            ["precedence"] = matching[0].Precedence,
                            ["rules"] = new JArray(matching
                                .Where(r => r.Precedence == matching[0].Precedence)
                                .Select(r => (JToken)r.Id)),
                            ["means"] = "two rules claim this layer at the same precedence, so which system, " +
                                        "bore and height its runs have is not decided by the requirement set. " +
                                        "Nothing was declared for it: an undeclared layer produces runs that " +
                                        "cannot be built, which is visible, and a wrongly declared one " +
                                        "produces runs that are built wrong, which is not."
                        });
                    return null;
                }

                CadRule rule = matching[0];
                return new CadRunDeclaration
                {
                    SystemType = rule.SystemType,
                    DiameterMm = rule.DiameterMm,
                    // THE OFFSET IS THE ELEVATION. In a requirement set it is the
                    // height above the storey the rule's level names, which is
                    // exactly what a network reading needs to tell a riser from an
                    // elbow. Null stays null: a rule that declares no offset has not
                    // said the run is on the slab.
                    ElevationMm = rule.OffsetMm,
                    // PARSED, VALIDATED, AND UNTIL NOW DROPPED. A rule that declared
                    // 1% produced a horizontal pipe, in the one discipline where the
                    // slope IS the design.
                    SlopePercent = rule.SlopePercent,
                    Source = "requirement_set",
                    RuleId = rule.Id
                };
            };
        }

        // ---------------------------------------------------------------------
        private static CadJunction Classify(string nodeKey, CadPoint at, List<CadIncidence> incident,
                                            Dictionary<string, CadRun> runs, CadNetworkOptions opt)
        {
            CadJunction junction = ClassifyCore(nodeKey, at, incident, runs, opt);

            // ONE DECLARED HEIGHT AMONG SEVERAL RUNS IS NOT AGREEMENT.
            //
            // The core checks the set of DECLARED elevations and holds when it has
            // more than one value. When one run declares +2400 and another declares
            // nothing, the set has one value and this reads as an ordinary elbow -
            // between a run at 2400 and a run whose height nobody stated.
            //
            // Refusing would be worse: it would hold every junction on a layer the
            // requirement set has not given an offset, which early in a conversion
            // is most of them. So it is classified, and the sentence says which
            // part of it is a guess.
            if (junction.Kind == CadJunctionKind.Elbow || junction.Kind == CadJunctionKind.Collinear ||
                junction.Kind == CadJunctionKind.Tee || junction.Kind == CadJunctionKind.Cross)
            {
                int declaredHere = 0, undeclaredHere = 0;
                foreach (CadIncidence i in incident)
                {
                    CadRun r;
                    if (runs.TryGetValue(i.RunId, out r) && r.ElevationMm.HasValue) declaredHere++;
                    else undeclaredHere++;
                }
                if (declaredHere > 0 && undeclaredHere > 0)
                    junction.Says += " NOTE: " +
                        undeclaredHere.ToString(CultureInfo.InvariantCulture) + " of the " +
                        incident.Count.ToString(CultureInfo.InvariantCulture) +
                        " runs meeting here have no declared height while " +
                        declaredHere.ToString(CultureInfo.InvariantCulture) +
                        " do. They agree in plan and nothing says they agree in the building, so this " +
                        "classification assumes a height for the undeclared ones that nobody stated.";
            }
            return junction;
        }

        private static CadJunction ClassifyCore(string nodeKey, CadPoint at, List<CadIncidence> incident,
                                                Dictionary<string, CadRun> runs, CadNetworkOptions opt)
        {
            var j = new CadJunction { NodeKey = nodeKey, Point = at, Incident = incident };

            // ELEVATION FIRST. Two runs meeting in plan at declared heights that
            // differ do not meet. Checking this before the degree arithmetic is
            // deliberate: an elbow between a run at +2400 and one at +400 is a
            // riser somebody drew as a corner, and calling it an elbow builds a
            // bent pipe through a slab.
            var declared = incident
                .Select(i => { CadRun r; return runs.TryGetValue(i.RunId, out r) ? r : null; })
                .Where(r => r != null && r.ElevationMm.HasValue)
                .Select(r => r.ElevationMm.Value)
                .Distinct()
                .ToList();

            if (declared.Count > 1)
            {
                j.Kind = CadJunctionKind.ElevationChange;
                j.Automatic = false;
                j.Says = "the runs meeting here were declared at " +
                         string.Join(" and ", declared.OrderBy(v => v)
                             .Select(v => v.ToString("0.#", CultureInfo.InvariantCulture) + " mm")) +
                         ". In plan they share a point; in the building they do not. This is a riser, a drop, " +
                         "or a rule that gave one of these layers the wrong height - and none of those is an " +
                         "elbow. Nothing was connected.";
                return j;
            }

            switch (incident.Count)
            {
                case 1:
                    j.Kind = CadJunctionKind.Terminal;
                    j.Automatic = false;
                    j.Says = "one run ends here and nothing continues. In a real network this is a fixture, a " +
                             "piece of equipment, a connection to something on another drawing, or a run the " +
                             "drawing simply stops. Nothing was placed: which of those it is cannot be read " +
                             "from a line that ends.";
                    return j;

                case 2:
                {
                    double between = AngleBetween(incident[0].BearingDegrees, incident[1].BearingDegrees);
                    // Two runs meeting head-on point in OPPOSITE directions when
                    // measured outward, so 180 degrees apart is straight.
                    double deviation = Math.Abs(180.0 - between);
                    if (deviation <= opt.CollinearToleranceDegrees)
                    {
                        j.Kind = CadJunctionKind.Collinear;
                        j.Automatic = true;
                        j.Says = "two runs continue straight through this point (" +
                                 deviation.ToString("0.##", CultureInfo.InvariantCulture) +
                                 " degrees off). The drawing split one straight length into two; joining them " +
                                 "end to end is exact, and no fitting belongs here.";
                        j.ThroughRunIds.Add(incident[0].RunId);
                        j.ThroughRunIds.Add(incident[1].RunId);
                    }
                    else
                    {
                        j.Kind = CadJunctionKind.Elbow;
                        j.Automatic = true;
                        j.Says = "two runs meet at " + between.ToString("0.##", CultureInfo.InvariantCulture) +
                                 " degrees. An elbow, and the angle is the drawing's, not a rounded one.";
                    }
                    return j;
                }

                case 3:
                {
                    Tuple<int, int> through = MostOpposed(incident, opt.ThroughToleranceDegrees);
                    if (through == null)
                    {
                        j.Kind = CadJunctionKind.Irregular;
                        j.Automatic = false;
                        j.Says = "three runs meet here and no two of them are close enough to opposite to be " +
                                 "the through pair (tolerance " +
                                 opt.ThroughToleranceDegrees.ToString("0.#", CultureInfo.InvariantCulture) +
                                 " degrees). Which one is the branch decides which way the fitting faces, and " +
                                 "the geometry does not say. Nothing was placed.";
                        return j;
                    }
                    // THE BRANCH IS THE THIRD INCIDENCE, BY POSITION IN THE LIST - not "the
                    // run whose id is neither of the other two". A run shorter than the
                    // connect tolerance meets its node with BOTH ends, so one id appears
                    // twice among three incidences; picking by id then found nothing and
                    // the whole reading died on an unhandled exception (measured on a real
                    // corridor supply plan whose main backtracks 19.7 mm at its end).
                    int branchIndex = Enumerable.Range(0, incident.Count)
                        .First(k => k != through.Item1 && k != through.Item2);
                    string throughA = incident[through.Item1].RunId, throughB = incident[through.Item2].RunId;
                    string branch = incident[branchIndex].RunId;
                    if (throughA == throughB || branch == throughA || branch == throughB)
                    {
                        j.Kind = CadJunctionKind.Irregular;
                        j.Automatic = false;
                        j.Says = "one run meets this point with both of its ends - it is shorter than the connect " +
                                 "tolerance, which in a drawing is a doubled-back or overdrawn end rather than a " +
                                 "piece of duct or pipe. Three incidences, two runs: no fitting fits that, and " +
                                 "which of them is real is a question for a person. Nothing was placed.";
                        return j;
                    }
                    j.Kind = CadJunctionKind.Tee;
                    j.Automatic = true;
                    j.ThroughRunIds.Add(throughA);
                    j.ThroughRunIds.Add(throughB);
                    j.BranchRunId = branch;
                    j.Says = "three runs: " + j.ThroughRunIds[0] + " and " + j.ThroughRunIds[1] +
                             " run through, " + j.BranchRunId + " branches off. A tee.";
                    return j;
                }

                case 4:
                {
                    Tuple<int, int> first = MostOpposed(incident, opt.ThroughToleranceDegrees);
                    if (first != null)
                    {
                        var rest = incident.Where((x, k) => k != first.Item1 && k != first.Item2).ToList();
                        Tuple<int, int> second = MostOpposed(rest, opt.ThroughToleranceDegrees);
                        if (second != null)
                        {
                            j.Kind = CadJunctionKind.Cross;
                            j.Automatic = true;
                            j.ThroughRunIds.Add(incident[first.Item1].RunId);
                            j.ThroughRunIds.Add(incident[first.Item2].RunId);
                            j.ThroughRunIds.Add(rest[second.Item1].RunId);
                            j.ThroughRunIds.Add(rest[second.Item2].RunId);
                            j.Says = "four runs in two straight pairs. A cross.";
                            return j;
                        }
                    }
                    j.Kind = CadJunctionKind.Irregular;
                    j.Automatic = false;
                    j.Says = "four runs meet here but they do not form two straight pairs, so this is not a " +
                             "cross. It is more likely two junctions the drawing put at one point. Nothing " +
                             "was placed.";
                    return j;
                }

                default:
                    j.Kind = CadJunctionKind.Unsupported;
                    j.Automatic = false;
                    j.Says = incident.Count.ToString(CultureInfo.InvariantCulture) +
                             " runs meet at one point. No fitting takes that many, and picking four of them " +
                             "would build a cross plus an unexplained orphan. Refused rather than rounded down.";
                    return j;
            }
        }

        private static CadConnectionIntent IntentFor(CadJunction j, Dictionary<string, CadRun> runs)
        {
            if (j.Degree < 2) return null;
            string fitting =
                j.Kind == CadJunctionKind.Collinear ? "direct" :
                j.Kind == CadJunctionKind.Elbow ? "elbow" :
                j.Kind == CadJunctionKind.Tee ? "tee" :
                j.Kind == CadJunctionKind.Cross ? "cross" : "none";

            var intent = new CadConnectionIntent
            {
                JunctionNode = j.NodeKey,
                Fitting = fitting,
                Participants = j.Incident,
                At = j.Point,
                Automatic = j.Automatic && fitting != "none",
                Says = fitting == "none"
                    ? "no fitting is proposed here: " + j.Says
                    : j.Says
            };

            // THE ORDER IS THE FITTING'S, NOT THE ENUMERATION'S. A tee takes the two
            // through-run members and THEN the branch; a cross takes the first
            // through pair and then the second. Sending them in the order they
            // happened to arrive puts the branch in the run, which is a fitting
            // facing the wrong way in a model that otherwise looks right.
            var ordered = new List<string>();
            if (j.ThroughRunIds.Count > 0)
            {
                ordered.AddRange(j.ThroughRunIds);
                if (j.BranchRunId != null) ordered.Add(j.BranchRunId);
            }
            else
            {
                ordered.AddRange(j.Incident.Select(i => i.RunId));
            }

            foreach (string runId in ordered)
            {
                CadRun run;
                intent.MemberSemanticIds.Add(runs.TryGetValue(runId, out run) ? run.SemanticId : null);
            }
            return intent;
        }

        /// <summary>
        /// Ends that nearly meet and were left alone.
        ///
        /// Only ends of degree one are considered: an end already at a junction
        /// is connected, and its distance to a third end nearby is not a gap in
        /// the drawing, it is the next thing along.
        /// </summary>
        private static List<CadGap> FindGaps(List<CadRun> runs, CadNodeIndex index, CadNetworkOptions opt)
        {
            var gaps = new List<CadGap>();
            if (opt.GapReviewDistanceMm <= opt.ConnectToleranceMm) return gaps;

            var degree = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CadRun r in runs)
            {
                Bump(degree, r.StartNode);
                Bump(degree, r.EndNode);
            }

            var open = new List<Tuple<CadRun, string, CadPoint>>();
            foreach (CadRun r in runs)
            {
                int d;
                if (degree.TryGetValue(r.StartNode, out d) && d == 1)
                    open.Add(Tuple.Create(r, "start", r.Start));
                if (degree.TryGetValue(r.EndNode, out d) && d == 1)
                    open.Add(Tuple.Create(r, "end", r.End));
            }

            for (int i = 0; i < open.Count; i++)
                for (int k = i + 1; k < open.Count; k++)
                {
                    if (ReferenceEquals(open[i].Item1, open[k].Item1)) continue;
                    double dist = open[i].Item3.PlanDistanceTo(open[k].Item3);
                    if (dist <= opt.ConnectToleranceMm) continue;
                    if (dist > opt.GapReviewDistanceMm) continue;
                    gaps.Add(new CadGap
                    {
                        RunA = open[i].Item1.Id,
                        EndA = open[i].Item2,
                        AtA = open[i].Item3,
                        RunB = open[k].Item1.Id,
                        EndB = open[k].Item2,
                        AtB = open[k].Item3,
                        DistanceMm = dist
                    });
                }
            return gaps;
        }

        /// <summary>
        /// Crossings: runs that intersect in plan without sharing a node.
        ///
        /// Every one is reported and none is joined. The point is to make the
        /// ones that SHOULD be junctions visible to a reviewer, not to decide
        /// which they are.
        /// </summary>
        private static List<CadCrossing> FindCrossings(List<CadRun> runs, CadNetworkOptions opt)
        {
            var crossings = new List<CadCrossing>();
            var nodes = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < runs.Count; i++)
                for (int k = i + 1; k < runs.Count; k++)
                {
                    CadRun a = runs[i], b = runs[k];
                    // Sharing a node is a junction, already classified. Skip it.
                    if (a.StartNode == b.StartNode || a.StartNode == b.EndNode ||
                        a.EndNode == b.StartNode || a.EndNode == b.EndNode) continue;

                    CadPoint at;
                    var sa = new CadSegment(a.Start, a.End, a.Layer);
                    var sb = new CadSegment(b.Start, b.End, b.Layer);
                    if (!CadTopologyRules.Intersect(sa, sb, opt.ConnectToleranceMm, out at)) continue;

                    string key = a.Id + "|" + b.Id;
                    if (!nodes.Add(key)) continue;

                    double? gap = a.ElevationMm.HasValue && b.ElevationMm.HasValue
                        ? (double?)Math.Abs(a.ElevationMm.Value - b.ElevationMm.Value)
                        : null;

                    crossings.Add(new CadCrossing
                    {
                        RunA = a.Id,
                        RunB = b.Id,
                        At = at,
                        SameLayer = string.Equals(a.Layer, b.Layer, StringComparison.OrdinalIgnoreCase),
                        ElevationGapMm = gap
                    });
                }
            return crossings;
        }

        private static List<CadNetworkComponent> Components(
            CadNetwork net, Dictionary<string, List<CadIncidence>> incidenceByNode)
        {
            var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (CadRun r in net.Runs) adjacency[r.Id] = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in incidenceByNode)
                for (int i = 0; i < kv.Value.Count; i++)
                    for (int k = i + 1; k < kv.Value.Count; k++)
                    {
                        HashSet<string> a, b;
                        if (adjacency.TryGetValue(kv.Value[i].RunId, out a)) a.Add(kv.Value[k].RunId);
                        if (adjacency.TryGetValue(kv.Value[k].RunId, out b)) b.Add(kv.Value[i].RunId);
                    }

            var terminalNodes = new HashSet<string>(
                net.Junctions.Where(j => j.Kind == CadJunctionKind.Terminal).Select(j => j.NodeKey),
                StringComparer.Ordinal);

            var runById = net.Runs.ToDictionary(r => r.Id, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<CadNetworkComponent>();

            foreach (CadRun start in net.Runs)
            {
                if (!seen.Add(start.Id)) continue;
                var comp = new CadNetworkComponent
                {
                    Id = "n" + (result.Count + 1).ToString(CultureInfo.InvariantCulture)
                };
                var stack = new Stack<string>();
                stack.Push(start.Id);
                var systems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var terms = new HashSet<string>(StringComparer.Ordinal);

                while (stack.Count > 0)
                {
                    string id = stack.Pop();
                    CadRun r;
                    if (!runById.TryGetValue(id, out r)) continue;
                    comp.RunIds.Add(id);
                    comp.TotalLengthMm += r.LengthMm;
                    if (!string.IsNullOrWhiteSpace(r.SystemType)) systems.Add(r.SystemType);
                    if (terminalNodes.Contains(r.StartNode)) terms.Add(r.StartNode);
                    if (terminalNodes.Contains(r.EndNode)) terms.Add(r.EndNode);

                    HashSet<string> next;
                    if (!adjacency.TryGetValue(id, out next)) continue;
                    foreach (string m in next) if (seen.Add(m)) stack.Push(m);
                }

                comp.RunIds.Sort(StringComparer.Ordinal);
                comp.TerminalNodes.AddRange(terms.OrderBy(x => x, StringComparer.Ordinal));
                comp.DeclaredSystems.AddRange(systems.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                result.Add(comp);
            }
            return result;
        }

        // ---------------------------------------------------------------------
        /// <summary>Bearing of a unit plan direction, 0..360, measured anticlockwise from +X.</summary>
        internal static double Bearing(CadPoint unit)
        {
            double a = Math.Atan2(unit.Y, unit.X) * 180.0 / Math.PI;
            return a < 0 ? a + 360.0 : a;
        }

        /// <summary>The smaller angle between two bearings, 0..180.</summary>
        internal static double AngleBetween(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        /// <summary>
        /// The pair of incidences closest to opposite, when one is close enough
        /// to count as "through". Null when none is - which is what makes a
        /// junction irregular rather than a tee with a guessed branch.
        /// </summary>
        internal static Tuple<int, int> MostOpposed(IList<CadIncidence> incident, double toleranceDegrees)
        {
            Tuple<int, int> best = null;
            double bestDeviation = double.MaxValue;
            for (int i = 0; i < incident.Count; i++)
                for (int k = i + 1; k < incident.Count; k++)
                {
                    double deviation = Math.Abs(
                        180.0 - AngleBetween(incident[i].BearingDegrees, incident[k].BearingDegrees));
                    if (deviation < bestDeviation) { bestDeviation = deviation; best = Tuple.Create(i, k); }
                }
            return bestDeviation <= toleranceDegrees ? best : null;
        }

        private static void Add(Dictionary<string, List<CadIncidence>> map, string key, CadIncidence value)
        {
            List<CadIncidence> list;
            if (!map.TryGetValue(key, out list)) map[key] = list = new List<CadIncidence>();
            list.Add(value);
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            int n;
            map[key] = map.TryGetValue(key, out n) ? n + 1 : 1;
        }

        /// <summary>
        /// Is this drawn segment part of that run? Both of its ends must lie on
        /// the run's line, within tolerance, and between the run's own ends.
        ///
        /// Tolerance is applied to the PERPENDICULAR distance and to the
        /// parameter separately: a segment 0.5 mm off the line over a 12 m run
        /// is the same piece of drafting, and one that runs 0.5 mm past the end
        /// is still inside it.
        /// </summary>
        internal static bool LiesOn(CadRun run, CadSegment s, double toleranceMm)
        {
            double length = run.LengthMm;
            if (length <= 0) return false;
            var line = new CadSegment(run.Start, run.End, run.Layer);
            if (CadTopologyRules.PerpendicularDistance(line, s.A) > toleranceMm) return false;
            if (CadTopologyRules.PerpendicularDistance(line, s.B) > toleranceMm) return false;
            double ta = CadTopologyRules.Project(line, s.A);
            double tb = CadTopologyRules.Project(line, s.B);
            double slack = toleranceMm;
            return ta >= -slack && ta <= length + slack && tb >= -slack && tb <= length + slack;
        }

        /// <summary>
        /// The drawn entities this run consumed, and how many segments they were -
        /// in ONE pass, with a bounding-box rejection before the arithmetic.
        ///
        /// It was two passes, each walking the whole layer for every run. On a
        /// permit drawing with fifty thousand segments on a layer and five thousand
        /// runs that is half a billion perpendicular-distance computations to fill
        /// in a provenance field, done twice.
        /// </summary>
        private static IEnumerable<string> SourcesOn(CadRun run,
                                                     Dictionary<string, List<CadSegment>> byLayer,
                                                     double toleranceMm, out int counted)
        {
            counted = 0;
            List<CadSegment> bucket;
            if (!byLayer.TryGetValue(run.Layer ?? "", out bucket)) return Enumerable.Empty<string>();

            double minX = Math.Min(run.Start.X, run.End.X) - toleranceMm;
            double maxX = Math.Max(run.Start.X, run.End.X) + toleranceMm;
            double minY = Math.Min(run.Start.Y, run.End.Y) - toleranceMm;
            double maxY = Math.Max(run.Start.Y, run.End.Y) + toleranceMm;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (CadSegment s in bucket)
            {
                // The cheap rejection first: most segments on a layer are nowhere
                // near any given run, and a box test is four comparisons where the
                // real test is two projections and two perpendicular distances.
                if (s.A.X < minX && s.B.X < minX) continue;
                if (s.A.X > maxX && s.B.X > maxX) continue;
                if (s.A.Y < minY && s.B.Y < minY) continue;
                if (s.A.Y > maxY && s.B.Y > maxY) continue;

                if (!LiesOn(run, s, toleranceMm)) continue;
                ids.Add(s.SourceCurveId);
                counted++;
            }
            return ids.OrderBy(x => x, StringComparer.Ordinal);
        }
    }
}
