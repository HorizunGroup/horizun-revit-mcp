// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// What an MCP CLIENT sees of the planimetry tools. The Core tests prove the
// contract facts and the Settings decisions; these prove the last hop - the
// tools/list entry a client actually branches on: the annotations that tell a
// client it may call these without asking a human (readOnlyHint=true,
// destructiveHint=false, idempotentHint=true), the published schemas, and the
// visibility under each profile INCLUDING the explicit denial.
//
// The hint assertions matter because they are derived: a regression in the
// contract's effect classification would flip readOnlyHint to false here, and a
// cautious client would start asking a human before every read.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class PlanimetryToolListTests
    {
        private static readonly string[] Names =
        { "horizun_query_planimetry", "horizun_audit_planimetry" };

        private static JObject Entry(string tool) =>
            (JObject)Tools.List().FirstOrDefault(t => (string)t["name"] == tool);

        [Fact]
        public void Both_tools_are_advertised_on_a_fresh_install_with_read_only_annotations()
        {
            WithDataRoot(null, () =>
            {
                foreach (string name in Names)
                {
                    JObject entry = Entry(name);
                    Assert.True(entry != null, name + " is missing from tools/list on a fresh install");
                    JObject annotations = (JObject)entry["annotations"];
                    Assert.True((bool)annotations["readOnlyHint"], name + " must advertise readOnlyHint=true");
                    Assert.False((bool)annotations["destructiveHint"], name + " must advertise destructiveHint=false");
                    Assert.True((bool)annotations["idempotentHint"], name + " must advertise idempotentHint=true");
                    Assert.False((bool)annotations["openWorldHint"], name + " reads the model and nothing outside it");
                    Assert.NotNull(entry["inputSchema"]);
                    Assert.Equal("object", (string)entry["inputSchema"]["type"]);
                    Assert.False((bool)entry["inputSchema"]["additionalProperties"]);
                }
            });
        }

        [Fact]
        public void Both_tools_survive_read_only_and_safe_write()
        {
            foreach (string profile in new[] { "read_only", "safe_write" })
                WithDataRoot("{\"permission_profile\":\"" + profile + "\"}", () =>
                {
                    foreach (string name in Names)
                        Assert.True(Entry(name) != null,
                            name + " must stay advertised under permission_profile=" + profile);
                });
        }

        [Fact]
        public void An_explicit_denied_tools_entry_removes_exactly_the_named_tool()
        {
            WithDataRoot("{\"denied_tools\":[\"horizun_audit_planimetry\"]}", () =>
            {
                Assert.Null(Entry("horizun_audit_planimetry"));
                Assert.NotNull(Tools.DisabledReason("horizun_audit_planimetry"));
                Assert.NotNull(Entry("horizun_query_planimetry"));
            });
        }

        [Fact]
        public void The_published_schemas_carry_the_modes_and_the_inline_requirement_set()
        {
            WithDataRoot(null, () =>
            {
                JObject query = Entry("horizun_query_planimetry");
                string[] modes = query["inputSchema"]["properties"]["mode"]["enum"]
                    .Select(t => (string)t).ToArray();
                Assert.Equal(6, modes.Length);
                Assert.Contains("inventory", modes);
                Assert.Contains("references", modes);

                JObject audit = Entry("horizun_audit_planimetry");
                Assert.NotNull(audit["inputSchema"]["properties"]["requirement_set"]);
                Assert.Null(audit["inputSchema"]["properties"]["requirement_set_path"]);
            });
        }

        private static void WithDataRoot(string settingsJson, Action action)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string temp = Path.Combine(Path.GetTempPath(), "hz-planimetry-list-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, temp);
                if (settingsJson != null) File.WriteAllText(HorizunPaths.SettingsPath(), settingsJson);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
