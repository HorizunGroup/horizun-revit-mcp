// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_document_session operation=new_project: a blank project from a template.
//
// The decisions - never overwrite, which template, a newer template, the token's
// identity - are NewProjectRules (Revit-free, unit-tested). This file is the Revit
// half, and its whole job is to report only what it re-read:
//
//   1. Application.NewProjectDocument(template) creates the project IN MEMORY, as a
//      background document with no window.
//   2. Document.SaveAs(target) with OverwriteExistingFile=false writes it - to a path
//      the rehearsal and this call both proved empty.
//   3. THE FILE is re-read: it exists, it has a size, and BasicFileInfo reads a Revit
//      year off it that is this host's. The DOCUMENT is re-read: its PathName is the
//      requested path. Either check failing closes the document without saving and
//      deletes the file this call created - the path is put back as it was, empty.
//   4. ACTIVATION is reported, never assumed. The Revit API activates a document only
//      through UIApplication.OpenAndActivateDocument; this uses the same bare-path
//      call document_session's already-open branch uses for a document that is open
//      in the background, and proves the active document is the new one afterwards.
//      A refused activation leaves a verified project open in the background, and the
//      reply says activated=false and why - the project exists either way.
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class DocumentSessionCommand
    {
        private static CommandResult NewProject(UIApplication app, JObject request)
        {
            var clock = Stopwatch.StartNew();
            string host = HostVersion(app);

            // ---- the target: absolute, .rvt, an existing folder, and EMPTY. ----
            string target = request.Value<string>("save_as_path");
            string targetProblem = NewProjectRules.TargetProblem(target, TestExists(target), FolderExists(target));
            if (targetProblem != null) return NewProjectRefuse("target_rejected", targetProblem);

            // ---- the template: named, or Revit's own default - never "none". ----
            string revitDefault = null;
            try { revitDefault = app.Application.DefaultProjectTemplate; } catch { }
            string templateProblem = NewProjectRules.ResolveTemplate(
                request.Value<string>("template_path"), revitDefault, p => TestExists(p) == true,
                out string template, out string templateSource);
            if (templateProblem != null) return NewProjectRefuse("template_rejected", templateProblem);

            JObject templateProbe = ProbeFile(template);
            string templateVersion = VersionOf(templateProbe);
            string versionProblem = NewProjectRules.TemplateVersionProblem(template, templateVersion, host);
            if (versionProblem != null) return NewProjectRefuse("template_newer_than_host", versionProblem);

            string templateStamp = (templateProbe.Value<string>("bytes") ?? "?") + "@" +
                                   (templateProbe.Value<string>("modified_utc") ?? "?");
            string documentKey = "host:" + (host ?? "unknown");
            string planHash = NewProjectRules.PlanHash(template, templateStamp, target);

            // ---- the rehearsal: the default, as for sync_with_central. ----
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            if (dryRun)
            {
                Confirmation issued = DocumentGate.Confirmations.Issue(NewProjectRules.ConfirmationScope, documentKey, planHash);
                return CommandResult.Ok(new JObject
                {
                    ["operation"] = NewProjectRules.Operation,
                    ["dry_run"] = true,
                    ["validation_level"] = "arguments_and_files",
                    ["created"] = false,
                    ["write_started"] = false, ["changes_applied"] = false, ["transaction_status"] = "not_started",
                    ["target_path"] = target,
                    ["destination_exists"] = false,
                    ["template_path"] = template,
                    ["template_source"] = templateSource,
                    ["template_file"] = templateProbe,
                    ["host_version"] = host,
                    ["confirmation_token"] = issued.Token,
                    ["confirmation_expires_utc"] = issued.ExpiresUtc.ToString("u"),
                    ["confirmation_note"] =
                        "Bound to this template (path, size and write time) and this target path. Apply with the same " +
                        "arguments, dry_run=false, this token and a new idempotency_key. A template replaced or a file " +
                        "appearing at the target in between is refused, and nothing is created.",
                    ["note"] = "NewProjectDocument was not called and nothing was written. The template's header was read " +
                               "off disk (template_file); whether Revit accepts it is proven only by the apply.",
                    ["elapsed_ms"] = clock.ElapsedMilliseconds
                });
            }

            ConfirmationCheck check = DocumentGate.Confirmations.Validate(
                request.Value<string>("confirmation_token"), NewProjectRules.ConfirmationScope, documentKey, planHash);
            if (!check.Ok)
                return NewProjectRefuse("confirmation_rejected",
                    "REFUSING TO CREATE '" + target + "': " + check.Message + " Run operation=new_project with " +
                    "dry_run=true again and apply with the token it returns. " + DocumentTokenGuidance.For("new_project") + " Nothing was created.");

            // ---- 1 + 2: create in memory, save to the proven-empty path. ----
            Document created = null;
            Interference watch = Interference.Current;
            int mark = watch == null ? 0 : watch.Seen.Count;
            try
            {
                created = app.Application.NewProjectDocument(template);
                if (created == null)
                    throw new InvalidOperationException("NewProjectDocument returned no document.");
            }
            catch (Exception ex)
            {
                var detail = NewProjectDetail("new_project_failed", target, created: false);
                detail["stage"] = NewProjectRules.FailureStage(false, ex);
                detail["template_path"] = template;
                detail["template_file"] = ProbeFile(template);
                detail["template_version"] = VersionOf((JObject)detail["template_file"]);
                detail["host_version"] = host;
                if (watch != null) detail["revit_raised"] = RaisedRecord.Window(watch.Seen, mark);
                return CommandResult.FailWithDetail("Creating the project failed: " + ex.GetType().Name + ": " + ex.Message, detail);
            }
            try
            {
                var opts = new SaveAsOptions { OverwriteExistingFile = false };
                created.SaveAs(ModelPathUtils.ConvertUserVisiblePathToModelPath(target), opts);
            }
            catch (Exception ex)
            {
                string cleanup;
                try { created.Close(false); cleanup = "The new document was closed without saving."; }
                catch (Exception closeEx) { cleanup = "The new document could not be closed: " + closeEx.Message; }
                bool? exists = TestExists(target);
                var detail = NewProjectDetail("new_project_failed", target, created: false);
                detail["stage"] = NewProjectRules.FailureStage(true, ex);
                detail["destination_exists"] = exists.HasValue ? (JToken)exists.Value : JValue.CreateNull();
                return CommandResult.FailWithDetail(
                    "Saving the project failed: " + ex.GetType().Name + ": " + ex.Message + ". " + cleanup +
                    (exists == false ? " No file was written at the destination." : exists == true ? " A file remains at the destination." : " Destination existence could not be determined."), detail);
            }

            // ---- 3: the file and the document, re-read. ----
            JObject file = ProbeFile(target);
            string fileVersion = VersionOf(file);
            string docPath = SafePath(created);
            bool pathMatches = DocIdentity.SamePath(docPath, target);
            bool fileOk = file.Value<bool?>("exists") == true && (file.Value<long?>("bytes") ?? 0) > 0 &&
                          fileVersion != null && SameVersion(fileVersion, host);
            if (!fileOk || !pathMatches)
            {
                string why = !fileOk
                    ? "the file at '" + target + "' did not re-read as a Revit " + host + " project (exists=" +
                      (file["exists"]?.ToString() ?? "null") + ", bytes=" + (file["bytes"]?.ToString() ?? "null") +
                      ", version=" + (fileVersion ?? "unreadable") +
                      (file.Value<string>("read_error") == null ? "" : ", " + file.Value<string>("read_error")) + ")"
                    : "the new document's PathName is '" + (docPath ?? "(none)") + "', not the requested path";
                string cleanup = DiscardNewProject(created, target, fileWasOurs: true);
                return CommandResult.FailWithDetail(
                    "NewProjectDocument and SaveAs raised no exception, but " + why + ". Not reported as created. " + cleanup,
                    NewProjectDetail("new_project_unverified", target, created: false));
            }

            // ---- 4: activation, proven or reported as not done. ----
            bool activated = false;
            string activationNote;
            try
            {
                app.OpenAndActivateDocument(target);
                Document active = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
                activated = OpenGuard.SameDocument(active, created) && DocIdentity.SamePath(SafePath(active), target);
                activationNote = activated
                    ? "The new project is the ACTIVE document (re-read after OpenAndActivateDocument)."
                    : "OpenAndActivateDocument returned, but the active document is '" +
                      (SafeTitle(app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null) ?? "(none)") +
                      "', not the new project. The project is open in the background; activate it with operation=open " +
                      "before running commands against it.";
            }
            catch (Exception ex)
            {
                activationNote = "Revit refused to activate the new project (" + ex.Message + "). It is created, saved and " +
                                 "open in the background; activate it with operation=open before running commands against it.";
            }

            return CommandResult.Ok(new JObject
            {
                ["operation"] = NewProjectRules.Operation,
                ["dry_run"] = false,
                ["status"] = "created",
                ["created"] = true,
                ["write_started"] = true, ["changes_applied"] = true,
                ["path"] = docPath,
                ["title"] = SafeTitle(created),
                ["path_matches_request"] = true,
                ["document_open"] = true,
                ["activated"] = activated,
                ["active_document_verified"] = activated,
                ["activation_note"] = activationNote,
                ["template_path"] = template,
                ["template_source"] = templateSource,
                ["template_version"] = templateVersion,
                ["host_version"] = host,
                ["file"] = file,
                ["created_in_version"] = fileVersion,
                ["is_workshared"] = WorksharedJson(SafeWorkshared(created)),
                ["verified_means"] =
                    "The path was proven empty before the write; afterwards a file is there with a size and a BasicFileInfo " +
                    "that reads this host's Revit year, and the new document's PathName is that path - all re-read, not " +
                    "'the call did not throw'. The template file was only read, never written.",
                ["elapsed_ms"] = clock.ElapsedMilliseconds
            });
        }

        /// <summary>
        /// Undo a creation that did not verify: close the in-memory document WITHOUT saving
        /// and delete the file this call wrote (the path was proven empty before the call).
        /// Says what it did and what it could not.
        /// </summary>
        private static string DiscardNewProject(Document created, string target, bool fileWasOurs)
        {
            var said = new System.Text.StringBuilder();
            if (created != null)
            {
                try { created.Close(false); said.Append("The new document was closed without saving. "); }
                catch (Exception ex) { said.Append("The new document could NOT be closed (" + ex.Message + ") and is still open, unsaved. "); }
            }
            if (fileWasOurs)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    said.Append("The file this call wrote at '" + target + "' was deleted; the path is empty again.");
                }
                catch (Exception ex)
                {
                    said.Append("The file this call wrote at '" + target + "' could NOT be deleted (" + ex.Message +
                                "); delete it by hand.");
                }
            }
            else said.Append("The destination file was not removed.");
            return said.ToString();
        }

        private static CommandResult NewProjectRefuse(string code, string message)
            => CommandResult.FailWithDetail(message, NewProjectDetail(code, null, created: false));

        private static JObject NewProjectDetail(string code, string target, bool created) => new JObject
        {
            ["code"] = code,
            ["operation"] = NewProjectRules.Operation,
            ["target_path"] = target,
            ["created"] = created,
            ["write_started"] = code == "new_project_failed" || code == "new_project_unverified",
            ["changes_applied"] = false,
            ["transaction_status"] = code == "new_project_failed" || code == "new_project_unverified" ? "rolled_back" : "not_started"
        };

        /// <summary>File.Exists, tri-state: null when the test itself threw.</summary>
        private static bool? TestExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return File.Exists(path); } catch { return null; }
        }

        private static bool FolderExists(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
            }
            catch { return false; }
        }
    }
}
