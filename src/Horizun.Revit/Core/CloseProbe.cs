using System;
using System.Threading;

namespace Horizun.Revit.Core
{
    // Best effort only: never wait for disk or a logging lock on Revit's UI thread.
    public static class CloseProbe
    {
        public static IDisposable Enter(string name) => Enter(name, Record);
        public static IDisposable Enter(string name, Action<string, string> record)
        {
            Mark(record, name, "enter");
            return new Scope(name, record);
        }
        private static void Mark(Action<string, string> record, string name, string phase)
        {
            try { record(name, phase); } catch { }
        }
        public static void Record(string name, string phase)
        {
            try
            {
                string line = "close-probe " + name + " " + phase + " at=" + DateTime.UtcNow.ToString("O") + " thread=" + Thread.CurrentThread.ManagedThreadId;
                ThreadPool.QueueUserWorkItem(_ => { try { Log.Info(line); } catch { } });
            }
            catch { }
        }
        private sealed class Scope : IDisposable
        {
            private readonly string _name;
            private readonly Action<string, string> _record;
            internal Scope(string name, Action<string, string> record) { _name = name; _record = record; }
            public void Dispose() { Mark(_record, _name, "exit"); }
        }
    }
}
