using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Validates SQL intended for KEI project DB writes through the Revit process.
    ///
    /// Why this path exists: BIM delivery is shifting from humans alone editing
    /// 3D/models + project data, to AI agents assisting on both geometry (Revit)
    /// and project SQLite metadata (equipment, specs, coordination data). Agents
    /// must write inside the Revit process because the host holds the DB with WAL;
    /// external writers cannot safely take the lock. This guard keeps that write
    /// surface to data-only DML (INSERT/UPDATE/DELETE/REPLACE) so agents speed
    /// up accurate project data work without free-form schema destruction.
    /// </summary>
    public static class KeiSqlGuard
    {
        public const int MaxStatements = 50;
        public const int MaxStatementLength = 100_000;

        private static readonly Regex LeadingComment = new Regex(
            @"^\s*(--[^\n]*\n|/\*.*?\*/)*\s*",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // Word-boundary patterns — avoid false positives like CREATED_AT matching CREATE.
        private static readonly string[] BlockedPatterns =
        {
            @"\bATTACH\b", @"\bDETACH\b", @"\bDROP\b", @"\bALTER\b", @"\bCREATE\b",
            @"\bVACUUM\b", @"\bREINDEX\b", @"\bPRAGMA\b", @"\bLOAD_EXTENSION\b",
            @"\bINSERT\s+INTO\s+SQLITE_", @"\bUPDATE\s+SQLITE_",
            @"\bDELETE\s+FROM\s+SQLITE_", @"\bREPLACE\s+INTO\s+SQLITE_"
        };

        /// <summary>
        /// Returns null if the statement is allowed DML; otherwise a reason string.
        /// </summary>
        public static string ValidateWriteStatement(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return "SQL statement is empty.";

            if (sql.Length > MaxStatementLength)
                return $"SQL statement exceeds {MaxStatementLength} characters.";

            // One statement per command (Microsoft.Data.Sqlite executes one statement).
            var trimmed = sql.Trim().TrimEnd(';').Trim();
            if (trimmed.IndexOf(';') >= 0)
                return "Only one SQL statement per entry is allowed (no multi-statement strings).";

            var body = LeadingComment.Replace(trimmed, "");
            if (string.IsNullOrWhiteSpace(body))
                return "SQL statement has no executable body.";

            var firstWord = FirstWord(body);
            if (firstWord == null)
                return "Could not parse SQL verb.";

            bool allowedVerb =
                firstWord.Equals("INSERT", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("REPLACE", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("WITH", StringComparison.OrdinalIgnoreCase);

            if (!allowedVerb)
            {
                return "Only INSERT, UPDATE, DELETE, REPLACE, or WITH…DML are allowed " +
                       "(read via revit_query_kei_database; no DDL).";
            }

            if (firstWord.Equals("WITH", StringComparison.OrdinalIgnoreCase) &&
                !ContainsDmlVerb(body))
            {
                return "WITH must be followed by INSERT/UPDATE/DELETE/REPLACE (not SELECT-only).";
            }

            foreach (var pattern in BlockedPatterns)
            {
                if (Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase))
                    return $"Blocked SQL construct matching /{pattern}/.";
            }

            return null;
        }

        public static List<string> NormalizeStatements(IEnumerable<string> statements, out string error)
        {
            error = null;
            var list = new List<string>();
            if (statements == null)
            {
                error = "No statements provided.";
                return list;
            }

            foreach (var raw in statements)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var reason = ValidateWriteStatement(raw);
                if (reason != null)
                {
                    error = reason + " Statement: " + Truncate(raw.Trim(), 120);
                    return list;
                }
                list.Add(raw.Trim().TrimEnd(';').Trim());
            }

            if (list.Count == 0)
            {
                error = "No non-empty SQL statements provided.";
                return list;
            }

            if (list.Count > MaxStatements)
            {
                error = $"Too many statements ({list.Count}); max is {MaxStatements}.";
                return list;
            }

            return list;
        }

        private static bool ContainsDmlVerb(string body)
        {
            // Word-boundary-ish checks so "UPDATED_AT" does not count as UPDATE.
            return Regex.IsMatch(body, @"\bINSERT\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(body, @"\bUPDATE\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(body, @"\bDELETE\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(body, @"\bREPLACE\b", RegexOptions.IgnoreCase);
        }

        private static string FirstWord(string body)
        {
            int i = 0;
            while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
            int start = i;
            while (i < body.Length && (char.IsLetter(body[i]) || body[i] == '_')) i++;
            if (i <= start) return null;
            return body.Substring(start, i - start);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }
    }
}
