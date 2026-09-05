// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The ownership census, proved by running it. The test that matters most is the
// invariant: four states that add up to what was scanned. Without it, an element
// whose status could not be read drifts into "not owned" - which a reader acts
// on as "free to edit" about an element a colleague has open right now.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OwnershipCensusTests
    {
        private static OwnershipTally T(long me, long others, long none, long unreadable)
        {
            var t = new OwnershipTally();
            for (long i = 0; i < me; i++) t.Count(CheckoutState.Me, null, i);
            for (long i = 0; i < others; i++) t.Count(CheckoutState.Others, "colleague", 1000 + i);
            for (long i = 0; i < none; i++) t.Count(CheckoutState.NoOne, null, 2000 + i);
            for (long i = 0; i < unreadable; i++) t.Count(CheckoutState.Unreadable, null, 3000 + i);
            return t;
        }

        // ----------------------------------------------------------- invariant

        [Fact]
        public void The_four_states_account_for_every_element_scanned()
        {
            OwnershipTally t = T(3, 2, 10, 1);
            Assert.Equal(16, t.Scanned);
            Assert.True(OwnershipCensus.Balances(t));
            Assert.True(OwnershipCensus.ToJson(t).Value<bool>("counts_balance"));
        }

        [Fact]
        public void Scanned_is_incremented_by_the_counter_so_it_cannot_drift()
        {
            // Scanned is not settable from outside on purpose: a caller that
            // maintained it separately would eventually disagree with the buckets,
            // and the invariant would be checking one number against itself.
            var t = new OwnershipTally();
            t.Count(CheckoutState.Me, null, 1);
            t.Count(CheckoutState.Others, "x", 2);
            Assert.Equal(2, t.Scanned);
            Assert.True(OwnershipCensus.Balances(t));
        }

        [Fact]
        public void An_unbalanced_tally_is_reported_as_unbalanced_rather_than_hidden()
        {
            var t = T(1, 1, 1, 0);
            t.Scanned += 5;                       // as if something was scanned and never classified
            Assert.False(OwnershipCensus.Balances(t));
            Assert.False(OwnershipCensus.ToJson(t).Value<bool>("counts_balance"));
        }

        // -------------------------------------------------------- not readable

        [Fact]
        public void Unknown_is_not_unowned()
        {
            // The whole point. An element whose status threw must not land in
            // not_owned, which a reader treats as free to edit.
            OwnershipTally t = T(0, 0, 0, 4);
            Assert.Equal(0, t.NotOwned);
            Assert.Equal(4, t.Unreadable);
            Assert.Contains("unknown is not unowned", OwnershipCensus.Note(t));
            Assert.Contains("LOWER BOUND", OwnershipCensus.Note(t));
            Assert.False(OwnershipCensus.ToJson(t).Value<bool>("counts_are_exact"));
        }

        [Fact]
        public void A_clean_census_does_not_carry_the_lower_bound_warning()
        {
            Assert.DoesNotContain("LOWER BOUND", OwnershipCensus.Note(T(1, 1, 1, 0)));
            Assert.True(OwnershipCensus.ToJson(T(1, 1, 1, 0)).Value<bool>("counts_are_exact"));
        }

        // ------------------------------------------------------------- shares

        [Fact]
        public void A_census_that_scanned_nothing_reports_unknown_not_zero()
        {
            var t = new OwnershipTally();
            Assert.Null(OwnershipCensus.ShareOwnedByOthers(t));
            Assert.Contains("UNKNOWN", OwnershipCensus.Note(t));
            Assert.Contains("not a model where nothing is borrowed", OwnershipCensus.Note(t));
        }

        [Fact]
        public void The_share_is_over_everything_scanned_including_the_unreadable()
        {
            // 2 of 10, not 2 of 8. Excluding the unreadable would overstate how
            // much of the model is known to be free.
            OwnershipTally t = T(0, 2, 6, 2);
            Assert.Equal(20.0, OwnershipCensus.ShareOwnedByOthers(t));
        }

        // ------------------------------------------------------------- owners

        [Fact]
        public void Only_other_users_appear_in_the_by_owner_breakdown()
        {
            OwnershipTally t = T(5, 1, 0, 0);
            KeyValuePair<string, long> only = Assert.Single(OwnershipCensus.OwnersRanked(t));
            Assert.Equal("colleague", only.Key);
            Assert.Equal(1, only.Value);
        }

        [Fact]
        public void An_element_whose_owner_will_not_name_itself_is_still_counted_as_owned()
        {
            // Dropping it would lose it from the breakdown while it stayed in the
            // total, and the two would stop agreeing.
            var t = new OwnershipTally();
            t.Count(CheckoutState.Others, null, 1);
            t.Count(CheckoutState.Others, "   ", 2);

            Assert.Equal(2, t.OwnedByOthers);
            KeyValuePair<string, long> only = Assert.Single(OwnershipCensus.OwnersRanked(t));
            Assert.Equal("(owner name unreadable)", only.Key);
            Assert.Equal(2, only.Value);
        }

        [Fact]
        public void Owners_are_ranked_largest_first_and_ties_break_by_name()
        {
            var t = new OwnershipTally();
            t.Count(CheckoutState.Others, "zoe", 1);
            t.Count(CheckoutState.Others, "adam", 2);
            t.Count(CheckoutState.Others, "adam", 3);

            List<KeyValuePair<string, long>> r = OwnershipCensus.OwnersRanked(t);
            Assert.Equal("adam", r[0].Key);
            Assert.Equal("zoe", r[1].Key);
        }

        [Fact]
        public void The_ids_of_what_others_hold_are_kept_so_a_reader_can_go_and_look()
        {
            OwnershipTally t = T(1, 3, 1, 1);
            Assert.Equal(3, t.OwnedByOthersIds.Count);
        }

        // ------------------------------------------------- not workshared

        [Fact]
        public void A_document_that_is_not_workshared_has_absent_counts_not_zero_ones()
        {
            // Four zeros are a census that RAN and found nothing. This one could
            // not run, and the difference is the whole story of the file.
            JObject j = OwnershipCensus.NotApplicable("this document is not workshared.");
            Assert.Equal("not_applicable", j.Value<string>("status"));
            Assert.Contains("not workshared", j.Value<string>("reason"));
            Assert.Contains("every count here is absent", j.Value<string>("means"));
            Assert.Null(j["elements_scanned"]);
            Assert.Null(j["elements_owned_by_others"]);
        }

        // -------------------------------------------------------- what it means

        [Fact]
        public void The_census_says_it_took_nothing_and_that_borrowing_is_not_a_defect()
        {
            Assert.Contains("WITHOUT relinquishing", OwnershipCensus.Means);
            Assert.Contains("not a defect", OwnershipCensus.Means);
            Assert.Contains("no standard was supplied", OwnershipCensus.Means);
        }
    }
}
