// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_verify_changes operation=snapshot / operation=compare_to - visual
// diff before/after, on top of the SAME temporary-view capture path the
// default operation=check already uses (a TransactionGroup around a 3D view,
// always rolled back). Like the check, a rollback that does not confirm - or a
// temporary view still resolvable in the document afterwards - fails the call
// with temporary_view_not_rolled_back; it is never a footnote on an "ok".
//
// snapshot: frames a camera (around element_ids with the check's own Frame() +
// orientation, or from a seed ORTHOGRAPHIC 3D view's own section/crop box;
// perspective seeds and sketched crops are refused because an isometric view
// cannot reproduce them), exports it, and records the PNG plus the camera Revit
// ACTUALLY applied (orientation, section box, crop box - read back from the
// temporary view after the commit, not the values we asked for) under
// %USERPROFILE%\.horizun\verify\baselines\<doc-key>\<name>.{png,json}. The
// doc-key is the title plus a hash of the document path, so two models with the
// same file name do not share baselines. The pair is staged under temporary
// names, re-read, then moved into place; the camera json carries the PNG's
// SHA-256, and compare_to refuses a pair whose hashes disagree.
//
// compare_to: rebuilds a temporary view from the STORED camera (not from
// whatever the live view looks like now), checks that Revit applied that camera
// (direction, crop rectangle within half a pixel, section box), exports at the
// same pixel size, and diffs the two PNGs with Core/ImageDiff. PNG decode/encode
// uses WPF imaging (PresentationCore), which the add-in references on net48,
// net8 and net10 alike - System.Drawing is NOT referenced by Horizun.Revit.csproj
// on net8/10 (UseWPF only), which is why CaptureViewCalibration reads PNGs the
// same way.
//
// Region-to-model mapping is DELIBERATELY marked approximate: a region's pixel
// rectangle is swept through the depth of the stored section box (the crop's own
// near/far range when there is none) and clipped to that box - a bound on where
// the change can be, not the changed element's box. The element attribution runs
// the other way, which is what a person needs: each element changed since the
// baseline (ChangeLedger) has its bounding box projected into pixel space, and a
// region lists the ids whose projected box it touches.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class VerifyChangesCommand
    {
        private const int MaxChangedElementsReported = 500;
        private const int MaxRegionsReported = 50;
        /// <summary>Pixels of slack when attributing an element box to a changed region (edges, antialiasing, the dilation itself).</summary>
        private const int RegionAttributionMarginPx = 6;

        private sealed class Camera
        {
            public ViewOrientation3D Orientation;   // exact camera; null = use OrientName
            public string OrientName = "isometric";
            public BoundingBoxXYZ Section, Crop;
            public bool FitCropToSection;           // the check's own crop framing (Picture())
        }

        /// <summary>What one temporary-view capture left behind: the file, the camera Revit applied, and whether the view is really gone.</summary>
        private sealed class Capture
        {
            public string Png;
            public string Rollback = "not_attempted";
            public Camera Applied = new Camera();
            public ElementId ViewId;
            public bool ViewGone = true;
            public string Error;
            public bool Clean => ViewGone && (Rollback == TransactionStatus.RolledBack.ToString() || Rollback == "not_attempted");
        }

        private static string BaselinesDir(Document doc)
        {
            string key = QualityHistory.ProjectKey(doc?.Title ?? "default");
            string path = null;
            try { path = doc?.PathName; } catch { }
            if (!string.IsNullOrEmpty(path)) key += "-" + HashText(path.ToLowerInvariant()).Substring(0, 10);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".horizun", "verify", "baselines", key);
        }

        // ---------------------------------------------------------------- snapshot

        private static CommandResult RunSnapshot(UIApplication app, Document doc, JObject request)
        {
            string rawName = (request.Value<string>("snapshot_name") ?? "").Trim();
            if (rawName.Length == 0) return CommandResult.Fail("snapshot_name is required for operation=snapshot.");
            string name = QualityHistory.ProjectKey(rawName);
            int pixel = request.Value<int?>("pixel_size") ?? 1400;
            if (pixel < 256 || pixel > 4096) return CommandResult.Fail("pixel_size must be between 256 and 4096.");

            var cam = new Camera();
            long? seedId = null;
            JArray idsJson = request["element_ids"] as JArray;
            if (idsJson != null && idsJson.Count > 0)
            {
                // Framed around elements: the same Frame() + Orient() + crop fit the check uses.
                var framed = new List<Element>();
                foreach (JToken t in idsJson)
                {
                    long id = t.Value<long>();
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (e == null) return CommandResult.Fail("element_ids: " + id + " does not resolve to an element in this document.");
                    framed.Add(e);
                }
                try { cam.Section = Frame(framed); }
                catch (Exception ex) { return CommandResult.Fail("Cannot frame element_ids: " + ex.Message); }
                cam.OrientName = request.Value<string>("orientation") ?? "isometric";
                cam.FitCropToSection = true;
            }
            else
            {
                View3D seed = ResolveSeedView3D(app, doc, request, out CommandResult viewError);
                if (viewError != null) return viewError;
                seedId = Rid.Value(seed.Id);
                // The capture view is always orthographic (CreateIsometric): a perspective crop
                // box's Min/Max mean something else there, so the baseline would not show the seed.
                if (seed.IsPerspective)
                    return CommandResult.FailWithDetail("The seed view " + seedId + " is a perspective (camera) view; snapshot reproduces " +
                        "orthographic 3D views only. Pass an orthographic 3D view, or element_ids to frame around.",
                        new JObject { ["code"] = "perspective_seed_not_supported", ["seed_view_id"] = seedId });
                bool sketched = false;
                try { sketched = seed.CropBoxActive && seed.GetCropRegionShapeManager()?.ShapeSet == true; } catch { }
                if (sketched)
                    return CommandResult.FailWithDetail("The seed view " + seedId + " has a sketched (non-rectangular) crop region; the " +
                        "snapshot would flatten it to its bounding box. Reset the crop to a rectangle, or pass element_ids.",
                        new JObject { ["code"] = "non_rectangular_crop_not_supported", ["seed_view_id"] = seedId });
                cam.Orientation = seed.GetOrientation();
                cam.Crop = seed.CropBoxActive ? seed.CropBox : null;
                cam.Section = seed.IsSectionBoxActive ? seed.GetSectionBox() : null;
                if (cam.Crop == null && cam.Section == null)
                    return CommandResult.Fail("The seed view has no active crop region and no active section box, so the same " +
                        "frame cannot be reproduced later (Revit would refit to the model's extents). Pass element_ids to frame " +
                        "around, or activate a crop/section box on the view.");
                cam.FitCropToSection = cam.Crop == null;
            }

            Capture cap = ExportCamera(doc, cam, pixel);
            CommandResult leftover = TemporaryViewFailure(cap, "snapshot");
            if (leftover != null) return leftover;
            if (cap.Error != null) return CaptureFailure("Could not capture the snapshot: " + cap.Error, cap);
            if (cap.Png == null) return CaptureFailure("ExportImage produced no file for the snapshot.", cap);
            Camera applied = cap.Applied;
            if (applied.Crop == null && applied.Section == null)
                return CommandResult.Fail("Revit applied neither a crop nor a section box to the snapshot view; it could not be reproduced.");

            DateTime capturedUtc = DateTime.UtcNow;
            JObject camera = CameraJson(applied);
            camera["seed_view_id"] = seedId;
            camera["pixel_size"] = pixel;
            camera["captured_at_utc"] = capturedUtc.ToString("o");
            camera["captured_at_ticks"] = capturedUtc.Ticks;
            camera["document_path"] = SafePath(doc);

            string dir = BaselinesDir(doc);
            string pngPath = Path.Combine(dir, name + ".png");
            string jsonPath = Path.Combine(dir, name + ".json");
            string tag = Guid.NewGuid().ToString("N");
            string tmpPng = pngPath + "." + tag + ".tmp", tmpJson = jsonPath + "." + tag + ".tmp";

            // A same-named (or same-sanitised) baseline is replaced - say so, with its age.
            bool replaced = File.Exists(pngPath) || File.Exists(jsonPath);
            string replacedCapturedAt = null;
            if (File.Exists(jsonPath)) try { replacedCapturedAt = ParseCamera(File.ReadAllText(jsonPath)).Value<string>("captured_at_utc"); } catch { }

            // FileArtifactReread: stage both files, re-read them (the PNG must decode and its
            // hash must match the camera's), and only then move them over the old pair; re-read
            // again in place. Never trust a copy that "did not throw".
            int bw = 0, bh = 0; string reread = null; bool pngMoved = false;
            try
            {
                Directory.CreateDirectory(dir);
                File.Copy(cap.Png, tmpPng, true);
                camera["png_sha256"] = HashFile(tmpPng);
                File.WriteAllText(tmpJson, camera.ToString(Newtonsoft.Json.Formatting.None));
                DecodePng(tmpPng, out _, out bw, out bh);
                if (ParseCamera(File.ReadAllText(tmpJson)).Value<string>("png_sha256") != HashFile(tmpPng))
                    throw new InvalidOperationException("the staged camera does not name the staged PNG");
                MoveIntoPlace(tmpPng, pngPath); pngMoved = true;
                MoveIntoPlace(tmpJson, jsonPath);
                DecodePng(pngPath, out _, out bw, out bh);
                if (ParseCamera(File.ReadAllText(jsonPath)).Value<string>("png_sha256") != HashFile(pngPath))
                    throw new InvalidOperationException("the baseline PNG and camera in place do not belong together");
            }
            catch (Exception ex)
            {
                reread = ex.Message;
                // A new PNG must never sit beside an old camera: drop the camera so compare_to says "no baseline".
                if (pngMoved) TryDelete(jsonPath);
            }
            finally { TryDelete(tmpPng); TryDelete(tmpJson); }

            var result = new JObject
            {
                ["status"] = reread == null ? "ok" : "failed",
                ["operation"] = "snapshot",
                ["snapshot_name"] = name,
                ["baseline_png"] = pngPath,
                ["baseline_camera"] = jsonPath,
                ["framed_from"] = seedId.HasValue ? "seed_view" : "element_ids",
                ["seed_view_id"] = seedId,
                ["pixel_size"] = pixel,
                ["width"] = bw,
                ["height"] = bh,
                ["replaced_existing"] = replaced,
                ["replaced_captured_at_utc"] = replacedCapturedAt,
                ["temporary_view_rollback"] = cap.Rollback,
                ["read_only"] = false
            };
            if (reread != null)
                return CommandResult.FailWithDetail("The snapshot did not verify after writing: " + reread,
                    new JObject { ["code"] = "snapshot_not_verified", ["result"] = result });
            result["next"] = "After modelling, call operation=compare_to with snapshot_name='" + name + "' to see what changed.";
            return CommandResult.Ok(result);
        }

        // ---------------------------------------------------------------- compare_to

        private static CommandResult RunCompareTo(UIApplication app, Document doc, JObject request)
        {
            string rawName = (request.Value<string>("snapshot_name") ?? "").Trim();
            if (rawName.Length == 0) return CommandResult.Fail("snapshot_name is required for operation=compare_to.");
            string name = QualityHistory.ProjectKey(rawName);
            string dir = BaselinesDir(doc);
            string pngPath = Path.Combine(dir, name + ".png");
            string jsonPath = Path.Combine(dir, name + ".json");
            if (!File.Exists(pngPath) || !File.Exists(jsonPath))
                return CommandResult.Fail("No baseline named '" + name + "' for this document. Call operation=snapshot first.");

            JObject camera;
            try { camera = ParseCamera(File.ReadAllText(jsonPath)); }
            catch (Exception ex) { return CommandResult.Fail("Could not read the baseline camera (" + jsonPath + "): " + ex.Message); }

            string storedHash = camera.Value<string>("png_sha256"), actualHash;
            try { actualHash = HashFile(pngPath); }
            catch (Exception ex) { return CommandResult.Fail("Could not read the baseline PNG (" + pngPath + "): " + ex.Message); }
            if (string.IsNullOrEmpty(storedHash) || !string.Equals(storedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                return CommandResult.FailWithDetail("The baseline PNG does not belong to its camera (a snapshot was interrupted or a file " +
                    "was replaced). Take the snapshot again.", new JObject { ["code"] = "baseline_unpaired", ["baseline_png"] = pngPath, ["baseline_camera"] = jsonPath });
            string storedDocPath = camera.Value<string>("document_path"), docPath = SafePath(doc);
            if (!string.IsNullOrEmpty(storedDocPath) && !string.IsNullOrEmpty(docPath) && !string.Equals(storedDocPath, docPath, StringComparison.OrdinalIgnoreCase))
                return CommandResult.FailWithDetail("The baseline was taken on another document (" + storedDocPath + ").",
                    new JObject { ["code"] = "baseline_other_document", ["baseline_document"] = storedDocPath, ["document"] = docPath });

            int pixel = camera.Value<int?>("pixel_size") ?? 1400;
            var cam = new Camera();
            try
            {
                cam.Orientation = new ViewOrientation3D(PointFromJson(camera["eye"]), PointFromJson(camera["up"]), PointFromJson(camera["forward"]));
                cam.Crop = BoxFromJson(camera["crop_box"] as JObject);
                cam.Section = BoxFromJson(camera["section_box"] as JObject);
            }
            catch (Exception ex) { return CommandResult.Fail("The baseline camera is corrupt: " + ex.Message); }
            if (cam.Crop == null && cam.Section == null)
                return CommandResult.Fail("The baseline camera has neither a crop region nor a section box; it cannot be reproduced.");

            Capture cap = ExportCamera(doc, cam, pixel);
            CommandResult leftover = TemporaryViewFailure(cap, "compare_to");
            if (leftover != null) return leftover;
            if (cap.Error != null) return CaptureFailure("Could not recapture the baseline's camera: " + cap.Error, cap);
            if (cap.Png == null) return CaptureFailure("ExportImage produced no file for the recapture.", cap);
            Camera applied = cap.Applied;
            string afterPng = cap.Png;

            int[] beforePixels, afterPixels; int bw, bh, aw, ah;
            try { DecodePng(pngPath, out beforePixels, out bw, out bh); }
            catch (Exception ex) { return CommandResult.Fail("Could not decode the baseline PNG: " + ex.Message); }
            try { DecodePng(afterPng, out afterPixels, out aw, out ah); }
            catch (Exception ex) { return CommandResult.Fail("Could not decode the recaptured PNG: " + ex.Message); }

            // The frame must be the stored one, not merely the same size: a crop Revit nudged
            // while keeping the aspect would shift every edge and read as "everything changed".
            string mismatch = CameraMismatch(cam, applied, bw, out double tolFt, out double devFt);
            if (mismatch != null || bw != aw || bh != ah)
                return CommandResult.FailWithDetail((mismatch ?? ("the recapture is " + aw + "x" + ah + " but the baseline is " + bw + "x" + bh)) +
                    ": the stored camera was not reproduced, so a pixel diff would be meaningless.",
                    new JObject
                    {
                        ["code"] = "frame_not_reproduced", ["after_path"] = afterPng, ["before_path"] = pngPath,
                        ["stored_camera"] = CameraJson(cam), ["applied_camera"] = CameraJson(applied),
                        ["tolerance_ft"] = tolFt, ["max_deviation_ft"] = devFt
                    });

            ImageDiffResult diff = ImageDiff.Compare(beforePixels, afterPixels, aw, ah);
            int[] overlaid = ImageDiff.Overlay(afterPixels, diff.Mask, aw, ah);

            string outDir = Path.Combine(Path.GetTempPath(), "Horizun", "verify", "diff", Guid.NewGuid().ToString("N"));
            string beforeOut = Path.Combine(outDir, "before.png"), afterOut = Path.Combine(outDir, "after.png"), diffOut = Path.Combine(outDir, "diff.png");
            try
            {
                Directory.CreateDirectory(outDir);
                File.Copy(pngPath, beforeOut, true);
                File.Copy(afterPng, afterOut, true);
                EncodePng(overlaid, aw, ah, diffOut);
            }
            catch (Exception ex) { return CommandResult.Fail("Could not write the diff images: " + ex.Message); }
            // FileArtifactReread: the three files handed back must be the ones we meant.
            try
            {
                DecodePng(diffOut, out _, out int dw, out int dh);
                if (dw != aw || dh != ah) throw new InvalidOperationException("diff.png reads back as " + dw + "x" + dh + ", not " + aw + "x" + ah);
                if (HashFile(beforeOut) != actualHash) throw new InvalidOperationException("before.png is not the baseline");
                if (HashFile(afterOut) != HashFile(afterPng)) throw new InvalidOperationException("after.png is not the recapture");
            }
            catch (Exception ex)
            {
                return CommandResult.FailWithDetail("The diff images did not verify after writing: " + ex.Message,
                    new JObject { ["code"] = "diff_not_verified", ["diff_path"] = diffOut });
            }

            // Changes since the baseline (ChangeLedger: Horizun writes only). Ticks, not the ISO
            // string: a round trip through a JSON date would drop the fraction of a second.
            DateTime? sinceUtc = CapturedAtUtc(camera);
            var changedIds = new List<long>();
            int deletedCount = 0; bool historyEvicted = false;
            if (sinceUtc.HasValue)
            {
                IReadOnlyList<ChangeLedger.Entry> history = ChangeLedger.HistoryFor(doc);
                historyEvicted = history.Count >= ChangeLedger.HistoryCap && history[0].AtUtc > sinceUtc.Value;
                foreach (ChangeLedger.Entry h in history.Where(h => h.AtUtc >= sinceUtc.Value))
                {
                    changedIds.AddRange((h.Added ?? new long[0]).Concat(h.Modified ?? new long[0]));
                    deletedCount += h.Deleted;
                }
            }
            changedIds = changedIds.Distinct().ToList();

            // Pixel <-> crop plane map, from the crop Revit applied to THIS recapture.
            CropPixelMap map = null; Transform cropT = null; BoundingBoxXYZ crop = applied.Crop ?? cam.Crop;
            if (crop != null)
                try { map = new CropPixelMap(crop.Min.X, crop.Max.X, crop.Max.Y, aw, ah); cropT = crop.Transform ?? Transform.Identity; } catch { map = null; }

            var elementBoxes = new Dictionary<long, int[]>();
            double depthMin = 0, depthMax = 0; double[] sectionAabb = null; string depthSource = null;
            if (map != null)
            {
                Transform toLocal = cropT.Inverse;
                foreach (long id in changedIds.Take(MaxChangedElementsReported))
                {
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    BoundingBoxXYZ b = null;
                    try { b = e?.get_BoundingBox(null); } catch { }
                    if (b == null) continue;
                    int[] box = map.PixelBox(WorldCorners(b).Select(toLocal.OfPoint).Select(p => new[] { p.X, p.Y }));
                    if (box != null) elementBoxes[id] = box;
                }
                // Depth to sweep a region through: the section box (where the geometry can be),
                // else the crop's own near/far range.
                BoundingBoxXYZ sec = applied.Section ?? cam.Section;
                if (sec != null)
                {
                    List<XYZ> local = WorldCorners(sec).Select(toLocal.OfPoint).ToList();
                    depthMin = local.Min(p => p.Z); depthMax = local.Max(p => p.Z);
                    sectionAabb = WorldAabb(sec); depthSource = "section_box";
                }
                else { depthMin = crop.Min.Z; depthMax = crop.Max.Z; depthSource = "crop_clip_range"; }
            }

            var regions = new JArray();
            foreach (ImageDiffRegion r in diff.Regions.Take(MaxRegionsReported))
            {
                var rj = new JObject { ["pixel_bbox"] = new JArray(r.MinX, r.MinY, r.MaxX, r.MaxY), ["pixel_count"] = r.RawPixelCount };
                if (map != null)
                {
                    map.ToLocal(r.MinX, r.MinY, out double lx0, out double ly1);
                    map.ToLocal(r.MaxX + 1, r.MaxY + 1, out double lx1, out double ly0);
                    var corners = new List<XYZ>();
                    foreach (double lx in new[] { lx0, lx1 }) foreach (double ly in new[] { ly0, ly1 }) foreach (double lz in new[] { depthMin, depthMax })
                        corners.Add(cropT.OfPoint(new XYZ(lx, ly, lz)));
                    double[] box = { corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z), corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z) };
                    if (sectionAabb != null)
                    {
                        double[] clipped = new double[6];
                        for (int k = 0; k < 3; k++) { clipped[k] = Math.Max(box[k], sectionAabb[k]); clipped[k + 3] = Math.Min(box[k + 3], sectionAabb[k + 3]); }
                        if (clipped[0] <= clipped[3] && clipped[1] <= clipped[4] && clipped[2] <= clipped[5]) box = clipped;
                    }
                    rj["model_bbox_approx"] = new JObject
                    {
                        ["min"] = new JArray(box[0], box[1], box[2]),
                        ["max"] = new JArray(box[3], box[4], box[5]),
                        ["units"] = "feet",
                        ["depth_from"] = depthSource,
                        ["note"] = "the region's pixel rectangle swept through that depth (clipped to the section box): a bound on where the change is, not an element box"
                    };
                    rj["element_ids"] = new JArray(elementBoxes.Where(kv => CropPixelMap.Overlaps(r, kv.Value, RegionAttributionMarginPx)).Select(kv => kv.Key));
                }
                regions.Add(rj);
            }

            var result = new JObject
            {
                ["status"] = "ok",
                ["operation"] = "compare_to",
                ["snapshot_name"] = name,
                ["before_path"] = beforeOut,
                ["after_path"] = afterOut,
                ["diff_path"] = diffOut,
                ["artifacts_verified"] = true,
                ["camera_reproduced"] = new JObject { ["tolerance_ft"] = tolFt, ["max_deviation_ft"] = devFt },
                ["changed_pixel_count"] = diff.ChangedPixelCount,
                ["changed_pixel_ratio"] = diff.ChangedPixelRatio,
                ["width"] = aw,
                ["height"] = ah,
                ["regions"] = regions,
                ["regions_truncated"] = diff.Regions.Count > MaxRegionsReported,
                ["elements_changed_since_baseline"] = new JArray(changedIds.Take(MaxChangedElementsReported)),
                ["elements_changed_since_baseline_truncated"] = changedIds.Count > MaxChangedElementsReported || historyEvicted,
                ["elements_deleted_since_baseline_count"] = deletedCount,
                ["change_history_evicted"] = historyEvicted,
                ["elements_changed_scope"] = sinceUtc.HasValue
                    ? "Horizun writes recorded since the baseline in this Revit session (manual edits are not in the ledger). Deleted " +
                      "elements are only counted: a region they caused has no element_ids." + (historyEvicted
                      ? " The ledger keeps the last " + ChangeLedger.HistoryCap + " writes and older ones since the baseline were evicted, so the list is incomplete." : "")
                    : "unknown: the baseline camera has no readable capture time",
                ["temporary_view_rollback"] = cap.Rollback,
                ["read_only"] = false
            };
            result["next"] = diff.ChangedPixelRatio > 0
                ? "changed_pixel_ratio > 0: open diff_path (changed pixels in red over the after image) and after_path."
                : "changed_pixel_ratio is 0: nothing visible changed at this camera.";
            return CommandResult.Ok(result);
        }

        // ---------------------------------------------------------------- shared: view resolution, capture, camera json

        private static View3D ResolveSeedView3D(UIApplication app, Document doc, JObject request, out CommandResult error)
        {
            error = null;
            long? viewId = request.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                View3D v = Rid.CanRepresent(viewId.Value) ? doc.GetElement(Rid.Make(viewId.Value)) as View3D : null;
                if (v == null || v.IsTemplate) { error = CommandResult.Fail("view_id does not resolve to a 3D view in this document."); return null; }
                return v;
            }
            try
            {
                if (app?.ActiveUIDocument?.Document != null && app.ActiveUIDocument.Document.Equals(doc) && app.ActiveUIDocument.ActiveView is View3D active)
                    return active;
            }
            catch { }
            error = CommandResult.Fail("operation=snapshot needs element_ids to frame around, a view_id of a 3D view, or an active 3D view.");
            return null;
        }

        /// <summary>The check path's guard: a temporary view that is not confirmed gone fails the call, write_started=true.</summary>
        private static CommandResult TemporaryViewFailure(Capture cap, string operation)
        {
            if (cap.Clean) return null;
            long? viewId = cap.ViewId == null ? (long?)null : Rid.Value(cap.ViewId);
            return CommandResult.FailWithDetail(
                "operation=" + operation + ": the temporary verification view was not rolled back (" + cap.Rollback +
                (cap.ViewGone ? "" : "; view " + viewId + " still resolves in the document") +
                "): a view may remain in the model. Inspect it and delete it with horizun_delete_verified.",
                new JObject
                {
                    ["code"] = "temporary_view_not_rolled_back", ["write_started"] = true,
                    ["temporary_view_rollback"] = cap.Rollback, ["temporary_view_id"] = viewId, ["capture_error"] = cap.Error
                });
        }

        /// <summary>A capture that failed AFTER a confirmed rollback: nothing remains in the model.</summary>
        private static CommandResult CaptureFailure(string message, Capture cap)
            => CommandResult.FailWithDetail(message, new JObject
            {
                ["code"] = "capture_failed", ["write_started"] = false, ["temporary_view_rollback"] = cap.Rollback
            });

        /// <summary>
        /// The capture path of operation=check's Picture(): a temporary View3D inside a
        /// TransactionGroup that is ALWAYS rolled back. The camera is either given exactly
        /// (compare_to, seed view) or framed like Picture() (element_ids); either way the
        /// camera Revit actually applied is read back so it can be stored and reproduced.
        /// Never throws: a failure inside the group is recorded in Error AFTER the rollback,
        /// and the view's id is looked up again once the group is gone.
        /// </summary>
        private static Capture ExportCamera(Document doc, Camera cam, int pixel)
        {
            var cap = new Capture();
            string dir;
            try { dir = Path.Combine(Path.GetTempPath(), "Horizun", "verify", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); }
            catch (Exception ex) { cap.Error = "could not create the export folder: " + ex.Message; return cap; }
            using (var group = new TransactionGroup(doc, "Horizun: verify changes (temporary view)"))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("the temporary transaction group did not start");
                    View3D view;
                    using (var tx = new Transaction(doc, "Horizun: fixed-camera verification view"))
                    {
                        tx.Start();
                        ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                            ?? throw new InvalidOperationException("the model has no 3D view type");
                        view = View3D.CreateIsometric(doc, vft.Id);
                        cap.ViewId = view.Id;
                        ShowEveryDiscipline(view);
                        try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                        try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
                        if (cam.Orientation != null) view.SetOrientation(cam.Orientation); else Orient(view, cam.OrientName, cam.Section);
                        if (cam.Section != null) { view.SetSectionBox(cam.Section); view.IsSectionBoxActive = true; }
                        doc.Regenerate();
                        if (cam.Crop != null) { view.CropBox = cam.Crop; view.CropBoxActive = true; view.CropBoxVisible = false; }
                        else if (cam.FitCropToSection && cam.Section != null)
                        {
                            // Same crop fit as Picture(): the section box corners in view
                            // coordinates, depth range included.
                            FitCropToBox(view, cam.Section);
                            view.CropBoxActive = true; view.CropBoxVisible = false;
                        }
                        foreach (Category c in doc.Settings.Categories)
                            if (c.CategoryType == CategoryType.Annotation && view.CanCategoryBeHidden(c.Id))
                                try { view.SetCategoryHidden(c.Id, true); } catch { }
                        tx.Commit();
                    }
                    // Read back what Revit applied: this, not the request, is the camera to store.
                    cap.Applied.Orientation = view.GetOrientation();
                    cap.Applied.Section = view.IsSectionBoxActive ? view.GetSectionBox() : null;
                    cap.Applied.Crop = view.CropBoxActive ? view.CropBox : null;
                    var opts = new ImageExportOptions
                    {
                        ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(dir, "verify"),
                        HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                        ZoomType = ZoomFitType.FitToPage, FitDirection = FitDirectionType.Horizontal,
                        ImageResolution = ImageResolution.DPI_150
                    };
                    try { opts.PixelSize = pixel; } catch { }
                    opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                    doc.ExportImage(opts);
                }
                catch (Exception ex) { cap.Error = ex.Message; }
                finally
                {
                    try { if (group.GetStatus() == TransactionStatus.Started) cap.Rollback = Guard.RollBack(group).StatusName; }
                    catch (Exception ex) { cap.Rollback = "failed: " + ex.Message; }
                }
            }
            // Re-read the model, not the status: the temporary view must no longer resolve.
            if (cap.ViewId != null)
                try { cap.ViewGone = doc.GetElement(cap.ViewId) == null; } catch { cap.ViewGone = false; }
            if (cap.Error == null)
                try { cap.Png = Directory.GetFiles(dir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(); }
                catch (Exception ex) { cap.Error = "could not list the exported image: " + ex.Message; }
            return cap;
        }

        /// <summary>
        /// Did Revit apply the stored camera? Judged on what decides the frame: view direction and
        /// up (within 1e-8 of the dot product, ~0.1 px across a 1400 px image), the crop rectangle
        /// projected on the stored view's right/up axes (within half a pixel - independent of the
        /// eye sliding along the view direction, which moves nothing in an orthographic image),
        /// and the section box's world extents (what is cut, within max(half a pixel, 1e-3 ft)).
        /// Null = reproduced. A LIVE measurement confirms Revit re-applies a read-back camera exactly.
        /// </summary>
        private static string CameraMismatch(Camera stored, Camera applied, int pixelWidth, out double tolFt, out double maxDevFt)
        {
            tolFt = 0; maxDevFt = 0;
            if (applied.Orientation == null) return "Revit returned no orientation for the recapture view";
            if (stored.Orientation.ForwardDirection.Normalize().DotProduct(applied.Orientation.ForwardDirection.Normalize()) < 1 - 1e-8)
                return "the view direction Revit applied differs from the stored one";
            if (stored.Orientation.UpDirection.Normalize().DotProduct(applied.Orientation.UpDirection.Normalize()) < 1 - 1e-8)
                return "the up direction Revit applied differs from the stored one";
            if (stored.Crop != null)
            {
                if (applied.Crop == null) return "the stored crop region was not applied";
                tolFt = 0.5 * (stored.Crop.Max.X - stored.Crop.Min.X) / Math.Max(1, pixelWidth);
                Transform st = stored.Crop.Transform ?? Transform.Identity;
                double[] a = FrameExtents(stored.Crop, st.BasisX, st.BasisY), b = FrameExtents(applied.Crop, st.BasisX, st.BasisY);
                for (int i = 0; i < 4; i++) maxDevFt = Math.Max(maxDevFt, Math.Abs(a[i] - b[i]));
                if (maxDevFt > tolFt) return "the crop rectangle Revit applied is " + maxDevFt.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " ft off the stored one (tolerance " + tolFt.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " ft)";
            }
            if (stored.Section != null)
            {
                if (applied.Section == null) return "the stored section box was not applied";
                double secTol = Math.Max(1e-3, tolFt), dev = 0;
                double[] a = WorldAabb(stored.Section), b = WorldAabb(applied.Section);
                for (int i = 0; i < 6; i++) dev = Math.Max(dev, Math.Abs(a[i] - b[i]));
                maxDevFt = Math.Max(maxDevFt, dev);
                if (dev > secTol) return "the section box Revit applied is " + dev.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " ft off the stored one (tolerance " + secTol.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + " ft)";
            }
            return null;
        }

        /// <summary>{minRight, maxRight, minUp, maxUp} of the crop rectangle's world corners along the given axes.</summary>
        private static double[] FrameExtents(BoundingBoxXYZ crop, XYZ right, XYZ up)
        {
            Transform t = crop.Transform ?? Transform.Identity;
            var r = new List<double>(); var u = new List<double>();
            foreach (double x in new[] { crop.Min.X, crop.Max.X })
                foreach (double y in new[] { crop.Min.Y, crop.Max.Y })
                {
                    XYZ p = t.OfPoint(new XYZ(x, y, 0));
                    r.Add(p.DotProduct(right)); u.Add(p.DotProduct(up));
                }
            return new[] { r.Min(), r.Max(), u.Min(), u.Max() };
        }

        private static IEnumerable<XYZ> WorldCorners(BoundingBoxXYZ b)
        {
            Transform t = b.Transform ?? Transform.Identity;
            for (int c = 0; c < 8; c++)
                yield return t.OfPoint(new XYZ((c & 1) == 0 ? b.Min.X : b.Max.X, (c & 2) == 0 ? b.Min.Y : b.Max.Y, (c & 4) == 0 ? b.Min.Z : b.Max.Z));
        }

        /// <summary>{minX, minY, minZ, maxX, maxY, maxZ} of a box's eight world corners.</summary>
        private static double[] WorldAabb(BoundingBoxXYZ b)
        {
            List<XYZ> w = WorldCorners(b).ToList();
            return new[] { w.Min(p => p.X), w.Min(p => p.Y), w.Min(p => p.Z), w.Max(p => p.X), w.Max(p => p.Y), w.Max(p => p.Z) };
        }

        private static JObject CameraJson(Camera c) => new JObject
        {
            ["eye"] = c.Orientation == null ? null : PointJson(c.Orientation.EyePosition),
            ["up"] = c.Orientation == null ? null : PointJson(c.Orientation.UpDirection),
            ["forward"] = c.Orientation == null ? null : PointJson(c.Orientation.ForwardDirection),
            ["section_box"] = c.Section == null ? null : BoxJson(c.Section),
            ["crop_box"] = c.Crop == null ? null : BoxJson(c.Crop)
        };

        /// <summary>Camera files are parsed WITHOUT date conversion, so strings stay exactly as written.</summary>
        private static JObject ParseCamera(string text)
            => Newtonsoft.Json.JsonConvert.DeserializeObject<JObject>(text,
                new Newtonsoft.Json.JsonSerializerSettings { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
               ?? throw new InvalidOperationException("the camera file is empty");

        private static DateTime? CapturedAtUtc(JObject camera)
        {
            long? ticks = camera.Value<long?>("captured_at_ticks");
            if (ticks.HasValue && ticks.Value > 0 && ticks.Value <= DateTime.MaxValue.Ticks) return new DateTime(ticks.Value, DateTimeKind.Utc);
            if (DateTime.TryParseExact(camera.Value<string>("captured_at_utc"), "o", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                return parsed.ToUniversalTime();
            return null;
        }

        private static string SafePath(Document doc) { try { return doc?.PathName ?? ""; } catch { return ""; } }

        private static void MoveIntoPlace(string source, string destination)
        {
            if (File.Exists(destination)) File.Replace(source, destination, null);
            else File.Move(source, destination);
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        private static string HashFile(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var s = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        private static string HashText(string text)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static JArray PointJson(XYZ p) => new JArray(p.X, p.Y, p.Z);
        private static XYZ PointFromJson(JToken t) => t is JArray a && a.Count == 3 ? new XYZ(a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>()) : throw new InvalidOperationException("expected a 3-element point array");

        private static JObject BoxJson(BoundingBoxXYZ b)
        {
            Transform t = b.Transform ?? Transform.Identity;
            return new JObject
            {
                ["min"] = PointJson(b.Min), ["max"] = PointJson(b.Max),
                ["origin"] = PointJson(t.Origin), ["basis_x"] = PointJson(t.BasisX),
                ["basis_y"] = PointJson(t.BasisY), ["basis_z"] = PointJson(t.BasisZ)
            };
        }

        private static BoundingBoxXYZ BoxFromJson(JObject j)
        {
            if (j == null) return null;
            var b = new BoundingBoxXYZ { Min = PointFromJson(j["min"]), Max = PointFromJson(j["max"]) };
            Transform t = Transform.CreateTranslation(XYZ.Zero);
            t.Origin = PointFromJson(j["origin"]);
            t.BasisX = PointFromJson(j["basis_x"]);
            t.BasisY = PointFromJson(j["basis_y"]);
            t.BasisZ = PointFromJson(j["basis_z"]);
            b.Transform = t;
            return b;
        }

        // ---------------------------------------------------------------- PNG <-> ARGB (WPF imaging; add-in side only)

        /// <summary>Bgra32 bytes read as little-endian ints are 0xAARRGGBB - exactly what Core/ImageDiff expects.</summary>
        private static void DecodePng(string path, out int[] pixels, out int width, out int height)
        {
            using (var stream = File.OpenRead(path))
            {
                BitmapFrame frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                width = frame.PixelWidth; height = frame.PixelHeight;
                var bgra = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                pixels = new int[checked(width * height)];
                bgra.CopyPixels(pixels, width * 4, 0);
            }
        }

        private static void EncodePng(int[] pixels, int width, int height, string path)
        {
            BitmapSource src = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
