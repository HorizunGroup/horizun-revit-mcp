// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// IS THE WALL A SYMBOL WAS HOSTED ON THE WALL IT IS DRAWN AGAINST?
//
// MEASURED: a receptacle drawn 60 mm from a wall that the wall reading did not
// convert - its face line had been paired with the chase next to it - was hosted
// on the nearest wall that DID exist, 220 mm away, inside the host search. The
// row was built, verified and matched by revision: every check this bridge had
// passed, on the wrong wall.
//
// The model alone cannot see it: the right wall is not in the model. The drawing
// can. When a rule declares which layers its hosts are drawn on, the symbol's
// nearest line on those layers must be one of its host's own faces - a line
// parallel to the host, within its band, alongside it. If some OTHER drawn wall
// is clearly nearer, the row is withdrawn and says which line and how far.
//
// "Clearly" is the tolerance: a device beside a corner is a few millimetres from
// the other wall's face too, and that is not a different host.
//
// A HOST'S OWN FACE STANDS AT ITS HALF WIDTH. MEASURED (unit 915F): a receptacle
// drawn against a 119.1 mm wall that was withdrawn for want of a type was hosted
// on the BACK face of the 101.6 mm wall behind it, 170 mm from where it is drawn.
// The check had counted every parallel line within half width + host search as the
// host's own face, so the drawn face of the missing wall, 220 mm off the host's
// centreline, passed as the host's. A face now stands within the half width plus
// the point tolerance plus the finish a reading may leave outside the pair.
//
// AND A SYMBOL IS AGAINST A LINE ONLY WHERE IT FACES IT. MEASURED (unit 914, 15969):
// with the narrower face band, a switch 168.6 mm off its host face was withdrawn
// because the corner of a neighbouring wall's step, which starts 91 mm beyond the
// symbol along the wall, was 133 mm away. A line whose extent the symbol does not
// project onto is a corner, not a wall the symbol is drawn against.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class CadHostPlausibilityResult
    {
        /// <summary>True when another drawn wall is clearly nearer than any face of the host.</summary>
        public bool NearerWallDrawn;
        /// <summary>The distance to the nearest host face line, or null when the host's faces are not drawn.</summary>
        public double? HostFaceMm;
        /// <summary>The distance to the nearest line on the host layers that is NOT one of the host's faces.</summary>
        public double? OtherWallMm;
        public CadSegment OtherWallLine;
    }

    public static class CadHostPlausibility
    {
        /// <summary>
        /// How far a drawn face may stand outside the model wall's own face: the finish
        /// lines a reading leaves outside the pair it built (measured 8 to 17.5 mm).
        /// </summary>
        public const double FaceStandOffMm = 20.0;

        public static CadHostPlausibilityResult Check(CadPoint symbol, CadPoint hostA, CadPoint hostB,
                                                      double hostHalfWidthMm, IEnumerable<CadSegment> hostLayerLines,
                                                      double angleToleranceDegrees, double toleranceMm,
                                                      double hostSearchMm)
        {
            var result = new CadHostPlausibilityResult();
            double dx = hostB.X - hostA.X, dy = hostB.Y - hostA.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9 || hostLayerLines == null) return result;
            var u = new CadVector(dx / length, dy / length);

            double host = double.MaxValue, other = double.MaxValue;
            CadSegment otherLine = null;
            foreach (CadSegment s in hostLayerLines)
            {
                if (s == null) continue;
                double d = Distance(symbol, s.A, s.B);
                CadVector? dir = s.PlanDirection;
                bool isHostFace = false;
                if (dir != null && dir.Value.UndirectedAngleDegrees(u) <= angleToleranceDegrees)
                {
                    CadPoint mid = s.Midpoint;
                    double across = Math.Abs((mid.X - hostA.X) * -u.Y + (mid.Y - hostA.Y) * u.X);
                    double t0 = (s.A.X - hostA.X) * u.X + (s.A.Y - hostA.Y) * u.Y;
                    double t1 = (s.B.X - hostA.X) * u.X + (s.B.Y - hostA.Y) * u.Y;
                    double alongside = Math.Min(Math.Max(t0, t1), length) - Math.Max(Math.Min(t0, t1), 0);
                    isHostFace = across <= hostHalfWidthMm + toleranceMm + FaceStandOffMm &&
                                 alongside > -toleranceMm;
                }
                if (isHostFace) host = Math.Min(host, d);
                else if (d < other && Faces(symbol, s.A, s.B, toleranceMm)) { other = d; otherLine = s; }
            }

            if (host < double.MaxValue) result.HostFaceMm = host;
            if (other < double.MaxValue) { result.OtherWallMm = other; result.OtherWallLine = otherLine; }
            // No drawn face of the host is not evidence of a different host.
            result.NearerWallDrawn = result.HostFaceMm.HasValue && result.OtherWallMm.HasValue &&
                                     other + toleranceMm < host && other <= hostSearchMm;
            return result;
        }

        /// <summary>True when the symbol's foot on the line falls within the line, give or take the tolerance.</summary>
        private static bool Faces(CadPoint p, CadPoint a, CadPoint b, double toleranceMm)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9) return false;
            double along = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length;
            return along >= -toleranceMm && along <= length + toleranceMm;
        }

        private static double Distance(CadPoint p, CadPoint a, CadPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double l2 = dx * dx + dy * dy;
            double t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
            double x = a.X + t * dx, y = a.Y + t * dy;
            return Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
        }
    }
}
