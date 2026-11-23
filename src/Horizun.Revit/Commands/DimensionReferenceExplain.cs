// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_get_dimension_references explain_references / dimension_id (4.2-N3, CT-P2-2f).
//
// "Invalid number of references" says nothing. This reads each reference you hand it (or every
// reference of an existing dimension) and says WHY it cannot carry a dimension in view_id: the text
// is not a reference of this document, the element is gone, the link is unloaded or the element
// inside it deleted, a linked grid or level (Revit's dimension API refuses datums lifted through a
// link), the element or its category is hidden in the view, the reference lies parallel to the view
// plane; and for each consecutive pair, whether the two are parallel. Read-only: no transaction.
// The decisions are in Core/DimensionReferenceExplainRules.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class DimensionReferenceExplain
    {
        private sealed class Row
        {
            public string Stable;
            public ReferenceFacts Facts = new ReferenceFacts();
            public JObject Identity = new JObject();
            public List<ReferenceReason> Reasons;
        }

        public static CommandResult Run(Document doc, View view, JObject request)
        {
            string invalid = DimensionReferenceExplainRules.Validate(request);
            if (invalid != null) return CommandResult.Fail(invalid);

            var rows = new List<Row>();
            JObject dimensionInfo = null;
            if (request["dimension_id"] != null)
            {
                long raw = request.Value<long>("dimension_id");
                Dimension dim = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) as Dimension : null;
                if (dim == null) return CommandResult.Fail("dimension_id " + raw + " is not a dimension of the active document.");
                dimensionInfo = new JObject { ["dimension_id"] = raw, ["owner_view_id"] = Rid.Value(dim.OwnerViewId), ["explained_in_view_id"] = Rid.Value(view.Id) };
                ReferenceArray refs = null;
                try { refs = dim.References; } catch { }
                if (refs == null || refs.Size == 0)
                    return CommandResult.Fail("dimension " + raw + " exposes no references (a spot dimension or a constraint?); there is nothing to explain.");
                foreach (Reference r in refs)
                {
                    var row = new Row();
                    try { row.Stable = r.ConvertToStableRepresentation(doc); } catch { row.Stable = null; }
                    Fill(doc, view, r, row);
                    rows.Add(row);
                }
            }
            else
            {
                foreach (string text in ((JArray)request["explain_references"]).Select(t => (string)t))
                {
                    var row = new Row { Stable = text };
                    Reference r = null;
                    try { r = Reference.ParseFromStableRepresentation(doc, text); } catch { r = null; }
                    if (r == null) row.Facts.Parsed = false; else Fill(doc, view, r, row);
                    rows.Add(row);
                }
            }

            var items = new JArray();
            int blocked = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                row.Reasons = DimensionReferenceExplainRules.Explain(row.Facts);
                bool usable = row.Reasons.Count == 0;
                if (!usable) blocked++;
                items.Add(new JObject
                {
                    ["index"] = i, ["stable_representation"] = row.Stable, ["usable_in_dimension"] = usable,
                    ["identity"] = row.Identity,
                    ["in_view_direction"] = row.Facts.InViewDirection == null ? JValue.CreateNull() : new JArray(row.Facts.InViewDirection.Select(v => Math.Round(v, 6))),
                    ["reasons"] = new JArray(row.Reasons.Select(x => (JToken)x.ToJson()))
                });
            }
            var pairs = new JArray();
            for (int i = 0; i + 1 < rows.Count; i++)
            {
                List<ReferenceReason> why = DimensionReferenceExplainRules.ExplainPair(rows[i].Stable, rows[i + 1].Stable,
                    rows[i].Facts.InViewDirection, rows[i + 1].Facts.InViewDirection);
                bool bothFine = rows[i].Reasons.Count == 0 && rows[i + 1].Reasons.Count == 0;
                pairs.Add(new JObject
                {
                    ["a"] = i, ["b"] = i + 1, ["compatible"] = bothFine && why.Count == 0,
                    ["reasons"] = new JArray(why.Select(x => (JToken)x.ToJson()))
                });
            }
            var result = new JObject
            {
                ["read_only"] = true, ["mode"] = "explain", ["view_id"] = Rid.Value(view.Id), ["view_name"] = view.Name,
                ["references"] = items, ["pairs"] = pairs, ["blocked_count"] = blocked, ["total"] = rows.Count,
                ["means"] = "usable_in_dimension=false names, by code, what keeps the reference from carrying a dimension in this view; " +
                            "true means nothing found against it, not that Revit has accepted it (only a creation does)."
            };
            if (dimensionInfo != null) result["dimension"] = dimensionInfo;
            return CommandResult.Ok(result);
        }

        private static void Fill(Document doc, View view, Reference r, Row row)
        {
            ReferenceFacts f = row.Facts;
            Element owner = null;
            try { owner = doc.GetElement(r.ElementId); } catch { }
            ElementId linkedId = ElementId.InvalidElementId;
            try { linkedId = r.LinkedElementId; } catch { }
            f.IsLinked = linkedId != null && linkedId != ElementId.InvalidElementId;
            try { row.Identity["reference_type"] = r.ElementReferenceType.ToString(); } catch { }

            Element target = owner;
            Transform toHost = Transform.Identity;
            if (f.IsLinked)
            {
                var inst = owner as RevitLinkInstance;
                row.Identity["link_instance_id"] = Rid.Value(r.ElementId);
                row.Identity["linked_element_id"] = Rid.Value(linkedId);
                f.LinkInstanceExists = inst != null;
                if (inst != null)
                {
                    string status = "Unknown";
                    try { if (doc.GetElement(inst.GetTypeId()) is RevitLinkType t) status = t.GetLinkedFileStatus().ToString(); } catch { }
                    f.LinkLoaded = status == "Loaded";
                    Document linkDoc = null;
                    try { linkDoc = inst.GetLinkDocument(); } catch { }
                    if (linkDoc == null) f.LinkLoaded = false;
                    else
                    {
                        Element inside = null;
                        try { inside = linkDoc.GetElement(linkedId); } catch { }
                        f.LinkedElementExists = inside != null;
                        target = inside;
                        try { toHost = inst.GetTotalTransform(); } catch { }
                    }
                    row.Identity["link_status"] = status;
                }
            }
            else
            {
                f.ElementExists = owner != null;
                if (owner != null) row.Identity["element_id"] = Rid.Value(owner.Id);
            }
            if (target == null) return;
            row.Identity["category"] = target.Category?.Name; row.Identity["built_in_category"] = CategoryKey.Of(target);
            row.Identity["class"] = target.GetType().Name;
            f.IsDatum = target is DatumPlane;
            if (!f.IsLinked)
            {
                try { f.HiddenInView = target.IsHidden(view); } catch { }
                try { if (target.Category != null) f.CategoryHiddenInView = view.GetCategoryHidden(target.Category.Id); } catch { }
            }
            try { Direction(doc, view, r, target, f, toHost); }
            catch { f.GeometryUnreadable = true; }
        }

        /// <summary>The reference's unit direction as it lies in the view plane, or FaceOnToView when it is parallel to that plane.</summary>
        private static void Direction(Document doc, View view, Reference r, Element target, ReferenceFacts f, Transform toHost)
        {
            XYZ vn = view.ViewDirection.Normalize();
            XYZ planeNormal = null, lineDirection = null;
            if (target is Grid grid)
            {
                Curve c = grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault() ?? grid.Curve;
                if (c is Line gl) lineDirection = toHost.OfVector(gl.Direction);
            }
            else if (target is Level) planeNormal = toHost.OfVector(XYZ.BasisZ);
            else if (target is ReferencePlane rp) planeNormal = toHost.OfVector(rp.Normal);
            else
            {
                GeometryObject g = null;
                try { g = doc.GetElement(r.ElementId)?.GetGeometryObjectFromReference(r); } catch { }
                if (g is PlanarFace pf) planeNormal = toHost.OfVector(pf.FaceNormal);
                else if (g is Edge edge && edge.AsCurve() is Line el) lineDirection = toHost.OfVector(el.Direction);
                else if (g is Line l) lineDirection = toHost.OfVector(l.Direction);
            }
            if (planeNormal == null && lineDirection == null) { f.GeometryUnreadable = true; return; }
            XYZ inView = planeNormal != null ? vn.CrossProduct(planeNormal) : lineDirection - vn.Multiply(lineDirection.DotProduct(vn));
            if (inView.GetLength() < 1e-6) { f.FaceOnToView = true; return; }
            inView = inView.Normalize();
            f.InViewDirection = new[] { inView.X, inView.Y, inView.Z };
        }
    }
}
