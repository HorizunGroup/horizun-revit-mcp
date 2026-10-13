// -----------------------------------------------------------------------------
// Horizun Revit MCP - bars of DIFFERENT rules measured against each other.
// Original Horizun code. No Revit types: both the plan's rows and the model's
// bars arrive as the JSON ReinforcementResolver and RebarFacts emit, so every
// excuse and every finding here is provable at a desk.
//
// WHY THIS EXISTS. MEASURED live on Revit 2024 and 2026 (acero 3.0, L5): a wall's
// horizontal bars laid through a boundary element's stirrups came out pushed by
// Revit until they touched them - the array 12.5 mm short - and nothing said so.
// Revit moves a bar that crosses another. Every check this bridge had looked at
// one bar against its host; none looked at two bars against each other, and the
// audit published that gap as `overlapping_bar` in not_checked.
//
// WHAT IS MEASURED. Each bar is its centreline polyline (arcs already sampled),
// at every position of its set, with the MODEL diameter's radius. Two bars are
// compared segment by segment, exactly: the clear distance is the distance
// between the two centrelines less both radii. A grid of cells is the broad
// phase - a building set is ~20,000 bars and all pairs is not an option - and
// only pieces that share a cell are measured.
//
// WHAT IS NOT A FINDING, and why (each one has a test):
//   - two bars of ONE set or ONE layout (the zones of a stirrup zone rule, the
//     pieces of a mat component): their spacing is the layout's;
//   - a DECLARED lap: the pieces are laid one diameter across, in contact (D4),
//     and the lap check measures them (lap_insufficient);
//   - a DECLARED coupler: the pieces meet end to end at the station, and the
//     coupler check measures them (missing_coupler, coupled_end_seated);
//   - a cage's stirrups and ties against its OWN bars (same section or wall
//     boundary rule, same host): the stirrup is laid against the bars, Revit
//     seats the corner bars in its bends (L10), and a tie is declared through
//     the centre of the bar it encloses. Two longitudinal bars of one cage, two
//     ties, or a bar of another cage are still compared;
//   - a hook: the plan's centrelines carry none, and the audit cuts the hook off
//     the drawn bar where the hook-free centreline ends. Where Revit bends a
//     hook round another bar is Revit's drawing of a declared termination.
// Anything else closer than declared - or THROUGH another bar, declared or not -
// is a finding.
//
// THE SAME BAR TWICE (duplicate_bar). Two bars of different rules, or of two sets,
// laid on ONE centreline - every point of each within the length tolerance of the
// other, the same model diameter - are not two bars crossing: they are one bar
// built twice, and the takeoff counts its steel twice. A duplicate is the limiting
// case of an intersection, so it is found FIRST and its bar pairs are taken out of
// the intersection test: the pair is reported once, as a duplicate. It is never
// excused - a lap, a coupler or a cage is a contact between two different bars.
// A member marked DuplicateOnly (a bar no rule of the set built, in the audit) is
// compared for duplicates only: whether it crosses the set's bars is not judged
// there (overlap_with_unattributed_bar; the model-wide steel review judges it).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One set of bars - one rule in one host - as the overlap check sees it.</summary>
    public sealed class OverlapMember
    {
        /// <summary>The caller's own index (the plan's row index), carried through untouched.</summary>
        public int Tag = -1;
        public string RuleId;
        public long HostId = -1;
        /// <summary>The bar set in the model (audit), or -1 (plan).</summary>
        public long RebarId = -1;
        public string DeclaredRuleId;
        public string Layout;
        public string CageId;
        public string CageRole;
        public string LapNextRuleId;
        public string CouplerNextRuleId;
        public double RadiusMm;
        public List<OverlapBar> Bars = new List<OverlapBar>();
        /// <summary>Hooked ends cut off the drawn bar before measuring (audit). Published, never silent.</summary>
        public int HooksNotMeasured;
        /// <summary>Why this set could not be measured. Unknown is never a pass.</summary>
        public string UnreadableWhy;
        /// <summary>
        /// Compared for duplicate_bar only, never for intersections or clearances: a bar no rule of the audited
        /// set built (hand modelling, another set, an area or path system's bar).
        /// </summary>
        public bool DuplicateOnly;
        /// <summary>What this member is when no rule names it: rebar_set, area or path (the steel review). Null for a rule's set.</summary>
        public string Label;
        /// <summary>The category of its host, as Revit names it, when the reader knows it (the steel review counts by it).</summary>
        public string HostCategory;
        /// <summary>The area or path system whose bar this is (the steel review), or -1.</summary>
        public long SystemId = -1;
        /// <summary>standard or stirrup_tie, when the reader knows it (the steel review counts intersections by the pair of styles).</summary>
        public string Style;
        /// <summary>
        /// The bar sets a COUPLER IN THE MODEL joins to this one (the steel review reads them off the bars). Two bars a
        /// Revit coupler links meet end to end at it by construction - a fact of the model, not a declaration.
        /// </summary>
        public HashSet<long> CoupledTo = new HashSet<long>();
        /// <summary>
        /// The centre of the host's bounding box (plan only): the side a declared crossing_order's
        /// `toward_host_core` displacement goes to. Null when it was not read - and then nothing is derived from it.
        /// </summary>
        public double[] HostCentreMm;

        public string LayoutKey => (Layout ?? RuleId) + "\u0001" + HostId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One bar of a set: its position index and its centreline in model millimetres.</summary>
    public sealed class OverlapBar
    {
        public int Position;
        public List<double[]> Points;
    }

    /// <summary>Two sets whose bars come closer than they may: the worst bar pair, and how many bar pairs do.</summary>
    public sealed class OverlapPair
    {
        public OverlapMember A, B;
        public int PositionA, PositionB;
        public string Kind, Relation;
        public double ClearMm, RequiredClearMm;
        public double[] PointMm;
        /// <summary>The two bars' directions (unit) at the worst point: what crossing_order's toward_host_core crosses.</summary>
        public double[] DirA, DirB;
        public int BarPairs;
        /// <summary>duplicate_bar: the farthest any point of one centreline stands from the other (within the tolerance).</summary>
        public double DeviationMm;
        /// <summary>duplicate_bar: the coinciding bar pairs, by position, oriented as A and B (the first few).</summary>
        public List<int[]> PositionPairs = new List<int[]>();

        public bool IsDuplicate => Kind == RebarOverlap.KindDuplicate;

        public JObject ToJson(bool fromB = false)
        {
            OverlapMember me = fromB ? B : A, other = fromB ? A : B;
            var o = new JObject
            {
                ["rule_id"] = me.RuleId,
                ["host_id"] = me.HostId,
                ["rebar_id"] = me.RebarId,
                ["position"] = fromB ? PositionB : PositionA,
                ["other_rule_id"] = other.RuleId,
                ["other_host_id"] = other.HostId,
                ["other_rebar_id"] = other.RebarId,
                ["other_position"] = fromB ? PositionA : PositionB,
                ["kind"] = Kind,
                ["relation"] = Relation,
                ["clear_mm"] = Math.Round(ClearMm, 3),
                ["required_clear_mm"] = Math.Round(RequiredClearMm, 3),
                ["point_mm"] = new JArray(Math.Round(PointMm[0], 3), Math.Round(PointMm[1], 3), Math.Round(PointMm[2], 3)),
                ["bar_pairs"] = BarPairs
            };
            if (me.Label != null || other.Label != null)
            {
                o["what"] = me.Label;
                o["other_what"] = other.Label;
            }
            if (me.SystemId > 0 || other.SystemId > 0)
            {
                o["system_id"] = me.SystemId > 0 ? (JToken)me.SystemId : JValue.CreateNull();
                o["other_system_id"] = other.SystemId > 0 ? (JToken)other.SystemId : JValue.CreateNull();
            }
            if (IsDuplicate)
            {
                o["max_deviation_mm"] = Math.Round(DeviationMm, 3);
                o["position_pairs"] = new JArray(PositionPairs.Select(pp => (object)new JArray(fromB ? pp[1] : pp[0], fromB ? pp[0] : pp[1])).ToArray());
                if (BarPairs > PositionPairs.Count) o["position_pairs_not_listed"] = BarPairs - PositionPairs.Count;
            }
            return o;
        }
    }

    /// <summary>What the plan does with a set that clashes: refuse it, by code, naming the other set.</summary>
    public sealed class OverlapRefusal
    {
        public int Tag;
        public string Code;
        public string Why;
        public JArray Overlaps = new JArray();
    }

    public sealed class OverlapReport
    {
        public List<OverlapPair> Pairs = new List<OverlapPair>();
        public List<OverlapMember> Unreadable = new List<OverlapMember>();
        /// <summary>Set pairs that came closer than they may and were excused, by reason; hooks are counted per hooked end.</summary>
        public Dictionary<string, int> Excused = new Dictionary<string, int>(StringComparer.Ordinal);
        public string Checked;
        public StructuralClearances Clearances;
        public double ToleranceMm, CellMm;
        public int SetsCompared, BarsCompared, DuplicateOnlySets;
        public long Segments, Pieces, CandidatePairs, ExactTests;
        public long ElapsedMs;

        public JObject ToJson()
        {
            var excused = new JObject();
            foreach (var kv in Excused.OrderBy(k => k.Key, StringComparer.Ordinal)) excused[kv.Key] = kv.Value;
            var overlaps = new JArray();
            foreach (OverlapPair p in Pairs.Take(RebarOverlap.MaxPairsPublished)) overlaps.Add(p.ToJson());
            var o = new JObject
            {
                ["checked"] = Checked,
                ["checked_means"] = Checked == RebarOverlap.CheckedIntersectionsOnly
                    ? "the set declares no clearances, so only bars that pass through each other were looked for (clear " +
                      "distance below zero, beyond the length tolerance) - Revit moves such a bar until it touches the other. " +
                      "Bars close together were not judged: declare clearances.between_rules_mm to have them judged."
                    : "bars of different rules were held to the declared clearances: between_rules_mm (or a declared pair) where " +
                      "they run alongside each other, crossing_mm where they cross; and bars that pass through each other are " +
                      "always a finding.",
                ["clearances"] = Clearances == null ? null : Clearances.ToJson(),
                ["alongside_within_degrees"] = RebarOverlap.AlongsideDegrees,
                ["tolerance_mm"] = ToleranceMm,
                ["sets_compared"] = SetsCompared,
                ["bars_compared"] = BarsCompared,
                ["duplicates"] = new JObject
                {
                    ["set_pairs"] = Pairs.Count(p => p.IsDuplicate),
                    ["bar_pairs"] = Pairs.Where(p => p.IsDuplicate).Sum(p => p.BarPairs),
                    ["same_diameter_within_mm"] = RebarOverlap.SameDiameterMm,
                    ["means"] = "bars of different rules (or sets) laid on ONE centreline: every point of each within " +
                                "tolerance_mm of the other, the same model diameter. One bar built twice - its steel " +
                                "counted twice. Reported as kind duplicate (duplicate_bar), never also as an intersection."
                },
                ["segments"] = Segments,
                ["broad_phase"] = new JObject
                {
                    ["cell_mm"] = Math.Round(CellMm, 3),
                    ["pieces"] = Pieces,
                    ["candidate_pairs"] = CandidatePairs,
                    ["exact_tests"] = ExactTests
                },
                ["excused"] = excused,
                ["excused_means"] =
                    "set pairs that DID come closer than they may and are not findings by design: declared_lap (the pieces of a " +
                    "declared lap, in contact - lap_insufficient measures them), declared_coupler (the pieces a declared coupler " +
                    "joins end to end), cage_ties_its_own_bars (a section or wall boundary rule's stirrups and ties against its " +
                    "own bars in the same host). hooks_not_measured counts hooked ends left out of the measurement. Bars of one " +
                    "set or one layout are never compared with each other.",
                ["not_compared"] =
                    "for intersections and clearances: bars no rule of this set builds (other sets, hand modelling), " +
                    "area/path/fabric systems' bars, and in a chunked run the bars of other pieces. The audit compares " +
                    "the bars in its hosts that no rule built for duplicate_bar only (duplicate_only_sets); fabric is " +
                    "never compared.",
                ["duplicate_only_sets"] = DuplicateOnlySets,
                ["unreadable"] = new JArray(Unreadable.Select(m => (object)new JObject
                {
                    ["rule_id"] = m.RuleId, ["host_id"] = m.HostId, ["rebar_id"] = m.RebarId, ["why"] = m.UnreadableWhy
                }).ToArray()),
                ["overlaps"] = overlaps,
                ["elapsed_ms"] = ElapsedMs
            };
            if (Pairs.Count > RebarOverlap.MaxPairsPublished) o["overlaps_not_listed"] = Pairs.Count - RebarOverlap.MaxPairsPublished;
            return o;
        }
    }

    public static class RebarOverlap
    {
        /// <summary>Two segments within this angle of each other run ALONGSIDE; past it they CROSS. Published in every reply.</summary>
        public const double AlongsideDegrees = 45.0;
        public const int MaxPairsPublished = 200;

        // Plan refusals: in ReinforcementResolver.AllCodes.
        public const string CodeIntersect = "bars_intersect";
        public const string CodeTooClose = "bars_closer_than_declared_clearance";
        public const string CodeNotEvaluable = "overlap_not_evaluable";
        /// <summary>Two rules of the set lay the same bar: both refused (in ReinforcementResolver.AllCodes).</summary>
        public const string CodeDuplicate = "duplicate_bar";
        /// <summary>Two model diameters this close are the same bar size. Published in every reply.</summary>
        public const double SameDiameterMm = 0.05;
        /// <summary>A centreline is sampled at least this often when two are compared point by point.</summary>
        public const double DuplicateSampleMm = 50.0;

        public const string KindIntersection = "intersection";
        public const string KindTooClose = "closer_than_declared_clearance";
        public const string KindDuplicate = "duplicate";
        public const string RelationAlongside = "alongside";
        public const string RelationCrossing = "crossing";
        public const string CheckedIntersectionsOnly = "intersections_only";
        public const string CheckedDeclared = "declared_clearance";

        public const string ExcuseLap = "declared_lap";
        public const string ExcuseCoupler = "declared_coupler";
        public const string ExcuseCage = "cage_ties_its_own_bars";
        public const string ExcuseHook = "hooks_not_measured";

        public const string RoleLongitudinal = SectionRules.CageLongitudinal;
        public const string RoleStirrup = SectionRules.CageStirrup;
        public const string RoleTie = SectionRules.CageTie;

        // ------------------------------------------------------------ identity

        /// <summary>
        /// What a resolved rule belongs to, as the plan publishes it (`belongs_to`): the rule the set
        /// declared, the layout it is part of, its cage and part in it, and the declared lap or coupler
        /// to the next piece. Absent keys are absent, never null.
        /// </summary>
        public static JObject Identity(StructuralRebarRule r)
        {
            var o = new JObject { ["declared_rule_id"] = r.OriginRuleId ?? r.Id };
            if (r.LayoutGroupId != null) o["layout"] = r.LayoutGroupId;
            if (r.CageId != null) o["cage"] = new JObject { ["id"] = r.CageId, ["role"] = r.CageRole };
            if (r.LapNextRuleId != null) o["lap_next_rule_id"] = r.LapNextRuleId;
            if (r.CouplerNextRuleId != null) o["coupler_next_rule_id"] = r.CouplerNextRuleId;
            return o;
        }

        private static OverlapMember Identify(JObject row)
        {
            var m = new OverlapMember
            {
                RuleId = (string)row?["rule_id"],
                HostId = row?["host_id"] != null && row["host_id"].Type == JTokenType.Integer ? (long)row["host_id"] : -1,
                Tag = row?["index"] != null && row["index"].Type == JTokenType.Integer ? (int)row["index"] : -1
            };
            var b = row?["belongs_to"] as JObject;
            m.DeclaredRuleId = (string)b?["declared_rule_id"] ?? m.RuleId;
            m.Layout = (string)b?["layout"];
            m.CageId = (string)b?["cage"]?["id"];
            m.CageRole = (string)b?["cage"]?["role"];
            m.LapNextRuleId = (string)b?["lap_next_rule_id"];
            m.CouplerNextRuleId = (string)b?["coupler_next_rule_id"] ?? (string)row?["coupler_next"]?["next_rule_id"];
            return m;
        }

        // ---------------------------------------------------------- plan rows

        /// <summary>
        /// A buildable row as ReinforcementResolver.DescribeRebarRow publishes it: the declared centreline
        /// (arcs sampled, hook-free) at every signed position the layout builds - a suppressed first or last
        /// bar is not there - with the MODEL diameter.
        /// </summary>
        public static OverlapMember FromPlanRow(JObject row)
        {
            OverlapMember m = Identify(row);
            double? dia = Num(row?["bar_type"]?["model_diameter_mm"]);
            if (!dia.HasValue || dia.Value <= 0)
                return Unreadable(m, "the bar type would not report a MODEL diameter, and a clear distance is measured between bar surfaces.");
            m.RadiusMm = dia.Value / 2;
            List<double[]> curve = RebarAuditRules.PointsOf(row["curve_mm"]);
            if (curve == null || curve.Count < 2) return Unreadable(m, "the row carries no declared centreline.");
            bool closedRow = row.Value<bool?>("closed") == true;
            // THE CORNERS AS REVIT DRAWS THEM (etapa 1c). MEASURED, Revit 2024 (anonymized regression case): corner bars declared in a
            // stirrup's sharp corner passed this check, and the audit - on the drawn bars - found every one of them
            // 10.29 mm into the stirrup's bend. A declared corner is drawn as a bend of the bar type's bend diameter
            // (stirrup/tie or standard, by the row's style), so it is measured as one. A row with arcs keeps its
            // sampled arcs; a row that does not carry its bend diameters is measured sharp.
            double? bendDia = row.Value<string>("style") == StructuralStyle.StirrupTie
                ? Num(row["bar_type"]?["stirrup_tie_bend_diameter_mm"]) : Num(row["bar_type"]?["standard_bend_diameter_mm"]);
            if (bendDia.HasValue && bendDia.Value > 0 && row["arcs"] == null)
                curve = DrawnCorners(curve, closedRow, bendDia.Value / 2 + dia.Value / 2);
            if (closedRow) curve.Add(curve[0]);
            double[] n = Unit(Vec(row["normal"]));
            var signed = row["layout"]?["signed_positions_mm"] as JArray;
            if (signed == null || signed.Count == 0) return Unreadable(m, "the row carries no bar positions.");
            if (n == null && signed.Count > 1) return Unreadable(m, "the row carries no usable normal to lay its positions along.");
            bool first = row["layout"]?.Value<bool?>("include_first_bar") ?? true;
            bool last = row["layout"]?.Value<bool?>("include_last_bar") ?? true;
            int count = signed.Count;
            for (int i = 0; i < count; i++)
            {
                if (count > 1 && ((i == 0 && !first) || (i == count - 1 && !last))) continue;
                double? at = Num(signed[i]);
                if (!at.HasValue) return Unreadable(m, "position " + i + " is not a number.");
                double s = at.Value;
                var pts = new List<double[]>(curve.Count);
                foreach (double[] p in curve)
                    pts.Add(n == null ? new[] { p[0], p[1], p[2] } : new[] { p[0] + n[0] * s, p[1] + n[1] * s, p[2] + n[2] * s });
                m.Bars.Add(new OverlapBar { Position = i, Points = pts });
            }
            return m;
        }

        /// <summary>
        /// A declared polyline with every corner drawn as Revit draws it: a circular bend of centreline radius
        /// <paramref name="radiusMm"/>, tangent to both segments, sampled every 15 degrees. A closed polyline bends at
        /// its first vertex too (the returned list does not repeat it). A corner whose tangent length would not fit
        /// on its segments (shared half by half with the next corner) is drawn with the radius that fits; a straight
        /// vertex, or a radius of zero, is left as it is.
        /// </summary>
        public static List<double[]> DrawnCorners(IList<double[]> pts, bool closed, double radiusMm)
        {
            var outp = new List<double[]>();
            int n = pts.Count;
            if (n < 3 || !(radiusMm > 0)) { foreach (double[] q in pts) outp.Add(new[] { q[0], q[1], q[2] }); return outp; }
            for (int i = 0; i < n; i++)
            {
                double[] b = pts[i];
                bool end = !closed && (i == 0 || i == n - 1);
                if (end) { outp.Add(new[] { b[0], b[1], b[2] }); continue; }
                double[] a = pts[(i - 1 + n) % n], c = pts[(i + 1) % n];
                double[] u1 = Sub3(a, b), u2 = Sub3(c, b);
                double l1 = Len3(u1), l2 = Len3(u2);
                if (l1 < 1e-9 || l2 < 1e-9) { outp.Add(new[] { b[0], b[1], b[2] }); continue; }
                u1 = Scale3(u1, 1 / l1); u2 = Scale3(u2, 1 / l2);
                double cos = Math.Max(-1, Math.Min(1, Dot3(u1, u2)));
                double theta = Math.Acos(cos);                    // the interior angle at b
                if (theta > Math.PI - 1e-4) { outp.Add(new[] { b[0], b[1], b[2] }); continue; }
                double half = theta / 2, r = radiusMm;
                double t = r / Math.Tan(half);
                double room = Math.Min(l1, l2) / 2;
                if (t > room) { t = room; r = t * Math.Tan(half); }
                double[] bis = Unit3(Add3(u1, u2));
                double[] centre = Add3(b, Scale3(bis, r / Math.Sin(half)));
                double[] t1 = Add3(b, Scale3(u1, t)), t2 = Add3(b, Scale3(u2, t));
                double sweep = Math.PI - theta;
                int steps = Math.Max(2, (int)Math.Ceiling(sweep / (Math.PI / 12)));
                double[] e1 = Scale3(Sub3(t1, centre), 1 / r), e2v = Sub3(t2, centre);
                double[] nrm = Unit3(Cross3(e1, e2v));
                double[] e2 = Cross3(nrm, e1);
                for (int k = 0; k <= steps; k++)
                {
                    double ang = sweep * k / steps;
                    outp.Add(Add3(centre, Add3(Scale3(e1, r * Math.Cos(ang)), Scale3(e2, r * Math.Sin(ang)))));
                }
            }
            return outp;
        }

        private static double[] Sub3(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Add3(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static double[] Scale3(double[] a, double k) => new[] { a[0] * k, a[1] * k, a[2] * k };
        private static double Dot3(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Len3(double[] a) => Math.Sqrt(Dot3(a, a));
        private static double[] Unit3(double[] a) { double l = Len3(a); return l < 1e-12 ? new[] { 0.0, 0.0, 0.0 } : Scale3(a, 1 / l); }
        private static double[] Cross3(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        // ------------------------------------------------------- audit bars

        /// <summary>
        /// A bar set as RebarFacts.Describe(doc, bar, includePositions: true) publishes it, identified by the
        /// row of the rule it was matched to. The DRAWN centreline (bends as Revit drew them) with its hooks cut
        /// off where the hook-free centreline ends, offset to every position the model says exists.
        /// </summary>
        public static OverlapMember FromObserved(JObject expectedRow, JObject observed)
        {
            OverlapMember m = Identify(expectedRow);
            m.Tag = -1;
            m.RebarId = observed?["id"] != null && observed["id"].Type == JTokenType.Integer ? (long)observed["id"] : -1;
            double? dia = Num(observed?["bar_type"]?["model_diameter_mm"]) ?? Num(expectedRow?["bar_type"]?["model_diameter_mm"]);
            if (!dia.HasValue || dia.Value <= 0)
                return Unreadable(m, "the bar type would not report a MODEL diameter, and a clear distance is measured between bar surfaces.");
            m.RadiusMm = dia.Value / 2;

            List<double[]> drawn = RebarAuditRules.PointsOf(observed?["geometry"]?["centreline_points_mm"]);
            List<double[]> declared = RebarAuditRules.PointsOf(observed?["geometry"]?["centreline_points_as_declared_mm"]);
            if (drawn == null || drawn.Count < 2) return Unreadable(m, "the bar would not return its drawn centreline.");
            bool? hook0 = HookAt(observed, 0), hook1 = HookAt(observed, 1);
            List<double[]> body;
            if (declared != null && declared.Count >= 2)
            {
                body = Trim(drawn, declared, expectedRow?.Value<bool?>("closed") == true);
                m.HooksNotMeasured = (hook0 == true ? 1 : 0) + (hook1 == true ? 1 : 0);
            }
            else if (hook0 != false || hook1 != false)
                return Unreadable(m, "the bar is hooked (or would not say) and its hook-free centreline would not read, so where " +
                                     "the hook ends and the bar begins is not known.");
            else body = drawn;

            var positions = observed["bar_positions"] as JArray;
            if (positions == null || positions.Count == 0)
                return Unreadable(m, "the set would not return its bar positions, so where its bars stand is not known.");
            foreach (JToken t in positions)
            {
                var po = t as JObject;
                if (po == null) return Unreadable(m, "a bar position is not an object.");
                if (po.Value<bool?>("exists") == false) continue;
                double[] off = Vec(po["offset_from_first_bar_mm"]);
                int index = po.Value<int?>("index") ?? -1;
                if (off == null) return Unreadable(m, "position " + index + " would not return its transform.");
                m.Bars.Add(new OverlapBar
                {
                    Position = index,
                    Points = body.Select(p => new[] { p[0] + off[0], p[1] + off[1], p[2] + off[2] }).ToList()
                });
            }
            if (m.Bars.Count == 0) return Unreadable(m, "the model says no position of this set holds a bar.");
            return m;
        }

        /// <summary>
        /// The drawn centreline between the points nearest the hook-free centreline's two ends: the start
        /// searched in the first half of the drawn length, the end in the second - a closed stirrup's two
        /// ends are one corner.
        /// </summary>
        public static List<double[]> Trim(List<double[]> drawn, List<double[]> declared, bool closed = false)
        {
            var cum = new double[drawn.Count];
            for (int i = 1; i < drawn.Count; i++) cum[i] = cum[i - 1] + Dist(drawn[i - 1], drawn[i]);
            double half = cum[drawn.Count - 1] / 2;
            double[] a0 = declared[0], a1 = declared[declared.Count - 1];
            int i0 = 0, i1 = drawn.Count - 1;
            double best = double.MaxValue;
            for (int i = 0; i < drawn.Count && cum[i] <= half; i++)
            {
                double d = Dist(drawn[i], a0);
                if (d < best) { best = d; i0 = i; }
            }
            best = double.MaxValue;
            for (int i = drawn.Count - 1; i >= 0 && cum[i] >= half; i--)
            {
                double d = Dist(drawn[i], a1);
                if (d < best) { best = d; i1 = i; }
            }
            if (i1 <= i0) return drawn;
            // AN OPEN BAR IS CUT WHERE ITS DRAWN CENTRELINE JOINS THE HOOK-FREE ONE (etapa 1c). A hooked end's
            // hook-free end is its out-to-out corner, off the drawn bar, and the drawn vertex nearest it lies half way
            // round the hook's bend (measured, anonymized regression case: half a 135 degree hook measured against a beam leg, -2.335 mm).
            // From that vertex the cut walks inward to the first drawn point on the hook-free line. A closed bar keeps
            // the nearest vertex: its first corner is a bend of the bar itself.
            if (!closed)
            {
                while (i0 < i1 && OffLine(drawn[i0], declared)) i0++;
                while (i1 > i0 && OffLine(drawn[i1], declared)) i1--;
            }
            return drawn.GetRange(i0, i1 - i0 + 1);
        }

        /// <summary>Farther than 0.5 mm from every segment of the polyline.</summary>
        private static bool OffLine(double[] p, List<double[]> line)
        {
            double best = double.MaxValue;
            for (int k = 0; k + 1 < line.Count; k++)
            {
                double[] a = line[k], b = line[k + 1];
                double[] ab = Sub3(b, a), ap = Sub3(p, a);
                double l2 = Dot3(ab, ab);
                double t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, Dot3(ap, ab) / l2));
                best = Math.Min(best, Len3(Sub3(p, Add3(a, Scale3(ab, t)))));
            }
            return best > 0.5;
        }

        private static bool? HookAt(JObject observed, int end)
        {
            JObject t = (observed?["terminations"] as JArray)?.OfType<JObject>().FirstOrDefault(o => (o.Value<int?>("end") ?? -1) == end);
            if (t == null || t.Value<bool?>("hook_readable") == false) return null;
            bool? has = t.Value<bool?>("has_hook");
            if (has.HasValue) return has;
            long? id = t.Value<long?>("hook_type_id");
            return id.HasValue ? (bool?)(id.Value != -1) : null;
        }

        private static OverlapMember Unreadable(OverlapMember m, string why)
        {
            m.UnreadableWhy = why;
            m.Bars.Clear();
            return m;
        }

        // -------------------------------------------------------------- excuses

        /// <summary>Why two DIFFERENT sets may come into contact by declaration, or null when nothing excuses it.</summary>
        public static string Excuse(OverlapMember a, OverlapMember b)
        {
            // A COUPLER THE MODEL CARRIES joins these two bars end to end: in contact at it by construction.
            if (a.RebarId > 0 && b.RebarId > 0 && (a.CoupledTo.Contains(b.RebarId) || b.CoupledTo.Contains(a.RebarId)))
                return ExcuseCoupler;
            if (a.HostId == b.HostId)
            {
                if ((a.LapNextRuleId != null && a.LapNextRuleId == b.RuleId) || (b.LapNextRuleId != null && b.LapNextRuleId == a.RuleId))
                    return ExcuseLap;
                if ((a.CouplerNextRuleId != null && a.CouplerNextRuleId == b.RuleId) || (b.CouplerNextRuleId != null && b.CouplerNextRuleId == a.RuleId))
                    return ExcuseCoupler;
            }
            if (a.CageId != null && a.CageId == b.CageId && a.HostId == b.HostId)
            {
                bool aBar = a.CageRole == RoleLongitudinal, bBar = b.CageRole == RoleLongitudinal;
                bool aTie = a.CageRole == RoleStirrup || a.CageRole == RoleTie, bTie = b.CageRole == RoleStirrup || b.CageRole == RoleTie;
                if ((aBar && bTie) || (bBar && aTie)) return ExcuseCage;
                if ((a.CageRole == RoleStirrup && b.CageRole == RoleTie) || (a.CageRole == RoleTie && b.CageRole == RoleStirrup)) return ExcuseCage;
            }
            return null;
        }

        // ---------------------------------------------------------------- check

        public static OverlapReport Check(IList<OverlapMember> members, StructuralClearances clearances, double tolMm)
            => Check(members, clearances, tolMm, 0);

        /// <summary>
        /// Every pair of bars of different sets, held to the declared clearances. Broad phase: every centreline
        /// segment is cut into pieces no longer than a cell and each piece is filed under the cells its box -
        /// grown by its radius and half the largest declared clearance - touches; a pair of pieces is measured
        /// once, in the lowest cell their two boxes share. Narrow phase: the exact distance between the two
        /// pieces. The minimum over a segment pair's pieces is the segment pair's own minimum.
        /// </summary>
        public static OverlapReport Check(IList<OverlapMember> members, StructuralClearances clearances, double tolMm, double cellMm)
        {
            var sw = Stopwatch.StartNew();
            clearances = clearances ?? new StructuralClearances();
            double tol = tolMm > 0 && !double.IsNaN(tolMm) ? tolMm : 0;
            var report = new OverlapReport
            {
                Checked = clearances.Declared ? CheckedDeclared : CheckedIntersectionsOnly,
                Clearances = clearances,
                ToleranceMm = tol
            };
            foreach (string k in new[] { ExcuseLap, ExcuseCoupler, ExcuseCage, ExcuseHook }) report.Excused[k] = 0;

            var live = new List<OverlapMember>();
            foreach (OverlapMember m in members ?? new List<OverlapMember>())
            {
                if (m == null) continue;
                if (m.UnreadableWhy != null) { report.Unreadable.Add(m); continue; }
                if (m.Bars.Count == 0) continue;
                live.Add(m);
                report.Excused[ExcuseHook] += m.HooksNotMeasured;
                if (m.DuplicateOnly) report.DuplicateOnlySets++;
            }
            report.SetsCompared = live.Count;
            report.BarsCompared = live.Sum(m => m.Bars.Count);

            // Layout groups: bars of one layout are never compared with each other.
            var groupOf = new int[live.Count];
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < live.Count; i++)
            {
                int g;
                if (!groups.TryGetValue(live[i].LayoutKey, out g)) { g = groups.Count; groups[live[i].LayoutKey] = g; }
                groupOf[i] = g;
            }

            double maxR = live.Count == 0 ? 0 : live.Max(m => m.RadiusMm);
            double reach = clearances.LargestMm() / 2;
            double cell = cellMm > 0 ? cellMm : Math.Max(500.0, 4 * (maxR + reach));

            // ---- pieces
            var pa = new List<double[]>();
            var pb = new List<double[]>();
            var pMember = new List<int>();
            var pPos = new List<int>();
            var pBar = new List<int>();
            var pSeg = new List<int>();
            var pBox = new List<double[]>();
            int segId = 0;
            for (int mi = 0; mi < live.Count; mi++)
            {
                OverlapMember m = live[mi];
                // Grown by the tolerance too: a duplicate's first point is looked up within it.
                double grow = Math.Max(m.RadiusMm + reach, tol) + 1e-6;
                for (int bi = 0; bi < m.Bars.Count; bi++)
                {
                    OverlapBar bar = m.Bars[bi];
                    for (int k = 0; k + 1 < bar.Points.Count; k++, segId++)
                    {
                        double[] s0 = bar.Points[k], s1 = bar.Points[k + 1];
                        report.Segments++;
                        double len = Dist(s0, s1);
                        int cuts = Math.Max(1, (int)Math.Ceiling(len / cell));
                        for (int c = 0; c < cuts; c++)
                        {
                            double t0 = (double)c / cuts, t1 = (double)(c + 1) / cuts;
                            double[] a = Lerp(s0, s1, t0), b = Lerp(s0, s1, t1);
                            pa.Add(a); pb.Add(b); pMember.Add(mi); pPos.Add(bar.Position); pBar.Add(bi); pSeg.Add(segId);
                            pBox.Add(new[]
                            {
                                Math.Min(a[0], b[0]) - grow, Math.Min(a[1], b[1]) - grow, Math.Min(a[2], b[2]) - grow,
                                Math.Max(a[0], b[0]) + grow, Math.Max(a[1], b[1]) + grow, Math.Max(a[2], b[2]) + grow
                            });
                        }
                    }
                }
            }
            report.Pieces = pa.Count;
            report.CellMm = cell;

            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < pa.Count; i++)
            {
                double[] bx = pBox[i];
                long x0 = Cell(bx[0], cell), y0 = Cell(bx[1], cell), z0 = Cell(bx[2], cell);
                long x1 = Cell(bx[3], cell), y1 = Cell(bx[4], cell), z1 = Cell(bx[5], cell);
                for (long x = x0; x <= x1; x++)
                    for (long y = y0; y <= y1; y++)
                        for (long z = z0; z <= z1; z++)
                        {
                            long key = Key(x, y, z);
                            List<int> l;
                            if (!grid.TryGetValue(key, out l)) { l = new List<int>(4); grid[key] = l; }
                            l.Add(i);
                        }
            }

            // ---- duplicates, FIRST: a bar whose first point lies within the tolerance of a piece of a bar of
            // another layout, of the same diameter, is compared with that bar point by point. Coinciding bar
            // pairs are taken out of the intersection test below - one bar built twice is reported once.
            var dups = new Dictionary<long, DupAgg>();
            var tested = new HashSet<string>(StringComparer.Ordinal);
            for (int mi = 0; mi < live.Count; mi++)
            {
                OverlapMember m = live[mi];
                for (int bi = 0; bi < m.Bars.Count; bi++)
                {
                    double[] first = m.Bars[bi].Points[0];
                    List<int> l;
                    if (!grid.TryGetValue(Key(Cell(first[0], cell), Cell(first[1], cell), Cell(first[2], cell)), out l)) continue;
                    foreach (int j in l)
                    {
                        int mj = pMember[j];
                        if (mj == mi || groupOf[mi] == groupOf[mj]) continue;
                        OverlapMember o = live[mj];
                        if (Math.Abs(2 * m.RadiusMm - 2 * o.RadiusMm) > SameDiameterMm) continue;
                        double[] c1, c2;
                        if (SegmentDistance(first, first, pa[j], pb[j], out c1, out c2) > tol) continue;
                        int bj = pBar[j];
                        int lo = Math.Min(mi, mj), hi = Math.Max(mi, mj);
                        int barLo = mi == lo ? bi : bj, barHi = mi == lo ? bj : bi;
                        if (!tested.Add(lo + ":" + barLo + ":" + hi + ":" + barHi)) continue;
                        double dev;
                        if (!Coincide(live[lo].Bars[barLo].Points, live[hi].Bars[barHi].Points, tol, out dev)) continue;
                        long pairKey = ((long)lo << 32) | (uint)hi;
                        DupAgg d;
                        if (!dups.TryGetValue(pairKey, out d)) { d = new DupAgg { Lo = lo, Hi = hi }; dups[pairKey] = d; }
                        int posLo = live[lo].Bars[barLo].Position, posHi = live[hi].Bars[barHi].Position;
                        if (d.BarPairs.Add(((long)posLo << 32) | (uint)posHi))
                        {
                            if (d.Pairs.Count < MaxPositionPairsListed) d.Pairs.Add(new[] { posLo, posHi });
                            if (d.Point == null) d.Point = live[lo].Bars[barLo].Points[0];
                            d.Deviation = Math.Max(d.Deviation, dev);
                        }
                    }
                }
            }

            double cosAlong = Math.Cos(AlongsideDegrees * Math.PI / 180.0);
            var excuseOf = new Dictionary<long, string>();
            var excusedSeen = new HashSet<long>();
            var worst = new Dictionary<long, Agg>();

            foreach (KeyValuePair<long, List<int>> entry in grid)
            {
                List<int> l = entry.Value;
                if (l.Count < 2) continue;
                for (int u = 0; u < l.Count; u++)
                {
                    int i = l[u];
                    int mi = pMember[i];
                    double[] bi = pBox[i];
                    for (int v = u + 1; v < l.Count; v++)
                    {
                        int j = l[v];
                        int mj = pMember[j];
                        if (groupOf[mi] == groupOf[mj]) continue;
                        if (live[mi].DuplicateOnly || live[mj].DuplicateOnly) continue;
                        double[] bj = pBox[j];
                        if (bi[0] > bj[3] || bj[0] > bi[3] || bi[1] > bj[4] || bj[1] > bi[4] || bi[2] > bj[5] || bj[2] > bi[5]) continue;
                        // Measured once: in the lowest cell the two boxes share.
                        if (Key(Cell(Math.Max(bi[0], bj[0]), cell), Cell(Math.Max(bi[1], bj[1]), cell), Cell(Math.Max(bi[2], bj[2]), cell)) != entry.Key)
                            continue;
                        int lo = Math.Min(mi, mj), hi = Math.Max(mi, mj);
                        long pairKey = ((long)lo << 32) | (uint)hi;
                        string excuse;
                        if (!excuseOf.TryGetValue(pairKey, out excuse))
                        {
                            excuse = Excuse(live[lo], live[hi]);
                            excuseOf[pairKey] = excuse;
                        }
                        report.CandidatePairs++;
                        if (excuse != null && excusedSeen.Contains(pairKey)) continue;
                        // ONE BAR BUILT TWICE is a duplicate, already found: not an intersection as well.
                        DupAgg dup;
                        if (dups.TryGetValue(pairKey, out dup))
                        {
                            int dLo = mi == lo ? pPos[i] : pPos[j], dHi = mi == lo ? pPos[j] : pPos[i];
                            if (dup.BarPairs.Contains(((long)dLo << 32) | (uint)dHi)) continue;
                        }

                        report.ExactTests++;
                        double[] c1, c2;
                        double d = SegmentDistance(pa[i], pb[i], pa[j], pb[j], out c1, out c2);
                        OverlapMember a = live[mi], b = live[mj];
                        double clear = d - a.RadiusMm - b.RadiusMm;
                        bool alongside = Alongside(pa[i], pb[i], pa[j], pb[j], cosAlong);
                        double required = alongside ? clearances.AlongsideMm(a.DeclaredRuleId, b.DeclaredRuleId) : clearances.CrossingMm;
                        if (clear >= required - tol) continue;

                        if (excuse != null)
                        {
                            excusedSeen.Add(pairKey);
                            report.Excused[excuse]++;
                            continue;
                        }
                        // Oriented low member first, so a pair is the same pair whichever piece was seen first.
                        bool swap = mi != lo;
                        int posLo = swap ? pPos[j] : pPos[i], posHi = swap ? pPos[i] : pPos[j];
                        Agg g;
                        if (!worst.TryGetValue(pairKey, out g)) { g = new Agg { Lo = lo, Hi = hi }; worst[pairKey] = g; }
                        g.BarPairs.Add(((long)posLo << 32) | (uint)posHi);
                        bool intersects = clear < -tol;
                        double[] point = { (c1[0] + c2[0]) / 2, (c1[1] + c2[1]) / 2, (c1[2] + c2[2]) / 2 };
                        double[] dirI = Unit(Sub(pb[i], pa[i])), dirJ = Unit(Sub(pb[j], pa[j]));
                        double[] dirLo = swap ? dirJ : dirI, dirHi = swap ? dirI : dirJ;
                        if (intersects)
                        {
                            if (!g.HasIntersection || clear < g.Clear - 1e-9)
                            {
                                g.HasIntersection = true;
                                g.Set(clear, required, alongside, point, posLo, posHi, dirLo, dirHi);
                            }
                        }
                        else if (!g.HasIntersection && (g.Point == null || clear - required < g.Clear - g.Required - 1e-9))
                            g.Set(clear, required, alongside, point, posLo, posHi, dirLo, dirHi);
                    }
                }
            }

            foreach (Agg g in worst.Values.OrderBy(x => x.Lo).ThenBy(x => x.Hi))
                report.Pairs.Add(new OverlapPair
                {
                    A = live[g.Lo], B = live[g.Hi],
                    PositionA = g.PosLo, PositionB = g.PosHi,
                    Kind = g.HasIntersection ? KindIntersection : KindTooClose,
                    Relation = g.Alongside ? RelationAlongside : RelationCrossing,
                    ClearMm = g.Clear, RequiredClearMm = g.Required,
                    PointMm = g.Point,
                    DirA = g.DirLo, DirB = g.DirHi,
                    BarPairs = g.BarPairs.Count
                });
            foreach (DupAgg d in dups.Values)
            {
                OverlapMember a = live[d.Lo], b = live[d.Hi];
                report.Pairs.Add(new OverlapPair
                {
                    A = a, B = b,
                    PositionA = d.Pairs[0][0], PositionB = d.Pairs[0][1],
                    Kind = KindDuplicate,
                    Relation = RelationAlongside,
                    // The surfaces of one bar built twice interpenetrate by its whole diameter, less how far apart the
                    // two centrelines stand at worst.
                    ClearMm = d.Deviation - a.RadiusMm - b.RadiusMm,
                    RequiredClearMm = 0,
                    PointMm = new[] { d.Point[0], d.Point[1], d.Point[2] },
                    BarPairs = d.BarPairs.Count,
                    DeviationMm = d.Deviation,
                    PositionPairs = d.Pairs
                });
            }
            // One order whatever was found first: by the two members, a duplicate before an intersection of the same two.
            var index = new Dictionary<OverlapMember, int>();
            for (int k = 0; k < live.Count; k++) index[live[k]] = k;
            report.Pairs = report.Pairs.OrderBy(p => index[p.A]).ThenBy(p => index[p.B]).ThenBy(p => p.IsDuplicate ? 0 : 1).ToList();
            sw.Stop();
            report.ElapsedMs = sw.ElapsedMilliseconds;
            return report;
        }

        /// <summary>How many coinciding position pairs a duplicate lists by name; the count is always whole.</summary>
        public const int MaxPositionPairsListed = 20;

        private sealed class DupAgg
        {
            public int Lo, Hi;
            public double Deviation;
            public double[] Point;
            public HashSet<long> BarPairs = new HashSet<long>();
            public List<int[]> Pairs = new List<int[]>();
        }

        /// <summary>
        /// Whether two centrelines are ONE bar: every point of each - its vertices, and points along every segment
        /// no farther apart than DuplicateSampleMm - within `tolMm` of the other polyline (both ways: a bar that is
        /// part of a longer one is not the same bar). `deviation` is the farthest such point.
        /// </summary>
        public static bool Coincide(List<double[]> a, List<double[]> b, double tolMm, out double deviation)
        {
            deviation = 0;
            if (a == null || b == null || a.Count < 2 || b.Count < 2) return false;
            double d1, d2;
            if (!Within(a, b, tolMm, out d1) || !Within(b, a, tolMm, out d2)) return false;
            deviation = Math.Max(d1, d2);
            return true;
        }

        private static bool Within(List<double[]> from, List<double[]> to, double tolMm, out double worst)
        {
            worst = 0;
            for (int k = 0; k + 1 < from.Count; k++)
            {
                double[] s0 = from[k], s1 = from[k + 1];
                int steps = Math.Max(1, (int)Math.Ceiling(Dist(s0, s1) / DuplicateSampleMm));
                for (int t = (k == 0 ? 0 : 1); t <= steps; t++)
                {
                    double[] q = Lerp(s0, s1, (double)t / steps);
                    double best = double.MaxValue;
                    for (int m = 0; m + 1 < to.Count && best > 1e-9; m++)
                    {
                        double[] c1, c2;
                        best = Math.Min(best, SegmentDistance(q, q, to[m], to[m + 1], out c1, out c2));
                    }
                    if (best > tolMm) return false;
                    worst = Math.Max(worst, best);
                }
            }
            return true;
        }

        private sealed class Agg
        {
            public int Lo, Hi, PosLo, PosHi;
            public bool HasIntersection, Alongside;
            public double Clear, Required;
            public double[] Point, DirLo, DirHi;
            public HashSet<long> BarPairs = new HashSet<long>();
            public void Set(double clear, double required, bool alongside, double[] point, int posLo, int posHi, double[] dirLo, double[] dirHi)
            {
                Clear = clear; Required = required; Alongside = alongside; Point = point; PosLo = posLo; PosHi = posHi;
                DirLo = dirLo; DirHi = dirHi;
            }
        }

        // ------------------------------------------------------- what follows

        public static string RefusalCode(OverlapPair p) =>
            p.Kind == KindDuplicate ? CodeDuplicate : p.Kind == KindIntersection ? CodeIntersect : CodeTooClose;

        /// <summary>
        /// The plan's answer: BOTH sets of every pair are refused - either one alone would build, together they
        /// cannot be built as declared - each naming the other, the clear distance, the point and what was required.
        /// A set in several pairs takes an intersection's code over a clearance's, and lists every pair.
        /// </summary>
        public static List<OverlapRefusal> Refusals(OverlapReport report)
        {
            var byTag = new Dictionary<int, OverlapRefusal>();
            var worstOf = new Dictionary<int, OverlapPair>();
            foreach (OverlapPair p in report.Pairs)
                for (int side = 0; side < 2; side++)
                {
                    OverlapMember me = side == 0 ? p.A : p.B;
                    OverlapRefusal r;
                    if (!byTag.TryGetValue(me.Tag, out r)) { r = new OverlapRefusal { Tag = me.Tag }; byTag[me.Tag] = r; }
                    r.Overlaps.Add(p.ToJson(side == 1));
                    OverlapPair w;
                    if (!worstOf.TryGetValue(me.Tag, out w) || Worse(p, w)) worstOf[me.Tag] = p;
                }
            foreach (OverlapRefusal r in byTag.Values)
            {
                OverlapPair p = worstOf[r.Tag];
                OverlapMember me = p.A.Tag == r.Tag ? p.A : p.B, other = p.A.Tag == r.Tag ? p.B : p.A;
                r.Code = RefusalCode(p);
                r.Why = Describe(p, me, other, report.ToleranceMm) +
                        (r.Overlaps.Count > 1 ? " It clashes with " + r.Overlaps.Count + " sets in all; every one is listed in overlaps." : "") +
                        (p.IsDuplicate
                            ? " BOTH sets are refused: built together they lay this steel twice. Remove one of the two rules, " +
                              "or move it - nothing declarable makes one bar built twice right."
                            : " BOTH sets are refused: either one alone would build, together they cannot be built as declared. " +
                              "Move one of them, declare which passes over (crossing_order), or declare the contact (a lap, a coupler, or a clearances pair with min_clear_mm).");
            }
            return byTag.Values.OrderBy(r => r.Tag).ToList();
        }

        private static bool Worse(OverlapPair p, OverlapPair than)
        {
            if (p.IsDuplicate != than.IsDuplicate) return p.IsDuplicate;
            if ((p.Kind == KindIntersection) != (than.Kind == KindIntersection)) return p.Kind == KindIntersection;
            return p.ClearMm - p.RequiredClearMm < than.ClearMm - than.RequiredClearMm;
        }

        private static string Whose(OverlapMember m)
        {
            string set = m.RebarId > 0 ? (m.Label == RebarSteelReview.LabelArea || m.Label == RebarSteelReview.LabelPath ? "bar " : "bar set ") + m.RebarId : null;
            if (m.RuleId == null)
                return (m.Label == RebarSteelReview.LabelArea ? "an area reinforcement " : m.Label == RebarSteelReview.LabelPath ? "a path reinforcement " : "") +
                       (set ?? "a set") + (m.SystemId > 0 ? " of system " + m.SystemId : "") + " in element " + m.HostId +
                       (m.DuplicateOnly ? ", which no rule of this set built" : "");
            return "rule '" + m.RuleId + "' in element " + m.HostId + (set != null ? " (" + set + ")" : "");
        }

        private static string Describe(OverlapPair p, OverlapMember me, OverlapMember other, double tolMm)
        {
            string at = "(" + Mm(p.PointMm[0]) + ", " + Mm(p.PointMm[1]) + ", " + Mm(p.PointMm[2]) + ")";
            string whose = Whose(other);
            if (p.IsDuplicate)
                return "it lays the SAME BAR as " + whose + ": " + p.BarPairs + " bar pair(s) on one centreline - every point of " +
                       "each within " + Mm(tolMm) + " mm of the other (the farthest " + Mm(p.DeviationMm) + " mm), the same model " +
                       "diameter - the first at " + at + " (positions " + string.Join(", ", p.PositionPairs.Take(5).Select(pp =>
                           (ReferenceEquals(me, p.A) ? pp[0] : pp[1]) + "/" + (ReferenceEquals(me, p.A) ? pp[1] : pp[0]))) +
                       "). One bar built twice is its steel counted twice, and it is reported as a duplicate, not as an intersection.";
            return p.Kind == KindIntersection
                ? "its bars pass THROUGH the bars of " + whose + ": " + p.BarPairs + " bar pair(s), the worst " + Mm(p.ClearMm) +
                  " mm clear (surfaces interpenetrating) at " + at + ", " + p.Relation + ". Revit moves a bar that crosses another " +
                  "until the two touch, and says nothing (measured, Revit 2024 and 2026: a wall's horizontals laid through a " +
                  "boundary element's stirrups came out pushed, the array 12.5 mm short) - so the model would not be what was declared."
                : "its bars come within " + Mm(p.ClearMm) + " mm clear of the bars of " + whose + " where they run " + p.Relation +
                  ", and the set declares " + Mm(p.RequiredClearMm) + " mm, judged with a tolerance of " + Mm(tolMm) + " mm: " + p.BarPairs +
                  " bar pair(s), the worst at " + at + ".";
        }

        /// <summary>
        /// The audit's findings: one `overlapping_bar` error per pair of sets (both rule ids, both bar ids, the
        /// clear distance, the point), and one UNKNOWN per set that could not be measured - never a pass.
        /// </summary>
        public static JArray AuditFindings(OverlapReport report)
        {
            var findings = new JArray();
            foreach (OverlapPair p in report.Pairs)
            {
                JObject f = p.IsDuplicate
                    ? RebarAuditRules.Finding(RebarFinding.DuplicateBar, RebarSeverity.Error, p.A.RuleId, p.A.RebarId,
                        "no bar of another rule or set on this bar's centreline", "the same bar, " + p.BarPairs + " bar pair(s)",
                        report.ToleranceMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm",
                        Describe(p, p.A, p.B, report.ToleranceMm), false, null)
                    : RebarAuditRules.Finding(RebarFinding.OverlappingBar, RebarSeverity.Error, p.A.RuleId, p.A.RebarId,
                    p.RequiredClearMm > 0
                        ? "at least " + Mm(p.RequiredClearMm) + " mm clear between the surfaces of bars of different rules (" + p.Relation + ")"
                        : "no bar of another rule passing through this one",
                    Math.Round(p.ClearMm, 3),
                    report.ToleranceMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm",
                    Describe(p, p.A, p.B, report.ToleranceMm), false, null);
                foreach (JProperty prop in p.ToJson().Properties())
                    if (f[prop.Name] == null) f[prop.Name] = prop.Value;
                f["host_id"] = p.A.HostId;
                findings.Add(f);
            }
            foreach (OverlapMember m in report.Unreadable)
            {
                JObject u = m.DuplicateOnly
                    ? RebarAuditRules.Unknown(RebarFinding.DuplicateBar, m.RuleId, m.RebarId,
                        "not the same bar as a bar of this set",
                        "this set, which no rule of the requirement set built, could not be compared with the others: " + m.UnreadableWhy)
                    : RebarAuditRules.Unknown(RebarFinding.OverlappingBar, m.RuleId, m.RebarId,
                    "no bar of another rule closer than declared",
                    "this set could not be measured against the others: " + m.UnreadableWhy);
                u["host_id"] = m.HostId;
                findings.Add(u);
            }
            return findings;
        }

        // ------------------------------------------------------------ geometry

        /// <summary>
        /// The exact closest distance between segments p1-q1 and p2-q2, and the closest point on each
        /// (Ericson, Real-Time Collision Detection, 5.1.9). A segment of zero length is a point.
        /// </summary>
        public static double SegmentDistance(double[] p1, double[] q1, double[] p2, double[] q2, out double[] c1, out double[] c2)
        {
            const double eps = 1e-12;
            double[] d1 = Sub(q1, p1), d2 = Sub(q2, p2), r = Sub(p1, p2);
            double a = Dot(d1, d1), e = Dot(d2, d2), f = Dot(d2, r);
            double s, t;
            if (a <= eps && e <= eps) { s = 0; t = 0; }
            else if (a <= eps) { s = 0; t = Clamp(f / e); }
            else
            {
                double c = Dot(d1, r);
                if (e <= eps) { t = 0; s = Clamp(-c / a); }
                else
                {
                    double b = Dot(d1, d2), denom = a * e - b * b;
                    s = denom > eps * a * e ? Clamp((b * f - c * e) / denom) : 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = Clamp(-c / a); }
                    else if (t > 1) { t = 1; s = Clamp((b - c) / a); }
                }
            }
            c1 = new[] { p1[0] + d1[0] * s, p1[1] + d1[1] * s, p1[2] + d1[2] * s };
            c2 = new[] { p2[0] + d2[0] * t, p2[1] + d2[1] * t, p2[2] + d2[2] * t };
            return Dist(c1, c2);
        }

        private static bool Alongside(double[] a0, double[] a1, double[] b0, double[] b1, double cosAlong)
        {
            double[] u = Sub(a1, a0), v = Sub(b1, b0);
            double nu = Math.Sqrt(Dot(u, u)), nv = Math.Sqrt(Dot(v, v));
            if (nu < 1e-9 || nv < 1e-9) return false;
            return Math.Abs(Dot(u, v)) / (nu * nv) >= cosAlong;
        }

        private static long Cell(double v, double cell) => (long)Math.Floor(v / cell);

        private static long Key(long x, long y, long z)
        {
            const long off = 1L << 20, mask = (1L << 21) - 1;
            return (((x + off) & mask) << 42) | (((y + off) & mask) << 21) | ((z + off) & mask);
        }

        private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Dist(double[] a, double[] b)
        {
            double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
            return Math.Sqrt(x * x + y * y + z * z);
        }
        private static double[] Lerp(double[] a, double[] b, double t) =>
            t <= 0 ? new[] { a[0], a[1], a[2] } : t >= 1 ? new[] { b[0], b[1], b[2] }
                   : new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t };

        private static double? Num(JToken t)
        {
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) return null;
            double v = t.Value<double>();
            return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v;
        }

        /// <summary>{x, y, z} or [x, y, z], or null.</summary>
        private static double[] Vec(JToken t)
        {
            if (t is JObject o)
            {
                double? x = Num(o["x"]), y = Num(o["y"]), z = Num(o["z"]);
                return x.HasValue && y.HasValue && z.HasValue ? new[] { x.Value, y.Value, z.Value } : null;
            }
            if (t is JArray a && a.Count >= 3)
            {
                double? x = Num(a[0]), y = Num(a[1]), z = Num(a[2]);
                return x.HasValue && y.HasValue && z.HasValue ? new[] { x.Value, y.Value, z.Value } : null;
            }
            return null;
        }

        private static double[] Unit(double[] v)
        {
            if (v == null) return null;
            double n = Math.Sqrt(Dot(v, v));
            return n < 1e-12 ? null : new[] { v[0] / n, v[1] / n, v[2] / n };
        }

        private static string Mm(double v) => Math.Round(v, 1).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
