using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ExportCommand
    {
        // Shared by the command and Revit-free tests: the same failure path produces
        // the actual CommandResult after restoring every completed move.
        internal static CommandResult ExecuteExportAttempt(string format, string[] planned, bool overwrite,
            Action<string, string> move, Func<bool> export, Action finish,
            Func<JObject> isolationReport, Func<string> isolationHeadline,
            out List<AsideFile> asideFiles, out JArray restoredPdfs, out bool accepted)
        {
            asideFiles = new List<AsideFile>();
            restoredPdfs = null;
            accepted = false;
            Exception failure = null;
            try
            {
                if (format == "pdf")
                    PdfOverwriteRules.MoveAside(PdfOverwriteRules.ToMoveAside(planned, overwrite, File.Exists),
                        DateTime.UtcNow, asideFiles, move);
                accepted = export();
            }
            catch (Exception ex) { failure = ex; }
            if (asideFiles.Count > 0 && (failure != null || !accepted))
                restoredPdfs = RestorePdfAsides(asideFiles);
            finish();
            if (failure == null) return null;
            var detail = new JObject { ["external_files_may_exist"] = true, ["rollback_available"] = false,
                ["planned_files"] = new JArray(planned) };
            if (restoredPdfs != null) detail["previous_pdfs_restored"] = restoredPdfs;
            JObject report = isolationReport();
            if (report != null) detail["model_isolation"] = report;
            string headline = isolationHeadline();
            return ExportException(failure, detail, headline);
        }

        internal static CommandResult ExportException(Exception exception, JObject detail = null, string headline = null)
        {
            string code = exception is TimeoutException ? ErrorCodes.TiempoAgotado
                : exception is UnauthorizedAccessException || exception is IOException ? ErrorCodes.ArchivoNoAccesible
                : ErrorCodes.ApiRevitExcepcion;
            // Dialog wrapping retains the original exception; the dialog is the actionable cause.
            if (exception.Message.IndexOf("Revit raised the dialog", StringComparison.OrdinalIgnoreCase) >= 0)
                code = ErrorCodes.RevitOcupado;
            string message = "Revit export failed: " + exception.Message + (headline != null ? " " + headline : "");
            return CommandResult.FailCoded(code, message, exception.GetType().Name, extra: detail);
        }

        internal static CommandResult NormalizeExportFailure(CommandResult result)
        {
            if (result == null) return CommandResult.FailCoded(ErrorCodes.ErrorInterno,
                "Export returned no result. Inspect the output folder and diagnostics before retrying; files may already exist.");
            if (result.Success) return result;
            string message = string.IsNullOrWhiteSpace(result.Error) ? "Export failed without a diagnostic." : result.Error;
            JObject detail = ErrorCodes.Ensure(message, result.Detail);
            string code = (string)detail[ErrorCodes.CodeField];
            if (message.IndexOf("Revit raised the dialog", StringComparison.OrdinalIgnoreCase) >= 0 && code != ErrorCodes.TiempoAgotado)
                code = ErrorCodes.RevitOcupado;
            else if (message.StartsWith("The output directory does not exist:", StringComparison.OrdinalIgnoreCase) ||
                     message.StartsWith("output_path does not exist:", StringComparison.OrdinalIgnoreCase))
                code = ErrorCodes.ArchivoNoAccesible;
            else if (message.StartsWith("Every view_id must identify", StringComparison.OrdinalIgnoreCase))
                code = ErrorCodes.ElementoNoEncontrado;
            else if (message.StartsWith("format=pdf_qa reads a .pdf file", StringComparison.OrdinalIgnoreCase))
                code = ErrorCodes.EsquemaInvalido;
            else if (code == ErrorCodes.NoClasificado) code = ErrorCodes.ErrorInterno;
            detail[ErrorCodes.CodeField] = code;
            string advice = code == ErrorCodes.ArchivoNoAccesible ? "Check the path, permissions and whether another application has the file open."
                : code == ErrorCodes.ElementoNoEncontrado ? "Find the view or sheet again in the active document and use its current ID."
                : code == ErrorCodes.RevitOcupado ? "Inspect the recorded dialog and resolve its cause before retrying."
                : code == ErrorCodes.TiempoAgotado ? "Check the job and output before retrying; the export may still be running."
                : code == ErrorCodes.VerificacionFallida ? "Inspect the verification details and produced files before retrying."
                : code == ErrorCodes.EsquemaInvalido || code == ErrorCodes.EntradaInvalida ? "Correct the indicated arguments and retry."
                : "Inspect the diagnostic and output files before retrying; the export may have started.";
            result.ReplaceDetail(detail);
            result.ReplaceError(message + " " + advice);
            return result;
        }

        private static JArray RestorePdfAsides(List<AsideFile> asideFiles)
        {
            var restored = new JArray();
            foreach (RestoreResult result in PdfOverwriteRules.RestoreAsides(asideFiles))
                if (result.Error == null) restored.Add(result.Path);
                else restored.Add(new JObject { ["path"] = result.Path, ["restore_failed"] = result.Error, ["old_copy"] = result.OldCopy });
            return restored;
        }
    }
}
