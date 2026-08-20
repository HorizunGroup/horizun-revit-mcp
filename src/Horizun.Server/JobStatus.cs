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
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class JobStatus
    {
        internal const int MaxJobsPerCall = 100;
        internal const int MaxCheckpointsPerJob = 1000;
        internal const int MaxRecordBytes = 64 * 1024;
        internal const int MaxCheckpointBytesPerJob = 512 * 1024;
        internal const int MaxResponseBytes = 4 * 1024 * 1024;
        internal const int MaxExternalResultBytes = Horizun.Contracts.Contract.MaxAsyncResultBytes;
        /// <summary>
        /// The add-in's job directory, resolved by the function the add-in itself calls.
        /// This tool's whole value is reading those records without Revit in the loop,
        /// which requires both halves to mean the same folder by "jobs".
        /// </summary>
        private static string Dir() => Horizun.Revit.Core.HorizunPaths.JobsDir();

        internal static JObject Handle(JObject args) => Handle(args, CancellationToken.None);

        internal static JObject Handle(JObject args, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string jobId = (string)args?["job_id"];
            int responseBudget = string.IsNullOrEmpty(jobId)
                ? MaxResponseBytes
                : Horizun.Contracts.Contract.MaxReplyBytes;
            int limit = (int?)args?["limit"] ?? 5;
            int tail = (int?)args?["checkpoints"] ?? 10;
            if (limit < 1) limit = 1;
            if (tail < 1) tail = 1;
            if (limit > MaxJobsPerCall) limit = MaxJobsPerCall;
            if (tail > MaxCheckpointsPerJob) tail = MaxCheckpointsPerJob;

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

            // Enumerate all names to keep job_count exact, but retain at most `limit`
            // FileInfo objects. OrderBy().ToList() used to materialize the entire durable
            // store before the response cap could help.
            var files = new List<FileInfo>(limit);
            int matchingCount = 0;
            foreach (FileInfo candidate in new DirectoryInfo(dir).EnumerateFiles("*.jsonl"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(jobId) && Path.GetFileNameWithoutExtension(candidate.Name) != jobId)
                    continue;

                matchingCount++;
                int at = files.FindIndex(f => candidate.LastWriteTimeUtc > f.LastWriteTimeUtc);
                if (at < 0) files.Add(candidate); else files.Insert(at, candidate);
                if (files.Count > limit) files.RemoveAt(files.Count - 1);
            }
            if (!string.IsNullOrEmpty(jobId) && matchingCount == 0)
                throw new FileNotFoundException("No job record with id '" + jobId + "' under " + dir + ".");

            var jobs = new JArray();
            int jobsOmittedForBudget = 0;
            long jobsBytes = 0;
            // Keep fixed response metadata out of the jobs budget. The final exact-size
            // pass below is authoritative; this reserve prevents normal paths from
            // repeatedly building and removing the last job.
            const int metadataReserveBytes = 32 * 1024;
            foreach (FileInfo f in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JObject described = Describe(f, tail, cancellationToken,
                    string.IsNullOrEmpty(jobId) ? 0 : MaxExternalResultBytes);
                int describedBytes = Encoding.UTF8.GetByteCount(described.ToString(Newtonsoft.Json.Formatting.None));
                if (describedBytes > responseBudget - metadataReserveBytes - jobsBytes)
                {
                    jobsOmittedForBudget++;
                    continue;
                }
                jobs.Add(described);
                jobsBytes += describedBytes;
            }

            var response = new JObject
            {
                ["jobs_dir"] = dir,
                ["job_count"] = matchingCount,
                ["jobs"] = jobs,
                ["jobs_returned"] = jobs.Count,
                ["jobs_omitted_for_response_budget"] = jobsOmittedForBudget,
                ["response_truncated"] = jobsOmittedForBudget > 0,
                ["read_without_revit"] = true,
                ["limits"] = new JObject
                {
                    ["jobs_per_call"] = MaxJobsPerCall,
                    ["checkpoints_per_job"] = MaxCheckpointsPerJob,
                    ["record_bytes"] = MaxRecordBytes,
                    ["checkpoint_bytes_per_job"] = MaxCheckpointBytesPerJob,
                    ["response_bytes"] = responseBudget,
                    ["list_response_bytes"] = MaxResponseBytes,
                    ["external_result_bytes"] = MaxExternalResultBytes
                },
                ["note"] = "Read straight from disk. Revit may be busy inside the very command these describe - " +
                           "that is what this tool is for."
            };

            // Exact UTF-8 budget, including metadata and JSON escaping. Remove complete
            // job objects rather than slicing serialized JSON, so the response is always
            // valid JSON and every omission is counted explicitly.
            while (Encoding.UTF8.GetByteCount(response.ToString(Newtonsoft.Json.Formatting.None)) > responseBudget &&
                   jobs.Count > 0)
            {
                jobs.RemoveAt(jobs.Count - 1);
                jobsOmittedForBudget++;
                response["jobs_returned"] = jobs.Count;
                response["jobs_omitted_for_response_budget"] = jobsOmittedForBudget;
                response["response_truncated"] = true;
            }
            return response;
        }

        /// <summary>
        /// MCP Tasks already possess the unguessable job id and need the exact underlying
        /// tool result, not the 4 MiB human status-list budget. This path still has the
        /// transport's 32 MiB hard cap and reads one exact file only.
        /// </summary>
        internal static JObject ReadForTask(string jobId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(jobId) || jobId != Path.GetFileName(jobId) ||
                jobId.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
                throw new InvalidDataException("invalid durable job id");
            string path = Path.Combine(Dir(), jobId + ".jsonl");
            if (!File.Exists(path)) return null;
            return Describe(new FileInfo(path), 10, cancellationToken, MaxExternalResultBytes);
        }

        private static JObject Describe(FileInfo f, int tail, CancellationToken cancellationToken,
                                        int externalResultLimit)
        {
            var checkpoints = new JArray();
            string tool = null, finished = null, finishNote = null;
            string lastAt = null, startedAt = null, runningAt = null, resultPayload = null;
            string recordFault = null;
            JToken revitSaid = null, fallback = null, capabilityGaps = null, detail = null;
            int count = 0;
            int? pid = null;
            var recent = new List<JObject>();
            var recentBytes = new List<int>();
            int recentByteCount = 0;
            int oversizedRecords = 0;
            int invalidRecords = 0;
            int semanticInvalidRecords = 0;
            int checkpointsOmittedForByteBudget = 0;
            string readError = null;
            bool resultExternal = false, resultOmittedForBudget = false;
            string resultRef = null;
            long? resultBytes = null;
            bool seenStart = false, seenRunning = false, seenResult = false, terminalEventSeen = false;

            try
            {
                foreach (BoundedRecord record in ReadBoundedRecords(f.FullName, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record.Oversized)
                    {
                        oversizedRecords++;
                        continue;
                    }
                    string line = record.Text;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject o;
                    // A crash can leave a half-written last line. That is data, not a
                    // failure: skip it and say the record is truncated.
                    try { o = JObject.Parse(line); }
                    catch { invalidRecords++; continue; }

                    string ev = o["event"]?.Type == JTokenType.String ? (string)o["event"] : null;
                    string eventAt = o["at"]?.Type == JTokenType.String ? (string)o["at"] : null;
                    bool timestampValid = IsJobTimestamp(eventAt);
                    if (string.IsNullOrWhiteSpace(ev) || !timestampValid || terminalEventSeen)
                    {
                        semanticInvalidRecords++;
                        if (ev == "finish") terminalEventSeen = true;
                        continue;
                    }
                    if (ev == "start")
                    {
                        string candidateTool = o["tool"]?.Type == JTokenType.String ? (string)o["tool"] : null;
                        if (seenStart || seenRunning || seenResult || string.IsNullOrWhiteSpace(candidateTool))
                        { semanticInvalidRecords++; continue; }
                        seenStart = true;
                        tool = candidateTool; startedAt = eventAt;
                        // The pid of the Revit that opened this record. Absent in records
                        // written before it was stamped; a 0 means the writer could not
                        // read its own pid, and both are reported as "not knowable".
                        try
                        {
                            int p = o["pid"] != null ? (int)o["pid"] : 0;
                            if (p < 0) { semanticInvalidRecords++; continue; }
                            if (p > 0) pid = p;
                        }
                        catch { semanticInvalidRecords++; }
                    }
                    // The moment the UI thread picked it up. Its ABSENCE is the whole
                    // point: a record with neither this nor a finish line is a job that
                    // is still waiting for a turn, which used to be indistinguishable
                    // from one that was running and from one whose process had died.
                    else if (ev == "running")
                    {
                        if (!seenStart || seenRunning || seenResult)
                        { semanticInvalidRecords++; continue; }
                        seenRunning = true; runningAt = eventAt; lastAt = eventAt;
                    }
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
                    else if (ev == "result")
                    {
                        if (!seenStart || !seenRunning || seenResult)
                        { semanticInvalidRecords++; continue; }
                        bool inline = o["payload"]?.Type == JTokenType.String;
                        bool external = o["payload_ref"]?.Type == JTokenType.String;
                        if (inline == external ||
                            o["revit_said"] != null && o["revit_said"].Type != JTokenType.Object ||
                            o["fallback"] != null && o["fallback"].Type != JTokenType.Object ||
                            o["capability_gaps"] != null && o["capability_gaps"].Type != JTokenType.Array ||
                            o["detail"] != null && o["detail"].Type != JTokenType.Object)
                        { semanticInvalidRecords++; continue; }
                        seenResult = true;
                        if (inline)
                        {
                            resultPayload = (string)o["payload"];
                            revitSaid = o["revit_said"]?.DeepClone();
                            fallback = o["fallback"]?.DeepClone();
                            capabilityGaps = o["capability_gaps"]?.DeepClone();
                            detail = o["detail"]?.DeepClone();
                        }
                        else
                        {
                            resultExternal = true;
                            resultRef = (string)o["payload_ref"];
                            long declaredBytes = 0;
                            string declaredHash = o["payload_sha256"]?.Type == JTokenType.String
                                ? (string)o["payload_sha256"] : null;
                            bool metadataValid = o["payload_bytes"]?.Type == JTokenType.Integer &&
                                long.TryParse(o["payload_bytes"].ToString(), NumberStyles.None,
                                    CultureInfo.InvariantCulture, out declaredBytes) && declaredBytes > 0 &&
                                declaredBytes <= MaxExternalResultBytes && IsSha256(declaredHash) &&
                                resultRef == Path.GetFileNameWithoutExtension(f.Name) + ".json";
                            if (!metadataValid)
                            {
                                semanticInvalidRecords++;
                            }
                            else
                            {
                                resultBytes = declaredBytes;
                                string resultPath = Path.Combine(Dir(), "results", resultRef);
                                try
                                {
                                    if (externalResultLimit > 0 && declaredBytes <= externalResultLimit)
                                    {
                                        byte[] bytes = ReadExactBounded(resultPath, declaredBytes, MaxExternalResultBytes);
                                        if (!string.Equals(Sha256(bytes), declaredHash, StringComparison.OrdinalIgnoreCase))
                                            throw new InvalidDataException("external result hash does not match its job record");
                                        JObject artifact = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
                                        if (artifact["payload"]?.Type != JTokenType.String ||
                                            artifact["revit_said"] != null && artifact["revit_said"].Type != JTokenType.Object ||
                                            artifact["fallback"] != null && artifact["fallback"].Type != JTokenType.Object ||
                                            artifact["capability_gaps"] != null && artifact["capability_gaps"].Type != JTokenType.Array ||
                                            artifact["detail"] != null && artifact["detail"].Type != JTokenType.Object)
                                            throw new InvalidDataException("external result artifact has an invalid schema");
                                        resultPayload = (string)artifact["payload"];
                                        revitSaid = artifact["revit_said"]?.DeepClone();
                                        fallback = artifact["fallback"]?.DeepClone();
                                        capabilityGaps = artifact["capability_gaps"]?.DeepClone();
                                        detail = artifact["detail"]?.DeepClone();
                                    }
                                    else
                                    {
                                        var info = new FileInfo(resultPath);
                                        if (!info.Exists || info.Length != declaredBytes)
                                            throw new InvalidDataException("external result artifact is missing or has the wrong length");
                                        resultOmittedForBudget = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    readError = "external result is unreadable: " + ex.Message;
                                }
                            }
                        }
                        lastAt = eventAt;
                    }
                    else if (ev == "record_fault")
                    {
                        recordFault = (string)o["error"] ?? "unknown append failure"; lastAt = eventAt;
                    }
                    else if (ev == "finish")
                    {
                        terminalEventSeen = true;
                        string candidateStatus = o["status"]?.Type == JTokenType.String ? (string)o["status"] : null;
                        bool knownStatus = candidateStatus == "ok" || candidateStatus == "failed" || candidateStatus == "not_started";
                        bool validSequence = seenStart &&
                            (candidateStatus == "not_started" ? !seenRunning && !seenResult : seenRunning);
                        int declaredCheckpoints;
                        bool checkpointCountValid = o["checkpoints"]?.Type == JTokenType.Integer &&
                            int.TryParse(o["checkpoints"].ToString(), out declaredCheckpoints) &&
                            declaredCheckpoints == count;
                        bool noteValid = o["note"] == null || o["note"].Type == JTokenType.Null ||
                                         o["note"].Type == JTokenType.String;
                        if (!knownStatus || !validSequence || !checkpointCountValid || !noteValid)
                        { semanticInvalidRecords++; continue; }
                        finished = candidateStatus; finishNote = (string)o["note"]; lastAt = eventAt;
                    }
                    else if (ev == "checkpoint")
                    {
                        // A checkpoint proves that execution began. Accepting it while
                        // still "queued" would later advise a dead caller that no work
                        // ran and a retry is safe. It also cannot follow the terminal
                        // result event in the append-only lifecycle.
                        if (!seenStart || !seenRunning || seenResult ||
                            o["n"]?.Type != JTokenType.Integer || (int)o["n"] != count + 1 ||
                            o["label"]?.Type != JTokenType.String ||
                            !IsOptionalJobNumber(o["done"]) || !IsOptionalJobNumber(o["total"]))
                        { semanticInvalidRecords++; continue; }
                        count++;
                        lastAt = eventAt;
                        int checkpointBytes = Encoding.UTF8.GetByteCount(o.ToString(Newtonsoft.Json.Formatting.None));
                        recent.Add(o);
                        recentBytes.Add(checkpointBytes);
                        recentByteCount += checkpointBytes;
                        if (recent.Count > tail)
                        {
                            recentByteCount -= recentBytes[0];
                            recentBytes.RemoveAt(0);
                            recent.RemoveAt(0);
                        }
                        while (recentByteCount > MaxCheckpointBytesPerJob && recent.Count > 0)
                        {
                            recentByteCount -= recentBytes[0];
                            recentBytes.RemoveAt(0);
                            recent.RemoveAt(0);
                            checkpointsOmittedForByteBudget++;
                        }
                    }
                    else semanticInvalidRecords++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A file selected during enumeration can disappear, become locked, or
                // become unreadable before/during the scan. That is not a quiet empty
                // record. Preserve partial facts, but mark the record incomplete and
                // publish exactly why the durable source could not be fully read.
                readError = ex.GetType().Name + ": " + ex.Message;
            }

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

            bool open = !terminalEventSeen;

            // SIX STATES, not two. record_incomplete is the fail-closed state for a
            // terminal record known to have lost at least one earlier append.
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
            //   not_started      Revit refused to schedule it, or shut down first. NEVER RAN.
            //   record_incomplete a later finish exists but an earlier append is known missing
            string state;
            if (recordFault != null || readError != null) state = "record_incomplete";
            else if (oversizedRecords > 0 || invalidRecords > 0 || semanticInvalidRecords > 0) state = "record_truncated";
            else if (finished == null) state = runningAt == null ? "queued" : "running";
            else state = finished;

            // Is the process that claimed this job still alive? Only asked for a job
            // with no finish line - for a finished one the record already answers
            // everything - and only when the record says which pid to ask about.
            // Measured 2026-07-31, 31 models from ACC: Revit crashed three times
            // mid-batch and all three showed "running... or the process died" for as
            // long as it took to leave the MCP and ask Windows by hand. Checking the
            // pid is an OS call that never touches Revit, exactly like reading the log.
            bool? processAlive = null;
            if (!terminalEventSeen && pid.HasValue)
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
                ["record_complete"] = recordFault == null && readError == null &&
                                      oversizedRecords == 0 && invalidRecords == 0 && semanticInvalidRecords == 0,
                ["record_fault"] = recordFault == null ? JValue.CreateNull() : new JValue(recordFault),
                ["read_error"] = readError == null ? JValue.CreateNull() : new JValue(readError),
                ["oversized_records_omitted"] = oversizedRecords,
                ["invalid_records_omitted"] = invalidRecords,
                ["semantic_invalid_records_omitted"] = semanticInvalidRecords,
                ["checkpoints_omitted_for_byte_budget"] = checkpointsOmittedForByteBudget,
                ["record_truncated"] = oversizedRecords > 0 || invalidRecords > 0 || semanticInvalidRecords > 0,
                // For an async run this IS the answer. Parsed back into JSON when it is
                // JSON, handed over as text when it is not - never dropped because it did
                // not parse, and never described as absent when it is merely unparseable.
                ["result"] = ParseResult(resultPayload),
                ["result_present"] = seenResult,
                ["result_external"] = resultExternal,
                ["result_ref"] = resultRef == null ? JValue.CreateNull() : new JValue(resultRef),
                ["result_bytes"] = resultBytes.HasValue ? (JToken)resultBytes.Value : JValue.CreateNull(),
                ["result_omitted_for_response_budget"] = resultOmittedForBudget,
                // What Revit raised while the job ran - the sibling of the payload the
                // sync path always carries. Null here means the job recorded none (Revit
                // raised nothing, or the record predates this field): NOT that it was
                // dropped, which is the exact confusion 5.21 removed on the async path.
                ["revit_said"] = revitSaid ?? JValue.CreateNull(),
                ["fallback"] = fallback ?? JValue.CreateNull(),
                ["capability_gaps"] = capabilityGaps ?? JValue.CreateNull(),
                ["detail"] = detail ?? JValue.CreateNull(),
                ["recent_checkpoints"] = checkpoints,
                ["what_this_means"] = Explain(state, seenResult, pid, processAlive)
            };
        }

        private static bool IsJobTimestamp(string value)
        {
            DateTime parsed;
            return value != null && DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
        }

        private static bool IsOptionalJobNumber(JToken token)
            => token != null && (token.Type == JTokenType.Null ||
                                 token.Type == JTokenType.Integer ||
                                 token.Type == JTokenType.Float);

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

                case "record_incomplete":
                    return "This durable record is INCOMPLETE: read record_fault and read_error for whether an append " +
                           "failed or the file could not be fully read. Its model or external effect may have happened " +
                           "while result/checkpoint evidence is missing. Even when final_status is present, do not " +
                           "treat it as a complete answer and do not retry until the destination is inspected.";

                case "record_truncated":
                    return "One or more job-record lines were invalid or exceeded the per-record byte limit and " +
                           "were omitted. The visible fields are valid JSON but are NOT a complete account of the " +
                           "job; do not infer that an absent result or finish event never existed.";

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

        private sealed class BoundedRecord
        {
            public string Text;
            public bool Oversized;
        }

        /// <summary>
        /// Stream JSONL without ever materializing more than MaxRecordBytes for one line.
        /// Oversized lines are drained to the next newline and represented explicitly.
        /// </summary>
        private static IEnumerable<BoundedRecord> ReadBoundedRecords(
            string path, CancellationToken cancellationToken)
        {
            byte[] readBuffer = new byte[16 * 1024];
            byte[] lineBuffer = new byte[MaxRecordBytes];
            int lineLength = 0;
            bool oversized = false;
            var utf8 = new UTF8Encoding(false, true);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
            {
                int read;
                while ((read = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int i = 0; i < read; i++)
                    {
                        byte b = readBuffer[i];
                        if (b == (byte)'\n')
                        {
                            yield return Decode(lineBuffer, lineLength, oversized, utf8);
                            lineLength = 0;
                            oversized = false;
                            continue;
                        }
                        if (!oversized)
                        {
                            if (lineLength < MaxRecordBytes) lineBuffer[lineLength++] = b;
                            else oversized = true;
                        }
                    }
                }
            }

            if (lineLength > 0 || oversized)
                yield return Decode(lineBuffer, lineLength, oversized, utf8);
        }

        private static BoundedRecord Decode(byte[] bytes, int count, bool oversized, Encoding utf8)
        {
            if (oversized) return new BoundedRecord { Oversized = true };
            if (count > 0 && bytes[count - 1] == (byte)'\r') count--;
            try
            {
                string text = utf8.GetString(bytes, 0, count);
                if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                return new BoundedRecord { Text = text };
            }
            catch
            {
                return new BoundedRecord { Text = "\u0000" };
            }
        }

        private static byte[] ReadExactBounded(string path, long expected, long max)
        {
            if (expected <= 0 || expected > max || expected > int.MaxValue)
                throw new InvalidDataException("external result exceeds its bounded size");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.Read | FileShare.Delete))
            {
                if (stream.Length != expected) throw new InvalidDataException("external result length does not match its record");
                var bytes = new byte[(int)expected];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("external result ended while it was read");
                    offset += read;
                }
                if (stream.ReadByte() != -1 || stream.Length != expected)
                    throw new InvalidDataException("external result changed while it was read");
                return bytes;
            }
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var text = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
    }
}
