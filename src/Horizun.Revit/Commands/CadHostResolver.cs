// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH WALL DOES THIS BELONG IN?
//
// A drawing has no ids. A door is a symbol at a point, and Revit will not place
// one without a host, so somewhere the point has to become a specific wall.
//
// Two commands need that answer and they must give the same one. The first
// conversion asks it to place a door; the incremental update asks it to notice
// that a door which used to live in one wall now belongs in another. If those
// two disagreed - a different distance, a different allowance - an update would
// report a rehosting every time it ran, on a model nobody had touched.
//
// So the rule is here, once, and it is deliberately conservative: the nearest
// wall, within half its own thickness plus the tolerance the requirement set
// declares. A set that declares one millimetre does not thereby accept a door a
// metre from any wall.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>What the search found, including the case where it found nothing.</summary>
    internal sealed class CadHostMatch
    {
        /// <summary>The wall, or null when none was near enough - or none exists at all.</summary>
        public Wall Wall;
        /// <summary>How far the point is from that wall's centreline, in mm. Null when no wall exists.</summary>
        public double? DistanceMm;
        /// <summary>How far this set is willing to look, in mm.</summary>
        public double AllowanceMm;
        /// <summary>True when the document contains no wall at all - a different answer from "too far".</summary>
        public bool NoWallsAtAll;
    }

    /// <summary>Which slab a ring falls on, including the cases where nobody can say.</summary>
    internal sealed class CadSlabMatch
    {
        /// <summary>The one floor, roof or ceiling that covers the point - null when none does, or more than one does.</summary>
        public Element Slab;
        /// <summary>Every slab whose footprint covers the point. One entry is the answer; several is the refusal.</summary>
        public List<Element> Covering = new List<Element>();
        /// <summary>True when the document holds no floor, roof or ceiling at all - a different answer from "none covers this".</summary>
        public bool NoSlabsAtAll;
        /// <summary>True when several covered the point and the rule's level decided between them.</summary>
        public bool NarrowedByLevel;
        /// <summary>The storey the rule named, when it named one.</summary>
        public string DeclaredLevel;
        /// <summary>Slabs cover this point and NONE of them is on the storey the rule named.</summary>
        public bool CoveredButNotOnThatLevel;
    }

    internal static class CadHostResolver
    {
        /// <summary>Every wall with a location curve, read once for a whole pass.</summary>
        public static List<Wall> Walls(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Wall))
                .Cast<Wall>().Where(w => w.Location is LocationCurve).ToList();
        }

        /// <summary>
        /// The wall a point belongs in, or the reason none does.
        ///
        /// HALF THE WALL'S THICKNESS is where its centreline sits relative to its
        /// face, and a drawn symbol sits on a face as often as on the centre. The
        /// point tolerance is added on top, never instead.
        /// </summary>
        public static CadHostMatch Nearest(IList<Wall> walls, XYZ point, double pointToleranceMm)
        {
            var match = new CadHostMatch { AllowanceMm = pointToleranceMm };
            if (walls == null || walls.Count == 0 || point == null)
            {
                match.NoWallsAtAll = walls == null || walls.Count == 0;
                return match;
            }

            Wall best = null;
            double bestFeet = double.MaxValue;
            foreach (Wall w in walls)
            {
                Curve curve = (w.Location as LocationCurve)?.Curve;
                if (curve == null) continue;
                double d;
                try { d = curve.Distance(point); } catch { continue; }
                if (d >= bestFeet) continue;
                bestFeet = d; best = w;
            }

            if (best == null) { match.NoWallsAtAll = true; return match; }

            double widthMm = 0;
            try { widthMm = CadUnits.FeetToMm(best.Width); } catch { }
            match.AllowanceMm = pointToleranceMm + Math.Max(widthMm, 0) / 2.0 + 1.0;
            match.DistanceMm = CadUnits.FeetToMm(bestFeet);
            if (match.DistanceMm.Value <= match.AllowanceMm) match.Wall = best;
            return match;
        }

        /// <summary>Every floor, roof and ceiling, read once for a whole pass.</summary>
        public static List<Element> Slabs(Document doc)
        {
            var filters = new List<ElementFilter>
            {
                new ElementClassFilter(typeof(Floor)),
                new ElementClassFilter(typeof(RoofBase)),
                new ElementClassFilter(typeof(Ceiling))
            };
            return new FilteredElementCollector(doc)
                .WherePasses(new LogicalOrFilter(filters))
                .WhereElementIsNotElementType()
                .ToList();
        }

        /// <summary>
        /// THE SLAB A HOLE IS CUT IN.
        ///
        /// A drawing shows a ring and no ids, so somewhere the ring has to become
        /// one specific floor. NOT the nearest one, the way a door finds its wall:
        /// a hole belongs to the slab it is INSIDE, and a bounding box is not a
        /// footprint - an L-shaped floor's box covers the courtyard it does not
        /// have, and a hole cut there is cut in thin air. So the point is projected
        /// onto the slab's own horizontal faces, which is the only test that knows
        /// the difference.
        ///
        /// SEVERAL SLABS CAN COVER ONE POINT, because buildings have storeys. The
        /// rule's level decides between them, and when it does not, this REFUSES:
        /// a hole cut through the wrong floor is not visible in the plan the ring
        /// was drawn on.
        /// </summary>
        public static CadSlabMatch Containing(IList<Element> slabs, XYZ point, string levelName)
        {
            var match = new CadSlabMatch();
            if (slabs == null || slabs.Count == 0 || point == null)
            {
                match.NoSlabsAtAll = slabs == null || slabs.Count == 0;
                return match;
            }

            foreach (Element e in slabs)
                if (CoversPoint(e, point)) match.Covering.Add(e);

            if (match.Covering.Count == 0) return match;

            // THE DECLARED STOREY DECIDES FIRST, NOT LAST.
            //
            // This used to short-circuit on "exactly one slab covers the point"
            // and only consult the rule's level when two did. So a rule saying
            // level: 'Level 2', run against a model where only the Level 1 floor
            // had been converted, cut the hole through Level 1 and reported it
            // verified - the kind and the host id both agreed, and nothing
            // anywhere said the opening had landed on a storey nobody asked for.
            // A hole in the wrong floor is invisible in the plan it was drawn on.
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                var onLevel = match.Covering
                    .Where(e => string.Equals(LevelNameOf(e), levelName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                match.DeclaredLevel = levelName;
                if (onLevel.Count == 0)
                {
                    // Covered, but not by the storey that was named. That is a
                    // finding about the model or the rule and never a licence to
                    // cut the slab that happens to be there.
                    match.CoveredButNotOnThatLevel = true;
                    return match;
                }
                match.NarrowedByLevel = match.Covering.Count > onLevel.Count;
                match.Covering = onLevel;
            }

            if (match.Covering.Count == 1) { match.Slab = match.Covering[0]; return match; }
            return match;
        }

        /// <summary>
        /// Is this point over the slab itself, rather than merely over its
        /// bounding box?
        ///
        /// The test is a VERTICAL ray. It used to be a projection onto faces whose
        /// normal was exactly +/-Z, which excluded every slab that is not perfectly
        /// flat: a terrace with a 1% fall, a parking deck, and every pitched roof -
        /// on a command whose own documentation advertises "a hole through one
        /// floor, roof or ceiling". The refusal that followed blamed the drawing
        /// and said the building had no floor there, about a floor plainly under
        /// the ring, and no amount of redrawing could ever have fixed it.
        ///
        /// Vertical faces are still excluded, because a point beside a slab
        /// projects onto its edge happily. Everything else is asked the question
        /// that was always meant: is this point inside the outline, holes included.
        /// </summary>
        /// <summary>
        /// Whether this slab is under (or over) that point. Published because the
        /// shaft gate asks the same question about the floors it would cut, and
        /// two answers to "is this point on that slab" is one answer too many.
        /// </summary>
        public static bool Covers(Element slab, XYZ point) => CoversPoint(slab, point);

        private static bool CoversPoint(Element e, XYZ point)
        {
            try
            {
                var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Medium };
                GeometryElement geometry = e.get_Geometry(options);
                if (geometry == null) return false;
                return CoversPoint(geometry, point);
            }
            catch { return false; }
        }

        private static bool CoversPoint(GeometryElement geometry, XYZ point)
        {
            foreach (GeometryObject go in geometry)
            {
                var instance = go as GeometryInstance;
                if (instance != null)
                {
                    GeometryElement inner = null;
                    try { inner = instance.GetInstanceGeometry(); } catch { }
                    if (inner != null && CoversPoint(inner, point)) return true;
                    continue;
                }

                var solid = go as Solid;
                if (solid == null || solid.Faces.Size == 0) continue;
                foreach (Face face in solid.Faces)
                {
                    var planar = face as PlanarFace;
                    if (planar == null) continue;
                    // NOT VERTICAL, rather than exactly horizontal. A slab's edges
                    // are vertical and a point beside the floor projects onto one
                    // of them; a slab with a fall on it is still a slab.
                    if (Math.Abs(planar.FaceNormal.Z) < 1e-6) continue;
                    try
                    {
                        // WHERE THE VERTICAL THROUGH THIS POINT MEETS THE FACE'S
                        // PLANE. Projecting a point that is not already on that
                        // plane finds the nearest point on the face, which for a
                        // sloped face is not the one below the ring.
                        XYZ n = planar.FaceNormal;
                        if (Math.Abs(n.Z) < 1e-9) continue;
                        double z = planar.Origin.Z -
                                   ((point.X - planar.Origin.X) * n.X + (point.Y - planar.Origin.Y) * n.Y) / n.Z;
                        IntersectionResult hit = face.Project(new XYZ(point.X, point.Y, z));
                        if (hit != null) return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        private static string LevelNameOf(Element e)
        {
            try { return (e.Document.GetElement(e.LevelId) as Level)?.Name; }
            catch { return null; }
        }

        /// <summary>Millimetres into Revit's own decimal feet, for a point a plan states in mm.</summary>
        public static XYZ PointFromMm(double x, double y, double z) =>
            new XYZ(x / 304.8, y / 304.8, z / 304.8);
    }
}
