// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_mep_routing: unit conversion, the rule-list edit
// that set_rules must reproduce exactly, catalog membership with a stated
// tolerance, and the velocity sizing that size_by_flow proposes.
//
// Why it lives apart. Two claims of that command are arithmetic, not Revit:
//
//   * set_rules verifies by comparing the rule list RE-READ after the commit with
//     the list it EXPECTED. The expectation is computed here, from the list read
//     before and the ordered edits, so an off-by-one in "move 3 to 0" is caught by
//     a unit test instead of by a model that quietly routes with the wrong elbow.
//
//   * size_by_flow picks the smallest catalog size whose free area carries the
//     flow at or below a velocity limit. That is deterministic given the catalog,
//     and the only method it claims: no friction, no pressure drop, no fluid
//     properties. Revit's own duct/pipe sizing dialog has no public API.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class MepRoutingRules
    {
        /// <summary>Catalog sizes are compared at this tolerance, in feet (about 0.003 mm).</summary>
        public const double SizeToleranceFeet = 1e-5;

        public const double FeetPerMm = 1.0 / 304.8;
        public const double CubicFeetPerSecondPerLitrePerSecond = 0.0353146667214886;
        public const double FeetPerSecondPerMetrePerSecond = 1.0 / 0.3048;

        /// <summary>Length unit to feet. mm, in and feet; anything else is refused by the caller.</summary>
        public static bool TryUnitScale(string units, out double toFeet)
        {
            switch ((units ?? "mm").Trim().ToLowerInvariant())
            {
                case "mm": toFeet = FeetPerMm; return true;
                case "in": toFeet = 1.0 / 12.0; return true;
                case "feet": toFeet = 1.0; return true;
                default: toFeet = double.NaN; return false;
            }
        }

        /// <summary>True when some catalog value equals the wanted one within SizeToleranceFeet.</summary>
        public static bool CatalogHas(IEnumerable<double> catalogFeet, double wantedFeet)
            => catalogFeet != null && catalogFeet.Any(v => Math.Abs(v - wantedFeet) <= SizeToleranceFeet);

        // ---- a rule's size range --------------------------------------------------------

        /// <summary>
        /// The size range an added rule is written with. Omitting BOTH bounds is the
        /// documented form of "all sizes": the command then writes PrimarySizeCriterion.All()
        /// itself. Its bounds are Revit's own representation of an open range and are never
        /// validated as if the caller had typed them - doing that refused every rule sent
        /// without min_size/max_size (live, Revit 2026, 2026-09-24). One bound alone reads
        /// two ways (open or closed on the other side) and is refused rather than guessed.
        /// </summary>
        public static bool ResolveSizeRange(double? min, double? max, out bool allSizes, out string error)
        {
            error = null;
            allSizes = !min.HasValue && !max.HasValue;
            if (allSizes) return true;
            if (!min.HasValue || !max.HasValue)
            { error = "give both min_size and max_size, or neither for all sizes."; return false; }
            double a = min.Value, b = max.Value;
            if (double.IsNaN(a) || double.IsInfinity(a) || double.IsNaN(b) || double.IsInfinity(b))
            { error = "min_size and max_size must be finite numbers."; return false; }
            if (a < 0 || b < a) { error = "min_size must be >= 0 and <= max_size (omit both for all sizes)."; return false; }
            return true;
        }

        // ---- rule-list edits ---------------------------------------------------------

        public sealed class RuleEdit
        {
            public string Action;   // add | remove | move
            public int? Index;      // add: insert position (null = end); remove/move: the rule
            public int? ToIndex;    // move: final position
            public string Added;    // add: the signature of the rule being added
        }

        /// <summary>
        /// Apply ordered edits to a copy of one group's rule list and return the list Revit
        /// must hold afterwards. Returns null with a reason when an edit cannot be applied:
        /// an index outside the list AT THAT POINT of the sequence, a move without its
        /// destination, an add with nothing to add. Edits are applied in order, so an index
        /// always refers to the list as the previous edits left it - the same order the
        /// command applies them to Revit.
        /// </summary>
        public static List<string> Simulate(IList<string> before, IList<RuleEdit> edits, out string error)
        {
            error = null;
            var list = new List<string>(before ?? new string[0]);
            for (int i = 0; i < (edits?.Count ?? 0); i++)
            {
                RuleEdit e = edits[i];
                string where = "edit " + i + " (" + (e?.Action ?? "null") + ")";
                if (e == null) { error = where + " is empty."; return null; }
                switch (e.Action)
                {
                    case "add":
                        if (string.IsNullOrEmpty(e.Added)) { error = where + " names no rule to add."; return null; }
                        int at = e.Index ?? list.Count;
                        if (at < 0 || at > list.Count) { error = where + ": index " + at + " is outside 0.." + list.Count + "."; return null; }
                        list.Insert(at, e.Added);
                        break;
                    case "remove":
                        if (e.Index == null || e.Index < 0 || e.Index >= list.Count)
                        { error = where + ": index " + (e.Index?.ToString() ?? "missing") + " names no rule; the group holds " + list.Count + " at this point."; return null; }
                        list.RemoveAt(e.Index.Value);
                        break;
                    case "move":
                        if (e.Index == null || e.Index < 0 || e.Index >= list.Count)
                        { error = where + ": index " + (e.Index?.ToString() ?? "missing") + " names no rule; the group holds " + list.Count + " at this point."; return null; }
                        if (e.ToIndex == null || e.ToIndex < 0 || e.ToIndex >= list.Count)
                        { error = where + ": to_index " + (e.ToIndex?.ToString() ?? "missing") + " is outside 0.." + (list.Count - 1) + "."; return null; }
                        if (e.ToIndex == e.Index) { error = where + " moves rule " + e.Index + " onto itself, which changes nothing."; return null; }
                        string moved = list[e.Index.Value];
                        list.RemoveAt(e.Index.Value);
                        list.Insert(e.ToIndex.Value, moved);
                        break;
                    default:
                        error = where + ": action must be add, remove or move."; return null;
                }
            }
            return list;
        }

        // ---- velocity sizing ---------------------------------------------------------

        public sealed class SizeOption
        {
            /// <summary>Catalog nominal size, feet: what resize writes.</summary>
            public double Nominal;
            /// <summary>Free bore used for the area, feet. The inner diameter for a pipe.</summary>
            public double Bore;
        }

        public sealed class SizingResult
        {
            public bool Found;
            public double Nominal;
            public double AreaSquareFeet;
            public double VelocityFeetPerSecond;
            public double RequiredAreaSquareFeet;
            public string Reason;
        }

        /// <summary>
        /// The smallest round size whose free area carries flow at or below the velocity limit.
        /// Sizes with a non-positive bore are ignored. Not found when even the largest is too
        /// small - then the reply names the largest instead of silently capping.
        /// </summary>
        public static SizingResult SmallestRound(double flowCfs, double maxVelocityFps, IEnumerable<SizeOption> catalog)
        {
            var r = Precheck(flowCfs, maxVelocityFps);
            if (r.Reason != null) return r;
            foreach (SizeOption o in (catalog ?? Enumerable.Empty<SizeOption>()).Where(o => o.Bore > 0).OrderBy(o => o.Bore))
            {
                double area = Math.PI * o.Bore * o.Bore / 4.0;
                if (area + 1e-12 >= r.RequiredAreaSquareFeet)
                    return Done(r, o.Nominal, area, flowCfs);
            }
            r.Reason = "no catalog size is large enough: the flow needs " + r.RequiredAreaSquareFeet.ToString("G6") +
                       " sq ft of free area at the velocity limit.";
            return r;
        }

        /// <summary>
        /// The narrowest catalog width that, at the fixed height, carries flow at or below the
        /// velocity limit. The height is held because a rectangular section has two free
        /// dimensions and picking both would be a design choice, not arithmetic.
        /// </summary>
        public static SizingResult NarrowestRectangle(double flowCfs, double maxVelocityFps, double heightFeet,
                                                      IEnumerable<double> widthsFeet)
        {
            var r = Precheck(flowCfs, maxVelocityFps);
            if (r.Reason != null) return r;
            if (!(heightFeet > 0) || double.IsInfinity(heightFeet)) { r.Reason = "the height to hold is not a positive size."; return r; }
            foreach (double w in (widthsFeet ?? Enumerable.Empty<double>()).Where(w => w > 0).OrderBy(w => w))
            {
                double area = w * heightFeet;
                if (area + 1e-12 >= r.RequiredAreaSquareFeet) return Done(r, w, area, flowCfs);
            }
            r.Reason = "no catalog width is large enough at this height: the flow needs " +
                       r.RequiredAreaSquareFeet.ToString("G6") + " sq ft of free area at the velocity limit.";
            return r;
        }

        private static SizingResult Precheck(double flowCfs, double maxVelocityFps)
        {
            var r = new SizingResult();
            if (double.IsNaN(flowCfs) || double.IsInfinity(flowCfs) || flowCfs <= 0)
            { r.Reason = "no positive flow: the element carries none, or its system has not been calculated."; return r; }
            if (double.IsNaN(maxVelocityFps) || double.IsInfinity(maxVelocityFps) || maxVelocityFps <= 0)
            { r.Reason = "max_velocity must be a positive number."; return r; }
            r.RequiredAreaSquareFeet = flowCfs / maxVelocityFps;
            return r;
        }

        private static SizingResult Done(SizingResult r, double nominal, double area, double flowCfs)
        {
            r.Found = true; r.Nominal = nominal; r.AreaSquareFeet = area;
            r.VelocityFeetPerSecond = flowCfs / area;
            return r;
        }
    }
}
