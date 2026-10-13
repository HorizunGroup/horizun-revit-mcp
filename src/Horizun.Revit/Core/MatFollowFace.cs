// -----------------------------------------------------------------------------
// Horizun Revit MCP - a mat whose bars follow their face.
// Original Horizun code. No Revit types.
//
// A flat mat lies in ONE plane: the outermost face along face_normal. A roof is
// not one plane. Measured on a real kiosk roof: two floors - a flat strip and a
// 6% slope meeting at a break - whose top and bottom bars run continuously across
// both pieces, bent at the break, each layer at its cover from the face it lies
// under; and whose bars across the slope are each straight but at their OWN
// height, so they cannot be one Revit array. The pipeline that built it cast rays
// at the faces from outside. This file does it from the host meshes the bridge
// already reads, and nothing casts a ray.
//
// For a bar at station v across the face, in the vertical plane through it:
//
//   the FACE      the upper envelope (along face_normal) of the host triangles
//                 that face that way, of every element the face runs through;
//                 coplanar triangles merged into facets, so a vertex appears only
//                 where two facets really meet
//   the OFFSET    each facet's plane moved inward by offset_from_face_mm along
//                 THAT facet's normal - the cover is the cover to the face the bar
//                 is under, on both sides of a break. Where two facets meet, the
//                 bar bends where their offset planes meet: inside a valley the
//                 higher of the two, under a ridge the lower (the erosion of a
//                 polyhedron has its creases there, and nowhere is it rounded)
//   the ENDS      the face's own extent at that station, less the end cover
//
// What it refuses, by name and with the measurement: a face that steps under the
// bar (a vertical jump is not something a bent bar can follow), a fold bent at a
// curved face's tessellation rather than at a real break, a facet shorter under
// the bar than the declared minimum, an offset that folds over on itself, and a
// station with no face under it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A mat component that follows its face, and the two numbers it is followed by. Both declared.</summary>
    public sealed class StructuralFollowFace
    {
        /// <summary>How far a facet's triangles may stray from one plane and still be one facet, in mm.</summary>
        public double PlanarityMm;
        /// <summary>The shortest stretch of a facet, in mm, a bar may be bent onto.</summary>
        public double MinFacetMm;
    }

    /// <summary>One element a followed face runs through: the host first, then the into elements in order.</summary>
    public sealed class MatHostPart
    {
        /// <summary>Null in a test with one part: the rule's own host selector is used.</summary>
        public long? ElementId;
        public HostMesh Mesh;
        /// <summary>The element's common cover; null when it has none.</summary>
        public double? CommonCoverMm;
        /// <summary>The cover of its face along face_normal, read when it has no common cover; null otherwise.</summary>
        public double? FaceCoverMm;
    }

    /// <summary>A planar piece of the face: every facing triangle within planarity_mm of one plane.</summary>
    public sealed class FaceFacet
    {
        public int Index;
        /// <summary>Unit outward normal, from the facet's largest triangle.</summary>
        public double[] Normal;
        /// <summary>The plane is Normal . p = D.</summary>
        public double D;
        public double AreaMm2;
        /// <summary>The worst distance of a member vertex from the plane - at most planarity_mm by construction.</summary>
        public double MaxDeviationMm;
        /// <summary>At least one of its triangles is a tessellation of a curved face.</summary>
        public bool Curved;
        /// <summary>The mesh said it had a curved face and did not say which triangles: unknown, not planar.</summary>
        public bool CurvedUnknown;
        public int TriangleCount;
        public SortedSet<int> Parts = new SortedSet<int>();
        public List<FaceFold> Folds = new List<FaceFold>();
        internal List<int> Tris = new List<int>();
        internal double[] Min = { double.MaxValue, double.MaxValue, double.MaxValue };
        internal double[] Max = { double.MinValue, double.MinValue, double.MinValue };
    }

    /// <summary>Where a facet meets another along a common line: the line's stretches and which way it folds.</summary>
    public sealed class FaceFold
    {
        public int Other;
        /// <summary>True when the other facet lies inside this one's plane: a ridge, convex.</summary>
        public bool Ridge;
        /// <summary>The stretches of the fold line both facets reach, as [p, q] in model mm.</summary>
        public List<double[][]> Segments = new List<double[][]>();
    }

    internal sealed class FacingTri
    {
        public int Part;
        public int MeshIndex;
        public int Facet;
        public double[] A, B, C, N;
        public double Area;
        public bool Curved, CurvedUnknown;
    }

    /// <summary>The face along face_normal of every element the face runs through, as facets.</summary>
    public sealed class FacingFace
    {
        public double[] Up;
        public double PlanarityMm;
        public int PartCount;
        public List<FaceFacet> Facets = new List<FaceFacet>();
        internal List<FacingTri> Tris = new List<FacingTri>();
        /// <summary>Rings of the face that wind the other way round: holes, by part.</summary>
        public List<FaceLoop> Openings = new List<FaceLoop>();
        /// <summary>Null when the rings were read; why they could not be otherwise.</summary>
        public string LoopsWhy;
        public string Why;
        public bool Ok { get { return Why == null; } }
    }

    /// <summary>One stretch of the face's section, on one facet of one part.</summary>
    public sealed class SectionPiece
    {
        public double U0, U1;
        public int Facet;
        public int Part;
    }

    /// <summary>The face cut by the vertical plane through one bar station.</summary>
    public sealed class FaceSection
    {
        public double V;
        public List<SectionPiece> Pieces = new List<SectionPiece>();
        public bool Empty { get { return Pieces.Count == 0; } }
        public double MinU { get { return Pieces.Count == 0 ? 0 : Pieces[0].U0; } }
        public double MaxU { get { return Pieces.Count == 0 ? 0 : Pieces[Pieces.Count - 1].U1; } }
    }

    /// <summary>One straight stretch of the offset face along u: h = C + M u, governed by one facet's offset plane.</summary>
    public sealed class OffsetSegment
    {
        public double U0, U1, C, M;
        public int Facet;
        public double At(double u) { return C + M * u; }
    }

    /// <summary>The offset face under one station, over the face's whole extent there - or why not.</summary>
    public sealed class OffsetPath
    {
        public List<OffsetSegment> Segments = new List<OffsetSegment>();
        /// <summary>Stretches where the face is absent and its two sides are different facets: nothing to follow.</summary>
        public List<double[]> Unbridged = new List<double[]>();
        public string Code;
        public string Why;
        public bool Ok { get { return Code == null; } }

        public double At(double u)
        {
            foreach (OffsetSegment s in Segments)
                if (u <= s.U1 + 1e-9) return s.At(u);
            return Segments.Count == 0 ? double.NaN : Segments[Segments.Count - 1].At(u);
        }

        public OffsetSegment SegmentAt(double u)
        {
            foreach (OffsetSegment s in Segments)
                if (u <= s.U1 + 1e-9) return s;
            return Segments.Count == 0 ? null : Segments[Segments.Count - 1];
        }
    }

    /// <summary>One bar on the face: its polyline as (u, h), which part hosts it, which it runs into - or why not.</summary>
    public sealed class FollowedBar
    {
        public List<double[]> Uh = new List<double[]>();
        /// <summary>The facet governing each segment of the polyline, in order.</summary>
        public List<int> Facets = new List<int>();
        public int HostPart;
        public List<int> IntoParts = new List<int>();
        public string Code;
        public string Why;
        public bool Ok { get { return Code == null; } }
        public int Bends { get { return Math.Max(0, Uh.Count - 2); } }
    }

    /// <summary>What one expanded rule of a followed mat was laid on. Reported on the planned row.</summary>
    public sealed class MatFollowContext
    {
        public double PlanarityMm;
        public double MinFacetMm;
        public double OffsetFromFaceMm;
        public int FirstBar, LastBar, Bars;
        public double FirstStationMm;
        public List<double[]> PolylineUh = new List<double[]>();
        public List<FaceFacet> FacetsUnder = new List<FaceFacet>();
        public long? HostElementId;
        public List<long> IntoElementIds = new List<long>();
        /// <summary>The worst difference between a station's own polyline and the run's first one: within SameShapeToleranceMm.</summary>
        public double WorstShapeDifferenceMm;
        public int StationsInComponent;
        public int RunsInComponent;

        public JObject ToJson()
        {
            var facets = new JArray();
            foreach (FaceFacet f in FacetsUnder)
                facets.Add(new JObject
                {
                    ["facet"] = f.Index,
                    ["normal"] = new JArray(f.Normal.Select(x => (object)Math.Round(x, 6)).ToArray()),
                    ["triangles"] = f.TriangleCount,
                    ["max_deviation_mm"] = Math.Round(f.MaxDeviationMm, 4),
                    ["parts"] = new JArray(f.Parts.Cast<object>().ToArray())
                });
            return new JObject
            {
                ["planarity_mm"] = PlanarityMm,
                ["min_facet_mm"] = MinFacetMm,
                ["offset_from_face_mm"] = OffsetFromFaceMm,
                ["first_bar"] = FirstBar,
                ["last_bar"] = LastBar,
                ["bars"] = Bars,
                ["first_station_mm"] = Math.Round(FirstStationMm, 3),
                ["polyline_uh_mm"] = new JArray(PolylineUh.Select(p => (object)new JArray(Math.Round(p[0], 3), Math.Round(p[1], 3))).ToArray()),
                ["bends"] = Math.Max(0, PolylineUh.Count - 2),
                ["facets_under"] = facets,
                ["host_element_id"] = HostElementId.HasValue ? (JToken)HostElementId.Value : JValue.CreateNull(),
                ["into_element_ids"] = new JArray(IntoElementIds.Cast<object>().ToArray()),
                ["worst_shape_difference_in_run_mm"] = Math.Round(WorstShapeDifferenceMm, 6),
                ["stations_in_component"] = StationsInComponent,
                ["runs_in_component"] = RunsInComponent,
                ["how"] = MatFollowFace.How
            };
        }
    }

    public static class MatFollowFace
    {
        /// <summary>A triangle whose outward normal is closer than this to perpendicular to face_normal is a side, not the face.</summary>
        public const double FacingMinimum = 0.01;

        /// <summary>A gap in the face's section narrower than this is a seam between triangles or elements, not a hole.</summary>
        public const double SeamToleranceMm = 0.5;

        /// <summary>Two stations whose polylines differ by no more than this are the same bar translated: one Revit set.</summary>
        public const double SameShapeToleranceMm = 0.01;

        /// <summary>The offset path is continuous to this; anything larger is a jump a bar cannot follow.</summary>
        public const double ContinuityToleranceMm = 1e-4;

        public const string How =
            "the face is the upper envelope, along face_normal, of the triangles of the host (and of every into element) " +
            "that face that way, with the triangles within planarity_mm of one plane merged into a facet. Each bar's " +
            "centreline is that face cut by the vertical plane through the bar, every facet's plane moved inward by " +
            "offset_from_face_mm along its own normal, bent where two offset planes meet (the higher in a valley, the " +
            "lower under a ridge), and stopped end_cover_mm short of the face's extent at that station. Stations whose " +
            "polylines are the same, translated, are one Revit set; every other station is its own.";

        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        // ------------------------------------------------------------- the face

        /// <summary>
        /// The facing triangles of every part, merged into facets, with the folds between them and the holes
        /// of each part. Fails, with the reason, rather than returning a face with nothing in it.
        /// </summary>
        public static FacingFace Build(IList<HostMesh> parts, double[] up, double planarityMm)
        {
            var f = new FacingFace { Up = RebarContainment.Unit(up), PlanarityMm = planarityMm, PartCount = parts == null ? 0 : parts.Count };
            if (f.Up == null) { f.Why = "face_normal is not a usable vector."; return f; }
            if (parts == null || parts.Count == 0) { f.Why = "no host boundary was obtained."; return f; }
            double tol = planarityMm;

            var facingByPart = new List<List<int[]>>();
            for (int p = 0; p < parts.Count; p++)
            {
                HostMesh m = parts[p];
                var mine = new List<int[]>();
                facingByPart.Add(mine);
                if (m == null) continue;
                bool attributed = !m.AnyCurvedFace || (m.CurvedTriangles != null && m.CurvedTriangles.Count > 0);
                for (int ti = 0; ti < m.Triangles.Count; ti++)
                {
                    int[] t = m.Triangles[ti];
                    double[] a = m.Vertices[t[0]], b = m.Vertices[t[1]], c = m.Vertices[t[2]];
                    double[] n = Cross(Sub(b, a), Sub(c, a));
                    double len = Norm(n);
                    if (!(len > 1e-9)) continue;
                    n = new[] { n[0] / len, n[1] / len, n[2] / len };
                    if (Dot(n, f.Up) <= FacingMinimum) continue;
                    mine.Add(t);
                    f.Tris.Add(new FacingTri
                    {
                        Part = p, MeshIndex = ti, A = a, B = b, C = c, N = n, Area = len / 2,
                        Curved = m.CurvedTriangles != null && m.CurvedTriangles.Contains(ti),
                        CurvedUnknown = !attributed
                    });
                }
            }
            if (f.Tris.Count == 0)
            {
                f.Why = "no triangle of the host" + (parts.Count > 1 ? " or of the elements it runs into" : "") +
                        " faces along face_normal, so there is no face to follow.";
                return f;
            }

            // FACETS: the largest triangle first, each joining the first facet whose plane it lies on to
            // within planarity_mm at all three corners - and facing the same way.
            foreach (int i in Enumerable.Range(0, f.Tris.Count).OrderByDescending(k => f.Tris[k].Area).ThenBy(k => k))
            {
                FacingTri t = f.Tris[i];
                FaceFacet home = null;
                double worst = 0;
                foreach (FaceFacet fc in f.Facets)
                {
                    if (Dot(t.N, fc.Normal) <= 0) continue;
                    double da = Math.Abs(Dot(fc.Normal, t.A) - fc.D), db = Math.Abs(Dot(fc.Normal, t.B) - fc.D), dc = Math.Abs(Dot(fc.Normal, t.C) - fc.D);
                    double w = Math.Max(da, Math.Max(db, dc));
                    if (w <= tol) { home = fc; worst = w; break; }
                }
                if (home == null)
                {
                    home = new FaceFacet { Index = f.Facets.Count, Normal = t.N, D = Dot(t.N, t.A) };
                    f.Facets.Add(home);
                }
                home.Tris.Add(i);
                home.TriangleCount++;
                home.AreaMm2 += t.Area;
                home.MaxDeviationMm = Math.Max(home.MaxDeviationMm, worst);
                home.Parts.Add(t.Part);
                if (t.Curved) home.Curved = true;
                if (t.CurvedUnknown) home.CurvedUnknown = true;
                foreach (double[] q in new[] { t.A, t.B, t.C })
                    for (int k = 0; k < 3; k++) { home.Min[k] = Math.Min(home.Min[k], q[k]); home.Max[k] = Math.Max(home.Max[k], q[k]); }
                t.Facet = home.Index;
            }

            // FOLDS: two facets meet where each has edges lying in the other's plane, along the line the
            // planes share. Only those stretches are a fold; the planes' line elsewhere means nothing.
            for (int i = 0; i < f.Facets.Count; i++)
                for (int k = i + 1; k < f.Facets.Count; k++)
                {
                    FaceFacet a = f.Facets[i], b = f.Facets[k];
                    bool apart = false;
                    for (int x = 0; x < 3; x++)
                        if (a.Min[x] > b.Max[x] + tol || b.Min[x] > a.Max[x] + tol) apart = true;
                    if (apart) continue;
                    double[] dir = Cross(a.Normal, b.Normal);
                    double dl = Norm(dir);
                    if (dl < 1e-9) continue;
                    dir = new[] { dir[0] / dl, dir[1] / dl, dir[2] / dl };
                    double[] origin = null;
                    List<double[]> onA = EdgesOnPlane(f, a, b, dir, tol, ref origin);
                    List<double[]> onB = EdgesOnPlane(f, b, a, dir, tol, ref origin);
                    if (onA.Count == 0 || onB.Count == 0) continue;
                    List<double[]> both = MatOpenings.Intersect(Merge(onA, tol), Merge(onB, tol));
                    var segs = new List<double[][]>();
                    foreach (double[] s in both)
                        if (s[1] - s[0] > tol)
                            segs.Add(new[] { Add(origin, Scale(dir, s[0])), Add(origin, Scale(dir, s[1])) });
                    if (segs.Count == 0) continue;
                    a.Folds.Add(new FaceFold { Other = b.Index, Ridge = Inside(f, b, a), Segments = segs });
                    b.Folds.Add(new FaceFold { Other = a.Index, Ridge = Inside(f, a, b), Segments = segs });
                }

            // THE HOLES, part by part: a ring of the facing triangles that winds clockwise seen from
            // face_normal has the face outside it. Rings of different elements never meet in one ring.
            double[] ax = AnyPerpendicular(f.Up);
            double[] ay = Cross(f.Up, ax);
            for (int p = 0; p < parts.Count; p++)
            {
                if (facingByPart[p].Count == 0) continue;
                List<FaceLoop> rings;
                string why = MatOpenings.ChainRings(parts[p], facingByPart[p], out rings);
                if (why != null)
                {
                    f.LoopsWhy = (parts.Count > 1 ? "part " + (p + 1) + ": " : "") + why;
                    f.Openings.Clear();
                    break;
                }
                foreach (FaceLoop l in rings)
                {
                    double signed = MatOpenings.SignedArea(MatOpenings.ProjectUv(l.PointsMm, ax, ay));
                    l.AreaMm2 = Math.Abs(signed);
                    l.Outer = signed > 0;
                    if (!l.Outer) f.Openings.Add(l);
                }
            }
            return f;
        }

        /// <summary>The extent of the face's triangles along a direction.</summary>
        public static void Extent(FacingFace f, double[] dir, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            foreach (FacingTri t in f.Tris)
                foreach (double[] p in new[] { t.A, t.B, t.C })
                {
                    double d = Dot(p, dir);
                    if (d < min) min = d;
                    if (d > max) max = d;
                }
        }

        private static List<double[]> EdgesOnPlane(FacingFace f, FaceFacet of, FaceFacet plane, double[] dir, double tol, ref double[] origin)
        {
            var outp = new List<double[]>();
            foreach (int ti in of.Tris)
            {
                FacingTri t = f.Tris[ti];
                double[][] v = { t.A, t.B, t.C };
                for (int e = 0; e < 3; e++)
                {
                    double[] p = v[e], q = v[(e + 1) % 3];
                    if (Math.Abs(Dot(plane.Normal, p) - plane.D) > tol || Math.Abs(Dot(plane.Normal, q) - plane.D) > tol) continue;
                    if (origin == null) origin = new[] { p[0], p[1], p[2] };
                    double tp = Dot(Sub(p, origin), dir), tq = Dot(Sub(q, origin), dir);
                    outp.Add(new[] { Math.Min(tp, tq), Math.Max(tp, tq) });
                }
            }
            return outp;
        }

        private static List<double[]> Merge(List<double[]> intervals, double tol)
        {
            var outp = new List<double[]>();
            foreach (double[] iv in intervals.OrderBy(x => x[0]))
            {
                if (outp.Count > 0 && iv[0] <= outp[outp.Count - 1][1] + tol)
                    outp[outp.Count - 1][1] = Math.Max(outp[outp.Count - 1][1], iv[1]);
                else outp.Add(new[] { iv[0], iv[1] });
            }
            return outp;
        }

        /// <summary>Whether facet `other` lies inside the plane of `of` (on the side away from its normal).</summary>
        private static bool Inside(FacingFace f, FaceFacet other, FaceFacet of)
        {
            double worst = 0;
            foreach (int ti in other.Tris)
            {
                FacingTri t = f.Tris[ti];
                foreach (double[] p in new[] { t.A, t.B, t.C })
                {
                    double d = Dot(of.Normal, p) - of.D;
                    if (Math.Abs(d) > Math.Abs(worst)) worst = d;
                }
            }
            return worst < 0;
        }

        // ---------------------------------------------------------- the section

        /// <summary>
        /// The face cut by the plane through station <paramref name="v"/> (across) spanned by the bar
        /// direction and face_normal: the outermost facet over every stretch of u, seams closed.
        /// </summary>
        public static FaceSection Section(FacingFace f, double[] along, double[] across, double v)
        {
            var sec = new FaceSection { V = v };
            var raw = new List<double[]>();   // u0, u1, tri index
            for (int i = 0; i < f.Tris.Count; i++)
            {
                FacingTri t = f.Tris[i];
                double[][] p = { t.A, t.B, t.C };
                var us = new List<double>();
                for (int e = 0; e < 3; e++)
                {
                    double[] a = p[e], b = p[(e + 1) % 3];
                    double sa = Dot(a, across) - v, sb = Dot(b, across) - v;
                    if (sa == 0 && sb == 0) { us.Add(Dot(a, along)); us.Add(Dot(b, along)); continue; }
                    if ((sa <= 0 && sb >= 0) || (sa >= 0 && sb <= 0))
                    {
                        double k = sa / (sa - sb);
                        us.Add(Dot(a, along) + k * (Dot(b, along) - Dot(a, along)));
                    }
                }
                if (us.Count < 2) continue;
                double u0 = us.Min(), u1 = us.Max();
                if (u1 - u0 < 1e-9) continue;
                raw.Add(new[] { u0, u1, i });
            }
            if (raw.Count == 0) return sec;

            var cuts = raw.SelectMany(x => new[] { x[0], x[1] }).OrderBy(x => x).ToList();
            var bps = new List<double>();
            foreach (double c in cuts) if (bps.Count == 0 || c - bps[bps.Count - 1] > 1e-9) bps.Add(c);

            var pieces = new List<SectionPiece>();
            for (int i = 0; i + 1 < bps.Count; i++)
            {
                double a = bps[i], b = bps[i + 1], mid = (a + b) / 2;
                int best = -1; double bestH = double.MinValue;
                foreach (double[] x in raw)
                {
                    if (x[0] > mid || x[1] < mid) continue;
                    FacingTri t = f.Tris[(int)x[2]];
                    FaceFacet fc = f.Facets[t.Facet];
                    double h = FaceHeight(fc, along, across, f.Up, mid, v);
                    if (best < 0 || h > bestH + 1e-6) { best = (int)x[2]; bestH = h; continue; }
                    if (h >= bestH - 1e-6 && f.Tris[best].Facet != t.Facet && PreferOnTie(f, fc, f.Facets[f.Tris[best].Facet]))
                    { best = (int)x[2]; bestH = Math.Max(bestH, h); }
                }
                if (best < 0) continue;
                FacingTri bt = f.Tris[best];
                SectionPiece last = pieces.Count > 0 ? pieces[pieces.Count - 1] : null;
                if (last != null && last.Facet == bt.Facet && last.Part == bt.Part && Math.Abs(last.U1 - a) <= 1e-9) last.U1 = b;
                else pieces.Add(new SectionPiece { U0 = a, U1 = b, Facet = bt.Facet, Part = bt.Part });
            }
            // SEAMS: a sliver narrower than the seam tolerance between two pieces is closed at its middle.
            for (int i = 0; i + 1 < pieces.Count; i++)
            {
                double gap = pieces[i + 1].U0 - pieces[i].U1;
                if (gap > 1e-9 && gap <= SeamToleranceMm)
                {
                    double m = (pieces[i].U1 + pieces[i + 1].U0) / 2;
                    pieces[i].U1 = m; pieces[i + 1].U0 = m;
                }
            }
            sec.Pieces = pieces;
            return sec;
        }

        /// <summary>
        /// Two facets tie only where the bar's plane runs along their fold. The bar is then under the
        /// facet whose offset governs there: the steeper under a ridge, the flatter in a valley.
        /// </summary>
        private static bool PreferOnTie(FacingFace f, FaceFacet candidate, FaceFacet current)
        {
            FaceFold fold = current.Folds.FirstOrDefault(x => x.Other == candidate.Index);
            if (fold == null) return false;
            double nc = Dot(candidate.Normal, f.Up), nb = Dot(current.Normal, f.Up);
            return fold.Ridge ? nc < nb : nc > nb;
        }

        private static double FaceHeight(FaceFacet fc, double[] along, double[] across, double[] up, double u, double v)
        {
            return (fc.D - u * Dot(fc.Normal, along) - v * Dot(fc.Normal, across)) / Dot(fc.Normal, up);
        }

        // ---------------------------------------------------------- the offset

        private sealed class Line { public double C, M; public int Facet; public double At(double u) { return C + M * u; } }

        private static Line OffsetLine(FaceFacet fc, double[] along, double[] across, double[] up, double v, double offset)
        {
            double nu = Dot(fc.Normal, up);
            return new Line { C = (fc.D - offset - v * Dot(fc.Normal, across)) / nu, M = -Dot(fc.Normal, along) / nu, Facet = fc.Index };
        }

        /// <summary>
        /// The face under one station, every facet's plane moved inward by the offset along its own
        /// normal, as straight segments over the whole of the face's extent there.
        /// </summary>
        public static OffsetPath Offset(FacingFace f, FaceSection sec, double[] along, double[] across, double offset, string name)
        {
            var path = new OffsetPath();
            double[] up = f.Up;
            double tol = f.PlanarityMm;
            if (sec.Empty) { path.Code = MatRules.CodeNoFaceUnderBar; path.Why = "component '" + name + "': no face under station " + Mm(sec.V) + "."; return path; }

            // STEPS AND GAPS. Contiguous pieces of different facets must meet within planarity_mm; a gap
            // between two pieces of one facet is bridged (a hole the bars may run over is the policy's to
            // decide), and a gap between two different facets has no face to follow across it.
            var spans = new List<SectionPiece>();
            for (int i = 0; i < sec.Pieces.Count; i++)
            {
                SectionPiece p = sec.Pieces[i];
                SectionPiece last = spans.Count > 0 ? spans[spans.Count - 1] : null;
                if (last == null) { spans.Add(new SectionPiece { U0 = p.U0, U1 = p.U1, Facet = p.Facet, Part = p.Part }); continue; }
                bool contiguous = p.U0 - last.U1 <= 1e-9;
                if (last.Facet == p.Facet) { last.U1 = p.U1; continue; }
                FaceFacet a = f.Facets[last.Facet], b = f.Facets[p.Facet];
                if (contiguous)
                {
                    double jump = Math.Abs(FaceHeight(a, along, across, up, p.U0, sec.V) - FaceHeight(b, along, across, up, p.U0, sec.V));
                    if (jump > tol)
                    {
                        path.Code = MatRules.CodeFaceSteps;
                        path.Why = "component '" + name + "': at station " + Mm(sec.V) + " the face steps by " + Mm(jump) + " at " +
                                   Mm(p.U0) + " along the bar (from facet " + a.Index + " to facet " + b.Index + "), more than the " +
                                   "declared planarity_mm of " + Mm(tol) + ". A bar can bend over a fold; it cannot follow a step.";
                        return path;
                    }
                }
                else path.Unbridged.Add(new[] { last.U1, p.U0 });
                spans.Add(new SectionPiece { U0 = p.U0, U1 = p.U1, Facet = p.Facet, Part = p.Part });
            }

            // EACH SPAN'S OFFSET: its own facet's offset line, combined with the facets it folds against
            // wherever the station passes within reach of that fold - the higher in a valley, the lower
            // under a ridge. Exact: between breakpoints one line governs.
            var segs = new List<OffsetSegment>();
            foreach (SectionPiece s in spans)
            {
                FaceFacet fc = f.Facets[s.Facet];
                Line own = OffsetLine(fc, along, across, up, sec.V, offset);
                var active = new List<Tuple<Line, bool, double, double>>();   // line, ridge, from, to
                foreach (FaceFold fold in fc.Folds)
                {
                    FaceFacet other = f.Facets[fold.Other];
                    double reach = offset / Math.Min(Dot(fc.Normal, up), Dot(other.Normal, up)) + tol + 1.0;
                    var within = new List<double[]>();
                    foreach (double[][] seg in fold.Segments)
                    {
                        double[] i = CapsuleOnLine(seg[0], seg[1], reach, along, across, sec.V);
                        if (i != null) within.Add(i);
                    }
                    foreach (double[] w in MatOpenings.Merge(within))
                    {
                        double lo = Math.Max(w[0], s.U0), hi = Math.Min(w[1], s.U1);
                        if (hi > lo + 1e-9) active.Add(Tuple.Create(OffsetLine(other, along, across, up, sec.V, offset), fold.Ridge, lo, hi));
                    }
                }
                var bps = new List<double> { s.U0, s.U1 };
                var lines = new List<Line> { own };
                foreach (var a in active) { bps.Add(a.Item3); bps.Add(a.Item4); lines.Add(a.Item1); }
                for (int i = 0; i < lines.Count; i++)
                    for (int k = i + 1; k < lines.Count; k++)
                    {
                        double dm = lines[i].M - lines[k].M;
                        if (Math.Abs(dm) < 1e-15) continue;
                        double u = (lines[k].C - lines[i].C) / dm;
                        if (u > s.U0 && u < s.U1) bps.Add(u);
                    }
                bps = bps.Distinct().OrderBy(x => x).ToList();
                for (int i = 0; i + 1 < bps.Count; i++)
                {
                    double a = bps[i], b = bps[i + 1];
                    if (b - a <= 1e-9) continue;
                    double mid = (a + b) / 2;
                    Line gov = own;
                    double h = own.At(mid);
                    foreach (var act in active.Where(x => !x.Item2 && mid >= x.Item3 && mid <= x.Item4))
                        if (act.Item1.At(mid) > h) { h = act.Item1.At(mid); gov = act.Item1; }
                    foreach (var act in active.Where(x => x.Item2 && mid >= x.Item3 && mid <= x.Item4))
                        if (act.Item1.At(mid) < h) { h = act.Item1.At(mid); gov = act.Item1; }
                    OffsetSegment last = segs.Count > 0 ? segs[segs.Count - 1] : null;
                    if (last != null && last.Facet == gov.Facet && Math.Abs(last.C - gov.C) < 1e-9 && Math.Abs(last.M - gov.M) < 1e-12) last.U1 = b;
                    else segs.Add(new OffsetSegment { U0 = a, U1 = b, C = gov.C, M = gov.M, Facet = gov.Facet });
                }
            }

            // CONTINUITY. Inside a span the governing lines meet where they cross; across a fold both
            // sides took the same crease. What is left is a jump, and a bar cannot follow it.
            for (int i = 0; i + 1 < segs.Count; i++)
            {
                OffsetSegment a = segs[i], b = segs[i + 1];
                if (b.U0 - a.U1 > 1e-9) continue;   // an unbridged gap, judged per bar
                double jump = Math.Abs(a.At(a.U1) - b.At(b.U0));
                if (jump > ContinuityToleranceMm)
                {
                    path.Code = MatRules.CodeFaceSteps;
                    path.Why = "component '" + name + "': at station " + Mm(sec.V) + " the face offset by " + Mm(offset) +
                               " jumps by " + Mm(jump) + " at " + Mm(a.U1) + " along the bar, between facets " + a.Facet + " and " +
                               b.Facet + ". Their fold was not found along the section (a fold that ends under the bar, or two " +
                               "faces that meet without sharing an edge), so there is no crease to bend the bar at.";
                    return path;
                }
            }
            path.Segments = segs;
            return path;
        }

        /// <summary>Where the plan capsule of radius r round segment [p, q] meets the line v = const, as [u0, u1].</summary>
        private static double[] CapsuleOnLine(double[] p3, double[] q3, double r, double[] along, double[] across, double v)
        {
            double[] p = { Dot(p3, along), Dot(p3, across) }, q = { Dot(q3, along), Dot(q3, across) };
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double[] c in new[] { p, q })
            {
                double dv = v - c[1];
                if (Math.Abs(dv) > r) continue;
                double half = Math.Sqrt(Math.Max(0, r * r - dv * dv));
                lo = Math.Min(lo, c[0] - half); hi = Math.Max(hi, c[0] + half);
            }
            double eu = q[0] - p[0], ev = q[1] - p[1], len = Math.Sqrt(eu * eu + ev * ev);
            if (len > 1e-12)
            {
                double nu = -ev / len * r, nv = eu / len * r;
                double[][] quad =
                {
                    new[] { p[0] + nu, p[1] + nv }, new[] { q[0] + nu, q[1] + nv },
                    new[] { q[0] - nu, q[1] - nv }, new[] { p[0] - nu, p[1] - nv }
                };
                for (int k = 0; k < 4; k++)
                {
                    double[] a = quad[k], b = quad[(k + 1) % 4];
                    if ((a[1] >= v) != (b[1] >= v) && Math.Abs(b[1] - a[1]) > 1e-15)
                    {
                        double x = a[0] + (v - a[1]) * (b[0] - a[0]) / (b[1] - a[1]);
                        lo = Math.Min(lo, x); hi = Math.Max(hi, x);
                    }
                }
            }
            return lo <= hi ? new[] { lo, hi } : null;
        }

        // ------------------------------------------------------------- the bar

        /// <summary>
        /// The bar from <paramref name="u0"/> to <paramref name="u1"/> on the offset face of one station: its
        /// polyline, its host part (the part most of it lies on) and the parts it runs into - or the refusal.
        /// </summary>
        public static FollowedBar Bar(FacingFace f, FaceSection sec, OffsetPath path, double u0, double u1,
                                      double minFacetMm, string name)
        {
            var bar = new FollowedBar();
            foreach (double[] g in path.Unbridged)
                if (g[1] > u0 + 1e-9 && g[0] < u1 - 1e-9)
                {
                    bar.Code = MatRules.CodeNoFaceUnderBar;
                    bar.Why = "component '" + name + "': at station " + Mm(sec.V) + " the bar from " + Mm(u0) + " to " + Mm(u1) +
                              " runs over a stretch with no face, " + Mm(g[0]) + " to " + Mm(g[1]) + ", whose two sides are " +
                              "different facets. There is nothing to follow across it: declare an openings policy that omits " +
                              "or trims the bars there, or a window that keeps them off it.";
                    return bar;
                }

            var pts = new List<double[]> { new[] { u0, path.At(u0) } };
            var facets = new List<int>();
            foreach (OffsetSegment s in path.Segments)
            {
                if (s.U1 <= u0 + 1e-9 || s.U0 >= u1 - 1e-9) continue;
                facets.Add(s.Facet);
                if (s.U1 < u1 - 1e-9) pts.Add(new[] { s.U1, s.At(s.U1) });
            }
            pts.Add(new[] { u1, path.At(u1) });

            // STRAIGHT-THROUGH VERTICES go: a segment boundary where the line does not turn is not a bend.
            var keepPts = new List<double[]> { pts[0] };
            var keepFacets = new List<int>();
            int segIndex = 0;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                double[] a = keepPts[keepPts.Count - 1], b = pts[i], c = pts[i + 1];
                double dev = DistanceToLine(b, a, c);
                if (dev <= 1e-6) { segIndex++; continue; }
                keepPts.Add(b);
                keepFacets.Add(facets[Math.Min(segIndex, facets.Count - 1)]);
                segIndex++;
            }
            keepPts.Add(pts[pts.Count - 1]);
            keepFacets.Add(facets.Count > 0 ? facets[facets.Count - 1] : -1);
            bar.Uh = keepPts;
            bar.Facets = keepFacets;

            // A BEND AT A TESSELLATION is not a break of the face.
            for (int i = 1; i < keepPts.Count - 1; i++)
            {
                int fa = keepFacets[i - 1], fb = keepFacets[i];
                FaceFacet a = fa >= 0 ? f.Facets[fa] : null, b = fb >= 0 ? f.Facets[fb] : null;
                if ((a != null && (a.Curved || a.CurvedUnknown)) || (b != null && (b.Curved || b.CurvedUnknown)))
                {
                    double angle = a != null && b != null ? Math.Acos(Math.Max(-1, Math.Min(1, Dot(a.Normal, b.Normal)))) * 180 / Math.PI : 0;
                    bar.Code = MatRules.CodeFaceNotPlanar;
                    bar.Why = "component '" + name + "': at station " + Mm(sec.V) + " the bar would bend at " + Mm(keepPts[i][0]) +
                              " between facets " + fa + " and " + fb + ", " + Math.Round(angle, 4).ToString(CultureInfo.InvariantCulture) +
                              " degrees apart, and " + ((a != null && a.CurvedUnknown) || (b != null && b.CurvedUnknown)
                                  ? "the host mesh has a curved face whose triangles were not attributed, so whether this is a real break is unknown"
                                  : "at least one of them is the tessellation of a CURVED face") +
                              ". Following it would bend the bar at the triangulation, not at the face, and the offset would be " +
                              "honest only to the chord tolerance. Widen planarity_mm if the face is meant to be treated as planar " +
                              "within it, or reinforce the curved face otherwise.";
                    return bar;
                }
            }
            // A FACET TOO SMALL to bend onto.
            if (keepPts.Count > 2)
                for (int i = 0; i + 1 < keepPts.Count; i++)
                {
                    double du = keepPts[i + 1][0] - keepPts[i][0], dh = keepPts[i + 1][1] - keepPts[i][1];
                    double len = Math.Sqrt(du * du + dh * dh);
                    if (len < minFacetMm - 1e-9)
                    {
                        bar.Code = MatRules.CodeFacetTooSmall;
                        bar.Why = "component '" + name + "': at station " + Mm(sec.V) + " the bar's stretch from " + Mm(keepPts[i][0]) +
                                  " to " + Mm(keepPts[i + 1][0]) + " along it, on facet " + keepFacets[i] + ", is " + Mm(len) +
                                  " long, shorter than the declared min_facet_mm of " + Mm(minFacetMm) + ". A curved face arrives as " +
                                  "many short facets; bending a bar onto each of them follows the triangulation. Move the bar's " +
                                  "ends (end cover, run window) off the short facet, or declare a smaller minimum if it is real.";
                        return bar;
                    }
                }

            // WHICH PART HOSTS IT: the one most of it lies on; ties go to the earlier part, the host first.
            var lengths = new Dictionary<int, double>();
            foreach (SectionPiece p in sec.Pieces)
            {
                double lo = Math.Max(p.U0, u0), hi = Math.Min(p.U1, u1);
                if (hi <= lo + 1e-9) continue;
                double had;
                lengths[p.Part] = (lengths.TryGetValue(p.Part, out had) ? had : 0) + (hi - lo);
            }
            bar.HostPart = lengths.Count == 0 ? 0 : lengths.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;
            bar.IntoParts = lengths.Keys.Where(k => k != bar.HostPart).OrderBy(k => k).ToList();
            return bar;
        }

        private static double DistanceToLine(double[] p, double[] a, double[] b)
        {
            double du = b[0] - a[0], dh = b[1] - a[1], len = Math.Sqrt(du * du + dh * dh);
            if (len < 1e-12) return Math.Sqrt((p[0] - a[0]) * (p[0] - a[0]) + (p[1] - a[1]) * (p[1] - a[1]));
            return Math.Abs((p[0] - a[0]) * dh - (p[1] - a[1]) * du) / len;
        }

        /// <summary>Two polylines with the same vertices to within the tolerance: the worst difference, or NaN when they differ in count.</summary>
        public static double ShapeDifference(List<double[]> a, List<double[]> b)
        {
            if (a == null || b == null || a.Count != b.Count) return double.NaN;
            double worst = 0;
            for (int i = 0; i < a.Count; i++)
                worst = Math.Max(worst, Math.Max(Math.Abs(a[i][0] - b[i][0]), Math.Abs(a[i][1] - b[i][1])));
            return worst;
        }

        /// <summary>A polyline point (u, h) at station v, in model millimetres.</summary>
        public static double[] Model(double[] along, double[] across, double[] up, double u, double v, double h)
        {
            return new[]
            {
                along[0] * u + across[0] * v + up[0] * h,
                along[1] * u + across[1] * v + up[1] * h,
                along[2] * u + across[2] * v + up[2] * h
            };
        }

        // ------------------------------------------------------------- vectors

        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double[] Add(double[] a, double[] b) { return new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
        private static double[] Scale(double[] a, double k) { return new[] { a[0] * k, a[1] * k, a[2] * k }; }
        private static double Norm(double[] a) { return Math.Sqrt(Dot(a, a)); }
        private static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }
        private static double[] AnyPerpendicular(double[] n)
        {
            double[] probe = Math.Abs(n[0]) < 0.9 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
            return RebarContainment.Unit(Cross(n, probe));
        }
        private static string Mm(double v) { return Math.Round(v, 3).ToString(CultureInfo.InvariantCulture) + " mm"; }
    }
}
