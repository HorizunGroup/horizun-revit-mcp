// -----------------------------------------------------------------------------
// Horizun Revit MCP - exports verified against the filesystem after Revit returns.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
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
    public sealed partial class ExportCommand : ICommand
    {
        public string Name => "horizun_export";
        public string Description => "Export PDF, DWG/DGN/DWFX view sets, IFC, NWC, FBX, image, schedule CSV, gbXML, family .rfa or a COBie workbook and verify actual files.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject r;
            try { r = JObject.Parse(paramsJson ?? "{}"); } catch { r = new JObject(); }
            CommandResult result;
            var assumptions = new List<JObject>();
            try { result = NormalizeExportFailure(ExecuteCore(app, paramsJson, assumptions)); }
            catch (Exception ex) { result = NormalizeExportFailure(ExportException(ex)); }
            if (r.Value<string>("format") == "mesh_json" && !result.Success)
                return AssumptionNotes.MarkFailure(result, "SUPUESTO used; ", ReliabilityDefaults.Mesh(r));
            return result.Success ? AssumptionNotes.StampResult(result, assumptions)
                : AssumptionNotes.MarkFailure(result, "SUPUESTO used; ", assumptions);
        }

        private CommandResult ExecuteCore(UIApplication app, string paramsJson, List<JObject> assumptions)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string format = (request.Value<string>("format") ?? "").ToLowerInvariant();
            if (format != "pdf" && format != "dwg" && format != "ifc" && format != "nwc" && format != "fbx" && format != "image" && format != "schedule_csv" && format != "dwg_layers" && format != "ifc_setup"
                && format != "dgn" && format != "dwfx" && format != "gbxml" && format != "rfa" && format != "cobie" && format != "pdf_qa" && format != "mesh_json")
                return CommandResult.Fail(ExportDeliveryRules.InvalidFormatMessage(format));
            // THE PER-CALL HALF OF THE PROFILE, before anything is resolved or written. The
            // tool is listed on read_only/documentation/safe_write for PDF - the deliverable
            // those profiles exist to produce - and a one-view image (a picture of a view,
            // nothing written to the model); every other format refuses here, naming
            // the profile (OperationPermission.ExportRefusal).
            string formatRefusal = OperationPermission.ExportRefusal(Horizun.Revit.Core.Settings.PermissionProfile, format);
            if (formatRefusal != null) return CommandResult.Fail(formatRefusal);
            if (format != "cobie" && request["cobie"] != null && request["cobie"].Type != JTokenType.Null)
                return CommandResult.Fail("cobie applies to format cobie only. Nothing was exported.");
            // dialog_policy (4.2-c): how the export answers the modal dialogs Revit raises while it
            // runs. Parsed before anything is resolved; a typo refuses, it never falls back.
            ExportDialogMode dialogMode = ExportDialogPolicy.Parse(request.Value<string>("dialog_policy"), out string dialogPolicyError);
            if (dialogPolicyError != null) return CommandResult.Fail(dialogPolicyError);
            bool dialogPolicyRequested = !string.IsNullOrWhiteSpace(request.Value<string>("dialog_policy"));
            if (dialogPolicyRequested && !ExportDialogPolicy.AppliesTo(format, format == "dwg" && IsDwgSet(request)))
                return CommandResult.Fail("dialog_policy applies to the single-file exports (pdf, dwg, ifc, nwc, fbx, image, schedule_csv); " +
                                          "format=" + format + " does not use it. Nothing was exported.");
            bool canCreateFolder = format == "pdf" || format == "image" || format == "dwg" || format == "ifc" || format == "fbx";
            if (request["require_existing_folder"] != null && !canCreateFolder)
                return CommandResult.RefuseBeforeWrite("require_existing_folder applies only to PDF, image, DWG, IFC and FBX.");
            string output = request.Value<string>("output_path");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output) ||
                (canCreateFolder && Path.DirectorySeparatorChar == '\\' &&
                 !(output.StartsWith(@"\\", StringComparison.Ordinal) || output.StartsWith("//", StringComparison.Ordinal) ||
                   (output.Length >= 3 && output[1] == ':' && (output[2] == '\\' || output[2] == '/')))))
                return CommandResult.Fail("output_path must be absolute.");
            try { output = System.IO.Path.GetFullPath(output); }
            catch (Exception ex) { return CommandResult.Fail("output_path is invalid: " + ex.Message); }
            // 4.2-N4 (CT-S0-9, CT-P1-4d): QA of a PDF that already exists. Reads, writes nothing.
            if (format == "pdf_qa") return ExecutePdfQa(doc, request, output);
            if (format == "mesh_json") return ExecuteMeshJson(app, gate, doc, request, output);
            foreach (string meshField in new[] { "categories", "skip_directshape", "include_links" })
                if (request[meshField] != null) return CommandResult.RefuseBeforeWrite(meshField + " applies to mesh_json only.");
            // 4.2-N4 (LP-P3-13a): with one file per sheet, output_path may be the FOLDER.
            bool outputIsFolder = false;
            if (format == "pdf" && request.Value<bool?>("pdf_combine") == false && ExportDeliveryRules.LooksLikeFolder(output, Directory.Exists))
            {
                try { output = ExportDeliveryRules.FolderStem(output); outputIsFolder = true; }
                catch (ArgumentException ex) { return CommandResult.Fail(ex.Message + " Nothing was exported."); }
            }
            // rfa writes one file per family into a FOLDER; see ExportSets.cs.
            if (format == "rfa") return ExecuteRfa(app, gate, doc, request, output);
            if (!ExpectedExtension(format, output))
                return CommandResult.Fail("output_path extension does not match format=" + format + ". Use " + ExpectedExtensionDescription(format) + ".");
            // The DWG layer table: a named export setup read, created and written in the
            // document, with its re-read table written to output_path. See ExportDwgSetup.cs.
            if (format == "dwg_layers") return ExecuteDwgLayers(app, gate, doc, request, output);
            // A named IFC export setup saved in the document (the exporter UI's own storage),
            // plus IfcExportAs/IfcExportType per type, re-read and mirrored to output_path. See ExportIfcSetup.cs.
            if (format == "ifc_setup") return ExecuteIfcSetup(app, gate, doc, request, output);
            if (request["ifc_setup"] != null) return CommandResult.Fail("ifc_setup applies to format ifc_setup only. Nothing was exported.");
            // gbXML and the DWG/DGN/DWFX view sets (one file per view or sheet). See ExportSets.cs.
            if (format == "gbxml") return ExecuteGbXml(app, gate, doc, request, output);
            // A COBie 2.4 workbook written by the Core writer and re-read cell by cell. See ExportCobie.cs.
            if (format == "cobie") return ExecuteCobie(app, gate, doc, request, output);
            if (format == "dgn" || format == "dwfx" || (format == "dwg" && IsDwgSet(request)))
                return ExecuteViewSet(app, gate, doc, request, format, output, assumptions);
            foreach (string setField in new[] { "file_naming", "dwg_xrefs", "family_ids", "category" })
                if (request[setField] != null)
                    return CommandResult.Fail(setField + " applies to the dwg/dgn/dwfx view sets and rfa only. Nothing was exported.");
            if (request["dwg_setup"] != null && format != "dwg")
                return CommandResult.Fail("dwg_setup applies to format dwg (export with it) and dwg_layers (read/write it).");
            DWGExportOptions dwgOptions = null;
            if (format == "dwg")
            {
                string dwgRefusal;
                dwgOptions = BuildDwgOptions(doc, request, out dwgRefusal);
                if (dwgOptions == null) return CommandResult.Fail(dwgRefusal);
            }

            // ---- ISO 19650 information container (optional). ----
            // Validated BEFORE anything else is decided, so an invalid container refuses
            // here with every problem named and nothing exported. With a valid one the
            // produced file takes the container's name (directory and extension from
            // output_path) and, after the export is verified, a sidecar is written beside
            // it and read back. Without the argument nothing below changes.
            ContainerSpec container = null; ContainerValidation containerCheck = null; string requestedOutput = null;
            if (request["information_container"] != null && request["information_container"].Type != JTokenType.Null)
            {
                if (format == "image")
                    return CommandResult.Fail("information_container cannot name an image export: Revit derives image file names " +
                        "from the view, so the file produced would not carry the container's name. Nothing was exported.");
                try { container = InformationContainer.ParseSpec(request["information_container"]); }
                catch (ContainerRuleException ex) { return CommandResult.Fail("information_container refused: " + ex.Message + " Nothing was exported."); }
                containerCheck = InformationContainer.Validate(container, true);
                if (!containerCheck.Valid)
                    return CommandResult.FailWithDetail("information_container does not validate: " +
                        string.Join("; ", containerCheck.Problems.Select(p => (string)p["field"] + ": " + (string)p["reason"])) +
                        ". Nothing was exported.", new JObject { ["information_container"] = containerCheck.ToJson() });
                requestedOutput = output;
                output = InformationContainer.ContainerOutputPath(output, containerCheck);
            }
            string folder = System.IO.Path.GetDirectoryName(output);
            var folderPlan = canCreateFolder ? new ExportDirectoryPlan(output, request.Value<bool?>("require_existing_folder") == true) : null;
            bool folderMissing = folderPlan?.NeedsCreation == true;
            if (!canCreateFolder && (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)))
                return CommandResult.Fail("The output directory does not exist: " + folder + ". It is not created implicitly: create that folder first, or point output_path (the full path of the file to write) at a folder that exists. Nothing was written.");
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
            // ---- preset: a named, hashed option bundle handed IN as an argument. ----
            // Organisation-neutral by construction: nothing here ships options for
            // anybody; the preset arrives with the request, its options override the
            // loose arguments, its hash joins the plan (an edited preset is a
            // different plan and the token refuses), and after the export each
            // option is either PROVED from the produced file or reported
            // requested_unverifiable by name.
            ExportPreset preset = null; string presetHash = null;
            if (request["preset"] is JObject presetToken)
            {
                var presetOptions = new List<KeyValuePair<string, string>>();
                if (presetToken["options"] is JObject optionsToken)
                    foreach (JProperty property in optionsToken.Properties())
                        presetOptions.Add(new KeyValuePair<string, string>(property.Name,
                            property.Value.Type == JTokenType.Boolean
                                ? ((bool)property.Value ? "true" : "false")
                                : JsonScalar.ToInvariantString(property.Value) ?? ""));   // never the current culture: 0.5 is not "0,5"
                string presetReason;
                preset = ExportPresetRules.Parse(
                    presetToken.Value<string>("name"), format,
                    presetToken.Value<int?>("schema_version") ?? 1,
                    presetToken.Value<string>("overwrite_policy"),
                    presetOptions, out presetReason);
                if (preset == null)
                    return CommandResult.Fail("preset refused: " + presetReason + " Nothing was exported.");
                presetHash = ExportPresetRules.Hash(preset);
                if (preset.OverwritePolicy == ExportPresetRules.PolicyReplace) overwrite = true;
                string optionValue;
                if (preset.Options.TryGetValue("ifc_version", out optionValue))
                    request["ifc_version"] = optionValue;
                if (preset.Options.TryGetValue("acad_version", out optionValue))
                    request["acad_version"] = optionValue;
                if (preset.Options.TryGetValue("pixel_size", out optionValue))
                    request["image_pixels"] = int.Parse(optionValue, System.Globalization.CultureInfo.InvariantCulture);
                if (preset.Options.TryGetValue("combine", out optionValue))
                    request["pdf_combine"] = optionValue == "true";
            }

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

            bool pdfCombine = request.Value<bool?>("pdf_combine") ?? true;
            bool emitManifest = request.Value<bool?>("emit_manifest") ?? false;
            if (format != "pdf" && (request["pdf_combine"] != null || emitManifest || request["pdf_print"] != null))
                return CommandResult.Fail("pdf_combine, emit_manifest and pdf_print are PDF-only options.");
            // 4.2-N4 delivery options (ExportDeliveryRules, PdfQa).
            int pdfParts = 0; bool pdfAlsoPerSheet = false, pdfArchivePrevious = false; string pdfArchiveFolder = ExportDeliveryRules.DefaultArchiveFolder;
            PdfQaOptions pdfQa = null;
            foreach (string only in new[] { "pdf_parts", "pdf_also_per_sheet", "pdf_archive_previous", "pdf_archive_folder", "pdf_qa" })
                if (format != "pdf" && request[only] != null && request[only].Type != JTokenType.Null)
                    return CommandResult.Fail(only + " is a PDF-only option. Nothing was exported.");
            if (format == "pdf")
            {
                if (request["pdf_parts"] != null && request["pdf_parts"].Type != JTokenType.Null)
                {
                    if (request["pdf_parts"].Type != JTokenType.Integer) return CommandResult.Fail("pdf_parts must be an integer 2.." + ExportDeliveryRules.MaxParts + ". Nothing was exported.");
                    pdfParts = (int)Math.Min(int.MaxValue, Math.Max(-1, request.Value<long>("pdf_parts")));
                }
                pdfAlsoPerSheet = request.Value<bool?>("pdf_also_per_sheet") == true;
                pdfArchivePrevious = request.Value<bool?>("pdf_archive_previous") == true;
                if (request["pdf_archive_folder"] != null)
                {
                    pdfArchiveFolder = request.Value<string>("pdf_archive_folder");
                    string archiveError = ExportDeliveryRules.ArchiveFolderError(pdfArchiveFolder);
                    if (archiveError != null) return CommandResult.Fail(archiveError + " Nothing was exported.");
                }
                if (request["pdf_qa"] != null && request["pdf_qa"].Type != JTokenType.Null)
                {
                    pdfQa = PdfQa.Parse(request["pdf_qa"], out string qaError);
                    if (pdfQa == null) return CommandResult.Fail(qaError + " Nothing was exported.");
                    if (pdfQa.CompareTo != null && (!pdfCombine || pdfParts > 0 || pdfAlsoPerSheet))
                        return CommandResult.Fail("pdf_qa.compare_to compares ONE file with an earlier one; this export produces several. Export combined, or compare afterwards with format=pdf_qa. Nothing was exported.");
                    if (pdfQa.CompareTo != null && !File.Exists(pdfQa.CompareTo))
                        return CommandResult.Fail("pdf_qa.compare_to does not exist: " + pdfQa.CompareTo + ". Nothing was exported.");
                    ResolvePaper(doc, pdfQa);
                }
                string jobError = ExportDeliveryRules.ValidateJobOptions(pdfCombine, pdfParts, pdfAlsoPerSheet, views.Count);
                if (jobError != null) return CommandResult.Fail(jobError + " Nothing was exported.");
            }
            if (container != null && format == "pdf" && (!pdfCombine || pdfParts > 0 || pdfAlsoPerSheet))
                return CommandResult.Fail("information_container names ONE file; pdf_combine=false, pdf_parts and pdf_also_per_sheet produce several. " +
                    "Export combined, or export each view with its own container. Nothing was exported.");
            if (container != null && File.Exists(InformationContainer.SidecarPath(output)))
                return CommandResult.Fail("An information-container sidecar already exists at " + InformationContainer.SidecarPath(output) +
                    " and is never overwritten, whatever overwrite says. Nothing was exported.");
            // THE PRINT POLICY. Parsed before anything else is decided so that a
            // wrong field refuses here, by name, and is never silently dropped on
            // the way to the exporter. Absent -> the policy defaults, with nothing
            // counted as requested.
            PdfPrintPolicy printPolicy;
            int hostYear;
            try
            {
                if (!int.TryParse(app.Application.VersionNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out hostYear))
                    return CommandResult.Fail("The host Revit year could not be read from VersionNumber '" + app.Application.VersionNumber + "'.");
                printPolicy = PdfPrintPolicy.Parse(request["pdf_print"], request.Value<string>("units") ?? "mm", hostYear);
            }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message + " Nothing was exported."); }
            // FILE NAMES BY PATTERN (PdfNamePattern.cs): {sheet_number} {sheet_name} {view_name}
            // {date} {version} {index}, per file or - combined - from the first view; the
            // folder is output_path's. Resolved and checked for collisions here, before any
            // exporter runs, and bound into the plan hash below so a rehearsal that named
            // A101_20261018_v1.pdf cannot apply under another name.
            string pdfPattern = request.Value<string>("pdf_name_pattern");
            int pdfVersion = 1;
            bool autoVersion = false;
            if (request["pdf_version"] != null && request["pdf_version"].Type != JTokenType.Null)
            {
                // 4.2-N4 (CT-P1-4c): "auto" = one more than the highest {version} already in the folder.
                if (request["pdf_version"].Type == JTokenType.String && string.Equals(request.Value<string>("pdf_version"), "auto", StringComparison.OrdinalIgnoreCase))
                    autoVersion = true;
                else if (request["pdf_version"].Type != JTokenType.Integer || (long)request["pdf_version"] < 0 || (long)request["pdf_version"] > int.MaxValue)
                    return CommandResult.Fail("pdf_version must be an integer of 0 or more, or \"auto\". Nothing was exported.");
                else pdfVersion = (int)(long)request["pdf_version"];
            }
            if (format != "pdf" && (pdfPattern != null || request["pdf_version"] != null))
                return CommandResult.Fail("pdf_name_pattern and pdf_version are PDF-only options. Nothing was exported.");
            if (pdfPattern != null && container != null)
                return CommandResult.Fail("Give pdf_name_pattern OR information_container: both name the file. Nothing was exported.");
            if (pdfPattern == null && request["pdf_version"] != null)
                return CommandResult.Fail("pdf_version fills {version} in pdf_name_pattern; give the pattern too. Nothing was exported.");
            if (pdfArchivePrevious && (pdfPattern == null || !ExportDeliveryRules.HasVersionToken(pdfPattern)))
                return CommandResult.Fail("pdf_archive_previous moves the OLDER versions of pdf_name_pattern aside; the pattern needs {version}. Nothing was exported.");
            if (autoVersion)
            {
                try { pdfVersion = ExportDeliveryRules.NextVersion(pdfPattern ?? "", folderMissing ? Array.Empty<string>() : Directory.GetFiles(folder, "*.pdf")); }
                catch (ArgumentException ex) { return CommandResult.Fail(ex.Message + " Nothing was exported."); }
            }
            string[] pdfPaths;
            PdfJobs pdfJobs = null;
            bool wantsPerSheet = !pdfCombine || pdfAlsoPerSheet;
            if (format == "pdf" && pdfPattern != null)
            {
                try
                {
                    List<PdfNameSource> nameSources = views.Select(NameSource).ToList();
                    DateTime stamp = DateTime.Now;
                    string combinedPath = PdfNamePattern.Paths(folder, pdfPattern, nameSources, true, stamp, pdfVersion)[0];
                    string[] perSheet = wantsPerSheet ? PdfNamePattern.Paths(folder, pdfPattern, nameSources, false, stamp, pdfVersion) : new string[0];
                    pdfJobs = ExportDeliveryRules.Jobs(combinedPath, perSheet, views.Count, pdfCombine, pdfParts, pdfAlsoPerSheet);
                    pdfPaths = pdfJobs.Paths;
                    output = combinedPath;
                }
                catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }
            }
            else
            {
                try
                {
                    if (format == "pdf")
                    {
                        long[] viewIds = views.Select(v => Rid.Value(v.Id)).ToArray();
                        string[] perSheet = DeliveryPdf.Paths(output, false, viewIds);
                        DeliveryPdf.Paths(output, true, viewIds);
                        pdfJobs = ExportDeliveryRules.Jobs(output, perSheet, views.Count, pdfCombine, pdfParts, pdfAlsoPerSheet);
                        pdfPaths = pdfJobs.Paths;
                    }
                    else pdfPaths = new string[0];
                }
                catch (ArgumentException ex)
                {
                    return CommandResult.FailWithDetail(ex.Message + " Nothing was exported.", ErrorCodes.Detail(ErrorCodes.EntradaInvalida));
                }
            }
            // The older versions that will be MOVED (never deleted) after the new one is verified.
            List<string> archiveCandidates = format == "pdf" && pdfArchivePrevious && !folderMissing
                ? ExportDeliveryRules.PreviousVersions(pdfPattern, Directory.GetFiles(folder, "*.pdf"), pdfVersion) : new List<string>();
            string manifestPath = output + ".manifest.json";
            List<string> existing = format == "pdf" ? pdfPaths.ToList() : folderMissing ? new List<string>() : CandidateFiles(format, output);
            if (emitManifest) existing.Add(manifestPath);
            if (!overwrite && existing.Any(File.Exists))
                return CommandResult.Fail("Output already exists and overwrite=false: " + string.Join(", ", existing.Where(File.Exists)));

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            // THE PREVENTION GATE, on the rehearsal and again on the apply - each call
            // measures the document as it stands at that moment. Optional: without
            // require_gate nothing below changes. A blocked or not-assessable decision
            // refuses before any exporter runs, and the reply carries the decision
            // either way. It is deliberately outside the plan hash: it is not a field
            // that changes WHAT is exported, and a token must not be refused because a
            // caller added the gate between rehearsal and apply.
            OperationGateResult gateDecision = OperationGate.Evaluate(app, doc, request["require_gate"],
                                                                      GatedOperation.Export, Name);
            if (gateDecision.Refusal != null) return gateDecision.Refusal;
            // THE DELIVERY GATE. With delivery_id, this export is the publish stage of a
            // ledgered delivery: every recorded scope is re-read first, and the file is
            // touched only if the gate is open - complete, audited, every sheet approval
            // still current. A closed gate refuses before any exporter runs.
            JObject deliveryRecord = null; JObject deliveryGate = null; JArray deliveryReverification = null;
            string deliveryId = request.Value<string>("delivery_id");
            if (deliveryId != null)
            {
                if (format != "pdf") return CommandResult.Fail("delivery_id applies to the PDF publication stage only.");
                string ledgerRefusal;
                deliveryRecord = DeliveryLedgerHost.Load(deliveryId, out ledgerRefusal);
                if (deliveryRecord == null) return CommandResult.Fail(ledgerRefusal);
                string mismatch = DeliveryLedgerHost.DocumentMismatch(app, doc, deliveryRecord);
                if (mismatch != null) return CommandResult.Fail(mismatch);
                deliveryReverification = DeliveryLedgerHost.Reverify(doc, deliveryRecord, deliveryId, DateTime.UtcNow);
                deliveryGate = DeliveryLedger.PublishGate(deliveryRecord);
                if (!deliveryGate.Value<bool>("open"))
                    return CommandResult.FailWithDetail("The delivery publish gate is closed for '" + deliveryId + "': " +
                        string.Join("; ", deliveryGate["reasons"].Values<string>()) + ". Nothing was exported.",
                        new JObject { ["publish_gate"] = deliveryGate, ["reverification"] = deliveryReverification });
            }
            string planHash = DocumentGate.PlanHash(request, "pdf_name_pattern", "pdf_version", "format", "output_path", "view_ids", "schedule_id", "image_pixels", "overwrite", "preset", "dwg_setup",
                "ifc_version", "ifc_filter_view_id", "ifc_export_base_quantities", "ifc_split_walls_and_columns", "ifc_space_boundary_level",
                "nwc_scope", "nwc_coordinates", "nwc_parameters", "nwc_export_links", "nwc_export_element_ids", "nwc_export_room_geometry",
                "nwc_export_parts", "fbx_without_boundary_edges", "fbx_use_lod", "fbx_lod", "fbx_stop_on_error", "pdf_combine", "emit_manifest",
                "pdf_print", "units", "delivery_id", "information_container", "dialog_policy",
                "pdf_parts", "pdf_also_per_sheet", "pdf_archive_previous", "pdf_archive_folder", "pdf_qa", "require_existing_folder");
            // ---- The MATERIALISED plan: the SOURCES and the DESTINATION as they stand. --
            // An export publishes the model outward, and two ambient facts shape what
            // lands on disk: WHICH views/schedule the ids resolve to - a renamed or
            // re-cropped view exports different content under the same id - and the
            // overwrite decision, which was taken against files that existed at rehearsal
            // time. A file that appears at the destination after a no-overwrite rehearsal
            // makes the same request destroy data it promised not to touch; the plan
            // carries that file-existence fact so the apply refuses instead.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (View v in views)
            {
                resolvedPlan.Elements.Add(new PlannedElement
                {
                    UniqueId = SafePlanUid(v),
                    Category = "view",
                    TypeName = SafePlanName(v),
                    Action = PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string> { { "role", "export_source" } }
                });
            }
            if (schedule != null)
            {
                resolvedPlan.Elements.Add(new PlannedElement
                {
                    UniqueId = SafePlanUid(schedule),
                    Category = "schedule",
                    TypeName = SafePlanName(schedule),
                    Action = PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string> { { "role", "export_source" } }
                });
            }
            // Not an element, but ambient state the approval depends on: which candidate
            // files already exist. Sorted for stability; existence only, not size or time
            // - an export target being rewritten by its own previous run must not read as
            // drift.
            resolvedPlan.ContextFingerprint = "existing=" + string.Join(",",
                existing.Where(File.Exists).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) +
                ";overwrite=" + (overwrite ? "1" : "0") +
                // A patterned name carries {date}: the RESOLVED names are part of what was
                // approved, so a rehearsal that named A101_20261018_v1.pdf cannot apply
                // under the next day's name.
                (pdfPattern != null ? ";pdf_files=" + string.Join("|", pdfPaths) : "") + folderPlan?.Fingerprint;

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["format"] = format, ["output_path"] = output,
                    ["planned_files"] = new JArray(format == "pdf" ? pdfPaths : new[]{output}),
                    ["pdf_name_pattern"] = pdfPattern,
                    ["pdf_version_resolved"] = format == "pdf" && pdfPattern != null ? (JToken)pdfVersion : JValue.CreateNull(),
                    ["pdf_version_auto"] = autoVersion,
                    ["output_is_folder"] = outputIsFolder,
                    ["pdf_jobs"] = pdfJobs == null ? (JToken)JValue.CreateNull() : new JArray(Enumerable.Range(0, pdfJobs.Paths.Length).Select(i => (JToken)new JObject
                    {
                        ["path"] = pdfJobs.Paths[i], ["sheets"] = new JArray(pdfJobs.Groups[i].Select(ix => (JToken)Rid.Value(views[ix].Id)))
                    })),
                    ["archive_previous"] = format == "pdf" && pdfArchivePrevious
                        ? (JToken)new JObject { ["folder"] = System.IO.Path.Combine(folder, pdfArchiveFolder), ["would_move"] = new JArray(archiveCandidates), ["deleted"] = 0 }
                        : JValue.CreateNull(),
                    ["pdf_qa"] = pdfQa == null ? JValue.CreateNull() : new JObject { ["expected_fonts"] = new JArray(pdfQa.ExpectedFonts), ["runs_after_export"] = true },
                    ["delivery"] = deliveryGate == null ? (JToken)JValue.CreateNull() : new JObject { ["delivery_id"] = deliveryId, ["publish_gate"] = deliveryGate, ["reverification"] = deliveryReverification },
                    ["print_policy"] = format == "pdf" ? (JToken)new JObject { ["requested"] = printPolicy.Requested.DeepClone(),
                        ["effective"] = printPolicy.Canonical(), ["verified_from_output"] = new JArray(PdfPrintPolicy.VerifiableFromOutput),
                        ["means"] = "effective is what the exporter will be handed; only paper_format and orientation are proved from the produced pages" } : JValue.CreateNull(),
                    ["manifest_path"] = emitManifest ? manifestPath : null,
                    ["dialog_policy"] = ExportDialogPolicy.Block(dialogMode, dialogPolicyRequested, null),
                    ["views"] = new JArray(views.Select(v => new JObject { ["id"] = Rid.Value(v.Id), ["name"] = v.Name })),
                    ["schedule"] = schedule?.Name, ["overwrite"] = overwrite,
                    ["exporter_available"] = exporterAvailable,
                    ["note"] = "Nothing was exported and no file was created."
                };
                if (format == "pdf" && views.Count > 6 && Job.Ambient == null) result["advice"] = PdfVerification.Advice;
                if (container != null)
                    result["information_container"] = new JObject
                    {
                        ["validation"] = containerCheck.ToJson(), ["requested_output_path"] = requestedOutput,
                        ["container_output_path"] = output, ["sidecar_path"] = InformationContainer.SidecarPath(output)
                    };
                if (gateDecision.Requested) result["prevention"] = gateDecision.Prevention;
                if (exporterAvailable) DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, exporterAvailable,
                    exporterAvailable
                        ? "the token binds format, destination, options, the IDENTITY of every selected view and " +
                          "schedule as resolved now, and which destination files already exist - a source renamed or " +
                          "a file that appears under a no-overwrite approval refuses as a stale plan."
                        : "no usable token is issued because the optional Navisworks exporter is not installed",
                    // 4.2-N4 (CT-P1-4e): a set of many files takes long to read and approve.
                    format == "pdf" && pdfPaths.Length > 1 ? ExportDeliveryRules.TokenTtl(pdfPaths.Length) : (TimeSpan?)null);
                if (folderMissing) assumptions.Add(folderPlan.Assumption(false));
                return CommandResult.Ok(result);
            }
            if (!exporterAvailable)
                return CommandResult.Fail("The optional Autodesk Navisworks NWC exporter is not installed for this Revit version.");
            // Recomputed by THIS call, including the file-existence context.
            CommandResult refusal = ExportDirectoryPlan.Apply(folderPlan,
                () => DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null),
                () => DocumentGate.StillTheSame(app, gate.Fingerprint, Name), out JObject folderAssumption, out var directoryGuard);
            using var exportDirectory = directoryGuard;
            if (refusal != null) return refusal;

            assumptions.Add(folderAssumption);
            if (folderMissing && !overwrite)
            {
                var conflicts = format == "pdf" ? pdfPaths.Where(File.Exists).ToList() : CandidateFiles(format, output);
                if (emitManifest && File.Exists(manifestPath)) conflicts.Add(manifestPath);
                if (conflicts.Count > 0)
                    return CommandResult.Fail("Output already exists and overwrite=false: " + string.Join(", ", conflicts));
            }
            var before = Snapshot(folder, format, output, pdfPaths);
            bool apiAccepted = false;
            JObject pdfApplied = null;
            JObject scheduleRefresh = null;
            ExportIsolation isolation = null;
            JObject isolationReport = null, provenChanges = null; string isolationHeadline = null;
            CommandResult exportFailure = ExecuteExportAttempt(format, format == "pdf" ? pdfPaths : new[] { output },
                overwrite, File.Move, () =>
            {
              // Only the export call answers dialogs from the catalog; the scope is restored on any exit,
              // and a failure leads with the dialog that caused it instead of "exporter rejected output".
              using (Interference.WithExportDialogs(dialogMode))
              try
              {
                isolation = ExportIsolationRules.Isolates(format) ? ExportIsolation.Begin(app, doc, format) : null;
                switch (format)
                {
                    case "pdf":
                        apiAccepted = true;
                        // Schedules on the sheets are rebuilt first (renamed types printed their old names),
                        // inside a group rolled back after the files are written. See ScheduleRefresh.cs.
                        scheduleRefresh = ScheduleRefresh.Around(doc, views, () => {
                        for(int p=0;p<pdfPaths.Length;p++)
                        {
                            // 4.2-N4 (CT-P1-4e): progress per file, polled through job_status when run as a job.
                            CallProgress.Checkpoint("pdf " + System.IO.Path.GetFileName(pdfPaths[p]), p, pdfPaths.Length);
                            // Single-view calls give deterministic one-file-per-view names.
                            // Revit appends .pdf itself. Including the extension
                            // produces name.pdf.pdf and violates the approved path.
                            var pdf = new PDFExportOptions { Combine = true, FileName = System.IO.Path.GetFileNameWithoutExtension(pdfPaths[p]) };
                            pdfApplied = ApplyPrintPolicy(pdf, printPolicy);
                            bool accepted = doc.Export(folder, pdfJobs.Groups[p].Select(ix=>views[ix].Id).ToList(), pdf);
                            apiAccepted = apiAccepted && accepted;
                            if (!accepted) throw new InvalidOperationException("PDF exporter rejected output " + pdfPaths[p]);
                        }
                        });
                        break;
                    case "dwg":
                        apiAccepted = doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output),
                            new List<ElementId> { views[0].Id }, dwgOptions); break;
                    case "ifc":
                        var ifc = new IFCExportOptions
                        {
                            ExportBaseQuantities = request.Value<bool?>("ifc_export_base_quantities") == true,
                            WallAndColumnSplitting = request.Value<bool?>("ifc_split_walls_and_columns") == true,
                            SpaceBoundaryLevel = ifcSpaceBoundary,
                            FilterViewId = ifcFilterView?.Id ?? ElementId.InvalidElementId
                        };
                        ifc.FileVersion = ifcVersion;
                        // MEASURED on run 15: Revit's IFC exporter WRITES to the
                        // document (export marks) and throws 'Modifying is forbidden'
                        // without an open transaction. The transaction is the API's
                        // requirement, not a model edit of ours: it commits inside the
                        // export's isolation group, which is rolled back below, so the
                        // bookkeeping lasts exactly as long as the exporter needs it.
                        using (var ifcTx = new Transaction(doc, "Horizun: export IFC"))
                        {
                            ifcTx.Start();
                            apiAccepted = doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), ifc);
                            ifcTx.Commit();
                        }
                        break;
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
                        // Revit's defaults are a tab-separated file with a title row: Excel, Power BI and
                        // tabular_source read it as ONE column. A .csv is comma-separated, quoted, one header row.
                        schedule.Export(folder, System.IO.Path.GetFileName(output), new ViewScheduleExportOptions
                        {
                            FieldDelimiter = ",", TextQualifier = ExportTextQualifier.DoubleQuote, Title = false,
                            ColumnHeaders = ExportColumnHeaders.OneRow, HeadersFootersBlanks = false
                        }); apiAccepted = true; break;
                }
                return apiAccepted;
              }
              catch (Exception exportEx) when (Interference.Current != null && Interference.Current.ExportDecisions.Count > 0)
              {
                  throw new InvalidOperationException(ExportDialogPolicy.CauseFirst(Interference.Current.ExportDecisions, exportEx.Message), exportEx);
              }
            }, () =>
            {
                if (isolation != null)
                {
                    ExportIsolationRules.Facts isolationFacts = isolation.End();
                    isolationReport = ExportIsolationRules.Report(format, isolationFacts);
                    isolationHeadline = ExportIsolationRules.Headline(format, isolationFacts);
                    provenChanges = ExportIsolationRules.ProvenModelChanges(isolationFacts);
                }
            }, () => isolationReport, () => isolationHeadline,
                out var asideFiles, out var restoredPdfs, out var attemptAccepted);
            apiAccepted = attemptAccepted;
            JArray RestoreAsides() => RestorePdfAsides(asideFiles);
            if (exportFailure != null)
            {
                if (format == "pdf" && exportFailure.Detail != null)
                {
                    var failedAfter = Snapshot(folder, format, output, pdfPaths);
                    var failedProduced = ExportFileDiff.Diff(before, failedAfter).Produced;
                    exportFailure.Detail["verification"] = PdfVerification.Compare(pdfPaths, failedProduced.Select(x => new KeyValuePair<string, long>(x, failedAfter[x].Size)), rule: "export_exception");
                    return CommandResult.FailWithDetail(exportFailure.Error + ". " + PdfVerification.Advice, exportFailure.Detail);
                }
                return exportFailure;
            }
            IList<ExportDialogDecision> dialogDecisions = Interference.Current?.ExportDecisions;
            JObject DialogBlock() => ExportDialogPolicy.Block(dialogMode, dialogPolicyRequested, dialogDecisions);
            JObject WithIsolation(JObject detail)
            {
                if (isolationReport != null) detail["model_isolation"] = isolationReport;
                detail["dialog_policy"] = DialogBlock();
                return detail;
            }
            var after = Snapshot(folder, format, output, pdfPaths);
            (List<string> produced, List<string> unmeasured) = ExportFileDiff.Diff(before, after);
            // Nothing new was measured: the old PDFs go back, so the failure leaves them where they were.
            if (produced.Count == 0 && asideFiles.Count > 0) restoredPdfs = RestoreAsides();
            if (produced.Count == 0)
            {
                var noFilesDetail = new JObject { ["external_files_may_exist"]=true,["rollback_available"]=false,
                    ["planned_files"]=new JArray(format=="pdf"?pdfPaths:new[]{output}),
                    ["unmeasured_files"]=new JArray(unmeasured) };
                if (format == "pdf") noFilesDetail["verification"] = PdfVerification.Compare(pdfPaths, produced.Select(x => new KeyValuePair<string, long>(x, after[x].Size)));
                if (restoredPdfs != null) noFilesDetail["previous_pdfs_restored"] = restoredPdfs;
                return CommandResult.FailWithDetail(ExportDialogPolicy.CauseFirst(dialogDecisions, "Revit returned from export (accepted=" + apiAccepted +
                    "), but no new or changed non-empty file was measured in " + folder +
                    (unmeasured.Count > 0 ? ". " + unmeasured.Count + " matching file(s) existed before this call and could " +
                        "not be read then, so a change could not be proven either way: " + string.Join(", ", unmeasured) + "." : ".") +
                    " Success is not claimed." + (format == "pdf" ? " " + PdfVerification.Advice : "")),
                    WithIsolation(noFilesDetail));
            }
            // Non-PDF formats produce exactly one file per call (dwg/image/nwc(view) take
            // exactly one view_id, ifc/nwc(model)/schedule_csv take none, fbx combines every
            // 3D view_id into ONE .fbx) - so, unlike PDF's per-view pdfPaths, the expected
            // set here is always {output}. More than one matching produced file, as much as
            // fewer, is reported by name rather than folded into a bare count.
            string imageRenamedFrom = null;
            // overwrite: the output exists and was approved for replacement, but Revit still
            // writes the image under the view's name, never onto it - so replace it here.
            if (format == "image" && (!File.Exists(output) || overwrite))
            {
                // Revit names the image after the view (ExportFileDiff.ImageToRename); the one file
                // it produced is moved to the path the caller asked for, then measured there.
                string revitNamed = ExportFileDiff.ImageToRename(produced, output);
                if (revitNamed != null)
                {
                    try
                    {
                        if (File.Exists(output)) File.Replace(revitNamed, output, null); else File.Move(revitNamed, output);
                        produced = new List<string> { output }; imageRenamedFrom = revitNamed;
                    }
                    catch (Exception moveEx) { Log.Warn("image export: could not move '" + revitNamed + "' to '" + output + "': " + moveEx.Message); }
                }
            }
            if (format != "pdf")
            {
                (List<string> missing, List<string> extra) = ExportFileDiff.AgainstExpectedSingleFile(produced, output);
                if (missing.Count > 0 || extra.Count > 0)
                    return CommandResult.FailWithDetail("Expected exactly 1 produced file for format=" +
                        format + "; measured " + produced.Count + "." +
                        (missing.Count > 0 ? " Missing: " + string.Join(", ", missing) + "." : "") +
                        (extra.Count > 0 ? " Unexpected: " + string.Join(", ", extra) + "." : "") +
                        // Measured 2026-09-30: a plan showing loaded RVT links exports each link
                        // as its own <stem>-...rvt-N-... file beside the drawing. That is Revit's
                        // xref output, and dwg_xrefs is the argument that accounts for it.
                        (format == "dwg" && extra.Count > 0 && missing.Count == 0 && extra.All(p => MatchesOutput(format, output, p))
                            ? " Every unexpected file is named as a companion of this drawing: Revit writes the loaded RVT links a view shows (and a sheet's views) as separate xref files. Re-run with dwg_xrefs='bound' for one self-contained file, or 'linked' to keep the companions and have them verified."
                            : "") +
                        " Success is not claimed.",
                        WithIsolation(new JObject { ["external_files_may_exist"] = true, ["rollback_available"] = false,
                            ["planned_files"] = new JArray(new[] { output }), ["produced_files"] = new JArray(produced),
                            ["missing_files"] = new JArray(missing), ["unexpected_files"] = new JArray(extra),
                            ["unmeasured_files"] = new JArray(unmeasured) }));
            }

            var replacedPdfs = new JArray();
            var files = new JArray();
            foreach (string path in produced)
            {
                var info = new FileInfo(path);
                files.Add(new JObject { ["path"] = path, ["bytes"] = info.Length, ["last_write_utc"] = info.LastWriteTimeUtc.ToString("o") });
            }
            var exportResult = new JObject
            {
                ["format"] = format, ["api_accepted"] = apiAccepted, ["files_verified"] = produced.Count,
                ["requested_output_path"] = output, ["files"] = files,
                ["note"] = produced.Count == 1 ? "One produced file was re-read from disk." :
                    "Revit produced multiple sidecar/output files; every changed non-empty file is reported."
            };
            exportResult["dialog_policy"] = DialogBlock();
            if (format == "schedule_csv" && schedule != null)
            {
                // Re-read the header from disk and compare its columns with the schedule's visible fields.
                JObject csv = ScheduleCsvCheck(output, schedule);
                exportResult["csv"] = csv;
                if (csv.Value<bool?>("columns_match") != true)
                    return CommandResult.FailWithDetail("The schedule was exported, but its header row read back from " + output + " has " +
                        csv["header_columns"] + " comma-separated column(s) and the schedule shows " + csv["expected_columns"] +
                        ". Success is not claimed.", WithIsolation(new JObject { ["external_files_may_exist"] = true,
                            ["rollback_available"] = false, ["csv"] = csv, ["files"] = files }));
            }
            if (scheduleRefresh != null) exportResult["schedule_refresh"] = scheduleRefresh;
            if (imageRenamedFrom != null) exportResult["revit_named_it"] = imageRenamedFrom;
            if (unmeasured.Count > 0)
                exportResult["unmeasured_files"] = new JObject
                {
                    ["paths"] = new JArray(unmeasured),
                    ["means"] = "matching file(s) existed before this call and could not be read (or hashed) at that " +
                                "moment - a lock, a permission blip. Whether THIS export touched them cannot be proven " +
                                "either way, so they are reported here rather than folded into files_verified as new."
                };
            if (preset != null)
                exportResult["preset"] = VerifyPreset(preset, presetHash, produced);
            if (format == "pdf")
            {
                var verifiedPdf=new JArray();
                int? pagesExpected = null, pagesFound = null;
                try
                {
                    if (PdfVerification.Compare(pdfPaths, produced.Select(x => new KeyValuePair<string, long>(x, after[x].Size))).Value<string>("rule") != null) throw new InvalidOperationException("PDF file names do not match the requested package.");
                    var pageVerdicts=new List<JObject>(); var pageFiles=new List<string>();
                    for(int i=0;i<pdfPaths.Length;i++)
                    {
                        List<View> sources=pdfJobs.Groups[i].Select(ix=>views[ix]).ToList();
                        JObject file=DeliveryPdf.Inspect(pdfPaths[i],sources.Count);
                        file["source_views"]=new JArray(sources.Select(v=>new JObject {
                            ["id"]=Rid.Value(v.Id),["unique_id"]=v.UniqueId,["name"]=v.Name,
                            ["sheet_number"]=(v as ViewSheet)?.SheetNumber,
                            ["revision_ids"]=v is ViewSheet sheet ? new JArray(sheet.GetAllRevisionIds().Select(Rid.Value)) : new JArray() }));
                        verifiedPdf.Add(file);
                        pagesExpected = sources.Count; pagesFound = file.Value<int>("pages");
                        if (file.Value<bool>("page_count_verified")!=true) throw new InvalidOperationException("PDF page count mismatch.");
                        // Every produced page is judged against the print policy: the
                        // page geometry PdfPig read is compared with the requested paper
                        // (or with the sheet's own size for Default). This is the one
                        // place a PDF can testify about paper and orientation.
                        var pages=(JArray)file["page_geometry"];
                        var filePages=new JArray();
                        for(int pg=0;pg<pages.Count;pg++)
                        {
                            View source=pg<sources.Count?sources[pg]:null;
                            JObject verdict=printPolicy.VerifyPage(pages[pg].Value<double>("width_points"),pages[pg].Value<double>("height_points"),
                                SheetPoints(source), 2.0, TitleblockPoints(doc, source));
                            verdict["page"]=pages[pg]["page"].DeepClone();
                            verdict["source_view_id"]=source==null?(JToken)JValue.CreateNull():Rid.Value(source.Id);
                            filePages.Add(verdict); pageVerdicts.Add(verdict); pageFiles.Add(System.IO.Path.GetFileName(pdfPaths[i]));
                        }
                        file["page_verdicts"]=filePages;
                    }
                    // 4.2-N4 (CT-P1-4d, LP-P3-13c): read every produced file back for fonts, blank pages and text collisions.
                    JObject qaSummary = null;
                    if (pdfQa != null)
                    {
                        int qaClean = 0, qaPartial = 0;
                        int qaFile = 0;
                        foreach (JObject produced1 in verifiedPdf.OfType<JObject>())
                        {
                            JObject qa;
                            List<View> qaPages = qaFile < pdfJobs.Groups.Length ? pdfJobs.Groups[qaFile].Select(ix => views[ix]).ToList() : new List<View>();
                            qaFile++;
                            try { qa = PdfQa.Inspect(produced1.Value<string>("path"), ForPages(doc, pdfQa, qaPages)); }
                            catch (Exception qaEx) { qa = new JObject { ["verdict"] = "unreadable", ["reason"] = qaEx.Message }; }
                            produced1["qa"] = qa;
                            if (qa.Value<string>("verdict") == "clean") qaClean++;
                            else if (qa.Value<string>("verdict") == "partial") qaPartial++;
                        }
                        qaSummary = new JObject { ["files"] = verifiedPdf.Count, ["clean"] = qaClean, ["partial"] = qaPartial, ["with_findings"] = verifiedPdf.Count - qaClean - qaPartial, ["render_checked"] = false,
                            ["note"] = "findings are reported, not hidden and not fatal: the files exist as exported" };
                    }
                    // 4.2-N4 (CT-P1-4c): older versions move into the archive folder - moved, never deleted - and are re-read there.
                    JObject archiveReport = null;
                    if (pdfArchivePrevious)
                    {
                        string archiveDir = System.IO.Path.Combine(folder, pdfArchiveFolder);
                        var moved = new JArray(); var notMoved = new JArray();
                        foreach (string old in archiveCandidates)
                        {
                            try
                            {
                                Directory.CreateDirectory(archiveDir);
                                string target = System.IO.Path.Combine(archiveDir, System.IO.Path.GetFileName(old));
                                if (File.Exists(target)) { notMoved.Add(new JObject { ["file"] = old, ["reason"] = "a file with that name is already in the archive" }); continue; }
                                File.Move(old, target);
                                if (File.Exists(target) && !File.Exists(old)) moved.Add(new JObject { ["from"] = old, ["to"] = target });
                                else notMoved.Add(new JObject { ["file"] = old, ["reason"] = "the move could not be confirmed on disk" });
                            }
                            catch (Exception moveEx) { notMoved.Add(new JObject { ["file"] = old, ["reason"] = moveEx.Message }); }
                        }
                        archiveReport = new JObject { ["folder"] = archiveDir, ["moved"] = moved, ["not_moved"] = notMoved, ["deleted"] = 0 };
                    }
                    JObject printReport=printPolicy.Report(pdfApplied,pageVerdicts);
                    if (!printReport.Value<bool>("verifiable_options_held"))
                        // Every reason the report holds against the policy is named: a page
                        // that contradicts it (verified_mismatch), an option the exporter
                        // rewrote (applied_mismatch) and a page larger than its titleblock.
                        throw new PdfVerification.PolicyContradiction(PdfVerification.PolicyFailureMessage(printReport, idx =>
                        {
                            // idx is the page's position among ALL produced pages (each file numbers its own from 1).
                            try
                            {
                                if (idx < 0 || idx >= pageVerdicts.Count) return null;
                                JObject verdict = pageVerdicts[idx];
                                long sid = verdict.Value<long?>("source_view_id") ?? 0;
                                ViewSheet sheet = sid != 0 ? doc.GetElement(Rid.Make(sid)) as ViewSheet : null;
                                PdfSheetMap map = sheet != null ? SheetMap(doc, sheet, verdict.Value<int?>("page") ?? 0) : null;
                                return PdfVerification.PaperMismatchDetail(verdict, sheet != null ? sheet.SheetNumber + " - " + sheet.Name : null, map,
                                    printPolicy.PaperFormat, pageFiles[idx]);
                            }
                            catch { return null; }
                        }));
                    var manifest=new JObject { ["schema"]="horizun.delivery-manifest/1",["utc"]=DateTime.UtcNow.ToString("o"),
                        ["document"]=doc.Title,["combined"]=pdfCombine,["files"]=verifiedPdf,
                        ["version"]=pdfPattern!=null?(JToken)pdfVersion:JValue.CreateNull(),["version_auto"]=autoVersion,
                        ["parts"]=pdfParts,["also_per_sheet"]=pdfAlsoPerSheet,
                        ["archive"]=archiveReport==null?JValue.CreateNull():archiveReport,["qa"]=qaSummary==null?JValue.CreateNull():qaSummary,
                        ["print_policy"]=printReport,
                        ["source_mapping"]="export invocation mapping; page identity and visual content are not independently verified",
                        ["visual_approval"]="required",["package_verified"]=true };
                    exportResult["delivery"]=manifest;
                    if (emitManifest)
                    {
                        DeliveryPdf.WriteManifestVerified(manifestPath, manifest, overwrite);
                        exportResult["manifest_path"]=manifestPath;
                    }
                    if (deliveryRecord != null)
                    {
                        // The publish stage is recorded from what was just verified: the
                        // produced files and their hashes. A ledger write failure is
                        // reported, never hidden - the files exist either way.
                        DateTime now=DateTime.UtcNow; string why;
                        var facts=new JObject { ["idempotency_key"]=request.Value<string>("idempotency_key"),
                            ["files"]=new JArray(verifiedPdf.OfType<JObject>().Select(f=>new JObject { ["path"]=f["path"].DeepClone(),["sha256"]=f["sha256"].DeepClone(),["bytes"]=f["bytes"].DeepClone() })),
                            ["manifest_path"]=emitManifest?(JToken)manifestPath:JValue.CreateNull() };
                        JObject publishStage=((JArray)deliveryRecord["stages"]).OfType<JObject>().FirstOrDefault(st=>st.Value<string>("kind")==DeliveryLedger.KindPublish);
                        var ledgerNotes=new JArray();
                        if (publishStage==null) ledgerNotes.Add("the delivery has no publish stage to record");
                        else
                        {
                            string pk=publishStage.Value<string>("key");
                            foreach(string step in new[]{DeliveryLedger.InProgress,DeliveryLedger.Completed})
                            {
                                if (publishStage.Value<string>("status")==step) continue;
                                JObject stepFacts=step==DeliveryLedger.Completed?facts:null;
                                if (!DeliveryLedger.TryTransition(deliveryRecord,pk,step,stepFacts,now,out why)) { ledgerNotes.Add(why); break; }
                                try { DeliveryLedger.AppendEvent(FileJobSink.Instance,DeliveryLedgerHost.Directory(),deliveryId,DeliveryLedger.TransitionEvent(pk,step,stepFacts,now)); }
                                catch(Exception ex) { ledgerNotes.Add("ledger write failed: "+ex.Message); break; }
                            }
                        }
                        exportResult["delivery_ledger"]=new JObject { ["delivery_id"]=deliveryId,["publish_stage"]=publishStage?["status"]?.DeepClone(),
                            ["publish_gate_at_export"]=deliveryGate,["reverification"]=deliveryReverification,["notes"]=ledgerNotes };
                    }
                }
                catch(Exception ex) { return CommandResult.FailWithDetail(PdfVerification.FailureText(ex),
                    new JObject { ["verification"] = PdfVerification.Compare(pdfPaths, produced.Select(x => new KeyValuePair<string, long>(x, after[x].Size)), pagesExpected, pagesFound, ex.Message == "PDF page count mismatch." ? "page_count" : ex.Message == "PDF file names do not match the requested package." ? null : "pdf_inspection_or_policy"), ["files"]=files,["pdf_evidence"]=verifiedPdf,["external_files_may_exist"]=true,["rollback_available"]=false,
                        ["previous_pdfs_restored"] = RestoreAsides() }); }
            }
            // Keep old PDFs until all requested PDFs and their print policy pass verification.
            foreach (AsideFile moved in asideFiles)
            {
                try { File.Delete(moved.Aside); replacedPdfs.Add(moved.Original); }
                catch (Exception deleteEx) { replacedPdfs.Add(new JObject { ["path"] = moved.Original, ["old_copy_left_at"] = moved.Aside, ["reason"] = deleteEx.Message }); }
            }
            if (replacedPdfs.Count > 0) exportResult["replaced_existing_pdfs"] = replacedPdfs;
            if (container != null)
            {
                // The container names ONE file, the effective output. Revit not producing
                // it means no sidecar: the other files exist and are reported, but none of
                // them is sealed as the container.
                if (!produced.Contains(output, StringComparer.OrdinalIgnoreCase))
                    return CommandResult.FailWithDetail("The export ran but did not produce " + output + ", the file the information " +
                        "container names. No sidecar was written.", new JObject { ["files"] = files,
                        ["external_files_may_exist"] = true, ["rollback_available"] = false });
                try
                {
                    long containerBytes;
                    string containerSha = InformationContainer.Sha256File(output, out containerBytes);
                    JObject sidecar = InformationContainer.BuildSidecar(container, containerCheck, output, containerBytes, containerSha,
                        Name, doc.Title, hostYear.ToString(CultureInfo.InvariantCulture), DateTime.UtcNow,
                        new JObject { ["state"] = JValue.CreateNull(), ["format"] = format });
                    JObject evidence = InformationContainer.WriteSidecarVerified(output, sidecar);
                    exportResult["information_container"] = new JObject
                    {
                        ["name"] = containerCheck.Name, ["file"] = output, ["requested_output_path"] = requestedOutput,
                        ["sidecar"] = sidecar, ["verification"] = evidence, ["warnings"] = containerCheck.Warnings
                    };
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    return CommandResult.FailWithDetail("The export was produced and verified, but its information-container sidecar " +
                        "could not be written and verified: " + ex.Message, new JObject { ["files"] = files,
                        ["external_files_may_exist"] = true, ["rollback_available"] = false });
                }
            }
            if (gateDecision.Requested) exportResult["prevention"] = gateDecision.Prevention;
            if (isolationReport != null)
            {
                exportResult["model_isolation"] = isolationReport;
                if (provenChanges != null) exportResult["model_changes"] = provenChanges;
                if (isolationHeadline != null)
                {
                    // First key: a finding at the bottom of a long payload is not read.
                    var first = new JObject { ["attention"] = isolationHeadline };
                    foreach (JProperty p in exportResult.Properties()) first.Add(p.Name, p.Value);
                    exportResult = first;
                }
            }
            return CommandResult.Ok(exportResult);
        }

        /// <summary>
        /// Set every policy option on Revit's option object and READ EACH ONE BACK.
        /// The returned object is what the exporter was actually handed, not what
        /// was asked for; the two are compared in the print report.
        /// </summary>
        private static JObject ApplyPrintPolicy(PDFExportOptions pdf, PdfPrintPolicy policy)
        {
            pdf.PaperFormat = (ExportPaperFormat)Enum.Parse(typeof(ExportPaperFormat), policy.PaperFormat);
            pdf.PaperOrientation = policy.Orientation == "portrait" ? PageOrientationType.Portrait
                                 : policy.Orientation == "landscape" ? PageOrientationType.Landscape : PageOrientationType.Auto;
            // Margins and LowerLeft are one enum value in Revit's API (measured on 2026:
            // both are 1 and Margins reads back as LowerLeft), so the policy offers
            // lower_left and sets the offsets on it.
            pdf.PaperPlacement = policy.Placement == "lower_left" ? PaperPlacementType.LowerLeft : PaperPlacementType.Center;
            if (policy.Placement == "lower_left" && policy.OriginOffsetXFeet.HasValue)
            {
                pdf.OriginOffsetX = policy.OriginOffsetXFeet ?? 0;
                pdf.OriginOffsetY = policy.OriginOffsetYFeet ?? 0;
            }
            pdf.ZoomType = policy.Zoom == "zoom" ? ZoomType.Zoom : ZoomType.FitToPage;
            if (policy.Zoom == "zoom" && policy.ZoomPercentage.HasValue) pdf.ZoomPercentage = policy.ZoomPercentage.Value;
            pdf.ColorDepth = policy.ColorDepth == "black_line" ? ColorDepthType.BlackLine
                           : policy.ColorDepth == "grayscale" ? ColorDepthType.GrayScale : ColorDepthType.Color;
            pdf.RasterQuality = policy.RasterQuality == "low" ? RasterQualityType.Low
                              : policy.RasterQuality == "medium" ? RasterQualityType.Medium
                              : policy.RasterQuality == "presentation" ? RasterQualityType.Presentation : RasterQualityType.High;
            pdf.ExportQuality = (PDFExportQualityType)Enum.Parse(typeof(PDFExportQualityType), "DPI" + policy.ExportQualityDpi);
            pdf.AlwaysUseRaster = policy.AlwaysUseRaster;
            pdf.HideCropBoundaries = policy.HideCropBoundaries;
            pdf.HideScopeBoxes = policy.HideScopeBoxes;
            pdf.HideReferencePlane = policy.HideReferencePlanes;
            pdf.HideUnreferencedViewTags = policy.HideUnreferencedViewTags;
            pdf.MaskCoincidentLines = policy.MaskCoincidentLines;
            pdf.ReplaceHalftoneWithThinLines = policy.ReplaceHalftoneWithThinLines;
            pdf.ViewLinksInBlue = policy.ViewLinksInBlue;
            pdf.StopOnError = policy.StopOnError;
#if !REVIT2023 && !REVIT2024
            pdf.SetExportInBackground(policy.ExportInBackground);
#endif
            var applied = new JObject
            {
                ["paper_format"] = pdf.PaperFormat.ToString(),
                ["orientation"] = pdf.PaperOrientation == PageOrientationType.Portrait ? "portrait"
                                : pdf.PaperOrientation == PageOrientationType.Landscape ? "landscape" : "auto",
                ["placement"] = pdf.PaperPlacement == PaperPlacementType.LowerLeft ? "lower_left" : "center",
                ["origin_offset_x"] = pdf.PaperPlacement == PaperPlacementType.LowerLeft && policy.OriginOffsetXFeet.HasValue ? (JToken)pdf.OriginOffsetX : JValue.CreateNull(),
                ["origin_offset_y"] = pdf.PaperPlacement == PaperPlacementType.LowerLeft && policy.OriginOffsetYFeet.HasValue ? (JToken)pdf.OriginOffsetY : JValue.CreateNull(),
                ["zoom"] = pdf.ZoomType == ZoomType.Zoom ? "zoom" : "fit_to_page",
                ["zoom_percentage"] = pdf.ZoomType == ZoomType.Zoom ? (JToken)pdf.ZoomPercentage : JValue.CreateNull(),
                ["color_depth"] = pdf.ColorDepth == ColorDepthType.BlackLine ? "black_line"
                                : pdf.ColorDepth == ColorDepthType.GrayScale ? "grayscale" : "color",
                ["raster_quality"] = pdf.RasterQuality.ToString().ToLowerInvariant(),
                ["export_quality_dpi"] = int.Parse(pdf.ExportQuality.ToString().Substring(3), CultureInfo.InvariantCulture),
                ["always_use_raster"] = pdf.AlwaysUseRaster,
                ["hide_crop_boundaries"] = pdf.HideCropBoundaries,
                ["hide_scope_boxes"] = pdf.HideScopeBoxes,
                ["hide_reference_planes"] = pdf.HideReferencePlane,
                ["hide_unreferenced_view_tags"] = pdf.HideUnreferencedViewTags,
                ["mask_coincident_lines"] = pdf.MaskCoincidentLines,
                ["replace_halftone_with_thin_lines"] = pdf.ReplaceHalftoneWithThinLines,
                ["view_links_in_blue"] = pdf.ViewLinksInBlue,
                ["stop_on_error"] = pdf.StopOnError
            };
#if !REVIT2023 && !REVIT2024
            applied["export_in_background"] = pdf.GetExportInBackground();
#endif
            return applied;
        }

        /// <summary>A sheet's own paper size in points, from its outline; null for anything else.</summary>
        private static double[] SheetPoints(View view)
        {
            var sheet = view as ViewSheet;
            if (sheet == null) return null;
            try
            {
                BoundingBoxUV outline = sheet.Outline;
                if (outline == null) return null;
                double w = outline.Max.U - outline.Min.U, h = outline.Max.V - outline.Min.V;
                if (w <= 0 || h <= 0) return null;
                return new[] { PdfPrintPolicy.FeetToPoints(w), PdfPrintPolicy.FeetToPoints(h) };
            }
            catch { return null; }
        }

        /// <summary>
        /// The paper the titleblock DECLARES, in points: its SHEET_WIDTH / SHEET_HEIGHT
        /// parameters (instance first, then type). Not its bounding box - measured live
        /// 2026-09-08: the box of a titleblock includes its labels, so an overflowing
        /// sheet number widened the box exactly as much as it widened the page and the
        /// two agreed with each other. Null when there is not exactly one titleblock or
        /// the parameters cannot be read; the verdict then makes no composition claim.
        /// </summary>
        private static double[] TitleblockPoints(Document doc, View view)
        {
            var sheet = view as ViewSheet;
            if (sheet == null) return null;
            try
            {
                var blocks = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType().ToElements();
                if (blocks.Count != 1) return null;
                Element block = blocks[0];
                double? w = DeclaredLength(block, BuiltInParameter.SHEET_WIDTH), h = DeclaredLength(block, BuiltInParameter.SHEET_HEIGHT);
                if (!w.HasValue || !h.HasValue)
                {
                    Element type = doc.GetElement(block.GetTypeId());
                    if (type != null)
                    {
                        w = w ?? DeclaredLength(type, BuiltInParameter.SHEET_WIDTH);
                        h = h ?? DeclaredLength(type, BuiltInParameter.SHEET_HEIGHT);
                    }
                }
                if (!w.HasValue || !h.HasValue || w.Value <= 0 || h.Value <= 0) return null;
                return new[] { PdfPrintPolicy.FeetToPoints(w.Value), PdfPrintPolicy.FeetToPoints(h.Value) };
            }
            catch { return null; }
        }

        private static double? DeclaredLength(Element e, BuiltInParameter bp)
        {
            try
            {
                Parameter p = e.get_Parameter(bp);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                return p.AsDouble();
            }
            catch { return null; }
        }

        private static DWGExportOptions BuildDwgOptions(Document doc, JObject request, out string refusal)
        {
            refusal = null;
            var options = new DWGExportOptions();
            JObject setup = request["dwg_setup"] as JObject;
            if (setup != null)
            {
                string name = setup.Value<string>("name");
                if (setup["layers"] != null || setup["source"] != null)
                {
                    refusal = "format dwg EXPORTS with a setup; writing its layers or creating it is format dwg_layers. Nothing was exported.";
                    return null;
                }
                options = string.IsNullOrWhiteSpace(name) ? null : DWGExportOptions.GetPredefinedOptions(doc, name);
                if (options == null)
                {
                    refusal = "dwg_setup '" + name + "' is not a DWG export setup in this document. It has: " +
                              string.Join(", ", ExportDWGSettings.ListNames(doc)) + ". Nothing was exported.";
                    return null;
                }
            }
            string acad = request.Value<string>("acad_version");
            if (acad == "2013") options.FileVersion = ACADVersion.R2013;
            else if (acad == "2018") options.FileVersion = ACADVersion.R2018;
            return options;
        }

        /// <summary>
        /// Prove each preset option from the produced file where the format admits
        /// proof; name the rest requested_unverifiable. A claim per option, never a
        /// blanket "applied".
        /// </summary>
        private static JObject VerifyPreset(ExportPreset preset, string presetHash, List<string> produced)
        {
            var optionRows = new JArray();
            bool allProvableHeld = true;
            int provable = 0, proven = 0;
            foreach (KeyValuePair<string, string> option in preset.Options)
            {
                var row = new JObject { ["option"] = option.Key, ["requested"] = option.Value };
                if (!ExportPresetRules.Verifiable(preset.Format, option.Key))
                {
                    row["status"] = "requested_unverifiable";
                    row["reason"] = "the produced format carries no readable trace of this option; it was passed " +
                                    "to the exporter and is NOT claimed as verified.";
                    optionRows.Add(row);
                    continue;
                }
                string readBack = null;
                try
                {
                    string first = produced.FirstOrDefault();
                    switch (option.Key)
                    {
                        case "ifc_version":
                        {
                            string head = first == null ? null : ReadHeadText(first, 4096);
                            string schema = ExportPresetRules.IfcSchemaOf(head);
                            readBack = schema;
                            row["verified"] = schema != null &&
                                schema.StartsWith(option.Value, StringComparison.OrdinalIgnoreCase);
                            break;
                        }
                        case "acad_version":
                        {
                            byte[] head = first == null ? null : ReadHeadBytes(first, 6);
                            readBack = ExportPresetRules.DwgVersionOf(head);
                            row["verified"] = string.Equals(readBack, option.Value, StringComparison.Ordinal);
                            break;
                        }
                        case "pixel_size":
                        {
                            byte[] head = first == null ? null : ReadHeadBytes(first, 24);
                            int width = ExportPresetRules.PngWidthOf(head);
                            readBack = width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            row["verified"] = width.ToString(System.Globalization.CultureInfo.InvariantCulture) == option.Value;
                            break;
                        }
                        case "combine":
                        {
                            readBack = produced.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " file(s)";
                            row["verified"] = option.Value == "true" ? produced.Count == 1 : produced.Count > 0;
                            row["note"] = "Exact expected-file and page counts are verified separately by delivery evidence.";
                            break;
                        }
                    }
                }
                catch (Exception ex) { row["verified"] = false; row["error"] = ex.Message; }
                row["read_back"] = readBack;
                provable++;
                // A verifiable key this switch has no reader for leaves verified unset: that is
                // an option nobody proved, and it used to leave the verdict true.
                if ((bool?)row["verified"] != true) allProvableHeld = false;
                else proven++;
                row["status"] = row["status"] ?? ((bool?)row["verified"] == true ? "verified" : "failed");
                optionRows.Add(row);
            }
            return new JObject
            {
                ["name"] = preset.Name, ["format"] = preset.Format, ["sha256"] = presetHash,
                // Held only when at least one option was provable and every provable one was
                // proved: a preset of unverifiable options proves nothing, and says so.
                ["options"] = optionRows, ["all_provable_options_held"] = allProvableHeld && provable > 0,
                ["provable_options"] = provable, ["proven_options"] = proven
            };
        }

        private static string ReadHeadText(string path, int bytes)
        {
            using (var stream = File.OpenRead(path))
            {
                var buffer = new byte[Math.Min(bytes, (int)Math.Min(stream.Length, int.MaxValue))];
                int read = stream.Read(buffer, 0, buffer.Length);
                return System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            }
        }

        private static byte[] ReadHeadBytes(string path, int bytes)
        {
            using (var stream = File.OpenRead(path))
            {
                var buffer = new byte[Math.Min(bytes, (int)Math.Min(stream.Length, int.MaxValue))];
                int read = stream.Read(buffer, 0, buffer.Length);
                Array.Resize(ref buffer, read);
                return buffer;
            }
        }

        /// <summary>What pdf_name_pattern may read from one exported view.</summary>
        private static PdfNameSource NameSource(View v)
        {
            var sheet = v as ViewSheet;
            string name = null;
            try { name = v.Name; } catch { }
            return new PdfNameSource
            {
                ViewId = Rid.Value(v.Id),
                ViewName = name,
                SheetNumber = sheet == null ? null : (sheet.SheetNumber ?? ""),
                SheetName = sheet == null ? null : (name ?? "")
            };
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
        /// <summary>Guarded reads: a plan must never fail while MEASURING.</summary>
        private static string SafePlanUid(Element e)
        {
            try { return e == null ? null : e.UniqueId; } catch { return null; }
        }

        private static string SafePlanName(Element e)
        {
            try { return e == null ? null : e.Name; } catch { return "<unreadable>"; }
        }

        private static List<string> CandidateFiles(string format, string output)
        {
            string folder = System.IO.Path.GetDirectoryName(output);
            return Directory.GetFiles(folder).Where(path => MatchesOutput(format, output, path)).ToList();
        }
        /// <summary>The header of an exported schedule CSV: its comma-separated columns against the visible fields.</summary>
        private static JObject ScheduleCsvCheck(string path, ViewSchedule schedule)
        {
            int expected = -1;
            try
            {
                ScheduleDefinition def = schedule.Definition;
                expected = def.GetFieldOrder().Count(id => !def.GetField(id).IsHidden);
            }
            catch { }
            string header = null;
            try { header = File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)); } catch { }
            int columns = header == null ? 0 : TabularRules.CountCsvFields(header, ',');
            return new JObject
            {
                ["delimiter"] = ",",
                ["text_qualifier"] = "\"",
                ["title_row"] = false,
                ["header_columns"] = columns,
                ["expected_columns"] = expected < 0 ? (JToken)JValue.CreateNull() : expected,
                ["columns_match"] = expected >= 0 && header != null && columns == expected
            };
        }

        private static bool MatchesOutput(string format, string output, string path)
        {
            string wantedStem = System.IO.Path.GetFileNameWithoutExtension(output);
            string actualStem = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!ExportFileDiff.StemMatches(wantedStem, actualStem)) return false;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            switch (format)
            {
                case "pdf": return ext == ".pdf";
                case "dwg": return ext == ".dwg";
                case "ifc": return ext == ".ifc";
                case "nwc": return ext == ".nwc";
                case "fbx": return ext == ".fbx";
                case "dgn": return ext == ".dgn";
                case "dwfx": return ext == ".dwfx";
                case "gbxml": return ext == ".xml";
                case "cobie": return ext == ".xlsx";
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
                case "dgn": return ext == ".dgn";
                case "dwfx": return ext == ".dwfx";
                case "gbxml": return ext == ".xml";
                case "cobie": return ext == ".xlsx";
                case "image": return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".tif" || ext == ".tiff";
                case "schedule_csv": return ext == ".csv" || ext == ".txt";
                case "dwg_layers": case "ifc_setup": return ext == ".json";
                default: return false;
            }
        }
        private static string ExpectedExtensionDescription(string format)
        {
            if (format == "image") return ".png, .jpg, .jpeg, .bmp, .tif or .tiff";
            if (format == "schedule_csv") return ".csv or .txt";
            if (format == "dwg_layers" || format == "ifc_setup") return ".json";
            if (format == "gbxml") return ".xml";
            if (format == "cobie") return ".xlsx";
            return "." + format;
        }
        /// <summary>
        /// Only files this export could plausibly touch (MatchesOutput/the exact
        /// PDF paths) are snapshotted - bounded cost even in a folder that also
        /// holds other people's large CAD files this call never looks at.
        ///
        /// MEASURED gap this closes: the old version dropped a file from the
        /// snapshot entirely when its FileInfo threw (locked, permission blip) -
        /// so a pre-existing file that was merely unreadable AT THAT INSTANT came
        /// back missing from 'before', and the diff then called it NEW. Now every
        /// matching path Directory.GetFiles returned gets an entry: Existed is
        /// always true for it, and Readable is true only when size, mtime AND a
        /// content hash could all be measured. The comparison itself is Revit-free
        /// (Core/ExportFileDiff.cs) and unit-tested without a Revit in the room.
        /// </summary>
        private static Dictionary<string, ExportFileStamp> Snapshot(string folder, string format, string output, string[] pdfPaths)
        {
            var result = new Dictionary<string, ExportFileStamp>(StringComparer.OrdinalIgnoreCase);
            string[] files;
            try { files = Directory.GetFiles(folder); } catch { return result; }
            foreach (string file in files)
            {
                bool relevant = format == "pdf"
                    ? string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase)
                    : MatchesOutput(format, output, file);
                if (!relevant) continue;
                var stamp = new ExportFileStamp { Existed = true };
                try
                {
                    var f = new FileInfo(file);
                    stamp.Size = f.Length;
                    stamp.Mtime = f.LastWriteTimeUtc.Ticks;
                    stamp.Hash = FileHash(file);
                    stamp.Readable = true;
                }
                catch { /* Existed stays true; Size/Mtime/Hash stay unmeasured. */ }
                result[file] = stamp;
            }
            return result;
        }

        /// <summary>SHA-256 of the file's bytes. Bounded to the handful of matching output/sidecar files, never the whole folder.</summary>
        private static string FileHash(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(path))
                return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        private static T ParseEnum<T>(string raw, T fallback, string field) where T : struct
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (Enum.TryParse(raw, true, out T value) && Enum.IsDefined(typeof(T), value)) return value;
            throw new ArgumentException(field + " has unsupported value '" + raw + "'.");
        }
    }
}
