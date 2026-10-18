using System.Linq;
// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Open a model and make it active — with the guards that make this command safe to
// hand to an automation instead of a person.
//
// THE GUARDS THEMSELVES LIVE IN Core/OpenGuard.cs, shared with
// horizun_document_session's open. They used to be written out here and again
// there, months apart, and each copy ended up strict about something the other was
// not: this one had the central guard and no newer-file rule, that one had the
// newer-file rule and no central guard at all. Neither file shows that on its own.
// Read OpenGuard.cs for what they are and why; this file is now about what happens
// AFTER the guards say yes.
//
// And what happens after is: the active document is re-read and compared to what
// was asked, because "Revit did not throw" is not evidence that the document you
// now hold is the one you named.
//
// CLOUD MODELS (ACC / BIM 360) are opened by GUID instead of by path, and that
// changes what can be promised — so it says so rather than reporting the same
// fields and hoping:
//
//   * THE VERSION GUARD CANNOT RUN. BasicFileInfo reads a file on disk; a cloud
//     model has no path to read before it is open, so its saved version is
//     unknowable up front. The response reports version_guard="not_applicable_cloud"
//     and a null file_saved_in_version. That is not the same as "checked and fine",
//     and it is not spelled like it.
//
//   * A BREADCRUMB IS LEFT BEFORE THE OPEN. Opening a cloud model can take Revit
//     down with it — an access violation inside Revit's own loader, with nothing
//     on the managed side to catch. Measured on 2026-07-30 over 24 ACC models:
//     Revit 2025.4 died twice with 0xc0000005 in SelectedPartitionsForEdit /
//     decommitDocument, and the only record of WHICH model was in Revit's own
//     journal — the process that knew could no longer write. So the model being
//     attempted goes to the log BEFORE the call. A line with no result after it
//     names the model that did it.
//
//     The follow-up is why open_all_worksets defaults to false. Both crashes were
//     the same signature, both with 30 GB of RAM free, and BOTH MODELS OPENED AND
//     READ FINE once the all-worksets configuration was dropped. That makes it the
//     option, not the models — SelectedPartitionsForEdit is workset code. Reach for
//     it when something downstream measures worksets, and drop it first when a
//     specific model dies on open.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed class OpenDocumentCommand : ICommand
    {
        public string Name => "horizun_open_document";

        public string Description =>
            "Open a .rvt/.rfa by path, or a cloud model (ACC / BIM 360) by GUID, and make it the active " +
            "document. Shares one set of guards with horizun_document_session: REFUSES a file saved in a " +
            "different Revit than the one running (opening it upgrades it irreversibly) unless " +
            "allow_upgrade=true, refuses a NEWER file outright because no flag can downgrade one, and refuses " +
            "a CENTRAL model — including a cloud model, which is a central — unless detach=true or " +
            "open_central=true. By GUID the version guard CANNOT run, because a cloud model's saved version is " +
            "not readable before it is open, and the response says so instead of implying it was checked. " +
            "Either way the active document is re-read and compared to what you asked for.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject req;
            try { req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var request = new OpenRequest
            {
                CommandName = Name,
                Path = req.Value<string>("path"),
                CloudProjectGuid = req.Value<string>("cloud_project_guid"),
                CloudModelGuid = req.Value<string>("cloud_model_guid"),
                CloudRegion = req.Value<string>("cloud_region"),
                // Accepted here, required by document_session. Same rule, applied the same
                // way; only whether it may be absent differs between the two tools.
                ExpectedVersion = req.Value<string>("expected_version"),
                ExpectedVersionRequired = false,
                AllowUpgrade = req.Value<bool?>("allow_upgrade") ?? false,
                Detach = req.Value<bool?>("detach") ?? false,
                Audit = req.Value<bool?>("audit") ?? false,
                OpenCentral = req.Value<bool?>("open_central") ?? false,
                OpenAllWorksets = req.Value<bool?>("open_all_worksets") ?? false,
                OnOpenDialog = OpenRequest.ParseDialogAnswer(req.Value<string>("on_open_dialog"), out string dialogError)
            };
            if (dialogError != null) return CommandResult.Fail(dialogError);

            OpenPlan plan = OpenGuard.Check(app, request);
            if (!plan.Ok) return plan.Refusal;

            return plan.IsCloud ? OpenCloud(app, plan, request) : OpenLocal(app, plan, request);
        }

        // ---------------------------------------------------------------- cloud
        private static CommandResult OpenCloud(UIApplication app, OpenPlan plan, OpenRequest r)
        {
            // THE BREADCRUMB. Written BEFORE the call, because the failure this guards against is not
            // an exception - it is Revit dying mid-open and taking the answer with it. A line here with
            // no result line after it names the model that did it.
            Log.Warn("open_document CLOUD attempting region=" + plan.Region + " project=" + plan.CloudProject +
                     " model=" + plan.CloudModel + (r.Detach ? " detached" : "") +
                     (r.OpenAllWorksets ? " all-worksets" : "") +
                     " - if no result line follows this one, opening this model took Revit down.");

            Document opened;
            try
            {
                UIDocument uidoc;
                using (Interference.WithDialogAnswer(r.OnOpenDialog))
                    uidoc = app.OpenAndActivateDocument(plan.ModelPath, plan.Options(), false);
                opened = uidoc != null ? uidoc.Document : null;
            }
            catch (Exception ex)
            {
                Log.Warn("open_document CLOUD refused model=" + plan.CloudModel + ": " + ex.Message);
                return CommandResult.Fail(
                    "Revit refused to open the cloud model: " + ex.Message +
                    " (region '" + plan.Region + "', project " + plan.CloudProject + ", model " + plan.CloudModel +
                    "). 'The central model is missing' usually means the GUIDs are not this Revit's - the URN " +
                    "from the ACC web UI is a different identifier and does not convert. Nothing was opened.");
            }

            if (opened == null)
                return CommandResult.Fail(
                    "Revit returned no document for cloud model " + plan.CloudModel + ". Nothing is confirmed open.");

            Log.Info("open_document CLOUD opened '" + opened.Title + "' (model " + plan.CloudModel + ")");

            Document nowActive = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            bool activeConfirmed = OpenGuard.SameDocument(nowActive, opened);
            Guid actualProject;
            Guid actualModel;
            string cloudIdentityError;
            bool cloudIdentityConfirmed = TryReadCloudIdentity(nowActive, out actualProject, out actualModel,
                                                               out cloudIdentityError) &&
                                          actualProject == plan.CloudProject && actualModel == plan.CloudModel;

            if (!activeConfirmed || !cloudIdentityConfirmed)
            {
                return CommandResult.Fail(
                    "A DOCUMENT WAS OPENED, but the requested cloud model is not proven active. " +
                    "active_document_matches_returned=" + activeConfirmed +
                    ", cloud_identity_matches_request=" + cloudIdentityConfirmed +
                    (cloudIdentityError == null ? "" : ", cloud_identity_error=" + cloudIdentityError) +
                    ". The opened document remains in this Revit session; do not run another command until " +
                    "you identify or close it.");
            }

            string centralPath = null;
            bool? isWorkshared = null, isInCloud = null;
            try { isWorkshared = opened.IsWorkshared; } catch { }
            try { isInCloud = opened.IsModelInCloud; } catch { }
            try
            {
                if (opened.IsWorkshared && opened.GetWorksharingCentralModelPath() != null)
                    centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(
                        opened.GetWorksharingCentralModelPath());
            }
            catch { }

            return CommandResult.Ok(new
            {
                opened = true,
                confirmed_active = true,
                source = "cloud",
                cloud_region = plan.Region,
                cloud_project_guid = actualProject.ToString(),
                cloud_model_guid = actualModel.ToString(),
                cloud_identity_matches_request = true,
                active_document = nowActive == null ? null : nowActive.Title,
                // A cloud document's PathName is not a file you can open again; it is not reported as one.
                active_path = (string)null,
                path_matches_request = (bool?)null,
                file_saved_in_version = (string)null,
                running_revit_version = plan.HostVersion,
                version_guard = plan.VersionGuard,
                version_guard_note =
                    "The upgrade guard did NOT run. It reads the saved version out of the file with " +
                    "BasicFileInfo, and a cloud model has no local file to read before it is open - so its " +
                    "version was unknown at the moment of opening, not verified. If this model belongs to a " +
                    "different Revit year, Revit decided what to do about that, not this command.",
                detached = r.Detach,
                audited = r.Audit,
                all_worksets_opened = r.OpenAllWorksets,
                is_workshared = isWorkshared,
                is_model_in_cloud = isInCloud,
                central_path = centralPath,
                central_guard = r.Detach ? "detached" : "open_central",
                note = (string)null
            });
        }

        // ---------------------------------------------------------------- local
        private static CommandResult OpenLocal(UIApplication app, OpenPlan plan, OpenRequest r)
        {
            string path = r.Path;
            if (plan.AlreadyOpen != null) return ActivateOpen(app, plan, r);

            // A model loaded as a LINK in a document already open cannot be opened as a document of its own:
            // Revit returns null with no dialog (measured 2026-10-16: ACA_EST_POR while ACA_ARQ_POR, which links it, was open).
            string linkedIn = LinkedInOpenDocuments(app, path);
            if (linkedIn != null)
                return CommandResult.Fail("'" + Path.GetFileName(path) + "' is loaded as a LINK in the open document(s) " + linkedIn +
                    ", and Revit cannot open a model that is linked in this session. Close those documents (or unload the link) " +
                    "and retry. Nothing was opened.");

            Document opened;
            try
            {
                UIDocument uidoc;
                using (Interference.WithDialogAnswer(r.OnOpenDialog))
                    uidoc = app.OpenAndActivateDocument(plan.ModelPath, plan.Options(), false);
                opened = uidoc != null ? uidoc.Document : null;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Revit refused to open the file: " + ex.Message +
                    " (file is Revit " + (plan.FileVersion ?? "unknown") + ", host is Revit " + plan.HostVersion + ")");
            }

            if (opened == null)
            {
                // SAY WHAT REVIT ASKED: an open that silently returns nothing is almost always a dialog that was
                // answered Cancel (measured 2026-10-16: 'Unresolved References' on a model from another team).
                string asked = "";
                try
                {
                    var seen = Interference.Current?.Seen;
                    if (seen != null && seen.Count > 0)
                        asked = " Revit raised while opening: " + string.Join(" | ", seen.Where(i => i.Kind == "dialog")
                            .Select(i => (i.Description ?? "?") + " -> " + (i.Answered ?? "?")).Take(5)) +
                            (r.OnOpenDialog == DialogAnswer.Dismiss ? "" : ". If the model is only to be READ, on_open_dialog='dismiss' answers 'continue'.");
                }
                catch { }
                return CommandResult.Fail("Revit returned no document for '" + path + "'. Nothing is confirmed open." + asked);
            }

            // --- Verify: is the ACTIVE document really the one asked for? -----
            Document nowActive = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            string activePath = nowActive != null ? nowActive.PathName : null;

            // A detached model deliberately has no path that exists, so it is confirmed by
            // identity, not by path.
            bool activeConfirmed = OpenGuard.SameDocument(nowActive, opened);
            bool pathMatches = PathsEqual(activePath, path);
            bool sourceConfirmed = r.Detach
                ? activeConfirmed && TitleIdentifies(opened.Title, path)
                : pathMatches;

            if (!activeConfirmed || !sourceConfirmed)
            {
                return CommandResult.Fail(
                    "A DOCUMENT WAS OPENED, but the requested file is not proven active. " +
                    "active_document_matches_returned=" + activeConfirmed +
                    ", source_matches_request=" + sourceConfirmed +
                    ", active_path='" + (activePath ?? "(none)") + "', requested_path='" + path + "'. " +
                    "The opened document remains in this Revit session; do not run another command until you " +
                    "identify or close it.");
            }

            return CommandResult.Ok(new
            {
                opened = true,
                confirmed_active = true,
                source = "local",
                path_matches_request = r.Detach ? (bool?)null : pathMatches,
                requested_path = path,
                active_document = nowActive == null ? null : nowActive.Title,
                active_path = string.IsNullOrEmpty(activePath) ? null : activePath,
                file_saved_in_version = plan.FileVersion,
                running_revit_version = plan.HostVersion,
                version_guard = plan.VersionGuard,
                upgraded_on_open = plan.WillUpgrade,
                detached = r.Detach,
                audited = r.Audit,
                all_worksets_opened = r.OpenAllWorksets,
                was_central = plan.FileIsCentral,
                central_path = plan.CentralPath,
                is_workshared = plan.FileIsWorkshared,
                central_guard = plan.FileIsCentral == true
                    ? (r.Detach ? "detached" : "open_central")
                    : "not_a_central",
                note = plan.WillUpgrade
                    ? "This file was saved in Revit " + plan.FileVersion + " and has now been UPGRADED to " +
                      plan.HostVersion + ". That is permanent. Saving it writes the new version to disk; closing " +
                      "without saving leaves the file on disk as it was."
                    : null
            });
        }

        // ---------------------------------------------------------------- already open
        /// <summary>
        /// The path is already open in this session: ACTIVATE it, never open it again. No
        /// upgrade guard ran, because none applies - the document is in memory already and
        /// this call cannot change its version. It used to demand allow_upgrade here, read
        /// off a disk header that still said 2023 for a model an earlier open had upgraded
        /// in memory (course dry run 2026-09-30, defect #13). Activation goes through the
        /// same bare-path OpenAndActivateDocument document_session uses for this case, and
        /// is proven like an open: the active document must be that document, at that path.
        /// </summary>
        private static CommandResult ActivateOpen(UIApplication app, OpenPlan plan, OpenRequest r)
        {
            Document already = plan.AlreadyOpen;
            Document before = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            bool wasActive = OpenGuard.SameDocument(before, already);
            if (!wasActive)
            {
                try
                {
                    using (Interference.WithDialogAnswer(r.OnOpenDialog))
                        app.OpenAndActivateDocument(r.Path);
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail(
                        "The requested document is already open in this session, but Revit refused to make it " +
                        "active: " + ex.Message + ". It remains open in the background; nothing was opened or " +
                        "upgraded by this call.");
                }
            }

            Document nowActive = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            string activePath = nowActive != null ? nowActive.PathName : null;
            if (!OpenGuard.SameDocument(nowActive, already) || !PathsEqual(activePath, r.Path))
                return CommandResult.Fail(
                    "The requested document is already open, but it is NOT proven active after the activation " +
                    "attempt (active_path='" + (activePath ?? "(none)") + "', requested_path='" + r.Path + "'). " +
                    "Refusing to report success: the next command would target another document.");

            return CommandResult.Ok(new
            {
                opened = false,
                opened_now = false,
                already_open = true,
                status = wasActive ? "already_open_and_active" : "already_open_activated",
                confirmed_active = true,
                source = "local",
                path_matches_request = true,
                requested_path = r.Path,
                active_document = nowActive.Title,
                active_path = activePath,
                file_saved_in_version = plan.FileVersion,
                running_revit_version = plan.HostVersion,
                version_guard = plan.VersionGuard,
                upgraded_on_open = false,
                detached = false,
                audited = false,
                all_worksets_opened = false,
                was_central = plan.FileIsCentral,
                central_path = plan.CentralPath,
                is_workshared = plan.FileIsWorkshared,
                central_guard = plan.CentralGuard,
                note = "This document was already open in this session, so it was ACTIVATED, not opened again: this " +
                       "call opened, upgraded and modified nothing, and needed no allow_upgrade. file_saved_in_version " +
                       "is the header ON DISK" +
                       (plan.FileVersion == null
                           ? " (unreadable here)."
                           : OpenGuard.SameVersion(plan.FileVersion, plan.HostVersion)
                           ? "."
                           : ", which differs from this Revit (" + plan.HostVersion + "): the document in memory " +
                             "was upgraded when it was first opened, and saving it writes Revit " + plan.HostVersion +
                             " over that file.")
            });
        }

        /// <summary>The titles of the open documents that hold <paramref name="path"/> as a loaded link, or null.</summary>
        static string LinkedInOpenDocuments(UIApplication app, string path)
        {
            try
            {
                var docs = app.Application.Documents.Cast<Document>().ToList();
                if (!docs.Any(d => d.IsLinked && PathsEqual(d.PathName, path))) return null;
                var hosts = docs.Where(d => !d.IsLinked && new FilteredElementCollector(d).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                                         .Any(li => { var ld = li.GetLinkDocument(); return ld != null && PathsEqual(ld.PathName, path); }))
                                .Select(d => "'" + d.Title + "'").Distinct().ToList();
                return hosts.Count > 0 ? string.Join(", ", hosts) : "(a document of this session)";
            }
            catch { return null; }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool TitleIdentifies(string title, string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(requestedPath)) return false;
            string expected;
            try { expected = Path.GetFileNameWithoutExtension(requestedPath); }
            catch { return false; }
            return string.Equals(title, expected, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(title, expected + "_detached", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(title, expected + "_desenlazado", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadCloudIdentity(Document doc, out Guid project, out Guid model, out string error)
        {
            project = Guid.Empty;
            model = Guid.Empty;
            error = null;
            try
            {
                ModelPath path = doc == null ? null : doc.GetCloudModelPath();
                if (path == null)
                {
                    error = "GetCloudModelPath returned null";
                    return false;
                }
                project = path.GetProjectGUID();
                model = path.GetModelGUID();
                return project != Guid.Empty && model != Guid.Empty;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
