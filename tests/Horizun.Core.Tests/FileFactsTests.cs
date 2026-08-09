// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// FileFacts (story 5.12): the save evidence must be readable while Revit holds
// the file, because Revit ALWAYS holds the file this evidence is taken of.
//
// The regression these tests pin: the old private copy inside family_apply used
// File.OpenRead(), which demands that nobody else holds write access. Revit
// does, for every open document, so the hash threw on all 9 of 9 measured
// families and file_changed was null on every reply - while save_document
// hashed the same situation fine. The first test holds the file open with
// write access, the way Revit does, and proves BOTH halves: the shared-mode
// read succeeds, and the OpenRead approach it replaced really does throw.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class FileFactsTests : IDisposable
    {
        private readonly string _dir;

        public FileFactsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-filefacts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Write(string name, string content)
        {
            var p = Path.Combine(_dir, name);
            File.WriteAllText(p, content, Encoding.UTF8);
            return p;
        }

        // The 9/9 field failure. The holder opens the file the way Revit holds a
        // document - write access, sharing reads - and the hash must still land.
        [Fact]
        public void Hashes_while_another_handle_holds_the_file_with_write_access()
        {
            var path = Write("held.rfa", "family bytes");
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                // The approach this replaced: OpenRead() demands no other writer, so it
                // throws here. If this ever STOPS throwing, the share-mode fix is no
                // longer load-bearing and the comment in FileFacts is stale.
                Assert.ThrowsAny<IOException>(() => { using (File.OpenRead(path)) { } });

                var f = FileFacts.Read(path);
                Assert.True(f.Existed);
                Assert.NotNull(f.Sha256);
                Assert.Null(f.Error);
                Assert.Equal(new FileInfo(path).Length, f.Size);
            }
        }

        [Fact]
        public void Identical_content_reads_as_unchanged_and_moved_bytes_as_changed()
        {
            var path = Write("f.rfa", "v1");
            var before = FileFacts.Read(path);
            var same = FileFacts.Read(path);
            Assert.False(FileFacts.Changed(before, same));

            File.WriteAllText(path, "v2 - different bytes", Encoding.UTF8);
            var after = FileFacts.Read(path);
            Assert.True(FileFacts.Changed(before, after));
        }

        [Fact]
        public void A_file_that_did_not_exist_before_reads_as_changed()
        {
            var path = Path.Combine(_dir, "not-yet.rfa");
            var before = FileFacts.Read(path);
            Assert.False(before.Existed);

            Write("not-yet.rfa", "now it does");
            var after = FileFacts.Read(path);
            Assert.True(FileFacts.Changed(before, after));
        }

        // Null is UNKNOWN, never false: a hash that could not be taken must not let
        // "I could not look" read as "it did not change".
        [Fact]
        public void Unreadable_hash_makes_changed_null_not_false()
        {
            var path = Write("locked.rfa", "content");
            FileFacts before;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                before = FileFacts.Read(path);

            Assert.True(before.Existed);       // existence and size still read via FileInfo
            Assert.Null(before.Sha256);
            Assert.NotNull(before.Error);

            var after = FileFacts.Read(path);
            Assert.NotNull(after.Sha256);
            Assert.Null(FileFacts.Changed(before, after));
            Assert.Null(FileFacts.Changed(after, before));
        }

        [Fact]
        public void Over_the_cap_the_hash_is_withheld_and_error_says_why()
        {
            var path = Write("big.rfa", "these bytes exceed a tiny injected cap");
            var f = FileFacts.Read(path, maxHashBytes: 4);

            Assert.True(f.Existed);
            Assert.NotNull(f.Size);            // size and timestamp still measured
            Assert.NotNull(f.WrittenUtc);
            Assert.Null(f.Sha256);
            Assert.Contains("hashing cap", f.Error);
        }

        [Fact]
        public void No_path_and_null_sides_stay_unknown()
        {
            var none = FileFacts.Read(null);
            Assert.Equal("no path", none.Error);
            Assert.Null(FileFacts.Changed(null, none));
            Assert.Null(FileFacts.Changed(none, null));
        }
    }
}
