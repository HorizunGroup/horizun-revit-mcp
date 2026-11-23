// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_create_elements, placement='all_enclosed'. Original
// Horizun code.
//
// One room (or space) in every closed region of a level IN A PHASE that does not
// already hold one. The entry is EXPANDED here, on every call, into one ordinary
// room/space row per region to fill - so the rehearsal lists every region it saw
// (area, Revit's interior point, what will happen to it) and a wall moved between
// rehearsal and apply resolves a different plan, which the token refuses as stale.
//
// ROOMS: the regions are Revit's PlanTopology(level, phase) circuits, each with
// PlanCircuit.IsRoomLocated; a room goes in with NewRoom(Phase) + NewRoom(Room,
// PlanCircuit) - the circuit itself, no point guessing. Per circuit and not
// NewRooms2, because NewRooms2 fills EVERY empty circuit in one call and a caller's
// min_area (the shafts nobody wants a room in) could only be honoured by creating
// and then deleting.
//
// SPACES: space regions are bounded by SPACE separators, not room separators, so the
// room topology is not theirs. The regions are the ones Revit's own NewSpaces2(level,
// phase, floor plan) fills, read in a rolled-back transaction; each is then placed
// with NewSpace(Level, Phase, UV) at the point Revit itself chose (the API has no
// NewSpace(Space, PlanCircuit)). Spaces that already stand on the level in that phase
// are listed as occupied beside them.
//
// Reading PlanTopology needs a MODIFIABLE document - Revit computes the topology on
// first access (RevitAPI.xml, Document.PlanTopology) - and NewSpaces2 writes, so both
// run inside a transaction that is ALWAYS rolled back; only plain values leave it.
// NewRoom(Room, PlanCircuit) throws when the level has no view, and NewSpaces2 tags
// into one, so a level without a floor plan is refused by name before anything runs.
//
// PlanCircuit exposes an area, a side count and ONE interior point - no centroid and
// no boundary. The rehearsal says point_inside, never centroid.
//
// NOT PROVEN and NOT MEASURED by this build or its live probe: whether walls of a
// LINKED model marked Room Bounding close a region of the host. The reply says so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private const double SquareFeetToM2 = 0.09290304;
        /// <summary>How close a re-read interior point must land to be the SAME circuit (feet, ~0.3 mm).</summary>
        private const double CircuitMatchFeet = 1e-3;

        private const string LinkBoundingNote =
            "not_proven: whether Room Bounding walls of a LINKED model close a host region is neither established nor measured by this build.";

        /// <summary>One region all_enclosed may fill, copied out of Revit into plain values: no PlanCircuit outlives the rolled-back read.</summary>
        private sealed class EnclosedRegion
        {
            public UV Point; public double AreaM2; public int? Sides; public bool? RoomLocated; public bool Occupied; public string Problem;
        }

        /// <summary>
        /// Replaces every placement='all_enclosed' entry with one room/space row per
        /// region to fill. Null = fine (block is null when no entry asked for it).
        /// </summary>
        private static string ExpandEnclosed(Document doc, JObject request, ref JArray input, out JArray block)
        {
            block = null;
            int enclosedEntries = 0, otherEntries = 0;
            for (int i = 0; i < input.Count; i++)
            {
                var r = input[i] as JObject;
                if (r == null) { otherEntries++; continue; }
                // The expansion's own bookkeeping: a caller's row carrying it would be planned as a circuit it never was.
                if (r["enclosed_from"] != null || r["circuit_area_m2"] != null)
                    return "elements[" + i + "]: enclosed_from and circuit_area_m2 are written by placement='all_enclosed', not by a caller.";
                string placementProblem = EnclosedRules.PlacementFieldsProblem(r);
                if (placementProblem != null) return "elements[" + i + "]: " + placementProblem;
                if (r.Value<string>("placement") == "all_enclosed") enclosedEntries++; else otherEntries++;
            }
            if (enclosedEntries == 0) return null;
            // The regions are read BEFORE anything in the batch is built, and each entry expands into
            // rows that would renumber the caller's other entries: all_enclosed goes in a batch of its own.
            string mix = EnclosedRules.MixProblem(enclosedEntries, otherEntries);
            if (mix != null) return mix;
            double scale;
            if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale)) return "units must be mm, m or feet.";
            var expanded = new JArray();
            block = new JArray();
            for (int i = 0; i < input.Count; i++)
            {
                var o = (JObject)input[i];
                string placement = o.Value<string>("placement");
                string where = "elements[" + i + "]: ";
                if (placement != "all_enclosed") return where + "placement must be all_enclosed.";
                string kind = (o.Value<string>("kind") ?? "").ToLowerInvariant();
                if (kind != "room" && kind != "space") return where + "placement='all_enclosed' is for kind room or space only.";
                if (o["point"] != null) return where + "all_enclosed finds the points itself - give no point.";
                if (o["name"] != null || o["number"] != null)
                    return where + "a name or number would repeat on every room of the level; name them afterwards " +
                           "with horizun_write_params_verified.";
                Level level = Rid.CanRepresent(o.Value<long?>("level_id") ?? -1) ? doc.GetElement(Rid.Make(o.Value<long>("level_id"))) as Level : null;
                if (level == null) return where + "level_id must identify a Level.";
                Phase phase = Rid.CanRepresent(o.Value<long?>("phase_id") ?? -1) ? doc.GetElement(Rid.Make(o.Value<long>("phase_id"))) as Phase : null;
                if (phase == null)
                    return where + "phase_id is required and must identify a Phase: circuits and rooms exist per phase, " +
                           "and the phase is never guessed.";
                double minArea = o.Value<double?>("min_area_m2") ?? 0;
                string minBad = EnclosedRules.MinAreaProblem(minArea);
                if (minBad != null) return where + minBad;
                ViewPlan plan = FloorPlanOf(doc, level);
                if (plan == null)
                    return where + "no_floor_plan: level '" + level.Name + "' has no floor plan view. Revit places a room in a circuit " +
                           "(NewRoom(Room, PlanCircuit) throws without one) and finds a level's space regions only through a plan " +
                           "view of that level - create one (horizun_manage_views create_floor_plan) and call again.";

                List<EnclosedRegion> regions;
                string revitNote;
                try { regions = ReadRegions(doc, kind, level, phase, plan, out revitNote); }
                catch (Exception ex) { return where + "Revit could not give the enclosed regions of that level and phase: " + ex.Message; }
                var circuits = new JArray();
                int toCreate = 0;
                foreach (EnclosedRegion g in regions)
                {
                    string action = g.Problem ?? EnclosedRules.CircuitAction(kind, g.Occupied, g.AreaM2, minArea);
                    var row = new JObject
                    {
                        ["point_inside"] = g.Point == null ? null : new JArray(Math.Round(g.Point.U / scale, 3), Math.Round(g.Point.V / scale, 3)),
                        ["area_m2"] = Math.Round(g.AreaM2, 3), ["action"] = action
                    };
                    if (g.Sides != null) row["sides"] = g.Sides.Value;
                    if (g.RoomLocated != null) row["is_room_located"] = g.RoomLocated.Value;
                    circuits.Add(row);
                    if (action != EnclosedRules.Create) continue;
                    toCreate++;
                    expanded.Add(EnclosedRules.GeneratedRow(kind, Rid.Value(level.Id), Rid.Value(phase.Id), g.Point.U / scale, g.Point.V / scale, i, g.AreaM2));
                }
                var entry = new JObject
                {
                    ["index"] = i, ["kind"] = kind, ["level"] = level.Name, ["level_id"] = Rid.Value(level.Id),
                    ["phase"] = phase.Name, ["phase_id"] = Rid.Value(phase.Id), ["min_area_m2"] = minArea,
                    ["floor_plan_id"] = Rid.Value(plan.Id),
                    ["circuits"] = circuits, ["circuits_seen"] = circuits.Count, ["to_create"] = toCreate,
                    ["regions_from"] = kind == "room"
                        ? "PlanTopology(level, phase): every circuit, with IsRoomLocated."
                        : "NewSpaces2(level, phase, floor plan) in a rolled-back transaction - the regions Revit itself fills with a space, " +
                          "bounded by space separators - plus the spaces already standing on the level in that phase (skipped_has_space).",
                    ["point_inside_means"] = "Revit's own interior point of the region (PlanCircuit.GetPointInside, or where NewSpaces2 put the space) - not a centroid.",
                    ["link_bounding"] = LinkBoundingNote
                };
                if (revitNote != null) entry["revit_said"] = revitNote;
                if (circuits.Count == 0) entry["note"] = "no closed region was seen on this level in this phase.";
                block.Add(entry);
            }
            input = expanded;
            return null;
        }

        /// <summary>
        /// The expansion left nothing to create. A rehearsal that saw regions, all occupied or too small,
        /// lists them and writes nothing; a level with NO region, and any apply, is refused by name
        /// (EnclosedRules.NothingToFillProblem says why).
        /// </summary>
        private static CommandResult NothingEnclosed(JObject request, JArray block)
        {
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string refusal = EnclosedRules.NothingToFillProblem(dryRun, block);
            if (refusal != null) return CommandResult.Fail(refusal);
            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = true, ["transaction_status"] = "not_started", ["requested"] = 0, ["created"] = 0,
                ["enclosed"] = block,
                ["note"] = "no region is left to fill: each one listed already holds a room/space, is under min_area_m2 or has no " +
                           "interior point Revit can give. Nothing was written."
            });
        }

        /// <summary>The level's floor plan with the lowest id (not a template); null when it has none.</summary>
        private static ViewPlan FloorPlanOf(Document doc, Level level)
        {
            foreach (ViewPlan v in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().OrderBy(v => Rid.Value(v.Id)))
            {
                if (v.IsTemplate || v.ViewType != ViewType.FloorPlan) continue;
                Level g = null;
                try { g = v.GenLevel; } catch { }
                if (g != null && g.Id == level.Id) return v;
            }
            return null;
        }

        /// <summary>
        /// Runs <paramref name="read"/> in a transaction that is always rolled back (a sub-transaction
        /// when one is already open): what it computes or creates never reaches the model.
        /// </summary>
        private static T InThrowaway<T>(Document doc, Func<T> read)
        {
            if (doc.IsModifiable)
            {
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    try { return read(); } finally { sub.RollBack(); }
                }
            }
            using (var t = new Transaction(doc, "Horizun: read enclosed regions (rolled back)"))
            {
                t.Start();
                // Whatever NewSpaces2 posts is discarded with the rollback, never shown.
                FailureHandlingOptions options = t.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(options);
                try { return read(); } finally { t.RollBack(); }
            }
        }

        private static List<EnclosedRegion> ReadRegions(Document doc, string kind, Level level, Phase phase, ViewPlan plan, out string revitNote)
        {
            string note = null;
            List<EnclosedRegion> list = InThrowaway(doc, () =>
            {
                var found = new List<EnclosedRegion>();
                if (kind == "room")
                {
                    foreach (PlanCircuit c in doc.get_PlanTopology(level, phase).Circuits)
                    {
                        var g = new EnclosedRegion { AreaM2 = c.Area * SquareFeetToM2, Sides = c.SideNum, RoomLocated = c.IsRoomLocated, Occupied = c.IsRoomLocated };
                        // GetPointInside throws when Revit cannot give a valid point (a sliver): that circuit is
                        // listed and skipped by name, the others still go.
                        try { g.Point = c.GetPointInside(); } catch (Exception) { g.Problem = EnclosedRules.NoInteriorPoint; }
                        found.Add(g);
                    }
                    return found;
                }
                // The spaces standing on the level in this phase BEFORE anything is created here: their
                // regions are occupied, and NewSpaces2 reports only the regions it fills.
                long phaseId = Rid.Value(phase.Id);
                foreach (Space s in new FilteredElementCollector(doc).OfClass(typeof(SpatialElement)).OfType<Space>())
                {
                    if (s.LevelId != level.Id || PhaseOf(s) != phaseId || !(s.Area > 0)) continue;
                    XYZ at = (s.Location as LocationPoint)?.Point;
                    found.Add(new EnclosedRegion { Point = at == null ? null : new UV(at.X, at.Y), AreaM2 = s.Area * SquareFeetToM2, Occupied = true });
                }
                ICollection<ElementId> made;
                try { made = doc.Create.NewSpaces2(level, phase, plan); }
                catch (Exception ex)
                {
                    // Not measured: whether NewSpaces2 throws or returns nothing when every region holds a
                    // space. With occupied regions listed, its word is kept beside them; with none, it refuses.
                    if (found.Count == 0) throw;
                    note = "NewSpaces2: " + ex.Message;
                    made = new List<ElementId>();
                }
                doc.Regenerate();
                foreach (ElementId id in made ?? new List<ElementId>())
                {
                    var s = doc.GetElement(id) as Space;
                    if (s == null) continue;
                    XYZ at = (s.Location as LocationPoint)?.Point;
                    found.Add(new EnclosedRegion
                    {
                        Point = at == null ? null : new UV(at.X, at.Y), AreaM2 = s.Area * SquareFeetToM2,
                        Problem = at == null ? EnclosedRules.NoInteriorPoint : null
                    });
                }
                return found;
            });
            revitNote = note;
            // One order whatever order Revit reports in: the plan (and so the token) must not change when the model did not.
            return list.OrderBy(g => g.Point == null ? double.MaxValue : Math.Round(g.Point.U, 4))
                       .ThenBy(g => g.Point == null ? double.MaxValue : Math.Round(g.Point.V, 4)).ToList();
        }

        /// <summary>PlanItem half: the row came from ExpandEnclosed (it carries phase_id).</summary>
        private static void PlanEnclosed(Document doc, JObject item, Plan p)
        {
            p.Phase = doc.GetElement(Rid.Make(item.Value<long>("phase_id"))) as Phase
                      ?? throw new ArgumentException("phase_id must identify a Phase");
            p.Enclosed = true;
            p.ExtraPlanFacts = p.ExtraPlanFacts ?? new Dictionary<string, string>();
            p.ExtraPlanFacts["enclosed.phase_uid"] = SafePlanUid(p.Phase);
            p.ExtraPlanFacts["enclosed.point"] = Canon01(p.Start);
            p.ExtraPlanFacts["enclosed.area_m2"] = (item.Value<double?>("circuit_area_m2") ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Create half, inside the batch transaction.</summary>
        private static Element CreateEnclosed(Document doc, Plan p)
        {
            if (p.Kind == "space")
            {
                // The point is where NewSpaces2 put a space in the rehearsal: inside a region Revit bounds for spaces.
                Space space = doc.Create.NewSpace(p.Level, p.Phase, new UV(p.Start.X, p.Start.Y));
                if (space == null) throw new InvalidOperationException("Revit placed no space in that region. Nothing was kept.");
                return space;
            }
            // The circuit is re-read NOW and matched by Revit's own interior point: a
            // PlanCircuit from the rehearsal belongs to a topology that no longer exists.
            PlanCircuit circuit = null;
            foreach (PlanCircuit c in doc.get_PlanTopology(p.Level, p.Phase).Circuits)
            {
                UV q;
                // A sliver without an interior point is not the circuit planned: skip it, never abort the batch on it.
                try { q = c.GetPointInside(); } catch (Exception) { continue; }
                if (Math.Abs(q.U - p.Start.X) <= CircuitMatchFeet && Math.Abs(q.V - p.Start.Y) <= CircuitMatchFeet) { circuit = c; break; }
            }
            if (circuit == null)
                throw new InvalidOperationException("enclosed_circuit_gone: no circuit of that level and phase still has its interior " +
                    "point where the plan found it - the bounding walls changed. Nothing was kept.");
            if (circuit.IsRoomLocated)
                throw new InvalidOperationException("a room already stands in that circuit. Nothing was kept.");
            Room room = doc.Create.NewRoom(p.Phase);
            doc.Create.NewRoom(room, circuit);
            return room;
        }

        /// <summary>Every boundary loop the element reports closes on itself (end of the last segment = start of the first).</summary>
        private static bool BoundaryClosed(Element e)
        {
            try
            {
                IList<IList<BoundarySegment>> loops = ((SpatialElement)e).GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (loops == null || loops.Count == 0) return false;
                foreach (IList<BoundarySegment> loop in loops)
                {
                    if (loop == null || loop.Count == 0) return false;
                    XYZ previous = loop[loop.Count - 1].GetCurve().GetEndPoint(1);
                    foreach (BoundarySegment segment in loop)
                    {
                        Curve curve = segment.GetCurve();
                        if (curve.GetEndPoint(0).DistanceTo(previous) > 1e-3) return false;
                        previous = curve.GetEndPoint(1);
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private static long PhaseOf(Element e)
        {
            try { return Rid.Value(e.get_Parameter(BuiltInParameter.ROOM_PHASE_ID)?.AsElementId() ?? ElementId.InvalidElementId); }
            catch { return -1; }
        }
    }
}
