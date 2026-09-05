// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// MANY MODELS, ONE AT A TIME, AND NOTHING SAVED.
//
// A sweep over twelve models is not a new subsystem. It is a model list turned
// into an ORDERED SEQUENCE for the queue that already exists - open, audit,
// close per model - and the executed steps turned back into a per-model report.
// Everything else the bridge already had:
//
//   * the queue and its bounded depth   -> AsyncQueue.cs
//   * the durable record and its steps  -> Job.cs
//   * admission and the read-only rule  -> JobSequenceRules.cs
//   * opening local AND cloud models    -> OpenGuard.cs
//
// So this file is two decisions and nothing else: what a model list must
// satisfy before anything opens, and what the executed steps MEAN.
//
// An earlier draft of this file also carried an in-process sequencer with its
// own session interface. It was deleted: the sequence runner in Dispatcher.cs
// executes the typed tools directly, so the sequencer here was unreachable, and
// its sixteen tests proved the behaviour of code nobody ran while the code that
// does run had no guard at all. That is worse than no tests.
//
// A MODEL THAT WAS NOT OPENED IS `not_assessed`, NEVER CLEAN. This is the
// falsification a consolidated report makes almost inevitable: twelve models,
// one would not open, and the summary reports eleven clean out of eleven. The
// aggregate here counts every model LISTED, so the denominator cannot quietly
// shrink to the ones that cooperated.
//
// NOTHING IS EVER SAVED, and that is admission rather than good intentions: the
// sequence allowlist contains no tool that writes to a model, and every open
// this sweep generates is DETACHED, so the document it holds has no central to
// synchronise with.
//
// AND EVERY CLOSE ASKS TO ACTIVATE ANOTHER DOCUMENT FIRST. Revit cannot close
// the document it is showing; the open activates what it opens and the audit
// refuses to run against anything else, so the model being closed is always the
// active one. Without activate_other every sweep stopped after its first model
// and left that model open.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ModelOrigin
    {
        public const string Local = "local";
        public const string Cloud = "cloud";

        public static readonly string[] All = { Local, Cloud };
    }

    public static class BatchOutcome
    {
        public const string Audited = "audited";
        public const string NotOpened = "not_opened";
        public const string CloseFailed = "close_failed";
        /// <summary>Never looked at. Not clean, not broken - unknown.</summary>
        public const string NotAssessed = "not_assessed";

        public static readonly string[] All = { Audited, NotOpened, CloseFailed, NotAssessed };

        /// <summary>The only outcome in which this model was actually examined.</summary>
        public static bool IsEvidence(string outcome)
        {
            return outcome == Audited;
        }
    }

    public static class BatchRunStatus
    {
        public const string Completed = "completed";
        /// <summary>Ran to the end, and at least one model was never examined.</summary>
        public const string Incomplete = "incomplete";
        public const string StoppedDocumentLeftOpen = "stopped_document_left_open";
        public const string Refused = "refused";
    }

    public static class BatchRefusal
    {
        public const string NoModels = "no_models";
        public const string DuplicateId = "duplicate_model_id";
        public const string NoIdentifier = "model_without_identifier";
        public const string CloudWithoutIdentity = "cloud_model_without_typed_identity";
        public const string LocalPathAsCloud = "local_path_offered_as_cloud";
        public const string CloudIdentityNotAGuid = "cloud_identity_is_not_a_guid";
        public const string UnknownOrigin = "unknown_origin";
        public const string NotDetached = "sweep_must_open_detached";
        public const string NoExpectedTitle = "model_without_expected_title";
    }

    /// <summary>One model to visit. Identity is typed, never a path pretending to be one.</summary>
    public sealed class BatchModel
    {
        public string Id;
        public string Origin = ModelOrigin.Local;
        public string LocalPath;
        /// <summary>Cloud identity: project, model and region, or it is not a cloud model.</summary>
        public string CloudProjectGuid;
        public string CloudModelGuid;
        public string CloudRegion;
        /// <summary>What the opened document must be called. A sweep that audits the wrong file is worse than one that stops.</summary>
        public string ExpectedTitle;
        public string ExpectedVersion;
        /// <summary>Per-model profile, so one sweep can judge models by different rules.</summary>
        public string ProfileVersion;
    }

    public sealed class BatchModelResult
    {
        public string Id;
        public string Outcome;
        public string Why;
        public string DocumentTitle;
        public string ProfileVersionUsed;
        public bool DocumentClosed;
        /// <summary>Where this model's audit reply was stored by the job record.</summary>
        public string ResultRef;
    }

    public sealed class BatchPlan
    {
        public bool Ok;
        public string Code;
        public string Message;
        public List<BatchModel> Models = new List<BatchModel>();
    }

    public sealed class BatchOptions
    {
        /// <summary>Run-wide default when a model names no profile of its own.</summary>
        public string ProfileVersion;
        /// <summary>
        /// A sweep opens DETACHED. Not a preference: detached is what makes "never
        /// synchronises" structural rather than a promise, and a cloud model is a
        /// central model.
        /// </summary>
        public bool Detach = true;
    }

    public sealed class BatchRun
    {
        public string Status;
        public string Why;
        public List<BatchModelResult> Results = new List<BatchModelResult>();
    }

    public static class BatchAuditRules
    {
        public const string SerialMeans =
            "one document at a time, always. Revit has one UI thread and one active document; two models open " +
            "at once inside one Revit is not concurrency, it is a race for the thing every command in this " +
            "bridge depends on knowing. The sweep is one ordered sequence for exactly that reason.";

        public const string NotAssessedMeans =
            "a model that was not opened is not_assessed, NEVER clean. This is the falsification a " +
            "consolidated report makes almost inevitable: twelve models, one would not open, and the summary " +
            "says eleven clean out of eleven. The denominator here is every model LISTED, so it cannot " +
            "quietly shrink to the ones that cooperated.";

        public const string NeverSavesMeans =
            "nothing is saved, synchronised or transmitted, and that is admission rather than good " +
            "intentions: the sequence allowlist contains no tool that writes to a model, and every open this " +
            "sweep generates is DETACHED, so the document it holds has no central to synchronise with.";

        public const string CloudMeans =
            "a cloud model is identified by project guid, model guid and region. A downloaded copy on disk is " +
            "a LOCAL model that resembles it: same geometry, different worksharing, different ownership, and " +
            "no evidence whatsoever about the cloud model's state. Offering one as the other is refused.";

        public const string WrongDocumentMeans =
            "a sweep that audits the wrong document and files the result under this model's id is the worst " +
            "available outcome - a clean report about a file nobody asked about, attributed to one somebody " +
            "did. So every audit and every close NAMES its target, and horizun_audit_model refuses when the " +
            "active document is not the one named.";

        // ---------------------------------------------------------------------
        // PLAN. Everything that can be refused before a single model opens.
        // ---------------------------------------------------------------------

        public static BatchPlan Plan(IEnumerable<BatchModel> models, BatchOptions options)
        {
            var p = new BatchPlan();
            List<BatchModel> list = (models ?? Enumerable.Empty<BatchModel>()).Where(m => m != null).ToList();

            if (list.Count == 0)
            {
                p.Code = BatchRefusal.NoModels;
                p.Message = "no model was listed. An empty sweep is not a sweep that found nothing wrong.";
                return p;
            }

            if (options != null && !options.Detach)
            {
                p.Code = BatchRefusal.NotDetached;
                p.Message = "a read-only sweep opens detached. " + NeverSavesMeans;
                return p;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (BatchModel m in list)
            {
                if (string.IsNullOrWhiteSpace(m.Id))
                {
                    p.Code = BatchRefusal.NoIdentifier;
                    p.Message = "every model needs a stable id, so a result can be attributed and a resumed " +
                                "run can tell which ones are already done.";
                    return p;
                }
                if (!seen.Add(m.Id))
                {
                    p.Code = BatchRefusal.DuplicateId;
                    p.Message = "two models share the id '" + m.Id + "'. A consolidated report keyed on a " +
                                "duplicated id cannot say which model a result came from.";
                    return p;
                }

                if (m.Origin == ModelOrigin.Cloud)
                {
                    if (string.IsNullOrWhiteSpace(m.CloudProjectGuid) ||
                        string.IsNullOrWhiteSpace(m.CloudModelGuid) ||
                        string.IsNullOrWhiteSpace(m.CloudRegion))
                    {
                        p.Code = BatchRefusal.CloudWithoutIdentity;
                        p.Message = "'" + m.Id + "' is declared cloud and lacks a typed identity. " + CloudMeans;
                        return p;
                    }
                    Guid ignored;
                    if (!Guid.TryParse(m.CloudProjectGuid.Trim(), out ignored) ||
                        !Guid.TryParse(m.CloudModelGuid.Trim(), out ignored))
                    {
                        p.Code = BatchRefusal.CloudIdentityNotAGuid;
                        p.Message = "'" + m.Id + "' carries a cloud identity that is not a GUID. Revit builds " +
                                    "a cloud path from GUIDs, so a name here fails at open time rather than now.";
                        return p;
                    }
                    if (!string.IsNullOrWhiteSpace(m.LocalPath))
                    {
                        p.Code = BatchRefusal.LocalPathAsCloud;
                        p.Message = "'" + m.Id + "' is declared cloud and carries a local path. A downloaded " +
                                    "copy is not the cloud model. " + CloudMeans;
                        return p;
                    }
                }
                else if (m.Origin == ModelOrigin.Local)
                {
                    if (string.IsNullOrWhiteSpace(m.LocalPath))
                    {
                        p.Code = BatchRefusal.NoIdentifier;
                        p.Message = "'" + m.Id + "' is a local model with no path.";
                        return p;
                    }
                }
                else
                {
                    p.Code = BatchRefusal.UnknownOrigin;
                    p.Message = "'" + m.Id + "' declares origin '" + m.Origin + "', which is neither local nor " +
                                "cloud. Guessing which one it meant would decide where the sweep looks.";
                    return p;
                }

                if (string.IsNullOrWhiteSpace(m.ExpectedTitle))
                {
                    p.Code = BatchRefusal.NoExpectedTitle;
                    p.Message = "'" + m.Id + "' does not say what the opened document must be called. Both the " +
                                "audit and the close name their target, and a sweep that cannot name it acts " +
                                "on whatever document happens to be in front. " + WrongDocumentMeans;
                    return p;
                }
            }

            p.Ok = true;
            p.Models = list;
            return p;
        }

        /// <summary>
        /// Which models a resumed sweep still has to visit: everything that was not
        /// actually examined. A model whose open failed IS retried - the previous run
        /// learned nothing about it - and an audited one is not.
        /// </summary>
        public static List<BatchModel> Remaining(BatchPlan plan, IEnumerable<BatchModelResult> done)
        {
            var finished = new HashSet<string>(
                (done ?? Enumerable.Empty<BatchModelResult>())
                    .Where(r => r != null && BatchOutcome.IsEvidence(r.Outcome))
                    .Select(r => r.Id),
                StringComparer.Ordinal);

            if (plan == null || !plan.Ok) return new List<BatchModel>();
            return plan.Models.Where(m => !finished.Contains(m.Id)).ToList();
        }

        // ---------------------------------------------------------------------
        // THE SWEEP AS A JOB SEQUENCE. This is what makes the batch real rather
        // than a shape: the model list becomes ordered entries for the queue that
        // already exists, and that queue's own read-only allowlist is what keeps a
        // sweep from writing. No second queue, and no second guarantee.
        // ---------------------------------------------------------------------

        public const string SequenceMeans =
            "the sweep IS a job sequence: open, audit, close per model, in order, on the queue that already " +
            "exists. Every entry names a tool from the sequence allowlist, which contains nothing that writes, " +
            "so the read-only guarantee is the queue's admission rather than this file's good intentions.";

        public static JArray ToSequence(BatchPlan plan, BatchOptions options)
        {
            var seq = new JArray();
            if (plan == null || !plan.Ok) return seq;
            options = options ?? new BatchOptions();

            foreach (BatchModel m in plan.Models)
            {
                var open = new JObject { ["detach"] = true };
                if (m.Origin == ModelOrigin.Cloud)
                {
                    open["cloud_project_guid"] = m.CloudProjectGuid;
                    open["cloud_model_guid"] = m.CloudModelGuid;
                    open["cloud_region"] = m.CloudRegion;
                }
                else
                {
                    open["path"] = m.LocalPath;
                }
                if (!string.IsNullOrWhiteSpace(m.ExpectedVersion)) open["expected_version"] = m.ExpectedVersion;

                seq.Add(new JObject
                {
                    ["key"] = m.Id + ".open",
                    ["tool"] = "horizun_open_document",
                    ["arguments"] = open
                });

                // The audit NAMES its document. horizun_audit_model refuses when the
                // active document is not the one named, so the wrong-document guard is
                // the tool's own rather than a check this sweep hopes it remembered.
                var audit = new JObject { ["target_document"] = m.ExpectedTitle };
                string profile = string.IsNullOrWhiteSpace(m.ProfileVersion) ? options.ProfileVersion : m.ProfileVersion;
                if (!string.IsNullOrWhiteSpace(profile)) audit["profile_version"] = profile;
                seq.Add(new JObject
                {
                    ["key"] = m.Id + ".audit",
                    ["tool"] = "horizun_audit_model",
                    ["arguments"] = audit
                });

                seq.Add(new JObject
                {
                    ["key"] = m.Id + ".close",
                    ["tool"] = "horizun_document_session",
                    ["arguments"] = new JObject
                    {
                        ["operation"] = "close",
                        ["target_document"] = m.ExpectedTitle,
                        // WITHOUT THIS THE SWEEP CANNOT CLOSE ANYTHING IT OPENED.
                        // The open ACTIVATES what it opens, and the audit refuses
                        // unless its target is the active document - so at this
                        // point the model being closed is always the active one,
                        // and Revit's API cannot close the active document. The
                        // close was refused every time, models after the first
                        // were reported not_run, and a detached copy stayed open
                        // in somebody's Revit while the reply said read_only.
                        //
                        // activate_other is asked for rather than assumed because
                        // activation changes what the user is looking at; a sweep
                        // that opens documents is exactly the caller that has to
                        // ask. The command reports which document it activated.
                        ["activate_other"] = true
                    }
                });
            }
            return seq;
        }

        /// <summary>
        /// The consolidated per-model report, read back off the executed steps.
        ///
        /// The mapping is where a sweep lies most easily: a model whose open failed has
        /// two more steps reported not_run, and counting those as "nothing found" is
        /// exactly how eleven clean out of eleven happens. Only a model whose audit
        /// SUCCEEDED is audited; everything else names what stopped it.
        /// </summary>
        public static BatchRun Consolidate(BatchPlan plan, IEnumerable<SequenceEntry> steps)
        {
            var run = new BatchRun();
            if (plan == null || !plan.Ok)
            {
                run.Status = BatchRunStatus.Refused;
                run.Why = plan == null ? "no plan." : plan.Message;
                return run;
            }

            var byKey = new Dictionary<string, SequenceEntry>(StringComparer.Ordinal);
            foreach (SequenceEntry e in steps ?? Enumerable.Empty<SequenceEntry>())
                if (e != null && e.Key != null) byKey[e.Key] = e;

            bool anyStopped = false;
            foreach (BatchModel m in plan.Models)
            {
                SequenceEntry open = Step(byKey, m.Id + ".open");
                SequenceEntry audit = Step(byKey, m.Id + ".audit");
                SequenceEntry close = Step(byKey, m.Id + ".close");

                var r = new BatchModelResult
                {
                    Id = m.Id,
                    DocumentTitle = m.ExpectedTitle,
                    ProfileVersionUsed = string.IsNullOrWhiteSpace(m.ProfileVersion) ? null : m.ProfileVersion,
                    DocumentClosed = close != null && close.Status == StepStatus.Succeeded
                };

                if (open == null || open.Status == StepStatus.NotRun || open.Status == StepStatus.Queued)
                {
                    // NEVER OPENED AT ALL, so there is nothing to have left behind.
                    r.Outcome = BatchOutcome.NotAssessed;
                    r.Why = "this model was never opened. " + NotAssessedMeans;
                    r.DocumentClosed = true;
                    anyStopped = true;
                }
                else if (open.Status != StepStatus.Succeeded)
                {
                    r.Outcome = BatchOutcome.NotOpened;
                    r.Why = open.Error;
                    r.DocumentClosed = true;
                    anyStopped = true;
                }
                else if (audit == null || audit.Status != StepStatus.Succeeded)
                {
                    // OPENED AND NOT AUDITED. The document existed, so whether it closed
                    // is a real question rather than a formality.
                    r.Outcome = BatchOutcome.NotAssessed;
                    r.Why = "the model opened and was not audited: " +
                            (audit == null ? "no audit step was executed." : (audit.Error ?? audit.Status)) +
                            " " + NotAssessedMeans;
                    anyStopped = true;
                }
                else
                {
                    r.Outcome = BatchOutcome.Audited;
                    r.ResultRef = audit.ResultRef;
                }

                if (close != null && close.Status == StepStatus.Failed)
                {
                    r.Outcome = BatchOutcome.CloseFailed;
                    r.Why = "the document was left open: " + close.Error + " " + SerialMeans;
                    r.DocumentClosed = false;
                    anyStopped = true;
                }

                run.Results.Add(r);
            }

            bool leftOpen = run.Results.Any(x => !x.DocumentClosed);
            run.Status = leftOpen ? BatchRunStatus.StoppedDocumentLeftOpen
                       : anyStopped ? BatchRunStatus.Incomplete
                       : BatchRunStatus.Completed;
            run.Why = anyStopped ? "at least one model was not audited. " + NotAssessedMeans : null;
            return run;
        }

        private static SequenceEntry Step(Dictionary<string, SequenceEntry> byKey, string key)
        {
            SequenceEntry e;
            return byKey.TryGetValue(key, out e) ? e : null;
        }

        // ---------------------------------------------------------------------
        // AGGREGATE. The denominator is every model LISTED.
        // ---------------------------------------------------------------------

        public static JObject Aggregate(BatchPlan plan, BatchRun run)
        {
            List<BatchModelResult> all = run == null
                ? new List<BatchModelResult>()
                : run.Results.Where(r => r != null).ToList();
            int listed = plan == null || !plan.Ok ? 0 : plan.Models.Count;

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (string s in BatchOutcome.All) counts[s] = 0;
            foreach (BatchModelResult r in all)
                if (r.Outcome != null && counts.ContainsKey(r.Outcome)) counts[r.Outcome]++;

            // Models listed and never reported are not_assessed, counted here rather
            // than dropped out of the denominator.
            var reported = new HashSet<string>(all.Select(r => r.Id), StringComparer.Ordinal);
            long unreported = plan == null || !plan.Ok
                ? 0 : plan.Models.Count(m => !reported.Contains(m.Id));
            counts[BatchOutcome.NotAssessed] += unreported;

            long audited = counts[BatchOutcome.Audited];
            var o = new JObject
            {
                ["status"] = run == null ? BatchRunStatus.Refused : run.Status,
                ["why"] = run == null ? null : run.Why,
                ["models_listed"] = listed,
                ["models_audited"] = audited,
                ["models_not_assessed"] = counts[BatchOutcome.NotAssessed],
                // True only when EVERY listed model was examined. A rate over the ones
                // that opened is the number that turns a failed open into a clean model.
                ["all_models_assessed"] = listed > 0 && audited == listed,
                ["documents_left_open"] = all.Count(r => !r.DocumentClosed),
                ["serial_means"] = SerialMeans,
                ["not_assessed_means"] = NotAssessedMeans,
                ["never_saves_means"] = NeverSavesMeans,
                ["sequence_means"] = SequenceMeans
            };
            var by = new JObject();
            foreach (string s in BatchOutcome.All) by[s] = counts[s];
            o["by_outcome"] = by;
            o["models"] = new JArray(all.Select(r => (JToken)ToJson(r)));
            return o;
        }

        public static JObject ToJson(BatchModelResult r)
        {
            if (r == null) return null;
            return new JObject
            {
                ["model_id"] = r.Id,
                ["outcome"] = r.Outcome,
                ["why"] = r.Why,
                ["document_title"] = r.DocumentTitle,
                ["profile_version"] = r.ProfileVersionUsed,
                ["document_closed"] = r.DocumentClosed,
                ["result_ref"] = r.ResultRef
            };
        }
    }
}
