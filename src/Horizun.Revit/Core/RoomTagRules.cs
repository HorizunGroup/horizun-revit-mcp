using System;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    public static class RoomTagRules
    {
        public static bool IsDefaultFamilyName(string fileName)
        {
            if (!string.Equals(Path.GetExtension(fileName), ".rfa", StringComparison.OrdinalIgnoreCase)) return false;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            return string.Equals(stem, "M_Room Tag", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, "Room Tag", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ContainsFields(string text, string number, string name, string area)
        {
            string observed = Normalize(text);
            return !string.IsNullOrEmpty(Normalize(number)) && !string.IsNullOrEmpty(Normalize(name)) &&
                !string.IsNullOrEmpty(Normalize(area)) &&
                Regex.IsMatch(text ?? "", @"(?<![\p{L}\p{N}])" + Regex.Escape(number.Trim()) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase) &&
                observed.IndexOf(Normalize(name), StringComparison.OrdinalIgnoreCase) >= 0 &&
                observed.IndexOf(Normalize(area), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool ContainsConcatenatedRoomFields(string text, string number, string name, double areaM2)
        {
            if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(name) ||
                double.IsNaN(areaM2) || areaM2 <= 0) return false;
            // Revit's RoomTag.TagText concatenates adjacent family Labels without
            // spaces. The Spanish architecture template renders Name + Number +
            // Area, for example "Habitación 1127.596 m²" for name "Habitación 1",
            // number "1" and area 27.596 m². Require that exact field order so
            // the final "1" cannot be borrowed from the name or the area.
            string observed = Normalize(text);
            string prefix = Normalize(name) + Normalize(number);
            if (!observed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string suffix = observed.Substring(prefix.Length);
            Match match = Regex.Match(suffix, @"^(?<value>\d+(?:[.,]\d+)?)m(?:²|2)$", RegexOptions.IgnoreCase);
            if (!match.Success || !double.TryParse(match.Groups["value"].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double shown)) return false;
            int decimals = match.Groups["value"].Value.IndexOfAny(new[] { '.', ',' });
            int places = decimals < 0 ? 0 : match.Groups["value"].Value.Length - decimals - 1;
            double roundingTolerance = 0.5 * Math.Pow(10, -places);
            return Math.Abs(shown - areaM2) <= Math.Max(0.02, roundingTolerance + 0.001);
        }

        /// <summary>
        /// A RoomTag without a leader must have its head INSIDE its room: Revit refuses an outside head with a raw exception
        /// ("Head position of the tag with no leader could not be located outside of the host element"). <paramref name="inside"/>
        /// is Room.IsPointInRoom for the requested point (null when it could not be asked: then Revit decides, as before).
        /// </summary>
        public static string OutsideRoomRefusal(bool? inside, bool addLeader, long roomId)
            => inside == false && !addLeader
                ? "point is outside room " + roomId + ": a room tag without a leader must sit inside its room. Give a point inside the room, or send add_leader: true to put the head outside. Nothing was written."
                : null;

        /// <summary>
        /// The height, in feet, at which a point is asked whether it is inside a room: the room's base
        /// (level ProjectElevation + base offset, never Level.Elevation, which moves with the project's elevation base) plus
        /// the smaller of one foot and half the room's height. The height is upper level + limit offset minus the base, or
        /// <paramref name="unboundedHeightFeet"/> when the room has no upper limit. Null when no positive height is known:
        /// the point cannot be asked and Revit decides.
        /// </summary>
        public static double? ProbeZFeet(double levelProjectElevationFeet, double baseOffsetFeet, double? upperLevelProjectElevationFeet,
                                         double limitOffsetFeet, double? unboundedHeightFeet)
        {
            double baseZ = levelProjectElevationFeet + baseOffsetFeet;
            double? height = upperLevelProjectElevationFeet.HasValue ? upperLevelProjectElevationFeet.Value + limitOffsetFeet - baseZ : unboundedHeightFeet;
            if (!height.HasValue || double.IsNaN(height.Value) || double.IsInfinity(height.Value) || height.Value <= 1e-6) return null;
            return baseZ + Math.Min(1.0, height.Value / 2.0);
        }

        private static readonly Regex AreaLike = new Regex(@"\d[\d.,]*\s*(?:m²|m2|m\^2|sqm|sf|sq\.?\s?ft|ft²|ft2)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);

        /// <summary>The text carries something that reads as an area (a figure followed by m², m2, SF, sq ft...).</summary>
        public static bool ShowsAnArea(string text) => AreaLike.IsMatch(text ?? "");

        /// <summary>
        /// The room's name and number both appear in the text, labels joined without spaces as Revit's TagText does
        /// ("Residential Lobby106"): the name is taken out once and the number must stand alone in what is left.
        /// </summary>
        public static bool ShowsNameAndNumber(string text, string number, string name)
        {
            string n = Collapse(name), num = Collapse(number);
            if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(num)) return false;
            string observed = Collapse(text);
            int at = observed.IndexOf(n, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            string rest = observed.Remove(at, n.Length);
            return Regex.IsMatch(rest, @"(?<![\p{L}\p{N}])" + Regex.Escape(num) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);
        }

        private static string Collapse(string value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();

        /// <summary>
        /// Why a committed RoomTag's text does not prove the room, or null when it does. The type decides what is shown: a family
        /// that displays the area must show the room's area (as before), one that does not (field, Revit 2025: the default type
        /// reads "Residential Lobby106") is proven by its number and name alone - but a text that shows an area which is NOT the
        /// room's is still refused, so the relaxation never accepts a wrong figure.
        /// </summary>
        public static string TextProblem(string text, string number, string name, string area, double areaM2)
        {
            if (ContainsFields(text, number, name, area) || ContainsConcatenatedRoomFields(text, number, name, areaM2)) return null;
            bool nameAndNumber = ShowsNameAndNumber(text, number, name);
            if (nameAndNumber && !ShowsAnArea(text)) return null;
            return nameAndNumber
                ? "the RoomTag text shows an area, but not the Room's displayed area (" + (area ?? "?") + ")"
                : "the RoomTag text does not contain the Room's number and name (the area is required only when the tag type shows one)";
        }

        private static string Normalize(string value)
        {
            string compact = Regex.Replace(value ?? "", @"\s+", " ").Trim();
            // Revit's TagText joins labels with newlines; display values may insert
            // ordinary or nonbreaking spaces before area units.
            return compact.Replace(" ", "");
        }
    }
}
