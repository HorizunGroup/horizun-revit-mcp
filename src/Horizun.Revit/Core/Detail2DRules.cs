// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// EVERY DECISION ABOUT 2D DETAIL GEOMETRY THAT DOES NOT NEED A REVIT.
//
// horizun_detail_2d draws lines, arcs, polylines and filled regions into a view,
// and horizun_query_detail_2d reads them back. Both need to answer the same two
// questions about geometry, and neither question needs a building:
//
//   * "is this drawable?" - a zero-length segment, an arc through three
//     collinear points, a boundary that crosses itself, a hole outside its
//     region: Revit rejects some of these with its least helpful sentence and
//     silently accepts others as garbage. The refusal has to happen BEFORE the
//     transaction, with a message that names the exact points, or the caller
//     burns a dry-run round trip per mistake;
//
//   * "is this the SAME geometry?" - verification re-reads what was committed,
//     and Revit is free to hand a curve back with its endpoints swapped, a loop
//     rotated to a different start curve, or the whole boundary traversed the
//     other way round. A comparison that treats any of those as a difference
//     refuses correct work; one that compares with raw doubles flags drift on
//     every regeneration. So identity is a SIGNATURE: every coordinate lands on
//     the 0.1 mm grid first (regeneration jitter never crosses it, a real move
//     does), a line's endpoints are ordered before they are rendered, and a
//     loop's signature is the ordinal minimum over every rotation of both
//     traversal directions - the same drawn figure gets the same string no
//     matter where Revit started reading it.
//
// The self-intersection and containment tests run on the SAME quantised grid as
// the signatures, in exact decimal arithmetic over integer ticks - so "touches"
// and "is identical to" are decided by the same notion of position, and no
// floating-point epsilon can answer one way in the validator and the other way
// in the fingerprint.
//
// Separators inside signatures are control characters, exactly as in
// DimensionPlanRules: LoopSignature and RegionSignature consume caller-supplied
// strings, and a printable separator would let a forged "curve signature"
// containing one collide two different loops into one identity. Inputs that
// carry a separator are refused with null - fail closed, never hash a forgery.
//
// Revit-free on purpose: no `using Autodesk`. The commands feed this file plain
// doubles and strings; the tests prove the rules without a model.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Horizun.Revit.Core
{
    public static class Detail2DRules
    {
        // ---- tolerances -----------------------------------------------------

        /// <summary>
        /// The tolerance every re-read curve comparison runs at, in internal feet,
        /// published in responses as comparison_tolerance_feet - the same number
        /// DimensionPlanRules uses, so "the same line" means one thing bridge-wide.
        /// </summary>
        public const double CurveToleranceFeet = 1e-6;

        /// <summary>
        /// The identity grid: 0.1 mm in internal feet (1 ft = 304.8 mm). Every
        /// coordinate is quantised onto this grid BEFORE it enters a signature or
        /// a topology decision, so regeneration jitter keeps an identity and a
        /// real move of 0.2 mm or more changes it.
        /// </summary>
        public const double QuantumFeet = 0.1 / 304.8;

        /// <summary>
        /// Coordinates beyond this are refused as malformed rather than quantised:
        /// past it the grid arithmetic would overflow, and no view holds a curve
        /// a billion feet from its origin anyway.
        /// </summary>
        public const double MaxCoordinateFeet = 1e9;

        // ---- limits (the command publishes these in its contract) -----------

        public const int MaxActions = 500;
        public const int MaxPolylinePoints = 200;
        public const int MaxLoopsPerRegion = 32;
        public const int MaxCurvesPerLoop = 200;

        // ---- curve kinds, spelled once --------------------------------------

        public const string KindLine = "line";
        public const string KindArc = "arc";

        // ---- structured error codes -----------------------------------------
        // Every validation message in this file BEGINS with its code followed by
        // ": ", so a client can branch on the prefix while a person reads the
        // sentence. The command-side codes (view, style, symbol, masking) live
        // here too so both commands and the contract spell them identically.

        public const string CodeOpenLoop = "open_loop";
        public const string CodeSelfIntersection = "self_intersection";
        public const string CodeDegenerateCurve = "degenerate_curve";
        public const string CodeNonCoplanar = "non_coplanar_geometry";
        public const string CodeAmbiguousResource = "ambiguous_resource";
        public const string CodeInvalidLineStyle = "invalid_line_style";
        public const string CodeMaskingMismatch = "masking_mismatch";
        public const string CodeInvalidGeometry = "invalid_geometry";
        public const string CodeLoopHierarchy = "invalid_loop_hierarchy";
        public const string CodeViewNotFound = "view_not_found";
        public const string CodeIncompatibleView = "incompatible_view";
        public const string CodeInvalidFamilySymbol = "invalid_family_symbol";
        public const string CodeInvalidPlacementType = "invalid_placement_type";

        // ---- signature separators -------------------------------------------
        // Unit separator between the fields of one curve, record separator
        // between the curves of one loop, group separator between the parts of
        // one region. Control characters so no input can forge a boundary.

        private const char Us = (char)31;
        private const char Rs = (char)30;
        private const char Gs = (char)29;

        // ---- quantisation ---------------------------------------------------

        private static long Ticks(double feet)
            => (long)Math.Round(feet / QuantumFeet, MidpointRounding.AwayFromZero);

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>A point onto the grid, refusing anything that is not [x, y, z] finite.</summary>
        private static bool TryQuantize(double[] p, out long[] q)
        {
            q = null;
            if (p == null || p.Length != 3) return false;
            var t = new long[3];
            for (int i = 0; i < 3; i++)
            {
                double v = p[i];
                if (!IsFinite(v) || Math.Abs(v) > MaxCoordinateFeet) return false;
                t[i] = Ticks(v);
            }
            q = t;
            return true;
        }

        /// <summary>
        /// One grid coordinate as the signature renders it: millimetres with one
        /// decimal, invariant culture. Ticks are integers, so a negative zero
        /// cannot exist here by construction.
        /// </summary>
        private static string Canon(long ticks)
            => (ticks / 10.0).ToString("0.0", CultureInfo.InvariantCulture);

        private static string Pt(long[] q) => Canon(q[0]) + "," + Canon(q[1]) + "," + Canon(q[2]);

        /// <summary>The human rendering of a point for error messages.</summary>
        private static string Show(double[] p)
        {
            long[] q;
            if (!TryQuantize(p, out q)) return "(unrenderable)";
            return "(" + Canon(q[0]) + ", " + Canon(q[1]) + ", " + Canon(q[2]) + ") mm";
        }

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static int Compare(long[] a, long[] b)
        {
            int c = a[0].CompareTo(b[0]);
            if (c != 0) return c;
            c = a[1].CompareTo(b[1]);
            return c != 0 ? c : a[2].CompareTo(b[2]);
        }

        private static bool SamePt(long[] a, long[] b)
            => a[0] == b[0] && a[1] == b[1] && a[2] == b[2];

        // ---- canonical signatures -------------------------------------------

        /// <summary>
        /// The direction-free identity of one straight segment. Both endpoints are
        /// quantised, then the lexicographically smaller one (x, then y, then z)
        /// leads - so the segment and its reverse, and the segment Revit handed
        /// back swapped, are one string. Null for malformed input (missing point,
        /// wrong arity, NaN, Infinity, out of range) - a signature over garbage
        /// would be an identity for something that was never drawn.
        /// </summary>
        public static string CanonicalLineSignature(double[] a, double[] b)
        {
            long[] qa, qb;
            if (!TryQuantize(a, out qa) || !TryQuantize(b, out qb)) return null;
            if (Compare(qa, qb) > 0) { long[] t = qa; qa = qb; qb = t; }
            return KindLine + Us + Pt(qa) + Us + Pt(qb);
        }

        /// <summary>
        /// The identity of one arc: quantised centre, quantised radius, and the
        /// two endpoints ordered exactly like a line's - Revit may return an arc
        /// traversed either way, and that is the same drawn arc. Null for
        /// malformed input or a radius that is not a finite positive length.
        /// </summary>
        public static string CanonicalArcSignature(double[] center, double radius, double[] start, double[] end)
        {
            long[] qc, qs, qe;
            if (!TryQuantize(center, out qc) || !TryQuantize(start, out qs) || !TryQuantize(end, out qe)) return null;
            if (!IsFinite(radius) || radius <= 0 || radius > MaxCoordinateFeet) return null;
            if (Compare(qs, qe) > 0) { long[] t = qs; qs = qe; qe = t; }
            return KindArc + Us + Pt(qc) + Us + Canon(Ticks(radius)) + Us + Pt(qs) + Us + Pt(qe);
        }

        /// <summary>
        /// One identity for one closed boundary, no matter where the traversal
        /// started or which way it went: all N rotations of the sequence and all N
        /// rotations of the reversed sequence are rendered, and the ordinal
        /// minimum wins. (The curve signatures are already direction-free, so
        /// reversing the traversal only reverses their ORDER.) Null for an empty
        /// list, a null or empty entry, or an entry carrying a separator control
        /// character - such an entry could collide two different loops.
        /// </summary>
        public static string LoopSignature(IReadOnlyList<string> curveSignatures)
        {
            if (curveSignatures == null || curveSignatures.Count == 0) return null;
            int n = curveSignatures.Count;
            var curves = new string[n];
            for (int i = 0; i < n; i++)
            {
                string s = curveSignatures[i];
                if (string.IsNullOrEmpty(s) || s.IndexOf(Rs) >= 0 || s.IndexOf(Gs) >= 0) return null;
                curves[i] = s;
            }

            string best = null;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int start = 0; start < n; start++)
                {
                    var sb = new StringBuilder();
                    for (int k = 0; k < n; k++)
                    {
                        int idx = pass == 0 ? (start + k) % n : ((start - k) % n + n) % n;
                        if (k > 0) sb.Append(Rs);
                        sb.Append(curves[idx]);
                    }
                    string candidate = sb.ToString();
                    if (best == null || string.CompareOrdinal(candidate, best) < 0) best = candidate;
                }
            }
            return "loop" + Rs + best;
        }

        /// <summary>
        /// One identity for one filled region: the outer loop is DISTINGUISHED
        /// (a region whose outer ring is B with hole A is not the region whose
        /// outer ring is A with hole B), and the holes are sorted ordinally so
        /// the order Revit enumerated them in cannot change the identity. Null
        /// for a missing outer signature, a null or empty hole entry, or any
        /// part carrying the region separator.
        /// </summary>
        public static string RegionSignature(string outerLoopSignature, IReadOnlyList<string> holeSignatures)
        {
            if (string.IsNullOrEmpty(outerLoopSignature) || outerLoopSignature.IndexOf(Gs) >= 0) return null;
            var holes = new List<string>();
            if (holeSignatures != null)
            {
                foreach (string h in holeSignatures)
                {
                    if (string.IsNullOrEmpty(h) || h.IndexOf(Gs) >= 0) return null;
                    holes.Add(h);
                }
            }
            holes.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder("region").Append(Gs).Append(outerLoopSignature);
            foreach (string h in holes) sb.Append(Gs).Append(h);
            return sb.ToString();
        }

        /// <summary>
        /// SHA-256 hex over a canonical string - the one hash the whole bridge
        /// uses, delegated to RequestFingerprint so there is a single
        /// implementation to trust.
        /// </summary>
        public static string Sha256Hex(string canonical) => RequestFingerprint.Sha256Hex(canonical);

        // ---- validation: points and segments --------------------------------

        private static string BadPoint(double[] p, string name)
        {
            if (p == null)
                return CodeInvalidGeometry + ": " + name + " is missing; a point is [x, y, z] in internal feet.";
            if (p.Length != 3)
                return CodeInvalidGeometry + ": " + name + " has " + p.Length + " coordinates; a point is " +
                       "[x, y, z] - pad z with 0 for a view-plane point.";
            for (int i = 0; i < 3; i++)
            {
                double v = p[i];
                if (double.IsNaN(v))
                    return CodeInvalidGeometry + ": " + name + "[" + i + "] is NaN - a coordinate nobody " +
                           "computed. Nothing can be drawn at it.";
                if (double.IsInfinity(v))
                    return CodeInvalidGeometry + ": " + name + "[" + i + "] is " + (v > 0 ? "+" : "-") +
                           "Infinity, which is not a place in a view.";
                if (Math.Abs(v) > MaxCoordinateFeet)
                    return CodeInvalidGeometry + ": " + name + "[" + i + "] is " + R(v) + " ft; coordinates " +
                           "beyond ±" + R(MaxCoordinateFeet) + " ft are outside anything a view can hold.";
            }
            return null;
        }

        private static double Dist2(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Null when a straight segment can be drawn; a coded message when it
        /// cannot. Degeneracy is judged on the raw 3D length against the central
        /// tolerance - a segment shorter than 1e-6 ft is a point wearing the
        /// shape of a line.
        /// </summary>
        public static string ValidateSegment(double[] a, double[] b)
        {
            string bad = BadPoint(a, "start") ?? BadPoint(b, "end");
            if (bad != null) return bad;
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d < CurveToleranceFeet)
                return CodeDegenerateCurve + ": the segment from " + Show(a) + " to " + Show(b) + " is " +
                       R(d) + " ft long; under the " + R(CurveToleranceFeet) + " ft tolerance that is a " +
                       "point, not a line.";
            return null;
        }

        /// <summary>
        /// Solves the circle through start, end and point_on_arc by circumcentre,
        /// in the view plane. The three points must share a z (on the 0.1 mm
        /// grid), be pairwise distinct, and not be collinear - collinearity is
        /// judged by point_on_arc's distance off the start-end chord against the
        /// central tolerance, and the refusal names all three points. On success
        /// center is [x, y, z] (z the mean of the inputs) and radius is positive;
        /// on any refusal center is null and radius 0 - never a half answer.
        /// </summary>
        public static string ValidateArcByThreePoints(double[] s, double[] e, double[] pOn,
                                                      out double[] center, out double radius)
        {
            center = null;
            radius = 0;
            string bad = BadPoint(s, "start") ?? BadPoint(e, "end") ?? BadPoint(pOn, "point_on_arc");
            if (bad != null) return bad;

            long zs = Ticks(s[2]), ze = Ticks(e[2]), zp = Ticks(pOn[2]);
            if (zs != ze || zs != zp)
                return CodeNonCoplanar + ": start, end and point_on_arc sit at different heights (z = " +
                       Canon(zs) + ", " + Canon(ze) + ", " + Canon(zp) + " mm on the 0.1 mm grid); an arc " +
                       "is a view-plane figure - send all three at the same z.";

            if (Dist2(s, e) < CurveToleranceFeet)
                return CodeDegenerateCurve + ": start and end coincide at " + Show(s) + "; three DISTINCT " +
                       "points define an arc.";
            if (Dist2(s, pOn) < CurveToleranceFeet)
                return CodeDegenerateCurve + ": start and point_on_arc coincide at " + Show(s) + "; three " +
                       "DISTINCT points define an arc.";
            if (Dist2(e, pOn) < CurveToleranceFeet)
                return CodeDegenerateCurve + ": end and point_on_arc coincide at " + Show(e) + "; three " +
                       "DISTINCT points define an arc.";

            double vx = e[0] - s[0], vy = e[1] - s[1];
            double chord = Math.Sqrt(vx * vx + vy * vy);
            double off = Math.Abs(vx * (pOn[1] - s[1]) - vy * (pOn[0] - s[0])) / chord;
            if (off < CurveToleranceFeet)
                return CodeDegenerateCurve + ": the three points " + Show(s) + ", " + Show(e) + " and " +
                       Show(pOn) + " are collinear (point_on_arc sits " + R(off) + " ft off the chord, " +
                       "under the " + R(CurveToleranceFeet) + " ft tolerance) - no circle passes through them.";

            double ax = s[0], ay = s[1], bx = e[0], by = e[1], cx = pOn[0], cy = pOn[1];
            double d = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
            double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
            double r = Math.Sqrt((ux - ax) * (ux - ax) + (uy - ay) * (uy - ay));
            if (!IsFinite(ux) || !IsFinite(uy) || !IsFinite(r) || r <= 0)
                return CodeDegenerateCurve + ": no finite circle passes through " + Show(s) + ", " + Show(e) +
                       " and " + Show(pOn) + " - the three points are numerically collinear.";

            center = new[] { ux, uy, (s[2] + e[2] + pOn[2]) / 3.0 };
            radius = r;
            return null;
        }

        // ---- validation: polylines ------------------------------------------

        /// <summary>
        /// Null when a polyline's vertex list can be drawn as consecutive
        /// segments. Continuity is implicit - the points ARE the vertices - so
        /// the only ways to break it are too few points, a vertex the grid cannot
        /// tell from its neighbour, or (closed) repeating the first vertex, whose
        /// closing segment is implicit. An OPEN polyline may cross itself - that
        /// is a drawing, not a region boundary; only ValidateLoop forbids it.
        /// </summary>
        public static string ValidatePolyline(IReadOnlyList<double[]> pts, bool closed)
        {
            if (pts == null || pts.Count == 0)
                return CodeInvalidGeometry + ": a polyline needs points; none were sent.";
            if (pts.Count > MaxPolylinePoints)
                return CodeInvalidGeometry + ": the polyline has " + pts.Count + " points; the limit is " +
                       MaxPolylinePoints + " per action. Split it - consecutive polylines chain by sharing " +
                       "an endpoint.";
            if (pts.Count < 2)
                return CodeInvalidGeometry + ": a polyline needs at least 2 points; 1 was sent.";
            if (closed && pts.Count < 3)
                return CodeOpenLoop + ": a closed polyline needs at least 3 vertices; " + pts.Count +
                       " were sent - two points close into a doubled segment, not a loop.";

            var q = new long[pts.Count][];
            for (int i = 0; i < pts.Count; i++)
            {
                string bad = BadPoint(pts[i], "points[" + i + "]");
                if (bad != null) return bad;
                TryQuantize(pts[i], out q[i]);
            }
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                if (SamePt(q[i], q[i + 1]))
                    return CodeDegenerateCurve + ": points[" + i + "] and points[" + (i + 1) + "] quantize " +
                           "to the same 0.1 mm grid point " + Show(pts[i]) + " - a zero-length segment " +
                           "cannot be drawn. Drop one of them.";
            }
            if (closed && SamePt(q[pts.Count - 1], q[0]))
                return CodeDegenerateCurve + ": points[" + (pts.Count - 1) + "] repeats points[0] on the " +
                       "0.1 mm grid; a closed polyline lists each vertex once - the closing segment is implicit.";
            return null;
        }

        // ---- validation: loops ----------------------------------------------

        /// <summary>
        /// Null when the vertex list bounds a region: at least 3 vertices (the
        /// closing segment is implicit), one z plane, no segment the grid cannot
        /// resolve, and NO self-intersection. The intersection test runs pairwise
        /// over the quantised segments in exact arithmetic: consecutive segments
        /// may share exactly their common vertex (doubling back along each other
        /// is refused), and any contact between non-consecutive segments -
        /// crossing, touching an edge, or touching a vertex - is refused naming
        /// both segments.
        /// </summary>
        public static string ValidateLoop(IReadOnlyList<double[]> pts)
        {
            if (pts == null || pts.Count == 0)
                return CodeInvalidGeometry + ": a loop needs vertices; none were sent.";
            if (pts.Count > MaxCurvesPerLoop)
                return CodeInvalidGeometry + ": the loop has " + pts.Count + " vertices; the limit is " +
                       MaxCurvesPerLoop + " per loop.";
            if (pts.Count < 3)
                return CodeOpenLoop + ": a loop needs at least 3 vertices; " + pts.Count +
                       (pts.Count == 1 ? " was" : " were") + " sent. The closing segment is implicit - " +
                       "2 points bound a segment, not a region.";

            int n = pts.Count;
            var q = new long[n][];
            for (int i = 0; i < n; i++)
            {
                string bad = BadPoint(pts[i], "points[" + i + "]");
                if (bad != null) return bad;
                TryQuantize(pts[i], out q[i]);
            }

            for (int i = 1; i < n; i++)
            {
                if (q[i][2] != q[0][2])
                    return CodeNonCoplanar + ": points[" + i + "] sits at z = " + Canon(q[i][2]) +
                           " mm while points[0] sits at z = " + Canon(q[0][2]) + " mm (0.1 mm grid); " +
                           "a loop lives in one view plane.";
            }

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (!SamePt(q[i], q[j])) continue;
                return j == 0
                    ? CodeDegenerateCurve + ": points[" + i + "] repeats points[0] on the 0.1 mm grid; " +
                      "the closing segment is implicit - list each vertex once."
                    : CodeDegenerateCurve + ": points[" + i + "] and points[" + j + "] quantize to the " +
                      "same 0.1 mm grid point " + Show(pts[i]) + " - a zero-length segment cannot bound " +
                      "a region.";
            }

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    bool adjacent = j == i + 1 || (i == 0 && j == n - 1);
                    if (adjacent)
                    {
                        long[] a, v, c;
                        if (j == i + 1) { a = q[i]; v = q[j]; c = q[(j + 1) % n]; }
                        else { a = q[n - 1]; v = q[0]; c = q[1]; }
                        if (Orient(a, v, c) == 0 && (OnSeg(a, v, c) || OnSeg(v, c, a)))
                            return CodeSelfIntersection + ": segment " + i + " (points[" + i + "]->points[" +
                                   ((i + 1) % n) + "]) and segment " + j + " (points[" + j + "]->points[" +
                                   ((j + 1) % n) + "]) double back along each other - consecutive segments " +
                                   "may share only their common vertex.";
                    }
                    else if (SegmentsTouch(q[i], q[(i + 1) % n], q[j], q[(j + 1) % n]))
                    {
                        return CodeSelfIntersection + ": segment " + i + " (points[" + i + "]->points[" +
                               (i + 1) + "]) and segment " + j + " (points[" + j + "]->points[" +
                               ((j + 1) % n) + "]) cross or touch - the boundary must be a simple loop " +
                               "(touching at a shared non-consecutive vertex counts).";
                    }
                }
            }
            return null;
        }

        // ---- exact 2D primitives on the quantised grid ----------------------
        // Ticks are integers; the cross products are computed in decimal, which
        // holds every product of two coordinate differences exactly. No epsilon,
        // no false sign near zero.

        private static int Orient(long[] a, long[] b, long[] c)
        {
            decimal v = (decimal)(b[0] - a[0]) * (c[1] - a[1]) - (decimal)(b[1] - a[1]) * (c[0] - a[0]);
            return v > 0m ? 1 : (v < 0m ? -1 : 0);
        }

        /// <summary>Collinearity already established; is c within the bbox of [a, b]?</summary>
        private static bool OnSeg(long[] a, long[] b, long[] c)
            => Math.Min(a[0], b[0]) <= c[0] && c[0] <= Math.Max(a[0], b[0])
            && Math.Min(a[1], b[1]) <= c[1] && c[1] <= Math.Max(a[1], b[1]);

        /// <summary>Any contact at all: proper crossing, endpoint touch, collinear overlap.</summary>
        private static bool SegmentsTouch(long[] p1, long[] p2, long[] q1, long[] q2)
        {
            int o1 = Orient(p1, p2, q1), o2 = Orient(p1, p2, q2);
            int o3 = Orient(q1, q2, p1), o4 = Orient(q1, q2, p2);
            if (o1 * o2 < 0 && o3 * o4 < 0) return true;
            if (o1 == 0 && OnSeg(p1, p2, q1)) return true;
            if (o2 == 0 && OnSeg(p1, p2, q2)) return true;
            if (o3 == 0 && OnSeg(q1, q2, p1)) return true;
            if (o4 == 0 && OnSeg(q1, q2, p2)) return true;
            return false;
        }

        /// <summary>1 strictly inside, 0 on the boundary, -1 outside. Exact.</summary>
        private static int PointInPolygon(long[] p, long[][] poly)
        {
            bool inside = false;
            int n = poly.Length;
            for (int i = 0; i < n; i++)
            {
                long[] a = poly[i], b = poly[(i + 1) % n];
                if (Orient(a, b, p) == 0 && OnSeg(a, b, p)) return 0;
                if ((a[1] > p[1]) != (b[1] > p[1]))
                {
                    // Does the edge cross the horizontal ray to +x? Decided by sign
                    // arithmetic instead of a division, so it stays exact.
                    decimal num = (decimal)(a[0] - p[0]) * (b[1] - a[1]) + (decimal)(p[1] - a[1]) * (b[0] - a[0]);
                    bool crosses = b[1] > a[1] ? num > 0m : num < 0m;
                    if (crosses) inside = !inside;
                }
            }
            return inside ? 1 : -1;
        }

        private static bool TryQuantizeLoop(IReadOnlyList<double[]> pts, out long[][] q)
        {
            q = null;
            if (pts == null || pts.Count < 3) return false;
            var result = new long[pts.Count][];
            for (int i = 0; i < pts.Count; i++)
                if (!TryQuantize(pts[i], out result[i])) return false;
            q = result;
            return true;
        }

        private static bool LoopsTouch(long[][] a, long[][] b)
        {
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    if (SegmentsTouch(a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length])) return true;
            return false;
        }

        // ---- validation: region hierarchy -----------------------------------

        /// <summary>
        /// Whether outer STRICTLY contains inner, in the view plane (z is not
        /// consulted - coplanarity is ValidateRegionLoops' check). True only when
        /// every vertex of inner is strictly inside outer AND no edge of inner
        /// touches or crosses an edge of outer - a hole that touches its region's
        /// boundary is not inside it. Malformed input (null, fewer than 3
        /// vertices, non-finite coordinates) answers false, never a guess. The
        /// winding direction of either loop does not matter.
        /// </summary>
        public static bool LoopContains(IReadOnlyList<double[]> outer, IReadOnlyList<double[]> inner)
        {
            long[][] o, inn;
            if (!TryQuantizeLoop(outer, out o) || !TryQuantizeLoop(inner, out inn)) return false;
            for (int i = 0; i < inn.Length; i++)
                if (PointInPolygon(inn[i], o) != 1) return false;
            return !LoopsTouch(inn, o);
        }

        private static string WithLoopContext(string error, int loopIndex)
        {
            int colon = error.IndexOf(':');
            if (colon < 0) return error + " (loops[" + loopIndex + "])";
            return error.Substring(0, colon) + ": loops[" + loopIndex + "]:" + error.Substring(colon + 1);
        }

        /// <summary>
        /// The whole region shape, decided at once: every loop individually valid
        /// (the failing loop's index is prefixed into the message), all loops on
        /// one z plane, EXACTLY one loop containing all the others (its index
        /// comes out as outerIndex - the caller may send outer-first or not, the
        /// hierarchy is detected, never assumed from position), and the holes
        /// mutually disjoint: none contains another, none touches or overlaps
        /// another. Every violation is a coded message naming the loop indices
        /// involved. On any error outerIndex is -1.
        /// </summary>
        public static string ValidateRegionLoops(IReadOnlyList<IReadOnlyList<double[]>> loops, out int outerIndex)
        {
            outerIndex = -1;
            if (loops == null || loops.Count == 0)
                return CodeInvalidGeometry + ": a region needs at least one loop; none were sent.";
            if (loops.Count > MaxLoopsPerRegion)
                return CodeInvalidGeometry + ": the region has " + loops.Count + " loops; the limit is " +
                       MaxLoopsPerRegion + " per region.";

            for (int i = 0; i < loops.Count; i++)
            {
                string err = ValidateLoop(loops[i]);
                if (err != null) return WithLoopContext(err, i);
            }

            long z0 = Ticks(loops[0][0][2]);
            for (int i = 1; i < loops.Count; i++)
            {
                long zi = Ticks(loops[i][0][2]);
                if (zi != z0)
                    return CodeNonCoplanar + ": loops[" + i + "] sits at z = " + Canon(zi) + " mm while " +
                           "loops[0] sits at z = " + Canon(z0) + " mm (0.1 mm grid); a region's loops " +
                           "live in one view plane.";
            }

            if (loops.Count == 1)
            {
                outerIndex = 0;
                return null;
            }

            int n = loops.Count;
            var q = new long[n][][];
            for (int i = 0; i < n; i++) TryQuantizeLoop(loops[i], out q[i]);

            var contains = new bool[n, n];
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n; k++)
                    if (j != k) contains[j, k] = LoopContains(loops[j], loops[k]);

            var candidates = new List<int>();
            for (int j = 0; j < n; j++)
            {
                bool all = true;
                for (int k = 0; k < n && all; k++)
                    if (k != j && !contains[j, k]) all = false;
                if (all) candidates.Add(j);
            }

            if (candidates.Count == 0)
            {
                int bestJ = 0, bestCount = -1;
                for (int j = 0; j < n; j++)
                {
                    int count = 0;
                    for (int k = 0; k < n; k++) if (k != j && contains[j, k]) count++;
                    if (count > bestCount) { bestCount = count; bestJ = j; }
                }
                var sb = new StringBuilder(CodeLoopHierarchy);
                sb.Append(": no single loop contains all the others - a filled region is one outer ")
                  .Append("boundary with every hole strictly inside it. loops[").Append(bestJ)
                  .Append("] contains ").Append(bestCount).Append(" of the other ").Append(n - 1)
                  .Append(" but not ");
                bool first = true;
                for (int k = 0; k < n; k++)
                {
                    if (k == bestJ || contains[bestJ, k]) continue;
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append("loops[").Append(k).Append("] (which ")
                      .Append(LoopsTouch(q[bestJ], q[k]) ? "touches or crosses it" : "lies outside it")
                      .Append(")");
                }
                sb.Append(". Split disjoint areas into separate regions.");
                return sb.ToString();
            }

            if (candidates.Count > 1)
                return CodeLoopHierarchy + ": loops[" + candidates[0] + "] and loops[" + candidates[1] +
                       "] each contain every other loop - the nesting is deeper than one outer boundary " +
                       "with holes. A region has exactly one outer loop; model nested rings as separate regions.";

            int outer = candidates[0];
            for (int i = 0; i < n; i++)
            {
                if (i == outer) continue;
                for (int k = i + 1; k < n; k++)
                {
                    if (k == outer) continue;
                    if (contains[i, k] || contains[k, i])
                    {
                        int big = contains[i, k] ? i : k, small = contains[i, k] ? k : i;
                        return CodeLoopHierarchy + ": loops[" + big + "] contains loops[" + small +
                               "], and neither is the outer boundary (loops[" + outer + "] is) - holes " +
                               "must be mutually disjoint. An island inside a hole is a separate region, " +
                               "not a third nesting level.";
                    }
                    if (LoopsTouch(q[i], q[k]))
                        return CodeLoopHierarchy + ": loops[" + i + "] and loops[" + k + "] touch or " +
                               "overlap - holes must be mutually disjoint; merge them into one loop or " +
                               "pull them apart.";
                }
            }

            outerIndex = outer;
            return null;
        }
    }
}
