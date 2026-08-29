// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// The requirement set, pinned. Nearly every test here asserts a REFUSAL, and
// that is the point: the document decides what a drawing means, so a document
// nobody validated is a model nobody can defend.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadGlobTests
    {
        [Theory]
        [InlineData("A-WALL-EXTR", "A-WALL-*", true)]
        [InlineData("A-WALL", "A-WALL-*", false)]
        [InlineData("A-WALL", "A-WALL*", true)]
        [InlineData("A-WALL-DEMO", "*-DEMO", true)]
        [InlineData("A-WALL-EXTR", "*-DEMO", false)]
        [InlineData("S-GRID", "?-GRID", true)]
        [InlineData("SS-GRID", "?-GRID", false)]
        [InlineData("anything", "*", true)]
        [InlineData("anything", "**", true)]
        [InlineData("", "*", true)]
        [InlineData("A-WALL", "", false)]
        public void Globs_match_the_way_a_cad_standard_is_written(string text, string pattern, bool expected)
        {
            Assert.Equal(expected, CadGlob.IsMatch(text, pattern, caseSensitive: false));
        }

        [Fact]
        public void Case_sensitivity_is_the_callers_declaration()
        {
            Assert.True(CadGlob.IsMatch("a-wall", "A-WALL", caseSensitive: false));
            Assert.False(CadGlob.IsMatch("a-wall", "A-WALL", caseSensitive: true));
        }

        [Fact]
        public void A_regex_metacharacter_is_a_literal_not_a_wildcard()
        {
            // Globs, not regex: a '.' in a layer name is a '.', and a stray '('
            // is a character rather than a crash.
            Assert.False(CadGlob.IsMatch("AXWALL", "A.WALL", caseSensitive: false));
            Assert.True(CadGlob.IsMatch("A.WALL", "A.WALL", caseSensitive: false));
            Assert.True(CadGlob.IsMatch("A(WALL)", "A(WALL)", caseSensitive: false));
        }
    }

    public class CadRequirementSetTests
    {
        private static JObject Minimal(params string[] ruleOverrides)
        {
            var doc = JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'demo', 'version': '1.0.0', 'title': 'Demo' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [ {
                'id': 'walls',
                'precedence': 10,
                'layers': ['A-WALL*'],
                'produces': 'wall',
                'category': 'OST_Walls',
                'family_type': 'Basic Wall: Generic - 200mm',
                'height_mm': 3000,
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500,
                              'min_overlap_mm': 300, 'min_overlap_fraction': 0.5 }
              } ]
            }".Replace('\'', '"'));
            return doc;
        }

        [Fact]
        public void A_well_formed_set_loads_and_stamps_itself()
        {
            CadRequirementSet set = CadRequirementSet.Load(Minimal());
            Assert.Equal("demo", set.Id);
            Assert.Equal("1.0.0", set.Version);
            Assert.Equal(1.0, set.SourceUnitsToMm);
            Assert.Single(set.Rules);
            Assert.False(string.IsNullOrWhiteSpace(set.Sha256));
            JObject stamp = set.Stamp();
            Assert.Equal("demo", (string)stamp["id"]);
            Assert.Equal(set.Sha256, (string)stamp["sha256"]);
            Assert.Equal(1, (int)stamp["rule_count"]);
        }

        [Fact]
        public void The_hash_ignores_formatting_but_not_content()
        {
            JObject a = Minimal();
            JObject b = JObject.Parse(a.ToString(Newtonsoft.Json.Formatting.Indented));
            Assert.Equal(CadRequirementSet.Load(a).Sha256, CadRequirementSet.Load(b).Sha256);

            JObject changed = Minimal();
            ((JObject)changed["tolerances"])["gap_mm"] = 30.0;
            Assert.NotEqual(CadRequirementSet.Load(a).Sha256, CadRequirementSet.Load(changed).Sha256);
        }

        [Fact]
        public void Reordering_object_keys_does_not_change_the_hash()
        {
            JObject a = Minimal();
            var reordered = new JObject();
            foreach (JProperty p in a.Properties().Reverse()) reordered[p.Name] = p.Value;
            Assert.Equal(CadRequirementSet.Load(a).Sha256, CadRequirementSet.Load(reordered).Sha256);
        }

        [Fact]
        public void A_document_with_no_schema_name_is_refused()
        {
            JObject doc = Minimal();
            doc.Remove("schema");
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("schema must be", ex.Message);
        }

        [Fact]
        public void A_misspelt_top_level_section_is_refused_rather_than_ignored()
        {
            JObject doc = Minimal();
            doc["tolerence"] = new JObject();
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("tolerence", ex.Message);
            Assert.Contains("silently does not run", ex.Message);
        }

        [Fact]
        public void A_misspelt_rule_key_is_refused()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["heigth_mm"] = 3000;
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("heigth_mm", ex.Message);
        }

        [Fact]
        public void Undeclared_units_are_refused_with_the_reason_spelled_out()
        {
            JObject doc = Minimal();
            ((JObject)doc["source"])["units"] = "default";
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("not a unit this bridge can resolve", ex.Message);
        }

        [Fact]
        public void Missing_source_is_refused_because_nothing_will_guess_the_scale()
        {
            JObject doc = Minimal();
            doc.Remove("source");
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("either metres or a mistake", ex.Message);
        }

        [Theory]
        [InlineData("point_mm")]
        [InlineData("gap_mm")]
        [InlineData("angle_degrees")]
        [InlineData("arc_sagitta_mm")]
        public void Every_tolerance_is_required(string missing)
        {
            JObject doc = Minimal();
            ((JObject)doc["tolerances"]).Remove(missing);
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains(missing, ex.Message);
        }

        [Fact]
        public void A_gap_tolerance_below_the_point_tolerance_is_refused_as_meaningless()
        {
            JObject doc = Minimal();
            ((JObject)doc["tolerances"])["gap_mm"] = 0.5;
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("can never do anything", ex.Message);
        }

        [Fact]
        public void A_double_line_rule_without_thickness_bounds_is_refused()
        {
            JObject doc = Minimal();
            var geom = (JObject)doc["rules"][0]["geometry"];
            geom.Remove("min_thickness_mm");
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("any two parallel lines are a wall", ex.Message);
        }

        [Fact]
        public void An_inverted_thickness_range_is_refused()
        {
            JObject doc = Minimal();
            var geom = (JObject)doc["rules"][0]["geometry"];
            geom["min_thickness_mm"] = 500;
            geom["max_thickness_mm"] = 80;
            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
        }

        [Fact]
        public void An_unknown_produces_value_is_refused_and_the_message_lists_the_known_ones()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["produces"] = "wal";
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("wal", ex.Message);
            Assert.Contains("wall", ex.Message);
        }

        [Fact]
        public void A_rule_with_no_layers_is_refused()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["layers"] = new JArray();
            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
        }

        [Fact]
        public void Duplicate_rule_ids_are_refused_because_provenance_is_read_back_by_id()
        {
            JObject doc = Minimal();
            var clone = (JObject)doc["rules"][0].DeepClone();
            ((JArray)doc["rules"]).Add(clone);
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("appears twice", ex.Message);
        }

        [Fact]
        public void Two_rules_claiming_the_same_layers_at_the_same_precedence_are_refused()
        {
            JObject doc = Minimal();
            var clone = (JObject)doc["rules"][0].DeepClone();
            clone["id"] = "walls-again";
            clone["produces"] = "floor";
            clone["geometry"] = JObject.Parse("{\"from\":\"closed_loops\"}");
            ((JArray)doc["rules"]).Add(clone);
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("Nothing here can choose between them", ex.Message);
        }

        [Fact]
        public void The_same_layers_at_DIFFERENT_precedence_is_fine_because_one_of_them_wins()
        {
            JObject doc = Minimal();
            var clone = (JObject)doc["rules"][0].DeepClone();
            clone["id"] = "walls-fallback";
            clone["precedence"] = 1;
            clone["produces"] = "floor";
            clone["geometry"] = JObject.Parse("{\"from\":\"closed_loops\"}");
            ((JArray)doc["rules"]).Add(clone);
            CadRequirementSet set = CadRequirementSet.Load(doc);
            Assert.Equal(2, set.Rules.Count);
            Assert.Equal("walls", set.RulesFor("A-WALL-EXTR")[0].Id);   // precedence 10 beats 1
        }

        [Fact]
        public void Overwriting_a_humans_edit_has_to_be_said_out_loud()
        {
            JObject doc = Minimal();
            Assert.Equal(CadDivergencePolicy.Preserve,
                CadRequirementSet.Load(doc).Rules[0].OnManualDivergence);

            ((JObject)doc["rules"][0])["on_manual_divergence"] = "overwrite";
            Assert.Equal(CadDivergencePolicy.Overwrite,
                CadRequirementSet.Load(doc).Rules[0].OnManualDivergence);

            ((JObject)doc["rules"][0])["on_manual_divergence"] = "clobber";
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("has to be said out loud", ex.Message);
        }

        [Fact]
        public void Ambiguity_is_left_for_review_unless_the_set_says_otherwise()
        {
            Assert.Equal(CadAmbiguityPolicy.LeaveForReview,
                CadRequirementSet.Load(Minimal()).Rules[0].OnAmbiguous);
        }

        [Fact]
        public void Excludes_beat_includes()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["exclude_layers"] = new JArray("*-DEMO");
            CadRequirementSet set = CadRequirementSet.Load(doc);
            Assert.Single(set.RulesFor("A-WALL-EXTR"));
            Assert.Empty(set.RulesFor("A-WALL-DEMO"));
        }

        [Fact]
        public void A_point_cluster_rule_must_declare_what_the_same_symbol_means()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["produces"] = "furniture";
            ((JObject)doc["rules"][0])["geometry"] = JObject.Parse("{\"from\":\"point_clusters\"}");
            var ex = Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
            Assert.Contains("cluster_radius_mm is required", ex.Message);
        }

        [Fact]
        public void A_confidence_outside_zero_to_one_is_refused()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["min_confidence"] = 1.5;
            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
        }

        [Fact]
        public void An_empty_rule_list_is_refused_because_a_set_that_maps_nothing_is_not_a_mapping()
        {
            JObject doc = Minimal();
            doc["rules"] = new JArray();
            Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc));
        }

        [Fact]
        public void Parameters_are_read_as_typed_writes_the_writer_can_actually_apply()
        {
            // This test used to say the parameters "ride through untouched for
            // the writer to apply", and nothing applied them: the property was
            // parsed and read by no one, so a set could declare a fire rating on
            // every wall it produced and the walls came out blank, silently.
            // They are now typed writes that reach horizun_write_params_verified.
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["parameters"] =
                JObject.Parse("{\"Comments\":\"from CAD\",\"Mark\":7}");
            CadRequirementSet set = CadRequirementSet.Load(doc);

            Assert.Equal(2, set.Rules[0].Parameters.Count);
            CadParameterWrite comments = set.Rules[0].Parameters.Single(x => x.Parameter == "Comments");
            Assert.Equal("from CAD", (string)comments.Value);
            Assert.Equal("instance", comments.Scope);
            Assert.True(comments.Required, "a parameter is required unless the set says otherwise");
            Assert.Equal(7, (int)set.Rules[0].Parameters.Single(x => x.Parameter == "Mark").Value);
        }

        [Fact]
        public void A_parameter_can_declare_its_SCOPE_and_whether_it_is_required()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["parameters"] = JObject.Parse(
                "{\"Fire Rating\": {\"value\": \"2 h\", \"scope\": \"type\", \"required\": false}}");
            CadParameterWrite w = CadRequirementSet.Load(doc).Rules[0].Parameters.Single();

            Assert.Equal("type", w.Scope);
            Assert.False(w.Required);
        }

        [Fact]
        public void A_parameter_with_no_VALUE_is_refused_rather_than_written_as_nothing()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["parameters"] =
                JObject.Parse("{\"Comments\": {\"scope\": \"instance\"}}");
            Assert.Contains("declares no value",
                Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc)).Message);
        }

        [Fact]
        public void An_unknown_SCOPE_is_refused_and_says_what_a_type_write_costs()
        {
            JObject doc = Minimal();
            ((JObject)doc["rules"][0])["parameters"] =
                JObject.Parse("{\"Comments\": {\"value\": \"x\", \"scope\": \"family\"}}");
            Assert.Contains("changes every instance of that type",
                Assert.Throws<CadRequirementSetException>(() => CadRequirementSet.Load(doc)).Message);
        }
    }
}
