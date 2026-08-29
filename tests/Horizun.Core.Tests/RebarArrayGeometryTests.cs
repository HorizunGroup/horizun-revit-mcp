using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// The eleven live measurements, and the bound that survived them.
    ///
    /// A first version of this file asserted a FORMULA - the model reports the
    /// declared array length minus exactly one model bar diameter - because that
    /// is what four measurements on Revit 2026 showed. Seven more on Revit 2023,
    /// varying one thing at a time, showed a full diameter, half a diameter and
    /// none at all, with no rule separating them. The formula went; the bound
    /// stayed. These tests encode the bound and the cases that killed the formula,
    /// so nobody reinstates it.
    /// </summary>
    public class RebarArrayGeometryTests
    {
        private const double Tol = 2.0;

        /// <summary>
        /// The eleven live cases, each named. They are given as MemberData rather
        /// than InlineData because several are numerically identical and only the
        /// LABEL tells them apart - and a duplicate InlineData is both a compiler
        /// warning and a row nobody can trace back to the bar it came from.
        /// </summary>
        public static IEnumerable<object[]> MeasuredCases()
        {
            // Revit 2026, closed stirrup, declared 5949.2 - a full model diameter.
            yield return new object[] { "2026 closed stirrup, model 12", 5949.2, 5937.2, 12.0 };
            yield return new object[] { "2026 closed stirrup, model 20", 5949.2, 5929.2, 20.0 };
            yield return new object[] { "2026 closed stirrup, model 32", 5949.2, 5917.2, 32.0 };
            // nominal 12 with a MODEL diameter of 25: it is the model one that bounds it.
            yield return new object[] { "2026 closed stirrup, nominal 12 model 25", 5949.2, 5924.2, 25.0 };
            // Revit 2023, same wall and same 12 mm bar, one variable at a time.
            yield return new object[] { "2023 closed profile, stirrup_tie, max 400", 5950.0, 5938.0, 12.0 };
            yield return new object[] { "2023 open bar, standard, max 400", 5950.0, 5938.0, 12.0 };
            yield return new object[] { "2023 open bar, stirrup_tie, max 400 - HALF", 5950.0, 5944.0, 12.0 };
            yield return new object[] { "2023 closed profile, standard, max 400", 5950.0, 5938.0, 12.0 };
            yield return new object[] { "2023 open bar, standard, max 300", 5950.0, 5938.0, 12.0 };
            yield return new object[] { "2023 open bar, standard, max 200", 5950.0, 5938.0, 12.0 };
            yield return new object[] { "2023 open bar, standard, max 500 - HALF", 5950.0, 5944.0, 12.0 };
            // and a straight slab bar on the same Revit, short by NOTHING at all.
            yield return new object[] { "2023 straight slab bar - NONE", 5880.0, 5880.0, 12.0 };
        }

        [Theory]
        [MemberData(nameof(MeasuredCases))]
        public void Every_measured_case_is_inside_the_bound(
            string label, double declared, double model, double diameter)
        {
            double shortfall;
            string why;
            Assert.True(RebarArrayGeometry.SpanIsWithinBound(declared, model, diameter, Tol, out shortfall, out why),
                label + ": measured on real Revit. If this is ever false the bound is wrong, not the model.");
            Assert.Equal(declared - model, shortfall, 6);
            Assert.Equal(RebarArrayGeometry.WhyMeasuredNotPredicted, why);
        }

        [Fact]
        public void The_formula_this_replaced_would_have_failed_three_of_those_cases()
        {
            // The retired rule was "expect declared minus one model diameter". Held
            // to it, the three cases below are wrong by 6, 6 and 12 mm - all outside
            // a 2 mm tolerance, and all correctly built arrays. This is the test
            // that says why there is a bound here and not an equation.
            var killers = new[]
            {
                new { declared = 5950.0, model = 5944.0, diameter = 12.0 },  // half a diameter
                new { declared = 5950.0, model = 5944.0, diameter = 12.0 },  // half a diameter
                new { declared = 5880.0, model = 5880.0, diameter = 12.0 },  // none at all
            };
            foreach (var k in killers)
            {
                double predictedByTheOldRule = k.declared - k.diameter;
                Assert.True(System.Math.Abs(k.model - predictedByTheOldRule) > Tol);

                double shortfall;
                string why;
                Assert.True(RebarArrayGeometry.SpanIsWithinBound(
                    k.declared, k.model, k.diameter, Tol, out shortfall, out why));
            }
        }

        [Fact]
        public void An_array_shorter_than_a_whole_diameter_is_a_finding()
        {
            double shortfall;
            string why;
            // One diameter plus the tolerance is the edge; a millimetre past it is not.
            Assert.True(RebarArrayGeometry.SpanIsWithinBound(5950.0, 5936.0, 12.0, Tol, out shortfall, out why));
            Assert.False(RebarArrayGeometry.SpanIsWithinBound(5950.0, 5935.0, 12.0, Tol, out shortfall, out why));
            Assert.Equal(15.0, shortfall, 6);
            Assert.Contains("more than one model bar diameter", why);
        }

        [Fact]
        public void An_array_LONGER_than_declared_is_refused_rather_than_waved_through()
        {
            double shortfall;
            string why;
            Assert.True(RebarArrayGeometry.SpanIsWithinBound(5950.0, 5951.0, 12.0, Tol, out shortfall, out why));
            Assert.False(RebarArrayGeometry.SpanIsWithinBound(5950.0, 5960.0, 12.0, Tol, out shortfall, out why));
            Assert.Equal(-10.0, shortfall, 6);
            Assert.Contains("LONGER", why);
        }

        [Fact]
        public void A_bar_type_that_will_not_report_a_diameter_is_not_a_pass()
        {
            double shortfall;
            string why;
            Assert.False(RebarArrayGeometry.SpanIsWithinBound(5950.0, 5938.0, 0, Tol, out shortfall, out why));
            Assert.Equal(RebarArrayGeometry.WhyNoDiameter, why);
        }

        [Fact]
        public void A_model_that_would_not_report_its_array_is_not_a_pass_either()
        {
            double shortfall;
            string why;
            Assert.False(RebarArrayGeometry.SpanIsWithinBound(5950.0, double.NaN, 12.0, Tol, out shortfall, out why));
            Assert.Equal(RebarArrayGeometry.WhyRevitWouldNotSay, why);
        }

        [Fact]
        public void A_declared_length_that_is_not_positive_is_refused_before_anything_else()
        {
            double shortfall;
            string why;
            Assert.False(RebarArrayGeometry.SpanIsWithinBound(0, 100, 12.0, Tol, out shortfall, out why));
            Assert.False(RebarArrayGeometry.SpanIsWithinBound(-1, 100, 12.0, Tol, out shortfall, out why));
            Assert.False(RebarArrayGeometry.SpanIsWithinBound(double.NaN, 100, 12.0, Tol, out shortfall, out why));
            Assert.NotEqual(RebarArrayGeometry.WhyNoDiameter, why);
        }

        [Fact]
        public void The_first_position_is_the_anchor_and_the_last_takes_the_whole_shift()
        {
            var declared = Evenly(0, 5949.2, 16);
            var moved = RebarArrayGeometry.Rescale(declared, 5949.2, 5937.2);

            Assert.Equal(16, moved.Count);
            // MEASURED: the worst difference over sixteen 12 mm bars was 12.00 mm
            // and not 6.00, which is what tells first-anchored from centred.
            Assert.Equal(0.0, moved[0] - declared[0], 6);
            Assert.Equal(12.0, declared[15] - moved[15], 6);
            double worst = declared.Select((d, i) => System.Math.Abs(d - moved[i])).Max();
            Assert.Equal(12.0, worst, 6);
        }

        [Fact]
        public void A_set_that_marches_the_other_way_shrinks_the_same_way()
        {
            // bars_on_normal_side false puts the set at NEGATIVE offsets.
            var declared = Evenly(0, -5949.2, 16);
            var moved = RebarArrayGeometry.Rescale(declared, 5949.2, 5937.2);

            Assert.Equal(0.0, moved[0], 6);
            Assert.Equal(-5937.2, moved[15], 3);
            Assert.True(System.Math.Abs(moved[15]) < System.Math.Abs(declared[15]));
        }

        [Fact]
        public void An_array_that_starts_away_from_zero_keeps_its_start()
        {
            var declared = Evenly(1000.0, 1000.0 + 5949.2, 16);
            var moved = RebarArrayGeometry.Rescale(declared, 5949.2, 5937.2);

            Assert.Equal(1000.0, moved[0], 6);
            Assert.Equal(1000.0 + 5937.2, moved[15], 3);
        }

        [Fact]
        public void Rescaling_onto_the_span_the_model_reports_is_exact_for_every_measured_case()
        {
            // The point of measuring rather than predicting: whatever Revit did,
            // distributing the predicted positions across the span it REPORTS puts
            // them where the model has them. Half a diameter, a whole one, or none.
            foreach (var model in new[] { 5938.0, 5944.0, 5950.0 })
            {
                var declared = Evenly(0, 5950.0, 16);
                var moved = RebarArrayGeometry.Rescale(declared, 5950.0, model);
                Assert.Equal(0.0, moved[0], 6);
                Assert.Equal(model, moved[15], 6);
                // Evenly spaced across the span the model used.
                double pitch = model / 15.0;
                for (int i = 1; i < moved.Count; i++)
                    Assert.Equal(pitch, moved[i] - moved[i - 1], 6);
            }
        }

        [Fact]
        public void One_bar_is_not_an_array_and_is_returned_untouched()
        {
            var one = new List<double> { 42.0 };
            var moved = RebarArrayGeometry.Rescale(one, 5949.2, 5937.2);
            Assert.Single(moved);
            Assert.Equal(42.0, moved[0], 6);

            Assert.Empty(RebarArrayGeometry.Rescale(new List<double>(), 5949.2, 5937.2));
            Assert.Null(RebarArrayGeometry.Rescale(null, 5949.2, 5937.2));
        }

        [Fact]
        public void A_nonsense_span_leaves_the_positions_alone_rather_than_dividing_by_it()
        {
            var declared = Evenly(0, 100, 5);
            foreach (double bad in new[] { 0.0, -1.0, double.NaN })
                Assert.Equal(declared, RebarArrayGeometry.Rescale(declared, bad, 90));
            // An unusable target span must not silently become positions at NaN,
            // which compare false against everything and read as a real difference
            // rather than an unanswered question.
            Assert.Equal(declared, RebarArrayGeometry.Rescale(declared, 100, double.NaN));
        }

        private static List<double> Evenly(double from, double to, int count)
        {
            var list = new List<double>(count);
            for (int i = 0; i < count; i++) list.Add(from + (to - from) * i / (count - 1));
            return list;
        }
    }
}
