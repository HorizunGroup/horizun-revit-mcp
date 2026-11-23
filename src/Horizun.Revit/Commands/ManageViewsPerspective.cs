// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_manage_views operation=create_perspective: a perspective 3D view (a
// camera) from an eye, a target and an optional up direction - or a FAN of N
// cameras from one eye, turned about world Z in equal steps - written as ONE
// action of the batch's single transaction.
//
//   * The camera is decided by the pure PerspectiveRules: eye/target/up become the
//     eye/forward/up triple a ViewOrientation3D takes, with up made perpendicular to
//     the line of sight. An eye on its target, an up along the sight line and a fan
//     looking straight up or down are refused by name in the rehearsal - never
//     nudged into a camera nobody asked for.
//   * eye and target are 'start' and 'end' in the request's units; 'up' is a
//     direction and has none. The rehearsal lists every camera it will create.
//   * After the commit each view is re-read: IsPerspective, the name it was given,
//     and GetOrientation compared with the triple SENT (SameOrientation), so a
//     camera Revit moved or refused reads as a failure, not as a view.
//   * A fan's key aliases its FIRST view; every view of the fan is in the row.
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
        internal static bool IsPerspectiveOperation(string op) => string.Equals(op, "create_perspective", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The batch's units, read from the request that holds this action. Validate runs
        /// per action without them, and the rehearsal must refuse a sight line shorter
        /// than 1 mm in the SAME units the apply will use - not 1 foot.
        /// </summary>
        private static double UnitsOf(JObject a)
        {
            string u = ((a.Parent?.Parent?.Parent as JObject)?.Value<string>("units") ?? "mm").ToLowerInvariant();
            return Scale(u, out double s) ? s : 1 / 304.8;
        }

        private static double[] Triple(JToken t, string field, double scale)
        {
            if (!(t is JArray arr) || arr.Count != 3 || arr.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                throw new ArgumentException("create_perspective needs " + field + " as [x, y, z] numbers.");
            return arr.Select(v => Finite(v.Value<double>(), field) * scale).ToArray();
        }

        /// <summary>The cameras an action asks for, in internal feet; PerspectiveRules refuses a degenerate one.</summary>
        private static IList<CameraTriple> PerspectiveCameras(JObject a, double scale)
        {
            double[] eye = Triple(a["start"], "start (the eye)", scale);
            double[] target = Triple(a["end"], "end (the target)", scale);
            double[] up = a["up"] == null || a["up"].Type == JTokenType.Null ? null : Triple(a["up"], "up (a direction)", 1.0);
            int fan = 1;
            if (a["fan"] != null && a["fan"].Type != JTokenType.Null)
            {
                if (a["fan"].Type != JTokenType.Integer)
                    throw new ArgumentException("fan must be an integer 1.." + PerspectiveRules.MaxFan + ".");
                fan = a.Value<int>("fan");
            }
            return PerspectiveRules.Fan(eye, target, fan, up);
        }

        /// <summary>
        /// A fan names each view by its azimuth: N names that are distinct (steps are at
        /// least 10 degrees, so whole degrees never collide) and say where each one looks.
        /// </summary>
        private static string PerspectiveName(string name, CameraTriple c, int count) => PerspectiveRules.ViewName(name, c, count);

        internal static void ValidatePerspective(Document doc, JObject a)
        {
            PerspectiveCameras(a, UnitsOf(a));
            Validate3DView(a, false);
            OptionalViewFamilyType(doc, a, ViewFamily.ThreeDimensional);
        }

        private static IEnumerable<string> PerspectiveViewNames(JObject action)
        {
            IList<CameraTriple> cameras = PerspectiveCameras(action, UnitsOf(action));
            string name = action.Value<string>("name");
            return cameras.Select(camera => PerspectiveName(name, camera, cameras.Count));
        }

        private static JArray Arr3(double[] v) => new JArray(v[0], v[1], v[2]);
        private static double[] Arr3(XYZ p) => new[] { p.X, p.Y, p.Z };
        private static XYZ Xyz(double[] v) => new XYZ(v[0], v[1], v[2]);

        private static JObject CameraJson(CameraTriple c, string name) => new JObject
        {
            ["name"] = name,
            ["azimuth_degrees"] = c.AzimuthDegrees,
            ["pitch_degrees"] = c.PitchDegrees,
            ["eye_internal_feet"] = Arr3(c.Eye),
            ["forward"] = Arr3(c.Forward),
            ["up"] = Arr3(c.Up)
        };

        /// <summary>The rehearsal's list of cameras: what will be created, before anything is.</summary>
        internal static JObject PerspectivePreview(JObject a, double scale)
        {
            IList<CameraTriple> cams = PerspectiveCameras(a, scale);
            string name = a.Value<string>("name");
            return new JObject
            {
                ["views_to_create"] = cams.Count,
                ["cameras"] = new JArray(cams.Select(c => CameraJson(c, PerspectiveName(name, c, cams.Count))))
            };
        }

        internal static Element ApplyPerspective(Document doc, JObject a, double scale)
        {
            IList<CameraTriple> cams = PerspectiveCameras(a, scale);
            ElementId typeId = ResolveVft(doc, a, ViewFamily.ThreeDimensional).Id;
            string name = a.Value<string>("name");
            var made = new JArray();
            var styleSent = new JObject();
            View3D first = null;
            foreach (CameraTriple c in cams)
            {
                View3D view = View3D.CreatePerspective(doc, typeId);
                // ViewOrientation3D takes (eye, up, forward) - in that order.
                view.SetOrientation(new ViewOrientation3D(Xyz(c.Eye), Xyz(c.Up), Xyz(c.Forward)));
                Apply3DStyle(view, a, styleSent);
                string wanted = PerspectiveName(name, c, cams.Count);
                if (wanted != null) view.Name = wanted;
                JObject row = CameraJson(c, wanted);
                row["view_id"] = Rid.Value(view.Id);
                made.Add(row);
                if (first == null) first = view;
            }
            a["__perspective"] = made;
            if (styleSent["display_style"] != null) a["__perspective_style"] = styleSent["display_style"];
            return first;
        }

        /// <summary>
        /// Every camera re-reads as a perspective with the name it was given and the
        /// orientation SENT: the eye within 1e-6 ft, each direction within 1e-4 degrees.
        /// </summary>
        internal static bool VerifyPerspective(Document doc, JObject a, Element e)
        {
            if (!(e is View3D) || !(a["__perspective"] is JArray made) || made.Count == 0) return false;
            bool ok = true;
            foreach (JObject row in made.Cast<JObject>())
            {
                bool rowOk = false;
                try
                {
                    if (doc.GetElement(Rid.Make(row.Value<long>("view_id"))) is View3D view)
                    {
                        ViewOrientation3D o = view.GetOrientation();
                        var reread = new CameraTriple { Eye = Arr3(o.EyePosition), Forward = Arr3(o.ForwardDirection), Up = Arr3(o.UpDirection) };
                        var sent = new CameraTriple
                        {
                            Eye = row["eye_internal_feet"].ToObject<double[]>(),
                            Forward = row["forward"].ToObject<double[]>(),
                            Up = row["up"].ToObject<double[]>()
                        };
                        bool same = PerspectiveRules.SameOrientation(sent, reread);
                        string wanted = row.Value<string>("name");
                        bool named = string.IsNullOrEmpty(wanted) || string.Equals(view.Name, wanted, StringComparison.Ordinal);
                        bool perspective = view.IsPerspective;
                        bool styled = a["__perspective_style"] == null || string.Equals(view.DisplayStyle.ToString(), (string)a["__perspective_style"], StringComparison.Ordinal);
                        row["reread"] = new JObject
                        {
                            ["eye_internal_feet"] = Arr3(reread.Eye), ["forward"] = Arr3(reread.Forward), ["up"] = Arr3(reread.Up),
                            ["name"] = view.Name, ["is_perspective"] = perspective
                        };
                        row["orientation_verified"] = same;
                        row["display_style_verified"] = styled;
                        rowOk = perspective && same && named && styled;
                    }
                    else row["reread"] = JValue.CreateNull();
                }
                catch (Exception ex) { row["reread_error"] = ex.Message; }
                row["verified"] = rowOk;
                if (!rowOk) ok = false;
            }
            return ok;
        }

        internal static JObject PerspectiveDetail(JObject a) => new JObject { ["views"] = a["__perspective"] };
    }
}
