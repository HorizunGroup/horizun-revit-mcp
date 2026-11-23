using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand : ICommand
    {
        public string Name => "horizun_create_elements";
        public string Description => "Create model elements.";
        // Planning stand-ins. The real PrepareCreation (CreateElementsCommand.Prepare.cs) runs over them:
        // tabular expansion, recipe expansion, row validation, batch limits and level checks.
        private static Plan PlanItem(Document doc, int index, JObject item, string kind, double scale,
            out string error, out string gap)
        {
            error = null;
            gap = null;
            if (kind == "family_instance")
            {
                var point = (JArray)item["point"];
                return new Plan
                {
                    Index = index, Kind = kind, Input = item,
                    Type = doc.GetElement(new ElementId(item.Value<long>("type_id"))),
                    Level = (Level)doc.GetElement(new ElementId(item.Value<long>("level_id"))),
                    Start = new XYZ(point[0].Value<double>() * scale, point[1].Value<double>() * scale, point[2].Value<double>() * scale),
                    Point = point
                };
            }
            var row = new Plan
            {
                Index = index, Kind = "wall", Input = item,
                Type = new Element { Id = new ElementId(5) },
                Level = (Level)doc.Elements[2], TopLevel = doc.CreationTopLevel, Height = doc.CreationWallHeight
            };
            if (kind == "fitting")
            {
                row.Kind = "fitting";
                row.FittingSubtype = item.Value<string>("fitting");
                row.FittingMembers = new List<FittingMember>();
                foreach (JObject member in (JArray)item["elements"])
                {
                    var batchIndex = member.Value<int?>("batch_index");
                    Element owner = batchIndex.HasValue ? null : doc.GetElement(new ElementId(member.Value<long>("element_id")));
                    row.FittingMembers.Add(new FittingMember { Owner = owner, Fact = (owner as Pipe)?.Connector, BatchIndex = batchIndex });
                }
            }
            if (kind == "conduit")
            {
                row.Kind = "conduit";
                row.Diameter = item.Value<double?>("diameter") * scale;
            }
            return row;
        }

        private static JObject ResolveHostedRecipe(Document doc, JObject row, string kind, JArray assumptions) =>
            throw new NotSupportedException(kind);
        private static string ExpandEnclosed(Document doc, JObject request, ref JArray input, out JArray response)
        { response = null; return null; }
        private static CommandResult NothingEnclosed(JObject request, JArray response) => throw new NotSupportedException();
        private static string LevelSpanCreationError(Document doc, Plan plan, string exception) => null;
        private static double? DiameterOf(Element element) => element is Conduit conduit ? conduit.ProbeDiameter :
            (element as Pipe)?.Ports?.First().Radius * 2;
        private static List<MepGraphIssue> MepGraphCheck(Document doc, JArray input, double scale) => new List<MepGraphIssue>();

        // Stand-ins for native construction and observation only. The production execution
        // owns the rehearsal decision, transactions, confirmation comparison and replies.
        private static Element Create(Document doc, Plan plan, List<Created> created)
        {
            if (plan.Kind == "conduit")
            {
                var conduit = new Conduit { Id = new ElementId(doc.NextElementId++), ProbeDiameter = 0.2 };
                doc.Elements.Add(conduit.Id.Value, conduit);
                return conduit;
            }
            if (plan.Kind == "fitting" && plan.FittingSubtype == "union")
            {
                var ports = plan.FittingMembers.Select(m => ((Pipe)doc.GetElement(m.Owner.Id)).Ports.Single()).ToList();
                var fitting = CreateDiameterCheckedFitting(doc, plan, ports);
                fitting.Id = new ElementId(doc.NextElementId++);
                doc.Elements.Add(fitting.Id.Value, fitting);
                return fitting;
            }
            var element = new Element { Id = new ElementId(doc.NextElementId++), Document = doc };
            if (plan.Kind == "fitting")
            {
                plan.BuiltWyeTypeUniqueId = doc.CreationWyeType;
                // A junction can move an existing pipe. Simulate that change as well as the new fitting.
                var branch = (Pipe)plan.FittingMembers[2].Owner;
                doc.Elements[branch.Id.Value] = new Pipe { Id = branch.Id, Connector = branch.Connector, Document = doc };
            }
            doc.Elements.Add(element.Id.Value, element);
            if (plan.Point != null) doc.CreatedPlacements = new JArray((doc.CreatedPlacements ?? new JArray()).Concat(new[] { plan.Point }));
            return element;
        }

        private static void RecordCreated(Document doc, Plan plan, Element element, List<Created> created) =>
            created.Add(new Created { Index = plan.Index, Kind = plan.Kind, Plan = plan, Id = element.Id });

        private static JObject VerifyCreated(Document doc, Created created) =>
            new JObject { ["verified"] = doc.GetElement(created.Id) != null };
        private static void ApplyInstanceParameters(Element element, Plan plan) { }
        private static bool RehearsesGeometryChecks(Document doc, List<Plan> plans) => false;
        private static JArray DeclaredExceptions(IEnumerable<Plan> plans) => new JArray();
        private static JObject MeasureWallFoundation(Document doc, Created made) => throw new NotSupportedException();
        private static CommandResult ApplyStairs(Document doc, Plan plan, bool rehearsal) => throw new NotSupportedException();
        private static bool IsRoomRegionWarning(Guid definition) => throw new NotSupportedException();
        private static long PhaseOf(Element element) => throw new NotSupportedException();
        private static CommandResult CreationResponse(CommandResult response, IEnumerable<Plan> plans, JArray expansion,
            string failurePrefix = "SUPUESTO: creation used assumed values. ") =>
            ArchitectureCreationRules.CreationResponse(response, plans.Select(p => (p.Index, p.Assumptions)), expansion, failurePrefix);

        private sealed class Created
        {
            public int Index;
            public string Kind;
            public ElementId Id;
            public Plan Plan;
        }

        private sealed class FittingMember
        {
            public Element Owner;
            public ConnectorFact Fact;
            public int? BatchIndex;
        }

        private sealed class BatchFailureCapture : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor) => FailureProcessingResult.Continue;
            public readonly List<FailureFact> Facts = new List<FailureFact>();
            public readonly HashSet<long> RoomRegionIds = new HashSet<long>();
        }

        private sealed class Plan
        {
            public int Index { get; set; }
            public string Kind { get; set; }
            public JObject Input { get; set; }
            public JObject Summary => new JObject { ["kind"] = Kind };
            public JArray Assumptions { get; } = new JArray();
            public Element Type { get; set; }
            public Element SystemType { get; set; }
            public Level Level { get; set; }
            public Level TopLevel { get; set; }
            public Element Host { get; set; }
            public Wall FoundationWall { get; set; }
            public Dictionary<string, string> ExtraPlanFacts { get; set; } = new Dictionary<string, string>();
            public double? Diameter;
            public bool KeepNativeConduitDiameter { get; set; }
            public SizedFitting DiameterFitting;
            public ReducerPorts BuiltReducerPorts;
            public XYZ Start { get; set; }
            public XYZ End { get; set; }
            public double Height { get; set; }
            public double RotationRadians { get; set; }
            public JObject DisplacedState { get; set; }
            public string FittingSubtype { get; set; }
            public List<FittingMember> FittingMembers { get; set; }
            public string BuiltWyeTypeUniqueId { get; set; }
            public bool Enclosed => false;
            public JObject TopoSource => null;
            public JArray Point { get; set; }
        }
    }

    internal static class FloorWallOverlapGuard
    {
        internal sealed class Snapshot
        {
            internal readonly JArray Warnings = new JArray();
        }

        internal static bool IsFinishFloor(Document doc, Floor floor) => false;
        internal static Snapshot Capture(Document doc, IEnumerable<ElementId> ids) => new Snapshot();
        internal static JArray ThrowIfIncreased(Document doc, Snapshot snapshot, IEnumerable<ElementId> ids) => null;
    }

    internal static class UndoCapture
    {
        internal static JObject Record(Document doc, string command, List<UndoEntry> entries) => new JObject();
        internal static UndoEntry Entry(Document doc, string action, IEnumerable<long> ids, JObject before, JObject after) =>
            new UndoEntry();
    }
}

namespace Horizun.Revit.Core
{
    internal static partial class SourceTraceStorage
    {
        internal static void Write(Element element, JObject trace) => throw new NotSupportedException();
    }
}

namespace Autodesk.Revit.DB
{
    public sealed class FailureMessage
    {
        public (Guid Guid, int Unused) GetFailureDefinitionId() => throw new NotSupportedException();
        public IEnumerable<ElementId> GetFailingElements() => throw new NotSupportedException();
        public IEnumerable<ElementId> GetAdditionalElements() => throw new NotSupportedException();
    }

    public partial class Document
    {
        internal Action CreationProbeRollback;
        public CreationFittingFactory Create { get; } = new CreationFittingFactory();
        public IEnumerable<FailureMessage> GetWarnings() => Array.Empty<FailureMessage>();
        public ConfirmationStore CreationConfirmations { get; set; }
        public Level CreationTopLevel { get; set; }
        public double CreationWallHeight { get; set; }
        public int CreationWrites => Elements.Keys.Count(id => id >= 100);
        public string CreationWyeType { get; set; }
    }

    public sealed class CreationFittingFactory
    {
        public FamilyInstance NewElbowFitting(Connector first, Connector second) => throw new NotSupportedException();
        public FamilyInstance NewUnionFitting(Connector first, Connector second) => throw new NotSupportedException();
        public FamilyInstance NewTransitionFitting(Connector first, Connector second)
        {
            var fitting = new FamilyInstance { Ports = new List<Connector>() };
            foreach (var run in new[] { second, first })
            {
                var port = new Connector { Owner = fitting, Id = fitting.Ports.Count + 1, Radius = run.Radius };
                port.ConnectTo(run);
                fitting.Ports.Add(port);
            }
            return fitting;
        }
    }
}

namespace Autodesk.Revit.DB.Electrical
{
    public sealed class Conduit : Element { public double ProbeDiameter { get; set; } }
}

namespace Autodesk.Revit.DB.Plumbing
{
    public partial class Pipe { public ConnectorFact Connector { get; set; } }
}
