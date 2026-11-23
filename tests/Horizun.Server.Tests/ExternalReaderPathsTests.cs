// -----------------------------------------------------------------------------
// Horizun MCP server tests - original Horizun code.
//
// SCRIPTS OUTSIDE THE SERVER READ THE ADVERTISED SCHEMAS. The release gate
// (scripts/verify-live.ps1) and the live harness (scripts/live/horizun-live.lib.ps1)
// read enums and property maps straight out of tools/list, and generate-inventory
// used to. Abridging the advertised copy must never move a path they read, so each
// one is resolved here in the published entry and compared with the contract.
// verify-live itself needs a running Revit; this is its offline half.
// -----------------------------------------------------------------------------
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ExternalReaderPathsTests
    {
        private static JObject Advertised(string tool) =>
            (JObject)Tools.Publish(Tools.Find(tool), true)["inputSchema"];

        [Fact]
        public void Gate_checks_on_the_planimetry_fix_still_hold()
        {
            JObject s = Advertised("horizun_fix_planimetry");
            Assert.False((bool)s["additionalProperties"]);
            Assert.NotNull(s.SelectToken("properties.confirmation_token"));
            Assert.NotNull(s.SelectToken("properties.idempotency_key"));
            Assert.True((bool)s.SelectToken("properties.dry_run.default"));
        }

        [Fact]
        public void Power_bi_push_property_names_are_the_contract_ones()
        {
            JObject full = (JObject)Contract.Find("horizun_power_bi_push").InputSchema["properties"];
            JObject adv = (JObject)Advertised("horizun_power_bi_push")["properties"];
            Assert.Equal(string.Join(",", Names(full)), string.Join(",", Names(adv)));
        }

        [Fact]
        public void Live_harness_top_level_enums_equal_the_contract()
        {
            // scripts/live/horizun-live.lib.ps1 ~503 reads inputSchema.properties.<p>.enum or
            // .items.enum for whatever tool and property a probe names: check them all.
            foreach (CommandContract c in Contract.All)
            {
                if (!(c.InputSchema?["properties"] is JObject props)) continue;
                JObject adv = (JObject)Advertised(c.Name)["properties"];
                foreach (JProperty p in props.Properties())
                    foreach (string sub in new[] { "enum", "items.enum" })
                    {
                        JToken f = p.Value.SelectToken(sub);
                        if (f == null) continue;
                        Assert.True(JToken.DeepEquals(f, adv[p.Name]?.SelectToken(sub)), c.Name + "." + p.Name + "." + sub + " differs");
                    }
            }
        }

        private static System.Collections.Generic.IEnumerable<string> Names(JObject o)
        {
            foreach (JProperty p in o.Properties()) yield return p.Name;
        }
    }
}
