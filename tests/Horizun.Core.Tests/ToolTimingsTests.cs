// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The timing fold. What matters: the ring is bounded, the averages are the
// two different questions they claim to be, and the snapshot orders by cost.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    [Collection("tool-timings")]
    public class ToolTimingsTests
    {
        public ToolTimingsTests() { ToolTimings.Reset(); }

        [Fact]
        public void The_lifetime_average_and_the_recent_average_answer_different_questions()
        {
            // 32 slow calls push the fast start out of the ring: the lifetime average
            // remembers it, the recent one does not.
            for (int i = 0; i < 10; i++) ToolTimings.Record("t", 10);
            for (int i = 0; i < ToolTimings.RingSize; i++) ToolTimings.Record("t", 1000);
            JObject tool = (JObject)ToolTimings.Snapshot()["tools"]["t"];
            Assert.Equal(42, (long)tool["calls"]);
            Assert.True((long)tool["avg_ms"] < 1000);
            Assert.Equal(1000, (long)tool["recent_avg_ms"]);
            Assert.Equal(ToolTimings.RingSize, (int)tool["recent_window"]);
        }

        [Fact]
        public void The_max_survives_leaving_the_ring()
        {
            ToolTimings.Record("t", 5000);
            for (int i = 0; i < ToolTimings.RingSize + 5; i++) ToolTimings.Record("t", 10);
            JObject tool = (JObject)ToolTimings.Snapshot()["tools"]["t"];
            Assert.Equal(5000, (long)tool["max_ms"]);
        }

        [Fact]
        public void Expensive_tools_lead_the_snapshot()
        {
            ToolTimings.Record("cheap", 1);
            ToolTimings.Record("dear", 10000);
            JObject tools = (JObject)ToolTimings.Snapshot()["tools"];
            string first = null;
            foreach (var property in tools) { first = property.Key; break; }
            Assert.Equal("dear", first);
        }

        [Fact]
        public void The_tool_table_is_bounded_and_drops_are_counted()
        {
            for (int i = 0; i < ToolTimings.MaxTools + 3; i++) ToolTimings.Record("tool-" + i, 1);
            JObject snapshot = ToolTimings.Snapshot();
            Assert.Equal(ToolTimings.MaxTools, (int)snapshot["tools_tracked"]);
            Assert.Equal(3, (long)snapshot["tools_dropped"]);
        }

        [Fact]
        public void Garbage_is_ignored_rather_than_recorded()
        {
            ToolTimings.Record(null, 5);
            ToolTimings.Record("t", -1);
            Assert.Equal(0, (int)ToolTimings.Snapshot()["tools_tracked"]);
        }
    }
}
