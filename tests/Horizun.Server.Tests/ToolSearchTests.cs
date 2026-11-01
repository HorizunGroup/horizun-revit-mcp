// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_list_tools { query } (facade mode). A small model finds the right tool by
// DESCRIBING what it wants, in Spanish or English, with or without accents. These tests
// hold: the right tool ranks near the top for the typical Revit tasks; accents never
// matter; a tool outside the active toolsets is reported ONLY in matches_not_active and
// never as callable; the permission profile is never widened; query and toolset combine;
// and a call without query answers exactly what it answered before the search existed.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class ToolSearchTests
    {
        private const string FullWrite = "{\"permission_profile\":\"full_write\"}";

        private static string[] Matches(JObject r, string field = "matches") =>
            ((JArray)r[field]).Select(m => (string)m["name"]).ToArray();

        private static JObject Search(string query, string toolset = null)
        {
            var args = new JObject { ["query"] = query };
            if (toolset != null) args["toolset"] = toolset;
            return SchemaFacade.ListTools(args);
        }

        private static void AssertInTop(string query, int top, params string[] expectedAny)
        {
            SchemaFacadeTests.WithDataRoot(FullWrite, null, () =>
            {
                string[] got = Matches(Search(query)).Take(top).ToArray();
                Assert.True(got.Intersect(expectedAny).Any(),
                    "query [" + query + "]: expected one of [" + string.Join(", ", expectedAny) + "] in the top " + top +
                    ", got [" + string.Join(", ", got) + "]");
            });
        }

        [Fact]
        public void How_many_walls_finds_the_reading_tools()
            => AssertInTop("cuántos muros hay", 3, "horizun_list_elements", "horizun_query_model");

        [Fact]
        public void Delete_element_in_either_language_finds_delete_verified_first()
        {
            AssertInTop("borrar elemento", 1, "horizun_delete_verified");
            AssertInTop("delete element", 1, "horizun_delete_verified");
            AssertInTop("eliminar un muro", 3, "horizun_delete_verified");
        }

        [Fact]
        public void Changing_comments_of_given_ids_finds_write_params_verified()
            => AssertInTop("cambiar comentarios de estos IDs", 2, "horizun_write_params_verified");

        [Fact]
        public void Create_a_wall_finds_create_elements()
            => AssertInTop("crea un muro de 3 m", 2, "horizun_create_elements");

        [Fact]
        public void Capture_a_view_finds_capture_view()
        {
            AssertInTop("capturar vista", 2, "horizun_capture_view");
            AssertInTop("take a screenshot of the view", 3, "horizun_capture_view");
        }

        [Fact]
        public void Accents_never_change_the_answer()
        {
            SchemaFacadeTests.WithDataRoot(FullWrite, null, () =>
            {
                foreach (var pair in new[] { ("parámetro", "parametro"), ("habitación", "habitacion"),
                                             ("Tubería", "tuberia"), ("¿Cuántos MUROS hay?", "cuantos muros hay") })
                    Assert.Equal(Matches(Search(pair.Item1)), Matches(Search(pair.Item2)));
                Assert.NotEmpty(Matches(Search("habitacion")));
                Assert.NotEmpty(Matches(Search("parametro")));
            });
        }

        [Fact]
        public void Matches_are_ranked_capped_short_and_carry_name_summary_and_toolsets()
        {
            SchemaFacadeTests.WithDataRoot(FullWrite, null, () =>
            {
                JObject r = Search("view");
                JArray m = (JArray)r["matches"];
                Assert.InRange(m.Count, 1, 8);
                foreach (JToken t in m)
                {
                    Assert.True(((string)t["summary"]).Length <= 200);
                    Assert.NotEmpty((JArray)t["toolsets"]);
                    Assert.NotNull(Tools.Find((string)t["name"]));
                }
                double[] scores = m.Select(t => (double)t["score"]).ToArray();
                Assert.Equal(scores.OrderByDescending(s => s).ToArray(), scores);
                Assert.Equal("view", (string)r["query"]);
            });
        }

        [Fact]
        public void A_tool_outside_the_active_toolsets_appears_only_as_not_active()
        {
            SchemaFacadeTests.WithDataRoot(FullWrite, "read", () =>
            {
                Assert.Null(Tools.Advertisable().FirstOrDefault(t => t.Name == "horizun_delete_verified"));
                JObject r = Search("borrar elemento");
                Assert.DoesNotContain("horizun_delete_verified", Matches(r));
                Assert.DoesNotContain("horizun_delete_verified", ((JObject)r["tools"]).Properties().Select(p => p.Name));
                JToken row = ((JArray)r["matches_not_active"]).FirstOrDefault(m => (string)m["name"] == "horizun_delete_verified");
                Assert.NotNull(row);
                Assert.Contains("model", ((JArray)row["toolsets"]).Select(x => (string)x));
                Assert.NotEmpty((JArray)row["enable_toolsets"]);
            });
        }

        [Fact]
        public void The_permission_profile_is_never_widened_by_the_search()
        {
            SchemaFacadeTests.WithDataRoot("{\"permission_profile\":\"read_only\"}", null, () =>
            {
                JObject r = Search("borrar elemento");
                Assert.DoesNotContain("horizun_delete_verified", Matches(r));
                Assert.DoesNotContain("horizun_delete_verified", Matches(r, "matches_not_active"));
                // Nothing listed as callable is outside what the full list advertises.
                string[] reachable = Tools.List(true).Select(t => (string)t["name"]).ToArray();
                Assert.Empty(Matches(r).Except(reachable));
            });
        }

        [Fact]
        public void Query_and_toolset_combine()
        {
            SchemaFacadeTests.WithDataRoot(FullWrite, null, () =>
            {
                JObject r = Search("borrar elemento", "model");
                Assert.Equal("model", (string)r["toolset"]);
                Assert.Contains("horizun_delete_verified", Matches(r));
                foreach (JToken m in (JArray)r["matches"])
                    Assert.Contains("model", ((JArray)m["toolsets"]).Select(x => (string)x));

                // A toolset the tool is not in removes it.
                JObject other = Search("borrar elemento", "powerbi");
                Assert.DoesNotContain("horizun_delete_verified", Matches(other));
            });
        }

        [Fact]
        public void An_empty_or_stopword_only_query_is_refused_clearly()
        {
            SchemaFacadeTests.WithDataRoot(FullWrite, null, () =>
            {
                foreach (string q in new[] { "", "   ", "de la el", "the and of", "¿?" })
                {
                    var ex = Assert.Throws<ToolRefusal>(() => Search(q));
                    Assert.Contains("query", ex.Message);
                }
                Assert.Throws<ToolRefusal>(() => SchemaFacade.ListTools(new JObject { ["query"] = 5 }));
                Assert.Throws<ToolRefusal>(() => SchemaFacade.ListTools(new JObject { ["query"] = "x", ["bogus"] = 1 }));
            });
        }

        [Fact]
        public void A_query_with_no_match_answers_empty_lists_not_an_error()
        {
            SchemaFacadeTests.WithDataRoot(FullWrite, null, () =>
            {
                JObject r = Search("zzqxv wwkpl");
                Assert.Empty((JArray)r["matches"]);
                Assert.Empty((JArray)r["matches_not_active"]);
            });
        }

        [Fact]
        public void Without_query_the_answer_is_exactly_the_old_shape()
        {
            SchemaFacadeTests.WithDataRoot(FullWrite, null, () =>
            {
                JObject r = SchemaFacade.ListTools(new JObject());
                Assert.Equal(new[] { "mode", "toolset", "tools", "toolsets", "toolsets_not_active", "next",
                                     "contract_hash", "facade_hash" },
                             r.Properties().Select(p => p.Name).ToArray());
                Assert.Null(r["matches"]);
                Assert.Null(r["matches_not_active"]);
            });
        }

        [Fact]
        public void The_list_tools_schema_declares_query_and_the_hashes_stay_distinct()
        {
            // The list_tools schema grew 'query', so SchemaFacade.Hash is a new value; the
            // meta-tools are not contract rows, so Contract.Hash cannot have moved.
            Assert.Contains("\"query\"", SchemaFacade.List(SchemaSurface.Facade, false).ToString());
            Assert.NotEqual(Horizun.Contracts.Contract.Hash, SchemaFacade.Hash);
        }

        [Fact]
        public void Terms_fold_accents_drop_stopwords_numbers_and_short_words_and_stem_plurals()
        {
            Assert.Equal(new[] { "cuanto", "muro" }, ToolSearch.Terms("¿Cuántos MUROS hay de 3 m?").ToArray());
            Assert.Equal(new[] { "habitacion" }, ToolSearch.Terms("habitación").ToArray());
            Assert.Equal(ToolSearch.Terms("habitaciones"), ToolSearch.Terms("HABITACIÓN"));
            Assert.Equal(ToolSearch.Terms("walls"), ToolSearch.Terms("wall"));
            Assert.Empty(ToolSearch.Terms("the of a de la 12"));
        }
    }
}
