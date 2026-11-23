using System;
using System.IO;
using Autodesk.Revit.DB;
using Horizun.Revit.Commands;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HealthModelSizeIntegrationTests
    {
        [Fact]
        public void Measures_large_document_and_reports_declared_threshold()
        {
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "12345");
                var doc = new Document { PathName = file, IsWorkshared = true, TestElements = 120000, TestLinks = 7, TestWorksets = 3 };
                JObject result = JObject.FromObject(HealthModelSize.Measure(doc));
                Assert.Equal(120000, (int)result["element_count"]);
                Assert.Equal(5, (long)result["file_size_bytes"]);
                Assert.Equal(7, (int)result["link_instances"]);
                Assert.Equal(3, (int)result["workset_count"]);
                Assert.True((bool)result["large_model"]);
                Assert.Equal(100000, (int)result["large_model_threshold"]["element_count"]);
                Assert.Equal(2, doc.TestNativeCounts);
            }
            finally { File.Delete(file); }
        }

        [Fact]
        public void Failed_element_count_is_unknown_not_small()
        {
            var doc = new Document { TestCountThrows = true };
            JObject result = JObject.FromObject(HealthModelSize.Measure(doc));
            Assert.Equal(JTokenType.Null, result["element_count"].Type);
            Assert.Equal(JTokenType.Null, result["large_model"].Type);
        }
    }
}
