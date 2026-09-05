// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A misspelt key is a rule that silently does not run.
//
// Before this file, `lenght_mm` on a stirrup zone was read as "no length" - the
// zone became the remainder of the span - and the plan that came back was
// complete, plausible and wrong. The parser now refuses any key it does not
// admit, at the root, inside every object and inside every list item, names
// the exact path, lists what IS admitted there and suggests the nearest key.
// Every valid set from the sibling test files still loads unchanged.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StructuralRequirementSetStrictKeysTests
    {
        private const string Head = @"{
  'schema': 'horizun.structural-requirements/1',
  'requirement_set': { 'id': 'beams', 'version': '1.0.0', 'title': 'Beam reinforcement' },
  'units': 'millimeter',
  'bar_types': [ { 'id': 'T16', 'type_name': '16M', 'nominal_diameter_mm': 16 },
                 { 'id': 'T10', 'type_name': '10M', 'nominal_diameter_mm': 10 } ],
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
      'layout': { 'rule': 'fixed_number', 'number': 4, 'array_length_mm': 300 },
      'start': { 'hook_type': 'H135', 'orientation': 'left' }
  } ]";

        private const string OneZoneRule = @"
  'stirrup_zone_rules': [ {
      'id': 'B1',
      'host': { 'element_ids': [1001] },
      'bar_type': 'T10',
      'allow_new_shape': true,
      'profile_mm': [[0,0,0],[0,300,0],[0,300,500],[0,0,500]],
      'along': [1,0,0],
      'span_mm': 6000,
      'zones': [
        { 'name': 'start', 'length_mm': 1500, 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 100 } },
        { 'name': 'middle', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 200 } }
      ]
  } ]";

        private const string OneMatRule = @"
  'mat_rules': [ {
      'id': 'S1',
      'host': { 'category': 'OST_Floors' },
      'face_normal': [0,0,1],
      'components': [
        { 'name': 'top_x', 'bar_type': 'T10', 'direction': [1,0,0], 'offset_from_face_mm': 30,
          'allow_new_shape': true, 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 200 } }
      ]
  } ]";

        // ------------------------------------------------------- compatibility

        [Fact]
        public void Every_valid_shape_still_loads_with_every_admitted_key_present()
        {
            StructuralRequirementSet s = Load(@"
  'tolerances': { 'length_mm': 2, 'spacing_mm': 2, 'cover_mm': 1, 'angle_degrees': 1 },
  'cover_rules': [ { 'id': 'c', 'host': { 'category': 'OST_StructuralFraming', 'type_name': 'B', 'element_ids': [7] },
                     'face': 'common', 'cover_type_name': 'Interior', 'distance_mm': 40, 'required': true } ]," +
                OneRule + "," + OneZoneRule + "," + OneMatRule);
            Assert.True(s.Ok, s.Error);
            Assert.Null(s.Path);
            Assert.Empty(s.Allowed);
            Assert.Single(s.CoverRules);
            Assert.Single(s.RebarRules);
            Assert.Single(s.StirrupZoneRules);
            Assert.Single(s.MatRules);
            Assert.Equal(2, s.StirrupZoneRules[0].Zones.Count);
        }

        // ------------------------------------------------------------- root

        [Fact]
        public void A_misspelt_root_section_is_refused_with_the_nearest_key()
        {
            StructuralRequirementSet s = Load(OneRule + @", 'matt_rules': []");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("matt_rules", s.Path);
            Assert.Contains("mat_rules", s.DidYouMean);
            Assert.Contains("mat_rules", s.Allowed);
            Assert.Contains("stirrup_zone_rules", s.Allowed);
            Assert.Contains("matt_rules", s.Error);
            Assert.Empty(s.RebarRules);   // a refused set carries nothing
        }

        [Fact]
        public void A_misspelt_rule_section_cannot_shrink_the_set_silently()
        {
            // Before: 'reinforcment_rules' was skipped and the zone rule alone
            // loaded - a smaller set that looked complete.
            StructuralRequirementSet s = Load(OneZoneRule + @",
  'reinforcment_rules': [ { 'id': 'x' } ]");
            Assert.False(s.Ok);
            Assert.Equal("reinforcment_rules", s.Path);
            Assert.Equal(new[] { "reinforcement_rules" }, s.DidYouMean);
            Assert.Empty(s.StirrupZoneRules);
        }

        [Fact]
        public void An_unknown_header_key_is_refused_at_its_path()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(JObject.Parse((@"{
  'schema': 'horizun.structural-requirements/1',
  'requirement_set': { 'id': 'beams', 'version': '1.0.0', 'titel': 'x' },
  'bar_types': [ { 'id': 'T16', 'type_name': '16M' } ]," + OneRule + "}").Replace('\'', '"')));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("requirement_set.titel", s.Path);
            Assert.Equal(new[] { "id", "title", "version" }, s.Allowed);
        }

        [Fact]
        public void An_unknown_tolerance_is_refused_rather_than_left_at_its_default()
        {
            StructuralRequirementSet s = Load(@"'tolerances': { 'lenght_mm': 5 }," + OneRule);
            Assert.False(s.Ok);
            Assert.Equal("tolerances.lenght_mm", s.Path);
            Assert.Contains("length_mm", s.DidYouMean);
        }

        // ------------------------------------------------------- list items

        [Fact]
        public void A_typo_inside_a_bar_type_names_the_index()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(JObject.Parse((@"{
  'schema': 'horizun.structural-requirements/1',
  'requirement_set': { 'id': 'beams', 'version': '1.0.0' },
  'bar_types': [ { 'id': 'T16', 'type_name': '16M' },
                 { 'id': 'T10', 'type_name': '10M', 'nominal_diameter': 10 } ]," + OneRule + "}").Replace('\'', '"')));
            Assert.False(s.Ok);
            Assert.Equal("bar_types[1].nominal_diameter", s.Path);
            Assert.Equal(new[] { "nominal_diameter_mm" }, s.DidYouMean);
        }

        [Fact]
        public void A_typo_inside_a_reinforcement_rule_is_refused_before_anything_is_read()
        {
            StructuralRequirementSet s = Load(@"
  'reinforcement_rules': [ {
      'id': 'bottom',
      'host': { 'category': 'OST_StructuralFraming' },
      'bar_type': 'T16',
      'style': 'standard',
      'curve': [[0,0,0],[4000,0,0]],
      'normal': [0,1,0],
      'layout': { 'rule': 'fixed_number', 'number': 4, 'array_length_mm': 300 }
  } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("reinforcement_rules[0].curve", s.Path);
            Assert.Contains("curve_mm", s.DidYouMean);
        }

        [Fact]
        public void A_typo_inside_a_layout_block_is_refused_with_the_nested_path()
        {
            StructuralRequirementSet s = Load(@"
  'reinforcement_rules': [ {
      'id': 'bottom',
      'host': { 'category': 'OST_StructuralFraming' },
      'bar_type': 'T16',
      'style': 'standard',
      'curve_mm': [[0,0,0],[4000,0,0]],
      'normal': [0,1,0],
      'layout': { 'rule': 'fixed_number', 'number': 4, 'array_length': 300 }
  } ]");
            Assert.False(s.Ok);
            Assert.Equal("reinforcement_rules['bottom'].layout.array_length", s.Path);
            Assert.Equal(new[] { "array_length_mm" }, s.DidYouMean);
            Assert.Contains("spacing_mm", s.Allowed);
        }

        [Fact]
        public void A_typo_inside_a_host_selector_is_refused_at_the_selector_path()
        {
            StructuralRequirementSet s = Load(@"
  'reinforcement_rules': [ {
      'id': 'bottom',
      'host': { 'categroy': 'OST_StructuralFraming' },
      'bar_type': 'T16',
      'style': 'standard',
      'curve_mm': [[0,0,0],[4000,0,0]],
      'normal': [0,1,0],
      'layout': { 'rule': 'fixed_number', 'number': 4, 'array_length_mm': 300 }
  } ]");
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("reinforcement_rules['bottom'].host.categroy", s.Path);
            Assert.Equal(new[] { "category", "element_ids", "type_name" }, s.Allowed);
        }

        [Fact]
        public void A_typo_inside_a_termination_block_is_refused()
        {
            StructuralRequirementSet s = Load(OneRule.Replace("'orientation': 'left'", "'orientacion': 'left'"));
            Assert.False(s.Ok);
            Assert.Equal("reinforcement_rules['bottom'].start.orientacion", s.Path);
            Assert.Equal(new[] { "orientation" }, s.DidYouMean);
        }

        // --------------------------------------------------- deeply nested

        [Fact]
        public void A_typo_inside_a_zone_cannot_turn_that_zone_into_the_remainder()
        {
            // THE CASE THIS FILE EXISTS FOR. 'lenght_mm' used to read as "no
            // length", which is the remainder zone - so the set had two
            // remainders, or one zone silently swallowed the span.
            StructuralRequirementSet s = Load(OneZoneRule.Replace("'length_mm': 1500", "'lenght_mm': 1500"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("stirrup_zone_rules['B1'].zones[0].lenght_mm", s.Path);
            Assert.Equal(new[] { "length_mm" }, s.DidYouMean);
            Assert.Equal(new[] { "layout", "length_mm", "mark", "name" }, s.Allowed);
            Assert.Empty(s.StirrupZoneRules);
        }

        [Fact]
        public void A_typo_inside_a_zone_layout_is_refused_with_the_full_path()
        {
            StructuralRequirementSet s = Load(OneZoneRule.Replace("'spacing_mm': 200", "'spacing': 200"));
            Assert.False(s.Ok);
            Assert.Equal("stirrup_zone_rules['B1'].zones[1].layout.spacing", s.Path);
            Assert.Equal(new[] { "spacing_mm" }, s.DidYouMean);
        }

        [Fact]
        public void A_typo_on_a_zone_rule_offset_cannot_become_zero()
        {
            StructuralRequirementSet s = Load(OneZoneRule.Replace("'span_mm': 6000", "'span_mm': 6000, 'start_offset': 250"));
            Assert.False(s.Ok);
            Assert.Equal("stirrup_zone_rules[0].start_offset", s.Path);
            Assert.Contains("start_offset_mm", s.DidYouMean);
        }

        [Fact]
        public void A_typo_inside_a_mat_component_is_refused_with_the_component_path()
        {
            StructuralRequirementSet s = Load(OneMatRule.Replace("'offset_from_face_mm': 30", "'offset_from_face_mm': 30, 'end_cover': 40"));
            Assert.False(s.Ok);
            Assert.Equal("mat_rules['S1'].components[0].end_cover", s.Path);
            Assert.Equal(new[] { "end_cover_mm" }, s.DidYouMean);
            Assert.Empty(s.MatRules);
        }

        [Fact]
        public void A_typo_inside_a_mat_component_layout_is_refused()
        {
            StructuralRequirementSet s = Load(OneMatRule.Replace("'spacing_mm': 200", "'spacing_mm': 200, 'include_first': true"));
            Assert.False(s.Ok);
            Assert.Equal("mat_rules['S1'].components[0].layout.include_first", s.Path);
            Assert.Equal(new[] { "include_first_bar" }, s.DidYouMean);
        }

        // ---------------------------------------------------------- shape

        [Fact]
        public void An_invented_key_lists_the_admitted_keys_and_suggests_nothing()
        {
            StructuralRequirementSet s = Load(OneRule + @", 'colour': 'red'");
            Assert.False(s.Ok);
            Assert.Equal("colour", s.Path);
            Assert.Empty(s.DidYouMean);
            Assert.Equal(StructuralRequirementSet.RootKeys.OrderBy(x => x, System.StringComparer.Ordinal), s.Allowed);
            Assert.DoesNotContain("Did you mean", s.Error);
        }

        [Fact]
        public void The_refusal_detail_carries_path_allowed_and_suggestions()
        {
            StructuralRequirementSet s = Load(OneZoneRule.Replace("'length_mm': 1500", "'lenght_mm': 1500"));
            JObject d = StructuralRequirementSet.RefusalDetail(s);
            Assert.Equal("unknown_key", (string)d["code"]);
            Assert.Equal(StructuralRequirementSet.SchemaName, (string)d["schema"]);
            Assert.Equal("stirrup_zone_rules['B1'].zones[0].lenght_mm", (string)d["path"]);
            Assert.Equal("length_mm", (string)d["did_you_mean"][0]);
            Assert.Contains("layout", d["allowed"].Select(t => (string)t));
        }

        [Fact]
        public void A_refusal_that_is_not_about_a_key_still_names_its_field_path_when_it_has_one()
        {
            StructuralRequirementSet s = Load(OneRule.Replace("'orientation': 'left'", "'orientation': 'sideways'"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Equal("reinforcement_rules['bottom'].start", s.Path);
            Assert.Empty(s.Allowed);
            JObject d = StructuralRequirementSet.RefusalDetail(s);
            Assert.Null(d["allowed"]);
            Assert.Equal("reinforcement_rules['bottom'].start", (string)d["path"]);
        }

        [Fact]
        public void The_digest_does_not_care_that_keys_are_now_checked()
        {
            // Strictness is a loader property; the hash of a valid document is
            // unchanged by it, so provenance written before this build still matches.
            JObject doc = JObject.Parse((Head + OneRule + "}").Replace('\'', '"'));
            string before = StructuralRequirementSet.Sha256Of(doc);
            Assert.True(StructuralRequirementSet.Load(doc).Ok);
            Assert.Equal(before, StructuralRequirementSet.Sha256Of(doc));
        }
    }
}
