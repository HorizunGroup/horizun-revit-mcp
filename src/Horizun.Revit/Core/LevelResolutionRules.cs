// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH FLOOR AN ELEMENT STANDS ON, read from where its category actually keeps it.
//
// Revit has no single answer. Element.LevelId is its consolidated one and is right
// for walls, floors, rooms, doors and most hosted instances - and INVALID for a
// beam, whose level is its Reference Level (INSTANCE_REFERENCE_LEVEL_PARAM), and
// for some roofs and stairs, which keep it in their own base-level parameter.
//
// MEASURED (dry run 2026-09, Autodesk sample models): horizun_query_model grouped
// all 397 Structural Framing, 91 Rooms, 6 Roofs and 5 Stairs under "(no level)".
// The read was a `??` chain of parameters, and `??` stops at the first parameter
// that EXISTS - not the first that names a level. A beam carries a level parameter
// holding an invalid id, so the chain stopped there and never reached the one that
// held the answer. "(no level)" over 397 beams that each have one is a falsehood
// with the shape of a fact.
//
// So the rule is an ORDERED list of sources, and a source counts only when it
// resolves to a real level. A source that is absent, holds no id or names
// something that is not a level is passed over. "(no level)" is kept for elements
// where EVERY source was asked and none answered - and the source that did answer
// is reported, so a count per source shows how each level was found.
//
// Revit-free: the Revit half (Commands/ElementLevelReader.cs) only reads each
// source; the order and the stopping rule live here and are proved with fakes.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>What one source said about an element's level.</summary>
    public enum LevelProbeState
    {
        /// <summary>The element has no such source (no parameter, not that class).</summary>
        Absent,
        /// <summary>The source exists but names no level (invalid id, or not a Level element).</summary>
        NoLevel,
        /// <summary>The source names a real level.</summary>
        Level,
        /// <summary>Reading the source threw.</summary>
        Unreadable
    }

    public struct LevelProbe
    {
        public LevelProbeState State;
        public string LevelName;
        public string Error;

        public static LevelProbe Absent() => new LevelProbe { State = LevelProbeState.Absent };
        public static LevelProbe NoLevel() => new LevelProbe { State = LevelProbeState.NoLevel };
        public static LevelProbe Found(string name) => new LevelProbe { State = LevelProbeState.Level, LevelName = name };
        public static LevelProbe Failed(string error) => new LevelProbe { State = LevelProbeState.Unreadable, Error = error };
    }

    /// <summary>The answer: a level name and the source that gave it, or neither.</summary>
    public sealed class LevelResolution
    {
        /// <summary>The level's name; null when no source named one.</summary>
        public string LevelName;
        /// <summary>The source that resolved it (one of LevelResolutionRules.Sources); null when none did.</summary>
        public string Source;
        /// <summary>How many sources threw on the way. A miss with unreadable sources is not a clean miss.</summary>
        public int UnreadableSources;

        public bool Resolved => Source != null;
    }

    public static class LevelResolutionRules
    {
        /// <summary>The label for an element whose every source was asked and none named a level.</summary>
        public const string NoLevelLabel = "(no level)";

        /// <summary>The label used in a per-source count for elements no source resolved.</summary>
        public const string NoSourceLabel = "(none)";

        public const string ElementLevelId = "Element.LevelId";
        public const string HostLevel = "FamilyInstance.Host";

        /// <summary>
        /// A railing hosted on a stair or a ramp keeps no level of its own (its Base Level reads -1 and
        /// is read-only), so no parameter names a floor and it was counted under "(no level)". Its floor
        /// is the floor of the element that carries it: the stair's base level. Reported as its own
        /// source so a count per source shows how many levels were inherited from a host.
        /// </summary>
        public const string RailingHostElement = "Railing.HostElement";

        /// <summary>
        /// The order sources are asked in. Element.LevelId first: it is Revit's own
        /// consolidated answer and right for most categories (walls, floors, rooms and
        /// other spatial elements, doors). Then the parameters a category keeps its
        /// level in when LevelId is invalid, base/start/reference before schedule
        /// levels - "the level of a beam" means its Reference Level, which is also
        /// what Revit's schedules mean by it. A host that IS a level (a beam or a
        /// level-hosted family placed on it) comes after the parameters, and
        /// SCHEDULE_LEVEL_PARAM last because it is a reporting level, not a placement.
        ///
        /// Base before top throughout: the level of a wall, a stair or a column is
        /// where it stands.
        /// </summary>
        public static readonly IReadOnlyList<string> Sources = new[]
        {
            ElementLevelId,
            "LEVEL_PARAM",
            "WALL_BASE_CONSTRAINT",
            "FAMILY_BASE_LEVEL_PARAM",
            "INSTANCE_REFERENCE_LEVEL_PARAM",
            "RBS_START_LEVEL_PARAM",
            "ROOF_BASE_LEVEL_PARAM",
            "ROOF_CONSTRAINT_LEVEL_PARAM",
            "STAIRS_BASE_LEVEL_PARAM",
            "ROOM_LEVEL_ID",
            "FAMILY_LEVEL_PARAM",
            HostLevel,
            RailingHostElement,
            "INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM",
            "SCHEDULE_LEVEL_PARAM"
        };

        /// <summary>
        /// Asks each source in order and stops at the FIRST that names a real level.
        /// Absent, empty and non-level sources are passed over; a source that threw is
        /// counted and passed over too - it told us nothing, and the next source may
        /// still know. `probe` is the Revit half: it reads one source and reports what
        /// it found, deciding nothing.
        /// </summary>
        public static LevelResolution Resolve(Func<string, LevelProbe> probe)
        {
            return Resolve(Sources, probe);
        }

        public static LevelResolution Resolve(IEnumerable<string> order, Func<string, LevelProbe> probe)
        {
            if (order == null) throw new ArgumentNullException(nameof(order));
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            var result = new LevelResolution();
            foreach (string source in order)
            {
                LevelProbe p;
                try { p = probe(source); }
                catch (Exception ex) { p = LevelProbe.Failed(ex.Message); }
                if (p.State == LevelProbeState.Unreadable) { result.UnreadableSources++; continue; }
                if (p.State != LevelProbeState.Level) continue;
                // A level with no readable name is still a level; it is not "(no level)".
                result.LevelName = string.IsNullOrEmpty(p.LevelName) ? "(unnamed level)" : p.LevelName;
                result.Source = source;
                return result;
            }
            return result;
        }

        /// <summary>The key a per-source count uses for this resolution.</summary>
        public static string SourceKey(LevelResolution r) => r == null || r.Source == null ? NoSourceLabel : r.Source;
    }
}
