using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class AppearanceColourRules
    {
        private static readonly Dictionary<string, string[]> Names = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Generic"] = new[] { "generic_diffuse" },
            ["Ceramic"] = new[] { "ceramic_color" },
            ["Concrete"] = new[] { "concrete_color" },
            ["Metal"] = new[] { "metal_color" },
            ["MetallicPaint"] = new[] { "metallicpaint_base_color" },
            ["WallPaint"] = new[] { "wallpaint_color" },
            ["Plastic"] = new[] { "plasticvinyl_color" },
            ["Masonry"] = new[] { "masonrycmu_color" },
            ["Stone"] = new[] { "stone_color" },
            ["Glazing"] = new[] { "glazing_transmittance_map", "glazing_transmittance_color" },
            ["SolidGlass"] = new[] { "solidglass_transmittance_custom_color" }
        };

        public static string[] Candidates(string schema)
        {
            if (schema == null) return Array.Empty<string>();
            string key = schema.EndsWith("Schema", StringComparison.OrdinalIgnoreCase)
                ? schema.Substring(0, schema.Length - 6) : schema;
            return Names.TryGetValue(key, out var names) ? (string[])names.Clone() : Array.Empty<string>();
        }
    }
}
