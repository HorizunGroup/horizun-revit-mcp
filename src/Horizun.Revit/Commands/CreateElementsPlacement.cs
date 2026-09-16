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
                                               double? facingRadians = null, double? sideDeadBandMm = null)
        {
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

        private static string Describe(Element e)
        {
            if (e == null) return "(no host)";
            string name;
            try { name = e.Name; } catch { name = "(unnamed)"; }
            return (e.Category?.Name ?? e.GetType().Name) + " " + Rid.Value(e.Id) + " '" + name + "'";
        }
    }
}
