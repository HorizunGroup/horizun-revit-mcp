// -----------------------------------------------------------------------------
// Put any installed Revit command onto the durable-status asynchronous queue.
//
// OR AN ORDERED SEQUENCE OF THEM. A sweep over twelve models used to be twelve
// MCP requests the caller had to sequence, each able to time out on its own,
// with no single job to poll. Now one submission carries either:
//
//   * tool + arguments      - one call, exactly as before, unchanged;
//   * sequence              - ordered {key, tool, arguments} entries; or
//   * models                - a read-only sweep, expanded here into that same
//                             sequence: open, audit, close per model.
//
// EXACTLY ONE OF THE THREE. A submission carrying two shapes is refused rather
// than resolved by precedence: letting one win silently runs something other
// than what was asked, and the caller finds out from the result.
//
// The sequence allowlist (JobSequenceRules.Allowed) is what makes a sweep
// read-only. It contains no tool that writes, so a submission naming one is
// refused WHOLE with nothing queued - the guarantee is admission, not the
// runner's good behaviour.
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
            JArray sequence = request["sequence"] as JArray;
            JArray models = request["models"] as JArray;
            bool hasToolShape = !string.IsNullOrWhiteSpace(tool) || arguments != null;

            // A SWEEP IS A SEQUENCE. Expanding the model list here rather than in a
            // second execution path means the read-only allowlist, the step reporting
            // and the stop-at-first-failure rule are the SAME ones, not a parallel set
            // that has to be kept honest by hand.
            if (models != null)
            {
                if (sequence != null)
                    return CommandResult.Fail("a submission carries 'models' or 'sequence', never both. " +
                                              "Nothing was queued.");
                CommandResult expansionRefusal;
                sequence = ExpandModels(models, request["batch"] as JObject, out expansionRefusal);
                if (sequence == null) return expansionRefusal;
            }

            if (sequence != null)
                return SubmitSequence(request, sequence, hasToolShape);

            if (string.IsNullOrWhiteSpace(tool)) return CommandResult.Fail("tool is required. Nothing was queued.");
            if (arguments == null) return CommandResult.Fail("arguments must be an object. Nothing was queued.");
            if (string.Equals(tool, Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool, "horizun_execute_python", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool, "horizun_request_python_access", StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// Admit a whole sequence or refuse it whole, then queue it as ONE job.
        ///
        /// The refusal names the offending index, because "one of your thirty-six
        /// entries is wrong" is not something a caller can act on.
        /// </summary>
        private CommandResult SubmitSequence(JObject request, JArray sequence, bool hasToolShape)
        {
            SequenceAdmission admission = JobSequenceRules.Admit(sequence, hasToolShape);
            if (!admission.Ok) return CommandResult.Fail(admission.Refusal);

            // Every named tool must be installed here too. Admission checks the
            // allowlist; this checks that this Revit actually has them.
            foreach (SequenceEntry e in admission.Entries)
            {
                if (_resolve(e.Tool) == null || Contract.Find(e.Tool) == null)
                    return CommandResult.Fail("sequence step " + e.Key + " names " + e.Tool +
                                              ", which is not an installed Revit command. Nothing was queued.");
                string permissionReason;
                if (!Settings.IsToolAllowed(Contract.Find(e.Tool), out permissionReason))
                    return CommandResult.Fail("sequence step " + e.Key + ": " + permissionReason +
                                              " Nothing was queued.");
            }

            DateTimeOffset? retainUntilUtc;
            CommandResult retainRefusal;
            if (!TryRetention(request, out retainUntilUtc, out retainRefusal)) return retainRefusal;

            Job job;
            string admissionRefusal;
            if (!AsyncJobAdmission.TryOpenProtected(Name, retainUntilUtc, out job, out admissionRefusal))
                return CommandResult.Fail(admissionRefusal);

            string refusal;
            if (!AsyncQueue.TryAdd(new AsyncWork
            {
                JobId = job.Id,
                Command = Name,
                ParamsJson = "{}",
                Sequence = admission.Entries,
                Record = job,
                QueuedUtc = DateTime.UtcNow
            }, out refusal))
            {
                try { job.Finish("not_started", refusal); } catch { }
                return CommandResult.Fail(refusal + " Job " + job.Id + " is recorded as not_started.");
            }

            return CommandResult.Ok(new JObject
            {
                ["mode"] = "async",
                ["job_id"] = job.Id,
                ["tool"] = Name,
                ["status"] = "queued",
                ["steps_submitted"] = admission.Entries.Count,
                ["steps"] = JobSequenceRules.StepsJson(admission.Entries),
                ["queue_depth"] = AsyncQueue.Count,
                ["queue_capacity"] = AsyncQueue.MaxDepth,
                ["executed"] = false,
                ["read_only"] = true,
                ["read_only_means"] = JobSequenceRules.ReadOnlyMeans,
                ["not_run_means"] = JobSequenceRules.NotRunMeans,
                ["poll_with"] = "horizun_job_status with job_id=" + job.Id,
                ["note"] = "Nothing has run yet. Every submitted step appears in the result in every terminal " +
                           "state; a step after a failed one is reported not_run rather than omitted."
            });
        }

        /// <summary>
        /// A read-only sweep, expanded into the sequence it actually is. Returns null
        /// and fills <paramref name="refusal"/> when the model list itself is refused -
        /// which happens BEFORE anything is queued, because a duplicate id or a
        /// downloaded copy offered as a cloud model is a mistake worth catching now
        /// rather than at model nine.
        /// </summary>
        private static JArray ExpandModels(JArray models, JObject batch, out CommandResult refusal)
        {
            refusal = null;
            var list = new System.Collections.Generic.List<BatchModel>();
            for (int i = 0; i < models.Count; i++)
            {
                var o = models[i] as JObject;
                if (o == null)
                {
                    refusal = CommandResult.Fail("models entry " + i + " is not an object. Nothing was queued.");
                    return null;
                }
                list.Add(new BatchModel
                {
                    Id = o.Value<string>("id"),
                    Origin = o.Value<string>("origin") ?? ModelOrigin.Local,
                    LocalPath = o.Value<string>("path"),
                    CloudProjectGuid = o.Value<string>("cloud_project_guid"),
                    CloudModelGuid = o.Value<string>("cloud_model_guid"),
                    CloudRegion = o.Value<string>("cloud_region"),
                    ExpectedTitle = o.Value<string>("expected_title"),
                    ExpectedVersion = o.Value<string>("expected_version"),
                    ProfileVersion = o.Value<string>("profile_version")
                });
            }

            var options = new BatchOptions
            {
                ProfileVersion = batch == null ? null : batch.Value<string>("profile_version")
            };
            BatchPlan plan = BatchAuditRules.Plan(list, options);
            if (!plan.Ok)
            {
                refusal = CommandResult.Fail(plan.Code + ": " + plan.Message + " Nothing was queued.");
                return null;
            }
            return BatchAuditRules.ToSequence(plan, options);
        }

        private static bool TryRetention(JObject request, out DateTimeOffset? retainUntilUtc, out CommandResult refusal)
        {
            retainUntilUtc = null;
            refusal = null;
            JToken retainToken = request["retain_until_utc"];
            if (retainToken == null || retainToken.Type == JTokenType.Null) return true;

            DateTimeOffset parsed;
            if (!TryInstant(retainToken, out parsed) ||
                parsed <= DateTimeOffset.UtcNow ||
                parsed > DateTimeOffset.UtcNow.Add(Job.MaxRetentionLease).AddMinutes(1))
            {
                refusal = CommandResult.Fail("retain_until_utc must be a future UTC instant no more than seven " +
                                             "days away. Nothing was queued.");
                return false;
            }
            retainUntilUtc = parsed.ToUniversalTime();
            return true;
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
