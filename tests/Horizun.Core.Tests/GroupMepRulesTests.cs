// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Groups of MEP elements: the closure of members NewGroup returns (pipe + fittings +
// center lines, a tee that reduces on its own), the physical count apart from the
// representations, and the typed refusal of a level change on a group that carries
// connected MEP elements.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GroupMepRulesTests
    {
        private static Dictionary<long, string> Kinds(params (long, string)[] k) => k.ToDictionary(x => x.Item1, x => x.Item2);

        [Fact]
        public void Pipes_plus_their_center_lines_hold_and_are_counted_apart()
        {
            // asked for two pipes and a fitting; Revit brought two center lines
            GroupClosure c = GroupMemberClosureRules.Evaluate(new long[] { 10, 11, 12 }, new long[] { 10, 11, 12, 20, 21 },
                Kinds((20, GroupMemberClosureRules.KindCenterLine), (21, GroupMemberClosureRules.KindCenterLine)),
                new HashSet<long> { 20, 21 });
            Assert.True(c.Holds);
            Assert.Equal(3, c.PhysicalMembers);
            Assert.Equal(2, c.RepresentationalMembers);
            Assert.Equal(new long[] { 20, 21 }, c.AcceptedExtras);
        }

        [Fact]
        public void A_tee_that_reduces_on_its_own_adds_a_fitting_that_is_accepted_as_physical()
        {
            GroupClosure c = GroupMemberClosureRules.Evaluate(new long[] { 10, 11, 12 }, new long[] { 10, 11, 12, 30, 20 },
                Kinds((30, GroupMemberClosureRules.KindAutoFitting), (20, GroupMemberClosureRules.KindCenterLine)),
                new HashSet<long> { 20 });
            Assert.True(c.Holds);
            Assert.Equal(4, c.PhysicalMembers);
            Assert.Equal(1, c.RepresentationalMembers);
        }

        [Fact]
        public void An_extra_nobody_accounts_for_still_refuses_and_is_named()
        {
            GroupClosure c = GroupMemberClosureRules.Evaluate(new long[] { 10, 11 }, new long[] { 10, 11, 99 }, Kinds(), new HashSet<long>());
            Assert.False(c.Holds);
            Assert.Equal(new long[] { 99 }, c.Unexpected);
            Assert.Contains("99", GroupMemberClosureRules.Refusal(c));
        }

        [Fact]
        public void A_requested_member_missing_from_the_group_refuses()
        {
            GroupClosure c = GroupMemberClosureRules.Evaluate(new long[] { 10, 11, 12 }, new long[] { 10, 11 }, Kinds(), null);
            Assert.False(c.Holds);
            Assert.Equal(new long[] { 12 }, c.Missing);
            Assert.Contains("missing from the group: 12", GroupMemberClosureRules.Refusal(c));
        }

        [Fact]
        public void An_empty_group_never_holds()
        {
            Assert.False(GroupMemberClosureRules.Evaluate(new long[] { 10 }, new long[0], Kinds(), null).Holds);
            Assert.False(GroupMemberClosureRules.Evaluate(new long[0], new long[0], Kinds(), null).Holds);
        }

        [Fact]
        public void The_closure_reports_why_each_extra_is_there()
        {
            GroupClosure c = GroupMemberClosureRules.Evaluate(new long[] { 1 }, new long[] { 1, 2 }, Kinds((2, "center_line")), new HashSet<long> { 2 });
            JObject j = c.ToJson(Kinds((2, "center_line")));
            Assert.Equal("center_line", (string)j["accepted_extras"][0]["why"]);
            Assert.Equal(1, (int)j["physical_members"]);
            Assert.Equal(1, (int)j["representational_members"]);
        }

        [Fact]
        public void A_group_with_mep_members_refuses_a_level_change_with_the_working_route()
        {
            string refusal = GroupLevelRules.MepLevelChangeRefusal(14, new List<long> { 5, 6, 7, 8, 9, 10 });
            Assert.StartsWith(GroupLevelRules.MepGroupLevelCode, refusal);
            Assert.Contains("14 MEP element", refusal);
            Assert.Contains("operation=ungroup", refusal);
            Assert.Contains("move_run", refusal);
            Assert.Contains("operation=create", refusal);
            Assert.Contains("Nothing was written", refusal);
            Assert.Contains("5, 6, 7, 8, 9)", refusal);   // five examples only
        }

        [Fact]
        public void A_group_without_mep_members_may_change_level()
        {
            Assert.Null(GroupLevelRules.MepLevelChangeRefusal(0, null));
        }
    }
}
