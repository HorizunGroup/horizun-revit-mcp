using System.Threading;

namespace Horizun.Revit.Core
{
    // The pipe checks this from worker threads. Posting the exit and observing
    // the next ExternalEvent callback both happen on Revit's UI thread.
    internal sealed class RevitExitPending
    {
        private int _queued;

        internal bool IsQueued => Volatile.Read(ref _queued) != 0;

        internal void MarkQueued() => Volatile.Write(ref _queued, 1);

        internal bool ResumeOnExternalEvent() => Interlocked.Exchange(ref _queued, 0) != 0;
    }
}
