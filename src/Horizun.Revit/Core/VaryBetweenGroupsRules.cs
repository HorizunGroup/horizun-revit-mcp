// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHEN horizun_write_params_verified should call SetAllowVaryBetweenGroups(true)
// before a write, decided out of facts rather than by trying it.
//
// The step exists for one case: a project or shared parameter bound to a category by
// an InstanceBinding, written with a value that differs between members of a model
// group. Without the flag Revit raises the "ungroup" modal mid-batch and the bridge
// hangs. Everywhere else the setter has nothing to do, and Revit says so with an
// ArgumentException ("This parameter does not support the specified value of
// allowVaryBetweenGroups"). The command used to call it for every parameter whose
// VariesAcrossGroups read false and print that exception on the row - so a clean
// write of Project Information (PROJECT_NAME, PROJECT_BUILDING_NAME: built-in
// parameters of an element no group can hold) came back with a
// vary_between_groups_error on every row of a write that succeeded (course dry run
// 2026-09-30, defect #15).
//
// So the step runs only when it can apply, and when it does not, nothing is said:
// there was nothing to set. An attempt that does fail is still reported, as before.
// No `using Autodesk.*`: the command reads the facts, this decides.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    /// <summary>What the command knows about one planned write when deciding.</summary>
    public sealed class VaryStepFacts
    {
        /// <summary>allow_vary_between_groups as the caller sent it (the command's default is false since audit 2026-10-08, 6.9).</summary>
        public bool AllowVary = true;

        /// <summary>InternalDefinition.VariesAcrossGroups before the write; null when unreadable.</summary>
        public bool? VariesBefore;

        /// <summary>The definition is a BuiltInParameter (BuiltInParameter != INVALID). Null when unreadable.</summary>
        public bool? IsBuiltIn;

        /// <summary>"instance", "type" or "project_info" - the write's target kind.</summary>
        public string TargetKind;

        /// <summary>
        /// The binding the document holds for the definition: "instance", "type", or null when
        /// none was found (a built-in parameter has none) or it could not be read.
        /// </summary>
        public string Binding;
    }

    public static class VaryBetweenGroupsRules
    {
        /// <summary>
        /// Should the command call SetAllowVaryBetweenGroups(true) for this write?
        ///
        ///   allow_vary_between_groups=false        -> no (the caller said so)
        ///   already varies, or unreadable          -> no (nothing to change / nothing known)
        ///   Project Information or an element type -> no (no group holds them)
        ///   built-in parameter                     -> no (the setter is for project/shared ones)
        ///   bound by a TypeBinding                 -> no (a type value cannot vary per instance)
        ///   otherwise                              -> yes, and a failure is reported on the row
        ///
        /// An instance target whose binding could not be read still attempts it: that is the
        /// case the step exists for, and skipping it would bring the modal back.
        /// </summary>
        /// <summary>
        /// Must this write be refused BEFORE anything is written? An instance in a model group
        /// whose parameter does not vary between groups, with the caller not allowing the
        /// definition change: Revit would ask to ungroup mid-batch (audit 2026-10-08, 6.9).
        /// Unreadable (null) is not refused here; the attempt and its report decide it.
        /// </summary>
        public static bool RefuseGroupMember(bool allowVary, string targetKind, bool? variesBefore, bool inGroup) =>
            !allowVary && targetKind == "instance" && variesBefore == false && inGroup;

        public static bool ShouldAttempt(VaryStepFacts f)
        {
            if (f == null || !f.AllowVary) return false;
            if (f.VariesBefore != false) return false;
            if (f.TargetKind == "project_info" || f.TargetKind == "type") return false;
            if (f.IsBuiltIn == true) return false;
            if (f.Binding == "type") return false;
            return true;
        }
    }
}
