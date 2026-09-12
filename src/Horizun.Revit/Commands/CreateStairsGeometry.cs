using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private sealed class StairRunSpec { public XYZ Start, End; public double Width; public int Risers; public ElementId Id; }
        private sealed class StairLandingSpec { public CurveLoop Profile; public double AbsoluteZ; public ElementId Id; }
        private static void PlanStairs(Document doc, Plan p)
        {
            p.Level = Need<Level>(doc, p.Input, "level_id"); p.TopLevel = Need<Level>(doc, p.Input, "top_level_id"); p.Type = Need<StairsType>(doc, p.Input, "type_id");
            p.Height = p.TopLevel.ProjectElevation - p.Level.ProjectElevation;
            if (p.Height <= 0) throw new ArgumentException("Stairs top level must be above the base level.");
            if (p.Input["desired_risers"]?.Type != JTokenType.Integer) throw new ArgumentException("desired_risers must be an integer.");
            p.DesiredRisers = p.Input.Value<int>("desired_risers");
            if (p.DesiredRisers < 1 || p.DesiredRisers > 1000) throw new ArgumentException("desired_risers must be 1..1000.");
            p.TreadDepth = GeometryInput.Number(p.Input["tread_depth"], "tread_depth") * p.Scale;
            if (p.TreadDepth <= doc.Application.ShortCurveTolerance) throw new ArgumentException("tread_depth must be positive.");
            if (!(p.Input["runs"] is JArray runs) || runs.Count < 1 || runs.Count > 50) throw new ArgumentException("runs must contain 1..50 straight runs.");
            p.StairRuns = new List<StairRunSpec>();
            foreach (var token in runs)
            {
                if (!(token is JObject run) || run.Properties().Any(f => !new[] { "start", "end", "width", "expected_risers" }.Contains(f.Name))) throw new ArgumentException("Invalid run fields.");
                var r = new StairRunSpec { Start = Point(run["start"], p.Scale, true), End = Point(run["end"], p.Scale, true), Width = GeometryInput.Number(run["width"], "width") * p.Scale };
                if (run["expected_risers"]?.Type != JTokenType.Integer) throw new ArgumentException("Each run needs expected_risers.");
                r.Risers = run.Value<int>("expected_risers");
                if (r.Risers < 1 || r.Width <= 0 || r.Start.DistanceTo(r.End) <= doc.Application.ShortCurveTolerance || Math.Abs(r.Start.Z - r.End.Z) > GeometryInput.Tolerance)
                    throw new ArgumentException("Invalid run dimensions: use a horizontal location path with positive width and riser count.");
                if (Math.Abs(r.Start.Z - (p.Level.ProjectElevation + p.StairRuns.Sum(x => x.Risers) * p.Height / p.DesiredRisers)) > GeometryInput.Tolerance)
                    throw new ArgumentException("Run start elevation must equal the previous runs' cumulative rise from the base level.");
                p.StairRuns.Add(r);
            }
            if (p.StairRuns.Sum(x => x.Risers) != p.DesiredRisers) throw new ArgumentException("Run expected_risers must sum to desired_risers.");
            p.StairLandings = new List<StairLandingSpec>();
            if (p.Input["landings"] != null)
            {
                if (!(p.Input["landings"] is JArray landings) || landings.Count > 49) throw new ArgumentException("landings must be an array with at most 49 entries.");
                foreach (var token in landings)
                {
                    if (!(token is JObject landing) || landing.Properties().Any(f => f.Name != "profile")) throw new ArgumentException("Landing accepts only profile.");
                    var loops = Loops(landing["profile"], p.Scale, doc.Application.ShortCurveTolerance);
                    if (loops.Count != 1) throw new ArgumentException("A landing requires one contour without holes.");
                    double z = loops[0].First().GetEndPoint(0).Z;
                    if (z <= p.Level.ProjectElevation || z >= p.TopLevel.ProjectElevation) throw new ArgumentException("Landing must be between base and top levels.");
                    p.StairLandings.Add(new StairLandingSpec { Profile = loops[0], AbsoluteZ = z });
                }
            }
            if (p.StairLandings.Count != p.StairRuns.Count - 1) throw new ArgumentException("Supply one explicit landing between consecutive runs.");
        }
        private sealed class StairFailures : IFailuresPreprocessor
        {
            public readonly JArray Records = new JArray();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool errors = false;
                foreach (var failure in accessor.GetFailureMessages())
                {
                    Records.Add(new JObject { ["description"] = failure.GetDescriptionText(), ["severity"] = failure.GetSeverity().ToString(), ["id"] = failure.GetFailureDefinitionId().Guid.ToString() });
                    if (failure.GetSeverity() == FailureSeverity.Error) errors = true;
                }
                return errors ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
        private static CommandResult ApplyStairs(Document doc, Plan p, bool rehearsal)
        {
            ElementId stairsId = null; var failures = new StairFailures(); JObject checks = null;
            using (var group = new TransactionGroup(doc, "Horizun: stairs"))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("Stairs transaction group did not start.");
                    using (var scope = new StairsEditScope(doc, "Horizun: stairs edit"))
                    {
                        stairsId = scope.Start(p.Level.Id, p.TopLevel.Id);
                        using (var tx = new Transaction(doc, "Horizun: runs and landings"))
                        {
                            tx.Start(); var stairs = (Stairs)doc.GetElement(stairsId);
                            stairs.ChangeTypeId(p.Type.Id); stairs.DesiredRisersNumber = p.DesiredRisers; stairs.ActualTreadDepth = p.TreadDepth;
                            foreach (var run in p.StairRuns)
                            {
                                var created = StairsRun.CreateStraightRun(doc, stairsId, Line.CreateBound(run.Start, run.End), StairsRunJustification.Center);
                                created.ActualRunWidth = run.Width; run.Id = created.Id;
                            }
                            foreach (var landing in p.StairLandings)
                            {
                                var flat = CurveLoop.CreateViaTransform(landing.Profile, Transform.CreateTranslation(new XYZ(0, 0, p.Level.ProjectElevation - landing.AbsoluteZ)));
                                landing.Id = StairsLanding.CreateSketchedLanding(doc, stairsId, flat, landing.AbsoluteZ - p.Level.ProjectElevation).Id;
                            }
                            ApplyInstanceParameters(stairs, p);
                            if (p.Input["source_reference"] is JObject trace) SourceTraceStorage.Write(stairs, trace);
                            Guard.Commit(tx, "stairs geometry");
                        }
                        scope.Commit(failures);
                    }
                    checks = VerifyStairs(doc, p, stairsId);
                    if (checks.Value<bool>("verified") != true) throw new InvalidOperationException("Stairs geometry, dimensions or connectivity did not match the request.");
                    if (rehearsal)
                    {
                        var rb = Guard.RollBack(group);
                        if (!rb.Confirmed || doc.GetElement(stairsId) != null) throw new InvalidOperationException("Stairs rehearsal rollback was not verified.");
                        return CommandResult.Ok(new JObject { ["transaction_status"] = rb.StatusName, ["changes_applied"] = false, ["provisional_elements_absent"] = true, ["provisional_verification"] = checks, ["failures"] = failures.Records });
                    }
                    Guard.Assimilate(group, "stairs");
                }
                catch (Exception ex)
                {
                    string rb = "not_attempted", rbError = null;
                    try { if (group.GetStatus() == TransactionStatus.Started) rb = Guard.RollBack(group).StatusName; }
                    catch (Exception error) { rb = "failed"; rbError = error.Message; }
                    return CommandResult.FailWithDetail("Stairs creation failed: " + ex.Message, new JObject
                    {
                        ["code"] = "stairs_creation_failed",
                        ["exception_type"] = ex.GetType().FullName,
                        ["exception_message"] = ex.Message,
                        ["write_started"] = stairsId != null,
                        ["changes_applied"] = rb == "RolledBack" ? (JToken)false : null,
                        ["rollback_status"] = rb,
                        ["rollback_error"] = rbError,
                        ["verification"] = checks,
                        ["failures"] = failures.Records
                    });
                }
            }
            checks = VerifyStairs(doc, p, stairsId);
            if (!checks.Value<bool>("verified")) return CommandResult.FailWithDetail("Post-assimilation stairs verification failed.", new JObject { ["changes_applied"] = true, ["verification"] = checks });
            var result = new JObject { ["transaction_status"] = "Committed", ["created_verified"] = 1, ["rows"] = new JArray(checks), ["failures"] = failures.Records };
            ApplicationOutcome.StampApplied(result, ApplicationOutcome.Committed, 1, 1, 1, 0, 0, 0); return CommandResult.Ok(result);
        }
        private static JObject VerifyStairs(Document doc, Plan p, ElementId id)
        {
            try { return ReadStairs(doc, p, id); }
            catch (Exception ex) { return new JObject { ["element_id"] = Rid.Value(id), ["verified"] = false, ["measurement_complete"] = false, ["error"] = ex.Message }; }
        }
        private static JObject ReadStairs(Document doc, Plan p, ElementId id)
        {
            var expected = new Dictionary<string, JToken>(); var reads = new Dictionary<string, Func<JToken>>(); var units = new Dictionary<string, string>();
            void Exact(string key, JToken value, Func<JToken> read) { expected.Add(key, value); reads.Add(key, read); }
            void Number(string key, double value, Func<double> read) { Exact(key, value, () => read()); units.Add(key, "feet"); }
            var stairs = doc.GetElement(id) as Stairs;
            Exact("type_id", Rid.Value(p.Type.Id), () => Rid.Value(stairs.GetTypeId()));
            Exact("level_id", Rid.Value(p.Level.Id), () => Rid.Value(stairs.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM).AsElementId()));
            Exact("top_level_id", Rid.Value(p.TopLevel.Id), () => Rid.Value(stairs.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM).AsElementId()));
            Number("base_elevation", p.Level.ProjectElevation, () => stairs.BaseElevation); Number("top_elevation", p.TopLevel.ProjectElevation, () => stairs.TopElevation);
            Number("height", p.Height, () => stairs.Height); Number("tread_depth", p.TreadDepth, () => stairs.ActualTreadDepth);
            Number("riser_height", p.Height / p.DesiredRisers, () => stairs.ActualRiserHeight);
            Exact("risers", p.DesiredRisers, () => stairs.ActualRisersNumber);
            Exact("run_count", p.StairRuns.Count, () => stairs.GetStairsRuns().Count);
            Exact("landing_count", p.StairLandings.Count, () => stairs.GetStairsLandings().Count);
            for (int i = 0; i < p.StairRuns.Count; i++)
            {
                var run = p.StairRuns[i]; string key = "run_" + i + "_";
                StairsRun Read() => (StairsRun)doc.GetElement(run.Id);
                Exact(key + "risers", run.Risers, () => Read().ActualRisersNumber);
                Number(key + "width", run.Width, () => Read().ActualRunWidth);
                Number(key + "base_elevation", run.Start.Z, () => stairs.BaseElevation + Read().BaseElevation);
                Number(key + "height", run.Risers * p.Height / p.DesiredRisers, () => Read().Height);
                Exact(key + "path_xy", true, () =>
                {
                    var path = Read().GetStairsPath().ToList();
                    return path.Count == 1 && SameXYEdge(Line.CreateBound(run.Start, run.End), path[0]);
                });
                var requiredPeers = new List<ElementId>();
                if (i > 0) requiredPeers.Add(p.StairLandings[i - 1].Id);
                if (i < p.StairLandings.Count) requiredPeers.Add(p.StairLandings[i].Id);
                Exact(key + "connected_to_requested_landings", true, () =>
                {
                    var connected = Read().GetConnections().Select(c => c.PeerElementId).ToList();
                    return requiredPeers.All(peer => connected.Contains(peer));
                });
            }
            for (int i = 0; i < p.StairLandings.Count; i++)
            {
                var landing = p.StairLandings[i]; string key = "landing_" + i + "_";
                StairsLanding Read() => (StairsLanding)doc.GetElement(landing.Id);
                Number(key + "elevation", landing.AbsoluteZ, () => stairs.BaseElevation + Read().BaseElevation);
                Exact(key + "profile_xy", true, () =>
                {
                    var actual = Read().GetFootprintBoundary().ToList(); var desired = landing.Profile.ToList();
                    double[] Segment(Curve c) => new[] { c.GetEndPoint(0).X, c.GetEndPoint(0).Y, c.GetEndPoint(1).X, c.GetEndPoint(1).Y };
                    return actual.All(c => c is Line) && GeometryInput.SameBoundaryXY(actual.Select(Segment).ToList(), desired.Select(Segment).ToList());
                });
            }
            foreach (var write in p.ParameterWrites ?? new List<ManageSystemTypesCommand.Write>())
                Exact("parameters." + write.Spec, write.Expected, () => ManageSystemTypesCommand.Read(ManageSystemTypesCommand.ResolveParameter(stairs, write.Spec, out _)));
            var check = new PostconditionCheck(expected.Keys.ToArray());
            foreach (var pair in expected)
            {
                try { var found = reads[pair.Key](); if (units.ContainsKey(pair.Key)) check.Measure(pair.Key, pair.Value.Value<double>(), found.Value<double>(), GeometryInput.Tolerance, "feet", "Revit stairs geometry"); else check.Record(pair.Key, pair.Value, found, JToken.DeepEquals(pair.Value, found)); }
                catch (Exception ex) { check.Unreadable(pair.Key, pair.Value, ex.Message); }
            }
            bool traceOk = true; JObject comparison = null;
            if (p.Input["source_reference"] is JObject trace)
            {
                comparison = SourceTrace.Compare(trace, check.ToJson()); traceOk = comparison.Value<bool>("matches") && JToken.DeepEquals(trace, SourceTraceStorage.Read(stairs));
            }
            return new JObject
            {
                ["element_id"] = Rid.Value(id),
                ["kind"] = "stairs",
                ["verified"] = check.AllVerified && traceOk,
                ["postconditions"] = check.ToJson(),
                ["source_comparison"] = comparison,
                ["run_ids"] = new JArray(p.StairRuns.Select(r => Rid.Value(r.Id))),
                ["landing_ids"] = new JArray(p.StairLandings.Select(l => Rid.Value(l.Id))),
                ["landing_boundaries_feet"] = new JArray(p.StairLandings.Select(l => new JArray(
                    ((StairsLanding)doc.GetElement(l.Id)).GetFootprintBoundary().Select(c =>
                        new JArray(new JArray(c.GetEndPoint(0).X, c.GetEndPoint(0).Y, c.GetEndPoint(0).Z),
                            new JArray(c.GetEndPoint(1).X, c.GetEndPoint(1).Y, c.GetEndPoint(1).Z))))))
            };
        }
    }
}
