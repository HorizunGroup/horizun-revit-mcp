// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A HOOK LENGTH IS DECLARED, NEVER CHOSEN. "Estribo #3 con ganchos de 135 de .10"
// is a straight extension of 100 mm somebody is answerable for, and Revit stores
// it on the BAR TYPE, per hook type - so writing it changes every bar of that
// type with that hook, not only the bars this set builds. These tests hold the
// parser to the declaration (refusals name their path) and hold the plan, the
// apply's re-read and the audit to one comparison, made on the row the Revit
// reader emits (HookLengthRules.ReadRow) and nothing else.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HookLengthTests
    {
        private const string Head = @"{
  'schema': 'horizun.structural-requirements/1',
  'requirement_set': { 'id': 'stirrups', 'version': '1.0.0' },
  'units': 'millimeter',
  'bar_types': [ { 'id': 'N3', 'type_name': '#3' }, { 'id': 'N5', 'type_name': '#5' } ],";

        private const string OneRule = @",
  'reinforcement_rules': [ {
      'id': 'bottom', 'host': { 'category': 'OST_StructuralFraming' }, 'bar_type': 'N5', 'style': 'standard',
      'curve_mm': [[0,0,0],[4000,0,0]], 'normal': [0,1,0],
      'layout': { 'rule': 'fixed_number', 'number': 4, 'array_length_mm': 300 }
  } ]";

        private static StructuralRequirementSet Load(string hookTypes)
        {
            return StructuralRequirementSet.Load(
                JObject.Parse((Head + " 'hook_types': " + hookTypes + OneRule + "}").Replace('\'', '"')));
        }

        // ------------------------------------------------------------- parsing

        [Fact]
        public void A_declared_length_is_kept_per_bar_type_and_listed_as_a_pair()
        {
            StructuralRequirementSet s = Load(
                "[ { 'id': 'H135', 'type_name': 'Stirrup/Tie - 135 deg', 'lengths_mm': { 'N3': 100, 'N5': 150.5 } }," +
                "  { 'id': 'NONE', 'none': true } ]");
            Assert.True(s.Ok, s.Error);
            Assert.Equal(100.0, s.HookTypes["H135"].LengthsMm["N3"]);
            Assert.Equal(150.5, s.HookTypes["H135"].LengthsMm["N5"]);
            Assert.Empty(s.HookTypes["NONE"].LengthsMm);

            var pairs = s.DeclaredHookLengths();
            Assert.Equal(2, pairs.Count);
            DeclaredHookLength p = pairs[0];
            Assert.Equal("H135", p.HookTypeId);
            Assert.Equal("Stirrup/Tie - 135 deg", p.HookTypeName);
            Assert.Equal("N3", p.BarTypeId);
            Assert.Equal("#3", p.BarTypeName);
            Assert.Equal(100.0, p.LengthMm);
            Assert.Equal("hook_types['H135'].lengths_mm['N3']", p.Path);
        }

        [Fact]
        public void A_set_without_lengths_declares_no_hook_length_and_changes_no_type()
        {
            StructuralRequirementSet s = Load("[ { 'id': 'H135', 'type_name': 'Stirrup/Tie - 135 deg' } ]");
            Assert.True(s.Ok, s.Error);
            Assert.Empty(s.DeclaredHookLengths());
        }

        [Fact]
        public void A_length_for_a_bar_type_the_set_does_not_declare_is_refused_with_its_path()
        {
            StructuralRequirementSet s = Load("[ { 'id': 'H135', 'type_name': 'T', 'lengths_mm': { 'N4': 100 } } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnresolvedReference, s.Code);
            Assert.Equal("hook_types['H135'].lengths_mm['N4']", s.Path);
            Assert.Empty(s.RebarRules);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-10")]
        public void A_length_that_is_not_greater_than_zero_is_refused(string v)
        {
            StructuralRequirementSet s = Load("[ { 'id': 'H135', 'type_name': 'T', 'lengths_mm': { 'N3': " + v + " } } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("hook_types['H135'].lengths_mm['N3']", s.Path);
        }

        [Fact]
        public void A_length_that_is_not_finite_is_refused()
        {
            StructuralRequirementSet s = Load("[ { 'id': 'H135', 'type_name': 'T', 'lengths_mm': { 'N3': NaN } } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeNotFinite, s.Code);
            Assert.Equal("hook_types['H135'].lengths_mm['N3']", s.Path);
        }

        [Theory]
        [InlineData("'100'")]
        [InlineData("true")]
        [InlineData("null")]
        public void A_length_that_is_not_a_number_is_refused_rather_than_converted(string v)
        {
            StructuralRequirementSet s = Load("[ { 'id': 'H135', 'type_name': 'T', 'lengths_mm': { 'N3': " + v + " } } ]");
            Assert.False(s.Ok);
            Assert.Equal("hook_types['H135'].lengths_mm['N3']", s.Path);
            Assert.Empty(s.RebarRules);
        }

        [Fact]
        public void A_length_on_a_straight_end_is_refused()
        {
            StructuralRequirementSet s = Load("[ { 'id': 'NONE', 'none': true, 'lengths_mm': { 'N3': 100 } } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("hook_types['NONE'].lengths_mm", s.Path);
        }

        [Theory]
        [InlineData("100")]
        [InlineData("[100]")]
        [InlineData("{}")]
        public void Lengths_that_are_not_a_non_empty_object_are_refused(string v)
        {
            StructuralRequirementSet s = Load("[ { 'id': 'H135', 'type_name': 'T', 'lengths_mm': " + v + " } ]");
            Assert.False(s.Ok);
            Assert.Equal("hook_types['H135'].lengths_mm", s.Path);
        }

        [Fact]
        public void Two_entries_that_give_one_pair_of_types_two_lengths_are_refused()
        {
            // One bar type, one hook type, two numbers: whichever was written last would be
            // the one in the model, and nobody declared that order.
            StructuralRequirementSet s = Load(
                "[ { 'id': 'A', 'type_name': 'Stirrup/Tie - 135 deg', 'lengths_mm': { 'N3': 100 } }," +
                "  { 'id': 'B', 'type_name': 'Stirrup/Tie - 135 deg', 'lengths_mm': { 'N3': 120 } } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("hook_types['B'].lengths_mm['N3']", s.Path);
        }

        [Fact]
        public void Two_entries_that_agree_on_one_pair_are_one_declaration()
        {
            StructuralRequirementSet s = Load(
                "[ { 'id': 'A', 'type_name': 'Stirrup/Tie - 135 deg', 'lengths_mm': { 'N3': 100 } }," +
                "  { 'id': 'B', 'type_name': 'Stirrup/Tie - 135 deg', 'lengths_mm': { 'N3': 100 } } ]");
            Assert.True(s.Ok, s.Error);
            Assert.Single(s.DeclaredHookLengths());
        }

        [Fact]
        public void The_key_is_admitted_and_a_misspelling_is_pointed_at_it()
        {
            StructuralRequirementSet s = Load("[ { 'id': 'H135', 'type_name': 'T', 'length_mm': { 'N3': 100 } } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Contains("lengths_mm", s.Allowed);
            Assert.Contains("lengths_mm", s.DidYouMean);
        }

        // ---------------------------------------------------- the reader's row

        private static readonly DeclaredHookLength Pair = new DeclaredHookLength
        {
            HookTypeId = "H135", HookTypeName = "Stirrup/Tie - 135 deg",
            BarTypeId = "N3", BarTypeName = "#3", LengthMm = 100
        };

        // EXACTLY the shape RebarHookLengths.Read emits on the Revit side: it builds the row
        // with HookLengthRules.ReadRow, so a fake written here cannot drift from it.
        private static JObject Row(bool? auto, double? mm, string why = null) => HookLengthRules.ReadRow(auto, mm, why);

        [Fact]
        public void The_read_row_has_one_shape()
        {
            JObject r = Row(true, 76.2);
            Assert.Equal(new[] { "auto_calculated", "length_mm", "readable", "why" },
                         r.Properties().Select(p => p.Name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray());
            Assert.True((bool)r["readable"]);
            Assert.False((bool)Row(null, null, "Revit raised")["readable"]);
            Assert.False((bool)Row(false, null, "no length")["readable"]);
        }

        // ---------------------------------------------------------------- plan

        [Fact]
        public void The_plan_names_the_type_change_with_what_the_model_has_now()
        {
            JObject e = HookLengthRules.PlanEntry(Pair, 501, 601, Row(true, 76.2), 2.0);
            Assert.True((bool)e["change"]);
            Assert.Equal("N3", (string)e["bar_type"]["id"]);
            Assert.Equal("#3", (string)e["bar_type"]["type_name"]);
            Assert.Equal(501L, (long)e["bar_type"]["element_id"]);
            Assert.Equal("H135", (string)e["hook_type"]["id"]);
            Assert.Equal(601L, (long)e["hook_type"]["element_id"]);
            Assert.True((bool)e["current"]["auto_calculated"]);
            Assert.Equal(76.2, (double)e["current"]["length_mm"], 6);
            Assert.Equal(100.0, (double)e["declared_mm"], 6);
            Assert.Equal("hook_types['H135'].lengths_mm['N3']", (string)e["path"]);
            Assert.Contains("TYPE", (string)e["means"]);
            Assert.Contains("every bar", (string)e["means"]);
        }

        [Fact]
        public void A_pair_already_fixed_at_the_declared_length_is_listed_without_a_change()
        {
            JObject e = HookLengthRules.PlanEntry(Pair, 501, 601, Row(false, 101.5), 2.0);
            Assert.False((bool)e["change"]);
        }

        [Fact]
        public void An_auto_calculated_length_that_happens_to_match_is_still_a_change()
        {
            // Auto-calculated follows the bend diameter; the declaration is a fixed number.
            JObject e = HookLengthRules.PlanEntry(Pair, 501, 601, Row(true, 100), 2.0);
            Assert.True((bool)e["change"]);
        }

        [Fact]
        public void A_pair_that_could_not_be_read_is_a_change_and_says_it_was_not_read()
        {
            JObject e = HookLengthRules.PlanEntry(Pair, 501, 601, Row(null, null, "Revit raised"), 2.0);
            Assert.True((bool)e["change"]);
            Assert.False((bool)e["current"]["readable"]);
        }

        [Fact]
        public void An_unresolved_pair_carries_its_code_and_no_change()
        {
            JObject e = HookLengthRules.Unresolved(Pair, "bar_type_not_found", "no RebarBarType is named '#3'.");
            Assert.False((bool)e["resolved"]);
            Assert.Equal("bar_type_not_found", (string)e["code"]);
            Assert.Equal(JTokenType.Null, e["change"].Type);
        }

        // -------------------------------------------------------- apply re-read

        [Fact]
        public void A_pair_that_reads_back_fixed_at_the_declared_length_is_verified()
        {
            JObject v = HookLengthRules.VerifyEntry(Pair, 501, 601, Row(true, 76.2), Row(false, 100.0000001), true, 2.0);
            Assert.True((bool)v["verified"]);
            Assert.True((bool)v["written"]);
            Assert.Equal(100.0, (double)v["requested_mm"], 6);
            Assert.False((bool)v["read"]["auto_calculated"]);
            Assert.True((bool)v["before"]["auto_calculated"]);
        }

        [Theory]
        [InlineData(true, 100.0)]
        [InlineData(false, 76.2)]
        public void A_pair_that_does_not_read_back_is_not_verified(bool auto, double mm)
        {
            JObject v = HookLengthRules.VerifyEntry(Pair, 501, 601, Row(true, 76.2), Row(auto, mm), true, 2.0);
            Assert.False((bool)v["verified"]);
        }

        [Fact]
        public void A_pair_that_could_not_be_read_back_is_not_verified()
        {
            JObject v = HookLengthRules.VerifyEntry(Pair, 501, 601, Row(true, 76.2), Row(null, null, "raised"), true, 2.0);
            Assert.False((bool)v["verified"]);
        }

        // ---------------------------------------------------------------- audit

        [Fact]
        public void A_model_length_that_differs_is_an_error_with_its_evidence()
        {
            JArray f = HookLengthRules.AuditFindings(Pair, 501, 601, Row(false, 76.2), 2.0);
            JObject x = Assert.IsType<JObject>(Assert.Single(f));
            Assert.Equal(RebarFinding.HookLengthDiffers, (string)x["code"]);
            Assert.Equal(RebarSeverity.Error, (string)x["severity"]);
            Assert.Equal(100.0, (double)x["expected"], 6);
            Assert.Equal(76.2, (double)x["observed"], 6);
            Assert.Equal("hook_types['H135'].lengths_mm['N3']", (string)x["rule_id"]);
            Assert.Equal(501L, (long)x["bar_type_element_id"]);
            Assert.Equal(601L, (long)x["hook_type_element_id"]);
            Assert.True((bool)x["fixable"]);
            Assert.Equal("horizun_apply_reinforcement", (string)x["suggested_typed_action"]);
        }

        [Fact]
        public void A_model_length_fixed_within_the_tolerance_agrees()
        {
            Assert.Empty(HookLengthRules.AuditFindings(Pair, 501, 601, Row(false, 101.9), 2.0));
        }

        [Fact]
        public void An_auto_calculated_length_is_a_difference_even_at_the_same_number()
        {
            JArray f = HookLengthRules.AuditFindings(Pair, 501, 601, Row(true, 100), 2.0);
            JObject x = Assert.IsType<JObject>(Assert.Single(f));
            Assert.Equal(RebarFinding.HookLengthDiffers, (string)x["code"]);
            Assert.Equal(RebarSeverity.Error, (string)x["severity"]);
        }

        [Fact]
        public void A_length_that_could_not_be_read_is_unknown_never_a_pass()
        {
            JArray f = HookLengthRules.AuditFindings(Pair, 501, 601, Row(null, null, "Revit raised"), 2.0);
            JObject x = Assert.IsType<JObject>(Assert.Single(f));
            Assert.Equal(RebarSeverity.Unknown, (string)x["severity"]);
            Assert.Equal(RebarFinding.HookLengthDiffers, (string)x["about"]);
        }

        [Fact]
        public void The_finding_code_is_published()
        {
            Assert.Equal("hook_length_differs", RebarFinding.HookLengthDiffers);
            Assert.Contains(RebarFinding.HookLengthDiffers, RebarFinding.All);
        }
    }
}
