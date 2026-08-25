// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// What an MCP CLIENT sees of horizun_fix_planimetry - the last hop, and the one
// a client branches on before deciding whether to ask a human.
//
// The auditor and the corrector must NOT look alike here. The auditor advertises
// readOnlyHint=true and a client may call it freely; the corrector writes, and
// every hint that says so has to be right:
//
//   * readOnlyHint FALSE - it changes the model.
//   * idempotentHint TRUE - it is backed by the durable at-most-once ledger, so
//     a retry of an identical apply replays rather than writing twice. This is
//     the hint a client uses to decide whether a timeout is safe to retry, and
//     answering it wrongly in either direction is expensive: false would make a
//     client refuse a safe retry, true without the ledger would licence a second
//     write.
//   * openWorldHint FALSE - it touches the model and nothing outside it.
//
// And the visibility rule: a read-only machine must not be OFFERED a tool it
// would be refused for calling.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class PlanimetryFixToolListTests
    {
        private const string Tool = "horizun_fix_planimetry";

        private static JObject Entry(string tool) =>
            (JObject)Tools.List().FirstOrDefault(t => (string)t["name"] == tool);

        [Fact]
        public void It_is_advertised_on_a_fresh_install_with_write_annotations()
        {
            WithDataRoot(null, () =>
            {
                JObject entry = Entry(Tool);
                Assert.True(entry != null, Tool + " is missing from tools/list on a fresh install");

                JObject annotations = (JObject)entry["annotations"];
                Assert.False((bool)annotations["readOnlyHint"],
                    "fix_planimetry writes to the model; readOnlyHint=true would tell a client it is free to call");
                Assert.False((bool)annotations["destructiveHint"],
                    "every operation is a reversible property write or one added instance");
                Assert.True((bool)annotations["idempotentHint"],
                    "an identical apply replays through the durable ledger instead of writing twice");
                Assert.False((bool)annotations["openWorldHint"],
                    "it changes the model and nothing outside it");
            });
        }

        [Fact]
        public void The_auditor_and_the_corrector_do_not_advertise_the_same_safety()
        {
            WithDataRoot(null, () =>
            {
                bool auditIsReadOnly = (bool)Entry("horizun_audit_planimetry")["annotations"]["readOnlyHint"];
                bool fixIsReadOnly = (bool)Entry(Tool)["annotations"]["readOnlyHint"];
                Assert.True(auditIsReadOnly);
                Assert.False(fixIsReadOnly);
            });
        }

        [Fact]
        public void A_read_only_machine_is_not_offered_a_tool_that_writes()
        {
            WithDataRoot("{\"permission_profile\":\"read_only\"}", () =>
            {
                Assert.Null(Entry(Tool));
                Assert.NotNull(Tools.DisabledReason(Tool));
                // ...while the read-only half of the surface stays available.
                Assert.NotNull(Entry("horizun_audit_planimetry"));
                Assert.NotNull(Entry("horizun_query_planimetry"));
            });
        }

        [Fact]
        public void It_is_advertised_from_safe_write_upward()
        {
            foreach (string profile in new[] { "safe_write", "full_write", "unsafe_code" })
                WithDataRoot("{\"permission_profile\":\"" + profile + "\"}", () =>
                    Assert.True(Entry(Tool) != null,
                        Tool + " must be advertised under permission_profile=" + profile));
        }

        [Fact]
        public void An_explicit_denial_removes_exactly_this_tool()
        {
            WithDataRoot("{\"denied_tools\":[\"" + Tool + "\"]}", () =>
            {
                Assert.Null(Entry(Tool));
                Assert.NotNull(Tools.DisabledReason(Tool));
                Assert.NotNull(Entry("horizun_audit_planimetry"));
            });
        }

        [Fact]
        public void The_published_schema_carries_the_operations_the_findings_and_the_dry_run()
        {
            WithDataRoot(null, () =>
            {
                JObject schema = (JObject)Entry(Tool)["inputSchema"];
                Assert.Equal("object", (string)schema["type"]);
                Assert.False((bool)schema["additionalProperties"]);

                string[] operations = schema["properties"]["actions"]["items"]["properties"]["operation"]["enum"]
                    .Select(t => (string)t).ToArray();
                Assert.Equal(9, operations.Length);
                Assert.Contains("set_view_template", operations);
                Assert.Contains("place_title_block", operations);
                Assert.Contains("set_crop", operations);
                // The later phases must not appear as callable operations.
                Assert.DoesNotContain("pack_sheet", operations);
                Assert.DoesNotContain("auto_tag", operations);

                Assert.True((bool)schema["properties"]["dry_run"]["default"]);
                Assert.NotNull(schema["properties"]["confirmation_token"]);
                Assert.NotNull(schema["properties"]["idempotency_key"]);
                Assert.NotNull(schema["properties"]["actions"]["items"]["properties"]["finding"]);

                // No file path anywhere on this surface, exactly as on the auditor.
                Assert.Null(schema["properties"]["requirement_set_path"]);
                Assert.NotNull(schema["properties"]["requirement_set"]);
            });
        }

        [Fact]
        public void The_advertised_description_is_within_the_context_budget_and_points_at_the_contract()
        {
            WithDataRoot(null, () =>
            {
                string description = (string)Entry(Tool)["description"];
                Assert.True(description.Length <= 900, "description is " + description.Length);
                Assert.Contains("horizun://contract/tools", description);
            });
        }

        private static void WithDataRoot(string settingsJson, Action action)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string temp = Path.Combine(Path.GetTempPath(), "hz-fixplan-list-" + Guid.NewGuid().ToString("N"));
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
