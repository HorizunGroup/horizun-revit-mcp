using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class TransportProgress
    {
        internal static JObject CancellationNotice(JObject status)
        {
            var progress = status?["progress"] as JObject;
            return new JObject
            {
                ["status"] = "cancel_requested", ["request_state"] = status?["state"] ?? new JValue("unobserved"),
                ["progress"] = progress?.DeepClone(), ["host_verified"] = false,
                ["means"] = "Cancellation was requested; the job record is the source of the terminal state and any partial result."
            };
        }

        /// <summary>
        /// A remaining-time estimate from the script's own checkpoint(label, done, total), or null when it gave none.
        /// Linear extrapolation over the time since the request was sent: a rough guide, never a promise.
        /// </summary>
        internal static JObject Estimate(double elapsedSeconds, double? done, double? total)
        {
            if (!done.HasValue || !total.HasValue || total.Value <= 0 || done.Value <= 0 || elapsedSeconds <= 0) return null;
            if (done.Value > total.Value) return null;
            double remaining = elapsedSeconds * (total.Value - done.Value) / done.Value;
            return new JObject
            {
                ["remaining_s_approx"] = (long)System.Math.Round(remaining),
                ["done"] = done.Value, ["total"] = total.Value,
                ["basis"] = "linear extrapolation from the script's last checkpoint(label, done, total) and the elapsed time; steps are rarely uniform"
            };
        }

        internal static JObject Notice(JObject status, bool neverStarted, long elapsedMs = 0)
        {
            var progress = status?["progress"] as JObject;
            var result = NoticeCore(status, neverStarted, progress);
            if (neverStarted) return result;
            if (elapsedMs > 0) result["elapsed_s"] = (long)System.Math.Round(elapsedMs / 1000.0);
            string jobId = (string)progress?["job_id"];
            if (!string.IsNullOrEmpty(jobId))
            {
                result["job_id"] = jobId;
                result["poll_with"] = new JObject
                    { ["tool"] = "horizun_job_status", ["arguments"] = new JObject { ["job_id"] = jobId } };
                JObject est = Estimate(elapsedMs / 1000.0, (double?)progress["last_done"], (double?)progress["last_total"]);
                if (est != null) result["estimate"] = est;
            }
            return result;
        }

        private static JObject NoticeCore(JObject status, bool neverStarted, JObject progress)
        {
            return new JObject
            {
                ["status"] = "timeout", ["request_state"] = status?["state"] ?? new JValue("unobserved"),
                ["progress"] = neverStarted ? null : progress?.DeepClone(),
                ["host_verified"] = false,
                ["means"] = neverStarted ? "The request never started; nothing was executed."
                    : "The wait expired. Revit may still be running. Checkpoints are script progress, not proof of committed changes. Inspect the same job before retrying."
            };
        }

        internal static string Sentence(JObject notice)
        {
            var p = notice?["progress"] as JObject;
            if (p == null) return " No progress measurement is available.";
            string text = " Last checkpoint: " + ((string)p["last_checkpoint"] ?? "none") +
                  "; checkpoints=" + (p["checkpoint_count"]?.ToString() ?? "unknown") +
                  "; job_id=" + ((string)p["job_id"] ?? "unavailable");
            if (notice["elapsed_s"] != null) text += "; elapsed " + notice["elapsed_s"] + " s";
            text += ". Progress is not verification.";
            if (notice["poll_with"] != null)
                text += " Poll it with horizun_job_status job_id=" + (string)notice["job_id"] + " (do not resend the script).";
            if (notice["estimate"] != null)
                text += " Rough estimate: about " + notice["estimate"]["remaining_s_approx"] + " s left (" +
                        notice["estimate"]["done"] + " of " + notice["estimate"]["total"] + " steps done).";
            return text;
        }
    }
}
