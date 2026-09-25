// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_catalog_lookup operation=bsdd_*, against a fake HttpMessageHandler. No test reaches the
// network: every request is recorded and answered here, so what is proved is
// what this code sends and what it makes of the answer.
//
//   * the published endpoints and parameter names are the ones requested;
//   * a class's properties come back as loin_property suggestions that never
//     invent an IFC data type;
//   * the cache answers a repeated query without a call, expires, and can be
//     bypassed; the call cap refuses before a request leaves;
//   * without network the reply says so, and serves only an expired cached copy,
//     labelled stale - an unreachable dictionary is never reported as empty.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class BsddLookupTests : IDisposable
    {
        private readonly string _cache;
        private DateTime _now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        public BsddLookupTests()
        {
            _cache = Path.Combine(Path.GetTempPath(), "hz-bsdd-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_cache, true); } catch { }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            public readonly List<Uri> Requests = new List<Uri>();
            public Func<HttpRequestMessage, HttpResponseMessage> Respond;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(request.RequestUri);
                return Task.FromResult(Respond(request));
            }
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
            => new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private BsddLookup.Options Options(FakeHandler handler, BsddLookup.CallBudget budget = null) => new BsddLookup.Options
        {
            Handler = handler,
            CacheDir = _cache,
            UtcNow = () => _now,
            Budget = budget ?? new BsddLookup.CallBudget(30, 500)
        };

        private const string SearchBody = @"{
  ""totalCount"": 2, ""offset"": 0, ""count"": 2,
  ""classes"": [ { ""dictionaryUri"": ""https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1"", ""dictionaryName"": ""Uniclass 2015"",
                   ""name"": ""Doorsets"", ""code"": ""Pr_30_59_24"", ""uri"": ""https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1/class/Pr_30_59_24"",
                   ""classType"": ""Class"", ""status"": ""Active"", ""relatedIfcEntityNames"": [""IfcDoor""] } ],
  ""properties"": [ { ""dictionaryUri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3"", ""dictionaryName"": ""IFC"",
                      ""name"": ""Fire rating"", ""code"": ""FireRating"", ""uri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/prop/FireRating"" } ]
}";

        private const string ClassBody = @"{
  ""dictionaryUri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3"",
  ""code"": ""IfcWall"", ""name"": ""Wall"", ""uri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/class/IfcWall"",
  ""classType"": ""Class"", ""status"": ""Active"", ""definition"": ""Vertical construction."",
  ""relatedIfcEntityNames"": [""IfcWall""],
  ""parentClassReference"": { ""uri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/class/IfcBuiltElement"", ""code"": ""IfcBuiltElement"", ""name"": ""Built element"" },
  ""classProperties"": [
    { ""name"": ""Fire rating"", ""propertyCode"": ""FireRating"", ""propertyUri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/prop/FireRating"",
      ""propertySet"": ""Pset_WallCommon"", ""dataType"": ""String"" },
    { ""name"": ""Status"", ""propertyCode"": ""Status"", ""propertySet"": ""Pset_WallCommon"", ""dataType"": ""String"",
      ""allowedValues"": [ { ""code"": ""NEW"", ""value"": ""New"" }, { ""code"": ""EXISTING"", ""value"": ""Existing"" } ] },
    { ""name"": ""Width"", ""propertyCode"": ""Width"", ""dataType"": ""Real"", ""units"": [""m""], ""minInclusive"": 0 }
  ],
  ""classRelations"": [ { ""relationType"": ""IsEqualTo"", ""relatedClassUri"": ""https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1/class/EF_25_10"", ""relatedClassName"": ""Walls"" } ],
  ""childClassReferences"": [ { ""uri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/class/IfcWallSOLIDWALL"", ""code"": ""IfcWallSOLIDWALL"", ""name"": ""Solid wall"" } ]
}";

        [Fact]
        public void Search_calls_the_published_endpoint_and_a_repeat_is_answered_from_the_cache()
        {
            var handler = new FakeHandler { Respond = r => Json(SearchBody) };
            var args = new JObject
            {
                ["operation"] = "bsdd_search", ["text"] = "door set",
                ["dictionary_uris"] = new JArray("https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1",
                                                 "https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3")
            };
            JObject first = BsddLookup.Handle((JObject)args.DeepClone(), CancellationToken.None, Options(handler));

            Uri sent = handler.Requests.Single();
            Assert.Equal("api.bsdd.buildingsmart.org", sent.Host);
            Assert.Equal("/api/TextSearch/v2", sent.AbsolutePath);
            Assert.Contains("SearchText=door%20set", sent.Query);
            Assert.Equal(2, sent.Query.Split('&').Count(q => q.StartsWith("DictionaryUris=") || q.StartsWith("?DictionaryUris=")));
            Assert.Equal("network", (string)first["source"]);
            Assert.Equal("Pr_30_59_24", (string)first["classes"][0]["code"]);
            Assert.Equal("IfcDoor", (string)first["classes"][0]["related_ifc_entities"][0]);
            Assert.Equal("FireRating", (string)first["properties"][0]["code"]);

            JObject second = BsddLookup.Handle((JObject)args.DeepClone(), CancellationToken.None, Options(handler));
            Assert.Single(handler.Requests);
            Assert.Equal("cache", (string)second["source"]);
            Assert.True(JToken.DeepEquals(first["classes"], second["classes"]));

            // Through the tool's own dispatch: the leaf check is untouched, bsdd_* goes to bSDD.
            Assert.Throws<ArgumentException>(() => CatalogLookup.Handle(new JObject { ["operation"] = "bsdd" }));
        }

        [Fact]
        public void A_class_comes_back_with_properties_relations_and_loin_suggestions_that_do_not_guess_a_type()
        {
            var handler = new FakeHandler { Respond = r => Json(ClassBody) };
            JObject reply = BsddLookup.Handle(new JObject
            {
                ["operation"] = "bsdd_class", ["uri"] = "https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/class/IfcWall"
            }, CancellationToken.None, Options(handler));

            Uri sent = handler.Requests.Single();
            Assert.Equal("/api/Class/v1", sent.AbsolutePath);
            Assert.Contains("IncludeClassProperties=true", sent.Query);
            Assert.Contains("IncludeClassRelations=true", sent.Query);

            Assert.Equal("IfcWall", (string)reply["class"]["code"]);
            Assert.Equal("IfcBuiltElement", (string)reply["class"]["parent"]["code"]);
            Assert.Equal("IsEqualTo", (string)reply["relations"][0]["relation_type"]);
            Assert.Single((JArray)reply["child_classes"]);

            var suggestions = ((JArray)reply["loin_property_suggestions"]).OfType<JObject>().ToList();
            Assert.Equal(3, suggestions.Count);
            foreach (JObject s in suggestions)
            {
                Assert.Null(s["loin_property"]["data_type"]);
                Assert.Contains("data_type", s["still_needed"].Select(t => (string)t));
            }
            JObject fire = suggestions[0];
            Assert.True((bool)fire["complete"]);
            Assert.Equal("Pset_WallCommon", (string)fire["loin_property"]["property_set"]);
            Assert.Equal("FireRating", (string)fire["loin_property"]["name"]);
            Assert.StartsWith("https://identifier.buildingsmart.org/", (string)fire["loin_property"]["uri"]);
            Assert.Equal("String", (string)fire["bsdd_data_type"]);
            Assert.Equal(new[] { "NEW", "EXISTING" }, suggestions[1]["loin_property"]["allowed_values"].Select(t => (string)t));
            JObject width = suggestions[2];
            Assert.False((bool)width["complete"]);                     // bSDD named no property set
            Assert.Contains("property_set", width["still_needed"].Select(t => (string)t));
            Assert.Equal("m", (string)width["loin_property"]["unit"]);
            Assert.Equal(0, (int)width["loin_property"]["min_inclusive"]);
        }

        [Fact]
        public void Search_in_dictionary_flattens_the_tree_and_property_and_dictionaries_use_their_endpoints()
        {
            var handler = new FakeHandler
            {
                Respond = r =>
                {
                    switch (r.RequestUri.AbsolutePath)
                    {
                        case "/api/SearchInDictionary/v1":
                            return Json(@"{ ""dictionary"": { ""dictionaryUri"": ""https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1"", ""dictionaryName"": ""Uniclass 2015"", ""version"": ""1"" },
                                ""classes"": [ { ""uri"": ""u1"", ""code"": ""Pr_30"", ""name"": ""Products"", ""children"": [ { ""uri"": ""u2"", ""code"": ""Pr_30_59"", ""name"": ""Doors"" } ] } ] }");
                        case "/api/Property/v5":
                            return Json(@"{ ""code"": ""FireRating"", ""name"": ""Fire rating"", ""dataType"": ""String"", ""propertySet"": ""Pset_WallCommon"", ""uri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/prop/FireRating"" }");
                        default:
                            return Json(@"{ ""totalCount"": 1, ""dictionaries"": [ { ""dictionaryUri"": ""https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1"", ""dictionaryName"": ""Uniclass 2015"", ""version"": ""1"", ""status"": ""Active"" } ] }");
                    }
                }
            };
            JObject tree = BsddLookup.Handle(new JObject
            {
                ["operation"] = "bsdd_search_dictionary", ["uri"] = "https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1",
                ["text"] = "door", ["related_ifc_entity"] = "IfcDoor"
            }, CancellationToken.None, Options(handler));
            Assert.Contains("RelatedIfcEntity=IfcDoor", handler.Requests[0].Query);
            Assert.Equal(new[] { "Pr_30", "Pr_30_59" }, tree["classes"].Select(c => (string)c["code"]));
            Assert.Equal("Pr_30", (string)tree["classes"][1]["parent_code"]);
            Assert.Equal(1, (int)tree["classes"][1]["depth"]);

            JObject property = BsddLookup.Handle(new JObject
            {
                ["operation"] = "bsdd_property", ["uri"] = "https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/prop/FireRating"
            }, CancellationToken.None, Options(handler));
            Assert.Equal("/api/Property/v5", handler.Requests[1].AbsolutePath);
            Assert.True((bool)property["loin_property_suggestion"]["complete"]);

            JObject dictionaries = BsddLookup.Handle(new JObject { ["operation"] = "bsdd_dictionaries" }, CancellationToken.None, Options(handler));
            Assert.Equal("/api/Dictionary/v1", handler.Requests[2].AbsolutePath);
            Assert.Equal("Uniclass 2015", (string)dictionaries["dictionaries"][0]["name"]);
        }

        [Fact]
        public void Search_in_dictionary_reads_the_live_shape_where_classes_are_nested_in_the_dictionary()
        {
            // Recorded from api.bsdd.buildingsmart.org on 2026-09-24 (trimmed to two classes).
            // The first implementation read classes and dictionaryName at the top level and
            // returned an empty dictionary with zero classes for a query that has 179.
            var handler = new FakeHandler
            {
                Respond = r => Json(@"{""dictionary"":{""name"":""Uniclass 2015"",""uri"":""https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1"",""classes"":[" +
                    @"{""name"":""Outdoor sports activities"",""uri"":""https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1/class/Ac_42_55"",""referenceCode"":""Ac_42_55"",""classType"":""Class""}," +
                    @"{""name"":""Indoor sports activities"",""uri"":""https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1/class/Ac_42_40"",""referenceCode"":""Ac_42_40"",""classType"":""Class""}]}," +
                    @"""totalCount"":179,""offset"":0,""count"":2}")
            };
            JObject tree = BsddLookup.Handle(new JObject
            {
                ["operation"] = "bsdd_search_dictionary", ["uri"] = "https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1", ["text"] = "door"
            }, CancellationToken.None, Options(handler));
            Assert.Equal("Uniclass 2015", (string)tree["dictionary"]["name"]);
            Assert.Equal("https://identifier.buildingsmart.org/uri/nbs/uniclass2015/1", (string)tree["dictionary"]["uri"]);
            Assert.Equal(179, (int)tree["total_count"]);
            Assert.Equal(new[] { "Ac_42_55", "Ac_42_40" }, tree["classes"].Select(c => (string)c["code"]));
        }

        [Fact]
        public void Without_network_it_says_so_and_serves_only_an_expired_copy_labelled_stale()
        {
            var args = new JObject { ["operation"] = "bsdd_search", ["text"] = "wall" };
            var offline = new FakeHandler { Respond = r => throw new HttpRequestException("No such host is known.") };

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                BsddLookup.Handle((JObject)args.DeepClone(), CancellationToken.None, Options(offline)));
            Assert.Contains("could not be reached", ex.Message);
            Assert.Contains("not an empty one", ex.Message);

            var online = new FakeHandler { Respond = r => Json(SearchBody) };
            BsddLookup.Handle((JObject)args.DeepClone(), CancellationToken.None, Options(online));

            _now = _now.AddDays(30);   // the cached answer is now far past max_age_hours
            JObject stale = BsddLookup.Handle((JObject)args.DeepClone(), CancellationToken.None, Options(offline));
            Assert.Equal("stale_cache", (string)stale["source"]);
            Assert.Contains("EXPIRED", (string)stale["warning"]);
            Assert.True((double)stale["age_hours"] >= 720);
        }

        [Fact]
        public void An_expired_or_refreshed_query_calls_again()
        {
            var handler = new FakeHandler { Respond = r => Json(SearchBody) };
            var args = new JObject { ["operation"] = "bsdd_search", ["text"] = "wall", ["max_age_hours"] = 1 };
            BsddLookup.Handle((JObject)args.DeepClone(), CancellationToken.None, Options(handler));
            _now = _now.AddHours(2);
            Assert.Equal("network", (string)BsddLookup.Handle((JObject)args.DeepClone(), CancellationToken.None, Options(handler))["source"]);
            args["refresh"] = true;
            BsddLookup.Handle((JObject)args.DeepClone(), CancellationToken.None, Options(handler));
            Assert.Equal(3, handler.Requests.Count);
            Assert.Single(Directory.GetFiles(_cache));   // one query, one cache entry, rewritten in place
        }

        [Fact]
        public void The_call_cap_refuses_before_a_request_leaves()
        {
            var handler = new FakeHandler { Respond = r => Json(SearchBody) };
            var budget = new BsddLookup.CallBudget(2, 10);
            for (int i = 0; i < 2; i++)
                BsddLookup.Handle(new JObject { ["operation"] = "bsdd_search", ["text"] = "q" + i, ["refresh"] = true },
                                  CancellationToken.None, Options(handler, budget));
            ToolRefusal refused = Assert.Throws<ToolRefusal>(() =>
                BsddLookup.Handle(new JObject { ["operation"] = "bsdd_search", ["text"] = "q9", ["refresh"] = true },
                                  CancellationToken.None, Options(handler, budget)));
            Assert.Contains("cap", refused.Message);
            Assert.Equal(2, handler.Requests.Count);

            _now = _now.AddMinutes(2);   // the minute window slides
            BsddLookup.Handle(new JObject { ["operation"] = "bsdd_search", ["text"] = "q9", ["refresh"] = true },
                              CancellationToken.None, Options(handler, budget));
            Assert.Equal(3, handler.Requests.Count);
        }

        [Fact]
        public void Not_found_is_an_answer_and_errors_are_not_cached()
        {
            var handler = new FakeHandler { Respond = r => Json("{}", HttpStatusCode.NotFound) };
            JObject reply = BsddLookup.Handle(new JObject
            {
                ["operation"] = "bsdd_class", ["uri"] = "https://identifier.buildingsmart.org/uri/x/y/1/class/NOPE"
            }, CancellationToken.None, Options(handler));
            Assert.False((bool)reply["found"]);

            var failing = new FakeHandler { Respond = r => Json(@"{""message"":""bad""}", HttpStatusCode.BadRequest) };
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => BsddLookup.Handle(
                new JObject { ["operation"] = "bsdd_search", ["text"] = "wall" }, CancellationToken.None, Options(failing)));
            Assert.Contains("rejected", ex.Message);
            Assert.False(Directory.Exists(_cache) && Directory.GetFiles(_cache).Length > 0);
        }

        [Fact]
        public void Bad_arguments_are_refused_before_any_request()
        {
            var handler = new FakeHandler { Respond = r => Json("{}") };
            foreach (JObject bad in new[]
            {
                new JObject { ["operation"] = "bsdd_search", ["text"] = "a" },
                new JObject { ["operation"] = "bsdd_search", ["text"] = "wall", ["dictionary_uris"] = new JArray("file:///C:/x") },
                new JObject { ["operation"] = "bsdd_class", ["uri"] = "not a uri" },
                new JObject { ["operation"] = "bsdd_class" },
                new JObject { ["operation"] = "bsdd_search_dictionary" },
                new JObject { ["operation"] = "bsdd_search", ["text"] = "wall", ["limit"] = 1000 },
                new JObject { ["operation"] = "bsdd_delete" }
            })
                Assert.Throws<ToolRefusal>(() => BsddLookup.Handle(bad, CancellationToken.None, Options(handler)));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void The_operations_ride_on_the_read_only_open_world_catalog_tool()
        {
            CommandContract c = Contract.Find("horizun_catalog_lookup");
            Assert.NotNull(c);
            Assert.Null(c.Command);
            Assert.Equal(ToolEffect.ReadOnly, c.Effect);
            Assert.True(c.OpenWorld);
            Assert.False(c.Destructive);
            Assert.True(c.ExternalContent);
            var operations = c.InputSchema["properties"]["operation"]["enum"].Select(t => (string)t).ToList();
            // "search" (2026-09-25) joined "leaf" as a host-answered, non-bSDD operation of
            // this same tool - description matching by normalized token overlap, so a real
            // field session stopped having to open the catalog CSV by hand.
            Assert.Equal(new[] { "leaf", "search", "bsdd_search", "bsdd_search_dictionary", "bsdd_class", "bsdd_property", "bsdd_dictionaries" }, operations);
            // Adding operations did not make the leaf check's arguments optional in the handler.
            Assert.Throws<ArgumentException>(() => CatalogLookup.Handle(new JObject { ["code"] = "A" }));
            Assert.Null(Contract.Find("horizun_bsdd_lookup"));
        }
    }
}
