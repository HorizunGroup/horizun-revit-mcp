// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The verdict of horizun_bind_shared_param, Revit-free so it can be proved
// without a model open. BindSharedParamCommand takes the three measurements
// (kind, categories, VariesAcrossGroups) and this file decides what they add up
// to.
//
// "Vary between groups" is an INSTANCE notion. A Model Group holds instances; a
// type parameter has one value per type, shared by every instance inside and
// outside any group, so there is nothing that could vary and nothing that could
// raise the DESAGRUPAR modal. Revit agrees: SetAllowVaryBetweenGroups on a type
// parameter throws "This parameter does not support the specified value of
// allowVaryBetweenGroups". Found in the 2026-10-03 course rehearsal: a Type
// binding committed and read back correctly, then reported not_bound / failed
// because the flag it could never carry read false. For a Type binding the flag
// is NOT APPLICABLE - not a measurement that failed, and not a gate on the
// verdict.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public static class SharedParamBindingRules
    {
        public const string Confirmed = "confirmed";
        public const string NotBound = "not_bound";
        public const string Unknown = "unknown";
        public const string CategoriesDropped = "categories_dropped";

        /// <summary>
        /// Whether VariesAcrossGroups means anything for this binding kind. Only an
        /// InstanceBinding can vary between group instances.
        /// </summary>
        public static bool VaryApplies(bool typeBinding) => !typeBinding;

        /// <summary>
        /// The facts BindSharedParamCommand measured after the commit. Each nullable is
        /// tri-state on purpose: null is "could not look", never "false".
        /// </summary>
        public sealed class Facts
        {
            public bool Committed;
            public bool? ExistsAfter;
            public bool KindMeasured;
            public bool KindMatches;
            public bool CatsMeasured;
            public bool CatsComplete;
            public bool CatsHaveUnreadable;
            public bool? NoUnintendedDrop;
            public bool? VariesAfter;
            public bool AllowVaryRequested;
            public bool TypeBinding;
        }

        public static string Classify(Facts f)
        {
            // A rollback is the only claim of absence here that does not rest on a read:
            // Revit undid the transaction, so nothing from this call reached the model.
            if (!f.Committed) return NotBound;

            // Could not even establish whether a binding exists.
            if (f.ExistsAfter == null) return Unknown;
            if (f.ExistsAfter == false) return NotBound;

            if (!f.KindMeasured) return Unknown;
            if (!f.KindMatches) return NotBound;      // it is bound - as the OTHER kind.

            if (!f.CatsMeasured) return Unknown;
            // A category missing from a list that is admittedly a LOWER BOUND is not a
            // category that is absent - it may be one of the ones we could not read.
            if (!f.CatsComplete && f.CatsHaveUnreadable) return Unknown;
            if (!f.CatsComplete) return NotBound;     // read back, and your categories are not in it.

            // Your categories are in it - but with merge=true you asked for the UNION, so a
            // binding missing OTHER categories that were there before is not what you asked
            // for either, and the values in them are gone.
            if (!f.NoUnintendedDrop.HasValue) return Unknown;
            if (!f.NoUnintendedDrop.Value) return CategoriesDropped;

            // A Type binding has no VariesAcrossGroups to gate on. Its kind already
            // matched above, so this is the binding's measured kind, not the request.
            if (!VaryApplies(f.TypeBinding)) return Confirmed;

            // The flag we could not read is not the flag that is off. One of those means
            // the desagrupar modal is armed; the other means we do not know if it is.
            if (!f.VariesAfter.HasValue) return Unknown;
            if (f.AllowVaryRequested && !f.VariesAfter.Value) return NotBound;

            return Confirmed;
        }
    }
}
