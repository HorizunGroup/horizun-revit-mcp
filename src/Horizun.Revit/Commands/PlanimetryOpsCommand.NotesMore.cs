// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_planimetry_ops, notes and leaders, round two (4.2 front N2). They all ride
// on MoveNote (leader ends restored and read back; whole edit rolled back on any
// disagreement):
//
//   arrange_column         the notes of a view stacked in one column outside the crop, on
//                          the left or right of the drawing, ordered by where their leaders
//                          end; leaders keep their ends and leave horizontally.
//   set_leader_end         one leader of one note: a new end (model point) or a shift of the end.
//   reseat_leaders         the ends of notes made by note_from_model that drifted off their
//                          element (outside its bounding box in the view) brought back to the
//                          nearest point of that box, never farther than max_shift_mm.
//   replace_text           a substring replaced in notes, formatting kept; a note without it
//                          is a refusal.
//   sync_sheet_references  "VER PLANCHA D-xxx" style references checked against the sheet
//                          numbers that exist; with `map` the ones renamed are rewritten.
//   required_notes         note_from_model that only creates the notes a view is missing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class PlanimetryOpsCommand
    {
        private static List<TextNote> NotesInView(Document doc, View v) =>
            new FilteredElementCollector(doc, v.Id).OfClass(typeof(TextNote)).Cast<TextNote>().OrderBy(n => Rid.Value(n.Id)).ToList();

        // ---- arrange_column -------------------------------------------------------------------

        private static ArchModelEdit ArrangeColumn(Document doc, JObject r)
        {
            View v = Need<View>(doc, r.Value<long>("view_id"), "view");
            if (!v.CropBoxActive) throw new ArgumentException("arrange_column needs an active crop in view " + Rid.Value(v.Id) + ": the column goes outside it. Nothing was written.");
            bool left = r.Value<string>("side") == "left";
            double gap = (r.Value<double?>("gap_mm") ?? 3) * Mm * v.Scale;
            double? widthPaper = r["width_mm"] != null ? r.Value<double>("width_mm") * Mm : (double?)null;
            List<TextNote> notes = r["note_ids"] is JArray ids ? ids.Select(t => Need<TextNote>(doc, t.Value<long>(), "text note")).ToList() : NotesInView(doc, v).Where(n => n.LeaderCount > 0).ToList();
            if (notes.Count == 0) return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["note"] = "The view has no note with a leader to arrange; nothing was written." } };
            foreach (TextNote n in notes)
                if (n.OwnerViewId != v.Id) throw new ArgumentException("text note " + Rid.Value(n.Id) + " is not in view " + Rid.Value(v.Id) + ". Nothing was written.");
            double[] crop = CropRect(v);
            // Top to bottom by where the first leader ends; notes without a leader by their own position.
            var ordered = notes.Select(n => new { N = n, U = n.LeaderCount > 0 ? U(n.GetLeaders()[0].End, v) : U(n.Coord, v) })
                .OrderByDescending(x => x.U).ThenBy(x => Rid.Value(x.N.Id)).ToList();
            var deltas = new Dictionary<long, XYZ>();
            double cursor = double.MaxValue;
            foreach (var x in ordered)
            {
                double w = (widthPaper ?? x.N.Width) * v.Scale, h = x.N.Height * v.Scale;
                double top = Math.Min(Math.Min(x.U + h / 2, crop[3]), cursor);
                double targetR = left ? crop[0] - gap - w : crop[2] + gap;
                deltas[Rid.Value(x.N.Id)] = v.RightDirection * (targetR - R(x.N.Coord, v)) + v.UpDirection * (top - U(x.N.Coord, v));
                cursor = top - h - gap;
            }
            var ops = new NoteOps
            {
                Op = "arrange_column", Ids = ordered.Select(x => Rid.Value(x.N.Id)).ToList(),
                DeltaFor = (n, view) => deltas[Rid.Value(n.Id)], Elbow = "horizontal", Side = left ? "right" : "left",
                Extra = new JObject { ["column"] = left ? "left of the drawing" : "right of the drawing", ["order"] = "top to bottom by leader end", ["height_assumption"] = "TextNote.Height at paper scale times the view scale" }
            };
            return MoveNote(doc, r, ops);
        }

        // ---- set_leader_end -------------------------------------------------------------------

        private static ArchModelEdit SetLeaderEnd(Document doc, JObject r)
        {
            long id = ((JArray)r["note_ids"])[0].Value<long>();
            int index = r.Value<int?>("leader_index") ?? 0;
            string frame = r.Value<string>("frame") ?? "view";
            double[] dm = r["delta_mm"] is JArray da ? new[] { da[0].Value<double>(), da[1].Value<double>() } : null;
            var ops = new NoteOps
            {
                Op = "set_leader_end", Ids = new List<long> { id },
                EndFor = (n, v, i, old) =>
                {
                    if (i != index)
                    {
                        if (i == 0 && index >= n.LeaderCount) throw new ArgumentException("text note " + id + " has " + n.LeaderCount + " leader(s); leader_index " + index + " does not exist. Nothing was written.");
                        return null;
                    }
                    if (dm == null) return OnPlane(v, MmPoint(r["end_mm"]));
                    double dr = dm[0] * Mm * v.Scale, du = dm[1] * Mm * v.Scale;
                    if (frame == "sheet")
                    {
                        Viewport vp = ViewportOf(doc, v) ?? throw new ArgumentException("frame=sheet needs the view placed on a sheet. Nothing was written.");
                        double[] sv = NoteLeaderRules.SheetToView(dr, du, NoteLeaderRules.QuarterTurns(vp.Rotation.ToString()));
                        dr = sv[0]; du = sv[1];
                    }
                    return old + v.RightDirection * dr + v.UpDirection * du;
                }
            };
            return MoveNote(doc, r, ops);
        }

        // ---- reseat_leaders -------------------------------------------------------------------

        private static ArchModelEdit ReseatLeaders(Document doc, JObject r)
        {
            double maxShiftPaper = r.Value<double?>("max_shift_mm") ?? 50;
            List<long> ids;
            if (r["note_ids"] is JArray a) ids = a.Select(t => t.Value<long>()).ToList();
            else ids = NotesInView(doc, Need<View>(doc, r.Value<long>("view_id"), "view")).Where(n => n.LeaderCount > 0 && NoteOrigin.Read(n) != null).Select(n => Rid.Value(n.Id)).ToList();
            if (ids.Count == 0) return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["note"] = "No note with a leader and a stored origin was found; nothing was written." } };
            var skipped = new JArray();
            var ops = new NoteOps
            {
                Op = "reseat_leaders", Ids = ids,
                Extra = new JObject { ["target"] = "nearest point of the origin element's bounding box in the view", ["skipped_without_origin"] = skipped },
                EndFor = (n, v, i, old) =>
                {
                    NoteOrigin.Record rec = NoteOrigin.Read(n);
                    if (rec == null) { if (i == 0) skipped.Add(Rid.Value(n.Id)); return null; }
                    Document sd = doc; Transform toHost = Transform.Identity;
                    if (rec.LinkInstanceUniqueId != null)
                    {
                        var inst = doc.GetElement(rec.LinkInstanceUniqueId) as RevitLinkInstance;
                        sd = inst?.GetLinkDocument() ?? throw new ArgumentException("the link of text note " + Rid.Value(n.Id) + " is not loaded. Nothing was written.");
                        toHost = inst.GetTotalTransform();
                    }
                    Element src = sd.GetElement(rec.SourceUniqueId) ?? throw new ArgumentException("the element text note " + Rid.Value(n.Id) + " was made from no longer exists; use annotation_staleness. Nothing was written.");
                    BoundingBoxXYZ b = src.get_BoundingBox(null) ?? throw new ArgumentException("the element of text note " + Rid.Value(n.Id) + " has no bounding box. Nothing was written.");
                    double minR = double.MaxValue, minU = double.MaxValue, maxR = double.MinValue, maxU = double.MinValue;
                    foreach (double x in new[] { b.Min.X, b.Max.X }) foreach (double y in new[] { b.Min.Y, b.Max.Y }) foreach (double z in new[] { b.Min.Z, b.Max.Z })
                            {
                                XYZ p = toHost.OfPoint(new XYZ(x, y, z));
                                minR = Math.Min(minR, R(p, v)); maxR = Math.Max(maxR, R(p, v)); minU = Math.Min(minU, U(p, v)); maxU = Math.Max(maxU, U(p, v));
                            }
                    double er = R(old, v), eu = U(old, v);
                    double nr = Math.Max(minR, Math.Min(maxR, er)), nu = Math.Max(minU, Math.Min(maxU, eu));
                    double shift = Math.Sqrt((nr - er) * (nr - er) + (nu - eu) * (nu - eu));
                    if (shift < 0.05 * Mm) return null;
                    if (shift > maxShiftPaper * Mm * v.Scale)
                        throw new ArgumentException("the end of text note " + Rid.Value(n.Id) + " is " + Math.Round(shift / Mm / v.Scale, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " paper mm off its element, more than max_shift_mm. Nothing was written.");
                    return old + v.RightDirection * (nr - er) + v.UpDirection * (nu - eu);
                }
            };
            return MoveNote(doc, r, ops);
        }

        private static List<FormattedRunEdit.Run> ReadFormattedRuns(FormattedText ft)
        {
            string text = ft.GetPlainText();
            var runs = new List<FormattedRunEdit.Run>();
            for (int i = 0; i < text.Length; i++)
            {
                var range = new TextRange(i, 1);
                var statuses = new[] { ft.GetBoldStatus(range), ft.GetItalicStatus(range), ft.GetUnderlineStatus(range),
                    ft.GetAllCapsStatus(range), ft.GetSuperscriptStatus(range), ft.GetSubscriptStatus(range) };
                if (statuses.Any(x => x == FormatStatus.Mixed)) throw new ArgumentException("Cannot read a uniform character format. Nothing was written.");
                string format = string.Join(",", statuses.Select(x => x == FormatStatus.All ? "1" : "0")) + "," +
                    (int)ft.GetListType(range) + "," + ft.GetIndentLevel(range) + "," + ft.GetListStartNumber(range);
                if (runs.Count > 0 && runs[runs.Count - 1].Format == format) runs[runs.Count - 1].Length++;
                else runs.Add(new FormattedRunEdit.Run(i, 1, format));
            }
            return runs;
        }

        private static void ApplyRunFormat(FormattedText ft, TextRange range, string format)
        {
            string[] f = format.Split(',');
            ft.SetBoldStatus(range, f[0] == "1"); ft.SetItalicStatus(range, f[1] == "1");
            ft.SetUnderlineStatus(range, f[2] == "1"); ft.SetAllCapsStatus(range, f[3] == "1");
            ft.SetSuperscriptStatus(range, f[4] == "1"); ft.SetSubscriptStatus(range, f[5] == "1");
            var list = (ListType)int.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture);
            int indent = int.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture);
            int start = int.Parse(f[8], System.Globalization.CultureInfo.InvariantCulture);
            if (ft.GetListType(range) != list) ft.SetListType(range, list);
            if (ft.GetIndentLevel(range) != indent) ft.SetIndentLevel(range, indent);
            if (start >= 0 && ft.GetListStartNumber(range) != start) ft.SetListStartNumber(range, start);
        }

        // ---- replace_text ---------------------------------------------------------------------

        private static ArchModelEdit ReplaceText(Document doc, JObject r)
        {
            string o = r.Value<string>("old"), nw = r.Value<string>("new");
            var map = new Dictionary<long, List<Tuple<string, string>>>();
            var ids = ((JArray)r["note_ids"]).Select(t => t.Value<long>()).ToList();
            foreach (long id in ids) map[id] = new List<Tuple<string, string>> { Tuple.Create(o, nw) };
            return MoveNote(doc, r, new NoteOps { Op = "replace_text", Ids = ids, Replace = map, Extra = new JObject { ["formatting"] = "reapplied and checked after replacement" } });
        }

        // ---- sync_sheet_references ------------------------------------------------------------

        private const string DefaultSheetReference = @"(?i)\b(?:PLANCHA|L[ÁA]MINA|HOJA|SHEET)\s+([A-Z]{1,4}[-.]?\d{1,4}(?:[.-]\d{1,3})?)";

        private static ArchModelEdit SyncSheetReferences(Document doc, JObject r)
        {
            View v = Need<View>(doc, r.Value<long>("view_id"), "view");
            Regex rx;
            try { rx = new Regex(r.Value<string>("pattern") ?? DefaultSheetReference, RegexOptions.None, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { throw new ArgumentException("pattern is not a valid regular expression: " + ex.Message + " Nothing was written."); }
            if (rx.GetGroupNumbers().Length < 2) throw new ArgumentException("pattern needs one capture group holding the sheet number. Nothing was written.");
            var sheets = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
            var map = (r["map"] as JObject)?.Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.OrdinalIgnoreCase);
            var refs = new JArray(); var edits = new Dictionary<long, List<NoteLeaderRules.SheetRefEdit>>(); var expectedText = new Dictionary<long, string>(); int unknown = 0;
            foreach (TextNote n in NotesInView(doc, v))
            {
                string plain = n.GetFormattedText().GetPlainText();
                // `renamed_to` is what the rule WILL rename and nothing else (INT-7 m-f): the rows and the edits come from the same list.
                foreach (NoteLeaderRules.SheetRefMatch found in NoteLeaderRules.ListSheetReferences(plain, rx, map))
                {
                    bool exists = sheets.Contains(found.Number);
                    if (!exists) unknown++;
                    refs.Add(new JObject { ["note_id"] = Rid.Value(n.Id), ["reference"] = found.Reference, ["sheet_number"] = found.Number, ["sheet_exists"] = exists, ["renamed_to"] = found.RenamedTo });
                }
                if (map == null) continue;
                List<NoteLeaderRules.SheetRefEdit> noteEdits = NoteLeaderRules.PlanSheetReferenceEdits(plain, rx, map);
                if (noteEdits.Count == 0) continue;
                edits[Rid.Value(n.Id)] = noteEdits;
                expectedText[Rid.Value(n.Id)] = NoteLeaderRules.ExpectedAfterSheetReferences(plain, rx, map);
            }
            if (edits.Count == 0)
                return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["references"] = refs, ["references_to_missing_sheets"] = unknown,
                    ["note"] = map == null ? "Report only: pass map {old: new} to rewrite references; nothing was written." : "No reference matched the map; nothing was written." } };
            var ops = new NoteOps { Op = "sync_sheet_references", Ids = edits.Keys.OrderBy(x => x).ToList(), RefEdits = edits, RefExpected = expectedText,
                Extra = new JObject { ["references"] = refs, ["references_to_missing_sheets"] = unknown } };
            return MoveNote(doc, r, ops);
        }
    }
}
