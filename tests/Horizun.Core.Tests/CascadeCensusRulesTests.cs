using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CascadeCensusRulesTests
    {
        [Fact]
        public void The_census_counts_the_whole_cascade_not_the_first_page()
        {
            var categories = Enumerable.Repeat("Doors", 2400)
                .Concat(Enumerable.Repeat("Windows", 1000))
                .Concat(Enumerable.Repeat("Tags", 94));
            var census = CascadeCensusRules.ByCategory(categories);
            Assert.Equal(2400, (int)census["Doors"]);
            Assert.Equal(1000, (int)census["Windows"]);
            Assert.Equal(94, (int)census["Tags"]);
            Assert.Equal(3494, census.Properties().Sum(p => (int)p.Value));
        }

        [Fact]
        public void The_biggest_category_comes_first_and_ties_are_alphabetical()
        {
            var census = CascadeCensusRules.ByCategory(new[] { "B", "A", "C", "C", "C", "A" });
            Assert.Equal(new[] { "C", "A", "B" }, census.Properties().Select(p => p.Name).ToArray());
        }

        [Fact]
        public void An_element_whose_category_cannot_be_read_is_counted_not_dropped()
        {
            var census = CascadeCensusRules.ByCategory(new[] { null, "", "Doors", "  " });
            Assert.Equal(3, (int)census[CascadeCensusRules.Unreadable]);
            Assert.Equal(1, (int)census["Doors"]);
        }

        [Fact]
        public void An_empty_cascade_is_an_empty_census()
        {
            Assert.Empty(CascadeCensusRules.ByCategory(null));
            Assert.Empty(CascadeCensusRules.ByCategory(new string[0]));
        }

        [Fact]
        public void The_cap_note_appears_only_when_ids_were_left_out()
        {
            Assert.Null(CascadeCensusRules.CapNote(200, 200));
            Assert.Null(CascadeCensusRules.CapNote(5, 5));
            string note = CascadeCensusRules.CapNote(3494, 200);
            Assert.Contains("first 200 of 3494", note);
            Assert.Contains("counts all 3494", note);
        }
    }
}
