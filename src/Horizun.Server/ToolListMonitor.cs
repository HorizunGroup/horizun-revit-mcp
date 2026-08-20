// -----------------------------------------------------------------------------
// Announces effective tool-list changes caused by the machine-owner settings.
// In particular, the Revit ribbon's Python ON/OFF button becomes visible to MCP
// clients without restarting those that implement notifications/tools/list_changed.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal sealed class ToolListMonitor : IDisposable
    {
        private readonly Action<string, JObject> _notify;
        private readonly object _gate = new object();
        private readonly Timer _timer;
        private readonly FileSystemWatcher _watcher;
        private string _snapshot;
        private bool _disposed;

        public ToolListMonitor(Action<string, JObject> notify, bool watch = true)
        {
            _notify = notify ?? throw new ArgumentNullException(nameof(notify));
            _snapshot = Capture();
            if (!watch) return;

            string settings = Horizun.Revit.Core.Settings.Path();
            string directory = Path.GetDirectoryName(settings);
            Directory.CreateDirectory(directory);
            _timer = new Timer(_ => CheckNow(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(settings))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                               NotifyFilters.CreationTime | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Changed += Changed;
            _watcher.Created += Changed;
            _watcher.Deleted += Changed;
            _watcher.Renamed += Renamed;
        }

        private void Changed(object sender, FileSystemEventArgs e) => ScheduleSoon();
        private void Renamed(object sender, RenamedEventArgs e) => ScheduleSoon();

        private void ScheduleSoon()
        {
            lock (_gate)
            {
                if (_disposed || _timer == null) return;
                _timer.Change(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30));
            }
        }

        internal void CheckNow()
        {
            string next;
            try { next = Capture(); }
            catch (Exception ex) { Log.Warn("tool-list monitor could not read settings: " + ex.Message); return; }

            bool changed;
            lock (_gate)
            {
                if (_disposed) return;
                changed = !string.Equals(_snapshot, next, StringComparison.Ordinal);
                if (changed) _snapshot = next;
            }
            if (changed)
                _notify("notifications/tools/list_changed", null);
        }

        internal static string Capture() => string.Join("\n", Tools.List(false)
            .OfType<JObject>()
            .Select(t => (string)t["name"])
            .OrderBy(n => n, StringComparer.Ordinal));

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            try { _watcher?.Dispose(); } catch { }
            try { _timer?.Dispose(); } catch { }
        }
    }
}
