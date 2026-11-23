// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of PythonUndoRules: after a real execute_python run, journal what
// it created so horizun_undo can reverse it. Never throws, never changes the
// script's answer except to add `undo` beside it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class PythonUndoRecord
    {
        internal static void AfterCommand(string tool, JObject request, ChangeWatch changes, CommandResult result)
        {
            if (result == null || changes == null || !PythonUndoRules.Applies(tool, request)) return;
            try
            {
                changes.Settle();
                List<ChangeWatch.DocChanges> docs = changes.Documents
                    .Where(d => d.Document != null && d.Document.IsValidObject && !d.Document.IsLinked &&
                                (d.Added.Count > 0 || d.Modified.Count > 0 || d.Deleted.Count > 0)).ToList();
                if (docs.Count == 0)
                {
                    if (!changes.ObservationComplete)
                        result.AttachDetail("undo", new JObject { ["undoable"] = false, ["reason"] = "Change observation was incomplete; undo coverage cannot be established." });
                    return;
                }
                ChangeWatch.DocChanges main = docs.OrderByDescending(d => d.Added.Count + d.Modified.Count + d.Deleted.Count).First();
                PythonUndoPlan plan = PythonUndoRules.Decide(
                    main.Added, main.Modified.Count, main.Deleted.Count, docs.Count,
                    changes.ObservationComplete && main.Added.All(Rid.CanRepresent));
                if (plan == null) return;

                var entries = new List<UndoEntry>();
                if (plan.Created.Count > 0 && plan.Created.Count <= PythonUndoRules.MaxCreated && plan.Created.All(Rid.CanRepresent))
                    entries.Add(UndoCapture.Entry(main.Document, "created", plan.Created, new JObject(), new JObject()));
                JObject undo = UndoCapture.Record(main.Document, tool, entries, plan.Reason);
                undo["covers"] = "created elements only";
                undo["created"] = plan.Created.Count;
                undo["modified_not_reversible"] = plan.Modified;
                undo["deleted_not_reversible"] = plan.Deleted;

                if (result.Success)
                {
                    JObject data = result.Data as JObject ?? (result.Data == null ? new JObject() : JObject.FromObject(result.Data));
                    data["undo"] = undo;
                    result.ReplaceData(data);
                }
                else
                {
                    result.AttachDetail("undo", undo);
                }
            }
            catch (Exception ex) { Log.Warn("python undo journal skipped: " + ex.Message); }
        }
    }
}
