// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-FREE.
//
// THE ARITHMETIC OF horizun_query_planimetry mode=section_mark_diagnosis.
//
// Field (Revit 2024): a section mark showed up in a plan only when that plan's view range
// crossed the Z of the section, so "impossible marks" were really a view range that did not
// reach the section. Each cause below is measured and carries its numbers; a fact that could
// not be read is "unknown" with its reason, never 0 and never "no".
//
// Every length here is millimetres in MODEL coordinates (internal origin), the same frame as
// the section's crop box once its transform is applied.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class SectionMarkCategories
    {
        // These internal annotation categories can be collected by built-in id
        // even when the document does not expose a Category object for them.
        internal static readonly string[] BuiltInNames = { "OST_Sections", "OST_Viewers" };

        internal static long? QueryId(string name, long? categoryId, Func<string, long> builtInId)
        {
            foreach (string native in BuiltInNames)
                if (string.Equals(name, native, StringComparison.OrdinalIgnoreCase))
                    return builtInId(native);
            return categoryId;
        }

        internal static bool MatchesQuery(ISet<long> requested, bool fromCategoryCollector,
            Func<long?> managedCategory, Func<bool> matchesNativeMark)
        {
            if (requested == null) return true;
            if (fromCategoryCollector) return requested.Count > 0;
            long? managed = managedCategory();
            return managed.HasValue ? requested.Contains(managed.Value) : matchesNativeMark();
        }
    }

    /// <summary>One plane of a plan view range, in absolute model Z: a value, open (no limit) or unknown with a reason.</summary>
    public sealed class ZBound
    {
        public double? Mm;
        public bool Open;
        public string Reason;
        public static ZBound At(double mm) => new ZBound { Mm = mm };
        public static ZBound Unlimited() => new ZBound { Open = true };
        public static ZBound Unknown(string reason) => new ZBound { Reason = reason };
        public bool IsKnown => Mm.HasValue || Open;
        public JToken ToJson() => Mm.HasValue ? (JToken)Math.Round(Mm.Value, 1) : Open ? "unlimited" : (JToken)new JObject { ["unknown"] = Reason };
    }

    /// <summary>The verdict of one filter of the view over the section: what the view does with it and whether it matches.</summary>
    public sealed class FilterVerdict
    {
        public string Name;
        public bool Visible;          // the visibility checkbox of the filter in this view
        public bool? Enabled;         // the filter is enabled in the view (a disabled filter hides nothing); null = unreadable
        public bool? Matches;        // null when the match could not be evaluated
        public string Reason;
    }

    public static class SectionMarkRules
    {
        public const double EpsilonMm = 0.01;
        public static readonly string[] Blocks = { "yes", "no", "unknown", "not_applicable" };

        private static JObject Cause(string code, string blocks, JObject detail, string reason = null)
        {
            var o = new JObject { ["cause"] = code, ["blocks_mark"] = blocks };
            if (reason != null) o["reason"] = reason;
            if (detail != null) o["measured"] = detail;
            return o;
        }

        // ---- view range against the Z of the section ------------------------------------------

        /// <summary>The section's Z extent (min/max of its crop box in model Z) against cut / top / bottom / view depth.</summary>
        public static JObject ZRange(ZBound cut, ZBound top, ZBound bottom, ZBound depth, double? secMinMm, double? secMaxMm, string secReason)
        {
            var d = new JObject { ["cut_plane_z_mm"] = cut.ToJson(), ["top_z_mm"] = top.ToJson(), ["bottom_z_mm"] = bottom.ToJson(), ["view_depth_z_mm"] = depth.ToJson() };
            if (!secMinMm.HasValue || !secMaxMm.HasValue)
                return Cause("view_range", "unknown", d, secReason ?? "the section's crop box could not be read");
            double lo = Math.Min(secMinMm.Value, secMaxMm.Value), hi = Math.Max(secMinMm.Value, secMaxMm.Value);
            d["section_z_min_mm"] = Math.Round(lo, 1); d["section_z_max_mm"] = Math.Round(hi, 1);
            if (!top.IsKnown || !bottom.IsKnown || !depth.IsKnown)
                return Cause("view_range", "unknown", d, "a plane of the view range could not be read: " + (top.Reason ?? bottom.Reason ?? depth.Reason));

            double topMm = top.Open ? double.PositiveInfinity : top.Mm.Value;
            double botMm = bottom.Open ? double.NegativeInfinity : bottom.Mm.Value;
            double depMm = depth.Open ? double.NegativeInfinity : depth.Mm.Value;
            if (hi >= botMm - EpsilonMm && lo <= topMm + EpsilonMm)
            {
                if (!cut.Mm.HasValue)
                    return Cause("view_range", "unknown", d, "the cut plane could not be read: " + (cut.Reason ?? "unknown"));
                if (cut.Mm.Value < lo - EpsilonMm || cut.Mm.Value > hi + EpsilonMm)
                {
                    d["relation"] = "crosses_range_not_cut_plane";
                    return Cause("view_range", "unknown", d, "the section crosses the view range but not its cut plane; whether Revit draws the mark then is not established, measure it");
                }
                d["relation"] = "crosses_primary_range";
                return Cause("view_range", "no", d);
            }
            if (lo > topMm)
            {
                d["relation"] = "above_top"; d["gap_mm"] = Math.Round(lo - topMm, 1);
                return Cause("view_range", "yes", d, "the section lies entirely above the top of the view range");
            }
            // below the bottom plane: inside the view-depth band, or beyond it
            if (hi >= depMm - EpsilonMm)
            {
                d["relation"] = "only_in_view_depth_band"; d["gap_mm"] = Math.Round(botMm - hi, 1);
                return Cause("view_range", "unknown", d, "the section lies only between the bottom and the view depth; whether Revit draws the mark there is not established, measure it");
            }
            d["relation"] = "below_view_depth"; d["gap_mm"] = Math.Round(depMm - hi, 1);
            return Cause("view_range", "yes", d, "the section lies entirely below the view depth");
        }

        /// <summary>A view that is not a plan has no view range: the range check does not apply.</summary>
        public static JObject ZRangeNotApplicable(string viewType) =>
            Cause("view_range", "not_applicable", new JObject { ["view_type"] = viewType }, "only plan views carry a view range");

        // ---- hide at scales coarser than -------------------------------------------------------

        /// <summary>The section hides its mark in views whose scale (the 100 of 1:100) is coarser than its own limit.</summary>
        public static JObject Scale(int? viewScale, int? hideCoarserThan, string reason)
        {
            var d = new JObject { ["view_scale"] = viewScale.HasValue ? (JToken)viewScale.Value : null, ["hide_coarser_than"] = hideCoarserThan.HasValue ? (JToken)hideCoarserThan.Value : null };
            if (!viewScale.HasValue) return Cause("hide_at_coarser_scale", "unknown", d, reason ?? "the view scale could not be read");
            if (!hideCoarserThan.HasValue) return Cause("hide_at_coarser_scale", "unknown", d, reason ?? "the section's limit could not be read");
            if (hideCoarserThan.Value <= 0) return Cause("hide_at_coarser_scale", "unknown", d, "the section's limit is not a positive scale");
            return viewScale.Value > hideCoarserThan.Value
                ? Cause("hide_at_coarser_scale", "yes", d, "the view's 1:" + viewScale.Value + " is coarser than the section's 1:" + hideCoarserThan.Value)
                : Cause("hide_at_coarser_scale", "no", d);
        }

        // ---- category hidden -------------------------------------------------------------------

        /// <summary>The Sections / Viewers categories hidden in the view (the answer already includes a view template's control).</summary>
        public static JObject CategoryHidden(IList<KeyValuePair<string, bool?>> categories, string templateName,
            IDictionary<string, JObject> evidence = null)
        {
            var d = new JObject { ["view_template"] = templateName };
            var rows = new JArray();
            foreach (var c in categories)
            {
                var row = new JObject { ["category"] = c.Key, ["hidden"] = c.Value.HasValue ? (JToken)c.Value.Value : null };
                if (evidence != null && evidence.TryGetValue(c.Key, out JObject ev) && ev != null)
                    foreach (JProperty pr in ev.Properties()) row[pr.Name] = pr.Value;
                rows.Add(row);
            }
            d["categories"] = rows;
            if (categories.Any(c => c.Value == true))
            {
                bool byTemplate = evidence == null ? templateName != null : categories.Any(c => c.Value == true && evidence.TryGetValue(c.Key, out JObject ev) && ev != null && (string)ev["read_from"] == "view_template");
                return Cause("category_hidden", "yes", d, byTemplate && templateName != null
                    ? "the category is hidden by the view template that controls annotation visibility: " + templateName
                    : "the category is hidden in this view");
            }
            if (categories.Count == 0 || categories.Any(c => !c.Value.HasValue))
                return Cause("category_hidden", "unknown", d, "visibility of a category could not be read");
            return Cause("category_hidden", "no", d);
        }

        // ---- view filters ---------------------------------------------------------------------

        /// <summary>A filter hides when it is enabled AND its visibility is off AND it matches the viewer of the section.</summary>
        public static JObject Filters(IList<FilterVerdict> filters, string unreadableReason)
        {
            var rows = new JArray();
            foreach (FilterVerdict f in filters)
                rows.Add(new JObject { ["filter"] = f.Name, ["visible"] = f.Visible, ["enabled"] = f.Enabled.HasValue ? (JToken)f.Enabled.Value : null,
                    ["matches_section"] = f.Matches.HasValue ? (JToken)f.Matches.Value : null, ["reason"] = f.Reason });
            var d = new JObject { ["filters_that_cover_the_category"] = rows };
            if (unreadableReason != null) return Cause("view_filter", "unknown", d, unreadableReason);
            if (filters.Any(f => f.Enabled == true && !f.Visible && f.Matches == true))
                return Cause("view_filter", "yes", d, "an enabled filter with visibility off matches the viewer of the section");
            if (filters.Any(f => f.Enabled != false && !f.Visible && (!f.Enabled.HasValue || !f.Matches.HasValue)))
                return Cause("view_filter", "unknown", d, "a filter with visibility off could not be fully evaluated (enabled state or match unreadable)");
            return Cause("view_filter", "no", d);
        }

        // ---- phase ----------------------------------------------------------------------------

        /// <summary>The phase filter's presentation of the section on the view's phase (DontShow hides it).</summary>
        public static JObject Phase(string presentation, string phaseName, string filterName, string reason)
        {
            var d = new JObject { ["view_phase"] = phaseName, ["phase_filter"] = filterName, ["presentation"] = presentation };
            if (presentation == null) return Cause("phase", "unknown", d, reason ?? "the phase status could not be read");
            if (presentation == "NotApplicable") return Cause("phase", "not_applicable", d, "the viewer of the section carries no phase status");
            return presentation == "DontShow"
                ? Cause("phase", "yes", d, "the view's phase filter does not show the section in this phase")
                : Cause("phase", "no", d);
        }

        // ---- crop of the view -------------------------------------------------------------------

        /// <summary>Outside a plan the crop frame is another one (elevation, section): not measured here, so unknown, never "not applicable".</summary>
        public static JObject CropNotMeasured(string viewType) =>
            Cause("outside_view_crop", "unknown", new JObject { ["view_type"] = viewType }, "the crop is measured only in plan views; in a " + viewType + " it was not measured");

        // ---- which elements count as the mark of a section -----------------------------------------

        /// <summary>Null when the element can be the mark (a viewer element that points at no other view); otherwise why it is discarded.</summary>
        public static string DiscardReason(bool isView, bool isMarkCategory, bool referencesOtherView, bool drawnInSection, bool nameEqualsSection, bool otherViewHasThatName)
        {
            if (isView) return "dependent_view";
            if (!isMarkCategory) return "not_a_viewer";
            if (referencesOtherView) return "references_other_view";
            if (drawnInSection) return "drawn_in_section";                 // a callout or detail head drawn inside the section
            if (!nameEqualsSection) return otherViewHasThatName ? "viewer_of_other_view" : "name_differs";
            return null;
        }

        /// <summary>Category membership by raw id: BuiltInCategory values are ints, and Category.GetCategory(doc, ...) can return null for
        /// Viewers, which made every candidate "not a viewer" when its id was compared with the id of a null category.</summary>
        public static bool IsMarkCategory(long categoryId, IEnumerable<long> markCategoryIds) => markCategoryIds.Contains(categoryId);

        /// <summary>Is the mark in the view? valid = candidates that passed DiscardReason; inView = how many of them the view holds.
        /// Some in and some out cannot be told apart: unknown, never visible.</summary>
        public static bool? MarkFound(int valid, int inView, out string reason)
        {
            reason = null;
            if (valid == 0) { reason = "no viewer element of the section was found"; return null; }
            if (inView == 0) return false;
            if (inView == valid) return true;
            reason = inView + " of the " + valid + " viewer elements of the section are in the view: which one is the mark cannot be told apart";
            return null;
        }

        /// <summary>nonRectangular: null when the crop shape could not be read (unknown, never taken as rectangular).</summary>
        public static JObject Crop(bool cropActive, bool? nonRectangular, double[] segment, double[] rect, string reason)
        {
            if (!cropActive) return Cause("outside_view_crop", "no", new JObject { ["crop_active"] = false });
            var d = new JObject { ["crop_active"] = true };
            if (!nonRectangular.HasValue) return Cause("outside_view_crop", "unknown", d, "the shape of the crop could not be read");
            if (nonRectangular.Value) return Cause("outside_view_crop", "unknown", d, "the crop has a non-rectangular shape; only its bounding rectangle is readable here");
            if (segment == null || rect == null) return Cause("outside_view_crop", "unknown", d, reason ?? "the section line or the crop could not be read");
            d["section_line_mm"] = new JArray(segment.Select(v => (JToken)Math.Round(v, 1)));
            d["crop_rect_mm"] = new JArray(rect.Select(v => (JToken)Math.Round(v, 1)));
            return SegmentIntersectsRect(segment[0], segment[1], segment[2], segment[3], rect[0], rect[1], rect[2], rect[3])
                ? Cause("outside_view_crop", "no", d)
                : Cause("outside_view_crop", "yes", d, "the section line lies entirely outside the crop of the view");
        }

        /// <summary>Liang-Barsky: does the segment (x0,y0)-(x1,y1) touch the rectangle [minX,maxX] x [minY,maxY]?</summary>
        public static bool SegmentIntersectsRect(double x0, double y0, double x1, double y1, double minX, double minY, double maxX, double maxY)
        {
            double dx = x1 - x0, dy = y1 - y0, t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x0 - minX, maxX - x0, y0 - minY, maxY - y0 };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12) { if (q[i] < 0) return false; continue; }
                double r = q[i] / p[i];
                if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            return true;
        }

        // ---- the verdict ----------------------------------------------------------------------------

        /// <summary>markFound: true the viewer is in the view, false it is not, null the search itself failed.</summary>
        public static JObject Verdict(bool? markFound, IList<JObject> causes, string searchReason)
        {
            string[] yes = causes.Where(c => (string)c["blocks_mark"] == "yes").Select(c => (string)c["cause"]).ToArray();
            string[] unk = causes.Where(c => (string)c["blocks_mark"] == "unknown").Select(c => (string)c["cause"]).ToArray();
            string verdict;
            if (!markFound.HasValue) verdict = "unknown";
            else if (markFound.Value) verdict = yes.Length > 0 ? "measurement_conflict" : "visible";
            else if (yes.Length > 0) verdict = "not_visible_cause_measured";
            else if (unk.Length > 0) verdict = "not_visible_cause_not_ruled_out";
            else verdict = "not_visible_no_cause_found";
            var o = new JObject { ["verdict"] = verdict, ["blocking_causes"] = new JArray(yes), ["unknown_causes"] = new JArray(unk) };
            if (!markFound.HasValue) o["reason"] = searchReason ?? "the viewers of the view could not be read";
            if (verdict == "measurement_conflict")
                o["reason"] = "the mark IS in the view, yet these causes measured as hiding it: " + string.Join(", ", yes) + ". One of the two readings is wrong; do not act on either without checking.";
            return o;
        }
    }
}
