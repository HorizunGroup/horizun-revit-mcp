// -----------------------------------------------------------------------------
// Horizun Revit MCP - the operations pane. Original Horizun code.
//
// G06 of the 2026-09-14 competitive inventory: a panel inside Revit for seeing
// what this bridge is doing to the model - the queue, the errors, the history,
// and a per-element diff of what changed - without leaving Revit and without
// reading a log file.
//
// WHY A PANE AND NOT A DIALOG. A dialog is modal, and a modal dialog inside Revit
// is the single thing this whole codebase works hardest to avoid: it holds the UI
// thread, and the bridge's own health probe then reports a Revit that looks hung.
// A DockablePane is passive. It never blocks, it never asks a question, and
// closing it changes nothing.
//
// IT IS A READER. Everything on it is read from records the bridge already
// writes - the durable job records and the receipt ledger - because a panel that
// computed its own version of "what happened" would be a second account of the
// truth, and the two would disagree on the day somebody needed them not to.
//
// THE ONE THING IT CAN DO is cancel work that has NOT STARTED. That limit is not
// a simplification: the Revit API offers no way to interrupt a command already
// running on its UI thread, and a button that appeared to cancel one would be a
// lie told at the worst possible moment. The pane says so on its face, next to
// the button, rather than in documentation nobody reads at that moment.
//
// NO XAML. The UI is built in code so it compiles the same way on net48 (Revit
// 2023-2024) and on net8/net10-windows (2025-2027) without a per-year resource
// pipeline. It is a list and three buttons; a markup file would buy nothing and
// cost a build dimension.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Ui
{
    /// <summary>
    /// The pane's identity. A DockablePaneId must be stable across sessions or Revit
    /// forgets where the user put it, so the GUID is a constant and never generated.
    /// </summary>
    public static class OperationsPaneIdentity
    {
        public static readonly Guid Guid = new Guid("5B5F8E2C-3C3F-4C3E-9C2B-7E5A2C9D4A11");

        public static DockablePaneId PaneId => new DockablePaneId(Guid);

        public const string Title = "Horizun · operations";
    }

    public sealed class OperationsPane : Page, IDockablePaneProvider
    {
        /// <summary>Revit's own UI language, set at start-up (App.OnStartup). Words follow it.</summary>
        public static bool Spanish { get; set; }
        private static string T(string es, string en) => Spanish ? es : en;
        private readonly CheckBox _showAll = new CheckBox();
        private readonly ListView _rows = new ListView();
        private readonly TextBlock _summary = new TextBlock();
        private readonly TextBlock _detail = new TextBlock();
        private readonly Button _cancelQueued = new Button();
        private readonly Button _select = new Button();
        private readonly TextBlock _context = new TextBlock();
        private readonly DispatcherTimer _timer;

        /// <summary>
        /// The one way a pane may touch the Revit API.
        ///
        /// A WPF click handler does not run inside a Revit API context. Calling the API
        /// from one throws, or - worse - works often enough that nobody notices until it
        /// does not. An ExternalEvent is Revit's own answer: the pane asks, and Revit
        /// raises the handler on its thread when it is ready to be interrupted.
        /// </summary>
        private readonly PaneRequestHandler _handler = new PaneRequestHandler();
        private readonly ExternalEvent _event;

        /// <summary>
        /// How many records the pane holds. A panel is for seeing what is happening now
        /// and what just happened; a full history is what the ledger on disk is for, and
        /// loading all of it would make the pane slower the longer the day went on.
        /// </summary>
        private const int MaxRows = 200;

        public OperationsPane()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Orientation = Orientation.Vertical };

            // THE FIRST QUESTION ANYBODY ASKS OF A PANEL LIKE THIS is whether the thing it
            // reports on is running at all. It goes first, above the counts.
            _context.Margin = new Thickness(8, 8, 8, 0);
            _context.TextWrapping = TextWrapping.Wrap;
            header.Children.Add(_context);

            _summary.Margin = new Thickness(8, 4, 8, 4);
            _summary.TextWrapping = TextWrapping.Wrap;
            header.Children.Add(_summary);

            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var columns = new GridView();
            // What a person needs from a row: when, whether it changed their model, and
            // WHAT it did - in words. The tool id and the raw receipt are in the detail.
            columns.Columns.Add(Column(T("Hora", "When"), "When", 70));
            columns.Columns.Add(Column(T("Resultado", "Result"), "State", 110));
            columns.Columns.Add(Column(T("Qué se hizo", "What was done"), "Tool", 420));
            columns.Columns.Add(Column(T("Documento", "Document"), "DocumentTitle", 150));
            _rows.View = columns;
            _rows.SelectionChanged += (s, e) => ShowDetail(_rows.SelectedItem as Row);
            Grid.SetRow(_rows, 1);
            root.Children.Add(_rows);

            _detail.Margin = new Thickness(8);
            _detail.TextWrapping = TextWrapping.Wrap;
            _detail.FontFamily = new FontFamily("Consolas");
            var detailScroll = new ScrollViewer
            {
                Content = _detail,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(detailScroll, 2);
            root.Children.Add(detailScroll);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };

            _showAll.Content = T("Mostrar también consultas y ensayos", "Also show reads and rehearsals");
            _showAll.Margin = new Thickness(0, 4, 12, 0);
            _showAll.IsChecked = false;
            _showAll.Click += (s, e) => Refresh();
            buttons.Children.Add(_showAll);

            var refresh = new Button { Content = T("Actualizar", "Refresh"), Padding = new Thickness(10, 3, 10, 3) };
            refresh.Click += (s, e) => Refresh();
            buttons.Children.Add(refresh);

            _cancelQueued.Content = T("Cancelar lo que no ha empezado", "Cancel what has not started");
            _cancelQueued.Margin = new Thickness(8, 0, 0, 0);
            _cancelQueued.Padding = new Thickness(10, 3, 10, 3);
            _cancelQueued.Click += (s, e) => CancelQueued();
            buttons.Children.Add(_cancelQueued);

            _select.Content = T("Seleccionar en Revit", "Select in Revit");
            _select.Margin = new Thickness(8, 0, 0, 0);
            _select.Padding = new Thickness(10, 3, 10, 3);
            _select.IsEnabled = false;
            _select.Click += (s, e) => SelectRowElements();
            buttons.Children.Add(_select);

            var caveat = new TextBlock
            {
                Margin = new Thickness(10, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                // The sentence that keeps the button honest, on the button's own row.
                Text = T("Lo que ya se está ejecutando dentro de Revit no se puede interrumpir, ni siquiera desde Revit.",
                         "Work already running inside Revit cannot be interrupted by anyone, including Revit.")
            };
            buttons.Children.Add(caveat);

            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;

            // The ExternalEvent has to be created on Revit's UI thread, which is where a
            // DockablePane is constructed during start-up. A failure here disables the
            // buttons that need it and leaves the rest of the pane working: a panel that
            // cannot navigate is worth more than no panel.
            try { _event = ExternalEvent.Create(_handler); }
            catch (Exception ex)
            {
                _event = null;
                Log.Warn("the operations pane cannot reach Revit's thread: " + ex.Message +
                         " Its history still works; selecting and reading the document do not.");
            }

            // Two seconds: fast enough that a queued job appears while somebody is
            // looking at the pane, slow enough that it never competes with Revit for the
            // UI thread. The timer reads files; it never touches the Revit API, which is
            // why it is safe to run on a tick at all.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();

            Refresh();
        }

        /// <summary>
        /// Ask Revit to select what the chosen row names.
        ///
        /// ASKS. The ids go to the handler and the handler runs when Revit raises it; this
        /// method returns immediately and changes nothing. If the ids name elements that
        /// no longer exist, the handler says so rather than selecting the subset silently -
        /// a selection of four when the row named six is a different answer.
        /// </summary>
        private void SelectRowElements()
        {
            var row = _rows.SelectedItem as Row;
            if (row == null) return;

            List<long> ids = ElementIdsOf(row.Raw);
            if (ids.Count == 0 && LinkedPairsOf(row.Raw).Count == 0)
            {
                _detail.Text = "This row names no element ids, so there is nothing to select. A receipt " +
                               "records what a call changed; a call that changed nothing has none.";
                return;
            }
            if (_event == null)
            {
                _detail.Text = "This pane could not create its Revit event at start-up, so it cannot ask " +
                               "Revit to select anything. The ids are: " +
                               string.Join(", ", ids.Take(50).Select(i => i.ToString(CultureInfo.InvariantCulture)));
                return;
            }

            _handler.Request(ids, LinkedPairsOf(row.Raw),
                             message => Dispatcher.Invoke(() => _detail.Text = message));
            _event.Raise();
            _detail.Text = "Asked Revit to select " + ids.Count + " element(s). Revit runs this when it is " +
                           "ready to be interrupted, so it may take a moment - and it will say here if any " +
                           "of them no longer exist.";
        }

        /// <summary>
        /// Every element id a record names, wherever the writer happened to put it.
        ///
        /// SEVERAL SHAPES, because the records were written by different commands over two
        /// years and normalising them now would mean rewriting history that is already on
        /// disk. A reader that understands one shape shows a Select button that does
        /// nothing for most rows.
        /// </summary>
        internal static List<long> ElementIdsOf(JObject record)
        {
            var ids = new List<long>();
            if (record == null) return ids;

            foreach (string key in new[] { "element_ids", "created_ids", "written_ids", "ids" })
            {
                JArray array = record[key] as JArray;
                if (array == null) continue;
                foreach (JToken token in array)
                    if (token.Type == JTokenType.Integer) ids.Add((long)token);
            }

            foreach (string key in new[] { "elements", "rows", "created" })
            {
                JArray array = record[key] as JArray;
                if (array == null) continue;
                foreach (JObject entry in array.OfType<JObject>())
                    foreach (string field in new[] { "element_id", "id", "revit_element_id", "created_id" })
                    {
                        JToken token = entry[field];
                        if (token != null && token.Type == JTokenType.Integer) ids.Add((long)token);
                    }
            }

            return ids.Where(i => i > 0).Distinct().Take(1000).ToList();
        }

        /// <summary>
        /// Elements a record places INSIDE A LINK, as (link instance, element) pairs.
        ///
        /// A pair is the smallest thing that names one physical element in a federated
        /// model: two instances of the same linked file hold the same element ids and stand
        /// in different places, so an id alone names a shape rather than a thing. A record
        /// that carries only the element id is NOT turned into a pair by picking an
        /// instance - the handler reports where those ids live instead.
        /// </summary>
        internal static List<Tuple<long, long>> LinkedPairsOf(JObject record)
        {
            var pairs = new List<Tuple<long, long>>();
            if (record == null) return pairs;

            foreach (string key in new[] { "linked_elements", "link_elements", "federated" })
                foreach (JObject entry in (record[key] as JArray ?? new JArray()).OfType<JObject>())
                {
                    long instance = entry.Value<long?>("link_instance_id") ?? -1;
                    long element = entry.Value<long?>("element_id") ?? entry.Value<long?>("id") ?? -1;
                    if (instance > 0 && element > 0) pairs.Add(Tuple.Create(instance, element));
                }

            // Rows that carry the instance beside each element, which is how the clash and
            // federated readers write them.
            foreach (string key in new[] { "elements", "rows", "clashes" })
                foreach (JObject entry in (record[key] as JArray ?? new JArray()).OfType<JObject>())
                {
                    long instance = entry.Value<long?>("link_instance_id") ?? -1;
                    long element = entry.Value<long?>("element_id") ?? -1;
                    if (instance > 0 && element > 0) pairs.Add(Tuple.Create(instance, element));
                }

            return pairs.Distinct().Take(1000).ToList();
        }

        private static GridViewColumn Column(string header, string path, double width) => new GridViewColumn
        {
            Header = header,
            Width = width,
            DisplayMemberBinding = new System.Windows.Data.Binding(path)
        };

        // =====================================================================
        // IDockablePaneProvider
        // =====================================================================

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }

        // =====================================================================
        // Reading what the bridge already wrote
        // =====================================================================

        private void Refresh()
        {
            try
            {
                List<Row> all = Read();
                bool showAll = _showAll.IsChecked == true;
                List<Row> rows = showAll ? all : all.Where(r => r.Prominent).ToList();
                object selected = _rows.SelectedItem;
                _rows.ItemsSource = rows;
                if (selected is Row previous)
                {
                    Row again = rows.FirstOrDefault(r => r.Id == previous.Id);
                    if (again != null) _rows.SelectedItem = again;
                }

                int queued = 0;
                try { queued = AsyncQueue.Count; } catch { queued = -1; }

                int failed = all.Count(r => r.Kind == OperationDescription.Failed);
                int changed = all.Count(r => r.Kind == OperationDescription.Changed);
                int hidden = all.Count - rows.Count;
                _summary.Text =
                    T("En cola: ", "Queued: ") + (queued < 0 ? T("desconocido", "unknown") : queued.ToString(CultureInfo.InvariantCulture)) +
                    "   ·   " + T("cambiaron el modelo: ", "changed the model: ") + changed +
                    "   ·   " + T("fallaron: ", "failed: ") + failed +
                    (hidden > 0 ? "   ·   " + hidden + T(" consultas/ensayos ocultos", " reads/rehearsals hidden") : "") +
                    (all.Count >= MaxRows ? "   ·   " + T("(solo los últimos ", "(only the most recent ") + MaxRows + ")" : "");

                _cancelQueued.IsEnabled = queued > 0;
                _select.IsEnabled = _event != null && _rows.SelectedItem is Row selectedRow &&
                                    (ElementIdsOf(selectedRow.Raw).Count > 0 ||
                                     LinkedPairsOf(selectedRow.Raw).Count > 0);
                ShowContext();
            }
            catch (Exception ex)
            {
                // A pane that throws takes Revit's UI with it. It reports and carries on.
                _summary.Text = "The operations pane could not read the bridge's records: " + ex.Message;
            }
        }

        /// <summary>
        /// Which document, which Revit, which build - and whether the bridge is listening.
        ///
        /// THE LAST ONE IS WHY THIS EXISTS. The pane reads files, and files outlive the
        /// process that wrote them: a bridge that had stopped answering an hour ago looked
        /// exactly like one that was idle, and the history underneath it looked current.
        ///
        /// The document title comes from the handler, which reads it on Revit's thread and
        /// leaves it here. It is stamped with when it was read, because a title from four
        /// minutes ago presented as the current document is worse than no title.
        /// </summary>
        private void ShowContext()
        {
            bool listening;
            try { listening = Transport.PipeServer.IsListening; } catch { listening = false; }

            string document = _handler.LastDocumentTitle;
            string when = _handler.LastDocumentReadUtc == DateTime.MinValue
                ? null
                : _handler.LastDocumentReadUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            // Ask for a fresh reading, on Revit's thread, whenever the pane refreshes. It
            // costs nothing when Revit is busy: the event simply does not fire yet.
            if (_event != null) { _handler.RequestDocument(); _event.Raise(); }

            _context.Text =
                (listening
                    ? "Bridge: LISTENING"
                    : "Bridge: NOT LISTENING - nothing below is current. The add-in is loaded and its pipe " +
                      "is not accepting connections, so a client cannot reach this Revit.") +
                "   ·   Document: " + (document ?? "not read yet") +
                (when == null ? "" : " (read " + when + ")") +
                "   ·   Revit " + (_handler.LastRevitVersion ?? "unknown") +
                "   ·   bridge " + SafeVersion();
            _context.Foreground = listening
                ? SystemColors.ControlTextBrush
                : new SolidColorBrush(Color.FromRgb(176, 0, 32));
        }

        private static string SafeVersion()
        {
            try { return Build.Version ?? "unknown"; } catch { return "unknown"; }
        }

        /// <summary>
        /// The rows, newest first, from the durable job records and the receipt ledger.
        ///
        /// BOTH, because they answer different questions: a job record is work the bridge
        /// accepted and its state, and a receipt is what a completed call actually
        /// changed. A pane showing only one of them would be missing either everything
        /// still running or everything that finished.
        /// </summary>
        private static List<Row> Read()
        {
            var rows = new List<Row>();
            rows.AddRange(JobRows());
            rows.AddRange(ReceiptRows());
            return rows
                .OrderByDescending(r => r.WhenUtc)
                .Take(MaxRows)
                .ToList();
        }

        private static IEnumerable<Row> JobRows()
        {
            string directory;
            try { directory = Job.Dir(); } catch { yield break; }
            if (!Directory.Exists(directory)) yield break;

            FileInfo[] files;
            try { files = new DirectoryInfo(directory).GetFiles("*.json"); }
            catch { yield break; }

            foreach (FileInfo file in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxRows))
            {
                JObject record = ReadJson(file.FullName);
                if (record == null) continue;
                yield return new Row
                {
                    Id = "job:" + file.Name,
                    WhenUtc = file.LastWriteTimeUtc,
                    When = file.LastWriteTimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    State = JobState(record.Value<string>("state")),
                    Kind = JobKind(record.Value<string>("state")),
                    Prominent = true,
                    Tool = OperationDescription.ToolPhrase(record.Value<string>("tool"), Spanish) + T(" (trabajo en segundo plano)", " (background job)"),
                    Changed = "-",
                    DocumentTitle = record.Value<string>("document_title") ?? "",
                    Raw = record
                };
            }
        }

        private static IEnumerable<Row> ReceiptRows()
        {
            string directory;
            try { directory = ReceiptLedger.DefaultDirectory(); } catch { yield break; }
            if (!Directory.Exists(directory)) yield break;

            FileInfo[] files;
            try { files = new DirectoryInfo(directory).GetFiles("*.jsonl"); }
            catch { yield break; }

            foreach (FileInfo file in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(4))
            {
                foreach (string line in TailLines(file.FullName, MaxRows))
                {
                    JObject receipt;
                    try { receipt = JObject.Parse(line); } catch { continue; }

                    DateTime when = file.LastWriteTimeUtc;
                    string stamp = receipt.Value<string>("utc") ?? receipt.Value<string>("timestamp_utc");
                    DateTime parsed;
                    if (!string.IsNullOrWhiteSpace(stamp) &&
                        DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                        when = parsed;

                    // The ledger writes outcome=ok|failed. Reading a field it never wrote made
                    // every row "failed" (field report, 2026-09-25).
                    string kind = OperationDescription.Kind(receipt);
                    yield return new Row
                    {
                        Id = "receipt:" + file.Name + ":" + line.GetHashCode().ToString(CultureInfo.InvariantCulture),
                        WhenUtc = when,
                        When = when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                        State = OperationDescription.KindLabel(kind, Spanish),
                        Kind = kind,
                        Prominent = OperationDescription.ShownByDefault(receipt),
                        Tool = OperationDescription.Sentence(receipt, Spanish),
                        Changed = ChangedCount(receipt),
                        DocumentTitle = DocumentOf(receipt),
                        Raw = receipt
                    };
                }
            }
        }

        private static string DocumentOf(JObject receipt)
        {
            string d = receipt.Value<string>("document") ?? receipt.Value<string>("document_title") ?? "";
            int bracket = d.IndexOf(" [", StringComparison.Ordinal);
            return bracket > 0 ? d.Substring(0, bracket) : d;
        }

        private static string JobState(string state)
        {
            switch ((state ?? "").ToLowerInvariant())
            {
                case "queued": return T("En cola", "Queued");
                case "running": return T("Ejecutándose", "Running");
                case "completed": case "succeeded": return T("Terminado", "Done");
                case "failed": return T("Falló", "Failed");
                case "cancelled": case "canceled": return T("Cancelado", "Cancelled");
                default: return state ?? T("desconocido", "unknown");
            }
        }

        private static string JobKind(string state) =>
            string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase) ? OperationDescription.Failed : "job";

        private static string ChangedCount(JObject receipt)
        {
            foreach (string key in new[] { "changed", "applied", "written", "verified" })
            {
                JToken token = receipt[key];
                if (token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
                    return token.ToString();
            }
            JArray elements = receipt["elements"] as JArray ?? receipt["rows"] as JArray;
            return elements == null ? "-" : elements.Count.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The per-element diff, shown as the record itself.
        ///
        /// NOT a prettier re-derivation of it: the receipt already carries what the tool
        /// verified after its commit, and re-formatting it into a summary is where a
        /// panel starts telling a different story from the ledger it read.
        /// </summary>
        private void ShowDetail(Row row)
        {
            if (row == null) { _detail.Text = ""; return; }
            try
            {
                string attention = row.Raw.Value<string>("attention");
                string tool = row.Raw.Value<string>("tool");
                _detail.Text = row.State + " - " + row.Tool + Environment.NewLine +
                               (attention == null ? "" : "(!) " + attention + Environment.NewLine) +
                               T("Herramienta: ", "Tool: ") + tool + Environment.NewLine + Environment.NewLine +
                               row.Raw.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception ex) { _detail.Text = "This record could not be rendered: " + ex.Message; }
        }

        /// <summary>
        /// Remove everything still WAITING. Nothing that has started is touched, and the
        /// message says exactly how many were removed - not "cancelled", which would
        /// imply something about the one that is running.
        /// </summary>
        private void CancelQueued()
        {
            int removed;
            // Through the pump, which FINISHES each removed job's record as not_started -
            // draining the queue alone left horizun_job_status saying "queued" forever
            // (review 2026-09-26). Only background jobs wait here; a synchronous call in
            // the FIFO is not in this queue and is not touched.
            try { removed = AsyncPump.FailEverythingWaiting("Cancelled from the operations pane before it started. It NEVER RAN."); }
            catch (Exception ex)
            {
                _summary.Text = "The queue could not be drained: " + ex.Message;
                return;
            }

            _summary.Text = removed == 0
                ? "Nothing was waiting. Anything running now cannot be interrupted."
                : removed + " background job(s) were cancelled before they started (their status says not_started). Anything already running " +
                  "continues: Revit offers no way to interrupt a command on its UI thread.";
            Refresh();
        }

        // =====================================================================
        // Small readers
        // =====================================================================

        private static JObject ReadJson(string path)
        {
            try { return JObject.Parse(File.ReadAllText(path)); }
            catch { return null; }
        }

        /// <summary>The last N lines of a file, without loading a ledger that has been appended to all day.</summary>
        private static IEnumerable<string> TailLines(string path, int count)
        {
            var kept = new LinkedList<string>();
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        kept.AddLast(line);
                        if (kept.Count > count) kept.RemoveFirst();
                    }
                }
            }
            catch { return new string[0]; }
            return kept.Reverse();
        }

        /// <summary>One line on the pane. Public properties because WPF binds to them.</summary>
        public sealed class Row
        {
            public string Id { get; set; }
            public DateTime WhenUtc { get; set; }
            public string When { get; set; }
            public string State { get; set; }
            public string Kind { get; set; }
            public bool Prominent { get; set; }
            public string Tool { get; set; }
            public string Changed { get; set; }
            public string DocumentTitle { get; set; }
            public JObject Raw { get; set; }
        }
    }

    /// <summary>
    /// The ribbon button that shows the pane.
    ///
    /// Showing a DockablePane is the ONLY thing it does, and it is wrapped: a pane that
    /// failed to register at start-up must produce a sentence, never an exception dialog
    /// in the middle of somebody's model.
    /// </summary>
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public sealed class ShowOperationsPaneCommand : Autodesk.Revit.UI.IExternalCommand
    {
        public Autodesk.Revit.UI.Result Execute(ExternalCommandData commandData, ref string message,
                                                Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                DockablePane pane = commandData.Application.GetDockablePane(OperationsPaneIdentity.PaneId);
                pane.Show();
                return Autodesk.Revit.UI.Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "The Horizun operations pane is not available in this session: " + ex.Message +
                          " The bridge itself is unaffected; nothing about a pane stops a command from running.";
                return Autodesk.Revit.UI.Result.Failed;
            }
        }
    }
}
