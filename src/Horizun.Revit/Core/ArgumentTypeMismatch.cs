using System;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// A command that reads an argument with Value&lt;string&gt;() / (JObject) and receives another JSON type throws a
    /// Newtonsoft cast exception. Field 2026-11-09 (manage_materials, a colour sent as an array; execute_python output
    /// read as an object): that surfaced as error_interno "InvalidCastException: Cannot cast JArray to JToken", which
    /// told the agent nothing it could fix. It is a wrong argument type, so it is answered as one.
    /// </summary>
    public static class ArgumentTypeMismatch
    {
        /// <summary>The sentence for a JSON type mismatch, or null when the exception is something else.</summary>
        public static string Explain(Exception ex, string tool)
        {
            if (!Is(ex)) return null;
            return "An argument of " + (string.IsNullOrEmpty(tool) ? "this tool" : tool) + " has the wrong JSON type: a list or " +
                   "object was sent where the tool reads a single value (text, number or true/false), or a single value where it " +
                   "reads a list or object - for example a colour as [r, g, b] instead of \"#RRGGBB\", or one id instead of [id]. " +
                   "Compare each argument with the exact schema horizun://contract/tools/" + (string.IsNullOrEmpty(tool) ? "{tool}" : tool) +
                   " and resend. A transaction the command had open was rolled back by Revit; whether an earlier one had committed " +
                   "cannot be told from this exception, so for a write call horizun_verify_changes before retrying.";
        }

        public static bool Is(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                string m = e.Message ?? "";
                if (e is InvalidCastException && m.IndexOf("Newtonsoft.Json.Linq.J", StringComparison.Ordinal) >= 0) return true;
                // (string)token on an array/object: "Can not convert Array to String." from Newtonsoft.
                if (e is ArgumentException && m.StartsWith("Can not convert ", StringComparison.Ordinal) &&
                    (m.IndexOf("Array", StringComparison.Ordinal) >= 0 || m.IndexOf("Object", StringComparison.Ordinal) >= 0) &&
                    (e.StackTrace ?? "").IndexOf("Newtonsoft.Json", StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }
    }
}
