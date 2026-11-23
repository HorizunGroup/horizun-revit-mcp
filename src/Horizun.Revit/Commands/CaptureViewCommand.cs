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
    public sealed partial class CaptureViewCommand : ICommand
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
            JObject req;
            try
            {
                req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson);
                var allowed = new HashSet<string>(TemporaryFields) { "view_name","view_id","pixel_size","target_document","expected_document","target_document_title" };
                foreach(var field in req.Properties()) if(!allowed.Contains(field.Name)) return CommandResult.Fail("Unknown capture argument: "+field.Name);
                viewName = req.Value<string>("view_name");
                viewId = req.Value<long?>("view_id") ?? 0;
                pixelSize = req.Value<int?>("pixel_size") ?? 1600;
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            if (pixelSize < 64 || pixelSize > 8192)
                return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, "pixel_size must be between 64 and 8192; got " + pixelSize + ".");

            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc != null ? uidoc.Document : null;
            if (doc == null) return CommandResult.Fail("No document is active.");
            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, req, Name);
            if (wrongDocument != null) return wrongDocument;

            // Resolve the view: explicit id, then name, then whatever is on screen.
            View view = null;
            string how;
            if (viewId != 0)
            {
                if (!Rid.CanRepresent(viewId)) return DocumentContext.MissingView(doc, viewId.ToString());
                Element e = doc.GetElement(Rid.ToElementId(viewId));
                view = e as View;
                if (view == null) return DocumentContext.MissingView(doc, viewId.ToString());
                how = "view_id";
            }
            else if (!string.IsNullOrEmpty(viewName))
            {
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
                {
                    if (v == null || v.IsTemplate) continue;
                    if (string.Equals(SafeName(v), viewName, StringComparison.OrdinalIgnoreCase)) { view = v; break; }
                }
                how = "view_name";
                if (view == null)
                {
                    // A sheet is usually named by its number ("E-601") or "number - name", not by its Name.
                    foreach (ViewSheet sh in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
                    {
                        if (sh == null || sh.IsTemplate) continue;
                        string number = null;
                        try { number = sh.SheetNumber; } catch { }
                        if (number == null) continue;
                        if (string.Equals(number.Trim(), viewName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(number.Trim() + " - " + SafeName(sh).Trim(), viewName.Trim(), StringComparison.OrdinalIgnoreCase))
                        { view = sh; how = "sheet_number"; break; }
                    }
                }
                if (view == null) return DocumentContext.MissingView(doc, viewName);
            }
            else
            {
                view = uidoc.ActiveView;
                if (view == null) return CommandResult.FailCoded(ErrorCodes.SinVista, "There is no active view to capture.");
                how = "active_view";
            }

            string captureProblem = CaptureFrameRules.ValidateCapture(view.IsTemplate, req["orientation"] != null,
                view is View3D threeD && !threeD.IsPerspective, pixelSize);
            if (captureProblem != null) return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, captureProblem);

            // An empty view collector proves there are no candidates; a non-empty
            // collector does not prove that those candidates will be visible in the image.
            // Do not preclude framing elements currently outside the crop.
            if (req["element_ids"] == null && !(view is ViewSheet) &&
                view.ViewType != ViewType.Schedule && view.ViewType != ViewType.ColumnSchedule &&
                view.ViewType != ViewType.PanelSchedule)
            {
                int count = -1;
                try { count = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().GetElementCount(); }
                catch { /* Some view kinds cannot be iterated: visibility remains unknown. */ }
                string emptyProblem = CaptureFrameRules.EmptyViewProblem(count >= 0, count);
                if (emptyProblem != null) return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida, emptyProblem);
            }

            // A schedule or a legend has no raster to export. Refuse before producing a
            // file that is not there and calling it a success.
            if (view.ViewType == ViewType.Schedule || view.ViewType == ViewType.ColumnSchedule ||
                view.ViewType == ViewType.PanelSchedule)
            {
                var sheets = new Dictionary<long, ViewSheet>();
                foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
                {
                    if (instance.ScheduleId != view.Id) continue;
                    var sheet = doc.GetElement(instance.OwnerViewId) as ViewSheet;
                    if (sheet != null) sheets[Rid.Value(sheet.Id)] = sheet;
                }
                var candidates = new JArray();
                foreach (var entry in sheets)
                    candidates.Add(new JObject { ["view_id"] = entry.Key, ["name"] = SafeName(entry.Value) });
                var detail = new JObject { ["sheet_candidates"] = candidates, ["write_started"] = false };
                var retry = FieldCallRules.CaptureSheetRetry(sheets.Keys);
                if (retry != null) detail["retry_with"] = retry;
                return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido,
                    "'" + SafeName(view) + "' is a " + view.ViewType + ". Revit does not raster-export schedules; " +
                    (sheets.Count == 1 ? "capture its sheet using retry_with. " : sheets.Count > 1
                        ? "choose a sheet from sheet_candidates and pass its view_id. "
                        : "it is not placed on a readable sheet; read its data with horizun_get_schedule_data. ") +
                    "Nothing was written.", extra: detail);
            }

            string dir = Path.Combine(Path.GetTempPath(), "Horizun", "captures",
                                      Guid.NewGuid().ToString("N"));
            if (HasTemporaryOptions(req)) return CaptureTemporary(app, req, view);
            string stem = Path.Combine(dir, "view");
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                return CommandResult.FailCoded(ErrorCodes.ErrorInterno, "Could not create the output folder: " + ex.Message,
                    ErrorCodes.TypeNameOf(ex));
            }

            // E6: refresh what can be refreshed before rendering. Neither call is a model
            // change: Regenerate may refuse outside a transaction and RefreshActiveView only
            // applies to the view on screen; both are best effort.
            RefreshBeforeExport(uidoc, doc, view);
            JObject scheduleRefresh = null;
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

                // A sheet's schedules are rebuilt first, in a rolled-back group (ScheduleRefresh.cs).
                if (view is ViewSheet) scheduleRefresh = ScheduleRefresh.Around(doc, new[] { view }, () => doc.ExportImage(opts));
                else doc.ExportImage(opts);
            }
            catch (Exception ex)
            {
                return CommandResult.FailCoded(ErrorCodes.ApiRevitExcepcion,
                    "Revit refused to export '" + SafeName(view) + "': " + ex.Message, ErrorCodes.TypeNameOf(ex));
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
                return CommandResult.FailCoded(ErrorCodes.CapturaSinResultado,
                    "ExportImage returned without error but produced no file in " + dir + ". Nothing was captured — " +
                    "do not treat this view as seen.");

            long bytes = 0;
            try { bytes = new FileInfo(produced).Length; } catch { }
            if (bytes == 0)
                return CommandResult.FailCoded(ErrorCodes.CapturaSinResultado,
                    "The exported file '" + produced + "' is empty. Nothing was captured.");

            int width = 0, height = 0;
            ReadPngSize(produced, out width, out height);

            // MEASURED (INFORME_P6 6.4): a fit by WIDTH sets the width to pixel_size whatever the aspect, so a tall
            // frame came back 900 x 9000 px. A portrait export taller than pixel_size is exported again fitted by
            // HEIGHT, into a fresh folder, and that file - re-read like the first - is the one reported.
            string fit = "horizontal";
            if (CaptureFrameRules.NeedsHeightFit(width, height, pixelSize))
            {
                string dir2 = Path.Combine(Path.GetTempPath(), "Horizun", "captures", Guid.NewGuid().ToString("N"));
                string refit = null; int w2 = 0, h2 = 0;
                try
                {
                    Directory.CreateDirectory(dir2);
                    var opts2 = new ImageExportOptions
                    {
                        ExportRange = ExportRange.SetOfViews,
                        FilePath = Path.Combine(dir2, "view"),
                        HLRandWFViewsFileType = ImageFileType.PNG,
                        ShadowViewsFileType = ImageFileType.PNG,
                        ZoomType = ZoomFitType.FitToPage,
                        FitDirection = FitDirectionType.Vertical,
                        ImageResolution = ImageResolution.DPI_150
                    };
                    try { opts2.PixelSize = pixelSize; } catch { }
                    opts2.SetViewsAndSheets(new List<ElementId> { view.Id });
                    if (view is ViewSheet) ScheduleRefresh.Around(doc, new[] { view }, () => doc.ExportImage(opts2));
                    else doc.ExportImage(opts2);
                    DateTime newest2 = DateTime.MinValue;
                    foreach (string f in Directory.GetFiles(dir2))
                    {
                        DateTime t = File.GetLastWriteTimeUtc(f);
                        if (t >= newest2) { newest2 = t; refit = f; }
                    }
                    if (refit != null && new FileInfo(refit).Length > 0) ReadPngSize(refit, out w2, out h2);
                }
                catch { refit = null; }
                if (refit != null && w2 > 0 && h2 > 0 && CaptureFrameRules.WithinPixelSize(w2, h2, pixelSize))
                {
                    produced = refit; width = w2; height = h2; fit = "vertical";
                    try { bytes = new FileInfo(produced).Length; } catch { }
                }
            }
            if (width > 0 && height > 0 && !CaptureFrameRules.WithinPixelSize(width, height, pixelSize))
                return CommandResult.FailCoded(ErrorCodes.CapturaSinResultado,
                    "Revit exported '" + SafeName(view) + "' at " + width + " x " + height +
                    " px, larger than pixel_size " + pixelSize + " on its long side even fitted by height. The file is " +
                    produced + "; it is not reported as a capture. Frame fewer elements or crop the view.");

            // E6: identical bytes after a model change are re-exported once; still identical
            // is reported as possibly_stale, never passed off as a fresh image.
            string shaNow = Sha256(produced);
            string freshKey = SafeKey(doc) + "|" + Rid.GetId(view.Id) + "|" + pixelSize;
            long changesNow = QueryCacheLifecycle.Cache.Epoch;
            bool hadLast = CaptureFreshness.TryGetLast(freshKey, out string lastSha, out long lastChanges);
            string freshness = CaptureFreshness.Decide(hadLast ? lastSha : null, hadLast ? (long?)lastChanges : null,
                                                       shaNow, changesNow, false);
            if (freshness == CaptureFreshness.RetryNeeded)
            {
                string again = ReExport(uidoc, doc, view, pixelSize, fit == "vertical");
                string shaAgain = again != null ? Sha256(again) : null;
                if (shaAgain != null)
                {
                    produced = again; shaNow = shaAgain;
                    try { bytes = new FileInfo(produced).Length; } catch { }
                    ReadPngSize(produced, out width, out height);
                }
                freshness = CaptureFreshness.Decide(lastSha, lastChanges, shaNow, changesNow, true);
            }
            CaptureFreshness.Remember(freshKey, shaNow, changesNow);

            return CommandResult.Ok(new
            {
                captured = true,
                freshness,
                freshness_note = freshness == CaptureFreshness.PossiblyStale
                    ? "The model changed since the last capture of this view, yet Revit rendered byte-identical images twice " +
                      "(after a regeneration). The image may show the PREVIOUS state: verify the change by reading the model, " +
                      "or make the view active and capture again."
                    : null,
                // The path Revit actually wrote, which is not the one requested: it appends
                // the view type and name to the stem.
                image_path = produced,
                requested_stem = stem + ".png",
                view = SafeName(view),
                view_id = Rid.GetId(view.Id),
                view_type = view.ViewType.ToString(),
                resolved_by = how,
                is_sheet = view is ViewSheet,
                schedule_refresh = scheduleRefresh,
                detail_level = SafeDetail(view),
                bytes,
                // Read from the PNG header: what the file IS, not what was asked for.
                pixel_width = width > 0 ? (int?)width : null,
                pixel_height = height > 0 ? (int?)height : null,
                requested_pixel_size = pixelSize,
                fit_direction = fit,
                sha256 = shaNow,
                spatial = CaptureSpatial(view, width, height),
                note = fit == "vertical"
                    ? "The view is taller than wide: the image was fitted by HEIGHT so its long side is pixel_size (" + pixelSize + ")."
                    : width > 0 && width != pixelSize
                    ? "Revit fitted the image to the view's aspect; the width it produced (" + width +
                      ") is not the pixel_size requested (" + pixelSize + ")."
                    : null
            });
        }

        private static string SafeKey(Document doc)
        {
            try { return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName; } catch { return "?"; }
        }

        private static void RefreshBeforeExport(UIDocument uidoc, Document doc, View view)
        {
            try { if (!doc.IsReadOnly) doc.Regenerate(); } catch { }
            try { if (uidoc != null && uidoc.ActiveView != null && uidoc.ActiveView.Id == view.Id) uidoc.RefreshActiveView(); } catch { }
        }

        /// <summary>One more export into a fresh folder after a refresh; the produced file or null.</summary>
        private static string ReExport(UIDocument uidoc, Document doc, View view, int pixelSize, bool vertical)
        {
            try
            {
                RefreshBeforeExport(uidoc, doc, view);
                string dir = Path.Combine(Path.GetTempPath(), "Horizun", "captures", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var opts = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(dir, "view"),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = vertical ? FitDirectionType.Vertical : FitDirectionType.Horizontal,
                    ImageResolution = ImageResolution.DPI_150
                };
                try { opts.PixelSize = pixelSize; } catch { }
                opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                if (view is ViewSheet) ScheduleRefresh.Around(doc, new[] { view }, () => doc.ExportImage(opts));
                else doc.ExportImage(opts);
                string produced = null; DateTime newest = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(dir))
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t >= newest) { newest = t; produced = f; }
                }
                return produced != null && new FileInfo(produced).Length > 0 ? produced : null;
            }
            catch { return null; }
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
