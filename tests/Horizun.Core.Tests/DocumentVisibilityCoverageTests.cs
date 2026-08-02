// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// How much of the model an answer is about.
//
// A closed workset is not a workset whose elements are hidden. Its elements are
// NOT IN THE DOCUMENT - Revit never loaded them - so a FilteredElementCollector
// does not skip them, it never sees them. There is no flag, no exception, and no
// count that comes back short, because "short" would be relative to a total
// measured over the same partial model.
//
// So every read this bridge offers can fail the same way: "0 imported CAD
// instances", "no clashes", "no in-place families", "4,200 m3 of concrete". All
// true about what got loaded, all presented as statements about the building.
//
// The states are here because a real Revit will not produce them on request -
// least of all the one that matters most, the workset collector throwing, which
// has to come back as INCOMPLETE rather than as fine.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DocumentVisibilityCoverageTests
    {
        [Fact]
        public void A_single_user_model_loads_all_of_itself()
        {
            DocumentVisibilityCoverage c = DocumentVisibilityCoverage.NotWorkshared();

            Assert.True(c.CoverageComplete);
            Assert.False(c.IsWorkshared);
            Assert.Null(c.WorksetsTotal);
            Assert.Contains("not workshared", c.Note());
            Assert.Contains("covers all of it", c.Note());
        }

        [Fact]
        public void Every_workset_open_is_complete_coverage()
        {
            DocumentVisibilityCoverage c = DocumentVisibilityCoverage.From(total: 7, open: 7);

            Assert.True(c.CoverageComplete);
            Assert.Equal(7, c.WorksetsTotal);
            Assert.Equal(7, c.WorksetsOpen);
            Assert.Equal(0, c.WorksetsClosed);
        }

        /// <summary>THE ONE THIS EXISTS FOR.</summary>
        [Fact]
        public void One_closed_workset_makes_the_whole_answer_incomplete()
        {
            DocumentVisibilityCoverage c = DocumentVisibilityCoverage.From(total: 7, open: 6);

            Assert.False(c.CoverageComplete);
            Assert.Equal(1, c.WorksetsClosed);
            Assert.False((bool)c.ToJson()["coverage_complete"]);
        }

        [Fact]
        public void The_note_says_what_a_closed_workset_does_to_the_reader_not_what_it_is()
        {
            // "Some worksets are closed" is a fact about Revit. What the caller needs is
            // that the zero they are looking at is not evidence of anything.
            string note = DocumentVisibilityCoverage.From(10, 7).Note();

            Assert.Contains("3 of 10", note);
            Assert.Contains("NOT IN THE DOCUMENT", note);
            Assert.Contains("DO NOT READ AN ABSENCE HERE AS AN ABSENCE IN THE MODEL", note);
        }

        [Fact]
        public void A_model_where_no_workset_is_open_is_the_extreme_of_the_same_case()
        {
            DocumentVisibilityCoverage c = DocumentVisibilityCoverage.From(total: 4, open: 0);

            Assert.False(c.CoverageComplete);
            Assert.Equal(4, c.WorksetsClosed);
        }

        [Fact]
        public void A_workshared_model_with_no_user_worksets_is_complete()
        {
            // Nothing to be closed, so nothing is missing. Zero of zero is covered.
            DocumentVisibilityCoverage c = DocumentVisibilityCoverage.From(total: 0, open: 0);

            Assert.True(c.CoverageComplete);
            Assert.Equal(0, c.WorksetsTotal);
        }

        // ---- unknown is not complete -------------------------------------------

        /// <summary>
        /// The direction that matters. A scan that cannot say how much of the model it
        /// saw has not earned the benefit of the doubt: of the two ways to be wrong,
        /// "we may have missed something" costs a re-run and "we saw everything" costs a
        /// decision made on a model nobody saw.
        /// </summary>
        [Fact]
        public void Worksets_that_could_not_be_read_are_incomplete_not_fine()
        {
            DocumentVisibilityCoverage c =
                DocumentVisibilityCoverage.Unreadable("the workset collector threw: access violation");

            Assert.False(c.CoverageComplete);
            Assert.True(c.IsWorkshared);
            Assert.Null(c.WorksetsTotal);
            Assert.Null(c.WorksetsOpen);
            Assert.Null(c.WorksetsClosed);
            Assert.Contains("COVERAGE UNKNOWN", c.Note());
            Assert.Contains("access violation", c.Note());   // the reason travels
        }

        [Fact]
        public void An_unreadable_coverage_still_gives_a_reason_when_nobody_supplied_one()
        {
            DocumentVisibilityCoverage c = DocumentVisibilityCoverage.Unreadable(null);

            Assert.False(c.CoverageComplete);
            Assert.Equal("no reason given", c.ReadError);
        }

        [Fact]
        public void Counts_that_cannot_be_true_are_refused_rather_than_published()
        {
            // More open than exist, or a negative total, means the caller of From has a
            // bug. Publishing it would put an impossible pair of numbers in a report that
            // exists to be trusted.
            Assert.Throws<ArgumentOutOfRangeException>(() => DocumentVisibilityCoverage.From(3, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => DocumentVisibilityCoverage.From(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => DocumentVisibilityCoverage.From(3, -1));
        }

        // ---- the shape every command carries ------------------------------------

        [Fact]
        public void The_block_is_the_same_shape_whatever_the_state()
        {
            // One shape, on every read-only command, so a caller learns to look for it
            // once rather than per tool. Absent fields would make a reader write
            // per-tool handling and then forget one.
            foreach (DocumentVisibilityCoverage c in new[]
                     {
                         DocumentVisibilityCoverage.NotWorkshared(),
                         DocumentVisibilityCoverage.From(5, 5),
                         DocumentVisibilityCoverage.From(5, 2),
                         DocumentVisibilityCoverage.Unreadable("nope")
                     })
            {
                var json = c.ToJson();
                foreach (string field in new[]
                         {
                             "coverage_complete", "is_workshared", "worksets_total", "worksets_open",
                             "worksets_closed", "worksets_read_error", "note"
                         })
                    Assert.True(json.ContainsKey(field), field + " is missing from " + json.ToString());

                Assert.False(string.IsNullOrWhiteSpace((string)json["note"]));
            }
        }

        [Fact]
        public void The_numbers_in_the_block_are_the_numbers_that_were_measured()
        {
            var json = DocumentVisibilityCoverage.From(12, 9).ToJson();

            Assert.Equal(12, (int)json["worksets_total"]);
            Assert.Equal(9, (int)json["worksets_open"]);
            Assert.Equal(3, (int)json["worksets_closed"]);
            Assert.Null((string)json["worksets_read_error"]);
        }
    }
}
