// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// COMPATIBILITY BEFORE EXECUTION: the mistakes an agent makes about WHICH Revit
// and WHICH Python it is talking to, fixed or refused before anything runs.
//
// Field signals (October 2026) put 10-20 % of execute_python failures here. Every
// one of them is certain to fail at runtime and certain to be fixable from the
// text alone, so the bridge does it - in place, on the same line, so tracebacks
// keep the caller's line numbers - and SAYS what it changed (compat_rewrites):
//
//   * ElementId.IntegerValue on Revit 2026+, where it no longer exists, becomes
//     .Value (the same number as horizun.id_value returns; 64-bit since 2024).
//   * <SomethingId>.Value on Revit 2023, where ElementId has only IntegerValue,
//     becomes .IntegerValue. Only names that end in "Id" (e.Id, ViewTemplateId,
//     GetTypeId()...) are touched; UniqueId is a string and is left alone.
//   * X.None (StorageType.None, ...) is a SyntaxError in Python 3 - None is a
//     keyword - and becomes getattr(X, 'None'), which reads the .NET member.
//   * BuiltInCategory.OST_* names that differ only in case are corrected; names
//     that do not exist at all are REFUSED with the closest real names, because
//     choosing one for the caller would be a guess about the model.
//
// Two instrumentations ride along, both invisible when nothing goes wrong:
//   * Transaction( / SubTransaction( / TransactionGroup( are created through
//     _hz_new_tx, which remembers them, so the bridge can roll back what a script
//     left open instead of reporting a poisoned document;
//   * name.Close( goes through _hz_closing(name) first, which rolls back that
//     document's open transactions - Revit otherwise refuses with "Close is not
//     allowed when there is any open sub-transaction, transaction or transaction
//     group".
//
// And one decision: AutoTransactionMarkers says whether a script WRITES without
// opening any transaction ("Attempt to modify the model outside of transaction").
// The command then runs it inside one transaction it owns, committed on success and
// rolled back on failure. Scripts that open documents, save, close, post commands or
// manage transactions themselves are never wrapped.
//
// Pure: regexes over source masked by PythonSourceMask (comments and strings
// blanked, same length), so nothing inside a string or a comment is ever rewritten.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PythonCompat
    {
        public sealed class Result
        {
            /// <summary>The source that will run. Same line count as the input.</summary>
            public string Code;
            /// <summary>One entry per change: rule, line, from, to.</summary>
            public JArray Rewrites = new JArray();
            /// <summary>What could not be adapted: rule, line, found, fix, suggestions. Non-empty means refuse.</summary>
            public JArray Errors = new JArray();
            /// <summary>How many transaction constructors are tracked by _hz_new_tx.</summary>
            public int TrackedTransactions;
            public int GuardedCloses;
        }

        private sealed class Edit
        {
            public int Start, Length;
            public string Replacement;
            public JObject Report;
        }

        private const string Chain = @"(?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*";

        private static readonly Regex IntegerValue = new Regex(@"\.IntegerValue\b", RegexOptions.CultureInvariant);
        private static readonly Regex IdValue = new Regex(
            @"(?<![\w.])(?<expr>(?:" + Chain + @"\.)?(?<name>(?:[A-Za-z_]\w*)?Id)(?:\(\))?)\.Value\b", RegexOptions.CultureInvariant);
        private static readonly Regex NoneMember = new Regex(
            @"(?<![\w.])(?<expr>" + Chain + @")\.None\b", RegexOptions.CultureInvariant);
        private static readonly Regex Category = new Regex(
            @"\bBuiltInCategory\.(?<name>OST_\w+)", RegexOptions.CultureInvariant);
        private static readonly Regex TxConstructor = new Regex(
            @"(?<![\w.])(?<!\bdef\s+)(?<!\bclass\s+)(?<expr>(?:" + Chain + @"\.)?(?:Sub)?Transaction(?:Group)?)\((?<empty>\s*\))?", RegexOptions.CultureInvariant);
        private static readonly Regex CloseCall = new Regex(
            @"(?<![\w.)\]])(?<expr>" + Chain + @")\.Close\(", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> NotElementIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "UniqueId", "VersionGuid", "ExportId", "SessionId", "ThreadId", "ProcessId", "ManagedThreadId"
        };

        /// <summary>
        /// Rewrites what is certainly wrong for this Revit year and this Python, and lists
        /// what cannot be rewritten. <paramref name="builtInCategories"/> null skips the
        /// category check (the names come from the running Revit's enumeration).
        /// </summary>
        public static Result Apply(string source, int revitYear, ICollection<string> builtInCategories)
        {
            var result = new Result { Code = source };
            if (string.IsNullOrEmpty(source)) return result;
            string masked = PythonSourceMask.StripCommentsAndStrings(source) ?? source;
            var edits = new List<Edit>();

            if (revitYear >= 2026)
                foreach (Match m in IntegerValue.Matches(masked))
                    edits.Add(new Edit
                    {
                        Start = m.Index, Length = m.Length, Replacement = ".Value",
                        Report = Report("element_id_integer_value", source, m.Index, ".IntegerValue", ".Value",
                            "ElementId.IntegerValue does not exist in Revit " + revitYear + "; .Value is the same number " +
                            "(what horizun.id_value returns)")
                    });

            if (revitYear > 0 && revitYear <= 2023)
                foreach (Match m in IdValue.Matches(masked))
                {
                    if (NotElementIds.Contains(m.Groups["name"].Value)) continue;
                    int at = m.Index + m.Length - ".Value".Length;
                    edits.Add(new Edit
                    {
                        Start = at, Length = ".Value".Length, Replacement = ".IntegerValue",
                        Report = Report("element_id_value_2023", source, at, m.Value, m.Groups["expr"].Value + ".IntegerValue",
                            "ElementId has no .Value in Revit 2023; .IntegerValue is the same number")
                    });
                }

            foreach (Match m in NoneMember.Matches(masked))
            {
                string expr = m.Groups["expr"].Value;
                string replacement = "getattr(" + expr + ", 'None')";
                edits.Add(new Edit
                {
                    Start = m.Index, Length = m.Length, Replacement = replacement,
                    Report = Report("dotnet_member_named_none", source, m.Index, m.Value, replacement,
                        "None is a keyword in Python 3, so " + m.Value + " does not compile; getattr reads the .NET member")
                });
            }

            if (builtInCategories != null && builtInCategories.Count > 0)
            {
                var exact = new HashSet<string>(builtInCategories, StringComparer.Ordinal);
                var byLower = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string n in builtInCategories) if (!byLower.ContainsKey(n)) byLower[n] = n;
                foreach (Match m in Category.Matches(masked))
                {
                    Group g = m.Groups["name"];
                    string name = source.Substring(g.Index, g.Length);
                    if (exact.Contains(name)) continue;
                    string fixedCase;
                    if (byLower.TryGetValue(name, out fixedCase))
                    {
                        edits.Add(new Edit
                        {
                            Start = g.Index, Length = g.Length, Replacement = fixedCase,
                            Report = Report("builtincategory_case", source, g.Index, name, fixedCase,
                                "BuiltInCategory names are case-sensitive")
                        });
                        continue;
                    }
                    var suggestions = Closest(name, builtInCategories, 3);
                    result.Errors.Add(new JObject
                    {
                        ["rule"] = "unknown_builtincategory",
                        ["line"] = LineOf(source, g.Index),
                        ["found"] = "BuiltInCategory." + name,
                        ["suggestions"] = new JArray(suggestions.Select(s => "BuiltInCategory." + s)),
                        ["fix"] = suggestions.Count == 0
                            ? "Use a name that exists in this Revit's BuiltInCategory enumeration."
                            : "BuiltInCategory." + name + " does not exist in this Revit. Use one of: " +
                              string.Join(", ", suggestions.Select(s => "BuiltInCategory." + s)) + "."
                    });
                }
            }

            foreach (Match m in TxConstructor.Matches(masked))
            {
                string expr = m.Groups["expr"].Value;
                string replacement = m.Groups["empty"].Success
                    ? "_hz_new_tx(" + expr + ")"
                    : "_hz_new_tx(" + expr + ", ";
                edits.Add(new Edit { Start = m.Index, Length = m.Length, Replacement = replacement });
                result.TrackedTransactions++;
            }

            foreach (Match m in CloseCall.Matches(masked))
            {
                string expr = m.Groups["expr"].Value;
                if (expr.StartsWith("_hz_", StringComparison.Ordinal)) continue;
                edits.Add(new Edit
                {
                    Start = m.Index, Length = m.Length, Replacement = "_hz_closing(" + expr + ").Close("
                });
                result.GuardedCloses++;
            }

            // Overlaps are impossible by construction except a None member or an Id name
            // inside a Close chain; the leftmost-longest edit wins and the other is dropped.
            edits.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
            var kept = new List<Edit>();
            int end = -1;
            foreach (Edit e in edits)
            {
                if (e.Start < end) continue;
                kept.Add(e);
                end = e.Start + e.Length;
            }

            var sb = new StringBuilder(source.Length + kept.Count * 16);
            int cursor = 0;
            foreach (Edit e in kept)
            {
                sb.Append(source, cursor, e.Start - cursor);
                sb.Append(e.Replacement);
                cursor = e.Start + e.Length;
                if (e.Report != null) result.Rewrites.Add(e.Report);
            }
            sb.Append(source, cursor, source.Length - cursor);
            result.Code = sb.ToString();
            return result;
        }

        // ---- automatic transaction -------------------------------------------------

        private static readonly Regex OwnsTransactions = new Regex(
            @"\b(?:Sub)?Transaction(?:Group)?\b|\bhorizun\.transaction\b|\bOpenDocumentFile\b|\bOpenAndActivateDocument\b|" +
            @"\.Close\(|\.Save\(|\.SaveAs\(|\bEditFamily\b|\bPostCommand\b|\bSynchroniz|\bLoadFamily\b|\bLoadFamilySymbol\b|" +
            @"\bdialog_answer\b|\bNewFamilyDocument\b|\bNewProjectDocument\b|\bCreate\.NewFamilyDocument\b|\bReload\(|\bLoadFrom\(",
            RegexOptions.CultureInvariant);

        private static readonly Regex Writes = new Regex(
            @"\.Delete\(|\.Set\(|\.SetValueString\(|\.Create\(|\.Create\.New\w*\(|\bNewFamilyInstance\(|\bElementTransformUtils\.\w+\(|" +
            @"\bJoinGeometryUtils\.(?:Join|Unjoin|Switch)\w*\(|\.Paint\(|\.RemovePaint\(|\.SetElementOverrides\(|\.SetCategoryOverrides\(|" +
            @"\.SetCategoryHidden\(|\.HideElements\(|\.UnhideElements\(|\.HideCategoryTemporary\(|\.Duplicate\(|\.ChangeTypeId\(|" +
            @"\.Regenerate\(|\.SetFilterOverrides\(|\.AddFilter\(|\.RemoveFilter\(|\.SetSectionBox\(|\.Mirror\w*\(|\.Rotate\(|" +
            @"\.Move\(|\.Flip\w*\(|\.Ungroup\(|\.SetTypeId\(|\.ConnectTo\(|\.DisconnectFrom\(|" +
            @"\.(?:Name|Pinned|ViewTemplateId|CropBox|CropBoxActive|CropBoxVisible|Scale|DetailLevel|DisplayStyle|Curve|Point)\s*=(?!=)",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// The write calls found in a script that opens no transaction and manages no
        /// documents - empty when the script should run as it is. The command wraps a
        /// non-empty answer in one transaction of its own.
        /// </summary>
        public static List<string> AutoTransactionMarkers(string source)
        {
            var markers = new List<string>();
            if (string.IsNullOrEmpty(source)) return markers;
            string masked = PythonSourceMask.StripCommentsAndStrings(source) ?? source;
            if (OwnsTransactions.IsMatch(masked)) return markers;
            foreach (Match m in Writes.Matches(masked))
            {
                string v = Regex.Replace(m.Value, @"\s+", "");
                if (!markers.Contains(v)) markers.Add(v);
                if (markers.Count >= 5) break;
            }
            return markers;
        }

        // ---- helpers ---------------------------------------------------------------

        private static JObject Report(string rule, string source, int index, string from, string to, string why)
        {
            return new JObject { ["rule"] = rule, ["line"] = LineOf(source, index), ["from"] = from, ["to"] = to, ["why"] = why };
        }

        public static int LineOf(string source, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < source.Length; i++) if (source[i] == '\n') line++;
            return line;
        }

        /// <summary>The nearest names by edit distance on the part after OST_, ties by length.</summary>
        public static List<string> Closest(string name, IEnumerable<string> candidates, int take)
        {
            string key = Strip(name);
            return candidates
                .Select(c => new { c, d = Distance(key, Strip(c)), contains = Strip(c).Contains(key) || key.Contains(Strip(c)) })
                .OrderBy(x => x.contains ? x.d / 2 : x.d)
                .ThenBy(x => x.c.Length)
                .Take(take)
                .Select(x => x.c)
                .ToList();
        }

        private static string Strip(string n) =>
            (n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? n.Substring(4) : n).ToLowerInvariant();

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                       d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }
}
