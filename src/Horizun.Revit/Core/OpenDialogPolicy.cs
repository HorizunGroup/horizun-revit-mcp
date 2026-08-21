// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// How an open answers a modal dialog it raises, and how the argument parses
// (story 5.22). Revit-free on purpose: the enum and the parse rule are the part
// worth proving without a Revit in the room - the watcher that ACTS on the answer
// lives in Interference, which needs one.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    /// <summary>
    /// How a modal dialog is answered while a command runs. The default, and the only
    /// safe unattended answer in general, is Cancel: do not proceed on the user's
    /// behalf. Dismiss is the deliberate opt-in for READING a model whose open raises a
    /// dialog whose only unattended answer is "acknowledge and continue" - 6 of 123
    /// models in one batch could not be audited for want of it.
    /// </summary>
    public enum DialogAnswer
    {
        Cancel,
        Dismiss,

        // Never accepted from an MCP argument. Compiled Horizun consent UI uses this
        // internal policy so the global dialog watcher observes the prompt without
        // answering it on the machine owner's behalf.
        Human
    }

    public static class OpenDialogPolicy
    {
        /// <summary>
        /// Parse the on_open_dialog argument. Null/empty is Cancel (the default). Anything
        /// other than cancel|dismiss is an ERROR in <paramref name="error"/>, not a silent
        /// fallback: a caller who meant to acknowledge dialogs and mistyped the word must
        /// not be told the model simply would not open.
        /// </summary>
        public static DialogAnswer Parse(string raw, out string error)
        {
            error = null;
            string v = (raw ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0 || v == "cancel") return DialogAnswer.Cancel;
            if (v == "dismiss") return DialogAnswer.Dismiss;
            error = "on_open_dialog must be 'cancel' or 'dismiss' (got '" + raw + "'). Nothing was opened.";
            return DialogAnswer.Cancel;
        }
    }
}
