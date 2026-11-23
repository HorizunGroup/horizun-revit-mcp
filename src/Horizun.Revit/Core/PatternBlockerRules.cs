// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. No Revit.
//
// WHICH OVERRIDES SWITCH A PATTERN OFF (4.2-N4, CT-P2-2e).
//
// A hatch that does not print is rarely a missing pattern; it is a setting somewhere that turns it
// off, and the person looking at the Visibility/Graphics dialog has to open six tabs to find it.
// This turns the facts read off an override (or off the view) into the list of reasons, each with
// the setting that causes it and what it does to the drawing. It decides nothing about the model:
// the reasons are conditions that hide or weaken a pattern, listed in the order they matter.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class OverrideFacts
    {
        public bool SurfaceForegroundPatternSet, SurfaceForegroundVisible, SurfaceForegroundColorSet;
        public bool CutForegroundPatternSet, CutForegroundVisible, CutForegroundColorSet;
        public bool SurfaceBackgroundPatternSet, SurfaceBackgroundVisible;
        public bool CutBackgroundPatternSet, CutBackgroundVisible;
        public bool Halftone;
        public int Transparency;
    }

    public sealed class ViewPatternFacts
    {
        /// <summary>Revit's DisplayStyle name: Wireframe, HLR, Shading, ShadingWithEdges, FlatColors, Rendering, Realistic, Raytrace.</summary>
        public string DisplayStyle;
        public bool OverridesAllowed = true;
        public string TemplateName;
        public bool TemplateControlsGraphics;
    }

    public static class PatternBlockerRules
    {
        private static JObject Reason(string code, string setting, string effect) =>
            new JObject { ["code"] = code, ["setting"] = setting, ["effect"] = effect };

        /// <summary>The reasons an element's or category's override hides or weakens a pattern. Empty: nothing in the override does.</summary>
        public static List<JObject> Explain(OverrideFacts f)
        {
            var reasons = new List<JObject>();
            if (f == null) return reasons;
            if (f.Transparency >= 100)
                reasons.Add(Reason("transparency_full", "Surface transparency = " + f.Transparency, "the surface, and every pattern on it, is not drawn"));
            else if (f.Transparency > 0)
                reasons.Add(Reason("transparency", "Surface transparency = " + f.Transparency, "the pattern prints washed out in proportion to the transparency"));
            if (f.SurfaceForegroundPatternSet && !f.SurfaceForegroundVisible)
                reasons.Add(Reason("surface_foreground_off", "Surface > Foreground pattern: Visible is off", "the surface pattern is set but not drawn"));
            if (f.CutForegroundPatternSet && !f.CutForegroundVisible)
                reasons.Add(Reason("cut_foreground_off", "Cut > Foreground pattern: Visible is off", "the cut pattern is set but not drawn"));
            if (f.SurfaceBackgroundPatternSet && !f.SurfaceBackgroundVisible)
                reasons.Add(Reason("surface_background_off", "Surface > Background pattern: Visible is off", "the background pattern is set but not drawn"));
            if (f.CutBackgroundPatternSet && !f.CutBackgroundVisible)
                reasons.Add(Reason("cut_background_off", "Cut > Background pattern: Visible is off", "the cut background pattern is set but not drawn"));
            if (f.SurfaceForegroundColorSet && !f.SurfaceForegroundPatternSet)
                reasons.Add(Reason("colour_on_no_pattern", "Surface foreground colour set with no pattern", "a colour on no pattern paints nothing: pick a pattern (solid for a flat fill)"));
            if (f.CutForegroundColorSet && !f.CutForegroundPatternSet)
                reasons.Add(Reason("cut_colour_on_no_pattern", "Cut foreground colour set with no pattern", "a colour on no pattern paints nothing: pick a pattern"));
            if (f.Halftone)
                reasons.Add(Reason("halftone", "Halftone is on", "lines and patterns print at half intensity"));
            return reasons;
        }

        /// <summary>The reasons the VIEW itself keeps a pattern from showing or an override from taking effect.</summary>
        public static List<JObject> ExplainView(ViewPatternFacts v)
        {
            var reasons = new List<JObject>();
            if (v == null) return reasons;
            string style = (v.DisplayStyle ?? "").ToLowerInvariant();
            if (style == "wireframe")
                reasons.Add(Reason("view_wireframe", "Visual style = Wireframe", "surface patterns are not drawn in a wireframe view"));
            else if (style == "hlr")
                reasons.Add(Reason("view_hidden_line", "Visual style = Hidden line", "surface foreground patterns are drawn, but a surface colour/transparency is ignored"));
            if (!v.OverridesAllowed)
                reasons.Add(Reason("view_overrides_not_allowed", "this view type takes no graphic overrides", "an override set on it has no effect"));
            if (v.TemplateControlsGraphics)
                reasons.Add(Reason("view_template_controls_graphics", "View template '" + v.TemplateName + "' governs Visibility/Graphics", "overrides set on the view are ignored; change them in the template"));
            return reasons;
        }
    }
}
