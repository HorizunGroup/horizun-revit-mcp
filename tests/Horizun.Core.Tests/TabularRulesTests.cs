// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The CSV a spreadsheet actually exports: quoted commas, doubled quotes,
// newlines inside cells, CRLF endings - and the mapping refusals that name
// every problem at once instead of one per afternoon.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TabularRulesTests
    {
        [Fact]
        public void Plain_rows_parse_with_crlf_and_lf_alike()
        {
            List<string[]> rows = TabularRules.ParseCsv("a,b,c\r\n1,2,3\n4,5,6");
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "a", "b", "c" }, rows[0]);
            Assert.Equal(new[] { "4", "5", "6" }, rows[2]);
        }

        [Fact]
        public void Quoted_cells_keep_commas_doubled_quotes_and_newlines()
        {
            List<string[]> rows = TabularRules.ParseCsv("Mark,Comment\r\nM-1,\"cruza, dijo \"\"ok\"\"\nlinea 2\"");
            Assert.Equal(2, rows.Count);
            Assert.Equal("cruza, dijo \"ok\"\nlinea 2", rows[1][1]);
        }

        [Fact]
        public void A_trailing_newline_does_not_invent_an_empty_row()
        {
            Assert.Equal(2, TabularRules.ParseCsv("a,b\r\n1,2\r\n").Count);
        }

        [Fact]
        public void An_empty_trailing_cell_is_still_a_cell()
        {
            List<string[]> rows = TabularRules.ParseCsv("a,b\n1,");
            Assert.Equal(2, rows[1].Length);
            Assert.Equal("", rows[1][1]);
        }

        [Fact]
        public void Every_missing_column_is_named_at_once()
        {
            string error = TabularRules.MapColumns(new[] { "Mark", "Comments" }, "Codigo",
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Precio", "HRZ_Precio"),
                    new KeyValuePair<string, string>("Comments", "Comments")
                }, out _);
            Assert.NotNull(error);
            Assert.Contains("'Codigo'", error);
            Assert.Contains("'Precio'", error);
            Assert.DoesNotContain("'Comments'", error);
        }

        [Fact]
        public void Column_names_match_exactly_including_case()
        {
            string error = TabularRules.MapColumns(new[] { "mark" }, "Mark",
                new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("mark", "Mark") }, out _);
            Assert.NotNull(error);
            Assert.Contains("including case", error);
        }

        [Fact]
        public void A_clean_mapping_resolves_indexes_in_declared_order()
        {
            string error = TabularRules.MapColumns(new[] { "Mark", "A", "B" }, "Mark",
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("B", "ParamB"),
                    new KeyValuePair<string, string>("A", "ParamA")
                }, out TabularMapping mapping);
            Assert.Null(error);
            Assert.Equal(0, mapping.KeyIndex);
            Assert.Equal(2, mapping.ValueColumns[0].Key);
            Assert.Equal("ParamB", mapping.ValueColumns[0].Value);
        }

        [Fact]
        public void Duplicate_file_keys_come_back_with_their_row_numbers()
        {
            var rows = new List<string[]> { new[] { "M-1", "x" }, new[] { "M-2", "y" }, new[] { "M-1", "z" } };
            Dictionary<string, List<int>> duplicates = TabularRules.DuplicateKeys(rows, 0, firstRowNumber: 2);
            Assert.Single(duplicates);
            Assert.Equal(new List<int> { 2, 4 }, duplicates["M-1"]);
        }

        [Fact]
        public void The_unchanged_skip_is_exact_and_conservative()
        {
            Assert.False(TabularRules.ShouldWrite("3000", "3000"));
            Assert.True(TabularRules.ShouldWrite("3000", "3000.0"));   // formatting differs -> write again, harmlessly
            Assert.True(TabularRules.ShouldWrite("x", null));
            Assert.False(TabularRules.ShouldWrite("", ""));
        }

        [Fact]
        public void A_cell_parses_only_under_its_declared_separator()
        {
            double value;
            Assert.True(TabularRules.TryParseCell("300.5", ".", out value));
            Assert.Equal(300.5, value, 9);
            Assert.True(TabularRules.TryParseCell("300,5", ",", out value));
            Assert.Equal(300.5, value, 9);
            // The OTHER mark is never silently a thousands separator: the cell
            // simply does not parse here and falls back to the string compare.
            Assert.False(TabularRules.TryParseCell("1.234,5", ",", out _));
            Assert.False(TabularRules.TryParseCell("300,5", ".", out _));
            Assert.False(TabularRules.TryParseCell("300 mm", ".", out _));
            Assert.False(TabularRules.TryParseCell("", ".", out _));
            Assert.False(TabularRules.TryParseCell(null, ".", out _));
        }

        [Fact]
        public void The_numeric_unchanged_rule_absorbs_display_rounding_and_nothing_else()
        {
            Assert.True(TabularRules.NumbersEqual(300.0, 300.0));
            Assert.True(TabularRules.NumbersEqual(300.0, 300.0000001));     // display rounding
            Assert.True(TabularRules.NumbersEqual(0.0, 0.0));
            Assert.False(TabularRules.NumbersEqual(300.0, 300.001));        // a real change
            Assert.False(TabularRules.NumbersEqual(300.0, 350.0));
            Assert.False(TabularRules.NumbersEqual(0.0, 0.001));
        }
    }
}
