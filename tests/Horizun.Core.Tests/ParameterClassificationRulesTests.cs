// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
// The Revit-free half of horizun_manage_parameters / horizun_query_classification.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ParameterClassificationRulesTests
    {
        private const string Spf =
            "# This is a Revit shared parameter file.\r\n# Do not edit manually.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n" +
            "*GROUP\tID\tNAME\r\nGROUP\t1\tIdentity\r\nGROUP\t2\tHorizun\r\n" +
            "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n" +
            "PARAM\t6f1c0a52-8e3d-4a51-9a0e-2b7a1f0c1d11\tHZ_Code\tTEXT\t\t2\t1\t\t1\t0\r\n" +
            "PARAM\t0b9f7e2c-3a41-4c8e-bb6d-5d2e9a7c4f22\tHZ_Width\tLENGTH\t\t1\t1\t\t1\t0\r\n";

        [Fact]
        public void An_spf_is_read_from_its_header_with_group_names_resolved()
        {
            List<ParameterClassificationRules.SpfEntry> all = ParameterClassificationRules.ReadSpf(Spf);
            Assert.Equal(2, all.Count);
            Assert.Equal("HZ_Code", all[0].Name);
            Assert.Equal("Horizun", all[0].Group);
            Assert.Equal("TEXT", all[0].DataType);
            Assert.Equal(Guid.Parse("0b9f7e2c-3a41-4c8e-bb6d-5d2e9a7c4f22"), all[1].Guid);
            Assert.Equal("Identity", all[1].Group);
        }

        [Fact]
        public void An_empty_or_headerless_file_holds_no_definitions()
        {
            Assert.Empty(ParameterClassificationRules.ReadSpf(""));
            Assert.Empty(ParameterClassificationRules.ReadSpf("PARAM\t6f1c0a52-8e3d-4a51-9a0e-2b7a1f0c1d11\tX\tTEXT"));
        }

        [Fact]
        public void A_unicode_file_on_disk_reads_the_same()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz-spf-test-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, Spf, System.Text.Encoding.Unicode);
            try { Assert.Equal(2, ParameterClassificationRules.ReadSpf(File.ReadAllText(path)).Count); }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData("Text", "String.Text")]
        [InlineData("yesno", "Boolean.YesNo")]
        [InlineData("ParameterType.Length", "Length")]
        [InlineData("Integer", "Int.Integer")]
        public void Legacy_parameter_type_names_map_to_spec_paths(string legacy, string path)
            => Assert.Equal(path, ParameterClassificationRules.LegacySpecPath(legacy));

        [Fact]
        public void An_unknown_legacy_name_maps_to_nothing()
            => Assert.Null(ParameterClassificationRules.LegacySpecPath("Banana"));

        [Theory]
        [InlineData("autodesk.spec.aec:length-2.0.0", "autodesk.spec.aec:length")]
        [InlineData("autodesk.spec.aec:length", "autodesk.spec.aec:length")]
        [InlineData("autodesk.spec:spec.string-2.0.0", "autodesk.spec:spec.string")]
        public void Spec_versions_are_stripped(string id, string expected)
            => Assert.Equal(expected, ParameterClassificationRules.Unversioned(id));

        private static List<ParameterClassificationRules.TypeCode> Types() => new List<ParameterClassificationRules.TypeCode>
        {
            new ParameterClassificationRules.TypeCode { Id = 1, Name = "W1", Category = "Walls", Code = "A.10", Instances = 4 },
            new ParameterClassificationRules.TypeCode { Id = 2, Name = "W2", Category = "Walls", Code = "", Instances = 3 },
            new ParameterClassificationRules.TypeCode { Id = 3, Name = "W3", Category = "Walls", Code = null, Instances = 0 },
            new ParameterClassificationRules.TypeCode { Id = 4, Name = "D1", Category = "Doors", Code = "Z.99", Instances = 1 },
            new ParameterClassificationRules.TypeCode { Id = 5, Name = "D2", Category = "Doors", Code = "A.10", Instances = 2 }
        };

        private static readonly string[] Table = { "A", "A.10", "A.20" };

        [Fact]
        public void Unused_codes_are_the_table_codes_no_type_carries()
        {
            JObject r = ParameterClassificationRules.Unused(Table, Types(), 100);
            Assert.Equal(new[] { "A", "A.20" }, ((JArray)r["unused"]).ToObject<string[]>());
            Assert.False((bool)r["truncated"]);
        }

        [Fact]
        public void Missing_codes_name_placed_types_without_a_code_and_codes_outside_the_table()
        {
            JObject r = ParameterClassificationRules.Missing(Table, Types(), 100);
            Assert.Equal(1, (int)r["placed_types_without_code"]);      // W2; W3 is not placed
            Assert.Equal(2L, (long)r["without_code"][0]["type_id"]);
            Assert.Equal(1, (int)r["types_with_unknown_code"]);        // Z.99
            Assert.Equal("Z.99", (string)r["unknown_code"][0]["code"]);
        }

        [Fact]
        public void An_empty_table_makes_every_code_unknown_rather_than_passing()
        {
            JObject r = ParameterClassificationRules.Missing(new string[0], Types(), 100);
            Assert.Equal(3, (int)r["types_with_unknown_code"]);
        }

        [Fact]
        public void Usage_counts_types_and_their_placed_instances_per_code()
        {
            Dictionary<string, int[]> u = ParameterClassificationRules.UsageByCode(Types());
            Assert.Equal(new[] { 2, 6 }, u["A.10"]);
            Assert.Equal(new[] { 1, 1 }, u["Z.99"]);
            Assert.False(u.ContainsKey(""));
        }

        [Fact]
        public void Rows_are_capped_and_the_cap_is_said()
        {
            JObject r = ParameterClassificationRules.Unused(Table, Types(), 1);
            Assert.Single((JArray)r["unused"]);
            Assert.True((bool)r["truncated"]);
        }
    }
}
