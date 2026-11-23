using System.Collections.Generic;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// Point 0 of the November 2026 telemetry review. These cases exercise the
    /// same contract validator used to attach schema advice to server refusals.
    /// The 4.3.0 and 5.0.0 contract snapshots were compared before these cases
    /// were selected; none of the fields below was removed between those releases.
    /// </summary>
    public sealed class TelemetryRegression430To500Tests
    {
        private static List<string> Errors(string tool, JObject args) =>
            ContractSchemaCheck.Validate(args, Contract.Find(tool).InputSchema);

        [Fact]
        public void Open_document_path_and_dialog_forms_are_valid_but_file_path_is_not()
        {
            JObject pathOnly = new JObject
            {
                ["path"] = @"C:\Models\sample.rvt",
                ["idempotency_key"] = "open-sample"
            };
            JObject pathWithDialog = (JObject)pathOnly.DeepClone();
            pathWithDialog["on_open_dialog"] = "dismiss";

            Assert.Empty(Errors("horizun_open_document", pathOnly));
            Assert.Empty(Errors("horizun_open_document", pathWithDialog));

            JObject filePath = new JObject
            {
                ["file_path"] = @"C:\Models\sample.rvt",
                ["idempotency_key"] = "open-sample"
            };
            Assert.NotEmpty(Errors("horizun_open_document", filePath));
            Assert.Contains("file_path", UndeclaredArguments.Find("horizun_open_document", filePath));
        }

        [Fact]
        public void Manage_views_shape_is_valid_when_action_payload_is_valid()
        {
            var args = new JObject
            {
                ["target_document"] = "Example",
                ["dry_run"] = true,
                ["transaction_name"] = "Telemetry shape",
                ["actions"] = new JArray(new JObject
                {
                    ["operation"] = "explain_graphics",
                    ["view_id"] = 123
                })
            };

            Assert.Empty(Errors("horizun_manage_views", args));
            Assert.Empty(UndeclaredArguments.Find("horizun_manage_views", args));
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void Create_elements_shape_accepts_documented_optional_fields(bool units, bool responseMode, bool transactionName)
        {
            var args = new JObject
            {
                ["target_document"] = "Example",
                ["dry_run"] = true,
                ["elements"] = new JArray(new JObject
                {
                    ["kind"] = "level",
                    ["name"] = "Level 1",
                    ["elevation"] = 0
                })
            };
            if (units) args["units"] = "mm";
            if (responseMode) args["response_mode"] = "summary";
            if (transactionName) args["transaction_name"] = "Telemetry shape";

            Assert.Empty(Errors("horizun_create_elements", args));
            Assert.Empty(UndeclaredArguments.Find("horizun_create_elements", args));
        }

        [Theory]
        [InlineData("limit", 20)]
        [InlineData("ids", "123")]
        [InlineData("kind", "views")]
        public void Query_model_rejects_names_and_shapes_not_in_its_contract(string field, object value)
        {
            var args = new JObject { [field] = JToken.FromObject(value) };
            Assert.NotEmpty(Errors("horizun_query_model", args));
            Assert.Contains(field, UndeclaredArguments.Find("horizun_query_model", args));
        }

        [Theory]
        [InlineData("limit", 20)]
        [InlineData("parameters", "Mark")]
        public void List_elements_rejects_query_filters_and_limit_alias(string field, object value)
        {
            var args = new JObject { ["category"] = "OST_Walls", [field] = JToken.FromObject(value) };
            Assert.NotEmpty(Errors("horizun_list_elements", args));
            Assert.Contains(field, UndeclaredArguments.Find("horizun_list_elements", args));
        }

        [Fact]
        public void Correct_query_and_list_names_and_types_pass()
        {
            var query = new JObject
            {
                ["element_ids"] = new JArray(123),
                ["max_rows"] = 20
            };
            var list = new JObject
            {
                ["category"] = "OST_Walls",
                ["max_rows"] = 20
            };

            Assert.Empty(Errors("horizun_query_model", query));
            Assert.Empty(Errors("horizun_list_elements", list));
        }
    }
}
