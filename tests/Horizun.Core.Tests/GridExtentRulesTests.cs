using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GridExtentRulesTests
    {
        [Theory]
        [InlineData(null, "maximize")]
        [InlineData("", "maximize")]
        [InlineData("Maximize", "maximize")]
        [InlineData(" NONE ", "none")]
        public void The_extent_mode_defaults_to_maximize(string raw, string expected)
        {
            Assert.Equal(expected, GridExtentRules.Parse(raw, out string error));
            Assert.Null(error);
        }

        [Fact]
        public void An_unknown_extent_mode_is_refused_and_says_the_two_it_knows()
        {
            Assert.Null(GridExtentRules.Parse("all_floors", out string error));
            Assert.Contains("'maximize'", error);
            Assert.Contains("'none'", error);
        }

        [Fact]
        public void An_extent_that_reaches_every_level_covers_them()
        {
            Assert.True(GridExtentRules.CoversLevels(-5, 80, new[] { 0.0, 10, 20, 30, 70 }, 1e-3, out var uncovered));
            Assert.Empty(uncovered);
        }

        [Fact]
        public void A_level_above_the_top_of_the_extent_is_listed_not_hidden()
        {
            // Field case: the grid stopped at the first floor, so floors 2..roof stayed uncovered.
            bool covers = GridExtentRules.CoversLevels(-2, 12, new[] { 0.0, 10, 20, 30 }, 1e-3, out List<double> uncovered);
            Assert.False(covers);
            Assert.Equal(new[] { 20.0, 30.0 }, uncovered);
        }

        [Fact]
        public void The_tolerance_forgives_rounding_at_the_edge_and_nothing_more()
        {
            Assert.True(GridExtentRules.CoversLevels(0.0005, 10, new[] { 0.0, 10 }, 1e-3, out _));
            Assert.False(GridExtentRules.CoversLevels(0.01, 10, new[] { 0.0, 10 }, 1e-3, out _));
        }

        [Fact]
        public void An_unreadable_or_inverted_extent_never_counts_as_covering()
        {
            Assert.False(GridExtentRules.CoversLevels(double.NaN, 10, new[] { 0.0 }, 1e-3, out _));
            Assert.False(GridExtentRules.CoversLevels(10, 0, new[] { 5.0 }, 1e-3, out _));
        }

        [Fact]
        public void A_z_on_a_grid_endpoint_is_flattened_and_the_note_says_so()
        {
            Assert.True(GridExtentRules.NeedsFlatten(20.5, 20.5));
            Assert.False(GridExtentRules.NeedsFlatten(0, 0));
            string note = GridExtentRules.FlattenNote(20.5, 20.5, "m");
            Assert.Contains("20.5", note);
            Assert.Contains("vertical datum plane", note);
        }

        [Fact]
        public void The_report_carries_the_levels_not_covered()
        {
            var report = GridExtentRules.Report("maximize", -1, 11, 4, new List<double> { 20, 30 });
            Assert.Equal("maximize", (string)report["extent_3d"]);
            Assert.Equal(4, (int)report["levels_checked"]);
            Assert.Equal(2, ((Newtonsoft.Json.Linq.JArray)report["levels_not_covered_feet"]).Count);
        }
    }
}
