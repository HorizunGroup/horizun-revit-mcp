using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests;

public sealed class GroupLevelRulesTests
{
    private static JObject Copy() => new JObject { ["operation"] = "copy_to_levels", ["target_document"] = "Model",
        ["group_ids"] = new JArray(10), ["level_ids"] = new JArray(20, 30) };

    private static GroupMemberBounds Member(long id, double bottom, double top, string type = "Wall:10") =>
        new GroupMemberBounds { Id = id, Key = type, Min = new[] { 0.0, 0.0, bottom }, Max = new[] { 1.0, 1.0, top } };

    [Fact]
    public void CopyAndVariationReadEveryMembersTopAndBottom()
    {
        var source = new[] { Member(1, 0, 1), Member(2, 1, 2) };
        var actual = new[] { Member(11, 10, 11), Member(12, 11, 12) };
        JObject report = GroupLevelRules.CompareMemberBounds(source, actual, new[] { 0.0, 0, 10 }, 1);
        Assert.True(report.Value<bool>("matches"));
        Assert.Equal(2, ((JArray)report["members"]).Count);
        Assert.Equal(3048.0, report["members"][0].Value<double>("bottom_mm"), 6);
        actual[1].Max[2] = 12.1;
        report = GroupLevelRules.CompareMemberBounds(source, actual, new[] { 0.0, 0, 10 }, 1);
        Assert.False(report.Value<bool>("matches"));
        Assert.True(report["members"][0].Value<bool>("height_matches"));
        Assert.False(report["members"][1].Value<bool>("height_matches"));
    }

    [Fact]
    public void MissingMembersTypesAndPlanMovementAreMeasuredVariation()
    {
        var source = new[] { Member(1, 0, 1), Member(2, 1, 2) };
        var actual = new[] { Member(11, 0, 1), Member(12, 1, 2, "Wall:99") };
        Assert.False(GroupLevelRules.CompareMemberBounds(source, actual, new[] { 0.0, 0, 0 }, 1).Value<bool>("matches"));
        actual[1].Key = "Wall:10"; actual[1].Min[0] = 0.1;
        Assert.False(GroupLevelRules.CompareMemberBounds(source, actual, new[] { 0.0, 0, 0 }, 1).Value<bool>("matches"));
        JObject missing = GroupLevelRules.CompareMemberBounds(source, new[] { actual[0] }, new[] { 0.0, 0, 0 }, 1);
        Assert.True(missing.Value<bool>("coverage_complete"));
        Assert.False(missing.Value<bool>("matches"));
    }

    [Fact]
    public void UnreadableMemberIsUnknownRatherThanMatchingOrVariation()
    {
        var source = new[] { Member(1, 0, 1) };
        var actual = new[] { Member(11, 10, 11) }; actual[0].Min = null;
        JObject report = GroupLevelRules.CompareMemberBounds(source, actual, new[] { 0.0, 0, 10 }, 1);
        Assert.False(report.Value<bool>("coverage_complete"));
        Assert.Equal(JTokenType.Null, report["matches"].Type);
        Assert.Equal(JTokenType.Null, report["members"][0]["height_matches"].Type);
        actual[0].Min = new[] { 0.0, double.NaN, 10 };
        Assert.False(GroupLevelRules.CompareMemberBounds(source, actual, new[] { 0.0, 0, 10 }, 1).Value<bool>("coverage_complete"));
    }

    [Fact]
    public void CopyRequiresOneSourceAndDistinctTargetLevels()
    {
        Assert.Null(GroupLevelRules.ValidateCopyOrVariation(Copy()));
        JObject r = Copy(); r.Remove("target_document");
        Assert.Contains("target_document", GroupLevelRules.ValidateCopyOrVariation(r));
        r = Copy(); r["group_ids"] = new JArray(10, 11);
        Assert.Contains("group_ids", GroupLevelRules.ValidateCopyOrVariation(r));
        r = Copy(); r["level_ids"] = new JArray(20, 20);
        Assert.Contains("distinct", GroupLevelRules.ValidateCopyOrVariation(r));
        r = Copy(); r["level_ids"] = new JArray();
        Assert.Contains("level_ids", GroupLevelRules.ValidateCopyOrVariation(r));
        r = Copy(); r["level_ids"] = new JArray(-2);
        Assert.Contains("positive", GroupLevelRules.ValidateCopyOrVariation(r));
        r = Copy(); r["level_id"] = 20;
        Assert.Contains("not applicable", GroupLevelRules.ValidateCopyOrVariation(r));
    }

    [Fact]
    public void VariationIsReadOnlyAndNeedsTwoDistinctInstances()
    {
        var r = new JObject { ["operation"] = "check_variation", ["group_ids"] = new JArray(1, 2) };
        Assert.Null(GroupLevelRules.ValidateCopyOrVariation(r));
        r["group_ids"] = new JArray(1, 1);
        Assert.Contains("distinct", GroupLevelRules.ValidateCopyOrVariation(r));
        r["group_ids"] = new JArray(1);
        Assert.Contains("2..50", GroupLevelRules.ValidateCopyOrVariation(r));
        r["group_ids"] = new JArray(1, 2); r["tolerance_mm"] = double.PositiveInfinity;
        Assert.Contains("finite", GroupLevelRules.ValidateCopyOrVariation(r));
        r["tolerance_mm"] = 0;
        Assert.Contains("positive", GroupLevelRules.ValidateCopyOrVariation(r));
        r.Remove("tolerance_mm");
        r["relative_rotations_degrees"] = new JArray(90);
        Assert.Null(GroupLevelRules.ValidateCopyOrVariation(r));
        r["group_ids"] = new JArray(1, 2, 3);
        Assert.Contains("relative_rotations_degrees", GroupLevelRules.ValidateCopyOrVariation(r));
        r["relative_rotations_degrees"] = new JArray(90, double.NaN);
        Assert.Contains("relative_rotations_degrees", GroupLevelRules.ValidateCopyOrVariation(r));
        r["relative_rotations_degrees"] = new JArray(90, 361);
        Assert.Contains("relative_rotations_degrees", GroupLevelRules.ValidateCopyOrVariation(r));
    }

    [Theory]
    [InlineData("type", true)]
    [InlineData("all_instances", true)]
    [InlineData("this_instance", false)]
    public void Group_addition_targets_every_instance_that_will_be_regenerated(string input, bool all)
    {
        string scope = GroupRedefinitionRules.ResolveScope(input, 1, out string error, "add_members");
        Assert.Null(error);
        Assert.Equal(all, GroupRedefinitionRules.IsTypeAddition("add_members", scope));
        Assert.Equal(all ? new[] { 100, 101 } : new[] { 100 },
            GroupRedefinitionRules.AffectedAddInstances(scope, 100, new[] { 101 }));
        Assert.Null(GroupRedefinitionRules.ResolveScope("type", 1, out _, "remove_members"));
    }

    [Fact]
    public void Adding_to_the_type_requires_a_new_member_at_the_translated_reference_bounds()
    {
        var source = new[] { Member(1, 0, 1), Member(2, 1, 2, "Floor:9") };
        var copy = new[] { Member(11, 10, 11), Member(12, 11, 12, "Floor:9") };
        var translation = new[] { 0d, 0d, 10d };
        JArray Added(long priorMember)
        {
            var retained = new GroupMemberBounds { Id = priorMember, Key = copy[0].Key };
            return GroupLevelRules.AddedMembersInCopy(source, copy, new[] { 2L }, Array.Empty<long>(),
                Placement(new double[3], source[0]), Placement(translation, retained), new[] { source[1] }, 0, out _);
        }
        Assert.Equal(12, Added(11)[0].Value<long>());
        Assert.Null(Added(12));
        copy[1].Min[0] += 0.2; copy[1].Max[0] += 0.2;
        Assert.Null(Added(11));
        copy[1].Min[0] -= 0.2; copy[1].Max[0] -= 0.2;
        copy[1].Key = "Wall:11";
        Assert.Null(Added(11));
    }

    [Fact]
    public void Type_addition_checks_the_translated_geometry_of_automatically_included_members()
    {
        var reference = new[] { Member(1, 0, 1), Member(2, 1, 2, "Pipe:9"), Member(3, 1.5, 1.5, "CenterLine:10") };
        var copy = new[] { Member(11, 10, 11), Member(12, 11, 12, "Pipe:9"), Member(13, 11.5, 11.5, "CenterLine:10") };
        JArray Added() => GroupLevelRules.AddedMembersInCopy(reference, copy, new[] { 2L }, new[] { 3L },
            Placement(new double[3], reference[0]), Placement(new[] { 0d, 0d, 10d }, copy[0]), reference.Skip(1).ToList(), 0, out _);
        Assert.Equal(new[] { 12L, 13L }, Added().Values<long>());
        copy[2].Min[0] += 0.2; copy[2].Max[0] += 0.2;
        Assert.Null(Added());
        copy[2].Min = null;
        Assert.Null(Added());
    }

    [Fact]
    public void Type_addition_refuses_contradictory_family_reflection_even_when_the_bounds_match()
    {
        var source = new[] { Member(1, 0, 1), Member(2, 1, 2), Member(3, 2, 3) };
        var copy = new[] { Member(11, 0, 1), Member(12, 1, 2), Member(13, 2, 3) };
        source[1].Axes = copy[1].Axes = new[] { new[] { 1d, 0d, 0d }, new[] { 0d, 1d, 0d } };
        source[1].Reflected = false;
        copy[1].Reflected = true;

        JArray added = GroupLevelRules.AddedMembersInCopy(source, copy, new[] { 3L }, Array.Empty<long>(),
            Placement(new double[3], source[0], source[1]), Placement(new double[3], copy[0], copy[1]), new[] { source[2] }, 0, out JObject report);

        Assert.Null(added);
        Assert.False(report.Value<bool>("matches"));
        Assert.Contains("different reflection states", report.Value<string>("reason"));
    }

    [Fact]
    public void Oblique_rotation_uses_geometry_instead_of_turning_the_world_bounding_box()
    {
        double diagonal = Math.Sqrt(0.5);
        double[] referenceOrigin = { 0, 0, 0 }, copyOrigin = { 10, 0, 4 };
        GroupMemberBounds Subset(long id, double[][] points, double heading) => new GroupMemberBounds
        {
            Id = id, Key = "Wall:10", ReadGeometryPoints = () => points,
            Axes = new[] { new[] { Math.Cos(heading), Math.Sin(heading), 0d } },
            Min = Enumerable.Range(0, 3).Select(i => points.Min(p => p[i])).ToArray(),
            Max = Enumerable.Range(0, 3).Select(i => points.Max(p => p[i])).ToArray()
        };
        double[] Rotate(double[] p) => new[] { 10 + (p[0] - p[1]) * diagonal, (p[0] + p[1]) * diagonal, p[2] + 4 };
        double[][] retained = { new[] { 0d, 0, 0 }, new[] { 6d, 0, 4 } };
        double[][] added = { new[] { 1d, 1, 1 }, new[] { 3d, 3, 3 } };
        var source = new[] { Subset(1, retained, 0), Subset(2, added, Math.PI / 4) };
        var copy = new[] { Subset(11, retained.Select(Rotate).ToArray(), Math.PI / 4),
            Subset(12, added.Select(Rotate).ToArray(), Math.PI / 2) };

        JArray VerifyAdded() => GroupLevelRules.AddedMembersInCopy(source, copy, new[] { 2L }, Array.Empty<long>(),
            Placement(referenceOrigin, source[0]), Placement(copyOrigin, copy[0]), new[] { source[1] }, 45, out _);
        JObject Compare() => GroupLevelRules.ComparePlacedMembers(source, copy, referenceOrigin, copyOrigin, 1, 45);

        Assert.Equal(new[] { 12L }, VerifyAdded().Values<long>());
        Assert.True(Compare().Value<bool>("matches"));
        Assert.Equal(45, Compare().Value<double>("rotation_degrees"), 8);

        copy[1] = Subset(12, added.Select(Rotate).Select(p => new[] { p[0] + 0.5, p[1], p[2] }).ToArray(), Math.PI / 2);
        Assert.Null(VerifyAdded());
        Assert.False(Compare().Value<bool>("matches"));

        copy[1].ReadGeometryPoints = null;
        Assert.Null(VerifyAdded());
        JObject unreadable = Compare();
        Assert.False(unreadable.Value<bool>("coverage_complete"));
        Assert.Equal(JTokenType.Null, unreadable["matches"].Type);
        Assert.True(unreadable["members"][1].Value<bool>("height_matches"));
        copy[1].Max[2] += 100 / 304.8;
        JObject displaced = Compare();
        Assert.False(displaced.Value<bool>("matches"));
        Assert.False(displaced["members"][1].Value<bool>("height_matches"));
        Assert.Equal(JTokenType.Null, displaced["members"][1]["geometry_matches"].Type);
    }

    private static GroupPlacementSnapshot Placement(double[] origin, params GroupMemberBounds[] members) =>
        new GroupPlacementSnapshot { Origin = origin, Members = members };

    [Fact]
    public void Type_addition_can_verify_a_declared_rotation_when_curve_endpoints_are_reversed()
    {
        var source = new[] { Member(1, 0, 1), Member(2, 1, 2), Member(3, 2, 3) };
        var copy = new[] { Member(11, 0, 1), Member(12, 1, 2), Member(13, 2, 3) };
        source[0].Axes = new[] { new[] { 1d, 0d, 0d } };
        copy[0].Axes = new[] { new[] { -1d, 0d, 0d } };

        JArray added = GroupLevelRules.AddedMembersInCopy(source, copy, new[] { 3L }, Array.Empty<long>(),
            Placement(new double[3], source[0], source[1]), Placement(new double[3], copy[0], copy[1]), new[] { source[2] }, 0, out JObject report);

        Assert.True(report.Value<bool>("coverage_complete"));
        Assert.Equal(new[] { 13L }, added.Values<long>());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Rebuild_checks_the_commit_additions_and_reports_the_commit_independently(bool newTypeObserved, bool referencesHeld)
    {
        var audit = new GroupRebuildChecks();
        bool committed = false;
        audit.Run(() => new PostconditionCheck("references").Compare("references", true, true),
            () => committed = true, () =>
            {
                Assert.True(committed);
                long[] observed = newTypeObserved ? new[] { 100L, 200, 301 } : new[] { 100L, 301 };
                JArray additions = GroupLevelRules.AlsoCreated(100, 200, observed, new[] { 301L }, id => "uid-" + id);
                var check = new PostconditionCheck("references", "also_created", "deleted_elements")
                    .Compare("references", true, referencesHeld);
                GroupRebuildChecks.RecordChanges(check, null, additions, new[] { 89L, 90 }, new[] { 89L, 90 }, null, id => false);
                return check;
            });
        Assert.True(audit.Committed);
        Assert.Equal(newTypeObserved && referencesHeld, audit.Verified);
        Assert.Equal(3, audit.Check.Count);
    }

    [Fact]
    public void A_group_cascade_must_be_owned_or_explicitly_regenerated_and_match_the_approved_ids()
    {
        var planned = new GroupRebuildChecks.Deletion { Id = 89, UniqueId = "group-old", InRegenerationScope = true };
        var indirect = new GroupRebuildChecks.Deletion { Id = 90, UniqueId = "collateral" };
        Assert.True(planned.Authorized(true));
        Assert.False(indirect.Authorized(true));
        indirect.Owned = true;
        Assert.False(indirect.Authorized(false));
        planned.Owned = true;
        Assert.True(planned.Authorized(false));
        planned.UniqueId = null;
        Assert.False(planned.Authorized(true));

        long[] required = { 89 };
        long[] approved = { 89, 90 };
        Assert.True(GroupRebuildChecks.DeletionsMatch(new[] { 90L, 89 }, required, approved, id => false));
        Assert.False(GroupRebuildChecks.DeletionsMatch(new[] { 89L, 90, 91 }, required, approved, id => false));
        Assert.False(GroupRebuildChecks.DeletionsMatch(new[] { 89L }, required, approved, id => false));
        Assert.False(GroupRebuildChecks.DeletionsMatch(new[] { 89L, 90 }, required, approved, id => id == 90));
        Assert.False(GroupRebuildChecks.DeletionsMatch(null, required, approved, id => false));
    }

    [Fact]
    public void The_hidden_definition_of_the_replaced_type_goes_with_the_type_but_only_inside_the_scope()
    {
        // Measured: deleting an old group type also deletes Revit's hidden definition of it (a group outside
        // GroupType.Groups, its member copies and its own level copy). None carries a mark of its own.
        var definition = new GroupRebuildChecks.Deletion { Id = 91, UniqueId = "definition-wall", TypeDefinition = true,
            InRegenerationScope = true };
        Assert.True(definition.Authorized(false));
        Assert.Equal("type_definition", definition.PlanRow().BeforeValues["ownership"]);
        Assert.True(definition.Evidence(false).Value<bool>("type_definition"));
        definition.InRegenerationScope = false;
        Assert.False(definition.Authorized(true));
        definition.InRegenerationScope = true; definition.UniqueId = null;
        Assert.False(definition.Authorized(true));
    }

    [Fact]
    public void Group_cascade_identity_and_ownership_are_bound_to_confirmation()
    {
        const string command = "horizun_manage_groups", document = "model", hash = "add_members";
        var store = new ConfirmationStore();
        ResolvedPlan Plan(string identity, bool owned)
        {
            var plan = new ResolvedPlan { Command = command, DocumentKey = document };
            plan.Elements.Add(new GroupRebuildChecks.Deletion
            {
                Id = 90, UniqueId = identity, Owned = owned, InRegenerationScope = true
            }.PlanRow());
            return plan;
        }
        ResolvedPlan original = Plan("old-type", false);
        var token = store.IssueForPlan(command, document, hash, null, original);
        Assert.Same(original, store.ReadRehearsedPlan(token.Token, command, document, hash));
        Assert.Null(store.ReadRehearsedPlan(token.Token, command, document, "other request"));
        Assert.Equal("stale_plan", store.Require(token.Token, command, document, hash, Plan("other-type", false), null)
            .Detail.Value<string>("state"));
        Assert.Equal("stale_plan", store.Require(token.Token, command, document, hash, Plan("old-type", true), null)
            .Detail.Value<string>("state"));
        Assert.Null(store.Require(token.Token, command, document, hash, original, null));
        Assert.Null(store.ReadRehearsedPlan(token.Token, command, document, hash));
    }

    [Fact]
    public void Also_created_reconciles_the_new_type_and_members_but_excludes_existing_copies()
    {
        var additions = new[] { 100L, 200, 301, 302 };
        var expected = new[] { 100L, 301, 302 };
        string Read(long id) => "uid-" + id;
        JArray rows = GroupLevelRules.AlsoCreated(100, 200, additions, expected, Read);
        Assert.Equal(new[] { 200L, 301, 302 }, rows.Select(row => row.Value<long>("element_id")));
        Assert.All(rows, row => Assert.Equal(Read(row.Value<long>("element_id")), row.Value<string>("unique_id")));
        Assert.Null(GroupLevelRules.AlsoCreated(100, 200, additions.Where(id => id != 302), expected, Read));
        Assert.Null(GroupLevelRules.AlsoCreated(100, 200, additions.Where(id => id != 200), expected, Read));
        Assert.Null(GroupLevelRules.AlsoCreated(100, 200, additions, expected, id => id == 301 ? null : Read(id)));
    }

    [Fact]
    public void Retained_slots_disambiguate_three_equally_spaced_members_of_the_same_type()
    {
        var before = new[] { Member(11, 0, 1), Member(12, 2, 3) };
        // The new member is first in the new type's order. Geometry alone also fits
        // the adjacent pair, but those are not the two retained slots.
        var after = new[] { Member(21, 9, 10), Member(22, 5, 6), Member(23, 7, 8) };
        double[] shift = GroupLevelRules.MeasureRetainedTranslation(new[] { 1L, 2L }, new[] { 3L, 1L, 2L },
            before, after, 0.01);
        Assert.Equal(new[] { 0.0, 0.0, 5.0 }, shift);
        after[2].Max[2] += 1;
        Assert.Null(GroupLevelRules.MeasureRetainedTranslation(new[] { 1L, 2L }, new[] { 3L, 1L, 2L }, before, after, 0.01));
    }

    [Fact]
    public void Variation_counts_differing_member_bounds_and_preserves_unknown_coverage()
    {
        var reference = new[] { Member(1, 0, 1), Member(2, 1, 2, "Floor:9") };
        var copy = new[] { Member(11, 10, 11), Member(12, 11, 12, "Floor:9") };
        JObject Reply() => GroupLevelRules.VariationResult(100, 50, new JArray(
            GroupLevelRules.CompareMemberBounds(reference, copy, new[] { 0.0, 0, 10 }, 1)));
        JObject held = Reply();
        Assert.Equal(0, held.Value<int>("variation_count"));
        Assert.False(held.Value<bool>("has_variation"));
        Assert.True(held.Value<bool>("coverage_complete"));
        copy[1].Max[2] += 1;
        Assert.Equal(1, Reply().Value<int>("variation_count"));
        copy[1].Max = null;
        Assert.Equal(JTokenType.Null, Reply()["variation_count"].Type);
        Assert.Equal(JTokenType.Null, Reply()["has_variation"].Type);
    }

    [Fact]
    public void EveryMemberBottomAndTopMustFollowTheTargetLevel()
    {
        Assert.True(GroupLevelRules.MemberHeightHeld(100, 200, 3100, 3200, 3000, 1));
        Assert.True(GroupLevelRules.MemberHeightHeld(100, 200, -2900, -2800, -3000, 1));
        Assert.False(GroupLevelRules.MemberHeightHeld(100, 200, 3100, 3202, 3000, 1));
        Assert.False(GroupLevelRules.MemberHeightHeld(100, 200, 100, 200, 3000, 1));
        Assert.False(GroupLevelRules.MemberHeightHeld(100, 200, null, 3200, 3000, 1));
        Assert.False(GroupLevelRules.MemberHeightHeld(100, 200, double.NaN, 3200, 3000, 1));
        Assert.False(GroupLevelRules.MemberHeightHeld(200, 100, 3200, 3100, 3000, 1));
    }

    [Fact]
    public void KeepPositionOffsetsNewLevelFromExistingWorldElevation()
    {
        Assert.Equal(-2000.0, GroupLevelRules.OffsetMmForWorldZ(1000, 3000));
        Assert.True(GroupLevelRules.PositionHeld(1, 2, 3, 1.4, 2, 3, 0.5));
        Assert.False(GroupLevelRules.PositionHeld(1, 2, 3, 2, 2, 3, 0.5));
    }
}
