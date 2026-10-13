// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE DATA SECTION IS A KEYWORD, NOT A SUBSTRING (backlog P0 #9, 2026-10-10). Parse
// looked for the first "DATA" anywhere in the text, ignoring case, so any header that
// contained the word - the file name in FILE_NAME ('Datacenter.ifc'), an author, an
// originating system - cut the header before FILE_SCHEMA: the schema came back null,
// and with it ifc_schema in plan_from_ifc / validate_ids and the IDS ifcVersion check.
// Checked first against 24 IFC files the bridge exported from Revit 2024-2026 (all
// 'ODA SDAI'): none has the word in its header, so a Revit export trips it only through
// its own file name - which is enough.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IfcHeaderSectionTests
    {
        private const string Step =
            "ISO-10303-21;\n" +
            "HEADER;\n" +
            "FILE_DESCRIPTION(('ViewDefinition [CoordinationView_V2.0]'),'2;1');\n" +
            "FILE_NAME('Datacenter-Level-2.ifc','2026-10-10T11:14:41-05:00',('Data team'),(''),'The EXPRESS Data Manager Version 5','Autodesk Revit','');\n" +
            "FILE_SCHEMA(('IFC4'));\n" +
            "ENDSEC;\n" +
            "\n" +
            "DATA;\n" +
            "#1=IFCWALL('0abc',$,'W-1',$,$,$,$,$,$);\n" +
            "ENDSEC;\n" +
            "END-ISO-10303-21;\n";

        [Fact]
        public void A_header_that_contains_the_word_data_still_yields_its_schema()
        {
            string error;
            IfcStepReader.Document doc = IfcStepReader.Parse(Step, out error);
            Assert.Null(error);
            Assert.Equal("IFC4", doc.SchemaIdentifier);
            Assert.True(doc.ById.ContainsKey(1), "the entity after DATA; was not read");
        }

        [Fact]
        public void A_file_with_no_data_section_is_still_refused()
        {
            string error;
            Assert.Null(IfcStepReader.Parse("ISO-10303-21;\nHEADER;\nFILE_NAME('Data.ifc');\nENDSEC;\n", out error));
            Assert.NotNull(error);
        }
    }
}
