// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// EVERY TEST HERE IS A DEFECT THAT WAS FOUND BY READING THIS CODE ADVERSARIALLY
// OR BY MEASURING REVIT, after the first live run was already green.
//
// They fall into four families, and the families are the interesting part:
//
//   NON-FINITE NUMBERS pass every guard that looks for a bad one, because a
//   comparison against NaN is false whichever way it is written. An array length
//   of NaN produced a plan of NaN positions and reported success.
//
//   AN UNBOUNDED COUNT is a mistake in one number, not a set of bars. A spacing
//   of a nanometre asked for four trillion gaps; cast to int that is undefined,
//   and on one of the two runtimes this repository targets it wrapped NEGATIVE
//   and came back as two bars four metres apart under a declared maximum of a
//   nanometre.
//
//   THE AUDIT WAS BLIND to the direction a set marches in, the side it marches
//   to, its style, its mark and its measured length - so a set running the wrong
//   way through a beam matched on every field it did compare and was reported as
//   agreeing.
//
//   A VERDICT THAT IGNORES ITS OWN FINDINGS. `agrees` was returned whenever
//   errors and unknowns were zero, which included every reply carrying an `info`
//   finding - and info is where "built from a different version of the set"
//   lives.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarHardenedTests
    {
        private static RebarLayoutPlan R(string layout, int? number = null, double? spacing = null,
                                         double? array = null, bool first = true, bool last = true,
                                         double? diameter = null)
        {
            return RebarLayoutRules.Resolve(new RebarLayoutRequest
            {
                Layout = layout, Number = number, SpacingMm = spacing, ArrayLengthMm = array,
                IncludeFirstBar = first, IncludeLastBar = last, BarDiameterMm = diameter
            });
        }

        // =================================================== non-finite numbers

        [Fact]
        public void An_array_length_of_NaN_is_refused_rather_than_producing_NaN_positions()
        {
            // `if (array <= 0)` is FALSE for NaN. The old code sailed past it,
            // divided by it, and returned Ok with four positions of NaN.
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 4, array: double.NaN);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeNotFinite, p.Code);
            Assert.Empty(p.PositionsMm);
        }

        [Fact]
        public void An_INFINITE_spacing_is_refused_too()
        {
            RebarLayoutPlan p = R(RebarLayout.NumberWithSpacing, number: 5, spacing: double.PositiveInfinity);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeNotFinite, p.Code);
        }

        [Fact]
        public void A_NaN_bar_diameter_is_refused_before_the_clear_spacing_arithmetic()
        {
            RebarLayoutPlan p = R(RebarLayout.MinimumClearSpacing, spacing: 100, array: 900,
                                  diameter: double.NaN);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeNotFinite, p.Code);
        }





        // ====================================================== unbounded counts

        [Fact]
        public void A_spacing_of_a_NANOMETRE_is_refused_instead_of_overflowing_to_two_bars()
        {
            // 4000 / 1e-9 is four trillion gaps. Cast straight to int that is
            // undefined: on .NET Framework it wrapped to int.MinValue, was clamped
            // to one gap, and returned Ok with two bars four metres apart under a
            // declared maximum of a nanometre.
            RebarLayoutPlan p = R(RebarLayout.MaximumSpacing, spacing: 1e-9, array: 4000);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeTooManyBars, p.Code);
        }

        [Fact]
        public void A_clear_spacing_of_a_nanometre_is_refused_the_same_way()
        {
            RebarLayoutPlan p = R(RebarLayout.MinimumClearSpacing, spacing: 1e-9, array: 4000,
                                  diameter: 1e-9);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeTooManyBars, p.Code);
        }

        [Fact]
        public void A_HALF_BILLION_BARS_is_refused_rather_than_allocated()
        {
            // The old code allocated one double per position with no upper bound and
            // took OutOfMemoryException out through Load, whose contract is to
            // return a refusal rather than throw.
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 500000000, array: 1000);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeTooManyBars, p.Code);
        }

        // ============================================== declarations not ignored

        [Fact]
        public void include_first_bar_declared_false_beside_SINGLE_is_refused()
        {
            // Number, spacing and array length beside single are all refused by name
            // on the principle that somebody who wrote it meant something by it.
            // These two were tidied away instead, and the plan echoed true back.
            RebarLayoutPlan p = R(RebarLayout.Single, first: false);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeStatedNotUsed, p.Code);
        }

        [Fact]
        public void A_spacing_beside_fixed_number_that_AGREES_is_accepted()
        {
            // 4 positions over 900 mm IS 300 mm spacing. Refusing that was a false
            // alarm, and number_with_spacing already accepted the mirror case.
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 4, array: 900, spacing: 300);
            Assert.True(p.Ok, p.Error);
            Assert.Equal(300.0, p.ResultingSpacingMm.Value, 6);
        }

        [Fact]
        public void A_spacing_beside_fixed_number_that_DISAGREES_is_still_refused()
        {
            RebarLayoutPlan p = R(RebarLayout.FixedNumber, number: 4, array: 900, spacing: 250);
            Assert.False(p.Ok);
            Assert.Equal(RebarLayoutRules.CodeStatedNotUsed, p.Code);
        }

        [Fact]
        public void A_number_beside_maximum_spacing_that_agrees_is_accepted_and_one_that_does_not_is_refused()
        {
            Assert.True(R(RebarLayout.MaximumSpacing, spacing: 300, array: 1000, number: 5).Ok);
            RebarLayoutPlan bad = R(RebarLayout.MaximumSpacing, spacing: 300, array: 1000, number: 4);
            Assert.False(bad.Ok);
            Assert.Equal(RebarLayoutRules.CodeStatedNotUsed, bad.Code);
        }













        // ==================================================== coverage words

        [Fact]
        public void NOTHING_MEASURED_is_not_partial()
        {
            // partial's published meaning is "every count is a FLOOR", which a reader
            // takes as "at least this many were found". Nothing was found, because
            // nothing was looked at.
            Assert.Equal(StructuralCoverage.Unreadable,
                StructuralCoverage.Weakest(new[] { StructuralCoverage.Unavailable, StructuralCoverage.Unreadable }));
            Assert.Equal(StructuralCoverage.Unavailable,
                StructuralCoverage.Weakest(new[] { StructuralCoverage.Unavailable, StructuralCoverage.NotApplicable }));
        }

        [Fact]
        public void Something_measured_beside_something_unread_IS_partial()
        {
            Assert.Equal(StructuralCoverage.Partial,
                StructuralCoverage.Weakest(new[] { StructuralCoverage.Complete, StructuralCoverage.Unreadable }));
        }

        [Fact]
        public void A_word_that_is_not_in_the_vocabulary_is_refused_rather_than_dropped()
        {
            // A typo used to fall through every branch and answer not_applicable -
            // "the question did not arise".
            Assert.Throws<ArgumentException>(() => StructuralCoverage.Weakest(new[] { "unreadible" }));
            Assert.Throws<ArgumentException>(() => StructuralCoverage.Weakest(new string[] { null }));
        }



        private static JObject Expected()
        {
            return JObject.Parse(@"{
              'rule_id': 'r', 'host_id': 5001,
              'bar_type': { 'id': 300, 'nominal_diameter_mm': 10.0 },
              'shape': { 'declared': true, 'id': 400 },
              'style': 'standard',
              'mark': 'S1',
              'expected_total_steel_length_mm': 9000.0,
              'normal': { 'x': 1.0, 'y': 0.0, 'z': 0.0 },
              'layout': { 'rule': 'fixed_number', 'number_of_bar_positions': 3, 'quantity': 3,
                          'array_length_mm': 600.0, 'resulting_spacing_mm': 300.0,
                          'include_first_bar': true, 'include_last_bar': true,
                          'bars_on_normal_side': true },
              'terminations': { 'start': { 'hook_type_id': -1, 'has_hook': false, 'orientation': 'left' },
                                'end':   { 'hook_type_id': -1, 'has_hook': false, 'orientation': 'left' } }
            }".Replace('\'', '"'));
        }

        private static JObject Observed()
        {
            return JObject.Parse(@"{
              'id': 9001,
              'host': { 'id': 5001, 'resolved': true },
              'bar_type': { 'resolved': true, 'id': 300, 'nominal_diameter_mm': 10.0 },
              'shape': { 'id': 400, 'resolved': true },
              'style_horizun': 'standard',
              'layout': { 'rule_horizun': 'fixed_number', 'number_of_bar_positions': 3, 'quantity': 3,
                          'array_length_mm': 600.0, 'measured_pitch_mm': 300.0,
                          'include_first_bar': true, 'include_last_bar': true,
                          'bars_on_normal_side': true,
                          'normal': { 'x': 1.0, 'y': 0.0, 'z': 0.0 } },
              'terminations': [ { 'end': 0, 'hook_type_id': -1, 'hook_readable': true, 'orientation': 'left' },
                                { 'end': 1, 'hook_type_id': -1, 'hook_readable': true, 'orientation': 'left' } ],
              'measured': { 'schedule_mark': 'S1', 'total_length_mm': 9000.0 }
            }".Replace('\'', '"'));
        }

        private static List<string> Codes(JArray f)
        {
            return f.OfType<JObject>().Select(o => (string)o["code"]).ToList();
        }


































    }
}
