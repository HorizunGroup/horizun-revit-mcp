using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// horizun_view_visibility datum_cleanup and set_scale (4.2-N3, CT-P1-3b and CT-P1-3d): the argument
    /// rules and the decisions that need no Revit.
    /// </summary>
    public sealed class ViewDatumRulesTests
    {
        private static JObject Cleanup(string extra = null)
        {
            var r = new JObject { ["operation"] = "datum_cleanup", ["view_id"] = 10 };
            if (extra != null) foreach (var p in JObject.Parse(extra).Properties()) r[p.Name] = p.Value;
            return r;
        }

        private static JObject Scale(string extra = null)
        {
            var r = new JObject { ["operation"] = "set_scale", ["view_id"] = 10 };
            if (extra != null) foreach (var p in JObject.Parse(extra).Properties()) r[p.Name] = p.Value;
            return r;
        }

        // ---- argument rules ------------------------------------------------------------------

        [Fact]
        public void Both_operations_are_listed()
        {
            Assert.Contains("datum_cleanup", ViewVisibilityRules.Operations);
            Assert.Contains("set_scale", ViewVisibilityRules.Operations);
        }

        [Fact]
        public void Datum_cleanup_defaults_validate()
        {
            Assert.Null(ViewVisibilityRules.Validate(Cleanup()));
            Assert.Null(ViewVisibilityRules.Validate(Cleanup("{\"datum_kinds\":[\"grid\"],\"criteria\":[\"outside_crop\",\"foreign\"],\"keep_ids\":[5,6]}")));
        }

        [Fact]
        public void Datum_cleanup_needs_view_id()
        {
            var r = Cleanup(); r.Remove("view_id");
            Assert.Contains("view_id", ViewVisibilityRules.Validate(r));
        }

        [Theory]
        [InlineData("{\"datum_kinds\":[]}")]
        [InlineData("{\"datum_kinds\":[\"wall\"]}")]
        [InlineData("{\"criteria\":[\"far\"]}")]
        [InlineData("{\"criteria\":\"foreign\"}")]
        [InlineData("{\"keep_ids\":[\"a\"]}")]
        [InlineData("{\"element_ids\":[1]}")]
        [InlineData("{\"categories\":[\"OST_Grids\"]}")]
        [InlineData("{\"scale\":50}")]
        public void Datum_cleanup_refuses_what_does_not_belong(string extra) =>
            Assert.NotNull(ViewVisibilityRules.Validate(Cleanup(extra)));

        [Fact]
        public void Set_scale_validates_and_refuses()
        {
            Assert.Null(ViewVisibilityRules.Validate(Scale("{\"scale\":100}")));
            Assert.Null(ViewVisibilityRules.Validate(Scale("{\"scale\":50,\"rehide_foreign_markers\":false}")));
            Assert.Contains("scale", ViewVisibilityRules.Validate(Scale()));
            Assert.Contains("1..24000", ViewVisibilityRules.Validate(Scale("{\"scale\":0}")));
            Assert.Contains("1..24000", ViewVisibilityRules.Validate(Scale("{\"scale\":24001}")));
            Assert.NotNull(ViewVisibilityRules.Validate(Scale("{\"scale\":\"50\"}")));
            Assert.Contains("boolean", ViewVisibilityRules.Validate(Scale("{\"scale\":50,\"rehide_foreign_markers\":\"yes\"}")));
            Assert.NotNull(ViewVisibilityRules.Validate(Scale("{\"scale\":50,\"keep_ids\":[1]}")));
            Assert.NotNull(ViewVisibilityRules.Validate(Scale("{\"scale\":50,\"criteria\":[\"foreign\"]}")));
        }

        [Fact]
        public void Other_operations_are_untouched()
        {
            Assert.Null(ViewVisibilityRules.Validate(new JObject { ["operation"] = "hide_foreign_markers", ["view_id"] = 3 }));
            Assert.NotNull(ViewVisibilityRules.Validate(new JObject { ["operation"] = "explode", ["view_id"] = 3 }));
        }

        // ---- parsing ---------------------------------------------------------------------------

        [Fact]
        public void Kinds_default_to_both_and_deduplicate()
        {
            string e;
            Assert.Equal(new[] { "grid", "level" }, ViewDatumRules.ParseKinds(null, out e));
            Assert.Equal(new[] { "level" }, ViewDatumRules.ParseKinds(JArray.Parse("[\"Level\",\" level \"]"), out e));
            Assert.Null(ViewDatumRules.ParseKinds(JArray.Parse("[\"grid\",\"x\"]"), out e));
            Assert.Contains("'x'", e);
        }

        [Fact]
        public void Criteria_default_to_outside_crop()
        {
            string e;
            Assert.Equal(new[] { "outside_crop" }, ViewDatumRules.ParseCriteria(null, out e));
            Assert.Equal(new[] { "foreign", "outside_crop" }, ViewDatumRules.ParseCriteria(JArray.Parse("[\"foreign\",\"outside_crop\"]"), out e));
        }

        // ---- geometry: does the datum touch the crop? --------------------------------------------

        [Theory]
        [InlineData(-5, 5, 15, 5, true)]     // crosses the rectangle (0..10) clean through
        [InlineData(2, 2, 8, 8, true)]       // fully inside
        [InlineData(-5, -5, 15, -5, false)]  // parallel, below it
        [InlineData(11, 0, 11, 10, false)]   // vertical, to the right
        [InlineData(10, -3, 10, 4, true)]    // ends touching the border: still drawn
        [InlineData(-5, 15, 15, -5, true)]   // diagonal through the corner area
        [InlineData(-5, 11, 4, 20, false)]   // diagonal that misses the corner
        public void Segment_against_rectangle(double ax, double ay, double bx, double by, bool expected) =>
            Assert.Equal(expected, ViewDatumRules.SegmentTouchesRect(ax, ay, bx, by, 0, 0, 10, 10));

        // ---- the verdict -------------------------------------------------------------------------

        private static readonly string[] Crop = { "outside_crop" };
        private static readonly string[] Foreign = { "foreign" };
        private static readonly string[] Both = { "outside_crop", "foreign" };

        [Fact]
        public void Outside_the_crop_is_hidden()
        {
            var v = ViewDatumRules.Decide(Crop, false, true, true, false);
            Assert.True(v.Hide);
            Assert.Equal(new[] { "outside_crop" }, v.Because);
        }

        [Fact]
        public void Inside_the_crop_stays()
        {
            var v = ViewDatumRules.Decide(Crop, false, true, true, true);
            Assert.False(v.Hide);
            Assert.Equal("meets no requested criterion", v.Reason);
        }

        [Fact]
        public void Unreadable_extent_is_kept_and_says_so_when_it_is_the_only_reason()
        {
            var v = ViewDatumRules.Decide(Crop, false, true, true, null);
            Assert.False(v.Hide);
            Assert.Contains("could not be read", v.Reason);
        }

        [Fact]
        public void Foreign_is_hidden_even_when_the_extent_is_unreadable()
        {
            var v = ViewDatumRules.Decide(Both, false, true, false, null);
            Assert.True(v.Hide);
            Assert.Equal(new[] { "foreign" }, v.Because);
        }

        [Fact]
        public void A_datum_meeting_two_criteria_names_both()
        {
            var v = ViewDatumRules.Decide(Both, false, true, false, false);
            Assert.True(v.Hide);
            Assert.Equal(new[] { "foreign", "outside_crop" }, v.Because.OrderBy(x => x));
        }

        [Fact]
        public void Own_datum_is_not_foreign()
        {
            Assert.False(ViewDatumRules.Decide(Foreign, false, true, true, null).Hide);
        }

        [Fact]
        public void The_keep_list_wins_over_every_criterion()
        {
            var v = ViewDatumRules.Decide(Both, true, true, false, false);
            Assert.False(v.Hide);
            Assert.Equal("in keep_ids", v.Reason);
        }

        [Fact]
        public void A_datum_the_view_cannot_hide_is_kept_with_the_reason()
        {
            var v = ViewDatumRules.Decide(Crop, false, false, true, false);
            Assert.False(v.Hide);
            Assert.Empty(v.Because);
            Assert.Equal("cannot be hidden in this view", v.Reason);
        }

        // ---- scale ----------------------------------------------------------------------------------

        [Theory]
        [InlineData(null, false)]
        [InlineData(0L, false)]
        [InlineData(1L, true)]
        [InlineData(24000L, true)]
        [InlineData(24001L, false)]
        public void Scale_error(long? scale, bool ok) => Assert.Equal(ok, ViewDatumRules.ScaleError(scale) == null);
    }
}
