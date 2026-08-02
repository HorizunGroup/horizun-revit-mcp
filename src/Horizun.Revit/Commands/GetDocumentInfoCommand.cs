// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The first command, and the smoke test for the whole pipeline: transport ->
// dispatcher -> UI thread -> Revit API -> result -> back out. Read-only.
// -----------------------------------------------------------------------------
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

            return CommandResult.Ok(new
            {
                title = doc.Title,
                path = doc.PathName,
                is_workshared = doc.IsWorkshared,
                is_family_document = doc.IsFamilyDocument,
                revit_version = app.Application.VersionNumber,
                revit_build = app.Application.VersionBuild,
                element_count = elements
            });
        }
    }
}
