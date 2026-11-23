// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_annotate, backlog P4 (field feedback 2026-11-17): level planes as
// references, NewSpotElevation's silent conditions checked at plan time, and the
// paper facts read off every dimension (will it print, what does it print, which
// requested reference did Revit drop, does a pattern joint fall outside its face).
// The rules are in Core/DimensionPaperRules.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        /// <summary>'level:&lt;id&gt;' becomes the stable representation of Level.GetPlaneReference().</summary>
        private static string ResolveLevelToken(Document doc, string text, string field)
        {
            long id;
            if (!DimensionLinkRules.TryParseLevelToken(text, out id)) return text;
            Level level = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as Level : null;
            if (level == null)
                throw new ArgumentException(field + ": '" + text + "' names no Level of this document. 'level:<id>' " +
                    "takes a HOST level; a linked level comes from horizun_get_dimension_references linked_targets. " +
                    "Nothing was written.");
            Reference plane = level.GetPlaneReference();
            if (plane == null)
                throw new ArgumentException(field + ": level " + id + " returned no plane reference. Nothing was written.");
            return plane.ConvertToStableRepresentation(doc);
        }

        /// <summary>
        /// NewSpotElevation fails when the point is on the edge of the face, off the
        /// face, or where the view does not see it (beyond a section's far clip, below
        /// a plan's view depth). Checked here, on host planar faces, so the refusal says
        /// what to move; anything this cannot measure is left to the rehearsal, whose
        /// failure message then names the same two causes.
        /// </summary>
        private static void SpotElevationPrecheck(Document doc, Plan p)
        {
            PlanarFace face;
            XYZ q = SpotPointOnFace(doc, p, out face);
            if (q == null) return;
            XYZ vd = p.View.ViewDirection.Normalize();

            bool? inside = null; double? toEdge = null;
            try
            {
                // q lies on the face's plane, so its projection onto the FACE moves it only
                // when it is outside the boundary: a distance over half a millimetre is "off
                // the face". No projection at all is "could not measure", never a refusal.
                // Face.Project answering null is not proof of "off the face": unknown, no
                // pre-check, the rehearsal decides (SpotElevationRules.InsideFromProjection).
                IntersectionResult onFace = face.Project(q);
                inside = SpotElevationRules.InsideFromProjection(onFace != null, onFace?.Distance ?? 0, 0.5 / 304.8);
                double best = double.MaxValue;
                foreach (EdgeArray loop in face.EdgeLoops)
                    foreach (Edge e in loop)
                    {
                        IntersectionResult hit = e.AsCurve()?.Project(q);
                        if (hit != null && hit.Distance < best) best = hit.Distance;
                    }
                if (best < double.MaxValue) toEdge = best;
            }
            catch { inside = null; toEdge = null; }
            string edgeError = SpotElevationRules.EdgeError(p.Index, inside, toEdge);
            if (edgeError != null) throw new ArgumentException(edgeError);

            if (p.View is ViewSection)
            {
                double depth = p.View.Origin.Subtract(q).DotProduct(vd);
                double? far = null;
                try
                {
                    Parameter clip = p.View.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                    if (clip != null && clip.AsInteger() != 0)
                        far = p.View.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)?.AsDouble();
                }
                catch { far = null; }
                string depthError = SpotElevationRules.DepthError(p.Index, "section", depth, 0.0, far);
                if (depthError != null) throw new ArgumentException(depthError);
            }
            else if (p.View is ViewPlan plan)
            {
                // A ceiling plan looks UP: its band runs from the cut plane to the view
                // depth. Direction unknown or planes contradicting it: no pre-check, the
                // rehearsal decides.
                bool? looksUp = null;
                try { looksUp = SpotElevationRules.LooksUp(plan.ViewType == ViewType.CeilingPlan, plan.ViewDirection.Z); }
                catch { looksUp = null; }
                double? min, max;
                if (SpotElevationRules.PlanBand(looksUp,
                        PlaneElevation(doc, plan, PlanViewPlane.CutPlane),
                        PlaneElevation(doc, plan, PlanViewPlane.TopClipPlane),
                        PlaneElevation(doc, plan, PlanViewPlane.ViewDepthPlane), out min, out max))
                {
                    string depthError = SpotElevationRules.DepthError(p.Index, "plan", q.Z, min, max);
                    if (depthError != null) throw new ArgumentException(depthError);
                }
            }
        }

        /// <summary>
        /// Where the spot lands: the requested point carried along the view direction onto
        /// the plane of a HOST planar face. Null (not measured) for a link, a family
        /// instance or a non-planar face.
        /// </summary>
        private static XYZ SpotPointOnFace(Document doc, Plan p, out PlanarFace face)
        {
            face = null;
            if (p.SpotOrigin == null || p.RefList.Count == 0 || p.RefLinks.Count == 0 || p.RefLinks[0] != null)
                return null;
            Element owner = null; GeometryObject g = null;
            try { owner = doc.GetElement(p.RefList[0]); g = owner?.GetGeometryObjectFromReference(p.RefList[0]); }
            catch { return null; }
            face = g as PlanarFace;
            if (face == null || owner is FamilyInstance) { face = null; return null; }
            XYZ point = p.SpotOrigin;
            XYZ n = face.FaceNormal.Normalize();
            XYZ vd = p.View.ViewDirection.Normalize();
            double denom = vd.DotProduct(n);
            return Math.Abs(denom) > 1e-6
                ? point.Add(vd.Multiply(face.Origin.Subtract(point).DotProduct(n) / denom))
                : point.Subtract(n.Multiply(point.Subtract(face.Origin).DotProduct(n)));
        }

        /// <summary>The elevation of one view-range plane, or null when unlimited or unreadable.</summary>
        private static double? PlaneElevation(Document doc, ViewPlan plan, PlanViewPlane which)
        {
            try
            {
                PlanViewRange range = plan.GetViewRange();
                ElementId levelId = range.GetLevelId(which);
                var level = levelId == null ? null : doc.GetElement(levelId) as Level;
                if (level == null) return null;
                return level.ProjectElevation + range.GetOffset(which);
            }
            catch { return null; }
        }

        /// <summary>The paper facts of one dimension: printed text and reference visibility.</summary>
        private static void ReadPaperFacts(Document doc, Plan p, Dimension dim, Measured m)
        {
            XYZ spotPoint = null;
            if (dim is SpotDimension)
                try { PlanarFace unused; spotPoint = SpotPointOnFace(doc, p, out unused); } catch { spotPoint = null; }
            try
            {
                m.Printed = DimensionPaper.PrintedText(doc, dim, spotPoint,
                    spotPoint == null ? null : "requested_point_along_view_onto_face");
            }
            catch (Exception ex) { m.Printed = new JObject { ["printed_text"] = null, ["note"] = ex.Message }; }
            try { m.HasBox = dim.get_BoundingBox(p.View) != null; }
            catch (Exception ex) { m.PrintUnknown.Add("box: " + ex.Message); }
            var refs = new List<Reference>();
            try { foreach (Reference r in dim.References) refs.Add(r); }
            catch { refs = new List<Reference>(p.RefList); }
            if (refs.Count == 0) refs = new List<Reference>(p.RefList);
            DimensionPaper.ReferenceVisibility(doc, p.View, refs, m.HiddenRefs, m.PrintUnknown);
            var linePoints = p.Line == null ? null : new[] { p.Line.GetEndPoint(0), p.Line.GetEndPoint(1) };
            DimensionPaper.DimensionVisibility(doc, p.View, dim, linePoints, m.HiddenRefs, m.PrintUnknown);
        }

        /// <summary>
        /// Post-commit: every segment end on a pattern line must fall inside its face.
        /// Null (no pattern reference) leaves the measurement untouched.
        /// </summary>
        private static void ReadPatternExtent(Document doc, Dimension dim, Measured m)
        {
            if (m.RefReps == null || m.LineDirection == null) return;
            var faces = new List<string>();
            foreach (string rep in m.RefReps)
            {
                string face; int k;
                if (DimensionPatternRules.TryParse(rep, out face, out k)) faces.Add(face);
            }
            if (faces.Count == 0) return;
            XYZ dir = new XYZ(m.LineDirection[0], m.LineDirection[1], m.LineDirection[2]).Normalize();
            // The segment ends come from the committed dimension itself, BEFORE any face
            // extent: the joint check needs only them, and a family or linked face (no
            // extent to read) must not leave it unavailable.
            var origins = new List<double>(); var values = new List<double>();
            try
            {
                if (dim.NumberOfSegments > 1)
                    foreach (DimensionSegment s in dim.Segments)
                    {
                        double? v = s.Value;
                        if (!v.HasValue) { origins.Clear(); break; }
                        origins.Add(s.Origin.DotProduct(dir)); values.Add(v.Value);
                    }
                else if (dim.Value.HasValue) { origins.Add(dim.Origin.DotProduct(dir)); values.Add(dim.Value.Value); }
            }
            catch { origins.Clear(); }
            if (origins.Count == 0)
            {
                m.PatternExtent = new DimensionPatternRules.ExtentVerdict
                { Status = "unavailable", Reason = "segment positions unreadable" };
                return;
            }
            List<double> boundaries = DimensionPatternRules.BoundaryScalars(origins, values,
                DimensionGeometryRules.DefaultToleranceFeet);
            m.PatternBoundaries = boundaries;
            double min = double.MaxValue, max = double.MinValue;
            foreach (string face in faces)
            {
                double fmin, fmax; string why;
                if (!DimensionPaper.TryFaceExtent(doc, face, dir, out fmin, out fmax, out why))
                {
                    m.PatternExtent = new DimensionPatternRules.ExtentVerdict { Status = "unavailable", Reason = why };
                    return;
                }
                min = Math.Min(min, fmin); max = Math.Max(max, fmax);
            }
            var known = new List<double?>();
            if (m.RefGeometries != null)
                foreach (RefGeometry g in m.RefGeometries)
                {
                    string reason;
                    known.Add(DimensionGeometryRules.Coordinate(g, m.LineDirection, out reason));
                }
            m.PatternExtent = DimensionPatternRules.CheckBoundaries(boundaries, known, faces.Count, min, max,
                DimensionGeometryRules.DefaultToleranceFeet);
        }

        /// <summary>
        /// Post-commit: the joint each '/?' resolved to must be where a segment ends.
        /// verified | failed | unavailable, with the joints no boundary accounts for.
        /// </summary>
        private static JObject JointPositionCheck(Plan p, Measured m, out bool? match)
        {
            match = null;
            var read = new JObject();
            if (p.PatternResolutions == null || p.PatternResolutions.Count == 0) return null;
            if (m.PatternBoundaries == null || m.LineDirection == null)
            {
                read["status"] = "unavailable"; read["reason"] = "segment positions unreadable";
                return read;
            }
            XYZ dir = new XYZ(m.LineDirection[0], m.LineDirection[1], m.LineDirection[2]).Normalize();
            var scalars = new List<double>();
            foreach (JToken r in p.PatternResolutions)
            {
                var pt = r["joint_point_internal_feet"] as JArray;
                if (pt == null || pt.Count != 3)
                {
                    read["status"] = "unavailable"; read["reason"] = "a resolved joint has no readable position";
                    return read;
                }
                scalars.Add(new XYZ((double)pt[0], (double)pt[1], (double)pt[2]).DotProduct(dir));
            }
            List<int> missing;
            string status = DimensionPatternRules.JointCheck(m.PatternBoundaries, scalars,
                DimensionGeometryRules.DefaultToleranceFeet, out missing);
            read["status"] = status;
            read["basis"] = "segment ends of the committed dimension (no face extent needed)";
            if (status == "unavailable") { read["reason"] = "segment positions unreadable"; return read; }
            match = missing.Count == 0;
            read["missing_resolutions"] = new JArray(missing);
            if (missing.Count > 0)
                read["reason"] = "the committed dimension does not end on the joint(s) the search chose";
            return read;
        }

        /// <summary>The will-print verdict for a stage: the box only counts once committed.</summary>
        private static DimensionPrintRules.Verdict PrintVerdict(Measured m, bool committed)
            => DimensionPrintRules.Evaluate(committed ? m.HasBox : null, m.HiddenRefs, m.PrintUnknown);
    }
}
