using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class CaptureViewCommand
    {
        private static readonly double[][] AnchorFractions = {
            new[] { .18,.21 },new[] { .79,.19 },new[] { .23,.77 },
            new[] { .72,.69 },new[] { .45,.42 },new[] { .56,.81 }
        };
        private static readonly byte[][] AnchorColors = {
            new byte[] {255,0,191},new byte[] {0,255,127},new byte[] {255,127,0},
            new byte[] {63,127,255},new byte[] {191,255,0},new byte[] {191,0,255}
        };
        private void Calibrate(UIApplication app, View view, JObject clean, int pixelSize)
        {
            if (!(view is ViewPlan) && !(view is ViewSection)) throw new ArgumentException("Calibrated captures currently support plans and sections only.");
            if (!view.CropBoxActive) throw new ArgumentException("Calibration requires an active rectangular crop.");
            using (var manager = view.GetCropRegionShapeManager())
                if (manager.ShapeSet || manager.NumberOfSplitRegions > 1) throw new ArgumentException("Calibration does not support shaped or split crop regions.");
            var doc = view.Document;
            var box = view.CropBox;
            var origin = view.Origin; var right = view.RightDirection; var up = view.UpDirection; var normal = view.ViewDirection;
            var points = AnchorFractions.Select(f => box.Transform.OfPoint(new XYZ(box.Min.X + (box.Max.X - box.Min.X) * f[0], box.Min.Y + (box.Max.Y - box.Min.Y) * f[1], 0))).Select(p => p - normal * ((p - origin).DotProduct(normal))).ToList();
            double halfSize = Math.Min(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) * .007;
            if (halfSize <= doc.Application.ShortCurveTolerance) throw new ArgumentException("Crop is too small for calibration markers.");
            CommandResult marked = null;
            using (var group = new TransactionGroup(doc, "Horizun: pixel calibration"))
            {
                if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Calibration group did not start.");
                try
                {
                    using (var tx = new Transaction(doc, "Horizun: calibration anchors"))
                    {
                        tx.Start();
                        for (int i = 0; i < points.Count; i++)
                        {
                            var color = AnchorColors[i];
                            var settings = new OverrideGraphicSettings().SetProjectionLineColor(new Autodesk.Revit.DB.Color(color[0], color[1], color[2])).SetProjectionLineWeight(8);
                            foreach (var axis in new[] { right, up })
                            {
                                var line = doc.Create.NewDetailCurve(view, Line.CreateBound(points[i] - axis * halfSize, points[i] + axis * halfSize));
                                view.SetElementOverrides(line.Id, settings);
                            }
                        }
                        Guard.Commit(tx, "calibration anchors");
                    }
                    marked = Execute(app, new JObject { ["view_id"] = Rid.Value(view.Id), ["pixel_size"] = pixelSize }.ToString());
                }
                finally
                {
                    if (group.GetStatus() == TransactionStatus.Started && !Guard.RollBack(group).Confirmed)
                        throw new InvalidOperationException("Calibration markers could not be rolled back.");
                }
            }
            if (marked == null || !marked.Success) throw new InvalidOperationException(marked?.Error ?? "Calibration export failed.");
            var markedData = JObject.FromObject(marked.Data);
            var first = ReadPixels(clean.Value<string>("image_path"), out int width, out int height);
            var second = ReadPixels(markedData.Value<string>("image_path"), out int markedWidth, out int markedHeight);
            if (width != markedWidth || height != markedHeight) throw new InvalidOperationException("Calibration markers changed the export dimensions.");
            var pixels = new List<double[]>();
            var markerMask = new bool[width * height];
            foreach (var color in AnchorColors)
            {
                long sumX = 0, sumY = 0; int count = 0, minX = width, maxX = -1, minY = height, maxY = -1;
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int k = (y * width + x) * 4;
                    bool near = Math.Abs(second[k + 2] - color[0]) < 24 && Math.Abs(second[k + 1] - color[1]) < 24 && Math.Abs(second[k] - color[2]) < 24;
                    bool changed = Math.Abs(first[k] - second[k]) + Math.Abs(first[k + 1] - second[k + 1]) + Math.Abs(first[k + 2] - second[k + 2]) > 24;
                    if (!near || !changed) continue;
                    sumX += x; sumY += y; count++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                if (count < 12 || maxX - minX > width * .06 || maxY - minY > height * .06) throw new InvalidOperationException("Calibration anchor absent or ambiguous in exported PNG.");
                pixels.Add(new[] { (double)sumX / count + .5, (double)sumY / count + .5 });
                for (int y = Math.Max(0, minY - 4); y <= Math.Min(height - 1, maxY + 4); y++)
                    for (int x = Math.Max(0, minX - 4); x <= Math.Min(width - 1, maxX + 4); x++) markerMask[y * width + x] = true;
            }
            int different = 0, checkedPixels = 0;
            for (int i = 0; i < markerMask.Length; i++) if (!markerMask[i])
            {
                checkedPixels++; int k = i * 4;
                if (Math.Abs(first[k] - second[k]) + Math.Abs(first[k + 1] - second[k + 1]) + Math.Abs(first[k + 2] - second[k + 2]) > 12) different++;
            }
            // The extra export must not move/resize the scene it is meant to calibrate.
            if (different > Math.Max(4, checkedPixels * .0001)) throw new InvalidOperationException("Scene pixels outside anchors changed between clean and calibration exports.");
            var plane = points.Select(p => new[] { (p - origin).DotProduct(right), (p - origin).DotProduct(up) }).ToArray();
            double[] V(XYZ v) => new[] { v.X, v.Y, v.Z };
            var calibration = PixelCalibration.Fit(plane, pixels.ToArray(), V(origin), V(right), V(up), 1.5);
            calibration["calibration_image_path"] = markedData["image_path"];
            calibration["calibration_image_sha256"] = markedData["sha256"];
            calibration["clean_image_sha256"] = clean["sha256"];
            calibration["background_pixels_checked"] = checkedPixels; calibration["background_pixels_changed"] = different;
            calibration["model_anchors"] = new JArray(points.Select(p => new JArray(V(p))));
            calibration["pixel_anchors"] = new JArray(pixels.Select(p => new JArray(p)));
            var spatial = (JObject)clean["spatial"];
            spatial["world_to_pixel"] = calibration; spatial["world_to_pixel_verified"] = true;
            spatial["calibration_status"] = "measured_png_anchors_with_independent_holdouts";
            spatial["note"] = "Affine map for this PNG and view plane only, measured with provisional markers; independent reprojection tolerance is 1.5 pixels. Markers were rolled back; the delivered image is the clean export.";
        }
        private static byte[] ReadPixels(string path, out int width, out int height)
        {
            using (var stream = File.OpenRead(path))
            {
                var bitmap = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                width = bitmap.PixelWidth; height = bitmap.PixelHeight;
                if (width < 64 || height < 64 || width > 4096 || height > 4096) throw new InvalidOperationException("Calibration PNG dimensions must be 64..4096 pixels.");
                var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[checked(width * height * 4)]; bgra.CopyPixels(pixels, width * 4, 0); return pixels;
            }
        }
    }
}
