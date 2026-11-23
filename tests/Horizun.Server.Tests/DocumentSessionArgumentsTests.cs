using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class DocumentSessionArgumentsTests
    {
        [Theory]
        [InlineData("save", "audit")]
        [InlineData("save_as", "save_on_close")]
        [InlineData("close", "overwrite")]
        [InlineData("inspect", "detach")]
        public void Inapplicable_arguments_are_not_ignored(string operation, string field)
        {
            var request = new JObject { ["operation"] = operation, [field] = false };
            Assert.Contains(field, ToolInputRules.ValidateSession(request, operation));
        }
        [Theory]
        [InlineData("save")]
        [InlineData("save_as")]
        [InlineData("close")]
        [InlineData("inspect")]
        public void Dry_run_is_an_explicit_supported_operation_argument(string operation)
        {
            Assert.Null(ToolInputRules.ValidateSession(new JObject { ["operation"] = operation, ["dry_run"] = true }, operation));
        }
        [Fact]
        public void Open_rejects_an_unsupported_dry_run_before_execution()
        {
            Assert.Contains("does not support", ToolInputRules.ValidateSession(new JObject { ["dry_run"] = true }, "open"));
        }
        [Fact]
        public void Published_contract_has_ten_disjoint_operation_variants()
        {
            var variants = (JArray)Contract.Find("horizun_document_session").InputSchema["oneOf"];
            Assert.Equal(10, variants.Count);
            foreach (var variant in variants)
                Assert.False((bool)variant["additionalProperties"]);
        }
        [Fact]
        public void Activate_names_target_rehearses_and_accepts_save_as_token()
        {
            var schema = Contract.Find("horizun_document_session").InputSchema;
            var variant = ((JArray)schema["oneOf"]).Single(v => (string)v["properties"]["operation"]["const"] == "activate");
            Assert.Contains("target_document", variant["required"].Select(t => (string)t));
            Assert.True((bool)variant["properties"]["dry_run"]["default"]);
            Assert.Null(ToolInputRules.ValidateSession(new JObject { ["operation"] = "activate", ["target_document"] = "A", ["save_as_path"] = "C:/local/a.rvt", ["confirmation_token"] = "token" }, "activate"));
            Assert.Contains("overwrite", ToolInputRules.ValidateSession(new JObject { ["operation"] = "activate", ["overwrite"] = true }, "activate"));
        }
        [Fact]
        public void Sync_with_central_names_its_target_and_takes_only_its_own_arguments()
        {
            var sync = ((JArray)Contract.Find("horizun_document_session").InputSchema["oneOf"])
                .Single(v => (string)v["properties"]["operation"]["const"] == "sync_with_central");
            Assert.Contains("target_document", sync["required"].Select(t => (string)t));
            // A sync previews when dry_run is omitted; the shared base property says false.
            Assert.True((bool)sync["properties"]["dry_run"]["default"]);
            Assert.Equal(new[] { "all", "keep_borrowed", "borrowed_only", "none" },
                sync["properties"]["relinquish"]["enum"].Select(t => (string)t).ToArray());
            Assert.Null(ToolInputRules.ValidateSession(new JObject
            {
                ["operation"] = "sync_with_central", ["target_document"] = "A", ["relinquish"] = "none",
                ["comment"] = "c", ["compact"] = true, ["dry_run"] = true
            }, "sync_with_central"));
            Assert.Contains("file_path", ToolInputRules.ValidateSession(new JObject
            { ["operation"] = "sync_with_central", ["file_path"] = "A" }, "sync_with_central"));
            Assert.Contains("detach", ToolInputRules.ValidateSession(new JObject
            { ["operation"] = "sync_with_central", ["target_document"] = "A", ["detach"] = true }, "sync_with_central"));
        }
        [Fact]
        public void New_project_takes_its_target_template_and_token_and_rehearses_by_default()
        {
            // Course dry run 2026-09-30, defect #18: there was no typed way to create a blank project.
            var schema = Contract.Find("horizun_document_session").InputSchema;
            Assert.Contains("new_project", schema["properties"]["operation"]["enum"].Select(t => (string)t));
            var variant = ((JArray)schema["oneOf"])
                .Single(v => (string)v["properties"]["operation"]["const"] == "new_project");
            Assert.Contains("save_as_path", variant["required"].Select(t => (string)t));
            Assert.True((bool)variant["properties"]["dry_run"]["default"]);
            Assert.NotNull(variant["properties"]["template_path"]);
            Assert.NotNull(variant["properties"]["confirmation_token"]);

            Assert.Null(ToolInputRules.ValidateSession(new JObject
            {
                ["operation"] = "new_project", ["save_as_path"] = "C:/P/Nuevo.rvt",
                ["template_path"] = "C:/T/a.rte", ["dry_run"] = true
            }, "new_project"));
            Assert.Null(ToolInputRules.ValidateSession(new JObject
            {
                ["operation"] = "new_project", ["save_as_path"] = "C:/P/Nuevo.rvt",
                ["dry_run"] = false, ["confirmation_token"] = "t", ["idempotency_key"] = "k"
            }, "new_project"));
        }

        [Theory]
        [InlineData("overwrite")]          // there is no overwrite for new_project, by design
        [InlineData("target_document")]
        [InlineData("expected_version")]
        [InlineData("compact")]
        public void New_project_refuses_arguments_that_are_not_its_own(string field)
        {
            var request = new JObject { ["operation"] = "new_project", ["save_as_path"] = "C:/P/Nuevo.rvt", [field] = true };
            Assert.Contains(field, ToolInputRules.ValidateSession(request, "new_project"));
        }

        [Fact]
        public void Profiles_have_three_array_dimensions_and_categories_reject_ignored_fields()
        {
            var item=Contract.Find("horizun_create_elements").InputSchema["properties"]["elements"]["items"];
            var floor=((JArray)item["oneOf"]).Single(v=>(string)v["properties"]["kind"]["const"]=="floor");
            var profile=floor["properties"]["profile"];
            Assert.Equal("array",(string)profile["type"]);
            Assert.Equal("array",(string)profile["items"]["type"]);
            Assert.Equal("array",(string)profile["items"]["items"]["type"]);
            Assert.Equal("number",(string)profile["items"]["items"]["items"]["type"]);
            Assert.Contains("height",ToolInputRules.ValidateCreation(new JObject { ["kind"]="ceiling",["height"]=9 },"ceiling"));
            Assert.Contains("coordinate_mode",ToolInputRules.ValidateCreation(new JObject { ["kind"]="family_instance" },"family_instance"));
        }
    }
}
