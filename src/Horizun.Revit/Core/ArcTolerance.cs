// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// HOW CLOSE IS "THE ARC THAT WAS DECLARED". A built arc is compared with the
// declared one by its centre and its radius. A fixed 1 mm refused curved scan
// walls of large radius: measured 5.0.4, a 21.7 m arc came back 1.055 mm off in
// both, which is Revit's own rounding at that size, not a different wall. The
// bar grows with the radius (0.01 %) but never below 1 mm and never above 5 mm,
// so a real error is still refused.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ArcTolerance
    {
        public const double MinimumMm = 1.0;
        public const double MaximumMm = 5.0;
        public const double RelativeToRadius = 0.0001;   // 0.01 %

        /// <summary>The tolerance, in mm, for the centre and the radius of an arc of this radius (mm).</summary>
        public static double Mm(double radiusMm)
        {
            if (double.IsNaN(radiusMm) || double.IsInfinity(radiusMm) || radiusMm <= 0) return MinimumMm;
            return Math.Min(MaximumMm, Math.Max(MinimumMm, RelativeToRadius * radiusMm));
        }

        public const string Rule = "max(1 mm, 0.01 % of the radius), capped at 5 mm";

        /// <summary>
        /// HOW FAR THE BUILT CURVE LIES FROM THE DECLARED CIRCLE, in mm: the largest |distance to the declared centre -
        /// declared radius| over points sampled on the built curve (plan coordinates, mm). This is the shape check.
        /// The centre and the radius of a shallow arc are ill-conditioned: Revit builds the arc through start, end and a
        /// third point, start and end may sit up to 1 mm off the declared circle (the plan accepts that), and on a 18 m
        /// arc 1 mm at an end moves the centre 2 mm while the wall itself stays within 1 mm of what was declared.
        /// Measured 14-nov: 34 of 102 curved scan walls of the Teatro were refused that way.
        /// </summary>
        public static double ShapeOffMm(double centreX, double centreY, double radius, IEnumerable<double[]> builtPoints)
        {
            double worst = 0; bool any = false;
            foreach (var p in builtPoints ?? Enumerable.Empty<double[]>())
            {
                if (p == null || p.Length < 2) continue;
                double d = Math.Abs(Math.Sqrt((p[0] - centreX) * (p[0] - centreX) + (p[1] - centreY) * (p[1] - centreY)) - radius);
                if (double.IsNaN(d)) return double.NaN;
                worst = Math.Max(worst, d); any = true;
            }
            return any ? worst : double.NaN;
        }

        /// <summary>
        /// One line per stage instead of every row: how many arcs were checked, the largest tolerance applied and how
        /// many arcs passed while more than 1 mm off (the ones the fixed bar used to refuse). Null when no row had an arc.
        /// </summary>
        public static JObject Summarize(IEnumerable<JArray> createdRows)
        {
            var curves = (createdRows ?? Enumerable.Empty<JArray>()).Where(a => a != null).SelectMany(a => a.OfType<JObject>())
                .Select(r => r["curve_verified"] as JObject).Where(c => c != null).ToList();
            if (curves.Count == 0) return null;
            double Off(JObject c) => c["shape_off_mm"] != null && c["shape_off_mm"].Type != JTokenType.Null
                ? Math.Max(c.Value<double>("shape_off_mm"), c.Value<double?>("third_off_mm") ?? 0)
                : Math.Max(c.Value<double?>("centre_off_mm") ?? 0, c.Value<double?>("radius_off_mm") ?? 0);
            var tolerances = curves.Select(c => c.Value<double?>("tolerance_mm")).Where(t => t.HasValue).Select(t => t.Value).ToList();
            return new JObject
            {
                ["arcs"] = curves.Count,
                ["verified"] = curves.Count(c => c.Value<bool?>("verified") == true),
                ["max_tolerance_mm"] = tolerances.Count > 0 ? (JToken)Math.Round(tolerances.Max(), 3) : JValue.CreateNull(),
                ["passed_above_1mm"] = curves.Count(c => c.Value<bool?>("verified") == true && Off(c) > MinimumMm),
                ["rule"] = Rule,
            };
        }
    }
}
