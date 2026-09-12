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
        private static readonly string[] TemporaryFields = { "element_ids", "margin_ratio", "hide_category_ids", "hide_annotations", "display_style", "orientation", "calibrate_world_to_pixel" };
        private static bool HasTemporaryOptions(JObject request) => TemporaryFields.Any(f => request[f] != null);
        private CommandResult CaptureTemporary(UIApplication app, JObject request, View view)
        {
            var gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            var categoryIds = new HashSet<ElementId>();
            JObject before; double margin;
            try
            {
                if (request["calibrate_world_to_pixel"] != null && request["calibrate_world_to_pixel"].Type != JTokenType.Boolean) throw new ArgumentException("calibrate_world_to_pixel must be boolean.");
                if (request.Value<bool?>("calibrate_world_to_pixel") == true && request.Value<bool?>("hide_annotations") != true) throw new ArgumentException("Pixel calibration requires hide_annotations=true to keep annotation extents out of the export.");
                if (request.Value<bool?>("calibrate_world_to_pixel") == true && (!(view is ViewPlan) && !(view is ViewSection))) throw new ArgumentException("Pixel calibration supports plans and sections only.");
                if (request.Value<bool?>("calibrate_world_to_pixel") == true && (request.Value<int?>("pixel_size") ?? 1600) > 4096) throw new ArgumentException("Pixel calibration supports pixel_size up to 4096.");
                margin = request["margin_ratio"] == null ? 0.08 : GeometryInput.Number(request["margin_ratio"], "margin_ratio");
                if (margin < 0 || margin > 1) throw new ArgumentException("margin_ratio must be in [0,1].");
                if (request["margin_ratio"] != null && request["element_ids"] == null) throw new ArgumentException("margin_ratio requires element_ids.");
                if (request["hide_category_ids"] != null)
                {
                    if (!(request["hide_category_ids"] is JArray categories) || categories.Count > 256) throw new ArgumentException("hide_category_ids must have at most 256 IDs.");
                    foreach (var id in categories)
                    {
                        if (id.Type != JTokenType.Integer || !Rid.CanRepresent(id.Value<long>())) throw new ArgumentException("Category IDs must be integers.");
                        var categoryId = Rid.Make(id.Value<long>());
                        if (Category.GetCategory(doc, categoryId) == null || !view.CanCategoryBeHidden(categoryId)) throw new ArgumentException("Category cannot be hidden in this view: " + id);
                        categoryIds.Add(categoryId);
                    }
                }
                if (request.Value<bool?>("hide_annotations") == true)
                    foreach (Category category in doc.Settings.Categories)
                        if (category.CategoryType == CategoryType.Annotation && view.CanCategoryBeHidden(category.Id)) categoryIds.Add(category.Id);
                before = ViewState(view, categoryIds);
            }
            catch (Exception ex) { return CommandResult.Fail("Invalid capture options: " + ex.Message); }
            CommandResult captured = null; string rollback = "not_attempted", error = null;
            using (var group = new TransactionGroup(doc, "Horizun: temporary capture"))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Capture group did not start.");
                    using (var tx = new Transaction(doc, "Horizun: frame capture"))
                    {
                        tx.Start();
                        foreach (var id in categoryIds) view.SetCategoryHidden(id, true);
                        if (request["display_style"] != null)
                        {
                            if (!Enum.TryParse(request.Value<string>("display_style"), false, out DisplayStyle style) || !Enum.IsDefined(typeof(DisplayStyle), style)) throw new ArgumentException("Invalid display_style.");
                            view.DisplayStyle = style;
                        }
                        if (request["orientation"] != null) Orient(view, request.Value<string>("orientation"));
                        if (request["element_ids"] != null) Frame(doc, view, request["element_ids"], margin);
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
            try { restored = rollback == "RolledBack" && JToken.DeepEquals(before, ViewState(view, categoryIds)); }
            catch (Exception ex) { error = (error ?? "") + " Readback: " + ex.Message; }
            if (error != null || !restored || captured == null || !captured.Success)
                return CommandResult.FailWithDetail(error ?? captured?.Error ?? "View restoration was not verified.", new JObject
                {
                    ["code"] = "temporary_capture_failed",
                    ["rollback_status"] = rollback,
                    ["view_restored"] = restored,
                    ["changes_applied"] = restored ? (JToken)false : null,
                    ["capture_data"] = captured?.Data == null ? null : JToken.FromObject(captured.Data)
                });
            var result = JObject.FromObject(captured.Data);
            result["view_restored"] = restored; result["rollback_status"] = rollback;
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
        private static void Frame(Document doc, View view, JToken token, double margin)
        {
            if (!(token is JArray ids) || ids.Count < 1 || ids.Count > 2000) throw new ArgumentException("element_ids must contain 1..2000 IDs.");
            var points = new List<XYZ>();
            foreach (var id in ids)
            {
                if (id.Type != JTokenType.Integer || !Rid.CanRepresent(id.Value<long>())) throw new ArgumentException("Invalid element ID.");
                var element = doc.GetElement(Rid.Make(id.Value<long>()));
                var box = element?.get_BoundingBox(null);
                if (box == null) throw new ArgumentException("Element has no model bounding box: " + id);
                points.AddRange(Corners(box));
            }
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
            var state = new JObject { ["crop_active"] = view.CropBoxActive, ["crop_visible"] = view.CropBoxVisible, ["display_style"] = view.DisplayStyle.ToString(), ["crop"] = Box(view.CropBox) };
            state["categories"] = new JArray(ids.OrderBy(Rid.Value).Select(id => new JObject { ["id"] = Rid.Value(id), ["hidden"] = view.GetCategoryHidden(id) }));
            if (view is View3D v)
            {
                var o = v.GetOrientation(); state["eye"] = Vector(o.EyePosition); state["up"] = Vector(o.UpDirection); state["forward"] = Vector(o.ForwardDirection);
                state["section_active"] = v.IsSectionBoxActive; state["section_box"] = Box(v.GetSectionBox());
            }
            return state;
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
