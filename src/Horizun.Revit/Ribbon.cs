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
// So: one tab, one panel, two buttons. STATUS answers the support question
// without leaving Revit — loaded, which version, which commit, is the bridge
// listening, where is the log. HUB is where the layer above this one lives.
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

            AddImages(status, "status");
            AddImages(hub, "hub");

            panel.AddItem(status);
            panel.AddItem(hub);
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
