using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class AsyncResultPayloadTests
    {
        [Fact]
        public void File_backed_result_is_copied_under_the_job_store_before_serialization()
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string root = Path.Combine(Path.GetTempPath(), "hz-async-payload-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, root);
                Directory.CreateDirectory(root);
                string source = Path.Combine(root, "temporary.png");
                File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
                string jobId = Guid.NewGuid().ToString("N");

                JObject payload = JObject.Parse(AsyncResultPayload.Serialize(
                    new JObject { ["captured"] = true, ["image_path"] = source }, jobId));
                string durable = (string)payload["image_path"];
                Assert.Equal(Path.Combine(HorizunPaths.JobsDir(), "attachments", jobId + ".png"), durable);
                File.Delete(source);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(durable));
            }
            finally
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
