// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_model_diff operation=colorize - THE ONLY WRITE in this tool. It never
// touches the caller's view: it DUPLICATES view_id (no detailing, template
// removed so element overrides show), names the copy after the comparison, and
// overrides added elements green and modified ones orange. Deleted elements do
// not exist in the active model and cannot be coloured; the reply counts them.
//
// dry_run (default) resolves the comparison and returns a confirmation_token
// bound to the document, the request AND the exact set of elements to colour.
// The apply re-reads every override inside the transaction and rolls the whole
// thing back if one does not hold, then re-reads again after the commit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ModelDiffCommand
    {
        private static readonly Color AddedColor = new Color(0, 170, 0);
        private static readonly Color ModifiedColor = new Color(255, 140, 0);

        private sealed class Target { public ElementId Id; public string UniqueId; public string State; }

        private CommandResult Colorize(UIApplication app, JObject request)
        {
            GateResult gate = MutationGate(app, request);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string beforeId = request.Value<string>("before");
            long? viewId = request.Value<long?>("view_id");
            if (string.IsNullOrWhiteSpace(beforeId) || viewId == null)
                return CommandResult.Fail("colorize needs 'before' (a snapshot id) and 'view_id' (the view to duplicate). " +
                                          "The active model is always the 'after'. Nothing was changed.");
            View source = doc.GetElement(Rid.Make(viewId.Value)) as View;
            if (source == null || source.IsTemplate || !source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                return CommandResult.Fail("view_id " + viewId + " is not a view that can be duplicated. Nothing was changed.");

            DiffSnapshot before, after;
            CommandResult failure = ResolvePair(app, request, beforeId, Active, out before, out after);
            if (failure != null) return failure;
            DiffResult diff = ModelDiffRules.Compare(before, after, Options(request));

            var targets = new List<Target>();
            foreach (DiffRow row in diff.Rows)
            {
                if (row.State == ModelDiffRules.Deleted) continue;
                Element e = doc.GetElement(row.UniqueId);
                if (e != null) targets.Add(new Target { Id = e.Id, UniqueId = row.UniqueId, State = row.State });
            }
            int deleted = diff.Count(ModelDiffRules.Deleted);

            var scopeRequest = new JObject
            {
                ["before"] = beforeId, ["view_id"] = viewId,
                ["targets"] = string.Join(";", targets.Select(t => t.State[0] + t.UniqueId).OrderBy(x => x, StringComparer.Ordinal))
            };
            string planHash = DocumentGate.PlanHash(scopeRequest, "before", "view_id", "targets");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            if (dryRun)
            {
                var rehearsal = new JObject
                {
                    ["operation"] = "colorize", ["dry_run"] = true, ["transaction_status"] = "not_started",
                    ["source_view"] = Safe(() => source.Name),
                    ["to_color"] = new JObject { ["added"] = targets.Count(t => t.State == ModelDiffRules.Added),
                                                 ["modified"] = targets.Count(t => t.State == ModelDiffRules.Modified) },
                    ["deleted_not_colorable"] = deleted,
                    ["colors"] = new JObject { ["added"] = "0,170,0", ["modified"] = "255,140,0" },
                    ["note"] = "Nothing was changed. The apply duplicates the view and overrides elements in the COPY only."
                };
                ApplicationOutcome.StampRehearsal(rehearsal, targets.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(rehearsal, gate, ToolName, planHash, true,
                    "the token is bound to the exact set of elements the comparison resolved; if the model changes " +
                    "that set before you spend it, the apply is refused.");
                return CommandResult.Ok(rehearsal);
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, ToolName, planHash);
            if (refusal != null) return refusal;

            FillPatternElement solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>().FirstOrDefault(f => SafeSolid(f));
            ElementId newViewId = null;
            var inView = new List<Target>();
            int notInView = 0;
            string txName = "Horizun: colorize model diff";
            string viewName = UniqueViewName(doc, "Horizun diff " + (before.Id ?? "snapshot"));
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try
                {
                    newViewId = source.Duplicate(ViewDuplicateOption.Duplicate);
                    View copy = (View)doc.GetElement(newViewId);
                    if (copy.ViewTemplateId != ElementId.InvalidElementId) copy.ViewTemplateId = ElementId.InvalidElementId;
                    copy.Name = viewName;
                    doc.Regenerate();
                    var visible = new HashSet<long>(new FilteredElementCollector(doc, newViewId).ToElementIds().Select(Rid.Value));
                    foreach (Target t in targets)
                    {
                        if (!visible.Contains(Rid.Value(t.Id))) { notInView++; continue; }
                        copy.SetElementOverrides(t.Id, Overrides(t.State, solid));
                        inView.Add(t);
                    }
                    doc.Regenerate();
                    int held = inView.Count(t => Holds(copy, t));
                    if (held != inView.Count)
                        throw new InvalidOperationException(held + " of " + inView.Count + " overrides read back as requested");
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    string rb = tx.GetStatus() == TransactionStatus.Started ? Guard.RollBack(tx).StatusName : PlanFailure.NotAttempted;
                    return CommandResult.Fail("colorize failed and was rolled back (" + rb + "): " + ex.Message +
                                              ". No view was created and no override was kept.");
                }
            }

            // After the commit: the copy exists, carries no template, and every override is there.
            View after_ = doc.GetElement(newViewId) as View;
            int verified = after_ == null ? 0 : inView.Count(t => Holds(after_, t));
            bool viewOk = after_ != null && after_.ViewTemplateId == ElementId.InvalidElementId && after_.Name == viewName;
            var payload = new JObject
            {
                ["operation"] = "colorize", ["transaction_status"] = "Committed", ["transaction_name"] = txName,
                ["view_id"] = newViewId == null ? JValue.CreateNull() : (JToken)Rid.Value(newViewId),
                ["view_name"] = viewName, ["view_verified"] = viewOk,
                ["overrides_requested"] = targets.Count, ["overrides_applied"] = inView.Count,
                ["overrides_verified"] = verified, ["not_in_view"] = notInView,
                ["deleted_not_colorable"] = deleted,
                ["solid_fill_found"] = solid != null,
                ["colors"] = new JObject { ["added"] = "0,170,0", ["modified"] = "255,140,0" }
            };
            ApplicationOutcome.StampApplied(payload, ApplicationOutcome.Committed, targets.Count, inView.Count,
                                            viewOk ? verified : 0, notInView, 0, inView.Count - verified);
            return CommandResult.Ok(payload);
        }

        private static OverrideGraphicSettings Overrides(string state, FillPatternElement solid)
        {
            Color c = state == ModelDiffRules.Added ? AddedColor : ModifiedColor;
            var o = new OverrideGraphicSettings().SetProjectionLineColor(c);
            if (solid != null) o = o.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(c);
            return o;
        }

        private static bool Holds(View view, Target t)
        {
            try
            {
                Color want = t.State == ModelDiffRules.Added ? AddedColor : ModifiedColor;
                Color got = view.GetElementOverrides(t.Id).ProjectionLineColor;
                return got != null && got.IsValid && got.Red == want.Red && got.Green == want.Green && got.Blue == want.Blue;
            }
            catch { return false; }
        }

        private static bool SafeSolid(FillPatternElement f)
        {
            try { return f.GetFillPattern().IsSolidFill; } catch { return false; }
        }

        private static string UniqueViewName(Document doc, string wanted)
        {
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Select(v => Safe(() => v.Name)).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            string name = wanted;
            for (int i = 2; names.Contains(name); i++) name = wanted + " (" + i + ")";
            return name;
        }
    }
}
