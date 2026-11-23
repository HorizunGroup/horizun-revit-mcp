// Horizun Revit MCP - original Horizun code. Clash resolution geometry and the undo journal.
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{

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

    // The captured "loc" of a moved element, translated in the shape it was captured in.
    public class UndoLocShiftTests
    {
        [Fact]
        public void A_single_JArray_argument_is_the_copy_constructor_which_is_why_a_point_loc_is_flat()
        {
            // The fact the shape rests on: new JArray(JArray) COPIES, it does not nest.
            var point = new JArray(1.0, 2.0, 3.0);
            JArray loc = new JArray(point);
            Assert.Equal(3, loc.Count);
            Assert.Equal(JTokenType.Float, loc[0].Type);
        }

        [Fact]
        public void A_flat_point_loc_shifts_as_one_point_and_stays_flat()
        {
            JToken shifted = UndoRules.ShiftLoc(new JArray(1.0, 2.0, 3.0), 10, 20, -1.3);
            Assert.True(UndoRules.StatesMatch(new JArray(11.0, 22.0, 1.7), shifted));
            Assert.Equal(JTokenType.Float, ((JArray)shifted)[0].Type);
        }

        [Fact]
        public void A_curve_loc_shifts_both_endpoints_and_stays_nested()
        {
            var curve = new JArray(new JArray(0.0, 0.0, 0.0), new JArray(4.0, 0.0, 0.0));
            JToken shifted = UndoRules.ShiftLoc(curve, 0, 0, -1.3155);
            var expected = new JArray(new JArray(0.0, 0.0, -1.3155), new JArray(4.0, 0.0, -1.3155));
            Assert.True(UndoRules.StatesMatch(expected, shifted));
        }

        [Fact]
        public void Integer_coordinates_are_numbers_too()
        {
            Assert.True(UndoRules.StatesMatch(new JArray(2.0, 3.0, 4.0), UndoRules.ShiftLoc(new JArray(1, 2, 3), 1, 1, 1)));
        }

        [Fact]
        public void Anything_else_is_not_a_location()
        {
            Assert.Null(UndoRules.ShiftLoc(null, 1, 1, 1));
            Assert.Null(UndoRules.ShiftLoc(new JValue(5), 1, 1, 1));
            Assert.Null(UndoRules.ShiftLoc(new JArray(), 1, 1, 1));
            Assert.Null(UndoRules.ShiftLoc(new JArray(1.0, 2.0), 1, 1, 1));
            Assert.Null(UndoRules.ShiftLoc(new JArray(new JArray(1.0, 2.0)), 1, 1, 1));
            Assert.Null(UndoRules.ShiftLoc(new JArray("a", "b", "c"), 1, 1, 1));
        }    }
}
