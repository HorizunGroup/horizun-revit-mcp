// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_coordination operation=show - a persistent 3D view over ledger findings
// (navisworks-origin or not), so a coordinator can SEE what horizun_resolve_clash
// is about to move without leaving Revit: section box around the selected
// findings, the side that must move in red, the immovable side in orange. Unlike
// every other CoordinationCommand operation this one DOES write the model (a real,
// kept view + graphic overrides), so it is the one operation here that opens
// DocumentGate.ForMutation and is verified with a PostconditionCheck like any
// other typed write - the ledger itself stays bridge state either way.
//
// LINKED ELEMENTS: Revit's per-element view overrides apply to host elements. A
// finding whose side lives in a link is painted at the LINK INSTANCE level
// instead (the whole link tinted that finding's colour) and the reply says so
// explicitly, in `link_level_overrides` - never silently applied per-element
// where the API cannot do that.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CoordinationCommand
    {
        private static readonly Color ColorMustMove = new Color(220, 30, 30);      // red
        private static readonly Color ColorImmovable = new Color(240, 150, 20);    // orange
        private static readonly Color ColorUnknownSide = new Color(40, 110, 220);  // blue: no side preference known

        private static CommandResult Show(UIApplication app, JObject request)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, "horizun_coordination");
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            if (request.Value<bool?>("per_issue") == true)
                return CommandResult.Fail("per_issue=true is not yet supported by this build: pass finding_ids to " +
                    "scope one call to a single issue's findings instead, or call this once per finding_id. " +
                    "Nothing was changed.");

            string ledgerPath = CoordinationLedger.PathFor(doc.Title, UndoCapture.SafePath(doc));
            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out _);

            List<string> requestedIds = (request["finding_ids"] as JArray ?? new JArray()).Select(t => (string)t).ToList();
            string statusFilter = request.Value<string>("status");
            List<CoordinationFinding> selected;
            if (requestedIds.Count > 0)
            {
                var missing = requestedIds.Where(id => !ledger.ContainsKey(id)).ToList();
                if (missing.Count > 0)
                    return CommandResult.Fail("finding_ids names " + missing.Count + " id(s) not in this document's " +
                        "ledger: " + string.Join(", ", missing.Take(5)) + ". Nothing was changed.");
                selected = requestedIds.Select(id => ledger[id]).ToList();
            }
            else
            {
                selected = ledger.Values
                    .Where(f => statusFilter == null || string.Equals(f.Status, statusFilter, StringComparison.Ordinal))
                    .Where(f => f.Status != CoordinationRules.StatusResolvedByModel && f.Status != CoordinationRules.StatusClosedByDecision)
                    .ToList();
            }
            if (selected.Count == 0)
                return CommandResult.Fail("No finding matches the selection (finding_ids/status). operation=list " +
                    "shows what is available. Nothing was changed.");
            if (selected.Count > 300)
                return CommandResult.Fail(selected.Count + " findings match; pass finding_ids or a narrower status " +
                    "to keep this to 300 or fewer. Nothing was changed.");

            string viewName = request.Value<string>("view_name");
            if (string.IsNullOrWhiteSpace(viewName))
                viewName = "Horizun - Navisworks " + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            bool selectAlso = request.Value<bool?>("select") == true;

            // ---- resolve every side to something paintable: a host element, or (a link
            //      we can still identify) the whole RevitLinkInstance. --------------------
            var linksByLabel = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .ToDictionary(li => SafeLinkName(li), li => li, StringComparer.Ordinal);

            var paint = new Dictionary<long, Color>();           // host element id -> colour
            var linkPaint = new Dictionary<long, Color>();       // RevitLinkInstance id -> colour
            var framedBoxes = new List<BoundingBoxXYZ>();
            var perFindingRows = new JArray();
            var unresolvedSides = new JArray();

            foreach (CoordinationFinding f in selected)
            {
                bool? immovableIsA = f.ImmovableSideIsA;
                PaintSide(doc, linksByLabel, f.SideA, immovableIsA == true ? ColorImmovable : immovableIsA == false ? ColorMustMove : ColorUnknownSide,
                          paint, linkPaint, framedBoxes, unresolvedSides);
                PaintSide(doc, linksByLabel, f.SideB, immovableIsA == true ? ColorMustMove : immovableIsA == false ? ColorImmovable : ColorUnknownSide,
                          paint, linkPaint, framedBoxes, unresolvedSides);
                perFindingRows.Add(new JObject
                {
                    ["finding_id"] = f.Id,
                    ["external_source"] = f.ExternalSource,
                    ["immovable_side_known"] = immovableIsA.HasValue
                });
            }
            if (framedBoxes.Count == 0)
                return CommandResult.Fail("None of the selected findings' elements could be resolved to geometry in " +
                    "this session (host or a loaded link). Nothing was changed. Unresolved: " +
                    string.Join("; ", unresolvedSides.Select(u => (string)u["reason"]).Take(5)));

            BoundingBoxXYZ box = FrameBoxes(framedBoxes);
            string planHash = DocumentGate.PlanHash(request, "finding_ids", "status", "view_name");
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            if (dryRun)
            {
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["view_name"] = viewName,
                    ["findings_selected"] = selected.Count,
                    ["elements_painted"] = paint.Count,
                    ["links_painted_whole"] = linkPaint.Count,
                    ["unresolved_sides"] = unresolvedSides,
                    ["legend"] = "red = side that must move, orange = immovable side, blue = no side preference known " +
                                 "for that finding",
                    ["note"] = "Nothing was created. A view named exactly '" + viewName + "' must not already exist."
                };
                bool nameFree = new FilteredElementCollector(doc).OfClass(typeof(View3D))
                    .Cast<View3D>().All(v => !string.Equals(SafeViewName(v), viewName, StringComparison.Ordinal));
                if (!nameFree)
                {
                    preview["name_available"] = false;
                    DocumentGate.StampConfirmation(preview, gate, "horizun_coordination", planHash, true,
                        "a view named '" + viewName + "' already exists - pass a different view_name before applying.");
                    return CommandResult.Ok(preview);
                }
                preview["name_available"] = true;
                DocumentGate.StampConfirmation(preview, gate, "horizun_coordination", planHash, true, null);
                return CommandResult.Ok(preview);
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, "horizun_coordination", planHash);
            if (refusal != null) return refusal;

            View3D created = null;
            string txName = "Horizun: navisworks coordination view";
            var checklist = new PostconditionCheck("view_created", "section_box_active", "overrides_sample");
            using (var tx = new Transaction(doc, txName))
            {
                RevitErrorRecorder said = RevitErrorRecorder.On(tx);
                tx.Start();
                try
                {
                    if (new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                        .Any(v => string.Equals(SafeViewName(v), viewName, StringComparison.Ordinal)))
                    {
                        tx.RollBack();
                        return CommandResult.Fail("A view named '" + viewName + "' already exists. Nothing was " +
                            "changed - pass a different view_name.");
                    }
                    ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                        .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                        ?? throw new InvalidOperationException("the model has no 3D view family type");
                    created = View3D.CreateIsometric(doc, vft.Id);
                    created.Name = viewName;
                    try { created.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                    created.SetSectionBox(box);
                    created.IsSectionBoxActive = true;
                    ElementId solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                        .FirstOrDefault(p => { try { return p.GetFillPattern().IsSolidFill; } catch { return false; } })?.Id;
                    foreach (var kv in paint)
                        try { created.SetElementOverrides(Rid.Make(kv.Key), Paint(kv.Value, solid)); } catch { }
                    foreach (var kv in linkPaint)
                        try { created.SetElementOverrides(Rid.Make(kv.Key), Paint(kv.Value, solid)); } catch { }
                    Guard.Commit(tx, txName);
                }
                catch (Exception ex)
                {
                    string rb = tx.GetStatus() == TransactionStatus.Started ? Guard.RollBack(tx).StatusName : PlanFailure.NotAttempted;
                    return CommandResult.Fail("show failed: " + ex.Message + said.Said() + " " +
                        PlanFailure.SingleTransactionOutcome(tx.GetStatus() == TransactionStatus.Started, rb, "nothing was created"));
                }
            }

            // ---- re-read from the committed model ----------------------------------
            View3D reread = doc.GetElement(created.Id) as View3D;
            bool viewOk = reread != null && string.Equals(reread.Name, viewName, StringComparison.Ordinal);
            checklist.Compare("view_created", true, viewOk);
            bool boxOk = false;
            try
            {
                BoundingBoxXYZ got = reread?.GetSectionBox();
                boxOk = reread != null && reread.IsSectionBoxActive && got != null &&
                        Close(got.Min, box.Min) && Close(got.Max, box.Max);
                checklist.Record("section_box_active", true, reread?.IsSectionBoxActive == true && boxOk, boxOk);
            }
            catch (Exception ex) { checklist.Unreadable("section_box_active", true, ex.Message); }

            int sampleOk = 0, sampleTotal = 0;
            try
            {
                foreach (var kv in paint.Take(20).Concat(linkPaint.Take(10)))
                {
                    sampleTotal++;
                    OverrideGraphicSettings got = reread.GetElementOverrides(Rid.Make(kv.Key));
                    Color c = got?.ProjectionLineColor;
                    if (c != null && c.IsValid && c.Red == kv.Value.Red && c.Green == kv.Value.Green && c.Blue == kv.Value.Blue)
                        sampleOk++;
                }
                checklist.Record("overrides_sample", sampleTotal, sampleOk, sampleTotal > 0 && sampleOk == sampleTotal);
            }
            catch (Exception ex) { checklist.Unreadable("overrides_sample", "all_sampled_match", ex.Message); }

            if (selectAlso)
                try { app.ActiveUIDocument.Selection.SetElementIds(paint.Keys.Select(Rid.Make).ToList()); } catch { }

            var applied = new JObject
            {
                ["dry_run"] = false,
                ["view_id"] = Rid.Value(created.Id),
                ["view_name"] = viewName,
                ["findings_selected"] = selected.Count,
                ["elements_painted"] = paint.Count,
                ["links_painted_whole"] = linkPaint.Count,
                ["link_level_overrides"] = linkPaint.Count > 0,
                ["unresolved_sides"] = unresolvedSides,
                ["postconditions"] = checklist.ToJson()
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, 1, viewOk ? 1 : 0, viewOk ? 1 : 0, 0, viewOk ? 0 : 1, 0);
            return CommandResult.Ok(applied);
        }

        private static bool Close(XYZ a, XYZ b) => a.DistanceTo(b) < 0.01;   // ~3 mm, section box round-trip tolerance

        private static void PaintSide(Document doc, Dictionary<string, RevitLinkInstance> linksByLabel, string sideKey,
                                      Color color, Dictionary<long, Color> paint, Dictionary<long, Color> linkPaint,
                                      List<BoundingBoxXYZ> framed, JArray unresolved)
        {
            if (!ClashResolveRules.ParseSide(sideKey, out bool host, out string uid) || string.IsNullOrEmpty(uid))
            { unresolved.Add(new JObject { ["side"] = sideKey, ["reason"] = "side identity could not be parsed" }); return; }

            if (host)
            {
                Element e = doc.GetElement(uid);
                if (e == null) { unresolved.Add(new JObject { ["side"] = sideKey, ["reason"] = "element no longer exists in the host" }); return; }
                paint[Rid.Value(e.Id)] = color;
                try { BoundingBoxXYZ bb = e.get_BoundingBox(null); if (bb != null) framed.Add(bb); } catch { }
                return;
            }

            // Not a host side: the label before the first '|' is the link's display name,
            // as ClashCommand and this command both use it (SideKey(source, instance, uid)).
            string label = sideKey.Split('|')[0];
            if (linksByLabel.TryGetValue(label, out RevitLinkInstance li))
            {
                linkPaint[Rid.Value(li.Id)] = color;
                try { BoundingBoxXYZ bb = li.get_BoundingBox(null); if (bb != null) framed.Add(bb); } catch { }
                return;
            }
            unresolved.Add(new JObject { ["side"] = sideKey, ["reason"] = "link '" + label + "' is not currently loaded - painted at no level" });
        }

        private static BoundingBoxXYZ FrameBoxes(List<BoundingBoxXYZ> boxes)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (BoundingBoxXYZ b in boxes)
            {
                minX = Math.Min(minX, b.Min.X); minY = Math.Min(minY, b.Min.Y); minZ = Math.Min(minZ, b.Min.Z);
                maxX = Math.Max(maxX, b.Max.X); maxY = Math.Max(maxY, b.Max.Y); maxZ = Math.Max(maxZ, b.Max.Z);
            }
            double pad = Math.Max(1.0, 0.15 * Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)));
            return new BoundingBoxXYZ { Min = new XYZ(minX - pad, minY - pad, minZ - pad), Max = new XYZ(maxX + pad, maxY + pad, maxZ + pad) };
        }

        private static OverrideGraphicSettings Paint(Color c, ElementId solid)
        {
            var g = new OverrideGraphicSettings();
            g.SetProjectionLineColor(c); g.SetCutLineColor(c);
            if (solid != null)
            {
                g.SetSurfaceForegroundPatternId(solid); g.SetSurfaceForegroundPatternColor(c);
                g.SetCutForegroundPatternId(solid); g.SetCutForegroundPatternColor(c);
            }
            return g;
        }

        private static string SafeViewName(View3D v) { try { return v?.Name; } catch { return null; } }
    }
}
