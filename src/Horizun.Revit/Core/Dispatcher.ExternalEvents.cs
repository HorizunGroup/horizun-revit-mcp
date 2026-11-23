using System.Threading;
using Autodesk.Revit.UI;

namespace Horizun.Revit.Core
{
    public sealed partial class Dispatcher
    {
        private ExternalEvent _event;

        private volatile bool _shuttingDown;
        private static readonly RevitExitPending ExitPending = new RevitExitPending();
        private static Dispatcher _instance;
        internal static void MarkExiting()
        {
            ExitPending.MarkQueued();
            _instance?._gate.FailQueued("Revit is exiting");
            AsyncPump.FailEverythingWaiting("Revit is exiting", Log.Warn);
        }

        private int _backgroundRaiseScheduled;

        /// <summary>
        /// The ExternalEvent behind IWorkRaiser, which is the ONLY Revit-dependent part
        /// of deciding what a refused raise means.
        ///
        /// Denied needs a Revit that is shutting down, so that path was "reasoned and
        /// compiled" and could not be exercised. Everything on the other side of this
        /// interface is now an ordinary test case.
        /// </summary>
        private sealed class ExternalEventRaiser : IWorkRaiser
        {
            private readonly ExternalEvent _ev;
            public ExternalEventRaiser(ExternalEvent ev) { _ev = ev; }

            public RaiseOutcome Raise()
            {
                if (_ev == null) return RaiseOutcome.Unknown;
                return Map(_ev.Raise());
            }
        }

        /// <summary>
        /// Revit's answer, in our terms. Anything unrecognised maps to Unknown and is
        /// treated exactly like Denied - an answer this code does not understand is not
        /// evidence that a callback is coming.
        /// </summary>
        internal static RaiseOutcome Map(ExternalEventRequest r)
        {
            switch (r)
            {
                case ExternalEventRequest.Accepted: return RaiseOutcome.Accepted;
                case ExternalEventRequest.Pending: return RaiseOutcome.Pending;
                case ExternalEventRequest.Denied: return RaiseOutcome.Denied;
                default: return RaiseOutcome.Unknown;
            }
        }

        private IWorkRaiser Raiser() => new ExternalEventRaiser(_event);

        private RaiseAttemptResult RaiseWithBoundedRetry(string context)
        {
            return RaiseRetryPolicy.TrySchedule(
                Raiser(),
                milliseconds => Thread.Sleep(milliseconds),
                warn: message => Log.Warn(context + ": " + message));
        }

        /// <summary>Create the ExternalEvent. Call once, from OnStartup, on the UI thread.</summary>
        public void Initialize()
        {
            _instance = this;
            _event = ExternalEvent.Create(this);
        }

        /// <summary>
        /// Called from the transport (background) thread. Blocks until the command has
        /// run on the UI thread and returns its result. A timeout means Revit is busy
        /// or stuck in a modal — we say so rather than hang the pipe forever, and the
        /// abandoned request is marked so the next caller learns why the thread is not
        /// free instead of meeting an unexplained refusal.
        /// </summary>
        public CommandResult Invoke(string name, string paramsJson, int timeoutMs)
            => Invoke(null, name, paramsJson, timeoutMs);

        public CommandResult Invoke(string wireId, string name, string paramsJson, int timeoutMs)
        {
            if (ExitPending.IsQueued)
            {
                // If Revit temporarily denied the recovery raise, a new call
                // can ask for that callback again without entering either queue.
                PumpNext();
                return CommandResult.RefuseBeforeWrite("Revit is exiting");
            }
            return InvokeWork(wireId, name, paramsJson, timeoutMs);
        }

        /// <summary>The ExternalEvent callback — runs on Revit's UI thread.</summary>
        public void Execute(UIApplication app)
        {
            using var probe = CloseProbe.Enter("Dispatcher.Execute");
            // Revit serviced another ExternalEvent after we returned from the
            // one that posted ExitRevit. Use this turn solely to reopen admission.
            if (ExitPending.ResumeOnExternalEvent())
            {
                Log.Info("Revit serviced an ExternalEvent after an exit was queued; accepting new requests.");
                PumpNext();
                return;
            }
            try { ExecuteWork(app); }
            finally
            {
                // Exit may have drained both queues. Give the host another turn
                // anyway so it can reopen admission if it has stayed up.
                if (ExitPending.IsQueued) PumpNext();
            }
        }

        /// <summary>
        /// Schedule one more callback for queued work or to observe a posted exit. This method is called
        /// while Revit is still unwinding the current ExternalEvent callback, precisely
        /// the window in which Revit 2026 was measured returning a transient Denied.
        /// Raising is therefore deferred to a background worker; that worker exhausts
        /// the bounded retry policy before a refusal is allowed to drain the queues.
        /// </summary>
        private void PumpNext()
        {
            if (!NeedsCallback()) return;
            if (Interlocked.Exchange(ref _backgroundRaiseScheduled, 1) != 0) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Let the callback that called PumpNext return before the first raise.
                Thread.Sleep(5);
                Interlocked.Exchange(ref _backgroundRaiseScheduled, 0);
                if (_shuttingDown || !NeedsCallback()) return;

                RaiseAttemptResult raise = RaiseWithBoundedRetry("queued-work ExternalEvent raise");
                if (raise.Scheduled || _shuttingDown) return;

                string reason = "Revit refused to schedule queued work: Raise() returned " + raise.Outcome +
                                " after " + raise.Attempts + " bounded attempt(s). It NEVER STARTED; nothing " +
                                "was executed or written. Revit is closing or the ExternalEvent is no longer available.";
                int sync = _gate.FailQueued(reason);
                int async = AsyncPump.FailEverythingWaiting(reason, Log.Warn);
                Log.Warn("queued work abandoned after Raise()=" + raise.Outcome + ": " + sync +
                         " synchronous request(s), " + async + " async job(s)");
            });
        }

        private bool NeedsCallback() => ExitPending.IsQueued || _gate.HasPending || AsyncQueue.Count > 0;

        public int Shutdown()
        {
            _shuttingDown = true;
            string reason = "Revit shut down before this queued command started. It NEVER RAN: nothing was " +
                            "executed and nothing was written.";
            return _gate.FailQueued(reason);
        }

        public string GetName() => "Horizun.Dispatcher";
    }
}
