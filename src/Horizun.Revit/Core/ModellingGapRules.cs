using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>kind=reference_plane of horizun_create_elements: a named reference plane drawn in a view. No Revit calls.</summary>
    public static class ReferencePlaneRules
    {
        public const double MinLengthFeet = 1.0 / 304.8;

        /// <summary>Null when the row is well formed; otherwise why not. Points in feet.</summary>
        public static string Validate(string name, double[] start, double[] end, string viewKindName)
        {
            if (string.IsNullOrWhiteSpace(name)) return "name is required for a reference plane: an unnamed one cannot be told from the rest.";
            if (name.Trim().Length > 255) return "name is longer than 255 characters.";
            if (start == null || end == null || start.Length != 3 || end.Length != 3) return "start and end must be [x,y,z].";
            if (start.Concat(end).Any(v => double.IsNaN(v) || double.IsInfinity(v))) return "start and end must be finite.";
            double dx = end[0] - start[0], dy = end[1] - start[1], dz = end[2] - start[2];
            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) < MinLengthFeet) return "start and end are the same point (less than 1 mm apart).";
            if (string.IsNullOrEmpty(viewKindName)) return "view_id must identify a view that can show a reference plane.";
            return null;
        }

        /// <summary>
        /// The cut vector Revit needs is the view's own normal: a plan's plane stands vertical (cut vector Z), a
        /// section's contains the view direction. Returns it unit-length, or null when the direction is degenerate.
        /// </summary>
        public static double[] CutVector(double[] viewDirection)
        {
            if (viewDirection == null || viewDirection.Length != 3) return null;
            double n = Math.Sqrt(viewDirection.Sum(v => v * v));
            if (n < 1e-9 || double.IsNaN(n)) return null;
            return viewDirection.Select(v => v / n).ToArray();
        }

        /// <summary>The plane holds the line start-end and the cut vector, so the line must not run along the cut vector.</summary>
        public static bool LineAlongCutVector(double[] start, double[] end, double[] cut)
        {
            double dx = end[0] - start[0], dy = end[1] - start[1], dz = end[2] - start[2];
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-12) return true;
            double dot = (dx * cut[0] + dy * cut[1] + dz * cut[2]) / len;
            return Math.Abs(Math.Abs(dot) - 1) < 1e-6;
        }
    }

    /// <summary>horizun_join_geometry excavate / unexcavate: a toposolid cut by another element (Revit 2025+). No Revit calls.</summary>
    public static class ToposolidExcavationRules
    {
        public const string Excavate = "excavate";
        public const string Unexcavate = "unexcavate";
        /// <summary>Toposolid.ExcavateBy first appears in this Revit release.</summary>
        public const int FirstRevitYear = 2025;

        public static bool IsExcavation(string operation) =>
            string.Equals(operation, Excavate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, Unexcavate, StringComparison.OrdinalIgnoreCase);

        public static bool Supported(int revitYear) => revitYear >= FirstRevitYear;

        public static string UnsupportedMessage(int revitYear) =>
            "excavate/unexcavate need Toposolid.ExcavateBy, which Revit " + FirstRevitYear + " introduced; this Revit (" + revitYear +
            ") has no such API call" + (revitYear < 2024 ? " and no Toposolid at all" : "") +
            ". Nothing was written. Cut the terrain in Revit, or in this release model the excavation as a toposolid sub-division.";

        /// <summary>Null when pairs is [[toposolid, excavator], ...] with distinct, positive ids; otherwise why not.</summary>
        public static string PairsError(JToken pairs, JToken elementIds)
        {
            if (elementIds != null && elementIds.Type != JTokenType.Null)
                return "excavate/unexcavate take pairs: [[toposolid_id, excavating_element_id], ...]; element_ids is not applicable.";
            var arr = pairs as JArray;
            if (arr == null || arr.Count == 0 || arr.Count > JoinGeometryRules.MaxPairs)
                return "pairs is required: [[toposolid_id, excavating_element_id], ...], 1.." + JoinGeometryRules.MaxPairs + " entries.";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken t in arr)
            {
                var pair = t as JArray;
                if (pair == null || pair.Count != 2 || pair.Any(x => x.Type != JTokenType.Integer) || pair.Any(x => x.Value<long>() <= 0))
                    return "each pair must be two positive integer ids: [toposolid_id, excavating_element_id].";
                if (pair[0].Value<long>() == pair[1].Value<long>()) return "a toposolid cannot excavate itself.";
                if (!seen.Add(pair[0].Value<long>() + ":" + pair[1].Value<long>())) return "pairs names the same pair twice.";
            }
            return null;
        }
    }
}
