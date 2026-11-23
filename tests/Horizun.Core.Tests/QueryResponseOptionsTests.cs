using System;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class QueryResponseOptionsTests
    {
        [Theory]
        [InlineData("view")]
        [InlineData("current_view")]
        [InlineData("VIEW")]
        public void View_scope_defaults_to_host_without_mutating_the_request(string scope)
        {
            var request = new JObject { ["scope"] = scope, ["view_id"] = 123 };
            JObject prepared = QueryResponseOptions.Prepare(request);
            Assert.Equal(false, prepared.Value<bool?>("include_links"));
            Assert.Null(request["include_links"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void View_scope_keeps_an_explicit_federation_choice_for_the_command_guard(bool includeLinks)
        {
            var request = new JObject { ["scope"] = "view", ["include_links"] = includeLinks };
            Assert.Equal(includeLinks, QueryResponseOptions.Prepare(request).Value<bool>("include_links"));
            Assert.Null(QueryResponseOptions.Prepare(new JObject())["include_links"]);
        }

        [Fact]
        public void Full_remains_unchanged_and_compact_preserves_filter_and_explicit_projection()
        {
            var input = JObject.Parse(@"{'family':'Valve','parameters':[{'name':'Length','operator':'gt','value':2}]}");
            Assert.True(JToken.DeepEquals(input, QueryResponseOptions.Prepare(input)));
            input["response_mode"] = "compact";
            input["return_fields"] = new JArray("unique_id");
            input["parameter_format"] = "full";
            Assert.True(JToken.DeepEquals(input, QueryResponseOptions.Prepare(input)));
            input.Remove("return_fields"); input.Remove("parameter_format");
            JObject compact = QueryResponseOptions.Prepare(input);
            Assert.Equal("compact", (string)compact["parameter_format"]);
            Assert.Contains("category", compact["return_fields"].Values<string>());
            Assert.Contains("link_instance_id", compact["return_fields"].Values<string>());
            Assert.Contains("source_model", compact["return_fields"].Values<string>());
            Assert.True(JToken.DeepEquals(input["parameters"], compact["parameters"]));
            Assert.Null(input["return_fields"]);
        }

        [Theory]
        [InlineData("cursor")][InlineData("return_parameters")][InlineData("return_fields")]
        public void Summary_normalizes_row_only_requests(string key)
        {
            var prepared = QueryResponseOptions.Prepare(new JObject {
                ["response_mode"]="summary", [key]=new JArray("x") }, out JArray changes);
            Assert.Null(prepared[key]);
            Assert.Equal(key, (string)changes[0]["field"]);
        }

        [Fact]
        public void Element_id_is_a_redundant_projection_and_unknown_fields_still_fail()
        {
            var ordinary = QueryResponseOptions.ProjectedFields(new[] { "name" });
            var redundant = QueryResponseOptions.ProjectedFields(new[] { "name", "element_id", "ELEMENT_ID" });
            Assert.True(ordinary.SetEquals(redundant));
            Assert.Empty(QueryResponseOptions.ProjectedFields(new[] { "element_id" }));
            Assert.Contains("is not a row field", Assert.Throws<ArgumentException>(() => QueryResponseOptions.ProjectedFields(new[] { "not_a_field" })).Message);
        }

        [Fact]
        public void Element_id_only_projection_returns_only_the_id_not_all_fields()
        {
            var row = JObject.Parse(@"{'element_id':42,'unique_id':'u','name':'Wall','source_kind':'link','link_instance_id':7}");
            var projected = QueryResponseOptions.ApplyIdOnlyProjection(row, QueryResponseOptions.ProjectedFields(new[] { "element_id" }));
            Assert.True(JToken.DeepEquals(JObject.Parse(@"{'element_id':42}"), projected));
            var full = JObject.Parse(@"{'element_id':42,'name':'Wall'}");
            Assert.NotNull(QueryResponseOptions.ApplyIdOnlyProjection(full, null)["name"]);
        }

        [Fact]
        public void Summary_grouping_is_accepted_and_keeps_group_counts_without_rows()
        {
            var prepared = QueryResponseOptions.Prepare(new JObject { ["response_mode"] = "summary", ["group_by"] = new JArray("category") });
            Assert.Equal("category", (string)prepared["group_by"][0]);
            var result = QueryResponseOptions.Shape(JObject.Parse("{groups:[{count:3}],rows:[{element_id:1}],coverage_complete:true}"), "summary");
            Assert.Equal  (3, (int)result["groups"][0]["count"]);
            Assert.Null(result["rows"]);
        }

        [Fact]
        public void Summary_allows_grouped_aggregates_but_keeps_row_projections_out()
        {
            var request = new JObject { ["response_mode"] = "summary", ["group_by"] = new JArray("category") };
            JObject prepared = QueryResponseOptions.Prepare(request);
            Assert.Equal("summary", (string)prepared["response_mode"]);
            Assert.True(JToken.DeepEquals(request["group_by"], prepared["group_by"]));
            Assert.Null(request["return_fields"]);
        }

        [Fact]
        public void Summary_preserves_group_results_when_shaping_an_aggregate()
        {
            var aggregate = new JObject { ["matched_total"] = 4, ["groups"] = new JArray("Walls"),
                ["coverage_complete"] = true, ["truncated"] = false, ["rows"] = new JArray() };
            JObject shaped = QueryResponseOptions.Shape(aggregate, "summary");
            Assert.True(JToken.DeepEquals(aggregate["groups"], shaped["groups"]));
            Assert.Equal(4, (int)shaped["matched_total"]);
            Assert.Null(shaped["rows"]);
        }

        [Fact]
        public void Summary_removes_rows_but_keeps_counts_and_incomplete_coverage_evidence()
        {
            var rows = new JArray();
            for (int i=0; i<500; i++) rows.Add(new JObject { ["element_id"]=i, ["name"]="Fixture element", ["parameters"]=new JObject { ["Length"]=2.0 } });
            var full = new JObject { ["rows"]=rows, ["matched_total"]=8000, ["returned"]=500,
                ["truncated"]=true, ["next_cursor"]="page2", ["coverage_complete"]=false,
                ["unreadable"]=new JArray("missing parameter"), ["unreadable_total"]=1,
                ["federated_coverage"]=new JObject { ["unloaded_links"]=2 }, ["summary"]=new JObject { ["total"]=8000 } };
            JObject summary = QueryResponseOptions.Shape((JObject)full.DeepClone(), "summary");
            Assert.Null(summary["rows"]); Assert.Null(summary["next_cursor"]);
            foreach (string key in new[] { "matched_total", "summary", "coverage_complete", "unreadable", "unreadable_total", "federated_coverage" })
                Assert.True(JToken.DeepEquals(full[key], summary[key]), key);
            int before=Encoding.UTF8.GetByteCount(full.ToString(Formatting.None));
            int after=Encoding.UTF8.GetByteCount(summary.ToString(Formatting.None));
            Assert.True(after < before / 10, $"Summary {after} bytes, full {before}");
        }

        [Theory]
        [InlineData(false,false,0)][InlineData(false,true,1)][InlineData(true,false,1)][InlineData(true,true,1)]
        public void Geometry_is_read_once_only_when_needed(bool filter, bool output, int expected)
        {
            int reads=0;
            object result=QueryResponseOptions.ReadBounds(filter,output,()=> {reads++;return new object();});
            Assert.Equal(expected,reads); Assert.Equal(expected==0,result==null);
        }
    }
}
