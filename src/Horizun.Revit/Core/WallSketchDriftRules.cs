// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A "STRANDED PROFILE" ("perfil varado" in the field's own words): moving a
// wall does not move its edited elevation profile (Wall.SketchId / Sketch).
// MEASURED 2026-09-25: a wall dragged to a new position kept its old sketch
// exactly where it was, and Revit reported the resulting geometry as an
// overlap or a duplicate against its neighbours - a warning whose true cause
// was never in the pair of elements it named.
//
// The wall's sketch curves sit ON a fixed SketchPlane; a wall move that stays
// IN that plane (along the wall's own run, or vertically) leaves the plane
// itself valid and only the curves' position within it is wrong - a
// translation fixes it. A move that carries the wall's location line OFF the
// plane (sideways, across its own face) makes the plane itself wrong, and no
// translation of the curves living on it can follow; that case is reported
// and refused, never guessed at.
//
// Revit-free: given the plane (as three plain vectors) and the wall's and the
// sketch's own points, in millimetres, this decides drift, correctability and
// the correction vector. The commands that read a Document supply the numbers.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    /// <summary>A point or vector in millimetres, model-space. No Revit type behind it.</summary>
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    }

    public sealed class WallSketchDriftResult
    {
        /// <summary>False when the sketch sits under the wall's current location line within tolerance.</summary>
        public bool Stranded;
        /// <summary>Distance from the wall's location line to the sketch's own plane, mm. 0 means the
        /// plane is still valid for the wall's current position.</summary>
        public double PerpendicularOffsetMm;
        /// <summary>How far the sketch's own footprint sits from the wall's location line WITHIN the
        /// plane, mm - the part a translation can fix.</summary>
        public double InPlaneOffsetMm;
        /// <summary>True when PerpendicularOffsetMm is within tolerance: the plane the curves live on
        /// is still the right plane for the wall's current position, so a translation can align them.
        /// False means the wall moved OFF its sketch's plane and no translation of the sketch's own
        /// curves can follow it - the profile needs to be redrawn, not corrected.</summary>
        public bool Correctable;
        /// <summary>The vector a correction would move the sketch's curves by, mm - lies IN the plane
        /// by construction (its perpendicular component is dropped, not just small).</summary>
        public Vec3 CorrectionVectorMm;
        public string Reason;
    }

    public static class WallSketchDriftRules
    {
        /// <summary>Below this, a wall's sketch and its location line are read as still aligned - the
        /// tolerance a modeller's own snap/rounding leaves behind, not drift.</summary>
        // CALIBRATED on a real architecture model (2026-09-26): 2 mm flagged 64 walls, every
        // one at exactly 12.5 mm - the gap between where a joined wall's location line ends and
        // where its profile was drawn, not a move. A wall that was really moved shifts tens of
        // millimetres or more; 30 mm keeps those and drops the join geometry.
        public const double DefaultToleranceMm = 30.0;

        public static readonly string StrandedMeans =
            "the wall was moved after its elevation profile was edited; moving a wall does not move its " +
            "Sketch. perpendicular_offset_mm measures whether the sketch's OWN plane still passes through " +
            "the wall's current location line - a translation can only correct the case where it does " +
            "(the wall moved along its own run or vertically); a wall moved sideways across its own face " +
            "carried its location line off the sketch's plane, and no translation of the sketch's curves " +
            "can follow it there.";

        /// <summary>
        /// planeOrigin/planeNormal describe the sketch's own SketchPlane (planeNormal need not be a
        /// literal unit vector; it is normalised here). wallLineStart/End are the wall's CURRENT
        /// LocationCurve endpoints. sketchFootprintMin/Max is the axis-aligned box of every point in
        /// the sketch's profile loops - its centre stands in for "where the profile currently is".
        /// All in millimetres.
        /// </summary>
        public static WallSketchDriftResult Evaluate(Vec3 planeOrigin, Vec3 planeNormal,
                                                      Vec3 wallLineStart, Vec3 wallLineEnd,
                                                      Vec3 sketchFootprintMin, Vec3 sketchFootprintMax,
                                                      double toleranceMm = DefaultToleranceMm)
        {
            var result = new WallSketchDriftResult();
            double len = planeNormal.Length;
            if (len < 1e-9)
            {
                result.Stranded = false;
                result.Correctable = false;
                result.Reason = "the sketch's plane normal could not be read; nothing is measured.";
                return result;
            }
            Vec3 n = planeNormal * (1.0 / len);

            double dStart = (wallLineStart - planeOrigin).Dot(n);
            double dEnd = (wallLineEnd - planeOrigin).Dot(n);
            // The larger of the two endpoint offsets: a wall that also rotated relative to the
            // plane has one endpoint further off than the other, and the worse one governs whether
            // a single translation could ever bring both back onto it.
            result.PerpendicularOffsetMm = Math.Max(Math.Abs(dStart), Math.Abs(dEnd));

            // ONLY the component ALONG THE WALL'S OWN RUN, never the vertical one. A profile
            // legitimately spans a different height range than the location line (that is the whole
            // point of editing it - following a roof, stepping down a stair) so comparing the full 3D
            // box centres would read "the wall is 1.5 m tall" as 1.5 m of drift. Sliding along the
            // wall's own direction is the case a translation fixes; the plane's own normal already
            // separates the "moved sideways" case above, so what is left to measure is displacement
            // along the run.
            Vec3 along = wallLineEnd - wallLineStart;
            double alongLen = along.Length;
            if (alongLen < 1e-6)
            {
                result.Stranded = false;
                result.Correctable = false;
                result.Reason = "the wall's location line has no length; nothing is measured.";
                return result;
            }
            along = along * (1.0 / alongLen);

            Vec3 wallMid = (wallLineStart + wallLineEnd) * 0.5;
            Vec3 sketchMid = (sketchFootprintMin + sketchFootprintMax) * 0.5;
            Vec3 rawDrift = wallMid - sketchMid;
            double alongComponent = rawDrift.Dot(along);
            Vec3 inPlaneDrift = along * alongComponent;
            result.InPlaneOffsetMm = Math.Abs(alongComponent);

            result.Stranded = result.PerpendicularOffsetMm > toleranceMm || result.InPlaneOffsetMm > toleranceMm;
            if (!result.Stranded)
            {
                result.Correctable = false;
                result.Reason = "the sketch sits under the wall's current location line within tolerance; not stranded.";
                return result;
            }

            result.Correctable = result.PerpendicularOffsetMm <= toleranceMm;
            if (!result.Correctable)
            {
                result.Reason = "the wall's location line no longer lies in the sketch's own plane " +
                                 "(perpendicular offset " + result.PerpendicularOffsetMm.ToString("0.#") + " mm): " +
                                 "the wall moved sideways across its own face. A translation of the sketch's " +
                                 "curves cannot follow it off their own plane; the profile needs to be redrawn.";
                result.CorrectionVectorMm = new Vec3(0, 0, 0);
                return result;
            }

            result.CorrectionVectorMm = inPlaneDrift;
            result.Reason = "the wall's location line still lies in the sketch's plane; translating the " +
                             "sketch's curves by " + result.InPlaneOffsetMm.ToString("0.#") +
                             " mm within that plane realigns them.";
            return result;
        }
    }
}
