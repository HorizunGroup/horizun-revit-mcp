// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_fix_planimetry: turn findings the planimetry
// auditor produced into typed, rehearsed, confirmed, atomic, re-read corrections.
//
// The auditor has eyes; this command is the hands, and the hands never guess.
// A correction runs only when ALL of these hold, and each absence is its own
// named refusal with nothing written:
//
//   1. the finding is identified stably (rule id, set, sheet/view, element ids);
//   2. the model still shows the OBSERVED state the caller approved a fix for;
//   3. the final value or geometry is explicit in the request;
//   4. the operation is in the closed typed catalog;
//   5. the rehearsal could materialise and verify the whole batch provisionally;
//   6. the confirmation token matches exactly that materialised plan;
//   7. every postcondition can be re-read from the committed model.
//
// The write discipline is the one the dimension and 2D-detail commands earned:
// DocumentGate.ForMutation, dry_run defaulting to true, a single-use token bound
// to the request AND the resolved before-state, StillTheSame immediately before
// the write, ONE TransactionGroup for the batch, verification in the reversible
// state, rollback of everything on any failed check, and a post-assimilate
// re-read the reply stands on.
//
// Packing, automatic annotation, revisions and visual judgement live on their
// dedicated typed surfaces. They remain deliberately outside this finding-driven
// fixer so an audit finding cannot silently become a layout, type or standard
// decision. The pure rules live in Core/PlanimetryFixRules.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class FixPlanimetryCommand : ICommand
    {
        public string Name => "horizun_fix_planimetry";
        public string Description =>
            "Apply typed corrections to findings from horizun_audit_planimetry - view template/scale/name, sheet " +
            "number/name, title-block placement, viewport/schedule moves, element-override clearing and " +
            "rectangular crops - each bound to the finding it corrects, rehearsed provisionally, confirmed, " +
            "committed atomically, re-read from the model, and re-audited so resolved, persistent and new " +
            "findings are told apart.";

        public const int MaxActions = 100;
        private const int MaxReportedNewFindings = 50;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            bool readOnly = false;
            try { readOnly = doc.IsReadOnly; } catch { /* the transaction would refuse */ }
            if (readOnly)
                return CommandResult.Fail("The active document is READ-ONLY, so no correction can be applied - " +
                    "not even the dry run's provisional rehearsal, which materialises and rolls back inside a " +
                    "transaction. Nothing was changed.");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double displayScale;
            if (!PlanimetryGeometry.TryScaleFromFeet(units, out displayScale))
                return CommandResult.Fail("units must be mm, m or feet.");
            double toFeet = 1.0 / displayScale;

            double toleranceFeet;
            string tolError = PlanimetryFixRules.ToleranceError(request["tolerance"], toFeet, out toleranceFeet);
            if (tolError != null) return CommandResult.Fail(tolError);

            JArray input = request["actions"] as JArray;
            if (input == null || input.Count == 0 || input.Count > MaxActions)
                return CommandResult.Fail("actions must contain 1.." + MaxActions + " entries.");

            // ---- The source audit this call corrects. -------------------------------
            var sourceAudit = request["source_audit"] as JObject;
            if (sourceAudit == null)
                return CommandResult.Fail("source_audit is required: it names the horizun_audit_planimetry " +
                    "result these findings were copied from (finding_set_fingerprint), so the correction " +
                    "declares its provenance instead of writing from memory.");
            string sourceFingerprint = sourceAudit.Value<string>("finding_set_fingerprint");
            if (string.IsNullOrWhiteSpace(sourceFingerprint))
                return CommandResult.Fail("source_audit.finding_set_fingerprint is required.");
            string sourceUnits = (sourceAudit.Value<string>("units") ?? "mm").ToLowerInvariant();
            double sourceScale;
            if (!PlanimetryGeometry.TryScaleFromFeet(sourceUnits, out sourceScale))
                return CommandResult.Fail("source_audit.units must be mm, m or feet.");

            // ---- The requirement set, inline, exactly as the auditor takes it. ------
            PlanimetryRequirementSet set = null;
            JToken rawSet = request["requirement_set"];
            if (rawSet != null && rawSet.Type != JTokenType.Null)
            {
                var setObject = rawSet as JObject;
                if (setObject == null)
                    return CommandResult.Fail("requirement_set must be an inline JSON object, exactly as " +
                                              "horizun_audit_planimetry takes it. Nothing was changed.");
                try { set = PlanimetryRequirementSet.Load(setObject); }
                catch (PlanimetryRequirementSetException ex)
                {
                    return CommandResult.Fail("The requirement set was REFUSED and nothing was corrected " +
                                              "against it: " + ex.Message);
                }
            }

            // ---- Recompute the audit NOW. This is the staleness authority: every cited
            // finding must still exist with the observed state the caller approved. ----
            PlanimetrySnapshot snapBefore;
            List<PlanimetryFinding> beforeFindings;
            try { beforeFindings = RunAudit(doc, set, sourceUnits, sourceScale, out snapBefore); }
            catch (Exception ex)
            {
                return CommandResult.Fail("The planimetry audit could not be recomputed, so no finding can be " +
                                          "proven current and nothing was corrected: " + ex.Message);
            }
            var beforeByKey = new Dictionary<string, PlanimetryFinding>(StringComparer.Ordinal);
            foreach (PlanimetryFinding f in beforeFindings)
            {
                string key = PlanimetryFixRules.IdentityOf(f);
                if (!beforeByKey.ContainsKey(key)) beforeByKey.Add(key, f);
            }
            string recomputedFingerprint;
            try { recomputedFingerprint = AuditPlanimetryCommand.Fingerprint(beforeFindings); }
            catch (Exception ex)
            {
                return CommandResult.Fail("The recomputed finding set could not be fingerprinted, so this call " +
                                          "cannot state its provenance: " + ex.Message + " Nothing was changed.");
            }
            // The published fingerprint is the first 16 characters, so a prefix match
            // is the right comparison - but ONLY against something at least that long.
            // A one-character fingerprint matches roughly one call in sixteen, and the
            // reply then prints "the audit recomputed NOW produces exactly the finding
            // set the caller cited". It gates nothing, which is precisely why it must
            // not be allowed to say something untrue.
            const int PublishedFingerprintLength = 16;
            if (sourceFingerprint.Length < 8)
                return CommandResult.Fail("source_audit.finding_set_fingerprint is '" + sourceFingerprint +
                    "', which is too short to identify anything: copy the finding_set_fingerprint from the " +
                    "horizun_audit_planimetry reply (it is " + PublishedFingerprintLength + " characters). " +
                    "Nothing was changed.");
            bool sourceMatches = recomputedFingerprint.StartsWith(sourceFingerprint, StringComparison.Ordinal);

            // ---- Plan every action. -------------------------------------------------
            var plans = new List<Plan>();
            var errors = new JArray();
            var outcomes = new List<ActionOutcome>();
            var claimedTargets = new HashSet<long>();
            var claimedFinals = new HashSet<string>(StringComparer.Ordinal);
            int staleCount = 0;
            for (int i = 0; i < input.Count; i++)
            {
                string error = null, reason = null;
                bool stale = false;
                Plan plan = PlanAction(doc, i, input[i] as JObject, toFeet, set, beforeByKey, snapBefore,
                                       claimedTargets, claimedFinals, out error, out reason, out stale);
                if (plan == null)
                {
                    string message = error ?? "entry is not an object";
                    errors.Add(new JObject { ["index"] = i, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                    if (stale) staleCount++;
                }
                else plans.Add(plan);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "tolerance", "source_audit",
                                                   "requirement_set", "actions");

            // ---- The MATERIALISED plan: these elements, as they stand right now. ----
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = Try(() => app?.Application?.VersionNumber),
                DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ContextFingerprint = "set=" + (set == null ? "-" : set.Sha256) +
                                     ";source=" + sourceFingerprint +
                                     ";units=" + units +
                                     ";tol=" + PlanimetryFixRules.CanonicalTenthMillimetre(toleranceFeet)
            };
            foreach (Plan p in plans) resolvedPlan.Elements.Add(p.PlannedRow());

            // ---- AUTHORISATION COMES BEFORE ANY TRANSACTION. ----------------------
            // The rehearsal below is a real write: it opens a Transaction, applies
            // every action and rolls back. Running it before the token was checked
            // meant a dry_run=false call carrying NO token - or a spent, expired or
            // stale one - still executed the whole batch provisionally against the
            // document, and a rollback Revit would not confirm then left an
            // UNAUTHORISED call reporting state=uncertain. So on the apply path the
            // invalid-action refusal, the confirmation and the active-document recheck
            // all run first, and only an authorised request may open a transaction.
            // The dry run has no token to check - issuing one is what it is for - so it
            // goes straight to the rehearsal.
            if (!dryRun)
            {
                if (errors.Count > 0)
                {
                    FallbackVerdict invalidVerdict = FallbackDecision.Decide(outcomes, writeStarted: false);
                    return CommandResult.FailWithDetail(
                        (staleCount > 0
                            ? staleCount + " action(s) cite findings the model no longer shows; nothing ran: "
                            : "Invalid actions; nothing ran: ") + errors.ToString(Formatting.None),
                        new JObject
                        {
                            ["state"] = PlanimetryFixRules.StateRefused,
                            ["stale_findings"] = staleCount,
                            ["invalid_actions"] = errors.Count,
                            ["write_started"] = false
                        },
                        invalidVerdict.Signal, invalidVerdict.CapabilityGaps);
                }

                CommandResult gateRefusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                             resolvedPlan, null);
                if (gateRefusal != null) return gateRefusal;
                gateRefusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
                if (gateRefusal != null) return gateRefusal;
            }

            // ---- The REHEARSAL: provisional materialisation, measurement, mandatory
            // rollback. Run on both paths whenever every action planned cleanly. ------
            Rehearsal rehearsal = null;
            if (errors.Count == 0)
            {
                rehearsal = Rehearse(doc, plans, toleranceFeet, displayScale, units);
                if (!rehearsal.RollbackConfirmed)
                    return CommandResult.FailWithDetail(
                        "The rehearsal transaction could not be rolled back: Revit reported '" +
                        rehearsal.RollbackStatus + "', not RolledBack. The model may still carry the provisional " +
                        "corrections, so the state of this call is UNCERTAIN - no confirmation token is issued " +
                        "and nothing is claimed clean. Re-read the model before anything else.",
                        new JObject
                        {
                            ["state"] = PlanimetryFixRules.StateUncertain,
                            ["rehearsal_rollback_status"] = rehearsal.RollbackStatus,
                            ["write_started"] = true,
                            ["rehearsal"] = rehearsal.ToJson()
                        });
            }

            if (dryRun)
            {
                bool constructible = rehearsal != null && rehearsal.AllConstructible;
                var rehearsed = new JObject
                {
                    ["dry_run"] = true,
                    ["transaction_status"] = ApplicationOutcome.NotStarted,
                    ["write_started"] = false,
                    ["host_verified"] = false,
                    ["actions"] = input.Count,
                    ["valid_actions"] = plans.Count,
                    ["invalid_actions"] = errors.Count,
                    ["stale_findings"] = staleCount,
                    ["errors"] = errors,
                    ["units"] = units,
                    ["tolerance"] = ToleranceJson(toleranceFeet, displayScale, units),
                    ["source_audit"] = SourceAuditJson(sourceFingerprint, recomputedFingerprint, sourceMatches,
                                                       sourceUnits),
                    ["requirement_set_sha256"] = set == null ? (JToken)JValue.CreateNull() : set.Sha256,
                    ["plan"] = new JArray(plans.Select(p => (JToken)p.Summary())),
                    ["fix_catalog"] = FixCatalog(),
                    ["not_covered"] = NotCovered(),
                    ["note"] = "Nothing persists: the rehearsal materialised the batch inside a transaction, " +
                               "verified it, and rolled it back."
                };
                rehearsed["rehearsal"] = rehearsal == null ? (JToken)JValue.CreateNull() : rehearsal.ToJson();
                if (rehearsal == null)
                    rehearsed["rehearsal_note"] = "The batch was NOT rehearsed: " + errors.Count + " action(s) are " +
                        "invalid, so no transaction was opened and nothing was provisionally materialised.";
                if (errors.Count == 0 && constructible) DocumentGate.RecordResolvedPlan(resolvedPlan);
                ApplicationOutcome.StampRehearsal(rehearsed, input.Count, errors.Count,
                                                  rehearsal == null ? 0 : rehearsal.NotConstructibleCount, 0);
                DocumentGate.StampConfirmation(rehearsed, gate, Name, planHash, errors.Count == 0 && constructible,
                    errors.Count == 0 && constructible
                        ? "the token binds the ordered actions, the finding each one cites (identity AND observed " +
                          "state), the requirement set's SHA-256, and the before-state of every element resolved - " +
                          "a model that moves before you spend it refuses as a stale plan rather than being " +
                          "corrected into something else."
                        : errors.Count > 0
                            ? "no usable token while any action is invalid or its finding is stale"
                            : "no usable token: the rehearsal could not materialise and verify every correction - " +
                              "see the rehearsal rows for Revit's own reason per action");
                return FallbackDecision.Attach(
                    CommandResult.Ok(rehearsed),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }

            if (!rehearsal.AllConstructible)
                return CommandResult.FailWithDetail(
                    "Refused: " + rehearsal.NotConstructibleCount + " of " + plans.Count + " correction(s) could " +
                    "not be materialised and verified against the current model - see the rehearsal rows for " +
                    "Revit's reason per action. Nothing was committed: the rehearsal transaction rolled back " +
                    "(Revit reported '" + rehearsal.RollbackStatus + "'). The confirmation token was already " +
                    "SPENT by this call - it is validated before any transaction opens, and it is single use - " +
                    "so re-run the dry run to get a new one.",
                    new JObject
                    {
                        ["state"] = PlanimetryFixRules.StateRefused,
                        ["write_started"] = false,
                        ["confirmation_token_spent"] = true,
                        ["rehearsal"] = rehearsal.ToJson()
                    });

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: fix planimetry";

            JArray reversibleRows;
            string materialiseFailure = null;
            using (var group = new TransactionGroup(doc, txName))
            {
                group.Start();
                using (var tx = new Transaction(doc, txName))
                {
                    tx.Start();
                    try
                    {
                        foreach (Plan p in plans) Apply(doc, p);
                        doc.Regenerate();
                        Guard.Commit(tx, txName);
                    }
                    catch (Exception ex)
                    {
                        bool attempted = false;
                        string terminal;
                        if (ex is SilentRollbackException silent) terminal = silent.Status.ToString();
                        else if (tx.GetStatus() == TransactionStatus.Started)
                        {
                            attempted = true;
                            terminal = Guard.RollBack(tx).StatusName;
                        }
                        else terminal = ApplicationOutcome.NotStarted;
                        Guard.RollbackResult rbGroup = Guard.RollBack(group);
                        bool confirmed = PlanFailure.IsConfirmedRollback(rbGroup.StatusName);
                        var detail = new JObject
                        {
                            ["state"] = PlanimetryFixRules.DecideFinalState(rbGroup.StatusName, false),
                            ["transaction_status"] = terminal,
                            ["transaction_group_status"] = rbGroup.StatusName,
                            ["rollback_attempted"] = attempted,
                            ["rollback_confirmed"] = rbGroup.Confirmed,
                            ["write_started"] = true
                        };
                        ApplicationOutcome.StampApplied(detail, rbGroup.StatusName, plans.Count, 0, 0, 0,
                                                        confirmed ? plans.Count : 0,
                                                        confirmed ? 0 : plans.Count);
                        return CommandResult.FailWithDetail(
                            "Atomic planimetry fix failed: " + ex.Message + " " +
                            PlanFailure.SingleTransactionOutcome(attempted,
                                attempted ? terminal : PlanFailure.NotAttempted, "nothing was corrected") +
                            " The TransactionGroup reported '" + rbGroup.StatusName + "'.",
                            detail);
                    }
                }

                // A committed regeneration makes Revit compute the facts the verification
                // reads (crop shapes and box outlines behave like dimension values: they
                // are not reliably readable inside the transaction that changed them).
                using (var regen = new Transaction(doc, txName + " (materialise for verification)"))
                {
                    regen.Start();
                    try { doc.Regenerate(); Guard.Commit(regen, txName); }
                    catch (Exception ex)
                    {
                        // Recorded, not swallowed: the verification below then reads
                        // facts Revit may not have finished computing, and the reply
                        // must say the materialisation did not happen rather than
                        // leave a reader to infer it from a failed comparison. The
                        // rollback is itself guarded, because a throw here would
                        // escape the group with nothing reported.
                        materialiseFailure = ex.Message;
                        try { if (regen.GetStatus() == TransactionStatus.Started) Guard.RollBack(regen); }
                        catch (Exception rex) { materialiseFailure += "; its rollback also threw: " + rex.Message; }
                    }
                }

                // ---- Verify with the GROUP still open: the reversible state. --------
                reversibleRows = new JArray();
                int reversibleFailures = 0;
                foreach (Plan p in plans)
                {
                    bool okOne;
                    reversibleRows.Add(VerifyPlan(doc, p, toleranceFeet, displayScale, units, out okOne));
                    if (!okOne) reversibleFailures++;
                }
                if (reversibleFailures > 0)
                {
                    Guard.RollbackResult rb = Guard.RollBack(group);
                    var detail = new JObject
                    {
                        ["state"] = PlanimetryFixRules.DecideFinalState(rb.StatusName, false),
                        ["transaction_status"] = ApplicationOutcome.Committed,
                        ["transaction_group_status"] = rb.StatusName,
                        ["rollback_confirmed"] = rb.Confirmed,
                        ["verified_in_reversible_state"] = false,
                        ["write_started"] = true,
                        ["rows"] = reversibleRows
                    };
                    ApplicationOutcome.StampApplied(detail, rb.StatusName, plans.Count, 0, 0, 0,
                        rb.Confirmed ? reversibleFailures : 0,
                        rb.Confirmed ? 0 : plans.Count);
                    return CommandResult.FailWithDetail(
                        "Postcondition verification in the reversible state found " + reversibleFailures +
                        " correction(s) whose re-read did not match the request, so the WHOLE batch was rolled " +
                        "back. " + PlanFailure.SingleTransactionOutcome(true, rb.StatusName,
                            "nothing was corrected") +
                        " The TransactionGroup reported '" + rb.StatusName + "'.",
                        detail);
                }

                try { Guard.Assimilate(group, txName); }
                catch (SilentRollbackException ex)
                {
                    string rbStatus;
                    try { rbStatus = Guard.RollBack(group).StatusName; }
                    catch (Exception rex) { rbStatus = "RollBack threw: " + rex.Message; }
                    bool confirmed = PlanFailure.IsConfirmedRollback(rbStatus);
                    var detail = new JObject
                    {
                        ["state"] = PlanimetryFixRules.DecideFinalState(rbStatus, false),
                        ["transaction_status"] = ApplicationOutcome.Committed,
                        ["transaction_group_status"] = rbStatus,
                        ["write_started"] = true,
                        ["rows"] = reversibleRows
                    };
                    ApplicationOutcome.StampApplied(detail, rbStatus, plans.Count, 0, 0, 0,
                        confirmed ? plans.Count : 0, confirmed ? 0 : plans.Count);
                    return CommandResult.FailWithDetail(
                        "Every correction verified, but the TransactionGroup would not assimilate: " + ex.Message +
                        " A rollback was attempted and Revit reported '" + rbStatus + "'.",
                        detail);
                }
            }

            // ---- Post-assimilate: the same checks over the settled model. -----------
            var rows = new JArray();
            int verified = 0;
            foreach (Plan p in plans)
            {
                bool okOne;
                rows.Add(VerifyPlan(doc, p, toleranceFeet, displayScale, units, out okOne));
                if (okOne) verified++;
            }
            if (verified != plans.Count)
            {
                var detail = new JObject
                {
                    ["state"] = PlanimetryFixRules.DecideFinalState(ApplicationOutcome.Committed, false),
                    ["transaction_status"] = ApplicationOutcome.Committed,
                    ["verified_in_reversible_state"] = true,
                    ["write_started"] = true,
                    ["rows"] = rows
                };
                ApplicationOutcome.StampApplied(detail, ApplicationOutcome.Committed, plans.Count,
                                                verified, verified, 0, 0, plans.Count - verified);
                return CommandResult.FailWithDetail(
                    "The batch committed and assimilated, but " + (plans.Count - verified) + " correction(s) " +
                    "failed the post-assimilate re-read after passing in the reversible state. Two measurements " +
                    "of one fact in contradiction are the absence of knowledge; inspect the model before any " +
                    "retry.",
                    detail);
            }

            // ---- Re-run the audit: the finding's own rule is the resolution verdict. -
            // A partial re-evaluation of only the affected checks is not demonstrably
            // equivalent (overlaps and coverage are cross-entity), so the FULL audit
            // runs again, exactly as it ran before.
            JObject reconciliationJson;
            try
            {
                PlanimetrySnapshot snapAfter;
                List<PlanimetryFinding> afterFindings = RunAudit(doc, set, sourceUnits, sourceScale, out snapAfter);
                // How many collection passes DIED on each side. The inventory does not
                // throw when one does - it records the failure and returns that
                // population EMPTY - so without these counts a dead pass would read as
                // "every finding in it resolved", and would zero the NEW list at the
                // same time.
                PlanimetryFixRules.Reconciliation rec = PlanimetryFixRules.Reconcile(
                    plans.Select(p => p.Finding), beforeFindings, afterFindings,
                    snapBefore.ChecksFailed.Count, snapAfter.ChecksFailed.Count);
                reconciliationJson = ReconciliationJson(rec, snapBefore, snapAfter, afterFindings);
            }
            catch (Exception ex)
            {
                // The corrections committed and verified; what failed is the re-audit.
                // Nothing may be DECLARED resolved on that basis - a rule that was not
                // re-run has not stopped producing its finding.
                reconciliationJson = new JObject
                {
                    ["audit_rerun"] = "failed",
                    ["error"] = ex.Message,
                    ["selected"] = plans.Count,
                    ["resolved"] = new JArray(),
                    ["persistent"] = new JArray(),
                    ["new_findings"] = new JArray(),
                    ["note"] = "The corrections committed and every postcondition verified, but the audit could " +
                               "not be re-run, so NO finding is declared resolved. Re-run " +
                               "horizun_audit_planimetry to read the current state."
                };
            }

            var result = new JObject
            {
                ["dry_run"] = false,
                ["state"] = PlanimetryFixRules.DecideFinalState(ApplicationOutcome.Committed, true),
                ["transaction_status"] = ApplicationOutcome.Committed,
                ["transaction_group_status"] = "Assimilated",
                ["transaction_name"] = txName,
                ["write_started"] = true,
                ["host_verified"] = true,
                ["actions"] = plans.Count,
                ["actions_verified"] = verified,
                ["units"] = units,
                ["tolerance"] = ToleranceJson(toleranceFeet, displayScale, units),
                ["source_audit"] = SourceAuditJson(sourceFingerprint, recomputedFingerprint, sourceMatches,
                                                   sourceUnits),
                ["requirement_set_sha256"] = set == null ? (JToken)JValue.CreateNull() : set.Sha256,
                ["rows"] = rows,
                ["reconciliation"] = reconciliationJson
            };
            DocumentGate.StampConfirmation(result, gate, Name, planHash, false);
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, plans.Count,
                                            plans.Count, verified, 0, 0, 0);
            return CommandResult.Ok(result);
        }

        // =====================================================================
        // The audit, recomputed exactly as horizun_audit_planimetry runs it.
        // =====================================================================
        private static List<PlanimetryFinding> RunAudit(Document doc, PlanimetryRequirementSet set,
                                                        string unitsName, double scaleFromFeet,
                                                        out PlanimetrySnapshot snap)
        {
            var scope = new PlanimetryScope
            {
                NeedSheets = true, NeedViews = true, NeedPlacements = true,
                NeedAnnotations = true, NeedReferences = true,
                IncludeParameters = set != null
            };
            if (set != null)
            {
                scope.ParameterNames.AddRange(AuditPlanimetryCommand.ParameterNames(set));
                scope.TagCoverageCategories.AddRange(set.TagCoverageCategories);
                scope.TagCoverageExcludeParameters.AddRange(set.TagCoverageExcludeParameters);
            }
            snap = PlanimetryInventory.Collect(doc, scope, QueryPlanimetryCommand.RevitYear());
            var options = new PlanimetryRuleOptions
            {
                Units = unitsName,
                ScaleFromFeet = scaleFromFeet,
                ToleranceFeet = PlanimetryGeometry.TouchToleranceFeet,
                IncludeAdvisory = true,
                IncludePassedChecks = false
            };
            PlanimetryAuditResult universal = PlanimetryRules.EvaluateUniversal(snap, options);
            var findings = new List<PlanimetryFinding>(universal.Findings);
            if (set != null)
            {
                PlanimetryAuditResult configured = PlanimetryRules.EvaluateRequirementSet(snap, set, options);
                PlanimetryRules.Attribute(configured, set);
                findings.AddRange(configured.Findings);
            }
            findings.Sort(PlanimetryFinding.Compare);
            return findings;
        }

        // =====================================================================
        // Planning.
        // =====================================================================
        private sealed class Plan
        {
            public int Index;
            public PlanimetryFixOperation Op;
            public PlanimetryFixRules.CitedFinding Finding;
            public string FindingSignature;

            public ElementId TargetId;
            public string TargetUniqueId;
            public string TargetClass;

            // Final values, resolved.
            public ElementId TemplateId; public string TemplateName;
            public int Scale;
            public string NewName; public string NewNumber;
            public ElementId TitleBlockTypeId; public string TitleBlockTypeName;
            public double PxFeet, PyFeet;
            public ElementId ViewId;                       // clear_element_override / set_crop target view
            public double CropMinX, CropMinY, CropMaxX, CropMaxY;   // view-plane feet

            // Before-state, for the resolved plan and for unchanged-postconditions.
            public Dictionary<string, string> Before = new Dictionary<string, string>(StringComparer.Ordinal);
            public PlanBox SheetExtentBefore = PlanBox.Unreadable;
            public string SheetExtentSource;
            public bool? CropVisibleBefore;
            public string CategoryOverrideBefore;
            public long TemplateBefore = long.MinValue;

            // Filled at apply/rehearsal time.
            public ElementId CreatedId;

            public PlannedElement PlannedRow()
            {
                var before = new Dictionary<string, string>(Before, StringComparer.Ordinal);
                before["finding"] = FindingSignature;
                return new PlannedElement
                {
                    UniqueId = TargetUniqueId,
                    Category = Op.Name,
                    TypeName = TargetClass,
                    Action = Op.Name == "place_title_block" ? PlannedAction.Create : PlannedAction.Modify,
                    BeforeValues = before
                };
            }

            public JObject Summary()
            {
                var o = new JObject
                {
                    ["index"] = Index,
                    ["operation"] = Op.Name,
                    ["target_id"] = Rid.Value(TargetId),
                    ["finding_rule"] = Finding.RuleId,
                    ["finding_set"] = Finding.RequirementSetId
                };
                switch (Op.Name)
                {
                    case "set_view_template": o["template_id"] = Rid.Value(TemplateId); o["template"] = TemplateName; break;
                    case "set_view_scale": o["scale"] = Scale; break;
                    case "rename_view": o["new_name"] = NewName; break;
                    case "rename_sheet":
                        o["new_number"] = NewNumber == null ? (JToken)JValue.CreateNull() : NewNumber;
                        o["new_name"] = NewName == null ? (JToken)JValue.CreateNull() : NewName;
                        break;
                    case "place_title_block": o["title_block_type_id"] = Rid.Value(TitleBlockTypeId); o["type"] = TitleBlockTypeName; break;
                    case "move_viewport":
                    case "move_schedule":
                        o["point_feet"] = new JArray(PxFeet, PyFeet); break;
                    case "clear_element_override": o["view_id"] = Rid.Value(ViewId); break;
                    case "set_crop":
                        o["crop_feet"] = new JArray(CropMinX, CropMinY, CropMaxX, CropMaxY); break;
                }
                return o;
            }
        }

        private static Plan PlanAction(Document doc, int index, JObject a, double toFeet,
                                       PlanimetryRequirementSet set,
                                       Dictionary<string, PlanimetryFinding> beforeByKey,
                                       PlanimetrySnapshot snapBefore,
                                       HashSet<long> claimedTargets, HashSet<string> claimedFinals,
                                       out string error, out string unsupportedReason, out bool stale)
        {
            error = null; unsupportedReason = null; stale = false;
            if (a == null) { error = "entry is not an object"; return null; }
            try
            {
                string opName = a.Value<string>("operation");
                PlanimetryFixOperation op = PlanimetryFixRules.Operation(opName);
                // ---- THE FINDING FIRST, and its staleness, BEFORE the operation name.
                // An operation outside the catalog is a capability gap, and a capability
                // gap is the one refusal that GRANTS the Python fallback. Judging the
                // operation first meant a batch of unknown operations could be told
                // "go write the script" without any finding ever being checked - for
                // corrections the model may no longer license at all. Staleness beats
                // capability, so the property "no fallback for a stale action" holds
                // whatever the operation is called.
                string findingError;
                PlanimetryFixRules.CitedFinding cited = PlanimetryFixRules.ParseFinding(a["finding"], out findingError);
                if (cited == null) throw new ArgumentException(findingError);

                if (!cited.IsUniversal)
                {
                    if (set == null)
                        throw new ArgumentException("the finding cites requirement set '" + cited.RequirementSetId +
                            "', but no requirement_set was provided inline. The fix must re-check the finding " +
                            "against the SAME rules that produced it; pass the set exactly as the audit took it.");
                    if (!string.Equals(set.Id, cited.RequirementSetId, StringComparison.Ordinal))
                        throw new ArgumentException("the finding cites requirement set '" + cited.RequirementSetId +
                            "', but the inline set is '" + set.Id + "'.");
                    if (!string.Equals(set.Version, cited.RequirementSetVersion, StringComparison.Ordinal))
                        throw new ArgumentException("the finding cites '" + cited.RequirementSetId + "' version '" +
                            cited.RequirementSetVersion + "', but the inline set is version '" + set.Version +
                            "'. A finding from other rules cannot be re-checked by these.");
                    if (!string.Equals(set.Sha256, cited.RequirementSetSha256, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("THE REQUIREMENT SET WAS MODIFIED: the finding cites SHA-256 " +
                            cited.RequirementSetSha256 + ", but the inline set hashes to " + set.Sha256 + ". A " +
                            "fix judged by different rules than the audit is not a fix. Re-run the audit with " +
                            "this set and cite ITS findings, or pass the original set.");
                }

                PlanimetryFinding current;
                beforeByKey.TryGetValue(cited.IdentityKey(), out current);
                string staleError = PlanimetryFixRules.StaleError(cited, current);
                if (staleError != null) { stale = true; throw new ArgumentException(staleError); }

                // ---- Only now the operation, its fields, and whether it addresses
                // ---- this rule at all.
                if (op == null)
                    throw new UnsupportedCapability(
                        "unsupported operation '" + (opName ?? "(none)") + "' - horizun_fix_planimetry " +
                        "implements a closed set of typed corrections: " +
                        PlanimetryFixRules.OperationsSentence() + ". Nothing was written.",
                        FallbackSignal.ReasonUnsupportedOperation);

                string fieldError = PlanimetryFixRules.UnknownFieldError(op, a.Properties().Select(p => p.Name));
                if (fieldError != null) throw new ArgumentException(fieldError);
                fieldError = PlanimetryFixRules.RequiredFieldError(op, f => a[f] != null && a[f].Type != JTokenType.Null);
                if (fieldError != null) throw new ArgumentException(fieldError);

                string remedyError = PlanimetryFixRules.RemedyError(cited.RuleId, cited.RequirementSetId,
                                                                    cited.EntityKind, op);
                if (remedyError != null) throw new ArgumentException(remedyError);

                // ---- Resolve the target and validate the final value. --------------
                var plan = new Plan { Index = index, Op = op, Finding = cited,
                                      FindingSignature = current.Signature() };
                switch (op.Name)
                {
                    case "set_view_template": PlanSetTemplate(doc, a, cited, plan); break;
                    case "set_view_scale": PlanSetScale(doc, a, cited, plan); break;
                    case "rename_view": PlanRenameView(doc, a, cited, claimedFinals, plan); break;
                    case "rename_sheet": PlanRenameSheet(doc, a, cited, claimedFinals, plan); break;
                    case "place_title_block": PlanPlaceTitleBlock(doc, a, cited, plan); break;
                    case "move_viewport": PlanMoveViewport(doc, a, cited, toFeet, snapBefore, plan); break;
                    case "move_schedule": PlanMoveSchedule(doc, a, cited, toFeet, snapBefore, plan); break;
                    case "clear_element_override": PlanClearOverride(doc, a, cited, plan); break;
                    case "set_crop": PlanSetCrop(doc, a, cited, toFeet, plan); break;
                    default: throw new InvalidOperationException("operation escaped the catalog");
                }

                string claimError = PlanimetryFixRules.ClaimTargetError(claimedTargets, Rid.Value(plan.TargetId));
                if (claimError != null) throw new ArgumentException(claimError);

                return plan;
            }
            catch (Exception ex)
            {
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                error = ex.Message;
                return null;
            }
        }

        // ---- Per-operation planning. -----------------------------------------------

        private static T Need<T>(Document doc, JObject a, string field) where T : Element
        {
            JToken token = a[field];
            if (token == null || token.Type != JTokenType.Integer)
                throw new ArgumentException("'" + field + "' is required and must be an integer ElementId.");
            long id = token.Value<long>();
            if (!Rid.CanRepresent(id)) throw new ArgumentException(Rid.RangeError(id));
            Element e = doc.GetElement(Rid.Make(id));
            if (e == null)
                throw new ArgumentException("'" + field + "' = " + id + " does not exist in the active document.");
            var typed = e as T;
            if (typed == null)
                throw new ArgumentException("'" + field + "' = " + id + " is a " + e.GetType().Name + ", not a " +
                                            typeof(T).Name + ".");
            return typed;
        }

        private static void RequireTargetInFinding(PlanimetryFixRules.CitedFinding cited, long targetId,
                                                   string field)
        {
            if (!cited.ElementIds.Contains(targetId))
                throw new ArgumentException("'" + field + "' = " + targetId + " is not among the cited finding's " +
                    "element_ids [" + string.Join(", ", cited.ElementIds) + "]. A correction may only touch the " +
                    "elements its finding is about.");
        }

        private static string SafeUid(Element e)
        {
            try { return e.UniqueId ?? ""; } catch { return "<unreadable>"; }
        }

        private static string SafeName(Element e)
        {
            try { return e.Name ?? ""; } catch { return "<unreadable>"; }
        }

        private static void PlanSetTemplate(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited,
                                            Plan plan)
        {
            View view = Need<View>(doc, a, "view_id");
            RequireTargetInFinding(cited, Rid.Value(view.Id), "view_id");
            if (view is ViewSheet)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a SHEET, which takes no view " +
                                            "template. Nothing was written.");
            if (view.IsTemplate)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is itself a view TEMPLATE; a " +
                                            "template does not take a template.");
            View template = Need<View>(doc, a, "template_id");
            if (!template.IsTemplate)
                throw new ArgumentException("template_id " + Rid.Value(template.Id) + " is not a view template " +
                    "(View.IsTemplate is false). Pass the ElementId of an actual ViewTemplate; nothing is " +
                    "resolved from a name.");
            plan.TargetId = view.Id;
            plan.TargetUniqueId = SafeUid(view);
            plan.TargetClass = view.GetType().Name;
            plan.TemplateId = template.Id;
            plan.TemplateName = SafeName(template);
            plan.Before["template_id"] = Rid.Value(view.ViewTemplateId).ToString(CultureInfo.InvariantCulture);
            plan.Before["template_ref"] = SafeUid(template) + "|" + plan.TemplateName;
        }

        private static void PlanSetScale(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited, Plan plan)
        {
            View view = Need<View>(doc, a, "view_id");
            RequireTargetInFinding(cited, Rid.Value(view.Id), "view_id");
            if (view.IsTemplate)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a view template; assign the " +
                                            "scale through the views that use it, or edit the template deliberately.");
            if (view is ViewSchedule || view is ViewSheet)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a " + view.GetType().Name +
                                            ", which has no view scale to set.");
            var threeD = view as View3D;
            if (threeD != null && threeD.IsPerspective)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a PERSPECTIVE 3D view, which " +
                                            "has no meaningful scale.");
            string scaleError = PlanimetryFixRules.ScaleError(a.Value<long?>("scale"));
            if (scaleError != null) throw new ArgumentException(scaleError);
            int current;
            try { current = view.Scale; }
            catch (Exception ex)
            { throw new ArgumentException("the view's current scale could not be read (" + ex.Message + "), so a " +
                                          "scale change cannot be validated or verified."); }
            if (current == 0)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " reports scale 0: this view kind " +
                                            "does not take a scale.");
            plan.TargetId = view.Id;
            plan.TargetUniqueId = SafeUid(view);
            plan.TargetClass = view.GetType().Name;
            plan.Scale = (int)a.Value<long>("scale");
            plan.Before["scale"] = current.ToString(CultureInfo.InvariantCulture);
        }

        private static void PlanRenameView(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited,
                                           HashSet<string> claimedFinals, Plan plan)
        {
            View view = Need<View>(doc, a, "view_id");
            RequireTargetInFinding(cited, Rid.Value(view.Id), "view_id");
            // A ViewSheet IS a View, so without this a sheet could be renamed through
            // the rename_view path - skipping every sheet-specific check, including the
            // sheet-number uniqueness rename_sheet enforces. The two operations exist
            // separately because a sheet has two names and a view has one.
            if (view is ViewSheet)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a SHEET. Use rename_sheet, " +
                    "which validates the sheet number as well as the name; a sheet renamed through " +
                    "rename_view would skip that. Nothing was written.");
            if (view.IsTemplate)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a view template. Renaming " +
                                            "templates is a project-standards decision, not a finding correction.");
            string name = a.Value<string>("new_name");
            string nameError = PlanimetryFixRules.NameError("new_name", name);
            if (nameError != null) throw new ArgumentException(nameError);
            View holder = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.Id != view.Id && string.Equals(SafeName(v), name, StringComparison.Ordinal));
            if (holder != null)
                throw new ArgumentException("a view named '" + name + "' already exists (id " +
                    Rid.Value(holder.Id) + ", " + holder.GetType().Name + "). Revit demands unique view names; " +
                    "nothing was written.");
            string claim = PlanimetryFixRules.ClaimFinalValueError(claimedFinals, "view name", name);
            if (claim != null) throw new ArgumentException(claim);
            plan.TargetId = view.Id;
            plan.TargetUniqueId = SafeUid(view);
            plan.TargetClass = view.GetType().Name;
            plan.NewName = name;
            plan.Before["name"] = SafeName(view);
        }

        private static void PlanRenameSheet(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited,
                                            HashSet<string> claimedFinals, Plan plan)
        {
            ViewSheet sheet = Need<ViewSheet>(doc, a, "sheet_id");
            RequireTargetInFinding(cited, Rid.Value(sheet.Id), "sheet_id");
            string number = a.Value<string>("new_number");
            string name = a.Value<string>("new_name");
            if (number != null)
            {
                string numberError = PlanimetryFixRules.NameError("new_number", number);
                if (numberError != null) throw new ArgumentException(numberError);
                ViewSheet holder = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .FirstOrDefault(s => s.Id != sheet.Id &&
                                         string.Equals(Try(() => s.SheetNumber), number, StringComparison.Ordinal));
                if (holder != null)
                    throw new ArgumentException("a sheet numbered '" + number + "' already exists (id " +
                        Rid.Value(holder.Id) + "). Sheet numbers are unique; nothing was written.");
                string claim = PlanimetryFixRules.ClaimFinalValueError(claimedFinals, "sheet number", number);
                if (claim != null) throw new ArgumentException(claim);
            }
            if (name != null)
            {
                string nameError = PlanimetryFixRules.NameError("new_name", name);
                if (nameError != null) throw new ArgumentException(nameError);
            }
            plan.TargetId = sheet.Id;
            plan.TargetUniqueId = SafeUid(sheet);
            plan.TargetClass = sheet.GetType().Name;
            plan.NewNumber = number;
            plan.NewName = name;
            plan.Before["sheet_number"] = Try(() => sheet.SheetNumber) ?? "<unreadable>";
            plan.Before["name"] = SafeName(sheet);
        }

        private static void PlanPlaceTitleBlock(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited,
                                                Plan plan)
        {
            ViewSheet sheet = Need<ViewSheet>(doc, a, "sheet_id");
            RequireTargetInFinding(cited, Rid.Value(sheet.Id), "sheet_id");
            bool placeholder;
            try { placeholder = sheet.IsPlaceholder; }
            catch (Exception ex)
            { throw new ArgumentException("whether sheet " + Rid.Value(sheet.Id) + " is a placeholder could not " +
                                          "be read (" + ex.Message + "); a title block cannot be planned onto it."); }
            if (placeholder)
                throw new ArgumentException("sheet_id " + Rid.Value(sheet.Id) + " is a PLACEHOLDER sheet; it " +
                                            "cannot carry a title block.");
            FamilySymbol symbol = Need<FamilySymbol>(doc, a, "title_block_type_id");
            long categoryId = symbol.Category == null ? 0 : Rid.Value(symbol.Category.Id);
            if (categoryId != (long)BuiltInCategory.OST_TitleBlocks)
                throw new ArgumentException("title_block_type_id " + Rid.Value(symbol.Id) + " is not a " +
                    "title-block FamilySymbol (its category is " +
                    (symbol.Category == null ? "unreadable" : "'" + symbol.Category.Name + "'") + ").");
            int existing = TitleBlockCount(doc, sheet.Id);
            if (existing != 0)
                throw new ArgumentException("sheet " + Rid.Value(sheet.Id) + " already carries " + existing +
                    " title block(s). This operation corrects sheet.no-titleblock and will NEVER place a second " +
                    "title block.");
            plan.TargetId = sheet.Id;
            plan.TargetUniqueId = SafeUid(sheet);
            plan.TargetClass = sheet.GetType().Name;
            plan.TitleBlockTypeId = symbol.Id;
            plan.TitleBlockTypeName = SafeName(symbol);
            plan.Before["titleblock_count"] = existing.ToString(CultureInfo.InvariantCulture);
            plan.Before["type_ref"] = SafeUid(symbol) + "|" + plan.TitleBlockTypeName;
        }

        private static void PlanMoveViewport(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited,
                                             double toFeet, PlanimetrySnapshot snapBefore, Plan plan)
        {
            Viewport vp = Need<Viewport>(doc, a, "viewport_id");
            RequireTargetInFinding(cited, Rid.Value(vp.Id), "viewport_id");
            bool pinned;
            try { pinned = vp.Pinned; } catch { pinned = false; }
            if (pinned)
                throw new ArgumentException("viewport " + Rid.Value(vp.Id) + " is PINNED. Somebody pinned it on " +
                    "purpose; unpinning is a deliberate act (horizun_transform_elements), not a side effect of a " +
                    "fix. Nothing was written.");
            double x, y;
            string pointError = PlanimetryFixRules.PointError("point", a["point"], out x, out y);
            if (pointError != null) throw new ArgumentException(pointError);
            plan.TargetId = vp.Id;
            plan.TargetUniqueId = SafeUid(vp);
            plan.TargetClass = vp.GetType().Name;
            plan.PxFeet = x * toFeet;
            plan.PyFeet = y * toFeet;
            XYZ centre = null;
            try { centre = vp.GetBoxCenter(); } catch { }
            plan.Before["box_center"] = centre == null ? "<unreadable>"
                : PlanimetryFixRules.CanonicalPoint2D(centre.X, centre.Y);
            RecordSheetExtent(doc, snapBefore, Try(() => Rid.GetIdOrNull(vp.SheetId)), plan);
        }

        private static void PlanMoveSchedule(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited,
                                             double toFeet, PlanimetrySnapshot snapBefore, Plan plan)
        {
            ScheduleSheetInstance ssi = Need<ScheduleSheetInstance>(doc, a, "schedule_instance_id");
            RequireTargetInFinding(cited, Rid.Value(ssi.Id), "schedule_instance_id");
            // The typed API for this move is the ScheduleSheetInstance.Point setter. If a
            // Revit year ships without it, the refusal names the API - and grants NO
            // Python fallback, because a script calls the same absent setter.
            var pointProperty = typeof(ScheduleSheetInstance).GetProperty("Point");
            if (pointProperty == null || pointProperty.GetSetMethod() == null)
                throw new ArgumentException("ScheduleSheetInstance.Point has no setter in this Revit's API, so a " +
                    "schedule placement cannot be moved by any path - do NOT fall back to Python; a script faces " +
                    "the same absent API. Nothing was written.");
            bool pinned;
            try { pinned = ssi.Pinned; } catch { pinned = false; }
            if (pinned)
                throw new ArgumentException("schedule placement " + Rid.Value(ssi.Id) + " is PINNED. Unpinning " +
                    "is a deliberate act (horizun_transform_elements), not a side effect of a fix.");
            double x, y;
            string pointError = PlanimetryFixRules.PointError("point", a["point"], out x, out y);
            if (pointError != null) throw new ArgumentException(pointError);
            plan.TargetId = ssi.Id;
            plan.TargetUniqueId = SafeUid(ssi);
            plan.TargetClass = ssi.GetType().Name;
            plan.PxFeet = x * toFeet;
            plan.PyFeet = y * toFeet;
            XYZ point = null;
            try { point = ssi.Point; } catch { }
            plan.Before["point"] = point == null ? "<unreadable>"
                : PlanimetryFixRules.CanonicalPoint2D(point.X, point.Y);
            RecordSheetExtent(doc, snapBefore, Try(() => Rid.GetIdOrNull(ssi.OwnerViewId)), plan);
        }

        private static void RecordSheetExtent(Document doc, PlanimetrySnapshot snap, long? sheetId, Plan plan)
        {
            if (!sheetId.HasValue) return;
            SheetFact sheet = snap.SheetById(sheetId.Value);
            if (sheet == null) return;
            plan.SheetExtentBefore = sheet.Extent;
            plan.SheetExtentSource = sheet.ExtentSource;
        }

        private static void PlanClearOverride(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited,
                                              Plan plan)
        {
            View view = Need<View>(doc, a, "view_id");
            JToken elementToken = a["element_id"];
            if (elementToken == null || elementToken.Type != JTokenType.Integer)
                throw new ArgumentException("'element_id' is required and must be an integer ElementId.");
            long elementId = elementToken.Value<long>();
            if (!Rid.CanRepresent(elementId)) throw new ArgumentException(Rid.RangeError(elementId));
            Element element = doc.GetElement(Rid.Make(elementId));
            if (element == null)
                throw new ArgumentException("'element_id' = " + elementId + " does not exist in the active document.");
            RequireTargetInFinding(cited, elementId, "element_id");
            // THE VIEW MUST BE BOUND TOO, not only the element. Clearing an override is
            // a per-VIEW act, so "a correction may only touch the elements its finding
            // is about" has to mean "in the view its finding is about". The binding
            // comes from the finding when it names a view, otherwise from the element's
            // own owner view; when NEITHER is available the request is refused rather
            // than allowed to clear that element's override in any view the caller
            // happens to name.
            long? owner = Try(() => Rid.GetIdOrNull(element.OwnerViewId));
            bool ownerUsable = owner.HasValue && owner.Value != -1;
            if (cited.ViewId.HasValue)
            {
                if (cited.ViewId.Value != Rid.Value(view.Id))
                    throw new ArgumentException("'view_id' = " + Rid.Value(view.Id) + " is not the view the " +
                        "finding is about (finding.view_id = " + cited.ViewId.Value + ").");
            }
            else if (ownerUsable)
            {
                if (owner.Value != Rid.Value(view.Id))
                    throw new ArgumentException("element " + elementId + " belongs to view " + owner.Value +
                        ", not to view " + Rid.Value(view.Id) + ".");
            }
            else
            {
                throw new ArgumentException("the finding names no view and element " + elementId + " has no " +
                    "owner view, so nothing binds this correction to view " + Rid.Value(view.Id) +
                    " in particular. Clearing an override is a per-view act and this phase will not choose the " +
                    "view for you. Cite a finding that names its view. Nothing was written.");
            }
            if (ownerUsable && owner.Value != Rid.Value(view.Id))
                throw new ArgumentException("element " + elementId + " belongs to view " + owner.Value + ", not " +
                    "to view " + Rid.Value(view.Id) + ".");
            OverrideGraphicSettings current;
            try { current = view.GetElementOverrides(element.Id); }
            catch (Exception ex)
            { throw new ArgumentException("the element's override in this view could not be read (" + ex.Message +
                                          "), so clearing it cannot be verified."); }
            if (!PlanimetryInventory.OverridesDifferFromDefaults(current))
                throw new ArgumentException("element " + elementId + " carries NO per-element override in view " +
                    Rid.Value(view.Id) + " - there is nothing to clear. If the audit reported one, the model " +
                    "has moved; re-run the audit.");
            plan.TargetId = element.Id;
            plan.TargetUniqueId = SafeUid(element);
            plan.TargetClass = element.GetType().Name;
            plan.ViewId = view.Id;
            plan.Before["override"] = PlanimetryInventory.OverrideSignature(current);
            plan.TemplateBefore = Rid.Value(view.ViewTemplateId);
            long categoryId = element.Category == null ? -1 : Rid.Value(element.Category.Id);
            plan.Before["category_id"] = categoryId.ToString(CultureInfo.InvariantCulture);
            plan.CategoryOverrideBefore = CategoryOverrideSignature(view, element);
        }

        /// <summary>
        /// The element's CATEGORY override in this view, canonically - or null when it
        /// could not be read.
        ///
        /// Null rather than a "(unreadable: ...)" string on purpose. Two unreadable
        /// reads produce the same sentence, and comparing those two sentences would
        /// report "the category override did not move" on the strength of having
        /// failed to read it twice. The caller turns null into an UNMEASURED
        /// postcondition instead, which cannot pass.
        /// </summary>
        private static string CategoryOverrideSignature(View view, Element element)
        {
            try
            {
                if (element.Category == null) return "(no category)";
                return PlanimetryInventory.OverrideSignature(view.GetCategoryOverrides(element.Category.Id));
            }
            catch { return null; }
        }

        private static void PlanSetCrop(Document doc, JObject a, PlanimetryFixRules.CitedFinding cited,
                                        double toFeet, Plan plan)
        {
            View view = Need<View>(doc, a, "view_id");
            bool inFinding = cited.ElementIds.Contains(Rid.Value(view.Id)) ||
                             (cited.ViewId.HasValue && cited.ViewId.Value == Rid.Value(view.Id));
            if (!inFinding)
                throw new ArgumentException("'view_id' = " + Rid.Value(view.Id) + " is neither the finding's " +
                    "view nor among its element_ids. A crop change may only touch the view its finding is about.");
            if (view.IsTemplate)
                throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a view template; a template " +
                                            "has no crop of its own to set.");
            if (PlanimetryFixRules.NonRectangularCrop(a["crop"]))
                throw new UnsupportedCapability(
                    "a NON-RECTANGULAR crop shape was requested (crop.loop). This phase reproduces rectangular " +
                    "crops only, because an arbitrary loop cannot be verified against the request without " +
                    "geometry this contract does not carry. Nothing was written.",
                    FallbackSignal.ReasonUnsupportedKind);
            bool active;
            try { active = view.CropBoxActive; }
            catch (Exception ex)
            { throw new ArgumentException("whether the crop is active could not be read (" + ex.Message + ")."); }
            if (!active)
                throw new ArgumentException("view " + Rid.Value(view.Id) + "'s crop is NOT ACTIVE. Activating a " +
                    "crop changes what the view shows everywhere it is placed - a display decision this fix does " +
                    "not take as a side effect. Activate it deliberately, re-run the audit, then fix the shape.");
            // CanHaveShape is deliberately NOT required: it answers whether the view
            // can carry a NON-rectangular shape, and this operation writes a
            // rectangle through View.CropBox. Requiring it would refuse views whose
            // crop is perfectly settable. What IS required is a readable CropBox,
            // because that is what the write and the re-read both stand on.
            BoundingBoxXYZ currentBox;
            try { currentBox = view.CropBox; }
            catch (Exception ex)
            { throw new ArgumentException("the view's CropBox could not be read (" + ex.Message + "), so a crop " +
                                          "cannot be written or verified."); }
            if (currentBox == null)
                throw new ArgumentException("view " + Rid.Value(view.Id) + " returned no CropBox, so there is " +
                                            "nothing to set.");
            double minX, minY, maxX, maxY;
            string cropError = PlanimetryFixRules.CropError(a["crop"], out minX, out minY, out maxX, out maxY);
            if (cropError != null) throw new ArgumentException(cropError);
            plan.TargetId = view.Id;
            plan.TargetUniqueId = SafeUid(view);
            plan.TargetClass = view.GetType().Name;
            plan.ViewId = view.Id;
            plan.CropMinX = minX * toFeet; plan.CropMinY = minY * toFeet;
            plan.CropMaxX = maxX * toFeet; plan.CropMaxY = maxY * toFeet;
            plan.CropVisibleBefore = Try(() => (bool?)view.CropBoxVisible);
            PlanBox current = ReadCropBox(view);
            plan.Before["crop"] = current.Valid
                ? PlanimetryFixRules.CanonicalPoint2D(current.MinX, current.MinY) + ";" +
                  PlanimetryFixRules.CanonicalPoint2D(current.MaxX, current.MaxY)
                : "<unreadable>";
            plan.Before["crop_active"] = "true";
        }

        // =====================================================================
        // Application - shared by the rehearsal and the confirmed apply.
        // =====================================================================
        private static void Apply(Document doc, Plan plan)
        {
            switch (plan.Op.Name)
            {
                case "set_view_template":
                {
                    var view = (View)doc.GetElement(plan.TargetId);
                    view.ViewTemplateId = plan.TemplateId;
                    return;
                }
                case "set_view_scale":
                {
                    var view = (View)doc.GetElement(plan.TargetId);
                    view.Scale = plan.Scale;
                    return;
                }
                case "rename_view":
                {
                    var view = (View)doc.GetElement(plan.TargetId);
                    view.Name = plan.NewName;
                    return;
                }
                case "rename_sheet":
                {
                    var sheet = (ViewSheet)doc.GetElement(plan.TargetId);
                    if (plan.NewNumber != null) sheet.SheetNumber = plan.NewNumber;
                    if (plan.NewName != null) sheet.Name = plan.NewName;
                    return;
                }
                case "place_title_block":
                {
                    var sheet = (ViewSheet)doc.GetElement(plan.TargetId);
                    var symbol = (FamilySymbol)doc.GetElement(plan.TitleBlockTypeId);
                    if (!symbol.IsActive) symbol.Activate();
                    FamilyInstance instance = doc.Create.NewFamilyInstance(XYZ.Zero, symbol, sheet);
                    plan.CreatedId = instance == null ? null : instance.Id;
                    if (plan.CreatedId == null)
                        throw new InvalidOperationException("Revit returned no instance for the title block.");
                    return;
                }
                case "move_viewport":
                {
                    var vp = (Viewport)doc.GetElement(plan.TargetId);
                    vp.SetBoxCenter(new XYZ(plan.PxFeet, plan.PyFeet, 0));
                    return;
                }
                case "move_schedule":
                {
                    var ssi = (ScheduleSheetInstance)doc.GetElement(plan.TargetId);
                    ssi.Point = new XYZ(plan.PxFeet, plan.PyFeet, 0);
                    return;
                }
                case "clear_element_override":
                {
                    var view = (View)doc.GetElement(plan.ViewId);
                    view.SetElementOverrides(plan.TargetId, new OverrideGraphicSettings());
                    return;
                }
                case "set_crop":
                {
                    // A RECTANGULAR crop is set through the RECTANGULAR api.
                    //
                    // MEASURED on Revit 2026 (2026-08-25, the live gate): using
                    // ViewCropRegionShapeManager.SetCropShape installed a crop-region
                    // SKETCH, and Revit models that sketch's constraints as two
                    // non-view-specific Dimension elements - the model's dimension
                    // census rose by exactly two per call, and they were still there
                    // after the crop was set back. For a command whose entire contract
                    // is that it writes only what it names, adding two undeclared
                    // elements to somebody's model is not an acceptable side effect.
                    //
                    // View.CropBox takes the rectangle directly and creates no sketch.
                    // An existing shape is removed first, because a shape-set crop
                    // ignores CropBox - and removing it is exactly what the caller
                    // asked for by naming a rectangle.
                    var view = (View)doc.GetElement(plan.TargetId);
                    ViewCropRegionShapeManager manager = view.GetCropRegionShapeManager();
                    if (manager != null && manager.ShapeSet) manager.RemoveCropRegionShape();

                    BoundingBoxXYZ box = view.CropBox;
                    if (box == null)
                        throw new InvalidOperationException("the view returned no CropBox to set");
                    Transform inverse = (box.Transform ?? Transform.Identity).Inverse;
                    XYZ a = inverse.OfPoint(OnViewPlane(view, plan.CropMinX, plan.CropMinY));
                    XYZ b = inverse.OfPoint(OnViewPlane(view, plan.CropMaxX, plan.CropMaxY));
                    box.Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), box.Min.Z);
                    box.Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), box.Max.Z);
                    view.CropBox = box;
                    return;
                }
                default:
                    throw new InvalidOperationException("operation escaped the catalog");
            }
        }

        /// <summary>A view-plane point in model space: origin + x*right + y*up. The
        /// same convention the inventory projects with, so the crop this writes and
        /// the crop the auditor reads are the same rectangle.</summary>
        private static XYZ OnViewPlane(View view, double x, double y)
            => view.Origin + view.RightDirection * x + view.UpDirection * y;

        // =====================================================================
        // The rehearsal: provisional materialisation, measurement, MANDATORY rollback.
        // =====================================================================
        private sealed class Rehearsal
        {
            public JArray Rows = new JArray();
            public int NotConstructibleCount;
            public string RollbackStatus;
            public bool RollbackConfirmed;

            /// <summary>Revit's message when the provisional regeneration failed, or
            /// null. A rehearsal measured over an unmaterialised state has not
            /// rehearsed, so this withholds the token exactly as a broken row does.</summary>
            public string RegenerateFailure;

            public bool AllConstructible => NotConstructibleCount == 0 && RegenerateFailure == null;

            public JObject ToJson()
            {
                return new JObject
                {
                    ["materialised_provisionally"] = true,
                    ["rolled_back"] = RollbackConfirmed,
                    ["rollback_status"] = RollbackStatus,
                    ["not_constructible"] = NotConstructibleCount,
                    ["regenerate_failure"] = RegenerateFailure == null
                        ? (JToken)JValue.CreateNull() : RegenerateFailure,
                    ["rows"] = Rows,
                    ["meaning"] = "Every correction was APPLIED inside a transaction, its postconditions were " +
                                  "measured in that provisional state, and the transaction was rolled back. " +
                                  "'constructible' is Revit's own answer, not a prediction."
                };
            }
        }

        private static Rehearsal Rehearse(Document doc, List<Plan> plans, double toleranceFeet,
                                          double displayScale, string units)
        {
            var rehearsal = new Rehearsal();
            using (var tx = new Transaction(doc, "Horizun: fix planimetry (rehearsal)"))
            {
                tx.Start();
                var applied = new Dictionary<int, string>();   // index -> Revit's reason, null = applied
                foreach (Plan p in plans)
                {
                    try { Apply(doc, p); applied[p.Index] = null; }
                    catch (Exception ex) { applied[p.Index] = ex.Message; }
                }
                string regenerateFailure = null;
            try { doc.Regenerate(); }
            catch (Exception ex)
            {
                // NOT swallowed. Verification below reads facts Revit may not have
                // finished computing, and where those stale reads happen to agree with
                // the request the rows would say `constructible` over a state that was
                // never materialised. The failure travels into the rehearsal so the
                // caller can see it beside the rows it produced.
                regenerateFailure = ex.Message;
            }
            rehearsal.RegenerateFailure = regenerateFailure;

                foreach (Plan p in plans)
                {
                    string reason = applied[p.Index];
                    if (reason != null)
                    {
                        rehearsal.NotConstructibleCount++;
                        rehearsal.Rows.Add(new JObject
                        {
                            ["index"] = p.Index,
                            ["operation"] = p.Op.Name,
                            ["constructible"] = false,
                            ["revit_reason"] = reason
                        });
                        continue;
                    }
                    bool ok;
                    JObject row = VerifyPlan(doc, p, toleranceFeet, displayScale, units, out ok);
                    row["constructible"] = ok;
                    if (!ok) rehearsal.NotConstructibleCount++;
                    rehearsal.Rows.Add(row);
                }

                // Transaction.RollBack() can THROW, and this was the one rollback in
                // the file not guarded - so a rehearsal that had just applied the whole
                // batch provisionally could escape Execute with a generic dispatcher
                // failure: no state, no write_started, no rehearsal block, and a
                // document whose provisional edits may still be there. A throw is
                // treated exactly as an unconfirmed rollback, which is what it is.
                try
                {
                    Guard.RollbackResult rb = Guard.RollBack(tx);
                    rehearsal.RollbackStatus = rb.StatusName;
                    rehearsal.RollbackConfirmed = rb.Confirmed;
                }
                catch (Exception ex)
                {
                    rehearsal.RollbackStatus = "RollBack threw: " + ex.Message;
                    rehearsal.RollbackConfirmed = false;
                }
            }
            return rehearsal;
        }

        // =====================================================================
        // Verification: every promised property re-read and compared.
        // =====================================================================
        private static JObject VerifyPlan(Document doc, Plan plan, double toleranceFeet,
                                          double displayScale, string units, out bool ok)
        {
            PostconditionCheck check;
            var row = new JObject
            {
                ["index"] = plan.Index,
                ["operation"] = plan.Op.Name,
                ["target_id"] = Rid.Value(plan.TargetId),
                ["finding_rule"] = plan.Finding.RuleId
            };
            try
            {
                switch (plan.Op.Name)
                {
                    case "set_view_template": check = VerifyTemplate(doc, plan); break;
                    case "set_view_scale": check = VerifyScale(doc, plan); break;
                    case "rename_view": check = VerifyViewName(doc, plan); break;
                    case "rename_sheet": check = VerifySheet(doc, plan); break;
                    case "place_title_block": check = VerifyTitleBlock(doc, plan); break;
                    case "move_viewport": check = VerifyViewportMove(doc, plan, toleranceFeet, row,
                                                                     displayScale, units); break;
                    case "move_schedule": check = VerifyScheduleMove(doc, plan, toleranceFeet, row); break;
                    case "clear_element_override": check = VerifyOverrideCleared(doc, plan); break;
                    case "set_crop": check = VerifyCrop(doc, plan, toleranceFeet); break;
                    default:
                        check = new PostconditionCheck("operation");
                        check.Unreadable("operation", plan.Op.Name, "operation escaped the catalog");
                        break;
                }
            }
            catch (Exception ex)
            {
                check = new PostconditionCheck("re_read");
                check.Unreadable("re_read", plan.Op.Name, ex.Message);
            }
            ok = check.AllVerified;
            row["verified"] = ok;
            row["postconditions"] = check.ToJson();
            return row;
        }

        private static PostconditionCheck VerifyTemplate(Document doc, Plan plan)
        {
            var check = new PostconditionCheck("view_template_id");
            var view = doc.GetElement(plan.TargetId) as View;
            if (view == null) { check.Unreadable("view_template_id", Rid.Value(plan.TemplateId), "the view is gone"); return check; }
            try { check.Compare("view_template_id", Rid.Value(plan.TemplateId), Rid.Value(view.ViewTemplateId)); }
            catch (Exception ex) { check.Unreadable("view_template_id", Rid.Value(plan.TemplateId), ex.Message); }
            return check;
        }

        private static PostconditionCheck VerifyScale(Document doc, Plan plan)
        {
            var check = new PostconditionCheck("scale");
            var view = doc.GetElement(plan.TargetId) as View;
            if (view == null) { check.Unreadable("scale", plan.Scale, "the view is gone"); return check; }
            try { check.Compare("scale", plan.Scale, view.Scale); }
            catch (Exception ex) { check.Unreadable("scale", plan.Scale, ex.Message); }
            return check;
        }

        private static PostconditionCheck VerifyViewName(Document doc, Plan plan)
        {
            var check = new PostconditionCheck("name");
            var view = doc.GetElement(plan.TargetId) as View;
            if (view == null) { check.Unreadable("name", plan.NewName, "the view is gone"); return check; }
            try { check.Compare("name", plan.NewName, view.Name); }
            catch (Exception ex) { check.Unreadable("name", plan.NewName, ex.Message); }
            return check;
        }

        private static PostconditionCheck VerifySheet(Document doc, Plan plan)
        {
            var check = new PostconditionCheck("sheet_number", "name");
            var sheet = doc.GetElement(plan.TargetId) as ViewSheet;
            if (sheet == null)
            {
                check.Unreadable("sheet_number", plan.NewNumber, "the sheet is gone");
                check.Unreadable("name", plan.NewName, "the sheet is gone");
                return check;
            }
            // Both fields are re-read whether or not they were renamed: an unchanged
            // field's expected value is its before-value, and a fix that quietly moved
            // it would fail here.
            string wantedNumber = plan.NewNumber ?? plan.Before["sheet_number"];
            string wantedName = plan.NewName ?? plan.Before["name"];
            try { check.Compare("sheet_number", wantedNumber, sheet.SheetNumber); }
            catch (Exception ex) { check.Unreadable("sheet_number", wantedNumber, ex.Message); }
            try { check.Compare("name", wantedName, sheet.Name); }
            catch (Exception ex) { check.Unreadable("name", wantedName, ex.Message); }
            return check;
        }

        private static PostconditionCheck VerifyTitleBlock(Document doc, Plan plan)
        {
            var check = new PostconditionCheck("instance_present", "owner_sheet", "symbol", "category",
                                               "titleblock_count");
            Element instance = plan.CreatedId == null ? null : doc.GetElement(plan.CreatedId);
            if (instance == null)
            {
                check.Record("instance_present", true, false, false);
                check.Unreadable("owner_sheet", Rid.Value(plan.TargetId), "no instance to read");
                check.Unreadable("symbol", Rid.Value(plan.TitleBlockTypeId), "no instance to read");
                check.Unreadable("category", (long)BuiltInCategory.OST_TitleBlocks, "no instance to read");
                check.Unreadable("titleblock_count", 1, "no instance to read");
                return check;
            }
            check.Record("instance_present", true, true, true);
            try { check.Compare("owner_sheet", Rid.Value(plan.TargetId), Rid.Value(instance.OwnerViewId)); }
            catch (Exception ex) { check.Unreadable("owner_sheet", Rid.Value(plan.TargetId), ex.Message); }
            try { check.Compare("symbol", Rid.Value(plan.TitleBlockTypeId), Rid.Value(instance.GetTypeId())); }
            catch (Exception ex) { check.Unreadable("symbol", Rid.Value(plan.TitleBlockTypeId), ex.Message); }
            try
            {
                long category = instance.Category == null ? -1 : Rid.Value(instance.Category.Id);
                check.Compare("category", (long)BuiltInCategory.OST_TitleBlocks, category);
            }
            catch (Exception ex) { check.Unreadable("category", (long)BuiltInCategory.OST_TitleBlocks, ex.Message); }
            try { check.Compare("titleblock_count", 1, TitleBlockCount(doc, plan.TargetId)); }
            catch (Exception ex) { check.Unreadable("titleblock_count", 1, ex.Message); }
            return check;
        }

        private static int TitleBlockCount(Document doc, ElementId sheetId)
        {
            return new FilteredElementCollector(doc, sheetId)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .GetElementCount();
        }

        private static PostconditionCheck VerifyViewportMove(Document doc, Plan plan, double toleranceFeet,
                                                             JObject row, double displayScale, string units)
        {
            var vp = doc.GetElement(plan.TargetId) as Viewport;
            if (vp == null)
            {
                var gone = new PostconditionCheck("box_center");
                gone.Unreadable("box_center", RequestedPoint(plan), "the viewport is gone");
                row["inside_sheet_extent"] = "unreadable: the viewport is gone";
                return gone;
            }

            // WHETHER containment is part of the promise is decided BEFORE the
            // checklist is built, because a checklist must declare exactly what it
            // covers. The contract scopes this one: verify that the placement did not
            // land off the sheet "when that geometry is available". When either extent
            // is unreadable it is not a postcondition at all - it is reported in the
            // row and decides nothing, rather than failing a move whose own point
            // verified or, worse, passing as though it had been measured.
            PlanBox afterExtent = ViewportExtent(vp);
            bool containmentMeasurable = plan.SheetExtentBefore.Valid && afterExtent.Valid;

            PostconditionCheck check = containmentMeasurable
                ? new PostconditionCheck("box_center", "inside_sheet_extent")
                : new PostconditionCheck("box_center");

            try
            {
                XYZ centre = vp.GetBoxCenter();
                double distance = Distance2D(centre.X, centre.Y, plan.PxFeet, plan.PyFeet);
                check.Record("box_center", RequestedPoint(plan),
                             PlanimetryFixRules.CanonicalPoint2D(centre.X, centre.Y),
                             distance <= toleranceFeet);
            }
            catch (Exception ex) { check.Unreadable("box_center", RequestedPoint(plan), ex.Message); }

            if (containmentMeasurable)
            {
                // A move that lands the placement wholly off the sheet recreates the
                // very finding this phase exists to fix, so a measured "outside" fails
                // the action and rolls the batch back.
                bool inside = !PlanimetryGeometry.Disjoint(plan.SheetExtentBefore, afterExtent,
                                                           PlanimetryGeometry.TouchToleranceFeet);
                check.Record("inside_sheet_extent",
                             "intersects the sheet extent (" + plan.SheetExtentSource + ")",
                             inside ? "intersects" : "wholly outside",
                             inside);
                row["inside_sheet_extent"] = inside;
            }
            else
            {
                row["inside_sheet_extent"] = plan.SheetExtentBefore.Valid
                    ? "not measured: the moved viewport's outline would not read"
                    : "not measured: the sheet's extent could not be read";
            }
            return check;
        }

        private static PlanBox ViewportExtent(Viewport vp)
        {
            try
            {
                Outline box = vp.GetBoxOutline();
                if (box == null) return PlanBox.Unreadable;
                PlanBox b = PlanBox.FromCorners(box.MinimumPoint.X, box.MinimumPoint.Y,
                                                box.MaximumPoint.X, box.MaximumPoint.Y);
                try
                {
                    Outline label = vp.GetLabelOutline();
                    if (label != null)
                        b = PlanimetryGeometry.UnionOptional(b,
                            PlanBox.FromCorners(label.MinimumPoint.X, label.MinimumPoint.Y,
                                                label.MaximumPoint.X, label.MaximumPoint.Y));
                }
                catch { /* the label is optional; the box already answered */ }
                return b;
            }
            catch { return PlanBox.Unreadable; }
        }

        private static PostconditionCheck VerifyScheduleMove(Document doc, Plan plan, double toleranceFeet,
                                                             JObject row)
        {
            var ssi = doc.GetElement(plan.TargetId) as ScheduleSheetInstance;
            if (ssi == null)
            {
                var gone = new PostconditionCheck("point");
                gone.Unreadable("point", RequestedPoint(plan), "the schedule placement is gone");
                row["inside_sheet_extent"] = "unreadable: the schedule placement is gone";
                return gone;
            }

            // The SAME containment promise move_viewport makes, for the same reason:
            // move_schedule is a remedy for sheet.placement-outside-extent, so a move
            // that lands the placement wholly off the sheet recreates the finding it
            // was meant to correct. It was measured on one operation and not the
            // other, which is a promise kept by accident of which method a caller
            // happened to use. Measurability is decided before the checklist is built.
            PlanBox afterExtent = SheetExtentOf(doc, ssi, plan);
            bool containmentMeasurable = plan.SheetExtentBefore.Valid && afterExtent.Valid;

            PostconditionCheck check = containmentMeasurable
                ? new PostconditionCheck("point", "inside_sheet_extent")
                : new PostconditionCheck("point");

            try
            {
                XYZ point = ssi.Point;
                double distance = Distance2D(point.X, point.Y, plan.PxFeet, plan.PyFeet);
                check.Record("point", RequestedPoint(plan),
                             PlanimetryFixRules.CanonicalPoint2D(point.X, point.Y),
                             distance <= toleranceFeet);
            }
            catch (Exception ex) { check.Unreadable("point", RequestedPoint(plan), ex.Message); }

            if (containmentMeasurable)
            {
                bool inside = !PlanimetryGeometry.Disjoint(plan.SheetExtentBefore, afterExtent,
                                                           PlanimetryGeometry.TouchToleranceFeet);
                check.Record("inside_sheet_extent",
                             "intersects the sheet extent (" + plan.SheetExtentSource + ")",
                             inside ? "intersects" : "wholly outside",
                             inside);
                row["inside_sheet_extent"] = inside;
            }
            else
            {
                row["inside_sheet_extent"] = plan.SheetExtentBefore.Valid
                    ? "not measured: the moved placement would not report a bounding box on its sheet"
                    : "not measured: the sheet's extent could not be read";
            }
            return check;
        }

        /// <summary>
        /// A placement's extent on its own sheet, read the way the INVENTORY reads it -
        /// the element's bounding box IN the sheet view, projected to a rectangle - so
        /// the fix and the audit cannot disagree about where a placement is.
        /// </summary>
        private static PlanBox SheetExtentOf(Document doc, Element placement, Plan plan)
        {
            try
            {
                long? sheetId = Try(() => Rid.GetIdOrNull(placement.OwnerViewId));
                if (!sheetId.HasValue || !Rid.CanRepresent(sheetId.Value)) return PlanBox.Unreadable;
                var sheet = doc.GetElement(Rid.Make(sheetId.Value)) as ViewSheet;
                if (sheet == null) return PlanBox.Unreadable;
                BoundingBoxXYZ box = placement.get_BoundingBox(sheet);
                if (box == null) return PlanBox.Unreadable;
                Transform t = box.Transform ?? Transform.Identity;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (double x in new[] { box.Min.X, box.Max.X })
                    foreach (double y in new[] { box.Min.Y, box.Max.Y })
                        foreach (double z in new[] { box.Min.Z, box.Max.Z })
                        {
                            XYZ p = t.OfPoint(new XYZ(x, y, z));
                            if (p.X < minX) minX = p.X;
                            if (p.Y < minY) minY = p.Y;
                            if (p.X > maxX) maxX = p.X;
                            if (p.Y > maxY) maxY = p.Y;
                        }
                if (minX > maxX) return PlanBox.Unreadable;
                return PlanBox.FromCorners(minX, minY, maxX, maxY);
            }
            catch { return PlanBox.Unreadable; }
        }

        private static PostconditionCheck VerifyOverrideCleared(Document doc, Plan plan)
        {
            var check = new PostconditionCheck("element_override_cleared", "category_override_unchanged",
                                               "view_template_unchanged");
            var view = doc.GetElement(plan.ViewId) as View;
            Element element = doc.GetElement(plan.TargetId);
            if (view == null || element == null)
            {
                string why = view == null ? "the view is gone" : "the element is gone";
                check.Unreadable("element_override_cleared", "defaults", why);
                check.Unreadable("category_override_unchanged", plan.CategoryOverrideBefore, why);
                check.Unreadable("view_template_unchanged", plan.TemplateBefore, why);
                return check;
            }
            try
            {
                OverrideGraphicSettings now = view.GetElementOverrides(element.Id);
                check.Record("element_override_cleared", "defaults",
                             PlanimetryInventory.OverridesDifferFromDefaults(now) ? "still overridden" : "defaults",
                             !PlanimetryInventory.OverridesDifferFromDefaults(now));
            }
            catch (Exception ex) { check.Unreadable("element_override_cleared", "defaults", ex.Message); }
            try
            {
                string catNow = CategoryOverrideSignature(view, element);
                if (plan.CategoryOverrideBefore == null || catNow == null)
                    check.Unreadable("category_override_unchanged", plan.CategoryOverrideBefore,
                        plan.CategoryOverrideBefore == null
                            ? "the category override could not be read BEFORE the write, so 'unchanged' cannot be measured"
                            : "the category override could not be read after the write");
                else
                    check.Compare("category_override_unchanged", plan.CategoryOverrideBefore, catNow);
            }
            catch (Exception ex)
            { check.Unreadable("category_override_unchanged", plan.CategoryOverrideBefore, ex.Message); }
            try { check.Compare("view_template_unchanged", plan.TemplateBefore, Rid.Value(view.ViewTemplateId)); }
            catch (Exception ex) { check.Unreadable("view_template_unchanged", plan.TemplateBefore, ex.Message); }
            return check;
        }

        private static PostconditionCheck VerifyCrop(Document doc, Plan plan, double toleranceFeet)
        {
            var check = new PostconditionCheck("crop_active", "crop_visible_unchanged", "crop_shape");
            var view = doc.GetElement(plan.TargetId) as View;
            if (view == null)
            {
                check.Unreadable("crop_active", true, "the view is gone");
                check.Unreadable("crop_visible_unchanged", plan.CropVisibleBefore, "the view is gone");
                check.Unreadable("crop_shape", RequestedCrop(plan), "the view is gone");
                return check;
            }
            try { check.Compare("crop_active", true, view.CropBoxActive); }
            catch (Exception ex) { check.Unreadable("crop_active", true, ex.Message); }
            try
            {
                // A before-value nobody could read is NOT agreement. Recording a match
                // here would claim the crop's visibility was left alone while never
                // having known what it was - the exact "could not measure becomes
                // matches" substitution PostconditionCheck.Unreadable exists to refuse.
                if (plan.CropVisibleBefore.HasValue)
                    check.Compare("crop_visible_unchanged", plan.CropVisibleBefore.Value, view.CropBoxVisible);
                else
                    check.Unreadable("crop_visible_unchanged", JValue.CreateNull(),
                        "CropBoxVisible could not be read before the write, so 'unchanged' cannot be measured");
            }
            catch (Exception ex) { check.Unreadable("crop_visible_unchanged", plan.CropVisibleBefore, ex.Message); }
            PlanBox after = ReadCropBox(view);
            if (!after.Valid)
                check.Unreadable("crop_shape", RequestedCrop(plan), "the committed crop shape would not read");
            else
            {
                bool matches = Math.Abs(after.MinX - plan.CropMinX) <= toleranceFeet &&
                               Math.Abs(after.MinY - plan.CropMinY) <= toleranceFeet &&
                               Math.Abs(after.MaxX - plan.CropMaxX) <= toleranceFeet &&
                               Math.Abs(after.MaxY - plan.CropMaxY) <= toleranceFeet;
                check.Record("crop_shape", RequestedCrop(plan),
                             PlanimetryFixRules.CanonicalPoint2D(after.MinX, after.MinY) + ";" +
                             PlanimetryFixRules.CanonicalPoint2D(after.MaxX, after.MaxY),
                             matches);
            }
            return check;
        }

        /// <summary>The crop as a view-plane rectangle, read the way the INVENTORY reads
        /// it - shape when there is one, CropBox otherwise, every point projected.</summary>
        private static PlanBox ReadCropBox(View view)
        {
            try
            {
                // The same two-step read the INVENTORY makes - shape manager first,
                // CropBox corners with its own transform otherwise - so the fix and the
                // audit cannot disagree about what "the crop" is.
                var points = new List<XYZ>();
                ViewCropRegionShapeManager manager = view.GetCropRegionShapeManager();
                try
                {
                    foreach (CurveLoop loop in manager.GetCropShape())
                        foreach (Curve curve in loop)
                            points.AddRange(curve.Tessellate());
                }
                catch { points.Clear(); }
                if (points.Count == 0)
                {
                    BoundingBoxXYZ box = view.CropBox;
                    if (box != null)
                    {
                        Transform t = box.Transform ?? Transform.Identity;
                        foreach (double x in new[] { box.Min.X, box.Max.X })
                            foreach (double y in new[] { box.Min.Y, box.Max.Y })
                                foreach (double z in new[] { box.Min.Z, box.Max.Z })
                                    points.Add(t.OfPoint(new XYZ(x, y, z)));
                    }
                }
                if (points.Count == 0) return PlanBox.Unreadable;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (XYZ p in points)
                {
                    XYZ d = p.Subtract(view.Origin);
                    double x = d.DotProduct(view.RightDirection);
                    double y = d.DotProduct(view.UpDirection);
                    if (double.IsNaN(x) || double.IsNaN(y)) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
                if (minX > maxX) return PlanBox.Unreadable;
                return PlanBox.FromCorners(minX, minY, maxX, maxY);
            }
            catch { return PlanBox.Unreadable; }
        }

        // ---- Small helpers. --------------------------------------------------------

        private static string RequestedPoint(Plan plan)
            => PlanimetryFixRules.CanonicalPoint2D(plan.PxFeet, plan.PyFeet);

        private static string RequestedCrop(Plan plan)
            => PlanimetryFixRules.CanonicalPoint2D(plan.CropMinX, plan.CropMinY) + ";" +
               PlanimetryFixRules.CanonicalPoint2D(plan.CropMaxX, plan.CropMaxY);

        private static double Distance2D(double ax, double ay, double bx, double by)
        {
            double dx = ax - bx, dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static T Try<T>(Func<T> f, T fallback = default(T))
        {
            try { return f(); } catch { return fallback; }
        }

        private static JObject ToleranceJson(double toleranceFeet, double displayScale, string units)
        {
            return new JObject
            {
                ["value"] = PlanimetryGeometry.Display(toleranceFeet, displayScale),
                ["units"] = units,
                ["meaning"] = "A committed point or crop edge must land within this distance of the request, " +
                              "or the whole batch rolls back."
            };
        }

        private static JObject SourceAuditJson(string given, string recomputed, bool matches, string sourceUnits)
        {
            return new JObject
            {
                ["finding_set_fingerprint"] = given,
                ["units"] = sourceUnits,
                ["units_note"] = "Every observed value was recomputed and compared in THESE units, which " +
                                 "default to mm when source_audit.units is omitted. An audit that ran in m or " +
                                 "feet and did not say so will see every geometric finding refused as a stale " +
                                 "observation - the model did not move, the units did.",
                ["recomputed_fingerprint"] = recomputed.Substring(0, Math.Min(16, recomputed.Length)),
                ["matches_current_model"] = matches,
                ["meaning"] = matches
                    ? "The audit recomputed NOW produces exactly the finding set the caller cited."
                    : "The recomputed finding set differs from the cited one. That alone refuses NOTHING - each " +
                      "action's own finding is checked by identity and observed state - and it is routinely " +
                      "false for a correct call: this recomputation is always whole-model with advisories " +
                      "included, so an audit that used scope, sheet_ids, view_ids, checks or " +
                      "include_advisory=false will legitimately fingerprint differently. Read it as provenance, " +
                      "not as a verdict."
            };
        }

        private static JArray FixCatalog()
        {
            var catalog = new JArray();
            foreach (var kv in PlanimetryFixRules.UniversalRemedyCatalog())
                catalog.Add(new JObject
                {
                    ["rule_id"] = kv.Key,
                    ["operations"] = new JArray(kv.Value.Select(v => (JToken)v))
                });
            return catalog;
        }

        private static JObject ReconciliationJson(PlanimetryFixRules.Reconciliation rec,
                                                  PlanimetrySnapshot before, PlanimetrySnapshot after,
                                                  List<PlanimetryFinding> afterFindings)
        {
            var newListed = rec.New.Take(MaxReportedNewFindings).ToList();
            return new JObject
            {
                ["audit_rerun"] = "full",
                ["audit_rerun_reason"] = "A partial re-evaluation of only the affected checks is not " +
                    "demonstrably equivalent - overlap, containment and coverage are cross-entity - so the " +
                    "complete universal catalog (and the requirement set, when given) ran again.",
                ["selected"] = rec.SelectedCount,
                ["resolved_total"] = rec.ResolvedKeys.Count,
                ["resolved"] = new JArray(rec.ResolvedKeys.Select(k => (JToken)k)),
                ["persistent_total"] = rec.Persistent.Count,
                ["persistent"] = new JArray(rec.Persistent.Select(f => (JToken)f.ToJson())),
                ["new_total"] = rec.New.Count,
                ["new_findings"] = new JArray(newListed.Select(f => (JToken)f.ToJson())),
                ["new_findings_listed"] = newListed.Count,
                ["findings_after_total"] = afterFindings.Count(f => f.Status != "passed"),
                // A selected finding the re-audit could not DECIDE about, because a
                // collection pass died and left its population empty. Absence from an
                // uncollected population is not absence of defect.
                ["undetermined_total"] = rec.UndeterminedKeys.Count,
                ["undetermined"] = new JArray(rec.UndeterminedKeys.Select(k => (JToken)k)),
                ["undetermined_reason"] = rec.UndeterminedReason == null
                    ? (JToken)JValue.CreateNull() : rec.UndeterminedReason,
                ["new_findings_complete"] = rec.NewIsComplete,
                ["coverage_before"] = before.CoverageJson(),
                ["coverage_after"] = after.CoverageJson(),
                ["resolved_means"] = "The finding's own rule, re-run over the committed model, no longer " +
                    "produces a finding with this identity, AND every collection pass that could have " +
                    "produced it actually ran. A postcondition that verified does NOT by itself make a " +
                    "finding resolved; a persistent finding is reported as persistent even when the typed " +
                    "write landed exactly as requested; and a finding whose population was never collected " +
                    "is undetermined, never resolved.",
                ["new_findings_complete_means"] = "False when a collection pass died on either side, which " +
                    "makes the new-finding list a LOWER BOUND rather than the answer."
            };
        }

        /// <summary>The corrections this phase deliberately refuses, published so their
        /// absence is a decision and never a gap.</summary>
        private static JArray NotCovered()
        {
            return new JArray
            {
                new JObject { ["capability"] = "automatic sheet packing",
                              ["reason"] = "A LAYOUT decision handled by horizun_pack_sheets, not inferred from an audit finding." },
                new JObject { ["capability"] = "auto-tagging",
                              ["reason"] = "Handled by horizun_plan_annotations operation=auto_tags and then horizun_annotate." },
                new JObject { ["capability"] = "dimensioning by intent",
                              ["reason"] = "Handled by horizun_plan_annotations operation=intent_dimension and then horizun_annotate." },
                new JObject { ["capability"] = "revision generation",
                              ["reason"] = "Handled by horizun_manage_revisions after explicit approval of the revision record and cloud loops." },
                new JObject { ["capability"] = "visual judgement",
                              ["reason"] = "Handled by the planimetry-review MCP prompt over direct sheet images; this command reads database facts only." },
                new JObject { ["capability"] = "choosing types, positions, names or standards",
                              ["reason"] = "Every final value arrives explicit in the request. A missing " +
                                           "instruction is not permission to choose." }
            };
        }
    }
}
