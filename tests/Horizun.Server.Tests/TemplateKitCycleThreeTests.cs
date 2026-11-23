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
    public sealed class TemplateKitCycleThreeTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "kit-cycle-three-" + Guid.NewGuid().ToString("N"));
        private readonly Func<JObject, CancellationToken, JToken> _saved = TemplateKitTool.Invoker;
        private readonly JArray _calls = new JArray();
        private readonly JObject _kit;
        private readonly List<JObject> _rows = new List<JObject>();
        private Func<string, JObject, JObject> _reply;
        private string PathKit => Path.Combine(_dir, "test.hzkit.json");
        public TemplateKitCycleThreeTests()
        {
            Directory.CreateDirectory(_dir);
            _kit = new JObject { ["schema"] = TemplateKitTool.Schema, ["template"] = new JObject(), ["mcp"] = new JObject(),
                ["catalog"] = new JArray(Entry("Walls"), Entry("Structural Columns")), ["checks"] = new JArray() };
            _rows.Add(Row(1, "Walls"));
            TemplateKitTool.Invoker = (call, ct) =>
            {
                _calls.Add(call.DeepClone());
                string tool = (string)call["name"]; var args = (JObject)call["arguments"];
                Assert.Empty(ContractSchemaCheck.Validate(args, Horizun.Contracts.Contract.Find(tool).InputSchema));
                JObject body = _reply?.Invoke(tool, args);
                if (body == null) switch (tool)
                {
                    case "horizun_query_classification": body = JObject.Parse("{placed_types_without_code:0,types_with_unknown_code:0}"); break;
                    case "horizun_query_model": body = new JObject { ["rows"] = args["categories"] == null ? new JArray(_rows) : (string)args["categories"][0] == "OST_Rebar" ? new JArray(_rows.Where(r => (string)r["category"] == "Structural Rebar")) : new JArray(), ["coverage_complete"] = true }; break;
                    case "horizun_audit_model": body = new JObject { ["findings"] = new JArray(
                        new JObject { ["check"] = "warnings", ["count"] = 0, ["items"] = new JArray() },
                        new JObject { ["check"] = "level_span", ["count"] = 0, ["coverage_complete"] = true }) }; break;
                    case "horizun_clash": body = new JObject { ["result"] = "complete", ["clashes"] = new JArray() }; break;
                    case "horizun_query_family_internals": body = new JObject { ["coverage_complete"] = true, ["types"] = new JArray() }; break;
                    case "horizun_audit_support": body = new JObject { ["coverage_complete"] = true, ["examined"] = 1, ["findings"] = new JArray(), ["aligned"] = true }; break;
                    case "horizun_join_geometry": body = new JObject { ["coverage_complete"] = true, ["pairs"] = args["pairs"].DeepClone(), ["shared_volume_total_m3"] = 0 }; break;
                    case "horizun_query_structure": body = new JObject { ["verdict"] = "clean", ["counts"] = new JObject(), ["pairs"] = new JArray() }; break;
                    default: throw new InvalidOperationException("unexpected tool " + tool);
                }
                return new JObject { ["structuredContent"] = body };
            };
        }
        [Fact]
        public void An_audit_finishes_with_separate_measured_failures_and_exact_missing_inputs()
        {
            Check("cobertura_contra_plano"); Check("support_coverage_per_level");
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats && (string)cats[0] == "OST_Levels"
                ? new JObject { ["rows"] = new JArray(new JObject { ["element_id"] = 11, ["name"] = "Synthetic level" }), ["coverage_complete"] = true }
                : tool == "horizun_audit_support" ? new JObject { ["coverage_complete"] = true, ["examined"] = 0, ["findings_total"] = 0, ["findings"] = new JArray() } : null;
            JObject result = Audit();
            Assert.True((bool?)result["audit_completed"]);
            Assert.Equal(2, ((JArray)result["acceptance_blockers"]["missing_measurements"]).Count);
            JObject support = State(result, "support_coverage_per_level");
            Assert.Equal("not_executed", (string)support["status"]);
            Assert.Contains("bounded Rooms", support["required_input"].ToString());
            Assert.Equal(11, (int)support["examined_by_level"][0]["retry_arguments"]["level_id"]);
            Assert.NotNull(State(result, "cobertura_contra_plano")["required_evidence"]);
        }

        [Fact]
        public void Not_yet_is_a_completed_verdict_with_the_measured_blockers_named()
        {
            Check("hosted_elements_have_their_host");
            _kit["catalog"][0]["placement"]["host"] = "face"; _rows[0]["host_id"] = null;
            JObject result = Audit();
            Assert.Equal("not_yet", (string)result["verdict"]);
            Assert.True((bool?)result["audit_completed"]);
            Assert.Contains(((JArray)result["acceptance_blockers"]["measured_failures"]).OfType<JObject>(), f => (string)f["check"] == "hosts");
        }

        private static JObject Entry(string category) => new JObject { ["key"] = category + "|T", ["family"] = category, ["type"] = "T", ["category"] = category,
            ["code"] = "22.00", ["placement"] = new JObject() };
        private static JObject Row(int id, string category) => new JObject { ["element_id"] = id, ["family"] = category, ["type"] = "T", ["category"] = category,
            ["host_id"] = 99, ["bounding_box"] = new JObject { ["min"] = new JArray(0, 0, 0), ["max"] = new JArray(1000, 1000, 3000) },
            ["parameters"] = new JObject { ["HZ_AI_Source"] = new JObject { ["exists"] = true, ["raw"] = "plan" }, ["HZ_AI_Status"] = new JObject { ["exists"] = true, ["raw"] = "checked" } } };
        private void Check(string id, JObject args = null) => ((JArray)_kit["checks"]).Add(new JObject { ["id"] = id, ["args"] = args ?? new JObject() });
        private JObject Audit(string declaredEvidencePath = null)
        {
            File.WriteAllText(PathKit, _kit.ToString());
            var args = new JObject { ["operation"] = "audit", ["path"] = PathKit, ["target_document"] = "Synthetic model" };
            if (declaredEvidencePath != null) args["declared_evidence_path"] = declaredEvidencePath;
            Assert.Empty(ContractSchemaCheck.Validate(args, Horizun.Contracts.Contract.Find("horizun_template_kit").InputSchema));
            return TemplateKitTool.Handle(args, CancellationToken.None);
        }
        private static JObject State(JObject result, string id) => ((JArray)result["checks_coverage"]).OfType<JObject>().Single(c => (string)c["id"] == id);

        [Fact]
        public void Catalog_boolean_product_marker_finds_all_fifteen_existing_products_without_inventing_a_file_route()
        {
            _kit["catalog"] = new JArray(Enumerable.Range(1, 15).Select(i => new JObject { ["key"] = "Door|" + i, ["family"] = "Door", ["type"] = i.ToString(),
                ["category"] = "Doors", ["product_family"] = true, ["resolve"] = new JObject { ["tool"] = "horizun_query_model" } }));
            ((JArray)_kit["catalog"]).Add(new JObject { ["key"] = "Generic|T", ["product_family"] = false });
            File.WriteAllText(PathKit, _kit.ToString());
            JObject result = TemplateKitTool.Handle(new JObject { ["operation"] = "find", ["path"] = PathKit, ["product_only"] = true, ["limit"] = 50 }, CancellationToken.None);
            Assert.Equal(15, (int)result["total"]);
            Assert.All(((JArray)result["hits"]).OfType<JObject>(), h => { Assert.True((bool)h["product_family"]); Assert.Null(h["source_path"]); Assert.NotNull(h["resolve"]); });
        }
        [Fact]
        public void Installed_check_ids_execute_codes_hosts_and_level_span_and_do_not_mask_missing_measurements()
        {
            Check("every_placed_type_is_coded"); Check("hosted_elements_have_their_host"); Check("level_span");
            _kit["catalog"][0]["placement"]["host"] = "face"; _rows[0]["host_id"] = null;
            JObject result = Audit();
            Assert.Equal("executed", (string)State(result, "every_placed_type_is_coded")["status"]);
            Assert.Equal("executed", (string)State(result, "hosted_elements_have_their_host")["status"]);
            Assert.False((bool)State(result, "hosted_elements_have_their_host")["passed"]);
            Assert.Equal("executed", (string)State(result, "level_span")["status"]);
            _reply = (tool, args) => tool == "horizun_query_classification" ? new JObject { ["placed_types_without_code"] = 0 } :
                tool == "horizun_audit_model" ? new JObject { ["findings"] = new JArray(new JObject { ["check"] = "warnings", ["count"] = 0 }) } : null;
            result = Audit();
            Assert.Equal("not_executed", (string)State(result, "every_placed_type_is_coded")["status"]);
            Assert.Equal("not_executed", (string)State(result, "level_span")["status"]);
        }
        [Fact]
        public void Hard_clashes_use_kit_categories_and_report_partial_even_with_no_hits()
        {
            Check("no_hard_clashes", new JObject { ["categories_a"] = new JArray("<category A>"), ["categories_b"] = new JArray("<category B>"), ["tolerance_mm"] = 3 });
            _reply = (tool, args) => tool == "horizun_clash" && (double?)args["tolerance_mm"] == 3 ? new JObject { ["result"] = "partial", ["clashes"] = new JArray() } : null;
            JObject result = Audit();
            Assert.Equal("not_executed", (string)State(result, "no_hard_clashes")["status"]);
            JObject call = _calls.OfType<JObject>().Single(c => (string)c["name"] == "horizun_clash" && (double?)c["arguments"]["tolerance_mm"] == 3);
            Assert.Equal(new[] { "OST_Walls", "OST_StructuralColumns" }, ((JArray)call["arguments"]["categories_a"]).Values<string>());
            Assert.True((bool)call["arguments"]["include_links"]);
            Assert.Contains("OST_StructuralFraming", ((JArray)call["arguments"]["categories_b"]).Values<string>());
            Assert.Contains("OST_StructuralFoundation", ((JArray)call["arguments"]["categories_b"]).Values<string>());
        }
        [Theory]
        [InlineData(null, "executed")]
        [InlineData(false, "not_executed")]
        public void Classification_uses_global_counts_but_respects_explicit_incomplete_coverage(bool? complete, string status)
        {
            Check("every_placed_type_is_coded");
            _reply = (tool, args) => tool == "horizun_query_classification" ? new JObject { ["coverage_complete"] = complete,
                ["placed_types_without_code"] = 0, ["types_with_unknown_code"] = 0, ["truncated"] = true } : null;
            Assert.Equal(status, (string)State(Audit(), "every_placed_type_is_coded")["status"]);
        }
        [Fact]
        public void Host_check_includes_a_hosted_type_that_is_not_in_the_catalog()
        {
            Check("hosted_elements_have_their_host", new JObject { ["categories"] = new JArray("OST_Doors") });
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats && (string)cats[0] == "OST_Doors"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(new JObject { ["element_id"] = 8, ["family"] = "New door", ["type"] = "T", ["host_id"] = null }) } : null;
            JObject result = Audit(); Assert.Equal("executed", (string)State(result, "hosted_elements_have_their_host")["status"]);
            Assert.False((bool)State(result, "hosted_elements_have_their_host")["passed"]);
            Assert.Contains(((JArray)result["findings"]).OfType<JObject>(), f => (string)f["check"] == "hosts" && (int)f["sample"][0]["element_id"] == 8);
        }
        private static JObject ClashPage(int offset, int total, string fingerprint = "stable", bool geometryComplete = true)
        {
            int count = Math.Min(500, total - offset); bool more = count == 500;
            return new JObject { ["result"] = more || !geometryComplete ? "partial" : "complete", ["offset"] = offset,
                ["next_offset"] = more ? (JToken)(offset + count) : null, ["truncated"] = more, ["sweep_fingerprint"] = fingerprint,
                ["coverage"] = new JObject { ["complete"] = geometryComplete }, ["visibility_coverage"] = new JObject { ["coverage_complete"] = true },
                ["headline"] = geometryComplete ? "page" : "boolean intersection failed on pair 8/9",
                ["clashes"] = new JArray(Enumerable.Range(offset, count).Select(i => new JObject { ["sweep_index"] = i,
                    ["a"] = new JObject { ["element_id"] = i + 10, ["source_model"] = "host" },
                    ["b"] = new JObject { ["element_id"] = i + 10, ["source_model"] = "linked", ["link_instance_id"] = 42 },
                    ["cross_model"] = true, ["intersection_volume_m3"] = 0.001, ["intersection_volume_is_complete"] = true })) };
        }
        [Fact]
        public void Support_derives_discipline_categories_and_runs_room_and_link_modes()
        {
            Check("audit_support"); _kit["checks"][0]["also_run_modes"] = new JArray("coverage_gaps", "model_offset");
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats && (string)cats[0] == "OST_Rooms"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(new JObject { ["element_id"] = 41 }) }
                : tool == "horizun_query_model" && args["categories"] is JArray linkCats && (string)linkCats[0] == "OST_RvtLinks"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(new JObject { ["element_id"] = 42 }) } : null;
            Assert.Equal("executed", (string)State(Audit(), "audit_support")["status"]);
            var calls = _calls.OfType<JObject>().Where(c => (string)c["name"] == "horizun_audit_support").ToList();
            Assert.Equal(3, calls.Count);
            Assert.Equal(new[] { "OST_Walls", "OST_StructuralColumns" }, ((JArray)calls[0]["arguments"]["categories"]).Values<string>());
            Assert.Equal(41, (int)calls[1]["arguments"]["room_ids"][0]); Assert.Equal(42, (int)calls[2]["arguments"]["link_instance_id"]);
        }
        [Fact]
        public void Partial_support_is_not_a_clean_gate_and_a_measured_floating_item_is_blocking()
        {
            Check("audit_support");
            _reply = (tool, args) => tool == "horizun_audit_support" ? new JObject { ["coverage_complete"] = false,
                ["findings"] = new JArray(new JObject { ["element_id"] = 7, ["gap_below_mm"] = 300 }) } : null;
            JObject result = Audit();
            Assert.Equal("not_executed", (string)State(result, "audit_support")["status"]);
            Assert.Equal("not_yet", (string)result["verdict"]);
        }
        [Fact]
        public void Shared_volume_covers_cross_batch_pairs_and_distinguishes_zero_from_unmeasured()
        {
            Check("volumen_compartido_0"); _rows.Clear();
            _rows.AddRange(Enumerable.Range(1, 22).Select(i => Row(i, "Walls")));
            JObject result = Audit();
            Assert.Equal("executed", (string)State(result, "volumen_compartido_0")["status"]);
            var reads = _calls.OfType<JObject>().Where(c => (string)c["name"] == "horizun_join_geometry").ToList();
            Assert.Equal(2, reads.Count); Assert.Equal(231, reads.Sum(c => ((JArray)c["arguments"]["pairs"]).Count));
            Assert.Contains(reads.SelectMany(c => ((JArray)c["arguments"]["pairs"]).OfType<JArray>()), p => (int)p[0] == 1 && (int)p[1] == 22);
            _reply = (tool, args) => tool == "horizun_join_geometry" ? new JObject { ["coverage_complete"] = false, ["pairs"] = args["pairs"].DeepClone(), ["shared_volume_total_m3"] = null } : null;
            result = Audit(); Assert.Equal("not_executed", (string)State(result, "volumen_compartido_0")["status"]);
            Assert.Equal(JTokenType.Null, State(result, "volumen_compartido_0")["shared_volume_total_m3"].Type);
        }
        [Fact]
        public void Residual_shared_volume_is_a_blocking_finding()
        {
            Check("volumen_compartido_0"); _rows.Add(Row(2, "Walls"));
            _reply = (tool, args) => tool == "horizun_join_geometry" ? new JObject { ["coverage_complete"] = true,
                ["pairs"] = new JArray(new JObject { ["a"] = 1, ["b"] = 2, ["shared_volume_m3"] = 0.2 }), ["shared_volume_total_m3"] = 0.2 } : null;
            JObject result = Audit(); Assert.False((bool)State(result, "volumen_compartido_0")["passed"]); Assert.Equal("not_yet", (string)result["verdict"]);
        }
        [Fact]
        public void Beam_cutback_and_rebar_evaluators_propagate_findings_and_partial_measurements()
        {
            _rows.Add(Row(6, "Structural Rebar"));
            Check("beam_join_cutback"); Check("rebar_clash_free", new JObject { ["tolerance_mm"] = 2 });
            _reply = (tool, args) => tool == "horizun_query_structure" ? new JObject { ["verdict"] = "incomplete", ["counts"] = new JObject(), ["pairs"] = new JArray() }
                : tool == "horizun_query_model" && args["return_parameters"] is JArray parameters && (string)parameters[0] == "START_JOIN_CUTBACK"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(new JObject { ["element_id"] = 5,
                    ["parameters"] = new JObject { ["START_JOIN_CUTBACK"] = new JObject { ["raw"] = 0.0416666667 }, ["END_JOIN_CUTBACK"] = new JObject { ["raw"] = 0 } } }) } : null;
            JObject result = Audit(); Assert.False((bool)State(result, "beam_join_cutback")["passed"]); Assert.Equal("not_executed", (string)State(result, "rebar_clash_free")["status"]);
            _reply = (tool, args) => tool == "horizun_query_structure" ? new JObject { ["verdict"] = "findings", ["counts"] = new JObject { ["intersections"] = new JObject { ["set_pairs"] = 2 } }, ["pairs"] = new JArray() } : null;
            result = Audit(); Assert.True((string)State(result, "rebar_clash_free")["status"] == "executed", result.ToString()); Assert.False((bool)State(result, "rebar_clash_free")["passed"]);
        }

        [Fact]
        public void Rebar_with_no_intersections_passes_and_partial_census_does_not()
        {
            Check("rebar_clash_free", new JObject { ["tolerance_mm"] = 2 }); _rows.Add(Row(6, "Structural Rebar"));
            JObject clean = State(Audit(), "rebar_clash_free");
            Assert.True((bool?)clean["passed"] == true, clean.ToString());
            _reply = (tool, args) => tool == "horizun_query_model" && (string)args["categories"]?[0] == "OST_Rebar"
                ? new JObject { ["rows"] = new JArray(), ["coverage_complete"] = false } : null;
            JObject row = State(Audit(), "rebar_clash_free");
            Assert.Equal("not_executed", (string)row["status"]);
            Assert.Equal(JTokenType.Null, row["passed"].Type);
        }

        [Fact]
        public void Empty_hosts_shared_volume_and_levels_are_not_passes()
        {
            _rows.Clear(); Check("hosts", new JObject { ["categories"] = new JArray("OST_Doors") });
            Check("shared_volume"); Check("levels_honoured");
            _reply = (tool, args) => tool == "horizun_query_model" && (string)args["categories"]?[0] == "OST_Levels"
                ? new JObject { ["rows"] = new JArray(new JObject { ["datum"] = new JObject { ["elevation"] = 0 } }), ["coverage_complete"] = true } : null;
            JObject result = Audit();
            foreach (string id in new[] { "hosts", "shared_volume", "levels_honoured" })
            {
                JObject row = State(result, id); Assert.Equal("not_applicable", (string)row["status"]);
                Assert.False(string.IsNullOrWhiteSpace((string)row["reason"])); Assert.Equal(JTokenType.Null, row["passed"].Type);
            }
        }

        [Theory]
        [InlineData("kit_types")]
        [InlineData("hosts")]
        [InlineData("legacy")]
        [InlineData("unusable_types")]
        [InlineData("traceability")]
        public void Empty_kit_elements_do_not_pass_element_checks(string check)
        {
            _rows.Clear(); Check(check); JObject row = State(Audit(), check);
            Assert.Equal("not_applicable", (string)row["status"]); Assert.NotNull((string)row["reason"]);
            Assert.Equal(JTokenType.Null, row["passed"].Type);
        }

        [Fact]
        public void A_model_with_no_hosted_kit_types_does_not_pass_the_host_check()
        {
            Check("hosts"); JObject row = State(Audit(), "hosts");
            Assert.Equal("not_applicable", (string)row["status"]);
            Assert.Equal("no kit elements requiring a host in this model", (string)row["reason"]);
            Assert.Equal(JTokenType.Null, row["passed"].Type);
        }

        [Theory]
        [InlineData("codes")]
        [InlineData("level_span")]
        [InlineData("duplicates")]
        [InlineData("datums")]
        public void Empty_model_does_not_pass_population_checks(string id)
        {
            _rows.Clear(); Check(id);
            _reply = (tool, args) => tool == "horizun_audit_model" ? new JObject { ["findings"] = new JArray(
                new JObject { ["check"] = "warnings", ["count"] = 0, ["items"] = new JArray() },
                new JObject { ["check"] = "level_span", ["count"] = 0, ["coverage_complete"] = true },
                new JObject { ["check"] = "datums", ["count"] = 0, ["coverage_complete"] = true }) } : null;
            JObject row = State(Audit(), id);
            Assert.Equal("not_applicable", (string)row["status"]); Assert.Equal("no model elements to examine", (string)row["reason"]);
            Assert.Equal(JTokenType.Null, row["passed"].Type);
        }

        [Fact]
        public void Foreign_names_with_no_used_types_is_not_applicable()
        {
            _rows.Clear(); Check("foreign_names");
            _kit["conventions"] = new JObject { ["foreign_names"] = new JObject { ["forbidden"] = new JArray("forbidden") } };
            JObject row = State(Audit(), "foreign_names");
            Assert.Equal("not_applicable", (string)row["status"]); Assert.Equal("no used types in this model", (string)row["reason"]);
            Assert.Equal(JTokenType.Null, row["passed"].Type);
        }
        [Theory]
        [InlineData(3000, true)]
        [InlineData(2700, false)]
        public void Levels_honoured_compares_the_actual_extents_with_level_elevations(double upper, bool passed)
        {
            Check("levels_honoured"); _rows[0]["bounding_box"]["max"][2] = upper;
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats && (string)cats[0] == "OST_Levels"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(
                    new JObject { ["element_id"] = 11, ["datum"] = new JObject { ["elevation"] = 0 } },
                    new JObject { ["element_id"] = 12, ["datum"] = new JObject { ["elevation"] = 3000 } }) } : null;
            Assert.Equal(passed, (bool)State(Audit(), "levels_honoured")["passed"]);
        }
        [Theory]
        [InlineData("Structural Columns", 500, 0, true)]
        [InlineData("Structural Columns", 800, 0, false)]
        [InlineData("Walls", 500, 0, true)]
        [InlineData("Structural Columns", 0, 0, true)]
        [InlineData("Structural Columns", 500, 2000, false)]
        public void Levels_honoured_accepts_only_overlapping_foundation_tops_at_the_bottom(string category, int bottom, int foundationX, bool passed)
        {
            Check("levels_honoured");
            _rows.Clear(); _rows.Add(Row(1, category));
            _rows[0]["bounding_box"]["min"][2] = bottom;
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats
                ? (string)cats[0] == "OST_Levels" ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(
                    new JObject { ["datum"] = new JObject { ["elevation"] = 0 } },
                    new JObject { ["datum"] = new JObject { ["elevation"] = 3000 } }) }
                : (string)cats[0] == "OST_StructuralFoundation" ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(
                    new JObject { ["bounding_box"] = new JObject { ["min"] = new JArray(foundationX, 0, 0), ["max"] = new JArray(foundationX + 1000, 1000, 500) } }) } : null : null;
            JObject state = State(Audit(), "levels_honoured");
            Assert.Equal("executed", (string)state["status"]);
            Assert.Equal(passed, (bool)state["passed"]);
            Assert.Contains(_calls.OfType<JObject>(), c => (string)c["name"] == "horizun_query_model" && (string)c["arguments"]["categories"]?[0] == "OST_StructuralFoundation" && (bool?)c["arguments"]["include_bounding_box"] == true);
        }

        [Fact]
        public void Levels_honoured_is_not_executed_when_foundations_are_incomplete()
        {
            Check("levels_honoured");
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats
                ? (string)cats[0] == "OST_Levels" ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(
                    new JObject { ["datum"] = new JObject { ["elevation"] = 0 } },
                    new JObject { ["datum"] = new JObject { ["elevation"] = 3000 } }) }
                : (string)cats[0] == "OST_StructuralFoundation" ? new JObject { ["coverage_complete"] = false, ["rows"] = new JArray() } : null : null;
            Assert.Equal("not_executed", (string)State(Audit(), "levels_honoured")["status"]);
        }

        [Fact]
        public void Localized_categories_use_stable_ids_for_geometry_and_host_only_queries()
        {
            Check("volumen_compartido_0"); Check("levels_honoured");
            // QueryModel publishes JSON null for non-category document records.
            // They are outside a category-based geometry scope, not unreadable walls.
            _rows.Add(new JObject { ["element_id"] = 999, ["category"] = JValue.CreateNull() });
            _rows[0]["category"] = "Muros"; _rows[0]["built_in_category"] = "OST_Walls";
            var other = Row(2, "Muros"); other["built_in_category"] = "OST_Walls"; _rows.Add(other);
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats && (string)cats[0] == "OST_Levels"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(new JObject { ["datum"] = new JObject { ["elevation"] = 0 } }, new JObject { ["datum"] = new JObject { ["elevation"] = 3000 } }) } : null;
            JObject result = Audit();
            Assert.Equal("executed", (string)State(result, "volumen_compartido_0")["status"]);
            Assert.Equal(1, (int)State(result, "volumen_compartido_0")["pairs_examined"]);
            Assert.Equal("executed", (string)State(result, "levels_honoured")["status"]);
            Assert.All(_calls.OfType<JObject>().Where(c => (string)c["name"] == "horizun_query_model"), c => Assert.False((bool)c["arguments"]["include_links"]));
            _rows.ForEach(r => r.Remove("built_in_category"));
            result = Audit();
            Assert.Equal("not_executed", (string)State(result, "volumen_compartido_0")["status"]);
            Assert.Equal("not_executed", (string)State(result, "levels_honoured")["status"]);
        }
        [Theory]
        [InlineData(3, "executed", true)]
        [InlineData(2, "executed", false)]
        [InlineData(null, "not_executed", null)]
        public void Coordination_view_requires_fine_detail_and_readable_metadata(int? detail, string status, bool? passed)
        {
            Check("vista_3d_de_coordinacion");
            _reply = (tool, args) => tool == "horizun_query_model" && args["return_fields"] != null ? new JObject { ["coverage_complete"] = true,
                ["rows"] = new JArray(new JObject { ["element_id"] = 3, ["name"] = "TEST_3D_COORDINACION", ["view_type"] = "ThreeD", ["is_view_template"] = false,
                    ["parameters"] = new JObject { ["VIEW_DETAIL_LEVEL"] = new JObject { ["raw"] = detail } } }) } : null;
            JObject state = State(Audit(), "vista_3d_de_coordinacion"); Assert.Equal(status, (string)state["status"]); Assert.Equal(passed, (bool?)state["passed"]);
        }
        [Fact]
        public void Actual_drawing_comparisons_explicitly_require_drawings()
        {
            foreach (string id in new[] { "cobertura_contra_plano", "visual_evidence", "quantities_by_code", "induccion_completa" }) Check(id);
            Assert.All(((JArray)Audit()["checks_coverage"]).OfType<JObject>(), state => {
                Assert.Equal("not_executed", (string)state["status"]); Assert.Equal("requiere planos", (string)state["reason"]);
                Assert.Equal(TemplateKitTool.DeclaredEvidenceSchemaName, (string)state["required_evidence"]["schema"]);
                Assert.NotEmpty((JArray)state["required_evidence"]["missing"]); Assert.Null((bool?)state["passed"]);
            });
        }

        private JObject DeclaredEvidence(string check, string result = "passed")
        {
            string source = Path.Combine(_dir, "drawing-coverage.json");
            File.WriteAllText(source, "{\"drawing\":\"S-01\",\"element\":1}");
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(source))).Replace("-", "").ToLowerInvariant();
            return new JObject { ["schema"] = TemplateKitTool.DeclaredEvidenceSchemaName, ["target_document"] = "Synthetic model",
                ["checks"] = new JArray(new JObject { ["check_id"] = check, ["source"] = new JObject { ["origin"] = "Independent synthetic drawing review", ["path"] = source, ["sha256"] = hash },
                    ["coverage"] = new JArray(new JObject { ["drawing_ref"] = "S-01 / region A", ["model_element_ids"] = new JArray(1) }), ["result"] = result, ["summary"] = "Declared drawing/model review" }) };
        }

        private string SaveEvidence(JObject evidence)
        {
            string path = Path.Combine(_dir, "external-evidence.json"); File.WriteAllText(path, evidence.ToString()); return path;
        }

        [Theory]
        [InlineData("cobertura_contra_plano", "passed")]
        [InlineData("visual_evidence", "passed")]
        [InlineData("quantities_by_code", "passed")]
        [InlineData("induccion_completa", "passed")]
        [InlineData("cobertura_contra_plano", "failed")]
        [InlineData("visual_evidence", "failed")]
        [InlineData("quantities_by_code", "failed")]
        [InlineData("induccion_completa", "failed")]
        public void External_drawing_results_are_validated_per_check_and_never_become_measured_passes(string check, string declaredResult)
        {
            Check(check);
            JObject result = Audit(SaveEvidence(DeclaredEvidence(check, declaredResult))), state = State(result, check);
            Assert.Equal("executed_with_declared_evidence", (string)state["status"]);
            Assert.Null((bool?)state["passed"]); Assert.False((bool)state["host_verified"]); Assert.False((bool)state["measured"]);
            Assert.Equal(declaredResult == "passed", (bool)state["declared_passed"]);
            Assert.Equal((string)state["declared_evidence"]["source"]["sha256"], (string)state["source_sha256_verified"]);
            Assert.Equal(64, ((string)state["manifest_sha256"]).Length);
            Assert.Equal(declaredResult == "passed" ? "usable_with_corrections" : "not_yet", (string)result["verdict"]);
            Assert.Empty((JArray)result["blocking_checks_not_executed"]);
            Assert.Single((JArray)result["checks_with_declared_evidence"]);
            Assert.DoesNotContain(((JArray)result["acceptance_blockers"]["measured_failures"]).OfType<JObject>(), f => (string)f["basis"] == "declared_evidence");
            Assert.Equal(declaredResult == "failed" ? 1 : 0, ((JArray)result["acceptance_blockers"]["declared_failures"]).Count);
        }

        [Theory]
        [InlineData("hash", "SHA-256 mismatch")]
        [InlineData("source_missing", "Could not find")]
        [InlineData("model", "target_document")]
        [InlineData("reference", "not read in this audit")]
        [InlineData("duplicate", "exactly once")]
        [InlineData("schema", "required")]
        [InlineData("extra", "not allowed")]
        [InlineData("unknown_check", "not a drawing check")]
        [InlineData("empty_coverage", "minItems")]
        public void Invalid_or_stale_external_evidence_abstains_instead_of_passing(string invalid, string reason)
        {
            Check("cobertura_contra_plano"); JObject evidence = DeclaredEvidence("cobertura_contra_plano");
            var entry = (JObject)evidence["checks"][0];
            switch (invalid)
            {
                case "hash": File.AppendAllText((string)entry["source"]["path"], "changed after review"); break;
                case "source_missing": File.Delete((string)entry["source"]["path"]); break;
                case "model": evidence["target_document"] = "Another model"; break;
                case "reference": entry["coverage"][0]["model_element_ids"] = new JArray(999); break;
                case "duplicate": ((JArray)evidence["checks"]).Add(entry.DeepClone()); break;
                case "schema": entry.Remove("result"); break;
                case "extra": entry["unvalidated_pass"] = true; break;
                case "unknown_check": entry["check_id"] = "codes"; break;
                case "empty_coverage": entry["coverage"] = new JArray(); break;
            }
            JObject result = Audit(SaveEvidence(evidence)), state = State(result, "cobertura_contra_plano");
            Assert.Equal("not_executed", (string)state["status"]); Assert.Contains(reason, (string)state["reason"]);
            Assert.Null((bool?)state["passed"]); Assert.Null((bool?)state["declared_passed"]);
            Assert.Single((JArray)result["blocking_checks_not_executed"]); Assert.Empty((JArray)result["checks_with_declared_evidence"]);
        }

        [Fact]
        public void One_drawing_declaration_cannot_satisfy_another_check()
        {
            Check("cobertura_contra_plano"); Check("visual_evidence");
            JObject result = Audit(SaveEvidence(DeclaredEvidence("cobertura_contra_plano")));
            Assert.Equal("executed_with_declared_evidence", (string)State(result, "cobertura_contra_plano")["status"]);
            Assert.Equal("not_executed", (string)State(result, "visual_evidence")["status"]);
            Assert.Single((JArray)result["blocking_checks_not_executed"]);
        }

        [Fact]
        public void Evidence_schema_is_discoverable_without_a_kit_or_Revit()
        {
            JObject schema = TemplateKitTool.Handle(new JObject { ["operation"] = "evidence_schema" }, CancellationToken.None);
            schema.Remove("$schema");
            Assert.Empty(ContractSchemaCheck.UnknownKeywords(schema));
            Assert.Empty(ContractSchemaCheck.Validate(DeclaredEvidence("visual_evidence"), schema));
            Assert.Empty(_calls);
        }
        private static JArray HostLevels() => new JArray(
            new JObject { ["element_id"] = 51, ["name"] = "TEST_LEVEL_0", ["datum"] = new JObject { ["elevation"] = 0 } },
            new JObject { ["element_id"] = 52, ["name"] = "TEST_LEVEL_1", ["datum"] = new JObject { ["elevation"] = 3000 } });
        [Fact]
        public void Datum_names_requires_the_project_datum_instead_of_inventing_it_from_template_prose()
        {
            Check("datum_names"); _kit["datum"] = JValue.CreateNull();
            _kit["conventions"] = new JObject { ["levels"] = "A template example is not the project instruction." };
            JObject state = State(Audit(), "datum_names");
            Assert.Equal("not_executed", (string)state["status"]); Assert.Contains("requiere datum del encargo", (string)state["reason"]);
            Assert.DoesNotContain("no audit evaluator", (string)state["reason"]);
            Assert.Null((bool?)state["passed"]);
        }
        [Fact]
        public void Datum_names_uses_nested_declared_pattern_units_and_elevations_and_detects_disagreement()
        {
            Check("datum_names");
            var expected = new JObject { ["naming_convention"] = new JObject { ["level_regex"] = "^TEST_LEVEL_[0-9]+$" }, ["units"] = "m", ["elevation_tolerance_mm"] = 1,
                ["levels"] = new JArray(new JObject { ["name"] = "TEST_LEVEL_0", ["elevation"] = 0 }, new JObject { ["name"] = "TEST_LEVEL_1", ["elevation"] = 3 }) };
            _kit["project_context"] = new JObject { ["datum"] = expected };
            var levels = HostLevels();
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats && (string)cats[0] == "OST_Levels"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = levels.DeepClone() } : null;
            Assert.True((bool)State(Audit(), "datum_names")["passed"]);
            levels[1]["datum"]["elevation"] = 2900;
            JObject result = Audit(); Assert.False((bool)State(result, "datum_names")["passed"]);
            Assert.Contains(((JArray)result["findings"]).OfType<JObject>(), f => (string)f["check"] == "datum_names" && (string)f["sample"][0]["issue"] == "elevation differs from the declared project datum");
            levels[1]["datum"]["elevation"] = 0; levels[1]["name"] = "unexpected";
            result = Audit();
            Assert.Contains(((JArray)result["findings"]).OfType<JObject>().Single(f => (string)f["check"] == "datum_names")["sample"].OfType<JObject>(), f => (string)f["issue"] == "duplicate datum name or elevation");
        }
        [Fact]
        public void Datum_names_refuses_unstructured_naming_text_and_elevations_without_units()
        {
            Check("datum_names"); _kit["datum"] = new JObject { ["naming_convention"] = "Use the agreed project names", ["levels"] = new JArray(new JObject { ["name"] = "TEST_LEVEL_0", ["elevation"] = 0 }) };
            Assert.Equal("not_executed", (string)State(Audit(), "datum_names")["status"]);
            _kit["datum"]["naming_convention"] = "^TEST_LEVEL_[0-9]+$";
            Assert.Contains("unidades declaradas", (string)State(Audit(), "datum_names")["reason"]);
        }
        [Fact]
        public void Support_coverage_resolves_real_level_ids_and_zero_rooms_never_proves_expected_room_coverage()
        {
            Check("support_coverage_per_level");
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats && (string)cats[0] == "OST_Levels"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = HostLevels() }
                : tool == "horizun_audit_support" ? new JObject { ["coverage_complete"] = true, ["examined"] = (int)args["level_id"] == 51 ? 3 : 0, ["findings_total"] = 0, ["findings"] = new JArray() } : null;
            JObject state = State(Audit(), "support_coverage_per_level");
            Assert.Equal("not_executed", (string)state["status"]); Assert.Contains("0 recintos examinados", (string)state["reason"]);
            Assert.Equal(new[] { 51, 52 }, _calls.OfType<JObject>().Where(c => (string)c["name"] == "horizun_audit_support").Select(c => (int)c["arguments"]["level_id"]));
            Assert.Equal(3, (int)state["examined_by_level"][0]["examined"]); Assert.Equal(0, (int)state["examined_by_level"][1]["examined"]);
            Assert.Null((bool?)state["passed"]);
        }
        [Fact]
        public void Support_coverage_reports_level_specific_gaps_and_partial_reads_without_erasing_evidence()
        {
            Check("support_coverage_per_level"); bool partial = false;
            _reply = (tool, args) => tool == "horizun_query_model" && args["categories"] is JArray cats && (string)cats[0] == "OST_Levels"
                ? new JObject { ["coverage_complete"] = true, ["rows"] = HostLevels() }
                : tool == "horizun_audit_support" ? new JObject { ["coverage_complete"] = !partial, ["examined"] = 2,
                    ["findings_total"] = (int)args["level_id"] == 52 ? 1 : 0,
                    ["findings"] = (int)args["level_id"] == 52 ? new JArray(new JObject { ["room_id"] = 61, ["uncovered_area_m2"] = 1.5 }) : new JArray() } : null;
            JObject result = Audit(); Assert.Equal("executed", (string)State(result, "support_coverage_per_level")["status"]); Assert.False((bool)State(result, "support_coverage_per_level")["passed"]);
            Assert.Contains(((JArray)result["findings"]).OfType<JObject>(), f => (string)f["check"] == "support_coverage_per_level" && (int)f["sample"][0]["level_id"] == 52);
            partial = true; result = Audit(); Assert.Equal("not_executed", (string)State(result, "support_coverage_per_level")["status"]); Assert.Equal("not_yet", (string)result["verdict"]);
        }
        public void Dispose() { TemplateKitTool.Invoker = _saved; Directory.Delete(_dir, true); }
    }
}
