using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class ListExportSettingsHandler : IRevitCommand
    {
        public string Name => "list_export_settings";

        public string Description =>
            "List saved export/print configurations in the active document: DWG export setups " +
            "(ExportDWGSettings), named print settings (PrintSetting), and view/sheet sets " +
            "(ViewSheetSet, with the number of views in each set). Read-only.";

        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            // Parameters accepted for consistency; none are required.
            try
            {
                if (!string.IsNullOrWhiteSpace(paramsJson))
                    JObject.Parse(paramsJson);
            }
            catch (JsonException)
            {
                // No parameters are used; ignore malformed JSON rather than failing.
            }

            // ----- DWG export settings -----
            var dwgExportSettings = new List<object>();
            try
            {
                var dwgSetups = new FilteredElementCollector(doc)
                    .OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>()
                    .ToList();

                foreach (var setup in dwgSetups)
                {
                    try
                    {
                        dwgExportSettings.Add(new
                        {
                            id = RevitCompat.GetId(setup.Id).ToString(),
                            name = setup.Name
                        });
                    }
                    catch
                    {
                        // Skip a setup that fails introspection.
                    }
                }
            }
            catch
            {
                // ExportDWGSettings unavailable; leave list empty.
            }

            // ----- Named print settings -----
            var printSettings = new List<object>();
            try
            {
                var prints = new FilteredElementCollector(doc)
                    .OfClass(typeof(PrintSetting))
                    .Cast<PrintSetting>()
                    .ToList();

                foreach (var print in prints)
                {
                    try
                    {
                        printSettings.Add(new
                        {
                            id = RevitCompat.GetId(print.Id).ToString(),
                            name = print.Name
                        });
                    }
                    catch
                    {
                        // Skip a print setting that fails introspection.
                    }
                }
            }
            catch
            {
                // PrintSetting unavailable; leave list empty.
            }

            // ----- View/sheet sets -----
            var viewSheetSets = new List<object>();
            try
            {
                var sets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheetSet))
                    .Cast<ViewSheetSet>()
                    .ToList();

                foreach (var set in sets)
                {
                    try
                    {
                        int viewCount = 0;
                        try
                        {
                            var views = set.Views;
                            if (views != null)
                                viewCount = views.Size;
                        }
                        catch
                        {
                            viewCount = 0;
                        }

                        viewSheetSets.Add(new
                        {
                            id = RevitCompat.GetId(set.Id).ToString(),
                            name = set.Name,
                            view_count = viewCount
                        });
                    }
                    catch
                    {
                        // Skip a view/sheet set that fails introspection.
                    }
                }
            }
            catch
            {
                // ViewSheetSet unavailable; leave list empty.
            }

            return CommandResult.Ok(new
            {
                doc_title = doc.Title,
                dwg_export_settings = dwgExportSettings.ToArray(),
                print_settings = printSettings.ToArray(),
                view_sheet_sets = viewSheetSets.ToArray()
            });
        }
    }
}
