// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of text notes verified on paper (horizun_annotate operation=text,
// 5.2, field feedback "detalles constructivos de Colon", Revit 2024). The API accepts a
// note and re-reads what was written, but the paper shows something else; every rule
// here answers "what will PRINT", from numbers the add-in measures:
//
//   width_mm        TextElement.Width is in PAPER feet. Multiplied by the view scale it
//                   gave a 1.75 m note; the width is validated against the type's own
//                   GetMinimumAllowedWidth / GetMaximumAllowedWidth (paper feet too).
//   lines           a width that is too small stops the wrap and the note prints as ONE
//                   long line while Width still re-reads what was written. The printed
//                   lines are counted from the note's box height on paper, calibrated with
//                   a one-line and a two-line probe of the same type (rehearsal only).
//   box trap        Element.get_BoundingBox(view) of an annotation came back with its
//                   coordinates multiplied by the view scale (everything "outside the
//                   crop"). The box is tested against the note's own anchor: a box that
//                   does not contain it but contains it once divided by the scale is that
//                   trap, corrected and said so; one that contains it neither way is
//                   reported unreliable, never used.
//   leader side     with the elbow inside the text box Revit draws the arrow from the
//                   OTHER side, across the sheet. The side is chosen (or checked) from the
//                   box span and the leader end; an end inside the span is refused.
//   leader target   the arrow is aimed by a ray from the leader end into the model (host
//                   and links, nested ones reported); the hit is compared with the element
//                   the caller named and a different hit is refused, naming it.
//   visibility      a 3D view with "show annotation categories" off creates the note and
//                   never shows it: the rehearsal says so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class TextNoteLayoutRules
    {
        /// <summary>Start at the centre and nearby positions, then sample smaller features toward the edges.</summary>
        internal static IEnumerable<(double Right, double Up)> AimFractions()
        {
            double[] near = { .5, .3, .7 };
            foreach (double up in near)
                foreach (double right in near)
                    yield return (right, up);

            double[] fine = { .025, .1, .2, .4, .5, .6, .8, .9, .975, .3, .7 };
            foreach (double up in fine)
                foreach (double right in fine)
                    if (Array.IndexOf(near, right) < 0 || Array.IndexOf(near, up) < 0)
                        yield return (right, up);
        }

        internal static string LeaderRayProblem(bool pointOnly, bool cast, string coverage, bool hit, string uncertainty)
        {
            if (!cast)
                return pointOnly ? null : "the aiming ray could not be cast (" + (uncertainty ?? coverage) +
                    "), so the element the arrow reaches cannot be proven.";
            if (coverage != "complete")
                return pointOnly ? null : "the aiming ray was not resolved (" + (uncertainty ?? coverage) +
                    "); a farther element is never substituted.";
            return hit ? null : "the leader end touches no element along the view direction (" +
                (uncertainty ?? "no detail") + "): the arrow would point at nothing.";
        }

        // Model-space tolerance. Tight enough to distinguish the drawn stroke from nearby geometry.
        internal const double PlanStrokeToleranceFeet = 0.1 / 304.8;

        internal static double PlanSegmentDistance(double x, double y, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared == 0 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / lengthSquared));
            double ex = x - (ax + t * dx), ey = y - (ay + t * dy);
            return Math.Sqrt(ex * ex + ey * ey);
        }

        // A vertical host probe must be transformed at both ends. A linked model's vertical can
        // differ from the host's, and transforming only its centre would test a different line.
        internal static (T Start, T End) ProbeInElementSpace<T>(T start, T end, Func<T, T> fromHost)
            => (fromHost(start), fromHost(end));

        internal static bool ConfirmsTarget(bool cast, string coverage, bool matchesIdentity)
            => cast && coverage == "complete" && matchesIdentity;

        internal static string AimMissDetail(int samples) => samples == 0
            ? "No geometry or bounding box supplied an aim point. "
            : "None of the tested points produced a measured hit. ";

        internal static TPoint FirstAim<TPoint, THit>(IEnumerable<TPoint> points, Func<TPoint, THit> measure,
            Func<THit, bool> confirms, out THit evidence) where TPoint : class where THit : class
        {
            evidence = null;
            foreach (TPoint point in points)
            {
                THit hit = measure(point);
                if (evidence == null) evidence = hit;
                if (!confirms(hit)) continue;
                evidence = hit;
                return point;
            }
            return null;
        }

        public const double MmPerFoot = 304.8;
        /// <summary>Width re-read tolerance, paper millimetres.</summary>
        public const double WidthToleranceMm = 0.1;
        /// <summary>How far, in paper millimetres, the elbow must clear the text box edge.</summary>
        public const double ElbowClearanceMm = 0.5;
        /// <summary>A line count is accepted only when the measured height is this close (in lines) to a whole number.</summary>
        public const double LineFractionTolerance = 0.3;

        /// <summary>Fields that only operation=text accepts.</summary>
        public static readonly string[] TextOnlyFields = { "width_mm", "expected_lines", "leader_target", "leader_side", "leader_attachment" };

        /// <summary>The text-only fields a non-text action carries (they would otherwise be dropped silently), or null.</summary>
        public static string ForeignFields(string operation, Func<string, bool> has)
        {
            if (operation == "text") return null;
            var found = new List<string>();
            foreach (string f in TextOnlyFields) if (has(f)) found.Add(f);
            return found.Count == 0 ? null
                : operation + " does not carry " + string.Join(", ", found) + " - those are text-note fields, and accepting them here would drop them silently. Remove them, or use operation=text.";
        }

        // ---- 1. width in paper millimetres --------------------------------------------------------

        public static double PaperFeet(double paperMm) => paperMm / MmPerFoot;

        /// <summary>
        /// Null when <paramref name="widthMm"/> is a width Revit will lay this type out at; otherwise the sentence. When the
        /// value is too large but would fit once divided by the view scale, it was given in MODEL millimetres and the
        /// sentence says so with the paper value.
        /// </summary>
        public static string WidthProblem(double widthMm, double minPaperFeet, double maxPaperFeet, double viewScale)
        {
            if (double.IsNaN(widthMm) || double.IsInfinity(widthMm) || widthMm <= 0)
                return "width_mm must be a positive, finite PAPER length in millimetres.";
            double min = minPaperFeet * MmPerFoot, max = maxPaperFeet * MmPerFoot;
            if (widthMm < min - WidthToleranceMm)
                return "width_mm " + F(widthMm) + " mm is below the " + F(min) + " mm this text type allows on paper " +
                       "(TextElement.GetMinimumAllowedWidth); a narrower box makes Revit stop wrapping and print one long line. Nothing was written.";
            if (widthMm > max + WidthToleranceMm)
            {
                string hint = viewScale > 1 && widthMm / viewScale >= min - WidthToleranceMm && widthMm / viewScale <= max + WidthToleranceMm
                    ? " It looks like a MODEL length: width_mm is PAPER millimetres (TextElement.Width is paper feet), so at 1:" +
                      F(viewScale) + " the paper width is " + F(widthMm / viewScale) + " mm."
                    : "";
                return "width_mm " + F(widthMm) + " mm is above the " + F(max) + " mm this text type allows on paper " +
                       "(TextElement.GetMaximumAllowedWidth)." + hint + " Nothing was written.";
            }
            return null;
        }

        /// <summary>A view without an active crop: no note wider than an A1 sheet (841 mm) can be meant on paper.</summary>
        public const double UncroppedPaperLimitMm = 841.0;
        /// <summary>A sheet has no crop and no scale: its title block's width is the limit, and only when none can be read this ARCH E long edge (36 in).</summary>
        public const double SheetPaperLimitMm = 1219.0;

        /// <summary>A note whose base direction is within this many degrees of vertical runs along the crop's HEIGHT.</summary>
        public const double RotatedNearVerticalDegrees = 10.0;

        /// <summary>True when a note rotated by <paramref name="rotationDegrees"/> lies along the vertical of the view (about 90 or 270 degrees).</summary>
        public static bool RunsAlongHeight(double rotationDegrees)
        {
            if (double.IsNaN(rotationDegrees) || double.IsInfinity(rotationDegrees)) return false;
            double a = Math.Abs(((rotationDegrees % 180.0) + 180.0) % 180.0 - 90.0);
            return a <= RotatedNearVerticalDegrees;
        }

        /// <summary>
        /// A width that Revit accepts (inside the type's allowed range) but that cannot fit the view it is placed in: wider
        /// than the paper width of the view's active crop (<paramref name="cropPaperWidthMm"/>, the crop width divided by the
        /// scale) or, with no crop, wider than <paramref name="uncroppedLimitMm"/>. The note then has no box in the view
        /// (field, 1:16 view, 153 mm of crop on paper, width_mm 2000: accepted and verified, nothing visible). Null when the
        /// width fits or <paramref name="allowOutsideCrop"/> was said on purpose; otherwise the sentence, with the paper width
        /// that would be meant if the value was a MODEL length (width_mm / scale).
        /// </summary>
        public static string WidthFrameProblem(double widthMm, double viewScale, double? cropPaperWidthMm, double? cropPaperHeightMm, double rotationDegrees,
                                               double uncroppedLimitMm, bool allowOutsideCrop)
        {
            if (allowOutsideCrop || double.IsNaN(widthMm) || double.IsInfinity(widthMm) || widthMm <= 0) return null;
            // a note turned to about 90 degrees runs along the crop's HEIGHT
            bool vertical = RunsAlongHeight(rotationDegrees);
            double? along = vertical ? cropPaperHeightMm : cropPaperWidthMm;
            bool cropped = along.HasValue && along.Value > 0 && !double.IsNaN(along.Value) && !double.IsInfinity(along.Value);
            double limit = cropped ? along.Value : uncroppedLimitMm;
            if (widthMm <= limit + WidthToleranceMm) return null;
            string suggestion = viewScale > 1
                ? " If " + F(widthMm) + " was a MODEL length, the paper width at 1:" + F(viewScale) + " is " + F(widthMm / viewScale) + " mm: send width_mm " + F(widthMm / viewScale) + "."
                : "";
            return "width_mm " + F(widthMm) + " mm (PAPER millimetres) does not fit " +
                   (cropped ? "the active crop of this view, which is " + F(limit) + " mm " + (vertical ? "high" : "wide") + " on paper at 1:" + F(viewScale)
                            : "a view with no active crop (the limit is " + F(limit) + " mm of paper)") +
                   ": the note would have no box in the view." + suggestion +
                   " To place it that wide on purpose, send allow_outside_crop: true. Nothing was written.";
        }

        /// <summary>
        /// Does a ray cast from the leader end count as hitting the element the caller named? In a plan or ceiling plan the ray
        /// starts AT the cut plane, so an element the plane cuts (a wall) is the thing the view shows there but the ray starts
        /// INSIDE its solid and ReferenceIntersector reports the next face down (field: a floor, never the cut wall). A start
        /// point inside the named element's solid therefore counts as hitting it, in plan views only, and only when
        /// <see cref="InsideHitBlocker"/> finds nothing against it; in any other view the first face the ray meets decides.
        /// </summary>
        public static bool CutPlaneInsideCountsAsHit(bool rayFirstHitIsTarget, string insideBlocker, bool startInsideTargetSolid)
            => rayFirstHitIsTarget || (insideBlocker == null && startInsideTargetSolid);

        /// <summary>
        /// Why a start point inside the element's solid may NOT stand for "the view shows this element there" - null when nothing
        /// is against it. Every fact must be proven: a plan view whose cut plane was really read, a host element (a link's
        /// visibility cannot be proven from here), shown in the view (not hidden in it, its category not hidden, found by the
        /// view's own collector) and neither a room/space/area nor a mass (volumes that are never what the plan shows).
        /// </summary>
        public static string InsideHitBlocker(bool viewIsPlan, bool cutPlaneRead, bool targetInLink, bool hiddenInView,
                                              bool categoryVisible, bool inViewCollector, bool spatialOrMass)
        {
            if (!viewIsPlan) return "not a plan view";
            if (!cutPlaneRead) return "the view's cut plane could not be read";
            if (targetInLink) return "the element is in a link, whose visibility in this view cannot be proven";
            if (spatialOrMass) return "a room, space, area or mass is a volume, never what the plan shows";
            if (hiddenInView) return "the element is hidden in this view";
            if (!categoryVisible) return "its category is hidden (or its visibility unreadable) in this view";
            if (!inViewCollector) return "the view's own element collector does not list it";
            return null;
        }

        /// <summary>
        /// A probe segment through the start point lies INSIDE a solid only when a segment of the intersection covers it on both
        /// sides: <paramref name="segmentLength"/> at least 90 % of the probe and the point on the segment. A face touch (half
        /// the probe, or none) is not inside.
        /// </summary>
        public static bool SegmentProvesInside(double segmentLength, double probeLength, double pointDistanceToSegment)
            => probeLength > 0 && segmentLength >= 0.9 * probeLength && pointDistanceToSegment <= 1e-6;

        public static bool WidthMatches(double requestedPaperFeet, double readPaperFeet)
            => Math.Abs(requestedPaperFeet - readPaperFeet) * MmPerFoot <= WidthToleranceMm;

        // ---- 2. printed lines ------------------------------------------------------------------

        /// <summary>The lines the text itself forces (hard breaks), whatever the width: CR, LF and CRLF each break once.</summary>
        public static int HardLineCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            string t = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            int n = 1;
            foreach (char c in t) if (c == '\n') n++;
            return n;
        }

        /// <summary>
        /// Lines printed by a box of <paramref name="height"/>, from a one-line box of <paramref name="oneLine"/> and the
        /// extra height of each further line <paramref name="pitch"/> (all in the same paper unit). Null when the
        /// calibration is unusable or the height sits between two whole lines - an ambiguous count is not a count.
        /// </summary>
        public static int? LinesFromHeight(double height, double oneLine, double pitch)
        {
            if (!(pitch > 1e-9) || !(oneLine > 1e-9) || double.IsNaN(height) || double.IsInfinity(height) || height <= 0) return null;
            double n = 1.0 + (height - oneLine) / pitch;
            double rounded = Math.Round(n);
            if (rounded < 1 || Math.Abs(n - rounded) > LineFractionTolerance) return null;
            return (int)rounded;
        }

        /// <summary>
        /// The line verdict: what the height measured, what the box measured (either may be null), what was expected.
        /// Two measurements that disagree make the count UNKNOWN; an expectation with an unknown count fails closed.
        /// </summary>
        /// <summary>
        /// The view box counts lines only for a note whose base direction runs along the view's right direction (0 or 180
        /// degrees) in a view that is not 3D: get_BoundingBox(view) is axis-aligned, so a rotated note's box height is its
        /// WIDTH (90 degrees) or a mix, and in a 3D view it is a world box. Null when the box count applies, otherwise
        /// "skipped_rotated_or_3d" and the height count stands alone.
        /// </summary>
        public static string BoxCountSkip(double baseDirectionDotRight, bool viewIs3D)
            => viewIs3D || double.IsNaN(baseDirectionDotRight) || Math.Abs(Math.Abs(baseDirectionDotRight) - 1.0) > 1e-6
                ? "skipped_rotated_or_3d" : null;

        public static JObject LinesVerdict(int? expected, int? byHeight, int? byBox, int hardLines, out bool ok)
        {
            int? measured = byHeight ?? byBox;
            string disagreement = null;
            if (byHeight.HasValue && byBox.HasValue && byHeight.Value != byBox.Value)
            {
                measured = null;
                disagreement = "the box height on paper says " + byHeight.Value + " line(s) and the view box says " + byBox.Value + "; the count is unknown";
            }
            var o = new JObject
            {
                ["lines_measured"] = measured.HasValue ? (JToken)measured.Value : JValue.CreateNull(),
                ["lines_by_height"] = byHeight.HasValue ? (JToken)byHeight.Value : JValue.CreateNull(),
                ["lines_by_view_box"] = byBox.HasValue ? (JToken)byBox.Value : JValue.CreateNull(),
                ["hard_line_breaks"] = hardLines - 1,
                ["expected_lines"] = expected.HasValue ? (JToken)expected.Value : JValue.CreateNull()
            };
            if (disagreement != null) o["lines_uncertain"] = disagreement;
            if (!expected.HasValue) { ok = true; return o; }
            if (!measured.HasValue)
            {
                ok = false;
                o["lines_reason"] = "expected_lines is " + expected.Value + " but the printed lines could not be measured" +
                                    (disagreement != null ? " (" + disagreement + ")" : "") + "; an unmeasured count is never a pass";
                return o;
            }
            ok = measured.Value == expected.Value;
            if (!ok)
                o["lines_reason"] = "the note prints " + measured.Value + " line(s), expected_lines is " + expected.Value +
                                    (measured.Value == 1 && expected.Value > 1
                                        ? ": the text did not wrap - widen or narrow width_mm within the type's allowed range, or break the line yourself"
                                        : ": change width_mm or the text");
            return o;
        }

        // ---- the get_BoundingBox(view) trap -----------------------------------------------------

        public sealed class BoxFix
        {
            /// <summary>[minR, minU, maxR, maxU] in model units of the view frame, or null when unreliable.</summary>
            public double[] Box;
            public bool Corrected;
            /// <summary>as_read | divided_by_view_scale | unreliable</summary>
            public string Method;
        }

        /// <summary>
        /// An annotation's box in the view frame, checked against the annotation's own anchor (note Coord, tag head).
        /// A raw box that contains the anchor is used as read. One that does not, but contains it once every coordinate
        /// is divided by the view scale, is the measured trap (coordinates multiplied by the scale) and is corrected. One
        /// that contains it neither way is unreliable and returned as null - never used as a measurement.
        /// <paramref name="tolerance"/> is in model units.
        /// </summary>
        public static BoxFix NormalizeAnnotationBox(double[] raw, double[] anchor, double viewScale, double tolerance)
        {
            if (raw == null || raw.Length != 4 || anchor == null || anchor.Length != 2) return new BoxFix { Method = "unreliable" };
            if (Contains(raw, anchor, tolerance)) return new BoxFix { Box = raw, Method = "as_read" };
            if (viewScale > 1)
            {
                double[] scaled = { raw[0] / viewScale, raw[1] / viewScale, raw[2] / viewScale, raw[3] / viewScale };
                if (Contains(scaled, anchor, tolerance)) return new BoxFix { Box = scaled, Corrected = true, Method = "divided_by_view_scale" };
            }
            return new BoxFix { Method = "unreliable" };
        }

        private static bool Contains(double[] b, double[] p, double tol)
            => p[0] >= Math.Min(b[0], b[2]) - tol && p[0] <= Math.Max(b[0], b[2]) + tol &&
               p[1] >= Math.Min(b[1], b[3]) - tol && p[1] <= Math.Max(b[1], b[3]) + tol;

        // ---- 4. the side the leader leaves from ---------------------------------------------------

        /// <summary>The text box's extent along the note's base direction: [low, high], in the unit of the inputs (paper mm in use).</summary>
        public static double[] BoxSpan(string horizontalAlignment, double coordAlong, double widthModel)
        {
            switch ((horizontalAlignment ?? "left").ToLowerInvariant())
            {
                case "right": return new[] { coordAlong - widthModel, coordAlong };
                case "center": return new[] { coordAlong - widthModel / 2.0, coordAlong + widthModel / 2.0 };
                default: return new[] { coordAlong, coordAlong + widthModel };
            }
        }

        /// <summary>
        /// The side (left | right) the leader leaves from, so that the elbow - half way from the anchor at that edge to
        /// the end - stays OUT of the text box. An end inside the box span is refused for every side: whichever edge it
        /// leaves from, the shoulder would run back across the text and Revit flips the arrow to the other side.
        /// </summary>
        public static string ResolveSide(string requested, double low, double high, double endAlong, double clearance, out string error)
        {
            error = null;
            string side = (requested ?? "auto").ToLowerInvariant();
            bool rightOk = endAlong >= high + 2 * clearance, leftOk = endAlong <= low - 2 * clearance;
            if (side == "auto")
            {
                if (rightOk) return "right";
                if (leftOk) return "left";
            }
            else if (side == "right" && rightOk) return "right";
            else if (side == "left" && leftOk) return "left";
            else if (side != "right" && side != "left")
            {
                error = "leader_side must be auto, left or right.";
                return null;
            }
            error = "the leader end is " + F(endAlong) + " mm along the note, and the text box spans " + F(low) + " .. " + F(high) +
                    " mm (paper; the end must clear the " + (side == "auto" ? "left or right" : side) + " edge by " + F(2 * clearance) +
                    " mm): the elbow would fall inside the box and Revit draws the arrow from the " +
                    "OTHER side, across the sheet. Move the note so the end is clear of its " + (side == "auto" ? "left or right" : side) +
                    " edge, change leader_side, or narrow width_mm. Nothing was written.";
            return null;
        }

        /// <summary>True when <paramref name="elbowAlong"/> lies inside the box span by more than the clearance.</summary>
        public static bool ElbowInsideBox(double elbowAlong, double low, double high, double clearance)
            => elbowAlong > low + clearance && elbowAlong < high - clearance;

        /// <summary>The anchor sits on the edge of the chosen side, within the tolerance.</summary>
        public static bool AnchorOnSide(string side, double anchorAlong, double low, double high, double tolerance)
            => Math.Abs(anchorAlong - (side == "left" ? low : high)) <= tolerance;

        // ---- 3. what the leader points at ------------------------------------------------------

        /// <summary>
        /// The form of a leader_target object, or an error. Forms: point (a ray from the point, whatever it hits),
        /// element (a host element: from point, or aimed at its box), linked_element (an element of a link, also nested).
        /// </summary>
        public static string LeaderTargetForm(bool hasPoint, bool hasElement, bool hasLinkInstance, bool hasLinkedElement, out string error)
        {
            error = null;
            if (hasElement && (hasLinkInstance || hasLinkedElement))
            { error = "leader_target takes element_id OR link_instance_id + linked_element_id, not both."; return null; }
            if (hasLinkInstance != hasLinkedElement)
            { error = "leader_target needs link_instance_id AND linked_element_id together for an element of a link."; return null; }
            if (hasLinkInstance) return "linked_element";
            if (hasElement) return "element";
            if (hasPoint) return "point";
            error = "leader_target needs point, element_id, or link_instance_id + linked_element_id.";
            return null;
        }

        /// <summary>The refusal when the ray from the leader end meets another element than the one named.</summary>
        public static string HitMismatch(string expected, string hitCategory, long hitId, string hitType, string hitDocument, long? hitLinkInstanceId, int linkDepth)
            => "the ray from the leader end meets " + (string.IsNullOrEmpty(hitCategory) ? "an element" : hitCategory) + " " +
               hitId.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(hitType) ? "" : " (" + hitType + ")") +
               (hitLinkInstanceId.HasValue
                   ? " in link instance " + hitLinkInstanceId.Value.ToString(CultureInfo.InvariantCulture) +
                     (string.IsNullOrEmpty(hitDocument) ? "" : " '" + hitDocument + "'") + (linkDepth > 1 ? " (nested " + linkDepth + " levels)" : "")
                   : "") +
               " first, not " + expected + ": the arrow would point at the wrong thing. Move leader_target.point onto the element, " +
               "or name the element it hits. Nothing was written.";

        // ---- 5. a note nobody will see -----------------------------------------------------------

        /// <summary>The warning for a note created where it will not show, or null.</summary>
        public static string HiddenWarning(bool annotationCategoriesHidden, bool textCategoryHidden, bool byTemplate, bool is3D)
        {
            if (!annotationCategoriesHidden && !textCategoryHidden) return null;
            string what = annotationCategoriesHidden
                ? "\"Show annotation categories in this view\" is OFF" : "the Text Notes category is hidden";
            return "the note will be created but will NOT be visible: " + what + (byTemplate ? " (set by the view template)" : "") + " in this view" +
                   (is3D ? "; in a 3D/isometric view turning annotation categories on also shows levels and grids, which then need hiding" : "") + ".";
        }

        // ---- the ray origin in an orthographic 3D view -----------------------------------------------

        /// <summary>
        /// Where a ray along <paramref name="dir"/> through <paramref name="origin"/> first enters the axis-aligned box
        /// [min, max] (all in the box's own frame): the parameter t along dir, or null when it misses. A negative t means
        /// the origin is already past the entry face. Slab method.
        /// </summary>
        public static double? RayBoxEntry(double[] origin, double[] dir, double[] min, double[] max)
            => RayBoxInterval(origin, dir, min, max)?.Entry;

        internal static (double Entry, double Exit)? RayBoxInterval(double[] origin, double[] dir, double[] min, double[] max)
        {
            double tMin = double.NegativeInfinity, tMax = double.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(dir[i]) < 1e-12)
                {
                    if (origin[i] < min[i] || origin[i] > max[i]) return null;
                    continue;
                }
                double t1 = (min[i] - origin[i]) / dir[i], t2 = (max[i] - origin[i]) / dir[i];
                if (t1 > t2) { double s = t1; t1 = t2; t2 = s; }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return null;
            }
            return (tMin, tMax);
        }

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
