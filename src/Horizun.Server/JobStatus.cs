// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// "How is that long job going?" — answered WITHOUT touching Revit.
//
// This is the whole point of the design. While a long command runs, Revit's UI
// thread is inside it and the pipe is waiting for it to end: asking the plugin
// for progress is asking the thing that is busy. So the running script writes its
// checkpoints to a file, and this reads that file. Revit can be mid-transaction,
// frozen, or gone — the answer still comes back.
//
// It reports the SHAPE of what it found and nothing more. A job whose last
// checkpoint was four minutes ago is reported as exactly that, not as "stalled":
// the difference between a slow step and a hang is not visible from a log, and
// guessing which one it is would be inventing the one fact the caller actually
// wants. A DEAD process is different - the record carries the pid that claimed
// the job, asking the OS whether that pid still exists touches nothing, and
// "this job will never finish" is then a fact, not a guess. That check used to
// be the caller's problem: three crashes in one 31-model batch, each costing
// the minutes it took to leave the MCP and ask Windows by hand.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class JobStatus
    {
        /// <summary>
        /// The add-in's job directory, resolved by the function the add-in itself calls.
        /// This tool's whole value is reading those records without Revit in the loop,
        /// which requires both halves to mean the same folder by "jobs".
        /// </summary>
        private static string Dir() => Horizun.Revit.Core.HorizunPaths.JobsDir();

        internal static JObject Handle(JObject args)
        {
            string jobId = (string)args?["job_id"];
            int limit = (int?)args?["limit"] ?? 5;
            int tail = (int?)args?["checkpoints"] ?? 10;
            if (limit < 1) limit = 1;
            if (tail < 1) tail = 1;

            string dir = Dir();
            if (!Directory.Exists(dir))
                return new JObject
                {
                    ["jobs_dir"] = dir,
                    ["job_count"] = 0,
                    ["jobs"] = new JArray(),
                    ["note"] = "No job has been recorded on this machine yet. Long runs of horizun_execute_python " +
                               "create one automatically; call checkpoint(\"label\", done, total) inside the script " +
                               "to fill it in."
                };

            var files = new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (!string.IsNullOrEmpty(jobId))
            {
                files = files.Where(f => Path.GetFileNameWithoutExtension(f.Name) == jobId).ToList();
                if (files.Count == 0)
                    throw new FileNotFoundException("No job record with id '" + jobId + "' under " + dir + ".");
            }

            var jobs = new JArray();
            foreach (FileInfo f in files.Take(limit))
                jobs.Add(Describe(f, tail));

            return new JObject
            {
                ["jobs_dir"] = dir,
                ["job_count"] = files.Count,
                ["jobs"] = jobs,
                ["read_without_revit"] = true,
                ["note"] = "Read straight from disk. Revit may be busy inside the very command these describe - " +
                           "that is what this tool is for."
            };
        }

        private static JObject Describe(FileInfo f, int tail)
        {
            var checkpoints = new JArray();
            string tool = null, finished = null, finishNote = null;
            string lastAt = null, startedAt = null, runningAt = null, resultPayload = null;
            JToken revitSaid = null;
            int count = 0;
            int? pid = null;
            var recent = new List<JObject>();

            try
            {
                foreach (string line in File.ReadLines(f.FullName))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject o;
                    // A crash can leave a half-written last line. That is data, not a
                    // failure: skip it and say the record is truncated.
                    try { o = JObject.Parse(line); } catch { continue; }

                    string ev = (string)o["event"];
                    if (ev == "start")
                    {
                        tool = (string)o["tool"]; startedAt = (string)o["at"];
                        // The pid of the Revit that opened this record. Absent in records
                        // written before it was stamped; a 0 means the writer could not
                        // read its own pid, and both are reported as "not knowable".
                        try { int p = o["pid"] != null ? (int)o["pid"] : 0; if (p > 0) pid = p; } catch { }
                    }
                    // The moment the UI thread picked it up. Its ABSENCE is the whole
                    // point: a record with neither this nor a finish line is a job that
                    // is still waiting for a turn, which used to be indistinguishable
                    // from one that was running and from one whose process had died.
                    else if (ev == "running") { runningAt = (string)o["at"]; lastAt = (string)o["at"]; }
                    // The OUTPUT of an async run. Its caller got a job_id and went away,
                    // so this record is the only place the answer exists - "finished, ok"
                    // with no output is not an answer to anything.
                    // The OUTPUT, and beside it what Revit raised while the job ran. The
                    // sync path carries revit_said as a sibling of data in every reply;
                    // the async record now stores it on the result event, so a job_status
                    // reader gets the warnings, errors and cancelled dialogs a synchronous
                    // caller would have seen. Absent = Revit raised nothing, same as sync.
                    // DeepClone: the token belongs to this line's parsed object, and it
                    // has to outlive it in the job we return without dragging a second
                    // parent along.
                    else if (ev == "result") { resultPayload = (string)o["payload"]; revitSaid = o["revit_said"]?.DeepClone(); lastAt = (string)o["at"]; }
                    else if (ev == "finish") { finished = (string)o["status"]; finishNote = (string)o["note"]; lastAt = (string)o["at"]; }
                    else if (ev == "checkpoint")
                    {
                        count++;
                        lastAt = (string)o["at"];
                        recent.Add(o);
                        if (recent.Count > tail) recent.RemoveAt(0);
                    }
                }
            }
            catch { }

            foreach (JObject o in recent) checkpoints.Add(o);

            double? sinceLast = null;
            try
            {
                DateTime t;
                if (lastAt != null && DateTime.TryParseExact(lastAt, "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    sinceLast = Math.Round((DateTime.Now - t).TotalSeconds, 1);
            }
            catch { }

            bool open = finished == null;

            // FIVE STATES, not two.
            //
            // This used to report `finished` and, when it was false, one sentence
            // covering "still running or the process died". That refusal to guess was
            // right about the case it was written for and wrong about two others: a job
            // WAITING FOR A TURN is knowable exactly, and so is one Revit declined to
            // schedule or shut down before starting. Reporting those as the same
            // ambiguity threw away facts the system had.
            //
            //   queued       opened, never picked up by the UI thread
            //   running      picked up, no finish line yet (or died - see the note)
            //   ok           finished and said so
            //   failed       ran and failed
            //   not_started  Revit refused to schedule it, or shut down first. NEVER RAN.
            string state;
            if (finished == null) state = runningAt == null ? "queued" : "running";
            else if (finished == "ok" || finished == "failed" || finished == "not_started") state = finished;
            else state = finished;   // an unrecognised status is reported verbatim, never mapped to a guess

            // Is the process that claimed this job still alive? Only asked for a job
            // with no finish line - for a finished one the record already answers
            // everything - and only when the record says which pid to ask about.
            // Measured 2026-07-31, 31 models from ACC: Revit crashed three times
            // mid-batch and all three showed "running... or the process died" for as
            // long as it took to leave the MCP and ask Windows by hand. Checking the
            // pid is an OS call that never touches Revit, exactly like reading the log.
            bool? processAlive = null;
            if (finished == null && pid.HasValue)
                processAlive = PipeClient.IsRevitAlive(pid.Value);

            return new JObject

            {
                ["job_id"] = Path.GetFileNameWithoutExtension(f.Name),
                ["tool"] = tool,
                // queued | running | ok | failed | not_started
                ["state"] = state,
                // The pid of the Revit that opened the record, when the record carries
                // it, and whether that process is still alive. Null pid: the record
                // predates pid stamping and liveness is genuinely not knowable from it.
                // Null process_alive on a finished job: nobody needs to know.
                ["pid"] = pid.HasValue ? (JToken)pid.Value : JValue.CreateNull(),
                ["process_alive"] = processAlive.HasValue ? (JToken)processAlive.Value : JValue.CreateNull(),
                ["started_at"] = startedAt,
                ["running_since"] = runningAt,
                ["checkpoint_count"] = count,
                ["last_event_at"] = lastAt,
                ["seconds_since_last_event"] = sinceLast.HasValue ? (JToken)sinceLast.Value : JValue.CreateNull(),
                ["finished"] = !open,
                ["final_status"] = finished,
                ["final_note"] = finishNote,
                // For an async run this IS the answer. Parsed back into JSON when it is
                // JSON, handed over as text when it is not - never dropped because it did
                // not parse, and never described as absent when it is merely unparseable.
                ["result"] = ParseResult(resultPayload),
                ["result_present"] = resultPayload != null,
                // What Revit raised while the job ran - the sibling of the payload the
                // sync path always carries. Null here means the job recorded none (Revit
                // raised nothing, or the record predates this field): NOT that it was
                // dropped, which is the exact confusion 5.21 removed on the async path.
                ["revit_said"] = revitSaid ?? JValue.CreateNull(),
                ["recent_checkpoints"] = checkpoints,
                ["what_this_means"] = Explain(state, resultPayload != null, pid, processAlive)
            };
        }

        /// <summary>
        /// One sentence per state, and the ambiguity kept ONLY where it is real.
        ///
        /// An open record used to be reported as "running, or the process died" even
        /// though the record carries the pid and the OS knows whether that pid exists.
        /// With the process ALIVE the real ambiguity remains - a slow step and a hang
        /// look the same from a log. With the process DEAD there is no ambiguity at
        /// all: the job will never finish, and saying so is a fact, not a guess. Only
        /// a record with no pid (written before it was stamped) keeps the old sentence,
        /// because there liveness genuinely is not knowable.
        /// </summary>
        private static string Explain(string state, bool hasResult, int? pid, bool? processAlive)
        {
            switch (state)
            {
                case "queued":
                    if (processAlive == false)
                        return "It will NEVER RUN. The record was opened, the UI thread never picked it up, and " +
                               "the Revit that queued it (pid " + pid + ") is no longer running - there is no " +
                               "process left to give it a turn. Nothing was executed and nothing was written, so " +
                               "it is safe to send again once a Revit is up. Use a NEW idempotency_key: keys are " +
                               "bound to the Revit process that issued them.";
                    return "QUEUED and not started. The record was opened and the UI thread has not picked it up " +
                           "yet - entries run one at a time, so it is waiting behind whatever is in front of it. " +
                           "Nothing has been executed and nothing has been written. Do NOT re-send it; a retry " +
                           "with the same idempotency_key returns this same job_id and queues nothing.";

                case "running":
                    if (processAlive == true)
                        return "RUNNING, or hung - the Revit that is executing it (pid " + pid + ") is alive, so " +
                               "the process did not die. A log cannot tell a slow step from a hang, and this will " +
                               "not guess; seconds_since_last_event is how long it has been quiet.";
                    if (processAlive == false)
                        return "The PROCESS DIED. The Revit that was executing it (pid " + pid + ") no longer " +
                               "exists, so this job will NEVER finish and no finish record is coming - that is a " +
                               "fact, not a guess. Everything checkpointed before the crash HAS HAPPENED and is " +
                               "in the record; re-running the script on top of it is a second write, not a " +
                               "recovery.";
                    return "RUNNING, or the process died. It was picked up by Revit's UI thread and there is no " +
                           "finish record - this record predates pid stamping, so a log is all there is, and a " +
                           "log cannot tell those two apart. This will not guess. Check " +
                           "whether Revit is alive; seconds_since_last_event is how long it has been quiet.";

                case "ok":
                    return hasResult
                        ? "It finished and said so, and its output is in 'result'."
                        : "It finished and said so. There is no 'result' record: a synchronous run returns its " +
                          "output over the wire instead, so only run_async jobs carry one here.";

                case "failed":
                    return "It RAN and FAILED - read final_note. Whatever it did before failing has happened; a " +
                           "failure is not a rollback.";

                case "not_started":
                    return "It NEVER RAN. Revit refused to schedule it, or shut down before its turn came, or the " +
                           "queue was full - read final_note for which. Nothing was executed and nothing was " +
                           "written, so this is safe to send again. Use a NEW idempotency_key: keys are bound to " +
                           "the Revit process that issued them.";

                default:
                    return "The record carries a final status of '" + state + "', which this tool does not " +
                           "recognise. It is reported verbatim rather than mapped onto one it does.";
            }
        }

        /// <summary>
        /// The stored payload as JSON when it is JSON, as text when it is not.
        ///
        /// A result that fails to parse is still the result. Returning null for it would
        /// report an answer that exists as an answer that does not, which is the whole
        /// class of error this file is written against.
        /// </summary>
        private static JToken ParseResult(string payload)
        {
            if (payload == null) return JValue.CreateNull();
            if (payload.Length == 0) return JValue.CreateNull();
            try { return JToken.Parse(payload); }
            catch
            {
                return new JObject
                {
                    ["unparsed_text"] = payload,
                    ["note"] = "The job recorded a result that is not valid JSON. It is handed over verbatim rather " +
                               "than dropped - it is still what the script produced."
                };
            }
        }
    }
}
