// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// A HOST-SIDE tool. Unlike every other horizun_* tool, this one does NOT forward
// to the Revit plugin — there is no live model in the loop. It reads a hierarchy
// catalog off DISK (a tab-separated CODE<TAB>TITLE<TAB>PARENT_CODE file), and
// answers two questions three of the user's coding processes depend on:
//
//   validate — is this exact code a real row, and is it ASSIGNABLE?
//   search   — which ASSIGNABLE rows mention this term?
//
// ASSIGNABLE means LEAF: no other row names it as its PARENT. The classic lie
// this tool exists to kill is confirming a code "exists" by prefix / substring /
// segment-count / regex shape while it is actually a CHAPTER that other rows hang
// off. Leaf-ness here is derived from the PARENT COLUMN and nothing else: a code
// is a leaf iff it is nobody's parent. Segment count and breadcrumb are reported
// as EVIDENCE of depth, never as the test.
//
// The contract: never report work not verified. Every response carries provenance
// for the file it actually read — absolute path, last-write time, sha256, rows
// parsed, leaf count — computed fresh on every call (no cache: a cached answer
// cannot prove the bytes on disk today). A missing/unreadable file, a parse that
// yields zero rows, or a call with no catalog_path is a hard "Error: ..." that
// names itself, never a silent empty result that reads like "nothing matched".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    public class HorizunCatalogTools
    {
        // ---- LOOKUP -----------------------------------------------------------

        [McpServerTool(Name = "horizun_catalog_lookup", ReadOnly = true, Idempotent = true),
         System.ComponentModel.Description(
            "Look up codes in a hierarchy catalog on DISK (tab-separated CODE<TAB>TITLE<TAB>PARENT_CODE, e.g. a " +
            "classification / cost-breakdown tree). HOST-SIDE: reads the file, touches no Revit model. " +
            "THE ONE RULE: a code is ASSIGNABLE only if it is a LEAF — no other row names it as its PARENT. A code " +
            "with children is a CHAPTER and must be REFUSED even though it is a real row. Leaf-ness is computed from " +
            "the parent column, NEVER from segment count or code shape — that is exactly the lie this tool prevents. " +
            "mode='validate': pass codes (JSON array of strings); for each you get exists (literal row match), " +
            "is_leaf (nobody's parent), assignable (exists AND leaf), title, children_count (WHY it was rejected — how " +
            "many rows hang off it), breadcrumb (parent chain root->code) and segments — segments/breadcrumb are depth " +
            "EVIDENCE, not the test. " +
            "mode='search': pass term; returns ONLY leaf (assignable) rows whose code or title contains it, capped by " +
            "limit with an explicit total_leaf_matches vs returned vs truncated — a caller coding a type wants " +
            "assignable targets, not chapters. " +
            "catalog_path is REQUIRED and absolute. Every response carries sources {path, last_write_utc, sha256, " +
            "rows_parsed, leaf_count}. A missing/unreadable file, 0 rows parsed, or a missing catalog_path is a hard " +
            "Error, never an empty result. Duplicate CODEs (a catalog defect) are reported as warnings, not silently " +
            "collapsed to one.")]
        public static Task<string> CatalogLookup(
            string mode,
            string catalog_path,
            string codes = null,
            string term = null,
            int? limit = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mode))
                    throw new ArgumentException("'mode' is required: 'validate' or 'search'.");
                var m = mode.Trim().ToLowerInvariant();
                if (m != "validate" && m != "search")
                    throw new ArgumentException($"Unknown mode '{mode}'. Expected 'validate' or 'search'.");

                if (string.IsNullOrWhiteSpace(catalog_path))
                    throw new ArgumentException(
                        "'catalog_path' is required — refusing to guess a catalog. Pass the absolute path to the " +
                        "tab-separated CODE<TAB>TITLE<TAB>PARENT_CODE file.");

                var catalog = Catalog.Load(catalog_path);

                var response = new JObject
                {
                    ["mode"] = m,
                    ["sources"] = catalog.Sources
                };
                if (catalog.Warnings.Count > 0)
                    response["warnings"] = new JArray(catalog.Warnings);

                if (m == "validate")
                {
                    var codeList = ParseCodeList(codes);
                    if (codeList.Count == 0)
                        throw new ArgumentException(
                            "mode='validate' requires 'codes': a JSON array of code strings, e.g. " +
                            "[\"D001-A1-A01\",\"D001-A1\"]. Refusing to return an empty validation.");

                    var results = new JArray();
                    foreach (var raw in codeList)
                    {
                        var code = (raw ?? string.Empty).Trim();
                        results.Add(catalog.Validate(code));
                    }
                    response["requested"] = codeList.Count;
                    response["results"] = results;
                }
                else // search
                {
                    if (string.IsNullOrWhiteSpace(term))
                        throw new ArgumentException(
                            "mode='search' requires a non-empty 'term'. Refusing to return the whole catalog as a " +
                            "silent match.");
                    var cap = limit ?? 200;
                    if (cap < 1) cap = 1;
                    response["search"] = catalog.SearchLeaves(term.Trim(), cap);
                }

                return Task.FromResult(JsonConvert.SerializeObject(response, Formatting.Indented));
            }
            catch (Exception ex)
            {
                return Task.FromResult($"Error: {ex.Message}");
            }
        }

        // ---- helpers ----------------------------------------------------------

        // Accept a JSON array of strings (house convention). To be forgiving of a
        // single bare code passed without array brackets, fall back to treating the
        // whole string as one code — but never silently swallow: a blank stays blank
        // and is reported per-code as not found.
        private static List<string> ParseCodeList(string codes)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(codes)) return list;

            JToken parsed = null;
            try { parsed = JToken.Parse(codes); } catch { parsed = null; }

            if (parsed is JArray arr)
            {
                foreach (var t in arr)
                {
                    if (t.Type == JTokenType.Null) continue;
                    list.Add(t.ToString());
                }
            }
            else if (parsed is JValue val && val.Type == JTokenType.String)
            {
                list.Add(val.ToString());
            }
            else
            {
                // Not JSON at all — treat the raw argument as a single code token.
                list.Add(codes.Trim());
            }
            return list;
        }

        // =====================================================================
        // Catalog — parse once per call, derive leaf-ness from the parent column.
        // =====================================================================
        private sealed class Catalog
        {
            private sealed class Row
            {
                public string Code;
                public string Title;
                public string Parent;
                public int Line;
            }

            private readonly List<Row> _rows;
            private readonly Dictionary<string, Row> _byCode;   // first occurrence wins
            private readonly HashSet<string> _parents;          // every code named as a parent

            public JObject Sources { get; }
            public List<JToken> Warnings { get; }

            private Catalog(List<Row> rows, Dictionary<string, Row> byCode, HashSet<string> parents,
                            JObject sources, List<JToken> warnings)
            {
                _rows = rows;
                _byCode = byCode;
                _parents = parents;
                Sources = sources;
                Warnings = warnings;
            }

            public static Catalog Load(string path)
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full))
                    throw new FileNotFoundException(
                        $"Catalog file not found: {full}. Refusing to validate against a catalog that is not there.");

                byte[] bytes = File.ReadAllBytes(full); // throws on unreadable -> caught upstream as Error
                var lastWriteUtc = File.GetLastWriteTimeUtc(full);

                string sha256;
                using (var sha = SHA256.Create())
                    sha256 = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();

                // Decode UTF-8, stripping a BOM if present. Do not let the encoder emit one.
                var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                int start = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
                string text = enc.GetString(bytes, start, bytes.Length - start);

                var rows = new List<Row>();
                var byCode = new Dictionary<string, Row>(StringComparer.Ordinal);
                var parents = new HashSet<string>(StringComparer.Ordinal);
                var duplicates = new List<JToken>();
                int malformed = 0;
                var malformedLines = new List<int>();

                int lineNo = 0;
                foreach (var rawLine in text.Split('\n'))
                {
                    lineNo++;
                    var line = rawLine.EndsWith("\r", StringComparison.Ordinal)
                        ? rawLine.Substring(0, rawLine.Length - 1)
                        : rawLine;
                    if (line.Length == 0) continue; // blank line, not a row

                    var parts = line.Split('\t');
                    // A row needs at least a CODE and a TITLE field. Fewer tabs, or an
                    // empty code, is a structural surprise — count it, name it, keep going.
                    if (parts.Length < 2 || parts[0].Trim().Length == 0)
                    {
                        malformed++;
                        if (malformedLines.Count < 20) malformedLines.Add(lineNo);
                        continue;
                    }

                    var code = parts[0].Trim();
                    var title = parts.Length > 1 ? parts[1] : string.Empty;
                    var parent = parts.Length > 2 ? parts[2].Trim() : string.Empty;

                    var row = new Row { Code = code, Title = title, Parent = parent, Line = lineNo };
                    rows.Add(row);

                    if (!byCode.ContainsKey(code))
                    {
                        byCode[code] = row;
                    }
                    else
                    {
                        duplicates.Add(new JObject
                        {
                            ["code"] = code,
                            ["first_line"] = byCode[code].Line,
                            ["duplicate_line"] = lineNo
                        });
                    }

                    if (parent.Length > 0) parents.Add(parent);
                }

                if (rows.Count == 0)
                    throw new InvalidDataException(
                        $"Parsed 0 rows from {full} ({malformed} malformed line(s)). Expected tab-separated " +
                        "CODE<TAB>TITLE<TAB>PARENT_CODE. Refusing to report a clean empty catalog.");

                // Count over the deduplicated first-occurrence set (byCode), not the raw
                // rows list, so a duplicated CODE is one distinct code — matching the
                // 'lookups use the FIRST occurrence' semantics the duplicate warning promises.
                int distinctCount = byCode.Count;
                int leafCount = byCode.Values.Count(r => !parents.Contains(r.Code));

                var sources = new JObject
                {
                    ["path"] = full,
                    ["last_write_utc"] = lastWriteUtc.ToString("o"),
                    ["sha256"] = sha256,
                    ["rows_parsed"] = rows.Count,
                    ["distinct_codes"] = distinctCount,
                    ["leaf_count"] = leafCount,
                    ["chapter_count"] = distinctCount - leafCount
                };

                var warnings = new List<JToken>();
                if (duplicates.Count > 0)
                {
                    warnings.Add(new JObject
                    {
                        ["kind"] = "duplicate_codes",
                        ["message"] = $"{duplicates.Count} row(s) repeat a CODE already seen — a catalog defect; " +
                                       "lookups use the FIRST occurrence.",
                        ["count"] = duplicates.Count,
                        ["items"] = new JArray(duplicates.Take(50))
                    });
                }
                if (malformed > 0)
                {
                    warnings.Add(new JObject
                    {
                        ["kind"] = "malformed_rows",
                        ["message"] = $"{malformed} non-blank line(s) did not parse as CODE<TAB>TITLE<TAB>PARENT " +
                                       "and were skipped.",
                        ["count"] = malformed,
                        ["sample_lines"] = new JArray(malformedLines.Cast<object>())
                    });
                }

                return new Catalog(rows, byCode, parents, sources, warnings);
            }

            private bool IsLeaf(string code) => !_parents.Contains(code);

            // Count distinct child codes (first occurrence wins), never duplicate rows.
            private int ChildrenCount(string code) => _byCode.Values.Count(r => r.Parent == code);

            private static int Segments(string code) =>
                string.IsNullOrEmpty(code) ? 0 : code.Split('-').Length;

            // Walk code -> parent -> ... up to a root (empty parent). Returns root-first.
            // reaches_root is false if the chain dead-ends on a parent that is not a row
            // (a dangling reference — another catalog defect worth surfacing).
            private JObject Breadcrumb(string code, out bool reachesRoot)
            {
                var chain = new List<Row>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var cur = code;
                reachesRoot = false;

                while (!string.IsNullOrEmpty(cur) && _byCode.TryGetValue(cur, out var r) && seen.Add(cur))
                {
                    chain.Add(r);
                    if (string.IsNullOrEmpty(r.Parent)) { reachesRoot = true; break; }
                    cur = r.Parent;
                }
                chain.Reverse(); // root first

                var arr = new JArray();
                foreach (var r in chain)
                    arr.Add(new JObject { ["code"] = r.Code, ["title"] = r.Title });

                return new JObject { ["path"] = arr, ["reaches_root"] = reachesRoot };
            }

            public JObject Validate(string code)
            {
                var result = new JObject { ["code"] = code };

                if (string.IsNullOrEmpty(code))
                {
                    result["exists"] = false;
                    result["is_leaf"] = null;
                    result["assignable"] = false;
                    result["reason"] = "empty_code";
                    result["segments"] = 0;
                    return result;
                }

                var exists = _byCode.TryGetValue(code, out var row);
                result["exists"] = exists;
                result["segments"] = Segments(code);

                if (!exists)
                {
                    result["is_leaf"] = null;   // undecidable for a code that is not a row
                    result["assignable"] = false;
                    result["title"] = null;
                    result["children_count"] = 0;
                    result["reason"] = "not_found";
                    return result;
                }

                var leaf = IsLeaf(code);
                var children = ChildrenCount(code);
                result["is_leaf"] = leaf;
                result["assignable"] = leaf;
                result["title"] = row.Title;
                result["children_count"] = children;
                result["reason"] = leaf
                    ? "ok_leaf"
                    : $"is_chapter_parent_of_{children}";

                var bc = Breadcrumb(code, out _);
                result["breadcrumb"] = bc["path"];
                result["breadcrumb_reaches_root"] = bc["reaches_root"];
                return result;
            }

            public JObject SearchLeaves(string term, int cap)
            {
                // Iterate the deduplicated first-occurrence set so a duplicated leaf CODE is
                // matched, returned, and counted exactly once — honoring the same 'first
                // occurrence wins' rule the duplicate warning promises and validate obeys.
                var matches = _byCode.Values
                    .Where(r => IsLeaf(r.Code) &&
                                (r.Code.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (r.Title ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                var returned = new JArray();
                foreach (var r in matches.Take(cap))
                {
                    returned.Add(new JObject
                    {
                        ["code"] = r.Code,
                        ["title"] = r.Title,
                        ["segments"] = Segments(r.Code)
                    });
                }

                return new JObject
                {
                    ["term"] = term,
                    ["leaves_only"] = true,
                    ["total_leaf_matches"] = matches.Count,
                    ["returned"] = returned.Count,
                    ["truncated"] = matches.Count > returned.Count,
                    ["limit"] = cap,
                    ["matches"] = returned
                };
            }
        }
    }
}
