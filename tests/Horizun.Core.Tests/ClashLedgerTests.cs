// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A clash count is read as "coordinated". These fix the ways it could be smaller
// than reality while still calling itself complete:
//
//   * elements dropped by a bare `continue` (failed collector, throwing bounding
//     box, null bounding box) and then not counted as candidates;
//   * one physical pair reported twice when the two category sets overlap;
//   * two placements of the same link sharing a solid cache entry, so the second
//     is tested with the first one's geometry, positioned where the first one is;
//   * a pair with no usable solid, and a boolean that threw, both falling through
//     as though they had been tested and found clean.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ClashLedgerTests
    {
        // ---- the pair key ------------------------------------------------------

        [Fact]
        public void The_same_pair_in_either_order_is_one_pair()
        {
            var p = new PairLedger();

            Assert.True(p.Claim("host", "100", "host", "200"));
            Assert.False(p.Claim("host", "200", "host", "100"));   // the duplicate

            Assert.Equal(1, p.Tested);
            Assert.Equal(1, p.Duplicates);
        }

        [Fact]
        public void Two_placements_of_one_link_are_different_elements()
        {
            // THE CACHE COLLISION. Same link name, same element id, different instance.
            string first = PairLedger.ElementKey("TOWER.rvt", "9001", "500");
            string second = PairLedger.ElementKey("TOWER.rvt", "9002", "500");

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void A_key_cannot_be_forged_by_a_name_containing_the_separator()
        {
            // Link source names contain spaces and colons in the real world, e.g.
            // "MOD_STRC-REF_A.rvt : 859 : location <Not Shared>". A space separator
            // would let two different elements build one key.
            string a = PairLedger.ElementKey("a b", "1", "2");
            string b = PairLedger.ElementKey("a", "b 1", "2");

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Pairs_from_different_link_placements_are_not_deduplicated_together()
        {
            var p = new PairLedger();
            string inst1 = PairLedger.ElementKey("TOWER.rvt", "9001", null);
            string inst2 = PairLedger.ElementKey("TOWER.rvt", "9002", null);

            Assert.True(p.Claim("host", "10", inst1, "500"));
            Assert.True(p.Claim("host", "10", inst2, "500"));   // a genuinely different pair

            Assert.Equal(2, p.Tested);
            Assert.Equal(0, p.Duplicates);
        }

        [Fact]
        public void A_null_source_or_id_still_produces_a_stable_distinct_key()
        {
            Assert.NotEqual(PairLedger.ElementKey(null, null, "1"), PairLedger.ElementKey(null, null, "2"));
            Assert.Equal(PairLedger.ElementKey(null, null, "1"), PairLedger.ElementKey(null, null, "1"));
        }

        // ---- the side ledger ---------------------------------------------------

        [Fact]
        public void An_element_with_no_bounding_box_is_recorded_not_dropped()
        {
            var l = new SideLedger("side A");
            l.Add(ClashInclusion.Included);
            l.Add(ClashInclusion.Excluded, "No bounding box.", "host #42");

            Assert.Equal(2, l.Candidates);       // NOT 1 - the survivor count was the bug
            Assert.Equal(1, l.Included);
            Assert.False(l.Complete);
            Assert.Contains("UNKNOWN, not absent", l.Describe());
            Assert.Contains("host #42", l.Examples);
        }

        [Fact]
        public void A_failed_collector_makes_the_side_incomplete()
        {
            var l = new SideLedger("side B");
            l.Add(ClashInclusion.Failed, "The collector threw for category OST_Walls", "OST_Walls @ link");

            Assert.False(l.Complete);
            Assert.Equal(1, l.Failed);
            Assert.Equal(0, l.Included);
        }

        [Fact]
        public void A_side_where_everything_was_checked_is_complete()
        {
            var l = new SideLedger("side A");
            l.Add(ClashInclusion.Included);
            l.Add(ClashInclusion.Included);

            Assert.True(l.Complete);
            Assert.Equal("side A: all 2 element(s) were checked.", l.Describe());
        }

        [Fact]
        public void Drop_reasons_are_grouped_with_counts_so_the_cause_is_visible()
        {
            var l = new SideLedger("side A");
            l.Add(ClashInclusion.Excluded, "No bounding box.");
            l.Add(ClashInclusion.Excluded, "No bounding box.");
            l.Add(ClashInclusion.Failed, "Bounding box threw.");

            var reasons = l.Reasons.ToList();
            Assert.Equal(2, reasons.Count);
            Assert.Equal("No bounding box.", reasons[0].Key);    // most frequent first
            Assert.Equal(2, reasons[0].Value);
        }

        [Fact]
        public void A_side_with_no_matching_elements_says_so_rather_than_claiming_a_clean_check()
        {
            var l = new SideLedger("side B");

            Assert.True(l.Complete);            // nothing was dropped
            Assert.Contains("no elements matched", l.Describe());
        }

        // ---- the pair ledger ---------------------------------------------------

        [Fact]
        public void A_pair_with_no_usable_solid_is_not_a_clean_pair()
        {
            var p = new PairLedger();
            p.Claim("host", "1", "host", "2");
            p.MarkNoSolids();

            Assert.False(p.Complete);
            Assert.Equal(1, p.SkippedNoSolids);
        }

        [Fact]
        public void An_unresolved_boolean_leaves_the_run_incomplete()
        {
            var p = new PairLedger();
            p.Claim("host", "1", "host", "2");
            p.MarkUnresolved();

            Assert.False(p.Complete);
            Assert.Equal(1, p.Unresolved);
        }

        [Fact]
        public void A_run_where_every_claimed_pair_resolved_is_complete()
        {
            var p = new PairLedger();
            p.Claim("host", "1", "host", "2");
            p.Claim("host", "1", "host", "3");

            Assert.True(p.Complete);
            Assert.Equal(2, p.Tested);
        }
    }
}
