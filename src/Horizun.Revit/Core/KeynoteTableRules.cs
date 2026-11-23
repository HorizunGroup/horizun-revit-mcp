// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The Revit-free half of the keynote table: read a keynote file (key <TAB> text
// [<TAB> parent]) the way Revit does and say what is wrong with it BEFORE a table
// is loaded or a code is written. Revit loads a file with a duplicated key or a
// parent that does not exist and only shows the damage later, in the Keynote
// dialog or in a tag that reads "?"; a code assigned off such a table is a budget
// line that silently does not join. So the diagnosis is a gate, not a report.
//
// No Autodesk types: compiled into the Core tests as well as into the add-in.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class KeynoteTableRules
    {
        public sealed class Entry
        {
            public string Key;
            public string Text;
            public string Parent;
            public int Line;
        }

        public sealed class Diagnosis
        {
            public string Encoding;
            public readonly List<Entry> Entries = new List<Entry>();
            public readonly Dictionary<string, Entry> ByKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public int DuplicateIdentical;
            public int DuplicateConflicting;
            public int MissingParents;
            public int ParentAfterChild;
            public int Cycles;
            public bool Readable;

            public bool Ok => Readable && Errors.Count == 0;

            public JObject ToJson(int listLimit = 20)
            {
                return new JObject
                {
                    ["ok"] = Ok,
                    ["readable"] = Readable,
                    ["encoding"] = Encoding,
                    ["entries"] = ByKey.Count,
                    ["rows"] = Entries.Count,
                    ["duplicate_identical"] = DuplicateIdentical,
                    ["duplicate_conflicting"] = DuplicateConflicting,
                    ["missing_parents"] = MissingParents,
                    ["parent_after_child"] = ParentAfterChild,
                    ["cycles"] = Cycles,
                    ["errors"] = new JArray(Errors.Take(listLimit)),
                    ["errors_total"] = Errors.Count,
                    ["warnings"] = new JArray(Warnings.Take(listLimit)),
                    ["warnings_total"] = Warnings.Count
                };
            }
        }

        /// <summary>
        /// Decode the bytes as Revit writes keynote files: UTF-16 with a BOM (what Revit
        /// and Excel "Unicode text" produce), UTF-8 with or without BOM, else the ANSI code
        /// page - and SAY which one, because a misread file is a table of codes that never match.
        /// </summary>
        public static string Decode(byte[] bytes, out string encoding)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            { encoding = "utf-16le"; return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            { encoding = "utf-16be"; return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); }
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            try
            {
                string s = new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
                encoding = start == 3 ? "utf-8-bom" : "utf-8";
                return s;
            }
            catch (DecoderFallbackException)
            {
                encoding = "latin-1 (not valid UTF-8 or UTF-16; accented text may be misread)";
                var sb = new StringBuilder(bytes.Length);
                for (int i = start; i < bytes.Length; i++) sb.Append((char)bytes[i]);
                return sb.ToString();
            }
        }

        public static Diagnosis Diagnose(byte[] bytes)
        {
            var d = new Diagnosis();
            string text;
            try { text = Decode(bytes, out d.Encoding); }
            catch (Exception ex) { d.Errors.Add("The file could not be decoded: " + ex.Message); return d; }
            if (text.IndexOf('\0') >= 0)
            {
                d.Errors.Add("The file contains NUL characters: it is UTF-16 without a byte-order mark or not a text file. Save it as Unicode text (UTF-16 with BOM) or UTF-8.");
                return d;
            }
            d.Readable = true;
            if (d.Encoding.StartsWith("latin-1", StringComparison.Ordinal))
                d.Warnings.Add("The file is neither UTF-8 nor UTF-16; it was read as latin-1. Accented descriptions may not match what Revit shows.");

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var firstLine = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw.Trim().Length == 0) continue;
                string[] cells = raw.Split('\t');
                var e = new Entry
                {
                    Key = cells[0].Trim(),
                    Text = cells.Length > 1 ? cells[1].Trim() : "",
                    Parent = cells.Length > 2 ? cells[2].Trim() : "",
                    Line = i + 1
                };
                if (e.Key.Length == 0) { d.Errors.Add("Line " + e.Line + ": empty key."); continue; }
                if (cells.Length < 2) d.Warnings.Add("Line " + e.Line + ": no tab after key '" + e.Key + "' (no description).");
                if (e.Parent == e.Key) { d.Errors.Add("Line " + e.Line + ": key '" + e.Key + "' is its own parent."); d.Cycles++; continue; }
                d.Entries.Add(e);
                if (d.ByKey.TryGetValue(e.Key, out Entry prior))
                {
                    if (prior.Text == e.Text && prior.Parent == e.Parent)
                    {
                        d.DuplicateIdentical++;
                        d.Warnings.Add("Line " + e.Line + ": key '" + e.Key + "' repeats line " + prior.Line + " identically.");
                    }
                    else
                    {
                        d.DuplicateConflicting++;
                        d.Errors.Add("Line " + e.Line + ": key '" + e.Key + "' repeats line " + prior.Line +
                                     " with a different description or parent; Revit keeps one of them and the other code meaning is lost.");
                    }
                    continue;
                }
                d.ByKey[e.Key] = e;
                firstLine[e.Key] = e.Line;
            }

            foreach (Entry e in d.ByKey.Values)
            {
                if (e.Parent.Length == 0) continue;
                if (!d.ByKey.TryGetValue(e.Parent, out Entry parent))
                {
                    d.MissingParents++;
                    d.Errors.Add("Line " + e.Line + ": key '" + e.Key + "' names parent '" + e.Parent + "', which is not in the file.");
                    continue;
                }
                if (parent.Line > e.Line)
                {
                    d.ParentAfterChild++;
                    d.Warnings.Add("Line " + e.Line + ": key '" + e.Key + "' comes before its parent '" + e.Parent + "' (line " + parent.Line + ").");
                }
            }

            // A cycle (A -> B -> A) makes the tree unreadable in the Keynote dialog.
            var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = on path, 2 = done
            foreach (string start in d.ByKey.Keys)
            {
                var path = new List<string>();
                string k = start;
                while (k.Length > 0 && d.ByKey.TryGetValue(k, out Entry cur))
                {
                    if (state.TryGetValue(k, out int s))
                    {
                        if (s == 1) { d.Cycles++; d.Errors.Add("Parent cycle through key '" + k + "'."); }
                        break;
                    }
                    state[k] = 1; path.Add(k);
                    k = cur.Parent;
                }
                foreach (string p in path) state[p] = 2;
            }
            if (d.ByKey.Count == 0) d.Errors.Add("The file has no keynote entries.");
            return d;
        }
    }
}
