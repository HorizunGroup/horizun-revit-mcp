// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A TRANSFORM REHEARSAL THAT SAYS "YES" AND THEN THE APPLY FAILS IS THE DEFECT.
//
// Field telemetry (30 days): 21 of the horizun_transform_elements failures came AT APPLY after a rehearsal
// that had passed (transaccion_revertida 8, error_interno 8, rollback 3, verificacion_fallida 2). The
// rehearsal of every operation but rename_level and the sketch moves only validated arguments.
//
// The rehearsal now runs the very same application path inside a TransactionGroup (the inner transaction
// commits, the post-commit verification reads the committed model) and rolls the whole group back. This
// file is the Revit-free half: it reads the verdict of that run, attributes a failure to rows of the batch
// and decides when the model state is uncertain. The Revit half only records and rolls back.
//
// A batch is ONE transaction, so a failure rolls back every row. The verdict per row therefore says which
// rows Revit (or the verification) named, which rows were never reached, and which were rolled back with
// the batch without being implicated - never "passed" for a batch that would not commit.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class TransformRehearsalRules
    {
        public const string RefusedBy = "revit_rehearsal";
        public const string ValidationLevel = "revit_transform_rolled_back";

        // Phases of the application path, in the order the apply runs them.
        public const string PhaseApply = "apply";
        public const string PhaseHosts = "host_guard";
        public const string PhasePrecommit = "precommit_check";
        public const string PhaseCommit = "commit";
        public const string PhasePostcommit = "postcommit_verification";

        public const string RowPassed = "passed";
        public const string RowFailed = "failed";
        public const string RowNotReached = "not_reached";
        public const string RowRolledBackWithBatch = "rolled_back_with_batch";

        public const string StateClean = "clean";
        public const string StateFailed = "failed";
        public const string StateUncertain = "uncertain";

        public const int MaxWarnings = 10;

        public sealed class RowRef
        {
            public int Index;
            public string Operation;
        }

        /// <summary>
        /// The state of the model after the rehearsal. UNCERTAIN beats everything: a rollback Revit did
        /// not confirm, or a created element still present after it, means the dry run wrote something.
        /// </summary>
        public static string Decide(bool applySucceeded, bool rollbackConfirmed, bool createdElementsAbsent)
        {
            if (!rollbackConfirmed || !createdElementsAbsent) return StateUncertain;
            return applySucceeded ? StateClean : StateFailed;
        }

        /// <summary>Verdict of a clean rehearsal: every row passed the whole path.</summary>
        public static JArray RowsForSuccess(IEnumerable<RowRef> rows)
        {
            var result = new JArray();
            foreach (RowRef r in rows ?? Enumerable.Empty<RowRef>())
                result.Add(new JObject { ["index"] = r.Index, ["operation"] = r.Operation, ["rehearsal"] = RowPassed });
            return result;
        }

        /// <summary>
        /// Verdict of a failed rehearsal, per row.
        /// culprits: the rows the failing step named (an exception thrown while applying row k names k; a
        /// verification row that did not hold names itself). reached: the last row the apply processed, or
        /// null when the failure was not row-by-row. With no culprit named, no row is blamed: every row
        /// reads rolled_back_with_batch and the reason stays on the batch.
        /// </summary>
        public static JArray RowsForFailure(IEnumerable<RowRef> rows, IEnumerable<int> culprits, int? reached,
                                            string phase, string errorCode, string reason)
        {
            var named = new HashSet<int>(culprits ?? Enumerable.Empty<int>());
            var result = new JArray();
            foreach (RowRef r in rows ?? Enumerable.Empty<RowRef>())
            {
                string status;
                if (named.Contains(r.Index)) status = RowFailed;
                else if (phase == PhaseApply && reached.HasValue && r.Index > reached.Value && named.Count > 0) status = RowNotReached;
                else status = RowRolledBackWithBatch;
                var row = new JObject { ["index"] = r.Index, ["operation"] = r.Operation, ["rehearsal"] = status };
                if (status == RowFailed)
                {
                    row["error_code"] = errorCode;
                    row["reason"] = reason;
                }
                result.Add(row);
            }
            return result;
        }

        /// <summary>The plan indices of the verification rows that did not hold.</summary>
        public static List<int> FailedRows(JArray verification)
        {
            var failed = new List<int>();
            foreach (JObject row in (verification ?? new JArray()).OfType<JObject>())
                if (row["verified"] == null || row.Value<bool?>("verified") != true)
                    if (row["index"] != null && row["index"].Type == JTokenType.Integer) failed.Add(row.Value<int>("index"));
            return failed;
        }

        public const string ProvisionalIdsNote = "Elements the rehearsal created (copies, array members) were rolled back with the group: " +
            "any element id of that kind named in this reply does not exist in the model and must not be used.";

        private static readonly string[] KeptRowKeys = { "index", "operation", "verified", "targets", "copies_expected", "copy_monitor_alerts" };

        /// <summary>
        /// Verification rows reduced to what is true after the rollback. The apply's rows name the ids of the copies it
        /// made; a rehearsal rolled those back, so the ids are dropped and only the verdict of each row is kept.
        /// </summary>
        public static JArray SanitizeRows(JArray verification)
        {
            var result = new JArray();
            foreach (JObject row in (verification ?? new JArray()).OfType<JObject>())
            {
                var kept = new JObject();
                foreach (string key in KeptRowKeys)
                    if (row[key] != null) kept[key] = row[key].DeepClone();
                result.Add(kept);
            }
            return result;
        }

        /// <summary>
        /// The fields a refused rehearsal adds to the apply's own failure detail. The error_code is the one
        /// the apply would have given and is left as it is; nothing here relabels it.
        /// </summary>
        public static JObject RefusalDetail(JObject applyDetail, string phase, JArray rows, string rollbackStatus, JArray invalidRows, bool createdProvisional = false)
        {
            JObject detail = applyDetail == null ? new JObject() : (JObject)applyDetail.DeepClone();
            detail["dry_run"] = true;
            detail["refused_by"] = RefusedBy;
            detail["apply_would_refuse"] = true;
            detail["write_started"] = false;
            detail["rollback_confirmed"] = true;
            detail["rollback_status"] = rollbackStatus;
            detail["rehearsal_phase"] = phase;
            detail["rehearsal_rows"] = rows ?? new JArray();
            if (invalidRows != null && invalidRows.Count > 0) detail["invalid_rows"] = invalidRows;
            if (detail["rows"] is JArray applyRows) detail["rows"] = SanitizeRows(applyRows);
            if (createdProvisional) detail["provisional_ids_note"] = ProvisionalIdsNote;
            return detail;
        }

        /// <summary>Detail when the rollback of the rehearsal could not be confirmed: the dry run may have written.</summary>
        public static JObject UncertainDetail(string rollbackStatus, string rollbackError, bool createdElementsAbsent)
        {
            return new JObject
            {
                ["error_code"] = ErrorCodes.ErrorInterno,
                ["state"] = StateUncertain,
                ["dry_run"] = true,
                ["refused_by"] = RefusedBy,
                ["write_started"] = true,
                ["rollback_confirmed"] = false,
                ["rollback_status"] = rollbackStatus,
                ["rollback_error"] = rollbackError,
                ["created_elements_absent"] = createdElementsAbsent
            };
        }

        public const string UncertainMessage = "The transform rehearsal ran in Revit but its rollback could not be confirmed; " +
            "model state is uncertain. Read the model before doing anything else.";

        /// <summary>The block a clean rehearsal adds to the dry-run reply.</summary>
        public static JObject CleanBlock(IEnumerable<RowRef> rows, string rollbackStatus, IEnumerable<string> revitWarnings)
        {
            var warnings = (revitWarnings ?? Enumerable.Empty<string>()).Take(MaxWarnings).ToList();
            return new JObject
            {
                ["validation_level"] = ValidationLevel,
                ["rollback_status"] = rollbackStatus,
                ["rollback_confirmed"] = true,
                ["rows"] = RowsForSuccess(rows),
                ["revit_warnings"] = new JArray(warnings)
            };
        }

        /// <summary>
        /// The one-line note of the dry-run reply. Invalid entries keep their partial status: the rows that
        /// resolved were rehearsed, but no token is issued while any entry is invalid.
        /// </summary>
        public static string Note(bool rehearsed, int invalid)
        {
            if (!rehearsed)
                return "Nothing was transformed; no transaction was opened (no entry resolved to an operation).";
            return invalid == 0
                ? "Every operation ran in Revit through the same path as the apply, including the post-commit verification, and the whole group was rolled back; nothing was transformed."
                : "The valid operations ran in Revit through the same path as the apply and were rolled back; nothing was transformed. " +
                  invalid + " invalid entr" + (invalid == 1 ? "y" : "ies") + " block the confirmation token.";
        }
    }
}
