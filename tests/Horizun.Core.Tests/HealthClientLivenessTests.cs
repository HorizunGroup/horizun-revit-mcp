// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The second-client warning of horizun_health must not fire for (a) the MCP server's own instance probes
// (with two Revit open, every agent's server probes both), (b) a recycled pid, (c) an orphan server left
// running by a client that is gone; and it must survive detail=compact (which drops the clients block).
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HealthClientLivenessTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_request_marked_as_a_probe_is_not_presence_and_an_ordinary_one_is()
        {
            Assert.True(ProbeFlag.IsProbe(JObject.Parse("{\"id\":\"1\",\"command\":\"horizun_health\",\"probe\":true}")));
            Assert.False(ProbeFlag.IsProbe(JObject.Parse("{\"id\":\"1\",\"command\":\"horizun_health\"}")));
            Assert.False(ProbeFlag.IsProbe(JObject.Parse("{\"probe\":\"yes\"}")));      // only a real true counts
            Assert.False(ProbeFlag.IsProbe(JObject.Parse("{\"probe\":false}")));
            Assert.False(ProbeFlag.IsProbe(null));
        }

        [Fact]
        public void A_recycled_pid_is_not_the_client_that_connected()
        {
            Assert.False(ClientLiveness.Alive(true, T0.AddHours(3), T0, null));          // same pid, another start time
            Assert.True(ClientLiveness.Alive(true, T0.AddSeconds(1), T0, null));         // clock resolution of two reads
            Assert.False(ClientLiveness.Alive(false, null, T0, null));                   // no such process
        }

        [Fact]
        public void An_orphan_server_whose_parent_exited_is_not_counted()
        {
            Assert.False(ClientLiveness.Alive(true, T0, T0, false));
            Assert.True(ClientLiveness.Alive(true, T0, T0, true));
            Assert.True(ClientLiveness.Alive(true, T0, T0, null));                       // unknown parent: counted, never dropped
            Assert.True(ClientLiveness.Alive(true, null, null, null));
            Assert.Null(ClientLiveness.Alive(null, null, null, null));
        }

        [Fact]
        public void The_warning_uses_the_per_client_liveness()
        {
            var p = new ClientPresence();
            p.Seen(200, T0.AddSeconds(-30));
            p.Seen(100, T0);
            Assert.Null(ConcurrentClientRule.HealthWarning(p.Take(T0), c => false));
            Assert.Contains("200", ConcurrentClientRule.HealthWarning(p.Take(T0), c => c.Pid == 200 ? true : (bool?)false));
        }

        [Fact]
        public void The_warning_survives_the_compact_answer_while_the_clients_block_does_not()
        {
            var payload = new JObject
            {
                ["clients"] = new JObject { ["other_clients_connected"] = 1 },
                ["other_client_warning"] = "Another MCP client is connected to this same Revit: ...",
                ["status"] = "healthy"
            };
            JObject compact = HealthDetailRules.Apply(payload, compact: true);
            Assert.NotNull(compact["other_client_warning"]);
            Assert.Null(compact["clients"]);
            Assert.DoesNotContain("other_client_warning", HealthDetailRules.OmittedWhenCompact);
        }
    }
}
