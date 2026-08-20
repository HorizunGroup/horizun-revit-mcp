// -----------------------------------------------------------------------------
// Horizun Server tests - advertise MCP Tasks only when the negotiated protocol and
// the underlying durable queue can support them.
//
// Down-level clients see no execution block. 2025-11-25 clients see optional
// exactly for Revit-forwarding tools accepted by submit_job, and forbidden for
// host tools plus the two explicit exclusions.
// -----------------------------------------------------------------------------
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class NoUnimplementedTaskSupportTests
    {
        [Fact]
        public void Downlevel_tool_list_does_not_advertise_task_support()
        {
            JArray tools = Tools.List();
            Assert.NotEmpty(tools);

            var offenders = tools.OfType<JObject>()
                                 .Where(t => t["execution"] != null)
                                 .Select(t => (string)t["name"])
                                 .ToList();

            Assert.True(offenders.Count == 0,
                "Down-level MCP clients must not receive a 2025-11-25 execution block: " +
                string.Join(", ", offenders));
        }

        [Fact]
        public void Current_tool_list_marks_exactly_supported_tasks_optional()
        {
            JArray tools = Tools.List(true);
            Assert.NotEmpty(tools);
            foreach (JObject tool in tools)
            {
                ToolDef def = Tools.Find((string)tool["name"]);
                string expected = McpTasks.Supports(def) ? "optional" : "forbidden";
                Assert.Equal(expected, (string)tool["execution"]["taskSupport"]);
            }
        }

        /// <summary>
        /// The replacement is not "nothing". submit_job and job_status are the bridge's
        /// own long-running-work path, and they must stay listed - removing an MCP hint
        /// is not the same as removing the capability it was hinting at.
        /// </summary>
        [Fact]
        public void The_proprietary_queue_is_still_offered()
        {
            var names = Tools.List().OfType<JObject>().Select(t => (string)t["name"]).ToList();

            Assert.Contains("horizun_submit_job", names);
            Assert.Contains("horizun_job_status", names);
        }
    }
}
