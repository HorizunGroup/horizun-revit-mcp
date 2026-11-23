using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CaptureViewCommand
    {
        private static readonly IReadOnlyList<string> TemporaryFields = ElementIdentityGuard.CaptureTemporaryFields;
        private static bool HasTemporaryOptions(JObject request) => TemporaryFields.Any(f => request[f] != null);
        private CommandResult CaptureTemporary(UIApplication app, JObject request, View view)
        {
            // The repair transaction below restores ExportImage's persistent camera
            // state, but committing it on its own dirties a previously saved model.
            // Enclose BOTH the export and its repair in a disposable undo boundary.
            var gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            var doc = gate.Document;
            // Missing geometric extents are a precondition, not a rolled-back write.
            // Resolve the exact same bounds used by Frame before opening even the
            // outer isolation group, so a level/type ID never masquerades as a
            // failed transaction.
            if (request["element_ids"] != null)
            {
                try { FramePoints(doc, view, request["element_ids"], out _); }
                catch (ArgumentException ex)
                {
                    return CommandResult.FailCoded(ex.Message.StartsWith("element_ids contains an element that does not exist:")
                        ? ErrorCodes.ElementoNoEncontrado : ErrorCodes.PrecondicionNoCumplida, ex.Message,
                        ErrorCodes.TypeNameOf(ex));
                }
            }
            bool modifiedBefore = doc.IsModified;
            var categories = new HashSet<ElementId>();
            foreach (Category category in doc.Settings.Categories)
                if (view.CanCategoryBeHidden(category.Id)) categories.Add(category.Id);
            if (request["hide_category_ids"] is JArray requestedCategories)
                foreach (var id in requestedCategories)
                    if (id.Type == JTokenType.Integer && Rid.CanRepresent(id.Value<long>()))
                    {
                        var categoryId = Rid.Make(id.Value<long>());
                        if (Category.GetCategory(doc, categoryId) != null && view.CanCategoryBeHidden(categoryId))
                            categories.Add(categoryId);
                    }
            var stateBefore = ViewState(view, categories);
            using (var scope = new TransactionGroup(doc, "Horizun: capture isolation"))
            {
                if (scope.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Capture isolation did not start.");
                CommandResult result;
                try { result = CaptureTemporaryCore(app, request, view); }
                finally
                {
                    if (scope.GetStatus() == TransactionStatus.Started && !Guard.RollBack(scope).Confirmed)
                        throw new InvalidOperationException("Capture isolation rollback was not confirmed.");
                }
                var data = result.Success ? JObject.FromObject(result.Data) :
                    result.Detail == null ? new JObject() : (JObject)result.Detail.DeepClone();
                bool viewRestored = SameViewState(stateBefore, ViewState(view, categories));
                data["view_restored"] = viewRestored;
                data["document_modified_before"] = modifiedBefore;
                data["document_modified_after"] = doc.IsModified;
                data["document_modified_preserved"] = modifiedBefore == doc.IsModified;
                data["capture_isolation_rollback"] = "RolledBack";
                if (!viewRestored || modifiedBefore != doc.IsModified)
                    return CommandResult.FailWithDetail("View or document modified state changed after capture isolation rollback.", data);
                if (!result.Success) return CommandResult.FailWithDetail(result.Error, data);
                return CommandResult.Ok(data);
            }
        }
        private CommandResult CaptureTemporaryCore(UIApplication app, JObject request, View view)
        {
            var gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            var categoryIds = new HashSet<ElementId>();
            var explicitIds = new HashSet<ElementId>();
            bool templated = false, templateDetached = false;
            JObject before; CaptureRestoreState saved; double margin; JObject framing = null;
            try
            {
                if (request["calibrate_world_to_pixel"] != null && request["calibrate_world_to_pixel"].Type != JTokenType.Boolean) throw new ArgumentException("calibrate_world_to_pixel must be boolean.");
                if (request.Value<bool?>("calibrate_world_to_pixel") == true && request.Value<bool?>("hide_annotations") != true) throw new ArgumentException("Pixel calibration requires hide_annotations=true to keep annotation extents out of the export.");
                if (request.Value<bool?>("calibrate_world_to_pixel") == true && (!(view is ViewPlan) && !(view is ViewSection))) throw new ArgumentException("Pixel calibration supports plans and sections only.");
                if (request.Value<bool?>("calibrate_world_to_pixel") == true && (request.Value<int?>("pixel_size") ?? 1600) > 4096) throw new ArgumentException("Pixel calibration supports pixel_size up to 4096.");
                if (request["display_style"] != null)
                {
                    // shaded / hlr / wireframe / ... or Revit's own DisplayStyle names; an unknown word is
                    // refused here, before any transaction, with the list of words that work.
                    View3DRequestRules.ParseStyle(request.Value<string>("display_style"), out string styleProblem);
                    if (styleProblem != null) throw new ArgumentException(styleProblem);
                }
                margin = request["margin_ratio"] == null ? 0.08 : GeometryInput.Number(request["margin_ratio"], "margin_ratio");
                if (margin < 0 || margin > 1) throw new ArgumentException("margin_ratio must be in [0,1].");
                // margin_ratio frames element_ids; without them it has nothing to frame and is ignored (field: 6 refusals).
                // A view template locks visibility: the categories are checked AFTER the template is
                // copied onto the view and detached inside the rolled-back transaction below.
                templated = view.ViewTemplateId != null && view.ViewTemplateId != ElementId.InvalidElementId;
                if (request["hide_category_ids"] != null)
                {
                    if (!(request["hide_category_ids"] is JArray categories) || categories.Count > 256) throw new ArgumentException("hide_category_ids must have at most 256 IDs.");
                    foreach (var id in categories)
                    {
                        if (id.Type != JTokenType.Integer || !Rid.CanRepresent(id.Value<long>())) throw new ArgumentException("Category IDs must be integers.");
                        var categoryId = Rid.Make(id.Value<long>());
                        if (Category.GetCategory(doc, categoryId) == null || (!templated && !view.CanCategoryBeHidden(categoryId))) throw new ArgumentException("Category cannot be hidden in this view: " + id);
                        categoryIds.Add(categoryId);
                        explicitIds.Add(categoryId);
                    }
                }
                if (request.Value<bool?>("hide_annotations") == true)
                    foreach (Category category in doc.Settings.Categories)
                        if (category.CategoryType == CategoryType.Annotation && (templated || view.CanCategoryBeHidden(category.Id))) categoryIds.Add(category.Id);
                before = ViewState(view, categoryIds);
                saved = new CaptureRestoreState(view, categoryIds);
            }
            catch (Exception ex) { return CommandResult.Fail("Invalid capture options: " + ex.Message); }
            CommandResult captured = null; string rollback = "not_attempted", error = null;
            string styleOverrideWarning = null;
            using (var group = new TransactionGroup(doc, "Horizun: temporary capture"))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Capture group did not start.");
                    using (var tx = new Transaction(doc, "Horizun: frame capture"))
                    {
                        tx.Start();
                        if (templated && (categoryIds.Count > 0 || request["display_style"] != null))
                        {
                            // Copy the template's settings onto the view, then detach it: the image keeps
                            // the template's look and the requested categories can be hidden. The whole
                            // group is rolled back below, so the view gets its template back.
                            var template = doc.GetElement(view.ViewTemplateId) as View;
                            view.ViewTemplateId = ElementId.InvalidElementId;
                            if (template != null) view.ApplyViewTemplateParameters(template);
                            templateDetached = true;
                            foreach (var id in categoryIds.ToList())
                            {
                                if (view.CanCategoryBeHidden(id)) continue;
                                if (explicitIds.Contains(id)) throw new ArgumentException("Category cannot be hidden in this view: " + Rid.Value(id));
                                categoryIds.Remove(id);
                            }
                        }
                        foreach (var id in categoryIds) view.SetCategoryHidden(id, true);
                        if (request["display_style"] != null)
                        {
                            string styleWord = request.Value<string>("display_style");
                            string styleName = View3DRequestRules.ParseStyle(styleWord, out _) ?? styleWord;
                            if (!Enum.TryParse(styleName, false, out DisplayStyle style) || !Enum.IsDefined(typeof(DisplayStyle), style)) throw new ArgumentException("Invalid display_style.");
                            // A view template may forbid even an idempotent assignment.
                            if (view.DisplayStyle != style)
                            {
                                try { view.DisplayStyle = style; }
                                catch (InvalidOperationException ex)
                                {
                                    styleOverrideWarning = "display_style was not applied: " + ex.Message +
                                        " The image uses the view's existing style.";
                                }
                            }
                        }
                        if (request["orientation"] != null) Orient(view, request.Value<string>("orientation"));
                        if (request["element_ids"] != null) framing = Frame(doc, view, request["element_ids"], margin);
                        doc.Regenerate(); Guard.Commit(tx, "temporary capture");
                    }
                    // Capture the actual temporary state before rollback; the recursive
                    // request contains no editing fields and cannot recurse again.
                    var plain = new JObject { ["view_id"] = Rid.Value(view.Id), ["pixel_size"] = request.Value<int?>("pixel_size") ?? 1600 };
                    captured = Execute(app, plain.ToString());
                    if (captured.Success && request.Value<bool?>("calibrate_world_to_pixel") == true)
                    {
                        var clean = JObject.FromObject(captured.Data);
                        Calibrate(app, view, clean, plain.Value<int>("pixel_size"));
                        captured = CommandResult.Ok(clean);
                    }
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                finally
                {
                    try { if (group.GetStatus() == TransactionStatus.Started) rollback = Guard.RollBack(group).StatusName; }
                    catch (Exception ex) { rollback = "failed"; error = (error ?? "") + " Restore: " + ex.Message; }
                }
            }
            bool restored = false;
            try
            {
                restored = rollback == "RolledBack" && SameViewState(before, ViewState(view, categoryIds));
                // ExportImage can persist a 3D orientation outside the transaction
                // group's rollback. Restore those view properties explicitly.
                if (!restored && rollback == "RolledBack")
                {
                    using (var restore = new Transaction(doc, "Horizun: restore captured view"))
                    {
                        restore.Start();
                        saved.Apply(view);
                        doc.Regenerate();
                        Guard.Commit(restore, "restore captured view");
                    }
                    restored = SameViewState(before, ViewState(view, categoryIds));
                }
            }
            catch (Exception ex) { error = (error ?? "") + " Readback: " + ex.Message; }
            JObject after = null;
            try { if (!restored) after = ViewState(view, categoryIds); } catch { }
            if (restored && captured == null && request["display_style"] != null &&
                error != null && error.Contains("DisplayStyle cannot be modified"))
            {
                // Some views accept the setter but reject it at transaction commit.
                // After the rollback has been verified, repeat the capture without
                // that one temporary override. The reply names the downgrade.
                var withoutStyle = (JObject)request.DeepClone();
                withoutStyle.Remove("display_style");
                CommandResult retry = Execute(app, withoutStyle.ToString());
                if (!retry.Success) return retry;
                var fallback = JObject.FromObject(retry.Data);
                fallback["view_restored"] = true;
                fallback["rollback_status"] = rollback;
                fallback["display_style_applied"] = false;
                fallback["display_style_warning"] = "Revit refused display_style during the temporary transaction; the image uses the view's existing style.";
                fallback["requested_display_style"] = request.Value<string>("display_style");
                return CommandResult.Ok(fallback);
            }
            if (error != null || !restored || captured == null || !captured.Success)
                return CommandResult.FailWithDetail(error ?? captured?.Error ?? "View restoration was not verified.", new JObject
                {
                    ["code"] = "temporary_capture_failed",
                    ["error_code"] = restored ? ErrorCodes.TransaccionRevertida : ErrorCodes.ApiRevitExcepcion,
                    ["rollback_status"] = rollback,
                    ["view_restored"] = restored,
                    ["changes_applied"] = restored ? (JToken)false : null,
                    ["capture_data"] = captured?.Data == null ? null : JToken.FromObject(captured.Data),
                    ["state_difference"] = after == null ? null : new JArray(before.Properties()
                        .Where(p => !SameViewState(p.Value, after[p.Name])).Select(p => p.Name))
                });
            var result = JObject.FromObject(captured.Data);
            result["view_restored"] = restored; result["rollback_status"] = rollback;
            if (request["display_style"] != null)
            {
                result["display_style_applied"] = styleOverrideWarning == null;
                if (styleOverrideWarning != null) result["display_style_warning"] = styleOverrideWarning;
            }
            if (framing != null) result["framing"] = framing;
            if (templateDetached) result["view_template_detached_temporarily"] = true;
            result["temporary_options"] = new JObject(request.Properties().Where(p => TemporaryFields.Contains(p.Name)).Select(p => new JProperty(p.Name, p.Value.DeepClone())));
            return CommandResult.Ok(result);
        }
        private static void Orient(View view, string orientation)
        {
            if (!(view is View3D v) || v.IsPerspective) throw new ArgumentException("orientation requires an orthographic 3D view.");
            XYZ forward, up;
            switch (orientation)
            {
                case "top": forward = -XYZ.BasisZ; up = XYZ.BasisY; break;
                case "front": forward = XYZ.BasisY; up = XYZ.BasisZ; break;
                case "right": forward = -XYZ.BasisX; up = XYZ.BasisZ; break;
                case "isometric": forward = new XYZ(-1, -1, -1).Normalize(); up = new XYZ(-1, -1, 2).Normalize(); break;
                default: throw new ArgumentException("orientation must be top, front, right or isometric.");
            }
            v.SetOrientation(new ViewOrientation3D(v.GetOrientation().EyePosition, up, forward));
        }
        private static JObject Frame(Document doc, View view, JToken token, double margin)
        {
            var points = FramePoints(doc, view, token, out JArray byGeometry, out JArray excluded);
            var ids = (JArray)token;
            if (view is View3D v)
            {
                if (v.IsPerspective) throw new ArgumentException("Framing currently requires orthographic projection.");
                v.SetSectionBox(Bounds(points, margin)); v.IsSectionBoxActive = true;
            }
            else
            {
                if (!(view is ViewPlan) && !(view is ViewSection)) throw new ArgumentException("Framing requires a plan, section or orthographic 3D view.");
                using (var crop = view.CropBox)
                {
                    var bounds = Bounds(points.Select(p => crop.Transform.Inverse.OfPoint(p)).ToList(), margin);
                    bounds.Min = new XYZ(bounds.Min.X, bounds.Min.Y, crop.Min.Z); bounds.Max = new XYZ(bounds.Max.X, bounds.Max.Y, crop.Max.Z);
                    bounds.Transform = crop.Transform; view.CropBox = bounds;
                }
                view.CropBoxActive = true; view.CropBoxVisible = false;
            }
            return new JObject
            {
                ["elements"] = ids.Count - excluded.Count,
                ["requested_elements"] = ids.Count,
                ["excluded_without_geometry"] = excluded,
                ["warning"] = excluded.Count == 0 ? null : "Elements without readable geometric bounds were excluded from framing; the other requested elements determine the frame.",
                ["framed_by_geometry"] = byGeometry,
                ["note"] = byGeometry.Count == 0 ? null :
                    "These elements' model bounding boxes were far larger than their geometry (reinforcement does this), so the frame uses their geometry instead."
            };
        }

        private static List<XYZ> FramePoints(Document doc, View view, JToken token, out JArray byGeometry)
            => FramePoints(doc, view, token, out byGeometry, out _);

        private static List<XYZ> FramePoints(Document doc, View view, JToken token, out JArray byGeometry, out JArray excluded)
        {
            if (!(token is JArray ids) || ids.Count < 1 || ids.Count > 2000) throw new ArgumentException("element_ids must contain 1..2000 IDs.");
            var points = new List<XYZ>();
            byGeometry = new JArray();
            excluded = new JArray();
            foreach (var id in ids)
            {
                if (id.Type != JTokenType.Integer || !Rid.CanRepresent(id.Value<long>())) throw new ArgumentException("Invalid element ID.");
                var element = doc.GetElement(Rid.Make(id.Value<long>()));
                if (element == null) throw new ArgumentException(CaptureFrameRules.ElementProblem(id.Value<long>(), false, false));
                var box = element?.get_BoundingBox(null);
                // MEASURED (INFORME_P6 6.4): a bar's model box came back +-100 ft and the frame with it. Where the model
                // box is far larger than the geometry it holds, the geometry frames the element.
                double[] model = box == null ? null : Flat(Corners(box).ToList());
                double[] geometry = NeedsGeometry(element, model) ? GeometryBox(element, view) : null;
                double[] used = CaptureFrameRules.ChooseBounds(model, geometry, out string basis);
                if (used == null) { excluded.Add(id.Value<long>()); continue; }
                if (basis == "geometry") byGeometry.Add(id.Value<long>());
                points.Add(new XYZ(used[0], used[1], used[2])); points.Add(new XYZ(used[3], used[4], used[5]));
            }
            if (points.Count == 0) throw new ArgumentException(CaptureFrameRules.ElementProblem(0, true, false) + " IDs: " + excluded.ToString(Newtonsoft.Json.Formatting.None));
            return points;
        }

        /// <summary>Reinforcement, or any element whose model box is large enough that a wrong one would ruin the frame.</summary>
        private static bool NeedsGeometry(Element element, double[] model)
        {
            if (element == null) return false;
            if (element is Autodesk.Revit.DB.Structure.Rebar || element is Autodesk.Revit.DB.Structure.RebarInSystem ||
                element is Autodesk.Revit.DB.Structure.AreaReinforcement || element is Autodesk.Revit.DB.Structure.PathReinforcement ||
                element is Autodesk.Revit.DB.Structure.FabricSheet || element is Autodesk.Revit.DB.Structure.FabricArea) return true;
            if (model == null) return true;
            for (int axis = 0; axis < 3; axis++) if (model[axis + 3] - model[axis] > 30) return true;
            return false;
        }

        private static double[] Flat(List<XYZ> pts)
        {
            if (pts == null || pts.Count == 0) return null;
            return new[] { pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z), pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z) };
        }

        /// <summary>The box of what the element really draws: its solids' edges and its curves, tessellated; for a bar, also its centreline.</summary>
        private static double[] GeometryBox(Element element, View view)
        {
            var pts = new List<XYZ>();
            foreach (Options o in new[] { new Options { DetailLevel = ViewDetailLevel.Fine }, ViewOptions(view) })
            {
                if (o == null) continue;
                try { Collect(element.get_Geometry(o), pts); } catch { }
                if (pts.Count > 0) break;
            }
            if (element is Autodesk.Revit.DB.Structure.Rebar bar)
            {
                try
                {
                    foreach (Curve c in bar.GetCenterlineCurves(false, false, false, Autodesk.Revit.DB.Structure.MultiplanarOption.IncludeAllMultiplanarCurves, 0))
                        pts.AddRange(c.Tessellate());
                }
                catch { }
            }
            return Flat(pts);
        }

        private static Options ViewOptions(View view)
        {
            try { return view == null || view.IsTemplate ? null : new Options { View = view }; } catch { return null; }
        }

        private static void Collect(GeometryElement g, List<XYZ> pts)
        {
            if (g == null) return;
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Faces.Size > 0) { foreach (Edge e in s.Edges) pts.AddRange(e.Tessellate()); }
                else if (o is Curve c) pts.AddRange(c.Tessellate());
                else if (o is PolyLine pl) pts.AddRange(pl.GetCoordinates());
                else if (o is Mesh m) for (int i = 0; i < m.Vertices.Count; i++) pts.Add(m.Vertices[i]);
                else if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), pts);
            }
        }
        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            for (int i = 0; i < 8; i++) yield return box.Transform.OfPoint(new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z));
        }
        private static BoundingBoxXYZ Bounds(List<XYZ> points, double margin)
        {
            var min = new XYZ(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z));
            var max = new XYZ(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z));
            var pad = new XYZ(Math.Max((max.X - min.X) * margin, 0.01), Math.Max((max.Y - min.Y) * margin, 0.01), Math.Max((max.Z - min.Z) * margin, 0.01));
            return new BoundingBoxXYZ { Min = min - pad, Max = max + pad };
        }
        private static JArray Vector(XYZ p) => new JArray(p.X, p.Y, p.Z);
        private static JObject Box(BoundingBoxXYZ b) => new JObject
        { ["min"] = Vector(b.Min), ["max"] = Vector(b.Max), ["origin"] = Vector(b.Transform.Origin), ["basis_x"] = Vector(b.Transform.BasisX), ["basis_y"] = Vector(b.Transform.BasisY), ["basis_z"] = Vector(b.Transform.BasisZ) };
        private static JObject ViewState(View view, IEnumerable<ElementId> ids)
        {
            var state = new JObject { ["template_id"] = Rid.Value(view.ViewTemplateId), ["crop_active"] = view.CropBoxActive, ["crop_visible"] = view.CropBoxVisible, ["display_style"] = view.DisplayStyle.ToString(), ["crop"] = Box(view.CropBox) };
            state["categories"] = new JArray(ids.OrderBy(Rid.Value).Select(id => new JObject { ["id"] = Rid.Value(id), ["hidden"] = view.GetCategoryHidden(id) }));
            if (view is View3D v)
            {
                var o = v.GetOrientation(); state["eye"] = Vector(o.EyePosition); state["up"] = Vector(o.UpDirection); state["forward"] = Vector(o.ForwardDirection);
                state["section_active"] = v.IsSectionBoxActive; state["section_box"] = Box(v.GetSectionBox());
            }
            return state;
        }
        private static bool SameViewState(JToken a, JToken b)
        {
            if (a.Type != b.Type) return false;
            if (a is JObject ao && b is JObject bo)
                return ao.Count == bo.Count && ao.Properties().All(p => bo[p.Name] != null && SameViewState(p.Value, bo[p.Name]));
            if (a is JArray aa && b is JArray ba)
                return aa.Count == ba.Count && aa.Zip(ba, SameViewState).All(x => x);
            if (a.Type == JTokenType.Float || a.Type == JTokenType.Integer)
                return Math.Abs(a.Value<double>() - b.Value<double>()) < 1e-8;
            return JToken.DeepEquals(a, b);
        }
        private sealed class CaptureRestoreState
        {
            private readonly bool _cropActive, _cropVisible, _sectionActive;
            private readonly DisplayStyle _style;
            private readonly BoundingBoxXYZ _crop, _section;
            private readonly ViewOrientation3D _orientation;
            private readonly Dictionary<ElementId, bool> _categories;
            private readonly ElementId _template;
            public CaptureRestoreState(View view, IEnumerable<ElementId> ids)
            {
                _template = view.ViewTemplateId;
                _cropActive = view.CropBoxActive; _cropVisible = view.CropBoxVisible;
                _style = view.DisplayStyle; _crop = view.CropBox;
                _categories = ids.ToDictionary(x => x, x => view.GetCategoryHidden(x));
                if (view is View3D threeD)
                {
                    _orientation = threeD.GetOrientation();
                    _sectionActive = threeD.IsSectionBoxActive;
                    _section = threeD.GetSectionBox();
                }
            }
            public void Apply(View view)
            {
                if (_template != null && view.ViewTemplateId != _template) view.ViewTemplateId = _template;
                bool locked = view.ViewTemplateId != null && view.ViewTemplateId != ElementId.InvalidElementId;
                if (!locked)
                    foreach (var item in _categories)
                    if (view.GetCategoryHidden(item.Key) != item.Value) view.SetCategoryHidden(item.Key, item.Value);
                if (view.DisplayStyle != _style) view.DisplayStyle = _style;
                if (view is View3D threeD)
                {
                    threeD.SetOrientation(_orientation);
                    threeD.SetSectionBox(_section);
                    threeD.IsSectionBoxActive = _sectionActive;
                }
                view.CropBox = _crop;
                view.CropBoxActive = _cropActive;
                view.CropBoxVisible = _cropVisible;
            }
        }
        private static JObject CaptureSpatial(View view, int width, int height)
        {
            try
            {
                var result = new JObject
                {
                    ["coordinate_reference"] = "internal_origin",
                    ["units"] = "feet",
                    ["pixel_width"] = width,
                    ["pixel_height"] = height,
                    ["pixel_origin"] = "top_left",
                    ["crop_box"] = Box(view.CropBox),
                    ["view_right"] = Vector(view.RightDirection),
                    ["view_up"] = Vector(view.UpDirection),
                    ["view_direction"] = Vector(view.ViewDirection)
                };
                bool perspective = view is View3D v && v.IsPerspective;
                result["projection"] = perspective ? "perspective" : "orthographic";
                result["display_style"] = view.DisplayStyle.ToString();
                // The camera is what the image shows. A 3D view with no stated eye cannot be
                // told apart from "the default isometric", which is how a ground-level camera
                // that was never applied went unnoticed.
                if (view is View3D threeD)
                {
                    ViewOrientation3D o = threeD.GetOrientation();
                    result["camera"] = View3DRequestRules.Describe(new[] { o.EyePosition.X, o.EyePosition.Y, o.EyePosition.Z },
                        new[] { o.ForwardDirection.X, o.ForwardDirection.Y, o.ForwardDirection.Z },
                        new[] { o.UpDirection.X, o.UpDirection.Y, o.UpDirection.Z },
                        threeD.IsPerspective, threeD.IsLocked, threeD.IsSectionBoxActive, view.DisplayStyle.ToString());
                }
                result["world_to_pixel_verified"] = false;
                result["world_to_pixel"] = null;
                result["calibration_status"] = perspective ? "requires_perspective_projection" : "requires_export_pixel_calibration";
                result["note"] = "Crop and view axes are model measurements. Export margins and annotation extents are not pixel-calibrated, so no exact affine transform or global pixels-per-unit is claimed.";
                return result;
            }
            catch (Exception ex) { return new JObject { ["world_to_pixel_verified"] = false, ["error"] = ex.Message }; }
        }
    }
}
