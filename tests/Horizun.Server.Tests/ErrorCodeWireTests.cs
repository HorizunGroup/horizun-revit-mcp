// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// STABLE ERROR CODES on the server side (Pro 3.4.0): the one seat where an error
// reply is assembled (McpResult), the transport's timeouts, horizun_health answering
// with a state in every situation, and horizun_execute_python refusing a script that
// does not compile BEFORE any transaction can open.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Horizun.Server;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ErrorCodeWireTests
    {
        private static string RepoRoot([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));

        private static string Text(JObject result) =>
            (string)((JArray)result["content"]).OfType<JObject>().First(b => (string)b["type"] == "text")["text"];

        // ---- McpResult: every failure leaves with a code ----------------------------------------

        [Fact]
        public void Section_box_operations_are_in_the_published_contract()
        {
            var schema = Horizun.Contracts.Contract.All.Single(x => x.Name == "horizun_view_crop").InputSchema;
            var ops = (JArray)schema["properties"]["operation"]["enum"];
            foreach (string name in new[] { "read_section_box", "set_section_box", "section_box_active" })
                Assert.Contains(ops, x => (string)x == name);
            Assert.Equal("boolean", (string)schema["properties"]["keep_transform"]["type"]);
            Assert.True((bool)schema["properties"]["dry_run"]["default"]);
        }

        [Fact]
        public void Unsupported_view_fallback_reports_consistent_prewrite_state()
        {
            var fallback = FallbackSignal.Allowed(FallbackSignal.ReasonUnsupportedKind).ToJson();
            var result = McpResult.ExplainFailure(McpResult.Error("Unsupported view. Nothing was written.", fallback, null), "horizun_view_crop");
            Assert.True((bool)result["structuredContent"]["fallback"]["allowed"]);
            Assert.False((bool)result["structuredContent"]["write_started"]);
            Assert.Contains("nothing was written", Text(result));
            Assert.DoesNotContain("whether anything was written is unknown", Text(result));
        }

        [Theory]
        [InlineData("Section box operations require an orthographic 3D view. For a plan, use set_crop_box.", "esquema_invalido")]
        [InlineData("view_id 85169 is a view template; a template has no crop of its own.", "precondicion_no_cumplida")]
        [InlineData("max must exceed min on all three axes.", "esquema_invalido")]
        public void Section_box_prewrite_refusals_explicitly_deny_fallback(string message, string code)
        {
            var refusal = CommandResult.RefuseBeforeWrite(message, message.StartsWith("max must") ? code : null);
            var result = McpResult.ExplainFailure(McpResult.Error(refusal.Error, null, null, refusal.Detail), "horizun_view_crop");
            Assert.Equal(code, (string)result["structuredContent"]["error_code"]);
            Assert.False((bool)result["structuredContent"]["write_started"]);
            Assert.False((bool)result["structuredContent"]["changes_applied"]);
            Assert.False((bool)result["structuredContent"]["fallback"]["allowed"]);
            Assert.False((bool)result["structuredContent"]["fallback"]["write_started"]);
            Assert.Equal(code, (string)result["structuredContent"]["fallback"]["reason"]);
            Assert.Contains("nothing was written", Text(result), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("whether anything was written is unknown", Text(result));
        }

        [Theory]
        [InlineData("Nothing was undone: the last Horizun batch could not be recorded as undoable.")]
        [InlineData("Rehearsal refused 1 of 2 rows. Nothing was applied; correct those rows.")]
        [InlineData("The view could not be read. Nothing was written.")]
        [InlineData("The rehearsal could not verify purge_legacy_marks. Nothing was committed.")]
        public void A_message_that_says_nothing_was_written_is_not_contradicted_by_the_trailer(string message)
        {
            var result = McpResult.ExplainFailure(McpResult.Text(message, true), "horizun_undo");
            Assert.DoesNotContain("whether anything was written is unknown", Text(result));
            Assert.Contains("nothing was written (as stated above)", Text(result));
        }

        [Fact]
        public void A_message_without_that_statement_keeps_the_unknown_trailer()
        {
            var result = McpResult.ExplainFailure(McpResult.Text("Revit stopped responding.", true), "horizun_undo");
            Assert.Contains("whether anything was written is unknown", Text(result));
        }

        private sealed class SilentBridgeException : Exception
        {
            private readonly string _message;
            public SilentBridgeException(string message, Exception inner = null) : base(null, inner) { _message = message; }
            public override string Message => _message;
        }

        [Fact]
        public void Confirmed_family_reload_rollback_is_not_reported_as_unknown()
        {
            var result = McpResult.Text("Family reload failed: Cannot restore 26 x 84: Type Comments. The transaction rolled back (Revit reported RolledBack), so nothing was changed.", true);
            result["structuredContent"] = new JObject { ["error_code"] = ErrorCodes.PrecondicionNoCumplida, ["rollback_status"] = "RolledBack", ["exception_type"] = "System.InvalidOperationException" };
            result = McpResult.ExplainFailure(result, "horizun_family_reload");
            Assert.Contains("nothing was changed", Text(result));
            Assert.DoesNotContain("whether anything was written is unknown", Text(result));
            Assert.DoesNotContain("unknown exception", Text(result));
            Assert.Equal(ErrorCodes.PrecondicionNoCumplida, (string)result["structuredContent"]["error_code"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Silent_bridge_exceptions_name_tool_type_and_unknown_write_state(string message)
        {
            Func<JObject> bridge = () => throw new SilentBridgeException(message);
            JObject result;
            try { result = bridge(); }
            catch (Exception ex) { result = McpResult.ExceptionError("horizun_transform_elements", ex, bridge: true); }
            Assert.Equal(ErrorCodes.TransporteFallido, (string)result["structuredContent"]["error_code"]);
            Assert.Contains("horizun_transform_elements", Text(result));
            Assert.Contains("SilentBridgeException", Text(result));
            Assert.Contains("(no message)", Text(result));
            Assert.Contains("unknown", Text(result));
        }

        [Fact]
        public void Nested_bridge_exception_preserves_both_causes_and_known_write_state()
        {
            Func<JObject> bridge = () => throw new SilentBridgeException("outer", new TimeoutException("inner"));
            JObject result;
            try { result = bridge(); }
            catch (Exception ex) { result = McpResult.ExceptionError("horizun_export", ex, new JObject { ["write_started"] = false }, true); }
            Assert.Contains("SilentBridgeException: outer", Text(result));
            Assert.Contains("TimeoutException: inner", Text(result));
            Assert.Contains("Writing did not start", Text(result));
            Assert.NotEqual(ErrorCodes.NoClasificado, (string)result["structuredContent"]["error_code"]);
        }

        [Fact]
        public void Host_exception_keeps_excel_classification_and_diagnostic()
        {
            var ex = new System.IO.FileNotFoundException("Workbook not found: sample.xlsx");
            var result = McpResult.ExceptionError("horizun_excel_read_rows", ex, HostFailure.Detail("horizun_excel_read_rows", ex, 1000));
            Assert.Equal(ErrorCodes.ArchivoNoAccesible, (string)result["structuredContent"]["error_code"]);
            Assert.Contains("FileNotFoundException", Text(result));
            Assert.Contains("Workbook not found", Text(result));
        }
        [Fact]
        public void Empty_failure_is_explained_even_without_an_exception()
        {
            JObject result = McpResult.EnsureFailureMessage(McpResult.Text("", true), "horizun_export");
            Assert.Contains("horizun_export", Text(result));
            Assert.Contains("unknown exception", Text(result));
            Assert.Contains("unknown", Text(result));
        }

        [Fact]
        public void Health_silent_failure_reports_the_tool_and_unknown_state()
        {
            JObject failure = McpResult.EnsureFailureMessage(McpResult.Text("Error: ", true), "horizun_health");
            JObject result = (JObject)HealthAnswer.Apply(failure);
            Assert.Contains("horizun_health", (string)result["structuredContent"]["last_error"]["message"]);
            Assert.Contains("unknown", (string)result["structuredContent"]["last_error"]["message"]);
        }

        [Fact]
        public void A_plain_text_failure_carries_its_code_as_data_and_as_a_last_line()
        {
            JObject r = McpResult.Text("Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?", true);
            Assert.True((bool)r["isError"]);
            Assert.Equal("revit_no_disponible", (string)r["structuredContent"]["error_code"]);
            // The original message is untouched and nothing is hidden when only structure is forwarded.
            Assert.StartsWith("Error: no Revit is reachable.", Text(r));
            Assert.Contains("error_code: revit_no_disponible", Text(r));
            Assert.Contains("no Revit is reachable", (string)r["structuredContent"]["error"]);
        }

        [Fact]
        public void A_success_is_byte_for_byte_what_it_was()
        {
            JObject ok = McpResult.Text("all good", false);
            Assert.False((bool)ok["isError"]);
            Assert.Null(ok["structuredContent"]);
            Assert.Equal("all good", Text(ok));
            JObject structured = McpResult.Structured(new JObject { ["a"] = 1 }, "{\"a\":1}");
            Assert.False((bool)structured["isError"]);
            Assert.Null(structured["structuredContent"]["error_code"]);
            Assert.DoesNotContain("error_code", Text(structured));
        }

        [Fact]
        public void The_code_the_addin_named_wins_and_its_diagnostics_stay()
        {
            var detail = new JObject
            {
                ["error_code"] = "transaccion_revertida",
                ["exception_type"] = "InvalidOperationException",
                ["write_started"] = true,
                ["rollback_status"] = "rolled_back"
            };
            JObject r = McpResult.Error("Error: the copy failed and was rolled back", null, null, detail);
            JObject s = (JObject)r["structuredContent"];
            Assert.Equal("transaccion_revertida", (string)s["error_code"]);
            Assert.Equal("InvalidOperationException", (string)s["exception_type"]);
            Assert.True((bool)s["write_started"]);
            Assert.Contains("error_code: transaccion_revertida (InvalidOperationException)", Text(r));
        }

        [Fact]
        public void Invalid_schedule_reference_keeps_its_code_candidates_and_transaction_state()
        {
            var detail = JObject.Parse("{error_code:'entrada_invalida',transaction_status:'not_started',errors:[{index:0,error_code:'entrada_invalida',error:\"no candidate matches 'Missing'. Candidates: 'Level 1' (311)\"}]}");
            JObject r = McpResult.Error("Error: Invalid actions; nothing ran: " + detail["errors"], null, null, detail);
            Assert.True((bool)r["isError"]);
            Assert.Equal("entrada_invalida", (string)r["structuredContent"]["error_code"]);
            Assert.Equal("not_started", (string)r["structuredContent"]["transaction_status"]);
            Assert.True(JToken.DeepEquals(detail["errors"], r["structuredContent"]["errors"]));
            Assert.Contains("error_code: entrada_invalida", Text(r));
            McpResult.StampError(r);
            Assert.Equal("entrada_invalida", (string)r["structuredContent"]["error_code"]);
        }

        [Fact]
        public void An_addin_code_without_a_message_carries_the_text_as_error()
        {
            // Live 5.2 (Revit 2025): manage_styles answered {error_code, answered_by, tool} and the cause lived only in the text.
            var detail = new JObject { ["error_code"] = "esquema_invalido", ["tool"] = "horizun_manage_styles" };
            JObject r = McpResult.Error("Error: 'X' is not a valid name for a new 'Rooms' scheme (it may already exist). Nothing was written.", null, null, detail);
            string error = (string)r["structuredContent"]["error"];
            Assert.Contains("it may already exist", error);
            Assert.DoesNotContain("error_code:", error);
            Assert.Equal("esquema_invalido", (string)r["structuredContent"]["error_code"]);
            string first = r.ToString();
            McpResult.StampError(r);
            Assert.Equal(first, r.ToString());
        }

        [Fact]
        public void A_structure_that_already_says_why_is_not_given_a_second_message()
        {
            var detail = new JObject { ["error_code"] = "entrada_invalida", ["message"] = "own words" };
            JObject r = McpResult.Error("Error: the text", null, null, detail);
            Assert.Null(r["structuredContent"]["error"]);
            Assert.Equal("own words", (string)r["structuredContent"]["message"]);
        }

        [Fact]
        public void An_error_from_an_older_addin_with_no_code_is_classified_here()
        {
            var detail = new JObject { ["code"] = "type_not_found", ["write_started"] = false };
            JObject r = McpResult.Error("Error: no type named 'X' in 'Y'", null, null, detail);
            Assert.Equal("elemento_no_encontrado", (string)r["structuredContent"]["error_code"]);
            Assert.Equal("type_not_found", (string)r["structuredContent"]["code"]);
        }

        [Fact]
        public void A_timeout_names_the_limit()
        {
            JObject r = McpResult.Text("Error talking to Revit: No reply from Revit within 30000 ms. The request was still waiting", true);
            Assert.Equal("tiempo_agotado", (string)r["structuredContent"]["error_code"]);
            Assert.Equal(30000, (long)r["structuredContent"]["limite_ms"]);
            Assert.Contains("limite_ms=30000", Text(r));
        }

        [Fact]
        public void An_image_that_could_not_be_attached_is_its_own_code()
        {
            JObject r = McpResult.ImageAttachmentError(new JObject { ["image_path"] = "x.png" }, "capture ok", "too big", null, null);
            Assert.True((bool)r["isError"]);
            Assert.Equal("adjunto_imagen_fallido", (string)r["structuredContent"]["error_code"]);
            Assert.Equal("x.png", (string)r["structuredContent"]["image_path"]);
        }

        [Fact]
        public void Stamping_is_idempotent()
        {
            JObject once = McpResult.Text("Error: pixel_size must be between 64 and 8192; got 10.", true);
            string first = once.ToString();
            McpResult.StampError(once);
            Assert.Equal(first, once.ToString());
            Assert.Equal(1, Text(once).Split(new[] { "error_code:" }, StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void Schema_advice_attached_later_upgrades_the_generic_code()
        {
            JObject r = McpResult.Text("Error: something nobody foresaw", true);
            Assert.Equal("error_no_clasificado", (string)r["structuredContent"]["error_code"]);
            ((JObject)r["structuredContent"])["schema_help"] = new JObject { ["tool"] = "x" };
            McpResult.StampError(r);
            Assert.Equal("esquema_invalido", (string)r["structuredContent"]["error_code"]);
            Assert.Equal("esquema_invalido", (string)r["structuredContent"]["fallback"]["reason"]);
            Assert.Contains("error_code: esquema_invalido", Text(r));
            Assert.DoesNotContain("error_no_clasificado", Text(r));
        }

        // ---- the transport's own timeouts ----------------------------------------------------------

        [Fact]
        public async Task Query_missing_document_detail_survives_pipe_and_mcp_response()
        {
            string name = "hrz-document-seam-" + Guid.NewGuid().ToString("N");
            var args = JObject.Parse("{\"target_document\":\"Missing\",\"response_mode\":\"summary\"}");
            using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var peer = Task.Run(async () =>
                {
                    await pipe.WaitForConnectionAsync();
                    string request;
                    using (var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 1024, true))
                        request = await reader.ReadLineAsync();
                    var sent = JObject.Parse(request);
                    Assert.Equal("horizun_query_model", (string)sent["command"]);
                    Assert.True(JToken.DeepEquals(args, sent["params"]));
                    // Execute the production add-in dispatch with the Revit doubles from Core.Tests.
                    string core = Path.Combine(AppContext.BaseDirectory, "Horizun.Core.Tests.dll");
                    if (!File.Exists(core))
                    {
                        string configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
                        core = Path.Combine(RepoRoot(), "tests", "Horizun.Core.Tests", "bin", configuration, "net8.0", "Horizun.Core.Tests.dll");
                    }
                    var assembly = System.Reflection.Assembly.LoadFrom(core);
                    Type docType = assembly.GetType("Autodesk.Revit.DB.Document", true);
                    object doc = Activator.CreateInstance(docType);
                    docType.GetProperty("Title").SetValue(doc, "Model A");
                    Type uidocType = assembly.GetType("Autodesk.Revit.UI.UIDocument", true);
                    object uidoc = Activator.CreateInstance(uidocType);
                    uidocType.GetProperty("Document").SetValue(uidoc, doc);
                    Type appType = assembly.GetType("Autodesk.Revit.UI.UIApplication", true);
                    object app = Activator.CreateInstance(appType);
                    appType.GetProperty("ActiveUIDocument").SetValue(app, uidoc);
                    Type commandType = assembly.GetType("Horizun.Core.Tests.LargeModelDispatchTests+FakeCommand", true);
                    object command = Activator.CreateInstance(commandType, true);
                    commandType.GetProperty("Name").SetValue(command, "horizun_query_model");
                    Type dispatch = assembly.GetType("Horizun.Revit.Core.LargeModelDispatch", true);
                    object refusal = dispatch.GetMethod("Execute", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(null, new object[] { app, command, args.ToString(Newtonsoft.Json.Formatting.None) });
                    string error = (string)refusal.GetType().GetProperty("Error").GetValue(refusal);
                    var detail = (JObject)refusal.GetType().GetProperty("Detail").GetValue(refusal);
                    var reply = new JObject { ["id"] = sent["id"], ["success"] = false,
                        ["error"] = error, ["detail"] = detail };
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(reply.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                    await pipe.WriteAsync(bytes, 0, bytes.Length);
                    await pipe.FlushAsync();
                });
                string discovery = Path.Combine(Path.GetTempPath(), "hrz-document-wire-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(discovery);
                Type pipeClient = null;
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                try
                {
                    var server = System.Reflection.Assembly.LoadFrom(Path.Combine(RepoRoot(), "src", "Horizun.Server", "bin", "Release", "net8.0", "horizun-mcp.dll"));
                    pipeClient = server.GetType("Horizun.Server.PipeClient", true);
                    pipeClient.GetProperty("DirectoryOverride", flags).SetValue(null, discovery);
                    pipeClient.GetProperty("LivenessProbe", flags).SetValue(null, (Func<int, bool>)(_ => true));
                    File.WriteAllText(Path.Combine(discovery, "revit-2026-123.json"), new JObject {
                        ["pid"] = 123, ["revit_year"] = "2026", ["pipe_name"] = name, ["auth_token"] = "t"
                    }.ToString());
                    Type program = server.GetType("Horizun.Server.Program", true);
                    var method = program.GetMethod("CallTool", flags, null, new[] { typeof(JObject), typeof(System.Threading.CancellationToken) }, null);
                    var call = new JObject { ["name"] = "horizun_query_model", ["arguments"] = args };
                    JObject result = (JObject)method.Invoke(null, new object[] { call, System.Threading.CancellationToken.None });
                    await peer;
                    Assert.Contains("Model A", Text(result));
                    Assert.Equal(new[] { "Model A" }, ((JArray)result["structuredContent"]["open_documents"]).Values<string>().ToArray());
                }
                finally
                {
                    if (pipeClient != null)
                    {
                        pipeClient.GetProperty("DirectoryOverride", flags).SetValue(null, null);
                        pipeClient.GetProperty("LivenessProbe", flags).SetValue(null, null);
                    }
                    try { Directory.Delete(discovery, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task A_reply_that_never_comes_is_tiempo_agotado_with_the_limit_in_the_detail()
        {
            string name = "hrz-timeout-code-" + Guid.NewGuid().ToString("N");
            using (var main = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var peer = Task.Run(async () =>
                {
                    await main.WaitForConnectionAsync();
                    using (var reader = new StreamReader(main, System.Text.Encoding.UTF8, false, 1024, true))
                        await reader.ReadLineAsync();   // the request is read, and never answered
                    await Task.Delay(4000);
                });
                var ex = Assert.Throws<TimeoutException>(() =>
                    PipeClient.Send(new Discovered { PipeName = name }, "horizun_capture_view", new JObject(), 700));
                JObject detail = (JObject)ex.Data["horizun_transport_detail"];
                Assert.Equal("tiempo_agotado", (string)detail["error_code"]);
                Assert.Equal(700, (long)detail["limite_ms"]);
                Assert.Equal("TimeoutException", (string)detail["exception_type"]);

                JObject mcp = McpResult.Error("Error talking to Revit: " + ex.Message, null, null, detail);
                Assert.Equal("tiempo_agotado", (string)mcp["structuredContent"]["error_code"]);
                Assert.Equal(700, (long)mcp["structuredContent"]["limite_ms"]);
                try { await peer; } catch { }
            }
        }

        [Fact]
        public void A_bridge_that_is_not_there_is_revit_no_disponible()
        {
            var ex = Assert.ThrowsAny<Exception>(() => PipeClient.Send(
                new Discovered { PipeName = "hrz-nobody-" + Guid.NewGuid().ToString("N") }, "horizun_copy_between_documents",
                new JObject(), 5000));
            JObject detail = (JObject)ex.Data["horizun_transport_detail"];
            Assert.Equal("revit_no_disponible", (string)detail["error_code"]);
            Assert.Equal(ErrorCodes.TypeNameOf(ex), (string)detail["exception_type"]);
        }

        // ---- horizun_health: always an answer, never an exception ------------------------------------

        private static JObject Failed(string text, JObject detail = null) => McpResult.Error(text, null, null, detail);

        private static JObject Health(JToken result) => (JObject)((JObject)result)["structuredContent"];

        [Fact]
        public void Health_with_no_revit_at_all_is_host_cerrado()
        {
            Func<bool> before = HealthAnswer.RevitProcessRunning;
            try
            {
                HealthAnswer.RevitProcessRunning = () => false;
                JToken answer = HealthAnswer.Apply(Failed("Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?"));
                Assert.False((bool)answer["isError"]);
                JObject h = Health(answer);
                Assert.Equal("host_cerrado", (string)h["estado"]);
                Assert.Equal("revit_no_disponible", (string)h["error_code"]);
                Assert.True((int)h["retry_after_ms"] > 0);
            }
            finally { HealthAnswer.RevitProcessRunning = before; }
        }

        [Fact]
        public void Health_with_failed_process_probe_does_not_claim_revit_is_closed()
        {
            Func<bool> before = HealthAnswer.RevitProcessRunning;
            try
            {
                HealthAnswer.RevitProcessRunning = () => throw new InvalidOperationException("probe failed");
                JObject h = Health(HealthAnswer.Apply(Failed("Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?")));
                Assert.Equal("puente_inaccesible", (string)h["estado"]);
                Assert.DoesNotContain("abra Revit", (string)h["que_hacer"]);
            }
            finally { HealthAnswer.RevitProcessRunning = before; }
        }

        [Fact]
        public void Health_with_a_revit_process_but_no_bridge_does_not_claim_it_is_starting()
        {
            Func<bool> before = HealthAnswer.RevitProcessRunning;
            try
            {
                HealthAnswer.RevitProcessRunning = () => true;
                JObject h = Health(HealthAnswer.Apply(Failed("Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?")));
                Assert.Equal("puente_inaccesible", (string)h["estado"]);
                Assert.Equal(Horizun.Contracts.Contract.Hash, (string)h["server"]["contract_hash"]);
                Assert.NotNull(h["instances"]);
                Assert.NotNull(h["last_error"]);
                Assert.Equal("this health request", (string)h["last_error"]["scope"]);
                Assert.Equal(JTokenType.Null, h["active_document"].Type);
                Assert.Equal("unknown", (string)h["modal_dialog_observation"]);
            }
            finally { HealthAnswer.RevitProcessRunning = before; }
        }

        [Fact]
        public void Health_that_times_out_or_meets_a_dialog_is_ocupado()
        {
            JObject timeout = Health(HealthAnswer.Apply(Failed("Error talking to Revit: No reply from Revit within 30000 ms.",
                new JObject { ["error_code"] = "tiempo_agotado", ["limite_ms"] = 30000 })));
            Assert.Equal("ocupado", (string)timeout["estado"]);
            Assert.Equal(30000, (long)timeout["limite_ms"]);
            Assert.Equal(10000, (int)timeout["retry_after_ms"]);

            JObject modal = Health(HealthAnswer.Apply(Failed("Error: Revit has a MODAL DIALOG open: New Project. 'horizun_health' was queued",
                new JObject { ["error_code"] = "revit_ocupado" })));
            Assert.Equal("ocupado", (string)modal["estado"]);
            Assert.Equal("revit_ocupado", (string)modal["error_code"]);
        }

        [Fact]
        public void Health_with_no_document_is_sin_documento_without_a_retry()
        {
            JObject fromError = Health(HealthAnswer.Apply(Failed("Error: No document is active in Revit 2026, so 'horizun_health' has nothing to act on.")));
            Assert.Equal("sin_documento", (string)fromError["estado"]);
            Assert.Null(fromError["retry_after_ms"]);
        }

        [Fact]
        public void Health_that_arrives_is_left_exactly_as_it_is()
        {
            var ok = McpResult.Structured(new JObject { ["revit_year"] = "2026", ["estado"] = "listo", ["no_active_document"] = false },
                                          "{...}");
            string before = ok.ToString();
            Assert.Equal(before, HealthAnswer.Apply(ok).ToString());
        }

        [Fact]
        public void An_exception_while_asking_for_health_still_becomes_a_state()
        {
            foreach (Exception boom in new Exception[]
                     { new TimeoutException("No reply from Revit within 30000 ms."), new IOException("Pipe closed before a reply arrived."),
                       new InvalidOperationException("who knows") })
            {
                JToken answer = HealthAnswer.FromException(boom);
                Assert.False((bool)answer["isError"]);
                JObject h = Health(answer);
                Assert.Contains((string)h["estado"], HealthState.All);
                Assert.True(ErrorCodes.IsStable((string)h["error_code"]));
                Assert.NotNull(h["retry_after_ms"]);
            }
            Assert.False(HealthAnswer.ShouldAnswer(new OperationCanceledException()));
            Assert.False(HealthAnswer.ShouldAnswer(new McpError(-32602, "bad params")));
            Assert.True(HealthAnswer.ShouldAnswer(new InvalidOperationException()));
        }

        [Fact]
        public void A_deliberate_refusal_of_health_stays_an_error()
        {
            // A quarantined call, a tool switched off, a withheld permission or arguments that
            // break the contract are decisions about the CALL - not an unreachable Revit - and
            // "wait and retry" would be the wrong advice for any of them.
            foreach (var detail in new[]
            {
                new JObject { ["code"] = "call_quarantined", ["error_code"] = "llamada_en_cuarentena" },
                new JObject { ["error_code"] = "herramienta_desactivada" },
                new JObject { ["error_code"] = "permiso_insuficiente" },
                new JObject { ["error_code"] = "esquema_invalido" }
            })
            {
                JObject refusal = Failed("Error: refused on purpose", detail);
                JToken answered = HealthAnswer.Apply(refusal);
                Assert.True((bool)answered["isError"], detail.ToString());
                Assert.Equal((string)detail["error_code"], (string)answered["structuredContent"]["error_code"]);
            }
        }

        [Fact]
        public void Only_a_plain_horizun_health_call_is_answered_this_way()
        {
            Assert.True(HealthAnswer.IsHealth(new JObject { ["name"] = "horizun_health" }));
            Assert.False(HealthAnswer.IsHealth(new JObject { ["name"] = "horizun_health", ["task"] = new JObject() }));
            Assert.False(HealthAnswer.IsHealth(new JObject { ["name"] = "horizun_save_document" }));
            Assert.False(HealthAnswer.IsHealth(null));
        }

        [Fact]
        public void The_server_routes_every_call_through_the_health_and_stamping_seats()
        {
            string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Server", "Program.cs"));
            int core = program.IndexOf("CallToolCore(prms, ct, observe, envelope, ref safety)", StringComparison.Ordinal);
            int health = program.IndexOf("HealthAnswer.Apply(result)", StringComparison.Ordinal);
            int stamp = program.IndexOf("McpResult.StampError(failed)", StringComparison.Ordinal);
            int finish = program.IndexOf("ContentSafety.Finish(result, safety)", StringComparison.Ordinal);
            Assert.True(core > 0 && health > core && stamp > health && finish > stamp,
                "CallTool must run the tool, then answer health with a state, then stamp the error code, then seal the content");
        }

        // ---- execute_python: the syntax is checked before anything can open a transaction -------------------

        private static ScriptEngine Engine() => Python.CreateEngine();

        [Fact]
        public void A_script_with_a_syntax_error_is_refused_by_the_precompile_and_runs_nothing()
        {
            ScriptEngine engine = Engine();
            ScriptPrecompile.Failure f = ScriptPrecompile.Check(engine, "x = 1\nif x ==\n    print(x)\n", "driver.py", null);
            Assert.NotNull(f);
            Assert.Equal("SyntaxError", f.ErrorType);
            Assert.Contains("driver.py", f.Formatted);
            JObject detail = ErrorCodes.ScriptFailureDetail(null, true, null, f.ErrorType, false);
            Assert.Equal("python_error_sintaxis", (string)detail["error_code"]);
        }

        [Fact]
        public void A_script_that_compiles_is_not_executed_by_the_precompile()
        {
            ScriptEngine engine = Engine();
            ScriptScope scope = engine.CreateScope();
            scope.SetVariable("touched", 0);
            // If Check ran the script, `touched` would change - and in Revit, the model would too.
            ScriptPrecompile.Failure f = ScriptPrecompile.Check(engine, "touched = 99\nraise ValueError('would run')\n", null, null);
            Assert.Null(f);
            Assert.Equal(0, scope.GetVariable<int>("touched"));
        }

        [Fact]
        public void A_syntax_error_in_an_include_is_found_before_the_main_script()
        {
            ScriptEngine engine = Engine();
            var includes = new[] { new KeyValuePair<string, string>("def broken(:\n  pass\n", "lib.py") };
            ScriptPrecompile.Failure f = ScriptPrecompile.Check(engine, "print('fine')", "main.py", includes);
            Assert.NotNull(f);
            Assert.Contains("lib.py", f.Formatted);
        }

        [Fact]
        public void The_command_compiles_before_it_starts_a_transaction_group_or_runs_the_prelude()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "ExecutePythonCommand.cs"));
            int precompile = source.IndexOf("ScriptPrecompile.Failure compile = ScriptPrecompile.Check(", StringComparison.Ordinal);
            Assert.True(precompile > 0, "ExecutePythonCommand must call ScriptPrecompile.Check");
            int group = source.IndexOf("new TransactionGroup(", StringComparison.Ordinal);
            int prelude = source.IndexOf("engine.Execute(ScriptPrelude.Prologue", StringComparison.Ordinal);
            int job = source.IndexOf("Job.StartBestEffort(", StringComparison.Ordinal);
            int scope = source.IndexOf("engine.CreateScope()", StringComparison.Ordinal);
            Assert.True(precompile < scope, "the scope (and so uidoc/doc) must not exist before the script compiles");
            Assert.True(precompile < group, "the read-only TransactionGroup must not start before the script compiles");
            Assert.True(precompile < prelude, "the prelude must not run before the script compiles");
            Assert.True(precompile < job, "no job record may open before the script compiles");
            // And the refusal is a failure with the stable code, not an exception.
            string slice = source.Substring(precompile, Math.Min(1800, source.Length - precompile));
            Assert.Contains("ErrorCodes.ScriptFailureDetail(", slice);
            Assert.Contains("return CommandResult.FailWithDetail(", slice);
        }

        [Fact]
        public void The_command_tells_the_script_the_api_and_the_rollback_apart()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "ExecutePythonCommand.cs"));
            Assert.Contains("RevitApiTypeOf(scriptException)", source);
            Assert.Contains("ErrorCodes.PythonTypeFromTraceback(error)", source);
            Assert.Contains("ErrorCodes.ScriptFailureDetail(raised,", source);
            Assert.Contains("ErrorCodes.PermisoPythonRequerido", source);
        }
    }
}
