// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The first command, and the smoke test for the whole pipeline: transport ->
// dispatcher -> UI thread -> Revit API -> result -> back out. Read-only.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class GetDocumentInfoCommand : ICommand
    {
        public string Name => "get_document_info";
        public string Description => "Basic facts about the active document: title, path, version, element count.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            int elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementCount();

            // The shared-coordinate facts a tabular placement or a survey hand-off
            // needs, read from the ACTIVE project location. Unreadable stays null
            // with the reason beside it rather than pretending zeroes.
            object sharedCoordinates = null;
            try
            {
                ProjectLocation location = doc.ActiveProjectLocation;
                ProjectPosition position = location?.GetProjectPosition(XYZ.Zero);
                if (position != null)
                    sharedCoordinates = new
                    {
                        active_location = location.Name,
                        east_west_mm = Math.Round(position.EastWest * 304.8, 1),
                        north_south_mm = Math.Round(position.NorthSouth * 304.8, 1),
                        elevation_mm = Math.Round(position.Elevation * 304.8, 1),
                        angle_to_true_north_degrees = Math.Round(position.Angle * 180.0 / Math.PI, 4)
                    };
            }
            catch { }

            // D0 (3.0.0 interoperability): the evidence a project registry binds a model_ref to.
            // Every value is read the same way in every year; what a year cannot provide is null
            // with the reason in identity_note, never a guess.
            Newtonsoft.Json.Linq.JObject modelIdentity = ModelIdentity.Evidence(
#if REVIT2023
                null,                                           // Document.CreationGUID arrived in Revit 2024
#else
                SafeText(() => doc.CreationGUID.ToString()),
#endif
                SafeText(() => doc.WorksharingCentralGUID.ToString()),
                SafeText(() => { var p = doc.GetCloudModelPath(); return p == null ? null : p.GetModelGUID().ToString(); }),
                SafeText(() => { var p = doc.GetCloudModelPath(); return p == null ? null : p.GetProjectGUID().ToString(); }),
                null,                                           // the header's Unique Document GUID has no API: not read
                SafeText(() => doc.ProjectInformation?.UniqueId),
                app.Application.VersionNumber,
                SafeText(() => doc.Title),
                SafeText(() => doc.PathName));

            return CommandResult.Ok(new
            {
                title = doc.Title,
                path = doc.PathName,
                is_workshared = doc.IsWorkshared,
                is_family_document = doc.IsFamilyDocument,
                revit_version = app.Application.VersionNumber,
                revit_build = app.Application.VersionBuild,
                element_count = elements,
                shared_coordinates = sharedCoordinates,
                model_identity = modelIdentity
            });
        }

        // A cloud path on a local model, a central GUID on a file-based one: "not applicable"
        // throws on some years. That is null, not an unreadable document.
        private static string SafeText(Func<string> read)
        {
            try { return read(); } catch (Exception) { return null; }
        }
    }
}
