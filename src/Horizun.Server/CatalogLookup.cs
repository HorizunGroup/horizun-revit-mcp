// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// A HOST-RESIDENT tool: it answers entirely inside this process and never touches
// Revit. The catalog is a file the CALLER passes at call time, so this file bakes
// in NO codes, NO thresholds and NO client data — it is a generic resolver.
//
// The question is "is this code a LEAF of that hierarchical catalog", and the
// honesty contract is the whole point of the shape returned:
//
//   * A code that is NOT in the catalog does not get is_leaf=false. That would be
//     a fabricated negative — "this is not a leaf" read as fact when the real
//     state is "I have never heard of this code". Missing => is_leaf=null (unknown),
//     and 'exists' is its own separate field so the two are never conflated.
//   * A code IS a leaf iff it exists AND no OTHER code in the catalog is its strict
//     descendant (no other code begins with code + the hierarchy separator).
//
// The pure leaf rule (EvaluateLeaf) takes an already-parsed set of codes and does
// no I/O, so it is unit-testable without a file or Revit. The I/O — reading the
// CSV, hashing its bytes — lives in Handle, which also returns a sha256 of the
// file and the parsed row_count so the answer is auditable after the fact.
//
// TWO SEPARATE NOTIONS OF "SEPARATOR" LIVE HERE, deliberately kept apart in naming:
//   * The HIERARCHY separator ('separator' arg, DefaultDelimiters) — the character
//     inside a code string that marks a level break (e.g. "D01-A1-A01").
//   * The COLUMN delimiter ('delimiter' arg, ColumnDelimiterCandidates) — the
//     character that splits one catalog LINE into cells (tab/;/,/|). A field
//     report caught this tool assuming comma unconditionally: a real classification
//     catalog was tab-delimited, so the whole line (15,783 of them) was read as one
//     "code" and a real code was reported as absent. DetectColumnDelimiter below is
//     the fix — it is deterministic and documented, and ties refuse rather than guess.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    /// <summary>The verdict on one code: does it exist, and is it a leaf (unknown when absent).</summary>
    internal struct LeafOutcome
    {
        public bool Exists;
        public bool? IsLeaf;   // null == unknown, reserved for "not in the catalog" — never a stand-in for false.
    }

    /// <summary>The resolved COLUMN delimiter and how it was chosen — never confused with the hierarchy separator.</summary>
    internal struct ColumnDelimiterDetection
    {
        public string Delimiter;
        public string Mode;   // "explicit" | "detected" | "single_column_default"
    }

    /// <summary>One catalog, parsed into (code, description) rows plus the column bookkeeping that produced them.</summary>
    internal sealed class CatalogRows
    {
        public List<KeyValuePair<string, string>> Codes = new List<KeyValuePair<string, string>>();
        public List<string> Headers;                 // null when has_header=false
        public int CodeColumnIndex;
        public string CodeColumnName;                 // header text when resolvable, else null
        public int? DescriptionColumnIndex;            // null when no description column was requested
        public string DescriptionColumnName;
    }

    internal static class CatalogLookup
    {
        // When the caller gives no explicit separator, a code is treated as opaque and a
        // descendant is any OTHER code that begins with it followed by one of these common
        // hierarchy delimiters. This is the documented default rule; pass 'separator' to
        // pin it to exactly one delimiter. THIS IS THE HIERARCHY SEPARATOR — see the file
        // header note; it has nothing to do with the column delimiter below.
        private static readonly string[] DefaultDelimiters = { "-", ".", "_", "/", " " };

        // The COLUMN delimiters this tool can detect automatically, in no particular priority
        // order — detection collects every candidate that is consistent and only accepts a
        // single winner (see DetectColumnDelimiter).
        private static readonly string[] ColumnDelimiterCandidates = { "\t", ";", ",", "|" };

        /// <summary>
        /// THE PURE LEAF RULE. No I/O, no Revit — feed it the parsed set of codes.
        /// exists = the set contains 'code'. When it does not, is_leaf is null (unknown),
        /// NEVER false. When it does, is_leaf is true unless some OTHER code in the set is a
        /// strict descendant of it (begins with code + separator).
        /// </summary>
        internal static LeafOutcome EvaluateLeaf(ISet<string> codes, string code, string separator)
        {
            if (codes == null) throw new ArgumentNullException(nameof(codes));
            if (code == null) throw new ArgumentNullException(nameof(code));

            if (!codes.Contains(code))
                return new LeafOutcome { Exists = false, IsLeaf = null };

            bool hasDescendant = false;
            foreach (string other in codes)
            {
                if (string.Equals(other, code, StringComparison.Ordinal)) continue;
                if (IsStrictDescendant(other, code, separator)) { hasDescendant = true; break; }
            }
            return new LeafOutcome { Exists = true, IsLeaf = !hasDescendant };
        }

        /// <summary>Is 'candidate' a strict descendant of 'code' — code + a separator as a prefix.</summary>
        private static bool IsStrictDescendant(string candidate, string code, string separator)
        {
            if (!string.IsNullOrEmpty(separator))
                return candidate.StartsWith(code + separator, StringComparison.Ordinal);

            foreach (string d in DefaultDelimiters)
                if (candidate.StartsWith(code + d, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// LEGACY parser, kept exactly as it always behaved: one code per line, the FIRST
        /// comma-separated field of each non-blank line, trimmed and with a single pair of
        /// surrounding double quotes removed. No header is assumed or skipped. Superseded in
        /// Handle by ParseCatalogRows (delimiter-aware, column-aware, header-aware), but kept
        /// as a direct, dependency-free entry point and because tests pin its exact behavior.
        /// </summary>
        internal static List<string> ParseCodes(string text)
        {
            var codes = new List<string>();
            if (text == null) return codes;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Replace("\r", string.Empty);
                int comma = line.IndexOf(',');
                string cell = comma >= 0 ? line.Substring(0, comma) : line;
                cell = cell.Trim();
                if (cell.Length >= 2 && cell[0] == '"' && cell[cell.Length - 1] == '"')
                    cell = cell.Substring(1, cell.Length - 2).Trim();
                if (cell.Length == 0) continue;
                codes.Add(cell);
            }
            return codes;
        }

        /// <summary>A human name for an error message — never for parsing itself.</summary>
        private static string DelimiterDisplayName(string d)
        {
            switch (d)
            {
                case "\t": return "tab";
                case ";": return "semicolon (;)";
                case ",": return "comma (,)";
                case "|": return "pipe (|)";
                default: return "'" + d + "'";
            }
        }

        /// <summary>
        /// DETERMINISTIC column-delimiter detection. An explicit 'delimiter' always wins. Absent
        /// that, this samples the first 'sampleLines' non-blank lines and, for each candidate in
        /// tab/;/,/|, counts how many times it appears OUTSIDE double quotes on the first sampled
        /// line; a candidate is "consistent" only if every other sampled line has that SAME count.
        /// Exactly one consistent candidate wins (delimiter_mode=detected). Zero candidates (no
        /// recognised delimiter appears at all — e.g. one code per line) fall back to comma,
        /// matching the tool's historical single-column behavior (delimiter_mode=single_column_default).
        /// More than one consistent candidate is a genuine ambiguity — this REFUSES rather than
        /// guesses, exactly as the rest of this bridge refuses over an unclear write.
        /// </summary>
        internal static ColumnDelimiterDetection DetectColumnDelimiter(string text, string explicitDelimiter, int sampleLines = 20)
        {
            if (!string.IsNullOrEmpty(explicitDelimiter))
                return new ColumnDelimiterDetection { Delimiter = explicitDelimiter, Mode = "explicit" };

            var lines = new List<string>();
            if (text != null)
            {
                foreach (string rawLine in text.Split('\n'))
                {
                    string line = rawLine.Replace("\r", string.Empty);
                    if (line.Trim().Length == 0) continue;
                    lines.Add(line);
                    if (lines.Count >= sampleLines) break;
                }
            }

            if (lines.Count == 0)
                return new ColumnDelimiterDetection { Delimiter = ",", Mode = "single_column_default" };

            var consistent = new List<string>();
            foreach (string d in ColumnDelimiterCandidates)
            {
                int firstCount = CountDelimiterOutsideQuotes(lines[0], d);
                if (firstCount == 0) continue;
                bool ok = true;
                for (int i = 1; i < lines.Count; i++)
                {
                    if (CountDelimiterOutsideQuotes(lines[i], d) != firstCount) { ok = false; break; }
                }
                if (ok) consistent.Add(d);
            }

            if (consistent.Count == 0)
                return new ColumnDelimiterDetection { Delimiter = ",", Mode = "single_column_default" };

            if (consistent.Count > 1)
            {
                var names = new List<string>();
                foreach (string d in consistent) names.Add(DelimiterDisplayName(d));
                throw new ArgumentException(
                    "Cannot determine the catalog's column delimiter deterministically: " + string.Join(" and ", names) +
                    " both appear a consistent number of times across the first " + lines.Count +
                    " non-blank line(s). Pass 'delimiter' explicitly to resolve the tie.");
            }

            return new ColumnDelimiterDetection { Delimiter = consistent[0], Mode = "detected" };
        }

        private static bool MatchesAt(string line, int index, string token)
        {
            if (string.IsNullOrEmpty(token) || index + token.Length > line.Length) return false;
            for (int k = 0; k < token.Length; k++)
                if (line[index + k] != token[k]) return false;
            return true;
        }

        /// <summary>Counts occurrences of 'delimiter' in 'line' that are OUTSIDE a double-quoted span.</summary>
        private static int CountDelimiterOutsideQuotes(string line, string delimiter)
        {
            int count = 0;
            bool inQuotes = false;
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '"') { inQuotes = !inQuotes; i++; continue; }
                if (!inQuotes && MatchesAt(line, i, delimiter)) { count++; i += delimiter.Length; continue; }
                i++;
            }
            return count;
        }

        /// <summary>
        /// Splits ONE line into cells on 'delimiter', quote-aware: a cell that starts with a
        /// double quote runs until its matching closing quote (a doubled "" inside it is a
        /// literal quote character), and a delimiter inside that span is not a split point.
        /// Not full RFC-4180 (no multi-line quoted cells — a catalog is one row per line here),
        /// but enough to survive a quoted field that itself contains the delimiter.
        /// </summary>
        internal static List<string> SplitRow(string line, string delimiter)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        inQuotes = false; i++; continue;
                    }
                    sb.Append(c); i++; continue;
                }
                if (c == '"' && sb.Length == 0) { inQuotes = true; i++; continue; }
                if (MatchesAt(line, i, delimiter)) { cells.Add(sb.ToString()); sb.Clear(); i += delimiter.Length; continue; }
                sb.Append(c); i++;
            }
            cells.Add(sb.ToString());
            return cells;
        }

        /// <summary>
        /// Parses the whole catalog into (code, description) rows using a resolved column
        /// delimiter, an optional header row and optional column selectors. 'codeColumn' /
        /// 'descriptionColumn' are each either a 0-based integer index or (only when
        /// has_header=true) a header-name string, resolved case-sensitively against the exact
        /// header text. codeColumn defaults to column 0 (matching the tool's historical
        /// behavior); descriptionColumn null means "no description requested" (the leaf
        /// operation never needs one). A blank code cell is skipped, same as before.
        /// </summary>
        internal static CatalogRows ParseCatalogRows(string text, string delimiter, JToken codeColumn, JToken descriptionColumn, bool hasHeader)
        {
            var result = new CatalogRows();

            bool codeIsHeaderName = codeColumn != null && codeColumn.Type == JTokenType.String;
            bool descIsHeaderName = descriptionColumn != null && descriptionColumn.Type == JTokenType.String;
            if (codeIsHeaderName && !hasHeader)
                throw new ArgumentException("code_column ('" + (string)codeColumn + "') is a header name but has_header is not true.");
            if (descIsHeaderName && !hasHeader)
                throw new ArgumentException("description_column ('" + (string)descriptionColumn + "') is a header name but has_header is not true.");

            int codeIdx = codeColumn != null && codeColumn.Type == JTokenType.Integer ? (int)codeColumn : 0;
            if (codeIdx < 0) throw new ArgumentException("code_column index must be >= 0.");
            string codeWanted = codeIsHeaderName ? (string)codeColumn : null;

            int? descIdx = descriptionColumn != null && descriptionColumn.Type == JTokenType.Integer ? (int)descriptionColumn : (int?)null;
            if (descIdx.HasValue && descIdx.Value < 0) throw new ArgumentException("description_column index must be >= 0.");
            string descWanted = descIsHeaderName ? (string)descriptionColumn : null;

            List<string> headers = null;
            bool headerConsumed = !hasHeader;

            if (text != null)
            {
                foreach (string rawLine in text.Split('\n'))
                {
                    string line = rawLine.Replace("\r", string.Empty);
                    if (line.Trim().Length == 0) continue;

                    if (!headerConsumed)
                    {
                        headers = SplitRow(line, delimiter);
                        for (int i = 0; i < headers.Count; i++) headers[i] = headers[i].Trim();
                        headerConsumed = true;

                        if (codeWanted != null)
                        {
                            int idx = headers.FindIndex(h => string.Equals(h, codeWanted, StringComparison.Ordinal));
                            if (idx < 0)
                                throw new ArgumentException("code_column header '" + codeWanted + "' not found. Columns found: " + string.Join(", ", headers));
                            codeIdx = idx;
                        }
                        if (descWanted != null)
                        {
                            int idx = headers.FindIndex(h => string.Equals(h, descWanted, StringComparison.Ordinal));
                            if (idx < 0)
                                throw new ArgumentException("description_column header '" + descWanted + "' not found. Columns found: " + string.Join(", ", headers));
                            descIdx = idx;
                        }
                        continue;
                    }

                    List<string> cells = SplitRow(line, delimiter);
                    string code = codeIdx < cells.Count ? cells[codeIdx].Trim() : string.Empty;
                    if (code.Length == 0) continue;
                    string description = descIdx.HasValue && descIdx.Value < cells.Count ? cells[descIdx.Value].Trim() : string.Empty;
                    result.Codes.Add(new KeyValuePair<string, string>(code, description));
                }
            }

            result.Headers = headers;
            result.CodeColumnIndex = codeIdx;
            result.CodeColumnName = headers != null && codeIdx < headers.Count ? headers[codeIdx] : codeWanted;
            result.DescriptionColumnIndex = descIdx;
            result.DescriptionColumnName = headers != null && descIdx.HasValue && descIdx.Value < headers.Count ? headers[descIdx.Value] : descWanted;
            return result;
        }

        /// <summary>Lowercase, accent-stripped normalization used ONLY for operation=search matching.</summary>
        private static string NormalizeForSearch(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (char c in formD)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().ToLowerInvariant();
        }

        /// <summary>Splits a normalized string into letter/digit runs — the "tokens" that operation=search matches on.</summary>
        private static List<string> Tokenize(string normalized)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in normalized)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        /// <summary>
        /// The host handler. Reads the catalog file the caller named, parses its codes,
        /// applies the pure leaf rule and returns an auditable answer. Genuine I/O failure
        /// (missing/unreadable file) throws — the caller sees an error, not a fabricated
        /// verdict. A code simply not in the catalog is NOT a failure: it is exists=false /
        /// is_leaf=null, the honest unknown.
        /// </summary>
        internal static JObject Handle(JObject args) => Handle(args, CancellationToken.None);

        internal static JObject Handle(JObject args, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The bSDD operations (2026-09-24) and the description search (2026-09-25) share
            // this tool rather than getting one of their own: tools/list has a 512 KiB budget.
            string operation = args?["operation"] is JValue op && op.Type == JTokenType.String ? (string)op : "leaf";
            if (operation.StartsWith(BsddLookup.OperationPrefix, StringComparison.Ordinal))
                return BsddLookup.Handle(args, cancellationToken);
            if (operation == "search")
                return HandleSearch(args, cancellationToken);
            if (operation != "leaf")
                throw new ArgumentException("Unknown operation '" + operation + "'. Use leaf (default), search, bsdd_search, " +
                                            "bsdd_search_dictionary, bsdd_class, bsdd_property or bsdd_dictionaries.");

            string catalogPath = (string)args?["catalog_path"];
            string code = (string)args?["code"];
            string separator = (string)args?["separator"];
            string delimiterArg = (string)args?["delimiter"];
            bool hasHeader = args?["has_header"] != null && args["has_header"].Type == JTokenType.Boolean && (bool)args["has_header"];
            JToken codeColumn = args?["code_column"];

            if (string.IsNullOrEmpty(catalogPath))
                throw new ArgumentException("catalog_path is required.");
            if (code == null)
                throw new ArgumentException("code is required.");
            if (!File.Exists(catalogPath))
                throw new FileNotFoundException("Catalog file not found: " + catalogPath);

            byte[] bytes = File.ReadAllBytes(catalogPath);
            cancellationToken.ThrowIfCancellationRequested();
            string sha = Sha256Hex(bytes);
            string encodingUsed;
            string text = DecodeCatalog(StripBom(bytes), out encodingUsed);

            ColumnDelimiterDetection detection = DetectColumnDelimiter(text, delimiterArg);
            CatalogRows parsed = ParseCatalogRows(text, detection.Delimiter, codeColumn, null, hasHeader);
            List<string> codes = parsed.Codes.ConvertAll(kv => kv.Key);
            var set = new HashSet<string>(codes, StringComparer.Ordinal);

            LeafOutcome outcome = EvaluateLeaf(set, code, separator);

            bool explicitSep = !string.IsNullOrEmpty(separator);
            string note = outcome.Exists
                ? (outcome.IsLeaf == true
                    ? "'" + code + "' is in the catalog and no other code is its strict descendant: it is a last-level leaf."
                    : "'" + code + "' is in the catalog but at least one other code descends from it: it is a parent, not a leaf.")
                : "'" + code + "' is NOT in the catalog. is_leaf is null (unknown), not false — this catalog says nothing about it.";

            return new JObject
            {
                ["catalog_path"] = catalogPath,
                ["code"] = code,
                ["separator"] = explicitSep ? separator : null,
                ["separator_mode"] = explicitSep ? "explicit" : "default_delimiters",
                ["exists"] = outcome.Exists,
                ["is_leaf"] = outcome.IsLeaf.HasValue ? (JToken)new JValue(outcome.IsLeaf.Value) : JValue.CreateNull(),
                ["row_count"] = codes.Count,
                ["distinct_code_count"] = set.Count,
                ["encoding_used"] = encodingUsed,
                ["sha256"] = sha,
                ["delimiter_used"] = detection.Delimiter,
                ["delimiter_mode"] = detection.Mode,
                ["code_column_index"] = parsed.CodeColumnIndex,
                ["code_column_name"] = parsed.CodeColumnName,
                ["columns"] = parsed.Headers != null ? (JToken)new JArray(parsed.Headers) : null,
                ["note"] = note
            };
        }

        /// <summary>
        /// operation=search: find catalog codes whose description best matches 'query', by
        /// normalized (accent- and case-insensitive) whole-token overlap — never fuzzy or
        /// substring beyond that. Exists purely because doing this by hand (opening the CSV
        /// and reading it) had already happened four times in one field session. Returns
        /// code / description / is_leaf / score, ranked by the fraction of query tokens found
        /// in the description, ties broken by an exact normalized-substring hit and then by
        /// code text — deterministic, not by file order.
        /// </summary>
        private static JObject HandleSearch(JObject args, CancellationToken cancellationToken)
        {
            string catalogPath = (string)args?["catalog_path"];
            string query = (string)args?["query"];
            string separator = (string)args?["separator"];
            string delimiterArg = (string)args?["delimiter"];
            bool hasHeader = args?["has_header"] != null && args["has_header"].Type == JTokenType.Boolean && (bool)args["has_header"];
            JToken codeColumn = args?["code_column"];
            JToken descriptionColumn = args?["description_column"];
            int maxResults = args?["max_results"] != null ? (int)args["max_results"] : 10;

            if (string.IsNullOrEmpty(catalogPath))
                throw new ArgumentException("catalog_path is required.");
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("query is required and must not be blank.");
            if (maxResults <= 0)
                throw new ArgumentException("max_results must be a positive integer.");
            if (maxResults > 200) maxResults = 200;   // a lookup aid, not a bulk export
            if (!File.Exists(catalogPath))
                throw new FileNotFoundException("Catalog file not found: " + catalogPath);

            byte[] bytes = File.ReadAllBytes(catalogPath);
            cancellationToken.ThrowIfCancellationRequested();
            string sha = Sha256Hex(bytes);
            string encodingUsed;
            string text = DecodeCatalog(StripBom(bytes), out encodingUsed);

            ColumnDelimiterDetection detection = DetectColumnDelimiter(text, delimiterArg);
            JToken descriptionColumnEffective = descriptionColumn ?? new JValue(1);   // column 1: code,description,...
            CatalogRows parsed = ParseCatalogRows(text, detection.Delimiter, codeColumn, descriptionColumnEffective, hasHeader);

            var allCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in parsed.Codes) allCodes.Add(row.Key);

            string normalizedQuery = NormalizeForSearch(query);
            List<string> queryTokens = Tokenize(normalizedQuery);
            if (queryTokens.Count == 0)
                throw new ArgumentException("query has no searchable tokens after normalization.");

            var scored = new List<Tuple<KeyValuePair<string, string>, double, bool>>();
            var seenCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in parsed.Codes)
            {
                // A code can repeat across rows (e.g. a re-exported catalog); score the first
                // occurrence only — the same one that answers exists/is_leaf for that code.
                if (!seenCodes.Add(row.Key)) continue;

                string normalizedDescription = NormalizeForSearch(row.Value);
                var descTokens = new HashSet<string>(Tokenize(normalizedDescription), StringComparer.Ordinal);
                int matched = 0;
                foreach (string t in queryTokens) if (descTokens.Contains(t)) matched++;
                if (matched == 0) continue;

                double score = matched / (double)queryTokens.Count;
                bool substringHit = normalizedDescription.Length > 0 && normalizedDescription.Contains(normalizedQuery);
                scored.Add(Tuple.Create(row, score, substringHit));
            }

            scored.Sort((a, b) =>
            {
                int byScore = b.Item2.CompareTo(a.Item2);
                if (byScore != 0) return byScore;
                int bySubstring = (b.Item3 ? 1 : 0).CompareTo(a.Item3 ? 1 : 0);
                if (bySubstring != 0) return bySubstring;
                return string.Compare(a.Item1.Key, b.Item1.Key, StringComparison.Ordinal);
            });

            var matches = new JArray();
            int taken = 0;
            foreach (var s in scored)
            {
                if (taken >= maxResults) break;
                LeafOutcome outcome = EvaluateLeaf(allCodes, s.Item1.Key, separator);
                matches.Add(new JObject
                {
                    ["code"] = s.Item1.Key,
                    ["description"] = s.Item1.Value,
                    ["exists"] = true,
                    ["is_leaf"] = outcome.IsLeaf.HasValue ? (JToken)new JValue(outcome.IsLeaf.Value) : JValue.CreateNull(),
                    ["score"] = Math.Round(s.Item2, 4)
                });
                taken++;
            }

            bool explicitSep = !string.IsNullOrEmpty(separator);
            return new JObject
            {
                ["catalog_path"] = catalogPath,
                ["query"] = query,
                ["separator"] = explicitSep ? separator : null,
                ["separator_mode"] = explicitSep ? "explicit" : "default_delimiters",
                ["delimiter_used"] = detection.Delimiter,
                ["delimiter_mode"] = detection.Mode,
                ["code_column_index"] = parsed.CodeColumnIndex,
                ["code_column_name"] = parsed.CodeColumnName,
                ["description_column_index"] = parsed.DescriptionColumnIndex,
                ["description_column_name"] = parsed.DescriptionColumnName,
                ["columns"] = parsed.Headers != null ? (JToken)new JArray(parsed.Headers) : null,
                ["row_count"] = parsed.Codes.Count,
                ["distinct_code_count"] = allCodes.Count,
                ["matched_count"] = scored.Count,
                ["max_results"] = maxResults,
                ["results_count"] = matches.Count,
                ["encoding_used"] = encodingUsed,
                ["sha256"] = sha,
                ["matches"] = matches,
                ["note"] = scored.Count == 0
                    ? "No description in the catalog matched any normalized token of the query."
                    : ("Ranked by the fraction of query tokens matched in the accent- and case-normalized description; " +
                       matches.Count + " of " + scored.Count + " matching row(s) returned.")
            };
        }

        /// <summary>
        /// Decode the catalog bytes, and SAY which encoding was used. UTF-8 is tried
        /// strictly first: the default lenient decoder turns every byte it does not
        /// understand into U+FFFD, so a catalog saved as ANSI — which is what Excel
        /// produces by default on a non-English Windows — silently loses every accented
        /// character. The code then never matches and the answer comes back exists=false,
        /// a fabricated "not in the catalog" that is really "I misread the file". A file
        /// that is not valid UTF-8 is decoded as Latin-1 (every byte maps to a character,
        /// so it cannot fail) and the choice is reported to the caller, never hidden.
        /// </summary>
        internal static string DecodeCatalog(byte[] bytes, out string encodingUsed)
        {
            try
            {
                string strict = new UTF8Encoding(false, true).GetString(bytes);
                encodingUsed = "utf-8";
                return strict;
            }
            catch (DecoderFallbackException)
            {
                encodingUsed = "latin-1 (the bytes are not valid UTF-8)";
                var sb = new StringBuilder(bytes.Length);
                foreach (byte b in bytes) sb.Append((char)b);
                return sb.ToString();
            }
        }

        /// <summary>Lowercase hex SHA-256 of the given bytes — the provenance stamp.</summary>
        internal static string Sha256Hex(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Drop a leading UTF-8 BOM so a byte-order mark never becomes part of the first code.</summary>
        private static byte[] StripBom(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                var trimmed = new byte[bytes.Length - 3];
                Array.Copy(bytes, 3, trimmed, 0, trimmed.Length);
                return trimmed;
            }
            return bytes;
        }
    }
}
