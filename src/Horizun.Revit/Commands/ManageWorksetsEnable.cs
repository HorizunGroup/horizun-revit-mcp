// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_worksets enable / create_local. Before these two operations the
// bridge could not make a shared model at all: enabling worksharing needed Python
// (Pro 3.1.0 live test, 2026-10-18 - sync_with_central and relinquish_own could
// only be proved with unit tests because no typed path made a central).
//
//   * enable: Document.EnableWorksharing(levels_grids_name, name) and, in the same
//     call, SaveAs a NEW central at central_path (SaveAsCentral). Neither step can
//     be rolled back, so the dry run is a MEASURED preview (checks and names) and
//     says so; the apply re-reads the document (IsWorkshared, the two user
//     worksets, PathName) and the file on disk (BasicFileInfo: workshared, central).
//     Nothing is overwritten: central_path must not exist.
//   * create_local: WorksharingUtils.CreateNewLocal(central, local) and the local
//     header is re-read (workshared, the central it records). It does not open the
//     local: horizun_open_document path=<local> open_central=true does, because a
//     local made this way reads as a central until Revit opens and saves it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageWorksetsCommand
    {
        private const string PreviewNote =
            "Enabling worksharing and writing a central cannot be rolled back, so this dry run is a measured preview of " +
            "the checks, not a provisional change. Apply does it and re-reads the document and the file.";

        private CommandResult Enable(UIApplication app, GateResult gate, JObject r)
        {
            Document doc = gate.Document;
            if (OpenRecoveryDocuments.IsMarked(doc))
                return Refused("enable", "A document recovered in memory can only be saved with a confirmed save_as to a new path.");
            if (doc.IsFamilyDocument) return Refused("enable", "a family document cannot be workshared.");
            if (doc.IsWorkshared)
                return CommandResult.FailWithDetail("'" + doc.Title + "' is already workshared. Nothing was written.",
                    new JObject { ["state"] = "refused", ["code"] = "already_workshared", ["operation"] = "enable", ["write_started"] = false });
            string central = (r.Value<string>("central_path") ?? "").Trim();
            string levels = r.Value<string>("levels_grids_name") ?? WorksharingEnableRules.DefaultLevelsGrids;
            string name = r.Value<string>("name") ?? WorksharingEnableRules.DefaultWorkset;
            string problem = WorksharingEnableRules.EnableProblem(central, SafeExists(central), FolderExists(central), levels, name);
            if (problem != null) return Refused("enable", problem);

            string hash = DocumentGate.PlanHash(r, "operation", "central_path", "name", "levels_grids_name");
            ResolvedPlan resolved = FilePlan(gate, app, "enable", new Dictionary<string, string>
            {
                ["central_path"] = central, ["levels_grids_name"] = levels, ["name"] = name, ["document_path"] = doc.PathName ?? ""
            });
            var plan = new JObject
            {
                ["operation"] = "enable", ["central_path"] = central, ["levels_grids_name"] = levels, ["name"] = name,
                ["document_path"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName
            };
            if (r["dry_run"] == null || r.Value<bool>("dry_run"))
            {
                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true, ["plan"] = plan,
                    ["rehearsal"] = new JObject { ["rehearsal_kind"] = "measured_preview", ["verified"] = true, ["is_workshared"] = false, ["note"] = PreviewNote }
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, Name, hash, true,
                    "the token binds this document, central_path and both workset names.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, r, Name, hash, resolved, null);
            if (refusal != null) return refusal;
            if (SafeExists(central)) return Refused("enable", "central_path '" + central + "' appeared after the dry run; nothing is overwritten.");

            try { doc.EnableWorksharing(levels, name); }
            catch (Exception ex)
            {
                bool shared = SafeWorkshared(doc);
                return CommandResult.FailWithDetail("EnableWorksharing failed: " + ex.Message,
                    new JObject { ["state"] = shared ? "uncertain" : "failed", ["write_started"] = true, ["is_workshared"] = shared, ["central_written"] = false });
            }
            try
            {
                var options = new SaveAsOptions { OverwriteExistingFile = false };
                options.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                doc.SaveAs(ModelPathUtils.ConvertUserVisiblePathToModelPath(central), options);
            }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail("Worksharing is enabled in memory but saving the central failed: " + ex.Message +
                    ". The file at central_path was not vouched for; the document is unsaved and workshared.",
                    new JObject { ["state"] = "uncertain", ["write_started"] = true, ["is_workshared"] = SafeWorkshared(doc), ["central_written"] = SafeExists(central),
                                  ["central_file"] = BasicFileProbe.Read(central) });
            }

            JObject file = BasicFileProbe.Read(central);
            List<string> userNames = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Select(w => w.Name).ToList();
            List<string> problems = WorksharingEnableRules.EnableVerdict(SafeWorkshared(doc), userNames, levels, name, doc.PathName, central,
                file.Value<bool?>("is_workshared"), file.Value<bool?>("is_central"));
            bool ok = problems.Count == 0;
            var done = new JObject
            {
                ["state"] = ok ? "committed_verified" : "uncertain", ["host_verified"] = ok, ["operation"] = "enable",
                ["plan"] = plan, ["document_path"] = doc.PathName, ["central_file"] = file,
                ["active_workset_id"] = doc.GetWorksetTable().GetActiveWorksetId().IntegerValue,
                ["worksets"] = Table(doc), ["problems"] = new JArray(problems), ["geometry_untouched"] = true,
                ["next"] = "This document IS the central now. To work in a local: create_local, then horizun_open_document path=<local> open_central=true."
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, 1, 1, ok ? 1 : 0, 0, 0, ok ? 0 : 1);
            DocumentGate.StampConfirmation(done, gate, Name, hash, false);
            done["document_before"] = done["document"];
            done["document"] = doc.Title + " [" + doc.PathName + "]";
            if (!ok) return CommandResult.FailWithDetail("Worksharing was enabled but a postcondition does not re-read as planned: " + string.Join("; ", problems) + ".", done);
            return CommandResult.Ok(done);
        }

        private CommandResult CreateLocal(UIApplication app, GateResult gate, JObject r)
        {
            string central = (r.Value<string>("central_path") ?? "").Trim();
            string local = (r.Value<string>("local_path") ?? "").Trim();
            JObject centralFile = BasicFileProbe.Read(central);
            string problem = WorksharingEnableRules.LocalProblem(central, centralFile.Value<bool?>("is_workshared"), local, SafeExists(local), FolderExists(local));
            if (problem != null) return Refused("create_local", problem);

            string hash = DocumentGate.PlanHash(r, "operation", "central_path", "local_path");
            ResolvedPlan resolved = FilePlan(gate, app, "create_local", new Dictionary<string, string>
            {
                ["central_path"] = central, ["local_path"] = local, ["central_modified_utc"] = centralFile.Value<string>("modified_utc") ?? ""
            });
            var plan = new JObject { ["operation"] = "create_local", ["central_path"] = central, ["local_path"] = local, ["central_file"] = centralFile };
            if (r["dry_run"] == null || r.Value<bool>("dry_run"))
            {
                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true, ["plan"] = plan,
                    ["rehearsal"] = new JObject { ["rehearsal_kind"] = "measured_preview", ["verified"] = true,
                        ["note"] = "A new file is written only at local_path, which does not exist; the central is read, not changed." }
                };
                ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, Name, hash, true, "the token binds central_path and local_path.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, r, Name, hash, resolved, null);
            if (refusal != null) return refusal;
            if (SafeExists(local)) return Refused("create_local", "local_path '" + local + "' appeared after the dry run; nothing is overwritten.");
            try
            {
                WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(central),
                                                ModelPathUtils.ConvertUserVisiblePathToModelPath(local));
            }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail("CreateNewLocal failed: " + ex.Message,
                    new JObject { ["state"] = SafeExists(local) ? "uncertain" : "failed", ["write_started"] = true, ["local_file"] = BasicFileProbe.Read(local) });
            }
            JObject localFile = BasicFileProbe.Read(local);
            var problems = new List<string>();
            if (localFile.Value<bool?>("exists") != true) problems.Add("the local file does not exist");
            if (localFile.Value<bool?>("is_workshared") != true) problems.Add("the local file does not read as workshared");
            if (!WorksharingEnableRules.SamePath(localFile.Value<string>("central_path"), central))
                problems.Add("the local records '" + localFile.Value<string>("central_path") + "' as its central");
            bool ok = problems.Count == 0;
            var done = new JObject
            {
                ["state"] = ok ? "committed_verified" : "uncertain", ["host_verified"] = ok, ["operation"] = "create_local",
                ["plan"] = plan, ["local_file"] = localFile, ["problems"] = new JArray(problems),
                ["next"] = "Open it with horizun_open_document path=<local_path> open_central=true: until Revit opens and saves it, a new local reads as a central."
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed, 1, 1, ok ? 1 : 0, 0, 0, ok ? 0 : 1);
            DocumentGate.StampConfirmation(done, gate, Name, hash, false);
            if (!ok) return CommandResult.FailWithDetail("The local was written but does not re-read as planned: " + string.Join("; ", problems) + ".", done);
            return CommandResult.Ok(done);
        }

        private static CommandResult Refused(string op, string why) =>
            CommandResult.FailWithDetail(why + " Nothing was written.",
                new JObject { ["state"] = "refused", ["operation"] = op, ["write_started"] = false });

        private static ResolvedPlan FilePlan(GateResult gate, UIApplication app, string op, Dictionary<string, string> values)
        {
            var rp = new ResolvedPlan { Command = "horizun_manage_worksets", DocumentKey = gate.Fingerprint, RevitVersion = app.Application.VersionNumber, DocumentFingerprint = gate.Identity.FingerprintDigest() };
            rp.Elements.Add(new PlannedElement { UniqueId = "worksets:" + op, Category = "workshared_file", Action = PlannedAction.Create, BeforeValues = values });
            return rp;
        }

        private static bool SafeExists(string path) { try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); } catch { return false; } }

        private static bool FolderExists(string path)
        {
            try { string dir = Path.GetDirectoryName(path); return !string.IsNullOrEmpty(dir) && Directory.Exists(dir); }
            catch { return false; }
        }

        private static bool SafeWorkshared(Document doc) { try { return doc.IsWorkshared; } catch { return false; } }
    }
}
