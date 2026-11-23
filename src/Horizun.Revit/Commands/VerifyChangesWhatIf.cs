// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_verify_changes operation=what_if - rehearse a change and measure it, with the
// rollback guaranteed and proven. It lives on verify_changes because that tool already
// owns "build something temporary, always roll it back" (its pictures) and is classified
// as writing: it is document-gated (DocumentGate.ForMutation) like every write and
// refused on workshared models. The measures are horizun_measure_geometry's own
// (MeasureGeometryCommand.MeasureAll), so a reading here equals a reading there.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class VerifyChangesCommand
    {
        // ---- what_if: apply, measure, ALWAYS roll back, prove the rollback ----
        //
        // The field's self-undoing Python rehearsals, as one typed operation. The change runs in
        // a Transaction inside a TransactionGroup; whatever happens - the action throws, Revit
        // rejects it, the measure fails - the GROUP is rolled back in a finally, so nothing is
        // ever kept and nothing reaches the undo stack. The rollback is then PROVEN, not
        // assumed: the elements are measured a third time and compared with the first reading,
        // and every id the action deleted (dependents included) must exist again.
        /// <summary>Called by Execute after DocumentGate.ForMutation resolved <paramref name="doc"/>.</summary>
        private CommandResult RunWhatIf(Document doc, JObject request)
        {
            if (!GeometryWhatIfRules.TryParseRequest(request, out MeasureRequest req, out WhatIfSpec w, out string parseError))
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, parseError + " Nothing was changed.");
            List<long> ids = req.Ids;
            bool truncated = false;
            if (doc.IsWorkshared)
                return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida, "what_if refuses a workshared model: even a change that is rolled back borrows " +
                    "elements from central. Rehearse on a detached copy. Nothing was changed.");
            if (doc.IsReadOnly) return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida, "what_if needs a document Revit lets this session change; this one is read-only. Nothing was changed.");
            if (doc.IsModifiable) return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida, "A transaction is already open in this document, so a rehearsal could not be rolled back on its own. Nothing was changed.");
            var targets = new List<ElementId>();
            foreach (long id in w.Ids)
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "what_if.ids: element " + id + " is not in '" + doc.Title + "'. Nothing was changed.");
                if (e is ElementType) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "what_if.ids: element " + id + " is a type; the rehearsal acts on placed elements. Nothing was changed.");
                targets.Add(e.Id);
            }

            var beforeUnread = new JArray();
            JArray before = MeasureGeometryCommand.MeasureAll(doc, ids, req, beforeUnread);
            JArray after = null;
            var afterUnread = new JArray();
            var deleted = new List<ElementId>();
            string applyError = null;
            string commitStatus = null;
            var failures = new JArray();
            bool groupRolledBack = false;
            string groupStatus = null;

            using (var group = new TransactionGroup(doc, "Horizun: what_if (always rolled back)"))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.FailCoded(ErrorCodes.ApiRevitExcepcion, "The rehearsal could not open its transaction group. Nothing was changed.");
                try
                {
                    using (var t = new Transaction(doc, "Horizun: what_if " + w.Action))
                    {
                        var capture = new WhatIfFailures();
                        FailureHandlingOptions options = t.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(capture);
                        options.SetClearAfterRollback(true);
                        t.SetFailureHandlingOptions(options);
                        if (t.Start() != TransactionStatus.Started) applyError = "the rehearsal transaction did not start";
                        else
                        {
                            try { Apply(doc, w, targets, deleted); }
                            catch (Exception ex) { applyError = ex.GetType().Name + ": " + ex.Message; }
                            if (applyError == null)
                            {
                                try { doc.Regenerate(); } catch (Exception ex) { applyError = "Revit could not regenerate after the action: " + ex.Message; }
                            }
                            if (applyError == null)
                            {
                                TransactionStatus s = t.Commit();
                                commitStatus = s.ToString();
                                if (s != TransactionStatus.Committed) applyError = "Revit rejected the action (" + s + ")";
                            }
                            else if (t.GetStatus() == TransactionStatus.Started) Guard.RollBack(t);
                        }
                        foreach (JObject f in capture.Facts) failures.Add(f);
                    }
                    if (applyError == null) after = MeasureGeometryCommand.MeasureAll(doc, ids, req, afterUnread);
                }
                catch (Exception ex)
                {
                    if (applyError == null) applyError = ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    try
                    {
                        if (group.GetStatus() == TransactionStatus.Started)
                        {
                            Guard.RollbackResult rb = Guard.RollBack(group);
                            groupRolledBack = rb.Confirmed;
                            groupStatus = rb.StatusName;
                        }
                    }
                    catch (Exception ex) { groupStatus = "rollback threw " + ex.GetType().Name; }
                }
            }

            // THE PROOF. A third reading must equal the first, and every deleted id is back.
            var restoredUnread = new JArray();
            JArray restored = MeasureGeometryCommand.MeasureAll(doc, ids, req, restoredUnread);
            double tolerance = 0.01 / 304.8 * req.LengthFromFeet;   // 0.01 mm in the request unit
            List<string> notRestored = GeometryWhatIfRules.Differences(before, restored, Math.Max(tolerance, 1e-6));
            var missingAgain = deleted.Where(id => doc.GetElement(id) == null).Select(Rid.Value).ToList();
            bool verified = groupRolledBack && !doc.IsModifiable && notRestored.Count == 0 && missingAgain.Count == 0 &&
                            beforeUnread.Count == restoredUnread.Count;

            var changes = new JArray();
            if (after != null)
                foreach (JObject b in before.OfType<JObject>())
                {
                    long id = (long)b["element_id"];
                    JObject a = after.OfType<JObject>().FirstOrDefault(x => (long)x["element_id"] == id);
                    if (a == null) { changes.Add(new JObject { ["element_id"] = id, ["after"] = "deleted" }); continue; }
                    List<string> d = GeometryWhatIfRules.Differences(b, a, Math.Max(tolerance, 1e-6), 10);
                    if (d.Count > 0) changes.Add(new JObject { ["element_id"] = id, ["changed"] = new JArray(d) });
                }

            var deletedRows = new JObject();
            if (deleted.Count > 0)
            {
                var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (ElementId id in deleted)
                {
                    string cat = "(no category)";
                    try { cat = doc.GetElement(id)?.Category?.Name ?? cat; } catch { }
                    byCategory[cat] = byCategory.TryGetValue(cat, out int n) ? n + 1 : 1;
                }
                deletedRows["count"] = deleted.Count;
                deletedRows["dependents"] = deleted.Count(id => !targets.Contains(id));
                deletedRows["by_category"] = JObject.FromObject(byCategory);
                deletedRows["ids"] = new JArray(deleted.Take(500).Select(Rid.Value));
            }

            var result = new JObject
            {
                ["target_document"] = doc.Title,
                ["what_if"] = new JObject { ["action"] = w.Action, ["ids"] = new JArray(w.Ids) },
                ["applied"] = applyError == null,
                ["apply_error"] = applyError,
                ["revit_messages"] = failures,
                ["unit"] = req.Unit, ["volume_unit"] = "m3", ["area_unit"] = "m2",
                ["measures"] = new JArray(req.Measures),
                ["before"] = before,
                ["after"] = after,
                ["changes"] = changes,
                ["deleted"] = deleted.Count > 0 ? deletedRows : null,
                ["unreadable"] = new JArray(beforeUnread.Concat(afterUnread).Take(200)),
                ["rolled_back"] = groupRolledBack,
                ["rollback_status"] = groupStatus,
                ["rollback_proof"] = new JObject
                {
                    ["remeasured_equal_to_before"] = notRestored.Count == 0,
                    ["differences"] = new JArray(notRestored),
                    ["deleted_ids_back"] = missingAgain.Count == 0,
                    ["deleted_ids_still_missing"] = new JArray(missingAgain.Take(50)),
                    ["document_modifiable_after"] = doc.IsModifiable
                },
                ["rolled_back_verified"] = verified,
                ["changes_kept"] = false,
                ["truncated"] = truncated,
                ["note"] = "A rehearsal: the action ran inside a transaction group that was rolled back whatever happened. " +
                           "after is the model WITH the action; nothing of it was kept. rolled_back_verified is the proof - " +
                           "a third reading equal to before and every deleted id present again."
            };
            if (!verified) result[ErrorCodes.CodeField] = ErrorCodes.VerificacionFallida;
            if (!verified)
                return CommandResult.FailWithDetail("The rehearsal's rollback could NOT be proven (" +
                    (groupRolledBack ? "" : "group " + (groupStatus ?? "not rolled back") + "; ") +
                    (notRestored.Count > 0 ? "differences: " + string.Join("; ", notRestored.Take(5)) + "; " : "") +
                    (missingAgain.Count > 0 ? missingAgain.Count + " deleted id(s) still missing; " : "") +
                    "). Inspect the model before continuing.", result);
            return CommandResult.Ok(result);
        }

        private static void Apply(Document doc, WhatIfSpec w, List<ElementId> targets, List<ElementId> deleted)
        {
            switch (w.Action)
            {
                case WhatIfActions.Delete:
                    ICollection<ElementId> gone = doc.Delete(targets);
                    if (gone != null) deleted.AddRange(gone);
                    break;
                case WhatIfActions.Move:
                    ElementTransformUtils.MoveElements(doc, targets, new XYZ(w.VectorFeet[0], w.VectorFeet[1], w.VectorFeet[2]));
                    break;
                case WhatIfActions.Join:
                {
                    Element a = doc.GetElement(targets[0]), b = doc.GetElement(targets[1]);
                    if (JoinGeometryUtils.AreElementsJoined(doc, a, b))
                        throw new InvalidOperationException("the two elements are already joined; there is nothing to rehearse");
                    JoinGeometryUtils.JoinGeometry(doc, a, b);
                    break;
                }
                case WhatIfActions.Unjoin:
                {
                    Element a = doc.GetElement(targets[0]), b = doc.GetElement(targets[1]);
                    if (!JoinGeometryUtils.AreElementsJoined(doc, a, b))
                        throw new InvalidOperationException("the two elements are not joined; there is nothing to rehearse");
                    JoinGeometryUtils.UnjoinGeometry(doc, a, b);
                    break;
                }
                default:
                    throw new InvalidOperationException("unknown what_if action " + w.Action);
            }
        }

        /// <summary>
        /// Records every message the rehearsal raised. Warnings are deleted so no warning UI is
        /// left behind (the change is rolled back anyway); an error rolls the action back.
        /// </summary>
        private sealed class WhatIfFailures : IFailuresPreprocessor
        {
            public readonly List<JObject> Facts = new List<JObject>();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool error = false;
                try
                {
                    foreach (FailureMessageAccessor m in accessor.GetFailureMessages())
                    {
                        bool isError = m.GetSeverity() != FailureSeverity.Warning;
                        string text;
                        try { text = m.GetDescriptionText(); } catch { text = null; }
                        var ids = new JArray();
                        try { foreach (ElementId id in m.GetFailingElementIds()) ids.Add(Rid.Value(id)); } catch { }
                        Facts.Add(new JObject { ["severity"] = isError ? "error" : "warning", ["text"] = text, ["element_ids"] = ids });
                        if (isError) error = true;
                        else accessor.DeleteWarning(m);
                    }
                }
                catch { }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
    }
}
