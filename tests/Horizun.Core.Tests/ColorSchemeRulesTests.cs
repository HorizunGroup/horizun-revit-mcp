// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The arithmetic behind the colour fill scheme operations of horizun_manage_styles.
// A scheme that colours nothing (no entries, no fill pattern), two colours for one
// value, a colour that is not a colour: each is refused here, before a transaction.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ColorSchemeRulesTests
    {
        [Fact]
        public void A_numeric_scheme_needs_value_units_and_converts_once_to_internal()
        {
            // Review R3 (5.2): 20 meant as m2 was written as 20 ft2 and verified raw against raw.
            string error;
            var entries = ColorSchemeRules.ParseEntries(JArray.Parse("[{\"value\":20,\"color\":\"#FF0000\"}]"), out error);
            Assert.Contains("value_units", ColorSchemeRules.NumericToInternal(entries, null));
            Assert.Null(ColorSchemeRules.NumericToInternal(entries, "m2"));
            Assert.Equal(20 / 0.09290304, entries[0].Value.Value<double>(), 6);
            Assert.Null(ColorSchemeRules.NumericToInternal(entries, "m2"));   // a second pass (rehearsal then commit) does not convert again
            Assert.Equal(20 / 0.09290304, entries[0].Value.Value<double>(), 6);
            var mm = ColorSchemeRules.ParseEntries(JArray.Parse("[{\"value\":3048,\"color\":\"#00FF00\"}]"), out error);
            Assert.Null(ColorSchemeRules.NumericToInternal(mm, "mm"));
            Assert.Equal(10.0, mm[0].Value.Value<double>(), 9);
            Assert.Contains("value_units must be", ColorSchemeRules.NumericToInternal(ColorSchemeRules.ParseEntries(JArray.Parse("[{\"value\":1,\"color\":\"#00FF00\"}]"), out error), "cubits"));
        }

        private static List<ColorEntrySpec> Parse(string json, out string error)
            => ColorSchemeRules.ParseEntries(JToken.Parse(json), out error);

        [Fact]
        public void Entries_are_parsed_with_their_colour_in_canonical_form()
        {
            string error;
            var entries = Parse(@"[ { ""value"": ""Office"", ""color"": ""#00a0ff"" },
                                  { ""value"": 12, ""color"": ""FF0000"", ""visible"": false, ""caption"": ""12 and up"", ""pattern"": ""Solid fill"" } ]", out error);
            Assert.Null(error);
            Assert.Equal(2, entries.Count);
            Assert.Equal("#00A0FF", entries[0].Color);
            Assert.Equal("s:Office", entries[0].Key);
            Assert.Equal("n:12", entries[1].Key);
            Assert.False(entries[1].Visible.Value);
            Assert.Equal("12 and up", entries[1].Caption);
            Assert.Equal("Solid fill", entries[1].Pattern.Value<string>());
        }

        [Fact]
        public void One_value_written_two_ways_is_still_one_value()
        {
            Assert.Equal(ColorSchemeRules.ValueKey(new JValue(12)), ColorSchemeRules.ValueKey(new JValue(12.0)));
            string error;
            Assert.Null(Parse(@"[ { ""value"": 12, ""color"": ""#111111"" }, { ""value"": 12.0, ""color"": ""#222222"" } ]", out error));
            Assert.Contains("repeats", error);
        }

        [Theory]
        [InlineData(@"{ ""value"": 1 }", "entries")]
        [InlineData(@"[]", "empty")]
        [InlineData(@"[ 3 ]", "must be an object")]
        [InlineData(@"[ { ""color"": ""#111111"" } ]", "value is required")]
        [InlineData(@"[ { ""value"": """", ""color"": ""#111111"" } ]", "value is required")]
        [InlineData(@"[ { ""value"": ""A"" } ]", "#RRGGBB")]
        [InlineData(@"[ { ""value"": ""A"", ""color"": ""blue"" } ]", "#RRGGBB")]
        [InlineData(@"[ { ""value"": ""A"", ""color"": ""#111111"", ""caption"": ""  "" } ]", "caption")]
        [InlineData(@"[ { ""value"": ""A"", ""color"": ""#111111"", ""visible"": ""no"" } ]", "visible")]
        [InlineData(@"[ { ""value"": ""A"", ""color"": ""#111111"", ""pattern"": true } ]", "pattern")]
        [InlineData(@"[ { ""value"": ""A"", ""color"": ""#111111"", ""opacity"": 5 } ]", "not an entry attribute")]
        public void Entries_that_could_not_be_written_are_refused_naming_the_row(string json, string expected)
        {
            string error;
            Assert.Null(Parse(json, out error));
            Assert.Contains(expected, error);
        }

        [Fact]
        public void More_entries_than_the_cap_are_refused()
        {
            var array = new JArray(Enumerable.Range(0, ColorSchemeRules.MaxEntries + 1)
                .Select(i => new JObject { ["value"] = "v" + i, ["color"] = "#000000" }));
            string error;
            Assert.Null(ColorSchemeRules.ParseEntries(array, out error));
            Assert.Contains("limit", error);
        }

        // ---- scheme-level settings -----------------------------------------------------------

        [Fact]
        public void An_edit_that_changes_nothing_is_refused_but_a_create_may_only_copy()
        {
            List<ColorEntrySpec> entries;
            Assert.Contains("changes nothing", ColorSchemeRules.ValidateSettings(new JObject(), false, out entries));
            Assert.Null(ColorSchemeRules.ValidateSettings(new JObject(), true, out entries));
        }

        [Theory]
        [InlineData(@"{ ""by_range"": ""yes"" }", "by_range")]
        [InlineData(@"{ ""include_links"": 1 }", "include_links")]
        [InlineData(@"{ ""title"": "" "" }", "title")]
        [InlineData(@"{ ""parameter"": """" }", "parameter")]
        [InlineData(@"{ ""by_range"": true, ""entries"": [ { ""value"": ""A"", ""color"": ""#111111"" } ] }", "numeric")]
        public void Settings_of_the_wrong_shape_are_refused(string json, string expected)
        {
            List<ColorEntrySpec> entries;
            string error = ColorSchemeRules.ValidateSettings(JObject.Parse(json), false, out entries);
            Assert.NotNull(error);
            Assert.Contains(expected, error);
        }

        [Fact]
        public void A_range_scheme_takes_numeric_starts()
        {
            List<ColorEntrySpec> entries;
            Assert.Null(ColorSchemeRules.ValidateSettings(JObject.Parse(
                @"{ ""by_range"": true, ""entries"": [ { ""value"": 10, ""color"": ""#111111"" }, { ""value"": 20.5, ""color"": ""#222222"", ""caption"": ""20.5 +"" } ] }"),
                false, out entries));
            Assert.Equal(2, entries.Count);
        }

        // ---- what colours what ------------------------------------------------------------------

        [Theory]
        [InlineData("rooms", "OST_Rooms")]
        [InlineData("Habitaciones", "OST_Rooms")]
        [InlineData("areas", "OST_Areas")]
        [InlineData("spaces", "OST_MEPSpaces")]
        [InlineData("ESPACIOS", "OST_MEPSpaces")]
        [InlineData("OST_Walls", null)]
        [InlineData("", null)]
        public void Everyday_words_map_to_the_built_in_category(string text, string expected)
        {
            Assert.Equal(expected, ColorSchemeRules.CategoryAlias(text));
        }

        [Fact]
        public void Apply_needs_a_view_and_a_scheme_or_the_category_to_clear()
        {
            Assert.Contains("view_id", ColorSchemeRules.ValidateApply(JObject.Parse(@"{ ""scheme_id"": 5 }")));
            Assert.Contains("REMOVE", ColorSchemeRules.ValidateApply(JObject.Parse(@"{ ""view_id"": 7 }")));
            Assert.Null(ColorSchemeRules.ValidateApply(JObject.Parse(@"{ ""view_id"": 7, ""scheme_id"": 5 }")));
            Assert.Null(ColorSchemeRules.ValidateApply(JObject.Parse(@"{ ""view_id"": 7, ""category"": ""rooms"" }")));
        }

        [Fact]
        public void A_legend_needs_a_view_a_scheme_or_category_and_a_point()
        {
            Assert.Contains("view_id", ColorSchemeRules.ValidateLegend(JObject.Parse(@"{ ""scheme_id"": 5, ""point"": [0, 0] }")));
            Assert.Contains("scheme_id or category", ColorSchemeRules.ValidateLegend(JObject.Parse(@"{ ""view_id"": 7, ""point"": [0, 0] }")));
            Assert.Contains("point", ColorSchemeRules.ValidateLegend(JObject.Parse(@"{ ""view_id"": 7, ""scheme_id"": 5 }")));
            Assert.Contains("point", ColorSchemeRules.ValidateLegend(JObject.Parse(@"{ ""view_id"": 7, ""scheme_id"": 5, ""point"": [1] }")));
            Assert.Contains("point", ColorSchemeRules.ValidateLegend(JObject.Parse(@"{ ""view_id"": 7, ""scheme_id"": 5, ""point"": [1, ""x""] }")));
            Assert.Null(ColorSchemeRules.ValidateLegend(JObject.Parse(@"{ ""view_id"": 7, ""scheme_id"": 5, ""point"": [100, 200] }")));
        }

        [Fact]
        public void The_write_operations_are_the_four_that_change_the_model()
        {
            Assert.Equal(new[] { "create_color_scheme", "edit_color_scheme", "apply_color_scheme", "place_color_legend" },
                         ColorSchemeRules.WriteOperations.ToArray());
            Assert.DoesNotContain(ColorSchemeRules.OpList, ColorSchemeRules.WriteOperations);
        }
    }
}
