// -----------------------------------------------------------------------------
// Horizun Server tests - what discovery costs, and that the list is stable.
// Original Horizun code.
//
// NOT EXECUTED YET. Written during the competitive-gap campaign of 2026-09-15,
// an implementation-only phase.
//
// G03's acceptance is a PERCENTAGE - discipline profiles must cut discovery bytes
// by at least 40% on a common fixture - and a percentage that comes from an
// estimate has not been met, it has been asserted. These tests are about the
// instrument: that it measures the same document the server answers with, that it
// holds the permission posture constant, and that it never reports a failed
// measurement as a measurement of zero.
// -----------------------------------------------------------------------------
using Horizun.Server.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class DiscoveryCostTests
    {
        [Fact]
        public void Two_tool_lists_in_a_row_are_byte_identical()
        {
            // 2026-07-28 asks servers to return tools in a deterministic order so clients
            // can cache and so an LLM's prompt cache keeps hitting. An order that varies
            // between calls silently costs every caller a cache miss per turn.
            string first = new JObject { ["tools"] = Tools.List() }.ToString(Formatting.None);
            string second = new JObject { ["tools"] = Tools.List() }.ToString(Formatting.None);
            Assert.Equal(first, second);
        }

        [Fact]
        public void The_measurement_reports_what_it_measured_or_says_it_could_not()
        {
            JObject cost = DiscoveryCost.Measure(advertiseTaskSupport: false);

            if ((bool)cost["measured"])
            {
                Assert.NotNull(cost["bytes"]);
                Assert.NotNull(cost["baseline_bytes"]);
                Assert.True((long)cost["bytes"] > 0);
                Assert.True((double)cost["reduction_percent"] >= 0.0);
            }
            else
            {
                // A measurement that failed must be legible AS a failure, never as a
                // saving of zero that a report would then publish as a result.
                Assert.NotNull(cost["error"]);
            }
        }

        [Fact]
        public void An_unrestricted_session_claims_no_saving()
        {
            JObject cost = DiscoveryCost.Measure(advertiseTaskSupport: false);
            if (!(bool)cost["measured"]) return;
            if ((bool)cost["active_selection"]["restricting"]) return;

            Assert.Equal(0L, (long)cost["bytes_saved"]);
            Assert.Equal(0.0, (double)cost["reduction_percent"]);
            Assert.Equal((long)cost["bytes"], (long)cost["baseline_bytes"]);
        }

        [Fact]
        public void The_baseline_is_never_smaller_than_the_restricted_list()
        {
            JObject cost = DiscoveryCost.Measure(advertiseTaskSupport: false);
            if (!(bool)cost["measured"]) return;
            Assert.True((long)cost["baseline_bytes"] >= (long)cost["bytes"],
                        "lifting the pack restriction can only ever add tools");
        }

        [Fact]
        public void Every_discipline_row_names_a_real_pack()
        {
            JArray rows = DiscoveryCost.Disciplines();
            Assert.NotEmpty(rows);
            foreach (JToken row in rows)
            {
                string pack = (string)row["pack"];
                Assert.Contains(pack, Horizun.Revit.Core.ToolPacks.KnownPacks);
                Assert.True((int)row["tools"] >= 0);
            }
        }

        [Fact]
        public void Lifting_the_pack_filter_never_lifts_the_permission_filter()
        {
            // The baseline exists to isolate the packs' contribution. If it also lifted
            // the permission profile, a read_only machine would credit its tool packs
            // with a saving its OWNER made - a flattering number rather than a measured
            // one. execute_python is the sharpest case: it is off on a fresh install and
            // must stay absent from both lists.
            string unrestricted = new JObject { ["tools"] = Tools.ListIgnoringPacks() }.ToString(Formatting.None);
            string reason;
            bool pythonAllowed = Horizun.Revit.Core.Settings.IsToolAllowed(
                Horizun.Contracts.Contract.Find("horizun_execute_python"), out reason);

            if (!pythonAllowed)
                Assert.DoesNotContain("\"horizun_execute_python\"", unrestricted);
        }
    }
}
