// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_model_diff explain: the ISO 19650 block is the project_context
// evaluation, and its absences are named rather than filled in.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ModelExplainIsoTests
    {
        [Fact]
        public void No_path_is_not_requested_and_a_missing_file_is_not_found()
        {
            Assert.Equal("not_requested", (string)ModelExplainIso.Evaluate(null)["status"]);
            Assert.Equal("not_found", (string)ModelExplainIso.Evaluate(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))["status"]);
        }

        [Fact]
        public void An_empty_context_is_evaluated_by_the_project_context_validator_and_lists_what_is_missing()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-ctx-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "{}");
                JObject r = ModelExplainIso.Evaluate(path);
                Assert.Equal("evaluated", (string)r["status"]);
                Assert.NotEqual("complete", (string)r["state"]);
                Assert.True(((JArray)r["missing"]).Count > 0);
                Assert.True(((JArray)r["missing_topics"]).Count > 0);

                File.WriteAllText(path, "{ nope");
                Assert.Equal("unreadable", (string)ModelExplainIso.Evaluate(path)["status"]);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
