// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The link tally, and the fabricated defect it exists to prevent.
//
// Measured on a real delivery model (222,984 elements, Autodesk Docs): two links
// reported as NOT LOADED while both linked documents were open in Revit. Two
// separate causes, same lie - a status that could not be read was counted as a
// link that is broken. Every case below is the shape of a real reading, including
// the cloud one that only a hosted model produces.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LinkStatusTallyTests
    {
        [Fact]
        public void An_unreadable_status_is_unknown_never_not_loaded()
        {
            // THE REGRESSION. Both links are loaded; the API refused to say so.
            var t = LinkStatusTally.Of(new string[] { null, null });

            Assert.Equal(2, t.Total);
            Assert.Equal(0, t.NotLoaded);      // <- was 2
            Assert.Equal(2, t.Unknown);
            Assert.Equal(0, t.Loaded);
            Assert.False(t.Complete);
        }

        [Fact]
        public void Partial_coverage_never_says_all_links_are_loaded()
        {
            var t = LinkStatusTally.Of(new[] { "Loaded", null });

            Assert.False(t.Complete);
            Assert.DoesNotContain("All 2 link(s) are loaded", t.Summary());
            Assert.Contains("PARTIAL", t.Summary());
            Assert.Contains("UNKNOWN", t.Summary());
        }

        [Fact]
        public void Full_coverage_and_all_loaded_may_say_so()
        {
            var t = LinkStatusTally.Of(new[] { "Loaded", "Loaded" });

            Assert.True(t.Complete);
            Assert.Equal(2, t.Loaded);
            Assert.Equal("All 2 link(s) are loaded.", t.Summary());
        }

        [Fact]
        public void A_genuinely_unloaded_link_is_still_reported()
        {
            // The fix must not overshoot into never reporting a real problem.
            var t = LinkStatusTally.Of(new[] { "Loaded", "NotFound", "Unloaded" });

            Assert.True(t.Complete);
            Assert.Equal(2, t.NotLoaded);
            Assert.Contains("2 of 3 link(s) are NOT loaded", t.Summary());
        }

        [Fact]
        public void Unloaded_and_unknown_are_reported_as_different_things()
        {
            var t = LinkStatusTally.Of(new[] { "Loaded", "NotFound", null });

            Assert.Equal(1, t.Loaded);
            Assert.Equal(1, t.NotLoaded);
            Assert.Equal(1, t.Unknown);
            Assert.False(t.Complete);
            Assert.Contains("1 of 3 link(s) are NOT loaded", t.Summary());
            Assert.Contains("1 more would not report", t.Summary());
        }

        [Fact]
        public void Nothing_is_dropped_or_double_counted()
        {
            var t = LinkStatusTally.Of(new[] { "Loaded", "Loaded", "NotFound", null, "", "Unloaded" });

            Assert.Equal(6, t.Total);
            Assert.Equal(t.Total, t.Loaded + t.NotLoaded + t.Unknown);
        }

        [Fact]
        public void An_empty_string_counts_as_unknown_not_as_a_status()
        {
            var t = LinkStatusTally.Of(new[] { "" });

            Assert.Equal(1, t.Unknown);
            Assert.Equal(0, t.NotLoaded);
        }

        [Fact]
        public void Every_status_unreadable_says_so_plainly()
        {
            var t = LinkStatusTally.Of(new string[] { null, null, null });

            Assert.Contains("None of the 3 link(s) would report their status", t.Summary());
            Assert.Contains("UNKNOWN", t.Summary());
        }

        [Fact]
        public void No_links_is_not_partial_coverage()
        {
            var t = LinkStatusTally.Of(new string[0]);

            Assert.Equal(0, t.Total);
            Assert.True(t.Complete);
            Assert.Equal("No Revit links.", t.Summary());
        }

        [Fact]
        public void A_null_sequence_is_empty_not_a_crash()
        {
            var t = LinkStatusTally.Of(null);

            Assert.Equal(0, t.Total);
            Assert.True(t.Complete);
        }

        [Fact]
        public void The_summary_names_the_unit_it_counts()
        {
            // audit_model tallies link TYPES and model_scan tallies link INSTANCES, and
            // on a model with the same link loaded several times they published "1 of 8"
            // against "4 of 22" - both right, and unreadable side by side. The unit goes
            // in the sentence so the reader does not supply their own.
            var t = LinkStatusTally.Of(new[] { "Loaded", "NotFound" });

            Assert.Contains("link type(s)", t.Summary("link type"));
            Assert.Contains("link instance(s)", t.Summary("link instance"));
            // The default stays "link(s)" so an unmigrated caller keeps its old sentence
            // rather than silently claiming a unit nobody chose.
            Assert.Contains("link(s)", t.Summary());
        }

        [Fact]
        public void The_status_comparison_does_not_depend_on_casing()
        {
            // The value arrives from an enum ToString(); a casing change upstream must not
            // silently reclassify every loaded link as unloaded.
            var t = LinkStatusTally.Of(new[] { "loaded", "LOADED" });

            Assert.Equal(2, t.Loaded);
            Assert.Equal(0, t.NotLoaded);
        }
    }
}
