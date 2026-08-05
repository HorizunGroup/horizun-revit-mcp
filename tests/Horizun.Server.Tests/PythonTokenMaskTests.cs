// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE ADVISORY, MASKED BY PYTHON'S OWN LEXER. These run a real IronPython engine
// - the same one the add-in creates - and use its public TokenCategorizer, which
// lexes without executing. The hand scanner's rules are proved separately in
// Horizun.Core.Tests.PythonSourceMaskTests; what is proved here is the path the
// command actually takes, and the ONE case the scanner cannot get right by
// construction: the f-string.
//
// The f-string result is asserted as it IS, not as one might wish: IronPython
// lexes an f-string as a single string token, so an API call inside one is
// masked along with the literal. That direction is deliberate for an advisory -
// the cost is a hint not given, never a hint given wrongly - and pinning it here
// stops the tokenizer's presence from being read as a precision claim it does
// not make.
// -----------------------------------------------------------------------------
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public class PythonTokenMaskTests
    {
        private static readonly ScriptEngine Engine = Python.CreateEngine();

        private static readonly Regex Move =
            new Regex(@"\bElementTransformUtils\s*\.\s*(Move|Rotate|Mirror)Element", RegexOptions.Compiled);
        private static readonly Regex Delete = new Regex(@"(?<!\w)doc\s*\.\s*Delete\s*\(", RegexOptions.Compiled);

        /// <summary>Masked by the tokenizer, asserting it was actually available.</summary>
        private static string Masked(string source)
        {
            string masked = PythonTokenMask.Mask(Engine, source);
            Assert.True(masked != null,
                "the tokenizer path must be available - if this fails the command silently falls back to the " +
                "hand scanner and this whole file stops testing what it claims to");
            return masked;
        }

        private static bool Advises(Regex r, string source) => r.IsMatch(Masked(source));

        [Fact]
        public void A_comment_raises_no_advisory()
        {
            Assert.False(Advises(Move, "# ElementTransformUtils.MoveElement is discussed here\nx = 1\n"));
            Assert.False(Advises(Delete, "x = 1  # and doc.Delete( too\n"));
        }

        [Fact]
        public void Single_and_double_quoted_strings_raise_no_advisory()
        {
            Assert.False(Advises(Move, "label = 'ElementTransformUtils.MoveElement'\n"));
            Assert.False(Advises(Move, "label = \"ElementTransformUtils.RotateElement\"\n"));
            Assert.False(Advises(Delete, "msg = 'call doc.Delete( to remove it'\n"));
        }

        [Fact]
        public void Triple_quoted_strings_and_docstrings_raise_no_advisory()
        {
            Assert.False(Advises(Move,
                "\"\"\"\nNotes.\nElementTransformUtils.MirrorElement moves things.\n\"\"\"\nx = 1\n"));
            Assert.False(Advises(Delete, "'''\ndoc.Delete( removes them\n'''\ny = 2\n"));
        }

        [Fact]
        public void Escaped_quotes_do_not_end_the_literal_early()
        {
            Assert.False(Advises(Move, "s = 'it\\'s ElementTransformUtils.MoveElement'\n"));
            Assert.False(Advises(Move, "s = \"a \\\" then ElementTransformUtils.RotateElement\"\n"));
        }

        /// <summary>
        /// THE DOCUMENTED LIMIT. An f-string is one string token to this lexer, so an API
        /// call inside its expression is masked with the rest of the literal: no advisory
        /// is raised. Conservative on purpose - a missed hint, never a false one.
        /// </summary>
        [Fact]
        public void An_f_string_expression_is_masked_and_the_limit_is_pinned_here()
        {
            string code = "name = f\"{ElementTransformUtils.MoveElement(doc, eid, v)}\"\n";

            Assert.False(Advises(Move, code));

            // Stated as a property rather than a wish: the whole literal is blanked.
            string masked = Masked(code);
            Assert.DoesNotContain("ElementTransformUtils", masked);
            Assert.Contains("name =", masked);
        }

        [Fact]
        public void A_real_call_still_raises_the_advisory()
        {
            Assert.True(Advises(Move, "ElementTransformUtils.MoveElement(doc, eid, vector)\n"));
            Assert.True(Advises(Move, "ElementTransformUtils . RotateElement (doc, eid, axis, angle)\n"));
            Assert.True(Advises(Delete, "doc.Delete(element.Id)\n"));
        }

        [Fact]
        public void A_composite_script_advises_on_the_call_and_not_on_the_prose()
        {
            string code =
                "# This script does several things the typed commands do not cover.\n" +
                "note = 'we also mention doc.Delete( here'\n" +
                "\"\"\" and again in a docstring: doc.Delete( \"\"\"\n" +
                "t.Start()\n" +
                "ElementTransformUtils.MoveElement(doc, eid, vector)   # the real one\n" +
                "t.Commit()\n";

            Assert.True(Advises(Move, code));
            // ...and the one that only ever appeared in prose stays quiet.
            Assert.False(Advises(Delete, code));
        }

        [Fact]
        public void Masking_preserves_length_so_the_source_still_lines_up()
        {
            string code = "a = 'xxx'  # note\nb = 2\n";
            string masked = Masked(code);

            Assert.Equal(code.Length, masked.Length);
            Assert.Equal(code.Split('\n').Length, masked.Split('\n').Length);
            Assert.Contains("b = 2", masked);
        }

        /// <summary>
        /// The advisory must never be the reason a run does not happen: a script the lexer
        /// cannot finish returns null so the caller falls back, rather than throwing.
        /// </summary>
        [Fact]
        public void A_source_the_lexer_cannot_handle_fails_soft_rather_than_throwing()
        {
            string masked = PythonTokenMask.Mask(Engine, "def broken(:\n");
            // Either it lexed it (a lexer is more tolerant than a parser) or it declined.
            // What must NOT happen is an exception escaping into the command.
            Assert.True(masked == null || masked.Length == "def broken(:\n".Length);
        }

        [Fact]
        public void No_engine_or_no_source_declines_instead_of_guessing()
        {
            Assert.Null(PythonTokenMask.Mask(null, "x = 1"));
            Assert.Null(PythonTokenMask.Mask(Engine, null));
            Assert.Null(PythonTokenMask.Mask(Engine, ""));
        }
    }
}
