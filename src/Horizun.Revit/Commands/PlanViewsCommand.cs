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
            if(operation=="deliverable_set")
            {
                try
                {
                    var profile=request["delivery_profile"] as JObject;
                    JObject plan=DeliveryPlan.Build(profile,string.IsNullOrWhiteSpace(doc.PathName)?doc.Title:doc.PathName);
                    // THE PREFLIGHT: every predictable error of every stage, before the
                    // first write. The static half walks the profile; this half asks the
                    // document. A plan with a known-invalid later stage is refused whole,
                    // carrying every finding, instead of being handed out to run its
                    // first stages and die on the third.
                    int hostYear;
                    if(!int.TryParse(app.Application.VersionNumber,out hostYear)) hostYear=0;
                    JObject preflight=DeliveryPreflight.Static(profile,hostYear);
                    var hostErrors=new List<JObject>(); var hostUndetermined=new List<JObject>(); var hostChecked=new List<string>();
                    HostPreflight(doc,profile,hostErrors,hostUndetermined,hostChecked);
                    preflight=DeliveryPreflight.Merge(preflight,hostErrors,hostUndetermined,hostChecked);
                    plan["preflight"]=preflight;
                    plan["safe_to_execute"]=preflight.Value<bool>("ok");
                    if(!preflight.Value<bool>("ok"))
                        return CommandResult.FailWithDetail("Delivery profile preflight found "+preflight.Value<int>("error_count")+
                            " error(s) across its stages; the plan is withheld so no stage writes ahead of a known-invalid one. Nothing was written.",
                            preflight);
                    return CommandResult.Ok(plan);
                }
                catch(Exception ex) { return CommandResult.Fail("Invalid delivery profile: "+ex.Message); }
            }
            if (operation.StartsWith("delivery_", StringComparison.Ordinal))
                return Delivery(app, doc, operation, request);
            if (operation != "room_views")
                return CommandResult.Fail("operation must be room_views, deliverable_set, delivery_open, delivery_status, delivery_record, delivery_approve or delivery_invalidate.");

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
                        roomViews.Add(new JObject { ["kind"] = "elevation", ["index"] = i + 1, ["name"] = viewName, ["view_key"] = key });
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
                        roomViews.Add(new JObject { ["kind"] = "section", ["index"] = i + 1, ["name"] = viewName, ["view_key"] = key });
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
                    roomViews.Add(new JObject { ["kind"] = "plan", ["index"] = 1, ["name"] = viewName, ["view_key"] = key });
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

        // =====================================================================
        // The document half of the delivery preflight.
        // =====================================================================
        private static void HostPreflight(Document doc, JObject profile, List<JObject> errors, List<JObject> undetermined, List<string> checkedItems)
        {
            // ---- annotation views, their types and their elements ----------------
            foreach (JObject v in (profile["views"] as JArray ?? new JArray()).OfType<JObject>())
            {
                long viewId = v.Value<long?>("view_id") ?? -1;
                string stage = "view_" + viewId;
                View view = Rid.CanRepresent(viewId) ? doc.GetElement(Rid.Make(viewId)) as View : null;
                if (view == null || view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                {
                    errors.Add(DeliveryPreflight.Finding(stage, "view_id", "not_found",
                        "view " + viewId + " is not an existing, non-template graphical model view in the active document"));
                    continue;
                }
                bool printable; try { printable = view.CanBePrinted; } catch { printable = false; }
                if (!printable)
                    undetermined.Add(DeliveryPreflight.Finding(stage, "view_id", "not_printable",
                        "view " + viewId + " reports CanBePrinted=false; it can be annotated, but it will not appear on a printed sheet"));
                foreach (JObject spec in (v["dimension_sets"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string dstage = "dimensions_" + viewId, role = spec.Value<string>("role") ?? "?";
                    long typeId = spec.Value<long?>("dimension_type_id") ?? -1;
                    var dimType = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as DimensionType : null;
                    if (dimType == null)
                        errors.Add(DeliveryPreflight.Finding(dstage, "dimension_sets[" + role + "].dimension_type_id", "not_found",
                            "dimension_type_id " + typeId + " is not a DimensionType in the active document"));
                    else
                    {
                        DimensionStyleType style; bool readable = true;
                        try { style = dimType.StyleType; } catch { style = DimensionStyleType.Linear; readable = false; }
                        if (!readable)
                            undetermined.Add(DeliveryPreflight.Finding(dstage, "dimension_sets[" + role + "].dimension_type_id", "unreadable", "the dimension type's StyleType could not be read"));
                        else if (style != DimensionStyleType.Linear)
                            errors.Add(DeliveryPreflight.Finding(dstage, "dimension_sets[" + role + "].dimension_type_id", "wrong_style",
                                "dimension type '" + dimType.Name + "' is " + style + "; the delivery dimension sets create linear dimensions"));
                    }
                    IEnumerable<long> ids = spec["element_ids"] is JArray e ? e.Values<long>()
                        : (spec["reference_targets"] as JArray ?? new JArray()).OfType<JObject>().Select(t => t.Value<long?>("element_id") ?? -1);
                    foreach (long id in ids.Distinct())
                        if (!Rid.CanRepresent(id) || doc.GetElement(Rid.Make(id)) == null)
                            errors.Add(DeliveryPreflight.Finding(dstage, "dimension_sets[" + role + "].element_ids", "not_found",
                                "element " + id + " does not exist in the active document"));
                }
                if (v["tags"] is JObject tags)
                {
                    string tstage = "tags_" + viewId;
                    if (tags["tag_type_id"] != null)
                    {
                        long typeId = tags.Value<long>("tag_type_id");
                        var symbol = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) as FamilySymbol : null;
                        if (symbol == null || symbol.Category == null || symbol.Category.CategoryType != CategoryType.Annotation)
                            errors.Add(DeliveryPreflight.Finding(tstage, "tags.tag_type_id", "not_found",
                                "tag_type_id " + typeId + " is not an annotation family type in the active document"));
                        else
                            undetermined.Add(DeliveryPreflight.Finding(tstage, "tags.tag_type_id", "decided_by_rehearsal",
                                "'" + symbol.FamilyName + " : " + symbol.Name + "' exists; whether it can tag each target and read a label is proved by the annotate rehearsal"));
                    }
                    foreach (long id in (tags["element_ids"] as JArray ?? new JArray()).Values<long>().Distinct())
                        if (!Rid.CanRepresent(id) || doc.GetElement(Rid.Make(id)) == null)
                            errors.Add(DeliveryPreflight.Finding(tstage, "tags.element_ids", "not_found", "element " + id + " does not exist in the active document"));
                }
            }
            checkedItems.Add("host.views");
            checkedItems.Add("host.dimension_types");
            checkedItems.Add("host.tag_types");
            checkedItems.Add("host.elements");

            // ---- sheets: existence, placeholder, exactly one titleblock -----------
            var sheets = new Dictionary<long, ViewSheet>();
            foreach (JToken idToken in (profile["publication"]?["view_ids"] as JArray ?? new JArray()))
            {
                long id = idToken.Type == JTokenType.Integer ? (long)idToken : -1;
                var sheet = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as ViewSheet : null;
                if (sheet == null || sheet.IsPlaceholder)
                {
                    errors.Add(DeliveryPreflight.Finding("publish", "publication.view_ids", "not_found", "sheet " + id + " is not an existing non-placeholder sheet"));
                    continue;
                }
                sheets[id] = sheet;
                int titleblocks;
                try
                {
                    titleblocks = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsNotElementType().GetElementCount();
                }
                catch { titleblocks = -1; }
                if (titleblocks < 0)
                    undetermined.Add(DeliveryPreflight.Finding("audit", "publication.view_ids", "unreadable", "the titleblocks of sheet " + sheet.SheetNumber + " could not be counted"));
                else if (titleblocks != 1)
                    errors.Add(DeliveryPreflight.Finding("audit", "publication.view_ids", titleblocks == 0 ? "no_titleblock" : "multiple_titleblocks",
                        "sheet " + sheet.SheetNumber + " (" + id + ") carries " + titleblocks + " titleblock(s); a delivered sheet carries exactly one"));
            }
            checkedItems.Add("host.sheets");
            checkedItems.Add("host.titleblocks");

            // ---- packing items: placeable on at least one candidate --------------
            if (profile["packing"] is JObject packing)
            {
                JArray layouts = packing["sheets"] as JArray ?? new JArray(packing.DeepClone());
                var candidates = layouts.OfType<JObject>().Select(l => l.Value<long?>("sheet_id") ?? -1).Where(sheets.ContainsKey).Select(id => sheets[id]).ToList();
                foreach (JObject item in (packing["items"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string key = item.Value<string>("key") ?? "?";
                    if (item["view_id"] != null)
                    {
                        long id = item.Value<long?>("view_id") ?? -1;
                        var view = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as View : null;
                        if (view == null || view.IsTemplate || view is ViewSheet)
                        { errors.Add(DeliveryPreflight.Finding("pack", "packing.items[" + key + "].view_id", "not_found", "view " + id + " is not a placeable existing view")); continue; }
                        bool anywhere = false, undecided = false;
                        foreach (ViewSheet c in candidates)
                        {
                            try { if (Viewport.CanAddViewToSheet(doc, c.Id, view.Id)) { anywhere = true; break; } }
                            catch { undecided = true; }
                        }
                        if (!anywhere && !undecided && candidates.Count > 0)
                            errors.Add(DeliveryPreflight.Finding("pack", "packing.items[" + key + "].view_id", "not_placeable",
                                "view " + id + " (" + view.Name + ") cannot be added to any candidate sheet (Viewport.CanAddViewToSheet is false on all of them): it is already placed on a sheet or is not a placeable view kind"));
                        else if (undecided && !anywhere)
                            undetermined.Add(DeliveryPreflight.Finding("pack", "packing.items[" + key + "].view_id", "unreadable", "Viewport.CanAddViewToSheet threw for view " + id));
                    }
                    else if (item["schedule_id"] != null)
                    {
                        long id = item.Value<long?>("schedule_id") ?? -1;
                        var schedule = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as ViewSchedule : null;
                        if (schedule == null || schedule.IsTemplate)
                        { errors.Add(DeliveryPreflight.Finding("pack", "packing.items[" + key + "].schedule_id", "not_found", "schedule " + id + " is not an existing schedule")); continue; }
                        bool placed;
                        try
                        {
                            placed = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                                .Any(si => si.ScheduleId == schedule.Id);
                        }
                        catch { placed = false; undetermined.Add(DeliveryPreflight.Finding("pack", "packing.items[" + key + "].schedule_id", "unreadable", "existing placements of schedule " + id + " could not be read")); }
                        if (placed)
                            errors.Add(DeliveryPreflight.Finding("pack", "packing.items[" + key + "].schedule_id", "not_placeable",
                                "schedule " + id + " (" + schedule.Name + ") is already placed on a sheet; one schedule cannot live on two sheets"));
                    }
                }
                checkedItems.Add("host.packing_items");
            }

            // ---- output: directory, writability, conflicts -------------------------
            if (profile["publication"] is JObject publication)
            {
                string output = publication.Value<string>("output_path");
                bool overwrite = publication.Value<bool?>("overwrite") == true;
                bool combine = publication.Value<bool?>("pdf_combine") ?? true;
                bool manifest = publication.Value<bool?>("emit_manifest") ?? true;
                if (!string.IsNullOrWhiteSpace(output) && System.IO.Path.IsPathRooted(output))
                {
                    string folder = null;
                    try { folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output)); } catch { folder = null; }
                    if (folder == null || !System.IO.Directory.Exists(folder))
                        errors.Add(DeliveryPreflight.Finding("publish", "publication.output_path", "directory_missing",
                            "the output directory does not exist: " + (folder ?? "(unresolvable)") + "; it is not created implicitly"));
                    else
                    {
                        string probe = System.IO.Path.Combine(folder, ".horizun-preflight-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try { System.IO.File.WriteAllBytes(probe, new byte[0]); System.IO.File.Delete(probe); }
                        catch (Exception ex)
                        {
                            errors.Add(DeliveryPreflight.Finding("publish", "publication.output_path", "not_writable",
                                "the output directory refused a write probe: " + ex.GetType().Name + ": " + ex.Message));
                        }
                        try
                        {
                            var ids = (publication["view_ids"] as JArray ?? new JArray()).Where(t => t.Type == JTokenType.Integer).Select(t => (long)t).ToList();
                            var planned = ids.Count > 0 && ids.Distinct().Count() == ids.Count
                                ? DeliveryPdf.Paths(System.IO.Path.GetFullPath(output), combine, ids).ToList() : new List<string>();
                            if (manifest) planned.Add(System.IO.Path.GetFullPath(output) + ".manifest.json");
                            var existing = planned.Where(System.IO.File.Exists).ToList();
                            if (existing.Count > 0 && !overwrite)
                                errors.Add(DeliveryPreflight.Finding("publish", "publication.overwrite", "conflict",
                                    "these planned outputs already exist and overwrite=false: " + string.Join(", ", existing)));
                        }
                        catch (Exception ex)
                        {
                            undetermined.Add(DeliveryPreflight.Finding("publish", "publication.output_path", "unreadable", "planned output names could not be resolved: " + ex.Message));
                        }
                    }
                }
                checkedItems.Add("host.output_directory");
                checkedItems.Add("host.output_conflicts");
            }
        }

        // =====================================================================
        // The delivery ledger operations. They write the LEDGER (a file under
        // %USERPROFILE%\.horizun\deliveries), never the model; the model is only
        // read to compare recorded scopes with what stands now.
        // =====================================================================
        private static CommandResult Delivery(UIApplication app, Document doc, string operation, JObject request)
        {
            DateTime utc = DateTime.UtcNow;
            string refusal;
            switch (operation)
            {
                case "delivery_open":
                {
                    var profile = request["delivery_profile"] as JObject;
                    JObject plan;
                    try { plan = DeliveryPlan.Build(profile, string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName); }
                    catch (Exception ex) { return CommandResult.Fail("Invalid delivery profile: " + ex.Message); }
                    JObject document = DeliveryLedgerHost.DocumentIdentity(app, doc);
                    string deliveryId = request.Value<string>("delivery_id") ??
                        DeliveryLedger.DeriveId(plan.Value<string>("profile_sha256"), document.Value<string>("fingerprint"));
                    // Identity BEFORE the preflight. An operator re-running a profile after
                    // an interruption gets "already exists, resume it" - the ledger already
                    // holds what changed - rather than a preflight finding about a model the
                    // ledger has been tracking since the first open (measured c8b: the same
                    // profile, re-opened after a recorded deletion, was refused by the
                    // preflight and the resume path stayed hidden).
                    if (System.IO.File.Exists(DeliveryLedger.FileFor(DeliveryLedgerHost.Directory(), deliveryId)))
                        return CommandResult.Fail("delivery '" + deliveryId + "' already exists on this machine; use delivery_status to resume it, " +
                                                  "or open a new one under an explicit different delivery_id. Nothing was written.");
                    int hostYear; if (!int.TryParse(app.Application.VersionNumber, out hostYear)) hostYear = 0;
                    JObject preflight = DeliveryPreflight.Static(profile, hostYear);
                    var hostErrors = new List<JObject>(); var hostUndetermined = new List<JObject>(); var hostChecked = new List<string>();
                    HostPreflight(doc, profile, hostErrors, hostUndetermined, hostChecked);
                    preflight = DeliveryPreflight.Merge(preflight, hostErrors, hostUndetermined, hostChecked);
                    if (!preflight.Value<bool>("ok"))
                        return CommandResult.FailWithDetail("Delivery profile preflight found " + preflight.Value<int>("error_count") +
                            " error(s); no delivery is opened on a plan with a known-invalid stage.", preflight);
                    JObject record;
                    try { record = DeliveryLedger.Open(plan, document, DeliveryLedgerHost.AddinIdentity(), deliveryId, utc); }
                    catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }
                    try { DeliveryLedger.AppendEvent(FileJobSink.Instance, DeliveryLedgerHost.Directory(), deliveryId, DeliveryLedger.OpenedEvent(record)); }
                    catch (Exception ex) { return CommandResult.Fail("The delivery ledger could not be written: " + ex.Message); }
                    JObject opened = DeliveryLedgerHost.Summary(record, null);
                    opened["plan"] = plan; opened["preflight"] = preflight;
                    return CommandResult.Ok(opened);
                }
                case "delivery_status":
                {
                    JObject record = DeliveryLedgerHost.Load(request.Value<string>("delivery_id"), out refusal);
                    if (record == null) return CommandResult.Fail(refusal);
                    string mismatch = DeliveryLedgerHost.DocumentMismatch(app, doc, record);
                    JArray reverification = mismatch == null ? DeliveryLedgerHost.Reverify(doc, record, record.Value<string>("delivery_id"), utc) : new JArray();
                    JObject summary = DeliveryLedgerHost.Summary(record, reverification);
                    if (mismatch != null) summary["document_mismatch"] = mismatch;
                    return CommandResult.Ok(summary);
                }
                case "delivery_record":
                {
                    JObject record = DeliveryLedgerHost.Load(request.Value<string>("delivery_id"), out refusal);
                    if (record == null) return CommandResult.Fail(refusal);
                    string mismatch = DeliveryLedgerHost.DocumentMismatch(app, doc, record);
                    if (mismatch != null) return CommandResult.Fail(mismatch);
                    string key = request.Value<string>("stage_key"), status = request.Value<string>("status");
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(status)) return CommandResult.Fail("stage_key and status are required.");
                    if (status == DeliveryLedger.Approved || status == DeliveryLedger.Rejected)
                        return CommandResult.Fail("approvals go through delivery_approve, which binds the sheet's current scope to the decision.");
                    if (status == DeliveryLedger.Invalidated)
                        return CommandResult.Fail("invalidations go through delivery_invalidate, which names the reason and cascades.");
                    var facts = (request["facts"] as JObject)?.DeepClone() as JObject ?? new JObject();
                    JObject stage = DeliveryLedger.Stage(record, key);
                    if (stage == null) return CommandResult.Fail("stage '" + key + "' is not in delivery '" + record.Value<string>("delivery_id") + "'.");
                    string kind = stage.Value<string>("kind");
                    // Completed writes carry the elements they created; the host reads
                    // their scope NOW so the ledger can tell later whether they moved.
                    if (status == DeliveryLedger.Completed && kind == DeliveryLedger.KindWrite)
                    {
                        var ids = (facts["element_ids"] as JArray ?? new JArray()).Where(t => t.Type == JTokenType.Integer).Select(t => (long)t).ToList();
                        if (ids.Count == 0) return CommandResult.Fail("a completed write stage records the element_ids it created or moved; none were given. Nothing was recorded.");
                        List<string> missing;
                        JArray scope = DeliveryLedgerHost.ReadScope(doc, ids, out missing);
                        if (missing.Count > 0)
                            return CommandResult.Fail("cannot record '" + key + "' as completed: these elements do not exist in the active document: " +
                                                      string.Join(", ", missing) + ". A write whose elements cannot be re-read is not verified.");
                        facts["scope"] = scope; facts["scope_read_utc"] = utc.ToString("o");
                    }
                    if (status == DeliveryLedger.Completed && facts["files"] is JArray fileList)
                    {
                        List<string> missing;
                        JArray hashed = DeliveryLedgerHost.HashFiles(fileList.Values<string>(), out missing);
                        if (missing.Count > 0) return CommandResult.Fail("cannot record '" + key + "' as completed: these files do not exist: " + string.Join(", ", missing));
                        facts["files"] = hashed;
                    }
                    if (!DeliveryLedger.TryTransition(record, key, status, facts, utc, out refusal)) return CommandResult.Fail(refusal + " Nothing was recorded.");
                    try { DeliveryLedger.AppendEvent(FileJobSink.Instance, DeliveryLedgerHost.Directory(), record.Value<string>("delivery_id"), DeliveryLedger.TransitionEvent(key, status, facts, utc)); }
                    catch (Exception ex) { return CommandResult.Fail("The transition was refused because the ledger could not be written: " + ex.Message); }
                    return CommandResult.Ok(DeliveryLedgerHost.Summary(record, null));
                }
                case "delivery_approve":
                {
                    JObject record = DeliveryLedgerHost.Load(request.Value<string>("delivery_id"), out refusal);
                    if (record == null) return CommandResult.Fail(refusal);
                    string mismatch = DeliveryLedgerHost.DocumentMismatch(app, doc, record);
                    if (mismatch != null) return CommandResult.Fail(mismatch);
                    string key = request.Value<string>("stage_key"), identity = request.Value<string>("identity"), decision = request.Value<string>("decision");
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(identity)) return CommandResult.Fail("stage_key and identity are required; an anonymous approval is not one.");
                    if (decision != DeliveryLedger.Approved && decision != DeliveryLedger.Rejected) return CommandResult.Fail("decision must be approved or rejected.");
                    JObject stage = DeliveryLedger.Stage(record, key);
                    if (stage == null || stage.Value<string>("kind") != DeliveryLedger.KindApprovalCapture)
                        return CommandResult.Fail("'" + key + "' is not a sheet approval stage of this delivery.");
                    // The approval is bound to the sheet AND everything placed on it as
                    // they stand now: a later change to any of them invalidates it.
                    long sheetId; var scopeIds = new List<long>();
                    if (long.TryParse(key.Substring("capture_sheet_".Length), out sheetId))
                    {
                        scopeIds.Add(sheetId);
                        var sheet = Rid.CanRepresent(sheetId) ? doc.GetElement(Rid.Make(sheetId)) as ViewSheet : null;
                        if (sheet != null)
                        {
                            try { scopeIds.AddRange(sheet.GetAllViewports().Select(Rid.Value)); } catch { }
                            try { scopeIds.AddRange(sheet.GetAllPlacedViews().Select(Rid.Value)); } catch { }
                        }
                    }
                    List<string> missing;
                    JArray scope = DeliveryLedgerHost.ReadScope(doc, scopeIds, out missing);
                    if (missing.Count > 0) return CommandResult.Fail("the approved sheet or its placements no longer exist: " + string.Join(", ", missing));
                    var facts = new JObject { ["identity"] = identity, ["decision"] = decision, ["note"] = request.Value<string>("note"),
                        ["scope"] = scope, ["scope_read_utc"] = utc.ToString("o"), ["document_fingerprint"] = DeliveryLedgerHost.DocumentIdentity(app, doc)["fingerprint"] };
                    if (!DeliveryLedger.TryTransition(record, key, decision, facts, utc, out refusal)) return CommandResult.Fail(refusal + " Nothing was recorded.");
                    try { DeliveryLedger.AppendEvent(FileJobSink.Instance, DeliveryLedgerHost.Directory(), record.Value<string>("delivery_id"), DeliveryLedger.TransitionEvent(key, decision, facts, utc)); }
                    catch (Exception ex) { return CommandResult.Fail("The approval was refused because the ledger could not be written: " + ex.Message); }
                    return CommandResult.Ok(DeliveryLedgerHost.Summary(record, null));
                }
                case "delivery_invalidate":
                {
                    JObject record = DeliveryLedgerHost.Load(request.Value<string>("delivery_id"), out refusal);
                    if (record == null) return CommandResult.Fail(refusal);
                    string key = request.Value<string>("stage_key"), reason = request.Value<string>("reason");
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(reason)) return CommandResult.Fail("stage_key and reason are required.");
                    if (DeliveryLedger.Stage(record, key) == null) return CommandResult.Fail("stage '" + key + "' is not in this delivery.");
                    List<string> hit = DeliveryLedger.Invalidate(record, key, reason, utc);
                    try { DeliveryLedger.AppendEvent(FileJobSink.Instance, DeliveryLedgerHost.Directory(), record.Value<string>("delivery_id"), DeliveryLedger.InvalidationEvent(key, reason, utc)); }
                    catch (Exception ex) { return CommandResult.Fail("The invalidation could not be written: " + ex.Message); }
                    JObject summary = DeliveryLedgerHost.Summary(record, null);
                    summary["invalidated"] = new JArray(hit);
                    return CommandResult.Ok(summary);
                }
                default:
                    return CommandResult.Fail("operation must be room_views, deliverable_set, delivery_open, delivery_status, delivery_record, delivery_approve or delivery_invalidate.");
            }
        }
    }
}
