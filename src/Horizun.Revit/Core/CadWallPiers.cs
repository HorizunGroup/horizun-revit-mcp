// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// A SHORT, THICKER STRETCH THAT CLOSES A WALL'S END: A PIER. READ ONLY WHEN ASKED.
//
// MEASURED (case 159C4, unit 914): a wall read from x 34150 eastwards is closed at
// its west end by a box 254 mm thick and 146 mm long - two face lines of 130 mm and
// an end cap with a 16 mm board. Both faces are shorter than the 200 mm minimum
// overlap, so the pairing never reads them, the model wall ends 206 mm short of the
// drawn end, and the device drawn 62 mm in front of that end cap has no wall end to
// sit on.
//
// Lowering the minimum overlap is NOT the fix: every jamb return and finish board
// would become a wall. Nor is "extend every wall to the next cap": a gap between two
// walls, a jamb and a different wall passing nearby look alike at that scale. Whether
// the box is the wall's own end, a column wrap or a finish return is the PROJECT's
// call (decision D6), so geometry.end_piers is opt-in:
//
//   "report"  every pier found is listed with its measures; nothing changes.
//   "extend"  the wall is extended through the pier to its end cap, at the WALL's
//             thickness (the type does not change), and the assumption is recorded.
//
// A pier is recognised only when ALL of this holds, and each refusal is reported:
//   - two face lines parallel to the wall, one on each side of it, starting at the
//     wall's end and running outward no longer than min_overlap_mm;
//   - the box they bound contains the wall's band (a thicker or equal stretch of the
//     same wall, not a different wall beside it) and is no thicker than the rule allows;
//   - it stands out further than a finish does (a board wrapped round a jamb is not one);
//   - an end cap closes it: a line across the whole box at the faces' outer end;
//   - no other reading occupies the box (a gap between two walls, a crossing wall).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadWallPiers
    {
        public const string Report = "report";
        public const string Extend = "extend";

        /// <summary>How far the pier's face lines may start from the wall's end, and its cap lie past them.</summary>
        public const double EndSlackMm = CadWallContinuity.FaceDepthMm;

        public static List<CadWallBand> Apply(List<CadWallBand> bands, IList<CadSegment> lines, string mode,
                                              double maxPierLengthMm, double maxThicknessMm,
                                              double angleToleranceDegrees, JArray evidence)
        {
            bool extend = mode == Extend;
            var result = new List<CadWallBand>();
            for (int i = 0; i < bands.Count; i++)
            {
                CadWallBand b = bands[i];
                double from = b.From, to = b.To;
                var notes = new List<string>();
                foreach (int side in new[] { -1, +1 })
                {
                    JObject found = Find(b, i, bands, lines, side, maxPierLengthMm, maxThicknessMm,
                                         angleToleranceDegrees);
                    if (found == null) continue;
                    bool ok = found["refused"] == null;
                    found["extended"] = extend && ok;
                    if (extend && ok)
                    {
                        double cap = (double)found["cap_along_mm"];
                        if (side < 0) from = cap; else to = cap;
                        notes.Add((string)found["means"]);
                    }
                    else if (ok)
                        found["means"] = (string)found["means"] + " Not extended: geometry.end_piers is 'report'.";
                    evidence?.Add(found);
                }
                if (Math.Abs(from - b.From) < 0.5 && Math.Abs(to - b.To) < 0.5)
                {
                    result.Add(b);
                    continue;
                }
                CadWallBand longer = b.Piece(from, to, b.Lo, b.Hi);
                longer.Assumptions.AddRange(notes);
                result.Add(longer);
            }
            return result;
        }

        /// <summary>The pier at one end of a band, as evidence (with "refused" when it is not one), or null.</summary>
        private static JObject Find(CadWallBand b, int self, List<CadWallBand> bands, IList<CadSegment> lines, int side,
                                    double maxLen, double maxThickness, double angleTol)
        {
            double end = side < 0 ? b.From : b.To;
            double cosTol = Math.Cos(angleTol * Math.PI / 180.0), sinTol = Math.Sin(angleTol * Math.PI / 180.0);
            double lowFace = double.NaN, highFace = double.NaN, lowOuter = double.NaN, highOuter = double.NaN;
            var caps = new List<double[]>();   // along, acrossMin, acrossMax
            foreach (CadSegment s in lines)
            {
                double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= 0) continue;
                double dot = Math.Abs((dx * b.Direction.X + dy * b.Direction.Y) / len);
                double fa = s.A.X * b.Direction.X + s.A.Y * b.Direction.Y;
                double fb = s.B.X * b.Direction.X + s.B.Y * b.Direction.Y;
                double oa = s.A.X * b.Normal.X + s.A.Y * b.Normal.Y;
                double ob = s.B.X * b.Normal.X + s.B.Y * b.Normal.Y;
                if (dot >= cosTol && Math.Abs(oa - ob) <= 1.0)
                {
                    // a face line: starts at the wall's end and runs outward
                    double inner = side < 0 ? Math.Max(fa, fb) : Math.Min(fa, fb);
                    double outer = side < 0 ? Math.Min(fa, fb) : Math.Max(fa, fb);
                    if (Math.Abs(inner - end) > EndSlackMm) continue;
                    double outward = side * (outer - end);
                    if (outward <= 1.0 || outward > maxLen) continue;
                    double o = (oa + ob) / 2;
                    // the nearest line outside each face of the band
                    if (o <= b.Lo + 1.0 && (double.IsNaN(lowFace) || o > lowFace)) { lowFace = o; lowOuter = outer; }
                    if (o >= b.Hi - 1.0 && (double.IsNaN(highFace) || o < highFace)) { highFace = o; highOuter = outer; }
                }
                else if (dot <= sinTol && Math.Abs(fa - fb) <= 1.0)
                    caps.Add(new[] { (fa + fb) / 2, Math.Min(oa, ob), Math.Max(oa, ob) });
            }
            if (double.IsNaN(lowFace) || double.IsNaN(highFace)) return null;   // nothing pier-like: no evidence

            double thickness = highFace - lowFace;
            double faceEnd = side < 0 ? Math.Max(lowOuter, highOuter) : Math.Min(lowOuter, highOuter);
            var ev = new JObject
            {
                ["end"] = side < 0 ? "from" : "to",
                ["wall_end_mm"] = R(end),
                ["wall_thickness_mm"] = R(b.ThicknessMm),
                ["pier_thickness_mm"] = R(thickness),
                ["pier_faces_across_mm"] = new JArray(R(lowFace), R(highFace)),
                ["pier_face_length_mm"] = R(side * (faceEnd - end))
            };
            // a board wrapped round a jamb stands out as far as a finish does, and no further
            if (side * (faceEnd - end) <= EndSlackMm)
                return Refuse(ev, "finish_wrap_not_a_pier");
            if (thickness > maxThickness + 1.0)
                return Refuse(ev, "pier_thicker_than_the_rule_allows");
            if (Math.Abs(lowOuter - highOuter) > EndSlackMm)
                return Refuse(ev, "pier_faces_end_apart");

            // the end cap: across the whole box, at the faces' outer end or up to EndSlackMm past it
            double? cap = null;
            foreach (double[] c in caps)
            {
                double past = side * (c[0] - faceEnd);
                if (past < -1.0 || past > EndSlackMm) continue;
                if (c[1] > lowFace + EndSlackMm || c[2] < highFace - EndSlackMm) continue;
                if (cap == null || side * (c[0] - cap.Value) > 0) cap = c[0];
            }
            if (cap == null) return Refuse(ev, "pier_not_closed_by_an_end_cap");
            ev["cap_along_mm"] = R(cap.Value);
            ev["pier_length_mm"] = R(side * (cap.Value - end));

            // nothing else may stand in the box
            double boxFrom = Math.Min(end, cap.Value) + 1.0, boxTo = Math.Max(end, cap.Value) - 1.0;
            for (int j = 0; j < bands.Count; j++)
            {
                if (j == self) continue;
                double[] r = Rect(bands[j], b);
                if (r[1] > boxFrom && r[0] < boxTo && r[3] > lowFace + 1.0 && r[2] < highFace - 1.0)
                {
                    ev["other_wall"] = new JArray(R(r[0]), R(r[1]), R(r[2]), R(r[3]));
                    return Refuse(ev, "pier_box_holds_another_wall");
                }
            }
            ev["means"] = "a pier " + F(thickness) + " mm thick and " + F(side * (cap.Value - end)) +
                          " mm long closes this wall's " + (side < 0 ? "start" : "end") + ": the wall (" +
                          F(b.ThicknessMm) + " mm) is extended to the pier's end cap at its own thickness; the type " +
                          "is not changed (geometry.end_piers 'extend').";
            return ev;
        }

        private static JObject Refuse(JObject ev, string why)
        {
            ev["refused"] = why;
            ev["extended"] = false;
            return ev;
        }

        /// <summary>Another band's footprint in this band's frame: along min/max, across min/max.</summary>
        private static double[] Rect(CadWallBand other, CadWallBand frame)
        {
            var corners = new List<CadPoint>();
            foreach (double along in new[] { other.From, other.To })
                foreach (double across in new[] { other.Lo, other.Hi })
                    corners.Add(new CadPoint(other.Direction.X * along + other.Normal.X * across,
                                             other.Direction.Y * along + other.Normal.Y * across));
            var alongs = corners.Select(p => p.X * frame.Direction.X + p.Y * frame.Direction.Y).ToList();
            var acrosses = corners.Select(p => p.X * frame.Normal.X + p.Y * frame.Normal.Y).ToList();
            return new[] { alongs.Min(), alongs.Max(), acrosses.Min(), acrosses.Max() };
        }

        private static double R(double v) => Math.Round(v, 1);
        private static string F(double v) => v.ToString("0", CultureInfo.InvariantCulture);
    }
}
