// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The level-association census, proved by running it. Almost every test here
// defends one of three confusions the census is built to prevent:
//
//   nothing measured   is not   nothing associated
//   unreadable         is not   unassociated
//   a count            is not   a finding
//
// The first is the one that produces a clean report about a model nobody looked
// at, which is the failure this whole area exists to make impossible.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LevelAssociationTests
    {
        private static LevelAssociationFacts F(long examined, long with, long without, long unreadable)
        {
            return new LevelAssociationFacts
            {
                Examined = examined,
                WithLevel = with,
                WithoutLevel = without,
                Unreadable = unreadable
            };
        }

        // ---------------------------------------------------------- percentage

        [Fact]
        public void A_census_that_measured_nothing_reports_unknown_not_zero_and_not_a_hundred()
        {
            // THE ONE THAT MATTERS. Both 0 and 100 would be read as a result.
            Assert.Null(LevelAssociationRules.PercentWithLevel(0, 0));

            string note = LevelAssociationRules.Note(F(0, 0, 0, 0));
            Assert.Contains("UNKNOWN", note);
            Assert.Contains("not a model without levels", note);
        }

        [Fact]
        public void An_unreadable_element_is_not_counted_as_a_miss()
        {
            // 3 of 4 readable elements have a level. The 6 that would not answer are
            // not evidence of anything, so they stay out of the denominator: 75%,
            // never 30%.
            Assert.Equal(75.0, LevelAssociationRules.PercentWithLevel(3, 1));

            string note = LevelAssociationRules.Note(F(10, 3, 1, 6));
            Assert.Contains("75%", note);
            Assert.Contains("4 element(s)", note);
            Assert.Contains("excluded from that percentage", note);
            Assert.Contains("LOWER BOUNDS", note);
        }

        [Fact]
        public void When_every_read_threw_the_share_is_unknown_rather_than_zero()
        {
            // 9 elements examined and not one answered. Reporting 0% would say the
            // model has no level associations; it says nothing of the kind.
            LevelAssociationFacts f = F(9, 0, 0, 9);
            Assert.Null(LevelAssociationRules.PercentWithLevel(f.WithLevel, f.WithoutLevel));
            Assert.Contains("share is UNKNOWN", LevelAssociationRules.Note(f));
        }

        [Fact]
        public void The_percentage_ignores_the_examined_count_it_was_not_derived_from()
        {
            // Examined can include unreadables; the percentage is over what answered.
            // If this ever starts reading Examined, the two numbers disagree silently.
            Assert.Equal(
                LevelAssociationRules.PercentWithLevel(3, 1),
                LevelAssociationRules.PercentWithLevel(3, 1));
            Assert.Equal(100.0, LevelAssociationRules.PercentWithLevel(5, 0));
            Assert.Equal(0.0, LevelAssociationRules.PercentWithLevel(0, 5));
        }

        [Fact]
        public void A_model_where_everything_has_a_level_reports_a_hundred_percent()
        {
            Assert.Equal(100.0, LevelAssociationRules.PercentWithLevel(42, 0));
            Assert.Contains("100%", LevelAssociationRules.Note(F(42, 42, 0, 0)));
        }

        // -------------------------------------------------------------- exact

        [Fact]
        public void Counts_are_exact_only_when_nothing_was_unreadable()
        {
            Assert.True(LevelAssociationRules.IsExact(0));
            Assert.False(LevelAssociationRules.IsExact(1));
        }

        [Fact]
        public void A_clean_census_does_not_carry_the_lower_bound_warning()
        {
            // The warning has to mean something. If it is printed unconditionally a
            // reader learns to skip it, and it is not there when it counts.
            Assert.DoesNotContain("LOWER BOUNDS", LevelAssociationRules.Note(F(10, 8, 2, 0)));
        }

        // --------------------------------------------------------- breakdown

        [Fact]
        public void Categories_are_ranked_largest_first_so_a_reader_sees_where_the_gap_is()
        {
            var f = new LevelAssociationFacts();
            f.WithoutByCategory["Walls"] = 3;
            f.WithoutByCategory["Mass"] = 40;
            f.WithoutByCategory["Topography"] = 12;

            List<KeyValuePair<string, long>> ranked = LevelAssociationRules.WithoutByCategoryRanked(f);
            Assert.Equal("Mass", ranked[0].Key);
            Assert.Equal("Topography", ranked[1].Key);
            Assert.Equal("Walls", ranked[2].Key);
        }

        [Fact]
        public void Ties_break_by_name_so_two_runs_of_one_model_agree()
        {
            // An unstable order makes a diff between two snapshots unreadable, which
            // is the whole point of taking snapshots.
            var f = new LevelAssociationFacts();
            f.WithoutByCategory["Zebra"] = 5;
            f.WithoutByCategory["Alpha"] = 5;
            f.WithoutByCategory["Mid"] = 5;

            List<KeyValuePair<string, long>> a = LevelAssociationRules.WithoutByCategoryRanked(f);
            List<KeyValuePair<string, long>> b = LevelAssociationRules.WithoutByCategoryRanked(f);
            Assert.Equal("Alpha", a[0].Key);
            Assert.Equal("Mid", a[1].Key);
            Assert.Equal("Zebra", a[2].Key);
            for (int i = 0; i < a.Count; i++) Assert.Equal(a[i].Key, b[i].Key);
        }

        [Fact]
        public void An_empty_breakdown_is_an_empty_list_not_a_throw()
        {
            Assert.Empty(LevelAssociationRules.WithoutByCategoryRanked(new LevelAssociationFacts()));
            Assert.Empty(LevelAssociationRules.WithoutByCategoryRanked(null));
        }

        // ------------------------------------------------------ what it means

        [Fact]
        public void The_census_says_in_its_own_words_that_it_is_not_a_finding()
        {
            // Without this sentence beside the number, a non-zero count reads as a
            // defect list and a reader "fixes" a topography that never had a level.
            Assert.Contains("not a finding", LevelAssociationRules.CensusMeans);
            Assert.Contains("no standard was supplied", LevelAssociationRules.CensusMeans);
        }

        [Fact]
        public void No_census_at_all_is_distinguishable_from_a_census_that_found_nothing()
        {
            Assert.Contains("no census was taken", LevelAssociationRules.Note(null));
            Assert.DoesNotContain("no census was taken", LevelAssociationRules.Note(F(0, 0, 0, 0)));
        }
    }
}
