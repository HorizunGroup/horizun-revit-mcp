using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// horizun_view_visibility copy_link_overrides: the argument rules and the comparisons
    /// the re-read uses (field request: turn off only a link's Grids and Levels per view).
    /// </summary>
    public sealed class CopyLinkOverridesRulesTests
    {
        private static JObject Req(object viewId, params long[] targets)
        {
            var r = new JObject { ["operation"] = "copy_link_overrides" };
            if (viewId != null) r["view_id"] = JToken.FromObject(viewId);
            if (targets.Length > 0) r["view_ids"] = new JArray(targets);
            return r;
        }

        [Fact]
        public void A_source_and_own_targets_validate()
        {
            Assert.Null(ViewVisibilityRules.Validate(Req(10L, 20, 30)));
            JObject withCats = Req(10L, 20);
            withCats["categories"] = new JArray("OST_Grids", "OST_Levels");   // evidence categories
            Assert.Null(ViewVisibilityRules.Validate(withCats));
        }

        [Fact]
        public void Missing_source_missing_targets_or_the_source_among_them_refuse()
        {
            Assert.Contains("needs view_id", ViewVisibilityRules.Validate(Req(null, 20)));
            Assert.Contains("needs view_ids", ViewVisibilityRules.Validate(Req(10L)));
            Assert.Contains("must not contain the source", ViewVisibilityRules.Validate(Req(10L, 20, 10)));
        }

        [Fact]
        public void Arguments_of_other_operations_refuse_and_the_target_count_is_bounded()
        {
            JObject r = Req(10L, 20);
            r["element_ids"] = new JArray(1);
            Assert.Contains("element_ids does not apply", ViewVisibilityRules.Validate(r));
            var many = new long[ViewVisibilityRules.MaxLinkTargets + 1];
            for (int i = 0; i < many.Length; i++) many[i] = 100 + i;
            Assert.Contains("at most", ViewVisibilityRules.Validate(Req(10L, many)));
        }

        [Fact]
        public void Link_settings_compare_key_by_key()
        {
            var source = new JObject { ["visibility_type"] = "Custom", ["linked_view_id"] = -1 };
            Assert.Empty(ViewVisibilityRules.LinkSettingsDiffer(source, (JObject)source.DeepClone()));
            var target = new JObject { ["visibility_type"] = "ByHostView", ["linked_view_id"] = -1 };
            Assert.Equal(new[] { "visibility_type" }, ViewVisibilityRules.LinkSettingsDiffer(source, target));
            // A key read on one side only is a difference, never assumed equal.
            Assert.Equal(new[] { "object_styles" },
                ViewVisibilityRules.LinkSettingsDiffer(new JObject { ["object_styles"] = "Custom" }, new JObject()));
        }

        [Fact]
        public void Category_evidence_names_only_presence_differences()
        {
            var source = new JObject { ["OST_Walls"] = 40, ["OST_Grids"] = 0 };
            var sameShape = new JObject { ["OST_Walls"] = 12, ["OST_Levels"] = 0 };
            Assert.Empty(ViewVisibilityRules.CategoryPresenceDiffers(source, sameShape));

            var stillShowsGrids = new JObject { ["OST_Walls"] = 12, ["OST_Grids"] = 6 };
            JArray d = ViewVisibilityRules.CategoryPresenceDiffers(source, stillShowsGrids);
            Assert.Single(d);
            Assert.Equal("OST_Grids", (string)d[0]["category"]);
            Assert.Equal(0, (long)d[0]["visible_in_source"]);
            Assert.Equal(6, (long)d[0]["visible_in_target"]);
        }
    }
}
