using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Arithmetic of a footprint roof drawn from SEVERAL contours (a perimeter first, holes after it) and of the
    /// per-edge extras (overhang, offset). No Revit calls: the add-in feeds it counts and areas, the tests feed it
    /// numbers.
    /// </summary>
    public static class RoofFootprintRules
    {
        /// <summary>Edges across every contour: the length edge_slopes must have, perimeter edges first.</summary>
        public static int EdgeCount(IEnumerable<int> loopSizes) => loopSizes.Sum();

        /// <summary>
        /// Which edges define the slope when ONE uniform slope is requested. Only the perimeter's edges do: a hole's
        /// edge sloping would tilt the roof INTO the hole, which nobody asks for by writing a single number.
        /// Flattened in input order (perimeter, then each hole).
        /// </summary>
        public static bool[] UniformDefinesSlope(IList<int> loopSizes, double ratio)
        {
            var result = new bool[EdgeCount(loopSizes)];
            if (ratio <= 0 || loopSizes.Count == 0) return result;
            for (int i = 0; i < loopSizes[0]; i++) result[i] = true;
            return result;
        }

        /// <summary>
        /// The plan area a roof's top face(s) should project to: the perimeter's area minus every hole's.
        /// Areas are unsigned; the first is the perimeter.
        /// </summary>
        public static double NetFootprintArea(IList<double> loopAreas)
        {
            if (loopAreas == null || loopAreas.Count == 0) return 0;
            return Math.Abs(loopAreas[0]) - loopAreas.Skip(1).Sum(a => Math.Abs(a));
        }

        /// <summary>
        /// The projected-area check holds the roof to its footprint. An overhang makes the roof LARGER than its
        /// footprint by construction, so with any overhang the check does not apply (named, never passed).
        /// </summary>
        public static bool ProjectedAreaCheckApplies(IEnumerable<double> overhangs) =>
            overhangs == null || overhangs.All(o => Math.Abs(o) < 1e-12);

        /// <summary>Null when a per-edge number is finite; otherwise why not.</summary>
        public static string EdgeNumberError(string field, double? value, int edge)
        {
            if (!value.HasValue) return null;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                return "edge_slopes[" + edge + "]." + field + " must be a finite number.";
            return null;
        }
    }
}
