// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing operation=wall: the Revit half.
// Original Horizun code.
//
// READING THE WALL. Only a straight Basic wall is framed (a curved one is refused
// by name: its studs would need a radial layout this pass does not plan). The
// wall's own frame is x along the location line from its start, y across it along
// Wall.Orientation (the exterior side), z up from the wall's BASE (base level +
// base offset). Its height is the top constraint's (top level + top offset) or the
// unconnected height; a wall attached to a roof or floor is framed to that
// constraint height, which the probe measures.
//
// THE LAYER. CompoundStructure lists layers exterior -> interior. With W the total
// width, layer i's centre sits at W/2 - (w0 + .. + w(i-1)) - wi/2 from the wall's
// centreline, along Orientation; the location line sits at 0 (wall centreline),
// +/-W/2 (finish faces), or at the core's centre/faces (WALL_KEY_REF_PARAM). The
// studs go on the chosen layer's centre: layer='core' takes the core's structural
// layer (StructuralMaterialIndex) when it is inside the core, else the thickest
// core layer, else the thickest layer (reported).
//
// OPENINGS. Wall.FindInserts (doors, windows, rectangular wall openings, embedded
// walls such as a storefront) plus the inner loops of an edited profile. A door or
// window spans its rough width/height when the family publishes them, else its
// nominal width/height, centred on its location point; its sill is the instance's
// sill height above its level, its head the head height (or sill + height). A
// rectangular Opening spans its BoundaryRect. Each span says where it came from
// (rough | nominal | boundary_rect | bounding_box | embedded_wall | profile_hole) so
// a probe can compare it. The frame's x-range is clipped to the carrying layer's
// real extent at joins (the wall solid, cut at the layer centre).
//
// PLACEMENT BY CATEGORY. RevitAPI.xml (2023 and 2026 read identically) documents
// Document.NewFamilyInstance(Curve, FamilySymbol, Level, StructuralType) without
// ANY statement about the curve's orientation, so the tool does not rely on an
// undocumented one:
//   * Structural Framing  -> StructuralType.Beam, HORIZONTAL members only (tracks,
//     headers, sills, blocking, ceiling mains/cross/perimeter). A stud of that
//     category is refused by name before anything is written.
//   * Structural Columns  -> StructuralType.Column on a VERTICAL line only (studs,
//     kings, jacks, cripples, hangers).
//   * line-based Generic Model (FamilyPlacementType.CurveBased) -> HORIZONTAL members
//     only, placed on the source's level with NewFamilyInstance(Curve, FamilySymbol,
//     Level, StructuralType) and the height put back from the axis. Hosting the line
//     on a created reference plane or sketch plane (the route that would let it stand
//     vertical) was refused by Revit for every member, "does not coincide with the
//     input face", at 0.000 mm off the plane (MEASURED 2026-09-26, Revit 2026): the
//     overload wants a FACE of an element, and a created plane has none.
// Anything else is refused by name. Which placements Revit really commits, and
// whether a column/beam's location curve keeps the planned endpoints, is what
// framing.probes.ps1 measures.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;
using System.Globalization;

namespace Horizun.Revit.Commands
{
    internal enum FramingPlacementKind { Beam, Column, LineBased }

    /// <summary>A straight Basic wall read into its own frame (feet internally, millimetres for the plan).</summary>
    internal sealed class FramedWall
    {
        public Wall Wall;
        public Level Level;
        public XYZ Origin;      // location line start, z = 0
        public XYZ Dir;         // along the wall, unit, horizontal
        public XYZ Normal;      // Wall.Orientation, unit, horizontal
        public double BaseZ;    // feet, project internal
        public double LengthMm, HeightMm;
        /// <summary>The chosen layer's centre from the LOCATION LINE along Normal, feet.</summary>
        public double LayerOffset;
        /// <summary>The wall's centre plane from its location curve along Normal (feet), measured on its side faces when they read.</summary>
        public double CentreFromCurve;
        public double LayerWidthMm;
        public int LayerIndex;
        public string LayerChoice;
        public readonly List<WallOpeningSpan> OpeningsMm = new List<WallOpeningSpan>();
        public readonly List<string> OpeningSources = new List<string>();
        public readonly List<long> InsertIds = new List<long>();
        public readonly List<string> Warnings = new List<string>();

        public XYZ ToModel(double xMm, double yMm, double zMm)
            => new XYZ(Origin.X, Origin.Y, BaseZ + zMm / 304.8) + Dir * (xMm / 304.8) + Normal * (LayerOffset + yMm / 304.8);

        /// <summary>A model point back in the wall's frame, millimetres: {x, y from the layer centre, z from the base}.</summary>
        public double[] ToFrame(XYZ p)
        {
            XYZ d = new XYZ(p.X - Origin.X, p.Y - Origin.Y, 0);
            return new[] { d.DotProduct(Dir) * 304.8, (d.DotProduct(Normal) - LayerOffset) * 304.8, (p.Z - BaseZ) * 304.8 };
        }
    }

    public sealed partial class FramingCommand
    {
        /// <summary>
        /// Where the wall's centre plane really is: the signed distance (feet, along Wall.Orientation)
        /// from its location curve to the midpoint of its exterior and interior side faces, measured on
        /// the faces. Null when they do not read as two parallel planes. MEASURED 2026-09-27 in Revit
        /// 2026: after WALL_KEY_REF_PARAM is set to Finish Face: Exterior on an existing wall, Revit
        /// keeps the curve AND the wall where they were (faces still at +/-100 mm from the curve of a
        /// 200 mm wall), so arithmetic on the parameter put a carrier's centre 100 mm off and Revit
        /// could not cut its door out of it.
        /// </summary>
        internal static double? MeasuredCentreFromCurve(Wall wall)
        {
            try
            {
                if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line line)) return null;
                XYZ p = line.GetEndPoint(0);
                XYZ n = new XYZ(wall.Orientation.X, wall.Orientation.Y, 0).Normalize();
                double? Side(ShellLayerType kind)
                {
                    foreach (Reference r in HostObjectUtils.GetSideFaces(wall, kind))
                        if (wall.GetGeometryObjectFromReference(r) is PlanarFace f && Math.Abs(Math.Abs(f.FaceNormal.DotProduct(n)) - 1) < 1e-6)
                            return (f.Origin - p).DotProduct(n);
                    return null;
                }
                double? ext = Side(ShellLayerType.Exterior), inn = Side(ShellLayerType.Interior);
                if (!ext.HasValue || !inn.HasValue || !(ext.Value > inn.Value)) return null;
                return (ext.Value + inn.Value) / 2;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        internal static FramedWall ReadWall(Document doc, Wall wall, WallFramingSpec spec, out string refusal)
        {
            refusal = null;
            string who = "wall " + Rid.Value(wall.Id);
            if (wall.WallType == null || wall.WallType.Kind != WallKind.Basic) { refusal = who + " is not a Basic wall (curtain and stacked walls are not framed)."; return null; }
            if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line line)) { refusal = who + " is not straight: an arc wall needs a radial stud layout this tool does not plan."; return null; }
            CompoundStructure cs = wall.WallType.GetCompoundStructure();
            IList<CompoundStructureLayer> layers = cs?.GetLayers();
            if (layers == null || layers.Count == 0) { refusal = who + " has no compound structure."; return null; }

            var fw = new FramedWall { Wall = wall };
            XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
            XYZ flat = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0);
            if (flat.GetLength() < 1e-6) { refusal = who + " has no length in plan."; return null; }
            fw.Origin = new XYZ(p0.X, p0.Y, 0);
            fw.Dir = flat.Normalize();
            XYZ n = wall.Orientation;
            fw.Normal = new XYZ(n.X, n.Y, 0).Normalize();
            fw.LengthMm = flat.GetLength() * 304.8;

            // ---- height ------------------------------------------------------------------
            fw.Level = doc.GetElement(wall.LevelId) as Level;
            if (fw.Level == null) { refusal = who + " has no base level."; return null; }
            double baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
            fw.BaseZ = fw.Level.ProjectElevation + baseOffset;
            ElementId topId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
            double height;
            if (topId != ElementId.InvalidElementId && doc.GetElement(topId) is Level top)
                height = top.ProjectElevation + (wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0) - fw.BaseZ;
            else
                height = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
            if (!(height > 0)) { refusal = who + " has no positive height."; return null; }
            fw.HeightMm = height * 304.8;
            try { if (wall.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.AsInteger() == 1) fw.Warnings.Add(who + ": top is attached; framed to its constraint height"); } catch { }

            // ---- layer -------------------------------------------------------------------
            double total = cs.GetWidth();
            int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
            int pick;
            if (!spec.CoreLayer)
            {
                if (spec.LayerIndex >= layers.Count) { refusal = who + " has " + layers.Count + " layers; layer " + spec.LayerIndex + " does not exist."; return null; }
                pick = spec.LayerIndex;
                fw.LayerChoice = "index";
            }
            else if (first >= 0 && last >= first && last < layers.Count)
            {
                int structural = cs.StructuralMaterialIndex;
                if (structural >= first && structural <= last && layers[structural].Width > 0) { pick = structural; fw.LayerChoice = "core_structural"; }
                else { pick = Enumerable.Range(first, last - first + 1).OrderByDescending(i => layers[i].Width).First(); fw.LayerChoice = "core_thickest"; }
            }
            else
            {
                pick = Enumerable.Range(0, layers.Count).OrderByDescending(i => layers[i].Width).First();
                fw.LayerChoice = "thickest_no_core";
                fw.Warnings.Add(who + ": the type has no core; the thickest layer carries the studs");
            }
            if (!(layers[pick].Width > 0)) { refusal = who + ": layer " + pick + " has no thickness (a membrane) and cannot carry studs."; return null; }
            fw.LayerIndex = pick;
            fw.LayerWidthMm = layers[pick].Width * 304.8;
            Func<int, double> faceAfter = k => total / 2 - layers.Take(k).Sum(l => l.Width);   // exterior face of layer k
            double layerCentre = faceAfter(pick) - layers[pick].Width / 2;
            double coreExt = first >= 0 ? faceAfter(first) : total / 2, coreInt = last >= 0 ? faceAfter(last + 1) : -total / 2;
            int key = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0;
            double loc = key == 1 ? (coreExt + coreInt) / 2 : key == 2 ? total / 2 : key == 3 ? -total / 2 : key == 4 ? coreExt : key == 5 ? coreInt : 0;
            // The centre plane is MEASURED on the side faces, not deduced from the location line: the
            // curve does not always sit on the line the parameter names (see MeasuredCentreFromCurve).
            double? measured = MeasuredCentreFromCurve(wall);
            fw.CentreFromCurve = measured ?? -loc;
            if (!measured.HasValue)
                fw.Warnings.Add(who + ": its side faces could not be read as two parallel planes; the layer is placed by the location line's arithmetic");
            else if (Math.Abs(measured.Value + loc) * 304.8 > 1.0)
                fw.Warnings.Add(who + ": the location line reads " + (WallLocationLine)key + " but the curve sits " + Math.Round(Math.Abs(measured.Value + loc) * 304.8, 1).ToString(CultureInfo.InvariantCulture)
                                + " mm from that line (Revit keeps the curve when the location line parameter changes); the measured faces place the layer");
            fw.LayerOffset = fw.CentreFromCurve + layerCentre;

            // ---- the layer's real length ------------------------------------------------------
            // The location curve runs to the join point, which at a T or L junction lies INSIDE
            // the adjoining wall; the layer itself stops at that wall's face. Only a clip, never
            // an extension: studs of two walls must not meet at a corner.
            double[] extent = LayerExtent(wall, fw);
            if (extent == null) fw.Warnings.Add(who + ": the layer's extent could not be read from the wall's solid; framed over the location line's length");
            else if (extent[0] > 0.05 || extent[1] < fw.LengthMm - 0.05)
            {
                double s = Math.Max(0, extent[0]), e = Math.Min(fw.LengthMm, extent[1]);
                if (!(e - s > 0)) { refusal = who + ": its layer has no length between its joins."; return null; }
                fw.Warnings.Add(who + ": layer_clipped_at_joins start " + Math.Round(s, 1).ToString(CultureInfo.InvariantCulture) + " mm, end " + Math.Round(fw.LengthMm - e, 1).ToString(CultureInfo.InvariantCulture) + " mm (read from the wall's solid)");
                fw.Origin += fw.Dir * (s / 304.8);
                fw.LengthMm = e - s;
            }

            // ---- openings ------------------------------------------------------------------
            // Embedded walls (a storefront in a partition) are voids too; the inserts of an
            // embedded wall are not asked for, they sit inside its span and would only overlap it.
            foreach (ElementId id in wall.FindInserts(true, false, true, false))
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                fw.InsertIds.Add(Rid.Value(id));
                WallOpeningSpan span = OpeningSpan(doc, fw, e, out string source);
                if (span == null) { fw.Warnings.Add("insert " + Rid.Value(id) + " (" + (e.Category?.Name ?? e.GetType().Name) + "): no measurable span; ignored"); continue; }
                fw.OpeningsMm.Add(span);
                fw.OpeningSources.Add(source);
            }
            // An edited profile's inner loops are holes no insert reports.
            if (wall.SketchId != ElementId.InvalidElementId && doc.GetElement(wall.SketchId) is Sketch sketch && sketch.Profile != null)
            {
                var loops = new List<List<double[]>>();
                foreach (CurveArray arr in sketch.Profile)
                {
                    var pts = new List<double[]>();
                    foreach (Curve c in arr) foreach (XYZ q in c.Tessellate()) { double[] f = fw.ToFrame(q); pts.Add(new[] { f[0], f[2] }); }
                    if (pts.Count >= 3) loops.Add(pts);
                }
                loops = loops.OrderByDescending(l => Math.Abs(CeilingFramingRules.Area(l))).ToList();
                if (loops.Count > 0) fw.Warnings.Add(who + ": the profile is edited; its outline is not re-planned, only its holes are framed around");
                for (int k = 1; k < loops.Count; k++)
                {
                    fw.OpeningsMm.Add(new WallOpeningSpan
                    {
                        Id = Rid.Value(wall.SketchId).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + k,
                        Start = loops[k].Min(q => q[0]), End = loops[k].Max(q => q[0]), Sill = loops[k].Min(q => q[1]), Head = loops[k].Max(q => q[1])
                    });
                    fw.OpeningSources.Add("profile_hole");
                }
            }
            return fw;
        }

        /// <summary>
        /// The carrying layer's x-range in the wall frame, mm: a line through the layer's centre,
        /// along the wall, intersected with the wall's solid at three heights (so a door at one
        /// end does not shorten it), min start and max end. Null when no solid answers.
        /// </summary>
        internal static double[] LayerExtent(Wall wall, FramedWall fw)
        {
            try
            {
                var solids = new List<Solid>();
                GeometryElement ge = wall.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                if (ge == null) return null;
                foreach (GeometryObject g in ge)
                    if (g is Solid s && s.Volume > 0) solids.Add(s);
                if (solids.Count == 0) return null;
                double lo = double.MaxValue, hi = double.MinValue, pad = 10.0 * 304.8 + fw.LengthMm;
                foreach (double frac in new[] { 0.1, 0.5, 0.9 })
                {
                    Line probe = Line.CreateBound(fw.ToModel(-pad, 0, fw.HeightMm * frac), fw.ToModel(fw.LengthMm + pad, 0, fw.HeightMm * frac));
                    foreach (Solid s in solids)
                    {
                        SolidCurveIntersection hit = s.IntersectWithCurve(probe, new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
                        for (int i = 0; i < hit.SegmentCount; i++)
                        {
                            Curve seg = hit.GetCurveSegment(i);
                            foreach (XYZ q in new[] { seg.GetEndPoint(0), seg.GetEndPoint(1) }) { double x = fw.ToFrame(q)[0]; lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
                        }
                    }
                }
                return lo < hi ? new[] { lo, hi } : null;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        private static WallOpeningSpan OpeningSpan(Document doc, FramedWall fw, Element e, out string source)
        {
            string id = Rid.Value(e.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);
            source = null;
            if (e is Opening op)
            {
                if (!op.IsRectBoundary || op.BoundaryRect == null || op.BoundaryRect.Count < 2) return BoxSpan(fw, e, id, out source);
                double[] a = fw.ToFrame(op.BoundaryRect[0]), b = fw.ToFrame(op.BoundaryRect[1]);
                source = "boundary_rect";
                return new WallOpeningSpan { Id = id, Start = Math.Min(a[0], b[0]), End = Math.Max(a[0], b[0]), Sill = Math.Min(a[2], b[2]), Head = Math.Max(a[2], b[2]) };
            }
            // An embedded wall (a storefront): along the wall its own location line, up its box.
            if (e is Wall ew && ew.Location is LocationCurve elc && elc.Curve != null)
            {
                BoundingBoxXYZ eb = ew.get_BoundingBox(null);
                if (eb == null) return null;
                double xa = fw.ToFrame(elc.Curve.GetEndPoint(0))[0], xb = fw.ToFrame(elc.Curve.GetEndPoint(1))[0];
                source = "embedded_wall";
                return new WallOpeningSpan { Id = id, Start = Math.Min(xa, xb), End = Math.Max(xa, xb),
                                             Sill = fw.ToFrame(eb.Transform.OfPoint(eb.Min))[2], Head = fw.ToFrame(eb.Transform.OfPoint(eb.Max))[2] };
            }
            if (e is FamilyInstance fi && fi.Location is LocationPoint lp)
            {
                double? width = Length(fi, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM), height = Length(fi, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM);
                source = "rough";
                if (width == null || height == null)
                {
                    width = Length(fi, BuiltInParameter.DOOR_WIDTH) ?? Length(fi, BuiltInParameter.WINDOW_WIDTH) ?? Length(fi, BuiltInParameter.FAMILY_WIDTH_PARAM);
                    height = Length(fi, BuiltInParameter.DOOR_HEIGHT) ?? Length(fi, BuiltInParameter.WINDOW_HEIGHT) ?? Length(fi, BuiltInParameter.FAMILY_HEIGHT_PARAM);
                    source = "nominal";
                }
                if (width == null || height == null || !(width > 0) || !(height > 0)) return BoxSpan(fw, e, id, out source);
                Level lvl = doc.GetElement(fi.LevelId) as Level ?? fw.Level;
                double sill = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0;
                double sillZ = lvl.ProjectElevation + sill;
                double x = fw.ToFrame(lp.Point)[0];
                double zs = (sillZ - fw.BaseZ) * 304.8;
                return new WallOpeningSpan { Id = id, Start = x - width.Value * 304.8 / 2, End = x + width.Value * 304.8 / 2, Sill = zs, Head = zs + height.Value * 304.8 };
            }
            return BoxSpan(fw, e, id, out source);
        }

        private static WallOpeningSpan BoxSpan(FramedWall fw, Element e, string id, out string source)
        {
            source = "bounding_box";
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            if (bb == null) return null;
            var xs = new List<double>();
            var zs = new List<double>();
            foreach (XYZ c in new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) })
            {
                double[] f = fw.ToFrame(bb.Transform.OfPoint(c));
                xs.Add(f[0]);
                zs.Add(f[2]);
            }
            return new WallOpeningSpan { Id = id, Start = xs.Min(), End = xs.Max(), Sill = zs.Min(), Head = zs.Max() };
        }

        /// <summary>
        /// A positive length parameter, instance first then type, or null. A rough width or
        /// height a family publishes as 0 is "not given": the span falls back to the nominal
        /// size rather than to the (wider, trim-inclusive) bounding box.
        /// </summary>
        private static double? Length(FamilyInstance fi, BuiltInParameter bip)
        {
            Parameter p = fi.get_Parameter(bip) ?? fi.Symbol?.get_Parameter(bip);
            return p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0 ? p.AsDouble() : (double?)null;
        }

        // ---- placement ------------------------------------------------------------------

        /// <summary>How a type places a member of that orientation, or why it cannot (by name, before any write).</summary>
        internal static string ClassifyType(FamilySymbol symbol, bool vertical, string role, out FramingPlacementKind kind)
        {
            kind = FramingPlacementKind.LineBased;
            if (symbol == null) return role + ": the type id is not a family type.";
            long cat = symbol.Category == null ? 0 : Rid.Value(symbol.Category.Id);
            string name = (symbol.Family?.Name ?? "?") + " : " + symbol.Name;
            if (cat == (long)BuiltInCategory.OST_StructuralFraming)
            {
                if (vertical) return role + ": '" + name + "' is Structural Framing, which places as a beam; a vertical " + role +
                                     " needs a Structural Columns or a line-based Generic Model type.";
                kind = FramingPlacementKind.Beam;
                return null;
            }
            if (cat == (long)BuiltInCategory.OST_StructuralColumns)
            {
                if (!vertical) return role + ": '" + name + "' is a Structural Column, placed on a vertical line only; a horizontal " + role +
                                      " needs a Structural Framing or a line-based Generic Model type.";
                kind = FramingPlacementKind.Column;
                return null;
            }
            if (cat == (long)BuiltInCategory.OST_GenericModel && symbol.Family?.FamilyPlacementType == FamilyPlacementType.CurveBased)
            {
                // A line stands vertical only on a work plane, and Revit hosts a line on a FACE of
                // an element: a created reference plane or sketch plane has none, and every member
                // was refused "does not coincide with the input face" at 0.000 mm off the plane
                // (MEASURED 2026-09-26, Revit 2026). Refused here, before anything is written.
                if (vertical) return role + ": '" + name + "' is a line-based Generic Model, which Revit places on a level line, not standing " +
                                     "vertical on a created work plane; a vertical " + role + " needs a Structural Columns type.";
                kind = FramingPlacementKind.LineBased;
                return null;
            }
            return role + ": '" + name + "' (" + (symbol.Category?.Name ?? "no category") + ", " + (symbol.Family?.FamilyPlacementType.ToString() ?? "?") +
                   ") cannot carry a framing member: use Structural Framing or a line-based Generic Model (horizontal) and Structural Columns (vertical).";
        }

        /// <summary>A type's published section width (stud flange along the wall), millimetres, or null.</summary>
        internal static double? TypeWidthMm(FamilySymbol symbol)
        {
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
            {
                Parameter p = symbol?.get_Parameter(bip);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble() * 304.8;
            }
            return null;
        }

        /// <summary>
        /// Places one member on its axis. A line-based family goes on a reference plane
        /// through the axis spanned with 'across' (the wall's normal for a wall, +Z/plan
        /// normal for a ceiling); that plane is returned so it can be marked and removed.
        /// </summary>
        internal static FamilyInstance PlaceMember(Document doc, FamilySymbol symbol, FramingPlacementKind kind, Line axis, Level level,
            XYZ planeSpan, View planeView, out Element workPlane)
        {
            workPlane = null;
            if (!symbol.IsActive) symbol.Activate();
            switch (kind)
            {
                case FramingPlacementKind.Beam:
                {
                    FamilyInstance beam = doc.Create.NewFamilyInstance(axis, symbol, level, StructuralType.Beam);
                    // The axis is the member's centreline; Revit's default justification hangs a
                    // beam BELOW its line. Centre it where the parameter exists (measured live).
                    try { beam.get_Parameter(BuiltInParameter.Z_JUSTIFICATION)?.Set((int)ZJustification.Center); } catch { }
                    // A track, header or blocking piece ends where the plan says, against a stud's
                    // face. Revit's automatic beam joins would cut back or extend it at a column or
                    // another beam, and the member would no longer measure what the plan and the
                    // takeoff say. Its geometry follows its location line only with joins off at
                    // both ends (whether an unjoined end keeps the planned endpoint is measured live).
                    for (int end = 0; end < 2; end++)
                        try { if (StructuralFramingUtils.IsJoinAllowedAtEnd(beam, end)) StructuralFramingUtils.DisallowJoinAtEnd(beam, end); } catch { }
                    return beam;
                }
                case FramingPlacementKind.Column:
                {
                    FamilyInstance col = doc.Create.NewFamilyInstance(axis, symbol, level, StructuralType.Column);
                    // A vertical line carries no orientation: the section comes in on the project
                    // axes. Its width axis (HandOrientation) is turned onto the run direction, the
                    // wall for a stud; the verification re-reads it (section_along_wall).
                    XYZ along = XYZ.BasisZ.CrossProduct(planeSpan);
                    XYZ hand = col.HandOrientation;
                    if (along.GetLength() > 0.5 && hand != null && hand.GetLength() > 0.5)
                    {
                        along = along.Normalize();
                        double angle = Math.Atan2(hand.CrossProduct(along).Z, hand.DotProduct(along));
                        if (angle > Math.PI / 2) angle -= Math.PI; else if (angle < -Math.PI / 2) angle += Math.PI;   // a section is symmetric
                        if (Math.Abs(angle) > 1e-9)
                        {
                            XYZ p = axis.GetEndPoint(0);
                            ElementTransformUtils.RotateElement(doc, col.Id, Line.CreateBound(p, p + XYZ.BasisZ), angle);
                        }
                    }
                    return col;
                }
                default:
                {
                    // The documented route for a line-based family: a curve and a reference level
                    // (NewFamilyInstance(Curve, FamilySymbol, Level, StructuralType)). Only horizontal
                    // members reach here (ClassifyType refuses a vertical one). Revit may keep the line
                    // on the level and carry the height as an offset, so the height is put back from
                    // the axis and the post-commit re-read judges both ends.
                    FamilyInstance lineBased = doc.Create.NewFamilyInstance(axis, symbol, level, StructuralType.NonStructural);
                    doc.Regenerate();
                    if (lineBased.Location is LocationCurve lc && lc.Curve != null)
                    {
                        double dz = axis.GetEndPoint(0).Z - lc.Curve.GetEndPoint(0).Z;
                        if (Math.Abs(dz) > 1e-6)
                        {
                            Parameter off = lineBased.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                            if (off == null || off.IsReadOnly) off = lineBased.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                            if (off != null && !off.IsReadOnly && off.StorageType == StorageType.Double) off.Set(off.AsDouble() + dz);
                            else ElementTransformUtils.MoveElement(doc, lineBased.Id, new XYZ(0, 0, dz));
                        }
                    }
                    return lineBased;
                }
            }
        }
    }
}
