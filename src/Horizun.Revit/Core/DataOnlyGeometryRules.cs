// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// SpatialAfterWrite.cs skips the spatial coherence check for "data-only" tools
// (horizun_write_params_verified, horizun_family_apply (Pro edition), ...) because Revit's
// DocumentChanged cannot tell a moved element from a renamed one - a parameter
// write to ten thousand elements would otherwise pay a full spatial pass for
// every rename. But a parameter write CAN move geometry: an offset, a base
// height, a type swap that changes a family's footprint. Skipping outright
// misses exactly the write most likely to introduce the field defect this
// check exists for (a column and a door left in the same place).
//
// The dispatcher cannot snapshot "before" for ids it does not know about until
// the command has already run - so this is reactive: BBoxCache.cs (Revit-
// touching) remembers the last bounding box Attach saw for an element, across
// calls, and this file (pure, no Revit types) decides what that comparison
// means:
//
//   * a modified element WITH a remembered previous box: compare boxes. Moved
//     beyond the tolerance -> always checked. This is the precise half - no
//     false positives from an element that only had a parameter renamed.
//   * a modified element with NO remembered box (first time this process has
//     seen it, e.g. right after Revit started): unknown. Included WHOLE, never
//     partially, only when there are at most UnknownFallbackCap of them across
//     the call - the cheapest honest design: bounded cost, and the scope note
//     says exactly how many elements were, or were not, looked at and why.
//
// This is a documented LIMIT, not a guarantee: a data-only write to more than
// UnknownFallbackCap never-before-seen elements is not spatially checked at
// all. horizun_verify_changes (scope=last_write or session) remains the way to
// look at those elements after the fact once they are in the ledger.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class DataOnlyGeometryRules
    {
        /// <summary>Below ~3 mm on every axis is regen jitter, not a move.</summary>
        public const double MoveToleranceFt = 0.01;

        /// <summary>
        /// Above this many modified elements with no remembered previous position, a
        /// data-only write's spatial check is skipped entirely rather than run partially -
        /// "checked" must always mean every candidate was looked at.
        /// </summary>
        public const int UnknownFallbackCap = 200;

        public sealed class Candidate
        {
            public long Id;
            public bool Physical;
            /// <summary>The bounding box (minX,minY,minZ,maxX,maxY,maxZ) the last time ANY call
            /// recorded one for this id, or null the first time this id is seen this session.</summary>
            public double[] Previous;
            /// <summary>The bounding box now, or null when the element has none (not physical,
            /// unreadable, or deleted between the write and this check).</summary>
            public double[] Current;
        }

        public sealed class Outcome
        {
            public readonly List<long> Subjects = new List<long>();
            public int Moved, UnknownIncluded, UnknownSkipped, NotPhysical, NoCurrentBox;
            /// <summary>Null when nothing needs explaining; set whenever the fallback cap changed
            /// what got checked, so the caller never has to infer scope from silence.</summary>
            public string ScopeNote;
        }

        /// <summary>True when two recorded boxes differ by more than the tolerance on any axis.
        /// A box with the wrong shape (not exactly 6 numbers) or either side missing is never
        /// reported as "moved" by this function - the caller decides what "no evidence" means.</summary>
        public static bool Moved(double[] previous, double[] current)
        {
            if (previous == null || current == null || previous.Length != 6 || current.Length != 6) return false;
            for (int i = 0; i < 6; i++)
                if (Math.Abs(previous[i] - current[i]) > MoveToleranceFt) return true;
            return false;
        }

        /// <summary>Which of a data-only write's modified elements the spatial check should look
        /// at, and why. See the file header for the two-tier rule.</summary>
        public static Outcome Decide(IEnumerable<Candidate> candidates)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            var o = new Outcome();
            var unknownPhysical = new List<Candidate>();
            foreach (Candidate c in candidates)
            {
                if (!c.Physical) { o.NotPhysical++; continue; }
                if (c.Current == null) { o.NoCurrentBox++; continue; }
                if (c.Previous == null) { unknownPhysical.Add(c); continue; }
                if (Moved(c.Previous, c.Current)) { o.Subjects.Add(c.Id); o.Moved++; }
            }
            if (unknownPhysical.Count == 0) return o;
            if (unknownPhysical.Count <= UnknownFallbackCap)
            {
                foreach (Candidate c in unknownPhysical) o.Subjects.Add(c.Id);
                o.UnknownIncluded = unknownPhysical.Count;
                o.ScopeNote = o.UnknownIncluded + " of the modified elements had no earlier recorded position (first seen this Revit session) and " +
                              "were checked anyway, at or under the " + UnknownFallbackCap + "-element fallback cap.";
            }
            else
            {
                o.UnknownSkipped = unknownPhysical.Count;
                o.ScopeNote = o.UnknownSkipped + " of the modified elements had no earlier recorded position and exceed the " + UnknownFallbackCap +
                              "-element fallback cap: the spatial check was skipped for this data-only write. Call horizun_verify_changes (scope=last_write) " +
                              "to check them explicitly.";
            }
            return o;
        }
    }
}
