// -----------------------------------------------------------------------------
// Horizun MCP server - what a -32700 says about the line it could not read.
// Original Horizun code.
//
// MEASURED 2026-09-24. The server log held 132 "parse error answered" lines over
// five days and not one of them said where the line came from or what shape it
// had: "Expected ':' but got: i. Path '', line 1, position 6." Every one turned
// out to be this repository's own wire tests sending "{this is not json" and
// "{not json at all" on purpose - but proving that took reading the lines around
// each entry, because the entry itself carried nothing a person could act on.
//
// So a parse error now carries, to the caller and to the log:
//   * WHERE: line, column and the zero-based character offset of the failure;
//   * WHAT KIND: a short hint code for the causes that actually happen on a
//     stdio JSON-RPC stream (an unescaped Windows path, two messages on one
//     line, a message cut by a raw newline, a byte-order mark, a bare word);
//   * A SAFE FRAGMENT: a window around the offset in which every letter becomes
//     'a', every digit '0' and everything non-ASCII '?'. Quotes, backslashes,
//     braces, colons and commas - the characters that decide whether JSON parses -
//     survive; the words, numbers, paths and tokens the caller sent do not. The
//     rule the original handler stated still holds: the content is not echoed.
//
// The parser's own message is reduced to its first clause for the same reason:
// Newtonsoft appends "Path 'x'" (a property NAME from the caller's payload) and
// sometimes the offending character itself.
// -----------------------------------------------------------------------------
using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal sealed class ParseErrorDiagnosis
    {
        public const int WindowBefore = 20;
        public const int WindowAfter = 20;

        public const string HintByteOrderMark = "byte_order_mark";
        public const string HintConcatenated = "concatenated_messages";
        public const string HintUnescapedBackslash = "unescaped_backslash";
        public const string HintTruncated = "truncated_message";
        public const string HintBareWord = "bare_word";
        public const string HintNotJson = "not_json";
        public const string HintInvalid = "invalid_json";

        public int Line { get; private set; }
        public int Column { get; private set; }
        /// <summary>Zero-based character offset of the failure inside the received line.</summary>
        public int Offset { get; private set; }
        public int Length { get; private set; }
        public string Hint { get; private set; }
        public string Reason { get; private set; }
        /// <summary>The masked window. Never contains a letter or digit the caller sent.</summary>
        public string Shape { get; private set; }
        /// <summary>Index inside <see cref="Shape"/> of the character at <see cref="Offset"/>.</summary>
        public int ShapeCaret { get; private set; }

        public static ParseErrorDiagnosis Of(string line, Exception ex)
        {
            line = line ?? "";
            var d = new ParseErrorDiagnosis { Length = line.Length, Line = 1, Column = 0 };

            var jre = ex as JsonReaderException;
            if (jre != null)
            {
                d.Line = Math.Max(1, jre.LineNumber);
                d.Column = Math.Max(0, jre.LinePosition);
            }
            d.Offset = OffsetOf(line, d.Line, d.Column);
            d.Reason = FirstClause(ex?.Message);
            d.Hint = Classify(line, ex?.Message ?? "", d.Offset);
            int caret;
            d.Shape = MaskedWindow(line, d.Offset, out caret);
            d.ShapeCaret = caret;
            return d;
        }

        /// <summary>
        /// Newtonsoft counts LinePosition as the characters consumed on that line, which
        /// for a failure on the first line is exactly the zero-based index of the
        /// character it could not accept. A line from the stdio reader has no newline in
        /// it, but a '\r' or an embedded U+2028 would; walk the line breaks rather than
        /// assume.
        /// </summary>
        internal static int OffsetOf(string line, int lineNumber, int column)
        {
            int start = 0;
            for (int l = 1; l < lineNumber && start < line.Length; l++)
            {
                int nl = line.IndexOf('\n', start);
                if (nl < 0) { start = line.Length; break; }
                start = nl + 1;
            }
            return Math.Max(0, Math.Min(line.Length, start + column));
        }

        internal static string FirstClause(string message)
        {
            if (string.IsNullOrEmpty(message)) return "unreadable JSON";
            int cut = message.Length;
            foreach (char c in new[] { '.', ':' })
            {
                int i = message.IndexOf(c);
                if (i > 0 && i < cut) cut = i;
            }
            return message.Substring(0, cut).Trim();
        }

        internal static string Classify(string line, string message, int offset)
        {
            if (line.Length > 0 && line[0] == '\uFEFF') return HintByteOrderMark;
            if (message.IndexOf("Additional text encountered after finished reading JSON content",
                                StringComparison.Ordinal) >= 0) return HintConcatenated;
            if (message.IndexOf("Bad JSON escape sequence", StringComparison.Ordinal) >= 0) return HintUnescapedBackslash;
            if (message.IndexOf("Unterminated string", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("Unexpected end", StringComparison.Ordinal) >= 0) return HintTruncated;
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return HintNotJson;
            if (offset < line.Length && IsAsciiLetter(line[offset])) return HintBareWord;
            return HintInvalid;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        internal static char Mask(char c)
        {
            if (IsAsciiLetter(c)) return 'a';
            if (c >= '0' && c <= '9') return '0';
            if (c == '\t') return ' ';
            if (c < 0x20 || c == 0x7F) return '.';
            if (c > 0x7E) return '?';
            return c;
        }

        internal static string MaskedWindow(string line, int offset, out int caret)
        {
            int from = Math.Max(0, offset - WindowBefore);
            int to = Math.Min(line.Length, offset + WindowAfter);
            var sb = new StringBuilder();
            if (from > 0) sb.Append("...");
            caret = sb.Length + (offset - from);
            for (int i = from; i < to; i++) sb.Append(Mask(line[i]));
            if (to < line.Length) sb.Append("...");
            return sb.ToString();
        }

        public string Advice()
        {
            switch (Hint)
            {
                case HintByteOrderMark:
                    return "The line starts with a UTF-8 byte-order mark; write stdin without a BOM.";
                case HintConcatenated:
                    return "A complete JSON value ended before the line did - two messages were written on one " +
                           "line. End every JSON-RPC message with a newline.";
                case HintUnescapedBackslash:
                    return "A backslash in a string is not a valid JSON escape - usually a Windows path such as " +
                           "C:\\folder written without doubling each backslash. Build the message with a JSON " +
                           "serializer (ConvertTo-Json, json.dumps) or use forward slashes.";
                case HintTruncated:
                    return "The line ended inside a string or object. A message was cut short, often by a raw line " +
                           "break inside a string value: escape it as \\n, since every newline ends a message on " +
                           "this transport.";
                case HintBareWord:
                    return "An unquoted word stands where JSON needs a quoted string, ':' or ','. Quote property " +
                           "names and string values with double quotes.";
                case HintNotJson:
                    return "The line does not start with '{' - it is not a JSON-RPC message at all. Only JSON-RPC " +
                           "messages may be written to this server's stdin.";
                default:
                    return "Build the message with a JSON serializer rather than by concatenating strings.";
            }
        }

        public string Message()
        {
            return "Parse error: that line is not valid JSON (" + Reason + ") at line " + Line + ", column " +
                   Column + " (character " + Offset + " of " + Length + "); hint: " + Hint + ". " + Advice() +
                   " Masked shape around that point (letters shown as 'a', digits as '0'): " + Shape +
                   " Nothing was run, and no id could be read from it, so this reply carries id null - it cannot " +
                   "be matched to your request. The content itself is not echoed back because a line that failed " +
                   "to parse can still contain a path or a token.";
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["hint"] = Hint,
                ["reason"] = Reason,
                ["line"] = Line,
                ["column"] = Column,
                ["offset"] = Offset,
                ["length"] = Length,
                ["shape"] = Shape,
                ["shape_caret"] = ShapeCaret,
                ["content_echoed"] = false
            };
        }

        /// <summary>
        /// The client's self-declared software name (initialize clientInfo.name) as a
        /// short token safe for a log line: at most 40 characters of [A-Za-z0-9 ._-],
        /// anything else replaced by '_'. Null when absent or not a string.
        /// </summary>
        public static string SafeClientName(JToken name)
        {
            string raw = name is JValue v ? v.Value as string : null;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var sb = new StringBuilder();
            foreach (char c in raw)
            {
                if (sb.Length >= 40) break;
                bool ok = IsAsciiLetter(c) || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == ' ';
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>One log line: where and what kind, never the content.</summary>
        public string LogLine(string client)
        {
            return "parse error answered: " + Hint + " at line " + Line + " column " + Column + " (offset " +
                   Offset + " of a " + Length + "-char line), reason '" + Reason + "', shape " +
                   JsonConvert.ToString(Shape) + (string.IsNullOrEmpty(client) ? "" : ", client '" + client + "'");
        }
    }
}
