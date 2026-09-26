// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_verify_changes - look at what was just modelled, the way an expert would.
//
// Every write re-reads its own postconditions; that proves the request was carried
// out, not that the result makes sense. Measured in field use: a session left a
// column and a door in the same place with every postcondition true. This command
// is the second look: the spatial coherence check (Core/SpatialCoherence.cs) on the
// elements the previous write changed - or on the ids you name - AND a picture of
// them, with the findings coloured, returned as an image so the caller actually
// sees the result instead of trusting counts.
//
// The picture comes from a temporary isometric 3D view (section box around the
// elements, subjects blue, errors red, warnings orange) created inside a transaction
// group that is always rolled back: the model is left exactly as it was.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class VerifyChangesCommand : ICommand
    {
        public string Name => "horizun_verify_changes";
        public string Description => "Spatial coherence check of the elements the last write changed (or the ids given), with an image of them and the findings highlighted.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            bool capture = request["capture"] == null || request.Value<bool>("capture");
            int pixel = request.Value<int?>("pixel_size") ?? 1400;
            if (pixel < 256 || pixel > 4096) return CommandResult.Fail("pixel_size must be between 256 and 4096.");
            int maxFindings = request.Value<int?>("max_findings") ?? 50;
            if (maxFindings < 1 || maxFindings > 500) return CommandResult.Fail("max_findings must be between 1 and 500.");
            int budgetS = request.Value<int?>("time_budget_seconds") ?? 60;
            if (budgetS < 5 || budgetS > 600) return CommandResult.Fail("time_budget_seconds must be between 5 and 600.");

            Document doc;
            GateResult gate = null;
            if (capture)
            {
                // The picture needs a temporary view, i.e. a transaction group, so the gate is
                // the mutation gate even though nothing survives the call.
                gate = DocumentGate.ForMutation(app, request, Name);
                if (!gate.Ok) return gate.Refusal;
                doc = gate.Document;
            }
            else
            {
                doc = app?.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("No active Revit document.");
                CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
                if (wrong != null) return wrong;
            }
            if (doc.IsFamilyDocument) return CommandResult.Fail("horizun_verify_changes checks project models; this is a family document.");

            // ---- scope ----
            var scope = new JObject();
            var ids = new List<ElementId>();
            if (request["element_ids"] != null)
            {
                if (!(request["element_ids"] is JArray arr) || arr.Count < 1 || arr.Count > 5000)
                    return CommandResult.Fail("element_ids must hold 1..5000 ids.");
                foreach (JToken t in arr)
                {
                    if (t.Type != JTokenType.Integer || !Rid.CanRepresent(t.Value<long>())) return CommandResult.Fail("element_ids must be integers.");
                    ids.Add(Rid.Make(t.Value<long>()));
                }
                scope["source"] = "element_ids";
            }
            else
            {
                ChangeLedger.Entry last = ChangeLedger.For(doc);
                if (last == null)
                    return CommandResult.Ok(new JObject
                    {
                        ["status"] = "nothing_to_check",
                        ["reason"] = "No Horizun write has changed this document since Revit started (the ledger lives in memory). Pass element_ids to check specific elements.",
                        ["read_only"] = true
                    });
                ids.AddRange(last.Added.Concat(last.Modified).Where(Rid.CanRepresent).Select(Rid.Make));
                scope["source"] = "last_write";
                scope["tool"] = last.Tool;
                scope["at_utc"] = last.AtUtc.ToString("o");
                scope["added"] = last.Added.Length; scope["modified"] = last.Modified.Length; scope["deleted"] = last.Deleted;
                scope["transactions"] = new JArray(last.Transactions);
            }

            List<Element> subjects = SpatialCoherence.Subjects(doc, ids);
            scope["model_elements"] = subjects.Count;
            SpatialCoherence.Outcome outcome = SpatialCoherence.Check(doc, subjects, 5000, budgetS * 1000);
            JObject check = SpatialCoherence.ToJson(outcome, maxFindings);
            var result = new JObject();
            string headline = SpatialCoherence.Headline(outcome);
            if (headline != null) result["attention"] = headline;
            result["status"] = check["status"];
            result["scope"] = scope;
            result["spatial_check"] = check;
            result["read_only"] = true;

            if (capture && subjects.Count > 0)
            {
                JObject picture = Picture(doc, subjects, outcome, pixel, request.Value<string>("orientation") ?? "isometric");
                foreach (JProperty p in picture.Properties()) result[p.Name] = p.Value;
                // The temporary view must be GONE. A rollback that did not confirm leaves a
                // view in somebody's model; that is a failure of this call, not a footnote.
                string rolled = picture["image"]?.Value<string>("temporary_view_rollback");
                if (rolled != null && rolled != TransactionStatus.RolledBack.ToString() && rolled != "not_attempted")
                    return CommandResult.FailWithDetail(
                        "The spatial check ran, but the temporary verification view was not rolled back (" + rolled +
                        "): a view may remain in the model. Inspect it and delete it with horizun_delete_verified.",
                        new JObject { ["code"] = "temporary_view_not_rolled_back", ["write_started"] = true, ["result"] = result });
            }
            else if (capture) result["image"] = new JObject { ["captured"] = false, ["why"] = "no model element with geometry in scope" };
            result["next"] = outcome.Errors + outcome.Warnings > 0
                ? "Fix each finding (move, delete the duplicate, reroute) or undo the write with horizun_undo; then call this again. Do not report the modelling as done while errors remain."
                : outcome.Partial ? "Partial check - narrow element_ids or raise time_budget_seconds before calling the result clean."
                : "No spatial conflict among the changed elements. Still look at the image: this check sees solids, not intent (wrong level, wrong room, missing element).";
            return CommandResult.Ok(result);
        }

        private static JObject Picture(Document doc, List<Element> subjects, SpatialCoherence.Outcome outcome, int pixel, string orientation)
        {
            var o = new JObject();
            string dir = Path.Combine(Path.GetTempPath(), "Horizun", "verify", Guid.NewGuid().ToString("N"));
            string rollback = "not_attempted";
            try
            {
                Directory.CreateDirectory(dir);
                var errorIds = new HashSet<long>(); var warnIds = new HashSet<long>();
                foreach (var f in outcome.Findings)
                {
                    var set = f.Verdict.Severity == "error" ? errorIds : warnIds;
                    set.Add(Rid.Value(f.A.Id)); if (f.LinkB == null) set.Add(Rid.Value(f.B.Id));
                }
                // Frame the subjects plus everything a finding names.
                var framed = new Dictionary<long, Element>();
                foreach (Element e in subjects) framed[Rid.Value(e.Id)] = e;
                // A finding's B side in a LINK has link coordinates and a link id: it is
                // framed and coloured through its host-side partner, never by its own id.
                foreach (var f in outcome.Findings) { framed[Rid.Value(f.A.Id)] = f.A; if (f.LinkB == null) framed[Rid.Value(f.B.Id)] = f.B; }
                BoundingBoxXYZ box = Frame(framed.Values);
                using (var group = new TransactionGroup(doc, "Horizun: verify changes (temporary view)"))
                {
                    try
                    {
                        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("the temporary transaction group did not start");
                        View3D view;
                        using (var tx = new Transaction(doc, "Horizun: temporary verification view"))
                        {
                            tx.Start();
                            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                                ?? throw new InvalidOperationException("the model has no 3D view type");
                            view = View3D.CreateIsometric(doc, vft.Id);
                            try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                            try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
                            Orient(view, orientation);
                            view.SetSectionBox(box);
                            view.IsSectionBoxActive = true;
                            // FRAME THE PICTURE TO THE BOX. ZoomFitType.FitToPage fits the view's
                            // extents, and datums and far elements outside the section box still
                            // widen them: measured on a real model, the checked door came out as a
                            // speck in one corner. The crop box is set to the section box's corners
                            // expressed in view coordinates.
                            try
                            {
                                doc.Regenerate();
                                BoundingBoxXYZ crop = view.CropBox;
                                Transform toView = crop.Transform.Inverse;
                                var pts = new List<XYZ>();
                                for (int c = 0; c < 8; c++)
                                    pts.Add(toView.OfPoint(new XYZ((c & 1) == 0 ? box.Min.X : box.Max.X,
                                                                   (c & 2) == 0 ? box.Min.Y : box.Max.Y,
                                                                   (c & 4) == 0 ? box.Min.Z : box.Max.Z)));
                                double pad = 0.5;
                                crop.Min = new XYZ(pts.Min(q => q.X) - pad, pts.Min(q => q.Y) - pad, crop.Min.Z);
                                crop.Max = new XYZ(pts.Max(q => q.X) + pad, pts.Max(q => q.Y) + pad, crop.Max.Z);
                                view.CropBox = crop;
                                view.CropBoxActive = true;
                                view.CropBoxVisible = false;
                            }
                            catch { /* the section box alone still limits what is drawn */ }
                            foreach (Category c in doc.Settings.Categories)
                                if (c.CategoryType == CategoryType.Annotation && view.CanCategoryBeHidden(c.Id))
                                    try { view.SetCategoryHidden(c.Id, true); } catch { }
                            ElementId solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                                .FirstOrDefault(p => { try { return p.GetFillPattern().IsSolidFill; } catch { return false; } })?.Id;
                            foreach (Element e in framed.Values)
                            {
                                long id = Rid.Value(e.Id);
                                Color color = errorIds.Contains(id) ? new Color(220, 30, 30) : warnIds.Contains(id) ? new Color(240, 150, 20) : new Color(40, 110, 220);
                                try { view.SetElementOverrides(e.Id, Paint(color, solid)); } catch { }
                            }
                            tx.Commit();
                        }
                        var opts = new ImageExportOptions
                        {
                            ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(dir, "verify"),
                            HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                            ZoomType = ZoomFitType.FitToPage, FitDirection = FitDirectionType.Horizontal,
                            ImageResolution = ImageResolution.DPI_150
                        };
                        try { opts.PixelSize = pixel; } catch { }
                        opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                        doc.ExportImage(opts);
                    }
                    finally
                    {
                        try { if (group.GetStatus() == TransactionStatus.Started) rollback = Guard.RollBack(group).StatusName; }
                        catch (Exception ex) { rollback = "failed: " + ex.Message; }
                    }
                }
                string produced = Directory.GetFiles(dir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (produced == null || new FileInfo(produced).Length == 0)
                {
                    o["image"] = new JObject { ["captured"] = false, ["why"] = "ExportImage produced no file", ["temporary_view_rollback"] = rollback };
                    return o;
                }
                // image_path at the top level is what the server attaches as an image block.
                o["image_path"] = produced;
                o["image"] = new JObject
                {
                    ["captured"] = true, ["bytes"] = new FileInfo(produced).Length,
                    ["legend"] = "blue = changed elements without findings, red = in an error finding, orange = in a warning finding; annotations hidden; section box around them",
                    ["orientation"] = orientation, ["temporary_view_rollback"] = rollback
                };
            }
            catch (Exception ex)
            {
                o["image"] = new JObject { ["captured"] = false, ["why"] = ex.Message, ["temporary_view_rollback"] = rollback };
            }
            return o;
        }

        private static OverrideGraphicSettings Paint(Color c, ElementId solid)
        {
            var g = new OverrideGraphicSettings();
            g.SetProjectionLineColor(c);
            g.SetCutLineColor(c);
            if (solid != null)
            {
                g.SetSurfaceForegroundPatternId(solid); g.SetSurfaceForegroundPatternColor(c);
                g.SetCutForegroundPatternId(solid); g.SetCutForegroundPatternColor(c);
            }
            return g;
        }

        private static void Orient(View3D v, string orientation)
        {
            XYZ forward, up;
            switch (orientation)
            {
                case "top": forward = -XYZ.BasisZ; up = XYZ.BasisY; break;
                case "front": forward = XYZ.BasisY; up = XYZ.BasisZ; break;
                case "right": forward = -XYZ.BasisX; up = XYZ.BasisZ; break;
                case "isometric": forward = new XYZ(-1, 1, -1).Normalize(); up = new XYZ(-1, 1, 2).Normalize(); break;
                default: throw new ArgumentException("orientation must be isometric, top, front or right.");
            }
            v.SetOrientation(new ViewOrientation3D(v.GetOrientation().EyePosition, up, forward));
        }

        private static BoundingBoxXYZ Frame(IEnumerable<Element> elements)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (Element e in elements)
            {
                BoundingBoxXYZ b = null;
                try { b = e.get_BoundingBox(null); } catch { }
                if (b == null) continue;
                minX = Math.Min(minX, b.Min.X); minY = Math.Min(minY, b.Min.Y); minZ = Math.Min(minZ, b.Min.Z);
                maxX = Math.Max(maxX, b.Max.X); maxY = Math.Max(maxY, b.Max.Y); maxZ = Math.Max(maxZ, b.Max.Z);
            }
            if (minX > maxX) throw new InvalidOperationException("no element in scope has a bounding box");
            double pad = Math.Max(1.0, 0.15 * Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)));
            return new BoundingBoxXYZ { Min = new XYZ(minX - pad, minY - pad, minZ - pad), Max = new XYZ(maxX + pad, maxY + pad, maxZ + pad) };
        }
    }
}
