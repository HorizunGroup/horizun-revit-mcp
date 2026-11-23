// The resource templates serve the contract's own objects: one tool's row, and one
// variant of a discriminated tool. tools/list advertises an abridged copy, so these
// are where the exact schema lives; every fact below compares against Contract.* so
// a template can never quietly serve something weaker than what a call is checked by.
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using Horizun.Contracts;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ContractTemplateTests
    {
        private static JObject ReadJson(string uri)
            => JObject.Parse((string)McpResources.Read(new JObject { ["uri"] = uri })["contents"][0]["text"]);

        [Fact]
        public void Python_resource_guides_project_creation_and_safe_retries()
        {
            string text = ReadJson("horizun://contract/tools/horizun_execute_python").ToString();
            Assert.Contains("horizun_document_session operation=new_project", text);
            Assert.Contains("a fresh UUID per deliberate write", text);
            Assert.Contains("retry the identical request", text);
            string published = (string)Tools.Publish(Tools.Find("horizun_execute_python"), true)["description"];
            Assert.Contains("horizun_document_session operation=new_project", published);
            Assert.Contains("a fresh UUID per deliberate write", published);
            string recipe = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "python-fallback-recipes.md"));
            Assert.Contains("API by year (2023–2027)", recipe);
            Assert.Contains("str(uuid.uuid4())", recipe);
            Assert.Contains("operation=new_project", recipe);
        }

        private static string RepoRoot([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));

        [Fact]
        public void Templates_list_one_tool_and_one_variant()
        {
            JArray templates = McpResources.Templates();
            Assert.Equal(new[] { "horizun://contract/tools/{tool}", "horizun://contract/tools/{tool}/{variant}" },
                templates.Select(t => (string)t["uriTemplate"]).ToArray());
            foreach (JObject t in templates.Cast<JObject>())
            {
                Assert.Equal("application/json", (string)t["mimeType"]);
                Assert.False(string.IsNullOrWhiteSpace((string)t["name"]));
                Assert.False(string.IsNullOrWhiteSpace((string)t["description"]));
            }
        }

        [Fact]
        public void Every_tool_row_serves_the_contract_input_schema()
        {
            JArray whole = (JArray)JObject.Parse(
                (string)McpResources.Read(new JObject { ["uri"] = "horizun://contract/tools" })["contents"][0]["text"])["tools"];
            foreach (CommandContract c in Contract.All)
            {
                JObject row = ReadJson("horizun://contract/tools/" + c.Name);
                Assert.True(JToken.DeepEquals(c.InputSchema, row["input_schema"]), c.Name);
                // The per-tool read and the whole document share one row builder.
                Assert.True(JToken.DeepEquals(whole.Single(r => (string)r["name"] == c.Name), row), c.Name);
            }
        }

        [Fact]
        public void Sites_are_exactly_the_known_two()
        {
            // Pinned: a new discriminated site changes what the variant template and
            // schema_help serve, so it has to arrive as a deliberate edit of this line.
            var sites = ContractVariants.All
                .Select(s => s.Tool + " " + s.Pointer + " " + s.Discriminator + " " + s.Values.Count).ToArray();
            Assert.Equal(new[]
            {
                "horizun_create_elements /properties/elements/items kind 37",
                "horizun_plan_annotations  operation 11",
                "horizun_document_session  operation 10"
            }, sites);
            foreach (VariantSite s in ContractVariants.All)
                Assert.All(s.Values, v => Assert.Matches("^[a-z0-9_]+$", v));
        }

        [Fact]
        public void Every_variant_serves_the_contract_branch_verbatim()
        {
            foreach (VariantSite s in ContractVariants.All)
            {
                // Walk the contract itself to the site, independently of the finder.
                JToken node = Contract.Find(s.Tool).InputSchema;
                foreach (string seg in s.Segments)
                    node = node is JArray arr ? arr[int.Parse(seg)] : node[seg];
                foreach (string v in s.Values)
                {
                    JObject body = ReadJson("horizun://contract/tools/" + s.Tool + "/" + v);
                    JToken branch = ((JArray)node[s.Combinator])[s.BranchIndex[v]];
                    Assert.True(JToken.DeepEquals(branch, body["schema"]), s.Tool + "/" + v);
                    Assert.Equal((string)branch["properties"][s.Discriminator]["const"], v);
                    Assert.Equal(v, (string)body["value"]);
                    Assert.Equal(s.Discriminator, (string)body["discriminator"]);
                    Assert.Equal("horizun://contract/tools/" + s.Tool, (string)body["shared_arguments_uri"]);
                    // What the variant needs is what the node and its branch both require.
                    foreach (JToken r in (JArray)branch["required"] ?? new JArray())
                        Assert.Contains((string)r, ((JArray)body["required"]).Select(x => (string)x));
                }
            }
        }

        [Fact]
        public void Opening_planner_operation_has_a_readable_contract_uri()
        {
            JObject body = ReadJson("horizun://contract/tools/horizun_plan_annotations/auto_dimension_openings");
            Assert.Equal("operation", (string)body["discriminator"]);
            Assert.Equal("auto_dimension_openings", (string)body["schema"]["properties"]["operation"]["const"]);
            Assert.Contains("view_id", ((JArray)body["required"]).Select(x => (string)x));
            Assert.Contains("horizun_plan_annotations", (string)body["shared_arguments_uri"]);
        }

        [Theory]
        [InlineData("horizun://contract/tools/horizun_nope", "Valid tools: ")]
        [InlineData("horizun://contract/tools/horizun_create_elements/no_such_kind", "Valid values: ")]
        [InlineData("horizun://contract/tools/horizun_health/anything", "has no discriminated variants")]
        [InlineData("horizun://contract/tools/Horizun_Health", "[a-z0-9_]")]
        [InlineData("horizun://contract/tools/horizun_create_elements/wall/extra", "[a-z0-9_]")]
        [InlineData("horizun://contract/tools/", "[a-z0-9_]")]
        public void Unknown_values_are_invalid_params_naming_what_is_valid(string uri, string expected)
        {
            var e = Assert.Throws<McpError>(() => McpResources.Read(new JObject { ["uri"] = uri }));
            Assert.Equal(-32602, e.Code);
            Assert.Contains(expected, e.Message);
        }

        [Fact]
        public void Completion_offers_tools_and_the_chosen_tools_variants()
        {
            JObject tools = McpCompletions.Complete(new JObject
            {
                ["ref"] = new JObject { ["type"] = "ref/resource", ["uri"] = "horizun://contract/tools/{tool}/{variant}" },
                ["argument"] = new JObject { ["name"] = "tool", ["value"] = "" }
            });
            Assert.Equal(new[] { "horizun_create_elements", "horizun_document_session", "horizun_plan_annotations" },
                ((JArray)tools["completion"]["values"]).Select(v => (string)v).OrderBy(v => v).ToArray());

            JObject ops = McpCompletions.Complete(new JObject
            {
                ["ref"] = new JObject { ["type"] = "ref/resource", ["uri"] = "horizun://contract/tools/{tool}/{variant}" },
                ["argument"] = new JObject { ["name"] = "variant", ["value"] = "sa" },
                ["context"] = new JObject { ["arguments"] = new JObject { ["tool"] = "horizun_document_session" } }
            });
            Assert.Contains("save", ((JArray)ops["completion"]["values"]).Select(v => (string)v));
            Assert.DoesNotContain("wall", ((JArray)ops["completion"]["values"]).Select(v => (string)v));
        }
    }
}
