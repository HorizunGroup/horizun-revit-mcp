// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_transform_elements - column_attach / column_detach: join the BASE or the TOP of a structural column to a
// beam, brace, floor, roof or ceiling (Revit's ColumnAttachment, identical in 2023-2027), or release it. The
// column then follows the target: its top stops at the underside of the slab or the beam instead of at a level and
// an offset someone typed.
//
// Its own path, like edit_sketch, and sent alone: the dry run is a REAL rehearsal - the attachments are made inside
// a transaction that is rolled back (rollback confirmed) and the columns' new extents are read from the rehearsal;
// the apply re-reads every attachment (target, end, cut style, justification) from the committed model and rolls
// the whole group back on any disagreement.
//
// Defaults are stated, not guessed: the column is cut by its target (ColumnAttachmentCutStyle.CutColumn) at the
// minimum intersection (ColumnAttachmentJustification.Minimum), with no offset.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class TransformElementsCommand
    {
        private sealed class ColumnAttachRow
        {
            public int OperationIndex;
            public bool Attach;
            public long ColumnId;
            public int End;                 // 0 base, 1 top
            public long TargetId;           // attach only
            public bool AlreadyAttached;    // attach: this column end already holds THIS target
            public double[] ExtentBefore;   // z min, z max (feet)
            public double[] ExtentRehearsed;
        }

        private static double[] ZExtent(Element e)
        {
            BoundingBoxXYZ b = null;
            try { b = e.get_BoundingBox(null); } catch { }
            return b == null ? null : new[] { b.Min.Z, b.Max.Z };
        }

        private static JToken ZJson(double[] z, double scale) =>
            z == null ? JValue.CreateNull() : new JArray(Math.Round(z[0] / scale, 4), Math.Round(z[1] / scale, 4));

        private CommandResult ExecuteColumnAttach(UIApplication app, GateResult gate, JObject request, JArray input)
        {
            Document doc = gate.Document;
            double scale;
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!Scale(units, out scale)) return CommandResult.Fail("units must be mm, m or feet.");
            if (!ColumnAttachRules.BatchIsPure(input.OfType<JObject>().Select(o => o.Value<string>("operation") ?? "")) || input.Any(t => !(t is JObject)))
                return CommandResult.Fail("column_attach and column_detach cannot be mixed with other operations in one call: their dry run really attaches " +
                                          "inside a transaction it rolls back, which would blur what the other operations did. Send them alone.");
            CommandResult memberRefusal = RefuseGroupMembers(doc, input);
            if (memberRefusal != null) return memberRefusal;

            var rows = new List<ColumnAttachRow>();
            var errors = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < input.Count; i++)
            {
                var o = (JObject)input[i];
                try
                {
                    string invalid = ColumnAttachRules.Validate(o);
                    if (invalid != null) throw new ArgumentException(invalid);
                    bool attach = string.Equals(o.Value<string>("operation"), ColumnAttachRules.Attach, StringComparison.OrdinalIgnoreCase);
                    int end = ColumnAttachRules.EndIndex(o.Value<string>("column_end"));
                    Element target = null;
                    if (attach)
                    {
                        long tid = o.Value<long>("attachment_target_id");
                        target = Rid.CanRepresent(tid) ? doc.GetElement(Rid.Make(tid)) : null;
                        if (target == null) throw new ArgumentException("attachment_target_id " + tid + " does not exist in the host document.");
                    }
                    foreach (long raw in o["element_ids"].Select(t => t.Value<long>()))
                    {
                        Element e = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) : null;
                        var column = e as FamilyInstance;
                        if (column == null) throw new ArgumentException("ElementId " + raw + " is " + (e == null ? "not in the host document" : "a " + e.GetType().Name + ", not a structural column") + ".");
                        if (!ColumnAttachment.IsValidColumn(column))
                            throw new ArgumentException("ElementId " + raw + " is not a column that supports attachments (a structural column family instance is needed; slanted columns attach to floors, roofs and ceilings only).");
                        if (!seen.Add(raw + ":" + end)) throw new ArgumentException("column " + raw + " appears twice for its " + (end == 0 ? "base" : "top") + "; combine them deliberately first.");
                        bool pinned; try { pinned = column.Pinned; } catch { pinned = false; }
                        if (pinned) throw new ArgumentException("column " + raw + " is PINNED; unpin it deliberately first.");
                        ColumnAttachment held = ColumnAttachment.GetColumnAttachment(column, end);
                        var row = new ColumnAttachRow { OperationIndex = i, Attach = attach, ColumnId = raw, End = end, ExtentBefore = ZExtent(column) };
                        if (attach)
                        {
                            if (target.Id == column.Id) throw new ArgumentException("A column cannot attach to itself.");
                            if (!ColumnAttachment.IsValidTarget(column, target))
                                throw new ArgumentException("element " + Rid.Value(target.Id) + " (" + (target.Category?.Name ?? target.GetType().Name) +
                                    ") is not a valid attachment target for column " + raw + ": Revit takes beams, braces, floors, roofs and ceilings.");
                            row.TargetId = Rid.Value(target.Id);
                            if (held != null)
                            {
                                if (held.TargetId == target.Id) row.AlreadyAttached = true;
                                else throw new ArgumentException("the " + (end == 0 ? "base" : "top") + " of column " + raw + " is already attached to element " +
                                    Rid.Value(held.TargetId) + "; detach it deliberately first (column_detach).");
                            }
                            else
                            {
                                ColumnAttachment other = ColumnAttachment.GetColumnAttachment(column, target.Id);
                                if (other != null) throw new ArgumentException("column " + raw + " is already attached to element " + Rid.Value(target.Id) +
                                    " at its other end; Revit allows one attachment per target.");
                            }
                        }
                        else if (held == null)
                            throw new ArgumentException("the " + (end == 0 ? "base" : "top") + " of column " + raw + " is not attached to anything, so there is nothing to detach.");
                        rows.Add(row);
                    }
                }
                catch (Exception ex) { errors.Add(TransformFailure.Planning(i, ex.Message)); }
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "operations");
            var resolved = new ResolvedPlan { Command = Name, DocumentKey = gate.Fingerprint, RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest() };
            var planned = new HashSet<long>();
            foreach (ColumnAttachRow r in rows)
            {
                foreach (long id in new[] { r.ColumnId, r.TargetId })
                {
                    if (id <= 0 || !planned.Add(id)) continue;
                    Element e = doc.GetElement(Rid.Make(id));
                    if (e == null) continue;
                    resolved.Elements.Add(new PlannedElement
                    {
                        UniqueId = SafePlanUniqueId(e), ElementId = id, Category = e.Category?.Name, TypeName = SafePlanTypeName(doc, e),
                        Action = PlannedAction.Modify, GeometryFingerprint = SafePlanGeometry(e),
                        BeforeValues = new Dictionary<string, string> { { "operation", "column_attach_family" } },
                        ProposedValues = new Dictionary<string, string> { { "specification", input.ToString(Formatting.None) } }
                    });
                }
            }

            if (dryRun)
            {
                JObject rehearsal = errors.Count == 0 ? RehearseColumnAttach(doc, rows) : null;
                bool ok = errors.Count == 0 && rehearsal != null && rehearsal.Value<bool>("rollback_confirmed") && rehearsal["error"] == null;
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = errors.Count == 0 ? "rehearsed_and_rolled_back" : "not_started",
                    ["operations"] = input.Count, ["targets"] = rows.Count, ["valid_operations"] = errors.Count == 0 ? input.Count : 0,
                    ["invalid_operations"] = errors.Count, ["errors"] = errors,
                    ["plan"] = new JArray(rows.Select(r => RowJson(r, scale, units))),
                    ["rehearsal"] = rehearsal,
                    ["note"] = errors.Count == 0
                        ? "The attachments were made inside a transaction and rolled back (rollback_confirmed); nothing remains in the model. extent_z_* are the column's bounding-box Z min/max in the request's units."
                        : "Nothing was attempted: correct the invalid operations first."
                };
                if (errors.Count == 0 && ok) DocumentGate.RecordResolvedPlan(resolved);
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, ok || errors.Count > 0 ? 0 : 1, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, ok,
                    ok ? "the token binds the ordered operations, the columns, their ends and the targets" : "no usable confirmation is issued while any operation is invalid or the rehearsal fails");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0)
                return CommandResult.Fail("Invalid operations; nothing ran: " + errors.ToString(Formatting.None));
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolved, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: column attachments";
            bool started = false;
            using (var group = new TransactionGroup(doc, txName))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction group did not start");
                    using (var tx = new Transaction(doc, txName))
                    {
                        RevitErrorRecorder recorder = RevitErrorRecorder.On(tx);
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the transaction did not start");
                        started = true;
                        try { ApplyColumnAttach(doc, rows); doc.Regenerate(); Guard.Commit(tx, txName); }
                        catch (Exception ex) { throw new InvalidOperationException(ex.Message + recorder.Said(), ex); }
                    }
                    string disagreement = ReadBackColumnAttach(doc, rows);
                    if (disagreement != null)
                    {
                        Guard.RollbackResult rolled = Guard.RollBack(group);
                        return CommandResult.FailWithDetail("The committed model disagreed with the request, so the whole edit was rolled back: " + disagreement,
                            new JObject { ["error_code"] = ErrorCodes.VerificacionFallida, ["write_started"] = true,
                                ["changes_applied"] = rolled.Confirmed ? (JToken)false : null, ["rollback_status"] = rolled.StatusName });
                    }
                    Guard.Assimilate(group, txName);
                }
                catch (Exception ex)
                {
                    string rb = "not_attempted";
                    try { if (group.GetStatus() == TransactionStatus.Started) rb = Guard.RollBack(group).StatusName; } catch (Exception e2) { rb = "failed: " + e2.Message; }
                    return CommandResult.FailWithDetail(Name + " failed: " + ex.Message, new JObject
                    {
                        ["code"] = ex is ArgumentException ? ErrorCodes.PrecondicionNoCumplida : (rb == "RolledBack" ? ErrorCodes.TransaccionRevertida : "revit_edit_failed"),
                        ["write_started"] = started, ["rollback_status"] = rb,
                        ["changes_applied"] = !started ? (JToken)false : (rb == "RolledBack" ? (JToken)false : null)
                    });
                }
            }
            string late = ReadBackColumnAttach(doc, rows);
            if (late != null)
                return CommandResult.FailWithDetail("Committed, but the re-read after the group assimilated disagrees; inspect the model: " + late,
                    new JObject { ["error_code"] = ErrorCodes.VerificacionFallida, ["write_started"] = true, ["changes_applied"] = true });
            foreach (ColumnAttachRow r in rows) r.ExtentRehearsed = ZExtent(doc.GetElement(Rid.Make(r.ColumnId)));
            var applied = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["operations_verified"] = input.Count, ["targets"] = rows.Count,
                ["rows"] = new JArray(rows.Select(r => { JObject j = RowJson(r, scale, units); j["verified"] = true; return j; })),
                ["undo"] = new JObject { ["supported"] = false,
                    ["reason"] = "horizun_undo records no inverse for column_attach/column_detach; run the opposite operation or Revit's own undo." }
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, input.Count, input.Count, input.Count, 0, 0, 0);
            return CommandResult.Ok(applied);
        }

        private static JObject RowJson(ColumnAttachRow r, double scale, string units) => new JObject
        {
            ["operation"] = r.Attach ? ColumnAttachRules.Attach : ColumnAttachRules.Detach, ["column_id"] = r.ColumnId,
            ["column_end"] = r.End == 0 ? "base" : "top", ["target_id"] = r.Attach ? (JToken)r.TargetId : null,
            ["already_attached"] = r.Attach ? (JToken)r.AlreadyAttached : null,
            ["cut_style"] = r.Attach ? "cut_column" : null, ["justification"] = r.Attach ? "minimum" : null,
            ["extent_z_before"] = ZJson(r.ExtentBefore, scale), ["extent_z_after"] = ZJson(r.ExtentRehearsed, scale), ["units"] = units
        };

        private static void ApplyColumnAttach(Document doc, List<ColumnAttachRow> rows)
        {
            foreach (ColumnAttachRow r in rows)
            {
                var column = (FamilyInstance)doc.GetElement(Rid.Make(r.ColumnId));
                if (r.Attach)
                {
                    if (r.AlreadyAttached) continue;
                    ColumnAttachment.AddColumnAttachment(doc, column, doc.GetElement(Rid.Make(r.TargetId)), r.End,
                        ColumnAttachmentCutStyle.CutColumn, ColumnAttachmentJustification.Minimum, 0.0);
                }
                else ColumnAttachment.RemoveColumnAttachment(column, r.End);
            }
        }

        /// <summary>Null when every attachment reads as asked; otherwise the first disagreement.</summary>
        private static string ReadBackColumnAttach(Document doc, List<ColumnAttachRow> rows)
        {
            foreach (ColumnAttachRow r in rows)
            {
                var column = doc.GetElement(Rid.Make(r.ColumnId)) as FamilyInstance;
                if (column == null) return "column " + r.ColumnId + " is gone.";
                ColumnAttachment now = ColumnAttachment.GetColumnAttachment(column, r.End);
                string end = r.End == 0 ? "base" : "top";
                if (!r.Attach)
                {
                    if (now != null) return "the " + end + " of column " + r.ColumnId + " is still attached to " + Rid.Value(now.TargetId) + ".";
                    continue;
                }
                if (now == null) return "the " + end + " of column " + r.ColumnId + " reads as unattached.";
                if (Rid.Value(now.TargetId) != r.TargetId) return "the " + end + " of column " + r.ColumnId + " is attached to " + Rid.Value(now.TargetId) + ", not " + r.TargetId + ".";
                if (now.BaseOrTop != r.End) return "the attachment of column " + r.ColumnId + " sits at the other end.";
                if (!r.AlreadyAttached && (now.CutStyle != ColumnAttachmentCutStyle.CutColumn || now.Justification != ColumnAttachmentJustification.Minimum))
                    return "the attachment of column " + r.ColumnId + " reads cut style " + now.CutStyle + " / justification " + now.Justification + ", not the requested ones.";
            }
            return null;
        }

        private static JObject RehearseColumnAttach(Document doc, List<ColumnAttachRow> rows)
        {
            RevitErrorRecorder log = null;
            Guard.RollbackResult? rb = null; string rbError = null, error = null;
            using (var tx = new Transaction(doc, "Horizun: rehearse column attachments"))
            {
                try
                {
                    log = RevitErrorRecorder.On(tx, captureWarnings: true);
                    tx.Start();
                    ApplyColumnAttach(doc, rows);
                    doc.Regenerate();
                    foreach (ColumnAttachRow r in rows) r.ExtentRehearsed = ZExtent(doc.GetElement(Rid.Make(r.ColumnId)));
                    error = ReadBackColumnAttach(doc, rows);
                }
                catch (Exception ex) { error = ex.Message; }
                try { rb = Guard.RollBack(tx); } catch (Exception ex) { rbError = ex.Message; }
            }
            return new JObject
            {
                ["error"] = error, ["warnings"] = new JArray(log?.Warnings ?? new List<string>()), ["errors_raised"] = new JArray(log?.Errors ?? new List<string>()),
                ["rollback_status"] = rb.HasValue ? rb.Value.StatusName : "exception: " + rbError,
                ["rollback_confirmed"] = rb.HasValue && rb.Value.Confirmed
            };
        }
    }
}
