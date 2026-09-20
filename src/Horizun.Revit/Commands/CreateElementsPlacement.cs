// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// ONE OVERLOAD DOES NOT PLACE EVERY FAMILY.
//
// MEASURED, on Revit 2026.4 and the imperial electrical template:
//
//   Duplex Receptacle / GFCI          WorkPlaneBased   work_plane_based = 1
//   Lighting Switches                 WorkPlaneBased   work_plane_based = 1
//   Data Outlet                       WorkPlaneBased   work_plane_based = 1
//   Panelboard 208V MLO               WorkPlaneBased   work_plane_based = 1
//
// Every one of them was being sent through
// NewFamilyInstance(point, symbol, wall, level, structuralType), which Revit
// ACCEPTS and which returns an instance whose Host is null. It raises nothing.
// The postcondition caught it - the row asked for a host and the committed
// element had none - and twenty-one rows of a real conversion refused.
//
// A work-plane based family is placed on a FACE:
// NewFamilyInstance(Reference face, XYZ location, XYZ referenceDirection, symbol).
// That is what this file does, and everything hard about it is in the four
// questions it has to answer honestly:
//
//   WHICH FACE.  A wall has two sides and they are different walls to whoever
//                lives there. The side is chosen by where the symbol was DRAWN,
//                measured against the face's own outward normal - never by
//                taking whichever face came back first.
//   IS THE POINT ON IT.  A face is bounded. Face.Project gives a UV and
//                Face.IsInside says whether that UV is on the face rather than
//                on its infinite plane, and a point beyond the end of a wall
//                projects happily onto nothing.
//   WHICH WAY.   The reference direction must lie IN the face. The symbol's
//                rotation is a direction in plan; its component in the face
//                plane is the hand. When that component is degenerate - a symbol
//                pointing straight into the wall - the wall's own direction is
//                used and the reply SAYS SO.
//   HOW HIGH.    The drawing has no heights. The Z asked for is kept exactly:
//                the face route places at the point it is given, and a mounting
//                height is the requirement set's statement, not this file's.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>How a family must be placed, decided from the family itself.</summary>
    public enum CadPlacementRoute
    {
        /// <summary>OneLevelBased: a point and a level.</summary>
        Level,
        /// <summary>OneLevelBasedHosted: a point, a host element and a level.</summary>
        HostedOnElement,
        /// <summary>WorkPlaneBased: a reference to a FACE, a point on it and a direction in it.</summary>
        Face,
        /// <summary>Something this route does not cover, named rather than attempted.</summary>
        Unsupported
    }

    /// <summary>What the face search decided, and on what evidence.</summary>
    public sealed class CadFaceChoice
    {
        public Reference Face;
        public XYZ Point;
        public XYZ ReferenceDirection;
        public string Refusal;
        public JObject Evidence;
    }

    public static class CreateElementsPlacement
    {
        /// <summary>
        /// The route this family needs. Read from the family, never guessed from
        /// the category: a ceiling speaker and a floor box are both electrical
        /// fixtures and they are placed differently.
        /// </summary>
        public static CadPlacementRoute RouteFor(FamilySymbol symbol, out string placementType)
        {
            placementType = "(unreadable)";
            if (symbol?.Family == null) return CadPlacementRoute.Unsupported;
            FamilyPlacementType t = symbol.Family.FamilyPlacementType;
            placementType = t.ToString();
            switch (t)
            {
                case FamilyPlacementType.OneLevelBased: return CadPlacementRoute.Level;
                case FamilyPlacementType.OneLevelBasedHosted: return CadPlacementRoute.HostedOnElement;
                case FamilyPlacementType.WorkPlaneBased: return CadPlacementRoute.Face;
                default: return CadPlacementRoute.Unsupported;
            }
        }

        /// <summary>
        /// The face of this host that the symbol was drawn on, the point on it,
        /// and a direction lying in it.
        ///
        /// <paramref name="asked"/> is where the drawing put the symbol, in
        /// Revit's feet. <paramref name="rotationRadians"/> is the symbol's own
        /// rotation in plan, or null when the drawing did not declare one.
        /// </summary>
        public static CadFaceChoice ChooseFace(Document doc, Element host, XYZ asked,
                                               double? rotationRadians, double allowanceMm,
                                               double? facingRadians = null, double? sideDeadBandMm = null,
                                               string faceKind = null)
        {
            // THE END OF A WALL, ONLY WHEN IT IS ASKED FOR BY NAME. A symbol beyond a wall's end is
            // usually a symbol on the NEXT wall; taking the end face by default would host it on the
            // wrong one. So the terminal face is a separate, explicit route.
            if (string.Equals(faceKind, "end", StringComparison.Ordinal))
                return ChooseEndFace(doc, host, asked, rotationRadians, allowanceMm);
            // THE ROTATION IS NOT A SIDE when the caller says so (CadDeviceSide):
            // inside the wall, only a declared facing or the centreline decides.
            bool sideRule = sideDeadBandMm.HasValue;
            var choice = new CadFaceChoice();
            var evidence = new JObject();
            choice.Evidence = evidence;

            var hostObject = host as HostObject;
            if (hostObject == null)
            {
                choice.Refusal = "host_is_not_a_host_object: " + Describe(host) + " has no faces to place on. " +
                                 "A work-plane based family needs a wall, a floor, a ceiling or a roof.";
                return choice;
            }

            var candidates = new List<Tuple<string, Reference>>();
            foreach (ShellLayerType shell in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(hostObject, shell); }
                catch { continue; }
                if (refs == null) continue;
                foreach (Reference r in refs) candidates.Add(Tuple.Create(shell.ToString(), r));
            }
            // A floor or a ceiling answers on its top and bottom faces instead.
            if (candidates.Count == 0)
                foreach (bool top in new[] { true, false })
                {
                    IList<Reference> refs;
                    try
                    {
                        refs = top ? HostObjectUtils.GetTopFaces(hostObject)
                                   : HostObjectUtils.GetBottomFaces(hostObject);
                    }
                    catch { continue; }
                    if (refs == null) continue;
                    foreach (Reference r in refs) candidates.Add(Tuple.Create(top ? "Top" : "Bottom", r));
                }

            if (candidates.Count == 0)
            {
                choice.Refusal = "host_exposes_no_faces: Revit returned no side, top or bottom face for " +
                                 Describe(host) + ", so there is nothing to place a work-plane based family on.";
                return choice;
            }

            // WHICH SIDE THE SYMBOL IS ON. For each face: project the asked point
            // onto it, and measure whether the point sits on the OUTWARD side.
            // The winner is the face the symbol is actually in front of; ties (a
            // point exactly on the centreline) are refused rather than guessed.
            var rows = new JArray();
            double bestDistance = double.MaxValue;
            string bestSide = null;
            IntersectionResult bestProjection = null;
            Reference bestReference = null;

            // The fallback: faces that carry the point but have it BEHIND them,
            // which is a symbol drawn inside the wall.
            double bestAgreement = double.MinValue;
            string insideSide = null;
            IntersectionResult insideProjection = null;
            Reference insideReference = null;
            int carriesThePoint = 0;
            var behind = new List<Tuple<string, IntersectionResult, Reference, double>>();
            var behindNormals = new List<XYZ>();

            foreach (Tuple<string, Reference> candidate in candidates)
            {
                Face face;
                try { face = doc.GetElement(candidate.Item2)?.GetGeometryObjectFromReference(candidate.Item2) as Face; }
                catch { continue; }
                if (face == null) continue;

                IntersectionResult projection;
                try { projection = face.Project(asked); }
                catch { projection = null; }
                if (projection == null)
                {
                    rows.Add(new JObject
                    {
                        ["side"] = candidate.Item1,
                        ["rejected"] = "the point does not project onto this face at all"
                    });
                    continue;
                }

                bool inside;
                try { inside = face.IsInside(projection.UVPoint); }
                catch { inside = false; }

                XYZ normal = face.ComputeNormal(projection.UVPoint);
                double outward = normal.DotProduct(asked - projection.XYZPoint);
                double distanceMm = projection.Distance * 304.8;

                rows.Add(new JObject
                {
                    ["side"] = candidate.Item1,
                    ["distance_mm"] = Math.Round(distanceMm, 1),
                    ["point_is_within_the_face"] = inside,
                    ["symbol_is_in_front"] = outward >= -1e-9,
                    ["normal"] = new JArray(Math.Round(normal.X, 4), Math.Round(normal.Y, 4), Math.Round(normal.Z, 4))
                });

                // A face the point is genuinely ON, and in front of, wins outright.
                if (!inside) continue;
                carriesThePoint++;
                if (outward < -1e-9)
                {
                    // BEHIND the face: the symbol is drawn inside the wall's own
                    // thickness, which is where a draughtsman usually puts it.
                    // Kept as a fallback, decided by rotation below.
                    double? pointsAt = sideRule ? facingRadians : rotationRadians;
                    if (pointsAt.HasValue || sideRule)
                    {
                        double agreement = pointsAt.HasValue
                            ? normal.DotProduct(new XYZ(Math.Cos(pointsAt.Value), Math.Sin(pointsAt.Value), 0))
                            : 0.0;
                        behind.Add(Tuple.Create(candidate.Item1, projection, candidate.Item2, agreement));
                        behindNormals.Add(normal);
                        if (agreement > bestAgreement)
                        {
                            bestAgreement = agreement;
                            insideSide = candidate.Item1;
                            insideProjection = projection;
                            insideReference = candidate.Item2;
                        }
                    }
                    continue;
                }
                if (distanceMm < bestDistance)
                {
                    bestDistance = distanceMm;
                    bestSide = candidate.Item1;
                    bestProjection = projection;
                    bestReference = candidate.Item2;
                }
            }

            // THE SYMBOL WAS DRAWN ON THE WALL, NOT BESIDE IT.
            //
            // Nothing had it in front, and something had it within a face. Which
            // side the device belongs to is then a question the point cannot
            // answer and the symbol's own rotation can: a wall device faces the
            // room it serves.
            string chosenBy = "the point lies in front of this face";

            // A ROTATION ALONG THE WALL POINTS OUT OF NEITHER FACE. Then the side
            // of the centreline the symbol was drawn on decides - the nearer face -
            // and a symbol ON the centreline is refused rather than guessed.
            string alongTheWall = null;
            if (bestReference == null && insideReference != null && bestAgreement <= 0.5 && behind.Count > 0)
            {
                var byDistance = behind.OrderBy(b => b.Item2.Distance).ToList();
                double nearMm = byDistance[0].Item2.Distance * 304.8;
                double farMm = byDistance.Count > 1 ? byDistance[1].Item2.Distance * 304.8 : double.MaxValue;
                // A HORIZONTAL HOST WITH NO THICKNESS answers the same face as its top and its
                // bottom (MEASURED: a Basic Ceiling). A device under it cannot be told apart
                // from one on top of it, and no dead band is the reason.
                if (behindNormals.Count >= 2 && behindNormals.All(v => Math.Abs(v.Z) > 0.9) &&
                    behindNormals.All(v => v.DotProduct(behindNormals[0]) > 0.99) && farMm - nearMm < 1.0)
                {
                    evidence["faces_considered"] = rows;
                    choice.Refusal = "host_has_no_thickness: " + Describe(host) + " reports its top and its bottom as " +
                                     "one face, facing the same way, so a device asked below it cannot be placed on its " +
                                     "underside. Use a host with a thickness (a compound ceiling or a floor).";
                    return choice;
                }
                if (sideRule && (farMm - nearMm) / 2.0 <= sideDeadBandMm.Value)
                {
                    evidence["faces_considered"] = rows;
                    choice.Refusal = "no_side_could_be_chosen: the symbol was drawn inside the thickness of " + Describe(host) +
                                     ", " + ((farMm - nearMm) / 2.0).ToString("0.#", CultureInfo.InvariantCulture) +
                                     " mm from its centreline - no further than the " +
                                     sideDeadBandMm.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm this set " +
                                     "treats as no evidence - and no declared facing points out of either face. A guess " +
                                     "here puts the device in the next room.";
                    return choice;
                }
                if (farMm - nearMm < 1.0)
                {
                    evidence["faces_considered"] = rows;
                    choice.Refusal = "no_side_could_be_chosen: the symbol was drawn on the centreline of " + Describe(host) +
                                     " and points along it, so neither its position nor its rotation says which side " +
                                     "the device is on. A guess here puts a switch in the next room.";
                    return choice;
                }
                insideSide = byDistance[0].Item1;
                insideProjection = byDistance[0].Item2;
                insideReference = byDistance[0].Item3;
                alongTheWall = (sideRule
                                   ? "the symbol was drawn INSIDE the wall's thickness and no declared facing points out of " +
                                     "a face, so the side was chosen by the side of the centreline it was drawn on: "
                                   : "the symbol was drawn INSIDE the wall's thickness and points ALONG the wall, so the side " +
                                     "was chosen by the side of the centreline it was drawn on: ") +
                               nearMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm from this face and " +
                               (farMm == double.MaxValue ? "no other face" : farMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm from the other");
            }
            if (bestReference == null && insideReference != null)
            {
                bestReference = insideReference;
                bestProjection = insideProjection;
                bestSide = insideSide;
                bestDistance = insideProjection.Distance * 304.8;
                chosenBy = alongTheWall ??
                           ("the symbol was drawn INSIDE the wall's thickness, so the side was chosen by the " +
                            (sideRule ? "facing declared for its block" : "direction the symbol points") + " (agreement " +
                            bestAgreement.ToString("0.###", CultureInfo.InvariantCulture) + " with this face's " +
                            "outward normal)");
            }
            evidence["side_chosen_by"] = chosenBy;

            evidence["faces_considered"] = rows;

            if (bestReference == null)
            {
                choice.Refusal = carriesThePoint > 0
                    ? "no_side_could_be_chosen: the symbol sits within a face of " + Describe(host) +
                      " and behind every one of them - it was drawn inside the wall's own thickness - and the " +
                      "drawing declares no rotation, so nothing says which side the device is on. A guess here " +
                      "puts a receptacle in the next room."
                    : "no_face_carries_this_point: the symbol does not sit on any bounded face of " +
                      Describe(host) + ". It projects onto the plane of a face and beyond its edge, which is a " +
                      "symbol drawn past the end of the wall it belongs to.";
                return choice;
            }
            if (bestDistance > allowanceMm)
            {
                choice.Refusal = "face_too_far: the nearest face of " + Describe(host) + " carrying this point is " +
                                 bestDistance.ToString("0.#", CultureInfo.InvariantCulture) + " mm away and " +
                                 allowanceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm is the most " +
                                 "this set allows.";
                return choice;
            }

            evidence["chosen_side"] = bestSide;
            evidence["distance_mm"] = Math.Round(bestDistance, 1);
            evidence["placed_at_mm"] = new JArray(Math.Round(bestProjection.XYZPoint.X * 304.8, 1),
                                                  Math.Round(bestProjection.XYZPoint.Y * 304.8, 1),
                                                  Math.Round(bestProjection.XYZPoint.Z * 304.8, 1));

            // THE DIRECTION, WHICH MUST LIE IN THE FACE.
            Face chosen = doc.GetElement(bestReference).GetGeometryObjectFromReference(bestReference) as Face;
            XYZ faceNormal = chosen.ComputeNormal(bestProjection.UVPoint);
            XYZ wanted = rotationRadians.HasValue
                ? new XYZ(Math.Cos(rotationRadians.Value), Math.Sin(rotationRadians.Value), 0)
                : XYZ.BasisX;

            XYZ inPlane = wanted - faceNormal.Multiply(wanted.DotProduct(faceNormal));
            string directionFrom = "the symbol's own rotation, projected into the face";
            if (inPlane.GetLength() < 1e-6)
            {
                // A symbol pointing straight into the wall says nothing about its
                // hand. Use a direction along the face and say that is what happened.
                XYZ alternative = faceNormal.CrossProduct(XYZ.BasisZ);
                if (alternative.GetLength() < 1e-6) alternative = faceNormal.CrossProduct(XYZ.BasisX);
                inPlane = alternative;
                directionFrom = "the face itself: the symbol's rotation points along the face normal, which " +
                                "says nothing about which way round the device goes";
            }
            evidence["reference_direction_from"] = directionFrom;
            evidence["reference_direction"] = new JArray(Math.Round(inPlane.Normalize().X, 4),
                                                         Math.Round(inPlane.Normalize().Y, 4),
                                                         Math.Round(inPlane.Normalize().Z, 4));

            choice.Face = bestReference;
            choice.Point = bestProjection.XYZPoint;
            choice.ReferenceDirection = inPlane.Normalize();
            return choice;
        }

        /// <summary>
        /// The terminal faces of a wall - planar, vertical, their normal along the wall's own
        /// direction - read from its geometry with references, never by an index into the faces.
        /// Item3 is the end they close: 0 at the location curve's start, 1 at its end.
        /// </summary>
        public static List<Tuple<PlanarFace, Reference, int>> EndFaces(Wall wall)
        {
            var found = new List<Tuple<PlanarFace, Reference, int>>();
            Curve curve = (wall?.Location as LocationCurve)?.Curve;
            if (curve == null) return found;
            var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false };
            GeometryElement geometry;
            try { geometry = wall.get_Geometry(options); } catch { return found; }
            if (geometry == null) return found;
            XYZ start = curve.GetEndPoint(0), end = curve.GetEndPoint(1);
            XYZ tangent0 = curve.ComputeDerivatives(0, true).BasisX.Normalize();
            XYZ tangent1 = curve.ComputeDerivatives(1, true).BasisX.Normalize();
            foreach (GeometryObject g in geometry)
            {
                var solid = g as Solid;
                if (solid == null || solid.Faces.Size == 0) continue;
                foreach (Face f in solid.Faces)
                {
                    var pf = f as PlanarFace;
                    if (pf == null || pf.Reference == null) continue;
                    XYZ n = pf.FaceNormal;
                    if (Math.Abs(n.Z) > 0.01) continue;
                    // a face closing the start looks backwards along the start tangent and lies at the start
                    if (n.DotProduct(tangent0) < -0.99 && Math.Abs((pf.Origin - start).DotProduct(tangent0)) < 0.05)
                        found.Add(Tuple.Create(pf, pf.Reference, 0));
                    else if (n.DotProduct(tangent1) > 0.99 && Math.Abs((pf.Origin - end).DotProduct(tangent1)) < 0.05)
                        found.Add(Tuple.Create(pf, pf.Reference, 1));
                }
            }
            return found;
        }

        /// <summary>What kind of face of its host a reference names: side, end, top, bottom, or why not.</summary>
        public static string FaceKind(Document doc, Element host, Reference face)
        {
            if (face == null) return "none";
            Face f;
            try { f = doc.GetElement(face)?.GetGeometryObjectFromReference(face) as Face; }
            catch { return "unresolved"; }
            if (f == null) return "unresolved";
            var pf = f as PlanarFace;
            if (pf == null) return "curved";
            if (pf.FaceNormal.Z > 0.99) return "top";
            if (pf.FaceNormal.Z < -0.99) return "bottom";
            var wall = host as Wall;
            Curve curve = (wall?.Location as LocationCurve)?.Curve;
            if (wall != null && curve != null)
            {
                // A FACE ALONG THE WALL'S OWN DIRECTION CLOSES AN END. MEASURED (campaign 5): after a
                // wall was joined to that end, the instance kept the SAME stable reference, which now
                // named the buried joint face; the device stayed where it was, inside the other wall.
                XYZ n = pf.FaceNormal;
                XYZ t0 = curve.ComputeDerivatives(0, true).BasisX.Normalize();
                XYZ t1 = curve.ComputeDerivatives(1, true).BasisX.Normalize();
                int end = n.DotProduct(t0) < -0.99 ? 0 : n.DotProduct(t1) > 0.99 ? 1 : -1;
                if (end < 0) return "side";
                bool joined = false;
                try
                {
                    foreach (Element e in ((LocationCurve)wall.Location).get_ElementsAtJoin(end))
                        if (e != null && e.Id != wall.Id) { joined = true; break; }
                }
                catch { }
                if (joined) return "end_joined";
                XYZ at = curve.GetEndPoint(end);
                return Math.Abs((pf.Origin - at).DotProduct(end == 0 ? t0 : t1)) < 0.05 ? "end" : "end_displaced";
            }
            return "vertical";
        }

        /// <summary>
        /// A device on the TERMINAL face of a wall. The face is found by geometry; the point must
        /// project inside it and stand in front of it; a joined end has no exposed face and is
        /// refused with the wall it is joined to.
        /// </summary>
        private static CadFaceChoice ChooseEndFace(Document doc, Element host, XYZ asked, double? rotationRadians,
                                                   double allowanceMm)
        {
            var choice = new CadFaceChoice();
            var evidence = new JObject { ["face_kind_asked"] = "end" };
            choice.Evidence = evidence;
            var wall = host as Wall;
            var lc = wall?.Location as LocationCurve;
            if (wall == null || lc == null)
            {
                choice.Refusal = "end_face_needs_a_wall: " + Describe(host) + " is not a wall with a location line, " +
                                 "so it has no terminal face to place on.";
                return choice;
            }
            // WHICH END: the one the point is nearer to along the wall.
            IntersectionResult onLine = null;
            try { onLine = lc.Curve.Project(asked); } catch { }
            double param = 0.5;
            try { if (onLine != null) param = lc.Curve.ComputeNormalizedParameter(onLine.Parameter); } catch { }
            int endIndex = param >= 0.5 ? 1 : 0;
            evidence["wall_end"] = endIndex;

            var joined = new List<long>();
            try
            {
                foreach (Element e in lc.get_ElementsAtJoin(endIndex))
                    if (e != null && e.Id != wall.Id) joined.Add(Rid.Value(e.Id));
            }
            catch { }
            if (joined.Count > 0)
            {
                evidence["joined_at_this_end"] = new JArray(joined);
                choice.Refusal = "wall_end_is_joined: end " + endIndex + " of " + Describe(wall) + " is joined to " +
                                 string.Join(", ", joined) + ", so it has no exposed terminal face - a device there " +
                                 "stands on the other wall. Place it on that wall's side face, or unjoin the end.";
                return choice;
            }

            var ends = EndFaces(wall).Where(e => e.Item3 == endIndex).ToList();
            var rows = new JArray();
            Tuple<PlanarFace, Reference, int> best = null;
            IntersectionResult bestProjection = null;
            double bestDistance = double.MaxValue;
            foreach (var e in ends)
            {
                IntersectionResult projection;
                try { projection = e.Item1.Project(asked); } catch { projection = null; }
                if (projection == null)
                {
                    rows.Add(new JObject { ["rejected"] = "the point does not project onto this end face" });
                    continue;
                }
                bool inside;
                try { inside = e.Item1.IsInside(projection.UVPoint); } catch { inside = false; }
                double outward = e.Item1.FaceNormal.DotProduct(asked - projection.XYZPoint) * 304.8;
                double distanceMm = projection.Distance * 304.8;
                rows.Add(new JObject
                {
                    ["distance_mm"] = Math.Round(distanceMm, 1),
                    ["point_is_within_the_face"] = inside,
                    ["in_front_mm"] = Math.Round(outward, 1),
                    ["stable_reference"] = e.Item2.ConvertToStableRepresentation(doc)
                });
                if (!inside || outward < -1.0) continue;
                if (distanceMm < bestDistance) { bestDistance = distanceMm; best = e; bestProjection = projection; }
            }
            evidence["end_faces_considered"] = rows;
            if (ends.Count == 0)
            {
                choice.Refusal = "no_end_face: " + Describe(wall) + " exposes no planar terminal face at end " + endIndex +
                                 " (a curved or cut end). Nothing was placed.";
                return choice;
            }
            if (best == null)
            {
                choice.Refusal = "no_face_carries_this_point: the point is not in front of, and within the outline " +
                                 "of, the terminal face at end " + endIndex + " of " + Describe(wall) + ". Nothing was placed.";
                return choice;
            }
            if (bestDistance > allowanceMm)
            {
                choice.Refusal = "face_too_far: the terminal face of " + Describe(wall) + " is " +
                                 bestDistance.ToString("0.#", CultureInfo.InvariantCulture) + " mm from the point and " +
                                 allowanceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm is the most allowed.";
                return choice;
            }

            XYZ normal = best.Item1.FaceNormal;
            XYZ wanted = rotationRadians.HasValue
                ? new XYZ(Math.Cos(rotationRadians.Value), Math.Sin(rotationRadians.Value), 0)
                : XYZ.BasisX;
            XYZ inPlane = wanted - normal.Multiply(wanted.DotProduct(normal));
            string directionFrom = "the symbol's own rotation, projected into the end face";
            if (inPlane.GetLength() < 1e-6)
            {
                inPlane = normal.CrossProduct(XYZ.BasisZ);
                directionFrom = "the end face itself: the symbol's rotation points along the face normal";
            }
            evidence["chosen_side"] = "end " + endIndex;
            evidence["side_chosen_by"] = "asked for by name (host_face 'end'); the end nearer the point along the wall";
            evidence["distance_mm"] = Math.Round(bestDistance, 1);
            evidence["placed_at_mm"] = new JArray(Math.Round(bestProjection.XYZPoint.X * 304.8, 1),
                                                  Math.Round(bestProjection.XYZPoint.Y * 304.8, 1),
                                                  Math.Round(bestProjection.XYZPoint.Z * 304.8, 1));
            evidence["face_normal"] = new JArray(Math.Round(normal.X, 4), Math.Round(normal.Y, 4), Math.Round(normal.Z, 4));
            evidence["reference_direction_from"] = directionFrom;
            choice.Face = best.Item2;
            choice.Point = bestProjection.XYZPoint;
            choice.ReferenceDirection = inPlane.Normalize();
            return choice;
        }

        private static string Describe(Element e)
        {
            if (e == null) return "(no host)";
            string name;
            try { name = e.Name; } catch { name = "(unnamed)"; }
            return (e.Category?.Name ?? e.GetType().Name) + " " + Rid.Value(e.Id) + " '" + name + "'";
        }
    }
}
