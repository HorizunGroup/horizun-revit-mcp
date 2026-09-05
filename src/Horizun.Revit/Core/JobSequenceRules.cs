// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE JOB, SEVERAL MODELS, IN ORDER - on the queue that already exists.
//
// A sweep over twelve models used to be twelve MCP requests the caller had to
// sequence, each able to time out on its own, with no single job to poll and no
// consolidated answer. This makes it one job whose work is an ordered sequence,
// reusing horizun_submit_job's admission, AsyncQueue's bounded depth and Job's
// durable record. Nothing here is a second queue.
//
// THE ALLOWLIST IS THE READ-ONLY GUARANTEE. A sequence may open a document,
// read it, and close it. It may not write to one - not because the runner
// promises to be careful, but because no writing tool is admissible in a
// sequence entry, and a submission containing one is refused WHOLE with nothing
// queued. horizun_document_session is admitted only for operation "close": the
// same tool can save-as and activate, and a sweep has no business doing either.
//
// A STEP AFTER A FAILURE IS `not_run`, NEVER OMITTED AND NEVER SUCCEEDED. This
// is the reporting failure that matters: a sequence that stops at step three and
// returns two steps reads as a two-step sequence that worked. Every submitted
// entry appears in the reply in every terminal state.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class StepStatus
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        /// <summary>Never started, because an earlier step failed. Not a pass.</summary>
        public const string NotRun = "not_run";

        public static readonly string[] All = { Queued, Running, Succeeded, Failed, NotRun };
    }

    public sealed class SequenceEntry
    {
        public string Key;
        public string Tool;
        public JObject Arguments = new JObject();

        public string Status = StepStatus.Queued;
        public string StartedUtc;
        public string FinishedUtc;
        public string ResultRef;
        public string Error;

        public JObject ToJson()
        {
            return new JObject
            {
                ["key"] = Key,
                ["tool"] = Tool,
                ["status"] = Status,
                ["started_utc"] = StartedUtc,
                ["finished_utc"] = FinishedUtc,
                ["result_ref"] = ResultRef,
                ["error"] = Error
            };
        }
    }

    public sealed class SequenceAdmission
    {
        public bool Ok;
        public string Refusal;
        public List<SequenceEntry> Entries = new List<SequenceEntry>();
    }

    public static class JobSequenceRules
    {
        /// <summary>
        /// Every tool a sequence may name. READ AND NAVIGATE ONLY - there is no write
        /// here, and that is the whole guarantee rather than a habit of the runner.
        /// </summary>
        public static readonly string[] Allowed =
        {
            "horizun_open_document",
            "horizun_model_scan",
            "horizun_audit_model",
            "horizun_quantities",
            "horizun_document_session"
        };

        /// <summary>A sweep of this length already holds Revit for a long time.</summary>
        public const int MaxEntries = 200;

        public const string ReadOnlyMeans =
            "a sequence may open a document, read it and close it. No tool that writes to a model is " +
            "admissible in one, so a sweep cannot modify what it audits - and a submission naming one is " +
            "refused whole, with nothing queued, rather than refused at the step that would have run it.";

        public const string NotRunMeans =
            "a step after a failed step is reported not_run: never omitted, never succeeded. A sequence that " +
            "stops at step three and returns two steps reads as a two-step sequence that worked.";

        /// <summary>
        /// Admit a whole sequence or refuse it whole. The refusal names the index,
        /// because "one of your twelve entries is wrong" is not actionable.
        /// </summary>
        public static SequenceAdmission Admit(JArray sequence, bool hasToolShape)
        {
            var a = new SequenceAdmission();

            if (hasToolShape)
                return Refuse(a, "a submission carries either 'tool' and 'arguments' or a 'sequence', never " +
                                 "both. Letting one win silently would run something other than what was " +
                                 "asked. Nothing was queued.");
            if (sequence == null || sequence.Count == 0)
                return Refuse(a, "'sequence' must contain at least one entry. An empty sequence is not a " +
                                 "sequence that did nothing wrong. Nothing was queued.");
            if (sequence.Count > MaxEntries)
                return Refuse(a, "a sequence is limited to " + MaxEntries + " entries and this one has " +
                                 sequence.Count + ". Nothing was queued.");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sequence.Count; i++)
            {
                var o = sequence[i] as JObject;
                if (o == null) return Refuse(a, At(i, "is not an object."));

                string key = o.Value<string>("key");
                string tool = o.Value<string>("tool");
                var args = o["arguments"] as JObject;

                if (string.IsNullOrWhiteSpace(key)) return Refuse(a, At(i, "has no 'key'."));
                if (!keys.Add(key))
                    return Refuse(a, At(i, "repeats the key '" + key + "'. Steps are reported by key, and two " +
                                          "steps sharing one cannot be told apart in the result."));
                if (string.IsNullOrWhiteSpace(tool)) return Refuse(a, At(i, "has no 'tool'."));
                if (args == null) return Refuse(a, At(i, "has no 'arguments' object."));

                if (!Allowed.Contains(tool, StringComparer.Ordinal))
                    return Refuse(a, At(i, "names '" + tool + "', which is not admissible in a sequence. " +
                                          ReadOnlyMeans + " Admissible: " + string.Join(", ", Allowed) + "."));

                // document_session can save-as and activate. Only close is a read-only act.
                if (tool == "horizun_document_session")
                {
                    string op = args.Value<string>("operation");
                    if (!string.Equals(op, "close", StringComparison.Ordinal))
                        return Refuse(a, At(i, "uses horizun_document_session with operation '" +
                                              (op ?? "<none>") + "'. Only 'close' is admissible in a sequence: " +
                                              "the same tool can save-as and activate, and a read-only sweep " +
                                              "has no business doing either."));
                }

                a.Entries.Add(new SequenceEntry
                {
                    Key = key,
                    Tool = tool,
                    Arguments = (JObject)args.DeepClone(),
                    Status = StepStatus.Queued
                });
            }

            a.Ok = true;
            return a;
        }

        /// <summary>
        /// A refusal carries NOTHING. The entries parsed before the offending one are
        /// discarded rather than handed back: a partial list is exactly the shape a
        /// caller would mistake for "the admitted subset", and there is no such thing -
        /// a sequence is admitted whole or not at all.
        /// </summary>
        private static SequenceAdmission Refuse(SequenceAdmission a, string refusal)
        {
            a.Ok = false;
            a.Entries.Clear();
            a.Refusal = refusal;
            return a;
        }

        private static string At(int index, string what)
        {
            return "sequence entry " + index + " " + what + " Nothing was queued.";
        }

        /// <summary>
        /// What every step's status is once execution has stopped. Called after a
        /// failure and at the end: a step never started because an earlier one failed
        /// is not_run, and one still marked running when the record closes is failed -
        /// a step that was running when the process died did not succeed.
        /// </summary>
        public static void SettleAfterStop(IList<SequenceEntry> entries, int stoppedAt)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                SequenceEntry e = entries[i];
                if (i <= stoppedAt) continue;
                if (e.Status == StepStatus.Queued || e.Status == StepStatus.Running)
                {
                    e.Status = StepStatus.NotRun;
                    e.Error = "an earlier step failed, so this one never ran. " + NotRunMeans;
                }
            }
        }

        /// <summary>The reply shape. Every submitted entry appears, in submission order.</summary>
        public static JArray StepsJson(IEnumerable<SequenceEntry> entries)
        {
            return new JArray((entries ?? Enumerable.Empty<SequenceEntry>()).Select(e => (JToken)e.ToJson()));
        }

        /// <summary>
        /// The terminal state of the whole sequence. Any failed step fails the job:
        /// eleven succeeded steps and one failed is not a successful sweep.
        /// </summary>
        public static string TerminalStatus(IEnumerable<SequenceEntry> entries)
        {
            List<SequenceEntry> all = (entries ?? Enumerable.Empty<SequenceEntry>()).ToList();
            if (all.Count == 0) return "failed";
            if (all.Any(e => e.Status == StepStatus.Failed)) return "failed";
            if (all.Any(e => e.Status != StepStatus.Succeeded)) return "failed";
            return "ok";
        }
    }
}
