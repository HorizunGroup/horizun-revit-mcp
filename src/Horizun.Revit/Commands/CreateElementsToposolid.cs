// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_create_elements, kind='toposolid'. Original Horizun code.
//
// Toposolid.Create(doc, points, typeId, levelId) - the points ARE the top surface,
// triangulated by Revit. The type and the level are the caller's (a toposolid type is
// never "the first one"; the refusal lists the document's types by name and id). The
// points come inline (at most 100, internal coordinates) or from a LandXML TIN the
// caller exported (landxml_path, shared coordinates: CreateElementsLandXml.cs).
//
// VERIFIED by re-reading the committed solid, not the call that did not throw: at up to
// ToposolidRules.MaxSamples input points (every point when there are fewer; otherwise the
// lowest, the highest and an even spread, each named in the postconditions) the top of
// the solid at that X,Y must stand at the point's Z within 1 mm. The height is read from
// the solid's own vertices at that plan point (the highest one is the top face) and, when
// Revit merged the point into a flat face and left no vertex there, from a vertical line
// through the faces. An X,Y where neither finds the solid is UNMEASURED, and the row fails.
//
// NOT MEASURED YET: whether Toposolid.Create reads a point's Z as absolute or relative to
// the level. The absolute reading is asserted; a Revit that reads it the other way fails
// the row (it is rolled back) instead of passing - the live probe stages its own level
// ABOVE zero precisely so the two readings differ.
//
// Revit 2023 has no Toposolid (it arrived in 2024): the row is refused by name there,
// and a TopographySurface - a different element - is not offered in its place.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        /// <summary>A solid vertex this close in plan (~0.3 mm) IS the input point.</summary>
        private const double TopoXyMatchFeet = 1e-3;
        /// <summary>1 mm: the top must stand at the point's Z within this.</summary>
        private const double TopoZToleranceFeet = 1.0 / 304.8;
        internal const string ToposolidNotIn2023 = "toposolid_not_in_revit_2023";

        private static void PlanToposolid(Document doc, JObject item, Plan p, double scale)
        {
#if REVIT2023
            p.TopoPoints = null; p.TopoSamples = null; p.TopoSource = null;   // nothing to plan here; the fields exist in every year's build
            throw new ArgumentException(ToposolidNotIn2023 + ": Revit 2023 has no Toposolid element (it arrived in Revit 2024). " +
                "A TopographySurface is a different element and this tool does not create one in its place. Nothing was planned.");
#else
            p.Level = Need<Level>(doc, item, "level_id");
            if (item["type_id"] == null)
                throw new ArgumentException("type_id is required for toposolid - the type is never guessed. Toposolid types here: " + ToposolidTypeNames(doc));
            p.Type = Optional<ToposolidType>(doc, item, "type_id");
            JArray raw = item["points"] as JArray;
            string landXml = item.Value<string>("landxml_path");
            if ((raw == null) == (landXml == null))
                throw new ArgumentException("toposolid takes points ([[x, y, z], ...] in the request's units, internal coordinates) " +
                                            "OR landxml_path (a LandXML TIN in shared coordinates) - exactly one of the two.");
            List<double[]> pts;
            List<string> ids = null;
            if (landXml != null) pts = ToposolidFromLandXml(doc, landXml, p, out ids);
            else
            {
                pts = new List<double[]>(raw.Count);
                foreach (JToken t in raw) { XYZ q = Point(t, scale, true); pts.Add(new[] { q.X, q.Y, q.Z }); }
            }
            string bad = ToposolidRules.ValidatePoints(pts, TopoXyMatchFeet, ids == null ? ToposolidRules.MaxPoints : LandXmlTinRules.MaxFilePoints);
            // A file's points are named by the file's own ids, not by a position the caller never wrote.
            if (bad != null && ids != null)
                bad = "landxml_path: " + System.Text.RegularExpressions.Regex.Replace(bad, @"points\[(\d+)\]",
                    m => "point '" + ids[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] + "'");
            if (bad != null) throw new ArgumentException(bad);
            p.TopoPoints = pts.Select(a => new XYZ(a[0], a[1], a[2])).ToList();
            p.TopoSamples = ToposolidRules.SampleIndices(pts);
            if (ids != null) p.TopoSource["sampled_point_ids"] = new JArray(p.TopoSamples.Select(k => ids[k]));
#endif
        }

#if !REVIT2023
        private static string ToposolidTypeNames(Document doc)
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).Cast<ToposolidType>()
                .OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => "'" + t.Name + "' (id " + Rid.Value(t.Id) + ")").ToList();
            return names.Count == 0 ? "none - copy one in first (horizun_copy_between_documents (Pro edition))." : string.Join(", ", names.Take(20));
        }
#endif

        /// <summary>Create half, inside the batch transaction.</summary>
        private static Element CreateToposolid(Document doc, Plan p)
        {
#if REVIT2023
            throw new InvalidOperationException(ToposolidNotIn2023);
#else
            Toposolid made = Toposolid.Create(doc, p.TopoPoints, p.Type.Id, p.Level.Id);
            if (made == null) throw new InvalidOperationException("Revit returned no toposolid for those points. Nothing was kept.");
            // The solid is what the re-read measures; a new element's geometry is not there before a regeneration.
            doc.Regenerate();
            return made;
#endif
        }

        private static bool IsToposolid(Element e)
        {
#if REVIT2023
            return false;
#else
            return e is Toposolid;
#endif
        }

        private static IEnumerable<Solid> TopoSolidsOf(GeometryElement g)
        {
            if (g == null) yield break;
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Faces.Size > 0) yield return s;
                else if (o is GeometryInstance gi) foreach (Solid inner in TopoSolidsOf(gi.GetInstanceGeometry())) yield return inner;
            }
        }

        /// <summary>Every edge end of the committed solid, in model coordinates - read once per row.</summary>
        private static List<XYZ> TopoVertices(Element e)
        {
            var list = new List<XYZ>();
            if (e == null) return list;
            foreach (Solid s in TopoSolidsOf(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })))
                foreach (Edge edge in s.Edges)
                {
                    Curve c = edge.AsCurve();
                    list.Add(c.GetEndPoint(0)); list.Add(c.GetEndPoint(1));
                }
            return list;
        }

        /// <summary>The top of the solid at that plan point, or NaN when nothing of it is found there (unmeasured).</summary>
        private static double TopoZAt(Element e, List<XYZ> vertices, XYZ at)
        {
            double best = double.NaN;
            foreach (XYZ v in vertices)
                if (Math.Abs(v.X - at.X) <= TopoXyMatchFeet && Math.Abs(v.Y - at.Y) <= TopoXyMatchFeet && (double.IsNaN(best) || v.Z > best)) best = v.Z;
            if (!double.IsNaN(best) || e == null) return best;
            // No vertex there: the point was merged into a flat face. A vertical line meets it in its interior.
            BoundingBoxXYZ box = e.get_BoundingBox(null);
            if (box == null) return best;
            Line ray = Line.CreateBound(new XYZ(at.X, at.Y, box.Min.Z - 1), new XYZ(at.X, at.Y, box.Max.Z + 1));
            foreach (Solid s in TopoSolidsOf(e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })))
                foreach (Face f in s.Faces)
                {
                    IntersectionResultArray hits;
                    if (f.Intersect(ray, out hits) != SetComparisonResult.Overlap || hits == null) continue;
                    foreach (IntersectionResult h in hits) if (double.IsNaN(best) || h.XYZPoint.Z > best) best = h.XYZPoint.Z;
                }
            return best;
        }
    }
}
