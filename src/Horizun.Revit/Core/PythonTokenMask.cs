// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// MASKING PYTHON WITH PYTHON'S OWN LEXER, so the typed-overlap advisory can only
// ever match code.
//
// PythonSourceMask is a hand-written scanner. It handles comments, quotes,
// triples and escapes, and it was enough to kill the reported false positive -
// but it is a re-implementation of a lexer, and the case it cannot get right by
// construction is the f-string: `f"{ElementTransformUtils.MoveElement(...)}"`
// really does contain code, and a scanner that masks whole literals hides it
// while a scanner that does not mask them re-opens the false positives.
//
// The DLR exposes a real one. Microsoft.Scripting.Hosting.TokenCategorizer is a
// PUBLIC hosting service that IronPython implements, it reports a TokenCategory
// per token, and - the part that matters here - it LEXES WITHOUT EXECUTING. So
// this asks the same engine that will later run the script what its tokens are,
// blanks out everything categorised as a comment or a string literal, and hands
// back source of identical length whose remaining characters are code.
//
// WHAT IS STILL NOT PERFECT, stated rather than implied: IronPython lexes an
// f-string as a single string token, so the expression inside one is masked with
// the rest of the literal. That direction is deliberate for an advisory - the
// cost is a hint we fail to give, never a hint we give wrongly. It is documented
// on the tool and asserted in the tests so nobody later reads the tokenizer's
// presence as a promise of precision it does not make.
//
// FAILS SOFT. Any tokenizer problem - an unavailable service, a lexer error on a
// half-written script - returns null, and the caller falls back to the scanner.
// An advisory must never be the reason a run does not happen.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;

namespace Horizun.Revit.Core
{
    public static class PythonTokenMask
    {
        /// <summary>
        /// The source with comment and string tokens blanked to spaces, same length and
        /// same newlines. Null when the tokenizer could not be used at all, which is the
        /// caller's signal to fall back to PythonSourceMask.
        /// </summary>
        public static string Mask(ScriptEngine engine, string source)
        {
            if (engine == null || string.IsNullOrEmpty(source)) return null;

            try
            {
                TokenCategorizer categorizer = engine.GetService<TokenCategorizer>();
                if (categorizer == null) return null;

                ScriptSource script = engine.CreateScriptSourceFromString(
                    source, SourceCodeKind.Statements);
                categorizer.Initialize(null, script, SourceLocation.MinValue);

                var masked = new StringBuilder(source);
                bool sawAnything = false;

                foreach (TokenInfo token in ReadAll(categorizer))
                {
                    sawAnything = true;
                    if (!IsMaskable(token.Category)) continue;

                    int start = token.SourceSpan.Start.Index;
                    int length = token.SourceSpan.Length;
                    if (start < 0 || length <= 0 || start + length > masked.Length) continue;

                    for (int i = start; i < start + length; i++)
                        if (masked[i] != '\n' && masked[i] != '\r') masked[i] = ' ';
                }

                // A tokenizer that produced nothing at all is not evidence that the source
                // has no code in it.
                return sawAnything ? masked.ToString() : null;
            }
            catch
            {
                // Including a lexer error on a malformed script: the advisory is not the
                // place to decide a run cannot proceed.
                return null;
            }
        }

        /// <summary>
        /// Comments and string literals - the two categories that are text rather than
        /// calls. Everything else, including identifiers and operators, stays.
        /// </summary>
        private static bool IsMaskable(TokenCategory category)
        {
            switch (category)
            {
                case TokenCategory.Comment:
                case TokenCategory.LineComment:
                case TokenCategory.DocComment:
                case TokenCategory.StringLiteral:
                case TokenCategory.CharacterLiteral:
                case TokenCategory.RegularExpressionLiteral:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Every token to end of stream, bounded. The bound exists because this runs on
        /// Revit's UI thread inside a command: a pathological input must cost a missed
        /// advisory, never a hang.
        /// </summary>
        private static IEnumerable<TokenInfo> ReadAll(TokenCategorizer categorizer)
        {
            const int maxTokens = 200000;
            for (int i = 0; i < maxTokens; i++)
            {
                TokenInfo token = categorizer.ReadToken();
                if (token.Category == TokenCategory.EndOfStream) yield break;
                yield return token;
            }
        }
    }
}
