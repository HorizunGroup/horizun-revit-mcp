using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// THE NAMES THE AUDIT'S FINDINGS CARRY, declared once.
    ///
    /// The pre-delivery gate reads its measurements out of the findings, keyed by
    /// the finding's own `check` string. Both halves used to spell those names
    /// independently, and one of them was wrong: the gate mapped
    /// `forbid_orphan_group_types` to a check called "group_types" while the
    /// finding emits "orphan_group_types". The lookup could never hit, so the row
    /// was permanently `not_measurable` - and a requirement set containing that key
    /// could never return the verdict `pass`, whatever the model was like.
    ///
    /// It failed silently and in the safe direction, which is why it survived: a
    /// gate that will not pass looks like a strict gate. The cost was a standard
    /// somebody declared and that was never actually enforced, reported every run
    /// as "not measurable" beside ten checks that were measured perfectly well.
    ///
    /// One list, and a test that every name the gate maps to is a name the audit
    /// actually emits.
    /// </summary>
    public static class AuditCheckNames
    {
        public const string Warnings = "warnings";
        public const string OrphanGroupTypes = "orphan_group_types";
        public const string InPlaceFamilies = "in_place_families";
        public const string OpenMepConnectors = "open_mep_connectors";
        public const string UnpinnedLinks = "unpinned_links";
        public const string ViewsWithoutTemplate = "views_without_template";
        public const string ImportedCad = "imported_cad";
        public const string ViewsOffSheets = "views_off_sheets";
        public const string Rooms = "rooms";
        public const string Links = "links";
        public const string DesignOptions = "design_options";

        // THE DIAGNOSTICS P0 SLICE. Each of these findings publishes named PARTS
        // rather than one count, because "how many levels share a name" and "how
        // many levels sit on top of each other" are two questions about one area
        // and the gate could previously read only one number per finding.
        public const string Coordinates = "coordinates";
        public const string Datums = "datums";
        public const string Readiness = "readiness";

        /// <summary>
        /// Not a finding: the file size is measured beside the findings and injected
        /// into the gate's measurements under this name. It is here because the gate
        /// maps a requirement onto it, and the test below has to know that a mapping
        /// onto this one is legitimate rather than a typo.
        /// </summary>
        public const string FileSizeMb = "file_size_mb";

        /// <summary>Every name a finding can carry. The order is the order they run in.</summary>
        public static readonly string[] Findings =
        {
            Warnings, OrphanGroupTypes, InPlaceFamilies, OpenMepConnectors, UnpinnedLinks,
            ViewsWithoutTemplate, ImportedCad, ViewsOffSheets, Rooms, Links, DesignOptions,
            Coordinates, Datums, Readiness
        };

        /// <summary>Every name the gate may legitimately map a requirement onto.</summary>
        public static IEnumerable<string> Measurable()
        {
            foreach (string n in Findings) yield return n;
            yield return FileSizeMb;
        }

        /// <summary>
        /// A check name may name a PART of a finding: "datums.coincident_levels".
        /// The head must still be a finding this audit can emit - a part of a
        /// finding that does not exist is the same defect as before, one level
        /// down.
        /// </summary>
        public static bool IsMeasurable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int dot = name.IndexOf('.');
            string head = dot < 0 ? name : name.Substring(0, dot);
            if (dot >= 0 && dot == name.Length - 1) return false;   // "datums." names no part
            foreach (string n in Measurable())
                if (string.Equals(n, head, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
