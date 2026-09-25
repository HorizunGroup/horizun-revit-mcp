// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FACING HALF of stranded-profile detection: reads a wall, its
// Sketch and its SketchPlane, and hands the numbers (millimetres) to
// WallSketchDriftRules, which decides. Shared by horizun_audit_model
// (wall_sketch_drift finding, warnings root-cause) and horizun_transform_elements
// (realign_wall_sketch correction) so both read the SAME drift for the SAME
// wall - a correction that disagreed with the finding that proposed it would
// be its own defect.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    public static class WallSketchGeometry
    {
        private const double MmPerFoot = 304.8;

        /// <summary>Every wall with an edited profile, classified once.</summary>
        public static Dictionary<long, WallSketchDriftResult> ComputeStrandedWalls(Document doc, double toleranceMm)
        {
            var result = new Dictionary<long, WallSketchDriftResult>();
            foreach (Wall wall in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                ElementId sketchId;
                try { sketchId = wall.SketchId; } catch { continue; }
                if (sketchId == null || Rid.Value(sketchId) < 0) continue;
                WallSketchDriftResult r = Evaluate(doc, wall, sketchId, toleranceMm);
                if (r != null) result[Rid.Value(wall.Id)] = r;
            }
            return result;
        }

        /// <summary>The one wall's drift, or null when it cannot be measured (no sketch plane,
        /// no location curve, an empty profile).</summary>
        public static WallSketchDriftResult Evaluate(Document doc, Wall wall, ElementId sketchId, double toleranceMm)
        {
            var sketch = doc.GetElement(sketchId) as Sketch;
            if (sketch == null || sketch.SketchPlane == null) return null;
            Plane plane;
            try { plane = sketch.SketchPlane.GetPlane(); } catch { return null; }
            if (plane == null) return null;

            var lc = wall.Location as LocationCurve;
            if (lc == null || lc.Curve == null) return null;
            XYZ ws = lc.Curve.GetEndPoint(0), we = lc.Curve.GetEndPoint(1);

            double minU = double.MaxValue, minV = double.MaxValue, minW = double.MaxValue;
            double maxU = double.MinValue, maxV = double.MinValue, maxW = double.MinValue;
            int points = 0;
            if (sketch.Profile != null)
                foreach (CurveArray loop in sketch.Profile)
                    foreach (Curve c in loop.Cast<Curve>())
                        foreach (XYZ p in new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                        {
                            points++;
                            minU = Math.Min(minU, p.X); maxU = Math.Max(maxU, p.X);
                            minV = Math.Min(minV, p.Y); maxV = Math.Max(maxV, p.Y);
                            minW = Math.Min(minW, p.Z); maxW = Math.Max(maxW, p.Z);
                        }
            if (points == 0) return null;

            return WallSketchDriftRules.Evaluate(
                ToVec(plane.Origin), ToVec(plane.Normal), ToVec(ws), ToVec(we),
                new Vec3(minU * MmPerFoot, minV * MmPerFoot, minW * MmPerFoot),
                new Vec3(maxU * MmPerFoot, maxV * MmPerFoot, maxW * MmPerFoot),
                toleranceMm);
        }

        public static Vec3 ToVec(XYZ p) => new Vec3(p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot);

        /// <summary>The wall's own run direction as a Revit vector, unit length, or null when the
        /// location curve is degenerate.</summary>
        public static XYZ RunDirection(Wall wall)
        {
            var lc = wall.Location as LocationCurve;
            if (lc == null || lc.Curve == null) return null;
            XYZ v = lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0);
            return v.GetLength() < 1e-9 ? null : v.Normalize();
        }
    }
}
