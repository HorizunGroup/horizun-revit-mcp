using System;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.UI
{
    public interface IExternalEventHandler
    {
        void Execute(UIApplication app);
        string GetName();
    }

    public enum ExternalEventRequest { Accepted, Pending, Denied }

    public sealed class ExternalEvent
    {
        private readonly IExternalEventHandler handler;
        private readonly SemaphoreSlim requested = new SemaphoreSlim(0);
        private int denyNext;

        private ExternalEvent(IExternalEventHandler handler) { this.handler = handler; }
        public static ExternalEvent Create(IExternalEventHandler handler) => new ExternalEvent(handler);

        public ExternalEventRequest Raise()
        {
            if (Interlocked.Exchange(ref denyNext, 0) != 0) return ExternalEventRequest.Denied;
            requested.Release();
            return ExternalEventRequest.Accepted;
        }

        internal void DenyNextRaise() => Volatile.Write(ref denyNext, 1);

        internal bool ServiceNext(UIApplication app, TimeSpan timeout)
        {
            if (!requested.Wait(timeout)) return false;
            handler.Execute(app);
            return true;
        }
    }
}

namespace Horizun.Revit.Core
{
    public sealed partial class Dispatcher : IExternalEventHandler
    {
        internal static void ClearFixtureExitPending() => ExitPending.ResumeOnExternalEvent();
        private readonly RequestGate _gate = new RequestGate();
        internal ExternalEvent HostEvent => _event;
        internal Action HostWork;
        internal int WorkCalls { get; private set; }

        private void ExecuteWork(UIApplication app)
        {
            WorkCalls++;
            HostWork();
        }
        private CommandResult InvokeWork(string wireId, string name, string paramsJson, int timeoutMs) =>
            CommandResult.Ok(new JObject { ["admitted"] = true });
    }
}
