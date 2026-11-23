using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>Compare actual non-structural floor/host wall intersections around a typed edit.
    /// Layer functions do not establish floor purpose: finish types may use Structure.
    /// Preserve measured existing overlaps and exclude only explicitly structural slabs.
    /// The caller owns the transaction and must roll it back when this guard throws.</summary>
    internal static class FloorWallOverlapGuard
    {
        internal sealed class Snapshot
        {
            internal readonly HashSet<long> ElementIds = new HashSet<long>();
            internal readonly Dictionary<string, double?> Volumes = new Dictionary<string, double?>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Tuple<long, long>> Pairs = new Dictionary<string, Tuple<long, long>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Methods = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly JArray Warnings = new JArray();
            internal readonly JArray Diagnostics = new JArray();
        }

        internal static Snapshot Capture(Document doc, IEnumerable<ElementId> elementIds)
            => Capture(doc, elementIds, true);

        private static Snapshot Capture(Document doc, IEnumerable<ElementId> elementIds, bool includeDependentFloors)
        {
            var snapshot = new Snapshot();
            var changed = (elementIds ?? Enumerable.Empty<ElementId>()).Distinct().Select(doc.GetElement)
                .Where(e => e is Wall || e is Floor floor && IsFinishFloor(doc, floor)).ToList();
            if (changed.Count == 0) return snapshot;
            var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => IsFinishFloor(doc, f)).ToList();
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            if (includeDependentFloors)
            {
                // A wall edit may drag the sketch of an adjacent floor. Capture that floor's
                // other wall pairs too, so an induced crossing does not escape the guard.
                var wallBoxes = changed.OfType<Wall>().Select(w => w.get_BoundingBox(null)).ToList();
                foreach (Floor floor in floors)
                {
                    BoundingBoxXYZ floorBox = floor.get_BoundingBox(null);
                    if (wallBoxes.Any(box => box == null || floorBox == null || BoxesTouchOrOverlap(box, floorBox)) &&
                        !changed.Any(e => e.Id == floor.Id)) changed.Add(floor);
                }
            }
            foreach (Element e in changed) snapshot.ElementIds.Add(Rid.Value(e.Id));
            foreach (Element e in changed)
            {
                BoundingBoxXYZ box = e.get_BoundingBox(null);
                IEnumerable<Element> opposite = e is Floor ? walls.Cast<Element>() : floors.Cast<Element>();
                foreach (Element other in opposite)
                {
                    BoundingBoxXYZ otherBox = other.get_BoundingBox(null);
                    // Empty, completely cut bodies may have no bounds. Measure them rather than
                    // equating missing bounds with zero; the solid reader distinguishes empty from unreadable.
                    if (box != null && otherBox != null && !BoxesTouchOrOverlap(box, otherBox)) continue;
                    long floor = Rid.Value((e is Floor ? e : other).Id), wall = Rid.Value((e is Wall ? e : other).Id);
                    string key = floor.ToString(CultureInfo.InvariantCulture) + ":" + wall.ToString(CultureInfo.InvariantCulture);
                    if (snapshot.Volumes.ContainsKey(key)) continue;
                    double? volume = JoinGeometryCommand.SharedVolumeM3(e, other, out string cause);
                    string method = CurtainFloorOverlapRules.SolidIntersectionMethod;
                    Wall curtain = e as Wall ?? other as Wall;
                    string label = WallLabel(curtain);
                    if (!volume.HasValue && curtain != null && curtain.CurtainGrid != null)
                    {
                        volume = ApproximateIntersectionM3(e is Floor ? e : other, curtain, out method);
                    }
                    string problem = CurtainFloorOverlapRules.Problem(volume, JoinGeometryRules.OverlapToleranceM3, method);
                    if (problem == "floor_wall_overlap_unmeasured")
                    {
                        if (curtain == null || curtain.CurtainGrid == null)
                            throw new InvalidOperationException("floor_wall_overlap_unmeasured: floor " + floor + ", wall " + wall + ": " + cause);
                        snapshot.Warnings.Add(new JObject { ["reason_code"] = problem, ["floor_id"] = floor,
                            ["wall_id"] = wall, ["measurement_method"] = method,
                            ["message"] = CurtainFloorOverlapRules.Guidance(method, wall, label) });
                        continue;
                    }
                    snapshot.Volumes[key] = volume;
                    snapshot.Pairs[key] = Tuple.Create(floor, wall);
                    snapshot.Methods[key] = method;
                    snapshot.Labels[key] = label;
                    if (curtain != null && curtain.CurtainGrid != null && problem == "floor_wall_overlap_approximate")
                        snapshot.Warnings.Add(new JObject { ["reason_code"] = problem, ["floor_id"] = floor,
                            ["wall_id"] = wall, ["overlap_m3"] = volume.Value, ["measurement_method"] = method,
                            ["message"] = CurtainFloorOverlapRules.Guidance(method, wall, label) });
                    else if (curtain != null && curtain.CurtainGrid != null && volume.Value > JoinGeometryRules.OverlapToleranceM3)
                        snapshot.Diagnostics.Add(new JObject { ["reason_code"] = "floor_wall_overlap_measured", ["floor_id"] = floor,
                            ["wall_id"] = wall, ["overlap_m3"] = volume.Value, ["measurement_method"] = method,
                            ["message"] = CurtainFloorOverlapRules.Guidance(method, wall, label) });
                }
            }
            return snapshot;
        }

        internal static JArray Increased(Snapshot before, Snapshot after)
        {
            if (before == null || after == null) throw new ArgumentNullException(before == null ? nameof(before) : nameof(after));
            var rows = new JArray();
            foreach (string key in after.Volumes.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (before.Warnings.OfType<JObject>().Any(w =>
                    w.Value<long>("floor_id").ToString(CultureInfo.InvariantCulture) + ":" +
                    w.Value<long>("wall_id").ToString(CultureInfo.InvariantCulture) == key)) continue;
                if (CurtainFloorOverlapRules.IsApproximateMethod(after.Methods[key])) continue;
                double? old = before.Volumes.TryGetValue(key, out double? measured) ? measured : 0;
                double? current = after.Volumes[key];
                string problem = RoomFinishPlanRules.FloorWallOverlapChangeProblem(old, current, JoinGeometryRules.OverlapToleranceM3);
                if (problem == null) continue;
                Tuple<long, long> pair = after.Pairs[key];
                rows.Add(new JObject { ["reason_code"] = problem, ["floor_id"] = pair.Item1, ["wall_id"] = pair.Item2,
                    ["before_m3"] = old.HasValue ? (JToken)old.Value : null, ["after_m3"] = current.HasValue ? (JToken)current.Value : null,
                    ["measurement_method"] = after.Methods[key],
                    ["message"] = CurtainFloorOverlapRules.Guidance(after.Methods[key], pair.Item2,
                        after.Labels.TryGetValue(key, out string label) ? label : null) });
            }
            return rows;
        }

        internal static JArray ThrowIfIncreased(Document doc, Snapshot before, IEnumerable<ElementId> currentElementIds = null)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            IEnumerable<ElementId> ids = before.ElementIds.Select(Rid.Make).Concat(currentElementIds ?? Enumerable.Empty<ElementId>());
            // Keep the dependent-floor scope measured before the write. Newly adjacent floors
            // are checked against the changed wall, without treating their unrelated old pairs as new.
            Snapshot after = Capture(doc, ids, false);
            JArray findings = Increased(before, after);
            if (findings.Count > 0) throw new InvalidOperationException("floor_wall_overlap_increased: " + findings.ToString(Newtonsoft.Json.Formatting.None));
            var diagnostics = new JArray(after.Warnings.Select(w => w.DeepClone()));
            foreach (JToken item in after.Diagnostics) diagnostics.Add(item.DeepClone());
            return diagnostics;
        }

        private static double? ApproximateIntersectionM3(Element floor, Wall curtain, out string method)
        {
            method = CurtainFloorOverlapRules.WarningMethod;
            try
            {
                Solid prism = OrientedPrism(curtain);
                if (prism != null)
                {
                    double? volume = IntersectFloorWithPrism(floor, prism);
                    if (volume.HasValue)
                    {
                        method = CurtainFloorOverlapRules.OrientedPrismMethod;
                        return volume;
                    }
                }
            }
            catch { }
            return BoundingBoxIntersectionM3(floor, curtain, out method);
        }

        private static Solid OrientedPrism(Wall curtain)
        {
            var location = curtain.Location as LocationCurve;
            var line = location?.Curve as Line;
            BoundingBoxXYZ wallBox = curtain.get_BoundingBox(null);
            if (line == null || wallBox == null || curtain.CurtainGrid == null) return null;
            XYZ start = line.GetEndPoint(0), end = line.GetEndPoint(1);
            double dx = end.X - start.X, dy = end.Y - start.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9) return null;
            double nx = -dy / length, ny = dx / length;
            double width = curtain.WallType?.Width ?? 0;
            foreach (ElementId id in curtain.CurtainGrid.GetMullionIds())
            {
                BoundingBoxXYZ component = curtain.Document.GetElement(id)?.get_BoundingBox(null);
                if (component == null) continue;
                XYZ[] corners = BoxCorners(component);
                double min = corners.Min(p => p.X * nx + p.Y * ny);
                double max = corners.Max(p => p.X * nx + p.Y * ny);
                width = Math.Max(width, max - min);
            }
            if (width <= 1e-9) return null;
            CurtainFloorOverlapRules.Point2[] footprint = CurtainFloorOverlapRules.OrientedFootprint(start.X, start.Y, end.X, end.Y, width);
            double[] bounds = WorldBounds(wallBox);
            if (bounds[5] - bounds[2] <= 1e-9) return null;
            var loop = new CurveLoop();
            XYZ[] points = footprint.Select(p => new XYZ(p.X, p.Y, bounds[2])).ToArray();
            for (int i = 0; i < points.Length; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Length]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, bounds[5] - bounds[2]);
        }

        private static double? IntersectFloorWithPrism(Element floor, Solid prism)
        {
            var solids = new List<Solid>();
            CollectFloorSolids(floor.get_Geometry(new Options { ComputeReferences = false, IncludeNonVisibleObjects = true }), solids);
            if (solids.Count == 0) return null;
            double total = 0;
            foreach (Solid solid in solids)
            {
                if (!ClashBooleanRetry.Try(
                    () => BooleanOperationsUtils.ExecuteBooleanOperation(solid, prism, BooleanOperationsType.Intersect),
                    () => BooleanOperationsUtils.ExecuteBooleanOperation(prism, solid, BooleanOperationsType.Intersect),
                    out Solid intersection, out _, out _)) return null;
                if (intersection != null) total += Math.Max(0, intersection.Volume);
            }
            return total * 0.028316846592;
        }

        private static void CollectFloorSolids(GeometryElement geometry, IList<Solid> solids)
        {
            if (geometry == null) return;
            foreach (GeometryObject item in geometry)
            {
                if (item is Solid solid)
                {
                    if (solid.Faces != null && solid.Faces.Size > 0 && solid.Volume > 1e-12) solids.Add(solid);
                }
                else if (item is GeometryInstance instance) CollectFloorSolids(instance.GetInstanceGeometry(), solids);
            }
        }

        private static double? BoundingBoxIntersectionM3(Element floor, Wall curtain, out string method)
        {
            method = CurtainFloorOverlapRules.WarningMethod;
            try
            {
                BoundingBoxXYZ floorBox = floor.get_BoundingBox(null), wallBox = curtain.get_BoundingBox(null);
                if (floorBox == null) return null;
                double[] aa = WorldBounds(floorBox), bb;
                if (wallBox != null)
                {
                    bb = WorldBounds(wallBox);
                    method = CurtainFloorOverlapRules.WallBoundsMethod;
                }
                else
                {
                    var componentBounds = new List<double[]>();
                    foreach (ElementId id in curtain.CurtainGrid.GetPanelIds().Concat(curtain.CurtainGrid.GetMullionIds()))
                    {
                        BoundingBoxXYZ component = floor.Document.GetElement(id)?.get_BoundingBox(null);
                        if (component != null) componentBounds.Add(WorldBounds(component));
                    }
                    if (componentBounds.Count == 0) return null;
                    bb = new[] { componentBounds.Min(x => x[0]), componentBounds.Min(x => x[1]), componentBounds.Min(x => x[2]),
                        componentBounds.Max(x => x[3]), componentBounds.Max(x => x[4]), componentBounds.Max(x => x[5]) };
                    method = CurtainFloorOverlapRules.ComponentBoundsMethod;
                }
                double minX = Math.Max(aa[0], bb[0]), minY = Math.Max(aa[1], bb[1]), minZ = Math.Max(aa[2], bb[2]);
                double maxX = Math.Min(aa[3], bb[3]), maxY = Math.Min(aa[4], bb[4]), maxZ = Math.Min(aa[5], bb[5]);
                const double feet3ToM3 = 0.028316846592;
                return Math.Max(0, maxX - minX) * Math.Max(0, maxY - minY) * Math.Max(0, maxZ - minZ) * feet3ToM3;
            }
            catch { return null; }
        }

        private static double[] WorldBounds(BoundingBoxXYZ box)
        {
            var world = BoxCorners(box).ToList();
            return new[] { world.Min(p => p.X), world.Min(p => p.Y), world.Min(p => p.Z),
                world.Max(p => p.X), world.Max(p => p.Y), world.Max(p => p.Z) };
        }

        private static XYZ[] BoxCorners(BoundingBoxXYZ box)
        {
            XYZ[] corners =
            {
                new XYZ(box.Min.X, box.Min.Y, box.Min.Z), new XYZ(box.Min.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Min.X, box.Max.Y, box.Max.Z),
                new XYZ(box.Max.X, box.Min.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Max.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Max.Y, box.Max.Z)
            };
            return corners.Select(p => box.Transform.OfPoint(p)).ToArray();
        }

        /// <summary>The wall's real kind and type for the guidance text (a basic wall is never called a curtain wall).</summary>
        private static string WallLabel(Wall wall)
        {
            if (wall == null) return null;
            WallType type = null;
            try { type = wall.WallType; } catch (Exception) { }
            string kind = wall.CurtainGrid != null || type?.Kind == WallKind.Curtain ? "Curtain wall"
                : type?.Kind == WallKind.Stacked ? "Stacked wall" : "Basic wall";
            return CurtainFloorOverlapRules.WallLabel(kind, type?.Name);
        }

        internal static bool IsFinishFloor(Document doc, Floor floor)
        {
            Parameter structural = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
            if (structural == null || structural.StorageType != StorageType.Integer)
                throw new InvalidOperationException("floor_wall_role_unmeasured: floor " + Rid.Value(floor.Id) + " structural role cannot be read");
            return structural.AsInteger() != 1;
        }

        private static bool BoxesTouchOrOverlap(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
            a.Max.X >= b.Min.X - 1e-6 && b.Max.X >= a.Min.X - 1e-6 &&
            a.Max.Y >= b.Min.Y - 1e-6 && b.Max.Y >= a.Min.Y - 1e-6 &&
            a.Max.Z >= b.Min.Z - 1e-6 && b.Max.Z >= a.Min.Z - 1e-6;
    }
}
