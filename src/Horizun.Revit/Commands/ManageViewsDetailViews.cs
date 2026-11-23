// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_manage_views create_callout / create_section as DETAIL views
// (FIELD FEEDBACK 2.2, 2.6, 4.3):
//   * template_view_id applies a view template in the same action;
//   * "Hide at scales coarser than" is written on the new view: by default 1 in a
//     workshared model, so the new marker never shows in somebody else's plan
//     (hide_marker_coarser_than overrides it; 0 keeps Revit's default);
//   * the reply carries the new view's crop (model corners, size, size on paper)
//     and the marker elements that stand for it in other views.
// Every one of those is re-read in the verification that decides the batch.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        private static void ValidateNewDetailView(Document doc, JObject a)
        {
            string bad = ViewMarkerRules.Validate(a.Value<int?>("hide_marker_coarser_than"));
            if (bad != null) throw new ArgumentException(bad);
            if (IsCallout(a) && (a.Value<int?>("hide_marker_coarser_than") ?? 0) > 0)
                throw new ArgumentException("hide_marker_coarser_than does not apply to create_callout: a callout's marker shows only in its " +
                    "parent view, and Revit keeps the callout's 'Hide at scales coarser than' read-only (measured live, Revit 2026). " +
                    "Draw the callout in one of your own views.");
            if (a["template_view_id"] != null)
            {
                View template = Need<View>(doc, a, "template_view_id");
                if (!template.IsTemplate) throw new ArgumentException("template_view_id is not a view template");
            }
        }

        private static void ApplyNewDetailView(Document doc, View view, JObject a)
        {
            if (a["template_view_id"] != null) view.ViewTemplateId = Need<View>(doc, a, "template_view_id").Id;
            bool workshared = false;
            try { workshared = doc.IsWorkshared; } catch { }
            int? requested = a.Value<int?>("hide_marker_coarser_than");
            // A callout's marker lives only in its parent view, and its scale is read-only.
            int? value = IsCallout(a) ? null : ViewMarkerRules.ForNewView(workshared, requested);
            if (IsCallout(a)) a["__coarser_skipped"] = "a callout's marker shows only in its parent view; Revit keeps its scale read-only.";
            if (value == null) return;
            try
            {
                ViewMarkers.SetCoarser(view, value.Value);
                a["__coarser"] = value.Value;
            }
            catch (Exception ex)
            {
                // Asked for explicitly: the batch must not pretend. By default: reported, not fatal.
                if (requested.HasValue) throw;
                a["__coarser_skipped"] = ex.Message;
            }
        }

        private static bool IsCallout(JObject a) =>
            string.Equals(a.Value<string>("operation"), "create_callout", StringComparison.OrdinalIgnoreCase);

        private static bool NewDetailViewHolds(Document doc, View view, JObject a)
        {
            if (a["template_view_id"] != null && Rid.Value(view.ViewTemplateId) != a.Value<long>("template_view_id")) return false;
            int? coarser = a.Value<int?>("__coarser");
            if (coarser.HasValue && ViewMarkers.ReadCoarser(view) != coarser.Value) return false;
            return true;
        }

        private static JObject NewDetailViewFacts(Document doc, View view, JObject a)
        {
            var o = new JObject();
            try
            {
                BoundingBoxXYZ cb = view.CropBox;
                Transform t = cb.Transform ?? Transform.Identity;
                XYZ p0 = t.OfPoint(cb.Min), p1 = t.OfPoint(cb.Max);
                double w = (cb.Max.X - cb.Min.X) * 304.8, h = (cb.Max.Y - cb.Min.Y) * 304.8;
                int scale = 0; try { scale = view.Scale; } catch { }
                o["crop"] = new JObject
                {
                    ["active"] = view.CropBoxActive,
                    ["model_min_mm"] = new JArray(Math.Round(Math.Min(p0.X, p1.X) * 304.8, 1), Math.Round(Math.Min(p0.Y, p1.Y) * 304.8, 1), Math.Round(Math.Min(p0.Z, p1.Z) * 304.8, 1)),
                    ["model_max_mm"] = new JArray(Math.Round(Math.Max(p0.X, p1.X) * 304.8, 1), Math.Round(Math.Max(p0.Y, p1.Y) * 304.8, 1), Math.Round(Math.Max(p0.Z, p1.Z) * 304.8, 1)),
                    ["size_mm"] = new JArray(Math.Round(w, 1), Math.Round(h, 1)),
                    ["scale"] = scale,
                    ["paper_size_mm"] = scale > 0 ? new JArray(Math.Round(w / scale, 1), Math.Round(h / scale, 1)) : null
                };
            }
            catch (Exception ex) { o["crop_error"] = ex.Message; }
            try { o["marker_ids"] = new JArray(ViewMarkers.MarkersOf(doc, view).Select(id => (JToken)Rid.Value(id))); }
            catch (Exception ex) { o["marker_error"] = ex.Message; }
            o["hide_marker_coarser_than"] = ViewMarkers.ReadCoarser(view);
            if (a["__coarser_skipped"] != null) o["hide_marker_coarser_than_skipped"] = a["__coarser_skipped"].DeepClone();
            if (view.ViewTemplateId != ElementId.InvalidElementId) o["template_id"] = Rid.Value(view.ViewTemplateId);
            return o;
        }
    }
}
