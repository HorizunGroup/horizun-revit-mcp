// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The advertised contract of the schedule appearance / split operations and of the
// colour fill scheme operations. The command's own vocabulary (ScheduleEditRules,
// ColorSchemeRules) and the schema a client reads are two copies of the same fact;
// these tests make them meet, in both directions, so an operation cannot be added to
// one and forgotten in the other.
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
    public class DocumentationSurfaceT2ContractTests
    {
        private static JObject Schema(string tool) => Contract.All.Single(c => c.Name == tool).InputSchema;

        private static JObject ScheduleActionProperties()
            => (JObject)Schema("horizun_manage_schedules")["properties"]["actions"]["items"]["properties"];

        private static string[] EnumOf(JToken node) => ((JArray)node["enum"]).Select(t => (string)t).ToArray();

        [Fact]
        public void Every_operation_the_schedule_command_knows_is_in_its_schema_and_the_other_way_round()
        {
            string[] advertised = EnumOf(ScheduleActionProperties()["operation"]);
            Assert.Equal(ScheduleEditRules.KnownOperations.OrderBy(x => x, StringComparer.Ordinal),
                         advertised.OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void The_schedule_schema_declares_every_argument_of_appearance_and_split()
        {
            JObject properties = ScheduleActionProperties();
            foreach (string name in new[] { "scope", "text_type", "style", "show_grid", "show_title", "parts", "heights_mm" })
                Assert.True(properties[name] != null, "horizun_manage_schedules is missing '" + name + "'");
            Assert.Equal(new[] { "title", "headers", "body", "field" }, EnumOf(properties["scope"]));
        }

        [Fact]
        public void The_style_object_names_exactly_the_attributes_the_rules_accept()
        {
            JObject style = (JObject)ScheduleActionProperties()["style"];
            var declared = ((JObject)style["properties"]).Properties().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(ScheduleAppearanceRules.StyleKeys.OrderBy(x => x, StringComparer.Ordinal), declared);
            Assert.False((bool)style["additionalProperties"]);
        }

        [Fact]
        public void Conditional_formatting_is_not_advertised_because_Revit_has_no_API_for_it()
        {
            string text = Schema("horizun_manage_schedules").ToString().ToLowerInvariant();
            Assert.DoesNotContain("conditional", text);
            string description = Contract.All.Single(c => c.Name == "horizun_manage_schedules").Description;
            Assert.Contains("no API for conditional formatting", description);
        }

        [Fact]
        public void Placing_a_schedule_names_the_segment_of_a_split_one()
        {
            JObject properties = (JObject)Schema("horizun_manage_views")["properties"]["actions"]["items"]["properties"];
            Assert.NotNull(properties["segment_index"]);
            Assert.Equal(0, (int)properties["segment_index"]["minimum"]);
            Assert.Contains("place_schedule", EnumOf(properties["operation"]));
        }
    }
}
