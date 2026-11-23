// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// THE PROGRESS OF THE CALL RUNNING NOW, HOWEVER IT WAS CALLED.
//
// Field report (Revit 2024, a group type with 29 instances of ~1,000 members): a
// synchronous edit_type_members rehearsal was cut at the 600 s limit, kept running
// ~11 more minutes inside Revit, and then the log said "finished after its caller
// had given up; result discarded (it had failed)". The failure cause - the one
// thing the caller needed - existed for a moment in memory and was thrown away;
// and nothing could be polled meanwhile, because a synchronous call has no record.
//
// So every call gets a CallProgress, and a call has AT MOST ONE record:
//
//   * A command reports through Checkpoint(): under submit_job it lands in the job
//     record the caller polls (Job.Ambient); synchronously it is kept in memory.
//   * A command that keeps its own record (execute_python, whose script calls
//     checkpoint()) obtains it through OpenForCommand.
//   * The dispatcher, when the caller times out, obtains it through OpenForTimeout.
//
// Both go through one lock, so whichever comes first opens the record and the other
// gets THE SAME one: the job_id the timeout hands out is the record the script writes
// to (review finding: a record opened by each side left the handed-out id "running"
// forever). The end is one transition under the same lock: Complete() writes the
// final result and closes the progress; a timeout that arrives after it learns the
// call already finished (and the dispatcher returns the real answer instead), and a
// checkpoint after it is dropped - never a checkpoint after the finish line, never a
// handed-out id whose record ends without the payload.
//
// Revit-free: unit-tested in Horizun.Core.Tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class CallProgress
    {
        /// <summary>Checkpoints kept in memory before a record exists; the oldest is dropped first, the count is not.</summary>
        public const int MaxBuffered = 200;

        private sealed class Entry
        {
            public string Label;
            public object Done, Total;
            public DateTime At;
        }

        private readonly object _lock = new object();
        private readonly List<Entry> _buffer = new List<Entry>();
        private Job _record;
        private int _count;
        private int _dropped;
        private string _last;
        private bool _completed;
        private bool _abandoned;

        [ThreadStatic] private static CallProgress _current;

        /// <summary>Set by the dispatcher around a synchronous call, on the UI thread; null otherwise.</summary>
        public static CallProgress Current
        {
            get { return _current; }
            set { _current = value; }
        }

        /// <summary>
        /// The one place a command reports a step. Never throws: progress is telemetry and
        /// must not fail the work it describes.
        /// </summary>
        public static void Checkpoint(string label, object done = null, object total = null)
        {
            try
            {
                Job ambient = Job.Ambient;
                if (ambient != null) { ambient.Write(label, done, total); return; }
                _current?.Write(label, done, total);
            }
            catch { }
        }

        public int Count { get { lock (_lock) return _count; } }
        public int Dropped { get { lock (_lock) return _dropped; } }
        public string LastLabel { get { lock (_lock) return _last; } }
        public Job Record { get { lock (_lock) return _record; } }
        public bool Completed { get { lock (_lock) return _completed; } }
        public bool Abandoned { get { lock (_lock) return _abandoned; } }

        /// <summary>The record was opened for the command itself (OpenForCommand) before any timeout.</summary>
        public bool AdoptedFromCommand { get; private set; }

        public void Write(string label, object done, object total) => Write(label, done, total, DateTime.Now);

        public void Write(string label, object done, object total, DateTime at)
        {
            lock (_lock)
            {
                if (_completed) return;          // nothing after the final result
                _count++;
                _last = label;
                if (_record != null) { _record.Write(label, done, total); return; }
                _buffer.Add(new Entry { Label = label, Done = done, Total = total, At = at });
                if (_buffer.Count > MaxBuffered) { _buffer.RemoveAt(0); _dropped++; }
            }
        }

        /// <summary>Under the lock: make `record` this call's record and replay what memory kept.</summary>
        private void AttachLocked(Job record)
        {
            if (_dropped > 0)
                record.Write("(" + _dropped + " earlier checkpoint(s) were not kept in memory)", null, null, _buffer.Count > 0 ? _buffer[0].At : DateTime.Now);
            foreach (Entry e in _buffer) record.Write(e.Label, e.Done, e.Total, e.At);
            _buffer.Clear();
            _record = record;
        }

        /// <summary>
        /// The record a command writes its own progress to (execute_python's checkpoint()).
        /// If the call already has one - the caller timed out first - that one is returned
        /// and `open` is not called. A record that could not be made durable is returned
        /// to the command but not attached. The command never finishes a record obtained
        /// here: the dispatcher does, in Complete.
        /// </summary>
        public Job OpenForCommand(Func<Job> open)
        {
            lock (_lock)
            {
                if (_record != null) return _record;
                Job r = open();
                if (r == null || !r.IsDurable) return r;
                AttachLocked(r);
                AdoptedFromCommand = true;
                return r;
            }
        }

        /// <summary>
        /// The caller gave up. Returns the call's record - the command's own if it has one,
        /// else a new one from `open` - with `note` written, and marks the call abandoned so
        /// Complete writes the full result. Null with `alreadyCompleted` when the call had
        /// finished first: its real answer is about to be signalled and must be returned
        /// instead of a job_id.
        /// </summary>
        public Job OpenForTimeout(Func<Job> open, string note, out bool alreadyCompleted)
        {
            lock (_lock)
            {
                alreadyCompleted = _completed;
                if (_completed) return null;
                if (_record == null)
                {
                    Job r = open();
                    if (r == null) return null;
                    AttachLocked(r);
                }
                _abandoned = true;
                if (note != null) { _count++; _last = note; _record.Write(note, null, null); }
                return _record;
            }
        }

        /// <summary>
        /// The call returned. Once only, under the lock that OpenForTimeout takes: `write`
        /// gets the record (when the call has one) and whether the caller had given up -
        /// true: the full result is owed to whoever polls; false: the answer went over the
        /// pipe. After this, checkpoints are dropped and a timeout reports the call done.
        /// </summary>
        public void Complete(Action<Job, bool> write)
        {
            lock (_lock)
            {
                if (_completed) return;
                _completed = true;
                if (_record == null || write == null) return;
                try { write(_record, _abandoned); }
                catch (Exception ex) { try { Log.Warn("could not close the call's job record " + _record.Id + ": " + ex.Message); } catch { } }
            }
        }
    }
}
