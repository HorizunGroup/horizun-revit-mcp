// -----------------------------------------------------------------------------
// Horizun Revit MCP - deterministic, read-only MEP production planning.
//
// horizun_plan_mep answers "run this pipe along these points" the way
// plan_structure answers grids: MEASURE (the types, the system, the polyline),
// DECIDE with rules proved in Core/MepRouteRules.cs, and return a complete
// horizun_create_elements request - one atomic batch of pipes/ducts plus the
// elbows between consecutive segments as batch_index fittings, so the whole
// run commits verified or not at all. This command writes NOTHING.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class PlanMepCommand : ICommand
    {
        public string Name => "horizun_plan_mep";
        public string Description =>
            "Plan a multi-segment pipe or duct run along an explicit polyline, deterministically and READ-ONLY: " +
            "collinear vertices are merged and named, degenerate segments refuse with the measured millimetres, " +
            "and the reply is a ready create_elements request - segments plus batch_index elbows - that commits " +
            "as ONE atomic batch through the normal rehearse/token/verify pipeline.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();
            if (operation == "network_census") return NetworkCensus(doc, request);
            if (operation != "route_run")
                return CommandResult.Fail("operation must be route_run or network_census.");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double toFeet;
            if (units == "mm") toFeet = 1 / 304.8;
            else if (units == "m") toFeet = 1 / 0.3048;
            else if (units == "feet") toFeet = 1;
            else return CommandResult.Fail("units must be mm, m or feet.");
            double fromFeet = 1 / toFeet;

            string kind = (request.Value<string>("kind") ?? "pipe").ToLowerInvariant();
            if (kind != "pipe" && kind != "duct")
                return CommandResult.Fail("kind must be pipe or duct (conduit/cable_tray runs join their own way; plan them per segment).");

            long levelId = request.Value<long?>("level_id") ?? -1;
            Level level = Rid.CanRepresent(levelId) ? doc.GetElement(Rid.Make(levelId)) as Level : null;
            if (level == null) return CommandResult.Fail("level_id must identify a Level.");

            long typeId = request.Value<long?>("type_id") ?? -1;
            Element curveType = Rid.CanRepresent(typeId) ? doc.GetElement(Rid.Make(typeId)) : null;
            bool typeOk = kind == "pipe" ? curveType is PipeType : curveType is DuctType;
            if (!typeOk) return CommandResult.Fail("type_id must identify a " + (kind == "pipe" ? "PipeType" : "DuctType") + ".");

            long systemTypeId = request.Value<long?>("system_type_id") ?? -1;
            Element systemType = Rid.CanRepresent(systemTypeId) ? doc.GetElement(Rid.Make(systemTypeId)) : null;
            bool systemOk = kind == "pipe" ? systemType is PipingSystemType : systemType is MechanicalSystemType;
            if (!systemOk) return CommandResult.Fail("system_type_id must identify a " +
                (kind == "pipe" ? "PipingSystemType" : "MechanicalSystemType") + ".");

            var pointsToken = request["points"] as JArray;
            var points = new List<double[]>();
            if (pointsToken != null)
                foreach (JToken token in pointsToken)
                {
                    var xyz = token as JArray;
                    if (xyz == null || xyz.Count != 3)
                        return CommandResult.Fail("every route point is [x, y, z] in " + units + ".");
                    points.Add(new[] { (double)xyz[0] * toFeet, (double)xyz[1] * toFeet, (double)xyz[2] * toFeet });
                }

            List<RouteSegment> segments; List<int> merged;
            string error = MepRouteRules.Segments(points, out segments, out merged);
            if (error != null) return CommandResult.Fail(error + " Nothing was planned.");

            var elements = new JArray();
            var plannedRows = new JArray();
            for (int i = 0; i < segments.Count; i++)
            {
                RouteSegment segment = segments[i];
                elements.Add(new JObject
                {
                    ["kind"] = kind,
                    ["type_id"] = Rid.Value(curveType.Id),
                    ["system_type_id"] = Rid.Value(systemType.Id),
                    ["level_id"] = Rid.Value(level.Id),
                    ["start"] = MmPoint(segment.Start, fromFeet),
                    ["end"] = MmPoint(segment.End, fromFeet)
                });
                plannedRows.Add(new JObject
                {
                    ["segment"] = i,
                    ["from_vertex"] = segment.FromVertex, ["to_vertex"] = segment.ToVertex,
                    ["length_mm"] = Math.Round(Length(segment) * 304.8, 1)
                });
            }
            // The corners: one elbow per consecutive segment pair, referencing the
            // batch entries above - resolved inside the same atomic transaction.
            for (int i = 0; i + 1 < segments.Count; i++)
                elements.Add(new JObject
                {
                    ["kind"] = "fitting",
                    ["fitting"] = "elbow",
                    ["elements"] = new JArray(
                        new JObject { ["batch_index"] = i },
                        new JObject { ["batch_index"] = i + 1 })
                });

            var result = new JObject
            {
                ["operation"] = "route_run",
                ["kind"] = kind,
                ["points_given"] = points.Count,
                ["segments_planned"] = segments.Count,
                ["elbows_planned"] = Math.Max(0, segments.Count - 1),
                ["collinear_vertices_merged"] = new JArray(merged),
                ["segments"] = plannedRows,
                ["coverage"] = "complete",
                ["note"] = merged.Count == 0
                    ? "Every requested vertex became a corner or an end."
                    : merged.Count + " collinear vertex(es) were merged - the run has fewer corners than the " +
                      "request had points, and each folded vertex is named above.",
                ["next_arguments"] = new JObject
                {
                    ["tool"] = "horizun_create_elements",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = doc.Title,
                        ["units"] = "mm",
                        ["elements"] = elements
                    }
                }
            };
            return CommandResult.Ok(result);
        }

        // ---- network_census: connectivity as MEASURED through connectors. -------
        // Two elements are in one component only when a connector of one IS
        // CONNECTED to a connector of the other - geometric coincidence does not
        // count, which is exactly the difference between a drawing that looks
        // joined and a network a flow calculation can cross.
        private static CommandResult NetworkCensus(Document doc, JObject request)
        {
            var seeds = new List<Element>();
            if (request["element_ids"] is JArray idsToken && idsToken.Count > 0)
            {
                foreach (JToken token in idsToken)
                {
                    long id = (long)token;
                    Element element = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (element == null) return CommandResult.Fail("element_ids: " + id + " does not resolve.");
                    seeds.Add(element);
                }
            }
            else
            {
                foreach (Element element in new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)))
                    seeds.Add(element);
                if (seeds.Count > 2000)
                    return CommandResult.Fail("the model carries " + seeds.Count + " MEP curves; a whole-model " +
                        "census over 2000 needs explicit element_ids to bound it. Nothing was traversed.");
            }

            var visited = new HashSet<long>();
            var components = new JArray();
            int totalOpen = 0, unreadable = 0;
            foreach (Element seed in seeds)
            {
                if (visited.Contains(Rid.Value(seed.Id))) continue;
                // BFS across CONNECTED connectors only.
                var queue = new Queue<Element>();
                var members = new List<Element>();
                queue.Enqueue(seed);
                visited.Add(Rid.Value(seed.Id));
                int openEnds = 0;
                var systems = new HashSet<string>();
                var domains = new HashSet<string>();
                while (queue.Count > 0 && members.Count < 5000)
                {
                    Element current = queue.Dequeue();
                    members.Add(current);
                    ConnectorManager manager;
                    try { manager = MepFacts.ManagerOf(current); } catch { unreadable++; continue; }
                    if (manager == null) continue;
                    foreach (Connector connector in MepFacts.Ordered(manager))
                    {
                        try
                        {
                            domains.Add(connector.Domain.ToString());
                            if (connector.MEPSystem != null) systems.Add(connector.MEPSystem.Name);
                            if (!connector.IsConnected) { openEnds++; continue; }
                            foreach (Connector other in connector.AllRefs.OfType<Connector>())
                            {
                                Element neighbor = other.Owner;
                                if (neighbor == null || neighbor.Id == current.Id) continue;
                                if (!(neighbor is MEPCurve) && !(neighbor is FamilyInstance)) continue;
                                if (visited.Add(Rid.Value(neighbor.Id))) queue.Enqueue(neighbor);
                            }
                        }
                        catch { unreadable++; }
                    }
                }
                totalOpen += openEnds;
                components.Add(new JObject
                {
                    ["elements"] = members.Count,
                    ["open_connectors"] = openEnds,
                    ["closed"] = openEnds == 0,
                    ["systems"] = new JArray(systems.OrderBy(x => x, StringComparer.Ordinal)),
                    ["domains"] = new JArray(domains.OrderBy(x => x, StringComparer.Ordinal)),
                    ["example_ids"] = new JArray(members.Take(5).Select(m => Rid.Value(m.Id)))
                });
            }
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "network_census",
                ["components"] = components,
                ["component_count"] = components.Count,
                ["open_connectors_total"] = totalOpen,
                ["unreadable_connectors"] = unreadable,
                ["note"] = "membership is CONNECTOR CONNECTIVITY, never geometric coincidence: an element joins a " +
                           "component only through a connector Revit reports IsConnected to one of its members." +
                           (unreadable == 0 ? "" : " " + unreadable + " connector(s) could not be read and are " +
                            "counted here rather than silently dropped.")
            });
        }

        private static JArray MmPoint(double[] feet, double fromFeet) => new JArray(
            Math.Round(feet[0] * 304.8, 1), Math.Round(feet[1] * 304.8, 1), Math.Round(feet[2] * 304.8, 1));

        private static double Length(RouteSegment segment)
        {
            double dx = segment.End[0] - segment.Start[0];
            double dy = segment.End[1] - segment.Start[1];
            double dz = segment.End[2] - segment.Start[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
