// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_code_check, egress travel distance. Original Horizun code.
//
// THE GAP THIS CLOSES. A rule "max travel distance <= N m" could never pass or fail
// through the requirement-set grammar: Core/CodeCheckEvaluation.cs answered
// not_decidable for travel_distance_m, always. Two entry points now measure it with
// Revit's own Autodesk.Revit.DB.Analysis.PathOfTravel service (present with the same
// members in every year 2023-2027, read from each year's RevitAPI.xml):
//
//   operation=check        each rule whose measure is travel_distance_m and whose OWN
//                          config names route_view_id(s) and exits gets a routed number
//                          per room, under that rule's own key, BEFORE the pure evaluator
//                          runs (AttachTravelDistance). A rule without a config borrows
//                          nothing and stays not_decidable.
//   operation=travel_distance
//                          the measurement itself, per room, with its polyline, the exit
//                          reached and a verdict against travel.max_m; create_paths=true
//                          keeps one PathOfTravel element per room in the plan for review
//                          (dry_run -> confirmation_token -> apply, re-read after commit).
//
// MEASURING OPENS NO TRANSACTION AT ALL. FindShortestPaths, FindEndsOfShortestPaths
// and FindStartsOfLongestPathsFromRooms are computations: nothing is created, so
// there is nothing to roll back. Only create_paths writes.
//
// THE FARTHEST POINT, HONESTLY. FindStartsOfLongestPathsFromRooms has no room argument
// and returns the worst point(s) of the WHOLE plan. Per room, candidates are routed
// instead (Core/EgressTravelRules.cs says why corners): boundary corners pulled 300 mm
// toward the room's point - each kept only if Revit's own room lookup still puts it in
// THIS room - the room's point, and any whole-plan longest start inside the room. The
// longest routed candidate is a LOWER bound on the room's number, reported as one: a
// certain fail above the limit. A pass needs a proven ceiling (EgressTravelRules.
// UpperBound), which exists only for a convex room of straight walls, no island, and
// every candidate routed; a candidate that found no route is counted and named.
//
// ONE LEVEL, ONE PHASE, NO CROP, STATED. The service is two-dimensional and flattens
// every destination's Z onto the view's level, so exits are filtered to the plan's own
// level and a level with no matching exit is not_assessable (egress via a stair). The
// plan's obstacles are its phase's and its design options', so a room of another phase
// or of a secondary option is out of scope. An active crop box makes Revit ignore what
// lies outside it (PointOutsideActiveCrop, ResultAffectedByCrop) and makes Create throw,
// so a cropped plan is refused before anything is measured.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using System.Globalization;

namespace Horizun.Revit.Commands
{
    public sealed partial class CodeCheckCommand
    {
        private const double TravelFeetToMm = AccessGeometry.MillimetresPerFoot;
        private const int MaxCornersPerRoom = 24;
        private const int MaxPolylinePoints = 200;
        private const string CeilingAssumes = "a straight walk inside the convex room to a routed sample point is clear of obstacles";

        /// <summary>One room's egress measurement. Distances in millimetres, points in Revit feet.</summary>
        private sealed class TravelRow
        {
            public long RoomId;
            public string Number, Name, Level;
            public long ViewId = -1;
            public double? DistanceMm, UpperMm;
            public XYZ Start, End;
            public long ExitId = -1;
            public IList<XYZ> Path;
            public int Candidates, Routed, Dropped;
            public bool UsedLongestSearch;
            public string NotAssessable, Unavailable, OutOfScope, NoCeiling;
        }

        /// <summary>A room's sample points, its outer boundary vertices, and why no ceiling can be proven (null = it can).</summary>
        private sealed class CandidateSet
        {
            public List<XYZ> Points = new List<XYZ>();
            public List<PlanPoint> Polygon = new List<PlanPoint>();
            public int Dropped;
            public string NoCeiling;
        }

        // =====================================================================
        // Measurement, shared by both entry points
        // =====================================================================

        private static List<TravelRow> MeasureTravel(Document doc, IList<ViewPlan> plans, List<FamilyInstance> exits,
                                                     HashSet<long> roomScope, out JObject coverage)
        {
            var rows = new List<TravelRow>();
            var planByLevel = new Dictionary<long, ViewPlan>();
            foreach (ViewPlan p in plans)
            {
                Level lv = null;
                try { lv = p.GenLevel; } catch { }
                if (lv != null && !planByLevel.ContainsKey(Rid.Value(lv.Id))) planByLevel[Rid.Value(lv.Id)] = p;
            }

            IEnumerable<Room> rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().OfType<Room>();
            if (roomScope != null) rooms = rooms.Where(r => roomScope.Contains(Rid.Value(r.Id)));

            int notAskedOutOfScope = 0;
            var byPlan = new Dictionary<long, List<(TravelRow row, Room room)>>();
            foreach (Room room in rooms)
            {
                string level = Safe(() => room.Level?.Name);
                planByLevel.TryGetValue(Rid.Value(room.LevelId), out ViewPlan plan);
                string outOfScope = plan == null
                    ? "no given plan view shows level '" + (level ?? "?") + "'. Revit's path of travel runs in ONE floor plan " +
                      "per level; give a plan of this level to measure it."
                    : ScopeMismatch(room, plan);
                // A room nobody asked about (room_ids omitted) outside every given plan is not a
                // result: it is counted, not reported, so it does not inflate the summary.
                if (outOfScope != null && roomScope == null) { notAskedOutOfScope++; continue; }

                var row = new TravelRow
                {
                    RoomId = Rid.Value(room.Id), Number = Safe(() => room.Number), Name = Safe(() => room.Name), Level = level
                };
                rows.Add(row);
                if (outOfScope != null) { row.OutOfScope = outOfScope; continue; }
                double area = 0;
                try { area = room.Area; } catch { }
                if (area <= 0 || room.Location == null) { row.Unavailable = "the room is not placed or not enclosed; it has no area to route from."; continue; }
                row.ViewId = Rid.Value(plan.Id);
                if (!byPlan.TryGetValue(row.ViewId, out var list)) byPlan[row.ViewId] = list = new List<(TravelRow, Room)>();
                list.Add((row, room));
            }

            var gaps = new List<string>();
            int unattributed = 0;
            foreach (var kv in byPlan)
            {
                ViewPlan plan = (ViewPlan)doc.GetElement(Rid.Make(kv.Key));
                ElementId levelId = plan.GenLevel.Id;
                List<FamilyInstance> levelExits = exits.Where(d => d.LevelId != null && d.LevelId.Equals(levelId)).ToList();
                var destinations = levelExits.Select(d => (d.Location as LocationPoint)?.Point).ToList();
                var exitIds = new List<long>();
                var destPoints = new List<XYZ>();
                for (int i = 0; i < levelExits.Count; i++)
                    if (destinations[i] != null) { destPoints.Add(destinations[i]); exitIds.Add(Rid.Value(levelExits[i].Id)); }
                if (destPoints.Count == 0)
                {
                    foreach (var (row, _) in kv.Value)
                        row.NotAssessable = "no declared exit matched on level '" + (row.Level ?? "?") + "'. If egress from this level " +
                                            "runs through a stair or another level, Revit's per-level path of travel cannot route it; " +
                                            "if an exit door is on this level, widen the exits selector.";
                    continue;
                }

                // The whole plan's worst start(s), attributed to rooms by Revit's own lookup.
                AccessGeometry.LongestPathResult longest = AccessGeometry.LongestPathStarts(plan, destPoints);
                var longestByRoom = new Dictionary<long, List<XYZ>>();
                int outside = 0;
                foreach (XYZ pt in longest.Starts)
                {
                    Room r = AccessGeometry.RoomForPoint(plan, pt);
                    if (r == null) { outside++; continue; }
                    long id = Rid.Value(r.Id);
                    if (!longestByRoom.ContainsKey(id)) longestByRoom[id] = new List<XYZ>();
                    longestByRoom[id].Add(pt);
                }
                if (longest.Problem != null) gaps.Add("view " + kv.Key + ": " + longest.Problem);
                if (outside > 0)
                {
                    unattributed += outside;
                    gaps.Add("view " + kv.Key + ": " + outside + " of " + longest.Starts.Count + " whole-plan longest start(s) fell in no " +
                             "room by Revit's lookup and were not attributed; each room still has its own samples.");
                }

                var starts = new List<XYZ>();
                var owner = new List<int>();
                var fromLongest = new List<bool>();
                var sets = new List<CandidateSet>();
                for (int k = 0; k < kv.Value.Count; k++)
                {
                    var (row, room) = kv.Value[k];
                    CandidateSet cs = Candidates(room, plan);
                    sets.Add(cs);
                    row.Dropped = cs.Dropped;
                    foreach (XYZ c in cs.Points)
                    { starts.Add(c); owner.Add(k); fromLongest.Add(false); row.Candidates++; }
                    if (longestByRoom.TryGetValue(row.RoomId, out List<XYZ> extra))
                        foreach (XYZ c in extra) { starts.Add(c); owner.Add(k); fromLongest.Add(true); row.Candidates++; }
                    if (row.Candidates == 0)
                        row.Unavailable = "no sample point of the room is inside it by Revit's own room lookup (" + cs.Dropped +
                                          " dropped); the room reports no usable location point or boundary.";
                }
                if (starts.Count == 0) continue;

                IList<IList<XYZ>> paths;
                try
                {
                    // Revit's signature is (view, DESTINATIONS, STARTS): backwards against every
                    // expectation, and a swap answers the wrong question plausibly.
                    paths = PathOfTravel.FindShortestPaths(plan, destPoints, starts);
                }
                catch (Exception ex)
                {
                    string why = "Revit's path-of-travel service did not run in view " + kv.Key + ": " + ex.Message +
                                 ". A gap in the MEASUREMENT, not a finding about the model.";
                    foreach (var (row, _) in kv.Value) if (row.Unavailable == null) row.Unavailable = why;
                    gaps.Add(why);
                    continue;
                }

                var chosen = new List<(TravelRow row, int start)>();
                for (int k = 0; k < kv.Value.Count; k++)
                {
                    TravelRow row = kv.Value[k].row;
                    var idx = new List<int>();
                    var dist = new List<double?>();
                    for (int s = 0; s < starts.Count; s++)
                    {
                        if (owner[s] != k) continue;
                        IList<XYZ> path = paths != null && s < paths.Count ? paths[s] : null;
                        idx.Add(s);
                        dist.Add(path != null && path.Count >= 2
                            ? EgressTravelRules.Length(path.Select(p => new PlanPoint(p.X, p.Y)).ToList()) * TravelFeetToMm
                            : (double?)null);
                    }
                    row.Routed = dist.Count(d => d.HasValue);
                    int best = EgressTravelRules.FarthestIndex(dist);
                    if (best < 0)
                    {
                        if (row.Unavailable == null && row.Candidates > 0)
                            row.Unavailable = "no route was computed from any of " + row.Candidates + " sample points in this room to a " +
                                              "declared exit on its level (a start inside an obstacle, or no connection). A gap in the " +
                                              "measurement, not a verdict.";
                        continue;
                    }
                    int s0 = idx[best];
                    row.DistanceMm = dist[best];
                    row.Start = starts[s0];
                    row.Path = paths[s0];
                    row.UsedLongestSearch = fromLongest[s0];
                    chosen.Add((row, s0));

                    // The ceiling - only where it is proven (see EgressTravelRules.UpperBound).
                    CandidateSet cs = sets[k];
                    int unrouted = row.Candidates - row.Routed;
                    row.NoCeiling = cs.NoCeiling
                        ?? (unrouted > 0 ? unrouted + " of " + row.Candidates + " sample points found no route (a start inside " +
                                           "furniture or another obstacle, or no connection): the room's interior is not clear, so " +
                                           "no ceiling is proven and the farthest points were not all measured." : null)
                        ?? (cs.Dropped > 0 ? cs.Dropped + " inset corner(s) fell outside the room by Revit's own lookup." : null);
                    if (row.NoCeiling == null)
                    {
                        var routed = new List<(PlanPoint, double)>();
                        for (int j = 0; j < idx.Count; j++)
                            if (dist[j].HasValue) routed.Add((new PlanPoint(starts[idx[j]].X, starts[idx[j]].Y), dist[j].Value / TravelFeetToMm));
                        double? upperFt = EgressTravelRules.UpperBound(routed, cs.Polygon);
                        if (upperFt.HasValue) row.UpperMm = upperFt.Value * TravelFeetToMm;
                        else row.NoCeiling = "no routed sample or no boundary vertex to bound the room with.";
                    }
                }

                // Which exit each room's longest route ends at: Revit's own nearest-destination answer.
                if (chosen.Count == 0) continue;
                IList<XYZ> ends = null;
                try { ends = PathOfTravel.FindEndsOfShortestPaths(plan, destPoints, chosen.Select(c => starts[c.start]).ToList()); }
                catch (Exception ex) { gaps.Add("view " + kv.Key + ": FindEndsOfShortestPaths did not run: " + ex.Message); }
                var destPlan = destPoints.Select(p => new PlanPoint(p.X, p.Y)).ToList();
                for (int c = 0; c < chosen.Count; c++)
                {
                    TravelRow row = chosen[c].row;
                    XYZ end = ends != null && c < ends.Count ? ends[c] : row.Path[row.Path.Count - 1];
                    int exit = EgressTravelRules.NearestIndex(new PlanPoint(end.X, end.Y), destPlan, 1.0);
                    row.End = exit >= 0 ? destPoints[exit] : end;
                    row.ExitId = exit >= 0 ? exitIds[exit] : -1;
                }
            }

            int measured = rows.Count(r => r.DistanceMm.HasValue);
            coverage = AccessGeometry.Coverage("egress.travel_distance", rows.Count, measured, MeasurementBasis.TravelPath, gaps);
            coverage["not_assessable"] = rows.Count(r => r.NotAssessable != null);
            coverage["out_of_scope"] = rows.Count(r => r.OutOfScope != null);
            coverage["rooms_not_asked_out_of_scope"] = notAskedOutOfScope;
            coverage["with_proven_ceiling"] = rows.Count(r => r.UpperMm.HasValue);
            coverage["sample_points_unrouted"] = rows.Where(r => r.DistanceMm.HasValue).Sum(r => r.Candidates - r.Routed);
            coverage["sample_points_dropped_outside_room"] = rows.Sum(r => r.Dropped);
            coverage["longest_starts_unattributed"] = unattributed;
            coverage["views"] = new JArray(plans.Select(p => Rid.Value(p.Id)));
            return rows;
        }

        /// <summary>
        /// Why a room is outside what this plan can route, or null: another phase than the view's
        /// (the obstacles are that phase's), or a secondary design option (the plan routes against
        /// the main model and primary options).
        /// </summary>
        private static string ScopeMismatch(Room room, ViewPlan plan)
        {
            try
            {
                Parameter rp = room.get_Parameter(BuiltInParameter.ROOM_PHASE);
                Parameter vp = plan.get_Parameter(BuiltInParameter.VIEW_PHASE);
                ElementId roomPhase = rp != null && rp.StorageType == StorageType.ElementId ? rp.AsElementId() : null;
                ElementId viewPhase = vp != null && vp.StorageType == StorageType.ElementId ? vp.AsElementId() : null;
                if (roomPhase != null && viewPhase != null && roomPhase != ElementId.InvalidElementId &&
                    viewPhase != ElementId.InvalidElementId && !roomPhase.Equals(viewPhase))
                    return "the room belongs to phase '" + (room.Document.GetElement(roomPhase)?.Name ?? "?") + "', not the plan's phase '" +
                           (room.Document.GetElement(viewPhase)?.Name ?? "?") + "': the plan's obstacles are another phase's.";
            }
            catch { /* unreadable phase: measured, and the plan's phase is what the reply names */ }
            try
            {
                DesignOption option = room.DesignOption;
                if (option != null && !option.IsPrimary)
                    return "the room is in the secondary design option '" + option.Name + "'; the plan routes against the main " +
                           "model and primary options.";
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Boundary corners pulled inward toward the room's point, each kept only if Revit's room
        /// lookup puts it in THIS room, plus the point itself; the outer boundary's vertices; and
        /// why a ceiling cannot be proven for this room (curved wall, island, not convex).
        /// </summary>
        private static CandidateSet Candidates(Room room, ViewPlan plan)
        {
            var set = new CandidateSet();
            XYZ centre = null;
            try { centre = (room.Location as LocationPoint)?.Point; } catch { }
            try
            {
                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (loops == null || loops.Count == 0) set.NoCeiling = "the room has no boundary to bound it with.";
                else
                {
                    if (loops.Count > 1)
                        set.NoCeiling = "the room has an island (a column or an inner boundary): the straight walk a ceiling assumes may be blocked.";
                    foreach (BoundarySegment seg in loops[0])
                    {
                        Curve curve = seg.GetCurve();
                        if (curve == null) continue;
                        if (!(curve is Line) && set.NoCeiling == null)
                            set.NoCeiling = "a boundary segment is curved: the room's vertices do not bound it.";
                        XYZ p = curve.GetEndPoint(0);
                        set.Polygon.Add(new PlanPoint(p.X, p.Y));
                    }
                }
            }
            catch { set.NoCeiling = "the room's boundary could not be read."; }
            if (set.NoCeiling == null && !EgressTravelRules.IsConvex(set.Polygon))
                set.NoCeiling = "the room is not convex: a point behind a corner can be farther than every sample.";

            // Internal-coordinate Z (see AccessGeometry.RoomForPoint): Level.Elevation follows the
            // level's Elevation Base and can be relative to a relocated survey point.
            double z = plan.GenLevel?.ProjectElevation ?? 0;
            if (centre == null) return set;
            foreach (PlanPoint q in EgressTravelRules.InsetToward(set.Polygon, new PlanPoint(centre.X, centre.Y),
                                                                  EgressTravelRules.DefaultInsetMm / TravelFeetToMm).Take(MaxCornersPerRoom))
            {
                // Moving toward the room's point stays inside a convex room, not a non-convex one:
                // a corner of an L moved toward a point in the other arm lands in a wall or in the
                // next space, whose distance must never be credited to this room.
                var p = new XYZ(q.X, q.Y, z);
                if (InRoom(plan, p, room)) set.Points.Add(p); else set.Dropped++;
            }
            var c = new XYZ(centre.X, centre.Y, z);
            if (!set.Points.Any(p => p.DistanceTo(c) < 1e-9))
            {
                if (InRoom(plan, c, room)) set.Points.Add(c); else set.Dropped++;
            }
            return set;
        }

        private static bool InRoom(ViewPlan plan, XYZ p, Room room)
        {
            Room at = AccessGeometry.RoomForPoint(plan, p);
            return at != null && at.Id.Equals(room.Id);
        }

        private static string Safe(Func<string> read) { try { return read(); } catch { return null; } }

        private static string TravelBasis(TravelRow r) =>
            "measured_travel_path: a LOWER bound - the longest of " + r.Routed + " routed of " + r.Candidates + " sample points in " +
            "the room (corners inset " + EgressTravelRules.DefaultInsetMm + " mm and kept only inside the room, room point" +
            (r.UsedLongestSearch ? ", Revit's whole-plan longest start" : "") + ") to its nearest declared exit" +
            (r.UpperMm.HasValue
                ? "; proven ceiling " + Math.Round(r.UpperMm.Value / 1000, 3).ToString(CultureInfo.InvariantCulture) + " m, assuming " + CeilingAssumes
                : "; no ceiling: " + (r.NoCeiling ?? "not computed."));

        private static JObject RowJson(TravelRow r, double? maxM)
        {
            double? lower = r.DistanceMm.HasValue ? r.DistanceMm.Value / 1000.0 : (double?)null;
            double? upper = r.UpperMm.HasValue ? r.UpperMm.Value / 1000.0 : (double?)null;
            string outcome = EgressTravelRules.Evaluate(lower, upper, maxM, r.NotAssessable);
            var o = new JObject
            {
                ["room_id"] = r.RoomId, ["number"] = r.Number, ["name"] = r.Name, ["level"] = r.Level,
                ["view_id"] = r.ViewId >= 0 ? (JToken)r.ViewId : JValue.CreateNull(),
                ["distance_m"] = lower.HasValue ? (JToken)Math.Round(lower.Value, 3) : JValue.CreateNull(),
                ["bound"] = "lower",
                // Rounded UP: a ceiling rounded down would be a ceiling nobody proved.
                ["upper_bound_m"] = upper.HasValue ? (JToken)(Math.Ceiling(upper.Value * 1000) / 1000) : JValue.CreateNull(),
                ["outcome"] = outcome,
                ["exit_door_id"] = r.ExitId >= 0 ? (JToken)r.ExitId : JValue.CreateNull(),
                ["candidates"] = r.Candidates, ["routed"] = r.Routed, ["dropped_outside_room"] = r.Dropped
            };
            if (r.NotAssessable != null) o["reason"] = r.NotAssessable;
            else if (r.OutOfScope != null) o["reason"] = "out of scope: " + r.OutOfScope;
            else if (r.Unavailable != null) o["reason"] = r.Unavailable;
            else if (outcome == EgressTravelRules.NotDecidable && lower.HasValue)
                o["reason"] = "the routed " + Math.Round(lower.Value, 3).ToString(CultureInfo.InvariantCulture) + " m is a LOWER bound under the limit, and the true farthest " +
                              "point may exceed it: " + (upper.HasValue ? "the proven ceiling " + Math.Round(upper.Value, 3).ToString(CultureInfo.InvariantCulture) + " m is over the limit."
                                                                         : "no ceiling - " + (r.NoCeiling ?? "not computed."));
            if (r.DistanceMm.HasValue)
            {
                o["basis"] = TravelBasis(r);
                if (upper.HasValue) o["ceiling_assumes"] = CeilingAssumes;
                o["start_m"] = new JArray(Math.Round(r.Start.X * TravelFeetToMm / 1000, 3), Math.Round(r.Start.Y * TravelFeetToMm / 1000, 3));
                o["polyline_m"] = new JArray(r.Path.Take(MaxPolylinePoints).Select(p =>
                    new JArray(Math.Round(p.X * TravelFeetToMm / 1000, 3), Math.Round(p.Y * TravelFeetToMm / 1000, 3))));
            }
            return o;
        }

        // =====================================================================
        // operation=check: fill each travel rule's own Measures key before evaluation
        // =====================================================================

        private static void AttachTravelDistance(Document doc, List<CheckedElement> facts, RequirementSet set, JObject coverage)
        {
            List<CheckedElement> rooms = facts.Where(f => f.CategoryToken == "OST_Rooms").ToList();
            var scope = new HashSet<long>(rooms.Select(r => r.Id));
            var perRule = new JObject();
            // One routing per distinct (views, exits): two rules with the same config share it,
            // two with different exits never do.
            var byConfig = new Dictionary<string, (string refusal, Dictionary<long, TravelRow> rows, JObject cov)>();
            foreach (Requirement rule in set.Rules.Where(r => r.AssertionMeasure == "travel_distance_m"))
            {
                if (rule.Config == null)
                {
                    // Nothing is written under this rule's key: MeasureFor answers not_decidable itself.
                    perRule[rule.Id ?? ""] = new JObject { ["not_covered"] = true, ["reason"] = "this rule has no config (route_view_id and exits)." };
                    continue;
                }
                string configKey = new JObject
                {
                    ["one"] = rule.Config["route_view_id"]?.DeepClone(), ["many"] = rule.Config["route_view_ids"]?.DeepClone(),
                    ["exits"] = rule.Config["exits"]?.DeepClone()
                }.ToString(Formatting.None);
                if (!byConfig.TryGetValue(configKey, out var m))
                {
                    m = MeasureForConfig(doc, rule.Config, scope);
                    byConfig[configKey] = m;
                }
                perRule[rule.Id ?? ""] = m.refusal != null ? new JObject { ["not_covered"] = true, ["reason"] = m.refusal } : m.cov;
                string key = CodeCheckRules.TravelKey(rule);
                foreach (CheckedElement fact in rooms) fact.Measures[key] = TravelMeasure(m.refusal, m.rows, fact.Id);
            }
            coverage["travel_distance"] = perRule;
        }

        private static (string refusal, Dictionary<long, TravelRow> rows, JObject cov) MeasureForConfig(Document doc, JObject cfg, HashSet<long> scope)
        {
            List<ViewPlan> plans = Plans(doc, cfg, "route_view_id", "route_view_ids", out string refusal);
            if (refusal != null) return (refusal, null, null);
            JObject exitsCfg = cfg["exits"] as JObject;
            if (exitsCfg == null) return ("config.exits must select the exit doors: {parameter, value?}, {mark_prefix} or {element_ids}.", null, null);
            List<FamilyInstance> exits = AuditAccessCommand.ResolveExits(doc, exitsCfg, out refusal);
            if (refusal != null) return (refusal, null, null);
            if (exits == null || exits.Count == 0) return ("config.exits matched no door in this document.", null, null);
            List<TravelRow> rows = MeasureTravel(doc, plans, exits, scope, out JObject cov);
            return (null, rows.ToDictionary(r => r.RoomId), cov);
        }

        private static MeasuredValue TravelMeasure(string refusal, Dictionary<long, TravelRow> rows, long roomId)
        {
            if (refusal != null) return MeasuredValue.None(refusal);
            if (!rows.TryGetValue(roomId, out TravelRow row)) return MeasuredValue.None("the room could not be re-read.");
            if (row.NotAssessable != null) return MeasuredValue.None("not_assessable: " + row.NotAssessable);
            if (row.OutOfScope != null) return MeasuredValue.None("out of scope for this rule's route view: " + row.OutOfScope);
            if (!row.DistanceMm.HasValue) return MeasuredValue.None(row.Unavailable ?? "no route was measured.");
            return new MeasuredValue
            {
                Value = row.DistanceMm.Value / 1000.0,
                Bound = "lower",
                Upper = row.UpperMm.HasValue ? row.UpperMm.Value / 1000.0 : (double?)null,
                Basis = TravelBasis(row),
                Detail = new JObject
                {
                    ["view_id"] = row.ViewId, ["exit_door_id"] = row.ExitId >= 0 ? (JToken)row.ExitId : JValue.CreateNull(),
                    ["candidates"] = row.Candidates, ["routed_points"] = row.Routed, ["dropped_outside_room"] = row.Dropped,
                    ["upper_bound_m"] = row.UpperMm.HasValue ? (JToken)(Math.Ceiling(row.UpperMm.Value) / 1000) : JValue.CreateNull(),
                    ["no_ceiling"] = row.NoCeiling
                }
            };
        }

        private static List<ViewPlan> Plans(Document doc, JObject cfg, string one, string many, out string refusal)
        {
            refusal = null;
            var ids = new List<long>();
            if (cfg[many] is JArray arr) ids.AddRange(arr.Select(t => t.Value<long?>() ?? -1));
            else if (cfg[one] != null) ids.Add(cfg.Value<long?>(one) ?? -1);
            if (ids.Count == 0) { refusal = one + " (or " + many + ") is missing: path of travel runs IN a floor plan view."; return null; }
            var plans = new List<ViewPlan>();
            foreach (long id in ids)
            {
                var p = id >= 0 && Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as ViewPlan : null;
                if (p == null || p.ViewType != ViewType.FloorPlan || p.IsTemplate || p.GenLevel == null)
                { refusal = "view " + id + " is not a floor plan view of a level in this document."; return null; }
                bool cropped = false;
                try { cropped = p.CropBoxActive; } catch { }
                if (cropped)
                {
                    // RevitAPI.xml: Create throws when the crop is active and a point lies outside it;
                    // PathOfTravelCalculationStatus has PointOutsideActiveCrop and ResultAffectedByCrop.
                    refusal = "view " + id + " has its crop box active: Revit's path of travel ignores what lies outside the crop, so " +
                              "a route could be longer, missing, or impossible to keep. Give a floor plan with its crop off. Nothing was measured.";
                    return null;
                }
                plans.Add(p);
            }
            return plans;
        }

        // =====================================================================
        // operation=travel_distance
        // =====================================================================

        private CommandResult ExecuteTravel(UIApplication app, JObject request)
        {
            JObject travel = request["travel"] as JObject;
            if (travel == null)
                return CommandResult.Fail("operation=travel_distance needs travel: {view_ids, exits, room_ids?, max_m?, create_paths?}.");
            bool createPaths = travel.Value<bool?>("create_paths") ?? false;

            GateResult gate = null;
            Document doc;
            if (createPaths)
            {
                // The tool stays listed on a read_only machine because check and measuring write
                // nothing (Settings.IsToolAllowed); the one write it has is refused here instead.
                if (Horizun.Revit.Core.Settings.PermissionProfile == "read_only")
                    return CommandResult.Fail("travel.create_paths writes PathOfTravel elements and is refused by permission_profile=read_only. " +
                                              "Measure without create_paths; nothing was changed.");
                gate = DocumentGate.ForMutation(app, request, Name);
                if (!gate.Ok) return gate.Refusal;
                doc = gate.Document;
            }
            else
            {
                doc = app?.ActiveUIDocument?.Document;
                if (doc == null) return CommandResult.Fail("No active Revit document.");
                CommandResult wrong = DocumentGate.ReadGuard(doc, request, Name);
                if (wrong != null) return wrong;
            }

            List<ViewPlan> plans = Plans(doc, travel, "view_id", "view_ids", out string refusal);
            if (refusal != null) return CommandResult.Fail(refusal + " Nothing was measured.");
            JObject exitsCfg = travel["exits"] as JObject;
            if (exitsCfg == null)
                return CommandResult.Fail("travel.exits must select the exit doors: {parameter, value?}, {mark_prefix} or {element_ids}. " +
                                          "Nothing in a Revit model reliably marks an exit, so it is never guessed.");
            List<FamilyInstance> exits = AuditAccessCommand.ResolveExits(doc, exitsCfg, out refusal);
            if (refusal != null) return CommandResult.Fail(refusal);
            if (exits == null || exits.Count == 0) return CommandResult.Fail("travel.exits matched no door in this document. Nothing was measured.");
            HashSet<long> scope = travel["room_ids"] is JArray ra ? new HashSet<long>(ra.Select(t => t.Value<long?>() ?? -1)) : null;
            double? maxM = travel.Value<double?>("max_m");

            var coverage = new JObject();
            List<TravelRow> rows = MeasureTravel(doc, plans, exits, scope, out JObject cov);
            coverage["travel_distance"] = cov;
            var outRows = new JArray(rows.Select(r => RowJson(r, maxM)));
            var head = new JObject
            {
                ["document"] = doc.Title,
                ["operation"] = "travel_distance",
                ["views"] = new JArray(plans.Select(p => Rid.Value(p.Id))),
                ["exit_door_ids"] = new JArray(exits.Select(d => Rid.Value(d.Id))),
                ["max_m"] = maxM.HasValue ? (JToken)maxM.Value : JValue.CreateNull(),
                ["summary"] = EgressTravelRules.Summary(outRows.Select(r => (string)r["outcome"])),
                ["rooms"] = outRows,
                ["coverage"] = coverage,
                ["limits"] = new JArray(
                    "one level per plan view: a stair or another level is not_assessable, never flattened into this number",
                    "obstacles are what the given plan view shows, in its phase (hidden furniture is not an obstacle); a cropped plan is refused",
                    "distance_m is a LOWER bound (the longest routed sample): over max_m is a certain fail; a pass needs upper_bound_m, " +
                    "proven only for a convex room of straight walls with every sample routed, assuming " + CeilingAssumes)
            };
            if (!createPaths) return CommandResult.Ok(head);
            return KeepPaths(app, gate, request, doc, rows, head);
        }

        /// <summary>create_paths=true: one PathOfTravel per measured room, dry_run -> token -> apply, re-read after commit.</summary>
        private CommandResult KeepPaths(UIApplication app, GateResult gate, JObject request, Document doc, List<TravelRow> rows, JObject head)
        {
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            List<TravelRow> ready = rows.Where(r => r.DistanceMm.HasValue && r.Start != null && r.End != null).ToList();
            int unresolved = rows.Count - ready.Count;

            var scope = new JObject
            {
                ["op"] = "travel_distance.create_paths", ["travel"] = request["travel"]?.DeepClone(),
                ["paths"] = new JArray(ready.Select(r => new JArray(r.RoomId, r.ViewId, Math.Round(r.Start.X, 4), Math.Round(r.Start.Y, 4),
                                                                     Math.Round(r.End.X, 4), Math.Round(r.End.Y, 4))))
            };
            string planHash;
            using (SHA256 sha = SHA256.Create())
                planHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(scope.ToString(Formatting.None)))).Replace("-", "");
            var plan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (TravelRow r in ready)
                plan.Elements.Add(new PlannedElement
                {
                    Action = PlannedAction.Create, Category = "OST_PathOfTravelLines",
                    ProposedValues = new Dictionary<string, string> { ["room_id"] = r.RoomId.ToString(), ["view_id"] = r.ViewId.ToString() }
                });

            if (dryRun)
            {
                head["dry_run"] = true;
                head["would_create_paths"] = ready.Count;
                DocumentGate.RecordResolvedPlan(plan);
                DocumentGate.StampConfirmation(head, gate, Name, planHash, true,
                    "the token binds the views, the exits and each room's routed start and exit end.");
                ApplicationOutcome.StampRehearsal(head, rows.Count, unresolved, 0, 0);
                return CommandResult.Ok(head);
            }
            if (ready.Count == 0)
                return CommandResult.Fail("Nothing to keep: no room was measured, so no path of travel can be created. Nothing was changed.");
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null);
            if (refusal != null) return refusal;

            var created = new List<(TravelRow row, ElementId id, string status)>();
            TransactionStatus status = TransactionStatus.Uninitialized;
            using (var tx = new Transaction(doc, "Horizun: keep egress paths of travel"))
            {
                tx.Start();
                try
                {
                    foreach (TravelRow r in ready)
                    {
                        // One room's refusal (start and end too close, a split crop) is that room's
                        // unverified row, never every other room's lost path.
                        try
                        {
                            var view = (View)doc.GetElement(Rid.Make(r.ViewId));
                            PathOfTravel pot = PathOfTravel.Create(view, r.Start, r.End, out PathOfTravelCalculationStatus st);
                            created.Add((r, pot?.Id, st.ToString()));
                        }
                        catch (Exception ex) when (!(ex is OutOfMemoryException))
                        {
                            created.Add((r, null, "not created: " + ex.GetType().Name + ": " + ex.Message));
                        }
                    }
                    try { status = Guard.Commit(tx, "egress paths of travel"); }
                    catch (SilentRollbackException ex) { status = ex.Status; }
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                    throw;
                }
            }

            // Re-read from the committed model: the element exists, lives in the plan, and is the measured route.
            int verified = 0, unverified = 0;
            var verification = new JArray();
            foreach (var (row, id, st) in created)
            {
                var pot = id == null ? null : doc.GetElement(id) as PathOfTravel;
                double rereadMm = 0;
                try { if (pot != null) rereadMm = pot.GetCurves().Sum(c => c.Length) * TravelFeetToMm; } catch { }
                bool inView = pot != null && Rid.Value(pot.OwnerViewId) == row.ViewId;
                bool agrees = EgressTravelRules.LengthsAgree(row.DistanceMm.Value, rereadMm, out double delta);
                bool ok = inView && agrees;
                if (ok) verified++; else unverified++;
                verification.Add(new JObject
                {
                    ["room_id"] = row.RoomId, ["path_id"] = pot != null ? (JToken)Rid.Value(pot.Id) : JValue.CreateNull(),
                    ["creation_status"] = st, ["length_m"] = Math.Round(rereadMm / 1000, 3),
                    ["delta_mm"] = Math.Round(delta, 1), ["verified"] = ok
                });
            }
            head["dry_run"] = false;
            head["paths"] = verification;
            head["paths_verified"] = verified;
            DocumentGate.StampConfirmation(head, gate, Name, planHash, false);
            ApplicationOutcome.Stamp(head, WriteTally.PerTarget(status.ToString(), ready.Count, unresolved, verified, unverified));
            return status == TransactionStatus.Committed && unverified == 0
                ? CommandResult.Ok(head)
                : CommandResult.FailWithDetail("Paths of travel were not all verified after the commit (" + unverified +
                                               " unverified, commit " + status + "). See paths.", head);
        }
    }
}
