// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHAT THE API ACCEPTS BUT THE PAPER DOES NOT SHOW (field report 2026-11-17).
//
// horizun_api_help answers "which members exist" by reflection. Some members exist and still
// do not do what a script expects; those traps were measured in the field and are attached to
// the type they belong to, so a script author reads them where they look.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ApiFieldNotes
    {
        private const string NoCategoryTable =
            "There is no per-category table for a link (V/G > Revit Links > Custom > Grids/Levels off) in the 2023-2027 API. " +
            "Typed alternatives: horizun_view_visibility link_datum_cleanup (a hidden filter 'Type Name not equal to' the host's types; " +
            "reports what it cannot separate) or copy_link_overrides from a view set by hand.";

        private const string Nested =
            "Showing a NESTED link needs both the nested RevitLinkInstance and its RevitLinkType visible: unhiding the instance alone changes nothing.";

        private const string LinksCannot =
            "WHAT THE API CANNOT DO WITH LINKS (field report 2026-11-17, Revit 2024; not re-measured on every year): " +
            "switch a link's Grids/Levels off per category (RevitLinkGraphicsSettings Custom has no category table); " +
            "hide one linked element (View.HideElements takes host ids); override one linked element's graphics; " +
            "replace a linked element one by one; hatch a link's finishes from the host.";

        private const string LinksCan =
            "WHAT WORKS: set a link By host view, By linked view or Custom (LinkVisibilityType, LinkedViewId; 2025+ also ViewFilterType); " +
            "hide a whole class of linked datums with an invisible ParameterFilterElement on the host view (horizun_view_visibility " +
            "link_datum_cleanup: 'Type Name not equal to' the host's types; it lists the datums it cannot separate, e.g. a linked type " +
            "with the same name as a host one, and only the datum Name is a string a rule compares by value); copy a hand-set RVT Links tab " +
            "to other views (copy_link_overrides); read what a link draws in a view (horizun_selection_context view_contents).";

        private const string ColourNoPattern =
            "A cut/surface foreground colour with no pattern paints nothing, and Get*Overrides still re-reads the colour: set the pattern (solid for a flat fill) and its visibility too.";

        private static readonly Dictionary<string, string[]> Notes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["RevitLinkGraphicsSettings"] = new[]
            {
                NoCategoryTable,
                LinksCannot,
                LinksCan,
                "View.GetLinkOverrides returns null when the link row is at its default (By host view); 2024 exposes only LinkVisibilityType and LinkedViewId, 2025+ also ViewFilterType and the other switches."
            },
            ["RevitLinkInstance"] = new[] { Nested, NoCategoryTable, LinksCannot, LinksCan },
            ["RevitLinkType"] = new[] { Nested, LinksCannot, LinksCan },
            ["View"] = new[]
            {
                "HideElements/UnhideElements take HOST element ids only; an element inside a link cannot be hidden one by one.",
                ColourNoPattern,
                "Element overrides live per view: setting them in one view leaves every other view as it was.",
                "A filter or category row with halftone wins over an element override: halftone=false on the element means 'no override', not 'off'."
            },
            ["OverrideGraphicSettings"] = new[]
            {
                ColourNoPattern,
                "A surface override on a FilledRegion never prints (its fill comes from FilledRegionType).",
                "A cut override on an element the view does not cut draws no pixel.",
                "Geometry nested in a family (stair nosings, in-family sweeps) may keep its own graphics."
            },
            ["FilledRegion"] = new[] { "A surface override on a FilledRegion never prints; change the FilledRegionType's pattern instead." },
            ["ParameterFilterElement"] = new[]
            {
                "A filter on a host view reaches linked elements only while the link displays with the host's filters (By host view; 2025+ ViewFilterType ByHostView).",
                "A Type Name rule cannot separate a linked datum whose type has the same name as a host type."
            }
        };

        /// <summary>The field notes of a type (simple name), or null.</summary>
        public static JArray For(string typeName)
        {
            if (string.IsNullOrEmpty(typeName) || !Notes.TryGetValue(typeName, out string[] notes)) return null;
            return new JArray(notes);
        }
    }
}
