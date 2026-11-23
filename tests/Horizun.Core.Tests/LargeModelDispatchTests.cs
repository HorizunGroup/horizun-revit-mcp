using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LargeModelDispatchTests
    {
        private sealed class FakeCommand : ICommand
        {
            public string Name { get; set; }
            public string Description => "test";
            public int Calls;
            public CommandResult Execute(UIApplication app, string args) { Calls++; return CommandResult.Ok(new { ok = true }); }
        }

        [Theory]
        [InlineData("full", false, false)]
        [InlineData("compact", false, false)]
        [InlineData("full", true, true)]
        [InlineData("summary", false, true)]
        [InlineData("summary", true, true)]
        public void Query_large_scope_uses_confirmation_independent_of_page_size(string mode, bool confirmed, bool runs)
        {
            var doc = new Document { TestElements = 98899, TestTypes = 6606 };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var cmd = new FakeCommand { Name = "horizun_query_model" };
            var args = JObject.Parse("{ 'include_links': true, 'include_types': true, 'return_parameters': ['*'], 'parameter_format': 'full', 'max_rows': 1 }");
            args["response_mode"] = mode;
            if (confirmed) args["confirm_large_operation"] = true;
            var result = LargeModelDispatch.Execute(app, cmd, args.ToString());
            Assert.Equal(runs, result.Success);
            Assert.Equal(runs ? 1 : 0, cmd.Calls);
            if (!runs)
            {
                Assert.Equal("confirmacion_requerida", ErrorCodes.Classify(result.Error, result.Detail).Code);
                Assert.False((bool)result.Detail["operation_started"]);
                Assert.Equal(105505, (long)result.Detail["estimate"]["scope_upper_bound"]);
                var expected = (JObject)args.DeepClone(); expected["confirm_large_operation"] = true;
                Assert.Equal("horizun_query_model", (string)result.Detail["suggested_call"]["tool"]);
                Assert.True(JToken.DeepEquals(expected, result.Detail["suggested_call"]["arguments"]));
            }
        }

        [Theory]
        [InlineData("horizun_query_model")]
        [InlineData("horizun_list_elements")]
        [InlineData("horizun_model_scan")]
        [InlineData("horizun_audit_model")]
        [InlineData("horizun_quantities")]
        public void Actual_dispatch_refuses_and_reports_estimate_then_runs_only_with_confirmation(string tool)
        {
            var doc = new Document { TestElements = 120000 };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var cmd = new FakeCommand { Name = tool };
            var refused = LargeModelDispatch.Execute(app, cmd, "{}");
            Assert.False(refused.Success);
            Assert.Contains("confirm_large_operation=true", refused.Error);
            Assert.Equal(0, cmd.Calls);
            Assert.Equal(120000, (long)refused.Detail["estimate"]["scope_upper_bound"]);
            Assert.False((bool)refused.Detail["operation_started"]);
            Assert.NotEmpty((string)refused.Detail["scope_proposal"]);
            if (tool == "horizun_query_model")
                Assert.Equal("confirmacion_requerida", ErrorCodes.Classify(refused.Error, refused.Detail).Code);
            var invalid = LargeModelDispatch.Execute(app, cmd, "{\"confirm_large_operation\":\"true\"}");
            Assert.False(invalid.Success);
            Assert.Contains("must be a boolean", invalid.Error);
            Assert.Equal(0, cmd.Calls);
            var accepted = LargeModelDispatch.Execute(app, cmd, "{\"confirm_large_operation\":true}");
            Assert.True(accepted.Success);
            Assert.Equal(1, cmd.Calls);
        }
        

        [Fact]
        public void Federated_counts_include_links_and_unavailable_link_is_unknown()
        {
            var doc = new Document { TestElements = 70000 };
            doc.TestLinkDocuments.Add(new RevitLinkInstance { Linked = new Document { TestElements = 50000 } });
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var cmd = new FakeCommand { Name = "horizun_query_model" };
            var refused = LargeModelDispatch.Execute(app, cmd, "{}");
            Assert.False(refused.Success);
            Assert.Equal(120000, (long)refused.Detail["estimate"]["scope_upper_bound"]);
            Assert.Equal(50000, (long)refused.Detail["estimate"]["counts_by_source"]["link_1"]);
            doc.TestLinkDocuments.Add(new RevitLinkInstance());
            refused = LargeModelDispatch.Execute(app, cmd, "{}");
            Assert.False(refused.Success);
            Assert.Equal(JTokenType.Null, refused.Detail["estimate"]["scope_upper_bound"].Type);
            Assert.False((bool)refused.Detail["estimate"]["coverage_complete"]);
            Assert.Equal(0, cmd.Calls);
        }

        [Fact]
        public void Sequence_step_serializes_the_structured_refusal()
        {
            var step = new SequenceEntry { Detail = new JObject { ["scope_proposal"] = "narrow" } };
            Assert.Equal("narrow", (string)step.ToJson()["detail"]["scope_proposal"]);
        }
        [Fact]
        public void Wrong_document_is_refused_before_the_census()
        {
            var doc = new Document { Title = "actual", TestElements = 120000 };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var cmd = new FakeCommand { Name = "horizun_query_model" };
            var refused = LargeModelDispatch.Execute(app, cmd, "{\"target_document\":\"other\"}");
            Assert.False(refused.Success);
            Assert.Contains("actual", refused.Error);
            Assert.Equal("actual", (string)refused.Detail["open_documents"][0]);
            Assert.Equal(0, doc.TestNativeCounts);
            Assert.Equal(0, cmd.Calls);
        }

        
    }
}
