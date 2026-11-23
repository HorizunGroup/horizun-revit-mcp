// horizun_join_geometry without Revit: what each operation accepts, the cut priority and the
// verdict on a pair after the commit. field cycle (video 2026-10-24, E2/A3/A6): beams, walls and
// slabs with the right cut order and no concrete counted twice.
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class JoinGeometryRulesTests
    {
        [Theory]
        [InlineData("expected_document")]
        [InlineData("target_document_title")]
        public void Document_alias_is_accepted_for_read_and_write_and_keeps_guard(string alias)
        {
            foreach (string operation in new[] { "read", "join" })
            {
                var request = JObject.Parse("{\"operation\":\"" + operation + "\",\"pairs\":[[1,2]]}");
                request[alias] = "Model";
                Assert.Null(JoinGeometryRules.Validate(request));
                Assert.True(ReadDocumentRules.Allows(ReadDocumentRules.Target(request), "Model"));
                Assert.False(ReadDocumentRules.Allows(ReadDocumentRules.Target(request), "Other"));
            }
        }
        private static JoinGeometryRules.CurrentPair Fresh(bool? joined, bool? cuts = null) =>
            new JoinGeometryRules.CurrentPair { Joined = joined, ACuts = cuts, Touching = true, HasSolids = true };

        [Fact]
        public void JoinRegeneratesAndResolvesCurrentMembersBeforeReadingOrSwitchingCutOrder()
        {
            var calls = new List<string>();
            bool joined = false, regenerated = false;
            JoinGeometryRules.ApplyCurrentPair("join", 1,
                () =>
                {
                    calls.Add("read");
                    if (joined) Assert.True(regenerated); // Native snapshots must be regenerated first.
                    return Fresh(joined, joined ? false : (bool?)null);
                },
                action => { calls.Add(action); if (action == "join") joined = true; },
                () => { calls.Add("regenerate"); regenerated = true; });
            Assert.Equal(new[] { "read", "join", "regenerate", "read", "read", "switch_order", "regenerate" }, calls);
        }

        [Fact]
        public void AlreadyJoinedPairIsNeverJoinedAgainAndMatchingPriorityNeverSwitches()
        {
            var calls = new List<string>();
            JoinGeometryRules.ApplyCurrentPair("join", 1, () => Fresh(true, true), calls.Add,
                () => calls.Add("regenerate"));
            Assert.Empty(calls);
        }

        [Theory]
        [InlineData("member_missing_or_invalid: deleted by a previous pair")]
        [InlineData("group_member: group changed since planning")]
        [InlineData("nested_member: nested instance")]
        public void InvalidCurrentMembersNeverReachANativeMutation(string reason)
        {
            var calls = new List<string>();
            var ex = Assert.Throws<InvalidOperationException>(() => JoinGeometryRules.ApplyCurrentPair("join", 1,
                () => new JoinGeometryRules.CurrentPair { InvalidReason = reason }, calls.Add,
                () => calls.Add("regenerate")));
            Assert.Equal(reason, ex.Message);
            Assert.Empty(calls);
        }

        [Fact]
        public void DisappearingMemberAfterJoinNeverReachesCutOrderApis()
        {
            int reads = 0;
            var calls = new List<string>();
            Assert.Throws<InvalidOperationException>(() => JoinGeometryRules.ApplyCurrentPair("join", 1,
                () => ++reads == 1 ? Fresh(false) : new JoinGeometryRules.CurrentPair { InvalidReason = "member_missing_or_invalid" },
                calls.Add, () => calls.Add("regenerate")));
            Assert.Equal(new[] { "join", "regenerate" }, calls);
        }

        [Fact]
        public void JoinNotRetainedAfterRegenerationNeverReachesSwitch()
        {
            var calls = new List<string>();
            var ex = Assert.Throws<InvalidOperationException>(() => JoinGeometryRules.ApplyCurrentPair("join", -1,
                () => Fresh(false), calls.Add, () => calls.Add("regenerate")));
            Assert.StartsWith("join_not_retained:", ex.Message);
            Assert.Equal(new[] { "join", "regenerate" }, calls);
        }

        [Theory]
        [InlineData(false, "not_joined:")]
        [InlineData(null, "join_state_unreadable:")]
        public void UnjoinedOrUnreadableSwitchIsRefusedBeforeNativeMutation(bool? joined, string reason)
        {
            var calls = new List<string>();
            var ex = Assert.Throws<InvalidOperationException>(() => JoinGeometryRules.ApplyCurrentPair("switch_order", -1,
                () => Fresh(joined), calls.Add, () => calls.Add("regenerate")));
            Assert.StartsWith(reason, ex.Message);
            Assert.Empty(calls);
        }

        [Theory]
        [InlineData(false, true, "no_longer_touching:")]
        [InlineData(true, false, "no_readable_solid:")]
        public void PairWhoseCurrentGeometryChangedIsSkippedBeforeNativeJoin(bool touching, bool solids, string reason)
        {
            var calls = new List<string>();
            var ex = Assert.Throws<InvalidOperationException>(() => JoinGeometryRules.ApplyCurrentPair("join", 0,
                () => new JoinGeometryRules.CurrentPair { Joined = false, Touching = touching, HasSolids = solids },
                calls.Add, () => calls.Add("regenerate")));
            Assert.StartsWith(reason, ex.Message);
            Assert.Empty(calls);
        }

        [Fact]
        public void LostJoinImmediatelyBeforeSwitchIsRefused()
        {
            int reads = 0;
            var calls = new List<string>();
            var ex = Assert.Throws<InvalidOperationException>(() => JoinGeometryRules.ApplyCurrentPair("join", 1,
                () => ++reads == 1 ? Fresh(true, false) : Fresh(false), calls.Add, () => calls.Add("regenerate")));
            Assert.StartsWith("not_joined:", ex.Message);
            Assert.Empty(calls);
        }

        private static JObject Join() => new JObject
        {
            ["target_document"] = "EST", ["operation"] = "join", ["pairs"] = new JArray(new JArray(10, 20), new JArray(10, 30))
        };

        private static JObject AutoPriority() => new JObject
        {
            ["target_document"] = "Structure", ["operation"] = "auto_priority", ["level_id"] = 10,
            ["priority"] = new JArray("floor", "wall")
        };

        [Fact]
        public void Level_priority_requires_an_explicit_neutral_rule_and_a_positive_level()
        {
            JObject request = AutoPriority();
            Assert.Null(JoinGeometryRules.Validate(request));
            request.Remove("priority");
            Assert.Contains("priority", JoinGeometryRules.Validate(request));
            request["priority"] = JValue.CreateNull();
            Assert.Contains("priority", JoinGeometryRules.Validate(request));
            request["priority"] = new JArray();
            Assert.Contains("priority", JoinGeometryRules.Validate(request));
            request["priority"] = new JArray("wall", "wall");
            Assert.Contains("twice", JoinGeometryRules.Validate(request));
            request["priority"] = new JArray("unknown_category");
            Assert.Contains("not a class", JoinGeometryRules.Validate(request));
            request = AutoPriority(); request["level_id"] = 0;
            Assert.Contains("positive", JoinGeometryRules.Validate(request));
            request["level_id"] = -1;
            Assert.Contains("positive", JoinGeometryRules.Validate(request));
            request["level_id"] = 10.5;
            Assert.Contains("positive", JoinGeometryRules.Validate(request));
        }

        [Fact]
        public void Level_priority_does_not_silently_complete_omitted_categories()
        {
            var priority = new List<string> { "floor", "wall" };
            Assert.Null(JoinGeometryRules.PriorityCoverageError(new[] { "wall", "floor", "wall" }, priority));
            Assert.Contains("structural_column", JoinGeometryRules.PriorityCoverageError(new[] { "wall", "structural_column" }, priority));
            Assert.Equal(1, JoinGeometryRules.Cutter("floor", "wall", priority));
            priority.Reverse();
            Assert.Equal(-1, JoinGeometryRules.Cutter("floor", "wall", priority));
            Assert.Equal(0, JoinGeometryRules.Cutter("wall", "wall", priority));
        }

        [Fact]
        public void Closure_uses_the_total_not_independent_pair_tolerances()
        {
            double? sum = JoinGeometryRules.SharedVolumeSum(new double?[] { 0.0000006, 0.0000006 });
            Assert.Equal(0.0000012, sum.Value, 12);
            Assert.False(JoinGeometryRules.SharedVolumeClosed(sum));
            Assert.True(JoinGeometryRules.SharedVolumeClosed(JoinGeometryRules.SharedVolumeSum(new double?[] { 0, 0 })));
            Assert.True(JoinGeometryRules.SharedVolumeClosed(JoinGeometryRules.OverlapToleranceM3));
            Assert.True(JoinGeometryRules.SharedVolumeClosed(JoinGeometryRules.SharedVolumeSum(Array.Empty<double?>())));
            Assert.Equal(0.6, JoinGeometryRules.SharedVolumeSum(new double?[] { 0.1, 0.2, 0.3 }).Value, 12);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-0.1)]
        public void Unknown_or_invalid_intersections_never_become_zero(double? reading)
        {
            Assert.Null(JoinGeometryRules.SharedVolumeSum(new double?[] { 0.2, reading, 0 }));
            Assert.False(JoinGeometryRules.SharedVolumeClosed(reading));
            Assert.Contains("could not be measured", JoinGeometryRules.JoinedPairVerdict(true, 1, true, reading));
        }

        [Fact]
        public void GeometryTraversalDiscardsEarlierSolidsWhenALaterInstanceFails()
        {
            static IEnumerable<int> Geometry()
            {
                yield return 1; // A readable solid precedes an unreadable nested geometry instance.
                throw new InvalidOperationException("Nested geometry is unreadable.");
            }
            List<int> solids = JoinGeometryRules.CollectComplete<int>(found =>
            {
                foreach (int solid in Geometry()) found.Add(solid);
            });
            Assert.Empty(solids);
        }

        [Fact]
        public void GeometryTraversalPublishesAllSolidsOnlyAfterTheWholeReadSucceeds()
        {
            Assert.Equal(new[] { 1, 2 }, JoinGeometryRules.CollectComplete<int>(found =>
            {
                found.Add(1); found.Add(2);
            }));
            Assert.Empty(JoinGeometryRules.CollectComplete<int>(found =>
            {
                found.Add(1);
                throw new ArgumentNullException("geometryBranch");
            }));
        }

        [Fact]
        public void A_well_formed_join_passes()
            => Assert.Null(JoinGeometryRules.Validate(Join()));

        [Fact]
        public void A_write_needs_its_document_and_a_read_does_not()
        {
            var r = Join(); r.Remove("target_document");
            Assert.Contains("target_document", JoinGeometryRules.Validate(r));
            r["operation"] = "read";
            Assert.Null(JoinGeometryRules.Validate(r));
        }

        [Fact]
        public void Pairs_or_element_ids_exactly_one()
        {
            var r = Join(); r["element_ids"] = new JArray(1, 2);
            Assert.Contains("exactly one", JoinGeometryRules.Validate(r));
            r = Join(); r.Remove("pairs");
            Assert.Contains("exactly one", JoinGeometryRules.Validate(r));
            r = new JObject { ["target_document"] = "EST", ["operation"] = "unjoin" };
            Assert.Contains("pairs is required", JoinGeometryRules.Validate(r));
        }

        [Fact]
        public void A_pair_is_two_distinct_ids_and_never_repeated_in_either_order()
        {
            var r = Join(); r["pairs"] = new JArray { new JArray(10, 10) };
            Assert.Contains("itself", JoinGeometryRules.Validate(r));
            r["pairs"] = new JArray(new JArray(10, 20), new JArray(20, 10));
            Assert.Contains("repeats", JoinGeometryRules.Validate(r));
            r["pairs"] = new JArray { new JArray(10) };
            Assert.Contains("pairs[0]", JoinGeometryRules.Validate(r));
            r["pairs"] = new JArray();
            Assert.Contains("1..200", JoinGeometryRules.Validate(r));
        }

        [Fact]
        public void First_cuts_needs_pairs_and_priority_only_rides_cut_order_priority()
        {
            var r = new JObject { ["target_document"] = "EST", ["operation"] = "join", ["element_ids"] = new JArray(1, 2, 3), ["cut_order"] = "first_cuts" };
            Assert.Contains("first_cuts needs pairs", JoinGeometryRules.Validate(r));
            r["cut_order"] = "priority";
            Assert.Null(JoinGeometryRules.Validate(r));
            r["priority"] = new JArray("structural_framing", "structural_column");
            Assert.Null(JoinGeometryRules.Validate(r));
            r["priority"] = new JArray("beam");
            Assert.Contains("not a class", JoinGeometryRules.Validate(r));
            r = Join(); r["priority"] = new JArray("wall");
            Assert.Contains("only with cut_order=priority", JoinGeometryRules.Validate(r));
        }

        [Fact]
        public void A_field_of_another_operation_is_refused_by_name()
        {
            var r = Join(); r["operation"] = "unjoin"; r["cut_order"] = "priority";
            Assert.Contains("cut_order", JoinGeometryRules.Validate(r));
            r = Join(); r["operation"] = "explode";
            Assert.Contains("operation must be one of", JoinGeometryRules.Validate(r));
        }

        [Theory]
        [InlineData("OST_StructuralColumns", null, "structural_column")]
        [InlineData("OST_StructuralFraming", null, "structural_framing")]
        [InlineData("OST_StructuralFoundation", null, "structural_foundation")]
        [InlineData("OST_Walls", true, "wall_structural")]
        [InlineData("OST_Walls", false, "wall")]
        [InlineData("OST_Walls", null, "wall")]
        [InlineData("OST_Floors", true, "floor_structural")]
        [InlineData("OST_Floors", false, "floor")]
        [InlineData("OST_Roofs", null, "roof")]
        [InlineData("OST_Roofs", true, "roof")]
        [InlineData("OST_Doors", null, null)]
        public void Each_category_takes_its_class(string token, bool? structural, string expected)
            => Assert.Equal(expected, JoinGeometryRules.ClassOf(token, structural));

        [Fact]
        public void The_default_priority_puts_columns_over_beams_over_structural_walls_over_slabs()
        {
            List<string> p = JoinGeometryRules.PriorityOf(null);
            Assert.Equal(1, JoinGeometryRules.Cutter("structural_column", "structural_framing", p));
            Assert.Equal(-1, JoinGeometryRules.Cutter("floor_structural", "structural_framing", p));
            Assert.Equal(1, JoinGeometryRules.Cutter("wall_structural", "floor_structural", p));
            Assert.Equal(1, JoinGeometryRules.Cutter("floor_structural", "wall", p));
            Assert.Equal(0, JoinGeometryRules.Cutter("wall", "wall", p));
        }

        [Fact]
        public void A_caller_priority_comes_first_and_the_rest_keep_the_default_order()
        {
            List<string> p = JoinGeometryRules.PriorityOf(new JArray("floor_structural"));
            Assert.Equal("floor_structural", p[0]);
            Assert.Equal(JoinGeometryRules.Classes.Length, p.Count);
            Assert.Equal(1, JoinGeometryRules.Cutter("floor_structural", "structural_framing", p));
        }

        [Theory]
        [InlineData(false, null, 1, "join_a_cuts")]
        [InlineData(false, null, -1, "join_b_cuts")]
        [InlineData(false, null, 0, "join")]
        [InlineData(true, true, 1, "nothing")]
        [InlineData(true, false, 1, "switch")]
        [InlineData(true, true, -1, "switch")]
        [InlineData(true, null, 1, "switch_if_needed")]
        [InlineData(true, false, 0, "nothing")]
        public void What_a_join_does_depends_on_the_current_state(bool joined, bool? aCuts, int wanted, string expected)
            => Assert.Equal(expected, JoinGeometryRules.JoinAction(joined, aCuts, wanted));

        [Fact]
        public void A_pair_holds_only_when_joined_cut_right_and_no_volume_is_left_shared()
        {
            Assert.Null(JoinGeometryRules.JoinedPairVerdict(true, 1, true, 0.0));
            Assert.Null(JoinGeometryRules.JoinedPairVerdict(true, 0, null, 0.0000005));
            Assert.Contains("not joined", JoinGeometryRules.JoinedPairVerdict(false, 1, null, 0));
            Assert.Contains("wrong element", JoinGeometryRules.JoinedPairVerdict(true, 1, false, 0));
            Assert.Contains("could not be read", JoinGeometryRules.JoinedPairVerdict(true, -1, null, 0));
            Assert.Contains("still share", JoinGeometryRules.JoinedPairVerdict(true, 1, true, 0.12));
            // An unmeasured volume is unknown, never zero.
            Assert.Contains("could not be measured", JoinGeometryRules.JoinedPairVerdict(true, 1, true, null));
        }

        [Fact]
        public void Boxes_touch_within_the_tolerance_and_not_beyond()
        {
            double[] a = { 0, 0, 0, 1, 1, 1 };
            Assert.True(JoinGeometryRules.BoxesTouch(a, new double[] { 1, 0, 0, 2, 1, 1 }, 0.001));
            Assert.True(JoinGeometryRules.BoxesTouch(a, new double[] { 1.0005, 0, 0, 2, 1, 1 }, 0.001));
            Assert.False(JoinGeometryRules.BoxesTouch(a, new double[] { 1.01, 0, 0, 2, 1, 1 }, 0.001));
            Assert.False(JoinGeometryRules.BoxesTouch(a, null, 0.001));
        }

        [Fact]
        public void The_pair_key_ignores_order()
            => Assert.Equal(JoinGeometryRules.Key(5, 9), JoinGeometryRules.Key(9, 5));

        [Fact]
        public void Successful_empty_boolean_is_zero_but_failed_boolean_stays_unknown()
        {
            Assert.Equal(0, JoinGeometryRules.IntersectionVolume(true, null));
            Assert.Equal(0, JoinGeometryRules.IntersectionVolume(true, 0));
            Assert.Null(JoinGeometryRules.IntersectionVolume(false, null));
            Assert.Null(JoinGeometryRules.IntersectionVolume(false, 0));
            Assert.Null(JoinGeometryRules.IntersectionVolume(true, double.NaN));
            Assert.Equal(16.79, JoinGeometryRules.MemberVolumeReduction(140.95, 124.16).Value, 6);
            Assert.Null(JoinGeometryRules.MemberVolumeReduction(null, 0));
        }

        [Fact]
        public void Per_pair_is_an_explicit_join_verification_mode()
        {
            var request = Join(); request["verification_mode"] = "per_pair";
            Assert.Null(JoinGeometryRules.Validate(request));
            request["verification_mode"] = "ignore";
            Assert.Contains("verification_mode", JoinGeometryRules.Validate(request));
            Assert.Contains("exactly one", JoinGeometryRules.Validate(new JObject { ["operation"] = "read" }));
        }
    }
}
