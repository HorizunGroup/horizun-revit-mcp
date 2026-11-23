using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using static Horizun.Revit.Core.WallSlabRules;

namespace Horizun.Revit.Commands
{
    public partial class AuditModelCommand
    {
        private const int DefaultWallSlabPageSize = 100;
        private const double WallSlabFeetToMm = 304.8;
        private static readonly double[] WallSlabStations = { 0.1, 0.3, 0.5, 0.7, 0.9 };
        private static readonly double[] WallSlabOffsets = { 0, 0.25, -0.25, 0.75, -0.75 };
        private static readonly string[] WallSlabStates =
            { "ok", "hueco", "metida", "atraviesa", "sin_losa", "mixto", "no_medido" };

        private static CommandResult WallSlab(Document doc, JObject request)
        {
            if (!string.Equals(request.Value<string>("target_document"), doc.Title, StringComparison.Ordinal))
                return CommandResult.Fail("target_document must match the active model title: " + doc.Title);
            if (doc.IsFamilyDocument) return CommandResult.Fail("Wall-slab audit requires a project document.");

            double? suppliedTolerance = request.Value<double?>("tolerance_mm");
            double tolerance = suppliedTolerance ?? DefaultToleranceMm;
            if (double.IsNaN(tolerance) || double.IsInfinity(tolerance) || tolerance < 0)
                return CommandResult.Fail("tolerance_mm must be a finite, nonnegative number.");

            long? levelId = request.Value<long?>("level_id");
            if (levelId.HasValue && (!Rid.CanRepresent(levelId.Value) ||
                                    !(doc.GetElement(Rid.Make(levelId.Value)) is Level)))
                return CommandResult.Fail("level_id must identify a level in the host model.");

            var wallIds = (request["wall_ids"] as JArray)?.Values<long>().Distinct().ToArray();
            int offset = request.Value<int?>("offset") ?? 0;
            int pageSize = request.Value<int?>("page_size") ?? DefaultWallSlabPageSize;
            if (offset < 0 || pageSize < 1 || pageSize > 1000)
                return CommandResult.Fail("offset must be nonnegative and page_size must be between 1 and 1000.");
            var assumptions = new List<JObject>();
            if (!suppliedTolerance.HasValue)
                assumptions.Add(AssumptionNotes.Row("wall-slab contact tolerance = 5 mm",
                    "No project tolerance was supplied.", "Set tolerance_mm."));

            try
            {
                return AssumptionNotes.StampResult(ReadWallSlab(doc, levelId, wallIds, offset, pageSize, tolerance), assumptions);
            }
            catch (Exception ex)
            {
                return AssumptionNotes.MarkFailure(CommandResult.Fail("Wall-slab audit could not finish: " + ex.Message),
                    "Using the default contact tolerance (SUPUESTO). ", assumptions);
            }
        }

        private static CommandResult ReadWallSlab(Document doc, long? levelId, long[] wallIds,
                                                  int offset, int pageSize, double tolerance)
        {
            var unresolved = new JArray();
            IEnumerable<Wall> candidates;
            if (wallIds == null)
                candidates = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>();
            else
            {
                var selected = new List<Wall>();
                foreach (long id in wallIds)
                {
                    if (Rid.CanRepresent(id) && doc.GetElement(Rid.Make(id)) is Wall wall) selected.Add(wall);
                    else unresolved.Add(id);
                }
                candidates = selected;
            }
            var walls = candidates.Select(wall => new
                {
                    Wall = wall,
                    Level = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? wall.LevelId
                })
                .Where(w => !levelId.HasValue || Rid.Value(w.Level) == levelId.Value)
                .OrderBy(w => Rid.Value(w.Level)).ThenBy(w => Rid.Value(w.Wall.Id)).ToList();

            var floors = new Lazy<WallSlabFloor[]>(() => new FilteredElementCollector(doc)
                .OfClass(typeof(Floor)).Cast<Floor>().Select(floor => new WallSlabFloor(floor)).ToArray());
            var failures = new Dictionary<long, string>();
            var groupCounts = new Lazy<Dictionary<long, int>>(() => new FilteredElementCollector(doc)
                .OfClass(typeof(Group)).Cast<Group>().GroupBy(group => Rid.Value(group.GetTypeId()))
                .ToDictionary(group => group.Key, group => group.Count()));
            JObject[] rows = MeasurePage(walls, offset, pageSize,
                item => MeasureWallSlab(doc, item.Wall, Rid.Value(item.Level), floors, groupCounts, failures, tolerance));
            var rowsByLevel = rows.ToLookup(row => (long)row["level_id"]);

            var summary = new JArray();
            int incomplete = 0;
            foreach (var level in walls.GroupBy(w => Rid.Value(w.Level)))
            {
                var levelName = (doc.GetElement(level.First().Level) as Level)?.Name;
                var crownCounts = WallSlabCounts();
                var baseCounts = WallSlabCounts();
                int levelIncomplete = 0;
                var levelRows = rowsByLevel[level.Key];
                foreach (JObject row in levelRows)
                {
                    if (!(bool)row["coverage_complete"]) levelIncomplete++;
                    IncrementWallSlabCount(crownCounts, (string)row["crown"]);
                    IncrementWallSlabCount(baseCounts, (string)row["base"]);
                }
                incomplete += levelIncomplete;
                summary.Add(new JObject
                {
                    ["level_id"] = level.Key,
                    ["level_name"] = levelName,
                    ["wall_count"] = level.Count(),
                    ["walls_shown"] = levelRows.Count(),
                    ["walls_incomplete"] = levelIncomplete,
                    ["crown"] = crownCounts,
                    ["base"] = baseCounts
                });
            }

            var worksets = DocumentVisibility.Measure(doc);
            int links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount();
            bool pageComplete = worksets.CoverageComplete && incomplete == 0 && unresolved.Count == 0;
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "wall_slab",
                ["document"] = doc.Title,
                ["scope"] = "Host floors and walls, without a view or phase filter. Status counts cover this page; wall_count covers all filtered walls.",
                ["links_evaluated"] = false,
                ["link_instance_count"] = links,
                ["link_scope_reason"] = "Linked geometry and link instance transforms are outside this host audit.",
                ["host_worksets_coverage_complete"] = worksets.CoverageComplete,
                ["coverage_complete"] = pageComplete && rows.Length == walls.Count,
                ["page_coverage_complete"] = pageComplete,
                ["tolerance_mm"] = tolerance,
                ["walls_total"] = walls.Count,
                ["walls_shown"] = rows.Length,
                ["walls_incomplete"] = incomplete,
                ["unresolved_wall_ids"] = unresolved,
                ["floor_errors"] = new JArray(failures.OrderBy(entry => entry.Key).Select(entry => new JObject
                {
                    ["element_id"] = entry.Key, ["reason"] = entry.Value
                })),
                ["offset"] = offset,
                ["next_offset"] = offset + (long)rows.Length < walls.Count ? (JToken)(offset + rows.Length) : JValue.CreateNull(),
                ["by_level"] = summary,
                ["walls"] = new JArray(rows)
            });
        }

        private static JObject MeasureWallSlab(Document doc, Wall wall, long levelId, Lazy<WallSlabFloor[]> floors,
                                               Lazy<Dictionary<long, int>> groupCounts,
                                               Dictionary<long, string> failures, double tolerance)
        {
            var samples = new JArray();
            var row = new JObject
            {
                ["element_id"] = Rid.Value(wall.Id), ["unique_id"] = wall.UniqueId, ["level_id"] = levelId,
                ["in_model_group"] = wall.GroupId != ElementId.InvalidElementId,
                ["group_repeated"] = JValue.CreateNull(), ["samples"] = samples
            };
            bool complete = true;
            try
            {
                row["group_repeated"] = false;
                if ((bool)row["in_model_group"])
                {
                    row["group_repeated"] = JValue.CreateNull();
                    if (!(doc.GetElement(wall.GroupId) is Group group) ||
                        !groupCounts.Value.TryGetValue(Rid.Value(group.GetTypeId()), out int count))
                        throw new InvalidOperationException("The model group or its instances could not be read.");
                    row["group_id"] = Rid.Value(group.Id);
                    row["group_instance_count"] = count;
                    row["group_repeated"] = count > 1;
                }

                if (wall.WallType.Kind != WallKind.Basic || wall.CrossSection != WallCrossSection.Vertical ||
                    !(wall.Location is LocationCurve location) || !location.Curve.IsBound)
                    throw new InvalidOperationException("Vertical basic walls with a bounded location curve are supported.");

                Curve axis = location.Curve;
                var bounds = wall.get_BoundingBox(null) ?? throw new InvalidOperationException("No wall bounding box.");
                var solids = WallSlabSolids(wall);
                if (solids.Count == 0) throw new InvalidOperationException("No wall solids.");
                long wallOption = WallSlabOption(wall);
                double width = wall.Width;
                foreach (double station in WallSlabStations)
                {
                    var sample = new JObject { ["fraction"] = station };
                    samples.Add(sample);
                    try
                    {
                        Transform axisFrame = axis.ComputeDerivatives(station, true);
                        XYZ transverse = XYZ.BasisZ.CrossProduct(axisFrame.BasisX).Normalize();
                        var points = WallSlabOffsets.Select(shift => axisFrame.Origin + transverse * (shift * width));
                        var measured = MeasureStation(points, point => WallSlabRay(solids, bounds, point),
                            point => ReadWallSlabFloors(floors.Value, wallOption, point, failures), tolerance);
                        if (measured.UnreadableFloors.Length != 0)
                        {
                            sample["unreadable_floor_ids"] = new JArray(measured.UnreadableFloors);
                            throw new InvalidOperationException("One or more candidate floors could not be measured.");
                        }
                        if (!measured.Crown.HasValue)
                            throw new InvalidOperationException("No wall solid intersected the vertical sample.");
                        // The station coordinates are from the crown probe; the base can use a different point.
                        sample["x_mm"] = measured.Crown.Value.Point.X * WallSlabFeetToMm;
                        sample["y_mm"] = measured.Crown.Value.Point.Y * WallSlabFeetToMm;
                        sample["crown"] = WallSlabContact(measured.Crown.Value);
                        sample["base"] = WallSlabContact(measured.Base.Value);
                    }
                    catch (Exception ex)
                    {
                        complete = false;
                        sample["reason"] = ex.Message;
                        sample["crown"] = new JObject { ["state"] = "no_medido" };
                        sample["base"] = new JObject { ["state"] = "no_medido" };
                    }
                }
            }
            catch (Exception ex)
            {
                row["reason"] = ex.Message;
                complete = false;
            }
            row["crown"] = WallSlabEndState(samples, "crown");
            row["base"] = WallSlabEndState(samples, "base");
            row["coverage_complete"] = complete;
            return row;
        }

        private static (IReadOnlyList<SlabHit> Hits, IReadOnlyList<long> Unreadable) ReadWallSlabFloors(
            IEnumerable<WallSlabFloor> floors, long wallOption, XYZ point, Dictionary<long, string> failures)
        {
            var hits = new List<SlabHit>();
            var unreadable = new List<long>();
            foreach (WallSlabFloor floor in floors)
            {
                if (!MayAffectProbe(floor.Option, wallOption, floor.ContainsXY(point))) continue;
                try
                {
                    hits.AddRange(floor.Intersections(point).Select(span => new SlabHit(floor.Id, span)));
                }
                catch (Exception ex)
                {
                    failures[floor.Id] = ex.Message;
                    unreadable.Add(floor.Id);
                }
            }
            return (hits, unreadable);
        }

        private static JObject WallSlabContact(EndProbe<XYZ> probe)
        {
            var row = new JObject
            {
                ["wall_end_z_mm"] = probe.Elevation,
                ["x_mm"] = probe.Point.X * WallSlabFeetToMm, ["y_mm"] = probe.Point.Y * WallSlabFeetToMm
            };
            Contact? contact = probe.Contact;
            if (!contact.HasValue)
            {
                row["state"] = "sin_losa";
                return row;
            }
            Contact found = contact.Value;
            row["state"] = WallSlabStates[(int)found.Relation];
            row["slab_id"] = found.Hit.Id;
            row["slab_bottom_z_mm"] = found.Hit.VerticalSpan.Bottom;
            row["slab_top_z_mm"] = found.Hit.VerticalSpan.Top;
            row["face_delta_mm"] = found.FaceDelta;
            if (found.Relation == Relation.Gap) row["gap_mm"] = -found.FaceDelta;
            return row;
        }

        private static string WallSlabEndState(JArray samples, string end)
        {
            var states = samples.Select(s => (string)s[end]["state"]).Distinct().ToArray();
            if (states.Length == 0) return "no_medido";
            return states.Length == 1 ? states[0] : "mixto";
        }

        private static JObject WallSlabCounts()
        {
            return new JObject(WallSlabStates.Select(state => new JProperty(state, 0)));
        }

        private static void IncrementWallSlabCount(JObject counts, string state)
        {
            counts[state] = (int)counts[state] + 1;
        }

        private static long WallSlabOption(Element element)
        {
            return Rid.Value(element.DesignOption?.Id);
        }

        private static List<Solid> WallSlabSolids(Element element)
        {
            var solids = new List<Solid>();
            AddWallSlabSolids(element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), solids);
            return solids;
        }

        private static void AddWallSlabSolids(GeometryElement geometry, List<Solid> solids)
        {
            if (geometry == null) return;
            foreach (GeometryObject piece in geometry)
            {
                if (piece is Solid solid && solid.Faces.Size > 0 && solid.Volume > 0) solids.Add(solid);
                else if (piece is GeometryInstance instance) AddWallSlabSolids(instance.GetInstanceGeometry(), solids);
            }
        }

        private static List<VerticalSpan> WallSlabRay(List<Solid> solids, BoundingBoxXYZ bounds, XYZ point)
        {
            var segments = new List<VerticalSpan>();
            using (Line ray = Line.CreateBound(new XYZ(point.X, point.Y, bounds.Min.Z - 1),
                                               new XYZ(point.X, point.Y, bounds.Max.Z + 1)))
            using (var options = new SolidCurveIntersectionOptions())
            {
                foreach (Solid solid in solids)
                    using (SolidCurveIntersection hit = solid.IntersectWithCurve(ray, options))
                        for (int i = 0; i < hit.SegmentCount; i++)
                            using (Curve segment = hit.GetCurveSegment(i))
                            {
                                double a = segment.GetEndPoint(0).Z * WallSlabFeetToMm;
                                double b = segment.GetEndPoint(1).Z * WallSlabFeetToMm;
                                segments.Add(new VerticalSpan(Math.Min(a, b), Math.Max(a, b)));
                            }
            }
            return Merge(segments);
        }

        private sealed class WallSlabFloor
        {
            private readonly BoundingBoxXYZ _bounds;
            private readonly string _boundsError;
            private readonly Lazy<List<Solid>> _solids;

            public WallSlabFloor(Floor floor)
            {
                Id = Rid.Value(floor.Id);
                try { Option = WallSlabOption(floor); }
                catch (Exception ex) { _boundsError = "Floor design option could not be read: " + ex.Message; }
                try
                {
                    _bounds = floor.get_BoundingBox(null) ?? throw new InvalidOperationException("No floor bounding box.");
                }
                catch (Exception ex) { _boundsError = ex.Message; }
                _solids = new Lazy<List<Solid>>(() =>
                {
                    var solids = WallSlabSolids(floor);
                    if (solids.Count == 0) throw new InvalidOperationException("No floor solids.");
                    return solids;
                });
            }

            public long Id { get; }
            public long? Option { get; }

            public bool? ContainsXY(XYZ point)
            {
                if (_bounds == null) return null;
                return point.X >= _bounds.Min.X && point.X <= _bounds.Max.X &&
                       point.Y >= _bounds.Min.Y && point.Y <= _bounds.Max.Y;
            }

            public List<VerticalSpan> Intersections(XYZ point)
            {
                if (_boundsError != null) throw new InvalidOperationException(_boundsError);
                return WallSlabRay(_solids.Value, _bounds, point);
            }
        }
    }
}
