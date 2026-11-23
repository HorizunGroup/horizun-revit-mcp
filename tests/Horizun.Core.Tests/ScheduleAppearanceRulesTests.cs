// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The arithmetic behind horizun_manage_schedules set_appearance / split / unsplit.
// A style written without its override flag is accepted by Revit and ignored, so
// the checklist of attributes a request asks for is what protects the caller; the
// header layout decides which row is the title; and conditional formatting, which
// Revit has no API for, must be refused with the reason and the alternatives.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleAppearanceRulesTests
    {
        [Fact]
        public void Split_heights_read_back_as_header_plus_body_are_a_match()
        {
            // Review R3 (5.2): GetSegmentHeight = header + body, Split took the body; a raw compare always failed.
            Assert.True(ScheduleAppearanceRules.SegmentHeightsMatch(new List<double> { 112, 92 }, new List<double> { 100, 80 }, 0.5, out double header, out string why), why);
            Assert.Equal(12, header, 3);
            Assert.False(ScheduleAppearanceRules.SegmentHeightsMatch(new List<double> { 112, 99 }, new List<double> { 100, 80 }, 0.5, out _, out why));
            Assert.Contains("segment 1", why);
            Assert.False(ScheduleAppearanceRules.SegmentHeightsMatch(new List<double> { 90 }, new List<double> { 100 }, 0.5, out _, out why));
        }

        private static JObject Action(string json) => JObject.Parse(json);

        // ---- the style object ----------------------------------------------------------

        [Fact]
        public void A_style_is_parsed_once_into_the_attributes_that_were_asked_for()
        {
            string error;
            StyleSpec spec = ScheduleAppearanceRules.ParseStyle(JObject.Parse(
                @"{ ""font"": ""Arial"", ""size_pt"": 11, ""bold"": true, ""color"": ""#1f2a44"", ""fill"": ""eeeeee"", ""h_align"": ""centre"", ""v_align"": ""middle"",
                    ""border"": { ""bottom"": ""Thin Lines"", ""top"": ""none"" } }"), out error);
            Assert.Null(error);
            Assert.Equal("Arial", spec.Font);
            Assert.Equal(11.0, spec.SizePt.Value);
            Assert.Equal("#1F2A44", spec.Color);
            Assert.Equal("#EEEEEE", spec.Fill);
            Assert.Equal("center", spec.HAlign);
            Assert.Equal(new[] { "font", "size_pt", "bold", "color", "fill", "h_align", "v_align", "border_top", "border_bottom" },
                         spec.Attributes());
            Assert.Null(spec.Italic);
        }

        [Fact]
        public void An_empty_style_is_refused_because_it_would_change_nothing()
        {
            string error;
            Assert.Null(ScheduleAppearanceRules.ParseStyle(new JObject(), out error));
            Assert.Contains("changes nothing", error);
        }

        [Theory]
        [InlineData(@"{ ""colour"": ""#FFFFFF"" }", "style.colour")]
        [InlineData(@"{ ""color"": ""red"" }", "#RRGGBB")]
        [InlineData(@"{ ""fill"": ""#12345"" }", "#RRGGBB")]
        [InlineData(@"{ ""size_pt"": 0 }", "size_pt")]
        [InlineData(@"{ ""size_pt"": 500 }", "size_pt")]
        [InlineData(@"{ ""bold"": ""yes"" }", "true or false")]
        [InlineData(@"{ ""h_align"": ""justify"" }", "h_align")]
        [InlineData(@"{ ""v_align"": ""baseline"" }", "v_align")]
        [InlineData(@"{ ""font"": """" }", "font")]
        [InlineData(@"{ ""border"": { ""diagonal"": ""none"" } }", "not a side")]
        [InlineData(@"{ ""border"": { ""top"": """" } }", "line style")]
        [InlineData(@"{ ""border"": ""thin"" }", "border")]
        public void A_style_that_cannot_be_written_is_refused_before_Revit_sees_it(string json, string expected)
        {
            string error;
            Assert.Null(ScheduleAppearanceRules.ParseStyle(JObject.Parse(json), out error));
            Assert.Contains(expected, error);
        }

        [Theory]
        [InlineData("#FF8000", true, 255, 128, 0)]
        [InlineData("ff8000", true, 255, 128, 0)]
        [InlineData("#FF80", false, 0, 0, 0)]
        [InlineData("#GG8000", false, 0, 0, 0)]
        [InlineData("", false, 0, 0, 0)]
        public void Colours_are_exactly_six_hex_digits(string text, bool ok, int r, int g, int b)
        {
            byte rr, gg, bb;
            Assert.Equal(ok, ScheduleAppearanceRules.TryParseColor(text, out rr, out gg, out bb));
            Assert.Equal(new[] { r, g, b }, new[] { (int)rr, (int)gg, (int)bb });
        }

        // ---- the set_appearance action -------------------------------------------------

        [Fact]
        public void A_well_formed_appearance_action_gives_back_its_style()
        {
            StyleSpec spec;
            Assert.Null(ScheduleAppearanceRules.ValidateAppearance(
                Action(@"{ ""operation"": ""set_appearance"", ""scope"": ""headers"", ""style"": { ""bold"": true } }"), out spec));
            Assert.True(spec.Bold);
        }

        [Fact]
        public void The_definition_switches_need_no_scope()
        {
            StyleSpec spec;
            Assert.Null(ScheduleAppearanceRules.ValidateAppearance(
                Action(@"{ ""operation"": ""set_appearance"", ""show_grid"": true, ""show_title"": false }"), out spec));
            Assert.Null(spec);
        }

        [Theory]
        [InlineData(@"{ ""operation"": ""set_appearance"" }", "changes nothing")]
        [InlineData(@"{ ""operation"": ""set_appearance"", ""style"": { ""bold"": true } }", "needs scope")]
        [InlineData(@"{ ""operation"": ""set_appearance"", ""scope"": ""footer"", ""style"": { ""bold"": true } }", "not one of")]
        [InlineData(@"{ ""operation"": ""set_appearance"", ""scope"": ""body"", ""show_grid"": true }", "scope only means something")]
        [InlineData(@"{ ""operation"": ""set_appearance"", ""scope"": ""field"", ""style"": { ""bold"": true } }", "ONE column")]
        [InlineData(@"{ ""operation"": ""set_appearance"", ""scope"": ""field"", ""text_type"": ""Notes"", ""parameter_id"": 5 }", "REGION")]
        [InlineData(@"{ ""operation"": ""set_appearance"", ""show_grid"": ""si"" }", "show_grid")]
        public void An_appearance_action_that_means_nothing_or_two_things_is_refused(string json, string expected)
        {
            StyleSpec spec;
            string error = ScheduleAppearanceRules.ValidateAppearance(Action(json), out spec);
            Assert.NotNull(error);
            Assert.Contains(expected, error);
        }

        [Fact]
        public void A_column_style_can_name_its_field_three_ways()
        {
            StyleSpec spec;
            foreach (string key in new[] { @"""parameter_id"": 5", @"""name"": ""Comments""", @"""field_index"": 2" })
                Assert.Null(ScheduleAppearanceRules.ValidateAppearance(
                    Action(@"{ ""operation"": ""set_appearance"", ""scope"": ""field"", " + key + @", ""style"": { ""italic"": true } }"), out spec));
        }

        [Fact]
        public void Text_types_are_a_region_choice_by_name_or_id()
        {
            StyleSpec spec;
            Assert.Null(ScheduleAppearanceRules.ValidateAppearance(
                Action(@"{ ""operation"": ""set_appearance"", ""scope"": ""title"", ""text_type"": ""Arial 5mm"" }"), out spec));
            Assert.Null(ScheduleAppearanceRules.ValidateAppearance(
                Action(@"{ ""operation"": ""set_appearance"", ""scope"": ""body"", ""text_type"": 4455 }"), out spec));
            Assert.Contains("name or the id", ScheduleAppearanceRules.ValidateAppearance(
                Action(@"{ ""operation"": ""set_appearance"", ""scope"": ""body"", ""text_type"": true }"), out spec));
        }

        // ---- conditional formatting: no API, said once ------------------------------------

        [Theory]
        [InlineData("conditional_format")]
        [InlineData("conditional_formatting")]
        public void Conditional_formatting_is_refused_with_the_reason_and_the_alternatives(string key)
        {
            StyleSpec spec;
            string error = ScheduleAppearanceRules.ValidateAppearance(
                Action(@"{ ""operation"": ""set_appearance"", """ + key + @""": { ""field"": ""Area"" } }"), out spec);
            Assert.Equal(ScheduleAppearanceRules.ConditionalFormatRefusal, error);
            Assert.Contains("no API", error);
            Assert.Contains("set_filters", error);
            Assert.Contains("color_by_value", error);
            Assert.Contains("execute_python", error);
        }

        // ---- which header row is the title ---------------------------------------------------

        [Theory]
        [InlineData(2, true, true, "0", "1")]
        [InlineData(2, false, true, "", "0,1")]
        [InlineData(1, true, true, "0", "")]
        [InlineData(2, true, false, "0", "")]
        [InlineData(3, true, true, "0", "1,2")]
        [InlineData(0, true, true, "", "")]
        public void Row_zero_is_the_title_only_when_the_title_is_shown(int rows, bool title, bool headers, string expectTitle, string expectHeadings)
        {
            int[] titleRows, headingRows;
            ScheduleAppearanceRules.HeaderLayout(rows, title, headers, out titleRows, out headingRows);
            Assert.Equal(expectTitle, string.Join(",", titleRows));
            Assert.Equal(expectHeadings, string.Join(",", headingRows));
        }

        // ---- split ------------------------------------------------------------------------------

        [Fact]
        public void A_split_by_parts_makes_that_many_segments()
        {
            int count; List<double> heights;
            Assert.Null(ScheduleAppearanceRules.ValidateSplit(Action(@"{ ""parts"": 3 }"), out count, out heights));
            Assert.Equal(3, count);
            Assert.Null(heights);
        }

        [Fact]
        public void A_split_by_heights_makes_one_segment_more_than_it_lists()
        {
            int count; List<double> heights;
            Assert.Null(ScheduleAppearanceRules.ValidateSplit(Action(@"{ ""heights_mm"": [200, 150.5] }"), out count, out heights));
            Assert.Equal(3, count);
            Assert.Equal(new[] { 200.0, 150.5 }, heights);
        }

        [Theory]
        [InlineData(@"{ }", "OR")]
        [InlineData(@"{ ""parts"": 3, ""heights_mm"": [100] }", "OR")]
        [InlineData(@"{ ""parts"": 1 }", "2 to")]
        [InlineData(@"{ ""parts"": 51 }", "2 to")]
        [InlineData(@"{ ""parts"": 2.5 }", "2 to")]
        [InlineData(@"{ ""heights_mm"": [] }", "heights_mm")]
        [InlineData(@"{ ""heights_mm"": [100, 0] }", "greater than zero")]
        [InlineData(@"{ ""heights_mm"": [100, ""x""] }", "numbers")]
        [InlineData(@"{ ""heights_mm"": -5 }", "heights_mm")]
        public void A_split_that_names_neither_or_both_or_nonsense_is_refused(string json, string expected)
        {
            int count; List<double> heights;
            string error = ScheduleAppearanceRules.ValidateSplit(Action(json), out count, out heights);
            Assert.NotNull(error);
            Assert.Contains(expected, error);
        }

        // ---- the canonical appearance section feeds the stale-plan token ------------------------------

        [Fact]
        public void A_restyle_changes_the_appearance_section_and_only_that_one()
        {
            string before = Canonical(ScheduleAppearanceRules.CanonicalLines(false, true, 1, new[] { "style.title=Arial|10" }));
            string after = Canonical(ScheduleAppearanceRules.CanonicalLines(false, true, 1, new[] { "style.title=Arial|14" }));
            Assert.NotEqual(ScheduleEditRules.DefinitionFingerprint(before), ScheduleEditRules.DefinitionFingerprint(after));
            Assert.Equal(new[] { "appearance" }, ScheduleEditRules.ChangedSections(before, after));
        }

        [Fact]
        public void Splitting_and_grid_lines_are_part_of_the_definition_a_token_binds()
        {
            string whole = Canonical(ScheduleAppearanceRules.CanonicalLines(true, true, 1, null));
            string split = Canonical(ScheduleAppearanceRules.CanonicalLines(true, true, 3, null));
            string noGrid = Canonical(ScheduleAppearanceRules.CanonicalLines(false, true, 1, null));
            Assert.Equal(new[] { "appearance" }, ScheduleEditRules.ChangedSections(whole, split));
            Assert.Equal(new[] { "appearance" }, ScheduleEditRules.ChangedSections(whole, noGrid));
            Assert.Empty(ScheduleEditRules.ChangedSections(whole, whole));
        }

        [Fact]
        public void A_definition_without_appearance_lines_reads_as_before()
        {
            string old = ScheduleEditRules.CanonicalDefinition(null, null, null, true, false, true);
            Assert.DoesNotContain("appearance", old);
        }

        private static string Canonical(IEnumerable<string> appearance)
            => ScheduleEditRules.CanonicalDefinition(new List<ScheduleFieldFacts>(), new string[0], new string[0], true, false, true, appearance);

        // ---- the operation vocabulary --------------------------------------------------------------------

        [Fact]
        public void The_three_new_operations_are_known_to_the_command()
        {
            foreach (string op in new[] { "set_appearance", "split", "unsplit" })
            {
                Assert.Null(ScheduleEditRules.ValidateOperation(op));
                Assert.Contains(op, ScheduleEditRules.KnownOperations);
            }
            Assert.Contains("set_appearance", ScheduleEditRules.ValidateOperation("paint"));
        }
    }
}
