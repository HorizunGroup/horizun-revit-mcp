// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Connectors as facts. One reader shared by discovery (query_model's mep block)
// and creation (the fitting kind), so both halves of a workflow describe the
// same connector the same way. Everything here READS; the decisions over these
// facts live in MepRules, which is provable without Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class MepFacts
    {
        /// <summary>The connector manager of anything that has one, else null.</summary>
        public static ConnectorManager ManagerOf(Element element)
        {
            if (element is MEPCurve curve) return curve.ConnectorManager;
            if (element is Autodesk.Revit.DB.FamilyInstance instance)
            {
                try { return instance.MEPModel?.ConnectorManager; }
                catch { return null; }
            }
            return null;
        }

        /// <summary>
        /// Connectors in a stable order - Revit's own connector id, ascending. The id
        /// is how a caller names one connector across two calls, so the order must
        /// not depend on enumeration luck.
        /// </summary>
        public static List<Connector> Ordered(ConnectorManager manager)
        {
            var list = new List<Connector>();
            if (manager == null) return list;
            foreach (Connector connector in manager.Connectors)
                if (connector.ConnectorType == ConnectorType.End ||
                    connector.ConnectorType == ConnectorType.Curve ||
                    connector.ConnectorType == ConnectorType.Physical)
                    list.Add(connector);
            return list.OrderBy(c => c.Id).ToList();
        }

        public static string DomainName(Connector connector)
        {
            try
            {
                switch (connector.Domain)
                {
                    case Domain.DomainHvac: return "hvac";
                    case Domain.DomainPiping: return "piping";
                    case Domain.DomainElectrical: return "electrical";
                    case Domain.DomainCableTrayConduit: return "cable_tray_conduit";
                    default: return "undefined";
                }
            }
            catch { return "undefined"; }
        }

        /// <summary>The plain facts MepRules decides over. Origin in host feet.</summary>
        public static ConnectorFact FactOf(Connector connector)
        {
            XYZ origin = connector.Origin;
            XYZ direction;
            try { direction = connector.CoordinateSystem.BasisZ; }
            catch { direction = XYZ.BasisZ; }
            return new ConnectorFact
            {
                Id = connector.Id,
                X = origin.X, Y = origin.Y, Z = origin.Z,
                IsConnected = connector.IsConnected,
                Domain = DomainName(connector),
                DirX = direction.X, DirY = direction.Y, DirZ = direction.Z
            };
        }

        /// <summary>
        /// One connector as a reply row. `fromFeet` converts lengths to the caller's
        /// units; `transform` maps a linked element's connector into host coordinates
        /// (identity for host elements). What cannot be read for this domain is
        /// absent, never invented - an electrical connector has no profile.
        /// </summary>
        public static JObject Json(Connector connector, Transform transform, double fromFeet)
        {
            var row = new JObject
            {
                ["id"] = connector.Id,
                ["domain"] = DomainName(connector),
                ["is_connected"] = connector.IsConnected
            };
            try
            {
                XYZ origin = transform == null ? connector.Origin : transform.OfPoint(connector.Origin);
                row["origin"] = new JArray(
                    Math.Round(origin.X * fromFeet, 3), Math.Round(origin.Y * fromFeet, 3), Math.Round(origin.Z * fromFeet, 3));
            }
            catch { }
            try
            {
                switch (connector.Shape)
                {
                    case ConnectorProfileType.Round:
                        row["shape"] = "round";
                        row["diameter"] = Math.Round(connector.Radius * 2 * fromFeet, 3);
                        break;
                    case ConnectorProfileType.Rectangular:
                        row["shape"] = "rectangular";
                        row["width"] = Math.Round(connector.Width * fromFeet, 3);
                        row["height"] = Math.Round(connector.Height * fromFeet, 3);
                        break;
                    case ConnectorProfileType.Oval:
                        row["shape"] = "oval";
                        row["width"] = Math.Round(connector.Width * fromFeet, 3);
                        row["height"] = Math.Round(connector.Height * fromFeet, 3);
                        break;
                }
            }
            catch { }
            try { row["flow_direction"] = connector.Direction.ToString(); } catch { }
            try
            {
                MEPSystem system = connector.MEPSystem;
                if (system != null)
                    row["system"] = new JObject
                    {
                        ["id"] = Rid.Value(system.Id),
                        ["name"] = Safe(() => system.Name),
                        ["classification"] = Safe(() => SystemClassification(connector))
                    };
            }
            catch { }
            try
            {
                var partners = new JArray();
                foreach (Connector other in connector.AllRefs.OfType<Connector>())
                {
                    Element owner = other.Owner;
                    if (owner == null || owner.Id == connector.Owner.Id) continue;
                    if (owner is MEPSystem) continue;
                    partners.Add(Rid.Value(owner.Id));
                }
                if (partners.Count > 0) row["connected_to"] = partners;
            }
            catch { }
            return row;
        }

        /// <summary>
        /// The penetrant cross-section: the first profiled connector's size, in feet.
        /// Round reports diameter as both width and height. False when nothing on the
        /// element carries a profile - the caller refuses rather than inventing one.
        /// </summary>
        public static bool TryProfile(Element element, out string shape, out double widthFeet, out double heightFeet)
        {
            shape = null; widthFeet = 0; heightFeet = 0;
            ConnectorManager manager = ManagerOf(element);
            if (manager == null) return false;
            foreach (Connector connector in Ordered(manager))
            {
                try
                {
                    switch (connector.Shape)
                    {
                        case ConnectorProfileType.Round:
                            shape = "round"; widthFeet = heightFeet = connector.Radius * 2; return true;
                        case ConnectorProfileType.Rectangular:
                            shape = "rectangular"; widthFeet = connector.Width; heightFeet = connector.Height; return true;
                        case ConnectorProfileType.Oval:
                            shape = "oval"; widthFeet = connector.Width; heightFeet = connector.Height; return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static string SystemClassification(Connector connector)
        {
            try { return connector.MEPSystem?.GetType().Name; } catch { return null; }
        }

        private static string Safe(Func<string> read)
        {
            try { return read(); } catch { return null; }
        }
    }
}
