using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // Frente C (4.2): the Revit-free decisions of the cooperative clash, rebuilt after R2-N.
    public class CooperativeClashPolicyTests
    {
        private static string Source(params string[] parts)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Horizun.Revit"))) root = root.Parent;
            Assert.NotNull(root);
            return File.ReadAllText(Path.Combine(new[] { root.FullName }.Concat(parts).ToArray()));
        }









        // ---- M1: a view or a save does not kill the clash; a change ends it with a clear state ---

        private static string BlockHead(string source, int at) => source.Substring(at, source.IndexOf("try", at, StringComparison.Ordinal) - at);

        [Fact]
        public void Model_epoch_ignores_saves_and_views_but_not_edits_or_links()
        {
            string source = Source("src", "Horizun.Revit", "Core", "QueryCacheLifecycle.cs");
            string[] counted = { "Changed", "Opened", "Closing", "Closed", "Synchronized", "Synchronizing", "ReloadedLatest",
                "ReloadingLatest", "LinkedOpened", "LinkedOpening" };
            foreach (string name in counted)
                Assert.Contains("static void " + name + "(", source);
            foreach (string name in counted)
            {
                int at = source.IndexOf("static void " + name + "(", StringComparison.Ordinal);
                string line = source.Substring(at, source.IndexOf('\n', at) - at);
                // Synchronized/Synchronizing/Closing/Closed/ReloadedLatest are blocks (they also keep the document bookkeeping of R2U-R2W,
                // or record the sync window): the epoch bump is their first statement.
                string bump = name == "Changed" ? "DocumentEdited(e.GetDocument);" :
                    name == "Synchronized" || name == "ReloadedLatest" ? "DocumentEdited(() => e.Document);" : "Model();";
                if (name == "Synchronized" || name == "Synchronizing" || name == "Closing" || name == "Closed" || name == "ReloadedLatest")
                    Assert.Contains(bump, source.Substring(at, source.IndexOf("try", at, StringComparison.Ordinal) - at));
                else
                    Assert.Contains("=> " + bump, line);
            }
            foreach (string name in new[] { "Saved", "SavedAs", "ViewActivated" })
            {
                int at = source.IndexOf("static void " + name + "(", StringComparison.Ordinal);
                string line = source.Substring(at, source.IndexOf('\n', at) - at);
                // SavedAs is a block since R2U (Save As moves the ledgers): it invalidates the cache and never bumps the model epoch.
                if (name == "SavedAs") line = BlockHead(source, at);
                Assert.Contains(name == "SavedAs" ? "Cache.Invalidate();" : "=> Cache.Invalidate();", line);
                Assert.DoesNotContain("Model()", line);
            }
        }

        [Fact]
        public void A_change_ends_the_run_with_one_clear_resumable_block()
        {
            string partial = "{\"result\":\"partial\",\"valid\":true,\"observed_clash_count\":7}";
            JObject o = JObject.Parse(CooperativeInterruption.Apply(partial, CooperativeInterruption.DocumentChanged,
                "A document or its observed state changed between callbacks.", "job-1", "A document or its observed state changed between callbacks."));
            Assert.False((bool)o["valid"]);
            Assert.Equal(7, (int)o["observed_clash_count"]);
            Assert.Equal("document_changed", (string)o["interruption"]["kind"]);
            Assert.True((bool)o["interruption"]["resumable"]);
            Assert.Equal("job-1", (string)o["interruption"]["resume_from_job_id"]);
            Assert.Equal("restart_from_first_pair", (string)o["interruption"]["resume_mode"]);
        }

        // ---- M4: cancellation is treated the same whenever it arrives ---------------------------

        [Fact]
        public void A_cancel_keeps_the_snapshot_exactly_as_valid_as_the_stamp_says_wherever_it_lands()
        {
            foreach (bool stale in new[] { false, true })
            {
                string reasonOrNull = stale ? "A document or its observed state changed between callbacks." : null;
                // (a) the cancel found the job waiting in the queue
                var waiting = new AsyncWork { JobId = "w" + stale, Record = null, HasStarted = true,
                    PartialResultJson = "{\"result\":\"partial\",\"valid\":true,\"observed_clash_count\":3}",
                    Revalidate = () => reasonOrNull };
                waiting.FinishCancelledWhileWaiting("Cancelled.");
                // (b) the cancel was observed inside the callback
                var running = new AsyncWork { JobId = "r" + stale, Record = null, HasStarted = true,
                    PartialResultJson = "{\"result\":\"partial\",\"valid\":true,\"observed_clash_count\":3}",
                    Revalidate = () => reasonOrNull };
                running.MarkInterrupted(CooperativeInterruption.Cancelled, "Cancelled.");

                JObject a = JObject.Parse(waiting.PartialResultJson), b = JObject.Parse(running.PartialResultJson);
                Assert.Equal((bool)a["valid"], (bool)b["valid"]);
                Assert.Equal(!stale, (bool)a["valid"]);
                Assert.Equal((string)a["interruption"]["kind"], (string)b["interruption"]["kind"]);
                Assert.Equal((int)a["observed_clash_count"], (int)b["observed_clash_count"]);
            }
        }

        [Fact]
        public void A_cancel_without_a_stamp_to_consult_never_claims_validity()
        {
            var w = new AsyncWork { JobId = "x", HasStarted = true, PartialResultJson = "{\"result\":\"partial\",\"valid\":true}" };
            w.FinishCancelledWhileWaiting("Cancelled.");
            Assert.False((bool)JObject.Parse(w.PartialResultJson)["valid"]);
        }











        // ---- M3: checkpoints and job records are bounded -----------------------------------------

        [Fact]
        public void A_long_run_writes_a_bounded_number_of_checkpoint_lines_and_counts_the_rest()
        {
            var throttle = new CheckpointThrottle();
            long now = 0;
            int written = 0;
            for (int i = 0; i < 100000; i++)
            {
                now += 50;
                string label = "clash:" + (i < 90000 ? "pairs" : "findings");
                if (throttle.ShouldWrite(label, now, 90)) written++;
            }
            Assert.Equal(written, throttle.Written);
            Assert.InRange(written, 50, 400);
            Assert.Equal(100000 - written, throttle.Suppressed);
        }

        [Fact]
        public void A_change_of_label_is_never_swallowed_before_the_ceiling()
        {
            var throttle = new CheckpointThrottle(wholeUntil: 2, maxLines: 100, maxBytes: 1 << 20, minIntervalMs: 10000);
            Assert.True(throttle.ShouldWrite("a", 0, 10));
            Assert.True(throttle.ShouldWrite("a", 1, 10));
            Assert.False(throttle.ShouldWrite("a", 2, 10));
            Assert.True(throttle.ShouldWrite("b", 3, 10));
            Assert.False(throttle.ShouldWrite("b", 4, 10));
            Assert.True(throttle.ShouldWrite("b", 20000, 10));
        }

        private sealed class MemorySink : IJobSink
        {
            public readonly List<string> Lines = new List<string>();
            public void EnsureDirectory(string directory) { }
            public void Append(string path, string line) { lock (Lines) Lines.Add(line); }
        }

        [Fact]
        public void A_bounded_job_record_stays_small_and_its_finish_line_reports_what_was_not_written()
        {
            var sink = new MemorySink();
            Job job = Job.Start("horizun_clash", sink);
            job.BoundCheckpoints();
            for (int i = 0; i < 20000; i++) job.Write("clash:pairs", i, null);
            job.Finish("ok", null);
            Assert.Equal(20000, job.Checkpoints);
            Assert.InRange(sink.Lines.Count, 3, 500);
            JObject finish = JObject.Parse(sink.Lines.Last());
            Assert.Equal(20000, (int)finish["checkpoints"]);
            int written = sink.Lines.Count(l => l.Contains("\"event\":\"checkpoint\""));
            Assert.Equal(20000 - written, (int)finish["checkpoints_not_written"]);
            Assert.Equal(20000 - written, job.CheckpointsNotWritten);
        }

        [Fact]
        public void An_ordinary_job_still_writes_one_line_per_checkpoint()
        {
            var sink = new MemorySink();
            Job job = Job.Start("t", sink);
            for (int i = 0; i < 700; i++) job.Write("step", i, 700);
            Assert.Equal(700, sink.Lines.Count(l => l.Contains("\"event\":\"checkpoint\"")));
        }

    }
}
