// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_measure_geometry (read-only).
//
// Field data (2026-10-19..23, 398 horizun_execute_python samples, 10.3 % error): the same
// purposes were rewritten in Python each time - volume of a solid, the section of a beam,
// the end points of a run, the normals of a face. This file holds the arithmetic so it is
// provable without Revit:
//
//  - the request: ids or a selector, which measures, which length unit;
//  - the section profile: the width and height of a prismatic member measured PERPENDICULAR
//    to its axis, from the vertices of its solid (a 300 x 500 beam reads 300 x 500 however
//    it is rotated in plan);
//  - the distinct face normals with their area;
//  - the reasons an element cannot be read. A value that cannot be read is null with one of
//    these reasons and the element lands in `unreadable`; it is NEVER 0.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class MeasureKinds
    {
        public const string Summary = "summary";
        public const string SolidVolume = "solid_volume";
        public const string SectionProfile = "section_profile";
        public const string BoundingBox = "bounding_box";
        public const string EndPoints = "end_points";
        public const string FaceNormals = "face_normals";
        // 2026-10 (field: "read the selection and its geometry" was over 40 % of Python purposes).
        public const string Faces = "faces";
        public const string Edges = "edges";
        public const string Location = "location";
        public static readonly string[] All = { Summary, SolidVolume, SectionProfile, BoundingBox, EndPoints, FaceNormals, Faces, Edges, Location };
    }

    public static class UnreadableReason
    {
        public const string ElementNotFound = "element_not_found";
        public const string NoSolidGeometry = "no_solid_geometry";
        public const string NoBoundingBox = "no_bounding_box";
        public const string NoAxis = "no_axis";
        public const string NoLocation = "no_location";
        public const string NoPlanarFaces = "no_planar_faces";
        public const string DegenerateProfile = "degenerate_profile";
        public const string ReadException = "read_exception";
    }

    public sealed class MeasureRequest
    {
        public List<long> Ids = new List<long>();
        public SelectorSpec Selector;
        public List<string> Measures = new List<string>();
        public string Unit = "mm";
        public double LengthFromFeet = 304.8;
        /// <summary>true: the ids are Revit's current selection, read when the call runs.</summary>
        public bool Selection;
        public int Offset;
        public int PageSize = 100;
    }

    public static class WhatIfActions
    {
        public const string Delete = "delete";
        public const string Join = "join";
        public const string Unjoin = "unjoin";
        public const string Move = "move";
        public static readonly string[] All = { Delete, Join, Unjoin, Move };
    }

    public sealed class WhatIfSpec
    {
        public string Action;
        public List<long> Ids = new List<long>();
        /// <summary>move only: the translation in FEET (converted from the request unit).</summary>
        public double[] VectorFeet;
    }

    public sealed class SectionProfileResult
    {
        public bool Ok;
        public double WidthFeet;   // along axis x vertical-reference (horizontal for a horizontal member)
        public double HeightFeet;  // perpendicular to both
        public double LengthFeet;  // along the axis
        public string Reason;
    }

    public sealed class NormalGroup
    {
        public double[] Normal;
        public double AreaSquareFeet;
        public int Faces;
    }

    public static class GeometryMeasureRules
    {
        public const int MaxIds = 500;
        public const int MaxSelected = 2000;
        public const double CubicMetresPerCubicFoot = 0.028316846592;
        public const double SquareMetresPerSquareFoot = 0.09290304;

        public static double SquareMillimetres(double value) => value * 304.8 * 304.8;
        public static double CubicMillimetres(double value) => value * 304.8 * 304.8 * 304.8;
        public static (int Start, int Count, int? Next) Page(int total, int offset, int size)
        {
            int start = Math.Min(total, offset), count = Math.Min(size, total - start);
            return (start, count, start + count < total ? (int?)(start + count) : null);
        }
        public static bool TryUnit(string unit, out double lengthFromFeet)
        {
            switch ((unit ?? "mm").Trim().ToLowerInvariant())
            {
                case "mm": lengthFromFeet = 304.8; return true;
                case "m": lengthFromFeet = 0.3048; return true;
                case "feet": lengthFromFeet = 1.0; return true;
                default: lengthFromFeet = 0; return false;
            }
        }

        public static bool TryParse(JObject request, out MeasureRequest parsed, out string error)
        {
            parsed = null; error = null;
            var r = new MeasureRequest();
            JToken idsTok = request["ids"];
            if (idsTok != null && idsTok.Type != JTokenType.Null)
            {
                var arr = idsTok as JArray;
                if (arr == null) { error = "ids must be an array of integer element ids."; return false; }
                foreach (JToken t in arr)
                {
                    if (t.Type != JTokenType.Integer) { error = "ids contains a value that is not an integer element id: " + t; return false; }
                    long id = (long)t;
                    if (!r.Ids.Contains(id)) r.Ids.Add(id);
                }
                if (r.Ids.Count > MaxIds) { error = "ids carries " + r.Ids.Count + " ids; the limit is " + MaxIds + ". Use selector or split the call."; return false; }
            }
            JToken selTok = request["selector"];
            if (selTok != null && selTok.Type != JTokenType.Null)
                if (!ElementSelectorRules.TryParse(selTok, out r.Selector, out error)) return false;
            JToken selTrue = request["selection"];
            if (selTrue != null && selTrue.Type != JTokenType.Null)
            {
                if (selTrue.Type != JTokenType.Boolean) { error = "selection must be true or false."; return false; }
                r.Selection = (bool)selTrue;
            }
            r.Unit = ((string)request["unit"] ?? "mm").Trim().ToLowerInvariant();
            if (!TryUnit(r.Unit, out r.LengthFromFeet)) { error = "unit must be mm, m or feet."; return false; }
            int sources = (r.Ids.Count > 0 ? 1 : 0) + (r.Selector != null ? 1 : 0) + (r.Selection ? 1 : 0);
            if (sources > 1) { error = "send ids or selector or selection=true, not both (one source of elements)."; return false; }
            if (sources == 0) { error = "send ids or selector (or selection=true): this tool never measures everything by default."; return false; }

            JArray measures = request["measures"] as JArray;
            if (measures == null || measures.Count == 0) { error = "measures is required: one or more of " + string.Join(", ", MeasureKinds.All) + "."; return false; }
            foreach (JToken t in measures)
            {
                string m = ((string)t ?? "").Trim().ToLowerInvariant();
                if (Array.IndexOf(MeasureKinds.All, m) < 0) { error = "measure '" + t + "' is not one of " + string.Join(", ", MeasureKinds.All) + "."; return false; }
                if (!r.Measures.Contains(m)) r.Measures.Add(m);
            }
            if (request["offset"] != null && (request["offset"].Type != JTokenType.Integer || (long)request["offset"] < 0 || (long)request["offset"] > int.MaxValue)) { error = "offset must be a non-negative integer."; return false; }
            if (request["page_size"] != null && (request["page_size"].Type != JTokenType.Integer || (long)request["page_size"] < 1 || (long)request["page_size"] > 100)) { error = "page_size must be between 1 and 100."; return false; }
            r.Offset = (int?)request["offset"] ?? 0; r.PageSize = (int?)request["page_size"] ?? 100;
            if ((request["offset"] != null || request["page_size"] != null) && !r.Measures.Contains(MeasureKinds.Summary)) { error = "pagination requires summary."; return false; }
            parsed = r;
            return true;
        }

        /// <summary>
        /// Width and height of a prismatic member from the vertices of its solid, measured in the
        /// plane perpendicular to the axis. Width runs along axis x vertical (the horizontal
        /// direction across a horizontal member); height is perpendicular to both. For a vertical
        /// axis the reference is the X axis. Units are the caller's (feet in Revit).
        /// </summary>
        public static SectionProfileResult SectionProfile(IList<double[]> points, double[] axis)
        {
            var res = new SectionProfileResult();
            if (points == null || points.Count < 4) { res.Reason = UnreadableReason.NoSolidGeometry; return res; }
            double[] a = Normalize(axis);
            if (a == null) { res.Reason = UnreadableReason.NoAxis; return res; }
            double[] up = Math.Abs(a[2]) > 0.99 ? new[] { 1.0, 0, 0 } : new[] { 0, 0, 1.0 };
            double[] u = Normalize(Cross(a, up));
            double[] v = u == null ? null : Normalize(Cross(u, a));
            if (v == null) { res.Reason = UnreadableReason.NoAxis; return res; }
            Extent(points, u, out double uMin, out double uMax);
            Extent(points, v, out double vMin, out double vMax);
            Extent(points, a, out double aMin, out double aMax);
            res.WidthFeet = uMax - uMin;
            res.HeightFeet = vMax - vMin;
            res.LengthFeet = aMax - aMin;
            if (res.WidthFeet <= 1e-9 || res.HeightFeet <= 1e-9) { res.Reason = UnreadableReason.DegenerateProfile; return res; }
            res.Ok = true;
            return res;
        }

        public static List<NormalGroup> GroupNormals(IEnumerable<KeyValuePair<double[], double>> faces, double angleToleranceDegrees = 0.5)
        {
            var groups = new List<NormalGroup>();
            double cosTol = Math.Cos(angleToleranceDegrees * Math.PI / 180.0);
            foreach (var f in faces)
            {
                double[] n = Normalize(f.Key);
                if (n == null) continue;
                NormalGroup g = groups.FirstOrDefault(x => Dot(x.Normal, n) >= cosTol);
                if (g == null) groups.Add(new NormalGroup { Normal = n, AreaSquareFeet = f.Value, Faces = 1 });
                else { g.AreaSquareFeet += f.Value; g.Faces++; }
            }
            return groups.OrderByDescending(g => g.AreaSquareFeet).ToList();
        }

        /// <summary>
        /// The host-coordinate envelope of a box read in a LINK document: its eight corners are
        /// carried through the link's transform and enveloped again. Carrying only min and max
        /// would be wrong as soon as the link is rotated.
        /// </summary>
        public static void HostEnvelope(double[] min, double[] max, Func<double[], double[]> toHost,
                                        out double[] hostMin, out double[] hostMax)
        {
            SelectionContextRules.Envelope(SelectionContextRules.Corners(min, max).Select(toHost), out hostMin, out hostMax);
        }

        /// <summary>
        /// A point family's rotation in host terms: its own rotation inside the link plus the link's
        /// rotation about Z (read from the transform's X basis), normalised to (-180, 180].
        /// </summary>
        public static double HostRotationDegrees(double localDegrees, double basisXx, double basisXy)
        {
            double d = localDegrees + Math.Atan2(basisXy, basisXx) * 180.0 / Math.PI;
            d %= 360.0;
            if (d <= -180.0) d += 360.0;
            if (d > 180.0) d -= 360.0;
            return Math.Round(d, 4);
        }

        public static double[] Normalize(double[] v)
        {
            if (v == null || v.Length != 3) return null;
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l < 1e-12 ? null : new[] { v[0] / l, v[1] / l, v[2] / l };
        }

        public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        public static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static void Extent(IList<double[]> pts, double[] dir, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            foreach (double[] p in pts)
            {
                double d = Dot(p, dir);
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }
    }
}
