// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// offset THAT PAGED NOTHING (field cycle, 2026-10-21).
//
// MEASURED: horizun_query_model took an `offset` argument without complaint and
// ignored it - every call answered the first page, so a loop that advanced by offset
// never ended. Only `cursor` paged.
//
// This file is the Revit-free half: offset is honoured as a start position (refused
// together with a cursor, which already carries one). A malformed value is refused by
// name, never dropped.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class QueryPagingRules
    {
        /// <summary>The return_parameters entry that lists EVERY parameter of the element and its type.</summary>
        public const string AllParameters = "*";

        public sealed class WildcardBudget
        {
            private readonly int rowLimit, byteLimit;
            private int bytes;
            public int Rows { get; private set; }
            public bool Exhausted { get; private set; }
            public bool CanProject => !Exhausted && Rows < rowLimit;
            public WildcardBudget(int rowLimit, int byteLimit) { this.rowLimit = rowLimit; this.byteLimit = byteLimit; }
            public bool TryAdd(int size)
            {
                if (size < 0 || size > byteLimit - bytes) { Exhausted = true; return false; }
                bytes += size; Rows++; return true;
            }
        }

        // Project lazily: at most one rejected row exists outside the returned page.
        public static System.Collections.Generic.List<T> ProjectWildcardPage<T>(
            System.Collections.Generic.IEnumerable<T> candidates, WildcardBudget budget,
            System.Func<T, int> project, System.Action<T> discard)
        {
            var page = new System.Collections.Generic.List<T>();
            foreach (T row in candidates)
            {
                if (!budget.CanProject) break;
                if (!budget.TryAdd(project(row))) { discard(row); break; }
                page.Add(row);
            }
            return page;
        }

        /// <summary>
        /// "*" needs the full row format and cannot be summed or grouped: the columns are not known until the
        /// elements are read, and a sum over columns nobody named would answer a question nobody asked.
        /// </summary>
        public static string AllParametersProblem(System.Collections.Generic.IEnumerable<string> returnParameters,
                                                  string parameterFormat, bool grouped, bool summed)
        {
            if (returnParameters == null) return null;
            bool star = false;
            foreach (string s in returnParameters) if (s == AllParameters) { star = true; break; }
            if (!star) return null;
            if (!string.Equals(parameterFormat, "full", System.StringComparison.OrdinalIgnoreCase))
                return "return_parameters '*' needs parameter_format=full: the columns are only known once each element is read.";
            if (grouped || summed)
                return "return_parameters '*' cannot be combined with group_by or sum_parameters: those need parameters named in advance.";
            return null;
        }

        /// <summary>The key of one listed parameter: scope and name, with a numeric suffix if the name repeats in that scope.</summary>
        public static string ParameterKey(string scope, string name, System.Collections.Generic.ISet<string> used)
        {
            string baseKey = scope + ":" + name;
            string key = baseKey;
            for (int n = 2; !used.Add(key); n++) key = baseKey + " (" + n + ")";
            return key;
        }

        /// <summary>The start position of a page: offset when given, else 0. A cursor carries its own and excludes offset.</summary>
        public static string ParseOffset(JToken offset, string cursor, out int start)
        {
            start = 0;
            if (offset == null || offset.Type == JTokenType.Null) return null;
            if (offset.Type != JTokenType.Integer || offset.Value<long>() < 0 || offset.Value<long>() > int.MaxValue)
                return "offset must be a non-negative integer (the number of matched rows to skip).";
            if (!string.IsNullOrWhiteSpace(cursor))
                return "offset and cursor cannot be combined: the cursor already carries the position. Send one of them.";
            start = offset.Value<int>();
            return null;
        }
    }
}
