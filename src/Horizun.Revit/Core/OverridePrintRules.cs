// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WILL THIS OVERRIDE PRINT? (P5, field report 2026-11-17, Revit 2024, cloud workshared model)
//
// MEASURED in the field: 791 element overrides were written and re-read as "verified"; about a
// third reached the paper. Revit stored every one of them; the paper showed something else:
//
//   - a cut/surface COLOUR with no PATTERN paints nothing, and the colour still re-reads;
//   - a view filter (or the template's) with halftone turned a gray 90 into 200: an element
//     override cannot switch halftone off (false means "no override"), so the filter's wins;
//   - a SURFACE override on a FilledRegion never prints (its fill is the region type's);
//   - a CUT override on an element that this view only shows in projection draws no pixel;
//   - geometry inside families (stair nosings, in-family sweeps) kept its own graphics.
//
// This file turns the facts read off the view into one verdict per element, BEFORE the write:
// prints / partial / masked / will_not_print, each reason with the setting that causes it.
// It predicts from settings; it does not measure ink (that is the PDF check's job).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>A layer that also paints the element in this view: a filter that applies, or its category row.</summary>
    public sealed class PrintLayer
    {
        public string Label;
        public bool Hides;
        public bool Halftone;
        public int Transparency;
        public bool FromTemplate;
        /// <summary>The element's own override already in the view: the write MERGES into it, so halftone=false or a transparency sent now replaces it.</summary>
        public bool Own;
    }

    public sealed class PrintFacts
    {
        /// <summary>False: the view does not draw the element (other level, outside range/crop, other phase...); null: not known.</summary>
        public bool? InView = true;
        public bool HiddenInView;
        /// <summary>The workset hidden in this view, or null.</summary>
        public string HiddenWorkset;
        /// <summary>Phase presentation of the element in this view: "not_displayed", "overridden", "by_category"; "unreadable"; null: no phase.</summary>
        public string Phase;
        public string PhaseStatus;
        /// <summary>"halftone": the element lies on the view's underlay level (drawn halftone); "unknown": the view has an underlay and the element's level is not readable.</summary>
        public string Underlay;
        public bool IsFilledRegion;
        /// <summary>True: the view cuts the element; false: projection only; null: not known.</summary>
        public bool? Cut;
        /// <summary>Subcategories of geometry nested in the family (nosings, sweeps); "(not overridable)" marks those SetCategoryOverrides refuses.</summary>
        public List<string> NestedGeometry = new List<string>();
        public List<PrintLayer> Layers = new List<PrintLayer>();
    }

    public static class OverridePrintRules
    {
        public const string Prints = "prints", Partial = "partial", Masked = "masked", WillNotPrint = "will_not_print";
        public const string PolicyRefuse = "refuse", PolicySkip = "skip", PolicyWrite = "write";
        public static readonly string[] Policies = { PolicyRefuse, PolicySkip, PolicyWrite };

        private static readonly string[] SurfaceKeys = { "surface_color", "surface_pattern" };
        private static readonly string[] CutKeys = { "cut_color", "cut_pattern", "cut_line_color" };

        private static int Rank(string v) => v == WillNotPrint ? 3 : v == Masked ? 2 : v == Partial ? 1 : 0;

        /// <summary>A verdict that stops a default write (unprinted=refuse).</summary>
        public static bool Blocks(string verdict) => verdict == WillNotPrint || verdict == Masked;

        /// <summary>
        /// A fill colour with no pattern paints on the solid drafting fill (the bridge's default, as horizun_manage_views);
        /// said in the reply as pattern_defaulted. A pattern with no colour is valid (it keeps the element's colour).
        /// Returns { "cut_pattern": "solid", ... } for the patterns that will be defaulted; empty when none.
        /// </summary>
        public static JObject PatternDefaults(JObject overrides)
        {
            var o = new JObject();
            if (overrides == null) return o;
            foreach (var pair in new[] { new[] { "surface_color", "surface_pattern" }, new[] { "cut_color", "cut_pattern" } })
                if (!string.IsNullOrWhiteSpace(overrides.Value<string>(pair[0])) && string.IsNullOrWhiteSpace(overrides.Value<string>(pair[1])))
                    o[pair[1]] = OverridePatternRules.Solid;
            return o;
        }

        /// <summary>The verdict for one element: { verdict, reasons:[{code, setting, effect}] }.</summary>
        public static JObject Predict(JObject requested, PrintFacts f)
        {
            requested = requested ?? new JObject();
            f = f ?? new PrintFacts();
            var reasons = new JArray();
            string verdict = Prints;
            void Raise(string v, string code, string setting, string effect)
            {
                if (Rank(v) > Rank(verdict)) verdict = v;
                reasons.Add(new JObject { ["code"] = code, ["setting"] = setting, ["effect"] = effect, ["verdict"] = v });
            }
            var keys = requested.Properties().Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal) && p.Value.Type != JTokenType.Null)
                .Select(p => p.Name).ToList();
            bool surface = keys.Any(k => SurfaceKeys.Contains(k));
            bool cut = keys.Any(k => CutKeys.Contains(k));
            bool onlySurface = surface && keys.All(k => SurfaceKeys.Contains(k) || k == "transparency");
            bool onlyCut = cut && keys.All(k => CutKeys.Contains(k));
            bool? halftone = requested.Value<bool?>("halftone");

            if (f.InView == false)
                Raise(WillNotPrint, "not_in_view", "the view does not draw this element", "an override on an element the view does not show prints nothing");
            if (f.HiddenInView)
                Raise(WillNotPrint, "hidden_in_view", "the element is hidden in this view (Hide in View)", "nothing prints until it is unhidden");
            if (!string.IsNullOrEmpty(f.HiddenWorkset))
                Raise(WillNotPrint, "workset_hidden", "workset '" + f.HiddenWorkset + "' is hidden in this view", "nothing prints until the workset is shown");
            if (f.Phase == "not_displayed")
                Raise(WillNotPrint, "phase_not_displayed", "the phase filter does not show " + (f.PhaseStatus ?? "this phase status"), "nothing prints");
            else if (f.Phase == "overridden")
            {
                // Existing under "Show Previous + New", Demolished...: the phase graphics paint it, and they are not readable
                // through the API. A colour or pattern asked for cannot be promised: masked, so unprinted=refuse stops it.
                bool paints = surface || cut || keys.Any(k => k == "line_color" || k == "projection_line_color" || k == "line_pattern");
                Raise(paints ? Masked : Partial, "phase_graphics", "the phase filter draws " + (f.PhaseStatus ?? "this status") + " with the phase graphic overrides",
                    "those overrides (often halftone for Existing) are not readable through the API" + (paints ? ", so the colour or pattern asked for cannot be promised" : "; check the print"));
            }
            else if (f.Phase == "unreadable")
                Raise(Partial, "phase_unreadable", "the element's phase status in this view could not be read", "phase graphics may change how it prints");
            if (f.Underlay == "halftone")
                Raise(Masked, "underlay_halftone", "the element is on the view's underlay level", "underlay elements print halftone");
            else if (f.Underlay == "unknown")
                Raise(Partial, "underlay_unknown", "the view has an underlay and the element's level is not readable", "if it is underlay it prints halftone");
            if (f.IsFilledRegion && surface)
                Raise(onlySurface ? WillNotPrint : Partial, "filled_region_surface", "surface override on a filled region",
                    "never prints: a filled region draws the fill of its FilledRegionType; change the type (or use a line override)");
            if (f.Cut == false && cut)
                Raise(onlyCut ? WillNotPrint : Partial, "projection_only", "cut override on an element this view does not cut",
                    "no pixel: the element is only seen in projection here; use surface_color/surface_pattern");
            if (f.Cut == true && surface && !cut)
                Raise(Partial, "cut_faces", "surface override on an element this view cuts",
                    "the cut faces print the CUT pattern; add cut_color/cut_pattern for them");
            if (f.NestedGeometry != null && f.NestedGeometry.Count > 0 && (surface || cut))
                Raise(Partial, "nested_family_geometry", "geometry nested in the family: " + string.Join(", ", f.NestedGeometry),
                    "may keep its own graphics (field: stair nosings, in-family sweeps); check the print");
            foreach (PrintLayer l in f.Layers ?? new List<PrintLayer>())
            {
                if (l == null) continue;
                string where = l.Label + (l.FromTemplate ? " (from the view template)" : "");
                if (l.Own)
                {
                    // The write merges into the element's current override: only what is not resent survives.
                    if (l.Halftone && halftone == null)
                        Raise(Masked, "halftone_already_on_element", where + " already has halftone", "it is kept by this write; send halftone=false to switch it off");
                    if (l.Transparency > 0 && surface && requested["transparency"] == null)
                        Raise(Masked, "transparency_already_on_element", where + " already has transparency " + l.Transparency, "it is kept by this write; send transparency to change it");
                    continue;
                }
                if (l.Hides)
                {
                    Raise(WillNotPrint, "hidden_by_layer", where + " hides the element", "nothing prints in this view");
                    continue;
                }
                if (l.Halftone && halftone != true)
                    Raise(Masked, "halftone_from_layer", where + " sets halftone",
                        "an element override cannot switch halftone off, so every colour prints lighter (field: gray 90 printed as 200)");
                if (l.Transparency > 0 && surface && requested["transparency"] == null)
                    Raise(Masked, "transparency_from_layer", where + " sets transparency " + l.Transparency,
                        "the surface pattern prints washed out; send transparency to set it on the element");
            }
            return new JObject { ["verdict"] = verdict, ["reasons"] = reasons };
        }

        /// <summary>Counts per verdict over many predictions.</summary>
        public static JObject Count(IEnumerable<string> verdicts)
        {
            var o = new JObject { [Prints] = 0, [Partial] = 0, [Masked] = 0, [WillNotPrint] = 0 };
            foreach (string v in verdicts ?? Enumerable.Empty<string>())
                if (v != null && o[v] != null) o[v] = o.Value<int>(v) + 1;
            return o;
        }

        /// <summary>The refusal text when unprinted=refuse meets elements that will not print or will print masked.</summary>
        public static string Refusal(int blocked, int total, JObject counts) =>
            blocked + " of " + total + " element(s) would be written and re-read but would not print as asked (" +
            counts.Value<int>(WillNotPrint) + " will_not_print, " + counts.Value<int>(Masked) + " masked; see print_check). " +
            "Fix the cause, or send unprinted=skip (write only what prints) or unprinted=write (write all, reported). Nothing was written.";

        /// <summary>
        /// The category row behind an element override (method change): the fields it still paints. Empty when the
        /// row is clear. <paramref name="fields"/> are the fields the category override SETS.
        /// </summary>
        /// <summary>
        /// The underlay verdict of a model element in a plan with an underlay (ids as longs, null = none / unreadable).
        /// Judged whether or not the view's collector returns the element (review round 2): "halftone" when it lies on
        /// the underlay level (and that is not the view's own level), "unknown" when its level cannot be read, else null.
        /// </summary>
        public static string Underlay(long? underlayLevel, long? viewLevel, long? elementLevel)
        {
            if (underlayLevel == null) return null;
            if (elementLevel == null) return "unknown";
            if (viewLevel != null && underlayLevel == viewLevel) return null;
            return elementLevel == underlayLevel ? "halftone" : null;
        }

        public static JObject CategoryBehind(string category, IDictionary<string, string> fields, bool fromTemplate)
        {
            if (fields == null || fields.Count == 0) return null;
            return new JObject
            {
                ["category"] = category, ["fields"] = JObject.FromObject(fields), ["from_template"] = fromTemplate,
                ["effect"] = "still paints whatever the element override does not set (an earlier category method); clear_category_overrides " +
                             "clears the WHOLE category row in this view (every field, for every element of the category, not only these); " +
                             (fromTemplate ? "it is the template's row: edit the template" : "send clear_category_overrides=true to clear it in the same write")
            };
        }
    }
}
