// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_planimetry_ops, notes and leaders (4.2 front N2). Three operations through
// ModelEditRunner (rehearse, token, TransactionGroup, re-read, roll back); the
// arithmetic is Core/NoteLeaderRules.
//
//   move_note        text notes moved / flipped / widened / re-texted WHILE every leader end
//                    stays where it was. Revit drags the leader ends with the note; this puts
//                    them back and reads each one again: it must be within 0.1 mm of the
//                    original or the whole edit is rolled back. delta_mm is in paper
//                    millimetres, in the VIEW frame or the SHEET frame (a rotated viewport
//                    turns a sheet direction into the right view direction).
//   note_from_model  a text note whose text is the layer stack / the layer material / the
//                    type name of the element its leader points at, with that element's
//                    UniqueId stored on the note (extensible storage). A leader end that
//                    points at some other element is refused.
//   clone_tag        tags cloned from a model tag onto other elements: references moved with
//                    AddReferences / RemoveReferences, heads set, ONE regeneration for the
//                    whole batch; a head left at the origin, a wrong target or a wrong
//                    orientation is a failed postcondition.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class PlanimetryOpsCommand
    {
        private static Viewport ViewportOf(Document doc, View view) =>
            new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(v => v.ViewId == view.Id);

        private static double R(XYZ p, View v) => p.DotProduct(v.RightDirection);
        private static double U(XYZ p, View v) => p.DotProduct(v.UpDirection);

        private static JArray Mm3(XYZ p) => new JArray(Math.Round(p.X / Mm, 3), Math.Round(p.Y / Mm, 3), Math.Round(p.Z / Mm, 3));

        private static XYZ OnPlane(View v, XYZ p) => p - v.ViewDirection * ((p - v.Origin).DotProduct(v.ViewDirection));

        // ---- move_note ---------------------------------------------------------------------------

        private sealed class LeaderSnap { public XYZ End, Elbow, Anchor, OrigEnd; public LeaderShape Shape; }

        /// <summary>What distinguishes the operations that ride on the move_note machinery (leader ends restored, re-read).</summary>
        private sealed class NoteOps
        {
            public string Op = "move_note";
            public IList<long> Ids;
            public Func<TextNote, View, XYZ> DeltaFor;                       // arrange_column
            public Func<TextNote, View, int, XYZ, XYZ> EndFor;               // set_leader_end, reseat_leaders: the NEW end, or null to leave it
            public Dictionary<long, List<Tuple<string, string>>> Replace;    // replace_text
            public Dictionary<long, List<NoteLeaderRules.SheetRefEdit>> RefEdits;   // sync_sheet_references: positions, one pass
            public Dictionary<long, string> RefExpected;                             // sync_sheet_references: what the note must read, computed apart from the edits
            public string Elbow, Side;
            public JObject Extra;                                           // facts the rehearsal shows
        }

        private sealed class NoteJob
        {
            public long Id; public View View; public XYZ OldCoord, Delta, ExpectedCoord; public double OldWidth;
            public List<LeaderSnap> Leaders = new List<LeaderSnap>(); public string Text; public List<Tuple<string, string>> Pairs; public bool EndsChanged;
            public List<NoteLeaderRules.SheetRefEdit> Edits;   // sync_sheet_references: by position, applied last to first
            public FormattedRunEdit.Plan FormatPlan;
            public string Side;           // the side every leader leaves from after the edit: left | right | null (unchanged)
            public double MaxEndDeviationMm;
        }

        private static ArchModelEdit MoveNote(Document doc, JObject r, NoteOps ops = null)
        {
            ops = ops ?? new NoteOps();
            bool keep = r.Value<bool?>("keep_leader_ends") ?? true;
            string elbow = ops.Elbow ?? r.Value<string>("elbow") ?? "keep", side = ops.Side ?? r.Value<string>("side") ?? "keep", frame = r.Value<string>("frame") ?? "view";
            double[] dm = r["delta_mm"] is JArray da ? new[] { da[0].Value<double>(), da[1].Value<double>() } : new[] { 0.0, 0.0 };
            double? width = r["width_mm"] != null ? r.Value<double>("width_mm") * Mm : (double?)null;
            double? fit = r["fit_crop_margin_mm"] != null ? r.Value<double>("fit_crop_margin_mm") * Mm : (double?)null;
            string text = r.Value<string>("text");
            bool moves = ops.DeltaFor != null || Math.Abs(dm[0]) > 1e-9 || Math.Abs(dm[1]) > 1e-9;
            bool changes = ops.EndFor != null || ops.Replace != null || ops.RefEdits != null;
            if (!moves && !changes && elbow == "keep" && side == "keep" && width == null && text == null && fit == null)
                return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["note"] = "Nothing was asked to change (no delta_mm, elbow, side, width_mm, text or fit_crop_margin_mm); nothing was written." } };

            var jobs = new List<NoteJob>();
            foreach (long noteId in ops.Ids ?? ((JArray)r["note_ids"]).Select(x => x.Value<long>()).ToList())
            {
                TextNote n = Need<TextNote>(doc, noteId, "text note");
                if (n.Pinned) throw new ArgumentException("text note " + Rid.Value(n.Id) + " is pinned; unpin it first. Nothing was written.");
                View v = doc.GetElement(n.OwnerViewId) as View ?? throw new ArgumentException("text note " + Rid.Value(n.Id) + " has no owner view. Nothing was written.");
                if (fit != null && !v.CropBoxActive) throw new ArgumentException("fit_crop_margin_mm needs an active crop in view " + Rid.Value(v.Id) + ". Nothing was written.");
                if (width != null) CheckPaperWidth(doc, n.GetTypeId(), width.Value, v, "text note " + Rid.Value(n.Id));
                double dr = dm[0] * Mm * v.Scale, du = dm[1] * Mm * v.Scale;
                if (frame == "sheet" && moves)
                {
                    Viewport vp = ViewportOf(doc, v) ?? throw new ArgumentException("frame=sheet needs view " + Rid.Value(v.Id) + " placed on a sheet. Nothing was written.");
                    double[] sv = NoteLeaderRules.SheetToView(dr, du, NoteLeaderRules.QuarterTurns(vp.Rotation.ToString()));
                    dr = sv[0]; du = sv[1];
                }
                var job = new NoteJob { Id = Rid.Value(n.Id), View = v, OldCoord = n.Coord, OldWidth = n.Width, Delta = ops.DeltaFor != null ? ops.DeltaFor(n, v) : v.RightDirection * dr + v.UpDirection * du, Text = text };
                job.ExpectedCoord = job.OldCoord + job.Delta;
                foreach (Leader l in n.GetLeaders())
                    job.Leaders.Add(new LeaderSnap { End = l.End, OrigEnd = l.End, Elbow = l.Elbow, Anchor = l.Anchor, Shape = l.LeaderShape });
                if (ops.EndFor != null)
                    for (int li = 0; li < job.Leaders.Count; li++)
                    {
                        XYZ ne = ops.EndFor(n, v, li, job.Leaders[li].End);
                        if (ne != null && ne.DistanceTo(job.Leaders[li].End) > 1e-9) { job.Leaders[li].End = ne; job.EndsChanged = true; }
                    }
                if (ops.Replace != null && ops.Replace.TryGetValue(noteId, out List<Tuple<string, string>> pairs))
                {
                    string plain = n.GetFormattedText().GetPlainText();
                    string expected = plain;
                    foreach (var pr in pairs) expected = expected.Replace(pr.Item1, pr.Item2);
                    if (expected == plain) throw new ArgumentException("text note " + noteId + " does not contain the text to replace. Nothing was written.");
                    job.Pairs = pairs; job.Text = expected;
                    if (ops.Op == "replace_text")
                        job.FormatPlan = FormattedRunEdit.Create(plain, ReadFormattedRuns(n.GetFormattedText()), pairs[0].Item1, pairs[0].Item2);
                }
                if (ops.RefEdits != null && ops.RefEdits.TryGetValue(noteId, out List<NoteLeaderRules.SheetRefEdit> refEdits))
                {
                    // The expected text was computed from the map and the pattern, not from these edits; the edits must
                    // reproduce it before anything is written, and the re-read after the commit compares with it again.
                    string plain = n.GetFormattedText().GetPlainText();
                    string expected = ops.RefExpected[noteId];
                    if (expected == plain) throw new ArgumentException("text note " + noteId + " does not contain the text to replace. Nothing was written.");
                    if (NoteLeaderRules.ApplySheetReferenceEdits(plain, refEdits) != expected)
                        throw new ArgumentException("the replacement positions of text note " + noteId + " do not reproduce the expected text; the note changed since it was read. Nothing was written.");
                    job.Edits = refEdits; job.Text = expected;
                }
                if (side != "keep" && keep)
                {
                    if (job.Leaders.Count == 0) { }
                    else
                    {
                        if (n.HorizontalAlignment != HorizontalTextAlignment.Left)
                            throw new ArgumentException("side needs a left-aligned note; text note " + job.Id + " is " + n.HorizontalAlignment + ". Nothing was written.");
                        double w = (width ?? n.Width) * v.Scale;
                        double left = R(job.ExpectedCoord, v);
                        job.Side = side == "auto" ? NoteLeaderRules.ChooseSide(left, left + w, R(job.Leaders[0].End, v)) : side;
                    }
                }
                jobs.Add(job);
            }
            if (jobs.Select(j => j.Id).Distinct().Count() != jobs.Count) throw new ArgumentException("note_ids repeats a note. Nothing was written.");
            if (ops.EndFor != null && ops.Replace == null && !moves && !jobs.Any(j => j.EndsChanged))
                return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["note"] = "Every leader end is already where it should be; nothing was written." } };

            var edit = new ArchModelEdit();
            if (jobs.Any(j => j.FormatPlan != null)) edit.VerificationFailureCode = "verificacion_fallida";
            foreach (NoteJob j in jobs) edit.Planned.Add(ModelEditRunner.Planned(doc.GetElement(Rid.Make(j.Id)), PlannedAction.Modify, r));
            edit.Summary["operation"] = ops.Op;
            if (ops.Extra != null) edit.Summary["info"] = ops.Extra;
            edit.Summary["notes"] = new JArray(jobs.Select(j => new JObject
            {
                ["note_id"] = j.Id, ["view_id"] = Rid.Value(j.View.Id), ["from_mm"] = Mm3(j.OldCoord), ["to_mm"] = Mm3(j.ExpectedCoord),
                ["leaders"] = j.Leaders.Count, ["leader_ends_kept"] = keep && j.Leaders.Count > 0 && !j.EndsChanged, ["side_after"] = j.Side, ["text_after"] = (j.Pairs != null || j.Edits != null) ? j.Text : null,
                ["ends_mm"] = j.EndsChanged ? new JArray(j.Leaders.Select(l => Mm3(l.End))) : null
            }));
            edit.Summary["keep_leader_ends"] = keep;
            edit.Summary["frame"] = frame;
            edit.Summary["note"] = "Leader ends are read back and must be within 0.1 mm of the original; the elbow, side, width and text are read back too.";

            edit.Apply = d =>
            {
                foreach (NoteJob j in jobs)
                {
                    var n = (TextNote)d.GetElement(Rid.Make(j.Id));
                    if (j.Edits != null)
                    {
                        // By position and from the END: each edit leaves the ones before it where they were, so a number
                        // renamed to the next one's old name is never renamed twice.
                        FormattedText ft = n.GetFormattedText();
                        foreach (NoteLeaderRules.SheetRefEdit e in j.Edits.OrderByDescending(x => x.Start))
                            ft.SetPlainText(new TextRange(e.Start, e.Length), e.NewNumber);
                        n.SetFormattedText(ft);
                    }
                    else if (j.Pairs != null)
                    {
                        FormattedText ft = n.GetFormattedText();
                        if (j.FormatPlan != null)
                        {
                            string replacement = j.Pairs[0].Item2;
                            foreach (var item in j.FormatPlan.Edits)
                            {
                                ft.SetPlainText(new TextRange(item.Start, item.Length), replacement);
                                if (replacement.Length > 0) ApplyRunFormat(ft, new TextRange(item.Start, replacement.Length), item.Format);
                            }
                        }
                        else foreach (var pr in j.Pairs)
                        {
                            int from = 0, guard = 0;
                            while (guard++ < 1000)
                            {
                                TextRange found = ft.Find(pr.Item1, from, true, false);
                                if (found == null || found.Length == 0) break;
                                ft.SetPlainText(found, pr.Item2);
                                from = found.Start + pr.Item2.Length;
                            }
                        }
                        n.SetFormattedText(ft);
                    }
                    else if (j.Text != null) n.Text = j.Text;
                    if (width != null) n.Width = width.Value;
                    if (moves) n.Coord = j.OldCoord + j.Delta;
                    if (fit != null)
                    {
                        d.Regenerate();
                        double[] box = Box2D(n, j.View), crop = CropRect(j.View);
                        double[] s = NoteLeaderRules.ShiftInside(box, crop, fit.Value * j.View.Scale);
                        if (Math.Abs(s[0]) > 1e-12 || Math.Abs(s[1]) > 1e-12)
                        {
                            n.Coord = n.Coord + j.View.RightDirection * s[0] + j.View.UpDirection * s[1];
                            j.ExpectedCoord = j.ExpectedCoord + j.View.RightDirection * s[0] + j.View.UpDirection * s[1];
                        }
                    }
                }
                if (!keep) return;
                d.Regenerate();
                foreach (NoteJob j in jobs)
                {
                    if (j.Leaders.Count == 0) continue;
                    var n = (TextNote)d.GetElement(Rid.Make(j.Id));
                    if (j.Side != null)
                    {
                        bool arc = j.Leaders[0].Shape == LeaderShape.Arc;
                        n.RemoveLeaders();
                        foreach (LeaderSnap ls in j.Leaders)
                            n.AddLeader(j.Side == "left" ? (arc ? TextNoteLeaderTypes.TNLT_ARC_L : TextNoteLeaderTypes.TNLT_STRAIGHT_L)
                                                         : (arc ? TextNoteLeaderTypes.TNLT_ARC_R : TextNoteLeaderTypes.TNLT_STRAIGHT_R));
                        d.Regenerate();
                    }
                    for (int i = 0; i < j.Leaders.Count; i++)
                    {
                        Leader l = n.GetLeaders()[i];
                        l.End = j.Leaders[i].End;
                        l = n.GetLeaders()[i];
                        if (elbow == "keep")
                        {
                            if (j.Leaders[i].Shape == LeaderShape.Kinked || j.Side != null) l.Elbow = j.Leaders[i].Elbow;
                        }
                        else
                        {
                            XYZ a = l.Anchor, e = j.Leaders[i].End;
                            double[] el = NoteLeaderRules.Elbow(elbow, new[] { R(a, j.View), U(a, j.View) }, new[] { R(e, j.View), U(e, j.View) });
                            l.Elbow = a + j.View.RightDirection * (el[0] - R(a, j.View)) + j.View.UpDirection * (el[1] - U(a, j.View));
                        }
                    }
                }
            };

            edit.Verify = d =>
            {
                var names = new List<string> { "coord", "leader_count" };
                if (keep) names.Add("leader_ends");
                if (keep && elbow != "keep") names.Add("elbow");
                if (keep && side != "keep") names.Add("side");
                if (width != null) names.Add("width");
                if (jobs.Any(j => j.Text != null)) names.Add("text");
                if (fit != null) names.Add("inside_crop");
                if (jobs.Any(j => j.FormatPlan != null)) names.Add("formatting");
                var check = new PostconditionCheck(names.ToArray());
                var formatDiffs = new JArray();
                var textDiffs = new JArray();
                int formats = 0;
                int coord = 0, count = 0, ends = 0, elbows = 0, elbowsAsked = 0, sides = 0, sidesAsked = 0, widths = 0, texts = 0, inside = 0, leadersTotal = 0;
                double tol = NoteLeaderRules.LeaderEndToleranceMm * Mm;
                foreach (NoteJob j in jobs)
                {
                    var n = d.GetElement(Rid.Make(j.Id)) as TextNote;
                    if (n == null)
                    {
                        if (j.Text != null) { var diff = NoteLeaderRules.TextDifference(j.Text, null); diff["note_id"] = j.Id; textDiffs.Add(diff); }
                        continue;
                    }
                    if (n.Coord.DistanceTo(j.ExpectedCoord) <= 0.05 * Mm) coord++;
                    IList<Leader> now = n.GetLeaders();
                    if (now.Count == j.Leaders.Count) count++;
                    if (keep)
                        for (int i = 0; i < Math.Min(now.Count, j.Leaders.Count); i++)
                        {
                            leadersTotal++;
                            double dev = now[i].End.DistanceTo(j.Leaders[i].End) / Mm;
                            j.MaxEndDeviationMm = Math.Max(j.MaxEndDeviationMm, dev);
                            if (NoteLeaderRules.DecideLeaderGeometry(new[] { dev }, NoteLeaderRules.LeaderEndToleranceMm).Accept) ends++;
                            if (elbow != "keep")
                            {
                                elbowsAsked++;
                                XYZ a = now[i].Anchor, e = now[i].End, el = now[i].Elbow;
                                bool level = Math.Abs(U(el, j.View) - U(a, j.View)) <= tol;
                                bool ok = level && (elbow != "L" || Math.Abs(R(el, j.View) - R(e, j.View)) <= tol);
                                if (ok) elbows++;
                            }
                            if (side != "keep" && j.Side != null)
                            {
                                sidesAsked++;
                                double edge = R(n.Coord, j.View) + (j.Side == "right" ? n.Width * j.View.Scale : 0);
                                if (Math.Abs(R(now[i].Anchor, j.View) - edge) <= 1.0 * Mm * j.View.Scale) sides++;
                            }
                        }
                    if (width != null && Math.Abs(n.Width - width.Value) <= 0.1 * Mm) widths++;
                    if (j.Text != null)
                    {
                        string reread = n.GetFormattedText().GetPlainText();
                        if (NoteLeaderRules.TextsMatch(j.Text, reread)) texts++;
                        else { var diff = NoteLeaderRules.TextDifference(j.Text, reread); diff["note_id"] = j.Id; textDiffs.Add(diff); }
                    }
                    if (j.FormatPlan != null)
                    {
                        try
                        {
                            var differences = FormattedRunEdit.Compare(j.FormatPlan.Runs, ReadFormattedRuns(n.GetFormattedText()));
                            if (differences.Count == 0) formats++;
                            else formatDiffs.Add(new JObject { ["note_id"] = j.Id, ["differences"] = new JArray(differences) });
                        }
                        catch (Exception ex)
                        {
                            formatDiffs.Add(new JObject { ["note_id"] = j.Id, ["read_error"] = ex.Message });
                        }
                    }
                    if (fit != null)
                    {
                        double[] b = Box2D(n, j.View), c = CropRect(j.View);
                        if (b != null && b[0] >= c[0] + fit.Value * j.View.Scale - 0.5 * Mm * j.View.Scale && b[2] <= c[2] - fit.Value * j.View.Scale + 0.5 * Mm * j.View.Scale
                            && b[1] >= c[1] + fit.Value * j.View.Scale - 0.5 * Mm * j.View.Scale && b[3] <= c[3] - fit.Value * j.View.Scale + 0.5 * Mm * j.View.Scale) inside++;
                    }
                }
                check.Compare("coord", jobs.Count, coord);
                check.Compare("leader_count", jobs.Count, count);
                if (keep) check.Compare("leader_ends", leadersTotal, ends);
                if (keep && elbow != "keep") check.Compare("elbow", elbowsAsked, elbows);
                if (keep && side != "keep") check.Compare("side", sidesAsked, sides);
                if (width != null) check.Compare("width", jobs.Count, widths);
                if (jobs.Any(j => j.Text != null)) check.Compare("text", jobs.Count(j => j.Text != null), texts);
                if (fit != null) check.Compare("inside_crop", jobs.Count, inside);
                if (jobs.Any(j => j.FormatPlan != null)) check.Compare("formatting", jobs.Count(j => j.FormatPlan != null), formats);
                edit.Evidence["format_differences"] = formatDiffs;
                edit.Evidence["text_differences"] = textDiffs;
                edit.Evidence["max_leader_end_deviation_mm"] = Math.Round(jobs.Max(j => j.MaxEndDeviationMm), 4);
                edit.Evidence["leaders_checked"] = leadersTotal;
                return check;
            };
            return edit;
        }

        private static double[] CropRect(View v)
        {
            BoundingBoxXYZ crop = v.CropBox;
            double minR = double.MaxValue, minU = double.MaxValue, maxR = double.MinValue, maxU = double.MinValue;
            foreach (double x in new[] { crop.Min.X, crop.Max.X }) foreach (double y in new[] { crop.Min.Y, crop.Max.Y })
                {
                    XYZ p = crop.Transform.OfPoint(new XYZ(x, y, 0));
                    double rr = R(p, v), uu = U(p, v);
                    minR = Math.Min(minR, rr); maxR = Math.Max(maxR, rr); minU = Math.Min(minU, uu); maxU = Math.Max(maxU, uu);
                }
            return new[] { minR, minU, maxR, maxU };
        }

        // ---- note_from_model -----------------------------------------------------------------------

        private sealed class NoteItem
        {
            public Element Source; public Document SourceDoc; public RevitLinkInstance Link; public string Text, LayerNote;
            public XYZ End, At; public long CreatedId;
        }

        private static ArchModelEdit NoteFromModel(Document doc, JObject r, bool onlyMissing = false)
        {
            View view = Need<View>(doc, r.Value<long>("view_id"), "view");
            var have = onlyMissing ? NotesInView(doc, view).Select(n => NoteOrigin.Read(n)).Where(x => x != null).ToList() : new List<NoteOrigin.Record>();
            var present = new JArray(); var stale = new JArray(); var notApplicable = new JArray();
            string source = r.Value<string>("source") ?? "layers";
            if (source == "schedule" && onlyMissing)
                throw new ArgumentException("required_notes does not update schedule notes; use note_from_model. Nothing was written.");
            ViewSchedule schedule = source == "schedule" ? Need<ViewSchedule>(doc, r.Value<long>("schedule_id"), "schedule") : null;
            string scheduleText = schedule == null ? null : ScheduleSource.Read(schedule);
            string scheduleHash = schedule == null ? null : ScheduleSource.Hash(schedule, scheduleText);
            ElementId typeId = r["text_note_type_id"] != null ? Rid.Make(r.Value<long>("text_note_type_id")) : doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (!(doc.GetElement(typeId) is TextNoteType)) throw new ArgumentException("text_note_type_id is not a text note type. Nothing was written.");
            double? width = r["width_mm"] != null ? r.Value<double>("width_mm") * Mm : (double?)null;
            if (width != null) CheckPaperWidth(doc, typeId, width.Value, view, "text_note_type_id " + Rid.Value(typeId));
            View3D v3 = LeaderTargetReader.FindView3D(doc);
            var items = new List<NoteItem>();
            var rays = new JArray();
            foreach (JObject n in ((JArray)r["notes"]).OfType<JObject>())
            {
                var it = new NoteItem();
                long eid = n.Value<long>("element_id");
                if (n["link_instance_id"] != null)
                {
                    it.Link = Need<RevitLinkInstance>(doc, n.Value<long>("link_instance_id"), "link instance");
                    it.SourceDoc = it.Link.GetLinkDocument() ?? throw new ArgumentException("link instance " + Rid.Value(it.Link.Id) + " is not loaded. Nothing was written.");
                    it.Source = Rid.CanRepresent(eid) ? it.SourceDoc.GetElement(Rid.Make(eid)) : null;
                }
                else { it.SourceDoc = doc; it.Source = Rid.CanRepresent(eid) ? doc.GetElement(Rid.Make(eid)) : null; }
                if (it.Source == null) throw new ArgumentException("element " + eid + " does not exist in " + (it.Link != null ? "the link" : "this document") + ". Nothing was written.");
                it.End = OnPlane(view, MmPoint(n["end_mm"])); it.At = OnPlane(view, MmPoint(n["note_mm"]));
                LeaderHit hit = LeaderTargetReader.Cast(doc, view, v3, it.End);
                bool sameLink = it.Link == null ? hit.Source == "host" : (hit.Source == "link" && hit.LinkInstanceId == Rid.Value(it.Link.Id));
                if (hit.Cast && hit.Source != null && !(sameLink && hit.ElementDocument == it.SourceDoc && hit.ElementId == eid))
                    throw new ArgumentException("the leader end " + Mm3(it.End) + " mm points at " + hit.Category + " " + hit.ElementId + (hit.Source == "link" ? " (link " + hit.LinkInstanceId + ")" : "") +
                        ", not at element " + eid + ". Nothing was written.");
                if (hit.Cast && hit.Source == null)
                    throw new ArgumentException("the leader end " + Mm3(it.End) + " mm touches no element along the view direction (" + (hit.Uncertainty ?? "no detail") + "); a note would point at nothing. Nothing was written.");
                rays.Add(new JObject { ["element_id"] = eid, ["ray"] = hit.Cast ? (hit.Source != null ? "confirmed" : "no_hit") : hit.Coverage, ["layer_resolution"] = hit.LayerResolution });

                ElementType type = it.SourceDoc.GetElement(it.Source.GetTypeId()) as ElementType;
                string typeName = type == null ? null : type.Name;
                if (source == "schedule") it.Text = scheduleText;
                else if (source == "type_name") it.Text = typeName ?? throw new ArgumentException("element " + eid + " has no type name. Nothing was written.");
                else if (source == "layers")
                {
                    IList<NoteLeaderRules.Layer> layers = LeaderTargetReader.Layers(it.SourceDoc, type as HostObjAttributes);
                    if (layers.Count == 0) throw new ArgumentException("element " + eid + " has no compound structure to list as layers; use source=material or type_name. Nothing was written.");
                    it.Text = NoteLeaderRules.LayersText(layers, typeName);
                }
                else
                {
                    string material = hit.Cast && hit.Source != null && hit.LayerMaterial != null ? hit.LayerMaterial : null;
                    if (material == null)
                    {
                        Parameter mp = it.Source.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                        if (mp != null && mp.AsElementId() != ElementId.InvalidElementId) material = it.SourceDoc.GetElement(mp.AsElementId())?.Name;
                    }
                    if (material == null) throw new ArgumentException("no material could be read for element " + eid + " (no ray hit with a layer, no structural material). Nothing was written.");
                    it.Text = material;
                    it.LayerNote = hit.LayerResolution;
                }
                if (onlyMissing)
                {
                    string suid = null, luid = null; try { suid = it.Source.UniqueId; luid = it.Link?.UniqueId; } catch { }
                    NoteOrigin.Record found = have.FirstOrDefault(x => x.SourceUniqueId == suid && x.LinkInstanceUniqueId == luid);
                    if (found != null)
                    {
                        bool current = found.Signature == NoteLeaderRules.Signature(suid, source, it.Text);
                        if (current) present.Add(eid); else stale.Add(eid);
                        notApplicable.Add(NoteLeaderRules.ExistingNoteOutcome(eid, current));
                        continue;
                    }
                }
                items.Add(it);
            }
            if (onlyMissing && items.Count == 0)
                return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["present"] = present, ["stale_not_recreated"] = stale, ["not_applicable"] = notApplicable,
                    ["note"] = "Every required note already exists" + (stale.Count > 0 ? "; the stale ones need annotation_staleness / replace_text, they are not duplicated" : "") + "; nothing was written." } };
            var edit = new ArchModelEdit();
            for (int index = 0; index < items.Count; index++)
            {
                NoteItem i = items[index];
                edit.Planned.Add(NoteLeaderRules.PlannedCreation("text_note", index, new Dictionary<string, string> {
                    ["source_unique_id"] = i.Source.UniqueId, ["link_unique_id"] = i.Link?.UniqueId ?? "",
                    ["view_unique_id"] = view.UniqueId, ["text_type_unique_id"] = doc.GetElement(typeId).UniqueId,
                    ["schedule_unique_id"] = schedule?.UniqueId ?? "", ["source"] = source, ["text"] = i.Text,
                    ["note_mm"] = Mm3(i.At).ToString(Newtonsoft.Json.Formatting.None), ["end_mm"] = Mm3(i.End).ToString(Newtonsoft.Json.Formatting.None), ["width_mm"] = r["width_mm"]?.ToString() ?? "" }));
            }
            edit.Summary["operation"] = onlyMissing ? "required_notes" : "note_from_model";
            if (onlyMissing) { edit.Summary["present"] = present; edit.Summary["stale_not_recreated"] = stale; edit.Summary["not_applicable"] = notApplicable; }
            edit.Summary["view_id"] = Rid.Value(view.Id);
            edit.Summary["source"] = source;
            edit.Summary["rays"] = rays;
            edit.Summary["notes"] = new JArray(items.Select(i => new JObject { ["element_id"] = Rid.Value(i.Source.Id), ["text"] = i.Text, ["note_mm"] = Mm3(i.At), ["end_mm"] = Mm3(i.End) }));
            edit.Apply = d =>
            {
                var created = new List<TextNote>();
                foreach (NoteItem it in items)
                {
                    var opts = new TextNoteOptions(typeId) { HorizontalAlignment = HorizontalTextAlignment.Left };
                    TextNote tn = TextNote.Create(d, view.Id, it.At, it.Text, opts);
                    if (width != null) tn.Width = width.Value;
                    bool left = R(it.End, view) < R(it.At, view);
                    Leader l = tn.AddLeader(left ? TextNoteLeaderTypes.TNLT_STRAIGHT_L : TextNoteLeaderTypes.TNLT_STRAIGHT_R);
                    l.End = it.End;
                    it.CreatedId = Rid.Value(tn.Id);
                    string uid = null, link = null; try { uid = it.Source.UniqueId; link = it.Link?.UniqueId; } catch { }
                    NoteOrigin.Write(tn, uid, link, source, NoteLeaderRules.Signature(uid, source, it.Text), it.SourceDoc.GetElement(it.Source.GetTypeId())?.Name);
                    if (schedule != null) ScheduleAssociationStorage.Write(tn, new ScheduleAssociation.Record {
                        NoteUniqueId = tn.UniqueId, ScheduleUniqueId = schedule.UniqueId, SourceUniqueId = schedule.UniqueId,
                        SourceHash = scheduleHash, RecordedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
                    created.Add(tn);
                }
                d.Regenerate();
                foreach (NoteItem it in items)
                {
                    var tn = (TextNote)d.GetElement(Rid.Make(it.CreatedId));
                    Leader l = tn.GetLeaders()[0];
                    XYZ a = l.Anchor;
                    double[] el = NoteLeaderRules.Elbow("horizontal", new[] { R(a, view), U(a, view) }, new[] { R(it.End, view), U(it.End, view) });
                    l.Elbow = a + view.RightDirection * (el[0] - R(a, view)) + view.UpDirection * (el[1] - U(a, view));
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("notes", "text", "leader_end", "origin", "schedule_association");
                var textDiffs = new JArray();
                int exist = 0, text = 0, ends = 0, origin = 0, associations = 0;
                foreach (NoteItem it in items)
                {
                    var tn = it.CreatedId > 0 ? d.GetElement(Rid.Make(it.CreatedId)) as TextNote : null;
                    if (tn == null)
                    {
                        var diff = NoteLeaderRules.TextDifference(it.Text, null); diff["note_id"] = it.CreatedId; textDiffs.Add(diff);
                        continue;
                    }
                    exist++;
                    string reread = tn.Text;
                    if (NoteLeaderRules.TextsMatch(it.Text, reread)) text++;
                    else { var diff = NoteLeaderRules.TextDifference(it.Text, reread); diff["note_id"] = it.CreatedId; textDiffs.Add(diff); }
                    IList<Leader> ls = tn.GetLeaders();
                    if (ls.Count == 1 && ls[0].End.DistanceTo(it.End) <= NoteLeaderRules.LeaderEndToleranceMm * Mm) ends++;
                    NoteOrigin.Record rec = NoteOrigin.Read(tn);
                    string uid = null; try { uid = it.Source.UniqueId; } catch { }
                    if (rec != null && rec.SourceUniqueId == uid && rec.Source == source && rec.Signature == NoteLeaderRules.Signature(uid, source, it.Text)) origin++;
                    var association = ScheduleAssociationStorage.Read(tn);
                    if (schedule == null || (association != null && association.ScheduleUniqueId == schedule.UniqueId && association.SourceHash == scheduleHash)) associations++;
                }
                check.Compare("notes", items.Count, exist);
                check.Compare("text", items.Count, text);
                check.Compare("leader_end", items.Count, ends);
                check.Compare("origin", items.Count, origin);
                check.Compare("schedule_association", items.Count, associations);
                edit.Evidence["created_note_ids"] = new JArray(items.Select(i => i.CreatedId));
                edit.Evidence["text_differences"] = textDiffs;
                return check;
            };
            return edit;
        }

        /// <summary>
        /// width_mm is PAPER millimetres (TextElement.Width is paper feet; multiplied by the scale it gave a 1.75 m note), and
        /// a box narrower than the type allows stops the wrap: refused against GetMinimumAllowedWidth/GetMaximumAllowedWidth.
        /// </summary>
        private static void CheckPaperWidth(Document doc, ElementId typeId, double widthPaperFeet, View v, string what)
        {
            double min = TextElement.GetMinimumAllowedWidth(doc, typeId), max = TextElement.GetMaximumAllowedWidth(doc, typeId);
            int scale; try { scale = v.Scale; } catch { scale = 1; }
            string problem = TextNoteLayoutRules.WidthProblem(widthPaperFeet * 304.8, min, max, scale);
            if (problem != null) throw new ArgumentException(what + ": " + problem);
        }

        private static XYZ MmPoint(JToken t) => new XYZ(t[0].Value<double>() * Mm, t[1].Value<double>() * Mm, t[2].Value<double>() * Mm);

        // ---- clone_tag -------------------------------------------------------------------------------

        private sealed class CloneItem { public Element Target; public XYZ Head; public long NewId; public string Keynote; public bool KeynoteReadable; public string Warning; }

        private static ArchModelEdit CloneTag(Document doc, JObject r)
        {
            IndependentTag model = Need<IndependentTag>(doc, r.Value<long>("model_tag_id"), "model tag");
            View view = doc.GetElement(model.OwnerViewId) as View ?? throw new ArgumentException("the model tag has no owner view. Nothing was written.");
            bool sheetH = r.Value<bool?>("sheet_horizontal") ?? false;
            TagOrientation? orientation = null; int quarter = 0;
            if (sheetH)
            {
                Viewport vp = ViewportOf(doc, view) ?? throw new ArgumentException("sheet_horizontal needs the model tag's view placed on a sheet. Nothing was written.");
                quarter = NoteLeaderRules.QuarterTurns(vp.Rotation.ToString());
                orientation = NoteLeaderRules.TagOrientationForSheet(true, quarter) == "horizontal" ? TagOrientation.Horizontal : TagOrientation.Vertical;
            }
            TagOrientation expectedOrientation = orientation ?? model.TagOrientation;
            bool isKeynote = model.Category != null && Rid.Value(model.Category.Id) == (long)BuiltInCategory.OST_KeynoteTags;
            var items = new List<CloneItem>();
            foreach (JObject t in ((JArray)r["targets"]).OfType<JObject>())
            {
                long id = t.Value<long>("element_id");
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null || e is View) throw new ArgumentException("target " + id + " is not a model element of this document. Nothing was written.");
                XYZ head = OnPlane(view, MmPoint(t["head_mm"]));
                if (head.GetLength() < Mm)
                    throw new ArgumentException("head_mm for target " + id + " is the model origin; that is what an unplaced tag looks like, so it is refused. Nothing was written.");
                string keynote = null; bool readable = false;
                if (isKeynote)
                {
                    try
                    {
                        keynote = e.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString();
                        if (string.IsNullOrWhiteSpace(keynote)) keynote = doc.GetElement(e.GetTypeId())?.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString();
                        readable = true;
                    }
                    catch { }
                }
                items.Add(new CloneItem { Target = e, Head = head, Keynote = keynote, KeynoteReadable = readable,
                    Warning = NoteLeaderRules.KeynoteWarning(isKeynote, readable, keynote) });
            }
            var edit = new ArchModelEdit();
            for (int index = 0; index < items.Count; index++)
            {
                CloneItem i = items[index];
                var inputs = ModelEditRunner.Planned(model, PlannedAction.Read, r);
                inputs.BeforeValues["model_unique_id"] = model.UniqueId;
                inputs.BeforeValues["target_unique_id"] = i.Target.UniqueId;
                inputs.BeforeValues["head_mm"] = Mm3(i.Head).ToString(Newtonsoft.Json.Formatting.None);
                inputs.BeforeValues["model_geometry"] = inputs.GeometryFingerprint ?? "";
                inputs.BeforeValues["orientation"] = expectedOrientation.ToString();
                inputs.BeforeValues["keynote_readable"] = i.KeynoteReadable.ToString();
                inputs.BeforeValues["keynote"] = i.Keynote ?? "";
                edit.Planned.Add(NoteLeaderRules.PlannedCreation("tag", index, inputs.BeforeValues));
            }
            edit.Summary["operation"] = "clone_tag";
            edit.Summary["model_tag_id"] = Rid.Value(model.Id);
            edit.Summary["view_id"] = Rid.Value(view.Id);
            edit.Summary["tags_to_create"] = items.Count;
            edit.Summary["targets_without_keynote"] = items.Count(i => i.Warning != null && i.KeynoteReadable);
            edit.Summary["target_warnings"] = new JArray(items.Where(i => i.Warning != null).Select(i => new JObject { ["target_id"] = Rid.Value(i.Target.Id), ["warning"] = i.Warning }));
            edit.Evidence["targets_without_keynote"] = edit.Summary["targets_without_keynote"].DeepClone();
            edit.Evidence["target_warnings"] = edit.Summary["target_warnings"].DeepClone();
            edit.Summary["orientation"] = orientation?.ToString();
            int regenerationsInApply = 0;
            edit.Apply = d =>
            {
                var batch = NoteLeaderRules.RunTagBatch(items, it =>
                {
                    ICollection<ElementId> copy = ElementTransformUtils.CopyElement(d, model.Id, new XYZ(Mm, 0, 0));
                    var tag = d.GetElement(copy.First()) as IndependentTag ?? throw new InvalidOperationException("the copy of the model tag is not a tag");
                    IList<Reference> old = tag.GetTaggedReferences();
                    tag.AddReferences(new List<Reference> { new Reference(it.Target) });
                    if (old.Count > 0) tag.RemoveReferences(old);
                    tag.TagHeadPosition = it.Head;
                    if (orientation != null) tag.TagOrientation = orientation.Value;
                    it.NewId = Rid.Value(tag.Id);
                }, () => d.Regenerate());
                regenerationsInApply += batch.Regenerations;
            };
            edit.Verify = d =>
            {
                var names = new List<string> { "tags", "target", "head", "orientation" };
                var check = new PostconditionCheck(names.ToArray());
                int exist = 0, target = 0, head = 0, orient = 0, empty = 0;
                var texts = new JArray();
                foreach (CloneItem it in items)
                {
                    var tag = it.NewId > 0 ? d.GetElement(Rid.Make(it.NewId)) as IndependentTag : null;
                    if (tag == null) continue;
                    exist++;
                    ISet<ElementId> tagged = tag.GetTaggedLocalElementIds();
                    if (tagged.Count == 1 && tagged.Contains(it.Target.Id)) target++;
                    if (tag.TagHeadPosition.DistanceTo(it.Head) <= NoteLeaderRules.LeaderEndToleranceMm * Mm) head++;
                    if (tag.TagOrientation == expectedOrientation) orient++;
                    string tx = null; try { tx = tag.TagText; } catch { }
                    if (string.IsNullOrWhiteSpace(tx)) empty++;
                    string key = null;
                    try
                    {
                        key = it.Target.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString();
                        if (string.IsNullOrEmpty(key)) key = d.GetElement(it.Target.GetTypeId())?.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString();
                    }
                    catch { }
                    texts.Add(new JObject { ["tag_id"] = it.NewId, ["target_id"] = Rid.Value(it.Target.Id), ["tag_text"] = tx, ["target_keynote_value"] = key });
                }
                check.Compare("tags", items.Count, exist);
                check.Compare("target", items.Count, target);
                check.Compare("head", items.Count, head);
                check.Compare("orientation", items.Count, orient);
                edit.Evidence["regenerations_in_apply"] = regenerationsInApply;
                edit.Evidence["regeneration_scope"] = "explicit calls in clone apply only; Revit and the runner may regenerate separately";
                edit.Evidence["created_tag_ids"] = new JArray(items.Select(i => i.NewId));
                edit.Evidence["tags_with_empty_text"] = empty;
                edit.Evidence["tag_texts"] = texts;
                if (empty > 0) edit.Evidence["warning"] = "some tags read empty; target_keynote_value is reported where readable, but the cause of empty tag text was not established";
                return check;
            };
            return edit;
        }
    }
}
