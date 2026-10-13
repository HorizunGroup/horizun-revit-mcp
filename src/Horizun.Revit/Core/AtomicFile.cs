// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WRITE A WHOLE FILE OR NOTHING.
//
// Audit of 2026-10-08: several stores wrote their JSON with File.WriteAllText
// straight over the live file (a crash mid-write leaves half a file, which the
// next Load reads as "empty" and the next Save makes permanent), and two used a
// FIXED temporary name, so two writers raced on the same "<file>.tmp" and one of
// them failed - or swapped in the other's bytes.
//
// Here: the bytes go to a temporary file with a name of its own, are flushed to
// disk, and only then take the target's place - File.Replace when there is a
// target, File.Move when there is not. A failure deletes the temporary file and
// throws; the target is either the old file or the new one, never a mixture.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;

namespace Horizun.Revit.Core
{
    public static class AtomicFile
    {
        /// <summary>UTF-8 without a BOM, atomically.</summary>
        public static void WriteAllText(string path, string text) =>
            WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text ?? ""));

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required.", nameof(path));
            string temporary = TemporaryFor(path);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes ?? new byte[0], 0, bytes?.Length ?? 0);
                    stream.Flush(true);
                }
                Swap(temporary, path);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                throw;
            }
        }

        /// <summary>A temporary name next to the target that no other writer can share.</summary>
        public static string TemporaryFor(string path) => path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        /// <summary>
        /// Put a fully written temporary file in the target's place. Another writer
        /// swapping the same target makes it vanish for an instant (Replace renames
        /// it aside) or holds it open; both are waited out for a bounded time, never
        /// turned into a half-written file.
        /// </summary>
        public static void Swap(string temporary, string path)
        {
            DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            int wait = 2;
            while (true)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) &&
                                           File.Exists(temporary) && DateTime.UtcNow < until)
                {
                    System.Threading.Thread.Sleep(wait);
                    wait = Math.Min(wait * 2, 100);
                }
            }
        }
    }
}
