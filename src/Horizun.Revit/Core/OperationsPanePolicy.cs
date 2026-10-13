namespace Horizun.Revit.Core
{
    /// <summary>
    /// When the operations pane is shown, and when it reads anything (owner, 2026-10-12).
    ///
    /// IT NEVER OPENS BY ITSELF. Revit shows a registered pane the first time an add-in loads unless its
    /// provider says otherwise, and on every later start it restores the state the pane had when the last
    /// session closed - so a pane opened once came back every time, on top of somebody's model. The pane is
    /// registered hidden, and one Revit restored as shown is hidden again at start-up. The ribbon button is
    /// the only way it opens.
    ///
    /// A HIDDEN PANE READS NOTHING. Its timer used to re-read the job records and the receipt ledger every two
    /// seconds whether anybody could see the result or not.
    /// </summary>
    public static class OperationsPanePolicy
    {
        /// <summary>DockablePaneProviderData.VisibleByDefault: the first load of the add-in.</summary>
        public const bool VisibleByDefault = false;

        /// <summary>Hide the pane at start-up when Revit restored it as shown from the last session.</summary>
        public static bool HideAtStartUp(bool shownByRevit) => shownByRevit;

        /// <summary>Whether the refresh timer reads the records on this tick.</summary>
        public static bool Refreshes(bool paneVisible) => paneVisible;
    }
}
