// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// The owner's switch for the "sync only" permission (M10, Core/SyncOnlyGrantRules.cs).
//
// OFF on a fresh install, and only a person at THIS Revit can turn it on: the dialog is one
// an MCP caller can neither see nor answer, and the flag it writes (allow_sync_only_grant)
// sits in the settings file no MCP call writes. It does not widen the permission profile:
// under safe_write it opens operation=sync_with_central of horizun_save_document, and only
// through the strict precheck.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using BridgeSettings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit
{
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SyncOnlyGrantCommand : IExternalCommand
    {
        internal static string Title(bool es) =>
            RibbonText.T(es, "Solo sincronizar (estricto)", "Sync only (strict)");

        internal static string Detail(bool es) =>
            SyncOnlyGrantRules.ExperimentalNote(es) + RibbonText.T(es,
                "Deja que el asistente sincronice con el central sin darle el modo completo, siempre con comentario, sin cambios propios sin verificar, devolviendo solo lo tuyo y releyendo después.",
                "Lets the assistant synchronize with central without the full mode, always with a comment, no unverified changes of its own, giving back only what is yours and re-reading afterwards.");

        internal static string State(bool es, bool enabled) =>
            enabled ? RibbonText.T(es, "permitido", "allowed") : RibbonText.T(es, "no permitido (por defecto)", "not allowed (default)");

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool es = RibbonText.IsSpanish(data);
                bool enabling = !BridgeSettings.SyncOnlyGrantEnabled;
                var dialog = new TaskDialog("Horizun — " + Title(es))
                {
                    MainInstruction = enabling
                        ? RibbonText.T(es, "¿Permitir que el asistente solo sincronice con el central?", "Allow the assistant to only synchronize with central?")
                        : RibbonText.T(es, "El asistente dejará de poder sincronizar con esta opción.", "The assistant will no longer be able to synchronize with this option."),
                    MainContent = enabling
                        ? SyncOnlyGrantRules.EnableDialogText(es, SyncCentralPermissionCommand.Title(es), RibbonText.CentralTitle(es))
                        : RibbonText.T(es,
                            "Se vuelve al comportamiento anterior: la sincronización del asistente depende solo de «Sincronizar con el central» y del modo completo. Tu propio «Sincronizar con el central» de Revit no cambia.",
                            "The earlier behaviour returns: the assistant's synchronize depends only on \"Synchronize with central\" and the full mode. Revit's own Synchronize with Central is unchanged."),
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    enabling ? RibbonText.T(es, "Permitir", "Allow") : RibbonText.T(es, "No permitir", "Do not allow"));
                using (Interference.WithDialogAnswer(DialogAnswer.Human))
                    if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
                if (!BridgeSettings.TrySetSyncOnlyGrant(enabling, out string error))
                {
                    message = error;
                    return Result.Failed;
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
