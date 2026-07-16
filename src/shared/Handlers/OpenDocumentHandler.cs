// -----------------------------------------------------------------------------
// Horizun hardening layer — NEW FILE (added to the rvt-mcp base by Horizun).
// Apache-2.0 (see LICENSE); this file is an original Horizun contribution.
//
// Opens (and activates) a Revit model from disk, so an agent can work against
// any file without a human first clicking File > Open. OpenAndActivateDocument
// is legal here because handlers run on the UI thread via ExternalEvent with no
// open transaction. If opening raises dialogs (missing links, upgrade notices),
// the McpDialogGuard auto-dismisses them while the command runs.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class OpenDocumentHandler : IRevitCommand
    {
        public string Name => "open_document";

        public string Description =>
            "Open a Revit model (.rvt) or family (.rfa) from disk and make it the active document. " +
            "Use before any other tool when the target model is not open yet. " +
            "If the file is already the active document this is a no-op; if it is open but not active, " +
            "it is brought to the foreground. Optional: detach (open a workshared central detached, " +
            "preserving worksets) and audit (open with audit).";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""file_path""],
  ""properties"":{
    ""file_path"":{""type"":""string"",""description"":""Absolute path to the .rvt/.rfa file.""},
    ""detach"":{""type"":""boolean"",""default"":false,""description"":""Detach from central (preserve worksets). Use for workshared models you must not touch as central.""},
    ""audit"":{""type"":""boolean"",""default"":false,""description"":""Open with audit (slower; repairs some corruption).""}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            var filePath = request.Value<string>("file_path");
            if (string.IsNullOrWhiteSpace(filePath))
                return CommandResult.Fail("file_path is required.");
            if (!Path.IsPathRooted(filePath))
                return CommandResult.Fail("file_path must be an absolute rooted path: " + filePath);
            if (!File.Exists(filePath))
                return CommandResult.Fail("File not found: " + filePath);

            var detach = request.Value<bool?>("detach") ?? false;
            var audit = request.Value<bool?>("audit") ?? false;

            // Already open? PathName comparison is the only identity we have.
            var already = app.Application.Documents
                .Cast<Document>()
                .FirstOrDefault(d => !d.IsLinked &&
                    string.Equals(d.PathName, filePath, StringComparison.OrdinalIgnoreCase));
            if (already != null)
            {
                var active = app.ActiveUIDocument?.Document;
                if (active != null && active.Equals(already))
                    return CommandResult.Ok(new
                    {
                        title = already.Title,
                        path = already.PathName,
                        status = "already_active"
                    });

                // Open but not active: OpenAndActivateDocument throws on already-open
                // files, so re-activate by showing one of its open views instead.
                var uidocExisting = new UIDocument(already);
                var view = uidocExisting.GetOpenUIViews().Count > 0
                    ? already.GetElement(uidocExisting.GetOpenUIViews()[0].ViewId) as View
                    : null;
                if (view != null)
                {
                    uidocExisting.RequestViewChange(view);
                    return CommandResult.Ok(new
                    {
                        title = already.Title,
                        path = already.PathName,
                        status = "activation_requested",
                        note = "Document was already open in another window; view change requested."
                    });
                }
                return CommandResult.Fail(
                    $"Document '{already.Title}' is already open but could not be activated. Switch to it manually.");
            }

            try
            {
                UIDocument uidoc;
                if (detach || audit)
                {
                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                    var opts = new OpenOptions { Audit = audit };
                    if (detach)
                        opts.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    uidoc = app.OpenAndActivateDocument(modelPath, opts, false);
                }
                else
                {
                    uidoc = app.OpenAndActivateDocument(filePath);
                }

                var doc = uidoc.Document;
                return CommandResult.Ok(new
                {
                    title = doc.Title,
                    path = doc.PathName,
                    status = "opened",
                    is_workshared = doc.IsWorkshared,
                    detached = detach,
                    audited = audit
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Failed to open '" + filePath + "': " + ex.Message);
            }
        }
    }
}
