// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Let the caller SEE the model.
//
// Everything else here reads parameters and counts elements. That is enough to
// check what is written down, and useless for everything that is not: whether a
// wall landed where it should, why a section looks wrong, whether a sheet reads
// properly. An automation that cannot look at the model is working blind, and on
// a hard task it will confidently build on top of something it never saw.
//
// The honesty problem this command exists to solve is specific and nasty: Revit's
// ExportImage does NOT write the file you asked for. It takes your path as a stem
// and appends the view type and view name — "plan.png" becomes
// "plan - Floor Plan - Level 1.png". A handler that reports back the path it was
// given is reporting a file that does not exist. So this one exports into a folder
// of its own, looks at what actually appeared, and reports THAT: the real path,
// the real byte count, and the real pixel dimensions read out of the PNG header
// rather than the ones that were requested.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed class CaptureViewCommand : ICommand
    {
        public string Name => "horizun_capture_view";

        public string Description =>
            "Export a view as a PNG and hand the image back, so the caller can actually look at the model. " +
            "Reports the file Revit REALLY produced: ExportImage treats the path as a stem and appends the view " +
            "type and name, so the requested path is not the resulting one. Dimensions are read from the PNG " +
            "header, not from what was asked for.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string viewName; long viewId; int pixelSize;
            try
            {
                JObject req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                viewName = req.Value<string>("view_name");
                viewId = req.Value<long?>("view_id") ?? 0;
                pixelSize = req.Value<int?>("pixel_size") ?? 1600;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (pixelSize < 64 || pixelSize > 8192)
                return CommandResult.Fail("pixel_size must be between 64 and 8192; got " + pixelSize + ".");

            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc != null ? uidoc.Document : null;
            if (doc == null) return CommandResult.Fail("No document is active.");

            // Resolve the view: explicit id, then name, then whatever is on screen.
            View view = null;
            string how;
            if (viewId != 0)
            {
                Element e = doc.GetElement(Rid.ToElementId(viewId));
                view = e as View;
                if (view == null) return CommandResult.Fail("Element " + viewId + " is not a view (or does not exist).");
                how = "view_id";
            }
            else if (!string.IsNullOrEmpty(viewName))
            {
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
                {
                    if (v == null || v.IsTemplate) continue;
                    if (string.Equals(SafeName(v), viewName, StringComparison.OrdinalIgnoreCase)) { view = v; break; }
                }
                if (view == null) return CommandResult.Fail("No view named '" + viewName + "' in this document.");
                how = "view_name";
            }
            else
            {
                view = uidoc.ActiveView;
                if (view == null) return CommandResult.Fail("There is no active view to capture.");
                how = "active_view";
            }

            // A schedule or a legend has no raster to export. Refuse before producing a
            // file that is not there and calling it a success.
            if (view.ViewType == ViewType.Schedule || view.ViewType == ViewType.ColumnSchedule ||
                view.ViewType == ViewType.PanelSchedule)
            {
                return CommandResult.Fail(
                    "'" + SafeName(view) + "' is a " + view.ViewType + ". Revit does not raster-export schedules; " +
                    "read its data with horizun_execute_python instead. Nothing was written.");
            }

            string dir = Path.Combine(Path.GetTempPath(), "Horizun", "captures",
                                      DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            string stem = Path.Combine(dir, "view");
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { return CommandResult.Fail("Could not create the output folder: " + ex.Message); }

            try
            {
                var opts = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = stem,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    ImageResolution = ImageResolution.DPI_150
                };
                try { opts.PixelSize = pixelSize; } catch { /* older API: resolution alone drives it */ }
                opts.SetViewsAndSheets(new List<ElementId> { view.Id });

                doc.ExportImage(opts);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Revit refused to export '" + SafeName(view) + "': " + ex.Message);
            }

            // What actually appeared. Revit decides the final name, not us.
            string produced = null;
            try
            {
                string[] files = Directory.GetFiles(dir);
                DateTime newest = DateTime.MinValue;
                foreach (string f in files)
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t >= newest) { newest = t; produced = f; }
                }
            }
            catch { }

            if (produced == null)
                return CommandResult.Fail(
                    "ExportImage returned without error but produced no file in " + dir + ". Nothing was captured — " +
                    "do not treat this view as seen.");

            long bytes = 0;
            try { bytes = new FileInfo(produced).Length; } catch { }
            if (bytes == 0)
                return CommandResult.Fail("The exported file '" + produced + "' is empty. Nothing was captured.");

            int width = 0, height = 0;
            ReadPngSize(produced, out width, out height);

            return CommandResult.Ok(new
            {
                captured = true,
                // The path Revit actually wrote, which is not the one requested: it appends
                // the view type and name to the stem.
                image_path = produced,
                requested_stem = stem + ".png",
                view = SafeName(view),
                view_id = Rid.GetId(view.Id),
                view_type = view.ViewType.ToString(),
                resolved_by = how,
                is_sheet = view is ViewSheet,
                detail_level = SafeDetail(view),
                bytes,
                // Read from the PNG header: what the file IS, not what was asked for.
                pixel_width = width > 0 ? (int?)width : null,
                pixel_height = height > 0 ? (int?)height : null,
                requested_pixel_size = pixelSize,
                sha256 = Sha256(produced),
                note = width > 0 && width != pixelSize
                    ? "Revit fitted the image to the view's aspect; the width it produced (" + width +
                      ") is not the pixel_size requested (" + pixelSize + ")."
                    : null
            });
        }

        private static string SafeName(View v)
        {
            try { return v.Name; } catch { return null; }
        }

        private static string SafeDetail(View v)
        {
            try { return v.DetailLevel.ToString(); } catch { return null; }
        }

        /// <summary>
        /// Width and height straight out of the PNG IHDR chunk: 8-byte signature, then a
        /// chunk header, then width and height as big-endian 32-bit values at offsets 16
        /// and 20. No imaging library, no extra dependency, and it describes the file that
        /// exists rather than the request that produced it.
        /// </summary>
        private static void ReadPngSize(string path, out int width, out int height)
        {
            width = 0; height = 0;
            try
            {
                var head = new byte[24];
                using (var fs = File.OpenRead(path))
                    if (fs.Read(head, 0, 24) < 24) return;

                if (head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47) return;  // not a PNG
                width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            }
            catch { width = 0; height = 0; }
        }

        private static string Sha256(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    var sb = new StringBuilder(64);
                    foreach (byte b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }
    }
}
