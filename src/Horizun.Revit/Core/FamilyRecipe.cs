using System;
using System.Linq;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Core
{
    // XY profile is fixed; only extrusion height is parameter-associated.
    public static class FamilyRecipe
    {
        public static JObject Expand(JObject request)
        {
            if (request["recipe"] == null) return request;
            var r = request["recipe"] as JObject ?? throw new ArgumentException("recipe must be an object.");
            if (r.Properties().Any(p => !new[] { "name", "width", "depth", "wall", "height_parameter", "types" }.Contains(p.Name))) throw new ArgumentException("Unknown recipe field.");
            string name = r.Value<string>("name");
            if (name != "rectangular_prism" && name != "rectangular_tube") throw new ArgumentException("Unknown family recipe.");
            foreach (string key in new[] { "parameters", "types", "forms", "dimensions", "reference_planes", "family_lines", "nested_instances", "connectors" })
                if (request[key] != null) throw new ArgumentException("Cannot mix recipe with raw " + key);
            foreach (string key in new[] { "flex", "emit_thumbnail" })
                if (request[key] != null && (request[key].Type != JTokenType.Boolean || !request.Value<bool>(key))) throw new ArgumentException("Recipes require " + key);
            double w = Positive(r["width"]), d = Positive(r["depth"]);
            string parameter = r["height_parameter"]?.Type == JTokenType.String ? (string)r["height_parameter"] : null;
            if (string.IsNullOrWhiteSpace(parameter)) throw new ArgumentException("Specify height_parameter.");
            var types = r["types"] as JArray;
            if (types == null || types.Count < 2 || types.Count > 50) throw new ArgumentException("Specify 2..50 types for flex.");
            var compiled = new JArray();
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var heights = new System.Collections.Generic.HashSet<double>();
            foreach (var token in types)
            {
                var t = token as JObject ?? throw new ArgumentException("Type must be an object.");
                if (t.Properties().Any(p => p.Name != "name" && p.Name != "height")) throw new ArgumentException("Type fields are name and height.");
                string n = t["name"]?.Type == JTokenType.String ? (string)t["name"] : null;
                if (string.IsNullOrWhiteSpace(n) || !names.Add(n)) throw new ArgumentException("Type names must be nonempty and unique.");
                double h = Positive(t["height"]); heights.Add(h);
                compiled.Add(new JObject { ["name"] = n, ["values"] = new JObject { [parameter] = h } });
            }
            if (heights.Count < 2) throw new ArgumentException("At least two heights must differ for flex.");
            var loops = new JArray { Rectangle(0, 0, w, d) };
            if (name == "rectangular_tube")
            {
                double wall = Positive(r["wall"]);
                if (2 * wall >= Math.Min(w, d)) throw new ArgumentException("wall must leave an internal opening.");
                loops.Add(new JArray(Rectangle(wall, wall, w - wall, d - wall).Reverse()));
            }
            else if (r["wall"] != null) throw new ArgumentException("wall is only valid for rectangular_tube.");
            var result = (JObject)request.DeepClone(); result.Remove("recipe");
            result["parameters"] = new JArray(new JObject { ["name"] = parameter, ["data_type"] = "length", ["group"] = "geometry", ["instance"] = false });
            result["types"] = compiled;
            result["forms"] = new JArray(new JObject { ["key"] = "body", ["kind"] = "extrusion", ["plane"] = "xy", ["profile"] = loops,
                ["depth"] = Positive(types[0]["height"]), ["end_parameter"] = parameter });
            result["flex"] = true; result["emit_thumbnail"] = true;
            return result;
        }
        static JArray Rectangle(double x, double y, double right, double top) => new JArray(
            new JArray(x, y, 0), new JArray(right, y, 0), new JArray(right, top, 0), new JArray(x, top, 0));
        static double Positive(JToken t)
        {
            if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)) throw new ArgumentException("Dimensions must be explicit numbers.");
            double v = t.Value<double>();
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) throw new ArgumentException("Dimensions must be finite and positive.");
            return v;
        }
    }
}
