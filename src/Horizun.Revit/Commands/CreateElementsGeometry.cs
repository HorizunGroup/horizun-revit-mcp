using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private static void NormalizePlan(Document doc, Plan p)
        {
            if (p.Input["source_reference"] != null) SourceTrace.Validate(p.Input["source_reference"] as JObject);
            foreach (string name in new[] { "elevation", "height", "offset", "base_offset", "top_offset", "rotation_degrees", "slope_degrees", "slope_ratio" })
                if (p.Input[name] != null) GeometryInput.Number(p.Input[name], name);
            foreach (string name in new[] { "flip", "structural" })
                if (p.Input[name] != null && p.Input[name].Type != JTokenType.Boolean) throw new ArgumentException(name + " must be boolean.");
            if (p.Kind == "room" && ((JArray)p.Input["point"]).Count != 2)
                throw new ArgumentException("room.point requires XY only; a room insertion does not apply Z.");
            if (p.Kind == "wall")
            {
                if (p.Input["offset"] != null && p.Input["base_offset"] != null) throw new ArgumentException("Use offset or base_offset, not both.");
                if (Math.Abs(p.Start.Z - p.End.Z) > GeometryInput.Tolerance) throw new ArgumentException("wall start/end must share one horizontal base plane.");
                p.Offset = p.Start.Z - p.Level.ProjectElevation;
                CheckOffset(p, p.Input["base_offset"] ?? p.Input["offset"]);
                p.TopLevel = Optional<Level>(doc, p.Input, "top_level_id");
                if (p.Input["top_offset"] != null && p.TopLevel == null) throw new ArgumentException("top_offset requires top_level_id.");
                p.TopOffset = (p.Input.Value<double?>("top_offset") ?? 0) * p.Scale;
                if (p.TopLevel != null && Math.Abs(p.TopLevel.ProjectElevation + p.TopOffset - p.Start.Z - p.Height) > GeometryInput.Tolerance)
                    throw new ArgumentException("height disagrees with top_level_id/top_offset and the requested base plane.");
            }
            if (p.Kind == "floor" || p.Kind == "ceiling" || p.Kind == "roof")
            {
                p.Offset = p.Loops[0].First().GetEndPoint(0).Z - p.Level.ProjectElevation;
                CheckOffset(p, p.Input["offset"]);
            }
            if (p.Kind == "family_instance" || p.Kind == "structural_column")
            {
                double z = GeometryInput.AbsoluteZ(p.Start.Z, p.Level?.ProjectElevation, p.Input.Value<string>("coordinate_mode"));
                p.Start = new XYZ(p.Start.X, p.Start.Y, z);
                p.Offset = z - (p.Level?.ProjectElevation ?? 0);
                p.Rotation = (p.Input.Value<double?>("rotation_degrees") ?? 0) * Math.PI / 180;
                p.Host = p.InstanceHost;
                var placement = ((FamilySymbol)p.Type).Family.FamilyPlacementType;
                if (p.Host != null && placement != FamilyPlacementType.OneLevelBasedHosted && placement != FamilyPlacementType.WorkPlaneBased)
                    throw new ArgumentException("host_id requires a hosted or work-plane-based family.");
                if (p.Host == null && placement != FamilyPlacementType.OneLevelBased && placement != FamilyPlacementType.TwoLevelsBased)
                    throw new ArgumentException("This family placement requires an explicit compatible host or a different placement route.");
            }
        }
        private static void CheckOffset(Plan p, JToken offset)
        {
            if (offset != null && Math.Abs(GeometryInput.Number(offset, "offset") * p.Scale - p.Offset) > GeometryInput.Tolerance)
                throw new ArgumentException("offset disagrees with absolute geometry Z minus the level elevation. Supply consistent coordinates and offset.");
        }
        private static void ReadRoofSlopes(Plan p)
        {
            int count = p.Loops[0].Count();
            p.Slopes = new double[count]; p.DefinesSlope = new bool[count];
            int modes = new[] { "slope_degrees", "slope_ratio", "edge_slopes" }.Count(f => p.Input[f] != null);
            if (modes > 1) throw new ArgumentException("Use exactly one of slope_degrees, slope_ratio or edge_slopes.");
            var edges = p.Input["edge_slopes"] as JArray;
            if (p.Input["edge_slopes"] != null && (edges == null || edges.Count != count))
                throw new ArgumentException("edge_slopes needs exactly one entry per perimeter edge, in input order.");
            for (int i = 0; i < count; i++)
            {
                JObject spec = edges == null ? p.Input : edges[i] as JObject;
                if (spec == null) throw new ArgumentException("edge_slopes[" + i + "] must be an object.");
                if (edges != null)
                {
                    if (spec.Properties().Any(f => f.Name != "defines_slope" && f.Name != "slope_degrees" && f.Name != "slope_ratio"))
                        throw new ArgumentException("Unknown edge_slopes field at edge " + i);
                    if (spec["defines_slope"]?.Type != JTokenType.Boolean) throw new ArgumentException("Each edge needs boolean defines_slope.");
                    if (spec["slope_degrees"] != null && spec["slope_ratio"] != null) throw new ArgumentException("An edge cannot use both slope units.");
                }
                double ratio = spec["slope_degrees"] != null ? GeometryInput.SlopeRatio(GeometryInput.Number(spec["slope_degrees"], "slope_degrees")) :
                    spec["slope_ratio"] == null ? 0 : GeometryInput.Number(spec["slope_ratio"], "slope_ratio");
                if (ratio < 0) throw new ArgumentException("slope_ratio must be non-negative.");
                bool defines = edges == null ? ratio > 0 : spec.Value<bool>("defines_slope");
                if (edges != null && (defines ? ratio <= 0 : ratio != 0)) throw new ArgumentException("A sloping edge needs a positive slope; a non-sloping edge cannot specify a nonzero slope.");
                p.Slopes[i] = ratio; p.DefinesSlope[i] = defines;
            }
        }
        private static int MatchEdge(Plan p, Curve found)
        {
            var edges = p.Loops[0].ToList();
            var matches = Enumerable.Range(0, edges.Count).Where(i => SameXYEdge(edges[i], found)).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Revit roof edge cannot be mapped unambiguously to the input perimeter.");
            return matches[0];
        }
        private static bool SameXYEdge(Curve a, Curve b)
        {
            bool Near(XYZ x, XYZ y) => Math.Abs(x.X - y.X) <= GeometryInput.Tolerance && Math.Abs(x.Y - y.Y) <= GeometryInput.Tolerance;
            return (Near(a.GetEndPoint(0), b.GetEndPoint(0)) && Near(a.GetEndPoint(1), b.GetEndPoint(1))) ||
                   (Near(a.GetEndPoint(0), b.GetEndPoint(1)) && Near(a.GetEndPoint(1), b.GetEndPoint(0)));
        }
        private static void SetDouble(Element element, BuiltInParameter name, double value)
        {
            var parameter = element.get_Parameter(name);
            if (parameter == null || parameter.IsReadOnly || !parameter.Set(value)) throw new InvalidOperationException(name + " could not be applied.");
        }
        private static void PositionInstance(Document doc, Plan p, FamilyInstance instance)
        {
            doc.Regenerate();
            if (!(instance.Location is LocationPoint point)) throw new InvalidOperationException("Family has no point placement to verify.");
            XYZ delta = p.Start - point.Point;
            if (delta.GetLength() > GeometryInput.Tolerance) ElementTransformUtils.MoveElement(doc, instance.Id, delta);
            if (p.Input["rotation_degrees"] != null)
                ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(p.Start, p.Start + XYZ.BasisZ), p.Rotation - point.Rotation);
        }

        private static CommandResult ApplyPlans(Document doc, JObject request, List<Plan> plans, int requested, bool rehearsal = false)
        {
            if (plans.Count == 1 && plans[0].Kind == "stairs") return ApplyStairs(doc, plans[0], rehearsal);
            string name = request.Value<string>("transaction_name") ?? "Horizun: create elements";
            var created = new List<Created>(); var rows = new JArray(); bool started = false;
            int index = -1; string phase = "start";
            using (var group = new TransactionGroup(doc, name))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Transaction group did not start.");
                    using (var tx = new Transaction(doc, name))
                    {
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("Transaction did not start.");
                        started = true; phase = "create";
                        foreach (Plan plan in plans)
                        {
                            index = plan.Index;
                            Element element = Create(doc, plan, created);
                            if (element == null) throw new InvalidOperationException("Creation returned no element.");
                            RecordCreated(doc, plan, element, created);
                            ApplyInstanceParameters(element, plan);
                            if (plan.Input["source_reference"] is JObject trace) SourceTraceStorage.Write(element, trace);
                        }
                        doc.Regenerate(); phase = "commit"; Guard.Commit(tx, name);
                    }
                    phase = "postcondition";
                    foreach (Created made in created)
                    {
                        index = made.Index; var row = VerifyCreated(doc, made); rows.Add(row);
                        if (row.Value<bool>("verified") != true) throw new InvalidOperationException("Requested properties do not match the committed element.");
                    }
                    if (rehearsal)
                    {
                        phase = "rehearsal_rollback";
                        var rolled = Guard.RollBack(group);
                        bool absent = rolled.Confirmed && created.All(x => doc.GetElement(x.Id) == null);
                        if (!absent) throw new InvalidOperationException("Rehearsal rollback could not be verified.");
                        return CommandResult.Ok(new JObject
                        {
                            ["dry_run"] = true,
                            ["transaction_status"] = rolled.StatusName,
                            ["changes_applied"] = false,
                            ["provisional_elements_absent"] = true,
                            ["provisional_verification"] = rows
                        });
                    }
                    phase = "assimilate"; Guard.Assimilate(group, name);
                }
                catch (Exception ex)
                {
                    string rollback = "not_attempted", rollbackError = null;
                    try { if (group.GetStatus() == TransactionStatus.Started) rollback = Guard.RollBack(group).StatusName; }
                    catch (Exception rb) { rollback = "failed"; rollbackError = rb.Message; }
                    bool? changed = started ? (bool?)null : false;
                    if (rollback == "RolledBack")
                    {
                        try { changed = created.Any(x => doc.GetElement(x.Id) != null) ? (bool?)null : false; }
                        catch { changed = null; }
                    }
                    return CommandResult.FailWithDetail("Atomic creation failed at item " + index + ": " + ex.Message, new JObject
                    {
                        ["code"] = phase == "postcondition" ? "geometry_postcondition_failed" : "revit_creation_failed",
                        ["tool"] = "horizun_create_elements",
                        ["operation"] = "create",
                        ["index"] = index,
                        ["phase"] = phase,
                        ["exception_type"] = ex.GetType().FullName,
                        ["exception_message"] = ex.Message,
                        ["exception_stack_trace"] = ex.StackTrace,
                        ["write_started"] = started,
                        ["changes_applied"] = changed,
                        ["transaction_status"] = group.GetStatus().ToString(),
                        ["rollback_status"] = rollback,
                        ["rollback_error"] = rollbackError,
                        ["verification"] = rows
                    });
                }
            }
            // Fresh reads after the outermost commit too. Failure here is reported as
            // committed/unverified: it cannot be erased by pretending rollback is possible.
            rows = new JArray(created.Select(x => VerifyCreated(doc, x)));
            int verified = rows.Count(r => r.Value<bool>("verified"));
            if (verified != created.Count) return CommandResult.FailWithDetail("Post-assimilation verification failed; inspect the model.", new JObject
            { ["code"] = "postcommit_verification_failed", ["write_started"] = true, ["changes_applied"] = true, ["transaction_status"] = "Committed", ["verification"] = rows });
            var result = new JObject
            {
                ["dry_run"] = false,
                ["transaction_status"] = "Committed",
                ["transaction_name"] = name,
                ["requested"] = requested,
                ["created_verified"] = verified,
                ["rows"] = rows,
                ["verification"] = new JObject { ["intended"] = requested, ["actual"] = verified, ["verified"] = verified == requested }
            };
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, requested, verified, verified, 0, 0, 0);
            return CommandResult.Ok(result);
        }

        private static void ApplyInstanceParameters(Element element, Plan p)
        {
            p.ParameterWrites = new List<ManageSystemTypesCommand.Write>();
            if (!(p.Input["parameters"] is JObject parameters)) return;
            foreach (var property in parameters.Properties())
            {
                var parameter = ManageSystemTypesCommand.ResolveParameter(element, property.Name, out string why);
                if (parameter == null || parameter.IsReadOnly) throw new ArgumentException("parameters." + property.Name + ": " + (why ?? "read-only"));
                ManageSystemTypesCommand.ValidateValue(parameter, property.Value);
                var write = new ManageSystemTypesCommand.Write { Spec = property.Name, Requested = property.Value.DeepClone() };
                ManageSystemTypesCommand.Apply(parameter, write); p.ParameterWrites.Add(write);
            }
        }

        private static JObject VerifyCreated(Document doc, Created made)
        {
            try { return ReadCreated(doc, made); }
            catch (Exception ex) { return new JObject { ["index"] = made.Index, ["element_id"] = Rid.Value(made.Id), ["verified"] = false, ["error"] = ex.Message, ["measurement_complete"] = false }; }
        }

        private static Created BatchElbowAt(Created made, XYZ requested)
        {
            return made.Batch?.FirstOrDefault(c => c.Plan.FittingSubtype == "elbow" &&
                c.ExpectedConnected?.Any(m => m.Owner?.Id == made.Id && m.Fact != null &&
                    new XYZ(m.Fact.X, m.Fact.Y, m.Fact.Z).DistanceTo(requested) <= GeometryInput.Tolerance) == true);
        }

        private static XYZ ReadElbowJunction(Document doc, Created made, Created fitting, int end)
        {
            var curve = (MEPCurve)doc.GetElement(made.Id);
            XYZ physical = ((LocationCurve)curve.Location).Curve.GetEndPoint(end);
            XYZ requested = end == 0 ? made.Plan.Start : made.Plan.End;
            XYZ other = end == 0 ? made.Plan.End : made.Plan.Start;
            XYZ outward = (requested-other).Normalize();
            double trim = (requested-physical).DotProduct(outward);
            if ((physical-other).CrossProduct(outward).GetLength() > GeometryInput.Tolerance ||
                trim < -GeometryInput.Tolerance || trim >= requested.DistanceTo(other))
                throw new InvalidOperationException("The elbow moved its run off the requested axis or outside the requested segment.");
            var ports = MepFacts.Ordered(MepFacts.ManagerOf(doc.GetElement(fitting.Id)))
                .Where(c => c.ConnectorType == ConnectorType.End).ToList();
            if (ports.Count != 2) throw new InvalidOperationException("An elbow must expose exactly two physical end connectors.");
            bool attached = MepFacts.Ordered(curve.ConnectorManager).Any(c =>
                c.Origin.DistanceTo(physical) <= GeometryInput.Tolerance &&
                ports.Any(p => p.Origin.DistanceTo(physical) <= GeometryInput.Tolerance && p.IsConnectedTo(c)));
            if (!attached || ports.Any(c => !c.IsConnected))
                throw new InvalidOperationException("The measured run end is not connected to the planned elbow.");
            ConnectorFact Axis(Connector c)
            {
                XYZ origin = c.Origin, direction = c.CoordinateSystem.BasisZ;
                return new ConnectorFact { X=origin.X, Y=origin.Y, Z=origin.Z,
                    DirX=direction.X, DirY=direction.Y, DirZ=direction.Z };
            }
            double[] intersection = MepRules.AxisIntersection(Axis(ports[0]), Axis(ports[1]), GeometryInput.Tolerance);
            if (intersection == null) throw new InvalidOperationException("The elbow connector axes do not define one measurable junction.");
            return new XYZ(intersection[0], intersection[1], intersection[2]);
        }
        private static JObject ReadCreated(Document doc, Created made)
        {
            Plan p = made.Plan;
            var expected = new Dictionary<string, JToken>(); var reads = new Dictionary<string, Func<JToken>>();
            var tolerances = new Dictionary<string, double>();
            void Exact(string field, JToken value, Func<JToken> read) { expected.Add(field, value); reads.Add(field, read); }
            void Numeric(string field, double value, Func<double> read, double tolerance = GeometryInput.Tolerance)
            { Exact(field, value, () => read()); tolerances.Add(field, tolerance); }
            Element e = null;
            try { e = doc.GetElement(made.Id); } catch { }
            Exact("kind", p.Kind, () => KindMatches(e, p.Kind) ? p.Kind : e?.GetType().Name);
            if (p.Type != null) Exact("type_id", Rid.Value(p.Type.Id), () => Rid.Value(e.GetTypeId()));
            if (p.Level != null)
            {
                Exact("level_id", Rid.Value(p.Level.Id), () => Rid.Value(e is MEPCurve mep ? mep.ReferenceLevel.Id : e is BeamSystem beamSystem ? beamSystem.Level.Id : e.LevelId));
                Numeric("level_elevation", p.Level.ProjectElevation, () => ((Level)doc.GetElement(p.Level.Id)).ProjectElevation);
            }
            if (p.WantName != null) Exact("name", p.WantName, () => IdentityOf(e, p.Kind, false));
            if (p.Kind == "wall_profile")
            {
                Exact("profile_world_silhouette", true, () => ProfileWallMatches(e, p));
                Numeric("profile_plane_distance", 0, () => (((LocationCurve)e.Location).Curve.Evaluate(0.5, true) - p.ProfileOrigin).DotProduct(p.ProfileNormal));
                Exact("structural", p.Input.Value<bool?>("structural") ?? false, () => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT).AsInteger() == 1);
            }
            if (p.Kind == "displacement")
            {
                Exact("view_id", Rid.Value(p.OwnerView.Id), () => Rid.Value(e.OwnerViewId));
                Exact("element_ids", new JArray(p.DisplacedIds.Select(Rid.Value).OrderBy(x => x)), () => new JArray(((DisplacementElement)e).GetDisplacedElementIds().Select(Rid.Value).OrderBy(x => x)));
                Exact("physical_bounds_unchanged", p.DisplacedState, () => DisplacementSourceState(doc, p.DisplacedIds));
                for (int axis = 0; axis < 3; axis++) { int a = axis; Numeric("displacement_" + "xyz"[a], p.Displacement[a], () => ((DisplacementElement)e).GetAbsoluteDisplacement()[a]); }
            }
            if (p.Kind == "level") Numeric("elevation", p.Elevation, () => ((Level)e).ProjectElevation);
            if (p.Kind == "wall_opening")
            {
                Exact("opening_corners", true, () =>
                {
                    var corners = ((Opening)e).BoundaryRect;
                    return corners.Count == 2 &&
                        ((corners[0].DistanceTo(p.Start) <= GeometryInput.Tolerance && corners[1].DistanceTo(p.End) <= GeometryInput.Tolerance) ||
                         (corners[1].DistanceTo(p.Start) <= GeometryInput.Tolerance && corners[0].DistanceTo(p.End) <= GeometryInput.Tolerance));
                });
            }
            if (p.Start != null && p.Kind != "wall_opening")
            {
                Created elbow = e is MEPCurve ? BatchElbowAt(made, p.Start) : null;
                XYZ PointNow() => elbow != null ? ReadElbowJunction(doc, made, elbow, 0) : e is Grid grid ? grid.Curve.GetEndPoint(0) : e.Location is LocationCurve curve ? curve.Curve.GetEndPoint(0) : ((LocationPoint)e.Location).Point;
                for (int axis = 0; axis < (p.Kind == "room" ? 2 : 3); axis++)
                { int a = axis; Numeric((elbow == null ? "start_" : "start_junction_") + "xyz"[a], p.Start[a], () => PointNow()[a]); }
            }
            if (p.End != null && p.Kind != "wall_opening")
            {
                Created elbow = e is MEPCurve ? BatchElbowAt(made, p.End) : null;
                for (int axis = 0; axis < 3; axis++)
                { int a = axis; Numeric((elbow == null ? "end_" : "end_junction_") + "xyz"[a], p.End[a], () => elbow != null ? ReadElbowJunction(doc, made, elbow, 1)[a] : (e is Grid grid ? grid.Curve : ((LocationCurve)e.Location).Curve).GetEndPoint(1)[a]); }
            }
            if (p.Kind == "wall")
            {
                Numeric("height", p.Height, () => e.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble());
                Numeric("offset", p.Offset, () => e.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble());
                Exact("flip", p.Input.Value<bool?>("flip") ?? false, () => ((Wall)e).Flipped);
                Exact("structural", p.Input.Value<bool?>("structural") ?? false, () => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT).AsInteger() == 1);
                if (p.TopLevel != null)
                {
                    Exact("top_level_id", Rid.Value(p.TopLevel.Id), () => Rid.Value(e.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId()));
                    Numeric("top_offset", p.TopOffset, () => e.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble());
                }
            }
            if (p.Kind == "family_instance" || p.Kind == "structural_column" || p.Kind == "structural_framing")
                Exact("structural_type", p.StructuralType.ToString(), () => ((FamilyInstance)e).StructuralType.ToString());
            if (p.Host != null) Exact("host_id", Rid.Value(p.Host.Id), () => Rid.Value(e is Opening opening ? opening.Host.Id : ((FamilyInstance)e).Host.Id));
            if (p.Input["rotation_degrees"] != null)
                Numeric("rotation", ((p.Rotation % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI),
                    () => ((((LocationPoint)e.Location).Rotation % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI), 1e-8);
            if (p.SystemType != null && (p.Kind == "duct" || p.Kind == "pipe"))
                Exact("system_type_id", Rid.Value(p.SystemType.Id), () => Rid.Value(e.get_Parameter(p.Kind == "duct" ? BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM : BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM).AsElementId()));
            if (p.Kind == "floor" || p.Kind == "ceiling" || p.Kind == "roof") AddProfileChecks(doc, p, e, Exact, Numeric);
            foreach (var write in p.ParameterWrites ?? new List<ManageSystemTypesCommand.Write>())
            {
                var w = write;
                if (w.Expected.Type == JTokenType.Float)
                    Numeric("parameters." + w.Spec, w.Expected.Value<double>(), () => ManageSystemTypesCommand.Read(ManageSystemTypesCommand.ResolveParameter(e, w.Spec, out _)).Value<double>(), 1e-9);
                else Exact("parameters." + w.Spec, w.Expected, () => ManageSystemTypesCommand.Read(ManageSystemTypesCommand.ResolveParameter(e, w.Spec, out _)));
            }
            var check = new PostconditionCheck(expected.Keys.ToArray());
            foreach (string field in expected.Keys)
            {
                try
                {
                    JToken actual = reads[field]();
                    if (tolerances.TryGetValue(field, out double tolerance))
                        check.Measure(field, expected[field].Value<double>(), actual.Value<double>(), tolerance,
                            field == "rotation" ? "radians" : field == "roof_projected_area" ? "square feet" : field.StartsWith("slope_") ? "rise/run" : field.StartsWith("parameters.") ? "Revit internal" : "feet", "Revit parameter/location/sketch/face readback");
                    else check.Record(field, expected[field], actual, JToken.DeepEquals(expected[field], actual));
                }
                catch (Exception ex) { check.Unreadable(field, expected[field], ex.Message); }
            }
            bool traceVerified = true; JObject traceComparison = null;
            if (p.Input["source_reference"] is JObject reference)
            {
                try
                {
                    traceComparison = SourceTrace.Compare(reference, check.ToJson());
                    traceVerified = JToken.DeepEquals(reference, SourceTraceStorage.Read(e)) && traceComparison.Value<bool>("matches");
                }
                catch (Exception ex) { traceVerified = false; traceComparison = new JObject { ["matches"] = false, ["error"] = ex.Message }; }
            }
            var row = new JObject
            {
                ["index"] = p.Index,
                ["kind"] = p.Kind,
                ["element_id"] = Rid.Value(made.Id),
                ["unique_id"] = e?.UniqueId,
                ["present_after_commit"] = e != null,
                ["verified"] = check.AllVerified && traceVerified,
                ["postconditions"] = check.ToJson(),
                ["source_comparison"] = traceComparison
            };
            if (e?.Location is LocationPoint point && p.Level != null)
            {
                row["coordinate_reference"] = "internal_origin"; row["absolute_z_feet"] = point.Point.Z;
                row["level_elevation_feet"] = p.Level.ProjectElevation; row["offset_feet"] = point.Point.Z - p.Level.ProjectElevation;
            }
            if (e is MEPCurve physicalRun && physicalRun.Location is LocationCurve physicalCurve)
            {
                XYZ a=physicalCurve.Curve.GetEndPoint(0), b=physicalCurve.Curve.GetEndPoint(1);
                row["physical_start_feet"] = new JArray(a.X,a.Y,a.Z);
                row["physical_end_feet"] = new JArray(b.X,b.Y,b.Z);
                row["endpoint_verification"] = "Same-batch elbows: requested junctions are compared to intersections of committed connector axes; physical endpoints and attachment are measured separately. Other endpoints compare directly.";
            }
            if (e is FootPrintRoof diagnosticRoof)
            {
                try
                {
                    row["roof_top_face_normals"] = new JArray(HostObjectUtils.GetTopFaces(diagnosticRoof)
                        .Select(r => diagnosticRoof.GetGeometryObjectFromReference(r) as PlanarFace)
                        .Select(f => f == null ? JValue.CreateNull() : (JToken)new JArray(f.FaceNormal.X, f.FaceNormal.Y, f.FaceNormal.Z)));
                    row["roof_top_face_details"] = new JArray(HostObjectUtils.GetTopFaces(diagnosticRoof).Select(r =>
                    {
                        var geometry = diagnosticRoof.GetGeometryObjectFromReference(r);
                        var face = geometry as Face;
                        return new JObject { ["type"] = geometry?.GetType().FullName, ["area"] = face?.Area,
                            ["reference"] = r.ConvertToStableRepresentation(doc) };
                    }));
                }
                catch (Exception ex) { row["roof_geometry_read_error"] = ex.Message; }
            }
            if (e is Wall profileWall && p.Kind == "wall_profile")
            {
                try
                {
                    row["profile_side_face_vertices_feet"] = new JArray(HostObjectUtils.GetSideFaces(profileWall, ShellLayerType.Exterior)
                        .Select(r => profileWall.GetGeometryObjectFromReference(r) as Face).Where(f => f != null)
                        .SelectMany(f => f.GetEdgesAsCurveLoops()).SelectMany(l => l)
                        .Select(c => c.GetEndPoint(0)).Select(v => new JArray(v.X, v.Y, v.Z)));
                }
                catch (Exception ex) { row["profile_geometry_read_error"] = ex.Message; }
            }
            var production = VerifyProductionProperties(doc, made);
            foreach (var property in production.Properties())
                if (row[property.Name] == null) row[property.Name] = property.Value.DeepClone();
            row["verified"] = row.Value<bool>("verified") && production.Value<bool>("verified");
            return row;
        }

        private static List<PlanarFace> RoofPhysicalTopFaces(Element roof)
        {
            var result = new List<PlanarFace>();
            void Visit(GeometryElement geometry)
            {
                if (geometry == null) throw new InvalidOperationException("Roof solid geometry is unavailable.");
                foreach (GeometryObject item in geometry)
                {
                    if (item is GeometryInstance instance) { Visit(instance.GetInstanceGeometry()); continue; }
                    if (!(item is Solid solid) || solid.Volume <= 0) continue;
                    foreach (Face face in solid.Faces)
                    {
                        if (!(face is PlanarFace planar)) throw new InvalidOperationException("Non-planar roof solid requires a different surface verification route.");
                        if (planar.FaceNormal.Z > 1e-9) result.Add(planar);
                    }
                }
            }
            Visit(roof.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }));
            if (result.Count == 0) throw new InvalidOperationException("Roof has no physical upward faces.");
            return result;
        }

        private static void AddProfileChecks(Document doc, Plan p, Element e,
            Action<string, JToken, Func<JToken>> exact, Action<string, double, Func<double>, double> numeric)
        {
            BuiltInParameter offset = p.Kind == "floor" ? BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM :
                p.Kind == "ceiling" ? BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM : BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM;
            numeric("offset", p.Offset, () => e.get_Parameter(offset).AsDouble(), GeometryInput.Tolerance);
            exact("profile_xy", true, () =>
            {
                List<List<Curve>> actual;
                if (e is FootPrintRoof roof) actual = roof.GetProfiles().Cast<ModelCurveArray>().Select(loop => loop.Cast<ModelCurve>().Select(x => x.GeometryCurve).ToList()).ToList();
                else
                {
                    ElementId sketchId = e is Floor floor ? floor.SketchId : ((Ceiling)e).SketchId;
                    var sketch = (Sketch)doc.GetElement(sketchId);
                    actual = sketch.Profile.Cast<CurveArray>().Select(loop => loop.Cast<Curve>().ToList()).ToList();
                }
                var desired = p.Loops.Select(loop => loop.ToList()).ToList();
                if (actual.Count != desired.Count) return false;
                foreach (var loop in desired)
                {
                    int match = actual.FindIndex(other => other.Count == loop.Count && loop.All(c => other.Count(d => SameXYEdge(c, d)) == 1));
                    if (match < 0) return false; actual.RemoveAt(match);
                }
                return true;
            });
            if (p.Kind == "floor" || p.Kind == "ceiling")
                numeric("reference_face_elevation", p.Level.ProjectElevation + p.Offset, () =>
                {
                    var references = p.Kind == "floor" ? HostObjectUtils.GetTopFaces((HostObject)e) : HostObjectUtils.GetBottomFaces((HostObject)e);
                    var faces = references.Select(r => e.GetGeometryObjectFromReference(r) as PlanarFace).ToList();
                    if (faces.Count == 0 || faces.Any(f => f == null || Math.Abs(Math.Abs(f.FaceNormal.Z) - 1) > 1e-8)) throw new InvalidOperationException("No fully horizontal reference faces.");
                    double z = faces[0].Origin.Z;
                    if (faces.Any(f => Math.Abs(f.Origin.Z - z) > GeometryInput.Tolerance)) throw new InvalidOperationException("Reference faces have different elevations.");
                    return z;
                }, GeometryInput.Tolerance);
            if (p.Kind == "roof")
            {
                exact("roof_face_slopes", true, () =>
                {
                    var faces = RoofPhysicalTopFaces(e);
                    var observed = faces.Select(f => Math.Sqrt(f.FaceNormal.X * f.FaceNormal.X + f.FaceNormal.Y * f.FaceNormal.Y) / Math.Abs(f.FaceNormal.Z)).ToList();
                    var desired = p.Slopes.Where((s, i) => p.DefinesSlope[i]).ToList(); if (desired.Count == 0) desired.Add(0);
                    return desired.All(s => observed.Any(a => Math.Abs(a - s) <= 1e-8)) && observed.All(a => desired.Any(s => Math.Abs(a - s) <= 1e-8));
                });
                double footprintArea = Math.Abs(p.Loops[0].Sum(c =>
                    c.GetEndPoint(0).X * c.GetEndPoint(1).Y - c.GetEndPoint(1).X * c.GetEndPoint(0).Y)) / 2;
                numeric("roof_projected_area", footprintArea,
                    () => RoofPhysicalTopFaces(e).Sum(f => f.Area * f.FaceNormal.Z), Math.Max(1e-6, footprintArea * 1e-8));
                for (int i = 0; i < p.Slopes.Length; i++)
                {
                    int edge = i;
                    ModelCurve ReadEdge() => ((FootPrintRoof)e).GetProfiles().Cast<ModelCurveArray>().SelectMany(a => a.Cast<ModelCurve>()).Single(c => MatchEdge(p, c.GeometryCurve) == edge);
                    exact("defines_slope_" + edge, p.DefinesSlope[edge], () => ((FootPrintRoof)e).get_DefinesSlope(ReadEdge()));
                    if (p.DefinesSlope[edge]) numeric("slope_" + edge, p.Slopes[edge], () => ((FootPrintRoof)e).get_SlopeAngle(ReadEdge()), 1e-8);
                }
            }
        }
    }
}
