using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Bimwright.Rvt.Plugin.Handlers
{
    /// <summary>
    /// Analyzes one MEP system's topology: element graph, connectivity health,
    /// equipment/terminal counts, open connectors, and issues. Read-only — no transaction.
    /// </summary>
    public class AnalyzeMepNetworkHandler : IRevitCommand
    {
        public string Name => "analyze_mep_network";

        public string Description =>
            "Analyze one MEP system's topology and health. Resolve the system by system_id " +
            "(ElementId) or system_name. Returns the domain, system type, element count, a " +
            "category breakdown of member elements, the base (source) equipment, the count of " +
            "open/unconnected connectors, whether the system is well connected, and a list of " +
            "issues with recommendations. Either system_id or system_name is required.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""system_id"": {""type"":""integer"",""description"":""MEP system ElementId. Either system_id or system_name required.""},
    ""system_name"": {""type"":""string"",""description"":""MEP system name. Either system_id or system_name required.""}
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                return CommandResult.Fail("No document is open.");

            JObject request;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JObject.Parse(paramsJson);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return CommandResult.Fail("Invalid JSON parameters: " + ex.Message);
            }

            var systemId = request.Value<long?>("system_id");
            var systemName = request.Value<string>("system_name");

            if (systemId == null && string.IsNullOrWhiteSpace(systemName))
                return CommandResult.Fail("Either system_id or system_name is required.");

            if (systemId != null && !RevitCompat.CanRepresentElementId(systemId.Value))
                return CommandResult.Fail(RevitCompat.ElementIdRangeError(systemId.Value));

            // Resolve the MEP system across all three domains.
            MEPSystem system = null;
            string domain = null;

            if (systemId != null)
            {
                Element el;
                try { el = doc.GetElement(RevitCompat.ToElementId(systemId.Value)); }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Failed to resolve system " + systemId.Value + ": " + ex.Message);
                }
                system = el as MEPSystem;
                if (system == null)
                    return CommandResult.Fail("Element " + systemId.Value + " is not an MEP system.");
                domain = DomainOf(system);
            }
            else
            {
                var target = systemName.Trim();
                var matches = new List<MEPSystem>();
                foreach (var sys in AllSystems(doc))
                {
                    string n = null;
                    try { n = sys.Name; } catch { }
                    if (!string.IsNullOrEmpty(n) &&
                        string.Equals(n.Trim(), target, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(sys);
                    }
                }
                if (matches.Count > 1)
                    return CommandResult.Fail("Multiple MEP systems named '" + target + "' (" + matches.Count + " found). Pass system_id to disambiguate.");
                if (matches.Count == 1)
                    system = matches[0];
                if (system == null)
                    return CommandResult.Fail("No MEP system named '" + target + "' found in model.");
                domain = DomainOf(system);
            }

            string resolvedName = null;
            try { resolvedName = system.Name; } catch { }

            // System type name.
            string systemType = null;
            try
            {
                var typeId = system.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var mepType = doc.GetElement(typeId) as MEPSystemType;
                    if (mepType != null) systemType = mepType.Name;
                }
            }
            catch { }

            // IsWellConnected — not exposed uniformly; try direct, then reflection, default true.
            bool isWellConnected = ReadIsWellConnected(system);

            // Iterate member elements → category breakdown + open connector count.
            var categoryBreakdown = new Dictionary<string, int>();
            int elementCount = 0;
            int openConnectorCount = 0;

            ElementSet members = null;
            try { members = system.Elements; } catch { members = null; }

            if (members != null)
            {
                foreach (Element member in members)
                {
                    if (member == null) continue;
                    elementCount++;

                    string catName = "Uncategorized";
                    try
                    {
                        var c = member.Category;
                        if (c != null && !string.IsNullOrEmpty(c.Name)) catName = c.Name;
                    }
                    catch { }

                    if (categoryBreakdown.ContainsKey(catName))
                        categoryBreakdown[catName]++;
                    else
                        categoryBreakdown[catName] = 1;

                    openConnectorCount += CountOpenEndConnectors(member);
                }
            }

            // Base (source) equipment.
            object baseEquipment = null;
            string baseEquipmentName = null;
            try
            {
                var be = system.BaseEquipment;
                if (be != null)
                {
                    string beName = null;
                    try { beName = be.Name; } catch { }
                    baseEquipmentName = beName;
                    baseEquipment = new
                    {
                        id = RevitCompat.GetId(be.Id).ToString(),
                        name = beName ?? string.Empty
                    };
                }
            }
            catch { }

            // Issues + recommendations.
            var issues = new List<string>();
            var recommendations = new List<string>();

            if (!isWellConnected)
            {
                issues.Add("System is not well connected.");
                recommendations.Add(
                    "Review the system graph for disconnected segments and reconnect open connectors " +
                    "so every element traces back to the source equipment.");
            }

            if (openConnectorCount > 0)
            {
                issues.Add(openConnectorCount + " open connector" +
                    (openConnectorCount == 1 ? "" : "s") + " detected.");
                recommendations.Add(
                    "Cap, connect, or terminate the " + openConnectorCount +
                    " open connector" + (openConnectorCount == 1 ? "" : "s") +
                    " to complete the network.");
            }

            if (baseEquipment == null)
            {
                issues.Add("No base equipment assigned.");
                recommendations.Add(
                    "Assign source equipment to the system so flow and pressure calculations resolve correctly.");
            }

            if (elementCount == 0)
            {
                issues.Add("System has no member elements.");
                recommendations.Add(
                    "This system is empty — assign elements to it or delete it if it is unused.");
            }

            if (issues.Count == 0)
                recommendations.Add("No topology issues detected — the system network is healthy.");

            return CommandResult.Ok(new
            {
                system_id = RevitCompat.GetId(system.Id).ToString(),
                system_name = resolvedName ?? string.Empty,
                domain = domain ?? "unknown",
                system_type = systemType ?? string.Empty,
                is_well_connected = isWellConnected,
                element_count = elementCount,
                category_breakdown = categoryBreakdown
                    .OrderByDescending(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                base_equipment = baseEquipment,
                open_connector_count = openConnectorCount,
                issues = issues.ToArray(),
                recommendations = recommendations.ToArray()
            });
        }

        /// <summary>Enumerates every MEP system in the document across all three domains.</summary>
        private static IEnumerable<MEPSystem> AllSystems(Document doc)
        {
            var result = new List<MEPSystem>();
            try
            {
                result.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(MechanicalSystem)).Cast<MEPSystem>());
            }
            catch { }
            try
            {
                result.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystem)).Cast<MEPSystem>());
            }
            catch { }
            try
            {
                result.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem)).Cast<MEPSystem>());
            }
            catch { }
            return result;
        }

        private static string DomainOf(MEPSystem system)
        {
            if (system is MechanicalSystem) return "mechanical";
            if (system is PipingSystem) return "piping";
            if (system is ElectricalSystem) return "electrical";
            return "unknown";
        }

        /// <summary>
        /// MEPSystem.IsWellConnected is not exposed uniformly across all domains/versions.
        /// Try the direct property; fall back to reflection; default to true.
        /// </summary>
        private static bool ReadIsWellConnected(MEPSystem system)
        {
            // IsWellConnected lives on the MechanicalSystem / PipingSystem subclasses,
            // not on the MEPSystem base. ElectricalSystem has no equivalent — default true.
            try
            {
                if (system is MechanicalSystem) return ((MechanicalSystem)system).IsWellConnected;
                if (system is PipingSystem) return ((PipingSystem)system).IsWellConnected;
            }
            catch { }
            return true;
        }

        /// <summary>
        /// Counts unconnected End connectors on an element. End connectors are physical
        /// connection points; an unconnected one is an open end in the network.
        /// </summary>
        private static int CountOpenEndConnectors(Element element)
        {
            int open = 0;
            ConnectorManager cm = GetConnectorManager(element);
            if (cm == null) return 0;

            ConnectorSet connectors = null;
            try { connectors = cm.Connectors; } catch { connectors = null; }
            if (connectors == null) return 0;

            foreach (Connector connector in connectors)
            {
                try
                {
                    if (connector.ConnectorType != ConnectorType.End) continue;
                    if (!connector.IsConnected) open++;
                }
                catch
                {
                    // One bad connector must not abort the whole count.
                }
            }
            return open;
        }

        private static ConnectorManager GetConnectorManager(Element element)
        {
            try
            {
                if (element is MEPCurve curve)
                    return curve.ConnectorManager;

                if (element is FamilyInstance fi)
                    return fi.MEPModel?.ConnectorManager;
            }
            catch { }

            return null;
        }
    }
}
