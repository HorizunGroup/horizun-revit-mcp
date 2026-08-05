// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE FALSE ADVISORY, measured live: a script whose only mention of
// ElementTransformUtils.MoveElement was inside a comment was told it duplicated
// horizun_transform_elements. Advice that fires on prose is advice people learn
// to skip - including when it is right - so the scanner has to see CODE only.
//
// Both directions are asserted here. Silencing false positives is worthless if
// it also silences the real calls the advisory exists for.
// -----------------------------------------------------------------------------
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PythonSourceMaskTests
    {
        // The same shape the overlap table uses, so these prove the real matcher.
        private static readonly Regex Move =
            new Regex(@"\bElementTransformUtils\s*\.\s*(Move|Rotate|Mirror)Element", RegexOptions.Compiled);
        private static readonly Regex Delete = new Regex(@"(?<!\w)doc\s*\.\s*Delete\s*\(", RegexOptions.Compiled);

        private static bool Matches(Regex r, string source) =>
            r.IsMatch(PythonSourceMask.StripCommentsAndStrings(source));

        [Fact]
        public void A_comment_does_not_trigger_the_advisory()
        {
            Assert.False(Matches(Move, "# mentions ElementTransformUtils.MoveElement in a comment only\nx = 1"));
            Assert.False(Matches(Delete, "x = 1  # doc.Delete( is discussed here\n"));
        }

        [Fact]
        public void A_string_literal_does_not_trigger_the_advisory()
        {
            Assert.False(Matches(Move, "label = 'ElementTransformUtils.MoveElement'\n"));
            Assert.False(Matches(Move, "label = \"ElementTransformUtils.RotateElement\"\n"));
            Assert.False(Matches(Delete, "msg = 'call doc.Delete( to remove it'\n"));
        }

        [Fact]
        public void A_triple_quoted_string_does_not_trigger_the_advisory()
        {
            string doc = "\"\"\"\nUsage notes.\nElementTransformUtils.MoveElement moves things.\ndoc.Delete( removes them.\n\"\"\"\nx = 1\n";
            Assert.False(Matches(Move, doc));
            Assert.False(Matches(Delete, doc));

            string singles = "'''\nElementTransformUtils.MirrorElement\n'''\ny = 2\n";
            Assert.False(Matches(Move, singles));
        }

        [Fact]
        public void A_real_call_still_triggers_the_advisory()
        {
            Assert.True(Matches(Move, "ElementTransformUtils.MoveElement(doc, eid, vector)\n"));
            Assert.True(Matches(Move, "ElementTransformUtils . RotateElement (doc, eid, axis, angle)\n"));
            Assert.True(Matches(Delete, "doc.Delete(element.Id)\n"));
        }

        [Fact]
        public void A_composite_script_with_a_real_call_among_prose_still_triggers()
        {
            string code =
                "# This script does several things the typed commands do not cover.\n" +
                "note = 'we also mention ElementTransformUtils.MirrorElement here'\n" +
                "\"\"\" and again in a docstring: doc.Delete( \"\"\"\n" +
                "t.Start()\n" +
                "ElementTransformUtils.MoveElement(doc, eid, vector)   # the real one\n" +
                "t.Commit()\n";
            Assert.True(Matches(Move, code));
            // ...and the one that only ever appeared in prose stays quiet.
            Assert.False(Matches(Delete, code));
        }

        [Fact]
        public void Masking_preserves_length_and_line_structure()
        {
            string code = "a = 'xxx'  # note\nb = 2\n";
            string masked = PythonSourceMask.StripCommentsAndStrings(code);
            Assert.Equal(code.Length, masked.Length);
            Assert.Equal(code.Split('\n').Length, masked.Split('\n').Length);
            // Code outside the literal survives untouched.
            Assert.Contains("a = ", masked);
            Assert.Contains("b = 2", masked);
        }

        [Fact]
        public void An_escaped_quote_does_not_end_the_string_early()
        {
            // Without escape handling the literal would "end" at the middle quote and the
            // API name after it would be scanned as code.
            Assert.False(Matches(Move, "s = 'it\\'s ElementTransformUtils.MoveElement'\n"));
        }

        [Fact]
        public void An_unterminated_string_ends_at_the_newline_rather_than_swallowing_the_file()
        {
            // A stray quote must not mask everything after it - that would silence every
            // advisory in the rest of the script.
            string code = "s = 'oops\nElementTransformUtils.MoveElement(doc, eid, v)\n";
            Assert.True(Matches(Move, code));
        }

        [Fact]
        public void Null_and_empty_are_returned_unchanged()
        {
            Assert.Null(PythonSourceMask.StripCommentsAndStrings(null));
            Assert.Equal("", PythonSourceMask.StripCommentsAndStrings(""));
        }
    }
}
