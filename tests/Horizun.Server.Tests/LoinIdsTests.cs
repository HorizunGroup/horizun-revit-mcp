// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The structured LOIN (ISO 7817-1) in project-context.json, and its translation
// to IDS 1.0 by horizun_project_context operation=ids_from_loin.
//
// What must hold, proved against the shipped handler and resources embedded under
// the server's own logical names:
//
//   * the loin block is optional and additive: a context with one is valid, and
//     validate names its contradictions by rule (a property with two data types,
//     an entity the targeted IFC schema does not have, a repeated id, bounds that
//     admit nothing);
//   * the alphanumerical part becomes IDS facets, and EVERYTHING else is listed as
//     not translated - geometry aspect by aspect, documentation, actors, a Revit
//     category, bounds in a non-default unit - never approximated into a facet;
//   * the IDS is valid against the published ids.xsd 1.0 AND clean for the IDS
//     reader the bridge's validators use; a file that passes one and not the other
//     is caught;
//   * it rehearses by default, writes only under full_write, re-reads and proves
//     what it wrote, and never replaces a file without overwrite=true.
//
// Neutral fixture data only.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class LoinIdsTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        public LoinIdsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-loin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-loin-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private void Profile(string profile)
            => File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""" + profile + @"""}");

        private string Write(JObject doc, string name = "project-context.json")
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, doc.ToString());
            return path;
        }

        private static JObject Call(JObject args) => ProjectContext.Handle(args);

        private static JObject Context() => JObject.Parse(@"{
  ""schema_version"": 1,
  ""project"": { ""code"": ""HZ01"", ""name"": ""Test building"" },
  ""delivery"": { ""ifc"": { ""version"": ""IFC4"" } },
  ""loin"": {
    ""source"": ""EIR rev 2"",
    ""requirements"": [
      {
        ""id"": ""W-01"", ""purpose"": ""Fire safety review"", ""milestone"": ""Stage 4"",
        ""actors"": { ""provider"": ""Architect"", ""receiver"": ""Fire engineer"" },
        ""applies_to"": { ""ifc_entity"": ""IfcWall"", ""revit_category"": ""OST_Walls"" },
        ""geometry"": { ""detail"": ""simplified envelope"", ""dimensionality"": ""3D"", ""location"": ""absolute"",
                        ""appearance"": ""none"", ""parametric_behaviour"": ""not required"" },
        ""alphanumeric"": {
          ""identification"": ""by type name"",
          ""attributes"": [ { ""name"": ""Name"", ""pattern"": ""W-[0-9]{3}"" } ],
          ""properties"": [
            { ""property_set"": ""Pset_WallCommon"", ""name"": ""FireRating"", ""data_type"": ""IfcLabel"", ""allowed_values"": [""EI60"", ""EI90""] },
            { ""property_set"": ""Pset_WallCommon"", ""name"": ""IsExternal"", ""data_type"": ""IfcBoolean"", ""allowed_values"": [""true""] },
            { ""property_set"": ""Qto_WallBaseQuantities"", ""name"": ""Width"", ""data_type"": ""IfcLengthMeasure"", ""min_inclusive"": 0.1, ""max_inclusive"": 0.5, ""unit"": ""m"" },
            { ""property_set"": ""Pset_WallCommon"", ""name"": ""ThermalTransmittance"", ""data_type"": ""IfcThermalTransmittanceMeasure"", ""max_inclusive"": 0.35,
              ""uri"": ""https://identifier.buildingsmart.org/uri/buildingsmart/ifc/4.3/prop/ThermalTransmittance"" }
          ]
        },
        ""documentation"": [ { ""name"": ""Fire test certificate"", ""format"": ""pdf"" } ]
      },
      {
        ""id"": ""D-01"", ""purpose"": ""Handover"", ""occurrence"": ""required"",
        ""applies_to"": { ""ifc_entity"": ""IfcDoor"", ""classification"": { ""system"": ""Uniclass 2015"", ""code"": ""Pr_30_59_24"" } },
        ""alphanumeric"": {
          ""properties"": [
            { ""property_set"": ""Pset_DoorCommon"", ""name"": ""AcousticRating"", ""data_type"": ""IfcLabel"", ""pattern"": ""[0-9]{2} dB"" },
            { ""property_set"": ""Pset_ManufacturerTypeInformation"", ""name"": ""NominalHeight"", ""data_type"": ""IfcLengthMeasure"", ""min_inclusive"": 2000, ""unit"": ""mm"" }
          ]
        }
      },
      {
        ""id"": ""R-01"",
        ""applies_to"": { ""revit_category"": ""OST_Rooms"" },
        ""alphanumeric"": { ""properties"": [ { ""property_set"": ""Pset_SpaceCommon"", ""name"": ""Reference"", ""data_type"": ""IfcIdentifier"" } ] }
      }
    ]
  }
}");

        private static JArray Rules(JObject reply) => new JArray(((JArray)reply["coherence"]).Select(f => f["rule"]));

        // ---- the resources ----------------------------------------------------------

        [Fact]
        public void The_embedded_ids_xsd_is_the_repository_file_verbatim()
        {
            string repo = Path.Combine(RepositoryRoot(), "schemas", "ids", "ids-1.0.xsd");
            Assert.Equal(File.ReadAllBytes(repo), IdsSchema.Resource(IdsSchema.XsdResource));
            string text = File.ReadAllText(repo);
            Assert.Contains("targetNamespace=\"http://standards.buildingsmart.org/IDS\"", text);
            Assert.Contains("version=\"1.0.0\"", text);
        }

        [Fact]
        public void The_ifc_entity_lists_have_the_published_counts()
        {
            Assert.Equal(653, IfcEntityCatalog.Count("IFC2X3"));
            Assert.Equal(776, IfcEntityCatalog.Count("IFC4"));
            Assert.Equal(876, IfcEntityCatalog.Count("IFC4X3_ADD2"));
            Assert.Equal("IfcWallStandardCase", IfcEntityCatalog.Find("IFC4", "IFCWALLSTANDARDCASE"));
            Assert.Null(IfcEntityCatalog.Find("IFC4", "IfcBuiltElement"));
            Assert.Equal("IfcBuiltElement", IfcEntityCatalog.Find("IFC4X3_ADD2", "ifcbuiltelement"));
            Assert.Null(IfcEntityCatalog.Find("IFC4X3_ADD2", "IfcWal"));
        }

        [Theory]
        [InlineData("IFC4", "IFC4")]
        [InlineData("IFC2x3", "IFC2X3")]
        [InlineData("IFC2X3 CV2.0", "IFC2X3")]
        [InlineData("IFC4x3", "IFC4X3_ADD2")]
        [InlineData("IFC4X3_ADD2", "IFC4X3_ADD2")]
        [InlineData("IFC4 ADD2 TC1", "IFC4")]
        [InlineData("IFC5", null)]
        public void Delivery_versions_map_onto_the_three_ids_names(string written, string expected)
            => Assert.Equal(expected, LoinIds.MapVersion(written));

        // ---- validate -----------------------------------------------------------------

        [Fact]
        public void A_context_with_a_loin_block_is_valid_and_coherent()
        {
            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write(Context()) });
            Assert.True((bool)reply["valid"], reply["errors"].ToString());
            Assert.DoesNotContain(((JArray)reply["coherence"]).OfType<JObject>(),
                f => ((string)f["rule"]).StartsWith("loin_") && (string)f["severity"] == "error");
        }

        [Fact]
        public void The_schema_refuses_a_malformed_loin_by_pointer()
        {
            JObject doc = Context();
            doc["loin"]["requirements"][0]["geometry"]["dimensionality"] = "4D";
            doc["loin"]["requirements"][1]["alphanumeric"]["properties"][0]["data_type"] = "IFC LABEL";
            ((JObject)doc["loin"]["requirements"][2]).Remove("id");
            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write(doc) });
            Assert.Equal("invalid", (string)reply["state"]);
            var pointers = ((JArray)reply["errors"]).Select(e => (string)e["pointer"]).ToList();
            Assert.Contains("/loin/requirements/0/geometry/dimensionality", pointers);
            Assert.Contains("/loin/requirements/1/alphanumeric/properties/0/data_type", pointers);
            Assert.Contains("/loin/requirements/2/id", pointers);
        }

        [Fact]
        public void Loin_contradictions_are_inconsistent_and_named_by_rule()
        {
            JObject doc = Context();
            var reqs = (JArray)doc["loin"]["requirements"];
            // Same pset.property, a second data type, in another requirement.
            ((JArray)reqs[1]["alphanumeric"]["properties"]).Add(JObject.Parse(
                @"{ ""property_set"": ""Pset_WallCommon"", ""name"": ""FireRating"", ""data_type"": ""IfcText"" }"));
            reqs[2]["id"] = "W-01";                                                     // repeated id
            reqs[2]["applies_to"]["ifc_entity"] = "IfcWal";                             // no such entity
            reqs[0]["alphanumeric"]["properties"][2]["min_inclusive"] = 0.9;            // 0.9 > 0.5
            reqs[0]["alphanumeric"]["attributes"][0]["pattern"] = "^W-[0-9]{3}$";      // anchors are literal in XSD

            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write(doc) });
            Assert.Equal("inconsistent", (string)reply["state"]);
            var rules = Rules(reply).Select(t => (string)t).ToList();
            Assert.Contains("loin_property_type_conflict", rules);
            Assert.Contains("loin_duplicate_requirement_id", rules);
            Assert.Contains("loin_unknown_ifc_entity", rules);
            Assert.Contains("loin_bounds_inverted", rules);
            Assert.Contains("loin_pattern_anchor", rules);
            JObject conflict = ((JArray)reply["coherence"]).OfType<JObject>().First(f => (string)f["rule"] == "loin_property_type_conflict");
            Assert.Equal("/loin/requirements/1/alphanumeric/properties/2/data_type", (string)conflict["pointer"]);
            Assert.Contains("/loin/requirements/0/alphanumeric/properties/0", (string)conflict["message"]);
        }

        [Fact]
        public void An_entity_is_judged_against_the_schemas_the_requirement_targets()
        {
            JObject doc = Context();
            var req = (JObject)doc["loin"]["requirements"][0];
            req["applies_to"]["ifc_entity"] = "IfcBuiltElement";
            req["ifc_versions"] = new JArray("IFC4", "IFC4X3_ADD2");
            JObject reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write(doc) });
            JObject finding = ((JArray)reply["coherence"]).OfType<JObject>().Single(f => (string)f["rule"] == "loin_unknown_ifc_entity");
            Assert.Contains("not an entity of IFC4", (string)finding["message"]);
            Assert.Contains("exists in IFC4X3_ADD2", (string)finding["message"]);

            req["ifc_versions"] = new JArray("IFC4X3_ADD2");
            reply = Call(new JObject { ["operation"] = "validate", ["path"] = Write(doc, "b.json") });
            Assert.DoesNotContain("loin_unknown_ifc_entity", Rules(reply).Select(t => (string)t));
        }

        // ---- ids_from_loin ------------------------------------------------------------

        [Fact]
        public void The_alphanumerical_part_becomes_ids_and_everything_else_is_listed()
        {
            JObject reply = Call(new JObject { ["operation"] = "ids_from_loin", ["path"] = Write(Context()), ["info"] = new JObject { ["date"] = "2026-09-24" } });

            Assert.False((bool)reply["written"]);
            Assert.True((bool)reply["dry_run"]);
            Assert.Equal(3, (int)reply["requirements_considered"]);
            Assert.Equal(new[] { "W-01", "D-01" }, reply["specifications"].Select(s => (string)s["requirement_id"]).ToArray());
            Assert.True((bool)reply["validation"]["xsd"]["valid"], reply["validation"].ToString());
            Assert.True((bool)reply["validation"]["reader"]["clean"], reply["validation"].ToString());

            var skipped = ((JArray)reply["not_translated"]).OfType<JObject>().ToList();
            string[] aspects = skipped.Select(s => (string)s["requirement_id"] + ":" + (string)s["aspect"]).ToArray();
            foreach (string g in new[] { "detail", "dimensionality", "location", "appearance", "parametric_behaviour" })
                Assert.Contains("W-01:geometry." + g, aspects);
            Assert.Contains("W-01:documentation[0]", aspects);
            Assert.Contains("W-01:applies_to.revit_category", aspects);
            Assert.Contains("W-01:alphanumeric.identification", aspects);
            Assert.Equal("description_text", (string)skipped.Single(s => (string)s["aspect"] == "actors")["handling"]);
            // Bounds in mm are not converted into the IFC default unit.
            Assert.Contains(skipped, s => (string)s["requirement_id"] == "D-01" && ((string)s["aspect"]).EndsWith("NominalHeight.bounds"));
            // A Revit category alone has no IDS applicability: the whole requirement is listed.
            Assert.Contains(skipped, s => (string)s["requirement_id"] == "R-01" && (string)s["aspect"] == "requirement");

            // And the file says what the LOIN said, read by the bridge's own IDS reader.
            var xml = new XmlDocument();
            xml.LoadXml((string)reply["ids_xml"]);
            string error;
            IdsFile ids = IdsReader.Parse(xml, out error);
            Assert.Null(error);
            Assert.Empty(ids.Problems);
            Assert.Equal("2026-09-24", ids.Date);
            IdsSpecification wall = ids.Specifications[0];
            Assert.Equal("W-01", wall.Identifier);
            Assert.Equal(new[] { "IFC4" }, wall.IfcVersions);
            Assert.Equal(IdsApplicabilityMode.Optional, wall.Mode);
            Assert.Equal("IFCWALL", wall.Applicability.Entity.Name.Simple);
            Assert.Contains("Architect -> Fire engineer", wall.Description);
            Assert.Single(wall.Requirements.Attributes);
            Assert.Equal(new[] { "W-[0-9]{3}" }, wall.Requirements.Attributes[0].Value.Patterns);
            IdsPropertyFacet fire = wall.Requirements.Properties.Single(p => p.BaseName.Simple == "FireRating");
            Assert.Equal("IFCLABEL", fire.DataType);
            Assert.Equal(new[] { "EI60", "EI90" }, fire.Value.Enumeration);
            Assert.Equal("true", wall.Requirements.Properties.Single(p => p.BaseName.Simple == "IsExternal").Value.Simple);
            IdsPropertyFacet width = wall.Requirements.Properties.Single(p => p.BaseName.Simple == "Width");
            Assert.Equal(0.1, width.Value.MinInclusive);
            Assert.Equal(0.5, width.Value.MaxInclusive);
            IdsPropertyFacet u = wall.Requirements.Properties.Single(p => p.BaseName.Simple == "ThermalTransmittance");
            Assert.StartsWith("https://identifier.buildingsmart.org/", u.Uri);

            IdsSpecification door = ids.Specifications[1];
            Assert.Equal(IdsApplicabilityMode.Required, door.Mode);
            Assert.Equal("Uniclass 2015", door.Applicability.Classifications[0].System.Simple);
            Assert.Equal("Pr_30_59_24", door.Applicability.Classifications[0].Value.Simple);
            IdsPropertyFacet height = door.Requirements.Properties.Single(p => p.BaseName.Simple == "NominalHeight");
            Assert.Null(height.Value);   // its only rule was the mm bound, which was not translated
        }

        [Fact]
        public void Filters_select_requirements_and_a_requirement_without_a_version_is_listed_not_guessed()
        {
            JObject doc = Context();
            ((JObject)doc["delivery"]["ifc"]).Remove("version");
            doc["loin"]["requirements"][1]["ifc_versions"] = new JArray("IFC4X3_ADD2");
            string path = Write(doc);

            JObject reply = Call(new JObject { ["operation"] = "ids_from_loin", ["path"] = path });
            Assert.Equal(new[] { "D-01" }, reply["specifications"].Select(s => (string)s["requirement_id"]).ToArray());
            Assert.Contains(((JArray)reply["not_translated"]).OfType<JObject>(),
                s => (string)s["requirement_id"] == "W-01" && ((string)s["why"]).Contains("no IFC version"));

            JObject only = Call(new JObject { ["operation"] = "ids_from_loin", ["path"] = path, ["requirement_ids"] = new JArray("D-01") });
            Assert.Equal(1, (int)only["requirements_considered"]);
            Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "ids_from_loin", ["path"] = path, ["requirement_ids"] = new JArray("NOPE")
            }));
        }

        [Fact]
        public void It_writes_only_under_full_write_rereads_and_never_replaces_without_overwrite()
        {
            string path = Write(Context());
            string output = Path.Combine(_dir, "loin.ids");
            JObject write = new JObject { ["operation"] = "ids_from_loin", ["path"] = path, ["output_path"] = output, ["dry_run"] = false };

            Profile("safe_write");
            ToolRefusal refused = Assert.Throws<ToolRefusal>(() => Call((JObject)write.DeepClone()));
            Assert.Contains("permission_profile=safe_write", refused.Message);
            Assert.False(File.Exists(output));

            Profile("full_write");
            JObject reply = Call((JObject)write.DeepClone());
            Assert.True((bool)reply["written"]);
            Assert.True((bool)reply["verification"]["reread"]);
            Assert.Equal(ProjectContext.Sha256Hex(File.ReadAllBytes(output)), (string)reply["verification"]["sha256"]);
            Assert.Equal(2, (int)reply["verification"]["reader_specifications"]);
            Assert.Empty(IdsSchema.Validate(File.ReadAllBytes(output)));
            string error;
            Assert.Equal(2, IdsReader.Read(output, out error).Specifications.Count);
            Assert.Null(error);

            byte[] before = File.ReadAllBytes(output);
            Assert.Throws<ToolRefusal>(() => Call((JObject)write.DeepClone()));
            Assert.Equal(before, File.ReadAllBytes(output));
            write["overwrite"] = true;
            Assert.True((bool)Call(write)["written"]);
        }

        [Fact]
        public void A_contradictory_or_invalid_loin_is_not_translated()
        {
            JObject doc = Context();
            ((JArray)doc["loin"]["requirements"][1]["alphanumeric"]["properties"]).Add(JObject.Parse(
                @"{ ""property_set"": ""Pset_WallCommon"", ""name"": ""FireRating"", ""data_type"": ""IfcText"" }"));
            ToolRefusal r = Assert.Throws<ToolRefusal>(() =>
                Call(new JObject { ["operation"] = "ids_from_loin", ["path"] = Write(doc) }));
            Assert.Contains("loin_property_type_conflict", r.Message);

            JObject invalid = Context();
            invalid["loin"]["requirements"][0]["occurrence"] = "sometimes";
            Assert.Throws<ToolRefusal>(() => Call(new JObject { ["operation"] = "ids_from_loin", ["path"] = Write(invalid, "i.json") }));

            Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "ids_from_loin", ["path"] = Write(Context(), "a.json"), ["info"] = new JObject { ["author"] = "not an address" }
            }));
            Assert.Throws<ToolRefusal>(() => Call(new JObject
            {
                ["operation"] = "ids_from_loin", ["path"] = Write(Context(), "o.json"), ["output_path"] = Path.Combine(_dir, "x.xml")
            }));
        }

        [Fact]
        public void The_proof_catches_a_schema_violation_and_a_file_our_reader_cannot_decide()
        {
            const string head = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ids:ids xmlns:ids=""http://standards.buildingsmart.org/IDS"" xmlns:xs=""http://www.w3.org/2001/XMLSchema"">
  <ids:info><ids:title>T</ids:title></ids:info>
  <ids:specifications>";
            const string tail = "</ids:specifications></ids:ids>";

            // Against the XSD: an unknown ifcVersion and a cardinality the schema does not define.
            byte[] bad = Encoding.UTF8.GetBytes(head + @"
    <ids:specification name=""S"" ifcVersion=""IFC5"">
      <ids:applicability><ids:entity><ids:name><ids:simpleValue>IFCWALL</ids:simpleValue></ids:name></ids:entity></ids:applicability>
      <ids:requirements><ids:attribute cardinality=""maybe""><ids:name><ids:simpleValue>Name</ids:simpleValue></ids:name></ids:attribute></ids:requirements>
    </ids:specification>" + tail);
            JObject proof = LoinIds.Prove(bad, 1);
            Assert.False((bool)proof["xsd"]["valid"]);
            Assert.Equal(2, ((JArray)proof["xsd"]["errors"]).Count);

            // Valid for the XSD, but an applicability with no facet applies to every element:
            // our reader calls it undecidable, so the file is not clean.
            byte[] undecidable = Encoding.UTF8.GetBytes(head + @"
    <ids:specification name=""S"" ifcVersion=""IFC4""><ids:applicability/></ids:specification>" + tail);
            proof = LoinIds.Prove(undecidable, 1);
            Assert.True((bool)proof["xsd"]["valid"], proof.ToString());
            Assert.False((bool)proof["reader"]["clean"]);
        }

        [Fact]
        public void The_contract_offers_the_operation_without_changing_the_tool_effect()
        {
            CommandContract c = Contract.Find("horizun_project_context");
            Assert.Contains("ids_from_loin", c.InputSchema["properties"]["operation"]["enum"].Select(t => (string)t));
            Assert.NotNull(c.InputSchema["properties"]["output_path"]);
            Assert.Equal(ToolEffect.ExternalSideEffectOnRequest, c.Effect);
        }

        private static string RepositoryRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "schemas"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
