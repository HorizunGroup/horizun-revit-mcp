// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// examples/ifc-delivery ships an IDS 1.0 file and a property-set mapping that are
// meant to AGREE: the mapping exports HZ_Delivery.Code on IfcDuctSegment and the IDS
// requires exactly that property on exactly that class. An example pair that drifted
// apart would teach a reader a delivery that fails its own gate. So both files are
// read with the SAME readers horizun_deliver_ifc uses (PsetMapping, IdsReader) and
// run against two small hand-written IFC files: one duct that carries the code (every
// gate passes) and one that does not (the IDS fails and the mapping coverage drops).
//
// Server.Tests validates the JSON payloads against the contract; this file validates
// the two non-JSON files those payloads point at.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExampleDeliveryFilesTests
    {
        private static string ExampleFolder()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string folder = Path.Combine(d.FullName, "examples", "ifc-delivery");
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) && Directory.Exists(folder)) return folder;
                d = d.Parent;
            }
            throw new InvalidOperationException("examples/ifc-delivery not found from " + AppContext.BaseDirectory);
        }

        private static IfcStepReader.Document Ifc(string code)
        {
            string value = code == null ? "" :
                "#10=IFCPROPERTYSET('0aaaaaaaaaaaaaaaaaaaS1',$,'HZ_Delivery',$,(#11,#12));\n" +
                "#11=IFCPROPERTYSINGLEVALUE('Code',$,IFCLABEL('" + code + "'),$);\n" +
                "#12=IFCPROPERTYSINGLEVALUE('System',$,IFCLABEL('Supply Air'),$);\n" +
                "#13=IFCRELDEFINESBYPROPERTIES('0aaaaaaaaaaaaaaaaaaaR1',$,$,$,(#2),#10);\n";
            string text =
                "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [ReferenceView]'),'2;1');\n" +
                "FILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
                "#1=IFCPROJECT('0aaaaaaaaaaaaaaaaaaaP1',$,'P',$,$,$,$,$,$);\n" +
                "#2=IFCDUCTSEGMENT('0aaaaaaaaaaaaaaaaaaaD1',$,'Duct 1',$,$,#40,$,$,.RIGIDSEGMENT.);\n" +
                value +
                "#40=IFCLOCALPLACEMENT($,#41);\n#41=IFCAXIS2PLACEMENT3D(#42,$,$);\n#42=IFCCARTESIANPOINT((0.,0.,0.));\n" +
                "ENDSEC;\nEND-ISO-10303-21;\n";
            string error;
            IfcStepReader.Document ifc = IfcStepReader.Parse(text, out error);
            Assert.Null(error);
            return ifc;
        }

        private static PsetMappingFile Mapping()
        {
            string error;
            PsetMappingFile mapping = PsetMapping.Read(Path.Combine(ExampleFolder(), "hz-delivery-psets.txt"), out error);
            Assert.True(mapping != null, "the example mapping does not parse (a TAB became spaces?): " + error);
            Assert.Empty(mapping.Warnings);
            return mapping;
        }

        private static IdsFile Ids()
        {
            string error;
            IdsFile ids = IdsReader.Read(Path.Combine(ExampleFolder(), "hz-delivery.ids"), out error);
            Assert.True(ids != null, "the example IDS does not parse: " + error);
            return ids;
        }

        [Fact]
        public void The_example_mapping_declares_the_property_the_example_IDS_requires()
        {
            PsetMappingFile mapping = Mapping();
            PsetMappingSet set = Assert.Single(mapping.Sets, s => s.Name == "HZ_Delivery");
            Assert.Equal('I', set.Level);
            Assert.Contains("IfcDuctSegment", set.Entities);
            Assert.Contains(set.Properties, p => p.Name == "Code");
            Assert.Single(Ids().Specifications);
        }

        [Fact]
        public void A_duct_that_carries_its_code_passes_both_the_IDS_and_the_mapping()
        {
            IfcStepReader.Document ifc = Ifc("DCT-0001");
            IdsReport report = IdsRun.Validate(Ids(), ifc, "hz-delivery.ids");
            Assert.Equal(DeliveryGateStatus.Passed, IfcDeliveryRules.IdsGate(report, null).Status);

            DeliveryGate gate = PsetMapping.Verify(Mapping(), ifc, 1.0, 5, out var rows);
            Assert.Equal(DeliveryGateStatus.Passed, gate.Status);
            Assert.All(rows, r => Assert.Equal(1, r.Carrying));
        }

        [Fact]
        public void A_duct_without_its_code_or_with_a_malformed_one_fails_the_IDS()
        {
            Assert.Equal(DeliveryGateStatus.Failed,
                IfcDeliveryRules.IdsGate(IdsRun.Validate(Ids(), Ifc(null), "hz-delivery.ids"), null).Status);
            Assert.Equal(DeliveryGateStatus.Failed,
                IfcDeliveryRules.IdsGate(IdsRun.Validate(Ids(), Ifc("D-1"), "hz-delivery.ids"), null).Status);

            DeliveryGate gate = PsetMapping.Verify(Mapping(), Ifc(null), 1.0, 5, out var rows);
            Assert.Equal(DeliveryGateStatus.Failed, gate.Status);
            Assert.Contains(rows, r => r.Property == "Code" && r.Carrying == 0);
        }
    }
}
