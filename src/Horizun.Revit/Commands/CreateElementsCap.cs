using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class CreateElementsCommand
    {
        private static void PlanCap(Document doc, Plan p, JObject item)
        {
            var members = item["elements"] as JArray;
            if (members == null || members.Count != 1 || !(members[0] is JObject member) || member["batch_index"] != null)
                throw new ArgumentException("cap needs exactly one existing pipe/duct in elements; use element_id and optional connector from connect_mep open_ends.");
            Element owner = Need<Element>(doc, member, "element_id");
            if (!(owner is Pipe) && !(owner is Duct)) throw new ArgumentException("cap requires a Pipe or Duct.");
            var symbol = Need<FamilySymbol>(doc, item, "type_id");
            BuiltInCategory expected = owner is Pipe ? BuiltInCategory.OST_PipeFitting : BuiltInCategory.OST_DuctFitting;
            if (!InCategory(symbol, expected)) throw new ArgumentException("cap type_id must be a fitting family type for this domain, supplied by the user.");
            ConnectorFact chosen = MepRules.SelectCapEnd(MepFacts.Ordered(MepFacts.ManagerOf(owner))
                .Where(c => c.ConnectorType == ConnectorType.End).Select(MepFacts.FactOf), member.Value<int?>("connector"));
            p.Type = symbol;
            p.FittingSubtype = "cap";
            p.FittingMembers = new List<FittingMember> { new FittingMember { Owner = owner, ConnectorId = chosen.Id, Fact = chosen } };
            Connector port = MepFacts.Ordered(MepFacts.ManagerOf(owner)).Single(c => c.Id == chosen.Id);
            p.ExtraPlanFacts = new Dictionary<string, string>
            {
                ["cap.owner"] = SafePlanUid(owner), ["cap.connector"] = chosen.Id.ToString(CultureInfo.InvariantCulture),
                ["cap.origin"] = Canon01(new XYZ(chosen.X, chosen.Y, chosen.Z)),
                ["cap.direction"] = Canon01(new XYZ(chosen.DirX, chosen.DirY, chosen.DirZ)),
                ["cap.symbol"] = SafePlanUid(symbol)
            };
            p.ExtraPlanFacts["cap.section"] = port.Shape.ToString() + ":" + (port.Shape == ConnectorProfileType.Round
                ? port.Radius.ToString("R", CultureInfo.InvariantCulture)
                : port.Width.ToString("R", CultureInfo.InvariantCulture) + "x" + port.Height.ToString("R", CultureInfo.InvariantCulture));
        }

        private static Connector CapPort(Element element)
        {
            var ends = MepFacts.Ordered(MepFacts.ManagerOf(element)).Where(c => c.ConnectorType == ConnectorType.End &&
                (c.Domain == Domain.DomainPiping || c.Domain == Domain.DomainHvac)).ToList();
            if (ends.Count != 1) throw new InvalidOperationException("cap family must expose exactly one physical end connector.");
            return ends[0];
        }

        private static Element CreateCap(Document doc, Plan p)
        {
            var member = p.FittingMembers[0];
            Connector target = MepFacts.Ordered(MepFacts.ManagerOf(doc.GetElement(member.Owner.Id))).Single(c => c.Id == member.ConnectorId);
            if (target.IsConnected) throw new InvalidOperationException("cap end became connected; rehearse again.");
            var symbol = (FamilySymbol)p.Type;
            if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
            FamilyInstance cap = doc.Create.NewFamilyInstance(target.Origin, symbol, StructuralType.NonStructural);
            doc.Regenerate();
            Connector port = CapPort(cap);
            if (port.Domain != target.Domain || port.Shape != target.Shape) throw new InvalidOperationException("cap connector domain/shape differs from selected end.");
            XYZ from = port.CoordinateSystem.BasisZ.Normalize(), to = -target.CoordinateSystem.BasisZ.Normalize();
            double angle = from.AngleTo(to);
            if (angle > 1e-8)
            {
                XYZ axis = from.CrossProduct(to);
                if (axis.GetLength() < 1e-8) axis = port.CoordinateSystem.BasisX;
                ElementTransformUtils.RotateElement(doc, cap.Id, Line.CreateUnbound(port.Origin, axis.Normalize()), angle);
                doc.Regenerate(); port = CapPort(cap);
            }
            if (port.Shape != ConnectorProfileType.Round)
            {
                XYZ x = port.CoordinateSystem.BasisX, wanted = target.CoordinateSystem.BasisX;
                double turn = Math.Atan2(to.DotProduct(x.CrossProduct(wanted)), x.DotProduct(wanted));
                if (Math.Abs(turn) > 1e-8) ElementTransformUtils.RotateElement(doc, cap.Id, Line.CreateUnbound(port.Origin, to), turn);
                doc.Regenerate(); port = CapPort(cap);
            }
            // A fitting connector whose size is associated with a family parameter refuses a
            // direct Connector.Radius/Width/Height set (ArgumentOutOfRangeException, measured
            // live on Revit 2025 with the template's pipe cap and rectangular endcap). Its size
            // is changed through that associated instance parameter; a cap sized by a type or
            // formula parameter refuses and the whole batch rolls back.
            if (port.Shape == ConnectorProfileType.Round)
            {
                if (Math.Abs(port.Radius - target.Radius) > 0.1 / 304.8)
                    SizeCapPort(cap, port, target.Radius, BuiltInParameter.CONNECTOR_RADIUS, BuiltInParameter.CONNECTOR_DIAMETER);
            }
            else
            {
                if (Math.Abs(port.Width - target.Width) > 0.1 / 304.8)
                    SizeCapPort(cap, port, target.Width, BuiltInParameter.CONNECTOR_WIDTH, null);
                doc.Regenerate(); port = CapPort(cap);
                if (Math.Abs(port.Height - target.Height) > 0.1 / 304.8)
                    SizeCapPort(cap, port, target.Height, BuiltInParameter.CONNECTOR_HEIGHT, null);
            }
            doc.Regenerate(); port = CapPort(cap);
            ElementTransformUtils.MoveElement(doc, cap.Id, target.Origin - port.Origin);
            doc.Regenerate(); port = CapPort(cap);
            port.ConnectTo(target);
            return cap;
        }

        /// <summary>Set a cap connector's size: through its associated instance parameter when the
        /// family drives the connector by one (radius, or diameter = 2 x radius), otherwise on the
        /// connector itself. Never guesses a parameter by name.</summary>
        private static void SizeCapPort(Element cap, Connector port, double value, BuiltInParameter primary, BuiltInParameter? doubled)
        {
            var info = port.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
            foreach (var candidate in doubled.HasValue ? new[] { primary, doubled.Value } : new[] { primary })
            {
                ElementId associated = info?.GetAssociateFamilyParameterId(new ElementId(candidate));
                if (associated == null || associated == ElementId.InvalidElementId) continue;
                Parameter parameter = cap.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id == associated);
                if (MepRules.CapSizeRoute(true, parameter != null, parameter != null && !parameter.IsReadOnly,
                        parameter != null && parameter.StorageType == StorageType.Double) == null)
                    throw new InvalidOperationException("cap connector size is driven by a type, formula or read-only parameter" +
                        (parameter == null ? "" : " ('" + parameter.Definition?.Name + "')") +
                        "; choose a cap type of the run's size. Nothing was written.");
                if (!parameter.Set(candidate == primary ? value : value * 2))
                    throw new InvalidOperationException("Revit refused the cap size parameter '" + parameter.Definition?.Name + "'. Nothing was written.");
                return;
            }
            if (primary == BuiltInParameter.CONNECTOR_RADIUS) port.Radius = value;
            else if (primary == BuiltInParameter.CONNECTOR_WIDTH) port.Width = value;
            else port.Height = value;
        }

        private static bool CapMatches(Element cap, Connector target)
        {
            try
            {
                Connector port = CapPort(cap);
                const double tol = 0.1 / 304.8;
                bool size = port.Shape == target.Shape && (port.Shape == ConnectorProfileType.Round
                    ? Math.Abs(port.Radius - target.Radius) <= tol
                    : Math.Abs(port.Width - target.Width) <= tol && Math.Abs(port.Height - target.Height) <= tol);
                return size && port.Domain == target.Domain && port.IsConnectedTo(target) && target.IsConnectedTo(port) &&
                    port.Origin.DistanceTo(target.Origin) <= tol && port.CoordinateSystem.BasisZ.DotProduct(target.CoordinateSystem.BasisZ) < -0.999;
            }
            catch { return false; }
        }
    }
}
