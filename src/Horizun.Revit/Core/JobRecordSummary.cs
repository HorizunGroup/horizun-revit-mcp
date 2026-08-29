// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// One job record's state, read from its own event lines. The ribbon's Jobs
// dialog and anything else that summarises the jobs directory decide with THIS,
// because the input is exactly the kind that punishes casual parsing: a JSONL
// file a killed process may have left with half a line at the end, and a
// half-written record must read as a STATE, never as an exception.
//
// The three states, and why "running" is not one of them:
//
//   queued          the record was opened and no running event was written -
//                   the work never started;
//   running_or_died a running event and no finish. From the file alone these
//                   two are indistinguishable, and CLAIMING "running" would be
//                   asserting a fact the file does not hold. horizun_job_status
//                   is the reader that checks the process and tells them apart;
//   finished        a finish event, whose status travels along.
//
// The liveness-aware overload takes a processAlive delegate and, when the
// record carries the writer's pid, resolves the ambiguity the same way the
// server does: running (alive), interrupted (dead - the finish line will never
// come), or running_or_died when the record predates pid stamping. The OS
// check is injected, so the folding stays provable without an OS.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class JobRecordSummary
    {
        public string State { get; private set; } = "queued";
        public string FinishStatus { get; private set; }

        /// <summary>True when the finish status names anything other than success.</summary>
        public bool Failed => FinishStatus != null && FinishStatus != "ok" && FinishStatus != "succeeded";

        /// <summary>
        /// Fold one record's lines. A line that does not parse is SKIPPED, not fatal:
        /// it is what a process killed mid-append leaves behind, and the record's
        /// earlier lines still carry the truth about how far the job got.
        /// </summary>
        public static JobRecordSummary FromLines(IEnumerable<string> lines) => FromLines(lines, null);

        /// <summary>
        /// The same fold, resolving the running-or-died ambiguity when it CAN be
        /// resolved: the record carries the writer's pid and the caller supplies an
        /// OS check. No pid, or no delegate, keeps the honest ambiguity.
        /// </summary>
        public static JobRecordSummary FromLines(IEnumerable<string> lines, Func<int, bool> processAlive)
        {
            var summary = new JobRecordSummary();
            if (lines == null) return summary;
            bool sawRunning = false;
            int? pid = null;
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject e;
                try { e = JObject.Parse(line); } catch { continue; }
                string kind = e.Value<string>("event");
                if (e["pid"] != null)
                {
                    try { int p = (int)e["pid"]; if (p > 0) pid = p; } catch { }
                }
                if (kind == "running") sawRunning = true;
                else if (kind == "finish")
                {
                    summary.State = "finished";
                    summary.FinishStatus = e.Value<string>("status");
                }
            }
            if (summary.State != "finished" && sawRunning)
            {
                summary.State = "running_or_died";
                if (pid.HasValue && processAlive != null)
                {
                    bool? alive = null;
                    try { alive = processAlive(pid.Value); } catch { }
                    if (alive == true) summary.State = "running";
                    else if (alive == false) summary.State = "interrupted";
                }
            }
            return summary;
        }
    }
}
