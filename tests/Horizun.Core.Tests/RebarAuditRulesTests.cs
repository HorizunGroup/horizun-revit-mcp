// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The failure these protect against is an audit that reports a clean model it
// never finished looking at. Every property that cannot be read has to produce a
// finding; a quiet audit and an agreeing audit must never be the same reply.
//
// Both sides of the comparison are the JSON that ReinforcementResolver and
// RebarFacts actually emit, so these cannot drift from either.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarAuditRulesTests
    {
        private static StructuralTolerances Tol()
        {
            return new StructuralTolerances { LengthMm = 2.0, SpacingMm = 2.0, CoverMm = 1.0 };
        }

        /// <summary>The shape ReinforcementResolver.DescribeRebarRow emits.</summary>
        private static JObject Expected()
        {
            return JObject.Parse(@"{
              'rule_id': 'beam-stirrups',
              'host_id': 5001,
              'bar_type': { 'id': 300, 'name': '10M', 'nominal_diameter_mm': 10.0 },
              'shape': { 'declared': true, 'id': 400, 'name': 'T1' },
              'style': 'stirrup_tie',
              'normal': { 'x': 1.0, 'y': 0.0, 'z': 0.0 },
              'layout': { 'rule': 'maximum_spacing', 'number_of_bar_positions': 9, 'quantity': 9,
                          'array_length_mm': 3800.0, 'resulting_spacing_mm': 475.0,
                          'include_first_bar': true, 'include_last_bar': true,
                          'bars_on_normal_side': true },
              'terminations': {
                 'start': { 'hook_type_id': 700, 'has_hook': true, 'orientation': 'left' },
                 'end':   { 'hook_type_id': 700, 'has_hook': true, 'orientation': 'right' } }
            }".Replace('\'', '"'));
        }

        /// <summary>The shape RebarFacts.Describe emits.</summary>
        private static JObject Observed()
        {
            return JObject.Parse(@"{
              'id': 9001,
              'host': { 'id': 5001, 'resolved': true },
              'bar_type': { 'resolved': true, 'id': 300, 'nominal_diameter_mm': 10.0 },
              'shape': { 'id': 400, 'resolved': true },
              'style_horizun': 'stirrup_tie',
              'layout': { 'rule_horizun': 'maximum_spacing', 'number_of_bar_positions': 9, 'quantity': 9,
                          'array_length_mm': 3800.0, 'measured_pitch_mm': 475.0,
                          'include_first_bar': true, 'include_last_bar': true,
                          'bars_on_normal_side': true,
                          'normal': { 'x': 1.0, 'y': 0.0, 'z': 0.0 } },
              'terminations': [
                 { 'end': 0, 'hook_type_id': 700, 'hook_readable': true, 'orientation': 'left' },
                 { 'end': 1, 'hook_type_id': 700, 'hook_readable': true, 'orientation': 'right' } ],
              'measured': { 'schedule_mark': 'S1' }
            }".Replace('\'', '"'));
        }

        private static List<string> Codes(JArray f)
        {
            return f.OfType<JObject>().Select(o => (string)o["code"]).ToList();
        }

        // ------------------------------------------------------------- agrees

        [Fact]
        public void A_model_that_carries_what_was_asked_produces_no_findings()
        {
            JArray f = RebarAuditRules.CompareBar(Expected(), Observed(), Tol());
            Assert.Empty(f);
            Assert.Equal("agrees", (string)RebarAuditRules.Summarise(f)["verdict"]);
        }

        // ------------------------------------------------------- disagreements

        [Fact]
        public void A_different_bar_TYPE_is_a_finding_with_both_ids()
        {
            JObject o = Observed();
            o["bar_type"]["id"] = 301;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.TypeDiffers);
            Assert.Equal(300, (int)one["expected"]);
            Assert.Equal(301, (int)one["observed"]);
            Assert.True((bool)one["fixable"]);
            Assert.Equal("horizun_apply_reinforcement", (string)one["suggested_typed_action"]);
        }

        [Fact]
        public void ONE_BAR_MISSING_from_the_set_is_caught_by_the_count()
        {
            // The single most common real difference: somebody deleted a stirrup.
            JObject o = Observed();
            o["layout"]["quantity"] = 8;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().First(x => (string)x["code"] == RebarFinding.QuantityDiffers);
            Assert.Equal(9, (int)one["expected"]);
            Assert.Equal(8, (int)one["observed"]);
        }

        [Fact]
        public void POSITIONS_and_BARS_are_compared_separately()
        {
            // A set with a suppressed end bar has 9 positions and 8 bars, and both
            // numbers are real. Comparing only one of them misses half the ways a
            // set can differ.
            JObject e = Expected(), o = Observed();
            o["layout"]["quantity"] = 8;
            o["layout"]["number_of_bar_positions"] = 9;
            JArray f = RebarAuditRules.CompareBar(e, o, Tol());
            Assert.Single(f.OfType<JObject>(), x => (string)x["code"] == RebarFinding.QuantityDiffers);
        }

        [Fact]
        public void A_hook_that_turns_the_other_way_is_a_finding_naming_the_END()
        {
            JObject o = Observed();
            ((JArray)o["terminations"])[1]["orientation"] = "left";
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.OrientationDiffers);
            Assert.Contains("end end", (string)one["why"]);
            Assert.Equal("right", (string)one["expected"]);
            Assert.Equal("left", (string)one["observed"]);
        }

        [Fact]
        public void An_array_length_INSIDE_the_tolerance_is_not_a_finding()
        {
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3801.5;
            Assert.Empty(RebarAuditRules.CompareBar(Expected(), o, Tol()));
        }

        [Fact]
        public void An_array_length_OUTSIDE_the_tolerance_is_one_and_says_what_it_allowed()
        {
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3810.0;
            JObject one = RebarAuditRules.CompareBar(Expected(), o, Tol())
                .OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.ArrayLengthDiffers);
            // The plan here declares no allowance, so the bound collapses to
            // equality and the finding says so: nothing short is permitted, plus
            // the length tolerance.
            Assert.Equal("0 mm short, plus 2 mm", (string)one["tolerance"]);
        }

        // -------------------------------- the array length is a BOUND, not equality
        //
        // Revit lays a set out over somewhere between the declared array length and
        // one MODEL bar diameter less than it, and eleven live measurements across
        // Revit 2023 and 2026 found no rule that says which. Comparing for equality
        // raised a finding on every correctly built array whose bar was thicker
        // than the tolerance - which is every real bar.

        [Fact]
        public void An_array_a_whole_model_diameter_short_is_NOT_a_finding_when_the_plan_allows_it()
        {
            JObject e = Expected();
            e["layout"]["array_length_shortfall_allowed_mm"] = 12.0;
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3788.0;      // declared 3800, a 12 mm bar
            Assert.DoesNotContain(RebarFinding.ArrayLengthDiffers,
                                  Codes(RebarAuditRules.CompareBar(e, o, Tol())));
        }

        [Fact]
        public void An_array_shorter_than_the_allowance_still_is_a_finding()
        {
            JObject e = Expected();
            e["layout"]["array_length_shortfall_allowed_mm"] = 12.0;
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3780.0;      // 20 short against 12 allowed + 2
            JObject one = RebarAuditRules.CompareBar(e, o, Tol())
                .OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.ArrayLengthDiffers);
            Assert.Equal("12 mm short, plus 2 mm", (string)one["tolerance"]);
        }

        [Fact]
        public void An_array_LONGER_than_declared_is_a_finding_even_with_an_allowance()
        {
            // The allowance is one-sided on purpose. Nothing measured has ever
            // produced an array longer than the declaration, so it is not a case
            // this understands - and an unknown is not a pass.
            JObject e = Expected();
            e["layout"]["array_length_shortfall_allowed_mm"] = 12.0;
            JObject o = Observed();
            o["layout"]["array_length_mm"] = 3815.0;
            Assert.Contains(RebarFinding.ArrayLengthDiffers,
                            Codes(RebarAuditRules.CompareBar(e, o, Tol())));
        }

        [Fact]
        public void A_bar_hosted_by_something_ELSE_is_caught()
        {
            JObject o = Observed();
            o["host"]["id"] = 5099;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.HostMissing, Codes(f));
        }

        [Fact]
        public void A_bar_whose_host_does_not_RESOLVE_is_caught_too()
        {
            JObject o = Observed();
            o["host"]["resolved"] = false;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().Single(x => (string)x["code"] == RebarFinding.HostMissing);
            Assert.False((bool)one["fixable"]);
        }

        // ----------------------------------------------- unknown is not a pass

        [Fact]
        public void A_property_that_could_not_be_READ_produces_a_finding_not_silence()
        {
            JObject o = Observed();
            o["layout"]["quantity"] = null;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            JObject one = f.OfType<JObject>().First(x => (string)x["code"] == RebarFinding.Unreadable);
            Assert.Equal(RebarSeverity.Unknown, (string)one["severity"]);
            Assert.Equal(RebarFinding.QuantityDiffers, (string)one["about"]);
            Assert.Contains("UNKNOWN IS NOT A PASS", (string)one["why"]);
        }

        [Fact]
        public void An_unreadable_ORIENTATION_is_unknown_rather_than_a_difference()
        {
            JObject o = Observed();
            ((JArray)o["terminations"])[0]["orientation"] = null;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.Unreadable, Codes(f));
            Assert.DoesNotContain(RebarFinding.OrientationDiffers, Codes(f));
        }

        [Fact]
        public void A_model_with_NOTHING_WRONG_and_something_unread_is_INCOMPLETE_not_clean()
        {
            // The whole reason the verdict is not a boolean.
            JObject o = Observed();
            o["layout"]["array_length_mm"] = null;
            JObject s = RebarAuditRules.Summarise(RebarAuditRules.CompareBar(Expected(), o, Tol()));
            Assert.Equal("incomplete", (string)s["verdict"]);
            Assert.Equal(0, (int)s["errors"]);
            Assert.Equal(1, (int)s["unknown"]);
            Assert.Contains("partly audited", (string)s["verdict_means"]);
        }

        [Fact]
        public void The_summary_names_what_this_bridge_does_NOT_check()
        {
            // A gap nobody wrote down is indistinguishable from a gap nobody found.
            JObject s = RebarAuditRules.Summarise(new JArray());
            var not = ((JArray)s["not_checked"]).Select(t => (string)t).ToList();
            Assert.Contains("lap_insufficient", not);
            Assert.Contains("missing_coupler", not);
            Assert.Contains("overlapping_bar", not);
        }

        [Fact]
        public void A_SINGLE_bar_is_not_asked_about_a_first_or_last_bar_it_cannot_have()
        {
            // MEASURED LIVE, 2026-08-28: Revit THROWS for IncludeFirstBar,
            // IncludeLastBar and BarsOnNormalSide on a single-bar set - there is no
            // first, no last and no side for an array of one. Reading that as
            // `unreadable` reported every correct single-bar rule as INCOMPLETE: a
            // partly audited model, for three questions that never arose.
            JObject e = Expected(), o = Observed();
            e["layout"]["rule"] = "single";
            e["layout"]["quantity"] = 1;
            e["layout"]["number_of_bar_positions"] = 1;
            e["layout"]["array_length_mm"] = 0.0;
            e["layout"]["resulting_spacing_mm"] = null;
            o["layout"]["rule_horizun"] = "single";
            o["layout"]["quantity"] = 1;
            o["layout"]["number_of_bar_positions"] = 1;
            o["layout"]["array_length_mm"] = 0.0;
            o["layout"]["include_first_bar"] = null;
            o["layout"]["include_last_bar"] = null;
            // single has no pitch and no array, on either side.
            e["layout"]["resulting_spacing_mm"] = null;
            o["layout"]["measured_pitch_mm"] = null;

            JArray f = RebarAuditRules.CompareBar(e, o, Tol());
            Assert.Empty(f);
            Assert.Equal("agrees", (string)RebarAuditRules.Summarise(f)["verdict"]);
        }

        [Fact]
        public void But_a_DISTRIBUTED_set_that_will_not_report_those_flags_is_still_unknown()
        {
            // The exemption is about the question not arising, not about the flag
            // being awkward to read. On any layout that has an array, silence is
            // still silence.
            JObject o = Observed();
            o["layout"]["include_first_bar"] = null;
            JArray f = RebarAuditRules.CompareBar(Expected(), o, Tol());
            Assert.Contains(RebarFinding.Unreadable, Codes(f));
        }

        // ---------------------------------------------------------- provenance

        [Fact]
        public void A_bar_with_NO_provenance_is_reported_and_is_not_an_error()
        {
            // A bar somebody modelled by hand carries none. That is a fact about
            // attribution, not a fault in the model.
            JArray f = RebarAuditRules.CheckProvenance(
                new JObject { ["written"] = false }, "r", 9001, "set", "abc");
            JObject one = f.OfType<JObject>().Single();
            Assert.Equal(RebarFinding.ProvenanceMissing, (string)one["code"]);
            Assert.Equal(RebarSeverity.Info, (string)one["severity"]);
        }

        [Fact]
        public void A_bar_built_from_an_OLDER_version_of_the_set_is_reported_not_judged()
        {
            JArray f = RebarAuditRules.CheckProvenance(
                new JObject { ["written"] = true, ["requirement_set_sha256"] = "old" }, "r", 9001, "set", "new");
            JObject one = f.OfType<JObject>().Single();
            Assert.Equal(RebarFinding.StaleRequirementSet, (string)one["code"]);
            Assert.Equal(RebarSeverity.Info, (string)one["severity"]);
            Assert.Contains("reported rather than judged", (string)one["why"]);
        }

        [Fact]
        public void A_bar_from_the_SAME_version_produces_nothing()
        {
            Assert.Empty(RebarAuditRules.CheckProvenance(
                new JObject { ["written"] = true, ["requirement_set_sha256"] = "same" }, "r", 9001, "set", "same"));
        }

        // --------------------------------------------------------------- marks

        [Fact]
        public void TWO_BARS_WITH_ONE_MARK_are_a_finding_because_a_schedule_counts_them_once()
        {
            var a = Observed(); a["id"] = 1;
            var b = Observed(); b["id"] = 2;
            JArray f = RebarAuditRules.DuplicateMarks(new[] { a, b });
            JObject one = f.OfType<JObject>().Single();
            Assert.Equal(RebarFinding.BarMarkDuplicate, (string)one["code"]);
            Assert.Equal(RebarSeverity.Error, (string)one["severity"]);
            Assert.Equal(new[] { 1L, 2L }, ((JArray)one["rebar_ids"]).Select(t => (long)t));
        }

        [Fact]
        public void Distinct_marks_and_blank_marks_are_not_duplicates()
        {
            var a = Observed(); a["id"] = 1;
            var b = Observed(); b["id"] = 2; b["measured"]["schedule_mark"] = "S2";
            var c = Observed(); c["id"] = 3; c["measured"]["schedule_mark"] = "";
            var d = Observed(); d["id"] = 4; d["measured"]["schedule_mark"] = "";
            // Two blanks are not two bars sharing a mark: an unmarked bar is
            // unmarked, and reporting them as a clash would bury the real ones.
            Assert.Empty(RebarAuditRules.DuplicateMarks(new[] { a, b, c, d }));
        }

        // ------------------------------------------------------------ vocabulary

        [Fact]
        public void Every_finding_code_is_in_the_published_list()
        {
            var produced = new List<string>();
            JObject o = Observed();
            o["bar_type"]["id"] = 999;
            o["layout"]["quantity"] = 8;
            o["layout"]["rule_horizun"] = "fixed_number";
            o["host"]["id"] = 1;
            ((JArray)o["terminations"])[0]["hook_type_id"] = 0;
            produced.AddRange(Codes(RebarAuditRules.CompareBar(Expected(), o, Tol())));
            produced.AddRange(Codes(RebarAuditRules.CheckProvenance(new JObject { ["written"] = false }, "r", 1, "s", "h")));
            Assert.NotEmpty(produced);
            foreach (string c in produced) Assert.Contains(c, RebarFinding.All);
        }

        [Fact]
        public void Every_finding_carries_its_evidence()
        {
            JObject o = Observed();
            o["layout"]["quantity"] = 8;
            foreach (JObject f in RebarAuditRules.CompareBar(Expected(), o, Tol()).OfType<JObject>())
            {
                Assert.NotNull(f["code"]);
                Assert.NotNull(f["severity"]);
                Assert.NotNull(f["tolerance"]);
                Assert.False(string.IsNullOrWhiteSpace((string)f["why"]));
                Assert.NotNull(f["expected"]);
            }
        }
    }
}
