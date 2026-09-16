// -----------------------------------------------------------------------------
// Horizun MCP server - the io.modelcontextprotocol/tasks extension.
// Original Horizun code.
//
// 2026-07-28 moved tasks out of the core protocol and redesigned them:
//
//   * the blocking tasks/result is GONE. tasks/get carries the outcome.
//   * tasks/update arrives, for client-to-server input on a task that is waiting.
//   * tasks/list is gone from the extension entirely.
//   * a task handle is a result with resultType "task".
//   * the client opts in once, per request, through its capabilities.
//
// NOTHING BELOW IS A SECOND TASK STORE. The durable sidecars, the two-phase
// submission, the frozen result snapshot and the terminal-state rules all live in
// McpTasks and are unchanged - this is the 2026-07-28 spelling of them. A second
// implementation of "is this task finished" is how two answers to that question
// come to exist.
//
// WHAT THIS DELIBERATELY DOES NOT DO. It never hands back a task the caller did
// not ask for. The spec now permits unsolicited task handles; this bridge's
// contract is that a reply does not change shape without the caller asking, and a
// client that expected a tool result and got a handle has to poll for something
// it never wanted. The augmentation stays explicit.
// -----------------------------------------------------------------------------
using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal static class ModernTasks
    {
        /// <summary>
        /// The task-handle result for a tools/call the client asked to run as a task.
        /// Returned with resultType "task"; the caller passes that to ResultEnvelope.
        /// </summary>
        public static JObject CreateResult(
            JObject toolCall, Func<JObject, CancellationToken, JToken> invokeTool,
            CancellationToken cancellationToken, RequestEnvelope envelope)
        {
            Require(envelope);

            // The durable creation, unchanged. It returns the legacy envelope; the task
            // object inside it is the same record, so only the FIELD NAMES differ.
            JObject legacy = McpTasks.Create(toolCall, invokeTool, cancellationToken);
            JObject task = legacy["task"] as JObject;
            if (task == null)
                throw new McpError(McpErrorCodes.InternalError,
                    "The task was created but its handle could not be read back. Nothing may be assumed about " +
                    "whether the work is queued; call tasks/get with the id in the server log before resending.");

            return new JObject { ["task"] = Rename(task) };
        }

        /// <summary>tasks/get: current state, and the outcome once it is terminal.</summary>
        public static JObject Get(JObject prms, RequestEnvelope envelope)
        {
            Require(envelope);
            string taskId = ReadTaskId(prms);
            JObject task = McpTasks.GetWithOutcome(taskId);
            return Rename(task);
        }

        /// <summary>
        /// tasks/update: the client answering an input request the task raised.
        ///
        /// Horizun raises none. Every typed command either has what it needs or refuses
        /// before it starts, and a task that stopped to ask a question nobody is there
        /// to answer is the failure mode the unattended contract exists to prevent - so
        /// no task of ours ever reaches input_required.
        ///
        /// The method still exists and still acknowledges, because the extension says a
        /// server implementing it MUST accept responses and ignore unknown or
        /// already-satisfied keys. Refusing here would make a conforming client think the
        /// extension was half-implemented; acknowledging an empty set is the truth.
        /// </summary>
        public static JObject Update(JObject prms, RequestEnvelope envelope)
        {
            Require(envelope);
            string taskId = ReadTaskId(prms);

            // Prove the task exists before acknowledging. An ack for an id that was never
            // created tells the client its answer landed somewhere.
            McpTasks.GetWithOutcome(taskId);

            JToken responses = prms?["inputResponses"];
            if (responses != null && responses.Type != JTokenType.Object && responses.Type != JTokenType.Null)
                throw new McpError(McpErrorCodes.InvalidParams,
                    "Invalid params: 'inputResponses' must be an object keyed by the input request it answers.");

            return new JObject();
        }

        /// <summary>
        /// tasks/cancel: record the intent, acknowledge, and promise nothing more.
        ///
        /// Cancellation is cooperative in the extension and genuinely limited here: work
        /// still waiting in the bridge's FIFO is removed before it starts, and a command
        /// already on Revit's UI thread cannot be interrupted by anyone, including Revit.
        /// The acknowledgement is the ack the spec asks for; the honesty is in the status
        /// message the next tasks/get returns.
        /// </summary>
        public static JObject Cancel(JObject prms, RequestEnvelope envelope)
        {
            Require(envelope);
            string taskId = ReadTaskId(prms);
            McpTasks.RequestCancellation(taskId);
            return new JObject();
        }

        // ---- helpers ---------------------------------------------------------------

        private static void Require(RequestEnvelope envelope)
        {
            if (envelope == null || envelope.Era != McpEra.Modern)
                throw new McpError(McpErrorCodes.MethodNotFound,
                    "The tasks extension is part of protocol " + McpRevision.Latest + ". This request did not " +
                    "declare that revision, so it is being served under the earlier protocol, where the task " +
                    "methods are tasks/get and tasks/result.");

            envelope.RequireExtension(ExtensionRegistry.Tasks,
                "Tasks change the shape of a reply - you get a handle to poll instead of the result - so this " +
                "server will not return one to a client that has not said it can handle it.");
        }

        private static string ReadTaskId(JObject prms)
        {
            JToken token = prms?["taskId"];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new McpError(McpErrorCodes.InvalidParams,
                    "Invalid params: this method requires a non-empty string 'taskId'.");
            return (string)token;
        }

        /// <summary>
        /// The legacy task object, in this revision's spelling: `ttl` became `ttlMs` and
        /// `pollInterval` became `pollIntervalMs`. Renamed rather than re-derived, so the
        /// two spellings cannot come to disagree about the same stored number.
        /// </summary>
        private static JObject Rename(JObject task)
        {
            var o = (JObject)task.DeepClone();
            JToken ttl = o["ttl"];
            if (ttl != null) { o.Remove("ttl"); o["ttlMs"] = ttl; }
            JToken poll = o["pollInterval"];
            if (poll != null) { o.Remove("pollInterval"); o["pollIntervalMs"] = poll; }
            return o;
        }
    }
}
