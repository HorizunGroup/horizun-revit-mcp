// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// The Revit half of the tag/text-note overlap check (rules in
// TagOverlapRules.cs). Given a view, collects every IndependentTag and
// TextNote placed IN it, reads each one's bounding box IN VIEW COORDINATES
// (Element.get_BoundingBox(view) - not model space: annotation is drawn flat
// on the sheet or view, and two tags a metre apart in the model can still sit
// on top of each other on paper), and reports:
//
//   * any two annotation items in the view whose boxes overlap;
//   * two tags that both tag the SAME element and whose boxes overlap - a
//     duplicate callout on the same object, the most literal reading of "a
//     tag overlapping the tagged element's own tag head".
//
// A LABEL-ONLY tag - MEASURED: an IndependentTag placed with TagOrientation
// that carries no leader and no box Revit will hand back through
// get_BoundingBox(view) - comes back with Box=null. It is counted as
// "unmeasured" and never participates in an overlap comparison: the check did
// not look, so it must not report the tag as clear.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class TagOverlapCheck
    {
        /// <summary>Above this many tags/text notes in one view, the pairwise pass is skipped
        /// for that view (O(n^2) on a title-heavy sheet) rather than run partially.</summary>
        public const int MaxItemsPerView = 2000;

        public sealed class Item
        {
            public long Id;
            public string Kind;   // "tag" | "text_note"
            public TagOverlapRules.Rect? Box;
            public long? TaggedElementId;
        }

        public sealed class Finding
        {
            public long A, B;
            public string Reason;
        }

        public sealed class ViewOutcome
        {
            public long ViewId;
            public string ViewName;
            public bool Skipped;
            public string SkippedWhy;
            public readonly List<Item> Items = new List<Item>();
            public readonly List<Finding> Findings = new List<Finding>();
            public int Unmeasured => Items.Count(i => !i.Box.HasValue);
        }

        /// <summary>Every IndependentTag and TextNote owned by this view, with the view-space
        /// box Revit reports for each, and the overlap findings among them.</summary>
        public static ViewOutcome Collect(Document doc, View view)
        {
            var o = new ViewOutcome { ViewId = Rid.Value(view.Id), ViewName = SafeName(view) };
            IList<Element> anns;
            try
            {
                anns = new FilteredElementCollector(doc, view.Id)
                    .WherePasses(new ElementMulticlassFilter(new[] { typeof(IndependentTag), typeof(TextNote) }))
                    .ToElements();
            }
            catch (Exception ex) { o.Skipped = true; o.SkippedWhy = "could not enumerate annotation in this view: " + ex.Message; return o; }
            if (anns.Count > MaxItemsPerView)
            {
                o.Skipped = true;
                o.SkippedWhy = anns.Count + " tags/text notes in this view exceed the " + MaxItemsPerView + "-item cap for the pairwise pass.";
                return o;
            }
            foreach (Element e in anns)
            {
                var item = new Item { Id = Rid.Value(e.Id), Kind = e is TextNote ? "text_note" : "tag", Box = SafeBox(e, view) };
                if (e is IndependentTag t)
                {
                    try { item.TaggedElementId = t.GetTaggedLocalElementIds()?.Select(Rid.Value).Cast<long?>().FirstOrDefault(); }
                    catch { }
                }
                o.Items.Add(item);
            }
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < o.Items.Count; i++)
            {
                Item a = o.Items[i];
                if (!a.Box.HasValue) continue;
                for (int j = i + 1; j < o.Items.Count; j++)
                {
                    Item b = o.Items[j];
                    if (!b.Box.HasValue || !TagOverlapRules.Overlaps(a.Box.Value, b.Box.Value)) continue;
                    if (!pairs.Add(PairKey(a.Id, b.Id))) continue;
                    string reason = a.TaggedElementId.HasValue && a.TaggedElementId == b.TaggedElementId
                        ? "two tags of the same element overlap"
                        : Describe(a) + " overlaps " + Describe(b);
                    o.Findings.Add(new Finding { A = a.Id, B = b.Id, Reason = reason });
                }
            }
            return o;
        }

        private static string Describe(Item i) => i.Kind == "text_note" ? "a text note" : "a tag";
        private static string PairKey(long a, long b) => Math.Min(a, b) + "|" + Math.Max(a, b);

        private static TagOverlapRules.Rect? SafeBox(Element e, View view)
        {
            try
            {
                BoundingBoxXYZ b = e.get_BoundingBox(view);
                if (b == null) return null;
                // ON SCREEN, not in model X/Y (backlog #12): the box's corners, through its own
                // transform, projected onto the view's right and up directions. Model X/Y read a
                // section's or an elevation's tags as flat (never overlapping) and a rotated
                // plan's neighbours as overlapping.
                XYZ right = view.RightDirection, up = view.UpDirection;
                Transform t = b.Transform;
                TagOverlapRules.BoxTransform bt = t == null || t.IsIdentity ? null : new TagOverlapRules.BoxTransform
                {
                    Origin = new[] { t.Origin.X, t.Origin.Y, t.Origin.Z },
                    BasisX = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z },
                    BasisY = new[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z },
                    BasisZ = new[] { t.BasisZ.X, t.BasisZ.Y, t.BasisZ.Z }
                };
                return TagOverlapRules.ViewRect(new[] { b.Min.X, b.Min.Y, b.Min.Z }, new[] { b.Max.X, b.Max.Y, b.Max.Z }, bt,
                                                new[] { right.X, right.Y, right.Z }, new[] { up.X, up.Y, up.Z });
            }
            catch { return null; }
        }

        private static string SafeName(Element e)
        {
            try { return e.Name; } catch { return "view " + Rid.Value(e.Id); }
        }

        private static JObject DescribeJson(Document doc, long id)
        {
            var o = new JObject { ["id"] = id };
            try
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e != null) { o["category"] = e.Category?.Name; o["class"] = e.GetType().Name; }
            }
            catch { }
            return o;
        }

        /// <summary>Run the check over one or more views and shape the JSON the caller returns.
        /// Duplicate views (by id) are examined once.</summary>
        public static JObject Run(Document doc, IEnumerable<View> views)
        {
            var viewsOut = new JArray();
            var findings = new JArray();
            int items = 0, unmeasured = 0;
            var seen = new HashSet<long>();
            foreach (View v in views ?? Enumerable.Empty<View>())
            {
                if (v == null || !v.IsValidObject) continue;
                long vid = Rid.Value(v.Id);
                if (!seen.Add(vid)) continue;
                ViewOutcome vo = Collect(doc, v);
                if (vo.Skipped)
                {
                    viewsOut.Add(new JObject { ["id"] = vid, ["name"] = vo.ViewName, ["skipped"] = true, ["why"] = vo.SkippedWhy });
                    continue;
                }
                items += vo.Items.Count; unmeasured += vo.Unmeasured;
                viewsOut.Add(new JObject { ["id"] = vid, ["name"] = vo.ViewName, ["items"] = vo.Items.Count, ["unmeasured"] = vo.Unmeasured });
                foreach (Finding f in vo.Findings)
                    findings.Add(new JObject { ["a"] = DescribeJson(doc, f.A), ["b"] = DescribeJson(doc, f.B), ["reason"] = f.Reason });
            }
            string status = findings.Count > 0 ? "overlaps" : viewsOut.Count == 0 ? "nothing_to_check" : "clean";
            return new JObject
            {
                ["status"] = status,
                ["views"] = viewsOut,
                ["items"] = items,
                ["unmeasured"] = unmeasured,
                ["findings"] = findings,
                ["method"] = "view-coordinate bounding boxes (Element.get_BoundingBox(view)) of every IndependentTag and TextNote in each view, compared pairwise; " +
                              "a label-only tag whose extent Revit does not expose is 'unmeasured', never counted as clear."
            };
        }
    }
}
