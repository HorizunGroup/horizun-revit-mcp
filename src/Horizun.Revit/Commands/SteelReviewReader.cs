// -----------------------------------------------------------------------------
// Horizun Revit MCP - one rebar set, or one bar of an area or path system, read for
// the bar-against-bar checks (duplicate_bar, overlapping_bar) without a requirement set.
// Original Horizun code. Read-only.
//
// THE BAR AT EVERY POSITION, AS THE MODEL CARRIES IT: GetTransformedCenterlineCurves
// at each position the model says holds a bar - the position transform and any bar
// somebody moved included - with the hooks suppressed (a hook is the drawing of a
// declared termination, measured nowhere here) and the bends kept. The model diameter,
// the host and its category. Whatever would not read is the set's UnreadableWhy:
// RebarSteelReview counts it, names it, and never calls a model with it `clean`.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public static class SteelReviewReader
    {
        public const double FtToMm = 304.8;

        /// <summary>A rebar set: every position that holds a bar, hook-free, in model millimetres.</summary>
        public static ReviewSetFacts Read(Document doc, Rebar bar, Dictionary<long, string> categories)
        {
            var f = new ReviewSetFacts { Id = Rid.Value(bar.Id), Label = RebarSteelReview.LabelSet };
            ElementId host = null;
            try { host = bar.GetHostId(); } catch { }
            Host(doc, host, f, categories);
            ElementId shape = null;
            try { shape = bar.GetShapeId(); } catch { }
            f.Style = Style(doc, shape);
            f.ModelDiameterMm = Diameter(doc, bar.GetTypeId());
            for (int end = 0; end <= 1; end++)
            {
                try { if (bar.GetHookTypeId(end) != ElementId.InvalidElementId) f.HooksNotMeasured++; } catch { }
                Coupled(doc, bar, end, f);
            }
            int n = 0;
            try { n = bar.NumberOfBarPositions; } catch { f.UnreadableWhy = "the set would not say how many bar positions it has."; return f; }
            for (int i = 0; i < n; i++)
            {
                bool exists = true;
                try { exists = bar.DoesBarExistAtPosition(i); }
                catch { f.UnreadableWhy = "position " + i + " would not say whether it holds a bar."; f.Bars.Clear(); return f; }
                if (!exists) continue;
                IList<Curve> curves = null;
                try { curves = bar.GetTransformedCenterlineCurves(false, true, false, MultiplanarOption.IncludeAllMultiplanarCurves, i); }
                catch { }
                List<double[]> pts = Tessellate(curves);
                if (pts == null) { f.UnreadableWhy = "position " + i + " would not return its centreline."; f.Bars.Clear(); return f; }
                f.Bars.Add(new OverlapBar { Position = i, Points = pts });
            }
            return f;
        }

        /// <summary>A bar of an area or path system: its own positions, the system's id for its layout.</summary>
        public static ReviewSetFacts Read(Document doc, RebarInSystem bar, Dictionary<long, string> categories)
        {
            var f = new ReviewSetFacts { Id = Rid.Value(bar.Id) };
            ElementId sys = null;
            try { sys = bar.SystemId; } catch { }
            Element system = sys != null && sys != ElementId.InvalidElementId ? doc.GetElement(sys) : null;
            f.SystemId = system == null ? -1 : Rid.Value(sys);
            f.Label = system is PathReinforcement ? RebarSteelReview.LabelPath : RebarSteelReview.LabelArea;
            ElementId host = null;
            try { host = bar.GetHostId(); } catch { }
            Host(doc, host, f, categories);
            ElementId shape = null;
            try { shape = bar.RebarShapeId; } catch { }
            f.Style = Style(doc, shape);
            f.ModelDiameterMm = Diameter(doc, bar.GetTypeId());
            for (int end = 0; end <= 1; end++)
            {
                try { if (bar.GetHookTypeId(end) != ElementId.InvalidElementId) f.HooksNotMeasured++; } catch { }
            }
            if (system == null)
            {
                f.UnreadableWhy = "the bar's area or path reinforcement system did not resolve.";
                return f;
            }
            int n = 0;
            try { n = bar.NumberOfBarPositions; } catch { f.UnreadableWhy = "the bar would not say how many positions it has."; return f; }
            for (int i = 0; i < n; i++)
            {
                bool exists = true;
                try { exists = bar.DoesBarExistAtPosition(i); }
                catch { f.UnreadableWhy = "position " + i + " would not say whether it holds a bar."; f.Bars.Clear(); return f; }
                if (!exists) continue;
                IList<Curve> curves = null;
                try { curves = bar.GetTransformedCenterlineCurves(false, true, false, i); } catch { }
                List<double[]> pts = Tessellate(curves);
                if (pts == null) { f.UnreadableWhy = "position " + i + " would not return its centreline."; f.Bars.Clear(); return f; }
                f.Bars.Add(new OverlapBar { Position = i, Points = pts });
            }
            return f;
        }

        /// <summary>
        /// The bar sets a coupler at this end joins to the set: a coupler is an element of the model that links two
        /// bars end to end, so they meet at it by construction. Unreadable is simply no partner - the pair is then
        /// measured like any other.
        /// </summary>
        private static void Coupled(Document doc, Rebar bar, int end, ReviewSetFacts f)
        {
            try
            {
                ElementId cid = bar.GetCouplerId(end);
                var coupler = cid == null || cid == ElementId.InvalidElementId ? null : doc.GetElement(cid) as RebarCoupler;
                if (coupler == null) return;
                foreach (ReinforcementData d in coupler.GetCoupledReinforcementData())
                {
                    var rd = d as RebarReinforcementData;
                    if (rd == null) continue;
                    long other = Rid.Value(rd.RebarId);
                    if (other != f.Id && !f.CoupledTo.Contains(other)) f.CoupledTo.Add(other);
                }
            }
            catch { }
        }

        /// <summary>The bars of the area and path systems a host carries, or an empty list when Revit would not say.</summary>
        public static List<long> SystemBarsIn(Element host)
        {
            var ids = new List<long>();
            RebarHostData data = null;
            try { data = RebarHostData.GetRebarHostData(host); } catch { }
            if (data == null) return ids;
            using (data)
            {
                try
                {
                    foreach (AreaReinforcement a in data.GetAreaReinforcementsInHost())
                        foreach (ElementId id in a.GetRebarInSystemIds()) ids.Add(Rid.Value(id));
                    foreach (PathReinforcement p in data.GetPathReinforcementsInHost())
                        foreach (ElementId id in p.GetRebarInSystemIds()) ids.Add(Rid.Value(id));
                }
                catch { }
            }
            return ids;
        }

        private static void Host(Document doc, ElementId host, ReviewSetFacts f, Dictionary<long, string> categories)
        {
            if (host == null || host == ElementId.InvalidElementId) return;
            f.HostId = Rid.Value(host);
            string cat;
            if (categories != null && categories.TryGetValue(f.HostId, out cat)) { f.HostCategory = cat; return; }
            try
            {
                Element h = doc.GetElement(host);
                cat = h?.Category?.Name;
            }
            catch { cat = null; }
            f.HostCategory = cat;
            if (categories != null) categories[f.HostId] = cat;
        }

        private static string Style(Document doc, ElementId shapeId)
        {
            try
            {
                var shape = shapeId == null || shapeId == ElementId.InvalidElementId ? null : doc.GetElement(shapeId) as RebarShape;
                if (shape == null) return null;
                return shape.RebarStyle == RebarStyle.StirrupTie ? StructuralStyle.StirrupTie : StructuralStyle.Standard;
            }
            catch { return null; }
        }

        private static double? Diameter(Document doc, ElementId typeId)
        {
            try
            {
                var t = doc.GetElement(typeId) as RebarBarType;
                if (t == null) return null;
                double d = t.BarModelDiameter * FtToMm;
                return d > 0 && !double.IsNaN(d) ? (double?)d : null;
            }
            catch { return null; }
        }

        private static List<double[]> Tessellate(IList<Curve> curves)
        {
            if (curves == null || curves.Count == 0) return null;
            var pts = new List<double[]>();
            double[] previous = null;
            foreach (Curve c in curves)
            {
                IList<XYZ> tess;
                try { tess = c.Tessellate(); } catch { return null; }
                if (tess == null) return null;
                foreach (XYZ q in tess)
                {
                    var v = new[] { q.X * FtToMm, q.Y * FtToMm, q.Z * FtToMm };
                    if (previous != null && Math.Abs(v[0] - previous[0]) < 1e-6 &&
                        Math.Abs(v[1] - previous[1]) < 1e-6 && Math.Abs(v[2] - previous[2]) < 1e-6) continue;
                    pts.Add(v);
                    previous = v;
                }
            }
            return pts.Count >= 2 ? pts : null;
        }
    }
}
