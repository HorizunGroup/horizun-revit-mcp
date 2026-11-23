// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A Python run still going at CommandDeadlinePolicy.PythonHandoffMs answers its caller
// with the job that will carry its result, instead of holding the request until the
// client's own limit (600 s) abandons it. Field 2026-11: 60 of 73 timeouts in three days
// were scripts that kept a client waiting the full ten minutes and then reported nothing.
//
// The work is not interrupted: it keeps running on Revit's UI thread, and its checkpoints,
// output and final status (or failure cause) land in the job record that
// horizun_job_status reads without Revit. This reply is NOT the script's result, and says
// so in a field a program can read (completed=false, state=running).
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PythonHandoff
    {
        public static JObject Payload(string jobId, long waitedMs, int checkpoints, string lastCheckpoint, JObject modalDialog,
                                      string command = "horizun_execute_python")
        {
            if (command == "horizun_export")
            {
                JObject e = Payload(jobId, waitedMs, checkpoints, lastCheckpoint, modalDialog);
                e["what_this_means"] =
                    "The export is STILL RUNNING in Revit; this is not its result, and it has neither finished nor failed. " +
                    "The files it writes, re-read from disk, and its final status (or the cause of a failure) are recorded as job " + jobId +
                    ". Poll horizun_job_status job_id=" + jobId + " - it reads the record from disk and does not need Revit - " +
                    "until finished is true, then read result. Do NOT send the export again: that would export a second time. " +
                    "Other Revit calls queue behind it until it ends, so wait for the job first.";
                return e;
            }
            var o = new JObject
            {
                ["mode"] = "handoff",
                ["state"] = "running",
                ["completed"] = false,
                ["job_id"] = jobId,
                ["waited_ms"] = waitedMs,
                ["handoff_after_ms"] = CommandDeadlinePolicy.PythonHandoffMs,
                ["checkpoints_so_far"] = checkpoints,
                ["last_checkpoint"] = lastCheckpoint == null ? JValue.CreateNull() : new JValue(lastCheckpoint),
                ["host_verified"] = false,
                ["verification_status"] = "pending",
                ["follow_with"] = "horizun_job_status job_id=" + jobId,
                ["what_this_means"] =
                    "The script is STILL RUNNING in Revit; this is not its result, and it has neither finished nor failed. " +
                    "Its checkpoints, output and final status (or the cause of a failure) are recorded as job " + jobId +
                    ". Poll horizun_job_status job_id=" + jobId + " - it reads the record from disk and does not need Revit - " +
                    "until finished is true, then read result. Do NOT send the script again: that would run it a second time. " +
                    "Other Revit calls queue behind it until it ends, so wait for the job first."
            };
            if (modalDialog != null) o["modal_dialog"] = modalDialog;
            return o;
        }
    }
}
