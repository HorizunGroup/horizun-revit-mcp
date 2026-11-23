using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class QueryPagingRulesTests
    {
        [Fact]
        public void Wildcard_budget_never_materializes_98899_parameter_rows()
        {
            var budget = new QueryPagingRules.WildcardBudget(100, 1000);
            int projected = 0, discarded = 0;
            var page = QueryPagingRules.ProjectWildcardPage(System.Linq.Enumerable.Range(0, 98899), budget,
                row => { projected++; return 200; }, row => discarded++);
            Assert.Equal(5, budget.Rows);
            Assert.Equal(5, page.Count);
            Assert.Equal(6, projected);
            Assert.Equal(1, discarded);
            Assert.True(budget.Exhausted);
        }
        [Fact]
        public void No_offset_starts_at_zero()
        {
            Assert.Null(QueryPagingRules.ParseOffset(null, null, out int start));
            Assert.Equal(0, start);
            Assert.Null(QueryPagingRules.ParseOffset(JValue.CreateNull(), "abc", out start));
            Assert.Equal(0, start);
        }

        [Fact]
        public void An_offset_is_a_start_position_and_is_no_longer_ignored()
        {
            // Field case: offset=200 answered the first page, so a loop advancing by offset never ended.
            Assert.Null(QueryPagingRules.ParseOffset(new JValue(200), null, out int start));
            Assert.Equal(200, start);
            Assert.Null(QueryPagingRules.ParseOffset(new JValue(0), "", out start));
            Assert.Equal(0, start);
        }

        [Fact]
        public void An_offset_beside_a_cursor_is_refused_not_silently_preferred()
        {
            string problem = QueryPagingRules.ParseOffset(new JValue(10), "Zm9v", out int start);
            Assert.Contains("cannot be combined", problem);
            Assert.Equal(0, start);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2.5)]
        [InlineData("10")]
        [InlineData(4000000000L)]
        public void A_malformed_offset_is_refused_by_name(object raw)
        {
            string problem = QueryPagingRules.ParseOffset(new JValue(raw), null, out int start);
            Assert.Contains("offset must be a non-negative integer", problem);
            Assert.Equal(0, start);
        }

        [Fact]
        public void Star_lists_every_parameter_and_needs_the_full_row_format()
        {
            Assert.Null(QueryPagingRules.AllParametersProblem(new[] { "*" }, "full", false, false));
            Assert.Contains("parameter_format=full", QueryPagingRules.AllParametersProblem(new[] { "*" }, "compact", false, false));
            Assert.Contains("group_by or sum_parameters", QueryPagingRules.AllParametersProblem(new[] { "*" }, "full", true, false));
            Assert.Contains("group_by or sum_parameters", QueryPagingRules.AllParametersProblem(new[] { "*" }, "full", false, true));
        }

        [Fact]
        public void Without_star_nothing_changes()
        {
            Assert.Null(QueryPagingRules.AllParametersProblem(new[] { "Height", "Width" }, "compact", true, true));
            Assert.Null(QueryPagingRules.AllParametersProblem(null, "compact", true, true));
        }

        [Fact]
        public void Parameter_keys_carry_the_scope_and_never_collide()
        {
            var used = new System.Collections.Generic.HashSet<string>();
            Assert.Equal("instance:Mark", QueryPagingRules.ParameterKey("instance", "Mark", used));
            Assert.Equal("type:Mark", QueryPagingRules.ParameterKey("type", "Mark", used));
            Assert.Equal("instance:Mark (2)", QueryPagingRules.ParameterKey("instance", "Mark", used));
            Assert.Equal("instance:Mark (3)", QueryPagingRules.ParameterKey("instance", "Mark", used));
        }

        [Theory]
        [InlineData(1200, 0, 100, 1100)]
        [InlineData(1200, 100, 500, 600)]
        [InlineData(1200, 1000, 200, 0)]
        [InlineData(0, 0, 0, 0)]
        public void Page_metadata_can_report_exact_rows_remaining(int total, int offset, int returned, int expected)
        {
            Assert.Equal(expected, QueryPageRules.Remaining(total, offset, returned));
        }
    }
}
