// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing, spec.ceiling.method = 'curtain': the Revit half.
// Original Horizun code.
//
// WHAT IT BUILDS. Each layer of the suspended ceiling's framing (mains, furring, the
// perimeter angle as the border mullion) is a FLAT footprint roof of the caller's
// Sloped Glazing type over the ceiling's own sketch loops, its plane at the ceiling's
// top face + offset_mm; the type's grid and mullions ARE the members. Optional hangers
// are vertical Curtain Walls of the caller's type along lines parallel to the first
// layer's grid 1 direction (Core/CurtainFramingRules.PlanCeiling), from the top layer's
// plane up to the structure above; the type's vertical grid places the rods.
//
// THE LAYERS. Document.Create.NewFootPrintRoof over the sketch curves moved down to the
// level, every footprint edge DefinesSlope = false, ROOF_LEVEL_OFFSET_PARAM = plane -
// level, and CURTAINGRID_ANGLE_1 = angle_deg when given (probed while planning, in a
// rolled-back transaction: a type whose roofs cannot take it refuses by name for layer 0
// with hangers - they are planned parallel to it - and skips the angle, named, elsewhere).
// Openings the ceiling hosts, and shafts, are NOT cut from the layers (the footprint is
// the ceiling's own sketch); the plan says so and the hanger lines avoid them.
//
// THE HANGERS. Three rays per line (both ends inset, and the middle) from the top
// layer's plane, the member method's rolled-back ray view (FramingCeiling.cs): a line
// with no support within max_length_mm, or whose support is not level along it (the
// three rods differ by more than 1 mm), is NAMED in not_built and never built.
//
// VERIFICATION re-reads the committed model: each piece by marker, its type; a layer's
// plane from its base level and offset (1 mm), its footprint against the ceiling's
// sketch both ways (1 mm), no slope-defining edge, its grid 1 lines' plan direction
// against angle_deg (0.1 deg; which reference Revit measures the angle from is measured
// live), each fixed-distance grid's spacing; a hanger's line, base and top, its own
// grid and mullions (ReadCurtainWallGrid), and one ray up from under its top at every
// station: the first support must be there within 1 mm.
//
// REVIT API, confirmed in RevitAPI.xml 2023 and 2026 (identical entries):
//   Creation.Document.NewFootPrintRoof(CurveArray, Level, RoofType, out ModelCurveArray)
//   FootPrintRoof.DefinesSlope(ModelCurve) (get/set); FootPrintRoof.GetProfiles();
//   FootPrintRoof.CurtainGrids; CurtainGrid.GetUGridLineIds / GetVGridLineIds;
//   BuiltInParameter ROOF_BASE_LEVEL_PARAM, ROOF_LEVEL_OFFSET_PARAM, CURTAINGRID_ANGLE_1,
//   SPACING_LAYOUT_1/2, SPACING_LENGTH_1/2 (the sloped glazing type's grid 1 / grid 2).
// SPACING_LAYOUT_1 = 1 is read as Fixed Distance, as SPACING_LAYOUT_VERT is for walls
// (FramingCurtain.cs); its value string is reported beside it for the live probe.
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
    /// <summary>The curtain ceiling's resolved state for one ceiling (the members are on the FramingSourcePlan).</summary>
    internal sealed class CurtainCeilingState
    {
        public CurtainCeilingFramingSpec Spec;
        /// <summary>The ceiling's own sketch loops (the layers' footprint), in model coordinates.</summary>
        public readonly List<List<Curve>> SketchLoops = new List<List<Curve>>();
        /// <summary>How many of FramedCeiling.LoopsMm are the sketch's (the rest are hosted openings and shafts).</summary>
        public int SketchLoopCount;
        public double HangerBaseMm, HangerAngleRad;
        /// <summary>Hanger lines not built, each with its reason.</summary>
        public readonly JArray NotBuilt = new JArray();
    }

    public sealed partial class FramingCommand
    {
        // ---- planning -----------------------------------------------------------------------

        private static List<FramingSourcePlan> PlanCurtainCeilings(Document doc, JObject request, CurtainCeilingFramingSpec spec, string specHash, List<string> skipped)
        {
            for (int i = 0; i < spec.Layers.Count; i++)
            {
                long id = spec.Layers[i].TypeId;
                string path = "spec.ceiling.layers[" + i + "].type_id " + id;
                if (!(RestoreLookup(doc, id) is RoofType rt)) throw new ArgumentException(path + " is not a roof type of this document.");
                if (rt.get_Parameter(BuiltInParameter.SPACING_LAYOUT_1) == null) throw new ArgumentException(path + " is a roof type with no curtain grid layout (not a Sloped Glazing type).");
            }
            if (spec.HangerTypeId.HasValue && !(RestoreLookup(doc, spec.HangerTypeId.Value) is WallType ht && ht.Kind == WallKind.Curtain))
                throw new ArgumentException("spec.ceiling.hanger.type_id " + spec.HangerTypeId.Value + " is not a Curtain Wall type of this document.");
            // The hangers follow layer 0's grid 1 LINES, so layer 0 must have a grid 1.
            if (spec.HangerTypeId.HasValue && spec.Layers.Count > 0 && RestoreLookup(doc, spec.Layers[0].TypeId) is RoofType first0 &&
                (first0.get_Parameter(BuiltInParameter.SPACING_LAYOUT_1)?.AsInteger() ?? 0) == 0)
                throw new ArgumentException("spec.ceiling.layers[0].type_id " + spec.Layers[0].TypeId + " has no grid 1 (its layout is None), and the hanger lines " +
                                            "follow layer 0's grid 1 lines: put the layer whose members sit on grid 1 first, or leave the hangers out.");

            bool viewScope = request["view_id"] != null && SourceIds(request) == null;
            var plans = new List<FramingSourcePlan>();
            foreach (Ceiling ceiling in Sources<Ceiling>(doc, request, "ceiling"))
            {
                long sid = Rid.Value(ceiling.Id);
                FramedCeiling fc = ReadCeiling(doc, ceiling, out string refusal);
                if (fc == null)
                {
                    if (!viewScope) throw new ArgumentException(refusal);
                    skipped.Add(refusal);
                    continue;
                }
                var st = new CurtainCeilingState { Spec = spec, SketchLoopCount = fc.LoopsMm.Count - fc.HoleIds.Count };
                foreach (CurveArray arr in ((Sketch)doc.GetElement(ceiling.SketchId)).Profile)
                {
                    var loop = new List<Curve>();
                    foreach (Curve c in arr) loop.Add(c);
                    if (loop.Count > 0) st.SketchLoops.Add(loop);
                }
                // MEASURED 2026-09-27 (Revit 2026, flat sloped glazing roofs): grid 1 is the V lines and
                // they run at CURTAINGRID_ANGLE_1 + 90 deg in project coordinates - the angle names the
                // direction the lines are SPACED along - while grid 2 (the U lines) runs AT
                // CURTAINGRID_ANGLE_2; neither follows the footprint's edges. The hangers run along
                // layer 0's grid 1 LINES, under its members.
                double angleRad = (spec.Layers[0].AngleDeg ?? 0) * Math.PI / 180;
                double lineRad = angleRad + Math.PI / 2;
                CurtainCeilingPlan plan = CurtainFramingRules.PlanCeiling(fc.LoopsMm, spec, lineRad, MaxMembersPerSource);
                if (!string.IsNullOrEmpty(plan.Refusal)) throw new ArgumentException("ceiling " + sid + ": " + plan.Refusal);
                var p = new FramingSourcePlan { Source = ceiling, Operation = "ceiling", Ceiling = fc, SpecHash = specHash, CurtainCeiling = st };
                p.Warnings.AddRange(plan.Warnings);
                if (fc.HoleIds.Count > 0)
                    p.Warnings.Add("ceiling " + sid + ": openings " + string.Join(", ", fc.HoleIds) + " are not cut from the layer roofs (their footprint is the ceiling's own sketch); the hanger lines avoid them");
                for (int i = 0; i < spec.Layers.Count; i++)
                {
                    CurtainLayerSpec l = spec.Layers[i];
                    double z = Math.Round(fc.TopMm + l.OffsetMm, 1);
                    // X0 carries the angle and Y0 whether one was given, so the signature binds both.
                    p.Members.Add(new FramingMember { Role = CurtainFramingRoles.Layer, TypeKey = WallFramingSpec.Key(l.TypeId), Source = i, X0 = l.AngleDeg ?? 0, Y0 = l.AngleDeg.HasValue ? 1 : 0, Z0 = z, Z1 = z });
                }
                st.HangerBaseMm = Math.Round(fc.TopMm + spec.TopOffsetMm, 1);
                st.HangerAngleRad = lineRad;
                foreach (double[] s in plan.HangerLines)
                    p.Members.Add(new FramingMember { Role = CurtainFramingRoles.Hanger, TypeKey = WallFramingSpec.Key(spec.HangerTypeId.Value), X0 = s[0], Y0 = s[1], X1 = s[2], Y1 = s[3], Z0 = st.HangerBaseMm, Z1 = st.HangerBaseMm });
                plans.Add(p);
            }
            if (plans.Count == 0)
                throw new ArgumentException("view " + request.Value<long?>("view_id") + " shows no ceiling this operation can frame (" + skipped.Count +
                                            " skipped: " + string.Join("; ", skipped.Take(5)) + ").");
            ProbeLayerAngles(doc, plans, spec);
            CastCurtainHangers(doc, plans, spec.HangerMaxLengthMm);
            int total = 0;
            foreach (FramingSourcePlan p in plans)
            {
                total += p.Members.Count;
                if (total > MaxCurtainPiecesTotal) throw new ArgumentException("the plan exceeds " + MaxCurtainPiecesTotal + " layer roofs and hanger walls; frame fewer ceilings per call or widen hanger.spacing_mm.");
                p.Signature = FramingPlanSignature.Of(p.Members);
                ClaimExisting(doc, p);
            }
            return plans;
        }

        /// <summary>
        /// Whether a roof of each angled layer's type takes its grid 1 angle, found in a rolled-back
        /// transaction on the first ceiling so the rehearsal knows before a token exists. Layer 0 with
        /// hangers refuses by name (the hangers run parallel to its grid); any other layer skips its
        /// angle - named in the plan's warnings, bound by the token (Y0 = -1), read as angle_skipped.
        /// </summary>
        private static void ProbeLayerAngles(Document doc, List<FramingSourcePlan> plans, CurtainCeilingFramingSpec spec)
        {
            if (!spec.Layers.Any(l => l.AngleDeg.HasValue)) return;
            FramingSourcePlan first = plans[0];
            var unsettable = new List<int>();
            using (var tx = new Transaction(doc, "Horizun: framing layer angle probe (rolled back)"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new ArgumentException("the layers' grid 1 angle could not be probed (no transaction could start).");
                try
                {
                    for (int i = 0; i < spec.Layers.Count; i++)
                    {
                        if (!spec.Layers[i].AngleDeg.HasValue) continue;
                        FramingMember m = first.Members[i];
                        // Placed without its angle (PlaceLayerRoof would refuse), then the angle is tried.
                        FootPrintRoof roof = PlaceLayerRoof(doc, first.CurtainCeiling, first.Ceiling.Level, new FramingMember { Role = m.Role, TypeKey = m.TypeKey, Source = m.Source, Z0 = m.Z0, Z1 = m.Z1 }, i);
                        Parameter angle = roof.get_Parameter(BuiltInParameter.CURTAINGRID_ANGLE_1);
                        if (angle == null || angle.IsReadOnly || angle.StorageType != StorageType.Double || !angle.Set(spec.Layers[i].AngleDeg.Value * Math.PI / 180)) unsettable.Add(i);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                { throw new ArgumentException("probing the layers in a rolled-back transaction: " + ex.Message, ex); }
                finally { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); }
            }
            foreach (int i in unsettable)
            {
                string why = "layer " + i + " (type " + spec.Layers[i].TypeId + "): a roof of this type takes no grid 1 angle (CURTAINGRID_ANGLE_1 absent, read-only or refused)";
                if (i == 0 && spec.HangerTypeId.HasValue)
                    throw new ArgumentException(why + "; the hanger lines run parallel to layer 0's grid, so its angle must be settable - use a type whose grid 1 layout is not None, or leave the hangers out.");
                foreach (FramingSourcePlan p in plans)
                {
                    FramingMember m = p.Members[i];
                    p.Members[i] = new FramingMember { Role = m.Role, TypeKey = m.TypeKey, Source = m.Source, X0 = 0, Y0 = -1, Z0 = m.Z0, Z1 = m.Z1 };
                    p.Warnings.Add(why + ": angle_deg " + spec.Layers[i].AngleDeg.Value.ToString(CultureInfo.InvariantCulture) + " is skipped and the grid keeps the type's own orientation");
                }
            }
        }

        /// <summary>Each hanger line's rods to the structure above; lines with no level support leave the plan, named.</summary>
        private static void CastCurtainHangers(Document doc, List<FramingSourcePlan> plans, double maxLengthMm)
        {
            if (!plans.Any(p => p.Members.Any(m => m.Role == CurtainFramingRoles.Hanger))) return;
            using (var tx = new Transaction(doc, "Horizun: framing ray view (rolled back)"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the temporary ray view could not be created: no transaction could start.");
                try
                {
                    var views = new Dictionary<long, View3D>();
                    var filter = new ElementMulticategoryFilter(SupportCategories.Concat(new[] { BuiltInCategory.OST_RvtLinks }).ToList());
                    foreach (FramingSourcePlan p in plans)
                    {
                        ElementId phase = SourcePhase(doc, p.Source);
                        long pk = phase == null ? -1 : Rid.Value(phase);
                        if (!views.TryGetValue(pk, out View3D view)) views[pk] = view = CeilingRayView(doc, phase);
                        var ray = new ReferenceIntersector(filter, FindReferenceTarget.Face, view) { FindReferencesInRevitLinks = true };
                        var kept = new List<FramingMember>();
                        foreach (FramingMember m in p.Members)
                        {
                            if (m.Role != CurtainFramingRoles.Hanger) { kept.Add(m); continue; }
                            var rods = new List<double>();
                            string support = null;
                            foreach (double[] q in HangerStations(m))
                            {
                                double d = SupportAbove(doc, ray, new XYZ(q[0] / MmPerFt, q[1] / MmPerFt, m.Z0 / MmPerFt), maxLengthMm / MmPerFt, out string s);
                                if (s == null || d * MmPerFt < 1.0) { rods = null; break; }
                                rods.Add(d * MmPerFt);
                                support = support ?? s;
                            }
                            var line = new JArray(Math.Round(m.X0, 1), Math.Round(m.Y0, 1), Math.Round(m.X1, 1), Math.Round(m.Y1, 1));
                            if (rods == null)
                            { p.CurtainCeiling.NotBuilt.Add(new JObject { ["line_mm"] = line, ["reason"] = "no_support_above within " + maxLengthMm + " mm at one of its stations" }); continue; }
                            if (rods.Max() - rods.Min() > EndpointToleranceMm)
                            {
                                p.CurtainCeiling.NotBuilt.Add(new JObject { ["line_mm"] = line, ["reason"] = "support_not_level: the rods along it measure " + string.Join(", ", rods.Select(r => Math.Round(r, 1))) + " mm" });
                                continue;
                            }
                            m.Z1 = Math.Round(m.Z0 + rods.Min(), 1);
                            p.Ceiling.Supports[support] = (p.Ceiling.Supports.TryGetValue(support, out int n) ? n : 0) + 1;
                            kept.Add(m);
                        }
                        p.Members = kept;
                        if (p.CurtainCeiling.NotBuilt.Count > 0)
                            p.Warnings.Add("ceiling " + Rid.Value(p.Source.Id) + ": " + p.CurtainCeiling.NotBuilt.Count + " hanger line(s) are not built (not_built names each and why)");
                    }
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                }
            }
        }

        /// <summary>A hanger line's ray stations in plan mm: both ends inset by min(50 mm, a quarter), and the middle.</summary>
        private static List<double[]> HangerStations(FramingMember m)
        {
            double dx = m.X1 - m.X0, dy = m.Y1 - m.Y0, len = Math.Sqrt(dx * dx + dy * dy);
            double inset = Math.Min(50, len / 4);
            return new[] { inset, len / 2, len - inset }.Select(t => new[] { m.X0 + dx * t / len, m.Y0 + dy * t / len }).ToList();
        }

        // ---- writing ------------------------------------------------------------------------

        private static void PlaceCurtainCeilingSource(Document doc, FramingSourcePlan p)
        {
            Level level = p.Ceiling.Level;
            long sid = Rid.Value(p.Source.Id);
            string uid = p.Source.UniqueId;
            for (int i = 0; i < p.Members.Count; i++)
            {
                FramingMember m = p.Members[i];
                Element made = m.Role == CurtainFramingRoles.Layer ? (Element)PlaceLayerRoof(doc, p.CurtainCeiling, level, m, i) : PlaceHangerWall(doc, level, m, i);
                FramingMarker.Write(made, new FramingMark { SourceId = sid, SourceUniqueId = uid, Role = m.Role, Index = i, SpecHash = p.SpecHash, PlanSignature = p.Signature, Operation = "ceiling" });
                p.MemberIds[i] = Rid.Value(made.Id);
            }
        }

        private static FootPrintRoof PlaceLayerRoof(Document doc, CurtainCeilingState st, Level level, FramingMember m, int i)
        {
            var type = (RoofType)doc.GetElement(Rid.Make(long.Parse(m.TypeKey, CultureInfo.InvariantCulture)));
            var footprint = new CurveArray();
            foreach (List<Curve> loop in st.SketchLoops)
                foreach (Curve c in loop)
                    footprint.Append(c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, level.ProjectElevation - c.GetEndPoint(0).Z))));
            FootPrintRoof roof;
            // Created BEFORE the call although the parameter is `out`: MEASURED 2026-09-27 in Revit 2026,
            // NewFootPrintRoof reads the array it is handed and throws "Value cannot be null." on a null
            // one - for any roof type, with any view active. create_elements' roof kind always did this.
            ModelCurveArray edges = new ModelCurveArray();
            try { roof = doc.Create.NewFootPrintRoof(footprint, level, type, out edges); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            { throw new InvalidOperationException("layer " + i + " (type " + m.TypeKey + "): " + ex.Message, ex); }
            if (roof == null) throw new InvalidOperationException("layer " + i + ": Revit returned no roof.");
            // Flat: no footprint edge defines a slope.
            foreach (ModelCurve mc in edges) roof.set_DefinesSlope(mc, false);
            Parameter off = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
            if (off == null || off.IsReadOnly) throw new InvalidOperationException("layer " + i + ": the roof's base offset (ROOF_LEVEL_OFFSET_PARAM) cannot be set.");
            off.Set(m.Z0 / MmPerFt - level.ProjectElevation);
            if (m.Y0 > 0.5)
            {
                Parameter angle = roof.get_Parameter(BuiltInParameter.CURTAINGRID_ANGLE_1);
                if (angle == null || angle.IsReadOnly || angle.StorageType != StorageType.Double)
                    throw new InvalidOperationException("layer " + i + ": the roof exposes no settable grid 1 angle (CURTAINGRID_ANGLE_1); omit angle_deg for this layer.");
                angle.Set(m.X0 * Math.PI / 180);
            }
            return roof;
        }

        private static Wall PlaceHangerWall(Document doc, Level level, FramingMember m, int i)
        {
            double z = level.ProjectElevation;
            Line line = Line.CreateBound(new XYZ(m.X0 / MmPerFt, m.Y0 / MmPerFt, z), new XYZ(m.X1 / MmPerFt, m.Y1 / MmPerFt, z));
            Wall w;
            try { w = Wall.Create(doc, line, Rid.Make(long.Parse(m.TypeKey, CultureInfo.InvariantCulture)), level.Id, (m.Z1 - m.Z0) / MmPerFt, m.Z0 / MmPerFt - z, false, false); }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
            { throw new InvalidOperationException("hanger " + i + " (type " + m.TypeKey + ", " + Math.Round(m.Z0, 1).ToString(CultureInfo.InvariantCulture) + " -> " + Math.Round(m.Z1, 1).ToString(CultureInfo.InvariantCulture) + " mm): " + ex.Message, ex); }
            if (w == null) throw new InvalidOperationException("hanger " + i + ": Revit returned no wall.");
            WallUtils.DisallowWallJoinAtEnd(w, 0);
            WallUtils.DisallowWallJoinAtEnd(w, 1);
            return w;
        }

        // ---- verifying ----------------------------------------------------------------------

        private static PostconditionCheck VerifyCurtainCeilings(Document doc, List<FramingSourcePlan> plans, JObject evidence)
        {
            var check = new PostconditionCheck("curtain_member_count", "curtain_types", "layer_plane", "layer_footprint", "layer_flat", "grid_direction",
                                               "grid_spacing", "mullion_types", "hanger_location", "hanger_base_top", "hanger_reaches_support");
            int planned = 0, found = 0, wrongType = 0, slopeEdges = 0, directionProblems = 0, gridProblems = 0, mullionProblems = 0;
            double maxPlane = 0, maxFoot = 0, maxLine = 0, maxZ = 0;
            var tops = new List<HangerTop>();
            var rows = new JArray();
            foreach (FramingSourcePlan p in plans)
            {
                CurtainCeilingState st = p.CurtainCeiling;
                long sid = Rid.Value(p.Source.Id);
                var byIndex = FramingMarker.Find(doc, new HashSet<long> { sid })
                    .Where(x => x.Value.SpecHash == p.SpecHash && x.Value.PlanSignature == p.Signature)
                    .GroupBy(x => x.Value.Index).ToDictionary(g => g.Key, g => g.ToList());
                List<List<double[]>> sketchMm = p.Ceiling.LoopsMm.Take(st.SketchLoopCount).ToList();
                ElementId phase = SourcePhase(doc, p.Source);
                var pieces = new JArray();
                for (int i = 0; i < p.Members.Count; i++)
                {
                    FramingMember m = p.Members[i];
                    planned++;
                    if (!byIndex.TryGetValue(i, out var list) || list.Count != 1 || list[0].Value.Role != m.Role) continue;
                    Element e = list[0].Key;
                    found++;
                    var row = new JObject { ["i"] = i, ["role"] = m.Role, ["id"] = Rid.Value(e.Id) };
                    pieces.Add(row);
                    if (Rid.Value(e.GetTypeId()).ToString(CultureInfo.InvariantCulture) != m.TypeKey) wrongType++;
                    if (m.Role == CurtainFramingRoles.Layer)
                    {
                        if (!(e is FootPrintRoof roof)) { wrongType++; row["read"] = "not a footprint roof"; continue; }
                        Level baseLevel = doc.GetElement(roof.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                        Parameter off = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                        double plane = baseLevel == null || off == null ? double.PositiveInfinity : Math.Abs((baseLevel.ProjectElevation + off.AsDouble()) * MmPerFt - m.Z0);
                        maxPlane = Math.Max(maxPlane, plane);
                        row["plane_deviation_mm"] = double.IsInfinity(plane) ? null : (JToken)Math.Round(plane, 3);
                        BoundingBoxXYZ bb = roof.get_BoundingBox(null);
                        if (bb != null) row["bbox_z_mm"] = new JArray(Math.Round(bb.Min.Z * MmPerFt, 1), Math.Round(bb.Max.Z * MmPerFt, 1));
                        double foot = 0;
                        int edgesSloped = 0;
                        var roofLoops = new List<List<double[]>>();
                        foreach (ModelCurveArray loop in roof.GetProfiles())
                        {
                            var pts = new List<double[]>();
                            foreach (ModelCurve mc in loop)
                            {
                                bool defines;
                                try { defines = roof.get_DefinesSlope(mc); } catch (Autodesk.Revit.Exceptions.ApplicationException) { defines = true; }
                                if (defines) edgesSloped++;
                                foreach (XYZ q in mc.GeometryCurve.Tessellate())
                                {
                                    pts.Add(new[] { q.X * MmPerFt, q.Y * MmPerFt });
                                    foot = Math.Max(foot, PlanDistanceToLoops(sketchMm, q.X * MmPerFt, q.Y * MmPerFt));
                                }
                            }
                            if (pts.Count > 1) roofLoops.Add(pts);
                        }
                        foreach (List<double[]> loop in sketchMm)
                            foreach (double[] q in loop) foot = Math.Max(foot, PlanDistanceToLoops(roofLoops, q[0], q[1]));
                        maxFoot = Math.Max(maxFoot, foot);
                        slopeEdges += edgesSloped;
                        row["footprint_deviation_mm"] = double.IsInfinity(foot) ? null : (JToken)Math.Round(foot, 3);
                        row["slope_defining_edges"] = edgesSloped;
                        row["grid"] = ReadLayerGrid(doc, roof, doc.GetElement(roof.GetTypeId()) as RoofType, sketchMm, m.Y0 > 0.5 ? (double?)m.X0 : null, ref gridProblems, ref directionProblems);
                        continue;
                    }
                    if (!(e is Wall w) || !(w.Location is LocationCurve lc) || !(lc.Curve is Line l)) { maxLine = double.PositiveInfinity; row["location"] = "unreadable"; continue; }
                    XYZ a = new XYZ(m.X0 / MmPerFt, m.Y0 / MmPerFt, 0), b = new XYZ(m.X1 / MmPerFt, m.Y1 / MmPerFt, 0);
                    double dev = Math.Max(Flat(l.GetEndPoint(0)).DistanceTo(a), Flat(l.GetEndPoint(1)).DistanceTo(b)) * MmPerFt;
                    double top = TopElevation(doc, w);
                    double zDev = Math.Max(Math.Abs(BaseElevation(doc, w) * MmPerFt - m.Z0), Math.Abs(top * MmPerFt - m.Z1));
                    maxLine = Math.Max(maxLine, dev);
                    maxZ = Math.Max(maxZ, zDev);
                    row["location_deviation_mm"] = Math.Round(dev, 3);
                    row["base_top_deviation_mm"] = Math.Round(zDev, 3);
                    row["grid"] = ReadCurtainWallGrid(doc, w, l, out int gp, out int mp);
                    gridProblems += gp;
                    mullionProblems += mp;
                    foreach (double[] q in HangerStations(m))
                        tops.Add(new HangerTop { Id = Rid.Value(w.Id), Top = new XYZ(q[0] / MmPerFt, q[1] / MmPerFt, top), LengthMm = m.Z1 - m.Z0, Phase = phase });
                }
                rows.Add(new JObject
                {
                    ["source_id"] = sid, ["already_applied"] = p.AlreadyApplied, ["pieces"] = pieces, ["not_built"] = st.NotBuilt,
                    ["member_ids"] = new JArray(p.MemberIds.OrderBy(kv => kv.Key).Select(kv => kv.Value))
                });
            }
            check.Compare("curtain_member_count", planned, found);
            check.Compare("curtain_types", 0, wrongType);
            check.Measure("layer_plane", 0, maxPlane, EndpointToleranceMm, "mm", "max over layers of |base level elevation + base offset - planned plane|, read from the roof's parameters");
            check.Measure("layer_footprint", 0, maxFoot, EndpointToleranceMm, "mm", "max plan distance between a layer's footprint and the ceiling's sketch boundary, both ways");
            check.Compare("layer_flat", 0, slopeEdges);
            check.Compare("grid_direction", 0, directionProblems);
            check.Compare("grid_spacing", 0, gridProblems);
            check.Compare("mullion_types", 0, mullionProblems);
            check.Measure("hanger_location", 0, maxLine, EndpointToleranceMm, "mm", "max over hangers of an end's horizontal distance to the planned line end");
            check.Measure("hanger_base_top", 0, maxZ, EndpointToleranceMm, "mm", "max over hangers of |base or top elevation - plan|, from the level and offset parameters");
            List<long> misses = RecheckHangers(doc, tops, out double maxGap);
            check.Compare("hanger_reaches_support", 0, misses.Distinct().Count());
            evidence["hanger_recheck"] = new JObject
            {
                ["stations_checked"] = tops.Count, ["not_at_support"] = new JArray(misses.Distinct()),
                ["max_gap_mm"] = tops.Count == 0 || double.IsInfinity(maxGap) ? JValue.CreateNull() : (JToken)Math.Round(maxGap, 3)
            };
            evidence["sources"] = rows;
            return check;
        }

        /// <summary>
        /// A layer roof's grid read back: per direction (grid 1 = U lines, grid 2 = V lines, as
        /// CurtainGrid.Grid1Angle's documentation names them) the line count, their plan
        /// direction, grid 1's direction against angle_deg, and a fixed-distance spacing check
        /// across the ceiling's extent. What cannot be read is named and counted.
        /// </summary>
        private static JObject ReadLayerGrid(Document doc, FootPrintRoof roof, RoofType type, List<List<double[]>> sketchMm, double? angleDeg, ref int gridProblems, ref int directionProblems)
        {
            var result = new JObject();
            CurtainGrid grid = null;
            CurtainGridSet set = roof.CurtainGrids;
            if (set != null) foreach (CurtainGrid g in set) { grid = g; break; }
            if (grid == null) { gridProblems++; if (angleDeg.HasValue) directionProblems++; result["read"] = "no curtain grid (not a Sloped Glazing roof?)"; return result; }
            for (int k = 1; k <= 2; k++)
            {
                // Grid 1 is the V lines on a flat footprint roof, grid 2 the U lines (MEASURED; see the plan).
                ICollection<ElementId> ids = k == 1 ? grid.GetVGridLineIds() : grid.GetUGridLineIds();
                var g = new JObject { ["lines"] = ids.Count };
                result["grid" + k] = g;
                XYZ dir = null;
                var mids = new List<XYZ>();
                int notParallel = 0;
                foreach (ElementId id in ids)
                {
                    Curve c = (doc.GetElement(id) as CurtainGridLine)?.FullCurve;
                    if (c == null) continue;
                    XYZ d = Flat(c.GetEndPoint(1) - c.GetEndPoint(0));
                    if (d.GetLength() < 1e-9) continue;
                    d = d.Normalize();
                    if (dir == null) dir = d;
                    else if (Math.Abs(dir.X * d.Y - dir.Y * d.X) > Math.Sin(0.1 * Math.PI / 180)) notParallel++;
                    mids.Add(Flat(c.Evaluate(0.5, true)));
                }
                BuiltInParameter layoutBip = k == 1 ? BuiltInParameter.SPACING_LAYOUT_1 : BuiltInParameter.SPACING_LAYOUT_2;
                BuiltInParameter lengthBip = k == 1 ? BuiltInParameter.SPACING_LENGTH_1 : BuiltInParameter.SPACING_LENGTH_2;
                int layout = type?.get_Parameter(layoutBip)?.AsInteger() ?? -1;
                g["layout"] = layout;
                g["layout_text"] = type?.get_Parameter(layoutBip)?.AsValueString();
                if (notParallel > 0) { gridProblems += notParallel; g["not_parallel"] = notParallel; }
                if (dir == null)
                {
                    g["spacing_check"] = "no grid line to measure";
                    if (layout == 1) { gridProblems++; g["problem"] = "a fixed-distance grid produced no line"; }
                    if (k == 1 && angleDeg.HasValue) { directionProblems++; g["direction_problem"] = "no grid 1 line to read the angle from"; }
                    continue;
                }
                double deg = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
                deg = ((deg % 180) + 180) % 180;
                g["direction_deg"] = Math.Round(deg, 3);
                if (k == 1 && angleDeg.HasValue)
                {
                    // Grid 1 lines run at the angle + 90 deg (they are spaced ALONG the angle).
                    double want = (((angleDeg.Value + 90) % 180) + 180) % 180, diff = Math.Abs(deg - want);
                    diff = Math.Min(diff, 180 - diff);
                    g["planned_direction_deg"] = Math.Round(want, 3);
                    if (diff > 0.1) { directionProblems++; g["direction_problem"] = "grid 1 lines run at " + Math.Round(deg, 2).ToString(CultureInfo.InvariantCulture) + " deg, planned " + Math.Round(want, 2).ToString(CultureInfo.InvariantCulture); }
                }
                if (layout != 1) { g["spacing_check"] = "not fixed distance: count and direction reported only"; continue; }
                XYZ n = new XYZ(-dir.Y, dir.X, 0);
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                foreach (List<double[]> loop in sketchMm)
                    foreach (double[] q in loop) { double v = q[0] * n.X + q[1] * n.Y; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                double spacingMm = (type?.get_Parameter(lengthBip)?.AsDouble() ?? 0) * MmPerFt;
                List<string> problems = CurtainFramingRules.CheckFixedSpacing(mids.Select(q => (q.X * n.X + q.Y * n.Y) * MmPerFt - lo), hi - lo, spacingMm, EndpointToleranceMm);
                g["spacing_mm"] = Math.Round(spacingMm, 2);
                g["spacing_problems"] = new JArray(problems.Take(10).ToArray());
                gridProblems += problems.Count;
            }
            return result;
        }

        /// <summary>Plan distance (mm) from a point to the nearest edge of closed loops; infinity when there are none.</summary>
        private static double PlanDistanceToLoops(List<List<double[]>> loops, double x, double y)
        {
            double best = double.PositiveInfinity;
            foreach (List<double[]> loop in loops)
                for (int i = 0; i < loop.Count; i++)
                {
                    double[] a = loop[i], b = loop[(i + 1) % loop.Count];
                    double dx = b[0] - a[0], dy = b[1] - a[1], len2 = dx * dx + dy * dy;
                    double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / len2));
                    double px = a[0] + t * dx - x, py = a[1] + t * dy - y;
                    best = Math.Min(best, Math.Sqrt(px * px + py * py));
                }
            return best;
        }

        // ---- summary ------------------------------------------------------------------------

        private static JObject CurtainCeilingSummary(List<FramingSourcePlan> plans)
        {
            var rows = new JArray();
            foreach (FramingSourcePlan p in plans)
            {
                CurtainCeilingState st = p.CurtainCeiling;
                FramedCeiling fc = p.Ceiling;
                var indexed = p.Members.Select((m, i) => new { m, i }).ToList();
                var supports = new JObject();
                foreach (KeyValuePair<string, int> kv in fc.Supports.OrderBy(k => k.Key, StringComparer.Ordinal)) supports[kv.Key] = kv.Value;
                bool hangers = indexed.Any(x => x.m.Role == CurtainFramingRoles.Hanger);
                rows.Add(new JObject
                {
                    ["source_id"] = Rid.Value(p.Source.Id), ["status"] = p.AlreadyApplied ? "already_applied" : "planned", ["method"] = "curtain",
                    ["top_face_mm"] = Math.Round(fc.TopMm, 1), ["sketch_loops"] = st.SketchLoopCount, ["openings_not_cut"] = new JArray(fc.HoleIds),
                    ["layers"] = new JArray(indexed.Where(x => x.m.Role == CurtainFramingRoles.Layer).Select(x => new JObject
                    {
                        ["i"] = x.i, ["type_id"] = long.Parse(x.m.TypeKey, CultureInfo.InvariantCulture), ["plane_mm"] = x.m.Z0,
                        ["angle_deg"] = x.m.Y0 > 0.5 ? (JToken)x.m.X0 : null, ["angle_skipped"] = x.m.Y0 < -0.5 ? (JToken)true : null
                    })),
                    ["hanger_base_mm"] = hangers ? (JToken)st.HangerBaseMm : null,
                    ["hanger_direction_deg"] = hangers ? (JToken)Math.Round(st.HangerAngleRad * 180 / Math.PI, 3) : null,
                    ["hangers"] = new JArray(indexed.Where(x => x.m.Role == CurtainFramingRoles.Hanger).Take(SummaryMemberCap).Select(x => new JObject
                    {
                        ["i"] = x.i, ["type_id"] = long.Parse(x.m.TypeKey, CultureInfo.InvariantCulture),
                        ["from"] = new JArray(Math.Round(x.m.X0, 1), Math.Round(x.m.Y0, 1)), ["to"] = new JArray(Math.Round(x.m.X1, 1), Math.Round(x.m.Y1, 1)),
                        ["base_mm"] = x.m.Z0, ["top_mm"] = x.m.Z1
                    })),
                    ["not_built"] = st.NotBuilt, ["hanger_supports"] = supports,
                    ["count_by_role"] = JObject.FromObject(FramingPlanSignature.CountByRole(p.Members)),
                    ["plan_signature"] = p.Signature, ["spec_hash"] = p.SpecHash,
                    ["warnings"] = new JArray(p.Warnings.ToArray()),
                    ["frame"] = "model x and y, absolute z, mm"
                });
            }
            return new JObject { ["method"] = "curtain", ["sources"] = rows, ["member_count"] = plans.Sum(p => p.Members.Count) };
        }
    }
}
