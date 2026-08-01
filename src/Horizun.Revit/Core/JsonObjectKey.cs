using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// JSON permits an empty object-property name, but several real MCP clients
    /// (notably Windows PowerShell 5.1 ConvertFrom-Json) cannot materialize one.
    /// Summaries use model data as keys, so normalize only the unusable blank
    /// case and preserve every meaningful Revit name verbatim.
    /// </summary>
    public static class JsonObjectKey
    {
        public static string Summary(string value) =>
            string.IsNullOrWhiteSpace(value) ? "(blank)" : value;

        /// <summary>
        /// Count model labels into a JSON object that both case-sensitive and
        /// case-insensitive clients can materialize. Revit can legitimately
        /// contain "Center line" and "Center Line" at once; emitting both as
        /// object keys makes PowerShell reject the complete MCP response.
        /// </summary>
        public static JObject SummaryCounts(IEnumerable<string> values)
        {
            var result = new JObject();
            foreach (IGrouping<string, string> group in
                (values ?? Enumerable.Empty<string>())
                    .Select(Summary)
                    .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                result[group.Key] = group.Count();
            return result;
        }
    }
}
