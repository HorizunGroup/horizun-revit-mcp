// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// A record of a long piece of work, written as it happens.
//
// Two problems, one file.
//
// FIRST: while a long command runs, the Revit UI thread is inside it and the pipe
// is waiting. Ask "how is it going?" and nothing can answer, because the only
// channel is the one that is busy. So progress does not go through the channel —
// it goes to a file, and the MCP server reads that file without touching Revit at
// all. You can watch a thirty minute job from outside the thing that is stuck.
//
// SECOND: if Revit dies at minute twenty, everything the run knew dies with it,
// and the only honest thing anyone can say afterwards is "no idea how far it
// got". Which is exactly the answer nobody can act on. The file is append-only
// and flushed line by line, so a crash leaves every checkpoint that had already
// happened. The next run reads it and skips what is already done.
//
// It records labels and counts, never model content: a job log is not a place to
// put a client's data.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Horizun.Revit.Core
{
    public sealed class Job
    {
        private readonly object _gate = new object();
        private int _checkpoints;
        private bool _running;

        public string Id { get; private set; }
        public string Path { get; private set; }
        public int Checkpoints { get { return _checkpoints; } }

        private Job() { }

        /// <summary>
        /// Where job records live. The SERVER reads this same directory to answer
        /// horizun_job_status without touching Revit, so the two must not each compute
        /// it - HorizunPaths is the single answer.
        /// </summary>
        public static string Dir()
        {
            return HorizunPaths.JobsDir();
        }

        /// <summary>
        /// The record a command should write to, when somebody upstream already opened
        /// one for this work.
        ///
        /// Measured live: a 150-second run_async job produced TWO records. The queue
        /// opened one and handed back its id; the command then opened its own and the
        /// script's 15 checkpoints went there. The caller polled the id it was given and
        /// saw checkpoint_count 0 for two and a half minutes - no progress at all, on
        /// exactly the long job run_async exists for - while the progress sat in a second
        /// file nobody had been told about.
        ///
        /// [ThreadStatic] because commands run on Revit's UI thread, one at a time. Set
        /// and cleared by the dispatcher around the call, so the synchronous path is
        /// untouched: Ambient is null there and the command opens its own record exactly
        /// as before.
        /// </summary>
        [ThreadStatic]
        private static Job _ambient;

        public static Job Ambient
        {
            get { return _ambient; }
            set { _ambient = value; }
        }

        /// <summary>Open a job record. Never throws: a job that cannot be logged still runs.</summary>
        public static Job Start(string tool)
        {
            var job = new Job();
            try
            {
                job.Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Directory.CreateDirectory(Dir());
                job.Path = System.IO.Path.Combine(Dir(), job.Id + ".jsonl");
                // The pid of the process writing this record. A record with no finish
                // line is either a job in progress or a process that died, and the LOG
                // cannot tell those apart - but the operating system can, if the record
                // says which process to ask about. The server checks this pid without
                // touching Revit, the same way it reads the rest of the file.
                int pid;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { pid = 0; }
                job.Append("{\"event\":\"start\",\"tool\":" + Str(tool) + ",\"pid\":" + pid + ",\"at\":" + Now() + "}");
            }
            catch { job.Path = null; }
            return job;
        }

        /// <summary>
        /// The UI thread has picked this up. Written by whoever starts the work.
        ///
        /// Without it, a record with no finish line covered THREE states: queued and
        /// waiting for a turn, running right now, and died with the process. The first
        /// is knowable exactly, and reporting it as the same unresolvable ambiguity as
        /// the third threw away the one fact the system had.
        ///
        /// Idempotent. A second call writes nothing: the first transition is the true
        /// one, and a record claiming to have started twice would be worse than one
        /// that did not say so at all.
        /// </summary>
        public void MarkRunning()
        {
            lock (_gate)
            {
                if (_running) return;
                _running = true;
                Append("{\"event\":\"running\",\"at\":" + Now() + "}");
            }
        }

        /// <summary>
        /// One step done. `done`/`total` are optional — a job that knows it is on item 40
        /// of 300 can say so, and one that only knows it reached a stage says that.
        /// Called from the script as checkpoint("...", done, total).
        /// </summary>
        public void Write(string label, object done, object total)
        {
            lock (_gate)
            {
                _checkpoints++;
                Append("{\"event\":\"checkpoint\",\"n\":" + _checkpoints +
                       ",\"label\":" + Str(label) +
                       ",\"done\":" + Num(done) +
                       ",\"total\":" + Num(total) +
                       ",\"at\":" + Now() + "}");
            }
        }

        /// <summary>
        /// The job's OUTPUT, kept because an async caller never sees the reply.
        ///
        /// A synchronous run hands its result back over the pipe and the record only
        /// needs to say it happened. run_async has no such reply - the caller got a
        /// job_id and went away - so the answer has to survive here or it is lost, and
        /// "it finished, status ok" with no output is not an answer to anything.
        ///
        /// Written BEFORE the finish line, so a reader that sees finish can rely on the
        /// result already being there.
        /// </summary>
        public void Result(string payload)
        {
            lock (_gate)
            {
                Append("{\"event\":\"result\",\"payload\":" + Str(payload ?? "") + ",\"at\":" + Now() + "}");
            }
        }

        public void Finish(string status, string note)
        {
            lock (_gate)
            {
                Append("{\"event\":\"finish\",\"status\":" + Str(status) +
                       ",\"checkpoints\":" + _checkpoints +
                       ",\"note\":" + Str(note) +
                       ",\"at\":" + Now() + "}");
            }
        }

        /// <summary>
        /// Append one line and let go of the handle. Slower than holding the file open,
        /// and the point: everything written is on disk the moment it is written, so a
        /// process that dies mid-job leaves a complete record up to that instant.
        /// </summary>
        private void Append(string line)
        {
            if (string.IsNullOrEmpty(Path)) return;
            try { File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(false)); }
            catch { }
        }

        private static string Now()
        {
            return Str(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        }

        private static string Num(object v)
        {
            if (v == null) return "null";
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("0.####", CultureInfo.InvariantCulture); }
            catch { return "null"; }
        }

        /// <summary>Minimal JSON string escaping — this file has no serializer dependency on purpose.</summary>
        private static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
