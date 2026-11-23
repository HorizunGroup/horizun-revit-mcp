using System;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TelemetryArgumentShapeTests
    {
        [Fact]
        public void Document_open_missing_idempotency_is_explained_by_the_contract()
        {
            var args = new JObject { ["path"] = "D:\\Example\\model.rvt" };
            var schema = Contract.Find("horizun_open_document").InputSchema;
            Assert.Contains(ContractSchemaCheck.Validate(args, schema), e => e.Contains("idempotency_key"));
            args["idempotency_key"] = "example-open";
            Assert.Empty(ContractSchemaCheck.Validate(args, schema));
        }

        [Fact]
        public void Schedule_apply_missing_idempotency_is_explained_by_the_contract()
        {
            var args = JObject.Parse("{category:'OST_Walls',name:'Example schedule',fields:['Count','Family','Type'],group_by:[],itemized:false,target_document:'Example',confirmation_token:'example-token',dry_run:false}");
            var schema = Contract.Find("horizun_create_schedule").InputSchema;
            Assert.Contains(ContractSchemaCheck.Validate(args, schema), e => e.Contains("idempotency_key"));
            args["idempotency_key"] = "example-operation";
            Assert.Empty(ContractSchemaCheck.Validate(args, schema));
            args.Remove("idempotency_key"); args["dry_run"] = true;
            Assert.Empty(ContractSchemaCheck.Validate(args, schema));
        }

        [Fact]
        public void Category_listing_admits_a_document_guard_and_checks_it_before_collecting()
        {
            var args = JObject.Parse("{category:'OST_Walls',target_document:'Example'}");
            Assert.Empty(UndeclaredArguments.Find("horizun_list_elements", args));
            Assert.Empty(ContractSchemaCheck.Validate(args, Contract.Find("horizun_list_elements").InputSchema));
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "ListElementsCommand.cs"));
            int guard = source.IndexOf("DocumentGate.ReadGuard", StringComparison.Ordinal);
            Assert.True(guard >= 0 && guard < source.IndexOf("ResolveCategory(doc", StringComparison.Ordinal));
        }

        [Fact]
        public void View_query_default_is_documented_without_a_contradictory_true_default()
        {
            var field = Contract.Find("horizun_query_model").InputSchema["properties"]["include_links"];
            Assert.Null(field["default"]);
            Assert.Contains("false for scope=view/current_view", (string)field["description"]);
        }
    }
}
