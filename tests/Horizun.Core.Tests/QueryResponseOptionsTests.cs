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
            Assert.Contains("link_instance_id", compact["return_fields"].Values<string>());
            Assert.Contains("source_model", compact["return_fields"].Values<string>());
            Assert.True(JToken.DeepEquals(input["parameters"], compact["parameters"]));
            Assert.Null(input["return_fields"]);
        }

        [Theory]
        [InlineData("cursor")][InlineData("group_by")][InlineData("return_parameters")][InlineData("return_fields")]
        public void Summary_rejects_ambiguous_detail_requests(string key)
        {
            Assert.Throws<ArgumentException>(() => QueryResponseOptions.Prepare(new JObject {
                ["response_mode"]="summary", [key]=new JArray("x") }));
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
