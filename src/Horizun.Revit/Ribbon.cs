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
// So: one tab, one panel, three buttons. STATUS answers the support question
// without leaving Revit — loaded, which version, which commit, is the bridge
// listening, where is the log. PYTHON makes arbitrary-code consent a local,
// visible, persistent human action. HUB is where the layer above this one lives.
//
// A ribbon must never be the reason Revit fails to start: everything here is
// wrapped, and a failure is logged and swallowed. The bridge does not depend on
// any of it.
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Horizun.Contracts;
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
            bool spanish = PythonPermissionCommand.IsSpanishLanguage(app.ControlledApplication.Language);

            var status = new PushButtonData(
                "HorizunBridgeStatus", "Estado\ndel puente", asm, typeof(BridgeStatusCommand).FullName)
            {
                ToolTip = "¿Está el puente activo, y con qué versión?",
                LongDescription =
                    "Muestra la versión y el commit del add-in cargado, si el canal con el cliente MCP está " +
                    "escuchando, y dónde está el registro. Es la primera pregunta de cualquier soporte, y se " +
                    "responde sin salir de Revit."
            };

            var hub = new PushButtonData(
                "HorizunOpenHub", "Horizun\nHub", asm, typeof(OpenHubCommand).FullName)
            {
                ToolTip = "Abrir Horizun Hub",
                LongDescription =
                    "Este puente es la mitad genérica: transporte, garantías y una superficie de comandos sobre " +
                    "la API de Revit. Los flujos de entrega construidos encima — auditorías de modelo, " +
                    "clasificación, homologación de familias, control de calidad de entrega — viven en Horizun Hub."
            };

            var python = new PushButtonData(
                "HorizunPythonPermission", "Python\nON/OFF", asm, typeof(PythonPermissionCommand).FullName)
            {
                ToolTip = spanish
                    ? "Activar persistentemente o revocar la ejecución Python"
                    : "Persistently enable or revoke Python execution",
                LongDescription = spanish
                    ? "horizun_execute_python ejecuta código arbitrario con los permisos del usuario. Está " +
                      "apagado por defecto. Este botón permite al dueño presente en Revit activarlo hasta que " +
                      "él mismo lo desactive, o revocarlo inmediatamente. La activación nunca puede concedérsela " +
                      "un cliente MCP por sí solo."
                    : "horizun_execute_python runs arbitrary code with the user's Windows permissions. It is " +
                      "off by default. This button lets the owner present in Revit enable it until they disable " +
                      "it themselves, or revoke it immediately. An MCP client can never grant itself access."
            };

            AddImages(status, "status");
            AddImages(hub, "hub");
            AddImages(python, "status");

            panel.AddItem(status);
            panel.AddItem(python);
            panel.AddItem(hub);

            // ---- the SESSION panel: what this session is about, and what it did. ----
            // A second panel rather than more buttons on the first, so "is it working"
            // and "what is it doing" read as the two different questions they are.
            RibbonPanel session = app.CreateRibbonPanel(TabName, spanish ? "Sesión" : "Session");

            var packs = new PushButtonData(
                "HorizunToolPacks", spanish ? "Packs de\nherramientas" : "Tool\npacks", asm,
                typeof(ToolPacksCommand).FullName)
            {
                ToolTip = spanish
                    ? "Qué herramientas ofrece esta sesión a los clientes MCP"
                    : "Which tools this session offers MCP clients",
                LongDescription = spanish
                    ? "Sesenta herramientas en un tools/list cuestan contexto. Un pack es un subconjunto con " +
                      "nombre; aquí se elige, se guarda por usuario, y los clientes compatibles se actualizan " +
                      "sin reiniciar. Un pack decide visibilidad, nunca privilegios: el perfil de permisos " +
                      "sigue mandando."
                    : "Sixty tools in one tools/list cost context. A pack is a named subset; choose here, it " +
                      "persists per user, and compatible clients refresh without a restart. A pack decides " +
                      "visibility, never privilege: the permission profile stays the authority."
            };

            var security = new PushButtonData(
                "HorizunSecurity", spanish ? "Seguridad" : "Security", asm,
                typeof(SecurityStatusCommand).FullName)
            {
                ToolTip = spanish
                    ? "Perfil de escritura, estado de Python y su origen"
                    : "Write profile, Python state and its origin",
                LongDescription = spanish
                    ? "Qué puede cambiar este puente y quién lo decidió: el perfil " +
                      "read_only/safe_write/full_write/unsafe_code, si Python está activo, de dónde salió esa " +
                      "autorización, y los últimos cambios hechos desde la cinta."
                    : "What this bridge may change and who decided it: the read_only/safe_write/full_write/" +
                      "unsafe_code profile, whether Python is on, where that authorization came from, and the " +
                      "recent ribbon changes."
            };

            var planimetry = new PushButtonData(
                "HorizunPlanimetryReview", spanish ? "Revisión de\nplanimetría" : "Planimetry\nreview", asm,
                typeof(PlanimetryReviewCommand).FullName)
            {
                ToolTip = spanish
                    ? "Auditar el documento activo con las reglas universales (solo lectura)"
                    : "Audit the active document under the universal rules (read-only)",
                LongDescription = spanish
                    ? "Ejecuta la MISMA auditoría de planimetría que un cliente MCP - no una copia - y resume " +
                      "los hallazgos por severidad y regla. Esta ventana solo muestra: corregir pasa siempre " +
                      "por horizun_fix_planimetry, con su ensayo y su confirmación."
                    : "Runs the SAME planimetry audit an MCP client runs - not a copy - and summarises the " +
                      "findings by severity and rule. This window only shows: correcting always goes through " +
                      "horizun_fix_planimetry, with its rehearsal and confirmation."
            };

            var jobs = new PushButtonData(
                "HorizunJobs", spanish ? "Trabajos" : "Jobs", asm, typeof(JobsStatusCommand).FullName)
            {
                ToolTip = spanish
                    ? "Trabajos asíncronos: en cola, corriendo, terminados"
                    : "Asynchronous jobs: queued, running, finished",
                LongDescription = spanish
                    ? "Los registros durables de horizun_submit_job, leídos de disco: cuántos esperan, cuántos " +
                      "corren (o murieron con su proceso - horizun_job_status los distingue), cuántos " +
                      "terminaron y cómo."
                    : "The durable records behind horizun_submit_job, read from disk: how many wait, how many " +
                      "run (or died with their process - horizun_job_status tells them apart), how many " +
                      "finished and how."
            };

            AddImages(packs, "hub");
            AddImages(security, "status");
            AddImages(planimetry, "status");
            AddImages(jobs, "hub");

            session.AddItem(packs);
            session.AddItem(security);
            session.AddItem(planimetry);
            session.AddItem(jobs);
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

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class BridgeStatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                string year = data.Application.Application.VersionNumber;
                // Read from DISK, not from a field: the question is whether an MCP client
                // could connect right now, and the discovery file is what a client reads.
                // A field would report what we intended, which is not the same thing.
                string discovery = Path.Combine(Discovery.Dir(), Discovery.FileName(year));
                bool published = File.Exists(discovery);

                var td = new TaskDialog("Horizun RVT MCP")
                {
                    MainInstruction = published
                        ? "El puente está activo."
                        : "El puente NO está publicado.",
                    MainContent =
                        "Versión: " + Build.Version + "\n" +
                        "Commit: " + (Build.Commit ?? "desconocido") +
                        (Build.BuiltFromCleanTree ? "" : "  (árbol con cambios sin confirmar)") + "\n" +
                        "Revit: " + year + "\n\n" +
                        "Perfil: " + BridgeSettings.PermissionProfile + "\n" +
                        PythonStatusLine(PythonPermissionCommand.IsSpanishLanguage(
                            data.Application.Application.Language)) + "\n\n" +
                        (published
                            ? "Descubrimiento: " + discovery + "\n\nUn cliente MCP que arranque ahora encontrará " +
                              "este Revit. Si aun así falla, casi siempre es que el servidor y el add-in vienen " +
                              "de commits distintos: compáralos con horizun_health."
                            : "No hay fichero de descubrimiento, así que ningún cliente MCP puede encontrar este " +
                              "Revit. El registro dice por qué falló el arranque.") +
                        "\n\nRegistro: " + Log.PathFor(year),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir el registro");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Abrir Horizun Hub");

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
            if (!allowed) return "Python: OFF";
            if (until != null)
                return (spanish
                    ? "Python: ON por un permiso temporal heredado hasta "
                    : "Python: ON under a legacy temporary grant until ") +
                    until.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            return spanish
                ? "Python: ON persistente hasta que el usuario lo desactive"
                : "Python: persistently ON until the user disables it";
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
                bool spanish = IsSpanishLanguage(data.Application.Application.Language);
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

        internal static bool IsSpanishLanguage(object language)
        {
            string value = language == null ? "" : language.ToString();
            return value.IndexOf("Spanish", StringComparison.OrdinalIgnoreCase) >= 0;
        }
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
}
