// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// Which DWG layer-table row a horizun_export dwg_layers request names. MEASURED on
// Revit 2026: an empty table refused 'Walls' as if the category did not exist, and
// a real table holds Walls five times (one row per SpecialType). What must hold:
// an empty table says EMPTY; the Default row wins when no special is named; a
// named special picks exactly that row; the BuiltInCategory token resolved to a
// Spanish name matches a Spanish table; nothing ambiguous is ever picked.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DwgLayerRowsTests
    {
        private static DwgLayerRow R(string cat, string sub = "", string special = "Default", string layer = "L") =>
            new DwgLayerRow { Category = cat, SubCategory = sub, Special = special, Layer = layer, Color = 7 };

        private static List<DwgLayerRow> Walls(string name = "Walls") => new List<DwgLayerRow>
        {
            R("Doors"),
            R(name, special: "RetainingWall"), R(name, special: "FoundationWall"), R(name, special: "ExteriorWall"),
            R(name, special: "InteriorWall"), R(name, special: "Default", layer: "A-WALL"),
            R(name, "Common Edges"),
        };

        [Fact]
        public void An_empty_table_is_refused_as_empty_not_as_a_missing_category()
        {
            int i = DwgLayerRows.Find(new List<DwgLayerRow>(), new[] { "Walls", "OST_Walls" }, "", null, out string error);
            Assert.Equal(-1, i);
            Assert.Contains("EMPTY", error);
            Assert.Contains("layer_standard", error);
        }

        [Fact]
        public void Without_special_the_Default_row_is_chosen_among_five()
        {
            var rows = Walls();
            int i = DwgLayerRows.Find(rows, new[] { "Walls", "OST_Walls" }, "", null, out string error);
            Assert.Null(error);
            Assert.Equal("A-WALL", rows[i].Layer);
            Assert.Equal("Walls", rows[i].Label);
        }

        [Fact]
        public void A_named_special_picks_exactly_that_row()
        {
            var rows = Walls();
            int i = DwgLayerRows.Find(rows, new[] { "Walls" }, "", "exteriorwall", out string error);
            Assert.Null(error);
            Assert.Equal("ExteriorWall", rows[i].Special);
            Assert.Equal("Walls [ExteriorWall]", rows[i].Label);
        }

        [Fact]
        public void A_special_the_category_lacks_is_refused_naming_what_exists()
        {
            int i = DwgLayerRows.Find(new List<DwgLayerRow> { R("Doors") }, new[] { "Doors" }, "", "ExteriorWall", out string error);
            Assert.Equal(-1, i);
            Assert.Contains("Default", error);
        }

        [Fact]
        public void The_token_resolved_to_the_documents_language_matches_a_Spanish_table()
        {
            // The caller resolves OST_Walls to the name THIS Revit gives it ('Muros' on a
            // Spanish 2023) and passes the token too; the English name never enters.
            var rows = Walls("Muros");
            int i = DwgLayerRows.Find(rows, new[] { "Muros", "OST_Walls" }, "", null, out string error);
            Assert.Null(error);
            Assert.Equal("Muros", rows[i].Category);

            int miss = DwgLayerRows.Find(rows, new[] { "Walls" }, "", null, out string why);
            Assert.Equal(-1, miss);
            Assert.Contains("OST_Walls", why);
        }

        [Fact]
        public void A_subcategory_row_is_matched_by_its_subcategory()
        {
            var rows = Walls();
            int i = DwgLayerRows.Find(rows, new[] { "walls" }, "common edges", null, out string error);
            Assert.Null(error);
            Assert.Equal("Walls / Common Edges", rows[i].Label);
        }

        [Fact]
        public void Several_non_default_rows_are_never_guessed()
        {
            var rows = new List<DwgLayerRow> { R("Walls", special: "ExteriorWall"), R("Walls", special: "InteriorWall") };
            int i = DwgLayerRows.Find(rows, new[] { "Walls" }, "", null, out string error);
            Assert.Equal(-1, i);
            Assert.Contains("ExteriorWall", error);
            Assert.Contains("InteriorWall", error);
        }

        [Fact]
        public void SameKey_treats_an_empty_special_as_Default_and_ignores_name_case()
        {
            var row = R("Walls");
            Assert.True(DwgLayerRows.SameKey(row, "WALLS", "", "Default"));
            Assert.True(DwgLayerRows.SameKey(row, "Walls", null, ""));
            Assert.False(DwgLayerRows.SameKey(row, "Walls", "", "ExteriorWall"));
            Assert.False(DwgLayerRows.SameKey(R("Walls", special: "ExteriorWall"), "Walls", "", "Default"));
        }

        [Fact]
        public void RowText_is_culture_invariant_and_stable()
        {
            Assert.Equal("layer=A;color=3;cut_layer=B;cut_color=250", DwgLayerRows.RowText("A", 3, "B", 250));
        }
    }
}
