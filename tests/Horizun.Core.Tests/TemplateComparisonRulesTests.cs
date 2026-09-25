// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Model-vs-template diffing: extras, missing entries, and the case that
// matters most - two "Material" parameters that share a name but not a guid,
// which is not one parameter drifting, it is two parameters wearing the same
// label.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TemplateComparisonRulesTests
    {
        private static TemplateFact Fact(string key, bool keyIsGuid, string name, Dictionary<string, string> attrs = null) =>
            new TemplateFact { Category = "project_parameter", Key = key, KeyIsGuid = keyIsGuid, Name = name, Attributes = attrs ?? new Dictionary<string, string>() };

        [Fact]
        public void A_parameter_only_in_the_model_is_extra()
        {
            var model = new[] { Fact("guid-a", true, "HRZ_COD_PRES") };
            var template = new TemplateFact[0];
            TemplateCategoryResult r = TemplateComparisonRules.Compare("project_parameter", model, template);
            Assert.Single(r.Extra);
            Assert.Empty(r.Missing);
            Assert.Empty(r.Conflicts);
        }

        [Fact]
        public void A_parameter_only_in_the_template_is_missing()
        {
            var model = new TemplateFact[0];
            var template = new[] { Fact("guid-a", true, "HRZ_COD_PRES") };
            TemplateCategoryResult r = TemplateComparisonRules.Compare("project_parameter", model, template);
            Assert.Empty(r.Extra);
            Assert.Single(r.Missing);
        }

        [Fact]
        public void Same_guid_same_attributes_matches_cleanly()
        {
            var attrs = new Dictionary<string, string> { ["data_type"] = "Text", ["binding"] = "instance" };
            var model = new[] { Fact("guid-a", true, "HRZ_COD_PRES", attrs) };
            var template = new[] { Fact("guid-a", true, "HRZ_COD_PRES", new Dictionary<string, string>(attrs)) };
            TemplateCategoryResult r = TemplateComparisonRules.Compare("project_parameter", model, template);
            Assert.Empty(r.Extra);
            Assert.Empty(r.Missing);
            Assert.Empty(r.Conflicts);
        }

        [Fact]
        public void Two_material_parameters_sharing_a_name_but_not_a_guid_are_extra_and_missing_not_matched()
        {
            // THE EXACT SHAPE OF THE FIELD FINDING: "Material" in the model has a
            // different guid than "Material" in the SPF. They are not the same
            // parameter that drifted - keying by name would report them matched.
            var model = new[] { Fact("guid-model", true, "Material") };
            var template = new[] { Fact("guid-spf", true, "Material") };
            TemplateCategoryResult r = TemplateComparisonRules.Compare("project_parameter", model, template);
            Assert.Single(r.Extra);
            Assert.Single(r.Missing);
            Assert.Empty(r.Conflicts);
            Assert.Equal("guid-model", r.Extra[0].Key);
            Assert.Equal("guid-spf", r.Missing[0].Key);
        }

        [Fact]
        public void Same_key_different_data_type_is_a_conflict_naming_the_differing_attribute()
        {
            var model = new[] { Fact("guid-a", true, "HRZ_COD_PRES", new Dictionary<string, string> { ["data_type"] = "Text" }) };
            var template = new[] { Fact("guid-a", true, "HRZ_COD_PRES", new Dictionary<string, string> { ["data_type"] = "Integer" }) };
            TemplateCategoryResult r = TemplateComparisonRules.Compare("project_parameter", model, template);
            Assert.Empty(r.Extra);
            Assert.Empty(r.Missing);
            TemplateConflict c = Assert.Single(r.Conflicts);
            Assert.Contains("data_type", c.DifferingAttributes);
            Assert.Equal("Text", c.ModelValues["data_type"]);
            Assert.Equal("Integer", c.TemplateValues["data_type"]);
        }

        [Fact]
        public void An_attribute_present_only_on_one_side_is_not_a_conflict()
        {
            var model = new[] { Fact("guid-a", true, "X", new Dictionary<string, string> { ["data_type"] = "Text", ["binding"] = "instance" }) };
            var template = new[] { Fact("guid-a", true, "X", new Dictionary<string, string> { ["data_type"] = "Text" }) };
            TemplateCategoryResult r = TemplateComparisonRules.Compare("project_parameter", model, template);
            Assert.Empty(r.Conflicts);
        }

        [Fact]
        public void Name_only_keys_match_by_name_when_no_guid_exists()
        {
            var model = new[] { Fact("Fire Rating", false, "Fire Rating") };
            var template = new[] { Fact("Fire Rating", false, "Fire Rating") };
            TemplateCategoryResult r = TemplateComparisonRules.Compare("view_filter", model, template);
            Assert.Empty(r.Extra);
            Assert.Empty(r.Missing);
        }

        [Fact]
        public void Counts_are_reported_independently_of_extras_and_missing()
        {
            var model = new[] { Fact("a", true, "A"), Fact("b", true, "B") };
            var template = new[] { Fact("a", true, "A"), Fact("c", true, "C") };
            TemplateCategoryResult r = TemplateComparisonRules.Compare("project_parameter", model, template);
            Assert.Equal(2, r.ModelCount);
            Assert.Equal(2, r.TemplateCount);
            Assert.Single(r.Extra);
            Assert.Single(r.Missing);
        }
    }
}
