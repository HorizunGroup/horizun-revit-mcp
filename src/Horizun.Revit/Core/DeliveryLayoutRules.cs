using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Core
{
    public static class DeliveryLayoutRules
    {
        /// <summary>
        /// A layout distance's default, in the caller's units. The defaults are MILLIMETRES
        /// (10 mm clearance, 15 mm offset and chain separation, 30 mm paper or 1200 mm model
        /// displacement); they used to be read in whatever units the call declared, so with
        /// units=m a dimension stood 15 METRES off its element (audit 2026-10-08, 6.17).
        /// <paramref name="toFeet"/> is feet per caller unit.
        /// </summary>
        public static double DefaultInUnits(string key, string distanceSpace, double toFeet)
        {
            double mm = key == "clearance" ? 10.0
                      : key == "max_displacement" ? (distanceSpace == "paper" ? 30.0 : 1200.0)
                      : 15.0;
            return mm / (toFeet * 304.8);
        }

        public static double Distance(double value, double unitToFeet, int scale, string space)
        {
            if (!Finite(value) || value < 0 || !Finite(unitToFeet) || unitToFeet <= 0 || scale < 1)
                throw new ArgumentException("Layout distances must be finite and nonnegative with a valid scale.");
            if (space != "model" && space != "paper") throw new ArgumentException("distance_space must be model or paper.");
            double converted = value * unitToFeet * (space == "paper" ? scale : 1);
            if (!Finite(converted)) throw new ArgumentException("Layout distance overflows the supported range.");
            return converted;
        }
        public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        public static PlanBox ReadBox(JToken token, double scale)
        {
            var a = token as JArray;
            if (a == null || a.Count != 4 || a.Any(t => t.Type != JTokenType.Float && t.Type != JTokenType.Integer))
                throw new ArgumentException("A layout rectangle must be [minX,minY,maxX,maxY].");
            double[] p = a.Values<double>().ToArray();
            if (!Finite(scale) || scale <= 0 || p.Any(x => !Finite(x) || !Finite(x*scale)) || p[2] <= p[0] || p[3] <= p[1])
                throw new ArgumentException("A layout rectangle must be finite, ordered and have positive area.");
            return PlanBox.FromCorners(p[0]*scale,p[1]*scale,p[2]*scale,p[3]*scale);
        }
        public static bool Clear(PlanBox candidate, IEnumerable<PlanBox> obstacles, double gap, PlanBox? bounds = null)
        {
            if (!Finite(gap) || gap < 0 || obstacles == null || !candidate.Valid ||
                (bounds.HasValue && (!bounds.Value.Valid || !PlanimetryGeometry.Contains(bounds.Value,candidate,1e-7)))) return false;
            return obstacles.All(b => b.Valid && !PlanimetryGeometry.Overlaps(candidate,b,1e-7) &&
                (gap == 0 || PlanimetryGeometry.Separation(candidate,b) + 1e-7 >= gap));
        }
        public static PlanBox Shift(PlanBox b, double x, double y) => PlanBox.FromCorners(b.MinX+x,b.MinY+y,b.MaxX+x,b.MaxY+y);
    }
}
