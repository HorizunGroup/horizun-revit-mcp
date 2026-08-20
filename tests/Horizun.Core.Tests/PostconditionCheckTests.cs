// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// create_schedule declared verified_applied on three of the five properties its
// own request carries. The schedule's NAME and its CATEGORY were never re-read -
// the reply reported `category` off the Category object resolved BEFORE the
// commit, which is the request talking back rather than the model.
//
// The checks themselves are equality. What is worth pinning is what a set of them
// ADDS UP TO, because that is where a boolean lies, and the audit of this file
// found a second way after the first fix: `Count > 0 && allMeasured && allMatched`
// proves that some checks passed, not that the RIGHT ones ran. Five checks with
// "name" twice and no "category" satisfied it, and so did four checks after
// somebody deleted the fifth. Hence a checklist that knows what it must cover.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PostconditionCheckTests
    {
        /// <summary>The properties create_schedule's request carries.</summary>
        private static readonly string[] Five =
            { "name", "category", "fields", "include_links", "itemized" };

        private static PostconditionCheck AllFiveMatching()
        {
            return new PostconditionCheck(Five)
                .Compare("name", "Wall Schedule", "Wall Schedule")
                .Record("category", 100L, 100L, true)
                .Record("fields", new JArray("Family", "Count"), new JArray("Family", "Count"), true)
                .Compare("include_links", true, true)
                .Compare("itemized", false, false);
        }

        [Fact]
        public void Exactly_the_required_properties_re_read_and_matching_is_the_only_pass()
        {
            PostconditionCheck check = AllFiveMatching();

            Assert.True(check.AllVerified);
            Assert.True(check.AllMeasured);
            Assert.Equal(5, check.Count);
            Assert.Empty(check.Missing);
            Assert.Empty(check.Unexpected);
            Assert.True(check.ToJson().Value<bool>("all_verified"));
        }

        // ---- The two properties that were never checked -----------------------

        [Fact]
        public void A_schedule_that_committed_under_a_different_name_is_not_verified()
        {
            PostconditionCheck check = new PostconditionCheck(Five)
                .Compare("name", "Wall Schedule", "Wall Schedule 1")
                .Record("category", 100L, 100L, true)
                .Record("fields", new JArray("Family"), new JArray("Family"), true)
                .Compare("include_links", true, true)
                .Compare("itemized", false, false);

            Assert.False(check.AllVerified);
            Assert.True(check.AllMeasured); // measured mismatch -> partial, not unknown

            JToken name = check.ToJson()["properties"][0];
            Assert.Equal("name", name.Value<string>("property"));
            Assert.False(name.Value<bool>("matches"));
            Assert.Equal("Wall Schedule", name.Value<string>("requested"));
            Assert.Equal("Wall Schedule 1", name.Value<string>("found_in_committed_model"));
        }

        [Fact]
        public void A_schedule_committed_against_a_different_category_is_not_verified()
        {
            // The comparison is by ID because two categories can share a display name
            // across disciplines, so a name check would pass over exactly the substitution
            // that matters.
            PostconditionCheck check = new PostconditionCheck(Five)
                .Compare("name", "Wall Schedule", "Wall Schedule")
                .Record("category",
                        new JObject { ["id"] = 100L, ["name"] = "Walls" },
                        new JObject { ["id"] = 220L, ["name"] = "Walls" },
                        false)
                .Record("fields", new JArray("Family"), new JArray("Family"), true)
                .Compare("include_links", true, true)
                .Compare("itemized", false, false);

            Assert.False(check.AllVerified);
            Assert.True(check.AllMeasured);
        }

        [Theory]
        [InlineData("name")]
        [InlineData("category")]
        [InlineData("fields")]
        [InlineData("include_links")]
        [InlineData("itemized")]
        public void Any_single_property_failing_blocks_the_whole_checklist(string failing)
        {
            var check = new PostconditionCheck(Five);
            foreach (string property in Five)
                check.Record(property, "requested", "found", property != failing);

            Assert.False(check.AllVerified);
        }

        // ---- COVERAGE: the checklist knows what it must cover ------------------

        [Theory]
        [InlineData("name")]
        [InlineData("category")]
        [InlineData("fields")]
        [InlineData("include_links")]
        [InlineData("itemized")]
        public void A_required_property_that_was_never_checked_blocks_the_checklist(string deleted)
        {
            // Somebody deletes one comparison from the command. Every remaining check
            // passes, and before the required set that was a verified schedule.
            var check = new PostconditionCheck(Five);
            foreach (string property in Five.Where(p => p != deleted))
                check.Record(property, "same", "same", true);

            Assert.False(check.AllVerified);
            Assert.Equal(new[] { deleted }, check.Missing.ToArray());
            Assert.Contains(deleted, check.ToJson()["missing"].Select(t => (string)t));
        }

        [Fact]
        public void A_duplicated_property_covering_for_a_missing_one_blocks_the_checklist()
        {
            // Five checks, all passing, all measured - and the category was never compared.
            PostconditionCheck check = new PostconditionCheck(Five)
                .Compare("name", "S", "S")
                .Compare("name", "S", "S")
                .Record("fields", new JArray("Family"), new JArray("Family"), true)
                .Compare("include_links", true, true)
                .Compare("itemized", false, false);

            Assert.Equal(5, check.Count);
            Assert.False(check.AllVerified);
            Assert.Contains("name", check.Unexpected);
            Assert.Contains("category", check.Missing);
        }

        [Fact]
        public void A_property_substituted_for_a_required_one_blocks_the_checklist()
        {
            PostconditionCheck check = new PostconditionCheck(Five)
                .Compare("name", "S", "S")
                .Compare("colour", "blue", "blue")           // not required, and category is gone
                .Record("fields", new JArray("Family"), new JArray("Family"), true)
                .Compare("include_links", true, true)
                .Compare("itemized", false, false);

            Assert.False(check.AllVerified);
            Assert.Contains("colour", check.Unexpected);
            Assert.Contains("category", check.Missing);
        }

        [Fact]
        public void A_duplicate_blocks_even_when_nothing_is_missing()
        {
            var check = new PostconditionCheck(Five);
            foreach (string property in Five) check.Record(property, "same", "same", true);
            check.Record("name", "same", "same", true);      // one honest check, recorded twice

            Assert.False(check.AllVerified);
            Assert.Empty(check.Missing);
            Assert.Contains("name", check.Unexpected);
        }

        // ---- The three ways a boolean says true without meaning it -------------

        [Fact]
        public void An_empty_checklist_is_not_a_pass()
        {
            Assert.False(new PostconditionCheck(Five).AllVerified);
            Assert.False(new PostconditionCheck().AllVerified);
        }

        [Fact]
        public void A_property_that_could_not_be_read_is_not_agreement()
        {
            PostconditionCheck check = new PostconditionCheck(Five)
                .Compare("name", "S", "S")
                .Unreadable("category", 100L, "the committed schedule's category could not be read: boom")
                .Record("fields", new JArray("Family"), new JArray("Family"), true)
                .Compare("include_links", true, true)
                .Compare("itemized", false, false);

            Assert.False(check.AllVerified);
            Assert.False(check.AllMeasured); // unreadable -> uncertain, not partial

            JObject json = check.ToJson();
            Assert.False(json.Value<bool>("all_measured"));
            JToken category = json["properties"][1];
            Assert.False(category.Value<bool>("measured"));
            Assert.Equal(JTokenType.Null, category["matches"].Type);   // not false, and not true
            Assert.Contains("boom", category.Value<string>("error"));
        }

        [Fact]
        public void An_unreadable_property_still_counts_as_covered_so_it_cannot_hide_as_missing()
        {
            // It must fail for the right reason: measured=false, not "nobody checked it".
            PostconditionCheck check = new PostconditionCheck(Five)
                .Compare("name", "S", "S")
                .Unreadable("category", 100L, "could not be read")
                .Record("fields", new JArray(), new JArray(), true)
                .Compare("include_links", true, true)
                .Compare("itemized", false, false);

            Assert.Empty(check.Missing);
            Assert.Empty(check.Unexpected);
            Assert.False(check.AllVerified);
        }

        // ---- Evidence integrity ------------------------------------------------

        [Fact]
        public void Mutating_a_token_after_it_was_recorded_does_not_change_the_evidence()
        {
            // The reply must publish what was COMPARED. A caller that keeps a handle on the
            // JArray it passed could otherwise edit the record after the verdict was taken.
            var requested = new JArray("Family", "Count");
            var found = new JArray("Family", "Count");
            PostconditionCheck check = new PostconditionCheck("fields")
                .Record("fields", requested, found, true);

            requested.Add("Injected");
            found.RemoveAt(0);

            JToken recorded = check.ToJson()["properties"][0];
            Assert.Equal(2, ((JArray)recorded["requested"]).Count);
            Assert.Equal("Family", (string)((JArray)recorded["found_in_committed_model"])[0]);
        }

        [Fact]
        public void ToJson_reflects_the_checklist_at_the_moment_it_is_called()
        {
            var check = new PostconditionCheck("name", "category");
            Assert.False(check.ToJson().Value<bool>("all_verified"));

            check.Compare("name", "S", "S");
            Assert.False(check.ToJson().Value<bool>("all_verified"));

            check.Record("category", 1L, 1L, true);
            Assert.True(check.ToJson().Value<bool>("all_verified"));
        }

        [Fact]
        public void The_checklist_shows_each_comparison_and_both_sides_of_it()
        {
            JObject json = AllFiveMatching().ToJson();
            var properties = (JArray)json["properties"];

            Assert.Equal(5, properties.Count);
            Assert.Equal(5, json.Value<int>("checked"));
            Assert.Equal(5, ((JArray)json["required"]).Count);
            foreach (JToken property in properties)
            {
                Assert.False(string.IsNullOrWhiteSpace(property.Value<string>("property")));
                Assert.True(property.Value<bool>("measured"));
                Assert.NotNull(property["requested"]);
                Assert.NotNull(property["found_in_committed_model"]);
            }
            Assert.False(string.IsNullOrWhiteSpace(json.Value<string>("verified_means")));
        }

        [Fact]
        public void String_comparison_is_ordinal_so_a_case_change_is_a_mismatch()
        {
            Assert.False(new PostconditionCheck("name").Compare("name", "Wall Schedule", "wall schedule").AllVerified);
            Assert.True(new PostconditionCheck("name").Compare("name", "Wall Schedule", "Wall Schedule").AllVerified);
        }

        [Fact]
        public void A_null_found_value_is_a_mismatch_not_a_pass()
        {
            Assert.False(new PostconditionCheck("name").Compare("name", "Wall Schedule", null).AllVerified);
        }

        [Fact]
        public void A_verdict_of_true_over_values_that_differ_is_the_callers_claim_and_is_recorded_as_such()
        {
            // Record takes the verdict for comparisons this type cannot compute (an ordered
            // field list). It cannot second-guess that verdict - but the two sides are
            // published, so a false claim is visible in the evidence rather than only in
            // the boolean.
            JToken row = new PostconditionCheck("fields")
                .Record("fields", new JArray("A"), new JArray("B"), true)
                .ToJson()["properties"][0];

            Assert.Equal("A", (string)((JArray)row["requested"])[0]);
            Assert.Equal("B", (string)((JArray)row["found_in_committed_model"])[0]);
        }
    }
}
