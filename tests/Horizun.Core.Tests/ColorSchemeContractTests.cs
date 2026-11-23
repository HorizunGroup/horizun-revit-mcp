// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The advertised contract of the colour fill scheme operations of horizun_manage_styles.
// The command's own vocabulary (ColorSchemeRules) and the schema a client reads are two
// copies of the same fact; these tests make them meet, so an operation cannot be added
// to one and forgotten in the other.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ColorSchemeContractTests
    {
        private static JObject Schema(string tool) => Contract.All.Single(c => c.Name == tool).InputSchema;

        private static string[] EnumOf(JToken node) => ((JArray)node["enum"]).Select(t => (string)t).ToArray();

        [Fact]
        public void The_styles_tool_lists_the_colour_scheme_operations_and_their_arguments()
        {
            JObject schema = Schema("horizun_manage_styles");
            string[] operations = EnumOf(schema["properties"]["operation"]);
            foreach (string op in new[] { ColorSchemeRules.OpList }.Concat(ColorSchemeRules.WriteOperations))
                Assert.Contains(op, operations);
            foreach (string name in new[] { "scheme_id", "parameter", "by_range", "include_links", "title", "entries", "view_id", "point" })
                Assert.True(schema["properties"][name] != null, "horizun_manage_styles is missing '" + name + "'");
            // the pre-existing operations are all still there
            foreach (string op in new[] { "list_object_styles", "set_object_style", "create_subcategory", "list_line_styles",
                                          "create_line_style", "list_line_patterns", "create_line_pattern", "list_fill_patterns", "create_fill_pattern" })
                Assert.Contains(op, operations);
        }

        [Fact]
        public void A_scheme_entry_asks_for_a_value_and_a_colour_and_nothing_unnamed()
        {
            JToken entry = Schema("horizun_manage_styles")["properties"]["entries"]["items"];
            Assert.Equal(new[] { "value", "color" }, ((JArray)entry["required"]).Select(t => (string)t).ToArray());
            Assert.Equal(new[] { "caption", "color", "pattern", "value", "visible" },
                         ((JObject)entry["properties"]).Properties().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.False((bool)entry["additionalProperties"]);
        }
    }
}
