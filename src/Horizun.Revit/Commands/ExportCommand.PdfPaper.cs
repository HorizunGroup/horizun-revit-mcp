// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_export pdf_qa on paper (5.2, P1). Original Horizun code.
//
// What only the add-in knows, handed to the pure PdfQa/PdfQaPaper: which sheet each page prints and
// the boxes Revit reports for its viewports and titles (Viewport.GetBoxOutline/GetLabelOutline, in
// sheet mm), an element's box carried to the sheet through its viewport, a note's text by id, and
// where each font is installed on this machine (all users or this user only).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ExportCommand
    {
        private const double MmPerFoot = 304.8;

        private static double[] RectMm(XYZ min, XYZ max) => new[] { min.X * MmPerFoot, min.Y * MmPerFoot, max.X * MmPerFoot, max.Y * MmPerFoot };

        /// <summary>The sheet a page prints, with its outline and its viewports' boxes and titles (sheet mm).</summary>
        private static PdfSheetMap SheetMap(Document doc, ViewSheet sheet, int page)
        {
            var map = new PdfSheetMap { Page = page, SheetId = Rid.Value(sheet.Id), Sheet = sheet.SheetNumber + " - " + sheet.Name, SheetNumber = sheet.SheetNumber };
            try
            {
                BoundingBoxUV o = sheet.Outline;
                if (o != null) map.OutlineMm = new[] { o.Min.U * MmPerFoot, o.Min.V * MmPerFoot, o.Max.U * MmPerFoot, o.Max.V * MmPerFoot };
                else map.OutlineWhy = "Revit returned no outline for sheet " + sheet.SheetNumber;
            }
            catch (Exception ex) { map.OutlineWhy = "the outline of sheet " + sheet.SheetNumber + " could not be read: " + ex.Message; }
            try
            {
                Element tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
                BoundingBoxXYZ tbb = tb?.get_BoundingBox(sheet);
                if (tbb != null) map.TitleBlockMm = RectMm(tbb.Min, tbb.Max);
            }
            catch { /* no title block box: the sheet number must then be unique on the page */ }
            foreach (ElementId id in sheet.GetAllViewports())
            {
                if (!(doc.GetElement(id) is Viewport vp)) continue;
                var row = new PdfViewportBox { ViewportId = Rid.Value(vp.Id), ViewId = Rid.Value(vp.ViewId) };
                try { row.View = (doc.GetElement(vp.ViewId) as View)?.Name; } catch { }
                try { Outline b = vp.GetBoxOutline(); row.BoxMm = RectMm(b.MinimumPoint, b.MaximumPoint); } catch { }
                try { Outline l = vp.GetLabelOutline(); if (l != null && !l.IsEmpty) row.LabelMm = RectMm(l.MinimumPoint, l.MaximumPoint); } catch { }
                map.Viewports.Add(row);
            }
            return map;
        }

        /// <summary>A copy of the options with the sheets of this file's pages (page i prints pages[i]).</summary>
        private static PdfQaOptions ForPages(Document doc, PdfQaOptions o, IList<View> pages)
        {
            var c = new PdfQaOptions
            {
                ExpectedFonts = o.ExpectedFonts, FrameMarginPoints = o.FrameMarginPoints, OverlapMinPoints = o.OverlapMinPoints,
                OverlapMinFraction = o.OverlapMinFraction, MaxFindings = o.MaxFindings, Regions = o.Regions, Viewports = o.Viewports,
                CompareTo = o.CompareTo, FontInventory = o.FontInventory
            };
            for (int i = 0; i < pages.Count; i++)
                if (pages[i] is ViewSheet sheet) c.SheetMaps.Add(SheetMap(doc, sheet, i + 1));
            foreach (PdfNoteRequest note in o.Notes) c.Notes.Add(NoteOnPages(doc, note.Clone(), pages, c.SheetMaps));
            return c;
        }

        /// <summary>
        /// B3: a note named by id is searched only on the page of the sheet that shows it (the sheet itself, or the
        /// sheet holding a viewport of the note's view) and only inside its box carried to that sheet. Without sheets
        /// (format=pdf_qa with no view_ids) it stays a text search, and the reply says so (scope).
        /// </summary>
        private static PdfNoteRequest NoteOnPages(Document doc, PdfNoteRequest note, IList<View> pages, List<PdfSheetMap> maps)
        {
            if (!note.NoteId.HasValue || note.Unresolved != null || !note.OwnerViewId.HasValue || maps.Count == 0) return note;
            // Every page whose sheet shows the note (the sheet itself, or a viewport of its view: a legend can be on
            // several sheets); the page is then chosen by PdfQaPaper.NotePage, never the first one found.
            var showing = new List<int>();
            for (int i = 0; i < pages.Count; i++)
            {
                if (!(pages[i] is ViewSheet sheet)) continue;
                if (Rid.Value(sheet.Id) == note.OwnerViewId.Value || NoteViewport(doc, sheet, note.OwnerViewId.Value) != null) showing.Add(i + 1);
            }
            int? page = PdfQaPaper.NotePage(showing, note.Page, out string choice);
            if (!page.HasValue)
            {
                note.Unresolved = "note " + note.NoteId.Value + ": " + choice + "; its lines were not searched";
                note.NotInThisFile = true;
                return note;
            }
            var onSheet = (ViewSheet)pages[page.Value - 1];
            PdfSheetMap map = maps.FirstOrDefault(mp => mp.Page == page.Value);
            double[] rect = null; string why = null;
            if (Rid.Value(onSheet.Id) == note.OwnerViewId.Value)
            {
                try
                {
                    BoundingBoxXYZ bb = doc.GetElement(Rid.Make(note.NoteId.Value))?.get_BoundingBox(onSheet);
                    if (bb != null) rect = RectMm(bb.Min, bb.Max); else why = "the note has no box on sheet " + onSheet.SheetNumber;
                }
                catch (Exception ex) { why = "the note's box on sheet " + onSheet.SheetNumber + " could not be read: " + ex.Message; }
            }
            else rect = ElementOnSheet(doc, Rid.Value(NoteViewport(doc, onSheet, note.OwnerViewId.Value).Id), note.NoteId.Value, out why);
            if (rect == null || map?.OutlineMm == null)
            {
                note.Unresolved = "note " + note.NoteId.Value + " on sheet " + onSheet.SheetNumber + ": " + (why ?? map?.OutlineWhy ?? "the sheet's outline could not be read") + "; its lines were not searched";
                note.NotInThisFile = true;
                return note;
            }
            note.Page = page.Value; note.SheetRectMm = rect; note.OutlineMm = map.OutlineMm;
            return note;
        }

        private static Viewport NoteViewport(Document doc, ViewSheet sheet, long viewId)
            => sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).FirstOrDefault(v => v != null && Rid.Value(v.ViewId) == viewId);

        /// <summary>Notes by id to their text, element regions to their box on the sheet, and the font inventory. Once per call.</summary>
        private static void ResolvePaper(Document doc, PdfQaOptions o)
        {
            foreach (PdfNoteRequest note in o.Notes.Where(n => n.NoteId.HasValue))
            {
                Element e = Rid.CanRepresent(note.NoteId.Value) ? doc.GetElement(Rid.Make(note.NoteId.Value)) : null;
                if (e is TextNote tn)
                {
                    string text = null;
                    try { text = tn.Text; } catch { }
                    note.Text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                    try { note.OwnerViewId = Rid.Value(tn.OwnerViewId); } catch { }
                    if (PdfQaPaper.Normalize(note.Text).Length == 0) note.Unresolved = "text note " + note.NoteId.Value + " holds no letters or digits";
                }
                else note.Unresolved = "note_id " + note.NoteId.Value + " is not a text note of this document";
            }
            foreach (PdfRegion rg in o.Regions.Where(r => r.ElementId.HasValue))
                rg.SheetRectMm = ElementOnSheet(doc, rg.ViewportId.Value, rg.ElementId.Value, out rg.Unresolved);
            if (o.ExpectedFonts.Count > 0) o.FontInventory = FontInventory();
        }

        /// <summary>The element's box in the viewport's view, carried to the sheet (sheet mm); null with the reason.</summary>
        private static double[] ElementOnSheet(Document doc, long viewportId, long elementId, out string why)
        {
            why = null;
            try
            {
                if (!(doc.GetElement(Rid.Make(viewportId)) is Viewport vp)) { why = "viewport_id " + viewportId + " is not a viewport"; return null; }
                if (!(doc.GetElement(vp.ViewId) is View view)) { why = "the viewport's view is unreadable"; return null; }
                Element e = Rid.CanRepresent(elementId) ? doc.GetElement(Rid.Make(elementId)) : null;
                if (e == null) { why = "element_id " + elementId + " is not an element of this document"; return null; }
                BoundingBoxXYZ box = e.get_BoundingBox(view);
                if (box == null) { why = "element " + elementId + " has no box in view '" + view.Name + "' (not visible there)"; return null; }
                IList<TransformWithBoundary> tw = view.GetModelToProjectionTransforms();
                if (tw == null || tw.Count != 1) { why = "the view's crop is split into regions; the element cannot be carried to the sheet"; return null; }
                Transform m2p = tw[0].GetModelToProjectionTransform(), p2s = vp.GetProjectionToSheetTransform();
                Transform bt = box.Transform ?? Transform.Identity;
                var pts = new List<XYZ>();
                foreach (double x in new[] { box.Min.X, box.Max.X })
                    foreach (double y in new[] { box.Min.Y, box.Max.Y })
                        foreach (double z in new[] { box.Min.Z, box.Max.Z })
                            pts.Add(p2s.OfPoint(m2p.OfPoint(bt.OfPoint(new XYZ(x, y, z)))));
                return new[] { pts.Min(p => p.X) * MmPerFoot, pts.Min(p => p.Y) * MmPerFoot, pts.Max(p => p.X) * MmPerFoot, pts.Max(p => p.Y) * MmPerFoot };
            }
            catch (Exception ex) { why = "the element could not be carried to the sheet: " + ex.Message; return null; }
        }

        /// <summary>Family key -> user | machine | both, from the Windows font registry (HKLM: all users; HKCU: this user only).</summary>
        internal static Dictionary<string, string> FontInventory()
        {
            var inv = new Dictionary<string, string>(StringComparer.Ordinal);
            const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
            foreach (bool machine in new[] { true, false })
            {
                try
                {
                    using (Microsoft.Win32.RegistryKey k = (machine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser).OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (string name in k.GetValueNames())
                        {
                            string fam = PdfQaPaper.RegistryFontKey(name);
                            if (fam.Length == 0) continue;
                            string where = machine ? "machine" : "user";
                            inv[fam] = inv.TryGetValue(fam, out string had) && had != where ? "both" : where;
                        }
                    }
                }
                catch { }
            }
            return inv;
        }
    }
}
