// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_deliver_ifc - A VERIFIED IFC DELIVERY IN ONE CALL.
//
//   1. optional IDS pre-check over the live model (the validate_ids pre-check,
//      not a second evaluator) - ADVISORY: a Revit parameter is not evidence of
//      an IFC property set, so it never decides readiness;
//   2. the export, with every option explicit: version, filter view, base
//      quantities, wall/column splitting, space boundaries, the exporter's own
//      user-defined property-set file, and the coordinate basis - beside what
//      the model's georeference is today, so a person sees what the file will
//      be placed by;
//   3. the IDS validation of the EXPORTED FILE - the delivery's verdict is the
//      file's, not the model's;
//   4. every property the mapping declares, looked for in the exported file,
//      with n-of-m coverage and the GlobalIds of what is missing;
//   5. optionally a BCF of the IDS failures, re-read like the ledger's BCF;
//   6. gates, and deliverable_ready only when every requested gate passed.
//
// A dry run (the default) returns the plan and writes nothing. The apply writes
// the IFC (and the BCF) OUTSIDE the model; Revit's exporter also insists on a
// transaction for its own export bookkeeping, measured on horizun_export, which
// is why this is classified exactly like horizun_export.
//
// NOTHING IS REPORTED AS PASSED THAT WAS NOT RE-READ: the file's existence,
// bytes and SHA-256, its header and trailer, the IDS outcome and the property
// sets all come from reading the produced file back.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class DeliverIfcCommand : ICommand
    {
        public string Name => "horizun_deliver_ifc";
        public string Description => "Verified IFC delivery: precheck, export, header, IDS on the file, property-set coverage, BCF.";

        private const double FtToMm = 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            // ---- where, and under which name -------------------------------------
            string folder = request.Value<string>("output_folder");
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
                return CommandResult.Fail("output_folder must be an absolute folder path.");
            try { folder = Path.GetFullPath(folder); }
            catch (Exception ex) { return CommandResult.Fail("output_folder is invalid: " + ex.Message); }
            if (!Directory.Exists(folder))
                return CommandResult.Fail("output_folder does not exist: " + folder + ". It is not created implicitly.");

            string composed, nameError;
            string stem = IfcDeliveryRules.ResolveStem(request.Value<string>("output_name"),
                request["information_container"] as JObject, out composed, out nameError);
            if (stem == null) return CommandResult.Fail(nameError + " Nothing was exported.");
            string ifcPath = Path.Combine(folder, stem + ".ifc");
            // A container delivery ends with a sidecar next to the IFC. One that already exists
            // belongs to an earlier delivery and is never replaced: refuse before exporting.
            ContainerSpec containerSpec = null;
            ContainerValidation containerCheck = null;
            if (request["information_container"] is JObject containerJson)
            {
                containerSpec = InformationContainer.ParseSpec(containerJson);
                containerCheck = InformationContainer.Validate(containerSpec, true);
                if (File.Exists(InformationContainer.SidecarPath(ifcPath)))
                    return CommandResult.Fail("An information-container sidecar already exists at " +
                        InformationContainer.SidecarPath(ifcPath) + "; a sealed delivery is never overwritten. Nothing was exported.");
            }
            string bcfPath = IfcDeliveryRules.BcfPathFor(ifcPath);

            // ---- the exporter options, every one explicit --------------------------
            string version = request.Value<string>("ifc_version");
            if (string.IsNullOrWhiteSpace(version) || !IfcDeliveryRules.VersionSchemaFamily.ContainsKey(version))
                return CommandResult.Fail("ifc_version is required and must be one of: " +
                    string.Join(", ", IfcDeliveryRules.VersionSchemaFamily.Keys) + ". A delivery does not take the exporter's default.");
            IFCVersion ifcVersion;
            if (!Enum.TryParse(version, false, out ifcVersion) || !Enum.IsDefined(typeof(IFCVersion), ifcVersion))
                return CommandResult.Fail("This Revit (" + app.Application.VersionNumber + ") has no IFCVersion." + version +
                                          ". Choose another ifc_version. Nothing was exported.");

            View filterView = null;
            if (request["ifc_filter_view_id"] != null)
            {
                long raw = request.Value<long>("ifc_filter_view_id");
                if (!Rid.CanRepresent(raw) || !(doc.GetElement(Rid.Make(raw)) is View found) || found.IsTemplate)
                    return CommandResult.Fail("ifc_filter_view_id must identify a non-template view.");
                filterView = found;
            }
            bool baseQuantities = request.Value<bool?>("export_base_quantities") == true;
            bool splitWalls = request.Value<bool?>("split_walls_and_columns") == true;
            int spaceBoundaries = request.Value<int?>("space_boundary_level") ?? 1;
            if (spaceBoundaries < 0 || spaceBoundaries > 2) return CommandResult.Fail("space_boundary_level must be 0..2.");
            bool? commonPsets = request.Value<bool?>("export_ifc_common_property_sets");
            bool? internalPsets = request.Value<bool?>("export_internal_revit_property_sets");
            string placementArg = request.Value<string>("site_placement");
            string placementOption = null;
            if (placementArg != null && !IfcDeliveryRules.SitePlacementOption.TryGetValue(placementArg, out placementOption))
                return CommandResult.Fail("site_placement must be one of: " + string.Join(", ", IfcDeliveryRules.SitePlacementOption.Keys) + ".");

            // ---- the mapping: parsed BEFORE anything is exported ------------------
            string mappingPath = request.Value<string>("pset_mapping_path");
            PsetMappingFile mapping = null;
            string mappingSha = null;
            if (mappingPath != null)
            {
                if (!Path.IsPathRooted(mappingPath)) return CommandResult.Fail("pset_mapping_path must be absolute.");
                string mappingError;
                mapping = PsetMapping.Read(mappingPath, out mappingError);
                if (mapping == null) return CommandResult.Fail("pset_mapping_path refused: " + mappingError + " Nothing was exported.");
                mappingSha = FileSha(mappingPath);
            }
            double minCoverage = request.Value<double?>("pset_min_coverage") ?? 1.0;
            if (minCoverage < 0 || minCoverage > 1) return CommandResult.Fail("pset_min_coverage must be 0..1.");
            if (mapping == null && request["pset_min_coverage"] != null)
                return CommandResult.Fail("pset_min_coverage needs pset_mapping_path.");

            // ---- the IDS ----------------------------------------------------------
            string idsPath = request.Value<string>("ids_path");
            IdsFile ids = null;
            string idsSha = null;
            if (idsPath != null)
            {
                string idsError;
                ids = IdsReader.Read(idsPath, out idsError);
                if (ids == null) return CommandResult.Fail("ids_path refused: " + idsError + " Nothing was exported.");
                if (ids.Specifications.Count == 0)
                    return CommandResult.Fail("'" + idsPath + "' holds no specification; an empty demand cannot be validated. Nothing was exported.");
                idsSha = FileSha(idsPath);
            }
            bool precheck = request.Value<bool?>("precheck") ?? (ids != null);
            bool bcf = request.Value<bool?>("bcf") == true;
            if ((precheck || bcf) && ids == null)
                return CommandResult.Fail("precheck and bcf need ids_path: both are about an IDS.");
            int maxFindings = request.Value<int?>("max_findings") ?? 100;
            if (maxFindings < 1 || maxFindings > 5000) return CommandResult.Fail("max_findings must be 1..5000.");

            bool overwrite = request.Value<bool?>("overwrite") == true;
            var targets = new List<string> { ifcPath };
            if (bcf) targets.Add(bcfPath);
            if (!overwrite && targets.Any(File.Exists))
                return CommandResult.Fail("Output already exists and overwrite=false: " + string.Join(", ", targets.Where(File.Exists)));

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            JObject georeference = ReadGeoreference(doc);
            JArray options = EffectiveOptions(version, filterView, baseQuantities, splitWalls, spaceBoundaries,
                                              commonPsets, internalPsets, placementArg, placementOption, mappingPath);

            // ---- 1. the pre-check: the validate_ids evaluator, advisory -----------
            var gates = new List<DeliveryGate>();
            JObject precheckJson = null;
            if (precheck)
            {
                IdsReport pre = IdsRevitPrecheck.Precheck(doc, ids, idsPath, null);
                precheckJson = pre.ToJson(maxFindings);
                DeliveryGate g = IfcDeliveryRules.IdsGate(pre, null);
                g.Name = "precheck";
                g.Advisory = true;
                g.Reason = "ADVISORY (model, evidence revit_precheck): " + g.Reason +
                           " It never decides readiness - property-set membership is decided by the export, and the " +
                           "delivery's verdict is ids_validate on the file.";
                gates.Add(g);
            }

            string planHash = DocumentGate.PlanHash(request, "output_folder", "output_name", "information_container",
                "ifc_version", "ifc_filter_view_id", "export_base_quantities", "split_walls_and_columns",
                "space_boundary_level", "export_ifc_common_property_sets", "export_internal_revit_property_sets",
                "site_placement", "pset_mapping_path", "pset_min_coverage", "ids_path", "precheck", "bcf", "overwrite");
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            if (filterView != null)
                resolvedPlan.Elements.Add(new PlannedElement
                {
                    UniqueId = SafeUid(filterView), Category = "view", TypeName = SafeName(filterView),
                    Action = PlannedAction.Modify, BeforeValues = new Dictionary<string, string> { { "role", "ifc_filter_view" } }
                });
            // The CONTENT of the mapping and the IDS joins the approval: an edited mapping
            // exports different sets under the same path, and an edited IDS judges by a
            // different demand. Existence of the targets joins it for the same reason as on
            // horizun_export - a file that appears after a no-overwrite rehearsal refuses.
            resolvedPlan.ContextFingerprint = "existing=" + string.Join(",", targets.Where(File.Exists)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) + ";overwrite=" + (overwrite ? "1" : "0") +
                ";mapping=" + (mappingSha ?? "-") + ";ids=" + (idsSha ?? "-");

            if (dryRun)
            {
                var plan = new JObject
                {
                    ["dry_run"] = true,
                    ["ifc_path"] = ifcPath,
                    ["bcf_path"] = bcf ? bcfPath : null,
                    ["output_name"] = stem,
                    ["information_container_name"] = composed,
                    ["options"] = options,
                    ["georeference_in_model"] = georeference,
                    ["ids"] = ids == null ? null : new JObject { ["path"] = idsPath, ["sha256"] = idsSha, ["info"] = ids.InfoJson() },
                    ["pset_mapping"] = mapping == null ? null : new JObject { ["path"] = mappingPath, ["sha256"] = mappingSha, ["summary"] = mapping.SummaryJson() },
                    ["gates_planned"] = PlannedGates(precheck, ids != null, mapping != null, bcf, containerSpec != null),
                    ["gates"] = IfcDeliveryRules.GatesJson(gates),
                    ["precheck"] = precheckJson,
                    ["overwrite"] = overwrite,
                    ["note"] = "Nothing was exported and no file was created. The precheck above, when present, read the " +
                               "live model only and is advisory."
                };
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(plan, gate, Name, planHash, true,
                    "the token binds the destination, every exporter option, the identity of the filter view, the " +
                    "content (SHA-256) of the mapping and the IDS, and which targets already exist.");
                return CommandResult.Ok(plan);
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;

            // ---- 2. the export ----------------------------------------------------
            FileStamp before = Stamp(ifcPath);
            bool apiAccepted;
            try
            {
                var ifc = new IFCExportOptions
                {
                    ExportBaseQuantities = baseQuantities,
                    WallAndColumnSplitting = splitWalls,
                    SpaceBoundaryLevel = spaceBoundaries,
                    FilterViewId = filterView?.Id ?? ElementId.InvalidElementId
                };
                ifc.FileVersion = ifcVersion;
                if (mapping != null)
                {
                    ifc.AddOption("ExportUserDefinedPsets", "true");
                    ifc.AddOption("ExportUserDefinedPsetsFileName", mappingPath);
                }
                if (placementOption != null) ifc.AddOption("SitePlacement", placementOption);
                if (commonPsets.HasValue) ifc.AddOption("ExportIFCCommonPropertySets", commonPsets.Value ? "true" : "false");
                if (internalPsets.HasValue) ifc.AddOption("ExportInternalRevitPropertySets", internalPsets.Value ? "true" : "false");
                // The exporter writes export bookkeeping into the document and refuses without
                // a transaction (measured on horizun_export, run 15).
                using (var tx = new Transaction(doc, "Horizun: deliver IFC"))
                {
                    tx.Start();
                    apiAccepted = doc.Export(folder, stem, ifc);
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail("Revit IFC export failed: " + ex.Message,
                    new JObject { ["external_files_may_exist"] = true, ["planned_file"] = ifcPath,
                                  ["gates"] = IfcDeliveryRules.GatesJson(gates) });
            }

            FileStamp after = Stamp(ifcPath);
            bool produced = after.Exists && after.Size > 0 && (!before.Exists || before.Size != after.Size || before.Mtime != after.Mtime);
            var exportGate = new DeliveryGate { Name = "export", Requested = true };
            if (!produced)
            {
                exportGate.Status = DeliveryGateStatus.Failed;
                exportGate.Reason = "Revit returned (accepted=" + apiAccepted + ") and no new or changed non-empty file was " +
                                    "measured at " + ifcPath + ". Success is not claimed.";
                gates.Add(exportGate);
                return CommandResult.FailWithDetail(exportGate.Reason,
                    new JObject { ["external_files_may_exist"] = true, ["planned_file"] = ifcPath,
                                  ["gates"] = IfcDeliveryRules.GatesJson(gates), ["deliverable_ready"] = false });
            }
            byte[] head, tail;
            string ifcSha;
            try { ifcSha = FileSha(ifcPath); ReadEnds(ifcPath, 16384, 4096, out head, out tail); }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail("The IFC was written but could not be re-read: " + ex.Message,
                    new JObject { ["external_files_may_exist"] = true, ["planned_file"] = ifcPath, ["deliverable_ready"] = false });
            }
            exportGate.Status = DeliveryGateStatus.Passed;
            exportGate.Reason = "a new non-empty file was measured at the exact planned path and hashed from disk.";
            exportGate.Evidence = new JObject
            {
                ["path"] = ifcPath, ["bytes"] = after.Size, ["sha256"] = ifcSha, ["api_accepted"] = apiAccepted,
                ["last_write_utc"] = new DateTime(after.Mtime, DateTimeKind.Utc).ToString("o")
            };
            gates.Add(exportGate);

            // ---- header / trailer -------------------------------------------------
            gates.Add(IfcDeliveryRules.CheckHeader(Encoding.ASCII.GetString(head), Encoding.ASCII.GetString(tail), version));

            // ---- one parse of the produced file, reused by every later gate -------
            string parseError = null;
            // Parsed even without an IDS or a mapping: georeference_in_file is read from it.
            IfcStepReader.Document parsed = IfcStepReader.Read(ifcPath, out parseError);

            // ---- 3. IDS on the FILE -----------------------------------------------
            IdsReport validation = null;
            JObject validationJson = null;
            if (ids != null)
            {
                if (parsed != null)
                {
                    validation = IdsRun.Validate(ids, parsed, idsPath);
                    validationJson = validation.ToJson(maxFindings);
                }
                gates.Add(IfcDeliveryRules.IdsGate(validation,
                    parsed == null ? "the exported file could not be parsed for validation: " + parseError : null));
            }

            // ---- 4. the mapping, in the FILE --------------------------------------
            if (mapping != null)
            {
                List<PsetMapping.Row> rows;
                if (parsed == null)
                    gates.Add(new DeliveryGate { Name = "pset_mapping", Requested = true, Status = DeliveryGateStatus.NotDecidable,
                                                 Reason = "the exported file could not be parsed: " + parseError });
                else gates.Add(PsetMapping.Verify(mapping, parsed, minCoverage, 10, out rows));
            }

            // ---- 5. BCF of the IDS failures ---------------------------------------
            if (bcf)
            {
                var bcfGate = new DeliveryGate { Name = "bcf", Requested = true };
                if (validation == null)
                {
                    bcfGate.Status = DeliveryGateStatus.NotDecidable;
                    bcfGate.Reason = "there is no IDS validation of the file to turn into topics.";
                }
                else
                {
                    string projectGuid = parsed.Of("IFCPROJECT").Select(e => IfcStepReader.Text(e.At(0))).FirstOrDefault();
                    List<IdsBcfTopic> topics = IdsBcf.Topics(validation, parsed, Path.GetFileName(ifcPath), ifcSha, 1000);
                    if (topics.Count == 0)
                    {
                        bcfGate.Status = DeliveryGateStatus.Skipped;
                        bcfGate.Reason = "no specification fails on the file, so there is nothing to report and no BCF was written.";
                    }
                    else
                    {
                        string bcfError = null;
                        JObject evidence = null;
                        try { evidence = IdsBcf.WriteAndVerify(bcfPath, topics, Path.GetFileName(ifcPath), projectGuid, DateTime.UtcNow, out bcfError); }
                        catch (Exception ex) { bcfError = ex.Message; }
                        bcfGate.Evidence = evidence;
                        bcfGate.Status = evidence != null ? DeliveryGateStatus.Passed : DeliveryGateStatus.Failed;
                        bcfGate.Reason = evidence != null
                            ? topics.Count + " topic(s), one per failed specification, written and re-read structurally."
                            : "the BCF could not be verified: " + bcfError + " A file may exist at " + bcfPath + "; it is not claimed.";
                    }
                }
                gates.Add(bcfGate);
            }

            // ---- 6. The ISO 19650 container sidecar ------------------------------
            // Written LAST, over the file every earlier gate judged, so the SHA-256 it seals is
            // the delivered one. It records status and revision; it does not decide them.
            if (containerSpec != null)
            {
                var containerGate = new DeliveryGate { Name = "information_container", Requested = true };
                try
                {
                    long containerBytes;
                    string containerSha = InformationContainer.Sha256File(ifcPath, out containerBytes);
                    if (!string.Equals(containerSha, ifcSha, StringComparison.OrdinalIgnoreCase))
                    {
                        containerGate.Status = DeliveryGateStatus.Failed;
                        containerGate.Reason = "the IFC changed on disk after it was verified (" + ifcSha + " -> " + containerSha + "); no sidecar was written.";
                    }
                    else
                    {
                        JObject sidecar = InformationContainer.BuildSidecar(containerSpec, containerCheck, ifcPath, containerBytes, containerSha,
                            Name, doc.Title, app?.Application?.VersionNumber, DateTime.UtcNow,
                            new JObject { ["state"] = JValue.CreateNull(), ["format"] = "ifc" });
                        JObject evidence = InformationContainer.WriteSidecarVerified(ifcPath, sidecar);
                        containerGate.Status = DeliveryGateStatus.Passed;
                        containerGate.Evidence = new JObject { ["sidecar"] = sidecar, ["verification"] = evidence };
                        containerGate.Reason = "sidecar " + InformationContainer.SidecarPath(ifcPath) + " written and re-read against the delivered file.";
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is ContainerRuleException)
                {
                    containerGate.Status = DeliveryGateStatus.Failed;
                    containerGate.Reason = "the sidecar could not be written and verified: " + ex.Message;
                }
                gates.Add(containerGate);
            }

            List<string> blocking;
            bool ready = IfcDeliveryRules.DeliverableReady(gates, out blocking);
            var result = new JObject
            {
                ["dry_run"] = false,
                ["deliverable_ready"] = ready,
                ["blocking"] = new JArray(blocking),
                ["ifc_path"] = ifcPath,
                ["bytes"] = after.Size,
                ["sha256"] = ifcSha,
                ["output_name"] = stem,
                ["information_container_name"] = composed,
                ["gates"] = IfcDeliveryRules.GatesJson(gates),
                ["options"] = options,
                ["georeference_in_model"] = georeference,
                ["georeference_in_file"] = parsed == null ? null : IfcGeoreferenceReadback.Read(parsed),
                ["ifc_parse_error"] = parseError,
                ["precheck"] = precheckJson,
                ["ids_validation"] = validationJson,
                ["verdict_basis"] = "deliverable_ready is the FILE's verdict: every requested non-advisory gate was decided " +
                    "by re-reading the exported IFC (and the BCF). The model precheck is advisory and never counts. " +
                    (ready ? "" : "The file stays at ifc_path and is NOT ready; 'blocking' names why.")
            };
            DocumentGate.StampConfirmation(result, gate, Name, planHash, false);
            return CommandResult.Ok(result);
        }

        // =====================================================================

        private static JArray PlannedGates(bool precheck, bool ids, bool mapping, bool bcf, bool container) => new JArray(
            new JObject { ["gate"] = "precheck", ["requested"] = precheck, ["advisory"] = true },
            new JObject { ["gate"] = "export", ["requested"] = true },
            new JObject { ["gate"] = "schema_header", ["requested"] = true },
            new JObject { ["gate"] = "ids_validate", ["requested"] = ids },
            new JObject { ["gate"] = "pset_mapping", ["requested"] = mapping },
            new JObject { ["gate"] = "bcf", ["requested"] = bcf },
            new JObject { ["gate"] = "information_container", ["requested"] = container });

        /// <summary>
        /// Every option the exporter will be handed, with HOW it reaches the exporter and HOW
        /// the delivery knows it held. Typed properties of IFCExportOptions are compiled
        /// against every supported Revit year; named options go through AddOption and are
        /// read by the open-source Revit IFC exporter by name - their effect is judged from
        /// the file wherever the file can show it, and named requested_unverifiable where it
        /// cannot.
        /// </summary>
        private static JArray EffectiveOptions(string version, View filterView, bool baseQuantities, bool splitWalls,
            int spaceBoundaries, bool? commonPsets, bool? internalPsets, string placementArg, string placementOption,
            string mappingPath)
        {
            Func<string, JToken, string, string, JObject> row = (name, value, channel, verification) =>
                new JObject { ["option"] = name, ["value"] = value, ["channel"] = channel, ["verification"] = verification };
            var a = new JArray
            {
                row("FileVersion", version, "typed IFCExportOptions.FileVersion", "schema_header gate (FILE_SCHEMA family)"),
                row("FilterViewId", filterView == null ? (JToken)JValue.CreateNull() : Rid.Value(filterView.Id),
                    "typed IFCExportOptions.FilterViewId", "requested_unverifiable"),
                row("ExportBaseQuantities", baseQuantities, "typed IFCExportOptions.ExportBaseQuantities",
                    "requested_unverifiable (Qto_ sets are visible to ids_validate if an IDS asks for them)"),
                row("WallAndColumnSplitting", splitWalls, "typed IFCExportOptions.WallAndColumnSplitting", "requested_unverifiable"),
                row("SpaceBoundaryLevel", spaceBoundaries, "typed IFCExportOptions.SpaceBoundaryLevel", "requested_unverifiable"),
                row("ExportUserDefinedPsets", mappingPath != null, mappingPath != null ? "AddOption (named)" : "not sent",
                    mappingPath != null ? "pset_mapping gate (the declared sets are looked for in the file)" : "not requested"),
                row("ExportUserDefinedPsetsFileName", mappingPath, mappingPath != null ? "AddOption (named)" : "not sent",
                    mappingPath != null ? "pset_mapping gate" : "not requested"),
                row("SitePlacement", placementOption == null ? "(exporter default)" : placementOption + " (" + placementArg + ")",
                    placementOption != null ? "AddOption (named)" : "not sent",
                    "observed, not judged: georeference_in_file reports the IfcSite placement and any IfcMapConversion"),
                row("ExportIFCCommonPropertySets", commonPsets.HasValue ? (JToken)commonPsets.Value : "(exporter default)",
                    commonPsets.HasValue ? "AddOption (named)" : "not sent", "requested_unverifiable"),
                row("ExportInternalRevitPropertySets", internalPsets.HasValue ? (JToken)internalPsets.Value : "(exporter default)",
                    internalPsets.HasValue ? "AddOption (named)" : "not sent", "requested_unverifiable")
            };
            return a;
        }

        /// <summary>
        /// The model's georeference as it stands: survey point, project base point, the
        /// active location's position and angle to true north, and the site location. Every
        /// read guarded on its own - one throw must not erase the rest - and lengths in mm.
        /// </summary>
        private static JObject ReadGeoreference(Document doc)
        {
            var geo = new JObject { ["units"] = "mm; angles in degrees" };
            geo["survey_point"] = Guarded(() => BasePointJson(BasePoint.GetSurveyPoint(doc)));
            geo["project_base_point"] = Guarded(() => BasePointJson(BasePoint.GetProjectBasePoint(doc)));
            geo["active_location"] = Guarded(() =>
            {
                ProjectLocation location = doc.ActiveProjectLocation;
                if (location == null) return new JObject { ["readable"] = false, ["why"] = "no active project location" };
                ProjectPosition p = location.GetProjectPosition(XYZ.Zero);
                return new JObject
                {
                    ["name"] = location.Name,
                    ["east_west_mm"] = p == null ? (JToken)JValue.CreateNull() : Math.Round(p.EastWest * FtToMm, 3),
                    ["north_south_mm"] = p == null ? (JToken)JValue.CreateNull() : Math.Round(p.NorthSouth * FtToMm, 3),
                    ["elevation_mm"] = p == null ? (JToken)JValue.CreateNull() : Math.Round(p.Elevation * FtToMm, 3),
                    ["angle_to_true_north_deg"] = p == null ? (JToken)JValue.CreateNull() : Math.Round(p.Angle * 180.0 / Math.PI, 6)
                };
            });
            geo["site_location"] = Guarded(() =>
            {
                SiteLocation site = doc.SiteLocation;
                if (site == null) return new JObject { ["readable"] = false, ["why"] = "no site location" };
                var s = new JObject();
                s["latitude_deg"] = Guarded(() => Math.Round(site.Latitude * 180.0 / Math.PI, 8));
                s["longitude_deg"] = Guarded(() => Math.Round(site.Longitude * 180.0 / Math.PI, 8));
                s["place_name"] = Guarded(() => site.PlaceName);
                s["time_zone_hours"] = Guarded(() => site.TimeZone);
                s["geo_coordinate_system_id"] = Guarded(() => site.GeoCoordinateSystemId);
                return s;
            });
            return geo;
        }

        private static JObject BasePointJson(BasePoint point)
        {
            if (point == null) return new JObject { ["readable"] = false, ["why"] = "the document reports none" };
            XYZ at = point.Position, shared = point.SharedPosition;
            return new JObject
            {
                ["internal_mm"] = new JArray(Math.Round(at.X * FtToMm, 3), Math.Round(at.Y * FtToMm, 3), Math.Round(at.Z * FtToMm, 3)),
                ["shared_mm"] = new JArray(Math.Round(shared.X * FtToMm, 3), Math.Round(shared.Y * FtToMm, 3), Math.Round(shared.Z * FtToMm, 3))
            };
        }

        private static JToken Guarded<T>(Func<T> read)
        {
            try
            {
                T value = read();
                if (value == null) return JValue.CreateNull();
                return value as JToken ?? JToken.FromObject(value);
            }
            catch (Exception ex) { return new JObject { ["readable"] = false, ["why"] = ex.Message }; }
        }

        private sealed class FileStamp { public bool Exists; public long Size, Mtime; }

        private static FileStamp Stamp(string path)
        {
            try
            {
                var f = new FileInfo(path);
                return f.Exists ? new FileStamp { Exists = true, Size = f.Length, Mtime = f.LastWriteTimeUtc.Ticks } : new FileStamp();
            }
            catch { return new FileStamp(); }
        }

        private static string FileSha(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void ReadEnds(string path, int headBytes, int tailBytes, out byte[] head, out byte[] tail)
        {
            using (var stream = File.OpenRead(path))
            {
                head = new byte[(int)Math.Min(headBytes, stream.Length)];
                int read = stream.Read(head, 0, head.Length);
                Array.Resize(ref head, read);
                long start = Math.Max(0, stream.Length - tailBytes);
                stream.Seek(start, SeekOrigin.Begin);
                tail = new byte[(int)(stream.Length - start)];
                read = stream.Read(tail, 0, tail.Length);
                Array.Resize(ref tail, read);
            }
        }

        private static string SafeUid(Element e) { try { return e?.UniqueId; } catch { return null; } }
        private static string SafeName(Element e) { try { return e?.Name; } catch { return "<unreadable>"; } }
    }
}
