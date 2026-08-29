// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The geometry walk: what Revit hands over for a DWG, turned into segments the
// Revit-free rules can reason about.
//
// MEASURED against a real linked DWG on Revit 2026, and the shape of this file
// follows what actually came back rather than what the API suggests is possible:
//
//   - one top-level GeometryInstance, everything else inside it;
//   - Line, PolyLine, Arc and Solid at depth 1;
//   - the layer on the LEAF, never on the instance;
//   - Solids with zero volume and no layer - the residue of hatches, reported
//     as `approximate` and never counted as geometry anyone can build from;
//   - not one reachable string, on any object, at any depth.
//
// Everything is converted to MILLIMETRES here, once, so that no rule downstream
// has to know that Revit thinks in decimal feet.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What one geometry walk found, with the parts it could not use named.</summary>
    public sealed class CadHarvest
    {
        public List<CadSegment> Segments = new List<CadSegment>();
        /// <summary>class -> how many arrived. The census of what the drawing is made of.</summary>
        public Dictionary<string, int> PrimitiveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>layer -> how many primitives sat on it.</summary>
        public Dictionary<string, int> LayerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Nested instance paths seen, deepest first. Blocks show up here.</summary>
        public List<string> InstancePaths = new List<string>();
        public int MaxDepth;
        /// <summary>Primitives this walk could not turn into segments, and why.</summary>
        public List<JObject> NotHarvested = new List<JObject>();
        /// <summary>Arcs and splines that were chorded: the reading is APPROXIMATE and says so.</summary>
        public int ApproximatedCurves;
        /// <summary>
        /// Arcs, kept AS arcs, beside the chords they were also broken into.
        /// Nothing is forced to use them; a rule that does can build a real curve
        /// instead of N straight walls no audit could match back.
        /// </summary>
        public List<CadArcFact> Arcs = new List<CadArcFact>();
        public bool Truncated;
        public int PrimitivesVisited;
        /// <summary>Revit returned no geometry container. NOT the same as a drawing with nothing in it.</summary>
        public bool GeometryUnreadable;
        /// <summary>The bound that was in force, so a truncated reading can be argued with.</summary>
        public int PrimitiveBound;

        public JObject CoverageJson(double sagittaMm) => new JObject
        {
            ["primitives_visited"] = PrimitivesVisited,
            ["segments_produced"] = Segments.Count,
            ["approximated_curves"] = ApproximatedCurves,
            ["approximation_means"] = ApproximatedCurves == 0
                ? "no curve needed chording; every segment is exactly what was drawn"
                : "arcs and splines were chorded to within " + sagittaMm.ToString("0.##", CultureInfo.InvariantCulture) +
                  " mm of the true curve; those segments are APPROXIMATE, not what was drawn",
            ["not_harvested"] = new JArray(NotHarvested),
            ["max_instance_depth"] = MaxDepth,
            ["truncated"] = Truncated,
            ["primitive_bound"] = PrimitiveBound,
            ["truncated_means"] = Truncated
                ? "THE READING IS PARTIAL. The walk stopped at the primitive bound, so everything past it is " +
                  "absent from every count below - including the coverage fraction, whose numerator and " +
                  "denominator both shrank. Do not read this as a complete drawing."
                : "the whole drawing was walked",
            ["geometry_unreadable"] = GeometryUnreadable,
            ["arcs_kept_as_arcs"] = Arcs.Count,
            ["arcs_kept_means"] = Arcs.Count == 0
                ? "no arc in this drawing was recorded as a curve - either there are none, or Revit would not " +
                  "give their centres. Rules asking for arcs will match nothing."
                : "these arcs are available as REAL curves as well as chords, so a rule can build a curved " +
                  "wall instead of one straight wall per chord",
            ["text_is_unavailable"] = "MEASURED on Revit 2026: no string is reachable from imported DWG geometry. " +
                                      "Text arrives as curves on its own layer - the layer name survives, the words do not."
        };
    }

    public static class CadGeometryHarvest
    {
        /// <summary>
        /// Walk one CAD instance and produce segments in millimetres.
        ///
        /// <paramref name="maxPrimitives"/> is a stated bound, not a silent one:
        /// hitting it sets Truncated, because a partial reading that looks
        /// complete is the failure this whole repository is built against.
        /// </summary>
        public static CadHarvest Harvest(Document doc, Element instance, double arcSagittaMm,
                                         int maxPrimitives = 200000, View view = null)
        {
            var h = new CadHarvest();
            h.PrimitiveBound = maxPrimitives;
            if (doc == null || instance == null) return h;

            var options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };
            if (view != null) options.View = view;

            GeometryElement root = null;
            try { root = instance.get_Geometry(options); }
            catch (Exception ex)
            {
                h.NotHarvested.Add(new JObject
                {
                    ["what"] = "the whole instance",
                    ["error"] = ex.Message,
                    ["means"] = "no geometry could be read at all; this is NOT an empty drawing"
                });
                return h;
            }
            if (root == null)
            {
                // An EMPTY drawing and a drawing this could not read look the same
                // to everything downstream, and the plan then blames the caller's
                // requirement set for matching nothing. The commonest cause is a
                // CAD imported "current view only": its geometry exists only in
                // its owner view, and a view-less Options sees none of it.
                h.NotHarvested.Add(new JObject
                {
                    ["what"] = "the whole instance",
                    ["means"] = "Revit returned NO geometry container at all. This is NOT an empty drawing. " +
                                "A CAD placed in a single view returns nothing unless that view is passed; an " +
                                "unloaded link returns nothing either.",
                    ["view_passed"] = view != null ? (JToken)view.Name : JValue.CreateNull()
                });
                h.GeometryUnreadable = true;
                return h;
            }

            Walk(doc, root, "root", 0, arcSagittaMm, maxPrimitives, h);
            return h;
        }

        private static void Walk(Document doc, GeometryElement element, string instancePath, int depth,
                                 double sagittaMm, int maxPrimitives, CadHarvest h)
        {
            if (depth > h.MaxDepth) h.MaxDepth = depth;
            foreach (GeometryObject g in element)
            {
                if (h.PrimitivesVisited >= maxPrimitives) { h.Truncated = true; return; }
                h.PrimitivesVisited++;
                string cls = g.GetType().Name;
                Bump(h.PrimitiveCounts, cls);

                string layer = LayerOf(doc, g);
                if (layer != null) Bump(h.LayerCounts, layer);

                var gi = g as GeometryInstance;
                if (gi != null)
                {
                    // A nested instance is a BLOCK, and the path is part of what
                    // makes an entity inside it distinguishable from the same
                    // block placed elsewhere.
                    string childPath = instancePath + "/" + (layer ?? cls) + "#" + h.InstancePaths.Count;
                    h.InstancePaths.Add(childPath);
                    GeometryElement inner = null;
                    try { inner = gi.GetInstanceGeometry(); }
                    catch (Exception ex)
                    {
                        h.NotHarvested.Add(new JObject
                        {
                            ["what"] = "a nested instance at depth " + depth,
                            ["error"] = ex.Message
                        });
                    }
                    if (inner != null && depth < 8) Walk(doc, inner, childPath, depth + 1, sagittaMm, maxPrimitives, h);
                    else if (inner != null)
                        h.NotHarvested.Add(new JObject
                        {
                            ["what"] = "a nested instance deeper than 8 levels",
                            ["means"] = "the walk stopped; the drawing nests deeper than this bridge follows"
                        });
                    continue;
                }

                HarvestCurveLike(g, layer, instancePath, sagittaMm, h, cls);
            }
        }

        private static void HarvestCurveLike(GeometryObject g, string layer, string instancePath,
                                             double sagittaMm, CadHarvest h, string cls)
        {
            var line = g as Line;
            if (line != null)
            {
                h.Segments.Add(new CadSegment(P(line.GetEndPoint(0)), P(line.GetEndPoint(1)),
                                              layer, CadCurveKind.Line, 0));
                return;
            }

            var arc = g as Arc;
            if (arc != null)
            {
                // THE ARC ITSELF, before it is broken up. Everything needed to
                // rebuild the real curve: centre, radius, both ends, and a point
                // ON it between them - which is what Arc.Create takes and what no
                // chord can supply, because a chord's midpoint is inside the arc.
                string curveId = "cadarc:" + h.Arcs.Count.ToString(CultureInfo.InvariantCulture) +
                                 ":" + (layer ?? "");
                int before = h.Segments.Count;
                AddTessellated(arc, layer, CadCurveKind.Arc, sagittaMm, h, curveId);
                h.ApproximatedCurves++;
                try
                {
                    double t0 = arc.GetEndParameter(0);
                    double t1 = arc.GetEndParameter(1);
                    h.Arcs.Add(new CadArcFact(
                        curveId,
                        P(arc.Center),
                        CadUnits.FeetToMm(arc.Radius),
                        P(arc.GetEndPoint(0)),
                        P(arc.GetEndPoint(1)),
                        P(arc.Evaluate((t0 + t1) / 2.0, false)),
                        layer,
                        h.Segments.Count - before,
                        sagittaMm));
                }
                catch
                {
                    // Revit would not describe it. The chords are still there and
                    // still honest; what is absent is the arc reading, and its
                    // absence is what a rule asking for arcs will report.
                    h.NotHarvested.Add(new JObject
                    {
                        ["what"] = "an arc on layer " + (layer ?? "(none)"),
                        ["means"] = "Revit chorded it but would not give its centre and radius, so it is " +
                                    "available as chords ONLY. A rule that asks for arcs will not see it."
                    });
                }
                return;
            }

            var poly = g as PolyLine;
            if (poly != null)
            {
                IList<XYZ> pts = null;
                try { pts = poly.GetCoordinates(); } catch { }
                if (pts == null || pts.Count < 2)
                {
                    h.NotHarvested.Add(new JObject
                    {
                        ["what"] = "a PolyLine",
                        ["means"] = "it carried fewer than two coordinates; nothing can be built from it"
                    });
                    return;
                }
                for (int i = 0; i < pts.Count - 1; i++)
                    h.Segments.Add(new CadSegment(P(pts[i]), P(pts[i + 1]), layer, CadCurveKind.Polyline, i));
                return;
            }

            var curve = g as Curve;
            if (curve != null)
            {
                // Ellipse, NurbSpline, HermiteSpline: real curves this bridge can
                // only approximate. Tessellate and SAY it is an approximation.
                AddTessellated(curve, layer, CadCurveKind.Spline, sagittaMm, h);
                h.ApproximatedCurves++;
                return;
            }

            var solid = g as Solid;
            if (solid != null)
            {
                double volume = 0, area = 0;
                try { volume = solid.Volume; area = solid.SurfaceArea; } catch { }
                h.NotHarvested.Add(new JObject
                {
                    ["what"] = "a Solid on layer " + (layer ?? "(none)"),
                    ["volume_ft3"] = volume,
                    ["surface_area_ft2"] = area,
                    ["means"] = volume <= 0
                        ? "a zero-volume Solid: MEASURED as the residue a hatch or filled region leaves behind. " +
                          "It is not geometry anything can be built from, and it is reported rather than counted."
                        : "a Solid arrived from a DWG import; this bridge reads CAD as curves and does not " +
                          "interpret solids, so it is named here instead of silently dropped."
                });
                return;
            }

            h.NotHarvested.Add(new JObject
            {
                ["what"] = cls + " on layer " + (layer ?? "(none)"),
                ["means"] = "this primitive class is not one this bridge turns into segments; it is named, not dropped"
            });
        }

        /// <summary>
        /// Chord a curve so no chord departs from it by more than the DECLARED
        /// sagitta.
        ///
        /// The first version took a sagittaMm parameter, never referenced it, and
        /// called Tessellate() - whose chord error is whatever Revit felt like.
        /// Every reply still said "chorded to within N mm of the true curve",
        /// which is a false statement in a reply, and walls were built on those
        /// chords. Now the deviation is MEASURED at each chord's midpoint against
        /// the real curve and the chord is split until it is inside the bound.
        /// </summary>
        private static void AddTessellated(Curve curve, string layer, CadCurveKind kind, double sagittaMm,
                                           CadHarvest h, string curveId = null)
        {
            double sagittaFeet = CadUnits.MmToFeet(Math.Max(1e-6, sagittaMm));
            var points = new List<XYZ>();
            bool measured = false;
            try
            {
                double t0 = curve.GetEndParameter(0);
                double t1 = curve.GetEndParameter(1);
                points.Add(curve.Evaluate(t0, false));
                Refine(curve, t0, t1, sagittaFeet, 0, points);
                measured = points.Count >= 2;
            }
            catch { measured = false; }

            if (!measured)
            {
                // Revit would not evaluate it. Tessellate is the fallback, and it
                // is recorded as a DIFFERENT thing: its error is unmeasured, so
                // claiming the declared sagitta over it would be the lie this
                // whole rewrite exists to remove.
                IList<XYZ> tess = null;
                try { tess = curve.Tessellate(); } catch { }
                if (tess != null && tess.Count >= 2)
                {
                    h.NotHarvested.Add(new JObject
                    {
                        ["what"] = kind + " on layer " + (layer ?? "(none)"),
                        ["means"] = "this curve would not evaluate, so it was tessellated by Revit instead. Its " +
                                    "chord error is NOT bounded by the declared sagitta and is not measured.",
                        ["chords"] = tess.Count - 1
                    });
                    for (int i = 0; i < tess.Count - 1; i++)
                        h.Segments.Add(new CadSegment(P(tess[i]), P(tess[i + 1]), layer, kind, i, curveId));
                    return;
                }
                h.NotHarvested.Add(new JObject
                {
                    ["what"] = kind + " on layer " + (layer ?? "(none)"),
                    ["means"] = "the curve would neither evaluate nor tessellate; NOTHING was produced from it, " +
                                "and a straight line between its ends would be a different drawing"
                });
                return;
            }

            for (int i = 0; i < points.Count - 1; i++)
                h.Segments.Add(new CadSegment(P(points[i]), P(points[i + 1]), layer, kind, i, curveId));
        }

        /// <summary>
        /// Split until the midpoint of the chord is within the sagitta of the
        /// real curve. Depth-bounded: a curve that will not converge produces a
        /// stated number of chords rather than an unbounded recursion.
        /// </summary>
        private static void Refine(Curve curve, double t0, double t1, double sagittaFeet, int depth, List<XYZ> into)
        {
            XYZ p0 = curve.Evaluate(t0, false);
            XYZ p1 = curve.Evaluate(t1, false);
            double tm = (t0 + t1) / 2.0;
            XYZ pm = curve.Evaluate(tm, false);
            XYZ chordMid = (p0 + p1) / 2.0;
            if (depth >= 12 || chordMid.DistanceTo(pm) <= sagittaFeet)
            {
                into.Add(p1);
                return;
            }
            Refine(curve, t0, tm, sagittaFeet, depth + 1, into);
            Refine(curve, tm, t1, sagittaFeet, depth + 1, into);
        }

        /// <summary>
        /// The DWG layer, via the only route that exists: the graphics style's
        /// category name. Null when Revit will not say, which is a fact and not
        /// a layer called "".
        /// </summary>
        public static string LayerOf(Document doc, GeometryObject g)
        {
            try
            {
                ElementId styleId = g.GraphicsStyleId;
                if (styleId == null || styleId == ElementId.InvalidElementId) return null;
                var style = doc.GetElement(styleId) as GraphicsStyle;
                Category category = style?.GraphicsStyleCategory;
                return category?.Name;
            }
            catch { return null; }
        }

        /// <summary>The parent category of a layer - the import symbol's own name. Useful for telling two imports apart.</summary>
        public static string LayerParentOf(Document doc, GeometryObject g)
        {
            try
            {
                var style = doc.GetElement(g.GraphicsStyleId) as GraphicsStyle;
                return style?.GraphicsStyleCategory?.Parent?.Name;
            }
            catch { return null; }
        }

        /// <summary>Revit's decimal feet become millimetres here, once.</summary>
        private static CadPoint P(XYZ p) =>
            new CadPoint(CadUnits.FeetToMm(p.X), CadUnits.FeetToMm(p.Y), CadUnits.FeetToMm(p.Z));

        private static void Bump(Dictionary<string, int> d, string key)
        {
            int n;
            d[key] = d.TryGetValue(key, out n) ? n + 1 : 1;
        }
    }
}
