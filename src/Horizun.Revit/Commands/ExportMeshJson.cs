using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ExportCommand
    {
        private static CommandResult ExecuteMeshJson(UIApplication app, GateResult gate, Document doc, JObject r, string output)
        {
            string error = ExportDeliveryRules.MeshArguments(r);
            if (error != null) return CommandResult.RefuseBeforeWrite(error);
            if (!string.Equals(Path.GetExtension(output), ".json", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.GetDirectoryName(output)))
                return CommandResult.RefuseBeforeWrite("mesh_json requires a .json path in an existing folder.");
            if (File.Exists(output)) return CommandResult.RefuseBeforeWrite("mesh_json never overwrites an existing file; choose a new output_path.");
            bool includeDefaultSubelements = r["categories"] == null;
            string[] categories = r["categories"] is JArray requested ? requested.Values<string>().ToArray() :
                doc.Settings.Categories.Cast<Category>().Where(c => c.CategoryType == CategoryType.Model && c.HasMaterialQuantities)
                    .Select(c => Enum.GetName(typeof(BuiltInCategory), (BuiltInCategory)Rid.Value(c.Id))).Where(s => s != null).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
            if (includeDefaultSubelements) categories = categories.Where(ReliabilityDefaults.SolidCategory).ToArray();
            foreach (string category in categories)
                if (!Enum.TryParse(category, out BuiltInCategory value) || !Enum.IsDefined(typeof(BuiltInCategory), value))
                    return CommandResult.RefuseBeforeWrite("Unknown OST_ category: " + category);
            bool links = r.Value<bool?>("include_links") ?? false;
            bool skipDirect = r.Value<bool?>("skip_directshape") ?? true;
            var options = new JObject { ["output_path"] = output, ["categories"] = new JArray(categories),
                ["include_links"] = links, ["skip_directshape"] = skipDirect, ["include_default_subelements"] = includeDefaultSubelements };
            return MeshExportRun.Run(r, output, options, gate.Fingerprint, DocumentGate.Confirmations,
                (Stream stream, out JObject summary) => MeshPayload(doc, categories, links, skipDirect, includeDefaultSubelements, stream, out summary),
                ExportDeliveryRules.MeshHash);
        }

        private static IEnumerable<KeyValuePair<string, Element>> MeshCandidates(Document doc, string[] categories)
        {
            foreach (string name in categories.OrderBy(ReliabilityDefaults.MeshCategoryOrder).ThenBy(s => s, StringComparer.Ordinal))
            {
                var category = (BuiltInCategory)Enum.Parse(typeof(BuiltInCategory), name);
                if (category == BuiltInCategory.OST_RvtLinks) continue;
                foreach (Element element in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType()
                    .OrderBy(e => Rid.Value(e.Id)))
                    if (ReliabilityDefaults.ExportLeaf(element is RevitLinkInstance, element is Group))
                        yield return new KeyValuePair<string, Element>(name, element);
            }
        }

        private static IEnumerable<KeyValuePair<string, Element>> MeshChildren(Document doc, Element element, Action<Element, string> omit)
        {
            var children = new List<KeyValuePair<string, Element>>();
            try
            {
                string parentType = element.GetType().Name;
                foreach (ElementId dependentId in element.GetDependentElements(null).OrderBy(Rid.Value))
                {
                    Element dependent = doc.GetElement(dependentId);
                    if (dependent == null || !ReliabilityDefaults.MeshSubelement(parentType, dependent.GetType().Name)) continue;
                    Category category = dependent.Category;
                    string name = category == null ? "uncategorized" :
                        Enum.GetName(typeof(BuiltInCategory), (BuiltInCategory)Rid.Value(category.Id)) ?? category.Name;
                    children.Add(new KeyValuePair<string, Element>(name, dependent));
                }
            }
            catch (Exception ex) { omit(element, "hierarchy_unreadable: " + ex.Message); }
            return children;
        }

        private static IEnumerable<double[][]> MeshFaces(Element element, Transform transform, Action<Element, string> omit)
        {
            var mesh = HostSolidMesh.ForExport(element, out string reason);
            if (mesh == null) { omit(element, reason); yield break; }
            bool reflected = transform.Determinant < 0;
            foreach (int[] triangle in mesh.Triangles)
            {
                int[] indices = reflected ? new[] { triangle[0], triangle[2], triangle[1] } : triangle;
                yield return indices.Select(index =>
                {
                    double[] v = mesh.Vertices[index];
                    XYZ p = transform.OfPoint(new XYZ(v[0] / 304.8, v[1] / 304.8, v[2] / 304.8)) * 0.3048;
                    return new[] { p.X, p.Y, p.Z };
                }).ToArray();
            }
        }

        private static string MeshPayload(Document root, string[] categories, bool links, bool skipDirect,
            bool includeDefaultSubelements, Stream destination, out JObject summary)
        {
            var counts = new JObject(); var ranges = new JArray(); var provenance = new JArray(); var omitted = new JArray();
            long triangles = 0, excluded = 0;
            using (var sha = SHA256.Create())
            using (var crypto = new CryptoStream(destination, sha, CryptoStreamMode.Write, true))
            using (var text = new StreamWriter(crypto, new UTF8Encoding(false), 65536, true))
            using (var writer = new JsonTextWriter(text) { CloseOutput = false, Culture = CultureInfo.InvariantCulture })
            {
                writer.WriteStartObject(); writer.WritePropertyName("triangles"); writer.WriteStartArray();
                var chain = new HashSet<Document>();
                Action<Document, Transform, JArray> collect = null;
                collect = (doc, transform, path) =>
                {
                    if (!chain.Add(doc)) { omitted.Add(new JObject { ["link_path"] = path.DeepClone(), ["reason"] = "cyclic_link" }); return; }
                    Action<Element, string> omit = (element, reason) => omitted.Add(new JObject
                    {
                        ["unique_id"] = element.UniqueId, ["link_path"] = path.DeepClone(), ["reason"] = reason
                    });
                    IEnumerable<double[][]> Read(Element element)
                    {
                        if (!skipDirect || !(element is DirectShape)) return MeshFaces(element, transform, omit);
                        excluded++;
                        return Enumerable.Empty<double[][]>();
                    }
                    new MeshHierarchyFaces().Collect(MeshCandidates(doc, categories), candidate => Rid.Value(candidate.Value.Id),
                        candidate => ReliabilityDefaults.MeshContainer(candidate.Value.GetType().Name),
                        candidate => MeshChildren(doc, candidate.Value, omit), includeDefaultSubelements,
                        candidate => Read(candidate.Value), (candidate, faces) =>
                        {
                            long first = triangles;
                            foreach (double[][] triangle in faces)
                            {
                                foreach (double[] p in triangle)
                                {
                                    writer.WriteValue(p[0]); writer.WriteValue(p[1]); writer.WriteValue(p[2]);
                                }
                                triangles++;
                            }
                            long count = triangles - first;
                            if (count == 0) return;
                            string name = candidate.Key;
                            Element element = candidate.Value;
                            counts[name] = (counts.Value<long?>(name) ?? 0) + 1;
                            ranges.Add(new JArray(name, first, count));
                            provenance.Add(new JObject { ["document"] = doc.Title, ["element_id"] = Rid.Value(element.Id), ["unique_id"] = element.UniqueId,
                                ["type_id"] = Rid.Value(element.GetTypeId()), ["category"] = name, ["link_path"] = path.DeepClone(), ["first_triangle"] = first, ["triangle_count"] = count });
                        });
                    if (links)
                        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().OrderBy(l => Rid.Value(l.Id)))
                        {
                            var next = (JArray)path.DeepClone(); next.Add(link.UniqueId);
                            var linked = link.GetLinkDocument();
                            if (linked == null) omitted.Add(new JObject { ["link_path"] = next, ["reason"] = "link_not_loaded" });
                            else if (path.Count >= 16) omitted.Add(new JObject { ["link_path"] = next, ["reason"] = "link_depth_limit" });
                            else collect(linked, transform.Multiply(link.GetTotalTransform()), next);
                        }
                    chain.Remove(doc);
                };
                collect(root, Transform.Identity, new JArray());
                writer.WriteEndArray();
                summary = new JObject { ["triangles"] = triangles, ["elements"] = counts, ["omitted"] = omitted,
                    ["excluded_directshapes"] = excluded, ["complete"] = omitted.Count == 0,
                    ["triangulation"] = "Revit native Fine-detail tessellation; no measured chord-error bound." };
                foreach (var p in summary.Properties()) { writer.WritePropertyName(p.Name == "triangles" ? "triangle_count" : p.Name); p.Value.WriteTo(writer); }
                writer.WritePropertyName("ranges"); ranges.WriteTo(writer);
                writer.WritePropertyName("element_ranges");
                new JArray(provenance.OfType<JObject>().Select(p => (JToken)new JArray(p["document"], p["element_id"], p["type_id"], p["category"], p["first_triangle"], p["triangle_count"]))).WriteTo(writer);
                writer.WritePropertyName("provenance"); provenance.WriteTo(writer);
                writer.WritePropertyName("units"); writer.WriteValue("metres");
                writer.WritePropertyName("coordinate_system"); writer.WriteValue("host_internal");
                writer.WriteEndObject(); writer.Flush(); text.Flush(); crypto.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
    }
}
