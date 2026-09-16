// -----------------------------------------------------------------------------
// Horizun Revit MCP - reading an IFC file, without an IFC library.
// Original Horizun code.
//
// G19 of the 2026-09-14 competitive inventory needs to READ an IFC before it can
// say anything true about reconstructing one. This is a STEP Physical File (ISO
// 10303-21) tokenizer: the format IFC ships in, which is a text format with a
// small, fully specified grammar.
//
// WHY NOT A LIBRARY. Every option is a large dependency that would ship inside a
// Revit add-in, run in Revit's process, and carry its own licence and its own
// failure modes. The DATA section's grammar is a few hundred lines to read
// correctly, and reading it here means the bridge's IFC support fails in ways
// this repository can explain. It is also why the scope is stated rather than
// implied: this reads entity instances and their attributes. It does not
// evaluate geometry, does not resolve inverse relationships, and does not
// validate against an EXPRESS schema.
//
// THE PARSING DECISIONS THAT MATTER, each one a place where a lazy reader
// silently produces wrong data rather than failing:
//
//   STRINGS. Single-quoted, with '' as an escaped quote, and \X2\...\X0\ /
//   \S\ encodings for non-ASCII. A reader that splits on commas without knowing
//   it is inside a string will cut a wall named "Muro, tipo A" in half and index
//   the halves as two attributes.
//
//   NESTING. Attribute lists nest arbitrarily: (1.,0.,0.) inside
//   IFCDIRECTION((1.,0.,0.)). Depth is tracked, so a top-level split is a
//   top-level split.
//
//   $ AND *. Unset and derived are DIFFERENT from an empty string and from zero,
//   and both are preserved as themselves. A reader that turns $ into "" makes an
//   absent name indistinguishable from a blank one.
//
//   COMMENTS. /* ... */ may appear anywhere, including inside the parameter list.
//
// Revit-free, so the grammar is provable without a building - which is the only
// way this can be trusted, since the files that break it will arrive from other
// people's software.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>One entity instance: #id = TYPE(attributes...).</summary>
    public sealed class IfcEntity
    {
        public int Id;
        public string Type;
        public IReadOnlyList<string> Attributes;

        /// <summary>The raw attribute at this position, or null when there is none.</summary>
        public string At(int index) =>
            Attributes != null && index >= 0 && index < Attributes.Count ? Attributes[index] : null;
    }

    public static class IfcStepReader
    {
        /// <summary>A file larger than this is refused rather than loaded into Revit's process.</summary>
        public const long MaxBytes = 512L * 1024 * 1024;

        public sealed class Document
        {
            public string SchemaIdentifier;
            public readonly Dictionary<int, IfcEntity> ById = new Dictionary<int, IfcEntity>();
            public readonly Dictionary<string, List<IfcEntity>> ByType =
                new Dictionary<string, List<IfcEntity>>(StringComparer.OrdinalIgnoreCase);

            public IfcEntity Resolve(string reference)
            {
                int id = ReferenceId(reference);
                IfcEntity entity;
                return id > 0 && ById.TryGetValue(id, out entity) ? entity : null;
            }

            public IReadOnlyList<IfcEntity> Of(string type)
            {
                List<IfcEntity> found;
                return ByType.TryGetValue(type, out found) ? found : new List<IfcEntity>();
            }
        }

        /// <summary>Read a file. Returns null with a reason rather than throwing at a caller.</summary>
        public static Document Read(string path, out string error)
        {
            error = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { error = "no file at '" + path + "'."; return null; }
                if (info.Length > MaxBytes)
                {
                    error = "'" + path + "' is " + (info.Length / (1024 * 1024)) + " MB; the bound is " +
                            (MaxBytes / (1024 * 1024)) + " MB. This runs inside Revit's own process, and a " +
                            "file that exhausts its memory takes the model down with it.";
                    return null;
                }
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                    return Parse(reader.ReadToEnd(), out error);
            }
            catch (Exception ex)
            {
                error = "'" + path + "' could not be read: " + ex.Message;
                return null;
            }
        }

        public static Document Parse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "the file is empty."; return null; }

            var document = new Document();
            int header = text.IndexOf("HEADER", StringComparison.OrdinalIgnoreCase);
            int dataStart = text.IndexOf("DATA", StringComparison.OrdinalIgnoreCase);
            if (header < 0 || dataStart < 0)
            {
                error = "this is not a STEP physical file: it has no HEADER and no DATA section. An IFC in " +
                        "ifcXML or ifcZIP form is a different format and is not read here.";
                return null;
            }

            document.SchemaIdentifier = SchemaOf(text.Substring(header, Math.Max(0, dataStart - header)));

            int position = dataStart;
            while (position < text.Length)
            {
                int hash = text.IndexOf('#', position);
                if (hash < 0) break;

                int equals = text.IndexOf('=', hash);
                if (equals < 0) break;

                int id;
                if (!int.TryParse(text.Substring(hash + 1, equals - hash - 1).Trim(),
                                  NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                {
                    position = hash + 1;
                    continue;
                }

                int open = text.IndexOf('(', equals);
                if (open < 0) break;
                string type = text.Substring(equals + 1, open - equals - 1).Trim();

                int close = MatchingParenthesis(text, open);
                if (close < 0)
                {
                    error = "entity #" + id + " (" + type + ") has an unterminated parameter list. The file is " +
                            "truncated or malformed, and nothing beyond that point can be trusted, so nothing " +
                            "is returned.";
                    return null;
                }

                var entity = new IfcEntity
                {
                    Id = id,
                    Type = type.ToUpperInvariant(),
                    Attributes = SplitTopLevel(text.Substring(open + 1, close - open - 1))
                };
                document.ById[id] = entity;

                List<IfcEntity> bucket;
                if (!document.ByType.TryGetValue(entity.Type, out bucket))
                {
                    bucket = new List<IfcEntity>();
                    document.ByType[entity.Type] = bucket;
                }
                bucket.Add(entity);

                position = close + 1;
            }

            if (document.ById.Count == 0)
            {
                error = "the DATA section holds no entity instance.";
                return null;
            }
            return document;
        }

        // =====================================================================
        // Grammar
        // =====================================================================

        /// <summary>
        /// The index of the ')' matching the '(' at <paramref name="open"/>, honouring
        /// strings and comments. Naive bracket counting is wrong the moment a name has a
        /// parenthesis in it, which names do.
        /// </summary>
        private static int MatchingParenthesis(string text, int open)
        {
            int depth = 0;
            bool inString = false;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c != '\'') continue;
                    // '' is an escaped quote, not the end of the string.
                    if (i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                    inString = false;
                    continue;
                }
                if (c == '\'') { inString = true; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) return -1;
                    i = end + 1;
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Split a parameter list on TOP-LEVEL commas only. Nested lists, strings and
        /// comments are passed through whole.
        /// </summary>
        public static IReadOnlyList<string> SplitTopLevel(string body)
        {
            var parts = new List<string>();
            if (body == null) return parts;

            var current = new StringBuilder();
            int depth = 0;
            bool inString = false;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (inString)
                {
                    current.Append(c);
                    if (c != '\'') continue;
                    if (i + 1 < body.Length && body[i + 1] == '\'') { current.Append('\''); i++; continue; }
                    inString = false;
                    continue;
                }
                switch (c)
                {
                    case '\'':
                        inString = true;
                        current.Append(c);
                        break;
                    case '(':
                        depth++;
                        current.Append(c);
                        break;
                    case ')':
                        depth--;
                        current.Append(c);
                        break;
                    case '/':
                        if (i + 1 < body.Length && body[i + 1] == '*')
                        {
                            int end = body.IndexOf("*/", i + 2, StringComparison.Ordinal);
                            i = end < 0 ? body.Length : end + 1;
                            break;
                        }
                        current.Append(c);
                        break;
                    case ',':
                        if (depth == 0) { parts.Add(current.ToString().Trim()); current.Clear(); }
                        else current.Append(c);
                        break;
                    default:
                        current.Append(c);
                        break;
                }
            }
            if (current.Length > 0 || parts.Count > 0) parts.Add(current.ToString().Trim());
            return parts;
        }

        // =====================================================================
        // Readers for the shapes that matter
        // =====================================================================

        /// <summary>The integer of a "#123" reference, or 0.</summary>
        public static int ReferenceId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string trimmed = raw.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '#') return 0;
            int id;
            return int.TryParse(trimmed.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
                ? id : 0;
        }

        /// <summary>
        /// A quoted string, decoded. $ becomes null - unset is not the empty string, and
        /// a reader that conflates them makes an absent name look like a blank one.
        /// </summary>
        public static string Text(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string trimmed = raw.Trim();
            if (trimmed == "$" || trimmed == "*") return null;
            if (trimmed.Length < 2 || trimmed[0] != '\'' || trimmed[trimmed.Length - 1] != '\'') return trimmed;

            string inner = trimmed.Substring(1, trimmed.Length - 2).Replace("''", "'");
            return DecodeIso10303(inner);
        }

        /// <summary>
        /// ISO 10303-21 string encodings. \X2\....\X0\ carries UTF-16 code units in hex;
        /// \X\41 carries one byte. A file from a Spanish project is full of these, and a
        /// reader that leaves them raw reports family names nobody can search for.
        /// </summary>
        private static string DecodeIso10303(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\') { sb.Append(value[i]); continue; }

                if (i + 3 < value.Length && value[i + 1] == 'X' && value[i + 2] == '2' && value[i + 3] == '\\')
                {
                    int end = value.IndexOf("\\X0\\", i + 4, StringComparison.OrdinalIgnoreCase);
                    string hex = end < 0 ? value.Substring(i + 4) : value.Substring(i + 4, end - i - 4);
                    for (int h = 0; h + 3 < hex.Length + 1 && h + 4 <= hex.Length; h += 4)
                    {
                        int code;
                        if (int.TryParse(hex.Substring(h, 4), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                    }
                    i = end < 0 ? value.Length : end + 3;
                    continue;
                }
                if (i + 2 < value.Length && value[i + 1] == 'X' && value[i + 2] == '\\')
                {
                    if (i + 4 < value.Length)
                    {
                        int code;
                        if (int.TryParse(value.Substring(i + 3, 2), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 4;
                        continue;
                    }
                }
                sb.Append(value[i]);
            }
            return sb.ToString();
        }

        /// <summary>A REAL or INTEGER attribute, or null when unset.</summary>
        public static double? Number(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string trimmed = raw.Trim();
            if (trimmed == "$" || trimmed == "*") return null;
            double value;
            return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : (double?)null;
        }

        /// <summary>An ENUMERATION attribute (.ELEMENT.) without its dots, or null.</summary>
        public static string Enumeration(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string trimmed = raw.Trim();
            if (trimmed.Length < 3 || trimmed[0] != '.' || trimmed[trimmed.Length - 1] != '.') return null;
            return trimmed.Substring(1, trimmed.Length - 2);
        }

        /// <summary>The members of a list attribute, unsplit further.</summary>
        public static IReadOnlyList<string> List(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            string trimmed = raw.Trim();
            if (trimmed == "$" || trimmed.Length < 2 || trimmed[0] != '(' || trimmed[trimmed.Length - 1] != ')')
                return new List<string>();
            return SplitTopLevel(trimmed.Substring(1, trimmed.Length - 2));
        }

        /// <summary>The index of the ')' that closes the '(' at <paramref name="open"/>, skipping quoted text; -1 if none.</summary>
        private static int MatchingClose(string text, int open)
        {
            int depth = 0;
            bool quoted = false;
            for (int i = open; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') i++;   // '' is an escaped quote
                        else quoted = false;
                    }
                    continue;
                }
                if (ch == '\'') quoted = true;
                else if (ch == '(') depth++;
                else if (ch == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static string SchemaOf(string header)
        {
            int schema = header.IndexOf("FILE_SCHEMA", StringComparison.OrdinalIgnoreCase);
            if (schema < 0) return null;
            int open = header.IndexOf('(', schema);
            // THE PARENTHESIS THAT CLOSES THIS ONE, not the first ')' after it:
            // FILE_SCHEMA(('IFC4')) nests, and cutting at the first ')' read the
            // schema as "('IFC4'" - which no schema family matches, so every
            // specification in every file became not decidable.
            int close = open < 0 ? -1 : MatchingClose(header, open);
            if (open < 0 || close < 0) return null;
            foreach (string part in SplitTopLevel(header.Substring(open + 1, close - open - 1)))
            {
                foreach (string member in List(part))
                {
                    string value = Text(member);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                string direct = Text(part);
                if (!string.IsNullOrWhiteSpace(direct)) return direct;
            }
            return null;
        }
    }
}
