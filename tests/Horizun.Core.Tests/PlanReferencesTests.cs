using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class PlanReferencesTests
    {
        [Fact]
        public void ResolvesNestedObjectAndArrayPathsWithoutStringCoercion()
        {
            var results = new Dictionary<string, JToken> { ["walls"] = JObject.Parse(@"{""rows"":[{""element_id"":42}]}" ) };
            JToken input = JObject.Parse(@"{""target_id"":""${walls.rows.0.element_id}"",""keep"":[true,3]}" );
            JToken actual = PlanReferences.Resolve(input, results, out string error);
            Assert.Null(error);
            Assert.Equal(JTokenType.Integer, actual["target_id"].Type);
            Assert.Equal(42, (int)actual["target_id"]);
            Assert.True((bool)actual["keep"][0]);
        }

        [Fact]
        public void OnlyAnExactReferenceStringIsSubstituted()
        {
            var results = new Dictionary<string, JToken> { ["a"] = new JObject { ["id"] = 7 } };
            JToken actual = PlanReferences.Resolve(new JValue("prefix ${a.id}"), results, out string error);
            Assert.Null(error);
            Assert.Equal("prefix ${a.id}", (string)actual);
        }

        [Fact]
        public void MissingActionIsAnExplicitFailure()
        {
            JToken actual = PlanReferences.Resolve(new JValue("${missing.rows.0}"),
                new Dictionary<string, JToken>(), out string error);
            Assert.Null(actual);
            Assert.Contains("No completed action", error);
        }

        [Fact]
        public void ReferenceKeysFindNestedExactReferencesOnly()
        {
            JToken input = JObject.Parse(@"{""a"":""${first.id}"",""nested"":[""text ${ignored.id}"",""${second.rows.0}""]}");
            Assert.Equal(new[] { "first", "second" }, PlanReferences.ReferenceKeys(input));
        }
    }
}
