// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// What a script HANDED BACK, rendered without losing it.
//
// execute_python used to return `output.ToString()`. For a scalar that is fine.
// For the thing scripts actually build - a dict of results - IronPython's
// PythonDictionary does not override ToString(), so the caller received the
// literal string "IronPython.Runtime.PythonDictionary". Measured on a real run:
// the whole payload of a diagnostic replaced by the name of its own type. The
// data was not truncated or malformed, it was silently discarded, and the reply
// still said executed=true.
//
// So the rendering is explicit, and it SAYS which of the three things happened:
// a scalar passed through, a structure was serialized, or serialization failed
// and only a text rendering survived. The third case is a loss, and a caller is
// told rather than left to notice.
//
// Revit-free on purpose: IronPython's collections are ordinary IDictionary and
// IEnumerable implementations, so the rule is provable with plain .NET types.
// -----------------------------------------------------------------------------
using System;
using System.Collections;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>How the value came back, and whether anything was lost doing it.</summary>
    public sealed class ScriptOutputRendering
    {
        /// <summary>The value to publish: a JSON scalar, a JSON structure, or a string.</summary>
        public JToken Value { get; internal set; }

        /// <summary>"absent" | "scalar" | "structure" | "text_only"</summary>
        public string Kind { get; internal set; }

        /// <summary>Set only when the structure could NOT be serialized. Null otherwise.</summary>
        public string Note { get; internal set; }

        /// <summary>True when the caller is getting less than the script produced.</summary>
        public bool Lossy => Kind == "text_only";
    }

    public static class ScriptOutput
    {
        /// <summary>
        /// Cut free text down to what a reader can use, and SAY that it was cut.
        ///
        /// A script that prints in a loop over 200,000 elements produces tens of megabytes
        /// of text that then has to cross the pipe, sit in the server, and reach a client
        /// that will render none of it. Truncating is right for printed output - it is for
        /// a human, and the first quarter-megabyte answers the question - but truncating
        /// SILENTLY is not: a caller reading the tail of a log to find out whether the
        /// loop finished would be reading a tail this cut off, and would conclude the
        /// wrong thing. So the marker is part of the returned text, not a flag beside it
        /// that a reader has to know to look for.
        /// </summary>
        public static string Clamp(string text, int maxChars = Horizun.Contracts.Contract.MaxScriptTextChars)
        {
            if (text == null || text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) +
                   "\n\n--- TRUNCATED: " + text.Length + " characters were produced and the first " + maxChars +
                   " are shown. The rest is NOT held anywhere and cannot be asked for. Print less, or write it to " +
                   "a file from inside the script and return the path. ---";
        }

        public static ScriptOutputRendering Render(object value)
        {
            if (value == null)
                return new ScriptOutputRendering { Value = JValue.CreateNull(), Kind = "absent" };

            if (value is string s)
                return new ScriptOutputRendering { Value = new JValue(s), Kind = "scalar" };

            if (value is bool || value is int || value is long || value is short || value is byte ||
                value is uint || value is ulong || value is ushort || value is sbyte ||
                value is double || value is float || value is decimal)
                return new ScriptOutputRendering { Value = new JValue(value), Kind = "scalar" };

            // Anything structured: a dict, a list, a tuple, an object with properties.
            // JToken.FromObject walks IDictionary and IEnumerable, which is what every
            // IronPython container is underneath.
            if (value is IDictionary || value is IEnumerable || !value.GetType().IsPrimitive)
            {
                try
                {
                    return Bound(new ScriptOutputRendering { Value = JToken.FromObject(value), Kind = "structure" });
                }
                catch (Exception ex)
                {
                    // Say what was lost and how to avoid losing it. A caller who sees a type
                    // name where their data should be has no way to guess this on their own.
                    string text = SafeToString(value);
                    return new ScriptOutputRendering
                    {
                        Value = new JValue(text),
                        Kind = "text_only",
                        Note = "__output__ held a " + value.GetType().Name + " that could not be converted to JSON (" +
                               ex.Message + "), so only a text rendering of it is returned and the structure is " +
                               "LOST. Assign a JSON-serializable value - or json.dumps(...) it yourself - to get " +
                               "the data back intact."
                    };
                }
            }

            return new ScriptOutputRendering { Value = new JValue(SafeToString(value)), Kind = "text_only" };
        }

        /// <summary>
        /// A structure too big to return is REFUSED, never trimmed.
        ///
        /// Text can be cut and marked, because a reader can see the marker and know what
        /// they have. A JSON structure cannot: half a list is a valid list, and a caller
        /// that iterates it gets a confident, complete-looking, wrong answer. So an
        /// oversized structure comes back as a note saying how big it was and what to do,
        /// and the data is not there at all - which is the one thing that cannot be
        /// mistaken for the data.
        /// </summary>
        private static ScriptOutputRendering Bound(ScriptOutputRendering r,
                                                   int maxChars = Horizun.Contracts.Contract.MaxScriptTextChars)
        {
            string json;
            try { json = r.Value.ToString(Newtonsoft.Json.Formatting.None); }
            catch { return r; }
            if (json.Length <= maxChars) return r;

            return new ScriptOutputRendering
            {
                Value = JValue.CreateNull(),
                Kind = "too_large",
                Note = "__output__ serialized to " + json.Length + " characters, over the " + maxChars +
                       " limit, so NONE of it is returned. It is not truncated: half a structure is still a " +
                       "valid structure, and a caller iterating it would get a complete-looking wrong answer. " +
                       "Return a summary, or write the full result to a file from inside the script - " +
                       "json.dump(...) - and put the path in __output__ instead."
            };
        }

        private static string SafeToString(object value)
        {
            try { return value.ToString(); }
            catch (Exception ex) { return "<ToString() threw: " + ex.Message + ">"; }
        }
    }
}
