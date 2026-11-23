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

        /// <summary>
        /// An edge of a CLOSED outline (a polyline that returns to its own vertex), whichever reader made it:
        /// the harvest names the ring ring:N, an IR names it by its entity. A rule reading lines as runs
        /// leaves these - and the ring's chords - unclaimed unless it declares include_closed_polylines.
        /// </summary>
        public bool ClosedRing { get; }

        public CadSegment(CadPoint a, CadPoint b, string layer = null,
                          CadCurveKind sourceKind = CadCurveKind.Line, int sourceIndex = 0,
                          string sourceCurveId = null, bool closedRing = false)
        {
            A = a; B = b; Layer = layer; SourceKind = sourceKind; SourceIndex = sourceIndex;
            SourceCurveId = sourceCurveId;
            ClosedRing = closedRing;
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
}
