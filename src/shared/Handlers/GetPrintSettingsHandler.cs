using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    public class GetPrintSettingsHandler : IRevitCommand
    {
        public string Name => "get_print_settings";

        public string Description =>
            "Report the active document's PrintManager state (target printer, print-to-file flag, " +
            "print range) plus all named print settings (PrintSetting, with paper size and page " +
            "orientation) and view/sheet sets (ViewSheetSet). Read-only.";

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

            // ----- PrintManager state -----
            // PrintManager properties can throw when no printer is configured;
            // each read is guarded independently so a partial state is still reported.
            bool? printToFile = null;
            string selectedPrinter = null;
            string printRange = null;

            try
            {
                var pm = doc.PrintManager;

                try
                {
                    printToFile = pm.PrintToFile;
                }
                catch
                {
                    printToFile = null;
                }

                try
                {
                    selectedPrinter = pm.PrinterName;
                }
                catch
                {
                    selectedPrinter = null;
                }

                try
                {
                    printRange = pm.PrintRange.ToString();
                }
                catch
                {
                    printRange = null;
                }
            }
            catch
            {
                // PrintManager itself unavailable; leave all reads null.
            }

            // ----- Named print settings -----
            var namedPrintSettings = new List<object>();
            try
            {
                var settings = new FilteredElementCollector(doc)
                    .OfClass(typeof(PrintSetting))
                    .Cast<PrintSetting>()
                    .ToList();

                foreach (var setting in settings)
                {
                    try
                    {
                        string paperSize = null;
                        string orientation = null;

                        try
                        {
                            var prm = setting.PrintParameters;
                            if (prm != null)
                            {
                                try
                                {
                                    var paper = prm.PaperSize;
                                    if (paper != null)
                                        paperSize = paper.Name;
                                }
                                catch
                                {
                                    paperSize = null;
                                }

                                try
                                {
                                    orientation = prm.PageOrientation.ToString();
                                }
                                catch
                                {
                                    orientation = null;
                                }
                            }
                        }
                        catch
                        {
                            // PrintParameters unreadable for this setting.
                        }

                        namedPrintSettings.Add(new
                        {
                            id = RevitCompat.GetId(setting.Id).ToString(),
                            name = setting.Name,
                            paper_size = paperSize,
                            orientation = orientation
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
                        viewSheetSets.Add(new
                        {
                            id = RevitCompat.GetId(set.Id).ToString(),
                            name = set.Name
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
                print_to_file = printToFile,
                selected_printer = selectedPrinter,
                print_range = printRange,
                named_print_settings = namedPrintSettings.ToArray(),
                view_sheet_sets = viewSheetSets.ToArray()
            });
        }
    }
}
