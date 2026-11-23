// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_sheet_layout (FIELD FEEDBACK 2.5).
//
// Everything is paper: rectangles [minX, minY, maxX, maxY] in sheet coordinates.
//
// USABLE AREA = the measured drawing frame inset by the margins (left, right,
// top, bottom) and an optional strip on the right for notes. For repair, omitted
// margins are zero, so the check and the repair agree on which viewports are outside.
//
// ROWS, NOT A FREE PACK. auto_layout places each viewport's extent (box plus
// title) left to right in rows from the top, in the order given, gap apart; every
// item in a row shares the row's BOTTOM edge, so the titles under the views line
// up. The scale is never touched: an arrangement that does not fit is refused
// whole, with the height it needed, rather than shrunk or half-placed.
//
// ANCHOR = CROP. A field measurement showed a 90-100 mm offset in Y placing by the viewport
// box: grids and levels reach outside the crop and drag the box centre with them.
// move_viewport places the CROP's centre (or corner) where asked by default.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class LayoutItem
    {
        public string Key;
        public double Width, Height;
    }

    public static class SheetLayoutRules
    {
        public const double FeetPerMm = 1.0 / 304.8;
        private const double MachineEpsilon = 2.2204460492503131e-16;
        /// <summary>Paper tolerance of the re-read: 0.5 mm.</summary>
        public const double ToleranceFeet = 0.5 * FeetPerMm;
        public static readonly double[] DefaultMarginsMm = { 10, 10, 10, 10 };
        public const double DefaultGapMm = 10;

        public static double[] Rect(double x0, double y0, double x1, double y1)
            => new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1) };

        public static double[] Union(double[] a, double[] b)
            => a == null ? b : b == null ? a : new[] { Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Max(a[2], b[2]), Math.Max(a[3], b[3]) };

        public static double[] Center(double[] r) => new[] { (r[0] + r[2]) / 2, (r[1] + r[3]) / 2 };

        /// <summary>Smallest translation of each box plus title into the area. Null if a box
        /// cannot fit or a translation creates or worsens an overlap. Only moved boxes are returned. Never resizes.</summary>
        public static Dictionary<string, double[]> RepairOutside(IList<KeyValuePair<string, double[]>> extents,
            double[] area, double tolerance)
            => RepairOutside(extents, area, tolerance, out _);

        public static Dictionary<string, double[]> RepairOutside(IList<KeyValuePair<string, double[]>> extents,
            double[] area, double tolerance, out string refusal)
        {
            refusal = null;
            if (area == null || extents == null) { refusal = "drawing area is unreadable"; return null; }
            var result = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (var item in extents)
            {
                double[] e = item.Value;
                if (e == null || e.Length != 4 || e.Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                    e[2] < e[0] || e[3] < e[1])
                { refusal = "viewport " + item.Key + " has an unreadable extent"; return null; }
                if (Inside(area, e, tolerance)) continue;
                if (e[2] - e[0] > area[2] - area[0] + tolerance || e[3] - e[1] > area[3] - area[1] + tolerance)
                { refusal = "viewport " + item.Key + " is too large for the drawing area"; return null; }
                double dx = RepairShift(area[0], area[2], e[0], e[2], tolerance);
                double dy = RepairShift(area[1], area[3], e[1], e[3], tolerance);
                result.Add(item.Key, new[] { e[0] + dx, e[1] + dy, e[2] + dx, e[3] + dy });
            }
            if (result.Count == 0) return result;
            var final = extents.Select(kv => new KeyValuePair<string, double[]>(kv.Key,
                result.TryGetValue(kv.Key, out double[] moved) ? moved : kv.Value)).ToList();
            string[] collision = NewOverlaps(extents, final, tolerance).FirstOrDefault();
            if (collision != null)
            {
                if (!result.ContainsKey(collision[0])) Array.Reverse(collision);
                refusal = "viewport " + collision[0] + " collides with viewport " + collision[1] +
                    " after minimum translation (new or increased overlap, or new full coverage)";
                return null;
            }
            return result;
        }

        private static double RepairShift(double areaMin, double areaMax, double min, double max, double tolerance)
        {
            if (min >= areaMin - tolerance && max <= areaMax + tolerance) return 0;
            return Math.Max(areaMin - min, Math.Min(0, areaMax - max));
        }

        /// <summary>Repair collisions: new overlaps above tolerance, increases in intersection area, and newly covered viewports.</summary>
        public static List<string[]> NewOverlaps(IList<KeyValuePair<string, double[]>> before,
            IList<KeyValuePair<string, double[]>> after, double tolerance)
        {
            var previous = before.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            var collisions = new List<string[]>();
            for (int i = 0; i < after.Count; i++)
                for (int j = i + 1; j < after.Count; j++)
                {
                    var a = after[i];
                    var b = after[j];
                    var (width, height, exceedsTolerance, areaRoundoff) = IntersectionSize(a.Value, b.Value, tolerance);
                    if (width <= 0 || height <= 0) continue;

                    previous.TryGetValue(a.Key, out double[] oldA);
                    previous.TryGetValue(b.Key, out double[] oldB);
                    var (oldWidth, oldHeight, previouslyExceeded, oldAreaRoundoff) = IntersectionSize(oldA, oldB, tolerance);
                    bool newOverlap = exceedsTolerance && !previouslyExceeded;
                    double oldArea = oldWidth * oldHeight;
                    bool increasedArea = oldArea > 0 && width * height - oldArea > areaRoundoff + oldAreaRoundoff;
                    bool newlyCovered = (Inside(a.Value, b.Value, 0) && !Inside(oldA, oldB, 0)) ||
                                        (Inside(b.Value, a.Value, 0) && !Inside(oldB, oldA, 0));
                    if (newOverlap || increasedArea || newlyCovered) collisions.Add(new[] { a.Key, b.Key });
                }
            return collisions;
        }

        private static (double Width, double Height, bool ExceedsTolerance, double AreaRoundoff) IntersectionSize(
            double[] a, double[] b, double tolerance)
        {
            if (a == null || b == null) return (0, 0, false, 0);
            var (width, widthExceeds, widthRoundoff) = IntersectionSpan(Math.Max(a[0], b[0]), Math.Min(a[2], b[2]), tolerance);
            var (height, heightExceeds, heightRoundoff) = IntersectionSpan(Math.Max(a[1], b[1]), Math.Min(a[3], b[3]), tolerance);
            // The error of a narrow intersection depends on its paper coordinates, not just its area.
            double areaRoundoff = widthRoundoff * height + heightRoundoff * (width + widthRoundoff)
                + MachineEpsilon * width * height;
            return (width, height, widthExceeds && heightExceeds, areaRoundoff);
        }

        private static (double Size, bool ExceedsTolerance, double Roundoff) IntersectionSpan(double lower, double upper, double tolerance)
        {
            double size = Math.Max(0, upper - lower);
            // Allow roundoff in the paper coordinates and their subtraction at the exact threshold.
            double roundoff = 4 * MachineEpsilon * Math.Max(Math.Abs(lower), Math.Abs(upper));
            return (size, size - tolerance > roundoff, roundoff);
        }

        /// <summary>Drawing frame minus margins [l, r, t, b] and a notes strip on the right. Throws when nothing is left.</summary>
        public static double[] Usable(double[] titleBlock, double[] margins, double notesWidth)
        {
            if (titleBlock == null) throw new ArgumentException("the sheet has no readable title block or outline.");
            if (margins == null || margins.Length != 4 || margins.Any(m => m < 0 || double.IsNaN(m))) throw new ArgumentException("margins must be four non-negative lengths.");
            if (notesWidth < 0 || double.IsNaN(notesWidth)) throw new ArgumentException("notes_width must be non-negative.");
            var u = new[] { titleBlock[0] + margins[0], titleBlock[1] + margins[3], titleBlock[2] - margins[1] - notesWidth, titleBlock[3] - margins[2] };
            if (u[2] - u[0] <= 0 || u[3] - u[1] <= 0)
                throw new ArgumentException("the margins and notes_width leave no usable area on this title block.");
            return u;
        }

        /// <summary>
        /// The drawing frame inside a title block, measured from its straight lines on paper
        /// ([x0, y0, x1, y1] each). The title block's box is the paper edge: a frame drawn 20-40 mm
        /// in from it, and a title panel down one side, are not in the box, which is why a layout
        /// "inside the title block" put a viewport title over the frame on the Autodesk sample.
        /// Per side, the innermost axis-aligned line that runs along at least <paramref name="minSpan"/>
        /// of the box (collinear pieces joined) in that half of the box: left = the rightmost such
        /// vertical in the left half, right = the leftmost in the right half (a title panel's edge),
        /// top/bottom the same with horizontals. A side with no such line keeps the box edge, and
        /// <paramref name="sides"/> says which it is ("frame_line" / "title_block_box"). A frame
        /// under a third of the box in either direction is not believed: the box comes back.
        /// </summary>
        public static double[] InnerFrame(IEnumerable<double[]> segments, double[] box, double minSpan, double tolerance, out string[] sides)
            => InnerFrame(segments, box, minSpan, tolerance, out sides, out _);

        // Keep the selected line position even when it coincides with the box edge. Null means box fallback.
        public static double[] InnerFrame(IEnumerable<double[]> segments, double[] box, double minSpan, double tolerance,
                                          out string[] sides, out double?[] boundaryLines)
        {
            sides = new[] { "title_block_box", "title_block_box", "title_block_box", "title_block_box" };
            boundaryLines = new double?[4];
            if (box == null) return null;
            double w = box[2] - box[0], h = box[3] - box[1];
            if (!(w > 0) || !(h > 0)) return box;
            var vertical = new List<double[]>(); var horizontal = new List<double[]>();
            foreach (double[] s in segments ?? Enumerable.Empty<double[]>())
            {
                if (s == null || s.Length < 4 || s.Any(double.IsNaN)) continue;
                if (Math.Abs(s[0] - s[2]) <= tolerance && Math.Abs(s[1] - s[3]) > tolerance)
                    vertical.Add(new[] { (s[0] + s[2]) / 2, Math.Min(s[1], s[3]), Math.Max(s[1], s[3]) });
                else if (Math.Abs(s[1] - s[3]) <= tolerance && Math.Abs(s[0] - s[2]) > tolerance)
                    horizontal.Add(new[] { (s[1] + s[3]) / 2, Math.Min(s[0], s[2]), Math.Max(s[0], s[2]) });
            }
            List<double> longV = LongLines(vertical, minSpan * h, tolerance), longH = LongLines(horizontal, minSpan * w, tolerance);
            double cx = (box[0] + box[2]) / 2, cy = (box[1] + box[3]) / 2;
            var f = (double[])box.Clone();
            var left = longV.Where(x => x < cx).ToList(); if (left.Count > 0) f[0] = left.Max();
            var right = longV.Where(x => x > cx).ToList(); if (right.Count > 0) f[2] = right.Min();
            var bottom = longH.Where(y => y < cy).ToList(); if (bottom.Count > 0) f[1] = bottom.Max();
            var top = longH.Where(y => y > cy).ToList(); if (top.Count > 0) f[3] = top.Min();
            if (f[2] - f[0] < w / 3 || f[3] - f[1] < h / 3)
            {
                sides = new[] { "title_block_box", "title_block_box", "title_block_box", "title_block_box" };
                return box;
            }
            // order: left, right, top, bottom (as margins)
            if (left.Count > 0) boundaryLines[0] = f[0];
            if (right.Count > 0) boundaryLines[1] = f[2];
            if (top.Count > 0) boundaryLines[2] = f[3];
            if (bottom.Count > 0) boundaryLines[3] = f[1];
            sides = new[]
            {
                f[0] > box[0] + tolerance ? "frame_line" : "title_block_box", f[2] < box[2] - tolerance ? "frame_line" : "title_block_box",
                f[3] < box[3] - tolerance ? "frame_line" : "title_block_box", f[1] > box[1] + tolerance ? "frame_line" : "title_block_box"
            };
            return f;
        }

        /// <summary>
        /// A title box drawn as a closed rectangle in a corner of the frame (the Horizun A1 title block:
        /// its frame IS the paper edge and the title data sits in a 160 x 275 mm box in the lower-right
        /// corner, drawn with short lines that <see cref="InnerFrame"/> rightly ignores). For every such
        /// box - four lines closing a rectangle, under half the frame, within <paramref name="cornerReach"/>
        /// of two frame edges - the frame loses either the strip beside the box or the strip above/below
        /// it, whichever leaves the larger area. Null <paramref name="cut"/> when there was no box.
        /// </summary>
        public static double[] AvoidCornerBoxes(IEnumerable<double[]> segments, double[] frame, double cornerReach, double tolerance,
                                                out List<double[]> boxes, out string cut)
        {
            boxes = new List<double[]>(); cut = null;
            if (frame == null) return null;
            var v = new List<double[]>(); var hz = new List<double[]>();
            foreach (double[] s in segments ?? Enumerable.Empty<double[]>())
            {
                if (s == null || s.Length < 4 || s.Any(double.IsNaN)) continue;
                if (Math.Abs(s[0] - s[2]) <= tolerance && Math.Abs(s[1] - s[3]) > tolerance) v.Add(new[] { (s[0] + s[2]) / 2, Math.Min(s[1], s[3]), Math.Max(s[1], s[3]) });
                else if (Math.Abs(s[1] - s[3]) <= tolerance && Math.Abs(s[0] - s[2]) > tolerance) hz.Add(new[] { (s[1] + s[3]) / 2, Math.Min(s[0], s[2]), Math.Max(s[0], s[2]) });
            }
            bool Covers(double y, double x0, double x1) => hz.Any(h => Math.Abs(h[0] - y) <= tolerance && h[1] <= x0 + tolerance && h[2] >= x1 - tolerance);
            double fw = frame[2] - frame[0], fh = frame[3] - frame[1];
            for (int i = 0; i < v.Count; i++)
                for (int j = 0; j < v.Count; j++)
                {
                    double[] a = v[i], b = v[j];
                    if (!(b[0] - a[0] > tolerance) || Math.Abs(a[1] - b[1]) > tolerance || Math.Abs(a[2] - b[2]) > tolerance) continue;
                    if (!Covers(a[1], a[0], b[0]) || !Covers(a[2], a[0], b[0])) continue;
                    var r = new[] { a[0], a[1], b[0], a[2] };
                    if (r[0] < frame[0] - tolerance || r[1] < frame[1] - tolerance || r[2] > frame[2] + tolerance || r[3] > frame[3] + tolerance) continue;
                    if ((r[2] - r[0]) * (r[3] - r[1]) >= 0.5 * fw * fh) continue;
                    bool nearX = r[2] >= frame[2] - cornerReach || r[0] <= frame[0] + cornerReach;
                    bool nearY = r[1] <= frame[1] + cornerReach || r[3] >= frame[3] - cornerReach;
                    if (nearX && nearY && !boxes.Any(k => Math.Abs(k[0] - r[0]) <= tolerance && Math.Abs(k[1] - r[1]) <= tolerance && Math.Abs(k[2] - r[2]) <= tolerance && Math.Abs(k[3] - r[3]) <= tolerance))
                        boxes.Add(r);
                }
            var f = (double[])frame.Clone();
            var cuts = new List<string>();
            foreach (double[] k in boxes)
            {
                bool right = k[2] >= f[2] - cornerReach, bottom = k[1] <= f[1] + cornerReach;
                double[] beside = (double[])f.Clone(), above = (double[])f.Clone();
                if (right) beside[2] = Math.Min(beside[2], k[0]); else beside[0] = Math.Max(beside[0], k[2]);
                if (bottom) above[1] = Math.Max(above[1], k[3]); else above[3] = Math.Min(above[3], k[1]);
                double aB = (beside[2] - beside[0]) * (beside[3] - beside[1]), aA = (above[2] - above[0]) * (above[3] - above[1]);
                if (aB >= aA) { f = beside; cuts.Add((right ? "right" : "left") + " side moved to the title box edge"); }
                else { f = above; cuts.Add((bottom ? "bottom" : "top") + " side moved to the title box edge"); }
            }
            if (cuts.Count > 0) cut = string.Join("; ", cuts);
            return f;
        }

        /// <summary>Each previous corner box needs a distinct current box within tolerance on all four edges.</summary>
        internal static bool HasDistinctCornerBoxMatches(IReadOnlyList<double[]> previous, IReadOnlyList<double[]> current, double tolerance)
        {
            if (previous.Count == 0) return true;
            if (previous.Count > current.Count) return false;
            var assignedPrevious = Enumerable.Repeat(-1, current.Count).ToArray();

            bool TryAssign(int previousIndex, bool[] visited)
            {
                for (int i = 0; i < current.Count; i++)
                {
                    if (visited[i] || !ViewCropGeometry.Matches(previous[previousIndex], current[i], tolerance)) continue;
                    visited[i] = true;
                    // If this box is already assigned, try to move its match to another box first.
                    if (assignedPrevious[i] >= 0 && !TryAssign(assignedPrevious[i], visited)) continue;
                    assignedPrevious[i] = previousIndex;
                    return true;
                }
                return false;
            }

            for (int i = 0; i < previous.Count; i++)
                if (!TryAssign(i, new bool[current.Count])) return false;
            return true;
        }

        /// <summary>Coordinates of lines (pos, from, to) whose collinear pieces cover at least minLength.</summary>
        private static List<double> LongLines(List<double[]> lines, double minLength, double tolerance)
        {
            var found = new List<double>();
            foreach (var group in Cluster(lines, tolerance))
            {
                double covered = 0, end = double.NegativeInfinity;
                foreach (double[] l in group.OrderBy(l => l[1]))
                {
                    double a = Math.Max(l[1], end);
                    if (l[2] > a) covered += l[2] - a;
                    end = Math.Max(end, l[2]);
                }
                if (covered >= minLength - tolerance) found.Add(group.Average(l => l[0]));
            }
            return found;
        }

        private static IEnumerable<List<double[]>> Cluster(List<double[]> lines, double tolerance)
        {
            List<double[]> current = null; double last = double.NaN;
            foreach (double[] l in lines.OrderBy(l => l[0]))
            {
                if (current == null || l[0] - last > tolerance) { if (current != null) yield return current; current = new List<double[]>(); }
                current.Add(l); last = l[0];
            }
            if (current != null) yield return current;
        }

        /// <summary>
        /// Rows from the top-left, bottoms aligned per row. Null with the reason when the
        /// items do not fit; otherwise the target extent of every item, by key.
        /// </summary>
        public static Dictionary<string, double[]> Rows(IList<LayoutItem> items, double[] usable, double gap, double tolerance, out string refusal)
        {
            refusal = null;
            if (items == null || items.Count == 0) { refusal = "the sheet has no viewport to lay out."; return null; }
            double width = usable[2] - usable[0];
            foreach (LayoutItem it in items)
                if (!(it.Width > 0) || !(it.Height > 0)) { refusal = "viewport " + it.Key + " has no readable extent."; return null; }
                else if (it.Width > width + tolerance)
                {
                    refusal = "viewport " + it.Key + " is " + Mm(it.Width) + " mm wide with its title; the usable width is " + Mm(width) +
                              " mm. Reduce margins/notes_width, or change its scale or crop first.";
                    return null;
                }
            var placed = new Dictionary<string, double[]>(StringComparer.Ordinal);
            double top = usable[3];
            int i = 0;
            while (i < items.Count)
            {
                var row = new List<LayoutItem>();
                double x = usable[0], rowHeight = 0;
                while (i < items.Count && (row.Count == 0 || x + gap + items[i].Width <= usable[2] + tolerance))
                {
                    if (row.Count > 0) x += gap;
                    row.Add(items[i]);
                    x += items[i].Width;
                    rowHeight = Math.Max(rowHeight, items[i].Height);
                    i++;
                }
                double bottom = top - rowHeight;
                double left = usable[0];
                foreach (LayoutItem it in row)
                {
                    placed[it.Key] = new[] { left, bottom, left + it.Width, bottom + it.Height };
                    left += it.Width + gap;
                }
                top = bottom - gap;
            }
            double lowest = placed.Values.Min(r => r[1]);
            if (lowest < usable[1] - tolerance)
            {
                double needed = usable[3] - lowest, have = usable[3] - usable[1];
                refusal = items.Count + " viewport(s) need " + Mm(needed) + " mm of height in rows at this scale; the usable height is " +
                          Mm(have) + " mm. Nothing was moved: reduce margins/gap, use a bigger title block, or split the sheet.";
                return null;
            }
            return placed;
        }

        /// <summary>Pairs of keys whose rectangles overlap by more than the tolerance.</summary>
        public static List<string[]> Overlaps(IList<KeyValuePair<string, double[]>> rects, double tolerance)
        {
            var found = new List<string[]>();
            for (int a = 0; a < rects.Count; a++)
                for (int b = a + 1; b < rects.Count; b++)
                {
                    if (IntersectionSize(rects[a].Value, rects[b].Value, tolerance).ExceedsTolerance)
                        found.Add(new[] { rects[a].Key, rects[b].Key });
                }
            return found;
        }

        /// <summary>True when r lies inside area within the tolerance.</summary>
        public static bool Inside(double[] area, double[] r, double tolerance)
            => area != null && r != null && r[0] >= area[0] - tolerance && r[1] >= area[1] - tolerance &&
               r[2] <= area[2] + tolerance && r[3] <= area[3] + tolerance;

        /// <summary>
        /// How far r sticks out of area on each side, [left, right, top, bottom] in the same units, or null
        /// when it is inside within the tolerance (5.0.4, B4b: a viewport over the title strip).
        /// </summary>
        public static double[] Protrusion(double[] area, double[] r, double tolerance)
        {
            if (area == null || r == null || Inside(area, r, tolerance)) return null;
            return new[]
            {
                Math.Max(0, area[0] - r[0]), Math.Max(0, r[2] - area[2]),
                Math.Max(0, r[3] - area[3]), Math.Max(0, area[1] - r[1])
            };
        }

        /// <summary>The paper move that puts the anchor rectangle's centre (or lower-left corner) on the point asked.</summary>
        public static double[] Delta(double[] anchor, double[] point, bool corner)
        {
            double[] from = corner ? new[] { anchor[0], anchor[1] } : Center(anchor);
            return new[] { point[0] - from[0], point[1] - from[1] };
        }

        public static JObject RectMm(double[] r)
            => r == null ? null : new JObject
            {
                ["min"] = new JArray(Mm(r[0]), Mm(r[1])), ["max"] = new JArray(Mm(r[2]), Mm(r[3])),
                ["center"] = new JArray(Mm((r[0] + r[2]) / 2), Mm((r[1] + r[3]) / 2))
            };

        public static double Mm(double feet) => Math.Round(feet * 304.8, 1);

        // ---- request shape ---------------------------------------------------------------

        private static readonly string[] Common = { "target_document", "operation", "dry_run", "confirmation_token", "idempotency_key" };

        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["get_viewport_box"] = new[] { "sheet_id", "viewport_id" },
            ["move_viewport"] = new[] { "viewport_id", "center", "corner", "anchor", "ink_box", "allow_unmarked" },
            ["auto_layout"] = new[] { "sheet_id", "margins", "notes_width", "gap", "repair_outside", "allow_unmarked" },
            // 4.2-N4 (CT-P1-4a, CT-P1-4b, LP-P3-11): see SheetGridRules.
            ["layout_grid"] = new[] { "sheet_id", "viewport_ids", "columns", "gap_x", "gap_y", "title_gap", "margins", "notes_width", "allow_unmarked" },
            ["set_view_scale"] = new[] { "viewport_id", "scale", "keep_viewport_center", "allow_unmarked" },
            ["rename"] = new[] { "items", "require_title_fit", "allow_unmarked" },
            ["set_title"] = new[] { "viewport_id", "viewport_ids", "title_gap", "line_length", "label_offset", "allow_unmarked" }
        };

        /// <summary>Null when the request is well formed for its operation; otherwise why not.</summary>
        public static string Validate(JObject r)
        {
            if (r == null) return "parameters must be a JSON object.";
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Fields.TryGetValue(op, out string[] own))
                return "operation must be one of " + string.Join(", ", Fields.Keys) + " for horizun_sheet_layout.";
            var allowed = new HashSet<string>(Common.Concat(own), StringComparer.Ordinal);
            foreach (JProperty p in r.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + op + "'. Nothing ran.";
            bool sheet = r["sheet_id"]?.Type == JTokenType.Integer, vp = r["viewport_id"]?.Type == JTokenType.Integer;
            if (op == "get_viewport_box" && sheet == vp) return "get_viewport_box takes exactly one of sheet_id or viewport_id.";
            if (op == "move_viewport")
            {
                if (!vp) return "viewport_id is required.";
                bool c = r["center"] != null, k = r["corner"] != null;
                if (c == k) return "move_viewport takes exactly one of center or corner ([x, y] in paper mm).";
                if (!ViewCropGeometry.Pair(r[c ? "center" : "corner"])) return (c ? "center" : "corner") + " must be [x, y] in paper mm.";
                string anchor = r.Value<string>("anchor");
                if (anchor != null && anchor != "crop" && anchor != "box" && anchor != "ink") return "anchor must be crop, box or ink.";
                // 5.2 (P2): anchor=ink places the PRINTED drawing, measured by horizun_export pdf_qa viewports.
                if ((anchor == "ink") != (r["ink_box"] != null))
                    return "anchor=ink and ink_box go together: ink_box is pdf_qa viewports[].ink_from_box_center_mm of this viewport.";
                if (anchor == "ink") { string ib = SheetPaperRules.ValidateInkBox(r["ink_box"]); if (ib != null) return ib; }
            }
            if (op == "auto_layout")
            {
                if (!sheet) return "sheet_id is required.";
                if (r["repair_outside"] != null && r["repair_outside"].Type != JTokenType.Boolean)
                    return "repair_outside must be a boolean.";
                if (r["margins"] != null && (!(r["margins"] is JArray m) || m.Count != 4 || m.Any(t => !ViewCropGeometry.Number(t) || t.Value<double>() < 0)))
                    return "margins must be [left, right, top, bottom], each >= 0 paper mm.";
                foreach (string n in new[] { "notes_width", "gap" })
                    if (r[n] != null && (!ViewCropGeometry.Number(r[n]) || r.Value<double>(n) < 0)) return n + " must be a non-negative number of paper mm.";
            }
            if (op == "layout_grid") { string g = SheetGridRules.ValidateGrid(r); if (g != null) return g; }
            if (op == "set_view_scale") { string g = SheetGridRules.ValidateScale(r); if (g != null) return g; }
            if (op == "rename") { string g = SheetGridRules.ValidateRename(r); if (g != null) return g; }
            if (op == "set_title") { string g = SheetGridRules.ValidateSetTitle(r); if (g != null) return g; }
            if (r["allow_unmarked"] != null && r["allow_unmarked"].Type != JTokenType.Boolean) return "allow_unmarked must be a boolean.";
            return null;
        }
    }
}
