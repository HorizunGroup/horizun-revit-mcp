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
        public string Description => "Export PDF, DWG, IFC, Navisworks NWC, FBX, image or schedule CSV and verify actual files.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string format = (request.Value<string>("format") ?? "").ToLowerInvariant();
            if (format != "pdf" && format != "dwg" && format != "ifc" && format != "nwc" && format != "fbx" && format != "image" && format != "schedule_csv")
                return CommandResult.Fail("format must be pdf, dwg, ifc, nwc, fbx, image or schedule_csv.");
            string output = request.Value<string>("output_path");
            if (string.IsNullOrWhiteSpace(output) || !System.IO.Path.IsPathRooted(output))
                return CommandResult.Fail("output_path must be absolute.");
            try { output = System.IO.Path.GetFullPath(output); }
            catch (Exception ex) { return CommandResult.Fail("output_path is invalid: " + ex.Message); }
            if (!ExpectedExtension(format, output))
                return CommandResult.Fail("output_path extension does not match format=" + format + ". Use " + ExpectedExtensionDescription(format) + ".");
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
            if (format == "fbx" && (views.Count == 0 || views.Any(v => !(v is View3D))))
                return CommandResult.Fail("FBX requires one or more non-template 3D view_ids.");
            string nwcScope = (request.Value<string>("nwc_scope") ?? "model").ToLowerInvariant();
            if (format == "nwc" && nwcScope != "model" && nwcScope != "view")
                return CommandResult.Fail("nwc_scope must be model or view.");
            if (format == "nwc" && ((nwcScope == "model" && views.Count != 0) || (nwcScope == "view" && views.Count != 1)))
                return CommandResult.Fail("NWC model scope uses no view_ids; view scope requires exactly one view_id.");
            if (format != "pdf" && format != "dwg" && format != "image" && format != "fbx" && format != "nwc" && views.Count > 0)
                return CommandResult.Fail("view_ids is not used for " + format + ".");

            View ifcFilterView = null;
            if (request["ifc_filter_view_id"] != null)
            {
                long raw = request.Value<long>("ifc_filter_view_id");
                if (!Rid.CanRepresent(raw) || !(doc.GetElement(Rid.Make(raw)) is View found) || found.IsTemplate)
                    return CommandResult.Fail("ifc_filter_view_id must identify a non-template view.");
                ifcFilterView = found;
            }
            if (format != "ifc" && ifcFilterView != null)
                return CommandResult.Fail("ifc_filter_view_id is only valid for IFC export.");

            IFCVersion ifcVersion = IFCVersion.Default;
            int ifcSpaceBoundary = request.Value<int?>("ifc_space_boundary_level") ?? 1;
            NavisworksCoordinates nwcCoordinates = NavisworksCoordinates.Shared;
            NavisworksParameters nwcParameters = NavisworksParameters.All;
            int fbxLod = request.Value<int?>("fbx_lod") ?? 8;
            int imagePixels = request.Value<int?>("image_pixels") ?? 2048;
            try
            {
                if (format == "ifc")
                {
                    ifcVersion = ParseEnum(request.Value<string>("ifc_version") ?? "Default", IFCVersion.Default, "ifc_version");
                    if (ifcSpaceBoundary < 0 || ifcSpaceBoundary > 2)
                        return CommandResult.Fail("ifc_space_boundary_level must be 0..2.");
                }
                if (format == "nwc")
                {
                    nwcCoordinates = ParseEnum(request.Value<string>("nwc_coordinates") ?? "Shared", NavisworksCoordinates.Shared, "nwc_coordinates");
                    nwcParameters = ParseEnum(request.Value<string>("nwc_parameters") ?? "All", NavisworksParameters.All, "nwc_parameters");
                }
            }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }
            if (format == "fbx" && (fbxLod < 0 || fbxLod > 15)) return CommandResult.Fail("fbx_lod must be 0..15.");
            if (format == "image" && (imagePixels < 128 || imagePixels > 8192)) return CommandResult.Fail("image_pixels must be 128..8192.");

            bool exporterAvailable = format != "nwc" || OptionalFunctionalityUtils.IsNavisworksExporterAvailable();

            List<string> existing = CandidateFiles(format, output);
            if (!overwrite && existing.Any(File.Exists))
                return CommandResult.Fail("Output already exists and overwrite=false: " + string.Join(", ", existing.Where(File.Exists)));

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "view_ids", "schedule_id", "image_pixels", "overwrite",
                "ifc_version", "ifc_filter_view_id", "ifc_export_base_quantities", "ifc_split_walls_and_columns", "ifc_space_boundary_level",
                "nwc_scope", "nwc_coordinates", "nwc_parameters", "nwc_export_links", "nwc_export_element_ids", "nwc_export_room_geometry",
                "nwc_export_parts", "fbx_without_boundary_edges", "fbx_use_lod", "fbx_lod", "fbx_stop_on_error");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["format"] = format, ["output_path"] = output,
                    ["views"] = new JArray(views.Select(v => new JObject { ["id"] = Rid.Value(v.Id), ["name"] = v.Name })),
                    ["schedule"] = schedule?.Name, ["overwrite"] = overwrite,
                    ["exporter_available"] = exporterAvailable,
                    ["note"] = "Nothing was exported and no file was created."
                };
                DocumentGate.StampConfirmation(result, gate, Name, planHash, exporterAvailable,
                    exporterAvailable ? "the token binds format, destination, selected views/schedule, export options and overwrite policy" :
                    "no usable token is issued because the optional Navisworks exporter is not installed");
                return CommandResult.Ok(result);
            }
            if (!exporterAvailable)
                return CommandResult.Fail("The optional Autodesk Navisworks NWC exporter is not installed for this Revit version.");
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
                        var ifc = new IFCExportOptions
                        {
                            ExportBaseQuantities = request.Value<bool?>("ifc_export_base_quantities") == true,
                            WallAndColumnSplitting = request.Value<bool?>("ifc_split_walls_and_columns") == true,
                            SpaceBoundaryLevel = ifcSpaceBoundary,
                            FilterViewId = ifcFilterView?.Id ?? ElementId.InvalidElementId
                        };
                        ifc.FileVersion = ifcVersion;
                        apiAccepted = doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), ifc); break;
                    case "nwc":
                        using (var nwc = new NavisworksExportOptions())
                        {
                            nwc.ExportScope = nwcScope == "view" ? NavisworksExportScope.View : NavisworksExportScope.Model;
                            if (nwcScope == "view") nwc.ViewId = views[0].Id;
                            nwc.Coordinates = nwcCoordinates;
                            nwc.Parameters = nwcParameters;
                            nwc.ExportLinks = request.Value<bool?>("nwc_export_links") == true;
                            nwc.ExportElementIds = request.Value<bool?>("nwc_export_element_ids") != false;
                            nwc.ExportRoomGeometry = request.Value<bool?>("nwc_export_room_geometry") != false;
                            nwc.ExportParts = request.Value<bool?>("nwc_export_parts") == true;
                            doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), nwc);
                            apiAccepted = true;
                        }
                        break;
                    case "fbx":
                        var viewSet = new ViewSet();
                        foreach (View view in views) viewSet.Insert(view);
                        var fbx = new FBXExportOptions
                        {
                            WithoutBoundaryEdges = request.Value<bool?>("fbx_without_boundary_edges") == true,
                            UseLevelsOfDetail = request.Value<bool?>("fbx_use_lod") == true,
                            LevelsOfDetailValue = fbxLod,
                            StopOnError = request.Value<bool?>("fbx_stop_on_error") != false
                        };
                        apiAccepted = doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), viewSet, fbx); break;
                    case "image":
                        var image = new ImageExportOptions
                        {
                            ExportRange = ExportRange.SetOfViews, FilePath = output, ZoomType = ZoomFitType.FitToPage,
                            PixelSize = imagePixels,
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
                case "nwc": return ext == ".nwc";
                case "fbx": return ext == ".fbx";
                case "image": return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tif" || ext == ".tiff";
                case "schedule_csv": return ext == ".csv" || ext == ".txt";
                default: return false;
            }
        }
        private static bool ExpectedExtension(string format, string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            switch (format)
            {
                case "pdf": return ext == ".pdf";
                case "dwg": return ext == ".dwg";
                case "ifc": return ext == ".ifc";
                case "nwc": return ext == ".nwc";
                case "fbx": return ext == ".fbx";
                case "image": return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tif" || ext == ".tiff";
                case "schedule_csv": return ext == ".csv" || ext == ".txt";
                default: return false;
            }
        }
        private static string ExpectedExtensionDescription(string format)
        {
            if (format == "image") return ".png, .jpg, .jpeg, .bmp, .tif or .tiff";
            if (format == "schedule_csv") return ".csv or .txt";
            return "." + format;
        }
        private static Dictionary<string, Stamp> Snapshot(string folder)
        {
            var result = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(folder))
            { try { var f = new FileInfo(file); result[file] = new Stamp { Size = f.Length, Mtime = f.LastWriteTimeUtc.Ticks }; } catch { } }
            return result;
        }
        private static T ParseEnum<T>(string raw, T fallback, string field) where T : struct
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (Enum.TryParse(raw, true, out T value) && Enum.IsDefined(typeof(T), value)) return value;
            throw new ArgumentException(field + " has unsupported value '" + raw + "'.");
        }
        private sealed class Stamp { public long Size, Mtime; }
    }
}
