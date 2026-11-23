using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class ReadResultCoverageTests
    {
        [Theory]
        [InlineData("result")]
        [InlineData("status")]
        public void Partial_reads_share_one_stable_coverage_key(string key)
        {
            var payload = new JObject { [key] = "partial", ["coverage"] = new JObject { ["pairs_unresolved"] = 3 } };
            var coverage = payload["coverage"].DeepClone();
            Assert.True(ReadResultCoverage.Stamp(payload));
            Assert.True((bool)payload["partial"]);
            Assert.False((bool)payload["read_coverage"]["complete"]);
            Assert.True((bool)payload["read_coverage"]["coverage_reported"]);
            Assert.True(JToken.DeepEquals(coverage, payload["read_coverage"]["details"]["coverage"]));
            Assert.True(ReadResultCoverage.Stamp(payload));
            Assert.True(JToken.DeepEquals(coverage, payload["read_coverage"]["details"]["coverage"]));
        }

        [Fact]
        public void Missing_coverage_is_explicitly_unknown()
        {
            var payload = new JObject { ["result"] = "partial" };
            Assert.True(ReadResultCoverage.Stamp(payload));
            Assert.False((bool)payload["read_coverage"]["coverage_reported"]);
            Assert.Empty((JObject)payload["read_coverage"]["details"]);
        }

        [Fact]
        public void Complete_reads_and_partial_write_applications_are_not_reclassified()
        {
            Assert.False(ReadResultCoverage.Stamp(new JObject { ["result"] = "complete" }));
            var payload = new JObject { ["status"] = "partial", ["application"] = new JObject { ["requested"] = 4, ["verified"] = 2 } };
            Assert.False(ReadResultCoverage.Stamp(payload));
            Assert.Null(payload["read_coverage"]);
        }
    }
}
