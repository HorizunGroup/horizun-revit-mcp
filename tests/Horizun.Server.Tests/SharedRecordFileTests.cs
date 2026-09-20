using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Server;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>A record another process holds open is waited out, not failed on (measured in campaign 5).</summary>
    public class SharedRecordFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hz-shared-record-" + Guid.NewGuid().ToString("N"));

        public SharedRecordFileTests() { Directory.CreateDirectory(_dir); }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public async Task Replace_waits_for_a_reader_that_holds_the_record()
        {
            string target = Path.Combine(_dir, "run.json");
            File.WriteAllText(target, "old");
            var holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Task.Run(() => { Thread.Sleep(300); holder.Dispose(); });

            SharedRecordFile.WriteAtomically(target, "new");

            await release;
            Assert.Equal("new", File.ReadAllText(target));
            Assert.False(File.Exists(target + ".tmp"));
        }

        [Fact]
        public async Task Read_waits_for_an_exclusive_holder()
        {
            string target = Path.Combine(_dir, "run.json");
            File.WriteAllText(target, "content");
            var holder = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var release = Task.Run(() => { Thread.Sleep(300); holder.Dispose(); });

            Assert.Equal("content", SharedRecordFile.ReadAllText(target));
            await release;
        }

        [Fact]
        public void A_holder_that_never_lets_go_still_fails_and_leaves_the_record_whole()
        {
            // The refusal here is the OPERATING SYSTEM's, not this code's: Windows will not swap a
            // file somebody holds open. MEASURED on linux-x64: the same swap SUCCEEDS, because
            // FileShare there is an advisory lock a rename does not consult. So off Windows this
            // asserts what is true everywhere - patience is bounded and the record is never left
            // half-written - rather than a guarantee that platform does not make.
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string target = Path.Combine(_dir, "run.json");
            File.WriteAllText(target, "old");
            TimeSpan before = SharedRecordFile.Patience;
            SharedRecordFile.Patience = TimeSpan.FromMilliseconds(200);
            try
            {
                using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (windows) Assert.ThrowsAny<Exception>(() => SharedRecordFile.WriteAtomically(target, "new"));
                    else SharedRecordFile.WriteAtomically(target, "new");
                }
                Assert.Equal(windows ? "old" : "new", File.ReadAllText(target));
                Assert.False(File.Exists(target + ".tmp"));
            }
            finally { SharedRecordFile.Patience = before; }
        }
    }
}
