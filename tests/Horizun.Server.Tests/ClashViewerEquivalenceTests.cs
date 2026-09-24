// -----------------------------------------------------------------------------
// Horizun Server tests - the MCP App and the text say the same thing.
// Original Horizun code.
//
// NOT EXECUTED YET. Written 2026-09-15 during an implementation-only phase.
//
// G05's acceptance is "mismo resultado en ambos modos", and that is the clause a
// viewer quietly breaks: it fetches a little extra, or it formats a number
// differently, or it silently drops the rows it could not parse - and now the
// picture and the sentence disagree, with no way to tell which is wrong.
//
// So the property under test is not "the app renders". It is that the app's
// SOURCE cannot reach anything the textual renderer did not have: no network, no
// second lookup, the same field names, the same fallbacks, the same order.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ClashViewerEquivalenceTests
    {
        private static JObject Payload() => JObject.Parse(@"{
          ""coverage"": ""two of three links were loaded; the third was not read"",
          ""clashes"": [
            { ""a"": { ""element_id"": 111, ""document"": ""HOST.rvt"", ""category"": ""Walls"" },
              ""b"": { ""element_id"": 222, ""document"": ""MEP.rvt"", ""category"": ""Ducts"" },
              ""kind"": ""intersection"", ""overlap"": 0.42 },
            { ""a_element_id"": 333, ""a_category"": ""Structural Framing"",
              ""b_element_id"": 444, ""b_document"": ""MEP.rvt"",
              ""kind"": ""clearance"", ""distance"": 12 }
          ]
        }");

        [Fact]
        public void The_text_renders_every_clash_the_payload_carries()
        {
            string text = McpAppResources.Text(Payload());
            Assert.Contains("Clashes: 2", text);
            Assert.Contains("111", text);
            Assert.Contains("222", text);
            Assert.Contains("333", text);
            Assert.Contains("444", text);
        }

        [Fact]
        public void A_side_whose_document_could_not_be_read_says_so_rather_than_defaulting_to_the_host()
        {
            // The second row's A side has no document. Two elements with the same id in
            // two documents are a different finding, so silence must not read as "host".
            string text = McpAppResources.Text(Payload());
            Assert.Contains("document unknown", text);
            Assert.Contains("document unknown", McpAppResources.Html());
        }

        [Fact]
        public void An_empty_scope_is_not_reported_as_a_clean_model()
        {
            string text = McpAppResources.Text(new JObject { ["clashes"] = new JArray() });
            Assert.Contains("no clashes in the measured scope", text);
            Assert.Contains("not the same as none in the model", text);
        }

        [Fact]
        public void The_app_reads_the_same_field_names_and_the_same_fallbacks_as_the_text()
        {
            // Both renderers accept the nested shape and the flat one, and both fall back
            // through the same list of array keys. A divergence here is exactly how the
            // picture and the sentence come to disagree.
            string html = McpAppResources.Html();
            foreach (string field in new[]
                     {
                         "clashes", "results", "rows", "findings",
                         "element_id", "document", "document_title", "category", "category_name",
                         "overlap", "overlap_volume", "distance", "coverage"
                     })
                Assert.Contains(field, html);
        }

        [Fact]
        public void The_app_fetches_nothing()
        {
            // The guarantee that makes the equivalence hold: the app cannot obtain a fact
            // the textual result did not contain, because it has no way to ask anyone.
            string html = McpAppResources.Html();
            foreach (string forbidden in new[] { "fetch(", "XMLHttpRequest", "WebSocket", "import(", "<script src=" })
                Assert.DoesNotContain(forbidden, html, StringComparison.Ordinal);
        }

        [Fact]
        public void The_app_is_served_as_a_resource_and_declared_by_the_clash_tool()
        {
            JObject definition = McpAppResources.Definition();
            Assert.Equal(McpAppResources.ClashViewerUri, (string)definition["uri"]);
            Assert.Equal(McpAppResources.AppMimeType, (string)definition["mimeType"]);

            JObject read = McpResources.Read(new JObject { ["uri"] = McpAppResources.ClashViewerUri });
            Assert.Equal(McpAppResources.AppMimeType, (string)read["contents"][0]["mimeType"]);

            JObject listed = ((JArray)McpResources.List(null)["resources"]).OfType<JObject>()
                .FirstOrDefault(r => (string)r["uri"] == McpAppResources.ClashViewerUri);
            Assert.NotNull(listed);
        }

        [Fact]
        public void Only_the_clash_tool_declares_an_app()
        {
            // An app attached to a tool whose payload it cannot render is a blank panel
            // the user blames their client for.
            foreach (JObject tool in Tools.List(true).OfType<JObject>())
            {
                JToken ui = tool["_meta"]?["ui"];
                if ((string)tool["name"] == "horizun_clash")
                    Assert.Equal(McpAppResources.ClashViewerUri, (string)ui?["resourceUri"]);
                // The impact preview declares itself on the five bulk writes it can read,
                // under the same rule; ImpactPreviewAppTests owns that list.
                else if (ImpactPreviewApp.Renders((string)tool["name"]))
                    Assert.Equal(ImpactPreviewApp.Uri, (string)ui?["resourceUri"]);
                else
                    Assert.Null(ui);
            }
        }
    }
}
