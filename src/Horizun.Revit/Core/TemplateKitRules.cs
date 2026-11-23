using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    // Revit-free rules. Allowed values and forbidden names belong to the kit.
    public static class TemplateKitRules
    {
        public static string TraceIssue(bool exists, string value, IEnumerable<string> allowed)
        {
            if (!exists) return "absent";
            if (string.IsNullOrWhiteSpace(value)) return "empty";
            return allowed != null && !allowed.Contains(value, StringComparer.Ordinal) ? "not_allowed" : null;
        }

        public static bool ForbiddenName(string value, IEnumerable<string> patterns, IEnumerable<string> fragments)
        {
            string text = value ?? "";
            // Build every expression before matching: an early hit must not conceal
            // an invalid convention and leave the rest of the audit unmeasured.
            var regexes = (patterns ?? Enumerable.Empty<string>()).Select(p =>
                new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))).ToArray();
            return regexes.Any(r => r.IsMatch(text)) || (fragments ?? Enumerable.Empty<string>())
                .Any(f => !string.IsNullOrEmpty(f) && text.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
