// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A SURFACE OVERRIDE THAT COLOURED NOTHING (field cycle, 2026-10-21).
//
// MEASURED: a view override with surface_color / cut_color set the colour of the
// foreground pattern, but took the pattern itself from "the first solid drafting fill
// pattern in the document" - and when the document had none, set the colour alone and
// reported the override as applied. A colour on no pattern paints nothing. The
// re-read compared the colour only, so it could never notice.
//
// This file is the Revit-free half: which pattern a request means (solid by default,
// or a named/numbered one), how the solid one is chosen deterministically, and the
// refusal when there is none. The Revit half applies the pattern AND its visibility
// and re-reads both.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PatternCandidate
    {
        public long Id;
        public string Name;
        public bool IsSolid;
        /// <summary>Drafting patterns scale with the sheet; model patterns with the model. Solid is only meaningful as drafting.</summary>
        public bool IsDrafting;
    }

    public static class OverridePatternRules
    {
        public const string Solid = "solid";

        /// <summary>What a pattern spec asks for: kind is "solid", "id" or "name".</summary>
        public static void Parse(string raw, out string kind, out long id, out string name)
        {
            kind = Solid; id = 0; name = null;
            if (string.IsNullOrWhiteSpace(raw)) return;
            string t = raw.Trim();
            if (string.Equals(t, Solid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "solid fill", StringComparison.OrdinalIgnoreCase)) return;
            if (long.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id))
            {
                kind = "id";
                return;
            }
            kind = "name"; name = t;
        }

        /// <summary>
        /// The solid fill an override uses: the drafting one, "Solid fill" by name first, the
        /// lowest id otherwise - the same answer on every run. Null when the document has none.
        /// </summary>
        public static PatternCandidate PickSolid(IEnumerable<PatternCandidate> candidates)
        {
            if (candidates == null) return null;
            return candidates.Where(c => c != null && c.IsSolid && c.IsDrafting)
                .OrderBy(c => string.Equals(c.Name, "Solid fill", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Id)
                .FirstOrDefault();
        }

        /// <summary>The candidate a name or id spec means; null when none matches (the caller refuses).</summary>
        public static PatternCandidate Pick(IEnumerable<PatternCandidate> candidates, string kind, long id, string name)
        {
            if (candidates == null) return null;
            if (kind == Solid) return PickSolid(candidates);
            if (kind == "id") return candidates.FirstOrDefault(c => c != null && c.Id == id);
            return candidates.FirstOrDefault(c => c != null && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static string NotFound(string field, string raw) =>
            field + " '" + (string.IsNullOrWhiteSpace(raw) ? Solid : raw) + "' is not a fill pattern of this document" +
            (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), Solid, StringComparison.OrdinalIgnoreCase)
                ? ": it has no solid DRAFTING fill pattern, so a colour alone would paint nothing. Add one (Manage > Additional Settings > Fill Patterns) or name another pattern."
                : ". Use 'solid', the exact name, or the pattern's element id.");

        /// <summary>A colour is only visible on a pattern that is switched on: the re-read must see both.</summary>
        public static bool PatternHolds(long wantedPatternId, long actualPatternId, bool actualVisible) =>
            wantedPatternId == actualPatternId && actualVisible;
    }
}
