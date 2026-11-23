using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class ProductFamilyCatalogTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "hz-products-" + Guid.NewGuid().ToString("N"));
        private readonly string kitPath;
        private readonly string familyPath;

        public ProductFamilyCatalogTests()
        {
            Directory.CreateDirectory(dir);
            kitPath = Path.Combine(dir, "generic.hzkit.json");
            familyPath = Path.Combine(dir, "ProductSink.rfa");
            // This is an availability fixture, not a valid RFA. The route must not claim geometry verification.
            File.WriteAllText(familyPath, "user fixture");
            Save(familyPath);
        }

        private void Save(string source)
        {
            File.WriteAllText(kitPath, new JObject
            {
                ["schema"] = TemplateKitTool.Schema, ["template"] = new JObject(), ["mcp"] = new JObject(),
                ["catalog"] = new JArray(new JObject { ["key"] = "GenericSink|One", ["family"] = "GenericSink", ["type"] = "One", ["name_en"] = "Sink", ["category"] = "Plumbing Fixtures" }),
                ["product_families"] = new JArray(new JObject
                {
                    ["key"] = "ProductSink|One", ["family"] = "ProductSink", ["type"] = "One", ["name_en"] = "Sink",
                    ["category"] = "Plumbing Fixtures", ["source_path"] = source,
                    ["product"] = new JObject { ["manufacturer"] = "Example", ["model"] = "S1" }
                })
            }.ToString());
        }

        private JObject Find(bool products = true) => TemplateKitTool.Handle(new JObject
        {
            ["operation"] = "find", ["path"] = kitPath, ["text"] = "sink", ["product_only"] = products
        }, CancellationToken.None);

        [Fact]
        public void Product_route_uses_existing_typed_load_with_rehearsal_and_requested_type()
        {
            JObject result = Find();
            Assert.Equal(1, (int)result["total"]);
            JToken route = result["hits"][0]["product_family"];
            Assert.Equal("available", (string)route["status"]);
            Assert.Equal("not_measured", (string)route["geometry_quality"]);
            Assert.Equal(familyPath, (string)route["source_path"]);
            // Community edition: loading a family is in the Pro edition, so no typed load is offered.
            Assert.Equal(JTokenType.Null, route["load"].Type);
            Assert.Contains("Pro edition", (string)route["next"]);
            Assert.True((bool)route["resolve_after_load"]["arguments"]["include_types"]);
        }

        [Fact]
        public void Normal_find_prefers_product_when_semantic_scores_tie_and_type_lookup_resolves_it()
        {
            JObject result = Find(false);
            Assert.Equal(2, (int)result["total"]);
            Assert.Equal("ProductSink|One", (string)result["hits"][0]["key"]);
            JObject entry = TemplateKitTool.Handle(new JObject { ["path"] = kitPath, ["operation"] = "type", ["key"] = "ProductSink|One" }, CancellationToken.None);
            Assert.Equal("available", (string)entry["entry"]["product_family"]["status"]);
        }

        [Fact]
        public void Catalog_products_uses_the_same_supplied_kit_and_never_falls_back_to_generic()
        {
            JObject result = CatalogLookup.Handle(new JObject { ["operation"] = "products", ["catalog_path"] = kitPath, ["query"] = "sink" });
            Assert.Equal(1, (int)result["total"]);
            Assert.Equal("products", (string)result["operation"]);
            JObject kit = JObject.Parse(File.ReadAllText(kitPath));
            kit["product_families"] = new JArray(); File.WriteAllText(kitPath, kit.ToString());
            Assert.Empty((JArray)Find()["hits"]);
        }

        [Theory]
        [InlineData("relative.rfa", "invalid_source_path")]
        [InlineData("https://example.test/file.rfa", "invalid_source_path")]
        [InlineData("\\\\example.test\\share\\file.rfa", "invalid_source_path")]
        public void Invalid_routes_are_reported_without_a_load_recipe(string source, string status)
        {
            Save(source);
            JToken route = Find()["hits"][0]["product_family"];
            Assert.Equal(status, (string)route["status"]);
            Assert.Equal(JTokenType.Null, route["file_exists"].Type);
            Assert.Equal(JTokenType.Null, route["load"].Type);
        }

        [Fact]
        public void Missing_file_is_explicit_and_does_not_offer_load()
        {
            Save(Path.Combine(dir, "missing.rfa"));
            JToken route = Find()["hits"][0]["product_family"];
            Assert.Equal("file_missing", (string)route["status"]);
            Assert.False((bool)route["file_exists"]);
            Assert.Equal(JTokenType.Null, route["load"].Type);
        }

        public void Dispose() { Directory.Delete(dir, true); }
    }
}
