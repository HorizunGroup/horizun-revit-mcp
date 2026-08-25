// -----------------------------------------------------------------------------
// Horizun Revit MCP - deterministic sheet packing with no Revit in the room.
//
// Packing is a design decision only within the rectangle and ordering the caller
// supplied. The algorithm never invents a paper size, margin, gap or priority:
// it fills from the upper-left, preserves item order, treats existing placements
// as obstacles, and refuses the WHOLE plan when one item cannot fit. A partial
// arrangement is worse than no arrangement because it looks approved until the
// missing view is noticed on paper.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PackingItem
    {
        public string Key;
        public double Width;
        public double Height;
    }

    public sealed class PackingPlacement
    {
        public string Key;
        public PlanBox Box;
        public double CenterX => Box.CenterX;
        public double CenterY => Box.CenterY;
    }

    public sealed class PackingResult
    {
        public bool Ok;
        public string Error;
        public PlanBox Usable;
        public List<PackingPlacement> Placements = new List<PackingPlacement>();
    }

    public static class PlanimetryPackingRules
    {
        public static PackingResult Pack(PlanBox sheet, IEnumerable<PlanBox> fixedObstacles,
                                         IEnumerable<PackingItem> requested,
                                         double marginFeet, double gapFeet,
                                         double toleranceFeet)
        {
            var result = new PackingResult();
            if (!sheet.Valid) return Refuse(result, "sheet extent is unreadable");
            if (!FiniteNonNegative(marginFeet)) return Refuse(result, "margin must be finite and non-negative");
            if (!FiniteNonNegative(gapFeet)) return Refuse(result, "gap must be finite and non-negative");
            if (!FiniteNonNegative(toleranceFeet)) return Refuse(result, "tolerance must be finite and non-negative");

            result.Usable = PlanimetryGeometry.Expand(sheet, -marginFeet);
            if (!result.Usable.Valid || result.Usable.Width <= toleranceFeet ||
                result.Usable.Height <= toleranceFeet)
                return Refuse(result, "the margins leave no usable sheet area");

            List<PlanBox> obstacles = (fixedObstacles ?? Enumerable.Empty<PlanBox>()).ToList();
            if (obstacles.Any(b => !b.Valid))
                return Refuse(result, "a fixed placement extent is unreadable; packing around an unknown box would guess");

            List<PackingItem> items = (requested ?? Enumerable.Empty<PackingItem>()).ToList();
            if (items.Count == 0) return Refuse(result, "items must contain at least one placement");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (PackingItem item in items)
            {
                if (item == null) return Refuse(result, "an item is null");
                if (string.IsNullOrWhiteSpace(item.Key)) return Refuse(result, "every item needs a non-empty key");
                if (!keys.Add(item.Key)) return Refuse(result, "item key '" + item.Key + "' is duplicated");
                if (!FinitePositive(item.Width) || !FinitePositive(item.Height))
                    return Refuse(result, "item '" + item.Key + "' has an unreadable or non-positive size");
                if (item.Width > result.Usable.Width + toleranceFeet ||
                    item.Height > result.Usable.Height + toleranceFeet)
                    return Refuse(result, "item '" + item.Key + "' is larger than the usable sheet area");
            }

            var occupied = new List<PlanBox>(obstacles);
            foreach (PackingItem item in items)
            {
                List<double> xs = new[] { result.Usable.MinX }
                    .Concat(occupied.Select(b => b.MaxX + gapFeet))
                    .Distinct().OrderBy(x => x).ToList();
                List<double> tops = new[] { result.Usable.MaxY }
                    .Concat(occupied.Select(b => b.MinY - gapFeet))
                    .Distinct().OrderByDescending(y => y).ToList();

                PlanBox chosen = PlanBox.Unreadable;
                foreach (double top in tops)
                {
                    foreach (double left in xs)
                    {
                        PlanBox candidate = PlanBox.FromCorners(left, top - item.Height,
                                                                left + item.Width, top);
                        if (!PlanimetryGeometry.Contains(result.Usable, candidate, toleranceFeet)) continue;
                        if (occupied.Any(b => Conflicts(candidate, b, gapFeet, toleranceFeet))) continue;
                        chosen = candidate;
                        break;
                    }
                    if (chosen.Valid) break;
                }

                if (!chosen.Valid)
                {
                    result.Placements.Clear();
                    return Refuse(result, "item '" + item.Key + "' cannot fit with the requested margin, gap and fixed placements");
                }
                occupied.Add(chosen);
                result.Placements.Add(new PackingPlacement { Key = item.Key, Box = chosen });
            }

            result.Ok = true;
            return result;
        }

        private static bool Conflicts(PlanBox a, PlanBox b, double gap, double tolerance)
        {
            if (PlanimetryGeometry.Overlaps(a, b, tolerance)) return true;
            if (gap <= tolerance) return false;
            double separation = PlanimetryGeometry.Separation(a, b);
            return double.IsNaN(separation) || separation + tolerance < gap;
        }

        private static bool FinitePositive(double v)
            => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0.0;

        private static bool FiniteNonNegative(double v)
            => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0.0;

        private static PackingResult Refuse(PackingResult result, string error)
        {
            result.Ok = false;
            result.Error = error;
            return result;
        }
    }
}
