// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// STEEL IN A MEMBER OF A GROUP PLACED MORE THAN ONCE (acero 3.0, L8). Measured by an
// external pipeline on real models: rebar created in a member of a model group with
// several instances disappeared at the commit, with no error - only a re-read of every
// created id caught it. The bridge's apply would report it unverified AFTER the commit
// without saying why; this refuses it before, by name, with the way out.
// -----------------------------------------------------------------------------
using System.Globalization;

namespace Horizun.Revit.Core
{
    public static class GroupHostRule
    {
        public const string Code = "host_in_model_group";

        /// <summary>
        /// Why steel may not be planned into a host that belongs to a group, or null. `groupId` is the
        /// group instance's id (negative when the host is in no group), `instances` how many instances
        /// of that group's type the document holds.
        /// </summary>
        public static string Refusal(long groupId, string groupName, int instances)
        {
            if (groupId < 0 || instances <= 1) return null;
            return "the host is a member of model group " + groupId.ToString(CultureInfo.InvariantCulture) +
                   (string.IsNullOrWhiteSpace(groupName) ? "" : " ('" + groupName + "')") + ", whose type is placed " +
                   instances.ToString(CultureInfo.InvariantCulture) + " times. Revit removes reinforcement created in a " +
                   "member of a group placed more than once, at the commit and without an error. Ungroup it first " +
                   "(horizun_ungroup_and_mark keeps the group's name on the members), or reinforce it in the group's " +
                   "edit mode by hand.";
        }
    }
}
