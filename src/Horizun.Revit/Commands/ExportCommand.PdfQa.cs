// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_export format=pdf_qa. Original Horizun code.
//
// 4.2-N4 (CT-S0-9, CT-P1-4d, LP-P3-13c): the QA of a PDF that ALREADY exists - fonts used
// (substituted ones named), blank pages, text over text, text beyond the frame. It reads the
// file at output_path and writes nothing, so every profile may use it. The same report is
// attached to each file of an export when format=pdf is called with pdf_qa.
// 5.2 (P1): view_ids names the sheets the pages print, in page order, so viewports, regions by
// viewport/element and notes by id can be measured on the paper (ExportCommand.PdfPaper.cs).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ExportCommand
    {
        private static CommandResult ExecutePdfQa(Document doc, JObject request, string output)
        {
            foreach (string notForQa in new[] { "schedule_id", "pdf_combine", "pdf_name_pattern", "pdf_version", "pdf_print", "emit_manifest", "overwrite", "pdf_parts", "pdf_also_per_sheet", "pdf_archive_previous", "pdf_archive_folder", "delivery_id", "information_container" })
                if (request[notForQa] != null && request[notForQa].Type != JTokenType.Null)
                    return CommandResult.Fail(notForQa + " does not apply to format=pdf_qa, which only reads the PDF at output_path. Nothing was read or written.");
            if (!string.Equals(Path.GetExtension(output), ".pdf", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("format=pdf_qa reads a .pdf file; output_path is '" + output + "'.");
            if (!File.Exists(output)) return CommandResult.Fail("output_path does not exist: " + output + ". pdf_qa reads an existing PDF.");
            PdfQaOptions options = PdfQa.Parse(request["pdf_qa"], out string optionError);
            if (options == null) return CommandResult.Fail(optionError);
            var pages = new List<View>();
            if (request["view_ids"] is JArray ids)
            {
                foreach (JToken t in ids)
                {
                    long raw = t.Type == JTokenType.Integer ? t.Value<long>() : 0;
                    if (!(Rid.CanRepresent(raw) && doc.GetElement(Rid.Make(raw)) is ViewSheet sheet))
                        return CommandResult.Fail("format=pdf_qa view_ids are the SHEETS the PDF's pages print, in page order; " + t + " is not a sheet. Nothing was read.");
                    pages.Add(sheet);
                }
            }
            else if (request["view_ids"] != null && request["view_ids"].Type != JTokenType.Null)
                return CommandResult.Fail("view_ids must be a list of sheet ids in page order.");
            if (options.Viewports && pages.Count == 0)
                return CommandResult.Fail("pdf_qa.viewports needs view_ids: the sheets the PDF's pages print, in page order. Nothing was read.");
            if (options.CompareTo != null && !File.Exists(options.CompareTo))
                return CommandResult.Fail("pdf_qa.compare_to does not exist: " + options.CompareTo + ". Nothing was read.");
            ResolvePaper(doc, options);
            options = ForPages(doc, options, pages);
            JObject report;
            try { report = PdfQa.Inspect(output, options); }
            catch (Exception ex) { return CommandResult.Fail("The PDF could not be read back: " + ex.Message + " No verdict was formed."); }
            var info = new FileInfo(output);
            return CommandResult.Ok(new JObject
            {
                ["format"] = "pdf_qa", ["read_only"] = true, ["path"] = output, ["bytes"] = info.Length,
                ["qa"] = report, ["verdict"] = report["verdict"],
                ["note"] = "Nothing was exported, created or changed."
            });
        }
    }
}
