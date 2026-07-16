// -----------------------------------------------------------------------------
// Horizun hardening layer — NEW FILE (added to the rvt-mcp base by Horizun).
// Apache-2.0 (see LICENSE); this file is an original Horizun contribution.
//
// Async job registry. Long operations (NWC/IFC export, sync, heavy send_code)
// exceed the transport timeout; instead of losing the result, a command run
// with "async": true returns a job_id immediately and the result is stored here
// for a later "job_status" poll. Static so it survives transport restarts and
// is reachable from the HTTP/transport thread (Create, Get) and the Revit UI
// thread (Complete). Written from multiple threads → ConcurrentDictionary.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace RvtMcp.Plugin
{
    public enum JobState { Pending, Running, Done, Error }

    public class JobRecord
    {
        public string JobId { get; set; }
        public string Tool { get; set; }
        public JobState State { get; set; } = JobState.Pending;
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;
        public DateTime? CompletedUtc { get; set; }
        public bool Success { get; set; }
        public string ResultJson { get; set; }
        public string Error { get; set; }
    }

    public static class McpJobRegistry
    {
        private static readonly ConcurrentDictionary<string, JobRecord> _jobs =
            new ConcurrentDictionary<string, JobRecord>();
        private const int MaxJobs = 200;

        public static JobRecord Create(string jobId, string tool)
        {
            Prune();
            var rec = new JobRecord { JobId = jobId, Tool = tool };
            _jobs[jobId] = rec;
            return rec;
        }

        public static void MarkRunning(string jobId)
        {
            if (jobId != null && _jobs.TryGetValue(jobId, out var rec) && rec.State == JobState.Pending)
                rec.State = JobState.Running;
        }

        public static void Complete(string jobId, bool success, string resultJson, string error)
        {
            if (jobId != null && _jobs.TryGetValue(jobId, out var rec))
            {
                rec.Success = success;
                rec.ResultJson = resultJson;
                rec.Error = error;
                rec.CompletedUtc = DateTime.UtcNow;
                rec.State = success ? JobState.Done : JobState.Error;
            }
        }

        public static JobRecord Get(string jobId)
            => jobId != null && _jobs.TryGetValue(jobId, out var rec) ? rec : null;

        private static void Prune()
        {
            if (_jobs.Count < MaxJobs) return;
            foreach (var old in _jobs.Values
                         .Where(j => j.CompletedUtc != null)
                         .OrderBy(j => j.CompletedUtc)
                         .Take(_jobs.Count - MaxJobs + 1))
                _jobs.TryRemove(old.JobId, out _);
        }
    }
}
