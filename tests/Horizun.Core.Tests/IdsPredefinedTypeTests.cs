// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// PredefinedType IS READ WHERE THE SCHEMA PUTS IT (backlog P0 #10, 2026-10-10). The
// evaluator took the first enumeration among an entity's last three attributes. For an
// IFC4 IfcDoor the tail is PredefinedType, OperationType, UserDefinedOperationType, so
// it read OperationType: an IDS asking for PredefinedType=DOOR failed a door that is a
// DOOR, "is 'SINGLE_SWING_LEFT'". IdsExpress already knows the chain; it is used first.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IdsPredefinedTypeTests
    {
        private static IdsIfcEvaluator.Index Index(string data, string schema = "IFC4")
        {
            string error;
            IfcStepReader.Document ifc = IfcStepReader.Parse(
                "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('" + schema + "'));\nENDSEC;\nDATA;\n" + data + "\nENDSEC;\nEND-ISO-10303-21;", out error);
            Assert.Null(error);
            return IdsIfcEvaluator.Build(ifc);
        }

        [Fact]
        public void An_ifc4_door_reads_its_predefined_type_not_its_operation_type()
        {
            IdsIfcEvaluator.Index idx = Index("#10=IFCDOOR('0aaaaaaaaaaaaaaaaaaaa1',$,'D1',$,$,$,$,$,2100.,900.,.DOOR.,.SINGLE_SWING_LEFT.,$);");
            Assert.Equal("DOOR", IdsIfcEvaluator.PredefinedTypeOf(idx, idx.Ifc.ById[10]));
        }

        [Fact]
        public void An_ifc4_window_reads_its_predefined_type_not_its_partitioning_type()
        {
            IdsIfcEvaluator.Index idx = Index("#11=IFCWINDOW('0aaaaaaaaaaaaaaaaaaaa2',$,'W1',$,$,$,$,$,1200.,1000.,.WINDOW.,.SINGLE_PANEL.,$);");
            Assert.Equal("WINDOW", IdsIfcEvaluator.PredefinedTypeOf(idx, idx.Ifc.ById[11]));
        }

        [Fact]
        public void A_wall_still_reads_its_last_attribute()
        {
            IdsIfcEvaluator.Index idx = Index("#12=IFCWALL('0aaaaaaaaaaaaaaaaaaaa3',$,'W',$,$,$,$,$,.SOLIDWALL.);");
            Assert.Equal("SOLIDWALL", IdsIfcEvaluator.PredefinedTypeOf(idx, idx.Ifc.ById[12]));
        }

        [Fact]
        public void A_door_whose_predefined_type_is_unset_says_so_rather_than_reading_the_next_one()
        {
            IdsIfcEvaluator.Index idx = Index("#13=IFCDOOR('0aaaaaaaaaaaaaaaaaaaa4',$,'D2',$,$,$,$,$,2100.,900.,$,.SINGLE_SWING_LEFT.,$);");
            Assert.Null(IdsIfcEvaluator.PredefinedTypeOf(idx, idx.Ifc.ById[13]));
        }
    }
}
