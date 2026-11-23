using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public enum Domain { DomainPiping }
    public enum ConnectorType { End }
    public enum ConnectorProfileType { Round }
    public enum PipeSystemType { Sanitary, DomesticColdWater }

    public class Connector
    {
        public int Id { get; set; }
        public Element Owner { get; set; }
        public XYZ Origin { get; set; }
        public Domain Domain => Domain.DomainPiping;
        public ConnectorType ConnectorType => ConnectorType.End;
        public ConnectorProfileType Shape => ConnectorProfileType.Round;
        public PipeSystemType PipeSystemType { get; set; }
        public double Radius { get; set; } = 0.1;
        public double Width => 0;
        public double Height => 0;
        public ConnectorTransform CoordinateSystem => new ConnectorTransform();
        public HashSet<Connector> AllRefs { get; } = new HashSet<Connector>();
        public bool IsConnected => AllRefs.Count > 0;
        public bool IsConnectedTo(Connector peer) => AllRefs.Contains(peer);
        public void DisconnectFrom(Connector peer) { AllRefs.Remove(peer); peer.AllRefs.Remove(this); }
        public void ConnectTo(Connector peer) { AllRefs.Add(peer); peer.AllRefs.Add(this); }
    }

    public class ConnectorManager
    {
        public IEnumerable<Connector> Ports { get; }
        public ConnectorManager(IEnumerable<Connector> ports) => Ports = ports;
    }

    public class ConnectorTransform { public XYZ BasisZ => new XYZ(0, 0, 1); }

    public partial class FamilySymbol
    {
        internal static readonly Category FixtureCategory = new Category { Id = new ElementId((long)BuiltInCategory.OST_PlumbingFixtures) };
        public override Category Category => _category ?? (TestCategory != null ? base.Category : FixtureCategory);
        public bool IsActive => true;
        public void Activate() { }
    }

    public partial class FamilyInstance : Element
    {
        public override Category Category => _category ?? (TestCategory != null ? base.Category : FamilySymbol.FixtureCategory);
        public ElementId TypeId { get; set; }
        public List<Connector> Ports { get; set; }
        public Func<ElementId, ElementId> Replace { get; set; }
        public Func<bool> HideMark { get; set; }
        public bool CanStamp { get; set; } = true;
        public JObject Mark { get; set; }
        public override ElementId GetTypeId() => TypeId ?? TestTypeId ?? base.GetTypeId();
        public ElementId ChangeTypeId(ElementId type) => Replace(type);
    }

    public class LocationCurve : Location
    {
        private Curve curve;
        public Connector[] Ends { get; set; }
        public Curve Curve
        {
            get => curve;
            set
            {
                curve = value;
                for (int i = 0; i < (Ends?.Length ?? 0); i++) Ends[i].Origin = value.GetEndPoint(i);
            }
        }
    }
}

namespace Autodesk.Revit.DB.Plumbing
{
    public partial class Pipe : Element { public Connector[] Ports { get; set; } }
}

namespace Horizun.Revit.Core
{
    internal static partial class HrzOwnership
    {
        public const string OwnerId = "0123456789abcdef0123456789abcdef";
        public const string Session = "test-session";

        public static int Stamp(Document doc, IEnumerable<ElementId> ids, string tool, out int skipped, List<string> reasons)
        {
            skipped = 0;
            int stamped = 0;
            foreach (var id in ids)
            {
                var fixture = (FamilyInstance)doc.GetElement(id);
                if (!fixture.CanStamp)
                {
                    reasons.Add("does not accept extensible storage");
                    skipped++;
                    continue;
                }
                fixture.Mark = new JObject { ["mark_version"] = 3, ["tool"] = tool, ["session"] = Session, ["owner"] = OwnerId };
                stamped++;
            }
            return stamped;
        }

        public static JObject Read(Element element) => element is FamilyInstance fixture && fixture.HideMark?.Invoke() != true ? fixture.Mark : null;
    }
}

namespace Horizun.Revit.Commands
{
    internal static class MepFacts
    {
        public static ConnectorManager ManagerOf(Element element)
        {
            IEnumerable<Connector> ports = element is FamilyInstance fixture
                ? fixture.Ports : (element as Autodesk.Revit.DB.Plumbing.Pipe)?.Ports;
            return ports == null ? null : new ConnectorManager(ports);
        }

        public static IEnumerable<Connector> Ordered(ConnectorManager manager) =>
            manager?.Ports.OrderBy(c => c.Id) ?? Enumerable.Empty<Connector>();
        internal static JObject Json(Connector connector, Transform transform, double scale) => throw new NotSupportedException();
    }

    internal static class NeighborEditGuard
    {
        public static void RequireOwned(Document doc, Element element) { }
    }

    public sealed partial class PlumbingFixturesCommand
    {
        private const double Mm = 1.0 / 304.8;
        private static double[] A(XYZ point) => new[] { point.X, point.Y, point.Z };
        private static XYZ X(double[] point) => new XYZ(point[0], point[1], point[2]);
    }
}
