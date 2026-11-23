using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class BatchSessionTests
    {
        private static JObject Request(params JObject[] models) => new JObject { ["operation"] = "batch", ["models"] = new JArray(models) };
        private static JObject Model(string path = "D:/local/a.rvt") => new JObject { ["file_path"] = path };

        [Fact]
        public void Minimal_batch_uses_explicitly_reported_defaults()
        {
            Assert.Null(ToolInputRules.ValidateSession(Request(Model()), "batch"));
            var row = ReliabilityDefaults.BatchRow(Model(), "2025", true);
            Assert.True(row.Value<bool>("has_assumptions"));
            Assert.Equal(3, ((JArray)row["assumptions"]).Count);
            Assert.All(((JArray)row["assumptions"]).OfType<JObject>(), a =>
            { Assert.Equal("SUPUESTO", (string)a["flag"]); Assert.NotNull(a["override"]); });
            var variant = ((JArray)Contract.Find("horizun_document_session").InputSchema["oneOf"])
                .Single(v => (string)v["properties"]["operation"]["const"] == "batch");
            Assert.True((bool)variant["properties"]["dry_run"]["default"]);
            Assert.Contains("models", variant["required"].Values<string>());
            Assert.NotNull(variant["properties"]["models"]["items"]["properties"]["actions"]["items"]["properties"]["tool"]);
            Assert.Empty(ContractSchemaCheck.Validate(Request(Model()), Contract.Find("horizun_document_session").InputSchema));
            Assert.Empty(ContractSchemaCheck.Validate(Request(Model()), Tools.CompactSchema("horizun_document_session", Contract.Find("horizun_document_session").InputSchema)));
        }

        [Theory]
        [InlineData("relative.rvt")]
        [InlineData("\\\\server\\share\\a.rvt")]
        [InlineData("D:/local/a.txt")]
        [InlineData("")]
        public void Invalid_sources_are_rejected_before_any_open(string path) => Assert.NotNull(BatchSessionRules.Validate(Request(Model(path))));

        [Fact]
        public void No_duplicate_models_or_save_destinations_or_source_overwrites()
        {
            Assert.NotNull(BatchSessionRules.Validate(Request(Model(), Model("d:\\local\\a.rvt"))));
            var a = Model(); a["close"] = "save"; a["save_as_path"] = "D:/local/b.rvt";
            Assert.NotNull(BatchSessionRules.Validate(Request(a, Model("D:/local/b.rvt"))));
            var b = Model("D:/local/c.rvt"); b["close"] = "save"; b["save_as_path"] = "D:/local/b.rvt";
            Assert.NotNull(BatchSessionRules.Validate(Request(a, b)));
            Assert.Null(BatchSessionRules.Validate(Request(a)));
        }

        [Fact]
        public void Save_allows_explicit_same_source_and_discard_cannot_smuggle_destination()
        {
            var a = Model(); a["close"] = "save";
            Assert.Null(BatchSessionRules.Validate(Request(a)));
            a["save_as_path"] = "D:/local/new.rfa";
            Assert.NotNull(BatchSessionRules.Validate(Request(a)));
            a["close"] = "discard"; a["save_as_path"] = "D:/local/new.rvt";
            Assert.NotNull(BatchSessionRules.Validate(Request(a)));
        }

        [Fact]
        public void Invalid_shapes_and_ignored_arguments_are_rejected()
        {
            Assert.NotNull(BatchSessionRules.Validate(Request()));
            var a = Model(); a["detach"] = "false";
            Assert.NotNull(BatchSessionRules.Validate(Request(a)));
            a.Remove("detach"); a["actions"] = new JArray("python");
            Assert.NotNull(BatchSessionRules.Validate(Request(a)));
            a.Remove("actions"); a["allow_upgrade"] = true;
            Assert.NotNull(BatchSessionRules.Validate(Request(a)));
            Assert.NotNull(ToolInputRules.ValidateSession(new JObject { ["operation"] = "batch", ["file_path"] = "D:/a.rvt" }, "batch"));
        }

        [Theory]
        [InlineData("horizun_model_scan", true)]
        [InlineData("horizun_execute_python", false)]
        [InlineData("horizun_document_session", false)]
        [InlineData("horizun_create_elements", false)]
        [InlineData("unknown", false)]
        public void Reads_cannot_bypass_mutation_or_session_permission(string tool, bool allowed)
        {
            var model = Model();
            model["reads"] = new JArray(new JObject { ["tool"] = tool, ["arguments"] = new JObject() });
            Assert.Equal(allowed, BatchSessionRules.Validate(Request(model)) == null);
        }

        [Theory]
        [InlineData("read", true)]
        [InlineData("override_report", true)]
        [InlineData("graphics_report", true)]
        [InlineData("set_overrides", false)]
        [InlineData("copy_view_graphics", false)]
        public void Only_registered_read_operations_of_mixed_tools_are_admitted(string operation, bool allowed)
        {
            var model = Model();
            model["reads"] = new JArray(new JObject { ["tool"] = "horizun_view_visibility", ["arguments"] = new JObject { ["operation"] = operation } });
            Assert.Equal(allowed, BatchSessionRules.Validate(Request(model)) == null);
        }

        [Fact]
        public void Batch_apply_envelope_matches_compact_schema_including_same_source_save()
        {
            var model = Model(); model["close"] = "save";
            model["actions"] = JArray.Parse("[{'key':'graphics','tool':'horizun_view_visibility','arguments':{'operation':'copy_view_graphics','view_id':1,'view_ids':[2]}}]");
            var request = Request(model); request["dry_run"] = false; request["confirmation_token"] = "token"; request["idempotency_key"] = "new-key";
            Assert.Null(BatchSessionRules.Validate(request));
            var full = Contract.Find("horizun_document_session").InputSchema;
            Assert.Empty(ContractSchemaCheck.Validate(request, full));
            Assert.Empty(ContractSchemaCheck.Validate(request, Tools.CompactSchema("horizun_document_session", full)));
            request["models"][0]["actions"][0]["arguments"] = "not an object";
            Assert.NotEmpty(ContractSchemaCheck.Validate(request, Tools.CompactSchema("horizun_document_session", full)));
        }
    }
}
