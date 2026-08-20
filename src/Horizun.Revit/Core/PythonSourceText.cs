// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Turning the BYTES of a .py file into the source string the engine compiles
// (story 5.27).
//
// WHY THE BRIDGE DOES THIS AND NOT THE SCRIPT. execute_python took only an inline
// `code` string, so a 535-line, 26 KB driver had to arrive as a stub that read
// its own file and compiled it. Measured on 2026-08-13, that stub took THREE
// attempts, and the first two failures teach nothing to anyone who is not already
// an IronPython expert:
//
//   open(path).read()               -> UnicodeDecodeError: 'charmap' codec can't
//                                      decode byte 0x8f in position 2634
//                                      (IronPython's open() uses the ANSI
//                                       codepage; the driver has accents)
//   open(path,"rb").read().replace  -> TypeError: expected IList[Byte], got str
//                                      (bytes have no str.replace)
//
// The working third attempt was File.ReadAllText + UTF8Encoding(False) +
// replace("\r\n","\n") + compile(). That is exactly what this file does, once,
// for everybody - and it does it on the HOST side of the size limit and the
// source hash, so `code_path` slips past neither: what is read here is what is
// measured, hashed and bound to the idempotency key.
//
// WHAT IT HONOURS, in this order: a byte-order mark (a .py written by a
// PowerShell redirect is UTF-16 and every byte after the first character looks
// like a NUL to a UTF-8 reader); then a PEP-263 `# -*- coding: ... -*-` line, in
// the first two lines, as Python itself defines it; then UTF-8. An UNDECODABLE
// file is an ERROR that names the byte and the offset and says how to fix it -
// never a mojibake string that compiles into something nobody wrote.
//
// Revit-free: bytes in, text out. The IO belongs to the command.
// -----------------------------------------------------------------------------
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    /// <summary>What the decode produced, or exactly why it produced nothing.</summary>
    public sealed class DecodedSource
    {
        /// <summary>The source, newlines normalised to \n. Null when Error is set.</summary>
        public string Text;

        /// <summary>How it was decoded, and on whose authority: "utf-8", "utf-8 (BOM)", "cp1252 (# coding: line)"...</summary>
        public string Encoding;

        /// <summary>Whether any \r\n or lone \r had to be rewritten.</summary>
        public bool NewlinesNormalized;

        /// <summary>Set when nothing could be decoded. Text is null whenever this is set.</summary>
        public string Error;

        public bool Ok { get { return Error == null; } }
    }

    public static class PythonSourceText
    {
        /// <summary>
        /// PEP 263: the declaration is recognised on the first or second line only, and
        /// this is the regular expression the reference implementation documents.
        /// </summary>
        private static readonly Regex CodingLine =
            new Regex(@"^[ \t\f]*#.*?coding[:=][ \t]*([-_.a-zA-Z0-9]+)", RegexOptions.Compiled);

        /// <summary>
        /// Python codec names that are NOT .NET encoding names. Only the ones a Windows
        /// BIM machine actually produces; anything else is looked up by name and, if the
        /// platform does not know it, REFUSED by name rather than silently replaced.
        /// </summary>
        private static int CodePageFor(string pythonName)
        {
            switch (pythonName)
            {
                case "utf8":
                case "utf-8":
                case "utf_8":
                case "u8":
                    return 65001;
                case "latin1":
                case "latin-1":
                case "latin_1":
                case "iso8859-1":
                case "iso-8859-1":
                case "iso_8859_1":
                case "l1":
                    return 28591;
                case "cp1252":
                case "windows-1252":
                case "windows_1252":
                    return 1252;
                case "ascii":
                case "us-ascii":
                    return 20127;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Decode a .py file's bytes. Never throws: an undecodable file comes back with
        /// Error set, because a caller that gets a string back must be able to trust that
        /// it is what the file says.
        /// </summary>
        public static DecodedSource Decode(byte[] raw)
        {
            var result = new DecodedSource();
            if (raw == null)
            {
                result.Error = "Nothing was read from the file.";
                return result;
            }
            if (raw.Length == 0)
            {
                result.Text = "";
                result.Encoding = "utf-8 (the file is empty)";
                return result;
            }

            // ---- 1. A byte-order mark decides, and outranks any coding line. -------
            // PowerShell's `>` and Out-File have written UTF-16 .py files for years; a
            // UTF-8 reader sees "i\0m\0p\0o\0r\0t\0" and reports a syntax error on line 1,
            // which sends everybody looking at the wrong thing.
            string bomText = FromBom(raw, out string bomName);
            if (bomName != null)
            {
                result.Encoding = bomName;
                return Normalize(result, bomText);
            }

            // ---- 2. PEP 263, first two lines. --------------------------------------
            string declared = DeclaredCoding(raw);
            if (declared != null)
            {
                int codePage = CodePageFor(declared.ToLowerInvariant());
                Encoding enc = null;
                try
                {
                    EnsureCodePages();
                    enc = codePage != 0 ? Encoding.GetEncoding(codePage) : Encoding.GetEncoding(declared);
                }
                catch (Exception ex)
                {
                    result.Error = "The file declares '# coding: " + declared + "' and this platform does not " +
                                   "know that encoding (" + ex.Message + "). Nothing was decoded and nothing ran. " +
                                   "Save the file as UTF-8 and drop the declaration, or declare a codec Windows " +
                                   "knows (utf-8, cp1252, latin-1).";
                    return result;
                }

                string text;
                try { text = Strict(enc).GetString(raw); }
                catch (DecoderFallbackException ex)
                {
                    result.Error = Undecodable(declared, ex);
                    return result;
                }
                catch (Exception ex)
                {
                    result.Error = "The file could not be decoded as '" + declared + "' (" + ex.Message +
                                   "). Nothing ran.";
                    return result;
                }

                result.Encoding = declared + " (from its own # coding: line)";
                return Normalize(result, text);
            }

            // ---- 3. UTF-8, strictly. -----------------------------------------------
            // Strict on purpose. A replacement-character decode would compile: the script
            // would run with a mangled string literal or an identifier nobody wrote, and
            // no error anywhere. Refusing names the byte and the offset instead - which is
            // the information the measured failure ("byte 0x8f in position 2634") carried
            // and nothing acted on.
            try
            {
                string text = Strict(Encoding.UTF8).GetString(raw);
                result.Encoding = "utf-8";
                return Normalize(result, text);
            }
            catch (DecoderFallbackException ex)
            {
                result.Error = Undecodable("utf-8", ex);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "The file could not be decoded as UTF-8 (" + ex.Message + "). Nothing ran.";
                return result;
            }
        }

        /// <summary>The message a person can act on, naming the byte and where it is.</summary>
        private static string Undecodable(string encodingName, DecoderFallbackException ex)
        {
            string b = ex.BytesUnknown != null && ex.BytesUnknown.Length > 0
                ? "0x" + ex.BytesUnknown[0].ToString("x2")
                : "(unknown byte)";
            return "This file is not valid " + encodingName + ": byte " + b + " at offset " + ex.Index +
                   " does not decode. NOTHING was compiled and nothing ran - a lenient decode would have " +
                   "replaced that byte and run a script nobody wrote. Fix it by saving the file as UTF-8, or by " +
                   "declaring its real encoding on the first or second line, e.g. '# -*- coding: cp1252 -*-'.";
        }

        private static Encoding Strict(Encoding baseEncoding)
        {
            var clone = (Encoding)baseEncoding.Clone();
            clone.DecoderFallback = DecoderFallback.ExceptionFallback;
            return clone;
        }

        /// <summary>
        /// IronPython registers this for its own reasons and only on .NET 5+; the decode
        /// path must not depend on that having happened first. Process-wide and idempotent.
        /// </summary>
        private static void EnsureCodePages()
        {
#if NET
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch { }
#endif
        }

        /// <summary>Decode by byte-order mark, or answer null so the caller keeps looking.</summary>
        private static string FromBom(byte[] raw, out string name)
        {
            name = null;
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            {
                name = "utf-8 (BOM)";
                // The BOM is dropped rather than decoded: a leading U+FEFF in the source
                // is a syntax error, and a script refused for an invisible character is
                // the least diagnosable failure this path could produce.
                return new UTF8Encoding(false).GetString(raw, 3, raw.Length - 3);
            }
            if (raw.Length >= 4 && raw[0] == 0xFF && raw[1] == 0xFE && raw[2] == 0x00 && raw[3] == 0x00)
            {
                name = "utf-32 little-endian (BOM)";
                return new UTF32Encoding(false, false).GetString(raw, 4, raw.Length - 4);
            }
            if (raw.Length >= 4 && raw[0] == 0x00 && raw[1] == 0x00 && raw[2] == 0xFE && raw[3] == 0xFF)
            {
                name = "utf-32 big-endian (BOM)";
                return new UTF32Encoding(true, false).GetString(raw, 4, raw.Length - 4);
            }
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            {
                name = "utf-16 little-endian (BOM)";
                return new UnicodeEncoding(false, false).GetString(raw, 2, raw.Length - 2);
            }
            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            {
                name = "utf-16 big-endian (BOM)";
                return new UnicodeEncoding(true, false).GetString(raw, 2, raw.Length - 2);
            }
            return null;
        }

        /// <summary>
        /// The codec named on the first or second line, or null. Read from the RAW bytes
        /// as Latin-1 - one byte, one char - because the whole point is that we do not yet
        /// know how to decode the file, and the declaration itself is always ASCII.
        /// </summary>
        private static string DeclaredCoding(byte[] raw)
        {
            int start = 0;
            for (int line = 0; line < 2 && start < raw.Length; line++)
            {
                int end = start;
                while (end < raw.Length && raw[end] != (byte)'\n' && raw[end] != (byte)'\r') end++;

                var sb = new StringBuilder(end - start);
                for (int i = start; i < end; i++) sb.Append((char)raw[i]);
                string text = sb.ToString();

                Match m = CodingLine.Match(text);
                if (m.Success) return m.Groups[1].Value;

                // A line that is not blank and not a comment ends the search, exactly as
                // CPython does: a `coding` word inside real code is not a declaration.
                string trimmed = text.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal)) return null;

                if (end < raw.Length && raw[end] == (byte)'\r' && end + 1 < raw.Length && raw[end + 1] == (byte)'\n') end++;
                start = end + 1;
            }
            return null;
        }

        /// <summary>
        /// CRLF and lone CR to LF. IronPython compiles CRLF source, but a normalised
        /// string is what the working stub produced, line numbers stay comparable, and a
        /// file edited on two operating systems stops being two different scripts.
        /// </summary>
        private static DecodedSource Normalize(DecodedSource result, string text)
        {
            if (text == null) { result.Text = null; result.Error = "Nothing was decoded."; return result; }

            if (text.IndexOf('\r') < 0)
            {
                result.Text = text;
                result.NewlinesNormalized = false;
                return result;
            }

            result.Text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            result.NewlinesNormalized = true;
            return result;
        }
    }
}
