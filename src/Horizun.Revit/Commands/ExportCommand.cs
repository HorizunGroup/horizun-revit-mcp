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
                                : property.Value.ToString()));
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
            if (container != null && format == "pdf" && !pdfCombine)
                return CommandResult.Fail("information_container names ONE file; pdf_combine=false produces one file per view. " +
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
            string[] pdfPaths = format == "pdf" ? DeliveryPdf.Paths(output,pdfCombine,views.Select(v=>Rid.Value(v.Id))) : new string[0];
            string manifestPath = output + ".manifest.json";
            List<string> existing = format == "pdf" ? pdfPaths.ToList() : CandidateFiles(format, output);
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
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "view_ids", "schedule_id", "image_pixels", "overwrite", "preset",
                "ifc_version", "ifc_filter_view_id", "ifc_export_base_quantities", "ifc_split_walls_and_columns", "ifc_space_boundary_level",
                "nwc_scope", "nwc_coordinates", "nwc_parameters", "nwc_export_links", "nwc_export_element_ids", "nwc_export_room_geometry",
                "nwc_export_parts", "fbx_without_boundary_edges", "fbx_use_lod", "fbx_lod", "fbx_stop_on_error", "pdf_combine", "emit_manifest",
                "pdf_print", "units", "delivery_id", "information_container");
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
                ";overwrite=" + (overwrite ? "1" : "0");

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["format"] = format, ["output_path"] = output,
                    ["planned_files"] = new JArray(format == "pdf" ? pdfPaths : new[]{output}),
                    ["delivery"] = deliveryGate == null ? (JToken)JValue.CreateNull() : new JObject { ["delivery_id"] = deliveryId, ["publish_gate"] = deliveryGate, ["reverification"] = deliveryReverification },
                    ["print_policy"] = format == "pdf" ? (JToken)new JObject { ["requested"] = printPolicy.Requested.DeepClone(),
                        ["effective"] = printPolicy.Canonical(), ["verified_from_output"] = new JArray(PdfPrintPolicy.VerifiableFromOutput),
                        ["means"] = "effective is what the exporter will be handed; only paper_format and orientation are proved from the produced pages" } : JValue.CreateNull(),
                    ["manifest_path"] = emitManifest ? manifestPath : null,
                    ["views"] = new JArray(views.Select(v => new JObject { ["id"] = Rid.Value(v.Id), ["name"] = v.Name })),
                    ["schedule"] = schedule?.Name, ["overwrite"] = overwrite,
                    ["exporter_available"] = exporterAvailable,
                    ["note"] = "Nothing was exported and no file was created."
                };
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
                        : "no usable token is issued because the optional Navisworks exporter is not installed");
                return CommandResult.Ok(result);
            }
            if (!exporterAvailable)
                return CommandResult.Fail("The optional Autodesk Navisworks NWC exporter is not installed for this Revit version.");
            // Recomputed by THIS call, including the file-existence context.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            var before = Snapshot(folder);
            bool apiAccepted = false;
            JObject pdfApplied = null;
            try
            {
                switch (format)
                {
                    case "pdf":
                        apiAccepted = true;
                        for(int p=0;p<pdfPaths.Length;p++)
                        {
                            // Single-view calls give deterministic one-file-per-view names.
                            // Revit appends .pdf itself. Including the extension
                            // produces name.pdf.pdf and violates the approved path.
                            var pdf = new PDFExportOptions { Combine = true, FileName = System.IO.Path.GetFileNameWithoutExtension(pdfPaths[p]) };
                            pdfApplied = ApplyPrintPolicy(pdf, printPolicy);
                            bool accepted = doc.Export(folder, pdfCombine ? views.Select(v=>v.Id).ToList() : new List<ElementId>{views[p].Id}, pdf);
                            apiAccepted = apiAccepted && accepted;
                            if (!accepted) throw new InvalidOperationException("PDF exporter rejected output " + pdfPaths[p]);
                        }
                        break;
                    case "dwg":
                        apiAccepted = doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output),
                            new List<ElementId> { views[0].Id }, BuildDwgOptions(request)); break;
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
                        // requirement, not a model edit of ours; it commits whatever
                        // bookkeeping the exporter insists on.
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
                        schedule.Export(folder, System.IO.Path.GetFileName(output), new ViewScheduleExportOptions()); apiAccepted = true; break;
                }
            }
            catch (Exception ex) { return CommandResult.FailWithDetail("Revit export failed: " + ex.Message,
                new JObject { ["external_files_may_exist"]=true,["rollback_available"]=false,
                    ["planned_files"]=new JArray(format=="pdf"?pdfPaths:new[]{output}) }); }

            var after = Snapshot(folder);
            List<string> produced = after.Where(kv => kv.Value.Size > 0 &&
                    (format == "pdf" ? pdfPaths.Contains(kv.Key,StringComparer.OrdinalIgnoreCase) : MatchesOutput(format, output, kv.Key)) &&
                    (!before.TryGetValue(kv.Key, out Stamp old) || old.Size != kv.Value.Size || old.Mtime != kv.Value.Mtime))
                .Select(kv => kv.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (produced.Count == 0)
                return CommandResult.FailWithDetail("Revit returned from export (accepted=" + apiAccepted +
                    "), but no new or changed non-empty file was measured in " + folder + ". Success is not claimed.",
                    new JObject { ["external_files_may_exist"]=true,["rollback_available"]=false,
                        ["planned_files"]=new JArray(format=="pdf"?pdfPaths:new[]{output}) });

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
            if (preset != null)
                exportResult["preset"] = VerifyPreset(preset, presetHash, produced);
            if (format == "pdf")
            {
                var verifiedPdf=new JArray();
                try
                {
                    if (produced.Count!=pdfPaths.Length) throw new InvalidOperationException("Not every requested PDF was produced or changed.");
                    var pageVerdicts=new List<JObject>();
                    for(int i=0;i<pdfPaths.Length;i++)
                    {
                        JObject file=DeliveryPdf.Inspect(pdfPaths[i],pdfCombine?views.Count:1);
                        List<View> sources=pdfCombine?views:new List<View>{views[i]};
                        file["source_views"]=new JArray(sources.Select(v=>new JObject {
                            ["id"]=Rid.Value(v.Id),["unique_id"]=v.UniqueId,["name"]=v.Name,
                            ["sheet_number"]=(v as ViewSheet)?.SheetNumber,
                            ["revision_ids"]=v is ViewSheet sheet ? new JArray(sheet.GetAllRevisionIds().Select(Rid.Value)) : new JArray() }));
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
                            filePages.Add(verdict); pageVerdicts.Add(verdict);
                        }
                        file["page_verdicts"]=filePages;
                        verifiedPdf.Add(file);
                    }
                    JObject printReport=printPolicy.Report(pdfApplied,pageVerdicts);
                    if (!printReport.Value<bool>("verifiable_options_held"))
                        // Every reason the report holds against the policy is named: a page
                        // that contradicts it (verified_mismatch), an option the exporter
                        // rewrote (applied_mismatch) and a page larger than its titleblock.
                        throw new InvalidOperationException("The produced PDF contradicts the print policy: " +
                            string.Join("; ",((JArray)printReport["options"]).Where(r=>r.Value<string>("status")==PdfPrintPolicy.StatusVerifiedMismatch
                                                                                    || r.Value<string>("status")==PdfPrintPolicy.StatusAppliedMismatch)
                                .Select(r=>r.Value<string>("option")+" ("+r.Value<string>("status")+") - "+r.Value<string>("reason"))
                                .Concat(((JArray)printReport["composition"]["pages_exceeding_titleblock"]).Select(c=>"page "+c["page"]+" (sheet "+c["source_view_id"]+") - "+c.Value<string>("reason")))));
                    var manifest=new JObject { ["schema"]="horizun.delivery-manifest/1",["utc"]=DateTime.UtcNow.ToString("o"),
                        ["document"]=doc.Title,["combined"]=pdfCombine,["files"]=verifiedPdf,
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
                catch(Exception ex) { return CommandResult.FailWithDetail("PDF package verification failed: "+ex.Message,
                    new JObject { ["files"]=files,["pdf_evidence"]=verifiedPdf,["external_files_may_exist"]=true,["rollback_available"]=false }); }
            }
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

        private static DWGExportOptions BuildDwgOptions(JObject request)
        {
            var options = new DWGExportOptions();
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
                if (row["verified"] != null && !(bool)row["verified"]) allProvableHeld = false;
                row["status"] = row["status"] ?? ((bool?)row["verified"] == true ? "verified" : "failed");
                optionRows.Add(row);
            }
            return new JObject
            {
                ["name"] = preset.Name, ["format"] = preset.Format, ["sha256"] = presetHash,
                ["options"] = optionRows, ["all_provable_options_held"] = allProvableHeld
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
