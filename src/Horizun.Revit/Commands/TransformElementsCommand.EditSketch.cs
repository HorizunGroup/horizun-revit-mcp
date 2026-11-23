// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_transform_elements - edit_sketch: change the boundary of a Floor,
// Ceiling, profiled Wall or Opening WITHOUT recreating it. Replacing one loop, or moving one
// vertex, inside the element's own SketchEditScope keeps its ElementId, its
// UniqueId and what refers to it - hosted instances, openings, tags, schedules,
// parameters - where delete-and-recreate would orphan all of them.
//
// THE LOOP'S OWN CURVES ARE KEPT WHEREVER THEY CAN BE. A vertex move reshapes
// the two lines that meet there; a loop replaced by one with the same number of
// straight edges reshapes every existing line onto the new edge nearest to it
// (Core/SketchEditRules.AlignCyclic), so what those curve elements carry - a
// line's Defines Slope flag, dimensions and constraints drawn to it - stays on
// them. Only a loop whose edge count changes (or that holds arcs) is deleted and
// redrawn, and that path refuses, by name and already in the rehearsal, a loop
// line that defines the slope and any deletion that would take another element
// with it.
//
// Same separate path as realign_wall_sketch, for the same reason: a
// SketchEditScope cannot nest inside the Transaction the other operations
// share, so edit_sketch is sent alone. Its dry_run is a REAL rehearsal of the
// curve edits inside the scope, which is then Cancelled. Revit checks the
// finished sketch as a whole only when the scope COMMITS, so the plan first
// holds the edit to what that check refuses - the edited loop closed and not
// self-crossing (a loop with arcs by its tessellated outline around the moved
// vertex), clear of the other loops, and no loop turned from hole to solid or
// back: a rehearsal must not pass an edit the apply would refuse. Whatever
// Revit still refuses at the apply's commit rolls it all back, and what Revit
// said there is reported.
//
// VERIFIED BY RE-READING, before and after the group is kept. Before: every
// hosted instance, opening and tag that depended on the element still exists,
// and the sketch holds the expected loops (a scope commit Revit rolled back
// without throwing leaves the old ones) - otherwise the whole group is rolled
// back. After: the loops again (Core/SketchEditRules.MatchLoops - cyclic, either
// direction, order-free), the UniqueId, the dependents, and the element's Area
// parameter against the area the new sketch encloses - but only where it
// matched the old sketch before the edit. A sloped or shape-edited floor, or one
// a shaft cuts, reports an area the sketch alone does not predict; there the
// check is named not_applicable, never counted as a pass.
//
// COORDINATES: the caller's points are model [x,y,z] in the request's units, and
// so are loops_before / loops_expected / loops_after, which can be sent back as
// start as they stand; sketch_plane names the frame (origin, x_dir, y_dir,
// normal) the *_mm (u, v) lists are measured in. A refusal that comes after the
// sketch was read carries the current boundary the same way, so a vertex can be
// chosen without first sending a valid edit.
//
// FootPrintRoof is refused by name: the public API exposes no SketchId on it in
// any of 2023-2027 (RevitAPI.xml lists SketchId on Wall, Floor, Ceiling, Opening,
// and on Toposolid from 2024 and PropertyLine from 2027 - not on any roof).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class TransformElementsCommand
    {
        private const string EditSketchOp = "edit_sketch";
        private const double SqFtToM2 = 0.09290304;
        /// <summary>How finely a full circle (one closed curve, no ends) is sampled to be carried as a fixed loop.</summary>
        private const int CircleSamples = 120;

        /// <summary>A profile read in its sketch plane: per loop, the curves in chain order and the tessellated outline.</summary>
        private sealed class SketchRead
        {
            public List<List<SketchPt>> Vertices = new List<List<SketchPt>>();
            public List<List<SketchPt>> Outlines = new List<List<SketchPt>>();
            public List<List<SketchSegment>> Segments = new List<List<SketchSegment>>();
            public List<List<bool>> IsLine = new List<List<bool>>();
            /// <summary>Per step, whether its curve runs against the chain (B to A).</summary>
            public List<List<bool>> Reversed = new List<List<bool>>();
            /// <summary>Per step, the ModelCurve the Profile curve came from (Curve.Reference.ElementId), or null.</summary>
            public List<List<ElementId>> CurveIds = new List<List<ElementId>>();
            /// <summary>Per loop: a full circle, carried and verified but never edited.</summary>
            public List<bool> Fixed = new List<bool>();
            public double AreaM2 => SketchEditRules.NetArea(Outlines.Cast<IList<SketchPt>>().ToList()) / 1e6;
        }

        private static ElementId EditableSketchId(Element e, out string refusal)
        {
            refusal = null;
            if (e is Floor f) return f.SketchId;
            if (e is Ceiling c) return c.SketchId;
            if (e is Opening o) return o.SketchId;
            if (e is Wall w) return w.SketchId;
            if (e is FootPrintRoof)
                refusal = "a FootPrintRoof exposes no SketchId in the Revit API (2023-2027), so its footprint cannot be edited through a SketchEditScope; edit it in Revit.";
            else
                refusal = "edit_sketch covers Floor, Ceiling, profiled Wall and Opening; this element is " + (e == null ? "missing" : e.GetType().Name) + ".";
            return null;
        }

        private static SketchPt ToPlane(Plane plane, XYZ p)
        {
            XYZ d = p - plane.Origin;
            return new SketchPt(d.DotProduct(plane.XVec) * 304.8, d.DotProduct(plane.YVec) * 304.8);
        }

        private static XYZ FromPlane(Plane plane, SketchPt p) =>
            plane.Origin + plane.XVec * (p.X / 304.8) + plane.YVec * (p.Y / 304.8);

        private static SketchSegment SegmentOf(Plane plane, Curve c) =>
            new SketchSegment(ToPlane(plane, c.GetEndPoint(0)), ToPlane(plane, c.GetEndPoint(1)), ToPlane(plane, c.Evaluate(0.5, true)));

        // Sketch.GetAllElements' own remark in RevitAPI.xml: the ModelCurve behind a Profile
        // curve is that curve's Reference.ElementId - an exact mapping, where geometry alone
        // can take a slope arrow drawn along an edge for the edge.
        private static ElementId RefId(Curve c)
        {
            try
            {
                ElementId id = c.Reference?.ElementId;
                return id == null || id == ElementId.InvalidElementId ? null : id;
            }
            catch { return null; }
        }

        private static SketchRead ReadSketch(Sketch sketch, Plane plane, out string problem)
        {
            problem = null;
            var read = new SketchRead();
            if (sketch?.Profile == null) { problem = "the sketch has no profile."; return null; }
            foreach (CurveArray array in sketch.Profile)
            {
                List<Curve> all = array.Cast<Curve>().ToList();
                // A circle drawn with Revit's Circle tool is ONE closed curve with no ends
                // (Curve.GetEndPoint throws on it): it is carried as a sampled, fixed loop, so an
                // edit to ANOTHER loop still plans, and an edit to it is refused by name.
                foreach (Curve c in all.Where(x => !x.IsBound))
                {
                    if (!c.IsCyclic) { problem = "the profile holds an unbound curve that is not closed; edit_sketch cannot describe it."; return null; }
                    var pts = new List<SketchPt>();
                    for (int i = 0; i < CircleSamples; i++) pts.Add(ToPlane(plane, c.Evaluate(c.Period * i / CircleSamples, false)));
                    read.Vertices.Add(pts);
                    read.Outlines.Add(pts.ToList());
                    read.Segments.Add(new List<SketchSegment>());
                    read.IsLine.Add(pts.Select(_ => false).ToList());
                    read.Reversed.Add(pts.Select(_ => false).ToList());
                    read.CurveIds.Add(new List<ElementId> { RefId(c) });
                    read.Fixed.Add(true);
                }
                List<Curve> curves = all.Where(x => x.IsBound).ToList();
                if (curves.Count == 0) continue;
                List<SketchSegment> segs = curves.Select(c => SegmentOf(plane, c)).ToList();
                List<List<ChainStep>> loops = SketchEditRules.Chain(segs, SketchEditRules.MatchToleranceMm, out problem);
                if (loops == null) return null;
                foreach (List<ChainStep> loop in loops)
                {
                    read.Vertices.Add(SketchEditRules.Vertices(segs, loop));
                    read.Segments.Add(loop.Select(s => segs[s.Segment]).ToList());
                    read.IsLine.Add(loop.Select(s => curves[s.Segment] is Line).ToList());
                    read.Reversed.Add(loop.Select(s => s.Reversed).ToList());
                    read.CurveIds.Add(loop.Select(s => RefId(curves[s.Segment])).ToList());
                    read.Fixed.Add(false);
                    var outline = new List<SketchPt>();
                    foreach (ChainStep s in loop)
                    {
                        List<SketchPt> pts = curves[s.Segment].Tessellate().Select(p => ToPlane(plane, p)).ToList();
                        if (s.Reversed) pts.Reverse();
                        outline.AddRange(pts.Take(pts.Count - 1));   // the next step starts where this one ends
                    }
                    read.Outlines.Add(outline);
                }
            }
            if (read.Vertices.Count == 0) { problem = "the sketch has no profile loops."; return null; }
            return read;
        }

        private static double? AreaParamM2(Element e)
        {
            try
            {
                Parameter p = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                return p != null && p.HasValue ? (double?)(p.AsDouble() * SqFtToM2) : null;
            }
            catch { return null; }
        }

        private static JArray LoopsJson(IEnumerable<List<SketchPt>> loops) =>
            new JArray(loops.Select(l => new JArray(l.Select(p => new JArray(Math.Round(p.X, 1), Math.Round(p.Y, 1))))));

        private static JArray XyzJson(XYZ q, double scale) =>
            new JArray(Math.Round(q.X / scale, 6), Math.Round(q.Y / scale, 6), Math.Round(q.Z / scale, 6));

        private static JArray DirJson(XYZ d) => new JArray(Math.Round(d.X, 9), Math.Round(d.Y, 9), Math.Round(d.Z, 9));

        /// <summary>Loops as model [x,y,z] in the request's units - what start and loop take back.</summary>
        private static JArray ModelLoopsJson(IEnumerable<List<SketchPt>> loops, Plane plane, double scale) =>
            new JArray(loops.Select(l => new JArray(l.Select(p => XyzJson(FromPlane(plane, p), scale)))));

        private static JObject SketchPlaneJson(Plane plane, double scale) => new JObject
        {
            ["origin"] = XyzJson(plane.Origin, scale), ["x_dir"] = DirJson(plane.XVec),
            ["y_dir"] = DirJson(plane.YVec), ["normal"] = DirJson(plane.Normal)
        };

        /// <summary>The hosted instances, openings and tags that depend on the element: what an edit must not strand.</summary>
        private static List<long> DependentIds(Element e)
        {
            try
            {
                var filter = new LogicalOrFilter(
                    new LogicalOrFilter(new ElementClassFilter(typeof(FamilyInstance)), new ElementClassFilter(typeof(Opening))),
                    new ElementClassFilter(typeof(IndependentTag)));
                long self = Rid.Value(e.Id);
                return e.GetDependentElements(filter).Select(x => Rid.Value(x)).Where(x => x != self).Distinct().OrderBy(x => x).ToList();
            }
            catch { return null; }   // named not_read in the reply, never counted as kept
        }

        /// <summary>What was asked, resolved against the current sketch.</summary>
        private sealed class SketchEditPlan
        {
            public long Id;
            public Element Element;
            public string UniqueId;
            public ElementId SketchId;
            public Plane Plane;
            public SketchRead Before;
            public int LoopIndex;
            public int VertexIndex = -1;          // move mode
            public SketchPt MoveTo;
            public List<SketchPt> NewLoop;        // replace mode
            public bool ReshapeInPlace;           // replace mode: same edge count, all straight - no curve is deleted
            public int[] Map;                     // ReshapeInPlace: old vertex i -> index in NewLoop
            public List<List<SketchPt>> ExpectedVertices;
            public double ExpectedAreaM2;
            public double? AreaBeforeM2;
            public bool AreaCheckApplies;
            public List<long> HostedBefore;       // null: could not be read
        }

        private CommandResult ExecuteEditSketch(UIApplication app, GateResult gate, JObject request, JArray input)
        {
            Document doc = gate.Document;
            if (input.Count != 1)
                return CommandResult.Fail(EditSketchOp + " edits ONE element per call; send one operation.");
            var o = input[0] as JObject;
            if (o == null) return CommandResult.Fail("operations[0] is not an object.");
            double scale;
            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!Scale(units, out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            string error; JObject boundary;
            SketchEditPlan plan = PlanEditSketch(doc, o, scale, out error, out boundary);
            if (plan == null)
            {
                if (boundary != null) boundary["units"] = units;
                var diagnostic = TransformFailure.SketchFailure(o["element_ids"]?.ToString(Formatting.None) ?? "in operation 0", error);
                return CommandResult.FailWithDetail(EditSketchOp + ": " + diagnostic["error"] + " Nothing was changed." + (boundary == null ? "" :
                    " The current boundary, model [x,y,z] in the request's units: " + boundary.ToString(Formatting.None)), diagnostic);
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "units", "operations");
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint, RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            resolvedPlan.Elements.Add(new PlannedElement
            {
                UniqueId = plan.UniqueId, ElementId = plan.Id, Category = plan.Element.Category?.Name,
                TypeName = SafePlanTypeName(doc, plan.Element), Action = PlannedAction.Modify,
                GeometryFingerprint = SafePlanGeometry(plan.Element),
                BeforeValues = new Dictionary<string, string>
                {
                    { "operation", EditSketchOp },
                    { "loops", LoopsJson(plan.Before.Vertices).ToString(Formatting.None) }
                },
                ProposedValues = new Dictionary<string, string>
                {
                    { "operation", EditSketchOp },
                    { "loops", LoopsJson(plan.ExpectedVertices).ToString(Formatting.None) }
                }
            });

            string method = plan.NewLoop == null || plan.ReshapeInPlace ? "reshape_in_place" : "delete_and_redraw";
            var planJson = new JObject
            {
                ["element_id"] = plan.Id,
                ["mode"] = plan.NewLoop != null ? "replace_loop" : "move_vertex",
                ["method"] = method,
                ["loop_index"] = plan.LoopIndex,
                ["vertex_index"] = plan.VertexIndex < 0 ? null : (JToken)plan.VertexIndex,
                ["units"] = units,
                ["sketch_plane"] = SketchPlaneJson(plan.Plane, scale),
                ["loops_before"] = ModelLoopsJson(plan.Before.Vertices, plan.Plane, scale),
                ["loops_expected"] = ModelLoopsJson(plan.ExpectedVertices, plan.Plane, scale),
                ["fixed_loops"] = new JArray(Enumerable.Range(0, plan.Before.Fixed.Count).Where(i => plan.Before.Fixed[i])),
                ["loops_before_mm"] = LoopsJson(plan.Before.Vertices),
                ["loops_expected_mm"] = LoopsJson(plan.ExpectedVertices),
                ["coordinates"] = "loops_before/loops_expected: model [x,y,z] in the request's units (send them back as start or loop); " +
                                  "*_mm: (u, v) in mm from sketch_plane.origin along its x_dir and y_dir",
                ["sketch_area_before_m2"] = Math.Round(plan.Before.AreaM2, 4),
                ["expected_area_m2"] = Math.Round(plan.ExpectedAreaM2, 4),
                ["area_parameter_before_m2"] = plan.AreaBeforeM2.HasValue ? (JToken)Math.Round(plan.AreaBeforeM2.Value, 4) : null,
                ["area_check"] = plan.AreaCheckApplies ? "will_verify" : "not_applicable",
                ["hosted_elements"] = plan.HostedBefore == null ? null : (JToken)plan.HostedBefore.Count
            };

            if (dryRun)
            {
                JObject detail; string err;
                bool ok = TryEditSketch(doc, plan, commit: false, out detail, out err);
                planJson["rehearsal_ok"] = ok;
                planJson["rehearsal_error"] = ok ? null : TransformFailure.SketchFailure(plan.Id.ToString(), err)["error"];
                planJson["error_code"] = ok ? null : TransformFailure.SketchFailure(plan.Id.ToString(), err)["error_code"];
                planJson["rehearsal_detail"] = detail;
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "rehearsed_and_cancelled",
                    ["targets"] = 1, ["plan"] = new JArray(planJson),
                    ["note"] = "The curve edits were made inside the element's SketchEditScope and the scope was Cancelled; nothing " +
                               "was committed. Revit checks the finished sketch only when the apply commits the scope (a refusal " +
                               "there rolls everything back and is reported with what Revit said), so the plan checked first: the " +
                               "edited loop closed and not self-crossing (arcs by their tessellation), clear of the other loops, and " +
                               "no loop turned from hole to solid or back."
                };
                if (ok) DocumentGate.RecordResolvedPlan(resolvedPlan);
                ApplicationOutcome.StampRehearsal(result, 1, 0, ok ? 0 : 1, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, ok,
                    ok ? "the token binds the element, the edit and the units" : "no usable confirmation is issued while the rehearsal fails");
                return CommandResult.Ok(result);
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            JObject applyDetail = null;
            using (var group = new TransactionGroup(doc, "Horizun: edit sketch"))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("the transaction group would not start. Nothing was changed.");
                try
                {
                    // A profile edit can reshape a non-structural floor directly, or
                    // move a wall and its associated floor sketch. Keep their before
                    // state while the outer group can still undo the scope's commit.
                    var floorBefore = FloorWallOverlapGuard.Capture(doc,
                        new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
                            .Where(f => FloorWallOverlapGuard.IsFinishFloor(doc, f)).Select(f => f.Id)
                            .Concat(new[] { Rid.Make(plan.Id) }));
                    string err;
                    if (!TryEditSketch(doc, plan, commit: true, out applyDetail, out err))
                    {
                        Guard.RollbackResult rb = Guard.RollBack(group);
                        return CommandResult.FailWithDetail(TransformFailure.AtomicMessage(plan.Id.ToString(), err,
                            PlanFailure.SingleTransactionOutcome(true, rb.StatusName, "nothing was changed")),
                            TransformFailure.RevertedDetail(rb.StatusName, rb.Confirmed));
                    }
                    // Held BEFORE the group is kept, so a failure here leaves nothing behind.
                    string held = HoldCommittedSketch(doc, plan, applyDetail);
                    if (held != null)
                    {
                        Guard.RollbackResult rb = Guard.RollBack(group);
                        return CommandResult.FailWithDetail(TransformFailure.AtomicMessage(plan.Id.ToString(), held,
                            PlanFailure.SingleTransactionOutcome(true, rb.StatusName, "nothing was changed")),
                            TransformFailure.RevertedDetail(rb.StatusName, rb.Confirmed));
                    }
                    FloorWallOverlapGuard.ThrowIfIncreased(doc, floorBefore, new[] { Rid.Make(plan.Id) });
                    Guard.Assimilate(group, EditSketchOp);
                }
                catch (SilentRollbackException)
                {
                    if (group.HasStarted()) Guard.RollBack(group);
                    throw;
                }
                catch (Exception ex)
                {
                    bool attempted = false; string rbStatus = PlanFailure.NotAttempted;
                    if (group.HasStarted()) { attempted = true; rbStatus = Guard.RollBack(group).StatusName; }
                    return CommandResult.FailWithDetail(TransformFailure.AtomicMessage(plan.Id.ToString(), ex.Message,
                        PlanFailure.SingleTransactionOutcome(attempted, rbStatus, "nothing was changed")),
                        TransformFailure.AtomicDetail(ex, string.Equals(rbStatus, "RolledBack", StringComparison.OrdinalIgnoreCase)));
                }
            }

            // ---- re-read from the model: the element, its sketch, its loops, its dependents, its area. ----
            Element after = doc.GetElement(Rid.Make(plan.Id));
            bool idKept = after != null && string.Equals(after.UniqueId, plan.UniqueId, StringComparison.Ordinal);
            string readProblem = null, loopProblem;
            SketchRead now = idKept ? ReadCurrentSketch(doc, plan, out readProblem) : null;
            loopProblem = now == null ? (readProblem ?? "the committed sketch could not be read.")
                : SketchEditRules.MatchLoops(plan.ExpectedVertices.Cast<IList<SketchPt>>().ToList(),
                                             now.Vertices.Cast<IList<SketchPt>>().ToList(), SketchEditRules.MatchToleranceMm);
            double? areaAfter = after == null ? null : AreaParamM2(after);
            string areaCheck = !plan.AreaCheckApplies ? "not_applicable"
                : areaAfter.HasValue && SketchEditRules.AreaAgrees(areaAfter.Value, plan.ExpectedAreaM2) ? "verified" : "failed";
            int kept = plan.HostedBefore == null ? 0 : plan.HostedBefore.Count(h => doc.GetElement(Rid.Make(h)) != null);
            string hostedCheck = plan.HostedBefore == null ? "not_read" : kept == plan.HostedBefore.Count ? "verified" : "failed";
            bool verified = idKept && loopProblem == null && areaCheck != "failed" && hostedCheck != "failed";
            var row = new JObject
            {
                ["element_id"] = plan.Id, ["verified"] = verified,
                ["unique_id_kept"] = idKept,
                ["method"] = method,
                ["floor_wall_overlap_increase_checked"] = true,
                ["loops_verified"] = loopProblem == null, ["loops_problem"] = loopProblem,
                ["loops_after"] = now == null ? null : ModelLoopsJson(now.Vertices, plan.Plane, scale),
                ["loops_after_mm"] = now == null ? null : LoopsJson(now.Vertices),
                ["sketch_area_after_m2"] = now == null ? null : (JToken)Math.Round(now.AreaM2, 4),
                ["expected_area_m2"] = Math.Round(plan.ExpectedAreaM2, 4),
                ["area_parameter_before_m2"] = plan.AreaBeforeM2.HasValue ? (JToken)Math.Round(plan.AreaBeforeM2.Value, 4) : null,
                ["area_parameter_after_m2"] = areaAfter.HasValue ? (JToken)Math.Round(areaAfter.Value, 4) : null,
                ["area_check"] = areaCheck,
                ["area_note"] = plan.AreaCheckApplies ? null :
                    "the element's Area parameter did not equal its sketch's area before the edit (slope, shape edit, a cut, or no Area parameter), so it cannot be held to the new sketch; the loops were verified instead.",
                ["hosted_elements_before"] = plan.HostedBefore == null ? null : (JToken)plan.HostedBefore.Count,
                ["hosted_elements_kept"] = plan.HostedBefore == null ? null : (JToken)kept,
                ["hosted_check"] = hostedCheck,
                ["curves"] = applyDetail,
                ["revit_said_at_commit"] = applyDetail?["revit_said_at_commit"]
            };
            if (!verified)
                return CommandResult.FailWithDetail("The sketch edit committed, but post-commit verification failed. Inspect the element; use Revit undo if needed: " + row.ToString(Formatting.None),
                    new JObject { ["error_code"] = ErrorCodes.VerificacionFallida, ["transaction_status"] = "Committed", ["rows"] = new JArray(row) });

            var applied = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed",
                ["operations_verified"] = 1, ["targets"] = 1, ["rows"] = new JArray(row)
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0);
            applied["undo"] = new JObject
            {
                ["recorded"] = false,
                ["reason"] = "sketch geometry edits are not covered by horizun_undo; use Revit's own undo within this session."
            };
            return CommandResult.Ok(applied);
        }

        /// <summary>The element's sketch as it stands now, read in the plan's own frame so both readings compare.</summary>
        private static SketchRead ReadCurrentSketch(Document doc, SketchEditPlan plan, out string problem)
        {
            problem = null;
            string ignored;
            ElementId sid = EditableSketchId(doc.GetElement(Rid.Make(plan.Id)), out ignored);
            var sketch = sid == null ? null : doc.GetElement(sid) as Sketch;
            return sketch == null ? null : ReadSketch(sketch, plan.Plane, out problem);
        }

        /// <summary>
        /// What must hold before the group is kept: every dependent that existed still does, and
        /// the sketch holds the expected loops - a scope commit Revit rolled back without throwing
        /// leaves the old ones, and must not be reported as committed. Null when both hold;
        /// otherwise why not, with what Revit said at the commit.
        /// </summary>
        private static string HoldCommittedSketch(Document doc, SketchEditPlan plan, JObject detail)
        {
            string said = (string)detail?["revit_said_at_commit"];
            string tail = said == null ? "" : " Revit said: " + said + ".";
            if (plan.HostedBefore != null)
            {
                List<long> lost = plan.HostedBefore.Where(h => doc.GetElement(Rid.Make(h)) == null).ToList();
                if (lost.Count > 0)
                    return "the commit deleted " + lost.Count + " element(s) that depended on the element (hosted instances, openings or tags): " +
                           string.Join(", ", lost.Take(20)) + "." + tail;
            }
            string problem;
            SketchRead mid = ReadCurrentSketch(doc, plan, out problem);
            if (mid == null) return "the committed sketch could not be read" + (problem == null ? "." : ": " + problem) + tail;
            var found = mid.Vertices.Cast<IList<SketchPt>>().ToList();
            string loops = SketchEditRules.MatchLoops(plan.ExpectedVertices.Cast<IList<SketchPt>>().ToList(), found, SketchEditRules.MatchToleranceMm);
            if (loops == null) return null;
            bool unchanged = SketchEditRules.MatchLoops(plan.Before.Vertices.Cast<IList<SketchPt>>().ToList(), found, SketchEditRules.MatchToleranceMm) == null;
            return (unchanged
                ? "Revit kept the OLD boundary when the scope committed - it rolled the edit back there without raising an error."
                : "the committed sketch does not hold the expected loops: " + loops) + tail;
        }

        private static List<SketchPt> PointsOf(JToken token, double scale, Plane plane, string what, out string error)
        {
            error = null;
            var list = new List<SketchPt>();
            var arr = token as JArray;
            if (arr == null) { error = what + " must be an array of [x,y,z] points."; return null; }
            foreach (JToken t in arr)
            {
                var c = t as JArray;
                if (c == null || c.Count != 3) { error = what + ": every point must be [x,y,z]."; return null; }
                XYZ p;
                try { p = new XYZ(c[0].Value<double>() * scale, c[1].Value<double>() * scale, c[2].Value<double>() * scale); }
                catch { error = what + ": coordinates must be numbers."; return null; }
                double off = Math.Abs((p - plane.Origin).DotProduct(plane.Normal)) * 304.8;
                if (off > SketchEditRules.OffPlaneToleranceMm)
                {
                    // Invariant numbers: a caller re-sends on the plane this names (the live probe
                    // does), and a comma decimal would read as one more coordinate separator.
                    CultureInfo inv = CultureInfo.InvariantCulture;
                    error = what + ": a point lies " + Math.Round(off, 1).ToString(inv) + " mm off the sketch plane. Points are refused rather than " +
                            "projected, so a wrong elevation is never silently flattened; the plane passes through (" +
                            Math.Round(plane.Origin.X * 304.8, 1).ToString(inv) + ", " + Math.Round(plane.Origin.Y * 304.8, 1).ToString(inv) + ", " +
                            Math.Round(plane.Origin.Z * 304.8, 1).ToString(inv) + ") mm.";
                    return null;
                }
                list.Add(ToPlane(plane, p));
            }
            return list;
        }

        private static SketchEditPlan PlanEditSketch(Document doc, JObject o, double scale, out string error, out JObject boundary)
        {
            error = null; boundary = null;
            var allowed = new HashSet<string> { "operation", "element_ids", "loop", "loop_index", "start", "end" };
            foreach (JProperty field in o.Properties())
                if (!allowed.Contains(field.Name)) { error = field.Name + " is not applicable to " + EditSketchOp + "."; return null; }
            var ids = o["element_ids"] as JArray;
            if (ids == null || ids.Count != 1) { error = "element_ids must name exactly one element."; return null; }
            long id = ids[0].Value<long>();
            if (!Rid.CanRepresent(id)) { error = "ElementId " + id + " is outside the supported range."; return null; }
            bool replace = o["loop"] != null, move = o["start"] != null || o["end"] != null;
            if (replace == move)
            { error = "pass EITHER loop with loop_index (replace that loop) OR start and end (move the vertex at start to end)."; return null; }

            Element e = doc.GetElement(Rid.Make(id));
            if (e == null) { error = "element " + id + " does not exist."; return null; }
            string refusal;
            ElementId sketchId = EditableSketchId(e, out refusal);
            if (refusal != null) { error = refusal; return null; }
            if (sketchId == null || Rid.Value(sketchId) < 0) { error = "element " + id + " carries no sketch."; return null; }
            var sketch = doc.GetElement(sketchId) as Sketch;
            if (sketch?.SketchPlane == null) { error = "the element's sketch or its plane could not be read."; return null; }
            Plane plane = sketch.SketchPlane.GetPlane();
            string problem;
            SketchRead before = ReadSketch(sketch, plane, out problem);
            if (before == null) { error = problem; return null; }
            // From here on every refusal carries the boundary as it stands, in the caller's terms.
            boundary = new JObject
            {
                ["element_id"] = id, ["sketch_plane"] = SketchPlaneJson(plane, scale),
                ["loops"] = ModelLoopsJson(before.Vertices, plane, scale),
                ["fixed_loops"] = new JArray(Enumerable.Range(0, before.Fixed.Count).Where(i => before.Fixed[i]))
            };

            var plan = new SketchEditPlan
            {
                Id = id, Element = e, UniqueId = e.UniqueId, SketchId = sketchId, Plane = plane, Before = before,
                ExpectedVertices = before.Vertices.Select(l => l.ToList()).ToList(),
                HostedBefore = DependentIds(e)
            };
            var outlines = before.Outlines.Select(l => l.ToList()).ToList();

            if (replace)
            {
                if (o["loop_index"] == null) { error = "loop needs loop_index: which of the " + before.Vertices.Count + " loop(s) listed below it replaces."; return null; }
                int k = o.Value<int>("loop_index");
                if (k < 0 || k >= before.Vertices.Count) { error = "loop_index " + k + " is out of range; the sketch has " + before.Vertices.Count + " loop(s), listed below."; return null; }
                if (before.Fixed[k]) { error = "loop " + k + " is a full circle - one closed curve with no vertices - which edit_sketch does not replace; edit it in Revit."; return null; }
                List<SketchPt> pts = PointsOf(o["loop"], scale, plane, "loop", out error);
                if (pts == null) return null;
                pts = SketchEditRules.Normalise(pts);
                string invalid = SketchEditRules.ValidateLoop(pts);
                if (invalid != null) { error = "loop: " + invalid; return null; }
                plan.LoopIndex = k; plan.NewLoop = pts;
                // Same number of straight edges: every existing line is reshaped onto the new edge
                // nearest to it, so nothing it carries (Defines Slope, dimensions, constraints) is lost.
                if (pts.Count == before.Vertices[k].Count && before.IsLine[k].All(x => x))
                {
                    plan.ReshapeInPlace = true;
                    plan.Map = SketchEditRules.AlignCyclic(before.Vertices[k], pts);
                }
                plan.ExpectedVertices[k] = pts;
                outlines[k] = pts;
            }
            else
            {
                if (o["start"] == null || o["end"] == null) { error = "a vertex move needs both start (the vertex now) and end (where it goes)."; return null; }
                List<SketchPt> from = PointsOf(new JArray(o["start"]), scale, plane, "start", out error);
                if (from == null) return null;
                List<SketchPt> to = PointsOf(new JArray(o["end"]), scale, plane, "end", out error);
                if (to == null) return null;
                int l, v;
                if (!SketchEditRules.FindVertex(before.Vertices.Cast<IList<SketchPt>>().ToList(), from[0], SketchEditRules.MatchToleranceMm, out l, out v, out problem))
                { error = problem; return null; }
                if (before.Fixed[l]) { error = "start lies on loop " + l + ", a full circle with no vertices, which edit_sketch does not edit."; return null; }
                int n = before.Vertices[l].Count;
                int prev = (v - 1 + n) % n;
                if (!before.IsLine[l][prev] || !before.IsLine[l][v])
                { error = "the vertex joins an arc or another non-line curve; moving it would redefine that curve, which edit_sketch does not guess. Replace the loop instead."; return null; }
                List<SketchPt> moved = SketchEditRules.MoveVertex(before.Vertices[l], v, to[0]);
                SketchPt old = before.Vertices[l][v];
                int at = outlines[l].FindIndex(p => p.DistanceTo(old) < SketchEditRules.MatchToleranceMm);
                if (at < 0) { error = "the vertex could not be found on the loop's outline."; return null; }
                outlines[l][at] = to[0];
                // Every loop is held to the rules Revit's finish check applies: an all-line loop
                // edge by edge, a loop with arcs by its tessellated outline around the moved vertex
                // (its arcs' chords are sub-millimetre by design, not edges to hold to the 1 mm rule).
                string invalid = before.IsLine[l].All(x => x) ? SketchEditRules.ValidateLoop(moved) : SketchEditRules.ValidateMovedVertex(outlines[l], at);
                if (invalid != null) { error = "the moved loop: " + invalid; return null; }
                plan.LoopIndex = l; plan.VertexIndex = v; plan.MoveTo = to[0];
                plan.ExpectedVertices[l] = moved;
            }

            // Loops that touch or cross are refused by Revit only when the sketch is finished -
            // after a dry run has Cancelled - so the plan refuses them first, by name.
            var after = outlines.Cast<IList<SketchPt>>().ToList();
            string crossing = SketchEditRules.CrossesOtherLoops(after, plan.LoopIndex);
            if (crossing != null) { error = (plan.NewLoop != null ? "loop: " : "the moved loop: ") + crossing; return null; }
            // A hole the new boundary leaves outside would become slab, an island it swallows a
            // hole - and the area would still agree with the sketch, so only this can see it.
            string role = SketchEditRules.RoleChange(before.Outlines.Cast<IList<SketchPt>>().ToList(), after);
            if (role != null) { error = role; return null; }

            plan.ExpectedAreaM2 = SketchEditRules.NetArea(after) / 1e6;
            plan.AreaBeforeM2 = AreaParamM2(e);
            plan.AreaCheckApplies = plan.AreaBeforeM2.HasValue && SketchEditRules.AreaAgrees(plan.AreaBeforeM2.Value, before.AreaM2);
            return plan;
        }

        /// <summary>
        /// The loop's own curve elements, step by step: the ModelCurve each Profile curve's
        /// Reference names, which must also lie where the step lies. Without a Reference, the ONE
        /// sketch curve on the step; two there (a slope arrow drawn along an edge) is refused
        /// rather than guessed.
        /// </summary>
        private static ElementId[] MatchLoopCurves(Document doc, Sketch sketch, SketchEditPlan plan, out string error)
        {
            error = null;
            List<SketchSegment> steps = plan.Before.Segments[plan.LoopIndex];
            List<ElementId> named = plan.Before.CurveIds[plan.LoopIndex];
            var curves = new List<KeyValuePair<ElementId, SketchSegment>>();
            foreach (ElementId eid in sketch.GetAllElements())
            {
                Curve g = (doc.GetElement(eid) as CurveElement)?.GeometryCurve;
                if (g == null || !g.IsBound) continue;
                curves.Add(new KeyValuePair<ElementId, SketchSegment>(eid, SegmentOf(plan.Plane, g)));
            }
            var byStep = new ElementId[steps.Count];
            for (int i = 0; i < steps.Count; i++)
            {
                List<ElementId> onStep = curves.Where(c => SketchEditRules.SameSegment(c.Value, steps[i], SketchEditRules.MatchToleranceMm))
                                               .Select(c => c.Key).ToList();
                ElementId want = named[i];
                if (want != null)
                {
                    if (!onStep.Any(x => Rid.Value(x) == Rid.Value(want)))
                    { error = "edge " + i + " of loop " + plan.LoopIndex + ": the sketch curve its profile names (" + Rid.Value(want) + ") does not lie on it; nothing was edited."; return null; }
                    byStep[i] = want;
                }
                else if (onStep.Count == 1) byStep[i] = onStep[0];
                else
                {
                    error = onStep.Count == 0
                        ? "edge " + i + " of loop " + plan.LoopIndex + " matches no sketch curve; nothing was edited."
                        : onStep.Count + " sketch curves lie on edge " + i + " of loop " + plan.LoopIndex + " (a slope arrow or another line drawn along it) " +
                          "and the profile names none of them; edit_sketch does not guess which is the boundary.";
                    return null;
                }
            }
            if (byStep.Select(x => Rid.Value(x)).Distinct().Count() != byStep.Length)
            { error = "two edges of loop " + plan.LoopIndex + " resolve to the same sketch curve; nothing was edited."; return null; }
            return byStep;
        }

        private static bool DefinesSlope(Element c)
        {
            try
            {
                Parameter p = c?.get_Parameter(BuiltInParameter.CURVE_IS_SLOPE_DEFINING);
                return p != null && p.HasValue && p.StorageType == StorageType.Integer && p.AsInteger() != 0;
            }
            catch { return false; }
        }

        private static string Describe(Document doc, ElementId id)
        {
            Element x = doc.GetElement(id);
            return x == null ? "?" : (x.Category?.Name ?? x.GetType().Name);
        }

        // Keeps each curve's own direction: a step walked B to A is set end to start.
        private static void Reshape(Document doc, ElementId id, bool reversed, XYZ from, XYZ to) =>
            ((CurveElement)doc.GetElement(id)).SetGeometryCurve(reversed ? Line.CreateBound(to, from) : Line.CreateBound(from, to), true);

        /// <summary>
        /// Applies the plan inside the element's SketchEditScope. commit=false makes the curve
        /// edits and then Cancels the whole scope; commit=true keeps it, and what Revit says at
        /// the scope commit is kept in detail["revit_said_at_commit"]. Same
        /// Start/inner-transaction/Commit/finally-Cancel shape as TryRealign and Core/IfcProfileUpdate.cs.
        /// </summary>
        private static bool TryEditSketch(Document doc, SketchEditPlan plan, bool commit, out JObject detail, out string error)
        {
            detail = new JObject(); error = null;
            var commitRecorder = new RevitErrorRecorder();
            var scope = new SketchEditScope(doc, "Horizun: edit sketch");
            try
            {
                if (!scope.IsSketchEditingSupported(plan.SketchId))
                { error = "Revit reports that this sketch cannot be edited (a group, a part, or an element borrowed by somebody else answers this way)."; return false; }
                scope.Start(plan.SketchId);
                var sketch = doc.GetElement(plan.SketchId) as Sketch;
                if (sketch?.SketchPlane == null) { error = "the sketch could not be read after the edit scope opened."; return false; }
                Plane plane = plan.Plane;
                ElementId[] byStep = MatchLoopCurves(doc, sketch, plan, out error);
                if (byStep == null) return false;
                List<bool> reversed = plan.Before.Reversed[plan.LoopIndex];

                using (var tx = new Transaction(doc, "Horizun: edit sketch curves"))
                {
                    RevitErrorRecorder recorder = RevitErrorRecorder.On(tx);
                    if (tx.Start() != TransactionStatus.Started) { error = "the sketch curve transaction would not start."; return false; }
                    if (plan.NewLoop != null && plan.ReshapeInPlace)
                    {
                        List<SketchPt> pts = plan.NewLoop;
                        int n = byStep.Length;
                        for (int i = 0; i < n; i++)
                            Reshape(doc, byStep[i], reversed[i], FromPlane(plane, pts[plan.Map[i]]), FromPlane(plane, pts[plan.Map[(i + 1) % n]]));
                        detail["curves_reshaped"] = n;
                    }
                    else if (plan.NewLoop != null)
                    {
                        // Redrawn: the old curves go. Refused, by name, when one defines the slope (the
                        // element would come back flat) or when deleting them would take anything
                        // else with it - both seen here, in the rehearsal, before a token is issued.
                        List<long> slope = byStep.Where(x => DefinesSlope(doc.GetElement(x))).Select(x => Rid.Value(x)).ToList();
                        if (slope.Count > 0)
                        {
                            error = "line(s) " + string.Join(", ", slope) + " of the loop define the element's slope; a loop with another number of edges " +
                                    "is redrawn, which would delete them and flatten the element. Send a loop with the same number of straight edges " +
                                    "(reshaped in place), or change the slope in Revit first.";
                            return false;
                        }
                        var mine = new HashSet<long>(byStep.Select(x => Rid.Value(x)));
                        var everything = new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true));
                        List<string> riders = byStep.SelectMany(x => doc.GetElement(x).GetDependentElements(everything))
                            .Where(d => !mine.Contains(Rid.Value(d))).Select(d => Rid.Value(d) + " (" + Describe(doc, d) + ")").Distinct().ToList();
                        if (riders.Count > 0)
                        {
                            error = "deleting the loop's curves would also delete " + riders.Count + " element(s) drawn to them: " + string.Join(", ", riders.Take(20)) +
                                    ". Send a loop with the same number of straight edges (its curves are reshaped, not deleted), or remove those first.";
                            return false;
                        }
                        ICollection<ElementId> gone = doc.Delete(byStep.ToList());
                        List<long> extra = (gone ?? new List<ElementId>()).Select(x => Rid.Value(x)).Where(x => !mine.Contains(x)).Distinct().ToList();
                        if (extra.Count > 0)
                        { error = "deleting the loop's curves also deleted " + extra.Count + " other element(s): " + string.Join(", ", extra.Take(20)) + "."; return false; }
                        List<SketchPt> pts = plan.NewLoop;
                        for (int i = 0; i < pts.Count; i++)
                            doc.Create.NewModelCurve(Line.CreateBound(FromPlane(plane, pts[i]), FromPlane(plane, pts[(i + 1) % pts.Count])), sketch.SketchPlane);
                        detail["curves_deleted"] = byStep.Length;
                        detail["curves_created"] = pts.Count;
                    }
                    else
                    {
                        List<SketchPt> verts = plan.Before.Vertices[plan.LoopIndex];
                        int n = verts.Count, v = plan.VertexIndex, prev = (v - 1 + n) % n;
                        // Both neighbours set explicitly with overrideJoins, so neither drags the
                        // other through its join: the result is exactly the two lines asked for.
                        Reshape(doc, byStep[prev], reversed[prev], FromPlane(plane, verts[prev]), FromPlane(plane, plan.MoveTo));
                        Reshape(doc, byStep[v], reversed[v], FromPlane(plane, plan.MoveTo), FromPlane(plane, verts[(v + 1) % n]));
                        detail["curves_reshaped"] = 2;
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                    { error = "the sketch curve transaction did not commit." + recorder.Said(); return false; }
                    detail["revit_said"] = recorder.Errors.Count == 0 ? null : string.Join("; ", recorder.Errors);
                }

                // COMMITTING THE SCOPE IS WHERE REVIT VALIDATES THE SKETCH AS A WHOLE; what it says
                // there is kept, so a refusal (or a quiet rollback, caught by the caller's re-read)
                // is reported with Revit's own words.
                if (commit)
                {
                    scope.Commit(commitRecorder);
                    detail["revit_said_at_commit"] = commitRecorder.Errors.Count == 0 ? null : string.Join("; ", commitRecorder.Errors);
                }
                else scope.Cancel();
                return true;
            }
            catch (Exception ex)
            {
                error = "Revit refused the sketch edit: " + ex.Message +
                        (commitRecorder.Errors.Count == 0 ? "" : " Revit said: " + string.Join("; ", commitRecorder.Errors) + ".") +
                        " A loop that is open, crosses itself or another loop, or would strand a hosted element is refused here, and the element keeps the boundary it had.";
                return false;
            }
            finally
            {
                try { if (scope.IsActive) scope.Cancel(); } catch { }
            }
        }
    }
}
