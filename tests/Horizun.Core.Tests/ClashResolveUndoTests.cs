// Horizun Revit MCP - original Horizun code. Clash resolution geometry and the undo journal.
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ClashResolveRulesTests
    {
        // A 100 mm pipe along X at z=3000, crossing a 200x200 duct box running along Y at the same elevation.
        private static ResolveRun Pipe() => new ResolveRun { Start = new[] { 0.0, 0, 3000 }, End = new[] { 4000.0, 0, 3000 }, Width = 100, Height = 100 };
        private static ResolveBox Duct() => new ResolveBox(1900, -2000, 2900, 2100, 2000, 3100);

        [Fact]
        public void Horizontal_run_gets_shift_and_elevation_candidates_smallest_first()
        {
            List<ResolveCandidate> c = ClashResolveRules.Candidates(Pipe(), Duct(), 50, out string code);
            Assert.Null(code);
            Assert.Equal(4, c.Count);
            // elevation up: 3100 + 50 + 50 - 3000 = 200; shift across the run cannot clear a 4 m duct in 600 mm.
            Assert.Equal("elevation", c[0].Kind);
            Assert.Equal(200, c[0].DistanceMm);
            Assert.Equal(new[] { 0.0, 0, 200 }, c[0].VectorMm);
            Assert.Contains(c, x => x.Kind == "shift" && x.DistanceMm == 2100);
        }

        [Fact]
        public void The_predicted_move_clears_the_box_with_the_clearance_and_the_run_axis_is_never_an_escape()
        {
            ResolveCandidate best = ClashResolveRules.Candidates(Pipe(), Duct(), 50, out _)[0];
            // moved pipe bottom = 3000 + 200 - 50 = 3150 = duct top + clearance.
            var moved = new ResolveBox(0, -50, 3150, 4000, 50, 3250);
            Assert.False(ClashResolveRules.BoxesOverlap(moved, Duct(), 49.9));
            Assert.DoesNotContain(ClashResolveRules.Candidates(Pipe(), Duct(), 50, out _), x => System.Math.Abs(x.Direction[0]) > 1e-9);
            Assert.Equal(200, best.DistanceMm);
        }

        [Fact]
        public void Sloped_runs_get_no_elevation_candidate_and_are_flagged()
        {
            var sloped = new ResolveRun { Start = new[] { 0.0, 0, 3000 }, End = new[] { 4000.0, 0, 3200 }, Width = 100, Height = 100 };
            var c = ClashResolveRules.Candidates(sloped, Duct(), 50, out string code);
            Assert.Equal(ClashResolveRules.CodeSloped, code);
            Assert.All(c, x => Assert.Equal("shift", x.Kind));
        }

        [Fact]
        public void Structure_and_architecture_are_never_movers()
        {
            Assert.Equal(-1, ClashResolveRules.ChooseMover(ClashResolveRules.RoleStructure, true, 0, ClashResolveRules.RoleArchitecture, true, 0, out string code, out _));
            Assert.Equal(ClashResolveRules.CodeNotMovable, code);
            Assert.Equal(1, ClashResolveRules.ChooseMover(ClashResolveRules.RoleStructure, true, 0, ClashResolveRules.RoleMovable, true, 1, out _, out _));
            Assert.Equal(-1, ClashResolveRules.ChooseMover(ClashResolveRules.RoleStructure, true, 0, ClashResolveRules.RoleMovable, false, 1, out code, out _));
            Assert.Equal(ClashResolveRules.CodeLinked, code);
            Assert.Equal(0, ClashResolveRules.ChooseMover(ClashResolveRules.RoleMovable, true, 1, ClashResolveRules.RoleMovable, true, 4, out _, out _));
            Assert.Equal(-1, ClashResolveRules.ChooseMover(ClashResolveRules.RoleMovable, true, 1, ClashResolveRules.RoleMovable, true, 1, out code, out _));
            Assert.Equal(ClashResolveRules.CodeBothMovable, code);
            Assert.Equal(ClashResolveRules.RoleStructure, ClashResolveRules.RoleOf("OST_Walls", true));
            Assert.Equal(ClashResolveRules.RoleMovable, ClashResolveRules.RoleOf("OST_PipeCurves", false));
        }

        [Fact]
        public void Keep_only_on_measured_clear_with_no_new_clash()
        {
            Assert.True(ClashResolveRules.Keep(true, true, 0, true, out _));
            Assert.False(ClashResolveRules.Keep(true, true, 1, true, out _));
            Assert.False(ClashResolveRules.Keep(true, false, 0, true, out _));
            Assert.False(ClashResolveRules.Keep(true, null, 0, true, out _));
            Assert.False(ClashResolveRules.Keep(true, true, 0, false, out _));
            Assert.False(ClashResolveRules.Keep(false, true, 0, true, out _));
            Assert.Equal(new[] { "3~9" }, ClashResolveRules.NewPairs(new[] { "1~2" }, new[] { "1~2", ClashResolveRules.PairKey(9, 3) }));
        }

        [Fact]
        public void Resolution_in_the_ledger_needs_a_measurement_and_parses_host_sides()
        {
            var f = new CoordinationFinding { Id = "x", Status = CoordinationRules.StatusOpen };
            Assert.False(ClashResolveRules.ResolveMeasured(f, false, null, "t"));
            Assert.Equal(CoordinationRules.StatusOpen, f.Status);
            Assert.True(ClashResolveRules.ResolveMeasured(f, true, "ok", "t"));
            Assert.Equal(CoordinationRules.StatusResolvedByModel, f.Status);
            Assert.Equal("resolved_by_model", f.History.Last().Kind);
            Assert.True(ClashResolveRules.ParseSide("host||abc-1", out bool host, out string uid));
            Assert.True(host); Assert.Equal("abc-1", uid);
            Assert.True(ClashResolveRules.ParseSide("MEP.rvt|77|abc", out host, out _));
            Assert.False(host);
        }
    }

    public class UndoJournalTests
    {
        private static UndoBatch Batch(string id, string at, bool undoable = true) => new UndoBatch
        {
            Id = id, Tool = "horizun_transform_elements", CreatedUtc = at, SaveStamp = "g#1", Undoable = undoable,
            NotUndoableReason = undoable ? null : "set_tag_leader has no recorded inverse",
            Entries = new List<UndoEntry>
            {
                new UndoEntry
                {
                    Op = "move", ElementIds = new List<long> { 5 },
                    Before = new JObject { ["5"] = new JObject { ["loc"] = new JArray(new JArray(0.0, 0, 0)) } },
                    After = new JObject { ["5"] = new JObject { ["loc"] = new JArray(new JArray(1.0, 0, 0)) } },
                    Inverse = new JObject { ["vector"] = new JArray(1.0, 0, 0) }
                }
            }
        };

        [Fact]
        public void Last_is_the_newest_not_undone_and_a_blocked_last_is_never_skipped()
        {
            var list = new List<UndoBatch> { Batch("a", "2026-01-01"), Batch("b", "2026-01-02") };
            Assert.Equal("b", UndoRules.Last(list, out _).Id);
            list[1].UndoneUtc = "x";
            Assert.Equal("a", UndoRules.Last(list, out _).Id);
            list.Add(Batch("c", "2026-01-03", undoable: false));
            Assert.Null(UndoRules.Last(list, out string why));
            Assert.Contains("c", why);
            Assert.Null(UndoRules.Last(new List<UndoBatch>(), out why));
        }

        [Fact]
        public void A_save_or_sync_since_the_batch_refuses()
        {
            Assert.False(UndoRules.SavedSince("g#1", "g#1", out _));
            Assert.True(UndoRules.SavedSince("g#1", "h#2", out string why));
            Assert.Contains("SAVED", why);
            Assert.True(UndoRules.SavedSince("g#1", null, out _));
        }

        [Fact]
        public void Drift_is_any_state_change_since_the_batch_within_tolerance()
        {
            UndoBatch b = Batch("a", "t");
            var same = new Dictionary<string, JToken> { ["move:5"] = new JObject { ["loc"] = new JArray(new JArray(1.00001, 0, 0)) } };
            Assert.Empty(UndoRules.Drifted(b, same));
            var moved = new Dictionary<string, JToken> { ["move:5"] = new JObject { ["loc"] = new JArray(new JArray(1.5, 0, 0)) } };
            Assert.Equal(new[] { "move:5" }, UndoRules.Drifted(b, moved));
            var gone = new Dictionary<string, JToken> { ["move:5"] = JValue.CreateNull() };
            Assert.Single(UndoRules.Drifted(b, gone));
            Assert.False(UndoRules.StatesMatch(new JObject { ["pinned"] = true }, new JObject { ["pinned"] = false }));
            Assert.False(UndoRules.StatesMatch(new JObject { ["type"] = 1 }, new JObject()));
        }

        [Fact]
        public void The_journal_round_trips_and_caps_its_length()
        {
            var list = new List<UndoBatch>();
            for (int i = 0; i < UndoRules.MaxBatches + 5; i++) UndoRules.Append(list, Batch("b" + i, "t" + i.ToString("00")));
            Assert.Equal(UndoRules.MaxBatches, list.Count);
            Assert.Equal("b5", list[0].Id);
            UndoBatch back = UndoBatch.FromJson(list[0].ToJson());
            Assert.Equal("move", back.Entries[0].Op);
            Assert.Equal(5, back.Entries[0].ElementIds[0]);
            Assert.True(UndoRules.StatesMatch(list[0].Entries[0].After, back.Entries[0].After));
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz-undo-" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                UndoJournalStore.Save(path, list);
                Assert.Equal(list.Count, UndoJournalStore.Load(path).Count);
            }
            finally { System.IO.File.Delete(path); }
        }
    }
}
