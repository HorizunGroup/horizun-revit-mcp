// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A MEASURE IS A NUMBER, NOT ITS STEP SPELLING (backlog P0 #11, 2026-10-10). Unwrap
// asked IfcStepReader.Text before Number, and Text returns an unquoted token as written,
// so IFCLENGTHMEASURE(2100.) was indexed as the text "2100." and an IDS simpleValue of
// 2100 (or 2100.0, or an enumeration of numbers) failed on a string compare. Ranges kept
// working, which is why nobody saw it. A quoted text is still text.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IdsNumericValueTests
    {
        private static IdsIfcEvaluator.Index Index(string value)
        {
            string error;
            IfcStepReader.Document ifc = IfcStepReader.Parse(
                "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n" +
                "#1=IFCWALL('0aaaaaaaaaaaaaaaaaaaa1',$,'W1',$,$,$,$,$,$);\n" +
                "#2=IFCPROPERTYSINGLEVALUE('Height',$," + value + ",$);\n" +
                "#3=IFCPROPERTYSET('0bbbbbbbbbbbbbbbbbbbb1',$,'Pset_Test',$,(#2));\n" +
                "#4=IFCRELDEFINESBYPROPERTIES('0cccccccccccccccccccc1',$,$,$,(#1),#3);\n" +
                "ENDSEC;\nEND-ISO-10303-21;", out error);
            Assert.Null(error);
            return IdsIfcEvaluator.Build(ifc);
        }

        private static string Height(IdsIfcEvaluator.Index idx) =>
            idx.Properties[1].Single(p => p.Item1 == "Pset_Test" && p.Item2 == "Height").Item3;

        [Fact]
        public void A_length_measure_with_a_trailing_point_reads_as_the_number()
        {
            Assert.Equal("2100", Height(Index("IFCLENGTHMEASURE(2100.)")));
        }

        [Fact]
        public void A_measure_with_an_exponent_reads_as_the_number()
        {
            Assert.Equal("2100", Height(Index("IFCLENGTHMEASURE(2.1E3)")));
        }

        [Fact]
        public void A_quoted_label_is_still_text()
        {
            Assert.Equal("2100.", Height(Index("IFCLABEL('2100.')")));
        }
    }
}
