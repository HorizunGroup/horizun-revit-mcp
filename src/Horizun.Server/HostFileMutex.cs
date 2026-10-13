// -----------------------------------------------------------------------------
// Horizun MCP - a lock on one record file that holds ACROSS PROCESSES.
// Original Horizun code.
//
// Claude's and Codex's MCP servers are separate processes reading and writing the
// same files under the Horizun data root. A `lock` or a Monitor stops two calls in
// ONE server; it does nothing about the other server, which loads the same record,
// sees the same pending step and dispatches it again. The repair memory learned
// this first (RepairMemory.MutexName); this is the same idea for every other record
// that is loaded, changed and saved: a named Local\ mutex derived from the file's
// full path, so every process that touches the file takes the same one.
//
// A named mutex is RE-ENTRANT on the thread that holds it, so it never replaces the
// in-process lock - it sits beside it. And it has THREAD AFFINITY: it is released
// by the thread that took it, which is why it is used with `using` around work that
// runs synchronously on that thread.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Horizun.Server
{
    internal sealed class HostFileMutex : IDisposable
    {
        private Mutex _mutex;

        private HostFileMutex(Mutex mutex) { _mutex = mutex; }

        /// <summary>
        /// The mutex name for one file (or one directory that is changed as a unit).
        /// Case-insensitive like NTFS: "Run-A" and "run-a" are one file, so one mutex.
        /// </summary>
        internal static string NameFor(string scope, string path)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path ?? ""); } catch { full = path ?? ""; }
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
                var sb = new StringBuilder("Local\\Horizun" + scope + "-");
                for (int i = 0; i < 10; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Take the mutex for <paramref name="path"/>, waiting at most <paramref name="wait"/>.
        /// Returns null when another holder kept it - the caller REFUSES then, naming the
        /// path, and does nothing. A holder that died without releasing leaves an abandoned
        /// mutex, which is taken: the records are written atomically, so the file is whole.
        /// </summary>
        internal static HostFileMutex TryAcquire(string scope, string path, TimeSpan wait)
        {
            var mutex = new Mutex(false, NameFor(scope, path));
            bool held;
            try { held = mutex.WaitOne(wait); }
            catch (AbandonedMutexException) { held = true; }
            if (held) return new HostFileMutex(mutex);
            mutex.Dispose();
            return null;
        }

        public void Dispose()
        {
            Mutex mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex == null) return;
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}
