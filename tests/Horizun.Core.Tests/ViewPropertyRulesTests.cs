// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// set_view_properties (horizun_manage_views): the Revit-free rules. Each block pins a way the
// operation could lie: a template that governs a property swallows the write in Revit, so the
// answer must name that template and must never unbind it; a word outside the closed lists must
// be refused instead of mapped to a default; an action that names no property, or two ways to
// name the views, is a mistake and not a no-op.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ViewPropertyRulesTests
    {
        [Fact]
        public void Requested_lists_only_the_fields_the_action_carries_in_write_order()
        {
            var present = new HashSet<string> { "phase_id", "view_scale", "other" };
            Assert.Equal(new[] { "view_scale", "phase_id" }, ViewPropertyRules.Requested(present.Contains));
            Assert.Empty(ViewPropertyRules.Requested(_ => false));
        }

        [Theory]
        [InlineData("coarse", "Coarse")] [InlineData("Medium", "Medium")] [InlineData(" FINE ", "Fine")]
        public void Detail_level_words_map_to_the_enum_names(string word, string name)
        {
            Assert.Equal(name, ViewPropertyRules.ParseDetailLevel(word, out string error));
            Assert.Null(error);
        }

        [Theory]
        [InlineData("high")] [InlineData("")] [InlineData(null)]
        public void An_unknown_detail_level_is_refused_not_defaulted(string word)
        {
            Assert.Null(ViewPropertyRules.ParseDetailLevel(word, out string error));
            Assert.Contains("coarse, medium or fine", error);
        }

        [Theory]
        [InlineData("architectural", "Architectural")] [InlineData("PLUMBING", "Plumbing")] [InlineData("coordination", "Coordination")]
        public void Discipline_words_map_to_the_enum_names(string word, string name)
        {
            Assert.Equal(name, ViewPropertyRules.ParseDiscipline(word, out string error));
            Assert.Null(error);
        }

        [Fact]
        public void An_unknown_discipline_is_refused_with_the_list()
        {
            Assert.Null(ViewPropertyRules.ParseDiscipline("civil", out string error));
            Assert.Contains("coordination", error);
        }

        [Fact]
        public void A_template_that_controls_a_requested_property_is_named_and_not_detached()
        {
            var controlled = new HashSet<string> { "VIEW_DETAIL_LEVEL", "VIEW_PHASE" };
            string refusal = ViewPropertyRules.TemplateRefusal(new[] { "view_scale", "detail_level" }, controlled, "Plan 1:100", 4242);
            Assert.NotNull(refusal);
            Assert.Contains("'Plan 1:100'", refusal);
            Assert.Contains("controls detail_level:", refusal);   // only the governed property is blamed, not the scale
            Assert.Contains("was not detached for you", refusal);
            Assert.Contains("view_id=4242", refusal);
        }

        [Fact]
        public void The_scale_is_governed_by_any_of_its_three_parameters()
        {
            foreach (string p in new[] { "VIEW_SCALE", "VIEW_SCALE_PULLDOWN_METRIC", "VIEW_SCALE_PULLDOWN_IMPERIAL" })
                Assert.NotNull(ViewPropertyRules.TemplateRefusal(new[] { "view_scale" }, new HashSet<string> { p }, "T", 1));
        }

        [Fact]
        public void No_controlled_parameters_or_no_overlap_means_no_refusal()
        {
            Assert.Null(ViewPropertyRules.TemplateRefusal(new[] { "discipline" }, new HashSet<string>(), "T", 1));
            Assert.Null(ViewPropertyRules.TemplateRefusal(new[] { "discipline" }, new HashSet<string> { "VIEW_DETAIL_LEVEL" }, "T", 1));
            Assert.Null(ViewPropertyRules.TemplateRefusal(null, new HashSet<string> { "VIEW_PHASE" }, "T", 1));
        }

        [Fact]
        public void Each_field_names_the_template_parameter_that_governs_it()
        {
            Assert.Equal(new[] { "VIEW_DETAIL_LEVEL" }, ViewPropertyRules.TemplateParameters("detail_level"));
            Assert.Equal(new[] { "VIEW_DISCIPLINE" }, ViewPropertyRules.TemplateParameters("discipline"));
            Assert.Equal(new[] { "MODEL_GRAPHICS_STYLE" }, ViewPropertyRules.TemplateParameters("display_style"));
            Assert.Equal(new[] { "VIEW_PHASE" }, ViewPropertyRules.TemplateParameters("phase_id"));
            Assert.Empty(ViewPropertyRules.TemplateParameters("name"));
        }

        [Fact]
        public void Sheets_and_schedules_have_none_of_these_properties_and_a_perspective_has_no_scale()
        {
            Assert.Contains("ViewSheet", ViewPropertyRules.KindRefusal("ViewSheet", true, false, new[] { "view_scale" }));
            Assert.NotNull(ViewPropertyRules.KindRefusal("View3D", false, true, new[] { "view_scale" }));
            Assert.Null(ViewPropertyRules.KindRefusal("View3D", false, true, new[] { "detail_level" }));
            Assert.Null(ViewPropertyRules.KindRefusal("ViewPlan", false, false, new[] { "view_scale" }));
        }

        [Fact]
        public void One_way_of_naming_the_views_and_a_sane_count()
        {
            Assert.Null(ViewPropertyRules.IdsError(true, false, null));
            Assert.Null(ViewPropertyRules.IdsError(false, true, null));
            Assert.Null(ViewPropertyRules.IdsError(false, false, new List<long> { 1, 2, 3 }));
            Assert.Contains("needs view_id", ViewPropertyRules.IdsError(false, false, null));
            Assert.Contains("not both", ViewPropertyRules.IdsError(true, false, new List<long> { 1 }));
            Assert.Contains("repeats", ViewPropertyRules.IdsError(false, false, new List<long> { 1, 1 }));
            Assert.Contains("1..200", ViewPropertyRules.IdsError(false, false, new List<long>()));
            var many = new List<long>(); for (long i = 0; i < 201; i++) many.Add(i);
            Assert.Contains("1..200", ViewPropertyRules.IdsError(false, false, many));
        }

        [Fact]
        public void An_action_with_no_property_says_it_would_change_nothing()
            => Assert.Contains("changes nothing", ViewPropertyRules.NothingRequested());
    }
}
