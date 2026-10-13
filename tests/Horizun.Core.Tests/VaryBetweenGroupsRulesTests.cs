// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// When horizun_write_params_verified calls SetAllowVaryBetweenGroups(true).
//
// Course dry run 2026-09-30, defect #15: filling Project Information
// (PROJECT_NAME, PROJECT_BUILDING_NAME) committed and confirmed 2/2, and every row
// still carried vary_between_groups_error "does not support allowVaryBetweenGroups",
// because the step was attempted for any parameter whose VariesAcrossGroups read
// false. It now runs only where it can apply.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class VaryBetweenGroupsRulesTests
    {
        private static VaryStepFacts Facts(string targetKind = "instance", bool? builtIn = false,
                                           string binding = "instance", bool? variesBefore = false,
                                           bool allowVary = true) =>
            new VaryStepFacts
            {
                AllowVary = allowVary,
                VariesBefore = variesBefore,
                IsBuiltIn = builtIn,
                TargetKind = targetKind,
                Binding = binding
            };

        [Fact]
        public void A_project_parameter_bound_per_instance_that_does_not_vary_yet_is_set()
        {
            // The case the step exists for: without it Revit raises the ungroup modal.
            Assert.True(VaryBetweenGroupsRules.ShouldAttempt(Facts()));
        }

        [Fact]
        public void Project_information_built_ins_are_not_attempted()
        {
            // The dry run's exact rows: PROJECT_NAME on Project Information, a built-in
            // parameter with no binding, VariesAcrossGroups reading false.
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(
                Facts(targetKind: "project_info", builtIn: true, binding: null)));
        }

        [Fact]
        public void Project_information_is_never_attempted_even_for_a_shared_parameter()
        {
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(Facts(targetKind: "project_info", builtIn: false, binding: "instance")));
        }

        [Fact]
        public void A_built_in_parameter_on_an_instance_is_not_attempted()
        {
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(Facts(builtIn: true, binding: null)));
        }

        [Fact]
        public void A_type_target_or_a_type_binding_is_not_attempted()
        {
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(Facts(targetKind: "type")));
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(Facts(binding: "type")));
        }

        [Fact]
        public void Already_varying_unreadable_or_declined_is_not_attempted()
        {
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(Facts(variesBefore: true)));
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(Facts(variesBefore: null)));
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(Facts(allowVary: false)));
            Assert.False(VaryBetweenGroupsRules.ShouldAttempt(null));
        }

        [Fact]
        public void An_instance_write_whose_binding_or_origin_could_not_be_read_still_attempts()
        {
            // Unknown is not a reason to skip the one step that keeps the modal away;
            // a failed attempt is reported on the row, as it always was.
            Assert.True(VaryBetweenGroupsRules.ShouldAttempt(Facts(builtIn: null, binding: null)));
        }

        [Fact]
        public void A_group_member_is_refused_unless_the_definition_change_is_asked_for()
        {
            // audit 2026-10-08, 6.9: the definition change is opt-in.
            Assert.True(VaryBetweenGroupsRules.RefuseGroupMember(false, "instance", false, true));
            Assert.False(VaryBetweenGroupsRules.RefuseGroupMember(true, "instance", false, true));
            Assert.False(VaryBetweenGroupsRules.RefuseGroupMember(false, "instance", true, true));    // already varies
            Assert.False(VaryBetweenGroupsRules.RefuseGroupMember(false, "instance", false, false));  // not in a group
            Assert.False(VaryBetweenGroupsRules.RefuseGroupMember(false, "type", false, true));
            Assert.False(VaryBetweenGroupsRules.RefuseGroupMember(false, "instance", null, true));    // unreadable: attempted and reported
        }
    }
}
