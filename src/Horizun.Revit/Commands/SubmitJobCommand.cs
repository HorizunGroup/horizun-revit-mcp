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

            DateTimeOffset? retainUntilUtc = null;
            JToken retainToken = request["retain_until_utc"];
            if (retainToken != null && retainToken.Type != JTokenType.Null)
            {
                DateTimeOffset parsed;
                if (!TryInstant(retainToken, out parsed) ||
                    parsed <= DateTimeOffset.UtcNow ||
                    parsed > DateTimeOffset.UtcNow.Add(Job.MaxRetentionLease).AddMinutes(1))
                    return CommandResult.Fail("retain_until_utc must be a future UTC instant no more than seven days away. Nothing was queued.");
                retainUntilUtc = parsed.ToUniversalTime();
            }

            // The wrapper's durable idempotency claim is established by Dispatcher before
            // this method runs. The child does not need a second key; retaining one would
            // create two unrelated retry identities for one operation.
            JObject queuedArguments = (JObject)arguments.DeepClone();
            queuedArguments.Remove("idempotency_key");
            // The record before the queue, and the record is not optional here. The old
            // guard tested job.Id, which Job.Start assigned before anything could fail,
            // so it never fired: an unwritable jobs directory produced a queued command
            // and a job_id that addressed nothing.
            Job job;
            string admissionRefusal;
            if (!AsyncJobAdmission.TryOpenProtected(tool, retainUntilUtc, out job, out admissionRefusal))
                return CommandResult.Fail(admissionRefusal);

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

        private static bool TryInstant(JToken token, out DateTimeOffset parsed)
        {
            parsed = default(DateTimeOffset);
            if (token?.Type == JTokenType.Date && token is JValue value)
            {
                if (value.Value is DateTimeOffset dto) { parsed = dto.ToUniversalTime(); return true; }
                if (value.Value is DateTime dt)
                {
                    if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    parsed = new DateTimeOffset(dt).ToUniversalTime();
                    return true;
                }
            }
            return token?.Type == JTokenType.String && DateTimeOffset.TryParse((string)token,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal, out parsed);
        }
    }
}
