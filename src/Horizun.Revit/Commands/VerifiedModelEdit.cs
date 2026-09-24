// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE VERIFIED WRITE, THE SAME WAY FOR THREE COMMANDS (styles, units, electrical).
//
// Each of those commands is a menu of small, single-subject writes: set a
// category's line weight, change the length format, put a circuit on a panel.
// They all need the identical ritual, so it lives here once:
//
//   * dry_run (the default) APPLIES the edit inside a transaction, regenerates,
//     re-reads every requested property through a PostconditionCheck and ROLLS
//     BACK, reporting Revit's rollback status. The token is bound to the
//     measured "before" state of the subject.
//   * apply spends the token, runs the edit inside a TransactionGroup, re-reads
//     before and after the inner commit, and only assimilates when the whole
//     checklist is verified. Anything else rolls the group back - a write whose
//     verification fails does not stay in the model.
//   * after assimilation the checklist is taken ONE more time from the committed
//     model; that last reading is what the reply publishes.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class ModelEdit
    {
        public string Tool;
        public string Operation;
        /// <summary>A stable identity for the subject (UniqueId, or a synthetic key for a new element).</summary>
        public string Subject;
        public string Category;
        public PlannedAction Action = PlannedAction.Modify;
        public readonly Dictionary<string, string> Before = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>What the edit will do, echoed in the dry run.</summary>
        public JObject Plan = new JObject();
        /// <summary>Performs the write. Runs twice (rehearsal, apply): keep its state in the closure.</summary>
        public Action<Document> Apply;
        /// <summary>Re-reads every requested property from the model.</summary>
        public Func<Document, PostconditionCheck> Verify;
        /// <summary>Ids and values to publish after the commit (created ids, etc.). Optional.</summary>
        public Func<Document, JObject> Result;
        public string TokenNote;
        /// <summary>Stated risk, repeated in the dry run and the apply.</summary>
        public string Warning;
    }

    internal static class VerifiedModelEdit
    {
        public static CommandResult Run(UIApplication app, GateResult gate, JObject request, ModelEdit edit,
                                        params string[] hashFields)
        {
            Document doc = gate.Document;
            var fields = new List<string> { "operation" };
            fields.AddRange(hashFields);
            string hash = DocumentGate.PlanHash(request, fields.ToArray());
            var resolved = new ResolvedPlan
            {
                Command = edit.Tool,
                DocumentKey = gate.Fingerprint,
                RevitVersion = Safe(() => app.Application.VersionNumber),
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolved.Elements.Add(new PlannedElement
            {
                UniqueId = edit.Subject,
                Category = edit.Category,
                Action = edit.Action,
                BeforeValues = edit.Before
            });
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string txName = request.Value<string>("transaction_name") ?? ("Horizun: " + edit.Operation);

            if (dry)
            {
                JObject rehearsal = Rehearse(doc, edit, txName, out bool verified, out bool rolledBack);
                if (!rolledBack)
                    return CommandResult.FailWithDetail("The rehearsal rollback was not confirmed; model state is uncertain.",
                        new JObject { ["state"] = "uncertain", ["rehearsal"] = rehearsal },
                        FallbackSignal.NotAllowed("rollback_unconfirmed", true), null);
                if (!verified)
                    return CommandResult.FailWithDetail(
                        "The rehearsal could not apply and verify '" + edit.Operation + "'" +
                        (rehearsal.Value<string>("error") is string e && e.Length > 0 ? ": " + e : "") +
                        ". Nothing was committed.",
                        new JObject { ["state"] = "refused", ["plan"] = edit.Plan, ["rehearsal"] = rehearsal },
                        FallbackSignal.NotAllowed("rehearsal_failed", false), null);
                DocumentGate.RecordResolvedPlan(resolved);
                var result = new JObject
                {
                    ["dry_run"] = true,
                    ["operation"] = edit.Operation,
                    ["plan"] = edit.Plan,
                    ["before"] = JObject.FromObject(edit.Before),
                    ["rehearsal"] = rehearsal
                };
                if (edit.Warning != null) result["warning"] = edit.Warning;
                ApplicationOutcome.StampRehearsal(result, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(result, gate, edit.Tool, hash, true,
                    edit.TokenNote ?? "the token binds the measured state of the subject before the change.");
                return CommandResult.Ok(result);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, edit.Tool, hash, resolved, null);
            if (refusal != null) return refusal;

            PostconditionCheck inner = null;
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Could not start the TransactionGroup. Nothing was written.");
                var tx = new Transaction(doc, txName);
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                    edit.Apply(doc);
                    doc.Regenerate();
                    inner = edit.Verify(doc);
                    if (!inner.AllVerified) throw new InvalidOperationException("a postcondition failed before commit");
                    Guard.Commit(tx, edit.Operation);
                    inner = edit.Verify(doc);
                    if (!inner.AllVerified) throw new InvalidOperationException("a postcondition failed after commit");
                    Guard.Assimilate(group, edit.Operation);
                }
                catch (Exception ex)
                {
                    try { if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx); } catch { }
                    Guard.RollbackResult? rb = null;
                    try { if (group.GetStatus() == TransactionStatus.Started) rb = Guard.RollBack(group); } catch { }
                    bool confirmed = rb.HasValue && rb.Value.Confirmed;
                    return CommandResult.FailWithDetail(
                        "'" + edit.Operation + "' failed and was " + (confirmed ? "rolled back" : "NOT confirmed rolled back") +
                        ": " + ex.Message,
                        new JObject
                        {
                            ["state"] = confirmed ? "rolled_back" : "uncertain",
                            ["group_rollback"] = rb.HasValue ? rb.Value.StatusName : "unknown",
                            ["postconditions"] = inner?.ToJson()
                        },
                        FallbackSignal.NotAllowed("write_failed", true), null);
                }
                finally { tx.Dispose(); }
            }

            PostconditionCheck committed = edit.Verify(doc);
            var done = new JObject
            {
                ["dry_run"] = false,
                ["operation"] = edit.Operation,
                ["state"] = committed.AllVerified ? "committed_verified" : "uncertain",
                ["host_verified"] = committed.AllVerified,
                ["before"] = JObject.FromObject(edit.Before),
                ["postconditions"] = committed.ToJson()
            };
            if (edit.Result != null)
            {
                try { done["result"] = edit.Result(doc); }
                catch (Exception ex) { done["result_error"] = ex.Message; }
            }
            if (edit.Warning != null) done["warning"] = edit.Warning;
            int ok = committed.AllVerified ? 1 : 0;
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, 1, ok, ok, 0,
                committed.AllVerified || !committed.AllMeasured ? 0 : 1, committed.AllMeasured ? 0 : 1);
            DocumentGate.StampConfirmation(done, gate, edit.Tool, hash, false);
            if (!committed.AllVerified)
                return CommandResult.FailWithDetail(
                    "The group assimilated but the committed model no longer re-reads as requested; state is uncertain.", done,
                    FallbackSignal.NotAllowed("post_commit_mismatch", true), null);
            return CommandResult.Ok(done);
        }

        private static JObject Rehearse(Document doc, ModelEdit edit, string txName, out bool verified, out bool rolledBack)
        {
            verified = false; rolledBack = false;
            PostconditionCheck check = null; string error = null; string rollback;
            using (var tx = new Transaction(doc, txName + " (rehearsal)"))
            {
                try
                {
                    if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                    edit.Apply(doc);
                    doc.Regenerate();
                    check = edit.Verify(doc);
                    verified = check.AllVerified;
                }
                catch (Exception ex) { error = ex.Message; verified = false; }
                try
                {
                    Guard.RollbackResult r = Guard.RollBack(tx);
                    rollback = r.StatusName; rolledBack = r.Confirmed;
                }
                catch (Exception ex) { rollback = "exception: " + ex.Message; rolledBack = false; }
            }
            return new JObject
            {
                ["applied_and_verified"] = verified,
                ["rollback_status"] = rollback,
                ["error"] = error == null ? (JToken)JValue.CreateNull() : error,
                ["postconditions"] = check?.ToJson()
            };
        }

        // ---- small shared helpers for the three commands ---------------------------------

        internal static string Safe(Func<string> f) { try { return f(); } catch { return null; } }

        internal static bool TryParseColor(string hex, out Color color)
        {
            color = null;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string h = hex.Trim().TrimStart('#');
            if (h.Length != 6) return false;
            try
            {
                color = new Color(Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16),
                                  Convert.ToByte(h.Substring(4, 2), 16));
                return true;
            }
            catch { return false; }
        }

        internal static string Hex(Color c)
        {
            try { return c != null && c.IsValid ? "#" + c.Red.ToString("X2") + c.Green.ToString("X2") + c.Blue.ToString("X2") : null; }
            catch { return null; }
        }

        /// <summary>The operation name, lower-cased; null for a missing one.</summary>
        internal static string Operation(JObject request) => (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();

        internal static CommandResult UnknownOperation(string tool, string op, string known)
            => CommandResult.FailWithFallback(
                tool + " has no operation '" + op + "'. Known: " + known + ". Nothing was read or written.",
                FallbackSignal.Allowed(FallbackSignal.ReasonUnsupportedOperation), null);

        internal static JObject Parse(string paramsJson, out CommandResult error)
        {
            error = null;
            try { return string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { error = CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); return null; }
        }

        /// <summary>A read reply, declared as the no-op it is (plans read every child's declaration).</summary>
        internal static CommandResult ReadReply(JObject payload)
        {
            ApplicationOutcome.StampApplied(payload, ApplicationOutcome.NotStarted, 0, 0, 0, 0, 0, 0);
            return CommandResult.Ok(payload);
        }
    }
}
