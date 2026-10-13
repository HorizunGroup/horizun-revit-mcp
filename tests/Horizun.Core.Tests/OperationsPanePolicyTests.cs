using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// The operations pane never opens by itself (owner, 2026-10-12): not on the first load, and not because
    /// Revit restored it from the previous session. Only the ribbon button shows it.
    /// </summary>
    public class OperationsPanePolicyTests
    {
        [Fact]
        public void The_pane_is_registered_hidden()
        {
            Assert.False(OperationsPanePolicy.VisibleByDefault);
        }

        [Fact]
        public void A_pane_Revit_restored_as_shown_is_hidden_at_start_up()
        {
            Assert.True(OperationsPanePolicy.HideAtStartUp(shownByRevit: true));
            Assert.False(OperationsPanePolicy.HideAtStartUp(shownByRevit: false));
        }

        [Fact]
        public void A_hidden_pane_does_not_read_the_records()
        {
            Assert.False(OperationsPanePolicy.Refreshes(paneVisible: false));
            Assert.True(OperationsPanePolicy.Refreshes(paneVisible: true));
        }
    }
}
