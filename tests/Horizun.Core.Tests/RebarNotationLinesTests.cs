// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// L10: the bar notation, wired to a tool. horizun_query_structure mode=parse_notation
// takes `lines: [...]` as written in a schedule and returns each line read into
// millimetres, or refused with its code. A line that is refused never stops the others,
// and a request that is not a list of strings is refused whole.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarNotationLinesTests
    {
        private static JObject Lines(params string[] lines)
        {
            JObject r = RebarNotation.ParseLines(new JArray(lines.Cast<object>().ToArray()), out string code, out string error);
            Assert.True(r != null, code + ": " + error);
            return r;
        }

        [Fact]
        public void Every_line_is_read_in_order_into_millimetres()
        {
            JObject r = Lines("4#5 L=6.20", "E#3 1@0.05 10@0.10 R@0.20", "2#4+1#5", "4Ø16");
            var rows = (JArray)r["rows"];
            Assert.Equal(4, rows.Count);
            Assert.Equal(new[] { 0, 1, 2, 3 }, rows.Select(x => (int)x["index"]).ToArray());

            Assert.True((bool)rows[0]["ok"]);
            Assert.Equal("4#5 L=6.20", (string)rows[0]["source"]);
            Assert.Equal(6200.0, (double)rows[0]["length_mm"]);
            Assert.Equal(4, (int)rows[0]["bars"][0]["count"]);

            Assert.Equal(RebarNotation.KindStirrup, (string)rows[1]["kind"]);
            Assert.Equal(new[] { 50.0, 100.0, 200.0 }, rows[1]["zones"].Select(z => (double)z["spacing_mm"]).ToArray());
            Assert.True((bool)rows[1]["zones"][2]["rest"]);

            Assert.Equal(2, rows[2]["bars"].Count());
            Assert.Equal(RebarNotation.SystemDiameterMm, (string)rows[3]["bars"][0]["system"]);

            Assert.Equal(4, (int)r["summary"]["lines"]);
            Assert.Equal(4, (int)r["summary"]["read"]);
            Assert.Equal(0, (int)r["summary"]["refused"]);
        }

        [Fact]
        public void A_refused_line_carries_its_code_and_the_rest_are_still_read()
        {
            JObject r = Lines("4#5", "4#5 L=1,234", "E#3", "4#25");
            var rows = (JArray)r["rows"];
            Assert.True((bool)rows[0]["ok"]);
            Assert.False((bool)rows[1]["ok"]);
            Assert.Equal(RebarNotation.CodeBadNumber, (string)rows[1]["code"]);
            Assert.Equal(RebarNotation.CodeNoSpacing, (string)rows[2]["code"]);
            Assert.Equal(RebarNotation.CodeBarOutOfRange, (string)rows[3]["code"]);
            Assert.Null(rows[1]["bars"]);                                      // a refusal reads as nothing, not as zero bars
            Assert.Equal(1, (int)r["summary"]["read"]);
            Assert.Equal(3, (int)r["summary"]["refused"]);
            Assert.Equal(1, (int)r["summary"]["refusals_by_code"][RebarNotation.CodeBadNumber]);
            Assert.Equal(1, (int)r["summary"]["refusals_by_code"][RebarNotation.CodeNoSpacing]);
        }

        [Fact]
        public void An_empty_line_is_a_refused_line_not_a_refused_request()
        {
            JObject r = Lines("", "4#5");
            Assert.Equal(RebarNotation.CodeEmpty, (string)r["rows"][0]["code"]);
            Assert.True((bool)r["rows"][1]["ok"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("'4#5'")]                 // a string, not a list
        [InlineData("[]")]                    // nothing to read
        [InlineData("[ '4#5', 5 ]")]          // a number is not a schedule cell
        [InlineData("[ '4#5', null ]")]
        [InlineData("[ '4#5', { 'text': '4#5' } ]")]
        public void A_request_that_is_not_a_list_of_strings_is_refused_whole(string json)
        {
            JToken lines = json == null ? null : JToken.Parse(json);
            JObject r = RebarNotation.ParseLines(lines, out string code, out string error);
            Assert.Null(r);
            Assert.Equal(RebarNotation.CodeLinesNotUsable, code);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void More_lines_than_the_ceiling_are_refused_rather_than_cut()
        {
            var many = new JArray(Enumerable.Repeat((object)"4#5", RebarNotation.MaxLines + 1).ToArray());
            JObject r = RebarNotation.ParseLines(many, out string code, out _);
            Assert.Null(r);
            Assert.Equal(RebarNotation.CodeLinesNotUsable, code);
            Assert.NotNull(RebarNotation.ParseLines(new JArray(Enumerable.Repeat((object)"4#5", RebarNotation.MaxLines).ToArray()), out _, out _));
        }
    }
}
