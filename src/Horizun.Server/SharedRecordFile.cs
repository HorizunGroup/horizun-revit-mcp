// -----------------------------------------------------------------------------
// Horizun MCP - a durable record another process may be reading. Original Horizun code.
//
// MEASURED (campaign 5, 2026-09-18): a client polling a procedure-run record every
// 50 ms made the executor's own advance fail with "The process cannot access the
// file because it is being used by another process" - File.Replace cannot swap a
// file somebody holds open, and File.ReadAllText cannot read it mid-swap. The run
// was not corrupted (the swap is all-or-nothing), but a step failed for a reason
// that has nothing to do with the model. Readers are legitimate - a person's editor,
// an indexer, an antivirus, a harness watching progress - so the record tolerates
// them for a bounded time and then fails as before, naming the file.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Horizun.Server
{
    public static class SharedRecordFile
    {
        /// <summary>How long a sharing violation is waited out before it is reported.</summary>
        public static TimeSpan Patience = TimeSpan.FromSeconds(3);

        // Windows names a sharing violation 32 or 33. MEASURED on linux-x64: the same refusal,
        // with the same message, arrives as HResult 0x0000000B - EWOULDBLOCK - because FileShare
        // there is an advisory lock. Off Windows the retry never engaged without it. The code is
        // ERROR_INVALID_FORMAT on Windows, so it is honoured only where it means what it means.
        private const int SharingViolation = 32, LockViolation = 33, WouldBlock = 11;

        public static string ReadAllText(string path) => Retry(() => File.ReadAllText(path));

        /// <summary>
        /// Write through a temporary file and swap it in, so a reader never sees half a
        /// record. The temporary name is unique per write: a fixed "&lt;file&gt;.tmp" was
        /// shared by two writers of one record, and one failed or swapped in the other's
        /// bytes (audit 2026-10-08).
        /// </summary>
        public static void WriteAtomically(string target, string text)
        {
            string temporary = Horizun.Revit.Core.AtomicFile.TemporaryFor(target);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text ?? "");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            try { Swap(temporary, target); }
            catch { try { File.Delete(temporary); } catch { } throw; }
        }

        /// <summary>
        /// Swap a fully written temporary file into place, waiting out a reader of the
        /// target - and another writer's swap, which makes the target vanish for an
        /// instant (File.Replace renames it aside) and used to fail this one with
        /// FileNotFoundException.
        /// </summary>
        public static void Swap(string temporary, string target)
        {
            DateTime until = DateTime.UtcNow + Patience;
            int wait = 10;
            while (true)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) &&
                                           File.Exists(temporary) && DateTime.UtcNow < until)
                {
                    Thread.Sleep(wait);
                    wait = Math.Min(wait * 2, 200);
                }
            }
        }

        private static T Retry<T>(Func<T> io)
        {
            DateTime until = DateTime.UtcNow + Patience;
            int wait = 10;
            while (true)
            {
                try { return io(); }
                catch (IOException ex) when (IsSharing(ex) && DateTime.UtcNow < until)
                {
                    Thread.Sleep(wait);
                    wait = Math.Min(wait * 2, 200);
                }
                catch (UnauthorizedAccessException) when (DateTime.UtcNow < until)
                {
                    // File.Replace reports a target held open without FILE_SHARE_DELETE this way
                    Thread.Sleep(wait);
                    wait = Math.Min(wait * 2, 200);
                }
            }
        }

        private static bool IsSharing(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            if (code == SharingViolation || code == LockViolation) return true;
            return code == WouldBlock && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
    }
}
