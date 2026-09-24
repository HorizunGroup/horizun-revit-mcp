// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit-free decisions behind horizun_manage_groups and horizun_manage_worksets:
// which instances a membership change touches, how a swapped instance's
// displacement is measured, when it counts as having kept its content, and how an
// element asked to change workset is classified.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GroupWorksetRulesTests
    {
        private static MemberSignature S(long cat, long type, double x, double y = 0, double z = 0, double size = 1)
            => new MemberSignature(cat, type, new[] { x, y, z }, new[] { x + size, y + size, z + size });

        [Fact]
        public void A_type_with_other_instances_needs_an_explicit_scope()
        {
            string error;
            Assert.Null(GroupRedefinitionRules.ResolveScope(null, 3, out error));
            Assert.Contains("reads two ways", error);
            Assert.Equal("this_instance", GroupRedefinitionRules.ResolveScope("this_instance", 3, out error));
            Assert.Equal("all_instances", GroupRedefinitionRules.ResolveScope("ALL_INSTANCES", 3, out error));
        }

        [Fact]
        public void A_type_with_a_single_instance_has_one_reading()
        {
            string error;
            Assert.Equal("all_instances", GroupRedefinitionRules.ResolveScope(null, 0, out error));
            Assert.Null(error);
            Assert.Null(GroupRedefinitionRules.ResolveScope("everything", 0, out error));
            Assert.Contains("scope must be", error);
        }

        [Fact]
        public void The_shift_is_measured_from_a_member_unique_before_and_after()
        {
            var before = new List<MemberSignature> { S(1, 10, 0), S(1, 10, 5), S(2, 20, 3) };
            var after = new List<MemberSignature> { S(1, 10, 2), S(1, 10, 7), S(2, 20, 5), S(3, 30, 9) };
            string why;
            double[] shift = GroupRedefinitionRules.MeasureShift(before, after, out why);
            Assert.NotNull(shift);
            Assert.Equal(2, shift[0], 9);
            Assert.Equal(0, shift[1], 9);
        }

        [Fact]
        public void No_unique_member_means_no_shift_rather_than_a_guess()
        {
            var before = new List<MemberSignature> { S(1, 10, 0), S(1, 10, 5) };
            var after = new List<MemberSignature> { S(1, 10, 2), S(1, 10, 7) };
            string why;
            Assert.Null(GroupRedefinitionRules.MeasureShift(before, after, out why));
            Assert.Contains("cannot be measured", why);
        }

        [Fact]
        public void A_member_without_a_box_is_never_measured_from()
        {
            var a = S(1, 10, 0); a.HasBox = false;
            var b = S(1, 10, 4); b.HasBox = false;
            string why;
            Assert.Null(GroupRedefinitionRules.MeasureShift(new[] { a }, new[] { b }, out why));
        }

        [Fact]
        public void An_addition_holds_only_when_every_old_member_is_still_in_place()
        {
            var before = new List<MemberSignature> { S(1, 10, 0), S(2, 20, 3) };
            var good = new List<MemberSignature> { S(1, 10, 0), S(2, 20, 3), S(3, 30, 9) };
            Assert.True(GroupRedefinitionRules.Match(before, good, true, 1, 0.001).Held);
            var moved = new List<MemberSignature> { S(1, 10, 0.5), S(2, 20, 3), S(3, 30, 9) };
            RetainedMatch m = GroupRedefinitionRules.Match(before, moved, true, 1, 0.001);
            Assert.False(m.Held);
            Assert.Equal(1, m.Unmatched);
            Assert.False(GroupRedefinitionRules.Match(before, before, true, 1, 0.001).Held);
        }

        [Fact]
        public void A_removal_holds_only_when_what_remains_was_there_before()
        {
            var before = new List<MemberSignature> { S(1, 10, 0), S(2, 20, 3), S(3, 30, 9) };
            Assert.True(GroupRedefinitionRules.Match(before, new[] { S(1, 10, 0), S(2, 20, 3) }, false, 1, 0.001).Held);
            Assert.False(GroupRedefinitionRules.Match(before, new[] { S(1, 10, 0), S(4, 40, 3) }, false, 1, 0.001).Held);
        }

        [Fact]
        public void An_empty_comparison_is_not_a_hold()
        {
            Assert.False(GroupRedefinitionRules.Match(new MemberSignature[0], new MemberSignature[0], true, 0, 0.001).Held);
        }

        [Fact]
        public void Names_are_refused_by_rule_not_by_the_api()
        {
            Assert.NotNull(WorksetEditRules.NameProblem("", null));
            Assert.NotNull(WorksetEditRules.NameProblem(" Shell", null));
            Assert.NotNull(WorksetEditRules.NameProblem("A{B}", null));
            Assert.NotNull(WorksetEditRules.NameProblem("shell", new[] { "Shell" }));
            Assert.Null(WorksetEditRules.NameProblem("Shell and Core", new[] { "Interiors" }));
        }

        [Fact]
        public void A_borrowed_element_is_reported_never_moved()
        {
            Assert.Equal(WorksetEditRules.Borrowed, WorksetEditRules.Classify(true, true, true, false));
            Assert.Equal(WorksetEditRules.ReadOnlyWorkset, WorksetEditRules.Classify(true, false, false, false));
            Assert.Equal(WorksetEditRules.AlreadyThere, WorksetEditRules.Classify(true, true, true, true));
            Assert.Equal(WorksetEditRules.Missing, WorksetEditRules.Classify(false, false, true, false));
            Assert.Equal(WorksetEditRules.Movable, WorksetEditRules.Classify(true, false, true, false));
        }

        private static string Command(string file)
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void The_commands_hold_their_refusals()
        {
            string worksets = Command("ManageWorksetsCommand.cs");
            Assert.Contains("[\"code\"] = \"not_workshared\"", worksets);
            Assert.Contains("CheckoutStatus.OwnedByOtherUser", worksets);
            string groups = Command("ManageGroupsCommand.cs");
            Assert.Contains("[\"code\"] = \"api_absent\"", groups);
            Assert.Contains("GroupRedefinitionRules.ResolveScope", groups);
            Assert.Contains("GetAvailableAttachedDetailGroupTypeIds", groups);
        }
    }
}
