// -----------------------------------------------------------------------------
// Horizun Revit MCP - what a declared reinforcement LAYOUT actually produces.
// Original Horizun code. No Revit types: this is arithmetic, and arithmetic is
// provable at a desk.
//
// WHY THIS FILE EXISTS AT ALL.
//
// A rebar set is the one thing in this bridge where "it was created" is almost
// worthless as evidence. Revit will happily accept a layout, place the bars, and
// report a healthy element - with half the set standing outside the beam. The
// element exists, its host is right, its type is right, its shape is right, and
// the steel is in the air.
//
// So the plan has to know, BEFORE anything is written, where every bar position
// will land - not the first and the last, EVERY one - and the apply has to
// compare Revit's own answer against that. Which means the arithmetic below is
// load-bearing: if it is wrong, the plan and the verification agree with each
// other and both are wrong, which is the failure mode this whole repository is
// built to avoid.
//
// Everything here is in MILLIMETRES, because that is what a requirement set
// declares and what an engineer reads. The conversion to feet happens once, at
// the Revit boundary, and never in here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The five layouts Revit has. Not four, not six, and not free text.</summary>
    public static class RebarLayout
    {
        public const string Single = "single";
        public const string FixedNumber = "fixed_number";
        public const string NumberWithSpacing = "number_with_spacing";
        public const string MaximumSpacing = "maximum_spacing";
        public const string MinimumClearSpacing = "minimum_clear_spacing";

        public static readonly string[] All =
        {
            Single, FixedNumber, NumberWithSpacing, MaximumSpacing, MinimumClearSpacing
        };

        public static bool IsKnown(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i], s, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>What a caller declared. Nulls mean "not stated", which is not zero.</summary>
    public sealed class RebarLayoutRequest
    {
        public string Layout;
        /// <summary>Number of BAR POSITIONS in the array - not the number of bars.</summary>
        public int? Number;
        /// <summary>Centre-to-centre for number_with_spacing and maximum_spacing; CLEAR distance for minimum_clear_spacing.</summary>
        public double? SpacingMm;
        /// <summary>Distance from the first position to the last, along the distribution direction.</summary>
        public double? ArrayLengthMm;
        public bool IncludeFirstBar = true;
        public bool IncludeLastBar = true;
        /// <summary>Needed only by minimum_clear_spacing, where clear distance is measured between bar surfaces.</summary>
        public double? BarDiameterMm;
    }

    /// <summary>What that declaration produces, or why it produces nothing.</summary>
    public sealed class RebarLayoutPlan
    {
        public string Layout;
        public string Error;
        public string Code;

        /// <summary>Distance of each bar POSITION from the start of the array, in mm, ascending.</summary>
        public List<double> PositionsMm = new List<double>();
        /// <summary>Array positions Revit will report as NumberOfBarPositions.</summary>
        public int NumberOfBarPositions;
        /// <summary>Bars actually present - positions minus the suppressed first and last.</summary>
        public int Quantity;
        public double ArrayLengthMm;
        /// <summary>Centre-to-centre spacing that results. Null for a single bar.</summary>
        public double? ResultingSpacingMm;
        public bool IncludeFirstBar = true;
        public bool IncludeLastBar = true;

        public bool Ok { get { return Error == null; } }
    }

    public static class RebarLayoutRules
    {
        // Refusal codes. A closed set: every one of these is a thing a caller can
        // read, act on, and test for.
        public const string CodeUnknownLayout = "unknown_layout";
        public const string CodeMissingNumber = "number_required";
        public const string CodeMissingSpacing = "spacing_required";
        public const string CodeMissingArrayLength = "array_length_required";
        public const string CodeMissingDiameter = "bar_diameter_required";
        public const string CodeNumberTooSmall = "number_below_two";
        public const string CodeSpacingNotPositive = "spacing_not_positive";
        public const string CodeArrayLengthNotPositive = "array_length_not_positive";
        public const string CodeSpacingExceedsArray = "spacing_exceeds_array_length";
        public const string CodeNothingLeft = "every_bar_suppressed";
        public const string CodeStatedNotUsed = "stated_value_not_used_by_this_layout";
        public const string CodeNotFinite = "value_is_not_a_finite_number";
        public const string CodeTooManyBars = "too_many_bar_positions";

        /// <summary>
        /// The largest array this will resolve. Not a structural opinion - it is the
        /// point past which a "layout" is a mistake in a number rather than a set of
        /// bars, and where allocating one double per position stops being free.
        /// Measured: number = 500000000 asked for a four-gigabyte list and took
        /// OutOfMemoryException out through Load, which promises never to throw.
        /// </summary>
        public const int MaxBarPositions = 20000;

        /// <summary>Two lengths are the same length if they agree to a tenth of a millimetre.</summary>
        public const double LengthToleranceMm = 0.1;

        /// <summary>
        /// Resolve a declaration into every bar position it produces.
        /// The caller gets an answer or a reason, never a guess.
        /// </summary>
        public static RebarLayoutPlan Resolve(RebarLayoutRequest r)
        {
            var p = new RebarLayoutPlan();
            if (r == null) return Fail(p, CodeUnknownLayout, "no layout was declared.");
            p.Layout = r.Layout;
            p.IncludeFirstBar = r.IncludeFirstBar;
            p.IncludeLastBar = r.IncludeLastBar;

            // NON-FINITE NUMBERS PASS EVERY COMPARISON THAT LOOKS FOR A BAD ONE.
            // `if (x <= 0)` is FALSE for NaN, so an array length of NaN reached the
            // arithmetic, produced NaN spacing and NaN positions, and came back
            // Ok = true with a plan nobody could build. Infinity does the same.
            string bad = FirstNonFinite(r);
            if (bad != null)
                return Fail(p, CodeNotFinite,
                    bad + " is not a finite number. A comparison against NaN is false whichever way it is " +
                    "written, so a value like this passes every guard that looks for a bad one and produces " +
                    "a plan of NaN positions that reports success.");

            if (!RebarLayout.IsKnown(r.Layout))
                return Fail(p, CodeUnknownLayout,
                    "layout must be one of " + string.Join(", ", RebarLayout.All) +
                    " - got " + Show(r.Layout) + ". The vocabulary is closed on purpose: these are the five " +
                    "layouts Revit has, and a sixth word would have to be approximated by one of them.");

            // ---------------------------------------------------------- single
            if (r.Layout == RebarLayout.Single)
            {
                // A DECLARATION THAT WOULD BE IGNORED IS REFUSED, not tidied away.
                // Somebody who wrote spacing beside single meant something by it,
                // and building one bar and staying quiet answers a question they
                // did not ask.
                if (r.Number.HasValue && r.Number.Value != 1)
                    return Fail(p, CodeStatedNotUsed,
                        "layout single places exactly one bar, and number=" + r.Number.Value + " was declared beside it.");
                if (r.SpacingMm.HasValue)
                    return Fail(p, CodeStatedNotUsed, "layout single has no spacing; one was declared.");
                if (r.ArrayLengthMm.HasValue)
                    return Fail(p, CodeStatedNotUsed, "layout single has no array; an array length was declared.");
                // AND THE INCLUDE FLAGS, on the same principle as the three above.
                // These were silently discarded: a caller who wrote
                // include_first_bar: false beside single got one bar and a plan that
                // echoed include_first_bar: true back at them.
                if (!r.IncludeFirstBar || !r.IncludeLastBar)
                    return Fail(p, CodeStatedNotUsed,
                        "layout single places one bar, which is neither a first nor a last bar of an array; " +
                        "include_first_bar or include_last_bar was declared false beside it.");
                p.NumberOfBarPositions = 1;
                p.Quantity = 1;
                p.ArrayLengthMm = 0.0;
                p.ResultingSpacingMm = null;
                p.PositionsMm.Add(0.0);
                // include_first/last do not apply to a single bar, and saying they
                // do would make the reply disagree with what Revit reports.
                p.IncludeFirstBar = true;
                p.IncludeLastBar = true;
                return p;
            }

            int n;
            double array;
            double? spacing = null;

            switch (r.Layout)
            {
                // ------------------------------------------------- fixed number
                case RebarLayout.FixedNumber:
                    if (!r.Number.HasValue) return Fail(p, CodeMissingNumber, "layout fixed_number needs number.");
                    if (!r.ArrayLengthMm.HasValue) return Fail(p, CodeMissingArrayLength, "layout fixed_number needs array_length_mm.");
                    n = r.Number.Value;
                    array = r.ArrayLengthMm.Value;
                    if (n < 2) return Fail(p, CodeNumberTooSmall,
                        "number must be at least 2 for layout fixed_number; one bar is layout single.");
                    if (n > MaxBarPositions) return Fail(p, CodeTooManyBars, TooMany(n));
                    if (array <= 0) return Fail(p, CodeArrayLengthNotPositive, "array_length_mm must be greater than zero.");
                    spacing = array / (n - 1);
                    // A DECLARED SPACING THAT AGREES IS NOT A CONTRADICTION. This
                    // used to refuse outright, so 4 bars over 900 mm with a spacing
                    // of 300 - which is what the other two numbers mean - came back
                    // as a refusal. number_with_spacing already accepts the mirror
                    // case; the two now hold the same line: agreement passes,
                    // disagreement is named.
                    if (r.SpacingMm.HasValue &&
                        Math.Abs(r.SpacingMm.Value - spacing.Value) > LengthToleranceMm)
                        return Fail(p, CodeStatedNotUsed,
                            "layout fixed_number derives the spacing as array_length_mm / (number - 1) = " +
                            Mm(spacing.Value) + ", and spacing_mm=" + Mm(r.SpacingMm.Value) +
                            " was declared. They disagree; state one.");
                    break;

                // ------------------------------------------- number with spacing
                case RebarLayout.NumberWithSpacing:
                    if (!r.Number.HasValue) return Fail(p, CodeMissingNumber, "layout number_with_spacing needs number.");
                    if (!r.SpacingMm.HasValue) return Fail(p, CodeMissingSpacing, "layout number_with_spacing needs spacing_mm.");
                    n = r.Number.Value;
                    if (n < 2) return Fail(p, CodeNumberTooSmall,
                        "number must be at least 2 for layout number_with_spacing; one bar is layout single.");
                    if (n > MaxBarPositions) return Fail(p, CodeTooManyBars, TooMany(n));
                    if (r.SpacingMm.Value <= 0) return Fail(p, CodeSpacingNotPositive, "spacing_mm must be greater than zero.");
                    spacing = r.SpacingMm.Value;
                    array = spacing.Value * (n - 1);
                    // A DECLARED array length that contradicts the derived one is a
                    // disagreement, not a rounding detail.
                    if (r.ArrayLengthMm.HasValue &&
                        Math.Abs(r.ArrayLengthMm.Value - array) > LengthToleranceMm)
                        return Fail(p, CodeStatedNotUsed,
                            "layout number_with_spacing derives the array length as spacing x (number - 1) = " +
                            Mm(array) + ", and array_length_mm=" + Mm(r.ArrayLengthMm.Value) + " was declared. " +
                            "They disagree; state one.");
                    break;

                // ----------------------------------------------- maximum spacing
                case RebarLayout.MaximumSpacing:
                    if (!r.SpacingMm.HasValue) return Fail(p, CodeMissingSpacing, "layout maximum_spacing needs spacing_mm.");
                    if (!r.ArrayLengthMm.HasValue) return Fail(p, CodeMissingArrayLength, "layout maximum_spacing needs array_length_mm.");
                    if (r.SpacingMm.Value <= 0) return Fail(p, CodeSpacingNotPositive, "spacing_mm must be greater than zero.");
                    array = r.ArrayLengthMm.Value;
                    if (array <= 0) return Fail(p, CodeArrayLengthNotPositive, "array_length_mm must be greater than zero.");
                    // Maximum spacing means NO GAP MAY EXCEED IT, so the count rounds
                    // UP and the resulting spacing is smaller than or equal to what
                    // was asked for. Rounding to nearest would put bars further apart
                    // than the instruction allows.
                    {
                        // THE COUNT IS COMPUTED IN DOUBLE AND BOUNDED BEFORE IT IS AN
                        // int. A spacing of 1e-9 mm over a 4 m array asks for 4e12
                        // gaps; cast straight to int that is undefined, and on one of
                        // the two runtimes this repository targets it wrapped to a
                        // NEGATIVE gap count, was clamped to 1, and returned Ok with
                        // two bars 4 m apart under a declared maximum of a nanometre.
                        double exactGaps = Math.Ceiling(array / r.SpacingMm.Value - 1e-9);
                        if (exactGaps < 1) exactGaps = 1;
                        if (exactGaps > MaxBarPositions - 1) return Fail(p, CodeTooManyBars, TooMany(exactGaps + 1));
                        int gaps = (int)exactGaps;
                        n = gaps + 1;
                        spacing = array / gaps;
                    }
                    // A DECLARED NUMBER THAT AGREES with the one this computes is not
                    // a contradiction, on the same principle as fixed_number above.
                    if (r.Number.HasValue && r.Number.Value != n)
                        return Fail(p, CodeStatedNotUsed,
                            "layout maximum_spacing computes " + n + " bar positions from the array length and " +
                            "the maximum spacing, and number=" + r.Number.Value + " was declared. They disagree; " +
                            "state one.");
                    break;

                // ----------------------------------------- minimum clear spacing
                case RebarLayout.MinimumClearSpacing:
                    if (!r.SpacingMm.HasValue) return Fail(p, CodeMissingSpacing, "layout minimum_clear_spacing needs spacing_mm.");
                    if (!r.ArrayLengthMm.HasValue) return Fail(p, CodeMissingArrayLength, "layout minimum_clear_spacing needs array_length_mm.");

                    // CLEAR spacing is measured between bar SURFACES, so the bar
                    // diameter is part of the arithmetic. Without it the count is
                    // wrong by one bar per diameter of array length, and nothing in
                    // the reply would show it.
                    if (!r.BarDiameterMm.HasValue)
                        return Fail(p, CodeMissingDiameter,
                            "layout minimum_clear_spacing measures the gap between bar SURFACES, so it needs the bar " +
                            "diameter to reach a centre-to-centre distance. Resolve the bar type first.");
                    if (r.SpacingMm.Value <= 0) return Fail(p, CodeSpacingNotPositive, "spacing_mm must be greater than zero.");
                    if (r.BarDiameterMm.Value <= 0) return Fail(p, CodeSpacingNotPositive, "the bar diameter must be greater than zero.");
                    array = r.ArrayLengthMm.Value;
                    if (array <= 0) return Fail(p, CodeArrayLengthNotPositive, "array_length_mm must be greater than zero.");
                    {
                        double centre = r.SpacingMm.Value + r.BarDiameterMm.Value;
                        // A MINIMUM may not be violated, so the count rounds DOWN -
                        // the opposite of maximum_spacing, and the reason the two are
                        // separate arms rather than one with a sign.
                        double exactGaps = Math.Floor(array / centre + 1e-9);
                        if (exactGaps > MaxBarPositions - 1) return Fail(p, CodeTooManyBars, TooMany(exactGaps + 1));
                        int gaps = (int)exactGaps;
                        if (gaps < 1)
                            return Fail(p, CodeSpacingExceedsArray,
                                "a clear spacing of " + Mm(r.SpacingMm.Value) + " on a bar of " +
                                Mm(r.BarDiameterMm.Value) + " needs " + Mm(centre) +
                                " centre to centre, and the array is only " + Mm(array) +
                                " long. Two bars will not fit.");
                        n = gaps + 1;
                        spacing = array / gaps;
                    }
                    if (r.Number.HasValue && r.Number.Value != n)
                        return Fail(p, CodeStatedNotUsed,
                            "layout minimum_clear_spacing computes " + n + " bar positions from the array length, " +
                            "the clear distance and the bar diameter, and number=" + r.Number.Value +
                            " was declared. They disagree; state one.");
                    break;

                default:
                    return Fail(p, CodeUnknownLayout, "unhandled layout " + Show(r.Layout) + ".");
            }

            if (spacing.HasValue && spacing.Value > array + LengthToleranceMm)
                return Fail(p, CodeSpacingExceedsArray,
                    "spacing " + Mm(spacing.Value) + " is longer than the array " + Mm(array) + ".");

            p.NumberOfBarPositions = n;
            p.ArrayLengthMm = array;
            p.ResultingSpacingMm = spacing;

            // EVERY position, not the ends. This list is what the apply compares
            // against, and what proves a set sits inside its host.
            for (int i = 0; i < n; i++)
            {
                // Multiply rather than accumulate: adding a spacing n times drifts,
                // and the drift lands on the last bar - which is exactly the one a
                // fit check is about.
                double d = (n == 1) ? 0.0 : array * i / (n - 1);
                p.PositionsMm.Add(d);
            }

            int quantity = n;
            if (!r.IncludeFirstBar) quantity--;
            if (!r.IncludeLastBar) quantity--;
            if (quantity <= 0)
                return Fail(p, CodeNothingLeft,
                    "the layout produces " + n + " positions and both the first and the last were excluded, " +
                    "leaving " + quantity + " bars. Nothing would be built.");
            p.Quantity = quantity;
            return p;
        }

        /// <summary>
        /// Do these positions fit between two bounds, measured along the same axis
        /// and in the same units? Returns the offending positions, never a verdict
        /// with no evidence.
        /// </summary>
        public static List<int> PositionsOutside(IList<double> positionsMm, double startMm, double endMm,
                                                 double toleranceMm)
        {
            var bad = new List<int>();
            if (positionsMm == null) return bad;
            double lo = Math.Min(startMm, endMm) - toleranceMm;
            double hi = Math.Max(startMm, endMm) + toleranceMm;
            for (int i = 0; i < positionsMm.Count; i++)
                if (positionsMm[i] < lo || positionsMm[i] > hi) bad.Add(i);
            return bad;
        }

        private static RebarLayoutPlan Fail(RebarLayoutPlan p, string code, string message)
        {
            p.Code = code;
            p.Error = message;
            p.PositionsMm.Clear();
            p.NumberOfBarPositions = 0;
            p.Quantity = 0;
            return p;
        }

        private static string TooMany(double n)
        {
            return "this layout resolves to " + n.ToString("0", CultureInfo.InvariantCulture) +
                   " bar positions, and the limit is " + MaxBarPositions + ". A number this large is a mistake " +
                   "in one of the declared values rather than a set of bars.";
        }

        /// <summary>The first declared value that is NaN or infinite, named, or null when all are finite.</summary>
        private static string FirstNonFinite(RebarLayoutRequest r)
        {
            if (r.SpacingMm.HasValue && !IsFinite(r.SpacingMm.Value)) return "spacing_mm";
            if (r.ArrayLengthMm.HasValue && !IsFinite(r.ArrayLengthMm.Value)) return "array_length_mm";
            if (r.BarDiameterMm.HasValue && !IsFinite(r.BarDiameterMm.Value)) return "the bar diameter";
            return null;
        }

        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static string Mm(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture) + " mm";
        }

        private static string Show(string s)
        {
            return s == null ? "null" : "'" + s + "'";
        }
    }
}
