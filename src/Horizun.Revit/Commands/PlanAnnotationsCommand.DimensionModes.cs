// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_plan_annotations operation=dimension_mode (item 2.3): high-level
// dimensioning modes so an agent does not have to guess faces. Every mode that an
// existing operation already covers DELEGATES to it (openings, room, grids,
// levels, intent_dimension); wall_thickness, chain_along_line and spot_elevation
// are planned here. Every produced linear action carries expected_value computed
// from the GEOMETRY of its references (never from Revit's Dimension.Value), so
// horizun_annotate verifies it; where the geometry is not measurable the action
// is left without one and the reason is listed - never invented.
//
// Read-only, like the rest of the planner: horizun_annotate stays the single
// rehearse / confirm / verify path.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class PlanAnnotationsCommand
    {
        private CommandResult PlanDimensionMode(UIApplication app, Document doc, JObject request, string units, double toFeet)
        {
            string mode = (request.Value<string>("mode") ?? "").ToLowerInvariant();
            var ids = request["element_ids"] as JArray;
            string invalid = DimensionModeRules.Validate(mode, ids != null, ids?.Count ?? 0,
                request["line_start"] is JArray && request["line_end"] is JArray, request["room_id"] != null);
            if (invalid != null) return CommandResult.Fail(invalid + " Nothing was planned.");
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);

            CommandResult planned;
            switch (mode)
            {
                case DimensionModeRules.WallThickness: planned = PlanWallThickness(app, doc, view, request, units, toFeet); break;
                case DimensionModeRules.ChainAlongLine: planned = PlanChainAlongLine(doc, view, request, units, toFeet); break;
                case DimensionModeRules.SpotElevation: planned = PlanSpotElevations(app, doc, view, request, units, toFeet); break;
                case DimensionModeRules.OpeningHeight: planned = PlanOpeningHeights(app, doc, request, units); break;
                default:
                {
                    var args = (JObject)request.DeepClone();
                    args.Remove("mode"); args.Remove("line_start"); args.Remove("line_end");
                    args["operation"] = DimensionModeRules.DelegateOperation(mode);
                    planned = Execute(app, args.ToString());
                    break;
                }
            }
            if (!planned.Success) return planned;
            var data = planned.Data as JObject ?? JObject.FromObject(planned.Data);
            JArray unavailable = AttachGeometryExpectations(doc, data, toFeet);
            data["mode"] = mode;
            data["delegated_to"] = DimensionModeRules.DelegateOperation(mode) ?? (JToken)JValue.CreateNull();
            data["expected_value_source"] = "reference geometry (plane/line positions along the dimension line), not Dimension.Value";
            data["expected_value_unavailable"] = unavailable;
            return CommandResult.Ok(data);
        }

        /// <summary>
        /// Every linear "dimension" action without an expected_value gets one computed from
        /// its references' geometry, with a 1 mm tolerance. Returns the actions where that was
        /// not possible, with the reason.
        /// </summary>
        private static JArray AttachGeometryExpectations(Document doc, JObject data, double toFeet)
        {
            var unavailable = new JArray();
            var actions = data["next_arguments"]?["actions"] as JArray;
            if (actions == null) return unavailable;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i] as JObject;
                if (a == null || a.Value<string>("operation") != "dimension" || a["expected_value"] != null) continue;
                string reason;
                double? total = GeometryTotal(doc, a, toFeet, out reason);
                if (!total.HasValue) { unavailable.Add(new JObject { ["action_index"] = i, ["reason"] = reason }); continue; }
                a["expected_value"] = Math.Round(total.Value / toFeet, 9);
                a["expected_tolerance"] = DimensionGeometryRules.DefaultToleranceFeet / toFeet;
            }
            return unavailable;
        }

        private static double? GeometryTotal(Document doc, JObject action, double toFeet, out string reason)
        {
            reason = null;
            try
            {
                var s = (JArray)action["line_start"]; var e = (JArray)action["line_end"];
                double[] dir = { (e[0].Value<double>() - s[0].Value<double>()) * toFeet,
                                 (e[1].Value<double>() - s[1].Value<double>()) * toFeet,
                                 (e[2].Value<double>() - s[2].Value<double>()) * toFeet };
                var geoms = new List<RefGeometry>();
                foreach (string stable in ((JArray)action["references"]).Values<string>())
                    geoms.Add(DimensionGeometryProbe.Read(doc, Reference.ParseFromStableRepresentation(doc, stable)));
                return DimensionGeometryRules.ExpectedTotal(geoms, dir, out reason);
            }
            catch (Exception ex) { reason = DimensionGeometryRules.CodeUnreadable + ": " + ex.Message; return null; }
        }

        // ---- wall_thickness ----------------------------------------------------

        private static CommandResult PlanWallThickness(UIApplication app, Document doc, View view, JObject request,
                                                       string units, double toFeet)
        {
            string error;
            List<Element> walls = ResolveTargets(doc, request["element_ids"] as JArray, 50, out error);
            if (walls == null) return CommandResult.Fail(error);
            var nonWall = walls.Where(w => !(w is Wall)).Select(w => Rid.Value(w.Id)).ToList();
            if (nonWall.Count > 0)
                return CommandResult.Fail("wall_thickness takes walls only; not walls: " + string.Join(", ", nonWall) + ". Nothing was planned.");
            double offset = Math.Max((request.Value<double?>("offset") ?? 15.0) * toFeet, 0.01);
            double tail = Math.Max(offset * 0.25, 0.01);
            var actions = new JArray(); var omitted = new JArray(); var rows = new JArray();
            foreach (Wall wall in walls.Cast<Wall>())
            {
                long id = Rid.Value(wall.Id);
                var refsRequest = new JObject
                {
                    ["view_id"] = Rid.Value(view.Id), ["element_ids"] = new JArray(id),
                    ["selectors"] = new JArray("exterior_face", "interior_face", "core_exterior_face", "core_interior_face"),
                    ["include_incompatible"] = true, ["max_results"] = 500, ["offset"] = 0, ["units"] = "feet"
                };
                CommandResult refs = new DimensionReferencesCommand().Execute(app, refsRequest.ToString());
                var body = refs.Data as JObject;
                if (!refs.Success || body == null) return CommandResult.Fail("Reference discovery failed for wall " + id + ": " + refs.Error);
                JObject Pick(string selector, out string why)
                {
                    why = null;
                    var all = ((JArray)body["rows"]).OfType<JObject>().Where(r => r.Value<string>("selector") == selector).ToList();
                    var good = all.Where(r => r.Value<bool>("compatible_with_dimension") && !r.Value<bool>("ambiguous") &&
                                              !string.IsNullOrEmpty(r.Value<string>("stable_representation"))).ToList();
                    if (good.Count == 1) return good[0];
                    var negative = all.FirstOrDefault(r => r["incompatibility_reason"] is JObject);
                    why = negative != null ? negative["incompatibility_reason"].Value<string>("code")
                        : good.Count > 1 ? "ambiguous_" + selector : "no_" + selector;
                    return null;
                }
                string w1, w2, w3, w4;
                JObject ext = Pick("exterior_face", out w1), inn = Pick("interior_face", out w2);
                JObject cext = Pick("core_exterior_face", out w3), cint = Pick("core_interior_face", out w4);
                if (ext == null || inn == null)
                {
                    omitted.Add(new JObject { ["element_id"] = id, ["role"] = "total", ["code"] = w1 ?? w2 });
                    continue;
                }
                XYZ n = Vec(ext["geometry"]["normal"]);
                XYZ eo = Vec(ext["geometry"]["origin"]), io = Vec(inn["geometry"]["origin"]);
                Curve loc = (wall.Location as LocationCurve)?.Curve;
                XYZ mid = loc == null ? eo : loc.Evaluate(0.5, true);
                XYZ along = loc is Line ln ? ln.Direction.Normalize() : n.CrossProduct(XYZ.BasisZ).Normalize();

                void Add(string role, JObject a, JObject b, double shift)
                {
                    XYZ ao = Vec(a["geometry"]["origin"]), bo = Vec(b["geometry"]["origin"]);
                    XYZ basePt = mid.Add(along.Multiply(shift));
                    double ta = ao.Subtract(basePt).DotProduct(n), tb = bo.Subtract(basePt).DotProduct(n);
                    double lo = Math.Min(ta, tb) - tail, hi = Math.Max(ta, tb) + tail;
                    var action = new JObject
                    {
                        ["operation"] = "dimension", ["view_id"] = Rid.Value(view.Id),
                        ["line_start"] = Point(basePt.Add(n.Multiply(lo)), 1.0 / toFeet),
                        ["line_end"] = Point(basePt.Add(n.Multiply(hi)), 1.0 / toFeet),
                        ["references"] = new JArray(a.Value<string>("stable_representation"), b.Value<string>("stable_representation"))
                    };
                    if (request["dimension_type_id"] != null) action["dimension_type_id"] = request["dimension_type_id"].DeepClone();
                    actions.Add(action);
                    rows.Add(new JObject { ["element_id"] = id, ["role"] = role, ["action_index"] = actions.Count - 1 });
                }
                Add("total", ext, inn, 0);
                if (cext == null || cint == null)
                    omitted.Add(new JObject { ["element_id"] = id, ["role"] = "core", ["code"] = w3 ?? w4 });
                else if (cext.Value<string>("stable_representation") == ext.Value<string>("stable_representation") &&
                         cint.Value<string>("stable_representation") == inn.Value<string>("stable_representation"))
                    omitted.Add(new JObject { ["element_id"] = id, ["role"] = "core", ["code"] = "core_equals_total" });
                else Add("core", cext, cint, offset);
            }
            bool complete = omitted.Count == 0 && actions.Count > 0;
            return CommandResult.Ok(new JObject
            {
                ["operation"] = DimensionModeRules.Operation, ["view_id"] = Rid.Value(view.Id),
                ["coverage"] = actions.Count == 0 ? "none" : complete ? "complete" : "partial",
                ["coverage_complete"] = complete, ["safe_to_execute"] = actions.Count > 0,
                ["planned"] = rows, ["omitted"] = omitted,
                ["next_tool"] = "horizun_annotate", ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["note"] = "Core faces exist only where the view draws wall layers (cut plan/section, medium or fine detail); " +
                           "an omitted core names the code. Run next_arguments as a horizun_annotate dry run."
            });
        }

        private static XYZ Vec(JToken t)
        {
            var a = (JArray)t;
            return new XYZ(a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>());
        }

        // ---- opening_height: sill to head, through intent_dimension -------------

        private CommandResult PlanOpeningHeights(UIApplication app, Document doc, JObject request, string units)
        {
            var actions = new JArray(); var rows = new JArray();
            foreach (long id in ((JArray)request["element_ids"]).Values<long>())
            {
                var args = new JObject
                {
                    ["operation"] = "intent_dimension", ["view_id"] = request["view_id"].DeepClone(), ["units"] = units,
                    ["axis"] = "vertical", ["side"] = request["side"]?.DeepClone() ?? "positive",
                    ["offset"] = request["offset"]?.DeepClone() ?? 15, ["distance_space"] = "model",
                    ["reference_targets"] = new JArray(
                        new JObject { ["element_id"] = id, ["selector"] = "opening_sill" },
                        new JObject { ["element_id"] = id, ["selector"] = "opening_head" })
                };
                if (request["dimension_type_id"] != null) args["dimension_type_id"] = request["dimension_type_id"].DeepClone();
                CommandResult one = Execute(app, args.ToString());
                if (!one.Success) return CommandResult.Fail("opening_height for element " + id + ": " + one.Error);
                var body = (JObject)one.Data;
                foreach (JObject a in (JArray)body["next_arguments"]["actions"]) actions.Add(a.DeepClone());
                rows.Add(new JObject { ["element_id"] = id, ["action_index"] = actions.Count - 1 });
            }
            return CommandResult.Ok(new JObject
            {
                ["operation"] = DimensionModeRules.Operation, ["view_id"] = request["view_id"].DeepClone(),
                ["coverage"] = "complete", ["coverage_complete"] = true, ["safe_to_execute"] = true, ["planned"] = rows,
                ["next_tool"] = "horizun_annotate", ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["note"] = "Plan in an elevation or section where sill and head are visible; run next_arguments as a dry run."
            });
        }

        // ---- spot_elevation: top face of each element -----------------------------

        private static CommandResult PlanSpotElevations(UIApplication app, Document doc, View view, JObject request,
                                                        string units, double toFeet)
        {
            var actions = new JArray(); var omitted = new JArray(); var rows = new JArray();
            foreach (long id in ((JArray)request["element_ids"]).Values<long>())
            {
                var refsRequest = new JObject
                {
                    ["view_id"] = Rid.Value(view.Id), ["element_ids"] = new JArray(id), ["selectors"] = new JArray("top_face"),
                    ["include_incompatible"] = true, ["max_results"] = 500, ["offset"] = 0, ["units"] = units
                };
                CommandResult refs = new DimensionReferencesCommand().Execute(app, refsRequest.ToString());
                var body = refs.Data as JObject;
                var good = body == null ? new List<JObject>() : ((JArray)body["rows"]).OfType<JObject>()
                    .Where(r => r.Value<bool>("compatible_with_dimension") && !r.Value<bool>("ambiguous") &&
                                !string.IsNullOrEmpty(r.Value<string>("stable_representation")) && r["representative_point"] is JArray).ToList();
                if (!refs.Success || good.Count != 1)
                {
                    omitted.Add(new JObject { ["element_id"] = id, ["code"] = refs.Success ? "top_face_count_" + good.Count : "reference_discovery_failed" });
                    continue;
                }
                var point = (JArray)good[0]["representative_point"];
                actions.Add(new JObject
                {
                    ["operation"] = "spot_elevation", ["view_id"] = Rid.Value(view.Id),
                    ["reference"] = good[0].Value<string>("stable_representation"), ["point"] = point.DeepClone(),
                    ["leader"] = request.Value<bool?>("add_leader") ?? true
                });
                rows.Add(new JObject { ["element_id"] = id, ["action_index"] = actions.Count - 1,
                                       ["geometry_elevation"] = point[2].DeepClone() });
            }
            bool complete = omitted.Count == 0 && actions.Count > 0;
            return CommandResult.Ok(new JObject
            {
                ["operation"] = DimensionModeRules.Operation, ["view_id"] = Rid.Value(view.Id),
                ["coverage"] = actions.Count == 0 ? "none" : complete ? "complete" : "partial",
                ["coverage_complete"] = complete, ["safe_to_execute"] = actions.Count > 0,
                ["planned"] = rows, ["omitted"] = omitted,
                ["next_tool"] = "horizun_annotate", ["next_arguments"] = AnnotateRequest(doc, units, actions),
                ["note"] = "A spot has no Dimension.Value, so no expected_value travels; geometry_elevation is the face's " +
                           "model Z (internal origin, in units) for the reviewer to compare with the displayed elevation."
            });
        }

        // ---- chain_along_line ----------------------------------------------------

        private static CommandResult PlanChainAlongLine(Document doc, View view, JObject request, string units, double toFeet)
        {
            string error;
            List<Element> targets = ResolveTargets(doc, request["element_ids"] as JArray, 200, out error);
            if (targets == null) return CommandResult.Fail(error);
            XYZ a, b;
            try { a = Pt(request["line_start"], toFeet); b = Pt(request["line_end"], toFeet); }
            catch (Exception ex) { return CommandResult.Fail("line_start/line_end must be [x, y, z]: " + ex.Message); }
            if (a.DistanceTo(b) < 1e-6) return CommandResult.Fail("line_start and line_end must differ.");
            XYZ dir = b.Subtract(a).Normalize();
            double[] da = { a.X, a.Y, a.Z }, db = { b.X, b.Y, b.Z }, dd = { dir.X, dir.Y, dir.Z };

            // A plan's faces are tested at the CUT height, where the view draws them; the
            // dimension line itself stays where the caller put it.
            double? cutZ = null;
            if (view is ViewPlan plan && plan.GenLevel != null)
            {
                try { cutZ = plan.GenLevel.ProjectElevation + plan.GetViewRange().GetOffset(PlanViewPlane.CutPlane); }
                catch { cutZ = null; }
            }

            var hits = new List<ChainHit>();
            var unreadable = new JArray();
            foreach (Element element in targets)
            {
                long id = Rid.Value(element.Id);
                try
                {
                    if (element is Grid grid)
                    {
                        var gl = grid.Curve as Line;
                        if (gl == null) { unreadable.Add(new JObject { ["element_id"] = id, ["code"] = "curved_grid" }); continue; }
                        XYZ gn = gl.Direction.CrossProduct(XYZ.BasisZ);
                        if (gn.GetLength() < 1e-9) continue;
                        gn = gn.Normalize();
                        double? tg = DimensionGeometryRules.PlaneCrossing(da, db, A3(gl.Origin), A3(gn));
                        if (!tg.HasValue) continue;
                        XYZ at = a.Add(b.Subtract(a).Multiply(tg.Value));
                        XYZ onGrid = gl.Project(new XYZ(at.X, at.Y, gl.Origin.Z))?.XYZPoint;
                        if (onGrid == null || new XYZ(onGrid.X, onGrid.Y, 0).DistanceTo(new XYZ(at.X, at.Y, 0)) > 1e-4) continue;
                        hits.Add(new ChainHit { ElementId = id, T = tg.Value, Coordinate = DimensionGeometryRules.Dot(A3(at), dd),
                                                Stable = new Reference(grid).ConvertToStableRepresentation(doc) });
                        continue;
                    }
                    GeometryElement ge = element.get_Geometry(new Options { ComputeReferences = true, View = view });
                    var faces = new List<Tuple<PlanarFace, Transform>>();
                    Walk(ge, Transform.Identity, faces, 0);
                    foreach (var f in faces)
                    {
                        PlanarFace pf = f.Item1; Transform tx = f.Item2;
                        if (pf.Reference == null) continue;
                        XYZ o = tx.OfPoint(pf.Origin), n = tx.OfVector(pf.FaceNormal).Normalize();
                        double? t = DimensionGeometryRules.PlaneCrossing(da, db, A3(o), A3(n));
                        if (!t.HasValue) continue;
                        XYZ at = a.Add(b.Subtract(a).Multiply(t.Value));
                        XYZ probe = cutZ.HasValue ? new XYZ(at.X, at.Y, cutZ.Value) : at;
                        XYZ local = tx.Inverse.OfPoint(probe);
                        IntersectionResult onFace = pf.Project(local);
                        if (onFace == null || onFace.Distance > DimensionReferenceRules.EquivalenceToleranceFeet) continue;
                        hits.Add(new ChainHit { ElementId = id, T = t.Value, Coordinate = DimensionGeometryRules.Dot(A3(at), dd),
                                                Stable = pf.Reference.ConvertToStableRepresentation(doc) });
                    }
                }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["element_id"] = id, ["code"] = "geometry_unreadable", ["reason"] = ex.Message });
                }
            }
            List<ChainHit> collapsed;
            List<ChainHit> chain = DimensionGeometryRules.OrderChain(hits, DimensionReferenceRules.EquivalenceToleranceFeet, out collapsed);
            if (chain.Count < 2)
                return CommandResult.FailWithDetail("chain_along_line found " + chain.Count + " face(s) crossed by the line; a " +
                    "dimension needs at least two. Nothing was planned.", new JObject { ["unreadable"] = unreadable });
            if (chain.Count > 32)
                return CommandResult.Fail("chain_along_line crosses " + chain.Count + " faces; a dimension takes at most 32. " +
                                          "Shorten the line or narrow element_ids. Nothing was planned.");
            var segments = new JArray();
            for (int i = 1; i < chain.Count; i++) segments.Add((chain[i].Coordinate - chain[i - 1].Coordinate) / toFeet);
            var action = new JObject
            {
                ["operation"] = "dimension", ["view_id"] = Rid.Value(view.Id),
                ["line_start"] = request["line_start"].DeepClone(), ["line_end"] = request["line_end"].DeepClone(),
                ["references"] = new JArray(chain.Select(h => h.Stable)),
                ["expected_value"] = Math.Round((chain[chain.Count - 1].Coordinate - chain[0].Coordinate) / toFeet, 9),
                ["expected_tolerance"] = DimensionGeometryRules.DefaultToleranceFeet / toFeet
            };
            if (request["dimension_type_id"] != null) action["dimension_type_id"] = request["dimension_type_id"].DeepClone();
            return CommandResult.Ok(new JObject
            {
                ["operation"] = DimensionModeRules.Operation, ["view_id"] = Rid.Value(view.Id),
                ["coverage"] = unreadable.Count == 0 ? "complete" : "partial",
                ["coverage_complete"] = unreadable.Count == 0, ["safe_to_execute"] = true,
                ["crossings"] = new JArray(chain.Select(h => new JObject { ["element_id"] = h.ElementId, ["t"] = Math.Round(h.T, 9),
                                                                           ["stable_representation"] = h.Stable })),
                ["expected_segments"] = segments,
                ["collapsed_coincident"] = new JArray(collapsed.Select(h => new JObject { ["element_id"] = h.ElementId,
                                                                                          ["stable_representation"] = h.Stable })),
                ["unreadable"] = unreadable,
                ["next_tool"] = "horizun_annotate", ["next_arguments"] = AnnotateRequest(doc, units, new JArray(action)),
                ["note"] = "Every planar face whose plane faces along the line (0.5 deg) and is actually crossed within the " +
                           "segment, ordered along it; coincident planes collapse to the lowest element id. Run as a dry run."
            });
        }

        private static double[] A3(XYZ v) => new[] { v.X, v.Y, v.Z };

        private static XYZ Pt(JToken t, double toFeet)
        {
            var a = (JArray)t;
            if (a.Count != 3) throw new ArgumentException("three coordinates required");
            return new XYZ(a[0].Value<double>() * toFeet, a[1].Value<double>() * toFeet, a[2].Value<double>() * toFeet);
        }

        private static void Walk(GeometryElement ge, Transform tx, List<Tuple<PlanarFace, Transform>> faces, int depth)
        {
            if (ge == null || depth > 8) return;
            foreach (GeometryObject go in ge)
            {
                if (go is Solid solid)
                {
                    foreach (Face f in solid.Faces) if (f is PlanarFace pf) faces.Add(Tuple.Create(pf, tx));
                }
                else if (go is GeometryInstance gi) Walk(gi.GetSymbolGeometry(), tx.Multiply(gi.Transform), faces, depth + 1);
                else if (go is GeometryElement nested) Walk(nested, tx, faces, depth + 1);
            }
        }
    }
}
