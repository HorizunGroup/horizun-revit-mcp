// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ROOMS, SPACES AND AREAS - three populations, never one.
//
// A Room and an MEP Space are different elements answering different questions,
// and an Area belongs to an area SCHEME that a room does not have. Merging them
// gives a "spaces" count nobody can reconcile with anything they see in Revit.
//
// FOUR STATES THAT ONE CONDITION CANNOT EXPRESS. Every implementation of this
// check that I have seen writes `area == 0` and calls the result unplaced, or
// unbounded, or redundant, depending on which word the author had in mind.
// They are different things:
//
//   unplaced    the element exists in the schedule and sits in no view. It has
//               no Location at all.
//   not enclosed  it IS placed, and its boundary leaks. Revit gives it zero area.
//   redundant   it is placed INSIDE another one's boundary. It also has zero
//               area, and it is a different mistake with a different fix.
//   zero area   the measurement came back zero and none of the above was
//               established - which is the honest answer when the reads that
//               would tell them apart did not succeed.
//
// A read that THREW is a fifth thing and never any of the four.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class SpatialState
    {
        public const string Placed = "placed";
        public const string Unplaced = "unplaced";
        public const string NotEnclosed = "not_enclosed";
        public const string Redundant = "redundant";
        public const string ZeroArea = "zero_area";
        public const string Unreadable = "unreadable";

        public static readonly string[] All =
        {
            Placed, Unplaced, NotEnclosed, Redundant, ZeroArea, Unreadable
        };
    }

    public static class SpatialKind
    {
        public const string Room = "room";
        public const string Space = "space";
        public const string Area = "area";

        public static readonly string[] All = { Room, Space, Area };
    }

    /// <summary>One room, space or area as the model reports it.</summary>
    public sealed class SpatialFact
    {
        public long ElementId;
        public string Kind;
        public string Name;
        public string Number;
        public bool NameReadable = true;
        public bool NumberReadable = true;
        public string LevelName;
        public string Phase;
        /// <summary>Area schemes belong to AREAS only. Null for a room or a space.</summary>
        public string AreaScheme;
        /// <summary>The view an area was measured in. Null for a room or a space.</summary>
        public string ViewName;

        public double? AreaSqM;
        /// <summary>Null when the read did not succeed - never false by default.</summary>
        public bool? HasLocation;
        public bool? IsEnclosed;
        public bool? IsRedundant;
        public bool Readable = true;

        public bool NameEmpty { get { return NameReadable && string.IsNullOrWhiteSpace(Name); } }
        public bool NumberEmpty { get { return NumberReadable && string.IsNullOrWhiteSpace(Number); } }
    }

    public static class SpatialCensusRules
    {
        public const string StatesMean =
            "unplaced, not_enclosed and redundant are three different mistakes with three different fixes, and " +
            "all three show zero area. An implementation that derives them from `area == 0` reports whichever " +
            "word its author had in mind and is wrong about the other two. zero_area is the honest fallback: " +
            "the measurement is zero and nothing established which of the three it was.";

        public const string PopulationsMean =
            "rooms, MEP spaces and areas are counted apart. They are different elements answering different " +
            "questions, and an area belongs to an area SCHEME that a room does not have. A single 'spaces' " +
            "number cannot be reconciled with anything visible in Revit.";

        /// <summary>
        /// The state of one element, decided in the order the states EXCLUDE each
        /// other rather than in the order they are convenient to read.
        ///
        /// Unplaced comes first because an unplaced element has no boundary to be
        /// unenclosed by and no neighbour to be redundant inside. Redundant comes
        /// before not_enclosed because Revit reports a redundant element as
        /// unenclosed too, and answering "not enclosed" about a room that is really
        /// a duplicate sends somebody to fix the wrong thing.
        /// </summary>
        public static string StateOf(SpatialFact f)
        {
            if (f == null) return SpatialState.Unreadable;
            if (!f.Readable) return SpatialState.Unreadable;

            // NULL IS NOT FALSE. A location that could not be read has not told us
            // the element is unplaced.
            if (f.HasLocation == false) return SpatialState.Unplaced;
            if (f.HasLocation == null) return SpatialState.Unreadable;

            if (f.IsRedundant == true) return SpatialState.Redundant;
            if (f.IsEnclosed == false) return SpatialState.NotEnclosed;

            if (!f.AreaSqM.HasValue) return SpatialState.Unreadable;
            if (f.AreaSqM.Value <= 0) return SpatialState.ZeroArea;
            return SpatialState.Placed;
        }

        /// <summary>Numbers more than one element of the SAME kind carries.</summary>
        public static List<string> DuplicateNumbers(IEnumerable<SpatialFact> facts, string kind)
        {
            return (facts ?? Enumerable.Empty<SpatialFact>())
                .Where(f => f != null && f.Kind == kind && f.NumberReadable &&
                            !string.IsNullOrWhiteSpace(f.Number))
                .GroupBy(f => f.Number, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// One kind's tally. Every state is present in the reply, so a reader never
        /// has to decide whether a missing key means zero or means nobody looked.
        /// </summary>
        public static JObject Tally(IEnumerable<SpatialFact> facts, string kind)
        {
            List<SpatialFact> all = (facts ?? Enumerable.Empty<SpatialFact>())
                .Where(f => f != null && f.Kind == kind).ToList();

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string s in SpatialState.All) counts[s] = 0;
            foreach (SpatialFact f in all) counts[StateOf(f)]++;

            var o = new JObject { ["kind"] = kind, ["total"] = all.Count };
            foreach (string s in SpatialState.All) o[s] = counts[s];
            o["name_empty"] = all.Count(f => f.NameEmpty);
            o["number_empty"] = all.Count(f => f.NumberEmpty);
            o["duplicate_numbers"] = new JArray(DuplicateNumbers(all, kind).Select(x => (JToken)x));
            o["counts_are_exact"] = counts[SpatialState.Unreadable] == 0;
            return o;
        }

        public static JObject ToJson(SpatialFact f)
        {
            if (f == null) return null;
            return new JObject
            {
                ["element_id"] = f.ElementId,
                ["kind"] = f.Kind,
                ["state"] = StateOf(f),
                ["name"] = f.Name,
                ["name_readable"] = f.NameReadable,
                ["number"] = f.Number,
                ["number_readable"] = f.NumberReadable,
                ["level"] = f.LevelName,
                ["phase"] = f.Phase,
                // Present only where the concept exists. An area scheme on a room
                // would be an invented field.
                ["area_scheme"] = f.Kind == SpatialKind.Area ? f.AreaScheme : null,
                ["view"] = f.Kind == SpatialKind.Area ? f.ViewName : null,
                ["area_sq_m"] = f.AreaSqM,
                ["has_location"] = f.HasLocation,
                ["is_enclosed"] = f.IsEnclosed,
                ["is_redundant"] = f.IsRedundant
            };
        }
    }
}
