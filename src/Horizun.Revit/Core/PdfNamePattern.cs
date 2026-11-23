// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_export format=pdf: FILE NAMES FROM A PATTERN.
//
// An office names its sheets "A101_20261018_v1.pdf", not "set-001-123456.pdf", and
// renaming forty files by hand after every issue is where the wrong revision gets
// sent. pdf_name_pattern builds the name from the sheet itself:
//
//   {sheet_number} {sheet_name} {view_name}  read from the view/sheet being exported
//   {date}                                   yyyyMMdd, local date of the export
//   {version}                                pdf_version, an integer, no padding
//   {index}                                  1-based position in view_ids
//
// With pdf_combine=false the pattern is applied per file; with true, the FIRST view
// names the single file. The folder always comes from output_path.
//
// Every refusal happens before anything is exported: an unknown token, a sheet token
// on a view that is not a sheet (an empty {sheet_number} would make every name
// collide or lie), a name that sanitizes to nothing, and - the one that loses data -
// two views resolving to the SAME file name, which would have the second export
// overwrite the first inside a single call. Characters Windows does not accept in a
// file name become '_', and the reply reports the names actually used.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>The facts a PDF file name may be built from, read from one exported view.</summary>
    public sealed class PdfNameSource
    {
        public long ViewId { get; set; }
        public string ViewName { get; set; }
        /// <summary>Null when the view is not a sheet.</summary>
        public string SheetNumber { get; set; }
        /// <summary>Null when the view is not a sheet.</summary>
        public string SheetName { get; set; }
    }

    public static class PdfNamePattern
    {
        public static readonly string[] Tokens = { "sheet_number", "sheet_name", "view_name", "date", "version", "index" };

        public const int MaxStemLength = 200;

        // Windows' own list, fixed here rather than read from Path.GetInvalidFileNameChars(),
        // which on a non-Windows test host returns only '/' and '\0' - and the files land on
        // Windows, where Revit runs.
        private static readonly char[] Invalid = "<>:\"/\\|?*".ToCharArray();

        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// The full paths the export will produce, in view order (one when combined).
        /// Throws ArgumentException with the reason when the pattern cannot name every
        /// file uniquely; nothing has been exported at that point.
        /// </summary>
        public static string[] Paths(string folder, string pattern, IList<PdfNameSource> views, bool combine,
                                     DateTime localNow, int version)
        {
            if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("pdf_name_pattern is empty.");
            if (views == null || views.Count == 0) throw new ArgumentException("PDF requires at least one view_id.");
            if (version < 0) throw new ArgumentException("pdf_version must be 0 or greater.");
            string p = pattern.Trim();
            if (p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
            Validate(p);

            int count = combine ? 1 : views.Count;
            var stems = new string[count];
            for (int i = 0; i < count; i++)
                stems[i] = Stem(p, views[i], i + 1, localNow, version);

            List<string> dupes = stems.GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                                      .Where(g => g.Count() > 1).Select(g => g.Key + ".pdf").ToList();
            if (dupes.Count > 0)
                throw new ArgumentException("pdf_name_pattern '" + pattern + "' gives several views the same file name (" +
                    string.Join(", ", dupes) + "); the later export would overwrite the earlier one. Add {sheet_number}, " +
                    "{view_name} or {index}. Nothing was exported.");
            return stems.Select(s => Path.Combine(folder ?? "", s + ".pdf")).ToArray();
        }

        /// <summary>Unknown tokens and unbalanced braces refuse, by name.</summary>
        private static void Validate(string p)
        {
            int i = 0;
            while (i < p.Length)
            {
                char c = p[i];
                if (c == '}') throw new ArgumentException("pdf_name_pattern has a '}' without its '{' at position " + i + ".");
                if (c != '{') { i++; continue; }
                int close = p.IndexOf('}', i + 1);
                if (close < 0) throw new ArgumentException("pdf_name_pattern has a '{' without its '}' at position " + i + ".");
                string token = p.Substring(i + 1, close - i - 1);
                if (!Tokens.Contains(token, StringComparer.Ordinal))
                    throw new ArgumentException("pdf_name_pattern token '{" + token + "}' is unknown. Known: " +
                        string.Join(" ", Tokens.Select(t => "{" + t + "}")) + ".");
                i = close + 1;
            }
        }

        private static string Stem(string p, PdfNameSource v, int index, DateTime localNow, int version)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < p.Length)
            {
                if (p[i] != '{') { sb.Append(p[i]); i++; continue; }
                int close = p.IndexOf('}', i + 1);
                string token = p.Substring(i + 1, close - i - 1);
                sb.Append(Value(token, v, index, localNow, version));
                i = close + 1;
            }
            string stem = Sanitize(sb.ToString());
            if (stem.Length == 0)
                throw new ArgumentException("pdf_name_pattern resolves to an empty file name for view " + v.ViewId + ".");
            if (stem.Length > MaxStemLength)
                throw new ArgumentException("pdf_name_pattern resolves to a " + stem.Length + "-character name for view " +
                    v.ViewId + "; the limit is " + MaxStemLength + ".");
            return stem;
        }

        private static string Value(string token, PdfNameSource v, int index, DateTime localNow, int version)
        {
            switch (token)
            {
                case "sheet_number":
                case "sheet_name":
                    string s = token == "sheet_number" ? v.SheetNumber : v.SheetName;
                    if (s == null)
                        throw new ArgumentException("pdf_name_pattern uses {" + token + "} but view " + v.ViewId + " ('" +
                            v.ViewName + "') is not a sheet. Use {view_name} for views. Nothing was exported.");
                    return s;
                case "view_name": return v.ViewName ?? "";
                case "date": return localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                case "version": return version.ToString(CultureInfo.InvariantCulture);
                case "index": return index.ToString(CultureInfo.InvariantCulture);
                default: throw new ArgumentException("pdf_name_pattern token '{" + token + "}' is unknown.");
            }
        }

        /// <summary>
        /// Characters Windows refuses in a file name become '_'; control characters too.
        /// Trailing dots and spaces are trimmed (Windows drops them silently, so the file
        /// would not be the one reported) and a reserved device name gets a '_' prefix.
        /// </summary>
        public static string Sanitize(string name)
        {
            if (name == null) return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(c < 32 || Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim().TrimEnd('.', ' ');
            string device = s.Split('.')[0];
            if (Reserved.Contains(device)) s = "_" + s;
            return s;
        }
    }
}
