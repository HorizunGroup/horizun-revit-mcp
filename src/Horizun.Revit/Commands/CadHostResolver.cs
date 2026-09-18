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

        /// <summary>Walls that were nearer and could not carry the point on any bounded face.</summary>
        public int WallsPassedOver;

        /// <summary>True when walls exist, one was nearest, and none of them carries this point.</summary>
        public bool NoFaceCarriesThePoint;
        /// <summary>True when the document contains no wall at all - a different answer from "too far".</summary>
        public bool NoWallsAtAll;
    }

    /// <summary>A wall END that carries a point: which wall, which end, how far, and where it looks.</summary>
    internal sealed class CadEndMatch
    {
        public Wall Wall;
        public int End;
        public double DistanceMm;
        public XYZ EndPoint;
        public XYZ Outward;
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

            // NEAREST FIRST, BUT IT HAS TO CARRY THE POINT.
            //
            // A wall whose centreline is nearest can still be the wrong wall: the
            // symbol may lie past its end, where it projects onto the PLANE of a
            // face and onto no face at all. The writer tests exactly that when it
            // places, so the search tests it here - otherwise a plan is clean and
            // its apply is not, which is the split this whole binding exists to
            // prevent.
            var byDistance = new List<KeyValuePair<double, Wall>>();
            foreach (Wall w in walls)
            {
                Curve curve = (w.Location as LocationCurve)?.Curve;
                if (curve == null) continue;
                double d;
                // IN PLAN. MEASURED (campaign 4): a receptacle asked at 457 mm above its level
                // was 480 mm from a wall line it stood 150 mm from, and was withdrawn.
                try { d = curve.Distance(new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z)); } catch { continue; }
                byDistance.Add(new KeyValuePair<double, Wall>(d, w));
            }
            byDistance.Sort((x, y) => x.Key.CompareTo(y.Key));

            Wall best = null;
            double bestFeet = double.MaxValue;
            int passedOver = 0;
            foreach (KeyValuePair<double, Wall> candidate in byDistance)
            {
                if (!CarriesPoint(candidate.Value, point)) { passedOver++; continue; }
                best = candidate.Value;
                bestFeet = candidate.Key;
                break;
            }
            match.WallsPassedOver = passedOver;

            if (best == null)
            {
                // Nothing carries it. Report the nearest one anyway, so the refusal
                // can say how far the nearest wall was rather than only that none
                // qualified.
                if (byDistance.Count == 0) { match.NoWallsAtAll = true; return match; }
                match.DistanceMm = CadUnits.FeetToMm(byDistance[0].Key);
                match.NoFaceCarriesThePoint = true;
                return match;
            }

            double widthMm = 0;
            try { widthMm = CadUnits.FeetToMm(best.Width); } catch { }
            match.AllowanceMm = pointToleranceMm + Math.Max(widthMm, 0) / 2.0 + 1.0;
            match.DistanceMm = CadUnits.FeetToMm(bestFeet);
            if (match.DistanceMm.Value <= match.AllowanceMm) match.Wall = best;
            return match;
        }

        /// <summary>
        /// The nearest FREE wall end whose terminal face carries the point in plan, within the search.
        /// Asked only when a rule allows end faces and no side face carried the point; the same test
        /// the placement's end route makes (CreateElementsPlacement.EndFaces).
        /// </summary>
        public static CadEndMatch NearestEnd(IList<Wall> walls, XYZ point, double searchMm)
        {
            CadEndMatch best = null;
            if (walls == null || point == null) return null;
            foreach (Wall w in walls)
            {
                var lc = w.Location as LocationCurve;
                Curve curve = lc?.Curve;
                if (curve == null) continue;
                double z = curve.GetEndPoint(0).Z + 1.0;           // one foot up: inside any wall's height
                var probe = new XYZ(point.X, point.Y, z);
                for (int end = 0; end <= 1; end++)
                {
                    XYZ e = curve.GetEndPoint(end);
                    double plan = new XYZ(point.X - e.X, point.Y - e.Y, 0).GetLength() * 304.8;
                    double halfWidth = 0;
                    try { halfWidth = w.Width * 304.8 / 2.0; } catch { }
                    if (plan > searchMm + halfWidth) continue;
                    bool joined = false;
                    try
                    {
                        foreach (Element j in lc.get_ElementsAtJoin(end))
                            if (j != null && j.Id != w.Id) { joined = true; break; }
                    }
                    catch { }
                    if (joined) continue;
                    foreach (var f in CreateElementsPlacement.EndFaces(w).Where(x => x.Item3 == end))
                    {
                        IntersectionResult pr;
                        try { pr = f.Item1.Project(probe); } catch { continue; }
                        if (pr == null) continue;
                        bool inside;
                        try { inside = f.Item1.IsInside(pr.UVPoint); } catch { inside = false; }
                        double front = f.Item1.FaceNormal.DotProduct(probe - pr.XYZPoint) * 304.8;
                        double d = pr.Distance * 304.8;
                        if (!inside || front < -1.0 || d > searchMm) continue;
                        if (best == null || d < best.DistanceMm)
                            best = new CadEndMatch { Wall = w, End = end, DistanceMm = d, EndPoint = e,
                                                     Outward = f.Item1.FaceNormal };
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Does this wall have a bounded face the point lands on?
        ///
        /// The same test the placement makes, so the plan and the apply cannot
        /// disagree about which wall a symbol belongs to. A wall whose faces
        /// cannot be read answers NO: an unreadable face is not a face this
        /// bridge can place on.
        /// </summary>
        internal static bool CarriesPoint(Wall wall, XYZ point)
        {
            try
            {
                foreach (ShellLayerType shell in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
                {
                    IList<Reference> refs = HostObjectUtils.GetSideFaces(wall, shell);
                    if (refs == null) continue;
                    foreach (Reference r in refs)
                    {
                        var face = wall.Document.GetElement(r)?.GetGeometryObjectFromReference(r) as Face;
                        if (face == null) continue;
                        IntersectionResult projection = face.Project(point);
                        if (projection == null) continue;
                        if (face.IsInside(projection.UVPoint)) return true;
                    }
                }
            }
            catch { }
            return false;
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
