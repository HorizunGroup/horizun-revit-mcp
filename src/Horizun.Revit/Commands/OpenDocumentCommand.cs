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
            try { req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
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
            bool confirmed = OpenGuard.SameDocument(nowActive, opened);

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
                confirmed_active = confirmed,
                source = "cloud",
                cloud_region = plan.Region,
                cloud_project_guid = plan.CloudProject.ToString(),
                cloud_model_guid = plan.CloudModel.ToString(),
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
                note = confirmed
                    ? null
                    : "Revit opened a document but it is NOT the active one. Do not run anything that assumes " +
                      "the active document is the model you named."
            });
        }

        // ---------------------------------------------------------------- local
        private static CommandResult OpenLocal(UIApplication app, OpenPlan plan, OpenRequest r)
        {
            string path = r.Path;

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
                return CommandResult.Fail("Revit returned no document for '" + path + "'. Nothing is confirmed open.");

            // --- Verify: is the ACTIVE document really the one asked for? -----
            Document nowActive = app.ActiveUIDocument != null ? app.ActiveUIDocument.Document : null;
            string activePath = nowActive != null ? nowActive.PathName : null;

            // A detached model deliberately has no path that exists, so it is confirmed by
            // identity, not by path.
            bool confirmed = OpenGuard.SameDocument(nowActive, opened);
            bool pathMatches = !string.IsNullOrEmpty(activePath) &&
                               string.Equals(Path.GetFullPath(activePath), Path.GetFullPath(path),
                                             StringComparison.OrdinalIgnoreCase);

            return CommandResult.Ok(new
            {
                opened = true,
                confirmed_active = confirmed,
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
                    : (confirmed ? null : "Revit opened a document but it is NOT the active one. Do not run " +
                                          "anything that assumes the active document is the file you named.")
            });
        }
    }
}
