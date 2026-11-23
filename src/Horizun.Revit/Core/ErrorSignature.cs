using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// A stable, data-free name for an internal failure: the exception's bare type and the Horizun methods it crossed,
    /// innermost first ("InvalidCastException U+FF20 ManageMaterials.CheckColours &lt; ManageMaterials.PlanAll").
    /// Field 2026-11-10: every error_interno of 5.0.2 reached the team telemetry with an empty message, because the
    /// exception text can carry model data and is redacted. Code identifiers carry none, so this line can travel whole.
    /// Only frames in the Horizun namespace are named; no message, no path, no argument value is ever included.
    /// </summary>
    public static class ErrorSignature
    {
        public const string Field = "error_signature";
        private const int MaxFrames = 4;
        // The team telemetry receiver drops a WHOLE message holding a run of 40 or more characters with no break
        // (it reads as a token or hash). Every word of the signature is kept below that: type <= 39, and a frame
        // "Class.Method" <= 39 (class <= 24 with its "Command" suffix dropped, method <= 14).
        private const int MaxType = 39, MaxClass = 24, MaxMethod = 14;
        private static readonly Regex Frame = new Regex(@"^\s*(?:at|en|bei|à|em|w|в)\s+(?<m>Horizun\.[\w.`<>]+?)\(", RegexOptions.CultureInvariant);
        private static readonly Regex Safe = new Regex(@"[^A-Za-z0-9_.]", RegexOptions.CultureInvariant);
        private static readonly Regex Letters = new Regex(@"[^A-Za-z]", RegexOptions.CultureInvariant);
        // The team telemetry lets this line through whole only when it matches, exactly,
        // ^error_signature: [A-Za-z]+( \uFF20 [A-Za-z0-9_.]+( < [A-Za-z0-9_.]+){0,3})?$ (downstream reader 0.4.81): the type is
        // letters only and the separator is the FULLWIDTH COMMERCIAL AT, so "@" in a message can never pass for it.
        public const string At = " \uFF20 ";

        public static string From(Exception ex)
        {
            if (ex == null) return null;
            Exception inner = ex;
            while (inner.InnerException != null && (inner is AggregateException || inner is System.Reflection.TargetInvocationException))
                inner = inner.InnerException;
            return From(inner.GetType().Name, inner.StackTrace ?? ex.StackTrace);
        }

        /// <summary>From a bare type name and a .NET stack trace text (any UI language).</summary>
        public static string From(string typeName, string stackTrace)
        {
            string type = Cap(Letters.Replace(typeName ?? "", ""), MaxType);
            if (type.Length == 0) return null;
            var frames = new List<string>();
            foreach (string raw in (stackTrace ?? "").Split('\n'))
            {
                Match m = Frame.Match(raw);
                if (!m.Success) continue;
                string[] parts = m.Groups["m"].Value.Split('.');
                if (parts.Length < 2) continue;
                // Class.Method: the namespace adds nothing and a compiler-generated name is cut at its marker.
                string cls = parts[parts.Length - 2], method = parts[parts.Length - 1];
                int angle = cls.IndexOf('<'); if (angle > 0) cls = cls.Substring(0, angle);
                cls = Safe.Replace(cls, "");
                if (cls.Length > "Command".Length && cls.EndsWith("Command", StringComparison.Ordinal)) cls = cls.Substring(0, cls.Length - "Command".Length);
                string name = Cap(cls, MaxClass) + "." + Cap(Safe.Replace(method, ""), MaxMethod);
                if (name.Length < 3 || frames.Contains(name)) continue;
                frames.Add(name);
                if (frames.Count == MaxFrames) break;
            }
            return frames.Count == 0 ? type : type + At + string.Join(" < ", frames);
        }

        private static string Cap(string s, int max) => s.Length <= max ? s : s.Substring(0, max);
    }
}
