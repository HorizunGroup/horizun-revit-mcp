// horizun_template_kit against a small kit written to a temp file: no Revit, no network.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class TemplateKitToolTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hzkit-" + Guid.NewGuid().ToString("N"));
        private readonly string _path;

        public TemplateKitToolTests()
        {
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "Plantilla.hzkit.json");
            var kit = new JObject
            {
                ["schema"] = "horizun.template-kit/1",
                ["template"] = new JObject { ["name"] = "Plantilla" },
                ["mcp"] = new JObject { ["contract_hash"] = Horizun.Contracts.Contract.Hash },
                ["conventions"] = new JObject { ["units"] = "mm" },
                ["catalog"] = new JArray(
                    Entry("HZ_Door_Flush wood door with louver|800x2100", "Doors", "22.08.14.16-004", "Puerta de baño con rejilla", "wall", 800),
                    Entry("HZ_Door_Flush wood door with louver|700x2100", "Doors", "22.08.14.16-004", "Puerta de baño con rejilla", "wall", 700),
                    Entry("HZ_Furniture_Bed|Queen 1600x2000", "Furniture", "22.12.58.29-001", "Cama", null, 1600)),
                ["rules"] = new JArray(new JObject { ["topic"] = "doors by room", ["rows"] = new JArray() }),
                ["playbook"] = new JObject { ["walls"] = new JObject { ["goal"] = "place walls", ["steps"] = new JArray() } },
                ["checks"] = new JArray(new JObject { ["name"] = "no uncoded types" })
            };
            File.WriteAllText(_path, kit.ToString());
        }

        private static JObject Entry(string key, string cat, string code, string es, string host, double width) => new JObject
        {
            ["key"] = key, ["family"] = key.Split('|')[0], ["type"] = key.Split('|')[1], ["category"] = cat, ["code"] = code,
            ["name_es"] = es, ["dims"] = new JObject { ["Width"] = width },
            ["placement"] = new JObject { ["host"] = host, ["recipe"] = new JObject { ["kind"] = "family_instance" } }
        };

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private JObject Call(JObject args) => TemplateKitTool.Handle(args, CancellationToken.None);

        [Fact]
        public void Load_briefs_and_reports_the_contract_match()
        {
            JObject r = Call(new JObject { ["path"] = _path });
            Assert.Equal(3, (int)r["catalog_summary"]["types"]);
            Assert.True((bool)r["contract_match"]["match"]);
            Assert.Equal("walls", (string)r["playbook_tasks"][0]["task"]);
        }

        [Fact]
        public void Version_is_reported_for_load_validate_and_list_and_missing_version_is_explained()
        {
            foreach (string op in new[] { "load", "validate" })
            {
                JObject missing = Call(new JObject { ["path"] = _path, ["operation"] = op });
                Assert.Equal(JTokenType.Null, missing["kit_version"].Type);
                Assert.NotNull(missing["kit_version_reason"]);
            }
            JObject kit = JObject.Parse(File.ReadAllText(_path));
            kit["kit_version"] = "2023.3.6";
            File.WriteAllText(_path, kit.ToString());
            foreach (string op in new[] { "load", "validate" })
            {
                JObject reply = Call(new JObject { ["path"] = _path, ["operation"] = op });
                Assert.Equal("2023.3.6", (string)reply["kit_version"]);
                Assert.Null(reply["kit_version_reason"]);
            }
            {
                string kits = Path.Combine(_dir, "kits");
                Directory.CreateDirectory(kits);
                File.Copy(_path, Path.Combine(kits, "versioned.hzkit.json"));
                kit.Remove("kit_version");
                File.WriteAllText(Path.Combine(kits, "missing.hzkit.json"), kit.ToString());
                var rows = (JArray)TemplateKitTool.List(kits)["kits"];
                JObject versioned = rows.OfType<JObject>().Single(x => (string)x["kit_name"] == "versioned");
                JObject missing = rows.OfType<JObject>().Single(x => (string)x["kit_name"] == "missing");
                Assert.Equal("2023.3.6", (string)versioned["kit_version"]);
                Assert.Equal(JTokenType.Null, missing["kit_version"].Type);
                Assert.NotNull(missing["kit_version_reason"]);
            }
        }

        [Fact]
        public void Model_path_finds_the_kit_beside_the_model()
        {
            JObject r = Call(new JObject { ["model_path"] = Path.Combine(_dir, "Plantilla.rte") });
            Assert.Equal(_path, (string)r["path"]);
        }

        [Fact]
        public void Find_is_accent_insensitive_and_filters_by_host_and_dims()
        {
            JObject r = Call(new JObject { ["path"] = _path, ["operation"] = "find", ["text"] = "puerta bano", ["host"] = "wall",
                                           ["dims"] = new JObject { ["Width"] = new JArray(750, null) } });
            Assert.Equal(1, (int)r["total"]);
            Assert.Equal("HZ_Door_Flush wood door with louver|800x2100", (string)r["hits"][0]["key"]);
        }

        [Fact]
        public void Validate_flags_duplicate_keys_and_a_missing_code()
        {
            JObject kit = JObject.Parse(File.ReadAllText(_path));
            ((JArray)kit["catalog"]).Add(kit["catalog"][0].DeepClone());
            kit["catalog"][1]["code"] = "";
            File.WriteAllText(_path, kit.ToString());
            JObject r = Call(new JObject { ["path"] = _path, ["operation"] = "validate" });
            Assert.False((bool)r["valid"]);
            Assert.Contains(((JArray)r["problems"]).Select(p => (string)p), p => p.StartsWith("duplicate key"));
            Assert.Contains(((JArray)r["problems"]).Select(p => (string)p), p => p.EndsWith("has no code"));
        }

        [Fact]
        public void Playbook_and_unknown_operation()
        {
            Assert.Equal("place walls", (string)Call(new JObject { ["path"] = _path, ["operation"] = "playbook", ["task"] = "WALLS" })["playbook"]["goal"]);
            Assert.Throws<ArgumentException>(() => Call(new JObject { ["path"] = _path, ["operation"] = "nope" }));
        }

        private static JObject Row(int id, string cat, string fam, double[] box, double[] facing = null, int? host = null)
        {
            var r = new JObject
            {
                ["element_id"] = id, ["category"] = cat, ["family"] = fam, ["type"] = "t",
                ["bounding_box"] = new JObject { ["min"] = new JArray(box[0], box[1], box[2]), ["max"] = new JArray(box[3], box[4], box[5]) }
            };
            if (facing != null) r["placement"] = new JObject { ["facing"] = new JArray(facing[0], facing[1], 0.0) };
            if (host != null) r["host_id"] = host.Value;
            return r;
        }

        [Fact]
        public void Front_clearance_flags_an_obstacle_in_front_and_ignores_what_the_fixture_sits_in()
        {
            var kit = new JObject
            {
                ["rules"] = new JArray(new JObject
                {
                    ["topic"] = "clearances",
                    ["rows"] = new JArray(new JObject { ["mm"] = 600, ["front_of"] = "water closet" }, new JObject { ["mm"] = 900, ["front_of"] = new JArray("countertop") })
                })
            };
            // a WC against the wall at y=3000 (facing +Y = its back): front is -Y; a wall 400 mm in front blocks it
            var wc = Row(1, "Plumbing Fixtures", "HZ_Plumbing_Residential water closet", new double[] { 0, 2340, 0, 400, 3000, 770 }, new double[] { 0, 1 });
            var wallInFront = Row(2, "Walls", "Basic Wall", new double[] { -500, 1900, 0, 1000, 2030, 2800 });
            // a counter with a sink inside it: the sink overlaps the counter, so it is not an obstacle
            var counter = Row(3, "Casework", "HZ_Casework_Quartz countertop", new double[] { 2000, 2400, 0, 4000, 3000, 900 }, new double[] { 0, 1 });
            var sink = Row(4, "Plumbing Fixtures", "HZ_Plumbing_Kitchen sink - single", new double[] { 2500, 2450, 700, 3100, 2950, 1020 }, new double[] { 0, 1 });
            var blocked = TemplateKitTool.FrontClearance(kit, new System.Collections.Generic.List<JObject> { wc, wallInFront, counter, sink }, out int judged);
            Assert.Equal(2, judged);
            Assert.Single(blocked);
            Assert.Equal(1, (int)blocked[0]["element_id"]);
            Assert.Equal(2, (int)blocked[0]["blocked_by"]["element_id"]);
        }

        [Fact]
        public void Accents_fold_without_unicode_normalization()
        {
            // the published server is InvariantGlobalization: the fold must not depend on string.Normalize
            Assert.Equal("puerta metalica / reja", TemplateKitTool.Norm("Puerta Metálica / Reja"));
            Assert.Equal("bano nino", TemplateKitTool.Norm("Baño NIÑO"));
            Assert.Equal("cafe", TemplateKitTool.Norm("café"));
        }

        [Fact]
        public void A_kit_of_another_schema_is_refused()
        {
            File.WriteAllText(_path, "{\"schema\":\"other/9\"}");
            Assert.Throws<ArgumentException>(() => Call(new JObject { ["path"] = _path }));
        }
    }
}
