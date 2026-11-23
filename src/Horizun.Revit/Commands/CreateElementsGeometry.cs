using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private static void NormalizePlan(Document doc, Plan p)
        {
            if (p.Input["source_reference"] != null) SourceTrace.Validate(p.Input["source_reference"] as JObject);
            foreach (string name in new[] { "elevation", "height", "offset", "base_offset", "top_offset", "rotation_degrees", "slope_degrees", "slope_ratio", "facing_degrees", "side_dead_band_mm" })
                if (p.Input[name] != null) GeometryInput.Number(p.Input[name], name);
            foreach (string name in new[] { "flip", "structural" })
                if (p.Input[name] != null && p.Input[name].Type != JTokenType.Boolean) throw new ArgumentException(name + " must be boolean.");
            if (p.Kind == "room" && ((JArray)p.Input["point"]).Count != 2)
                throw new ArgumentException("room.point requires XY only; a room insertion does not apply Z.");
            if (p.Kind == "wall")
            {
                if (p.Input["offset"] != null && p.Input["base_offset"] != null) throw new ArgumentException("Use offset or base_offset, not both.");
                if (Math.Abs(p.Start.Z - p.End.Z) > GeometryInput.Tolerance) throw new ArgumentException("wall start/end must share one horizontal base plane.");
                p.Offset = p.Start.Z - p.Level.ProjectElevation;
                CheckOffset(p, p.Input["base_offset"] ?? p.Input["offset"]);
                p.TopLevel = p.TopLevel ?? Optional<Level>(doc, p.Input, "top_level_id");
                if (p.Input["top_offset"] != null && p.TopLevel == null) throw new ArgumentException("top_offset requires top_level_id.");
                p.TopOffset = (p.Input.Value<double?>("top_offset") ?? 0) * p.Scale;
                if (p.TopLevel != null && Math.Abs(p.TopLevel.ProjectElevation + p.TopOffset - p.Start.Z - p.Height) > GeometryInput.Tolerance)
                    throw new ArgumentException("height disagrees with top_level_id/top_offset and the requested base plane.");
            }
            if (p.Kind == "floor" || p.Kind == "ceiling" || p.Kind == "roof")
            {
                p.Offset = p.Loops[0].First().GetEndPoint(0).Z - p.Level.ProjectElevation;
                CheckOffset(p, p.Input["offset"]);
            }
            if (p.Kind == "family_instance" || p.Kind == "sprinkler" || p.Kind == "structural_column")
            {
                double z = GeometryInput.AbsoluteZ(p.Start.Z, p.Level?.ProjectElevation, p.Input.Value<string>("coordinate_mode"));
                p.Start = new XYZ(p.Start.X, p.Start.Y, z);
                p.Offset = z - (p.Level?.ProjectElevation ?? 0);
                p.Rotation = (p.Input.Value<double?>("rotation_degrees") ?? 0) * Math.PI / 180;
                p.Host = p.InstanceHost;
                var placement = ((FamilySymbol)p.Type).Family.FamilyPlacementType;
                if (p.Host != null && placement != FamilyPlacementType.OneLevelBasedHosted && placement != FamilyPlacementType.WorkPlaneBased)
                    throw new ArgumentException("host_id requires a hosted or work-plane-based family.");
                if (p.Host == null && placement != FamilyPlacementType.OneLevelBased && placement != FamilyPlacementType.TwoLevelsBased)
                    throw new ArgumentException("This family placement requires an explicit compatible host or a different placement route.");
                if (placement == FamilyPlacementType.TwoLevelsBased || p.Kind == "structural_column" || InCategory(p.Type, BuiltInCategory.OST_StructuralColumns))
                {
                    // A column's TOP, stated rather than left to Revit's default (which puts it at
                    // whatever level happens to be above, or nowhere useful on the top level).
                    if (p.Level == null) throw new ArgumentException("a column (two-level family) needs level_id: its base level.");
                    p.TopLevel = Optional<Level>(doc, p.Input, "top_level_id");
                    if (p.Input["top_offset"] != null && p.TopLevel == null) throw new ArgumentException("top_offset requires top_level_id.");
                    double? height = p.Input["height"] == null ? (double?)null : GeometryInput.Number(p.Input["height"], "height") * p.Scale;
                    if (height.HasValue && height.Value <= 0) throw new ArgumentException("height must be positive.");
                    if (p.TopLevel != null)
                    {
                        p.TopOffset = (p.Input.Value<double?>("top_offset") ?? 0) * p.Scale;
                        double top = p.TopLevel.ProjectElevation + p.TopOffset;
                        // The base plane is p.Level.ProjectElevation + p.Offset, NOT p.Start.Z read
                        // directly - even though they hold the same value right here. p.Start.Z was
                        // set two lines above (line 42) to z = GeometryInput.AbsoluteZ(...), and
                        // p.Offset was computed FROM that same z as z - p.Level.ProjectElevation
                        // (line 43), so today base == p.Start.Z exactly. p.Offset is the quantity
                        // that actually governs the instance - it is what gets written to the
                        // family instance's own Level Offset parameter - while p.Start is a working
                        // XYZ that a future change (a host projection, a snap) could legitimately
                        // update without also touching p.Offset. Anchoring this check to p.Offset
                        // keeps it correct against what will actually be placed, not against a
                        // coordinate that happens to agree with it today.
                        double baseZ = p.Level.ProjectElevation + p.Offset;
                        if (top - baseZ <= GeometryInput.Tolerance) throw new ArgumentException("the column's top (top_level_id + top_offset) is not above its base.");
                        if (height.HasValue && Math.Abs(top - baseZ - height.Value) > GeometryInput.Tolerance)
                            throw new ArgumentException("height disagrees with top_level_id/top_offset and the base.");
                    }
                    else if (height.HasValue)
                    {
                        p.TopLevel = p.Level;
                        p.TopOffset = p.Offset + height.Value;
                    }
                }
            }
        }
        private static void CheckOffset(Plan p, JToken offset)
        {
            if (offset != null && Math.Abs(GeometryInput.Number(offset, "offset") * p.Scale - p.Offset) > GeometryInput.Tolerance)
                throw new ArgumentException("offset disagrees with absolute geometry Z minus the level elevation. Supply consistent coordinates and offset.");
        }
        private static void ReadRoofSlopes(Plan p)
        {
            var loopSizes = p.Loops.Select(l => l.Count()).ToList();
            int count = RoofFootprintRules.EdgeCount(loopSizes);
            p.Slopes = new double[count]; p.DefinesSlope = new bool[count];
            p.Overhangs = new double?[count]; p.EdgeOffsets = new double?[count];
            int modes = new[] { "slope_degrees", "slope_ratio", "edge_slopes" }.Count(f => p.Input[f] != null);
            if (modes > 1) throw new ArgumentException("Use exactly one of slope_degrees, slope_ratio or edge_slopes.");
            var edges = p.Input["edge_slopes"] as JArray;
            if (p.Input["edge_slopes"] != null && (edges == null || edges.Count != count))
                throw new ArgumentException("edge_slopes needs exactly one entry per edge, in input order across ALL contours (perimeter first, then each hole): " + count + " here.");
            bool[] uniform = null;
            for (int i = 0; i < count; i++)
            {
                JObject spec = edges == null ? p.Input : edges[i] as JObject;
                if (spec == null) throw new ArgumentException("edge_slopes[" + i + "] must be an object.");
                if (edges != null)
                {
                    if (spec.Properties().Any(f => f.Name != "defines_slope" && f.Name != "slope_degrees" && f.Name != "slope_ratio" && f.Name != "overhang" && f.Name != "offset"))
                        throw new ArgumentException("Unknown edge_slopes field at edge " + i);
                    if (spec["defines_slope"]?.Type != JTokenType.Boolean) throw new ArgumentException("Each edge needs boolean defines_slope.");
                    if (spec["slope_degrees"] != null && spec["slope_ratio"] != null) throw new ArgumentException("An edge cannot use both slope units.");
                    foreach (string field in new[] { "overhang", "offset" })
                    {
                        if (spec[field] == null) continue;
                        double v = GeometryInput.Number(spec[field], field) * p.Scale;
                        string bad = RoofFootprintRules.EdgeNumberError(field, v, i);
                        if (bad != null) throw new ArgumentException(bad);
                        if (field == "overhang") p.Overhangs[i] = v; else p.EdgeOffsets[i] = v;
                    }
                }
                double ratio = spec["slope_degrees"] != null ? GeometryInput.SlopeRatio(GeometryInput.Number(spec["slope_degrees"], "slope_degrees")) :
                    spec["slope_ratio"] == null ? 0 : GeometryInput.Number(spec["slope_ratio"], "slope_ratio");
                if (ratio < 0) throw new ArgumentException("slope_ratio must be non-negative.");
                if (edges == null && uniform == null) uniform = RoofFootprintRules.UniformDefinesSlope(loopSizes, ratio);
                bool defines = edges == null ? uniform[i] : spec.Value<bool>("defines_slope");
                if (edges != null && (defines ? ratio <= 0 : ratio != 0)) throw new ArgumentException("A sloping edge needs a positive slope; a non-sloping edge cannot specify a nonzero slope.");
                p.Slopes[i] = ratio; p.DefinesSlope[i] = defines;
            }
        }
        private static void ReadCeilingSlope(Plan p)
        {
            bool hasSlope = p.Input["slope_degrees"] != null || p.Input["slope_ratio"] != null;
            bool hasArrow = p.Input["slope_arrow_start"] != null || p.Input["slope_arrow_end"] != null;
            bool hasExpected = p.Input["expected_arrow_elevations"] != null;
            if (!hasSlope && !hasArrow && !hasExpected) return;
            if (!hasSlope || p.Input["slope_arrow_start"] == null || p.Input["slope_arrow_end"] == null)
                throw new ArgumentException("A sloped " + p.Kind + " needs slope_ratio or slope_degrees and both slope_arrow_start/end.");
            if (p.Input["slope_degrees"] != null && p.Input["slope_ratio"] != null)
                throw new ArgumentException("Use slope_ratio or slope_degrees for a " + p.Kind + ", not both.");
            double ratio = p.Input["slope_degrees"] != null
                ? GeometryInput.SlopeRatio(GeometryInput.Number(p.Input["slope_degrees"], "slope_degrees"))
                : GeometryInput.Number(p.Input["slope_ratio"], "slope_ratio");
            if (ratio <= 0 || double.IsInfinity(ratio))
                throw new ArgumentException("A sloped " + p.Kind + " needs a finite positive slope.");
            XYZ start = Point(p.Input["slope_arrow_start"], p.Scale, true);
            XYZ end = Point(p.Input["slope_arrow_end"], p.Scale, true);
            double datum = p.Loops[0].First().GetEndPoint(0).Z;
            if (Math.Abs(start.Z - end.Z) > GeometryInput.Tolerance || Math.Abs(start.Z - datum) > GeometryInput.Tolerance)
                throw new ArgumentException("The " + p.Kind + " slope arrow must be horizontal and on the profile datum plane.");
            if (start.DistanceTo(end) < 1 / 304.8)
                throw new ArgumentException("The " + p.Kind + " slope arrow must be at least 1 mm long.");
            p.CeilingArrow = Line.CreateBound(start, end);
            p.CeilingSlopeRatio = ratio;
            if (hasExpected)
            {
                JArray values = p.Input["expected_arrow_elevations"] as JArray;
                if (values == null || values.Count != 2)
                    throw new ArgumentException("expected_arrow_elevations needs two absolute Z values, at arrow start and end.");
                p.CeilingExpectedElevations = values.Select(v => GeometryInput.Number(v, "expected_arrow_elevations") * p.Scale).ToArray();
                double rise = p.CeilingExpectedElevations[1] - p.CeilingExpectedElevations[0];
                if (Math.Abs(rise - ratio * start.DistanceTo(end)) > 1 / 304.8)
                    throw new ArgumentException("expected_arrow_elevations disagree with the arrow length and slope by more than 1 mm.");
            }
        }
        private static int MatchEdge(Plan p, Curve found)
        {
            var edges = p.Loops.SelectMany(l => l).ToList();
            var matches = Enumerable.Range(0, edges.Count).Where(i => SameXYEdge(edges[i], found)).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Revit roof edge cannot be mapped unambiguously to the input perimeter.");
            return matches[0];
        }
        private static bool SameXYEdge(Curve a, Curve b)
        {
            bool Near(XYZ x, XYZ y) => Math.Abs(x.X - y.X) <= GeometryInput.Tolerance && Math.Abs(x.Y - y.Y) <= GeometryInput.Tolerance;
            return (Near(a.GetEndPoint(0), b.GetEndPoint(0)) && Near(a.GetEndPoint(1), b.GetEndPoint(1))) ||
                   (Near(a.GetEndPoint(0), b.GetEndPoint(1)) && Near(a.GetEndPoint(1), b.GetEndPoint(0)));
        }
        private static void SetDouble(Element element, BuiltInParameter name, double value)
        {
            var parameter = element.get_Parameter(name);
            if (parameter == null || parameter.IsReadOnly || !parameter.Set(value)) throw new InvalidOperationException(name + " could not be applied.");
        }
        // THE ELEVATION A LEVEL-BASED INSTANCE OBEYS IS ITS BASE OFFSET, NOT ITS POINT.
        // MEASURED on Revit 2023 and 2026, on a level at 5 ft: a structural column
        // created at Z = level + 2 ft commits with base offset 0 - Revit drops the Z
        // of the creation point - and translating it by that 2 ft changes NOTHING:
        // base offset stays 0 and the solid stays at 5 ft. Its LocationPoint.Z reads
        // 0 throughout, so it is not a place either. The offset parameter IS the
        // elevation, and setting it moves the real geometry (bbox base 5 -> 7 ft).
        // So the translation is confined to XY and the elevation goes through the
        // parameter that governs it; an instance with no such parameter keeps the
        // whole-vector move it always had.
        /// <summary>
        /// A column's top, when the row stated one: FAMILY_TOP_LEVEL_PARAM and
        /// FAMILY_TOP_LEVEL_OFFSET_PARAM set and READ BACK in the same transaction - a
        /// Set that Revit ignores fails the row here instead of leaving a column of
        /// whatever height it chose.
        /// </summary>
        private static void SetTop(Document doc, Plan p, FamilyInstance placed)
        {
            if (p.TopLevel == null || placed == null) return;
            Parameter topLevel = placed.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
            Parameter topOffset = placed.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
            if (topLevel == null || topOffset == null || topLevel.IsReadOnly || topOffset.IsReadOnly)
                throw new InvalidOperationException("this column exposes no writable top level/offset, so the top that was asked for cannot be set.");
            if (!topLevel.Set(p.TopLevel.Id) || !topOffset.Set(p.TopOffset))
                throw new InvalidOperationException("Revit refused the column's top level/offset.");
            doc.Regenerate();
            if (topLevel.AsElementId() != p.TopLevel.Id || Math.Abs(topOffset.AsDouble() - p.TopOffset) > 1e-6)
                throw new InvalidOperationException("the column's top did not read back as set.");
        }

        /// <summary>
        /// A column's base and top, in the order Revit will accept (acero 3.0, L8). MEASURED on Revit
        /// 2024: a column on the first level asked for a 3000 mm base offset and a 3000 mm height was
        /// refused - the base offset was set while the top still sat at its default, below the new
        /// base, and Revit declined the Set. When the base cannot go above a top not yet set, the top
        /// is set first and the base again; both are still read back by SetTop and PositionInstance.
        /// </summary>
        private static void PositionAndTop(Document doc, Plan p, FamilyInstance instance)
        {
            // NO STEP MAY LEAVE THE COLUMN AT ZERO HEIGHT (measured, Revit 2024): the error an
            // intermediate regeneration posts survives to the commit and rolls the batch back.
            if (p.TopLevel != null && TopGoesFirst(doc, p, instance))
            {
                SetTop(doc, p, instance);
                PositionInstance(doc, p, instance);
                SetTop(doc, p, instance);
                VerifyColumnBody(doc, p, instance);
                return;
            }
            try { PositionInstance(doc, p, instance); }
            catch (InvalidOperationException) when (p.TopLevel != null)
            {
                SetTop(doc, p, instance);
                PositionInstance(doc, p, instance);
            }
            SetTop(doc, p, instance);
            VerifyColumnBody(doc, p, instance);
        }

        private static void VerifyColumnBody(Document doc, Plan p, FamilyInstance instance)
        {
            if (p.Kind != "structural_column" && !InCategory(p.Type, BuiltInCategory.OST_StructuralColumns)) return;
            doc.Regenerate();
            double? top = p.TopLevel == null ? (double?)null : p.TopLevel.ProjectElevation + p.TopOffset;
            string problem = ColumnConstraintOrder.SolidSpanProblem(p.Start.Z, top, SolidElevationSpan(instance), 1 / 304.8);
            if (problem != null) throw new InvalidOperationException(problem + ": the physical column body does not retain the requested base/top; bind the family profile to its reference datum and height before placing it");
        }

        private static bool TopGoesFirst(Document doc, Plan p, FamilyInstance instance)
        {
            doc.Regenerate();
            Level baseLevel = BaseLevelOf(doc, instance);
            Parameter offset = ElevationParameter(instance);
            Parameter topLevel = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
            Parameter topOffset = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
            Level currentTopLevel = topLevel == null ? null : doc.GetElement(topLevel.AsElementId()) as Level;
            if (baseLevel == null || offset == null || currentTopLevel == null || topOffset == null) return false;
            double currentBase = baseLevel.ProjectElevation + offset.AsDouble();
            double currentTop = currentTopLevel.ProjectElevation + topOffset.AsDouble();
            return ColumnConstraintOrder.TopFirst(currentBase, currentTop, p.Start.Z, p.TopLevel.ProjectElevation + p.TopOffset);
        }

        private static void PositionInstance(Document doc, Plan p, FamilyInstance instance)
        {
            doc.Regenerate();
            if (!(instance.Location is LocationPoint point)) throw new InvalidOperationException("Family has no point placement to verify.");
            Parameter offset = ElevationParameter(instance);
            Level baseLevel = BaseLevelOf(doc, instance);
            bool governed = baseLevel != null && offset != null && !offset.IsReadOnly;
            XYZ delta = p.Start - point.Point;
            if (governed) delta = new XYZ(delta.X, delta.Y, 0);
            if (delta.GetLength() > GeometryInput.Tolerance) ElementTransformUtils.MoveElement(doc, instance.Id, delta);
            if (governed)
            {
                double want = p.Start.Z - baseLevel.ProjectElevation;
                if (Math.Abs(offset.AsDouble() - want) > GeometryInput.Tolerance && !offset.Set(want))
                    throw new InvalidOperationException(
                        "The base offset that governs this instance's elevation could not be applied, so the requested Z could not be reached.");
            }
            if (p.Input["rotation_degrees"] != null)
                ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(p.Start, p.Start + XYZ.BasisZ), p.Rotation - point.Rotation);
        }

        // A spatial element's committed area in square feet, 0 when unbounded or unreadable.
        private static double SpatialAreaNow(Element e)
        {
            try { return e is SpatialElement spatial ? spatial.Area : 0; } catch { return 0; }
        }

        // The lowest and highest Z of the element's real solids, in project feet, or
        // null when it publishes no solid geometry. Reported as evidence beside the
        // governed elevation. Hosted doors/windows also use it to prove their solid
        // stays within the requested storey; an attached wall may legitimately
        // differ from its base constraint, so walls retain reporting only.
        private static double[] SolidElevationSpan(Element e)
        {
            try
            {
                var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                double low = double.MaxValue, high = double.MinValue;
                void Walk(GeometryElement geometry, Transform transform)
                {
                    if (geometry == null) return;
                    foreach (GeometryObject item in geometry)
                    {
                        if (item is Solid solid && solid.Volume > 1e-9)
                        {
                            BoundingBoxXYZ box = solid.GetBoundingBox();
                            Transform total = transform.Multiply(box.Transform);
                            foreach (double x in new[] { box.Min.X, box.Max.X })
                            foreach (double y in new[] { box.Min.Y, box.Max.Y })
                            foreach (double zz in new[] { box.Min.Z, box.Max.Z })
                            {
                                double z = total.OfPoint(new XYZ(x, y, zz)).Z;
                                if (z < low) low = z;
                                if (z > high) high = z;
                            }
                        }
                        else if (item is GeometryInstance instance)
                            Walk(instance.GetInstanceGeometry(), transform);
                    }
                }
                Walk(e.get_Geometry(options), Transform.Identity);
                return low == double.MaxValue ? null : new[] { low, high };
            }
            catch { return null; }
        }

        // Geometry that Revit actually generated for the temporary footing. A type
        // name and the bearing wall's base cannot establish the footing's top Z:
        // offsets and joins can change it. Tessellated solid edges give the physical
        // extents, even when the wall axis is rotated in plan.
        private static JObject MeasureWallFoundation(Document doc, Created made)
        {
            var footing = doc.GetElement(made.Id) as WallFoundation;
            var wall = made.Plan.FoundationWall;
            if (footing == null || !(wall?.Location is LocationCurve location) || location.Curve == null)
                throw new InvalidOperationException("The provisional wall foundation or its host axis could not be read.");
            Curve axis = location.Curve;
            XYZ start = axis.GetEndPoint(0), end = axis.GetEndPoint(1);
            bool straight = axis is Line;
            XYZ along = straight ? new XYZ(end.X - start.X, end.Y - start.Y, 0).Normalize() : null;
            XYZ across = straight ? new XYZ(-along.Y, along.X, 0) : null;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            double minAlong = double.MaxValue, maxAlong = double.MinValue;
            double minAcross = double.MaxValue, maxAcross = double.MinValue;
            int samples = 0;
            var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            void Walk(GeometryElement geometry, Transform transform)
            {
                if (geometry == null) return;
                foreach (GeometryObject item in geometry)
                {
                    if (item is Solid solid && solid.Volume > 1e-9)
                    {
                        foreach (Edge edge in solid.Edges)
                            foreach (XYZ local in edge.Tessellate())
                            {
                                XYZ point = transform.OfPoint(local);
                                minZ = Math.Min(minZ, point.Z); maxZ = Math.Max(maxZ, point.Z);
                                if (straight)
                                {
                                    XYZ relative = point - start;
                                    double station = relative.DotProduct(along), lateral = relative.DotProduct(across);
                                    minAlong = Math.Min(minAlong, station); maxAlong = Math.Max(maxAlong, station);
                                    minAcross = Math.Min(minAcross, lateral); maxAcross = Math.Max(maxAcross, lateral);
                                }
                                samples++;
                            }
                    }
                    else if (item is GeometryInstance instance)
                        Walk(instance.GetInstanceGeometry(), transform);
                }
            }
            Walk(footing.get_Geometry(options), Transform.Identity);
            if (samples == 0) throw new InvalidOperationException("The provisional wall foundation exposed no measurable solid edges.");
            string Mm(double feet) => Math.Round(feet * 304.8, 1).ToString(CultureInfo.InvariantCulture);
            var values = new JObject
            {
                ["measurement"] = "revit_provisional_solid_rolled_back",
                ["host_wall_id"] = Rid.Value(wall.Id).ToString(CultureInfo.InvariantCulture),
                ["axis_start_mm"] = SafePlanPoint(start),
                ["axis_end_mm"] = SafePlanPoint(end),
                ["axis_length_mm"] = Mm(axis.Length),
                ["top_z_mm"] = Mm(maxZ),
                ["bottom_z_mm"] = Mm(minZ),
                ["thickness_mm"] = Mm(maxZ - minZ)
            };
            if (straight)
            {
                values["solid_length_mm"] = Mm(maxAlong - minAlong);
                values["solid_width_mm"] = Mm(maxAcross - minAcross);
            }
            else values["plan_dimensions_note"] = "curved host: length and width cannot be inferred from one straight cross-section";
            return values;
        }

        // The level a point-placed instance measures its base offset from, or null
        // when the instance is not governed that way (a beam, a hosted symbol).
        private static Level BaseLevelOf(Document doc, Element e)
        {
            if (!(e is FamilyInstance instance) || !(instance.Location is LocationPoint)) return null;
            Parameter level = e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
            if (level == null && e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM) == null)
                level = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            return level == null ? null : doc.GetElement(level.AsElementId()) as Level;
        }

        // THE PARAMETER THAT GOVERNS A POINT-PLACED INSTANCE'S HEIGHT. A column carries a
        // base offset; a level-based or wall-based device carries "Elevation from Level".
        // The second is used only where the first does not exist.
        private static Parameter ElevationParameter(Element e)
        {
            Parameter offset = e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
            if (offset != null) return offset;
            if (e is FamilyInstance fi && fi.HostFace != null) return null;
            return e.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
        }

        // WHAT THE CALLER ASKED FOR IS A PLANE IN THE MODEL, NOT A LOCATION OBJECT.
        // A wall's LocationCurve and a level-based instance's LocationPoint both sit
        // on the level's reference plane rather than on the physical base: MEASURED
        // on Revit 2023 and 2026, a wall based at Z = 0 on a level at 5 ft reports
        // LocationCurve.Z = 5 with WALL_BASE_OFFSET = -5, while its solid starts at
        // 0 - exactly where it was asked to. Verifying the request against the
        // location compares it with the wrong plane and refuses correct geometry;
        // verifying it against the element's OWN base constraint compares it with
        // the plane Revit builds from, and still fails when Revit moves the base or
        // rebinds the constraint to another level.
        /// <summary>
        /// How far a wall's end may slide ALONG its own line and still be the wall
        /// that was asked for.
        ///
        /// Zero when the row disallowed joins - it asked for exactly this line and
        /// Revit was told not to trim it. Otherwise the wall's own thickness,
        /// which is the most a corner join can take: Revit trims back to where the
        /// centrelines cross, and that is half the meeting wall's width.
        /// </summary>
        private static double JoinAllowanceFeet(Plan p)
        {
            if (string.Equals(p.Input.Value<string>("join_rule"), "none", StringComparison.Ordinal))
                return 0.004;   // ~1 mm: Revit rounds, it does not move a wall it may not join

            // The wall's own thickness, where it can be read: a corner trim takes
            // the wall back to where the centrelines cross, which is at most half
            // the meeting wall's width, and walls in one drawing are comparable.
            try
            {
                var type = p.Type as WallType;
                if (type != null && type.Width > 0) return type.Width;
            }
            catch { }
            return 1.0;         // one foot, the widest corner trim this bridge accepts unmeasured
        }

        /// <summary>A direction as a comparable triple: rounded, so 0.9999999 and 1.0 are one answer.</summary>
        private static JArray Direction(XYZ v) => v == null ? null : new JArray(
            Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));

        private static double? GovernedBaseZ(Document doc, Element e)
        {
            if (e is Wall)
            {
                Parameter constraint = e.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                Parameter offset = e.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                if (constraint != null && offset != null && doc.GetElement(constraint.AsElementId()) is Level level)
                    return level.ProjectElevation + offset.AsDouble();
                return null;
            }
            Level baseLevel = BaseLevelOf(doc, e);
            Parameter instanceOffset = ElevationParameter(e);
            if (baseLevel != null && instanceOffset != null) return baseLevel.ProjectElevation + instanceOffset.AsDouble();
            return null;
        }

        private static bool IsRoomRegionWarning(Guid definition) =>
            definition == BuiltInFailures.RoomFailures.RoomsInSameRegionRooms.Guid ||
            definition == BuiltInFailures.RoomFailures.RoomsInSameRegion.Guid;

        /// <summary>Records every failure message of the batch's transaction (description, severity, element ids); resolves nothing.</summary>
        private sealed class BatchFailureCapture : IFailuresPreprocessor
        {
            public readonly List<FailureFact> Facts = new List<FailureFact>();
            public readonly HashSet<long> RoomRegionIds = new HashSet<long>();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                try
                {
                    foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
                    {
                        var ids = new List<long>();
                        try { foreach (ElementId id in message.GetFailingElementIds()) ids.Add(Rid.Value(id)); } catch { }
                        try { foreach (ElementId id in message.GetAdditionalElementIds()) ids.Add(Rid.Value(id)); } catch { }
                        if (IsRoomRegionWarning(message.GetFailureDefinitionId().Guid)) RoomRegionIds.UnionWith(ids);
                        string text;
                        try { text = message.GetDescriptionText(); } catch { text = null; }
                        Facts.Add(new FailureFact
                        {
                            Description = text,
                            IsError = message.GetSeverity() == FailureSeverity.Error,
                            ElementIds = ids.ToArray()
                        });
                    }
                }
                catch { }
                return FailureProcessingResult.Continue;
            }
        }

        private static void ApplyInstanceParameters(Element element, Plan p)
        {
            p.ParameterWrites = new List<TypeParameterWrites.Write>();
            if (!(p.Input["parameters"] is JObject parameters)) return;
            foreach (var property in parameters.Properties())
            {
                var parameter = TypeParameterWrites.ResolveParameter(element, property.Name, out string why);
                if (parameter == null || parameter.IsReadOnly) throw new ArgumentException("parameters." + property.Name + ": " + (why ?? "read-only"));
                TypeParameterWrites.ValidateValue(parameter, property.Value);
                string rawLength = TypeParameterWrites.RawLengthRefusal(parameter, property.Name, property.Value);
                if (rawLength != null) throw new ArgumentException("parameters." + property.Name + ": " + rawLength);
                var write = new TypeParameterWrites.Write { Spec = property.Name, Requested = property.Value.DeepClone() };
                TypeParameterWrites.Apply(parameter, write); p.ParameterWrites.Add(write);
            }
        }

        private static JObject VerifyCreated(Document doc, Created made)
        {
            try { return ReadCreated(doc, made); }
            catch (Exception ex) { return new JObject { ["index"] = made.Index, ["element_id"] = Rid.Value(made.Id), ["verified"] = false, ["error"] = ex.Message, ["measurement_complete"] = false }; }
        }

        private static Created BatchFittingAt(Created made, XYZ requested)
        {
            return made.Batch?.FirstOrDefault(c => (c.Plan.FittingSubtype == "elbow" || c.Plan.FittingSubtype == "union") &&
                c.ExpectedConnected?.Any(m => m.Owner?.Id == made.Id && m.Fact != null &&
                    new XYZ(m.Fact.X, m.Fact.Y, m.Fact.Z).DistanceTo(requested) <= GeometryInput.Tolerance) == true);
        }

        private static JObject ReadInlineFittingEnd(Document doc, Created made, Created fitting, int end)
        {
            const double tolerance = 0.1 / 304.8;
            XYZ requested = end == 0 ? made.Plan.Start : made.Plan.End;
            XYZ other = end == 0 ? made.Plan.End : made.Plan.Start;
            XYZ inward = (other - requested).Normalize();
            var curve = (MEPCurve)doc.GetElement(made.Id);
            XYZ physical = ((LocationCurve)curve.Location).Curve.GetEndPoint(end);

            var ports = MepFacts.Ordered(MepFacts.ManagerOf(doc.GetElement(fitting.Id)))
                .Where(port => port.ConnectorType == ConnectorType.End).ToArray();
            if (ports.Length != 2 || fitting.ExpectedConnected.Count != 2)
                throw new InvalidOperationException("An inline fitting must expose exactly two physical end connectors.");

            Connector ConnectedPort(FittingMember member)
            {
                var runPort = MepFacts.Ordered(MepFacts.ManagerOf(doc.GetElement(member.Owner.Id)))
                    .SingleOrDefault(port => port.ConnectorType == ConnectorType.End && port.Id == member.ConnectorId);
                return runPort == null ? null : ports.SingleOrDefault(port =>
                    runPort.IsConnectedTo(port) && runPort.Origin.DistanceTo(port.Origin) <= tolerance);
            }

            var local = ConnectedPort(fitting.ExpectedConnected.Single(m => m.Owner.Id == made.Id));
            var opposite = ConnectedPort(fitting.ExpectedConnected.Single(m => m.Owner.Id != made.Id));
            if (local == null || opposite == null || local.Id == opposite.Id)
                throw new InvalidOperationException("The inline fitting does not join the two planned run ports.");

            XYZ localVector = physical - requested, oppositeVector = opposite.Origin - requested;
            double trim = localVector.DotProduct(inward);
            bool covers = MepOffsetProfileRules.InlineFittingCoversEnd(trim,
                localVector.CrossProduct(inward).GetLength(), requested.DistanceTo(other),
                oppositeVector.DotProduct(inward), oppositeVector.CrossProduct(inward).GetLength(),
                physical.DistanceTo(local.Origin), true, tolerance);

            JArray Millimeters(XYZ point) => new JArray(point.X * 304.8, point.Y * 304.8, point.Z * 304.8);
            return new JObject
            {
                ["fitting_id"] = Rid.Value(fitting.Id), ["physical_mm"] = Millimeters(physical),
                ["run_port_mm"] = Millimeters(local.Origin), ["opposite_port_mm"] = Millimeters(opposite.Origin),
                ["junction_mm"] = Millimeters(requested), ["trim_mm"] = trim * 304.8, ["covers_junction"] = covers
            };
        }

        /// <summary>The tee's insertion point: where the branch line meets the main's axis.</summary>
        private static XYZ TeePoint(Document doc, ElementId tee) => ((LocationPoint)doc.GetElement(tee).Location).Point;

        /// <summary>Is end (0/1) of this curve the one joined to the tee (the nearer one)?</summary>
        private static bool TeeEndIs(Document doc, Element e, ElementId tee, int end)
        {
            try
            {
                Curve c = ((LocationCurve)e.Location).Curve; XYZ t = TeePoint(doc, tee);
                return c.GetEndPoint(end).DistanceTo(t) <= c.GetEndPoint(1 - end).DistanceTo(t);
            }
            catch { return false; }
        }

        /// <summary>A curve split by takeoff_or_tee: the end, among both pieces' four, nearest the asked point.</summary>
        private static XYZ SplitEndNearest(Document doc, Element e, ElementId piece, XYZ asked)
        {
            var ends = new List<XYZ>();
            foreach (Element x in new[] { e, doc.GetElement(piece) })
                if (x?.Location is LocationCurve lc) { ends.Add(lc.Curve.GetEndPoint(0)); ends.Add(lc.Curve.GetEndPoint(1)); }
            return ends.OrderBy(q => q.DistanceTo(asked)).First();
        }

        private static XYZ ReadElbowJunction(Document doc, Created made, Created fitting, int end)
        {
            var curve = (MEPCurve)doc.GetElement(made.Id);
            XYZ physical = ((LocationCurve)curve.Location).Curve.GetEndPoint(end);
            XYZ requested = end == 0 ? made.Plan.Start : made.Plan.End;
            XYZ other = end == 0 ? made.Plan.End : made.Plan.Start;
            XYZ outward = (requested-other).Normalize();
            double trim = (requested-physical).DotProduct(outward);
            if ((physical-other).CrossProduct(outward).GetLength() > GeometryInput.Tolerance ||
                trim < -GeometryInput.Tolerance || trim >= requested.DistanceTo(other))
                throw new InvalidOperationException("The elbow moved its run off the requested axis or outside the requested segment.");
            var ports = MepFacts.Ordered(MepFacts.ManagerOf(doc.GetElement(fitting.Id)))
                .Where(c => c.ConnectorType == ConnectorType.End).ToList();
            if (ports.Count != 2) throw new InvalidOperationException("An elbow must expose exactly two physical end connectors.");
            bool attached = MepFacts.Ordered(curve.ConnectorManager).Any(c =>
                c.Origin.DistanceTo(physical) <= GeometryInput.Tolerance &&
                ports.Any(p => p.Origin.DistanceTo(physical) <= GeometryInput.Tolerance && p.IsConnectedTo(c)));
            if (!attached || ports.Any(c => !c.IsConnected))
                throw new InvalidOperationException("The measured run end is not connected to the planned elbow.");
            ConnectorFact Axis(Connector c)
            {
                XYZ origin = c.Origin, direction = c.CoordinateSystem.BasisZ;
                return new ConnectorFact { X=origin.X, Y=origin.Y, Z=origin.Z,
                    DirX=direction.X, DirY=direction.Y, DirZ=direction.Z };
            }
            double[] intersection = MepRules.AxisIntersection(Axis(ports[0]), Axis(ports[1]), GeometryInput.Tolerance);
            if (intersection == null) throw new InvalidOperationException("The elbow connector axes do not define one measurable junction.");
            return new XYZ(intersection[0], intersection[1], intersection[2]);
        }
        private static ElementId LevelFromParameters(Element e)
        {
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
                         BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM })
            {
                try
                {
                    Parameter prm = e?.get_Parameter(bip);
                    if (prm != null && prm.StorageType == StorageType.ElementId && prm.AsElementId() != ElementId.InvalidElementId)
                        return prm.AsElementId();
                }
                catch { }
            }
            return ElementId.InvalidElementId;
        }

        private static JObject ReadCreated(Document doc, Created made)
        {
            Plan p = made.Plan;
            var expected = new Dictionary<string, JToken>(); var reads = new Dictionary<string, Func<JToken>>();
            var tolerances = new Dictionary<string, double>();
            var inlineFittingEnds = new HashSet<string>();
            void Exact(string field, JToken value, Func<JToken> read) { expected.Add(field, value); reads.Add(field, read); }
            void Numeric(string field, double value, Func<double> read, double tolerance = GeometryInput.Tolerance)
            { Exact(field, value, () => read()); tolerances.Add(field, tolerance); }
            Element e = null;
            try { e = doc.GetElement(made.Id); } catch { }
            Exact("kind", p.Kind, () => KindMatches(e, p.Kind) ? p.Kind : e?.GetType().Name);
            if (p.Type != null) Exact("type_id", Rid.Value(p.Type.Id), () => Rid.Value(e.GetTypeId()));
            if (p.BuiltWyeTypeUniqueId != null)
                Exact("wye_type_unique_id", p.BuiltWyeTypeUniqueId, () => (e as FamilyInstance)?.Symbol?.UniqueId);
            if (p.Kind == "wall_foundation")
            {
                Exact("host_wall_id", Rid.Value(p.FoundationWall.Id), () => Rid.Value(((WallFoundation)e).WallId));
                // These are measurements of the physical solid, not the read-only
                // "Elevation at Top" or "Width" instance labels. A wall foundation
                // follows its host wall's base, so a different requested top must
                // refuse atomically rather than move somebody else's wall.
                JObject footingGeometry = null;
                double MeasuredMm(string field)
                {
                    if (footingGeometry == null) footingGeometry = MeasureWallFoundation(doc, made);
                    JToken value = footingGeometry[field];
                    if (value == null) throw new InvalidOperationException(field + " cannot be measured on this host");
                    return double.Parse(value.Value<string>(), CultureInfo.InvariantCulture);
                }
                if (p.FoundationTop.HasValue)
                    Numeric("foundation_top_z_mm", p.FoundationTop.Value * 304.8,
                        () => MeasuredMm("top_z_mm"), 1.0);
                if (p.FoundationWidth.HasValue)
                    Numeric("foundation_width_mm", p.FoundationWidth.Value * 304.8,
                        () => MeasuredMm("solid_width_mm"), 1.0);
                if (p.FoundationThickness.HasValue)
                    Numeric("foundation_thickness_mm", p.FoundationThickness.Value * 304.8,
                        () => MeasuredMm("thickness_mm"), 1.0);
            }
            if (p.Level != null)
            {
                // level_elevation used to re-read the level that was REQUESTED, which
                // can only ever agree with itself. It now re-reads the level the
                // committed element actually carries, so a host that rebinds the
                // element to a different level - MEASURED: BeamSystem.Create silently
                // binds another level in some models - fails the postcondition on the
                // elevation too, instead of passing a check about a level the element
                // is not on.
                // A framing member (beam/brace) carries NO Element.LevelId - MEASURED
                // 2026-09-26 in Revit 2026: a committed beam read LevelId = -1 and the
                // postcondition rolled back every beam this tool created. Its level is
                // the Reference Level parameter.
                Func<ElementId> actualLevel = () => e is MEPCurve mep ? mep.ReferenceLevel.Id
                    : e is BeamSystem beamSystem ? beamSystem.Level.Id
                    : e.LevelId != ElementId.InvalidElementId ? e.LevelId
                    : LevelFromParameters(e);
                Exact("level_id", Rid.Value(p.Level.Id), () => Rid.Value(actualLevel()));
                Numeric("level_elevation", p.Level.ProjectElevation,
                    () => doc.GetElement(actualLevel()) is Level carried ? carried.ProjectElevation : double.NaN);
            }
            if (p.WantName != null) Exact("name", p.WantName, () => IdentityOf(e, p.Kind, false));
            if (p.Kind == "reference_plane")
            {
                // The plane holds the line start-end AND the cut vector: both ends lie on it, and its normal is
                // perpendicular to the cut vector - read from the committed plane, not from what was passed in.
                Func<ReferencePlane> plane = () => (ReferencePlane)e;
                Numeric("start_distance_to_plane", 0, () => Math.Abs(plane().Normal.DotProduct(p.Start - plane().BubbleEnd)), 1e-5);
                Numeric("end_distance_to_plane", 0, () => Math.Abs(plane().Normal.DotProduct(p.End - plane().BubbleEnd)), 1e-5);
                Numeric("normal_to_cut_vector", 0, () => Math.Abs(plane().Normal.DotProduct(p.RefCut)), 1e-6);
            }
            if (p.Kind == "wall_profile")
            {
                Exact("profile_world_silhouette", true, () => ProfileWallMatches(e, p)
                    ? (JToken)true : ProfileWallMismatchDetails(e, p));
                Numeric("profile_plane_distance", 0, () => (((LocationCurve)e.Location).Curve.Evaluate(0.5, true) - p.ProfileOrigin).DotProduct(p.ProfileNormal));
                Exact("structural", p.Input.Value<bool?>("structural") ?? false, () => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT).AsInteger() == 1);
            }
            if (p.Kind == "displacement")
            {
                Exact("view_id", Rid.Value(p.OwnerView.Id), () => Rid.Value(e.OwnerViewId));
                Exact("element_ids", new JArray(p.DisplacedIds.Select(Rid.Value).OrderBy(x => x)), () => new JArray(((DisplacementElement)e).GetDisplacedElementIds().Select(Rid.Value).OrderBy(x => x)));
                Exact("physical_bounds_unchanged", p.DisplacedState, () => DisplacementSourceState(doc, p.DisplacedIds));
                for (int axis = 0; axis < 3; axis++) { int a = axis; Numeric("displacement_" + "xyz"[a], p.Displacement[a], () => ((DisplacementElement)e).GetAbsoluteDisplacement()[a]); }
            }
            if (p.Kind == "level") Numeric("elevation", p.Elevation, () => ((Level)e).ProjectElevation);
            if (p.Kind == "grid" && p.GridExtent3D == GridExtentRules.Maximize)
            {
                // Read the vertical extent back: it must reach every level, or the grid will
                // still be invisible (and its dimensions unavailable) on the upper plans.
                Exact("extent_3d_covers_levels", true, () =>
                {
                    Outline extent = ((Grid)e).GetExtents();
                    var elevations = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).ToList();
                    bool covers = GridExtentRules.CoversLevels(extent.MinimumPoint.Z, extent.MaximumPoint.Z, elevations, 1e-3, out List<double> uncovered);
                    if (covers) return (JToken)true;
                    return GridExtentRules.Report(p.GridExtent3D, extent.MinimumPoint.Z, extent.MaximumPoint.Z, elevations.Count, uncovered);
                });
            }
            if (p.Kind == "wall_opening")
            {
                Exact("opening_corners", true, () =>
                {
                    var corners = ((Opening)e).BoundaryRect;
                    var hostLine = (p.OpeningHost.Location as LocationCurve)?.Curve as Line;
                    if (corners.Count == 2 && hostLine != null)
                    {
                        double[] Coordinates(XYZ point) => new[] { point.X, point.Y, point.Z };
                        return GeometryInput.SameWallOpening(Coordinates(hostLine.GetEndPoint(0)),
                            Coordinates(hostLine.GetEndPoint(1)), Coordinates(p.Start), Coordinates(p.End),
                            Coordinates(corners[0]), Coordinates(corners[1]));
                    }
                    return corners.Count == 2 &&
                        ((corners[0].DistanceTo(p.Start) <= GeometryInput.Tolerance && corners[1].DistanceTo(p.End) <= GeometryInput.Tolerance) ||
                         (corners[1].DistanceTo(p.Start) <= GeometryInput.Tolerance && corners[0].DistanceTo(p.End) <= GeometryInput.Tolerance));
                });
            }
            // A WALL IS CHECKED AS A LINE, NOT AS TWO POINTS.
            //
            // Revit may trim a wall's end back to the centreline of whatever it
            // meets, and a row that demands the exact endpoint reports every
            // joined corner as a failure - MEASURED at 1.6 mm ALONG the wall.
            // What the row actually asked for is: this wall, on this line,
            // running between these points. So it is checked as such - strictly
            // across the line, with a stated allowance along it - and both
            // numbers are measured and reported.
            if (p.Kind == "wall")
                Exact("wall_location_line", (int)WallLocationLine.WallCenterline,
                    () => ((Wall)e).get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger());
            if (p.Kind == "wall" && p.Start != null && p.End != null && p.ArcThird == null)
            {
                // IN PLAN. A wall's line is where it stands; its height is a separate
                // question with a separate ruler. MEASURED (Revit 2023, a project base
                // point 94.17 mm off the internal origin): the LocationCurve sits at the
                // level's INTERNAL Z while the asked point's height is in PROJECT
                // elevation, so a 3D distance reported a wall built exactly on its line
                // as 94.17 mm off it - and 3800 mm on Level 2 - and refused every wall
                // in such a model. The height is not this check's question: level_id,
                // level_elevation and offset verify it, on the level's own ruler.
                Func<XYZ, XYZ> flat = v => new XYZ(v.X, v.Y, 0);
                XYZ asked0 = flat(p.Start), asked1 = flat(p.End);
                XYZ direction = (asked1 - asked0).Normalize();
                double allowanceFt = JoinAllowanceFeet(p);

                Numeric("centreline_offset_mm", 0.0, () =>
                {
                    Curve built = ((LocationCurve)e.Location).Curve;
                    double worst = 0;
                    foreach (XYZ end in new[] { built.GetEndPoint(0), built.GetEndPoint(1) })
                    {
                        XYZ v = flat(end) - asked0;
                        double along = v.DotProduct(direction);
                        double across = (v - direction.Multiply(along)).GetLength();
                        if (across > worst) worst = across;
                    }
                    return worst * 304.8;
                }, 1.0);

                Numeric("ends_slid_along_mm", 0.0, () =>
                {
                    Curve built = ((LocationCurve)e.Location).Curve;
                    double span = (asked1 - asked0).GetLength();
                    double worst = 0;
                    foreach (XYZ end in new[] { built.GetEndPoint(0), built.GetEndPoint(1) })
                    {
                        double along = (flat(end) - asked0).DotProduct(direction);
                        double slid = Math.Min(Math.Abs(along), Math.Abs(along - span));
                        if (slid > worst) worst = slid;
                    }
                    return worst * 304.8;
                }, allowanceFt * 304.8);
            }
            else if (p.Start != null && p.Kind != "wall_opening" && p.Kind != "flex_pipe" && p.Kind != "flex_duct")
            {
                Created fitting = e is MEPCurve ? BatchFittingAt(made, p.Start) : null;
                if (fitting?.Plan.FittingSubtype == "union")
                {
                    inlineFittingEnds.Add("start_fitting_trim");
                    Exact("start_fitting_trim", true, () => ReadInlineFittingEnd(doc, made, fitting, 0));
                }
                else
                {
                    XYZ PointNow() => p.TeeJunction != null && TeeEndIs(doc, e, p.TeeJunction, 0) ? TeePoint(doc, p.TeeJunction) : p.SplitPiece != null ? SplitEndNearest(doc, e, p.SplitPiece, p.Start) : fitting != null ? ReadElbowJunction(doc, made, fitting, 0) : e is Grid grid ? grid.Curve.GetEndPoint(0) : e.Location is LocationCurve curve ? curve.Curve.GetEndPoint(0) : ((LocationPoint)e.Location).Point;
                    for (int axis = 0; axis < (p.Kind == "room" || p.Kind == "space" || p.Kind == "area" ? 2 : 3); axis++)
                    { int a = axis; Numeric((fitting == null ? "start_" : "start_junction_") + "xyz"[a], p.Start[a], () => a == 2 && fitting == null ? (GovernedBaseZ(doc, e) ?? PointNow()[2]) : PointNow()[a]); }
                }
            }
            if (p.End != null && p.Kind != "wall_opening" && p.Kind != "flex_pipe" && p.Kind != "flex_duct" && !(p.Kind == "wall" && p.ArcThird == null))
            {
                Created fitting = e is MEPCurve ? BatchFittingAt(made, p.End) : null;
                if (fitting?.Plan.FittingSubtype == "union")
                {
                    inlineFittingEnds.Add("end_fitting_trim");
                    Exact("end_fitting_trim", true, () => ReadInlineFittingEnd(doc, made, fitting, 1));
                }
                else
                {
                    for (int axis = 0; axis < 3; axis++)
                    { int a = axis; Numeric((fitting == null ? "end_" : "end_junction_") + "xyz"[a], p.End[a], () => p.TeeJunction != null && TeeEndIs(doc, e, p.TeeJunction, 1) ? TeePoint(doc, p.TeeJunction)[a] : p.SplitPiece != null ? SplitEndNearest(doc, e, p.SplitPiece, p.End)[a] : a == 2 && fitting == null && GovernedBaseZ(doc, e) is double governed ? governed : fitting != null ? ReadElbowJunction(doc, made, fitting, 1)[a] : (e is Grid grid ? grid.Curve : ((LocationCurve)e.Location).Curve).GetEndPoint(1)[a]); }
                }
            }
            // FLEX RUNS ARE NOT ONE CURVE. FlexPipe/FlexDuct expose their path as Points
            // (including both ends), not as a LocationCurve.Curve with two endpoints - the
            // generic checks above assume the latter and would misread or throw on the
            // former. Points is re-read after commit and compared point-for-point, in
            // order and in COUNT: Revit is free to keep or discard interior points it
            // considers redundant, and a run that came back with fewer of them is a
            // different path even when both ends still land correctly.
            if ((p.Kind == "flex_pipe" || p.Kind == "flex_duct") && p.FlexPoints != null)
            {
                IList<XYZ> FlexPointsNow() => p.Kind == "flex_pipe" ? ((FlexPipe)e).Points : ((FlexDuct)e).Points;
                Exact("flex_point_count", p.FlexPoints.Count, () => FlexPointsNow().Count);
                for (int i = 0; i < p.FlexPoints.Count; i++)
                {
                    int idx = i;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int a = axis;
                        Numeric("flex_point_" + idx + "_" + "xyz"[a], p.FlexPoints[idx][a],
                            () => FlexPointsNow().Count > idx ? FlexPointsNow()[idx][a] : double.NaN);
                    }
                }
            }
            // SPACE: the 2D point and the level re-read via the generic checks above
            // (level_id already covers Space.LevelId, set directly from p.Level at
            // creation). This adds what those do not - whether the placement point
            // still reads as INSIDE the enclosed region via Space.IsPointInSpace, at a
            // height inside the space's own vertical range rather than an arbitrary one.
            // ONLY FOR AN ENCLOSED SPACE. MEASURED in Revit 2023-2027: a space placed where
            // no boundary closes around the point is created with Area 0 and
            // IsPointInSpace answers false for every point, because an unbounded space has
            // no volume to be inside of. That case is legitimate and REPORTED (area_enclosed
            // below), so asserting inside-ness there would refuse every unbounded space.
            if (p.Kind == "space" && p.Level != null && SpatialAreaNow(e) > 0)
            {
                Exact("point_inside_space", true, () =>
                {
                    var space = (Space)e;
                    double testZ = p.Level.ProjectElevation + (space.UnboundedHeight > 0 ? Math.Min(space.UnboundedHeight, 1.0) : 1.0);
                    try { return space.IsPointInSpace(new XYZ(p.Start.X, p.Start.Y, testZ)); } catch { return false; }
                });
            }
            // Point placement and enclosure are distinct outcomes. Missing boundaries are
            // reported below; only all_enclosed promises a closed region.
            if (p.Kind == "room" && !p.Enclosed)
            {
                Exact("area_readable", true, () =>
                {
                    try { double area = ((SpatialElement)e).Area; return !double.IsNaN(area) && !double.IsInfinity(area); }
                    catch { return false; }
                });
            }
            // ALL_ENCLOSED rows: the circuit really was filled - a positive area, every
            // boundary loop closing on itself, the phase asked for, the interior point inside.
            if (p.Enclosed)
            {
                Exact("area_positive", true, () => SpatialAreaNow(e) > 0);
                Exact("boundary_closed", true, () => BoundaryClosed(e));
                Exact("phase_id", Rid.Value(p.Phase.Id), () => PhaseOf(e));
                if (p.Kind == "room")
                    Exact("point_inside_room", true, () =>
                    {
                        try { return ((Autodesk.Revit.DB.Architecture.Room)e).IsPointInRoom(new XYZ(p.Start.X, p.Start.Y, p.Level.ProjectElevation + 0.5)); } catch { return false; }
                    });
            }
            // TOPOSOLID rows: the top of the committed solid at each sampled input point stands at
            // that point's Z (CreateElementsToposolid.cs); the vertices are read once, on first use.
            if (p.Kind == "toposolid" && p.TopoPoints != null && p.TopoSamples != null)
            {
                List<XYZ> topoVerts = null;
                foreach (int k in p.TopoSamples)
                {
                    XYZ at = p.TopoPoints[k];
                    Numeric("top_z_at_point_" + k, at.Z, () => TopoZAt(e, topoVerts ?? (topoVerts = TopoVertices(e)), at), TopoZToleranceFeet);
                }
            }
            if (p.Kind == "wall")
            {
                Numeric("height", p.Height, () => e.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble());
                Numeric("offset", p.Offset, () => e.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble());
                Exact("flip", p.Input.Value<bool?>("flip") ?? false, () => ((Wall)e).Flipped);
                Exact("structural", p.Input.Value<bool?>("structural") ?? false, () => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT).AsInteger() == 1);
                if (p.TopLevel != null)
                {
                    Exact("top_level_id", Rid.Value(p.TopLevel.Id), () => Rid.Value(e.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId()));
                    Numeric("top_offset", p.TopOffset, () => e.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble());
                }
            }
            if (p.Kind == "family_instance" || p.Kind == "sprinkler" || p.Kind == "structural_column" || p.Kind == "structural_framing")
                Exact("structural_type", p.StructuralType.ToString(), () => ((FamilyInstance)e).StructuralType.ToString());
            if (p.Kind == "structural_column" || InCategory(p.Type, BuiltInCategory.OST_StructuralColumns))
            {
                // A family body can remain at its origin while every constraint agrees.
                Numeric("column_solid_base", p.Start.Z, () =>
                    SolidElevationSpan(e)?[0] ?? throw new InvalidOperationException("column solid base could not be measured"), 1 / 304.8);
                if (p.TopLevel != null)
                {
                    Exact("column_top_level_id", Rid.Value(p.TopLevel.Id), () => Rid.Value(e.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM).AsElementId()));
                    Numeric("column_top_offset", p.TopOffset, () => e.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM).AsDouble());
                    Numeric("column_solid_top", p.TopLevel.ProjectElevation + p.TopOffset, () =>
                        SolidElevationSpan(e)?[1] ?? throw new InvalidOperationException("column solid top could not be measured"), 1 / 304.8);
                }
            }
            // Hosted door/window families can report a correct host and level while their
            // physical solid extends into the storey below (observed on GFX40_2). Check
            // the solid itself; a bounding box can include symbolic or analytical lines.
            if (p.Kind == "family_instance" && p.Level != null && e?.Category != null &&
                (Rid.Value(e.Category.Id) == (long)BuiltInCategory.OST_Doors ||
                 Rid.Value(e.Category.Id) == (long)BuiltInCategory.OST_Windows))
            {
                Exact("solid_within_storey", true, () =>
                {
                    double[] span = SolidElevationSpan(e);
                    if (span == null) return false;
                    double baseZ = p.Level.ProjectElevation;
                    double? nextZ = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .Where(level => level.ProjectElevation > baseZ + 1e-6)
                        .Select(level => (double?)level.ProjectElevation).OrderBy(z => z).FirstOrDefault();
                    return LevelSpanRules.WithinStorey(span[0] * 304.8, span[1] * 304.8,
                        baseZ * 304.8, nextZ.HasValue ? (double?)(nextZ.Value * 304.8) : null);
                });
            }
            if ((p.Kind == "family_instance" || p.Kind == "sprinkler") && p.Input["flip"] != null)
            {
                // TWO OPERATIONS, TWO TRACES. flipHand sets HandFlipped; a
                // reflected copy sets Mirrored and leaves HandFlipped alone.
                // Checking the wrong one reports a reflection that did not happen.
                bool wanted = p.Input.Value<bool?>("flip") ?? false;
                if (p.MirrorMethod == "reflected_copy")
                    Exact("mirrored", wanted, () => ((FamilyInstance)e).Mirrored);
                else
                    Exact("flip", wanted, () => ((FamilyInstance)e).HandFlipped);
            }
            if (p.Host != null) Exact("host_id", Rid.Value(p.Host.Id), () => Rid.Value(e is Opening opening ? opening.Host.Id : ((FamilyInstance)e).Host.Id));

            // THE FACE, NOT ONLY THE ELEMENT. A work-plane based instance can
            // report the right host and sit on its other side, which is a device
            // in the next room with every id matching.
            if (p.FacePlacement != null)
            {
                Exact("host_face", p.FacePlacement.Face.ConvertToStableRepresentation(doc),
                      () => ((FamilyInstance)e).HostFace?.ConvertToStableRepresentation(doc));
                Exact("hand_direction",
                      Direction(p.MirrorMethod == "reflected_copy"
                                    ? p.FacePlacement.ReferenceDirection.Negate()
                                    : p.FacePlacement.ReferenceDirection),
                      () => Direction(((FamilyInstance)e).HandOrientation));
            }
            // ROTATION IS CHECKED WHERE IT MEANS WHAT THE ROW MEANT.
            //
            // For an instance standing on a level, LocationPoint.Rotation is the
            // angle in plan the row asked for. For one hosted on a FACE it is
            // measured in that face's own frame - a row asking for 180 degrees in
            // plan came back 120 - so comparing them refuses a correct placement.
            // The orientation of a face-hosted instance is checked by its hand
            // direction instead, which is the thing the drawing's rotation was
            // turned into.
            // A wall-based instance turns with its wall; what the row's rotation meant is
            // the side it faces, and that is what is checked.
            if (p.HostedFacing != null)
                Exact("facing_side", Direction(p.HostedFacing),
                      () => Direction(((FamilyInstance)e).FacingOrientation));
            // A REFLECTED COPY reads its rotation in a mirrored frame: MEASURED, a row asking
            // 0 came back pi. What a reflection across the hand's plane keeps is the facing.
            bool reflectedCopy = p.MirrorMethod == "reflected_copy" && p.FacingBeforeReflection != null;
            if (reflectedCopy && p.FacePlacement == null && p.HostedFacing == null)
                Exact("facing_after_reflection", Direction(p.FacingBeforeReflection),
                      () => Direction(((FamilyInstance)e).FacingOrientation));
            if (p.Input["rotation_degrees"] != null && p.FacePlacement == null && p.HostedFacing == null && !reflectedCopy)
                Numeric("rotation", AngleRules.Normalize(p.Rotation),
                    () => AngleRules.NearestTurn(AngleRules.Normalize(p.Rotation), ((LocationPoint)e.Location).Rotation), 1e-8);
            if (p.SystemType != null && (p.Kind == "duct" || p.Kind == "pipe"))
                Exact("system_type_id", Rid.Value(p.SystemType.Id), () => Rid.Value(e.get_Parameter(p.Kind == "duct" ? BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM : BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM).AsElementId()));
            if (p.Kind == "floor" || p.Kind == "ceiling" || p.Kind == "roof") AddProfileChecks(doc, p, e, Exact, Numeric);
            foreach (var write in p.ParameterWrites ?? new List<TypeParameterWrites.Write>())
            {
                var w = write;
                if (w.Expected.Type == JTokenType.Float)
                    Numeric("parameters." + w.Spec, w.Expected.Value<double>(), () => TypeParameterWrites.Read(TypeParameterWrites.ResolveParameter(e, w.Spec, out _)).Value<double>(), 1e-9);
                else Exact("parameters." + w.Spec, w.Expected, () => TypeParameterWrites.Read(TypeParameterWrites.ResolveParameter(e, w.Spec, out _)));
            }
            var check = new PostconditionCheck(expected.Keys.ToArray());
            foreach (string field in expected.Keys)
            {
                try
                {
                    JToken actual = reads[field]();
                    if (tolerances.TryGetValue(field, out double tolerance))
                        check.Measure(field, expected[field].Value<double>(), actual.Value<double>(), tolerance,
                            field == "rotation" ? "radians" : field.EndsWith("_mm") ? "millimetres" : field == "roof_projected_area" ? "square feet" : field.StartsWith("slope_") ? "rise/run" : field.StartsWith("parameters.") ? "Revit internal" : "feet", "Revit parameter/location/sketch/face readback");
                    else check.Record(field, expected[field], actual, inlineFittingEnds.Contains(field)
                        ? actual.Value<bool>("covers_junction") : JToken.DeepEquals(expected[field], actual));
                }
                catch (Exception ex) { check.Unreadable(field, expected[field], ex.Message); }
            }
            bool traceVerified = true; JObject traceComparison = null;
            if (p.Input["source_reference"] is JObject reference)
            {
                try
                {
                    traceComparison = SourceTrace.Compare(reference, check.ToJson());
                    traceVerified = JToken.DeepEquals(reference, SourceTraceStorage.Read(e)) && traceComparison.Value<bool>("matches");
                }
                catch (Exception ex) { traceVerified = false; traceComparison = new JObject { ["matches"] = false, ["error"] = ex.Message }; }
            }
            var row = new JObject
            {
                ["index"] = p.Index,
                ["kind"] = p.Kind,
                ["element_id"] = Rid.Value(made.Id),
                ["unique_id"] = e?.UniqueId,
                ["present_after_commit"] = e != null,
                ["verified"] = check.AllVerified && traceVerified,
                ["postconditions"] = check.ToJson(),
                ["source_comparison"] = traceComparison
            };
            if (p.FacePlacement != null) row["placement"] = p.FacePlacement.Evidence;
            if (p.HostedEvidence != null) row["placement"] = p.HostedEvidence;
            if (e?.Location is LocationPoint point && p.Level != null)
            {
                row["coordinate_reference"] = "internal_origin";
                // absolute_z_feet is the elevation Revit GOVERNS - the base level it
                // carries plus the base offset it kept - and falls back to the location
                // point only for an instance no base offset governs. Reporting the raw
                // LocationPoint.Z here would publish 0 for a column that stands at 7 ft.
                double governedZ = GovernedBaseZ(doc, e) ?? point.Point.Z;
                row["absolute_z_feet"] = governedZ;
                row["location_point_z_feet"] = point.Point.Z;
                row["level_elevation_feet"] = p.Level.ProjectElevation; row["offset_feet"] = governedZ - p.Level.ProjectElevation;
            }
            // AREA (square feet), REPORTED RATHER THAN ASSERTED. Space and Area are both
            // SpatialElement: Area<=0 means the placement point found no closed boundary
            // around it - Revit still creates the element, at the requested point - and
            // that is a legitimate finding about the model's boundaries, not a placement
            // failure this row caused. Same convention as ModelScanCommand's rooms:
            // unreadable and unbounded are told apart, never folded into one "0".
            if ((p.Kind == "room" || p.Kind == "space" || p.Kind == "area") && e is SpatialElement spatial)
            {
                double? areaSqFt = null;
                try { areaSqFt = spatial.Area; } catch { }
                row["area_sqft"] = areaSqFt.HasValue ? (JToken)Math.Round(areaSqFt.Value, 4) : JValue.CreateNull();
                row["area_enclosed"] = areaSqFt.HasValue ? (JToken)(areaSqFt.Value > 0) : JValue.CreateNull();
                if (p.Kind == "room")
                {
                    JObject placement = ArchitectureCreationRules.RoomPlacement(areaSqFt, BoundaryClosed(e));
                    foreach (string name in new[] { "boundary_closed", "room_status", "area_sqft", "area_enclosed", "area_means" })
                        row[name] = placement[name].DeepClone();
                    row["phase_id"] = PhaseOf(e);
                    foreach (JObject note in (placement["assumptions"] as JArray ?? new JArray()).OfType<JObject>())
                        p.Assumptions.Add(note.DeepClone());
                    row["placement_only"] = !p.Enclosed;
                    if (row.Value<string>("room_status") == "not_enclosed")
                    {
                        row["warning"] = "Room placed without a closed boundary. Enclosure is not verified.";
                    }
                }
                else row["area_means"] = areaSqFt.HasValue
                    ? (areaSqFt.Value > 0 ? "the placement point found a closed boundary; area is measured, not assumed."
                                          : "area is 0: the point found no enclosing boundary at commit time - the element exists, unbounded.")
                    : "the Area property could not be read.";
            }
            // The SOLID Revit actually built, measured independently of every parameter
            // above, so a reader can compare the governed plane against real geometry.
            // Deliberately not the bounding box: MEASURED on a structural column asked
            // for 1500 mm above its level, get_BoundingBox reports a base of 0 because
            // it spans the analytical stick, while the solid starts at 1500 mm exactly.
            if (e != null && (p.Kind == "wall" || p.Kind == "structural_column" || p.Kind == "family_instance" || p.Kind == "sprinkler"))
            {
                double[] span = SolidElevationSpan(e);
                if (span != null) { row["geometry_base_z_feet"] = span[0]; row["geometry_top_z_feet"] = span[1]; }
            }
            if (e is MEPCurve physicalRun && physicalRun.Location is LocationCurve physicalCurve)
            {
                XYZ a=physicalCurve.Curve.GetEndPoint(0), b=physicalCurve.Curve.GetEndPoint(1);
                row["physical_start_feet"] = new JArray(a.X,a.Y,a.Z);
                row["physical_end_feet"] = new JArray(b.X,b.Y,b.Z);
                row["endpoint_verification"] = "Same-batch elbows: requested junctions are compared to intersections of committed connector axes; physical endpoints and attachment are measured separately. Other endpoints compare directly.";
            }
            if (e is FootPrintRoof diagnosticRoof)
            {
                try
                {
                    row["roof_top_face_normals"] = new JArray(HostObjectUtils.GetTopFaces(diagnosticRoof)
                        .Select(r => diagnosticRoof.GetGeometryObjectFromReference(r) as PlanarFace)
                        .Select(f => f == null ? JValue.CreateNull() : (JToken)new JArray(f.FaceNormal.X, f.FaceNormal.Y, f.FaceNormal.Z)));
                    row["roof_top_face_details"] = new JArray(HostObjectUtils.GetTopFaces(diagnosticRoof).Select(r =>
                    {
                        var geometry = diagnosticRoof.GetGeometryObjectFromReference(r);
                        var face = geometry as Face;
                        return new JObject { ["type"] = geometry?.GetType().FullName, ["area"] = face?.Area,
                            ["reference"] = r.ConvertToStableRepresentation(doc) };
                    }));
                }
                catch (Exception ex) { row["roof_geometry_read_error"] = ex.Message; }
            }
            if (e is Wall profileWall && p.Kind == "wall_profile")
            {
                try
                {
                    row["profile_side_face_vertices_feet"] = new JArray(HostObjectUtils.GetSideFaces(profileWall, ShellLayerType.Exterior)
                        .Select(r => profileWall.GetGeometryObjectFromReference(r) as Face).Where(f => f != null)
                        .SelectMany(f => f.GetEdgesAsCurveLoops()).SelectMany(l => l)
                        .Select(c => c.GetEndPoint(0)).Select(v => new JArray(v.X, v.Y, v.Z)));
                }
                catch (Exception ex) { row["profile_geometry_read_error"] = ex.Message; }
            }
            if (p.Kind == "room" || p.Kind == "space")
            {
                row["phase_id"] = PhaseOf(e);
                row["phase_source"] = p.Enclosed ? "explicit phase_id" : "assigned by Revit NewRoom/NewSpace(Level, UV)";
            }
            var production = VerifyProductionProperties(doc, made);
            foreach (var property in production.Properties())
                if (row[property.Name] == null) row[property.Name] = property.Value.DeepClone();
            row["verified"] = row.Value<bool>("verified") && production.Value<bool>("verified");
            return row;
        }

        private static List<PlanarFace> RoofPhysicalTopFaces(Element roof)
        {
            var result = new List<PlanarFace>();
            void Visit(GeometryElement geometry)
            {
                if (geometry == null) throw new InvalidOperationException("Roof solid geometry is unavailable.");
                foreach (GeometryObject item in geometry)
                {
                    if (item is GeometryInstance instance) { Visit(instance.GetInstanceGeometry()); continue; }
                    if (!(item is Solid solid) || solid.Volume <= 0) continue;
                    foreach (Face face in solid.Faces)
                    {
                        if (!(face is PlanarFace planar)) throw new InvalidOperationException("Non-planar roof solid requires a different surface verification route.");
                        if (planar.FaceNormal.Z > 1e-9) result.Add(planar);
                    }
                }
            }
            Visit(roof.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
            if (result.Count == 0) throw new InvalidOperationException("Roof has no physical upward faces.");
            return result;
        }

        private static void AddProfileChecks(Document doc, Plan p, Element e,
            Action<string, JToken, Func<JToken>> exact, Action<string, double, Func<double>, double> numeric)
        {
            BuiltInParameter offset = p.Kind == "floor" ? BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM :
                p.Kind == "ceiling" ? BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM : BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM;
            numeric("offset", p.Offset, () => e.get_Parameter(offset).AsDouble(), GeometryInput.Tolerance);
            exact("profile_xy", true, () =>
            {
                List<List<Curve>> actual;
                if (e is FootPrintRoof roof) actual = roof.GetProfiles().Cast<ModelCurveArray>().Select(loop => loop.Cast<ModelCurve>().Select(x => x.GeometryCurve).ToList()).ToList();
                else
                {
                    ElementId sketchId = e is Floor floor ? floor.SketchId : ((Ceiling)e).SketchId;
                    var sketch = (Sketch)doc.GetElement(sketchId);
                    actual = sketch.Profile.Cast<CurveArray>().Select(loop => loop.Cast<Curve>().ToList()).ToList();
                }
                var desired = p.Loops.Select(loop => loop.ToList()).ToList();
                if (actual.Count != desired.Count) return false;
                foreach (var loop in desired)
                {
                    int match = actual.FindIndex(other => other.Count == loop.Count && loop.All(c => other.Count(d => SameXYEdge(c, d)) == 1));
                    if (match < 0) return false; actual.RemoveAt(match);
                }
                return true;
            });
            if ((p.Kind == "ceiling" || p.Kind == "floor") && p.CeilingArrow != null)
            {
                double[] Actual()
                {
                    var faces = (p.Kind == "floor" ? HostObjectUtils.GetTopFaces((HostObject)e) : HostObjectUtils.GetBottomFaces((HostObject)e))
                        .Select(r => e.GetGeometryObjectFromReference(r) as PlanarFace).ToList();
                    if (faces.Count == 0 || faces.Any(f => f == null || Math.Abs(f.FaceNormal.Z) < 0.2))
                        throw new InvalidOperationException("Sloped " + p.Kind + " has no readable planar " + (p.Kind == "floor" ? "top face." : "underside."));
                    PlanarFace face = faces.OrderByDescending(f => f.Area).First();
                    XYZ a = p.CeilingArrow.GetEndPoint(0), b = p.CeilingArrow.GetEndPoint(1);
                    return PlanimetrySlopeRules.ElevationsOnPlane(a.X, a.Y, b.X, b.Y,
                        face.Origin.X, face.Origin.Z, face.FaceNormal.X, face.FaceNormal.Y,
                        face.FaceNormal.Z, face.Origin.Y);
                }
                double run = p.CeilingArrow.Length;
                numeric(p.Kind + "_slope_ratio", p.CeilingSlopeRatio,
                    () => { double[] z = Actual(); return (z[1] - z[0]) / run; }, 0.0005);
                if (p.CeilingExpectedElevations != null)
                {
                    numeric("arrow_start_elevation", p.CeilingExpectedElevations[0], () => Actual()[0], 5 / 304.8);
                    numeric("arrow_end_elevation", p.CeilingExpectedElevations[1], () => Actual()[1], 5 / 304.8);
                }
            }
            if ((p.Kind == "floor" && p.CeilingArrow == null) || (p.Kind == "ceiling" && p.CeilingArrow == null))
                numeric("reference_face_elevation", p.Level.ProjectElevation + p.Offset, () =>
                {
                    var references = p.Kind == "floor" ? HostObjectUtils.GetTopFaces((HostObject)e) : HostObjectUtils.GetBottomFaces((HostObject)e);
                    var faces = references.Select(r => e.GetGeometryObjectFromReference(r) as PlanarFace).ToList();
                    if (faces.Count == 0 || faces.Any(f => f == null || Math.Abs(Math.Abs(f.FaceNormal.Z) - 1) > 1e-8)) throw new InvalidOperationException("No fully horizontal reference faces.");
                    double z = faces[0].Origin.Z;
                    if (faces.Any(f => Math.Abs(f.Origin.Z - z) > GeometryInput.Tolerance)) throw new InvalidOperationException("Reference faces have different elevations.");
                    return z;
                }, GeometryInput.Tolerance);
            // MEASURED 2026-10-16: "Sloped Glazing" is a FootPrintRoof made of curtain panels - it has NO solid upward
            // face, so the face checks read "unreadable" and a correct roof was refused. Its slopes are still verified
            // per edge below (defines_slope_i / slope_i), which is the part that can be measured on a glazing.
            bool glazing = false;
            try { glazing = e is FootPrintRoof gr && gr.CurtainGrids != null && gr.CurtainGrids.Size > 0; } catch { }
            if (p.Kind == "roof" && glazing)
            {
                for (int i = 0; i < p.Slopes.Length; i++)
                {
                    int edge = i;
                    ModelCurve ReadEdge() => ((FootPrintRoof)e).GetProfiles().Cast<ModelCurveArray>().SelectMany(a => a.Cast<ModelCurve>()).Single(c => MatchEdge(p, c.GeometryCurve) == edge);
                    exact("defines_slope_" + edge, p.DefinesSlope[edge], () => ((FootPrintRoof)e).get_DefinesSlope(ReadEdge()));
                    if (p.DefinesSlope[edge]) numeric("slope_" + edge, p.Slopes[edge], () => ((FootPrintRoof)e).get_SlopeAngle(ReadEdge()), 1e-8);
                    if (p.Overhangs[edge].HasValue) numeric("overhang_" + edge, p.Overhangs[edge].Value, () => ((FootPrintRoof)e).get_Overhang(ReadEdge()), 1e-8);
                    if (p.EdgeOffsets[edge].HasValue) numeric("edge_offset_" + edge, p.EdgeOffsets[edge].Value, () => ((FootPrintRoof)e).get_Offset(ReadEdge()), 1e-8);
                }
            }
            else if (p.Kind == "roof")
            {
                exact("roof_face_slopes", true, () =>
                {
                    var faces = RoofPhysicalTopFaces(e);
                    var observed = faces.Select(f => Math.Sqrt(f.FaceNormal.X * f.FaceNormal.X + f.FaceNormal.Y * f.FaceNormal.Y) / Math.Abs(f.FaceNormal.Z)).ToList();
                    var desired = p.Slopes.Where((s, i) => p.DefinesSlope[i]).ToList(); if (desired.Count == 0) desired.Add(0);
                    return desired.All(s => observed.Any(a => Math.Abs(a - s) <= 1e-8)) && observed.All(a => desired.Any(s => Math.Abs(a - s) <= 1e-8));
                });
                // Perimeter minus holes. An OVERHANG makes the roof larger than its footprint by construction, so with
                // any overhang the area check does not apply (the per-edge overhang readback below holds it instead).
                double footprintArea = RoofFootprintRules.NetFootprintArea(p.Loops.Select(loop => loop.Sum(c =>
                    c.GetEndPoint(0).X * c.GetEndPoint(1).Y - c.GetEndPoint(1).X * c.GetEndPoint(0).Y) / 2).ToList());
                if (RoofFootprintRules.ProjectedAreaCheckApplies(p.Overhangs.Where(o => o.HasValue).Select(o => o.Value)))
                    numeric("roof_projected_area", footprintArea,
                        () => RoofPhysicalTopFaces(e).Sum(f => f.Area * f.FaceNormal.Z), Math.Max(1e-6, footprintArea * 1e-8));
                for (int i = 0; i < p.Slopes.Length; i++)
                {
                    int edge = i;
                    ModelCurve ReadEdge() => ((FootPrintRoof)e).GetProfiles().Cast<ModelCurveArray>().SelectMany(a => a.Cast<ModelCurve>()).Single(c => MatchEdge(p, c.GeometryCurve) == edge);
                    exact("defines_slope_" + edge, p.DefinesSlope[edge], () => ((FootPrintRoof)e).get_DefinesSlope(ReadEdge()));
                    if (p.DefinesSlope[edge]) numeric("slope_" + edge, p.Slopes[edge], () => ((FootPrintRoof)e).get_SlopeAngle(ReadEdge()), 1e-8);
                    if (p.Overhangs[edge].HasValue) numeric("overhang_" + edge, p.Overhangs[edge].Value, () => ((FootPrintRoof)e).get_Overhang(ReadEdge()), 1e-8);
                    if (p.EdgeOffsets[edge].HasValue) numeric("edge_offset_" + edge, p.EdgeOffsets[edge].Value, () => ((FootPrintRoof)e).get_Offset(ReadEdge()), 1e-8);
                }
            }
        }
    }
}
