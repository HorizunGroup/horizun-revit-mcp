// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Warning identity, proved by running it. The two tests that matter most are
// the pair at the top: same guid / different text must be ONE group, and same
// text / different guid must be TWO. A tool that groups by the description
// gets both of them wrong, and gets them wrong quietly.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WarningRulesTests
    {
        private const string GuidA = "6a1c0b2e-1111-4c3d-9e5f-000000000001";
        private const string GuidB = "6a1c0b2e-2222-4c3d-9e5f-000000000002";

        private static WarningFact W(string guid, string desc, string severity = "Warning",
                                     long[] ids = null, bool idsReadable = true)
        {
            return new WarningFact
            {
                DefinitionGuid = guid,
                Description = desc,
                Severity = severity,
                FailingElementIds = (ids ?? new long[0]).ToList(),
                IdsReadable = idsReadable,
                IdsError = idsReadable ? null : "GetFailingElements failed"
            };
        }

        // ------------------------------------------------------------ identity

        [Fact]
        public void One_warning_in_two_languages_is_one_group()
        {
            // THE LOCALIZATION TRAP. Grouping by text reports this model as having
            // two problems when Revit says it has one, twice.
            List<WarningGroup> g = WarningRules.Group(new[]
            {
                W(GuidA, "Highlighted elements are joined but do not intersect"),
                W(GuidA, "Los elementos resaltados estan unidos pero no se intersecan")
            });

            WarningGroup only = Assert.Single(g);
            Assert.Equal(2, only.Occurrences);
            Assert.True(only.IdentityIsStable);
            Assert.Equal(2, only.DistinctDescriptions);
        }

        [Fact]
        public void Two_different_warnings_that_read_alike_stay_two_groups()
        {
            // The mirror image: text-grouping MERGES genuinely different failures
            // and hides one of them entirely.
            List<WarningGroup> g = WarningRules.Group(new[]
            {
                W(GuidA, "Elements have duplicate mark values"),
                W(GuidB, "Elements have duplicate mark values")
            });
            Assert.Equal(2, g.Count);
            Assert.All(g, x => Assert.Equal(1, x.Occurrences));
        }

        [Fact]
        public void An_unreadable_guid_falls_back_to_text_and_admits_it()
        {
            // A fallback nobody is told about is the original defect renamed.
            WarningGroup only = Assert.Single(WarningRules.Group(new[] { W(null, "Something happened") }));
            Assert.False(only.IdentityIsStable);
            Assert.Null(only.DefinitionGuid);
            Assert.Contains("identity_is_stable", WarningRules.IdentityMeans);
        }

        [Fact]
        public void A_readable_and_an_unreadable_guid_do_not_merge()
        {
            // The unreadable one might BE the readable one. Merging asserts that;
            // keeping them apart only asserts we could not tell.
            List<WarningGroup> g = WarningRules.Group(new[]
            {
                W(GuidA, "Same words"),
                W(null, "Same words")
            });
            Assert.Equal(2, g.Count);
        }

        [Fact]
        public void A_description_that_reads_like_a_guid_does_not_collide_with_that_guid()
        {
            // The namespace prefix on text keys is not decoration. Without it, a
            // warning that could not report its id and whose DESCRIPTION happens to
            // be a guid lands in that guid's group, and its occurrences are
            // attributed to a different failure entirely.
            List<WarningGroup> g = WarningRules.Group(new[]
            {
                W(GuidA, "a real failure"),
                W(null, GuidA)
            });
            Assert.Equal(2, g.Count);
        }

        [Fact]
        public void The_guid_key_is_case_insensitive_so_one_warning_is_not_two()
        {
            List<WarningGroup> g = WarningRules.Group(new[]
            {
                W(GuidA.ToUpperInvariant(), "x"),
                W(GuidA.ToLowerInvariant(), "x")
            });
            Assert.Single(g);
        }

        // -------------------------------------------------------------- counts

        [Fact]
        public void Occurrences_counts_warnings_and_says_so_because_elements_are_a_different_number()
        {
            WarningGroup only = Assert.Single(WarningRules.Group(new[]
            {
                W(GuidA, "x", ids: new long[] { 1, 2, 3 }),
                W(GuidA, "x", ids: new long[] { 4, 5 })
            }));
            Assert.Equal(2, only.Occurrences);            // two warnings
            Assert.Equal(5, only.FailingElementIds.Count); // five elements
            Assert.Contains("not elements", WarningRules.OccurrencesMeans);
        }

        [Fact]
        public void The_same_element_named_twice_is_listed_once()
        {
            WarningGroup only = Assert.Single(WarningRules.Group(new[]
            {
                W(GuidA, "x", ids: new long[] { 7, 8 }),
                W(GuidA, "x", ids: new long[] { 8, 9 })
            }));
            Assert.Equal(new long[] { 7, 8, 9 }, only.FailingElementIds.ToArray());
        }

        [Fact]
        public void An_unreadable_id_list_makes_the_group_incomplete_but_not_the_count()
        {
            // We counted the warning. We could not ask which elements it names.
            // Those are different failures and only one of them is present.
            WarningGroup only = Assert.Single(WarningRules.Group(new[]
            {
                W(GuidA, "x", ids: new long[] { 1 }),
                W(GuidA, "x", idsReadable: false)
            }));
            Assert.Equal(2, only.Occurrences);
            Assert.False(only.IdsComplete);
            Assert.NotNull(only.IdsError);
        }

        [Fact]
        public void A_clean_group_reports_its_ids_as_complete()
        {
            WarningGroup only = Assert.Single(WarningRules.Group(new[] { W(GuidA, "x", ids: new long[] { 1 }) }));
            Assert.True(only.IdsComplete);
            Assert.Null(only.IdsError);
        }

        [Fact]
        public void No_warnings_is_no_groups_rather_than_a_throw()
        {
            Assert.Empty(WarningRules.Group(new WarningFact[0]));
            Assert.Empty(WarningRules.Group(null));
        }

        [Fact]
        public void Groups_are_ordered_by_occurrences_then_stably_by_key()
        {
            var facts = new List<WarningFact> { W(GuidB, "b"), W(GuidA, "a"), W(GuidA, "a") };
            List<WarningGroup> a = WarningRules.Group(facts);
            List<WarningGroup> b = WarningRules.Group(facts);
            Assert.Equal(2, a[0].Occurrences);
            Assert.Equal(a.Select(x => x.Key), b.Select(x => x.Key));
        }

        // ------------------------------------------------------------- profile

        [Fact]
        public void No_profile_means_no_warning_was_triaged_and_it_is_not_a_pass()
        {
            WarningProfile p = WarningRules.ReadProfile(null);
            Assert.True(p.Absent);
            Assert.False(p.Ok);
            Assert.Contains("NOT a pass", p.Message);

            List<WarningGroup> g = WarningRules.Group(new[] { W(GuidA, "x") });
            WarningRules.Triage(g, p);
            Assert.Null(g[0].CallerSeverity);
        }

        [Fact]
        public void A_profile_keyed_on_the_description_is_refused_with_the_reason()
        {
            // Allowing this rebuilds the fragility the whole file removes, and it
            // would fail silently on the next upgrade rather than here.
            WarningProfile p = WarningRules.ReadProfile(JToken.Parse(
                @"{ ""version"": ""v1"", ""Elements are joined but do not intersect"": { ""severity"": ""low"" } }"));
            Assert.False(p.Ok);
            Assert.Equal(WarningCodes.BadKey, p.Code);
            Assert.Contains("stops matching silently", p.Message);
        }

        [Fact]
        public void A_profile_without_a_version_is_refused()
        {
            WarningProfile p = WarningRules.ReadProfile(JToken.Parse(
                @"{ """ + GuidA + @""": { ""severity"": ""high"" } }"));
            Assert.Equal(WarningCodes.NoVersion, p.Code);
        }

        [Fact]
        public void An_unknown_rule_key_is_refused_rather_than_ignored()
        {
            WarningProfile p = WarningRules.ReadProfile(JToken.Parse(
                @"{ ""version"": ""v1"", """ + GuidA + @""": { ""severty"": ""high"" } }"));
            Assert.Equal(WarningCodes.UnknownKey, p.Code);
            Assert.Contains("severity, label", p.Message);
        }

        [Fact]
        public void A_refused_profile_is_not_applied_even_though_it_parsed_some_entries()
        {
            // ReadProfile fills ByGuid as it goes and only THEN meets the bad key,
            // so a refused profile can arrive holding real entries. Applying them
            // would triage the model against rules the caller was told were
            // rejected. Found by a mutation that went VACUOUS on the workset rules,
            // which have the identical shape.
            WarningProfile p = WarningRules.ReadProfile(JToken.Parse(
                @"{ ""version"": ""v1"",
                    """ + GuidA + @""": { ""severity"": ""blocking"" },
                    """ + GuidB + @""": { ""severty"": ""typo"" } }"));

            Assert.False(p.Ok);
            Assert.Equal(WarningCodes.UnknownKey, p.Code);
            Assert.NotEmpty(p.ByGuid);                       // it really did parse one

            List<WarningGroup> g = WarningRules.Group(new[] { W(GuidA, "x") });
            WarningRules.Triage(g, p);
            Assert.Null(g[0].CallerSeverity);
        }

        [Fact]
        public void A_triaged_warning_carries_the_callers_severity_beside_revits_own()
        {
            // Both, never one replacing the other: Revit's severity is a fact and
            // the caller's is an opinion, and a reader needs to tell them apart.
            WarningProfile p = WarningRules.ReadProfile(JToken.Parse(
                @"{ ""version"": ""v1"", """ + GuidA + @""": { ""severity"": ""blocking"", ""label"": ""Fix before issue"" } }"));
            Assert.True(p.Ok, p.Message);

            List<WarningGroup> g = WarningRules.Group(new[] { W(GuidA, "x", severity: "Warning") });
            WarningRules.Triage(g, p);
            Assert.Equal("blocking", g[0].CallerSeverity);
            Assert.Equal("Fix before issue", g[0].CallerLabel);
            Assert.Equal("Warning", g[0].Severity);
        }

        [Fact]
        public void A_warning_the_profile_is_silent_about_gets_no_invented_severity()
        {
            WarningProfile p = WarningRules.ReadProfile(JToken.Parse(
                @"{ ""version"": ""v1"", """ + GuidA + @""": { ""severity"": ""blocking"" } }"));
            List<WarningGroup> g = WarningRules.Group(new[] { W(GuidB, "other") });
            WarningRules.Triage(g, p);
            Assert.Null(g[0].CallerSeverity);
        }

        [Fact]
        public void The_reply_publishes_both_identities_and_both_severities()
        {
            List<WarningGroup> g = WarningRules.Group(new[] { W(GuidA, "x", ids: new long[] { 5 }) });
            JObject j = WarningRules.ToJson(g[0]);
            Assert.Equal(GuidA, j.Value<string>("failure_definition_guid"));
            Assert.True(j.Value<bool>("identity_is_stable"));
            Assert.Equal("Warning", j.Value<string>("revit_severity"));
            Assert.Null(j.Value<string>("caller_severity"));
            Assert.True(j.Value<bool>("failing_element_ids_complete"));
        }
    }
}
