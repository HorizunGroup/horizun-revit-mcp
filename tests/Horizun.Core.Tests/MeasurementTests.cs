// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The arithmetic of a takeoff that will not invent a number.
//
// horizun_quantities produces figures somebody bills. It used to write
// `Guard.ToM3(vParam ?? 0)`: a failed read became 0.0, entered a total, and that
// total was reconciled against another total built from a DIFFERENT set of
// elements. It also started each element at `agree = true` and only ever set it
// false, so an element that could not be compared counted as one that agreed.
//
// The cases below are the ones the brief names, plus the two that produced the
// original lie: a real zero, and two sources with different coverage.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MeasurementTests
    {
        // Agreement rule used throughout: within 1%.
        private static bool Within1Pct(double a, double b)
        {
            double scale = System.Math.Max(System.Math.Abs(a), System.Math.Abs(b));
            if (scale <= 1e-9) return true;              // both zero
            return System.Math.Abs(a - b) / scale <= 0.01;
        }

        // ---- states -------------------------------------------------------------

        [Fact]
        public void A_real_zero_is_a_measurement_not_an_absence()
        {
            // THE CASE THE OLD CODE ERASED. A parameter storing 0 was folded into "no
            // data", which removed the element from the comparison - so a parameter
            // claiming zero against real geometry was never flagged.
            var m = Measurement.Of(0.0);

            Assert.True(m.IsMeasured);
            Assert.Equal(0.0, m.Value);
            Assert.Equal(MeasureState.Measured, m.State);
        }

        [Fact]
        public void Not_applicable_and_failed_are_different_things()
        {
            var na = Measurement.NotApplicable("a tag has no volume");
            var f = Measurement.Failed("geometry threw");

            Assert.False(na.IsMeasured);
            Assert.False(f.IsMeasured);
            Assert.Null(na.Value);
            Assert.Null(f.Value);
            Assert.NotEqual(na.State, f.State);
        }

        [Fact]
        public void Converting_units_keeps_the_state_and_never_invents_a_value()
        {
            Assert.Equal(2.0, Measurement.Of(1.0).Convert(v => v * 2).Value);
            Assert.Null(Measurement.Failed("x").Convert(v => v * 2).Value);
            Assert.Equal(MeasureState.Failed, Measurement.Failed("x").Convert(v => v * 2).State);
        }

        // ---- one source ---------------------------------------------------------

        [Fact]
        public void A_failed_read_is_absent_from_the_total_and_the_total_says_so()
        {
            var t = new SourceTally("geometry");
            t.Add(Measurement.Of(10));
            t.Add(Measurement.Failed("could not read"));

            Assert.Equal(10, t.KnownTotal);         // NOT 10 + 0
            Assert.Equal(1, t.Measured);
            Assert.Equal(1, t.Failed);
            Assert.False(t.TotalIsComplete);
            Assert.Contains("INCOMPLETE", t.Describe());
        }

        [Fact]
        public void An_element_the_quantity_does_not_apply_to_does_not_make_a_total_incomplete()
        {
            var t = new SourceTally("geometry");
            t.Add(Measurement.Of(10));
            t.Add(Measurement.NotApplicable("annotation"));

            Assert.Equal(10, t.KnownTotal);
            Assert.True(t.TotalIsComplete);
            Assert.DoesNotContain("INCOMPLETE", t.Describe());
        }

        [Fact]
        public void Every_candidate_lands_in_exactly_one_bucket()
        {
            var t = new SourceTally("s");
            t.Add(Measurement.Of(1));
            t.Add(Measurement.Of(0));
            t.Add(Measurement.NotApplicable("x"));
            t.Add(Measurement.Failed("y"));
            t.Add(null);                            // a missing measurement is a failure

            Assert.Equal(5, t.Candidates);
            Assert.Equal(t.Candidates, t.Measured + t.NotApplicable + t.Failed);
            Assert.Equal(2, t.Measured);
            Assert.Equal(2, t.Failed);
        }

        // ---- two sources --------------------------------------------------------

        [Fact]
        public void Both_sources_present_and_agreeing()
        {
            var p = new PairTally("param", "geometry");
            p.Add(Measurement.Of(1.0), Measurement.Of(1.005), Within1Pct);

            Assert.Equal(1, p.Compared);
            Assert.Equal(1, p.Agreed);
            Assert.True(p.AllAgree);
            Assert.True(p.CoverageComplete);
        }

        [Fact]
        public void One_source_absent_is_not_a_comparison_and_never_an_agreement()
        {
            var p = new PairTally("param", "geometry");
            p.Add(Measurement.Of(1.0), Measurement.NotApplicable("no solids"), Within1Pct);

            Assert.Equal(0, p.Compared);
            Assert.Equal(0, p.Agreed);
            Assert.Equal(1, p.NotComparable);
            Assert.Null(p.AllAgree);                // not true - nothing was compared
            Assert.False(p.CoverageComplete);
            Assert.Contains("NOT ONE", p.Headline(1.0));
        }

        [Fact]
        public void Both_sources_absent_is_still_not_an_agreement()
        {
            var p = new PairTally("param", "geometry");
            p.Add(Measurement.Failed("a"), Measurement.Failed("b"), Within1Pct);

            Assert.Equal(0, p.Compared);
            Assert.Null(p.AllAgree);
            Assert.Contains("no comparison happened", p.Headline(1.0));
        }

        [Fact]
        public void Partial_coverage_can_never_report_all_agree()
        {
            // THE HEADLINE BUG: two agreed, one was never compared, and the old code
            // announced "All 3 measured element(s) agree".
            var p = new PairTally("param", "geometry");
            p.Add(Measurement.Of(2), Measurement.Of(2), Within1Pct);
            p.Add(Measurement.Of(3), Measurement.Of(3), Within1Pct);
            p.Add(Measurement.Of(4), Measurement.Failed("geometry threw"), Within1Pct);

            Assert.Equal(2, p.Compared);
            Assert.Equal(2, p.Agreed);
            Assert.Equal(0, p.Disagreed);
            Assert.Equal(1, p.NotComparable);
            Assert.False(p.AllAgree);               // NOT true, and not null: we know one is missing
            Assert.False(p.CoverageComplete);

            string h = p.Headline(1.0);
            Assert.Contains("COVERAGE IS PARTIAL", h);
            Assert.Contains("2 of 3", h);
        }

        [Fact]
        public void Comparable_totals_cover_only_the_elements_both_sources_measured()
        {
            // The old total_reconciliation summed each source over its OWN set and then
            // compared the sums. Here source B misses the 100, so a naive comparison would
            // report a huge disagreement that is really a coverage difference.
            var p = new PairTally("param", "geometry");
            p.Add(Measurement.Of(5), Measurement.Of(5), Within1Pct);
            p.Add(Measurement.Of(100), Measurement.NotApplicable("no solids"), Within1Pct);

            Assert.Equal(5, p.ComparableTotalA);
            Assert.Equal(5, p.ComparableTotalB);
            Assert.Equal(1, p.NotComparable);
        }

        [Fact]
        public void A_disagreement_is_reported_and_survives_partial_coverage()
        {
            var p = new PairTally("param", "geometry");
            p.Add(Measurement.Of(0.4531), Measurement.Of(0.7913), Within1Pct);   // the measured 75% gap
            p.Add(Measurement.Of(1), Measurement.Failed("threw"), Within1Pct);

            Assert.Equal(1, p.Disagreed);
            Assert.False(p.AllAgree);
            string h = p.Headline(1.0);
            Assert.Contains("DISAGREE", h);
            Assert.Contains("COVERAGE IS PARTIAL", h);
        }

        [Fact]
        public void Tolerance_decides_agreement_and_two_measured_zeros_agree()
        {
            var loose = new PairTally("a", "b");
            loose.Add(Measurement.Of(100), Measurement.Of(100.5), Within1Pct);
            Assert.True(loose.AllAgree);

            var tight = new PairTally("a", "b");
            tight.Add(Measurement.Of(100), Measurement.Of(120), Within1Pct);
            Assert.False(tight.AllAgree);

            var zeros = new PairTally("a", "b");
            zeros.Add(Measurement.Of(0), Measurement.Of(0), Within1Pct);
            Assert.True(zeros.AllAgree);
            Assert.Equal(1, zeros.Compared);        // a real zero IS compared
        }

        [Fact]
        public void Nothing_to_measure_says_so_rather_than_reporting_a_clean_pass()
        {
            var p = new PairTally("a", "b");

            Assert.Equal(0, p.Candidates);
            Assert.Null(p.AllAgree);
            Assert.False(p.CoverageComplete);
            Assert.Contains("nothing to compare", p.Headline(1.0));
        }

        [Fact]
        public void A_measured_zero_against_a_real_volume_is_a_disagreement_not_an_exclusion()
        {
            // Void-only geometry measuring 0 while the parameter claims volume. The old
            // code dropped this element from the comparison entirely.
            var p = new PairTally("param", "geometry");
            p.Add(Measurement.Of(0.45), Measurement.Of(0.0), Within1Pct);

            Assert.Equal(1, p.Compared);
            Assert.Equal(1, p.Disagreed);
            Assert.False(p.AllAgree);
        }
    }
}
