// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// IDS FAILURES AS BCF, and what the exported file says about where it sits.
//
// ONE TOPIC PER FAILED SPECIFICATION. A requirement is what somebody will be
// asked to fix, so it is the unit of a topic; the elements that fail it travel
// as the topic's component selection, by IFC GlobalId - the one identity BCF
// has, and one these elements really carry because they were read out of the
// file the BCF sits beside. Nothing here invents a GUID.
//
// The camera is aimed at the placement of the first failing element when the
// file lets that be resolved; when it does not, the topic's description says the
// camera is NOT aimed, because a viewpoint at the project origin looks exactly
// like an aimed one.
//
// VERIFIED THE WAY THE LEDGER'S BCF EXPORT IS: the zip is re-opened, every
// markup and viewpoint re-parsed as XML, every declared viewpoint found in the
// archive, and the topics and the GlobalIds counted against what was meant to
// be written. Structural, and exactly that is claimed - no consumer's round trip
// is proven.
//
// Revit-free: an IDS report and a parsed IFC in, a zip on disk out.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class IdsBcfTopic
    {
        public string Guid;
        public string Specification;
        public string Title;
        public string Description;
        public readonly List<string> GlobalIds = new List<string>();
        public int FailingElements;
        public double[] PointMm;
        public bool CameraAimed;
    }

    public static class IdsBcf
    {
        public const string ViewpointFile = "viewpoint.bcfv";

        /// <summary>Topics for every FAILED specification of a validation over the file.</summary>
        public static List<IdsBcfTopic> Topics(IdsReport report, IfcStepReader.Document ifc, string ifcFileName,
                                               string ifcSha256, int maxComponents)
        {
            var topics = new List<IdsBcfTopic>();
            if (report == null) return topics;
            if (maxComponents < 1) maxComponents = 1;
            string basis;
            double scale = ifc == null ? 1000.0 : IfcPlacement.LengthScale(ifc, out basis);

            foreach (IdsSpecificationResult result in report.Results.Where(r => r.Outcome == IdsOutcome.Fail))
            {
                var failing = result.Findings.Where(f => f.Outcome == IdsOutcome.Fail).ToList();
                var ids = failing.Where(f => !string.IsNullOrWhiteSpace(f.GlobalId))
                                 .Select(f => f.GlobalId).Distinct(StringComparer.Ordinal).ToList();
                var topic = new IdsBcfTopic
                {
                    Guid = TopicGuid(ifcSha256, result.Name, result.Identifier),
                    Specification = result.Name,
                    Title = "IDS: " + (result.Name ?? result.Identifier ?? "(unnamed specification)"),
                    FailingElements = ids.Count
                };
                topic.GlobalIds.AddRange(ids.Take(maxComponents));

                // Aim at the first failing element whose placement resolves.
                if (ifc != null)
                    foreach (IdsElementFinding finding in failing)
                    {
                        int id = IfcStepReader.ReferenceId(finding.EntityKey);
                        IfcEntity entity;
                        if (id <= 0 || !ifc.ById.TryGetValue(id, out entity)) continue;
                        IfcEntity placement = ifc.Resolve(entity.At(5));
                        if (placement == null) continue;
                        string why;
                        IfcTransform world = IfcPlacement.World(ifc, placement, scale, out why);
                        if (world == null) continue;
                        topic.PointMm = new[] { world.Origin[0], world.Origin[1], world.Origin[2] };
                        topic.CameraAimed = true;
                        break;
                    }

                var reasons = failing.Select(f => f.Reason).Where(r => !string.IsNullOrWhiteSpace(r))
                                     .Distinct().Take(3).ToList();
                var text = new StringBuilder();
                text.Append("Specification '").Append(result.Name).Append("'");
                if (!string.IsNullOrWhiteSpace(result.Identifier)) text.Append(" [").Append(result.Identifier).Append("]");
                text.Append(" fails on ").Append(ids.Count).Append(" element(s) of ").Append(ifcFileName)
                    .Append(" (").Append(result.Applicable).Append(" applicable, ").Append(result.Failing).Append(" failing). ");
                if (reasons.Count > 0) text.Append("Reasons: ").Append(string.Join(" | ", reasons)).Append(". ");
                if (ids.Count > topic.GlobalIds.Count)
                    text.Append("Only the first ").Append(topic.GlobalIds.Count).Append(" of ").Append(ids.Count)
                        .Append(" GlobalIds are selected in the viewpoint. ");
                text.Append(topic.CameraAimed
                    ? "The camera is aimed at the placement of the first failing element."
                    : "The camera is NOT aimed: no failing element's placement could be resolved, so the viewpoint " +
                      "looks at the project origin. Use the component selection.");
                text.Append(" Evidence level: ifc_validated, read from the exported file (sha256 ")
                    .Append(ifcSha256 ?? "unknown").Append(").");
                topic.Description = text.ToString();
                topics.Add(topic);
            }
            return topics;
        }

        /// <summary>A topic GUID that is the same for the same file and the same specification.</summary>
        public static string TopicGuid(string ifcSha256, string specificationName, string identifier)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    (ifcSha256 ?? "") + "\u001f" + (specificationName ?? "") + "\u001f" + (identifier ?? "")));
                var bytes = new byte[16];
                Array.Copy(hash, bytes, 16);
                return new Guid(bytes).ToString("D");
            }
        }

        public static string MarkupXml(IdsBcfTopic topic, string ifcFileName, string ifcProjectGlobalId, string creationUtc)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Markup>\n");
            sb.Append("  <Header>\n    <File");
            if (!string.IsNullOrWhiteSpace(ifcProjectGlobalId))
                sb.Append(" IfcProject=\"").Append(CoordinationRules.Xml(ifcProjectGlobalId)).Append("\"");
            sb.Append(" isExternal=\"true\">\n");
            sb.Append("      <Filename>").Append(CoordinationRules.Xml(ifcFileName)).Append("</Filename>\n");
            sb.Append("      <Date>").Append(CoordinationRules.Xml(creationUtc)).Append("</Date>\n");
            sb.Append("    </File>\n  </Header>\n");
            sb.Append("  <Topic Guid=\"").Append(topic.Guid).Append("\" TopicType=\"Issue\" TopicStatus=\"Open\">\n");
            sb.Append("    <Title>").Append(CoordinationRules.Xml(topic.Title)).Append("</Title>\n");
            sb.Append("    <Labels>IDS</Labels>\n");
            sb.Append("    <CreationDate>").Append(CoordinationRules.Xml(creationUtc)).Append("</CreationDate>\n");
            sb.Append("    <CreationAuthor>Horizun</CreationAuthor>\n");
            sb.Append("    <Description>").Append(CoordinationRules.Xml(topic.Description)).Append("</Description>\n");
            sb.Append("  </Topic>\n");
            sb.Append("  <Viewpoints Guid=\"").Append(TopicGuid(topic.Guid, "viewpoint", null)).Append("\">\n");
            sb.Append("    <Viewpoint>").Append(ViewpointFile).Append("</Viewpoint>\n");
            sb.Append("  </Viewpoints>\n");
            sb.Append("</Markup>\n");
            return sb.ToString();
        }

        /// <summary>
        /// Write the archive and RE-READ it. Returns the evidence on success, or null with the
        /// reason; a failed re-read is a failure even though a file exists.
        /// </summary>
        public static JObject WriteAndVerify(string path, IList<IdsBcfTopic> topics, string ifcFileName,
                                             string ifcProjectGlobalId, DateTime utcNow, out string error)
        {
            error = null;
            string creation = utcNow.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            if (File.Exists(path)) File.Delete(path);
            using (FileStream stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Entry(zip, "bcf.version", CoordinationRules.BcfVersionXml());
                foreach (IdsBcfTopic topic in topics)
                {
                    Entry(zip, topic.Guid + "/markup.bcf", MarkupXml(topic, ifcFileName, ifcProjectGlobalId, creation));
                    Entry(zip, topic.Guid + "/" + ViewpointFile,
                          BcfViewpoint.Xml(topic.PointMm, topic.GlobalIds.Select(g => new BcfComponent { IfcGuid = g })));
                }
            }

            int readTopics = 0, readComponents = 0;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    if (zip.GetEntry("bcf.version") == null) { error = "the archive holds no bcf.version entry."; return null; }
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (!entry.FullName.EndsWith("/markup.bcf", StringComparison.Ordinal)) continue;
                        XmlDocument markup = Load(entry);
                        XmlNode title = markup.DocumentElement?.SelectSingleNode("Topic/Title");
                        if (markup.DocumentElement?.Name != "Markup" || title == null || title.InnerText.Length == 0)
                        { error = "'" + entry.FullName + "' is not a BCF Markup with a titled Topic."; return null; }
                        XmlNode declared = markup.DocumentElement.SelectSingleNode("Viewpoints/Viewpoint");
                        if (declared == null) { error = "'" + entry.FullName + "' declares no viewpoint."; return null; }
                        string folder = entry.FullName.Substring(0, entry.FullName.Length - "markup.bcf".Length);
                        ZipArchiveEntry viewpoint = zip.GetEntry(folder + declared.InnerText);
                        if (viewpoint == null)
                        { error = "'" + entry.FullName + "' names viewpoint '" + declared.InnerText + "' and the archive does not hold it."; return null; }
                        XmlDocument view = Load(viewpoint);
                        if (view.DocumentElement?.Name != "VisualizationInfo" ||
                            view.DocumentElement.SelectSingleNode("PerspectiveCamera") == null)
                        { error = "the viewpoint beside '" + entry.FullName + "' is not a VisualizationInfo with a camera."; return null; }
                        XmlNodeList components = view.DocumentElement.SelectNodes("Components/Selection/Component[@IfcGuid]");
                        readComponents += components == null ? 0 : components.Count;
                        readTopics++;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is XmlException || ex is IOException)
            {
                error = "the archive could not be re-read: " + ex.Message;
                return null;
            }

            int expectedComponents = topics.Sum(t => t.GlobalIds.Count);
            if (readTopics != topics.Count)
            { error = "re-reading found " + readTopics + " topic(s) for " + topics.Count + " written."; return null; }
            if (readComponents != expectedComponents)
            { error = "re-reading found " + readComponents + " GlobalId component(s) for " + expectedComponents + " written."; return null; }

            byte[] bytes = File.ReadAllBytes(path);
            return new JObject
            {
                ["path"] = path,
                ["bytes"] = bytes.Length,
                ["sha256"] = Sha256(bytes),
                ["bcf_version"] = BcfViewpoint.Version,
                ["topics"] = readTopics,
                ["components_with_ifc_guid"] = readComponents,
                ["topic_list"] = new JArray(topics.Select(t => new JObject
                {
                    ["guid"] = t.Guid, ["specification"] = t.Specification, ["failing_elements"] = t.FailingElements,
                    ["selected_global_ids"] = t.GlobalIds.Count, ["camera_aimed"] = t.CameraAimed
                })),
                ["verified_by_reread"] = true,
                ["verification_scope"] = "STRUCTURAL: the zip was re-opened, every markup and viewpoint re-parsed as XML, " +
                    "every declared viewpoint found in the archive with a camera, and the topics and GlobalId components " +
                    "counted against what was written. No consumer's round trip is proven."
            };
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void Entry(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using (Stream s = entry.Open())
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        private static XmlDocument Load(ZipArchiveEntry entry)
        {
            var xml = new XmlDocument { XmlResolver = null };
            using (Stream s = entry.Open()) xml.Load(s);
            return xml;
        }
    }

    /// <summary>
    /// What an exported IFC says about where it sits. OBSERVED, never judged: the
    /// exporter decides these from the coordinate basis it was asked for, and this reads
    /// what it wrote so a person can compare it with the model's georeference.
    /// </summary>
    public static class IfcGeoreferenceReadback
    {
        public static JObject Read(IfcStepReader.Document ifc)
        {
            var result = new JObject();
            if (ifc == null) return result;
            string basis;
            double scale = IfcPlacement.LengthScale(ifc, out basis);
            result["length_unit"] = basis;

            var sites = new JArray();
            foreach (IfcEntity site in ifc.Of("IFCSITE").Take(5))
            {
                var row = new JObject { ["global_id"] = IfcStepReader.Text(site.At(0)), ["name"] = IfcStepReader.Text(site.At(2)) };
                IfcEntity placement = ifc.Resolve(site.At(5));
                string why = null;
                IfcTransform world = placement == null ? null : IfcPlacement.World(ifc, placement, scale, out why);
                row["placement_origin_mm"] = world == null ? (JToken)JValue.CreateNull()
                    : new JArray(world.Origin.Select(v => Math.Round(v, 3)));
                if (world == null) row["placement_unresolved"] = why ?? "no ObjectPlacement";
                row["ref_latitude"] = RawList(site.At(9));
                row["ref_longitude"] = RawList(site.At(10));
                double? elevation = IfcStepReader.Number(site.At(11));
                row["ref_elevation_mm"] = elevation.HasValue ? (JToken)Math.Round(elevation.Value * scale, 3) : JValue.CreateNull();
                sites.Add(row);
            }
            result["sites"] = sites;

            var conversions = new JArray();
            foreach (IfcEntity map in ifc.Of("IFCMAPCONVERSION").Take(5))
            {
                IfcEntity crs = ifc.Resolve(map.At(1));
                conversions.Add(new JObject
                {
                    ["target_crs"] = crs == null ? null : IfcStepReader.Text(crs.At(0)),
                    ["eastings"] = IfcStepReader.Number(map.At(2)),
                    ["northings"] = IfcStepReader.Number(map.At(3)),
                    ["orthogonal_height"] = IfcStepReader.Number(map.At(4)),
                    ["x_axis_abscissa"] = IfcStepReader.Number(map.At(5)),
                    ["x_axis_ordinate"] = IfcStepReader.Number(map.At(6)),
                    ["scale"] = IfcStepReader.Number(map.At(7))
                });
            }
            result["map_conversions"] = conversions;
            result["map_conversion_note"] = conversions.Count == 0
                ? "the file carries no IfcMapConversion (IFC2x3 has none; in IFC4 the exporter writes one only when " +
                  "the model has a coordinate system to convert to)."
                : "IfcMapConversion as written; eastings/northings/height are in the file's length unit.";

            foreach (IfcEntity context in ifc.Of("IFCGEOMETRICREPRESENTATIONCONTEXT"))
            {
                IfcEntity north = ifc.Resolve(context.At(5));
                if (north == null) continue;
                var ratios = IfcStepReader.List(north.At(0)).Select(IfcStepReader.Number).ToList();
                if (ratios.Count >= 2 && ratios[0].HasValue && ratios[1].HasValue)
                {
                    // TrueNorth is a direction in the project's XY plane; its angle from +Y,
                    // measured clockwise, is the rotation a surveyor reads.
                    double degrees = Math.Atan2(ratios[0].Value, ratios[1].Value) * 180.0 / Math.PI;
                    result["true_north_direction"] = new JArray(ratios[0].Value, ratios[1].Value);
                    result["true_north_angle_from_project_y_deg"] = Math.Round(degrees, 6);
                }
                break;
            }
            return result;
        }

        private static JToken RawList(string raw)
        {
            var parts = IfcStepReader.List(raw);
            if (parts == null || parts.Count == 0) return JValue.CreateNull();
            return new JArray(parts.Select(p => (JToken)(IfcStepReader.Number(p).HasValue ? (JToken)IfcStepReader.Number(p).Value : p)));
        }
    }
}
