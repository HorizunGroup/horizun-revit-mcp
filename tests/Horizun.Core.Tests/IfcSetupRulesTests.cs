// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_export format=ifc_setup (IfcSetupRules): what may go into the IFC
// exporter's saved-setup JSON, the per-type class mapping, and the comparison a
// re-read is judged by. An unknown exporter key is refused, never written and
// silently ignored.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IfcSetupRulesTests
    {
        private static JObject Setup(string json) => JObject.Parse(json);

        [Fact]
        public void Schema_identity_is_the_exporter_uis_own()
        {
            Assert.Equal("C2A3E6FE-CE51-4F35-8FF1-20C34567B687", IfcSetupRules.SchemaGuid);
            Assert.Equal("IFCExportConfigurationMap", IfcSetupRules.SchemaName);
            Assert.Equal("MapField", IfcSetupRules.FieldName);
        }

        [Fact]
        public void Builds_the_exporter_json_with_version_options_and_classification()
        {
            IfcSetupRequest r = IfcSetupRules.Read(Setup(@"{ ""name"": ""HZ_IFC4RV_Coordinacion"",
                ""options"": { ""ExportIFCCommonPropertySets"": true, ""ExportInternalRevitPropertySets"": true },
                ""classification"": { ""ClassificationName"": ""OmniClass"", ""ClassificationFieldName"": ""ClassificationCode"" } }"),
                baseQuantities: true, splitWalls: null, spaceBoundaryLevel: 1);
            JObject config = IfcSetupRules.BuildConfig(r, 9);
            Assert.Equal("HZ_IFC4RV_Coordinacion", (string)config["Name"]);
            Assert.Equal(9, (int)config["IFCVersion"]);
            Assert.False((bool)config["IsBuiltIn"]);
            Assert.False((bool)config["IsInSession"]);
            Assert.True((bool)config["ExportBaseQuantities"]);
            Assert.Equal(1, (int)config["SpaceBoundaries"]);
            Assert.Null(config["SplitWallsAndColumns"]);
            Assert.True((bool)config["ExportIFCCommonPropertySets"]);
            Assert.Equal("OmniClass", (string)config["ClassificationSettings"]["ClassificationName"]);
        }

        [Theory]
        [InlineData(@"{ ""name"": ""A"", ""options"": { ""ExportLinkedFiles"": true } }", "not an exporter option")]
        [InlineData(@"{ ""name"": ""A"", ""options"": { ""ExportBaseQuantities"": ""yes"" } }", "must be a boolean")]
        [InlineData(@"{ ""name"": ""A"", ""classification"": { ""ClassificationEditionDate"": ""2020"" } }", "is not written")]
        [InlineData(@"{ ""name"": """" }", "1..120")]
        [InlineData(@"{ ""name"": ""a/b"" }", "refuses")]
        [InlineData(@"{ ""name"": ""A"", ""extra"": 1 }", "does not take 'extra'")]
        [InlineData(@"{ ""name"": ""A"", ""type_mappings"": [ { ""type_id"": 5, ""export_as"": ""footing"" } ] }", "IFC entity name")]
        [InlineData(@"{ ""name"": ""A"", ""type_mappings"": [ { ""type_id"": 5, ""export_as"": ""IfcFooting"", ""predefined_type"": ""pad"" } ] }", "upper case")]
        [InlineData(@"{ ""name"": ""A"", ""type_mappings"": [ { ""type_id"": 5, ""export_as"": ""IfcFooting"" }, { ""type_id"": 5, ""export_as"": ""IfcSlab"" } ] }", "mapped twice")]
        [InlineData(@"{ ""name"": ""A"", ""options"": { ""SpaceBoundaries"": 3 } }", "0, 1 or 2")]
        public void Refuses_by_name(string json, string expected)
        {
            var ex = Assert.Throws<ArgumentException>(() => IfcSetupRules.Read(Setup(json), null, null, null));
            Assert.Contains(expected, ex.Message);
        }

        [Fact]
        public void An_option_that_disagrees_with_the_export_argument_is_refused()
        {
            var ex = Assert.Throws<ArgumentException>(() => IfcSetupRules.Read(
                Setup(@"{ ""name"": ""A"", ""options"": { ""ExportBaseQuantities"": false } }"), true, null, null));
            Assert.Contains("disagrees", ex.Message);
        }

        [Fact]
        public void Reads_type_mappings_with_optional_predefined_type()
        {
            IfcSetupRequest r = IfcSetupRules.Read(Setup(@"{ ""name"": ""A"", ""type_mappings"": [
                { ""type_id"": 101, ""export_as"": ""IfcFooting"", ""predefined_type"": ""PAD_FOOTING"" },
                { ""type_id"": 102, ""export_as"": ""IfcCovering"", ""predefined_type"": """" } ] }"), null, null, null);
            Assert.Equal(2, r.TypeMappings.Count);
            Assert.Equal("PAD_FOOTING", r.TypeMappings[0].PredefinedType);
            Assert.Null(r.TypeMappings[1].PredefinedType);
        }

        [Fact]
        public void Mismatches_names_every_key_the_reread_lost_or_changed()
        {
            var requested = JObject.Parse(@"{ ""Name"": ""A"", ""IFCVersion"": 9, ""ExportBaseQuantities"": true,
                ""ClassificationSettings"": { ""ClassificationName"": ""OmniClass"" } }");
            Assert.Empty(IfcSetupRules.Mismatches(requested, JObject.Parse(@"{ ""Name"": ""A"", ""IFCVersion"": 9.0, ""ExportBaseQuantities"": true,
                ""Other"": 1, ""ClassificationSettings"": { ""ClassificationName"": ""OmniClass"", ""X"": 2 } }")));
            Assert.Equal(new[] { "IFCVersion", "ExportBaseQuantities", "ClassificationSettings.ClassificationName" },
                IfcSetupRules.Mismatches(requested, JObject.Parse(@"{ ""Name"": ""A"", ""IFCVersion"": 6,
                    ""ClassificationSettings"": { ""ClassificationName"": ""Uniclass"" } }")).ToArray());
            Assert.Equal(new[] { "(setup not found)" }, IfcSetupRules.Mismatches(requested, null).ToArray());
        }

        [Fact]
        public void Setup_names_compare_like_the_exporter_map()
        {
            Assert.True(IfcSetupRules.SameName("HZ_IFC4RV_Coordinacion", " hz_ifc4rv_coordinacion "));
            Assert.False(IfcSetupRules.SameName("HZ_IFC4RV", "HZ_IFC4"));
        }
    }
}
