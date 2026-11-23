// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_quantities mode='room_finishes' - the faces of each room, who bounds
// them, and the openings in those walls, measured and never netted.
//
// Revit's SpatialElementGeometryCalculator builds the room's solid at the FINISH
// face of its boundaries and tells, face by face, which element bounds each piece
// (GetBoundaryFaceInfo -> SpatialElementBoundarySubface). That is the gross wall,
// floor and ceiling area a finishes schedule starts from.
//
// The room solid is NOT cut by the doors, windows or wall openings of its walls - it
// runs straight past them. So the face area here is gross, and the openings of each
// bounding wall that exist in the phase and face this room are measured separately
// (rough size when the family publishes it, else nominal, else the bounding box; a
// rectangular wall opening by its boundary rectangle - and each says which), listed one
// by one and summed in their own column. Deducting them is the reader's rule (many
// contracts do not deduct small openings); a net computed here could not be taken apart.
//
// PHASE. An insert is deducted only when it is in the wall in the measured phase
// (GetPhaseStatus New or Existing, or None: not phased). A door created later, or one
// demolished and infilled in this phase, is named in inserts_other_phase_status.
//
// MATERIAL. The finish a room sees is the PAINT when the face is painted (the Paint tool
// stores it apart from the layer material: Document.IsPainted / GetPaintedMaterial), and
// the face's own material otherwise. Each row says which (material_source).
//
// What is NOT counted is NAMED: unplaced, unenclosed, redundant rooms, rooms of another
// phase, geometry the calculator refused, a face whose boundary info could not be read,
// an embedded wall whose opening is not sized. None of them is a zero, and each one
// makes coverage say so.
//
// LINKS. A room bounded by a linked wall gets that face's area (it is the room's own
// geometry) and the linked element's type, material and code read from the link
// document. The openings of a LINKED wall are not read: FamilyInstance.FromRoom/ToRoom
// in the link answer the link's rooms, not this document's, so a deduction from them
// would be attributed by a different model's room layout. They are named instead, and
// the deductions of that room are reported incomplete.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class QuantitiesCommand
    {
        private const double FeetToM = 0.3048;
        private const double RoomProbeLiftFeet = 1.0 / 304.8;   // 1 mm, as the membership reader lifts
        private const string UnreadableBound = "(unreadable bounding element)";
        private const string BoundNotRead = "(bounding element not read)";
        private const string UnreadableMaterial = "(unreadable)";
        private const string FaceUnreadable = "(face unreadable)";

        private static readonly BuiltInParameter[] NominalWidth =
            { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM };
        private static readonly BuiltInParameter[] NominalHeight =
            { BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM };

        /// <summary>The phase the caller named, exactly (case-insensitive). No default: rooms exist per phase.</summary>
        private static Phase FindPhase(Document doc, string name, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                problem = "phase is required: rooms, spaces and the doors facing them are phase-dependent, and a hidden " +
                          "default (the last phase) would silently measure a different building. Name the phase.";
                return null;
            }
            var names = new List<string>();
            foreach (Phase p in doc.Phases)
            {
                names.Add(p.Name);
                if (string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            }
            problem = "No phase is named '" + name + "'. Phases in this document: " + string.Join(", ", names) + ".";
            return null;
        }

        private static long? PhaseIdOf(SpatialElement se)
        {
            try
            {
                var p = se.get_Parameter(BuiltInParameter.ROOM_PHASE);
                if (p == null || !p.HasValue) return null;
                return Rid.Value(p.AsElementId());
            }
            catch { return null; }
        }

        private static bool SpatialContains(SpatialElement se, XYZ p)
        {
            try
            {
                var room = se as Autodesk.Revit.DB.Architecture.Room;
                if (room != null) return room.IsPointInRoom(p);
                var space = se as Autodesk.Revit.DB.Mechanical.Space;
                if (space != null) return space.IsPointInSpace(p);
            }
            catch { }
            return false;
        }

        private CommandResult ExecuteRoomFinishes(Document doc, JObject request, int top)
        {
            string problem;
            Phase phase = FindPhase(doc, request.Value<string>("phase"), out problem);
            if (phase == null) return CommandResult.Fail("mode 'room_finishes': " + problem + " Nothing was measured.");
            long phaseId = Rid.Value(phase.Id);
            string codeParameter = request.Value<string>("code_parameter");
            if (string.IsNullOrWhiteSpace(codeParameter)) codeParameter = null;
            string levelName = request.Value<string>("level");
            if (string.IsNullOrWhiteSpace(levelName)) levelName = null;

            // ---- The spatial elements in scope. ----
            var notMeasured = new JArray();
            var duplicates = new JArray();
            var scope = new List<SpatialElement>();
            var idsToken = request["element_ids"] as JArray;
            bool explicitIds = idsToken != null && idsToken.Count > 0;
            int otherPhase = 0;
            var all = new List<SpatialElement>();
            foreach (var se in new FilteredElementCollector(doc).OfClass(typeof(SpatialElement)).Cast<SpatialElement>())
                if (se is Autodesk.Revit.DB.Architecture.Room || se is Autodesk.Revit.DB.Mechanical.Space) all.Add(se);

            if (explicitIds)
            {
                var seenIds = new HashSet<long>();
                foreach (var tok in idsToken)
                {
                    long id;
                    if (tok.Type != JTokenType.Integer || !Rid.CanRepresentElementId(id = tok.Value<long>()))
                    { notMeasured.Add(new JObject { ["element_id"] = tok.ToString(), ["state"] = "invalid_id" }); continue; }
                    // A repeated id would measure the room twice and double its gross while its openings
                    // (deduplicated per room) count once: measured once, and named.
                    if (!seenIds.Add(id)) { duplicates.Add(id); continue; }
                    var e = doc.GetElement(Rid.ToElementId(id));
                    var se = e as SpatialElement;
                    if (se == null || !(se is Autodesk.Revit.DB.Architecture.Room || se is Autodesk.Revit.DB.Mechanical.Space))
                    { notMeasured.Add(new JObject { ["element_id"] = id, ["state"] = e == null ? "not_found" : "not_a_room_or_space" }); continue; }
                    scope.Add(se);
                }
            }
            else scope.AddRange(all);

            var calcOptions = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            var calc = new SpatialElementGeometryCalculator(doc, calcOptions);

            var faces = new List<FinishFaceFact>();
            var openings = new List<OpeningDeductionFact>();
            var roomRows = new JArray();
            var linkedBounds = new JArray();
            var otherInserts = new JArray();
            var otherPhaseInserts = new JArray();
            var faceReadFailures = new JArray();
            var linkedSeen = new HashSet<string>(StringComparer.Ordinal);
            var kindByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            int measured = 0, notDeductedFacing = 0, phaseUnreadable = 0;

            foreach (var se in scope)
            {
                long sid = Rid.Value(se.Id);
                string number = Safe(() => se.Number), name = Safe(() => se.Name), lvl = Safe(() => se.Level?.Name);
                string kind = se is Autodesk.Revit.DB.Architecture.Room ? "room" : "space";
                if (levelName != null && !string.Equals(lvl, levelName, StringComparison.OrdinalIgnoreCase))
                {
                    if (explicitIds) notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "other_level", "Its level is '" + lvl + "', not '" + levelName + "'."));
                    continue;
                }
                long? pid = PhaseIdOf(se);
                if (pid != phaseId)
                {
                    if (explicitIds) notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "other_phase", "It belongs to another phase (id " + (pid?.ToString() ?? "unreadable") + ")."));
                    else otherPhase++;
                    continue;
                }
                var lp = se.Location as LocationPoint;
                if (lp == null)
                {
                    notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "unplaced", "It is not placed in the model, so it has no faces. Not a zero."));
                    continue;
                }
                double? areaRead = AreaOf(se);
                if (!areaRead.HasValue)
                {
                    // Unreadable is not "not enclosed" (review 2026-11-20, M4).
                    notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "unreadable", "Revit did not return its area. Not a zero."));
                    continue;
                }
                double area = areaRead.Value;
                if (area <= 0)
                {
                    // Redundant = its point lies inside another placed, enclosed room of the same phase. The
                    // point is lifted 1 mm: on its level it sits on the other room's bottom face, where the
                    // containment answer is Revit's either way.
                    XYZ probe = lp.Point + new XYZ(0, 0, RoomProbeLiftFeet);
                    SpatialElement owner = all.FirstOrDefault(o => o.Id != se.Id && PhaseIdOf(o) == phaseId &&
                        o.GetType() == se.GetType() && (AreaOf(o) ?? 0) > 0 && SpatialContains(o, probe));
                    notMeasured.Add(owner != null
                        ? NotMeasured(sid, kind, number, name, lvl, "redundant", "It shares its enclosure with " + kind + " " + Rid.Value(owner.Id) + " (its point lies inside that one).")
                        : NotMeasured(sid, kind, number, name, lvl, "not_enclosed", "Its boundaries do not close, so Revit gives it no area. Not a zero."));
                    continue;
                }

                SpatialElementGeometryResults results;
                Solid solid;
                try { results = calc.CalculateSpatialElementGeometry(se); solid = results.GetGeometry(); }
                catch (Exception ex)
                {
                    notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "geometry_failed", "The geometry calculator refused it: " + ex.Message));
                    continue;
                }
                if (solid == null)
                {
                    notMeasured.Add(NotMeasured(sid, kind, number, name, lvl, "geometry_failed", "The geometry calculator returned no solid."));
                    continue;
                }

                string roomKey = sid.ToString();
                kindByKey[roomKey] = kind;
                var hostWalls = new Dictionary<long, Wall>();
                double unbounded = 0;
                foreach (Face face in solid.Faces)
                {
                    double faceM2 = face.Area * RoomFinishRules.SquareFeetToM2;
                    string faceSurface = SurfaceOfNormal(face);
                    IList<SpatialElementBoundarySubface> subs = null;
                    string faceProblem = null;
                    try { subs = results.GetBoundaryFaceInfo(face); }
                    catch (Exception ex) { faceProblem = "GetBoundaryFaceInfo failed: " + ex.Message; }
                    double boundedM2 = 0;
                    if (subs != null)
                        foreach (var sub in subs)
                        {
                            string surface = RoomFinishRules.SurfaceOf(sub.SubfaceType.ToString());
                            double m2;
                            try { m2 = sub.GetSubface().Area * RoomFinishRules.SquareFeetToM2; }
                            catch (Exception ex) { faceProblem = "a subface area could not be read: " + ex.Message; continue; }
                            boundedM2 += m2;
                            faces.Add(BoundedFace(doc, sub, roomKey, surface ?? faceSurface, m2, codeParameter,
                                                  hostWalls, linkedBounds, linkedSeen, sid));
                        }
                    double rest = faceM2 - boundedM2;
                    if (faceProblem != null)
                    {
                        // A read failure is not the room's own limit: the area whose bounding element could
                        // not be told goes to a named row, and coverage says the areas are incomplete.
                        faceReadFailures.Add(new JObject
                        {
                            ["room_id"] = sid, ["surface"] = faceSurface,
                            ["unattributed_m2"] = Math.Round(Math.Max(rest, 0), 4), ["error"] = faceProblem
                        });
                        if (rest > 1e-6)
                            faces.Add(new FinishFaceFact
                            {
                                RoomKey = roomKey, Surface = faceSurface, GrossM2 = rest,
                                BoundingKey = "unreadable", TypeName = BoundNotRead, Material = UnreadableMaterial
                            });
                    }
                    else if (rest > 1e-6)
                    {
                        // The part of a room face nothing bounds (a room limit above an unbounded top, a gap):
                        // its own row, under a named type, so the totals still add up to the room.
                        unbounded += rest;
                        faces.Add(new FinishFaceFact { RoomKey = roomKey, Surface = faceSurface, GrossM2 = rest });
                    }
                }

                foreach (var kv in hostWalls)
                    CollectOpenings(doc, se, phase, kv.Value, roomKey, openings, otherInserts, otherPhaseInserts,
                                    ref notDeductedFacing, ref phaseUnreadable);

                measured++;
                roomRows.Add(new JObject
                {
                    ["id"] = sid, ["kind"] = kind, ["number"] = number, ["name"] = name, ["level"] = lvl,
                    ["area_m2"] = Math.Round(area * RoomFinishRules.SquareFeetToM2, 4),
                    ["unbounded_face_m2"] = Math.Round(unbounded, 4)
                });
            }

            List<OpeningDeductionFact> orphans;
            var groups = RoomFinishRules.Group(faces, openings, out orphans);
            var rows = new JArray();
            foreach (var g in groups.Take(top))
                rows.Add(new JObject
                {
                    ["room_id"] = long.Parse(g.RoomKey), ["kind"] = kindByKey[g.RoomKey], ["surface"] = g.Surface,
                    ["bounding_type"] = g.TypeName, ["material"] = g.Material, ["material_source"] = g.MaterialSource,
                    ["code"] = g.Code,
                    ["gross_m2"] = Math.Round(g.GrossM2, 4),
                    ["openings_deduction_m2"] = g.Surface == "wall" ? (JToken)Math.Round(g.OpeningDeductionM2, 4) : JValue.CreateNull(),
                    ["openings"] = g.Openings, ["openings_unsized"] = g.OpeningsUnsized,
                    ["opening_size_basis"] = new JArray(g.SizeBases),
                    ["opening_ids"] = new JArray(g.OpeningIds),
                    ["bounding_element_keys"] = new JArray(g.AreaByBoundingKey.Keys)
                });

            // Every attributed opening with its own size: "openings under X m2 are not deducted" needs each one.
            var openingRows = new JArray();
            foreach (var g in groups)
                foreach (var o in g.OpeningFacts)
                {
                    double? a = RoomFinishRules.RectangleM2(o.WidthM, o.HeightM);
                    openingRows.Add(new JObject
                    {
                        ["room_id"] = long.Parse(o.RoomKey), ["bounding_element_key"] = o.BoundingKey,
                        ["insert_id"] = long.Parse(o.InsertId), ["insert_kind"] = o.InsertKind,
                        ["width_m"] = Round4(o.WidthM), ["height_m"] = Round4(o.HeightM), ["area_m2"] = Round4(a),
                        ["size_basis"] = a.HasValue ? o.SizeBasis : null
                    });
                }

            // Rooms and spaces are totalled APART: a space drawn over a room measures the same faces
            // again, and one sum over both would count every face twice.
            var totalsByKind = new JObject();
            foreach (var k in new[] { "room", "space" })
            {
                var gk = groups.Where(g => kindByKey[g.RoomKey] == k).ToList();
                if (gk.Count == 0) continue;
                var t = new JObject();
                foreach (var s in new[] { "wall", "floor", "ceiling" })
                    t[s + "_gross_m2"] = Math.Round(gk.Where(g => g.Surface == s).Sum(g => g.GrossM2), 4);
                t["openings_deduction_m2"] = Math.Round(gk.Sum(g => g.OpeningDeductionM2), 4);
                totalsByKind[k] = t;
            }

            int unreadableBounds = faces.Count(f => f.TypeName == UnreadableBound || f.BoundingKey == "unreadable");
            int unreadableMaterials = faces.Count(f => f.TypeName != UnreadableBound && f.BoundingKey != "unreadable" &&
                (f.Material == UnreadableMaterial || f.Material == FaceUnreadable || f.MaterialSource == "paint_unreadable"));
            int linkedOpeningsNotRead = linkedBounds.Count(j => j.Value<bool?>("hosted_openings_not_read") == true);
            bool areasComplete = notMeasured.Count == 0 && faceReadFailures.Count == 0 && unreadableBounds == 0 && unreadableMaterials == 0;
            bool deductionsComplete = orphans.Count == 0 && groups.All(g => g.OpeningsUnsized == 0) && linkedOpeningsNotRead == 0 &&
                                      notDeductedFacing == 0 && phaseUnreadable == 0;
            return CommandResult.Ok(new JObject
            {
                ["mode"] = "room_finishes",
                ["phase"] = phase.Name,
                ["boundary_location"] = "Finish",
                ["code_parameter"] = codeParameter,
                ["rule"] = "gross_m2 is the room face, which Revit does NOT cut at openings. openings_deduction_m2 is the " +
                           "measured size of the doors, windows and wall openings of each bounding wall that are in the wall in " +
                           "this phase and face this room, in its own column and one by one in 'openings': no net is computed " +
                           "here - deduct by your contract's rule. material is the paint on the face when painted, else the face's.",
                ["rooms"] = roomRows,
                ["rows"] = rows,
                ["rows_total"] = groups.Count,
                ["truncated"] = groups.Count > top,
                ["totals_by_kind"] = totalsByKind,
                ["totals_rule"] = "Rooms and spaces are totalled apart: a space drawn over a room measures the same faces again.",
                ["openings"] = openingRows,
                ["not_measured"] = notMeasured,
                ["duplicate_ids"] = duplicates,
                ["rooms_in_other_phases"] = otherPhase,
                ["face_read_failures"] = faceReadFailures,
                ["openings_not_attributed"] = new JArray(orphans.Select(o => new JObject { ["room_id"] = o.RoomKey, ["wall"] = o.BoundingKey, ["insert_id"] = o.InsertId })),
                ["inserts_not_deducted"] = otherInserts,
                ["inserts_other_phase_status"] = otherPhaseInserts,
                ["linked_bounding_elements"] = linkedBounds,
                ["links_rule"] = "A linked bounding element contributes its face area, and its type, material and code read " +
                                 "from the link document. Openings hosted in a LINKED wall are not read (their From/To room " +
                                 "answers the link's rooms), so a room bounded by a linked wall has incomplete deductions.",
                ["coverage"] = new JObject
                {
                    ["rooms_measured"] = measured, ["rooms_not_measured"] = notMeasured.Count,
                    ["face_read_failures"] = faceReadFailures.Count,
                    ["unreadable_bounding_elements"] = unreadableBounds,
                    ["unreadable_materials"] = unreadableMaterials,
                    ["linked_walls_openings_not_read"] = linkedOpeningsNotRead,
                    ["facing_inserts_not_deducted"] = notDeductedFacing,
                    ["inserts_phase_unreadable"] = phaseUnreadable,
                    ["areas_complete"] = areasComplete,
                    ["deductions_complete"] = deductionsComplete,
                    ["complete"] = areasComplete && deductionsComplete
                }
            });
        }

        private static JToken Round4(double? v) => v.HasValue ? (JToken)Math.Round(v.Value, 4) : JValue.CreateNull();

        private static JObject NotMeasured(long id, string kind, string number, string name, string level, string state, string why)
            => new JObject { ["id"] = id, ["kind"] = kind, ["number"] = number, ["name"] = name, ["level"] = level, ["state"] = state, ["reason"] = why };

        private static string SurfaceOfNormal(Face face)
        {
            try
            {
                var pf = face as PlanarFace;
                if (pf != null)
                {
                    if (pf.FaceNormal.Z > 0.99) return "ceiling";
                    if (pf.FaceNormal.Z < -0.99) return "floor";
                }
            }
            catch { }
            return "wall";
        }

        private static FinishFaceFact BoundedFace(Document doc, SpatialElementBoundarySubface sub, string roomKey, string surface,
                                                  double m2, string codeParameter, Dictionary<long, Wall> hostWalls,
                                                  JArray linkedBounds, HashSet<string> linkedSeen, long roomId)
        {
            var fact = new FinishFaceFact { RoomKey = roomKey, Surface = surface, GrossM2 = m2 };
            LinkElementId lid = null;
            try { lid = sub.SpatialBoundaryElement; } catch { }
            if (lid == null) { fact.BoundingKey = "unreadable"; fact.TypeName = UnreadableBound; return fact; }

            Document owner = doc;
            Element be;
            if (lid.LinkInstanceId != ElementId.InvalidElementId)
            {
                var link = doc.GetElement(lid.LinkInstanceId) as RevitLinkInstance;
                owner = link == null ? null : Safe(() => link.GetLinkDocument());
                be = owner == null ? null : owner.GetElement(lid.LinkedElementId);
                fact.BoundingKey = "link:" + Rid.Value(lid.LinkInstanceId) + "/" + Rid.Value(lid.LinkedElementId);
                if (linkedSeen.Add(roomKey + "|" + fact.BoundingKey))
                {
                    // Only a wall hosts the doors and windows a finish deducts; an element of a link that is
                    // not loaded could be one, so its openings are unknown too.
                    bool mayHostOpenings = be == null || be is Wall;
                    linkedBounds.Add(new JObject
                    {
                        ["room_id"] = roomId, ["link_instance_id"] = Rid.Value(lid.LinkInstanceId),
                        ["linked_element_id"] = Rid.Value(lid.LinkedElementId),
                        ["read"] = be == null ? "area only: the link document is not loaded or the element is gone" : "area, type, material, code",
                        ["not_read"] = mayHostOpenings ? "hosted openings" : null,
                        ["hosted_openings_not_read"] = mayHostOpenings
                    });
                }
            }
            else
            {
                be = doc.GetElement(lid.HostElementId);
                fact.BoundingKey = "host:" + Rid.Value(lid.HostElementId);
                var wall = be as Wall;
                if (wall != null) hostWalls[Rid.Value(wall.Id)] = wall;
            }

            if (be == null) { fact.TypeName = UnreadableBound; fact.Material = UnreadableMaterial; return fact; }
            fact.TypeName = (SafeCategory(be) ?? "(no category)") + ": " + (SafeTypeName(owner, be) ?? SafeName(be) ?? "(no type)");
            if (be is CurveElement)
            {
                // A room separation line bounds the room but has no face and no material: a state, not a failure.
                fact.Material = "(separation line: no face)";
            }
            else
            {
                try
                {
                    Face bf = sub.GetBoundingElementFace();
                    if (bf == null) fact.Material = FaceUnreadable;
                    else
                    {
                        // The finish the room sees: the paint on that face when there is one, else the
                        // face's own layer/family material.
                        ElementId mid = bf.MaterialElementId;
                        fact.MaterialSource = "face";
                        try
                        {
                            if (owner.IsPainted(be.Id, bf)) { mid = owner.GetPaintedMaterial(be.Id, bf); fact.MaterialSource = "paint"; }
                        }
                        catch { fact.MaterialSource = "paint_unreadable"; }
                        var mat = mid == null || mid == ElementId.InvalidElementId ? null : owner.GetElement(mid) as Material;
                        fact.Material = mat?.Name ?? "(no material on face)";
                    }
                }
                catch { fact.Material = UnreadableMaterial; }
            }
            if (codeParameter != null) fact.Code = ReadCode(owner, be, codeParameter);
            return fact;
        }

        /// <summary>
        /// The inserts of one bounding wall: doors, windows and rectangular wall openings that are in the wall
        /// in this phase and face this room are measured; everything else is named, never dropped.
        /// </summary>
        private static void CollectOpenings(Document doc, SpatialElement se, Phase phase, Wall wall, string roomKey,
                                            List<OpeningDeductionFact> openings, JArray otherInserts, JArray otherPhaseInserts,
                                            ref int notDeductedFacing, ref int phaseUnreadable)
        {
            IList<ElementId> inserts;
            // addRectOpenings and includeEmbeddedWalls: a wall opening and a storefront cut this wall as a door
            // does, and the room solid runs past them all. Shadows (another wall's inserts) and shared embedded
            // inserts (the doors of an embedded wall) are not openings of THIS wall.
            try { inserts = wall.FindInserts(true, false, true, false); } catch { return; }
            XYZ along = null;
            try { along = ((wall.Location as LocationCurve)?.Curve as Line)?.Direction; } catch { }
            long roomId = long.Parse(roomKey), wallId = Rid.Value(wall.Id);
            foreach (var iid in inserts)
            {
                Element ins = doc.GetElement(iid);
                if (ins == null) continue;
                var fi = ins as FamilyInstance;
                var opening = ins as Opening;
                bool embeddedWall = ins is Wall;
                long? cat = null;
                try { cat = ins.Category == null ? (long?)null : Rid.Value(ins.Category.Id); } catch { }
                bool door = fi != null && cat == (long)BuiltInCategory.OST_Doors;
                bool window = fi != null && cat == (long)BuiltInCategory.OST_Windows;
                if (!door && !window && opening == null && !embeddedWall)
                {
                    otherInserts.Add(new JObject { ["room_id"] = roomId, ["wall_id"] = wallId, ["insert_id"] = Rid.Value(iid),
                                                   ["category"] = SafeCategory(ins), ["reason"] = "not a door, window or wall opening" });
                    continue;
                }

                // Phase first: an insert that is not in the wall in this phase faces nothing then.
                string status;
                try { status = ins.GetPhaseStatus(phase.Id).ToString(); }
                catch (Exception ex)
                {
                    phaseUnreadable++;
                    otherPhaseInserts.Add(new JObject { ["room_id"] = roomId, ["wall_id"] = wallId, ["insert_id"] = Rid.Value(iid),
                                                        ["category"] = SafeCategory(ins), ["phase_status"] = "unreadable", ["error"] = ex.Message });
                    continue;
                }
                if (!RoomFinishRules.ExistsInPhase(status))
                {
                    otherPhaseInserts.Add(new JObject { ["room_id"] = roomId, ["wall_id"] = wallId, ["insert_id"] = Rid.Value(iid),
                                                        ["category"] = SafeCategory(ins), ["phase_status"] = status });
                    continue;
                }

                bool? faces = door || window ? FacesRoom(fi, se, phase, wall) : FacesRoomAtCentre(ins, se, wall);
                if (faces == false) continue;
                if (faces == null || embeddedWall)
                {
                    // Not sized here (an embedded wall), or its side could not be told: named, and the
                    // deductions are reported incomplete rather than short.
                    notDeductedFacing++;
                    otherInserts.Add(new JObject
                    {
                        ["room_id"] = roomId, ["wall_id"] = wallId, ["insert_id"] = Rid.Value(iid), ["category"] = SafeCategory(ins),
                        ["faces_room"] = faces == null ? JValue.CreateNull() : (JToken)true,
                        ["reason"] = faces == null ? "its position could not be read, so whether it faces this room is unknown"
                                                   : "embedded wall: its opening in this wall is not sized here"
                    });
                    continue;
                }

                var fact = new OpeningDeductionFact
                {
                    RoomKey = roomKey, BoundingKey = "host:" + wallId, InsertId = Rid.Value(iid).ToString(),
                    InsertKind = door ? "door" : window ? "window" : "wall_opening"
                };
                if (opening != null) SizeWallOpening(opening, fact);
                else SizeDoorOrWindow(doc, fi, along, fact);
                openings.Add(fact);
            }
        }

        /// <summary>A rectangular wall opening from its two corners: width along the wall, height in Z. An arc one stays unsized.</summary>
        private static void SizeWallOpening(Opening opening, OpeningDeductionFact fact)
        {
            try
            {
                if (!opening.IsRectBoundary) return;
                IList<XYZ> r = opening.BoundaryRect;
                if (r == null || r.Count < 2) return;
                XYZ d = r[1] - r[0];
                fact.WidthM = Math.Sqrt(d.X * d.X + d.Y * d.Y) * FeetToM;
                fact.HeightM = Math.Abs(d.Z) * FeetToM;
                fact.SizeBasis = "opening_rect";
            }
            catch { fact.WidthM = null; fact.HeightM = null; fact.SizeBasis = null; }
        }

        /// <summary>Rough size, else nominal (DOOR_/WINDOW_/FAMILY_ width and height, as FramingWall reads them), else the box.</summary>
        private static void SizeDoorOrWindow(Document doc, FamilyInstance fi, XYZ along, OpeningDeductionFact fact)
        {
            double? w = SizeParam(doc, fi, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM), h = SizeParam(doc, fi, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM);
            if (w.HasValue && h.HasValue) fact.SizeBasis = "rough";
            else
            {
                w = FirstSizeParam(doc, fi, NominalWidth); h = FirstSizeParam(doc, fi, NominalHeight);
                if (w.HasValue && h.HasValue) fact.SizeBasis = "nominal";
                else
                {
                    // The box of the instance, measured along the wall: overstates a frame, never invents one.
                    w = null; h = null;
                    try
                    {
                        var bb = fi.get_BoundingBox(null);
                        if (bb != null && along != null)
                        {
                            var d = bb.Max - bb.Min;
                            w = (Math.Abs(d.X * along.X) + Math.Abs(d.Y * along.Y)) * FeetToM;
                            h = d.Z * FeetToM;
                            fact.SizeBasis = "bounding_box";
                        }
                    }
                    catch { }
                }
            }
            fact.WidthM = w; fact.HeightM = h;
        }

        /// <summary>
        /// A room: Revit's own From/To room in that phase. A space has no From/To, so a point
        /// half a wall plus 150 mm to each side of the insert is probed.
        /// </summary>
        private static bool FacesRoom(FamilyInstance fi, SpatialElement se, Phase phase, Wall wall)
        {
            try
            {
                if (se is Autodesk.Revit.DB.Architecture.Room)
                {
                    var from = fi.get_FromRoom(phase); var to = fi.get_ToRoom(phase);
                    return (from != null && from.Id == se.Id) || (to != null && to.Id == se.Id);
                }
                var lp = fi.Location as LocationPoint;
                if (lp == null) return false;
                var bb = fi.get_BoundingBox(null);
                double z = bb != null ? (bb.Min.Z + bb.Max.Z) / 2 : lp.Point.Z + 1;
                var n = wall.Orientation;
                double off = wall.Width / 2 + 0.5;
                var c = new XYZ(lp.Point.X, lp.Point.Y, z);
                return SpatialContains(se, c + n * off) || SpatialContains(se, c - n * off);
            }
            catch { return false; }
        }

        /// <summary>
        /// A wall opening or an embedded wall cuts through its host, so it faces whichever room a point beside
        /// its centre lies in (the centre of its boundary rectangle, else of its box). Null: no centre readable.
        /// </summary>
        private static bool? FacesRoomAtCentre(Element ins, SpatialElement se, Wall wall)
        {
            try
            {
                XYZ c = null;
                var op = ins as Opening;
                if (op != null && op.IsRectBoundary)
                {
                    IList<XYZ> r = op.BoundaryRect;
                    if (r != null && r.Count >= 2) c = (r[0] + r[1]) * 0.5;
                }
                if (c == null)
                {
                    var bb = ins.get_BoundingBox(null);
                    if (bb != null) c = (bb.Min + bb.Max) * 0.5;
                }
                if (c == null) return null;
                var n = wall.Orientation;
                double off = wall.Width / 2 + 0.5;
                return SpatialContains(se, c + n * off) || SpatialContains(se, c - n * off);
            }
            catch { return null; }
        }

        /// <summary>A length parameter in metres, instance first then type; null when absent, empty or not positive.</summary>
        private static double? SizeParam(Document doc, FamilyInstance fi, BuiltInParameter bip)
        {
            foreach (Element e in new Element[] { fi, fi.Symbol })
            {
                try
                {
                    var p = e?.get_Parameter(bip);
                    if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble() * FeetToM;
                }
                catch { }
            }
            return null;
        }

        /// <summary>The first of several length parameters that has a value, each instance first then type.</summary>
        private static double? FirstSizeParam(Document doc, FamilyInstance fi, BuiltInParameter[] bips)
        {
            foreach (var bip in bips)
            {
                double? v = SizeParam(doc, fi, bip);
                if (v.HasValue) return v;
            }
            return null;
        }

        private static double? AreaOf(SpatialElement se) { try { return se.Area; } catch { return null; } }

        private static T Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
    }
}
