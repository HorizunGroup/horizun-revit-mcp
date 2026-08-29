using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// WHERE REVIT ACTUALLY PUTS THE BARS OF A SET, given the array length it was
    /// asked for - and why this class MEASURES that rather than predicting it.
    ///
    /// MEASURED IN REVIT 2026, four times, on a closed stirrup with an explicit
    /// array_length_mm of 5949.2 mm:
    ///
    ///     nominal 12, model 12  ->  Revit reports 5937.2   short by 12.00
    ///     nominal 20, model 20  ->  Revit reports 5929.2   short by 20.00
    ///     nominal 32, model 32  ->  Revit reports 5917.2   short by 32.00
    ///     nominal 12, model 25  ->  Revit reports 5924.2   short by 25.00
    ///
    /// Exactly one MODEL bar diameter, every time, and the fourth row settles that
    /// it is the model diameter rather than the nominal one. That looked like a
    /// rule, and this class first implemented it as one: expect declared minus a
    /// model diameter.
    ///
    /// THE MULTIVERSION MATRIX KILLED THAT RULE THE SAME NIGHT. Seven cases on
    /// Revit 2023, same wall, same 12 mm bar, same declared array length, varying
    /// one thing at a time:
    ///
    ///     closed profile, stirrup_tie, max spacing 400   ->  short by 12
    ///     open bar,       standard,    max spacing 400   ->  short by 12
    ///     open bar,       stirrup_tie, max spacing 400   ->  short by  6
    ///     closed profile, standard,    max spacing 400   ->  short by 12
    ///     open bar,       standard,    max spacing 300   ->  short by 12
    ///     open bar,       standard,    max spacing 200   ->  short by 12
    ///     open bar,       standard,    max spacing 500   ->  short by  6
    ///
    /// and a straight slab bar in the reinforcement harness, on the same Revit,
    /// short by ZERO. Neither style, nor closedness, nor spacing explains which
    /// rows give a full diameter, which give half, and which give none. Whatever
    /// Revit is doing, five hours of measurement did not establish it.
    ///
    /// SO THIS CLASS NO LONGER PREDICTS IT. Two things are true of every case
    /// measured, on both Revit 2023 and Revit 2026, and they are enough:
    ///
    ///   1. Revit REPORTS the span it used, through ArrayLength. Reading that and
    ///      distributing the predicted positions across it tests what the check is
    ///      actually for - that the bars are evenly spaced over the array Revit
    ///      built - and is immune to a mechanism nobody has pinned down.
    ///
    ///   2. The shortfall is never negative and never more than one MODEL bar
    ///      diameter. That is a BOUND rather than a formula, it held across seven
    ///      deliberate cases and two Revit years, and it still catches the thing
    ///      the check exists to catch: an array built over the wrong span.
    ///
    /// The alternative was to keep a rule that was right on one bar and wrong on
    /// the next, which had already turned a passing probe into a failure by
    /// exactly 12 mm. A bound that is true is worth more than a formula that is
    /// nearly true, and this one is still narrow enough to fail a real defect.
    /// </summary>
    public static class RebarArrayGeometry
    {
        public const string WhyMeasuredNotPredicted =
            "the span Revit REPORTS for this set, not the one that was declared. Revit lays a set out over " +
            "somewhere between the declared length and one model bar diameter less than it - measured across " +
            "seven deliberate cases on Revit 2023 and four on Revit 2026, with no rule found that predicts " +
            "which. So the positions are compared across the span the model itself reports, and the span " +
            "itself is held to that measured bound rather than to a formula.";

        public const string WhyNoDiameter =
            "the bar type would not report a model diameter, and the bound on the array length is one model " +
            "diameter. Without it there is nothing to bound the difference with - and unknown is not a pass.";

        public const string WhyRevitWouldNotSay =
            "Revit would not report the array length, so there is no span to distribute the predicted " +
            "positions across and nothing to compare the declaration with.";

        /// <summary>
        /// Is the span Revit reports acceptable for the span that was declared?
        ///
        /// True when the model is between one model bar diameter short and exactly
        /// the declared length. A model LONGER than declared is refused outright:
        /// nothing measured has ever produced one, so it is not a case this
        /// understands, and passing it would be passing an unknown.
        /// </summary>
        public static bool SpanIsWithinBound(double declaredMm, double revitReportedMm,
                                             double modelDiameterMm, double toleranceMm,
                                             out double shortfallMm, out string why)
        {
            shortfallMm = double.NaN;
            why = null;

            if (!IsFinite(declaredMm) || declaredMm <= 0)
            {
                why = "the declared array length is not a positive number.";
                return false;
            }
            if (!IsFinite(revitReportedMm))
            {
                why = WhyRevitWouldNotSay;
                return false;
            }
            if (!IsFinite(modelDiameterMm) || modelDiameterMm <= 0)
            {
                why = WhyNoDiameter;
                return false;
            }

            shortfallMm = declaredMm - revitReportedMm;
            double tol = IsFinite(toleranceMm) && toleranceMm > 0 ? toleranceMm : 0;

            if (shortfallMm < -tol)
            {
                why = "the model reports an array LONGER than the one declared. Nothing measured has ever " +
                      "produced that, so it is not a case this understands, and an unknown is not a pass.";
                return false;
            }
            if (shortfallMm > modelDiameterMm + tol)
            {
                why = "the model's array is shorter than the declaration by more than one model bar diameter, " +
                      "which is the largest difference ever measured. Something moved the array rather than " +
                      "Revit fitting the bars into it.";
                return false;
            }

            why = WhyMeasuredNotPredicted;
            return true;
        }

        /// <summary>
        /// The declared positions moved onto the span Revit actually used. The
        /// FIRST position is the anchor and does not move - measured, not assumed:
        /// the worst difference over sixteen 12 mm bars was the full 12 mm rather
        /// than half of it, which is what tells first-anchored apart from centred.
        ///
        /// A list of fewer than two positions is returned unchanged: one bar has no
        /// array, and there is nothing to rescale.
        /// </summary>
        public static IList<double> Rescale(IList<double> declared, double declaredSpanMm, double revitSpanMm)
        {
            if (declared == null) return null;
            var outp = new List<double>(declared.Count);
            if (declared.Count < 2 || !IsFinite(declaredSpanMm) || declaredSpanMm <= 0 || !IsFinite(revitSpanMm))
            {
                outp.AddRange(declared);
                return outp;
            }

            double factor = revitSpanMm / declaredSpanMm;
            double anchor = declared[0];
            for (int i = 0; i < declared.Count; i++)
                outp.Add(anchor + (declared[i] - anchor) * factor);
            return outp;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
