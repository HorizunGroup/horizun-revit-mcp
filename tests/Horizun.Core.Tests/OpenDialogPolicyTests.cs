// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// How on_open_dialog parses (story 5.22). Cancel is the default and the safe
// unattended answer; dismiss is the deliberate opt-in. A typo is an ERROR, never
// a silent fall back to cancel - a caller who meant to acknowledge dialogs must
// not be told the model simply would not open.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OpenDialogPolicyTests
    {
        [Fact]
        public void Absent_or_empty_is_cancel_the_safe_default()
        {
            string err;
            Assert.Equal(DialogAnswer.Cancel, OpenDialogPolicy.Parse(null, out err));
            Assert.Null(err);
            Assert.Equal(DialogAnswer.Cancel, OpenDialogPolicy.Parse("", out err));
            Assert.Null(err);
            Assert.Equal(DialogAnswer.Cancel, OpenDialogPolicy.Parse("   ", out err));
            Assert.Null(err);
        }

        [Fact]
        public void Cancel_and_dismiss_parse_case_and_whitespace_tolerantly()
        {
            string err;
            Assert.Equal(DialogAnswer.Cancel, OpenDialogPolicy.Parse("cancel", out err));
            Assert.Null(err);
            Assert.Equal(DialogAnswer.Dismiss, OpenDialogPolicy.Parse("dismiss", out err));
            Assert.Null(err);
            Assert.Equal(DialogAnswer.Dismiss, OpenDialogPolicy.Parse(" DISMISS ", out err));
            Assert.Null(err);
        }

        [Fact]
        public void An_unknown_value_is_an_error_not_a_silent_cancel()
        {
            string err;
            DialogAnswer a = OpenDialogPolicy.Parse("acknowledge", out err);

            // It returns Cancel as the safe value, but the ERROR is set so the caller
            // refuses rather than silently opening with the wrong policy.
            Assert.Equal(DialogAnswer.Cancel, a);
            Assert.NotNull(err);
            Assert.Contains("cancel", err);
            Assert.Contains("dismiss", err);
            Assert.Contains("acknowledge", err);   // echoes what was passed
        }
    }
}
