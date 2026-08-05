// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Blank out Python comments and string literals so a regex over the source can
// only match CODE.
//
// The defect this fixes, reported from a live run: the typed-overlap advisory
// matched
//
//     # mentions ElementTransformUtils.MoveElement in a comment only
//
// and told the caller their script duplicated horizun_transform_elements. It did
// not - it mentioned it. A false advisory is cheap once and corrosive in bulk:
// advice that fires on prose is advice people learn to skip, including when it is
// right.
//
// WHY NOT THE IRONPYTHON PARSER. The engine could produce a real AST, but that
// binds an advisory to the scripting runtime, has to run before the document gate
// (so it would parse untrusted source earlier than anything else does), and turns
// a syntax error into a failure of the advisory rather than of the run. This is a
// scanner with no such dependencies.
//
// WHAT IT DOES. Walks the source once, tracking whether it is inside a comment,
// a short string or a triple-quoted string, and replaces the CONTENTS of each
// with spaces. Length and line structure are preserved, so offsets in the masked
// text still line up with the original.
//
// WHAT IT DELIBERATELY DOES NOT DO. It does not evaluate f-string expressions,
// which are code inside a literal. Masking them is the conservative direction for
// an advisory: the cost is a missed hint, never a false one.
//
// Revit-free: pure string work, provable in CI.
// -----------------------------------------------------------------------------
using System.Text;

namespace Horizun.Revit.Core
{
    public static class PythonSourceMask
    {
        /// <summary>
        /// The source with every comment body and string literal body replaced by
        /// spaces, same length, same newlines. Null in, null out.
        /// </summary>
        public static string StripCommentsAndStrings(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;

            var masked = new StringBuilder(source.Length);
            int i = 0;
            int n = source.Length;

            while (i < n)
            {
                char c = source[i];

                // ---- comment: everything to the end of the line ----
                if (c == '#')
                {
                    while (i < n && source[i] != '\n') { masked.Append(' '); i++; }
                    continue;
                }

                // ---- string literal, short or triple, single or double quoted ----
                if (c == '\'' || c == '"')
                {
                    bool triple = i + 2 < n && source[i + 1] == c && source[i + 2] == c;
                    char quote = c;

                    // The opening quotes survive as themselves: keeping them makes the
                    // masked text still look like Python, and they can never be part of
                    // an API name.
                    if (triple) { masked.Append(quote, 3); i += 3; }
                    else { masked.Append(quote); i++; }

                    while (i < n)
                    {
                        char d = source[i];

                        // A backslash escapes the next character, including a quote.
                        // Newlines are preserved so line numbers do not drift.
                        if (d == '\\' && i + 1 < n)
                        {
                            masked.Append(source[i] == '\n' ? '\n' : ' ');
                            masked.Append(source[i + 1] == '\n' ? '\n' : ' ');
                            i += 2;
                            continue;
                        }

                        if (triple)
                        {
                            if (d == quote && i + 2 < n && source[i + 1] == quote && source[i + 2] == quote)
                            { masked.Append(quote, 3); i += 3; break; }
                        }
                        else
                        {
                            if (d == quote) { masked.Append(quote); i++; break; }
                            // An unterminated short string ends at the newline, exactly as
                            // Python would report it. Without this a stray quote would mask
                            // the whole rest of the file and silence every advisory after it.
                            if (d == '\n') { masked.Append('\n'); i++; break; }
                        }

                        masked.Append(d == '\n' ? '\n' : ' ');
                        i++;
                    }
                    continue;
                }

                masked.Append(c);
                i++;
            }

            return masked.ToString();
        }
    }
}
