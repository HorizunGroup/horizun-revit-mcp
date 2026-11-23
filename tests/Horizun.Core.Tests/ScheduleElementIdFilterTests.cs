using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleElementIdFilterTests
    {
        private static ScheduleEditRules.ElementIdCandidate[] Candidates => new[]
        {
            new ScheduleEditRules.ElementIdCandidate { Id = 101, Name = "Level 1" },
            new ScheduleEditRules.ElementIdCandidate { Id = 202, Name = "Level 2" }
        };

        [Theory]
        [InlineData("'level 1'", 101L)]
        [InlineData("101", 101L)]
        [InlineData("'202'", 202L)]
        [InlineData("202.0", 202L)]
        public void Resolves_names_and_integral_ids(string json, long expected)
        {
            Assert.Null(ScheduleEditRules.ResolveElementIdFilter(Candidates, JToken.Parse(json), "equal", true, out long id));
            Assert.Equal(expected, id);
        }

        [Fact]
        public void Ambiguity_refuses_and_lists_the_candidates()
        {
            var candidates = Candidates.Concat(new[] { new ScheduleEditRules.ElementIdCandidate { Id = 303, Name = "LEVEL 1" } });
            string error = ScheduleEditRules.ResolveElementIdFilter(candidates, new JValue("Level 1"), "equal", true, out _);
            Assert.Contains("ambiguous", error);
            Assert.Contains("101", error);
            Assert.Contains("303", error);
        }

        [Theory]
        [InlineData("'Missing'")]
        [InlineData("999")]
        [InlineData("101.5")]
        [InlineData("null")]
        [InlineData("true")]
        [InlineData("9223372036854775808")]
        public void Unknown_or_invalid_values_refuse_with_candidates(string json)
        {
            string error = ScheduleEditRules.ResolveElementIdFilter(Candidates, JToken.Parse(json), "equal", false, out _);
            Assert.NotNull(error);
            Assert.Contains("Level 1", error);
            Assert.Contains("202", error);
        }

        [Theory]
        [InlineData("contains")]
        [InlineData("not_contains")]
        [InlineData("begins_with")]
        [InlineData("ends_with")]
        [InlineData("greater_than")]
        public void Non_ordered_id_fields_refuse_invalid_operators(string op)
        {
            string error = ScheduleEditRules.ResolveElementIdFilter(Candidates, new JValue("Level 1"), op, false, out _);
            Assert.Contains("does not apply", error);
            Assert.Contains("Candidates", error);
        }

        [Theory]
        [InlineData("greater_than")]
        [InlineData("greater_than_or_equal")]
        [InlineData("less_than")]
        [InlineData("less_than_or_equal")]
        [InlineData("not_equal")]
        public void Level_and_phase_ordering_accepts_names(string op)
        {
            Assert.Null(ScheduleEditRules.ValidateFilter(op, true, false, elementId: true));
            Assert.Null(ScheduleEditRules.ResolveElementIdFilter(Candidates, new JValue("Level 2"), op, true, out long id));
            Assert.Equal(202, id);
        }

        [Fact]
        public void Large_ids_are_not_rounded_through_double()
        {
            const long expected = 9007199254740993;
            var candidates = new[] { new ScheduleEditRules.ElementIdCandidate { Id = expected, Name = "Large" } };
            Assert.Null(ScheduleEditRules.ResolveElementIdFilter(candidates, new JValue(expected), "equal", false, out long id));
            Assert.Equal(expected, id);
        }
    }
}
