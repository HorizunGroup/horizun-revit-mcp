// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// STABLE ERROR CODES (Pro 3.4.0). Field data: 169 errors in 1,588 calls and 79 % of
// them with no identifiable cause, because the only evidence was free text. These
// tests pin the property the fix promises:
//
//   * every failure that leaves the add-in carries a stable `error_code` in its
//     detail - whatever built it (Fail, FailWithDetail, FailCoded, the bare envelope);
//   * the code is a fixed snake_case word from a documented catalog, with the bare
//     exception type NAME and the limit in ms where they apply, and never project data;
//   * `error_no_clasificado` is the exception: a corpus test measures it over every
//     failure message in the commands;
//   * the catalog in ErrorCodes is the catalog in the README, code for code.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Core.Tests
{
    public class ErrorCodeTests
    {
        private readonly ITestOutputHelper _out;
        public ErrorCodeTests(ITestOutputHelper output) { _out = output; }

        private static string RepoRoot([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));

        // The minimum catalog required. Extra codes are allowed; these may never go away.
        private static readonly string[] Minimum =
        {
            "revit_no_disponible", "revit_sin_documento", "revit_ocupado", "documento_objetivo_ambiguo",
            "python_error_sintaxis", "python_excepcion", "api_revit_excepcion", "transaccion_revertida",
            "elemento_no_encontrado", "esquema_invalido", "confirmacion_requerida", "token_invalido_o_vencido",
            "tiempo_agotado", "permiso_python_requerido"
        };

        // ---- the catalog ------------------------------------------------------------

        [Fact]
        public void The_catalog_holds_the_minimum_and_every_code_is_snake_case()
        {
            foreach (string code in Minimum) Assert.Contains(code, ErrorCodes.Catalog);
            foreach (string code in ErrorCodes.Catalog)
                Assert.Matches("^[a-z][a-z0-9_]*$", code);
            Assert.Equal(ErrorCodes.Catalog.Length, ErrorCodes.Catalog.Distinct().Count());
            Assert.Contains(ErrorCodes.NoClasificado, ErrorCodes.Catalog);
        }

        [Fact]
        public void Every_code_constant_is_in_the_catalog()
        {
            var constants = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue())
                .Where(v => v != ErrorCodes.CodeField && v != ErrorCodes.ExceptionTypeField && v != ErrorCodes.LimitField)
                .ToList();
            foreach (string c in constants) Assert.True(ErrorCodes.IsStable(c), c + " is a code constant missing from Catalog");
            foreach (string c in ErrorCodes.Catalog) Assert.Contains(c, constants);
        }

        [Fact]
        public void The_readme_documents_every_code_with_what_it_means_and_what_to_do()
        {
            string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
            string spanish = File.ReadAllText(Path.Combine(RepoRoot(), "README.es.md"));
            foreach (string code in ErrorCodes.Catalog)
            {
                // A table row: | `code` | meaning | what to do |
                Assert.Matches("\\|\\s*`" + code + "`\\s*\\|[^|\\n]+\\|[^|\\n]+\\|", readme);
                Assert.Matches("\\|\\s*`" + code + "`\\s*\\|[^|\\n]+\\|[^|\\n]+\\|", spanish);
            }
            foreach (string state in HealthState.All)
            {
                Assert.Contains("`" + state + "`", readme);
                Assert.Contains("`" + state + "`", spanish);
            }
        }

        [Theory]
        [InlineData("System.InvalidOperationException", "InvalidOperationException")]
        [InlineData("Autodesk.Revit.Exceptions.ArgumentException", "ArgumentException")]
        [InlineData("InvalidOperationException", "InvalidOperationException")]
        [InlineData("Outer+Inner", "Inner")]
        [InlineData("System.Collections.Generic.List`1", "List")]
        public void Exception_types_are_reduced_to_the_bare_name(string given, string expected)
            => Assert.Equal(expected, ErrorCodes.ShortTypeName(given));

        [Theory]
        [InlineData("a message, not a type")]
        [InlineData("C:\\Projects\\Secret Tower\\model.rvt")]
        [InlineData("")]
        [InlineData(null)]
        public void Something_that_is_not_a_type_name_is_never_republished_as_one(string given)
            => Assert.Null(ErrorCodes.ShortTypeName(given));

        // ---- the routes: one row per way a call can fail ---------------------------------
        // (route, message, detail json or null, expected code, expected exception_type, expected limite_ms)

        public static IEnumerable<object[]> Routes()
        {
            yield return R("cancelled_operation", "Operation cancelled by the user.", null, "cancelado");
            yield return R("annotation_budget", "Annotation work exceeded its budget.",
                "{\"error_code\":\"annotation_budget_exhausted\",\"budget_ms\":120000,\"rows_attempted\":5}", "annotation_budget_exhausted");
            yield return R("missing_active_view", "No active view is available.", null, "sin_vista");
            yield return R("inactive_document", "The target document is not the active document.", null, "documento_no_activo");
            yield return R("schedule_invalid_reference", "Invalid actions; nothing ran",
                "{\"error_code\":\"entrada_invalida\",\"transaction_status\":\"not_started\"}", "entrada_invalida");
            // The minimum catalog.
            yield return R("no_revit", "Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?", null, "revit_no_disponible");
            yield return R("no_revit_pinned_year", "Error: no Revit 2026 is reachable. That target was chosen explicitly (horizun_target)", null, "revit_no_disponible");
            yield return R("no_revit_pinned_year_5_0_3", "Error: no Revit is reachable for Revit 2026. That target was chosen explicitly (horizun_target)", null, "revit_no_disponible");
            yield return R("no_revit_closed", "Error: no Revit is reachable. No Revit process is running: Revit is CLOSED. Ask the person to open Revit with a model", null, "revit_no_disponible");
            yield return R("no_revit_running_without_bridge", "Error: no Revit is reachable. Revit IS running (Revit 2025 pid 47080) but has not published a bridge", null, "revit_no_disponible");
            yield return R("stale_discovery", "Error: the Revit 2026 that published this bridge (process 123) is no longer running, so there is nothing to talk to.", null, "revit_no_disponible");
            yield return R("connect_failed", "Could not connect to Revit's bridge within 5000 ms. Nothing was sent.", null, "revit_no_disponible");
            yield return R("pipe_closed", "Pipe closed before a reply arrived.", null, "revit_no_disponible");
            yield return R("revit_shutting_down", "Revit is shutting down. 'horizun_x' was not queued and NEVER RAN", null, "revit_no_disponible");
            yield return R("pinned_pid_missing", "Error: No Revit with process id 99 has published a bridge. Call horizun_target with no arguments to see the ones that have.", null, "revit_no_disponible");
            yield return R("no_document", "No document is active in Revit 2026, so 'horizun_save_document' has nothing to act on. Open the model you mean first.", null, "revit_sin_documento");
            yield return R("no_document_capture", "No document is active.", null, "revit_sin_documento");
            yield return R("modal_dialog", "Revit has a MODAL DIALOG open: New Project. 'horizun_health' was queued but Revit does not service the bridge until the dialog is answered", null, "revit_ocupado");
            yield return R("modal_in_detail", "something", "{\"modal_dialog\":{\"title\":\"x\"}}", "revit_ocupado");
            yield return R("target_required", "'target_document' is required for 'horizun_save_document'. A command that CHANGES a model must name the model", null, "documento_objetivo_ambiguo");
            yield return R("target_more_than_one", "target_document 'Tower' matches MORE THAN ONE open document and nothing says which", null, "documento_objetivo_ambiguo");
            yield return R("target_no_match", "No open document matches target_document 'Tower'. Open documents: A, B", null, "documento_objetivo_ambiguo");
            yield return R("two_revits", "Error: 2 Revit instances are running with the bridge loaded (Revit 2025 pid 1, Revit 2026 pid 2) and nothing says which one you mean. Refusing to pick", null, "documento_objetivo_ambiguo");
            yield return R("python_syntax", "The script does not compile", "{\"error_code\":\"python_error_sintaxis\",\"exception_type\":\"SyntaxError\"}", "python_error_sintaxis", "SyntaxError");
            yield return R("python_syntax_text", "Traceback ... SyntaxError: invalid syntax", null, "python_error_sintaxis");
            yield return R("python_exception_legacy", "ValueError: bad value", "{\"code\":\"python_execution_failed\",\"exception_type\":\"ValueError\"}", "python_excepcion", "ValueError");
            yield return R("api_exception_export", "Revit refused to export 'Plan 1': The view cannot be exported", "{\"error_code\":\"api_revit_excepcion\",\"exception_type\":\"Autodesk.Revit.Exceptions.InvalidOperationException\"}", "api_revit_excepcion", "InvalidOperationException");
            yield return R("api_exception_text", "Revit refused to open 'x.rvt' in the background: Autodesk.Revit.Exceptions.CorruptModelException", null, "api_revit_excepcion", "CorruptModelException");
            yield return R("unhandled_revit_exception", "ArgumentException: value", "{\"code\":\"unhandled_command_exception\",\"exception_type\":\"Autodesk.Revit.Exceptions.ArgumentException\"}", "api_revit_excepcion", "ArgumentException");
            yield return R("rolled_back", "The copy failed and was rolled back: Revit said no", null, "transaccion_revertida");
            yield return R("rolled_back_detail", "plan failed", "{\"rollback_status\":\"rolled_back\"}", "transaccion_revertida");
            yield return R("not_found_view", "No view named 'Level 9' in this document.", null, "elemento_no_encontrado");
            yield return R("not_found_type", "no type named 'X' in 'Y'. type_names matches EXACTLY", "{\"code\":\"type_not_found\"}", "elemento_no_encontrado");
            yield return R("not_a_view", "Element 12 is not a view (or does not exist).", null, "elemento_no_encontrado");
            yield return R("schema_range", "pixel_size must be between 64 and 8192; got 10.", null, "esquema_invalido");
            yield return R("schema_json", "Parameters must be a JSON object: Unexpected character", null, "esquema_invalido");
            yield return R("schema_unknown_arg", "Unknown capture argument: foo", null, "esquema_invalido");
            yield return R("schema_both", "Specify view_id or view_name, not both.", null, "esquema_invalido");
            yield return R("schema_spec", "bad spec", "{\"code\":\"invalid_spec\"}", "esquema_invalido");
            yield return R("confirmation_required", "confirmation_token from this batch's dry run is required.", null, "confirmacion_requerida");
            yield return R("confirmation_required_detail", "Nothing was changed.", "{\"error_code\":\"confirmacion_requerida\",\"state\":\"refused\"}", "confirmacion_requerida");
            yield return R("token_unknown", "No such confirmation token. Run this command with dry_run=true first", null, "token_invalido_o_vencido");
            yield return R("token_expired", "That confirmation expired (issued 2026-10-01, valid for 10 min).", null, "token_invalido_o_vencido");
            yield return R("token_used", "That confirmation was already used. One approval authorises one execution", null, "token_invalido_o_vencido");
            yield return R("token_stale_plan", "THE MODEL MOVED AFTER THE DRY RUN. The request is identical", null, "token_invalido_o_vencido");
            yield return R("token_other_plan", "THIS REQUEST IS NOT THE ONE THAT WAS REHEARSED.", null, "token_invalido_o_vencido");
            yield return R("bridge_auth", "Auth failed: token missing or wrong.", null, "token_invalido_o_vencido");
            yield return R("timeout_dispatcher", "'horizun_audit_model' timed out after 600000 ms. Revit may be busy or waiting on a modal dialog.", null, "tiempo_agotado", null, 600000);
            yield return R("timeout_no_reply", "No reply from Revit within 30000 ms. The request was still waiting in Revit's FIFO queue", null, "tiempo_agotado", null, 30000);
            yield return R("timeout_write", "Revit's bridge did not read the request within 35000 ms, so the write was abandoned.", null, "tiempo_agotado", null, 35000);
            yield return R("timeout_host", "Host-resident tool 'horizun_excel_read_rows' exceeded its 600000 ms deadline.", null, "tiempo_agotado", null, 600000);
            yield return R("timeout_transport_detail", "Error talking to Revit", "{\"code\":\"revit_transport_failed\",\"exception_type\":\"System.TimeoutException\"}", "tiempo_agotado", "TimeoutException");
            yield return R("python_disabled", "horizun_execute_python is DISABLED ON THIS MACHINE. This is the safe default", null, "permiso_python_requerido");
            yield return R("python_permission", "horizun_execute_python requires explicit permission_profile=unsafe_code and enable_execute_python=true", null, "permiso_python_requerido");

            // Beyond the minimum.
            yield return R("capture_nothing", "ExportImage returned without error but produced no file in the temp folder. Nothing was captured", "{\"error_code\":\"captura_sin_resultado\"}", "captura_sin_resultado");
            yield return R("image_not_attached", "The capture was made but could not be attached", "{\"error_code\":\"adjunto_imagen_fallido\"}", "adjunto_imagen_fallido");
            yield return R("precondition", "Output already exists and overwrite=false: report.pdf", null, "precondicion_no_cumplida");
            yield return R("verification", "The transaction committed, but only 2 of 3 actions passed post-commit verification.", null, "verificacion_fallida");
            yield return R("file_unreadable", "Cannot read table file: table.csv", null, "archivo_no_accesible");
            yield return R("group_member", "miembro_de_grupo: 765199 (move) is a member of group 765185. Nothing was written.", "{\"error_code\":\"miembro_de_grupo\"}", "miembro_de_grupo");
            yield return R("documentation_only", "documentation_only=true REFUSED the result: the script changed 1 element(s) outside views.", "{\"error_code\":\"documentacion_violada\"}", "documentacion_violada");
            yield return R("family_newer", "family_saved_in_newer_version: 'Toma doble.rfa' was saved in a NEWER Revit (2025) than this one (2023). Nothing was opened or loaded.", null, "familia_version_mas_nueva");
            yield return R("tool_switched_off", "Error: tool disabled", "{\"error_code\":\"herramienta_desactivada\"}", "herramienta_desactivada");
            yield return R("contract_hash", "Error: this server and the Horizun add-in loaded in Revit 2026 were built from DIFFERENT command contracts", null, "contrato_incompatible");
            yield return R("wire_protocol", "Error: the add-in in Revit 2026 speaks wire protocol 3 and this server speaks 4.", null, "contrato_incompatible");
            yield return R("command_missing", "Error: this server offers 'x', but the Horizun add-in loaded in Revit 2026 does not have the 'y' command", null, "contrato_incompatible");
            yield return R("unknown_command", "Unknown command: 'horizun_nope'.", null, "contrato_incompatible");
            yield return R("permission_profile", "permission_profile=read_only in settings.json does not authorize writing outside the document", null, "permiso_insuficiente");
            yield return R("quarantine", "refused", "{\"code\":\"call_quarantined\"}", "llamada_en_cuarentena");
            yield return R("reply_too_big", "Error talking to Revit: reply was over the byte limit", "{\"code\":\"revit_transport_failed\",\"exception_type\":\"System.IO.IOException\"}", "respuesta_demasiado_grande", "IOException");
            yield return R("transport_other", "Error talking to Revit: boom", "{\"code\":\"revit_transport_failed\",\"exception_type\":\"System.IO.IOException\"}", "transporte_fallido", "IOException");
            yield return R("internal_exception", "NullReferenceException: x", "{\"code\":\"unhandled_command_exception\",\"exception_type\":\"System.NullReferenceException\"}", "error_interno", "NullReferenceException");
            yield return R("nothing_matches", "Something nobody foresaw happened.", null, "error_no_clasificado");
            yield return R("family_document_open", "Close the family in Revit, saving it first if it has changes, or pass a copy. Nothing was written.", "{\"error_code\":\"family_document_open\"}", "family_document_open");
        }

        private static object[] R(string route, string message, string detail, string code, string type = null, long? limit = null)
            => new object[] { route, message, detail, code, type, limit };

        [Theory]
        [MemberData(nameof(Routes))]
        public void Every_route_resolves_to_its_code(string route, string message, string detailJson,
                                                     string expectedCode, string expectedType, long? expectedLimit)
        {
            JObject detail = detailJson == null ? null : JObject.Parse(detailJson);
            JObject stamped = ErrorCodes.Ensure(message, detail);

            Assert.True(expectedCode == (string)stamped["error_code"], route + ": got " + stamped["error_code"]);
            Assert.True(ErrorCodes.IsStable((string)stamped["error_code"]), route);
            if (expectedType != null) Assert.Equal(expectedType, (string)stamped["exception_type"]);
            if (expectedLimit != null) Assert.Equal(expectedLimit.Value, (long)stamped["limite_ms"]);
        }

        [Fact]
        public void The_routes_cover_every_catalog_code()
        {
            var covered = new HashSet<string>(Routes().Select(r => (string)r[3]));
            foreach (string code in ErrorCodes.Catalog)
                Assert.True(covered.Contains(code), code + " has no route in the table");
        }

        // ---- the add-in's own envelope: the one seat every failure leaves through -----------

        [Fact]
        public void A_plain_Fail_leaves_the_envelope_with_a_code()
        {
            JObject reply = PipeEnvelope.Of("1", CommandResult.Fail("No view named 'Level 9' in this document."));
            Assert.False((bool)reply["success"]);
            Assert.Equal("elemento_no_encontrado", (string)reply["detail"]["error_code"]);
        }

        [Fact]
        public void A_failure_with_a_detail_keeps_its_diagnostics_and_gains_the_code()
        {
            var detail = new JObject { ["state"] = "rehearsed", ["code"] = "copy_failed", ["write_started"] = false };
            JObject reply = PipeEnvelope.Of("1", CommandResult.FailWithDetail("the rehearsed copy failed and was rolled back: x", detail));
            Assert.Equal("transaccion_revertida", (string)reply["detail"]["error_code"]);
            Assert.Equal("copy_failed", (string)reply["detail"]["code"]);
            Assert.False((bool)reply["detail"]["write_started"]);
            // The command's own object is not mutated.
            Assert.Null(detail["error_code"]);
        }

        [Fact]
        public void A_code_the_command_named_is_never_overridden_by_the_text()
        {
            // The text says "timed out", the command knows better.
            CommandResult r = CommandResult.FailCoded(ErrorCodes.RevitOcupado, "x timed out after 600000 ms", extra: new JObject { ["limite_ms"] = 600000 });
            JObject reply = PipeEnvelope.Of("1", r);
            Assert.Equal("revit_ocupado", (string)reply["detail"]["error_code"]);
            Assert.Equal(600000, (long)reply["detail"]["limite_ms"]);
        }

        [Fact]
        public void Invalid_schedule_reference_keeps_its_code_and_candidates_in_the_envelope()
        {
            var errors = JArray.Parse("[{index:0,error_code:'entrada_invalida',error:\"no candidate matches 'Missing'. Candidates: 'Level 1' (311)\"}]");
            var result = CommandResult.FailCoded("entrada_invalida", "Invalid actions; nothing ran: " + errors,
                extra: new JObject { ["errors"] = errors, ["transaction_status"] = "not_started" });
            JObject reply = PipeEnvelope.Of("1", result);
            Assert.False((bool)reply["success"]);
            Assert.Equal("entrada_invalida", (string)reply["detail"]["error_code"]);
            Assert.Equal("not_started", (string)reply["detail"]["transaction_status"]);
            Assert.True(JToken.DeepEquals(errors, reply["detail"]["errors"]));
        }

        [Fact]
        public void The_bare_envelope_failures_carry_a_code_too()
        {
            Assert.Equal("token_invalido_o_vencido", (string)PipeEnvelope.Of("1", false, null, "Auth failed: token missing or wrong.")["detail"]["error_code"]);
            Assert.Equal("esquema_invalido", (string)PipeEnvelope.Of("1", false, null, "Malformed request: nope")["detail"]["error_code"]);
            Assert.Equal("esquema_invalido", (string)PipeEnvelope.Of("1", false, null, "No command given.")["detail"]["error_code"]);
            // A success carries nothing of the kind.
            Assert.Null(PipeEnvelope.Of("1", CommandResult.Ok(new { a = 1 }))["detail"]);
        }

        [Fact]
        public void A_qualified_exception_type_in_a_detail_is_reduced_to_the_bare_name()
        {
            var detail = new JObject { ["code"] = "unhandled_command_exception", ["exception_type"] = "Autodesk.Revit.Exceptions.InvalidOperationException" };
            JObject reply = PipeEnvelope.Of("1", CommandResult.FailWithDetail("InvalidOperationException: x", detail));
            Assert.Equal("InvalidOperationException", (string)reply["detail"]["exception_type"]);
            Assert.Equal("api_revit_excepcion", (string)reply["detail"]["error_code"]);
        }

        [Fact]
        public void The_code_never_carries_project_data()
        {
            string secret = "Torre Secreta 14 - C:\\Clientes\\Oculto\\modelo.rvt";
            JObject stamped = ErrorCodes.Ensure("No view named '" + secret + "' in this document.", null);
            string wire = stamped.ToString();
            Assert.DoesNotContain("Torre", wire);
            Assert.DoesNotContain("Oculto", wire);
            Assert.Equal("elemento_no_encontrado", (string)stamped["error_code"]);
        }

        [Fact]
        public void Classify_never_throws_on_hostile_input()
        {
            Assert.Equal("error_no_clasificado", ErrorCodes.Classify(null, null).Code);
            Assert.Equal("error_no_clasificado", ErrorCodes.Classify("", new JObject()).Code);
            var weird = new JObject { ["error_code"] = new JObject(), ["code"] = 7, ["exception_type"] = 3 };
            Assert.True(ErrorCodes.IsStable(ErrorCodes.Classify("x", weird).Code));
            // A code that is not in the catalog is not trusted.
            Assert.Equal("error_no_clasificado", (string)ErrorCodes.Ensure("zzz", new JObject { ["error_code"] = "inventado" })["error_code"]);
        }

        // ---- execute_python: the four ways a script ends badly --------------------------------

        [Fact]
        public void A_script_that_does_not_compile_is_python_error_sintaxis()
        {
            JObject d = ErrorCodes.ScriptFailureDetail(new JObject { ["code"] = "python_syntax_error" }, true, null, "SyntaxError", false);
            Assert.Equal("python_error_sintaxis", (string)d["error_code"]);
            Assert.Equal("SyntaxError", (string)d["exception_type"]);
            Assert.Equal("python_syntax_error", (string)d["code"]);
        }

        [Fact]
        public void An_exception_of_the_script_is_python_excepcion_with_its_type()
        {
            JObject d = ErrorCodes.ScriptFailureDetail(null, false, null, "ValueError", false);
            Assert.Equal("python_excepcion", (string)d["error_code"]);
            Assert.Equal("ValueError", (string)d["exception_type"]);
        }

        [Fact]
        public void An_exception_of_the_revit_api_is_api_revit_excepcion_with_its_type()
        {
            JObject d = ErrorCodes.ScriptFailureDetail(null, false, "InvalidOperationException", "InvalidOperationException", false);
            Assert.Equal("api_revit_excepcion", (string)d["error_code"]);
            Assert.Equal("InvalidOperationException", (string)d["exception_type"]);
        }

        [Fact]
        public void A_transaction_revit_threw_away_is_transaccion_revertida_and_keeps_the_cause()
        {
            JObject d = ErrorCodes.ScriptFailureDetail(null, false, "ArgumentException", "ArgumentException", true);
            Assert.Equal("transaccion_revertida", (string)d["error_code"]);
            Assert.Equal("api_revit_excepcion", (string)d["causa_error_code"]);
            Assert.Equal("ArgumentException", (string)d["exception_type"]);
            JObject silent = ErrorCodes.ScriptFailureDetail(null, false, null, null, true);
            Assert.Equal("transaccion_revertida", (string)silent["error_code"]);
            Assert.Equal("python_excepcion", (string)silent["causa_error_code"]);
        }

        [Theory]
        [InlineData("Traceback (most recent call last):\n  File \"<string>\", line 3, in <module>\nValueError: bad value", "ValueError")]
        [InlineData("Traceback (most recent call last):\r\n  File \"x.py\", line 9, in <module>\r\nNameError: name 'q' is not defined\r\n", "NameError")]
        [InlineData("Autodesk.Revit.Exceptions.InvalidOperationException: nope", "InvalidOperationException")]
        [InlineData("garbled !! output", null)]
        public void The_python_exception_type_is_read_from_the_last_line_of_the_traceback(string traceback, string expected)
            => Assert.Equal(expected, ErrorCodes.PythonTypeFromTraceback(traceback));

        // ---- health: every state, with the thresholds ----------------------------------------

        [Fact]
        public void A_health_payload_that_arrived_is_listo_or_sin_documento_and_changes_nothing_else()
        {
            var ready = new JObject { ["revit_year"] = "2026", ["no_active_document"] = false, ["pid"] = 12 };
            HealthState.StampSuccess(ready);
            Assert.Equal("listo", (string)ready["estado"]);
            Assert.Equal("2026", (string)ready["revit_year"]);
            Assert.Equal(12, (int)ready["pid"]);
            Assert.Null(ready["retry_after_ms"]);

            var empty = new JObject { ["revit_year"] = "2026", ["no_active_document"] = true };
            HealthState.StampSuccess(empty);
            Assert.Equal("sin_documento", (string)empty["estado"]);
            Assert.Null(empty["retry_after_ms"]);
        }

        [Theory]
        [InlineData("Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?", true, "puente_inaccesible", "revit_no_disponible", 15000)]
        [InlineData("Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?", false, "host_cerrado", "revit_no_disponible", 15000)]
        [InlineData("Could not connect to Revit's bridge within 5000 ms. Nothing was sent.", true, "puente_inaccesible", "revit_no_disponible", 15000)]
        [InlineData("No reply from Revit within 30000 ms. The request was still waiting", true, "ocupado", "tiempo_agotado", 10000)]
        [InlineData("'horizun_health' timed out after 30000 ms. Revit may be busy", false, "ocupado", "tiempo_agotado", 10000)]
        [InlineData("Revit has a MODAL DIALOG open: New Project. 'horizun_health' was queued", true, "ocupado", "revit_ocupado", 10000)]
        [InlineData("Error: this server and the Horizun add-in loaded in Revit 2026 were built from DIFFERENT command contracts", true, "puente_inaccesible", "contrato_incompatible", 15000)]
        [InlineData("Error: something strange", false, "puente_inaccesible", "error_no_clasificado", 15000)]
        public void A_health_call_that_failed_becomes_a_state_with_a_retry(string text, bool revitRunning,
                                                                          string estado, string code, int retry)
        {
            JObject body = HealthState.Unavailable(text, null, revitRunning);
            Assert.Equal(estado, (string)body["estado"]);
            Assert.Contains(estado, HealthState.All);
            Assert.Equal(code, (string)body["error_code"]);
            Assert.Equal(retry, (int)body["retry_after_ms"]);
            Assert.False((bool)body["ready"]);
            Assert.False(string.IsNullOrWhiteSpace((string)body["que_hacer"]));
        }

        [Fact]
        public void No_document_is_a_state_that_needs_a_person_not_a_retry()
        {
            JObject body = HealthState.Unavailable("No document is active in Revit 2026, so 'x' has nothing to act on.", null, true);
            Assert.Equal("sin_documento", (string)body["estado"]);
            Assert.Null(body["retry_after_ms"]);
        }

        [Fact]
        public void A_timeout_in_health_keeps_the_limit()
        {
            JObject body = HealthState.Unavailable("No reply from Revit within 30000 ms.", null, true);
            Assert.Equal(30000, (long)body["limite_ms"]);
        }

        // ---- the measure: how often does the generic code win? --------------------------------

        [Fact]
        public void The_generic_code_is_the_exception_over_every_failure_message_in_the_commands()
        {
            string commands = Path.Combine(RepoRoot(), "src", "Horizun.Revit");
            int total = 0, coded = 0;
            var unclassified = new List<string>();
            var byCode = new SortedDictionary<string, int>();
            foreach (var m in FailureCorpus.Messages(commands))
            {
                total++;
                string code = ErrorCodes.Classify(m.Value, null).Code;
                byCode[code] = (byCode.TryGetValue(code, out int n) ? n : 0) + 1;
                if (code != ErrorCodes.NoClasificado) coded++;
                else unclassified.Add(m.Key + ": " + m.Value.Substring(0, Math.Min(160, m.Value.Length)));
            }
            _out.WriteLine("failure messages: " + total + ", classified by text: " + coded + " (" + (100.0 * coded / Math.Max(1, total)).ToString("0.0") + " %)");
            foreach (var kv in byCode.OrderByDescending(k => k.Value)) _out.WriteLine("  " + kv.Value.ToString().PadLeft(5) + "  " + kv.Key);
            string dump = Environment.GetEnvironmentVariable("HORIZUN_DUMP_UNCLASSIFIED");
            if (!string.IsNullOrEmpty(dump)) File.WriteAllLines(dump, unclassified);
            Assert.True(total > 800, "the scan found only " + total + " messages; the scan is broken");
            // Community edition: the Pro commands carried many of the well-classified messages, so the share sits near 92.8 %.
            Assert.True(coded >= 0.92 * total,
                "only " + coded + " of " + total + " failure messages classify by text; the generic code must stay the exception");
        }
    }
}
