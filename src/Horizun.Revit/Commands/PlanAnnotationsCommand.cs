// -----------------------------------------------------------------------------
// Horizun Revit MCP - deterministic, read-only annotation planning.
//
// This command makes the choices a production modeler should not have to encode
// as raw XYZ values: tag-head positions and a linear dimension line derived from
// semantic references.  It NEVER writes.  Its result is a complete
// horizun_annotate request; that command remains the single rehearsed, confirmed
// and host-verified write path.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class PlanAnnotationsCommand : ICommand
    {
        public string Name => "horizun_plan_annotations";
        public string Description =>
            "Plan collision-aware tags or a linear dimension by intent and return a ready horizun_annotate dry-run request. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double toFeet;
            if (!DimensionPlanRules.UnitScale(units, out toFeet)) return CommandResult.Fail("units must be mm, m or feet.");
            string distanceSpace = request.Value<string>("distance_space") ?? "model";
            try
            {
                View distanceView = doc.GetElement(Rid.Make(request.Value<long?>("view_id") ?? -1)) as View;
                if (distanceView == null) return CommandResult.Fail("view_id is required.");
                // Only layout distances scale; element/probe coordinates retain units.
                foreach (string key in new[] { "offset", "chain_separation", "clearance", "max_displacement" })
                {
                    double defaultValue = DeliveryLayoutRules.DefaultInUnits(key, distanceSpace, toFeet);
                    if (key == "chain_separation" && request[key] == null) continue;
                    request[key] = DeliveryLayoutRules.Distance(request.Value<double?>(key) ?? defaultValue,
                        toFeet, distanceView.Scale, distanceSpace) / toFeet;
                }
                foreach (string key in new[] { "interior_offset", "exterior_offset", "thickness_offset",
                                            "annotation_clearance", "layout_margin" })
                    if (request[key] != null)
                        request[key] = DeliveryLayoutRules.Distance(request.Value<double>(key),
                            toFeet, distanceView.Scale, distanceSpace) / toFeet;
                // annotation_clearance defaults to 10 UNITS; in paper space that default must
                // scale with the view like every other layout distance (live: 10 mm of model
                // at 1:96 let two chains land 0.6 mm apart on paper).
                if (distanceSpace == "paper" && request["annotation_clearance"] == null)
                    request["annotation_clearance"] = DeliveryLayoutRules.Distance(3.5 / 304.8 / toFeet, toFeet, distanceView.Scale, "paper") / toFeet;
                request["distance_space"] = "model";
            }
            catch (Exception ex) { return CommandResult.Fail("Invalid layout distances: " + ex.Message); }
            string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();
            if(operation=="dimension_set") return PlanSet(app,doc,request,units,distanceSpace);
            if (operation == DimensionModeRules.Operation) return PlanDimensionMode(app, doc, request, units, toFeet);
            if (operation == "auto_tags") return PlanTags(doc, request, units, toFeet);
            if (operation == "model_notes") return PlanModelNotes(doc, request, units, toFeet);
            if (operation == "intent_dimension") return PlanDimension(app, doc, request, units, toFeet);
            if (operation == AutoDimensionRules.OpHeights)
                return PlanAutoHeights(app, doc, request, units, toFeet);
            if (operation == "auto_dimension_room")
                return PlanRoomDimensions(app, doc, request, units, toFeet);
            if (AutoDimensionRules.KnownOperations.Contains(operation))
                return PlanAutoDimension(doc, request, operation, units, toFeet);
            return CommandResult.Fail(AutoDimensionRules.OperationError(operation));
        }

        // =====================================================================
        private CommandResult PlanSet(UIApplication app,Document doc,JObject request,string units,string distanceSpace)
        {
            var sets=request["sets"] as JArray;
            if(sets==null || sets.Count<1 || sets.Count>30 || sets.Any(s=>!(s is JObject)))
                return CommandResult.Fail("dimension_set requires 1..30 named sets with explicit reference intent.");
            var actions=new JArray();var rows=new JArray();var roles=new HashSet<string>(StringComparer.Ordinal);
            var identities=new HashSet<string>(StringComparer.Ordinal);
            foreach(JObject spec in sets)
            {
                string role=spec.Value<string>("role");
                if(string.IsNullOrWhiteSpace(role) || !roles.Add(role)) return CommandResult.Fail("Each dimension set role must be nonempty and unique.");
                string op=spec.Value<string>("operation");
                if(op!="intent_dimension" && !AutoDimensionRules.KnownOperations.Contains(op))
                    return CommandResult.Fail("A dimension set must select intent_dimension or a supported auto_dimension operation.");
                if(spec["offset"]==null || spec["dimension_type_id"]==null || spec["side"]==null)
                    return CommandResult.Fail("Each set requires explicit offset, dimension_type_id and side; general/partial/thickness/opening criteria are never guessed.");
                var args=(JObject)spec.DeepClone();args.Remove("role");
                args["view_id"]=request["view_id"].DeepClone();args["units"]=units;
                if(args["distance_space"]==null) args["distance_space"]=distanceSpace;
                CommandResult plan=Execute(app,args.ToString());
                var data=plan.Data as JObject;
                if(!plan.Success || data?.Value<bool?>("safe_to_execute")!=true ||
                    data.Value<bool?>("coverage_complete")==false ||
                    (data["coverage"]!=null && data.Value<string>("coverage")!="complete"))
                    return CommandResult.FailWithDetail("Dimension set '"+role+"' did not produce complete coverage. No annotation was created.",
                        new JObject { ["role"]=role,["error"]=plan.Error,["planner"]=data });
                foreach(JObject a in (JArray)data["next_arguments"]["actions"])
                {
                    string identity=string.Join("|",((JArray)a["references"]).Values<string>().OrderBy(x=>x,StringComparer.Ordinal));
                    if(!identities.Add(identity)) return CommandResult.Fail("Two dimension sets duplicate the same references; resolve the redundant role.");
                    actions.Add(a.DeepClone());
                }
                rows.Add(new JObject { ["role"]=role,["plan"]=data });
            }
            if(actions.Count>500) return CommandResult.Fail("Dimension set exceeds 500 actions.");
            return CommandResult.Ok(new JObject { ["operation"]="dimension_set",["view_id"]=request["view_id"].DeepClone(),
                ["coverage_complete"]=true,["safe_to_execute"]=true,["sets"]=rows,["next_tool"]="horizun_annotate",
                ["next_arguments"]=AnnotateRequest(doc,units,actions),
                ["acceptance"]="Native rehearsal verifies references and values. Capture the view to approve glyph spacing and leader crossings; no visual approval is inferred." });
        }

        // =====================================================================
        // auto_dimension_* : whole-family chains, planned deterministically
        // =====================================================================

        private static CommandResult PlanAutoHeights(UIApplication app, Document doc, JObject request,
            string units, double toFeet)
        {
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);
            long? levelId = request.Value<long?>("level_id");
            if (!levelId.HasValue || !Rid.CanRepresent(levelId.Value) ||
                !(doc.GetElement(Rid.Make(levelId.Value)) is Level))
                return CommandResult.Fail("auto_dimension_heights requires level_id of a level in the active model. " +
                    "An auxiliary level can first be created with horizun_create_elements kind=level.");
            string selector = request.Value<string>("height_selector") ?? "top_face";
            if (!AutoDimensionRules.IsHeightSelector(selector))
                return CommandResult.Fail("height_selector must be top_face, bottom_face, opening_head or opening_sill.");
            List<Element> targets = ResolveTargets(doc, request["element_ids"] as JArray, 32, out error);
            if (targets == null || targets.Count == 0)
                return CommandResult.Fail(error ?? "auto_dimension_heights requires element_ids of walls, ceilings, roofs or openings.");
            var actions = new JArray();
            var rows = new JArray();
            var omitted = new JArray();
            Level datum = (Level)doc.GetElement(Rid.Make(levelId.Value));
            HashSet<string> existingHeights = request.Value<bool?>("skip_existing") == false
                ? new HashSet<string>(StringComparer.Ordinal)
                : AutoDimensionPlanner.ExistingChainIdentities(doc, view);
            foreach (Element target in targets.OrderBy(e => Rid.Value(e.Id)))
            {
                if (Rid.Value(target.Id) == levelId.Value)
                    return CommandResult.Fail("element_ids cannot include the datum level itself.");
                // Head and sill of a HOSTED opening: the faces of the void it cuts in its
                // wall, which horizun_annotate can fingerprint (a family's Top/Bottom
                // reference planes cannot be - measured live, Revit 2026, 2026-10-17).
                if ((selector == DimensionReferenceRules.SelectorOpeningHead || selector == DimensionReferenceRules.SelectorOpeningSill) &&
                    target is FamilyInstance hostedOpening && hostedOpening.Host is Wall)
                {
                    OpeningVoidResult voids = PlanimetryGeometryReader.OpeningVoid(doc, hostedOpening, out string voidError);
                    WallVoidFace cap = OpeningVoidRules.ForSelector(voids?.Choice, selector);
                    if (cap != null && voids.Faces.TryGetValue(cap.Stable, out PlanarFace capFace))
                    {
                        XYZ vo = view.Origin, vr = view.RightDirection.Normalize(), vu = view.UpDirection.Normalize();
                        double mid = (voids.Choice.Left.Along + voids.Choice.Right.Along) * 0.5;
                        XYZ p = voids.Origin.Add(voids.Tangent.Multiply(mid));
                        p = new XYZ(p.X, p.Y, cap.Z);
                        string datumStable;
                        try { datumStable = new Reference(datum).ConvertToStableRepresentation(doc); }
                        catch (Exception ex) { return CommandResult.Fail("The datum level has no stable reference: " + ex.Message); }
                        string capStable = capFace.Reference.ConvertToStableRepresentation(doc);
                        var at = new AutoDimensionCandidate { SubjectId = Rid.Value(target.Id), HostId = Rid.Value(hostedOpening.Host.Id),
                            Source = selector, Label = target.Name + " (wall void)", StableRepresentation = capStable,
                            X = p.Subtract(vo).DotProduct(vr), Y = p.Subtract(vo).DotProduct(vu) };
                        var lv = new AutoDimensionCandidate { SubjectId = levelId.Value, Source = "level", Label = datum.Name,
                            StableRepresentation = datumStable, X = at.X,
                            Y = new XYZ(p.X, p.Y, datum.ProjectElevation).Subtract(vo).DotProduct(vu) };
                        int before = actions.Count;
                        var chainRows = new JArray(); var refusedRows = new JArray();
                        AddOpeningChain(new List<AutoDimensionCandidate> { lv, at }, selector, "vertical", "positive",
                            (request.Value<double?>("offset") ?? 15) * toFeet, doc, view, request, 1.0 / toFeet,
                            existingHeights, actions, chainRows, refusedRows);
                        if (refusedRows.Count > 0)
                        {
                            omitted.Add(new JObject { ["element_id"] = Rid.Value(target.Id), ["code"] = refusedRows[0]["code"], ["reason"] = refusedRows[0]["reason"] });
                            continue;
                        }
                        if (actions.Count > before)
                        {
                            actions.Last["expected_value"] = Math.Round((cap.Z - datum.ProjectElevation) / toFeet, 6);
                            actions.Last["expected_tolerance"] = 1.0 / 304.8 / toFeet;
                        }
                        rows.Add(new JObject { ["element_id"] = Rid.Value(target.Id), ["axis"] = "vertical", ["reference_source"] = "wall_void",
                            ["estimated_value"] = (cap.Z - datum.ProjectElevation) / toFeet, ["already_dimensioned"] = actions.Count == before,
                            ["references"] = chainRows.Count > 0 ? chainRows[0]["references"].DeepClone() : new JArray() });
                        continue;
                    }
                    if (voids != null && cap == null && selector == DimensionReferenceRules.SelectorOpeningSill)
                    {
                        omitted.Add(new JObject { ["element_id"] = Rid.Value(target.Id), ["code"] = "sill_is_base",
                            ["reason"] = "The void reaches the wall base (a door): its sill is the base level itself; there is no height to dimension." });
                        continue;
                    }
                    // otherwise fall through to the family-reference path below
                }
                // The top of a WALL is decided by the Core rules against this view: a
                // horizontal top keeps the face path below; a sloped top gets the end
                // points of an edge-on top, or a horizontal end edge beyond the cut -
                // model references that follow the wall, never an auxiliary level.
                if (selector == DimensionReferenceRules.SelectorTopFace && target is Wall topWall)
                {
                    WallTopResult top = PlanimetryGeometryReader.WallTop(doc, view, topWall);
                    TopReferenceChoice topChoice = WallTopReferenceRules.Choose(top.Faces, out string topCode, out string topWhy);
                    if (topChoice == null)
                    {
                        omitted.Add(new JObject { ["element_id"] = Rid.Value(target.Id), ["code"] = topCode, ["reason"] = topWhy });
                        continue;
                    }
                    if (topChoice.Mode != WallTopReferenceRules.ModeHorizontalFace)
                    {
                        string datumStable;
                        try { datumStable = new Reference(datum).ConvertToStableRepresentation(doc); }
                        catch (Exception ex) { return CommandResult.Fail("The datum level has no stable reference: " + ex.Message); }
                        XYZ vo = view.Origin, vr = view.RightDirection.Normalize(), vu = view.UpDirection.Normalize();
                        double offsetFeet = (request.Value<double?>("offset") ?? 15) * toFeet;
                        foreach (KeyValuePair<string, ViewPointFact> end in topChoice.References)
                        {
                            if (!top.Points.TryGetValue(end.Value.Stable, out XYZ p))
                            {
                                omitted.Add(new JObject { ["element_id"] = Rid.Value(target.Id), ["code"] = AutoDimensionRules.CodeUnreadable,
                                    ["reason"] = "the " + end.Key + " reference has no readable position." });
                                continue;
                            }
                            var at = new AutoDimensionCandidate { SubjectId = Rid.Value(target.Id), HostId = Rid.Value(target.Id),
                                Source = "wall_top_" + end.Key, Label = topWall.Name, StableRepresentation = end.Value.Stable,
                                X = p.Subtract(vo).DotProduct(vr), Y = p.Subtract(vo).DotProduct(vu) };
                            var lv = new AutoDimensionCandidate { SubjectId = levelId.Value, Source = "level", Label = datum.Name,
                                StableRepresentation = datumStable, X = at.X,
                                Y = new XYZ(p.X, p.Y, datum.ProjectElevation).Subtract(vo).DotProduct(vu) };
                            int before = actions.Count;
                            var chainRows = new JArray(); var refusedRows = new JArray();
                            AddOpeningChain(new List<AutoDimensionCandidate> { lv, at }, "height_" + end.Key, "vertical",
                                end.Key == "start" ? "negative" : "positive", offsetFeet, doc, view, request, 1.0 / toFeet,
                                existingHeights, actions, chainRows, refusedRows);
                            if (refusedRows.Count > 0)
                            {
                                omitted.Add(new JObject { ["element_id"] = Rid.Value(target.Id), ["end"] = end.Key,
                                    ["code"] = refusedRows[0]["code"], ["reason"] = refusedRows[0]["reason"] });
                                continue;
                            }
                            // Every sloped-top action carries its own geometric height as a
                            // postcondition: horizun_annotate rolls the batch back if Revit
                            // measures anything else (a cut edge measured as zero, a vertex
                            // that resolved to the other end).
                            if (actions.Count > before)
                            {
                                actions.Last["expected_value"] = Math.Round((p.Z - datum.ProjectElevation) / toFeet, 6);
                                actions.Last["expected_tolerance"] = 1.0 / 304.8 / toFeet;
                            }
                            rows.Add(new JObject { ["element_id"] = Rid.Value(target.Id), ["axis"] = "vertical",
                                ["top_reference_mode"] = topChoice.Mode, ["end"] = end.Key,
                                ["estimated_value"] = (p.Z - datum.ProjectElevation) / toFeet,
                                ["already_dimensioned"] = actions.Count == before,
                                ["references"] = chainRows.Count > 0 ? chainRows[0]["references"].DeepClone() : new JArray(),
                                ["note"] = topChoice.Note });
                        }
                        continue;
                    }
                }
                var one = (JObject)request.DeepClone();
                one["operation"] = "intent_dimension";
                one["axis"] = "vertical";
                one.Remove("element_ids");
                one["reference_targets"] = new JArray(
                    new JObject { ["element_id"] = levelId.Value, ["selector"] = "level" },
                    new JObject { ["element_id"] = Rid.Value(target.Id), ["selector"] = selector });
                CommandResult plan = PlanDimension(app, doc, one, units, toFeet);
                JObject body = plan.Data as JObject;
                if (!plan.Success || body?.Value<bool?>("safe_to_execute") != true)
                {
                    omitted.Add(new JObject { ["element_id"] = Rid.Value(target.Id),
                        ["reason"] = plan.Error ?? "height reference did not produce a safe dimension plan" });
                    continue;
                }
                JObject action = (JObject)((JArray)body["next_arguments"]["actions"])[0].DeepClone();
                if (request["expected_value"] != null && targets.Count == 1 && actions.Count == 0)
                {
                    action["expected_value"] = request["expected_value"].DeepClone();
                    if (request["expected_tolerance"] != null)
                        action["expected_tolerance"] = request["expected_tolerance"].DeepClone();
                }
                actions.Add(action);
                rows.Add(new JObject { ["element_id"] = Rid.Value(target.Id),
                    ["references"] = body["reference_rows"].DeepClone(),
                    ["axis"] = "vertical" });
            }
            if (request["expected_value"] != null && actions.Count == 1 && targets.Count == 1)
            {
                actions[0]["expected_value"] = request["expected_value"].DeepClone();
                if (request["expected_tolerance"] != null)
                    actions[0]["expected_tolerance"] = request["expected_tolerance"].DeepClone();
            }
            return CommandResult.Ok(new JObject
            {
                ["operation"] = AutoDimensionRules.OpHeights, ["view_id"] = Rid.Value(view.Id),
                ["level_id"] = levelId.Value, ["height_selector"] = selector,
                ["planned"] = rows, ["omitted"] = omitted,
                ["coverage"] = omitted.Count == 0 && actions.Count > 0 ? "complete" :
                    actions.Count > 0 ? "partial" : "none",
                ["safe_to_execute"] = actions.Count > 0 && omitted.Count == 0,
                ["next_tool"] = "horizun_annotate",
                ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["next_step"] = actions.Count > 0
                    ? "Run next_arguments with horizun_annotate dry_run; Revit rehearsal proves that the " +
                      "level and face references can form a vertical dimension."
                    : "Inspect omitted, then use intent_dimension with level and an explicit top_face, " +
                      "bottom_face, opening_head or opening_sill reference.",
                ["note"] = "The planner wrote nothing. For a sloped top, use explicit end references and " +
                    "expected_value to verify each end; one sloped face is not two independent heights."
            });
        }

        private static CommandResult PlanRoomDimensions(UIApplication app, Document doc, JObject request,
            string units, double toFeet)
        {
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);
            if (!(view is ViewPlan)) return CommandResult.Fail("auto_dimension_room requires a plan view.");
            long? roomId = request.Value<long?>("room_id");
            Room room = roomId.HasValue && Rid.CanRepresent(roomId.Value)
                ? doc.GetElement(Rid.Make(roomId.Value)) as Room : null;
            if (room == null) return CommandResult.Fail("room_id must identify a Room in the active model.");
            IList<IList<BoundarySegment>> loops;
            try { loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions()); }
            catch (Exception ex) { return CommandResult.Fail("Room boundary could not be read: " + ex.Message); }
            if (loops == null || loops.Count != 1)
                return CommandResult.Fail("Room needs exactly one closed boundary loop for automatic dimensioning.");
            XYZ origin = view.Origin, right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize();
            var sides = new List<RoomBoundarySide>();
            foreach (BoundarySegment segment in loops[0])
            {
                Curve curve = segment.GetCurve();
                if (!(curve is Line) || !curve.IsBound)
                    return CommandResult.Fail("Room has a curved or unreadable boundary; no complete dimension plan was made.");
                Element wall = doc.GetElement(segment.ElementId);
                if (!(wall is Wall))
                    return CommandResult.Fail("Room boundary includes a non-wall segment " +
                        Rid.Value(segment.ElementId) + "; no complete wall dimension plan was made.");
                XYZ p0 = curve.GetEndPoint(0).Subtract(origin), p1 = curve.GetEndPoint(1).Subtract(origin);
                sides.Add(new RoomBoundarySide { WallId = Rid.Value(wall.Id),
                    X0 = p0.DotProduct(right), Y0 = p0.DotProduct(up),
                    X1 = p1.DotProduct(right), Y1 = p1.DotProduct(up) });
            }
            if (!AutoDimensionRules.TryRectangularRoom(sides, out RectangularRoomSides rectangle, out error))
                return PlanOrthogonalRoom(app, doc, view, request, units, toFeet, roomId.Value, sides, error);
            double centerX = (rectangle.Left.MidX + rectangle.Right.MidX) * 0.5;
            double centerY = (rectangle.Bottom.MidY + rectangle.Top.MidY) * 0.5;
            var chosen = new Dictionary<long, List<AutoDimensionCandidate>>();
            foreach (RoomBoundarySide side in sides)
            {
                Wall wall = doc.GetElement(Rid.Make(side.WallId)) as Wall;
                List<AutoDimensionCandidate> faces = RoomWallFaces(app, doc, view, wall,
                    centerX, centerY, units, toFeet, out error);
                if (faces == null) return CommandResult.Fail("Wall " + side.WallId + ": " + error);
                chosen[side.WallId] = faces;
            }
            double separation = (request.Value<double?>("chain_separation") ??
                                 request.Value<double?>("offset") ?? 15) * toFeet;
            double inside = (request.Value<double?>("interior_offset") ??
                             request.Value<double?>("offset") ?? 15) * toFeet;
            double outside = (request.Value<double?>("exterior_offset") ??
                              ((request.Value<double?>("offset") ?? 15) +
                               (request.Value<double?>("chain_separation") ?? 15))) * toFeet;
            double thickness = (request.Value<double?>("thickness_offset") ??
                                (request.Value<double?>("offset") ?? 15)) * toFeet;
            if (inside <= 0 || outside <= 0 || thickness <= 0 || separation <= 0)
                return CommandResult.Fail("Room dimension offsets and chain_separation must be positive.");
            var actions = new JArray(); var chains = new JArray(); var refused = new JArray();
            var existing = request.Value<bool?>("skip_existing") == false
                ? new HashSet<string>(StringComparer.Ordinal)
                : AutoDimensionPlanner.ExistingChainIdentities(doc, view);
            void Add(string role, RoomBoundarySide a, RoomBoundarySide b, string source,
                string axis, string side, double distance, HashSet<string> identities)
            {
                AddOpeningChain(new List<AutoDimensionCandidate> {
                    chosen[a.WallId].Single(c => c.Source == source),
                    chosen[b.WallId].Single(c => c.Source == source) },
                    role, axis, side, distance, doc, view, request, 1.0 / toFeet,
                    identities, actions, chains, refused);
                if (actions.Count > 0 && request["expected_values"] is JObject expected &&
                    expected[role] != null && (string)((JObject)chains.Last)["role"] == role)
                {
                    ((JObject)actions.Last)["expected_value"] = expected[role].DeepClone();
                    if (request["expected_tolerance"] != null)
                        ((JObject)actions.Last)["expected_tolerance"] = request["expected_tolerance"].DeepClone();
                }
            }
            Add("interior_x", rectangle.Left, rectangle.Right, "room_inner", "horizontal", "negative", inside, existing);
            Add("interior_y", rectangle.Bottom, rectangle.Top, "room_inner", "vertical", "negative", inside, existing);
            var rectInterior = new HashSet<int>(Enumerable.Range(0, actions.Count));
            var exteriorExisting = new HashSet<string>(existing, StringComparer.Ordinal);
            Add("exterior_x_top", rectangle.Left, rectangle.Right, "room_outer", "horizontal", "positive",
                outside, new HashSet<string>(exteriorExisting, StringComparer.Ordinal));
            Add("exterior_x_bottom", rectangle.Left, rectangle.Right, "room_outer", "horizontal", "negative",
                outside, new HashSet<string>(exteriorExisting, StringComparer.Ordinal));
            Add("exterior_y_right", rectangle.Bottom, rectangle.Top, "room_outer", "vertical", "positive",
                outside, new HashSet<string>(exteriorExisting, StringComparer.Ordinal));
            Add("exterior_y_left", rectangle.Bottom, rectangle.Top, "room_outer", "vertical", "negative",
                outside, new HashSet<string>(exteriorExisting, StringComparer.Ordinal));
            foreach (RoomBoundarySide wall in new[] { rectangle.Left, rectangle.Right, rectangle.Bottom, rectangle.Top })
            {
                string axis = wall == rectangle.Left || wall == rectangle.Right ? "horizontal" : "vertical";
                AddOpeningChain(chosen[wall.WallId], "thickness_" + wall.WallId, axis,
                    "positive", thickness, doc, view, request, 1.0 / toFeet,
                    existing, actions, chains, refused);
            }
            var wallIds = new HashSet<long>(sides.Select(s => s.WallId));
            var openingIds = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>().Where(f => f.Host is Wall host && wallIds.Contains(Rid.Value(host.Id)) &&
                    f.Category != null &&
                    (Rid.Value(f.Category.Id) == (long)BuiltInCategory.OST_Doors ||
                     Rid.Value(f.Category.Id) == (long)BuiltInCategory.OST_Windows))
                .Select(f => Rid.Value(f.Id)).OrderBy(id => id).ToList();
            var openingPlans = new JArray();
            if (openingIds.Count > 0)
            {
                foreach (RoomBoundarySide wall in new[] { rectangle.Left, rectangle.Right,
                                                            rectangle.Bottom, rectangle.Top })
                {
                    var hosted = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>().Where(f => f.Host != null &&
                            Rid.Value(f.Host.Id) == wall.WallId && f.Category != null &&
                            (Rid.Value(f.Category.Id) == (long)BuiltInCategory.OST_Doors ||
                             Rid.Value(f.Category.Id) == (long)BuiltInCategory.OST_Windows))
                        .Select(f => Rid.Value(f.Id)).OrderBy(id => id).ToList();
                    if (hosted.Count == 0) continue;
                    var openingRequest = (JObject)request.DeepClone();
                    openingRequest["element_ids"] = new JArray(hosted);
                    openingRequest["side"] = AutoDimensionRules.RoomOutsideSide(rectangle, wall);
                    openingRequest["offset"] = outside / toFeet + separation / toFeet;
                    CommandResult op = PlanAutoDimension(doc, openingRequest,
                        AutoDimensionRules.OpOpenings, units, toFeet);
                    if (op.Success && op.Data is JObject body)
                    {
                        openingPlans.Add(body);
                        foreach (JObject action in (JArray)body["next_arguments"]["actions"])
                            actions.Add(action.DeepClone());
                        if (body.Value<string>("coverage") != "complete")
                            refused.Add(new JObject { ["role"] = "openings:" + wall.WallId,
                                ["code"] = "partial_openings",
                                ["reason"] = "Opening chains are incomplete; inspect opening_plans." });
                    }
                    else refused.Add(new JObject { ["role"] = "openings:" + wall.WallId,
                        ["code"] = "opening_plan_failed", ["reason"] = op.Error });
                }
            }
            PlanBox rectRoom = PlanBox.FromCorners(rectangle.Left.MidX, rectangle.Bottom.MidY, rectangle.Right.MidX, rectangle.Top.MidY);
            JObject layout = ShiftRoomDimensionActions(doc, view, request, actions,
                centerX, centerY, toFeet, separation, rectRoom, outside + 6 * separation + 1.0, refused, rectInterior);
            if (layout.Value<bool?>("coverage_complete") != true)
                refused.Add(new JObject { ["role"] = "annotation_layout",
                    ["code"] = "annotation_collision_or_unknown", ["reason"] =
                    "Dimension lines could not all be placed clear of measured annotations; inspect annotation_layout." });
            bool complete = refused.Count == 0 && chains.Count == 10 &&
                (openingIds.Count == 0 || openingPlans.Count > 0);
            return CommandResult.Ok(new JObject {
                ["operation"] = AutoDimensionRules.OpRoom, ["room_id"] = roomId.Value,
                ["view_id"] = Rid.Value(view.Id), ["room_boundary_wall_ids"] = new JArray(wallIds.OrderBy(x => x)),
                ["chains"] = chains, ["opening_plans"] = openingPlans,
                ["annotation_layout"] = layout, ["refused"] = refused,
                ["coverage"] = complete ? "complete" : actions.Count > 0 ? "partial" : "none",
                ["safe_to_execute"] = complete, ["next_tool"] = "horizun_annotate",
                ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["next_step"] = complete
                    ? "Run next_arguments with horizun_annotate dry_run and inspect its constructibility and measured values."
                    : "Inspect refused and opening_plans; correct the ambiguous references or use dimension_set " +
                      "with explicit reference_targets for the affected walls.",
                ["note"] = "No model changes were made. Only a complete rectangular room plan is marked safe."
            });
        }

        /// <summary>
        /// auto_dimension_room for a simple ORTHOGONAL room that is not a rectangle (L,
        /// U, T...). The chain set and the inner/outer face of every wall are decided by
        /// Core OrthogonalRoomRules against the boundary and its outward normal; this
        /// method only reads faces and turns specs into horizun_annotate actions. A
        /// rectangle never comes here - its ten-chain plan is unchanged.
        /// </summary>
        private static CommandResult PlanOrthogonalRoom(UIApplication app, Document doc, View view, JObject request,
            string units, double toFeet, long roomId, List<RoomBoundarySide> sides, string rectangleError)
        {
            if (!OrthogonalRoomRules.TryOrthogonalLoop(sides, out List<OrthogonalRoomSide> loop, out string loopError))
                return CommandResult.Fail(rectangleError + " Not a simple orthogonal room either: " + loopError);
            double fromFeet = 1.0 / toFeet;
            double separation = (request.Value<double?>("chain_separation") ?? request.Value<double?>("offset") ?? 15) * toFeet;
            double inside = (request.Value<double?>("interior_offset") ?? request.Value<double?>("offset") ?? 15) * toFeet;
            double outside = (request.Value<double?>("exterior_offset") ?? request.Value<double?>("offset") ?? 15) * toFeet;
            double thickness = (request.Value<double?>("thickness_offset") ?? request.Value<double?>("offset") ?? 15) * toFeet;
            if (inside <= 0 || outside <= 0 || thickness <= 0 || separation <= 0)
                return CommandResult.Fail("Room dimension offsets and chain_separation must be positive.");

            var inner = new Dictionary<int, AutoDimensionCandidate>();
            var outer = new Dictionary<int, AutoDimensionCandidate>();
            var outerAcross = new Dictionary<int, double>();
            XYZ origin = view.Origin, right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize();
            foreach (OrthogonalRoomSide side in loop)
            {
                var wall = doc.GetElement(Rid.Make(side.WallId)) as Wall;
                var discovery = new JObject { ["view_id"] = Rid.Value(view.Id), ["element_ids"] = new JArray(side.WallId),
                    ["selectors"] = new JArray("exterior_face", "interior_face"), ["units"] = units, ["max_results"] = 500 };
                CommandResult found = new DimensionReferencesCommand().Execute(app, discovery.ToString());
                JObject data = found.Data as JObject;
                if (!found.Success || data?.Value<bool?>("truncated") != false || data["coverage"]?.Value<int?>("inspected") != 1)
                    return CommandResult.Fail("Wall " + side.WallId + ": " + (found.Error ?? "reference discovery did not inspect the wall completely"));
                var facts = new List<RoomFaceFact>();
                foreach (JObject row in ((JArray)data["rows"]).OfType<JObject>())
                {
                    if (row.Value<bool?>("compatible_with_dimension") != true || !(row["representative_point"] is JArray rp) ||
                        string.IsNullOrWhiteSpace(row.Value<string>("stable_representation"))) continue;
                    XYZ d = new XYZ(rp[0].Value<double>() * toFeet, rp[1].Value<double>() * toFeet, rp[2].Value<double>() * toFeet).Subtract(origin);
                    facts.Add(new RoomFaceFact { Stable = row.Value<string>("stable_representation"),
                        X = d.DotProduct(right), Y = d.DotProduct(up) });
                }
                double maxThickness = (wall?.Width ?? 2.0) + 5.0 / 304.8;
                if (!OrthogonalRoomRules.TryClassifyFaces(side, facts, maxThickness, out RoomFaceFact i, out RoomFaceFact o, out string faceError))
                    return CommandResult.Fail("Wall " + side.WallId + ": " + faceError);
                inner[side.Index] = new AutoDimensionCandidate { SubjectId = side.WallId, HostId = side.WallId, Source = "room_inner",
                    StableRepresentation = i.Stable, X = i.X, Y = i.Y, Label = wall?.Name };
                outer[side.Index] = new AutoDimensionCandidate { SubjectId = side.WallId, HostId = side.WallId, Source = "room_outer",
                    StableRepresentation = o.Stable, X = o.X, Y = o.Y, Label = wall?.Name };
                outerAcross[side.Index] = side.Axis == "horizontal" ? o.Y : o.X;
            }

            var actions = new JArray(); var chains = new JArray(); var refused = new JArray();
            HashSet<string> existing = request.Value<bool?>("skip_existing") == false
                ? new HashSet<string>(StringComparer.Ordinal)
                : AutoDimensionPlanner.ExistingChainIdentities(doc, view);
            List<RoomChainSpec> specs = OrthogonalRoomRules.PlanChains(loop, outerAcross, inside, outside, separation);
            var loopInterior = new HashSet<int>();
            var exteriorActions = new List<KeyValuePair<JObject, KeyValuePair<RoomChainSpec, List<AutoDimensionCandidate>>>>();
            var thicknessActions = new Dictionary<int, OrthogonalRoomSide>();
            double tail = Math.Max(outside * AutoDimensionRules.TailFraction, AutoDimensionRules.CoincidenceToleranceFeet * 10);
            foreach (RoomChainSpec spec in specs)
            {
                var members = spec.Members.Select(m => m.Value == "room_inner" ? inner[m.Key] : outer[m.Key]).ToList();
                string orderError = AutoDimensionRules.OrderAlongAxis(members, spec.Axis, out List<AutoDimensionCandidate> ordered, out string code);
                if (orderError != null) { refused.Add(new JObject { ["role"] = spec.Role, ["code"] = code, ["reason"] = orderError }); continue; }
                var reps = ordered.Select(c => c.StableRepresentation).ToList();
                // Exterior total/partial over one facade may repeat another facade's set; both
                // are drawn (two sides of the building), so only a set already ON the view skips.
                if (AutoDimensionRules.IsDuplicate(reps, existing)) continue;
                var chain = new AutoDimensionChain { GroupKey = spec.Role };
                OrthogonalRoomRules.PlaceLineAt(ordered, spec.Axis, spec.LineAcross, tail, chain);
                XYZ start = origin.Add(right.Multiply(chain.StartX)).Add(up.Multiply(chain.StartY));
                XYZ end = origin.Add(right.Multiply(chain.EndX)).Add(up.Multiply(chain.EndY));
                var action = new JObject { ["operation"] = "dimension", ["view_id"] = Rid.Value(view.Id),
                    ["line_start"] = Point(start, fromFeet), ["line_end"] = Point(end, fromFeet), ["references"] = new JArray(reps) };
                if (request["dimension_type_id"] != null) action["dimension_type_id"] = request["dimension_type_id"].DeepClone();
                if (request["expected_values"] is JObject expected && expected[spec.Role] != null)
                {
                    action["expected_value"] = expected[spec.Role].DeepClone();
                    if (request["expected_tolerance"] != null) action["expected_tolerance"] = request["expected_tolerance"].DeepClone();
                }
                actions.Add(action);
                if (spec.Kind == "interior") loopInterior.Add(actions.Count - 1);
                if (spec.Kind != "interior") exteriorActions.Add(new KeyValuePair<JObject, KeyValuePair<RoomChainSpec, List<AutoDimensionCandidate>>>(
                    action, new KeyValuePair<RoomChainSpec, List<AutoDimensionCandidate>>(spec, ordered)));
                chains.Add(new JObject { ["role"] = spec.Role, ["kind"] = spec.Kind, ["axis"] = spec.Axis, ["side"] = spec.Side,
                    ["references"] = new JArray(ordered.Select(c => AutoDimensionPlanner.CandidateJson(c, fromFeet))) });
            }

            var openingPlans = new JArray();
            int openingWalls = 0;
            foreach (OrthogonalRoomSide side in loop)
            {
                var hosted = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>().Where(f => f.Host != null && Rid.Value(f.Host.Id) == side.WallId && f.Category != null &&
                        (Rid.Value(f.Category.Id) == (long)BuiltInCategory.OST_Doors ||
                         Rid.Value(f.Category.Id) == (long)BuiltInCategory.OST_Windows))
                    .Select(f => Rid.Value(f.Id)).OrderBy(id => id).ToList();
                if (hosted.Count == 0) continue;
                openingWalls++;
                var openingRequest = (JObject)request.DeepClone();
                openingRequest["element_ids"] = new JArray(hosted);
                openingRequest["side"] = side.Outward > 0 ? "positive" : "negative";
                openingRequest["offset"] = outside / toFeet;
                CommandResult op = PlanAutoDimension(doc, openingRequest, AutoDimensionRules.OpOpenings, units, toFeet);
                if (op.Success && op.Data is JObject body)
                {
                    body["wall_id"] = side.WallId;
                    openingPlans.Add(body);
                    foreach (JObject action in (JArray)body["next_arguments"]["actions"]) actions.Add(action.DeepClone());
                    if (body.Value<string>("coverage") != "complete")
                        refused.Add(new JObject { ["role"] = "openings:" + side.WallId, ["code"] = "partial_openings",
                            ["reason"] = "Opening chains are incomplete; inspect opening_plans." });
                }
                else refused.Add(new JObject { ["role"] = "openings:" + side.WallId, ["code"] = "opening_plan_failed", ["reason"] = op.Error });
            }
            // Thickness lines LAST, so their strict check sees every other line.
            foreach (OrthogonalRoomSide side in loop)
            {
                string axis = side.Axis == "horizontal" ? "vertical" : "horizontal";
                loopInterior.Add(actions.Count);   // a thickness line stays at its wall: fixed, never slid away
                thicknessActions[actions.Count] = side;
                AddOpeningChain(new List<AutoDimensionCandidate> { inner[side.Index], outer[side.Index] }, "thickness_" + side.WallId,
                    axis, "positive", thickness, doc, view, request, fromFeet, existing, actions, chains, refused);
            }
            double centerX = (loop.Where(s => s.Axis == "vertical").Min(s => s.Across) + loop.Where(s => s.Axis == "vertical").Max(s => s.Across)) * 0.5;
            double centerY = (loop.Where(s => s.Axis == "horizontal").Min(s => s.Across) + loop.Where(s => s.Axis == "horizontal").Max(s => s.Across)) * 0.5;
            PlanBox loopRoom = PlanBox.FromCorners(loop.Where(s => s.Axis == "vertical").Min(s => s.Across), loop.Where(s => s.Axis == "horizontal").Min(s => s.Across),
                loop.Where(s => s.Axis == "vertical").Max(s => s.Across), loop.Where(s => s.Axis == "horizontal").Max(s => s.Across));
            JObject layout = ShiftRoomDimensionActions(doc, view, request, actions, centerX, centerY, toFeet, separation,
                loopRoom, outside + 6 * separation + 1.0, refused, loopInterior, new HashSet<int>(thicknessActions.Keys));
            if (layout.Value<bool?>("coverage_complete") != true)
                refused.Add(new JObject { ["role"] = "annotation_layout", ["code"] = "annotation_collision_or_unknown", ["reason"] =
                    "Dimension lines could not all be placed clear of measured annotations; inspect annotation_layout." });
            // A thickness line that collides where it is drawn is FOLDED into the exterior
            // chain that already ends on that wall's outer face (a partial chain first):
            // the wall's inner face joins the chain, so the thickness reads as the chain's
            // end segment instead of being lost.
            var folded = new JArray();
            foreach (JObject drop in (layout["dropped"] as JArray ?? new JArray()).OfType<JObject>().ToList())
            {
                int index = drop.Value<int>("action_index");
                if (!thicknessActions.TryGetValue(index, out OrthogonalRoomSide wallSide)) continue;
                string thicknessAxis = wallSide.Axis == "horizontal" ? "vertical" : "horizontal";
                var host = exteriorActions
                    .Where(x => x.Value.Key.Axis == thicknessAxis && actions.Contains(x.Key) &&
                                x.Value.Value.Any(m => m.StableRepresentation == outer[wallSide.Index].StableRepresentation))
                    .OrderBy(x => x.Value.Key.Kind == "exterior_partial" ? 0 : 1).FirstOrDefault();
                if (host.Key == null) continue;
                var members = new List<AutoDimensionCandidate>(host.Value.Value) { inner[wallSide.Index] };
                if (AutoDimensionRules.OrderAlongAxis(members, thicknessAxis, out List<AutoDimensionCandidate> reordered, out _) != null) continue;
                host.Value.Value.Clear(); host.Value.Value.AddRange(reordered);
                host.Key["references"] = new JArray(reordered.Select(m => m.StableRepresentation));
                host.Key.Remove("expected_value"); host.Key.Remove("expected_tolerance");
                folded.Add(new JObject { ["wall_id"] = wallSide.WallId, ["into"] = host.Value.Key.Role,
                    ["references"] = reordered.Count });
                foreach (JObject r in refused.OfType<JObject>().Where(r => r.Value<string>("code") == "chain_outside_layout_limit" &&
                             JToken.DeepEquals(r["references"], drop["references"])).ToList())
                    refused.Remove(r);
            }
            if (folded.Count > 0) layout["thickness_folded"] = folded;
            foreach (var x in exteriorActions)
            {
                JObject row = chains.OfType<JObject>().FirstOrDefault(c => c.Value<string>("role") == x.Value.Key.Role);
                if (row != null) row["references"] = new JArray(x.Value.Value.Select(c => AutoDimensionPlanner.CandidateJson(c, fromFeet)));
            }
            bool complete = refused.Count == 0 && openingPlans.Count == openingWalls && actions.Count > 0;
            return CommandResult.Ok(new JObject {
                ["operation"] = AutoDimensionRules.OpRoom, ["room_id"] = roomId, ["view_id"] = Rid.Value(view.Id),
                ["room_shape"] = "orthogonal", ["sides"] = new JArray(loop.Select(s => new JObject {
                    ["index"] = s.Index, ["wall_id"] = s.WallId, ["facade"] = s.Facade, ["axis"] = s.Axis,
                    ["length"] = (s.AlongMax - s.AlongMin) * fromFeet })),
                ["room_boundary_wall_ids"] = new JArray(loop.Select(s => s.WallId).OrderBy(x => x)),
                ["chains"] = chains, ["opening_plans"] = openingPlans, ["annotation_layout"] = layout, ["refused"] = refused,
                ["coverage"] = complete ? "complete" : actions.Count > 0 ? "partial" : "none",
                ["safe_to_execute"] = complete, ["next_tool"] = "horizun_annotate",
                ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["next_step"] = complete
                    ? "Run next_arguments with horizun_annotate dry_run and inspect its constructibility and measured values."
                    : "Inspect refused and opening_plans; correct the ambiguous references or use dimension_set " +
                      "with explicit reference_targets for the affected walls.",
                ["note"] = "No model changes were made. Roles: interior_w<wall> (clear span along that wall, between its " +
                    "neighbours' inner faces), exterior_<facade>_total and _partial (outer faces where the outline steps), " +
                    "thickness_<wall>; expected_values is keyed by these roles."
            });
        }
        private static JObject ShiftRoomDimensionActions(Document doc, View view, JObject request,
            JArray actions, double centerX, double centerY, double toFeet, double separation,
            PlanBox room, double defaultMargin, JArray refused, HashSet<int> interiorActions, HashSet<int> strictActions = null)
        {
            // How far outside the room box a chain may land (Core ViewCompositionRules.
            // LayoutLimit): interior chains stay inside the room, the rest within the room
            // box plus layout_margin. Measured live: without it an interior chain of an L
            // room was pushed 3150 mm out of the room. A chain that fits nowhere inside
            // its limit is DROPPED from the actions and reported, never moved further.
            double margin = (request.Value<double?>("layout_margin") is double lm ? lm * toFeet : defaultMargin);
            if (margin < 0) return new JObject { ["coverage_complete"] = false, ["reason"] = "layout_margin must be zero or positive." };
            AnnotationSurvey survey;
            try { survey = AnnotationLayout.Survey(doc, view); }
            catch (Exception ex) { return new JObject { ["coverage_complete"] = false,
                ["reason"] = "Annotation survey failed: " + ex.Message }; }
            if (!survey.Complete)
                return new JObject { ["coverage_complete"] = false,
                    ["reason"] = "Annotation extent coverage is incomplete.",
                    ["survey"] = survey.Coverage };
            double clearance = (request.Value<double?>("annotation_clearance") ?? 10) * toFeet;
            if (clearance <= 0)
                return new JObject { ["coverage_complete"] = false,
                    ["reason"] = "annotation_clearance must be positive." };
            // Each planned line remembers its axis: a dimension line may CROSS a perpendicular
            // one (interior spans of an L always do) - the overlap report checks text on
            // lines separately - but may not run over a parallel one or over a measured
            // annotation (axis null).
            var occupied = survey.Obstacles.Select(o => new KeyValuePair<PlanBox, string>(o, null)).ToList();
            var moved = new JArray();
            var dropped = new JArray();
            var keep = new List<JObject>();
            int actionIndex = 0;
            XYZ origin = view.Origin, right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize();
            foreach (JObject action in actions.OfType<JObject>().ToList())
            {
                JArray a = action["line_start"] as JArray, b = action["line_end"] as JArray;
                if (a == null || b == null) return new JObject { ["coverage_complete"] = false,
                    ["reason"] = "A dimension action lacks line endpoints." };
                XYZ start = new XYZ(a[0].Value<double>() * toFeet, a[1].Value<double>() * toFeet,
                    a[2].Value<double>() * toFeet);
                XYZ end = new XYZ(b[0].Value<double>() * toFeet, b[1].Value<double>() * toFeet,
                    b[2].Value<double>() * toFeet);
                XYZ s = start.Subtract(origin), e = end.Subtract(origin);
                double sx = s.DotProduct(right), sy = s.DotProduct(up);
                double ex = e.DotProduct(right), ey = e.DotProduct(up);
                string axis = Math.Abs(ex - sx) >= Math.Abs(ey - sy) ? "horizontal" : "vertical";
                PlanBox line = PlanBox.FromCorners(Math.Min(sx, ex) - clearance,
                    Math.Min(sy, ey) - clearance, Math.Max(sx, ex) + clearance,
                    Math.Max(sy, ey) + clearance);
                double outward = axis == "horizontal"
                    ? (sy + ey) * 0.5 >= centerY ? 1 : -1
                    : (sx + ex) * 0.5 >= centerX ? 1 : -1;
                bool interiorLine = interiorActions != null && interiorActions.Contains(actionIndex);
                PlanBox limit = ViewCompositionRules.LayoutLimit(room, line, margin, interiorLine);
                PlanBox? allowed = survey.Bounds.HasValue ? ViewCompositionRules.Intersect(survey.Bounds.Value, limit) : limit;
                if (allowed.HasValue && !allowed.Value.Valid) allowed = limit;
                // An interior chain is drawn at its declared offset from its own wall and is
                // never moved (measured live on an L room: shifting "away from the centre"
                // carried one across the notch, outside the room but inside its box). Other
                // chains move OUTWARD only, within their limit.
                double shift;
                // A STRICT line (a thickness, whose short text sits on whatever crosses it -
                // measured live) may not cross anything; it is folded into an exterior chain.
                bool strict = strictActions != null && strictActions.Contains(actionIndex);
                var blocking = occupied.Where(o => strict || o.Value == null || o.Value == axis).Select(o => o.Key).ToList();
                if (!AutoDimensionRules.TryShiftDimensionLine(line, blocking, allowed, axis, outward, separation,
                        interiorLine ? 0 : 120, out shift))
                {
                    dropped.Add(new JObject { ["action_index"] = actionIndex, ["axis"] = axis,
                        ["references"] = action["references"]?.DeepClone(),
                        ["reason"] = interiorLine
                            ? "the interior chain collides at its declared offset and interior chains are not moved; it is not planned."
                            : "no collision-free outward position within the room box plus layout_margin; the chain was not moved further and is not planned." });
                    actionIndex++;
                    continue;
                }
                XYZ translation = (axis == "horizontal" ? up : right).Multiply(shift);
                if (shift != 0)
                {
                    action["line_start"] = Point(start.Add(translation), 1.0 / toFeet);
                    action["line_end"] = Point(end.Add(translation), 1.0 / toFeet);
                }
                occupied.Add(new KeyValuePair<PlanBox, string>(axis == "horizontal"
                    ? PlanBox.FromCorners(line.MinX, line.MinY + shift, line.MaxX, line.MaxY + shift)
                    : PlanBox.FromCorners(line.MinX + shift, line.MinY, line.MaxX + shift, line.MaxY), axis));
                moved.Add(new JObject { ["action_index"] = actionIndex, ["axis"] = axis,
                    ["shift"] = shift / toFeet });
                keep.Add(action);
                actionIndex++;
            }
            if (dropped.Count > 0)
            {
                actions.Clear();
                foreach (JObject k in keep) actions.Add(k);
                foreach (JObject d in dropped)
                    refused.Add(new JObject { ["role"] = "annotation_layout", ["code"] = "chain_outside_layout_limit",
                        ["reason"] = d.Value<string>("reason"), ["references"] = d["references"]?.DeepClone() });
            }
            return new JObject { ["coverage_complete"] = true, ["survey"] = survey.Coverage,
                ["line_adjustments"] = moved, ["dropped"] = dropped,
                ["layout_limit"] = new JObject { ["room_box"] = new JArray(room.MinX / toFeet, room.MinY / toFeet, room.MaxX / toFeet, room.MaxY / toFeet),
                    ["margin"] = margin / toFeet, ["means"] = "interior chains stay inside room_box; others within room_box grown by margin." },
                ["note"] = "Clearance uses bounded dimension-line bands; actual text glyphs require " +
                    "the horizun_annotate rehearsal and visual review." };
        }

        private static List<AutoDimensionCandidate> RoomWallFaces(UIApplication app, Document doc,
            View view, Wall wall, double centerX, double centerY, string units, double toFeet, out string error)
        {
            error = null;
            var request = new JObject { ["view_id"] = Rid.Value(view.Id),
                ["element_ids"] = new JArray(Rid.Value(wall.Id)),
                ["selectors"] = new JArray("exterior_face", "interior_face"),
                ["units"] = units, ["max_results"] = 500 };
            CommandResult result = new DimensionReferencesCommand().Execute(app, request.ToString());
            JObject data = result.Data as JObject;
            if (!result.Success || data?.Value<bool?>("truncated") != false ||
                data["coverage"]?.Value<int?>("inspected") != 1)
            {
                error = result.Error ?? "reference discovery did not inspect the wall completely";
                return null;
            }
            XYZ origin = view.Origin, right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize();
            var choices = new List<KeyValuePair<double, AutoDimensionCandidate>>();
            foreach (JObject row in ((JArray)data["rows"]).OfType<JObject>())
            {
                if (row.Value<bool?>("compatible_with_dimension") != true ||
                    row["representative_point"] is not JArray p ||
                    string.IsNullOrWhiteSpace(row.Value<string>("stable_representation"))) continue;
                XYZ point = new XYZ(p[0].Value<double>() * toFeet,
                    p[1].Value<double>() * toFeet, p[2].Value<double>() * toFeet);
                XYZ delta = point.Subtract(origin);
                double x = delta.DotProduct(right), y = delta.DotProduct(up);
                var candidate = new AutoDimensionCandidate { SubjectId = Rid.Value(wall.Id),
                    HostId = Rid.Value(wall.Id), StableRepresentation = row.Value<string>("stable_representation"),
                    X = x, Y = y, Label = wall.Name };
                choices.Add(new KeyValuePair<double, AutoDimensionCandidate>(
                    Math.Sqrt(Math.Pow(x - centerX, 2) + Math.Pow(y - centerY, 2)), candidate));
            }
            if (choices.Count < 2)
            {
                error = "fewer than two compatible side-face references were read";
                return null;
            }
            var ranked = choices.OrderBy(c => c.Key).ToList();
            AutoDimensionCandidate inner = ranked.First().Value, outer = ranked.Last().Value;
            if (inner.StableRepresentation == outer.StableRepresentation ||
                Math.Abs(ranked.Last().Key - ranked.First().Key) <= AutoDimensionRules.CoincidenceToleranceFeet)
            {
                error = "interior and exterior faces cannot be distinguished relative to the Room";
                return null;
            }
            inner.Source = "room_inner"; outer.Source = "room_outer";
            return new List<AutoDimensionCandidate> { inner, outer };
        }

        /// <summary>
        /// Plan complete dimension chains for one semantic family - grids, levels,
        /// curtain grid lines, opening centres - in one view, over the host or over one
        /// named link instance. Read-only; the output is a horizun_annotate dry-run
        /// request plus the full account of what was found, what was planned, what was
        /// omitted and why, and whether that adds up to complete coverage.
        /// </summary>
        private static CommandResult PlanAutoDimension(Document doc, JObject request, string operation,
                                                       string units, double toFeet)
        {
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);

            string axisRequested = (request.Value<string>("axis") ?? "auto").ToLowerInvariant();
            error = AutoDimensionRules.ValidateAxis(axisRequested); if (error != null) return CommandResult.Fail(error);
            string side = (request.Value<string>("side") ?? "positive").ToLowerInvariant();
            error = AutoDimensionRules.ValidateSide(side); if (error != null) return CommandResult.Fail(error);
            double offset = (request.Value<double?>("offset") ?? 15.0) * toFeet;
            if (offset <= 0) return CommandResult.Fail("offset must be greater than zero.");
            double separation = (request.Value<double?>("chain_separation") ?? (request.Value<double?>("offset") ?? 15.0)) * toFeet;
            if (separation <= 0) return CommandResult.Fail("chain_separation must be greater than zero.");
            bool skipExisting = request.Value<bool?>("skip_existing") ?? true;

            // ---- the source is the HOST, and a link instance is refused with the ----
            // MEASURED reason. On live Revit 2026 (2026-08-26) NewDimension rejects
            // DATUM references lifted through CreateLinkReference ('Invalid number of
            // references' - the same rehearsal that PASSES against linked wall faces),
            // so a grid/level chain over a link would plan dimensions whose every
            // rehearsal fails. Curtain grid lines and opening-centre references are
            // datum-backed and have not been proven constructible through a link, and
            // this planner does not declare support Revit has not demonstrated.
            // Linked FACES are proven and flow through linked_targets +
            // horizun_annotate directly.
            var source = new AutoDimensionSource { GeometryDoc = doc };
            long? linkInstanceId = request.Value<long?>("link_instance_id");
            if (linkInstanceId.HasValue)
            {
                if (operation == AutoDimensionRules.OpGrids || operation == AutoDimensionRules.OpLevels)
                    return CommandResult.Fail(
                        operation + " cannot run over a link instance: " +
                        DimensionReferenceRules.LinkedDatumRejected().Message + " Nothing was planned.");
                return CommandResult.Fail(
                    operation + " over a link instance is refused: linked curtain-grid-line and opening-centre " +
                    "references are datum-backed, and Revit's dimension API rejects linked datum references " +
                    "(measured live 2026-08-26: 'Invalid number of references'). Dimension linked geometry " +
                    "through horizun_get_dimension_references linked_targets and horizun_annotate instead. " +
                    "Nothing was planned.");
            }

            List<long> explicitIds = null;
            if (request["element_ids"] is JArray idsArray)
            {
                if (idsArray.Count < 1 || idsArray.Any(t => t.Type != JTokenType.Integer))
                    return CommandResult.Fail("element_ids, when present, must be a non-empty array of integer " +
                                              "ids" + (source.IsLinked ? " from inside the linked document." : "."));
                explicitIds = idsArray.Select(t => t.Value<long>()).Distinct().ToList();
            }

            // ---- collect ------------------------------------------------------------
            var omissions = new List<AutoDimensionOmission>();
            List<AutoDimensionCandidate> candidates =
                AutoDimensionPlanner.Collect(operation, doc, view, source, explicitIds, omissions, out error);
            if (candidates == null) return CommandResult.Fail(error);
            if (operation == AutoDimensionRules.OpOpenings)
                return PlanOpeningDimensions(doc, view, request, units, toFeet, candidates, omissions,
                    side, offset, separation, skipExisting);

            // ---- group --------------------------------------------------------------
            // LEVELS are one vertical stack by definition and are never direction-
            // grouped: a level datum has no direction in a section, and the chain
            // everybody wants is the storey stack.
            List<List<AutoDimensionCandidate>> groups;
            if (operation == AutoDimensionRules.OpLevels)
            {
                groups = candidates.Count > 0
                    ? new List<List<AutoDimensionCandidate>> { candidates }
                    : new List<List<AutoDimensionCandidate>>();
            }
            else
            {
                List<AutoDimensionCandidate> ungroupable;
                groups = AutoDimensionRules.GroupByDirection(candidates,
                    AutoDimensionRules.DirectionToleranceDegrees, out ungroupable);
                foreach (AutoDimensionCandidate u in ungroupable)
                    omissions.Add(new AutoDimensionOmission(u, AutoDimensionRules.CodeNoDirection,
                        "the reference has no usable direction in this view's plane, so it cannot be assigned " +
                        "to a parallel chain. Use intent_dimension to name it explicitly."));
            }

            // Deterministic order over the groups themselves.
            groups = groups.OrderBy(g => AutoDimensionRules.GroupKey(g), StringComparer.Ordinal).ToList();

            HashSet<string> existing = skipExisting
                ? AutoDimensionPlanner.ExistingChainIdentities(doc, view)
                : new HashSet<string>(StringComparer.Ordinal);

            // ---- order, deduplicate, place ------------------------------------------
            var actions = new JArray();
            var chainRows = new JArray();
            var refusedGroups = new JArray();
            double fromFeet = 1.0 / toFeet;
            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;
            int planned = 0;
            int chainOrdinal = 0;
            foreach (List<AutoDimensionCandidate> group in groups)
            {
                string groupKey = AutoDimensionRules.GroupKey(group);
                // Levels are stacked vertically whatever "auto" would measure; every
                // other family measures ACROSS its lines: a family of parallel verticals
                // spreads horizontally, and that spread axis is the chain axis.
                double spreadX, spreadY;
                string axis = operation == AutoDimensionRules.OpLevels && axisRequested == "auto"
                    ? "vertical"
                    : AutoDimensionRules.ResolveAxis(group, axisRequested, out spreadX, out spreadY);

                List<AutoDimensionCandidate> ordered; string code;
                string groupError = AutoDimensionRules.OrderAlongAxis(group, axis, out ordered, out code);
                if (groupError != null)
                {
                    if (code == AutoDimensionRules.CodeGroupTooSmall)
                    {
                        foreach (AutoDimensionCandidate c in group)
                            omissions.Add(new AutoDimensionOmission(c, code, groupError));
                    }
                    else
                    {
                        refusedGroups.Add(new JObject
                        {
                            ["group"] = groupKey, ["axis"] = axis, ["code"] = code, ["reason"] = groupError,
                            ["members"] = new JArray(group.Select(c => AutoDimensionPlanner.CandidateJson(c, fromFeet)))
                        });
                        foreach (AutoDimensionCandidate c in group)
                            omissions.Add(new AutoDimensionOmission(c, code, "its group was refused: " + groupError));
                    }
                    continue;
                }

                List<string> reps = ordered.Select(c => c.StableRepresentation).ToList();
                List<string> dedupReps = ordered.Select(c => c.EffectiveDedupIdentity).ToList();
                if (AutoDimensionRules.IsDuplicate(dedupReps, existing))
                {
                    foreach (AutoDimensionCandidate c in ordered)
                        omissions.Add(new AutoDimensionOmission(c, AutoDimensionRules.CodeAlreadyDimensioned,
                            "a dimension over exactly this reference set already exists in the view; " +
                            "skip_existing=false plans it anyway."));
                    continue;
                }

                var chain = new AutoDimensionChain { GroupKey = groupKey };
                chain.Ordered.AddRange(ordered);
                double stacked = AutoDimensionRules.StackedOffset(offset, separation, chainOrdinal);
                AutoDimensionRules.PlaceLine(ordered, axis, side, stacked, chain);
                bool movedInside = AutoDimensionRules.KeepInsideCrop(chain, CropRectInViewFrame(view, origin, right, up), stacked);
                chainOrdinal++;

                XYZ start = origin.Add(right.Multiply(chain.StartX)).Add(up.Multiply(chain.StartY));
                XYZ end = origin.Add(right.Multiply(chain.EndX)).Add(up.Multiply(chain.EndY));
                var action = new JObject
                {
                    ["operation"] = "dimension", ["view_id"] = Rid.Value(view.Id),
                    ["line_start"] = Point(start, fromFeet), ["line_end"] = Point(end, fromFeet),
                    ["references"] = new JArray(reps)
                };
                if (request["dimension_type_id"] != null)
                    action["dimension_type_id"] = request["dimension_type_id"].DeepClone();
                actions.Add(action);
                planned += ordered.Count;
                chainRows.Add(new JObject
                {
                    ["group"] = groupKey, ["axis"] = chain.Axis, ["side"] = side,
                    ["offset_applied"] = stacked * fromFeet,
                    ["moved_inside_crop"] = movedInside,
                    ["references"] = new JArray(ordered.Select(c => AutoDimensionPlanner.CandidateJson(c, fromFeet))),
                    ["chain_identity"] = AutoDimensionRules.ChainIdentityUnordered(dedupReps)
                });
            }

            string coverage = AutoDimensionRules.Coverage(candidates.Count + omissions.Count(o =>
                o.Code == AutoDimensionRules.CodeNoReference || o.Code == AutoDimensionRules.CodeUnreadable),
                planned, omissions.Count);
            var result = new JObject
            {
                ["operation"] = operation,
                ["view_id"] = Rid.Value(view.Id),
                // Host-only by measurement (2026-08-26): link_instance_id refuses
                // above, so the source is always the host and says so.
                ["source"] = new JObject { ["linked"] = false },
                ["axis"] = axisRequested,
                ["found"] = candidates.Count + omissions.Count(o =>
                    o.Code == AutoDimensionRules.CodeNoReference || o.Code == AutoDimensionRules.CodeUnreadable),
                ["planned_references"] = planned,
                ["chains"] = chainRows,
                ["refused_groups"] = refusedGroups,
                ["omitted"] = new JArray(omissions.Select(o => AutoDimensionPlanner.OmissionJson(o, fromFeet))),
                ["coverage"] = coverage,
                ["ordering"] = AutoDimensionRules.OrderingNote,
                ["safe_to_execute"] = actions.Count > 0 && refusedGroups.Count == 0,
                ["next_tool"] = "horizun_annotate",
                ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["next_step"] = actions.Count > 0 && refusedGroups.Count == 0 && coverage == "complete"
                    ? "Run next_arguments with horizun_annotate dry_run; inspect its constructibility before applying."
                    : AutoDimensionRules.NextStep(operation,
                        omissions.FirstOrDefault()?.Code ?? (candidates.Count == 0
                            ? AutoDimensionRules.CodeNothingToDimension : null),
                        candidates.Count, actions.Count),
                ["note"] = "This planner made no model changes. Run the returned horizun_annotate dry run; only " +
                           "its rehearsal proves each chain is constructible in this view, and only its " +
                           "confirmation token writes."
            };
            if (actions.Count == 0)
                result["nothing_planned_code"] = candidates.Count == 0
                    ? AutoDimensionRules.CodeNothingToDimension
                    : (omissions.Any(o => o.Code == AutoDimensionRules.CodeAlreadyDimensioned)
                        ? AutoDimensionRules.CodeAlreadyDimensioned
                        : AutoDimensionRules.CodeNothingToDimension);
            return CommandResult.Ok(result);
        }

        private static CommandResult PlanOpeningDimensions(Document doc, View view, JObject request,
            string units, double toFeet, List<AutoDimensionCandidate> candidates,
            List<AutoDimensionOmission> omissions, string side, double offset, double separation,
            bool skipExisting)
        {
            double fromFeet = 1.0 / toFeet;
            var actions = new JArray();
            var chains = new JArray();
            var refused = new JArray();
            HashSet<string> existing = skipExisting
                ? AutoDimensionPlanner.ExistingChainIdentities(doc, view)
                : new HashSet<string>(StringComparer.Ordinal);
            var pairs = AutoDimensionRules.OpeningWidthPairs(candidates);
            foreach (var missing in candidates.GroupBy(c => c.SubjectId).Where(g =>
                !pairs.Any(p => p[0].SubjectId == g.Key)))
                foreach (AutoDimensionCandidate c in missing)
                    omissions.Add(new AutoDimensionOmission(c, AutoDimensionRules.CodeGroupTooSmall,
                        "opening has no distinct Left and Right pair; inspect its family references."));

            foreach (var wallGroup in pairs.GroupBy(p => p[0].HostId.Value).OrderBy(g => g.Key))
            {
                foreach (List<AutoDimensionCandidate> pair in wallGroup)
                {
                    string axis = AutoDimensionRules.AxisForOpeningDirection(
                        pair[0].DirectionX ?? 0, pair[0].DirectionY ?? 0);
                    if (axis == null)
                    {
                        refused.Add(new JObject { ["opening_id"] = pair[0].SubjectId,
                            ["code"] = "diagonal_opening", ["reason"] =
                            "The opening's wall is diagonal in this view; this planner needs an aligned dimension line." });
                        continue;
                    }
                    // Widths of one wall share the first line: openings do not overlap along
                    // the wall, so stacking them only pushed chains out of the drawing.
                    AddOpeningChain(pair, "width", axis, side,
                        AutoDimensionRules.StackedOffset(offset, separation, 0),
                        doc, view, request, fromFeet, existing, actions, chains, refused);
                }
                Wall wall = Rid.CanRepresent(wallGroup.Key)
                    ? doc.GetElement(Rid.Make(wallGroup.Key)) as Wall : null;
                if (wall == null) continue;
                var chainRefs = AutoDimensionPlanner.WallEndFaces(doc, view, wall, omissions);
                chainRefs.AddRange(wallGroup.SelectMany(p => p));
                string wallAxis = AutoDimensionRules.AxisForOpeningDirection(
                    wallGroup.First()[0].DirectionX ?? 0, wallGroup.First()[0].DirectionY ?? 0);
                if (wallAxis != null && chainRefs.Count >= 4)
                    AddOpeningChain(chainRefs, "wall_openings_and_piers", wallAxis, side,
                        AutoDimensionRules.StackedOffset(offset, separation, 1),
                        doc, view, request, fromFeet, existing, actions, chains, refused);
            }
            var result = new JObject
            {
                ["operation"] = AutoDimensionRules.OpOpenings,
                ["view_id"] = Rid.Value(view.Id),
                ["found"] = candidates.Count,
                ["planned_references"] = chains.OfType<JObject>().Sum(c => ((JArray)c["references"]).Count),
                ["chains"] = chains,
                ["refused_groups"] = refused,
                ["omitted"] = new JArray(omissions.Select(o => AutoDimensionPlanner.OmissionJson(o, fromFeet))),
                ["coverage"] = omissions.Count == 0 && refused.Count == 0 && actions.Count > 0
                    ? "complete" : actions.Count > 0 ? "partial" : "none",
                ["safe_to_execute"] = actions.Count > 0 && refused.Count == 0,
                ["next_tool"] = "horizun_annotate",
                ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["next_step"] = actions.Count > 0
                    ? "Run next_arguments as a horizun_annotate dry run; its rehearsal determines whether " +
                      "the actual Revit references construct the width and wall chains."
                    : "Inspect omitted and refused_groups, then use intent_dimension with " +
                      "opening_left and opening_right on the affected opening.",
                ["note"] = "This planner made no model changes. Family-reference positions are estimates; " +
                    "only horizun_annotate rehearsal and post-commit reread verify dimensions."
            };
            return CommandResult.Ok(result);
        }

        private static void AddOpeningChain(List<AutoDimensionCandidate> candidates, string role,
            string axis, string side, double offset, Document doc, View view, JObject request,
            double fromFeet, HashSet<string> existing, JArray actions, JArray chains, JArray refused)
        {
            List<AutoDimensionCandidate> ordered; string code;
            string error = AutoDimensionRules.OrderAlongAxis(candidates, axis, out ordered, out code);
            if (error != null)
            {
                refused.Add(new JObject { ["role"] = role, ["code"] = code, ["reason"] = error });
                return;
            }
            var reps = ordered.Select(c => c.StableRepresentation).ToList();
            if (AutoDimensionRules.IsDuplicate(reps, existing)) return;
            var chain = new AutoDimensionChain { GroupKey = role };
            AutoDimensionRules.PlaceLine(ordered, axis, side, offset, chain);
            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;
            AutoDimensionRules.KeepInsideCrop(chain, CropRectInViewFrame(view, origin, right, up), offset);
            XYZ start = origin.Add(right.Multiply(chain.StartX)).Add(up.Multiply(chain.StartY));
            XYZ end = origin.Add(right.Multiply(chain.EndX)).Add(up.Multiply(chain.EndY));
            var action = new JObject { ["operation"] = "dimension", ["view_id"] = Rid.Value(view.Id),
                ["line_start"] = Point(start, fromFeet), ["line_end"] = Point(end, fromFeet),
                ["references"] = new JArray(reps) };
            if (request["dimension_type_id"] != null)
                action["dimension_type_id"] = request["dimension_type_id"].DeepClone();
            actions.Add(action);
            existing.Add(AutoDimensionRules.ChainIdentityUnordered(reps));
            chains.Add(new JObject { ["role"] = role, ["axis"] = axis, ["side"] = side,
                ["references"] = new JArray(ordered.Select(c => AutoDimensionPlanner.CandidateJson(c, fromFeet))) });
        }

        // auto_tags lives in PlanAnnotationsCommand.AutoTags.cs (explicit ids, view sweep, links, per-category report).

        private static CommandResult PlanDimension(UIApplication app, Document doc, JObject request, string units, double toFeet)
        {
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);
            var referenceTargets=request["reference_targets"] as JArray;
            if(referenceTargets!=null && (request["element_ids"]!=null || referenceTargets.Count<2 || referenceTargets.Count>32 ||
                referenceTargets.Any(t=>!(t is JObject) || t["element_id"]?.Type!=JTokenType.Integer || t["selector"]?.Type!=JTokenType.String)))
                return CommandResult.Fail("reference_targets requires 2..32 {element_id,selector,probe_point?} entries, without element_ids.");
            List<Element> targets = ResolveTargets(doc, referenceTargets==null ? request["element_ids"] as JArray :
                new JArray(referenceTargets.Select(t=>(long)t["element_id"]).Distinct()), 32, out error);
            if (targets == null || (referenceTargets==null && targets.Count < 2))
                return CommandResult.Fail(error ?? "intent_dimension requires at least two distinct element_ids.");
            string selector = (request.Value<string>("selector") ?? "centerline").ToLowerInvariant();
            string axis = (request.Value<string>("axis") ?? "auto").ToLowerInvariant();
            string side = (request.Value<string>("side") ?? "positive").ToLowerInvariant();
            if (axis != "auto" && axis != "horizontal" && axis != "vertical") return CommandResult.Fail("axis must be auto, horizontal or vertical.");
            if (side != "positive" && side != "negative") return CommandResult.Fail("side must be positive or negative.");
            double offset = (request.Value<double?>("offset") ?? 15.0) * toFeet;
            if (offset <= 0) return CommandResult.Fail("offset must be greater than zero.");

            var refsRequest = new JObject
            {
                ["view_id"] = Rid.Value(view.Id), ["element_ids"] = new JArray(targets.Select(e => Rid.Value(e.Id))),
                ["selectors"] = new JArray(selector), ["include_incompatible"] = true,
                ["max_results"] = 500, ["offset"] = 0, ["units"] = units
            };
            if (request["probe_point"] != null) refsRequest["probe_point"] = request["probe_point"].DeepClone();
            if (request["reference_name"] != null) refsRequest["reference_name"] = request["reference_name"].DeepClone();
            CommandResult refsResult;
            if(referenceTargets==null) refsResult=new DimensionReferencesCommand().Execute(app,refsRequest.ToString());
            else
            {
                var combined=new JArray();
                for(int slot=0;slot<referenceTargets.Count;slot++)
                {
                    JObject spec=(JObject)referenceTargets[slot];
                    if(spec.Properties().Any(p=>p.Name!="element_id" && p.Name!="selector" && p.Name!="probe_point" && p.Name!="reference_name"))
                        return CommandResult.Fail("Unknown reference_targets field.");
                    var one=(JObject)refsRequest.DeepClone();one["element_ids"]=new JArray((long)spec["element_id"]);
                    one["selectors"]=new JArray((string)spec["selector"]);one.Remove("probe_point");one.Remove("reference_name");
                    if(spec["probe_point"]!=null) one["probe_point"]=spec["probe_point"].DeepClone();
                    if(spec["reference_name"]!=null) one["reference_name"]=spec["reference_name"].DeepClone();
                    var answer=new DimensionReferencesCommand().Execute(app,one.ToString());
                    var body=answer.Data as JObject;
                    if(!answer.Success || body?.Value<bool?>("truncated")!=false ||
                        body["coverage"]?.Value<int?>("inspected")!=1 || ((JArray)body["coverage"]["unreadable"]).Count!=0)
                        return CommandResult.Fail("Reference target "+slot+" has incomplete coverage: "+answer.Error);
                    foreach(JObject row in (JArray)body["rows"]) { row["request_slot"]=slot;combined.Add(row); }
                }
                refsResult=CommandResult.Ok(new JObject { ["rows"]=combined,["coverage"]=new JObject {
                    ["inspected"]=targets.Count,["unreadable"]=new JArray() },["truncated"]=false });
            }
            if (!refsResult.Success) return CommandResult.Fail("Reference planning failed: " + refsResult.Error);
            JObject data = refsResult.Data as JObject ?? JObject.FromObject(refsResult.Data);
            JObject coverage = data["coverage"] as JObject;
            if (coverage == null || coverage.Value<int>("inspected") != targets.Count || ((JArray)coverage["unreadable"]).Count > 0 || data.Value<bool>("truncated"))
                return CommandResult.Fail("Reference coverage is incomplete or truncated; no dimension action was produced.");

            var selected = new List<DimRef>();
            JArray rows = data["rows"] as JArray ?? new JArray();
            int targetSlot=0;
            foreach (Element target in referenceTargets==null ? targets :
                referenceTargets.Select(t=>targets.Single(e=>Rid.Value(e.Id)==(long)t["element_id"])).ToList())
            {
                long id = Rid.Value(target.Id);
                List<JObject> candidates = rows.OfType<JObject>().Where(r => r.Value<long>("element_id") == id &&
                    r.Value<bool>("compatible_with_dimension") && !r.Value<bool>("ambiguous") &&
                    !string.IsNullOrWhiteSpace(r.Value<string>("stable_representation")) && r["representative_point"] is JArray &&
                    (referenceTargets==null || r.Value<int?>("request_slot")==targetSlot)).ToList();
                if (candidates.Count != 1)
                    return CommandResult.Fail("Element " + id + " produced " + candidates.Count + " unambiguous compatible '" + selector +
                        "' references; exactly one is required. Choose a narrower selector/probe point. Nothing was written.");
                JArray p = (JArray)candidates[0]["representative_point"];
                selected.Add(new DimRef { Id = id, Stable = candidates[0].Value<string>("stable_representation"),
                    Point = new XYZ(p[0].Value<double>() * toFeet, p[1].Value<double>() * toFeet, p[2].Value<double>() * toFeet) });
                targetSlot++;
            }

            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;
            foreach (DimRef r in selected)
            {
                XYZ delta = r.Point.Subtract(origin); r.X = delta.DotProduct(right); r.Y = delta.DotProduct(up);
            }
            double rangeX = selected.Max(r => r.X) - selected.Min(r => r.X);
            double rangeY = selected.Max(r => r.Y) - selected.Min(r => r.Y);
            bool horizontal = axis == "horizontal" || (axis == "auto" && rangeX >= rangeY);
            selected = (horizontal ? selected.OrderBy(r => r.X) : selected.OrderBy(r => r.Y)).ToList();
            double spread = horizontal ? rangeX : rangeY;
            if (spread < 1e-7) return CommandResult.Fail("The selected references have no measurable spread on the chosen axis.");
            for (int i = 1; i < selected.Count; i++)
                if (Math.Abs((horizontal ? selected[i].X : selected[i].Y) - (horizontal ? selected[i - 1].X : selected[i - 1].Y)) < 1e-7)
                    return CommandResult.Fail("Two selected references project to the same position on the dimension axis; intent is ambiguous.");

            double tail = Math.Max(offset * 0.25, 0.01);
            double min = horizontal ? selected.Min(r => r.X) : selected.Min(r => r.Y);
            double max = horizontal ? selected.Max(r => r.X) : selected.Max(r => r.Y);
            double orth = horizontal ? (side == "positive" ? selected.Max(r => r.Y) + offset : selected.Min(r => r.Y) - offset)
                                     : (side == "positive" ? selected.Max(r => r.X) + offset : selected.Min(r => r.X) - offset);
            XYZ start = horizontal ? origin.Add(right.Multiply(min - tail)).Add(up.Multiply(orth))
                                   : origin.Add(right.Multiply(orth)).Add(up.Multiply(min - tail));
            XYZ end = horizontal ? origin.Add(right.Multiply(max + tail)).Add(up.Multiply(orth))
                                 : origin.Add(right.Multiply(orth)).Add(up.Multiply(max + tail));
            var action = new JObject
            {
                ["operation"] = "dimension", ["view_id"] = Rid.Value(view.Id),
                ["line_start"] = Point(start, 1.0 / toFeet), ["line_end"] = Point(end, 1.0 / toFeet),
                ["references"] = new JArray(selected.Select(r => r.Stable))
            };
            if (request["dimension_type_id"] != null) action["dimension_type_id"] = request["dimension_type_id"].DeepClone();
            var actions = new JArray(action);
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "intent_dimension", ["view_id"] = Rid.Value(view.Id), ["selector"] = referenceTargets==null ? selector : "per_target",
                ["axis_resolved"] = horizontal ? "horizontal" : "vertical", ["side"] = side,
                ["coverage_complete"] = true, ["safe_to_execute"] = true,
                ["reference_rows"] = new JArray(selected.Select(r => new JObject { ["element_id"] = r.Id, ["stable_representation"] = r.Stable })),
                ["next_tool"] = "horizun_annotate", ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["note"] = "This planner made no model changes. Activate this view, then run the returned horizun_annotate dry run and spend only its confirmation token."
            });
        }

        private static JObject AnnotateRequest(Document doc, string units, JArray actions) => new JObject
        {
            ["target_document"] = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName,
            ["units"] = units, ["actions"] = actions, ["dry_run"] = true
        };

        // ---------------------------------------------------------------------
        // model_notes (FIELD FEEDBACK 2.4): notes whose TEXT comes from the
        // model, with the source element id travelling in the reply and, by default,
        // as the note's last line ("id 12345"). note_source=layers reads the type's
        // CompoundStructure exterior to interior (function, material, thickness);
        // note_source=params writes type, material, thickness and finish. Placement
        // reuses the auto_tags survey (AnnotationLayout.Survey): the note's estimated
        // paper box is reserved against every measured annotation and every earlier
        // note. Read-only: horizun_annotate rehearses, creates and re-reads the text.
        // ---------------------------------------------------------------------
        private static CommandResult PlanModelNotes(Document doc, JObject request, string units, double toFeet)
        {
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);
            List<Element> targets = ResolveTargets(doc, request["element_ids"] as JArray, 200, out error);
            if (targets == null) return CommandResult.Fail(error);
            long ttId = request.Value<long?>("text_type_id") ?? -1;
            TextNoteType textType = Rid.CanRepresent(ttId) ? doc.GetElement(Rid.Make(ttId)) as TextNoteType : null;
            if (textType == null) return CommandResult.Fail("model_notes requires text_type_id: a TextNoteType of this document.");
            string source = (request.Value<string>("note_source") ?? "layers").ToLowerInvariant();
            if (source != "layers" && source != "params") return CommandResult.Fail("note_source must be layers or params.");
            bool includeId = !(request.Value<bool?>("omit_source_id") ?? false);
            double heightMm = 2.5;
            try { Parameter size = textType.get_Parameter(BuiltInParameter.TEXT_SIZE); if (size != null && size.HasValue) heightMm = size.AsDouble() * 304.8; }
            catch { }
            double clearance = (request.Value<double?>("clearance") ?? 10.0) * toFeet;
            double scale = Math.Max(1, view.Scale);

            HashSet<long> accepted;
            try { accepted = AnnotationVisibility.ReadAccepted(request["accept_unmeasurable"]); }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }
            List<Box2> occupied; JObject coverage;
            try
            {
                AnnotationSurvey survey = AnnotationLayout.Survey(doc, view, null, accepted);
                coverage = survey.Coverage;
                if (!survey.Complete)
                    return CommandResult.FailWithDetail(AnnotationVisibility.RefusalMessage(coverage), coverage);
                occupied = survey.Obstacles.Select(b => new Box2(b.MinX, b.MinY, b.MaxX, b.MaxY)).ToList();
            }
            catch (AnnotationCoverageException ex) { return CommandResult.FailWithDetail(ex.Message, ex.Coverage); }
            catch (Exception ex) { return CommandResult.Fail("Annotation coverage is unknown: " + ex.Message); }

            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;
            var actions = new JArray(); var notes = new JArray(); var skipped = new JArray();
            foreach (Element target in targets)
            {
                long id = Rid.Value(target.Id);
                ElementType type = null;
                try { type = doc.GetElement(target.GetTypeId()) as ElementType; } catch { }
                string typeName = type == null ? target.Name
                    : (string.IsNullOrWhiteSpace(type.FamilyName) ? type.Name : type.FamilyName + ": " + type.Name);
                CompoundStructure cs = null;
                try { cs = (type as HostObjAttributes)?.GetCompoundStructure(); } catch { cs = null; }
                var layers = new List<NoteLayer>();
                if (cs != null)
                {
                    int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
                    IList<CompoundStructureLayer> raw = cs.GetLayers();
                    for (int i = 0; i < raw.Count; i++)
                        layers.Add(new NoteLayer
                        {
                            Function = raw[i].Function.ToString(),
                            Material = raw[i].MaterialId == ElementId.InvalidElementId ? null : doc.GetElement(raw[i].MaterialId)?.Name,
                            ThicknessMm = raw[i].Width * 304.8,
                            IsCore = first >= 0 && i >= first && i <= last
                        });
                }
                string text;
                if (source == "layers")
                {
                    if (layers.Count == 0)
                    {
                        skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "no CompoundStructure layers: not a layered wall/floor/roof/ceiling type; use note_source=params" });
                        continue;
                    }
                    text = AnnotationNoteRules.LayersText(typeName, layers, id, includeId);
                }
                else
                {
                    string material = layers.Where(l => l.IsCore && l.Material != null).Select(l => l.Material).FirstOrDefault()
                        ?? ParamText(target, type, BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)
                        ?? ParamText(target, type, BuiltInParameter.MATERIAL_ID_PARAM);
                    double? thickness = cs != null ? cs.GetWidth() * 304.8
                        : ParamFeet(target, type, BuiltInParameter.WALL_ATTR_WIDTH_PARAM, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM,
                                    BuiltInParameter.ROOF_ATTR_THICKNESS_PARAM, BuiltInParameter.CEILING_THICKNESS) * 304.8;
                    text = AnnotationNoteRules.ParamsText(typeName, material, thickness, AnnotationNoteRules.FinishFromLayers(layers), id, includeId);
                }

                BoundingBoxXYZ bb = null;
                try { bb = target.get_BoundingBox(view); } catch { bb = null; }
                if (bb == null) { skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "no bounding box in the requested view" }); continue; }
                XYZ center = bb.Min.Add(bb.Max).Multiply(0.5);
                double wMm, hMm; AnnotationNoteRules.EstimateExtentMm(text, heightMm, out wMm, out hMm);
                double w = wMm / 304.8 * scale, h = hMm / 304.8 * scale;
                double step = Math.Max(clearance, heightMm / 304.8 * scale);
                XYZ chosen = null;
                for (int ring = 1; ring <= 60 && chosen == null; ring++)
                    foreach (int[] cell in Ring(ring))
                    {
                        XYZ candidate = center.Add(right.Multiply(cell[0] * step)).Add(up.Multiply(cell[1] * step));
                        double x = candidate.Subtract(origin).DotProduct(right), y = candidate.Subtract(origin).DotProduct(up);
                        var box = new Box2(x, y - h, x + w, y);
                        double pad = clearance * 0.5;
                        if (occupied.All(b => b.MaxX + pad < box.MinX || b.MinX - pad > box.MaxX || b.MaxY + pad < box.MinY || b.MinY - pad > box.MaxY))
                        { chosen = candidate; occupied.Add(box); break; }
                    }
                if (chosen == null) { skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "no collision-free note position in 60 bounded search rings" }); continue; }
                notes.Add(new JObject
                {
                    ["action_index"] = actions.Count, ["source_element_id"] = id, ["note_source"] = source, ["type"] = typeName,
                    ["layers"] = new JArray(layers.Select(l => new JObject { ["function"] = l.Function, ["material"] = l.Material,
                        ["thickness_mm"] = Math.Round(l.ThicknessMm, 2), ["core"] = l.IsCore })),
                    ["text"] = text
                });
                actions.Add(new JObject
                {
                    ["operation"] = "text", ["view_id"] = Rid.Value(view.Id), ["point"] = Point(chosen, 1.0 / toFeet),
                    ["text"] = text, ["text_type_id"] = ttId,
                    ["horizontal_alignment"] = "left", ["vertical_alignment"] = "top"
                });
            }
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "model_notes", ["view_id"] = Rid.Value(view.Id), ["note_source"] = source,
                ["requested"] = targets.Count, ["planned"] = actions.Count, ["skipped"] = skipped, ["notes"] = notes,
                ["annotation_coverage"] = coverage, ["clearance_scope"] = coverage.Value<string>("clearance_scope"),
                ["safe_to_execute"] = actions.Count > 0,
                ["next_tool"] = "horizun_annotate", ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["note"] = "This planner made no model changes. Layers are listed exterior to interior (top to bottom for " +
                           "floors/roofs/ceilings) as Revit's CompoundStructure orders them. Note boxes are ESTIMATED from the " +
                           "text height for clearance; horizun_annotate rehearses and re-reads each note. No leader is drawn: " +
                           "the note sits beside its element and names it by id."
            });
        }

        private static string ParamText(Element e, Element type, BuiltInParameter bip)
        {
            foreach (Element owner in new[] { e, type })
            {
                try
                {
                    Parameter p = owner?.get_Parameter(bip);
                    if (p == null || !p.HasValue) continue;
                    if (p.StorageType == StorageType.ElementId)
                    {
                        Element m = owner.Document.GetElement(p.AsElementId());
                        if (m != null) return m.Name;
                    }
                    else { string s = p.AsValueString() ?? p.AsString(); if (!string.IsNullOrWhiteSpace(s)) return s; }
                }
                catch { }
            }
            return null;
        }

        private static double? ParamFeet(Element e, Element type, params BuiltInParameter[] bips)
        {
            foreach (BuiltInParameter bip in bips)
                foreach (Element owner in new[] { e, type })
                {
                    try
                    {
                        Parameter p = owner?.get_Parameter(bip);
                        if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble();
                    }
                    catch { }
                }
            return null;
        }

        private static View ResolveView(Document doc, JObject request, out string error)
        {
            error = null; long id = request.Value<long?>("view_id") ?? -1;
            View view = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as View : null;
            if (view == null || view.IsTemplate || view is ViewSheet || view is ViewSchedule)
            { error = "view_id must identify a non-template graphical model view."; return null; }
            return view;
        }

        private static List<Element> ResolveTargets(Document doc, JArray ids, int max, out string error)
        {
            error = null;
            if (ids == null || ids.Count < 1 || ids.Count > max || ids.Any(t => t.Type != JTokenType.Integer))
            { error = "element_ids must contain 1.." + max + " integer ids."; return null; }
            var result = new List<Element>(); var seen = new HashSet<long>();
            foreach (JToken token in ids)
            {
                long id = token.Value<long>();
                if (!Rid.CanRepresent(id) || !seen.Add(id)) { error = "element_ids contains an invalid or duplicate id: " + id + "."; return null; }
                Element element = doc.GetElement(Rid.Make(id));
                if (element == null) { error = "element_id " + id + " does not exist."; return null; }
                result.Add(element);
            }
            return result;
        }

        private static IEnumerable<int[]> Ring(int r)
        {
            for (int x = -r; x <= r; x++) yield return new[] { x, r };
            for (int y = r - 1; y >= -r; y--) yield return new[] { r, y };
            for (int x = r - 1; x >= -r; x--) yield return new[] { x, -r };
            for (int y = -r + 1; y < r; y++) yield return new[] { -r, y };
        }


        /// <summary>The active crop as [minX, minY, maxX, maxY] in the view's right/up frame
        /// about view.Origin (the frame the auto-dimension rules use), or null.</summary>
        private static double[] CropRectInViewFrame(View view, XYZ origin, XYZ right, XYZ up)
        {
            try
            {
                if (view == null || !view.CropBoxActive) return null;
                BoundingBoxXYZ box = view.CropBox;
                if (box == null) return null;
                Transform t = box.Transform;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (double x in new[] { box.Min.X, box.Max.X })
                    foreach (double y in new[] { box.Min.Y, box.Max.Y })
                    {
                        XYZ d = t.OfPoint(new XYZ(x, y, box.Min.Z)).Subtract(origin);
                        double u = d.DotProduct(right), v = d.DotProduct(up);
                        minX = Math.Min(minX, u); maxX = Math.Max(maxX, u); minY = Math.Min(minY, v); maxY = Math.Max(maxY, v);
                    }
                return new[] { minX, minY, maxX, maxY };
            }
            catch { return null; }
        }

        private static JArray Point(XYZ p, double scale) => new JArray(
            Math.Round(p.X * scale, 9), Math.Round(p.Y * scale, 9), Math.Round(p.Z * scale, 9));

        private sealed class Box2
        {
            public readonly double MinX, MinY, MaxX, MaxY;
            public Box2(double minX, double minY, double maxX, double maxY) { MinX=minX; MinY=minY; MaxX=maxX; MaxY=maxY; }
            public bool Contains(double x, double y, double pad) => x >= MinX-pad && x <= MaxX+pad && y >= MinY-pad && y <= MaxY+pad;
        }
        private sealed class DimRef { public long Id; public string Stable; public XYZ Point; public double X, Y; }
    }
}
