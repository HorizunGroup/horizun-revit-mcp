// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// What a file on disk READS AS, before and after a save - the evidence a
// "saved" claim stands on (story 5.12).
//
// This existed as a private class inside family_apply, and it opened the file
// with File.OpenRead(). OpenRead() requests FileShare.Read - "nobody else may
// hold write access" - and the document a save command is pointed at is EXACTLY
// the one Revit currently holds with write access. Measured in the field, 9 of
// 9 families: every hash threw "file is being used by another process", so
// file_changed and sha256_before/after were null on every reply, while
// save_document hashed the same situation fine because its HashOf() shares
// write access. The strong evidence was unreachable in exactly the case it
// exists for.
//
// So the rule lives here now, Revit-free, with save_document's share mode:
// FileShare.ReadWrite | FileShare.Delete lets the read succeed while Revit
// holds the file. And it keeps save_document's size cap: a file over the cap
// returns a null hash WITH the reason in Error, so the caller reports "not
// hashed, and here is why" instead of an unexplained unknown.
//
// Null stays UNKNOWN throughout: Changed() answers null - never false - when
// either side could not be hashed, because "I could not look" must not read as
// "it did not change".
// -----------------------------------------------------------------------------
using System;
using System.IO;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Size, timestamp and content hash of a file, read without demanding
    /// exclusive access - the file under measurement is usually the one Revit
    /// itself is holding open.
    /// </summary>
    public sealed class FileFacts
    {
        public bool Existed;
        public long? Size;
        public DateTime? WrittenUtc;
        public string Sha256;
        public string Error;

        /// <summary>
        /// Same cap as save_document's: hashing a multi-gigabyte central twice
        /// per save would cost more than the save. Over the cap the hash is
        /// null and Error says so.
        /// </summary>
        public const long MaxHashBytes = 512L * 1024 * 1024;

        public static FileFacts Read(string path)
        {
            return Read(path, MaxHashBytes);
        }

        /// <summary>Cap injectable so the over-cap branch is provable without a 512 MiB file.</summary>
        public static FileFacts Read(string path, long maxHashBytes)
        {
            var f = new FileFacts();
            if (string.IsNullOrEmpty(path)) { f.Error = "no path"; return f; }
            try
            {
                if (!File.Exists(path)) { f.Existed = false; return f; }
                f.Existed = true;
                var fi = new FileInfo(path);
                f.Size = fi.Length;
                f.WrittenUtc = fi.LastWriteTimeUtc;
                if (fi.Length > maxHashBytes)
                {
                    f.Error = "the file is " + fi.Length + " bytes, over the " + maxHashBytes +
                              "-byte hashing cap - size and timestamp were read, the content was not hashed";
                    return f;
                }
                // FileShare.ReadWrite | Delete, not OpenRead(). OpenRead() demands that
                // nobody holds write access, and Revit holds write access to every
                // document this evidence is ever taken of.
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete))
                    f.Sha256 = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex) { f.Error = ex.Message; }
            return f;
        }

        /// <summary>true changed, false identical, NULL when it cannot be told.</summary>
        public static bool? Changed(FileFacts before, FileFacts after)
        {
            if (before == null || after == null) return null;
            if (!before.Existed) return true;                       // it did not exist; now it does
            if (before.Sha256 == null || after.Sha256 == null) return null;
            return !string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal);
        }
    }
}
