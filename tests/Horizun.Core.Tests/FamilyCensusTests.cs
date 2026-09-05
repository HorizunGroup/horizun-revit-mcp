// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Families and types, proved by running the rules. The two properties worth the
// most here are:
//
//   many types is an INDICATOR, never a weight and never a defect;
//   unreadable is its own count and is never folded into loadable.
//
// Both are the kind of mistake that makes a report look confident and wrong.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FamilyCensusTests
    {
        private static FamilyProfile P(string json) => FamilyCensusRules.Read(JToken.Parse(json));

        private static FamilyFact F(string name, string kind = FamilyKind.Loadable, string category = "Doors",
                                    int types = 1, int unused = 0, long instances = 1,
                                    bool? inPlace = false, bool? shared = false)
        {
            return new FamilyFact
            {
                ElementId = name == null ? -1 : name.GetHashCode() & 0xffff,
                Name = name,
                Kind = kind,
                Category = category,
                TypeCount = types,
                UnusedTypeCount = unused,
                InstanceCount = instances,
                IsInPlace = inPlace,
                IsShared = shared
            };
        }

        // ------------------------------------------------------------- kinds

        [Fact]
        public void An_unreadable_family_is_never_counted_as_a_loadable_one()
        {
            // THE ONE THAT MATTERS. A family nobody could classify is not loadable,
            // and adding it there reports more loadable families than the model has.
            JObject t = FamilyCensusRules.Totals(new[]
            {
                F("A"), F("B", FamilyKind.InPlace), F("Walls", FamilyKind.System),
                new FamilyFact { Name = null, NameReadable = false, Kind = FamilyKind.Unreadable }
            });

            Assert.Equal(4, t.Value<int>("families_total"));
            Assert.Equal(1, t.Value<int>("families_loadable"));
            Assert.Equal(1, t.Value<int>("families_in_place"));
            Assert.Equal(1, t.Value<int>("families_system"));
            Assert.Equal(1, t.Value<int>("families_unreadable"));
        }

        [Fact]
        public void A_system_family_is_reported_even_though_it_has_no_family_element()
        {
            // A wall type has no Family behind it, so a census built on
            // OfClass(Family) cannot see one and under-reports the model.
            FamilyFact sys = F("Basic Wall", FamilyKind.System, "Walls");
            sys.ElementId = -1;
            JObject j = FamilyCensusRules.ToJson(sys);
            Assert.Null(j["family_id"].Type == JTokenType.Null ? null : j["family_id"]);
            Assert.Equal(FamilyKind.System, j.Value<string>("kind"));
            Assert.Contains("has no Family behind it", FamilyCensusRules.KindsMean);
        }

        [Fact]
        public void In_place_is_reported_apart_from_loadable()
        {
            JObject j = FamilyCensusRules.ToJson(F("Ramp", FamilyKind.InPlace, inPlace: true));
            Assert.Equal(FamilyKind.InPlace, j.Value<string>("kind"));
            Assert.True(j.Value<bool>("is_in_place"));
        }

        [Fact]
        public void A_family_whose_shared_flag_could_not_be_read_is_null_and_counted()
        {
            // Never defaulted to false: FAMILY_SHARED is often absent, and absent
            // is not "this family is not shared".
            FamilyFact f = F("A", shared: null);
            Assert.Null(FamilyCensusRules.ToJson(f)["is_shared"].Value<bool?>());
            Assert.Equal(1, FamilyCensusRules.Totals(new[] { f }).Value<int>("families_shared_unreadable"));
        }

        [Fact]
        public void A_family_with_nothing_placed_reports_no_observed_nesting_depth()
        {
            // Null, not 0. Zero would claim a depth was observed and found to be
            // none, which is a different statement about a family nobody placed.
            FamilyFact f = F("A", instances: 0);
            f.NestedDepthObserved = null;
            Assert.Null(FamilyCensusRules.ToJson(f)["nested_depth_observed"].Value<int?>());
        }

        // -------------------------------------------------------- indicators

        [Fact]
        public void Candidates_are_indicators_and_say_so_in_every_row()
        {
            JObject c = FamilyCensusRules.Candidates(new[] { F("Big", types: 40), F("Small") }, 5);
            JArray rows = (JArray)c["selected"];
            Assert.Equal("Big", rows[0].Value<string>("name"));
            Assert.All(rows, r => Assert.Equal(EvidenceClass.Indicator, r.Value<string>("evidence")));
            Assert.Contains("not a measure of how much file", rows[0].Value<string>("why"));
        }

        [Fact]
        public void The_census_says_it_never_opened_a_family_and_never_weighed_one()
        {
            Assert.Contains("does not open family documents", FamilyCensusRules.IndicatorMeans);
            Assert.Contains("reports no file size", FamilyCensusRules.IndicatorMeans);
        }

        [Fact]
        public void A_triage_states_how_many_families_it_passed_over()
        {
            // A budget that does not say what it skipped reads as a complete list.
            JObject c = FamilyCensusRules.Candidates(
                new[] { F("a", types: 9), F("b", types: 8), F("c", types: 7) }, 2);

            Assert.Equal(3, c.Value<int>("ranked"));
            Assert.Equal(2, ((JArray)c["selected"]).Count);
            Assert.Equal(1, c.Value<int>("not_selected"));
            Assert.Equal(((JArray)c["selected"]).Count + c.Value<int>("not_selected"), c.Value<int>("ranked"));
            Assert.False(string.IsNullOrWhiteSpace(c.Value<string>("selection_rule")));
        }

        [Fact]
        public void A_budget_of_zero_selects_nothing_and_says_everything_was_passed_over()
        {
            JObject c = FamilyCensusRules.Candidates(new[] { F("a"), F("b") }, 0);
            Assert.Empty((JArray)c["selected"]);
            Assert.Equal(2, c.Value<int>("not_selected"));
        }

        [Fact]
        public void The_ranking_is_stable_so_two_runs_of_one_model_agree()
        {
            var fam = new[] { F("z", types: 5), F("a", types: 5), F("m", types: 5) };
            JArray a = (JArray)FamilyCensusRules.Candidates(fam, 3)["selected"];
            JArray b = (JArray)FamilyCensusRules.Candidates(fam, 3)["selected"];
            Assert.Equal(a.Select(x => x.Value<string>("name")), b.Select(x => x.Value<string>("name")));
            Assert.Equal("a", a[0].Value<string>("name"));
        }

        // ------------------------------------------------------------ judging

        [Fact]
        public void With_no_profile_nothing_is_a_violation()
        {
            FamilyProfile p = FamilyCensusRules.Read(null);
            Assert.True(p.Absent);
            Assert.False(p.Ok);
            Assert.Contains("NONE of them is a violation", p.Message);
            Assert.Empty(FamilyCensusRules.Judge(new[] { F("A", types: 900, instances: 90000) }, p));
        }

        [Fact]
        public void A_type_with_no_instances_is_found_only_when_a_ceiling_was_declared()
        {
            var f = F("A", types: 5, unused: 4);
            Assert.Empty(FamilyCensusRules.Judge(new[] { f }, FamilyCensusRules.Read(null)));

            List<FamilyFinding> found = FamilyCensusRules.Judge(
                new[] { f }, P(@"{ ""version"": ""v1"", ""max_unused_types"": 1 }"));
            Assert.Equal(FamilyFindingCodes.TooManyUnusedTypes, Assert.Single(found).Code);
        }

        [Fact]
        public void A_family_with_many_types_is_a_finding_only_against_a_declared_maximum()
        {
            List<FamilyFinding> found = FamilyCensusRules.Judge(
                new[] { F("A", types: 30) }, P(@"{ ""version"": ""v1"", ""max_types"": 10 }"));
            Assert.Equal(FamilyFindingCodes.TooManyTypes, Assert.Single(found).Code);
        }

        [Fact]
        public void An_in_place_family_is_judged_only_where_the_caller_forbade_one()
        {
            FamilyProfile p = P(@"{ ""version"": ""v1"",
                ""in_place_allowed_by_category"": { ""Walls"": false, ""Doors"": true } }");

            Assert.Single(FamilyCensusRules.Judge(
                new[] { F("W", FamilyKind.InPlace, "Walls", inPlace: true) }, p));
            Assert.Empty(FamilyCensusRules.Judge(
                new[] { F("D", FamilyKind.InPlace, "Doors", inPlace: true) }, p));
            // A category the caller said nothing about is not a violation.
            Assert.Empty(FamilyCensusRules.Judge(
                new[] { F("F", FamilyKind.InPlace, "Furniture", inPlace: true) }, p));
        }

        [Fact]
        public void A_family_whose_in_place_flag_is_unreadable_is_not_reported_as_in_place()
        {
            // null is not true. Reporting it would invent a forbidden in-place
            // family out of a read that failed.
            FamilyProfile p = P(@"{ ""version"": ""v1"", ""in_place_allowed_by_category"": { ""Walls"": false } }");
            Assert.Empty(FamilyCensusRules.Judge(
                new[] { F("W", FamilyKind.Unreadable, "Walls", inPlace: null) }, p));
        }

        [Fact]
        public void A_family_expected_to_be_shared_is_flagged_only_when_the_model_says_it_is_not()
        {
            FamilyProfile p = P(@"{ ""version"": ""v1"", ""expected_shared_families"": [""Panel""] }");
            Assert.Single(FamilyCensusRules.Judge(new[] { F("Panel", shared: false) }, p));
            Assert.Empty(FamilyCensusRules.Judge(new[] { F("Panel", shared: true) }, p));
            // Unreadable is not "not shared".
            Assert.Empty(FamilyCensusRules.Judge(new[] { F("Panel", shared: null) }, p));
        }

        [Fact]
        public void An_explicit_exception_is_honoured()
        {
            FamilyProfile p = P(@"{ ""version"": ""v1"", ""max_types"": 1, ""exceptions"": [""Catalogue""] }");
            Assert.Empty(FamilyCensusRules.Judge(new[] { F("Catalogue", types: 99) }, p));
            Assert.Single(FamilyCensusRules.Judge(new[] { F("Other", types: 99) }, p));
        }

        [Fact]
        public void A_category_outside_the_allowed_list_is_reported()
        {
            FamilyProfile p = P(@"{ ""version"": ""v1"", ""allowed_categories"": [""Doors""] }");
            Assert.Empty(FamilyCensusRules.Judge(new[] { F("A", category: "Doors") }, p));
            Assert.Equal(FamilyFindingCodes.CategoryNotAllowed,
                Assert.Single(FamilyCensusRules.Judge(new[] { F("B", category: "Windows") }, p)).Code);
        }

        [Fact]
        public void A_family_with_no_category_is_not_judged_against_a_category_rule()
        {
            FamilyProfile p = P(@"{ ""version"": ""v1"", ""allowed_categories"": [""Doors""] }");
            Assert.Empty(FamilyCensusRules.Judge(new[] { F("A", category: null) }, p));
        }

        // ----------------------------------------------------------- refusals

        [Fact]
        public void A_profile_without_a_version_is_refused()
        {
            Assert.Equal(FamilyProfileCodes.NoVersion, P(@"{ ""max_types"": 3 }").Code);
        }

        [Fact]
        public void An_unknown_key_refuses_the_whole_profile_with_the_offender_named()
        {
            FamilyProfile p = P(@"{ ""version"": ""v1"", ""max_typez"": 3 }");
            Assert.Equal(FamilyProfileCodes.UnknownKey, p.Code);
            Assert.Contains("max_typez", p.Message);
        }

        [Fact]
        public void An_empty_allowed_categories_list_is_refused_rather_than_banning_everything()
        {
            FamilyProfile p = P(@"{ ""version"": ""v1"", ""allowed_categories"": [] }");
            Assert.Equal(FamilyProfileCodes.BadRule, p.Code);
            Assert.Contains("forbid every category", p.Message);
        }

        [Fact]
        public void A_negative_maximum_is_refused()
        {
            Assert.Equal(FamilyProfileCodes.BadRule, P(@"{ ""version"": ""v1"", ""max_types"": -1 }").Code);
        }

        [Fact]
        public void A_refused_profile_is_not_applied_even_though_it_parsed_earlier_rules()
        {
            // Read fills the rules as it goes and only THEN meets the bad key.
            // Enforcing what it collected would judge a model against a profile the
            // caller was told had been rejected.
            FamilyProfile p = P(@"{ ""version"": ""v1"", ""max_types"": 1, ""bogus"": 2 }");
            Assert.False(p.Ok);
            Assert.Equal(1, p.MaxTypes);                 // it really did parse one
            Assert.Empty(FamilyCensusRules.Judge(new[] { F("A", types: 99) }, p));
        }

        // ----------------------------------------------------------- coverage

        [Fact]
        public void An_unreadable_type_or_instance_makes_the_family_incomplete()
        {
            var f = F("A");
            Assert.True(f.CoverageComplete);
            f.UnreadableTypeCount = 1;
            Assert.False(f.CoverageComplete);
            Assert.False(FamilyCensusRules.Totals(new[] { f }).Value<bool>("coverage_complete"));
        }

        [Fact]
        public void Instances_and_types_that_could_not_be_read_are_reported_in_the_totals()
        {
            // Their own scalars. Folded into the readable counts they would inflate
            // what the census claims to have seen; dropped entirely, a reader has
            // no way to know the totals are bounds.
            var f = F("A");
            f.UnreadableInstanceCount = 7;
            f.UnreadableTypeCount = 3;

            JObject t = FamilyCensusRules.Totals(new[] { f });
            Assert.Equal(7, t.Value<long>("instances_unreadable"));
            Assert.Equal(3, t.Value<long>("types_unreadable"));
            Assert.False(t.Value<bool>("coverage_complete"));
        }

        [Fact]
        public void A_document_with_no_families_reports_zeros_and_complete_coverage()
        {
            // Genuinely nothing, which IS zero - distinct from a walk that failed.
            JObject t = FamilyCensusRules.Totals(new FamilyFact[0]);
            Assert.Equal(0, t.Value<int>("families_total"));
            Assert.True(t.Value<bool>("coverage_complete"));
            Assert.Empty((JArray)FamilyCensusRules.Candidates(new FamilyFact[0], 10)["selected"]);
        }

        [Fact]
        public void Workset_and_host_distributions_are_ranked_and_stable()
        {
            var f = F("A");
            f.WorksetDistribution["z"] = 2;
            f.WorksetDistribution["a"] = 2;
            f.HostDistribution["Walls"] = 5;
            JObject j = FamilyCensusRules.ToJson(f);
            Assert.Equal("a", ((JArray)j["workset_distribution"])[0].Value<string>("workset"));
            Assert.Equal("Walls", ((JArray)j["host_distribution"])[0].Value<string>("host_category"));
        }

        [Fact]
        public void A_family_whose_parameters_could_not_be_read_reports_null_not_zero()
        {
            var f = F("A");
            f.ParametersReadable = false;
            Assert.Null(FamilyCensusRules.ToJson(f)["parameter_count"].Value<int?>());
        }
    }
}
