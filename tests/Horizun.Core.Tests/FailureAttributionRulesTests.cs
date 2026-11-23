using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FailureAttributionRulesTests
    {
        private static readonly Dictionary<long, int> Created = new Dictionary<long, int>
        {
            [5001] = 0, [5002] = 1, [5003] = 2, [5059] = 59
        };

        [Fact]
        public void A_commit_time_error_is_pinned_on_the_door_it_names_not_on_the_last_item()
        {
            // 60 doors; the one at item 17 cuts another wall. Item 59 was only the last processed.
            var created = new Dictionary<long, int>();
            for (int i = 0; i < 60; i++) created[9000 + i] = i;
            var facts = new[]
            {
                new FailureFact { Description = "Can't cut instance out of Wall.", IsError = true, ElementIds = new long[] { 9017, 777 } }
            };
            JArray attributed = FailureAttributionRules.Attribute(facts, created);
            Assert.Single(attributed);
            Assert.Equal(new[] { 17 }, FailureAttributionRules.Culprits(attributed));
            string sentence = FailureAttributionRules.Sentence(attributed);
            Assert.Contains("item 17", sentence);
            Assert.Contains("Can't cut instance out of Wall", sentence);
            Assert.Contains("only the last one processed", sentence);
        }

        [Fact]
        public void Warnings_are_never_culprits()
        {
            var facts = new[] { new FailureFact { Description = "Highlighted elements overlap.", IsError = false, ElementIds = new long[] { 5001 } } };
            Assert.Empty(FailureAttributionRules.Attribute(facts, Created));
            Assert.Equal("", FailureAttributionRules.Sentence(FailureAttributionRules.Attribute(facts, Created)));
        }

        [Fact]
        public void Several_failures_list_each_distinct_item_in_order()
        {
            var facts = new[]
            {
                new FailureFact { Description = "A", IsError = true, ElementIds = new long[] { 5003 } },
                new FailureFact { Description = "B", IsError = true, ElementIds = new long[] { 5001, 5003 } }
            };
            JArray attributed = FailureAttributionRules.Attribute(facts, Created);
            Assert.Equal(2, attributed.Count);
            Assert.Equal(new[] { 0, 2 }, FailureAttributionRules.Culprits(attributed));
            Assert.Contains("items 0, 2", FailureAttributionRules.Sentence(attributed));
        }

        [Fact]
        public void An_error_naming_only_existing_elements_is_kept_but_blames_no_item()
        {
            var facts = new[] { new FailureFact { Description = "Wall is too short.", IsError = true, ElementIds = new long[] { 12345 } } };
            JArray attributed = FailureAttributionRules.Attribute(facts, Created);
            Assert.Single(attributed);
            Assert.Empty((JArray)attributed[0]["items"]);
            Assert.Equal(12345, (long)attributed[0]["element_ids"][0]);
            Assert.Empty(FailureAttributionRules.Culprits(attributed));
            // Blames no item, but what Revit said is still the cause and is quoted.
            string sentence = FailureAttributionRules.Sentence(attributed);
            Assert.Contains("Revit said: Wall is too short.", sentence);
            Assert.Contains("12345", sentence);
            Assert.DoesNotContain("Revit names item", sentence);
        }

        [Fact]
        public void An_error_naming_no_element_is_still_quoted()
        {
            var facts = new[] { new FailureFact { Description = "Fitting cannot be created between the input connectors.", IsError = true } };
            string sentence = FailureAttributionRules.Sentence(FailureAttributionRules.Attribute(facts, Created));
            Assert.Contains("Revit said: Fitting cannot be created", sentence);
            Assert.DoesNotContain("[elements", sentence);
        }

        [Fact]
        public void Nothing_in_nothing_out()
        {
            Assert.Empty(FailureAttributionRules.Attribute(null, Created));
            Assert.Empty(FailureAttributionRules.Attribute(new[] { (FailureFact)null }, Created));
            Assert.Empty(FailureAttributionRules.Culprits(null));
            var noMap = FailureAttributionRules.Attribute(new[] { new FailureFact { IsError = true, Description = "x", ElementIds = new long[] { 1 } } }, null);
            Assert.Single(noMap);
        }
    }
}
