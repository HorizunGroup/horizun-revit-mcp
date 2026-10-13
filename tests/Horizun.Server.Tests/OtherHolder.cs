// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// A SECOND HOLDER of a named mutex, standing in for another server process.
// Named mutexes are re-entrant on the thread that owns them, so a lock taken on the
// test's own thread would let the code under test straight through and prove
// nothing. This takes the mutex through a SEPARATE Mutex instance on a SEPARATE
// thread and keeps it until disposed - which is exactly what Claude's server looks
// like to Codex's.
// -----------------------------------------------------------------------------
using System;
using System.Threading;

namespace Horizun.Server.Tests
{
    internal sealed class OtherHolder : IDisposable
    {
        private readonly ManualResetEventSlim _held = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private readonly Thread _thread;

        internal OtherHolder(string mutexName)
        {
            _thread = new Thread(() =>
            {
                using (var mutex = new Mutex(false, mutexName))
                {
                    mutex.WaitOne();
                    _held.Set();
                    _release.Wait();
                    mutex.ReleaseMutex();
                }
            }) { IsBackground = true };
            _thread.Start();
            if (!_held.Wait(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("the second holder never took " + mutexName);
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(10));
            _held.Dispose();
            _release.Dispose();
        }
    }
}
