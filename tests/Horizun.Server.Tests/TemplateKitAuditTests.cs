// horizun_template_kit operation=audit against a scripted bridge: every tools/call the audit
// makes is answered from a table here, so what is judged is the audit's own logic. No Revit.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("TemplateKitInvoker")]
    public class TemplateKitAuditTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hzkit-audit-" + Guid.NewGuid().ToString("N"));
        private readonly string _path;
        private readonly Func<JObject, CancellationToken, JToken> _saved;
        private readonly List<JObject> _calls = new List<JObject>();
        // geometry_quality reply: by default every product type reads detailed
        private JObject _geometry = new JObject { ["mode"] = "geometry_quality", ["types"] = new JArray(), ["coverage_complete"] = true };

        public TemplateKitAuditTests()
        {
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "Plantilla.hzkit.json");
            var kit = new JObject
            {
                ["schema"] = "horizun.template-kit/1",
                ["template"] = new JObject { ["name"] = "Plantilla" },
                ["mcp"] = new JObject { ["contract_hash"] = Horizun.Contracts.Contract.Hash },
                ["catalog"] = new JArray(
                    Entry("HZ_Plumbing_Lavatory|500", "Plumbing Fixtures"),
                    Entry("HZ_Plumbing_Shower|900", "Plumbing Fixtures"),
                    Entry("HZ_Casework_Vanity|900", "Casework")),
                ["rules"] = new JArray(),
                ["playbook"] = new JObject(),
                ["checks"] = new JArray()
            };
            File.WriteAllText(_path, kit.ToString());
            _saved = TemplateKitTool.Invoker;
        }

        public void Dispose()
        {
            TemplateKitTool.Invoker = _saved;
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static JObject Entry(string key, string cat) => new JObject
        {
            ["key"] = key, ["family"] = key.Split('|')[0], ["type"] = key.Split('|')[1], ["category"] = cat, ["code"] = "22.00",
            ["placement"] = new JObject { ["host"] = null, ["recipe"] = new JObject { ["kind"] = "family_instance" } }
        };

        private static JObject Row(int id, string cat, string fam, string type, double[] box) => new JObject
        {
            ["element_id"] = id, ["category"] = cat, ["family"] = fam, ["type"] = type,
            ["parameters"] = new JObject {
                ["HZ_AI_Source"] = new JObject { ["exists"] = true, ["raw"] = "plano A-101" },
                ["HZ_AI_Status"] = new JObject { ["exists"] = true, ["raw"] = "checked" } },
            ["bounding_box"] = new JObject { ["min"] = new JArray(box[0], box[1], box[2]), ["max"] = new JArray(box[3], box[4], box[5]) }
        };

        private static JObject Clash(int a, string catA, int b, string catB, double m3, string sourceB = "host") => new JObject
        {
            ["a"] = new JObject { ["element_id"] = a.ToString(), ["source_model"] = "host", ["category"] = catA, ["name"] = "a" },
            ["b"] = new JObject { ["element_id"] = b.ToString(), ["source_model"] = sourceB, ["category"] = catB, ["name"] = "b" },
            ["intersection_volume_m3"] = m3
        };

        /// <summary>
        /// A bridge in one table. clashFor decides each horizun_clash reply from its arguments,
        /// exactly as the real tool would see them.
        /// </summary>
        private void Bridge(List<JObject> rows, Func<JObject, JObject> clashFor, JObject span = null)
        {
            TemplateKitTool.Invoker = (call, ct) =>
            {
                _calls.Add((JObject)call.DeepClone());
                string tool = (string)call["name"]; var args = (JObject)call["arguments"];
                JObject body;
                switch (tool)
                {
                    case "horizun_query_classification":
                        body = new JObject { ["placed_types_without_code"] = 0, ["types_with_unknown_code"] = 0 }; break;
                    case "horizun_query_model":
                        body = new JObject { ["rows"] = new JArray(rows), ["next_cursor"] = null }; break;
                    case "horizun_audit_model":
                        body = new JObject { ["findings"] = new JArray(
                            new JObject { ["check"] = "warnings", ["count"] = 0, ["items"] = new JArray() },
                            span ?? new JObject { ["check"] = "level_span", ["count"] = 0,
                                ["coverage_complete"] = true, ["exceptions_total"] = 0, ["items"] = new JArray() }) }; break;
                    case "horizun_clash":
                        body = clashFor(args); break;
                    case "horizun_query_family_internals":
                        body = (JObject)_geometry.DeepClone(); break;
                    default: throw new InvalidOperationException("unexpected tool " + tool);
                }
                return new JObject { ["structuredContent"] = body };
            };
        }

        private JObject Audit() => TemplateKitTool.Handle(new JObject
        {
            ["path"] = _path, ["operation"] = "audit", ["target_document"] = "Casa_Arq"
        }, CancellationToken.None);

        private static bool Has(JArray cats, string c) => cats != null && cats.Values<string>().Contains(c);

        [Fact]
        public void Audit_reports_declared_and_missing_kit_version()
        {
            Bridge(new List<JObject>(), _ => new JObject { ["clashes"] = new JArray(), ["total"] = 0 });
            JObject absent = Audit();
            Assert.Equal(JTokenType.Null, absent["kit_version"].Type);
            Assert.NotNull(absent["kit_version_reason"]);
            JObject kit = JObject.Parse(File.ReadAllText(_path));
            kit["kit_version"] = "2023.3.6";
            File.WriteAllText(_path, kit.ToString());
            JObject present = Audit();
            Assert.Equal("2023.3.6", (string)present["kit_version"]);
            Assert.Null(present["kit_version_reason"]);
        }

        [Fact]
        public void Resolves_installed_kit_from_the_matching_open_model_and_refuses_other_models()
        {
            string name = "test-" + Guid.NewGuid().ToString("N");
            string kits = TemplateKitTool.KitsDir();
            Directory.CreateDirectory(kits);
            string installed = Path.Combine(kits, name + ".hzkit.json");
            File.Copy(_path, installed);
            string model = Path.Combine(_dir, "other", "model.rvt");
            string open = model;
            Bridge(new List<JObject>(), _ => new JObject { ["clashes"] = new JArray(), ["total"] = 0 });
            var bridge = TemplateKitTool.Invoker;
            TemplateKitTool.Invoker = (call, ct) =>
            {
                string tool = (string)call["name"];
                if (tool == "get_document_info") return new JObject { ["structuredContent"] = new JObject { ["path"] = open } };
                if (tool == "horizun_manage_units")
                {
                    Assert.Equal(model, (string)call["arguments"]["target_document"]);
                    return new JObject { ["structuredContent"] = new JObject { ["parameters"] = new JArray(new JObject { ["name"] = "HZ_AI_Kit", ["value"] = name }) } };
                }
                return bridge(call, ct);
            };
            try
            {
                var args = new JObject { ["operation"] = "audit", ["model_path"] = model, ["target_document"] = model };
                Assert.Equal("model_parameter", (string)TemplateKitTool.Handle(args, CancellationToken.None)["kit_resolved_by"]);
                args["operation"] = "checks";
                Assert.Equal("model_parameter", (string)TemplateKitTool.Handle(args, CancellationToken.None)["kit_resolved_by"]);
                open = Path.Combine(_dir, "another.rvt");
                Assert.Contains("open the model or pass kit_name", Assert.Throws<ArgumentException>(() => TemplateKitTool.Handle(args, CancellationToken.None)).Message);
            }
            finally { File.Delete(installed); }
        }

        [Fact]
        public void A_placeholder_box_sanitary_is_a_generic_geometry_correction()
        {
            // field cycle 2026-10-24: "los sanitarios son horribles, una cosa generica" - coded, hosted and connected,
            // and nothing measured the geometry. One box_like type is a correction with its reasons.
            _geometry = new JObject
            {
                ["mode"] = "geometry_quality", ["coverage_complete"] = true,
                ["types"] = new JArray(
                    new JObject { ["family"] = "HZ_Plumbing_Lavatory", ["type"] = "500", ["category"] = "Plumbing Fixtures", ["instances"] = 12,
                                  ["sample_instance_id"] = 1, ["verdict"] = "box_like", ["reasons"] = new JArray("1 solid(s), every one a plain box") },
                    new JObject { ["family"] = "HZ_Plumbing_Shower", ["type"] = "900", ["category"] = "Plumbing Fixtures", ["instances"] = 3,
                                  ["sample_instance_id"] = 2, ["verdict"] = "detailed", ["reasons"] = new JArray("80 faces") })
            };
            Bridge(new List<JObject> { Row(1, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 0, 0, 0, 500, 400, 900 }) },
                args => new JObject { ["result"] = "complete", ["clash_count"] = 0, ["clashes"] = new JArray() });
            JObject result = Audit();
            JObject f = ((JArray)result["findings"]).OfType<JObject>().Single(x => (string)x["check"] == "generic_geometry");
            Assert.Equal("correction", (string)f["severity"]);
            Assert.Equal(1, (int)f["count"]);
            Assert.Equal(12, (int)f["instances"]);
            Assert.Equal("HZ_Plumbing_Lavatory", (string)f["sample"][0]["family"]);
            Assert.Equal("usable_with_corrections", (string)result["verdict"]);
            JObject call = _calls.Single(c => (string)c["name"] == "horizun_query_family_internals");
            Assert.Equal("geometry_quality", (string)call["arguments"]["mode"]);
            Assert.True(Has((JArray)call["arguments"]["categories"], "OST_PlumbingFixtures"));
            Assert.True(Has((JArray)call["arguments"]["categories"], "OST_Doors"));
        }

        [Fact]
        public void Generic_geometry_that_could_not_be_read_is_not_a_pass()
        {
            Assert.Equal("not_assessable", (string)TemplateKitTool.GenericGeometryFinding(null, 5)["severity"]);
            var partial = new JObject { ["types"] = new JArray(), ["coverage_complete"] = false };
            Assert.Equal("not_assessable", (string)TemplateKitTool.GenericGeometryFinding(partial, 5)["severity"]);
            var clean = new JObject { ["types"] = new JArray(new JObject { ["verdict"] = "simple" }), ["coverage_complete"] = true };
            Assert.Null(TemplateKitTool.GenericGeometryFinding(clean, 5));
        }

        [Fact]
        public void Level_span_is_blocking_and_documented_exceptions_are_reported()
        {
            var span = new JObject {
                ["check"] = "level_span", ["count"] = 1, ["coverage_complete"] = true,
                ["items"] = new JArray(new JObject { ["element_id"] = 77,
                    ["category"] = "Walls", ["crossed_levels"] = new JArray("N+03.00") }),
                ["exceptions_total"] = 1,
                ["exceptions"] = new JArray(new JObject { ["element_id"] = 78,
                    ["exception_reason"] = "fachada continua" }) };
            Bridge(new List<JObject> { Row(1, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500",
                new double[] { 0, 0, 0, 500, 400, 900 }) },
                args => new JObject { ["result"] = "complete", ["clash_count"] = 0,
                    ["clashes"] = new JArray() }, span);
            JObject result = Audit();
            Assert.Equal("not_yet", (string)result["verdict"]);
            Assert.Contains(((JArray)result["findings"]).OfType<JObject>(),
                f => (string)f["check"] == "level_span" && (int)f["count"] == 1);
            Assert.Contains(((JArray)result["findings"]).OfType<JObject>(),
                f => (string)f["check"] == "level_span_exceptions" && (int)f["count"] == 1);
        }

        [Fact]
        public void A_loaded_link_does_not_leave_fit_out_in_walls_unassessed()
        {
            // INFORME_P6 §6.2: with Casa_Est.rvt linked the audit called horizun_clash with
            // include_links=false, the clash answered "partial (1 link(s) not checked)" and the
            // audit said not_assessable - while horizun_clash include_links=true is complete.
            var rows = new List<JObject> { Row(1, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 0, 0, 800, 500, 400, 900 }) };
            Bridge(rows, args =>
            {
                bool links = args.Value<bool?>("include_links") ?? true;
                return links
                    ? new JObject { ["result"] = "complete", ["clash_count"] = 0, ["clashes"] = new JArray(), ["headline"] = "No clashes" }
                    : new JObject { ["result"] = "partial", ["clash_count"] = 0, ["clashes"] = new JArray(), ["headline"] = "No clashes found, but the check was PARTIAL (1 link(s) not checked)" };
            });
            JObject r = Audit();
            var wallCheck = _calls.First(c => (string)c["name"] == "horizun_clash" && Has(c["arguments"]["categories_b"] as JArray, "OST_Walls"));
            Assert.True((bool)wallCheck["arguments"]["include_links"]);
            Assert.DoesNotContain(((JArray)r["findings"]).OfType<JObject>(), f => (string)f["check"] == "fit_out_in_walls");
            Assert.Equal("usable_today", (string)r["verdict"]);
        }

        [Fact]
        public void A_linked_wall_with_the_same_id_as_the_host_is_still_a_clash()
        {
            // the bracket exemption (a fixture inside ITS OWN host wall) holds only for a wall of
            // this model: an element id of a linked model names another element entirely.
            var lav = Row(1, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 0, 0, 800, 500, 400, 900 });
            lav["host_id"] = 77;
            Bridge(new List<JObject> { lav }, args => Has(args["categories_b"] as JArray, "OST_Walls")
                ? new JObject { ["result"] = "complete", ["clash_count"] = 1, ["clashes"] = new JArray(Clash(1, "Plumbing Fixtures", 77, "Walls", 0.01, "Casa_Est.rvt")) }
                : new JObject { ["result"] = "complete", ["clash_count"] = 0, ["clashes"] = new JArray() });
            JObject r = Audit();
            var f = ((JArray)r["findings"]).OfType<JObject>().Single(x => (string)x["check"] == "fit_out_in_walls");
            Assert.Equal(1, (int)f["count"]);
            Assert.Equal("not_yet", (string)r["verdict"]);
        }

        [Fact]
        public void A_lavatory_overlapping_a_shower_is_a_blocking_finding()
        {
            // INFORME_P6 §6.3 (Casa, baño 2): the lavatory overlapped the shower by ~140 mm and the
            // audit passed it - it only judged fit-out against walls and columns.
            var lav = Row(1, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 0, 0, 800, 500, 400, 900 });
            var shower = Row(2, "Plumbing Fixtures", "HZ_Plumbing_Shower", "900", new double[] { 360, -500, 0, 1260, 400, 2100 });
            Bridge(new List<JObject> { lav, shower }, args =>
            {
                bool fixtures = Has(args["categories_a"] as JArray, "OST_PlumbingFixtures") && Has(args["categories_b"] as JArray, "OST_PlumbingFixtures");
                return fixtures
                    ? new JObject { ["result"] = "complete", ["clash_count"] = 1, ["clashes"] = new JArray(Clash(1, "Plumbing Fixtures", 2, "Plumbing Fixtures", 0.012)) }
                    : new JObject { ["result"] = "complete", ["clash_count"] = 0, ["clashes"] = new JArray() };
            });
            JObject r = Audit();
            var f = ((JArray)r["findings"]).OfType<JObject>().Single(x => (string)x["check"] == "fixtures_overlap");
            Assert.Equal("blocking", (string)f["severity"]);
            Assert.Equal(1, (int)f["count"]);
            Assert.Equal("2", (string)f["sample"][0]["other_id"]);
            Assert.Equal("not_yet", (string)r["verdict"]);
        }

        [Fact]
        public void A_sink_sitting_in_its_vanity_is_not_an_overlap()
        {
            // a basin set into a counter overlaps it by design: in plan it lies INSIDE the counter
            var sink = Row(1, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 200, 50, 700, 700, 450, 900 });
            var vanity = Row(3, "Casework", "HZ_Casework_Vanity", "900", new double[] { 0, 0, 0, 900, 500, 850 });
            Bridge(new List<JObject> { sink, vanity }, args =>
                Has(args["categories_b"] as JArray, "OST_Casework") && !Has(args["categories_b"] as JArray, "OST_Walls")
                    ? new JObject { ["result"] = "complete", ["clash_count"] = 1, ["clashes"] = new JArray(Clash(1, "Plumbing Fixtures", 3, "Casework", 0.02)) }
                    : new JObject { ["result"] = "complete", ["clash_count"] = 0, ["clashes"] = new JArray() });
            JObject r = Audit();
            Assert.DoesNotContain(((JArray)r["findings"]).OfType<JObject>(), x => (string)x["check"] == "fixtures_overlap");
            Assert.Equal("usable_today", (string)r["verdict"]);
        }

        private static JObject Facing(JObject row, double fx, double fy)
        {
            row["placement"] = new JObject { ["facing"] = new JArray(fx, fy, 0) };
            return row;
        }

        [Fact]
        public void A_lavatory_hung_over_a_shower_tray_is_an_overlap_even_when_the_solids_do_not_touch()
        {
            // Casa, bano 2 live (Revit 2025): shower 90..990 x 6170..7070, lavatory 0..490 x 6930..7430, both from the
            // floor at 2800 - 400 x 140 mm of the lavatory stands in the shower, but its solid hangs at 800 mm over the
            // tray and horizun_clash finds no clash.
            var shower = Facing(Row(822922, "Plumbing Fixtures", "HZ_Plumbing_Shower", "900", new double[] { 90, 6170, 2800, 990, 7070, 4900 }), 0, 1);
            var lav = Facing(Row(822936, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 0, 6930, 2800, 490, 7430, 3650 }), 1, 0);
            lav["host_id"] = 5;
            Bridge(new List<JObject> { shower, lav }, args => new JObject { ["result"] = "complete", ["clash_count"] = 0, ["clashes"] = new JArray() });
            JObject r = Audit();
            var f = ((JArray)r["findings"]).OfType<JObject>().Single(x => (string)x["check"] == "fixtures_overlap");
            Assert.Equal(1, (int)f["count"]);
            Assert.Equal("plan_footprint", (string)f["sample"][0]["basis"]);
            Assert.Equal(400, (double)f["sample"][0]["overlap_mm"][0]);
            Assert.Equal(140, (double)f["sample"][0]["overlap_mm"][1]);
            Assert.Equal("not_yet", (string)r["verdict"]);
        }

        [Fact]
        public void Footprints_that_only_touch_or_nest_or_are_rotated_are_not_overlaps()
        {
            var shower = Facing(Row(1, "Plumbing Fixtures", "HZ_Plumbing_Shower", "900", new double[] { 0, 0, 0, 900, 900, 2100 }), 0, 1);
            var wc = Facing(Row(2, "Plumbing Fixtures", "HZ_Plumbing_WC", "380", new double[] { 930, 0, 0, 1310, 650, 750 }), 0, 1);
            var barely = Facing(Row(3, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 880, 870, 0, 1380, 1270, 900 }), 0, -1);
            var counter = Facing(Row(4, "Casework", "HZ_Casework_Vanity", "900", new double[] { 2000, 0, 0, 2900, 600, 900 }), 0, 1);
            var sink = Facing(Row(5, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 2200, 50, 750, 2700, 450, 950 }), 0, 1);
            var rotated = Facing(Row(6, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 100, 100, 0, 700, 700, 900 }), 0.7071, 0.7071);
            Bridge(new List<JObject> { shower, wc, barely, counter, sink, rotated }, args => new JObject { ["result"] = "complete", ["clash_count"] = 0, ["clashes"] = new JArray() });
            JObject r = Audit();
            Assert.DoesNotContain(((JArray)r["findings"]).OfType<JObject>(), x => (string)x["check"] == "fixtures_overlap");
        }

        [Fact]
        public void A_fixture_check_that_could_not_run_is_not_assessable_not_clean()
        {
            var lav = Row(1, "Plumbing Fixtures", "HZ_Plumbing_Lavatory", "500", new double[] { 0, 0, 800, 500, 400, 900 });
            Bridge(new List<JObject> { lav }, args => Has(args["categories_b"] as JArray, "OST_Walls")
                ? new JObject { ["result"] = "complete", ["clash_count"] = 0, ["clashes"] = new JArray() }
                : new JObject { ["result"] = "partial", ["clash_count"] = 0, ["clashes"] = new JArray(), ["headline"] = "PARTIAL (2 pair(s) unresolved)" });
            JObject r = Audit();
            var f = ((JArray)r["findings"]).OfType<JObject>().Single(x => (string)x["check"] == "fixtures_overlap");
            Assert.Equal("not_assessable", (string)f["severity"]);
            Assert.Equal("usable_with_corrections", (string)r["verdict"]);
        }
    }

    [CollectionDefinition("TemplateKitInvoker", DisableParallelization = true)]
    public class TemplateKitInvokerCollection { }
}
