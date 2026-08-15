// -----------------------------------------------------------------------------
// Horizun Core tests - an async job_id is a PROMISE, and it has to be kept.
//
// THE DEFECT. Job.Start assigned the id first and wrapped everything after it in
// `catch { job.Path = null; }`. So when the record could not be created - the
// jobs directory unwritable, the data root occupied by a file, the disk full -
// Start still returned a Job with a perfectly good-looking Id and no file behind
// it. The guard meant to catch that read:
//
//     if (string.IsNullOrWhiteSpace(job.Id)) ...
//
// and Id is never blank, because it is assigned before anything can fail. So the
// work was queued, the caller was told status "queued" with a job_id, and that
// id addressed nothing. For horizun_submit_job and the async execute_python this
// is not a lost log line: the record IS the channel. The caller got a job_id, went
// away, and every later horizun_job_status for it answers "unknown". The work
// runs - possibly a mutation - and its outcome is unobservable.
//
// So: an async job record must be on disk, flushed, BEFORE the id is handed out;
// and when it cannot be, the answer is an explicit refusal with nothing queued.
//
// The synchronous path keeps the old best-effort behaviour deliberately, and for
// a reason that is the mirror image: there the reply carries the answer, so the
// record is a convenience, and failing the command over it would be worse.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class JobDurabilityTests : IDisposable
    {
        private readonly List<string> _roots = new List<string>();

        private string NewRoot()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz-job-" + Guid.NewGuid().ToString("N"));
            _roots.Add(root);
            return root;
        }

        /// <summary>A sink that fails exactly where a test wants it to.</summary>
        private sealed class BrokenSink : IJobSink
        {
            private readonly bool _failDirectory;
            private readonly int _failOnAppendNumber;   // 1 = the start line
            private int _appends;
            public int Appended => _appends;

            internal BrokenSink(bool failDirectory = false, int failOnAppendNumber = 0)
            {
                _failDirectory = failDirectory;
                _failOnAppendNumber = failOnAppendNumber;
            }

            public void EnsureDirectory(string directory)
            {
                if (_failDirectory) throw new IOException("simulated: the jobs directory cannot be created");
            }

            public void Append(string path, string line)
            {
                _appends++;
                if (_failOnAppendNumber > 0 && _appends == _failOnAppendNumber)
                    throw new IOException("simulated: the disk refused this write");
            }
        }

        // -----------------------------------------------------------------
        // Job.Start - the durable entry point.
        // -----------------------------------------------------------------

        /// <summary>
        /// The whole promise in one assertion: when Start returns, the start event is
        /// already readable on disk. Not buffered, not queued behind a handle somebody
        /// still holds - readable.
        /// </summary>
        [Fact]
        public void Start_returns_only_after_the_start_event_is_on_disk()
        {
            WithRoot(NewRoot(), () =>
            {
                Job job = Job.Start("horizun_execute_python");

                Assert.False(string.IsNullOrWhiteSpace(job.Id));
                Assert.True(job.IsDurable);
                Assert.True(File.Exists(job.Path));

                string first = File.ReadAllLines(job.Path)[0];
                Assert.Contains("\"event\":\"start\"", first, StringComparison.Ordinal);
                Assert.Contains("horizun_execute_python", first, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// THE REPORTED SHAPE: the data root is occupied by a FILE where the jobs
        /// directory has to go. Nothing can be created, and Start must say so rather
        /// than hand back an id.
        /// </summary>
        [Fact]
        public void A_data_root_occupied_by_a_file_refuses_instead_of_handing_out_an_id()
        {
            string root = NewRoot();
            Directory.CreateDirectory(root);
            File.WriteAllText(System.IO.Path.Combine(root, "jobs"), "not a directory");

            WithRoot(root, () =>
            {
                var ex = Assert.Throws<JobRecordException>(() => Job.Start("horizun_execute_python"));
                Assert.Contains("job record", ex.Message, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>A directory that cannot be created is the same refusal.</summary>
        [Fact]
        public void A_jobs_directory_that_cannot_be_created_refuses()
        {
            WithRoot(NewRoot(), () =>
                Assert.Throws<JobRecordException>(
                    () => Job.Start("horizun_execute_python", new BrokenSink(failDirectory: true))));
        }

        /// <summary>
        /// And a writer that accepts the directory but refuses the start line. This is
        /// the one the old code hid best: the directory existed, the id was assigned,
        /// and only the append failed - straight into `catch { job.Path = null; }`.
        /// </summary>
        [Fact]
        public void A_writer_that_refuses_the_start_line_refuses_the_job()
        {
            WithRoot(NewRoot(), () =>
            {
                var sink = new BrokenSink(failOnAppendNumber: 1);
                var ex = Assert.Throws<JobRecordException>(() => Job.Start("horizun_execute_python", sink));

                Assert.Equal(1, sink.Appended);              // it tried exactly once
                Assert.Contains("simulated", ex.Message, StringComparison.Ordinal);   // and says what went wrong
            });
        }

        // -----------------------------------------------------------------
        // After the start line: failures are recorded, never swallowed.
        // -----------------------------------------------------------------

        /// <summary>
        /// A write that fails AFTER the job is running must not throw - that would
        /// replace the job's real outcome with its bookkeeping - but it must leave a
        /// mark somebody can read, because the record is now incomplete and every
        /// reader of it is entitled to know that.
        /// </summary>
        [Fact]
        public void A_later_write_failure_is_recorded_rather_than_swallowed()
        {
            WithRoot(NewRoot(), () =>
            {
                var sink = new BrokenSink(failOnAppendNumber: 2);   // start succeeds, the next one does not
                Job job = Job.Start("horizun_execute_python", sink);

                Assert.True(job.IsDurable);
                Assert.Null(job.WriteFault);

                job.Write("halfway", 5, 10);                        // this is append #2

                Assert.NotNull(job.WriteFault);
                Assert.Contains("simulated", job.WriteFault, StringComparison.Ordinal);
                Assert.False(job.RecordIsComplete);
            });
        }

        /// <summary>The fault is sticky: the first failure is the one that explains the gap.</summary>
        [Fact]
        public void The_first_write_fault_is_kept_and_later_writes_do_not_erase_it()
        {
            WithRoot(NewRoot(), () =>
            {
                var sink = new BrokenSink(failOnAppendNumber: 2);
                Job job = Job.Start("horizun_execute_python", sink);

                job.Write("first", null, null);      // fails
                string firstFault = job.WriteFault;
                job.Write("second", null, null);     // succeeds
                job.Finish("ok", null);              // succeeds

                Assert.Equal(firstFault, job.WriteFault);
                Assert.False(job.RecordIsComplete);
            });
        }

        // -----------------------------------------------------------------
        // The admission decision both async commands share.
        // -----------------------------------------------------------------

        /// <summary>
        /// A record that cannot be opened is an explicit refusal, and the refusal says
        /// the thing a caller most needs to hear: nothing was queued.
        /// </summary>
        [Fact]
        public void Async_admission_refuses_when_the_record_cannot_be_opened()
        {
            WithRoot(NewRoot(), () =>
            {
                Assert.False(AsyncJobAdmission.TryOpen("horizun_export", new BrokenSink(failDirectory: true),
                                                      out Job job, out string refusal));
                Assert.Null(job);
                Assert.Contains("Nothing was queued", refusal, StringComparison.Ordinal);
                Assert.Contains("horizun_export", refusal, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void Async_admission_hands_back_a_durable_record_when_it_can()
        {
            WithRoot(NewRoot(), () =>
            {
                Assert.True(AsyncJobAdmission.TryOpen("horizun_export", null, out Job job, out string refusal));
                Assert.Null(refusal);
                Assert.NotNull(job);
                Assert.True(job.IsDurable);
                Assert.True(File.Exists(job.Path));
            });
        }

        // -----------------------------------------------------------------
        // The synchronous path keeps its best-effort behaviour.
        // -----------------------------------------------------------------

        /// <summary>
        /// A synchronous command hands its answer back over the pipe. Losing the job
        /// record there costs a log line, not the result, so it must NOT fail the
        /// command - but it must also not pretend the record exists.
        /// </summary>
        [Fact]
        public void Best_effort_start_never_throws_but_admits_it_is_not_durable()
        {
            WithRoot(NewRoot(), () =>
            {
                Job job = Job.StartBestEffort("horizun_execute_python", new BrokenSink(failDirectory: true));

                Assert.NotNull(job);
                Assert.False(job.IsDurable);
                Assert.NotNull(job.WriteFault);

                // And it stays usable as a no-op sink, so the command around it runs.
                job.Write("still running", 1, 2);
                job.Finish("ok", null);
            });
        }

        private static void WithRoot(string root, Action action)
        {
            using (new EnvGuard())
            {
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, root);
                action();
            }
        }

        public void Dispose()
        {
            foreach (string root in _roots)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
