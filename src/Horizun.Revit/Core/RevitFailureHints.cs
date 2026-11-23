using System;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// What to do next for Revit failure messages that agents kept retrying unchanged.
    /// MEASURED in the bridge logs, 2026-10-17 to 2026-10-23: 112 transform rollbacks on
    /// "The family is connected in a network and can no longer keep the connectivity" and
    /// 166 create_elements rollbacks on "Fitting cannot be created ... the angle between
    /// them is too small or too large", most retried with the same arguments. The hint never
    /// changes what was refused; it names the way through.
    /// </summary>
    public static class RevitFailureHints
    {
        /// <summary>A sentence to append (leading space included), or "" when no hint applies.</summary>
        public static string For(string revitText)
        {
            if (string.IsNullOrEmpty(revitText)) return "";
            if (Has(revitText, "connected in a network and can no longer keep the connectivity"))
                return " What to do: Revit will not move a connected family away from its network. Move the whole " +
                       "connected run (curves and fittings) in the same batch, or disconnect it first with " +
                       "horizun_connect_mep (Pro edition) and reconnect and verify after the move. Retrying the same move fails the same way.";
            if (Has(revitText, "Fitting cannot be created between the input connectors because the angle"))
                return " What to do: the two connectors are almost aligned or too sharply turned for an elbow. For " +
                       "aligned ends join them with a straight segment or a union; for a sharp turn add an intermediate " +
                       "segment so each fitting turns less. Retrying the same pair fails the same way.";
            return "";
        }

        private static bool Has(string text, string needle) =>
            text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
