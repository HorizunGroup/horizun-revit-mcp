// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Audit 2026-10-08: every tool call ran on a ThreadPool thread (Task.Run) that then
// BLOCKED for the whole round trip to Revit, and each pipe read blocked a second
// pool thread. Sixteen queued calls hold thirty-two pool threads asleep, and the
// pool grows them slowly - so the server's own timers, heartbeats and replies
// starve behind work that is only waiting. Blocking waits now run on dedicated
// threads (TaskCreationOptions.LongRunning). A source check, because the defect is
// a scheduling property no unit can provoke deterministically.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Server.Tests
{
    public class BlockingWaitThreadTests
    {
        private static string Source(string relative)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Server"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        [Fact]
        public void Pipe_reads_never_block_a_thread_pool_thread()
        {
            string text = Source("src/Horizun.Server/PipeClient.cs");
            Assert.DoesNotMatch(new Regex(@"Task\.Run\("), text);
            Assert.Matches(new Regex(@"TaskCreationOptions\.LongRunning"), text);
        }

        [Fact]
        public void Tool_calls_run_on_dedicated_threads()
        {
            string text = Source("src/Horizun.Server/Program.cs");
            // The only Task.Run left is the async heartbeat, which awaits and never blocks.
            Assert.Equal(1, Regex.Matches(text, @"Task\.Run\(").Count);
            Assert.Matches(new Regex(@"Task\.Run\(async "), text);
        }
    }
}
