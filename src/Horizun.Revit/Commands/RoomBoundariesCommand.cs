using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class RoomBoundariesCommand : ICommand
    {
        public string Name => "horizun_room_boundaries";
        public string Description => "Read the effective wall and separation-line loops of named Rooms, closure gaps and Revit's redundancy warning.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            CommandResult guard = DocumentGate.ReadGuard(doc, request, Name);
            if (guard != null) return guard;
            JArray ids = request["room_ids"] as JArray;
            if (ids == null || ids.Count < 1 || ids.Count > 100)
                return CommandResult.Fail("room_ids must contain 1..100 Room element ids.");
            var rooms = new List<Room>(); var seen = new HashSet<long>();
            foreach (JToken token in ids)
            {
                long raw;
                try { raw = token.Value<long>(); } catch { return CommandResult.Fail("room_ids contains a non-integer id."); }
                if (!Rid.CanRepresent(raw) || !seen.Add(raw) || !(doc.GetElement(Rid.Make(raw)) is Room room))
                    return CommandResult.Fail("room_ids contains a duplicate or a non-Room element id: " + raw);
                rooms.Add(room);
            }
            var redundant = new HashSet<long>(); bool warningsComplete = true; int warningsUnreadable = 0;
            var warningGuids = new HashSet<Guid>
            {
                BuiltInFailures.RoomFailures.RoomsInSameRegionRooms.Guid,
                BuiltInFailures.RoomFailures.RoomsInSameRegion.Guid
            };
            try
            {
                foreach (FailureMessage warning in doc.GetWarnings())
                {
                    try
                    {
                        if (!warningGuids.Contains(warning.GetFailureDefinitionId().Guid)) continue;
                        foreach (ElementId id in warning.GetFailingElements()) redundant.Add(Rid.Value(id));
                    }
                    catch { warningsComplete = false; warningsUnreadable++; }
                }
            }
            catch { warningsComplete = false; warningsUnreadable++; }
            var rows = new JArray(); int unreadable = 0;
            var options = new SpatialElementBoundaryOptions();
            foreach (Room room in rooms)
            {
                var row = new JObject
                {
                    ["room_id"] = Rid.Value(room.Id), ["number"] = room.Number, ["name"] = room.Name,
                    ["area_m2"] = room.Area * 0.09290304, ["has_location"] = room.Location != null,
                    ["redundant_by_revit_warning"] = redundant.Contains(Rid.Value(room.Id))
                        ? (JToken)true : warningsComplete ? (JToken)false : JValue.CreateNull(),
                    ["boundary_location"] = options.SpatialElementBoundaryLocation.ToString()
                };
                var edgeLoops = new List<List<BoundaryEdge>>(); var loopRows = new JArray(); bool readable = true;
                try
                {
                    IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(options);
                    foreach (IList<BoundarySegment> loop in loops ?? new List<IList<BoundarySegment>>())
                    {
                        var edges = new List<BoundaryEdge>(); var segments = new JArray();
                        foreach (BoundarySegment segment in loop)
                        {
                            Curve curve = segment.GetCurve();
                            if (curve == null || !curve.IsBound) throw new InvalidOperationException("A boundary segment has no bound curve.");
                            XYZ a = curve.GetEndPoint(0), m = curve.Evaluate(0.5, true), b = curve.GetEndPoint(1);
                            edges.Add(new BoundaryEdge(new[] { a.X, a.Y }, new[] { m.X, m.Y }, new[] { b.X, b.Y }));
                            Element source = doc.GetElement(segment.ElementId);
                            string kind = source is Wall ? "wall" : source is CurveElement &&
                                source.Category?.Id == Category.GetCategory(doc, BuiltInCategory.OST_RoomSeparationLines)?.Id
                                ? "room_separation_line" : source?.GetType().Name ?? "unresolved";
                            segments.Add(new JObject { ["element_id"] = Rid.Value(segment.ElementId),
                                ["source_kind"] = kind, ["start_feet"] = new JArray(a.X, a.Y, a.Z),
                                ["end_feet"] = new JArray(b.X, b.Y, b.Z), ["length_feet"] = curve.Length });
                        }
                        edgeLoops.Add(edges); loopRows.Add(segments);
                    }
                }
                catch (Exception ex)
                {
                    readable = false; unreadable++; row["boundary_error"] = ex.Message;
                }
                row["loops"] = loopRows;
                row["boundary_read_complete"] = readable;
                if (readable)
                {
                    RoomBoundaryAssessment assessment = RoomBoundaryRules.Assess(edgeLoops, 1 / 304.8);
                    row["loop_count"] = edgeLoops.Count;
                    row["closed"] = assessment.Closed;
                    row["closure_gaps"] = assessment.GapCount;
                    row["duplicate_effective_segment_pairs"] = assessment.DuplicatePairs;
                    row["state"] = redundant.Contains(Rid.Value(room.Id)) ? "redundant" :
                        edgeLoops.Count == 0 ? warningsComplete ? "unbounded" : "unbounded_or_redundant" :
                        !assessment.Closed ? "open_boundary" : room.Area <= 0 ? "zero_area" : "closed";
                }
                else row["state"] = "unreadable";
                rows.Add(row);
            }
            return CommandResult.Ok(new JObject
            {
                ["target_document"] = doc.Title, ["rooms_checked"] = rows.Count,
                ["warnings_complete"] = warningsComplete, ["warnings_unreadable"] = warningsUnreadable,
                ["boundaries_unreadable"] = unreadable, ["complete"] = warningsComplete && unreadable == 0,
                ["redundancy_basis"] = "Revit's RoomsInSameRegionRooms/RoomsInSameRegion warning ids; zero area alone does not prove redundancy",
                ["duplicate_scope"] = "coincident segments in Revit's effective Room boundary only; hidden overlapping elements outside that boundary are not assessed",
                ["rooms"] = rows
            });
        }
    }
}
