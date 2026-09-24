// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_deliver_ifc, the Revit-free half: the property-set mapping parser,
// the check of the mapping against an exported file, the header/trailer proof,
// the gates and the one rule that makes a delivery ready, the container name,
// and the BCF of IDS failures - written to disk and re-read.
//
// The IFC files here are small and hand-written on purpose: every entity is one
// the assertion is about, so a passing test cannot be passing by accident.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IfcDeliveryTests
    {
        private const string Mapping =
            "# generic delivery mapping\n" +
            "PropertySet:\tHZ_Delivery\tI\tIfcWall\n" +
            "\tCode\tText\tHZ_Code\n" +
            "PropertySet:\tHZ_TypeData\tT\tIfcWall\n" +
            "\tMaker\tLabel\n";

        private static string IfcText(string data, string schema = "IFC4", bool trailer = true) =>
            "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [ReferenceView]'),'2;1');\n" +
            "FILE_SCHEMA(('" + schema + "'));\nENDSEC;\nDATA;\n" + data + "\nENDSEC;\n" +
            (trailer ? "END-ISO-10303-21;\n" : "");

        /// <summary>Two walls sharing a type; only wall A carries the instance set.</summary>
        private const string TwoWalls =
            "#1=IFCPROJECT('0aaaaaaaaaaaaaaaaaaaP1',$,'P',$,$,$,$,$,$);\n" +
            "#2=IFCWALL('0aaaaaaaaaaaaaaaaaaaW1',$,'Wall A',$,$,#40,$,$,.SOLIDWALL.);\n" +
            "#3=IFCWALL('0aaaaaaaaaaaaaaaaaaaW2',$,'Wall B',$,$,#40,$,$,.SOLIDWALL.);\n" +
            "#4=IFCWALLTYPE('0aaaaaaaaaaaaaaaaaaaT1',$,'WT',$,$,(#12),$,$,$,.SOLIDWALL.);\n" +
            "#10=IFCPROPERTYSET('0aaaaaaaaaaaaaaaaaaaS1',$,'HZ_Delivery',$,(#11));\n" +
            "#11=IFCPROPERTYSINGLEVALUE('Code',$,IFCLABEL('A-01'),$);\n" +
            "#12=IFCPROPERTYSET('0aaaaaaaaaaaaaaaaaaaS2',$,'HZ_TypeData',$,(#13));\n" +
            "#13=IFCPROPERTYSINGLEVALUE('Maker',$,IFCLABEL('X'),$);\n" +
            "#14=IFCRELDEFINESBYPROPERTIES('0aaaaaaaaaaaaaaaaaaaR1',$,$,$,(#2),#10);\n" +
            "#15=IFCRELDEFINESBYTYPE('0aaaaaaaaaaaaaaaaaaaR2',$,$,$,(#2,#3),#4);\n" +
            "#40=IFCLOCALPLACEMENT($,#41);\n" +
            "#41=IFCAXIS2PLACEMENT3D(#42,$,$);\n" +
            "#42=IFCCARTESIANPOINT((1.,2.,0.));";

        private static IfcStepReader.Document Parse(string data, string schema = "IFC4")
        {
            string error;
            IfcStepReader.Document ifc = IfcStepReader.Parse(IfcText(data, schema), out error);
            Assert.Null(error);
            return ifc;
        }

        private static PsetMappingFile ParseMapping(string text)
        {
            string error;
            PsetMappingFile file = PsetMapping.Parse(text, out error);
            Assert.True(file != null, error);
            return file;
        }

        // =====================================================================
        // The mapping file
        // =====================================================================

        [Fact]
        public void The_exporter_format_parses_into_sets_levels_classes_and_properties()
        {
            PsetMappingFile file = ParseMapping("﻿" + Mapping.Replace("\n", "\r\n") +
                                                "PropertySet:\tHZ_Mixed\tinstance\tIfcSlab, IfcBeam\n\tFire\tLabel\tFR\n");
            Assert.Equal(3, file.Sets.Count);
            Assert.Equal('I', file.Sets[0].Level);
            Assert.Equal('T', file.Sets[1].Level);
            Assert.Equal(new[] { "IfcSlab", "IfcBeam" }, file.Sets[2].Entities);
            Assert.Equal("HZ_Code", file.Sets[0].Properties[0].RevitParameter);
            Assert.Null(file.Sets[1].Properties[0].RevitParameter);
            Assert.Equal(3, file.PropertyCount);
            Assert.Empty(file.Warnings);
        }

        [Fact]
        public void A_line_written_with_spaces_is_refused_by_line_number()
        {
            // Revit's exporter splits on TAB: this file would export with NO set and no complaint.
            string error;
            Assert.Null(PsetMapping.Parse("# c\nPropertySet: HZ_Delivery I IfcWall\n\tCode\tText\n", out error));
            Assert.Contains("line 2", error);
            Assert.Contains("TAB", error);

            Assert.Null(PsetMapping.Parse("PropertySet:\tHZ\tI\tIfcWall\n    Code Text\n", out error));
            Assert.Contains("line 2", error);
        }

        [Theory]
        [InlineData("\tCode\tText\n", "before any PropertySet")]
        [InlineData("PropertySet:\tA\tX\tIfcWall\n\tCode\tText\n", "I (instance) or T (type)")]
        [InlineData("PropertySet:\tA\tI\tWall\n\tCode\tText\n", "not an IFC class")]
        [InlineData("PropertySet:\tA\tI\tIfcWall\n\tCode\tText\nPropertySet:\tA\tT\tIfcSlab\n\tX\tText\n", "defined twice")]
        [InlineData("PropertySet:\tA\tI\tIfcWall\n\tCode\tText\n\tCode\tLabel\n", "appears twice")]
        [InlineData("# only comments\n\n", "defines no PropertySet")]
        public void Malformed_mappings_are_refused_with_the_reason(string text, string expected)
        {
            string error;
            Assert.Null(PsetMapping.Parse(text, out error));
            Assert.Contains(expected, error);
        }

        [Fact]
        public void An_unfamiliar_data_type_and_an_empty_set_are_warnings_not_refusals()
        {
            PsetMappingFile file = ParseMapping("PropertySet:\tA\tI\tIfcWall\n\tCode\tFancyMeasure\nPropertySet:\tB\tI\tIfcSlab\n");
            Assert.Equal(2, file.Warnings.Count);
            Assert.Contains(file.Warnings, w => w.Contains("FancyMeasure"));
            Assert.Contains(file.Warnings, w => w.Contains("'B'"));
        }

        // =====================================================================
        // The mapping, looked for in the exported file
        // =====================================================================

        [Fact]
        public void Coverage_counts_entities_carrying_each_property_and_names_the_missing_by_GlobalId()
        {
            List<PsetMapping.Row> rows;
            DeliveryGate gate = PsetMapping.Verify(ParseMapping(Mapping), Parse(TwoWalls), 1.0, 10, out rows);

            PsetMapping.Row code = rows.Single(r => r.Property == "Code");
            Assert.Equal(2, code.Expected);
            Assert.Equal(1, code.Carrying);
            Assert.Equal(DeliveryGateStatus.Failed, code.Status);
            Assert.Equal("0aaaaaaaaaaaaaaaaaaaW2", code.MissingExamples.Single().Value<string>("global_id"));

            // The type set reaches both occurrences through IfcRelDefinesByType.
            PsetMapping.Row maker = rows.Single(r => r.Property == "Maker");
            Assert.Equal(2, maker.Carrying);
            Assert.Equal(DeliveryGateStatus.Passed, maker.Status);

            Assert.Equal(DeliveryGateStatus.Failed, gate.Status);
            Assert.Equal(3, (int)gate.Evidence["coverage_total"]["carrying"]);
            Assert.Equal(4, (int)gate.Evidence["coverage_total"]["expected"]);
        }

        [Fact]
        public void A_relaxed_minimum_coverage_lets_a_partly_filled_property_pass()
        {
            List<PsetMapping.Row> rows;
            DeliveryGate gate = PsetMapping.Verify(ParseMapping(Mapping), Parse(TwoWalls), 0.5, 10, out rows);
            Assert.Equal(DeliveryGateStatus.Passed, gate.Status);
        }

        [Fact]
        public void A_type_class_is_checked_on_its_own_property_sets()
        {
            List<PsetMapping.Row> rows;
            DeliveryGate gate = PsetMapping.Verify(
                ParseMapping("PropertySet:\tHZ_TypeData\tT\tIfcWallType\n\tMaker\tLabel\n"), Parse(TwoWalls), 1.0, 10, out rows);
            Assert.Equal(1, rows.Single().Expected);
            Assert.Equal(1, rows.Single().Carrying);
            Assert.Equal(DeliveryGateStatus.Passed, gate.Status);
        }

        [Fact]
        public void The_IFC2x3_standard_case_counts_as_its_class()
        {
            IfcStepReader.Document ifc = Parse(TwoWalls.Replace("IFCWALL(", "IFCWALLSTANDARDCASE("), "IFC2X3");
            List<PsetMapping.Row> rows;
            PsetMapping.Verify(ParseMapping(Mapping), ifc, 1.0, 10, out rows);
            Assert.Equal(2, rows.Single(r => r.Property == "Maker").Expected);
        }

        [Fact]
        public void Classes_absent_from_the_file_make_the_gate_not_decidable_never_passed()
        {
            List<PsetMapping.Row> rows;
            DeliveryGate gate = PsetMapping.Verify(ParseMapping("PropertySet:\tA\tI\tIfcDoor\n\tCode\tText\n"),
                                                   Parse(TwoWalls), 1.0, 10, out rows);
            Assert.Equal("no_entities", rows.Single().Status);
            Assert.Equal(DeliveryGateStatus.NotDecidable, gate.Status);
        }

        // =====================================================================
        // Header and trailer
        // =====================================================================

        [Theory]
        [InlineData("IFC4", "IFC4", true, DeliveryGateStatus.Passed)]
        [InlineData("IFC4", "IFC4RV", true, DeliveryGateStatus.Passed)]
        [InlineData("IFC2X3", "IFC2x3CV2", true, DeliveryGateStatus.Passed)]
        [InlineData("IFC4X3_ADD2", "IFC4x3", true, DeliveryGateStatus.Passed)]
        [InlineData("IFC4X3_ADD2", "IFC4", true, DeliveryGateStatus.Failed)]   // IFC4X3 is NOT IFC4
        [InlineData("IFC2X3", "IFC4", true, DeliveryGateStatus.Failed)]
        [InlineData("IFC4", "IFC4", false, DeliveryGateStatus.Failed)]          // truncated
        public void The_header_proves_the_schema_family_and_the_trailer_proves_completeness(
            string fileSchema, string requested, bool trailer, string expected)
        {
            string text = IfcText("#1=IFCWALL('0aaaaaaaaaaaaaaaaaaaW1',$,'W',$,$,$,$,$,$);", fileSchema, trailer);
            string tail = text.Substring(Math.Max(0, text.Length - 64));
            DeliveryGate gate = IfcDeliveryRules.CheckHeader(text, tail, requested);
            Assert.Equal(expected, gate.Status);
            Assert.Equal(trailer, (bool)gate.Evidence["end_trailer_found"]);
        }

        [Fact]
        public void A_file_that_is_not_STEP_fails_the_header()
        {
            DeliveryGate gate = IfcDeliveryRules.CheckHeader("<ifcXML/>", "", "IFC4");
            Assert.Equal(DeliveryGateStatus.Failed, gate.Status);
            Assert.Contains("ISO-10303-21", gate.Reason);
        }

        [Fact]
        public void Every_offered_version_maps_to_a_family_the_header_check_can_prove()
        {
            foreach (KeyValuePair<string, string> pair in IfcDeliveryRules.VersionSchemaFamily)
                Assert.Equal(pair.Value, IfcDeliveryRules.SchemaFamily(pair.Value));
        }

        // =====================================================================
        // Gates and readiness
        // =====================================================================

        private static DeliveryGate G(string name, string status, bool requested = true, bool advisory = false) =>
            new DeliveryGate { Name = name, Status = status, Requested = requested, Advisory = advisory };

        [Fact]
        public void Ready_needs_every_requested_non_advisory_gate_passed()
        {
            List<string> blocking;
            Assert.True(IfcDeliveryRules.DeliverableReady(new[]
            {
                G("precheck", DeliveryGateStatus.Failed, advisory: true),   // the model's verdict never counts
                G("export", DeliveryGateStatus.Passed), G("schema_header", DeliveryGateStatus.Passed),
                G("ids_validate", DeliveryGateStatus.Passed), G("pset_mapping", DeliveryGateStatus.Passed),
                G("bcf", DeliveryGateStatus.Skipped)                          // nothing to report is not a failure
            }, out blocking), string.Join(";", blocking));

            Assert.False(IfcDeliveryRules.DeliverableReady(new[]
            {
                G("export", DeliveryGateStatus.Passed), G("schema_header", DeliveryGateStatus.Passed),
                G("ids_validate", DeliveryGateStatus.NotDecidable)
            }, out blocking));
            Assert.Contains("ids_validate is not_decidable", blocking);

            Assert.False(IfcDeliveryRules.DeliverableReady(new[] { G("schema_header", DeliveryGateStatus.Passed) }, out blocking));
            Assert.Contains("export did not pass", blocking);
        }

        [Fact]
        public void The_gate_report_is_in_fixed_order_and_names_unrequested_gates()
        {
            JArray json = IfcDeliveryRules.GatesJson(new[] { G("export", DeliveryGateStatus.Passed) });
            Assert.Equal(IfcDeliveryRules.GateOrder, json.Select(g => g.Value<string>("gate")).ToArray());
            Assert.False(json.Single(g => g.Value<string>("gate") == "bcf").Value<bool>("requested"));
        }

        [Fact]
        public void The_IDS_gate_never_counts_an_undecided_specification_as_passed()
        {
            var report = new IdsReport();
            report.Results.Add(new IdsSpecificationResult { Name = "a", Passing = 3 });
            report.Results.Add(new IdsSpecificationResult { Name = "b", NotDecidable = 1 });
            Assert.Equal(DeliveryGateStatus.NotDecidable, IfcDeliveryRules.IdsGate(report, null).Status);

            report.Results.Add(new IdsSpecificationResult { Name = "c", Failing = 1 });
            Assert.Equal(DeliveryGateStatus.Failed, IfcDeliveryRules.IdsGate(report, null).Status);

            Assert.Equal(DeliveryGateStatus.NotDecidable, IfcDeliveryRules.IdsGate(null, "unreadable").Status);
        }

        [Fact]
        public void An_IDS_validated_on_the_file_passes_the_gate_end_to_end()
        {
            var doc = new XmlDocument();
            doc.LoadXml(
                "<ids xmlns=\"http://standards.buildingsmart.org/IDS\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">" +
                "<info><title>t</title></info><specifications>" +
                "<specification name=\"walls carry a code\" ifcVersion=\"IFC4\">" +
                "<applicability><entity><name><simpleValue>IFCWALL</simpleValue></name></entity></applicability>" +
                "<requirements><property><propertySet><simpleValue>HZ_Delivery</simpleValue></propertySet>" +
                "<baseName><simpleValue>Code</simpleValue></baseName></property></requirements>" +
                "</specification></specifications></ids>");
            string error;
            IdsFile ids = IdsReader.Parse(doc, out error);
            Assert.Null(error);
            IdsReport report = IdsRun.Validate(ids, Parse(TwoWalls), "t.ids");
            DeliveryGate gate = IfcDeliveryRules.IdsGate(report, null);
            Assert.Equal(DeliveryGateStatus.Failed, gate.Status);   // wall B has no HZ_Delivery.Code
        }

        // =====================================================================
        // The output name
        // =====================================================================

        private static JObject Container(string number = "0001") => JObject.Parse(
            "{ \"fields\": { \"project\": \"HZ01\", \"originator\": \"HRZ\", \"volume\": \"ZZ\", \"level\": \"XX\", " +
            "\"type\": \"M3\", \"role\": \"A\", \"number\": \"" + number + "\" }, " +
            "\"field_order\": [\"project\",\"originator\",\"volume\",\"level\",\"type\",\"role\",\"number\"], " +
            "\"field_patterns\": { \"number\": \"[0-9]{4,6}\" } }");

        [Fact]
        public void The_container_name_is_the_fields_in_order_joined_by_the_separator()
        {
            string composed, error;
            Assert.Equal("HZ01-HRZ-ZZ-XX-M3-A-0001", IfcDeliveryRules.ResolveStem(null, Container(), out composed, out error));
            Assert.Equal("HZ01-HRZ-ZZ-XX-M3-A-0001", IfcDeliveryRules.ResolveStem("HZ01-HRZ-ZZ-XX-M3-A-0001.ifc", Container(), out composed, out error));
        }

        [Fact]
        public void Two_names_that_disagree_or_a_field_that_misses_its_pattern_refuse()
        {
            string composed, error;
            Assert.Null(IfcDeliveryRules.ResolveStem("other", Container(), out composed, out error));
            Assert.Contains("disagree", error);

            Assert.Null(IfcDeliveryRules.ResolveStem(null, Container("12"), out composed, out error));
            Assert.Contains("does not match", error);

            JObject noOrder = Container(); noOrder.Remove("field_order");
            Assert.Null(IfcDeliveryRules.ResolveStem(null, noOrder, out composed, out error));
            Assert.Contains("field_order", error);

            JObject withSeparator = Container(); withSeparator["fields"]["volume"] = "Z-Z";
            Assert.Null(IfcDeliveryRules.ResolveStem(null, withSeparator, out composed, out error));
            Assert.Contains("separator", error);

            Assert.Null(IfcDeliveryRules.ResolveStem(null, null, out composed, out error));
            Assert.Contains("will not invent", error);

            Assert.Null(IfcDeliveryRules.ResolveStem("..\\escape", null, out composed, out error));
        }

        // =====================================================================
        // BCF of the IDS failures
        // =====================================================================

        private static IdsReport FailingReport()
        {
            var report = new IdsReport();
            var failed = new IdsSpecificationResult { Name = "walls carry a code", Identifier = "S1", Applicable = 2, Failing = 1, Passing = 1 };
            failed.Findings.Add(new IdsElementFinding
            {
                Outcome = IdsOutcome.Fail, Facet = "property", EntityKey = "#3", GlobalId = "0aaaaaaaaaaaaaaaaaaaW2",
                IfcClass = "IFCWALL", Reason = "no property set matching HZ_Delivery is attached"
            });
            report.Results.Add(failed);
            report.Results.Add(new IdsSpecificationResult { Name = "passes", Applicable = 2, Passing = 2 });
            return report;
        }

        [Fact]
        public void One_topic_per_failed_specification_with_its_GlobalIds_and_an_aimed_camera()
        {
            List<IdsBcfTopic> topics = IdsBcf.Topics(FailingReport(), Parse(TwoWalls), "d.ifc", "abc", 1000);
            IdsBcfTopic topic = topics.Single();
            Assert.Equal(new[] { "0aaaaaaaaaaaaaaaaaaaW2" }, topic.GlobalIds);
            Assert.True(topic.CameraAimed);
            Assert.Equal(IdsBcf.TopicGuid("abc", "walls carry a code", "S1"), topic.Guid);   // deterministic
            Assert.NotEqual(IdsBcf.TopicGuid("abd", "walls carry a code", "S1"), topic.Guid);
        }

        [Fact]
        public void The_BCF_is_written_and_re_read_structurally()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-deliver-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "d.ids-issues.bcf");
                List<IdsBcfTopic> topics = IdsBcf.Topics(FailingReport(), Parse(TwoWalls), "d.ifc", "abc", 1000);
                string error;
                JObject evidence = IdsBcf.WriteAndVerify(path, topics, "d.ifc", "0aaaaaaaaaaaaaaaaaaaP1",
                                                         new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc), out error);
                Assert.True(evidence != null, error);
                Assert.Equal(1, (int)evidence["topics"]);
                Assert.Equal(1, (int)evidence["components_with_ifc_guid"]);
                Assert.Equal(64, evidence.Value<string>("sha256").Length);

                using (ZipArchive zip = ZipFile.OpenRead(path))
                {
                    Assert.NotNull(zip.GetEntry("bcf.version"));
                    ZipArchiveEntry markup = zip.GetEntry(topics[0].Guid + "/markup.bcf");
                    var xml = new XmlDocument();
                    using (Stream s = markup.Open()) xml.Load(s);
                    Assert.Equal("d.ifc", xml.SelectSingleNode("/Markup/Header/File/Filename").InnerText);
                    Assert.StartsWith("IDS: ", xml.SelectSingleNode("/Markup/Topic/Title").InnerText);
                    Assert.NotNull(xml.SelectSingleNode("/Markup/Topic/CreationAuthor"));
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        // =====================================================================
        // What the file says about where it sits
        // =====================================================================

        [Fact]
        public void The_file_georeference_is_read_back_observed_not_judged()
        {
            IfcStepReader.Document ifc = Parse(
                "#1=IFCSITE('0aaaaaaaaaaaaaaaaaaaS1',$,'Site',$,$,#40,$,$,.ELEMENT.,(4,36,0,0),(-74,-4,0,0),2600.,$,$);\n" +
                "#2=IFCPROJECTEDCRS('EPSG:9377',$,$,$,$,$,$);\n" +
                "#3=IFCMAPCONVERSION(#9,#2,4900000.,2000000.,2600.,1.,0.,$);\n" +
                "#40=IFCLOCALPLACEMENT($,#41);\n#41=IFCAXIS2PLACEMENT3D(#42,$,$);\n#42=IFCCARTESIANPOINT((0.,0.,0.));");
            JObject geo = IfcGeoreferenceReadback.Read(ifc);
            Assert.Equal("EPSG:9377", geo["map_conversions"][0].Value<string>("target_crs"));
            Assert.Equal(4900000.0, geo["map_conversions"][0].Value<double>("eastings"));
            Assert.Equal(4.0, geo["sites"][0]["ref_latitude"][0].Value<double>());
            Assert.NotNull(geo["sites"][0]["placement_origin_mm"] as JArray);
        }
    }
}
