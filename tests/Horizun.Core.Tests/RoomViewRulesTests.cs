// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Room-driven view production, the arithmetic half. Each block pins a way forty
// generated views could be forty wrong ones:
//
//   * a room silently skipped is a missing apartment on a deliverable list;
//   * a marker rotated 90 degrees too far photographs the same walls in the
//     wrong order and looks fine in the browser;
//   * a typo'd naming token passed through as literal text names every view
//     wrong before anybody notices, and the renames are the expensive half;
//   * a section whose run was estimated instead of computed clips the room.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RoomViewRulesTests
    {
        private static RoomFacts Room(bool placed = true, double area = 200,
                                      double? dx = 1, double? dy = 0,
                                      double[] min = null, double[] max = null)
        {
            return new RoomFacts
            {
                Id = 42, Name = "Kitchen", Number = "101", LevelName = "L1",
                HasLocation = placed, AreaSquareFeet = area,
                LongestSegmentDx = dx, LongestSegmentDy = dy,
                BoundingBoxMin = min ?? new[] { 0.0, 0.0, 0.0 },
                BoundingBoxMax = max ?? new[] { 12.0, 10.0, 9.0 }
            };
        }

        // ---- eligibility -------------------------------------------------------

        [Fact]
        public void A_placed_enclosed_room_is_eligible()
        {
            Assert.Null(RoomViewRules.Eligibility(Room()));
        }

        [Fact]
        public void An_unplaced_room_is_excluded_as_not_placed_even_though_its_area_is_also_zero()
        {
            // Both facts are true; the actionable one is the placement, and the code
            // must say so - "fix the enclosure" on a room that is not even placed sends
            // somebody hunting the wrong problem.
            Assert.Equal(RoomViewRules.CodeNotPlaced,
                         RoomViewRules.Eligibility(Room(placed: false, area: 0)));
        }

        [Fact]
        public void A_room_with_location_but_no_area_is_the_enclosure_code()
        {
            Assert.Equal(RoomViewRules.CodeNotEnclosed, RoomViewRules.Eligibility(Room(area: 0)));
        }

        [Fact]
        public void Every_exclusion_code_has_a_sentence_naming_the_room()
        {
            foreach (string code in new[]
            {
                RoomViewRules.CodeNotPlaced, RoomViewRules.CodeNotEnclosed,
                RoomViewRules.CodeNoBoundary, RoomViewRules.CodeNoBoundingBox
            })
            {
                string message = RoomViewRules.EligibilityMessage(Room(), code);
                Assert.Contains("42", message);
                Assert.Contains("Kitchen", message);
            }
        }

        // ---- orientation -------------------------------------------------------

        [Fact]
        public void An_axis_aligned_room_needs_no_rotation_at_all()
        {
            Assert.Equal(0.0, RoomViewRules.PrincipalRotationDegrees(Room(dx: 1, dy: 0)).Value, 9);
            Assert.Equal(0.0, RoomViewRules.PrincipalRotationDegrees(Room(dx: 0, dy: 1)).Value, 9);
            Assert.Equal(0.0, RoomViewRules.PrincipalRotationDegrees(Room(dx: -1, dy: 0)).Value, 9);
        }

        [Fact]
        public void A_thirty_degree_room_rotates_thirty_degrees()
        {
            double rad = 30.0 * Math.PI / 180.0;
            double got = RoomViewRules.PrincipalRotationDegrees(Room(dx: Math.Cos(rad), dy: Math.Sin(rad))).Value;
            Assert.Equal(30.0, got, 6);
        }

        [Fact]
        public void A_sixty_degree_room_takes_the_smaller_turn_the_marker_symmetry_allows()
        {
            // 60 degrees and -30 degrees line the four-way marker up identically; the
            // smaller turn is the deterministic answer.
            double rad = 60.0 * Math.PI / 180.0;
            double got = RoomViewRules.PrincipalRotationDegrees(Room(dx: Math.Cos(rad), dy: Math.Sin(rad))).Value;
            Assert.Equal(-30.0, got, 6);
        }

        [Fact]
        public void The_fold_boundary_is_stable_at_forty_five_degrees()
        {
            double rad = 45.0 * Math.PI / 180.0;
            double got = RoomViewRules.PrincipalRotationDegrees(Room(dx: Math.Cos(rad), dy: Math.Sin(rad))).Value;
            Assert.Equal(45.0, got, 6);

            double rad2 = 135.0 * Math.PI / 180.0;
            double got2 = RoomViewRules.PrincipalRotationDegrees(Room(dx: Math.Cos(rad2), dy: Math.Sin(rad2))).Value;
            Assert.Equal(45.0, got2, 6);
        }

        [Fact]
        public void A_room_with_no_direction_answers_null_rather_than_zero()
        {
            // Zero would claim "axis-aligned, no turn needed"; null says "unknown", and
            // the caller decides whether cardinal is an acceptable substitute.
            Assert.Null(RoomViewRules.PrincipalRotationDegrees(Room(dx: null, dy: null)));
            Assert.Null(RoomViewRules.PrincipalRotationDegrees(Room(dx: 0, dy: 0)));
            Assert.Null(RoomViewRules.PrincipalRotationDegrees(Room(dx: double.NaN, dy: 1)));
        }

        // ---- naming ------------------------------------------------------------

        [Fact]
        public void The_pattern_expands_every_known_token()
        {
            string error;
            string name = RoomViewRules.ExpandPattern("{room_number} {room_name} - {level} {kind} {index}",
                                                      Room(), "ELEV", 2, out error);
            Assert.Null(error);
            Assert.Equal("101 Kitchen - L1 ELEV 2", name);
        }

        [Fact]
        public void An_unknown_token_refuses_naming_the_known_ones_instead_of_passing_through()
        {
            string error;
            string name = RoomViewRules.ExpandPattern("{room_number} {floor}", Room(), "ELEV", 1, out error);
            Assert.Null(name);
            Assert.Contains("{floor}", error);
            Assert.Contains("room_name", error);
        }

        [Fact]
        public void An_unclosed_brace_is_an_error_with_its_position()
        {
            string error;
            Assert.Null(RoomViewRules.ExpandPattern("Room {room_name", Room(), "ELEV", 1, out error));
            Assert.Contains("unclosed", error);
        }

        [Fact]
        public void A_pattern_that_expands_to_nothing_on_a_blank_room_is_an_error_not_an_empty_name()
        {
            var blank = Room(); blank.Name = null; blank.Number = null;
            string error;
            Assert.Null(RoomViewRules.ExpandPattern("{room_number}{room_name}", blank, "ELEV", 1, out error));
            Assert.NotNull(error);
        }

        [Fact]
        public void The_upfront_validation_catches_a_bad_pattern_before_any_room_is_processed()
        {
            Assert.Null(RoomViewRules.ValidatePattern("{room_number} {kind}"));
            Assert.NotNull(RoomViewRules.ValidatePattern("{typo}"));
            Assert.NotNull(RoomViewRules.ValidatePattern(""));
        }

        // ---- kinds -------------------------------------------------------------

        [Fact]
        public void Absent_kinds_means_all_of_them_and_an_unknown_kind_refuses()
        {
            List<string> kinds;
            Assert.Null(RoomViewRules.ValidateKinds(null, out kinds));
            Assert.Equal(RoomViewRules.KnownKinds.ToList(), kinds);

            Assert.Null(RoomViewRules.ValidateKinds(new[] { "plan", "PLAN", "sections" }, out kinds));
            Assert.Equal(new[] { "plan", "sections" }, kinds.ToArray());

            Assert.NotNull(RoomViewRules.ValidateKinds(new[] { "perspective" }, out kinds));
            Assert.NotNull(RoomViewRules.ValidateKinds(new string[0], out kinds));
        }

        [Fact]
        public void Elevation_count_is_one_to_four()
        {
            Assert.Null(RoomViewRules.ValidateElevationCount(1));
            Assert.Null(RoomViewRules.ValidateElevationCount(4));
            Assert.NotNull(RoomViewRules.ValidateElevationCount(0));
            Assert.NotNull(RoomViewRules.ValidateElevationCount(5));
        }

        // ---- geometry ----------------------------------------------------------

        [Fact]
        public void The_center_is_the_box_center_and_an_unreadable_box_is_null_not_the_origin()
        {
            double[] center = RoomViewRules.Center(Room());
            Assert.Equal(6.0, center[0], 9);
            Assert.Equal(5.0, center[1], 9);
            Assert.Equal(4.5, center[2], 9);

            RoomFacts noBox = Room(); noBox.BoundingBoxMin = null;
            Assert.Null(RoomViewRules.Center(noBox));
            Assert.Null(RoomViewRules.Center(Room(min: new[] { 5.0, 0.0, 0.0 }, max: new[] { 1.0, 1.0, 1.0 })));
            Assert.Null(RoomViewRules.Center(Room(min: new[] { double.NaN, 0.0, 0.0 })));
        }

        [Fact]
        public void The_half_extent_is_the_exact_support_function_of_the_box_not_an_estimate()
        {
            RoomFacts room = Room(); // 12 x 10 box
            Assert.Equal(6.0 + 2.0, RoomViewRules.HalfExtentAlong(room, 1, 0, 2.0), 9);
            Assert.Equal(5.0 + 2.0, RoomViewRules.HalfExtentAlong(room, 0, 1, 2.0), 9);
            // Along the diagonal the support is |dx|*hx + |dy|*hy - larger than either.
            double diag = RoomViewRules.HalfExtentAlong(room, Math.Sqrt(0.5), Math.Sqrt(0.5), 0.0);
            Assert.Equal((6.0 + 5.0) * Math.Sqrt(0.5), diag, 9);
        }

        // ---- coverage ----------------------------------------------------------

        [Fact]
        public void Coverage_uses_the_shared_vocabulary_and_is_never_optimistic()
        {
            Assert.Equal("complete", RoomViewRules.Coverage(rooms: 5, planned: 5, excluded: 0));
            Assert.Equal("partial", RoomViewRules.Coverage(rooms: 5, planned: 3, excluded: 2));
            Assert.Equal("none", RoomViewRules.Coverage(rooms: 5, planned: 0, excluded: 5));
            Assert.Equal("nothing_found", RoomViewRules.Coverage(rooms: 0, planned: 0, excluded: 0));
        }
    }
}
