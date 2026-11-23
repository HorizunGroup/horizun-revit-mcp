using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        internal static bool IsGraphicDisplayOperation(string op) => string.Equals(op, "set_graphic_display", StringComparison.OrdinalIgnoreCase);
        private static JObject GraphicRequest(JObject a)
        {
            var d = a["display"] as JObject ?? throw new ArgumentException("display must be an object");
            foreach (var p in d.Properties())
                if (p.Name != "style" && p.Name != "shadows" && p.Name != "ambient_shadows" && p.Name != "smooth_edges" && p.Name != "background")
                    throw new ArgumentException("display." + p.Name + " is not supported; use set_sun_study for sun settings");
            GraphicDisplayRules.Validate(d);
            if (d["shadows"] != null) throw new ArgumentException("display.shadows cannot be set: the Revit API has no shadow setting for a view (ViewDisplayModel.ShadowsCast is absent in 2023-2027). Turn shadows on in the view template and apply it");
            if (d["ambient_shadows"] != null) throw new ArgumentException("display.ambient_shadows cannot be set: the Revit API has no ambient shadow setting for a view (ViewDisplayModel.AmbientOcclusion is absent in 2023-2027). Turn them on in the view template and apply it");
            if ((string)d["background"]?["type"] == "none") throw new ArgumentException("display.background.type none is unavailable: this Revit API has no factory for a None background");
            if (d["background"] is JObject bg) foreach (var p in bg.Properties()) if (p.Name != "type" && p.Name != "sky_color" && p.Name != "horizon_color" && p.Name != "ground_color") throw new ArgumentException("display.background." + p.Name + " is not supported");
            return d;
        }
        private static DisplayStyle GraphicStyle(string s)
        {
            switch (s) { case "wireframe": return DisplayStyle.Wireframe; case "hidden": return DisplayStyle.HLR; case "shaded": return DisplayStyle.Shading; case "consistent_colors": return DisplayStyle.FlatColors; case "realistic": return DisplayStyle.Realistic; default: throw new ArgumentException("unknown style"); }
        }
        private static void CheckGraphicView(Document doc, View view, JObject d)
        {
            if (d["shadows"] != null) throw new ArgumentException("display.shadows is unavailable in this Revit API version (ViewDisplayModel.ShadowsCast is absent)");
            if (d["ambient_shadows"] != null) throw new ArgumentException("display.ambient_shadows is unavailable in this Revit API version (ViewDisplayModel.AmbientOcclusion is absent)");
            if (d["background"] != null && !(view is View3D)) throw new ArgumentException("display.background requires a 3D view; view '" + view.Name + "' is not 3D");
            if ((string)d["background"]?["type"] == "none") throw new ArgumentException("display.background.type none is unavailable: this Revit API has no factory for a None background");
            if (view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View template)
            {
                var ids = new HashSet<ElementId>(template.GetTemplateParameterIds());
                ids.ExceptWith(template.GetNonControlledTemplateParameterIds());
                var parameters = new List<BuiltInParameter>();
                if (d["style"] != null) parameters.Add(BuiltInParameter.MODEL_GRAPHICS_STYLE);
                if (d["smooth_edges"] != null) parameters.Add(BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_MODEL);
                if (d["background"] != null) parameters.Add(BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_BACKGROUND);
                parameters.Add(BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS);
                foreach (var p in parameters) if (ids.Contains(new ElementId(p)))
                    throw new ArgumentException("view template '" + template.Name + "' controls " + p + "; graphic display cannot be changed");
            }
            if (d["smooth_edges"] != null && view.GetViewDisplayModel() == null) throw new ArgumentException("display.smooth_edges is unavailable on view '" + view.Name + "'");
        }
        internal static void ValidateGraphicDisplay(Document doc, JObject a, Dictionary<string, Type> known)
        {
            var d = GraphicRequest(a);
            Reference<View>(doc, a, "view_id", "view_key", known);
            if (string.IsNullOrWhiteSpace(a.Value<string>("view_key"))) CheckGraphicView(doc, Need<View>(doc, a, "view_id"), d);
        }
        private static string Hex(Color c) => string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", c.Red, c.Green, c.Blue);
        private static Color GraphicColor(string s) => new Color(byte.Parse(s.Substring(1, 2), NumberStyles.HexNumber), byte.Parse(s.Substring(3, 2), NumberStyles.HexNumber), byte.Parse(s.Substring(5, 2), NumberStyles.HexNumber));
        private static JObject GraphicState(View view, JObject d)
        {
            var state = new JObject();
            if (d["style"] != null) state["style"] = view.DisplayStyle.ToString();
            if (d["smooth_edges"] != null) state["smooth_edges"] = view.GetViewDisplayModel().SmoothEdges;
            if (d["background"] != null)
            {
                var bg = view.GetBackground();
                var b = new JObject { ["type"] = bg.Type.ToString() };
                if (bg.Type == ViewDisplayBackgroundType.Gradient) { b["sky_color"] = Hex(bg.SkyColor); b["horizon_color"] = Hex(bg.BackgroundColor); b["ground_color"] = Hex(bg.GroundColor); }
                state["background"] = b;
            }
            return state;
        }
        internal static JObject GraphicDisplayPreview(Document doc, JObject a)
        {
            var d = GraphicRequest(a);
            var result = new JObject { ["requested"] = d.DeepClone() };
            long? id = a.Value<long?>("view_id");
            if (id.HasValue && Rid.CanRepresent(id.Value) && doc.GetElement(Rid.Make(id.Value)) is View view) result["current"] = GraphicState(view, d);
            return result;
        }
        internal static void BindGraphicDisplay(Document doc, JObject a, IDictionary<string, string> before)
        {
            long? id = a.Value<long?>("view_id");
            if (id.HasValue && Rid.CanRepresent(id.Value) && doc.GetElement(Rid.Make(id.Value)) is View view)
            {
                before["graphic_display"] = GraphicState(view, GraphicRequest(a)).ToString(Newtonsoft.Json.Formatting.None);
                before["graphic_template"] = Rid.Value(view.ViewTemplateId).ToString(CultureInfo.InvariantCulture);
            }
        }
        internal static Element ApplyGraphicDisplay(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            var d = GraphicRequest(a);
            var view = Resolve<View>(doc, a, "view_id", "view_key", aliases);
            CheckGraphicView(doc, view, d);
            var before = GraphicState(view, d);
            if (d["style"] != null) view.DisplayStyle = GraphicStyle((string)d["style"]);
            if (d["smooth_edges"] != null) { var model = view.GetViewDisplayModel(); model.SmoothEdges = (bool)d["smooth_edges"]; view.SetViewDisplayModel(model); }
            if (d["background"] is JObject bg)
                view.SetBackground((string)bg["type"] == "sky" ? ViewDisplayBackground.CreateSky() : ViewDisplayBackground.CreateGradient(GraphicColor((string)bg["sky_color"]), GraphicColor((string)bg["horizon_color"]), GraphicColor((string)bg["ground_color"])));
            a["__graphic_display"] = new JObject { ["view_id"] = Rid.Value(view.Id), ["before"] = before };
            return view;
        }
        internal static bool VerifyGraphicDisplay(Document doc, JObject a, Element e)
        {
            if (!(a["__graphic_display"] is JObject detail) || !(e is View view)) return false;
            try
            {
                var d = GraphicRequest(a); var now = GraphicState(view, d); bool ok = true;
                if (d["style"] != null) ok &= view.DisplayStyle == GraphicStyle((string)d["style"]);
                if (d["smooth_edges"] != null) ok &= (bool)now["smooth_edges"] == (bool)d["smooth_edges"];
                if (d["background"] is JObject bg)
                {
                    var b = (JObject)now["background"];
                    ok &= (string)b["type"] == ((string)bg["type"] == "sky" ? "SunAndClouds" : "Gradient");
                    if ((string)bg["type"] == "gradient") foreach (string field in new[] { "sky_color", "horizon_color", "ground_color" }) ok &= string.Equals((string)b[field], (string)bg[field], StringComparison.OrdinalIgnoreCase);
                }
                detail["reread"] = now; detail["verified"] = ok; return ok;
            }
            catch (Exception ex) { detail["verified"] = false; detail["reread_error"] = ex.Message; return false; }
        }
        internal static JObject GraphicDisplayDetail(JObject a) => a["__graphic_display"] as JObject;
    }
}
