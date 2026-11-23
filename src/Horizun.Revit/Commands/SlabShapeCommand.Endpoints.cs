using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class SlabShapeCommand
    {
        private const double Tol = ArchitecturalEditRules.PositionToleranceFeet;

        internal static ArchitecturalEditRules.SlabEndpoint LocateSplitEndpoint(
            IEnumerable<Face> faces, XYZ point, double top, out XYZ surface)
        {
            XYZ boundary = null;
            foreach (Face face in faces)
            {
                double z = top;
                if (face is PlanarFace plane && Math.Abs(plane.FaceNormal.Z) > 1e-9)
                    z = plane.Origin.Z - (plane.FaceNormal.X * (point.X - plane.Origin.X) +
                        plane.FaceNormal.Y * (point.Y - plane.Origin.Y)) / plane.FaceNormal.Z;
                XYZ candidate = new XYZ(point.X, point.Y, z);
                // Face.Project can return null on a trim. Check the edges before accepting AddPoint.
                XYZ edgePoint = face.GetEdgesAsCurveLoops().SelectMany(loop => loop)
                    .SelectMany(edge => EdgePoints(edge, candidate))
                    .Where(p => p != null && Flat(p).DistanceTo(point) <= Tol)
                    .OrderBy(p => p.DistanceTo(candidate)).FirstOrDefault();
                if (edgePoint != null)
                {
                    boundary = edgePoint;
                    continue;
                }
                XYZ onFace = face.Project(candidate)?.XYZPoint;
                if (onFace != null && Flat(onFace).DistanceTo(point) <= Tol)
                {
                    surface = onFace;
                    return ArchitecturalEditRules.SlabEndpoint.Interior;
                }
            }
            surface = boundary;
            return boundary == null ? ArchitecturalEditRules.SlabEndpoint.Outside : ArchitecturalEditRules.SlabEndpoint.Boundary;
        }

        private static IEnumerable<XYZ> EdgePoints(Curve edge, XYZ candidate)
        {
            XYZ projected;
            try { projected = edge.Project(candidate)?.XYZPoint; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                when (edge is Arc arc && candidate.DistanceTo(arc.Center) <= Tol)
            {
                // Curve.Project has no unique answer at an arc's centre. Use a point
                // on that same arc and continue testing the face and remaining trims.
                projected = edge.IsBound ? edge.Evaluate(0.5, true) : edge.Evaluate(0, false);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                when (edge is Ellipse ellipse && TryFocusParameter(ellipse, candidate, out double parameter))
            {
                // At a focus the nearest point is the major-axis vertex on that
                // side, if the trim contains it. Otherwise the endpoints suffice.
                projected = ellipse.IsInside(parameter) ? ellipse.Evaluate(parameter, false) : null;
            }
            yield return projected;
            if (edge.IsBound)
            {
                yield return edge.GetEndPoint(0);
                yield return edge.GetEndPoint(1);
            }
        }

        private static bool TryFocusParameter(Ellipse ellipse, XYZ candidate, out double parameter)
        {
            bool majorIsX = ellipse.RadiusX >= ellipse.RadiusY;
            XYZ axis = majorIsX ? ellipse.XDirection : ellipse.YDirection;
            double major = Math.Max(ellipse.RadiusX, ellipse.RadiusY);
            double minor = Math.Min(ellipse.RadiusX, ellipse.RadiusY);
            double focusDistance = Math.Sqrt((major - minor) * (major + minor));
            XYZ focusOffset = axis.Multiply(focusDistance);
            parameter = majorIsX ? 0 : Math.PI / 2;
            if (candidate.DistanceTo(ellipse.Center + focusOffset) > Tol)
            {
                if (candidate.DistanceTo(ellipse.Center - focusOffset) > Tol) return false;
                parameter += Math.PI;
            }
            if (ellipse.IsBound)
            {
                double period = 2 * Math.PI;
                parameter += period * Math.Ceiling((ellipse.GetEndParameter(0) - parameter) / period);
            }
            return true;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);
    }
}
