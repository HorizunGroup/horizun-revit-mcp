// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Field 2026-11 (feedback §9): when horizun_execute_python runs past its handoff time, the
// reply must say it is NOT the result, name the job, the time already waited and how to
// follow it. This pins those four things for the Python path and the export path.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PythonHandoffReplyTests
    {
        [Theory]
        [InlineData("horizun_execute_python")]
        [InlineData("horizun_export")]
        public void The_timeout_reply_carries_the_job_id_the_elapsed_time_and_how_to_follow_it(string command)
        {
            JObject p = PythonHandoff.Payload("job-77", 45321, 2, "paso 2", null, command);
            Assert.Equal("job-77", (string)p["job_id"]);
            Assert.Equal(45321, (long)p["waited_ms"]);
            Assert.Equal(CommandDeadlinePolicy.PythonHandoffMs, (int)p["handoff_after_ms"]);
            Assert.Equal("horizun_job_status job_id=job-77", (string)p["follow_with"]);
            Assert.Contains("horizun_job_status job_id=job-77", (string)p["what_this_means"]);
            Assert.Equal("running", (string)p["state"]);
            Assert.False((bool)p["completed"]);
        }
    }
}
