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
        public void The_container_sidecar_is_a_reported_gate_that_blocks_readiness_when_it_fails()
        {
            // Measured live 2026-09-24: the sidecar was written and verified but the gate was
            // missing from the report, because GateOrder did not name it.
            var passed = new[] { G("export", DeliveryGateStatus.Passed), G("schema_header", DeliveryGateStatus.Passed),
                                 G("information_container", DeliveryGateStatus.Passed) };
            JArray json = IfcDeliveryRules.GatesJson(passed);
            Assert.Equal("passed", json.Single(g => g.Value<string>("gate") == "information_container").Value<string>("status"));
            List<string> blocking;
            Assert.True(IfcDeliveryRules.DeliverableReady(passed, out blocking));

            var failed = new[] { G("export", DeliveryGateStatus.Passed), G("schema_header", DeliveryGateStatus.Passed),
                                 G("information_container", DeliveryGateStatus.Failed) };
            Assert.False(IfcDeliveryRules.DeliverableReady(failed, out blocking));
            Assert.Contains("information_container is failed", blocking);
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
            "\"field_patterns\": { \"number\": \"[0-9]{4,6}\" }, \"status\": \"S2\", \"revision\": \"P01\" }");

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

            // The seven ISO 19650-2 fields may omit field_order (the standard fixes it); any
            // other field set may not, because a JSON object's key order is not a convention.
            JObject noOrder = Container(); noOrder.Remove("field_order");
            Assert.Equal("HZ01-HRZ-ZZ-XX-M3-A-0001", IfcDeliveryRules.ResolveStem(null, noOrder, out composed, out error));
            JObject custom = Container(); custom.Remove("field_order"); custom["fields"]["zone"] = "N1";
            Assert.Null(IfcDeliveryRules.ResolveStem(null, custom, out composed, out error));
            Assert.Contains("field_order", error);

            JObject noStatus = Container(); noStatus.Remove("status");
            Assert.Null(IfcDeliveryRules.ResolveStem(null, noStatus, out composed, out error));
            Assert.Contains("status", error);

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

        

        

        
    }
}
