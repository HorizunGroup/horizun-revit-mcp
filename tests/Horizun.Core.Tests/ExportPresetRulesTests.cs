// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Export presets: a typo refuses instead of exporting defaults under the
// preset's name, the hash is canonical, and the file-side proofs read real
// headers.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExportPresetRulesTests
    {
        private static ExportPreset Parse(string format, params (string k, string v)[] options)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach ((string k, string v) in options) list.Add(new KeyValuePair<string, string>(k, v));
            ExportPreset preset = ExportPresetRules.Parse("p1", format, 1, null, list, out string reason);
            Assert.True(preset != null, reason);
            return preset;
        }

        [Fact]
        public void An_unknown_option_refuses_naming_the_known_ones()
        {
            ExportPreset preset = ExportPresetRules.Parse("p1", "ifc", 1, null,
                new[] { new KeyValuePair<string, string>("ifc_versio", "IFC4") }, out string reason);
            Assert.Null(preset);
            Assert.Contains("ifc_version", reason);
            Assert.Contains("defaults under this preset's name", reason);
        }

        [Fact]
        public void A_value_outside_the_closed_list_refuses()
        {
            ExportPreset preset = ExportPresetRules.Parse("p1", "dwg", 1, null,
                new[] { new KeyValuePair<string, string>("acad_version", "2007") }, out string reason);
            Assert.Null(preset);
            Assert.Contains("2013", reason);
        }

        [Fact]
        public void The_hash_is_canonical_and_moves_with_any_option()
        {
            string a = ExportPresetRules.Hash(Parse("ifc", ("ifc_version", "IFC4")));
            string b = ExportPresetRules.Hash(Parse("ifc", ("ifc_version", "IFC4")));
            string c = ExportPresetRules.Hash(Parse("ifc", ("ifc_version", "IFC2x3")));
            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void Verifiability_is_a_stated_fact_per_option()
        {
            Assert.True(ExportPresetRules.Verifiable("ifc", "ifc_version"));
            Assert.False(ExportPresetRules.Verifiable("nwc", "convert_element_properties"));
        }

        [Fact]
        public void The_ifc_schema_reads_from_a_real_header()
        {
            const string head = "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\n" +
                                "FILE_SCHEMA(('IFC4'));\nENDSEC;";
            Assert.Equal("IFC4", ExportPresetRules.IfcSchemaOf(head));
            Assert.Null(ExportPresetRules.IfcSchemaOf("no header here"));
        }

        [Fact]
        public void The_dwg_signature_maps_to_the_option_vocabulary()
        {
            Assert.Equal("2018", ExportPresetRules.DwgVersionOf(Encoding.ASCII.GetBytes("AC1032rest")));
            Assert.Equal("2013", ExportPresetRules.DwgVersionOf(Encoding.ASCII.GetBytes("AC1027rest")));
            Assert.StartsWith("unknown(", ExportPresetRules.DwgVersionOf(Encoding.ASCII.GetBytes("AC1015xx")));
        }

        [Fact]
        public void The_png_width_reads_from_ihdr()
        {
            var png = new byte[24];
            png[0] = 0x89; png[1] = 0x50; png[2] = 0x4E; png[3] = 0x47;
            png[16] = 0; png[17] = 0; png[18] = 0x02; png[19] = 0x00;   // width 512
            Assert.Equal(512, ExportPresetRules.PngWidthOf(png));
            Assert.Equal(-1, ExportPresetRules.PngWidthOf(new byte[] { 1, 2, 3 }));
        }

        [Fact]
        public void Pixel_size_is_bounded()
        {
            ExportPreset bad = ExportPresetRules.Parse("p1", "image", 1, null,
                new[] { new KeyValuePair<string, string>("pixel_size", "17") }, out string reason);
            Assert.Null(bad);
            Assert.Contains("64..8192", reason);
            Assert.NotNull(Parse("image", ("pixel_size", "512")));
        }
    }
}
