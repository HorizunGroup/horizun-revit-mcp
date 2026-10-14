// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// AtomicFile: a write is the old file or the new one, never half of either, and
// two writers never share a temporary name (audit 2026-10-08).
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class AtomicFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hz-atomic-" + Guid.NewGuid().ToString("N"));
        public AtomicFileTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void Creates_then_replaces_and_leaves_no_temporary_behind()
        {
            string path = Path.Combine(_dir, "a.json");
            AtomicFile.WriteAllText(path, "{\"v\":1}");
            AtomicFile.WriteAllText(path, "{\"v\":\"ñ\"}");
            Assert.Equal("{\"v\":\"ñ\"}", File.ReadAllText(path));
            Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);           // no BOM
            Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
        }

        [Fact]
        public void Concurrent_writers_never_collide_on_a_temporary_name()
        {
            string path = Path.Combine(_dir, "shared.json");
            AtomicFile.WriteAllText(path, "seed");
            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            Parallel.For(0, 40, i =>
            {
                try { AtomicFile.WriteAllText(path, "writer-" + i); }
                catch (Exception ex) { failures.Add(ex); }
            });
            Assert.Empty(failures);
            Assert.StartsWith("writer-", File.ReadAllText(path));
            // None of OUR temporaries is left. Windows' own ReplaceFile can leave
            // "<name>~RF*.TMP" when unsynchronised replaces race - which is why every
            // store that has more than one writer also takes a lock or a mutex.
            Assert.Empty(Directory.GetFiles(_dir, "shared.json.*.tmp"));
        }

        [Fact]
        public void A_failed_write_leaves_the_old_file_and_no_temporary()
        {
            string path = Path.Combine(_dir, "keep.json");
            AtomicFile.WriteAllText(path, "old");
            if (OperatingSystem.IsWindows())
            {
                // Windows denies Replace without FILE_SHARE_DELETE. Unix permits
                // renaming an open file, so this failure condition is Windows only.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "new"));
            }
            else
            {
                // A directory cannot replace a file. This exercises the failed
                // swap and temporary cleanup under Unix without relying on locks.
                string directoryTarget = Path.Combine(_dir, "directory-target");
                Directory.CreateDirectory(directoryTarget);
                Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(directoryTarget, "new"));
                Assert.True(Directory.Exists(directoryTarget));
                Assert.Empty(Directory.GetFiles(_dir, "directory-target.*.tmp"));
                Directory.Delete(directoryTarget);
            }
            Assert.Equal("old", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
        }

        [Fact]
        public void Temporary_names_are_unique_per_write()
        {
            string p = Path.Combine(_dir, "x.json");
            Assert.NotEqual(AtomicFile.TemporaryFor(p), AtomicFile.TemporaryFor(p));
        }
    }
}
