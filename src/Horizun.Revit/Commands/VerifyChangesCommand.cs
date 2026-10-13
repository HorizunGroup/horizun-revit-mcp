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
    public sealed partial class VerifyChangesCommand : ICommand
    {
        public string Name => "horizun_verify_changes";
        public string Description => "Spatial coherence check of the elements the last write changed (or the ids given), with an image of them and the findings highlighted.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            // operation=snapshot|compare_to (VerifyChangesSnapshot.cs) are a SEPARATE act
            // from the default spatial check below: a named baseline image, and a later
            // pixel diff against it. Both need the mutation gate because both build a
            // temporary view (rolled back, same as the check's own picture).
            string op = (request.Value<string>("operation") ?? "check").Trim();
            if (op != "check" && op != "snapshot" && op != "compare_to")
                return CommandResult.Fail("operation must be 'check', 'snapshot' or 'compare_to'.");
            if (op != "check")
            {
                GateResult opGate = DocumentGate.ForMutation(app, request, Name);
                if (!opGate.Ok) return opGate.Refusal;
                Document opDoc = opGate.Document;
                if (opDoc.IsFamilyDocument) return CommandResult.Fail("horizun_verify_changes checks project models; this is a family document.");
                return op == "snapshot" ? RunSnapshot(app, opDoc, request) : RunCompareTo(app, opDoc, request);
            }

            bool capture = request["capture"] == null || request.Value<bool>("capture");
            int pixel = request.Value<int?>("pixel_size") ?? 1400;
            if (pixel < 256 || pixel > 4096) return CommandResult.Fail("pixel_size must be between 256 and 4096.");
            int maxFindings = request.Value<int?>("max_findings") ?? 50;
            if (maxFindings < 1 || maxFindings > 500) return CommandResult.Fail("max_findings must be between 1 and 500.");
            int budgetS = request.Value<int?>("time_budget_seconds") ?? 60;
            if (budgetS < 5 || budgetS > 600) return CommandResult.Fail("time_budget_seconds must be between 5 and 600.");
            string scopeMode = (request.Value<string>("scope") ?? "last_write").Trim();
            if (scopeMode != "last_write" && scopeMode != "session")
                return CommandResult.Fail("scope must be 'last_write' or 'session'.");
            DateTime? sinceUtc = null;
            if (request["since_utc"] != null)
            {
                string rawSince = request.Value<string>("since_utc");
                if (string.IsNullOrWhiteSpace(rawSince) || !DateTime.TryParse(rawSince, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsedSince))
                    return CommandResult.Fail("since_utc must be a parseable ISO-8601 date/time.");
                sinceUtc = parsedSince;
            }
            bool includeAnnotation = request.Value<bool?>("include_annotation") ?? false;
            var ruleErrors = new List<string>();
            List<ClearanceZoneRules.Rule> clearanceRules = request["clearance_rules"] is JArray rulesArr
                ? ClearanceZoneRules.Parse(rulesArr, ruleErrors, ClearanceRulesSource.CanonicalCategory) : null;
            if (ruleErrors.Count > 0) return CommandResult.Fail(string.Join(" ", ruleErrors));
            List<long> explicitViewIds = null;
            if (request["view_ids"] != null)
            {
                if (!(request["view_ids"] is JArray viewArr) || viewArr.Count < 1 || viewArr.Count > 50)
                    return CommandResult.Fail("view_ids must hold 1..50 ids.");
                explicitViewIds = new List<long>();
                foreach (JToken t in viewArr)
                {
                    if (t.Type != JTokenType.Integer) return CommandResult.Fail("view_ids must be integers.");
                    explicitViewIds.Add(t.Value<long>());
                }
            }

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
            else if (scopeMode == "session")
            {
                IReadOnlyList<ChangeLedger.Entry> history = ChangeLedger.HistoryFor(doc);
                List<SessionScopeRules.WriteEntry> rows = history
                    .Select(h => new SessionScopeRules.WriteEntry { AtUtc = h.AtUtc, Tool = h.Tool, Added = h.Added, Modified = h.Modified }).ToList();
                SessionScopeRules.Outcome union = SessionScopeRules.Union(rows, sinceUtc);
                if (union.WritesConsidered == 0)
                    return CommandResult.Ok(new JObject
                    {
                        ["status"] = "nothing_to_check",
                        ["reason"] = sinceUtc.HasValue
                            ? "No Horizun write changed this document at or after since_utc=" + sinceUtc.Value.ToString("o") + "."
                            : "No Horizun write has changed this document since Revit started (the ledger lives in memory). Pass element_ids to check specific elements.",
                        ["read_only"] = true
                    });
                ids.AddRange(union.Ids.Where(Rid.CanRepresent).Select(Rid.Make));
                scope["source"] = "session";
                scope["writes_considered"] = union.WritesConsidered;
                scope["tools"] = new JArray(union.Tools);
                scope["ids_found"] = union.TotalIdsFound;
                scope["ids_checked"] = union.Ids.Count;
                scope["truncated"] = union.Truncated;
                if (union.Truncated)
                    scope["truncated_why"] = "more than " + SessionScopeRules.MaxIds + " distinct ids were touched across the writes considered; the " + SessionScopeRules.MaxIds +
                                             " of the most recent writes are checked (newest first) and the oldest are not.";
                if (sinceUtc.HasValue) scope["since_utc"] = sinceUtc.Value.ToString("o");
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
            // The explicit argument wins; without it, the same project/machine files the
            // automatic after-write pass reads (ClearanceRulesSource.cs), so an on-demand
            // look never judges less than the write it follows did.
            string clearanceOrigin = clearanceRules != null ? "argument" : null;
            List<string> fileRuleErrors = null;
            if (clearanceRules == null) clearanceRules = ClearanceRulesSource.Load(doc, out clearanceOrigin, out fileRuleErrors);
            SpatialCoherence.Outcome outcome = SpatialCoherence.Check(doc, subjects, 5000, budgetS * 1000, clearanceRules: clearanceRules);
            if (fileRuleErrors != null) outcome.ClearanceRuleErrors.AddRange(fileRuleErrors);
            JObject check = SpatialCoherence.ToJson(outcome, maxFindings);
            if (clearanceOrigin != null) check["clearance_rules_source"] = clearanceOrigin;
            string headline = SpatialCoherence.Headline(outcome);

            JObject annotationCheck = null;
            string annotationHeadline = null;
            if (includeAnnotation)
            {
                List<View> annotationViews = ResolveAnnotationViews(app, doc, explicitViewIds, subjects, out CommandResult viewError);
                if (viewError != null) return viewError;
                annotationCheck = TagOverlapCheck.Run(doc, annotationViews);
                int overlapCount = annotationCheck["findings"] is JArray fa ? fa.Count : 0;
                if (overlapCount > 0)
                    annotationHeadline = "Annotation check: " + overlapCount + " tag/text-note overlap(s) - review annotation_check.findings.";
            }

            var result = new JObject();
            string combinedHeadline = headline == null ? annotationHeadline : annotationHeadline == null ? headline : headline + " " + annotationHeadline;
            if (combinedHeadline != null) result["attention"] = combinedHeadline;
            result["status"] = check["status"];
            result["scope"] = scope;
            result["spatial_check"] = check;
            if (annotationCheck != null) result["annotation_check"] = annotationCheck;
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
            string next = outcome.Errors + outcome.Warnings > 0
                ? "Fix each finding (move, delete the duplicate, reroute) or undo the write with horizun_undo; then call this again. Do not report the modelling as done while errors remain."
                : outcome.Partial ? "Partial check - narrow element_ids or raise time_budget_seconds before calling the result clean."
                : outcome.IsPartial ? "Partial check - " + outcome.IsPartialWhy + ". Fix those rules or check that equipment by hand before calling the result clean."
                : "No spatial conflict among the changed elements. Still look at the image: this check sees solids, not intent (wrong level, wrong room, missing element).";
            if (annotationHeadline != null) next += " Also move or restyle the overlapping tags/text notes named in annotation_check.findings.";
            result["next"] = next;
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// Views to check for tag/text-note overlap: view_ids when given (every id must
        /// resolve to a View in this document - a caller-facing mistake, not a soft skip);
        /// otherwise the OwnerView of any tag/text note already in scope; otherwise the
        /// active view of THIS document when the UI has one open on it (never a different
        /// document's active view). No view found is "nothing_to_check", not an error.
        /// </summary>
        private static List<View> ResolveAnnotationViews(UIApplication app, Document doc, List<long> explicitViewIds,
            List<Element> subjects, out CommandResult error)
        {
            error = null;
            if (explicitViewIds != null)
            {
                var views = new List<View>();
                var bad = new List<long>();
                foreach (long vid in explicitViewIds)
                {
                    View v = Rid.CanRepresent(vid) ? doc.GetElement(Rid.Make(vid)) as View : null;
                    if (v == null) bad.Add(vid); else views.Add(v);
                }
                if (bad.Count > 0)
                {
                    error = CommandResult.Fail("view_ids does not resolve to a view in this document: " + string.Join(", ", bad) + ".");
                    return null;
                }
                return views;
            }
            var derived = new HashSet<long>();
            foreach (Element e in subjects)
                if (e is IndependentTag || e is TextNote)
                    try { derived.Add(Rid.Value(e.OwnerViewId)); } catch { }
            if (derived.Count > 0)
                return derived.Select(v => { try { return doc.GetElement(Rid.Make(v)) as View; } catch { return null; } }).Where(v => v != null).ToList();
            View active = null;
            try { if (app?.ActiveUIDocument?.Document != null && app.ActiveUIDocument.Document.Equals(doc)) active = app.ActiveUIDocument.ActiveView; }
            catch { }
            return active != null ? new List<View> { active } : new List<View>();
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
                            ShowEveryDiscipline(view);
                            try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                            try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
                            // The eye stands OUTSIDE the framed box on the viewer side: the
                            // default isometric eye was placed for the model as it was, and
                            // 75 walls at +30 m looked at "top" from it came back blank.
                            Orient(view, orientation, box);
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
                                FitCropToBox(view, box);
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
                // A FILE IS NOT A PICTURE. A blank export (measured: 7.7 KB, orientation=top,
                // walls at +30 m) used to say captured=true. The pixels are measured now, and
                // an image that is all background is reported as not captured, by name.
                JObject content;
                ImageContent measured = MeasureImage(produced, out content);
                if (measured != null && measured.IsBlank)
                {
                    o["image"] = new JObject
                    {
                        ["captured"] = false, ["finding"] = "blank_image",
                        ["why"] = "the exported image is blank: " + measured.ContentPixels + " of " +
                                  ((long)measured.Width * measured.Height) + " pixels differ from the background colour. " +
                                  "The elements were not drawn from this camera; try another orientation or look in Revit.",
                        ["content"] = content, ["blank_image_path"] = produced, ["bytes"] = new FileInfo(produced).Length,
                        ["orientation"] = orientation, ["temporary_view_rollback"] = rollback
                    };
                    return o;
                }
                // image_path at the top level is what the server attaches as an image block.
                o["image_path"] = produced;
                o["image"] = new JObject
                {
                    ["captured"] = true, ["bytes"] = new FileInfo(produced).Length,
                    ["legend"] = "blue = changed elements without findings, red = in an error finding, orange = in a warning finding; annotations hidden; section box around them",
                    ["orientation"] = orientation, ["content"] = content, ["temporary_view_rollback"] = rollback
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

        /// <summary>
        /// THE TEMPORARY VIEW DRAWS EVERYTHING IT FRAMES. A 3D view type can carry a
        /// template and a discipline: measured in Revit 2026 on 2026-09-30, the structural
        /// sample's 3D view is Structural, which does not draw non-structural walls, and 68
        /// such walls came back as a blank picture in every orientation while beams in the
        /// same model drew. No template, Coordination discipline.
        /// </summary>
        internal static void ShowEveryDiscipline(View3D view)
        {
            try { if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId; } catch { }
            try { view.Discipline = ViewDiscipline.Coordination; } catch { }
        }

        private static void Orient(View3D v, string orientation) => Orient(v, orientation, null);

        /// <summary>
        /// Aims the view. With a box, the eye stands outside it on the viewer side
        /// (CaptureFramingRules.Eye) so every element framed is in front of the camera;
        /// without one the view keeps its own eye, as before.
        /// </summary>
        private static void Orient(View3D v, string orientation, BoundingBoxXYZ frame)
        {
            double[] f, u;
            CaptureFramingRules.Directions(orientation, out f, out u);
            XYZ forward = new XYZ(f[0], f[1], f[2]), up = new XYZ(u[0], u[1], u[2]);
            XYZ eye = v.GetOrientation().EyePosition;
            if (frame != null)
            {
                double[] e = CaptureFramingRules.Eye(new[] { frame.Min.X, frame.Min.Y, frame.Min.Z },
                                                     new[] { frame.Max.X, frame.Max.Y, frame.Max.Z }, f);
                eye = new XYZ(e[0], e[1], e[2]);
            }
            v.SetOrientation(new ViewOrientation3D(eye, up, forward));
        }

        /// <summary>
        /// FRAME THE PICTURE TO THE BOX. The section box corners in the crop's own frame
        /// give the crop rectangle; the crop's Z range (the view's depth) is left as Revit
        /// made it.
        /// </summary>
        private static void FitCropToBox(View3D view, BoundingBoxXYZ box)
        {
            BoundingBoxXYZ crop = view.CropBox;
            Transform toView = crop.Transform.Inverse;
            var local = CaptureFramingRules.Corners(new[] { box.Min.X, box.Min.Y, box.Min.Z }, new[] { box.Max.X, box.Max.Y, box.Max.Z })
                .Select(c => toView.OfPoint(new XYZ(c[0], c[1], c[2])))
                .Select(p => new[] { p.X, p.Y, p.Z });
            double[] min, max;
            CaptureFramingRules.CropAround(local, 0.5, out min, out max);
            // THE DEPTH IS REVIT'S. Measured in Revit 2026 (2026-09-30): writing the box's
            // depth into the crop's Z blanked EVERY orientation, isometric included, on 68
            // walls at +30 m. The rectangle is ours; the depth stays the view's own, and the
            // eye outside the box (Orient) is what keeps the elements in front of it.
            crop.Min = new XYZ(min[0], min[1], crop.Min.Z);
            crop.Max = new XYZ(max[0], max[1], crop.Max.Z);
            view.CropBox = crop;
        }

        /// <summary>
        /// Decodes the exported PNG and measures how much of it is not background. Null
        /// (with content saying why) when it cannot be decoded: unmeasured is said, not
        /// promoted to "has content".
        /// </summary>
        private static ImageContent MeasureImage(string path, out JObject content)
        {
            try
            {
                int[] pixels; int width, height;
                DecodePng(path, out pixels, out width, out height);
                ImageContent m = ImageBlankness.Measure(pixels, width, height);
                content = new JObject
                {
                    ["measured"] = true, ["width"] = m.Width, ["height"] = m.Height,
                    ["background_argb"] = m.BackgroundArgb.ToString("X8"),
                    ["content_pixels"] = m.ContentPixels, ["content_ratio"] = Math.Round(m.ContentRatio, 6),
                    ["blank"] = m.IsBlank
                };
                return m;
            }
            catch (Exception ex)
            {
                content = new JObject { ["measured"] = false, ["why"] = "the image could not be decoded to measure it: " + ex.Message };
                return null;
            }
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
