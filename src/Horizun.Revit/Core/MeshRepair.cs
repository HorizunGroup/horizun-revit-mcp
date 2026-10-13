using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Cracks Revit's per-face triangulation leaves in a closed solid (acero 3.0, Paso 3). MEASURED on
    /// Revit 2024, a round column of a real model: 108 triangles and 4 edges not used once in each
    /// direction - two faces sharing a curved edge tessellate it differently, and the solid reads as open.
    /// A loop of open edges no wider than the chord tolerance the mesh already declares is that, and is
    /// closed with triangles that use its edges the other way round; anything wider is a real hole and is
    /// left for the caller to refuse. What was closed is written into the mesh's Source.
    /// </summary>
    public static class MeshRepair
    {
        /// <summary>
        /// The mesh with its cracks closed, the same mesh when it had none, or null when an open loop is
        /// wider than <paramref name="maxGapMm"/> or the open edges do not chain into loops.
        /// </summary>
        public static HostMesh CloseCracks(HostMesh mesh, double maxGapMm, out int closed, out double widestMm)
        {
            closed = 0; widestMm = 0;
            if (mesh == null) return null;
            var count = new Dictionary<(int, int), int>();
            foreach (int[] t in mesh.Triangles)
                for (int k = 0; k < 3; k++)
                {
                    var e = (t[k], t[(k + 1) % 3]);
                    count[e] = count.TryGetValue(e, out int c) ? c + 1 : 1;
                }
            // An open edge: used in one direction and not in the other, once.
            var next = new Dictionary<int, int>();
            foreach (KeyValuePair<(int, int), int> kv in count)
            {
                count.TryGetValue((kv.Key.Item2, kv.Key.Item1), out int back);
                if (kv.Value == back) continue;
                if (kv.Value != 1 || back != 0 || next.ContainsKey(kv.Key.Item1)) return null;
                next[kv.Key.Item1] = kv.Key.Item2;
            }
            if (next.Count == 0) return mesh;

            var loops = new List<List<int>>();
            var seen = new HashSet<int>();
            foreach (int start in next.Keys)
            {
                if (seen.Contains(start)) continue;
                var loop = new List<int>();
                int v = start;
                while (!seen.Contains(v))
                {
                    seen.Add(v); loop.Add(v);
                    if (!next.TryGetValue(v, out v)) return null;
                }
                if (v != start) return null;
                loops.Add(loop);
            }
            foreach (List<int> loop in loops)
            {
                double w = Width(mesh, loop);
                widestMm = Math.Max(widestMm, w);
            }
            if (widestMm > maxGapMm) return null;

            var outMesh = new HostMesh
            {
                AnyCurvedFace = mesh.AnyCurvedFace,
                ChordToleranceMm = mesh.ChordToleranceMm,
                Source = mesh.Source + ", " + loops.Count + " crack(s) of Revit's triangulation closed (widest " +
                         Math.Round(widestMm, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + " mm, within the " +
                         Math.Round(maxGapMm, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + " mm chord tolerance)"
            };
            foreach (double[] p in mesh.Vertices) outMesh.AddVertex(p[0], p[1], p[2]);
            foreach (int[] t in mesh.Triangles) outMesh.AddTriangle(t[0], t[1], t[2]);
            // The original triangles keep their indices, so which of them were curved is unchanged.
            outMesh.CurvedTriangles = new HashSet<int>(mesh.CurvedTriangles);
            foreach (List<int> loop in loops)
                // The open edges run v0 -> v1 -> ...; the fan uses each of them the other way round.
                for (int i = 1; i + 1 < loop.Count; i++) outMesh.AddTriangle(loop[0], loop[i + 1], loop[i]);
            closed = loops.Count;
            return outMesh;
        }

        /// <summary>
        /// How wide a loop is: four times its (vector) area over its perimeter - the width of a sliver
        /// (2 x length x width / 2, over twice the length), the side of a square hole.
        /// </summary>
        private static double Width(HostMesh m, List<int> loop)
        {
            int n = loop.Count;
            if (n < 3) return 0;
            double ax = 0, ay = 0, az = 0, per = 0;
            double[] o = m.Vertices[loop[0]];
            for (int i = 0; i < n; i++)
            {
                double[] a = m.Vertices[loop[i]], b = m.Vertices[loop[(i + 1) % n]];
                per += PointSegment(a, b, b);
                double[] u = { a[0] - o[0], a[1] - o[1], a[2] - o[2] }, v = { b[0] - o[0], b[1] - o[1], b[2] - o[2] };
                ax += u[1] * v[2] - u[2] * v[1]; ay += u[2] * v[0] - u[0] * v[2]; az += u[0] * v[1] - u[1] * v[0];
            }
            double area = 0.5 * Math.Sqrt(ax * ax + ay * ay + az * az);
            return per <= 0 ? 0 : 4 * area / per;
        }

        private static double PointSegment(double[] p, double[] a, double[] b)
        {
            double[] ab = { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, ap = { p[0] - a[0], p[1] - a[1], p[2] - a[2] };
            double l2 = ab[0] * ab[0] + ab[1] * ab[1] + ab[2] * ab[2];
            double t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, (ap[0] * ab[0] + ap[1] * ab[1] + ap[2] * ab[2]) / l2));
            double dx = ap[0] - t * ab[0], dy = ap[1] - t * ab[1], dz = ap[2] - t * ab[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
