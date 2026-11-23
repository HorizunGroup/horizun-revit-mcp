// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The NON-MODAL consent window behind horizun_request_python_access.
//
// The ribbon's Python ON/OFF button keeps its modal TaskDialog: a person clicked it
// and is waiting for it. A request from an MCP client is different - its modal dialog
// held Revit's UI thread for up to 600 s (field report), so every other call queued
// and an unattended client timed out with Revit still blocked. This window is shown
// with Show(), not ShowDialog(): the command returns at once with pending_approval
// and Revit keeps working while the question waits.
//
// The owner-only rule is the same as the ribbon's: the grant is written ONLY by this
// window's Enable button, which is enabled ONLY after the acknowledgement box is
// ticked by the person, and a grant counts only if Settings.IsToolAllowed agrees
// afterwards (otherwise it is revoked and recorded as failed). Nothing the MCP client
// sends can press the button. Closing the window, Cancel or Escape is a decline; a
// window nobody answers closes itself when its request expires.
//
// Built in code (no XAML), owned by Revit's main window, like AdvancedOptionsWindow.
// No Revit API is called from its handlers: the grant is a settings-file write.
// -----------------------------------------------------------------------------
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Horizun.Contracts;
using Horizun.Revit.Core;
using BridgeSettings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit
{
    internal sealed class PythonAccessWindow : Window
    {
        private readonly string _requestId;
        private bool _decided;

        /// <summary>Shows the window for this request and returns at once. Never throws: a failure is recorded on the request.</summary>
        internal static void ShowFor(PythonAccessRequest request, string currentRefusal, bool spanish, IntPtr owner)
        {
            try
            {
                var w = new PythonAccessWindow(request, currentRefusal, spanish, owner);
                w.Show();
                w.Activate();
            }
            catch (Exception ex)
            {
                PythonAccessRequests.Decide(request.Id, PythonAccessRequests.Failed, "The consent window could not be shown: " + ex.Message);
                Log.Warn("python access window could not be shown: " + ex.Message);
            }
        }

        private PythonAccessWindow(PythonAccessRequest request, string currentRefusal, bool es, IntPtr owner)
        {
            _requestId = request.Id;
            Title = es ? "Horizun — permiso Python (solicitud de un cliente MCP)" : "Horizun — Python permission (request from an MCP client)";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = true;
            ShowActivated = true;
            WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI");
            Background = Brushes.White;
            if (owner != IntPtr.Zero) new WindowInteropHelper(this).Owner = owner;

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            root.Children.Add(new TextBlock
            {
                Text = es ? "Un cliente MCP pide activar Python. Python está OFF." : "An MCP client asks to enable Python. Python is OFF.",
                FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(new TextBlock
            {
                Text = es
                    ? "Activarlo permite que un cliente MCP ejecute código arbitrario dentro de Revit con sus permisos de Windows. " +
                      "Las herramientas tipadas verifican sus cambios; Python no puede ofrecer esa garantía.\n\nLa autorización NO " +
                      "EXPIRA: permanecerá activa entre archivos, lotes y reinicios de Revit hasta que este usuario la desactive " +
                      "con el botón Python ON/OFF. Revit sigue funcionando mientras esta ventana está abierta."
                    : "Enabling it allows an MCP client to run arbitrary code inside Revit with your Windows permissions. Typed " +
                      "tools verify their changes; Python cannot provide that guarantee.\n\nThis permission DOES NOT EXPIRE: it " +
                      "remains active across files, batches and Revit restarts until this user disables it with the Python " +
                      "ON/OFF button. Revit keeps working while this window is open.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 10)
            });
            if (!string.IsNullOrWhiteSpace(request.Reason))
                root.Children.Add(new TextBlock
                {
                    Text = (es ? "Motivo declarado por el cliente MCP (texto no verificado):\n" : "Reason declared by the MCP client (unverified text):\n") + request.Reason,
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10)
                });
            if (!string.IsNullOrWhiteSpace(currentRefusal))
                root.Children.Add(new TextBlock
                {
                    Text = currentRefusal, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 10)
                });

            var acknowledge = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = es ? "Entiendo que ejecuta código arbitrario y permanecerá activo hasta que yo lo desactive"
                              : "I understand this runs arbitrary code and remains active until I disable it",
                    TextWrapping = TextWrapping.Wrap
                },
                Margin = new Thickness(0, 4, 0, 14)
            };
            root.Children.Add(acknowledge);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var enable = new Button
            {
                Content = es ? "Activar Python hasta que yo lo desactive" : "Enable Python until I disable it",
                Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0), IsEnabled = false
            };
            var cancel = new Button { Content = es ? "No activar" : "Do not enable", Padding = new Thickness(14, 6, 14, 6), IsCancel = true };
            acknowledge.Checked += (s, e) => enable.IsEnabled = true;
            acknowledge.Unchecked += (s, e) => enable.IsEnabled = false;
            enable.Click += (s, e) => { if (acknowledge.IsChecked == true) Grant(es); };
            cancel.Click += (s, e) => { Record(PythonAccessRequests.Declined, "The person in Revit chose not to enable Python."); Close(); };
            buttons.Children.Add(enable);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            // Closing the window any other way is a decline, recorded once.
            Closed += (s, e) => Record(PythonAccessRequests.Declined, "The person in Revit closed the window without enabling Python.");

            // A question nobody answers does not stay on screen forever.
            TimeSpan left = request.ExpiresUtc - DateTime.UtcNow;
            if (left < TimeSpan.FromSeconds(1)) left = TimeSpan.FromSeconds(1);
            var timer = new DispatcherTimer { Interval = left };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                Record(PythonAccessRequests.Expired, "Nobody answered in Revit before the request expired; the window closed.");
                Close();
            };
            timer.Start();
        }

        private void Grant(bool es)
        {
            string error;
            if (!BridgeSettings.TryGrantExecutePythonPersistently(out error))
            {
                Record(PythonAccessRequests.Failed, error);
                MessageBox.Show(this, error, Title);
                Close();
                return;
            }
            if (!BridgeSettings.IsToolAllowed(Contract.Find("horizun_execute_python"), out string stillRefused))
            {
                BridgeSettings.TryRevokeExecutePython(out _);
                Record(PythonAccessRequests.Failed, "Another machine policy prohibits it: " + stillRefused);
                MessageBox.Show(this, (es ? "No se activó porque otra política de la máquina lo prohíbe:\n\n"
                                          : "Python was not enabled because another machine policy prohibits it:\n\n") + stillRefused, Title);
                Close();
                return;
            }
            Record(PythonAccessRequests.Granted, "The person in Revit enabled Python persistently.");
            Log.Info("python access request " + _requestId + " granted by the person in Revit");
            Close();
        }

        private void Record(string state, string detail)
        {
            if (_decided) return;
            _decided = true;
            PythonAccessRequests.Decide(_requestId, state, detail);
        }
    }
}
