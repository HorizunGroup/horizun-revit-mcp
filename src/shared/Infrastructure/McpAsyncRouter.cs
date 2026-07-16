// -----------------------------------------------------------------------------
// Horizun hardening layer — NEW FILE (added to the rvt-mcp base by Horizun).
// Apache-2.0 (see LICENSE); this file is an original Horizun contribution.
//
// (B) Async submit / poll.
//
// Long operations (NWC/IFC export, sync-to-central, heavy send_code) can exceed
// the transport read timeout. Instead of losing the result, a command invoked
// with "async": true (top level or inside "params") is ACCEPTED immediately with
// a job_id; the real work still runs on the single UI-thread pump, and its result
// is stored in McpJobRegistry for a later "job_status" poll.
//
// This router runs on the transport thread, BEFORE anything is enqueued, so:
//   * a "job_status" poll is answered straight from the registry and never waits
//     behind the very job it is polling, and
//   * an async submit returns in microseconds while the work is queued for the
//     UI thread.
//
// Techniques are standard (ExternalEvent + a result store, à la Revit.Async);
// no third-party GPL code is used.
// -----------------------------------------------------------------------------
using System;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    public static class McpAsyncRouter
    {
        /// <summary>
        /// Handle async-submit and job_status requests off the UI pump.
        /// Returns <c>true</c> if the request was fully answered here (the caller
        /// must then stop); <c>false</c> to let the caller run the normal
        /// synchronous enqueue path.
        /// </summary>
        public static bool TryRoute(JObject request, TaskCompletionSource<string> tcs,
                                    McpEventHandler handler, ExternalEvent externalEvent)
        {
            if (request == null || tcs == null || handler == null || externalEvent == null)
                return false;

            var command = request.Value<string>("command");

            // job_status: answer straight from the registry, never enqueue.
            if (string.Equals(command, "job_status", StringComparison.Ordinal))
            {
                var reqId = request.Value<string>("id");
                var jobId = request["params"]?.Value<string>("job_id")
                            ?? request.Value<string>("job_id");
                tcs.TrySetResult(BuildStatusResponse(reqId, jobId));
                return true;
            }

            if (!WantsAsync(request))
                return false;

            var id = request.Value<string>("id");
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString("N");

            McpJobRegistry.Create(id, command);

            // Inner TCS carries the pump's real response; its continuation parks
            // that response in the registry for the poller. Runs off the UI thread.
            var inner = new TaskCompletionSource<string>();
            inner.Task.ContinueWith(t =>
            {
                try
                {
                    if (t.IsCanceled)
                    {
                        McpJobRegistry.Complete(id, false, null, "Job was canceled (transport restart or shutdown).");
                        return;
                    }
                    if (t.IsFaulted)
                    {
                        McpJobRegistry.Complete(id, false, null,
                            t.Exception?.GetBaseException().Message ?? "Job faulted.");
                        return;
                    }

                    var payload = t.Result;
                    bool ok = false;
                    string error = null;
                    try
                    {
                        var parsed = JObject.Parse(payload);
                        ok = parsed.Value<bool?>("success") ?? false;
                        error = parsed.Value<string>("error");
                    }
                    catch { /* store raw payload even if it is not the expected envelope */ }

                    McpJobRegistry.Complete(id, ok, payload, error);
                }
                catch (Exception ex)
                {
                    McpJobRegistry.Complete(id, false, null, ex.Message);
                }
            }, TaskScheduler.Default);

            var pending = new PendingRequest
            {
                Id = id,
                CommandName = command,
                ParamsJson = request["params"]?.ToString() ?? "{}",
                Tcs = inner
            };
            handler.Enqueue(pending);
            externalEvent.Raise();

            tcs.TrySetResult(JsonConvert.SerializeObject(new
            {
                id,
                success = true,
                data = new
                {
                    status = "accepted",
                    job_id = id,
                    tool = command,
                    message = "Accepted for async execution. Poll 'job_status' with this job_id."
                }
            }));
            return true;
        }

        private static bool WantsAsync(JObject request)
        {
            if (request.Value<bool?>("async") == true) return true;
            var p = request["params"] as JObject;
            if (p != null)
            {
                if (p.Value<bool?>("async") == true) return true;
                if (p.Value<bool?>("run_async") == true) return true;
            }
            return false;
        }

        private static string BuildStatusResponse(string reqId, string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return JsonConvert.SerializeObject(new
                {
                    id = reqId,
                    success = false,
                    error = "job_status requires a 'job_id' (from the async submit response)."
                });

            var rec = McpJobRegistry.Get(jobId);
            if (rec == null)
                return JsonConvert.SerializeObject(new
                {
                    id = reqId,
                    success = false,
                    error = $"Unknown job_id '{jobId}' (expired from the registry, or never created)."
                });

            var state = rec.State.ToString().ToLowerInvariant();
            var done = rec.State == JobState.Done || rec.State == JobState.Error;

            // When finished, unwrap the stored pump envelope so the poller sees the
            // same "data"/"error" it would have gotten from a synchronous call.
            JToken resultData = null;
            if (rec.State == JobState.Done && !string.IsNullOrEmpty(rec.ResultJson))
            {
                try { resultData = JObject.Parse(rec.ResultJson)?["data"]; }
                catch { }
            }

            return JsonConvert.SerializeObject(new
            {
                id = reqId,
                success = true,
                data = new
                {
                    job_id = rec.JobId,
                    tool = rec.Tool,
                    state,
                    done,
                    job_success = done ? (bool?)rec.Success : null,
                    result = resultData,
                    error = rec.Error,
                    created_utc = rec.CreatedUtc.ToString("o"),
                    completed_utc = rec.CompletedUtc?.ToString("o")
                }
            });
        }
    }
}
