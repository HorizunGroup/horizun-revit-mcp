using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// 5.2.1, branch q90-vinculos: view templates, the datum-name separator, nested links, filter categories and federation rules.
    /// Each test names the fix it guards; reverting that fix makes it fail. The Revit wiring (which view is gated, the copy, the
    /// re-read) is not reachable without Revit: it calls these pure functions, and the live battery measures the wiring.
    /// </summary>
    public class LinkTemplateAndFilterRulesTests
    {
        private const string Nothing = "Nothing was written";
        private static int Count(string text, string what) => Regex.Matches(text ?? "", Regex.Escape(what)).Count;

        // ---- 4. "Nothing was written" once, on the message the caller receives ------------------------------------------
        [Fact]
        public void Template_refusals_reach_the_caller_with_the_phrase_once()
        {
            Assert.Null(LinkDatumFilterRules.ParseTemplateMode(JValue.CreateString("copy"), out string modeError));
            var messages = new[]
            {
                LinkDatumFilterRules.TemplateRefusal("Plan 1", "TPL", 985116),
                modeError,
                LinkDatumFilterRules.ForeignTemplateRefusal("TPL", 985116, "carries no HRZ ownership mark"),
                LinkDatumFilterRules.ForeignTemplateUsersRefusal("TPL", 985116, 5, new[] { "'A' (1)" }, 1),
                LinkDatumFilterRules.NameSeparatorOnTemplateRefusal,
                ViewVisibilityRules.Validate(JObject.Parse("{ 'operation': 'hide', 'view_id': 1, 'element_ids': [1], 'template_mode': 'refuse' }"))
            };
            foreach (string m in messages)
            {
                Assert.NotNull(m);
                Assert.Equal(0, Count(m, Nothing));                                     // the function alone does not say it...
                string final = CommandResult.RefuseBeforeWrite(m).Error;                 // ...the runner's refusal says it once
                Assert.Equal(1, Count(final, Nothing));
            }
        }

        [Fact]
        public void A_refusal_that_already_says_it_is_not_given_a_second_copy()
        {
            // Field case (round 1): "... work on a duplicate. Nothing was written. Nothing was written."
            Assert.Equal(1, Count(CommandResult.RefuseBeforeWrite("view 'X' is not ours. Nothing was written.").Error, Nothing));
            Assert.Equal(1, Count(CommandResult.RefuseBeforeWrite("no phrase here").Error, Nothing));
            Assert.EndsWith("Nothing was written.", CommandResult.RefuseBeforeWrite("no phrase here").Error);
        }

        [Theory]
        [InlineData(null, "refuse")]
        [InlineData("refuse", "refuse")]
        [InlineData("Duplicate_View", "duplicate_view")]
        [InlineData(" edit_template ", "edit_template")]
        public void Template_mode_defaults_to_refuse_and_reads_the_three_names(string raw, string expected)
        {
            JToken t = raw == null ? null : JValue.CreateString(raw);
            Assert.Equal(expected, LinkDatumFilterRules.ParseTemplateMode(t, out string error));
            Assert.Null(error);
        }

        [Fact]
        public void A_non_string_template_mode_is_refused()
        {
            Assert.Null(LinkDatumFilterRules.ParseTemplateMode(new JValue(3), out string error));
            Assert.Contains("duplicate_view", error);
        }

        // ---- 1. edit_template: the strict gate, the users, no self-refusal on a repeat -----------------------------------
        [Fact]
        public void The_template_gate_trusts_a_version_1_mark_only_outside_a_collaborative_model()
        {
            // The gate of view_crop and sheet_layout (GateTrusts), not OwnershipRules.Decide: another user's mark is foreign everywhere.
            var me = new HrzOwnershipRules.UserRef { User = "usuario1" };
            var v1 = new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = null };
            var other = new HrzOwnershipRules.MarkFacts { Present = true, Session = "s0", User = new HrzOwnershipRules.UserRef { User = "ana" } };
            Assert.Null(HrzOwnershipRules.RefusalReason(v1, "s1", me, false, workshared: false));
            Assert.NotNull(HrzOwnershipRules.RefusalReason(v1, "s1", me, false, workshared: true));
            Assert.Contains("another user", HrzOwnershipRules.RefusalReason(other, "s1", me, false, workshared: false));
            Assert.Contains("no HRZ ownership mark", HrzOwnershipRules.RefusalReason(new HrzOwnershipRules.MarkFacts(), "s1", me, false, false));
        }

        [Fact]
        public void The_foreign_template_refusals_name_the_template_the_views_and_the_way_out()
        {
            string t = LinkDatumFilterRules.ForeignTemplateRefusal("TPL", 985116, "carries no HRZ ownership mark");
            Assert.Contains("985116", t); Assert.Contains("no HRZ ownership mark", t); Assert.Contains("duplicate_view", t);
            var names = Enumerable.Range(1, 25).Select(i => "'V" + i + "' (" + i + ")").ToList();
            string u = LinkDatumFilterRules.ForeignTemplateUsersRefusal("TPL", 985116, 30, names, 25);
            Assert.Contains("used by 30 view(s), and 25 of them are not ours", u);
            Assert.Contains("'V20' (20)", u); Assert.DoesNotContain("'V21'", u); Assert.Contains("and 5 more", u);
        }

        [Fact]
        public void Name_separator_with_edit_template_is_refused_with_both_ways_out()
        {
            string m = LinkDatumFilterRules.NameSeparatorOnTemplateRefusal;
            Assert.Contains("edit_template", m); Assert.Contains("duplicate_view", m);
        }

        [Fact]
        public void Repeating_edit_template_does_not_refuse_itself()
        {
            const long template = 985116;
            // A view governed by the template "uses" the template's filter: that is the template's own use.
            Assert.False(LinkDatumFilterRules.OtherFilterUser(312, template, true, template, targetIsTemplate: true));
            Assert.False(LinkDatumFilterRules.OtherFilterUser(template, -1, true, template, targetIsTemplate: true));
            // Another template, or a view on its own, that carries the filter is somebody else's use.
            Assert.True(LinkDatumFilterRules.OtherFilterUser(777, -1, true, template, targetIsTemplate: true));
            Assert.True(LinkDatumFilterRules.OtherFilterUser(312, template, true, 1427339, targetIsTemplate: false));
            Assert.False(LinkDatumFilterRules.OtherFilterUser(312, template, false, template, targetIsTemplate: true));
        }

        // ---- 2. duplicate_view -------------------------------------------------------------------------------------------
        [Fact]
        public void The_template_refusal_says_what_the_copy_is_without_promising_it_looks_the_same()
        {
            string m = LinkDatumFilterRules.TemplateRefusal("Plan 1", "TPL", 985116);
            Assert.Contains("NEW view", m); Assert.Contains("not placed on any sheet", m); Assert.Contains("detailing", m);
            Assert.DoesNotContain("safe", m); Assert.DoesNotContain("look is kept", m); Assert.DoesNotContain("looks the same", m);
        }

        [Fact]
        public void The_copy_assumptions_use_the_one_format_and_name_the_sheet()
        {
            List<JObject> rows = LinkDatumFilterRules.DuplicateViewAssumptions("TPL", new[] { "A-101 - Plantas" });
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => { Assert.Equal("SUPUESTO", (string)r["flag"]); Assert.NotNull(r["what"]); Assert.NotNull(r["why"]); Assert.NotNull(r["override"]); });
            Assert.Contains("NOT placed on any sheet", (string)rows[0]["what"]);
            Assert.Contains("A-101 - Plantas", (string)rows[0]["what"]);
            Assert.Contains("no view template", (string)rows[1]["what"]);
            Assert.Contains("view range", (string)rows[2]["what"]);
            JObject stamped = AssumptionNotes.Stamp(new JObject(), rows);
            Assert.True((bool)stamped["has_assumptions"]);
            Assert.Equal(3, ((JArray)stamped["assumptions"]).Count);
        }

        [Fact]
        public void The_copy_name_is_chosen_at_apply_and_unique_without_regard_to_case()
        {
            Assert.Contains("chosen at apply", LinkDatumFilterRules.CopyNameAtApply("Plan"));
            Assert.Equal("Plan - Horizun datums", LinkDatumFilterRules.CopyName("Plan", new[] { "Plan" }));
            Assert.Equal("Plan - Horizun datums (2)", LinkDatumFilterRules.CopyName("Plan", new[] { "plan - horizun datums" }));
        }

        private static LinkDatumFilterRules.ViewLook Look(int scale, string detail, params (long id, string state)[] filters)
        {
            var l = new LinkDatumFilterRules.ViewLook { Scale = scale, DetailLevel = detail };
            foreach (var f in filters) l.Filters[f.id] = f.state;
            l.HiddenCategories.Add(-2000240);
            return l;
        }

        [Fact]
        public void The_copy_is_compared_with_the_original_on_scale_detail_filters_and_categories()
        {
            string hidden = LinkDatumFilterRules.FilterState(false, true), shown = LinkDatumFilterRules.FilterState(true, true);
            var original = Look(100, "Medium", (10, hidden), (11, shown));
            var same = Look(100, "Medium", (10, hidden), (11, shown), (99, hidden));
            Assert.Empty(LinkDatumFilterRules.LookDifferences(original, same, new long[] { 99 }));   // our filter is excluded
            Assert.Contains("filter 99 added", LinkDatumFilterRules.LookDifferences(original, same, new long[0]));
            var other = Look(50, "Fine", (10, shown));
            other.HiddenCategories.Clear(); other.HiddenCategories.Add(-2000011);
            List<string> d = LinkDatumFilterRules.LookDifferences(original, other, new long[0]);
            Assert.Contains("scale 1:100 -> 1:50", d);
            Assert.Contains("detail level Medium -> Fine", d);
            Assert.Contains(d, x => x.StartsWith("filter 10: hidden"));
            Assert.Contains("filter 11 missing", d);
            Assert.Contains("category -2000240 shown in the copy", d);
            Assert.Contains("category -2000011 hidden in the copy", d);
            Assert.Contains("scale unreadable", LinkDatumFilterRules.LookDifferences(new LinkDatumFilterRules.ViewLook(), original, null));
        }

        // ---- 9. original_filters_unchanged compares WHICH filters and their visibility --------------------------------
        [Fact]
        public void Same_number_of_filters_with_another_one_or_another_visibility_is_a_change()
        {
            var before = new Dictionary<long, string> { [1] = "hidden|enabled", [2] = "visible|enabled" };
            Assert.Empty(LinkDatumFilterRules.FilterDifferences(before, new Dictionary<long, string>(before)));
            Assert.NotEmpty(LinkDatumFilterRules.FilterDifferences(before, new Dictionary<long, string> { [1] = "hidden|enabled", [3] = "visible|enabled" }));
            Assert.NotEmpty(LinkDatumFilterRules.FilterDifferences(before, new Dictionary<long, string> { [1] = "visible|enabled", [2] = "visible|enabled" }));
        }

        // ---- 3. separator warnings, in a coordinator's words, as SUPUESTO rows -----------------------------------------
        [Fact]
        public void The_name_and_type_filters_warn_what_a_coordinator_does_next()
        {
            JObject n = LinkDatumFilterRules.NameFilterAssumption("grid");
            Assert.Equal("SUPUESTO", (string)n["flag"]);
            Assert.Contains("renames a host grid", (string)n["why"]);
            Assert.Contains("creates a new one", (string)n["why"]);
            Assert.Contains("new grid type", (string)n["why"]);
            Assert.Contains("hidden in this view until link_datum_cleanup is run again", (string)n["why"]);
            JObject t = LinkDatumFilterRules.TypeFilterAssumption("level");
            Assert.Contains("creates a new level type", (string)t["why"]);
            Assert.Contains("hidden in this view until link_datum_cleanup is run again", (string)t["why"]);
        }

        // ---- 5. nested links -----------------------------------------------------------------------------------------
        [Fact]
        public void The_nested_document_cap_is_one_for_the_whole_call_and_counts_each_document_once()
        {
            var b = new LinkDatumFilterRules.NestedBudget();
            b.AddTop("A"); b.AddTop("B"); b.AddTop("A");                 // two instances of A: one document
            Assert.Equal(2, b.Documents);
            Assert.True(b.TryAdd("N1"));
            Assert.False(b.TryAdd("N1"));                                // read under A, not again under B
            Assert.False(b.TryAdd("A"));
            for (int i = 0; i < 1000; i++) b.TryAdd("X" + i);           // across every top link together
            Assert.Equal(LinkDatumFilterRules.MaxNestedDocuments, b.Documents);
            Assert.True(b.Truncated);
        }

        [Fact]
        public void A_nested_row_carries_the_whole_chain_of_links()
        {
            string p = LinkDatumFilterRules.LinkPath(new[] { new KeyValuePair<string, long>("Casa-E", 12), new KeyValuePair<string, long>("Est", 7) });
            Assert.Equal("Casa-E (link instance 12) > Est (link instance 7)", p);
        }

        [Fact]
        public void A_nested_link_takes_the_host_filters_only_as_far_as_its_parent_says()
        {
            Assert.True(LinkDatumFilterRules.NestedTakesHostFilters(true, null, null));
            Assert.True(LinkDatumFilterRules.NestedTakesHostFilters(true, "ByHostView", null));
            Assert.False(LinkDatumFilterRules.NestedTakesHostFilters(false, "ByLinkView", null));
            Assert.True(LinkDatumFilterRules.NestedTakesHostFilters(true, "Custom", "ByHostView"));
            Assert.False(LinkDatumFilterRules.NestedTakesHostFilters(true, "Custom", "ByLinkView"));
            Assert.Null(LinkDatumFilterRules.NestedTakesHostFilters(true, "Custom", null));      // 2024: not readable
            Assert.Null(LinkDatumFilterRules.NestedTakesHostFilters(null, "Custom", "ByHostView"));
        }

        // ---- 9. LinkedHidden never counts a datum twice -----------------------------------------------------------------
        [Fact]
        public void A_datum_the_type_filter_hides_is_never_counted_again_by_the_name_filter()
        {
            Assert.Equal(LinkDatumFilterRules.DatumOutcome.ByType, LinkDatumFilterRules.Classify(null, nameHides: true));
            Assert.Equal(LinkDatumFilterRules.DatumOutcome.ByNameOnly, LinkDatumFilterRules.Classify(LinkDatumFilterRules.SameName, true));
            Assert.Equal(LinkDatumFilterRules.DatumOutcome.Unresolved, LinkDatumFilterRules.Classify(LinkDatumFilterRules.SameName, false));
            Assert.Equal(LinkDatumFilterRules.DatumOutcome.Unresolved, LinkDatumFilterRules.Classify(LinkDatumFilterRules.Bypass, true));
        }

        [Fact]
        public void The_name_filter_has_its_own_name_per_kind_and_view()
        {
            Assert.NotEqual(LinkDatumFilterRules.FilterName("grid", 7), LinkDatumFilterRules.NameFilterName("grid", 7));
            Assert.Equal("Horizun linked levels hidden by name - view 9", LinkDatumFilterRules.NameFilterName("level", 9));
        }

        [Fact]
        public void Host_datum_names_are_safe_only_when_every_one_is_readable_and_trimmed()
        {
            Assert.Equal(new[] { "A", "b" }, LinkDatumFilterRules.HostDatumNames(new[] { "b", "A", "a" }));
            Assert.Null(LinkDatumFilterRules.HostDatumNames(new string[0]));
            Assert.Null(LinkDatumFilterRules.HostDatumNames(new[] { "A", "" }));
            Assert.Null(LinkDatumFilterRules.HostDatumNames(new[] { "A", "B " }));
        }

        [Fact]
        public void The_name_rule_hides_only_a_readable_name_that_no_host_datum_carries()
        {
            var host = new[] { "A", "B" };
            Assert.True(LinkDatumFilterRules.NameSeparates("L-A", host));
            Assert.False(LinkDatumFilterRules.NameSeparates("a", host));
            Assert.False(LinkDatumFilterRules.NameSeparates("", host));
            Assert.False(LinkDatumFilterRules.NameSeparates("X ", host));
            Assert.False(LinkDatumFilterRules.NameSeparates("X", new string[0]));
        }

        // ---- 9. visible_elements: a filter with no rules hides its whole category -------------------------------------
        [Fact]
        public void A_view_filter_covers_only_its_own_categories()
        {
            Assert.True(LinkViewVisibilityRules.FilterCoversCategory(new long[] { -2000240, -2000220 }, -2000240));
            Assert.False(LinkViewVisibilityRules.FilterCoversCategory(new long[] { -2000240 }, -2000011));
            Assert.False(LinkViewVisibilityRules.FilterCoversCategory(null, -2000240));
        }

        [Fact]
        public void A_hidden_filter_without_rules_hides_every_element_of_its_categories()
        {
            Assert.True(LinkViewVisibilityRules.HiddenByFilter(true, hasRules: false, passes: null));
            Assert.False(LinkViewVisibilityRules.HiddenByFilter(false, hasRules: false, passes: null));
            Assert.True(LinkViewVisibilityRules.HiddenByFilter(true, true, () => true));
            Assert.False(LinkViewVisibilityRules.HiddenByFilter(true, true, () => false));
        }















        [Fact]
        public void Carried_assumptions_reach_the_top_of_the_reply_once()
        {
            var plan = AssumptionNotes.Stamp(new JObject(), new[] { LinkDatumFilterRules.TypeFilterAssumption("grid") });
            var reply = new JObject { ["plan"] = plan };
            Horizun.Revit.Commands.ModelEditRunner.CarryAssumptions(reply, plan);
            Assert.True((bool)reply["has_assumptions"]);
            Assert.Single((JArray)reply["assumptions"]);
            Horizun.Revit.Commands.ModelEditRunner.CarryAssumptions(new JObject(), new JObject());   // nothing to carry: no throw
        }
    }
}
