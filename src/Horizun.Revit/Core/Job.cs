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
// Async results can contain model names, paths, element ids and parameter values.
// They are therefore governed by the explicit job retention policy rather than
// described as content-free logs.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// The record could not be opened, so no job_id was handed out. Thrown only by the
    /// DURABLE path - the one whose caller has no other channel to hear an answer on.
    /// </summary>
    public sealed class JobRecordException : Exception
    {
        public JobRecordException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Where a job record's lines actually go. A seam, and a narrow one: the only
    /// reason it exists is that "the disk refused this write" is a state a real
    /// filesystem will not produce on request, and it is precisely the state whose
    /// mishandling made an async job_id address nothing.
    /// </summary>
    public interface IJobSink
    {
        void EnsureDirectory(string directory);

        /// <summary>Append one line and do not return until it is ON DISK.</summary>
        void Append(string path, string line);
    }

    /// <summary>The real one: append, then flush through to the device.</summary>
    public sealed class FileJobSink : IJobSink
    {
        public static readonly FileJobSink Instance = new FileJobSink();

        public void EnsureDirectory(string directory) => Directory.CreateDirectory(directory);

        public void Append(string path, string line)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                // Flush(true) rather than File.AppendAllText, which returns once the
                // bytes are with the OS. The guarantee this file is built on is that a
                // process which dies mid-job leaves every checkpoint that had already
                // happened - and "already happened" has to mean on the device.
                stream.Flush(true);
            }
        }
    }

    public sealed class Job
    {
        // Keep ordinary progress files line-oriented and cheap to inspect. Larger
        // terminal results are stored as one bounded, hashed artifact and the JSONL
        // carries only its reference. Reserve room for the MCP result envelope,
        // structuredContent wrapper, task metadata and the bounded checkpoint summary;
        // accepting Contract.MaxReplyBytes as payload would make the final reply exceed
        // the transport even though the artifact itself fit.
        internal const int MaxInlineResultRecordBytes = 48 * 1024;
        public const int ReplyEnvelopeReserveBytes = Horizun.Contracts.Contract.AsyncResultEnvelopeReserveBytes;
        public const int MaxExternalResultBytes = Horizun.Contracts.Contract.MaxAsyncResultBytes;
        public static readonly TimeSpan MaxRetentionLease = TimeSpan.FromMilliseconds(
            Horizun.Contracts.Contract.MaxTaskTtlMilliseconds);

        private readonly object _gate = new object();
        private readonly IJobSink _sink;
        private int _checkpoints;
        private bool _running;
        private string _lastLabel;
        private bool _faultPublished;

        public string Id { get; private set; }
        public string Path { get; private set; }
        public int Checkpoints { get { return _checkpoints; } }

        /// <summary>
        /// True when the start event reached the disk. False only on a best-effort
        /// record, which is a log that failed to open, not a channel anybody is polling.
        /// </summary>
        public bool IsDurable { get; private set; }

        /// <summary>
        /// The FIRST write that failed after the record was opened, or null. Sticky: a
        /// later success does not fill the gap the failure left, so it must not erase
        /// the explanation for it.
        /// </summary>
        public string WriteFault { get; private set; }

        /// <summary>Every line this job tried to write is in the file.</summary>
        public bool RecordIsComplete => IsDurable && WriteFault == null;

        /// <summary>
        /// The label of the most recent checkpoint, or null before the first one.
        ///
        /// It is here so that something raised WHILE the job runs can be attributed to
        /// the step the job itself declared it was on - Interference asks for it when
        /// Revit puts up a dialog, and a batch that checkpoints per model gets each
        /// dialog labelled with the model. In memory only: the record on disk already
        /// has every label, and this is the one that is current.
        /// </summary>
        public string LastCheckpoint { get { lock (_gate) { return _lastLabel; } } }

        // One constructor, taking the sink: the parameterless one this branch used to
        // carry would leave _sink null, and every call site (Start, StartBestEffort)
        // already goes through here.
        private Job(IJobSink sink) { _sink = sink ?? FileJobSink.Instance; }

        /// <summary>
        /// Where job records live. The SERVER reads this same directory to answer
        /// horizun_job_status without touching Revit, so the two must not each compute
        /// it - HorizunPaths is the single answer.
        /// </summary>
        public static string Dir()
        {
            return HorizunPaths.JobsDir();
        }

        public static string ResultsDir() => System.IO.Path.Combine(Dir(), "results");
        public static string LeasesDir() => System.IO.Path.Combine(Dir(), "leases");

        /// <summary>
        /// Protect this durable job from configured job retention while an MCP task is
        /// entitled to retrieve it. The lease is written before the work is queued; an
        /// admission that cannot persist it is refused rather than returning a task id
        /// whose result a concurrent retention pass may delete.
        /// </summary>
        public void ProtectUntil(DateTimeOffset untilUtc)
        {
            if (!IsDurable || string.IsNullOrWhiteSpace(Id))
                throw new InvalidOperationException("a retention lease requires an open durable job record");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            untilUtc = untilUtc.ToUniversalTime();
            if (untilUtc <= now || untilUtc > now.Add(MaxRetentionLease).AddMinutes(1))
                throw new ArgumentOutOfRangeException("untilUtc", "job retention lease must be in the next seven days");

            // Custom sinks are test seams and do not address the production store.
            if (!ReferenceEquals(_sink, FileJobSink.Instance)) return;
            using (DurableStoreRetention.AcquireJobStoreMutex())
            {
                // Job.Start and this call are separate operations. If a retention pass
                // won the gap and removed the just-created record, refuse admission;
                // otherwise the same mutex prevents deletion until the durable lease is
                // visible and every later pass will protect the job.
                if (string.IsNullOrEmpty(Path) || !File.Exists(Path))
                    throw new IOException("job record disappeared before its retention lease was established");
                Directory.CreateDirectory(LeasesDir());
                string path = System.IO.Path.Combine(LeasesDir(), Id + ".json");
                string temp = System.IO.Path.Combine(LeasesDir(), "." + Id + "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    string json = new JObject
                    {
                        ["schema"] = 1,
                        ["job_id"] = Id,
                        ["retain_until_utc"] = untilUtc.ToString("O", CultureInfo.InvariantCulture)
                    }.ToString(Formatting.None);
                    WriteDurableFile(temp, Encoding.UTF8.GetBytes(json));
                    if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                    temp = null;
                }
                finally { if (temp != null) try { File.Delete(temp); } catch { } }
            }
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

        /// <summary>
        /// Open a job record DURABLY, or throw. When this returns, the start event is on
        /// disk and the id it carries addresses a file that exists.
        ///
        /// It used to be "never throws: a job that cannot be logged still runs", and for
        /// a synchronous command that is right - see StartBestEffort. For asynchronous
        /// work it was exactly wrong. The caller of horizun_submit_job receives a job_id
        /// and nothing else; the record is not a log of the answer, it IS the answer. So
        /// an id that names no file is a promise the bridge cannot keep, handed out at
        /// the one moment the caller has no way to check.
        ///
        /// The old shape also defeated its own guard: Id was assigned BEFORE the first
        /// thing that could fail, so `if (string.IsNullOrWhiteSpace(job.Id))` in
        /// SubmitJobCommand never fired. Here the id is not assigned until the write has
        /// happened.
        /// </summary>
        public static Job Start(string tool, IJobSink sink = null)
        {
            var job = new Job(sink);
            try
            {
                job.OpenRecord(tool);
                job.IsDurable = true;
                return job;
            }
            catch (Exception ex)
            {
                throw new JobRecordException(
                    "The persistent job record for '" + tool + "' could not be created in " + Dir() + " (" +
                    ex.Message + "). No job id was issued and nothing was queued: an asynchronous caller is " +
                    "handed a job_id and nothing else, so an id whose record does not exist would leave the " +
                    "work running and every later horizun_job_status answering 'unknown'.", ex);
            }
        }

        /// <summary>
        /// Open a job record if the disk allows it, and carry on if it does not.
        ///
        /// For the SYNCHRONOUS path only, where the reply carries the result back over
        /// the pipe and the record is a convenience. Failing a command that is about to
        /// succeed because its log could not be opened would be the worse trade. The
        /// difference from the old behaviour is that the failure is now visible -
        /// IsDurable is false and WriteFault says why - instead of being a null Path
        /// nobody looked at.
        /// </summary>
        public static Job StartBestEffort(string tool, IJobSink sink = null)
        {
            var job = new Job(sink);
            try
            {
                job.OpenRecord(tool);
                job.IsDurable = true;
            }
            catch (Exception ex)
            {
                job.Path = null;
                job.IsDurable = false;
                job.WriteFault = ex.Message;
                Log.Warn("The job record for '" + tool + "' could not be opened in " + Dir() + " (" + ex.Message +
                         "). The command still runs and its result is returned over the pipe; only the " +
                         "progress record is missing.");
            }
            return job;
        }

        /// <summary>
        /// Create the directory, name the file, write the start line. Every failure
        /// propagates - which of the two Start methods called it decides what that means.
        /// </summary>
        private void OpenRecord(string tool)
        {
            string dir = Dir();
            _sink.EnsureDirectory(dir);

            // Custom sinks exist to prove disk failures and may not write to this
            // directory at all. Retention applies only to the real durable store.
            if (ReferenceEquals(_sink, FileJobSink.Instance))
            {
                try
                {
                    DurableStoreRetentionReport retention = DurableStoreRetention.Apply(
                        dir, DurableStoreKind.Jobs, Settings.RawValue, DateTime.UtcNow);
                    if (retention.RemovedFiles > 0 || retention.Errors.Count > 0 ||
                        (!string.IsNullOrEmpty(retention.Note) && retention.Note.IndexOf("keeps records forever", StringComparison.Ordinal) < 0))
                        Log.Info("job retention: " + retention.Summary());
                }
                catch (Exception ex) { Log.Warn("job retention failed closed; no record was deleted (" + ex.Message + ")."); }
            }

            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string path = System.IO.Path.Combine(dir, id + ".jsonl");

            // The pid of the process writing this record. A record with no finish
            // line is either a job in progress or a process that died, and the LOG
            // cannot tell those apart - but the operating system can, if the record
            // says which process to ask about. The server checks this pid without
            // touching Revit, the same way it reads the rest of the file.
            int pid;
            try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch (InvalidOperationException) { pid = 0; }
            catch (PlatformNotSupportedException) { pid = 0; }

            _sink.Append(path, "{\"event\":\"start\",\"tool\":" + Str(tool) + ",\"pid\":" + pid + ",\"at\":" + Now() + "}");

            // Only now, once the line is on disk. An id is a promise that a record
            // exists; assigning it before the write is what let the promise be broken.
            Id = id;
            Path = path;
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
                _lastLabel = label;
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
            Result(payload, null);
        }

        /// <summary>
        /// The job's output PLUS what Revit raised while it ran.
        ///
        /// The synchronous path attaches revit_said - warnings, errors and cancelled
        /// modal dialogs - beside the payload in every reply (PipeEnvelope). The async
        /// path wrote only the payload, so the exact telemetry that diagnoses a batch
        /// failure ("Opening was canceled" was a DocWarnDialog the bridge cancelled)
        /// existed for a synchronous call and vanished for the same work submitted
        /// through run_async - which is how batches run. It is carried here so a
        /// job_status reader gets what a synchronous caller would have seen.
        ///
        /// <paramref name="revitSaidJson"/> is ALREADY-SERIALIZED, single-line JSON
        /// (the caller builds it exactly as PipeEnvelope does, so both paths report the
        /// same shape). It is embedded raw, not string-quoted; null or empty omits the
        /// field entirely, which reads as "Revit raised nothing", the same as a
        /// synchronous reply with no revit_said.
        /// </summary>
        public void Result(string payload, string revitSaidJson)
            => Result(payload, revitSaidJson, null, null, null);

        /// <summary>
        /// Async callers need every machine-readable sibling a synchronous pipe reply
        /// carries. The values are already serialized single-line JSON, just like
        /// revit_said, and are embedded raw into the append-only result event.
        /// </summary>
        public void Result(string payload, string revitSaidJson, string fallbackJson,
                           string capabilityGapsJson, string detailJson)
        {
            lock (_gate)
            {
                string said = string.IsNullOrEmpty(revitSaidJson)
                    ? ""
                    : ",\"revit_said\":" + revitSaidJson;
                string fallback = string.IsNullOrEmpty(fallbackJson) ? "" : ",\"fallback\":" + fallbackJson;
                string gaps = string.IsNullOrEmpty(capabilityGapsJson) ? "" : ",\"capability_gaps\":" + capabilityGapsJson;
                string detail = string.IsNullOrEmpty(detailJson) ? "" : ",\"detail\":" + detailJson;
                string inline = "{\"event\":\"result\",\"payload\":" + Str(payload ?? "") + said + fallback + gaps + detail +
                                ",\"at\":" + Now() + "}";
                if (!ReferenceEquals(_sink, FileJobSink.Instance) ||
                    Encoding.UTF8.GetByteCount(inline) <= MaxInlineResultRecordBytes)
                {
                    Append(inline);
                    return;
                }

                try
                {
                    JObject artifact = ResultArtifact(payload, revitSaidJson, fallbackJson,
                                                      capabilityGapsJson, detailJson);
                    byte[] bytes = new UTF8Encoding(false).GetBytes(artifact.ToString(Formatting.None));
                    if (bytes.Length > MaxExternalResultBytes)
                        throw new InvalidDataException("serialized async result exceeds the " +
                            MaxExternalResultBytes + " byte reply limit");
                    Directory.CreateDirectory(ResultsDir());
                    string fileName = Id + ".json";
                    string path = System.IO.Path.Combine(ResultsDir(), fileName);
                    string temp = System.IO.Path.Combine(ResultsDir(), "." + Id + "." +
                        Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        WriteDurableFile(temp, bytes);
                        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                        temp = null;
                    }
                    finally { if (temp != null) try { File.Delete(temp); } catch { } }

                    Append("{\"event\":\"result\",\"payload_ref\":" + Str(fileName) +
                           ",\"payload_bytes\":" + bytes.Length.ToString(CultureInfo.InvariantCulture) +
                           ",\"payload_sha256\":" + Str(Sha256(bytes)) + ",\"at\":" + Now() + "}");
                }
                catch (Exception ex)
                {
                    if (WriteFault == null) WriteFault = "external async result could not be persisted: " + ex.Message;
                    Log.Warn("Job " + (Id ?? "(no id)") + " could not persist its external result (" + ex.Message + ").");
                }
            }
        }

        private static JObject ResultArtifact(string payload, string revitSaidJson, string fallbackJson,
                                              string capabilityGapsJson, string detailJson)
        {
            var result = new JObject { ["payload"] = payload ?? "" };
            AddSerialized(result, "revit_said", revitSaidJson);
            AddSerialized(result, "fallback", fallbackJson);
            AddSerialized(result, "capability_gaps", capabilityGapsJson);
            AddSerialized(result, "detail", detailJson);
            return result;
        }

        private static void AddSerialized(JObject target, string name, string json)
        {
            if (!string.IsNullOrEmpty(json)) target[name] = JToken.Parse(json);
        }

        private static void WriteDurableFile(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        internal static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
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
            try
            {
                // A transient append failure followed by a successful finish used to
                // produce a clean-looking terminal record with a hole in it. Publish the
                // sticky fault before any later event. If even the marker cannot land,
                // do not append a misleading finish line after it.
                if (WriteFault != null && !_faultPublished)
                {
                    _sink.Append(Path, "{\"event\":\"record_fault\",\"error\":" + Str(WriteFault) +
                                      ",\"at\":" + Now() + "}");
                    _faultPublished = true;
                }
                _sink.Append(Path, line);
            }
            catch (Exception ex)
            {
                // NOT swallowed, and NOT thrown either. Throwing here would replace the
                // job's real outcome with its bookkeeping - Finish() runs on the failure
                // path too, and a command that failed for a good reason must not be
                // reported as having failed to write a log line.
                //
                // So the fault is kept instead. It is sticky: this is the write whose
                // absence explains the gap in the file, and a later success does not
                // fill that gap. RecordIsComplete goes false and stays false, which is
                // what a reader of a short record is entitled to be told.
                if (WriteFault == null) WriteFault = ex.Message;
                Log.Warn("Job " + (Id ?? "(no id)") + " could not append to its record (" + ex.Message +
                         "). The record is now INCOMPLETE; later events may be missing from " + (Path ?? "(no path)") + ".");
            }
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
