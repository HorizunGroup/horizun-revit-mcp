// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The only place this add-in is VISIBLE inside Revit.
//
// It ran headless for its whole life: a pipe, a discovery file and a log. That is
// the right shape for a bridge, and it had one consequence nobody had weighed —
// on a machine where it is installed and working, there is no way to tell. "Is
// Horizun loaded?" was answered by opening a log file, and "which version?" by
// reading a DLL. For a tool people are meant to adopt, invisible is indistinct
// from absent.
//
// So: one tab, two panels, four buttons. STATUS answers the support question
// without leaving Revit — loaded, which version, which commit, is the bridge
// listening, where is the log. PYTHON makes arbitrary-code consent a local,
// visible, persistent human action. HUB is where the layer above this one lives.
// ADVANCED OPTIONS opens one Horizun-styled menu with the owner-local controls
// (BIM mode, action history, pause MCP, central protection), each shown with
// its current state. Every label, tooltip and dialog follows the language Revit
// itself runs in (RibbonText).
//
// A ribbon must never be the reason Revit fails to start: everything here is
// wrapped, and a failure is logged and swallowed. The bridge does not depend on
// any of it.
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using BridgeSettings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit
{
    internal static class Ribbon
    {
        internal const string TabName = "Horizun Hub";
        internal const string PanelName = "Horizun RVT MCP";
        internal const string HubUrl = "https://horizunhub.com";

        /// <summary>
        /// Build the Horizun Hub tab. Never throws: a ribbon that fails to build must
        /// not stop the bridge, which is the part that does the work.
        /// </summary>
        internal static void Build(UIControlledApplication app)
        {
            // Revit throws if the tab already exists - a second Horizun add-in, or a
            // reload. An existing tab is a success, not a failure, so it is caught
            // narrowly rather than by wrapping the whole method and losing real errors.
            try { app.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }

            RibbonPanel panel = app.CreateRibbonPanel(TabName, PanelName);
            string asm = Assembly.GetExecutingAssembly().Location;
            bool es = RibbonText.IsSpanish(app.ControlledApplication.Language);

            var status = new PushButtonData(
                "HorizunBridgeStatus", RibbonText.StatusLabel(es), asm, typeof(BridgeStatusCommand).FullName)
            {
                ToolTip = RibbonText.StatusTooltip(es),
                LongDescription = RibbonText.StatusDescription(es)
            };

            var hub = new PushButtonData(
                "HorizunOpenHub", RibbonText.HubLabel(es), asm, typeof(OpenHubCommand).FullName)
            {
                ToolTip = RibbonText.HubTooltip(es),
                LongDescription = RibbonText.HubDescription(es)
            };

            var python = new PushButtonData(
                "HorizunPythonPermission", RibbonText.PythonLabel(es), asm, typeof(PythonPermissionCommand).FullName)
            {
                ToolTip = RibbonText.PythonTooltip(es),
                LongDescription = RibbonText.PythonDescription(es)
            };

            AddImages(status, "status");
            AddImages(hub, "hub");
            AddImages(python, "status");

            panel.AddItem(status);
            panel.AddItem(python);
            panel.AddItem(hub);

            // The owner-local controls, behind one button. Four buttons for four
            // rarely-touched switches spent ribbon space and taught nothing; one
            // menu names each control, says what it does and shows its state.
            RibbonPanel production = app.CreateRibbonPanel(TabName, RibbonText.ProductionPanel(es));
            var advanced = new PushButtonData(
                "HorizunAdvancedOptions", RibbonText.AdvancedLabel(es), asm, typeof(AdvancedOptionsCommand).FullName)
            {
                ToolTip = RibbonText.AdvancedTooltip(es),
                LongDescription = RibbonText.AdvancedDescription(es)
            };
            AddImages(advanced, "status");
            production.AddItem(advanced);
        }

        /// <summary>
        /// Icons live beside the DLL and are loaded by path. Best effort: a button with
        /// no image is a plain button, and that is a far better outcome than a tab that
        /// does not appear because an image was missing.
        /// </summary>
        private static void AddImages(PushButtonData b, string name)
        {
            try
            {
                string dir = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Resources");
                string large = Path.Combine(dir, name + "32.png");
                string small = Path.Combine(dir, name + "16.png");
                if (File.Exists(large)) b.LargeImage = new BitmapImage(new Uri(large));
                if (File.Exists(small)) b.Image = new BitmapImage(new Uri(small));
            }
            catch { }
        }
    }

    /// <summary>
    /// The "Advanced options" button: shows the Horizun menu, then runs exactly the
    /// command the chosen row stands for. The menu decides nothing by itself.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AdvancedOptionsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool es = RibbonText.IsSpanish(data);
                IntPtr owner = IntPtr.Zero;
                try { owner = data.Application.MainWindowHandle; } catch { }

                string selected;
                using (Interference.WithDialogAnswer(DialogAnswer.Human))
                {
                    var window = new AdvancedOptionsWindow(es, owner);
                    window.ShowDialog();
                    selected = window.Selected;
                }
                switch (selected)
                {
                    case AdvancedOptionsWindow.OptionMode: return new BimModeCommand().Execute(data, ref message, elements);
                    case AdvancedOptionsWindow.OptionHistory: return new AuditHistoryCommand().Execute(data, ref message, elements);
                    case AdvancedOptionsWindow.OptionPause: return new PauseMcpCommand().Execute(data, ref message, elements);
                    case AdvancedOptionsWindow.OptionCentral: return new CentralProtectionCommand().Execute(data, ref message, elements);
                    default: return Result.Cancelled;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class BridgeStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool es = RibbonText.IsSpanish(data);
                string year = data.Application.Application.VersionNumber;
                // Read from DISK, not from a field: the question is whether an MCP client
                // could connect right now, and the discovery file is what a client reads.
                // A field would report what we intended, which is not the same thing.
                string discovery = Path.Combine(Discovery.Dir(), Discovery.FileName(year));
                bool published = File.Exists(discovery);

                var td = new TaskDialog("Horizun RVT MCP")
                {
                    MainInstruction = published
                        ? RibbonText.T(es, "El asistente puede conectarse a este Revit.", "The assistant can connect to this Revit.")
                        : RibbonText.T(es, "El asistente NO puede conectarse a este Revit.", "The assistant can NOT connect to this Revit."),
                    MainContent =
                        RibbonText.T(es, "Versión de Horizun: ", "Horizun version: ") + Build.Version +
                            "  (" + (Build.Commit ?? RibbonText.T(es, "compilación desconocida", "unknown build")) +
                            (Build.BuiltFromCleanTree ? "" : RibbonText.T(es, ", con cambios sin confirmar", ", with uncommitted changes")) + ")\n" +
                        "Revit: " + year + "\n\n" +
                        RibbonText.T(es, "Qué puede hacer el asistente: ", "What the assistant may do: ") + RibbonText.ModeName(es, BridgeSettings.PermissionProfile) + "\n" +
                        RibbonText.T(es, "Ahora mismo está: ", "Right now it is: ") +
                            (Dispatcher.CurrentActivityDescription() ?? RibbonText.T(es, "sin hacer nada; Revit está libre", "doing nothing; Revit is free")) + "\n" +
                        RibbonText.T(es, "Asistente: ", "Assistant: ") + (BridgeSettings.McpPaused ? RibbonText.T(es, "EN PAUSA", "PAUSED") : RibbonText.T(es, "activo", "active")) + "\n" +
                        RibbonText.T(es, "Modelos compartidos: ", "Shared models: ") +
                            (BridgeSettings.ForceReadOnlyOnWorkshared ? RibbonText.T(es, "protegidos (solo consulta)", "protected (look only)") : RibbonText.T(es, "sin protección especial", "no special protection")) + "\n" +
                        PythonStatusLine(es) + "\n\n" +
                        (published
                            ? RibbonText.T(es,
                                "Conexión lista. Si el asistente aun así no responde, casi siempre es que el " +
                                "complemento de Revit y el asistente tienen versiones distintas: pídele al asistente " +
                                "que revise el estado de la conexión.",
                                "Connection ready. If the assistant still does not respond, the Revit add-in and " +
                                "the assistant almost always have different versions: ask the assistant to check " +
                                "the connection status.")
                            : RibbonText.T(es,
                                "Este Revit no está anunciado, así que el asistente no puede encontrarlo. El registro " +
                                "explica qué falló al arrancar.",
                                "This Revit is not announced, so the assistant cannot find it. The log explains what " +
                                "failed at start-up.")) +
                        "\n\n" + RibbonText.T(es, "Registro (para soporte): ", "Log (for support): ") + Log.PathFor(year),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, RibbonText.T(es, "Abrir el registro", "Open the log"));
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, RibbonText.T(es, "Abrir Horizun Hub", "Open Horizun Hub"));

                TaskDialogResult r = td.Show();
                if (r == TaskDialogResult.CommandLink1) OpenPath(Log.PathFor(year));
                else if (r == TaskDialogResult.CommandLink2) OpenPath(Ribbon.HubUrl);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        internal static void OpenPath(string target)
        {
            // UseShellExecute is the default on .NET Framework and false on .NET Core,
            // where a URL then fails with "the specified executable is not a valid
            // application". Set explicitly so both runtimes behave the same.
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("could not open '" + target + "': " + ex.Message); }
        }

        internal static string PythonStatusLine(bool spanish)
        {
            bool allowed = BridgeSettings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _);
            DateTimeOffset? until = BridgeSettings.ExecutePythonTemporaryGrantUntilUtc;
            if (!allowed) return spanish ? "Scripts de Python: bloqueados" : "Python scripts: blocked";
            if (until != null)
                return (spanish
                    ? "Scripts de Python: permitidos por un permiso temporal antiguo hasta "
                    : "Python scripts: allowed under an old temporary permission until ") +
                    until.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            return spanish
                ? "Scripts de Python: permitidos hasta que los apagues"
                : "Python scripts: allowed until you turn them off";
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class PythonPermissionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool allowed = BridgeSettings.IsToolAllowed(Contract.Find("horizun_execute_python"), out string refusal);
                bool spanish = RibbonText.IsSpanish(data);
                if (allowed) return Disable(ref message, spanish);
                return Enable(ref message, refusal, null, spanish);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        internal static Result Enable(
            ref string message, string currentRefusal, string requestReason, bool spanish)
        {
            string title = spanish ? "Horizun — permiso Python" : "Horizun — Python permission";
            var dialog = new TaskDialog(title)
            {
                MainInstruction = spanish ? "Python está OFF." : "Python is OFF.",
                MainContent = spanish
                    ? "Activarlo permite que un cliente MCP ejecute código arbitrario dentro de Revit con sus " +
                      "permisos de Windows. Las herramientas tipadas verifican sus cambios; Python no puede " +
                      "ofrecer esa garantía.\n\nLa autorización NO EXPIRA: permanecerá activa entre archivos, " +
                      "lotes y reinicios de Revit hasta que este usuario la desactive manualmente. " +
                      "Los clientes MCP compatibles refrescan tools/list automáticamente. Si el suyo no lo hace, " +
                      "reinícielo una vez."
                    : "Enabling it allows an MCP client to run arbitrary code inside Revit with your Windows " +
                      "permissions. Typed tools verify their changes; Python cannot provide that guarantee.\n\n" +
                      "This permission DOES NOT EXPIRE: it remains active across files, batches and Revit " +
                      "restarts until this user manually disables it. Compatible MCP clients refresh tools/list " +
                      "automatically. If yours does not, restart it once.",
                ExpandedContent =
                    (string.IsNullOrWhiteSpace(requestReason)
                        ? ""
                        : (spanish
                            ? "Solicitud declarada por el cliente MCP (texto no verificado):\n"
                            : "Reason declared by the MCP client (unverified text):\n") + requestReason + "\n\n") +
                    (currentRefusal ?? ""),
                CommonButtons = TaskDialogCommonButtons.Cancel,
                VerificationText = spanish
                    ? "Entiendo que ejecuta código arbitrario y permanecerá activo hasta que yo lo desactive"
                    : "I understand this runs arbitrary code and remains active until I disable it"
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                spanish ? "Activar Python hasta que yo lo desactive" : "Enable Python until I disable it");
            TaskDialogResult choice = ShowForHuman(dialog);
            if (choice != TaskDialogResult.CommandLink1) return Result.Cancelled;
            if (!dialog.WasVerificationChecked())
            {
                ShowForHuman(title, spanish
                    ? "No se activó. Marque la casilla de comprensión para conceder el permiso."
                    : "Python was not enabled. Check the acknowledgement box to grant permission.");
                return Result.Cancelled;
            }

            if (!BridgeSettings.TryGrantExecutePythonPersistently(out string error))
            {
                message = error;
                ShowForHuman(title, error);
                return Result.Failed;
            }

            if (!BridgeSettings.IsToolAllowed(Contract.Find("horizun_execute_python"), out string stillRefused))
            {
                BridgeSettings.TryRevokeExecutePython(out _);
                message = stillRefused;
                ShowForHuman(title, (spanish
                    ? "No se activó porque otra política de la máquina lo prohíbe:\n\n"
                    : "Python was not enabled because another machine policy prohibits it:\n\n") + stillRefused);
                return Result.Failed;
            }

            ShowForHuman(title, spanish
                ? "Python está ON de forma persistente. Permanecerá activo hasta que este usuario lo desactive " +
                  "desde el botón Python ON/OFF o con el comando administrativo -Disable.\n\nLos clientes " +
                  "compatibles actualizarán la herramienta automáticamente; si el suyo no lo hace, reinícielo una vez."
                : "Python is persistently ON. It remains active until this user disables it from the Python " +
                  "ON/OFF button or with the administrative -Disable command.\n\nCompatible clients update the " +
                  "tool automatically; if yours does not, restart it once.");
            return Result.Succeeded;
        }

        private static TaskDialogResult ShowForHuman(TaskDialog dialog)
        {
            // Calls originating in MCP run under the global dialog watcher. Its normal
            // fail-safe answer is Cancel; this narrowly scoped policy tells it to observe
            // this consent UI but never answer it for the human.
            using (Interference.WithDialogAnswer(DialogAnswer.Human)) return dialog.Show();
        }

        private static void ShowForHuman(string title, string content)
        {
            using (Interference.WithDialogAnswer(DialogAnswer.Human)) TaskDialog.Show(title, content);
        }

        private static Result Disable(ref string message, bool spanish)
        {
            string title = spanish ? "Horizun — permiso Python" : "Horizun — Python permission";
            var dialog = new TaskDialog(title)
            {
                MainInstruction = spanish ? "Python está ON." : "Python is ON.",
                MainContent = BridgeStatusCommand.PythonStatusLine(spanish) +
                    (spanish
                        ? "\n\nDesactivarlo se aplica a la siguiente llamada incluso si el cliente MCP todavía muestra la herramienta."
                        : "\n\nDisabling it applies to the next call even if the MCP client still displays the tool."),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                spanish ? "Desactivar Python ahora" : "Disable Python now");
            if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;

            if (!BridgeSettings.TryRevokeExecutePython(out string error))
            {
                message = error;
                TaskDialog.Show(title, error);
                return Result.Failed;
            }

            TaskDialog.Show(title, spanish
                ? "Python está OFF. Los clientes compatibles retirarán la herramienta automáticamente; si el suyo no lo hace, reinícielo una vez."
                : "Python is OFF. Compatible clients remove the tool automatically; if yours does not, restart it once.");
            return Result.Succeeded;
        }

        /// <summary>Kept for callers outside this file; the decision lives in RibbonText.</summary>
        internal static bool IsSpanishLanguage(object language) => RibbonText.IsSpanish(language);
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class OpenHubCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            BridgeStatusCommand.OpenPath(Ribbon.HubUrl);
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// A small, local permission control rather than a second control plane. It writes
    /// the same settings file the server and dispatcher read before every call, so a
    /// changed mode is effective on the next MCP request without restarting Revit.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class BimModeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool es = RibbonText.IsSpanish(data);
                string current = BridgeSettings.PermissionProfile;
                string title = RibbonText.T(es, "Horizun — ¿Qué puede hacer el asistente?", "Horizun — What may the assistant do?");
                var dialog = new TaskDialog(title)
                {
                    MainInstruction = RibbonText.T(es, "Ahora: ", "Now: ") + RibbonText.ModeName(es, current),
                    MainContent = RibbonText.T(es,
                        "Elige hasta dónde puede llegar el asistente en este Revit. Los scripts de Python se " +
                        "permiten aparte, con el botón «Permitir scripts».",
                        "Choose how far the assistant may go in this Revit. Python scripts are allowed " +
                        "separately, with the \"Allow scripts\" button."),
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    RibbonText.T(es, "Solo consultar", "Look only"),
                    RibbonText.T(es, "Puede mirar el modelo y responder preguntas; no cambia nada ni crea archivos.",
                                     "It may look at the model and answer questions; it changes nothing and creates no files."));
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                    RibbonText.T(es, "Modificar el modelo abierto", "Change the open model"),
                    RibbonText.T(es, "Puede crear y editar elementos con comandos que comprueban cada cambio, solo en el archivo abierto.",
                                     "It may create and edit elements with commands that check every change, only in the open file."));
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
                    RibbonText.T(es, "Modificar, exportar y manejar documentos", "Change, export and handle documents"),
                    RibbonText.T(es, "Además puede exportar (PDF, DWG…), abrir, cerrar y guardar documentos. Actívalo solo si lo autorizas.",
                                     "It may also export (PDF, DWG…), open, close and save documents. Enable it only if you authorise it."));

                TaskDialogResult selected;
                using (Interference.WithDialogAnswer(DialogAnswer.Human)) selected = dialog.Show();
                string profile = selected == TaskDialogResult.CommandLink1 ? "read_only" :
                                 selected == TaskDialogResult.CommandLink2 ? "safe_write" :
                                 selected == TaskDialogResult.CommandLink3 ? "full_write" : null;
                if (profile == null) return Result.Cancelled;

                if (profile == "full_write")
                {
                    var confirm = new TaskDialog(RibbonText.T(es, "Horizun — confirmar", "Horizun — confirm"))
                    {
                        MainInstruction = RibbonText.T(es,
                            "Este nivel deja al asistente crear archivos y abrir, cerrar o guardar documentos.",
                            "This level lets the assistant create files and open, close or save documents."),
                        MainContent = RibbonText.T(es,
                            "No permite scripts de Python. Confirma solo si autorizas esos efectos en este equipo.",
                            "It does not allow Python scripts. Confirm only if you authorise those effects on this computer."),
                        CommonButtons = TaskDialogCommonButtons.Cancel,
                        VerificationText = RibbonText.T(es, "Entiendo lo que permite este nivel", "I understand what this level allows")
                    };
                    confirm.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, RibbonText.T(es, "Activar este nivel", "Enable this level"));
                    using (Interference.WithDialogAnswer(DialogAnswer.Human))
                    {
                        if (confirm.Show() != TaskDialogResult.CommandLink1 || !confirm.WasVerificationChecked())
                            return Result.Cancelled;
                    }
                }

                if (!BridgeSettings.TrySetPermissionProfile(profile, out string error))
                {
                    message = error;
                    return Result.Failed;
                }

                using (Interference.WithDialogAnswer(DialogAnswer.Human))
                    TaskDialog.Show(title, RibbonText.T(es, "Listo. Desde la siguiente acción del asistente: ", "Done. From the assistant's next action: ") + RibbonText.ModeName(es, profile) + ".");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// Read-only window onto the dispatcher receipt ledger. It never fabricates a
    /// history from log lines: each row is a receipt built from the command reply.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AuditHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool es = RibbonText.IsSpanish(data);
                string title = RibbonText.T(es, "Horizun — ¿Qué ha hecho el asistente?", "Horizun — What has the assistant done?");
                string directory = ReceiptLedger.DefaultDirectory();
                if (!Directory.Exists(directory))
                {
                    using (Interference.WithDialogAnswer(DialogAnswer.Human))
                        TaskDialog.Show(title, RibbonText.T(es,
                            "Todavía no hay acciones registradas. Cuando el asistente haga algo en tus modelos quedará anotado aquí:\n",
                            "No actions recorded yet. When the assistant does something in your models it will be noted here:\n") + directory);
                    return Result.Succeeded;
                }

                string[] files = Directory.GetFiles(directory, "receipts-*.jsonl")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(7).ToArray();
                var rows = new System.Collections.Generic.List<string>();
                foreach (string file in files)
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(file); }
                    catch { continue; }
                    for (int i = lines.Length - 1; i >= 0 && rows.Count < 10; i--)
                    {
                        try
                        {
                            JObject row = JObject.Parse(lines[i]);
                            rows.Add((string)row["utc"] + "  " + (string)row["tool"] + "  " +
                                (string)row["outcome"] + "  " +
                                (row["dry_run"] == null ? "" : "dry_run=" + row["dry_run"] + "  ") +
                                (row["total_ms"] == null ? "" : row["total_ms"] + " ms"));
                        }
                        catch { /* malformed historical line is not a receipt */ }
                    }
                    if (rows.Count >= 10) break;
                }

                string content = rows.Count == 0
                    ? RibbonText.T(es, "Todavía no hay ninguna acción legible.", "No readable action yet.")
                    : string.Join("\n", rows);
                var dialog = new TaskDialog(title)
                {
                    MainInstruction = RibbonText.T(es, "Últimas ", "Latest ") + rows.Count +
                                      RibbonText.T(es, " acciones del asistente (fecha, acción, resultado, simulación, duración)", " assistant actions (date, action, outcome, rehearsal, duration)"),
                    MainContent = content + "\n\n" + RibbonText.T(es, "Carpeta con el registro completo: ", "Folder with the full record: ") + directory,
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, RibbonText.T(es, "Abrir la carpeta", "Open the folder"));
                TaskDialogResult answer;
                using (Interference.WithDialogAnswer(DialogAnswer.Human)) answer = dialog.Show();
                if (answer == TaskDialogResult.CommandLink1) BridgeStatusCommand.OpenPath(directory);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class PauseMcpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool es = RibbonText.IsSpanish(data);
                bool pausing = !BridgeSettings.McpPaused;
                string title = "Horizun — " + (pausing ? RibbonText.T(es, "pausar el asistente", "pause the assistant") : RibbonText.T(es, "reanudar el asistente", "resume the assistant"));
                var dialog = new TaskDialog(title)
                {
                    MainInstruction = pausing
                        ? RibbonText.T(es, "El asistente quedará en pausa.", "The assistant will be paused.")
                        : RibbonText.T(es, "El asistente volverá a funcionar.", "The assistant will work again."),
                    MainContent = pausing
                        ? RibbonText.T(es,
                            "No podrá hacer nada en Revit hasta que lo reanudes desde este mismo menú. Lo que ya esté en marcha termina; lo nuevo se rechaza. Solo podrá decir que está en pausa.",
                            "It will be able to do nothing in Revit until you resume it from this same menu. Whatever is already running finishes; anything new is refused. It will only be able to say that it is paused.")
                        : RibbonText.T(es,
                            "Volverá a poder trabajar con los permisos que tengas configurados.",
                            "It will be able to work again with the permissions you have configured."),
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    pausing ? RibbonText.T(es, "Pausar ahora", "Pause now") : RibbonText.T(es, "Reanudar ahora", "Resume now"));
                using (Interference.WithDialogAnswer(DialogAnswer.Human))
                    if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;

                if (!BridgeSettings.TrySetMcpPaused(pausing, out string error))
                {
                    message = error;
                    return Result.Failed;
                }
                using (Interference.WithDialogAnswer(DialogAnswer.Human))
                    TaskDialog.Show(title, pausing
                        ? RibbonText.T(es, "El asistente está en pausa.", "The assistant is paused.")
                        : RibbonText.T(es, "El asistente vuelve a estar activo.", "The assistant is active again."));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class CentralProtectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool es = RibbonText.IsSpanish(data);
                bool enabling = !BridgeSettings.ForceReadOnlyOnWorkshared;
                var dialog = new TaskDialog(RibbonText.T(es, "Horizun — modelos compartidos", "Horizun — shared models"))
                {
                    MainInstruction = enabling
                        ? RibbonText.T(es, "Los modelos compartidos quedarán protegidos: el asistente solo podrá consultarlos.", "Shared models will be protected: the assistant will only be able to look at them.")
                        : RibbonText.T(es, "Los modelos compartidos dejarán de estar protegidos.", "Shared models will no longer be protected."),
                    MainContent = enabling
                        ? RibbonText.T(es,
                            "Vale para los modelos de trabajo compartido (centrales, en la nube, con subproyectos) y para cualquier modelo del que Revit no pueda decir si es compartido. El asistente no los modificará, exportará ni cerrará; las simulaciones siguen permitidas.",
                            "It applies to shared models (central, cloud, with worksets) and to any model Revit cannot tell is shared or not. The assistant will not change, export or close them; rehearsals stay allowed.")
                        : RibbonText.T(es,
                            "Volverán a regirse por lo que elijas en «¿Qué puede hacer el asistente?».",
                            "They will follow again whatever you choose under \"What may the assistant do?\"."),
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    enabling ? RibbonText.T(es, "Proteger", "Protect") : RibbonText.T(es, "Quitar la protección", "Remove the protection"));
                using (Interference.WithDialogAnswer(DialogAnswer.Human))
                    if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
                if (!BridgeSettings.TrySetForceReadOnlyOnWorkshared(enabling, out string error))
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
