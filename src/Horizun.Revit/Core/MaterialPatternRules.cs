using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    /// <summary>One fill pattern of the document, as the material pattern choice sees it.</summary>
    public sealed class MaterialPatternCandidate
    {
        public long Id; public string Name; public bool IsDrafting; public bool IsSolid;
        public MaterialPatternCandidate(long id, string name, bool isDrafting, bool isSolid) { Id = id; Name = name; IsDrafting = isDrafting; IsSolid = isSolid; }
    }

    /// <summary>
    /// manage_materials surface_pattern / cut_pattern. Field 2026-11-09: a MODEL pattern given by its exact name was refused,
    /// because only drafting patterns were searched; Revit materials take model patterns too (bricks, tiles drawn to scale).
    /// Order: a drafting pattern by name (what this always resolved, so no existing call changes), then a model pattern by
    /// name, then a pattern element id. 'solid' is the drafting solid fill, as before. A CUT pattern takes drafting patterns
    /// only: measured on Revit 2025, Material.CutForegroundPatternId throws "The FillPattern target must be a drafting
    /// pattern" at the apply, after a rehearsal that had accepted it - so a model pattern for cut is refused up front.
    /// </summary>
    public static class MaterialPatternRules
    {
        /// <summary>The chosen pattern id, or null when nothing matches.</summary>
        public static long? Pick(IEnumerable<MaterialPatternCandidate> patterns, string name, bool allowModel = true)
        {
            string wanted = (name ?? "").Trim();
            if (wanted.Length == 0) return null;
            var all = new List<MaterialPatternCandidate>(patterns ?? new MaterialPatternCandidate[0]);
            bool solid = string.Equals(wanted, "solid", StringComparison.OrdinalIgnoreCase);
            foreach (bool drafting in allowModel ? new[] { true, false } : new[] { true })
                foreach (MaterialPatternCandidate c in all)
                {
                    if (c.IsDrafting != drafting) continue;
                    if (string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)) return c.Id;
                    if (solid && drafting && c.IsSolid) return c.Id;
                }
            if (long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
                foreach (MaterialPatternCandidate c in all)
                    if (c.Id == raw && (allowModel || c.IsDrafting)) return c.Id;
            return null;
        }

        public static string NotFound(string name, bool allowModel = true) => allowModel
            ? "no fill pattern named '" + name + "' exists (drafting or model), and it is not the id of one. Pass 'solid' for the " +
              "solid fill, the exact name or element id of a drafting or model pattern, or load the pattern first."
            : "no DRAFTING fill pattern named '" + name + "' exists, and it is not the id of one. A material's cut pattern takes " +
              "drafting patterns only (Revit refuses a model pattern there); pass 'solid', or the exact name or id of a drafting pattern.";
    }
}
