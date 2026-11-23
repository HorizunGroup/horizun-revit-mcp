using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private sealed class SizedFitting
        {
            public string Kind;
            public double First, Second;
        }

        private sealed class ReducerPorts
        {
            public (ElementId Owner, int Port) First, Second;
        }

        private sealed class ReducerPortVerificationFailed : InvalidOperationException
        {
            public const string Reason = "The reducer ports do not match the measured run diameters, approved diameters and run connections.";

            public ReducerPortVerificationFailed() : base(Reason) { }
        }

        private sealed class DiameterMember
        {
            public Plan Pending;
            public Element Existing;
            public double? Size => Pending != null ? Pending.Diameter : PositiveDiameter(DiameterOf(Existing));
            public bool CanSupplyConduitSize => Pending?.Kind == "conduit" || Pending?.Kind == "pipe" || Existing is Conduit || Existing is Pipe;
            public bool IsDeclaredConduit => Pending?.Kind == "conduit" && Pending.Input["diameter"] != null;
        }

        private static double? PositiveDiameter(double? value)
            => value.HasValue && value.Value > 0 && !double.IsInfinity(value.Value) ? value : null;

        private static MepGraphIssue ResolveConduitDiameters(Document doc, List<Plan> plans)
        {
            var byIndex = plans.ToDictionary(p => p.Index);
            var pairs = plans.Where(p => p.Kind == "fitting" &&
                (p.FittingSubtype == "elbow" || p.FittingSubtype == "union" || p.FittingSubtype == "transition"))
                .Select(p => (Fitting: p, Members: p.FittingMembers.Select(member =>
                {
                    var result = new DiameterMember { Existing = member.Owner };
                    if (member.BatchIndex.HasValue) byIndex.TryGetValue(member.BatchIndex.Value, out result.Pending);
                    return result;
                }).ToArray())).Where(p => p.Members.Length == 2).ToList();

            MepGraphIssue UnionRefusal(Plan fitting, DiameterMember[] members, double? first, double? second)
            {
                string reason = MepDiameterRules.DeclaredConduitUnionError(fitting.FittingSubtype,
                    members.Any(member => member.IsDeclaredConduit), first, second);
                return reason == null ? null : new MepGraphIssue
                {
                    Index = fitting.Index, Refusal = true, Code = "conduit_union_diameter_mismatch",
                    Message = reason + ". Request a transition explicitly or use matching diameters."
                };
            }

            // Refuse known mismatches before a default-size probe or construction can run.
            foreach (var pair in pairs)
            {
                var refusal = UnionRefusal(pair.Fitting, pair.Members, pair.Members[0].Size, pair.Members[1].Size);
                if (refusal != null) return refusal;
            }

            var unsized = plans.Where(p => p.Kind == "conduit" && !p.Diameter.HasValue).ToList();
            var peers = new Dictionary<Plan, List<DiameterMember>>();
            foreach (Plan conduit in unsized) peers.Add(conduit, new List<DiameterMember>());
            foreach (var pair in pairs.Where(p => p.Fitting.FittingSubtype != "transition"))
            {
                for (int i = 0; i < 2; i++)
                {
                    Plan pending = pair.Members[i].Pending;
                    DiameterMember peer = pair.Members[1 - i];
                    if (pending != null && peers.TryGetValue(pending, out List<DiameterMember> attached) && peer.CanSupplyConduitSize)
                        attached.Add(peer);
                }
            }
            // When a pending pipe supplies a conduit, use its measured default too.
            var pipeDefaults = new Dictionary<Plan, double>();
            double PipeDefault(Plan pipe)
            {
                if (!pipeDefaults.TryGetValue(pipe, out double size))
                    pipeDefaults.Add(pipe, size = MeasureRunDefault(doc, pipe));
                return size;
            }
            double? ConnectedSize(Plan plan)
            {
                double? known = peers[plan].Select(peer => peer.Size).FirstOrDefault(size => size.HasValue);
                if (known.HasValue) return known;
                Plan pipe = peers[plan].Select(peer => peer.Pending).FirstOrDefault(p => p?.Kind == "pipe");
                return pipe == null ? (double?)null : PipeDefault(pipe);
            }

            var nativeDefaults = new HashSet<Plan>();
            MepDiameterRules.InheritConnected(unsized, ConnectedSize,
                conduit =>
                {
                    Parameter parameter = conduit.Type?.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    double? size = parameter?.StorageType == StorageType.Double && parameter.HasValue
                        ? PositiveDiameter(parameter.AsDouble()) : null;
                    if (size.HasValue) return size.Value;
                    nativeDefaults.Add(conduit);
                    return MeasureRunDefault(doc, conduit);
                },
                (conduit, size, connected) =>
                {
                    conduit.Diameter = size;
                    if (nativeDefaults.Contains(conduit)) conduit.KeepNativeConduitDiameter = true;
                    conduit.ExtraPlanFacts = conduit.ExtraPlanFacts ?? new Dictionary<string, string>();
                    conduit.ExtraPlanFacts["conduit_diameter_feet"] = StableDiameter(size);
                    conduit.Assumptions.Add(AssumptionNotes.Row("item " + conduit.Index + ": conduit diameter = " +
                        (size * 304.8).ToString("0.###", CultureInfo.InvariantCulture) + " mm",
                        "diameter omitted; using " + (connected ? "connected run" : nativeDefaults.Contains(conduit)
                            ? "Revit's measured default for the conduit type" : "conduit type"),
                        "send diameter in the request units"));
                });

            foreach (var pair in pairs)
            {
                double? Size(DiameterMember member) => member.Size ??
                    (member.Pending?.Kind == "pipe" ? (double?)PipeDefault(member.Pending) : null);

                Plan fitting = pair.Fitting;
                double? first = Size(pair.Members[0]), second = Size(pair.Members[1]);
                var refusal = UnionRefusal(fitting, pair.Members, first, second);
                if (refusal != null) return refusal;
                if (!first.HasValue || !second.HasValue) continue;
                string kind = MepDiameterRules.FittingFor(fitting.FittingSubtype, first.Value, second.Value);
                fitting.DiameterFitting = new SizedFitting { Kind = kind, First = first.Value, Second = second.Value };
                fitting.ExtraPlanFacts["fitting.effective_subtype"] = kind;
                fitting.ExtraPlanFacts["fitting.diameters_feet"] = StableDiameter(first.Value) + "|" + StableDiameter(second.Value);
                fitting.Summary["effective_fitting"] = kind;
                if (kind != fitting.FittingSubtype)
                    fitting.Assumptions.Add(AssumptionNotes.Row("item " + fitting.Index + ": fitting = " + kind,
                        "the requested union joins different diameters", "request a transition explicitly or use matching diameters"));
            }
            return null;
        }

        private static string StableDiameter(double size) => size.ToString("R", CultureInfo.InvariantCulture);

        private static double MeasureRunDefault(Document doc, Plan plan)
        {
            double Read()
            {
                Element run = Create(doc, plan, null);
                doc.Regenerate();
                return PositiveDiameter(DiameterOf(run)) ?? throw new InvalidOperationException(
                    "item " + plan.Index + ": Revit's default " + plan.Kind + " diameter could not be read.");
            }

            if (doc.IsModifiable)
            {
                using (var sub = new SubTransaction(doc))
                {
                    if (sub.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Could not start the default diameter probe.");
                    try { return Read(); }
                    finally
                    {
                        const string scope = "the default diameter sub-transaction";
                        TransactionStatus status;
                        try { status = sub.RollBack(); }
                        catch (Exception ex) { throw new DiameterProbeRollbackFailed(scope, ex); }
                        if (status != TransactionStatus.RolledBack)
                            throw new DiameterProbeRollbackFailed(scope);
                    }
                }
            }
            using (var tx = new Transaction(doc, "Horizun: measure default run diameter"))
            {
                if (tx.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Could not start the default diameter probe.");
                try
                {
                    FailureHandlingOptions options = tx.GetFailureHandlingOptions();
                    options.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(options);
                    return Read();
                }
                finally
                {
                    if (!Guard.RollBack(tx).Confirmed)
                        throw new DiameterProbeRollbackFailed("the default diameter transaction");
                }
            }
        }

        private sealed class DiameterProbeRollbackFailed : Exception
        {
            public DiameterProbeRollbackFailed(string scope, Exception cause = null)
                : base("Could not confirm rollback of " + scope + ".", cause) { }
        }

        private static bool RehearsesRunDiameter(Plan plan) =>
            (plan.Kind == "conduit" && plan.Input["diameter"] == null) ||
            (plan.DiameterFitting != null && MepDiameterRules.Mismatch(plan.DiameterFitting.First, plan.DiameterFitting.Second, false) != null);

        private static FamilyInstance CreateDiameterCheckedFitting(Document doc, Plan fitting, List<Connector> live)
        {
            Connector a = live[0], b = live[1];
            var approved = fitting.DiameterFitting;
            double? first = PositiveDiameter(DiameterOf(a.Owner)), second = PositiveDiameter(DiameterOf(b.Owner));
            if (approved != null && (!first.HasValue || !second.HasValue ||
                MepDiameterRules.Mismatch(approved.First, first.Value, false) != null ||
                MepDiameterRules.Mismatch(approved.Second, second.Value, false) != null))
                throw new InvalidOperationException("The run diameters differ from the planned values. Rehearse again.");

            bool reduces = first.HasValue && second.HasValue && MepDiameterRules.Mismatch(first.Value, second.Value, false) != null;
            string effectiveSubtype = approved?.Kind ?? fitting.FittingSubtype;
            if (reduces && approved == null)
                throw new InvalidOperationException(MepDiameterRules.Mismatch(first.Value, second.Value, false) +
                    ". Supply both diameters to rehearse the reducing fitting.");

            FamilyInstance Construct()
            {
                try
                {
                    switch (effectiveSubtype)
                    {
                        case "elbow": return doc.Create.NewElbowFitting(a, b);
                        case "union": return doc.Create.NewUnionFitting(a, b);
                        default: return doc.Create.NewTransitionFitting(a, b);
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex) when (reduces &&
                    !(ex is Autodesk.Revit.Exceptions.RegenerationFailedException))
                {
                    throw new InvalidOperationException(ex.Message, ex);
                }
            }
            if (!reduces) return Construct();

            var endpoints = new ReducerPorts { First = (a.Owner.Id, a.Id), Second = (b.Owner.Id, b.Id) };
            var reducer = MepDiameterRules.ConstructReducer(first.Value, second.Value, Construct, made =>
            {
                doc.Regenerate();
                if (!ReducerPortsMatch(doc, made, endpoints, approved, (first.Value, second.Value)))
                    throw new ReducerPortVerificationFailed();
                return true;
            });
            fitting.BuiltReducerPorts = endpoints;
            return reducer;
        }

        private static bool ReducerPortsMatch(Document doc, Element fitting, ReducerPorts endpoints, SizedFitting sizes,
            (double First, double Second)? measuredBeforeConstruction = null)
        {
            Connector RunPort((ElementId Owner, int Port) endpoint) =>
                MepFacts.Ordered(MepFacts.ManagerOf(doc.GetElement(endpoint.Owner)))
                    .FirstOrDefault(c => c.Id == endpoint.Port);
            var ports = MepFacts.Ordered(MepFacts.ManagerOf(fitting))
                .Where(c => c.ConnectorType == ConnectorType.End).ToArray();
            if (ports.Length != 2) return false;
            Connector first = RunPort(endpoints.First), second = RunPort(endpoints.Second);
            var firstSize = measuredBeforeConstruction?.First ?? PositiveDiameter(DiameterOf(first?.Owner));
            var secondSize = measuredBeforeConstruction?.Second ?? PositiveDiameter(DiameterOf(second?.Owner));
            bool Joins(Connector port, Connector run, double approvedSize, double? measuredSize)
            {
                if (run == null || port.Shape != ConnectorProfileType.Round || !measuredSize.HasValue) return false;
                double diameter = port.Radius * 2;
                return MepDiameterRules.Mismatch(measuredSize.Value, diameter, false) == null &&
                    MepDiameterRules.Mismatch(approvedSize, diameter, false) == null && port.IsConnectedTo(run);
            }
            return (Joins(ports[0], first, sizes.First, firstSize) && Joins(ports[1], second, sizes.Second, secondSize)) ||
                   (Joins(ports[1], first, sizes.First, firstSize) && Joins(ports[0], second, sizes.Second, secondSize));
        }

        private static JObject VerifyCreatedWithReducer(Document doc, Created made)
        {
            JObject row = VerifyCreated(doc, made);
            if (made.Plan.BuiltReducerPorts == null) return row;

            string error = null;
            try
            {
                if (!ReducerPortsMatch(doc, doc.GetElement(made.Id), made.Plan.BuiltReducerPorts, made.Plan.DiameterFitting))
                    error = ReducerPortVerificationFailed.Reason;
            }
            catch (Exception ex)
            {
                error = "Could not re-read the reducer ports: " + ex.Message;
            }
            if (error != null)
            {
                row["reducer_ports_error"] = error;
                row["verified"] = false;
            }
            return row;
        }
    }
}
