using System.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class HealthOtherInstancesNoticeTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Health_success_lists_the_responding_pid_once_even_when_discovery_omits_it(bool found)
        {
            var answering = D(100, "2025");
            var other = D(200, "2026");
            var registered = found ? new[] { answering, other, D(300, "2027", alive: false) } : new[] { other };
            var health = new Newtonsoft.Json.Linq.JObject { ["answered_by"] = new Newtonsoft.Json.Linq.JObject { ["pid"] = 100 } };

            HealthAnswer.StampSuccess(health, answering, registered);

            var instances = (Newtonsoft.Json.Linq.JArray)health["instances"];
            Assert.Collection(instances, row => Assert.Equal(health["answered_by"]["pid"], row["pid"]),
                row => Assert.Equal(200, (int)row["pid"]));
            Assert.Equal(200, (int)health["other_revit_instances"]["instances"][0]["pid"]);
            Assert.True((bool)instances[0]["process_alive"]);
        }

        private static Discovered D(int pid, string year, bool alive = true) => new Discovered { Pid = pid, Year = year, ProcessAlive = alive };

        [Fact]
        public void Names_the_other_live_revits_even_when_one_is_pinned_and_warns_the_pid_changes_on_restart()
        {
            var n = HealthAnswer.OtherInstancesNotice(new[] { D(100, "2024"), D(200, "2024"), D(300, "2026") }, 100);
            Assert.Equal(2, (int)n["count"]);
            Assert.Equal(new[] { 200, 300 }, n["instances"].Select(i => (int)i["pid"]).ToArray());
            string w = (string)n["warning"];
            Assert.Contains("pid 200", w);
            Assert.Contains("Revit 2026 pid 300", w);
            Assert.Contains("restart", w);
            Assert.Contains("horizun_target", w);
        }

        [Fact]
        public void Says_nothing_when_this_is_the_only_live_revit_and_ignores_dead_processes()
        {
            Assert.Null(HealthAnswer.OtherInstancesNotice(new[] { D(100, "2024") }, 100));
            Assert.Null(HealthAnswer.OtherInstancesNotice(new[] { D(100, "2024"), D(200, "2024", alive: false) }, 100));
            Assert.Null(HealthAnswer.OtherInstancesNotice(null, 100));
        }
    }
}
