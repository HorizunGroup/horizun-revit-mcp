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
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    /// <summary>The verdict on one code: does it exist, and is it a leaf (unknown when absent).</summary>
    internal struct LeafOutcome
    {
        public bool Exists;
        public bool? IsLeaf;   // null == unknown, reserved for "not in the catalog" — never a stand-in for false.
    }

    internal static class CatalogLookup
    {
        // When the caller gives no explicit separator, a code is treated as opaque and a
        // descendant is any OTHER code that begins with it followed by one of these common
        // hierarchy delimiters. This is the documented default rule; pass 'separator' to
        // pin it to exactly one delimiter.
        private static readonly string[] DefaultDelimiters = { "-", ".", "_", "/", " " };

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
        /// Parse codes from CSV text: one code per line, taking the FIRST comma-separated
        /// field of each non-blank line, trimmed and with a single pair of surrounding
        /// double quotes removed. Deliberately simple (not full RFC-4180); this is the
        /// documented rule. Every non-blank line is a code — no header row is assumed or
        /// skipped, because guessing which line is a header would drop real data silently.
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

        /// <summary>
        /// The host handler. Reads the catalog file the caller named, parses its codes,
        /// applies the pure leaf rule and returns an auditable answer. Genuine I/O failure
        /// (missing/unreadable file) throws — the caller sees an error, not a fabricated
        /// verdict. A code simply not in the catalog is NOT a failure: it is exists=false /
        /// is_leaf=null, the honest unknown.
        /// </summary>
        internal static JObject Handle(JObject args)
        {
            string catalogPath = (string)args?["catalog_path"];
            string code = (string)args?["code"];
            string separator = (string)args?["separator"];

            if (string.IsNullOrEmpty(catalogPath))
                throw new ArgumentException("catalog_path is required.");
            if (code == null)
                throw new ArgumentException("code is required.");
            if (!File.Exists(catalogPath))
                throw new FileNotFoundException("Catalog file not found: " + catalogPath);

            byte[] bytes = File.ReadAllBytes(catalogPath);
            string sha = Sha256Hex(bytes);
            string encodingUsed;
            string text = DecodeCatalog(StripBom(bytes), out encodingUsed);

            List<string> parsed = ParseCodes(text);
            var set = new HashSet<string>(parsed, StringComparer.Ordinal);

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
                ["row_count"] = parsed.Count,
                ["distinct_code_count"] = set.Count,
                ["encoding_used"] = encodingUsed,
                ["sha256"] = sha,
                ["note"] = note
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
