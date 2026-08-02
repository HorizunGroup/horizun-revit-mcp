// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// ElementId <-> long across Revit versions. Revit 2024+ made ElementId 64-bit
// and added ElementId.Value (long); 2023 and earlier only have IntegerValue
// (int). This is the one place that knows the difference, so no command has to.
//
// REVIT2024 used to sit in the OLD branch of every one of these, contradicting
// the paragraph above. Two deprecation warnings on the 2024 build were the
// symptom; the defect was that on Revit 2024 - which supports 64-bit ids - this
// took the 32-bit path, so CanRepresent REFUSED ids that Revit itself accepts.
// Taking the warnings seriously is what found it.
//
// We compile per Revit year (the REVITxxxx constant), so the right path is
// chosen at compile time — no reflection, no runtime branching.
// -----------------------------------------------------------------------------
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    public static class Rid
    {
        /// <summary>The numeric value of an ElementId as a long, on any supported Revit.</summary>
        public static long Value(ElementId id)
        {
            if (id == null) return -1;
#if REVIT2022 || REVIT2023
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        /// <summary>Build an ElementId from a long. Guards the 32-bit range on old Revit.</summary>
        public static ElementId Make(long value)
        {
#if REVIT2022 || REVIT2023
            return new ElementId((int)value);
#else
            return new ElementId(value);
#endif
        }

        /// <summary>True if a long can be represented as an ElementId on this Revit.</summary>
        public static bool CanRepresent(long value)
        {
#if REVIT2022 || REVIT2023
            return value >= int.MinValue && value <= int.MaxValue;
#else
            return true;
#endif
        }

        /// <summary>Explains why a long is out of ElementId range on this Revit (for error text).</summary>
        public static string RangeError(long value)
        {
#if REVIT2022 || REVIT2023
            return $"Element id {value} is outside the 32-bit range this Revit ({typeof(ElementId).Assembly.GetName().Version}) supports.";
#else
            return $"Element id {value} could not be represented.";
#endif
        }

        // --- Handler-facing surface. The write/delete verbs interrogate ids by these
        //     names; each maps onto the primitives above so there is one compat story. ---

        /// <summary>The id as a long. Throws on null id (the caller has already null-checked).</summary>
        public static long GetId(ElementId id)
        {
#if REVIT2022 || REVIT2023
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        /// <summary>Null-safe id, for the results of ?. chains.</summary>
        public static long? GetIdOrNull(ElementId id)
        {
            if (id == null) return null;
            return GetId(id);
        }

        /// <summary>Build an ElementId from a long, refusing (not truncating) an out-of-range value on old Revit.</summary>
        public static ElementId ToElementId(long id)
        {
#if REVIT2022 || REVIT2023
            if (!CanRepresentElementId(id))
                throw new System.ArgumentOutOfRangeException(nameof(id), ElementIdRangeError(id));
            return new ElementId((int)id);
#else
            return new ElementId(id);
#endif
        }

        /// <summary>True if a long fits an ElementId on this Revit.</summary>
        public static bool CanRepresentElementId(long id) => CanRepresent(id);

        /// <summary>Human-readable reason a long is out of ElementId range here.</summary>
        public static string ElementIdRangeError(long id) => RangeError(id);
    }
}
