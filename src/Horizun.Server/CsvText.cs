// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// What every CSV this server writes has in common: RFC-4180 quoting, a cell that
// a spreadsheet would run as a formula neutralised with a leading apostrophe, and
// - when appending to a file somebody else started - the file's own encoding,
// delimiter and line ending, so an append never turns a Spanish-locale CSV into a
// mixture nobody can open.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Text;

namespace Horizun.Server
{
    internal static class CsvText
    {
        /// <summary>
        /// A text that Excel, LibreOffice or Sheets would read as a formula (= + - @, tab,
        /// CR) gets a leading apostrophe. Applied to TEXT only: a number the caller sent as
        /// a number stays a number, so -2.5 is still -2.5.
        /// </summary>
        internal static string NeutralizeFormula(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return "=+-@\t\r".IndexOf(s[0]) >= 0 ? "'" + s : s;
        }

        /// <summary>RFC-4180 quoting for one already-formatted field.</summary>
        internal static string Quote(string s, char delimiter)
        {
            s = s ?? "";
            if (s.IndexOf(delimiter) < 0 && s.IndexOfAny(new[] { '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// One cell. Text is neutralised then quoted; numbers are written invariant, with a
        /// decimal COMMA when the file is semicolon-delimited (the Spanish-locale CSV that
        /// Excel writes and reads), never the current culture's whim.
        /// </summary>
        internal static string Field(object value, char delimiter)
        {
            if (value == null) return "";
            if (value is bool b) return b ? "true" : "false";
            if (value is double || value is float || value is decimal || value is long || value is int)
            {
                string n = Convert.ToString(value, CultureInfo.InvariantCulture);
                return delimiter == ';' ? n.Replace('.', ',') : n;
            }
            return Quote(NeutralizeFormula(Convert.ToString(value, CultureInfo.InvariantCulture)), delimiter);
        }

        /// <summary>How an existing CSV is encoded, as far as its bytes can say.</summary>
        internal sealed class Detected
        {
            public string Name;          // utf-8 | utf-8-bom | windows-1252
            public Encoding Encoding;    // strict: throws rather than substituting '?'
            public byte[] Preamble;      // written back in front of the text
            public string Text;          // the decoded content, without preamble
        }

        /// <summary>
        /// UTF-8 with BOM, strict UTF-8, or Windows-1252 (what Excel writes for "CSV" on a
        /// Spanish Windows). Bytes valid in none of them are refused by the caller: an
        /// append into a file whose encoding is a guess is an append that corrupts it.
        /// </summary>
        internal static Detected Detect(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                var strict = new UTF8Encoding(false, true);
                return new Detected
                {
                    Name = "utf-8-bom", Encoding = strict, Preamble = new byte[] { 0xEF, 0xBB, 0xBF },
                    Text = strict.GetString(bytes, 3, bytes.Length - 3)
                };
            }
            try
            {
                var strict = new UTF8Encoding(false, true);
                return new Detected { Name = "utf-8", Encoding = strict, Preamble = new byte[0], Text = strict.GetString(bytes) };
            }
            catch (DecoderFallbackException) { }

            Encoding cp1252 = Windows1252();
            return new Detected { Name = "windows-1252", Encoding = cp1252, Preamble = new byte[0], Text = cp1252.GetString(bytes) };
        }

        /// <summary>Windows-1252 with exception fallbacks both ways: no silent '?'.</summary>
        internal static Encoding Windows1252() =>
            CodePagesEncodingProvider.Instance.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        /// <summary>
        /// The delimiter of an existing file, read from its first line outside quotes: a
        /// semicolon when it has more semicolons than commas, otherwise a comma.
        /// </summary>
        internal static char DetectDelimiter(string text)
        {
            int commas = 0, semis = 0;
            bool quoted = false;
            foreach (char ch in text ?? "")
            {
                if (ch == '"') quoted = !quoted;
                else if (!quoted && (ch == '\n' || ch == '\r')) break;
                else if (!quoted && ch == ',') commas++;
                else if (!quoted && ch == ';') semis++;
            }
            return semis > commas ? ';' : ',';
        }

        /// <summary>CRLF unless the file already ends its lines with a bare LF.</summary>
        internal static string DetectNewline(string text)
        {
            int lf = (text ?? "").IndexOf('\n');
            return lf > 0 && text[lf - 1] != '\r' ? "\n" : "\r\n";
        }
    }
}
