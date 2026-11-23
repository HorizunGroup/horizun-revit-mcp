using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>
    /// MODEL COHERENCE: the absurdities a person sees in a 3D view and no other finding
    /// measured - a door that opens onto nothing, a balcony nobody can reach, a railing
    /// with no infill, a room with no door or no floor finish, furniture left in the
    /// default grey, a beam that stops short of its support, sections that change along
    /// one axis, concrete counted twice. Read-only: no transaction is opened.
    ///
    /// Every check runs on a time budget. What the budget did not reach is counted as
    /// unreadable, so coverage_complete is false and the count is a lower bound - a
    /// check that ran out of time never reads as clean.
    /// </summary>
    public partial class AuditModelCommand
    {
        private const double FeetPerMm = 1.0 / 304.8;
        private static readonly TimeSpan CoherenceBudget = TimeSpan.FromSeconds(45);

        private static readonly BuiltInCategory[] SupportSurfaceCategories =
        {
            BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Topography, BuiltInCategory.OST_Ramps
        };

        private static readonly BuiltInCategory[] StructuralCategories =
        {
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_StructuralFoundation
        };

        private static readonly BuiltInCategory[] FurnishingCategories =
        {
            BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_SpecialityEquipment,
            BuiltInCategory.OST_LightingFixtures
        };

        /// <summary>The support categories plus the toposolid, which exists only from 2024 on.</summary>
        private static ElementMulticategoryFilter SupportFilter()
        {
            var cats = new List<BuiltInCategory>(SupportSurfaceCategories);
            if (Enum.TryParse("OST_Toposolid", out BuiltInCategory topo)) cats.Add(topo);
            return new ElementMulticategoryFilter(cats);
        }

        private static JObject Ident(Element e)
        {
            string uid = null;
            try { uid = e.UniqueId; } catch { }
            return new JObject
            {
                ["element_id"] = Rid.Value(e.Id), ["unique_id"] = uid,
                ["category"] = e.Category?.Name, ["built_in_category"] = CategoryKey.Of(e), ["name"] = SafeName(e)
            };
        }

        private static List<Solid> SolidsOf(Element e)
        {
            var solids = new List<Solid>();
            GeometryElement geo = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false });
            if (geo == null) return solids;
            foreach (GeometryObject g in geo)
            {
                if (g is Solid s && s.Volume > 1e-9) solids.Add(s);
                else if (g is GeometryInstance gi)
                    foreach (GeometryObject ig in gi.GetInstanceGeometry())
                        if (ig is Solid si && si.Volume > 1e-9) solids.Add(si);
            }
            return solids;
        }

        /// <summary>
        /// Highest point below <paramref name="from"/> (within <paramref name="depthFeet"/>) where a
        /// vertical line meets a support element's solid, or null when nothing is there. Geometry,
        /// not a view: a hidden category or a section box cannot make a floor disappear.
        /// </summary>
        private static double? SurfaceBelow(Document doc, XYZ from, double depthFeet, ElementFilter categories,
                                           out Element hit, ICollection<ElementId> exclude = null)
        {
            hit = null;
            double r = 5 * FeetPerMm;
            var box = new Outline(new XYZ(from.X - r, from.Y - r, from.Z - depthFeet), new XYZ(from.X + r, from.Y + r, from.Z));
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(categories).WherePasses(new BoundingBoxIntersectsFilter(box));
            if (exclude != null && exclude.Count > 0) collector = collector.Excluding(exclude);
            Line line = Line.CreateBound(from, new XYZ(from.X, from.Y, from.Z - depthFeet));
            double? best = null;
            foreach (Element e in collector)
                foreach (Solid s in SolidsOf(e))
                {
                    SolidCurveIntersection hits = s.IntersectWithCurve(line, new SolidCurveIntersectionOptions());
                    for (int i = 0; i < hits.SegmentCount; i++)
                    {
                        double z = Math.Max(hits.GetCurveSegment(i).GetEndPoint(0).Z, hits.GetCurveSegment(i).GetEndPoint(1).Z);
                        if (!best.HasValue || z > best.Value) { best = z; hit = e; }
                    }
                }
            return best;
        }

        private sealed class Budget
        {
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            public bool Spent => _watch.Elapsed > CoherenceBudget;
        }

        private static JObject CoherenceFinding(string check, List<JObject> hits, int top, int unreadable, string summary,
                                                string means, string fixHint, bool budgetSpent)
        {
            JObject f = Finding(check, hits.Count != 0, hits.Count, summary, new JArray(hits.Take(top)), hits.Count, unreadable);
            f["means"] = means;
            f["fix_hint"] = fixHint;
            if (budgetSpent)
                f["budget_note"] = "The " + CoherenceBudget.TotalSeconds + " s budget ran out; the elements not reached are " +
                                   "counted as unreadable, so the count is a lower bound.";
            return f;
        }

        // ---- 1. doors_to_void ------------------------------------------------

        private static JObject DoorsToVoid(Document doc, int top)
        {
            var budget = new Budget();
            ElementFilter support = SupportFilter();
            var hits = new List<JObject>();
            int unreadable = 0;
            List<FamilyInstance> doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            for (int i = 0; i < doors.Count; i++)
            {
                if (budget.Spent) { unreadable += doors.Count - i; break; }
                FamilyInstance door = doors[i];
                try
                {
                    var wall = door.Host as Wall;
                    var lp = door.Location as LocationPoint;
                    if (wall == null || lp == null)
                    {
                        JObject row = Ident(door);
                        row["problem"] = wall == null ? "no_host_wall" : "no_location";
                        hits.Add(row);
                        continue;
                    }
                    XYZ facing = new XYZ(door.FacingOrientation.X, door.FacingOrientation.Y, 0);
                    if (facing.GetLength() < 1e-9) { unreadable++; continue; }
                    facing = facing.Normalize();
                    double reach = wall.Width / 2 + AuditCoherenceRules.DoorSideOffsetMm * FeetPerMm;
                    double probeTop = 100 * FeetPerMm;
                    var sides = new JArray();
                    foreach (int side in new[] { 1, -1 })
                    {
                        XYZ p = lp.Point + facing * (side * reach) + new XYZ(0, 0, probeTop);
                        double? z = SurfaceBelow(doc, p, 20.0, support, out Element under);
                        double? dropMm = z.HasValue ? (lp.Point.Z - z.Value) / FeetPerMm : (double?)null;
                        if (!z.HasValue || AuditCoherenceRules.IsDropBeyondTolerance(dropMm.Value))
                            sides.Add(new JObject
                            {
                                ["side"] = side > 0 ? "facing" : "back",
                                ["drop_mm"] = dropMm.HasValue ? (JToken)Math.Round(dropMm.Value) : null,
                                ["surface_below_id"] = under == null ? null : (JToken)Rid.Value(under.Id),
                                ["note"] = z.HasValue ? null : "nothing below within 6 m"
                            });
                    }
                    if (sides.Count == 0) continue;
                    JObject hit = Ident(door);
                    hit["problem"] = "opens_to_void";
                    hit["host_wall_id"] = Rid.Value(wall.Id);
                    hit["void_sides"] = sides;
                    hits.Add(hit);
                }
                catch { unreadable++; }
            }
            return CoherenceFinding(AuditCheckNames.DoorsToVoid, hits, top, unreadable,
                hits.Count + " door(s) have no wall host or open onto a drop of more than " +
                AuditCoherenceRules.DoorDropToleranceMm + " mm (or onto nothing) " + AuditCoherenceRules.DoorSideOffsetMm +
                " mm in front of the wall.",
                "A door must sit in a wall and open onto a floor, terrain or stair at its own level on both sides.",
                "Move or delete the door (horizun_transform_elements / horizun_delete_verified), or build the floor, " +
                "terrace and railing it opens onto (horizun_create_elements, horizun_create_railing).",
                budget.Spent);
        }

        // ---- 2. balcony_without_access + 3. railing_without_infill ---------------

        private static List<Railing> FreeRailings(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Railing)).Cast<Railing>()
                .Where(r =>
                {
                    Element host = r.HostId == ElementId.InvalidElementId ? null : doc.GetElement(r.HostId);
                    return host == null || !(host is Stairs || host is Autodesk.Revit.DB.Architecture.StairsRun ||
                                             host is StairsLanding);
                }).ToList();
        }

        private static JObject BalconyWithoutAccess(Document doc, int top)
        {
            var budget = new Budget();
            ElementFilter support = SupportFilter();
            var hits = new List<JObject>();
            int unreadable = 0;

            // Every door's two thresholds, and the surface each one stands on.
            var doorSurfaces = new Dictionary<long, List<long>>();
            foreach (FamilyInstance door in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors)
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                if (budget.Spent) break;
                try
                {
                    var wall = door.Host as Wall;
                    var lp = door.Location as LocationPoint;
                    if (wall == null || lp == null) continue;
                    XYZ facing = new XYZ(door.FacingOrientation.X, door.FacingOrientation.Y, 0);
                    if (facing.GetLength() < 1e-9) continue;
                    facing = facing.Normalize();
                    double reach = wall.Width / 2 + AuditCoherenceRules.BalconyDoorDistanceMm * FeetPerMm / 2;
                    foreach (int side in new[] { 1, -1 })
                    {
                        XYZ p = lp.Point + facing * (side * reach) + new XYZ(0, 0, 100 * FeetPerMm);
                        if (SurfaceBelow(doc, p, AuditCoherenceRules.DoorDropToleranceMm * FeetPerMm, support, out Element under) == null || under == null)
                            continue;
                        long key = Rid.Value(under.Id);
                        if (!doorSurfaces.TryGetValue(key, out var list)) doorSurfaces[key] = list = new List<long>();
                        list.Add(Rid.Value(door.Id));
                    }
                }
                catch { /* a door that cannot be read gives no access; the railing below decides */ }
            }

            List<Railing> railings = FreeRailings(doc);
            for (int i = 0; i < railings.Count; i++)
            {
                if (budget.Spent) { unreadable += railings.Count - i; break; }
                Railing railing = railings[i];
                try
                {
                    IList<Curve> path = railing.GetPath();
                    if (path == null || path.Count == 0) { unreadable++; continue; }
                    Curve mid = path[path.Count / 2];
                    XYZ m = mid.Evaluate(0.5, true);
                    BoundingBoxXYZ box = railing.get_BoundingBox(null);
                    double startZ = (box != null ? box.Min.Z : m.Z) + 100 * FeetPerMm;
                    double? z = SurfaceBelow(doc, new XYZ(m.X, m.Y, startZ), 400 * FeetPerMm, support, out Element under);
                    if (z == null || under == null) continue;   // a railing over nothing is doors_to_void's / the reviewer's
                    if (under.Category == null || Rid.Value(under.Category.Id) != (long)BuiltInCategory.OST_Floors) continue;
                    if (doorSurfaces.ContainsKey(Rid.Value(under.Id))) continue;
                    JObject hit = Ident(railing);
                    hit["slab"] = Ident(under);
                    hit["slab_top_mm"] = Math.Round(z.Value / FeetPerMm);
                    hits.Add(hit);
                }
                catch { unreadable++; }
            }
            return CoherenceFinding(AuditCheckNames.BalconyWithoutAccess, hits, top, unreadable,
                hits.Count + " railing(s) stand on a floor slab that no door opens onto.",
                "A slab with a railing is a balcony or terrace; without a door onto it nobody can reach it. A balcony " +
                "modelled as part of the main slab cannot be told apart and is not reported.",
                "Add or move a door onto the slab (horizun_create_elements / horizun_transform_elements), or delete " +
                "the balcony and its railing (horizun_delete_verified).",
                budget.Spent);
        }

        private static JObject RailingWithoutInfill(Document doc, int top)
        {
            var hits = new List<JObject>();
            int unreadable = 0;
            var byType = new Dictionary<long, List<Railing>>();
            foreach (Railing r in FreeRailings(doc))
            {
                long t = Rid.Value(r.GetTypeId());
                if (!byType.TryGetValue(t, out var list)) byType[t] = list = new List<Railing>();
                list.Add(r);
            }
            foreach (var pair in byType)
            {
                try
                {
                    var type = doc.GetElement(pair.Value[0].GetTypeId()) as RailingType;
                    if (type == null) { unreadable += pair.Value.Count; continue; }
                    int balusters = 0;
                    BalusterPattern pattern = type.BalusterPlacement?.BalusterPattern;
                    if (pattern != null)
                        for (int i = 0; i < pattern.GetBalusterCount(); i++)
                        {
                            BalusterInfo b = pattern.GetBaluster(i);
                            if (b != null && b.BalusterFamilyId != ElementId.InvalidElementId) balusters++;
                        }
                    int rails = type.RailStructure?.GetNonContinuousRailCount() ?? 0;
                    if (AuditCoherenceRules.HasRailingInfill(balusters > 0, balusters, false, false, 0)) continue;
                    hits.Add(new JObject
                    {
                        ["type_id"] = pair.Key, ["type_name"] = SafeName(type),
                        ["instances"] = pair.Value.Count,
                        ["instance_ids"] = new JArray(pair.Value.Take(20).Select(r => Rid.Value(r.Id))),
                        ["balusters_in_pattern"] = balusters, ["horizontal_rails"] = rails,
                        ["problem"] = rails > 0 ? "horizontal_rails_only" : "handrail_only"
                    });
                }
                catch { unreadable += pair.Value.Count; }
            }
            return CoherenceFinding(AuditCheckNames.RailingWithoutInfill, hits, top, unreadable,
                hits.Count + " railing type(s) have no baluster, panel or glass in their pattern - only rails.",
                "A guard needs infill: balusters closer than 100 mm, a panel or glass. Horizontal rails alone can be climbed.",
                "Change the railing to a type with balusters or glass panels (horizun_create_railing, or change its type " +
                "with horizun_transform_elements change_type).",
                false);
        }

        // ---- 4. rooms_without_door + 5. rooms_without_floor_finish ---------------

        private static List<Room> PlacedRooms(Document doc)
        {
            return new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.Location != null && r.Area > 1e-6).ToList();
        }

        private static JObject RoomsWithoutDoor(Document doc, int top)
        {
            var hits = new List<JObject>();
            var open = new List<JObject>();
            int unreadable = 0;
            var from = new List<string>();
            var to = new List<string>();
            foreach (FamilyInstance door in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors)
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                // Every phase: a room of an earlier phase is reached by a door only in that phase.
                foreach (Phase phase in doc.Phases)
                {
                    try { Room r = door.get_FromRoom(phase); if (r != null) from.Add(r.UniqueId); } catch { }
                    try { Room r = door.get_ToRoom(phase); if (r != null) to.Add(r.UniqueId); } catch { }
                }
            }
            var options = new SpatialElementBoundaryOptions();
            foreach (Room room in PlacedRooms(doc))
            {
                try
                {
                    if (AuditCoherenceRules.HasRoomDoor(from, to, room.UniqueId)) continue;
                    bool separated = false;
                    foreach (IList<BoundarySegment> loop in room.GetBoundarySegments(options) ?? new List<IList<BoundarySegment>>())
                        foreach (BoundarySegment seg in loop)
                        {
                            Element e = doc.GetElement(seg.ElementId);
                            if (e?.Category != null && Rid.Value(e.Category.Id) == (long)BuiltInCategory.OST_RoomSeparationLines)
                                separated = true;
                        }
                    JObject row = Ident(room);
                    row["number"] = room.Number;
                    row["area_m2"] = Math.Round(room.Area * 0.09290304, 2);
                    row["level"] = room.Level?.Name;
                    if (separated) { row["note"] = "bounded in part by a room separation line: reached through an open side"; open.Add(row); }
                    else hits.Add(row);
                }
                catch { unreadable++; }
            }
            JObject f = CoherenceFinding(AuditCheckNames.RoomsWithoutDoor, hits, top, unreadable,
                hits.Count + " room(s) are not the From or To room of any door and have no open side; " + open.Count +
                " more are reached only through a room separation line.",
                "Every room needs a way in. Openings in walls without a door family are not seen; check those rooms in plan.",
                "Add a door into the room (horizun_create_elements kind=door) or join it to its neighbour.",
                false);
            f["open_side_only"] = new JArray(open.Take(top));
            f["open_side_only_total"] = open.Count;
            return f;
        }

        private static JObject RoomsWithoutFloorFinish(Document doc, int top)
        {
            var budget = new Budget();
            var noFloor = new List<JObject>();
            var noFinish = new List<JObject>();
            int unreadable = 0;
            var floors = new ElementCategoryFilter(BuiltInCategory.OST_Floors);
            List<Room> rooms = PlacedRooms(doc);
            for (int i = 0; i < rooms.Count; i++)
            {
                if (budget.Spent) { unreadable += rooms.Count - i; break; }
                Room room = rooms[i];
                try
                {
                    var lp = room.Location as LocationPoint;
                    if (lp == null || room.Level == null) { unreadable++; continue; }
                    double floorZ = room.Level.Elevation + room.BaseOffset;
                    double tol = AuditCoherenceRules.FloorFinishToleranceMm * FeetPerMm;
                    var from = new XYZ(lp.Point.X, lp.Point.Y, floorZ + tol);
                    double? z = SurfaceBelow(doc, from, 2 * tol, floors, out Element floor);
                    JObject row = Ident(room);
                    row["number"] = room.Number;
                    row["level"] = room.Level.Name;
                    if (z == null || floor == null) { noFloor.Add(row); continue; }
                    var type = doc.GetElement(floor.GetTypeId()) as HostObjAttributes;
                    CompoundStructure cs = type?.GetCompoundStructure();
                    bool finish = cs != null && cs.GetLayers().Any(l =>
                        l.Function == MaterialFunctionAssignment.Finish1 || l.Function == MaterialFunctionAssignment.Finish2);
                    if (finish) continue;
                    row["floor"] = Ident(floor);
                    row["floor_type"] = SafeName(type);
                    noFinish.Add(row);
                }
                catch { unreadable++; }
            }
            var hits = noFloor.Concat(noFinish).ToList();
            JObject f = CoherenceFinding(AuditCheckNames.RoomsWithoutFloorFinish, hits, top, unreadable,
                noFloor.Count + " room(s) have no floor within " + AuditCoherenceRules.FloorFinishToleranceMm +
                " mm of their level at the room point; " + noFinish.Count + " stand on a floor type with no finish layer.",
                "A room is finished when a floor with a finish layer is under it. Only the room's location point is " +
                "sampled; a floor that covers part of the room counts.",
                "Add a finish floor per room (horizun_room_finishes) or change the slab type to one with a finish layer.",
                budget.Spent);
            f["parts"] = new JObject { ["no_floor"] = noFloor.Count, ["no_finish_layer"] = noFinish.Count };
            return f;
        }

        // ---- 6. default_material_furniture -----------------------------------

        private static JObject DefaultMaterialFurniture(Document doc, int top)
        {
            var hits = new List<JObject>();
            int unreadable = 0;
            var groups = new Dictionary<long, JObject>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(FurnishingCategories))
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                try
                {
                    long typeKey = Rid.Value(fi.GetTypeId());
                    if (groups.TryGetValue(typeKey, out JObject seen))
                    {
                        if (seen != null) seen["instances"] = (int)seen["instances"] + 1;
                        continue;
                    }
                    ICollection<ElementId> ids = fi.GetMaterialIds(false);
                    string reason = null;
                    var names = new JArray();
                    if (ids == null || ids.Count == 0)
                    {
                        Material catMat = fi.Category?.Material;
                        if (catMat == null) reason = "no material: geometry by category and the category has none";
                        else if (IsGreyDefault(catMat)) reason = "by category: " + catMat.Name;
                        names.Add(catMat?.Name);
                    }
                    else
                    {
                        var mats = ids.Select(id => doc.GetElement(id) as Material).Where(mt => mt != null).ToList();
                        foreach (Material mt in mats) names.Add(mt.Name);
                        if (mats.Count > 0 && mats.All(IsGreyDefault)) reason = "every material is a default or neutral grey";
                    }
                    if (reason == null) { groups[typeKey] = null; continue; }
                    var type = doc.GetElement(fi.GetTypeId()) as FamilySymbol;
                    var row = new JObject
                    {
                        ["type_id"] = typeKey, ["family"] = type?.FamilyName, ["type_name"] = SafeName(type),
                        ["category"] = fi.Category?.Name, ["built_in_category"] = CategoryKey.Of(fi), ["instances"] = 1, ["example_id"] = Rid.Value(fi.Id),
                        ["example_unique_id"] = fi.UniqueId, ["materials"] = names, ["reason"] = reason
                    };
                    groups[typeKey] = row;
                    hits.Add(row);
                }
                catch { unreadable++; }
            }
            int instances = hits.Sum(h => (int)h["instances"]);
            return CoherenceFinding(AuditCheckNames.DefaultMaterialFurniture, hits, top, unreadable,
                hits.Count + " furniture/fixture type(s) (" + instances + " instance(s)) render in the default or a neutral grey material.",
                "Grey default materials make a model look unfinished in every render and view.",
                "Assign real materials to the family's material parameters or subcategories (horizun_manage_materials, " +
                "horizun_write_params_verified on the type).",
                false);
        }

        private static bool IsGreyDefault(Material m)
        {
            if (m == null) return true;
            Color c = null;
            try { c = m.Color; } catch { }
            bool hasAppearance = m.AppearanceAssetId != null && m.AppearanceAssetId != ElementId.InvalidElementId;
            return AuditCoherenceRules.IsDefaultOrNeutralGrey(m.Name,
                c != null && c.IsValid ? c.Red : (int?)null, c != null && c.IsValid ? c.Green : (int?)null,
                c != null && c.IsValid ? c.Blue : (int?)null, hasAppearance);
        }

        // ---- 7. framing_not_reaching_support ---------------------------------

        private static readonly double[] GapStepsMm = { 25, 50, 100, 200, 400, 800 };

        /// <summary>One end of a member: short rays leaving its real end face (and, where a support can sit
        /// beside or under it, its sides or bottom near that end).</summary>
        private sealed class MemberEnd
        {
            public string Name;
            public readonly List<KeyValuePair<XYZ, XYZ>> Rays = new List<KeyValuePair<XYZ, XYZ>>();
        }

        /// <summary>
        /// The ends of a member read from its SOLID, not from its location line: a beam's line runs to the
        /// column axis, inside the node, where the concrete may belong to nobody (a wall cutting the column
        /// leaves a hollow core). Each end face is sampled at its centre and at four points 90 % of the way
        /// to its edges, so a thin remaining shell of the support is still found.
        /// </summary>
        private static List<MemberEnd> EndsOf(List<Solid> solids, XYZ axis, bool isBeam)
        {
            XYZ d = axis.Normalize();
            XYZ u = Math.Abs(d.Z) > 0.9 ? XYZ.BasisX : d.CrossProduct(XYZ.BasisZ).Normalize();
            XYZ v = d.CrossProduct(u).Normalize();
            XYZ origin = null;
            double tMin = double.MaxValue, tMax = double.MinValue, aMin = double.MaxValue, aMax = double.MinValue,
                bMin = double.MaxValue, bMax = double.MinValue;
            // Face meshes, not edges: on a joined member Revit can return edges of another cut state
            // (a column whose faces reach 5850 had no edge above 5350).
            foreach (Solid s in solids)
                foreach (Face f in s.Faces)
                    foreach (XYZ p in f.Triangulate().Vertices)
                    {
                        if (origin == null) origin = p;
                        XYZ r = p - origin;
                        double t = r.DotProduct(d), a = r.DotProduct(u), b = r.DotProduct(v);
                        tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t);
                        aMin = Math.Min(aMin, a); aMax = Math.Max(aMax, a);
                        bMin = Math.Min(bMin, b); bMax = Math.Max(bMax, b);
                    }
            if (origin == null || tMax - tMin < 1e-6) return null;
            double aMid = (aMin + aMax) / 2, bMid = (bMin + bMax) / 2, aw = 0.45 * (aMax - aMin), bw = 0.45 * (bMax - bMin);
            var face = new[] { new[] { 0.0, 0.0 }, new[] { aw, bw }, new[] { aw, -bw }, new[] { -aw, bw }, new[] { -aw, -bw } };
            XYZ At(double t, double a, double b) => origin + d * t + u * a + v * b;
            double near = Math.Min(100 * FeetPerMm, (tMax - tMin) / 4);
            var ends = new List<MemberEnd>();
            foreach (bool atStart in new[] { true, false })
            {
                var end = new MemberEnd { Name = isBeam ? (atStart ? "start" : "end") : (atStart ? "base" : "top") };
                double t = atStart ? tMin : tMax;
                XYZ outward = atStart ? -d : d;
                foreach (double[] o in face) end.Rays.Add(new KeyValuePair<XYZ, XYZ>(At(t, aMid + o[0], bMid + o[1]), outward));
                double tIn = atStart ? tMin + near : tMax - near;
                if (isBeam)
                {
                    // A beam resting on a wall or column below its end: rays down from its bottom face.
                    XYZ down = v.Z < 0 ? v : -v;
                    double bBottom = v.Z < 0 ? bMax : bMin;
                    foreach (double a in new[] { aMid, aMid + aw, aMid - aw }) end.Rays.Add(new KeyValuePair<XYZ, XYZ>(At(tIn, a, bBottom), down));
                }
                else if (!atStart)
                {
                    // A column top reached by beams framing in from the sides, under no slab.
                    end.Rays.Add(new KeyValuePair<XYZ, XYZ>(At(tIn, aMax, bMid), u));
                    end.Rays.Add(new KeyValuePair<XYZ, XYZ>(At(tIn, aMin, bMid), -u));
                    end.Rays.Add(new KeyValuePair<XYZ, XYZ>(At(tIn, aMid, bMax), v));
                    end.Rays.Add(new KeyValuePair<XYZ, XYZ>(At(tIn, aMid, bMin), -v));
                }
                ends.Add(end);
            }
            return ends;
        }

        /// <summary>
        /// null when a ray from the end reaches a support within tolerance; else the smallest reach that met
        /// one, or -1. Each ray starts 5 mm inside the member and is intersected with the candidates' real
        /// solids, so a ray wholly inside a support still counts (a probe cube wholly inside one is not
        /// seen by ElementIntersectsSolidFilter). A neighbour whose solid Revit cannot intersect (not a
        /// closed volume) is listed in <paramref name="unmeasured"/>.
        /// </summary>
        private static double? GapToSupport(Document doc, MemberEnd end, ElementFilter categories, ElementId self,
                                            Dictionary<ElementId, List<Solid>> cache, HashSet<long> unmeasured)
        {
            var opt = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
            double inset = 5 * FeetPerMm;
            var reaches = new List<double> { AuditCoherenceRules.SupportToleranceMm };
            reaches.AddRange(GapStepsMm);
            foreach (double reachMm in reaches)
            {
                double reach = reachMm * FeetPerMm;
                double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
                var lines = new List<Line>();
                foreach (var ray in end.Rays)
                {
                    XYZ a = ray.Key - ray.Value * inset, b = ray.Key + ray.Value * reach;
                    lines.Add(Line.CreateBound(a, b));
                    foreach (XYZ p in new[] { a, b })
                    {
                        x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                        x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
                    }
                }
                double pad = 1 * FeetPerMm;
                var outline = new Outline(new XYZ(x0 - pad, y0 - pad, z0 - pad), new XYZ(x1 + pad, y1 + pad, z1 + pad));
                foreach (Element c in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(categories)
                             .Excluding(new List<ElementId> { self }).WherePasses(new BoundingBoxIntersectsFilter(outline)))
                {
                    if (!cache.TryGetValue(c.Id, out List<Solid> solids)) cache[c.Id] = solids = SolidsOf(c);
                    foreach (Solid s in solids)
                        foreach (Line line in lines)
                        {
                            try
                            {
                                SolidCurveIntersection hit = s.IntersectWithCurve(line, opt);
                                if (hit != null && hit.SegmentCount > 0)
                                    return reachMm <= AuditCoherenceRules.SupportToleranceMm ? (double?)null : reachMm;
                            }
                            catch (Autodesk.Revit.Exceptions.ArgumentException) { unmeasured.Add(Rid.Value(c.Id)); break; }
                        }
                }
            }
            return -1;
        }

        private static JObject FramingNotReachingSupport(Document doc, int top)
        {
            var budget = new Budget();
            var hits = new List<JObject>();
            int unreadable = 0;
            var beamSupport = new ElementMulticategoryFilter(new[] {
                // No floors: a beam's top sits at the slab's level, so every end would "touch" it.
                BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns, BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation });
            var columnSupport = new ElementMulticategoryFilter(new[] {
                BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Roofs });
            var members = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(new[] {
                    BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns }))
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            var cache = new Dictionary<ElementId, List<Solid>>();
            for (int i = 0; i < members.Count; i++)
            {
                if (budget.Spent) { unreadable += members.Count - i; break; }
                FamilyInstance m = members[i];
                try
                {
                    bool isBeam = Rid.Value(m.Category.Id) == (long)BuiltInCategory.OST_StructuralFraming;
                    XYZ axis;
                    if (isBeam)
                    {
                        var lc = m.Location as LocationCurve;
                        if (lc?.Curve == null || !lc.Curve.IsBound) { unreadable++; continue; }
                        axis = lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0);
                    }
                    else
                    {
                        // A slanted column's axis is its curve; a vertical one has a point.
                        axis = m.Location is LocationCurve cc && cc.Curve != null && cc.Curve.IsBound
                            ? cc.Curve.GetEndPoint(1) - cc.Curve.GetEndPoint(0) : XYZ.BasisZ;
                    }
                    List<Solid> own = SolidsOf(m);
                    List<MemberEnd> ends = own.Count == 0 || axis.GetLength() < 1e-9 ? null : EndsOf(own, axis, isBeam);
                    if (ends == null) { unreadable++; continue; }
                    var gaps = new JArray();
                    foreach (MemberEnd end in ends)
                    {
                        var unmeasured = new HashSet<long>();
                        double? gap = GapToSupport(doc, end, isBeam ? beamSupport : columnSupport, m.Id, cache, unmeasured);
                        if (gap == null) continue;
                        var row = new JObject
                        {
                            ["end"] = end.Name,
                            ["gap_mm_at_most"] = gap.Value < 0 ? null : (JToken)gap.Value,
                            ["note"] = gap.Value < 0 ? "no support within 800 mm" : null
                        };
                        if (unmeasured.Count > 0)
                        {
                            // Not a proven gap: the support may be the element whose solid could not be read.
                            row["unmeasured_neighbour_ids"] = new JArray(unmeasured.OrderBy(x => x));
                            row["note"] = "undetermined: the solid of a neighbouring element is not a closed volume, so contact with it cannot be measured";
                        }
                        gaps.Add(row);
                    }
                    if (gaps.Count == 0) continue;
                    JObject hit = Ident(m);
                    hit["type_name"] = SafeName(doc.GetElement(m.GetTypeId()));
                    hit["ends"] = gaps;
                    hits.Add(hit);
                }
                catch { unreadable++; }
            }
            return CoherenceFinding(AuditCheckNames.FramingNotReachingSupport, hits, top, unreadable,
                hits.Count + " beam(s)/column(s) have an end that does not touch a support within " +
                AuditCoherenceRules.SupportToleranceMm + " mm.",
                "Each beam end must bear on a column, wall or beam, and each column must stand on and reach a " +
                "slab, beam or footing. gap_mm_at_most brackets the gap (25/50/100/200/400/800 mm).",
                "Extend or move the member to its support (horizun_framing, horizun_transform_elements set_curve), or add the " +
                "missing column or footing (horizun_create_elements).",
                budget.Spent);
        }

        // ---- 8. framing_inconsistent_sections --------------------------------

        private static JObject FramingInconsistentSections(Document doc, int top)
        {
            var hits = new List<JObject>();
            int unreadable = 0;
            var beams = new List<CoherenceBeam>();
            foreach (FamilyInstance b in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming)
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                try
                {
                    var lc = b.Location as LocationCurve;
                    if (!(lc?.Curve is Line line)) continue;   // curved beams are not compared
                    XYZ s = line.GetEndPoint(0), e = line.GetEndPoint(1);
                    beams.Add(new CoherenceBeam
                    {
                        Id = Rid.Value(b.Id), UniqueId = b.UniqueId, TypeId = Rid.Value(b.GetTypeId()),
                        TypeName = SafeName(doc.GetElement(b.GetTypeId())),
                        X1 = s.X / FeetPerMm, Y1 = s.Y / FeetPerMm, Z1 = s.Z / FeetPerMm,
                        X2 = e.X / FeetPerMm, Y2 = e.Y / FeetPerMm, Z2 = e.Z / FeetPerMm
                    });
                }
                catch { unreadable++; }
            }
            var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => g.Curve is Line).Select(g => new
                {
                    g.Name, P = g.Curve.GetEndPoint(0), Q = g.Curve.GetEndPoint(1)
                }).ToList();
            foreach (List<CoherenceBeam> run in AuditCoherenceRules.CollinearRuns(beams))
            {
                if (run.Select(b => b.TypeId).Distinct().Count() < 2 && !run.Any(b => b.OverlapsAnother)) continue;
                CoherenceBeam first = run[0];
                string axis = grids.FirstOrDefault(g => AuditCoherenceRules.PointLineOffsetMm(
                    first.X1, first.Y1, g.P.X / FeetPerMm, g.P.Y / FeetPerMm, g.Q.X / FeetPerMm, g.Q.Y / FeetPerMm) <= 10 &&
                    AuditCoherenceRules.PointLineOffsetMm(first.X2, first.Y2, g.P.X / FeetPerMm, g.P.Y / FeetPerMm,
                        g.Q.X / FeetPerMm, g.Q.Y / FeetPerMm) <= 10)?.Name;
                hits.Add(new JObject
                {
                    ["axis"] = axis, ["elevation_mm"] = Math.Round(first.Z1),
                    ["sections"] = new JArray(run.Select(b => b.TypeName).Distinct()),
                    ["overlapping"] = run.Any(b => b.OverlapsAnother),
                    ["beams"] = new JArray(run.Select(b => new JObject
                    {
                        ["element_id"] = b.Id, ["unique_id"] = b.UniqueId, ["type_name"] = b.TypeName,
                        ["length_mm"] = Math.Round(b.LengthMm), ["overlaps_another"] = b.OverlapsAnother
                    }))
                });
            }
            return CoherenceFinding(AuditCheckNames.FramingInconsistentSections, hits, top, unreadable,
                hits.Count + " line(s) of collinear, touching beams change section along the line or carry overlapping beams.",
                "Beams that continue along one axis normally keep one section; a change is valid only where the span " +
                "changes and the engineer says so. Overlapping beams on one line are duplicates.",
                "Unify the type (horizun_transform_elements change_type) or delete the duplicate (horizun_delete_verified), " +
                "after the engineer confirms the section.",
                false);
        }

        // ---- 9. structural_overlap_unjoined ----------------------------------

        private static JObject StructuralOverlapUnjoined(Document doc, int top)
        {
            var budget = new Budget();
            var hits = new List<JObject>();
            int unreadable = 0;
            var cats = new ElementMulticategoryFilter(StructuralCategories);
            List<Element> elements = new FilteredElementCollector(doc).WherePasses(cats).WhereElementIsNotElementType()
                .Where(IsStructural).ToList();
            var structuralIds = new HashSet<long>(elements.Select(e => Rid.Value(e.Id)));
            var seen = new HashSet<string>();
            for (int i = 0; i < elements.Count; i++)
            {
                if (budget.Spent) { unreadable += elements.Count - i; break; }
                Element a = elements[i];
                try
                {
                    List<Solid> solidsA = SolidsOf(a);
                    if (solidsA.Count == 0) continue;
                    foreach (Element b in new FilteredElementCollector(doc).WherePasses(cats).WhereElementIsNotElementType()
                                 .Excluding(new List<ElementId> { a.Id }).WherePasses(new ElementIntersectsElementFilter(a)))
                    {
                        long ia = Rid.Value(a.Id), ib = Rid.Value(b.Id);
                        if (!structuralIds.Contains(ib)) continue;
                        string key = Math.Min(ia, ib) + ":" + Math.Max(ia, ib);
                        if (!seen.Add(key)) continue;
                        if (JoinGeometryUtils.AreElementsJoined(doc, a, b)) continue;
                        double volume = 0;
                        foreach (Solid sa in solidsA)
                            foreach (Solid sb in SolidsOf(b))
                            {
                                try
                                {
                                    Solid x = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                                    if (x != null) volume += x.Volume;
                                }
                                catch { /* a boolean Revit cannot compute is not an overlap we can measure */ }
                            }
                        double m3 = volume * 0.0283168466;
                        if (!AuditCoherenceRules.IsStructuralOverlap(m3)) continue;
                        hits.Add(new JObject
                        {
                            ["a"] = Ident(a), ["b"] = Ident(b), ["overlap_m3"] = Math.Round(m3, 4)
                        });
                    }
                }
                catch { unreadable++; }
            }
            return CoherenceFinding(AuditCheckNames.StructuralOverlapUnjoined, hits, top, unreadable,
                hits.Count + " pair(s) of structural elements overlap by more than " +
                (AuditCoherenceRules.StructuralOverlapThresholdM3 * 1000) + " dm3 without being joined.",
                "Overlapping concrete that is not joined is counted twice in quantities and shows seams in views.",
                "Join the pair (horizun_join_geometry) or trim/delete the duplicate element (horizun_delete_verified).",
                budget.Spent);
        }

        private static bool IsStructural(Element e)
        {
            long cat = e.Category == null ? 0 : Rid.Value(e.Category.Id);
            if (cat == (long)BuiltInCategory.OST_Walls)
                return e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;
            // Every floor and roof counts: a finish floor buried in a slab is concrete counted twice too.
            return true;
        }
    }
}
