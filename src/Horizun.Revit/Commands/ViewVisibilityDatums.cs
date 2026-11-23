// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_view_visibility operations datum_cleanup and set_scale (4.2-N3).
//
// Field request (documentation model, Revit 2024): the review views showed grids and levels
// that had nothing to do with the sheet - datums far outside the crop, datums other people own -
// and the person hid them one by one with scripts (hidefar, datumfix); and after changing a
// view's scale, other people's section marks reappeared in it.
//
//   datum_cleanup  in OUR view_id, hides HOST grids/levels that (outside_crop) do not touch the
//                  active crop or (foreign) are not ours, minus keep_ids. A view-level hide only:
//                  the grid or level itself is not touched. LINKED datums cannot be hidden one by
//                  one (HideElements takes host ids; the per-category table inside a link is not in
//                  the API): the reply says so and points to copy_link_overrides.
//   set_scale      changes OUR view's scale (the same checks as view creation: a template that
//                  controls View Scale refuses by name) and, by default, hides every foreign
//                  marker visible AFTER the new scale, i.e. those whose "hide at scales coarser
//                  than" no longer covers it. Re-read: the scale and zero visible foreign marks.
//
// Both go through ModelEditRunner: rehearse by default, single-use token, re-read after commit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ViewVisibilityCommand
    {
        /// <summary>The foreign (not ours) markers visible in the view that it can hide; <paramref name="kept"/> receives the ones left visible and why.</summary>
        private static List<Element> ForeignVisibleMarkers(Document doc, View view, HashSet<string> session, JArray kept)
        {
            var foreign = new List<Element>();
            foreach (Element m in new FilteredElementCollector(doc, view.Id).WherePasses(ViewMarkers.Filter()).WhereElementIsNotElementType())
            {
                bool hidden; try { hidden = m.IsHidden(view); } catch { hidden = false; }
                if (hidden) continue;
                string creator;
                OwnershipVerdict v = OwnershipProbe.Of(doc, m, session, out creator);
                if (v.Own) { kept.Add(new JObject { ["element_id"] = Rid.Value(m.Id), ["name"] = m.Name, ["evidence"] = v.Evidence }); continue; }
                if (!m.CanBeHidden(view)) { kept.Add(new JObject { ["element_id"] = Rid.Value(m.Id), ["name"] = m.Name, ["reason"] = "cannot be hidden in this view" }); continue; }
                foreign.Add(m);
            }
            return foreign;
        }

        // This is an inventory of loaded link documents, not a visibility test.
        private static JObject LinkedDatumInventory(Document doc)
        {
            var rows = new JArray();
            var unavailable = new JArray();
            foreach (RevitLinkInstance instance in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document linked = null;
                try { linked = instance.GetLinkDocument(); } catch { }
                if (linked == null) { unavailable.Add(Rid.Value(instance.Id)); continue; }
                try
                {
                    foreach (Element element in new FilteredElementCollector(linked).WhereElementIsNotElementType())
                    {
                        string kind = element is Grid ? "grid" : element is Level ? "level" : element is ReferencePlane ? "reference_plane" : null;
                        if (kind == null) continue;
                        var verdict = LinkedDatumRules.Cleanup(kind);
                        rows.Add(new JObject { ["link_instance_id"] = Rid.Value(instance.Id),
                            ["linked_element_id"] = Rid.Value(element.Id), ["linked_element_unique_id"] = element.UniqueId,
                            ["name"] = element.Name, ["kind"] = kind, ["reason"] = verdict.Reason,
                            ["cleaned"] = verdict.Cleaned, ["suggestion"] = verdict.Suggestion });
                    }
                }
                catch { unavailable.Add(Rid.Value(instance.Id)); }
            }
            return new JObject { ["items"] = rows, ["unavailable_link_instances"] = unavailable,
                ["coverage"] = "loaded link document inventory; visibility in this view is not established" };
        }

        // ---- datum_cleanup ---------------------------------------------------------------------

        private static ArchModelEdit DatumCleanup(Document doc, JObject r, View view, HashSet<string> session)
        {
            string why;
            List<string> kinds = ViewDatumRules.ParseKinds(r["datum_kinds"], out why);
            List<string> criteria = ViewDatumRules.ParseCriteria(r["criteria"], out why);
            var keep = new HashSet<long>(((r["keep_ids"] as JArray) ?? new JArray()).Select(t => t.Value<long>()));

            bool needCrop = criteria.Contains(ViewDatumRules.CriterionOutsideCrop);
            BoundingBoxXYZ crop = null;
            Transform toLocal = null;
            if (needCrop)
            {
                if (!view.CropBoxActive)
                    throw new ArgumentException("criteria outside_crop needs an ACTIVE crop on view '" + view.Name + "' (" + Rid.Value(view.Id) +
                        "): without a crop every datum is 'inside'. Activate the crop (horizun_manage_views set_crop), or use criteria=[foreign]. Nothing was written.");
                crop = view.CropBox;
                toLocal = crop.Transform.Inverse;
            }

            var hide = new List<Element>();
            var rows = new JArray();
            var kept = new JArray();
            int unreadable = 0, alreadyHidden = 0;
            foreach (string kind in kinds)
            {
                Type cls = kind == ViewDatumRules.KindGrid ? typeof(Grid) : typeof(Level);
                foreach (DatumPlane datum in new FilteredElementCollector(doc, view.Id).OfClass(cls).Cast<DatumPlane>().OrderBy(d => Rid.Value(d.Id)))
                {
                    bool hidden; try { hidden = datum.IsHidden(view); } catch { hidden = false; }
                    if (hidden) { alreadyHidden++; continue; }
                    bool? touches = null;
                    if (needCrop)
                    {
                        try
                        {
                            Curve c = datum.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault()
                                      ?? datum.GetCurvesInView(DatumExtentType.Model, view).FirstOrDefault();
                            if (c != null)
                            {
                                XYZ a = toLocal.OfPoint(c.GetEndPoint(0)), b = toLocal.OfPoint(c.GetEndPoint(1));
                                touches = ViewDatumRules.SegmentTouchesRect(a.X, a.Y, b.X, b.Y, crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y);
                            }
                        }
                        catch { touches = null; }
                        if (touches == null) unreadable++;
                    }
                    string creator;
                    OwnershipVerdict own = OwnershipProbe.Of(doc, datum, session, out creator);
                    bool canHide; try { canHide = datum.CanBeHidden(view); } catch { canHide = false; }
                    ViewDatumRules.Verdict verdict = ViewDatumRules.Decide(criteria, keep.Contains(Rid.Value(datum.Id)), canHide, own.Own, touches);
                    var row = new JObject
                    {
                        ["element_id"] = Rid.Value(datum.Id), ["name"] = datum.Name, ["kind"] = kind, ["own"] = own.Own,
                        ["touches_crop"] = touches.HasValue ? (JToken)touches.Value : JValue.CreateNull()
                    };
                    if (verdict.Hide) { row["because"] = new JArray(verdict.Because); hide.Add(datum); rows.Add(row); }
                    else { row["reason"] = verdict.Reason; kept.Add(row); }
                }
            }

            JObject linkedDatums = LinkedDatumInventory(doc);
            var edit = new ArchModelEdit();
            edit.Summary["operation"] = "datum_cleanup"; edit.Summary["view_id"] = Rid.Value(view.Id); edit.Summary["view_name"] = view.Name;
            edit.Summary["datum_kinds"] = new JArray(kinds); edit.Summary["criteria"] = new JArray(criteria);
            edit.Summary["hide"] = rows; edit.Summary["kept_visible"] = kept;
            edit.Summary["already_hidden"] = alreadyHidden;
            if (unreadable > 0) edit.Summary["extent_unreadable"] = unreadable;
            edit.Summary["linked_datums"] = linkedDatums;
            if (hide.Count == 0)
            {
                edit.ReadResult = new JObject
                {
                    ["operation"] = "datum_cleanup", ["view_id"] = Rid.Value(view.Id), ["state"] = "no_op", ["hide"] = 0,
                    ["kept_visible"] = kept, ["already_hidden"] = alreadyHidden, ["linked_datums"] = linkedDatums,
                    ["means"] = "no host grid or level in this view meets the criteria; nothing was written"
                };
                return edit;
            }
            var ids = hide.Select(e => e.Id).ToList();
            edit.Planned.Add(ModelEditRunner.Planned(view, PlannedAction.Modify, r));
            edit.Apply = d => ((View)d.GetElement(view.Id)).HideElements(ids);
            edit.Verify = d =>
            {
                View v = (View)d.GetElement(view.Id);
                var check = new PostconditionCheck(ids.Select(id => "hidden:" + Rid.Value(id)).ToArray());
                foreach (ElementId id in ids) { Element e = d.GetElement(id); check.Compare("hidden:" + Rid.Value(id), true, e != null && e.IsHidden(v)); }
                edit.Evidence["hidden_ids"] = new JArray(ids.Select(i => (JToken)Rid.Value(i)));
                edit.Evidence["datums_untouched"] = "the grids and levels were hidden in this view only; no datum element was modified";
                return check;
            };
            return edit;
        }

        // ---- set_scale -------------------------------------------------------------------------

        private static ArchModelEdit SetScale(Document doc, JObject r, View view, HashSet<string> session)
        {
            int wanted = r.Value<int>("scale");
            bool rehide = r["rehide_foreign_markers"] == null || r.Value<bool>("rehide_foreign_markers");
            string refusal = ManageViewsCommand.ViewScaleRefusal(doc, view, wanted, "set_scale");
            if (refusal != null) throw new ArgumentException(refusal + " Nothing was written.");
            int before = view.Scale;
            int foreignNow = ForeignVisibleMarkers(doc, view, session, new JArray()).Count;

            var edit = new ArchModelEdit();
            edit.Summary["operation"] = "set_scale"; edit.Summary["view_id"] = Rid.Value(view.Id); edit.Summary["view_name"] = view.Name;
            edit.Summary["from_scale"] = before; edit.Summary["to_scale"] = wanted;
            edit.Summary["rehide_foreign_markers"] = rehide;
            edit.Summary["foreign_markers_visible_now"] = foreignNow;
            edit.Summary["note"] = rehide
                ? "After the new scale is set, every foreign section/callout/elevation mark that the new scale makes visible is hidden in this view, in the same transaction."
                : "Foreign marks that the new scale makes visible are left as Revit draws them.";
            edit.Planned.Add(ModelEditRunner.Planned(view, PlannedAction.Modify, r));
            var hiddenNow = new List<ElementId>();
            edit.Apply = d =>
            {
                View v = (View)d.GetElement(view.Id);
                ManageViewsCommand.ApplyViewScale(d, v, wanted, "set_scale");
                d.Regenerate();
                if (!rehide) return;
                hiddenNow.Clear();
                hiddenNow.AddRange(ForeignVisibleMarkers(d, v, session, new JArray()).Select(e => e.Id));
                if (hiddenNow.Count > 0) v.HideElements(hiddenNow);
            };
            edit.Verify = d =>
            {
                View v = (View)d.GetElement(view.Id);
                var names = new List<string> { "scale" };
                if (rehide) names.Add("foreign_markers_visible");
                var check = new PostconditionCheck(names.ToArray());
                check.Compare("scale", (long)wanted, (long)v.Scale);
                if (rehide) check.Compare("foreign_markers_visible", 0L, (long)ForeignVisibleMarkers(d, v, session, new JArray()).Count);
                edit.Evidence["scale_before"] = before; edit.Evidence["scale_after"] = v.Scale;
                edit.Evidence["markers_hidden_after_scale"] = new JArray(hiddenNow.Select(i => (JToken)Rid.Value(i)));
                return check;
            };
            return edit;
        }
    }
}
