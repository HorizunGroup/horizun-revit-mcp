// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_export for DWG/DGN/DWFX view SETS (one file per view or
// sheet), gbXML and loadable families (.rfa). Each follows the export contract of
// ExportCommand.cs: the dry run resolves every file name and every refusal before
// anything is written, the apply needs the token, and success is claimed only for
// files re-read from disk after Revit returns - by size AND by what their first bytes
// (or, for gbXML, their elements) say they are.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ExportCommand
    {
        /// <summary>
        /// A DWG request takes the set path when it names more than one view, a naming
        /// rule or an xref choice. A single view with none of them keeps the one-file
        /// path above, with its preset and information-container support unchanged.
        /// </summary>
        private static bool IsDwgSet(JObject request)
        {
            return ((request["view_ids"] as JArray)?.Count ?? 0) > 1 || request["file_naming"] != null || request["dwg_xrefs"] != null;
        }

        private static CommandResult RefuseFields(JObject request, string what, params string[] fields)
        {
            foreach (string field in fields)
                if (request[field] != null && request[field].Type != JTokenType.Null)
                    return CommandResult.Fail(field + " is not accepted for " + what + ". Nothing was exported.");
            return null;
        }

        private ResolvedPlan NewPlan(UIApplication app, GateResult gate, IEnumerable<Element> sources, IEnumerable<string> existing, bool overwrite)
        {
            var plan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Element e in sources)
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = SafePlanUid(e), Category = e is View ? "view" : "family", TypeName = SafePlanName(e),
                    Action = PlannedAction.Modify, BeforeValues = new Dictionary<string, string> { { "role", "export_source" } }
                });
            // Which destination files exist is ambient state the approval depends on,
            // exactly as for the single-file exports.
            plan.ContextFingerprint = "existing=" + string.Join(",", existing.Where(File.Exists)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) + ";overwrite=" + (overwrite ? "1" : "0");
            return plan;
        }

        /// <summary>Size, first bytes and the header verdict of one produced file.</summary>
        private static JObject FileEvidence(string format, string path)
        {
            var row = new JObject { ["path"] = path };
            try
            {
                var info = new FileInfo(path);
                row["bytes"] = info.Length;
                row["last_write_utc"] = info.LastWriteTimeUtc.ToString("o");
                string kind = info.Length > 0 ? ExportFormatRules.HeaderKindOf(format, ReadHeadBytes(path, 8)) : null;
                row["header"] = kind;
                row["header_verified"] = kind != null;
            }
            catch (Exception ex) { row["header_verified"] = false; row["error"] = ex.Message; }
            return row;
        }

        /// <summary>
        /// Every file of one extension in the folder, stamped by size and time; only the files named
        /// like <paramref name="target"/> (its stem and stem-/stem_ companions) are also hashed.
        /// Hashing the whole folder around every exporter call would cost views x folder bytes on
        /// Revit's UI thread; a file outside the name family that changes still shows by its stamp.
        /// </summary>
        private static Dictionary<string, ExportFileStamp> SnapshotExtension(string folder, string extension, string format, string target)
        {
            var result = new Dictionary<string, ExportFileStamp>(StringComparer.OrdinalIgnoreCase);
            string[] found;
            try { found = Directory.GetFiles(folder, "*" + extension); } catch { return result; }
            foreach (string file in found)
            {
                if (!string.Equals(System.IO.Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase)) continue;
                var stamp = new ExportFileStamp { Existed = true };
                try
                {
                    var f = new FileInfo(file);
                    stamp.Size = f.Length; stamp.Mtime = f.LastWriteTimeUtc.Ticks;
                    if (MatchesOutput(format, target, file)) stamp.Hash = FileHash(file);
                    stamp.Readable = true;
                }
                catch { /* Existed stays true; the rest stays unmeasured. */ }
                result[file] = stamp;
            }
            return result;
        }

        private static Dictionary<string, ExportFileStamp> SnapshotDwgImages(string folder, string target)
        {
            var result = new Dictionary<string, ExportFileStamp>(StringComparer.OrdinalIgnoreCase);
            string[] found;
            try { found = Directory.GetFiles(folder); } catch { return result; }
            foreach (string file in found.Where(p => DwgCompanionRules.Kind(target, p) == "image"))
            {
                var stamp = new ExportFileStamp { Existed = true };
                try
                {
                    var info = new FileInfo(file);
                    stamp.Size = info.Length; stamp.Mtime = info.LastWriteTimeUtc.Ticks;
                    stamp.Hash = FileHash(file); stamp.Readable = true;
                }
                catch { /* Keep existed=true; an unreadable old file is never called new. */ }
                result[file] = stamp;
            }
            return result;
        }

        private static JObject ImageEvidence(string path)
        {
            var row = new JObject { ["path"] = path, ["role"] = "image" };
            try
            {
                var info = new FileInfo(path);
                row["bytes"] = info.Length;
                row["last_write_utc"] = info.LastWriteTimeUtc.ToString("o");
                row["header_verified"] = info.Length > 0 &&
                    DwgCompanionRules.ValidImageHeader(path, ReadHeadBytes(path, 8));
            }
            catch (Exception ex) { row["header_verified"] = false; row["error"] = ex.Message; }
            return row;
        }

        // ---- D1/D4: DWG, DGN and DWFX view sets -------------------------------------
        private CommandResult ExecuteViewSet(UIApplication app, GateResult gate, Document doc, JObject request, string format, string output, List<JObject> assumptions)
        {
            CommandResult refused = RefuseFields(request, "a " + format + " view set (one container or preset names ONE file)",
                "preset", "information_container", "delivery_id", "pdf_print", "pdf_combine", "emit_manifest", "schedule_id",
                "family_ids", "category");
            if (refused != null) return refused;
            if (format != "dwg")
            {
                refused = RefuseFields(request, "format " + format + " (DWG options)", "dwg_setup", "acad_version", "dwg_xrefs");
                if (refused != null) return refused;
            }
            string folder = System.IO.Path.GetDirectoryName(output);
            var folderPlan = format == "dwg" ? new ExportDirectoryPlan(output, request.Value<bool?>("require_existing_folder") == true) : null;
            bool folderMissing = folderPlan?.NeedsCreation == true;
            if (folderPlan == null && (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)))
                return CommandResult.Fail("The output directory does not exist: " + folder + ". It is not created implicitly: create that folder first, or point output_path (the full path of the file to write) at a folder that exists. Nothing was written.");
            List<View> views = ReadViews(doc, request["view_ids"] as JArray, out string viewError);
            if (viewError != null) return CommandResult.Fail(viewError);
            if (views.Count == 0) return CommandResult.Fail(format + " requires one or more view_ids. Nothing was exported.");
            List<View> notPrintable = views.Where(v => !v.CanBePrinted).ToList();
            if (notPrintable.Count > 0)
                return CommandResult.Fail("The " + format + " exporter takes printable views only; not printable: " +
                    string.Join(", ", notPrintable.Select(v => Rid.Value(v.Id) + " '" + SafePlanName(v) + "'")) + ". Nothing was exported.");
            string naming = (request.Value<string>("file_naming") ?? ExportFormatRules.NamingOrdinal).ToLowerInvariant();
            string xrefs = (request.Value<string>("dwg_xrefs") ?? ExportFormatRules.XrefsLinked).ToLowerInvariant();
            if (xrefs != ExportFormatRules.XrefsLinked && xrefs != ExportFormatRules.XrefsBound)
                return CommandResult.Fail("dwg_xrefs must be linked or bound.");
            var facts = views.Select(v => new ExportViewFacts
            {
                Id = Rid.Value(v.Id), Name = SafePlanName(v), IsSheet = v is ViewSheet, SheetNumber = (v as ViewSheet)?.SheetNumber
            }).ToList();
            List<string> stems = ExportFormatRules.SetStems(System.IO.Path.GetFileNameWithoutExtension(output), naming, facts, out string namingRefusal);
            if (stems == null) return CommandResult.Fail(namingRefusal + " Nothing was exported.");
            string extension = "." + format;
            string[] targets = stems.Select(s => System.IO.Path.Combine(folder, s + extension)).ToArray();

            DWGExportOptions dwgOptions = null;
            if (format == "dwg")
            {
                dwgOptions = BuildDwgOptions(doc, request, out string dwgRefusal);
                if (dwgOptions == null) return CommandResult.Fail(dwgRefusal);
                // Revit: MergedViews = "merge all views in one file (via XRefs)" is false
                // by default, which writes a sheet's views and links as external
                // references beside it; bound merges them into the sheet's own file. A
                // named dwg_setup carries its own choice, which an omitted dwg_xrefs keeps
                // (and the reply states) instead of silently replacing it with the default.
                if (request["dwg_xrefs"] == null) xrefs = dwgOptions.MergedViews ? ExportFormatRules.XrefsBound : ExportFormatRules.XrefsLinked;
                else dwgOptions.MergedViews = xrefs == ExportFormatRules.XrefsBound;
            }
            bool overwrite = request.Value<bool?>("overwrite") == true;
            // Each target's candidate files include what Revit writes beside it
            // (stem-*.dwg xrefs), so an overwrite=false approval refuses them too.
            List<string> existing = folderMissing ? new List<string>()
                : targets.SelectMany(t => CandidateFiles(format, t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!overwrite && existing.Count > 0)
                return CommandResult.Fail("Output already exists and overwrite=false: " + string.Join(", ", existing));

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            OperationGateResult gateDecision = OperationGate.Evaluate(app, doc, request["require_gate"], GatedOperation.Export, Name);
            if (gateDecision.Refusal != null) return gateDecision.Refusal;
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "view_ids", "overwrite", "dwg_setup",
                "acad_version", "file_naming", "dwg_xrefs", "require_existing_folder");
            ResolvedPlan resolvedPlan = NewPlan(app, gate, views, existing, overwrite);
            resolvedPlan.ContextFingerprint += folderPlan?.Fingerprint;
            var viewRows = new JArray(views.Select((v, i) => new JObject
            {
                ["id"] = Rid.Value(v.Id), ["name"] = SafePlanName(v), ["sheet_number"] = (v as ViewSheet)?.SheetNumber, ["file"] = targets[i]
            }));
            if (format == "dgn")
            {
                // A DGN needs a seed file: with SeedName left empty Revit 2026 returned without writing
                // anything (MEASURED 2026-09-27). The seed is the running Revit's own, chosen per view.
                for (int i = 0; i < views.Count; i++)
                {
                    string seed = DgnSeedFor(doc, views[i]);
                    if (!File.Exists(seed))
                        return CommandResult.Fail("no DGN seed file at '" + seed + "' (the running Revit's own ACADInterop seeds): " +
                            "a DGN cannot be written without one. Nothing was exported.");
                    ((JObject)viewRows[i])["dgn_seed"] = seed;
                }
            }
            if (dryRun)
            {
                bool longSet = views.Count > ExportFormatRules.LongSetViews;
                var plan = new JObject
                {
                    ["dry_run"] = true, ["format"] = format, ["output_path"] = output, ["file_naming"] = naming,
                    ["dwg_xrefs"] = format == "dwg" ? xrefs : null, ["planned_files"] = new JArray(targets), ["views"] = viewRows,
                    ["overwrite"] = overwrite, ["long_set"] = longSet,
                    ["note"] = "Nothing was exported and no file was created." + (longSet
                        ? " " + views.Count + " views are one exporter call each on Revit's UI thread: send the apply through " +
                          "horizun_submit_job and poll horizun_job_status rather than holding the request open."
                        : "")
                };
                if (gateDecision.Requested) plan["prevention"] = gateDecision.Prevention;
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(plan, gate, Name, planHash, true,
                    "the token binds format, destination, naming, options, the IDENTITY of every view and which destination files exist.");
                if (folderMissing) assumptions.Add(folderPlan.Assumption(false));
                return CommandResult.Ok(plan);
            }
            CommandResult refusal = ExportDirectoryPlan.Apply(folderPlan,
                () => DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null),
                () => DocumentGate.StillTheSame(app, gate.Fingerprint, Name), out JObject folderAssumption, out var directoryGuard);
            using var exportDirectory = directoryGuard;
            if (refusal != null) return refusal;

            assumptions.Add(folderAssumption);
            if (folderMissing && !overwrite)
            {
                var conflicts = targets.SelectMany(t => CandidateFiles(format, t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (conflicts.Count > 0)
                    return CommandResult.Fail("Output already exists and overwrite=false: " + string.Join(", ", conflicts));
            }
            var files = new JArray();
            var failed = new List<string>();
            int verified = 0;
            string acadWanted = format == "dwg" ? request.Value<string>("acad_version") : null;
            for (int i = 0; i < views.Count; i++)
            {
                // One exporter call per view, and a snapshot around EACH call: whatever
                // changed during call i belongs to view i, however the names overlap.
                Dictionary<string, ExportFileStamp> before = SnapshotExtension(folder, extension, format, targets[i]);
                Dictionary<string, ExportFileStamp> imageBefore = format == "dwg" ? SnapshotDwgImages(folder, targets[i]) : null;
                bool accepted = false; string error = null;
                try { accepted = ExportOneView(doc, format, folder, stems[i], views[i], dwgOptions); }
                catch (Exception ex) { error = ex.Message; }
                Dictionary<string, ExportFileStamp> after = SnapshotExtension(folder, extension, format, targets[i]);
                Dictionary<string, ExportFileStamp> imageAfter = format == "dwg" ? SnapshotDwgImages(folder, targets[i]) : null;
                (List<string> produced, List<string> unmeasured) = ExportFileDiff.Diff(before, after);
                (List<string> images, List<string> imageUnmeasured) = format == "dwg"
                    ? ExportFileDiff.Diff(imageBefore, imageAfter)
                    : (new List<string>(), new List<string>());
                bool mainProduced = produced.Contains(targets[i], StringComparer.OrdinalIgnoreCase);
                JObject main = mainProduced ? FileEvidence(format, targets[i]) : null;
                // Companions: unmerged, Revit writes a sheet's views AND a view's visible links as
                // separate files named <stem>-... beside the target - DWG with dwg_xrefs=linked, DGN
                // always (DGNExportOptions.MergedViews defaults to false). Any view type can carry
                // link companions, so the NAME FAMILY decides, not the view type.
                bool companionsExpected = (format == "dwg" && xrefs == ExportFormatRules.XrefsLinked) || format == "dgn";
                var others = new JArray(produced.Where(p => !p.Equals(targets[i], StringComparison.OrdinalIgnoreCase)).Select(p =>
                {
                    JObject evidence = FileEvidence(format, p);
                    evidence["role"] = companionsExpected && MatchesOutput(format, targets[i], p) ? "xref" : "unexpected";
                    CheckAcadVersion(evidence, acadWanted);
                    return evidence;
                }));
                foreach (string path in images) others.Add(ImageEvidence(path));
                CheckAcadVersion(main, acadWanted);
                // A file of this view's name family that the call left empty or unreadable is not a
                // verified file either; one untouched since before the call is not this call's.
                List<string> ownUnmeasured = unmeasured.Where(p => MatchesOutput(format, targets[i], p) && !Untouched(before, after, p)).ToList();
                ownUnmeasured.AddRange(imageUnmeasured.Where(p => !Untouched(imageBefore, imageAfter, p)));
                bool ok = main != null && Held(main) && others.All(o => Held((JObject)o) &&
                    (o.Value<string>("role") == "xref" || o.Value<string>("role") == "image")) && ownUnmeasured.Count == 0;
                var row = new JObject
                {
                    ["view_id"] = Rid.Value(views[i].Id), ["view_name"] = SafePlanName(views[i]), ["file"] = targets[i],
                    ["api_accepted"] = accepted, ["verified"] = ok, ["main"] = main, ["other_files"] = others,
                    ["reference_files"] = new JArray(others.OfType<JObject>().Where(o =>
                        o.Value<string>("role") == "xref" || o.Value<string>("role") == "image").Select(o => o.DeepClone()))
                };
                if (error != null) row["error"] = error;
                if (ownUnmeasured.Count > 0) row["unmeasured_files"] = new JArray(ownUnmeasured);
                if (ok) verified++; else failed.Add(targets[i]);
                files.Add(row);
            }
            var result = new JObject
            {
                ["format"] = format, ["file_naming"] = naming, ["dwg_xrefs"] = format == "dwg" ? xrefs : null,
                ["files_planned"] = targets.Length, ["files_verified"] = verified, ["files"] = files,
                ["annotation_location"] = format == "dwg" ? ExportFormatRules.DwgAnnotationLocation(xrefs) : null,
                ["reference_files"] = format == "dwg" ? new JArray(files.OfType<JObject>()
                    .SelectMany(f => (f["reference_files"] as JArray ?? new JArray()).OfType<JObject>())
                    .Select(r => r.DeepClone())) : (JToken)new JArray(),
                ["means"] = "verified = the planned file was written or changed by its own exporter call, is non-empty and its first bytes " +
                            "are the format's signature (with acad_version, that DWG version); companion files the call wrote beside it - a " +
                            "sheet's views and a view's links, for DWG linked and for DGN - must be named <stem>-... and pass the same check. " +
                            "DWG raster companions with the same filename family are separately checked by image signature. " +
                            "DWG entity contents and layouts are not verified by this file-level check."
            };
            if (gateDecision.Requested) result["prevention"] = gateDecision.Prevention;
            if (verified != targets.Length)
            {
                result["external_files_may_exist"] = true; result["rollback_available"] = false;
                return CommandResult.FailWithDetail(verified + " of " + targets.Length + " " + format + " files verified; not verified: " +
                    string.Join(", ", failed) + ". Success is not claimed.", result);
            }
            return CommandResult.Ok(result);
        }

        private static bool Held(JObject evidence) =>
            evidence.Value<bool>("header_verified") && evidence.Value<bool?>("acad_version_verified") != false;

        /// <summary>With acad_version requested, the DWG signature read back must be that version.</summary>
        private static void CheckAcadVersion(JObject evidence, string wanted)
        {
            if (evidence == null || string.IsNullOrEmpty(wanted)) return;
            string header = evidence.Value<string>("header");
            string got = header == null ? null : ExportPresetRules.DwgVersionOf(System.Text.Encoding.ASCII.GetBytes(header));
            evidence["acad_version_read"] = got;
            evidence["acad_version_verified"] = got == wanted;
        }

        private static bool Untouched(Dictionary<string, ExportFileStamp> before, Dictionary<string, ExportFileStamp> after, string path) =>
            before.TryGetValue(path, out ExportFileStamp old) && after.TryGetValue(path, out ExportFileStamp now) && old != null && now != null &&
            old.Existed && old.Readable == now.Readable && old.Size == now.Size && old.Mtime == now.Mtime;

        /// <summary>
        /// The V8 seed installed with the running Revit (ACADInterop beside RevitAPI.dll): metric or
        /// imperial by the document's display unit system, 3D for a 3-D view and 2D otherwise.
        /// </summary>
        internal static string DgnSeedFor(Document doc, View view)
        {
            string root = Path.GetDirectoryName(typeof(Document).Assembly.Location) ?? "";
            string units = doc.DisplayUnitSystem == DisplayUnit.METRIC ? "Metric" : "Imperial";
            string dims = view is View3D ? "3D" : "2D";
            return Path.Combine(root, "ACADInterop", "V8-" + units + "-Seed" + dims + ".dgn");
        }

        private static bool ExportOneView(Document doc, string format, string folder, string stem, View view, DWGExportOptions dwgOptions)
        {
            switch (format)
            {
                case "dwg":
                    return doc.Export(folder, stem, new List<ElementId> { view.Id }, dwgOptions);
                case "dgn":
                    return doc.Export(folder, stem, new List<ElementId> { view.Id }, new DGNExportOptions { SeedName = DgnSeedFor(doc, view) });
                case "dwfx":
                {
                    var set = new ViewSet();
                    set.Insert(view);
                    // RevitAPI.xml documents this overload as throwing when "the current
                    // document is not modifiable", so it runs inside a transaction - the
                    // API's requirement, not a model edit - which is rolled back: the file
                    // is on disk either way and the model is left as it was.
                    using (var tx = new Transaction(doc, "Horizun: export DWFX"))
                    {
                        tx.Start();
                        try { return doc.Export(folder, stem, set, new DWFXExportOptions()); }
                        finally { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); }
                    }
                }
                default:
                    throw new InvalidOperationException("not a view-set format: " + format);
            }
        }

        // ---- D3: gbXML ------------------------------------------------------------
        private static int CountPlaced(Document doc, BuiltInCategory category, ElementId phase)
        {
            int count = 0;
            foreach (Element e in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType())
                if (e is SpatialElement spatial && spatial.Location != null && spatial.Area > 1e-9 && EnergyModelBuild.InPhase(spatial, phase)) count++;
            return count;
        }

        private static CommandResult NoAnalyticalSpaces(string scope, int rooms, int spaces) =>
            CommandResult.FailWithDetail("no spaces: Revit's energy model built from the " + scope + " holds no analytical space, so a gbXML " +
                "export would be an empty campus. Nothing was written; the transaction that held the model is rolled back.",
                new JObject
                {
                    ["no_spaces"] = true, ["energy_scope"] = scope, ["analytical_spaces"] = 0, ["placed_rooms"] = rooms, ["placed_spaces"] = spaces,
                    ["external_files_may_exist"] = false
                });

        private CommandResult ExecuteGbXml(UIApplication app, GateResult gate, Document doc, JObject request, string output)
        {
            CommandResult refused = RefuseFields(request, "gbxml", "view_ids", "preset", "information_container", "delivery_id",
                "pdf_print", "pdf_combine", "emit_manifest", "schedule_id", "dwg_setup", "acad_version", "dwg_xrefs", "file_naming",
                "family_ids", "category");
            if (refused != null) return refused;
            string folder = System.IO.Path.GetDirectoryName(output);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return CommandResult.Fail("The output directory does not exist: " + folder + ". It is not created implicitly: create that folder first, or point output_path (the full path of the file to write) at a folder that exists. Nothing was written.");
            // Counted in the energy settings' scope - their export category and phase - which is
            // what the model is built from; the built model's own analytical spaces are counted
            // again on apply, before anything is written.
            EnergyModelBuild.Scope(doc, out BuiltInCategory? exportCategory, out ElementId energyPhase);
            string scope = EnergyModelBuild.ScopeText(doc, exportCategory, energyPhase);
            int rooms = exportCategory == BuiltInCategory.OST_MEPSpaces ? 0 : CountPlaced(doc, BuiltInCategory.OST_Rooms, energyPhase);
            int spaces = exportCategory == BuiltInCategory.OST_Rooms ? 0 : CountPlaced(doc, BuiltInCategory.OST_MEPSpaces, energyPhase);
            if (rooms + spaces == 0)
                return CommandResult.FailWithDetail("no spaces: the document has no placed, bounded " + scope + ", so a gbXML export " +
                    "would be an empty campus. Nothing was written.",
                    new JObject { ["no_spaces"] = true, ["energy_scope"] = scope, ["placed_rooms"] = 0, ["placed_spaces"] = 0 });
            bool overwrite = request.Value<bool?>("overwrite") == true;
            var existing = new List<string> { output }.Where(File.Exists).ToList();
            if (!overwrite && existing.Count > 0)
                return CommandResult.Fail("Output already exists and overwrite=false: " + output);
            bool mainModel = EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(doc) != null;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            OperationGateResult gateDecision = OperationGate.Evaluate(app, doc, request["require_gate"], GatedOperation.Export, Name);
            if (gateDecision.Refusal != null) return gateDecision.Refusal;
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "overwrite");
            ResolvedPlan resolvedPlan = NewPlan(app, gate, new Element[0], new[] { output }, overwrite);
            resolvedPlan.ContextFingerprint += ";rooms=" + rooms + ";spaces=" + spaces;
            if (dryRun)
            {
                var plan = new JObject
                {
                    ["dry_run"] = true, ["format"] = "gbxml", ["output_path"] = output, ["planned_files"] = new JArray(output),
                    ["placed_rooms"] = rooms, ["placed_spaces"] = spaces, ["main_energy_model_present"] = mainModel,
                    ["energy_scope"] = scope,
                    ["energy_model"] = "built from " + EnergyModelBuild.Description + " inside a transaction that is ROLLED BACK after the " +
                                       "file is written: the model is left as it was, including any energy model it had. A model that holds " +
                                       "no analytical space is refused as no spaces before anything is written.",
                    ["overwrite"] = overwrite, ["note"] = "Nothing was exported and no file was created."
                };
                if (gateDecision.Requested) plan["prevention"] = gateDecision.Prevention;
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(plan, gate, Name, planHash, true,
                    "the token binds the destination, which files exist and the placed room/space counts.");
                return CommandResult.Ok(plan);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            Dictionary<string, ExportFileStamp> before = SnapshotExtension(folder, ".xml", "gbxml", output);
            bool accepted = false;
            int analyticalSpaces = -1;
            string stage = "build";
            try
            {
                // RevitAPI.xml: the gbXML export "should be called from a transaction" and
                // runs on the MAIN energy analysis model, failing when there is none or its
                // type does not match. A fresh SpatialElement model is built for the export
                // (the existing one deleted first, so its type cannot mismatch) and the
                // whole transaction is rolled back once the file is on disk.
                using (var tx = new Transaction(doc, "Horizun: export gbXML"))
                {
                    tx.Start();
                    try
                    {
                        EnergyAnalysisDetailModel model = EnergyModelBuild.CreateSpatial(doc);
                        // An empty campus is refused BEFORE anything is written: the built model's
                        // own analytical spaces are counted, not the placed rooms.
                        analyticalSpaces = model == null ? 0 : model.GetAnalyticalSpaces().Count;
                        if (analyticalSpaces == 0) return NoAnalyticalSpaces(scope, rooms, spaces);
                        stage = "export";
                        var options = new GBXMLExportOptions();
#if REVIT2023 || REVIT2024 || REVIT2025
                        // AnalysisMode, 2026's default: the export follows the energy settings,
                        // which CreateSpatial set to rooms/spaces above. NOT SpatialElement, the
                        // documented default here: MEASURED 2026-09-27 in Revit 2024 on the
                        // SpatialElement/Final model Create had just made main, with the settings
                        // on RoomsOrSpaces SpatialElement is refused as "not compatible" while
                        // AnalysisMode writes the space with its constructions; with the settings
                        // on building elements SpatialElement is accepted but writes NO
                        // construction. 2027 removes the property.
                        options.ExportEnergyModelType = ExportEnergyModelType.AnalysisMode;
#endif
                        accepted = doc.Export(folder, System.IO.Path.GetFileNameWithoutExtension(output), options);
                    }
                    finally { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); }
                }
            }
            catch (Exception ex)
            {
                if (stage == "build")
                    return CommandResult.FailWithDetail("Revit could not build its energy model from the " + scope + " (" + ex.Message + "); RevitAPI.xml: " +
                        "Create throws when there are no valid spatial elements or spatial bounding elements. Nothing was written.",
                        new JObject { ["energy_scope"] = scope, ["external_files_may_exist"] = false, ["planned_files"] = new JArray(output) });
                return CommandResult.FailWithDetail("Revit gbXML export failed: " + ex.Message,
                    new JObject { ["external_files_may_exist"] = true, ["rollback_available"] = false, ["planned_files"] = new JArray(output) });
            }
            Dictionary<string, ExportFileStamp> after = SnapshotExtension(folder, ".xml", "gbxml", output);
            (List<string> produced, List<string> unmeasured) = ExportFileDiff.Diff(before, after);
            var result = new JObject
            {
                ["format"] = "gbxml", ["api_accepted"] = accepted, ["requested_output_path"] = output,
                ["placed_rooms"] = rooms, ["placed_spaces"] = spaces, ["energy_scope"] = scope, ["analytical_spaces"] = analyticalSpaces, ["files_verified"] = 0
            };
            if (unmeasured.Count > 0) result["unmeasured_files"] = new JArray(unmeasured);
            if (!produced.Contains(output, StringComparer.OrdinalIgnoreCase))
            {
                result["produced_files"] = new JArray(produced);
                result["external_files_may_exist"] = true; result["rollback_available"] = false;
                return CommandResult.FailWithDetail("Revit returned from the gbXML export (accepted=" + accepted + ") but " + output +
                    " was not written or changed. Success is not claimed.", result);
            }
            GbXmlCounts counts;
            try { using (FileStream stream = File.OpenRead(output)) counts = ExportFormatRules.CountGbXml(stream); }
            catch (Exception ex)
            {
                result["external_files_may_exist"] = true; result["rollback_available"] = false;
                return CommandResult.FailWithDetail("The gbXML file was written but could not be read back as XML: " + ex.Message +
                    ". Success is not claimed.", result);
            }
            var info = new FileInfo(output);
            result["files"] = new JArray(new JObject { ["path"] = output, ["bytes"] = info.Length, ["last_write_utc"] = info.LastWriteTimeUtc.ToString("o") });
            result["read_back"] = new JObject
            {
                ["root"] = counts.Root, ["campus"] = counts.Campus, ["building"] = counts.Building, ["space"] = counts.Space,
                ["zone"] = counts.Zone, ["surface"] = counts.Surface, ["opening"] = counts.Opening, ["construction"] = counts.Construction
            };
            string problem = ExportFormatRules.GbXmlProblem(counts);
            if (problem != null)
            {
                result["external_files_may_exist"] = true; result["rollback_available"] = false;
                return CommandResult.FailWithDetail("The gbXML file was written but " + problem + ". Success is not claimed.", result);
            }
            result["files_verified"] = 1;
            result["note"] = "The written XML was re-read: its Space and Zone counts are the file's own, not the model's.";
            if (gateDecision.Requested) result["prevention"] = gateDecision.Prevention;
            return CommandResult.Ok(result);
        }

        // ---- D2: loadable families as .rfa -------------------------------------------
        private CommandResult ExecuteRfa(UIApplication app, GateResult gate, Document doc, JObject request, string output)
        {
            CommandResult refused = RefuseFields(request, "rfa", "view_ids", "preset", "information_container", "delivery_id",
                "pdf_print", "pdf_combine", "emit_manifest", "schedule_id", "dwg_setup", "acad_version", "dwg_xrefs", "file_naming");
            if (refused != null) return refused;
            if (request.Value<bool?>("overwrite") == true)
                return CommandResult.Fail("rfa never overwrites: every family is saved with OverwriteExistingFile=false. Nothing was exported.");
            if (!Directory.Exists(output))
                return CommandResult.Fail("format rfa takes output_path as an EXISTING folder; " + output + " is not one. It is not created implicitly.");
            JArray ids = request["family_ids"] as JArray;
            string categoryName = request.Value<string>("category");
            if ((ids == null) == string.IsNullOrWhiteSpace(categoryName))
                return CommandResult.Fail("format rfa takes exactly one of family_ids or category.");

            var families = new List<Family>();
            var refusedRows = new JArray();
            var resolvedTypes = new JArray();
            if (ids != null)
            {
                foreach (JToken token in ids)
                {
                    long raw;
                    Element e = token.Type == JTokenType.Integer && long.TryParse(token.ToString(), out raw) && Rid.CanRepresent(raw)
                        ? doc.GetElement(Rid.Make(raw)) : null;
                    if (e is Family f) { if (!families.Any(x => x.Id == f.Id)) families.Add(f); continue; }
                    // A loadable family's TYPE id (what most query tools return) names its family:
                    // resolved, deduplicated and stated in the reply - never refused as a system type.
                    Family owner = null;
                    if (e is FamilySymbol symbol) { try { owner = symbol.Family; } catch { } }
                    if (owner != null)
                    {
                        resolvedTypes.Add(new JObject
                        {
                            ["type_id"] = token.DeepClone(), ["type"] = SafePlanName(e), ["family_id"] = Rid.Value(owner.Id), ["family"] = SafePlanName(owner)
                        });
                        if (!families.Any(x => x.Id == owner.Id)) families.Add(owner);
                        continue;
                    }
                    refusedRows.Add(new JObject
                    {
                        ["id"] = token.DeepClone(), ["name"] = e == null ? null : SafePlanName(e),
                        ["reason"] = e == null ? "not an element of the active document"
                            : e is ElementType ? "a system family type (" + e.GetType().Name + "): not a loadable family, there is no .rfa to write"
                            : "not a Family element (" + e.GetType().Name + ")"
                    });
                }
            }
            else
            {
                Category category = ResolveCategory(doc, categoryName);
                if (category == null) return CommandResult.Fail("category '" + categoryName + "' is not a category of this document.");
                foreach (Family f in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
                {
                    Category fc = null;
                    try { fc = f.FamilyCategory; } catch { }
                    if (fc != null && fc.Id == category.Id) families.Add(f);
                }
            }
            var loadable = new List<Family>();
            foreach (Family f in families)
            {
                string reason = f.IsInPlace ? "an in-place family: it lives only in this project and has no .rfa"
                              : !f.IsEditable ? "not editable through the API (Family.IsEditable is false)" : null;
                if (reason == null) loadable.Add(f);
                else refusedRows.Add(new JObject { ["id"] = Rid.Value(f.Id), ["name"] = SafePlanName(f), ["reason"] = reason });
            }
            // Named families the caller asked for by id are a statement: one that cannot be
            // written refuses the whole call. A category sweep reports what it skipped.
            if (ids != null && refusedRows.Count > 0)
                return CommandResult.FailWithDetail("Refused by name: " + string.Join("; ", refusedRows.Select(r => r["id"] + " " +
                    (string)r["name"] + " - " + (string)r["reason"])) + ". Nothing was exported.", new JObject { ["refused"] = refusedRows });
            if (loadable.Count == 0)
                return CommandResult.FailWithDetail("No loadable family to export" + (categoryName != null ? " in category '" + categoryName + "'" : "") +
                    ". Nothing was exported.", new JObject { ["refused"] = refusedRows });
            List<string> stems = ExportFormatRules.FamilyStems(loadable.Select(f => SafePlanName(f)).ToList(), out string stemRefusal);
            if (stems == null) return CommandResult.Fail(stemRefusal + " Nothing was exported.");
            string[] targets = stems.Select(s => System.IO.Path.Combine(output, s + ".rfa")).ToArray();
            List<string> existing = targets.Where(File.Exists).ToList();
            if (existing.Count > 0)
                return CommandResult.Fail("rfa never overwrites, and these files exist: " + string.Join(", ", existing) + ". Nothing was exported.");

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            OperationGateResult gateDecision = OperationGate.Evaluate(app, doc, request["require_gate"], GatedOperation.Export, Name);
            if (gateDecision.Refusal != null) return gateDecision.Refusal;
            string planHash = DocumentGate.PlanHash(request, "format", "output_path", "family_ids", "category");
            ResolvedPlan resolvedPlan = NewPlan(app, gate, loadable, targets, false);
            var familyRows = new JArray(loadable.Select((f, i) => new JObject
            {
                ["id"] = Rid.Value(f.Id), ["name"] = SafePlanName(f), ["category"] = SafeCategoryName(f), ["file"] = targets[i]
            }));
            if (dryRun)
            {
                var plan = new JObject
                {
                    ["dry_run"] = true, ["format"] = "rfa", ["output_folder"] = output, ["planned_files"] = new JArray(targets),
                    ["families"] = familyRows, ["refused"] = refusedRows, ["resolved_from_type_ids"] = resolvedTypes,
                    ["note"] = "Nothing was exported and no file was created."
                };
                if (gateDecision.Requested) plan["prevention"] = gateDecision.Prevention;
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(plan, gate, Name, planHash, true,
                    "the token binds the folder, the IDENTITY of every family and which destination files exist.");
                return CommandResult.Ok(plan);
            }
            if (doc.IsModifiable || doc.IsReadOnly)
                return CommandResult.Fail("Document.EditFamily cannot run while the document is " + (doc.IsModifiable ? "modifiable (an open transaction)" : "read-only") + ".");
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            var files = new JArray();
            int verified = 0;
            for (int i = 0; i < loadable.Count; i++)
            {
                var row = (JObject)familyRows[i].DeepClone();
                Document familyDoc = null;
                try
                {
                    // EditFamily hands back an independent in-memory copy; saving it writes the
                    // .rfa and changes nothing in the project. Closed without saving again.
                    familyDoc = doc.EditFamily(loadable[i]);
                    familyDoc.SaveAs(targets[i], new SaveAsOptions { OverwriteExistingFile = false });
                }
                catch (Exception ex) { row["error"] = ex.Message; }
                finally
                {
                    if (familyDoc != null) { try { familyDoc.Close(false); } catch (Exception ex) { row["close_error"] = ex.Message; } }
                }
                bool ok = false;
                try
                {
                    if (File.Exists(targets[i]))
                    {
                        var info = new FileInfo(targets[i]);
                        row["bytes"] = info.Length;
                        BasicFileInfo basic = BasicFileInfo.Extract(targets[i]);
                        row["saved_in_format"] = basic.Format;
                        ok = info.Length > 0 && !string.IsNullOrEmpty(basic.Format);
                    }
                    else row["error"] = row["error"] ?? "the file does not exist after SaveAs";
                }
                catch (Exception ex) { row["read_back_error"] = ex.Message; }
                row["verified"] = ok;
                if (ok) verified++;
                files.Add(row);
            }
            var result = new JObject
            {
                ["format"] = "rfa", ["output_folder"] = output, ["files_planned"] = targets.Length, ["files_verified"] = verified,
                ["files"] = files, ["refused"] = refusedRows, ["resolved_from_type_ids"] = resolvedTypes,
                ["means"] = "verified = the .rfa exists, is non-empty and BasicFileInfo.Extract reads its saved-in Revit format back."
            };
            if (gateDecision.Requested) result["prevention"] = gateDecision.Prevention;
            if (verified != targets.Length)
            {
                result["external_files_may_exist"] = true; result["rollback_available"] = false;
                return CommandResult.FailWithDetail(verified + " of " + targets.Length + " families verified as .rfa. Success is not claimed.", result);
            }
            return CommandResult.Ok(result);
        }

        private static Category ResolveCategory(Document doc, string token)
        {
            if (token.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(token, true, out BuiltInCategory bic))
            {
                try { return Category.GetCategory(doc, bic); } catch { return null; }
            }
            foreach (Category c in doc.Settings.Categories)
                if (string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        private static string SafeCategoryName(Family f)
        {
            try { return f.FamilyCategory?.Name; } catch { return null; }
        }
    }
}
