// -----------------------------------------------------------------------------
// Horizun MCP - a type's name as a requirement set writes it, in any Revit language. Original Horizun code.
//
// MEASURED (campaign 5b, 2026-09-18): the same requirement set, "Basic Wall: Generic - 8\"", was found in
// Revit 2023 in the morning and refused in the afternoon - the afternoon's Revit 2023 ran with a SPANISH
// interface, where the system family is "Muro básico". A SYSTEM family's name is translated with the
// interface; a loadable family's name is whatever its file says. So a wall type is also known by a
// canonical label built from its KIND (Basic / Curtain / Stacked Wall) and its type name, which does not
// change with the language. The localized label still wins when both are asked for; nothing else moves.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Commands
{
    public static class TypeNames
    {
        public static string Localized(ElementType t)
        {
            try { return string.IsNullOrEmpty(t.FamilyName) ? t.Name : t.FamilyName + ": " + t.Name; }
            catch { return null; }
        }

        /// <summary>"Basic Wall: Generic - 8\"" whatever the interface language, for wall types; null otherwise.</summary>
        public static string Canonical(ElementType t)
        {
            var wt = t as WallType;
            if (wt == null) return null;
            string family;
            switch (wt.Kind)
            {
                case WallKind.Basic: family = "Basic Wall"; break;
                case WallKind.Curtain: family = "Curtain Wall"; break;
                case WallKind.Stacked: family = "Stacked Wall"; break;
                default: return null;
            }
            try { return family + ": " + wt.Name; } catch { return null; }
        }

        public static bool Matches(ElementType t, string wanted, StringComparison comparison)
        {
            if (t == null || string.IsNullOrEmpty(wanted)) return false;
            return string.Equals(Localized(t), wanted, comparison) || string.Equals(Canonical(t), wanted, comparison);
        }
    }
}
