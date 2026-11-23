// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_manage_views create_3d: the camera and the visual style it silently
// ignored, plus display_style for create_perspective.
//
//   * create_3d takes start (the eye), end (the target) and up - the same fields as
//     create_perspective - and the isometric view it makes is ORIENTED with them. A
//     camera spelled eye / target / perspective is refused by name (View3DRequestRules)
//     instead of building the default isometric and saying nothing.
//   * display_style (shaded, hlr, wireframe, ...) is set on the new view. A view whose
//     template controls the style refuses the setting: the action fails, it is not
//     reported as styled.
//   * After the commit the camera is re-read (GetOrientation vs the triple SENT) and the
//     style (View.DisplayStyle); either differing fails the action.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        private static bool Wants3DCamera(JObject a, out string error) =>
            View3DRequestRules.WantsCamera(a["start"] != null, a["end"] != null, a["up"] != null, out error);

        /// <summary>Validation shared by create_3d and create_perspective for the style; create_3d adds the camera rules.</summary>
        internal static void Validate3DView(JObject a, bool camera)
        {
            if (camera)
            {
                string misnamed = View3DRequestRules.MisnamedCameraField(a.Properties().Select(p => p.Name));
                if (misnamed != null) throw new ArgumentException(misnamed);
                if (a["fan"] != null) throw new ArgumentException("create_3d makes one view; fan belongs to create_perspective.");
                if (Wants3DCamera(a, out string error))
                {
                    if (error != null) throw new ArgumentException("create_3d: " + error);
                    PerspectiveCameras(a, UnitsOf(a)); // eye on target, up along the sight line: refused by name
                }
            }
            if (a["display_style"] != null)
            {
                View3DRequestRules.ParseStyle(a.Value<string>("display_style"), out string styleError);
                if (styleError != null) throw new ArgumentException(styleError);
            }
        }

        /// <summary>Orients a freshly made isometric and sets its style; records what was sent for the re-read.</summary>
        internal static void Apply3DView(View3D view, JObject a, double scale)
        {
            var sent = new JObject();
            if (Wants3DCamera(a, out _))
            {
                CameraTriple c = PerspectiveCameras(a, scale)[0];
                view.SetOrientation(new ViewOrientation3D(Xyz(c.Eye), Xyz(c.Up), Xyz(c.Forward)));
                sent["camera"] = CameraJson(c, view.Name);
            }
            Apply3DStyle(view, a, sent);
            if (sent.Count > 0) a["__view3d"] = sent;
        }

        internal static void Apply3DStyle(View3D view, JObject a, JObject sent)
        {
            string name = View3DRequestRules.ParseStyle(a.Value<string>("display_style"), out _);
            if (name == null) return;
            if (!Enum.TryParse(name, false, out DisplayStyle style) || !Enum.IsDefined(typeof(DisplayStyle), style))
                throw new InvalidOperationException("display_style '" + name + "' does not exist in this Revit version.");
            try { view.DisplayStyle = style; }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("display_style was not applied: " + ex.Message +
                    " (a view template may control it). Nothing was kept.");
            }
            sent["display_style"] = name;
        }

        internal static bool Verify3DView(Document doc, JObject a, Element e)
        {
            if (!(e is View3D view)) return false;
            if (!(a["__view3d"] is JObject sent)) return true;
            bool ok = true;
            ViewOrientation3D o = view.GetOrientation();
            var reread = View3DRequestRules.Describe(Arr3(o.EyePosition), Arr3(o.ForwardDirection), Arr3(o.UpDirection),
                view.IsPerspective, view.IsLocked, view.IsSectionBoxActive, view.DisplayStyle.ToString());
            if (sent["camera"] is JObject cam)
            {
                var want = new CameraTriple
                {
                    Eye = cam["eye_internal_feet"].ToObject<double[]>(),
                    Forward = cam["forward"].ToObject<double[]>(),
                    Up = cam["up"].ToObject<double[]>()
                };
                var got = new CameraTriple { Eye = Arr3(o.EyePosition), Forward = Arr3(o.ForwardDirection), Up = Arr3(o.UpDirection) };
                bool same = PerspectiveRules.SameOrientation(want, got);
                reread["camera_verified"] = same;
                if (!same) ok = false;
            }
            if (sent["display_style"] != null)
            {
                bool styled = string.Equals(view.DisplayStyle.ToString(), (string)sent["display_style"], StringComparison.Ordinal);
                reread["display_style_verified"] = styled;
                if (!styled) ok = false;
            }
            sent["reread"] = reread;
            sent["verified"] = ok;
            return ok;
        }

        internal static JObject View3DDetail(JObject a) => a["__view3d"] as JObject;
    }
}
