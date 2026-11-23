using System;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Pure rules for text written to a String parameter. Revit strips leading and
    /// trailing whitespace on Parameter.Set (MEASURED, Revit 2023, 2026-10-23: a
    /// Comments value ending in a space read back without it, and the re-read refused
    /// and rolled back the whole atomic batch). The rehearsal refuses such a value up
    /// front, naming the fix, instead of letting the apply fail on a re-read the
    /// request could never satisfy.
    /// </summary>
    public static class TextValueRules
    {
        /// <summary>Null when the text survives Parameter.Set unchanged; otherwise the refusal.</summary>
        public static string EdgeWhitespaceError(string field, string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (string.Equals(value, value.Trim(), StringComparison.Ordinal)) return null;
            return "'" + field + "' has leading or trailing whitespace, which Revit strips when it stores a text " +
                   "parameter - the re-read would then differ from the request and the write would be refused. " +
                   "Send the trimmed value you mean.";
        }
    }
}
