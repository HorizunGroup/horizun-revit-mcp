using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Input checks that do not depend on a running Revit.</summary>
    public static class GraphicDisplayRules
    {
        public static void Validate(JObject action)
        {
            if (action == null) throw new ArgumentException("action is required");
            string[] fields = { "style", "shadows", "ambient_shadows", "smooth_edges", "sun", "background" };
            if (!fields.Any(f => action[f] != null)) throw new ArgumentException("at least one graphic display setting is required");
            if (action["style"] != null && (action["style"].Type != JTokenType.String ||
                !new[] { "wireframe", "hidden", "shaded", "consistent_colors", "realistic" }.Contains((string)action["style"])))
                throw new ArgumentException("style must be wireframe, hidden, shaded, consistent_colors or realistic");
            foreach (string f in new[] { "shadows", "ambient_shadows", "smooth_edges" })
                if (action[f] != null && action[f].Type != JTokenType.Boolean) throw new ArgumentException(f + " must be a boolean");
            if (action["sun"] != null)
            {
                if (!(action["sun"] is JObject sun)) throw new ArgumentException("sun must be an object");
                string mode = sun.Value<string>("setting");
                if (mode != "lighting" && mode != "still") throw new ArgumentException("sun.setting must be lighting or still");
                foreach (string f in new[] { "azimuth_deg", "altitude_deg" })
                {
                    JToken value = sun[f];
                    if (value == null) continue;
                    if (mode != "lighting") throw new ArgumentException("sun." + f + " only applies to lighting");
                    if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float) throw new ArgumentException("sun." + f + " must be a number");
                    double n = (double)value;
                    if (double.IsNaN(n) || double.IsInfinity(n) || n < 0 || n > (f == "azimuth_deg" ? 360 : 90))
                        throw new ArgumentException("sun." + f + " is outside its allowed range");
                }
                if (sun["relative_to_view"] != null && (mode != "lighting" || sun["relative_to_view"].Type != JTokenType.Boolean))
                    throw new ArgumentException("sun.relative_to_view must be a boolean and only applies to lighting");
            }
            if (action["background"] != null)
            {
                if (!(action["background"] is JObject bg)) throw new ArgumentException("background must be an object");
                string type = bg.Value<string>("type");
                if (type != "sky" && type != "gradient" && type != "none") throw new ArgumentException("background.type must be sky, gradient or none");
                foreach (string f in new[] { "sky_color", "horizon_color", "ground_color" })
                {
                    if (type == "gradient" && (bg[f]?.Type != JTokenType.String || !System.Text.RegularExpressions.Regex.IsMatch((string)bg[f], "^#[0-9a-fA-F]{6}$")))
                        throw new ArgumentException("background." + f + " must be #RRGGBB for gradient");
                    if (type != "gradient" && bg[f] != null) throw new ArgumentException("background." + f + " only applies to gradient");
                }
            }
        }
    }
}
