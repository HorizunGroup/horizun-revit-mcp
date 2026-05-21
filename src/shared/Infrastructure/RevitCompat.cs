using System;
using Autodesk.Revit.DB;

namespace Bimwright.Rvt.Plugin
{
    /// <summary>
    /// Centralizes all Revit API version differences.
    /// R22: net48, ElementId.IntegerValue (int), doc.Create.NewRoom()
    /// R27: net10, ElementId.Value (long), Room/Tag API may differ
    /// Uses #if REVIT2024_OR_GREATER (NOT #if REVIT2024).
    /// </summary>
    internal static class RevitCompat
    {
        public static long GetId(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return (long)id.IntegerValue;
#endif
        }

        /// <summary>Returns null-safe version for use with ?. operator results.</summary>
        public static long? GetIdOrNull(ElementId id)
        {
            if (id == null) return null;
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return (long)id.IntegerValue;
#endif
        }

        public static ElementId ToElementId(long id)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(id);
#else
            if (!CanRepresentElementId(id))
                throw new ArgumentOutOfRangeException(nameof(id), ElementIdRangeError(id));
            return new ElementId((int)id);
#endif
        }

        public static ElementId ToElementId(int id)
        {
#if REVIT2024_OR_GREATER
            return new ElementId((long)id);
#else
            return new ElementId(id);
#endif
        }

        public static bool CanRepresentElementId(long id)
        {
#if REVIT2024_OR_GREATER
            return true;
#else
            return id >= int.MinValue && id <= int.MaxValue;
#endif
        }

        public static string ElementIdRangeError(long id) =>
            "Element id " + id + " is outside this Revit version's supported ID range.";
    }
}
