// -----------------------------------------------------------------------------
// Put any installed Revit command onto the durable-status asynchronous queue.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class SubmitJobCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        public SubmitJobCommand(Func<string, ICommand> resolve) { _resolve = resolve; }
        public string Name => "horizun_submit_job";
        public string Description => "Queue an installed Revit command and return a persistent job id immediately.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            string tool = request.Value<string>("tool");
            JObject arguments = request["arguments"] as JObject;
            if (string.IsNullOrWhiteSpace(tool)) return CommandResult.Fail("tool is required. Nothing was queued.");
            if (arguments == null) return CommandResult.Fail("arguments must be an object. Nothing was queued.");
            if (string.Equals(tool, Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool, "horizun_execute_python", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("'" + tool + "' cannot be submitted through this generic queue. Nothing was queued.");
            ICommand command = _resolve(tool);
            CommandContract contract = Contract.Find(tool);
            if (command == null || contract == null)
                return CommandResult.Fail("'" + tool + "' is not an installed Revit command. Host-only MCP tools cannot be queued.");
            string permissionReason;
            if (!Settings.IsToolAllowed(contract, out permissionReason))
                return CommandResult.Fail(permissionReason + " Nothing was queued.");

            // The wrapper's durable idempotency claim is established by Dispatcher before
            // this method runs. The child does not need a second key; retaining one would
            // create two unrelated retry identities for one operation.
            JObject queuedArguments = (JObject)arguments.DeepClone();
            queuedArguments.Remove("idempotency_key");
            Job job = Job.Start(tool);
            if (string.IsNullOrWhiteSpace(job.Id))
                return CommandResult.Fail("Could not create the persistent job record. Nothing was queued.");
            string refusal;
            if (!AsyncQueue.TryAdd(new AsyncWork
            {
                JobId = job.Id,
                Command = tool,
                ParamsJson = queuedArguments.ToString(Formatting.None),
                Record = job,
                QueuedUtc = DateTime.UtcNow
            }, out refusal))
            {
                try { job.Finish("not_started", refusal); } catch { }
                return CommandResult.Fail(refusal + " Job " + job.Id + " is recorded as not_started.");
            }
            return CommandResult.Ok(new JObject
            {
                ["mode"] = "async", ["job_id"] = job.Id, ["tool"] = tool, ["status"] = "queued",
                ["queue_depth"] = AsyncQueue.Count, ["queue_capacity"] = AsyncQueue.MaxDepth,
                ["executed"] = false, ["poll_with"] = "horizun_job_status with job_id=" + job.Id,
                ["note"] = "The command has not run yet. Its result, failure, warnings and terminal state will be written to this job record."
            });
        }
    }
}
