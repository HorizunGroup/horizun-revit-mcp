// -----------------------------------------------------------------------------
// Horizun MCP - working the repair memory. Original Horizun code.
//
// The reader and writer for Core/RepairMemory.cs. HOST-RESIDENT: the memory is
// bridge state under the Horizun data root, not model state, so this answers in
// the server process and never needs a Revit at all - which matters, because the
// moment somebody most wants to ask "has this failure been seen before?" is the
// moment Revit is busy or gone.
//
// FIVE OPERATIONS AND NOT ONE OF THEM ACTS ON A MODEL:
//
//   list          what has been seen, newest first.
//   advice        what this exact failure shape's history says. Advice, never an
//                 action: a memory that applied its own remedy would be a second
//                 decision maker with no permission model and no dry run.
//   observe       record that a failure of this shape happened. Refused when the
//                 message could not be normalised safely - a message the redactor
//                 could not clean is a message that does not enter the file.
//   remedy        record what a person found worked. Recording the same text twice
//                 confirms it; that is the ONLY way a remedy becomes validated.
//   quarantine    refuse this shape until somebody lifts it. Needs a reason and an
//                 author, and `release` undoes it.
//
// WHY A SEPARATE TOOL AND NOT A FIELD ON EVERY ERROR. Two reasons. Consulting a
// memory on every failure would make every failure slower and would put a
// suggestion in front of a caller who did not ask for one. And a memory that
// writes itself on every failure is a memory that fills with the noise of one bad
// afternoon. Observation is an explicit act, which keeps the file worth reading.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class RepairMemoryTool
    {
        public const string Name = "horizun_repair_memory";
        /// <summary>How long another process's hold on the memory is waited out before refusing.</summary>
        internal static TimeSpan MutexWait = TimeSpan.FromSeconds(10);

        public static JObject Handle(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string operation = (arguments?.Value<string>("operation") ?? "list").ToLowerInvariant();
            string path = RepairMemory.Path();
            string nowUtc = DateTime.UtcNow.ToString("o");

            // ACROSS PROCESSES. Claude's and Codex's servers each load, change and save
            // this one file; an in-process lock let their cycles interleave.
            using (var mutex = new Mutex(false, RepairMemory.MutexName(path)))
            {
                bool held;
                try { held = mutex.WaitOne(MutexWait); }
                catch (AbandonedMutexException) { held = true; }   // the other process died holding it; the file is whole (atomic writes)
                if (!held)
                    throw new ToolRefusal("Another Horizun process is updating the repair memory at " + path +
                                          " and did not finish within " + MutexWait.TotalSeconds + " s. Nothing was " +
                                          "read or written; try again.");
                try
                {
                    Dictionary<string, RepairEntry> entries = RepairMemory.Load(path, out string problem);
                    JObject result;
                    switch (operation)
                    {
                        case "list": result = List(entries, arguments); break;
                        case "advice": result = Advice(entries, arguments); break;
                        case "observe": result = Observe(entries, arguments, path, nowUtc); break;
                        case "remedy": result = Remedy(entries, arguments, path, nowUtc); break;
                        case "quarantine": result = Quarantine(entries, arguments, path, nowUtc); break;
                        case "release": result = Release(entries, arguments, path); break;
                        default:
                            throw new ToolRefusal(
                                "operation must be list, advice, observe, remedy, quarantine or release; '" +
                                operation + "' is not one of them.");
                    }
                    if (problem != null) result["memory_problem"] = problem;
                    return result;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        // =====================================================================

        private static JObject List(Dictionary<string, RepairEntry> entries, JObject arguments)
        {
            string state = arguments?.Value<string>("state");
            string tool = arguments?.Value<string>("tool");
            int limit = arguments?.Value<int?>("limit") ?? 50;
            if (limit < 1 || limit > RepairMemory.MaxEntries) limit = 50;

            IEnumerable<RepairEntry> rows = entries.Values;
            if (!string.IsNullOrWhiteSpace(state))
                rows = rows.Where(e => string.Equals(e.State, state, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(tool))
                rows = rows.Where(e => string.Equals(e.Tool, tool, StringComparison.OrdinalIgnoreCase));

            List<RepairEntry> ordered = rows.OrderByDescending(e => e.LastSeenUtc ?? "").Take(limit).ToList();
            return new JObject
            {
                ["path"] = RepairMemory.Path(),
                ["total"] = entries.Count,
                ["shown"] = ordered.Count,
                ["quarantined"] = entries.Values.Count(e => e.State == RepairMemory.StateQuarantined),
                ["entries"] = new JArray(ordered.Select(RepairMemory.ToJson)),
                ["means"] =
                    "Failure SHAPES, not failure messages: every path, quoted name, identifier and number was " +
                    "replaced before anything was written. Nothing about any model can be reconstructed from " +
                    "these rows."
            };
        }

        private static JObject Advice(Dictionary<string, RepairEntry> entries, JObject arguments)
        {
            string tool = Required(arguments, "tool");
            string failureClass = arguments?.Value<string>("failure_class") ?? "";
            string message = Required(arguments, "message");

            JObject advice = RepairMemory.Advice(entries, tool, failureClass, message);
            return new JObject
            {
                ["shape"] = RepairMemory.ShapeOf(tool, failureClass, message),
                ["normalized_message"] = RepairMemory.Normalize(message),
                ["known"] = advice != null,
                ["advice"] = advice == null ? (JToken)JValue.CreateNull() : advice,
                ["means"] = advice == null
                    ? "this failure shape has not been recorded. That is not evidence that it is new - only " +
                      "that nobody wrote it down here."
                    : "what a PERSON recorded about this shape. Nothing applies it for you, and a shared shape " +
                      "is not proof of a shared cause."
            };
        }

        private static JObject Observe(Dictionary<string, RepairEntry> entries, JObject arguments,
                                       string path, string nowUtc)
        {
            string tool = Required(arguments, "tool");
            string failureClass = arguments?.Value<string>("failure_class") ?? "";
            string message = Required(arguments, "message");

            RepairEntry entry = RepairMemory.Observe(entries, tool, failureClass, message, nowUtc);
            if (entry == null)
                throw new ToolRefusal(
                    "that message still contains something specific after redaction - a path, a quoted name or " +
                    "a long number - so NOTHING was written. This memory stores shapes, not models, and it " +
                    "fails closed: losing one remembered failure costs nothing, and writing a client's project " +
                    "number into a file that outlives the project costs a great deal.");

            string fingerprint = CallFingerprintOf(entry.Tool, arguments);
            if (fingerprint != null) RepairMemory.AttachCall(entry, fingerprint);

            RepairMemory.Save(path, entries.Values);
            return new JObject
            {
                ["shape"] = entry.Shape,
                ["occurrences"] = entry.Occurrences,
                ["calls_recorded"] = entry.ArgumentFingerprints.Count,
                ["state"] = entry.State,
                ["normalized_message"] = entry.NormalizedMessage,
                ["suggest_attention"] = entry.Occurrences >= RepairMemory.SuggestAfterOccurrences &&
                                        string.IsNullOrWhiteSpace(entry.Remedy),
                ["means"] = "the SHAPE was recorded. The original message was never written anywhere" +
                            (fingerprint == null
                                ? ", and no call was recorded (call_arguments was not given), so a quarantine of " +
                                  "this shape could not refuse anything."
                                : "; the call was recorded as a hash of its normalised arguments, never the arguments.")
            };
        }

        private static JObject Remedy(Dictionary<string, RepairEntry> entries, JObject arguments,
                                      string path, string nowUtc)
        {
            RepairEntry entry = Find(entries, arguments);
            string error = RepairMemory.RecordRemedy(entry, arguments?.Value<string>("remedy"),
                                                     arguments?.Value<string>("author"), nowUtc);
            if (error != null) throw new ToolRefusal(error);

            RepairMemory.Save(path, entries.Values);
            return new JObject
            {
                ["shape"] = entry.Shape,
                ["state"] = entry.State,
                ["confirmations"] = entry.RemedyConfirmations,
                ["means"] = entry.State == RepairMemory.StateValidated
                    ? "the same remedy was recorded again, which is the only thing that validates one. Nothing " +
                      "here applies it."
                    : "recorded as PROPOSED. It becomes validated when somebody records the same text again " +
                      "after it worked a second time."
            };
        }

        private static JObject Quarantine(Dictionary<string, RepairEntry> entries, JObject arguments,
                                          string path, string nowUtc)
        {
            RepairEntry entry = Find(entries, arguments);
            string fingerprint = CallFingerprintOf(entry.Tool, arguments);
            string error = RepairMemory.Quarantine(entry, arguments?.Value<string>("reason"),
                                                   arguments?.Value<string>("author"), nowUtc);
            if (error != null) throw new ToolRefusal(error);
            if (fingerprint != null) RepairMemory.AttachCall(entry, fingerprint);

            RepairMemory.Save(path, entries.Values);
            int refuses = entry.ArgumentFingerprints.Count;
            return new JObject
            {
                ["shape"] = entry.Shape,
                ["state"] = entry.State,
                ["quarantined_at_utc"] = entry.QuarantinedAtUtc,
                ["refuses_calls"] = refuses,
                ["means"] = refuses == 0
                    ? "this exact shape is now marked do-not-retry, but NO CALL is refused: none was recorded " +
                      "against it. The bridge cannot refuse by shape alone - a shape is the failure's own " +
                      "message, known only once a call has failed. Pass the failing call's arguments as " +
                      "call_arguments (to observe or to quarantine) and that call is refused from then on. " +
                      "advice on this shape answers QUARANTINED with the reason, who set it and when. It is " +
                      "reversible with operation=release and does not expire silently."
                    : "this exact shape is now marked do-not-retry, and the " + refuses + " call(s) recorded " +
                      "against it - the same tool with the same arguments, idempotency_key and " +
                      "confirmation_token aside - are now refused before they reach Revit, with code " +
                      RepairMemory.CallQuarantinedCode + ". Any other call still runs. advice on this shape " +
                      "answers QUARANTINED with the reason, who set it and when. It is reversible with " +
                      "operation=release and does not expire silently."
            };
        }

        private static JObject Release(Dictionary<string, RepairEntry> entries, JObject arguments, string path)
        {
            RepairEntry entry = Find(entries, arguments);
            string error = RepairMemory.Release(entry);
            if (error != null) throw new ToolRefusal(error);

            RepairMemory.Save(path, entries.Values);
            return new JObject
            {
                ["shape"] = entry.Shape,
                ["state"] = entry.State,
                ["means"] = "the quarantine is lifted and the shape is back to what its remedy state was."
            };
        }

        // =====================================================================

        private static RepairEntry Find(Dictionary<string, RepairEntry> entries, JObject arguments)
        {
            string shape = arguments?.Value<string>("shape");
            if (string.IsNullOrWhiteSpace(shape))
                throw new ToolRefusal("shape is required; take it from operation=list or operation=advice.");
            RepairEntry entry;
            if (!entries.TryGetValue(shape, out entry))
                throw new ToolRefusal("no remembered shape with id '" + shape + "'.");
            return entry;
        }

        /// <summary>
        /// The fingerprint of the call named by call_arguments, or null when none was
        /// given. Only the hash leaves this method; the arguments are never stored.
        /// </summary>
        private static string CallFingerprintOf(string tool, JObject arguments)
        {
            JToken given = arguments?["call_arguments"];
            if (given == null || given.Type == JTokenType.Null) return null;
            if (given.Type != JTokenType.Object)
                throw new ToolRefusal("call_arguments must be the failing call's arguments object, exactly as " +
                                      "it was sent; it is " + given.Type + ". Nothing was written.");
            if (string.Equals(tool, Name, StringComparison.Ordinal))
                throw new ToolRefusal("the repair memory's own calls are never refused, so call_arguments " +
                                      "cannot be recorded for " + Name + ". Nothing was written.");
            return RepairMemory.CallFingerprint(tool, (JObject)given);
        }

        private static string Required(JObject arguments, string field)
        {
            string value = arguments?.Value<string>(field);
            if (string.IsNullOrWhiteSpace(value)) throw new ToolRefusal(field + " is required.");
            return value;
        }
    }
}
