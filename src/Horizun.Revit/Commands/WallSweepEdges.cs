// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_wall_sweep, the HOSTED-EDGE half: slab edges, fascias and gutters.
//
//   read host_id=N      the edges and faces of a floor or roof as STABLE REFERENCES
//                       (Reference.ConvertToStableRepresentation), the slab-edge,
//                       fascia and gutter types of the model, and what is already
//                       hosted on that element. Read-only.
//   place_edge          Document.NewSlabEdge / NewFascia / NewGutter with a ReferenceArray
//                       of the edges named (edge_references) and of every edge of the
//                       faces named (face_references). Through ModelEditRunner: rehearse,
//                       single-use token, re-read after commit, roll back on disagreement.
//
// The three Create methods and the three element classes are identical in Revit 2023-2027
// (measured by reflection over each year's RevitAPI.dll): no #if is needed here. Fascia,
// Gutter and their types live in Autodesk.Revit.DB.Architecture and are named in full so
// that namespace is not imported into a file that also uses DB's own Room and friends.
//
// WHAT THE RE-READ CAN AND CANNOT SAY. The element exists, is of the class asked for, has
// the type asked for, and its box touches its host's box. WHICH of the host's edges it
// ended on is Revit's reading of the references and is reported as evidence (segment count
// and reference-curve length against the edges named) rather than asserted: a sweep over a
// chain of edges reports its curve as one or as several pieces depending on the version,
// and that was not measured live when this was written.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class WallSweepCommand
    {
        private const int MaxEdgeRows = 300;

        private static IEnumerable<string> ClassChain(object o)
        {
            for (Type t = o?.GetType(); t != null && t != typeof(object); t = t.BaseType) yield return t.Name;
        }

        private static string StableKey(Document doc, Reference reference)
        {
            try
            {
                string key = reference?.ConvertToStableRepresentation(doc);
                return string.IsNullOrEmpty(key) ? null : key;
            }
            catch { return null; }
        }

        private static List<Solid> SolidsOf(Element host)
        {
            var solids = new List<Solid>();
            GeometryElement geometry = host.get_Geometry(new Options
            {
                ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false
            });
            if (geometry == null) return solids;
            foreach (GeometryObject go in geometry)
            {
                Solid s = go as Solid;
                if (s != null && s.Faces.Size > 0 && s.Volume > 1e-9) solids.Add(s);
            }
            return solids;
        }

        private static IEnumerable<Edge> EdgesOfFace(Face face)
        {
            foreach (EdgeArray loop in face.EdgeLoops)
                foreach (Edge edge in loop)
                    yield return edge;
        }

        private static JArray TypeRows(Document doc, Type typeClass, double scale)
            => new JArray(new FilteredElementCollector(doc).OfClass(typeClass).Cast<ElementType>()
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new JObject { ["type_id"] = Rid.Value(t.Id), ["name"] = t.Name }));

        private static JArray HostedOn(Document doc, Element host)
        {
            var rows = new JArray();
            var classes = new[]
            {
                new KeyValuePair<string, Type>(HostedEdgeRules.SlabEdge, typeof(SlabEdge)),
                new KeyValuePair<string, Type>(HostedEdgeRules.Fascia, typeof(Autodesk.Revit.DB.Architecture.Fascia)),
                new KeyValuePair<string, Type>(HostedEdgeRules.Gutter, typeof(Autodesk.Revit.DB.Architecture.Gutter))
            };
            foreach (var pair in classes)
            {
                ICollection<ElementId> ids;
                try { ids = host.GetDependentElements(new ElementClassFilter(pair.Value)); }
                catch { continue; }
                foreach (ElementId id in ids)
                {
                    Element e = doc.GetElement(id);
                    if (e == null) continue;
                    rows.Add(new JObject { ["element_id"] = Rid.Value(id), ["kind"] = pair.Key, ["type_id"] = Rid.Value(e.GetTypeId()) });
                }
            }
            return rows;
        }

        // ---- read host_id ------------------------------------------------------------------

        private static ArchModelEdit ReadEdges(Document doc, JObject r, double scale)
        {
            Element host = ModelEditRunner.Need<Element>(doc, r, "host_id");
            var chain = ClassChain(host).ToList();
            var result = new JObject
            {
                ["read_only"] = true,
                ["host"] = new JObject
                {
                    ["element_id"] = Rid.Value(host.Id), ["class"] = host.GetType().Name,
                    ["category"] = host.Category?.Name, ["built_in_category"] = CategoryKey.Of(host), ["name"] = host.Name
                },
                ["fits"] = new JObject
                {
                    [HostedEdgeRules.SlabEdge] = HostedEdgeRules.HostFits(HostedEdgeRules.SlabEdge, chain),
                    [HostedEdgeRules.Fascia] = HostedEdgeRules.HostFits(HostedEdgeRules.Fascia, chain),
                    [HostedEdgeRules.Gutter] = HostedEdgeRules.HostFits(HostedEdgeRules.Gutter, chain)
                },
                ["types"] = new JObject
                {
                    [HostedEdgeRules.SlabEdge] = TypeRows(doc, typeof(SlabEdgeType), scale),
                    [HostedEdgeRules.Fascia] = TypeRows(doc, typeof(Autodesk.Revit.DB.Architecture.FasciaType), scale),
                    [HostedEdgeRules.Gutter] = TypeRows(doc, typeof(Autodesk.Revit.DB.Architecture.GutterType), scale)
                },
                ["hosted_now"] = HostedOn(doc, host)
            };

            var faces = new JArray();
            var edges = new JArray();
            var seen = new Dictionary<string, JObject>(StringComparer.Ordinal);
            int unreferenced = 0, faceIndex = 0, totalEdges = 0;
            foreach (Solid solid in SolidsOf(host))
                foreach (Face face in solid.Faces)
                {
                    int index = faceIndex++;
                    string role = "curved";
                    PlanarFace planar = face as PlanarFace;
                    if (planar != null) role = HostedEdgeRules.FaceRole(planar.FaceNormal.X, planar.FaceNormal.Y, planar.FaceNormal.Z);
                    int count = 0;
                    foreach (Edge edge in EdgesOfFace(face))
                    {
                        count++;
                        string key = StableKey(doc, edge.Reference);
                        if (key == null) { unreferenced++; continue; }
                        if (seen.TryGetValue(key, out JObject known)) { ((JArray)known["faces"]).Add(index); continue; }
                        totalEdges++;
                        var row = new JObject { ["edge_key"] = key, ["faces"] = new JArray(index) };
                        try
                        {
                            Curve curve = edge.AsCurve();
                            XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
                            row["length"] = Math.Round(curve.Length / scale, 3);
                            row["start"] = ModelEditRunner.Arr(a, scale);
                            row["end"] = ModelEditRunner.Arr(b, scale);
                            row["horizontal"] = Math.Abs(a.Z - b.Z) < 1e-6;
                            row["straight"] = curve is Line;
                        }
                        catch (Exception ex) { row["curve_error"] = ex.Message; }
                        seen[key] = row;
                        if (edges.Count < MaxEdgeRows) edges.Add(row);
                    }
                    faces.Add(new JObject
                    {
                        ["index"] = index, ["face_key"] = StableKey(doc, face.Reference), ["role"] = role,
                        ["edges"] = count, ["area"] = Math.Round(face.Area / (scale * scale), 3)
                    });
                }
            result["faces"] = faces;
            result["edges"] = edges;
            result["edge_count"] = totalEdges;
            result["edges_truncated"] = totalEdges > edges.Count;
            if (unreferenced > 0) result["edges_without_reference"] = unreferenced;
            result["note"] = "Pass edge_key values as edge_references (or a face's face_key as face_references: every edge of that face is " +
                             "taken) to operation=place_edge with kind and a type_id from types. An edge shared by two faces is listed once with both " +
                             "face indexes. A chain of edges must be contiguous; Revit's own refusal is returned when it is not.";
            return new ArchModelEdit { ReadResult = result };
        }

        // ---- place_edge --------------------------------------------------------------------

        private sealed class EdgeWanted
        {
            public string Key;
            public Reference Reference;
            public double LengthFeet;
        }

        private static IEnumerable<string> Strings(JToken token)
            => token is JArray list ? list.Select(t => (string)t) : Enumerable.Empty<string>();

        private static Reference Parse(Document doc, Element host, string stable, string field)
        {
            Reference reference;
            try { reference = Reference.ParseFromStableRepresentation(doc, stable); }
            catch (Exception ex) { throw new ArgumentException(field + " '" + Short(stable) + "' is not a stable reference of this document (" + ex.Message + ")."); }
            if (reference == null || reference.ElementId != host.Id)
                throw new ArgumentException(field + " '" + Short(stable) + "' does not belong to host_id " + Rid.Value(host.Id) +
                    ". Read its edges with operation=read host_id=" + Rid.Value(host.Id) + ".");
            return reference;
        }

        private static string Short(string s) => s != null && s.Length > 60 ? s.Substring(0, 57) + "..." : s;

        private static ArchModelEdit PlaceEdge(Document doc, JObject r, double scale)
        {
            string kind = r.Value<string>("kind");
            ElementType type = ModelEditRunner.Need<ElementType>(doc, r, "type_id");
            string typeClass = HostedEdgeRules.TypeClass(kind);
            if (type.GetType().Name != typeClass)
                throw new ArgumentException("type_id " + Rid.Value(type.Id) + " is a " + type.GetType().Name + ", and kind=" + kind +
                    " needs a " + typeClass + ". operation=read with host_id lists them.");
            Element host = ModelEditRunner.Need<Element>(doc, r, "host_id");
            if (!HostedEdgeRules.HostFits(kind, ClassChain(host)))
                throw new ArgumentException("host_id " + Rid.Value(host.Id) + " is a " + host.GetType().Name + ", and kind=" + kind +
                    " is hosted on " + HostedEdgeRules.HostRequirement(kind) + ".");

            var wanted = new List<EdgeWanted>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string stable in Strings(r["edge_references"]))
            {
                Reference reference = Parse(doc, host, stable, "edge_references");
                Edge edge = host.GetGeometryObjectFromReference(reference) as Edge;
                if (edge == null)
                    throw new ArgumentException("edge_references '" + Short(stable) + "' names a " +
                        (host.GetGeometryObjectFromReference(reference)?.GetType().Name ?? "nothing") +
                        ", not an edge. A face goes in face_references.");
                if (keys.Add(stable)) wanted.Add(new EdgeWanted { Key = stable, Reference = reference, LengthFeet = edge.ApproximateLength });
            }
            foreach (string stable in Strings(r["face_references"]))
            {
                Reference reference = Parse(doc, host, stable, "face_references");
                Face face = host.GetGeometryObjectFromReference(reference) as Face;
                if (face == null)
                    throw new ArgumentException("face_references '" + Short(stable) + "' does not name a face. An edge goes in edge_references.");
                foreach (Edge edge in EdgesOfFace(face))
                {
                    string key = StableKey(doc, edge.Reference);
                    if (key == null) throw new ArgumentException("an edge of face_references '" + Short(stable) + "' has no reference Revit can host on.");
                    if (keys.Add(key)) wanted.Add(new EdgeWanted { Key = key, Reference = edge.Reference, LengthFeet = edge.ApproximateLength });
                }
            }
            if (wanted.Count == 0) throw new ArgumentException("the references name no edge.");
            if (wanted.Count > HostedEdgeRules.MaxEdgeReferences)
                throw new ArgumentException("the references expand to " + wanted.Count + " edges; the limit is " + HostedEdgeRules.MaxEdgeReferences + ".");

            double totalFeet = wanted.Sum(w => w.LengthFeet);
            var edit = new ArchModelEdit();
            edit.Planned.Add(ModelEditRunner.Planned(host, PlannedAction.Modify, r));
            edit.Summary["operation"] = "place_edge";
            edit.Summary["kind"] = kind;
            edit.Summary["type"] = new JObject { ["type_id"] = Rid.Value(type.Id), ["name"] = type.Name };
            edit.Summary["host"] = new JObject { ["element_id"] = Rid.Value(host.Id), ["class"] = host.GetType().Name, ["name"] = host.Name };
            edit.Summary["edges"] = wanted.Count;
            edit.Summary["total_length"] = Math.Round(totalFeet / scale, 3);

            long createdId = -1;
            edit.Apply = d =>
            {
                var refs = new ReferenceArray();
                foreach (EdgeWanted w in wanted) refs.Append(w.Reference);
                Element made;
                switch (kind)
                {
                    case HostedEdgeRules.SlabEdge:
                        made = d.Create.NewSlabEdge((SlabEdgeType)d.GetElement(type.Id), refs);
                        break;
                    case HostedEdgeRules.Fascia:
                        made = d.Create.NewFascia((Autodesk.Revit.DB.Architecture.FasciaType)d.GetElement(type.Id), refs);
                        break;
                    default:
                        made = d.Create.NewGutter((Autodesk.Revit.DB.Architecture.GutterType)d.GetElement(type.Id), refs);
                        break;
                }
                if (made == null) throw new InvalidOperationException("Revit created no " + kind + " and gave no reason.");
                createdId = Rid.Value(made.Id);
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("created", "class", "type_id", "at_host");
                Element made = createdId > 0 && Rid.CanRepresent(createdId) ? d.GetElement(Rid.Make(createdId)) : null;
                check.Compare("created", 1, made == null ? 0 : 1);
                string wantedClass = HostedEdgeRules.CreatedClass(kind);
                string foundClass = made?.GetType().Name;
                check.Record("class", wantedClass, foundClass, made != null && string.Equals(foundClass, wantedClass, StringComparison.Ordinal));
                check.Record("type_id", Rid.Value(type.Id), made == null ? (long?)null : Rid.Value(made.GetTypeId()),
                             made != null && made.GetTypeId() == type.Id);
                bool atHost = false;
                if (made != null)
                {
                    BoundingBoxXYZ sweepBox = made.get_BoundingBox(null);
                    BoundingBoxXYZ hostBox = d.GetElement(host.Id)?.get_BoundingBox(null);
                    if (sweepBox != null && hostBox != null)
                        atHost = HostedEdgeRules.BoxesTouch(
                            new[] { sweepBox.Min.X, sweepBox.Min.Y, sweepBox.Min.Z }, new[] { sweepBox.Max.X, sweepBox.Max.Y, sweepBox.Max.Z },
                            new[] { hostBox.Min.X, hostBox.Min.Y, hostBox.Min.Z }, new[] { hostBox.Max.X, hostBox.Max.Y, hostBox.Max.Z },
                            100.0 / 304.8);
                    var ev = new JObject { ["element_id"] = createdId, ["kind"] = kind, ["edges_named"] = wanted.Count,
                        ["edges_total_length"] = Math.Round(totalFeet / scale, 3) };
                    HostedSweep sweep = made as HostedSweep;
                    if (sweep != null)
                    {
                        try { ev["first_segment_curve_length"] = Math.Round(sweep.get_ReferenceCurve(wanted[0].Reference).Length / scale, 3); ev["first_edge_length"] = Math.Round(wanted[0].LengthFeet / scale, 3); } catch { ev["first_segment_curve_length"] = "unreadable"; }
                        try { ev["angle_degrees"] = Math.Round(sweep.Angle * 180.0 / Math.PI, 3); } catch { }
                    }
                    try
                    {
                        ev["dependent_of_host"] = d.GetElement(host.Id).GetDependentElements(new ElementClassFilter(made.GetType())).Contains(made.Id);
                    }
                    catch { ev["dependent_of_host"] = "unreadable"; }
                    edit.Evidence["sweep"] = ev;
                }
                check.Record("at_host", true, atHost, atHost);
                return check;
            };
            return edit;
        }
    }
}
