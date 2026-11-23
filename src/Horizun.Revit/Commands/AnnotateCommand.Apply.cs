using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using VerifyStage = Horizun.Revit.Core.DimensionPlanRules.VerifyStage;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        private CommandResult ApplyConfirmed(Document doc, string txName, List<Plan> plans, bool bestEffort,
                                             List<Plan> applyPlans, Rehearsal rehearsal, AnnotationBudget budget = null)
            => bestEffort ? ApplyBestEffort(doc, txName, plans, applyPlans, rehearsal, budget) : ApplyAtomic(doc, txName, plans, budget);

        private CommandResult ApplyAtomic(Document doc, string txName, List<Plan> plans, AnnotationBudget budget)
        {
            CommandResult failure = null;
            var group = new TransactionGroup(doc, txName);
            using (new ApplyResource(group, () => failure, "transaction_group"))
            {
                group.Start();
                var inTxRows = new JArray(); int inTxVerified = 0;
                var tx = new Transaction(doc, txName);
                using (new ApplyResource(tx, () => failure, "transaction"))
                {
                    tx.Start();
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new SilenceWarnings());
                    opts.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(opts);
                    try
                    {
                        foreach (Plan p in plans)
                        {
                            budget?.StartRow(p.Index);
                            Element e = Create(doc, p); p.Created = e?.Id;
                            Dimension d = e as Dimension; if (d != null) Decorate(d, p);
                        }
                        budget?.Check();
                        doc.Regenerate();
                        budget?.Check();
                        // Verify while the transaction is still reversible: a postcondition
                        // that fails here rolls back a write nobody has been told about yet.
                        foreach (Plan p in plans)
                        {
                            bool ok; JObject row = VerifyRow(doc, p, VerifyStage.InTransaction, out ok);
                            inTxRows.Add(row); if (ok) inTxVerified++;
                        }
                        if (inTxVerified != plans.Count)
                        {
                            string rbTx = RollbackStatus(tx.GetStatus, () => Guard.RollBack(tx));
                            string rbGroup = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                            string state = DimensionPlanRules.FinalState(false,
                                new[] { rbTx, rbGroup });
                            return failure = FailAfterRollback(
                                (plans.Count - inTxVerified) + " of " + plans.Count + " annotation(s) failed " +
                                "verification before the commit. Each row lists every requested/read comparison.",
                                state, rbTx, rbGroup, inTxRows);
                        }
                        budget?.Check();
                        Guard.Commit(tx, txName);
                    }
                    catch (Exception ex)
                    {
                        string rbTxStatus = RollbackStatus(tx.GetStatus, () => Guard.RollBack(tx));
                        string rbGrp = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                        string state = DimensionPlanRules.FinalState(false, new[] { rbTxStatus, rbGrp });
                        return failure = FailAfterRollback("Atomic annotation failed: " + ApplyFailureReason(ex) + ".",
                            state, rbTxStatus, rbGrp, inTxRows, ex);
                    }
                }
                // Committed inside the group. MEASURED (2025, 2026-08-24, twice): Revit
                // does not materialise AreReferencesAvailable or dimension values on the
                // inner commit alone - a correct dimension still read false/0 here. A
                // regeneration AFTER the commit, in its own committed transaction still
                // inside the group, is what asks Revit to compute them; the group remains
                // open, so a verification that fails after this still rolls EVERYTHING
                // back. The regeneration writes nothing of its own.
                failure = MaterialiseForVerification(doc, txName, group, inTxRows, budget);
                if (failure != null) return failure;
                // The only evidence that counts now is a FRESH read of every annotation;
                // a re-read that fails rolls the group back and takes the committed
                // transaction with it.
                var rows = new JArray();
                int verified;
                JObject rollback;
                try
                {
                    rollback = DimensionPlanRules.VerifyCommittedBatch(plans, rows,
                        (Plan p, VerifyStage stage, out bool ok) => VerifyRow(doc, p, stage, out ok),
                        () => budget?.Check(), () => RollbackStatus(group.GetStatus, () => Guard.RollBack(group)),
                        out verified);
                }
                catch (AnnotationBudgetExceeded ex)
                {
                    string rbGroup = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                    return failure = FailAfterRollback(ex.Message,
                        DimensionPlanRules.FinalState(false, new[] { rbGroup }), "Committed", rbGroup, rows, ex);
                }
                if (rollback != null)
                    return failure = FailAfterRollback(
                        "The transaction committed, but " + (plans.Count - verified) + " of " + plans.Count +
                        " annotation(s) failed the post-commit re-read. Each row lists every requested/read comparison.",
                        rollback);
                try { Guard.Assimilate(group, txName); }
                catch (Exception ex)
                {
                    string rbStatus = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                    string state = DimensionPlanRules.FinalState(false, new[] { rbStatus });
                    return failure = FailAfterRollback(
                        "Every annotation verified, but the TransactionGroup would not assimilate: " + ApplyFailureReason(ex),
                        state, "Committed", rbStatus, rows);
                }
                var anResult = new JObject
                {
                    ["transaction_status"] = "Committed",
                    ["transaction_group_status"] = "Committed",
                    ["state"] = DimensionPlanRules.StateCommittedVerified,
                    ["annotations_verified"] = verified,
                    ["comparison_tolerance_feet"] = DimensionPlanRules.CurveToleranceFeet,
                    ["units_note"] = "Measured values are Revit internal units (decimal feet); 'presented' is the " +
                                     "document's own display formatting.",
                    ["rows"] = rows
                };
                // Reached only when verified == plans.Count; a shortfall rolled back above.
                ApplicationOutcome.StampApplied(anResult, ApplicationOutcome.Committed,
                                                plans.Count, verified, verified, 0, 0, 0);
                return CommandResult.Ok(anResult);
            }
        }

        // ---------------------------------------------------------------------
        // on_failure=best_effort apply.
        //
        // The rows the apply's own rehearsal could not construct are SKIPPED (never
        // attempted). Each remaining row is created in its own SubTransaction, so a row
        // that throws now is rolled back alone and reported as failed. Every created row
        // is verified while the transaction is still open; one that fails is DELETED
        // inside that same transaction - and only when Revit's Delete removes exactly
        // that one element - after which the survivors are verified again. Anything
        // that cannot be removed that precisely rolls the WHOLE batch back.
        // After the commit (the group still open) a row that fails its re-read is NOT
        // removed piecemeal: the whole group is rolled back, because a second write to
        // repair a committed batch is a new, unrehearsed edit. Safest, and said.
        // ---------------------------------------------------------------------
        private CommandResult ApplyBestEffort(Document doc, string txName, List<Plan> plans, List<Plan> applyPlans,
                                              Rehearsal rehearsal, AnnotationBudget budget)
        {
            var applySet = new HashSet<int>(applyPlans.Select(p => p.Index));
            var outcomeRows = new JArray();
            foreach (JObject r in rehearsal.Rows.OfType<JObject>())
                if (!applySet.Contains(r.Value<int>("index")))
                    outcomeRows.Add(new JObject { ["index"] = r["index"], ["operation"] = r["operation"],
                        ["outcome"] = "skipped", ["reason"] = r["reason"] });
            int skipped = outcomeRows.Count;
            var failed = new Dictionary<int, JObject>();
            var kept = new List<Plan>();
            var inTxRows = new Dictionary<int, JObject>();
            CommandResult failure = null;
            var group = new TransactionGroup(doc, txName);
            using (new ApplyResource(group, () => failure, "transaction_group"))
            {
                group.Start();
                var tx = new Transaction(doc, txName);
                using (new ApplyResource(tx, () => failure, "transaction"))
                {
                    tx.Start();
                    var opts = tx.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new SilenceWarnings());
                    opts.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(opts);
                    try
                    {
                        foreach (Plan p in applyPlans)
                        {
                            budget?.StartRow(p.Index);
                            using (var st = new SubTransaction(doc))
                            {
                                st.Start();
                                try
                                {
                                    Element e = Create(doc, p);
                                    if (e == null) throw new InvalidOperationException("Revit returned no element");
                                    p.Created = e.Id;
                                    Dimension d = e as Dimension; if (d != null) Decorate(d, p);
                                    Guard.Commit(st, "annotate row " + p.Index);
                                    kept.Add(p);
                                }
                                catch (Exception ex)
                                {
                                    p.Created = null;
                                    // A row whose rollback is not CONFIRMED may have left something
                                    // behind: that is not best-effort any more, the whole batch goes.
                                    if (st.GetStatus() == TransactionStatus.Started)
                                    {
                                        Guard.RollbackResult rbRow = Guard.RollBack(st);
                                        if (!rbRow.Confirmed && !(ex is AnnotationBudgetExceeded))
                                            throw new InvalidOperationException("row " + p.Index + " failed (" + ex.Message +
                                                ") and its sub-transaction rollback reported '" + rbRow.StatusName + "'");
                                    }
                                    if (ex is AnnotationBudgetExceeded) throw;
                                    failed[p.Index] = new JObject { ["index"] = p.Index, ["operation"] = p.Operation,
                                        ["outcome"] = "failed", ["stage"] = "create", ["reason"] = ex.Message,
                                        ["in_model"] = false };
                                }
                            }
                        }
                        budget?.Check();
                        doc.Regenerate();
                        budget?.Check();
                        var drop = new List<Plan>();
                        foreach (Plan p in kept)
                        {
                            bool ok; JObject row = VerifyRow(doc, p, VerifyStage.InTransaction, out ok);
                            if (ok) inTxRows[p.Index] = row;
                            if (!ok)
                            {
                                drop.Add(p);
                                failed[p.Index] = new JObject { ["index"] = p.Index, ["operation"] = p.Operation,
                                    ["outcome"] = "failed", ["stage"] = "verify_before_commit",
                                    ["reason"] = "created, but it did not verify; removed before the commit",
                                    ["verification"] = row };
                            }
                        }
                        // A deletion attempt invalidates the earlier reads of the survivors.
                        if (drop.Count > 0) inTxRows.Clear();
                        foreach (Plan p in drop)
                        {
                            budget?.Check();
                            ICollection<ElementId> gone = doc.Delete(p.Created);
                            if (gone == null || gone.Count != 1 || !gone.Contains(p.Created))
                                throw new InvalidOperationException("removing the unverified row " + p.Index + " would delete " +
                                    (gone == null ? 0 : gone.Count) + " element(s), not exactly its own annotation");
                            failed[p.Index]["in_model"] = false;
                            kept.Remove(p); p.Created = null;
                        }
                        if (drop.Count > 0)
                        {
                            budget?.Check();
                            doc.Regenerate();
                            budget?.Check();
                            var unverified = new List<int>();
                            foreach (Plan p in kept)
                            {
                                bool ok; inTxRows[p.Index] = VerifyRow(doc, p, VerifyStage.InTransaction, out ok);
                                if (!ok) unverified.Add(p.Index);
                            }
                            if (unverified.Count > 0)
                                throw new InvalidOperationException("after removing the unverified row(s), row " +
                                    unverified[0] + " no longer verifies" +
                                    (unverified.Count == 1 ? "" : " (all failed rows: " + string.Join(", ", unverified) + ")"));
                        }
                        if (kept.Count == 0)
                        {
                            string rbTx0 = RollbackStatus(tx.GetStatus, () => Guard.RollBack(tx));
                            string rbG0 = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                            foreach (JObject f in failed.Values) outcomeRows.Add(f);
                            return failure = FailAfterRollback("on_failure=best_effort: no row could be created and verified. See rows.",
                                DimensionPlanRules.FinalState(false, new[] { rbTx0, rbG0 }),
                                rbTx0, rbG0, Sorted(outcomeRows));
                        }
                        budget?.Check();
                        Guard.Commit(tx, txName);
                    }
                    catch (Exception ex)
                    {
                        string rbTxStatus = RollbackStatus(tx.GetStatus, () => Guard.RollBack(tx));
                        string rbGrp = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                        return failure = FailAfterRollback("on_failure=best_effort could not complete safely: " + ApplyFailureReason(ex) + ".",
                            DimensionPlanRules.FinalState(false, new[] { rbTxStatus, rbGrp }),
                            rbTxStatus, rbGrp, BeforeCommitEvidence(), ex);
                    }
                }
                failure = MaterialiseForVerification(doc, txName, group, BeforeCommitEvidence(), budget);
                if (failure != null) return failure;
                int verified;
                JObject rollback;
                try
                {
                    rollback = DimensionPlanRules.VerifyCommittedBatch(kept, outcomeRows,
                        (Plan p, VerifyStage stage, out bool ok) => VerifyRow(doc, p, stage, out ok),
                        () => budget?.Check(), () => RollbackStatus(group.GetStatus, () => Guard.RollBack(group)),
                        out verified, failed.Values);
                }
                catch (AnnotationBudgetExceeded ex)
                {
                    string rbGroup = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                    return failure = FailAfterRollback(ex.Message,
                        DimensionPlanRules.FinalState(false, new[] { rbGroup }), "Committed", rbGroup, Sorted(outcomeRows), ex);
                }
                if (rollback != null)
                    return failure = FailAfterRollback(
                        "on_failure=best_effort: " + (kept.Count - verified) + " committed row(s) failed the post-commit " +
                        "re-read.",
                        rollback);
                try { Guard.Assimilate(group, txName); }
                catch (Exception ex)
                {
                    string rbStatus = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                    return failure = FailAfterRollback(
                        "Every created annotation verified, but the TransactionGroup would not assimilate: " + ApplyFailureReason(ex),
                        DimensionPlanRules.FinalState(false, new[] { rbStatus }), "Committed", rbStatus, Sorted(outcomeRows));
                }
                var result = new JObject
                {
                    ["transaction_status"] = "Committed",
                    ["transaction_group_status"] = "Committed",
                    ["state"] = DimensionPlanRules.StateCommittedVerified,
                    ["on_failure"] = AnnotationNoteRules.BestEffort,
                    ["requested"] = plans.Count, ["created"] = verified, ["skipped"] = skipped, ["failed"] = failed.Count,
                    ["annotations_verified"] = verified,
                    ["comparison_tolerance_feet"] = DimensionPlanRules.CurveToleranceFeet,
                    ["note"] = "Only rows with outcome 'created' are in the model, each re-read after the commit. " +
                               "'skipped' rows were not constructible in the rehearsal; 'failed' rows were rolled back " +
                               "or removed before the commit.",
                    ["rows"] = Sorted(outcomeRows)
                };
                ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, plans.Count, verified, verified,
                                                skipped, failed.Count, 0);
                return CommandResult.Ok(result);
            }

            JArray BeforeCommitEvidence() => Sorted(new JArray(outcomeRows.OfType<JObject>()
                .Concat(inTxRows.Values).Concat(failed.Values)));
        }

        private static CommandResult MaterialiseForVerification(Document doc, string txName, TransactionGroup group, JArray rows, AnnotationBudget budget)
        {
            CommandResult failure = null;
            var regen = new Transaction(doc, txName + " (materialise for verification)");
            using (new ApplyResource(regen, () => failure, "verification_transaction"))
            {
                try
                {
                    budget?.Check();
                    regen.Start();
                    budget?.Check();
                    doc.Regenerate();
                    budget?.Check();
                    Guard.Commit(regen, txName);
                    return null;
                }
                catch (Exception ex)
                {
                    string regenStatus = RollbackStatus(regen.GetStatus, () => Guard.RollBack(regen));
                    // A closed auxiliary transaction permits a fresh read after a recoverable
                    // error. A fatal regeneration failure requires aborting the group.
                    if (regenStatus == "RolledBack" &&
                        !(ex is Autodesk.Revit.Exceptions.RegenerationFailedException) && !(ex is AnnotationBudgetExceeded)) return null;
                    string groupStatus = RollbackStatus(group.GetStatus, () => Guard.RollBack(group));
                    failure = FailAfterRollback("Verification preparation failed with status '" + regenStatus +
                        "': " + ApplyFailureReason(ex) + ".", DimensionPlanRules.FinalState(false, new[] { groupStatus }),
                        "Committed", groupStatus, rows, ex);
                    failure.Detail["verification_transaction_status"] = regenStatus;
                    return failure;
                }
            }
        }

        private static JArray Sorted(JArray rows) =>
            DimensionPlanRules.SortAnnotationRows(rows);

        /// <summary>One response row for an apply phase - legacy fields kept, new ones added.</summary>
        private JObject VerifyRow(Document doc, Plan p, VerifyStage stage, out bool ok)
        {
            p.Budget?.Check();
            Element e = p.Created == null ? null : doc.GetElement(p.Created);
            if (DimensionPlanRules.IsDimensionOperation(p.Operation))
                return VerifyDimensionRow(doc, p, e, stage, out ok);

            string why;
            ok = Verify(p, e, out why);
            var legacy = new JObject
            {
                ["index"] = p.Index, ["operation"] = p.Operation,
                ["element_id"] = p.Created == null ? JValue.CreateNull() : new JValue(Rid.Value(p.Created)),
                ["verified"] = ok
            };
            if (!ok && why != null) legacy["reason"] = why;
            if (p.TagCoverage != null && p.TagCoverage.HasValues)
                legacy["annotation_coverage"] = p.TagCoverage.DeepClone();
            if (e != null) { try { legacy["unique_id"] = e.UniqueId; } catch { legacy["unique_id"] = null; } }
            AddTextEvidence(legacy, p);
            AddTagEvidence(legacy, p, e, ref ok);
            return legacy;
        }

        // Guard's silent-rollback message assumes nothing was written. Pending
        // and Error do not prove that. The statuses below decide what we can say.
        private static string ApplyFailureReason(Exception ex)
            => ex is SilentRollbackException ? "Revit did not confirm the commit" : ex.Message;

        private sealed class ApplyResource : IDisposable
        {
            private readonly IDisposable resource;
            private readonly Func<CommandResult> failure;
            private readonly string name;

            public ApplyResource(IDisposable resource, Func<CommandResult> failure, string name)
            {
                this.resource = resource;
                this.failure = failure;
                this.name = name;
            }

            public void Dispose()
            {
                try { resource.Dispose(); }
                catch (Exception ex) when (failure()?.Detail != null)
                {
                    // Disposal can also fail in failure mode. Retain the original
                    // verification rows and rollback evidence for the caller.
                    failure().Detail[name + "_dispose_error"] = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        private static string RollbackStatus(Func<TransactionStatus> readStatus, Func<Guard.RollbackResult> rollBack)
        {
            TransactionStatus before;
            try { before = readStatus(); }
            catch (Exception ex) { return "GetStatus threw: " + ex.GetType().Name + ": " + ex.Message; }
            if (before != TransactionStatus.Started) return before.ToString();
            try { return rollBack().StatusName; }
            catch (Exception ex)
            {
                // A pending inner transaction can prevent closing the group. Keep the
                // observed status and the exception without claiming it rolled back.
                return before + " (RollBack threw: " + ex.GetType().Name + ": " + ex.Message + ")";
            }
        }

        private static CommandResult FailAfterRollback(string reason, string state, string txStatus, string groupStatus, JArray rows, Exception cause = null)
            => FailAfterRollback(reason, DimensionPlanRules.ApplyFailureDetail(state, txStatus, groupStatus, rows), cause);

        private static CommandResult FailAfterRollback(string reason, JObject detail, Exception cause = null)
        {
            (cause as AnnotationBudgetExceeded)?.AddDetail(detail);
            string outcome = detail.Value<bool>("rollback_confirmed")
                ? "The batch was rolled back. Nothing from this call remains in the model."
                : "Revit did not confirm the rollback. Re-read the model before any retry.";
            return CommandResult.FailWithDetail(reason + " The transaction status is '" + detail.Value<string>("transaction_status") +
                "' and the group status is '" + detail.Value<string>("transaction_group_status") + "'. " + outcome, detail);
        }

    }
}
