// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Rooms, spaces and areas, proved by running the rules. The mandate asks for
// each state to be tested separately, and the reason is that all three of
// unplaced, not_enclosed and redundant show ZERO AREA - so a single `area == 0`
// condition reports whichever word its author had in mind and is wrong about
// the other two, silently, on every model.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SpatialCensusTests
    {
        private static SpatialFact F(string kind = SpatialKind.Room, string number = "101",
                                     string name = "Office", double? area = 12.5,
                                     bool? located = true, bool? enclosed = true, bool? redundant = false)
        {
            return new SpatialFact
            {
                ElementId = 1,
                Kind = kind,
                Number = number,
                Name = name,
                AreaSqM = area,
                HasLocation = located,
                IsEnclosed = enclosed,
                IsRedundant = redundant
            };
        }

        // --------------------------------------------- the four states, apart

        [Fact]
        public void A_placed_and_bounded_room_is_placed()
        {
            Assert.Equal(SpatialState.Placed, SpatialCensusRules.StateOf(F()));
        }

        [Fact]
        public void An_unplaced_room_is_unplaced_and_not_merely_zero_area()
        {
            // It sits in no view at all: there is no boundary to be unenclosed by.
            Assert.Equal(SpatialState.Unplaced,
                SpatialCensusRules.StateOf(F(area: 0, located: false, enclosed: null)));

            // AND THE CONVERSE, which is what makes the name true: a room that IS
            // placed and measures zero is not unplaced. Without this, code deriving
            // "unplaced" from `area == 0` passes the first assertion happily - the
            // unplaced room also has zero area - and mislabels every zero-area room
            // in the model.
            Assert.NotEqual(SpatialState.Unplaced,
                SpatialCensusRules.StateOf(F(area: 0, located: true, enclosed: true)));
            Assert.NotEqual(SpatialState.Unplaced,
                SpatialCensusRules.StateOf(F(area: 0, located: true, enclosed: false)));
        }

        [Fact]
        public void A_placed_room_whose_boundary_leaks_is_not_enclosed()
        {
            Assert.Equal(SpatialState.NotEnclosed,
                SpatialCensusRules.StateOf(F(area: 0, located: true, enclosed: false)));
        }

        [Fact]
        public void A_redundant_room_is_redundant_even_though_revit_also_calls_it_unenclosed()
        {
            // THE ORDERING THAT MATTERS. Revit reports a redundant room as
            // unenclosed too; answering "not enclosed" sends somebody to hunt a
            // boundary leak that does not exist, when the fix is to delete a
            // duplicate.
            Assert.Equal(SpatialState.Redundant,
                SpatialCensusRules.StateOf(F(area: 0, located: true, enclosed: false, redundant: true)));
        }

        [Fact]
        public void A_zero_area_that_nothing_else_explains_is_reported_as_zero_area()
        {
            // The honest fallback: the measurement is zero and nothing established
            // which of the three mistakes it was.
            Assert.Equal(SpatialState.ZeroArea,
                SpatialCensusRules.StateOf(F(area: 0, located: true, enclosed: true, redundant: false)));
        }

        [Fact]
        public void An_unreadable_element_is_never_any_of_the_four()
        {
            SpatialFact f = F();
            f.Readable = false;
            Assert.Equal(SpatialState.Unreadable, SpatialCensusRules.StateOf(f));

            // A location that could not be READ has not told us the room is unplaced.
            Assert.Equal(SpatialState.Unreadable, SpatialCensusRules.StateOf(F(located: null)));
            // Nor has an area that could not be measured.
            Assert.Equal(SpatialState.Unreadable, SpatialCensusRules.StateOf(F(area: null)));
        }

        [Fact]
        public void The_reply_explains_why_one_condition_cannot_express_the_states()
        {
            Assert.Contains("three different mistakes", SpatialCensusRules.StatesMean);
            Assert.Contains("area == 0", SpatialCensusRules.StatesMean);
        }

        // --------------------------------------------- three populations

        [Fact]
        public void Rooms_spaces_and_areas_are_counted_apart()
        {
            var facts = new[]
            {
                F(SpatialKind.Room), F(SpatialKind.Room),
                F(SpatialKind.Space),
                F(SpatialKind.Area)
            };
            Assert.Equal(2, SpatialCensusRules.Tally(facts, SpatialKind.Room).Value<int>("total"));
            Assert.Equal(1, SpatialCensusRules.Tally(facts, SpatialKind.Space).Value<int>("total"));
            Assert.Equal(1, SpatialCensusRules.Tally(facts, SpatialKind.Area).Value<int>("total"));
            Assert.Contains("counted apart", SpatialCensusRules.PopulationsMean);
        }

        [Fact]
        public void An_area_scheme_is_reported_on_an_area_and_never_invented_on_a_room()
        {
            SpatialFact area = F(SpatialKind.Area);
            area.AreaScheme = "Gross Building";
            area.ViewName = "Area Plan 1";
            JObject ja = SpatialCensusRules.ToJson(area);
            Assert.Equal("Gross Building", ja.Value<string>("area_scheme"));
            Assert.Equal("Area Plan 1", ja.Value<string>("view"));

            SpatialFact room = F(SpatialKind.Room);
            room.AreaScheme = "should not appear";
            Assert.Null(SpatialCensusRules.ToJson(room).Value<string>("area_scheme"));
        }

        [Fact]
        public void Every_state_appears_in_the_tally_so_a_missing_key_never_has_to_be_guessed()
        {
            JObject t = SpatialCensusRules.Tally(new SpatialFact[0], SpatialKind.Room);
            foreach (string s in SpatialState.All) Assert.NotNull(t[s]);
            Assert.Equal(0, t.Value<int>("total"));
            Assert.True(t.Value<bool>("counts_are_exact"));
        }

        [Fact]
        public void One_unreadable_element_makes_the_counts_inexact()
        {
            SpatialFact bad = F();
            bad.Readable = false;
            JObject t = SpatialCensusRules.Tally(new[] { F(), bad }, SpatialKind.Room);
            Assert.Equal(1, t.Value<int>(SpatialState.Unreadable));
            Assert.False(t.Value<bool>("counts_are_exact"));
        }

        // ------------------------------------------------- numbers and names

        [Fact]
        public void Duplicate_numbers_are_found_within_a_kind_and_never_across_kinds()
        {
            // A room 101 and a space 101 are not duplicates of each other.
            var facts = new[]
            {
                F(SpatialKind.Room, "101"), F(SpatialKind.Room, "101"),
                F(SpatialKind.Space, "101")
            };
            Assert.Equal(new[] { "101" }, SpatialCensusRules.DuplicateNumbers(facts, SpatialKind.Room).ToArray());
            Assert.Empty(SpatialCensusRules.DuplicateNumbers(facts, SpatialKind.Space));
        }

        [Fact]
        public void An_empty_name_and_an_empty_number_are_counted_separately()
        {
            JObject t = SpatialCensusRules.Tally(
                new[] { F(number: "  ", name: "Office"), F(number: "101", name: "") }, SpatialKind.Room);
            Assert.Equal(1, t.Value<int>("number_empty"));
            Assert.Equal(1, t.Value<int>("name_empty"));
        }

        [Fact]
        public void An_unreadable_number_is_neither_empty_nor_a_duplicate()
        {
            SpatialFact f = F(number: null);
            f.NumberReadable = false;
            Assert.False(f.NumberEmpty);
            Assert.Empty(SpatialCensusRules.DuplicateNumbers(new[] { f, f }, SpatialKind.Room));
        }
    }
}
