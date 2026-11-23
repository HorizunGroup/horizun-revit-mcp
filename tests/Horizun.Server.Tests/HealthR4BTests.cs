using System;
using System.Diagnostics;
using System.Threading;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class HealthR4BTests
    {
        [Fact]
        public async System.Threading.Tasks.Task A_bridge_that_never_answers_does_not_hold_health()
        {
            using (var release = new ManualResetEventSlim())
            {
                try
                {
                    var call = System.Threading.Tasks.Task.Run(() => HealthAnswer.ProbeInstances(
                        new[] { new Discovered { Pid = 1 } }, d =>
                        {
                            release.Wait();
                            return new JObject { ["pid"] = d.Pid };
                        }, 200));
                    JArray rows = await call.WaitAsync(TimeSpan.FromSeconds(8));
                    Assert.Contains("did not answer", (string)rows[0]["probe_error"]);
                }
                finally { release.Set(); }
            }
        }

        [Fact]
        public void Multiple_bridges_that_wait_or_throw_are_bounded_and_explained()
        {
            var instances = new[] { new Discovered { Pid = 1 }, new Discovered { Pid = 2 }, new Discovered { Pid = 3 } };
            var clock = Stopwatch.StartNew();
            JArray rows = HealthAnswer.ProbeInstances(instances, d =>
            {
                if (d.Pid == 1) { Thread.Sleep(300); return new JObject { ["pid"] = d.Pid }; }
                if (d.Pid == 2) throw new InvalidOperationException("bridge failed");
                Thread.Sleep(2000);
                return new JObject { ["pid"] = d.Pid };
            }, 600);
            Assert.True(clock.ElapsedMilliseconds < 1500);
            Assert.Equal(1, (int)rows[0]["pid"]);
            Assert.Contains("bridge failed", (string)rows[1]["probe_error"]);
            Assert.Contains("did not answer", (string)rows[2]["probe_error"]);
        }
    }
}
