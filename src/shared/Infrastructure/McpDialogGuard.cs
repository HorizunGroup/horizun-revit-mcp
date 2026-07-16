// -----------------------------------------------------------------------------
// Horizun hardening layer — NEW FILE (added to the rvt-mcp base by Horizun).
// Apache-2.0 (see LICENSE); this file is an original Horizun contribution.
//
// Global modal-dialog suppression. A modal dialog raised while an MCP command
// runs on the Revit UI thread would block the entire pump until a human
// dismisses it (the classic "the bridge froze" failure). We answer such dialogs
// programmatically — but ONLY while an MCP command is in flight
// (IsMcpExecuting), so the interactive user's own dialogs are never touched.
//
// Technique is standard/public: UIControlledApplication.DialogBoxShowing +
// OverrideResult (see The Building Coder; RevitBatchProcessor). The event is
// not cancelable — we can only override the result, and an invalid code fails
// silently, so we log every DialogId to grow the override map.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RvtMcp.Plugin
{
    public static class McpDialogGuard
    {
        /// <summary>
        /// True only while a command runs on the UI thread (set by McpEventHandler
        /// around command.Execute). volatile: written on the UI thread, read from
        /// the dialog/failure event handlers.
        /// </summary>
        public static volatile bool IsMcpExecuting;

        private static bool _subscribed;

        public static void Subscribe(UIControlledApplication app)
        {
            if (_subscribed || app == null) return;
            app.DialogBoxShowing += OnDialogBoxShowing;
            app.ControlledApplication.FailuresProcessing += OnFailuresProcessing;
            _subscribed = true;
        }

        public static void Unsubscribe(UIControlledApplication app)
        {
            if (!_subscribed || app == null) return;
            try { app.DialogBoxShowing -= OnDialogBoxShowing; } catch { }
            try { app.ControlledApplication.FailuresProcessing -= OnFailuresProcessing; } catch { }
            _subscribed = false;
        }

        /// <summary>
        /// Known DialogId → override result. 1001 = first command link
        /// (TaskDialogResult.CommandLink1); 1 = IDOK; 1002 = "ignore and continue".
        /// Grow from the DialogIds logged below.
        /// </summary>
        private static int MapOverride(string dialogId)
        {
            switch (dialogId)
            {
                case "TaskDialog_Family_Already_Exists": return 1001;        // Overwrite existing version
                case "TaskDialog_Missing_Third_Party_Updaters": return 1001; // Continue working with the file
                case "TaskDialog_Unresolved_References": return 1002;        // Ignore and continue opening
                case "TaskDialog_Audit_Warning": return 1;                   // OK
                default: return 1001;
            }
        }

        private static void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            // Never touch dialogs raised by the interactive user.
            if (!IsMcpExecuting) return;

            try
            {
                if (e is TaskDialogShowingEventArgs td)
                {
                    App.DebugLog($"McpDialogGuard: suppress TaskDialog {td.DialogId}: {td.Message}");
                    td.OverrideResult(MapOverride(td.DialogId));
                }
                else if (e is MessageBoxShowingEventArgs mb)
                {
                    App.DebugLog($"McpDialogGuard: suppress MessageBox {mb.DialogId}");
                    mb.OverrideResult(1); // IDOK
                }
                else
                {
                    App.DebugLog($"McpDialogGuard: suppress dialog {e.DialogId}");
                    e.OverrideResult(1);
                }
            }
            catch (Exception ex)
            {
                App.DebugLog($"McpDialogGuard: override failed for {e.DialogId}: {ex.Message}");
            }
        }

        private static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            if (!IsMcpExecuting) return;

            try
            {
                var fa = e.GetFailuresAccessor();
                bool hasError = false;
                foreach (var f in fa.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning)
                        fa.DeleteWarning(f);
                    else
                        hasError = true;
                }
                e.SetProcessingResult(hasError
                    ? FailureProcessingResult.ProceedWithRollBack
                    : FailureProcessingResult.Continue);
            }
            catch (Exception ex)
            {
                App.DebugLog($"McpDialogGuard: failures hook failed: {ex.Message}");
            }
        }
    }
}
