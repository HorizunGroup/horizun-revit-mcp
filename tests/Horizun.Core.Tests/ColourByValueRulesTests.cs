using System;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    // The pure half of manage_views color_by_value: where a value comes from (instance,
    // then type), what colour the empty value gets, and how the legend is ordered.
    public class ColourByValueRulesTests
    {
        [Fact]
        public void An_instance_value_wins_over_the_type_value()
        {
            var r = ColourByValueRules.Choose("A-01", "B-02");
            Assert.Equal("A-01", r.Text);
            Assert.Equal(ColourByValueRules.FromInstance, r.Source);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void An_instance_without_value_reads_its_type(string instanceText)
        {
            var r = ColourByValueRules.Choose(instanceText, "B-02");
            Assert.Equal("B-02", r.Text);
            Assert.Equal(ColourByValueRules.FromType, r.Source);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void No_value_anywhere_is_empty_from_nowhere(string instanceText, string typeText)
        {
            var r = ColourByValueRules.Choose(instanceText, typeText);
            Assert.Equal("", r.Text);
            Assert.Equal(ColourByValueRules.FromNone, r.Source);
        }

        [Fact]
        public void The_census_counts_elements_per_value_and_per_origin()
        {
            var c = new ColourByValueRules.Census();
            c.Add(ColourByValueRules.Choose(null, "T1"));
            c.Add(ColourByValueRules.Choose(null, "T1"));
            c.Add(ColourByValueRules.Choose("T1", null));
            c.Add(ColourByValueRules.Choose("I2", null));
            c.Add(ColourByValueRules.Choose(null, null));

            Assert.Equal(3, c.Count);   // T1, I2 and the empty value
            Assert.Equal(3, c.Of("T1").Elements);
            Assert.Equal(ColourByValueRules.FromMixed, c.Of("T1").Source);
            Assert.Equal(ColourByValueRules.FromInstance, c.Of("I2").Source);
            Assert.Equal(ColourByValueRules.FromNone, c.Of("").Source);
            Assert.Equal(2, c.InstanceElements);
            Assert.Equal(2, c.TypeElements);
            Assert.Equal(1, c.EmptyElements);
            Assert.True(c.Has(""));
        }

        [Fact]
        public void A_lone_empty_value_is_grey_and_never_the_first_palette_colour()
        {
            // The field case: a type-bound parameter read on the instance only gave ONE "(empty)" row, in red.
            var legend = ColourByValueRules.PlanLegend(new[] { "" }, 24, 12, null, out bool wrapped, out int real);
            Assert.Single(legend);
            Assert.True(legend[0].IsEmpty);
            Assert.Equal(-1, legend[0].PaletteIndex);
            Assert.Equal("#AAAAAA", legend[0].FixedColour);
            Assert.False(wrapped);
            Assert.Equal(0, real);
        }

        [Fact]
        public void Real_values_start_at_the_first_palette_colour_and_empty_goes_last_in_grey()
        {
            var legend = ColourByValueRules.PlanLegend(new[] { "", "B", "A", "C" }, 24, 12, "#101010", out bool wrapped, out int real);
            Assert.Equal(new[] { "A", "B", "C", "" }, legend.Select(l => l.Value).ToArray());
            Assert.Equal(new[] { 0, 1, 2, -1 }, legend.Select(l => l.PaletteIndex).ToArray());
            Assert.Equal("#101010", legend.Last().FixedColour);
            Assert.False(wrapped);
            Assert.Equal(3, real);
        }

        [Fact]
        public void Empty_never_moves_a_real_values_colour_and_does_not_count_against_the_cap()
        {
            var with = ColourByValueRules.PlanLegend(new[] { "", "A", "B" }, 2, 12, null, out _, out int found);
            var without = ColourByValueRules.PlanLegend(new[] { "A", "B" }, 2, 12, null, out _, out _);
            Assert.Equal(3, with.Count);   // A, B and the empty row, with max_values = 2
            Assert.Equal(without.Select(l => l.PaletteIndex), with.Where(l => !l.IsEmpty).Select(l => l.PaletteIndex));
            Assert.Equal(2, found);
        }

        [Fact]
        public void The_cap_counts_real_values_and_reports_how_many_there_were()
        {
            var values = Enumerable.Range(0, 30).Select(i => "V" + i.ToString("00")).ToList();
            var legend = ColourByValueRules.PlanLegend(values, 5, 12, null, out bool wrapped, out int found);
            Assert.Equal(5, legend.Count);
            Assert.Equal(30, found);
            Assert.False(wrapped);
        }

        [Fact]
        public void The_palette_wraps_past_its_length_and_says_so()
        {
            var values = Enumerable.Range(0, 14).Select(i => "V" + i.ToString("00")).ToList();
            var legend = ColourByValueRules.PlanLegend(values, 24, 12, null, out bool wrapped, out _);
            Assert.True(wrapped);
            Assert.Equal(0, legend[12].PaletteIndex);
            Assert.DoesNotContain(legend, l => l.IsEmpty);
        }

        [Fact]
        public void Values_sort_ordinally_so_the_same_model_gets_the_same_colours()
        {
            var a = ColourByValueRules.PlanLegend(new[] { "b", "B", "a" }, 24, 12, null, out _, out _);
            var b = ColourByValueRules.PlanLegend(new[] { "a", "B", "b" }, 24, 12, null, out _, out _);
            Assert.Equal(new[] { "B", "a", "b" }, a.Select(l => l.Value).ToArray());
            Assert.Equal(a.Select(l => l.Value), b.Select(l => l.Value));
        }

        [Theory]
        [InlineData(null, "#AAAAAA")]
        [InlineData("", "#AAAAAA")]
        [InlineData("  ", "#AAAAAA")]
        [InlineData("#aabbcc", "#AABBCC")]
        [InlineData("00ff7f", "#00FF7F")]
        public void The_empty_colour_is_normalised(string text, string expected) =>
            Assert.Equal(expected, ColourByValueRules.NormalizeColour(text, "empty_color"));

        [Theory]
        [InlineData("#fff")]
        [InlineData("#GGGGGG")]
        [InlineData("red")]
        [InlineData("#1234567")]
        public void A_bad_empty_colour_is_refused_naming_the_field(string text)
        {
            var ex = Assert.Throws<ArgumentException>(() => ColourByValueRules.NormalizeColour(text, "empty_color"));
            Assert.Contains("empty_color", ex.Message);
        }

        [Fact]
        public void Hex_is_upper_case_rrggbb() =>
            Assert.Equal("#E6194B", ColourByValueRules.Hex(0xE6, 0x19, 0x4B));
    }
}
