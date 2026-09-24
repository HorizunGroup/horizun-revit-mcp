// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
// horizun_federation_check, Revit-free: declared disciplines, expected links, site.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FederationCheckTests
    {
        private static JObject Rules(string json) => JObject.Parse(json.Replace('\'', '"'));

        private static FederationModelFact Model(string title, bool host, params (string cat, int n)[] counts)
        {
            var m = new FederationModelFact { Title = title, IsHost = host };
            foreach (var c in counts)
            {
                m.CategoryCounts[c.cat] = c.n;
                m.SampleIds[c.cat] = Enumerable.Range(1, c.n).Select(i => (long)i).ToList();
            }
            return m;
        }

        [Fact]
        public void Malformed_rules_are_refused_before_anything_is_read()
        {
            Assert.NotNull(FederationCheckRules.Validate(null));
            Assert.Contains("unknown key", FederationCheckRules.Validate(Rules("{ 'modles': [] }")));
            Assert.Contains("neither", FederationCheckRules.Validate(Rules("{ 'models': [ { 'match': 'ARQ' } ] }")));
            Assert.Contains("regex", FederationCheckRules.Validate(Rules("{ 'expected_links': [ { 'name_matches': '(' } ] }")));
            Assert.Null(FederationCheckRules.Validate(Rules("{ 'models': [ { 'match': '$host', 'allowed_categories': ['OST_Walls'] } ] }")));
        }

        [Fact]
        public void Out_of_place_categories_are_named_and_unclassified_models_are_not_judged()
        {
            JObject rules = Rules("{ 'models': [ { 'match': '$host', 'discipline': 'ARQ', 'allowed_categories': ['OST_Walls', 'OST_Doors'] }," +
                                  " { 'match': '-EST-', 'discipline': 'EST', 'forbidden_categories': ['OST_DuctCurves'] } ], 'same_site': false }");
            var models = new List<FederationModelFact>
            {
                Model("P-ARQ-01", true, ("OST_Walls", 10), ("OST_DuctCurves", 2)),
                Model("P-EST-01", false, ("OST_StructuralColumns", 5)),
                Model("P-MEP-01", false, ("OST_DuctCurves", 7))
            };
            JObject r = FederationCheckRules.Evaluate(rules, models, new List<FederationLinkFact>(), 10, 5);
            Assert.Equal("fails", (string)r["verdict"]);
            Assert.Equal(2, (int)r["summary"]["elements_out_of_place"]);
            JToken host = r["models"].First(m => (bool)m["host"]);
            Assert.Equal("OST_DuctCurves", (string)host["out_of_place"][0]["category"]);
            Assert.Equal("clean", (string)r["models"].First(m => (string)m["model"] == "P-EST-01")["state"]);
            Assert.Equal("unclassified", (string)r["models"].First(m => (string)m["model"] == "P-MEP-01")["state"]);
        }

        [Fact]
        public void Expected_links_report_missing_duplicated_wrong_workset_and_unexpected()
        {
            JObject rules = Rules("{ 'expected_links': [ { 'name_matches': 'EST', 'workset_matches': '^Links' }, { 'name_matches': 'MEP' }, { 'name_matches': 'HID', 'count': 1 } ], 'same_site': false }");
            var links = new List<FederationLinkFact>
            {
                new FederationLinkFact { InstanceId = 1, Title = "P-EST-01", Workset = "Shared Levels" },
                new FederationLinkFact { InstanceId = 2, Title = "P-HID-01", Workset = "Links" },
                new FederationLinkFact { InstanceId = 3, Title = "P-HID-01", Workset = "Links" },
                new FederationLinkFact { InstanceId = 4, Title = "P-SITE", Workset = "Links" }
            };
            JObject r = FederationCheckRules.Evaluate(rules, new List<FederationModelFact>(), links, 10, 5);
            var rows = r["expected_links"].ToDictionary(x => (string)x["name_matches"], x => (string)x["state"]);
            Assert.Equal("present", rows["EST"]);
            Assert.Equal("missing", rows["MEP"]);
            Assert.Equal("duplicated", rows["HID"]);
            Assert.Equal(1, (int)r["summary"]["links_wrong_workset"]);
            Assert.Equal(4L, (long)r["unexpected_links"][0]["instance_id"]);
        }

        [Fact]
        public void Site_is_coherent_within_tolerance_incoherent_beyond_and_not_decidable_unloaded()
        {
            JObject rules = Rules("{ 'same_site': true }");
            var links = new List<FederationLinkFact>
            {
                new FederationLinkFact { InstanceId = 1, Title = "A", Loaded = true, SiteDeltaMm = 3 },
                new FederationLinkFact { InstanceId = 2, Title = "B", Loaded = true, SiteDeltaMm = 2500 },
                new FederationLinkFact { InstanceId = 3, Title = "C", Loaded = false, SiteWhyNot = "not loaded" }
            };
            JObject r = FederationCheckRules.Evaluate(rules, new List<FederationModelFact>(), links, 10, 5);
            var st = r["site"].ToDictionary(x => (long)x["instance_id"], x => (string)x["state"]);
            Assert.Equal("coherent", st[1]);
            Assert.Equal("incoherent", st[2]);
            Assert.Equal("not_decidable", st[3]);
            Assert.Equal("fails", (string)r["verdict"]);

            JObject clean = FederationCheckRules.Evaluate(rules, new List<FederationModelFact>(), links.Where(l => l.InstanceId != 2).ToList(), 10, 5);
            Assert.Equal("not_decidable", (string)clean["verdict"]);     // an unloaded link keeps it from passing
        }
    }
}
