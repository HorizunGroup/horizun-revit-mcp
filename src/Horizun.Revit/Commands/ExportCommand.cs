// -----------------------------------------------------------------------------
// Horizun Revit MCP - exports verified against the filesystem after Revit returns.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ExportCommand : ICommand
    {
        public string Name => "horizun_export";
        public string Description => "Export PDF, DWG, IFC, image or schedule CSV and verify actual files.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string format = (request.Value<string>("format") ?? "").ToLowerInvariant();
            if (format != "pdf" && format != "dwg" && format != "ifc" && format != "image" && format != "schedule_csv")
                return CommandResult.Fail("format must be pdf, dwg, ifc, image or schedule_csv.");
            string output = request.Value<string>("output_path");
            if (string.IsNullOrWhiteSpace(output) || !System.IO.Path.IsPathRooted(output))
                return CommandResult.Fail("output_path must be absolute.");
            output = System.IO.Path.GetFullPath(output);
            string folder = System.IO.Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return CommandResult.Fail("The output directory does not exist: " + folder + ". It is not created implicitly.");
            bool overwrite = request.Value<bool?>("overwrite") == true;

            List<View> views = ReadViews(doc, request["view_ids"] as JArray, out string viewError);
            if (viewError != null) return CommandResult.Fail(viewError);
            ViewSchedule schedule = null;
            if (format == "schedule_csv")
            {
                long id = request.Value<long?>("schedule_id") ?? -1;
                if (!Rid.CanRepresent(id) || !(doc.GetElement(Rid.Make(id)) is ViewSchedule found))
                    return CommandResult.Fail("schedule_id must identify a native ViewSchedule.");
                schedule = found;
            }
            if (format == "pdf" && views.Count == 0) return CommandResult.Fail("PDF requires at least one view_id.");
            if ((format == "dwg" || format == "image") && views.Count != 1)
                return CommandResult.Fail(format + " requires exactly one view_id so the output can be identified and verified.");
            if (format != "pdf" && format != "dwg" && format != "image" && views.Count > 0)
                return CommandResult.Fail("view_ids is not used for " + format + ".");

            List<string> existing = CandidateFiles(format, output);
            if (!overwrite && existing.Any(File.Exists))
                return CommandResult.Fail("Output already exists and overwrite=false: " + string.Join(", ", existing.Where(File.Exists)));

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "view_ids", "schedule_id", "image_pixels", "overwrite");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["format"] = format, ["output_path"] = output,
                    ["views"] = new JArray(views.Select(v => new JObject { ["id"] = Rid.Value(v.Id), ["name"] = v.Name })),
                    ["schedule"] = schedule?.Name, ["overwrite"] = overwrite,
                    ["note"] = "Nothing was exported and no file was created."
                };
                DocumentGate.StampConfirmation(result, gate, Name, planHash, true,
                    "the token binds format, destination, selected views/schedule and overwrite policy");
                return CommandResult.Ok(result);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            var before = Snapshot(folder);
            bool apiAccepted = false;
            try
            {
                switch (format)
                {
                    case "pdf":
                        var pdf = new PDFExportOptions { Combine = true, FileName = System.IO.Path.GetFileName(output) };
                        apiAccepted = doc.Export(folder, views.Select(v => v.Id).ToList(), pdf); break;
                    case "dwg":
                        apiAccepted = doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output),
                            new List<ElementId> { views[0].Id }, new DWGExportOptions()); break;
                    case "ifc":
                        apiAccepted = doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), new IFCExportOptions()); break;
                    case "image":
                        var image = new ImageExportOptions
                        {
                            ExportRange = ExportRange.SetOfViews, FilePath = output, ZoomType = ZoomFitType.FitToPage,
                            PixelSize = Math.Max(128, Math.Min(8192, request.Value<int?>("image_pixels") ?? 2048)),
                            HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG
                        };
                        image.SetViewsAndSheets(new List<ElementId> { views[0].Id }); doc.ExportImage(image); apiAccepted = true; break;
                    case "schedule_csv":
                        schedule.Export(folder, System.IO.Path.GetFileName(output), new ViewScheduleExportOptions()); apiAccepted = true; break;
                }
            }
            catch (Exception ex) { return CommandResult.Fail("Revit export failed: " + ex.Message); }

            var after = Snapshot(folder);
            List<string> produced = after.Where(kv => kv.Value.Size > 0 &&
                    MatchesOutput(format, output, kv.Key) &&
                    (!before.TryGetValue(kv.Key, out Stamp old) || old.Size != kv.Value.Size || old.Mtime != kv.Value.Mtime))
                .Select(kv => kv.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (produced.Count == 0)
                return CommandResult.Fail("Revit returned from export (accepted=" + apiAccepted +
                    "), but no new or changed non-empty file was measured in " + folder + ". Success is not claimed.");

            var files = new JArray();
            foreach (string path in produced)
            {
                var info = new FileInfo(path);
                files.Add(new JObject { ["path"] = path, ["bytes"] = info.Length, ["last_write_utc"] = info.LastWriteTimeUtc.ToString("o") });
            }
            return CommandResult.Ok(new JObject
            {
                ["format"] = format, ["api_accepted"] = apiAccepted, ["files_verified"] = produced.Count,
                ["requested_output_path"] = output, ["files"] = files,
                ["note"] = produced.Count == 1 ? "One produced file was re-read from disk." :
                    "Revit produced multiple sidecar/output files; every changed non-empty file is reported."
            });
        }

        private static List<View> ReadViews(Document doc, JArray ids, out string error)
        {
            error = null; var views = new List<View>(); if (ids == null) return views;
            foreach (JToken token in ids)
            {
                long raw;
                if (token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), out raw) ||
                    !Rid.CanRepresent(raw) || !(doc.GetElement(Rid.Make(raw)) is View view) || view.IsTemplate)
                { error = "Every view_id must identify a non-template view in the active document; failed at " + token; return new List<View>(); }
                views.Add(view);
            }
            return views;
        }
        private static List<string> CandidateFiles(string format, string output)
        {
            string folder = System.IO.Path.GetDirectoryName(output);
            return Directory.GetFiles(folder).Where(path => MatchesOutput(format, output, path)).ToList();
        }
        private static bool MatchesOutput(string format, string output, string path)
        {
            string wantedStem = System.IO.Path.GetFileNameWithoutExtension(output);
            string actualStem = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!actualStem.Equals(wantedStem, StringComparison.OrdinalIgnoreCase) &&
                !actualStem.StartsWith(wantedStem + "-", StringComparison.OrdinalIgnoreCase) &&
                !actualStem.StartsWith(wantedStem + "_", StringComparison.OrdinalIgnoreCase))
                return false;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            switch (format)
            {
                case "pdf": return ext == ".pdf";
                case "dwg": return ext == ".dwg";
                case "ifc": return ext == ".ifc";
                case "image": return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tif" || ext == ".tiff";
                case "schedule_csv": return ext == ".csv" || ext == ".txt";
                default: return false;
            }
        }
        private static Dictionary<string, Stamp> Snapshot(string folder)
        {
            var result = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(folder))
            { try { var f = new FileInfo(file); result[file] = new Stamp { Size = f.Length, Mtime = f.LastWriteTimeUtc.Ticks }; } catch { } }
            return result;
        }
        private sealed class Stamp { public long Size, Mtime; }
    }
}
