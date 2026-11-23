// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_room_finishes: finish floors and baseboards BY ROOM, through
// ModelEditRunner (rehearse, token, TransactionGroup, re-read, roll back).
//
//   floors      one floor per room, its contour the room's boundary at the wall
//               FINISH faces (holes included), its type from the room's use
//               (type_by_use) or the default, on the room's level at offset_mm
//               measured from where the room sits (its level plus its base offset);
//               copy_parameters carries room values (zone, use) onto the floor.
//               Re-read: one floor per room, type, level, the floor's computed area
//               against the area its loops enclose (0.5 %), the volume it shares
//               with each nearby host wall that rises above the floor's top (<= 1 cm3:
//               it does not run through them; a wall ending below the floor or inside
//               its thickness - the wall it rests on - is not measured; a boolean that
//               fails is checked again with Revit's element-intersection filter), and
//               each copied parameter.
//   baseboards  one sweep of type_id on every wall face that bounds the room, on
//               the room's side (found by testing a point 50 mm off the face for
//               the room), once per wall and side; an existing sweep of the type on
//               that face is kept. Revit stops a sweep at the door openings in its
//               wall. Re-read: host, type, side and height of each sweep.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class RoomFinishesCommand : ICommand
    {
        public string Name => "horizun_room_finishes";
        public string Description => "Finish floors and baseboards by room: contour from the room's finish faces, type by its use; re-read after commit.";

        private const double Mm = 1.0 / 304.8;

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: room finishes", RoomFinishPlanRules.Validate, Plan, readOperation: "__none__");

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            var rooms = new List<Room>();
            var rejected = new JArray();
            foreach (JToken t in (JArray)r["room_ids"])
            {
                long id = t.Value<long>();
                var room = (Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null) as Room;
                if (room == null) { rejected.Add(new JObject { ["room_id"] = id, ["reason"] = "not a room of this document" }); continue; }
                if (room.Location == null || room.Area <= 0) { rejected.Add(new JObject { ["room_id"] = id, ["reason"] = "not placed or not enclosed (area 0)" }); continue; }
                rooms.Add(room);
            }
            ArchModelEdit edit = r.Value<string>("operation").Trim().ToLowerInvariant() == "floors" ? Floors(doc, r, rooms, rejected) : Baseboards(doc, r, rooms);
            if (r.Value<string>("operation").Trim().ToLowerInvariant() != "floors")
            {
                edit.Summary["rejected_rooms"] = rejected;
                edit.Evidence["rejected_rooms"] = rejected;
                edit.PartialFailures = () => rejected.Count;
            }
            return edit;
        }

        private static IList<IList<BoundarySegment>> Boundary(Room room) =>
            room.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish });

        private static string Text(Element e, string name)
        {
            Parameter p = e.LookupParameter(name);
            if (p == null || !p.HasValue) return null;
            return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        }

        // ---- floors ------------------------------------------------------------------------

        private sealed class FloorPlan
        {
            public Room Room; public long RoomId; public long TypeId; public ElementId Level; public List<CurveLoop> Loops; public double ExpectedFt2;
            public List<long> Walls = new List<long>(); public Dictionary<string, string> Copy = new Dictionary<string, string>(); public long Created = -1;
            /// <summary>The room's own base offset from its level (ROOM_LOWER_OFFSET): offset_mm is measured from where the room sits.</summary>
            public double BaseOffset; public JArray WallChecks;
        }

        private static ArchModelEdit Floors(Document doc, JObject r, List<Room> rooms, JArray rejected)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            string useParam = null;
            if (r["type_by_use"] is JObject u)
            {
                useParam = u.Value<string>("parameter");
                foreach (JProperty p in ((JObject)u["map"]).Properties()) map[p.Name.Trim()] = p.Value.Value<long>();
            }
            long? def = r.Value<long?>("default_type_id");
            foreach (long tid in map.Values.Concat(def.HasValue ? new[] { def.Value } : new long[0]).Distinct())
                if (!(doc.GetElement(Rid.Make(tid)) is FloorType))
                    throw new ArgumentException("type " + tid + " is not a floor type.");
            double offset = (r.Value<double?>("offset_mm") ?? 0) * Mm;
            var copy = (r["copy_parameters"] as JObject)?.Properties().ToDictionary(p => p.Name, p => (string)p.Value) ?? new Dictionary<string, string>();

            var plans = new List<FloorPlan>();
            var skipped = new JArray();
            var existingFloors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().ToList();
            double gapTolerance = (r.Value<double?>("boundary_gap_tolerance_mm") ?? RoomFinishPlanRules.BoundaryGapToleranceMm) * Mm;
            foreach (Room room in rooms)
            {
                try
                {
                string use = useParam == null ? null : Text(room, useParam);
                long? type = RoomFinishPlanRules.TypeFor(use, map, def);
                if (type == null) throw new ArgumentException("no floor type for " + useParam + "='" + (use ?? "") + "': add type_by_use.map or default_type_id");
                var fp = new FloorPlan { Room = room, RoomId = Rid.Value(room.Id), TypeId = type.Value, Level = room.LevelId, Loops = new List<CurveLoop>(),
                    BaseOffset = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET)?.AsDouble() ?? 0 };
                var tess = new List<IList<double[]>>();
                foreach (IList<BoundarySegment> loop in Boundary(room) ?? new List<IList<BoundarySegment>>())
                {
                    var curves = new List<Curve>();
                    var pts = new List<double[]>();
                    foreach (BoundarySegment s in loop)
                    {
                        Curve c = s.GetCurve();
                        curves.Add(c);
                        Element owner = doc.GetElement(s.ElementId);
                        if (owner is Wall && !fp.Walls.Contains(Rid.Value(owner.Id))) fp.Walls.Add(Rid.Value(owner.Id));
                    }
                    curves = OrderedClosedBoundary(curves, gapTolerance);
                    foreach (Curve c in curves)
                    {
                        IList<XYZ> points = c.Tessellate();
                        foreach (XYZ q in points.Take(points.Count - 1)) pts.Add(new[] { q.X, q.Y });
                    }
                    fp.Loops.Add(CurveLoop.Create(curves));
                    tess.Add(pts);
                }
                if (fp.Loops.Count == 0) throw new ArgumentException("no boundary");
                fp.ExpectedFt2 = RoomFinishPlanRules.EnclosedArea(tess);
                JArray overlaps = ExistingFinishFloors(doc, fp, offset, existingFloors);
                if (overlaps.Count > 0)
                {
                    skipped.Add(new JObject { ["room_id"] = fp.RoomId, ["reason"] = "existing_finish_floor_overlap_xy_and_z", ["existing_floors"] = overlaps });
                    continue;
                }
                foreach (var kv in copy)
                {
                    string v = Text(room, kv.Key);
                    if (v == null) throw new ArgumentException("no value for '" + kv.Key + "' to copy");
                    fp.Copy[kv.Value] = v;
                }
                plans.Add(fp);
                }
                catch (Exception ex) { rejected.Add(new JObject { ["room_id"] = Rid.Value(room.Id), ["reason"] = ex.Message }); }
            }
            if (plans.Count == 0)
            {
                var result = new JObject { ["no_op"] = true, ["status"] = rejected.Count > 0 ? "failed" : "already_present", ["rejected_rooms"] = rejected, ["skipped_rooms"] = skipped, ["already_present"] = skipped.Count,
                    ["note"] = "Rooms with an existing finish overlap were omitted; rejected_rooms names invalid plans. Nothing was written." };
                ApplicationOutcome.StampApplied(result, ApplicationOutcome.NotStarted, rejected.Count, 0, 0, 0, rejected.Count, 0);
                return new ArchModelEdit { ReadResult = result };
            }

            var edit = new ArchModelEdit();
            foreach (FloorPlan fp in plans) edit.Planned.Add(ModelEditRunner.Planned(fp.Room, PlannedAction.Modify, r));
            edit.Summary["operation"] = "floors";
            edit.Summary["skipped_rooms"] = skipped;
            edit.Summary["already_present"] = skipped.Count;
            edit.Evidence["skipped_rooms"] = skipped;
            edit.Summary["boundary_gap_tolerance_mm"] = gapTolerance / Mm;
            edit.Summary["rejected_rooms"] = rejected;
            edit.Evidence["rejected_rooms"] = rejected;
            edit.PartialFailures = () => rejected.Count;
            edit.VerifiedItems = () => plans.Count(p => p.Created > 0);
            edit.Summary["floors"] = new JArray(plans.Select(fp => new JObject
            {
                ["room_id"] = fp.RoomId, ["type_id"] = fp.TypeId, ["loops"] = fp.Loops.Count,
                ["area_m2"] = Math.Round(fp.ExpectedFt2 * 0.09290304, 3), ["bounding_walls"] = fp.Walls.Count,
                ["height_offset_mm"] = Math.Round((fp.BaseOffset + offset) / Mm, 1), ["room_base_offset_mm"] = Math.Round(fp.BaseOffset / Mm, 1)
            }));
            edit.Apply = d =>
            {
                foreach (FloorPlan fp in plans)
                {
                    using (var roomTx = new SubTransaction(d))
                    {
                    roomTx.Start();
                    try
                    {
                    Floor f = Floor.Create(d, fp.Loops, Rid.Make(fp.TypeId), fp.Level);
                    fp.Created = Rid.Value(f.Id);
                    Parameter off = f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                    if (off == null || off.IsReadOnly) throw new InvalidOperationException("the floor's height offset is not writable");
                    off.Set(fp.BaseOffset + offset);
                    foreach (var kv in fp.Copy)
                    {
                        Parameter p = f.LookupParameter(kv.Key);
                        if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
                            throw new InvalidOperationException("the floor has no writable text parameter '" + kv.Key + "' to receive the room's value.");
                        p.Set(kv.Value);
                    }
                    d.Regenerate();
                    string problem = FloorProblem(d, fp);
                    if (problem != null) throw new InvalidOperationException(problem);
                    if (roomTx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("room subtransaction did not commit");
                    }
                    catch (Exception ex)
                    {
                        if (ex is Autodesk.Revit.Exceptions.RegenerationFailedException) throw;
                        if (roomTx.GetStatus() == TransactionStatus.Started)
                        {
                            Guard.RollbackResult rolled = Guard.RollBack(roomTx);
                            if (!rolled.Confirmed) throw new InvalidOperationException("room rollback was not confirmed: " + rolled.StatusName, ex);
                        }
                        fp.Created = -1;
                        rejected.Add(new JObject { ["room_id"] = fp.RoomId, ["reason"] = ex.Message, ["wall_checks"] = fp.WallChecks });
                    }
                    }
                }
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "created", "type_id", "level", "area", "inside_walls" };
                if (copy.Count > 0) req.Add("copied_parameters");
                var check = new PostconditionCheck(req.ToArray());
                int created = 0, typed = 0, leveled = 0, area = 0, inside = 0, insideUnread = 0, copied = 0;
                var rows = new JArray();
                foreach (FloorPlan fp in plans.Where(p => p.Created > 0))
                {
                    var f = fp.Created > 0 ? d.GetElement(Rid.Make(fp.Created)) as Floor : null;
                    var row = new JObject { ["room_id"] = fp.RoomId, ["floor_id"] = f == null ? null : (JToken)fp.Created };
                    rows.Add(row);
                    if (f == null) continue;
                    created++;
                    if (Rid.Value(f.GetTypeId()) == fp.TypeId) typed++;
                    if (f.LevelId == fp.Level) leveled++;
                    double? got = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble();
                    row["area_m2"] = got.HasValue ? (JToken)Math.Round(got.Value * 0.09290304, 3) : null;
                    if (got.HasValue && RoomFinishPlanRules.AreaAgrees(fp.ExpectedFt2, got.Value)) area++;
                    bool through = false, unread = false;
                    var walls = new JArray();
                    foreach (long w in FloorWallCandidates(d, f, fp.Walls))
                    {
                        WallFit fit = WallAgainstFloor(d, f, w, out string how);
                        walls.Add(new JObject { ["wall_id"] = w, ["result"] = fit.ToString().ToLowerInvariant(), ["how"] = how });
                        if (fit == WallFit.Unmeasurable) unread = true; else if (fit == WallFit.Through) { through = true; row["runs_through_wall"] = w; }
                    }
                    row["wall_checks"] = walls;
                    row["room_base_offset_mm"] = Math.Round(fp.BaseOffset / Mm, 1);
                    if (unread && !through) insideUnread++; else if (!through) inside++;
                    if (fp.Copy.All(kv => f.LookupParameter(kv.Key)?.AsString() == kv.Value)) copied++;
                }
                int n = plans.Count(p => p.Created > 0);
                check.Compare("created", n, created);
                check.Compare("type_id", n, typed);
                check.Compare("level", n, leveled);
                check.Record("area", n, area, area == n);
                if (insideUnread > 0) check.Unreadable("inside_walls", n, insideUnread + " floor(s) whose shared volume with a bounding wall could not be measured");
                else check.Compare("inside_walls", n, inside);
                if (copy.Count > 0) check.Compare("copied_parameters", n, copied);
                edit.Evidence["rejected_rooms"] = rejected.DeepClone();
                edit.Evidence["floors"] = rows;
                return check;
            };
            return edit;
        }

        /// <summary>Measure actual footprint intersection in the proposed finish's Z band.</summary>
        private static JArray ExistingFinishFloors(Document doc, FloorPlan fp, double offset, List<Floor> floors)
        {
            var rows = new JArray();
            FloorType wanted = (FloorType)doc.GetElement(Rid.Make(fp.TypeId));
            double thickness = wanted.GetCompoundStructure()?.GetWidth() ?? 0;
            if (thickness <= 1e-6) throw new ArgumentException("finish type thickness is not measurable; duplicate prevention cannot be evaluated");
            double top = ((Level)doc.GetElement(fp.Level)).Elevation + fp.BaseOffset + offset;
            double bottom = top - thickness;
            double profileZ = fp.Loops[0].First().GetEndPoint(0).Z;
            var shift = Transform.CreateTranslation(new XYZ(0, 0, bottom - profileZ));
            var loops = fp.Loops.Select(loop => CurveLoop.Create(loop.Select(c => c.CreateTransformed(shift)).ToList())).ToList();
            Solid proposed = GeometryCreationUtilities.CreateExtrusionGeometry(loops, XYZ.BasisZ, thickness);
            foreach (Floor floor in floors)
            {
                var type = doc.GetElement(floor.GetTypeId()) as FloorType;
                bool finishType = floor.GetTypeId() == wanted.Id ||
                    (type?.GetCompoundStructure()?.GetLayers().Any(layer => layer.Function == MaterialFunctionAssignment.Finish1 || layer.Function == MaterialFunctionAssignment.Finish2) ?? false);
                if (!finishType) continue;
                BoundingBoxXYZ box = floor.get_BoundingBox(null);
                if (box == null || !RoomFinishPlanRules.RangesOverlap(bottom, top, box.Min.Z, box.Max.Z)) continue;
                GeometryElement geometry = floor.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                if (geometry == null) throw new ArgumentException("existing finish floor " + Rid.Value(floor.Id) + " geometry is not readable; cannot rule out a duplicate");
                double overlap = 0;
                foreach (Solid solid in geometry.OfType<Solid>().Where(solid => solid.Faces.Size > 0 && solid.Volume > 0))
                {
                    Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(proposed, solid, BooleanOperationsType.Intersect);
                    overlap += intersection?.Volume ?? 0;
                }
                if (overlap > 1e-8)
                    rows.Add(new JObject { ["floor_id"] = Rid.Value(floor.Id), ["type_id"] = Rid.Value(floor.GetTypeId()),
                        ["shared_volume_m3"] = overlap * 0.028316846592, ["plan_overlap_area_m2_in_z_band"] = overlap / thickness * 0.09290304,
                        ["z_band_feet"] = new JArray(bottom, top) });
            }
            return rows;
        }

        private static List<Curve> OrderedClosedBoundary(List<Curve> curves, double tolerance)
        {
            var endpoints = curves.Select(c => new[] { Coordinates(c.GetEndPoint(0)), Coordinates(c.GetEndPoint(1)) }).ToList();
            RoomFinishPlanRules.OrderedBoundary order = RoomFinishPlanRules.OrderBoundary(endpoints, tolerance);
            if (!order.Closed)
                throw new ArgumentException("boundary gap " + (order.MaximumGap / Mm).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) +
                    " mm exceeds boundary_gap_tolerance_mm=" + (tolerance / Mm).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
            var sorted = order.Indices.Select((index, i) => order.Reversed[i] ? curves[index].CreateReversed() : curves[index]).ToList();
            var starts = sorted.Select(c => c.GetEndPoint(0)).ToArray();
            var ends = sorted.Select(c => c.GetEndPoint(1)).ToArray();
            for (int i = 0; i < sorted.Count; i++)
            {
                int next = (i + 1) % sorted.Count;
                XYZ midpoint = (ends[i] + starts[next]) * 0.5;
                ends[i] = midpoint; starts[next] = midpoint;
            }
            for (int i = 0; i < sorted.Count; i++)
            {
                Curve c = sorted[i];
                if (starts[i].DistanceTo(c.GetEndPoint(0)) < 1e-9 && ends[i].DistanceTo(c.GetEndPoint(1)) < 1e-9) continue;
                if (c is Line) sorted[i] = Line.CreateBound(starts[i], ends[i]);
                else if (c is Arc) sorted[i] = Arc.Create(starts[i], ends[i], c.Evaluate(0.5, true));
                else throw new ArgumentException("boundary gap is within tolerance but repair of " + c.GetType().Name + " is not supported");
            }
            return sorted;
        }

        private static double[] Coordinates(XYZ p) => new[] { p.X, p.Y, p.Z };

        private static IReadOnlyList<long> FloorWallCandidates(Document doc, Floor floor, IEnumerable<long> boundaryWalls)
        {
            // Finish panels may be non-room-bounding or hidden behind another boundary owner.
            // A boundary-owner-only check therefore cannot establish that the floor avoids walls.
            BoundingBoxXYZ box = floor.get_BoundingBox(null);
            if (box == null) throw new InvalidOperationException("created floor bounds are unreadable; wall overlap coverage cannot be established");
            var outline = new Outline(box.Min, box.Max);
            IEnumerable<long> nearby = new FilteredElementCollector(doc).OfClass(typeof(Wall))
                .WherePasses(new BoundingBoxIntersectsFilter(outline, 1e-6)).ToElementIds().Select(Rid.Value);
            // Missing bounds are not evidence that an independent wall is clear.
            // Keep these candidates for the solid reader to distinguish empty from unreadable.
            nearby = nearby.Concat(new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.get_BoundingBox(null) == null).Select(w => Rid.Value(w.Id)));
            return RoomFinishPlanRules.WallsForFloorVerification(boundaryWalls, nearby);
        }

        /// <summary>Read each room's created floor before retaining its subtransaction.</summary>
        private static string FloorProblem(Document d, FloorPlan fp)
        {
            var f = d.GetElement(Rid.Make(fp.Created)) as Floor;
            if (f == null) return "created floor could not be re-read";
            if (Rid.Value(f.GetTypeId()) != fp.TypeId || f.LevelId != fp.Level) return "created floor type or level differs";
            double? area = f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble();
            if (!area.HasValue || !RoomFinishPlanRules.AreaAgrees(fp.ExpectedFt2, area.Value)) return "created floor area differs from the closed boundary";
            fp.WallChecks = new JArray();
            foreach (long wall in FloorWallCandidates(d, f, fp.Walls))
            {
                WallFit fit = WallAgainstFloor(d, f, wall, out string how);
                fp.WallChecks.Add(new JObject { ["wall_id"] = wall, ["result"] = fit.ToString().ToLowerInvariant(), ["how"] = how });
                if (fit == WallFit.Unmeasurable) return "shared volume with nearby wall " + wall + " is not measurable (" + how + ")";
                if (fit == WallFit.Through) return "floor runs through nearby wall " + wall + " (" + how + ")";
            }
            if (fp.Copy.Any(kv => f.LookupParameter(kv.Key)?.AsString() != kv.Value)) return "copied parameter differs";
            return null;
        }

        private enum WallFit { Clear, Through, Unmeasurable }

        /// <summary>
        /// One nearby wall against a created floor. The vertical extents settle walls below, above, or
        /// ending inside the floor's thickness. A wall rising above the floor's top is measured by solid
        /// intersection; when Revit's boolean fails on that pair (curtain walls, coplanar faces), Revit's
        /// own element-intersection filter is the second measurement - only its "no intersection" clears.
        /// </summary>
        private static WallFit WallAgainstFloor(Document d, Floor f, long wallId, out string how)
        {
            Element w = d.GetElement(Rid.Make(wallId));
            if (w == null) { how = "wall could not be re-read"; return WallFit.Unmeasurable; }
            BoundingBoxXYZ fb = f.get_BoundingBox(null), wb = w.get_BoundingBox(null);
            if (fb != null && wb != null)
            {
                string relation = RoomFinishPlanRules.WallVerticalRelation(fb.Min.Z, fb.Max.Z, wb.Min.Z, wb.Max.Z, Mm);
                if (relation != null) { how = relation; return WallFit.Clear; }
            }
            double? shared = JoinGeometryCommand.SharedVolumeM3(f, w, out string cause);
            if (shared.HasValue)
            {
                how = "shared_volume_m3=" + Math.Round(shared.Value, 6).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return shared.Value > JoinGeometryRules.OverlapToleranceM3 ? WallFit.Through : WallFit.Clear;
            }
            try
            {
                bool hits = new FilteredElementCollector(d, new List<ElementId> { w.Id }).WherePasses(new ElementIntersectsElementFilter(f)).Any();
                if (!hits) { how = "boolean failed (" + (cause ?? "unknown") + "); Revit's element-intersection check finds no intersection"; return WallFit.Clear; }
                how = "boolean failed (" + (cause ?? "unknown") + ") and Revit's element-intersection check reports contact";
            }
            catch (Exception ex) { how = "boolean failed (" + (cause ?? "unknown") + "); element-intersection check failed: " + ex.Message; }
            return WallFit.Unmeasurable;
        }

        // ---- baseboards -------------------------------------------------------------------

        private sealed class Board { public Wall Wall; public long WallId; public WallSide Side; public List<long> Rooms = new List<long>(); public long Created = -1; }

        private static ArchModelEdit Baseboards(Document doc, JObject r, List<Room> rooms)
        {
            var type = doc.GetElement(Rid.Make(r.Value<long>("type_id"))) as ElementType;
            if (type?.Category == null || type.Category.Id != new ElementId(BuiltInCategory.OST_Cornices))
                throw new ArgumentException("type_id is not a wall sweep type (horizun_wall_sweep operation=read lists them).");
            double height = (r.Value<double?>("height_mm") ?? 0) * Mm;
            var boards = new Dictionary<string, Board>(StringComparer.Ordinal);
            var skipped = new JArray();
            foreach (Room room in rooms)
            {
                double z = (doc.GetElement(room.LevelId) as Level)?.Elevation + 500 * Mm ?? 0;
                foreach (IList<BoundarySegment> loop in Boundary(room))
                    foreach (BoundarySegment s in loop)
                    {
                        var wall = doc.GetElement(s.ElementId) as Wall;
                        if (wall == null) continue;
                        long wid = Rid.Value(wall.Id);
                        if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Line)) { skipped.Add(new JObject { ["wall_id"] = wid, ["reason"] = "not a straight wall" }); continue; }
                        if (!WallSweep.WallAllowsWallSweep(wall)) { skipped.Add(new JObject { ["wall_id"] = wid, ["reason"] = "cannot host a sweep (curtain, or stacked)" }); continue; }
                        Curve c = s.GetCurve();
                        XYZ mid = c.Evaluate(0.5, true), n = wall.Orientation;
                        XYZ outP = new XYZ(mid.X + n.X * 50 * Mm, mid.Y + n.Y * 50 * Mm, z), inP = new XYZ(mid.X - n.X * 50 * Mm, mid.Y - n.Y * 50 * Mm, z);
                        WallSide? side = room.IsPointInRoom(outP) ? WallSide.Exterior : room.IsPointInRoom(inP) ? WallSide.Interior : (WallSide?)null;
                        if (side == null) { skipped.Add(new JObject { ["wall_id"] = wid, ["room_id"] = Rid.Value(room.Id), ["reason"] = "the room could not be found on either side of the face" }); continue; }
                        string key = wid + "/" + side;
                        if (!boards.TryGetValue(key, out Board b)) boards[key] = b = new Board { Wall = wall, WallId = wid, Side = side.Value };
                        if (!b.Rooms.Contains(Rid.Value(room.Id))) b.Rooms.Add(Rid.Value(room.Id));
                    }
            }
            var existing = new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Cast<WallSweep>().Where(w => w.GetTypeId() == type.Id).ToList();
            int kept = 0;
            foreach (string key in boards.Keys.ToList())
            {
                Board b = boards[key];
                if (existing.Any(w => { try { return w.GetHostIds().Contains(b.Wall.Id) && w.GetWallSweepInfo().WallSide == b.Side; } catch { return false; } }))
                { boards.Remove(key); kept++; }
            }
            if (boards.Count == 0)
                return new ArchModelEdit { ReadResult = new JObject { ["no_op"] = true, ["already_present"] = kept, ["skipped"] = skipped,
                    ["note"] = "Every bounding face already carries this baseboard, or none can host one; nothing was written." } };
            var list = boards.Values.ToList();
            var edit = new ArchModelEdit();
            foreach (Board b in list) edit.Planned.Add(ModelEditRunner.Planned(b.Wall, PlannedAction.Modify, r));
            edit.Summary["operation"] = "baseboards";
            edit.Summary["sweeps"] = list.Count;
            edit.Summary["already_present"] = kept;
            edit.Summary["faces"] = new JArray(list.Take(200).Select(b => new JObject { ["wall_id"] = b.WallId, ["side"] = b.Side == WallSide.Interior ? "interior" : "exterior", ["rooms"] = new JArray(b.Rooms) }));
            edit.Summary["skipped"] = skipped;
            edit.Apply = d =>
            {
                foreach (Board b in list)
                {
                    var info = new WallSweepInfo(WallSweepType.Sweep, false) { Distance = height, DistanceMeasuredFrom = DistanceMeasuredFrom.Base, WallSide = b.Side };
                    WallSweep s = WallSweep.Create((Wall)d.GetElement(b.Wall.Id), type.Id, info);
                    if (s == null) throw new InvalidOperationException("Revit created no sweep on wall " + b.WallId + ".");
                    b.Created = Rid.Value(s.Id);
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("created", "host", "type_id", "side", "height");
                int created = 0, hosted = 0, typed = 0, sided = 0, high = 0;
                var rows = new JArray();
                foreach (Board b in list)
                {
                    var s = b.Created > 0 ? d.GetElement(Rid.Make(b.Created)) as WallSweep : null;
                    rows.Add(new JObject { ["wall_id"] = b.WallId, ["sweep_id"] = s == null ? null : (JToken)b.Created, ["rooms"] = new JArray(b.Rooms) });
                    if (s == null) continue;
                    created++;
                    try { if (s.GetHostIds().Contains(b.Wall.Id)) hosted++; } catch { }
                    if (s.GetTypeId() == type.Id) typed++;
                    try
                    {
                        WallSweepInfo info = s.GetWallSweepInfo();
                        if (info.WallSide == b.Side) sided++;
                        if (Math.Abs(info.Distance - height) <= 0.5 * Mm && info.DistanceMeasuredFrom == DistanceMeasuredFrom.Base) high++;
                    }
                    catch { }
                }
                int n = list.Count;
                check.Compare("created", n, created);
                check.Compare("host", n, hosted);
                check.Compare("type_id", n, typed);
                check.Compare("side", n, sided);
                check.Compare("height", n, high);
                edit.Evidence["sweeps"] = rows;
                return check;
            };
            return edit;
        }
    }
}
