// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ROOM-DRIVEN VIEW PRODUCTION, decided as arithmetic.
//
// "One elevation set, two sections and a cropped plan per apartment" is the
// sentence; the decisions hiding in it are the ones this file owns:
//
//   * WHICH ROOMS QUALIFY. A room that is not placed has no location to put a
//     marker at; a room that is not enclosed (or is redundant) has no area and
//     no boundary to orient to. Both are excluded WITH A CODE, because a room
//     silently skipped is a missing apartment on somebody's deliverable list.
//
//   * WHICH WAY THE ROOM FACES. The principal direction is the direction of the
//     longest boundary segment - measured, not assumed - folded onto the 90-
//     degree symmetry of a four-way elevation marker, so the rotation applied
//     is always the SMALLEST turn that lines the marker up with the walls.
//
//   * WHAT EVERYTHING IS CALLED. Names come from a caller-supplied pattern with
//     named tokens; an unknown token refuses rather than passing "{floor}"
//     through as literal text onto forty sheets. A name that already exists in
//     the document is a collision with a code, never a silent overwrite and
//     never an invented suffix.
//
// The Revit halves (reading rooms, boundaries, bounding boxes; building the
// manage_views actions) live in PlanViewsCommand. Everything here is proved
// without a model.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>One room as the command measured it. Plain facts; no Revit types.</summary>
    public sealed class RoomFacts
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Number { get; set; }
        public string LevelName { get; set; }

        public bool HasLocation { get; set; }
        public double AreaSquareFeet { get; set; }

        /// <summary>Direction of the longest boundary segment, in plan; null when unreadable.</summary>
        public double? LongestSegmentDx { get; set; }
        public double? LongestSegmentDy { get; set; }

        /// <summary>Axis-aligned model-space bounding box, internal feet; null components mean unreadable.</summary>
        public double[] BoundingBoxMin { get; set; }
        public double[] BoundingBoxMax { get; set; }
    }

    public static class RoomViewRules
    {
        // ---- what can be produced per room, closed --------------------------------

        public const string KindElevations = "elevations";
        public const string KindSections = "sections";
        public const string KindPlan = "plan";

        public static readonly IReadOnlyList<string> KnownKinds = new[]
        {
            KindElevations, KindSections, KindPlan
        };

        // ---- exclusion codes ------------------------------------------------------

        public const string CodeNotPlaced = "room_not_placed";
        public const string CodeNotEnclosed = "room_not_enclosed_or_redundant";
        public const string CodeNoBoundary = "room_boundary_unreadable";
        public const string CodeNoBoundingBox = "room_bounding_box_unreadable";
        public const string CodeNameCollision = "view_name_collision";

        // ---- eligibility ----------------------------------------------------------

        /// <summary>
        /// Whether a room can drive view production, and if not, why - as a code. The
        /// order is deliberate: "not placed" outranks "not enclosed", because a room
        /// with no location has no boundary either and the actionable fact is the
        /// placement. Revit reports an unenclosed room and a redundant one identically
        /// (a location and zero area), so the code names both rather than pretending
        /// to a distinction the API does not surface.
        /// </summary>
        public static string Eligibility(RoomFacts room)
        {
            if (room == null) return CodeNotPlaced;
            if (!room.HasLocation) return CodeNotPlaced;
            if (room.AreaSquareFeet <= 0) return CodeNotEnclosed;
            return null;
        }

        public static string EligibilityMessage(RoomFacts room, string code)
        {
            string who = Describe(room);
            switch (code)
            {
                case CodeNotPlaced:
                    return who + " is not placed: it has no location point, so there is nowhere to stand a " +
                           "marker or aim a section. Place the room, or remove it from the program.";
                case CodeNotEnclosed:
                    return who + " has a location but no area, which is Revit's way of saying it is not " +
                           "enclosed or is redundant with another room. Its boundary cannot orient anything. " +
                           "Fix the enclosure (or delete the redundant room) and re-plan.";
                case CodeNoBoundary:
                    return who + " is placed and enclosed but its boundary segments could not be read, so its " +
                           "principal direction is unknown. Views without an orientation would be a guess.";
                case CodeNoBoundingBox:
                    return who + " has no readable bounding box, so crops, section extents and depths cannot " +
                           "be derived from it.";
                default:
                    return who + ": " + code;
            }
        }

        // ---- orientation ----------------------------------------------------------

        /// <summary>
        /// The rotation, in degrees, that lines a four-way elevation marker up with the
        /// room's principal wall. The marker has 90-degree symmetry, so the angle of
        /// the longest boundary segment is folded onto (-45, 45]: the SMALLEST turn
        /// that achieves the alignment, and a room whose walls are axis-aligned gets
        /// exactly zero rather than 90 or 180.
        ///
        /// Null when the room carries no usable direction - the caller then either
        /// omits the room (orient_to_walls) or falls back to axis-aligned (cardinal),
        /// and either way it says which it did.
        /// </summary>
        public static double? PrincipalRotationDegrees(RoomFacts room)
        {
            if (room?.LongestSegmentDx == null || room.LongestSegmentDy == null) return null;
            double dx = room.LongestSegmentDx.Value, dy = room.LongestSegmentDy.Value;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (double.IsNaN(length) || length < 1e-9) return null;
            double degrees = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            // Fold onto (-45, 45]: the marker cannot tell 0 from 90 from 180 from 270.
            while (degrees > 45.0) degrees -= 90.0;
            while (degrees <= -45.0) degrees += 90.0;
            return degrees;
        }

        // ---- naming ---------------------------------------------------------------

        /// <summary>The tokens a name pattern may use. Anything else refuses by name.</summary>
        public static readonly IReadOnlyList<string> KnownTokens = new[]
        {
            "room_name", "room_number", "level", "kind", "index"
        };

        /// <summary>
        /// Expand one name pattern. An UNKNOWN token is an error, not literal text: a
        /// typo like {floor} silently passing through would name forty views wrong
        /// before anybody noticed, and the forty renames are the expensive half.
        /// </summary>
        public static string ExpandPattern(string pattern, RoomFacts room, string kind, int index,
                                            out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                error = "name_pattern must not be empty.";
                return null;
            }
            var sb = new StringBuilder();
            int i = 0;
            while (i < pattern.Length)
            {
                char c = pattern[i];
                if (c != '{') { sb.Append(c); i++; continue; }
                int close = pattern.IndexOf('}', i + 1);
                if (close < 0)
                {
                    error = "name_pattern has an unclosed '{' at position " + i + ".";
                    return null;
                }
                string token = pattern.Substring(i + 1, close - i - 1);
                switch (token)
                {
                    case "room_name": sb.Append(room?.Name ?? ""); break;
                    case "room_number": sb.Append(room?.Number ?? ""); break;
                    case "level": sb.Append(room?.LevelName ?? ""); break;
                    case "kind": sb.Append(kind ?? ""); break;
                    case "index": sb.Append(index.ToString(CultureInfo.InvariantCulture)); break;
                    default:
                        error = "name_pattern token '{" + token + "}' is not one this planner understands. " +
                                "Known tokens: " + string.Join(", ", KnownTokens.Select(t => "{" + t + "}")) + ".";
                        return null;
                }
                i = close + 1;
            }
            string result = sb.ToString().Trim();
            if (result.Length == 0)
            {
                error = "name_pattern expanded to an empty name for " + Describe(room) + " - the tokens it " +
                        "uses are all empty on this room.";
                return null;
            }
            return result;
        }

        /// <summary>
        /// Validate the pattern ONCE, against a fully-populated dummy, before any room
        /// is processed - a bad token must refuse the request, not skip half the rooms.
        /// </summary>
        public static string ValidatePattern(string pattern)
        {
            string error;
            ExpandPattern(pattern, new RoomFacts { Name = "n", Number = "1", LevelName = "l" },
                          "kind", 1, out error);
            return error;
        }

        // ---- kinds ----------------------------------------------------------------

        public static string ValidateKinds(IEnumerable<string> requested, out List<string> kinds)
        {
            kinds = new List<string>();
            if (requested == null)
            {
                kinds.AddRange(KnownKinds);
                return null;
            }
            foreach (string raw in requested)
            {
                string k = (raw ?? "").Trim().ToLowerInvariant();
                if (!KnownKinds.Contains(k))
                    return "kind '" + raw + "' is not one this planner produces. Known kinds: " +
                           string.Join(", ", KnownKinds) + ".";
                if (!kinds.Contains(k)) kinds.Add(k);
            }
            if (kinds.Count == 0)
                return "kinds, when present, must name at least one of: " + string.Join(", ", KnownKinds) + ".";
            return null;
        }

        public static string ValidateElevationCount(int count)
        {
            if (count >= 1 && count <= 4) return null;
            return "elevation_count must be 1..4: a Revit elevation marker holds four views and a room " +
                   "cannot want zero of them from a planner asked for elevations.";
        }

        // ---- geometry helpers ------------------------------------------------------

        /// <summary>
        /// The centre of the room's bounding box - where markers stand and sections
        /// cross. Null when the box is unreadable, and the caller must then exclude
        /// the room rather than default to the model origin.
        /// </summary>
        public static double[] Center(RoomFacts room)
        {
            if (room?.BoundingBoxMin == null || room.BoundingBoxMax == null ||
                room.BoundingBoxMin.Length != 3 || room.BoundingBoxMax.Length != 3) return null;
            var result = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double lo = room.BoundingBoxMin[i], hi = room.BoundingBoxMax[i];
                if (double.IsNaN(lo) || double.IsNaN(hi) || hi < lo) return null;
                result[i] = (lo + hi) / 2.0;
            }
            return result;
        }

        /// <summary>Half-extent of the box along a unit direction in plan, plus margin. For section run lengths.</summary>
        public static double HalfExtentAlong(RoomFacts room, double dirX, double dirY, double marginFeet)
        {
            double[] min = room.BoundingBoxMin, max = room.BoundingBoxMax;
            double hx = (max[0] - min[0]) / 2.0, hy = (max[1] - min[1]) / 2.0;
            // Support function of an axis-aligned box: exact, not an estimate.
            return Math.Abs(dirX) * hx + Math.Abs(dirY) * hy + marginFeet;
        }

        // ---- coverage --------------------------------------------------------------

        /// <summary>Same verdict scheme as AutoDimensionRules.Coverage - shared spelling, shared meaning.</summary>
        public static string Coverage(int rooms, int planned, int excluded)
        {
            if (rooms == 0) return "nothing_found";
            if (excluded == 0 && planned > 0) return "complete";
            if (planned == 0) return "none";
            return "partial";
        }

        public static string Describe(RoomFacts room)
        {
            if (room == null) return "(no room)";
            string label = string.IsNullOrWhiteSpace(room.Number) ? "" : room.Number + " ";
            label += string.IsNullOrWhiteSpace(room.Name) ? "" : room.Name;
            label = label.Trim();
            return label.Length == 0
                ? "room " + room.Id.ToString(CultureInfo.InvariantCulture)
                : "room " + room.Id.ToString(CultureInfo.InvariantCulture) + " ('" + label + "')";
        }
    }
}
