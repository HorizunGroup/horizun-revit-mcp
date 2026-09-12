// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// Every word the ribbon shows, in the language Revit itself is running in, and
// the one window the ribbon owns: "Advanced options".
//
// Revit reports its UI language (LanguageType); a Spanish Revit gets Spanish
// labels, tooltips and dialogs, anything else gets English. The choice is made
// once per call from the host's own answer, never from Windows or the culture,
// so a Spanish Windows running an English Revit reads English, like the rest of
// that Revit.
//
// The advanced options - BIM mode, action history, pause MCP, central protection
// - used to be four ribbon buttons. They are owner-local controls that a person
// touches rarely and deliberately, so they live behind ONE button that opens a
// small Horizun-styled menu: each row names the control, says what it does and
// shows its CURRENT state before anything is clicked. Clicking a row runs the
// same command the old button ran; nothing about the settings file, the
// dispatcher or the guarantees changed.
// -----------------------------------------------------------------------------
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.UI;
using BridgeSettings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit
{
    /// <summary>Localized ribbon text. Spanish when Revit runs in Spanish, English otherwise.</summary>
    internal static class RibbonText
    {
        /// <summary>Revit's own language, as it reports it. Never Windows, never the culture.</summary>
        internal static bool IsSpanish(object revitLanguage)
        {
            string value = revitLanguage == null ? "" : revitLanguage.ToString();
            return value.IndexOf("Spanish", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsSpanish(ExternalCommandData data)
        {
            try { return IsSpanish(data.Application.Application.Language); }
            catch { return false; }
        }

        internal static string T(bool spanish, string es, string en) => spanish ? es : en;

        // Panels ------------------------------------------------------------------
        internal static string ProductionPanel(bool es) => T(es, "Producción BIM", "BIM Production");

        // Plain words for the permission profile ids. Nobody who draws in Revit
        // knows what "safe_write" is; everybody understands "may change the model".
        internal static string ModeName(bool es, string profile)
        {
            switch ((profile ?? "").Trim().ToLowerInvariant())
            {
                case "read_only": return T(es, "solo consultar; no cambia nada", "look only; changes nothing");
                case "safe_write": return T(es, "puede modificar el modelo abierto (cada cambio se comprueba)", "may change the open model (every change is checked)");
                case "full_write": return T(es, "puede modificar, exportar y abrir o cerrar documentos", "may change, export and open or close documents");
                case "unsafe_code": return T(es, "todo lo anterior y además scripts de Python", "all of the above, plus Python scripts");
                default: return profile ?? "?";
            }
        }

        // Buttons -----------------------------------------------------------------
        internal static string StatusLabel(bool es) => T(es, "Estado de\nconexión", "Connection\nstatus");
        internal static string StatusTooltip(bool es) => T(es,
            "¿El asistente está conectado a este Revit?",
            "Is the assistant connected to this Revit?");
        internal static string StatusDescription(bool es) => T(es,
            "Te dice si el asistente de IA (Claude, Codex u otro) puede trabajar con este Revit, qué versión " +
            "de Horizun tienes instalada y dónde está el registro por si algo falla. Es lo primero que se " +
            "revisa cuando algo no funciona.",
            "Tells you whether the AI assistant (Claude, Codex or another) can work with this Revit, which " +
            "version of Horizun is installed and where the log is in case something fails. It is the first " +
            "thing to check when something does not work.");

        internal static string HubLabel(bool es) => "Horizun\nHub";
        internal static string HubTooltip(bool es) => T(es, "Abrir Horizun Hub en el navegador", "Open Horizun Hub in the browser");
        internal static string HubDescription(bool es) => T(es,
            "Horizun Hub es la web con los flujos de trabajo listos para usar: auditorías de modelo, " +
            "homologación de familias, control de calidad de entregas. Este complemento es solo la conexión " +
            "entre el asistente y Revit.",
            "Horizun Hub is the website with the ready-to-use workflows: model audits, family " +
            "standardisation, delivery quality control. This add-in is only the connection between the " +
            "assistant and Revit.");

        internal static string PythonLabel(bool es) => T(es, "Permitir\nscripts", "Allow\nscripts");
        internal static string PythonTooltip(bool es) => T(es,
            "Permitir o bloquear que el asistente ejecute scripts de Python en Revit",
            "Allow or block the assistant running Python scripts in Revit");
        internal static string PythonDescription(bool es) => T(es,
            "Normalmente el asistente solo usa comandos seguros que comprueban lo que cambian. Si activas " +
            "esto, también podrá ejecutar scripts de Python (código libre) dentro de tu Revit: sirve para " +
            "cosas que los comandos no cubren, pero nadie comprueba lo que hacen. Solo tú puedes activarlo, " +
            "desde aquí, y queda activo hasta que lo apagues.",
            "Normally the assistant only uses safe commands that check what they change. If you enable " +
            "this, it may also run Python scripts (free code) inside your Revit: useful for things the " +
            "commands do not cover, but nobody checks what they do. Only you can enable it, from here, and " +
            "it stays on until you turn it off.");

        internal static string AdvancedLabel(bool es) => T(es, "Opciones\navanzadas", "Advanced\noptions");
        internal static string AdvancedTooltip(bool es) => T(es,
            "Decide qué puede hacer el asistente en tu Revit: solo mirar, modificar, pausarlo o proteger los modelos compartidos",
            "Decide what the assistant may do in your Revit: look only, change, pause it or protect shared models");
        internal static string AdvancedDescription(bool es) => T(es,
            "Un menú con cuatro controles que solo se tocan desde este Revit: hasta dónde puede llegar el " +
            "asistente (mirar, modificar, exportar), qué ha hecho últimamente, ponerlo en pausa, y proteger " +
            "los modelos de trabajo compartido para que solo los consulte. Verás el estado actual de cada " +
            "uno antes de cambiar nada.",
            "A menu with four controls that are only touched from this Revit: how far the assistant may go " +
            "(look, change, export), what it has done lately, pausing it, and protecting shared models so it " +
            "only reads them. You see the current state of each before changing anything.");

        // Advanced options window ------------------------------------------------
        internal static string AdvancedWindowTitle(bool es) => T(es, "Horizun — Opciones avanzadas", "Horizun — Advanced options");
        internal static string AdvancedWindowSubtitle(bool es) => T(es,
            "Estos ajustes se cambian solo desde este Revit. El asistente no puede modificarlos por su cuenta.",
            "These settings change only from this Revit. The assistant cannot alter them on its own.");
        internal static string Close(bool es) => T(es, "Cerrar", "Close");

        internal static string ModeTitle(bool es) => T(es, "¿Qué puede hacer el asistente?", "What may the assistant do?");
        internal static string ModeDetail(bool es) => T(es,
            "Elige si solo puede consultar el modelo, si puede modificarlo, o si además puede exportar archivos y abrir o cerrar documentos.",
            "Choose whether it may only look at the model, change it, or also export files and open or close documents.");
        internal static string ModeState(bool es, string profile) => T(es, "Ahora: ", "Now: ") + ModeName(es, profile);

        internal static string HistoryTitle(bool es) => T(es, "¿Qué ha hecho el asistente?", "What has the assistant done?");
        internal static string HistoryDetail(bool es) => T(es,
            "La lista de sus últimas acciones: qué hizo, cuándo, si salió bien, si fue solo una simulación y cuánto tardó.",
            "The list of its latest actions: what it did, when, whether it went well, whether it was only a rehearsal, and how long it took.");
        internal static string HistoryState(bool es) => T(es, "Solo consulta; no cambia nada", "Just a look; changes nothing");

        internal static string PauseTitle(bool es) => T(es, "Pausar el asistente", "Pause the assistant");
        internal static string PauseDetail(bool es) => T(es,
            "Mientras esté en pausa, el asistente no puede hacer nada en tus modelos —ni consultar ni cambiar—; solo puede responder que está en pausa, hasta que lo reanudes aquí. Útil para trabajar sin interrupciones o si algo te parece raro.",
            "While paused, the assistant can do nothing in your models — neither read nor change — and can only answer that it is paused, until you resume it here. Useful to work undisturbed, or if something looks odd.");
        internal static string PauseState(bool es, bool paused) => paused
            ? T(es, "Ahora: EN PAUSA — clic para reanudar", "Now: PAUSED — click to resume")
            : T(es, "Ahora: activo — clic para pausar", "Now: active — click to pause");

        internal static string CentralTitle(bool es) => T(es, "Proteger los modelos compartidos", "Protect shared models");
        internal static string CentralDetail(bool es) => T(es,
            "Con esto activo, el asistente solo puede consultar los modelos de trabajo compartido (centrales, en la nube, con subproyectos): no los modifica, no los exporta y no los cierra. Las simulaciones siguen permitidas.",
            "With this on, the assistant may only look at shared models (central, cloud, with worksets): it does not change, export or close them. Rehearsals stay allowed.");
        internal static string CentralState(bool es, bool active) => active
            ? T(es, "Ahora: protegidos — clic para quitar la protección", "Now: protected — click to remove the protection")
            : T(es, "Ahora: sin protección — clic para proteger", "Now: unprotected — click to protect");
    }

    /// <summary>
    /// The one window the ribbon owns. Built in code (no XAML) so it compiles the
    /// same way on net48 and on the modern TFMs, and owned by Revit's main window
    /// so it behaves like every other modal of the host.
    /// </summary>
    internal sealed class AdvancedOptionsWindow : Window
    {
        internal const string OptionMode = "mode";
        internal const string OptionHistory = "history";
        internal const string OptionPause = "pause";
        internal const string OptionCentral = "central";

        /// <summary>The option the person clicked, or null when the window was closed.</summary>
        internal string Selected { get; private set; }

        private static readonly Brush Ink = Brush("#0F172A");
        private static readonly Brush Muted = Brush("#64748B");
        private static readonly Brush Accent = Brush("#1D4ED8");
        private static readonly Brush Header = Brush("#0B1F33");
        private static readonly Brush Ground = Brush("#F5F7FA");
        private static readonly Brush Card = Brush("#FFFFFF");
        private static readonly Brush CardHover = Brush("#EEF3FB");
        private static readonly Brush Line = Brush("#E2E8F0");

        internal AdvancedOptionsWindow(bool spanish, IntPtr owner)
        {
            Title = RibbonText.AdvancedWindowTitle(spanish);
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            Background = Ground;
            FontFamily = new FontFamily("Segoe UI");
            if (owner != IntPtr.Zero) new WindowInteropHelper(this).Owner = owner;

            var root = new StackPanel();

            // Header: the brand, the menu's name, one sentence on who may touch it.
            var header = new Border { Background = Header, Padding = new Thickness(24, 18, 24, 18) };
            var headerText = new StackPanel();
            headerText.Children.Add(new TextBlock
            {
                Text = "Horizun Hub", Foreground = Brush("#93C5FD"), FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            });
            headerText.Children.Add(new TextBlock
            {
                Text = RibbonText.T(spanish, "Opciones avanzadas", "Advanced options"),
                Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold
            });
            headerText.Children.Add(new TextBlock
            {
                Text = RibbonText.AdvancedWindowSubtitle(spanish), Foreground = Brush("#CBD5E1"), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });
            header.Child = headerText;
            root.Children.Add(header);

            // Rows: one per control, each with its CURRENT state read from the settings
            // the dispatcher itself reads, so the menu never describes a stale world.
            var rows = new StackPanel { Margin = new Thickness(20, 18, 20, 6) };
            string profile; bool paused, central;
            try { profile = BridgeSettings.PermissionProfile; } catch { profile = "?"; }
            try { paused = BridgeSettings.McpPaused; } catch { paused = false; }
            try { central = BridgeSettings.ForceReadOnlyOnWorkshared; } catch { central = false; }

            rows.Children.Add(Row(OptionMode, RibbonText.ModeTitle(spanish), RibbonText.ModeDetail(spanish), RibbonText.ModeState(spanish, profile)));
            rows.Children.Add(Row(OptionHistory, RibbonText.HistoryTitle(spanish), RibbonText.HistoryDetail(spanish), RibbonText.HistoryState(spanish)));
            rows.Children.Add(Row(OptionPause, RibbonText.PauseTitle(spanish), RibbonText.PauseDetail(spanish), RibbonText.PauseState(spanish, paused)));
            rows.Children.Add(Row(OptionCentral, RibbonText.CentralTitle(spanish), RibbonText.CentralDetail(spanish), RibbonText.CentralState(spanish, central)));
            root.Children.Add(rows);

            // Footer: a plain close. Escape does the same.
            var footer = new DockPanel { Margin = new Thickness(20, 6, 20, 18), LastChildFill = false };
            var close = FlatButton(RibbonText.Close(spanish));
            close.Click += (s, e) => { Selected = null; Close(); };
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);
            root.Children.Add(footer);

            Content = root;
            PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Selected = null; Close(); } };
        }

        private Button Row(string key, string title, string detail, string state)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // The badge echoes the ribbon icons: a round mark with the letter Horizun uses.
            var badge = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            badge.Children.Add(new Ellipse { Fill = Accent });
            badge.Children.Add(new TextBlock
            {
                Text = "M", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(badge, 0);
            grid.Children.Add(badge);

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, Foreground = Ink, FontSize = 14, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = detail, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            text.Children.Add(new TextBlock { Text = state, Foreground = Accent, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var button = new Button
            {
                Content = grid, Tag = key, Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(16, 14, 16, 14),
                Background = Card, BorderBrush = Line, Foreground = Ink
            };
            button.Template = CardTemplate();
            button.Click += (s, e) => { Selected = key; Close(); };
            return button;
        }

        private static Button FlatButton(string text)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(18, 8, 18, 8), MinWidth = 96,
                Background = Card, BorderBrush = Line, Foreground = Ink, FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            b.Template = CardTemplate();
            return b;
        }

        /// <summary>
        /// A card: rounded border, the button's own background, a lighter tint on
        /// hover. Written as a template because the host's default button chrome
        /// paints its own hover over whatever background is set.
        /// </summary>
        private static ControlTemplate CardTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "card");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, CardHover, "card"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "card"));
            template.Triggers.Add(hover);
            return template;
        }

        private static Brush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
