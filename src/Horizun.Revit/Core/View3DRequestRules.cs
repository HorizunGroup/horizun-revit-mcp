// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A 3D VIEW THAT SILENTLY IGNORED ITS CAMERA (field cycle, 2026-10-21).
//
// MEASURED: horizun_manage_views create_3d accepted eye / target / perspective, raised
// no error and produced the default isometric view - a request the caller believed
// was honoured. The camera arguments of this tool are start (the eye), end (the target)
// and up, exactly as create_perspective takes them; and the visual style (shaded,
// hidden line, wireframe...) had no argument at all.
//
// This file is the Revit-free half: which spellings are a camera the tool does not
// have (refused by name, with the spelling that works), which visual style a request
// means, and the honest description of the camera a view really has.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class View3DRequestRules
    {
        /// <summary>Camera spellings that look right and would be ignored; each maps to what the tool really takes.</summary>
        private static readonly Dictionary<string, string> Misnamed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eye"] = "start (the eye position)",
            ["eye_position"] = "start (the eye position)",
            ["target"] = "end (the point looked at)",
            ["target_position"] = "end (the point looked at)",
            ["camera"] = "start/end/up",
            ["perspective"] = "operation=create_perspective (a perspective camera is its own operation)",
            ["is_perspective"] = "operation=create_perspective (a perspective camera is its own operation)"
        };

        /// <summary>Null when every key is honoured; otherwise the refusal that names the spelling that works.</summary>
        public static string MisnamedCameraField(IEnumerable<string> keys)
        {
            if (keys == null) return null;
            var hits = keys.Where(k => Misnamed.ContainsKey(k)).Distinct().ToList();
            if (hits.Count == 0) return null;
            return "create_3d does not take " + string.Join(", ", hits) + " - accepting it would build the default isometric " +
                   "and say nothing. Use " + string.Join("; ", hits.Select(k => Misnamed[k]).Distinct()) +
                   ". Nothing was written.";
        }

        /// <summary>
        /// True when the action asks for a camera: any of start, end, up. start and end must
        /// come together (an eye needs a target and the other way round); up alone is refused.
        /// </summary>
        public static bool WantsCamera(bool hasStart, bool hasEnd, bool hasUp, out string error)
        {
            error = null;
            if (!hasStart && !hasEnd && !hasUp) return false;
            if (hasStart != hasEnd)
            {
                error = "a camera needs BOTH start (the eye) and end (the target); only " + (hasStart ? "start" : "end") + " was given.";
                return true;
            }
            if (!hasStart && hasUp)
            {
                error = "up orients a camera and needs start and end.";
                return true;
            }
            return true;
        }

        private static readonly Dictionary<string, string> Styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wireframe"] = "Wireframe",
            ["hlr"] = "HLR", ["hidden_line"] = "HLR", ["hidden"] = "HLR",
            ["shading"] = "Shading", ["shaded"] = "Shading",
            ["shadingwithedges"] = "ShadingWithEdges", ["shaded_with_edges"] = "ShadingWithEdges",
            ["flatcolors"] = "FlatColors", ["flat_colors"] = "FlatColors", ["consistent_colors"] = "FlatColors",
            ["realistic"] = "Realistic",
            ["realisticwithedges"] = "RealisticWithEdges", ["realistic_with_edges"] = "RealisticWithEdges"
        };

        /// <summary>The Revit DisplayStyle name a request means, or null with the list of accepted words.</summary>
        public static string ParseStyle(string raw, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (Styles.TryGetValue(raw.Trim(), out string name)) return name;
            error = "display_style '" + raw + "' is not one of: wireframe, hlr (hidden_line), shaded, shaded_with_edges, " +
                    "consistent_colors, realistic, realistic_with_edges.";
            return null;
        }

        /// <summary>Azimuth (degrees counter-clockwise from +X, 0..360) and pitch (degrees above the horizon) of a sight line.</summary>
        public static double[] Angles(double[] forward)
        {
            double az = Math.Atan2(forward[1], forward[0]) * 180.0 / Math.PI;
            if (az < 0) az += 360.0;
            double horizontal = Math.Sqrt(forward[0] * forward[0] + forward[1] * forward[1]);
            double pitch = Math.Atan2(forward[2], horizontal) * 180.0 / Math.PI;
            return new[] { Math.Round(az, 4), Math.Round(pitch, 4) };
        }

        /// <summary>What a 3D view's camera REALLY is, in internal feet: for captures and for the re-read after create.</summary>
        public static JObject Describe(double[] eye, double[] forward, double[] up, bool perspective, bool locked,
                                       bool sectionBoxActive, string displayStyle)
        {
            double[] angles = Angles(forward);
            return new JObject
            {
                ["eye_internal_feet"] = new JArray(eye),
                ["forward"] = new JArray(forward),
                ["up"] = new JArray(up),
                ["azimuth_degrees"] = angles[0],
                ["pitch_degrees"] = angles[1],
                ["projection"] = perspective ? "perspective" : "orthographic",
                ["orientation_locked"] = locked,
                ["section_box_active"] = sectionBoxActive,
                ["display_style"] = displayStyle
            };
        }
    }
}
