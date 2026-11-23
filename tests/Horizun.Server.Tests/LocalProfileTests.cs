using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class LocalProfileTests
    {
        [Fact]
        public void Local_projection_does_not_mutate_the_compact_schema_cache_on_first_use()
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"full_write\"}", null, () =>
            {
                // Reading the cache directly does not attach it to a published JObject.
                // Run this test alone as well to exercise a cold process.
                var schemas = Tools.Advertisable().ToDictionary(t => t.Name,
                    t => Tools.CompactSchema(t.Name, t.InputSchema).ToString(Formatting.None));
                SchemaFacade.List(SchemaSurface.Local, false);
                foreach (var t in Tools.Advertisable())
                    Assert.Equal(schemas[t.Name], Tools.CompactSchema(t.Name, t.InputSchema).ToString(Formatting.None));
            });
        }

        [Theory]
        [InlineData("read_only")]
        [InlineData("safe_write")]
        [InlineData("full_write")]
        [InlineData("unsafe_code")]
        public void Local_surface_fits_4000_tokens_and_preserves_permissions_and_exact_contract(string profile)
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"" + profile + "\"}", null, () =>
            {
                string before = Tools.List(true).ToString(Formatting.None);
                JArray list = SchemaFacade.List(SchemaSurface.Local, true);
                string wire = new JObject { ["tools"] = list }.ToString(Formatting.None);
                Assert.True(ToolsetReport.ApproxTokens(wire.Length) <= 4000, wire.Length.ToString());
                Assert.InRange(list.Count, 7, 11);
                foreach (JObject tool in list)
                {
                    string name = (string)tool["name"];
                    Assert.InRange(((string)tool["description"]).Length, 1, 200);
                    if (SchemaFacade.IsMetaTool(name, SchemaSurface.Local)) continue;
                    Assert.Contains(Tools.Advertisable(), t => t.Name == name);
                    var exact = SchemaFacade.Describe(new JObject { ["name"] = name });
                    Assert.True(JToken.DeepEquals(Horizun.Contracts.Contract.Find(name).InputSchema, exact["input_schema"]));
                }
                Assert.Equal(before, Tools.List(true).ToString(Formatting.None));
                Assert.Contains(list, t => (string)t["name"] == "horizun_list_elements");
                Assert.DoesNotContain(list.First(t => (string)t["name"] == "horizun_query_model")["inputSchema"]["properties"].Children<JProperty>(),
                    p => p.Name == "cooperative");
            }, mode: "local");
        }

        [Theory]
        [InlineData("contar muros", "horizun_list_elements")]
        [InlineData("cuántos muros tiene el modelo", "horizun_list_elements")]
        [InlineData("how many walls are there", "horizun_list_elements")]
        [InlineData("¿Cuántas puertas?", "horizun_list_elements")]
        [InlineData("count walls", "horizun_list_elements")]
        [InlineData("how many doors", "horizun_list_elements")]
        [InlineData("editar parámetros", "horizun_write_params_verified")]
        [InlineData("remove elements", "horizun_delete_verified")]
        [InlineData("create_sheet", "horizun_manage_views")]
        [InlineData("place_view", "horizun_manage_views")]
        [InlineData("tag de puertas visibles", "horizun_annotate")]
        [InlineData("tag de puertas visibles", "horizun_plan_annotations")]
        [InlineData("planta", "horizun_query_planimetry")]
        [InlineData("planta", "horizun_navigate")]
        [InlineData("planta", "horizun_capture_view")]
        [InlineData("crear planchas", "horizun_manage_views")]
        [InlineData("crear láminas", "horizun_manage_views")]
        [InlineData("hojas", "horizun_manage_views")]
        [InlineData("floor plans", "horizun_manage_views")]
        [InlineData("cortes", "horizun_query_planimetry")]
        [InlineData("etiquetas", "horizun_annotate")]
        [InlineData("anotaciones", "horizun_annotate")]
        [InlineData("cotas", "horizun_annotate")]
        [InlineData("dimensions", "horizun_annotate")]
        [InlineData("cantidades", "horizun_quantities")]
        [InlineData("áreas", "horizun_quantities")]
        [InlineData("metrados", "horizun_quantities")]
        [InlineData("takeoff", "horizun_quantities")]
        [InlineData("comentarios", "horizun_write_params_verified")]
        [InlineData("comments", "horizun_write_params_verified")]
        [InlineData("crear planch", "horizun_manage_views")]
        [InlineData("create_sheet zzzzunknown", "horizun_manage_views")]
        [InlineData("niveles", "get_document_info")]
        public void Search_finds_tasks_in_both_languages(string query, string expected)
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"full_write\"}", null, () =>
            {
                var found = SchemaFacade.ListTools(new JObject { ["query"] = query });
                Assert.Single(found.Properties());
                Assert.NotNull(found["tools"][expected]);
                Assert.InRange(((JObject)found["tools"]).Count, 1, 3);
                foreach (var p in ((JObject)found["tools"]).Properties())
                {
                    Assert.Equal(JTokenType.String, p.Value.Type);
                    Assert.DoesNotContain("\n", (string)p.Value);
                }
            });
        }

        [Fact]
        public void Search_and_local_dispatch_do_not_bypass_gates()
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"read_only\"}", "read", () =>
            {
                var found = (JObject)SchemaFacade.ListTools(new JObject { ["query"] = "editar parametros" })["tools"];
                Assert.Null(found["horizun_write_params_verified"]);
                foreach (var match in found.Properties()) Assert.Null(Tools.DisabledReason(match.Name));
                var call = new JObject { ["name"] = SchemaFacade.CallToolName,
                    ["arguments"] = new JObject { ["name"] = "horizun_delete_verified", ["arguments"] = new JObject() } };
                Assert.Equal("horizun_delete_verified", (string)SchemaFacade.UnwrapCall(call, SchemaSurface.Local)["name"]);
                Assert.NotNull(Tools.DisabledReason("horizun_delete_verified"));
            });
        }

        [Fact]
        public void Search_filters_before_ranking_and_does_not_fill_with_unrelated_tools()
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"safe_write\"}", null, () =>
            {
                Assert.Empty((JObject)SchemaFacade.ListTools(new JObject { ["query"] = "zzzzzzzz" })["tools"]);
                var names = ((JObject)SchemaFacade.ListTools(new JObject { ["query"] = "planta" })["tools"]).Properties().Select(p => p.Name).ToArray();
                Assert.Equal(3, names.Length);
                Assert.Equal(names, ((JObject)SchemaFacade.ListTools(new JObject { ["query"] = "PLÁNTA" })["tools"]).Properties().Select(p => p.Name));
                var filtered = (JObject)SchemaFacade.ListTools(new JObject { ["query"] = "view", ["toolset"] = "read" })["tools"];
                Assert.NotEmpty(filtered);
                foreach (var match in filtered.Properties()) Assert.Contains("read", Tools.Find(match.Name).Toolsets);
                var exact = (JObject)SchemaFacade.ListTools(new JObject { ["query"] = "horizun_manage_views" })["tools"];
                Assert.Equal("horizun_manage_views", exact.Properties().First().Name);
            });
        }

        [Fact]
        public void Local_context_is_exposed_without_a_new_tool_and_capture_points_to_real_ids()
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"full_write\"}", null, () =>
            {
                var list = SchemaFacade.List(SchemaSurface.Local, false);
                var info = list.Single(t => (string)t["name"] == "get_document_info");
                Assert.Contains("context", info["inputSchema"]["properties"]["operation"]["enum"].Values<string>());
                Assert.NotNull(info["inputSchema"]["properties"]["offset"]);
                Assert.NotNull(info["inputSchema"]["properties"]["max_rows"]);
                Assert.Contains("operation=context", (string)list.Single(t => (string)t["name"] == "horizun_capture_view")["description"]);
                foreach (string name in new[] { "horizun_capture_view", "horizun_manage_views", "horizun_sheet_layout" })
                    Assert.Contains("First query view_id", Horizun.Contracts.Contract.Find(name).Description);
            }, mode: "local");
        }

        [Theory]
        [InlineData("create_sheet", "horizun_manage_views")]
        [InlineData("place_view", "horizun_manage_views")]
        [InlineData("planta", "horizun_navigate")]
        [InlineData("niveles", "get_document_info")]
        public void Battery_queries_also_work_with_the_local_toolset(string query, string expected)
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"full_write\"}", "local", () =>
            {
                var found = (JObject)SchemaFacade.ListTools(new JObject { ["query"] = query })["tools"];
                Assert.NotNull(found[expected]);
                foreach (var tool in found.Properties()) Assert.Null(Tools.DisabledReason(tool.Name));
                // The optional local TOOLSET deliberately excludes annotation and planimetry.
                // Schema mode=local alone (the battery configuration) does not narrow access.
                Assert.Null(found["horizun_annotate"]);
                Assert.Null(found["horizun_query_planimetry"]);
            }, mode: "local");
        }

        [Fact]
        public void Profile_alias_and_explicit_mode_precedence()
        {
            string saved = Environment.GetEnvironmentVariable("HORIZUN_PROFILE");
            try
            {
                Environment.SetEnvironmentVariable("HORIZUN_PROFILE", "local");
                SchemaFacadeTests.WithDataRoot("{}", null, () => Assert.Equal(SchemaSurface.Local, SchemaMode.Current));
                SchemaFacadeTests.WithDataRoot("{}", null, () => Assert.Equal(SchemaSurface.Full, SchemaMode.Current), mode: "full");
            }
            finally { Environment.SetEnvironmentVariable("HORIZUN_PROFILE", saved); }
        }

        [Fact]
        public void Compact_reads_are_short_and_keep_coverage_images_and_sanitization_findings()
        {
            SchemaFacadeTests.WithDataRoot("{}", null, () =>
            {
                var data = JObject.Parse("{\"category\":\"Muros\",\"total\":43,\"coverage_complete\":false,\"unavailable\":[{\"reason\":\"unloaded link\"}],\"bridge_queue\":{\"verbose\":true},\"rows\":[]}");
                var safety = new ContentSafety.Report { Origin = ContentSafety.OriginModel, SuspectedInstructions = 1 };
                ContentSafety.Attach(data, safety);
                var result = ContentSafety.Finish(McpResult.Structured(data, data.ToString()), safety);
                ((JArray)result["content"]).Add(new JObject { ["type"] = "image", ["data"] = "image-bytes" });
                var compact = CompactResponse.Apply(result, "horizun_list_elements");
                Assert.StartsWith("Muros: 43 (parcial", (string)compact["content"][0]["text"]);
                Assert.True(compact.ToString(Formatting.None).Length < 900);
                Assert.Null(compact["structuredContent"]["bridge_queue"]);
                Assert.Null(compact["structuredContent"]["content_safety"]);
                Assert.Equal(1, (int)compact["structuredContent"]["safety_findings"]["suspected_instructions"]);
                Assert.Equal("image-bytes", (string)compact["content"][1]["data"]);
                Assert.True(JToken.DeepEquals(data["unavailable"], compact["structuredContent"]["unavailable"]));
                Assert.NotNull(result["structuredContent"]["bridge_queue"]); // input unchanged
                Assert.Same(result, CompactResponse.Apply(result, "horizun_delete_verified"));
                Assert.Same(result, CompactResponse.Apply(result, SchemaFacade.DescribeToolName));
                var error = McpResult.Error("failed", null, null, data);
                Assert.Same(error, CompactResponse.Apply(error, "horizun_list_elements"));
            }, mode: "local");
        }

        [Fact]
        public void Ordinary_complete_count_with_null_cooperative_has_a_short_summary()
        {
            SchemaFacadeTests.WithDataRoot("{}", null, () =>
            {
                var data = JObject.Parse("{\"category\":\"Muros\",\"total\":43,\"coverage_complete\":true,\"cooperative\":null,\"rows\":[]}");
                var compact = CompactResponse.Apply(McpResult.Structured(data, data.ToString()), "horizun_list_elements");
                Assert.Equal("Muros: 43", (string)compact["content"][0]["text"]);
                Assert.Equal(43, (int)compact["structuredContent"]["total"]);
            }, mode: "local");
        }

        [Fact]
        public void Explicit_compact_works_in_full_mode_and_full_response_opts_out()
        {
            string saved = Environment.GetEnvironmentVariable("HORIZUN_RESPONSE");
            try
            {
                Environment.SetEnvironmentVariable("HORIZUN_RESPONSE", "compact");
                SchemaFacadeTests.WithDataRoot("{}", null, () => Assert.True(CompactResponse.Enabled), mode: "full");
                Environment.SetEnvironmentVariable("HORIZUN_RESPONSE", "full");
                SchemaFacadeTests.WithDataRoot("{}", null, () => Assert.False(CompactResponse.Enabled), mode: "local");
            }
            finally { Environment.SetEnvironmentVariable("HORIZUN_RESPONSE", saved); }
        }
    }
}
