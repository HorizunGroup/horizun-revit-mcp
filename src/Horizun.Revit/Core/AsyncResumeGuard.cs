using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class AsyncResumeGuard
    {
        public static string ArgumentsHash(JObject arguments)
        {
            var copy = (JObject)arguments.DeepClone();
            copy.Remove("idempotency_key"); copy.Remove("confirmation_token");
            return RequestFingerprint.Sha256Hex(RequestFingerprint.Canonical(copy));
        }

        public static JObject Context(string tool, JObject arguments, string document) => new JObject
        {
            ["schema"] = 1, ["durable_start_required"] = true, ["tool"] = tool,
            ["arguments_hash"] = ArgumentsHash(arguments), ["document"] = document
        };

        // Resume is deliberately narrower than retry. No running/result/checkpoint
        // event may exist. A new confirmation may replace an expired one, but the
        // semantic request and document must remain identical.
        public static void Validate(string id, string idempotencyKey, string tool, JObject arguments,
                                    string document, Func<int, bool> processAlive)
        {
            if (string.IsNullOrWhiteSpace(id) || id != Path.GetFileName(id) ||
                id.IndexOfAny(new[] { '/', '\\' }) >= 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException("Invalid resume_from_job_id.");
            if (idempotencyKey != "resume:" + id)
                throw new InvalidOperationException("Resume requires idempotency_key='resume:" + id + "' so repeated requests cannot create multiple replacements.");
            string path = Path.Combine(HorizunPaths.JobsDir(), id + ".jsonl");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
                throw new InvalidOperationException("The source job record is missing or too large for a never-started job.");
            JObject start = null;
            bool finished = false;
            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject record;
                try { record = JObject.Parse(line); }
                catch (JsonException) { throw new InvalidOperationException("The source record is truncated; inspect the model before retrying."); }
                string ev = record.Value<string>("event");
                DateTime at;
                if (record["at"]?.Type != JTokenType.String || !DateTime.TryParseExact((string)record["at"],
                    "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out at))
                    throw new InvalidOperationException("The source record has an invalid event timestamp.");
                if (start == null && ev == "start") { start = record; continue; }
                if (start != null && !finished && ev == "finish" && record.Value<string>("status") == "not_started" &&
                    record["record_fault"] == null && record["checkpoints"]?.Type == JTokenType.Integer &&
                    record.Value<int>("checkpoints") == 0) { finished = true; continue; }
                throw new InvalidOperationException("The source job ran or has incomplete/unexpected evidence. Automatic resume is refused.");
            }
            JObject context = start?["resume_guard"] as JObject;
            if (context?.Value<int?>("schema") != 1 || context.Value<bool?>("durable_start_required") != true ||
                start.Value<string>("tool") != tool || context.Value<string>("tool") != tool ||
                context.Value<string>("arguments_hash") != ArgumentsHash(arguments) ||
                string.IsNullOrWhiteSpace(document) || context.Value<string>("document") != document)
                throw new InvalidOperationException("The source job has no matching resumable request/document evidence.");
            int pid = start.Value<int?>("pid") ?? 0;
            if (!finished && (pid <= 0 || processAlive(pid)))
                throw new InvalidOperationException("The source owner may still execute this queued job. Keep polling its job_id.");
        }

        public static void Begin(Job job)
        {
            if (job == null || !job.RecordIsComplete)
                throw new InvalidOperationException("The job record is not durable and complete. Nothing was executed.");
            job.MarkRunning();
            if (!job.RecordIsComplete)
                throw new InvalidOperationException("The running event could not be persisted. Nothing was executed: " + job.WriteFault);
        }
    }
}
