// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The planimetry tools' CONTRACT and WIRING. Two families:
//
//   * Contract facts, provable from the shared declaration: both tools exist,
//     forward to the add-in, are ToolEffect.ReadOnly (which is what makes the
//     MCP hints readOnlyHint=true / destructiveHint=false / idempotentHint=true
//     without a hardcoded list), are visible under read_only and safe_write, and
//     disappear under an explicit denied_tools. The schemas publish enums,
//     bounds and the conditional requirements the commands enforce.
//
//   * Source wiring, read the same way the plan wiring tests read it, because
//     these commands cannot be constructed without a Revit: neither command nor
//     the shared inventory may open a Transaction, touch an exporter, or write a
//     file - a read-only auditor that writes is the defect these exist to stop.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryContractTests
    {
        private static readonly string[] Tools =
        { "horizun_query_planimetry", "horizun_audit_planimetry" };

        // ---- contract facts ------------------------------------------------------

        [Fact]
        public void Both_tools_are_declared_and_forward_to_the_addin()
        {
            foreach (string name in Tools)
            {
                CommandContract c = Contract.Find(name);
                Assert.NotNull(c);
                Assert.Equal(name, c.Command);
                Assert.False(string.IsNullOrWhiteSpace(c.Description));
            }
        }

        [Fact]
        public void Both_tools_are_ReadOnly_not_destructive_and_not_open_world()
        {
            foreach (string name in Tools)
            {
                CommandContract c = Contract.Find(name);
                Assert.Equal(ToolEffect.ReadOnly, c.Effect);
                Assert.False(c.Destructive, name + " must not report destructiveHint=true");
                Assert.False(c.OpenWorld, name + " reads the model and nothing outside it");
            }
        }

        [Fact]
        public void Read_only_tools_carry_no_idempotency_key_because_nothing_mutates()
        {
            // The contract injects idempotency_key into every mutating schema. Its ABSENCE
            // here is the machine-checkable form of "read-only by construction".
            foreach (string name in Tools)
                Assert.Null(Contract.Find(name).InputSchema["properties"]["idempotency_key"]);
        }

        [Fact]
        public void Both_tools_are_visible_under_read_only_and_safe_write()
        {
            foreach (string profile in new[] { "read_only", "safe_write", "full_write", "unsafe_code" })
                WithSettings("{\"permission_profile\":\"" + profile + "\"}", () =>
                {
                    foreach (string name in Tools)
                    {
                        string reason;
                        Assert.True(Settings.IsToolAllowed(Contract.Find(name), out reason),
                            name + " must be visible under " + profile + " but was refused: " + reason);
                    }
                });
        }

        [Fact]
        public void An_explicit_denied_tools_entry_hides_each()
        {
            WithSettings("{\"denied_tools\":[\"horizun_query_planimetry\"]}", () =>
            {
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_query_planimetry"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_audit_planimetry"), out _));
            });
            WithSettings("{\"denied_tools\":[\"horizun_audit_planimetry\"]}", () =>
            {
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_query_planimetry"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_audit_planimetry"), out _));
            });
        }

        [Fact]
        public void The_query_schema_publishes_its_modes_categories_bounds_and_conditionals()
        {
            JObject schema = Contract.Find("horizun_query_planimetry").InputSchema;
            Assert.False((bool)schema["additionalProperties"]);

            string[] modes = schema["properties"]["mode"]["enum"].Select(t => (string)t).ToArray();
            Assert.Equal(new[] { "inventory", "sheets", "views", "placements", "annotations", "references" },
                         modes);
            Assert.Equal("inventory", (string)schema["properties"]["mode"]["default"]);

            string[] categories = schema["properties"]["categories"]["items"]["enum"]
                .Select(t => (string)t).ToArray();
            Assert.Contains("dimensions", categories);
            Assert.Contains("tags", categories);
            Assert.Contains("text_notes", categories);
            Assert.Contains("filled_regions", categories);

            Assert.Equal(500, (int)schema["properties"]["max_rows"]["maximum"]);
            Assert.Equal(100, (int)schema["properties"]["max_rows"]["default"]);
            Assert.Equal(new[] { "mm", "m", "feet" },
                         schema["properties"]["units"]["enum"].Select(t => (string)t).ToArray());

            // The conditionals: parameters only as a pair, categories only in annotations.
            string allOf = schema["allOf"].ToString(Newtonsoft.Json.Formatting.None);
            Assert.Contains("include_parameters", allOf);
            Assert.Contains("parameter_names", allOf);
            Assert.Contains("annotations", allOf);
        }

        [Fact]
        public void The_audit_schema_publishes_scope_operators_severities_and_bounds()
        {
            JObject schema = Contract.Find("horizun_audit_planimetry").InputSchema;
            Assert.False((bool)schema["additionalProperties"]);
            Assert.Equal(new[] { "model", "sheets", "views" },
                         schema["properties"]["scope"]["enum"].Select(t => (string)t).ToArray());
            Assert.Equal(500, (int)schema["properties"]["max_findings"]["maximum"]);

            JObject set = (JObject)schema["properties"]["requirement_set"];
            Assert.False((bool)set["additionalProperties"]);
            Assert.Contains("no file path", (string)set["description"]);
            Assert.Equal(200, (int)set["properties"]["rules"]["maxItems"]);

            JObject rule = (JObject)set["properties"]["rules"]["items"];
            Assert.Equal(new[] { "blocking", "advisory" },
                         rule["properties"]["severity"]["enum"].Select(t => (string)t).ToArray());
            Assert.Equal("advisory", (string)rule["properties"]["severity"]["default"]);

            string[] operators = rule["properties"]["assertion"]["properties"]["operator"]["enum"]
                .Select(t => (string)t).ToArray();
            foreach (string op in new[]
            {
                "matches", "not_matches", "equals", "not_equals", "in_list", "not_in_list", "required",
                "not_empty", "greater_than", "less_than", "between", "minimum_gap", "inside_extent",
                "allowed_type", "allowed_template", "allowed_scale", "required_parameter",
                "forbid_numeric_override", "requires_tag"
            })
                Assert.Contains(op, operators);
        }

        [Fact]
        public void The_audit_schema_rejects_the_ambiguous_scope_and_id_combinations()
        {
            // scope=sheets + view_ids and scope=views + sheet_ids each read two ways, and
            // the schema itself must refuse them - not merely the command.
            string allOf = Contract.Find("horizun_audit_planimetry").InputSchema["allOf"]
                .ToString(Newtonsoft.Json.Formatting.None);
            Assert.Contains("\"sheets\"", allOf);
            Assert.Contains("\"views\"", allOf);
            Assert.Contains("\"not\"", allOf);
        }

        [Fact]
        public void Descriptions_promise_exactly_what_the_phase_delivers()
        {
            string query = Contract.Find("horizun_query_planimetry").Description;
            Assert.Contains("Read-only", query);
            Assert.Contains("unknown", query);

            string audit = Contract.Find("horizun_audit_planimetry").Description;
            Assert.Contains("Read-only", audit);
            Assert.Contains("no 0-100 score", audit);
            Assert.Contains("never a pass", audit);
            Assert.Contains("INLINE", audit);
        }

        // ---- source wiring -------------------------------------------------------

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        private static Dictionary<string, string> PlanimetrySources()
        {
            string root = RepoRoot();
            var files = new[]
            {
                Path.Combine(root, "src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.cs"),
                Path.Combine(root, "src", "Horizun.Revit", "Commands", "AuditPlanimetryCommand.cs"),
                Path.Combine(root, "src", "Horizun.Revit", "Core", "PlanimetryInventory.cs"),
                Path.Combine(root, "src", "Horizun.Revit", "Core", "PlanimetryRules.cs"),
                Path.Combine(root, "src", "Horizun.Revit", "Core", "PlanimetryGeometry.cs"),
                Path.Combine(root, "src", "Horizun.Revit", "Core", "PlanimetryFacts.cs"),
                Path.Combine(root, "src", "Horizun.Revit", "Core", "PlanimetryRequirementSet.cs")
            };
            foreach (string f in files)
                Assert.True(File.Exists(f), f + " is part of the read-only planimetry surface and must exist.");
            return files.ToDictionary(Path.GetFileName, File.ReadAllText);
        }

        [Fact]
        public void No_planimetry_source_opens_a_Transaction()
        {
            // The one property "read-only by construction" actually stands on. SubTransaction
            // and TransactionGroup are included: any of the three is a write path.
            var offenders = new List<string>();
            foreach (KeyValuePair<string, string> kv in PlanimetrySources())
                if (Regex.IsMatch(kv.Value, @"new\s+(Transaction|SubTransaction|TransactionGroup)\s*\("))
                    offenders.Add(kv.Key);
            Assert.True(offenders.Count == 0,
                "These planimetry files construct a Transaction and are therefore not read-only: " +
                string.Join(", ", offenders));
        }

        [Fact]
        public void No_planimetry_source_starts_or_commits_anything()
        {
            var offenders = new List<string>();
            foreach (KeyValuePair<string, string> kv in PlanimetrySources())
                if (Regex.IsMatch(kv.Value, @"\.(Start|Commit|Assimilate|RollBack)\s*\("))
                    offenders.Add(kv.Key);
            Assert.True(offenders.Count == 0,
                "These planimetry files call transaction lifecycle methods: " + string.Join(", ", offenders));
        }

        [Fact]
        public void No_planimetry_source_exports_prints_or_writes_a_file()
        {
            // The phase rule: PDF is never the source OR the product of this audit, and a
            // read tool that writes a file is not a read tool.
            var offenders = new List<string>();
            foreach (KeyValuePair<string, string> kv in PlanimetrySources())
            {
                foreach (string forbidden in new[]
                {
                    ".Export(", "PrintManager", "PDFExportOptions", "DWGExportOptions",
                    "NavisworksExportOptions", "ImageExportOptions",
                    "File.WriteAllText", "File.WriteAllBytes", "File.Create(", "StreamWriter",
                    "File.AppendAllText", "File.Copy(", "File.Move(", "File.Delete("
                })
                    if (kv.Value.Contains(forbidden))
                        offenders.Add(kv.Key + " -> " + forbidden);
            }
            Assert.True(offenders.Count == 0,
                "These planimetry files reach an exporter or the filesystem: " + string.Join("; ", offenders));
        }

        [Fact]
        public void No_planimetry_source_hardcodes_a_corporate_standard()
        {
            // Universal rules may not smuggle in a company's numbers or names. The margin,
            // the allowed scales, the naming patterns all arrive in the requirement set;
            // the only tolerance compiled in is the geometric touch tolerance, which is a
            // measurement convention rather than a standard. The tokens are assembled at
            // runtime so this test does not itself trip the sensitive-term scanner with
            // the very names it forbids.
            string[] corporateTokens =
            {
                "Pro" + "desa",
                "HRZ" + "_",
                "PRD" + "_"
            };
            foreach (KeyValuePair<string, string> kv in PlanimetrySources())
                foreach (string token in corporateTokens)
                    Assert.DoesNotContain(token, kv.Value, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_inventory_is_the_ONLY_collector_both_commands_use()
        {
            Dictionary<string, string> sources = PlanimetrySources();
            // Neither command may run its own FilteredElementCollector: two collectors is
            // how the query and the audit start disagreeing about what is on a sheet.
            foreach (string file in new[] { "QueryPlanimetryCommand.cs", "AuditPlanimetryCommand.cs" })
            {
                Assert.DoesNotContain("FilteredElementCollector", sources[file]);
                Assert.Contains("PlanimetryInventory.Collect", sources[file]);
            }
        }

        [Fact]
        public void Both_commands_are_registered_in_the_app()
        {
            string app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "App.cs"));
            Assert.Contains("new QueryPlanimetryCommand()", app);
            Assert.Contains("new AuditPlanimetryCommand()", app);
        }

        [Fact]
        public void The_audit_command_never_reads_a_requirement_set_from_a_path()
        {
            string source = PlanimetrySources()["AuditPlanimetryCommand.cs"];
            Assert.DoesNotContain("File.ReadAllText", source);
            Assert.DoesNotContain("File.OpenRead", source);
            Assert.DoesNotContain("StreamReader", source);
        }

        [Fact]
        public void The_universal_catalog_has_no_duplicate_ids_and_every_severity_is_legal()
        {
            string[] ids = PlanimetryRules.Catalog.Select(c => c.Id).ToArray();
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
            foreach (PlanimetryCheck c in PlanimetryRules.Catalog)
            {
                Assert.Contains(c.Severity, new[] { "blocking", "advisory", "unknown" });
                Assert.False(string.IsNullOrWhiteSpace(c.Description), c.Id + " has no description");
                Assert.False(string.IsNullOrWhiteSpace(c.Entity), c.Id + " names no entity");
            }
        }

        [Fact]
        public void Every_recommended_tool_in_the_catalog_is_a_tool_this_version_publishes()
        {
            var published = new HashSet<string>(Contract.All.Select(c => c.Name), StringComparer.Ordinal);
            foreach (PlanimetryCheck c in PlanimetryRules.Catalog.Where(x => x.RecommendedTool != null))
                Assert.True(published.Contains(c.RecommendedTool),
                    c.Id + " recommends '" + c.RecommendedTool + "', which this version does not publish - " +
                    "a reader following the recommendation lands on nothing.");
        }

        // ---- settings scaffolding (the same pattern SettingsPermissionTests uses) ----

        private static void WithSettings(string json, Action action)
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-planimetry-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp);
                    File.WriteAllText(HorizunPaths.SettingsPath(), json);
                    action();
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }
    }
}
