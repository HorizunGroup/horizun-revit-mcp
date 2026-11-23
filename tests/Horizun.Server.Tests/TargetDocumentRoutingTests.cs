using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TargetDocumentRoutingTests
    {
        private static readonly Discovered[] Instances = { new Discovered { Pid = 1 }, new Discovered { Pid = 2 } };
        private static JObject Probe(int pid, string title, string path) => new JObject {
            ["pid"] = pid, ["open_documents_complete"] = true,
            ["open_documents"] = new JArray(new JObject { ["title"] = title, ["path"] = path }) };
        [Fact]
        public void Unique_title_or_exact_path_selects_the_instance()
        {
            var rows = new JArray(Probe(1, "A", @"C:\a.rvt"), Probe(2, "B", @"D:\b.rvt"));
            Assert.Equal(2, InstanceRouting.MatchOpenDocument("B.rvt", Instances, rows).Pid);
            Assert.Equal(1, InstanceRouting.MatchOpenDocument(@"C:\a.rvt", Instances, rows).Pid);
            Assert.Null(InstanceRouting.MatchOpenDocument(@"D:\a.rvt", Instances, rows));
        }
        [Fact]
        public void Duplicate_missing_or_unavailable_probes_do_not_guess()
        {
            Assert.Null(InstanceRouting.MatchOpenDocument("A", Instances, new JArray(Probe(1, "A", null), Probe(2, "A", null))));
            Assert.Null(InstanceRouting.MatchOpenDocument("Z", Instances, new JArray(Probe(1, "A", null), Probe(2, "B", null))));
            var unknown = Probe(2, "B", null); unknown["open_documents_complete"] = false;
            Assert.Null(InstanceRouting.MatchOpenDocument("A", Instances, new JArray(Probe(1, "A", null), unknown)));
        }
        private static readonly Discovered[] Three =
            { new Discovered { Pid = 1, Year = "2026" }, new Discovered { Pid = 2, Year = "2027" }, new Discovered { Pid = 3, Year = "2023" } };

        private static JObject Docs(int pid, params (string title, string path, bool modified)[] docs)
        {
            var list = new JArray();
            foreach (var d in docs) list.Add(new JObject { ["title"] = d.title, ["path"] = d.path, ["is_modified"] = d.modified, ["is_linked"] = false });
            return new JObject { ["pid"] = pid, ["open_documents_complete"] = true, ["open_documents"] = list };
        }

        [Fact]
        public void Open_in_one_of_several_instances_goes_there_with_no_refusal()
        {
            var probes = new JArray(Docs(1, ("A", @"C:\a.rvt", false)), Docs(2, ("B", @"D:\b.rvt", true)), Docs(3, ("C", @"E:\c.rvt", false)));
            InstanceRouting.DocumentMatch m = InstanceRouting.MatchDocument("B", Three, probes);
            Assert.Equal(2, m.Instance.Pid);
            Assert.Equal(@"D:\b.rvt", (string)m.Document["path"]);
            Assert.Null(InstanceRouting.DocumentRefusal("B", m));
        }

        [Fact]
        public void Same_model_open_in_two_instances_is_refused_naming_each_with_its_path()
        {
            var probes = new JArray(Docs(1, ("A", @"C:\a.rvt", true)), Docs(2, ("A", @"\\srv\a.rvt", false)), Docs(3, ("C", @"E:\c.rvt", false)));
            InstanceRouting.DocumentMatch m = InstanceRouting.MatchDocument("A.rvt", Three, probes);
            Assert.Null(m.Instance);
            Assert.Equal(2, m.OpenIn.Count);
            JObject r = InstanceRouting.DocumentRefusal("A.rvt", m);
            string text = (string)r["text"];
            Assert.Contains("open in 2 Revit instances", text);
            Assert.Contains(@"Revit 2026 pid 1 (C:\a.rvt, unsaved changes)", text);
            Assert.Contains(@"Revit 2027 pid 2 (\\srv\a.rvt)", text);
            Assert.Contains("Close the one you do not mean", text);
            Assert.Equal("open_in_several_instances", (string)r["extra"]["target_document_state"]);
            Assert.False((bool)r["extra"]["write_started"]);
            Assert.Equal(2, ((JArray)r["extra"]["retry_with"]).Count);
        }

        [Fact]
        public void Model_open_nowhere_is_refused_listing_what_is_open()
        {
            var probes = new JArray(Docs(1, ("A", @"C:\a.rvt", false)), Docs(2, ("B", @"D:\b.rvt", false)), Docs(3));
            InstanceRouting.DocumentMatch m = InstanceRouting.MatchDocument("Z", Three, probes);
            Assert.Null(m.Instance);
            JObject r = InstanceRouting.DocumentRefusal("Z", m);
            Assert.Contains("'Z' is not open in any running Revit", (string)r["text"]);
            Assert.Contains(@"Revit 2026 pid 1 (C:\a.rvt)", (string)r["text"]);
            Assert.Contains(@"Revit 2027 pid 2 (D:\b.rvt)", (string)r["text"]);
            Assert.Equal("not_open", (string)r["extra"]["target_document_state"]);
            Assert.Equal(2, ((JArray)r["extra"]["open_documents"]).Count);
        }

        [Fact]
        public void An_incomplete_probe_says_nothing_about_where_the_model_is()
        {
            var unknown = Docs(2, ("B", @"D:\b.rvt", false)); unknown["open_documents_complete"] = false;
            Assert.Null(InstanceRouting.MatchDocument("A", Three, new JArray(Docs(1, ("A", @"C:\a.rvt", false)), unknown, Docs(3))));
            Assert.Null(InstanceRouting.DocumentRefusal("A", null));
        }

        [Fact]
        public void Refusal_suggests_each_pid()
        {
            var result = InstanceRouting.AmbiguityExtra(new JArray(new JObject { ["pid"] = 1 }, new JObject { ["pid"] = 2 }));
            Assert.Equal(2, ((JArray)result["retry_with"]).Count);
            Assert.Equal(2, (int)result["retry_with"][1]["arguments"]["pid"]);
        }
    }
}
