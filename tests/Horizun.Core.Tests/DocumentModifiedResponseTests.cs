using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DocumentModifiedResponseTests
    {
        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", name));
        }

        [Fact]
        public void Health_and_document_info_report_the_actual_modified_flag()
        {
            string health = Source("HealthCommand.cs");
            string info = Source("GetDocumentInfoCommand.cs");
            Assert.Contains("is_modified = SafeModified(d)", health);
            Assert.Contains("is_modified = SafeModified(active)", health);
            Assert.Contains("is_modified = SafeModified(doc)", info);
            foreach (bool? flag in new bool?[] { true, false, null })
            {
                var json = JObject.FromObject(new { is_modified = flag });
                Assert.Equal(flag, json.Value<bool?>("is_modified"));
                Assert.NotNull(json.Property("is_modified"));
            }
        }
    }
}
