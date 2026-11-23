// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// edit_dimensions move_by_sheet turns a paper vector into a model vector through the
// rotation of the viewport that shows the dimension's view. A legend can be placed
// on several sheets; taking the first viewport silently chose one rotation among
// several (review m9). Equal rotations are unambiguous; different ones are refused.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class ViewportRotationChoice
    {
        /// <summary>The single rotation every viewport of the view agrees on, or null with the reason in error.</summary>
        public static string Resolve(IEnumerable<string> rotations, string dimensionLabel, out string error)
        {
            error = null;
            var list = (rotations ?? Enumerable.Empty<string>()).ToList();
            if (list.Count == 0)
            {
                error = "move_by_sheet needs the view of dimension " + dimensionLabel + " placed on a sheet";
                return null;
            }
            var distinct = list.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count == 1) return distinct[0];
            error = "move_by_sheet is ambiguous for dimension " + dimensionLabel + ": its view is placed on " + list.Count +
                    " sheets with different rotations (" + string.Join(", ", distinct) + "). Use move_by in model units, or a view placed once";
            return null;
        }
    }
}
