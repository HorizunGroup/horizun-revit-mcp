using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Core
{
    public static class DeliveryLayoutRules
    {
        internal const int MaxTagNativeProbes = 32;
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
            => Clear(candidate, obstacles, gap, bounds, null);

        internal static bool Clear(PlanBox candidate, IEnumerable<PlanBox> obstacles, double gap, PlanBox? bounds,
                                   AnnotationBudget budget)
        {
            if (!Finite(gap) || gap < 0 || obstacles == null || !candidate.Valid ||
                (bounds.HasValue && (!bounds.Value.Valid || !PlanimetryGeometry.Contains(bounds.Value,candidate,1e-7)))) return false;
            foreach (PlanBox b in obstacles)
            {
                budget?.Check();
                if (!b.Valid || PlanimetryGeometry.Overlaps(candidate, b, 1e-7) ||
                    (gap != 0 && PlanimetryGeometry.Separation(candidate, b) + 1e-7 < gap)) return false;
            }
            return true;
        }
        public static PlanBox Shift(PlanBox b, double x, double y) => PlanBox.FromCorners(b.MinX+x,b.MinY+y,b.MaxX+x,b.MaxY+y);

        internal sealed class TagFallback
        {
            private double penalty = double.PositiveInfinity;
            private PlanBox bestBox;
            public (double X, double Y)? Best { get; private set; }
            public bool RestoreBest { get; set; }
            public bool Used => RestoreBest && penalty > 0;

            public bool VerifyRestored((double X, double Y) offset, PlanBox box)
            {
                const double tolerance = 1e-7;
                return RestoreBest && Best.HasValue && box.Valid &&
                    Math.Abs(offset.X - Best.Value.X) <= tolerance && Math.Abs(offset.Y - Best.Value.Y) <= tolerance &&
                    Math.Abs(box.MinX - bestBox.MinX) <= tolerance && Math.Abs(box.MinY - bestBox.MinY) <= tolerance &&
                    Math.Abs(box.MaxX - bestBox.MaxX) <= tolerance && Math.Abs(box.MaxY - bestBox.MaxY) <= tolerance;
            }

            public bool Measure((double X, double Y) offset, PlanBox box, IReadOnlyList<PlanBox> obstacles,
                                double gap, double radius, PlanBox? bounds, AnnotationBudget budget)
            {
                double travel = offset.X * offset.X + offset.Y * offset.Y;
                if (travel > radius * radius + 1e-7 || !box.Valid || !Clear(box, Array.Empty<PlanBox>(), gap, bounds))
                    return false;
                double score = 0;
                foreach (PlanBox obstacle in obstacles)
                {
                    budget?.Check();
                    if (!obstacle.Valid) return false;
                    if (PlanimetryGeometry.Overlaps(box, obstacle, 1e-7))
                    {
                        double x = Math.Min(box.MaxX, obstacle.MaxX) - Math.Max(box.MinX, obstacle.MinX);
                        double y = Math.Min(box.MaxY, obstacle.MaxY) - Math.Max(box.MinY, obstacle.MinY);
                        score += gap + Math.Max(1e-7, Math.Min(x, y));
                    }
                    else score += Math.Max(0, gap - PlanimetryGeometry.Separation(box, obstacle) - 1e-7);
                }
                if (score < penalty || (Best.HasValue && score == penalty &&
                    travel < Best.Value.X * Best.Value.X + Best.Value.Y * Best.Value.Y))
                {
                    Best = offset;
                    bestBox = box;
                    penalty = score;
                }
                return score == 0;
            }
        }

        // Only a predicted box translates rigidly. A native tag with a leader can
        // change shape, so its measured box is checked against the full survey.
        internal static (double X, double Y)? FindTagOffset(PlanBox currentBox, IReadOnlyList<PlanBox> obstacles,
            double gap, double radius, PlanBox? bounds, AnnotationBudget budget, Func<double, double, bool> probe,
            (double X, double Y)? currentHeadFromSeed = null, TagFallback fallback = null)
        {
            budget?.Check();
            // Candidate offsets are measured from the requested seed in view coordinates.
            // Rebase the prediction there before checking obstacles or generating crop offsets.
            var box = currentHeadFromSeed.HasValue
                ? Shift(currentBox, -currentHeadFromSeed.Value.X, -currentHeadFromSeed.Value.Y)
                : currentBox;
            int probes = 0;
            fallback?.Measure(currentHeadFromSeed ?? (0, 0), currentBox, obstacles, gap, radius, bounds, budget);
            if (Clear(box, obstacles, gap, bounds, budget))
            {
                if (!currentHeadFromSeed.HasValue || Probe(0, 0)) return (0, 0);
            }

            var nearby = new List<PlanBox>();
            double reach = radius + gap + 1e-7;
            foreach (PlanBox obstacle in obstacles)
            {
                budget?.Check();
                if (!obstacle.Valid ||
                    (obstacle.MaxX >= box.MinX - reach && obstacle.MinX <= box.MaxX + reach &&
                     obstacle.MaxY >= box.MinY - reach && obstacle.MinY <= box.MaxY + reach))
                    nearby.Add(obstacle);
            }
            var tested = new HashSet<(double, double)> { (0, 0) };
            using (var local = TagOffsets(box, gap, radius, bounds).GetEnumerator())
            {
                while (probes < 8 && local.MoveNext())
                    if (TryOffset(local.Current)) return local.Current;

                // A leader can change the native outline when the head moves.
                // Try other distances even when fewer than eight local offsets fit.
                foreach (double fraction in new[] { 0.5, 0.875 })
                {
                    for (int direction = 0; direction < 8; direction++)
                    {
                        double angle = direction * Math.PI / 4;
                        var far = (radius * fraction * Math.Cos(angle), radius * fraction * Math.Sin(angle));
                        if (probes < MaxTagNativeProbes && TryOffset(far, measureEvenIfCrowded: fallback != null)) return far;
                    }
                }

                while (probes < MaxTagNativeProbes && local.MoveNext())
                    if (TryOffset(local.Current, measureEvenIfCrowded: fallback != null && !fallback.Best.HasValue))
                        return local.Current;
            }
            if (fallback == null || !fallback.Best.HasValue) return null;
            fallback.RestoreBest = true;
            return fallback.Best;

            bool TryOffset((double X, double Y) offset, bool measureEvenIfCrowded = false)
            {
                budget?.Check();
                if (!tested.Add(offset)) return false;
                PlanBox predicted = Shift(box, offset.X, offset.Y);
                return Clear(predicted, measureEvenIfCrowded ? Array.Empty<PlanBox>() : (IEnumerable<PlanBox>)nearby,
                    gap, bounds, budget) && Probe(offset.X, offset.Y);
            }

            bool Probe(double x, double y)
            {
                probes++;
                bool accepted = probe(x, y);
                budget?.Check();
                return accepted;
            }
        }

        /// <summary>Try the full approved radius even when the normal lattice is too coarse or ends early.</summary>
        public static IEnumerable<(double X, double Y)> TagOffsets(PlanBox box, double gap, double radius, PlanBox? bounds)
        {
            double step = Math.Max(Math.Max(gap, Math.Max(box.Width, box.Height) * .25), 1.0 / 304.8);
            const int rings = 40;
            double limit = radius + 1e-7;
            var seen = new HashSet<(double, double)>();
            for (int ring = 0; ring <= rings && ring * step <= limit; ring++)
                foreach (var (x, y) in Ring(ring))
                    if ((x * (double)x + y * (double)y) * step * step <= limit * limit)
                    {
                        var offset = (x * step, y * step);
                        seen.Add(offset);
                        yield return offset;
                    }

            if (radius <= 0) yield break;
            // A tag seeded outside the crop can have a narrow reachable band in the crop.
            // Project each fallback point into the range of translations that fit the crop.
            double minX = bounds.HasValue ? bounds.Value.MinX - box.MinX : -radius;
            double maxX = bounds.HasValue ? bounds.Value.MaxX - box.MaxX : radius;
            double minY = bounds.HasValue ? bounds.Value.MinY - box.MinY : -radius;
            double maxY = bounds.HasValue ? bounds.Value.MaxY - box.MaxY : radius;
            if (minX > maxX || minY > maxY) yield break;
            double increment = radius / rings;
            for (int ring = 0; ring <= rings; ring++)
                foreach (var (x, y) in Ring(ring))
                {
                    double dx = Math.Max(minX, Math.Min(maxX, x * increment));
                    double dy = Math.Max(minY, Math.Min(maxY, y * increment));
                    if (dx * dx + dy * dy <= limit * limit && seen.Add((dx, dy)))
                        yield return (dx, dy);
                }
        }

        private static IEnumerable<(int X, int Y)> Ring(int ring)
        {
            for (int x = -ring; x <= ring; x++) yield return (x, -ring);
            if (ring == 0) yield break;
            for (int y = 1 - ring; y < ring; y++)
            {
                yield return (-ring, y);
                yield return (ring, y);
            }
            for (int x = -ring; x <= ring; x++) yield return (x, ring);
        }
    }
}
