// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The Revit half of annotation layout. AnnotationVisibility.cs holds the
// verdict arithmetic (and its tests); this file only asks Revit the questions.
//
// Two things it deliberately does NOT do:
//   * swallow a failed measurement (see AnnotationVisibility for why), and
//   * bound annotations by the MODEL crop. Annotations live inside the
//     ANNOTATION crop, which is the model crop plus its four offsets, so using
//     the model crop refused perfectly legal placements in the margin.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What one view contributes to a layout: obstacles, limits and coverage.</summary>
    internal sealed class AnnotationSurvey
    {
        public List<PlanBox> Obstacles = new List<PlanBox>();
        public PlanBox? Bounds;
        public JObject Coverage;
        public bool Complete;
    }

    internal static class AnnotationLayout
    {
        internal static PlanBox Box(Element e, View v)
        {
            BoundingBoxXYZ b = e.get_BoundingBox(v);
            if (b == null) throw new InvalidOperationException("Unreadable annotation extent: " + Rid.Value(e.Id));
            return Project(b, v);
        }

        internal static PlanBox Project(BoundingBoxXYZ b, View v)
        {
            var points = new List<XYZ>();
            foreach (double x in new[] { b.Min.X, b.Max.X })
                foreach (double y in new[] { b.Min.Y, b.Max.Y })
                    foreach (double z in new[] { b.Min.Z, b.Max.Z })
                        points.Add(b.Transform.OfPoint(new XYZ(x, y, z)).Subtract(v.Origin));
            return FromPoints(points, v);
        }

        private static PlanBox FromPoints(IEnumerable<XYZ> points, View v)
        {
            List<XYZ> list = points.ToList();
            if (list.Count == 0) return PlanBox.Unreadable;
            return PlanBox.FromCorners(
                list.Min(p => p.DotProduct(v.RightDirection)), list.Min(p => p.DotProduct(v.UpDirection)),
                list.Max(p => p.DotProduct(v.RightDirection)), list.Max(p => p.DotProduct(v.UpDirection)));
        }

        // ---------------------------------------------------------------------
        // Which elements are annotations for layout purposes.
        // ---------------------------------------------------------------------
        private static bool IsAnnotation(Element e)
        {
            return e is IndependentTag || e is SpatialElementTag || e is TextNote ||
                   e is Dimension || e is AnnotationSymbol;
        }

        // ---------------------------------------------------------------------
        // The survey. Everything else in this file is built on it.
        // ---------------------------------------------------------------------
        internal static AnnotationSurvey Survey(Document doc, View view, ElementId except = null,
                                                HashSet<long> accepted = null)
        {
            accepted = accepted ?? new HashSet<long>();
            var survey = new AnnotationSurvey();
            var entries = new List<JObject>();

            // The considered set is the UNION of what this view owns and what its
            // primary owns when this view is dependent: a dependent view shows the
            // primary's annotations, so sweeping only its own owner filter reports
            // an empty view and calls any placement clear.
            long primaryViewId = 0;
            var considered = new Dictionary<long, Element>();
            foreach (Element e in Owned(doc, view.Id)) considered[Rid.Value(e.Id)] = e;
            try
            {
                ElementId primary = view.GetPrimaryViewId();
                if (primary != null && primary != ElementId.InvalidElementId && primary != view.Id)
                {
                    primaryViewId = Rid.Value(primary);
                    foreach (Element e in Owned(doc, primary)) considered[Rid.Value(e.Id)] = e;
                }
            }
            catch
            {
                // Not every view kind answers GetPrimaryViewId. Absent is not
                // dependent, and the visible-element probe below still runs.
                primaryViewId = 0;
            }

            HashSet<long> visible = null;
            bool visibleProbed = false;
            string visibleProbeNote = "not_required";

            foreach (KeyValuePair<long, Element> pair in considered.OrderBy(p => p.Key))
            {
                Element e = pair.Value;
                if (except != null && e.Id == except) continue;

                string category, className;
                long ownerViewId;
                if (!Describe(e, out category, out className, out ownerViewId))
                {
                    entries.Add(AnnotationVisibility.Entry(pair.Key, null, null, 0,
                        AnnotationVisibility.ExcludedInvalidElement,
                        "the element could not be described; Revit reports it as no longer valid"));
                    continue;
                }
                if (!IsAnnotation(e)) continue;

                // 1. A readable extent is an obstacle, unconditionally. Including it
                //    is the conservative answer, so no other probe can talk us out
                //    of an annotation we were able to measure.
                PlanBox box;
                string readFailure;
                if (TryBox(e, view, out box, out readFailure))
                {
                    survey.Obstacles.Add(box);
                    entries.Add(AnnotationVisibility.Entry(pair.Key, category, className, ownerViewId,
                        AnnotationVisibility.Measured, "extent read in this view: " + PlanimetryGeometry.Signature(box)));
                    continue;
                }

                // 2. No extent. Look for a verifiable exclusion before refusing.
                string exclusion, evidence;
                if (Excluded(e, view, out exclusion, out evidence))
                {
                    entries.Add(AnnotationVisibility.Entry(pair.Key, category, className, ownerViewId, exclusion, evidence));
                    continue;
                }

                if (!visibleProbed)
                {
                    visible = VisibleIds(doc, view, out visibleProbeNote);
                    visibleProbed = true;
                }
                if (visible != null && !visible.Contains(pair.Key))
                {
                    entries.Add(AnnotationVisibility.Entry(pair.Key, category, className, ownerViewId,
                        AnnotationVisibility.ExcludedNotVisibleInView,
                        "no extent in this view AND absent from the host's own view-scoped visible-element " +
                        "collector (" + visibleProbeNote + "); Revit itself does not list it as visible here"));
                    continue;
                }

                // 3. Undecided. The caller may accept this exact id - and then the
                //    clearance it gets back says partial, not collision-free.
                if (accepted.Contains(pair.Key))
                {
                    entries.Add(AnnotationVisibility.Entry(pair.Key, category, className, ownerViewId,
                        AnnotationVisibility.AcceptedUnmeasurable,
                        "no readable extent (" + readFailure + "); accepted by explicit layout_accept_unmeasurable"));
                    continue;
                }
                entries.Add(AnnotationVisibility.Entry(pair.Key, category, className, ownerViewId,
                    visible == null ? AnnotationVisibility.UnknownVisibilityUndecidable
                                    : AnnotationVisibility.UnknownUnreadableExtent,
                    visible == null
                        ? "no readable extent (" + readFailure + ") and the view-scoped visible-element probe " +
                          "could not run (" + visibleProbeNote + ")"
                        : "no readable extent (" + readFailure + ") while the host's view-scoped collector DOES " +
                          "list it as visible in this view"));
            }

            JObject boundsReport;
            survey.Bounds = ResolveBounds(view, out boundsReport);
            survey.Coverage = AnnotationVisibility.Coverage(Rid.Value(view.Id), SafeScale(view), primaryViewId,
                                                            boundsReport, visibleProbed ? visibleProbeNote : "not_required",
                                                            entries);
            survey.Complete = survey.Coverage.Value<bool>("coverage_complete");
            return survey;
        }

        private static IEnumerable<Element> Owned(Document doc, ElementId viewId)
        {
            return new FilteredElementCollector(doc)
                .WherePasses(new ElementOwnerViewFilter(viewId))
                .WhereElementIsNotElementType();
        }

        private static bool Describe(Element e, out string category, out string className, out long ownerViewId)
        {
            category = null; className = null; ownerViewId = 0;
            try
            {
                if (!e.IsValidObject) return false;
                className = e.GetType().Name;
                category = e.Category == null ? null : e.Category.Name;
                ownerViewId = Rid.Value(e.OwnerViewId);
                return true;
            }
            catch { return false; }
        }

        private static bool TryBox(Element e, View view, out PlanBox box, out string failure)
        {
            box = PlanBox.Unreadable; failure = null;
            BoundingBoxXYZ raw;
            try { raw = e.get_BoundingBox(view); }
            catch (Exception ex) { failure = "get_BoundingBox threw " + ex.GetType().Name + ": " + ex.Message; return false; }
            if (raw == null) { failure = "get_BoundingBox returned null for this view"; return false; }
            try { box = Project(raw, view); }
            catch (Exception ex) { failure = "the extent could not be projected onto the view plane: " + ex.Message; return false; }
            if (!box.Valid) { failure = "the projected extent is degenerate"; return false; }
            return true;
        }

        /// <summary>Explicit, verifiable reasons an annotation is not in this view.</summary>
        private static bool Excluded(Element e, View view, out string verdict, out string evidence)
        {
            verdict = null; evidence = null;
            try
            {
                if (e.IsHidden(view))
                {
                    verdict = AnnotationVisibility.ExcludedElementHidden;
                    evidence = "Element.IsHidden(view) is true: hidden element by element in this view";
                    return true;
                }
            }
            catch { /* fall through: an unanswered probe is not an exclusion */ }
            try
            {
                if (e.Category != null && view.GetCategoryHidden(e.Category.Id))
                {
                    verdict = AnnotationVisibility.ExcludedCategoryHidden;
                    evidence = "View.GetCategoryHidden is true for category " + e.Category.Name;
                    return true;
                }
            }
            catch { }
            try
            {
                if (view.AreAnnotationCategoriesHidden)
                {
                    verdict = AnnotationVisibility.ExcludedAnnotationCategoriesHidden;
                    evidence = "View.AreAnnotationCategoriesHidden is true: every annotation category is off in this view";
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Revit's own answer to "what is visible in this view". Null when it cannot be asked.</summary>
        private static HashSet<long> VisibleIds(Document doc, View view, out string note)
        {
            if (view.IsTemplate)
            {
                note = "the view is a template and has no visible-element collector";
                return null;
            }
            try
            {
                var ids = new HashSet<long>();
                foreach (ElementId id in new FilteredElementCollector(doc, view.Id).ToElementIds())
                    ids.Add(Rid.Value(id));
                note = "FilteredElementCollector(doc, view_id) returned " + ids.Count + " visible elements";
                return ids;
            }
            catch (Exception ex)
            {
                note = "FilteredElementCollector(doc, view_id) threw " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static int SafeScale(View view)
        {
            try { return view.Scale; } catch { return 0; }
        }

        // ---------------------------------------------------------------------
        // The limit an annotation must stay inside.
        // ---------------------------------------------------------------------
        internal static PlanBox? ResolveBounds(View view, out JObject report)
        {
            bool cropActive;
            try { cropActive = view.CropBoxActive; }
            catch (Exception ex)
            {
                report = new JObject { ["source"] = "none", ["reason"] = "CropBoxActive could not be read: " + ex.Message };
                return null;
            }
            if (!cropActive)
            {
                report = new JObject { ["source"] = "none", ["reason"] = "the crop box is not active; the view imposes no layout limit" };
                return null;
            }

            PlanBox model;
            try { model = Project(view.CropBox, view); }
            catch (Exception ex)
            {
                report = new JObject { ["source"] = "none", ["reason"] = "the active crop box could not be projected: " + ex.Message };
                return null;
            }

            string annotationNote = null;
            try
            {
                ViewCropRegionShapeManager manager = view.GetCropRegionShapeManager();
                if (manager != null && manager.CanHaveAnnotationCrop)
                {
                    CurveLoop loop = manager.GetAnnotationCropShape();
                    if (loop != null)
                    {
                        var points = new List<XYZ>();
                        foreach (Curve curve in loop)
                            foreach (XYZ p in curve.Tessellate())
                                points.Add(p.Subtract(view.Origin));
                        PlanBox annotation = FromPoints(points, view);
                        // The annotation crop contains the model crop by construction.
                        // Anything smaller is a shape we did not understand, and the
                        // model crop is then the honest (tighter) limit.
                        if (annotation.Valid && PlanimetryGeometry.Contains(annotation, model, 1e-7))
                        {
                            report = new JObject
                            {
                                ["source"] = "annotation_crop",
                                ["rect_feet"] = new JArray(PlanimetryGeometry.ToDisplayArray(annotation, 1.0)),
                                ["model_crop_rect_feet"] = new JArray(PlanimetryGeometry.ToDisplayArray(model, 1.0)),
                                ["means"] = "annotations are bounded by the annotation crop, which is the model crop plus its offsets"
                            };
                            return annotation;
                        }
                        annotationNote = annotation.Valid
                            ? "the annotation crop shape does not contain the model crop; using the model crop"
                            : "the annotation crop shape produced no usable rectangle; using the model crop";
                    }
                    else annotationNote = "GetAnnotationCropShape returned null; using the model crop";
                }
                else annotationNote = "this view cannot have an annotation crop; using the model crop";
            }
            catch (Exception ex)
            {
                annotationNote = "GetAnnotationCropShape threw " + ex.GetType().Name + ": " + ex.Message + "; using the model crop";
            }

            report = new JObject
            {
                ["source"] = "model_crop",
                ["rect_feet"] = new JArray(PlanimetryGeometry.ToDisplayArray(model, 1.0)),
                ["annotation_crop_note"] = annotationNote
            };
            return model;
        }

        // ---------------------------------------------------------------------
        // Backwards-compatible entry points.
        // ---------------------------------------------------------------------
        internal static List<PlanBox> Obstacles(Document doc, View view, ElementId except = null,
                                                HashSet<long> accepted = null)
        {
            AnnotationSurvey survey = Survey(doc, view, except, accepted);
            if (!survey.Complete) throw new AnnotationCoverageException(survey.Coverage);
            return survey.Obstacles;
        }

        internal static PlanBox? Bounds(View view)
        {
            JObject ignored;
            return ResolveBounds(view, out ignored);
        }

        // ---------------------------------------------------------------------
        // Placement.
        // ---------------------------------------------------------------------
        /// <summary>
        /// The extent of the tag BEING PLACED. A tag with no readable extent cannot be
        /// laid out or verified, and the usual cause is not the view but the family:
        /// a tag family without a label renders nothing (measured live 2026-09-08 with
        /// a family created from the empty Metric Multi-Category Tag template), so the
        /// refusal names the family instead of an element id.
        /// </summary>
        private static BoundingBoxXYZ PointBox(XYZ at)
        {
            var box = new BoundingBoxXYZ();
            box.Transform = Transform.Identity;
            box.Min = at; box.Max = at;
            return box;
        }

        internal static PlanBox OwnBox(IndependentTag tag, View view)
        {
            BoundingBoxXYZ b;
            try { b = tag.get_BoundingBox(view); }
            catch (Exception ex) { throw new InvalidOperationException(OwnBoxRefusal(tag, view, "get_BoundingBox threw " + ex.Message)); }
            if (b == null) throw new InvalidOperationException(OwnBoxRefusal(tag, view, "get_BoundingBox returned null"));
            PlanBox box = Project(b, view);
            if (!box.Valid) throw new InvalidOperationException(OwnBoxRefusal(tag, view, "the projected extent is degenerate"));
            return box;
        }

        private static string OwnBoxRefusal(IndependentTag tag, View view, string probe)
        {
            string type = "(unknown)";
            try { Element t = tag.Document.GetElement(tag.GetTypeId()); if (t != null) type = t.Name + " (" + Rid.Value(t.Id) + ")"; } catch { }
            string text; try { text = tag.TagText ?? ""; } catch { text = "(unreadable)"; }
            string head = "The tag itself has no readable extent in view " + Rid.Value(view.Id) + " (" + probe + "; tag type " + type +
                          ", tag text '" + text + "'): a tag that renders nothing cannot be laid out or verified. ";
            // WHY it renders nothing is asked of the view before the family is blamed.
            // Measured on 2026 (2026-09-08): a labelled multi-category tag with its text
            // resolved ('D-01') still had no extent, because the view's template hid
            // the Multi-Category Tags category - a fact the view can state.
            string why = null;
            try
            {
                Category cat = tag.Category;
                // The tagged element first: a tag draws nothing for a host the view does
                // not show (outside its view range or crop, or hidden). Measured on 2026
                // (2026-09-08): a duct above a fresh plan's view range gave text 'D-01',
                // a visible category and still no geometry at any head elevation.
                Element host = null;
                try
                {
                    foreach (ElementId id in tag.GetTaggedLocalElementIds()) { host = tag.Document.GetElement(id); break; }
                }
                catch { host = null; }
                if (host != null && host.get_BoundingBox(view) == null)
                    why = "the tagged element " + Rid.Value(host.Id) + " (" + (host.Category != null ? host.Category.Name : host.GetType().Name) +
                          ") is not shown in this view - outside its view range or crop, or hidden - so the tag has nothing to draw; " +
                          "tag it in a view that shows it";
                else if (view.AreAnnotationCategoriesHidden)
                    why = "every annotation category is hidden in this view (View.AreAnnotationCategoriesHidden)";
                else if (cat != null && view.GetCategoryHidden(cat.Id))
                {
                    string template = null;
                    try { Element tpl = view.Document.GetElement(view.ViewTemplateId); template = tpl?.Name; } catch { template = null; }
                    why = "the tag's own category '" + cat.Name + "' is hidden in this view" +
                          (template != null ? " (view template '" + template + "')" : "") +
                          "; show the category there, or tag in a view that shows it";
                }
            }
            catch { why = null; }
            if (why != null) return head + "Cause found: " + why + ". Nothing was written.";
            if (string.IsNullOrEmpty(text))
                return head + "The tag text is empty: the family's label resolves to nothing for this element (an empty or " +
                       "label-less tag family, or a label whose parameter has no value here). Use a tag type whose family " +
                       "carries a visible label for this category, or fill the parameter it shows. Nothing was written.";
            return head + "The category is visible and the text resolved, yet Revit reports no extent: a LABEL-ONLY tag family " +
                   "has no bounding box in the API even when its label shows text (measured on 2026 with the stock " +
                   "M_Multi Category Tag). Give the family a frame or line around its label, or use a family that " +
                   "draws one. Nothing was written.";
        }

        internal static XYZ Place(Document doc, View view, IndependentTag tag, XYZ seed, double gap, double maxMove,
                                  HashSet<long> accepted = null, JObject coverageOut = null)
        {
            doc.Regenerate();
            // The bounding box includes the native leader when present: conservative
            // collision rejection, not a claim of exact glyph/leader intersection.
            // A LABEL-ONLY family publishes no box at all - that is every stock Autodesk
            // tag - and refusing to lay those out would refuse the ordinary case. So the
            // tag is laid out as a POINT at its head, which still keeps the head out of
            // other annotation, and the caller is told in coverage that the glyph's own
            // overlap was NOT measured. Nothing here claims a clearance it did not take.
            PlanBox initial; string extentProblem = null;
            try { initial = OwnBox(tag, view); }
            catch (Exception ex)
            {
                extentProblem = ex.Message;
                XYZ at = tag.TagHeadPosition;
                initial = Project(PointBox(at), view);
            }
            AnnotationSurvey survey = Survey(doc, view, tag.Id, accepted);
            if (!survey.Complete) throw new AnnotationCoverageException(survey.Coverage);
            if (coverageOut != null)
            {
                foreach (JProperty p in survey.Coverage.Properties()) coverageOut[p.Name] = p.Value.DeepClone();
                coverageOut["tag_extent_measured"] = extentProblem == null;
                if (extentProblem != null)
                {
                    coverageOut["tag_extent_unavailable_reason"] = extentProblem;
                    coverageOut["layout_claim"] = "The head was placed clear of every measured obstacle, but this tag " +
                        "publishes no extent, so its own glyph was NOT measured against them and no clearance is claimed for it.";
                }
            }
            List<PlanBox> obstacles = survey.Obstacles;
            PlanBox? bounds = survey.Bounds;
            double step = Math.Max(gap, Math.Max(initial.Width, initial.Height) * .25);
            step = Math.Max(step, 1.0 / 304.8);
            for (int ring = 0; ring <= 40; ring++)
            {
                for (int y = -ring; y <= ring; y++)
                    for (int x = -ring; x <= ring; x++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Abs(y)) != ring) continue;
                        double dx = x * step, dy = y * step;
                        if (Math.Sqrt(dx * dx + dy * dy) > maxMove + 1e-7) continue;
                        if (!DeliveryLayoutRules.Clear(DeliveryLayoutRules.Shift(initial, dx, dy), obstacles, gap, bounds)) continue;
                        XYZ chosen = seed.Add(view.RightDirection.Multiply(dx)).Add(view.UpDirection.Multiply(dy));
                        tag.TagHeadPosition = chosen; doc.Regenerate();
                        if (extentProblem != null) return chosen;
                        if (DeliveryLayoutRules.Clear(Box(tag, view), obstacles, gap, bounds)) return chosen;
                    }
            }
            throw new InvalidOperationException(
                "No measured collision-free tag layout within the approved displacement; batch refused. " +
                "Searched " + obstacles.Count + " measured obstacle(s) inside " +
                (bounds.HasValue ? "the view's " + survey.Coverage["bounds"].Value<string>("source") : "no view limit") +
                " for tag " + Rid.Value(tag.Id) + ". Raise layout_max_displacement, lower layout_clearance, or " +
                "make room in the view.");
        }

        // EVIDENCE IS NOT LAYOUT. Placing a tag somewhere specific is verified by the
        // view it owns, the element it points at, its head, orientation, leader, orphan
        // state and the text it renders - all measured here. An EXTENT is needed only to
        // keep a tag from overlapping something, and MEASURED on Revit 2023 and 2026 a
        // LABEL-ONLY family has none in the API even with its text resolved and its
        // category shown - which is every stock Autodesk tag. Throwing here refused all
        // of them for a fact that changes nothing about whether the tag is correct, so
        // the missing extent is now REPORTED, with the reason Revit gave, and only the
        // callers that actually lay a tag out still refuse.
        internal static JObject Evidence(IndependentTag tag, View view)
        {
            XYZ p = tag.TagHeadPosition;
            PlanBox box; string extentProblem = null;
            try { box = OwnBox(tag, view); }
            catch (Exception ex) { box = default(PlanBox); extentProblem = ex.Message; }
            var evidence = new JObject
            {
                ["view_id"] = Rid.Value(tag.OwnerViewId), ["type_id"] = Rid.Value(tag.GetTypeId()),
                ["head"] = new JArray(Math.Round(p.X, 8), Math.Round(p.Y, 8), Math.Round(p.Z, 8)),
                ["orientation"] = tag.TagOrientation.ToString(), ["has_leader"] = tag.HasLeader, ["orphaned"] = tag.IsOrphaned,
                ["text"] = tag.TagText,
                ["extent"] = extentProblem == null ? PlanimetryGeometry.Signature(box) : null,
                ["view_scale"] = view.Scale
            };
            if (extentProblem != null)
            {
                evidence["extent_measured"] = false;
                evidence["extent_unavailable_reason"] = extentProblem;
                evidence["layout_note"] = "This tag was placed and verified where it was asked for, but it publishes no " +
                    "extent, so nothing here claims it avoids overlapping other annotation. Automatic layout and " +
                    "avoid_collisions still refuse such a tag by name rather than guess.";
            }
            else evidence["extent_measured"] = true;
            return evidence;
        }
    }
}
