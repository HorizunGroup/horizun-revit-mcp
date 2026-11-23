using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// P5 and §6 of the field report 2026-11-17 (Revit 2024): each test names the field defect it pins.
    /// </summary>
    public class OverridePrintRulesTests
    {
        private static JObject SetOverrides(JObject overrides, JObject extra = null)
        {
            var r = new JObject { ["operation"] = "set_overrides", ["view_id"] = 42, ["element_ids"] = new JArray(7), ["overrides"] = overrides };
            if (extra != null) foreach (var p in extra.Properties()) r[p.Name] = p.Value;
            return r;
        }

        // ---- colour and pattern together ---------------------------------------------------

        [Fact]
        public void Colour_without_pattern_defaults_to_solid_and_says_so()
        {
            // Field: a colour on no pattern painted nothing. The bridge resolves the solid fill (as manage_views) and the
            // reply names it: pattern_defaulted, never a silent colour on nothing.
            Assert.Null(ViewVisibilityRules.Validate(SetOverrides(new JObject { ["cut_color"] = "#5A5A5A" })));
            JObject d = OverridePrintRules.PatternDefaults(new JObject { ["cut_color"] = "#5A5A5A", ["surface_color"] = "#5A5A5A", ["surface_pattern"] = "Diagonal up" });
            Assert.Equal("solid", d.Value<string>("cut_pattern"));
            Assert.Null(d["surface_pattern"]);
        }

        [Fact]
        public void Pattern_without_colour_is_valid_and_not_defaulted()
        {
            Assert.Null(ViewVisibilityRules.Validate(SetOverrides(new JObject { ["cut_pattern"] = "solid" })));
            Assert.Empty(OverridePrintRules.PatternDefaults(new JObject { ["cut_pattern"] = "solid" }));
        }

        [Fact]
        public void Colour_and_pattern_together_are_accepted()
            => Assert.Null(ViewVisibilityRules.Validate(SetOverrides(new JObject { ["cut_color"] = "#5A5A5A", ["cut_pattern"] = "solid", ["surface_color"] = "#5A5A5A", ["surface_pattern"] = "Diagonal up" })));

        // ---- predictions -----------------------------------------------------------------------

        private static readonly JObject CutFill = new JObject { ["cut_color"] = "#5A5A5A", ["cut_pattern"] = "solid" };
        private static readonly JObject SurfaceFill = new JObject { ["surface_color"] = "#5A5A5A", ["surface_pattern"] = "solid" };

        [Fact]
        public void Filter_with_halftone_masks_the_element_override()
        {
            // Field: a filter with halftone turned gray 90 into 200; the element override was "verified".
            var f = new PrintFacts { Cut = true, Layers = { new PrintLayer { Label = "filter 'Existing'", Halftone = true } } };
            JObject p = OverridePrintRules.Predict(CutFill, f);
            Assert.Equal(OverridePrintRules.Masked, p.Value<string>("verdict"));
            Assert.Contains(p["reasons"], x => x.Value<string>("code") == "halftone_from_layer");
            Assert.True(OverridePrintRules.Blocks(p.Value<string>("verdict")));
        }

        [Fact]
        public void Asking_halftone_on_the_element_is_not_masked_by_a_halftone_filter()
        {
            var want = new JObject(CutFill) { ["halftone"] = true };
            var f = new PrintFacts { Cut = true, Layers = { new PrintLayer { Label = "filter", Halftone = true } } };
            Assert.Equal(OverridePrintRules.Prints, OverridePrintRules.Predict(want, f).Value<string>("verdict"));
        }

        [Fact]
        public void Template_filter_is_named_as_the_template()
        {
            var f = new PrintFacts { Cut = true, Layers = { new PrintLayer { Label = "filter 'Demo'", Halftone = true, FromTemplate = true } } };
            Assert.Contains("view template", OverridePrintRules.Predict(CutFill, f)["reasons"][0].Value<string>("setting"));
        }

        [Fact]
        public void A_layer_that_hides_means_nothing_prints()
        {
            var f = new PrintFacts { Cut = true, Layers = { new PrintLayer { Label = "category 'Walls'", Hides = true } } };
            Assert.Equal(OverridePrintRules.WillNotPrint, OverridePrintRules.Predict(CutFill, f).Value<string>("verdict"));
        }

        [Fact]
        public void Layer_transparency_masks_a_surface_override_without_its_own_transparency()
        {
            var f = new PrintFacts { Cut = false, Layers = { new PrintLayer { Label = "category 'Floors'", Transparency = 60 } } };
            Assert.Equal(OverridePrintRules.Masked, OverridePrintRules.Predict(SurfaceFill, f).Value<string>("verdict"));
            var own = new JObject(SurfaceFill) { ["transparency"] = 0 };
            Assert.Equal(OverridePrintRules.Prints, OverridePrintRules.Predict(own, f).Value<string>("verdict"));
        }

        [Fact]
        public void Surface_override_on_a_filled_region_never_prints()
        {
            // Field: surface override on FilledRegion never reached the paper.
            JObject p = OverridePrintRules.Predict(SurfaceFill, new PrintFacts { IsFilledRegion = true });
            Assert.Equal(OverridePrintRules.WillNotPrint, p.Value<string>("verdict"));
            var withLine = new JObject(SurfaceFill) { ["line_color"] = "#000000" };
            Assert.Equal(OverridePrintRules.Partial, OverridePrintRules.Predict(withLine, new PrintFacts { IsFilledRegion = true }).Value<string>("verdict"));
        }

        [Fact]
        public void Cut_override_on_a_projection_only_element_draws_no_pixel()
        {
            // Field: cut override on an element the view only shows in projection produced no pixels.
            Assert.Equal(OverridePrintRules.WillNotPrint, OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = false }).Value<string>("verdict"));
            var both = new JObject(CutFill) { ["surface_color"] = "#5A5A5A", ["surface_pattern"] = "solid" };
            Assert.Equal(OverridePrintRules.Partial, OverridePrintRules.Predict(both, new PrintFacts { Cut = false }).Value<string>("verdict"));
        }

        [Fact]
        public void Unknown_cut_is_not_a_finding()
            => Assert.Equal(OverridePrintRules.Prints, OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = null }).Value<string>("verdict"));

        [Fact]
        public void Surface_override_on_a_cut_element_is_partial()
            => Assert.Equal(OverridePrintRules.Partial, OverridePrintRules.Predict(SurfaceFill, new PrintFacts { Cut = true }).Value<string>("verdict"));

        [Fact]
        public void Nested_family_geometry_is_partial_and_does_not_block()
        {
            // Field: stair nosings and in-family sweeps ignored the element override.
            var f = new PrintFacts { Cut = true, NestedGeometry = { "Stairs: Nosings", "Generic Models: Sweep (not overridable)" } };
            JObject p = OverridePrintRules.Predict(CutFill, f);
            Assert.Equal(OverridePrintRules.Partial, p.Value<string>("verdict"));
            Assert.False(OverridePrintRules.Blocks(OverridePrintRules.Partial));
            Assert.Contains("not overridable", p["reasons"][0].Value<string>("setting"));
        }

        [Fact]
        public void Worst_verdict_wins()
        {
            var f = new PrintFacts { Cut = false, NestedGeometry = { "x" }, Layers = { new PrintLayer { Label = "f", Halftone = true } } };
            Assert.Equal(OverridePrintRules.WillNotPrint, OverridePrintRules.Predict(CutFill, f).Value<string>("verdict"));
        }

        [Fact]
        public void Refusal_counts_and_says_nothing_was_written()
        {
            JObject counts = OverridePrintRules.Count(new[] { "prints", "masked", "will_not_print", "masked" });
            Assert.Equal(2, counts.Value<int>("masked"));
            string text = OverridePrintRules.Refusal(3, 4, counts);
            Assert.Contains("3 of 4", text);
            Assert.Contains("unprinted=skip", text);
            Assert.Contains("Nothing was written", text);
        }

        [Fact]
        public void Category_row_behind_is_reported_only_when_it_sets_something()
        {
            // Field: after switching from category to element overrides, the old category override kept painting behind.
            Assert.Null(OverridePrintRules.CategoryBehind("Walls", new Dictionary<string, string>(), false));
            JObject row = OverridePrintRules.CategoryBehind("Walls", new Dictionary<string, string> { ["line_color"] = "#FF0000" }, false);
            Assert.Equal("#FF0000", row["fields"].Value<string>("line_color"));
            Assert.Contains("clear_category_overrides", row.Value<string>("effect"));
            Assert.Contains("template", OverridePrintRules.CategoryBehind("Walls", new Dictionary<string, string> { ["halftone"] = "true" }, true).Value<string>("effect"));
        }

        // ---- arguments ---------------------------------------------------------------------------

        [Theory]
        [InlineData("refuse", true)]
        [InlineData("skip", true)]
        [InlineData("write", true)]
        [InlineData("ignore", false)]
        public void Unprinted_policy_is_closed(string policy, bool ok)
        {
            string why = ViewVisibilityRules.Validate(SetOverrides(CutFill, new JObject { ["unprinted"] = policy }));
            Assert.Equal(ok, why == null);
        }

        [Fact]
        public void Print_arguments_belong_to_set_overrides_only()
        {
            var r = new JObject { ["operation"] = "hide", ["view_id"] = 42, ["element_ids"] = new JArray(7), ["unprinted"] = "skip" };
            Assert.Contains("set_overrides only", ViewVisibilityRules.Validate(r));
            r = new JObject { ["operation"] = "clear_overrides", ["view_id"] = 42, ["element_ids"] = new JArray(7), ["clear_category_overrides"] = true };
            Assert.Contains("set_overrides only", ViewVisibilityRules.Validate(r));
            Assert.Contains("boolean", ViewVisibilityRules.Validate(SetOverrides(CutFill, new JObject { ["clear_category_overrides"] = "yes" })));
        }

        [Fact]
        public void Override_report_needs_views_and_elements_and_only_reads()
        {
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{operation:'override_report',view_ids:[1,2],element_ids:[7]}")));
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{operation:'override_report',view_ids:[1],type_names:['Stair'],overrides:{cut_color:'#5A5A5A'}}")));
            Assert.Contains("view_ids", ViewVisibilityRules.Validate(JObject.Parse("{operation:'override_report',element_ids:[7]}")));
            Assert.Contains("element_ids", ViewVisibilityRules.Validate(JObject.Parse("{operation:'override_report',view_ids:[1]}")));
            Assert.Contains("only reads", ViewVisibilityRules.Validate(JObject.Parse("{operation:'override_report',view_id:3,view_ids:[1],element_ids:[7]}")));
            Assert.Contains("Unknown override", ViewVisibilityRules.Validate(JObject.Parse("{operation:'override_report',view_ids:[1],element_ids:[7],overrides:{colour:'#000000'}}")));
        }

        [Fact]
        public void Link_datum_cleanup_takes_view_and_kinds_only()
        {
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{operation:'link_datum_cleanup',view_id:3}")));
            Assert.Null(ViewVisibilityRules.Validate(JObject.Parse("{operation:'link_datum_cleanup',view_id:3,datum_kinds:['level']}")));
            Assert.Contains("needs view_id", ViewVisibilityRules.Validate(JObject.Parse("{operation:'link_datum_cleanup'}")));
            Assert.Contains("does not apply", ViewVisibilityRules.Validate(JObject.Parse("{operation:'link_datum_cleanup',view_id:3,element_ids:[1]}")));
        }

        // ---- §6 linked datums --------------------------------------------------------------------

        [Fact]
        public void Host_names_are_every_host_type_not_only_those_drawn_today()
        {
            // Review B3: names taken from the datums visible today let the filter hide a host datum of another type
            // (hidden or outside the crop now) the day it is shown again.
            Assert.Equal(new[] { "6.5mm Bubble", "Other", "Plain" }, LinkDatumFilterRules.HostNames(new[] { "Plain", "6.5mm Bubble", "6.5MM BUBBLE", "Other", " ", null }));
            Assert.Empty(LinkDatumFilterRules.HostNames(null));
        }

        [Fact]
        public void Every_host_datum_with_a_listed_type_is_protected()
        {
            var names = LinkDatumFilterRules.HostNames(new[] { "Plain", "Bubble" });
            Assert.Null(LinkDatumFilterRules.HostUnprotected("bubble", names));
            Assert.Equal("type_not_in_rule", LinkDatumFilterRules.HostUnprotected("Other", names));
            Assert.Equal(LinkDatumFilterRules.Unreadable, LinkDatumFilterRules.HostUnprotected(null, names));
        }

        [Fact]
        public void Rule_values_must_match_exactly()
        {
            Assert.True(LinkDatumFilterRules.SameNames(new[] { "plain", "Bubble" }, new[] { "Bubble", "Plain" }));
            Assert.False(LinkDatumFilterRules.SameNames(new[] { "Plain" }, new[] { "Bubble", "Plain" }));
            Assert.False(LinkDatumFilterRules.SameNames(new[] { "Plain", "Plain", "Bubble" }, new[] { "Bubble", "Plain" }));
            Assert.False(LinkDatumFilterRules.SameNames(null, new[] { "Plain" }));
        }

        // ---- review B1 / B2 ---------------------------------------------------------------------

        [Fact]
        public void Element_the_view_does_not_draw_will_not_print()
        {
            // Review B1: an id outside the view used to predict "prints".
            Assert.Equal("not_in_view", OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = true, InView = false })["reasons"][0].Value<string>("code"));
            Assert.Equal(OverridePrintRules.WillNotPrint, OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = true, HiddenInView = true }).Value<string>("verdict"));
            Assert.Equal(OverridePrintRules.WillNotPrint, OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = true, HiddenWorkset = "Shell" }).Value<string>("verdict"));
            Assert.Equal(OverridePrintRules.Prints, OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = true, InView = null }).Value<string>("verdict"));
        }

        [Fact]
        public void Halftone_already_on_the_element_is_kept_by_the_merge()
        {
            // Review B2: the write merges into the element's current override; its halftone survived as "prints".
            var f = new PrintFacts { Cut = true, Layers = { new PrintLayer { Label = "the element's current override", Own = true, Halftone = true } } };
            JObject p = OverridePrintRules.Predict(CutFill, f);
            Assert.Equal(OverridePrintRules.Masked, p.Value<string>("verdict"));
            Assert.Equal("halftone_already_on_element", p["reasons"][0].Value<string>("code"));
            var off = new JObject(CutFill) { ["halftone"] = false };
            Assert.Equal(OverridePrintRules.Prints, OverridePrintRules.Predict(off, f).Value<string>("verdict"));
        }

        [Fact]
        public void Transparency_already_on_the_element_masks_a_surface_fill_unless_resent()
        {
            var f = new PrintFacts { Cut = false, Layers = { new PrintLayer { Label = "own", Own = true, Transparency = 50 } } };
            Assert.Equal(OverridePrintRules.Masked, OverridePrintRules.Predict(SurfaceFill, f).Value<string>("verdict"));
            Assert.Equal(OverridePrintRules.Prints, OverridePrintRules.Predict(new JObject(SurfaceFill) { ["transparency"] = 0 }, f).Value<string>("verdict"));
        }

        [Theory]
        [InlineData("not_displayed", "will_not_print", "phase_not_displayed")]
        [InlineData("overridden", "masked", "phase_graphics")]
        [InlineData("unreadable", "partial", "phase_unreadable")]
        [InlineData("by_category", "prints", null)]
        public void Phase_presentation_is_judged(string phase, string verdict, string code)
        {
            JObject p = OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = true, Phase = phase, PhaseStatus = "Existing" });
            Assert.Equal(verdict, p.Value<string>("verdict"));
            if (code != null) Assert.Equal(code, p["reasons"][0].Value<string>("code"));
        }

        [Fact]
        public void Underlay_prints_halftone_and_an_unreadable_level_is_partial()
        {
            Assert.Equal(OverridePrintRules.Masked, OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = true, InView = null, Underlay = "halftone" }).Value<string>("verdict"));
            Assert.Equal(OverridePrintRules.Partial, OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = true, InView = null, Underlay = "unknown" }).Value<string>("verdict"));
        }

        [Fact]
        public void Linked_datum_with_the_host_type_name_is_reported_not_claimed()
        {
            // Field: the Type Name filter solved 93 level clashes and failed on grids whose link type had the host's name.
            var host = new List<string> { "Grid Head - Circle" };
            Assert.Equal(LinkDatumFilterRules.SameName, LinkDatumFilterRules.Unresolved("grid head - circle", host, true));
            Assert.Null(LinkDatumFilterRules.Unresolved("Link Grid", host, true));
            Assert.Equal(LinkDatumFilterRules.Bypass, LinkDatumFilterRules.Unresolved("Link Grid", host, false));
            Assert.Equal(LinkDatumFilterRules.BypassUnknown, LinkDatumFilterRules.Unresolved("Link Grid", host, null));
            Assert.Equal(LinkDatumFilterRules.NoHostNames, LinkDatumFilterRules.Unresolved("Link Grid", new List<string>(), true));
            Assert.Equal(LinkDatumFilterRules.Unreadable, LinkDatumFilterRules.Unresolved(null, host, true));
            Assert.NotNull(LinkDatumFilterRules.Advice(LinkDatumFilterRules.SameName));
        }

        [Theory]
        [InlineData(null, null, true)]
        [InlineData("ByHostView", null, true)]
        [InlineData("ByLinkView", null, false)]
        [InlineData("Custom", null, null)]
        [InlineData("Custom", "ByHostView", true)]
        [InlineData("Custom", "ByLinkView", false)]
        public void Link_display_decides_whether_host_filters_reach_it(string visibility, string filters, bool? expected)
            => Assert.Equal(expected, LinkDatumFilterRules.TakesHostFilters(visibility, filters));

        [Fact]
        public void Filter_name_is_stable_per_kind_and_view()
        {
            Assert.Equal(LinkDatumFilterRules.FilterName("grid", 12), LinkDatumFilterRules.FilterName("grid", 12));
            Assert.NotEqual(LinkDatumFilterRules.FilterName("grid", 12), LinkDatumFilterRules.FilterName("level", 12));
            Assert.NotEqual(LinkDatumFilterRules.FilterName("grid", 12), LinkDatumFilterRules.FilterName("grid", 13));
        }

        [Fact]
        public void Api_help_carries_the_link_limits()
        {
            Assert.Contains(ApiFieldNotes.For("RevitLinkGraphicsSettings"), n => n.ToString().Contains("link_datum_cleanup"));
            Assert.Contains(ApiFieldNotes.For("RevitLinkInstance"), n => n.ToString().Contains("RevitLinkType"));
            Assert.Contains(ApiFieldNotes.For("View"), n => n.ToString().Contains("HOST element ids"));
            Assert.Null(ApiFieldNotes.For("Wall"));
        }

        // ---- review round 2 ---------------------------------------------------------------------

        [Fact]
        public void Phase_graphics_mask_a_colour_or_pattern_so_refuse_stops_it()
        {
            // Round 2 #1: Existing under Show Previous + New (or Demolished) is painted by unreadable phase graphics.
            var f = new PrintFacts { Cut = true, Phase = "overridden", PhaseStatus = "Existing" };
            JObject p = OverridePrintRules.Predict(CutFill, f);
            Assert.Equal(OverridePrintRules.Masked, p.Value<string>("verdict"));
            Assert.True(OverridePrintRules.Blocks(p.Value<string>("verdict")));
            Assert.Equal(OverridePrintRules.Masked, OverridePrintRules.Predict(new JObject { ["line_color"] = "#FF0000" }, f).Value<string>("verdict"));
            // No colour or pattern asked (halftone only): nothing to promise, partial.
            Assert.Equal(OverridePrintRules.Partial, OverridePrintRules.Predict(new JObject { ["halftone"] = true }, f).Value<string>("verdict"));
            // Not readable: stays partial.
            Assert.Equal(OverridePrintRules.Partial, OverridePrintRules.Predict(CutFill, new PrintFacts { Cut = true, Phase = "unreadable" }).Value<string>("verdict"));
        }

        [Theory]
        [InlineData(true, "FilterStringRule", "FilterStringEquals", true)]
        [InlineData(false, "FilterStringRule", "FilterStringNotEquals", true)]
        [InlineData(false, "FilterStringRule", "FilterStringEquals", false)]
        [InlineData(true, "FilterStringRule", "FilterStringContains", false)]
        [InlineData(true, "FilterDoubleRule", "FilterNumericEquals", false)]
        [InlineData(true, "FilterStringRule", null, false)]
        public void Not_equals_rule_shapes_are_recognised_and_others_are_unreadable(bool inverted, string rule, string evaluator, bool ok)
            => Assert.Equal(ok, LinkDatumFilterRules.RuleShape(inverted, rule, evaluator) == LinkDatumFilterRules.NotEqualsShape);

        [Fact]
        public void Rules_read_back_wrong_or_unreadable_stop_before_commit()
        {
            // Round 2 #2: checked inside the transaction; a message means the apply throws and the runner rolls back.
            Assert.Contains("unreadable", LinkDatumFilterRules.RulesReadBackError(null, new[] { "Plain" }));
            Assert.Contains("not the host type names", LinkDatumFilterRules.RulesReadBackError(new[] { "Other" }, new[] { "Plain" }));
            Assert.Null(LinkDatumFilterRules.RulesReadBackError(new[] { "plain" }, new[] { "Plain" }));
            string src = System.IO.File.ReadAllText(Source("Commands", "ViewVisibilityLinkDatums.cs"));
            int apply = src.IndexOf("edit.Apply = d =>", System.StringComparison.Ordinal), verify = src.IndexOf("edit.Verify = d =>", System.StringComparison.Ordinal);
            int check = src.IndexOf("RulesReadBackError(values, p.HostNames)", System.StringComparison.Ordinal);
            Assert.True(apply > 0 && check > apply && check < verify, "the read-back check must run inside Apply (before commit)");
        }

        [Fact]
        public void Print_context_is_built_once_per_view_per_call()
        {
            // Round 2 #3: Context() ran twice per set_overrides and once per view in override_report.
            string src = System.IO.File.ReadAllText(Source("Commands", "ViewVisibilityPrint.cs"));
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"Context\(doc, view\)").Count);   // only inside CachedContext
            Assert.DoesNotContain("PrintContext c = Context(", src);
            Assert.Contains("CachedContext(contexts, doc, v)", src);
            string cmd = System.IO.File.ReadAllText(Source("Commands", "ViewVisibilityCommand.cs"));
            Assert.Contains("PrintCheck(doc, view, elements, wanted, blocked, contexts)", cmd);
            Assert.Contains("CategoriesBehind(doc, view, elements, contexts)", cmd);
        }

        [Theory]
        [InlineData(10L, 20L, 10L, "halftone")]   // drawn or not: on the underlay level
        [InlineData(10L, 20L, 20L, null)]         // on the view's own level
        [InlineData(10L, 20L, null, "unknown")]   // level unreadable
        [InlineData(null, 20L, 10L, null)]        // no underlay
        [InlineData(20L, 20L, 20L, null)]         // underlay = the view's level: nothing extra
        public void Underlay_is_judged_whether_or_not_the_collector_returns_the_element(long? underlay, long? viewLevel, long? elementLevel, string expected)
            => Assert.Equal(expected, OverridePrintRules.Underlay(underlay, viewLevel, elementLevel));

        [Fact]
        public void Clear_category_overrides_says_it_clears_the_whole_row()
        {
            JObject row = OverridePrintRules.CategoryBehind("Walls", new Dictionary<string, string> { ["line_color"] = "#FF0000" }, false);
            Assert.Contains("WHOLE category row", row.Value<string>("effect"));
            Assert.Contains("WHOLE category row", Horizun.Contracts.Contract.Find("horizun_view_visibility").InputSchema
                .SelectToken("properties.clear_category_overrides.description").ToString());
        }

        private static string Source(string folder, string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here), "..", "..", "src", "Horizun.Revit", folder, file));
    }
}
