// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// create_selection_filter (horizun_manage_views): a SelectionFilterElement - a view filter
// that holds a fixed set of ELEMENT IDS rather than parameter rules - optionally applied to
// a view with graphic overrides in the same action. The rule-based filters were covered
// (create_filter / apply_filter); a set picked by hand was not.
//
// Revit-free half: what the request must carry and what a re-read must show.
// A set whose members changed between the request and the re-read is a failed write.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class SelectionFilterRules
    {
        public const int MaxElements = 5000;

        /// <summary>Null when the name can be given to a filter; otherwise why not.</summary>
        public static string NameError(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "name is required: a filter without a name cannot be referred to again, and Revit would invent one nobody chose.";
            if (name.Length > 255) return "name is longer than 255 characters.";
            return PlanimetryFixRules.NameError("name", name);
        }

        /// <summary>Null when the id list is a usable member set; otherwise why not.</summary>
        public static string MembersError(IList<long> ids)
        {
            if (ids == null || ids.Count == 0)
                return "element_ids must name at least one element: an empty selection filter selects nothing and would be a filter nobody can use.";
            if (ids.Count > MaxElements) return "element_ids holds more than " + MaxElements + " entries.";
            if (ids.Distinct().Count() != ids.Count) return "element_ids repeats an element.";
            return null;
        }

        /// <summary>Overrides or visibility without a view have nowhere to go; refused rather than ignored.</summary>
        public static string OrphanFieldError(bool hasView, bool hasOverrides, bool hasVisible)
        {
            if (hasView) return null;
            if (hasOverrides) return "overrides apply the filter to a view: send view_id (or view_key) with them, or create the filter alone without overrides.";
            if (hasVisible) return "visible applies the filter to a view: send view_id (or view_key) with it.";
            return null;
        }

        /// <summary>The members the re-read must show: exactly the requested ones, no more, no fewer.</summary>
        public static bool SameMembers(IEnumerable<long> requested, IEnumerable<long> found)
        {
            if (requested == null || found == null) return false;
            var a = new HashSet<long>(requested); var b = new HashSet<long>(found);
            return a.SetEquals(b);
        }
    }
}
