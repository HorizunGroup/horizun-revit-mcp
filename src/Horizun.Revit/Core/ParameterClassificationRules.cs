// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit-free half of horizun_manage_parameters and horizun_query_classification:
//
//   * which SpecTypeId a caller means when it writes a data type the old way
//     (ParameterType was removed from the API in 2023; scripts still say "Text",
//     "YesNo", "Length");
//   * reading a shared parameter file FROM DISK, so create_shared proves the
//     definition landed in the file instead of trusting the in-memory object that
//     wrote it;
//   * what a classification table and the model's codes add up to: codes nothing
//     uses, placed types with no code, and codes the table does not contain.
//
// Kept apart so it is testable without Revit (Core.Tests links this file).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ParameterClassificationRules
    {
        // Legacy ParameterType names -> SpecTypeId member paths. Only the ones scripts
        // actually use; anything else must be passed as a SpecTypeId id or path.
        private static readonly Dictionary<string, string> Legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Text", "String.Text" }, { "MultilineText", "String.MultilineText" }, { "URL", "String.Url" },
            { "YesNo", "Boolean.YesNo" }, { "Integer", "Int.Integer" }, { "Number", "Number" },
            { "Length", "Length" }, { "Area", "Area" }, { "Volume", "Volume" }, { "Angle", "Angle" },
            { "Slope", "Slope" }, { "Currency", "Currency" }, { "Material", "Reference.Material" },
            { "Image", "Reference.Image" }, { "MassDensity", "MassDensity" }
        };

        /// <summary>The SpecTypeId member path a legacy ParameterType name stands for, or null.</summary>
        public static string LegacySpecPath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string key = name.Trim();
            if (key.StartsWith("ParameterType.", StringComparison.OrdinalIgnoreCase)) key = key.Substring(14);
            return Legacy.TryGetValue(key, out string path) ? path : null;
        }

        /// <summary>"autodesk.spec.aec:length-2.0.0" -> "autodesk.spec.aec:length". Versions change between years.</summary>
        public static string Unversioned(string typeId)
        {
            if (string.IsNullOrEmpty(typeId)) return typeId ?? "";
            int dash = typeId.LastIndexOf('-');
            if (dash > typeId.IndexOf(':') && dash + 1 < typeId.Length && char.IsDigit(typeId[dash + 1]))
                return typeId.Substring(0, dash);
            return typeId;
        }

        public sealed class SpfEntry
        {
            public Guid Guid;
            public string Name, DataType, Group;
        }

        /// <summary>
        /// Every PARAM line of a shared parameter file, with its group NAME resolved from the
        /// GROUP lines. Columns are located by the *PARAM header, not by position, because the
        /// header is what Revit itself reads.
        /// </summary>
        public static List<SpfEntry> ReadSpf(string text)
        {
            var result = new List<SpfEntry>();
            if (string.IsNullOrEmpty(text)) return result;
            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] header = null;
            var lines = text.Replace("\r", "").Split('\n');
            foreach (string line in lines)
            {
                string[] cells = line.Split('\t');
                if (cells.Length == 0) continue;
                if (cells[0] == "GROUP" && cells.Length >= 3) groups[cells[1]] = cells[2];
                else if (cells[0] == "*PARAM") header = cells;
            }
            if (header == null) return result;
            int iGuid = Array.IndexOf(header, "GUID"), iName = Array.IndexOf(header, "NAME"),
                iType = Array.IndexOf(header, "DATATYPE"), iGroup = Array.IndexOf(header, "GROUP");
            if (iGuid < 0 || iName < 0) return result;
            foreach (string line in lines)
            {
                string[] cells = line.Split('\t');
                if (cells.Length <= Math.Max(iGuid, iName) || cells[0] != "PARAM") continue;
                if (!Guid.TryParse(cells[iGuid], out Guid g)) continue;
                string groupId = iGroup >= 0 && iGroup < cells.Length ? cells[iGroup] : null;
                result.Add(new SpfEntry
                {
                    Guid = g, Name = cells[iName],
                    DataType = iType >= 0 && iType < cells.Length ? cells[iType] : null,
                    Group = groupId != null && groups.TryGetValue(groupId, out string gn) ? gn : null
                });
            }
            return result;
        }

        public sealed class TypeCode
        {
            public long Id;
            public string Name, Category, Code;
            public int Instances;
        }

        /// <summary>Table codes no type carries.</summary>
        public static JObject Unused(IEnumerable<string> tableKeys, IEnumerable<TypeCode> types, int maxRows)
        {
            var used = new HashSet<string>(types.Where(t => !string.IsNullOrWhiteSpace(t.Code)).Select(t => t.Code.Trim()), StringComparer.Ordinal);
            var unused = tableKeys.Where(k => !string.IsNullOrWhiteSpace(k) && !used.Contains(k.Trim()))
                                  .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
            return new JObject
            {
                ["unused_count"] = unused.Count,
                ["truncated"] = unused.Count > maxRows,
                ["unused"] = new JArray(unused.Take(maxRows))
            };
        }

        /// <summary>
        /// Placed types (one instance or more) without a code, and any type whose code is not
        /// in the table. An unplaced type without a code is not reported: every template
        /// carries dozens, and listing them buries the ones that reach a schedule.
        /// </summary>
        public static JObject Missing(IEnumerable<string> tableKeys, IEnumerable<TypeCode> types, int maxRows)
        {
            var keys = new HashSet<string>(tableKeys.Where(k => k != null).Select(k => k.Trim()), StringComparer.Ordinal);
            var all = types.ToList();
            var without = all.Where(t => t.Instances > 0 && string.IsNullOrWhiteSpace(t.Code))
                             .OrderByDescending(t => t.Instances).ThenBy(t => t.Id).ToList();
            var unknown = all.Where(t => !string.IsNullOrWhiteSpace(t.Code) && !keys.Contains(t.Code.Trim()))
                             .OrderBy(t => t.Code, StringComparer.Ordinal).ThenBy(t => t.Id).ToList();
            return new JObject
            {
                ["placed_types_without_code"] = without.Count,
                ["types_with_unknown_code"] = unknown.Count,
                ["truncated"] = without.Count > maxRows || unknown.Count > maxRows,
                ["without_code"] = new JArray(without.Take(maxRows).Select(Row)),
                ["unknown_code"] = new JArray(unknown.Take(maxRows).Select(Row))
            };
        }

        private static JObject Row(TypeCode t) => new JObject
        {
            ["type_id"] = t.Id, ["name"] = t.Name, ["category"] = t.Category,
            ["code"] = string.IsNullOrWhiteSpace(t.Code) ? null : t.Code, ["instances"] = t.Instances
        };

        /// <summary>How many types and placed instances carry each code.</summary>
        public static Dictionary<string, int[]> UsageByCode(IEnumerable<TypeCode> types)
        {
            var usage = new Dictionary<string, int[]>(StringComparer.Ordinal);
            foreach (TypeCode t in types)
            {
                if (string.IsNullOrWhiteSpace(t.Code)) continue;
                string k = t.Code.Trim();
                if (!usage.TryGetValue(k, out int[] c)) usage[k] = c = new int[2];
                c[0]++; c[1] += t.Instances;
            }
            return usage;
        }
    }
}
