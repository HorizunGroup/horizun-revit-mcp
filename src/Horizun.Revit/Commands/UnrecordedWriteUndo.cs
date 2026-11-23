// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// 5.0.4 (course L26): horizun_undo reverses the last batch a tool RECORDED, but only
// some typed writes record one (create, write_params, transform, resolve_clash...).
// A write that recorded nothing - manage_system_types duplicating a type, say - left
// the journal untouched, so a later undo_last reached PAST it and reversed an older
// batch as if it were the last thing done. Now any committed write that changed the
// model without recording leaves a NOT-undoable batch: undo_last refuses and names it,
// and the write's own reply says, in `undo`, that horizun_undo cannot reverse it.
// Never throws, never changes the answer except to add `undo`.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class UnrecordedWriteUndo
    {
        internal static void AfterCommand(string tool, CommandContract contract, JObject request, ChangeWatch changes,
                                          CommandResult result, int undoRecordsBefore)
        {
            if (result == null || changes == null || contract == null) return;
            try
            {
                if (UndoRules.UnrecordedWriteExempt(tool) || PythonUndoRules.Applies(tool, request) ||
                    PythonUndoRules.RolledBackByDesign(tool, request)) return;
                if (UndoCapture.RecordCount != undoRecordsBefore) return;   // the tool (or a child) recorded its own batch
                bool mutating = contract.Effect == ToolEffect.Mutating || contract.Effect == ToolEffect.MutatingUnlessDryRun;
                if (!mutating) return;
                JObject data = result.Data as JObject;
                if (result.Success ? data?.Value<bool?>("dry_run") == true : request?.Value<bool?>("dry_run") != false) return;
                if (data?["undo"] != null) return;
                changes.Settle();
                List<ChangeWatch.DocChanges> docs = changes.Documents
                    .Where(d => d.Document != null && d.Document.IsValidObject && !d.Document.IsLinked && !d.Document.IsFamilyDocument &&
                                (d.Added.Count > 0 || d.Modified.Count > 0 || d.Deleted.Count > 0)).ToList();
                if (docs.Count == 0) return;
                ChangeWatch.DocChanges main = docs.OrderByDescending(d => d.Added.Count + d.Modified.Count + d.Deleted.Count).First();
                JObject undo = UndoCapture.Record(main.Document, tool, null, UndoRules.UnrecordedWriteReason(tool));
                undo["undoable"] = false;
                undo["note"] = UndoRules.UnrecordedWriteNote;
                undo["changed"] = new JObject { ["added"] = main.Added.Count, ["modified"] = main.Modified.Count, ["deleted"] = main.Deleted.Count };
                if (result.Success)
                {
                    JObject d2 = data ?? (result.Data == null ? new JObject() : JObject.FromObject(result.Data));
                    d2["undo"] = undo;
                    result.ReplaceData(d2);
                }
                else result.AttachDetail("undo", undo);
            }
            catch (Exception ex) { Log.Warn("unrecorded-write undo marker skipped: " + ex.Message); }
        }
    }
}
