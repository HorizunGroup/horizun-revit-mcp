// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Weight attribution, proved by behaviour. The properties that matter are the
// refusals: no bytes, no built-in opinion, and a contributor nobody could count
// never coming back as zero.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WeightAttributionTests
    {
        private static readonly string[] Kinds =
        {
            "in_place_families", "groups", "imported_cad", "images", "views", "warnings"
        };

        private static Contributor C(string kind, long count, string status = ContributorStatus.Counted,
                                     long unreadable = 0, long examined = 0, string limitation = null,
                                     string cls = EvidenceClass.Measured)
        {
            return new Contributor
            {
                Kind = kind, Count = count, Status = status, Unreadable = unreadable,
                Examined = examined == 0 ? count : examined, Limitation = limitation, Class = cls,
                Evidence = new List<string> { kind + ":1", kind + ":2" },
            };
        }

        private static WeightProfile Profile(string json) =>
            WeightAttributionRules.ReadProfile(JToken.Parse(json), Kinds);

        // ------------------------------------------------------------ profile

        [Fact]
        public void Without_a_profile_the_candidates_are_reported_but_NOT_ranked()
        {
            // No built-in default: that would be one organisation's opinion about
            // what makes a model heavy, compiled into a neutral bridge.
            WeightProfile p = WeightAttributionRules.ReadProfile(null, Kinds);
            Assert.False(p.Ok);
            Assert.Equal(WeightCodes.NoProfile, p.Code);

            WeightAttribution a = WeightAttributionRules.Attribute(
                new[] { C("groups", 900), C("views", 12) }, p);

            Assert.False(a.Ranked);
            Assert.Equal(2, a.Candidates.Count);          // the facts are still there
            Assert.All(a.Candidates, c => Assert.Equal(0, c.Score));
            Assert.Contains("alphabetical", a.Candidates[0].WhyItRanks);
            Assert.Contains("no weight profile", a.WhyNotRanked);
        }

        [Fact]
        public void A_profile_without_a_version_is_refused()
        {
            WeightProfile p = Profile(@"{ ""weights"": { ""groups"": 2 } }");
            Assert.False(p.Ok);
            Assert.Equal(WeightCodes.NoProfileVersion, p.Code);
        }

        [Fact]
        public void A_weight_for_a_kind_that_does_not_exist_is_refused()
        {
            // Silently doing nothing is how somebody believes they weighted a
            // contributor they never weighted.
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""gropus"": 2 } }");
            Assert.False(p.Ok);
            Assert.Equal(WeightCodes.UnknownProfileKey, p.Code);
            Assert.Contains("groups", p.Message);
        }

        [Fact]
        public void An_unknown_key_in_the_profile_object_is_refused()
        {
            Assert.Equal(WeightCodes.UnknownProfileKey,
                Profile(@"{ ""version"": ""v1"", ""weights"": {}, ""notes"": ""x"" }").Code);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData(@"""two""")]
        public void A_weight_that_is_negative_or_not_a_number_is_refused(string raw)
        {
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": " + raw + " } }");
            Assert.False(p.Ok);
            Assert.Equal(WeightCodes.BadWeight, p.Code);
        }

        [Fact]
        public void A_negative_weight_is_refused_with_the_reason_that_matters()
        {
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": -5 } }");
            Assert.Contains("cancel", p.Message);   // one contributor cancelling another out
        }

        // ------------------------------------------------------------ ranking

        [Fact]
        public void Candidates_rank_by_count_times_weight_and_say_so()
        {
            WeightProfile p = Profile(@"{ ""version"": ""org-v3"",
                ""weights"": { ""in_place_families"": 10, ""groups"": 1, ""views"": 0 } }");
            Assert.True(p.Ok);

            WeightAttribution a = WeightAttributionRules.Attribute(
                new[] { C("groups", 500), C("in_place_families", 60), C("views", 9000) }, p);

            Assert.True(a.Ranked);
            Assert.Equal("org-v3", a.ProfileVersion);
            Assert.Equal("in_place_families", a.Candidates[0].Kind);   // 60 x 10 = 600
            Assert.Equal("groups", a.Candidates[1].Kind);              // 500 x 1 = 500
            Assert.Equal("views", a.Candidates[2].Kind);               // 9000 x 0 = 0

            Assert.Contains("org-v3", a.Candidates[0].WhyItRanks);
            Assert.Contains("600", a.Candidates[0].WhyItRanks);
            Assert.Contains("weight of 0", a.Candidates[2].WhyItRanks);
        }

        [Fact]
        public void The_order_is_total_so_two_runs_agree()
        {
            // Equal scores must not depend on enumeration order.
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": 1, ""views"": 1 } }");
            WeightAttribution one = WeightAttributionRules.Attribute(new[] { C("views", 5), C("groups", 5) }, p);
            WeightAttribution two = WeightAttributionRules.Attribute(new[] { C("groups", 5), C("views", 5) }, p);
            Assert.Equal(one.Candidates.Select(c => c.Kind), two.Candidates.Select(c => c.Kind));
            Assert.Equal("groups", one.Candidates[0].Kind);
        }

        // -------------------------------------------------- the epistemic line

        [Fact]
        public void A_contributor_nobody_could_count_is_never_reported_as_zero()
        {
            // THE ONE THAT MATTERS. A heavy model whose heaviest category was
            // unreadable must not come back looking light.
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": 1, ""imported_cad"": 100 } }");
            WeightAttribution a = WeightAttributionRules.Attribute(new[]
            {
                C("groups", 3),
                C("imported_cad", 0, ContributorStatus.NotAssessable,
                  limitation: "the CAD link collector threw"),
            }, p);

            Assert.Single(a.Candidates);
            Assert.Equal("groups", a.Candidates[0].Kind);

            Assert.Single(a.NotAssessable);
            RankedContributor cad = a.NotAssessable[0];
            Assert.Equal("imported_cad", cad.Kind);
            Assert.Equal(ContributorStatus.NotAssessable, cad.Status);
            Assert.Contains("could not be counted", cad.WhyItRanks);
            Assert.Contains("nothing here", cad.WhyItRanks);   // it names the misreading
            Assert.Contains("imported_cad", string.Join(" ", a.Limitations));
        }

        [Fact]
        public void Not_requested_is_a_different_answer_from_not_assessable()
        {
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": 1 } }");
            WeightAttribution a = WeightAttributionRules.Attribute(new[]
            {
                C("images", 0, ContributorStatus.NotRequested),
            }, p);

            Assert.Empty(a.Candidates);
            Assert.Single(a.NotAssessable);
            Assert.Contains("not requested", a.NotAssessable[0].WhyItRanks);
            Assert.DoesNotContain("could not be counted", a.NotAssessable[0].WhyItRanks);
        }

        [Fact]
        public void A_partly_unreadable_population_ranks_as_a_lower_bound_and_says_so()
        {
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": 2 } }");
            WeightAttribution a = WeightAttributionRules.Attribute(new[]
            {
                C("groups", 40, ContributorStatus.LowerBound, unreadable: 7, examined: 47),
            }, p);

            Assert.True(a.Ranked);
            Assert.Equal(ContributorStatus.LowerBound, a.Candidates[0].Status);
            Assert.Contains("LOWER BOUND", a.Candidates[0].WhyItRanks);
            Assert.Contains("7", a.Candidates[0].WhyItRanks);
            Assert.Contains("lower bound", string.Join(" ", a.Limitations));
        }

        [Fact]
        public void The_reply_says_out_loud_that_it_is_not_measuring_bytes()
        {
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": 1 } }");
            JObject j = WeightAttributionRules.Attribute(new[] { C("groups", 2) }, p).ToJson();

            string note = j.Value<string>("bytes_are_not_known");
            Assert.Contains("does not publish", note);
            Assert.Contains("nothing here is a size", note);

            // and no field anywhere claims one
            Assert.DoesNotContain("\"bytes\"", j.ToString());
            Assert.DoesNotContain("\"mb\"", j.ToString().ToLowerInvariant());
        }

        [Fact]
        public void Every_row_carries_the_class_of_claim_it_is()
        {
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": 1, ""in_place_families"": 3 } }");
            WeightAttribution a = WeightAttributionRules.Attribute(new[]
            {
                C("groups", 5),
                C("in_place_families", 4, cls: EvidenceClass.Indicator),
            }, p);

            Assert.Equal(EvidenceClass.Indicator,
                a.Candidates.Single(c => c.Kind == "in_place_families").Class);
            Assert.Equal(EvidenceClass.Measured,
                a.Candidates.Single(c => c.Kind == "groups").Class);
        }

        [Fact]
        public void Evidence_travels_with_every_row_so_somebody_can_go_and_look()
        {
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": { ""groups"": 1 } }");
            WeightAttribution a = WeightAttributionRules.Attribute(new[] { C("groups", 2) }, p);
            Assert.NotEmpty(a.Candidates[0].Evidence);
        }

        [Fact]
        public void An_empty_model_produces_an_empty_ranking_not_a_crash()
        {
            WeightProfile p = Profile(@"{ ""version"": ""v1"", ""weights"": {} }");
            WeightAttribution a = WeightAttributionRules.Attribute(null, p);
            Assert.True(a.Ranked);
            Assert.Empty(a.Candidates);
            Assert.Empty(a.NotAssessable);
        }
    }
}
