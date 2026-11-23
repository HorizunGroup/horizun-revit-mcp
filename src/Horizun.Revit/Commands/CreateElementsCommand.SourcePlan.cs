using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        internal static ResolvedPlan ResolveCreationPlan(string command, string documentKey, string revitVersion,
            string documentFingerprint, JObject tabularBlock) => new ResolvedPlan
        {
            Command = command,
            DocumentKey = documentKey,
            RevitVersion = revitVersion,
            DocumentFingerprint = documentFingerprint,
            ContextFingerprint = tabularBlock?.Value<string>("sha256")
        };

        internal static string ReadPlacementCsv(string path, JObject source, string decimalSeparator, string coordinates,
            long typeId, long levelId, double scale, Func<double> readLevelElevation,
            Func<(double Angle, double EastWest, double NorthSouth, double Elevation)?> readProjectPosition,
            out JArray elements, out JObject provenance)
        {
            elements = null;
            provenance = null;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { return "tabular_source file could not be read: " + ex.Message; }
            string sha;
            using (var hasher = SHA256.Create())
                sha = BitConverter.ToString(hasher.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            // BOM tolerated, like the parameter writer's reader: PowerShell's
            // Set-Content utf8 prepends one, and a header whose first cell reads
            // \uFEFFx would silently match no column.
            string text = new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF');
            char delimiter = TabularRules.DetectDelimiter(text);
            List<string[]> rows = TabularRules.ParseCsv(text, delimiter);
            if (rows.Count < 2) return "the file has no data rows under its header.";
            string ragged = TabularRules.RaggedRow(rows, delimiter);
            if (ragged != null) return ragged;

            string[] header = rows[0];
            int xColumn = Array.FindIndex(header, h => string.Equals(h, source.Value<string>("x_column") ?? "x", StringComparison.OrdinalIgnoreCase));
            int yColumn = Array.FindIndex(header, h => string.Equals(h, source.Value<string>("y_column") ?? "y", StringComparison.OrdinalIgnoreCase));
            int zColumn = Array.FindIndex(header, h => string.Equals(h, source.Value<string>("z_column") ?? "z", StringComparison.OrdinalIgnoreCase));
            int rotationColumn = Array.FindIndex(header, h => string.Equals(h, source.Value<string>("rotation_column") ?? "rotation", StringComparison.OrdinalIgnoreCase));
            if (xColumn < 0 || yColumn < 0)
                return "the header names no x/y columns (looked for '" + (source.Value<string>("x_column") ?? "x") +
                       "' and '" + (source.Value<string>("y_column") ?? "y") + "'; header: " + string.Join(", ", header) + ").";

            // shared -> internal: undo the active project position (rotate back, subtract offsets).
            double angle = 0, eastWest = 0, northSouth = 0, elevation = 0;
            if (coordinates == "shared")
            {
                var position = readProjectPosition();
                if (!position.HasValue) return "coordinates=shared, but the active project position could not be read.";
                (angle, eastWest, northSouth, elevation) = position.Value;
            }

            var culture = decimalSeparator == "."
                ? CultureInfo.InvariantCulture
                : CultureInfo.GetCultureInfo("es-ES");
            elements = new JArray();
            for (int r = 1; r < rows.Count; r++)
            {
                string[] row = rows[r];
                double x, y, z = 0, rotation = 0;
                if (row.Length <= Math.Max(xColumn, yColumn) ||
                    !double.TryParse(row[xColumn], NumberStyles.Float, culture, out x) ||
                    !double.TryParse(row[yColumn], NumberStyles.Float, culture, out y))
                    return "row " + (r + 1) + " (cells: " + string.Join(" | ", row) + ") has no numeric x/y under " +
                           "the declared separator '" + decimalSeparator + "'.";
                if (zColumn >= 0 && (row.Length <= zColumn ||
                    !double.TryParse(row[zColumn], NumberStyles.Float, culture, out z)))
                    return "row " + (r + 1) + " (cells: " + string.Join(" | ", row) + "): the z value is not " +
                           "numeric under the declared separator '" + decimalSeparator + "'.";
                if (rotationColumn >= 0 && row.Length > rotationColumn && row[rotationColumn].Trim().Length > 0 &&
                    !double.TryParse(row[rotationColumn], NumberStyles.Float, culture, out rotation))
                    return "row " + (r + 1) + ": the rotation value is not numeric under the declared separator.";
                if (coordinates == "shared")
                {
                    // The file speaks survey (E, N, Elev) in the REQUEST's units; the
                    // API's offsets are feet. ProjectPosition: shared = R(angle) *
                    // internal + T, so internal = R(-angle) * (shared - T) - computed
                    // entirely in the request's units (offsets divided by scale).
                    double cos = Math.Cos(-angle), sin = Math.Sin(-angle);
                    double relativeEast = x - eastWest / scale;
                    double relativeNorth = y - northSouth / scale;
                    x = relativeEast * cos - relativeNorth * sin;
                    y = relativeEast * sin + relativeNorth * cos;
                    z = z - elevation / scale;
                }
                // No Z column means placement on the explicitly selected level.
                // An explicit Z is absolute in the declared coordinate system.
                if (zColumn < 0) z = readLevelElevation() / scale;
                var entry = new JObject
                {
                    ["kind"] = "family_instance", ["coordinate_mode"] = "absolute",
                    ["type_id"] = typeId,
                    ["level_id"] = levelId,
                    ["point"] = new JArray(x, y, z),
                    ["source_row"] = r + 1
                };
                if (Math.Abs(rotation) > 1e-9) entry["rotation_degrees"] = rotation;
                elements.Add(entry);
            }
            provenance = new JObject
            {
                ["path"] = path, ["sha256"] = sha, ["data_rows"] = rows.Count - 1,
                ["coordinates"] = coordinates, ["decimal_separator"] = decimalSeparator,
                ["note"] = "each element row carries source_row; the file was expanded on THIS call, so a file " +
                           "edited between rehearsal and apply resolves a different plan and the token refuses stale."
            };
            return null;
        }
    }
}
