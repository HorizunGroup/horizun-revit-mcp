// -----------------------------------------------------------------------------
// Horizun Revit MCP - layout geometry for planimetry, with no Revit in it.
//
// Every question the planimetry auditor asks about a SHEET is a question about
// axis-aligned rectangles: do two viewports overlap, by how much, how far apart
// are they, is this placement inside the sheet, does it clear the margin. Those
// are four lines of arithmetic each and every one of them has an edge case that
// decides whether a finding is real:
//
//   * TWO PLACEMENTS THAT TOUCH DO NOT OVERLAP. A viewport whose right edge is
//     the next one's left edge is a layout somebody chose. Reporting it as a
//     collision is how an auditor loses its reader. So an overlap must EXCEED an
//     explicit tolerance on BOTH axes before it is one, and the tolerance is a
//     named constant rather than a literal buried in a comparison.
//   * A BOX THAT COULD NOT BE READ IS NOT AN EMPTY BOX. A default-constructed
//     rectangle is (0,0)-(0,0), which sits inside every sheet and overlaps
//     nothing - the shape of a clean result. PlanBox therefore carries Valid,
//     and every predicate REFUSES to answer about an invalid box instead of
//     answering false. The caller turns that refusal into `unknown`.
//   * COORDINATES NEVER MIX. Everything here is in Revit's internal feet; the
//     display conversion happens once, at the edge, through Scale(). A number
//     that arrived in millimetres and was compared against a tolerance in feet
//     is a bug no test catches by looking at the sign.
//
// Pure, so the cases that matter - exact contact, contact within tolerance,
// containment, negative coordinates, a rotated placement's own extent - are
// ordinary unit tests and need no model anybody has to build first.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// An axis-aligned rectangle in ONE declared coordinate system, in internal feet.
    /// Valid=false means "this could not be read" and is contagious: no predicate
    /// below answers a question about an invalid box.
    /// </summary>
    public struct PlanBox
    {
        public bool Valid;
        public double MinX, MinY, MaxX, MaxY;

        public double Width { get { return MaxX - MinX; } }
        public double Height { get { return MaxY - MinY; } }
        public double CenterX { get { return (MinX + MaxX) / 2.0; } }
        public double CenterY { get { return (MinY + MaxY) / 2.0; } }
        public double Area { get { return Width * Height; } }

        public static readonly PlanBox Unreadable = new PlanBox { Valid = false };

        /// <summary>
        /// A box from two opposite corners in any order. Non-finite input is UNREADABLE
        /// rather than a rectangle with a NaN edge, which compares false against
        /// everything and would silently clear every check it is put through.
        /// </summary>
        public static PlanBox FromCorners(double x1, double y1, double x2, double y2)
        {
            if (!IsFinite(x1) || !IsFinite(y1) || !IsFinite(x2) || !IsFinite(y2))
                return Unreadable;
            return new PlanBox
            {
                Valid = true,
                MinX = Math.Min(x1, x2),
                MinY = Math.Min(y1, y2),
                MaxX = Math.Max(x1, x2),
                MaxY = Math.Max(y1, y2)
            };
        }

        private static bool IsFinite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
    }

    public static class PlanimetryGeometry
    {
        /// <summary>Internal feet per millimetre. One conversion table, used everywhere.</summary>
        public const double FeetPerMillimetre = 1.0 / 304.8;

        /// <summary>
        /// THE tolerance for "these edges are the same edge", in internal feet: 0.1 mm on
        /// paper. Chosen because sheet layout is paper geometry - 0.1 mm is finer than any
        /// plotter resolves and coarser than the float noise Revit returns for two
        /// viewports a human aligned. Named, published in every geometric finding, and
        /// never re-decided at a call site.
        /// </summary>
        public const double TouchToleranceFeet = 0.1 * FeetPerMillimetre;

        /// <summary>Internal feet -> the requested display unit. Unknown unit answers false.</summary>
        public static bool TryScaleFromFeet(string units, out double scale)
        {
            switch ((units ?? "").ToLowerInvariant())
            {
                case "mm": scale = 304.8; return true;
                case "m": scale = 0.3048; return true;
                case "feet": scale = 1.0; return true;
                default: scale = 0.0; return false;
            }
        }

        /// <summary>The display unit -> internal feet, for a length that arrived from a caller.</summary>
        public static bool TryScaleToFeet(string units, out double scale)
        {
            double fromFeet;
            if (!TryScaleFromFeet(units, out fromFeet)) { scale = 0.0; return false; }
            scale = 1.0 / fromFeet;
            return true;
        }

        /// <summary>
        /// A length in feet, expressed in the display unit and ROUNDED so two runs over an
        /// unchanged model emit the same bytes. Six decimals in millimetres is a nanometre:
        /// far below anything measurable, and far above the last bit of a double, which is
        /// where non-determinism lives.
        /// </summary>
        public static double Display(double feet, double scaleFromFeet)
        {
            return Math.Round(feet * scaleFromFeet, 6, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Do two boxes share AREA, beyond the tolerance, on both axes? Exact contact and
        /// contact within tolerance are deliberately NOT overlaps - see the file header.
        /// An unreadable box is never an overlap and never a clearance; ask Readable first.
        /// </summary>
        public static bool Overlaps(PlanBox a, PlanBox b, double toleranceFeet)
        {
            if (!a.Valid || !b.Valid) return false;
            return OverlapX(a, b) > toleranceFeet && OverlapY(a, b) > toleranceFeet;
        }

        /// <summary>Shared extent along X, 0 when they do not share any. Never negative.</summary>
        public static double OverlapX(PlanBox a, PlanBox b)
        {
            if (!a.Valid || !b.Valid) return 0.0;
            return Math.Max(0.0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        }

        /// <summary>Shared extent along Y, 0 when they do not share any. Never negative.</summary>
        public static double OverlapY(PlanBox a, PlanBox b)
        {
            if (!a.Valid || !b.Valid) return 0.0;
            return Math.Max(0.0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        }

        /// <summary>The shared AREA. Reported beside the two axis overlaps because a long
        /// thin intersection and a square one of the same area are different defects.</summary>
        public static double OverlapArea(PlanBox a, PlanBox b)
        {
            return OverlapX(a, b) * OverlapY(a, b);
        }

        /// <summary>
        /// The smallest distance between two rectangles: 0 when they touch or overlap,
        /// the axis distance when they are separated on one axis only, and the corner-to-
        /// corner distance when they are separated on both. That last case is why this is
        /// not max(dx, dy): two boxes diagonally apart are further apart than either axis
        /// gap alone says, and a minimum-gap rule that used the axis gap would pass a
        /// layout it should fail.
        /// </summary>
        public static double Separation(PlanBox a, PlanBox b)
        {
            if (!a.Valid || !b.Valid) return double.NaN;
            double dx = Math.Max(0.0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
            double dy = Math.Max(0.0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
            if (dx == 0.0 && dy == 0.0) return 0.0;
            if (dx == 0.0) return dy;
            if (dy == 0.0) return dx;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Is `inner` wholly inside `outer`, allowing the tolerance on every edge?</summary>
        public static bool Contains(PlanBox outer, PlanBox inner, double toleranceFeet)
        {
            if (!outer.Valid || !inner.Valid) return false;
            return inner.MinX >= outer.MinX - toleranceFeet &&
                   inner.MinY >= outer.MinY - toleranceFeet &&
                   inner.MaxX <= outer.MaxX + toleranceFeet &&
                   inner.MaxY <= outer.MaxY + toleranceFeet;
        }

        /// <summary>
        /// Do the two rectangles share NOTHING - not even an edge, beyond the tolerance?
        /// This is the predicate the "completely outside the sheet" finding stands on, and
        /// it is deliberately stricter than !Overlaps: a placement whose edge grazes the
        /// sheet is partly on it, and calling that "completely outside" would be false.
        /// </summary>
        public static bool Disjoint(PlanBox a, PlanBox b, double toleranceFeet)
        {
            if (!a.Valid || !b.Valid) return false;
            return a.MaxX < b.MinX - toleranceFeet || b.MaxX < a.MinX - toleranceFeet ||
                   a.MaxY < b.MinY - toleranceFeet || b.MaxY < a.MinY - toleranceFeet;
        }

        /// <summary>The box grown by a margin on every side. A negative margin shrinks it;
        /// shrinking past zero yields an EMPTY box at the centre rather than an inverted
        /// one, because an inverted rectangle contains nothing and overlaps nothing and
        /// would read as a clean result.</summary>
        public static PlanBox Expand(PlanBox box, double marginFeet)
        {
            if (!box.Valid) return PlanBox.Unreadable;
            double minX = box.MinX - marginFeet, maxX = box.MaxX + marginFeet;
            double minY = box.MinY - marginFeet, maxY = box.MaxY + marginFeet;
            if (minX > maxX) { minX = maxX = box.CenterX; }
            if (minY > maxY) { minY = maxY = box.CenterY; }
            return PlanBox.FromCorners(minX, minY, maxX, maxY);
        }

        /// <summary>
        /// The smallest box containing both. Used for the ONE fact a viewport's own box
        /// does not carry: the title label sits OUTSIDE the view box, so the extent a
        /// neighbouring placement actually collides with is the union of the two.
        /// Unreadable is contagious in one direction only - a readable box unioned with an
        /// unreadable one is UNREADABLE, because the result would understate the extent.
        /// </summary>
        public static PlanBox Union(PlanBox a, PlanBox b)
        {
            if (!a.Valid || !b.Valid) return PlanBox.Unreadable;
            return PlanBox.FromCorners(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY),
                                       Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));
        }

        /// <summary>
        /// Union where an unreadable operand is simply absent. For the case the rule above
        /// does not fit: a placement that has no label at all (a schedule) must still get
        /// its own box, and "there is no label" is not "the label could not be read".
        /// </summary>
        public static PlanBox UnionOptional(PlanBox required, PlanBox optional)
        {
            if (!required.Valid) return PlanBox.Unreadable;
            if (!optional.Valid) return required;
            return Union(required, optional);
        }

        /// <summary>
        /// A box as a JSON-ready array of four display-unit numbers, or null when the box
        /// could not be read. One renderer, so no caller invents a fifth way to say
        /// "no geometry".
        /// </summary>
        public static double[] ToDisplayArray(PlanBox box, double scaleFromFeet)
        {
            if (!box.Valid) return null;
            return new[]
            {
                Display(box.MinX, scaleFromFeet), Display(box.MinY, scaleFromFeet),
                Display(box.MaxX, scaleFromFeet), Display(box.MaxY, scaleFromFeet)
            };
        }

        /// <summary>A deterministic text form on the 0.1 mm grid, for signatures and cursors.</summary>
        public static string Signature(PlanBox box)
        {
            if (!box.Valid) return "unreadable";
            return string.Join(",", new[]
            {
                Quantize(box.MinX), Quantize(box.MinY), Quantize(box.MaxX), Quantize(box.MaxY)
            });
        }

        private static string Quantize(double feet)
        {
            double tenthsOfMm = feet * 304.8 * 10.0;
            long grid = (long)Math.Round(tenthsOfMm, MidpointRounding.AwayFromZero);
            return grid.ToString(CultureInfo.InvariantCulture);
        }
    }
}
