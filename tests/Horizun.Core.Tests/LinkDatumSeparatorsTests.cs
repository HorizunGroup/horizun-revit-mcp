using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>§6 of the field report 2026-11-17: the linked grid with the SAME type name as a host type.</summary>
    public class LinkDatumSeparatorsTests
    {
        private static LinkDatumSeparators.Row R(string id, string workset = null, string scope = null, string name = null)
        {
            var r = new LinkDatumSeparators.Row { Id = id };
            if (workset != null) r.Values[LinkDatumSeparators.Workset] = workset;
            if (scope != null) r.Values[LinkDatumSeparators.ScopeBox] = scope;
            if (name != null) r.Values[LinkDatumSeparators.Name] = name;
            return r;
        }

        private static LinkDatumSeparators.Verdict Of(IEnumerable<LinkDatumSeparators.Verdict> v, string c) => v.Single(x => x.Candidate == c);

        [Fact]
        public void Different_datum_names_separate_and_are_the_one_proposal()
        {
            var host = new[] { R("h1", "Shared Grids", name: "A"), R("h2", "Shared Grids", name: "B") };
            var linked = new[] { R("l1", "Link Grids", name: "L-A"), R("l2", "Link Grids", name: "L-B") };
            var v = LinkDatumSeparators.Analyze(host, linked);
            Assert.Equal(2, Of(v, "name").Separated);
            Assert.True(Of(v, "name").Usable);
            Assert.Equal("name", LinkDatumSeparators.Best(v).Candidate);
        }

        [Fact]
        public void Workset_and_scope_box_are_never_proposed_because_they_are_ids_of_each_document()
        {
            // A workset id in the host and in the link are different namespaces: a rule cannot be proven to tell them apart.
            var host = new[] { R("h1", "Shared Grids", "SB1", "A") };
            var linked = new[] { R("l1", "Link Grids", "SBL", "A") };
            var v = LinkDatumSeparators.Analyze(host, linked);
            Assert.Equal(1, Of(v, "workset").Separated);   // reported
            Assert.False(Of(v, "workset").Usable);
            Assert.False(Of(v, "scope_box").Usable);
            Assert.Contains("ids of the host's document", Of(v, "workset").Caveat);
            Assert.Null(LinkDatumSeparators.Best(v));       // same name as the host: nothing provable
        }

        [Fact]
        public void Same_value_as_a_host_datum_is_never_claimed_as_separated()
        {
            var host = new[] { R("h1", name: "Level 1") };
            var linked = new[] { R("l1", name: "level 1"), R("l2", name: "Other") };   // case-insensitive, like Revit's rule
            var w = Of(LinkDatumSeparators.Analyze(host, linked), "name");
            Assert.Equal(1, w.Separated);
            Assert.Equal(1, w.SameValue);
        }

        [Fact]
        public void A_host_datum_without_a_readable_value_makes_the_candidate_unsafe()
        {
            var host = new[] { R("h1", name: "A"), R("h2") };   // h2: name unreadable
            var v = Of(LinkDatumSeparators.Analyze(host, new[] { R("l1", name: "Z") }), "name");
            Assert.False(v.HostSafe);
            Assert.Equal(1, v.HostBlank);
            Assert.Null(LinkDatumSeparators.Best(new[] { v }));
            Assert.Contains("would not protect", v.Caveat);
            Assert.False(v.Usable);
        }

        [Fact]
        public void Unreadable_linked_values_are_counted_apart_not_as_separated()
        {
            var v = Of(LinkDatumSeparators.Analyze(new[] { R("h", name: "S") }, new[] { R("l1"), R("l2", name: "  ") }), "name");
            Assert.Equal(2, v.Unreadable);
            Assert.Equal(0, v.Separated);
        }

        [Fact]
        public void No_candidate_separates_when_everything_matches_and_the_report_says_what_to_do()
        {
            var host = new[] { R("h1", "W", "SB1", "A") };
            var linked = new[] { R("l1", "W2", "SB2", "A") };
            var v = LinkDatumSeparators.Analyze(host, linked).ToList();
            Assert.Null(LinkDatumSeparators.Best(v));
            var o = LinkDatumSeparators.ToJson("grid", v, 1);
            Assert.Equal("proposed_not_applied", (string)o["status"]);
            Assert.Contains("rename a host type", (string)o["means"]);
            Assert.NotNull(o["unmeasured"]);
        }

        [Fact]
        public void The_name_candidate_carries_the_future_datum_caveat()
        {
            var v = Of(LinkDatumSeparators.Analyze(new[] { R("h", name: "A") }, new[] { R("l", name: "Z") }), "name");
            Assert.True(v.HostSafe);
            Assert.Contains("created later", v.Caveat);
        }

        [Fact]
        public void No_host_datums_means_no_rule()
        {
            var v = Of(LinkDatumSeparators.Analyze(null, new[] { R("l", name: "L") }), "name");
            Assert.False(v.HostSafe);
            Assert.Null(LinkDatumSeparators.Best(new[] { v }));
        }

        [Fact]
        public void Name_not_filterable_in_this_revit_is_never_usable_nor_best()
        {
            var host = new[] { R("h", name: "A") };
            var linked = new[] { R("l", name: "Z") };
            var v = LinkDatumSeparators.Analyze(host, linked, null, nameFilterable: false);
            Assert.False(Of(v, "name").Usable);
            Assert.Null(LinkDatumSeparators.Best(v));
            Assert.Contains("not among the filterable", Of(v, "name").Caveat);
            var o = LinkDatumSeparators.ToJson("grid", v, 1, false);
            Assert.Contains("not in this category's filterable", (string)o["unmeasured"]);
            Assert.Equal(JTokenType.Null, o["best"].Type);
        }

        [Fact]
        public void Names_with_edge_spaces_are_unreadable_so_host_safe_cannot_over_claim()
        {
            var host = new[] { R("h1", name: "A"), R("h2", name: "B ") };
            var v = Of(LinkDatumSeparators.Analyze(host, new[] { R("l", name: " Z") }), "name");
            Assert.Equal(1, v.HostBlank);
            Assert.False(v.HostSafe);
            Assert.Equal(1, v.Unreadable);
            Assert.Equal(0, v.Separated);
        }

        [Theory]
        [InlineData("None", null)]
        [InlineData("Ninguno", null)]
        [InlineData("<None>", null)]
        [InlineData("  ", null)]
        [InlineData(null, null)]
        [InlineData("SB-Zone 1", "SB-Zone 1")]
        public void Scope_box_none_maps_to_no_value(string raw, string expected)
            => Assert.Equal(expected, LinkDatumSeparators.ScopeBoxName(raw));

        [Fact]
        public void Host_safe_is_omitted_for_candidates_not_compared_by_value()
        {
            var o = LinkDatumSeparators.ToJson("grid", LinkDatumSeparators.Analyze(new[] { R("h", "W", "S", "A") }, new[] { R("l", "W2", "S2", "A") }), 1);
            foreach (var c in (JArray)o["candidates"])
                Assert.Equal((bool)c["compares_by_value"], c["host_safe"] != null);
        }

        [Fact]
        public void The_command_requires_the_name_to_be_filterable_and_flags_the_nested_cap()
        {
            string src = System.IO.File.ReadAllText(Source("Commands", "ViewVisibilityLinkDatums.cs"));
            Assert.Contains("filterable.Contains(new ElementId(BuiltInParameter.DATUM_TEXT))", src);
            Assert.Contains("[\"nested_links_truncated\"] = nestedTruncated", src);
            Assert.Contains("ScopeBoxName(", src);
        }

        private static string Source(string folder, string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here), "..", "..", "src", "Horizun.Revit", folder, file));

        [Fact]
        public void The_cleanup_reports_separators_and_nested_links_without_hiding_either()
        {
            string src = System.IO.File.ReadAllText(Source("Commands", "ViewVisibilityLinkDatums.cs"));
            Assert.Contains("[\"same_type_name_separators\"] = separators", src);
            Assert.Contains("[\"nested_links\"] = nestedRows", src);
            Assert.Contains("link_type_name", src);   // the nested instance AND its RevitLinkType
            // Report only: the separators are computed outside Apply and nothing in Apply reads them.
            int apply = src.IndexOf("edit.Apply = d =>", System.StringComparison.Ordinal);
            Assert.DoesNotContain("LinkDatumSeparators", src.Substring(apply));
        }

        [Fact]
        public void Api_help_states_what_links_can_and_cannot_do()
        {
            foreach (string t in new[] { "RevitLinkInstance", "RevitLinkType", "RevitLinkGraphicsSettings" })
            {
                string all = string.Join(" ", ApiFieldNotes.For(t).Select(x => x.ToString()));
                Assert.Contains("CANNOT DO WITH LINKS", all);
                Assert.Contains("WHAT WORKS", all);
                Assert.Contains("link_datum_cleanup", all);
            }
        }
    }
}
