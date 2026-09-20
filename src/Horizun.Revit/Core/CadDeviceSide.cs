// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// WHICH SIDE OF ITS WALL A DRAWN DEVICE IS ON.
//
// MEASURED on a second apartment of the electrical plan: three receptacles of
// forty-one were built on the wrong face of their wall - in the next room - and
// the audit agreed with all three. Each was drawn over the wall's hatch, so the
// point could not say which face; the placement then took the symbol's rotation
// as the way it faces, and the audit checked the same assumption. The drawing
// disproves it: against every symbol drawn clearly OUTSIDE a wall (where the point
// does decide), a receptacle block faces its +x only once its insertion's MIRROR
// is applied (26 of 26 with it, 15 of 26 without), and a switch or a data outlet
// faces its +y, not its +x at all.
//
// So the decision, shared by the plan, the placement and the audit:
//
//   OUTSIDE the wall's thickness   the side the point is drawn on.
//   INSIDE, facing DECLARED        the face the declared facing points out of,
//                                  when it points out of one (|cos| > 0.5).
//   INSIDE, otherwise              the side of the centreline the point is on,
//                                  when it is further from it than the dead band.
//   otherwise                      nobody can say, and nothing is built.
//
// The rotation alone is never read as a side.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Revit.Core
{
    public static class CadDeviceSide
    {
        public const string FromPoint = "the drawn point, outside the wall's thickness";
        public const string FromDeclaredFacing =
            "the facing the requirement set declares for this block, because the symbol is drawn inside the wall's thickness";
        public const string FromCentreline =
            "the side of the centreline the symbol is drawn on, because it is drawn inside the wall's thickness and " +
            "no declared facing points out of a face";

        /// <summary>
        /// The side along <paramref name="normal"/> (+1 or -1) the drawing puts a
        /// wall device on, or 0 when it does not say.
        /// </summary>
        /// <param name="offsetMm">The drawn point's signed distance from the wall's centreline, along the normal.</param>
        /// <param name="halfWidthMm">Half the wall's width; null when it is not known.</param>
        /// <param name="facing">The device's facing in plan, as declared for its block; null when undeclared.</param>
        /// <param name="normal">The wall's unit normal in plan.</param>
        /// <param name="deadBandMm">How far from the centreline a point must be drawn for that to count.</param>
        /// <param name="from">What decided it, in words; null when nothing did.</param>
        public static int Expected(double offsetMm, double? halfWidthMm, CadVector? facing, CadVector normal,
                                   double deadBandMm, out string from)
        {
            from = null;
            if (halfWidthMm.HasValue && Math.Abs(offsetMm) > halfWidthMm.Value)
            {
                from = FromPoint;
                return Math.Sign(offsetMm);
            }
            if (facing.HasValue)
            {
                double dot = facing.Value.X * normal.X + facing.Value.Y * normal.Y;
                if (Math.Abs(dot) > 0.5)
                {
                    from = FromDeclaredFacing;
                    return Math.Sign(dot);
                }
            }
            if (halfWidthMm.HasValue && Math.Abs(offsetMm) > Math.Max(deadBandMm, 0))
            {
                from = FromCentreline;
                return Math.Sign(offsetMm);
            }
            return 0;
        }

        /// <summary>
        /// A block's declared facing, carried into plan: the insertion's mirror
        /// first (a negative scale reverses that axis), then its rotation. Null
        /// when the rotation is unknown - a facing turned by an unknown angle is
        /// not a facing.
        /// </summary>
        public static CadVector? InPlan(CadVector local, double? rotationRadians, double? scaleX, double? scaleY)
        {
            if (!rotationRadians.HasValue) return null;
            double lx = local.X * (scaleX.HasValue && scaleX.Value < 0 ? -1 : 1);
            double ly = local.Y * (scaleY.HasValue && scaleY.Value < 0 ? -1 : 1);
            double c = Math.Cos(rotationRadians.Value), s = Math.Sin(rotationRadians.Value);
            return new CadVector(lx * c - ly * s, lx * s + ly * c);
        }
    }
}
