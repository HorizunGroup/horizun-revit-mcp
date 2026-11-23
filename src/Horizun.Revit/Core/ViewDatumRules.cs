// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// horizun_view_visibility datum_cleanup / set_scale (4.2-N3, CT-P1-3b, CT-P1-3d):
// the decisions that need no Revit, proved without a model.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ViewDatumRules
    {
        public const string KindGrid = "grid";
        public const string KindLevel = "level";
        public const string CriterionOutsideCrop = "outside_crop";
        public const string CriterionForeign = "foreign";

        public static readonly string[] Kinds = { KindGrid, KindLevel };
        public static readonly string[] Criteria = { CriterionOutsideCrop, CriterionForeign };

        /// <summary>The datum kinds asked for (default both), or null with the refusal in <paramref name="error"/>.</summary>
        public static List<string> ParseKinds(JToken token, out string error)
        {
            error = null;
            if (token == null || token.Type == JTokenType.Null) return new List<string>(Kinds);
            return ParseSet(token, Kinds, "datum_kinds", out error);
        }

        /// <summary>The criteria asked for (default outside_crop), or null with the refusal.</summary>
        public static List<string> ParseCriteria(JToken token, out string error)
        {
            error = null;
            if (token == null || token.Type == JTokenType.Null) return new List<string> { CriterionOutsideCrop };
            return ParseSet(token, Criteria, "criteria", out error);
        }

        private static List<string> ParseSet(JToken token, string[] known, string field, out string error)
        {
            error = null;
            var arr = token as JArray;
            if (arr == null || arr.Count == 0 || arr.Any(t => t.Type != JTokenType.String))
            {
                error = field + " must be a non-empty array of strings from: " + string.Join(", ", known) + ".";
                return null;
            }
            var result = new List<string>();
            foreach (string s in arr.Select(t => ((string)t).Trim().ToLowerInvariant()))
            {
                if (!known.Contains(s)) { error = field + " '" + s + "' is not one of: " + string.Join(", ", known) + "."; return null; }
                if (!result.Contains(s)) result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// Liang-Barsky: does the segment (ax,ay)-(bx,by) touch the axis-aligned rectangle? Touching the
        /// border counts as inside (a datum that ends exactly on the crop edge is still drawn).
        /// </summary>
        public static bool SegmentTouchesRect(double ax, double ay, double bx, double by,
                                              double minX, double minY, double maxX, double maxY)
        {
            double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { ax - minX, maxX - ax, ay - minY, maxY - ay };
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(p[i]) < 1e-12)
                {
                    if (q[i] < 0) return false;
                    continue;
                }
                double t = q[i] / p[i];
                if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
            return true;
        }

        /// <summary>One datum's verdict. <c>Hide</c> true means the datum is hidden; otherwise <c>Reason</c> says why it stays.</summary>
        public sealed class Verdict
        {
            public bool Hide;
            public string Reason;
            public List<string> Because = new List<string>();
        }

        /// <summary>
        /// A datum is hidden when it meets ANY requested criterion and nothing protects it. Protected
        /// (kept) always wins: an id in keep_ids, a datum the view cannot hide, a datum whose extent in
        /// this view could not be read when outside_crop is the only reason.
        /// </summary>
        public static Verdict Decide(IList<string> criteria, bool inKeepList, bool canHide, bool own,
                                     bool? touchesCrop)
        {
            var v = new Verdict();
            if (inKeepList) { v.Reason = "in keep_ids"; return v; }
            if (criteria.Contains(CriterionForeign) && !own) v.Because.Add(CriterionForeign);
            if (criteria.Contains(CriterionOutsideCrop) && touchesCrop == false) v.Because.Add(CriterionOutsideCrop);
            if (v.Because.Count == 0)
            {
                v.Reason = criteria.Contains(CriterionOutsideCrop) && touchesCrop == null && !(criteria.Contains(CriterionForeign) && !own)
                    ? "extent in this view could not be read (no curve to test against the crop)"
                    : "meets no requested criterion";
                return v;
            }
            if (!canHide) { v.Reason = "cannot be hidden in this view"; v.Because.Clear(); return v; }
            v.Hide = true;
            return v;
        }

        /// <summary>set_scale: the scale must be a positive denominator Revit can hold.</summary>
        public static string ScaleError(long? scale)
        {
            if (!scale.HasValue) return "set_scale needs scale, the drawing scale's denominator (50 for 1:50).";
            if (scale.Value < 1 || scale.Value > 24000) return "scale must be an integer 1..24000.";
            return null;
        }
    }
}
