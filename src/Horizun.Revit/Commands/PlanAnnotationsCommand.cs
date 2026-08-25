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
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class PlanAnnotationsCommand : ICommand
    {
        public string Name => "horizun_plan_annotations";
        public string Description =>
            "Plan collision-aware tags or a linear dimension by intent and return a ready horizun_annotate dry-run request. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double toFeet;
            if (!DimensionPlanRules.UnitScale(units, out toFeet)) return CommandResult.Fail("units must be mm, m or feet.");
            string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();
            if (operation == "auto_tags") return PlanTags(doc, request, units, toFeet);
            if (operation == "intent_dimension") return PlanDimension(app, doc, request, units, toFeet);
            return CommandResult.Fail("operation must be auto_tags or intent_dimension.");
        }

        private static CommandResult PlanTags(Document doc, JObject request, string units, double toFeet)
        {
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);
            List<Element> targets = ResolveTargets(doc, request["element_ids"] as JArray, 500, out error);
            if (targets == null) return CommandResult.Fail(error);

            double clearance = (request.Value<double?>("clearance") ?? 10.0) * toFeet;
            if (clearance < 0) return CommandResult.Fail("clearance must be zero or greater.");
            bool skipExisting = request.Value<bool?>("skip_existing") ?? true;
            bool addLeader = request.Value<bool?>("add_leader") ?? true;
            long? tagType = request.Value<long?>("tag_type_id");
            if (tagType.HasValue && (!Rid.CanRepresent(tagType.Value) || !(doc.GetElement(Rid.Make(tagType.Value)) is ElementType)))
                return CommandResult.Fail("tag_type_id must identify an ElementType in the active document.");

            List<Box2> occupied = AnnotationBoxes(doc, view);
            var actions = new JArray();
            var skipped = new JArray();
            var unreadable = new JArray();
            var existing = ExistingTargets(doc, view.Id);
            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;
            int ordinal = 0;
            foreach (Element target in targets.OrderBy(e => Rid.Value(e.Id)))
            {
                long id = Rid.Value(target.Id);
                if (skipExisting && existing.Contains(id))
                {
                    skipped.Add(new JObject { ["element_id"] = id, ["reason"] = "already_tagged_in_view" });
                    continue;
                }
                BoundingBoxXYZ bb = null;
                try { bb = target.get_BoundingBox(view); } catch { bb = null; }
                if (bb == null)
                {
                    unreadable.Add(new JObject { ["element_id"] = id, ["reason"] = "no bounding box in the requested view" });
                    continue;
                }
                XYZ center = bb.Min.Add(bb.Max).Multiply(0.5);
                XYZ chosen = null;
                // Deterministic square spiral.  Candidate 0 is above the target; later
                // candidates walk right/up/left/down in view-plane coordinates.
                // Twelve 10 mm rings were only 120 mm: a perfectly ordinary
                // dimension string or tag body can occupy more than that. Search a
                // bounded 1.2 m at the default clearance (120 rings), still finite
                // and deterministic; the returned point remains subject to the real
                // annotate rehearsal rather than being called constructible here.
                for (int ring = 1; ring <= 120 && chosen == null; ring++)
                {
                    foreach (int[] cell in Ring(ring))
                    {
                        XYZ candidate = center.Add(right.Multiply(cell[0] * clearance))
                                              .Add(up.Multiply(cell[1] * clearance));
                        double x = candidate.Subtract(origin).DotProduct(right);
                        double y = candidate.Subtract(origin).DotProduct(up);
                        if (occupied.All(b => !b.Contains(x, y, clearance * 0.5)))
                        {
                            chosen = candidate;
                            occupied.Add(new Box2(x, y, x, y));
                            break;
                        }
                    }
                }
                if (chosen == null)
                {
                    unreadable.Add(new JObject { ["element_id"] = id, ["reason"] = "no collision-free tag point found in 120 bounded search rings" });
                    continue;
                }
                var action = new JObject
                {
                    ["operation"] = "tag", ["view_id"] = Rid.Value(view.Id), ["element_id"] = id,
                    ["point"] = Point(chosen, 1.0 / toFeet), ["add_leader"] = addLeader,
                    ["tag_mode"] = request.Value<string>("tag_mode") ?? "by_category",
                    ["orientation"] = request.Value<string>("orientation") ?? "horizontal"
                };
                if (tagType.HasValue) action["tag_type_id"] = tagType.Value;
                actions.Add(action); ordinal++;
            }

            bool complete = unreadable.Count == 0;
            var next = AnnotateRequest(doc, units, actions);
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "auto_tags", ["view_id"] = Rid.Value(view.Id),
                ["requested"] = targets.Count, ["planned"] = ordinal, ["skipped"] = skipped,
                ["unreadable"] = unreadable, ["coverage_complete"] = complete,
                ["safe_to_execute"] = complete && actions.Count > 0,
                ["next_tool"] = "horizun_annotate", ["next_arguments"] = next,
                ["note"] = "This planner made no model changes. Run the returned horizun_annotate dry run; only its rehearsal proves the selected tag family/type can tag each target."
            });
        }

        private static CommandResult PlanDimension(UIApplication app, Document doc, JObject request, string units, double toFeet)
        {
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);
            List<Element> targets = ResolveTargets(doc, request["element_ids"] as JArray, 32, out error);
            if (targets == null || targets.Count < 2)
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
            CommandResult refsResult = new DimensionReferencesCommand().Execute(app, refsRequest.ToString());
            if (!refsResult.Success) return CommandResult.Fail("Reference planning failed: " + refsResult.Error);
            JObject data = refsResult.Data as JObject ?? JObject.FromObject(refsResult.Data);
            JObject coverage = data["coverage"] as JObject;
            if (coverage == null || coverage.Value<int>("inspected") != targets.Count || ((JArray)coverage["unreadable"]).Count > 0 || data.Value<bool>("truncated"))
                return CommandResult.Fail("Reference coverage is incomplete or truncated; no dimension action was produced.");

            var selected = new List<DimRef>();
            JArray rows = data["rows"] as JArray ?? new JArray();
            foreach (Element target in targets)
            {
                long id = Rid.Value(target.Id);
                List<JObject> candidates = rows.OfType<JObject>().Where(r => r.Value<long>("element_id") == id &&
                    r.Value<bool>("compatible_with_dimension") && !r.Value<bool>("ambiguous") &&
                    !string.IsNullOrWhiteSpace(r.Value<string>("stable_representation")) && r["representative_point"] is JArray).ToList();
                if (candidates.Count != 1)
                    return CommandResult.Fail("Element " + id + " produced " + candidates.Count + " unambiguous compatible '" + selector +
                        "' references; exactly one is required. Choose a narrower selector/probe point. Nothing was written.");
                JArray p = (JArray)candidates[0]["representative_point"];
                selected.Add(new DimRef { Id = id, Stable = candidates[0].Value<string>("stable_representation"),
                    Point = new XYZ(p[0].Value<double>() * toFeet, p[1].Value<double>() * toFeet, p[2].Value<double>() * toFeet) });
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
                ["operation"] = "intent_dimension", ["view_id"] = Rid.Value(view.Id), ["selector"] = selector,
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

        private static HashSet<long> ExistingTargets(Document doc, ElementId viewId)
        {
            var result = new HashSet<long>();
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try
                {
                    if (tag.OwnerViewId != viewId) continue;
                    foreach (ElementId id in tag.GetTaggedLocalElementIds()) result.Add(Rid.Value(id));
                }
                catch { }
            }
            return result;
        }

        private static List<Box2> AnnotationBoxes(Document doc, View view)
        {
            var boxes = new List<Box2>(); XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;
            IEnumerable<Element> elements = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .Where(e => e is IndependentTag || e is TextNote || e is Dimension);
            foreach (Element e in elements)
            {
                try
                {
                    if (e.OwnerViewId != view.Id) continue;
                    BoundingBoxXYZ bb = e.get_BoundingBox(view); if (bb == null) continue;
                    var corners = new[] { new XYZ(bb.Min.X,bb.Min.Y,bb.Min.Z), new XYZ(bb.Min.X,bb.Max.Y,bb.Min.Z),
                        new XYZ(bb.Max.X,bb.Min.Y,bb.Max.Z), new XYZ(bb.Max.X,bb.Max.Y,bb.Max.Z) };
                    double[] xs = corners.Select(p => p.Subtract(origin).DotProduct(right)).ToArray();
                    double[] ys = corners.Select(p => p.Subtract(origin).DotProduct(up)).ToArray();
                    boxes.Add(new Box2(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
                }
                catch { }
            }
            return boxes;
        }

        private static IEnumerable<int[]> Ring(int r)
        {
            for (int x = -r; x <= r; x++) yield return new[] { x, r };
            for (int y = r - 1; y >= -r; y--) yield return new[] { r, y };
            for (int x = r - 1; x >= -r; x--) yield return new[] { x, -r };
            for (int y = -r + 1; y < r; y++) yield return new[] { -r, y };
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
