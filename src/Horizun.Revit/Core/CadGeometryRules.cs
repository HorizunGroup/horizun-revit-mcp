// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The Revit-FREE geometry a DWG reading is made of.
//
// Everything a CAD drawing means to a model has to survive three hostile facts,
// and they are the reason this file exists apart from Revit:
//
//   1. A DWG DOES NOT SAY WHAT IT IS. It says "two parallel lines 200 mm apart".
//      Whether that is a wall is an interpretation, and an interpretation has to
//      be arguable - which means the geometry it argues from must be inspectable
//      without a Revit in the room.
//
//   2. NOTHING IN THE DWG HAS A STABLE NAME. Measured on Revit 2026 against a
//      real linked DWG: GeometryObject.Id COLLIDES - 35 objects came back with
//      24 distinct ids, and nine PolyLines all answered Id = 1. There is no
//      AutoCAD handle anywhere in the Revit API (an exhaustive member search
//      over RevitAPI.dll found none). So identity is a SURROGATE we compute, and
//      the algorithm that computes it has to be pinned by tests, because every
//      incremental update and every audit match depends on it being the same
//      number tomorrow.
//
//   3. COORDINATES ARE NOT MILLIMETRES. They are whatever the drawing was set
//      up in, transformed by however it was linked. Unit normalisation is the
//      first thing that happens and the last thing anyone should have to think
//      about afterwards.
//
// Nothing here reads a model, opens a document or touches the Revit API. Feed it
// numbers, get numbers back, and pin them with tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// How a length in a drawing becomes a length in millimetres.
    ///
    /// The unit is DECLARED - read from the CAD link type's own "Import Units"
    /// parameter, or stated by the caller - and never guessed from the size of
    /// the numbers. A drawing whose walls are 0.2 apart is either metres or a
    /// mistake, and this file refuses to decide which.
    /// </summary>
    public static class CadUnits
    {
        public const double MillimetresPerFoot = 304.8;
        public const double MillimetresPerInch = 25.4;

        /// <summary>
        /// Millimetres per one unit of the named CAD unit. The names are the ones
        /// Revit's own ImportUnit enum uses, lower-cased; "default" and "custom"
        /// are NOT resolvable here and return null rather than a plausible guess.
        /// </summary>
        public static double? MillimetresPer(string unit)
        {
            switch ((unit ?? "").Trim().ToLowerInvariant())
            {
                case "millimeter":
                case "millimetre":
                case "mm": return 1.0;
                case "centimeter":
                case "centimetre":
                case "cm": return 10.0;
                case "decimeter":
                case "decimetre":
                case "dm": return 100.0;
                case "meter":
                case "metre":
                case "m": return 1000.0;
                case "inch":
                case "in": return MillimetresPerInch;
                case "foot":
                case "feet":
                case "ft": return MillimetresPerFoot;
                case "ussurveyfoot":
                case "us survey foot": return 1200.0 / 3937.0 * 1000.0;
                default: return null;   // "default", "custom", anything unknown
            }
        }

        /// <summary>Revit's internal length unit is the decimal foot. This is the only place that is written down.</summary>
        public static double FeetToMm(double feet) => feet * MillimetresPerFoot;

        public static double MmToFeet(double mm) => mm / MillimetresPerFoot;
    }

    /// <summary>A point in millimetres, in whatever frame the caller declared.</summary>
    public struct CadPoint : IEquatable<CadPoint>
    {
        public readonly double X, Y, Z;
        public CadPoint(double x, double y, double z = 0) { X = x; Y = y; Z = z; }

        public double DistanceTo(CadPoint other)
        {
            double dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Distance ignoring Z. A plan drawing is a plan drawing.</summary>
        public double PlanDistanceTo(CadPoint other)
        {
            double dx = X - other.X, dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public CadPoint WithZ(double z) => new CadPoint(X, Y, z);

        /// <summary>
        /// The point snapped to a tolerance grid. Two points that quantize the
        /// same are the same NODE - which is how gaps close and how a surrogate
        /// id survives a redraw that moved a vertex by a micron.
        /// </summary>
        public CadPoint Quantize(double tolerance)
        {
            if (tolerance <= 0) return this;
            return new CadPoint(Round(X, tolerance), Round(Y, tolerance), Round(Z, tolerance));
        }

        private static double Round(double v, double tol)
        {
            // Away-from-zero so -0.5 and 0.5 land symmetrically; a signed drawing
            // must not drift toward the origin.
            double snapped = Math.Round(v / tol, MidpointRounding.AwayFromZero) * tol;
            return snapped == 0 ? 0 : snapped;   // never hand back negative zero
        }

        public string Key(double tolerance)
        {
            CadPoint q = Quantize(tolerance);
            return string.Format(CultureInfo.InvariantCulture, "{0:0.####}|{1:0.####}|{2:0.####}", q.X, q.Y, q.Z);
        }

        public bool Equals(CadPoint other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is CadPoint p && Equals(p);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }

    /// <summary>What kind of drawn thing a curve came from. Measured on Revit 2026, these are what actually arrive.</summary>
    public enum CadCurveKind { Line, Arc, Polyline, Spline, Unknown }

    /// <summary>
    /// One straight run between two points, carrying where it came from.
    ///
    /// Arcs and polylines are decomposed INTO these for topology, and the
    /// decomposition is recorded on the segment (SourceKind) so nothing pretends
    /// a chord was drawn as a line.
    /// </summary>
    public sealed class CadSegment
    {
        public CadPoint A { get; }
        public CadPoint B { get; }
        /// <summary>The DWG layer this came from, exactly as Revit reported it. Never inferred.</summary>
        public string Layer { get; }
        public CadCurveKind SourceKind { get; }
        /// <summary>Index of this segment within its source curve, so a chord can name its arc.</summary>
        public int SourceIndex { get; }

        /// <summary>
        /// WHICH curve this chord came from, stable within one harvest.
        ///
        /// SourceIndex restarts at 0 for every curve, so two arcs on one layer
        /// produce chords that cannot be told apart or regrouped. Without this
        /// id, an arc reading can never be reassembled from the segments it was
        /// broken into. Null for a line, which is its own curve.
        /// </summary>
        public string SourceCurveId { get; }

        public CadSegment(CadPoint a, CadPoint b, string layer = null,
                          CadCurveKind sourceKind = CadCurveKind.Line, int sourceIndex = 0,
                          string sourceCurveId = null)
        {
            A = a; B = b; Layer = layer; SourceKind = sourceKind; SourceIndex = sourceIndex;
            SourceCurveId = sourceCurveId;
        }

        public double Length => A.DistanceTo(B);
        public double PlanLength => A.PlanDistanceTo(B);
        public CadPoint Midpoint => new CadPoint((A.X + B.X) / 2, (A.Y + B.Y) / 2, (A.Z + B.Z) / 2);

        /// <summary>Unit direction in plan. Null for a segment with no plan length - a degenerate is not a direction.</summary>
        public CadVector? PlanDirection
        {
            get
            {
                double dx = B.X - A.X, dy = B.Y - A.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= 0) return null;
                return new CadVector(dx / len, dy / len);
            }
        }

        public CadSegment Reversed() => new CadSegment(B, A, Layer, SourceKind, SourceIndex);

        public override string ToString() => A + "->" + B + (Layer == null ? "" : " [" + Layer + "]");
    }

    /// <summary>
    /// AN ARC, KEPT AS AN ARC.
    ///
    /// The harvest chords every curve, because topology - pairing, loops,
    /// containment - is line work and chords are what it can consume. But a chord
    /// is a lossy reading of an arc, and a curved wall built from chords is N
    /// straight walls that no audit can ever match back: CadAuditRules reduces a
    /// geometry to its first and last point, so a correctly built arc wall would
    /// read as massively moved.
    ///
    /// So the arc is recorded ALONGSIDE its chords. Nothing downstream is forced
    /// to use it - a rule that does not ask for arcs still gets the chorded
    /// reading, which stays the honest fallback - and a rule that does can build
    /// the real curve.
    /// </summary>
    public sealed class CadArcFact
    {
        /// <summary>Stable within one harvest, and shared with every chord this arc produced.</summary>
        public string CurveId { get; }
        public CadPoint Centre { get; }
        public double RadiusMm { get; }
        public CadPoint Start { get; }
        public CadPoint End { get; }
        /// <summary>A point ON the arc between its ends - what Arc.Create needs, and what a chord cannot give.</summary>
        public CadPoint Middle { get; }
        public string Layer { get; }
        /// <summary>How many chords this arc was decomposed into, so a reader can see what it cost.</summary>
        public int ChordCount { get; }
        /// <summary>The declared chord tolerance those chords honour. Not a guess - the rule's own number.</summary>
        public double SagittaMm { get; }

        public CadArcFact(string curveId, CadPoint centre, double radiusMm, CadPoint start, CadPoint end,
                          CadPoint middle, string layer, int chordCount, double sagittaMm)
        {
            CurveId = curveId; Centre = centre; RadiusMm = radiusMm;
            Start = start; End = end; Middle = middle;
            Layer = layer; ChordCount = chordCount; SagittaMm = sagittaMm;
        }

        /// <summary>The angle the arc sweeps, in radians, always positive.</summary>
        public double SweepRadians
        {
            get
            {
                double a0 = Math.Atan2(Start.Y - Centre.Y, Start.X - Centre.X);
                double a1 = Math.Atan2(End.Y - Centre.Y, End.X - Centre.X);
                double am = Math.Atan2(Middle.Y - Centre.Y, Middle.X - Centre.X);
                double direct = Normalise(a1 - a0);
                double viaMiddle = Normalise(am - a0);
                // If the middle is not inside the direct sweep, the arc goes the
                // other way round - which is the whole of "clockwise".
                return viaMiddle <= direct ? direct : (2 * Math.PI - direct);
            }
        }

        /// <summary>True when the arc runs clockwise in plan from Start to End.</summary>
        public bool Clockwise
        {
            get
            {
                double a0 = Math.Atan2(Start.Y - Centre.Y, Start.X - Centre.X);
                double a1 = Math.Atan2(End.Y - Centre.Y, End.X - Centre.X);
                double am = Math.Atan2(Middle.Y - Centre.Y, Middle.X - Centre.X);
                return Normalise(am - a0) > Normalise(a1 - a0);
            }
        }

        private static double Normalise(double radians)
        {
            while (radians < 0) radians += 2 * Math.PI;
            while (radians >= 2 * Math.PI) radians -= 2 * Math.PI;
            return radians;
        }

        public JObject ToJson() => new JObject
        {
            ["curve_id"] = CurveId,
            ["layer"] = Layer,
            ["centre_mm"] = new JArray(Math.Round(Centre.X, 4), Math.Round(Centre.Y, 4), Math.Round(Centre.Z, 4)),
            ["radius_mm"] = Math.Round(RadiusMm, 4),
            ["start_mm"] = new JArray(Math.Round(Start.X, 4), Math.Round(Start.Y, 4), Math.Round(Start.Z, 4)),
            ["end_mm"] = new JArray(Math.Round(End.X, 4), Math.Round(End.Y, 4), Math.Round(End.Z, 4)),
            ["middle_mm"] = new JArray(Math.Round(Middle.X, 4), Math.Round(Middle.Y, 4), Math.Round(Middle.Z, 4)),
            ["sweep_degrees"] = Math.Round(SweepRadians * 180.0 / Math.PI, 4),
            ["clockwise"] = Clockwise,
            ["chords"] = ChordCount,
            ["chord_sagitta_mm"] = SagittaMm
        };
    }

    /// <summary>A plan direction. Two dimensions, because parallelism in a plan drawing is a plan question.</summary>
    public struct CadVector
    {
        public readonly double X, Y;
        public CadVector(double x, double y) { X = x; Y = y; }
        public double Dot(CadVector o) => X * o.X + Y * o.Y;
        public double Cross(CadVector o) => X * o.Y - Y * o.X;

        /// <summary>
        /// Angle to another direction in degrees, folded to [0, 90]: a wall pair
        /// is parallel whether the two lines were drawn the same way round or not.
        /// </summary>
        public double UndirectedAngleDegrees(CadVector o)
        {
            double dot = Math.Abs(Dot(o));
            if (dot > 1) dot = 1;
            return Math.Acos(dot) * 180.0 / Math.PI;
        }

        public CadVector PerpendicularLeft() => new CadVector(-Y, X);
    }

    /// <summary>
    /// Identity for something a DWG never named.
    ///
    /// There is no handle. GeometryObject.Id collides (measured). So an entity's
    /// identity is computed from what it IS and where it sits, quantized so that
    /// re-linking the same file, or enumerating in a different order, yields the
    /// same string. Everything downstream - provenance, incremental diffing,
    /// audit matching - hangs off this being stable and being honest about what
    /// it includes.
    /// </summary>
    public static class CadIdentity
    {
        /// <summary>ASCII unit separator: it cannot occur in a layer name or a hash,
        /// so no two different inputs can concatenate into the same string.</summary>
        private const char Sep = '\u001f';

        /// <summary>
        /// The surrogate. Deliberately includes the source hash and the layer:
        /// the SAME geometry on a different layer is a different thing to a
        /// building, and the same drawing re-issued is a different source.
        /// </summary>
        public static string Surrogate(string sourceHash, string layerPath, string instancePath,
                                       CadCurveKind kind, IEnumerable<CadPoint> points, double tolerance)
        {
            var sb = new StringBuilder();
            sb.Append(sourceHash ?? "(no-source-hash)").Append(Sep);
            sb.Append(layerPath ?? "(no-layer)").Append(Sep);
            sb.Append(instancePath ?? "(root)").Append(Sep);
            sb.Append(kind).Append(Sep);
            sb.Append(tolerance.ToString("0.######", CultureInfo.InvariantCulture)).Append(Sep);
            foreach (CadPoint p in points ?? Enumerable.Empty<CadPoint>())
                sb.Append(p.Key(tolerance)).Append(';');
            return "cad:" + Sha256Hex(sb.ToString()).Substring(0, 24);
        }

        /// <summary>
        /// The same surrogate, for a curve whose direction is meaningless. A wall
        /// drawn left-to-right and right-to-left is one wall; without this the
        /// second issue of a drawing looks like a full replacement.
        /// </summary>
        public static string SurrogateUndirected(string sourceHash, string layerPath, string instancePath,
                                                 CadCurveKind kind, IList<CadPoint> points, double tolerance)
        {
            if (points == null || points.Count == 0)
                return Surrogate(sourceHash, layerPath, instancePath, kind, points, tolerance);
            var forward = points.Select(p => p.Key(tolerance)).ToList();
            var backward = Enumerable.Reverse(forward).ToList();
            bool forwardWins = string.CompareOrdinal(string.Join(";", forward), string.Join(";", backward)) <= 0;
            IList<CadPoint> canonical = forwardWins ? points : Enumerable.Reverse(points).ToList();
            return Surrogate(sourceHash, layerPath, instancePath, kind, canonical, tolerance);
        }

        /// <summary>
        /// WHAT THE THING IS. Geometry and kind, quantized and canonicalised -
        /// no file, no layer, no nesting.
        ///
        /// This exists because folding the DWG's file hash into every id was
        /// correct for one job and catastrophic for another. Re-issue a drawing
        /// with ONE wall moved and the file hash changes; with a single
        /// source-hashed id, every surviving entity gets a new name, so an
        /// incremental update sees the whole building deleted and rebuilt and an
        /// audit matches nothing. The identity that must survive a re-issue
        /// cannot contain the thing that changes on every re-issue.
        ///
        /// Canonical in three ways, each for a real redraw: direction (a wall
        /// drawn right-to-left is that wall), start vertex for a closed loop (a
        /// rectangle is the same rectangle whichever corner was clicked first),
        /// and winding.
        /// </summary>
        /// <summary>
        /// The identity of an ARC, which its endpoints alone cannot give.
        ///
        /// Two different arcs can share both ends - a minor and a major arc of the
        /// same chord, or two arcs of different radius - so an id taken over the
        /// endpoints collides between them, and an audit then matches an element
        /// to the wrong drawing entity. Radius and winding are what separate them.
        /// </summary>
        public static string ArcGeometryId(CadPoint centre, double radiusMm, CadPoint start, CadPoint end,
                                           bool clockwise, double tolerance)
        {
            var sb = new StringBuilder();
            sb.Append("arc").Append(Sep);
            sb.Append(Q(centre.X, tolerance)).Append(',').Append(Q(centre.Y, tolerance)).Append(Sep);
            sb.Append(Q(radiusMm, tolerance)).Append(Sep);
            sb.Append(Q(start.X, tolerance)).Append(',').Append(Q(start.Y, tolerance)).Append(Sep);
            sb.Append(Q(end.X, tolerance)).Append(',').Append(Q(end.Y, tolerance)).Append(Sep);
            sb.Append(clockwise ? "cw" : "ccw");
            return "cadgeo:" + Sha256Hex(sb.ToString()).Substring(0, 24);
        }

        private static string Q(double value, double tolerance)
        {
            double step = tolerance <= 0 ? 1.0 : tolerance;
            double snapped = Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
            if (snapped == 0) snapped = 0;   // never negative zero
            return snapped.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static string GeometryId(CadCurveKind kind, IList<CadPoint> points, double tolerance,
                                        bool closed = false)
        {
            var sb = new StringBuilder();
            sb.Append(kind).Append(Sep);
            sb.Append(tolerance.ToString("0.######", CultureInfo.InvariantCulture)).Append(Sep);
            foreach (string key in Canonical(points, tolerance, closed)) sb.Append(key).Append(';');
            return "cadgeo:" + Sha256Hex(sb.ToString()).Substring(0, 24);
        }

        /// <summary>
        /// WHAT IT IS, ON WHICH LAYER, IN WHICH BLOCK. The identity an
        /// incremental run matches on: it survives a re-issue of the file and
        /// still separates the same line drawn on two layers, which are two
        /// different statements about a building.
        ///
        /// A RENAMED layer changes this and does NOT change the geometry id, and
        /// that pair is exactly how a run tells "somebody moved this to another
        /// layer" from "somebody deleted it and drew a new one".
        /// </summary>
        public static string SemanticId(string layerPath, string instancePath, CadCurveKind kind,
                                        IList<CadPoint> points, double tolerance, bool closed = false)
        {
            var sb = new StringBuilder();
            sb.Append(layerPath ?? "(no-layer)").Append(Sep);
            sb.Append(instancePath ?? "(root)").Append(Sep);
            sb.Append(GeometryId(kind, points, tolerance, closed));
            return "cadsem:" + Sha256Hex(sb.ToString()).Substring(0, 24);
        }

        /// <summary>
        /// The semantic id of a thing whose geometry id is already known - an arc,
        /// whose identity its endpoints alone cannot give. Same shape as the
        /// points-based form: layer, instance path, then WHAT IT IS.
        /// </summary>
        public static string SemanticIdOf(string layerPath, string instancePath, string geometryId)
        {
            var sb = new StringBuilder();
            sb.Append(layerPath ?? "(no-layer)").Append(Sep);
            sb.Append(instancePath ?? "(root)").Append(Sep);
            sb.Append(geometryId ?? "(no-geometry)");
            return "cadsem:" + Sha256Hex(sb.ToString()).Substring(0, 24);
        }

        /// <summary>
        /// THAT ENTITY, IN THIS ISSUE OF THE FILE. What a provenance record
        /// cites to say which bytes it was built from, and what an audit compares
        /// to answer "is this element built from the drawing that is on disk
        /// now?". It changes on every re-issue, on purpose.
        /// </summary>
        public static string RevisionId(string sourceHash, string semanticId) =>
            "cadrev:" + Sha256Hex((sourceHash ?? "(no-source-hash)") + Sep + (semanticId ?? "(no-semantic)"))
                        .Substring(0, 24);

        /// <summary>
        /// The canonical key sequence for a point list: the lexicographically
        /// smallest reading among the ones that describe the SAME drawn thing.
        /// For an open curve that is forward or reversed; for a closed one it is
        /// every rotation of both windings, because a ring has no first corner.
        /// </summary>
        private static List<string> Canonical(IList<CadPoint> points, double tolerance, bool closed)
        {
            var keys = (points ?? new List<CadPoint>()).Select(p => p.Key(tolerance)).ToList();
            if (keys.Count == 0) return keys;
            if (!closed)
            {
                var reversed = Enumerable.Reverse(keys).ToList();
                return string.CompareOrdinal(string.Join(";", keys), string.Join(";", reversed)) <= 0
                    ? keys : reversed;
            }

            List<string> best = null;
            string bestJoined = null;
            foreach (List<string> ring in new[] { keys, Enumerable.Reverse(keys).ToList() })
                for (int start = 0; start < ring.Count; start++)
                {
                    var rotated = ring.Skip(start).Concat(ring.Take(start)).ToList();
                    string joined = string.Join(";", rotated);
                    if (bestJoined == null || string.CompareOrdinal(joined, bestJoined) < 0)
                    { bestJoined = joined; best = rotated; }
                }
            return best;
        }

        /// <summary>
        /// A fingerprint over a whole set of entities: what "this drawing, read
        /// this way" amounts to. Order-independent by construction, because
        /// enumeration order is Revit's business and not a change to the drawing.
        /// </summary>
        public static string SetFingerprint(IEnumerable<string> surrogates)
        {
            var sorted = (surrogates ?? Enumerable.Empty<string>()).Where(s => s != null).Distinct().ToList();
            sorted.Sort(StringComparer.Ordinal);
            return "cadset:" + Sha256Hex(string.Join("\n", sorted)).Substring(0, 32) + ":" + sorted.Count;
        }

        public static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")))
                                   .Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// Turning what a drawing hands over into segments that can be reasoned about.
    ///
    /// Arcs are chorded at a DECLARED sagitta, not at a fixed count: a 20 mm arc
    /// and a 20 m arc should not get the same number of chords, and the error a
    /// caller is accepting should be a number they chose rather than one this
    /// file picked.
    /// </summary>
    public static class CadCurves
    {
        /// <summary>
        /// Chord an arc so no chord departs from the true arc by more than
        /// <paramref name="maxSagittaMm"/>. Returns at least one chord; a
        /// degenerate radius or sweep gives a single straight chord.
        /// </summary>
        public static List<CadPoint> ChordArc(CadPoint centre, double radiusMm,
                                              double startAngleRad, double sweepRad,
                                              double maxSagittaMm)
        {
            var pts = new List<CadPoint>();
            if (radiusMm <= 0 || Math.Abs(sweepRad) < 1e-12 || maxSagittaMm <= 0)
            {
                pts.Add(PointOnArc(centre, radiusMm, startAngleRad));
                pts.Add(PointOnArc(centre, radiusMm, startAngleRad + sweepRad));
                return pts;
            }

            // sagitta = r(1 - cos(step/2))  ->  step = 2*acos(1 - s/r)
            double ratio = 1.0 - Math.Min(maxSagittaMm, radiusMm) / radiusMm;
            if (ratio < -1) ratio = -1;
            if (ratio > 1) ratio = 1;
            double step = 2.0 * Math.Acos(ratio);
            if (step <= 1e-9) step = Math.Abs(sweepRad);
            int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepRad) / step));
            if (count > 4096) count = 4096;      // a bound, stated, not a silent truncation
            double actual = sweepRad / count;
            for (int i = 0; i <= count; i++)
                pts.Add(PointOnArc(centre, radiusMm, startAngleRad + actual * i));
            return pts;
        }

        public static CadPoint PointOnArc(CadPoint centre, double radiusMm, double angleRad) =>
            new CadPoint(centre.X + radiusMm * Math.Cos(angleRad),
                         centre.Y + radiusMm * Math.Sin(angleRad),
                         centre.Z);

        /// <summary>An ordered point list becomes the segments between consecutive points.</summary>
        public static List<CadSegment> Explode(IList<CadPoint> points, string layer,
                                               CadCurveKind kind, bool closed = false)
        {
            var segs = new List<CadSegment>();
            if (points == null || points.Count < 2) return segs;
            for (int i = 0; i < points.Count - 1; i++)
                segs.Add(new CadSegment(points[i], points[i + 1], layer, kind, i));
            if (closed && points.Count > 2)
                segs.Add(new CadSegment(points[points.Count - 1], points[0], layer, kind, points.Count - 1));
            return segs;
        }

        /// <summary>
        /// Segments with no length are dropped, and HOW MANY were dropped is the
        /// caller's business - a drawing full of zero-length stubs is a fact
        /// about the drawing.
        /// </summary>
        public static List<CadSegment> DropDegenerate(IEnumerable<CadSegment> segments, double minLengthMm,
                                                      out int dropped)
        {
            var kept = new List<CadSegment>();
            dropped = 0;
            foreach (CadSegment s in segments ?? Enumerable.Empty<CadSegment>())
            {
                if (s == null) { dropped++; continue; }
                if (s.PlanLength < minLengthMm) { dropped++; continue; }
                kept.Add(s);
            }
            return kept;
        }

        /// <summary>
        /// Two segments are the SAME drawn thing when their endpoints coincide
        /// within tolerance, in either order. Returns the kept segments and, in
        /// <paramref name="duplicateGroups"/>, what was folded into what - a
        /// duplicate is evidence about the drawing, not litter to hide.
        /// </summary>
        public static List<CadSegment> Deduplicate(IEnumerable<CadSegment> segments, double toleranceMm,
                                                   out List<List<CadSegment>> duplicateGroups)
        {
            var kept = new List<CadSegment>();
            var groups = new Dictionary<string, List<CadSegment>>(StringComparer.Ordinal);
            foreach (CadSegment s in segments ?? Enumerable.Empty<CadSegment>())
            {
                if (s == null) continue;
                string ka = s.A.Key(toleranceMm), kb = s.B.Key(toleranceMm);
                string key = string.CompareOrdinal(ka, kb) <= 0 ? ka + "=>" + kb : kb + "=>" + ka;
                // The layer is part of identity: the same line on two layers is
                // two statements about the building, not one drawn twice.
                key = (s.Layer ?? "") + "" + key;
                List<CadSegment> bucket;
                if (!groups.TryGetValue(key, out bucket))
                {
                    groups[key] = bucket = new List<CadSegment>();
                    kept.Add(s);
                }
                bucket.Add(s);
            }
            duplicateGroups = groups.Values.Where(g => g.Count > 1).ToList();
            return kept;
        }
    }
}
