using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Core.Tests
{
    public class CopyRepresentationRulesTests
    {
        private readonly ITestOutputHelper output;

        public CopyRepresentationRulesTests(ITestOutputHelper output) => this.output = output;

        private static CopySignature S(long id, string type = "Family A: Type 1", bool isType = false) => new CopySignature { Id = id, Category = "OST_GenericModel", Type = type, IsType = isType, Position = new[] { 0d, 0d, 0d } };

        [Fact]
        public void One_copied_instance_cannot_represent_two_source_instances()
        {
            Assert.Equal(new long[] { 2 }, CopyRepresentationRules.Missing(new[] { S(1), S(2) }, new[] { S(11) }, null));
        }

        [Fact]
        public void Duplicate_returned_ids_do_not_inflate_verified_copy_count()
        {
            Assert.Equal(new long[] { 2 }, CopyRepresentationRules.Missing(new[] { S(1), S(2) }, new[] { S(11), S(11) }, null));
        }

        [Fact]
        public void Different_families_with_same_type_name_are_not_interchangeable()
        {
            Assert.Equal(new long[] { 1 }, CopyRepresentationRules.Missing(new[] { S(1) }, new[] { S(11, "Family B: Type 1") }, null));
        }

        [Fact]
        public void Destination_type_can_be_reused_only_for_a_type_request()
        {
            Assert.Empty(CopyRepresentationRules.Missing(new[] { S(1, isType: true) }, new CopySignature[0], new[] { S(11, isType: true) }));
            Assert.Equal(new long[] { 1 }, CopyRepresentationRules.Missing(new[] { S(1) }, new CopySignature[0], new[] { S(11, isType: true) }));
        }

        [Fact]
        public void Unreadable_identity_is_never_a_match_even_when_both_reads_are_null()
        {
            Assert.Equal(new long[] { 1 }, CopyRepresentationRules.Missing(new[] { S(1, null) }, new[] { S(11, null) }, null));
        }

        [Fact]
        public void Dependents_can_arrive_without_masking_a_missing_source_type()
        {
            Assert.Equal(new long[] { 2 }, CopyRepresentationRules.Missing(new[] { S(1), S(2, "Family C: Type 2") },
                new[] { S(11), S(12), S(13) }, null));
        }

        [Fact]
        public void Same_family_type_at_wrong_offset_is_not_the_requested_copy()
        {
            CopySignature requested = S(1), found = S(11);
            requested.Position = new[] { 10d, 0d, 2d };
            Assert.Equal(new long[] { 1 }, CopyRepresentationRules.Missing(new[] { requested }, new[] { found }, null));
            found.Position = new[] { 10d, 0d, 2d };
            Assert.Empty(CopyRepresentationRules.Missing(new[] { requested }, new[] { found }, null));
        }

        [Fact]
        public void Unreadable_position_cannot_verify_an_instance()
        {
            CopySignature requested = S(1), found = S(11);
            found.Position = null;
            Assert.Equal(new long[] { 1 }, CopyRepresentationRules.Missing(new[] { requested }, new[] { found }, null));
        }
        [Fact]
        public void Api_return_order_does_not_decide_source_to_copy_mapping()
        {
            CopySignature a = S(1), b = S(2), ac = S(11), bc = S(12);
            b.Position = bc.Position = new[] { 0d, 0d, 10d };
            var map = CopyRepresentationRules.Map(new[] { a, b }, new[] { bc, ac }, null);
            Assert.Equal(11L, map[1]);
            Assert.Equal(12L, map[2]);
        }

        [Fact]
        public void Equal_centres_do_not_mask_different_geometry_extents()
        {
            CopySignature a = S(1), b = S(11);
            a.BoundsMin = new[] { -1d, -1d, -1d }; a.BoundsMax = new[] { 1d, 1d, 1d };
            b.BoundsMin = new[] { -2d, -1d, -1d }; b.BoundsMax = new[] { 2d, 1d, 1d };
            Assert.Empty(CopyRepresentationRules.Map(new[] { a }, new[] { b }, null));
            b.BoundsMin = a.BoundsMin; b.BoundsMax = a.BoundsMax;
            Assert.Equal(11L, CopyRepresentationRules.Map(new[] { a }, new[] { b }, null)[1]);
        }

        [Fact]
        public void Displaced_geometry_samples_match_unordered_but_not_a_different_shape()
        {
            CopySignature source = S(1);
            source.GeometryPoints = new[] { new[] { 0d, 0d, 0d }, new[] { 1d, 1d, 0d } };
            CopySignature expected = CopyRepresentationRules.Translate(source, 0, 0, 10);
            CopySignature found = S(11); found.Position = new[] { 0d, 0d, 10d };
            found.GeometryPoints = new[] { new[] { 1d, 1d, 10d }, new[] { 0d, 0d, 10d } };
            Assert.Equal(11L, CopyRepresentationRules.Map(new[] { expected }, new[] { found }, null)[1]);
            found.GeometryPoints[0][0] = 0.5;
            Assert.Empty(CopyRepresentationRules.Map(new[] { expected }, new[] { found }, null));
        }

        [Fact]
        public void Wall_copy_matches_the_translated_axis_even_when_its_model_box_changes()
        {
            CopySignature source = S(1), found = S(11);
            source.Layout = new CopyLayout { Kind = "wall_axis", Base = 2, Top = 12, Thickness = 0.5,
                Facing = new[] { 0d, 1d, 0d }, Curves = new[] { Edge(0, 0, 10, 0) } };
            source.BoundsMin = new[] { 0.2, -0.25, 2d };
            source.BoundsMax = new[] { 10d, 0.25, 12d };
            source.GeometryPoints = new[] { new[] { 0.25, 0.25, 2d } };
            source.Solid = MeasuredSolid(48, join: new[] { new[] { -0.5, -0.5, 2d }, new[] { 0.5, 0.5, 12d } });
            var expected = CopyRepresentationRules.Translate(source, 20, 30, 4);
            found.Position = expected.Position;
            found.Layout = new CopyLayout { Kind = "wall_axis", Base = 6, Top = 16, Thickness = 0.5,
                Facing = source.Layout.Facing, Curves = new[] { Edge(30, 30, 20, 30) } };
            found.BoundsMin = new[] { 20d, 29.75, 6d };
            found.BoundsMax = new[] { 30.3, 30.25, 16d };
            found.GeometryPoints = new[] { new[] { 20.25, 30.25, 6d } };
            found.Solid = MeasuredSolid(48);
            Assert.Equal(11L, CopyRepresentationRules.Map(new[] { expected }, new[] { found }, null)[1]);
            Assert.Equal("volume_and_samples", CopyRepresentationRules.LayoutSolidCheck(expected, found));
            found.GeometryPoints[0][0] = 25;
            Assert.Null(CopyRepresentationRules.MatchBasis(expected, found));
            found.GeometryPoints[0][0] = 20.25;

            found.Layout.Top += 1;
            Assert.Equal(1, CopyRepresentationRules.LayoutDelta(expected.Layout, found.Layout), 8);
            Assert.Empty(CopyRepresentationRules.Map(new[] { expected }, new[] { found }, null));
        }

        [Fact]
        public void Floor_copy_matches_sketch_after_box_changes_and_refuses_a_changed_opening()
        {
            CopySignature source = S(1), found = S(11);
            double[][][] border = { Edge(0, 0, 10, 0), Edge(10, 0, 10, 10), Edge(10, 10, 0, 10), Edge(0, 10, 0, 0) };
            double[][][] opening = { Edge(2, 2, 4, 2), Edge(4, 2, 4, 4), Edge(4, 4, 2, 4), Edge(2, 4, 2, 2) };
            source.Layout = new CopyLayout { Kind = "floor_sketch", Base = 2, Top = 3, Thickness = 1,
                Curves = border.Concat(opening).ToArray() };
            source.BoundsMin = new[] { 0d, 0d, 2d };
            found.BoundsMin = new[] { 0.1, 0d, 2d };
            found.BoundsMax = source.BoundsMax = new[] { 10d, 10d, 3d };
            found.Layout = new CopyLayout { Kind = "floor_sketch", Base = 2, Top = 3.0001, Thickness = 1,
                Curves = Enumerable.Reverse(source.Layout.Curves).ToArray() };
            source.GeometryPoints = found.GeometryPoints = new[] { new[] { 0d, 0d, 2d } };
            source.Solid = found.Solid = MeasuredSolid(96, loop: new[] { new[] { 2d, 2d, 3d }, new[] { 4d, 2d, 3d },
                new[] { 4d, 4d, 3d }, new[] { 2d, 4d, 3d } });
            Assert.Equal("volume_and_samples", CopyRepresentationRules.LayoutSolidCheck(source, found));
            Assert.Equal(11L, CopyRepresentationRules.Map(new[] { source }, new[] { found }, null)[1]);

            found.Layout.Curves[0] = Edge(2, 5, 2, 2);
            Assert.True(CopyRepresentationRules.LayoutDelta(source.Layout, found.Layout) > CopyRepresentationRules.LayoutToleranceFeet);
            Assert.Empty(CopyRepresentationRules.Map(new[] { source }, new[] { found }, null));
        }

        [Theory]
        [InlineData(0.09, 0.04, -0.04)]
        [InlineData(0.15, 0.07, 0)]
        public void Overlapping_tolerances_find_a_complete_mapping_in_both_return_orders(
            double secondWall, double sharedCandidate, double firstWallOnly)
        {
            var requested = new[] { InstanceAt(1, 0, "wall_axis"), InstanceAt(2, secondWall, "wall_axis") };
            var copies = new[] { InstanceAt(11, sharedCandidate, "wall_axis"), InstanceAt(12, firstWallOnly, "wall_axis") };
            foreach (var arrived in new[] { copies, Enumerable.Reverse(copies).ToArray() })
            {
                var map = CopyRepresentationRules.Map(requested, arrived, null);
                Assert.Equal(12L, map[1]);
                Assert.Equal(11L, map[2]);
            }
        }

        [Theory]
        [InlineData("wall_axis", 1)] // A hosted insert can cut the wall all the way to its bottom.
        [InlineData("floor_sketch", 0)] // A shaft can cut a hole outside the floor sketch.
        public void Identical_layout_does_not_verify_a_lost_external_opening_even_with_joins(string kind, int cutters)
        {
            var source = InstanceAt(1, 0, kind);
            var copy = InstanceAt(11, 0, kind);
            var loop = new[] { new[] { 2d, 2d, 3d }, new[] { 4d, 2d, 3d }, new[] { 4d, 4d, 3d }, new[] { 2d, 4d, 3d } };
            var join = new[] { new[] { -1d, -1d, 0d }, new[] { 1d, 1d, 12d } };
            source.Solid = MeasuredSolid(96, join: join, cutters: cutters, loop: cutters == 0 ? loop : null);
            copy.Solid = MeasuredSolid(100, join: join);
            Assert.Equal(0, CopyRepresentationRules.LayoutDelta(source.Layout, copy.Layout));
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));

            var shifted = CopyRepresentationRules.Translate(source, 10, 20, 30);
            copy = CopyRepresentationRules.Translate(source, 10, 20, 30);
            copy.Id = 11;
            Assert.Equal(kind, CopyRepresentationRules.MatchBasis(shifted, copy));
            if (cutters == 0)
            {
                copy.Solid.OpeningLoops[0][0][0] += 1;
                Assert.Null(CopyRepresentationRules.MatchBasis(shifted, copy));
            }
        }

        [Fact]
        public void Matching_sketch_cannot_hide_missing_solid_evidence_or_unexplained_volume_change()
        {
            var source = InstanceAt(1, 0, "floor_sketch");
            var copy = InstanceAt(11, 0, "floor_sketch");
            copy.Solid = null;
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
            copy.Solid = MeasuredSolid(99);
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
        }

        [Theory]
        [InlineData("floor_sketch", 6.2166, 6.24)]
        [InlineData("wall_axis", 2.25285, 3.312)]
        public void Intrinsic_solid_verifies_with_a_warning_for_omitted_cuts_and_joins(
            string kind, double sourceM3, double copyM3)
        {
            var (source, copy) = IntrinsicPair(kind, 0, sourceM3, copyM3);
            Assert.Equal("external_cut_count_differs", CopyRepresentationRules.SolidMismatch(source, copy));
            Assert.Equal(kind, CopyRepresentationRules.MatchBasis(source, copy));
            Assert.Equal(11L, CopyRepresentationRules.Map(new[] { source }, new[] { copy }, null)[1]);
            Assert.Equal("intrinsic_solid_with_omitted_relationships", CopyRepresentationRules.LayoutSolidCheck(source, copy));
            JObject warning = CopyRepresentationRules.OmittedRelationshipWarning(source, copy);
            Assert.Equal(Enumerable.Range(20, 6).Select(id => (long)id),
                warning["omitted_cut_source_ids"].Values<long>());
            Assert.Equal(Enumerable.Range(30, 6).Select(id => (long)id),
                warning["omitted_join_source_ids"].Values<long>());
            Assert.Equal(copyM3 - sourceM3, warning.Value<double>("volume_delta_m3"), 10);
        }

        [Theory]
        [InlineData("offset")]
        [InlineData("axis")]
        [InlineData("bounds")]
        [InlineData("type")]
        [InlineData("thickness")]
        [InlineData("layer")]
        [InlineData("sketch_opening")]
        [InlineData("intrinsic_samples")]
        [InlineData("edited_wall_profile")]
        [InlineData("equal_volume_different_solid")]
        [InlineData("missing_measurement")]
        [InlineData("requested_cut")]
        public void Omitted_cuts_do_not_justify_an_unverified_copy(string difference)
        {
            string kind = difference == "edited_wall_profile" ? "wall_axis" : "floor_sketch";
            var (source, copy) = IntrinsicPair(kind, 0, 6.2166, 6.24);
            switch (difference)
            {
                case "offset": copy.Position = new[] { 1 / 304.8, 0d, 0d }; break;
                case "axis": copy.Layout.Curves = new[] { OffsetEdge(1) }; break;
                case "bounds": copy.Intrinsic.BoundsMax = new[] { 10d, 1d, 13d }; break;
                case "type": copy.Type += " other"; break;
                case "thickness": copy.Layout.Thickness += 1 / 304.8; break;
                case "layer": copy.Layers[0].Definition = "different material"; break;
                case "sketch_opening":
                    source.Layout.Curves = source.Layout.Curves.Concat(new[]
                    {
                        Edge(2, 2, 4, 2), Edge(4, 2, 4, 4), Edge(4, 4, 2, 4), Edge(2, 4, 2, 2)
                    }).ToArray();
                    break;
                case "intrinsic_samples": copy.Intrinsic.Samples = new[] { new[] { 0d, 0d, 1d } }; break;
                case "edited_wall_profile": source.Intrinsic.Volume -= 0.01; break;
                case "equal_volume_different_solid": copy.Intrinsic.IntersectionVolume -= 0.01; break;
                case "missing_measurement": copy.Intrinsic.ActualVolume = null; break;
                case "requested_cut": source.Solid.RequestedRelationshipIds = new HashSet<long> { 22 }; break;
            }
            Assert.Empty(CopyRepresentationRules.Map(new[] { source }, new[] { copy }, null));
            Assert.Null(CopyRepresentationRules.OmittedRelationshipWarning(source, copy));
        }

        [Fact]
        public void An_isolated_solid_cannot_verify_a_retained_cutter_when_other_cuts_were_omitted()
        {
            var (source, copy) = IntrinsicPair("wall_axis", 2, 2.25285, 3.312);
            source.Solid.RequestedRelationshipIds = new HashSet<long> { 20 };
            Assert.Null(CopyRepresentationRules.IntrinsicMismatch(source, copy));
            Assert.Equal("retained_relationship_effect_unavailable", CopyRepresentationRules.RelationshipMismatch(source, copy));
            Assert.Empty(CopyRepresentationRules.Map(new[] { source }, new[] { copy }, null));
            Assert.Null(CopyRepresentationRules.OmittedRelationshipWarning(source, copy));
        }

        [Theory]
        [InlineData("same", true)]
        [InlineData("moved_profile", false)]
        [InlineData("same_volume_wrong_material", false)]
        public void Retained_wall_cuts_must_match_the_measured_profile_and_actual_material(string change, bool verified)
        {
            var (source, copy) = IntrinsicPair("wall_axis", 2, 2.25285, 3.312);
            for (int i = 0; i < copy.Solid.Cutters.Length; i++)
            {
                source.Solid.Cutters[i].CutProfile = copy.Solid.Cutters[i].CutProfile = Edge(i, 0, i, 1);
            }
            copy.Intrinsic.ExpectedVolumeWithCuts = copy.Intrinsic.ActualVolume = copy.Intrinsic.IntersectionVolume = 240;
            if (change == "moved_profile") copy.Solid.Cutters[0].CutProfile = Edge(0, 0, 0, 0.5);
            if (change == "same_volume_wrong_material") copy.Intrinsic.IntersectionVolume -= 0.01;
            Assert.Equal(verified, CopyRepresentationRules.MatchBasis(source, copy) != null);
            Assert.Equal(verified, CopyRepresentationRules.OmittedRelationshipWarning(source, copy) != null);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void Missing_layers_fail_even_when_final_solids_are_equal(bool sourceRead, bool copyRead)
        {
            var (source, copy) = EqualJoinedHosts();
            if (!sourceRead) source.Layers = null;
            if (!copyRead) copy.Layers = null;
            Assert.Null(CopyRepresentationRules.SolidMismatch(source, copy));
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
            Assert.Null(CopyRepresentationRules.LayoutSolidCheck(source, copy));
        }

        [Theory]
        [InlineData("missing", "requested_relationship_missing")]
        [InlineData("opposite_direction", "retained_relationship_differs")]
        [InlineData("unreadable", "relationship_inventory_unavailable")]
        public void Requested_joins_are_checked_even_when_they_leave_the_solid_unchanged(string difference, string reason)
        {
            var (source, copy) = EqualJoinedHosts();
            Assert.Equal("wall_axis", CopyRepresentationRules.MatchBasis(source, copy));
            if (difference == "missing") copy.Solid.Joined = Array.Empty<CopySignature>();
            if (difference == "opposite_direction") copy.Solid.Joined[0].RelationshipRole = "cut_by_host";
            if (difference == "unreadable") copy.Solid.Joined = null;
            Assert.Null(CopyRepresentationRules.SolidMismatch(source, copy));
            Assert.Equal(reason, CopyRepresentationRules.RelationshipMismatch(source, copy));
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
        }

        [Theory]
        [InlineData("join")]
        [InlineData("cut")]
        public void Requested_relationship_must_point_to_the_mapped_copy(string kind)
        {
            var (source, copy) = EqualJoinedHosts();
            if (kind == "cut")
            {
                source.Solid.Cutters = source.Solid.Joined;
                copy.Solid.Cutters = copy.Solid.Joined;
                source.Solid.Joined = copy.Solid.Joined = Array.Empty<CopySignature>();
                source.Solid.ExternalCutCount = copy.Solid.ExternalCutCount = 1;
            }
            CopySignature sourceParticipant = source.Solid.Cutters.Concat(source.Solid.Joined).Single();
            CopySignature observedParticipant = copy.Solid.Cutters.Concat(copy.Solid.Joined).Single();
            var copiedParticipant = CopyRepresentationRules.Translate(observedParticipant, 0, 0, 0);
            var requested = new[] { source, sourceParticipant };
            var returned = new[] { copiedParticipant, copy };

            Assert.Equal(2, CopyRepresentationRules.Map(requested, returned, null).Count);

            // Equal signatures, including cutting direction. This ID belongs to an existing
            // destination element and happens to equal the source ID in its own document.
            observedParticipant.Id = sourceParticipant.Id;
            Assert.Null(CopyRepresentationRules.RelationshipMismatch(source, copy));
            var map = CopyRepresentationRules.Map(requested, returned, null, out var failures);
            Assert.False(map.ContainsKey(source.Id));
            Assert.Equal(copiedParticipant.Id, map[sourceParticipant.Id]);
            JObject failure = failures[source.Id];
            Assert.Equal(kind, failure.Value<string>("relationship_kind"));
            Assert.Equal(copiedParticipant.Id, failure.Value<long>("expected_copy_participant_id"));
            Assert.Equal(new[] { observedParticipant.Id }, failure["observed_participant_ids"].Values<long>());
        }

        [Fact]
        public void A_requested_join_selects_its_copy_among_many_unrelated_ambiguities()
        {
            var (source, copy) = EqualJoinedHosts();
            CopySignature sourceParticipant = source.Solid.Joined.Single();
            CopySignature joinedCopy = copy.Solid.Joined.Single();
            CopySignature otherCopy = CopyRepresentationRules.Translate(joinedCopy, 0, 0, 0);
            otherCopy.Id--;
            var unrelatedSources = Enumerable.Range(200, 70).Select(id =>
            {
                CopySignature unrelated = CopyRepresentationRules.Translate(sourceParticipant, id, 0, 0);
                unrelated.Id = id;
                return unrelated;
            }).ToArray();
            var unrelatedCopies = unrelatedSources.SelectMany(unrelated => new[] { 1000, 2000 }.Select(offset =>
            {
                CopySignature candidate = CopyRepresentationRules.Translate(unrelated, 0, 0, 0);
                candidate.Id += offset;
                return candidate;
            }));
            // This unrelated source can take either of the two geometrically equivalent join candidates.
            var competingSource = CopyRepresentationRules.Translate(sourceParticipant, 0, 0, 0);
            competingSource.Id = 300;
            var requested = new[] { source, sourceParticipant, competingSource }.Concat(unrelatedSources);
            var returned = new[] { copy, joinedCopy, otherCopy }.Concat(unrelatedCopies);
            Assert.Equal("solid_geometry", CopyRepresentationRules.MatchBasis(sourceParticipant, joinedCopy));
            Assert.Equal("solid_geometry", CopyRepresentationRules.MatchBasis(sourceParticipant, otherCopy));

            var map = CopyRepresentationRules.Map(requested, returned, null, out var failures);
            Assert.Empty(failures);
            Assert.Equal(requested.Count(), map.Count);
            Assert.Equal(copy.Id, map[source.Id]);
            Assert.Equal(joinedCopy.Id, map[sourceParticipant.Id]);
            Assert.Equal(otherCopy.Id, map[competingSource.Id]);
            Assert.All(unrelatedSources, unrelated => Assert.Equal(unrelated.Id + 1000, map[unrelated.Id]));
        }

        [Fact]
        public void A_requested_join_with_no_compatible_participant_copy_requires_rollback()
        {
            var (source, copy) = EqualJoinedHosts();
            CopySignature sourceParticipant = source.Solid.Joined.Single();
            CopySignature externalParticipant = copy.Solid.Joined.Single();
            var firstCopy = CopyRepresentationRules.Translate(externalParticipant, 0, 0, 0);
            var secondCopy = CopyRepresentationRules.Translate(externalParticipant, 0, 0, 0);
            firstCopy.Id++;
            secondCopy.Id += 2;

            var map = CopyRepresentationRules.Map(new[] { source, sourceParticipant },
                new[] { copy, firstCopy, secondCopy }, null, out var failures);
            Assert.False(map.ContainsKey(source.Id));
            JObject failure = failures[source.Id];
            Assert.Equal("join", failure.Value<string>("relationship_kind"));
            Assert.Equal("cuts_host", failure.Value<string>("relationship_role"));
            Assert.Equal(new[] { externalParticipant.Id }, failure["observed_participant_ids"].Values<long>());
            Assert.Null(failure["search_limit"]);
            var response = new JObject { ["requested"] = 2 };
            CopyRepresentationRules.RollBackFailedReread(response, null, () => ("RolledBack", true),
                () => new JArray(failure));
            Assert.True(response.Value<bool>("rollback_confirmed"));
            Assert.Equal("rolled_back", response.Value<string>("state"));
        }

        [Fact]
        public void A_requested_join_search_that_hits_the_candidate_limit_requires_rollback()
        {
            var (source, copy) = EqualJoinedHosts();
            CopySignature joinedCopy = copy.Solid.Joined.Single();
            joinedCopy.Id = 10000;
            var returned = Enumerable.Range(100, 4098).Select(id =>
            {
                CopySignature other = CopyRepresentationRules.Translate(joinedCopy, 0, 0, 0);
                other.Id = id;
                return other;
            }).Concat(new[] { copy, joinedCopy });

            var map = CopyRepresentationRules.Map(new[] { source, source.Solid.Joined.Single() },
                returned, null, out var failures);
            Assert.False(map.ContainsKey(source.Id));
            Assert.Equal("4096 candidate visits", failures[source.Id].Value<string>("search_limit"));
            Assert.Equal(3L, failures[source.Id].Value<long>("untried_branches"));
        }

        [Fact]
        public void A_committed_read_that_substituted_a_requested_join_rolls_back_before_reporting_failure()
        {
            var (source, beforeCommit) = EqualJoinedHosts();
            CopySignature sourceParticipant = source.Solid.Joined.Single();
            CopySignature copiedParticipant = beforeCommit.Solid.Joined.Single();
            CopySignature other = CopyRepresentationRules.Translate(copiedParticipant, 1, 0, 0);
            other.Id--;
            var requested = new[] { source, sourceParticipant };
            var precommitMap = CopyRepresentationRules.Map(requested, new[] { beforeCommit, copiedParticipant, other }, null);
            Assert.Equal(copiedParticipant.Id, precommitMap[sourceParticipant.Id]);

            var (_, committed) = EqualJoinedHosts();
            CopySignature substitutedParticipant = CopyRepresentationRules.Translate(other, -1, 0, 0);
            committed.Solid.Joined = new[] { substitutedParticipant };
            var returned = new[] { committed, substitutedParticipant, copiedParticipant };
            Assert.Equal(substitutedParticipant.Id, CopyRepresentationRules.Map(requested, returned, null)[sourceParticipant.Id]);
            var map = CopyRepresentationRules.Map(requested, returned, null, out var failures, precommitMap);
            Assert.False(map.ContainsKey(source.Id));
            Assert.Equal(copiedParticipant.Id, map[sourceParticipant.Id]);
            var response = new JObject { ["requested"] = 1 };
            bool rolledBack = false;
            CopyRepresentationRules.RollBackFailedReread(response, null, () =>
            {
                rolledBack = true;
                return ("RolledBack", true);
            }, () =>
            {
                Assert.True(rolledBack);
                return new JArray(failures[source.Id]);
            });
            Assert.True(response.Value<bool>("rollback_confirmed"));
            Assert.False(response.Value<bool>("host_verified"));
            Assert.Equal("rolled_back", response.Value<string>("state"));
            Assert.Equal(other.Id, (long)response["representation_mismatches"][0]["observed_participant_ids"][0]);
        }

        private static (CopySignature Source, CopySignature Copy) EqualJoinedHosts()
        {
            var (source, copy) = IntrinsicPair("wall_axis", 0, 3.312, 3.312);
            source.Solid.ExternalCutCount = 0;
            source.Solid.Cutters = Array.Empty<CopySignature>();
            source.Solid.Joined = new[] { source.Solid.Joined[0] };
            source.Solid.Joined[0].RelationshipRole = "cuts_host";
            source.Solid.RequestedRelationshipIds = new HashSet<long> { source.Solid.Joined[0].Id };
            copy.Solid.Joined = new[] { CopyRepresentationRules.Translate(source.Solid.Joined[0], 0, 0, 0) };
            copy.Solid.Joined[0].Id += 100;
            copy.GeometryPoints = source.GeometryPoints;
            return (source, copy);
        }

        private static (CopySignature Source, CopySignature Copy) IntrinsicPair(
            string kind, int retainedCuts, double sourceM3, double copyM3)
        {
            CopySignature Relationship(long id, int x) => new CopySignature
            {
                Id = id, Type = "Cutter", Category = "Opening", Position = new[] { (double)x, 0d, 0d },
                BoundsMin = new[] { (double)x, 0d, 0d }, BoundsMax = new[] { x + 1d, 1d, 1d }
            };
            var source = InstanceAt(1, 0, kind);
            var copy = InstanceAt(11, 0, kind);
            source.RequiresHostEvidence = copy.RequiresHostEvidence = true;
            source.BoundsMin = copy.BoundsMin = new[] { 0d, 0d, 2d };
            source.BoundsMax = copy.BoundsMax = new[] { 10d, 1d, 12d };
            source.Layers = new[] { new CopyLayer { Definition = "Concrete", Width = 0.5 } };
            copy.Layers = new[] { new CopyLayer { Definition = "Concrete", Width = 0.5 } };
            source.Solid = MeasuredSolid(sourceM3 / 0.028316846592, cutters: 6);
            copy.Solid = MeasuredSolid(copyM3 / 0.028316846592, cutters: retainedCuts);
            source.Solid.Cutters = Enumerable.Range(0, 6).Select(i => Relationship(20 + i, i)).ToArray();
            copy.Solid.Cutters = Enumerable.Range(0, retainedCuts).Select(i => Relationship(120 + i, i)).ToArray();
            source.Solid.Joined = Enumerable.Range(0, 6).Select(i => Relationship(30 + i, i)).ToArray();
            copy.Solid.Joined = Array.Empty<CopySignature>();
            copy.GeometryPoints = copy.GeometryPoints.Concat(new[] { new[] { 5d, 0d, 12d } }).ToArray();
            source.Intrinsic = new CopyIntrinsicEvidence { Volume = 250, BoundsMin = source.BoundsMin,
                BoundsMax = source.BoundsMax, Samples = source.GeometryPoints };
            copy.Intrinsic = new CopyIntrinsicEvidence { Volume = 250, BoundsMin = copy.BoundsMin,
                BoundsMax = copy.BoundsMax, Samples = source.GeometryPoints, ActualVolume = 250, IntersectionVolume = 250 };
            return (source, copy);
        }

        [Theory]
        [InlineData("wall_axis")]
        [InlineData("floor_sketch")]
        public void A_join_box_cannot_explain_a_missing_edge_notch(string kind)
        {
            var source = InstanceAt(1, 0, kind);
            var copy = InstanceAt(11, 0, kind);
            // An edge notch has no interior face loop and may have no external cutter ID.
            // Every changed sample lies within this neighbouring element's box.
            var join = new[] { new[] { -1d, -1d, 0d }, new[] { 2d, 2d, 12d } };
            source.Solid = MeasuredSolid(96, join: join);
            copy.Solid = MeasuredSolid(100);
            source.GeometryPoints = source.GeometryPoints.Concat(new[] { new[] { 1d, 1d, 3d } }).ToArray();
            Assert.Equal(0, CopyRepresentationRules.LayoutDelta(source.Layout, copy.Layout));
            Assert.Null(CopyRepresentationRules.LayoutSolidCheck(source, copy));
            Assert.Empty(CopyRepresentationRules.Map(new[] { source }, new[] { copy }, null));
        }



















        // Common cross-section sample grid for the convex extrusions in these tests.
        // Different denominators keep the sample centres off the tested diagonal.
        private static HashSet<(int, int)> Region(double[][] polygon)
        {
            var region = new HashSet<(int, int)>();
            int x0 = (int)Math.Floor(polygon.Min(p => p[0]) * 8), x1 = (int)Math.Ceiling(polygon.Max(p => p[0]) * 8);
            int y0 = (int)Math.Floor(polygon.Min(p => p[1]) * 12), y1 = (int)Math.Ceiling(polygon.Max(p => p[1]) * 12);
            for (int i = x0; i < x1; i++)
            for (int j = y0; j < y1; j++)
            {
                double x = (i + 0.5) / 8, y = (j + 0.5) / 12;
                var sides = polygon.Select((p, k) =>
                {
                    double[] q = polygon[(k + 1) % polygon.Length];
                    return (q[0] - p[0]) * (y - p[1]) - (q[1] - p[1]) * (x - p[0]);
                }).ToArray();
                if (sides.All(s => s >= 0) || sides.All(s => s <= 0)) region.Add((i, j));
            }
            return region;
        }

        [Theory]
        [InlineData(0, 0.01, 0.20, 0, 0.19, 0.20, 0.18)]
        [InlineData(0, 0.08, 0.40, 0.04, 0.32, 0.48, 0.24)]
        public void A_solid_sample_mismatch_reports_the_smallest_exclusive_pairing_distance(
            double a, double b, double c, double d, double e, double f, double expectedMm)
        {
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            source.GeometryPoints = new[] { Sample(a), Sample(b), Sample(c) };
            copy.GeometryPoints = new[] { Sample(f), Sample(e), Sample(d) };
            Assert.Equal("solid_samples_differ", CopyRepresentationRules.SolidMismatch(source, copy));
            var row = new JObject();
            CopyRepresentationRules.StampSampleDelta(row, source, copy, true);
            Assert.Equal("exact", (string)row["sample_delta_status"]);
            Assert.Equal(expectedMm, (double)row["sample_delta_mm"], 12);
            Assert.True((double)row["sample_delta_mm"] > CopyRepresentationRules.LayoutToleranceFeet * 304.8);

            copy.GeometryPoints = copy.GeometryPoints.Take(2).ToArray();
            row = new JObject();
            CopyRepresentationRules.StampSampleDelta(row, source, copy, true);
            Assert.True((bool)row["sample_count_differs"]);
            Assert.Equal("count_differs", (string)row["sample_delta_status"]);
            Assert.Equal(JTokenType.Null, row["sample_delta_mm"].Type);
        }

        [Theory]
        [InlineData(1500)]
        [InlineData(20000)]
        public void Dense_sample_diagnosis_bounds_the_exclusive_distance_with_linear_memory(int count)
        {
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            var unchanged = Enumerable.Range(10, count - 2).Select(i => new[] { (double)i, 0d, 3d }).ToArray();
            source.GeometryPoints = unchanged.Concat(Edge(0, 0, 1, 1)).ToArray();
            copy.GeometryPoints = Edge(0, 1, 1, 0).Concat(Enumerable.Reverse(unchanged)).ToArray();
            Assert.Equal("solid_samples_differ", CopyRepresentationRules.SolidMismatch(source, copy));

            var row = new JObject();
            long before = GC.GetAllocatedBytesForCurrentThread();
            CopyRepresentationRules.StampSampleDelta(row, source, copy, true);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.InRange(allocated, 0, 512_000 + 320L * count);
            Assert.Equal("bounded", (string)row["sample_delta_status"]);
            Assert.Equal(JTokenType.Null, row["sample_delta_mm"].Type);
            Assert.Equal(0d, (double)row["sample_delta_lower_bound_mm"]);
            Assert.Equal(304.8, (double)row["sample_delta_upper_bound_mm"]);
        }

        [Theory]
        [InlineData(1, 10000)]
        [InlineData(2, 19999)]
        public void Dense_planar_samples_are_verified_without_scanning_a_shared_coordinate(int axis, int displaced)
        {
            const int count = 20000;
            const double tolerance = CopyRepresentationRules.LayoutToleranceFeet;
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            source.GeometryPoints = Enumerable.Range(0, count).Select(i =>
            {
                var point = new double[3];
                point[axis] = i * 4 * tolerance;
                return point;
            }).ToArray();
            copy.GeometryPoints = source.GeometryPoints.Select(p => (double[])p.Clone()).Reverse().ToArray();
            // Move one X within tolerance so sorting by X changes correspondence.
            copy.GeometryPoints[count / 2][0] = tolerance / 2;
            Assert.Null(CopyRepresentationRules.SolidMismatch(source, copy, out long matchingVisits));
            Assert.InRange(matchingVisits, count, 48L * count);
            copy.GeometryPoints[count - 1 - displaced][3 - axis] = 2 * tolerance;
            Assert.Equal("solid_samples_differ", CopyRepresentationRules.SolidMismatch(source, copy, out long mismatchVisits));
            Assert.InRange(mismatchVisits, count, 48L * count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Three_dense_coordinate_windows_do_not_rescan_paired_samples(bool incompatible)
        {
            const int count = 20000;
            const double tolerance = CopyRepresentationRules.LayoutToleranceFeet;
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            source.GeometryPoints = Enumerable.Range(0, count - 1)
                .Select(i => new[] { tolerance * i / (8 * count), tolerance * (i % 8) / 32, tolerance * (i % 13) / 52 })
                .Append(new[] { -tolerance / 4, 4 * tolerance, 0d }).ToArray();
            copy.GeometryPoints = source.GeometryPoints.Select(p => (double[])p.Clone()).Reverse().ToArray();
            // The isolated pair is compatible but crosses the cluster in X, so
            // neither input order nor lexicographic order provides a pairing.
            copy.GeometryPoints[0][0] = tolerance / 4;
            if (incompatible)
                copy.GeometryPoints[1][1] = -4 * tolerance;

            Assert.Equal(incompatible ? "solid_samples_differ" : null,
                CopyRepresentationRules.SolidMismatch(source, copy, out long visits));
            Assert.InRange(visits, count, (incompatible ? 256L : 96L) * count);
        }

        [Fact]
        public void Alternating_outside_axes_do_not_force_full_tree_scans_for_unmatched_samples()
        {
            const int count = 20000;
            const double tolerance = CopyRepresentationRules.LayoutToleranceFeet;
            double step = tolerance / (2 * count);
            double epsilon = step / 4;
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            copy.GeometryPoints = Enumerable.Range(0, count).Select(i =>
            {
                double offset = i % 2 == 0 ? epsilon : -epsilon;
                return new[] { i * step, tolerance + offset, tolerance - offset };
            }).ToArray();
            source.GeometryPoints = Enumerable.Range(0, count / 2)
                .Select(i => new[] { -tolerance / 4 + i * step, 0d, 0d })
                .Concat(copy.GeometryPoints.Skip(count / 2)).ToArray();

            Assert.Equal("solid_samples_differ", CopyRepresentationRules.SolidMismatch(source, copy, out long visits));
            Assert.InRange(visits, 1, 96L * count);
        }

        [Fact]
        public void Dense_stripes_straddling_two_limits_do_not_repeat_expensive_empty_searches()
        {
            const int count = 16384;
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            copy.GeometryPoints = Enumerable.Range(0, count).Select(CornerStripe).ToArray();
            Assert.Equal(count, copy.GeometryPoints.DistinctBy(p =>
                (Math.Round(p[0], 6), Math.Round(p[1], 6), Math.Round(p[2], 6))).Count());
            // Consume a compatible destination before the 8192 missed queries.
            // The first miss needs proof against all destinations, including that one.
            double[] first = { -0.00008, copy.GeometryPoints[0][1], copy.GeometryPoints[0][2] };
            source.GeometryPoints = new[] { first }.Concat(Enumerable.Range(0, count / 2)
                .Select(i => new[] { -0.00004 + i * (0.00002 / count), 0d, 0d }))
                .Concat(copy.GeometryPoints.OrderBy(p => p[0]).Skip(count / 2 + 1)).ToArray();

            Assert.Equal("solid_samples_differ", CopyRepresentationRules.SolidMismatch(source, copy, out long visits));
            output.WriteLine($"Search visits for {count} destination samples: {visits}");
            Assert.InRange(visits, 1, 192);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Dense_corner_matching_respects_the_boundary_when_reassigning_a_sample(bool beyondTolerance)
        {
            const int count = 16384;
            const double tolerance = CopyRepresentationRules.LayoutToleranceFeet;
            double[][] other = Enumerable.Range(0, count - 2).Select(CornerStripe).ToArray();
            double[] flexible = { -0.00004, 0d, 0d };
            double[] constrained = { -0.00004, -tolerance / 2, 0d };
            double[] candidateA = { 0.00024, tolerance / 2, tolerance };
            double[] candidateB = { 0.00026, tolerance, beyondTolerance ? Math.BitIncrement(tolerance) : tolerance };
            double[][] source = new[] { flexible, constrained }.Concat(other).ToArray();
            double[][] destination = other.Concat(new[] { candidateA, candidateB }).ToArray();

            Assert.Equal(!beyondTolerance, CopySampleMatching.HasCompleteMatching(source, destination, tolerance, out _));
        }

        private static double[] CornerStripe(int index)
        {
            const double tolerance = CopyRepresentationRules.LayoutToleranceFeet;
            // X stripes contain the same Y/Z bands. At each Y or Z split,
            // half the remaining points are farther inside that limit and
            // outside the other. The near bands still straddle both limits.
            int stripe = 0, band = -1, insideAxis = 1, rank = 0;
            for (int depth = 0; depth < 14; depth++)
            {
                int bit = (index >> (13 - depth)) & 1;
                int axis = depth % 3;
                if (axis == 0) stripe = 2 * stripe + bit;
                else if (band >= 0) rank = 2 * rank + bit;
                else if (bit == 0) (band, insideAxis) = (depth, axis);
            }
            double gap = tolerance * (band < 0 ? 0.001 : 0.7 * Math.Pow(0.74, band));
            double jitterY = (rank / 16 % 4) * 0.00000105;
            double jitterZ = (rank / 64) * 0.00000105;
            return new[] { (stripe - 16) * 0.000017 + (rank % 16) * 0.00000105,
                tolerance + (insideAxis == 1 ? -gap : gap) + jitterY,
                tolerance + (insideAxis == 2 ? -gap : gap) + jitterZ };
        }

        [Fact]
        public void Dense_alternating_matches_share_the_search_layers_across_reassignments()
        {
            const int count = 20000;
            const double tolerance = CopyRepresentationRules.LayoutToleranceFeet;
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            double[][] Cluster(double x, double y) => Enumerable.Range(0, count / 2)
                .Select(i => new[] { tolerance * (x + i / (16d * count)), tolerance * y, 0d }).ToArray();
            // The first half of the source can use either destination cluster.
            // The second half needs the first cluster and must reassign each pair.
            source.GeometryPoints = Cluster(-0.1, 0).Concat(Cluster(0.1, -0.8)).ToArray();
            copy.GeometryPoints = Cluster(-0.1, -0.5).Concat(Cluster(0.1, 0.8)).ToArray();

            Assert.Null(CopyRepresentationRules.SolidMismatch(source, copy, out long visits));
            Assert.InRange(visits, count, 96L * count);
        }

        [Fact]
        public void Spatial_matching_agrees_with_exhaustive_pairing_on_small_clouds()
        {
            var random = new Random(671);
            for (int example = 0; example < 1200; example++)
            {
                int count = random.Next(2, 8);
                double[][] Cloud() => Enumerable.Range(0, count)
                    .Select(_ => new[] { (double)random.Next(4), random.Next(4), random.Next(4) }).ToArray();
                double[][] source = Cloud(), destination = Cloud();
                const double tolerance = 1;
                bool Exhaustive(int sourceIndex, int taken)
                {
                    if (sourceIndex == count) return true;
                    for (int i = 0; i < count; i++)
                        if ((taken & (1 << i)) == 0 &&
                            CopyRepresentationRules.Delta(source[sourceIndex], destination[i]) <= tolerance &&
                            Exhaustive(sourceIndex + 1, taken | (1 << i))) return true;
                    return false;
                }

                Assert.Equal(Exhaustive(0, 0),
                    CopySampleMatching.HasCompleteMatching(source, destination, tolerance, out _));
            }
        }

        [Fact]
        public void A_displaced_copy_reports_placement_without_measuring_dense_samples()
        {
            var source = InstanceAt(1, 0, "wall_axis");
            source.GeometryPoints = Enumerable.Range(0, 20000).Select(i => new[] { (double)i, 0d, 3d }).ToArray();
            var copy = CopyRepresentationRules.Translate(source, 0, 1, 0);
            double layoutDelta = CopyRepresentationRules.LayoutDelta(source.Layout, copy.Layout);
            Assert.Equal(1d, layoutDelta);
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
            var row = new JObject();
            CopyRepresentationRules.StampSampleDelta(row, source, copy, CopyRepresentationRules.PlacementMatches(source, copy));
            Assert.Equal("skipped_placement_mismatch", (string)row["sample_delta_status"]);
            Assert.Equal(JTokenType.Null, row["sample_delta_mm"].Type);
            Assert.Null(row["sample_delta_lower_bound_mm"]);
        }

        [Fact]
        public void Sample_verification_can_reassign_a_candidate_discovered_earlier()
        {
            double[] Point(double x, double y) => new[] { x / 304.8, y / 304.8, 0d };
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            source.GeometryPoints = new[] { Point(0, 0), Point(0.05, -0.08) };
            copy.GeometryPoints = new[] { Point(0.01, 0), Point(0.02, 0.09) };
            Assert.Null(CopyRepresentationRules.SolidMismatch(source, copy, out long visits));
            Assert.True(visits > 6); // Two fast matches in this tree would take at most six visits.
        }

        [Theory]
        [InlineData("wall_axis")]
        [InlineData("floor_sketch")]
        public void Both_layouts_unavailable_still_requires_matching_solid_evidence(string kind)
        {
            var source = InstanceAt(1, 0, kind);
            var copy = InstanceAt(11, 0, kind);
            source.Layout = copy.Layout = null;
            Assert.Equal("solid_geometry", CopyRepresentationRules.MatchBasis(source, copy));
            copy.Solid = MeasuredSolid(99);
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
            copy.Solid = MeasuredSolid(100, cutters: 1);
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
            copy.Solid = source.Solid = null;
            copy.SolidReadError = source.SolidReadError = "solid unavailable";
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Solid_samples_and_opening_samples_find_a_complete_pairing_in_either_order(bool reverse)
        {
            var source = InstanceAt(1, 0, "wall_axis");
            var copy = InstanceAt(11, 0, "wall_axis");
            var expected = new[] { Sample(0), Sample(0.09), new[] { 2d, 1d, 3d } };
            var candidate = new[] { Sample(0.04), Sample(-0.04), expected[2] };
            if (reverse) Array.Reverse(candidate);
            source.GeometryPoints = expected;
            copy.GeometryPoints = candidate;
            Assert.Equal("volume_and_samples", CopyRepresentationRules.LayoutSolidCheck(source, copy));

            source.Solid = MeasuredSolid(100, loop: expected);
            copy.Solid = MeasuredSolid(100, loop: candidate);
            Assert.Equal("wall_axis", CopyRepresentationRules.MatchBasis(source, copy));
            copy.GeometryPoints = expected;
            copy.Solid.OpeningLoops[0] = new[] { Sample(-0.04), Sample(-0.09), expected[2] };
            Assert.Null(CopyRepresentationRules.MatchBasis(source, copy));
        }

        [Fact]
        public void Sketch_curves_use_the_smallest_deviation_with_a_complete_pairing()
        {
            var source = InstanceAt(1, 0, "floor_sketch");
            var copy = InstanceAt(11, 0, "floor_sketch");
            source.Layout.Curves = new[] { Edge(0, 0, 10, 0), OffsetEdge(0.09) };
            copy.Layout.Curves = new[] { OffsetEdge(0.04), OffsetEdge(-0.045) };
            Assert.Equal(0.05 / 304.8, CopyRepresentationRules.LayoutDelta(source.Layout, copy.Layout), 12);
            Assert.Equal("floor_sketch", CopyRepresentationRules.MatchBasis(source, copy));
        }

        private static double[] Sample(double mm) => new[] { mm / 304.8, 0d, 3d };
        private static double[][] OffsetEdge(double mm) => Edge(0, mm / 304.8, 10, mm / 304.8);

        [Theory]
        [InlineData(true, "RolledBack", "rolled_back", ErrorCodes.TransaccionRevertida, "The copy batch was rolled back.")]
        [InlineData(false, "Error", "uncertain", ErrorCodes.VerificacionFallida, "Rollback was not confirmed; inspect the model.")]
        public void Failed_reread_reports_only_the_rollback_that_was_confirmed(
            bool confirmed, string status, string state, string code, string suffix)
        {
            var response = new JObject { ["host_verified"] = true, ["requested"] = 2 };
            string message = CopyRepresentationRules.StampFailedReread(response, null, status, confirmed);
            Assert.Equal("Copy geometry verification was incomplete. " + suffix, message);
            Assert.Equal(state, (string)response["state"]);
            Assert.Equal(code, (string)response["error_code"]);
            Assert.Equal(status, (string)response["transaction_status"]);
            Assert.Equal(confirmed, (bool)response["rollback_confirmed"]);
            Assert.False((bool)response["host_verified"]);
            Assert.Equal(2, (int)response["application"]["requested"]);
        }

        private static CopySignature InstanceAt(long id, double yMm, string kind)
        {
            var signature = S(id);
            double y = yMm / 304.8;
            signature.Layout = new CopyLayout { Kind = kind, Base = 2, Top = 12, Thickness = 0.5,
                Curves = new[] { Edge(0, y, 10, y) } };
            signature.Solid = MeasuredSolid(100);
            signature.GeometryPoints = new[] { new[] { 0d, y, 2d }, new[] { 10d, y, 12d } };
            return signature;
        }

        private static CopySolidEvidence MeasuredSolid(double volume, double[][] join = null, int cutters = 0, double[][] loop = null) =>
            new CopySolidEvidence { Volume = volume, JoinBounds = join == null ? Array.Empty<double[][]>() : new[] { join }, ExternalCutCount = cutters,
                OpeningLoops = loop == null ? Array.Empty<double[][]>() : new[] { loop } };

        private static double[][] Edge(double x1, double y1, double x2, double y2) =>
            new[] { new[] { x1, y1, 0d }, new[] { x2, y2, 0d } };
    }
}
