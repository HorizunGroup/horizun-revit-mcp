// -----------------------------------------------------------------------------
// Horizun Revit MCP - area reinforcement, path reinforcement and fabric (acero 3.0, L4b).
// Original Horizun code. No Revit types.
//
// Three Revit systems that each take an outline or a path on ONE face of ONE host
// and a handful of numbers, and then lay the bars themselves. What this bridge can
// still answer for is everything around that: that the outline is a polygon at all,
// that it lies on a face of the host's own boundary rather than somewhere in the
// concrete, that the major direction lies in that face - and, after the commit,
// that every parameter it set reads back as it was declared.
//
// The layer vocabulary is Revit's own, split the way Revit splits it: a floor has
// top and bottom layers, a wall has exterior and interior ones (RevitAPI.xml:
// REBAR_SYSTEM_*_FRONT_* is "Exterior", *_BACK_* is "Interior", DIR_1 is "Major",
// DIR_2 is "Minor"). A slab word on a wall is refused rather than translated:
// which face of a wall is its "top" is not a question this bridge answers.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Where a boundary or a path sits on a host's boundary mesh.</summary>
    public sealed class FaceFit
    {
        public bool Ok;
        public string Code;
        public string Why;
        /// <summary>The OUTWARD normal of the face the points lie on, unit length. Null when not Ok.</summary>
        public double[] NormalMm;
        /// <summary>The face plane's offset along that normal, in millimetres.</summary>
        public double OffsetMm;
    }

    /// <summary>One declared layer of an area system: the bar type it was resolved to and its spacing.</summary>
    public sealed class SystemLayerWant
    {
        public long BarTypeId;
        public double SpacingMm;
    }

    /// <summary>One layer of an area system as the model reports it. Null fields are unreadable.</summary>
    public sealed class SystemLayerRead
    {
        public bool? Active;
        public double? SpacingMm;
        public long? BarTypeId;
        /// <summary>-1 for no hook.</summary>
        public long? HookTypeId;
    }

    public static class SystemReinforcementRules
    {
        public const string KindArea = "area";
        public const string KindPath = "path";
        public const string KindFabric = "fabric";

        public const string FamilySlab = "slab";
        public const string FamilyWall = "wall";

        public const string TopMajor = "top_major", TopMinor = "top_minor",
                            BottomMajor = "bottom_major", BottomMinor = "bottom_minor";
        public const string ExteriorMajor = "exterior_major", ExteriorMinor = "exterior_minor",
                            InteriorMajor = "interior_major", InteriorMinor = "interior_minor";

        /// <summary>In Revit's own order: top/front major, top/front minor, bottom/back major, bottom/back minor.</summary>
        public static readonly string[] SlabLayers = { TopMajor, TopMinor, BottomMajor, BottomMinor };
        public static readonly string[] WallLayers = { ExteriorMajor, ExteriorMinor, InteriorMajor, InteriorMinor };

        // Refusal codes this file emits, closed.
        public const string CodeNotOnHostFace = "boundary_not_on_a_host_face";
        public const string CodeOutsideHostFace = "boundary_outside_the_host_face";
        public const string CodeMajorDirectionNotInFace = "major_direction_not_in_the_host_face";
        public const string CodeLayerNotOnThisHost = "area_layer_not_on_this_host";
        public const string CodeHostNotFloorOrWall = "system_host_not_a_floor_or_wall";

        public static readonly string[] AllCodes =
        {
            CodeNotOnHostFace, CodeOutsideHostFace, CodeMajorDirectionNotInFace, CodeLayerNotOnThisHost,
            CodeHostNotFloorOrWall
        };

        /// <summary>
        /// The refusal for a system type the set names and the document does not have, naming the
        /// types it DOES have (sorted, first twelve) - or that it has none of that kind at all.
        /// </summary>
        public static string TypeNotFoundWhy(string what, string name, IEnumerable<string> available)
        {
            List<string> names = (available ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            string head = "no " + what + " in this document is named '" + name + "'";
            string tail = ". This bridge does not create one: a type carries settings somebody is answerable for.";
            if (names.Count == 0) return head + ", and this document has no " + what + " at all" + tail;
            const int shown = 12;
            return head + "; it has: " + string.Join(", ", names.Take(shown).Select(n => "'" + n + "'")) +
                   (names.Count > shown ? " and " + (names.Count - shown) + " more" : "") + tail;
        }

        public static string FamilyOfLayer(string name)
        {
            if (SlabLayers.Contains(name, StringComparer.Ordinal)) return FamilySlab;
            if (WallLayers.Contains(name, StringComparer.Ordinal)) return FamilyWall;
            return null;
        }

        public static string[] LayersOf(string family)
        {
            return family == FamilyWall ? WallLayers : family == FamilySlab ? SlabLayers : new string[0];
        }

        /// <summary>The position of a layer in Revit's order (0..3), or -1.</summary>
        public static int SlotOf(string name)
        {
            int i = Array.IndexOf(SlabLayers, name);
            return i >= 0 ? i : Array.IndexOf(WallLayers, name);
        }

        // ------------------------------------------------------------ vectors

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double Len(double[] a) => Math.Sqrt(Dot(a, a));

        public static double[] Unit(double[] v)
        {
            if (v == null || v.Length != 3) return null;
            double l = Len(v);
            if (!(l > 1e-12) || double.IsInfinity(l)) return null;
            return new[] { v[0] / l, v[1] / l, v[2] / l };
        }

        private static string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>The Newell normal of a polygon, unit length, or null when it encloses no area.</summary>
        public static double[] NewellNormal(IList<double[]> pts)
        {
            if (pts == null || pts.Count < 3) return null;
            var n = new double[3];
            for (int i = 0; i < pts.Count; i++)
            {
                double[] a = pts[i], b = pts[(i + 1) % pts.Count];
                n[0] += (a[1] - b[1]) * (a[2] + b[2]);
                n[1] += (a[2] - b[2]) * (a[0] + b[0]);
                n[2] += (a[0] - b[0]) * (a[1] + b[1]);
            }
            // Twice the enclosed area; under a square millimetre encloses nothing.
            return Len(n) < 2.0 ? null : Unit(n);
        }

        private static double[] Centroid(IList<double[]> pts)
        {
            var c = new double[3];
            foreach (double[] p in pts) { c[0] += p[0]; c[1] += p[1]; c[2] += p[2]; }
            return new[] { c[0] / pts.Count, c[1] / pts.Count, c[2] / pts.Count };
        }

        /// <summary>
        /// A plane through a polyline: the first three points that are not in a line. Null when
        /// every point is on one line, which every plane through that line contains.
        /// </summary>
        public static double[] PolylineNormal(IList<double[]> pts)
        {
            if (pts == null || pts.Count < 3) return null;
            for (int i = 1; i < pts.Count - 1; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double[] n = Cross(Sub(pts[i], pts[0]), Sub(pts[j], pts[0]));
                    if (Len(n) > 1e-6 * Math.Max(1.0, Len(Sub(pts[i], pts[0])) * Len(Sub(pts[j], pts[0]))))
                        return Unit(n);
                }
            return null;
        }

        /// <summary>Does a direction lie in the plane whose normal this is, within an angle?</summary>
        public static bool InPlane(double[] direction, double[] normal, double angleDegrees)
        {
            double[] d = Unit(direction), n = Unit(normal);
            if (d == null || n == null) return false;
            return Math.Abs(Dot(d, n)) <= Math.Sin(angleDegrees * Math.PI / 180.0) + 1e-12;
        }

        // ------------------------------------------------------- the polygon

        /// <summary>
        /// A boundary: at least three points, no point repeated anywhere (closure is implied
        /// and the first point is NOT written again), enclosing an area, every point within
        /// the length tolerance of its plane, and no edge crossing another. Null when it is
        /// all of those; otherwise the reason.
        /// </summary>
        public static string CheckPolygon(IList<double[]> pts, double toleranceMm, string at)
        {
            if (pts == null || pts.Count < 3)
                return at + " needs at least three points: a boundary encloses an area, and closure is implied.";
            string rep = Repeated(pts, at, true);
            if (rep != null) return rep;
            // THE PLANE OF THE FIRST THREE POINTS NOT IN A LINE, for planarity and for the
            // crossing test. Not the Newell normal: a bow tie's two halves cancel in it, and
            // the outline would be called empty when it is in fact crossed.
            double[] n = PolylineNormal(pts);
            if (n == null)
                return at + " encloses no area: its points are on one line.";
            for (int i = 0; i < pts.Count; i++)
            {
                double off = Math.Abs(Dot(Sub(pts[i], pts[0]), n));
                if (off > toleranceMm)
                    return at + " is not in one plane: point " + i + " is " + Mm(off) + " mm off the plane of the " +
                           "others, and the tolerance is " + Mm(toleranceMm) + " mm. A boundary lies on ONE face.";
            }
            // A SELF-CROSSING OUTLINE encloses two areas with opposite senses, and what Revit
            // makes of one is not something to find out in the model.
            double[] u, v;
            Basis(n, out u, out v);
            var flat = pts.Select(p => new[] { Dot(p, u), Dot(p, v) }).ToList();
            int m = flat.Count;
            for (int i = 0; i < m; i++)
                for (int j = i + 1; j < m; j++)
                {
                    // Adjacent edges share a corner; that is not a crossing.
                    if (j == i + 1 || (i == 0 && j == m - 1)) continue;
                    if (SegmentsMeet(flat[i], flat[(i + 1) % m], flat[j], flat[(j + 1) % m]))
                        return at + " crosses itself: edge " + i + " meets edge " + j + ". A boundary is one " +
                               "simple outline, its points in order round it.";
                }
            if (NewellNormal(pts) == null)
                return at + " encloses no area.";
            return null;
        }

        /// <summary>
        /// A path: at least two points, open (the last is not the first), no point repeated,
        /// and every point within the length tolerance of one plane.
        /// </summary>
        public static string CheckPolyline(IList<double[]> pts, double toleranceMm, string at)
        {
            if (pts == null || pts.Count < 2)
                return at + " needs at least two points: a path runs from one to the next.";
            string rep = Repeated(pts, at, false);
            if (rep != null) return rep;
            double[] n = PolylineNormal(pts);
            if (n == null) return null;   // every point on one line: in a plane by construction
            for (int i = 0; i < pts.Count; i++)
            {
                double off = Math.Abs(Dot(Sub(pts[i], pts[0]), n));
                if (off > toleranceMm)
                    return at + " is not in one plane: point " + i + " is " + Mm(off) + " mm off the plane of the " +
                           "first three, and the tolerance is " + Mm(toleranceMm) + " mm. A path lies on ONE face.";
            }
            return null;
        }

        private static string Repeated(IList<double[]> pts, string at, bool polygon)
        {
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                    if (Len(Sub(pts[i], pts[j])) < 1e-6)
                        return at + " repeats a point: " + i + " and " + j + " are the same point. " +
                               (polygon ? "Closure is implied; do not write the first point again at the end."
                                        : "A path is open and visits each point once.");
            return null;
        }

        private static void Basis(double[] n, out double[] u, out double[] v)
        {
            double[] a = Math.Abs(n[0]) < 0.9 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
            u = Unit(Cross(n, a));
            v = Cross(n, u);
        }

        private static double Orient(double[] a, double[] b, double[] c) =>
            (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);

        private static bool SegmentsMeet(double[] p1, double[] p2, double[] q1, double[] q2)
        {
            double d1 = Orient(q1, q2, p1), d2 = Orient(q1, q2, p2), d3 = Orient(p1, p2, q1), d4 = Orient(p1, p2, q2);
            const double e = 1e-9;
            if (((d1 > e && d2 < -e) || (d1 < -e && d2 > e)) && ((d3 > e && d4 < -e) || (d3 < -e && d4 > e))) return true;
            return (Math.Abs(d1) <= e && OnSeg(q1, q2, p1)) || (Math.Abs(d2) <= e && OnSeg(q1, q2, p2)) ||
                   (Math.Abs(d3) <= e && OnSeg(p1, p2, q1)) || (Math.Abs(d4) <= e && OnSeg(p1, p2, q2));
        }

        private static bool OnSeg(double[] a, double[] b, double[] p) =>
            p[0] >= Math.Min(a[0], b[0]) - 1e-9 && p[0] <= Math.Max(a[0], b[0]) + 1e-9 &&
            p[1] >= Math.Min(a[1], b[1]) - 1e-9 && p[1] <= Math.Max(a[1], b[1]) + 1e-9;

        // --------------------------------------------- on a face of the host

        /// <summary>
        /// Do these points lie on ONE face of the host's boundary? A face is the set of
        /// boundary triangles in one plane facing one way. Every point must be within the
        /// length tolerance of that plane AND inside (or within the tolerance of) one of
        /// its triangles. Returns the face's outward normal, so the caller can hold a
        /// direction to it.
        ///
        /// VERTICES ARE TESTED, NOT EDGES: an outline whose corners are all on a face and
        /// whose edge crosses a notch or an opening of that face is not caught here. That
        /// gap is named in the audit's not_checked list as area_reinforcement_geometry.
        /// </summary>
        public static FaceFit OnHostFace(IList<double[]> pts, HostMesh mesh, double toleranceMm, double angleDegrees)
        {
            var fit = new FaceFit();
            if (mesh == null || mesh.Triangles.Count == 0 || pts == null || pts.Count == 0)
            {
                fit.Code = CodeNotOnHostFace;
                fit.Why = "the host's boundary is not available, so no face could be found for these points.";
                return fit;
            }
            double cosTol = Math.Cos(angleDegrees * Math.PI / 180.0);
            var tris = new List<Tri>();
            foreach (int[] t in mesh.Triangles)
            {
                double[] a = mesh.Vertices[t[0]], b = mesh.Vertices[t[1]], c = mesh.Vertices[t[2]];
                double[] n = Unit(Cross(Sub(b, a), Sub(c, a)));
                if (n == null) continue;
                tris.Add(new Tri { A = a, B = b, C = c, N = n, D = Dot(n, a) });
            }

            // Every distinct plane that ALL the points lie in.
            var planes = new List<Tri>();
            foreach (Tri t in tris)
            {
                if (planes.Any(p => Dot(p.N, t.N) >= cosTol && Math.Abs(p.D - t.D) <= toleranceMm)) continue;
                if (pts.All(p => Math.Abs(Dot(t.N, p) - t.D) <= toleranceMm)) planes.Add(t);
            }
            if (planes.Count == 0)
            {
                fit.Code = CodeNotOnHostFace;
                fit.Why = "the points do not all lie in the plane of any face of the host (tolerance " +
                          Mm(toleranceMm) + " mm). A boundary or a path is drawn ON a face of the host - its top or " +
                          "bottom for a floor, one of its sides for a wall - not inside the concrete or beside it.";
                return fit;
            }

            string firstOutside = null;
            foreach (Tri plane in planes)
            {
                List<Tri> face = tris.Where(t => Dot(t.N, plane.N) >= cosTol && Math.Abs(t.D - plane.D) <= toleranceMm).ToList();
                int outside = -1;
                for (int i = 0; i < pts.Count && outside < 0; i++)
                    if (!face.Any(t => NearTriangle(pts[i], t, toleranceMm))) outside = i;
                if (outside < 0)
                {
                    fit.Ok = true;
                    fit.NormalMm = plane.N;
                    fit.OffsetMm = plane.D;
                    return fit;
                }
                if (firstOutside == null)
                    firstOutside = "point " + outside + " lies in the plane of the host face whose outward normal is [" +
                                   string.Join(", ", plane.N.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) +
                                   "] but outside that face, by more than " + Mm(toleranceMm) + " mm.";
            }
            fit.Code = CodeOutsideHostFace;
            fit.Why = firstOutside + " Revit would place the system partly off its host, or refuse it.";
            return fit;
        }

        private sealed class Tri
        {
            public double[] A, B, C, N;
            public double D;
        }

        /// <summary>A point's projection on the triangle's plane is inside it, or within tolerance of an edge.</summary>
        private static bool NearTriangle(double[] p, Tri t, double tol)
        {
            double h = Dot(t.N, p) - t.D;
            double[] q = { p[0] - h * t.N[0], p[1] - h * t.N[1], p[2] - h * t.N[2] };
            double s1 = Dot(Cross(Sub(t.B, t.A), Sub(q, t.A)), t.N);
            double s2 = Dot(Cross(Sub(t.C, t.B), Sub(q, t.B)), t.N);
            double s3 = Dot(Cross(Sub(t.A, t.C), Sub(q, t.C)), t.N);
            if ((s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0)) return true;
            return SegDist(q, t.A, t.B) <= tol || SegDist(q, t.B, t.C) <= tol || SegDist(q, t.C, t.A) <= tol;
        }

        private static double SegDist(double[] p, double[] a, double[] b)
        {
            double[] ab = Sub(b, a);
            double l2 = Dot(ab, ab);
            double k = l2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, Dot(Sub(p, a), ab) / l2));
            return Len(Sub(p, new[] { a[0] + k * ab[0], a[1] + k * ab[1], a[2] + k * ab[2] }));
        }

        // ------------------------------------------------- read-back checks
        //
        // ONE SHAPE for every check, the same one the rebar apply uses:
        // { verified, requested, read, why? }. A value the model would not give is
        // `verified: false` with a why - never true, because unknown is not a pass.

        public static JObject Check(bool ok, object requested, object read, string why = null)
        {
            var o = new JObject
            {
                ["verified"] = ok,
                ["requested"] = requested == null ? JValue.CreateNull() : JToken.FromObject(requested),
                ["read"] = read == null ? JValue.CreateNull() : JToken.FromObject(read)
            };
            if (why != null) o["why"] = why;
            return o;
        }

        /// <summary>A question that does not arise. verified is null, and applicable false says why that is not a gap.</summary>
        public static JObject NotApplicable(string why, object read = null)
        {
            var o = new JObject { ["verified"] = JValue.CreateNull(), ["applicable"] = false, ["why"] = why };
            if (read != null) o["read"] = JToken.FromObject(read);
            return o;
        }

        public static JObject CheckId(long requested, long? read)
        {
            return Check(read.HasValue && read.Value == requested, requested, read,
                         read.HasValue ? null : "the model would not report this value.");
        }

        public static JObject CheckLengthMm(double requested, double? read, double toleranceMm)
        {
            bool ok = read.HasValue && RebarLayoutRules.IsFinite(read.Value) && Math.Abs(read.Value - requested) <= toleranceMm;
            return Check(ok, Math.Round(requested, 3), read.HasValue ? (object)Math.Round(read.Value, 3) : null,
                         read.HasValue ? null : "the model would not report this length.");
        }

        public static JObject CheckFlag(bool requested, bool? read)
        {
            return Check(read.HasValue && read.Value == requested, requested, read,
                         read.HasValue ? null : "the model would not report this flag.");
        }

        public static JObject CheckText(string requested, string read)
        {
            return Check(string.Equals(requested, read, StringComparison.Ordinal), requested, read);
        }

        public static JObject CheckAtLeast(int minimum, int? read)
        {
            return Check(read.HasValue && read.Value >= minimum, ">= " + minimum, read,
                         read.HasValue ? null : "the model would not say how many it produced.");
        }

        /// <summary>
        /// A direction compared as an AXIS: a major direction of +X and one of -X lay the
        /// same bars, so either sense agrees. Unreadable is not verified.
        /// </summary>
        public static JObject CheckDirection(double[] requested, double[] read, double angleDegrees)
        {
            double[] a = Unit(requested), b = Unit(read);
            bool ok = a != null && b != null && Math.Abs(Dot(a, b)) >= Math.Cos(angleDegrees * Math.PI / 180.0) - 1e-12;
            return Check(ok, requested, read == null ? null : read.Select(x => Math.Round(x, 6)).ToArray(),
                         read == null ? "the model would not report this direction." : null);
        }

        /// <summary>
        /// Every layer slot of the host's family, against what was declared. A declared
        /// layer must read active, with its bar type, its spacing and no hook; a layer that
        /// was NOT declared must read inactive - the set asked for those layers and no
        /// others. Keys are flat: "top_major.spacing_mm".
        /// </summary>
        public static JObject AreaLayerChecks(string family, IDictionary<string, SystemLayerWant> want,
                                              IDictionary<string, SystemLayerRead> read, double spacingToleranceMm)
        {
            var checks = new JObject();
            foreach (string slot in LayersOf(family))
            {
                SystemLayerRead r;
                if (read == null || !read.TryGetValue(slot, out r) || r == null) r = new SystemLayerRead();
                SystemLayerWant w;
                bool declared = want != null && want.TryGetValue(slot, out w) && w != null;
                if (!declared)
                {
                    checks[slot + ".active"] = CheckFlag(false, r.Active);
                    continue;
                }
                w = want[slot];
                checks[slot + ".active"] = CheckFlag(true, r.Active);
                checks[slot + ".bar_type_id"] = CheckId(w.BarTypeId, r.BarTypeId);
                checks[slot + ".spacing_mm"] = CheckLengthMm(w.SpacingMm, r.SpacingMm, spacingToleranceMm);
                // NO HOOK: this schema declares none, the system is created with none, and a
                // hook that appeared would change every bar in the layer.
                checks[slot + ".hook_type_id"] = CheckId(-1, r.HookTypeId);
            }
            return checks;
        }

        /// <summary>
        /// True only when there is at least one check and every one of them is verified,
        /// or explicitly not applicable. A check whose verified is null WITHOUT
        /// applicable:false is unknown, and unknown is not a pass.
        /// </summary>
        public static bool AllVerified(JObject checks)
        {
            if (checks == null) return false;
            int counted = 0;
            foreach (JProperty p in checks.Properties())
            {
                var c = p.Value as JObject;
                if (c == null) return false;
                JToken v = c["verified"];
                if (v == null) return false;
                if (v.Type == JTokenType.Boolean)
                {
                    if (!(bool)v) return false;
                    counted++;
                    continue;
                }
                if (v.Type == JTokenType.Null && c["applicable"] != null && c["applicable"].Type == JTokenType.Boolean &&
                    !(bool)c["applicable"])
                    continue;
                return false;
            }
            return counted > 0;
        }
    }
}
