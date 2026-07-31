// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Two instances of the same Revit YEAR running at once.
//
// This is not a hypothetical here: opening a file is enough to start a second
// Revit, and it happened twice in one afternoon of this work - once the bridge
// answered from an empty 2025 while the model was open in 2026, which was only
// caught because horizun_target reports which instance answered.
//
// Discovery used one file per year. So the second instance overwrote the first's
// pipe name, and whichever closed first deleted the file the OTHER was still
// using. Now there is one file per instance, and when two of them are running
// and nothing says which is meant, the call is REFUSED - a command sent to the
// wrong session is a correct edit to the wrong model.
//
// Proved against a temp directory with an injected liveness probe, because
// testing this by starting two Revits and hoping is not testing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class DiscoveryResolveTests : IDisposable
    {
        private readonly string _dir;
        private readonly HashSet<int> _alive = new HashSet<int>();

        public DiscoveryResolveTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            PipeClient.DirectoryOverride = _dir;
            PipeClient.LivenessProbe = pid => _alive.Contains(pid);
            PipeClient.Target = TargetSelection.Automatic;
        }

        public void Dispose()
        {
            PipeClient.DirectoryOverride = null;
            PipeClient.LivenessProbe = null;
            PipeClient.Target = TargetSelection.Automatic;
            try { Directory.Delete(_dir, true); } catch { }
        }

        private void Publish(string year, int pid, bool alive, int schema = 3)
        {
            var o = new JObject
            {
                ["schema"] = schema,
                ["revit_year"] = year,
                ["pipe_name"] = "Horizun-" + pid,
                ["auth_token"] = "t",
                ["pid"] = pid,
                ["addin_version"] = "0.2.0",
                ["commands"] = new JArray("horizun_health")
            };
            // instance_id and started_utc arrived WITH schema 3. A schema-2 fixture that
            // carries them is not a schema-2 file, and would let a test pass against a
            // shape that never existed.
            if (schema >= 3)
            {
                o["instance_id"] = Guid.NewGuid().ToString("N");
                o["started_utc"] = DateTime.UtcNow.ToString("o");
            }
            string name = schema >= 3 ? "revit-" + year + "-" + pid + ".json" : "revit-" + year + ".json";
            File.WriteAllText(Path.Combine(_dir, name), o.ToString());
            if (alive) _alive.Add(pid);
        }

        // ---- the case that started this ---------------------------------------

        [Fact]
        public void Two_live_instances_of_the_same_year_are_refused_not_guessed()
        {
            Publish("2026", 100, alive: true);
            Publish("2026", 200, alive: true);

            string refusal;
            Discovered d = PipeClient.Resolve("2026", null, out refusal);

            Assert.Null(d);
            Assert.NotNull(refusal);
            Assert.Contains("2 Revit instances", refusal);
            Assert.Contains("pid 100", refusal);
            Assert.Contains("pid 200", refusal);
            Assert.Contains("Refusing to pick", refusal);
        }

        [Fact]
        public void A_pid_settles_it()
        {
            Publish("2026", 100, alive: true);
            Publish("2026", 200, alive: true);

            string refusal;
            Discovered d = PipeClient.Resolve("2026", 200, out refusal);

            Assert.Null(refusal);
            Assert.Equal(200, d.Pid);
            Assert.Equal("Horizun-200", d.PipeName);
        }

        [Fact]
        public void Two_instances_of_DIFFERENT_years_are_still_ambiguous_without_a_year()
        {
            // The old code picked the newest file. Two disciplines open in two Revits is
            // the normal case, and "newest" is not a decision anybody made.
            Publish("2025", 100, alive: true);
            Publish("2026", 200, alive: true);

            string refusal;
            Assert.Null(PipeClient.Resolve("", null, out refusal));
            Assert.Contains("nothing says which one you mean", refusal);

            // Naming the year resolves it, because only one instance of it is running.
            Assert.Equal(200, PipeClient.Resolve("2026", null, out refusal).Pid);
            Assert.Null(refusal);
        }

        // ---- liveness ----------------------------------------------------------

        [Fact]
        public void A_live_instance_beats_a_stale_file_without_being_ambiguous()
        {
            Publish("2026", 100, alive: false);   // crashed, file left behind
            Publish("2026", 200, alive: true);

            string refusal;
            Discovered d = PipeClient.Resolve("2026", null, out refusal);

            Assert.Null(refusal);
            Assert.Equal(200, d.Pid);
        }

        [Fact]
        public void When_nothing_is_alive_a_stale_file_is_returned_so_the_caller_hears_why()
        {
            // Returning null here would produce "no Revit is reachable", which is true but
            // useless; the stale record produces "that Revit crashed, start it again".
            Publish("2026", 100, alive: false);

            string refusal;
            Discovered d = PipeClient.Resolve("2026", null, out refusal);

            Assert.Null(refusal);
            Assert.NotNull(d);
            Assert.False(d.ProcessAlive);
        }

        [Fact]
        public void An_unknown_pid_is_refused_and_says_where_to_look()
        {
            Publish("2026", 100, alive: true);

            string refusal;
            Assert.Null(PipeClient.Resolve("2026", 999, out refusal));
            Assert.Contains("process id 999", refusal);
            Assert.Contains("horizun_target", refusal);
        }

        // ---- one instance closing must not unregister another ------------------

        [Fact]
        public void Each_instance_owns_its_own_file()
        {
            Publish("2026", 100, alive: true);
            Publish("2026", 200, alive: true);

            // What Revit does on shutdown: delete ITS file, named for its own pid.
            File.Delete(Path.Combine(_dir, "revit-2026-100.json"));
            _alive.Remove(100);

            string refusal;
            Discovered d = PipeClient.Resolve("2026", null, out refusal);

            Assert.Null(refusal);
            Assert.Equal(200, d.Pid);      // the survivor is still findable
        }

        // ---- backward compatibility -------------------------------------------

        [Fact]
        public void An_add_in_that_has_not_been_redeployed_is_still_found()
        {
            // schema 2 wrote revit-<year>.json with no instance id. It must keep working:
            // the server and the add-in ship separately.
            Publish("2024", 100, alive: true, schema: 2);

            string refusal;
            Discovered d = PipeClient.Resolve("2024", null, out refusal);

            Assert.Null(refusal);
            Assert.Equal(100, d.Pid);
            Assert.Null(d.InstanceId);      // absent, and reported as absent
        }

        [Fact]
        public void A_legacy_file_and_a_per_instance_file_of_the_same_year_are_two_instances()
        {
            Publish("2026", 100, alive: true, schema: 2);
            Publish("2026", 200, alive: true);

            string refusal;
            Assert.Null(PipeClient.Resolve("2026", null, out refusal));
            Assert.Contains("2 Revit instances", refusal);
        }

        [Fact]
        public void An_empty_directory_is_no_bridge_rather_than_an_error()
        {
            string refusal;
            Assert.Null(PipeClient.Resolve("", null, out refusal));
            Assert.Null(refusal);           // nothing to be ambiguous about
        }
    }
}
