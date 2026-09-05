// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// SCOPE BOXES, and why this is a reading rather than a heuristic.
//
// A scope box is a real element in OST_VolumeOfInterest, and
// Element.get_BoundingBox returns ITS OWN extents - not those of anything near
// it. That distinction is the whole reason this file exists: substituting the
// bounding box of the elements a scope box happens to contain would be a guess
// that looks like a measurement, and it would be wrong exactly where the answer
// matters, on a scope box that crops more than it holds.
//
// FOUR STATES, because "no scope box" is three different situations:
//
//   not_assigned      the datum or view carries no scope box. A decision.
//   assigned          it carries one, and the reply names it.
//   unreadable        the assignment read threw. Not "none".
//   geometry_absent   the scope box IS assigned and named, and its extents
//                     would not come back. The assignment is still a fact.
//
// Verified present in Revit 2023 through 2027 by reflection over each
// RevitAPI.dll: DATUM_VOLUME_OF_INTEREST, VIEWER_VOLUME_OF_INTEREST_CROP,
// VOLUME_OF_INTEREST_NAME and OST_VolumeOfInterest all exist in every one.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ScopeBoxState
    {
        public const string NotAssigned = "not_assigned";
        public const string Assigned = "assigned";
        public const string Unreadable = "unreadable";
        public const string GeometryAbsent = "geometry_absent";

        public static readonly string[] All = { NotAssigned, Assigned, Unreadable, GeometryAbsent };
    }

    /// <summary>One scope box as the model reports it, with its OWN extents.</summary>
    public sealed class ScopeBoxFact
    {
        public long ElementId;
        public string Name;
        public bool NameReadable = true;
        public double? MinXMm, MinYMm, MinZMm;
        public double? MaxXMm, MaxYMm, MaxZMm;
        /// <summary>False when the scope box would not report its own bounding box.</summary>
        public bool GeometryReadable = true;

        public double? WidthMm { get { return Span(MinXMm, MaxXMm); } }
        public double? DepthMm { get { return Span(MinYMm, MaxYMm); } }
        public double? HeightMm { get { return Span(MinZMm, MaxZMm); } }

        private static double? Span(double? a, double? b)
        {
            if (!a.HasValue || !b.HasValue) return null;
            return Math.Abs(b.Value - a.Value);
        }
    }

    /// <summary>What one datum or view says about its scope box.</summary>
    public sealed class ScopeBoxAssignment
    {
        public long OwnerId;
        public string OwnerKind;
        public string ScopeBoxName;
        public long? ScopeBoxId;
        public bool Readable = true;
        /// <summary>True when the named scope box could not report its extents.</summary>
        public bool GeometryMissing;

        public string State
        {
            get
            {
                if (!Readable) return ScopeBoxState.Unreadable;
                if (string.IsNullOrWhiteSpace(ScopeBoxName)) return ScopeBoxState.NotAssigned;
                return GeometryMissing ? ScopeBoxState.GeometryAbsent : ScopeBoxState.Assigned;
            }
        }
    }

    public static class ScopeBoxRules
    {
        public const string GeometryMeans =
            "the extents below are the scope box's OWN bounding box, read from the scope box element itself. " +
            "They are not derived from the elements it contains or crops: that substitution would be a guess " +
            "shaped like a measurement, and it would be wrong exactly where the answer matters, on a box that " +
            "crops more than it holds.";

        public const string StatesMean =
            "not_assigned is a decision somebody made; unreadable is a read that threw; and geometry_absent " +
            "means the scope box IS assigned and named while its extents would not come back - the assignment " +
            "stays a fact even when the geometry does not.";

        /// <summary>
        /// Datums sharing a scope box, largest group first. Two grids on one box is
        /// ordinary; a box used by one grid out of forty is worth a look, and
        /// neither is a finding without a rule.
        /// </summary>
        public static List<KeyValuePair<string, long>> ByScopeBox(IEnumerable<ScopeBoxAssignment> assignments)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (ScopeBoxAssignment a in assignments ?? Enumerable.Empty<ScopeBoxAssignment>())
            {
                if (a == null || a.State == ScopeBoxState.NotAssigned || a.State == ScopeBoxState.Unreadable)
                    continue;
                string k = a.ScopeBoxName ?? "(unnamed)";
                long had;
                counts[k] = counts.TryGetValue(k, out had) ? had + 1 : 1;
            }
            return GroupOptionRules.Ranked(counts);
        }

        public static JObject Tally(IEnumerable<ScopeBoxAssignment> assignments,
                                    IEnumerable<ScopeBoxFact> boxes)
        {
            List<ScopeBoxAssignment> all =
                (assignments ?? Enumerable.Empty<ScopeBoxAssignment>()).Where(a => a != null).ToList();
            List<ScopeBoxFact> b = (boxes ?? Enumerable.Empty<ScopeBoxFact>()).Where(x => x != null).ToList();

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string s in ScopeBoxState.All) counts[s] = 0;
            foreach (ScopeBoxAssignment a in all) counts[a.State]++;

            var shared = new JArray();
            foreach (KeyValuePair<string, long> kv in ByScopeBox(all))
                shared.Add(new JObject { ["scope_box"] = kv.Key, ["owners"] = kv.Value });

            var o = new JObject
            {
                ["scope_boxes"] = b.Count,
                ["scope_boxes_without_geometry"] = b.Count(x => !x.GeometryReadable),
                ["assignments_examined"] = all.Count,
                ["owners_by_scope_box"] = shared,
                ["counts_are_exact"] = counts[ScopeBoxState.Unreadable] == 0,
                ["geometry_means"] = GeometryMeans,
                ["states_mean"] = StatesMean
            };
            foreach (string s in ScopeBoxState.All) o[s] = counts[s];
            return o;
        }

        public static JObject ToJson(ScopeBoxFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["scope_box_id"] = f.ElementId,
                ["name"] = f.Name,
                ["name_readable"] = f.NameReadable,
                ["geometry_readable"] = f.GeometryReadable,
                ["min_x_mm"] = f.MinXMm,
                ["min_y_mm"] = f.MinYMm,
                ["min_z_mm"] = f.MinZMm,
                ["max_x_mm"] = f.MaxXMm,
                ["max_y_mm"] = f.MaxYMm,
                ["max_z_mm"] = f.MaxZMm,
                ["width_mm"] = f.WidthMm,
                ["depth_mm"] = f.DepthMm,
                ["height_mm"] = f.HeightMm
            };
        }

        public static JObject ToJson(ScopeBoxAssignment a)
        {
            if (a == null) return null;
            return new JObject
            {
                ["owner_id"] = a.OwnerId,
                ["owner_kind"] = a.OwnerKind,
                ["state"] = a.State,
                ["scope_box"] = a.ScopeBoxName,
                ["scope_box_id"] = a.ScopeBoxId
            };
        }
    }
}
