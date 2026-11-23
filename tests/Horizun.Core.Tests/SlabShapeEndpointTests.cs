using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SlabShapeEndpointTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Circular_floor_has_an_interior_split_endpoint_at_the_arc_centre(bool splitBoundary)
        {
            var arcs = new List<Arc> { new Arc(0, splitBoundary ? Math.PI : 2 * Math.PI, splitBoundary) };
            if (splitBoundary) arcs.Add(new Arc(Math.PI, 2 * Math.PI, true));
            var face = new PlanarFace(arcs);
            var centre = SlabShapeCommand.LocateSplitEndpoint(new[] { face }, new XYZ(0, 0, 0), 0, out XYZ surface);
            Assert.Equal(ArchitecturalEditRules.SlabEndpoint.Interior, centre);
            Assert.Equal(8, surface.Z);
            Assert.All(arcs, arc => Assert.Equal(1, arc.CentreProjectionFailures));

            var boundary = SlabShapeCommand.LocateSplitEndpoint(new[] { face }, new XYZ(0, 10, 0), 0, out surface);
            Assert.Equal(ArchitecturalEditRules.SlabEndpoint.Boundary, boundary);
            Assert.Equal(10, surface.Y, 8);

            var outside = SlabShapeCommand.LocateSplitEndpoint(new[] { face }, new XYZ(11, 0, 0), 0, out surface);
            Assert.Equal(ArchitecturalEditRules.SlabEndpoint.Outside, outside);
            Assert.Null(surface);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Elliptical_floor_has_interior_split_endpoints_at_both_foci(bool splitBoundary, bool majorIsY)
        {
            double rx = majorIsY ? 6 : 10, ry = majorIsY ? 10 : 6;
            var edges = new List<Ellipse> { new Ellipse(rx, ry, 2 * Math.PI, 3 * Math.PI, splitBoundary) };
            if (splitBoundary) edges.Add(new Ellipse(rx, ry, 3 * Math.PI, 4 * Math.PI, true));
            var face = new PlanarFace(edges, rx, ry);

            foreach (double signedFocus in new[] { 8.0, -8.0 })
            {
                var focus = majorIsY ? new XYZ(0, signedFocus, 0) : new XYZ(signedFocus, 0, 0);
                Assert.Equal(ArchitecturalEditRules.SlabEndpoint.Interior,
                    SlabShapeCommand.LocateSplitEndpoint(new[] { face }, focus, 0, out XYZ onFace));
                Assert.Equal(0, onFace.DistanceTo(new XYZ(focus.X, focus.Y, 8)), 8);
            }
            Assert.All(edges, ellipse => Assert.Equal(2, ellipse.FocusProjectionFailures));

            var boundary = new XYZ(0, ry, 0);
            Assert.Equal(ArchitecturalEditRules.SlabEndpoint.Boundary,
                SlabShapeCommand.LocateSplitEndpoint(new[] { face }, boundary, 0, out XYZ onBoundary));
            Assert.Equal(0, onBoundary.DistanceTo(new XYZ(0, ry, 8)), 8);
        }

        [Fact]
        public void Ellipse_focus_within_boundary_tolerance_uses_the_vertex_inside_the_trim()
        {
            const double major = 10, minor = 0.15;
            var trim = new Ellipse(major, minor, 1.5 * Math.PI, 2.5 * Math.PI, true);
            var opposite = new Ellipse(major, minor, 2.5 * Math.PI, 3.5 * Math.PI, true);
            var face = new PlanarFace(new[] { trim, opposite }, major, minor);
            var point = new XYZ(Math.Sqrt(major * major - minor * minor), 0, 0);
            var onPlane = new XYZ(point.X, point.Y, face.Origin.Z);
            var vertex = new XYZ(major, 0, face.Origin.Z);
            double tolerance = ArchitecturalEditRules.PositionToleranceFeet;
            Assert.InRange(vertex.DistanceTo(onPlane), 0.1 / 304.8, tolerance);
            Assert.True(trim.GetEndPoint(0).DistanceTo(onPlane) > 1);
            Assert.True(trim.GetEndPoint(1).DistanceTo(onPlane) > 1);

            Assert.Equal(ArchitecturalEditRules.SlabEndpoint.Boundary,
                SlabShapeCommand.LocateSplitEndpoint(new[] { face }, point, 0, out XYZ surface));
            Assert.Equal(0, vertex.DistanceTo(surface), 8);
            Assert.Equal(1, trim.FocusProjectionFailures);
        }
    }
}

namespace Autodesk.Revit.Exceptions
{
    public class InvalidOperationException : System.InvalidOperationException
    {
        public InvalidOperationException() { }
        public InvalidOperationException(string message) : base(message) { }
    }
}

namespace Autodesk.Revit.DB
{
    public class IntersectionResult
    {
        public XYZ XYZPoint { get; set; }
        public double Distance => throw new NotSupportedException();
        public UV UVPoint => throw new NotSupportedException();
    }


    public class PlanarFace : Face
    {
        private readonly CurveLoop _boundary = new CurveLoop();
        private readonly double _radiusX, _radiusY;
        public PlanarFace(IEnumerable<Curve> edges, double radiusX = 10, double radiusY = 10, double z = 8)
        {
            _boundary.AddRange(edges);
            _radiusX = radiusX;
            _radiusY = radiusY;
            Origin = new XYZ(0, 0, z);
        }
        public XYZ Origin { get; }
        public XYZ FaceNormal => new XYZ(0, 0, 1);
        public override IList<CurveLoop> GetEdgesAsCurveLoops() => new[] { _boundary };
        public override IntersectionResult Project(XYZ point) =>
            Math.Pow(point.X / _radiusX, 2) + Math.Pow(point.Y / _radiusY, 2) < 1
                ? new IntersectionResult { XYZPoint = point } : null;
    }

    public class Arc : Curve
    {
        private readonly double _start, _end;
        private readonly Func<double, XYZ> _normalizedPath;
        public Arc(Func<double, XYZ> normalizedPath, XYZ center, double sweep)
        {
            _normalizedPath = normalizedPath; IsBound = true;
            Center = center; _end = sweep;
            A = normalizedPath(0); B = normalizedPath(1);
        }

        public Arc(double start, double end, bool bounded)
        {
            _start = start; _end = end; IsBound = bounded; Center = new XYZ(0, 0, 8);
            A = Evaluate(start, false); B = Evaluate(end, false);
        }
        public override bool IsBound { get; }
        public XYZ Center { get; }
        public double Radius => A.DistanceTo(Center);
        public double Length => Radius * Math.Abs(_end - _start);
        public int CentreProjectionFailures { get; private set; }
        public override IntersectionResult Project(XYZ point)
        {
            if (point.DistanceTo(Center) == 0)
            {
                CentreProjectionFailures++;
                throw new Autodesk.Revit.Exceptions.InvalidOperationException();
            }
            double angle = Math.Atan2(point.Y, point.X);
            if (angle < 0) angle += 2 * Math.PI;
            if (IsBound && (angle < _start || angle > _end)) return null;
            return new IntersectionResult { XYZPoint = Evaluate(angle, false) };
        }
        public override XYZ Evaluate(double parameter, bool normalized)
        {
            if (_normalizedPath != null) return _normalizedPath(parameter);
            double angle = normalized ? _start + parameter * (_end - _start) : parameter;
            return new XYZ(10 * Math.Cos(angle), 10 * Math.Sin(angle), Center.Z);
        }
    }

    public class Ellipse : Curve
    {
        private readonly double _start, _end;
        public double RadiusX { get; }
        public double RadiusY { get; }
        public XYZ Center => new XYZ(0, 0, 8);
        public XYZ XDirection => new XYZ(1, 0, 0);
        public XYZ YDirection => new XYZ(0, 1, 0);
        public override bool IsBound { get; }
        public int FocusProjectionFailures { get; private set; }

        public Ellipse(double radiusX, double radiusY, double start, double end, bool bounded)
        {
            RadiusX = radiusX;
            RadiusY = radiusY;
            _start = start;
            _end = end;
            IsBound = bounded;
            A = Evaluate(start, false);
            B = Evaluate(end, false);
        }

        public double GetEndParameter(int index) => index == 0 ? _start : _end;
        public bool IsInside(double parameter) => !IsBound || (parameter >= _start && parameter <= _end);

        public override IntersectionResult Project(XYZ point)
        {
            bool majorIsX = RadiusX >= RadiusY;
            double focus = Math.Sqrt(Math.Abs(RadiusX * RadiusX - RadiusY * RadiusY));
            if ((majorIsX && Math.Abs(point.X) == focus && point.Y == 0) ||
                (!majorIsX && Math.Abs(point.Y) == focus && point.X == 0))
            {
                FocusProjectionFailures++;
                throw new Autodesk.Revit.Exceptions.InvalidOperationException();
            }
            // The remaining test inputs are already on the ellipse.
            double angle = Math.Atan2(point.Y / RadiusY, point.X / RadiusX);
            double period = 2 * Math.PI;
            angle += period * Math.Ceiling((_start - angle) / period);
            return IsInside(angle) ? new IntersectionResult { XYZPoint = Evaluate(angle, false) } : null;
        }

        public override XYZ Evaluate(double parameter, bool normalized)
        {
            double angle = normalized ? _start + parameter * (_end - _start) : parameter;
            Assert.True(IsInside(angle));
            return new XYZ(RadiusX * Math.Cos(angle), RadiusY * Math.Sin(angle), Center.Z);
        }
    }
}
