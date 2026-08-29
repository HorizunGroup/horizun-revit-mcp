// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Export presets without Revit: a NAMED, HASHED bundle of options the caller
// supplies as an argument - never compiled in for any organisation. The rules
// here decide three things a wrong export would otherwise hide:
//
//   * WHICH options exist per format, so a typo refuses instead of silently
//     exporting defaults under a preset's name;
//   * the CANONICAL HASH, so the token binds the exact options approved and
//     an edited preset refuses as a different plan;
//   * WHICH options are VERIFIABLE from the produced file afterwards - and
//     the ones that are not are declared requested_unverifiable, because an
//     option nobody re-read is a hope, not a fact.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Horizun.Revit.Core
{
    public sealed class ExportPreset
    {
        public string Name, Format;
        public int SchemaVersion;
        public string OverwritePolicy;   // refuse | replace
        public readonly SortedDictionary<string, string> Options =
            new SortedDictionary<string, string>(StringComparer.Ordinal);
    }

    public static class ExportPresetRules
    {
        public const string PolicyRefuse = "refuse";
        public const string PolicyReplace = "replace";

        /// <summary>format -> option name -> whether the produced file can prove it.</summary>
        private static readonly Dictionary<string, Dictionary<string, bool>> Known =
            new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal)
            {
                ["ifc"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    { "ifc_version", true }    // FILE_SCHEMA is readable in the file header
                },
                ["dwg"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    { "acad_version", true }   // the 6-byte DWG signature names it
                },
                ["pdf"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    { "combine", true }        // one file vs one per view is countable
                },
                ["image"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    { "pixel_size", true }     // the PNG IHDR carries the width
                },
                ["nwc"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    { "convert_element_properties", false }
                },
                ["fbx"] = new Dictionary<string, bool>(StringComparer.Ordinal),
                ["schedule_csv"] = new Dictionary<string, bool>(StringComparer.Ordinal)
            };

        private static readonly Dictionary<string, string[]> AllowedValues =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "ifc_version", new[] { "IFC2x3", "IFC4" } },
                { "acad_version", new[] { "2013", "2018" } },
                { "combine", new[] { "true", "false" } },
                { "convert_element_properties", new[] { "true", "false" } }
            };

        /// <summary>
        /// Parse and validate. Returns null and a reason on refusal - an unknown
        /// format, an option the format does not carry, or a value outside the
        /// closed list. pixel_size is a bounded integer.
        /// </summary>
        public static ExportPreset Parse(string name, string format, int schemaVersion, string overwritePolicy,
                                         IEnumerable<KeyValuePair<string, string>> options, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(name)) { reason = "preset.name is required."; return null; }
            if (schemaVersion != 1) { reason = "preset.schema_version must be 1 (the only published schema)."; return null; }
            string policy = string.IsNullOrWhiteSpace(overwritePolicy) ? PolicyRefuse : overwritePolicy.ToLowerInvariant();
            if (policy != PolicyRefuse && policy != PolicyReplace)
            { reason = "preset.overwrite_policy must be refuse or replace."; return null; }
            Dictionary<string, bool> known;
            if (format == null || !Known.TryGetValue(format, out known))
            { reason = "preset.format '" + format + "' is not one this surface exports. Known: " +
                       string.Join(", ", Known.Keys) + "."; return null; }

            var preset = new ExportPreset
            { Name = name.Trim(), Format = format, SchemaVersion = schemaVersion, OverwritePolicy = policy };
            foreach (KeyValuePair<string, string> option in options ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (!known.ContainsKey(option.Key))
                {
                    reason = "preset option '" + option.Key + "' does not exist for format " + format +
                             ". Known: " + (known.Count == 0 ? "(none)" : string.Join(", ", known.Keys)) +
                             ". A misspelled option silently ignored would export defaults under this preset's name.";
                    return null;
                }
                string value = option.Value ?? "";
                string[] allowed;
                if (AllowedValues.TryGetValue(option.Key, out allowed) &&
                    !allowed.Contains(value, StringComparer.Ordinal))
                {
                    reason = "preset option '" + option.Key + "' value '" + value + "' is not in the closed list: " +
                             string.Join(", ", allowed) + ".";
                    return null;
                }
                if (option.Key == "pixel_size")
                {
                    int pixels;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pixels) ||
                        pixels < 64 || pixels > 8192)
                    { reason = "preset option pixel_size must be an integer 64..8192."; return null; }
                }
                preset.Options[option.Key] = value;
            }
            return preset;
        }

        /// <summary>The canonical hash the token binds: sorted options, unit separators.</summary>
        public static string Hash(ExportPreset preset)
        {
            var sb = new StringBuilder();
            const char F = (char)31;
            sb.Append(preset.Name).Append(F).Append(preset.Format).Append(F)
              .Append(preset.SchemaVersion.ToString(CultureInfo.InvariantCulture)).Append(F)
              .Append(preset.OverwritePolicy).Append(F);
            foreach (KeyValuePair<string, string> option in preset.Options)
                sb.Append(option.Key).Append('=').Append(option.Value).Append(F);
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))
                    .Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        /// <summary>Is this option provable from the produced file?</summary>
        public static bool Verifiable(string format, string option)
        {
            Dictionary<string, bool> known;
            return Known.TryGetValue(format, out known) && known.TryGetValue(option, out bool provable) && provable;
        }

        // ---- the file-side proofs, pure over bytes/text ------------------------

        /// <summary>The IFC schema named in FILE_SCHEMA, or null.</summary>
        public static string IfcSchemaOf(string headText)
        {
            if (headText == null) return null;
            int index = headText.IndexOf("FILE_SCHEMA", StringComparison.Ordinal);
            if (index < 0) return null;
            int open = headText.IndexOf("(('", index, StringComparison.Ordinal);
            if (open < 0) return null;
            int close = headText.IndexOf("'", open + 3, StringComparison.Ordinal);
            if (close < 0) return null;
            return headText.Substring(open + 3, close - open - 3);
        }

        /// <summary>Map the 6-byte DWG signature to the option vocabulary.</summary>
        public static string DwgVersionOf(byte[] head)
        {
            if (head == null || head.Length < 6) return null;
            string signature = Encoding.ASCII.GetString(head, 0, 6);
            switch (signature)
            {
                case "AC1027": return "2013";
                case "AC1032": return "2018";
                default: return "unknown(" + signature + ")";
            }
        }

        /// <summary>The PNG width from IHDR, or -1.</summary>
        public static int PngWidthOf(byte[] head)
        {
            if (head == null || head.Length < 24) return -1;
            if (head[0] != 0x89 || head[1] != 0x50) return -1;
            return (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        }
    }
}
