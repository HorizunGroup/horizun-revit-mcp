using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PurgeResponseTests
    {
        [Fact]
        public void Adviser_coverage_is_explicit_even_for_an_empty_or_converged_result()
        {
            var report = new JObject { ["converged"] = true, ["remaining_purgeable"] = 0 };
            PurgeResponse.StampCoverage(report, "performance_adviser");
            PurgeResponse.AppendCoverageNote(report, "performance_adviser");
            Assert.True((bool)report["purge_supported"]);
            Assert.Equal("performance_adviser", (string)report["purge_source"]);
            Assert.Contains("materials, styles", (string)report["coverage_note"]);
            Assert.Contains("does not mean the model is fully purged", (string)report["note"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Purge_unavailable_fallback_only_grants_before_writing(bool writeStarted)
        {
            var report = new JObject { ["purge_supported"] = false, ["converged"] = null, ["remaining_purgeable"] = null };
            var result = PurgeResponse.Unavailable(report, "rule missing", writeStarted);
            var signal = result.Fallback.ToJson();
            Assert.Equal(!writeStarted, (bool)signal["allowed"]);
            Assert.Equal(writeStarted, (bool)signal["write_started"]);
            Assert.Equal("horizun_execute_python", (string)signal["recommended_tool"]);
            Assert.Equal(JTokenType.Null, report["remaining_purgeable"].Type);
        }
    }
}
