// -----------------------------------------------------------------------------
// Horizun Revit MCP - the host's actual boundary, out of Revit and into numbers.
//
// SolidContainment answers "is the bar in the concrete" and knows nothing about
// Revit. This is the only place that turns an Element into the triangle mesh it
// works on, so there is exactly one answer to "which solid did you measure".
//
// Three things here are decisions rather than plumbing:
//
//  * The DETAIL LEVEL is Fine, and the instance transform is applied, so the mesh
//    is in model coordinates. A family instance whose geometry comes back inside
//    a GeometryInstance is in FAMILY coordinates until it is transformed, and a
//    beam measured in family coordinates is a beam at the origin.
//  * Openings and voids are already absent from the solids Revit returns, so a
//    slab with a hole has a hole here too. That is why a mat rule can ask whether
//    a bar crosses one.
//  * A CURVED face is tessellated, and the chord tolerance is published rather
//    than hidden. A round column's boundary is a many-sided prism here, slightly
//    inside the real cylinder, so a bar right at the surface of a round column is
//    reported marginally worse than it is - and the reply says so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public static class HostSolidMesh
    {
        public const double FtToMm = 304.8;

        /// <summary>What Revit is asked for. Smaller is finer; this is in feet.</summary>
        public const double ChordToleranceFt = 0.02;   // about 6 mm

        /// <summary>
        /// Every solid of an element, triangulated, in millimetres, in model
        /// coordinates. Returns null - never an empty mesh that would read as
        /// "nothing is inside" - when Revit would not give up its geometry.
        /// </summary>
        public static HostMesh For(Element host, out string why)
        {
            why = null;
            if (host == null) { why = "there was no host element to measure."; return null; }

            GeometryElement geometry;
            try
            {
                geometry = host.get_Geometry(new Options
                {
                    DetailLevel = ViewDetailLevel.Fine,
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false
                });
            }
            catch (Exception ex)
            {
                why = "Revit would not return the geometry of element " + Rid.Value(host.Id) + ": " + ex.Message;
                return null;
            }

            if (geometry == null)
            {
                why = "element " + Rid.Value(host.Id) + " returned no geometry at all.";
                return null;
            }

            var mesh = new HostMesh
            {
                ChordToleranceMm = ChordToleranceFt * FtToMm,
                Source = "every solid of element " + Rid.Value(host.Id) +
                         ", at Fine detail, triangulated, in model coordinates"
            };

            int solids = 0, skipped = 0;
            try { Harvest(geometry, Transform.Identity, mesh, ref solids, ref skipped); }
            catch (Exception ex)
            {
                why = "the geometry of element " + Rid.Value(host.Id) + " could not be walked: " + ex.Message;
                return null;
            }

            if (solids == 0 || mesh.Triangles.Count == 0)
            {
                why = "element " + Rid.Value(host.Id) + " has geometry but no solid with volume - there is " +
                      "no boundary to be inside of.";
                return null;
            }

            // A SOLID THAT WENT MISSING IS NOT A HOLE THE MANIFOLD CHECK CAN SEE.
            // A missing FACE leaves an open edge and is caught; a missing whole
            // SOLID leaves the rest perfectly closed, and every bar in the region it
            // occupied reads as completely_outside - with the mesh still saying it
            // is "every solid of element N".
            if (skipped > 0)
            {
                why = "element " + Rid.Value(host.Id) + " has " + skipped + " solid(s) whose geometry Revit " +
                      "would not return, and " + solids + " it would. What is left is closed, so nothing " +
                      "downstream could tell the difference: a bar in the missing part would read as outside " +
                      "the host. Containment is refused rather than measured against part of the member.";
                return null;
            }

            return mesh;
        }

        private static void Harvest(GeometryElement geometry, Transform t, HostMesh mesh,
                                    ref int solids, ref int skipped)
        {
            foreach (GeometryObject go in geometry)
            {
                var instance = go as GeometryInstance;
                if (instance != null)
                {
                    GeometryElement inner = null;
                    try { inner = instance.GetInstanceGeometry(); } catch { }
                    if (inner != null) { Harvest(inner, t, mesh, ref solids, ref skipped); continue; }

                    // GetInstanceGeometry already applies the transform; the symbol
                    // geometry does not, so it is applied here instead.
                    try { inner = instance.GetSymbolGeometry(); } catch { }
                    if (inner != null) Harvest(inner, t.Multiply(instance.Transform), mesh, ref solids, ref skipped);
                    else skipped++;
                    continue;
                }

                var solid = go as Solid;
                if (solid == null) continue;

                double volume;
                try { volume = solid.Volume; } catch { skipped++; continue; }
                if (volume <= 1e-9) continue;      // a sheet or a curve, not a piece of the member

                FaceArray faces;
                try { faces = solid.Faces; } catch { skipped++; continue; }
                if (faces == null || faces.Size == 0) { skipped++; continue; }

                solids++;
                foreach (Face face in faces)
                {
                    if (!(face is PlanarFace)) mesh.AnyCurvedFace = true;

                    Mesh tri;
                    try { tri = face.Triangulate(); } catch { continue; }
                    if (tri == null) continue;

                    int baseIndex = mesh.Vertices.Count;
                    for (int i = 0; i < tri.Vertices.Count; i++)
                    {
                        XYZ p = tri.Vertices[i];
                        if (t != null && !t.IsIdentity) p = t.OfPoint(p);
                        mesh.AddVertex(p.X * FtToMm, p.Y * FtToMm, p.Z * FtToMm);
                    }
                    for (int i = 0; i < tri.NumTriangles; i++)
                    {
                        MeshTriangle mt = tri.get_Triangle(i);
                        mesh.AddTriangle(baseIndex + (int)mt.get_Index(0),
                                         baseIndex + (int)mt.get_Index(1),
                                         baseIndex + (int)mt.get_Index(2));
                    }
                }
            }
        }

        /// <summary>
        /// Triangulation produces its vertices per FACE, so the same corner of a box
        /// arrives three times and every edge looks unshared. The manifold check in
        /// SolidContainment.Diagnose is about the SHAPE, not about how many copies of
        /// a point were sent, so identical points are merged first - to a grid finer
        /// than any tolerance the reinforcement work uses.
        /// </summary>
        public static HostMesh Weld(HostMesh mesh, double gridMm)
        {
            if (mesh == null) return null;
            if (!(gridMm > 0)) gridMm = 1e-4;

            var map = new Dictionary<Key, int>(mesh.Vertices.Count);
            var index = new int[mesh.Vertices.Count];
            var welded = new HostMesh
            {
                AnyCurvedFace = mesh.AnyCurvedFace,
                ChordToleranceMm = mesh.ChordToleranceMm,
                Source = mesh.Source + ", welded on a " + gridMm.ToString("0.####",
                    System.Globalization.CultureInfo.InvariantCulture) + " mm grid"
            };

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                double[] v = mesh.Vertices[i];
                // INTEGERS, not the string a double prints. Math.Round(-0.3) prints
                // "-0" and Math.Round(0.2) prints "0" on .NET 8, so two vertices at
                // the same physical point on either side of zero welded to DIFFERENT
                // vertices - the shared edges then lost their pair and the whole
                // element was refused as an open shell. And "-0" versus "0" differs
                // by framework, so the same host behaved differently by Revit year.
                // A large coordinate printed with the CURRENT CULTURE's separator on
                // top of that.
                var key = new Key(Snap(v[0], gridMm), Snap(v[1], gridMm), Snap(v[2], gridMm));
                int at;
                if (!map.TryGetValue(key, out at))
                {
                    at = welded.AddVertex(v[0], v[1], v[2]);
                    map[key] = at;
                }
                index[i] = at;
            }

            foreach (int[] t in mesh.Triangles)
            {
                int a = index[t[0]], b = index[t[1]], c = index[t[2]];
                if (a == b || b == c || a == c) continue;   // collapsed by the weld
                welded.AddTriangle(a, b, c);
            }
            return welded;
        }

        private static long Snap(double v, double grid)
        {
            double scaled = v / grid;
            if (double.IsNaN(scaled) || double.IsInfinity(scaled)) return long.MinValue;
            double f = Math.Floor(scaled + 0.5);
            if (f > 4.0e18) return long.MaxValue;
            if (f < -4.0e18) return long.MinValue;
            return (long)f;
        }

        /// <summary>A snapped vertex, compared as three integers rather than as printed text.</summary>
        private struct Key : IEquatable<Key>
        {
            private readonly long _x, _y, _z;
            public Key(long x, long y, long z) { _x = x; _y = y; _z = z; }
            public bool Equals(Key other) { return _x == other._x && _y == other._y && _z == other._z; }
            public override bool Equals(object obj) { return obj is Key && Equals((Key)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _x.GetHashCode();
                    h = (h * 397) ^ _y.GetHashCode();
                    return (h * 397) ^ _z.GetHashCode();
                }
            }
        }

        /// <summary>The mesh a containment check should use: harvested, then welded.</summary>
        public static HostMesh Usable(Element host, out string why)
        {
            HostMesh raw = For(host, out why);
            if (raw == null) return null;
            HostMesh welded = Weld(raw, 1e-4);

            MeshDiagnosis d = SolidContainment.Diagnose(welded);
            if (!d.Usable)
            {
                why = "the boundary of element " + Rid.Value(host.Id) + " is not usable: " + d.Why;
                return null;
            }
            return welded;
        }
    }
}
