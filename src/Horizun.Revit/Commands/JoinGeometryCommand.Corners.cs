// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_join_geometry operation=corners (read) and fix_corners (write): wall ends that
// join no wall although another, not parallel, is within `tolerance` - the L and T corners
// that "no encajan" (field cycle A3/A6). Geometry and verdicts live in Core/WallCornerRules.
//
// fix_corners, per open end: WallUtils.AllowWallJoinAtEnd when Revit has that end
// disallowed, and the end moved to the crossing of the two location lines (the other end
// stays put). Through ModelEditRunner: rehearse, token, one transaction, and after the
// commit each fixed end must list its partner in LocationCurve.ElementsAtJoin - else the
// whole edit rolls back. Ends whose crossing is beyond the tolerance are reported, never moved.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class JoinGeometryCommand
    {
        private static ArchModelEdit PlanCorners(Document doc, JObject r, double scale, string op)
        {
            double tolUnits = r["tolerance"] != null && r["tolerance"].Type != JTokenType.Null
                ? (double)r["tolerance"] : WallCornerRules.DefaultToleranceMm / 304.8 / scale;
            double tolFeet = tolUnits * scale;
            if (tolFeet > WallCornerRules.MaxToleranceMm / 304.8)
                throw new ArgumentException("tolerance is above " + WallCornerRules.MaxToleranceMm + " mm: a corner that far apart is a design question, not a join. Nothing was read.");

            var walls = new List<WallSegment>();
            var byId = new Dictionary<long, Wall>();
            var skipped = new JArray();
            foreach (long id in ((JArray)r["element_ids"]).Select(t => t.Value<long>()))
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null) throw new ArgumentException("element " + id + " is not an element of this document. Nothing was read.");
                if (!(e is Wall w)) { skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "not a wall" }); continue; }
                if (w.GroupId != null && w.GroupId != ElementId.InvalidElementId) { skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "member of a group" }); continue; }
                if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line line)) { skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "not a straight wall (arc or no location line)" }); continue; }
                BoundingBoxXYZ bb = w.get_BoundingBox(null);
                var seg = new WallSegment
                {
                    Id = id, X0 = line.GetEndPoint(0).X, Y0 = line.GetEndPoint(0).Y, X1 = line.GetEndPoint(1).X, Y1 = line.GetEndPoint(1).Y,
                    ZMin = bb?.Min.Z ?? line.GetEndPoint(0).Z, ZMax = bb?.Max.Z ?? line.GetEndPoint(0).Z,
                    JoinAllowedAtStart = WallUtils.IsWallJoinAllowedAtEnd(w, 0), JoinAllowedAtEnd = WallUtils.IsWallJoinAllowedAtEnd(w, 1)
                };
                seg.JoinedAtStart = JoinedWalls(lc, 0); seg.JoinedAtEnd = JoinedWalls(lc, 1);
                walls.Add(seg); byId[id] = w;
            }
            List<OpenCorner> corners = WallCornerRules.Find(walls, tolFeet);
            int originalCandidateCount = corners.Count;
            JObject linkedTerminations = ReadLinkedFinishTerminations(doc, walls, byId, corners);
            corners = WallCornerRules.Find(walls, tolFeet);
            double k = 1 / scale;
            JObject Row(OpenCorner c) => new JObject
            {
                ["wall_id"] = c.WallId, ["end"] = c.End == 0 ? "start" : "end", ["other_wall_id"] = c.OtherId, ["kind"] = c.Kind,
                ["gap"] = Math.Round(c.GapFeet * k, 1), ["move"] = Math.Round(c.MoveFeet * k, 1),
                ["to"] = c.ToX.HasValue ? new JArray(Math.Round(c.ToX.Value * k, 1), Math.Round(c.ToY.Value * k, 1)) : null,
                ["join_disallowed"] = c.NeedsAllowJoin, ["action"] = c.Action, ["reason"] = c.Reason
            };
            var fixable = corners.Where(c => c.Action == "allow_join" || c.Action == "extend" || c.Action == "allow_join_and_extend").ToList();
            // the other end of an L that closes by its partner's move is verified joined too, never written
            var closing = corners.Where(c => c.Action == WallCornerRules.ClosedByOther && fixable.Any(f => f.WallId == c.OtherId)).ToList();
            var summary = new JObject
            {
                ["operation"] = op, ["walls_read"] = walls.Count, ["skipped"] = skipped,
                ["tolerance"] = Math.Round(tolUnits, 3), ["open_corners"] = corners.Count,
                ["location_line_candidates"] = originalCandidateCount,
                ["linked_core_terminations"] = linkedTerminations,
                ["coverage_complete"] = linkedTerminations.Value<bool>("coverage_complete"),
                ["partial"] = !linkedTerminations.Value<bool>("coverage_complete"),
                ["by_kind"] = new JObject { ["L"] = corners.Count(c => c.Kind == "L"), ["T"] = corners.Count(c => c.Kind == "T") },
                ["fixable"] = fixable.Count, ["closed_by_other"] = closing.Count, ["report_only"] = corners.Count(c => c.Action == "report_only"),
                ["corners"] = new JArray(corners.Select(Row)),
                ["note"] = "Plan geometry of the location lines; walls whose vertical extents do not overlap never form a corner. " +
                           "An end joined to any wall or with measured finish-cap contact on a linked core is not open. report_only ends are design gaps or cases Revit must explain - never moved."
            };
            if (op == JoinGeometryRules.Corners) return new ArchModelEdit { ReadResult = summary };
            if (fixable.Count == 0)
            {
                summary["no_op"] = true;
                summary["note2"] = "No open corner can be closed within the tolerance; nothing was written and no transaction was opened.";
                return new ArchModelEdit { ReadResult = summary };
            }
            // two ends of one wall moved in one call would make each other's fixed end stale
            var twice = fixable.GroupBy(c => c.WallId).FirstOrDefault(g => g.Count() > 1 && g.Any(c => c.ToX.HasValue));
            if (twice != null)
                throw new ArgumentException("wall " + twice.Key + " has both ends open and to move; close one end per call (send element_ids without its other partner) or move it with horizun_transform_elements set_curve. Nothing was written.");

            var edit = new ArchModelEdit();
            foreach (long id in fixable.SelectMany(c => c.Kind == "L" ? new[] { c.WallId, c.OtherId } : new[] { c.WallId }).Distinct().OrderBy(x => x))
                edit.Planned.Add(ModelEditRunner.Planned(byId[id], PlannedAction.Modify, r));
            foreach (var kv in summary) edit.Summary[kv.Key] = kv.Value?.DeepClone();
            var affectedWallIds = fixable.SelectMany(c => c.Kind == "L" ? new[] { c.WallId, c.OtherId } : new[] { c.WallId })
                .Distinct().Select(Rid.Make).ToList();
            FloorWallOverlapGuard.Snapshot floorOverlap = null;
            edit.Apply = d =>
            {
                // End joins can alter more than the two walls. Capture every host finish
                // floor before changing an end so induced sketch changes are covered too.
                var finishFloorIds = new FilteredElementCollector(d).OfClass(typeof(Floor)).Cast<Floor>()
                    .Where(f => FloorWallOverlapGuard.IsFinishFloor(d, f)).Select(f => f.Id).ToList();
                floorOverlap = FloorWallOverlapGuard.Capture(d, finishFloorIds);
                foreach (OpenCorner c in fixable)
                {
                    var w = (Wall)d.GetElement(Rid.Make(c.WallId));
                    try
                    {
                        if (c.NeedsAllowJoin) WallUtils.AllowWallJoinAtEnd(w, c.End);
                        if (c.ToX.HasValue)
                        {
                            var lc = (LocationCurve)w.Location;
                            Line line = (Line)lc.Curve;
                            XYZ moved = new XYZ(c.ToX.Value, c.ToY.Value, line.GetEndPoint(c.End).Z);
                            XYZ fixedEnd = line.GetEndPoint(1 - c.End);
                            lc.Curve = c.End == 0 ? Line.CreateBound(moved, fixedEnd) : Line.CreateBound(fixedEnd, moved);
                        }
                        // Revit can clean the moving end while leaving the other L leg open.
                        // Bring BOTH near ends to the same crossing, then let regeneration
                        // establish the reciprocal join. Never extend a partner beyond tolerance.
                        if (c.Kind == "L")
                        {
                            WallSegment source = walls.Single(s => s.Id == c.WallId), partner = walls.Single(s => s.Id == c.OtherId);
                            if (WallCornerRules.Cross(source, partner, out double x, out double y))
                            {
                                var other = (Wall)d.GetElement(Rid.Make(c.OtherId));
                                var otherLocation = (LocationCurve)other.Location;
                                var otherLine = (Line)otherLocation.Curve;
                                int otherEnd = WallCornerRules.NearestEnd(partner, x, y);
                                XYZ current = otherLine.GetEndPoint(otherEnd);
                                double distance = Math.Sqrt(Math.Pow(current.X - x, 2) + Math.Pow(current.Y - y, 2));
                                if (distance <= tolFeet)
                                {
                                    WallUtils.AllowWallJoinAtEnd(other, otherEnd);
                                    if (distance > WallCornerRules.SamePointFeet)
                                    {
                                        XYZ moved = new XYZ(x, y, current.Z), fixedEnd = otherLine.GetEndPoint(1 - otherEnd);
                                        otherLocation.Curve = otherEnd == 0 ? Line.CreateBound(moved, fixedEnd) : Line.CreateBound(fixedEnd, moved);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Revit refused to close the corner of wall " + c.WallId + " (" + (c.End == 0 ? "start" : "end") +
                                                            ") against wall " + c.OtherId + ": " + ex.Message);
                    }
                }
                d.Regenerate();
                FloorWallOverlapGuard.ThrowIfIncreased(d, floorOverlap, affectedWallIds);
            };
            edit.Verify = d =>
            {
                FloorWallOverlapGuard.ThrowIfIncreased(d, floorOverlap, affectedWallIds);
                var check = new PostconditionCheck("corner_joined", "end_position", "meeting_closed");
                int joined = 0, placed = 0, closed = 0, unread = 0;
                var rows = new JArray();
                var toCheck = fixable.Concat(closing).ToList();
                var currentWalls = new List<WallSegment>();
                foreach (long id in byId.Keys)
                {
                    var currentWall = d.GetElement(Rid.Make(id)) as Wall;
                    if (!(currentWall?.Location is LocationCurve currentLocation) || !(currentLocation.Curve is Line currentLine)) continue;
                    BoundingBoxXYZ box = currentWall.get_BoundingBox(null);
                    currentWalls.Add(new WallSegment { Id = id,
                        X0 = currentLine.GetEndPoint(0).X, Y0 = currentLine.GetEndPoint(0).Y,
                        X1 = currentLine.GetEndPoint(1).X, Y1 = currentLine.GetEndPoint(1).Y,
                        ZMin = box?.Min.Z ?? double.NaN, ZMax = box?.Max.Z ?? double.NaN,
                        JoinedAtStart = JoinedWalls(currentLocation, 0), JoinedAtEnd = JoinedWalls(currentLocation, 1),
                        JoinAllowedAtStart = WallUtils.IsWallJoinAllowedAtEnd(currentWall, 0), JoinAllowedAtEnd = WallUtils.IsWallJoinAllowedAtEnd(currentWall, 1) });
                }
                var currentById = byId.Keys.Select(id => d.GetElement(Rid.Make(id)) as Wall).Where(w => w != null).ToDictionary(w => Rid.Value(w.Id));
                JObject currentTerminations = ReadLinkedFinishTerminations(d, currentWalls, currentById, WallCornerRules.Find(currentWalls, tolFeet));
                edit.Evidence["linked_core_terminations"] = currentTerminations;
                List<OpenCorner> remaining = WallCornerRules.Find(currentWalls, tolFeet);
                foreach (OpenCorner c in toCheck)
                {
                    var w = d.GetElement(Rid.Make(c.WallId)) as Wall;
                    var lc = w?.Location as LocationCurve;
                    if (lc == null || !(lc.Curve is Line line)) { unread++; rows.Add(new JObject { ["wall_id"] = c.WallId, ["problem"] = "location line unreadable after the commit" }); continue; }
                    HashSet<long> at = JoinedWalls(lc, c.End);
                    bool ok = at.Contains(c.OtherId);
                    bool partnerOk = true;
                    if (c.Kind == "L")
                    {
                        WallSegment source = walls.Single(s => s.Id == c.WallId), partner = walls.Single(s => s.Id == c.OtherId);
                        partnerOk = false;
                        var other = d.GetElement(Rid.Make(c.OtherId)) as Wall;
                        if (other?.Location is LocationCurve otherLocation && WallCornerRules.Cross(source, partner, out double x, out double y))
                            partnerOk = JoinedWalls(otherLocation, WallCornerRules.NearestEnd(partner, x, y)).Contains(c.WallId);
                    }
                    WallSegment initial = walls.Single(s => s.Id == c.WallId);
                    double meetingX = c.ToX ?? (c.End == 0 ? initial.X0 : initial.X1);
                    double meetingY = c.ToY ?? (c.End == 0 ? initial.Y0 : initial.Y1);
                    // Check the complete meeting, including a third incident wall: moving
                    // one end must not transfer the same opening onto another wall.
                    var near = remaining.Where(open => {
                        WallSegment wall = currentWalls.Single(s => s.Id == open.WallId);
                        double x = open.End == 0 ? wall.X0 : wall.X1, y = open.End == 0 ? wall.Y0 : wall.Y1;
                        return WallCornerRules.EndInMeeting(initial, wall, x, y, meetingX, meetingY, tolFeet);
                    }).ToList();
                    bool meetingOk = WallCornerRules.MeetingClosed(ok, partnerOk, c.Kind == "L") && near.Count == 0;
                    if (meetingOk) closed++;
                    XYZ p = line.GetEndPoint(c.End);
                    double off = c.ToX.HasValue ? Math.Sqrt(Math.Pow(p.X - c.ToX.Value, 2) + Math.Pow(p.Y - c.ToY.Value, 2)) : 0;
                    // Revit may clean the joined end onto the other wall's face; 1 mm beyond the asked point is tolerated only when joined
                    bool posOk = !c.ToX.HasValue || off <= 1.0 / 304.8 || (ok && off <= WallCornerRules.SamePointFeet * 4);
                    if (ok) joined++;
                    if (posOk) placed++;
                    rows.Add(new JObject
                    {
                        ["wall_id"] = c.WallId, ["end"] = c.End == 0 ? "start" : "end", ["other_wall_id"] = c.OtherId,
                        ["joined_to_other"] = ok, ["joined_at_end"] = new JArray(at.OrderBy(x => x)), ["end_offset"] = Math.Round(off * k, 2),
                        ["partner_joined_back"] = partnerOk, ["state"] = meetingOk && posOk ? "resolved" : "unresolved",
                        ["remaining_open_ends_at_meeting"] = new JArray(near.Select(open => new JObject { ["wall_id"] = open.WallId, ["other_wall_id"] = open.OtherId,
                            ["end"] = open.End, ["gap_mm"] = Math.Round(open.GapFeet * 304.8, 3) })),
                        ["problem"] = meetingOk && posOk ? null : (!meetingOk ? "unresolved: the meeting is not joined at both participating ends after regeneration" : "the end is not where it was sent")
                    });
                }
                if (unread > 0) { check.Unreadable("corner_joined", toCheck.Count, unread + " wall(s) unreadable"); check.Unreadable("end_position", toCheck.Count, unread + " wall(s) unreadable"); check.Unreadable("meeting_closed", toCheck.Count, unread + " wall(s) unreadable"); }
                else { check.Compare("corner_joined", toCheck.Count, joined); check.Compare("end_position", toCheck.Count, placed); check.Compare("meeting_closed", toCheck.Count, closed); }
                edit.Evidence["corners"] = rows;
                return check;
            };
            return edit;
        }

        private static HashSet<long> JoinedWalls(LocationCurve lc, int end)
        {
            var set = new HashSet<long>();
            try
            {
                foreach (Element e in lc.get_ElementsAtJoin(end))
                    if (e is Wall) set.Add(Rid.Value(e.Id));
            }
            catch { }
            return set;
        }
    }
}
