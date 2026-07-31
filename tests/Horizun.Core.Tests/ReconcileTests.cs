// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// Unit tests for Reconcile: the Revit-free arithmetic behind the "cannot lie"
// contract. Compiled with the source linked in (see the .csproj), so these run
// with no Revit assemblies loaded — the whole reason that arithmetic lives apart
// from Guard.
//
// The invariants under test are the ones that caught real lies: a zero on one
// side must never divide-by-zero nor flatter an agreement, the percentage is
// measured against the LARGER magnitude, and intent (intended) is never accepted
// as evidence of outcome (actual).
// -----------------------------------------------------------------------------
using Xunit;
using Horizun.Revit.Core;

namespace Horizun.Core.Tests
{
    public class ReconcileTests
    {
        // ----- Compare: agreement boundary -----------------------------------

        [Fact]
        public void Compare_ExactEquality_Agrees()
        {
            var c = Reconcile.Compare(10.0, 10.0);
            Assert.True(c.Agree);
            Assert.Equal(0.0, c.Difference, 6);
            Assert.Equal(0.0, c.DifferencePct, 6);
        }

        [Fact]
        public void Compare_JustWithinTolerance_Agrees()
        {
            // biggest = 100, delta = 1 => rel = 0.01, exactly at the 1% bound.
            var c = Reconcile.Compare(100.0, 99.0, 0.01);
            Assert.True(c.Agree);
            Assert.Equal(1.0, c.Difference, 6);
            Assert.Equal(1.0, c.DifferencePct, 6);
        }

        [Fact]
        public void Compare_JustOutsideTolerance_DoesNotAgree()
        {
            // biggest = 100, delta = 1.1 => rel = 0.011, just past the 1% bound.
            var c = Reconcile.Compare(100.0, 98.9, 0.01);
            Assert.False(c.Agree);
            Assert.Equal(1.1, c.Difference, 6);
            Assert.Equal(1.1, c.DifferencePct, 6);
        }

        [Fact]
        public void Compare_PercentIsAgainstLargerMagnitude()
        {
            // delta = 50. Against the larger (100) that is 50%; against the
            // smaller (50) it would be 100%. The contract says use the larger.
            var c = Reconcile.Compare(100.0, 50.0);
            Assert.False(c.Agree);
            Assert.Equal(50.0, c.DifferencePct, 6);
            // Order must not matter — same magnitudes, same answer.
            var flipped = Reconcile.Compare(50.0, 100.0);
            Assert.Equal(c.DifferencePct, flipped.DifferencePct, 6);
        }

        // ----- Compare: rounding ---------------------------------------------

        [Fact]
        public void Compare_DifferenceRoundedTo4dp()
        {
            // delta = 0.123456789 -> 0.1235 at 4dp.
            var c = Reconcile.Compare(0.0, 0.123456789, tolerance: 1.0);
            Assert.Equal(0.1235, c.Difference, 6);
        }

        [Fact]
        public void Compare_PercentRoundedTo1dp()
        {
            // biggest = 1057, delta = 57 => rel = 0.0539262... => 5.39262% -> 5.4 at 1dp.
            var c = Reconcile.Compare(1000.0, 1057.0);
            Assert.Equal(57.0, c.Difference, 6);
            Assert.Equal(5.4, c.DifferencePct, 6);
        }

        // ----- Compare: the zero-guard ---------------------------------------

        [Fact]
        public void Compare_ZeroVersusValue_DoesNotDivideByZeroAndDoesNotAgree()
        {
            // biggest = 5 (not the guard branch): rel = 1.0 => never agrees,
            // and no division by |a| = 0. This is the anti-"one side vanished"
            // guard: a measured 5 against a missing 0 is a 100% gap, not a match.
            var c = Reconcile.Compare(0.0, 5.0);
            Assert.False(c.Agree);
            Assert.Equal(5.0, c.Difference, 6);
            Assert.Equal(100.0, c.DifferencePct, 6);
        }

        [Fact]
        public void Compare_ZeroVersusZero_Agrees()
        {
            // biggest = 0 hits the guard branch: rel forced to 0, no divide.
            var c = Reconcile.Compare(0.0, 0.0);
            Assert.True(c.Agree);
            Assert.Equal(0.0, c.Difference, 6);
            Assert.Equal(0.0, c.DifferencePct, 6);
        }

        // ----- Compare: a non-finite input is an ABSENT measurement -----------
        //
        // The zero-guard used to swallow these: Math.Max(NaN, x) is NaN, NaN > 1e-9 is
        // false, so rel became 0 and the answer came back Agree = true — a fabricated
        // agreement between a real number and nothing at all. Worse than the zero case
        // it was written to prevent.

        [Fact]
        public void Compare_NaNAgainstARealValue_DoesNotAgreeAndIsNotComparable()
        {
            var c = Reconcile.Compare(double.NaN, 5.0);
            Assert.False(c.Agree);
            Assert.False(c.Comparable);

            var flipped = Reconcile.Compare(5.0, double.NaN);
            Assert.False(flipped.Agree);
            Assert.False(flipped.Comparable);
        }

        [Fact]
        public void Compare_NaNAgainstNaN_IsStillNotAnAgreement()
        {
            // Two sources that both measured nothing have not agreed on anything.
            var c = Reconcile.Compare(double.NaN, double.NaN);
            Assert.False(c.Agree);
            Assert.False(c.Comparable);
        }

        [Fact]
        public void Compare_Infinity_IsNotComparable()
        {
            var c = Reconcile.Compare(double.PositiveInfinity, 5.0);
            Assert.False(c.Agree);
            Assert.False(c.Comparable);
        }

        [Fact]
        public void Compare_NotComparable_ReportsNoDifference_NeverNaN()
        {
            // NaN is not valid JSON: leaking it here poisons the whole response.
            var c = Reconcile.Compare(double.NaN, 5.0);
            Assert.False(double.IsNaN(c.Difference));
            Assert.False(double.IsNaN(c.DifferencePct));
        }

        [Fact]
        public void Compare_RealValues_AreComparable()
        {
            Assert.True(Reconcile.Compare(100.0, 99.0).Comparable);
            Assert.True(Reconcile.Compare(0.0, 0.0).Comparable);
        }

        [Fact]
        public void IsMeasurement_RejectsOnlyTheNonFinite()
        {
            Assert.True(Reconcile.IsMeasurement(0.0));
            Assert.True(Reconcile.IsMeasurement(-12.5));
            Assert.False(Reconcile.IsMeasurement(double.NaN));
            Assert.False(Reconcile.IsMeasurement(double.PositiveInfinity));
            Assert.False(Reconcile.IsMeasurement(double.NegativeInfinity));
        }

        // ----- Unit conversions ----------------------------------------------

        [Fact]
        public void Conversion_Constants_AreTheMetricBoundaryValues()
        {
            Assert.Equal(0.0283168466, Reconcile.Ft3ToM3, 12);
            Assert.Equal(0.09290304, Reconcile.Ft2ToM2, 12);
            Assert.Equal(0.3048, Reconcile.FtToM, 12);
        }

        [Fact]
        public void ToM3_ConvertsCubicFeetToCubicMetres()
        {
            // A 10x10x10 ft cube = 1000 ft3 -> 28.3168466 m3.
            Assert.Equal(28.3168466, Reconcile.ToM3(1000.0), 9);
            Assert.Equal(0.0, Reconcile.ToM3(0.0), 12);
        }

        [Fact]
        public void ToM2_ConvertsSquareFeetToSquareMetres()
        {
            Assert.Equal(9.290304, Reconcile.ToM2(100.0), 9);
        }

        [Fact]
        public void ToM_ConvertsFeetToMetres()
        {
            Assert.Equal(3.048, Reconcile.ToM(10.0), 9);
        }

        // ----- Verified: intent is not evidence ------------------------------

        [Fact]
        public void Verified_CountMatches_IsTrue()
        {
            // Intended 5, model reports 5 — the only case that may return true.
            Assert.True(Reconcile.Verified(5, 5));
        }

        [Fact]
        public void Verified_IntendedManyButActualZero_IsFalse()
        {
            // The "758 purged" lie: intended 758, model changed nothing.
            Assert.False(Reconcile.Verified(758, 0));
        }

        [Fact]
        public void Verified_CountMismatch_IsFalse()
        {
            Assert.False(Reconcile.Verified(1, 8));
        }
    }
}
