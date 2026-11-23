// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The corpus of failure messages the commands can produce: the first argument of
// every CommandResult.Fail / FailWithDetail / FailCoded call in the add-in, with its
// string literals joined in order and any non-literal piece shown as {}. A
// parenthesis-aware scan - a regular expression cuts a message at its first "+".
// Used to MEASURE how often the text classifier names a cause, so the generic code
// stays the exception.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Horizun.Core.Tests
{
    internal static class FailureCorpus
    {
        private static readonly string[] Starts =
        {
            "CommandResult.Fail(", "CommandResult.FailWithDetail(", "CommandResult.FailCoded("
        };

        internal static IEnumerable<KeyValuePair<string, string>> Messages(string root)
        {
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                         .Where(f => f.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                                               StringComparison.Ordinal) < 0))
            {
                string text = File.ReadAllText(file);
                foreach (string start in Starts)
                {
                    int at = 0;
                    while ((at = text.IndexOf(start, at, StringComparison.Ordinal)) >= 0)
                    {
                        int i = at + start.Length;
                        at = i;
                        string msg = FirstArgument(text, i);
                        if (msg.Replace("{}", "").Trim().Length >= 12)
                            yield return new KeyValuePair<string, string>(Path.GetFileName(file), msg);
                    }
                }
            }
        }

        // The first argument, up to the first top-level comma or the closing parenthesis.
        private static string FirstArgument(string text, int i)
        {
            var message = new StringBuilder();
            int depth = 0;
            bool placeholderPending = false;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || ((c == '$' || c == '@') && i + 1 < text.Length &&
                                 (text[i + 1] == '"' || text[i + 1] == '$' || text[i + 1] == '@')))
                {
                    bool verbatim = false, interpolated = false;
                    while (text[i] != '"')
                    {
                        if (text[i] == '@') verbatim = true;
                        if (text[i] == '$') interpolated = true;
                        i++;
                    }
                    var lit = new StringBuilder();
                    for (i++; i < text.Length; i++)
                    {
                        if (text[i] == '"')
                        {
                            if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { lit.Append('"'); i++; continue; }
                            break;
                        }
                        if (!verbatim && text[i] == '\\' && i + 1 < text.Length) { lit.Append(text[i + 1]); i++; continue; }
                        lit.Append(text[i]);
                    }
                    string value = lit.ToString();
                    if (interpolated) value = Regex.Replace(value, @"\{[^}]*\}", "{}");
                    if (depth == 0) { message.Append(value); placeholderPending = false; }
                    continue;
                }
                if (c == '(' || c == '[' || c == '{')
                {
                    if (depth == 0 && !placeholderPending) { message.Append("{}"); placeholderPending = true; }
                    depth++;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (c == ',' && depth == 0) break;
                else if (depth == 0 && char.IsLetter(c) && !placeholderPending)
                {
                    message.Append("{}");
                    placeholderPending = true;
                    while (i + 1 < text.Length && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '.' || text[i + 1] == '_')) i++;
                }
            }
            return message.ToString();
        }
    }
}
