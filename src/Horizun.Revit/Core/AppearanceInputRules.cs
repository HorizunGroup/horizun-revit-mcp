using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class AppearanceInputRules
    {
        public static string Validate(JObject action)
        {
            foreach (var prop in action.Properties())
                if (Array.IndexOf(new[] { "key", "operation", "material_id", "material_name", "generic_diffuse", "match_shading_color", "remove_diffuse_texture", "transparency", "replace_with_generic", "identity", "unique_id", "unique_ids", "expected_category", "expected_type" }, prop.Name) < 0)
                    return "set_appearance does not accept " + prop.Name;
            foreach (string name in new[] { "match_shading_color", "remove_diffuse_texture", "replace_with_generic" })
                if (action[name] != null && action[name].Type != JTokenType.Boolean) return name + " must be boolean";
            JToken color = action["generic_diffuse"];
            if (color != null)
            {
                var a = color as JArray;
                if (a == null || a.Count != 3) return "generic_diffuse must contain three integers in 0..255";
                foreach (JToken component in a)
                    if (component.Type != JTokenType.Integer || (long)component < 0 || (long)component > 255)
                        return "generic_diffuse must contain three integers in 0..255";
            }
            bool match = action.Value<bool?>("match_shading_color") == true;
            if (match && color != null) return "generic_diffuse and match_shading_color are mutually exclusive";
            JToken t = action["transparency"];
            if (t != null)
            {
                if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) return "transparency must be 0..1";
                double v = (double)t;
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0 || v > 1) return "transparency must be 0..1";
            }
            if (color == null && !match && action.Value<bool?>("remove_diffuse_texture") != true && t == null)
                return "set_appearance changes nothing";
            return null;
        }
    }
}
