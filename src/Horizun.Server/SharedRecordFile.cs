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
using System.Text;
using System.Threading;

namespace Horizun.Server
{
    public static class SharedRecordFile
    {
        /// <summary>How long a sharing violation is waited out before it is reported.</summary>
        public static TimeSpan Patience = TimeSpan.FromSeconds(3);

        private const int SharingViolation = 32, LockViolation = 33;

        public static string ReadAllText(string path) => Retry(() => File.ReadAllText(path));

        /// <summary>Write through a temporary file and swap it in, so a reader never sees half a record.</summary>
        public static void WriteAtomically(string target, string text)
        {
            string temporary = target + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            try { Swap(temporary, target); }
            catch { try { File.Delete(temporary); } catch { } throw; }
        }

        /// <summary>Swap a fully written temporary file into place, waiting out a reader of the target.</summary>
        public static void Swap(string temporary, string target) => Retry(() =>
        {
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
            return true;
        });

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
            return code == SharingViolation || code == LockViolation;
        }
    }
}
