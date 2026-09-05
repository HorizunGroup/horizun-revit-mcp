// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A measurement, its absence, and its failure - kept apart.
//
// horizun_quantities produces the number somebody bills. It used to collapse
// three different situations into the same double:
//
//     Guard.ToM3(vParam ?? 0)
//
// "I could not read this" became 0.0, was added to a total, and the total was
// then reconciled against another total as though both covered the same
// elements. They did not: an element can have a Volume parameter and no readable
// solid, or the reverse, so the two sums were over DIFFERENT SETS and their
// agreement meant nothing. A takeoff that silently omits elements does not look
// wrong; it looks cheap.
//
// Worse, the pairwise check defaulted to `bool agree = true` and only flipped it
// when both values existed. An element that could not be compared at all counted
// as an element that agreed, and the headline then said "all N agree".
//
// Three states, never merged:
//
//   Measured       - we have a number. Zero is a legitimate number and stays.
//   NotApplicable  - this element has no such quantity (a tag has no volume).
//                    Contributes nothing and is not a defect.
//   Failed         - the read threw or came back unusable. Contributes nothing
//                    and IS a defect: the total is now incomplete and says so.
//
// A total is the sum over Measured only, published beside the count it covers.
// Two sources are compared only where BOTH measured the same element.
//
// Revit-free, because the arithmetic of not lying has to be provable without a
// building. Same reason Reconcile.cs is.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public enum MeasureState
    {
        /// <summary>A number was obtained. It may legitimately be zero.</summary>
        Measured,

        /// <summary>The quantity does not apply to this element. Not an error.</summary>
        NotApplicable,

        /// <summary>The read failed. The element is missing from the total.</summary>
        Failed
    }

    /// <summary>
    /// HOW A MEASUREMENT BECOMES A TAKEOFF READING'S STATE.
    ///
    /// A read that THREW is not an absence: the element is missing from the total
    /// and every code it touches is a lower bound, which is why unreadable exists
    /// beside absent. The mapping lives here, Revit-free, so it can be exercised
    /// with a substituted Measurement - a real read that throws needs an element
    /// whose geometry Revit itself cannot evaluate, and one of those cannot be
    /// built on demand.
    /// </summary>
    public static class TakeoffReadingRules
    {
        public const string Means =
            "measured: a number was obtained, and it may legitimately be zero. absent: the quantity does not " +
            "apply to this element, which is not an error. unreadable: the read FAILED - the element is missing " +
            "from the total, so every code it touches is a lower bound and the comparison downstream refuses it.";

        /// <summary>The state a takeoff reading carries for this measurement.</summary>
        public static string StateFor(Measurement m)
        {
            // The SAME vocabulary the takeoff writes and the comparison reads.
            if (m == null) return QuantityState.Unreadable;
            if (m.IsMeasured) return QuantityState.Measured;
            return m.State == MeasureState.Failed ? QuantityState.Unreadable : QuantityState.Absent;
        }

        /// <summary>True when this reading must not be counted in a total.</summary>
        public static bool IsLowerBound(Measurement m) => StateFor(m) == QuantityState.Unreadable;
    }

    public sealed class Measurement
    {
        public MeasureState State { get; private set; }

        /// <summary>The value, only when State is Measured. Null otherwise - never 0.</summary>
        public double? Value { get; private set; }

        /// <summary>Why, when the state is not Measured. Null when it is.</summary>
        public string Detail { get; private set; }

        private Measurement() { }

        /// <summary>A real number, including a real zero.</summary>
        public static Measurement Of(double value) =>
            new Measurement { State = MeasureState.Measured, Value = value };

        public static Measurement NotApplicable(string why) =>
            new Measurement { State = MeasureState.NotApplicable, Detail = why };

        public static Measurement Failed(string why) =>
            new Measurement { State = MeasureState.Failed, Detail = why };

        public bool IsMeasured => State == MeasureState.Measured;

        /// <summary>Apply a unit conversion without losing the state.</summary>
        public Measurement Convert(Func<double, double> f)
        {
            if (!IsMeasured || f == null) return this;
            return Of(f(Value.Value));
        }
    }

    /// <summary>
    /// What one source managed to measure across a set of elements. `Candidates ==
    /// Measured + NotApplicable + Failed` always holds.
    /// </summary>
    public sealed class SourceTally
    {
        public string Source { get; }
        public int Candidates { get; private set; }
        public int Measured { get; private set; }
        public int NotApplicable { get; private set; }
        public int Failed { get; private set; }

        /// <summary>Sum over the Measured elements ONLY. Never includes an assumed zero.</summary>
        public double KnownTotal { get; private set; }

        public SourceTally(string source) { Source = source; }

        public void Add(Measurement m)
        {
            Candidates++;
            if (m == null) { Failed++; return; }

            switch (m.State)
            {
                case MeasureState.Measured:
                    Measured++;
                    KnownTotal += m.Value.Value;
                    break;
                case MeasureState.NotApplicable:
                    NotApplicable++;
                    break;
                default:
                    Failed++;
                    break;
            }
        }

        /// <summary>
        /// True when nothing FAILED. Elements the quantity does not apply to do not make a
        /// total incomplete - they are legitimately outside it.
        /// </summary>
        public bool TotalIsComplete => Failed == 0;

        public string Describe()
        {
            // InvariantCulture on purpose. Measured live on a Spanish-locale machine, this
            // rendered "76,6386" inside a payload whose every other number is "76.6386" -
            // the same quantity written two ways in one response, and a decimal comma that
            // a downstream parser reads as a thousands separator.
            string head = Source + ": " +
                          KnownTotal.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                          " over " + Measured + " of " + Candidates + " element(s)";
            if (NotApplicable > 0) head += ", " + NotApplicable + " with no such quantity";
            if (Failed > 0)
                head += ", and " + Failed + " that could NOT be read - this total is INCOMPLETE and " +
                        "understates by an unknown amount";
            return head + ".";
        }
    }

    /// <summary>
    /// Two sources compared on the elements where BOTH produced a number. Anything
    /// else is not a disagreement and not an agreement - it is not a comparison.
    /// </summary>
    public sealed class PairTally
    {
        public string SourceA { get; }
        public string SourceB { get; }

        public int Candidates { get; private set; }
        public int Compared { get; private set; }
        public int Agreed { get; private set; }
        public int Disagreed { get; private set; }

        /// <summary>Candidates where at least one side had no number.</summary>
        public int NotComparable => Candidates - Compared;

        /// <summary>Totals over the COMPARED elements only, so the two are like for like.</summary>
        public double ComparableTotalA { get; private set; }
        public double ComparableTotalB { get; private set; }

        public PairTally(string a, string b) { SourceA = a; SourceB = b; }

        /// <summary>
        /// Record one element. Returns true when it was actually compared, so the caller
        /// can attach a reconciliation only where one exists.
        /// </summary>
        public bool Add(Measurement a, Measurement b, Func<double, double, bool> agrees)
        {
            Candidates++;
            if (a == null || b == null || !a.IsMeasured || !b.IsMeasured) return false;

            Compared++;
            ComparableTotalA += a.Value.Value;
            ComparableTotalB += b.Value.Value;

            if (agrees != null && agrees(a.Value.Value, b.Value.Value)) Agreed++;
            else Disagreed++;
            return true;
        }

        /// <summary>
        /// true only when every candidate was compared and every comparison agreed.
        /// NULL when nothing could be compared: no evidence either way is not agreement.
        /// false when anything disagreed OR anything could not be compared.
        /// </summary>
        public bool? AllAgree
        {
            get
            {
                if (Compared == 0) return null;
                if (Disagreed > 0) return false;
                return NotComparable == 0 ? true : (bool?)false;
            }
        }

        public bool CoverageComplete => Candidates > 0 && NotComparable == 0;

        public string Headline(double tolerancePct)
        {
            if (Candidates == 0)
                return "Nothing was measured, so there is nothing to compare.";

            if (Compared == 0)
                return "NOT ONE of the " + Candidates + " element(s) could be compared: at least one of " +
                       SourceA + " / " + SourceB + " produced no number for every single one. This is not " +
                       "agreement - no comparison happened.";

            string core = Disagreed == 0
                ? "All " + Compared + " compared element(s) agree within " + tolerancePct + "% between " +
                  SourceA + " and " + SourceB + "."
                : Disagreed + " of " + Compared + " compared element(s) DISAGREE by more than " + tolerancePct +
                  "% between " + SourceA + " and " + SourceB + ". No single number is reported on purpose: " +
                  "which source is correct depends on your measurement criteria, and whoever signs the " +
                  "takeoff is the one who gets to choose.";

            if (NotComparable > 0)
                core += " COVERAGE IS PARTIAL: " + NotComparable + " further element(s) were never compared " +
                        "because at least one source had no number for them, so this verdict covers " +
                        Compared + " of " + Candidates + ".";

            return core;
        }
    }
}
