// -----------------------------------------------------------------------------
// Horizun Server tests - do not advertise a protocol feature we do not implement.
//
// `execution.taskSupport` is part of MCP Tasks (2025-11-25). A tool carrying
// taskSupport:"optional" is telling a client "you may run me as a task", and the
// way a client acts on that is tasks/create - which this server does not
// implement, and never declared: initialize returns capabilities {"tools":{}}
// with no "tasks" member at all.
//
// So the server was making a promise in one field that it withdrew in another.
// The safe-looking reading is the wrong one: a client that trusts the per-tool
// hint over the server-level capability calls a method that does not exist, and
// what it gets back is a JSON-RPC "method not found" for work it believes it
// submitted.
//
// The honest position for this release is that the bridge has ITS OWN queue -
// horizun_submit_job plus horizun_job_status, which are real, tested and
// documented - and that this is NOT MCP Tasks. If tasks/* is ever implemented
// and proved against the spec, the hint comes back with the capability beside
// it, and these tests are what will have to be deliberately changed.
// -----------------------------------------------------------------------------
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class NoUnimplementedTaskSupportTests
    {
        /// <summary>Not one tool claims task support while tasks/* is unimplemented.</summary>
        [Fact]
        public void No_tool_advertises_execution_taskSupport()
        {
            JArray tools = Tools.List();
            Assert.NotEmpty(tools);

            var offenders = tools.OfType<JObject>()
                                 .Where(t => t["execution"] != null)
                                 .Select(t => (string)t["name"])
                                 .ToList();

            Assert.True(offenders.Count == 0,
                "These tools advertise an execution/taskSupport block while this server implements no " +
                "tasks/* method and declares no \"tasks\" capability: " + string.Join(", ", offenders) +
                ". Either implement MCP Tasks fully and declare the capability, or do not hint at it.");
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
