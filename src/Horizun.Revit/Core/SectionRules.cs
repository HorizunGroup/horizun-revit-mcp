// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// REINFORCEMENT DECLARED THE WAY A DETAIL SCHEDULE DECLARES IT: RELATIVE TO THE
// SECTION, NOT IN MODEL COORDINATES.
//
// A schedule says "B-12: 3#5 top, 2#5 bottom, E#3 every 100 at the ends" once for
// every beam of that mark. reinforcement_rules take a centreline in MODEL
// coordinates, so the same declaration had to be typed out per beam, per bar row,
// in absolute millimetres. A section rule is that declaration: faces, counts, bar
// types and a perimeter stirrup with zones. The resolver measures each host's own
// frame - its axis, its width and depth, read off its solid - and this file turns
// rule + frame into the ordinary reinforcement rules and stirrup zone rules they
// mean, so plan, apply, audit, provenance and idempotency need to know nothing
// about sections (refuerzo desde cartilla, F1.1).
//
// Expanded ids carry the host: <id>#h<element id>#<layer>, and the stirrups
// <id>#h<element id>#stirrups (whose zones then expand to ...#stirrups#<zone>).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One row of bars in a section: a face, a count, a bar type.</summary>
    public sealed class StructuralSectionLayer
    {
        public const string FaceTop = "top", FaceBottom = "bottom", FaceLeft = "left", FaceRight = "right",
                            FacePerimeter = "perimeter", FaceRing = "ring";
        public static readonly string[] Faces = { FaceTop, FaceBottom, FaceLeft, FaceRight, FacePerimeter, FaceRing };

        public const string ExtendHostLength = "host_length", ExtendClearSpan = "clear_span", ExtendExplicit = "explicit",
                            ExtendFromEnd = "from_end", ExtendAxes = "axes";

        public string Name;
        public string Face;
        public int Count;
        public string BarTypeId;
        public string ShapeName;
        public bool AllowNewShape;
        public string Extend = ExtendHostLength;
        /// <summary>For an explicit extent: from and to, in mm measured along the host from its start face.</summary>
        public double? FromMm, ToMm;
        /// <summary>
        /// For a bar laid from one end (F2.2, "bastones"): "start" or "end", and how far it runs -
        /// in mm, or as a share of the host's measured length (0.25 = L/4). The end cover is kept.
        /// </summary>
        public string FromEnd;
        public double? RunMm, RunRatio;
        /// <summary>
        /// For an extent measured from a NAMED AXIS (a schedule card's own reference): FromMm and
        /// ToMm are stations from the grid named Axis, positive toward the grid named Toward. The
        /// plan finds both grids in the model by name and where the host's axis crosses them;
        /// the start termination is at FromMm, the end at ToMm.
        /// </summary>
        public string Axis, Toward;
        /// <summary>
        /// The name of an earlier layer on this face (top or bottom) whose gaps this one fills: one bar
        /// at the middle of each gap, so it has one bar fewer than that layer (schedule face_slots).
        /// </summary>
        public string Between;
        public StructuralTermination Start = new StructuralTermination();
        public StructuralTermination End = new StructuralTermination();
        public string Mark;
        /// <summary>Splices along the layer (F2.1), in the order declared. Empty: one bar end to end.</summary>
        public List<StructuralLap> Laps = new List<StructuralLap>();
        /// <summary>The other elements this layer's bars run into (acero 3.0, L1). Lets an explicit extent leave the host.</summary>
        public List<long> Into = new List<long>();
        /// <summary>Anchored lengths, each on the piece at its declared end of the layer.</summary>
        public List<StructuralAnchorage> Anchorage = new List<StructuralAnchorage>();
        /// <summary>A bayoneta at a section change (L2): null when the bar runs straight.</summary>
        public StructuralCrank Crank;
    }

    /// <summary>
    /// A bayoneta (acero 3.0, L2): from a station measured along the host, the bar moves a
    /// declared distance toward the inside of the section over a declared run, and carries on
    /// there - into the smaller column above. All three numbers are the caller's.
    /// </summary>
    public sealed class StructuralCrank
    {
        public double AtMm, RunMm, InwardMm;
    }

    /// <summary>
    /// Crossties (acero 3.0, L2): one single-leg tie across the section at every intermediate
    /// bar of a named layer - across the width for a left/right layer, across the depth for a
    /// top/bottom one - at the stirrups' stations, beside each stirrup. Hooks are declared.
    /// </summary>
    public sealed class StructuralSectionTie
    {
        public string Name;
        public string BarTypeId;
        public string LayerName;
        public string ShapeName;
        public bool AllowNewShape;
        public StructuralTermination Start = new StructuralTermination();
        public StructuralTermination End = new StructuralTermination();
        public string Mark;
    }

    /// <summary>
    /// One splice of a layer: centred on a station measured along the host from its start face
    /// (mm, or a share of the measured length), the two pieces overlapping by a declared length
    /// (mm, or bar diameters). The pieces are NOT collinear (D4): the next piece sits one bar diameter
    /// across, in contact, toward the inside of the section unless the caller asks for the outside.
    /// </summary>
    public sealed class StructuralLap
    {
        public const string TowardInside = "inside", TowardOutside = "outside";
        public double? AtMm, AtRatio, LengthMm, LengthDb;
        public string Toward = TowardInside;
        /// <summary>
        /// A coupler instead of an overlap (acero 3.0, L7): the two pieces are collinear, meet at the
        /// station, and are joined there by a coupler of this type. Null for a lap.
        /// </summary>
        public string CouplerTypeName;
    }

    /// <summary>The cover a section rule lays its bars inside. One of three forms, never mixed.</summary>
    public sealed class StructuralSectionCover
    {
        /// <summary>Read the host's common cover when the rule is resolved.</summary>
        public bool FromHost;
        /// <summary>One distance for every face.</summary>
        public double? CommonMm;
        /// <summary>Per face, all five stated.</summary>
        public double? TopMm, BottomMm, LeftMm, RightMm, EndMm;
    }

    public sealed class StructuralSectionRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        public StructuralSectionCover Cover = new StructuralSectionCover();
        public List<StructuralSectionLayer> Layers = new List<StructuralSectionLayer>();
        /// <summary>The perimeter stirrup, parsed as a stirrup zone rule whose profile and run the frame supplies. Null when none.</summary>
        public StructuralStirrupZoneRule Stirrups;
        /// <summary>"vertical" frames a point-placed foundation (a pile) as a vertical member (L3); null otherwise.</summary>
        public string Axis;
        /// <summary>
        /// The stirrups measured from a named axis (null when they run the host's length): the zones
        /// fill from StirrupsFromMm to StirrupsToMm, stations from the grid StirrupsAxis toward the
        /// grid StirrupsToward, the first zone at StirrupsFromMm.
        /// </summary>
        public string StirrupsAxis, StirrupsToward;
        public double StirrupsFromMm, StirrupsToMm;
        /// <summary>Crossties (L2); they follow the stirrups' zones, so they need stirrups.</summary>
        public List<StructuralSectionTie> Ties = new List<StructuralSectionTie>();
        public string Mark;
        public bool Required = true;
        public Newtonsoft.Json.Linq.JObject Raw;
    }

    /// <summary>
    /// A host's own frame, measured by the resolver: U along the member, V across it,
    /// W "up" in the section, unit vectors in model coordinates, and the extents of the
    /// host's solid along each, in mm, from <see cref="OriginMm"/>.
    /// </summary>
    public sealed class SectionHostFrame
    {
        public long ElementId;
        public double[] OriginMm = new double[3];
        public double[] U = new double[3], V = new double[3], W = new double[3];
        public double UMin, UMax, VMin, VMax, WMin, WMax;
        /// <summary>Null when the solid is the box its extents describe; otherwise why it is not.</summary>
        public string NotRectangular;
        /// <summary>
        /// True when the solid is a circular cylinder about the axis (L2): equal extents across, and
        /// every vertex of the curved face at the same distance from the axis. The axis then runs
        /// through the middle of the extents, and RadiusMm is half of them.
        /// </summary>
        public bool Circular;
        /// <summary>
        /// The stirrup starts - and Revit lays its hooks - at the top corner on the VMax side instead
        /// of the VMin side (acero 3.0, L5): a wall boundary element at the wall's END, so the hooks'
        /// corner rests against the wall's end face as it does at the start.
        /// </summary>
        public bool HookCornerAtVMax;
        public double RadiusMm;
        /// <summary>
        /// Where the host's axis crosses each grid a rule names, as a U coordinate (the same frame as
        /// UMin/UMax), measured by the resolver from the model's grids. A name the resolver could not
        /// place carries its refusal in AxisProblems instead ("code: why").
        /// </summary>
        public Dictionary<string, double> AxisStations = new Dictionary<string, double>(StringComparer.Ordinal);
        public Dictionary<string, string> AxisProblems = new Dictionary<string, string>(StringComparer.Ordinal);
        public double LengthMm => UMax - UMin;
        public double WidthMm => VMax - VMin;
        public double DepthMm => WMax - WMin;

        public double[] At(double u, double v, double w) => new[]
        {
            OriginMm[0] + u * U[0] + v * V[0] + w * W[0],
            OriginMm[1] + u * U[1] + v * V[1] + w * W[1],
            OriginMm[2] + u * U[2] + v * V[2] + w * W[2]
        };
    }

    /// <summary>
    /// A host's section frame, measured off its own solid: the axis the caller reads from
    /// the model (a beam's location line, a column's vertical), "up" in the section, and
    /// the extents of every vertex of the solid along the three directions. A solid that
    /// does not fill the box those extents describe is not a rectangular prism - a haunch,
    /// a chamfer, a cut end, a rotated section - and says so instead of being reinforced as one.
    /// </summary>
    public static class SectionFrames
    {
        /// <summary>The solid must fill at least this share of its box to be read as a b x h prism.</summary>
        public const double MinimumFill = 0.98;

        public static SectionHostFrame Measure(long elementId, double[] originMm, double[] along, double[] up, HostMesh mesh)
        {
            double[] u = Unit(along);
            // UP, made perpendicular to the axis: on an inclined beam "up" is the section's
            // own up, square to the slope, not world Z.
            double d = Dot(up, u);
            double[] w = Unit(new[] { up[0] - d * u[0], up[1] - d * u[1], up[2] - d * u[2] });
            double[] v = Cross(w, u);
            var f = new SectionHostFrame
            {
                ElementId = elementId, OriginMm = new[] { originMm[0], originMm[1], originMm[2] }, U = u, V = v, W = w,
                UMin = double.MaxValue, UMax = double.MinValue, VMin = double.MaxValue, VMax = double.MinValue,
                WMin = double.MaxValue, WMax = double.MinValue
            };
            if (mesh == null || mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0)
            {
                f.NotRectangular = "its solid could not be read";
                f.UMin = f.UMax = f.VMin = f.VMax = f.WMin = f.WMax = 0;
                return f;
            }
            foreach (double[] p in mesh.Vertices)
            {
                double[] r = { p[0] - originMm[0], p[1] - originMm[1], p[2] - originMm[2] };
                double a = Dot(r, u), b = Dot(r, v), c = Dot(r, w);
                f.UMin = Math.Min(f.UMin, a); f.UMax = Math.Max(f.UMax, a);
                f.VMin = Math.Min(f.VMin, b); f.VMax = Math.Max(f.VMax, b);
                f.WMin = Math.Min(f.WMin, c); f.WMax = Math.Max(f.WMax, c);
            }
            double box = f.LengthMm * f.WidthMm * f.DepthMm;
            double vol = Math.Abs(Volume(mesh));
            if (mesh.AnyCurvedFace)
            {
                f.NotRectangular = "it has a curved face";
                // A CIRCULAR SECTION (L2): the solid's vertices are tessellated ON its surface, so
                // every one that is not on an end face sits at the radius from the axis.
                double r = f.WidthMm / 2, vc = (f.VMin + f.VMax) / 2, wc = (f.WMin + f.WMax) / 2;
                double slack = Math.Max(1.0, 2 * mesh.ChordToleranceMm);
                bool round = Math.Abs(f.WidthMm - f.DepthMm) <= slack && r > 0;
                double rMin = double.MaxValue, rMax = 0;
                foreach (double[] p in mesh.Vertices)
                {
                    double[] q = { p[0] - originMm[0], p[1] - originMm[1], p[2] - originMm[2] };
                    double dv = Dot(q, v) - vc, dw = Dot(q, w) - wc;
                    double rr = Math.Sqrt(dv * dv + dw * dw);
                    // ON THE AXIS: an end cap's centre, there to a hair rather than to a micron (measured,
                    // Revit 2024 and 2026) - within the slack it is the axis, not a radius.
                    if (rr <= slack) continue;
                    rMin = Math.Min(rMin, rr); rMax = Math.Max(rMax, rr);
                    if (Math.Abs(rr - r) > slack) round = false;
                }
                if (round) { f.Circular = true; f.RadiusMm = r; f.NotRectangular = "it is circular"; }
                else
                    // WHAT WAS MEASURED, so the reader can tell a section that is not round from a
                    // tolerance that is too tight (live 2024, acero L7).
                    f.NotRectangular = "it has a curved face; across its axis it is " + Mm(f.WidthMm) + " x " + Mm(f.DepthMm) +
                                       " mm across, with vertex radii from " + Mm(rMin == double.MaxValue ? 0 : rMin) + " to " + Mm(rMax) +
                                       " mm about the middle of those extents (a circle needs both equal to half the width within the slack " +
                                       Mm(slack) + " mm)";
            }
            else if (box <= 0 || vol / box < MinimumFill)
                f.NotRectangular = "its solid fills " + Math.Round(box <= 0 ? 0 : vol / box, 3).ToString(CultureInfo.InvariantCulture) +
                                   " of the box " + Math.Round(f.LengthMm, 1).ToString(CultureInfo.InvariantCulture) + " x " +
                                   Math.Round(f.WidthMm, 1).ToString(CultureInfo.InvariantCulture) + " x " +
                                   Math.Round(f.DepthMm, 1).ToString(CultureInfo.InvariantCulture) + " mm along its own axes";
            return f;
        }

        private static string Mm(double x) => Math.Round(x, 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>The enclosed volume of a closed triangle mesh, by the divergence theorem (signed).</summary>
        public static double Volume(HostMesh m)
        {
            double sum = 0;
            foreach (int[] t in m.Triangles)
            {
                double[] a = m.Vertices[t[0]], b = m.Vertices[t[1]], c = m.Vertices[t[2]];
                sum += Dot(a, Cross(b, c)) / 6.0;
            }
            return sum;
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double[] Unit(double[] a)
        {
            double l = Math.Sqrt(Dot(a, a));
            return l < 1e-12 ? new[] { 0.0, 0.0, 0.0 } : new[] { a[0] / l, a[1] / l, a[2] / l };
        }
    }

    public sealed class SectionResult
    {
        public bool Ok => Code == null;
        public string Code;
        public string Why;
        /// <summary>
        /// What a rule measured from named axes did NOT lay in this host because it lies wholly in
        /// another host's stretch of the axes (a member of several spans modelled as several hosts).
        /// </summary>
        public List<string> NotHere = new List<string>();
    }

    public static class SectionRules
    {
        /// <summary>
        /// May a host placed at a POINT be framed as a column (axis up world Z, its facing as
        /// the section's up)? Only a structural column. Anything else - an isolated footing
        /// above all - used to be framed that way silently, and a bottom layer landed as
        /// short vertical bars inside a side face, accepted by the plan and the audit
        /// (backlog P0 #8). Null when it may; otherwise the refusal, naming the way out.
        /// </summary>
        public static string PointHostRefusal(string builtInCategory) => PointHostRefusal(builtInCategory, false);

        /// <summary>
        /// The same, told whether the rule DECLARED its host vertical (axis: vertical, acero 3.0 L3):
        /// a pile is a point-placed foundation that is a vertical member, framed as one only when said.
        /// </summary>
        public static string PointHostRefusal(string builtInCategory, bool declaredVertical)
        {
            if (string.Equals(builtInCategory, "OST_StructuralColumns", System.StringComparison.Ordinal)) return null;
            if (declaredVertical && string.Equals(builtInCategory, "OST_StructuralFoundation", System.StringComparison.Ordinal)) return null;
            return "it is placed at a point and is " + (string.IsNullOrWhiteSpace(builtInCategory) ? "of no readable category" : "a " + builtInCategory) +
                   ", not a structural column: a section rule frames a point-placed host as a column (its axis up world Z), " +
                   "which is wrong for it. A footing is reinforced with mat_rules.";
        }

        public const string CodeNotRectangular = "section_not_rectangular";
        public const string CodeNoCover = "section_cover_not_available";
        public const string CodeNoRoom = "section_has_no_room_for_the_bars";
        public const string CodePerimeterCount = "perimeter_count_is_not_four";
        public const string CodeClearSpan = "clear_span_not_implemented";
        public const string CodeExtentOutside = "explicit_extent_outside_the_host";
        public const string CodeAxisNotInModel = "axis_not_in_model";
        public const string CodeAxisNotUnique = "axis_name_not_unique";
        public const string CodeAxisParallel = "axis_parallel_to_host";
        public const string CodeAxisNotStraight = "axis_not_straight";
        public const string CodeAxesCoincide = "axes_coincide_on_host";
        public const string CodeHostNotBetweenAxes = "host_not_between_axes";
        public const string CodeNothingInHost = "nothing_of_the_rule_in_any_host";
        public const string CodeNoDiameter = "bar_diameter_not_readable";
        public const string CodeLapOutside = "lap_outside_the_bar";
        public const string CodeLapsOverlap = "laps_overlap";
        public const string CodeLapOffsetNoRoom = "lap_offset_does_not_fit";
        // A FACE'S BARS STAND AT FIXED SHARES OF ITS WIDTH: i/(n-1), or the middle for a single
        // bar. Two layers on one face coincide only where those shares meet - a pair at the
        // corners and one bar at the middle (4#5 + 4#4 in a square column) never touch.
        private static bool StationsShare(int a, int b)
        {
            if (a <= 0 || b <= 0) return true;
            Func<int, IEnumerable<double>> at = n => n == 1 ? new[] { 0.5 } : Enumerable.Range(0, n).Select(i => (double)i / (n - 1));
            foreach (double x in at(a)) foreach (double y in at(b)) if (Math.Abs(x - y) < 1e-6) return true;
            return false;
        }

        public const string CodeLayersCoincide = "layers_coincide";
        public const string CodeCrankOutside = "crank_outside_the_bar";
        public const string CodeCrankWithLaps = "crank_with_laps_not_implemented";
        public const string CodeTieNoBars = "tie_layer_has_no_intermediate_bar";
        public const string CodeRingNeedsCircle = "ring_needs_a_circular_section";
        public const string CodeCoverBelowHost = "section_cover_below_the_host_cover";
        public const string CodePlacementNeedsCircle = "shape_placement_needs_a_circular_section";
        public const string CodeBetweenCount = "between_count_not_one_per_gap";
        public const string CodeBetweenOutside = "between_outside_its_layer";
        public const string CodeBetweenNotLaid = "between_layer_not_laid_in_this_host";
        public const string CodeBetweenNoRoom = "between_bars_do_not_fit";
        public static readonly string[] AllCodes =
            { CodeNotRectangular, CodeNoCover, CodeNoRoom, CodePerimeterCount, CodeClearSpan, CodeExtentOutside, CodeNoDiameter,
              CodeLapOutside, CodeLapsOverlap, CodeLayersCoincide, CodeLapOffsetNoRoom, CodeCrankOutside, CodeCrankWithLaps,
              CodeTieNoBars, CodeRingNeedsCircle, CodeCoverBelowHost, CodePlacementNeedsCircle,
              CodeBetweenCount, CodeBetweenOutside, CodeBetweenNotLaid, CodeBetweenNoRoom };

        private static SectionResult Refuse(string code, string why) => new SectionResult { Code = code, Why = why };

        /// <summary>A termination carried to the other end of a reversed bar: its hook turns the other way round.</summary>
        private static StructuralTermination Reversed(StructuralTermination t) => t == null ? new StructuralTermination() : new StructuralTermination
        {
            HookTypeId = t.HookTypeId,
            Orientation = t.Orientation == RebarApiVocabulary.OrientationLeft ? RebarApiVocabulary.OrientationRight
                        : t.Orientation == RebarApiVocabulary.OrientationRight ? RebarApiVocabulary.OrientationLeft : t.Orientation
        };

        /// <summary>
        /// Why a rule's DECLARED cover cannot be built in a host whose common cover is larger, or
        /// null (acero 3.0, L4). MEASURED on Revit 2024: joists with a 40 mm common cover reinforced
        /// from a declared 25 - Revit clamped the stirrup array to the host's cover (30 mm short) and
        /// the bars moved with it. Read from the host is never below itself; a host with no common
        /// cover is not judged here.
        /// </summary>
        public static string CoverBelowHost(StructuralSectionRule rule, double? hostCoverMm)
        {
            if (rule == null || !hostCoverMm.HasValue || rule.Cover.FromHost) return null;
            var declared = new List<double>();
            if (rule.Cover.CommonMm.HasValue) declared.Add(rule.Cover.CommonMm.Value);
            foreach (double? v in new[] { rule.Cover.TopMm, rule.Cover.BottomMm, rule.Cover.LeftMm, rule.Cover.RightMm, rule.Cover.EndMm })
                if (v.HasValue) declared.Add(v.Value);
            StructuralStirrupZoneCover zc = rule.Stirrups?.Cover;
            if (zc != null && zc.Source == StructuralStirrupZoneCover.SourceDeclared && zc.DistanceMm.HasValue) declared.Add(zc.DistanceMm.Value);
            if (declared.Count == 0) return null;
            double low = declared.Min();
            if (low >= hostCoverMm.Value - 1e-6) return null;
            return "rule '" + rule.Id + "' declares a cover of " + Mm(low) + " mm and the host's own common cover is " + Mm(hostCoverMm.Value) +
                   " mm. Revit holds hosted bars to the host's cover, so the steel would not be where this rule puts it. " +
                   "Declare at least " + Mm(hostCoverMm.Value) + " mm, read it with cover: { source: host }, or change the host's cover.";
        }

        /// <summary>
        /// The cage a section rule lays in one host (overlapping_bar): its bars, its stirrups and its ties share
        /// it, and its stirrups and ties are laid against and around its own bars by design.
        /// </summary>
        public static string CageIdOf(string ruleId, long hostId) => ruleId + "#h" + hostId.ToString(CultureInfo.InvariantCulture);
        public const string CageLongitudinal = "longitudinal";
        public const string CageStirrup = "stirrup";
        public const string CageTie = "tie";

        public static string LayerId(string ruleId, long hostId, string layer) =>
            ruleId + "#h" + hostId.ToString(CultureInfo.InvariantCulture) + "#" + layer;

        /// <summary>
        /// Turn a section rule and ONE host's frame into reinforcement rules (one per layer)
        /// and, when the rule has stirrups, one stirrup zone rule for that host. `diameterOf`
        /// answers the MODEL diameter of a bar type id in mm, or zero; `hostCoverMm` is the
        /// host's common cover when the rule reads it from the host.
        /// </summary>
        public static SectionResult Expand(StructuralSectionRule rule, SectionHostFrame f, Func<string, double> diameterOf,
                                           double? hostCoverMm, out List<StructuralRebarRule> bars,
                                           out StructuralStirrupZoneRule stirrups)
        {
            return Expand(rule, f, diameterOf, null, hostCoverMm, out bars, out stirrups);
        }

        /// <summary>
        /// How far a corner bar sits in from the stirrup's straight legs beyond touching them:
        /// a bar resting in a bend of inner radius R lies on its bisector at R - (R - d/2)/sqrt 2
        /// from each leg. MEASURED live 2026-10-08 (Revit 2026, 16M in a 10M stirrup with a
        /// 40 mm stirrup bend): Revit moves corner bars there, 3.53 mm in from where touching
        /// the legs puts them. Zero when the bend is not larger than the bar.
        /// </summary>
        public static double CornerInsetMm(double stirrupBendDiameterMm, double barDiameterMm)
        {
            double R = stirrupBendDiameterMm / 2, r = barDiameterMm / 2;
            if (R <= r) return 0;
            return R - (R - r) / Math.Sqrt(2) - r;
        }

        /// <summary>
        /// As above, and with `stirrupBendOf` (the inner stirrup/tie bend DIAMETER of a bar type
        /// id, in mm, or zero) the corner bars are planned where Revit puts them: in the bends,
        /// and - in the corner where the stirrup's two hooks overlap, which this rule puts at the
        /// TOP - half a stirrup diameter further in, which is where the hook's second pass leaves
        /// them (measured on the same run: +4.75 mm for a 9.5 mm stirrup).
        /// </summary>
        public static SectionResult Expand(StructuralSectionRule rule, SectionHostFrame f, Func<string, double> diameterOf,
                                           Func<string, double> stirrupBendOf, double? hostCoverMm,
                                           out List<StructuralRebarRule> bars, out StructuralStirrupZoneRule stirrups)
        {
            List<StructuralStirrupZoneRule> zones;
            SectionResult r = ExpandAll(rule, f, diameterOf, stirrupBendOf, hostCoverMm, out bars, out zones);
            stirrups = zones.FirstOrDefault(z => z.Id == LayerId(rule.Id, f.ElementId, "stirrups"));
            return r;
        }

        /// <summary>The same, with every zone rule it makes: the stirrups and, after them, the crossties (L2).</summary>
        public static SectionResult ExpandAll(StructuralSectionRule rule, SectionHostFrame f, Func<string, double> diameterOf,
                                           Func<string, double> stirrupBendOf, double? hostCoverMm,
                                           out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zoneRules)
        {
            bars = new List<StructuralRebarRule>();
            zoneRules = new List<StructuralStirrupZoneRule>();
            var result = new SectionResult();
            StructuralStirrupZoneRule stirrups = null;
            if (f.Circular) return ExpandCircular(rule, f, diameterOf, hostCoverMm, bars, zoneRules);
            StructuralSectionLayer ringOnBox = rule.Layers.FirstOrDefault(x => x.Face == StructuralSectionLayer.FaceRing);
            // A PLACED HOOP OR SPIRAL (L7) is round: on a box it would be a circle inside a rectangle
            // of bars, which is not what a rectangular stirrup is.
            if (rule.Stirrups?.ShapePlacement != null)
                return Refuse(CodePlacementNeedsCircle, "the stirrups of rule '" + rule.Id + "' are placed from shape '" +
                              rule.Stirrups.ShapePlacement.Shape + "' as a " + rule.Stirrups.ShapePlacement.Kind + ", and element " +
                              f.ElementId + " is not a circular section" + (f.NotRectangular != null ? " (" + f.NotRectangular + ")" : "") +
                              ". A rectangular stirrup is declared without shape_placement.");
            if (ringOnBox != null)
                return Refuse(CodeRingNeedsCircle, "layer '" + (ringOnBox.Name ?? "ring") + "' is a ring, and element " + f.ElementId +
                              " is not a circular section" + (f.NotRectangular != null ? " (" + f.NotRectangular + ")" : "") + ".");
            // layer name -> face, first bar's v and w, step between bars, count
            var stations = new Dictionary<string, Tuple<string, double, double, double, int>>();
            if (f.NotRectangular != null)
                return Refuse(CodeNotRectangular, "element " + f.ElementId + " is not a rectangular prism along its axis (" +
                              f.NotRectangular + "). A section rule lays bars in a b x h section; a member that is not one " +
                              "needs reinforcement_rules with its own curves.");

            double top, bottom, left, right, end;
            if (rule.Cover.FromHost)
            {
                if (!hostCoverMm.HasValue)
                    return Refuse(CodeNoCover, "the rule reads the cover from the host and element " + f.ElementId +
                                  " has no readable common cover. Declare cover distance_mm or per-face values.");
                top = bottom = left = right = end = hostCoverMm.Value;
            }
            else if (rule.Cover.CommonMm.HasValue) top = bottom = left = right = end = rule.Cover.CommonMm.Value;
            else
            {
                top = rule.Cover.TopMm.Value; bottom = rule.Cover.BottomMm.Value;
                left = rule.Cover.LeftMm.Value; right = rule.Cover.RightMm.Value; end = rule.Cover.EndMm.Value;
            }
            // THE COVER THIS SECTION DECLARES FOR ITS HOST, carried on every bar it lays: a bar declared into
            // other elements is held to the smallest cover any of them declares, and the host's is this one.
            double sectionCover = Math.Min(Math.Min(top, bottom), Math.Min(Math.Min(left, right), end));

            double sd = 0;
            if (rule.Stirrups != null)
            {
                sd = diameterOf(rule.Stirrups.BarTypeId);
                if (sd <= 0) return Refuse(CodeNoDiameter, "the stirrup bar type's diameter could not be read from the model.");
            }

            // The space inside the stirrup, along V and W, as bar CENTRE limits are derived per layer below.
            double vIn0 = f.VMin + left + sd, vIn1 = f.VMax - right - sd;
            double wIn0 = f.WMin + bottom + sd, wIn1 = f.WMax - top - sd;
            if (vIn1 <= vIn0 || wIn1 <= wIn0)
                return Refuse(CodeNoRoom, "element " + f.ElementId + " is " + Mm(f.WidthMm) + " x " + Mm(f.DepthMm) +
                              " mm and the covers and stirrup leave no room inside it.");

            var layers = new List<StructuralSectionLayer>();
            foreach (StructuralSectionLayer l in rule.Layers)
            {
                if (l.Face != StructuralSectionLayer.FacePerimeter) { layers.Add(l); continue; }
                // PERIMETER IS FOUR CORNER BARS AND NOTHING ELSE. More than four around a
                // rectangle has more than one reading (how many per face?), and a schedule
                // that means one of them says it per face.
                if (l.Count != 4)
                    return Refuse(CodePerimeterCount, "layer '" + (l.Name ?? l.Face) + "' declares " + l.Count +
                                  " bars on the perimeter. Four is the corners; any other count has more than one " +
                                  "arrangement, so declare top, bottom, left and right separately.");
                layers.Add(Copy(l, StructuralSectionLayer.FaceTop, 2, (l.Name ?? "perimeter") + "-top"));
                layers.Add(Copy(l, StructuralSectionLayer.FaceBottom, 2, (l.Name ?? "perimeter") + "-bottom"));
            }

            var laid = new List<Tuple<string, double, double, string>>();   // face, from, to, layer
            var laidCount = new Dictionary<string, int>();                  // layer -> bars across its face
            var heldDiameter = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int li = 0; li < layers.Count; li++)
            {
                StructuralSectionLayer l = layers[li];
                double db = diameterOf(l.BarTypeId);
                if (db <= 0) return Refuse(CodeNoDiameter, "layer '" + (l.Name ?? l.Face) + "': its bar type's diameter could not be read.");
                double u0, u1;
                Func<double, double> lapAt = at => f.UMin + at;   // a lap's station: from the start face, or from the layer's axis
                if (l.Extend == StructuralSectionLayer.ExtendClearSpan)
                    return Refuse(CodeClearSpan, "layer '" + (l.Name ?? l.Face) + "' extends over the clear span, which " +
                                  "needs the supports this build does not read yet. Use host_length or an explicit from/to.");
                if (l.Extend == StructuralSectionLayer.ExtendExplicit)
                {
                    u0 = f.UMin + l.FromMm.Value; u1 = f.UMin + l.ToMm.Value;
                    // A LAYER DECLARED INTO OTHER ELEMENTS may leave its host - a dowel into the
                    // footing, a bar lapped into the storey above. Whether the steel is still in
                    // concrete is then the union's question, which containment answers.
                    if (l.Into.Count == 0 && (u0 < f.UMin - 1e-6 || u1 > f.UMax + 1e-6) || u1 <= u0)
                        return Refuse(CodeExtentOutside, "layer '" + (l.Name ?? l.Face) + "' runs from " + Mm(l.FromMm.Value) +
                                      " to " + Mm(l.ToMm.Value) + " mm and element " + f.ElementId + " is " + Mm(f.LengthMm) + " mm long.");
                }
                else if (l.Extend == StructuralSectionLayer.ExtendAxes)
                {
                    SectionResult ax = AxisRange(f, l.Axis, l.Toward, l.FromMm.Value, l.ToMm.Value, "layer '" + (l.Name ?? l.Face) + "'",
                                                 out u0, out u1, out bool reversed);
                    if (ax != null) return ax;
                    // A MEMBER OF SEVERAL SPANS IS OFTEN SEVERAL HOSTS with one mark: steel that lies wholly
                    // in another host's stretch of the axes is that host's, and is not laid here.
                    if (u1 <= f.UMin + 1e-6 || u0 >= f.UMax - 1e-6) { result.NotHere.Add("layer '" + (l.Name ?? l.Face) + "'"); continue; }
                    if (l.Into.Count == 0 && (u0 < f.UMin + end - 1e-6 || u1 > f.UMax - end + 1e-6))
                        return Refuse(CodeExtentOutside, "layer '" + (l.Name ?? l.Face) + "' runs " + Mm(l.FromMm.Value) + ".." + Mm(l.ToMm.Value) +
                                      " mm from axis " + l.Axis + " toward " + l.Toward + ", which is " + Mm(u0 - f.UMin) + ".." + Mm(u1 - f.UMin) +
                                      " mm along element " + f.ElementId + "; the element is " + Mm(f.LengthMm) + " mm long less its end covers.");
                    // A LAP from the same axis: FromMm sits at u0, or - turned - at u1.
                    double au0 = u0, au1 = u1, afrom = l.FromMm.Value; bool rev = reversed;
                    lapAt = at => rev ? au1 - (at - afrom) : au0 + (at - afrom);
                    // Its start is where FromMm is: when that is the higher end of the host's U, the
                    // bar is laid the other way round, its declared start hook at U's far end.
                    if (reversed)
                    {
                        StructuralSectionLayer turned = Copy(l, l.Face, l.Count, l.Name);
                        turned.Start = Reversed(l.End); turned.End = Reversed(l.Start);
                        l = turned;
                    }
                }
                else if (l.Extend == StructuralSectionLayer.ExtendFromEnd)
                {
                    // A BASTON: from one end of the member, a declared run - mm, or a share of the
                    // MEASURED length (the solid's, not the clear span, which needs the supports).
                    double run = l.RunMm ?? l.RunRatio.Value * f.LengthMm;
                    if (l.FromEnd == "start") { u0 = f.UMin + end; u1 = u0 + run; }
                    else { u1 = f.UMax - end; u0 = u1 - run; }
                    if (u0 < f.UMin + end - 1e-6 || u1 > f.UMax - end + 1e-6)
                        return Refuse(CodeExtentOutside, "layer '" + (l.Name ?? l.Face) + "' runs " + Mm(run) + " mm from the " +
                                      l.FromEnd + " and element " + f.ElementId + " is " + Mm(f.LengthMm) + " mm long less its end covers.");
                }
                else { u0 = f.UMin + end; u1 = f.UMax - end; }
                if (u1 <= u0) return Refuse(CodeNoRoom, "element " + f.ElementId + " is too short for its end covers.");
                // TWO LAYERS ON ONE FACE OVER THE SAME STRETCH are spread across the same width, so
                // their bars would stand in each other - a continuous pair and bastones on one face,
                // say. Where a schedule means bastones BETWEEN the continuous bars, that placement is
                // a decision this build does not make: refused, not laid on top of each other.
                foreach (Tuple<string, double, double, string> other in laid)
                    if (other.Item1 == l.Face && other.Item4 != l.Between && Math.Min(other.Item3, u1) - Math.Max(other.Item2, u0) > 1e-6 &&
                        StationsShare(laidCount.TryGetValue(other.Item4, out int oc) ? oc : 0, l.Count))
                        return Refuse(CodeLayersCoincide, "layers '" + other.Item4 + "' and '" + (l.Name ?? l.Face) + "' are both on the " +
                                      l.Face + " face over " + Mm(Math.Min(other.Item3, u1) - Math.Max(other.Item2, u0)) + " mm of element " +
                                      f.ElementId + ", and both are spread across the same width: their bars would coincide. Declare " +
                                      "them as one layer, or give them extents that do not overlap.");
                laid.Add(Tuple.Create(l.Face, u0, u1, l.Name ?? l.Face));
                laidCount[l.Name ?? l.Face] = l.Count;

                double k = rule.Stirrups != null && stirrupBendOf != null
                    ? CornerInsetMm(stirrupBendOf(rule.Stirrups.BarTypeId), db) : 0;
                double hookPass = rule.Stirrups != null && stirrupBendOf != null ? sd / 2 : 0;
                double vA = vIn0 + db / 2 + k, vB = vIn1 - db / 2 - k;
                double wA = wIn0 + db / 2 + k, wB = wIn1 - db / 2 - k - hookPass;
                if (vB < vA || wB < wA) return Refuse(CodeNoRoom, "layer '" + (l.Name ?? l.Face) + "' does not fit inside the stirrup.");

                double[] start, startEnd, normal; double spread; int n = l.Count;
                double vLine, wLine, dvIn = 0, dwIn = 0;   // the layer's first bar, and the way into the section
                switch (l.Face)
                {
                    case StructuralSectionLayer.FaceTop:
                    case StructuralSectionLayer.FaceBottom:
                    {
                        double w = l.Face == StructuralSectionLayer.FaceTop ? wB : wA;
                        double vFirst = n == 1 ? (vA + vB) / 2 : vA;
                        spread = n == 1 ? 0 : vB - vA;
                        if (l.Between != null)
                        {
                            // IN THE GAPS of the named layer: one bar at the middle of each, on its line.
                            Tuple<string, double, double, double, int> held;
                            Tuple<string, double, double, string> heldRun = laid.LastOrDefault(x => x.Item4 == l.Between);
                            if (!stations.TryGetValue(l.Between, out held) || heldRun == null)
                                return Refuse(CodeBetweenNotLaid, "layer '" + (l.Name ?? l.Face) + "' is laid between the bars of '" + l.Between +
                                              "', which is not laid in element " + f.ElementId + ": there are no gaps to lay it in.");
                            // One bar in each gap - or, in a SINGLE gap (a pair at the corners), several bars
                            // evenly across it: 2#5 continuous + 2#5 bastones is four bars at thirds.
                            int gaps = held.Item5 - 1, perGap = gaps == 1 ? n : 1;
                            if (n != gaps && !(gaps == 1 && n > 1))
                                return Refuse(CodeBetweenCount, "layer '" + (l.Name ?? l.Face) + "' has " + n + " bar(s) and '" + l.Between + "' has " +
                                              held.Item5 + ", so " + gaps + " gap(s): one bar in each gap is " + gaps + "; several bars go only in a single gap.");
                            if (u0 < heldRun.Item2 - 1e-6 || u1 > heldRun.Item3 + 1e-6)
                                return Refuse(CodeBetweenOutside, "layer '" + (l.Name ?? l.Face) + "' runs " + Mm(u0 - f.UMin) + ".." + Mm(u1 - f.UMin) +
                                              " mm along element " + f.ElementId + " and '" + l.Between + "' " + Mm(heldRun.Item2 - f.UMin) + ".." +
                                              Mm(heldRun.Item3 - f.UMin) + " mm: past it there are no gaps.");
                            double heldDb = heldDiameter.TryGetValue(l.Between, out double hd) ? hd : db;
                            double pitch = held.Item4 / (perGap + 1);
                            if (pitch < (db + heldDb) / 2 - 1e-6 || (perGap > 1 && pitch < db - 1e-6))
                                return Refuse(CodeBetweenNoRoom, "layer '" + (l.Name ?? l.Face) + "': the gaps of '" + l.Between + "' are " + Mm(held.Item4) +
                                              " mm centre to centre, and " + perGap + " bar(s) of " + Mm(db) + " mm evenly in one stand in a " + Mm(heldDb) + " mm bar beside them.");
                            vFirst = held.Item2 + pitch;
                            spread = perGap > 1 ? pitch * (n - 1) : held.Item4 * (n - 1);
                            w = held.Item3;
                        }
                        start = f.At(u0, vFirst, w); startEnd = f.At(u1, vFirst, w); normal = f.V;
                        vLine = vFirst; wLine = w; dwIn = l.Face == StructuralSectionLayer.FaceTop ? -1 : 1;
                        break;
                    }
                    default:   // left / right: intermediate bars between the bottom and top rows
                    {
                        double v = l.Face == StructuralSectionLayer.FaceLeft ? vA : vB;
                        double step = (wB - wA) / (n + 1);
                        double wFirst = wA + step;
                        spread = step * (n - 1);
                        start = f.At(u0, v, wFirst); startEnd = f.At(u1, v, wFirst); normal = f.W;
                        vLine = v; wLine = wFirst; dvIn = l.Face == StructuralSectionLayer.FaceLeft ? 1 : -1;
                        break;
                    }
                }
                // THE PIECES: one bar end to end, or - with laps - one piece per stretch between
                // splices, each running half a lap past its splice so consecutive pieces overlap
                // by the declared length. Only the layer's own ends keep its terminations.
                var cuts = new List<double[]>();   // [station, half lap, lap]; a coupler (L7) has no overlap
                // A COUPLED LAYER (L7): every splice a coupler, its pieces collinear and meeting at the
                // station. The parser refuses a layer that mixes couplers and laps.
                string coupler = l.Laps.Count > 0 ? l.Laps[0].CouplerTypeName : null;
                foreach (StructuralLap lap in l.Laps)
                {
                    double at = lap.AtMm.HasValue ? lapAt(lap.AtMm.Value) : f.UMin + lap.AtRatio.Value * f.LengthMm;
                    double len = lap.CouplerTypeName != null ? 0 : lap.LengthMm ?? lap.LengthDb.Value * db;
                    cuts.Add(new[] { at, len / 2, len });
                }
                cuts.Sort((a, b) => a[0].CompareTo(b[0]));
                for (int c = 0; c < cuts.Count; c++)
                {
                    if (cuts[c][0] - cuts[c][1] <= u0 + 1e-6 || cuts[c][0] + cuts[c][1] >= u1 - 1e-6)
                        return Refuse(CodeLapOutside, "layer '" + (l.Name ?? l.Face) + "': the " +
                                      (coupler != null ? "coupler at " + Mm(cuts[c][0] - f.UMin) + " mm" : "lap at " + Mm(cuts[c][0] - f.UMin) +
                                      " mm, " + Mm(cuts[c][2]) + " mm long,") + " does not lie inside the bar, which runs from " +
                                      Mm(u0 - f.UMin) + " to " + Mm(u1 - f.UMin) + " mm along element " + f.ElementId + ".");
                    if (c > 0 && cuts[c][0] - cuts[c][1] <= cuts[c - 1][0] + cuts[c - 1][1] + 1e-6)
                        return Refuse(CodeLapsOverlap, "layer '" + (l.Name ?? l.Face) + "': the laps at " + Mm(cuts[c - 1][0] - f.UMin) +
                                      " and " + Mm(cuts[c][0] - f.UMin) + " mm overlap each other, which leaves no piece between them.");
                }
                // MORE THAN ONE COUPLER (stage 1b, lot 5): the first piece runs from its station back, the middle
                // pieces forward - coupled at their start (end 0) and at their end (end 1) - and the last forward.
                double du = u1 - u0;
                string layerId = LayerId(rule.Id, f.ElementId, l.Name ?? l.Face);
                stations[l.Name ?? l.Face] = Tuple.Create(l.Face, vLine, wLine, n > 1 ? spread / (n - 1) : 0.0, n);
                heldDiameter[l.Name ?? l.Face] = db;
                // A BAYONETA: straight to the station, inward over the run, and on. Planar, because
                // it moves only along the layer's own inward direction - the set still marches across.
                if (l.Crank != null && l.Laps.Count > 0)
                    return Refuse(CodeCrankWithLaps, "layer '" + (l.Name ?? l.Face) + "' declares a crank and laps; a cranked bar " +
                                  "that is also spliced is a placement this build does not lay. Declare the crank on the piece's own layer.");
                List<double[]> cranked = null;
                if (l.Crank != null)
                {
                    double ca = f.UMin + l.Crank.AtMm, cb = ca + l.Crank.RunMm;
                    if (ca <= u0 + 1e-6 || cb >= u1 - 1e-6)
                        return Refuse(CodeCrankOutside, "layer '" + (l.Name ?? l.Face) + "': the crank from " + Mm(l.Crank.AtMm) + " over " +
                                      Mm(l.Crank.RunMm) + " mm does not lie inside the bar, which runs from " + Mm(u0 - f.UMin) + " to " +
                                      Mm(u1 - f.UMin) + " mm along element " + f.ElementId + ".");
                    double iv = dvIn * l.Crank.InwardMm, iw = dwIn * l.Crank.InwardMm;
                    cranked = new List<double[]> { f.At(u0, vLine, wLine), f.At(ca, vLine, wLine),
                                                   f.At(cb, vLine + iv, wLine + iw), f.At(u1, vLine + iv, wLine + iw) };
                }
                // D4: every other piece sits one diameter across, in contact - into the section by
                // default. It must stay inside the cage the bars are laid in (the stirrup's, or the
                // cover's): the whole layer moves, so its first AND last bar are checked.
                double side = l.Laps.Count > 0 && l.Laps[0].Toward == StructuralLap.TowardOutside ? -1 : 1;
                // A COUPLED LAYER does not move across: its pieces meet end to end on the layer's line.
                double dv = coupler != null ? 0 : dvIn * side * db, dw = coupler != null ? 0 : dwIn * side * db;
                if (cuts.Count > 0 && coupler == null)
                {
                    double vLast = dvIn != 0 ? vLine : vLine + spread, wLast = dwIn != 0 ? wLine : wLine + spread;
                    double lo = -1e-6;
                    if (Math.Min(vLine, vLast) + dv - (vIn0 + db / 2) < lo || (vIn1 - db / 2) - (Math.Max(vLine, vLast) + dv) < lo ||
                        Math.Min(wLine, wLast) + dw - (wIn0 + db / 2) < lo || (wIn1 - db / 2) - (Math.Max(wLine, wLast) + dw) < lo)
                        return Refuse(CodeLapOffsetNoRoom, "layer '" + (l.Name ?? l.Face) + "': its lapped pieces sit one bar diameter (" +
                                      Mm(db) + " mm) toward the " + (side > 0 ? "inside" : "outside") + " of the section, and there " +
                                      "that leaves the bars' cover in element " + f.ElementId + ". Ask the other direction, or reduce the cover.");
                }
                for (int piece = 0; piece <= cuts.Count; piece++)
                {
                    double a = piece == 0 ? u0 : cuts[piece - 1][0] - cuts[piece - 1][1];
                    double b = piece == cuts.Count ? u1 : cuts[piece][0] + cuts[piece][1];
                    double off = piece % 2 == 1 ? 1 : 0;
                    double[] pa = f.At(a, vLine + dv * off, wLine + dw * off), pb = f.At(b, vLine + dv * off, wLine + dw * off);
                    // THE PIECE BEFORE A COUPLER RUNS FROM THE STATION BACK, so the coupler is at its end 0 -
                    // where Revit puts it, reversing the bar otherwise (measured, Revit 2024 and 2026).
                    bool fromStation = coupler != null && piece == 0;
                    // THE FIRST PIECE OF A LAPPED LAYER, hooked at its start only, is laid from its lap end back to
                    // the hook (measured, Revit 2023, Casa Duelo 2026-10-15): laid hook-first, Revit matched the
                    // straight-with-one-hook shape the other way round - hook at the lap end, the bar 1030 mm off
                    // the plan - and would not hold it. A dowel's anchorage names its start, so it is left alone.
                    bool hookLast = !fromStation && coupler == null && cranked == null && cuts.Count > 0 && piece == 0 &&
                                    l.Start != null && l.Start.HookTypeId != null && l.Anchorage.Count == 0;
                    string id = cuts.Count == 0 ? layerId : layerId + "#p" + (piece + 1).ToString(CultureInfo.InvariantCulture);
                    var layout = n == 1
                        ? new RebarLayoutRequest { Layout = RebarLayout.Single }
                        : new RebarLayoutRequest { Layout = RebarLayout.FixedNumber, Number = n, ArrayLengthMm = spread };
                    bars.Add(new StructuralRebarRule
                    {
                        Id = id,
                        Host = new StructuralHostSelector { ElementIds = new List<long> { f.ElementId }, SourceSelector = rule.Host?.SourceSelector },
                        BarTypeId = l.BarTypeId,
                        ShapeName = l.ShapeName,
                        AllowNewShape = l.AllowNewShape,
                        Style = StructuralStyle.Standard,
                        CurvesMm = cranked ?? (fromStation || hookLast ? new List<double[]> { pb, pa } : new List<double[]> { pa, pb }),
                        Closed = false,
                        NormalMm = new[] { normal[0], normal[1], normal[2] },
                        BarsOnNormalSide = true,
                        Layout = layout,
                        Start = fromStation || hookLast ? new StructuralTermination() : piece == 0 ? l.Start : new StructuralTermination(),
                        End = fromStation || hookLast ? Reversed(l.Start) : piece == cuts.Count ? l.End : new StructuralTermination(),
                        Mark = l.Mark ?? rule.Mark,
                        Required = rule.Required,
                        LapNextMm = piece < cuts.Count && coupler == null ? cuts[piece][2] : (double?)null,
                        LapNextRuleId = piece < cuts.Count && coupler == null ? layerId + "#p" + (piece + 2).ToString(CultureInfo.InvariantCulture) : null,
                        LapNextAcrossMm = piece < cuts.Count && coupler == null ? db : (double?)null,
                        CouplerNextTypeName = piece < cuts.Count ? coupler : null,
                        CouplerNextRuleId = piece < cuts.Count && coupler != null ? layerId + "#p" + (piece + 2).ToString(CultureInfo.InvariantCulture) : null,
                        CoupledAtStart = coupler != null && (piece > 0 || fromStation),
                        CouplerNextEnd = fromStation ? 0 : 1,
                        // THE ROW AT THE HOOKS' CORNER (measured, Revit 2024 and 2026): identical beams in one
                        // model came out with the hooks' second pass along either leg - by position - and the
                        // corner bar seated in that pass's bend, 0 or 3.964 mm off the plan. Half a stirrup.
                        HookCornerSeatMm = hookPass > 0 && l.Face == StructuralSectionLayer.FaceTop ? hookPass : (double?)null,
                        // WHOSE BAR THIS IS (overlapping_bar): the declared rule, its cage and its part in it.
                        OriginRuleId = rule.Id, CageId = CageIdOf(rule.Id, f.ElementId), CageRole = CageLongitudinal,
                        Into = new List<long>(l.Into), SectionCoverMm = sectionCover,
                        Anchorage = l.Anchorage.Where(a => (piece == 0 && a.End == StructuralAnchorage.EndStart) ||
                                                           (piece == cuts.Count && a.End == StructuralAnchorage.EndEnd)).ToList(),
                        Raw = rule.Raw
                    });
                }
            }

            // Stirrups measured from named axes that lie wholly in another host's stretch are that host's.
            bool stirrupsHere = true;
            if (rule.Stirrups != null && rule.StirrupsAxis != null)
            {
                SectionResult ax = AxisRange(f, rule.StirrupsAxis, rule.StirrupsToward, rule.StirrupsFromMm, rule.StirrupsToMm,
                                             "the stirrups", out double t0, out double t1, out _);
                if (ax != null) return ax;
                if (t1 <= f.UMin + 1e-6 || t0 >= f.UMax - 1e-6) { stirrupsHere = false; result.NotHere.Add("the stirrups"); }
            }
            if (rule.Stirrups != null && stirrupsHere)
            {
                // The stirrup's CENTRELINE: half a stirrup diameter inside the cover, on every face.
                double v0 = f.VMin + left + sd / 2, v1 = f.VMax - right - sd / 2;
                double w0 = f.WMin + bottom + sd / 2, w1 = f.WMax - top - sd / 2;
                StructuralStirrupZoneRule z = rule.Stirrups.CloneFor(
                    LayerId(rule.Id, f.ElementId, "stirrups"),
                    new StructuralHostSelector { ElementIds = new List<long> { f.ElementId }, SourceSelector = rule.Host?.SourceSelector },
                    // STARTS AT THE TOP-LEFT CORNER: Revit puts a closed stirrup's hooks at the
                    // first corner, and hooks go in the compression zone at the top.
                    // On a frame that says so, the same cycle from its top corner on the VMax side.
                    f.HookCornerAtVMax
                        ? new List<double[]> { f.At(f.UMin, v1, w1), f.At(f.UMin, v0, w1), f.At(f.UMin, v0, w0), f.At(f.UMin, v1, w0) }
                        : new List<double[]> { f.At(f.UMin, v0, w1), f.At(f.UMin, v0, w0), f.At(f.UMin, v1, w0), f.At(f.UMin, v1, w1) },
                    new[] { f.U[0], f.U[1], f.U[2] },
                    f.LengthMm);
                z.Mark = z.Mark ?? rule.Mark;
                z.Required = rule.Required;
                z.OriginRuleId = rule.Id; z.CageId = CageIdOf(rule.Id, f.ElementId); z.CageRole = CageStirrup;
                if (rule.StirrupsAxis != null)
                {
                    SectionResult ax = AxisRange(f, rule.StirrupsAxis, rule.StirrupsToward, rule.StirrupsFromMm, rule.StirrupsToMm,
                                                 "the stirrups", out double s0, out double s1, out bool reversed);
                    if (ax != null) return ax;
                    if (s0 < f.UMin - 1e-6 || s1 > f.UMax + 1e-6)
                        return Refuse(CodeExtentOutside, "the stirrups run " + Mm(rule.StirrupsFromMm) + ".." + Mm(rule.StirrupsToMm) + " mm from axis " +
                                      rule.StirrupsAxis + ", which is " + Mm(s0 - f.UMin) + ".." + Mm(s1 - f.UMin) + " mm along element " +
                                      f.ElementId + "; the element is " + Mm(f.LengthMm) + " mm long.");
                    z.StartOffsetMm = s0 - f.UMin;
                    z.EndOffsetMm = f.UMax - s1;
                    // The zones are declared from the named axis on: laid along U the other way round,
                    // their order turns, and each zone's first bar is its last.
                    if (reversed)
                        z.Zones = z.Zones.AsEnumerable().Reverse().Select(q => new StirrupZoneRequest
                        {
                            Name = q.Name, LengthMm = q.LengthMm, Mark = q.Mark, Layout = q.Layout.SwappedEnds()
                        }).ToList();
                }
                stirrups = z;
                zoneRules.Add(z);

                // CROSSTIES, one per intermediate bar of the named layer, from one stirrup leg to the
                // other, beside the stirrup (half a stirrup and half a tie further along), at the
                // stirrups' stations: they are the stirrup zone rule with a two-point open profile.
                foreach (StructuralSectionTie t in rule.Ties)
                {
                    Tuple<string, double, double, double, int> st;
                    if (!stations.TryGetValue(t.LayerName, out st) || st.Item5 < 1)
                        return Refuse(CodeTieNoBars, "tie '" + t.Name + "' crosses layer '" + t.LayerName + "', which laid no bars here.");
                    double dt = diameterOf(t.BarTypeId);
                    if (dt <= 0) return Refuse(CodeNoDiameter, "tie '" + t.Name + "': its bar type's diameter could not be read.");
                    double uT = f.UMin + sd / 2 + dt / 2;
                    bool acrossWidth = st.Item1 == StructuralSectionLayer.FaceLeft || st.Item1 == StructuralSectionLayer.FaceRight;
                    // left/right layers: every bar is intermediate. top/bottom: the corners are not.
                    int first = acrossWidth ? 0 : 1, last = acrossWidth ? st.Item5 - 1 : st.Item5 - 2;
                    if (last < first)
                        return Refuse(CodeTieNoBars, "tie '" + t.Name + "' crosses layer '" + t.LayerName + "', whose " + st.Item5 +
                                      " bar(s) are all corner bars: there is no intermediate bar to tie.");
                    for (int k = first; k <= last; k++)
                    {
                        List<double[]> line = acrossWidth
                            ? new List<double[]> { f.At(uT, v0, st.Item3 + k * st.Item4), f.At(uT, v1, st.Item3 + k * st.Item4) }
                            : new List<double[]> { f.At(uT, st.Item2 + k * st.Item4, w0), f.At(uT, st.Item2 + k * st.Item4, w1) };
                        StructuralStirrupZoneRule tz = rule.Stirrups.CloneFor(
                            LayerId(rule.Id, f.ElementId, "tie-" + t.Name + "-" + (k + 1).ToString(CultureInfo.InvariantCulture)),
                            new StructuralHostSelector { ElementIds = new List<long> { f.ElementId }, SourceSelector = rule.Host?.SourceSelector },
                            line, new[] { f.U[0], f.U[1], f.U[2] }, f.LengthMm, false);
                        tz.BarTypeId = t.BarTypeId; tz.ShapeName = t.ShapeName; tz.AllowNewShape = t.AllowNewShape;
                        tz.Start = t.Start; tz.End = t.End; tz.Mark = t.Mark ?? rule.Mark; tz.Required = rule.Required;
                        tz.OriginRuleId = rule.Id; tz.CageId = CageIdOf(rule.Id, f.ElementId); tz.CageRole = CageTie;
                        zoneRules.Add(tz);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// A CIRCULAR SECTION (acero 3.0, L2): ring layers - N bars evenly round, the first on +V,
        /// each its own single bar (a set cannot march round a circle) - inside a circular hoop made
        /// of two arcs, half a hoop diameter inside the cover. Face layers are refused: a circle has
        /// no top face.
        /// </summary>
        private static SectionResult ExpandCircular(StructuralSectionRule rule, SectionHostFrame f, Func<string, double> diameterOf,
                                                    double? hostCoverMm, List<StructuralRebarRule> bars,
                                                    List<StructuralStirrupZoneRule> zoneRules)
        {
            StructuralSectionLayer face = rule.Layers.FirstOrDefault(x => x.Face != StructuralSectionLayer.FaceRing);
            if (face != null)
                return Refuse(CodeNotRectangular, "element " + f.ElementId + " is a circular section of radius " + Mm(f.RadiusMm) +
                              " mm, and layer '" + (face.Name ?? face.Face) + "' is laid on a " + face.Face + " face, which a circle " +
                              "does not have. Use face: ring.");
            if (rule.Ties.Count > 0)
                return Refuse(CodeTieNoBars, "element " + f.ElementId + " is circular: a crosstie runs between faces it does not have.");
            double cover;
            if (rule.Cover.FromHost)
            {
                if (!hostCoverMm.HasValue) return Refuse(CodeNoCover, "the rule reads the cover from the host and element " + f.ElementId + " has none.");
                cover = hostCoverMm.Value;
            }
            else if (rule.Cover.CommonMm.HasValue) cover = rule.Cover.CommonMm.Value;
            else
            {
                cover = rule.Cover.TopMm.Value;
                if (new[] { rule.Cover.BottomMm, rule.Cover.LeftMm, rule.Cover.RightMm }.Any(x => Math.Abs(x.Value - cover) > 1e-9))
                    return Refuse(CodeNoCover, "element " + f.ElementId + " is circular, so its side cover is one number; the rule " +
                                  "gives different top, bottom, left and right covers.");
            }
            double end = rule.Cover.FromHost || rule.Cover.CommonMm.HasValue ? cover : rule.Cover.EndMm.Value;
            double sectionCover = Math.Min(cover, end);
            double sd = 0;
            if (rule.Stirrups != null)
            {
                sd = diameterOf(rule.Stirrups.BarTypeId);
                if (sd <= 0) return Refuse(CodeNoDiameter, "the hoop's bar type's diameter could not be read from the model.");
            }
            double vc = (f.VMin + f.VMax) / 2, wc = (f.WMin + f.WMax) / 2;
            foreach (StructuralSectionLayer l in rule.Layers)
            {
                double db = diameterOf(l.BarTypeId);
                if (db <= 0) return Refuse(CodeNoDiameter, "layer '" + (l.Name ?? l.Face) + "': its bar type's diameter could not be read.");
                double rb = f.RadiusMm - cover - sd - db / 2;
                if (rb <= db) return Refuse(CodeNoRoom, "element " + f.ElementId + " has radius " + Mm(f.RadiusMm) + " mm and the cover and hoop leave no ring.");
                double u0, u1;
                if (l.Extend == StructuralSectionLayer.ExtendExplicit)
                {
                    u0 = f.UMin + l.FromMm.Value; u1 = f.UMin + l.ToMm.Value;
                    if (l.Into.Count == 0 && (u0 < f.UMin - 1e-6 || u1 > f.UMax + 1e-6))
                        return Refuse(CodeExtentOutside, "layer '" + (l.Name ?? l.Face) + "' runs outside element " + f.ElementId + ".");
                }
                else if (l.Extend == StructuralSectionLayer.ExtendHostLength) { u0 = f.UMin + end; u1 = f.UMax - end; }
                else return Refuse(CodeClearSpan, "layer '" + (l.Name ?? l.Face) + "': a ring takes host_length or { from_mm, to_mm }.");
                if (u1 <= u0) return Refuse(CodeNoRoom, "element " + f.ElementId + " is too short for its end covers.");
                for (int k = 0; k < l.Count; k++)
                {
                    double a = 2 * Math.PI * k / l.Count;
                    double v = vc + rb * Math.Cos(a), w = wc + rb * Math.Sin(a);
                    // The set's normal: across, square to the bar, in the radial direction - one bar, so it only fixes the plane.
                    double[] radial = { Math.Cos(a) * f.V[0] + Math.Sin(a) * f.W[0], Math.Cos(a) * f.V[1] + Math.Sin(a) * f.W[1], Math.Cos(a) * f.V[2] + Math.Sin(a) * f.W[2] };
                    bars.Add(new StructuralRebarRule
                    {
                        Id = LayerId(rule.Id, f.ElementId, l.Name ?? l.Face) + "#b" + (k + 1).ToString(CultureInfo.InvariantCulture),
                        Host = new StructuralHostSelector { ElementIds = new List<long> { f.ElementId }, SourceSelector = rule.Host?.SourceSelector },
                        BarTypeId = l.BarTypeId, ShapeName = l.ShapeName, AllowNewShape = l.AllowNewShape, Style = StructuralStyle.Standard,
                        CurvesMm = new List<double[]> { f.At(u0, v, w), f.At(u1, v, w) }, Closed = false,
                        NormalMm = radial, BarsOnNormalSide = true, Layout = new RebarLayoutRequest { Layout = RebarLayout.Single },
                        Start = l.Start, End = l.End, Mark = l.Mark ?? rule.Mark, Required = rule.Required,
                        OriginRuleId = rule.Id, CageId = CageIdOf(rule.Id, f.ElementId), CageRole = CageLongitudinal,
                        Into = new List<long>(l.Into), SectionCoverMm = sectionCover,
                        Anchorage = new List<StructuralAnchorage>(l.Anchorage), Raw = rule.Raw
                    });
                }
            }
            if (rule.Stirrups != null)
            {
                double rh = f.RadiusMm - cover - sd / 2;
                StructuralStirrupZoneRule z = rule.Stirrups.CloneFor(
                    LayerId(rule.Id, f.ElementId, "stirrups"),
                    new StructuralHostSelector { ElementIds = new List<long> { f.ElementId }, SourceSelector = rule.Host?.SourceSelector },
                    new List<double[]> { f.At(f.UMin, vc + rh, wc), f.At(f.UMin, vc - rh, wc) },
                    new[] { f.U[0], f.U[1], f.U[2] }, f.LengthMm);
                z.ProfileArcThrough = new Dictionary<int, double[]> { [0] = f.At(f.UMin, vc, wc + rh), [1] = f.At(f.UMin, vc, wc - rh) };
                // PLACED FROM A SHAPE (L7): the hoop's centre on the axis at the start of the span, the
                // shape's x and y mapped to the section's V and W, the centreline radius half a hoop
                // inside the cover. The zone expansion moves it to each station.
                StructuralShapePlacement sp = rule.Stirrups.ShapePlacement;
                if (sp != null)
                    z.PlacementTemplate = new PlacedShapeGeometry
                    {
                        Shape = sp.Shape, Kind = sp.Kind,
                        CentreMm = f.At(f.UMin, vc, wc),
                        AxisMm = new[] { f.U[0], f.U[1], f.U[2] },
                        XVec = new[] { f.V[0], f.V[1], f.V[2] },
                        YVec = new[] { f.W[0], f.W[1], f.W[2] },
                        CentrelineRadiusMm = rh,
                        PitchMm = sp.PitchMm, BaseFinishingTurns = sp.BaseFinishingTurns, TopFinishingTurns = sp.TopFinishingTurns
                    };
                z.Mark = z.Mark ?? rule.Mark;
                z.Required = rule.Required;
                z.OriginRuleId = rule.Id; z.CageId = CageIdOf(rule.Id, f.ElementId); z.CageRole = CageStirrup;
                zoneRules.Add(z);
            }
            return new SectionResult();
        }

        private static StructuralSectionLayer Copy(StructuralSectionLayer l, string face, int count, string name) =>
            new StructuralSectionLayer
            {
                Name = name, Face = face, Count = count, BarTypeId = l.BarTypeId, ShapeName = l.ShapeName,
                AllowNewShape = l.AllowNewShape, Extend = l.Extend, FromMm = l.FromMm, ToMm = l.ToMm,
                // EVERY extent field: a perimeter baston split into its faces lost its run and failed.
                FromEnd = l.FromEnd, RunMm = l.RunMm, RunRatio = l.RunRatio, Axis = l.Axis, Toward = l.Toward,
                Start = l.Start, End = l.End, Mark = l.Mark, Laps = l.Laps, Into = l.Into, Anchorage = l.Anchorage,
                Crank = l.Crank, Between = l.Between
            };

        /// <summary>
        /// Where a host's axis (origin + t U, mm) crosses a grid drawn from g0 to g1 (mm), in PLAN: the
        /// t of the crossing, or null with the problem ("axis_parallel_to_host: ..."). A grid is a
        /// line in plan, so a host's axis crosses it at one station whatever its slope; a grid drawn
        /// short of the host still names that line. Shallower than one degree is parallel.
        /// </summary>
        public static double? AxisStation(double[] originMm, double[] u, double[] g0, double[] g1, string name, out string problem)
        {
            problem = null;
            double ux = u[0], uy = u[1], dx = g1[0] - g0[0], dy = g1[1] - g0[1];
            double nu = Math.Sqrt(ux * ux + uy * uy), nd = Math.Sqrt(dx * dx + dy * dy);
            double cross = ux * dy - uy * dx;
            if (nu < 1e-9 || nd < 1e-9 || Math.Abs(cross) / (nu * nd) < Math.Sin(Math.PI / 180))
            {
                problem = CodeAxisParallel + ": grid '" + name + "' runs along the host's axis in plan (or the host is vertical), so it does not mark a station on it.";
                return null;
            }
            double wx = g0[0] - originMm[0], wy = g0[1] - originMm[1];
            return (wx * dy - wy * dx) / cross;
        }

        /// <summary>
        /// Stations from the grid named `axis`, positive toward the grid named `toward`, as U on the
        /// host's frame: u0 &lt;= u1, and reversed when `toward` lies at lower U. Refused by name when
        /// a grid could not be placed, when both cross the host's axis at one point, or when the
        /// host does not lie between them at all.
        /// </summary>
        public static SectionResult AxisRange(SectionHostFrame f, string axis, string toward, double fromMm, double toMm, string what,
                                              out double u0, out double u1, out bool reversed)
        {
            u0 = u1 = 0; reversed = false;
            foreach (string n in new[] { axis, toward })
            {
                if (f.AxisProblems.TryGetValue(n, out string problem))
                {
                    int colon = problem.IndexOf(':');
                    return Refuse(colon > 0 ? problem.Substring(0, colon) : CodeAxisNotInModel, what + ": " + (colon > 0 ? problem.Substring(colon + 1).Trim() : problem));
                }
                if (!f.AxisStations.ContainsKey(n))
                    return Refuse(CodeAxisNotInModel, what + " is measured from axis '" + n + "', and no grid of that name was read for element " + f.ElementId + ".");
            }
            double a = f.AxisStations[axis], b = f.AxisStations[toward];
            if (Math.Abs(b - a) < 1.0)
                return Refuse(CodeAxesCoincide, what + ": axes '" + axis + "' and '" + toward + "' cross element " + f.ElementId +
                              "'s axis at the same point, so which way is toward '" + toward + "' is not defined.");
            if (Math.Max(a, b) <= f.UMin + 1e-6 || Math.Min(a, b) >= f.UMax - 1e-6)
                return Refuse(CodeHostNotBetweenAxes, what + ": element " + f.ElementId + " runs " + Mm(f.UMin) + ".." + Mm(f.UMax) +
                              " along its axis and the stretch between axes '" + axis + "' and '" + toward + "' is " + Mm(Math.Min(a, b)) + ".." +
                              Mm(Math.Max(a, b)) + ": the element is not between them.");
            double dir = b > a ? 1 : -1;
            reversed = dir < 0;
            double p = a + dir * fromMm, q = a + dir * toMm;
            u0 = Math.Min(p, q); u1 = Math.Max(p, q);
            return null;
        }

        private static string Mm(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
    }
}
