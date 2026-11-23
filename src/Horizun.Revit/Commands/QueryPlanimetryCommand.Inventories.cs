// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_query_planimetry, 4.2-N4 round 2. Original Horizun code. Read-only.
//
//   mode=iso_inventory  (F72) every 3D view: section box (mm), lock, projection, style, crop, and
//                       where it sits on a sheet. The census of isometrics that were rebuilt by hand.
//   mode=rasters        (LP-P3-11) every placed raster image: the image type, linked or embedded, its
//                       path and whether the file is there, pixels, size on paper/model, the view it
//                       lives in; and the image types nothing uses.
//
// Both are their own populations, not rows of the snapshot the other modes share: they read what they
// need and say so. sheet_ids / view_ids narrow them; max_rows caps the answer and truncation is reported.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryPlanimetryCommand
    {
        private static HashSet<long> IdSet(JObject request, string field, out string error)
        {
            error = null;
            if (request[field] == null || request[field].Type == JTokenType.Null) return null;
            if (!(request[field] is JArray a) || a.Any(t => t.Type != JTokenType.Integer)) { error = field + " must be a list of ids."; return null; }
            return new HashSet<long>(a.Select(t => t.Value<long>()));
        }

        private static JToken Mm(XYZ p, double scale) => new JArray(Math.Round(p.X * scale, 1), Math.Round(p.Y * scale, 1), Math.Round(p.Z * scale, 1));

        private static CommandResult NewInventory(Document doc, JObject request, string mode, double scale, int maxRows)
        {
            HashSet<long> sheetIds = IdSet(request, "sheet_ids", out string e1), viewIds = IdSet(request, "view_ids", out string e2);
            if (e1 != null) return CommandResult.Fail(e1);
            if (e2 != null) return CommandResult.Fail(e2);
            string refused = PlanimetryInventoryArgs.Refusal(request, mode);   // review m2: nothing is accepted and ignored
            if (refused != null) return CommandResult.Fail(refused);

            var placed = new Dictionary<long, List<Viewport>>();
            if (mode == "iso_inventory" || sheetIds != null)
            {
                foreach (Viewport vp in PlanimetryReadBudget.CheckEach(new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()))
                {
                    long v = Rid.Value(vp.ViewId);
                    if (!placed.TryGetValue(v, out List<Viewport> list)) placed[v] = list = new List<Viewport>();
                    list.Add(vp);
                }
            }
            var rows = new List<JObject>();
            var notes = new JArray();
            if (mode == "iso_inventory")
            {
                foreach (View3D v in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>())
                {
                    if (!CooperativeRead.AmbientContinue()) break;
                    if (v.IsTemplate) continue;
                    long id = Rid.Value(v.Id);
                    if (viewIds != null && !viewIds.Contains(id)) continue;
                    placed.TryGetValue(id, out List<Viewport> vps);
                    if (sheetIds != null && (vps == null || !vps.Any(p => sheetIds.Contains(Rid.Value(p.SheetId))))) continue;
                    var row = new JObject { ["view_id"] = id, ["name"] = v.Name, ["perspective"] = v.IsPerspective, ["hrz"] = HrzOwnership.IsOwned(v) };
                    try { row["locked"] = v.IsLocked; } catch { row["locked"] = null; }
                    try { row["display_style"] = v.DisplayStyle.ToString(); } catch { }
                    try { row["crop_active"] = v.CropBoxActive; } catch { }
                    try { row["scale"] = v.Scale; } catch { }
                    try
                    {
                        row["section_box_active"] = v.IsSectionBoxActive;
                        BoundingBoxXYZ b = v.GetSectionBox();
                        row["section_box"] = b == null ? (JToken)JValue.CreateNull() : new JObject { ["min"] = Mm(b.Min, scale), ["max"] = Mm(b.Max, scale), ["rotated"] = !b.Transform.BasisX.IsAlmostEqualTo(XYZ.BasisX) };
                    }
                    catch (Exception ex) { row["section_box_note"] = ex.Message; }
                    row["on_sheets"] = new JArray((vps ?? new List<Viewport>()).Select(p =>
                    {
                        var s = doc.GetElement(p.SheetId) as ViewSheet;
                        var o = new JObject { ["sheet_id"] = Rid.Value(p.SheetId), ["sheet"] = s?.SheetNumber, ["viewport_id"] = Rid.Value(p.Id) };
                        try { Outline ol = p.GetBoxOutline(); o["box_mm"] = new JArray(Math.Round(ol.MinimumPoint.X * scale, 1), Math.Round(ol.MinimumPoint.Y * scale, 1), Math.Round(ol.MaximumPoint.X * scale, 1), Math.Round(ol.MaximumPoint.Y * scale, 1)); } catch { }
                        return (JToken)o;
                    }));
                    rows.Add(row);
                }
                rows = rows.OrderBy(r => r.Value<string>("name"), StringComparer.Ordinal).ThenBy(r => r.Value<long>("view_id")).ToList();
            }
            else
            {
                var used = new HashSet<long>();
                foreach (ImageInstance ii in new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).Cast<ImageInstance>())
                {
                    if (!CooperativeRead.AmbientContinue()) break;
                    ElementId tid = ii.GetTypeId();
                    used.Add(Rid.Value(tid));
                    long ownerId = Rid.Value(ii.OwnerViewId);
                    if (viewIds != null && !viewIds.Contains(ownerId)) continue;
                    var owner = doc.GetElement(ii.OwnerViewId) as View;
                    if (sheetIds != null && !(owner is ViewSheet && sheetIds.Contains(ownerId)) &&
                        !(placed.TryGetValue(ownerId, out List<Viewport> onSheet) && onSheet.Any(p => sheetIds.Contains(Rid.Value(p.SheetId))))) continue;
                    rows.Add(ImageRow(doc, ii, doc.GetElement(tid) as ImageType, owner, scale));
                }
                if (viewIds == null && sheetIds == null && CooperativeRead.Ambient.Complete)
                    foreach (ImageType t in new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>())
                    {
                        if (!CooperativeRead.AmbientContinue()) break;
                        if (!used.Contains(Rid.Value(t.Id)))
                        {
                            var u = new JObject { ["image_type_id"] = Rid.Value(t.Id), ["type"] = t.Name, ["unused"] = true };
                            ImageTypeFacts(t, u);
                            rows.Add(u);
                        }
                    }
                rows = rows.OrderBy(r => r.Value<bool?>("unused") == true ? 1 : 0).ThenBy(r => r.Value<string>("type"), StringComparer.Ordinal).ThenBy(r => r.Value<long?>("element_id") ?? 0).ToList();
                notes.Add("only raster images placed as ImageInstance and the image types loaded in the document; images inside a linked model or a DWG are not listed");
            }
            // Review m2: the same cursor the other modes use, bound to the arguments and to the result set.
            string queryHash = QueryHash(request);
            string setHash = SetHash(rows);
            int offset = 0;
            string cursor = request.Value<string>("cursor");
            if (!CooperativeRead.Ambient.Complete && !string.IsNullOrWhiteSpace(cursor))
                throw new PlanimetryReadBudget.Interrupted();
            if (!string.IsNullOrWhiteSpace(cursor) && !TryCursor(cursor, queryHash, setHash, out offset, out string cursorError))
                return CommandResult.Fail(cursorError);
            if (offset > rows.Count)
                return CommandResult.Fail("The cursor starts beyond the current result set. Re-run without cursor.");
            List<JObject> page = rows.Skip(offset).Take(maxRows).ToList();
            int nextOffset = offset + page.Count;
            string nextCursor = nextOffset < rows.Count ? MakeCursor(nextOffset, queryHash, setHash) : null;
            return CommandResult.Ok(new JObject
            {
                ["document"] = doc.Title, ["mode"] = mode, ["read_only"] = true, ["population"] = mode,
                ["units"] = new JObject { ["display"] = (request.Value<string>("units") ?? "mm").ToLowerInvariant() },
                ["matched_total"] = rows.Count, ["returned"] = page.Count, ["offset"] = offset, ["truncated"] = nextCursor != null,
                ["next_cursor"] = nextCursor == null ? (JToken)JValue.CreateNull() : nextCursor,
                ["rows"] = new JArray(page), ["notes"] = notes
            });
        }

        private static void ImageTypeFacts(ImageType t, JObject o)
        {
            try { o["source"] = t.Source.ToString(); } catch { }
            try
            {
                string path = t.Path;
                o["path"] = path;
                bool linked = o.Value<string>("source") == "Link";
                o["file_exists"] = linked && !string.IsNullOrEmpty(path) ? (JToken)File.Exists(path) : JValue.CreateNull();
            }
            catch { }
            try { o["pixels"] = new JArray(t.WidthInPixels, t.HeightInPixels); } catch { }
        }

        private static JObject ImageRow(Document doc, ImageInstance ii, ImageType type, View owner, double scale)
        {
            var o = new JObject { ["element_id"] = Rid.Value(ii.Id), ["type"] = type?.Name, ["image_type_id"] = Rid.Value(ii.GetTypeId()), ["hrz"] = HrzOwnership.IsOwned(ii) };
            if (type != null) ImageTypeFacts(type, o);
            try { o["size"] = new JArray(Math.Round(ii.Width * scale, 1), Math.Round(ii.Height * scale, 1)); } catch { }
            if (owner != null) { o["view_id"] = Rid.Value(owner.Id); o["view"] = owner.Name; o["view_type"] = owner.ViewType.ToString(); }
            return o;
        }
    }
}
