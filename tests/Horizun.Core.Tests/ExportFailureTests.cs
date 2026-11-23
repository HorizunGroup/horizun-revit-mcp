using System;
using System.IO;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExportFailureTests
    {
        [Theory]
        [InlineData("format must be pdf, pdf_qa, dwg, dgn, dwfx, ifc, nwc, fbx, image, schedule_csv, dwg_layers, ifc_setup, gbxml, rfa or cobie. use format=image.", "esquema_invalido", "arguments")]
        [InlineData("Every view_id must identify a non-template view in the active document; failed at 42", "elemento_no_encontrado", "current ID")]
        [InlineData("The output directory does not exist: C:\\out. It is not created implicitly.", "archivo_no_accesible", "permissions")]
        [InlineData("output_path does not exist: C:\\out\\x.pdf. pdf_qa reads an existing PDF.", "archivo_no_accesible", "path")]
        [InlineData("format=pdf_qa reads a .pdf file; output_path is 'x.dwg'.", "esquema_invalido", "arguments")]
        [InlineData("Revit raised the dialog 'TaskDialog_Update_Resources' during the export and the bridge answered: cancel.", "revit_ocupado", "dialog")]
        [InlineData("PDF package verification failed: invalid page count. Check the produced PDF.", "verificacion_fallida", "verification")]
        [InlineData("Revit timed out after 60000 ms deadline", "tiempo_agotado", "may still be running")]
        [InlineData("unexpected diagnostic", "error_interno", "output files")]
        public void Actual_failure_messages_are_coded_and_actionable(string message, string code, string advice)
        {
            var result = ExportCommand.NormalizeExportFailure(CommandResult.Fail(message));
            Assert.Equal(code, (string)result.Detail["error_code"]);
            Assert.Contains(message, result.Error);
            Assert.Contains(advice, result.Error);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void File_exception_preserves_type_and_existing_evidence(bool denied)
        {
            Exception ex = denied ? (Exception)new UnauthorizedAccessException("Access to the path is denied.") : new IOException("The process cannot access the file because it is being used by another process.");
            var result = ExportCommand.NormalizeExportFailure(ExportCommand.ExportException(ex, new JObject { ["planned_files"] = new JArray("x.pdf") }));
            Assert.Equal(ErrorCodes.ArchivoNoAccesible, (string)result.Detail["error_code"]);
            Assert.Equal(ex.GetType().Name, (string)result.Detail["exception_type"]);
            Assert.Contains(ex.Message, result.Error);
            Assert.Contains("another application", result.Error);
            Assert.Single((JArray)result.Detail["planned_files"]);
        }

        [Fact]
        public void Timeout_exception_is_not_reported_as_api_failure()
        {
            var result = ExportCommand.NormalizeExportFailure(ExportCommand.ExportException(new TimeoutException("Exporter timed out")));
            Assert.Equal(ErrorCodes.TiempoAgotado, (string)result.Detail["error_code"]);
            Assert.Contains("before retrying", result.Error);
        }

        [Fact]
        public void Locked_output_in_real_attempt_keeps_evidence_and_action()
        {
            var result = ExportCommand.ExecuteExportAttempt("dwg", new[] { "x.dwg" }, false,
                (a, b) => { }, () => throw new IOException("The process cannot access the file because it is being used by another process."),
                () => { }, () => null, () => null, out _, out _, out _);
            result = ExportCommand.NormalizeExportFailure(result);
            Assert.Equal(ErrorCodes.ArchivoNoAccesible, (string)result.Detail["error_code"]);
            Assert.True((bool)result.Detail["external_files_may_exist"]);
            Assert.Contains("another application", result.Error);
        }

        [Fact]
        public void Real_format_and_dialog_diagnostics_are_classified()
        {
            var format = ExportCommand.NormalizeExportFailure(CommandResult.Fail(ExportDeliveryRules.InvalidFormatMessage("png")));
            Assert.Equal(ErrorCodes.EsquemaInvalido, (string)format.Detail["error_code"]);
            Assert.Contains("format=image", format.Error);
            var decision = ExportDialogPolicy.Decide(ExportDialogMode.Cancel, "TaskDialog_Update_Resources", "Update Resources");
            string cause = ExportDialogPolicy.CauseFirst(new[] { decision }, "Revit returned without a file.");
            var dialog = ExportCommand.NormalizeExportFailure(CommandResult.Fail(cause));
            Assert.Equal(ErrorCodes.RevitOcupado, (string)dialog.Detail["error_code"]);
            Assert.Contains("continue_known", dialog.Error);
        }
    }
}
