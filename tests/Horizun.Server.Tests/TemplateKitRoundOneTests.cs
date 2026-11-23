using System;
using System.IO;
using System.Linq;
using System.Threading;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("TemplateKitInvoker")]
    public class TemplateKitRoundOneTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "kit-round-one-" + Guid.NewGuid().ToString("N"));
        private string PathKit => Path.Combine(_dir, "kit.hzkit.json");
        private readonly Func<JObject, CancellationToken, JToken> _savedInvoker = TemplateKitTool.Invoker;
        private JObject _kit;
        private JObject _row;
        private bool _partial;
        private bool _foreignFailure;
        private string _foreignField = "type";
        private const int _blockCount = 0;
        private readonly JArray _calls = new JArray();

        public TemplateKitRoundOneTests()
        {
            Directory.CreateDirectory(_dir);
            _kit = new JObject
            {
                ["schema"] = TemplateKitTool.Schema, ["template"] = new JObject(),
                ["mcp"] = new JObject { ["contract_hash"] = Contract.Hash }, ["conventions"] = new JObject(),
                ["catalog"] = new JArray(Entry("Door 800", 950, 800), Entry("Door 700", 700, null)),
                ["playbook"] = new JObject(), ["checks"] = new JArray()
            };
            _row = new JObject
            {
                ["element_id"] = 1, ["type_id"] = 10, ["category"] = "Doors", ["family"] = "Door", ["type"] = "Door 800", ["host_id"] = 2,
                ["parameters"] = new JObject
                {
                    ["HZ_AI_Source"] = new JObject { ["exists"] = true, ["raw"] = "plan" },
                    ["HZ_AI_Status"] = new JObject { ["exists"] = true, ["raw"] = "checked" }
                }
            };
            TemplateKitTool.Invoker = (call, ct) =>
            {
                _calls.Add(call.DeepClone());
                var args = (JObject)call["arguments"]; JObject body;
                switch ((string)call["name"])
                {
                    case "horizun_query_classification": body = JObject.Parse("{placed_types_without_code:0,types_with_unknown_code:0}"); break;
                    case "horizun_audit_model": body = new JObject { ["findings"] = new JArray(
                        new JObject { ["check"] = "warnings", ["count"] = 0 },
                        new JObject { ["check"] = "level_span", ["count"] = 0, ["coverage_complete"] = true }) }; break;
                    case "horizun_clash": body = new JObject { ["result"] = "complete", ["clashes"] = new JArray() }; break;
                    case "horizun_query_family_internals": body = (string)args["mode"] == "audit_titleblock_text"
                        ? new JObject { ["coverage_complete"] = true, ["brand_check"] = "measured", ["findings"] = new JArray(new JObject { ["code"] = "brand_spelling" }) }
                        : new JObject { ["coverage_complete"] = true, ["types"] = new JArray() }; break;
                    case "horizun_query_model":
                        if (args["element_ids"] != null)
                        {
                            if (_foreignFailure) throw new InvalidOperationException("type read failed");
                            var type = new JObject { ["element_id"] = 10, ["family"] = "Door", ["type"] = "Door 800", ["parameters"] = new JObject() };
                            if (_foreignField == "type" || _foreignField == "family") type[_foreignField] = "TEST_FORBIDDEN";
                            else type["parameters"][_foreignField] = new JObject { ["exists"] = true, ["raw"] = "TEST_FORBIDDEN" };
                            body = new JObject { ["coverage_complete"] = true, ["rows"] = new JArray(type) };
                        }
                        else if (args["categories"] != null) body = new JObject { ["rows"] = _blockCount == 0 ? new JArray() : new JArray(new JObject { ["element_id"] = 22, ["family"] = "Sheet border" }) };
                        else body = new JObject { ["coverage_complete"] = !_partial, ["rows"] = new JArray(_row.DeepClone()) };
                        break;
                    default: throw new InvalidOperationException("unexpected tool");
                }
                return new JObject { ["structuredContent"] = body };
            };
        }

        private static JObject Entry(string name, double envelope, double? nominal)
        {
            var e = new JObject
            {
                ["key"] = "Door|" + name, ["family"] = "Door", ["type"] = name, ["name_es"] = "Puerta", ["category"] = "Doors", ["code"] = "22.01",
                ["dims"] = new JObject { ["Width"] = envelope }, ["placement"] = new JObject { ["host"] = "wall" }
            };
            if (nominal.HasValue) e["nominal"] = new JObject { ["Width"] = nominal.Value };
            return e;
        }
        private JObject Call(string op, JObject args = null)
        {
            File.WriteAllText(PathKit, _kit.ToString());
            args = args ?? new JObject(); args["operation"] = op; args["path"] = PathKit;
            if (op == "audit") args["target_document"] = "Temporary model";
            return TemplateKitTool.Handle(args, CancellationToken.None);
        }
        public void Dispose()
        {
            TemplateKitTool.Invoker = _savedInvoker;
            Directory.Delete(_dir, true);
        }
        private void Check(string id, bool blocking = true) => ((JArray)_kit["checks"]).Add(new JObject { ["id"] = id, ["name"] = id + " name", ["blocking"] = blocking });
        private JObject Finding(JObject result, string check) => ((JArray)result["findings"]).OfType<JObject>().Single(f => (string)f["check"] == check);

        [Theory]
        [InlineData(800, "nominal", 950)]
        [InlineData(700, "dims", 700)]
        public void Find_uses_nominal_or_falls_back_to_envelope(double wanted, string basis, double envelope)
        {
            JObject r = Call("find", new JObject { ["dims"] = new JObject { ["Width"] = wanted } });
            Assert.Equal(1, (int)r["total"]); Assert.Equal(basis, (string)r["hits"][0]["dimensions"]["search_basis"]);
            Assert.Equal(envelope, (double)r["hits"][0]["dimensions"]["envelope_mm"]["Width"]);
        }
        [Fact]
        public void Numeric_intent_ranks_the_nominal_door_first()
            => Assert.Equal("Door 800", (string)Call("find", new JObject { ["text"] = "puerta 800" })["hits"][0]["type"]);

        [Fact]
        public void Existing_only_without_exact_size_has_no_creation_advice()
        {
            JObject r = Call("find", new JObject { ["policy"] = "existing_only", ["near"] = new JObject { ["Width"] = 850 } });
            Assert.Equal("no_match", (string)r["status"]); Assert.Empty((JArray)r["hits"]); Assert.Null(r["size_missing"]); Assert.Null(r["how_to_place"]);
        }
        [Theory]
        [InlineData(800.5, 1)]
        [InlineData(800.51, 0)]
        public void Existing_only_uses_half_mm_tolerance_per_dimension(double value, int count)
            => Assert.Equal(count, (int)Call("find", new JObject { ["policy"] = "existing_only", ["near"] = new JObject { ["Width"] = value } })["total"]);

        [Fact]
        public void Default_policy_prefers_an_exact_existing_size_and_does_not_invent_resize_recipes()
        {
            Assert.Equal("existing_first", (string)Call("find")["policy"]);
            Assert.Equal("Door 700", (string)Call("find", new JObject { ["near"] = new JObject { ["Width"] = 700 } })["hits"][0]["type"]);
            var r = Call("find", new JObject { ["policy"] = "allow_create", ["near"] = new JObject { ["Width"] = 850 } });
            Assert.Contains("no executable resize recipe", (string)r["size_missing"]["note"]);
        }
        [Fact]
        public void Unusable_types_are_opt_in_and_keep_their_reason()
        {
            _kit["catalog"][0]["usable"] = false; _kit["catalog"][0]["usable_reason"] = "test retired";
            Assert.Equal(1, (int)Call("find")["total"]);
            var r = Call("find", new JObject { ["include_unusable"] = true });
            Assert.Equal(2, (int)r["total"]); Assert.Contains(((JArray)r["hits"]).OfType<JObject>(), h => (bool?)h["usable"] == false && (string)h["usable_reason"] == "test retired");
        }
        [Fact]
        public void Placed_unusable_type_is_a_blocking_finding()
        {
            _kit["catalog"][0]["usable"] = false;
            Assert.Equal(1, (int)Finding(Call("audit"), "legacy")["count"]);
        }
        [Fact]
        public void Unusable_system_type_is_resolved_by_its_type_only_key()
        {
            _kit["catalog"][0]["key"] = "Door 800"; _kit["catalog"][0]["usable"] = false;
            Assert.Equal(1, (int)Finding(Call("audit"), "legacy")["count"]);
        }
        [Fact]
        public void Skipped_clearance_geometry_and_absent_datum_measurement_are_not_executed()
        {
            _kit["rules"] = new JArray(new JObject { ["topic"] = "clearances", ["rows"] = new JArray(new JObject { ["front_of"] = "door", ["mm"] = 600 }) });
            Check("front_clearance"); Check("datums");
            var r = Call("audit");
            Assert.All(((JArray)r["checks_coverage"]).OfType<JObject>(), c => Assert.Equal("not_executed", (string)c["status"]));
            Assert.Equal("usable_with_corrections", (string)r["verdict"]);
        }
        [Theory]
        [InlineData("HZ_AI_Source", false, null, "absent")]
        [InlineData("HZ_AI_Status", false, null, "absent")]
        [InlineData("HZ_AI_Source", true, "", "empty")]
        [InlineData("HZ_AI_Status", true, "", "empty")]
        [InlineData("HZ_AI_Status", true, "unexpected", "not_allowed")]
        public void Audit_checks_each_trace_parameter(string parameter, bool exists, string value, string issue)
        {
            _kit["conventions"]["traceability"] = new JObject { [parameter] = new JObject { ["allowed_values"] = new JArray("plan", "checked") } };
            _row["parameters"][parameter] = new JObject { ["exists"] = exists, ["raw"] = value };
            var f = Finding(Call("audit"), "traceability");
            Assert.Equal(parameter, (string)f["parameter_issues"][0]["parameter"]); Assert.Equal(issue, (string)f["parameter_issues"][0]["issue"]);
        }
        [Fact]
        public void Every_check_gets_coverage_and_unimplemented_blocking_gate_prevents_usable_today()
        {
            Check("traceability"); ((JArray)_kit["checks"]).Add(new JObject { ["name"] = "compare drawings" });
            var r = Call("audit"); Assert.Equal("usable_with_corrections", (string)r["verdict"]);
            Assert.Equal("executed", (string)r["checks_coverage"][0]["status"]);
            Assert.Equal("compare drawings", (string)r["checks_coverage"][1]["id"]);
            Assert.Equal("not_executed", (string)r["checks_coverage"][1]["status"]); Assert.NotEmpty((string)r["checks_coverage"][1]["reason"]);
        }
        [Fact]
        public void Incomplete_read_is_not_executed_and_optional_unknown_gate_is_not_blocking()
        {
            Check("traceability"); _partial = true; var r = Call("audit"); Assert.Equal("not_executed", (string)r["checks_coverage"][0]["status"]);
            _partial = false; _kit["checks"] = new JArray(); Check("manual_review", false);
            Assert.Equal("usable_today", (string)Call("audit")["verdict"]);
        }
        [Theory]
        [InlineData("type")]
        [InlineData("family")]
        [InlineData("ALL_MODEL_MANUFACTURER")]
        [InlineData("ALL_MODEL_MODEL")]
        [InlineData("ALL_MODEL_DESCRIPTION")]
        [InlineData("ALL_MODEL_TYPE_COMMENTS")]
        public void Foreign_names_audit_reads_used_types_and_all_required_fields(string field)
        {
            _foreignField = field; Check("foreign_names"); _kit["conventions"]["foreign_names"] = new JObject { ["forbidden_regex"] = new JArray("TEST_FORBIDDEN") };
            var r = Call("audit"); Assert.Equal(field, (string)Finding(r, "foreign_names")["sample"][0]["field"]);
            Assert.Equal("executed", (string)r["checks_coverage"][0]["status"]); Assert.Equal("not_yet", (string)r["verdict"]);
            Assert.Contains(_calls.OfType<JObject>(), c => c["arguments"]["element_ids"] != null && (bool)c["arguments"]["include_types"]);
        }
        [Fact]
        public void Unreadable_types_and_bad_regex_never_pass_foreign_names()
        {
            Check("foreign_names"); _kit["conventions"]["foreign_names"] = new JObject { ["forbidden"] = new JArray("TEST_FORBIDDEN") };
            _foreignFailure = true; Assert.Equal("not_executed", (string)Call("audit")["checks_coverage"][0]["status"]);
            _foreignFailure = false; _kit["conventions"]["foreign_names"] = new JObject { ["forbidden_regex"] = new JArray("[") };
            Assert.Equal("not_executed", (string)Call("audit")["checks_coverage"][0]["status"]);
        }
        [Fact]
        public void Validation_separates_structure_recipes_and_contract_hash()
        {
            _kit["mcp"]["contract_hash"] = "old";
            _kit["playbook"]["broken"] = new JObject { ["tool"] = "horizun_get_schedule_data", ["args"] = new JObject { ["schedule"] = "bad", ["format"] = "bad" } };
            var r = Call("validate"); Assert.True((bool)r["kit_valid"]); Assert.False((bool)r["recipes_valid"]); Assert.False((bool)r["contract_match"]["match"]);
            Assert.Contains("schedule", r["recipe_problems"].ToString()); Assert.Contains("required", r["recipe_problems"].ToString());
            Assert.True(JToken.DeepEquals(r["contract_match"], Call("load")["contract_match"]));
            Assert.Equal(new[] { "kit_hash", "match", "note", "server_hash" }, ((JObject)r["contract_match"]).Properties().Select(p => p.Name).OrderBy(n => n).ToArray());
        }
        [Fact]
        public void Recipe_markers_count_as_present_in_required_numeric_and_enum_fields()
        {
            _kit["checks"] = new JArray(new JObject { ["tool"] = "horizun_get_schedule_data", ["args"] = new JObject { ["schedule_id"] = "<schedule id>" } });
            Assert.True((bool)Call("validate")["recipes_valid"]);
            _kit["catalog"][0]["new_size"] = new JObject { ["tool"] = "horizun_query_model", ["args"] = new JObject { ["response_mode"] = "invented" } };
            Assert.False((bool)Call("validate")["recipes_valid"]);
        }
        [Fact]
        public void Placement_recipe_and_new_size_are_checked_against_the_real_contract()
        {
            _kit["catalog"][0]["placement"]["recipe"] = new JObject { ["kind"] = "family_instance", ["not_an_argument"] = "<present>" };
            _kit["catalog"][1]["new_size"] = new JObject { ["tool"] = "missing_test_tool", ["args"] = new JObject() };
            var r = Call("validate"); Assert.False((bool)r["recipes_valid"]);
            Assert.Equal(2, ((JArray)r["recipe_problems"]).Count);
        }
        [Fact]
        public void Placement_fragment_gets_request_context_without_fabricating_element_arguments()
        {
            _kit["catalog"][0]["placement"]["recipe"] = new JObject
            {
                ["kind"] = "family_instance", ["type_id"] = "<type id>", ["level_id"] = "<level id>",
                ["coordinate_mode"] = "level_offset",
                ["point"] = new JArray("<x>", "<y>", "<z>")
            };
            JObject result = Call("validate");
            Assert.True((bool)result["recipes_valid"], result["recipe_problems"].ToString());
            Assert.Equal(1, (int)result["recipes_checked"]);
        }
        [Fact]
        public void Scalar_catalog_entries_return_structural_problems_instead_of_throwing()
        {
            ((JArray)_kit["catalog"]).Add(17); ((JArray)_kit["catalog"]).Add(JValue.CreateNull());
            Assert.False((bool)Call("validate")["kit_valid"]);
        }

        [Theory]
        [InlineData("catalog")]
        [InlineData("product_families")]
        public void Placement_uses_the_declared_railing_tool_without_an_elements_envelope(string section)
        {
            _kit[section] = new JArray(new JObject
            {
                ["placement"] = new JObject
                {
                    ["tool"] = "horizun_create_railing",
                    ["recipe"] = new JObject { ["type_id"] = "<type>", ["level_id"] = "<level>", ["path"] = new JArray(new JArray(0, 0, 0), new JArray(1000, 0, 0)) }
                }
            });
            JObject result = Call("validate");
            Assert.True((bool)result["recipes_valid"], result["recipe_problems"].ToString());
            _kit[section][0]["placement"]["recipe"]["path"] = 42;
            result = Call("validate");
            var problem = Assert.Single(((JArray)result["recipe_problems"]).OfType<JObject>());
            Assert.Equal("horizun_create_railing", (string)problem["tool"]);
            Assert.Contains("path", problem["errors"].ToString());
        }

        [Theory]
        [InlineData("horizun_query_model", "response_mode", "invalid")]
        [InlineData("missing_tool", "anything", "value")]
        public void Placement_other_tools_are_not_silently_validated_as_create_elements(string tool, string field, string value)
        {
            _kit["catalog"][0]["placement"] = new JObject { ["tool"] = tool, ["recipe"] = new JObject { [field] = value } };
            JObject result = Call("validate");
            var problem = Assert.Single(((JArray)result["recipe_problems"]).OfType<JObject>());
            Assert.Equal(tool, (string)problem["tool"]);
        }

        [Fact]
        public void Resolve_nested_beside_a_recipe_is_checked_but_ifc_metadata_is_not_a_call()
        {
            _kit["catalog"][0]["ifc"] = new JObject { ["class"] = "IfcDoor", ["predefined_type"] = "DOOR" };
            _kit["checks"] = new JArray(new JObject
            {
                ["tool"] = "horizun_get_schedule_data", ["args"] = new JObject { ["schedule_id"] = "<schedule>" },
                ["resolve"] = new JObject { ["tool"] = "horizun_list_schedules", ["args"] = new JObject { ["invalid_arg"] = true } }
            });
            var problem = Assert.Single(((JArray)Call("validate")["recipe_problems"]).OfType<JObject>());
            Assert.Equal("checks[0].resolve", (string)problem["path"]);
        }
        [Fact]
        public void Discovery_tools_have_recipes_and_are_available_in_core_profile()
        {
            string saved = ToolVisibility.CommandLineProfile;
            try
            {
                ToolVisibility.ResetSession(); ToolVisibility.CommandLineProfile = "nucleo";
                _kit["playbook"]["discover"] = new JObject { ["tool"] = "horizun_toolsets", ["args"] = new JObject { ["action"] = "list" } };
                Assert.True((bool)Call("validate")["recipes_valid"]);
                Assert.DoesNotContain(((JArray)Call("load")["unavailable_tools"]).OfType<JObject>(), t => (string)t["tool"] == "horizun_toolsets");
            }
            finally { ToolVisibility.CommandLineProfile = saved; ToolVisibility.ResetSession(); }
        }
        [Fact]
        public void List_load_and_validate_share_contract_and_template_match_objects()
        {
            string saved = Environment.GetEnvironmentVariable("HORIZUN_DATA_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", _dir);
                string kitDir = TemplateKitTool.KitsDir(); Directory.CreateDirectory(kitDir);
                JObject load = Call("load"); File.Copy(PathKit, Path.Combine(kitDir, "kit.hzkit.json"));
                JObject list = TemplateKitTool.Handle(new JObject { ["operation"] = "list" }, CancellationToken.None);
                Assert.True(JToken.DeepEquals(list["kits"][0]["contract_match"], load["contract_match"]));
                Assert.True(JToken.DeepEquals(Call("validate")["contract_match"], load["contract_match"]));
            }
            finally { Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", saved); }
        }
        [Fact]
        public void Load_echoes_selection_policy_and_reports_tools_missing_from_active_groups()
        {
            string saved = ToolVisibility.CommandLineProfile;
            try
            {
                ToolVisibility.ResetSession(); ToolVisibility.CommandLineProfile = "nucleo";
                _kit["conventions"]["selection_policy"] = new JObject { ["default"] = "existing_first" };
                _kit["mcp"]["tools_used"] = new JArray("horizun_manage_views");
                var r = Call("load"); Assert.Equal("existing_first", (string)r["selection_policy"]["default"]);
                Assert.Contains(((JArray)r["unavailable_tools"]).OfType<JObject>(), t => (string)t["tool"] == "horizun_manage_views" && (string)t["group"] == "documentacion");
            }
            finally { ToolVisibility.CommandLineProfile = saved; ToolVisibility.ResetSession(); }
        }
        [Fact]
        public void Template_bytes_match_or_mismatch_independently_of_contract()
        {
            string rte = Path.Combine(_dir, "test.rte"); File.WriteAllText(rte, "test bytes");
            using (var hash = System.Security.Cryptography.SHA256.Create())
                _kit["template"]["rte_sha256"] = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(rte))).Replace("-", "").ToLowerInvariant();
            _kit["template"]["file"] = "test.rte";
            Assert.True((bool)Call("load")["template_hash_match"]["match"]);
            Assert.True((bool)Call("validate")["template_hash_match"]["match"]);
            File.AppendAllText(rte, "changed"); Assert.False((bool)Call("validate")["template_hash_match"]["match"]);
            Assert.Equal(JTokenType.Null, Call("load", new JObject { ["model_path"] = Path.Combine(_dir, "model.rvt") })["template_hash_match"]["match"].Type);
        }
    }
}
