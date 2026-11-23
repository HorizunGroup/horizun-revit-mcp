// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_view_crop: a view's crop, in model terms, verified.
// Original Horizun code.
//
// FIELD FEEDBACK 2.1. Cropping a view was the one documentation step
// that still sent an agent to the screen, and dragging a crop in a shared model
// grabbed somebody else's grid (section 5 of that report). Through ModelEditRunner
// (rehearse, single-use token, re-read after commit, roll back on disagreement):
//
//   read                          crop in model terms, its size in model mm and paper mm
//   set_crop_box                  a rectangle in model X/Y (plans) or on the view's axes
//                                 (sections, elevations, callouts, details), optional depth
//   set_crop_from_elements        the elements' boxes carried into the crop frame + margin
//   set_crop_from_room            the room's boundary carried into the crop frame + margin
//   set_annotation_crop_offsets   left/right/top/bottom, paper mm; turns the annotation crop on
//   crop_active / crop_visible    the two switches
//
// 5.2 (P2): an orthographic 3D view is cropped too, always in VIEW coordinates (x = P.right,
// y = P.up, the view_axes convention) - a new 3D view's crop frame is the camera's, not the
// model's, so a model X/Y rectangle would not mean what it says. Perspective views are refused.
//
// The geometry is ViewCropGeometry (pure, tested). The crop frame is CropBox.Transform,
// so a rotated view is handled the same way as an aligned one. Writes are refused on a
// view template, on a crop a template / scope box / sketch owns (ViewCropRules, shared
// with manage_views set_crop), and on a view without the HRZ ownership mark unless it
// was created this session or allow_unmarked_view=true (HrzOwnership).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ViewCropCommand : ICommand
    {
        public string Name => "horizun_view_crop";
        public string Description => "Read and set a view's crop in model terms (box, from elements, from a room, annotation offsets, switches), re-read after commit.";

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: view crop", ViewCropGeometry.Validate, Plan);

        private static double[] V(XYZ p) => new[] { p.X, p.Y, p.Z };

        internal static CropFrame Frame(BoundingBoxXYZ crop)
        {
            Transform t = crop.Transform ?? Transform.Identity;
            return new CropFrame { Origin = V(t.Origin), X = V(t.BasisX), Y = V(t.BasisY), Z = V(t.BasisZ) };
        }

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string op = r.Value<string>("operation").Trim().ToLowerInvariant();
            View view = ModelEditRunner.Need<View>(doc, r, "view_id");
            bool shapeOp = op == "set_crop_shape" || op == "reset_crop_shape";
            if (view.IsTemplate)
            {
                string message = "view_id " + Rid.Value(view.Id) + " '" + view.Name + "' is a view template; a template has no crop of its own.";
                throw shapeOp ? InvalidShape(message) : new ArgumentException(message);
            }
            bool section = op == "read_section_box" || op == "set_section_box" || op == "section_box_active";
            if (section)
            {
                if (!(view is View3D v3) || v3.IsPerspective)
                    throw new ArgumentException("Section box operations require an orthographic 3D view. For a plan, use set_crop_box.");
                if (op == "read_section_box") return new ArchModelEdit { ReadResult = DescribeSectionBox(v3) };
                RequireSectionBoxEditable(doc, view);
                HrzOwnership.Require(doc, view, "view", r.Value<bool?>("allow_unmarked_view") == true, "allow_unmarked_view");
                return SectionBoxEdit(v3, r, op);
            }
            double[][] polygon = null;
            if (op == "set_crop_shape")
            {
                string error = ViewCropGeometry.ReadPolygon(r["points"], out polygon);
                if (error != null) throw InvalidShape(error);
            }
            if (shapeOp)
            {
                using (var manager = view.GetCropRegionShapeManager())
                    if (manager?.CanHaveShape != true)
                        throw InvalidShape("view_id " + Rid.Value(view.Id) + " does not allow a crop shape (CanHaveShape=false).");
            }

            if (!shapeOp && view is View3D persp && persp.IsPerspective)
                throw new UnsupportedCapability("view_id " + Rid.Value(view.Id) + " is a perspective 3D view; its crop is the camera's field of view, not a rectangle this operation sets.", FallbackSignal.ReasonUnsupportedKind);
            if (!shapeOp && !(view is ViewPlan) && !(view is ViewSection) && !(view is View3D))
                throw new UnsupportedCapability("view_id " + Rid.Value(view.Id) + " is a " + view.GetType().Name + "; this operation crops plans, sections, elevations, callouts, detail views and orthographic 3D views.", FallbackSignal.ReasonUnsupportedKind);
            if (op == "read")
            {
                JObject result = Describe(doc, view);
                try
                {
                    using (var manager = view.GetCropRegionShapeManager())
                        if (manager?.CanHaveShape == true) result["crop_shape"] = CropShapeEvidence(view, manager);
                }
                catch (Exception ex) { result["crop_shape_error"] = ex.Message; }
                return new ArchModelEdit { ReadResult = result };
            }

            bool boxOp = op == "set_crop_box" || op == "set_crop_from_elements" || op == "set_crop_from_room";
            CropPreflightFacts facts = ManageViewsCommand.CropPreflight(doc, view);
            if (!boxOp && !shapeOp) { facts.ScopeBoxId = null; facts.ScopeBoxName = null; }
            if (!boxOp) facts.NonRectangularShape = null;
            string refusal = ViewCropRules.Refusal(facts);
            if (refusal != null) throw new ArgumentException(refusal + " Nothing was written.");
            HrzOwnership.Require(doc, view, "view", r.Value<bool?>("allow_unmarked_view") == true, "allow_unmarked_view");

            var edit = new ArchModelEdit();
            edit.Planned.Add(ModelEditRunner.Planned(view, PlannedAction.Modify, r));
            edit.Summary["operation"] = op;
            edit.Summary["view"] = Describe(doc, view);
            ElementId vid = view.Id;

            if (shapeOp) return ShapeEdit(doc, view, r["points"], polygon, edit);
            if (boxOp) return BoxEdit(doc, view, r, op, edit);
            if (op == "set_annotation_crop_offsets")
            {
                if (view is View3D)
                    throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a 3D view; Revit gives it no annotation crop. set_annotation_crop_offsets works on plans, sections, elevations, callouts and detail views. Nothing was written.");
                double[] off = ((JArray)r["offsets"]).Select(t => t.Value<double>() * ViewCropGeometry.FeetPerMm).ToArray();
                edit.Summary["offsets_mm_paper"] = r["offsets"];
                edit.Apply = d =>
                {
                    View v = (View)d.GetElement(vid);
                    Parameter p = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                    if (p == null || p.IsReadOnly) throw new InvalidOperationException("view '" + v.Name + "' does not expose an editable annotation crop.");
                    if (!v.CropBoxActive) v.CropBoxActive = true;
                    p.Set(1);
                    ViewCropRegionShapeManager m = v.GetCropRegionShapeManager();
                    m.LeftAnnotationCropOffset = off[0]; m.RightAnnotationCropOffset = off[1];
                    m.TopAnnotationCropOffset = off[2]; m.BottomAnnotationCropOffset = off[3];
                };
                edit.Verify = d =>
                {
                    var check = new PostconditionCheck("annotation_crop_active", "left", "right", "top", "bottom");
                    View v = (View)d.GetElement(vid);
                    check.Compare("annotation_crop_active", true, v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.AsInteger() == 1);
                    ViewCropRegionShapeManager m = v.GetCropRegionShapeManager();
                    double[] got = { m.LeftAnnotationCropOffset, m.RightAnnotationCropOffset, m.TopAnnotationCropOffset, m.BottomAnnotationCropOffset };
                    string[] names = { "left", "right", "top", "bottom" };
                    for (int i = 0; i < 4; i++)
                        check.Measure(names[i], Math.Round(off[i] * 304.8, 2), Math.Round(got[i] * 304.8, 2), 0.1, "mm_paper", "ViewCropRegionShapeManager re-read");
                    edit.Evidence["view"] = Describe(d, v);
                    return check;
                };
                return edit;
            }
            // crop_active / crop_visible
            bool value = r.Value<bool>("value");
            bool visible = op == "crop_visible";
            edit.Summary["value"] = value;
            edit.Apply = d =>
            {
                View v = (View)d.GetElement(vid);
                if (visible) v.CropBoxVisible = value; else v.CropBoxActive = value;
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(op);
                View v = (View)d.GetElement(vid);
                check.Compare(op, value, visible ? v.CropBoxVisible : v.CropBoxActive);
                edit.Evidence["view"] = Describe(d, v);
                return check;
            };
            return edit;
        }

        private static ArgumentException InvalidShape(string message)
        {
            var error = new ArgumentException(message);
            error.Data["error_code"] = ErrorCodes.EntradaInvalida;
            return error;
        }

        private static ArchModelEdit ShapeEdit(Document doc, View view, JToken requested, double[][] points, ArchModelEdit edit)
        {
            bool reset = points == null;
            edit.VerificationFailureCode = ErrorCodes.VerificacionFallida;
            using (var manager = view.GetCropRegionShapeManager())
            {
                edit.Summary["removes_crop_split"] = !reset && manager.Split;
                edit.Planned[0].BeforeValues["crop_shape"] = CropShapeEvidence(view, manager).ToString(Newtonsoft.Json.Formatting.None);
            }

            ElementId id = view.Id;
            if (reset)
            {
                edit.Apply = d =>
                {
                    using (var manager = ((View)d.GetElement(id)).GetCropRegionShapeManager())
                        if (manager.ShapeSet) manager.RemoveCropRegionShape();
                };
                edit.Verify = d =>
                {
                    var v = (View)d.GetElement(id);
                    using (var manager = v.GetCropRegionShapeManager())
                    {
                        var check = new PostconditionCheck("shape_set");
                        check.Compare("shape_set", false, manager.ShapeSet);
                        edit.Evidence["crop_shape"] = CropShapeEvidence(v, manager);
                        return check;
                    }
                };
                return edit;
            }

            edit.Summary["points"] = requested;
            double[][] feet = points.Select(p => new[] { p[0] / 304.8, p[1] / 304.8 }).ToArray();
            XYZ origin = view.Origin, right = view.RightDirection, up = view.UpDirection;
            XYZ[] model = feet.Select(p => origin + p[0] * right + p[1] * up).ToArray();

            var loop = new CurveLoop();
            for (int i = 0; i < model.Length; i++)
            {
                XYZ a = model[i], b = model[(i + 1) % model.Length];
                if (!XYZ.IsWithinLengthLimits(a) || a.DistanceTo(b) <= doc.Application.ShortCurveTolerance)
                    throw InvalidShape("points[" + i + "] is outside Revit's model limits or starts an edge below its short-curve tolerance.");
                loop.Append(Line.CreateBound(a, b));
            }
            using (var manager = view.GetCropRegionShapeManager())
                if (!manager.IsCropRegionShapeValid(loop))
                    throw InvalidShape("Revit rejected the crop boundary (IsCropRegionShapeValid=false).");

            edit.Apply = d =>
            {
                var v = (View)d.GetElement(id);
                using (var manager = v.GetCropRegionShapeManager())
                {
                    // SetCropShape preserves rectangular splits. The request defines one region.
                    if (manager.Split) manager.RemoveSplit();
                    FixPlanimetryCommand.SetCropShapeWithoutGeneratedDimensions(d, manager, loop);
                }
                v.CropBoxActive = true;
            };
            edit.Verify = d =>
            {
                var v = (View)d.GetElement(id);
                using (var manager = v.GetCropRegionShapeManager())
                {
                    var actual = ReadCropPolygon(manager, origin, right, up);
                    var check = ViewCropGeometry.VerifyPolygon(feet, actual, manager.ShapeSet, manager.Split, v.CropBoxActive);
                    edit.Evidence["crop_shape"] = CropShapeEvidence(v, manager);
                    return check;
                }
            };
            return edit;
        }

        private static double[][] ReadCropPolygon(ViewCropRegionShapeManager manager, XYZ origin, XYZ right, XYZ up)
        {
            var loops = manager.GetCropShape();
            if (loops.Count != 1 || loops[0].Any(curve => !(curve is Line))) return Array.Empty<double[]>();
            return loops[0].Select(curve =>
            {
                XYZ delta = curve.GetEndPoint(0) - origin;
                return new[] { delta.DotProduct(right), delta.DotProduct(up) };
            }).ToArray();
        }

        private static JObject CropShapeEvidence(View view, ViewCropRegionShapeManager manager) =>
            new JObject
            {
                ["shape_set"] = manager.ShapeSet,
                ["active"] = view.CropBoxActive,
                ["split"] = manager.Split,
                ["points_mm"] = ViewCropGeometry.PolygonMm(ReadCropPolygon(manager, view.Origin, view.RightDirection, view.UpDirection))
            };

        private static JObject DescribeSectionBox(View3D view)
        {
            BoundingBoxXYZ box = view.GetSectionBox();
            Transform t = box.Transform;
            return new JObject { ["view_id"] = Rid.Value(view.Id), ["name"] = view.Name,
                ["active"] = view.IsSectionBoxActive, ["local_min_mm"] = Mm3(box.Min), ["local_max_mm"] = Mm3(box.Max),
                ["transform"] = new JObject { ["origin_mm"] = Mm3(t.Origin), ["x"] = new JArray(V(t.BasisX)), ["y"] = new JArray(V(t.BasisY)), ["z"] = new JArray(V(t.BasisZ)) } };
        }

        private static void RequireSectionBoxEditable(Document doc, View view)
        {
            if (view.ViewTemplateId == ElementId.InvalidElementId) return;
            var template = doc.GetElement(view.ViewTemplateId) as View;
            if (template == null) throw new ArgumentException("The assigned view template could not be read; section box control is unknown.");
            long param = (long)BuiltInParameter.VIEWER_MODEL_CLIP_BOX_ACTIVE;
            if (template.GetTemplateParameterIds().Select(Rid.Value).Contains(param) &&
                !template.GetNonControlledTemplateParameterIds().Select(Rid.Value).Contains(param))
                throw new ArgumentException("The section box is controlled by the assigned view template '" + template.Name + "'.");
        }

        private static ArchModelEdit SectionBoxEdit(View3D view, JObject r, string op)
        {
            var edit = new ArchModelEdit();
            edit.Planned.Add(ModelEditRunner.Planned(view, PlannedAction.Modify, r));
            edit.Planned[0].BeforeValues["section_box"] = DescribeSectionBox(view).ToString(Newtonsoft.Json.Formatting.None);
            edit.Summary["operation"] = op;
            edit.Summary["before"] = DescribeSectionBox(view);
            ElementId id = view.Id;
            if (op == "section_box_active")
            {
                bool value = r.Value<bool>("value");
                edit.Summary["value"] = value;
                edit.Apply = d => ((View3D)d.GetElement(id)).IsSectionBoxActive = value;
                edit.Verify = d => { var v = (View3D)d.GetElement(id); var c = new PostconditionCheck("section_box_active"); c.Compare("section_box_active", value, v.IsSectionBoxActive); edit.Evidence["view"] = DescribeSectionBox(v); return c; };
                return edit;
            }
            double[] mn = ((JArray)r["min"]).Select(x => x.Value<double>() / 304.8).ToArray();
            double[] mx = ((JArray)r["max"]).Select(x => x.Value<double>() / 304.8).ToArray();
            Transform transform = r.Value<bool?>("keep_transform") == true ? view.GetSectionBox().Transform : Transform.Identity;
            Transform inverse = transform.Inverse;
            var points = new List<XYZ>();
            foreach (double x in new[] { mn[0], mx[0] }) foreach (double y in new[] { mn[1], mx[1] }) foreach (double z in new[] { mn[2], mx[2] }) points.Add(inverse.OfPoint(new XYZ(x, y, z)));
            XYZ min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
            XYZ max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
            edit.Summary["target_local_min_mm"] = Mm3(min); edit.Summary["target_local_max_mm"] = Mm3(max);
            edit.Apply = d => { var v = (View3D)d.GetElement(id); var b = new BoundingBoxXYZ { Transform = transform, Min = min, Max = max }; v.SetSectionBox(b); v.IsSectionBoxActive = true; };
            edit.Verify = d =>
            {
                var v = (View3D)d.GetElement(id); var b = v.GetSectionBox();
                var c = new PostconditionCheck("active", "min", "max", "transform");
                c.Compare("active", true, v.IsSectionBoxActive);
                c.Record("min", Mm3(min), Mm3(b.Min), min.DistanceTo(b.Min) <= ViewCropGeometry.ToleranceFeet);
                c.Record("max", Mm3(max), Mm3(b.Max), max.DistanceTo(b.Max) <= ViewCropGeometry.ToleranceFeet);
                c.Record("transform", "requested frame", "re-read frame", b.Transform.Origin.DistanceTo(transform.Origin) <= ViewCropGeometry.ToleranceFeet && b.Transform.BasisX.IsAlmostEqualTo(transform.BasisX) && b.Transform.BasisY.IsAlmostEqualTo(transform.BasisY) && b.Transform.BasisZ.IsAlmostEqualTo(transform.BasisZ));
                edit.Evidence["view"] = DescribeSectionBox(v); return c;
            };
            return edit;
        }

        private static ArchModelEdit BoxEdit(Document doc, View view, JObject r, string op, ArchModelEdit edit)
        {
            BoundingBoxXYZ crop = view.CropBox;
            CropFrame frame = Frame(crop);
            bool planLike = PlanLike(view);
            double marginFt = (r.Value<double?>("margin") ?? 0) * ViewCropGeometry.FeetPerMm;
            double[] target;
            double[] volume = null;
            Transform sectionTransform = null;
            CropFrame sectionFrame = null;
            if (op == "set_crop_box")
            {
                JArray mn = (JArray)r["min"], mx = (JArray)r["max"];
                double[] rect = { mn[0].Value<double>() * ViewCropGeometry.FeetPerMm, mn[1].Value<double>() * ViewCropGeometry.FeetPerMm,
                                  mx[0].Value<double>() * ViewCropGeometry.FeetPerMm, mx[1].Value<double>() * ViewCropGeometry.FeetPerMm };
                target = planLike ? ViewCropGeometry.TargetFromModelRect(frame, rect, frame.Origin[2]) : ViewCropGeometry.TargetFromViewAxes(frame, rect);
                edit.Summary["convention"] = planLike ? "model_xy" : "view_axes (x = P.right, y = P.up)";
                if (r["depth"] != null && view is View3D)
                    throw new ArgumentException("depth does not apply to a 3D view: its depth is the section box (operation=set_section_box).");
                if (r["depth"] != null && planLike)
                    throw new ArgumentException("depth applies to sections, elevations and their callouts; a plan's depth is its view range (horizun_manage_views set_view_range).");
            }
            else if (op == "set_crop_from_elements")
            {
                var pts = new List<double[]>();
                foreach (JToken t in (JArray)r["element_ids"])
                {
                    long raw = t.Value<long>();
                    Element e = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) : null;
                    if (e == null) throw new ArgumentException("element_ids: " + raw + " is not an element of this document.");
                    BoundingBoxXYZ b = e.get_BoundingBox(null);
                    if (b == null) throw new ArgumentException("element " + raw + " has no model bounding box to crop to.");
                    pts.AddRange(Corners(b));
                }
                if (view is View3D view3D)
                {
                    RequireSectionBoxEditable(doc, view);
                    // Keep the native section box axes. The tilted camera crop and the section
                    // box have independent frames, and Revit can realign a camera-oriented box.
                    BoundingBoxXYZ nativeSection = view3D.GetSectionBox();
                    sectionTransform = nativeSection.Transform ?? Transform.Identity;
                    sectionFrame = Frame(nativeSection);
                    (target, volume) = ViewCropGeometry.ElementCropBounds(frame, sectionFrame, pts, marginFt);
                    if (Enumerable.Range(0, 3).Any(axis => volume[axis + 3] - volume[axis] <= ViewCropGeometry.ToleranceFeet))
                        throw new ArgumentException("The 3D section box would have no volume. Give a positive margin around the elements.");
                    edit.Planned[0].BeforeValues["section_box"] = DescribeSectionBox((View3D)view).ToString(Newtonsoft.Json.Formatting.None);
                    edit.Summary["section_box_depth_mm"] = (volume[5] - volume[2]) * 304.8;
                }
                else target = ViewCropGeometry.Bounds(pts.Select(frame.ToLocal), marginFt);
                edit.Summary["elements"] = ((JArray)r["element_ids"]).Count;
            }
            else
            {
                SpatialElement room = ModelEditRunner.Need<SpatialElement>(doc, r, "room_id");
                var pts = new List<double[]>();
                BoundingBoxXYZ rb = room.get_BoundingBox(null);
                double z0 = rb?.Min.Z ?? 0, z1 = rb?.Max.Z ?? 0;
                IList<IList<BoundarySegment>> loops = null;
                try { loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions()); } catch { }
                if (loops != null)
                    foreach (IList<BoundarySegment> loop in loops)
                        foreach (BoundarySegment s in loop)
                            foreach (XYZ p in s.GetCurve().Tessellate())
                            {
                                pts.Add(frame.ToLocal(new[] { p.X, p.Y, z0 }));
                                pts.Add(frame.ToLocal(new[] { p.X, p.Y, z1 }));
                            }
                if (pts.Count == 0)
                    throw new ArgumentException("room " + Rid.Value(room.Id) + " has no boundary (unplaced or not enclosed): there is nothing to crop to.");
                target = ViewCropGeometry.Bounds(pts, marginFt);
                edit.Summary["room"] = room.Name;
            }
            if (target[2] - target[0] <= ViewCropGeometry.ToleranceFeet || target[3] - target[1] <= ViewCropGeometry.ToleranceFeet)
                throw new ArgumentException("the crop would have no area in this view (the region is edge-on to it).");
            double[] wanted = ViewCropGeometry.InCallerTerms(frame, target, planLike, frame.Origin[2]);
            if (volume != null)
                edit.Summary["target_section_box"] = ViewCropGeometry.BindSectionBoxTarget(edit.Planned[0], sectionFrame, volume, wanted);
            int viewScale = SafeScale(view);
            edit.Summary["target"] = ViewCropGeometry.RectMm(wanted);
            edit.Summary["target_size_mm"] = new JArray(Math.Round((target[2] - target[0]) * 304.8, 1), Math.Round((target[3] - target[1]) * 304.8, 1));
            if (viewScale > 0)
                edit.Summary["target_paper_mm"] = new JArray(Math.Round(ViewCropGeometry.PaperMm(target[2] - target[0], viewScale), 1), Math.Round(ViewCropGeometry.PaperMm(target[3] - target[1], viewScale), 1));
            double? depthFt = r["depth"] == null ? (double?)null : r.Value<double>("depth") * ViewCropGeometry.FeetPerMm;
            ElementId vid = view.Id;
            edit.Apply = d =>
            {
                View v = (View)d.GetElement(vid);
                if (volume != null)
                {
                    var v3 = (View3D)v;
                    v3.SetSectionBox(new BoundingBoxXYZ
                    {
                        Transform = sectionTransform,
                        Min = new XYZ(volume[0], volume[1], volume[2]),
                        Max = new XYZ(volume[3], volume[4], volume[5])
                    });
                    v3.IsSectionBoxActive = true;
                    d.Regenerate();
                }
                BoundingBoxXYZ c = v.CropBox;
                double[] rect = volume == null ? target : ViewCropGeometry.TargetFromViewAxes(Frame(c), wanted);
                c.Min = new XYZ(rect[0], rect[1], c.Min.Z);
                c.Max = new XYZ(rect[2], rect[3], c.Max.Z);
                v.CropBox = c;
                v.CropBoxActive = true;
                if (depthFt.HasValue)
                {
                    Parameter far = v.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                    if (far == null || far.IsReadOnly) throw new InvalidOperationException("view '" + v.Name + "' exposes no editable far clip offset for depth.");
                    far.Set(depthFt.Value);
                }
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "crop_box", "crop_active" };
                if (depthFt.HasValue) req.Add("depth");
                if (volume != null) req.Add("section_box");
                var check = new PostconditionCheck(req.ToArray());
                View v = (View)d.GetElement(vid);
                BoundingBoxXYZ c = v.CropBox;
                double[] got = ViewCropGeometry.InCallerTerms(Frame(c), new[] { c.Min.X, c.Min.Y, c.Max.X, c.Max.Y }, planLike, frame.Origin[2]);
                check.Record("crop_box", ViewCropGeometry.RectMm(wanted), ViewCropGeometry.RectMm(got), ViewCropGeometry.Matches(wanted, got, ViewCropGeometry.ToleranceFeet));
                check.Compare("crop_active", true, v.CropBoxActive);
                if (volume != null)
                {
                    var v3 = (View3D)v;
                    BoundingBoxXYZ sectionBox = v3.GetSectionBox();
                    Transform t = sectionBox.Transform;
                    Transform original = sectionTransform;
                    bool sameFrame = t.BasisX.IsAlmostEqualTo(original.BasisX) &&
                        t.BasisY.IsAlmostEqualTo(original.BasisY) && t.BasisZ.IsAlmostEqualTo(original.BasisZ);
                    double[] gotVolume = ViewCropGeometry.SectionBounds(sectionFrame, Corners(sectionBox), 0);
                    check.Record("section_box", new JArray(volume), new JArray(gotVolume), v3.IsSectionBoxActive &&
                        sameFrame && volume.Zip(gotVolume, (a, b) => Math.Abs(a - b)).All(delta => delta <= ViewCropGeometry.ToleranceFeet));
                }
                if (depthFt.HasValue)
                {
                    double? far = v.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)?.AsDouble();
                    if (far.HasValue) check.Measure("depth", Math.Round(depthFt.Value * 304.8, 1), Math.Round(far.Value * 304.8, 1), 1.0, "mm", "VIEWER_BOUND_OFFSET_FAR re-read");
                    else check.Unreadable("depth", Math.Round(depthFt.Value * 304.8, 1), "far clip offset unreadable");
                }
                edit.Evidence["view"] = Describe(d, v);
                return check;
            };
            return edit;
        }

        /// <summary>Model X/Y for a plan-like 2D view; view axes for sections and for EVERY 3D view (its crop frame is the camera's).</summary>
        private static bool PlanLike(View v) => !(v is View3D) && ViewCropGeometry.IsPlanLike(V(v.ViewDirection));

        private static IEnumerable<double[]> Corners(BoundingBoxXYZ b)
        {
            Transform t = b.Transform ?? Transform.Identity;
            foreach (double x in new[] { b.Min.X, b.Max.X })
                foreach (double y in new[] { b.Min.Y, b.Max.Y })
                    foreach (double z in new[] { b.Min.Z, b.Max.Z })
                        yield return V(t.OfPoint(new XYZ(x, y, z)));
        }

        private static int SafeScale(View v) { try { return v.Scale; } catch { return 0; } }

        private static JArray Mm3(XYZ p) => new JArray(Math.Round(p.X * 304.8, 1), Math.Round(p.Y * 304.8, 1), Math.Round(p.Z * 304.8, 1));

        /// <summary>The crop as a caller reads it: model corners, the rectangle in the caller's convention, model and paper size.</summary>
        internal static JObject Describe(Document doc, View v)
        {
            var o = new JObject { ["view_id"] = Rid.Value(v.Id), ["name"] = v.Name, ["kind"] = v.ViewType.ToString() };
            int scale = SafeScale(v);
            o["scale"] = scale;
            try { o["crop_active"] = v.CropBoxActive; o["crop_visible"] = v.CropBoxVisible; } catch { }
            o["hrz"] = HrzOwnership.Read(v);
            try
            {
                if (v.ViewTemplateId != ElementId.InvalidElementId) o["template"] = doc.GetElement(v.ViewTemplateId)?.Name;
            }
            catch { }
            try
            {
                BoundingBoxXYZ c = v.CropBox;
                CropFrame f = Frame(c);
                bool planLike = PlanLike(v);
                o["crop"] = ViewCropGeometry.DescribeCrop(f, BoxLimits(c), planLike, scale);
            }
            catch (Exception ex) { o["crop_error"] = ex.Message; }
            try
            {
                if (v is View3D v3 && !v3.IsPerspective && v3.IsSectionBoxActive)
                {
                    BoundingBoxXYZ c = v.CropBox, section = v3.GetSectionBox();
                    o["section_box"] = DescribeSectionBox(v3);
                    ViewCropGeometry.DescribeSectionClippedCrop(o, Frame(c), BoxLimits(c), v3.CropBoxActive,
                        Frame(section), BoxLimits(section), scale);
                }
            }
            catch (Exception ex) { o["effective_crop_error"] = ex.Message; }
            try
            {
                ViewCropRegionShapeManager m = v.GetCropRegionShapeManager();
                o["annotation_crop"] = new JObject
                {
                    ["active"] = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.AsInteger() == 1,
                    ["offsets_mm_paper"] = new JArray(Math.Round(m.LeftAnnotationCropOffset * 304.8, 2), Math.Round(m.RightAnnotationCropOffset * 304.8, 2),
                                                     Math.Round(m.TopAnnotationCropOffset * 304.8, 2), Math.Round(m.BottomAnnotationCropOffset * 304.8, 2))
                };
            }
            catch { }
            return o;
        }

        private static double[] BoxLimits(BoundingBoxXYZ box) =>
            new[] { box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z };
    }
}
