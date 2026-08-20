// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Reading a .py file the way Python reads it (5.27).
//
// Every case here is one somebody hit, or one that would go wrong quietly:
//
//   * the measured failure: a driver with accents, read through IronPython's
//     open(), died with "'charmap' codec can't decode byte 0x8f in position
//     2634". So: UTF-8 by default, and a file that is NOT UTF-8 must be refused
//     with the byte and the offset - never decoded leniently, because a
//     replacement character compiles and then runs a script nobody wrote.
//   * PowerShell's `>` writes UTF-16. A UTF-8 reader sees NULs and reports a
//     syntax error on line 1, which sends everybody to the wrong place.
//   * a UTF-8 BOM left in the source is a syntax error on the first character.
//   * PEP 263 says the declaration counts on the first two lines only, and stops
//     at the first line of real code.
// -----------------------------------------------------------------------------
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PythonSourceTextTests
    {
        private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

        [Fact]
        public void The_measured_case_utf8_with_accents_and_crlf_reads_first_time()
        {
            // What the field driver actually is: Spanish text and Windows line endings.
            byte[] raw = Utf8("# -*- coding: utf-8 -*-\r\nmodelo = 'auditoría de tabiquería'\r\n");

            DecodedSource d = PythonSourceText.Decode(raw);

            Assert.True(d.Ok);
            Assert.Contains("auditoría de tabiquería", d.Text);
            Assert.DoesNotContain("\r", d.Text);
            Assert.True(d.NewlinesNormalized);
        }

        [Fact]
        public void A_file_that_is_not_utf8_is_refused_naming_the_byte_and_where_it_is()
        {
            // 0x8f alone is not valid UTF-8. This is the shape of the measured error,
            // and the point is that it is an ERROR here rather than a silent mangling.
            var raw = new byte[] { (byte)'x', (byte)' ', (byte)'=', (byte)' ', (byte)'\'', 0x8f, (byte)'\'' };

            DecodedSource d = PythonSourceText.Decode(raw);

            Assert.False(d.Ok);
            Assert.Null(d.Text);
            Assert.Contains("0x8f", d.Error);
            Assert.Contains("offset 5", d.Error);
            Assert.Contains("NOTHING was compiled", d.Error);
            // And it names the two ways out, so the caller is not left guessing.
            Assert.Contains("coding: cp1252", d.Error);
        }

        [Fact]
        public void A_declared_codepage_is_honoured_so_the_same_file_runs_instead_of_being_refused()
        {
            byte[] raw = Encoding.GetEncoding(1252).GetBytes("# -*- coding: cp1252 -*-\nx = 'tabiquería'\n");

            DecodedSource d = PythonSourceText.Decode(raw);

            Assert.True(d.Ok);
            Assert.Contains("tabiquería", d.Text);
            Assert.Contains("cp1252", d.Encoding);
            Assert.Contains("# coding: line", d.Encoding);
        }

        [Fact]
        public void A_codec_this_platform_does_not_know_is_refused_by_name_not_silently_replaced()
        {
            byte[] raw = Utf8("# -*- coding: klingon-1 -*-\nx = 1\n");

            DecodedSource d = PythonSourceText.Decode(raw);

            Assert.False(d.Ok);
            Assert.Contains("klingon-1", d.Error);
            Assert.Contains("Nothing was decoded", d.Error);
        }

        [Fact]
        public void A_utf8_bom_is_dropped_rather_than_compiled_into_a_syntax_error()
        {
            var raw = new byte[] { 0xEF, 0xBB, 0xBF }.Concat2(Utf8("import math\n"));

            DecodedSource d = PythonSourceText.Decode(raw);

            Assert.True(d.Ok);
            Assert.Equal("import math\n", d.Text);
            // By character, not by substring: String.IndexOf is culture-sensitive and
            // "finds" a zero-weight character like U+FEFF at position 0 of any string.
            Assert.DoesNotContain('﻿', d.Text.ToCharArray());
            Assert.Contains("BOM", d.Encoding);
        }

        [Fact]
        public void Utf16_written_by_a_powershell_redirect_is_decoded_not_read_as_nuls()
        {
            byte[] raw = new UnicodeEncoding(false, true).GetPreamble()
                         .Concat2(new UnicodeEncoding(false, false).GetBytes("x = 'año'\r\n"));

            DecodedSource d = PythonSourceText.Decode(raw);

            Assert.True(d.Ok);
            Assert.Equal("x = 'año'\n", d.Text);
            Assert.Contains("utf-16", d.Encoding);
            // By character: a substring search for "\0" is culture-sensitive and matches
            // at position 0 of every string, which would make this assertion meaningless.
            Assert.DoesNotContain('\0', d.Text.ToCharArray());
        }

        [Fact]
        public void A_bom_outranks_a_coding_line_that_contradicts_it()
        {
            // The bytes are the evidence; the comment is a claim about them.
            byte[] raw = new byte[] { 0xEF, 0xBB, 0xBF }.Concat2(Utf8("# -*- coding: cp1252 -*-\nx = 'é'\n"));

            DecodedSource d = PythonSourceText.Decode(raw);

            Assert.True(d.Ok);
            Assert.Contains("utf-8 (BOM)", d.Encoding);
            Assert.Contains("x = 'é'", d.Text);
        }

        [Fact]
        public void The_coding_line_counts_on_the_first_two_lines_and_no_further()
        {
            byte[] onLineTwo = Utf8("#!/usr/bin/env python\n# -*- coding: cp1252 -*-\nx = 1\n");
            byte[] onLineThree = Utf8("#!/usr/bin/env python\n#\n# -*- coding: cp1252 -*-\nx = 1\n");

            Assert.Contains("cp1252", PythonSourceText.Decode(onLineTwo).Encoding);
            Assert.Equal("utf-8", PythonSourceText.Decode(onLineThree).Encoding);
        }

        [Fact]
        public void The_word_coding_inside_real_code_is_not_a_declaration()
        {
            // A first line that is neither blank nor a comment ends the search, exactly
            // as CPython does. Otherwise a string mentioning an encoding would silently
            // change how the whole file is read.
            byte[] raw = Utf8("encoding = 'coding: cp1252'\n# -*- coding: cp1252 -*-\nx = 1\n");

            Assert.Equal("utf-8", PythonSourceText.Decode(raw).Encoding);
        }

        [Fact]
        public void Lone_carriage_returns_are_normalised_too_and_a_clean_file_reports_no_rewrite()
        {
            Assert.Equal("a = 1\nb = 2\n", PythonSourceText.Decode(Utf8("a = 1\rb = 2\r")).Text);
            Assert.True(PythonSourceText.Decode(Utf8("a = 1\rb = 2\r")).NewlinesNormalized);
            Assert.False(PythonSourceText.Decode(Utf8("a = 1\nb = 2\n")).NewlinesNormalized);
        }

        [Fact]
        public void An_empty_file_decodes_to_nothing_rather_than_failing_the_read()
        {
            // Whether an empty script is a run is the COMMAND's call, and it refuses it.
            // The decoder's job is to report what the bytes were, and there were none.
            DecodedSource d = PythonSourceText.Decode(new byte[0]);

            Assert.True(d.Ok);
            Assert.Equal("", d.Text);
        }

        [Fact]
        public void Nothing_read_at_all_is_an_error_not_an_empty_script()
        {
            DecodedSource d = PythonSourceText.Decode(null);

            Assert.False(d.Ok);
            Assert.Null(d.Text);
        }
    }

    internal static class ByteArrayJoin
    {
        /// <summary>Concat, spelled out - the tests read better than Enumerable.Concat().ToArray().</summary>
        public static byte[] Concat2(this byte[] first, byte[] second)
        {
            var joined = new byte[first.Length + second.Length];
            System.Array.Copy(first, 0, joined, 0, first.Length);
            System.Array.Copy(second, 0, joined, first.Length, second.Length);
            return joined;
        }
    }
}
