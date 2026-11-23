// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. No Revit.
//
// THE ISOMETRIC OF ONE ROOM (4.2-N4, CT-P2-2d): a 3D view looking at the room from one of its four
// corners, with the section box fitted to the room plus a margin so only the room and its enclosing
// walls, floor and ceiling show. The arithmetic is here: the four directions, the box, the name.
//
//   direction = where the viewer stands: ne, nw, se, sw. A true isometric looks down along
//   (-sx, -sy, -1)/sqrt(3), with up = (-sx, -sy, 2)/sqrt(6); the two are perpendicular.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RoomIsometricRules
    {
        public static readonly string[] Directions = { "ne", "nw", "se", "sw" };
        public const string DefaultDirection = "se";
        public const double DefaultMarginMm = 300;

        private static void Signs(string direction, out double sx, out double sy)
        {
            string d = (direction ?? DefaultDirection).Trim().ToLowerInvariant();
            if (!Directions.Contains(d)) throw new ArgumentException("direction must be one of " + string.Join(", ", Directions) + " (the corner the viewer stands in).");
            sx = d[1] == 'e' ? 1 : -1;
            sy = d[0] == 'n' ? 1 : -1;
        }

        public static double[] Forward(string direction)
        {
            Signs(direction, out double sx, out double sy);
            double k = 1 / Math.Sqrt(3);
            return new[] { -sx * k, -sy * k, -k };
        }

        public static double[] Up(string direction)
        {
            Signs(direction, out double sx, out double sy);
            double k = 1 / Math.Sqrt(6);
            return new[] { -sx * k, -sy * k, 2 * k };
        }

        /// <summary>The room's bounding box grown by the margin on every side. Min/max in the same unit as the margin.</summary>
        public static double[][] Box(double[] min, double[] max, double margin)
        {
            if (min == null || max == null || min.Length != 3 || max.Length != 3) throw new ArgumentException("the room has no readable bounding box.");
            if (margin < 0 || double.IsNaN(margin)) throw new ArgumentException("margin must be 0 or more.");
            for (int i = 0; i < 3; i++)
                if (!(max[i] > min[i])) throw new ArgumentException("the room's bounding box has no extent on axis " + "XYZ"[i] + ": it is not a placed, enclosed room.");
            return new[]
            {
                new[] { min[0] - margin, min[1] - margin, min[2] - margin },
                new[] { max[0] + margin, max[1] + margin, max[2] + margin }
            };
        }

        /// <summary>True when outer holds inner on all three axes within the tolerance.</summary>
        public static bool Contains(double[][] outer, double[][] inner, double tolerance)
        {
            for (int i = 0; i < 3; i++)
                if (outer[0][i] > inner[0][i] + tolerance || outer[1][i] < inner[1][i] - tolerance) return false;
            return true;
        }

        /// <summary>The view name when none is given: ISO number name, characters Revit refuses replaced.</summary>
        public static string DefaultName(string roomNumber, string roomName)
        {
            string raw = ("ISO " + (roomNumber ?? "").Trim() + " " + (roomName ?? "").Trim()).Trim();
            foreach (char bad in new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\' }) raw = raw.Replace(bad, '-');
            return raw.Length > 120 ? raw.Substring(0, 120) : raw;
        }

        /// <summary>Null when the action is well formed (room_id, optional direction, margin, name).</summary>
        public static string Validate(JObject a)
        {
            if (a["room_id"]?.Type != JTokenType.Integer) return "create_room_isometric needs room_id.";
            if (a["direction"] != null && a["direction"].Type != JTokenType.Null)
            {
                if (a["direction"].Type != JTokenType.String || !Directions.Contains(a.Value<string>("direction").Trim().ToLowerInvariant()))
                    return "direction must be one of " + string.Join(", ", Directions) + ".";
            }
            if (a["margin"] != null && ((a["margin"].Type != JTokenType.Integer && a["margin"].Type != JTokenType.Float) || a.Value<double>("margin") < 0))
                return "margin must be 0 or more.";
            foreach (string notHere in new[] { "start", "end", "up" })
                if (a[notHere] != null) return notHere + " does not apply to create_room_isometric: the camera comes from direction.";
            return null;
        }
    }
}
