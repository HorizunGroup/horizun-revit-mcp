// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_planimetry_ops: four documentation fixes, through ModelEditRunner
// (rehearse, token, TransactionGroup, re-read, roll back). The arithmetic is
// Core/PlanimetryOpsRules.
//
//   datum_extents        grids and levels in each view: both ends to ViewSpecific and
//                        the 2D curve set to the ACTIVE crop along the datum's own
//                        direction plus margin_mm of paper; bubbles end0/end1/both/none.
//                        Re-read: extent type, end points (1 mm), bubbles.
//   fit_viewport         each viewport (box + title) moved by the smallest shift inside
//                        its sheet's title block less margin_mm; too large is refused
//                        with the factor. Re-read: inside, within 0.5 mm.
//   resolve_overlaps     tags and text notes of a view that overlap each other: the one
//                        with the higher id moves off by the smaller axis move plus
//                        gap_mm. Pinned ones are not moved; dimensions are not moved.
//                        Re-read: none of the pairs found still overlaps.
//   legend_from_families the source legend duplicated under `name`, its content
//                        replaced by one legend component per family type placed in
//                        the categories, each with a text label. Re-read: one component
//                        per type showing that type, one label each.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class PlanimetryOpsCommand : ICommand
    {
        public string Name => "horizun_planimetry_ops";
        public string Description => "Planimetry fixes: datum 2D extents per view, viewports into the frame, tags and texts off each other, a legend from the families in use; re-read after commit.";

        private const double Mm = 1.0 / 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: planimetry", PlanimetryOpsRules.Validate, Plan, readOperation: "__none__");

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            switch (r.Value<string>("operation").Trim().ToLowerInvariant())
            {
                case "datum_extents": return Datums(doc, r);
                case "stagger_grid_bubbles": return StaggerBubbles(doc, r);
                case "fit_viewport": return Fit(doc, r);
                case "resolve_overlaps": return Overlaps(doc, r);
                case "move_note": return MoveNote(doc, r);
                case "note_from_model": return NoteFromModel(doc, r);
                case "clone_tag": return CloneTag(doc, r);
                case "arrange_column": return ArrangeColumn(doc, r);
                case "set_leader_end": return SetLeaderEnd(doc, r);
                case "reseat_leaders": return ReseatLeaders(doc, r);
                case "required_notes": return NoteFromModel(doc, r, true);
                case "replace_text": return ReplaceText(doc, r);
                case "sync_sheet_references": return SyncSheetReferences(doc, r);
                default: return Legend(doc, r);
            }
        }

        private static T Need<T>(Document doc, long id, string what) where T : Element =>
            (Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null) as T ?? throw new ArgumentException(what + " " + id + " is not a " + typeof(T).Name + " of this document. Nothing was written.");

        // ---- datum_extents ---------------------------------------------------------------

        private sealed class DatumJob { public View View; public DatumPlane Datum; public Line Target; }

        private static ArchModelEdit Datums(Document doc, JObject r)
        {
            string bubbles = r.Value<string>("bubbles");
            double marginPaper = (r.Value<double?>("margin_mm") ?? 5) * Mm;
            var jobs = new List<DatumJob>();
            foreach (JToken t in (JArray)r["view_ids"])
            {
                View v = Need<View>(doc, t.Value<long>(), "view");
                if (!v.CropBoxActive) throw new ArgumentException("view " + Rid.Value(v.Id) + " has no active crop; the extents would follow nothing. Nothing was written.");
                BoundingBoxXYZ crop = v.CropBox;
                var corners = new List<XYZ>();
                foreach (double x in new[] { crop.Min.X, crop.Max.X }) foreach (double y in new[] { crop.Min.Y, crop.Max.Y }) foreach (double z in new[] { crop.Min.Z, crop.Max.Z }) corners.Add(crop.Transform.OfPoint(new XYZ(x, y, z)));
                IEnumerable<DatumPlane> datums = r["datum_ids"] is JArray ids
                    ? ids.Select(x => Need<DatumPlane>(doc, x.Value<long>(), "datum"))
                    : new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().OfType<DatumPlane>().Where(d => d is Grid || d is Level).ToList();
                foreach (DatumPlane d in datums)
                {
                    if (!d.CanBeVisibleInView(v)) continue;
                    Curve c = d.GetCurvesInView(DatumExtentType.ViewSpecific, v).FirstOrDefault() ?? d.GetCurvesInView(DatumExtentType.Model, v).FirstOrDefault();
                    if (!(c is Line line)) throw new ArgumentException("datum " + Rid.Value(d.Id) + " is not straight in view " + Rid.Value(v.Id) + ". Nothing was written.");
                    XYZ o = line.GetEndPoint(0), u = line.Direction;
                    double[] range = PlanimetryOpsRules.Range(corners.Select(p => (p - o).DotProduct(u)), marginPaper * v.Scale);
                    jobs.Add(new DatumJob { View = v, Datum = d, Target = Line.CreateBound(o + u * range[0], o + u * range[1]) });
                }
            }
            if (jobs.Count == 0) return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["note"] = "No grid or level is visible in these views; nothing was written." } };
            var edit = new ArchModelEdit();
            foreach (var j in jobs.Select(x => x.Datum).Distinct()) edit.Planned.Add(ModelEditRunner.Planned(j, PlannedAction.Modify, r));
            edit.Summary["operation"] = "datum_extents";
            edit.Summary["datums_in_views"] = jobs.Count;
            edit.Summary["bubbles"] = bubbles;
            Dictionary<long, double[]> labelWatch = LabelOffsets(doc, jobs.Select(j => j.View.Id));
            edit.Apply = d =>
            {
                foreach (DatumJob j in jobs)
                {
                    j.Datum.SetDatumExtentType(DatumEnds.End0, j.View, DatumExtentType.ViewSpecific);
                    j.Datum.SetDatumExtentType(DatumEnds.End1, j.View, DatumExtentType.ViewSpecific);
                    j.Datum.SetCurveInView(DatumExtentType.ViewSpecific, j.View, j.Target);
                    if (bubbles != null)
                        foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                        {
                            bool show = bubbles == "both" || (bubbles == "end0" && end == DatumEnds.End0) || (bubbles == "end1" && end == DatumEnds.End1);
                            if (!j.Datum.HasBubbleInView(end, j.View)) continue;
                            if (show) j.Datum.ShowBubbleInView(end, j.View); else j.Datum.HideBubbleInView(end, j.View);
                        }
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(bubbles == null ? new[] { "extent_type", "curve" } : new[] { "extent_type", "curve", "bubbles" });
                int type = 0, curve = 0, bub = 0, bubAsked = 0;
                foreach (DatumJob j in jobs)
                {
                    if (j.Datum.GetDatumExtentTypeInView(DatumEnds.End0, j.View) == DatumExtentType.ViewSpecific && j.Datum.GetDatumExtentTypeInView(DatumEnds.End1, j.View) == DatumExtentType.ViewSpecific) type++;
                    Curve c = j.Datum.GetCurvesInView(DatumExtentType.ViewSpecific, j.View).FirstOrDefault();
                    if (c != null && c.GetEndPoint(0).DistanceTo(j.Target.GetEndPoint(0)) <= Mm && c.GetEndPoint(1).DistanceTo(j.Target.GetEndPoint(1)) <= Mm) curve++;
                    if (bubbles != null)
                        foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                        {
                            if (!j.Datum.HasBubbleInView(end, j.View)) continue;
                            bubAsked++;
                            bool want = bubbles == "both" || (bubbles == "end0" && end == DatumEnds.End0) || (bubbles == "end1" && end == DatumEnds.End1);
                            if (j.Datum.IsBubbleVisibleInView(end, j.View) == want) bub++;
                        }
                }
                check.Compare("extent_type", jobs.Count, type);
                check.Compare("curve", jobs.Count, curve);
                if (bubbles != null) check.Compare("bubbles", bubAsked, bub);
                WarnLabelRewrites(d, labelWatch, edit.Evidence);
                return check;
            };
            return edit;
        }

        /// <summary>5.2 (P2): Viewport.LabelOffset of every viewport showing these views (sheet feet), read before a datum's 2D extent changes.</summary>
        internal static Dictionary<long, double[]> LabelOffsets(Document doc, IEnumerable<ElementId> viewIds)
        {
            var views = new HashSet<long>(viewIds.Select(Rid.Value));
            var map = new Dictionary<long, double[]>();
            foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                try
                {
                    if (!views.Contains(Rid.Value(vp.ViewId))) continue;
                    XYZ o = vp.LabelOffset;
                    map[Rid.Value(vp.Id)] = new[] { o.X, o.Y };
                }
                catch { }
            }
            return map;
        }

        /// <summary>Revit can rewrite LabelOffset when a datum's 2D extent changes: the titles moved without being asked. Reported, never hidden.</summary>
        internal static void WarnLabelRewrites(Document d, Dictionary<long, double[]> before, JObject evidence)
        {
            if (before == null || before.Count == 0) return;
            var after = new Dictionary<long, double[]>();
            foreach (long id in before.Keys)
                try { if (d.GetElement(Rid.Make(id)) is Viewport vp) { XYZ o = vp.LabelOffset; after[id] = new[] { o.X, o.Y }; } } catch { }
            JArray changed = SheetPaperRules.LabelOffsetChanges(before, after);
            evidence["label_offsets_watched"] = before.Count;
            if (changed.Count == 0) return;
            evidence["label_offset_rewritten"] = changed;
            evidence["label_offset_warning"] = SheetPaperRules.LabelRewrittenWarning;
        }

        // ---- fit_viewport --------------------------------------------------------------------

        private static double[] Outline(Viewport vp)
        {
            Outline o = vp.GetBoxOutline();
            double[] b = { o.MinimumPoint.X, o.MinimumPoint.Y, o.MaximumPoint.X, o.MaximumPoint.Y };
            try
            {
                Outline l = vp.GetLabelOutline();
                if (l != null && !l.IsEmpty) b = new[] { Math.Min(b[0], l.MinimumPoint.X), Math.Min(b[1], l.MinimumPoint.Y), Math.Max(b[2], l.MaximumPoint.X), Math.Max(b[3], l.MaximumPoint.Y) };
            }
            catch { }
            return b;
        }

        private static ArchModelEdit Fit(Document doc, JObject r)
        {
            double margin = (r.Value<double?>("margin_mm") ?? 10) * Mm;
            var moves = new List<Tuple<Viewport, XYZ, double[]>>();
            int inside = 0;
            foreach (JToken t in (JArray)r["viewport_ids"])
            {
                Viewport vp = Need<Viewport>(doc, t.Value<long>(), "viewport");
                var sheet = (ViewSheet)doc.GetElement(vp.SheetId);
                Element tb = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstOrDefault()
                    ?? throw new ArgumentException("sheet " + sheet.SheetNumber + " has no title block to fit into. Nothing was written.");
                BoundingBoxXYZ bb = tb.get_BoundingBox(sheet);
                double[] usable = { bb.Min.X + margin, bb.Min.Y + margin, bb.Max.X - margin, bb.Max.Y - margin };
                double[] box = Outline(vp);
                double[] shift = PlanimetryOpsRules.FitShift(box, usable);
                if (shift == null)
                    throw new ArgumentException("viewport " + Rid.Value(vp.Id) + " is larger than the usable area of sheet " + sheet.SheetNumber + ": it needs a scale factor of " +
                        Math.Round(PlanimetryOpsRules.FitFactor(box, usable), 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + " (or a tighter crop). Nothing was written.");
                if (Math.Abs(shift[0]) < 1e-9 && Math.Abs(shift[1]) < 1e-9) { inside++; continue; }
                moves.Add(Tuple.Create(vp, new XYZ(shift[0], shift[1], 0), usable));
            }
            if (moves.Count == 0) return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["already_inside"] = inside, ["note"] = "Every viewport is already inside; nothing was written." } };
            var edit = new ArchModelEdit();
            foreach (var m in moves) edit.Planned.Add(ModelEditRunner.Planned(m.Item1, PlannedAction.Modify, r));
            edit.Summary["operation"] = "fit_viewport";
            edit.Summary["moves"] = new JArray(moves.Select(m => new JObject { ["viewport_id"] = Rid.Value(m.Item1.Id), ["shift_mm"] = new JArray(Math.Round(m.Item2.X / Mm, 2), Math.Round(m.Item2.Y / Mm, 2)) }));
            edit.Summary["already_inside"] = inside;
            edit.Apply = d => { foreach (var m in moves) m.Item1.SetBoxCenter(m.Item1.GetBoxCenter() + m.Item2); };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("inside");
                int ok = moves.Count(m =>
                {
                    double[] b = Outline((Viewport)d.GetElement(m.Item1.Id)), u = m.Item3; double tol = 0.5 * Mm;
                    return b[0] >= u[0] - tol && b[1] >= u[1] - tol && b[2] <= u[2] + tol && b[3] <= u[3] + tol;
                });
                check.Compare("inside", moves.Count, ok);
                return check;
            };
            return edit;
        }

        // ---- resolve_overlaps -----------------------------------------------------------------

        /// <summary>
        /// The annotation's box in the view frame. get_BoundingBox(view) of an annotation was measured with its
        /// coordinates multiplied by the view scale (Revit 2024); AnnotationBoxReader checks the box against the
        /// annotation's own anchor and divides it back when that is the case. A box that contains the anchor neither
        /// way is used as read, as before.
        /// </summary>
        private static double[] Box2D(Element e, View v)
        {
            double[] box = AnnotationBoxReader.ViewBox(e, v, out string _, out double[] raw);
            return box ?? raw;
        }

        private static ArchModelEdit Overlaps(Document doc, JObject r)
        {
            View v = Need<View>(doc, r.Value<long>("view_id"), "view");
            double gap = (r.Value<double?>("gap_mm") ?? 2) * Mm * v.Scale;
            var items = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().Where(e => e is IndependentTag || e is TextNote)
                .Select(e => new { E = e, B = Box2D(e, v) }).Where(x => x.B != null).OrderBy(x => Rid.Value(x.E.Id)).ToList();
            var moves = new Dictionary<long, XYZ>();
            var pairs = new List<long[]>();
            var boxes = items.ToDictionary(x => Rid.Value(x.E.Id), x => x.B);
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    long a = Rid.Value(items[i].E.Id), b = Rid.Value(items[j].E.Id);
                    if (!PlanimetryOpsRules.Overlap(boxes[a], boxes[b])) continue;
                    pairs.Add(new[] { a, b });
                    Element mover = items[j].E.Pinned ? items[i].E : items[j].E, fixedOne = mover == items[j].E ? items[i].E : items[j].E;
                    if (mover.Pinned || moves.ContainsKey(Rid.Value(mover.Id))) continue;
                    double[] s = PlanimetryOpsRules.Separate(boxes[Rid.Value(fixedOne.Id)], boxes[Rid.Value(mover.Id)], gap);
                    XYZ delta = v.RightDirection * s[0] + v.UpDirection * s[1];
                    moves[Rid.Value(mover.Id)] = delta;
                    double[] mb = boxes[Rid.Value(mover.Id)];
                    boxes[Rid.Value(mover.Id)] = new[] { mb[0] + s[0], mb[1] + s[1], mb[2] + s[0], mb[3] + s[1] };
                }
            if (moves.Count == 0) return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["overlapping_pairs"] = pairs.Count, ["note"] = pairs.Count == 0 ? "No tag or text overlaps another in this view." : "The overlapping ones are pinned; nothing was moved." } };
            var edit = new ArchModelEdit();
            foreach (long id in moves.Keys) edit.Planned.Add(ModelEditRunner.Planned(doc.GetElement(Rid.Make(id)), PlannedAction.Modify, r));
            edit.Summary["operation"] = "resolve_overlaps";
            edit.Summary["overlapping_pairs"] = pairs.Count;
            edit.Summary["moves"] = moves.Count;
            edit.Summary["not_handled"] = "dimensions are not moved by this operation";
            edit.Apply = d =>
            {
                foreach (var m in moves)
                {
                    Element e = d.GetElement(Rid.Make(m.Key));
                    if (e is IndependentTag tag) tag.TagHeadPosition = tag.TagHeadPosition + m.Value;
                    else if (e is TextNote tn) tn.Coord = tn.Coord + m.Value;
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("moved", "pairs_cleared");
                int cleared = pairs.Count(p => { double[] a = Box2D(d.GetElement(Rid.Make(p[0])), v), b = Box2D(d.GetElement(Rid.Make(p[1])), v); return a != null && b != null && !PlanimetryOpsRules.Overlap(a, b); });
                check.Compare("moved", moves.Count, moves.Count(m => { double[] b = Box2D(d.GetElement(Rid.Make(m.Key)), v); return b != null; }));
                check.Compare("pairs_cleared", pairs.Count, cleared);
                return check;
            };
            return edit;
        }

        // ---- legend_from_families ---------------------------------------------------------------

        private static ArchModelEdit Legend(Document doc, JObject r)
        {
            View source = Need<View>(doc, r.Value<long>("source_legend_id"), "legend");
            if (source.ViewType != ViewType.Legend) throw new ArgumentException("source_legend_id " + Rid.Value(source.Id) + " is not a legend. Nothing was written.");
            string name = r.Value<string>("name");
            if (new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Any(x => x.Name == name)) throw new ArgumentException("a view named '" + name + "' already exists. Nothing was written.");
            if (!new FilteredElementCollector(doc, source.Id).OfCategory(BuiltInCategory.OST_LegendComponents).Any())
                throw new ArgumentException("legend " + source.Name + " has no legend component to copy; the API cannot create one from nothing. Nothing was written.");
            var types = new List<FamilySymbol>();
            foreach (JToken t in (JArray)r["categories"])
            {
                if (!Enum.TryParse((string)t, out BuiltInCategory bic)) throw new ArgumentException("'" + t + "' is not a BuiltInCategory name. Nothing was written.");
                types.AddRange(new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().OfType<FamilyInstance>().Select(i => i.Symbol));
            }
            types = types.GroupBy(s => s.Id).Select(g => g.First()).OrderBy(s => s.Category?.Name).ThenBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            if (types.Count == 0) return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["note"] = "No family instance of these categories is placed; nothing was written." } };
            if (types.Count > 200) throw new ArgumentException(types.Count + " types: more than a legend of 200 rows; narrow the categories. Nothing was written.");
            string label = r.Value<string>("label") ?? "family_and_type";
            Func<FamilySymbol, string> text = s => label == "type_name" ? s.Name : label == "type_mark" ? (s.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() ?? s.Name) : s.FamilyName + " : " + s.Name;
            ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            var edit = new ArchModelEdit();
            var legendInputs = new Dictionary<string, string> { ["source_unique_id"] = source.UniqueId, ["name"] = name, ["request"] = ArchitecturalEditRules.ProposedRequest(r) };
            edit.Planned.Add(NoteLeaderRules.PlannedCreation("legend", 0, legendInputs));
            for (int index = 0; index < types.Count; index++)
            {
                var inputs = new Dictionary<string, string>(legendInputs) { ["type_unique_id"] = types[index].UniqueId, ["label"] = text(types[index]) };
                edit.Planned.Add(NoteLeaderRules.PlannedCreation("legend_label", index, inputs));
                if (index > 0) edit.Planned.Add(NoteLeaderRules.PlannedCreation("legend_component", index, inputs));
            }
            edit.Evidence.Merge(PlanimetryOpsRules.LegendRegenerationEvidence());
            edit.Summary["operation"] = "legend_from_families";
            edit.Summary["name"] = name;
            edit.Summary["rows"] = new JArray(types.Select(s => new JObject { ["type_id"] = Rid.Value(s.Id), ["label"] = text(s) }));
            long legendId = -1;
            var components = new List<long>(); var labels = new List<long>();
            edit.Apply = d =>
            {
                components.Clear(); labels.Clear();
                var legend = (View)d.GetElement(source.Duplicate(ViewDuplicateOption.WithDetailing));
                legendId = Rid.Value(legend.Id);
                legend.Name = name;
                var inLegend = new FilteredElementCollector(d, legend.Id).WhereElementIsNotElementType().ToElements();
                // INT-9 m-3: what came with the duplicate is the source's, not created here (the new view and the labels below are ours).
                SessionCreationLedger.NoteDuplicated(ChangeWatch.Key(d), inLegend.Select(e => e.UniqueId));
                Element template = inLegend.First(e => e.Category?.Id == new ElementId(BuiltInCategory.OST_LegendComponents));
                d.Delete(inLegend.Where(e => e.Id != template.Id && (e.Category?.Id == new ElementId(BuiltInCategory.OST_LegendComponents) || e is TextNote)).Select(e => e.Id).ToList());
                double step = (r.Value<double?>("spacing_mm") ?? 15) * Mm * legend.Scale;
                BoundingBoxXYZ tb = template.get_BoundingBox(legend);
                XYZ anchor = tb == null ? XYZ.Zero : (tb.Min + tb.Max) / 2;
                double width = tb == null ? step : (tb.Max - tb.Min).DotProduct(legend.RightDirection);
                for (int i = 0; i < types.Count; i++)
                {
                    ElementId cid = i == 0 ? template.Id : ElementTransformUtils.CopyElement(d, template.Id, -legend.UpDirection * step * i).First();
                    Parameter p = d.GetElement(cid).get_Parameter(BuiltInParameter.LEGEND_COMPONENT);
                    if (p == null || p.IsReadOnly || !p.Set(types[i].Id)) throw new InvalidOperationException("the legend component would not take type " + Rid.Value(types[i].Id) + " (" + text(types[i]) + ").");
                    components.Add(Rid.Value(cid));
                    XYZ at = anchor - legend.UpDirection * step * i + legend.RightDirection * (Math.Abs(width) / 2 + step / 2);
                    labels.Add(Rid.Value(TextNote.Create(d, legend.Id, at, text(types[i]), textType).Id));
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("legend", "components", "labels");
                var legend = legendId > 0 ? d.GetElement(Rid.Make(legendId)) as View : null;
                check.Record("legend", name, legend?.Name, legend != null && legend.ViewType == ViewType.Legend && legend.Name == name);
                check.Compare("components", types.Count, components.Select((c, i) => d.GetElement(Rid.Make(c))?.get_Parameter(BuiltInParameter.LEGEND_COMPONENT)?.AsElementId() == types[i].Id).Count(x => x));
                check.Compare("labels", types.Count, labels.Select((l, i) => (d.GetElement(Rid.Make(l)) as TextNote)?.Text?.TrimEnd('\r', '\n') == text(types[i])).Count(x => x));
                edit.Evidence["legend_id"] = legendId;
                return check;
            };
            return edit;
        }
    }
}
