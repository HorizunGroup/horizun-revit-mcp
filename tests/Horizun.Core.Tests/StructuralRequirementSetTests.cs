// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// These test a parser by what it REFUSES.
//
// A requirement set is where somebody's engineering arrives. The failure this
// file exists to prevent is not a crash - it is a set that is accepted with a
// hole in it, so that half of somebody's reinforcement is built and they are
// left believing they asked for what arrived.
//
// So: a missing number is never filled in, a declared number that this layout
// would not use is refused rather than ignored, and a set that fails anywhere
// carries NO rules at all afterwards.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StructuralRequirementSetTests
    {
        private const string Head = @"{
  'schema': 'horizun.structural-requirements/1',
  'requirement_set': { 'id': 'beams', 'version': '1.0.0', 'title': 'Beam reinforcement' },
  'units': 'millimeter',
  'bar_types': [ { 'id': 'T16', 'type_name': '16M', 'nominal_diameter_mm': 16 } ],
  'hook_types': [ { 'id': 'H135', 'type_name': 'Stirrup/Tie Hook - 135 deg' },
                  { 'id': 'NONE', 'none': true } ],";

        private static StructuralRequirementSet Load(string body)
        {
            return StructuralRequirementSet.Load(JObject.Parse((Head + body + "}").Replace('\'', '"')));
        }

        private const string OneRule = @"
  'reinforcement_rules': [ {
      'id': 'bottom',
      'host': { 'category': 'OST_StructuralFraming' },
      'bar_type': 'T16',
      'style': 'standard',
      'curve_mm': [[0,0,0],[4000,0,0]],
      'normal': [0,1,0],
      'layout': { 'rule': 'fixed_number', 'number': 4, 'array_length_mm': 300 }
  } ]";

        // ------------------------------------------------------------ accepted

        [Fact]
        public void A_complete_set_loads_and_keeps_what_was_declared()
        {
            StructuralRequirementSet s = Load(OneRule);
            Assert.True(s.Ok, s.Error);
            Assert.Equal("beams", s.Id);
            Assert.Single(s.RebarRules);
            StructuralRebarRule r = s.RebarRules[0];
            Assert.Equal("T16", r.BarTypeId);
            Assert.Equal(RebarLayout.FixedNumber, r.Layout.Layout);
            Assert.Equal(4, r.Layout.Number);
            Assert.Equal(2, r.CurvesMm.Count);
            Assert.True(r.Required);
        }

        [Fact]
        public void The_bar_DIAMETER_reaches_the_layout_so_clear_spacing_can_be_computed()
        {
            // minimum_clear_spacing measures between bar surfaces, and the diameter
            // only exists on the bar type. If it did not reach the layout the rule
            // would be refused for a missing diameter that was declared two blocks
            // above it.
            StructuralRequirementSet s = Load(@"
  'reinforcement_rules': [ {
      'id': 'clear', 'host': { 'category': 'OST_Walls' }, 'bar_type': 'T16', 'style': 'standard',
      'curve_mm': [[0,0,0],[1000,0,0]], 'normal': [0,0,1],
      'layout': { 'rule': 'minimum_clear_spacing', 'spacing_mm': 100, 'array_length_mm': 1000 }
  } ]".Replace('\'', '"').Replace('"', '\''));
            Assert.True(s.Ok, s.Error);
            Assert.Equal(16.0, s.RebarRules[0].Layout.BarDiameterMm.Value, 6);
        }

        [Fact]
        public void The_only_defaults_are_Revits_own_and_they_are_echoed_rather_than_hidden()
        {
            StructuralRequirementSet s = Load(OneRule);
            Assert.True(s.RebarRules[0].Layout.IncludeFirstBar);
            Assert.True(s.RebarRules[0].Layout.IncludeLastBar);
            Assert.Equal(StructuralStyle.Standard, s.RebarRules[0].Style);
        }

        // ------------------------------------------------------------ refused

        [Fact]
        public void Another_schema_is_refused_by_name()
        {
            var doc = JObject.Parse("{\"schema\":\"horizun.cad-requirements/1\"}");
            StructuralRequirementSet s = StructuralRequirementSet.Load(doc);
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeSchema, s.Code);
        }

        [Fact]
        public void UNITS_other_than_millimetres_are_refused_rather_than_converted()
        {
            // Every length in this schema is millimetres BY DEFINITION. Accepting a
            // second unit would mean each number had to be read together with a
            // field that a hand-edited set can lose - and feet read as millimetres
            // is a bar 300 times too short with nothing looking wrong.
            var doc = JObject.Parse(
                ("{'schema':'horizun.structural-requirements/1','units':'feet'," +
                 "'requirement_set':{'id':'x','version':'1'}}").Replace('\'', '"'));
            StructuralRequirementSet s = StructuralRequirementSet.Load(doc);
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnits, s.Code);
        }

        [Fact]
        public void A_bar_type_the_rules_reference_but_nobody_declared_is_refused()
        {
            StructuralRequirementSet s = Load(OneRule.Replace("'T16'", "'T20'"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnresolvedReference, s.Code);
            Assert.Contains("T20", s.Error);
        }

        [Fact]
        public void An_EMPTY_host_selector_is_refused_because_it_would_match_everything()
        {
            StructuralRequirementSet s = Load(OneRule.Replace(
                "'host': { 'category': 'OST_StructuralFraming' }", "'host': { }"));
            Assert.False(s.Ok);
            Assert.Contains("selects nothing", s.Error);
        }

        [Fact]
        public void A_missing_NORMAL_is_refused_rather_than_derived_from_the_curve()
        {
            // The same bar distributes along a beam or up a column depending on the
            // normal. Deriving it from one bar's own curves would be guessing which
            // member it is in.
            StructuralRequirementSet s = Load(OneRule.Replace("'normal': [0,1,0],", ""));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Contains("normal", s.Error);
        }

        [Fact]
        public void A_ZERO_normal_is_refused()
        {
            StructuralRequirementSet s = Load(OneRule.Replace("'normal': [0,1,0]", "'normal': [0,0,0]"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeGeometry, s.Code);
        }

        [Fact]
        public void A_bar_with_no_CURVE_is_refused_rather_than_derived_from_its_host()
        {
            StructuralRequirementSet s = Load(OneRule.Replace("'curve_mm': [[0,0,0],[4000,0,0]],", ""));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Contains("design decision", s.Error);
        }

        [Fact]
        public void TWO_IDENTICAL_consecutive_points_are_refused_with_their_index()
        {
            // Revit refuses a zero-length segment deep inside its geometry engine,
            // with a message about nothing in particular. Naming the index here is
            // the difference between a fix and an afternoon.
            StructuralRequirementSet s = Load(OneRule.Replace(
                "[[0,0,0],[4000,0,0]]", "[[0,0,0],[0,0,0],[4000,0,0]]"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeGeometry, s.Code);
            Assert.Contains("index 0 and 1", s.Error);
        }

        [Fact]
        public void A_CLOSED_loop_that_also_repeats_its_first_point_is_refused()
        {
            StructuralRequirementSet s = Load(OneRule.Replace(
                "'curve_mm': [[0,0,0],[4000,0,0]],",
                "'curve_mm': [[0,0,0],[300,0,0],[300,500,0],[0,500,0],[0,0,0]], 'closed': true,"));
            Assert.False(s.Ok);
            Assert.Contains("repeats its first point", s.Error);
        }

        [Fact]
        public void An_unknown_STYLE_is_refused_and_says_why_it_is_not_inferred()
        {
            StructuralRequirementSet s = Load(OneRule.Replace("'style': 'standard'", "'style': 'stirrup'"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Contains("stirrup_tie", s.Error);
        }

        [Fact]
        public void A_layout_that_the_arithmetic_refuses_takes_the_whole_SET_down()
        {
            StructuralRequirementSet s = Load(OneRule.Replace(
                "'rule': 'fixed_number', 'number': 4, 'array_length_mm': 300",
                "'rule': 'fixed_number', 'number': 4, 'array_length_mm': 300, 'spacing_mm': 90"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeLayout, s.Code);
        }

        [Fact]
        public void A_hook_type_that_nobody_declared_is_refused()
        {
            StructuralRequirementSet s = Load(OneRule.Replace(
                "'normal': [0,1,0],", "'normal': [0,1,0], 'start': { 'hook_type': 'H90' },"));
            Assert.False(s.Ok);
            Assert.Contains("H90", s.Error);
        }

        [Fact]
        public void An_unknown_hook_ORIENTATION_is_refused_with_the_two_words_that_work()
        {
            StructuralRequirementSet s = Load(OneRule.Replace(
                "'normal': [0,1,0],",
                "'normal': [0,1,0], 'start': { 'hook_type': 'H135', 'orientation': 'inward' },"));
            Assert.False(s.Ok);
            Assert.Contains("left", s.Error);
            Assert.Contains("right", s.Error);
        }

        [Fact]
        public void Per_face_COVER_is_refused_by_name_rather_than_silently_applied_to_all_faces()
        {
            StructuralRequirementSet s = Load(@"
  'cover_rules': [ { 'id': 'c1', 'host': { 'category': 'OST_Floors' },
                     'face': 'top', 'distance_mm': 40 } ]".Replace('\'', '"').Replace('"', '\''));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Contains("Only 'common' is implemented", s.Error);
        }

        [Fact]
        public void A_cover_rule_that_names_NEITHER_a_type_nor_a_distance_is_refused()
        {
            StructuralRequirementSet s = Load(@"
  'cover_rules': [ { 'id': 'c1', 'host': { 'category': 'OST_Floors' } } ]"
                .Replace('\'', '"').Replace('"', '\''));
            Assert.False(s.Ok);
            Assert.Contains("design decision", s.Error);
        }

        [Fact]
        public void A_set_that_asks_for_NOTHING_is_refused()
        {
            StructuralRequirementSet s = Load("\n  'reinforcement_rules': [ ]".Replace('\'', '"').Replace('"', '\''));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeNoRules, s.Code);
        }

        [Fact]
        public void Two_rules_with_ONE_id_are_refused()
        {
            string two = OneRule.Replace("] }", "");   // keep it simple: duplicate the object
            StructuralRequirementSet s = Load(@"
  'reinforcement_rules': [
     { 'id': 'a', 'host': { 'category': 'OST_Walls' }, 'bar_type': 'T16', 'style': 'standard',
       'curve_mm': [[0,0,0],[1000,0,0]], 'normal': [0,0,1],
       'layout': { 'rule': 'single' } },
     { 'id': 'a', 'host': { 'category': 'OST_Walls' }, 'bar_type': 'T16', 'style': 'standard',
       'curve_mm': [[0,0,0],[1000,0,0]], 'normal': [0,0,1],
       'layout': { 'rule': 'single' } } ]".Replace('\'', '"').Replace('"', '\''));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeDuplicateId, s.Code);
        }

        [Fact]
        public void A_ZERO_tolerance_is_refused_because_nothing_could_ever_agree_with_it()
        {
            var doc = JObject.Parse(
                ("{'schema':'horizun.structural-requirements/1'," +
                 "'requirement_set':{'id':'x','version':'1'}," +
                 "'tolerances':{'length_mm':0}}").Replace('\'', '"'));
            StructuralRequirementSet s = StructuralRequirementSet.Load(doc);
            Assert.False(s.Ok);
            Assert.Contains("tolerances.length_mm", s.Error);
        }

        // ------------------------------------------------------ nothing partial

        [Fact]
        public void A_REFUSED_set_carries_no_rules_at_all()
        {
            // The failure this whole file is about: half a requirement set is worse
            // than none, because some of somebody's reinforcement gets built.
            StructuralRequirementSet s = Load(@"
  'cover_rules': [ { 'id': 'c1', 'host': { 'category': 'OST_Floors' }, 'distance_mm': 40 } ],
  'reinforcement_rules': [ { 'id': 'broken', 'host': { 'category': 'OST_Walls' }, 'bar_type': 'NOPE',
       'style': 'standard',
       'curve_mm': [[0,0,0],[1000,0,0]], 'normal': [0,0,1], 'layout': { 'rule': 'single' } } ]"
                .Replace('\'', '"').Replace('"', '\''));
            Assert.False(s.Ok);
            Assert.Empty(s.RebarRules);
            Assert.Empty(s.CoverRules);   // the VALID cover rule is gone too
        }

        // -------------------------------------------------------------- digest

        [Fact]
        public void The_digest_is_stable_and_changes_with_the_content()
        {
            JObject a = JObject.Parse((Head + OneRule + "}").Replace('\'', '"'));
            JObject b = JObject.Parse((Head + OneRule.Replace("'number': 4", "'number': 5") + "}").Replace('\'', '"'));
            Assert.Equal(StructuralRequirementSet.Sha256Of(a), StructuralRequirementSet.Sha256Of(a));
            Assert.NotEqual(StructuralRequirementSet.Sha256Of(a), StructuralRequirementSet.Sha256Of(b));
            Assert.Equal(64, StructuralRequirementSet.Sha256Of(a).Length);
        }
    }
}
