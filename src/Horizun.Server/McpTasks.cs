// -----------------------------------------------------------------------------
// Horizun MCP server — MCP Tasks (2025-11-25), backed by the existing durable
// Revit job record rather than a second execution queue.
//
// The sidecar records MCP identity/TTL/original request; the Revit job remains the
// authority for whether work is queued, running, complete or incomplete. Creation is
// two-phase and durable before submission. If the server dies in the narrow interval
// between enqueue and acknowledgement, tasks/result safely replays the SUBMISSION with
// the same durable key and receives the original job id instead of queueing twice.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpTasks
    {
        internal const long DefaultTtlMs = 24L * 60 * 60 * 1000;
        internal const long MinTtlMs = 60L * 1000;
        internal const long MaxTtlMs = Horizun.Contracts.Contract.MaxTaskTtlMilliseconds;
        internal const int PollIntervalMs = 1000;
        internal const int PageSize = 50;
        internal const int MaxRetainedTasks = 256;
        internal const long MaxSidecarBytes = 8L * 1024 * 1024;
        internal const long MaxTaskResultBytes = Horizun.Contracts.Contract.MaxReplyBytes;
        internal const long MaxTaskStoreBytes = 256L * 1024 * 1024;

        private static readonly object StorageGate = new object();

        private static string Dir() => Path.Combine(Horizun.Revit.Core.HorizunPaths.DataRoot(), "mcp-tasks");

        public static bool Supports(ToolDef def) =>
            def != null && def.Host == null &&
            def.Name != "horizun_submit_job" && def.Name != "horizun_execute_python" &&
            def.Name != "horizun_request_python_access";

        public static JObject Create(
            JObject toolCall, Func<JObject, CancellationToken, JToken> invokeTool, CancellationToken cancellationToken)
        {
            JToken taskToken = toolCall?["task"];
            if (taskToken == null || taskToken.Type != JTokenType.Object)
                throw new McpError(-32602, "Invalid params: task augmentation requires an object 'task'.");
            string tool = RequiredString(toolCall, "name");
            ToolDef def = Tools.Find(tool);
            if (!Supports(def))
                throw new McpError(-32602,
                    "'" + tool + "' does not support MCP task augmentation. Host-resident tools, execute_python, " +
                    "request_python_access and submit_job itself must be called normally.");
            JToken argumentsToken = toolCall?["arguments"];
            if (argumentsToken != null && argumentsToken.Type != JTokenType.Object &&
                argumentsToken.Type != JTokenType.Null)
                throw new McpError(-32602, "Invalid params: task-augmented tool arguments must be an object.");

            long ttl = Ttl((JObject)taskToken);
            string taskId = Guid.NewGuid().ToString("N");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var record = new JObject
            {
                ["schema"] = 1,
                ["task_id"] = taskId,
                ["tool"] = tool,
                ["arguments"] = (argumentsToken as JObject)?.DeepClone() ?? new JObject(),
                ["submit_idempotency_key"] = "mcp-task-submit-" + taskId,
                ["job_id"] = JValue.CreateNull(),
                ["status"] = "working",
                ["status_message"] = "The task is being durably admitted to Revit's asynchronous queue.",
                ["created_at"] = now.ToString("O"),
                ["last_updated_at"] = now.ToString("O"),
                ["ttl"] = ttl
            };
            AdmitAndWrite(record);

            Submit(record, invokeTool, cancellationToken);
            JObject task = Describe(record);
            return new JObject
            {
                ["task"] = task,
                ["_meta"] = new JObject
                {
                    ["io.modelcontextprotocol/model-immediate-response"] =
                        "Horizun accepted task " + taskId + ". Poll tasks/get; retrieve the final tool result with tasks/result.",
                    ["io.modelcontextprotocol/related-task"] = new JObject { ["taskId"] = taskId }
                }
            };
        }

        public static JObject Get(JObject prms)
        {
            JObject record = ReadRequired(TaskId(prms));
            EnsureNotExpired(record);
            return Describe(record);
        }

        public static JObject List(JObject prms)
        {
            int offset = DecodeCursor(prms?["cursor"]);
            if (!Directory.Exists(Dir())) return new JObject { ["tasks"] = new JArray() };

            var files = new DirectoryInfo(Dir()).EnumerateFiles("*.json").Where(IsTaskSidecar)
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            if (offset < 0 || offset > files.Count)
                throw new McpError(-32602, "Invalid tasks/list cursor.");

            var tasks = new JArray();
            int index = offset;
            for (; index < files.Count && tasks.Count < PageSize; index++)
            {
                JObject record;
                try { record = JObject.Parse(ReadBoundedText(files[index].FullName, MaxSidecarBytes)); }
                catch { continue; }
                if (Expired(record)) continue;
                try { tasks.Add(Describe(record)); }
                catch { /* one corrupt sidecar cannot hide every other task */ }
            }

            var result = new JObject { ["tasks"] = tasks };
            if (index < files.Count) result["nextCursor"] = EncodeCursor(index);
            return result;
        }

        public static JObject WaitResult(
            JObject prms, Func<JObject, CancellationToken, JToken> invokeTool, CancellationToken cancellationToken)
        {
            string taskId = TaskId(prms);
            JObject record = ReadRequired(taskId);
            EnsureNotExpired(record);

            // Recover the two-phase submission. The wrapper idempotency key makes this
            // a replay if Revit accepted it before the acknowledgement was lost.
            if (((string)record["status"] == "working") &&
                (record["job_id"] == null || record["job_id"].Type == JTokenType.Null) &&
                ReadResultSnapshot(taskId) == null)
                Submit(record, invokeTool, cancellationToken);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                record = ReadRequired(taskId);
                JObject task = Describe(record);
                string status = (string)task["status"];
                if (status == "completed" || status == "failed" || status == "cancelled")
                    return Result(record, taskId);
                Thread.Sleep(250);
            }
        }

        private static void Submit(
            JObject record, Func<JObject, CancellationToken, JToken> invokeTool, CancellationToken cancellationToken)
        {
            string taskId = (string)record["task_id"];
            using (AcquireTaskMutex(taskId, cancellationToken))
            {
                JObject latest = ReadRequired(taskId);
                if (latest["job_id"] != null && latest["job_id"].Type == JTokenType.String)
                {
                    Replace(record, latest);
                    return;
                }

                var submit = new JObject
                {
                    ["name"] = "horizun_submit_job",
                    ["arguments"] = new JObject
                    {
                        ["tool"] = latest["tool"],
                        ["arguments"] = latest["arguments"]?.DeepClone() ?? new JObject(),
                        ["idempotency_key"] = latest["submit_idempotency_key"],
                        // Revit persists this lease before queueing the child. Its own
                        // configurable job retention therefore cannot delete the only
                        // result source while the MCP task TTL is still active.
                        ["retain_until_utc"] = ParseInstant(latest["created_at"], "created_at")
                            .AddMilliseconds((long)latest["ttl"]).ToString("O")
                    }
                };
                JToken response = invokeTool(submit, cancellationToken);
                JObject structured = response?["structuredContent"] as JObject;
                string jobId = (string)structured?["job_id"];
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(jobId))
                {
                    // Even a submission response marked error can prove that Revit
                    // durably created a job before the wrapper completion ledger failed.
                    // Following that id is safer than declaring the task terminal while
                    // model work may still run.
                    latest["job_id"] = jobId;
                    latest["status"] = "working";
                    latest["status_message"] = (bool?)response?["isError"] == true
                        ? "Revit admitted durable job " + jobId +
                          ", but submission acknowledgement was incomplete; polling the job record."
                        : "Queued in Revit as durable job " + jobId + ".";
                }
                else if ((bool?)response?["isError"] == true)
                {
                    latest["status"] = "failed";
                    latest["status_message"] = (string)response?["content"]?[0]?["text"] ??
                        "Task submission failed without a diagnostic.";
                    latest["submission_result"] = response?.DeepClone();
                }
                else
                {
                    latest["status"] = "failed";
                    latest["status_message"] = "Task submission succeeded without a durable job_id.";
                    latest["submission_result"] = response?.DeepClone();
                }
                // Recovery needs the original arguments only until a submission
                // response is obtained. Do not retain model selectors or other user
                // inputs for the remainder of the task TTL.
                latest.Remove("arguments");
                latest.Remove("submit_idempotency_key");
                latest["last_updated_at"] = now.ToString("O");
                if (IsTerminal((string)latest["status"]))
                    WriteResultSnapshot(taskId, BuildResult(latest, taskId, null));
                Write(latest);
                Replace(record, latest);
            }
        }

        private static JObject Describe(JObject record)
        {
            string taskId = (string)record["task_id"];
            using (AcquireTaskMutex(taskId, CancellationToken.None))
            {
                JObject latest = ReadRequired(taskId);
                Replace(record, latest);
                return DescribeLocked(record);
            }
        }

        private static JObject DescribeLocked(JObject record)
        {
            string jobId = (string)record["job_id"];
            string status = (string)record["status"] ?? "failed";
            string message = (string)record["status_message"];
            DateTimeOffset updated = ParseInstant(record["last_updated_at"], "last_updated_at");

            // MCP task states are terminal. Once one is persisted, neither a
            // temporarily hidden job record nor PID reuse may reverse it.
            if (!IsTerminal(status) && string.IsNullOrWhiteSpace(jobId))
            {
                // Submission failure writes its exact result snapshot before publishing
                // the terminal sidecar. Recover the crash window in that same direction:
                // a pre-existing snapshot proves submission already answered and must
                // never be followed by a new enqueue whose eventual success would still
                // return the older failure snapshot.
                JObject submissionSnapshot = ReadResultSnapshot((string)record["task_id"]);
                if (submissionSnapshot != null)
                {
                    status = "failed";
                    message = (string)submissionSnapshot["content"]?[0]?["text"] ??
                              "Task submission failed before a durable job id was issued.";
                    updated = DateTimeOffset.UtcNow;
                    record["status"] = status;
                    record["status_message"] = message;
                    record["last_updated_at"] = updated.ToString("O");
                    record.Remove("arguments");
                    record.Remove("submit_idempotency_key");
                    Write(record);
                }
            }
            if (!IsTerminal(status) && !string.IsNullOrWhiteSpace(jobId))
            {
                JObject job = Job(jobId);
                if (job == null)
                {
                    status = "failed";
                    message = "The durable Revit job record is missing or unreadable; the task cannot make further progress. Do not resend automatically.";
                }
                else
                {
                    string state = (string)job["state"];
                    bool processDead = (bool?)job["process_alive"] == false;
                    switch (state)
                    {
                        case "queued" when processDead:
                            status = "failed";
                            message = "The owning Revit process exited before the queued job could run. The durable record proves it will not make further progress.";
                            break;
                        case "running" when processDead:
                            status = "failed";
                            message = "The owning Revit process exited while the job was running. Its final application state is not proven; do not resend automatically.";
                            break;
                        case "queued": status = "working"; message = "Queued in Revit; execution has not started."; break;
                        case "running": status = "working"; message = "Running on Revit's UI thread."; break;
                        case "ok": status = "completed"; message = "The Revit tool completed and its result is available."; break;
                        case "cancelled": status = "cancelled"; message = "The queued Revit job was cancelled before execution."; break;
                        default: status = "failed"; message = JobFailureText(job); break;
                    }
                    string jobPath = Path.Combine(Horizun.Revit.Core.HorizunPaths.JobsDir(), jobId + ".jsonl");
                    try { updated = new DateTimeOffset(File.GetLastWriteTimeUtc(jobPath), TimeSpan.Zero); } catch { }
                }

                if (IsTerminal(status))
                {
                    // Freeze the exact tool result before publishing the terminal
                    // state. A crash can leave a harmless unreferenced snapshot, but
                    // never a terminal task whose result was not made durable first.
                    EnsureResultSnapshot(record, jobId, job);
                    updated = DateTimeOffset.UtcNow;
                    record["status"] = status;
                    record["status_message"] = message;
                    record["last_updated_at"] = updated.ToString("O");
                    Write(record);
                }
            }

            return new JObject
            {
                ["taskId"] = (string)record["task_id"],
                ["status"] = status,
                ["statusMessage"] = message,
                ["createdAt"] = ParseInstant(record["created_at"], "created_at").ToString("O"),
                ["lastUpdatedAt"] = updated.ToUniversalTime().ToString("O"),
                ["ttl"] = (long)record["ttl"],
                ["pollInterval"] = PollIntervalMs
            };
        }

        private static JObject Result(JObject record, string taskId)
        {
            JObject snapshot = ReadResultSnapshot(taskId);
            if (snapshot != null) return snapshot;

            JObject response = BuildResult(record, taskId, string.IsNullOrWhiteSpace((string)record["job_id"])
                ? null : Job((string)record["job_id"]));
            WriteResultSnapshot(taskId, response);
            return response;
        }

        private static JObject BuildResult(JObject record, string taskId, JObject job)
        {
            JObject response;
            JObject submission = record["submission_result"] as JObject;
            string jobId = (string)record["job_id"];
            if (string.IsNullOrWhiteSpace(jobId))
                response = submission?.DeepClone() as JObject ?? McpResult.Text(
                    (string)record["status_message"] ?? "Task submission failed.", true);
            else
            {
                if (job == null) response = McpResult.Text("The durable Revit job record is missing.", true);
                else if ((string)job["state"] == "ok")
                {
                    JToken data = job["result"]?.DeepClone();
                    string text = data == null ? "null" : data.ToString(Formatting.Indented);
                    text += RevitSaidText(job["revit_said"]);
                    response = McpResult.AttachImageIfAny(data, text,
                        job["fallback"] as JObject, job["capability_gaps"] as JArray);
                }
                else
                {
                    string error = "Error: " + JobFailureText(job) + RevitSaidText(job["revit_said"]);
                    response = McpResult.Error(error, job["fallback"] as JObject,
                        job["capability_gaps"] as JArray, job["detail"] as JObject);
                }
            }

            JObject meta = response["_meta"] as JObject ?? new JObject();
            meta["io.modelcontextprotocol/related-task"] = new JObject { ["taskId"] = taskId };
            response["_meta"] = meta;
            return response;
        }

        private static string JobFailureText(JObject job)
        {
            var parts = new List<string>();
            foreach (string field in new[] { "final_note", "what_this_means", "record_fault", "read_error" })
            {
                string value = (string)job?[field];
                if (!string.IsNullOrWhiteSpace(value) && !parts.Contains(value)) parts.Add(value);
            }
            return parts.Count == 0 ? "The asynchronous Revit task failed." : string.Join(" ", parts);
        }

        private static bool IsTerminal(string status)
            => status == "completed" || status == "failed" || status == "cancelled";

        private static void EnsureResultSnapshot(JObject record, string jobId, JObject observedJob)
        {
            string taskId = (string)record["task_id"];
            if (ReadResultSnapshot(taskId) != null) return;
            JObject job = observedJob ?? (string.IsNullOrWhiteSpace(jobId) ? null : Job(jobId));
            JObject result = BuildResult(record, taskId, job);
            WriteResultSnapshot(taskId, result);
        }

        private static JObject Job(string jobId)
        {
            try
            {
                return JobStatus.ReadForTask(jobId, CancellationToken.None);
            }
            catch { return null; }
        }

        private static string RevitSaidText(JToken said)
            => said == null || said.Type == JTokenType.Null ? "" :
               Environment.NewLine + Environment.NewLine + "--- what Revit raised while this ran ---" +
               Environment.NewLine + said.ToString(Formatting.Indented);

        private static long Ttl(JObject task)
        {
            JToken token = task?["ttl"];
            if (token == null) return DefaultTtlMs;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new McpError(-32602, "Invalid params: task.ttl must be a positive number of milliseconds.");
            double raw = (double)token;
            if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0)
                throw new McpError(-32602, "Invalid params: task.ttl must be a positive number of milliseconds.");
            long requested = raw >= long.MaxValue ? long.MaxValue : (long)raw;
            return Math.Max(MinTtlMs, Math.Min(MaxTtlMs, requested));
        }

        private static string TaskId(JObject prms)
        {
            string id = RequiredString(prms, "taskId");
            if (!Guid.TryParseExact(id, "N", out _))
                throw new McpError(-32602, "Invalid or nonexistent taskId.");
            return id;
        }

        private static string RequiredString(JObject o, string key)
        {
            JToken token = o?[key];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new McpError(-32602, "Invalid params: required string '" + key + "' is missing.");
            return (string)token;
        }

        private static JObject ReadRequired(string taskId)
        {
            string path = Path.Combine(Dir(), taskId + ".json");
            if (!File.Exists(path)) throw new McpError(-32602, "Invalid or nonexistent taskId '" + taskId + "'.");
            try
            {
                return JObject.Parse(ReadBoundedText(path, MaxSidecarBytes));
            }
            catch (Exception ex) { throw new McpError(-32603, "Task record is unreadable: " + ex.Message); }
        }

        private static void AdmitAndWrite(JObject record)
        {
            using (AcquireNamedMutex("Local\\Horizun.McpTaskStore.V1", CancellationToken.None))
            lock (StorageGate)
            {
                Directory.CreateDirectory(Dir());
                // A crash can occur after an atomic result snapshot was moved but
                // before its terminal sidecar was replaced. Reclaim only snapshots
                // that have no corresponding task; a locked orphan counts against
                // the byte budget instead of becoming invisible growth.
                foreach (FileInfo result in new DirectoryInfo(Dir()).EnumerateFiles("*.result.json"))
                {
                    string name = result.Name.Substring(0, result.Name.Length - ".result.json".Length);
                    if (Guid.TryParseExact(name, "N", out _) &&
                        !File.Exists(Path.Combine(Dir(), name + ".json")))
                    {
                        try { result.Delete(); } catch { }
                    }
                }

                int retained = 0;
                foreach (FileInfo file in new DirectoryInfo(Dir()).EnumerateFiles("*.json").Where(IsTaskSidecar))
                {
                    bool expired = false;
                    try
                    {
                        if (file.Length <= MaxSidecarBytes)
                            expired = Expired(JObject.Parse(ReadBoundedText(file.FullName, MaxSidecarBytes)));
                    }
                    catch { /* corrupt records count against capacity and fail closed */ }
                    if (expired)
                    {
                        try
                        {
                            string taskId = Path.GetFileNameWithoutExtension(file.Name);
                            string resultPath = ResultPath(taskId);
                            // Snapshot first. If it is locked, retain the sidecar so
                            // the orphan is visible and bounded on the next pass.
                            if (File.Exists(resultPath)) File.Delete(resultPath);
                            file.Delete();
                            continue;
                        }
                        catch { }
                    }
                    retained++;
                    if (retained >= MaxRetainedTasks)
                        throw new McpError(-32000,
                            "The durable MCP task store is full (" + MaxRetainedTasks + "). Wait for task TTLs " +
                            "to expire before creating more. Nothing was submitted to Revit.");
                }

                long storeBytes = TaskStoreBytes();
                long incoming = Encoding.UTF8.GetByteCount(record.ToString(Formatting.Indented));
                if (storeBytes > MaxTaskStoreBytes - incoming)
                    throw new McpError(-32000,
                        "The durable MCP task store reached its " + MaxTaskStoreBytes +
                        " byte aggregate limit. Wait for task TTLs to expire. Nothing was submitted to Revit.");
                Write(record);
            }
        }

        private static void Write(JObject record)
        {
            string taskId = (string)record["task_id"];
            if (!Guid.TryParseExact(taskId, "N", out _))
                throw new McpError(-32603, "Refusing to persist an invalid task id.");
            Directory.CreateDirectory(Dir());
            string path = Path.Combine(Dir(), taskId + ".json");
            string temp = Path.Combine(Dir(), "." + taskId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                string serialized = record.ToString(Formatting.Indented);
                if (Encoding.UTF8.GetByteCount(serialized) > MaxSidecarBytes)
                    throw new McpError(-32602,
                        "Task request is too large for the " + MaxSidecarBytes + " byte durable sidecar limit. " +
                        "Nothing was submitted to Revit.");
                File.WriteAllText(temp, serialized, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                temp = null;
            }
            finally { if (temp != null) try { File.Delete(temp); } catch { } }
        }

        private static IDisposable AcquireTaskMutex(string taskId, CancellationToken cancellationToken)
            => AcquireNamedMutex("Local\\Horizun.McpTask." + taskId, cancellationToken);

        private static IDisposable AcquireNamedMutex(string name, CancellationToken cancellationToken)
        {
            var mutex = new Mutex(false, name);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (mutex.WaitOne(250)) return new MutexLease(mutex);
                    }
                    catch (AbandonedMutexException) { return new MutexLease(mutex); }
                }
            }
            catch { mutex.Dispose(); throw; }
        }

        private static string ReadBoundedText(string path, long maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.Read | FileShare.Delete))
            {
                long length = stream.Length;
                if (length > maxBytes) throw new InvalidDataException("record exceeds the " + maxBytes + " byte limit");
                var bytes = new byte[(int)length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("record ended while it was being read");
                    offset += read;
                }
                if (stream.ReadByte() != -1 || stream.Length != length)
                    throw new InvalidDataException("record changed while it was being read");
                return new UTF8Encoding(false, true).GetString(bytes);
            }
        }

        private static string ResultPath(string taskId) => Path.Combine(Dir(), taskId + ".result.json");

        private static bool IsTaskSidecar(FileInfo file)
            => file != null && Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out _);

        private static JObject ReadResultSnapshot(string taskId)
        {
            string path = ResultPath(taskId);
            if (!File.Exists(path)) return null;
            try { return JObject.Parse(ReadBoundedText(path, MaxTaskResultBytes)); }
            catch (Exception ex) { throw new McpError(-32603, "Task result snapshot is unreadable: " + ex.Message); }
        }

        private static void WriteResultSnapshot(string taskId, JObject result)
        {
            string path = ResultPath(taskId);
            string serialized = result.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(serialized) > MaxTaskResultBytes)
                throw new McpError(-32603, "Task result exceeds the durable " + MaxTaskResultBytes + " byte limit.");
            using (AcquireNamedMutex("Local\\Horizun.McpTaskStore.V1", CancellationToken.None))
            lock (StorageGate)
            {
                if (File.Exists(path)) return;
                long incoming = Encoding.UTF8.GetByteCount(serialized);
                if (TaskStoreBytes() > MaxTaskStoreBytes - incoming)
                    throw new McpError(-32000,
                        "The durable MCP task store cannot snapshot this result without exceeding its " +
                        MaxTaskStoreBytes + " byte aggregate limit.");
                string temp = Path.Combine(Dir(), "." + taskId + ".result." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllText(temp, serialized, new UTF8Encoding(false));
                    try { File.Move(temp, path); }
                    catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
                    temp = null;
                }
                finally { if (temp != null) try { File.Delete(temp); } catch { } }
            }
        }

        private static long TaskStoreBytes()
        {
            long total = 0;
            if (!Directory.Exists(Dir())) return 0;
            foreach (FileInfo file in new DirectoryInfo(Dir()).EnumerateFiles("*.json"))
            {
                try
                {
                    if (file.Length > MaxTaskStoreBytes - total) return MaxTaskStoreBytes + 1;
                    total += file.Length;
                }
                catch { return MaxTaskStoreBytes + 1; }
            }
            return total;
        }

        private sealed class MutexLease : IDisposable
        {
            private Mutex _mutex;
            public MutexLease(Mutex mutex) { _mutex = mutex; }
            public void Dispose()
            {
                Mutex mutex = Interlocked.Exchange(ref _mutex, null);
                if (mutex == null) return;
                try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); }
            }
        }

        private static void Replace(JObject target, JObject source)
        {
            target.RemoveAll();
            foreach (JProperty p in source.Properties()) target[p.Name] = p.Value.DeepClone();
        }

        private static bool Expired(JObject record)
        {
            try { return DateTimeOffset.UtcNow >= ParseInstant(record["created_at"], "created_at")
                    .AddMilliseconds((long)record["ttl"]); }
            catch { return true; }
        }

        private static void EnsureNotExpired(JObject record)
        {
            if (Expired(record)) throw new McpError(-32602, "Invalid or expired taskId.");
        }

        private static DateTimeOffset ParseInstant(JToken token, string field)
        {
            if (token?.Type == JTokenType.Date && token is JValue date)
            {
                if (date.Value is DateTimeOffset dto) return dto.ToUniversalTime();
                if (date.Value is DateTime dt)
                {
                    if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    return new DateTimeOffset(dt.ToUniversalTime());
                }
            }
            string value = token?.Type == JTokenType.String ? (string)token : null;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                throw new McpError(-32603, "Task record has invalid " + field + ".");
            return parsed;
        }

        private static string EncodeCursor(int offset) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes("horizun-tasks-v1:" + offset.ToString(CultureInfo.InvariantCulture)));

        private static int DecodeCursor(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            if (token.Type != JTokenType.String) throw new McpError(-32602, "Invalid tasks/list cursor.");
            try
            {
                string raw = Encoding.UTF8.GetString(Convert.FromBase64String((string)token));
                const string prefix = "horizun-tasks-v1:";
                if (!raw.StartsWith(prefix, StringComparison.Ordinal) ||
                    !int.TryParse(raw.Substring(prefix.Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int offset) || offset < 0)
                    throw new FormatException();
                return offset;
            }
            catch { throw new McpError(-32602, "Invalid tasks/list cursor."); }
        }
    }
}
