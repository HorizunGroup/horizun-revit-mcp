// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// THE CIRCULAR HOOP AND THE SPIRAL, BUILT FROM A PLACED REBAR SHAPE (acero 3.0, L7).
//
// MEASURED on Revit 2024: Rebar.CreateFromCurves builds a single arc between two
// points, and every chain with an arc in it - a closed circle of two or four arcs,
// an open 350 degree hoop, a line followed by an arc - fails inside Revit with "An
// internal error has occurred", whatever the style. A hoop or a spiral therefore
// cannot be drawn; it has to be PLACED, as an instance of a rebar shape that the
// model already carries (Rebar.CreateFromRebarShape), and then sized.
//
// WHICH SHAPE IS NEVER INFERRED. A template carries shapes called M_T1..M_T9 and
// M_SP, and nothing in a name says which of them is a lapped circle: the shape is
// named by the caller, and its DEFINITION is read before anything is built -
// RebarShapeDefinitionByArc, and its Type - so a name that does not mean what the
// rule asks is refused by name rather than placed.
//
// HOW IT IS SIZED is the documented behaviour of RebarShapeDrivenAccessor.ScaleToBox:
// the width and height of the shape "including bar thickness" are fitted to a box.
// So the box is the hoop's OUTSIDE: the centreline diameter plus one bar. A spiral
// is fitted with ScaleToBoxFor3D, its start in that box and its end in the parallel
// plane at the declared height, and its pitch and finishing turns are then set from
// the declaration. Every one of those numbers is re-read after the commit, and the
// centreline Revit drew is measured against the axis: on its radius, in its plane
// (a hoop) or between its base and its top (a spiral). Live measurement is pending.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// What a section rule's stirrups declare when the hoop is placed from a shape:
    /// the shape's name, the kind it must be, and for a spiral its pitch and finishing
    /// turns. Nothing here is defaulted.
    /// </summary>
    public sealed class StructuralShapePlacement
    {
        public const string KindLappedCircle = "lapped_circle", KindSpiral = "spiral";
        public static readonly string[] Kinds = { KindLappedCircle, KindSpiral };

        public string Shape;
        public string Kind;
        /// <summary>Spiral only: the rise per turn, centre to centre.</summary>
        public double? PitchMm;
        /// <summary>Spiral only: flat turns at the base and at the top, whole numbers.</summary>
        public int? BaseFinishingTurns, TopFinishingTurns;
    }

    /// <summary>
    /// Where one placed hoop or spiral goes, in model millimetres: the centre of the hoop
    /// (or of the spiral's BASE turn), the member's axis, the two directions of the section
    /// plane the shape's own x and y are mapped to, the CENTRELINE radius and - for a
    /// spiral - its height, pitch and finishing turns. Set only by an expansion.
    /// </summary>
    public sealed class PlacedShapeGeometry
    {
        public string Shape;
        public string Kind;
        public double[] CentreMm, AxisMm, XVec, YVec;
        public double CentrelineRadiusMm;
        public double? HeightMm, PitchMm;
        public int? BaseFinishingTurns, TopFinishingTurns;

        public bool IsSpiral => Kind == StructuralShapePlacement.KindSpiral;

        /// <summary>The same placement moved along the axis.</summary>
        public PlacedShapeGeometry MovedAlong(double[] unit, double mm)
        {
            var c = (PlacedShapeGeometry)MemberwiseClone();
            c.CentreMm = new[] { CentreMm[0] + unit[0] * mm, CentreMm[1] + unit[1] * mm, CentreMm[2] + unit[2] * mm };
            return c;
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["shape"] = Shape,
                ["kind"] = Kind,
                ["centre_mm"] = Arr(CentreMm),
                ["axis"] = Arr(AxisMm),
                ["x_direction"] = Arr(XVec),
                ["y_direction"] = Arr(YVec),
                ["centreline_radius_mm"] = Math.Round(CentrelineRadiusMm, 3)
            };
            if (HeightMm.HasValue) o["height_mm"] = Math.Round(HeightMm.Value, 3);
            if (PitchMm.HasValue) o["pitch_mm"] = Math.Round(PitchMm.Value, 3);
            if (BaseFinishingTurns.HasValue) o["base_finishing_turns"] = BaseFinishingTurns.Value;
            if (TopFinishingTurns.HasValue) o["top_finishing_turns"] = TopFinishingTurns.Value;
            o["centre_means"] = IsSpiral
                ? "the centre of the spiral's BASE turn; the spiral rises height_mm along the axis from it."
                : "the centre of the hoop, which lies in the plane square to the axis through it.";
            return o;
        }

        public static PlacedShapeGeometry FromJson(JToken t)
        {
            var o = t as JObject;
            if (o == null) return null;
            return new PlacedShapeGeometry
            {
                Shape = (string)o["shape"],
                Kind = (string)o["kind"],
                CentreMm = Vec(o["centre_mm"]),
                AxisMm = Vec(o["axis"]),
                XVec = Vec(o["x_direction"]),
                YVec = Vec(o["y_direction"]),
                CentrelineRadiusMm = o.Value<double?>("centreline_radius_mm") ?? 0,
                HeightMm = o.Value<double?>("height_mm"),
                PitchMm = o.Value<double?>("pitch_mm"),
                BaseFinishingTurns = o.Value<int?>("base_finishing_turns"),
                TopFinishingTurns = o.Value<int?>("top_finishing_turns")
            };
        }

        private static JToken Arr(double[] v) =>
            v == null ? (JToken)JValue.CreateNull() : new JArray(Math.Round(v[0], 6), Math.Round(v[1], 6), Math.Round(v[2], 6));

        private static double[] Vec(JToken t)
        {
            var a = t as JArray;
            if (a == null || a.Count != 3) return null;
            return new[] { (double)a[0], (double)a[1], (double)a[2] };
        }
    }

    /// <summary>What the centreline Revit drew says about a placed hoop or spiral.</summary>
    public sealed class PlacedShapeCheck
    {
        /// <summary>False when there was nothing to measure. Never a pass.</summary>
        public bool Evaluated;
        public bool Verified;
        public int Points;
        public double WorstRadiusOffMm;
        /// <summary>A hoop: the farthest a drawn point lies from the hoop's plane.</summary>
        public double WorstPlaneOffMm;
        /// <summary>A hoop: the widest angle round the axis with no drawn point in it.</summary>
        public double LargestGapDegrees;
        /// <summary>A spiral: where the drawn bar starts and stops along the axis, from the base centre.</summary>
        public double AxialFromMm, AxialToMm;
        public double ToleranceMm;
        public string Why;

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["verified"] = Verified,
                ["evaluated"] = Evaluated,
                ["drawn_points"] = Points,
                ["tolerance_mm"] = ToleranceMm,
                ["why"] = Why
            };
            if (!Evaluated) return o;
            o["worst_radius_difference_mm"] = Math.Round(WorstRadiusOffMm, 3);
            o["worst_distance_off_the_plane_mm"] = Math.Round(WorstPlaneOffMm, 3);
            o["largest_gap_round_the_axis_degrees"] = Math.Round(LargestGapDegrees, 3);
            o["axial_from_mm"] = Math.Round(AxialFromMm, 3);
            o["axial_to_mm"] = Math.Round(AxialToMm, 3);
            return o;
        }
    }

    public static class PlacedShapes
    {
        public const string CodeNotACircle = "shape_is_not_a_circle";
        public const string CodeNotASpiral = "shape_is_not_a_spiral";
        public const string CodeDefinitionUnreadable = "shape_definition_unreadable";
        public static readonly string[] AllCodes = { CodeNotACircle, CodeNotASpiral, CodeDefinitionUnreadable };

        /// <summary>The words the resolver reads a shape's definition into.</summary>
        public const string DefinitionArc = "arc", DefinitionLappedCircle = "lapped_circle", DefinitionSpiral = "spiral",
                            DefinitionSegments = "segments", DefinitionUnreadable = "unreadable";

        /// <summary>A drawn hoop may leave no wider angle than this round its axis without a point.</summary>
        public const double MaxGapDegrees = 90.0;

        /// <summary>Segments per turn when the helix is sampled for measurement.</summary>
        public const int HelixSegmentsPerTurn = 36;

        /// <summary>
        /// Null when the shape's definition is the kind the rule asks for; otherwise
        /// { code, why }. `read` is what the definition says (see the Definition* words),
        /// or null when it could not be read - which is never agreement.
        /// </summary>
        public static string[] DefinitionRefusal(string wanted, string read, string shapeName)
        {
            string name = "'" + (shapeName ?? "?") + "'";
            if (read == null || read == DefinitionUnreadable)
                return new[] { CodeDefinitionUnreadable,
                    "the definition of shape " + name + " could not be read, so whether it is a " + Word(wanted) +
                    " is UNKNOWN. A shape is judged by its definition, never by its name; unknown is not agreement." };
            if (wanted == StructuralShapePlacement.KindLappedCircle && read != DefinitionLappedCircle)
                return new[] { CodeNotACircle,
                    "shape " + name + " is defined as " + Described(read) + ", not as a lapped circle " +
                    "(RebarShapeDefinitionByArc of type LappedCircle). Name a shape whose definition is one." };
            if (wanted == StructuralShapePlacement.KindSpiral && read != DefinitionSpiral)
                return new[] { CodeNotASpiral,
                    "shape " + name + " is defined as " + Described(read) + ", not as a spiral " +
                    "(RebarShapeDefinitionByArc of type Spiral). Name a shape whose definition is one." };
            return null;
        }

        private static string Word(string kind) => kind == StructuralShapePlacement.KindSpiral ? "spiral" : "lapped circle";

        private static string Described(string read)
        {
            switch (read)
            {
                case DefinitionArc: return "an open arc (RebarShapeDefinitionByArc of type Arc)";
                case DefinitionLappedCircle: return "a lapped circle";
                case DefinitionSpiral: return "a spiral";
                case DefinitionSegments: return "straight segments (RebarShapeDefinitionBySegments)";
                default: return "'" + read + "'";
            }
        }

        /// <summary>
        /// The zones of a placed stirrup rule, laid out by <see cref="StirrupZoneRules.Plan"/>,
        /// become the bars they mean. A LAPPED HOOP is one placed bar per station that has a bar
        /// - ids parent#zone#s1, #s2... - each laid out single at its station, its measured
        /// path the circle there. A SPIRAL (one zone, laid out single - the parser holds that) is
        /// one placed bar, parent#zone, rising from the zone's start over the zone's length, its
        /// measured path the helix. The cover prediction is not attached: a single bar has no
        /// array for Revit to clamp, and its station is held by the placement check.
        /// </summary>
        public static void ExpandZones(StructuralStirrupZoneRule rule, StirrupZoneResult plan, double[] unit,
                                       List<StructuralRebarRule> expanded)
        {
            PlacedShapeGeometry t = rule.PlacementTemplate;
            foreach (StirrupZonePlan z in plan.Zones)
            {
                if (t.IsSpiral)
                {
                    PlacedShapeGeometry g = t.MovedAlong(unit, z.StartMm);
                    g.HeightMm = z.LengthMm;
                    expanded.Add(Bar(rule, rule.Id + "#" + z.Name, z, g, HelixPath(g, HelixSegmentsPerTurn), false, null, unit, z.StartMm));
                    continue;
                }
                for (int k = 0; k < z.AbsoluteBarPositionsMm.Count; k++)
                {
                    double station = z.AbsoluteBarPositionsMm[k];
                    PlacedShapeGeometry g = t.MovedAlong(unit, station);
                    var moved = rule.ProfileMm.Select(p => new[] { p[0] + unit[0] * station, p[1] + unit[1] * station, p[2] + unit[2] * station }).ToList();
                    expanded.Add(Bar(rule, rule.Id + "#" + z.Name + "#s" + (k + 1).ToString(CultureInfo.InvariantCulture), z, g, moved, true,
                                     rule.ProfileArcThrough, unit, station));
                }
            }
        }

        private static StructuralRebarRule Bar(StructuralStirrupZoneRule rule, string id, StirrupZonePlan z, PlacedShapeGeometry g,
                                               List<double[]> path, bool closed, Dictionary<int, double[]> arcs, double[] unit, double station)
        {
            var bar = new StructuralRebarRule
            {
                Id = id,
                Host = rule.Host,
                BarTypeId = rule.BarTypeId,
                ShapeName = g.Shape,
                Style = rule.Style,
                CurvesMm = path,
                Closed = closed,
                NormalMm = new[] { unit[0], unit[1], unit[2] },
                BarsOnNormalSide = true,
                Layout = new RebarLayoutRequest { Layout = RebarLayout.Single },
                Start = rule.Start,
                End = rule.End,
                Mark = string.IsNullOrWhiteSpace(z.Mark) ? rule.Mark : z.Mark,
                Required = rule.Required,
                Raw = rule.Raw,
                Placement = g,
                // One layout with every other hoop of this zone rule, in the cage it came from (overlapping_bar).
                OriginRuleId = rule.OriginRuleId ?? rule.Id,
                LayoutGroupId = rule.Id,
                CageId = rule.CageId,
                CageRole = rule.CageRole
            };
            if (arcs != null)
                foreach (KeyValuePair<int, double[]> a in arcs)
                    bar.ArcThrough[a.Key] = new[] { a.Value[0] + unit[0] * station, a.Value[1] + unit[1] * station, a.Value[2] + unit[2] * station };
            return bar;
        }

        /// <summary>
        /// The box ScaleToBox fits the shape to: width and height INCLUDING the bar
        /// (Revit's documented behaviour), so the centreline diameter plus one bar, with
        /// its lower-left corner where the hoop's centre less half of that is.
        /// </summary>
        public static void Box(PlacedShapeGeometry g, double barDiameterMm, out double[] originMm,
                               out double[] xVecMm, out double[] yVecMm)
        {
            double side = 2 * g.CentrelineRadiusMm + barDiameterMm, h = side / 2;
            originMm = new[]
            {
                g.CentreMm[0] - h * (g.XVec[0] + g.YVec[0]),
                g.CentreMm[1] - h * (g.XVec[1] + g.YVec[1]),
                g.CentreMm[2] - h * (g.XVec[2] + g.YVec[2])
            };
            xVecMm = new[] { g.XVec[0] * side, g.XVec[1] * side, g.XVec[2] * side };
            yVecMm = new[] { g.YVec[0] * side, g.YVec[1] * side, g.YVec[2] * side };
        }

        /// <summary>
        /// The value a shape's size parameter should carry, by how the shape measures it:
        /// a diameter or a radius, to the centreline, the outside or the inside of the bar.
        /// Null for a measure this does not know - which is then reported, not guessed.
        /// </summary>
        public static double? ExpectedSizeMm(string constraint, string reference, double centrelineRadiusMm, double barDiameterMm)
        {
            double r;
            switch (reference)
            {
                case "centerline": r = centrelineRadiusMm; break;
                case "external": r = centrelineRadiusMm + barDiameterMm / 2; break;
                case "internal": r = centrelineRadiusMm - barDiameterMm / 2; break;
                default: return null;
            }
            if (constraint == "diameter") return 2 * r;
            if (constraint == "radius") return r;
            return null;
        }

        /// <summary>A hoop's circle, sampled: n segments, closed (the first point repeated last).</summary>
        public static List<double[]> CirclePath(PlacedShapeGeometry g, int n)
        {
            var pts = new List<double[]>();
            for (int i = 0; i <= n; i++)
            {
                double a = 2 * Math.PI * i / n;
                pts.Add(At(g, a, 0));
            }
            return pts;
        }

        /// <summary>
        /// The spiral, sampled: its base finishing turns flat at the base, its rising
        /// turns over the height at the declared pitch, its top finishing turns flat at
        /// the top. Starts on the x direction.
        /// </summary>
        public static List<double[]> HelixPath(PlacedShapeGeometry g, int segmentsPerTurn)
        {
            double h = g.HeightMm ?? 0, p = g.PitchMm ?? 0;
            int b = g.BaseFinishingTurns ?? 0, t = g.TopFinishingTurns ?? 0;
            double rising = p > 0 ? h / p : 0;
            var pts = new List<double[]> { At(g, 0, 0) };
            int nb = b * segmentsPerTurn;
            for (int i = 1; i <= nb; i++) pts.Add(At(g, 2 * Math.PI * i / segmentsPerTurn, 0));
            double a0 = 2 * Math.PI * b;
            int nr = Math.Max(1, (int)Math.Ceiling(rising * segmentsPerTurn - 1e-9));
            for (int i = 1; i <= nr; i++)
            {
                double f = (double)i / nr;
                pts.Add(At(g, a0 + 2 * Math.PI * rising * f, h * f));
            }
            double a1 = a0 + 2 * Math.PI * rising;
            int nt = t * segmentsPerTurn;
            for (int i = 1; i <= nt; i++) pts.Add(At(g, a1 + 2 * Math.PI * i / segmentsPerTurn, h));
            return pts;
        }

        private static double[] At(PlacedShapeGeometry g, double angle, double axial)
        {
            double c = Math.Cos(angle) * g.CentrelineRadiusMm, s = Math.Sin(angle) * g.CentrelineRadiusMm;
            return new[]
            {
                g.CentreMm[0] + c * g.XVec[0] + s * g.YVec[0] + axial * g.AxisMm[0],
                g.CentreMm[1] + c * g.XVec[1] + s * g.YVec[1] + axial * g.AxisMm[1],
                g.CentreMm[2] + c * g.XVec[2] + s * g.YVec[2] + axial * g.AxisMm[2]
            };
        }

        /// <summary>
        /// The centreline Revit DREW, against the placement. Every drawn point must lie on
        /// the centreline radius from the axis; a hoop's must lie in its plane and go all
        /// the way round (no gap wider than <see cref="MaxGapDegrees"/>); a spiral's must
        /// start at its base and stop at its top. Nothing drawn is not evaluated, and that
        /// is never a pass. The angle at which the lap or the spiral starts is not compared.
        /// </summary>
        public static PlacedShapeCheck CheckCentreline(PlacedShapeGeometry g, IList<double[]> drawn, double toleranceMm)
        {
            var c = new PlacedShapeCheck { ToleranceMm = toleranceMm };
            if (g == null || g.CentreMm == null || g.AxisMm == null)
            {
                c.Why = "there is no placement to measure against.";
                return c;
            }
            if (drawn == null || drawn.Count < 2)
            {
                c.Why = "the bar would not return the centreline it draws, so where the placed shape lies is UNKNOWN. " +
                        "Unknown is not a pass.";
                return c;
            }
            double[] axis = RebarContainment.Unit(g.AxisMm), x = RebarContainment.Unit(g.XVec);
            if (axis == null || x == null) { c.Why = "the placement's axis is not a usable vector."; return c; }
            double[] y = Cross(axis, x);
            c.Evaluated = true;
            c.Points = drawn.Count;
            double lo = double.MaxValue, hi = double.MinValue;
            var angles = new List<double>();
            foreach (double[] p in drawn)
            {
                double[] q = { p[0] - g.CentreMm[0], p[1] - g.CentreMm[1], p[2] - g.CentreMm[2] };
                double a = Dot(q, axis);
                double[] radial = { q[0] - a * axis[0], q[1] - a * axis[1], q[2] - a * axis[2] };
                double r = Math.Sqrt(Dot(radial, radial));
                c.WorstRadiusOffMm = Math.Max(c.WorstRadiusOffMm, Math.Abs(r - g.CentrelineRadiusMm));
                c.WorstPlaneOffMm = Math.Max(c.WorstPlaneOffMm, Math.Abs(a));
                lo = Math.Min(lo, a); hi = Math.Max(hi, a);
                if (r > 1e-9) angles.Add(Math.Atan2(Dot(radial, y), Dot(radial, x)) * 180 / Math.PI);
            }
            c.AxialFromMm = lo; c.AxialToMm = hi;
            c.LargestGapDegrees = LargestGap(angles);
            bool onRadius = c.WorstRadiusOffMm <= toleranceMm;
            if (g.IsSpiral)
            {
                double h = g.HeightMm ?? 0;
                bool baseOk = Math.Abs(lo) <= toleranceMm, topOk = Math.Abs(hi - h) <= toleranceMm;
                c.WorstPlaneOffMm = 0;
                c.Verified = onRadius && baseOk && topOk;
                c.Why = c.Verified
                    ? "every drawn point is on the centreline radius, and the spiral starts at its base and stops at its top."
                    : !onRadius
                        ? "a drawn point lies " + Mm(c.WorstRadiusOffMm) + " off the centreline radius of " + Mm(g.CentrelineRadiusMm) + "."
                        : "the drawn spiral runs from " + Mm(lo) + " to " + Mm(hi) + " along the axis, and it was placed from 0 to " + Mm(h) + ".";
                return c;
            }
            bool inPlane = c.WorstPlaneOffMm <= toleranceMm, round = c.LargestGapDegrees <= MaxGapDegrees;
            c.Verified = onRadius && inPlane && round;
            c.Why = c.Verified
                ? "every drawn point is on the centreline radius, in the hoop's plane, and the hoop goes all the way round."
                : !onRadius
                    ? "a drawn point lies " + Mm(c.WorstRadiusOffMm) + " off the centreline radius of " + Mm(g.CentrelineRadiusMm) + "."
                    : !inPlane
                        ? "a drawn point lies " + Mm(c.WorstPlaneOffMm) + " off the hoop's plane: it is not at the station it was placed at."
                        : "the drawn bar leaves " + Math.Round(c.LargestGapDegrees, 1).ToString(CultureInfo.InvariantCulture) +
                          " degrees round the axis without steel: it is not a closed hoop.";
            return c;
        }

        private static double LargestGap(List<double> degrees)
        {
            if (degrees.Count == 0) return 360;
            var a = degrees.Select(d => d < 0 ? d + 360 : d).OrderBy(d => d).ToList();
            double gap = 360 - a[a.Count - 1] + a[0];
            for (int i = 1; i < a.Count; i++) gap = Math.Max(gap, a[i] - a[i - 1]);
            return gap;
        }

        /// <summary>
        /// A spiral's pitch, height and finishing turns read back from the bar, against the
        /// declaration. A value Revit would not report is not a value that agrees.
        /// </summary>
        public static JObject CompareSpiralParameters(PlacedShapeGeometry g, double? pitchReadMm, double? heightReadMm,
                                                      int? baseRead, int? topRead, double toleranceMm)
        {
            var o = new JObject();
            var unread = new List<string>();
            var differ = new List<string>();
            Length(o, "pitch_mm", g.PitchMm, pitchReadMm, toleranceMm, unread, differ);
            Length(o, "height_mm", g.HeightMm, heightReadMm, toleranceMm, unread, differ);
            Count(o, "base_finishing_turns", g.BaseFinishingTurns, baseRead, unread, differ);
            Count(o, "top_finishing_turns", g.TopFinishingTurns, topRead, unread, differ);
            o["verified"] = unread.Count == 0 && differ.Count == 0;
            o["tolerance_mm"] = toleranceMm;
            o["why"] = unread.Count > 0
                ? "Revit would not report " + string.Join(", ", unread) + ", so the spiral is not known to carry what was declared. Unknown is not a pass."
                : differ.Count > 0
                    ? "the spiral carries a different " + string.Join(", ", differ) + " than was declared."
                    : "the spiral's pitch, height and finishing turns are the declared ones.";
            o["source"] = "REBAR_SHAPE_SPIRAL_PITCH, REBAR_SHAPE_SPIRAL_HEIGHT, REBAR_SHAPE_SPIRAL_BASE_FINISHING_TURNS and " +
                          "REBAR_SHAPE_SPIRAL_TOP_FINISHING_TURNS, read off the bar after the commit";
            return o;
        }

        private static void Length(JObject o, string name, double? want, double? got, double tol, List<string> unread, List<string> differ)
        {
            o[name] = new JObject
            {
                ["requested"] = want.HasValue ? (JToken)Math.Round(want.Value, 3) : JValue.CreateNull(),
                ["read"] = got.HasValue ? (JToken)Math.Round(got.Value, 3) : JValue.CreateNull()
            };
            if (!got.HasValue) unread.Add(name);
            else if (!want.HasValue || Math.Abs(got.Value - want.Value) > tol) differ.Add(name);
        }

        private static void Count(JObject o, string name, int? want, int? got, List<string> unread, List<string> differ)
        {
            o[name] = new JObject
            {
                ["requested"] = want.HasValue ? (JToken)want.Value : JValue.CreateNull(),
                ["read"] = got.HasValue ? (JToken)got.Value : JValue.CreateNull()
            };
            if (!got.HasValue) unread.Add(name);
            else if (!want.HasValue || got.Value != want.Value) differ.Add(name);
        }

        /// <summary>
        /// The audit's findings for one placed hoop or spiral: its drawn centreline against
        /// the placement, and - for a spiral, when the caller read them - its parameters.
        /// </summary>
        public static JArray AuditFindings(string ruleId, long barId, JObject placementJson, IList<double[]> drawn,
                                           JObject spiralParameters, StructuralTolerances tol)
        {
            var findings = new JArray();
            PlacedShapeGeometry g = PlacedShapeGeometry.FromJson(placementJson);
            PlacedShapeCheck c = CheckCentreline(g, drawn, tol.LengthMm);
            if (!c.Evaluated)
            {
                JObject u = RebarAuditRules.Unknown(RebarFinding.PlacedShapeDiffers, ruleId, barId,
                    "the placed " + (g != null && g.IsSpiral ? "spiral" : "hoop"), c.Why);
                u["placement"] = c.ToJson();
                findings.Add(u);
            }
            else if (!c.Verified)
            {
                JObject f = RebarAuditRules.Finding(RebarFinding.PlacedShapeDiffers, RebarSeverity.Error, ruleId, barId,
                    "centreline radius " + Mm(g.CentrelineRadiusMm) + (g.IsSpiral ? " over a height of " + Mm(g.HeightMm ?? 0) : " at its station"),
                    c.ToJson(), tol.LengthMm + " mm", c.Why, true, "horizun_apply_reinforcement");
                findings.Add(f);
            }
            if (g != null && g.IsSpiral && spiralParameters != null && (bool?)spiralParameters["verified"] != true)
            {
                bool unread = ((string)spiralParameters["why"] ?? "").Contains("would not report");
                JObject f = unread
                    ? RebarAuditRules.Unknown(RebarFinding.PlacedShapeDiffers, ruleId, barId, "the declared pitch, height and turns",
                        (string)spiralParameters["why"])
                    : RebarAuditRules.Finding(RebarFinding.PlacedShapeDiffers, RebarSeverity.Error, ruleId, barId,
                        "the declared pitch, height and turns", spiralParameters, tol.LengthMm + " mm",
                        (string)spiralParameters["why"], true, "horizun_apply_reinforcement");
                f["spiral_parameters"] = spiralParameters;
                findings.Add(f);
            }
            return findings;
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static string Mm(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture) + " mm";
    }
}
