// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_apply_corrections - the Model Doctor's correction cycle, closed:
//
//     diagnose (horizun_audit_model) -> select -> rehearse -> approve -> apply
//     -> re-audit
//
// THE DOCTOR STAYS READ-ONLY; THIS IS THE SEPARATE CALL. horizun_audit_model
// proposes and executes nothing. This command takes the audit's
// finding_set_fingerprint and the finding ids a person chose, and runs each
// correction THROUGH the typed command the registry names - never around it.
// The typed command rehearses, writes inside its own transaction, and re-reads
// the model after the commit; that re-read is the evidence, and this command
// adds one more: the audit's own check, re-run after the apply, per finding.
//
// FOUR THINGS THE APPLY PROVES BEFORE IT WRITES, and every one refuses by name:
//
//   * the audit exists in this session and was taken on THIS document;
//   * every cited check, re-run now, still produces the finding id that was
//     approved - a model that moved is a stale plan, whatever the token says;
//   * every action rehearsed cleanly through its typed tool, and the child
//     plans it resolved are the ones bound in the token;
//   * the token was issued for this document, this finding set and this exact
//     action set, and has not been spent.
//
// ROLLBACK SCOPE, STATED: EACH ACTION IS ITS OWN TRANSACTION. A pin that fails
// does not undo the deletion before it, and does not stop the reload after it.
// This is not an atomic plan and does not claim to be one - horizun_execute_plan
// exists for callers who need one group, and horizun_manage_links is now on
// its allowlist for exactly that composition.
//
// horizun_execute_python is not in the registry and cannot be reached from here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ApplyCorrectionsCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        public ApplyCorrectionsCommand(Func<string, ICommand> resolve) { _resolve = resolve; }

        public string Name => "horizun_apply_corrections";
        public string Description =>
            "Select findings from a horizun_audit_model run by finding_id, rehearse each correction through the " +
            "typed command the registry names, confirm with the token the rehearsal issued, apply one " +
            "transaction per action, and re-audit the intervened checks.";

        /// <summary>
        /// WHAT idempotency_key DOES HERE - and what it was WRONGLY said to do.
        ///
        /// This command used to declare that nothing replays the key: "no reply is
        /// recorded against it". That was measured false on 2026-09-03, Revit 2026:
        /// re-sending an applied call with the same key came back
        /// idempotency.status = replayed, command_executed_in_this_call = false, and
        /// nothing ran. The DISPATCHER's durable ledger records every mutating call,
        /// this one included; the claim was about what the COMMAND keeps, and a caller
        /// cannot see that distinction and should not have to.
        ///
        /// So the disclaimer is gone and the shared sentence - a retry with the same
        /// key returns the recorded result without executing twice - is simply true.
        /// The reply carries ONE idempotency block, the dispatcher's, rather than two
        /// that disagreed depending on whether the call was a replay.
        ///
        /// What ALSO prevents a second application, and is stronger than a replay
        /// cache: the confirmation token is single use and the cited checks are re-run
        /// before the apply, so the same actions under a NEW key are refused as a spent
        /// token or as a stale plan, with nothing written.
        /// </summary>

        // THE DEFINITION LIVES WITH THE LOOP THAT IMPLEMENTS IT, in Core and
        // Revit-free, so the sentence and the behaviour cannot drift apart.
        public const string RollbackScope = CorrectionApplyLoop.RollbackScope;
        public const string RollbackMeans = CorrectionApplyLoop.RollbackMeans;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // THE SHAPE FIRST. Unknown keys, an empty selection and a malformed action
            // are refused before any document is looked at.
            ScanRequestVerdict shape = CorrectionRequestRules.Check(request);
            if (!shape.Ok) return CommandResult.Fail(Name + ": " + shape.Message);

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            string fingerprint = gate.Identity?.FingerprintDigest();

            // THE AUDIT, from this session's record and not from the caller's copy.
            string setFingerprint = (string)request["finding_set_fingerprint"];
            FindingSetRecord record;
            if (!AuditFindingSetStore.Session.TryGet(setFingerprint, out record))
                return CommandResult.FailWithDetail(
                    Name + ": no audit with finding_set_fingerprint " + setFingerprint + " exists in this Revit " +
                    "session. Finding sets live as long as the session, like confirmation tokens; run " +
                    "horizun_audit_model again and cite the fingerprint it returns. Nothing was changed.",
                    new JObject { ["state"] = "refused", ["code"] = "unknown_finding_set" });

            if (!string.Equals(record.DocumentFingerprint, fingerprint, StringComparison.Ordinal))
                return CommandResult.FailWithDetail(
                    Name + ": audit " + setFingerprint + " was taken on '" + record.DocumentTitle + "' at " +
                    record.DocumentFingerprint + ", and the active document '" + SafeTitle(doc) + "' is " +
                    fingerprint + ". A correction aimed at another document is the worst thing this surface " +
                    "could produce. Nothing was changed.",
                    new JObject { ["state"] = "refused", ["code"] = ProposalRefusal.WrongDocument });

            string idempotencyKey = (string)request["idempotency_key"];
            var actionsArray = (JArray)request["actions"];
            List<CorrectionAction> actions = CorrectionSelection.Select(record, actionsArray, CorrectionRegistry.Default);
            List<string> skipped = CorrectionSelection.Skipped(record, actionsArray);
            List<string> checks = CorrectionSelection.ChecksOf(actions);
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            // THE MODEL MUST STILL SHOW THE FINDINGS. The cited checks are re-run at
            // the audit's top and their ids compared; a different id means an element
            // appeared, disappeared or the list was cut differently since the audit
            // the caller read. Refused as stale on the rehearsal and on the apply
            // alike, because a rehearsal over a stale finding proposes the wrong thing.
            AuditModelCommand.AuditRun before = null;
            if (checks.Count > 0)
            {
                before = AuditModelCommand.RunChecks(app, doc, record.Top, null, checks);
                var failedChecks = new List<string>();
                foreach (JToken t in before.ChecksFailed) failedChecks.Add((string)t["check"]);
                string drift = FindingSetDrift.Describe(record, checks, before.FindingIds(), failedChecks);
                if (drift != null)
                    return CommandResult.FailWithDetail(
                        Name + ": STALE PLAN - the findings you cited are not what the model shows now: " + drift +
                        " The model changed after audit " + setFingerprint + " was taken. Nothing was changed. " +
                        "Re-run horizun_audit_model, read the current findings, and cite the new fingerprint.",
                        new JObject
                        {
                            ["state"] = "stale_plan",
                            ["confirmation_state"] = ConfirmationState.StalePlan.ToString(),
                            ["drift"] = drift,
                            ["finding_set_fingerprint_cited"] = setFingerprint
                        });
            }

            // REHEARSE, THROUGH THE TYPED TOOL. Generating arguments is not a rehearsal;
            // the child's dry run resolving them is. Run in both modes: on the apply it
            // is the pre-apply recheck that binds the plan the token is checked against.
            foreach (CorrectionAction action in actions)
            {
                if (!action.Actionable) continue;
                foreach (CorrectionStep step in action.Steps)
                {
                    string refusal;
                    ICommand child = ResolvePermitted(step.Tool, out refusal);
                    if (child == null)
                    {
                        action.State = CorrectionActionState.NotPermitted;
                        action.Why = refusal;
                        break;
                    }
                    CommandResult rehearsal = child.Execute(app, ChildArguments(step.Arguments, gate, true)
                                                                    .ToString(Formatting.None));
                    ApplicationState state = ApplicationOutcome.Read(rehearsal.Data);
                    step.RehearsalOk = rehearsal.Success && ApplicationOutcome.IsValidRehearsal(state);
                    step.RehearsalState = ApplicationOutcome.Name(state);
                    step.RehearsalError = rehearsal.Error;
                    step.RehearsalData = ToToken(rehearsal.Data);
                    try { step.ChildPlanFingerprint = (string)(rehearsal.Data as JObject)?["plan_resolved"]?["fingerprint"]; }
                    catch { step.ChildPlanFingerprint = null; }
                    if (step.RehearsalOk != true)
                    {
                        action.State = CorrectionActionState.RehearsalFailed;
                        action.Why = rehearsal.Success
                            ? "'" + step.Tool + "' rehearsed to '" + step.RehearsalState + "', not a clean dry run; " +
                              "read the step's rehearsal data."
                            : "'" + step.Tool + "' refused the rehearsal: " + rehearsal.Error;
                        break;
                    }
                }
            }

            bool rehearsedCleanly = CorrectionSelection.RehearsedCleanly(actions);
            string planHash = DocumentGate.PlanHash(request, "finding_set_fingerprint", "actions");
            ResolvedPlan plan = BuildPlan(app, gate, record, actions);

            if (dryRun)
            {
                JObject preview = Reply(true, record, actions, skipped, checks, idempotencyKey);
                preview["rehearsed_cleanly"] = rehearsedCleanly;
                preview["confirmation_withheld"] = !rehearsedCleanly;
                preview["note"] = rehearsedCleanly
                    ? "Every action rehearsed cleanly through its typed tool. Nothing was written. Call again " +
                      "with dry_run=false and the confirmation_token to apply exactly this action set."
                    : "NO EXECUTABLE CONFIRMATION WAS ISSUED. At least one action is not rehearsed - read each " +
                      "row's state and why. Fix its inputs, narrow it, or drop it, and rehearse again: a token " +
                      "over 'the ones that worked' would authorise a set nobody read as such.";
                // COUNTED IN ACTIONS, BOTH COLUMNS. This used to declare the total in
                // STEPS and the unresolved count in ACTIONS, so a call with one
                // three-step action and one requires_input action reported "3 requested,
                // 1 unresolved" - and the unresolved one had contributed nothing to the
                // 3. The numbers have to reconcile or they are decoration.
                ApplicationOutcome.StampRehearsal(preview, actions.Count,
                    actions.Count(a => a.State != CorrectionActionState.Rehearsed), 0, 0);
                // Recorded and issued together, or neither.
                if (rehearsedCleanly) DocumentGate.RecordResolvedPlan(plan);
                DocumentGate.StampConfirmation(preview, gate, Name, planHash, rehearsedCleanly,
                    "the token binds this document, audit " + record.Fingerprint + ", the exact action set, and " +
                    "what each typed tool's own rehearsal resolved; the apply re-runs the cited checks and " +
                    "re-rehearses every action, and refuses as a stale plan if either differs.");
                return CommandResult.Ok(preview);
            }

            if (!rehearsedCleanly)
                return CommandResult.FailWithDetail(
                    Name + ": not every action rehearsed cleanly, so nothing was applied. Read the rows: " +
                    string.Join("; ", actions.Where(a => a.State != CorrectionActionState.Rehearsed)
                                             .Select(a => "actions[" + a.Index + "] " + a.State + " - " + a.Why)) +
                    ". Nothing was changed.",
                    new JObject { ["state"] = "refused", ["actions"] = ActionsJson(actions) });

            // THE TOKEN, checked against the plan recomputed by THIS call.
            CommandResult confirmation = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null);
            if (confirmation != null) return confirmation;

            // APPLY. Inside the confirmed scope the typed tools do not demand their own
            // tokens - the outer token covers the set - but they keep every other check:
            // document identity, their own plan fingerprint, their own re-read.
            using (DocumentGate.EnterConfirmedAtomicPlan())
            {
                // The loop is CorrectionApplyLoop's, in Core; what this supplies is
                // the only executor the shipped command ever builds - the one that
                // dispatches the typed child. There is no other way in.
                CorrectionApplyLoop.Apply(actions, step =>
                {
                    string refusal;
                    ICommand child = ResolvePermitted(step.Tool, out refusal);
                    if (child == null) return StepExecution.NotStarted(refusal);

                    JObject args = ChildArguments(step.Arguments, gate, false);
                    if (!string.IsNullOrEmpty(step.ChildPlanFingerprint))
                        args["__expected_plan_fingerprint"] = step.ChildPlanFingerprint;
                    CommandResult applied = child.Execute(app, args.ToString(Formatting.None));
                    return new StepExecution
                    {
                        Success = applied.Success,
                        State = ApplicationOutcome.Read(applied.Data),
                        Error = applied.Error,
                        Data = ToToken(applied.Data ?? applied.Detail)
                    };
                });
            }

            // RE-AUDIT. The same checks, run again, judged per element against what
            // the audit lists NOW - not against the count of calls that were made.
            AuditModelCommand.AuditRun after = AuditModelCommand.RunChecks(app, doc, record.Top, null, checks);
            var reAudit = new JArray();
            var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CorrectionAction action in actions)
            {
                JObject row = ReAuditRules.Compare(action, after.FindingFor(action.Check), after.CheckFailed(action.Check));
                reAudit.Add(row);
                string outcome = (string)row["outcome"];
                outcomes[outcome] = outcomes.ContainsKey(outcome) ? outcomes[outcome] + 1 : 1;
            }
            var outcomeCounts = new JObject();
            foreach (string o in new[] { ReAuditOutcome.Corrected, ReAuditOutcome.Persistent, ReAuditOutcome.Failed, ReAuditOutcome.NotVerifiable })
                outcomeCounts[o] = outcomes.ContainsKey(o) ? outcomes[o] : 0;

            JObject result = Reply(false, record, actions, skipped, checks, idempotencyKey);
            int steps = StepCount(actions);
            int stepsApplied = actions.Sum(a => a.Steps.Count(s => s.ApplyOk == true));
            result["re_audit"] = new JObject
            {
                ["checks_rerun"] = new JArray(checks.Select(x => (JToken)x)),
                ["rows"] = reAudit,
                ["counts"] = outcomeCounts,
                ["finding_ids_after"] = JObject.FromObject(after.FindingIds()),
                ["checks_failed"] = after.ChecksFailed,
                ["means"] = "corrected: every selected element is gone from the re-run finding. persistent: the " +
                            "audit still lists it, whatever the typed call said. failed: the typed call did not " +
                            "apply. not_verifiable: the check could not re-run, or its list was cut at top. The " +
                            "finding_set_fingerprint of this audit is now stale by construction; re-run " +
                            "horizun_audit_model for a new one."
            };
            ApplicationOutcome.StampApplied(result, stepsApplied > 0 ? ApplicationOutcome.Committed : ApplicationOutcome.NotStarted,
                                            steps, stepsApplied, stepsApplied, 0, steps - stepsApplied, 0);
            DocumentGate.StampConfirmation(result, gate, Name, planHash, false);

            if (stepsApplied == 0)
                return CommandResult.FailWithDetail(
                    Name + ": no correction was applied - every typed call failed or came back unverified. " +
                    "Read actions[].steps[].apply. " + RollbackMeans,
                    result);
            return CommandResult.Ok(result);
        }

        // ---- helpers ------------------------------------------------------------

        /// <summary>
        /// A correction runs under the same permission and pack rules as a direct
        /// call - the same check horizun_execute_plan makes on its children. Without
        /// it this command is a side door to a tool the owner's configuration hides.
        /// </summary>
        private ICommand ResolvePermitted(string tool, out string refusal)
        {
            refusal = null;
            CommandContract contract = Contract.Find(tool);
            if (contract == null) { refusal = "'" + tool + "' has no contract."; return null; }
            string reason;
            if (!Core.Settings.IsToolAllowed(contract, out reason))
            { refusal = "'" + tool + "' is not permitted on this machine: " + reason; return null; }
            ICommand child = _resolve == null ? null : _resolve(tool);
            if (child == null) { refusal = "'" + tool + "' is not installed in this add-in."; return null; }
            return child;
        }

        private static JObject ChildArguments(JObject source, GateResult gate, bool dryRun)
        {
            JObject child = source == null ? new JObject() : (JObject)source.DeepClone();
            child["target_document"] = gate.Identity.Path ?? gate.Identity.Title;
            child["target_document_title"] = gate.Identity.Title;
            child["dry_run"] = dryRun;
            child.Remove("confirmation_token"); child.Remove("idempotency_key");
            return child;
        }

        /// <summary>
        /// The materialised plan the token binds: one element per step, carrying the
        /// finding it corrects, the ids it touches and the CHILD's own plan
        /// fingerprint where the child resolved one. Built identically from the
        /// rehearsal and from the pre-apply recheck, so a child that resolves
        /// differently at apply time refuses as a stale plan.
        /// </summary>
        private ResolvedPlan BuildPlan(UIApplication app, GateResult gate, FindingSetRecord record,
                                       IEnumerable<CorrectionAction> actions)
        {
            var plan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = SafeVersion(app),
                DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ContextFingerprint = "finding_set=" + (record?.Fingerprint ?? "")
            };
            foreach (CorrectionAction a in actions)
            {
                int n = 0;
                foreach (CorrectionStep s in a.Steps)
                {
                    plan.Elements.Add(new PlannedElement
                    {
                        UniqueId = "step:" + a.Index + ":" + (n++),
                        Category = s.Tool,
                        Action = s.Tool == "horizun_delete_verified" ? PlannedAction.Delete : PlannedAction.Modify,
                        BeforeValues = new Dictionary<string, string>
                        {
                            { "finding_id", s.FindingId ?? "" },
                            { "check", s.Check ?? "" },
                            { "element_ids", string.Join(",", s.ElementIds.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))) },
                            { "child", s.ChildPlanFingerprint ?? "no_child_plan" }
                        }
                    });
                }
            }
            return plan;
        }

        private JObject Reply(bool dryRun, FindingSetRecord record, List<CorrectionAction> actions,
                              List<string> skipped, List<string> checks, string idempotencyKey)
        {
            return new JObject
            {
                // NO SECOND IDEMPOTENCY BLOCK. The dispatcher stamps the true one -
                // key, status, whether the command executed in this call - and a block
                // written here disagreed with it on exactly the replay it described.
                ["dry_run"] = dryRun,
                ["executed"] = !dryRun,
                ["finding_set_fingerprint"] = record.Fingerprint,
                ["audit"] = new JObject
                {
                    ["document"] = record.DocumentTitle,
                    ["document_fingerprint"] = record.DocumentFingerprint,
                    ["top"] = record.Top,
                    ["recorded_utc"] = record.RecordedUtc,
                    ["issue_findings"] = record.Findings.Count(f => f.IsIssue)
                },
                ["actions"] = ActionsJson(actions),
                ["tally"] = CorrectionReply.Tally(actions),
                ["selected"] = actions.Count,
                ["skipped"] = new JArray(skipped.Select(x => (JToken)x)),
                ["skipped_means"] = "issue findings in this audit that no action named. Nothing was done about " +
                                    "them, and this reply is not a claim about them.",
                ["checks_rerun_before"] = new JArray(checks.Select(x => (JToken)x)),
                ["rollback_scope"] = RollbackScope,
                ["rollback_means"] = RollbackMeans,
                ["registry"] = CorrectionRegistry.ToolsJson(),
                ["registry_means"] = CorrectionRegistry.RegistryMeans
            };
        }

        private static JArray ActionsJson(IEnumerable<CorrectionAction> actions)
            => new JArray(actions.Select(a => (JToken)CorrectionReply.ActionJson(a)));

        private static int StepCount(IEnumerable<CorrectionAction> actions) => actions.Sum(a => a.Steps.Count);

        private static JToken ToToken(object data) => data == null ? JValue.CreateNull() :
            (data as JToken)?.DeepClone() ?? JToken.FromObject(data);

        private static string SafeVersion(UIApplication app)
        {
            try { return app?.Application?.VersionNumber; } catch { return null; }
        }

        private static string SafeTitle(Document d) { try { return d?.Title; } catch { return null; } }
    }
}
