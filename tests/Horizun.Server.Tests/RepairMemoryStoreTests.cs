// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Audit 2026-10-08, the repair memory's file:
//   * Load swallowed a parse failure and returned an EMPTY memory, and the next
//     Save wrote that emptiness over the file - every recorded remedy gone, and
//     nothing said. A file that does not parse is now moved aside, kept, and named.
//   * Save wrote straight over the live file (a crash left half a file).
//   * Two MCP server processes (Claude and Codex) each held only an in-process
//     lock, so their load-modify-save cycles interleaved. A named mutex now spans
//     processes.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Horizun.Revit.Core;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class RepairMemoryStoreTests : IDisposable
    {
        private readonly ExcelBackupRoot _root = new ExcelBackupRoot();   // a private data root for the suite
        public void Dispose() => _root.Dispose();

        private static JObject Observe(string message) => new JObject
        {
            ["operation"] = "observe", ["tool"] = "horizun_test_tool", ["failure_class"] = "x", ["message"] = message
        };

        [Fact]
        public void A_corrupt_memory_is_moved_aside_and_named_never_overwritten()
        {
            string path = RepairMemory.Path();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{ \"entries\": [ half a file");

            JObject reply = RepairMemoryTool.Handle(Observe("the wall could not be joined"), CancellationToken.None);

            string problem = (string)reply["memory_problem"];
            Assert.False(string.IsNullOrEmpty(problem));
            string[] aside = Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".corrupt-*");
            Assert.Single(aside);
            Assert.Equal("{ \"entries\": [ half a file", File.ReadAllText(aside[0]));
            Assert.Contains(Path.GetFileName(aside[0]), problem);
            // The new file holds the new observation and parses.
            Assert.Single(RepairMemory.Load(path, out string again).Values);
            Assert.Null(again);
        }

        [Fact]
        public void A_save_is_atomic_and_leaves_no_temporary()
        {
            string path = RepairMemory.Path();
            RepairMemoryTool.Handle(Observe("the floor could not be split"), CancellationToken.None);
            RepairMemoryTool.Handle(Observe("the floor could not be split"), CancellationToken.None);
            string dir = Path.GetDirectoryName(path);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.Equal(2, RepairMemory.Load(path, out _).Values.Single().Occurrences);
        }

        [Fact]
        public void Another_process_holding_the_memory_is_waited_for_then_refused()
        {
            string name = RepairMemory.MutexName(RepairMemory.Path());
            TimeSpan saved = RepairMemoryTool.MutexWait;
            RepairMemoryTool.MutexWait = TimeSpan.FromMilliseconds(200);
            var held = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                using (var m = new Mutex(false, name))
                {
                    m.WaitOne();
                    held.Set();
                    release.Wait();
                    m.ReleaseMutex();
                }
            });
            try
            {
                holder.Start();
                held.Wait();
                var ex = Assert.Throws<ToolRefusal>(() => RepairMemoryTool.Handle(Observe("x failed"), CancellationToken.None));
                Assert.Contains("another", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(RepairMemory.Path()));
            }
            finally
            {
                release.Set();
                holder.Join();
                RepairMemoryTool.MutexWait = saved;
            }
        }
    }
}
