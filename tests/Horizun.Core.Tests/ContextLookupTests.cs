using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ContextLookupTests
    {
        private static JObject Row(long id, string name, double elevation = 0) => new JObject
        { ["id"] = id, ["name"] = name, ["elevation_m"] = elevation };

        [Theory]
        [InlineData("view", "plánta primer nivel", "Planta primer nivel", 41)]
        [InlineData("level", "Nvel 2", "Nivel 2", 22)]
        public void Missing_name_returns_closest_real_names_and_ids_without_substitution(string kind, string wanted, string closest, long id)
        {
            var detail = ContextLookup.Missing(kind, wanted, new[] {
                Row(1, "Cubierta"), Row(2, "Sótano"), Row(id, closest), Row(3, "Alzado norte") });
            Assert.Equal(wanted, (string)detail["requested"]);
            Assert.Equal(3, ((JArray)detail["suggestions"]).Count);
            Assert.Equal(id, (long)detail["suggestions"][0]["id"]);
            Assert.Equal(closest, (string)detail["suggestions"][0]["name"]);
            var failure = CommandResult.FailCoded(ErrorCodes.ElementoNoEncontrado, ContextLookup.Message(detail), extra: detail);
            Assert.False(failure.Success);
            Assert.Null(failure.Data);
            Assert.Null(failure.Fallback);
            Assert.Contains(closest, failure.Error);
            Assert.Contains("operation=context", failure.Error);
            Assert.NotNull(failure.Detail["suggestions"]);
        }

        [Fact]
        public void Invalid_numeric_id_and_empty_population_never_invent_candidates()
        {
            var detail = ContextLookup.Missing("level", "123456", new[] { Row(20, "Nivel 1"), Row(30, "Nivel 2") });
            Assert.Equal(new long[] { 20, 30 }, detail["suggestions"].Select(r => (long)r["id"]));
            Assert.Empty((JArray)ContextLookup.Missing("view", "inventada", Array.Empty<JObject>())["suggestions"]);
        }

        [Fact]
        public void Context_pages_both_populations_with_totals_units_and_null_level_for_unassociated_views()
        {
            var levels = new[] { Row(30, "Nivel 2", 3), Row(20, "Nivel 1", 0) };
            var views = new[] {
                new JObject { ["id"] = 41, ["name"] = "Planta 1", ["type"] = "FloorPlan", ["level_id"] = 20, ["level_name"] = "Nivel 1" },
                new JObject { ["id"] = 42, ["name"] = "3D", ["type"] = "ThreeD", ["level_id"] = null, ["level_name"] = null },
                new JObject { ["id"] = 43, ["name"] = "Planta 2", ["type"] = "FloorPlan", ["level_id"] = 30, ["level_name"] = "Nivel 2" } };
            var first = ContextLookup.Page("Modelo", levels, views, 0, 2);
            Assert.Equal(20, (long)first["levels"][0]["id"]);
            Assert.Equal("m", (string)first["units"]);
            Assert.Equal(3, (int)first["views_total"]);
            Assert.Equal(2, (int)first["next_offset"]);
            Assert.Equal(JTokenType.Null, first["views"][0]["level_id"].Type);
            var last = ContextLookup.Page("Modelo", levels, views, 2, 2);
            Assert.Empty((JArray)last["levels"]);
            Assert.Single((JArray)last["views"]);
            Assert.Equal(JTokenType.Null, last["next_offset"].Type);
            Assert.False((bool)last["truncated"]);
            // Out-of-range paging is clamped, not refused (field 2026-11-07..10: 6 refusals).
            Assert.Equal(0, (int)ContextLookup.Page("M", levels, views, -1, 2)["offset"]);
            Assert.NotNull(ContextLookup.Page("M", levels, views, 0, 0));
            Assert.NotNull(ContextLookup.Page("M", levels, views, 0, 501));
        }
    }
}
