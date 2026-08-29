// -----------------------------------------------------------------------------
// Horizun Revit MCP - deterministic, read-only VIEW production planning.
//
// horizun_plan_views answers "produce the room deliverables for this level" the
// way horizun_plan_annotations answers "dimension the grids": by MEASURING the
// model, DECIDING with rules proved in Core/RoomViewRules.cs, and returning a
// complete horizun_manage_views request that the caller inspects, rehearses and
// applies. This command writes NOTHING - manage_views remains the single
// rehearsed, confirmed and re-read write path for views and sheets.
//
// The account it renders is the whole point: every room found, every room
// excluded WITH A CODE, every view it would create with its final name, and a
// coverage verdict that is never optimistic. A plan that quietly skipped two
// apartments looks exactly like a finished deliverable list, and that is the
// failure this file exists to prevent.
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
    public sealed class PlanViewsCommand : ICommand
    {
        public string Name => "horizun_plan_views";
        public string Description =>
            "Plan per-room view production (elevations, sections, cropped plans) deterministically and return a " +
            "ready horizun_manage_views dry-run request. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();
            if (operation != "room_views")
                return CommandResult.Fail("operation must be room_views - the one production family this " +
                                          "planner covers today.");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double toFeet;
            if (!DimensionPlanRules.UnitScale(units, out toFeet))
                return CommandResult.Fail("units must be mm, m or feet.");
            double fromFeet = 1.0 / toFeet;

            // ---- the plan view rooms and elevations are anchored in ----------------
            long planViewId = request.Value<long?>("plan_view_id") ?? -1;
            ViewPlan planView = Rid.CanRepresent(planViewId) ? doc.GetElement(Rid.Make(planViewId)) as ViewPlan : null;
            if (planView == null || planView.IsTemplate)
                return CommandResult.Fail("plan_view_id must identify a non-template plan view; elevation markers " +
                                          "and the cropped plans hang off it.");

            // ---- what to produce ----------------------------------------------------
            List<string> kinds;
            string error = RoomViewRules.ValidateKinds(
                request["kinds"] == null ? null : (request["kinds"] as JArray)?.Select(t => (string)t), out kinds);
            if (error != null) return CommandResult.Fail(error);

            int elevationCount = request.Value<int?>("elevation_count") ?? 4;
            error = RoomViewRules.ValidateElevationCount(elevationCount);
            if (error != null && kinds.Contains(RoomViewRules.KindElevations)) return CommandResult.Fail(error);

            string namePattern = request.Value<string>("name_pattern") ?? "{room_number} {room_name} - {kind} {index}";
            error = RoomViewRules.ValidatePattern(namePattern);
            if (error != null) return CommandResult.Fail(error);

            bool orientToWalls = request.Value<bool?>("orient_to_walls") ?? true;
            double margin = (request.Value<double?>("margin") ?? 500.0) * toFeet;
            if (margin < 0) return CommandResult.Fail("margin must be zero or greater.");
            double markerScaleRaw = request.Value<double?>("scale") ?? 50;
            int viewScale = (int)markerScaleRaw;
            if (viewScale < 1 || viewScale > 24000) return CommandResult.Fail("scale must be 1..24000.");

            long? templateId = request.Value<long?>("template_view_id");
            if (templateId != null)
            {
                var template = Rid.CanRepresent(templateId.Value)
                    ? doc.GetElement(Rid.Make(templateId.Value)) as View : null;
                if (template == null || !template.IsTemplate)
                    return CommandResult.Fail("template_view_id must identify a view TEMPLATE.");
            }

            // ---- the rooms ----------------------------------------------------------
            List<Room> rooms;
            string roomsError = ResolveRooms(doc, request, planView, out rooms);
            if (roomsError != null) return CommandResult.Fail(roomsError);

            var existingNames = ExistingViewNames(doc);
            var actions = new JArray();
            var planRows = new JArray();
            var excluded = new JArray();
            int planned = 0, excludedCount = 0;
            int keyOrdinal = 0;

            foreach (Room room in rooms.OrderBy(r => Rid.Value(r.Id)))
            {
                RoomFacts facts = Measure(doc, room);
                string code = RoomViewRules.Eligibility(facts);
                if (code == null && RoomViewRules.Center(facts) == null) code = RoomViewRules.CodeNoBoundingBox;
                if (code == null && orientToWalls && facts.LongestSegmentDx == null)
                    code = RoomViewRules.CodeNoBoundary;
                if (code != null)
                {
                    excludedCount++;
                    excluded.Add(new JObject
                    {
                        ["room_id"] = facts.Id, ["room"] = RoomViewRules.Describe(facts),
                        ["code"] = code, ["reason"] = RoomViewRules.EligibilityMessage(facts, code)
                    });
                    continue;
                }

                double[] center = RoomViewRules.Center(facts);
                double? rotation = orientToWalls ? RoomViewRules.PrincipalRotationDegrees(facts) : null;
                var roomActions = new JArray();
                var roomViews = new JArray();
                bool collision = false;

                // ---- names first, and every one checked before any action is emitted:
                // a room that collides is excluded WHOLE, because half a room's views
                // is not a deliverable anybody asked for.
                var plannedNames = new List<KeyValuePair<string, string>>(); // kind+index label -> name
                if (kinds.Contains(RoomViewRules.KindElevations))
                    for (int i = 0; i < elevationCount; i++)
                        plannedNames.Add(Pair(RoomViewRules.KindElevations, namePattern, facts, "ELEV", i + 1,
                                              ref collision, existingNames));
                if (kinds.Contains(RoomViewRules.KindSections))
                    for (int i = 0; i < 2; i++)
                        plannedNames.Add(Pair(RoomViewRules.KindSections, namePattern, facts, "SEC", i + 1,
                                              ref collision, existingNames));
                if (kinds.Contains(RoomViewRules.KindPlan))
                    plannedNames.Add(Pair(RoomViewRules.KindPlan, namePattern, facts, "PLAN", 1,
                                          ref collision, existingNames));
                if (collision || plannedNames.Any(p => p.Value == null))
                {
                    excludedCount++;
                    excluded.Add(new JObject
                    {
                        ["room_id"] = facts.Id, ["room"] = RoomViewRules.Describe(facts),
                        ["code"] = RoomViewRules.CodeNameCollision,
                        ["reason"] = "one or more of this room's planned view names already exists in the " +
                                     "document (or expands empty). Nothing was planned for the room: half a " +
                                     "room's views is not a deliverable. Change name_pattern, or rename the " +
                                     "colliding views. Planned names: " +
                                     string.Join("; ", plannedNames.Select(p => p.Value ?? "(empty)"))
                    });
                    continue;
                }

                int nameCursor = 0;
                // ---- elevations ------------------------------------------------------
                if (kinds.Contains(RoomViewRules.KindElevations))
                {
                    for (int i = 0; i < elevationCount; i++)
                    {
                        string viewName = plannedNames[nameCursor++].Value;
                        string key = "room-" + facts.Id + "-elev-" + (i + 1) + "-" + keyOrdinal++;
                        var action = new JObject
                        {
                            ["operation"] = "create_elevation", ["key"] = key,
                            ["plan_view_id"] = planViewId,
                            ["point"] = new JArray(center[0] * fromFeet, center[1] * fromFeet, center[2] * fromFeet),
                            ["elevation_index"] = i,
                            ["marker_scale"] = viewScale,
                            ["name"] = viewName
                        };
                        // ONE rotation per room's marker would suffice, but each action
                        // creates its own marker (manage_views has no marker alias), so
                        // each carries the same rotation and the elevations still face
                        // the walls. The marker count is reported so nobody is surprised.
                        if (rotation != null && Math.Abs(rotation.Value) > 1e-9) action["rotation"] = rotation.Value;
                        roomActions.Add(action);
                        AddTemplate(roomActions, templateId, key, ref keyOrdinal);
                        roomViews.Add(new JObject { ["kind"] = "elevation", ["index"] = i + 1, ["name"] = viewName });
                    }
                }

                // ---- sections: one along the principal axis, one across --------------
                if (kinds.Contains(RoomViewRules.KindSections))
                {
                    double angle = (rotation ?? 0.0) * Math.PI / 180.0;
                    for (int i = 0; i < 2; i++)
                    {
                        string viewName = plannedNames[nameCursor++].Value;
                        double dirAngle = angle + (i == 1 ? Math.PI / 2.0 : 0.0);
                        double dx = Math.Cos(dirAngle), dy = Math.Sin(dirAngle);
                        double half = RoomViewRules.HalfExtentAlong(facts, dx, dy, margin);
                        double depth = RoomViewRules.HalfExtentAlong(facts, -dy, dx, margin);
                        string key = "room-" + facts.Id + "-sec-" + (i + 1) + "-" + keyOrdinal++;
                        var action = new JObject
                        {
                            ["operation"] = "create_section", ["key"] = key,
                            ["start"] = new JArray((center[0] - dx * half) * fromFeet,
                                                   (center[1] - dy * half) * fromFeet, center[2] * fromFeet),
                            ["end"] = new JArray((center[0] + dx * half) * fromFeet,
                                                 (center[1] + dy * half) * fromFeet, center[2] * fromFeet),
                            ["bottom_offset"] = (facts.BoundingBoxMin[2] - center[2] - margin) * fromFeet,
                            ["top_offset"] = (facts.BoundingBoxMax[2] - center[2] + margin) * fromFeet,
                            ["depth"] = depth * fromFeet,
                            ["name"] = viewName
                        };
                        roomActions.Add(action);
                        AddTemplate(roomActions, templateId, key, ref keyOrdinal);
                        roomViews.Add(new JObject { ["kind"] = "section", ["index"] = i + 1, ["name"] = viewName });
                    }
                }

                // ---- the cropped plan ------------------------------------------------
                if (kinds.Contains(RoomViewRules.KindPlan))
                {
                    string viewName = plannedNames[nameCursor++].Value;
                    string key = "room-" + facts.Id + "-plan-" + keyOrdinal++;
                    roomActions.Add(new JObject
                    {
                        ["operation"] = "duplicate_view", ["key"] = key,
                        ["source_view_id"] = planViewId,
                        ["duplicate_option"] = "Duplicate",
                        ["name"] = viewName
                    });
                    // The crop rectangle is the room's box plus the margin, expressed in
                    // the PLAN VIEW's own right/up plane - which for an unrotated plan is
                    // model XY, and for a rotated one is whatever the view says it is.
                    double[] min = ProjectToView(planView, facts.BoundingBoxMin[0] - margin,
                                                 facts.BoundingBoxMin[1] - margin);
                    double[] max = ProjectToView(planView, facts.BoundingBoxMax[0] + margin,
                                                 facts.BoundingBoxMax[1] + margin);
                    roomActions.Add(new JObject
                    {
                        ["operation"] = "set_crop", ["view_key"] = key,
                        ["box"] = new JArray(Math.Min(min[0], max[0]) * fromFeet, Math.Min(min[1], max[1]) * fromFeet,
                                             Math.Max(min[0], max[0]) * fromFeet, Math.Max(min[1], max[1]) * fromFeet)
                    });
                    AddTemplate(roomActions, templateId, key, ref keyOrdinal);
                    roomViews.Add(new JObject { ["kind"] = "plan", ["index"] = 1, ["name"] = viewName });
                }

                foreach (JToken action in roomActions) actions.Add(action);
                planned++;
                planRows.Add(new JObject
                {
                    ["room_id"] = facts.Id,
                    ["room"] = RoomViewRules.Describe(facts),
                    ["level"] = facts.LevelName,
                    ["rotation_degrees"] = rotation == null ? (JToken)JValue.CreateNull() : new JValue(rotation.Value),
                    ["orientation"] = rotation == null ? "cardinal" : "principal_wall",
                    ["views"] = roomViews
                });
            }

            string coverage = RoomViewRules.Coverage(rooms.Count, planned, excludedCount);
            var manageViewsRequest = new JObject
            {
                ["target_document"] = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName,
                ["units"] = units,
                ["actions"] = actions,
                ["dry_run"] = true
            };
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "room_views",
                ["plan_view_id"] = planViewId,
                ["rooms_found"] = rooms.Count,
                ["rooms_planned"] = planned,
                ["rooms_excluded"] = excludedCount,
                ["excluded"] = excluded,
                ["coverage"] = coverage,
                ["kinds"] = new JArray(kinds),
                ["rooms"] = planRows,
                ["actions_planned"] = actions.Count,
                ["safe_to_execute"] = actions.Count > 0,
                ["next_tool"] = "horizun_manage_views",
                ["next_arguments"] = manageViewsRequest,
                ["note"] = "This planner made no model changes. Each elevation action creates its OWN marker at " +
                           "the room centre (" + (kinds.Contains(RoomViewRules.KindElevations) ? elevationCount : 0) +
                           " marker(s) per room). Run the returned horizun_manage_views dry run; only its " +
                           "rehearsal validates the batch and only its confirmation token writes."
            });
        }

        // ---------------------------------------------------------------------

        private static KeyValuePair<string, string> Pair(string kind, string pattern, RoomFacts facts,
                                                          string kindLabel, int index, ref bool collision,
                                                          HashSet<string> existingNames)
        {
            string error;
            string name = RoomViewRules.ExpandPattern(pattern, facts, kindLabel, index, out error);
            if (name == null) { collision = true; return new KeyValuePair<string, string>(kind, null); }
            if (!existingNames.Add(name)) collision = true; // also collides with a twin planned earlier
            return new KeyValuePair<string, string>(kind, name);
        }

        private static void AddTemplate(JArray actions, long? templateId, string viewKey, ref int keyOrdinal)
        {
            if (templateId == null) return;
            actions.Add(new JObject
            {
                ["operation"] = "apply_template",
                ["view_key"] = viewKey,
                ["template_view_id"] = templateId.Value
            });
        }

        private static string ResolveRooms(Document doc, JObject request, ViewPlan planView, out List<Room> rooms)
        {
            rooms = new List<Room>();
            JArray ids = request["room_ids"] as JArray;
            long? levelId = request.Value<long?>("level_id");
            if (ids != null && levelId != null)
                return "room_ids and level_id are two ways of naming the same thing - the rooms. Send exactly one.";
            if (ids != null)
            {
                if (ids.Count == 0 || ids.Count > 200 || ids.Any(t => t.Type != JTokenType.Integer))
                    return "room_ids must contain 1..200 integer ids.";
                foreach (JToken t in ids)
                {
                    long id = t.Value<long>();
                    var room = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as Room : null;
                    if (room == null) return "room_ids entry " + id + " is not a Room in the active document.";
                    rooms.Add(room);
                }
                return null;
            }

            ElementId wantedLevel = null;
            if (levelId != null)
            {
                var level = Rid.CanRepresent(levelId.Value) ? doc.GetElement(Rid.Make(levelId.Value)) as Level : null;
                if (level == null) return "level_id must identify a Level.";
                wantedLevel = level.Id;
            }
            else
            {
                // No explicit selection: the rooms of the PLAN VIEW's own level. A
                // whole-model sweep is not a default anybody asked for.
                try { wantedLevel = planView.GenLevel?.Id; } catch { wantedLevel = null; }
                if (wantedLevel == null)
                    return "the plan view has no generating level; pass room_ids or level_id explicitly.";
            }

            foreach (Room room in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                ElementId roomLevel = null;
                try { roomLevel = room.Level?.Id; } catch { roomLevel = null; }
                if (roomLevel != null && roomLevel == wantedLevel) rooms.Add(room);
            }
            if (rooms.Count > 200)
                return "the level carries " + rooms.Count + " rooms; the limit is 200 per call. Pass room_ids " +
                       "in pages - the plan is deterministic, so pages compose.";
            return null;
        }

        private static RoomFacts Measure(Document doc, Room room)
        {
            var facts = new RoomFacts { Id = Rid.Value(room.Id) };
            try { facts.Name = room.Name; } catch { }
            try { facts.Number = room.Number; } catch { }
            try { facts.LevelName = room.Level?.Name; } catch { }
            try { facts.HasLocation = room.Location is LocationPoint; } catch { facts.HasLocation = false; }
            try { facts.AreaSquareFeet = room.Area; } catch { facts.AreaSquareFeet = 0; }

            try
            {
                BoundingBoxXYZ box = room.get_BoundingBox(null);
                if (box != null)
                {
                    facts.BoundingBoxMin = new[] { box.Min.X, box.Min.Y, box.Min.Z };
                    facts.BoundingBoxMax = new[] { box.Max.X, box.Max.Y, box.Max.Z };
                }
            }
            catch { }

            try
            {
                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                double bestLength = 0; Curve best = null;
                if (loops != null)
                    foreach (IList<BoundarySegment> loop in loops)
                        foreach (BoundarySegment segment in loop)
                        {
                            Curve curve = segment.GetCurve();
                            if (curve == null || !curve.IsBound) continue;
                            double length = curve.Length;
                            // Strictly-greater keeps the FIRST of equals: ties resolve by
                            // boundary order, which Revit reports deterministically.
                            if (length > bestLength + 1e-9) { bestLength = length; best = curve; }
                        }
                if (best != null)
                {
                    XYZ d = best.GetEndPoint(1).Subtract(best.GetEndPoint(0));
                    if (d.GetLength() > 1e-9)
                    {
                        facts.LongestSegmentDx = d.X;
                        facts.LongestSegmentDy = d.Y;
                    }
                }
            }
            catch { }
            return facts;
        }

        private static HashSet<string> ExistingViewNames(Document doc)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                try { if (!view.IsTemplate) names.Add(view.Name); } catch { }
            return names;
        }

        /// <summary>Model XY into the view's right/up plane - identity for an unrotated plan.</summary>
        private static double[] ProjectToView(View view, double x, double y)
        {
            XYZ right = view.RightDirection, up = view.UpDirection, origin = view.Origin;
            var p = new XYZ(x, y, origin.Z);
            XYZ d = p.Subtract(origin);
            return new[] { d.DotProduct(right), d.DotProduct(up) };
        }
    }
}
