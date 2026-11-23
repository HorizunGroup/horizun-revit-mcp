using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class WallAttachmentRules
    {
        /// <summary>
        /// LEGACY reading, kept for callers that only have one face's vertices: the
        /// highest (or lowest) side-face vertex within a wall width of each end.
        /// MEASURED 2026-10-17 on Revit 2026 against a 5-degree footprint roof: it reads a
        /// vertex up to a wall width ALONG a sloped top (13.3 mm high on a 152 mm wall)
        /// and the exterior face of a top sloped ACROSS the wall (half a width of slope),
        /// which looked like a "roof base convention" and was not one. Use
        /// EndElevationsOnLocation.
        /// </summary>
        public static double?[] EndElevations(IReadOnlyList<double[]> vertices, double[] start, double[] end,
            double wallWidth, bool top)
        {
            if (vertices == null || start == null || end == null || start.Length < 2 || end.Length < 2 || wallWidth <= 0)
                throw new ArgumentException("Wall profile samples and positive wall width are required.");
            double radius = wallWidth + 20 / 304.8;
            double? At(double[] station)
            {
                double? selected = null;
                foreach (double[] v in vertices)
                {
                    if (v == null || v.Length < 3) continue;
                    double distance = Math.Sqrt((v[0] - station[0]) * (v[0] - station[0]) +
                                                (v[1] - station[1]) * (v[1] - station[1]));
                    if (distance > radius) continue;
                    if (!selected.HasValue || (top ? v[2] > selected.Value : v[2] < selected.Value)) selected = v[2];
                }
                return selected;
            }
            return new[] { At(start), At(end) };
        }

        /// <summary>
        /// The top (or base) elevation of a wall AT ITS LOCATION-LINE ENDS, which is what
        /// a caller can compute from the target alone (a roof plane evaluated at those two
        /// XY points). Each side face's boundary is reduced to its top (base) envelope
        /// along the wall, interpolated - or extrapolated along its nearest straight
        /// piece when a join trimmed the face short of the end - at each end station;
        /// the two faces are then interpolated ACROSS the wall to the location line, so
        /// a top sloped across the thickness reads its value on the line, not on the
        /// higher face. Segments are [x0,y0,z0,x1,y1,z1]; one list per side face.
        /// </summary>
        public static double?[] EndElevationsOnLocation(IReadOnlyList<IReadOnlyList<double[]>> faces,
            double[] start, double[] end, bool top)
        {
            if (faces == null || start == null || end == null || start.Length < 2 || end.Length < 2)
                throw new ArgumentException("Side-face segments and both location ends are required.");
            double tx = end[0] - start[0], ty = end[1] - start[1];
            double length = Math.Sqrt(tx * tx + ty * ty);
            if (length < 1e-9) throw new ArgumentException("The location line has no length.");
            tx /= length; ty /= length;
            double nx = -ty, ny = tx;
            double Along(double x, double y) => (x - start[0]) * tx + (y - start[1]) * ty;
            double Across(double x, double y) => (x - start[0]) * nx + (y - start[1]) * ny;
            const double eps = 1e-6;

            var perFace = new List<(double across, double? z0, double? z1)>();
            foreach (IReadOnlyList<double[]> face in faces)
            {
                var segs = (face ?? new List<double[]>()).Where(s => s != null && s.Length >= 6)
                    .Select(s => new { a0 = Along(s[0], s[1]), a1 = Along(s[3], s[4]), z0 = s[2], z1 = s[5],
                                       c = (Across(s[0], s[1]) + Across(s[3], s[4])) * 0.5 })
                    .Where(s => Math.Abs(s.a1 - s.a0) > eps).ToList();
                if (segs.Count == 0) continue;
                double? At(double station)
                {
                    double? best = null;
                    foreach (var s in segs)
                    {
                        double lo = Math.Min(s.a0, s.a1), hi = Math.Max(s.a0, s.a1);
                        if (station < lo - eps || station > hi + eps) continue;
                        double z = s.z0 + (s.z1 - s.z0) * (station - s.a0) / (s.a1 - s.a0);
                        if (!best.HasValue || (top ? z > best.Value : z < best.Value)) best = z;
                    }
                    if (best.HasValue) return best;
                    // Outside the face: extend the envelope piece that reaches nearest.
                    var near = segs.OrderBy(s => Math.Min(Math.Abs(station - s.a0), Math.Abs(station - s.a1)))
                        .ThenBy(s => top ? -Math.Max(s.z0, s.z1) : Math.Min(s.z0, s.z1)).First();
                    double nearest = Math.Min(Math.Abs(station - near.a0), Math.Abs(station - near.a1));
                    var ties = segs.Where(s => Math.Abs(Math.Min(Math.Abs(station - s.a0), Math.Abs(station - s.a1)) - nearest) <= 1e-4).ToList();
                    double? ext = null;
                    foreach (var s in ties)
                    {
                        double z = s.z0 + (s.z1 - s.z0) * (station - s.a0) / (s.a1 - s.a0);
                        if (!ext.HasValue || (top ? z > ext.Value : z < ext.Value)) ext = z;
                    }
                    return ext;
                }
                perFace.Add((segs.Average(s => s.c), At(0), At(length)));
            }
            if (perFace.Count == 0) return new double?[2];
            if (perFace.Count == 1) return new[] { perFace[0].z0, perFace[0].z1 };
            var a = perFace.OrderBy(f => f.across).First();
            var b = perFace.OrderBy(f => f.across).Last();
            if (Math.Abs(b.across - a.across) < eps) return new[] { a.z0, a.z1 };
            double w = (0 - a.across) / (b.across - a.across);
            double? Mix(double? za, double? zb) => za.HasValue && zb.HasValue ? za + (zb - za) * w : null;
            return new[] { Mix(a.z0, b.z0), Mix(a.z1, b.z1) };
        }

        /// <summary>
        /// Z of a non-vertical plane (origin, normal) above the point (x, y): where a wall
        /// end attached to that face will stop. Null for a vertical plane.
        /// </summary>
        public static double? PlaneZ(double[] origin, double[] normal, double x, double y)
        {
            if (origin == null || normal == null || origin.Length < 3 || normal.Length < 3) return null;
            if (Math.Abs(normal[2]) < 1e-9) return null;
            return origin[2] - (normal[0] * (x - origin[0]) + normal[1] * (y - origin[1])) / normal[2];
        }
    }
}
