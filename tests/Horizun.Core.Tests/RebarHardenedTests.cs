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

        [Fact]
        public void A_TOLERANCE_of_NaN_is_refused_because_it_makes_everything_agree()
        {
            // The mirror of the zero tolerance the parser already refused, and the
            // more dangerous one: under it no measurement can ever DISAGREE, so an
            // audit reports agreement instead of failure.
            var doc = JObject.Parse(
                ("{'schema':'horizun.structural-requirements/1'," +
                 "'requirement_set':{'id':'x','version':'1'}," +
                 "'tolerances':{'length_mm':NaN}}").Replace('\'', '"'));
            StructuralRequirementSet s = StructuralRequirementSet.Load(doc);
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeNotFinite, s.Code);
        }

        [Fact]
        public void A_tolerance_of_a_METRE_is_refused_as_well()
        {
            var doc = JObject.Parse(
                ("{'schema':'horizun.structural-requirements/1'," +
                 "'requirement_set':{'id':'x','version':'1'}," +
                 "'tolerances':{'length_mm':1000}}").Replace('\'', '"'));
            StructuralRequirementSet s = StructuralRequirementSet.Load(doc);
            Assert.False(s.Ok);
            Assert.Contains("not a tolerance", s.Error);
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

        // ================================================= the parser refuses

        [Fact]
        public void A_NON_INTEGER_element_id_is_refused_rather_than_rounded_to_a_different_element()
        {
            StructuralRequirementSet s = Set("'host': { 'element_ids': [1.5] }");
            Assert.False(s.Ok);
            Assert.Contains("not a whole number", s.Error);
        }

        [Fact]
        public void A_curve_point_that_is_not_a_NUMBER_is_refused_and_does_not_throw()
        {
            // Value<double>() throws on a string, out of a method whose contract is
            // to return a refusal.
            StructuralRequirementSet s = Set("'host': { 'element_ids': [1] }",
                                             curve: "[[0,'a',0],[1000,0,0]]");
            Assert.False(s.Ok);
            Assert.Contains("not a number", s.Error);
        }

        [Fact]
        public void A_NaN_normal_is_refused_although_it_passes_the_zero_vector_test()
        {
            StructuralRequirementSet s = Set("'host': { 'element_ids': [1] }", normal: "[NaN,0,0]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeNotFinite, s.Code);
        }

        [Fact]
        public void The_digest_does_not_change_when_two_KEYS_are_REORDERED()
        {
            // It hashed the document as written, so reordering two keys in an editor
            // - with no change to a single number - marked every bar in the model as
            // built from a different version of the set.
            JObject a = JObject.Parse(("{'schema':'s','requirement_set':{'id':'x','version':'1'}}").Replace('\'', '"'));
            JObject b = JObject.Parse(("{'requirement_set':{'version':'1','id':'x'},'schema':'s'}").Replace('\'', '"'));
            Assert.Equal(StructuralRequirementSet.Sha256Of(a), StructuralRequirementSet.Sha256Of(b));
        }

        [Fact]
        public void But_reordering_the_RULES_does_change_it_because_their_order_is_part_of_the_set()
        {
            JObject a = JObject.Parse("{\"rules\":[{\"id\":\"a\"},{\"id\":\"b\"}]}");
            JObject b = JObject.Parse("{\"rules\":[{\"id\":\"b\"},{\"id\":\"a\"}]}");
            Assert.NotEqual(StructuralRequirementSet.Sha256Of(a), StructuralRequirementSet.Sha256Of(b));
        }

        private static StructuralRequirementSet Set(string host, string curve = "[[0,0,0],[1000,0,0]]",
                                                    string normal = "[0,0,1]")
        {
            string doc = @"{
              'schema': 'horizun.structural-requirements/1',
              'requirement_set': { 'id': 'x', 'version': '1.0.0' },
              'bar_types': [ { 'id': 'T', 'type_name': '12M', 'nominal_diameter_mm': 12 } ],
              'reinforcement_rules': [ { 'id': 'r', HOST, 'bar_type': 'T', 'style': 'standard',
                  'curve_mm': CURVE, 'normal': NORMAL,
                  'layout': { 'rule': 'single' } } ]
            }".Replace('\'', '"').Replace("HOST", host.Replace('\'', '"'))
              .Replace("CURVE", curve.Replace('\'', '"')).Replace("NORMAL", normal);
            return StructuralRequirementSet.Load(JObject.Parse(doc));
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

        // ======================================================== the audit

        private static StructuralTolerances Tol()
        {
            return new StructuralTolerances { LengthMm = 2.0, SpacingMm = 2.0, CoverMm = 1.0, AngleDegrees = 1.0 };
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

        [Fact]
        public void The_baseline_pair_still_agrees()
        {
            JArray f = RebarAuditRules.CompareBar(Expected(), Observed(), Tol());
            Assert.True(f.Count == 0, string.Join(" | ", f.OfType<JObject>().Select(
                x => (string)x["code"] + "/" + (string)x["about"] + ": " + (string)x["why"])));
        }

        [Fact]
        public void A_set_marching_the_WRONG_WAY_through_the_member_is_caught()
        {
            // The one that mattered most. Everything else matches - same count, same
            // array length, same type, same hooks - and the steel runs across the
            // beam instead of along it.
            JObject o = Observed();
            o["layout"]["normal"] = JObject.Parse("{\"x\":0.0,\"y\":1.0,\"z\":0.0}");
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.NormalDiffers, Codes(f));
        }

        [Fact]
        public void A_normal_within_the_declared_ANGLE_tolerance_is_not_a_finding()
        {
            JObject o = Observed();
            o["layout"]["normal"] = JObject.Parse("{\"x\":0.9999,\"y\":0.014,\"z\":0.0}");
            Assert.DoesNotContain(RebarFinding.NormalDiffers,
                Codes(RebarAuditRules.CompareBar(Expected(), o, Tol())));
        }

        [Fact]
        public void A_set_marching_to_the_OTHER_SIDE_is_caught()
        {
            JObject o = Observed();
            o["layout"]["bars_on_normal_side"] = false;
            Assert.Contains(RebarFinding.SideDiffers, Codes(RebarAuditRules.CompareBar(Expected(), o, Tol())));
        }

        [Fact]
        public void A_STIRRUP_where_a_standard_bar_was_declared_is_caught()
        {
            JObject o = Observed();
            o["style_horizun"] = "stirrup_tie";
            Assert.Contains(RebarFinding.StyleDiffers, Codes(RebarAuditRules.CompareBar(Expected(), o, Tol())));
        }

        [Fact]
        public void A_schedule_MARK_that_is_not_the_declared_one_is_caught()
        {
            JObject o = Observed();
            o["measured"]["schedule_mark"] = "S9";
            Assert.Contains(RebarFinding.MarkDiffers, Codes(RebarAuditRules.CompareBar(Expected(), o, Tol())));
        }

        [Fact]
        public void A_bar_somebody_STRETCHED_is_caught_by_the_measured_length()
        {
            // A stirrup pulled from 220x220 to 300x300 keeps its shape id, its type,
            // its host, its quantity and its array length. Only the steel changes.
            JObject o = Observed();
            o["measured"]["total_length_mm"] = 11400.0;
            Assert.Contains(RebarFinding.LengthDiffers, Codes(RebarAuditRules.CompareBar(Expected(), o, Tol())));
        }

        [Fact]
        public void The_length_is_NOT_compared_when_a_hook_is_declared()
        {
            // Revit adds hook length itself, so comparing against a centreline that
            // excludes it would fail on every correctly built hooked bar.
            JObject e = Expected(), o = Observed();
            e["terminations"]["start"]["has_hook"] = true;
            o["measured"]["total_length_mm"] = 11400.0;
            Assert.DoesNotContain(RebarFinding.LengthDiffers, Codes(RebarAuditRules.CompareBar(e, o, Tol())));
        }

        [Fact]
        public void A_TERMINATION_THE_MODEL_DID_NOT_REPORT_is_unreadable_rather_than_silence()
        {
            JObject o = Observed();
            o["terminations"] = new JArray();
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.Unreadable, Codes(f));
            Assert.Equal(2, f.OfType<JObject>().Count(x => (string)x["about"] == RebarFinding.HookDiffers));
        }

        [Fact]
        public void A_hook_that_could_not_be_READ_is_unknown_and_not_a_difference()
        {
            // RebarFacts writes -1 both when a hook is genuinely absent and when the
            // read threw. hook_readable is what separates them.
            JObject e = Expected(), o = Observed();
            e["terminations"]["start"]["hook_type_id"] = 700;
            ((JArray)o["terminations"])[0]["hook_readable"] = false;
            JArray f = RebarAuditRules.CompareBar(e, o, Tol());
            Assert.Contains(RebarFinding.Unreadable, Codes(f));
            Assert.DoesNotContain(RebarFinding.HookDiffers, Codes(f));
        }

        [Fact]
        public void An_unreadable_SHAPE_is_unknown_rather_than_a_definite_difference()
        {
            JObject o = Observed();
            o["shape"]["resolved"] = false;
            o["shape"]["id"] = -1;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.Unreadable, Codes(f));
            Assert.DoesNotContain(RebarFinding.ShapeDiffers, Codes(f));
        }

        [Fact]
        public void The_SPACING_is_compared_against_the_pitch_MEASURED_from_the_bar_positions()
        {
            // MEASURED on Revit 2026: MaxSpacing is the DECLARED value, not the
            // resulting pitch. maximum_spacing(300 over 1000) reports MaxSpacing 300
            // and lays the bars at 250; minimum_clear_spacing(100 over 900) reports
            // 100 and lays them at 128.57. Comparing the plan's resulting spacing
            // against MaxSpacing therefore raised spacing_differs on every correct
            // set of those two layouts.
            JObject o = Observed();
            o["layout"]["measured_pitch_mm"] = 250.0;
            Assert.Contains(RebarFinding.SpacingDiffers, Codes(RebarAuditRules.CompareBar(Expected(), o, Tol())));

            JObject fine = Observed();
            fine["layout"]["measured_pitch_mm"] = 300.4;
            Assert.DoesNotContain(RebarFinding.SpacingDiffers,
                Codes(RebarAuditRules.CompareBar(Expected(), fine, Tol())));
        }

        // ======================================================= the verdict

        [Fact]
        public void An_INFO_finding_no_longer_reads_as_everything_matched()
        {
            // `agrees` was returned whenever errors and unknowns were zero, directly
            // above a finding saying the bar was built from another version of the
            // set - beside the sentence "every property this bridge checks was read
            // and matched".
            JArray f = RebarAuditRules.CheckProvenance(
                new JObject { ["written"] = true, ["requirement_set_sha256"] = "old" }, "r", 1, "set", "new");
            JObject s = RebarAuditRules.Summarise(f);
            Assert.Equal(1, (int)s["info"]);
            Assert.Equal("agrees_with_notes", (string)s["verdict"]);
            Assert.Contains("agrees_with_notes", (string)s["verdict_means"]);
        }

        [Fact]
        public void A_clean_comparison_still_says_agrees()
        {
            Assert.Equal("agrees", (string)RebarAuditRules.Summarise(new JArray())["verdict"]);
        }

        [Fact]
        public void A_bar_that_could_not_be_DESCRIBED_produces_a_finding_rather_than_agreement()
        {
            JArray f = RebarAuditRules.CompareBar(Expected(), null, Tol());
            Assert.Single(f);
            Assert.Equal("incomplete", (string)RebarAuditRules.Summarise(f)["verdict"]);
        }

        [Fact]
        public void Provenance_naming_ANOTHER_requirement_set_is_an_error_not_a_note()
        {
            JArray f = RebarAuditRules.CheckProvenance(
                new JObject
                {
                    ["written"] = true,
                    ["requirement_set_id"] = "somebody-elses-set",
                    ["requirement_set_sha256"] = "abc"
                }, "r", 1, "my-set", "abc");
            JObject one = f.OfType<JObject>().Single();
            Assert.Equal(RebarFinding.StaleRequirementSet, (string)one["code"]);
            Assert.Equal(RebarSeverity.Error, (string)one["severity"]);
        }

        [Fact]
        public void Provenance_with_NO_DIGEST_is_unknown_rather_than_agreement()
        {
            JArray f = RebarAuditRules.CheckProvenance(
                new JObject { ["written"] = true, ["requirement_set_id"] = "my-set" }, "r", 1, "my-set", "abc");
            Assert.Contains(RebarFinding.Unreadable, Codes(f));
        }
    }
}
