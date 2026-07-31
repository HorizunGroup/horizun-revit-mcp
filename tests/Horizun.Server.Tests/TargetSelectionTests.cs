// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Which Revit this session is pointed at, under concurrency.
//
// The server answers requests on several threads on purpose: that is why
// job_status can be asked while a two-minute scan runs. horizun_target writes the
// session's target from one of those threads while calls that are already in
// flight read it from others - and until now it was TWO static fields, a year and
// a pid, written one after the other and read one after the other:
//
//     PipeClient.StickyPid  = wantPid;      // thread A
//     PipeClient.StickyYear = null;
//
//     string year = PipeClient.StickyYear;  // thread B, in between
//     ... PipeClient.Resolve(year, PipeClient.StickyPid, ...)
//
// A reader landing between those two writes sees a pair that was never chosen. A
// pid beats a year in Resolve, so the shape of the failure is the bad one: the
// command goes to the instance the caller was pointed at BEFORE, and the answer
// looks entirely correct - about the wrong model.
//
// Two properties are pinned here, and neither can be pinned by reading either file
// alone:
//
//   NO TORN PAIR. Every snapshot a reader takes is a target somebody chose.
//   NO BOTH. pid and year cannot be set together, in the type or in the tool.
//
// No Revit, and no second Revit: a temp directory and an injected liveness probe.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TargetSelectionTests : IDisposable
    {
        private readonly string _dir;
        private readonly HashSet<int> _alive = new HashSet<int>();

        public TargetSelectionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-target-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            PipeClient.DirectoryOverride = _dir;
            PipeClient.LivenessProbe = pid => { lock (_alive) return _alive.Contains(pid); };
            PipeClient.Target = TargetSelection.Automatic;
        }

        public void Dispose()
        {
            PipeClient.DirectoryOverride = null;
            PipeClient.LivenessProbe = null;
            PipeClient.Target = TargetSelection.Automatic;
            try { Directory.Delete(_dir, true); } catch { }
        }

        private void Publish(string year, int pid, bool alive = true)
        {
            var o = new JObject
            {
                ["schema"] = 3,
                ["revit_year"] = year,
                ["pipe_name"] = "Horizun-" + pid,
                ["auth_token"] = "t",
                ["pid"] = pid,
                ["addin_version"] = "0.3.0",
                ["commands"] = new JArray("horizun_health"),
                ["instance_id"] = Guid.NewGuid().ToString("N"),
                ["started_utc"] = DateTime.UtcNow.ToString("o")
            };
            File.WriteAllText(Path.Combine(_dir, "revit-" + year + "-" + pid + ".json"), o.ToString());
            if (alive) lock (_alive) _alive.Add(pid);
        }

        // ---- the type cannot express a contradiction ---------------------------

        [Fact]
        public void A_selection_is_a_year_or_a_pid_or_neither_and_never_both()
        {
            Assert.Null(TargetSelection.ByYear("2026").Pid);
            Assert.Null(TargetSelection.ByPid(4242).Year);

            Assert.True(TargetSelection.Automatic.IsAutomatic);
            Assert.False(TargetSelection.ByYear("2026").IsAutomatic);
            Assert.False(TargetSelection.ByPid(4242).IsAutomatic);
        }

        [Fact]
        public void A_selection_cannot_be_built_out_of_nothing()
        {
            // An empty year or a zero pid would read as "chosen" everywhere downstream
            // while naming nothing - a target that exists in the reply and not in fact.
            Assert.Throws<ArgumentException>(() => TargetSelection.ByYear(""));
            Assert.Throws<ArgumentException>(() => TargetSelection.ByYear("   "));
            Assert.Throws<ArgumentException>(() => TargetSelection.ByPid(0));
            Assert.Throws<ArgumentException>(() => TargetSelection.ByPid(-1));
        }

        [Fact]
        public void Clearing_the_target_is_never_a_null_reference_for_the_next_reader()
        {
            PipeClient.Target = TargetSelection.ByPid(4242);
            PipeClient.Target = null;

            Assert.NotNull(PipeClient.Target);
            Assert.True(PipeClient.Target.IsAutomatic);
        }

        // ---- the tool refuses to be given both ---------------------------------

        [Fact]
        public void Naming_a_pid_and_a_year_in_one_call_is_refused()
        {
            Publish("2025", 100);
            Publish("2026", 200);

            var refusal = Assert.Throws<ToolRefusal>(() =>
                Targets.Handle(new JObject { ["pid"] = 200, ["year"] = "2025" }));

            Assert.Contains("BOTH", refusal.Message);
            Assert.Contains("target is unchanged", refusal.Message);
        }

        [Fact]
        public void A_refused_call_leaves_the_previous_target_exactly_as_it_found_it()
        {
            // The failure this replaces: the pid branch ran, set the target, and THEN the
            // year branch overwrote it - so a call that should have changed nothing had
            // already changed something by the time it was refused.
            Publish("2025", 100);
            Publish("2026", 200);
            Targets.Handle(new JObject { ["pid"] = 200 });

            Assert.Throws<ToolRefusal>(() => Targets.Handle(new JObject { ["pid"] = 100, ["year"] = "2025" }));

            Assert.Equal(200, PipeClient.Target.Pid);
            Assert.Null(PipeClient.Target.Year);
        }

        [Fact]
        public void A_pid_that_published_nothing_is_refused_without_touching_the_target()
        {
            Publish("2026", 200);
            Targets.Handle(new JObject { ["year"] = "2026" });

            Assert.Throws<ToolRefusal>(() => Targets.Handle(new JObject { ["pid"] = 999 }));

            Assert.Equal("2026", PipeClient.Target.Year);
            Assert.Null(PipeClient.Target.Pid);
        }

        [Fact]
        public void A_year_that_published_nothing_is_refused_without_touching_the_target()
        {
            Publish("2026", 200);
            Targets.Handle(new JObject { ["pid"] = 200 });

            Assert.Throws<ToolRefusal>(() => Targets.Handle(new JObject { ["year"] = "2019" }));

            Assert.Equal(200, PipeClient.Target.Pid);
        }

        // ---- one choice replaces the other, whole ------------------------------

        [Fact]
        public void Choosing_a_year_drops_a_pinned_instance()
        {
            Publish("2026", 200);
            Targets.Handle(new JObject { ["pid"] = 200 });
            Assert.Equal(200, PipeClient.Target.Pid);

            Targets.Handle(new JObject { ["year"] = "2026" });

            Assert.Null(PipeClient.Target.Pid);
            Assert.Equal("2026", PipeClient.Target.Year);
        }

        [Fact]
        public void Choosing_an_instance_drops_a_pinned_year()
        {
            Publish("2026", 200);
            Targets.Handle(new JObject { ["year"] = "2026" });

            Targets.Handle(new JObject { ["pid"] = 200 });

            Assert.Null(PipeClient.Target.Year);
            Assert.Equal(200, PipeClient.Target.Pid);
        }

        [Fact]
        public void Auto_clears_both_halves_at_once()
        {
            Publish("2026", 200);
            Targets.Handle(new JObject { ["pid"] = 200 });

            JObject reply = Targets.Handle(new JObject { ["year"] = "auto" });

            Assert.True(PipeClient.Target.IsAutomatic);
            Assert.Contains("Target cleared", (string)reply["change"]);
        }

        [Fact]
        public void A_call_with_no_arguments_reports_without_changing_anything()
        {
            Publish("2026", 200);
            Targets.Handle(new JObject { ["pid"] = 200 });

            JObject reply = Targets.Handle(new JObject());

            Assert.Equal(200, PipeClient.Target.Pid);
            Assert.Null(reply["change"]);
            Assert.Equal("horizun_target (a specific process)", (string)reply["selected_by"]);
        }

        [Fact]
        public void A_pinned_instance_reports_itself_as_selected_even_beside_a_year_env_var()
        {
            // A pid outranks HORIZUN_REVIT_YEAR. Reporting the env var as the reason
            // while routing by pid is exactly the disagreement this tool exists to end.
            Publish("2025", 100);
            Publish("2026", 200);
            string previous = Environment.GetEnvironmentVariable("HORIZUN_REVIT_YEAR");
            try
            {
                Environment.SetEnvironmentVariable("HORIZUN_REVIT_YEAR", "2025");
                Targets.Handle(new JObject { ["pid"] = 200 });

                JObject reply = Targets.Handle(new JObject());

                Assert.Equal("horizun_target (a specific process)", (string)reply["selected_by"]);
                Assert.Equal(200, (int)reply["selected_pid"]);
                Assert.Equal("2026", (string)reply["selected_year"]);
            }
            finally { Environment.SetEnvironmentVariable("HORIZUN_REVIT_YEAR", previous); }
        }

        // ---- under concurrency -------------------------------------------------

        /// <summary>
        /// THE ONE THIS FILE EXISTS FOR. A writer flips the target between an instance and
        /// a year as fast as it can while readers snapshot it. Every snapshot must be one
        /// of the two targets that were actually published - never a pid beside a year,
        /// and never the momentary "nothing is chosen" that clearing one field before
        /// setting the other used to expose.
        ///
        /// Against the two-field version this fails: the window between the two writes is
        /// small, but a million reads find it.
        /// </summary>
        [Fact]
        public async Task A_reader_never_sees_a_target_that_was_never_chosen()
        {
            var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var torn = new ConcurrentBag<string>();
            long reads = 0;

            var writer = Task.Run(() =>
            {
                var byPid = TargetSelection.ByPid(4242);
                var byYear = TargetSelection.ByYear("2026");
                bool flip = false;
                while (!stop.IsCancellationRequested)
                {
                    PipeClient.Target = flip ? byPid : byYear;
                    flip = !flip;
                }
            });

            var readers = new Task[4];
            for (int i = 0; i < readers.Length; i++)
                readers[i] = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        TargetSelection t = PipeClient.Target;   // ONE read, as callers must
                        Interlocked.Increment(ref reads);

                        bool isTheInstance = t.Pid == 4242 && t.Year == null;
                        bool isTheYear = t.Year == "2026" && t.Pid == null;
                        if (!isTheInstance && !isTheYear)
                            torn.Add("pid=" + (t.Pid?.ToString() ?? "null") + " year=" + (t.Year ?? "null"));
                    }
                });

            PipeClient.Target = TargetSelection.ByYear("2026");
            await Task.WhenAll(readers);
            await writer;

            Assert.True(Interlocked.Read(ref reads) > 100000,
                        "only " + Interlocked.Read(ref reads) + " reads - too few to have found a narrow window");
            Assert.True(torn.IsEmpty,
                        "readers observed " + torn.Count + " target(s) nobody chose, e.g. " +
                        string.Join("; ", new List<string>(torn).GetRange(0, Math.Min(3, torn.Count))));
        }

        /// <summary>
        /// The tool itself, hammered from several threads at once. Whatever order the
        /// calls land in, the target that survives is one whole choice - and no call
        /// leaves the pair half-written for the next reader.
        /// </summary>
        [Fact]
        public async Task Concurrent_retargeting_always_settles_on_one_whole_choice()
        {
            Publish("2025", 100);
            Publish("2026", 200);

            var bad = new ConcurrentBag<string>();
            var barrier = new ManualResetEventSlim(false);

            var tasks = new Task[8];
            for (int i = 0; i < tasks.Length; i++)
            {
                int which = i;
                tasks[i] = Task.Run(() =>
                {
                    barrier.Wait();
                    for (int n = 0; n < 200; n++)
                    {
                        try
                        {
                            switch ((which + n) % 3)
                            {
                                case 0: Targets.Handle(new JObject { ["pid"] = 100 }); break;
                                case 1: Targets.Handle(new JObject { ["year"] = "2026" }); break;
                                default: Targets.Handle(new JObject { ["year"] = "auto" }); break;
                            }
                        }
                        catch (ToolRefusal) { /* a legitimate no; it must still not corrupt the pair */ }

                        TargetSelection t = PipeClient.Target;
                        if (t.Pid != null && t.Year != null)
                            bad.Add("pid=" + t.Pid + " AND year=" + t.Year);
                    }
                });
            }

            barrier.Set();
            await Task.WhenAll(tasks);

            Assert.True(bad.IsEmpty, "a pid and a year were set at the same time: " + string.Join("; ", bad));

            TargetSelection final = PipeClient.Target;
            Assert.True(final.IsAutomatic || final.Pid == 100 || final.Year == "2026",
                        "settled on a target nobody asked for: pid=" + (final.Pid?.ToString() ?? "null") +
                        " year=" + (final.Year ?? "null"));
        }
    }
}
