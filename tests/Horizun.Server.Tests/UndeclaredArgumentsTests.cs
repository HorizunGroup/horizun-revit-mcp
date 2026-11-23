// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// UNDECLARED ARGUMENTS ARE REFUSED BEFORE DISPATCH. The failure bench (October
// 2026) measured `description` in place of `purpose` (F1) and `dry_run` on
// horizun_execute_python (F4) running silently on Revit 2024 and 2025: the schemas
// said additionalProperties:false and nothing enforced it. These tests pin the
// rule (UndeclaredArguments), its deliberate exceptions, and that Program.cs
// applies it on the real stdio path before routing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class UndeclaredArgumentsTests
    {
        [Fact]
        public void A_wall_start_point_typo_is_refused_before_routing_with_the_start_suggestion()
        {
            var args = JObject.Parse("{'elements':[{'kind':'wall','start_point':[0,0,0],'end':[1000,0,0]}]}");
            JObject result = CallOverStdio("horizun_create_elements", args);
            JObject detail = (JObject)result["structuredContent"];
            Assert.True(result.Value<bool>("isError"));
            Assert.Equal("esquema_invalido", detail.Value<string>("error_code"));
            Assert.Equal("elements[0].start_point", detail.Value<string>("field_path"));
            Assert.Equal("start", detail.Value<string>("suggested_field"));
            Assert.Contains("Did you mean 'start'?", detail.Value<string>("message"));
            Assert.False(detail.Value<bool>("write_started"));
            Assert.False(detail.Value<bool>("changes_applied"));
            Assert.False(detail["fallback"].Value<bool>("allowed"));
        }

        [Fact]
        public void Wildcard_without_required_format_is_refused_before_revit()
        {
            JObject result = CallOverStdio("horizun_query_model", new JObject { ["return_parameters"] = new JArray("*") });
            Assert.Equal("esquema_invalido", (string)result["structuredContent"]["error_code"]);
            Assert.False((bool)result["structuredContent"]["write_started"]);
            Assert.Contains("parameter_format", result.ToString());
        }
        [Fact]
        public void Python_timeout_ms_refusal_names_seconds()
        {
            var found = UndeclaredArguments.Find("horizun_execute_python", new JObject { ["timeout_ms"] = 1000 });
            Assert.Contains("timeout_ms", found);
            var refusal = UndeclaredArguments.Refusal("horizun_execute_python", found);
            Assert.Contains("did you mean timeout_s (seconds)?", (string)refusal["text"]);
            Assert.Equal("timeout_s", (string)refusal["detail"]["suggestions"]["timeout_ms"]);
        }

        [Theory]
        [InlineData("operation")][InlineData("dry_run")][InlineData("target_document")]
        public void Open_document_refusal_points_to_document_session(string name)
        {
            var found = UndeclaredArguments.Find("horizun_open_document", new JObject { [name] = "example" });
            Assert.Contains(name, found);
            Assert.Contains("to list or manage open documents use horizun_document_session", (string)UndeclaredArguments.Refusal("horizun_open_document", found)["text"]);
        }

        [Theory]
        [InlineData("png")][InlineData("jpg")][InlineData("jpeg")][InlineData("tif")]
        public void Image_extension_as_export_format_suggests_image(string format)
        {
            Assert.Contains("use format=image", ExportDeliveryRules.InvalidFormatMessage(format));
            Assert.DoesNotContain("use format=image", ExportDeliveryRules.InvalidFormatMessage("other"));
        }

        [Fact]
        public void Coordination_accepts_legacy_default()
        {
            var args = new JObject { ["operation"] = "show", ["max_views"] = 10 };
        }

        [Theory]
        [InlineData("show", false, 3)]
        [InlineData("list", true, 3)]
        [InlineData("show", true, 10)]
        public void Coordination_accepts_noop_legacy_value(string operation, bool perIssue, int maxViews)
        {
            var args = new JObject { ["operation"] = operation, ["per_issue"] = perIssue, ["max_views"] = maxViews };
        }

        [Fact]
        public void Python_mode_confusion_suggests_read_only_boolean_not_code()
        {
            JObject refusal = UndeclaredArguments.Refusal("horizun_execute_python", new[] { "mode" });
            Assert.Equal("read_only", (string)refusal["detail"]["suggestions"]["mode"]);
            Assert.True((bool)refusal["detail"]["argument_help"]["arguments"]["read_only"]);
            Assert.DoesNotContain("did you mean 'code'", (string)refusal["text"]);
        }
        [Theory]
        [InlineData("horizun_list_elements", "family", "horizun_query_model", "categories")]
        [InlineData("horizun_list_elements", "element_ids", "horizun_query_model", "element_ids")]
        [InlineData("horizun_query_model", "category", "horizun_query_model", "categories")]
        [InlineData("horizun_manage_views", "operation", "horizun_query_planimetry", "mode")]
        [InlineData("horizun_navigate", "action", "horizun_navigate", "operation")]
        [InlineData("horizun_navigate", "view_name", "get_document_info", "operation")]
        [InlineData("horizun_export", "response_mode", "horizun_export", "format")]
        [InlineData("horizun_model_diff", "baseline_id", "horizun_model_diff", "before")]
        public void Field_confusions_name_the_supported_tool_and_give_a_contract_valid_example(
            string tool, string argument, string recommendedTool, string exampleField)
        {
            JObject refusal = UndeclaredArguments.Refusal(tool, new[] { argument });
            JObject help = (JObject)refusal["detail"]["argument_help"];
            Assert.NotNull(help);
            Assert.Equal(recommendedTool, (string)help["tool"]);
            Assert.NotNull(help["arguments"][exampleField]);
            Assert.Empty(ContractSchemaCheck.Validate(help["arguments"], Contract.Find(recommendedTool).InputSchema));
            Assert.Contains("Example:", (string)refusal["text"]);
            Assert.False((bool)refusal["detail"]["write_started"]);
        }

        private static JObject PythonArgs(params (string, JToken)[] extra)
        {
            var a = new JObject
            {
                ["target_document"] = "Casa", ["purpose"] = "test", ["code"] = "x = 1", ["idempotency_key"] = "k"
            };
            foreach (var (k, v) in extra) a[k] = v;
            return a;
        }

        [Fact]
        public void Description_in_place_of_purpose_is_named_and_purpose_suggested()
        {
            JObject args = PythonArgs(("description", "banco"));
            args.Remove("purpose");
            List<string> found = UndeclaredArguments.Find("horizun_execute_python", args);
            Assert.Equal(new[] { "description" }, found);

            JObject refusal = UndeclaredArguments.Refusal("horizun_execute_python", found);
            string text = (string)refusal["text"];
            Assert.Contains("'description' (did you mean 'purpose'?)", text);
            Assert.Contains("Nothing was run and nothing was written", text);
            Assert.Contains("horizun://contract/tools/horizun_execute_python", text);
            JObject detail = (JObject)refusal["detail"];
            Assert.Equal(ErrorCodes.EsquemaInvalido, (string)detail[ErrorCodes.CodeField]);
            Assert.Equal("purpose", (string)detail["suggestions"]["description"]);
            Assert.Equal("/description", (string)detail["schema_rejection"]["path"]);
            Assert.Equal("additional_property", (string)detail["schema_rejection"]["rule"]);
            Assert.False((bool)detail["write_started"]);
            Assert.False((bool)detail["changes_applied"]);
            Assert.Equal("horizun://contract/tools/horizun_execute_python", (string)detail["contract_uri"]);
        }

        [Fact]
        public void Dry_run_on_a_tool_that_does_not_declare_it_is_refused()
        {
            List<string> found = UndeclaredArguments.Find("get_document_info", new JObject { ["operation"] = "context", ["dry_run"] = true });
            Assert.Equal(new[] { "dry_run" }, found);
            JObject refusal = UndeclaredArguments.Refusal("horizun_execute_python", new[] { "dry_run" });
            Assert.Equal("preflight", (string)refusal["detail"]["suggestions"]["dry_run"]);

            // A tool whose schema declares dry_run takes it.
            Assert.Empty(UndeclaredArguments.Find("horizun_join_geometry",
                new JObject { ["target_document"] = "Casa", ["dry_run"] = true }));

            UndeclaredArguments.NormalizeAliases("horizun_execute_python", PythonArgs(("dry_run", false)), out _, out string refusalText);
            Assert.Contains("preflight=true", refusalText);
            Assert.Contains("Nothing was run", refusalText);
        }

        [Fact]
        public void A_well_formed_call_passes_and_every_name_is_listed_in_the_refusal()
        {
            Assert.Empty(UndeclaredArguments.Find("horizun_execute_python", PythonArgs()));
            JObject refusal = UndeclaredArguments.Refusal("horizun_execute_python", new[] { "a", "b" });
            Assert.Contains("the arguments 'a', 'b'", (string)refusal["text"]);
            Assert.Equal(2, ((JArray)refusal["detail"]["undeclared_arguments"]).Count);
        }

        [Fact]
        public void Safe_document_and_limit_aliases_are_normalized_with_field_only_warnings()
        {
            JObject normalized = UndeclaredArguments.NormalizeAliases("horizun_list_elements",
                JObject.Parse("{category:'OST_Walls',doc:'Example',limit:25}"), out List<string> warnings, out string refusal);
            Assert.Null(refusal);
            Assert.Equal("Example", (string)normalized["target_document"]);
            Assert.Equal(25, (int)normalized["max_rows"]);
            Assert.Null(normalized["doc"]);
            Assert.Null(normalized["limit"]);
            Assert.Equal(new[] { "doc accepted as target_document", "limit accepted as max_rows" }, warnings);
            Assert.Empty(UndeclaredArguments.Find("horizun_list_elements", normalized));
        }

        [Theory]
        [InlineData("horizun_open_document", "{file_path:'C:\\\\Example\\\\model.rvt'}", "path")]
        [InlineData("horizun_open_document", "{model_path:'C:\\\\Example\\\\model.rvt'}", "path")]
        [InlineData("horizun_document_session", "{operation:'inspect',path:'C:\\\\Example\\\\model.rvt'}", "file_path")]
        public void File_path_aliases_are_scoped_to_the_matching_document_operation(string tool, string json, string canonical)
        {
            JObject normalized = UndeclaredArguments.NormalizeAliases(tool, JObject.Parse(json), out _, out string refusal);
            Assert.Null(refusal);
            Assert.NotNull(normalized[canonical]);

            if (tool == "horizun_document_session")
            {
                JObject save = UndeclaredArguments.NormalizeAliases(tool,
                    JObject.Parse("{operation:'save',path:'C:\\\\Example\\\\model.rvt'}"), out _, out refusal);
                Assert.Null(save["file_path"]);
                Assert.NotNull(refusal);
            }
        }

        [Fact]
        public void Document_session_accepts_action_and_discard_changes_only_as_close_aliases()
        {
            JObject normalized = UndeclaredArguments.NormalizeAliases("horizun_document_session",
                JObject.Parse("{action:'close',discard_changes:true}"), out List<string> warnings, out string refusal);
            Assert.Null(refusal);
            Assert.Equal("close", (string)normalized["operation"]);
            Assert.True((bool)normalized["discard_unsaved"]);
            Assert.Null(normalized["action"]);
            Assert.Null(normalized["discard_changes"]);
            Assert.Equal(new[] { "action accepted as operation", "discard_changes accepted as discard_unsaved" }, warnings);
            Assert.Empty(UndeclaredArguments.Find("horizun_document_session", normalized));

            UndeclaredArguments.NormalizeAliases("horizun_document_session",
                JObject.Parse("{operation:'save',discard_changes:true}"), out _, out refusal);
            Assert.Null(refusal);
            Assert.NotNull(UndeclaredArguments.Refusal("horizun_document_session", new[] { "discard_changes" },
                JObject.Parse("{operation:'save',discard_changes:true}"))["detail"]["minimal_valid_call"]);
        }

        [Theory]
        [InlineData("horizun_navigate", "{action:'select',element_ids:[1]}", "action", "operation")]
        [InlineData("horizun_model_diff", "{action:'compare',before:'a',after:'b'}", "action", "operation")]
        [InlineData("horizun_query_structure", "{operation:'members'}", "operation", "mode")]
        [InlineData("horizun_export", "{format:'pdf',sheet_ids:[7],output_path:'C:\\\\o\\\\a.pdf'}", "sheet_ids", "view_ids")]
        [InlineData("horizun_execute_python", "{purpose:'p',file_path:'C:\\\\s\\\\a.py'}", "file_path", "code_path")]
        public void Same_value_renames_seen_in_telemetry_reach_the_declared_name(string tool, string json, string sent, string declaredName)
        {
            JObject input = JObject.Parse(json);
            JObject normalized = UndeclaredArguments.NormalizeAliases(tool, input, out List<string> warnings, out string refusal);
            Assert.Null(refusal);
            Assert.True(JToken.DeepEquals(input[sent], normalized[declaredName]));
            Assert.Null(normalized[sent]);
            Assert.Equal(new[] { sent + " accepted as " + declaredName }, warnings);
            Assert.Empty(UndeclaredArguments.Find(tool, normalized));

            JObject conflict = (JObject)input.DeepClone();
            conflict[declaredName] = "something else";
            UndeclaredArguments.NormalizeAliases(tool, conflict, out _, out refusal);
            Assert.Contains("values conflict", refusal);
        }

        [Fact]
        public void Export_folder_and_name_and_job_status_wait_are_refused_with_the_way_to_say_it()
        {
            string text = (string)UndeclaredArguments.Refusal("horizun_export", new[] { "output_folder", "file_name" },
                JObject.Parse("{format:'pdf',output_folder:'C:\\\\o',file_name:'a'}"))["text"];
            Assert.Contains("ONE output_path", text);
            text = (string)UndeclaredArguments.Refusal("horizun_job_status", new[] { "wait_ms" }, JObject.Parse("{job_id:'j',wait_ms:5}"))["text"];
            Assert.Contains("Use wait_s", text);
        }

        [Fact]
        public void Document_session_alias_conflicts_are_refused_without_rewriting_the_request()
        {
            JObject input = JObject.Parse("{action:'open',operation:'close'}");
            UndeclaredArguments.NormalizeAliases("horizun_document_session", input, out _, out string refusal);
            Assert.Contains("values conflict", refusal);
            Assert.Equal("open", (string)input["action"]);
            Assert.Equal("close", (string)input["operation"]);

            UndeclaredArguments.NormalizeAliases("horizun_document_session",
                JObject.Parse("{operation:'close',discard_unsaved:false,discard_changes:true}"), out _, out refusal);
            Assert.Contains("values conflict", refusal);
        }

        [Fact]
        public void Query_model_accepts_numeric_ids_aliases_but_not_a_non_number_or_conflict()
        {
            JObject normalized = UndeclaredArguments.NormalizeAliases("horizun_query_model",
                JObject.Parse("{ids:'123'}"), out List<string> warnings, out string refusal);
            Assert.Null(refusal);
            Assert.Equal(new[] { 123 }, normalized["element_ids"].Values<int>());
            Assert.Single(warnings);

            normalized = UndeclaredArguments.NormalizeAliases("horizun_query_model", JObject.Parse("{ids:'1, 2,3'}"), out _, out refusal);
            Assert.Null(refusal);
            Assert.Equal(new long[] { 1, 2, 3 }, normalized["element_ids"].Values<long>());
            Assert.Empty(UndeclaredArguments.Find("horizun_query_model", normalized));
            UndeclaredArguments.NormalizeAliases("horizun_query_model", JObject.Parse("{ids:'1,x'}"), out _, out refusal);
            Assert.Contains("'x' is not one", refusal);
            UndeclaredArguments.NormalizeAliases("horizun_query_model", JObject.Parse("{ids:'1',element_ids:[2]}"), out _, out refusal);
            Assert.Contains("either ids or element_ids", refusal);
        }

        [Fact]
        public void Save_document_mode_is_accepted_as_operation_only_for_a_real_operation()
        {
            JObject normalized = UndeclaredArguments.NormalizeAliases("horizun_save_document",
                JObject.Parse("{mode:'save_local',target_document:'A'}"), out List<string> warnings, out string refusal);
            Assert.Null(refusal);
            Assert.Equal("save_local", (string)normalized["operation"]);
            Assert.Null(normalized["mode"]);
            Assert.Empty(UndeclaredArguments.Find("horizun_save_document", normalized));
            UndeclaredArguments.NormalizeAliases("horizun_save_document", JObject.Parse("{mode:'quick',target_document:'A'}"), out _, out refusal);
            Assert.Contains("'quick' is none of them", refusal);
            UndeclaredArguments.NormalizeAliases("horizun_save_document",
                JObject.Parse("{mode:'save_local',operation:'relinquish_own',target_document:'A'}"), out _, out refusal);
            Assert.Contains("values conflict", refusal);
        }

        [Fact]
        public void Python_args_object_or_json_text_becomes_arguments()
        {
            JObject normalized = UndeclaredArguments.NormalizeAliases("horizun_execute_python",
                PythonArgs(("args", new JValue("{\"level\": \"N1\"}"))), out List<string> warnings, out string refusal);
            Assert.Null(refusal);
            Assert.Equal("N1", (string)normalized["arguments"]["level"]);
            Assert.Null(normalized["args"]);
            Assert.Empty(UndeclaredArguments.Find("horizun_execute_python", normalized));
            UndeclaredArguments.NormalizeAliases("horizun_execute_python", PythonArgs(("args", new JValue("not json"))), out _, out refusal);
            Assert.Contains("args is not a JSON object", refusal);
            UndeclaredArguments.NormalizeAliases("horizun_execute_python", PythonArgs(("args", new JArray(1))), out _, out refusal);
            Assert.Contains("args must be an object", refusal);
        }

        [Fact]
        public void Query_model_category_alias_becomes_a_single_category_filter()
        {
            JObject normalized = UndeclaredArguments.NormalizeAliases("horizun_query_model",
                JObject.Parse("{category:'OST_Walls'}"), out List<string> warnings, out string refusal);
            Assert.Null(refusal);
            Assert.Equal(new[] { "OST_Walls" }, normalized["categories"].Values<string>());
            Assert.Null(normalized["category"]);
            Assert.Single(warnings);
            Assert.Empty(UndeclaredArguments.Find("horizun_query_model", normalized));
        }

        [Fact]
        public void Python_dry_run_true_becomes_non_executing_preflight_but_false_is_refused()
        {
            JObject normalized = UndeclaredArguments.NormalizeAliases("horizun_execute_python",
                PythonArgs(("dry_run", new JValue(true))), out List<string> warnings, out string refusal);
            Assert.Null(refusal);
            Assert.True((bool)normalized["preflight"]);
            Assert.Null(normalized["dry_run"]);
            Assert.Contains("will not execute", warnings.Single());

            UndeclaredArguments.NormalizeAliases("horizun_execute_python",
                PythonArgs(("dry_run", new JValue(false))), out _, out refusal);
            Assert.Contains("has no dry_run mode", refusal);
        }

        [Fact]
        public void Alias_conflict_is_refused_without_choosing_a_target()
        {
            UndeclaredArguments.NormalizeAliases("horizun_query_model",
                JObject.Parse("{doc:'First',target_document:'Second'}"), out _, out string refusal);
            Assert.Contains("values conflict", refusal);
            Assert.Contains("ambiguous", refusal);
        }

        [Fact]
        public void Every_undeclared_refusal_has_a_contract_valid_minimal_example()
        {
            JObject refusal = UndeclaredArguments.Refusal("horizun_execute_python", new[] { "dry_run" });
            JObject example = (JObject)refusal["detail"]["minimal_valid_call"];
            Assert.Equal("horizun_execute_python", (string)example["tool"]);
            Assert.Empty(ContractSchemaCheck.Validate(example["arguments"], Contract.Find("horizun_execute_python").InputSchema));
        }

        [Fact]
        public void Mcp_meta_is_accepted_on_every_tool()
        {
            foreach (CommandContract c in Contract.All)
                Assert.Empty(UndeclaredArguments.Find(c.Name, new JObject { ["_meta"] = new JObject { ["progressToken"] = 1 } }));
        }

        [Fact]
        public void Document_aliases_are_accepted_only_where_target_document_is_declared()
        {
            var aliases = new JObject { ["expected_document"] = "Casa", ["target_document_title"] = "Casa" };
            // DocumentGate reads both; three descriptions promise them.
            Assert.Empty(UndeclaredArguments.Find("horizun_relinquish_all", aliases));
            Assert.Empty(UndeclaredArguments.Find("horizun_execute_python", aliases));
            // A tool with no target_document has nothing for them to alias.
            Assert.Equal(new[] { "expected_document", "target_document_title" },
                         UndeclaredArguments.Find("horizun_health", aliases));
        }

        [Fact]
        public void Every_name_a_closed_schema_declares_passes_including_variant_fields()
        {
            foreach (CommandContract c in Contract.All)
            {
                HashSet<string> declared = UndeclaredArguments.Declared(c.InputSchema);
                if (declared == null) continue;
                var args = new JObject();
                foreach (string n in declared) args[n] = JValue.CreateNull();
                Assert.True(UndeclaredArguments.Find(c.Name, args).Count == 0, c.Name);
            }
        }

        [Fact]
        public void A_field_declared_only_inside_a_top_level_variant_is_an_argument()
        {
            var schema = JObject.Parse(@"{""type"":""object"",""additionalProperties"":false,
                ""properties"":{""operation"":{""type"":""string""}},
                ""oneOf"":[{""properties"":{""a"":{}}},{""properties"":{""b"":{}}}],
                ""allOf"":[{""if"":{},""then"":{""properties"":{""c"":{}}},""else"":{""properties"":{""d"":{}}}}]}");
            HashSet<string> declared = UndeclaredArguments.Declared(schema);
            foreach (string n in new[] { "operation", "a", "b", "c", "d" }) Assert.Contains(n, declared);
            Assert.DoesNotContain("zzz", declared);
        }

        [Theory]
        [InlineData(@"{""type"":""object"",""properties"":{}}")]
        [InlineData(@"{""type"":""object"",""properties"":{},""additionalProperties"":true}")]
        [InlineData(@"{""type"":""object"",""properties"":{},""additionalProperties"":{""type"":""string""}}")]
        [InlineData(@"{""type"":""object"",""properties"":{},""additionalProperties"":false,""patternProperties"":{""^x_"":{}}}")]
        public void A_schema_that_leaves_extras_open_is_passthrough(string schema)
        {
            Assert.Null(UndeclaredArguments.Declared(JObject.Parse(schema)));
        }

        [Theory]
        [InlineData("descripton", "description")]
        [InlineData("Purpose", "purpose")]
        [InlineData("file_path", "code_path")]
        [InlineData("zzzzzz", null)]
        public void Suggestions_are_conservative(string sent, string expected)
        {
            var declared = new List<string> { "code", "code_path", "purpose", "description" };
            Assert.Equal(expected, UndeclaredArguments.Suggest(sent, declared));
        }

        [Theory]
        [InlineData("max_results")]
        [InlineData("max_items")]
        [InlineData("page_size")]
        public void A_row_cap_under_another_name_points_at_max_rows(string sent)
        {
            var declared = new List<string> { "categories", "max_rows", "offset", "response_mode" };
            Assert.Equal("max_rows", UndeclaredArguments.Suggest(sent, declared));
        }

        // ---------------- on the real stdio path ----------------

        internal static JObject CallOverStdio(string tool, JObject arguments)
        {
            string root = Path.Combine(Path.GetTempPath(), "hz-undeclared-wire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var psi = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe())
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false)
                };
                psi.Environment[HorizunPaths.RootOverrideVariable] = root;
                psi.Environment.Remove("HORIZUN_REVIT_YEAR");
                var call = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = "call-1", ["method"] = "tools/call",
                    ["params"] = new JObject { ["name"] = tool, ["arguments"] = arguments }
                };
                string[] lines =
                {
                    "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"undeclared-wire\",\"version\":\"1\"}}}",
                    "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}",
                    call.ToString(Newtonsoft.Json.Formatting.None)
                };
                using (var proc = Process.Start(psi))
                {
                    var stderr = proc.StandardError.ReadToEndAsync();
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    foreach (string l in lines) proc.StandardInput.WriteLine(l);
                    proc.StandardInput.Close();
                    if (!proc.WaitForExit(60000))
                    {
                        try { proc.Kill(); } catch { }
                        throw new Xunit.Sdk.XunitException("Server did not terminate within 60 seconds.");
                    }
                    stderr.GetAwaiter().GetResult();
                    foreach (string raw in stdout.GetAwaiter().GetResult().Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0) continue;
                        JObject r;
                        try { r = JObject.Parse(line); } catch { continue; }
                        if ((string)r["id"] == "call-1") return (JObject)r["result"];
                    }
                }
                throw new Xunit.Sdk.XunitException("tools/call was never answered.");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void On_the_wire_an_undeclared_argument_is_refused_before_routing()
        {
            // No discovery file under this root: a call that got past the gate would answer
            // "no Revit is reachable" instead.
            JObject result = CallOverStdio("get_document_info", new JObject { ["operation"] = "context", ["dry_run"] = true });
            Assert.NotNull(result);
            Assert.True((bool)result["isError"]);
            string text = (string)result["content"][0]["text"];
            Assert.Contains("'dry_run'", text);
            Assert.Contains("horizun://contract/tools/get_document_info", text);
            Assert.DoesNotContain("reachable", text);
            Assert.Equal(ErrorCodes.EsquemaInvalido, (string)result["structuredContent"]?[ErrorCodes.CodeField]);
            Assert.Equal("dry_run", (string)result["structuredContent"]["undeclared_arguments"][0]);
            Assert.NotNull(result["structuredContent"]["schema_help"]);
        }

        [Fact]
        public void On_the_wire_a_declared_call_is_still_routed()
        {
            JObject result = CallOverStdio("get_document_info", new JObject { ["operation"] = "context", ["_meta"] = new JObject() });
            Assert.NotNull(result);
            string text = (string)result["content"][0]["text"];
            Assert.DoesNotContain("does not take the argument", text);
            Assert.Contains("reachable", text);
        }

        [Fact]
        public void On_the_wire_safe_aliases_are_normalized_before_schema_refusal()
        {
            JObject result = CallOverStdio("horizun_query_model", JObject.Parse("{ids:'123',limit:4,doc:'Example'}"));
            Assert.DoesNotContain("does not take the argument", result.ToString());
            Assert.DoesNotContain("'ids'", result.ToString());
            Assert.Contains("ids converted to element_ids", result.ToString());
            Assert.Contains("limit accepted as max_rows", result.ToString());
            Assert.Contains("doc accepted as target_document", result.ToString());
        }

        [Fact]
        public void On_the_wire_schema_rejection_emits_only_the_path_and_rule()
        {
            JObject result = CallOverStdio("horizun_query_model", JObject.Parse("{unexpected_field:'private-value'}"));
            JObject rejection = (JObject)result["structuredContent"]["schema_rejection"];
            Assert.Equal("/unexpected_field", (string)rejection["path"]);
            Assert.Equal("additional_property", (string)rejection["rule"]);
            Assert.DoesNotContain("private-value", result.ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}
