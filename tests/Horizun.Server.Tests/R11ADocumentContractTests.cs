using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Xunit;

namespace Horizun.Server.Tests
{
    public class R11ADocumentContractTests
    {
        // Tools that do not read or write an open Revit document.
        private static readonly Dictionary<string, string> Exceptions = new Dictionary<string, string>
        {
            ["horizun_job_status"] = "reads job state",
            ["horizun_catalog_lookup"] = "queries an external catalog",
            ["horizun_project_context"] = "manages project context outside Revit",
            ["horizun_exchange"] = "handles exchange manifests",
            ["horizun_information_container"] = "handles information container metadata",
            ["horizun_cde_cloud"] = "queries cloud information",
            ["horizun_excel_read_rows"] = "reads a spreadsheet",
            ["horizun_read_schedule"] = "reads PDF, CAD or supplied primitives in the server",
            ["horizun_excel_write_rows"] = "writes a spreadsheet",
            ["horizun_budget_compare"] = "compares supplied budget data",
            ["horizun_power_bi_push"] = "pushes supplied data to Power BI",
            ["horizun_selection_exchange"] = "handles selection exchange files",
            ["horizun_repair_memory"] = "repairs server memory",
            ["horizun_target"] = "selects a Revit instance",
            ["horizun_health"] = "Revit and bridge session health",
            ["horizun_open_document"] = "opens a document by its own path selector",
            ["horizun_api_help"] = "reflects loaded API assemblies",
            ["horizun_submit_job"] = "dispatches a job whose command validates its document",
            ["horizun_request_python_access"] = "requests a session permission",
            ["horizun_file_info"] = "reads file headers without opening documents",
            ["horizun_acc_upload_status"] = "reads Desktop Connector logs"
        };

        [Fact]
        public void Document_operation_and_mode_variants_allow_the_target_document()
        {
            var missing = new List<string>();
            foreach (var c in Contract.All.Where(c => !Exceptions.ContainsKey(c.Name)))
            {
                foreach (string discriminator in new[] { "operation", "mode" })
                {
                    var values = c.InputSchema?["properties"]?[discriminator]?["enum"] as Newtonsoft.Json.Linq.JArray;
                    if (values == null) continue;
                    foreach (var value in values)
                        if (c.InputSchema?["properties"]?["target_document"] == null)
                            missing.Add(c.Name + "/" + discriminator + "=" + value);
                }
                var variants = c.InputSchema?["oneOf"] as Newtonsoft.Json.Linq.JArray;
                if (variants == null) continue;
                foreach (var variant in variants)
                {
                    var props = variant["properties"];
                    string op = (string)props?["operation"]?["const"] ?? (string)props?["mode"]?["const"];
                    if (op == null) continue;
                    // These session variants select or create a document by path.
                    if (c.Name == "horizun_document_session" && (op == "open" || op == "inspect" || op == "new_project" || op == "batch" || op == "exit_application")) continue;
                    if (props?["target_document"] == null && (bool?)variant["additionalProperties"] == false)
                        missing.Add(c.Name + "/" + op);
                }
            }
            Assert.True(missing.Count == 0, string.Join(", ", missing));
        }
        [Fact]
        public void Revit_commands_declare_the_target_document()
        {
            var missing = Contract.All.Where(c => !Exceptions.ContainsKey(c.Name) &&
                c.InputSchema?["properties"]?["target_document"] == null)
                .Select(c => c.Name).ToArray();
            Assert.True(missing.Length == 0, string.Join(", ", missing));
        }
    }
}
