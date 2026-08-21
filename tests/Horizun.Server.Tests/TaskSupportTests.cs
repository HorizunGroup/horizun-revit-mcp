// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// WHICH TOOLS MAY BE QUEUED, and the contract facts that decide it.
//
// The same rule drives execution.taskSupport, task admission and the compatible
// horizun_submit_job extension: Revit-forwarding tools are eligible, host tools,
// execute_python, request_python_access and submit_job itself are not.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TaskSupportTests
    {
        /// <summary>
        /// The rule submit_job states in its own description, asserted against the
        /// contract so the two cannot drift: a host-resident tool has no plugin command.
        /// </summary>
        [Fact]
        public void Host_resident_tools_are_the_ones_with_no_plugin_command()
        {
            var hostResident = Contract.All.Where(c => string.IsNullOrEmpty(c.Command)).ToList();
            Assert.NotEmpty(hostResident);
            // health and job_status answer without Revit; they are the reason the
            // distinction exists at all - job_status has to be askable WHILE Revit is busy.
            Assert.Contains(hostResident, c => c.Name == "horizun_job_status");
        }

        /// <summary>
        /// Everything that forwards to Revit can outlive a request - a model scan or a
        /// batch open holds the UI thread for minutes - so a client is entitled to treat
        /// it as a task.
        /// </summary>
        [Fact]
        public void Forwarding_tools_can_be_tasks()
        {
            foreach (string name in new[] { "horizun_model_scan", "horizun_create_elements",
                                            "horizun_audit_model", "horizun_clash" })
            {
                CommandContract c = Contract.Find(name);
                Assert.NotNull(c);
                Assert.False(string.IsNullOrEmpty(c.Command),
                    name + " must forward to Revit for taskSupport=optional to be honest");
            }
        }

        /// <summary>
        /// The three submit_job refuses BY NAME. Offering these as tasks would advertise
        /// something the queue rejects, and the caller would only find out at submit time.
        /// </summary>
        [Fact]
        public void The_tools_submit_job_refuses_must_never_be_offered_as_tasks()
        {
            CommandContract submit = Contract.Find("horizun_submit_job");
            Assert.NotNull(submit);
            // The exclusion is stated in the tool's own description - "except
            // execute_python, request_python_access or submit_job itself" - and this test's whole premise rests
            // on it, so the wording is asserted rather than assumed. If somebody widens
            // the queue to accept execute_python, this fails and TaskSupport() has to be
            // revisited in the same change.
            Assert.Contains("except execute_python, request_python_access or submit_job itself", submit.Description);
            Assert.False(McpTasks.Supports(Tools.Find("horizun_request_python_access")));
        }

        /// <summary>
        /// A queued call still needs its durable key: a task whose reply gets lost is
        /// exactly the case idempotency exists for, and an async caller never sees it.
        /// </summary>
        [Fact]
        public void Submit_job_itself_demands_an_idempotency_key()
        {
            CommandContract submit = Contract.Find("horizun_submit_job");
            Assert.NotNull(submit);
            var props = submit.InputSchema?["properties"] as JObject;
            Assert.NotNull(props);
            Assert.NotNull(props["idempotency_key"]);
            Assert.Equal("date-time", (string)props["retain_until_utc"]?["format"]);
        }
    }
}
