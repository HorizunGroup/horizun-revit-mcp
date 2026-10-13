// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Backlog #42: the VisualizationInfo Guid was new on every export and did not match
// the markup's Viewpoints Guid, so a reader that links the two (as BCF 2.1 means it)
// found no camera for the topic.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class BcfViewpointGuidTests
    {
        [Fact]
        public void The_ids_bcf_viewpoint_carries_the_guid_its_markup_names()
        {
            var topic = new IdsBcfTopic
            {
                Guid = IdsBcf.TopicGuid(new string('a', 64), "spec", "W1"),
                Specification = "spec", Title = "t", Description = "d", FailingElements = 1,
                PointMm = new[] { 1000.0, 2000.0, 3000.0 }
            };
            topic.GlobalIds.Add("0aaaaaaaaaaaaaaaaaaaa1");
            string path = Path.Combine(Path.GetTempPath(), "hz-bcf-" + Guid.NewGuid().ToString("N") + ".bcfzip");
            try
            {
                string error;
                Assert.NotNull(IdsBcf.WriteAndVerify(path, new[] { topic }, "m.ifc", null, DateTime.UtcNow, out error));
                Assert.Null(error);
                using (var zip = ZipFile.OpenRead(path))
                {
                    var markup = new XmlDocument();
                    using (var s = zip.GetEntry(topic.Guid + "/markup.bcf").Open()) markup.Load(s);
                    var view = new XmlDocument();
                    using (var s = zip.GetEntry(topic.Guid + "/" + IdsBcf.ViewpointFile).Open()) view.Load(s);
                    string named = markup.DocumentElement.SelectSingleNode("Viewpoints/@Guid").Value;
                    string carried = view.DocumentElement.GetAttribute("Guid");
                    Assert.Equal(named, carried);
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void The_same_finding_exports_the_same_viewpoint_guid_twice()
        {
            string a = BcfViewpoint.Xml(new[] { 0.0, 0.0, 0.0 }, null, BcfViewpoint.DefaultStandoffMm,
                                        CoordinationRules.ViewpointGuid(new string('b', 32)));
            string b = BcfViewpoint.Xml(new[] { 0.0, 0.0, 0.0 }, null, BcfViewpoint.DefaultStandoffMm,
                                        CoordinationRules.ViewpointGuid(new string('b', 32)));
            Assert.Equal(a, b);
            Assert.Contains("Guid=\"" + CoordinationRules.ViewpointGuid(new string('b', 32)) + "\"", a);
        }

        [Fact]
        public void The_coordination_markup_names_the_viewpoint_guid_the_command_writes()
        {
            string src = File.ReadAllText(Path.Combine(Root(), "src", "Horizun.Revit", "Commands", "CoordinationCommand.cs"));
            Assert.Contains("BcfViewpoint.Xml(f.PointMm, components, BcfViewpoint.DefaultStandoffMm, CoordinationRules.ViewpointGuid(f.Id))", src);
        }

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return d.FullName;
        }
    }
}
