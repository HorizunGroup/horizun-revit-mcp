// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Dimensions held against GEOMETRY, as arithmetic (item 2.3 of the detailing
// feedback: "compare against the geometry, tolerance <= 1 mm").
//
// A committed linear dimension reports Revit's own Value / segment values. That
// is Revit grading its own homework. These rules grade it against the geometry
// of the references it hangs off instead:
//
//   * coordinates      - every reference reduced to ONE number along the dimension
//                        line direction (plane: origin.dir with normal parallel to
//                        the line; line/grid/edge: any point, line perpendicular to
//                        the dimension line; point: itself). A reference whose
//                        geometry is not measurable is "unavailable" with a reason -
//                        never a guessed coordinate, never counted as verified.
//   * verification     - consecutive coordinate differences against the segment
//                        values, 1 mm by default. verified / partial / unavailable /
//                        failed; only "failed" fails a row, and "partial" says how
//                        many segments the geometry could not speak for.
//   * layer boundaries - where the core and finish boundaries of a compound
//                        structure sit, measured inward from the exterior face, so
//                        a face read from the model can be CLASSIFIED by offset.
//   * chain crossing   - where a line segment crosses a plane, for chain_along_line.
//   * crop             - whether a point sits inside a rectangular crop (view-local).
//   * flags            - value_override is refused unless explicitly allowed.
//
// Revit-free so every rule is unit-tested; the Revit side only reads geometry.
// All lengths are internal feet.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The measurable geometry behind one reference, in model coordinates (feet).</summary>
    public sealed class RefGeometry
    {
        public const string KindPlane = "plane";
        public const string KindLine = "line";
        public const string KindPoint = "point";

        /// <summary>plane, line, point - or null when <see cref="Unavailable"/> says why.</summary>
        public string Kind { get; set; }
        /// <summary>Plane origin, a point on the line, or the point.</summary>
        public double[] Point { get; set; }
        /// <summary>Plane normal or line direction; unused for a point.</summary>
        public double[] Vector { get; set; }
        /// <summary>Structured code naming why the geometry cannot be measured; null when it can.</summary>
        public string Unavailable { get; set; }

        public static RefGeometry Plane(double[] origin, double[] normal) => new RefGeometry { Kind = KindPlane, Point = origin, Vector = normal };
        public static RefGeometry Line(double[] point, double[] direction) => new RefGeometry { Kind = KindLine, Point = point, Vector = direction };
        public static RefGeometry AtPoint(double[] point) => new RefGeometry { Kind = KindPoint, Point = point };
        public static RefGeometry None(string code) => new RefGeometry { Unavailable = code };
    }

    public sealed class SegmentVerdict
    {
        public int Index { get; set; }
        public double? GeometryFeet { get; set; }
        public double? MeasuredFeet { get; set; }
        public double? DeviationFeet { get; set; }
        /// <summary>verified, failed or unavailable.</summary>
        public string Status { get; set; }
        public string Reason { get; set; }
    }

    public sealed class DimensionGeometryVerdict
    {
        /// <summary>verified, partial, unavailable or failed.</summary>
        public string Status { get; set; }
        public double ToleranceFeet { get; set; }
        public List<double?> Coordinates { get; } = new List<double?>();
        public List<string> CoordinateReasons { get; } = new List<string>();
        public List<SegmentVerdict> Segments { get; } = new List<SegmentVerdict>();
        public bool Failed => Status == DimensionGeometryRules.StatusFailed;
        /// <summary>True when the verdict holds over the references sorted along the line, not as stored.</summary>
        public bool Reordered { get; set; }
    }

    public sealed class ChainHit
    {
        public string Stable { get; set; }
        public long ElementId { get; set; }
        /// <summary>Parameter along the chain line, 0 at line_start and 1 at line_end.</summary>
        public double T { get; set; }
        /// <summary>Signed coordinate along the line direction (feet).</summary>
        public double Coordinate { get; set; }
    }

    public static class DimensionGeometryRules
    {
        public const string StatusVerified = "verified";
        public const string StatusPartial = "partial";
        public const string StatusUnavailable = "unavailable";
        public const string StatusFailed = "failed";

        /// <summary>1 mm: the item's ceiling, and the default.</summary>
        public const double DefaultToleranceFeet = 1.0 / 304.8;

        /// <summary>cos(0.5 deg): the parallel tolerance the auto-dimension planner already states.</summary>
        public const double ParallelCos = 0.99996192;
        /// <summary>sin(0.5 deg): the perpendicular tolerance, same angle.</summary>
        public const double PerpendicularSin = 0.00872654;

        // codes for unmeasurable references
        public const string CodeLinkedReference = "linked_reference_geometry_not_measured";
        public const string CodeFamilyReference = "family_instance_reference_geometry_not_measured";
        public const string CodeUnsupportedGeometry = "reference_geometry_unsupported";
        public const string CodeUnreadable = "reference_geometry_unreadable";
        public const string CodeNotPerpendicular = "reference_not_perpendicular_to_dimension_line";
        public const string CodeNoDirection = "dimension_line_direction_unreadable";

        // codes for the core-face selectors
        public const string CodeNoCompoundStructure = "no_compound_structure";
        public const string CodeNoCoreLayers = "no_core_layers";
        public const string CodeCoreFaceNotExposed = "core_face_not_exposed";

        public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        public static double[] Unit(double[] v)
        {
            if (v == null || v.Length != 3) return null;
            double n = Math.Sqrt(Dot(v, v));
            return n < 1e-12 ? null : new[] { v[0] / n, v[1] / n, v[2] / n };
        }

        /// <summary>
        /// The reference's position along the unit direction <paramref name="dir"/>, or
        /// null with a structured reason. A plane must face along the line (its normal
        /// parallel to it, either sign); a line must cross it (perpendicular). Anything
        /// else has no single position a linear dimension could measure from.
        /// </summary>
        public static double? Coordinate(RefGeometry g, double[] dir, out string reason)
        {
            reason = null;
            double[] d = Unit(dir);
            if (d == null) { reason = CodeNoDirection; return null; }
            if (g == null) { reason = CodeUnreadable; return null; }
            if (g.Unavailable != null) { reason = g.Unavailable; return null; }
            if (g.Point == null || g.Point.Length != 3) { reason = CodeUnreadable; return null; }
            switch (g.Kind)
            {
                case RefGeometry.KindPoint:
                    return Dot(g.Point, d);
                case RefGeometry.KindPlane:
                {
                    double[] n = Unit(g.Vector);
                    if (n == null) { reason = CodeUnreadable; return null; }
                    if (Math.Abs(Dot(n, d)) < ParallelCos) { reason = CodeNotPerpendicular; return null; }
                    return Dot(g.Point, d);
                }
                case RefGeometry.KindLine:
                {
                    double[] u = Unit(g.Vector);
                    if (u == null) { reason = CodeUnreadable; return null; }
                    if (Math.Abs(Dot(u, d)) > PerpendicularSin) { reason = CodeNotPerpendicular; return null; }
                    return Dot(g.Point, d);
                }
                default:
                    reason = CodeUnsupportedGeometry; return null;
            }
        }

        /// <summary>
        /// Hold a committed dimension's values against its references' geometry. One
        /// segment per consecutive pair: a two-reference dimension has one segment whose
        /// value is <paramref name="singleValue"/>; a chain has <paramref name="segmentValues"/>
        /// in reference order. A segment touching an unmeasurable reference is
        /// "unavailable" - it neither passes nor fails - and the whole verdict says so.
        /// </summary>
        public static DimensionGeometryVerdict Verify(IReadOnlyList<RefGeometry> refs, double[] dir, double? singleValue,
                                             IReadOnlyList<double> segmentValues, double toleranceFeet)
        {
            var v = new DimensionGeometryVerdict { ToleranceFeet = toleranceFeet > 0 ? toleranceFeet : DefaultToleranceFeet };
            int n = refs == null ? 0 : refs.Count;
            for (int i = 0; i < n; i++)
            {
                string why;
                v.Coordinates.Add(Coordinate(refs[i], dir, out why));
                v.CoordinateReasons.Add(why);
            }
            int segments = Math.Max(0, n - 1);
            var measured = new List<double?>();
            if (segments == 1) measured.Add(singleValue ?? (segmentValues != null && segmentValues.Count == 1 ? segmentValues[0] : (double?)null));
            else for (int i = 0; i < segments; i++)
                measured.Add(segmentValues != null && i < segmentValues.Count ? segmentValues[i] : (double?)null);

            int ok = 0, bad = 0, none = 0;
            for (int i = 0; i < segments; i++)
            {
                var s = new SegmentVerdict { Index = i, MeasuredFeet = measured[i] };
                double? a = v.Coordinates[i], b = v.Coordinates[i + 1];
                if (!a.HasValue || !b.HasValue)
                {
                    s.Status = StatusUnavailable;
                    s.Reason = v.CoordinateReasons[a.HasValue ? i + 1 : i];
                    none++;
                }
                else if (!s.MeasuredFeet.HasValue)
                {
                    s.GeometryFeet = Math.Abs(b.Value - a.Value);
                    s.Status = StatusFailed;
                    s.Reason = "segment_value_unreadable";
                    bad++;
                }
                else
                {
                    s.GeometryFeet = Math.Abs(b.Value - a.Value);
                    s.DeviationFeet = Math.Abs(s.GeometryFeet.Value - s.MeasuredFeet.Value);
                    if (s.DeviationFeet.Value <= v.ToleranceFeet) { s.Status = StatusVerified; ok++; }
                    else { s.Status = StatusFailed; s.Reason = "value_differs_from_geometry"; bad++; }
                }
                v.Segments.Add(s);
            }
            if (bad > 0) v.Status = StatusFailed;
            else if (segments == 0 || ok == 0) v.Status = StatusUnavailable;
            else if (none > 0) v.Status = StatusPartial;
            else v.Status = StatusVerified;
            return v;
        }

        /// <summary>
        /// <see cref="Verify"/> in the stored reference order first. Revit lists segments
        /// along the line; should its stored reference order differ, a chain whose every
        /// coordinate is known is re-checked sorted along the line (both senses) before a
        /// failure stands - the sorted order is what the segments physically measure.
        /// </summary>
        public static DimensionGeometryVerdict VerifyAnyOrder(IReadOnlyList<RefGeometry> refs, double[] dir,
            double? singleValue, IReadOnlyList<double> segmentValues, double toleranceFeet)
        {
            DimensionGeometryVerdict v = Verify(refs, dir, singleValue, segmentValues, toleranceFeet);
            if (!v.Failed || refs == null || refs.Count < 3 || v.Coordinates.Any(c => !c.HasValue)) return v;
            var order = Enumerable.Range(0, refs.Count).OrderBy(i => v.Coordinates[i].Value).ToList();
            foreach (bool descending in new[] { false, true })
            {
                var idx = descending ? Enumerable.Reverse(order).ToList() : order;
                DimensionGeometryVerdict w = Verify(idx.Select(i => refs[i]).ToList(), dir, singleValue, segmentValues, toleranceFeet);
                if (!w.Failed) { w.Reordered = true; return w; }
            }
            return v;
        }

        /// <summary>
        /// Expected TOTAL from geometry, for the planner's expected_value: first to last
        /// coordinate. Null (with the reason) when any reference is unmeasurable, because
        /// a total over a gap is a number nobody measured.
        /// </summary>
        public static double? ExpectedTotal(IReadOnlyList<RefGeometry> refs, double[] dir, out string reason)
        {
            reason = null;
            if (refs == null || refs.Count < 2) { reason = "fewer_than_two_references"; return null; }
            var coords = new List<double>();
            foreach (RefGeometry g in refs)
            {
                double? c = Coordinate(g, dir, out reason);
                if (!c.HasValue) return null;
                coords.Add(c.Value);
            }
            // A chain is monotonic along its line; one that is not would double back and
            // its total is not first-to-last. Refuse rather than report a wrong total.
            double sign = 0;
            for (int i = 1; i < coords.Count; i++)
            {
                double step = coords[i] - coords[i - 1];
                if (Math.Abs(step) < 1e-9) continue;
                if (sign == 0) sign = Math.Sign(step);
                else if (Math.Sign(step) != sign) { reason = "references_not_monotonic_along_line"; return null; }
            }
            double total = 0;
            for (int i = 1; i < coords.Count; i++) total += Math.Abs(coords[i] - coords[i - 1]);
            return total;
        }

        // ---- compound structure ------------------------------------------------

        /// <summary>
        /// The inward offset, from the EXTERIOR face, of the boundary a core selector names.
        /// Layers are listed exterior to interior, as CompoundStructure.GetLayers does.
        /// core_exterior_face: sum of widths before the first core layer. core_interior_face:
        /// sum through the last core layer. Null with a code when there is no core.
        /// </summary>
        public static double? CoreBoundaryOffset(IReadOnlyList<double> layerWidths, int firstCore, int lastCore,
                                                 bool interior, out string code)
        {
            code = null;
            if (layerWidths == null || layerWidths.Count == 0) { code = CodeNoCompoundStructure; return null; }
            if (firstCore < 0 || lastCore < firstCore || lastCore >= layerWidths.Count) { code = CodeNoCoreLayers; return null; }
            int upTo = interior ? lastCore + 1 : firstCore;
            double offset = 0;
            for (int i = 0; i < upTo; i++) offset += layerWidths[i];
            return offset;
        }

        /// <summary>Every layer boundary's inward offset from the exterior face: 0, w0, w0+w1, ..., total.</summary>
        public static List<double> LayerBoundaries(IReadOnlyList<double> layerWidths)
        {
            var result = new List<double> { 0 };
            if (layerWidths == null) return result;
            double acc = 0;
            foreach (double w in layerWidths) { acc += w; result.Add(acc); }
            return result;
        }

        /// <summary>
        /// Inward offset of a candidate face from the exterior face, along the wall's
        /// exterior normal; null when the candidate is not parallel to the wall's sides.
        /// </summary>
        public static double? InwardOffset(double[] exteriorOrigin, double[] exteriorNormal,
                                           double[] faceOrigin, double[] faceNormal)
        {
            double[] n = Unit(exteriorNormal), f = Unit(faceNormal);
            if (n == null || f == null || exteriorOrigin == null || faceOrigin == null) return null;
            if (Math.Abs(Dot(n, f)) < ParallelCos) return null;
            double[] delta = { exteriorOrigin[0] - faceOrigin[0], exteriorOrigin[1] - faceOrigin[1], exteriorOrigin[2] - faceOrigin[2] };
            return Dot(delta, n);
        }

        /// <summary>Index of the boundary within tolerance of the offset, or -1.</summary>
        public static int MatchBoundary(double offset, IReadOnlyList<double> boundaries, double toleranceFeet)
        {
            if (boundaries == null) return -1;
            int best = -1; double bestDelta = double.MaxValue;
            for (int i = 0; i < boundaries.Count; i++)
            {
                double d = Math.Abs(boundaries[i] - offset);
                if (d <= toleranceFeet && d < bestDelta) { best = i; bestDelta = d; }
            }
            return best;
        }

        // ---- chain_along_line ----------------------------------------------------

        /// <summary>
        /// Where segment a-b crosses the plane (origin, normal): the parameter t in [0,1],
        /// or null when the plane does not FACE along the line (normal within
        /// 0.5 deg of the line direction) or the crossing falls outside the segment.
        /// </summary>
        public static double? PlaneCrossing(double[] a, double[] b, double[] origin, double[] normal)
        {
            if (a == null || b == null || origin == null) return null;
            double[] ab = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double len = Math.Sqrt(Dot(ab, ab));
            double[] n = Unit(normal);
            if (len < 1e-9 || n == null) return null;
            double[] d = { ab[0] / len, ab[1] / len, ab[2] / len };
            double cos = Dot(n, d);
            if (Math.Abs(cos) < ParallelCos) return null;
            double[] ao = { origin[0] - a[0], origin[1] - a[1], origin[2] - a[2] };
            double t = Dot(ao, n) / Dot(ab, n);
            if (t < -1e-9 || t > 1 + 1e-9) return null;
            return Math.Min(1, Math.Max(0, t));
        }

        /// <summary>
        /// Order the hits along the line and collapse coincident ones (two faces in one
        /// plane, e.g. two abutting walls) within <paramref name="toleranceFeet"/>, keeping
        /// the lowest element id then the ordinal-smallest stable string - deterministic,
        /// and every collapsed hit is returned in <paramref name="collapsed"/>.
        /// </summary>
        public static List<ChainHit> OrderChain(IEnumerable<ChainHit> hits, double toleranceFeet, out List<ChainHit> collapsed)
        {
            collapsed = new List<ChainHit>();
            var ordered = (hits ?? Enumerable.Empty<ChainHit>())
                .OrderBy(h => h.Coordinate).ThenBy(h => h.ElementId).ThenBy(h => h.Stable, StringComparer.Ordinal).ToList();
            var result = new List<ChainHit>();
            foreach (ChainHit h in ordered)
            {
                ChainHit last = result.Count == 0 ? null : result[result.Count - 1];
                if (last != null && Math.Abs(h.Coordinate - last.Coordinate) <= toleranceFeet)
                {
                    bool replace = h.ElementId < last.ElementId ||
                                   (h.ElementId == last.ElementId && string.CompareOrdinal(h.Stable, last.Stable) < 0);
                    if (replace) { collapsed.Add(last); result[result.Count - 1] = h; }
                    else collapsed.Add(h);
                    continue;
                }
                result.Add(h);
            }
            return result;
        }

        // ---- crop -----------------------------------------------------------------

        /// <summary>Whether a view-local (x, y) lies inside the crop rectangle, with tolerance.</summary>
        public static bool InsideCrop(double x, double y, double minX, double minY, double maxX, double maxY, double toleranceFeet)
            => x >= minX - toleranceFeet && x <= maxX + toleranceFeet &&
               y >= minY - toleranceFeet && y <= maxY + toleranceFeet;

        // ---- flags ----------------------------------------------------------------

        /// <summary>value_override replaces the measured text: refused unless allow_text_override is true.</summary>
        public static string ValueOverrideError(bool hasOverride, bool allowTextOverride)
            => hasOverride && !allowTextOverride
                ? "value_override replaces the measured value with text, so the dimension would no longer show what " +
                  "the geometry measures. Pass allow_text_override: true to accept that explicitly. Nothing was written."
                : null;

        public static string OutsideCropError(int actionIndex)
            => "dimension line of action " + actionIndex + " falls outside the view's active crop region (model crop " +
               "box); the dimension would be clipped or invisible. Move line_start/line_end inside the crop or pass " +
               "allow_outside_crop: true. Nothing was written.";
    }

    /// <summary>
    /// horizun_plan_annotations operation=dimension_mode: the high-level modes and which
    /// EXISTING operation each delegates to (null = planned natively, because nothing
    /// existing covers it). Delegation keeps one implementation per concept.
    /// </summary>
    public static class DimensionModeRules
    {
        public const string Operation = "dimension_mode";
        public const string WallThickness = "wall_thickness";
        public const string OpeningWidth = "opening_width";
        public const string OpeningHeight = "opening_height";
        public const string RoomClearWidth = "room_clear_width";
        public const string GridToGrid = "grid_to_grid";
        public const string ChainAlongLine = "chain_along_line";
        public const string LevelHeights = "level_heights";
        public const string SpotElevation = "spot_elevation";

        public static readonly IReadOnlyList<string> Modes = new[]
        {
            WallThickness, OpeningWidth, OpeningHeight, RoomClearWidth, GridToGrid, ChainAlongLine, LevelHeights, SpotElevation
        };

        /// <summary>The existing planner operation a mode delegates to; null when planned natively.</summary>
        public static string DelegateOperation(string mode)
        {
            switch (mode)
            {
                case OpeningWidth: return "auto_dimension_openings";
                case OpeningHeight: return "intent_dimension";
                case RoomClearWidth: return "auto_dimension_room";
                case GridToGrid: return "auto_dimension_grids";
                case LevelHeights: return "auto_dimension_levels";
                default: return null;
            }
        }

        /// <summary>Null when the request carries what the mode needs; the refusal otherwise.</summary>
        public static string Validate(string mode, bool hasElementIds, int elementCount, bool hasLine, bool hasRoom)
        {
            if (string.IsNullOrEmpty(mode) || !Modes.Contains(mode))
                return "mode must be one of: " + string.Join(", ", Modes) + ".";
            switch (mode)
            {
                case WallThickness:
                case OpeningHeight:
                case SpotElevation:
                    if (!hasElementIds || elementCount < 1 || elementCount > 50)
                        return mode + " requires element_ids (1..50).";
                    return null;
                case ChainAlongLine:
                    if (!hasLine) return "chain_along_line requires line_start and line_end: the line whose crossings are dimensioned.";
                    if (!hasElementIds || elementCount < 1 || elementCount > 200)
                        return "chain_along_line requires element_ids (1..200): the elements whose faces the line may cross.";
                    return null;
                case RoomClearWidth:
                    return hasRoom ? null : "room_clear_width requires room_id.";
                default:
                    return null;
            }
        }
    }
}
