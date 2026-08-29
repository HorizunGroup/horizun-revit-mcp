// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The type catalog. A row Revit cannot parse fails silently in somebody
// else's session, so the format is proved here character by character.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TypeCatalogRulesTests
    {
        private static CatalogColumn Col(string name, string type) => new CatalogColumn { Name = name, DataType = type };

        private static KeyValuePair<string, IDictionary<string, string>> Type(string name, params (string, string)[] values)
        {
            IDictionary<string, string> map = new Dictionary<string, string>();
            foreach ((string parameter, string value) in values) map[parameter] = value;
            return new KeyValuePair<string, IDictionary<string, string>>(name, map);
        }

        [Fact]
        public void The_header_spells_units_and_the_rows_follow_column_order()
        {
            string error = TypeCatalogRules.Build(
                new[] { Col("Ancho", "length"), Col("Nota", "text") }, null, null,
                new[] { Type("T-600", ("Ancho", "600"), ("Nota", "std")), Type("T-900", ("Ancho", "900")) },
                out string content, out _);
            Assert.Null(error);
            string[] lines = content.Split(new[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(",Ancho##LENGTH##MILLIMETERS,Nota##OTHER##", lines[0]);
            Assert.Equal("T-600,600,std", lines[1]);
            Assert.Equal("T-900,900,", lines[2]);   // missing value = empty cell = keep the type's own
        }

        [Fact]
        public void Formula_and_material_parameters_are_excluded_by_name()
        {
            string error = TypeCatalogRules.Build(
                new[] { Col("Ancho", "length"), Col("Area", "area"), Col("Acabado", "material") },
                new[] { "Area" }, new[] { "Acabado" },
                new[] { Type("T", ("Ancho", "600")) },
                out string content, out List<string> excluded);
            Assert.Null(error);
            Assert.DoesNotContain("Area##", content);
            Assert.DoesNotContain("Acabado", content);
            Assert.Equal(2, excluded.Count);
            Assert.Contains(excluded, e => e.Contains("formula-driven"));
            Assert.Contains(excluded, e => e.Contains("ElementId"));
        }

        [Fact]
        public void Zero_types_is_not_a_deliverable()
        {
            string error = TypeCatalogRules.Build(new[] { Col("A", "length") }, null, null,
                new KeyValuePair<string, IDictionary<string, string>>[0], out _, out _);
            Assert.NotNull(error);
            Assert.Contains("zero types", error);
        }

        [Fact]
        public void No_surviving_column_refuses_with_the_exclusion_count()
        {
            string error = TypeCatalogRules.Build(new[] { Col("M", "material") }, null, null,
                new[] { Type("T") }, out _, out List<string> excluded);
            Assert.NotNull(error);
            Assert.Contains("no parameter survived", error);
            Assert.Single(excluded);
        }

        [Fact]
        public void A_type_name_with_a_comma_stays_one_cell()
        {
            TypeCatalogRules.Build(new[] { Col("A", "text") }, null, null,
                new[] { Type("Tee 3\" x 1,5\"", ("A", "x")) }, out string content, out _);
            Assert.Contains("\"Tee 3\"\" x 1,5\"\"\",x", content);
        }

        [Fact]
        public void Value_cells_are_culture_fixed_and_typed()
        {
            Assert.Equal("1", TypeCatalogRules.ValueCell("yesno", true));
            Assert.Equal("0", TypeCatalogRules.ValueCell("yesno", false));
            Assert.Equal("600.5", TypeCatalogRules.ValueCell("length", 600.5));
            Assert.Equal("42", TypeCatalogRules.ValueCell("integer", 42L));
            Assert.Equal("", TypeCatalogRules.ValueCell("text", null));
        }
    }
}
