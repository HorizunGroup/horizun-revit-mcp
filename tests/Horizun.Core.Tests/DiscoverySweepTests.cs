// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHICH discovery files are orphaned (story 5.24), decided out of names and a pid
// liveness predicate - the rule the add-in and the server both apply, so it is
// proved once here rather than trusted twice. The two states that matter are the
// ones a live machine will not produce on demand: a legacy two-segment name that
// must NEVER be deleted (it is what a not-yet-redeployed add-in publishes), and a
// pid recycled to another process, which must KEEP its file.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DiscoverySweepTests
    {
        // A pid set that is "alive"; everything else is dead.
        private static System.Func<int, bool> Alive(params int[] alive)
        {
            var set = new HashSet<int>(alive);
            return pid => set.Contains(pid);
        }

        [Fact]
        public void A_dead_pids_file_is_stale()
        {
            var stale = DiscoverySweep.StaleFiles(
                new[] { "revit-2025-11796.json" }, Alive(/* none */), selfPid: -1);

            Assert.Equal(new[] { "revit-2025-11796.json" }, stale);
        }

        [Fact]
        public void A_live_pids_file_is_kept()
        {
            var stale = DiscoverySweep.StaleFiles(
                new[] { "revit-2025-11796.json" }, Alive(11796), selfPid: -1);

            Assert.Empty(stale);
        }

        [Fact]
        public void A_legacy_two_segment_name_is_never_swept()
        {
            // revit-2025.json: its last segment "2025" parses as an int that is almost
            // never a live pid, so a naive rule would delete the file a not-yet-redeployed
            // add-in is actively publishing. It must be left alone whatever the pid map says.
            var stale = DiscoverySweep.StaleFiles(
                new[] { "revit-2025.json" }, Alive(/* nothing alive */), selfPid: -1);

            Assert.Empty(stale);
        }

        [Fact]
        public void The_callers_own_file_is_never_swept()
        {
            // The add-in passes its own pid; even with the file "dead" by the predicate,
            // its own is skipped. The server passes -1, which no file carries.
            var stale = DiscoverySweep.StaleFiles(
                new[] { "revit-2025-4242.json" }, Alive(/* 4242 not alive */), selfPid: 4242);

            Assert.Empty(stale);
        }

        [Fact]
        public void An_unreadable_liveness_answer_keeps_the_file()
        {
            // Conservative: if the liveness check throws, the file is KEPT, never deleted
            // on an answer nobody could give.
            var stale = DiscoverySweep.StaleFiles(
                new[] { "revit-2025-999.json" },
                pid => throw new System.InvalidOperationException("cannot read"),
                selfPid: -1);

            Assert.Empty(stale);
        }

        [Fact]
        public void Full_paths_are_returned_as_given()
        {
            // The caller passes whatever Directory.GetFiles handed it (full paths); the
            // returned entries must be those same strings, ready to File.Delete.
            string path = @"C:\Users\someone\.horizun\discovery\revit-2026-321.json";
            var stale = DiscoverySweep.StaleFiles(new[] { path }, Alive(/* dead */), selfPid: -1);

            Assert.Equal(new[] { path }, stale);
        }

        [Fact]
        public void A_mixed_set_sweeps_only_the_orphans()
        {
            var files = new[]
            {
                "revit-2025-100.json",   // alive   -> keep
                "revit-2025-200.json",   // dead    -> sweep
                "revit-2026-300.json",   // dead    -> sweep
                "revit-2024.json",       // legacy  -> keep
                "revit-2025-999.json"    // self    -> keep
            };
            var stale = DiscoverySweep.StaleFiles(files, Alive(100), selfPid: 999);

            Assert.Equal(new[] { "revit-2025-200.json", "revit-2026-300.json" }, stale.OrderBy(s => s).ToArray());
        }

        [Fact]
        public void A_non_integer_last_segment_is_ignored()
        {
            var stale = DiscoverySweep.StaleFiles(
                new[] { "revit-2025-notapid.json" }, Alive(/* dead */), selfPid: -1);

            Assert.Empty(stale);
        }

        [Fact]
        public void Null_and_empty_inputs_are_safe()
        {
            Assert.Empty(DiscoverySweep.StaleFiles(null, Alive(), -1));
            Assert.Empty(DiscoverySweep.StaleFiles(new[] { "revit-2025-1.json" }, null, -1));
            Assert.Empty(DiscoverySweep.StaleFiles(new string[] { null, "" }, Alive(), -1));
        }
    }
}
