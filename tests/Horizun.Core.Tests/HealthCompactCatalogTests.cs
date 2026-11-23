using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HealthCompactCatalogTests
    {
        [Fact]
        public void Requested_catalog_does_not_enable_unrelated_full_diagnostics()
        {
            var payload = new JObject {
                ["operational_controls"] = new JObject { ["permission_profile"] = "safe_write", ["mcp_paused"] = true },
                ["python_runtime"] = new JObject { ["state"] = "not_authorized" },
                ["pending_sync"] = new JObject { ["pending"] = true },
                ["verification_catalog"] = new JObject { ["tools"] = new JArray("writer") },
                ["jobs"] = new JArray(new string('x', 10000))
            };
            int fullBytes = Encoding.UTF8.GetByteCount(payload.ToString(Formatting.None));
            JObject answer = HealthDetailRules.Apply(payload, true, true);
            Assert.Equal("compact", (string)answer["detail"]);
            Assert.NotNull(answer["verification_catalog"]);
            Assert.Null(answer["jobs"]);
            Assert.Equal("safe_write", (string)answer["operational_controls"]["permission_profile"]);
            Assert.True((bool)answer["operational_controls"]["mcp_paused"]);
            Assert.Equal("not_authorized", (string)answer["python_runtime"]["state"]);
            Assert.True((bool)answer["pending_sync"]["pending"]);
            Assert.DoesNotContain("verification_catalog", answer["omitted_in_compact"].Select(t => (string)t));
            Assert.True(Encoding.UTF8.GetByteCount(answer.ToString(Formatting.None)) < fullBytes / 2);
        }
    }
}
