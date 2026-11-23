// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_wall_finish: which ARQ wall stands on which EST
// core, and where its finish layers go.
//
// field cycle ARQ (2026-10-24, A1/A5): "en ARQ los muros estructurales llevan solo
// acabados; el núcleo es de EST; sin traslapo". 515 ARQ walls of one type stood
// on the EST walls of a LINK and counted the concrete twice. Nothing typed told
// which ARQ wall matches which EST wall, nor placed the finish on the core faces.
//
// THE MATCH, said once, in plan (feet):
//   parallel within 1 degree; the core's axis within max_offset of the ARQ wall's
//   midpoint; the overlap along the ARQ axis at least min_overlap of the ARQ
//   wall's length. The best match is the largest overlap.
// THE FINISH: on each core face asked, a wall of the finish type whose axis sits
// core_width/2 + finish_width/2 off the core's axis, over the overlap only. It
// shares no volume with the core.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CoreSegment
    {
        public long Id;
        public double[] A, B;    // plan end points, host coordinates
        public double Width;
        public double ZMin = double.NaN, ZMax = double.NaN;
    }

    public sealed class CoreMatch
    {
        public long CoreId;
        public double OffsetFeet;   // signed distance from the ARQ axis to the core axis, along the ARQ wall's exterior normal
        public double From, To;     // overlap along the ARQ axis, feet from its start
        public double Ratio;
        public string Refusal;
    }

    public static class WallFinishRules
    {
        // Revit invalidates deleted elements. Materialize the IDs before any deletion and
        // use only those IDs for both apply and verification, including after commit.
        internal sealed class OriginalWalls
        {
            private readonly long[] ids;
            public OriginalWalls(IEnumerable<long> wallIds) => ids = wallIds.ToArray();
            public IReadOnlyList<long> Ids => ids;

            public void Delete(Func<IReadOnlyList<long>, IEnumerable<long>> delete)
            {
                long[] unexpected = delete(ids).Except(ids).ToArray();
                if (unexpected.Length > 0)
                    throw new ArgumentException("Deleting the original walls would also delete unplanned elements: " +
                        string.Join(", ", unexpected) + ". Preserve the originals or resolve their dependents.");
            }

            public int CountRemoved(Func<long, bool> exists) => ids.Count(id => !exists(id));
        }

        public const int MaxWalls = 300;
        public static readonly double ParallelCos = Math.Cos(Math.PI / 180);

        private static readonly string[] Common =
            { "target_document", "units", "operation", "dry_run", "confirmation_token", "transaction_name", "idempotency_key",
              "wall_ids", "link_instance_id", "max_offset_mm", "min_overlap" };

        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["match_core"] = new string[0],
            ["convert_to_finish"] = new[] { "finish_type_id", "thickness_mm", "side" },
            ["create_finish_layers"] = new[] { "exterior_type_id", "interior_type_id", "original_wall" }
        };

        public static string Validate(JObject r)
        {
            if (r == null) return "parameters must be a JSON object.";
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Fields.TryGetValue(op, out string[] own)) return "operation must be match_core, convert_to_finish or create_finish_layers for horizun_wall_finish.";
            var allowed = new HashSet<string>(Common.Concat(own), StringComparer.Ordinal);
            foreach (JProperty p in r.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + op + "'. Nothing ran.";
            if (!(r["wall_ids"] is JArray ids) || ids.Count == 0 || ids.Count > MaxWalls || ids.Any(t => t.Type != JTokenType.Integer))
                return "wall_ids must list 1.." + MaxWalls + " ARQ wall ids.";
            if (ids.Select(t => t.Value<long>()).Distinct().Count() != ids.Count) return "wall_ids names the same wall twice.";
            if (r["link_instance_id"]?.Type != JTokenType.Integer) return "link_instance_id (the EST link that holds the cores) is required.";
            JToken mo = r["max_offset_mm"];
            if (mo != null && (mo.Type != JTokenType.Integer && mo.Type != JTokenType.Float || mo.Value<double>() <= 0 || mo.Value<double>() > 1000)) return "max_offset_mm must be in (0, 1000].";
            JToken ov = r["min_overlap"];
            if (ov != null && (ov.Type != JTokenType.Integer && ov.Type != JTokenType.Float || ov.Value<double>() <= 0 || ov.Value<double>() > 1)) return "min_overlap must be in (0, 1].";
            if (op == "convert_to_finish")
            {
                if (string.IsNullOrWhiteSpace(r.Value<string>("target_document"))) return "target_document is required.";
                if (r["finish_type_id"]?.Type != JTokenType.Integer || r.Value<long>("finish_type_id") <= 0) return "finish_type_id must name an existing basic finish wall type.";
                JToken thickness = r["thickness_mm"];
                if (thickness == null || (thickness.Type != JTokenType.Integer && thickness.Type != JTokenType.Float) ||
                    !Finite(thickness.Value<double>()) || thickness.Value<double>() <= 0 || thickness.Value<double>() > 100)
                    return "thickness_mm must be in (0,100], matching the finish type width.";
                if (r.Value<string>("side") != "exterior" && r.Value<string>("side") != "interior") return "side is required: exterior or interior of the existing ARQ wall.";
            }
            if (op == "create_finish_layers")
            {
                if (string.IsNullOrWhiteSpace(r.Value<string>("target_document"))) return "target_document is required.";
                if (r["exterior_type_id"] == null && r["interior_type_id"] == null) return "give exterior_type_id and/or interior_type_id: the finish wall type of each core face.";
                foreach (string k in new[] { "exterior_type_id", "interior_type_id" })
                    if (r[k] != null && r[k].Type != JTokenType.Integer) return k + " must be a wall type id.";
                string orig = r.Value<string>("original_wall");
                if (orig != "keep" && orig != "delete_if_unhosted")
                    return "original_wall is required: keep (the ARQ wall stays and still overlaps the core) or delete_if_unhosted - it is said, never defaulted.";
            }
            return null;
        }

        private static double Dot2(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1];

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        public static bool ThicknessMatches(double widthFeet, double requestedMm) =>
            Finite(widthFeet) && Finite(requestedMm) && requestedMm > 0 && Math.Abs(widthFeet * 304.8 - requestedMm) <= 0.1;

        public static bool OpeningFits(double centerAlong, double width, double length, double bottom, double height, double wallBottom, double wallTop) =>
            new[] { centerAlong, width, length, bottom, height, wallBottom, wallTop }.All(Finite) && width > 0 && height > 0 &&
            centerAlong - width / 2 >= -1e-6 && centerAlong + width / 2 <= length + 1e-6 && bottom >= wallBottom - 1e-6 && bottom + height <= wallTop + 1e-6;

        /// <summary>A whole-wall conversion must not replace the portion outside the core with a thin finish.
        /// Splitting that portion, especially across an insert, requires a separate verified rehost plan.</summary>
        public static string ConversionCoverageRefusal(double length, double from, double to,
            double wallBottom, double wallTop, double coreBottom, double coreTop, double tolerance)
        {
            if (!new[] { length, from, to, tolerance }.All(Finite) || length <= 0 || tolerance < 0 ||
                from < -tolerance || to > length + tolerance || to <= from)
                return "core_coverage_unreadable";
            if (from > tolerance || to < length - tolerance) return "core_partial_span";
            if (!ValidRange(wallBottom, wallTop) || !ValidRange(coreBottom, coreTop)) return "height_unreadable";
            if (coreBottom > wallBottom + tolerance || coreTop < wallTop - tolerance) return "core_partial_height";
            return null;
        }

        /// <summary>The best core under an ARQ wall a-b (exterior normal n), or a refusal naming why none qualifies.</summary>
        public static CoreMatch Match(double[] a, double[] b, double[] n, IEnumerable<CoreSegment> cores, double maxOffset, double minOverlap,
            double zMin, double zMax)
        {
            if (!ValidRange(zMin, zMax)) return new CoreMatch { Refusal = "the wall's vertical range could not be read" };
            double[] ab = { b[0] - a[0], b[1] - a[1] };
            double len = Math.Sqrt(Dot2(ab, ab));
            if (len < 1e-6) return new CoreMatch { Refusal = "the wall has no length" };
            double[] u = { ab[0] / len, ab[1] / len };
            double[] mid = { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2 };
            CoreMatch best = null;
            string nearest = "no core is parallel within 1 degree";
            foreach (CoreSegment c in cores)
            {
                if (!VerticalOverlap(zMin, zMax, c.ZMin, c.ZMax))
                { nearest = "no candidate core has a readable vertical range overlapping the wall in Z"; continue; }
                double[] cd = { c.B[0] - c.A[0], c.B[1] - c.A[1] };
                double cl = Math.Sqrt(Dot2(cd, cd));
                if (cl < 1e-6 || Math.Abs(Dot2(cd, u)) / cl < ParallelCos) continue;
                double offset = Dot2(new[] { c.A[0] - mid[0], c.A[1] - mid[1] }, n);
                if (Math.Abs(offset) > maxOffset) { nearest = "the nearest parallel core is beyond max_offset_mm"; continue; }
                double s0 = Dot2(new[] { c.A[0] - a[0], c.A[1] - a[1] }, u), s1 = Dot2(new[] { c.B[0] - a[0], c.B[1] - a[1] }, u);
                double from = Math.Max(0, Math.Min(s0, s1)), to = Math.Min(len, Math.Max(s0, s1));
                double ratio = (to - from) / len;
                if (ratio < minOverlap) { nearest = "a parallel core overlaps only " + Math.Round(Math.Max(0, ratio) * 100) + " % of the wall"; continue; }
                if (best == null || ratio > best.Ratio) best = new CoreMatch { CoreId = c.Id, OffsetFeet = offset, From = from, To = to, Ratio = ratio };
            }
            return best ?? new CoreMatch { Refusal = nearest };
        }

        private static bool ValidRange(double min, double max) =>
            !double.IsNaN(min) && !double.IsNaN(max) && !double.IsInfinity(min) && !double.IsInfinity(max) && max > min;

        /// <summary>Face contact alone is not an overlap; both measured ranges must have positive height.</summary>
        public static bool VerticalOverlap(double minA, double maxA, double minB, double maxB) =>
            ValidRange(minA, maxA) && ValidRange(minB, maxB) && Math.Min(maxA, maxB) > Math.Max(minA, minB) + 1e-9;

        /// <summary>How far a finish wall's axis sits from the core's axis.</summary>
        public static double FinishOffset(double coreWidth, double finishWidth) => coreWidth / 2 + finishWidth / 2;
    }
}
