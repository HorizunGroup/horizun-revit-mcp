using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class PlanimetryProductionToolListTests
    {
        private static JObject Entry(string name) => (JObject)Tools.List().FirstOrDefault(t => (string)t["name"] == name);

        [Fact]
        public void Production_surface_is_visible_with_truthful_effects()
        {
            WithDataRoot(null, () =>
            {
                JObject pack = Entry("horizun_pack_sheets");
                JObject plan = Entry("horizun_plan_annotations");
                JObject revisions = Entry("horizun_manage_revisions");
                Assert.NotNull(pack); Assert.NotNull(plan); Assert.NotNull(revisions);
                Assert.False((bool)pack["annotations"]["readOnlyHint"]);
                Assert.True((bool)plan["annotations"]["readOnlyHint"]);
                Assert.False((bool)revisions["annotations"]["readOnlyHint"]);
                Assert.True((bool)pack["annotations"]["idempotentHint"]);
                Assert.True((bool)revisions["annotations"]["idempotentHint"]);
            });
        }

        [Fact]
        public void Read_only_profile_keeps_planning_and_hides_model_writes()
        {
            WithDataRoot("{\"permission_profile\":\"read_only\"}", () =>
            {
                Assert.NotNull(Entry("horizun_plan_annotations"));
                Assert.Null(Entry("horizun_pack_sheets"));
                Assert.Null(Entry("horizun_manage_revisions"));
            });
        }

        [Fact]
        public void Schemas_publish_the_closed_production_choices()
        {
            WithDataRoot(null, () =>
            {
                JObject pack = (JObject)Entry("horizun_pack_sheets")["inputSchema"];
                Assert.Equal(4, ((JArray)pack["properties"]["items"]["items"]["oneOf"]).Count);
                Assert.True((bool)pack["properties"]["dry_run"]["default"]);

                JObject plan = (JObject)Entry("horizun_plan_annotations")["inputSchema"];
                string[] ops = plan["properties"]["operation"]["enum"].Select(x => (string)x).ToArray();
                Assert.Equal(new[]
                {
                    "auto_tags", "intent_dimension",
                    "auto_dimension_grids", "auto_dimension_levels",
                    "auto_dimension_curtain_walls", "auto_dimension_openings"
                }, ops);
                // auto_dimension_* may sweep a view, so element_ids left the top-level
                // required list - and the two operations that DO need it must still
                // demand it conditionally, or an empty call plans nothing silently.
                string[] required = plan["required"].Select(x => (string)x).ToArray();
                Assert.DoesNotContain("element_ids", required);
                JArray allOf = (JArray)plan["allOf"];
                Assert.Contains(allOf.OfType<JObject>(), c =>
                    (string)c["if"]?["properties"]?["operation"]?["const"] == "auto_tags" &&
                    c["then"]?["required"] != null &&
                    c["then"]["required"].Any(r => (string)r == "element_ids"));
                Assert.Contains(allOf.OfType<JObject>(), c =>
                    (string)c["if"]?["properties"]?["operation"]?["const"] == "intent_dimension" &&
                    c["then"]?["required"] != null &&
                    c["then"]["required"].Any(r => (string)r == "element_ids"));
                Assert.NotNull(plan["properties"]["link_instance_id"]);
                Assert.NotNull(plan["properties"]["chain_separation"]);

                JObject revisions = (JObject)Entry("horizun_manage_revisions")["inputSchema"];
                Assert.NotNull(revisions["properties"]["actions"]["items"]["properties"]["clouds"]);
                Assert.NotNull(revisions["properties"]["idempotency_key"]);
            });
        }

        [Fact]
        public void Visual_review_prompt_requires_direct_model_images()
        {
            JArray prompts = (JArray)McpPrompts.List(null)["prompts"];
            Assert.Contains(prompts.OfType<JObject>(), p => (string)p["name"] == "planimetry-review");
            JObject prompt = McpPrompts.Get(new JObject { ["name"] = "planimetry-review", ["arguments"] = new JObject() });
            string body = (string)prompt["messages"][0]["content"]["text"];
            Assert.Contains("do not export or inspect a PDF", body, StringComparison.Ordinal);
            Assert.Contains("horizun_capture_view", body, StringComparison.Ordinal);
            Assert.Contains("UNKNOWN, never clean", body, StringComparison.Ordinal);
        }

        private static void WithDataRoot(string settingsJson, Action action)
        {
            string old = Environment.GetEnvironmentVariable("HORIZUN_DATA_ROOT");
            string dir = Path.Combine(Path.GetTempPath(), "hz-plan-prod-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", dir);
                if (settingsJson != null) File.WriteAllText(Path.Combine(dir, "settings.json"), settingsJson);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", old);
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
