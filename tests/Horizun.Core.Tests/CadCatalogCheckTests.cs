// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A PROJECT'S DATA, CHECKED AS ONE SET: every problem in one pass, nothing written.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadCatalogCheckTests
    {
        private static CadRequirementSet Set(string rulesJson)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'elec', 'version': '1.0.0', 'title': 'catalogue check' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': RULES
            }".Replace('\'', '"').Replace("RULES", rulesJson);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static readonly Dictionary<string, CadTypeFacts> Model = new Dictionary<string, CadTypeFacts>
        {
            ["Duplex Receptacle: Standard"] = new CadTypeFacts { Found = true, PlacementType = "WorkPlaneBased", Category = "OST_ElectricalFixtures" },
            ["HZ-TEST Detector: Smoke"] = new CadTypeFacts { Found = true, PlacementType = "OneLevelBased", Category = "OST_FireAlarmDevices" },
            ["HZ-TEST Range Receptacle: Range"] = new CadTypeFacts { Found = true, PlacementType = "OneLevelBasedHosted", Category = "OST_ElectricalFixtures" },
            ["Basic Wall: A 161.9"] = new CadTypeFacts { Found = true, IsWallType = true, WidthMm = 161.9, Category = "OST_Walls" },
            ["Basic Wall: B 161.9"] = new CadTypeFacts { Found = true, IsWallType = true, WidthMm = 161.9, Category = "OST_Walls" },
        };

        private static CadTypeFacts Lookup(string name) =>
            Model.TryGetValue(name, out CadTypeFacts f) ? f : new CadTypeFacts { Found = false };

        private static JObject Row(JObject check, string rule) =>
            check["rules"].OfType<JObject>().Single(r => (string)r["rule"] == rule);

        [Fact]
        public void Every_problem_of_the_set_is_named_in_one_pass()
        {
            CadRequirementSet set = Set(@"[
              { 'id': 'ok', 'layers': ['E-P'], 'produces': 'electrical_fixture', 'category': 'OST_ElectricalFixtures',
                'family_type': 'Duplex Receptacle: Standard', 'level': 'Level 1', 'hosted_on': 'wall',
                'offset_mm': 457.2, 'geometry': { 'from': 'blocks', 'blocks': ['OUT'] } },
              { 'id': 'missing', 'layers': ['E-P'], 'produces': 'electrical_fixture', 'category': 'OST_ElectricalFixtures',
                'family_type': 'Project Receptacle: Kitchen', 'level': 'Level 1', 'hosted_on': 'wall',
                'geometry': { 'from': 'blocks', 'blocks': ['OUT4'] } },
              { 'id': 'level-family-on-wall', 'layers': ['E-P'], 'produces': 'electrical_fixture', 'category': 'OST_FireAlarmDevices',
                'family_type': 'HZ-TEST Detector: Smoke', 'level': 'Level 1', 'hosted_on': 'wall',
                'geometry': { 'from': 'blocks', 'blocks': ['SD'] } },
              { 'id': 'face-family-unhosted', 'layers': ['E-P'], 'produces': 'electrical_fixture', 'category': 'OST_ElectricalFixtures',
                'family_type': 'Duplex Receptacle: Standard', 'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['FLOOR-BOX'] } }
            ]");

            JObject check = CadCatalogCheck.Check(set, Lookup, new HashSet<string> { "Level 1" });

            Assert.Equal("usable", (string)Row(check, "ok")["verdict"]);
            Assert.Contains("type_not_found", Row(check, "missing")["problems"].ToString());
            Assert.Contains("hosting_incompatible", Row(check, "level-family-on-wall")["problems"].ToString());
            JObject unhosted = Row(check, "face-family-unhosted");
            Assert.Contains("hosting_incompatible", unhosted["problems"].ToString());
            Assert.Contains("level_not_found", unhosted["problems"].ToString());
            Assert.Contains("no_mounting_height", unhosted["warnings"].ToString());
            Assert.Equal(3, (int)check["counts"]["refused"]);
            Assert.False((bool)check["would_write"]);
        }

        [Fact]
        public void A_missing_type_names_what_its_family_does_have_and_chooses_none_of_them()
        {
            // MEASURED (campaign 7): Revit 2023's mechanical template has no "Rectangular Duct: Radius Elbows / Tees";
            // the refusal said only that, and correcting the set meant guessing a name.
            CadRequirementSet set = Set(@"[
              { 'id': 'duct', 'layers': ['M-SUPPLY'], 'produces': 'duct', 'family_type': 'Rectangular Duct: Radius Elbows / Tees',
                'system_type': 'Supply Air', 'level': 'Level 1', 'offset_mm': 2743.2,
                'geometry': { 'from': 'single_lines', 'merge_collinear': false } },
              { 'id': 'none', 'layers': ['M-X'], 'produces': 'duct', 'family_type': 'Oval Duct: Taps',
                'system_type': 'Supply Air', 'level': 'Level 1', 'offset_mm': 2743.2,
                'geometry': { 'from': 'single_lines', 'merge_collinear': false } }
            ]");
            var loaded = new List<string> { "Rectangular Duct: Mitered Elbows / Taps", "Rectangular Duct: Radius Elbows / Taps" };
            JObject check = CadCatalogCheck.Check(set, n => new CadTypeFacts
            {
                Found = false, SameFamily = CadCatalogCheck.FamilyOf(n) == "Rectangular Duct" ? loaded : new List<string>()
            }, new HashSet<string> { "Level 1" });

            JObject duct = Row(check, "duct");
            Assert.Equal("refused", (string)duct["verdict"]);
            string said = duct["problems"].ToString();
            Assert.Contains("loaded types of family 'Rectangular Duct'", said);
            Assert.Contains("'Rectangular Duct: Radius Elbows / Taps'", said);
            Assert.Contains("caller's decision", said);
            Assert.Equal(loaded, duct["loaded_of_this_family"].ToObject<List<string>>());
            Assert.Contains("no type of family 'Oval Duct' is loaded", Row(check, "none")["problems"].ToString());

            Assert.Equal("Rectangular Duct", CadCatalogCheck.FamilyOf("Rectangular Duct: Radius Elbows / Tees"));
            Assert.Null(CadCatalogCheck.FamilyOf("Standard"));
            Assert.Contains("no family part", CadCatalogCheck.LoadedOfFamily("Standard", loaded));
        }

        [Fact]
        public void End_faces_are_allowed_only_by_name_and_only_on_a_wall()
        {
            CadRequirementSet ok = Set(@"[
              { 'id': 'r-pier', 'layers': ['E-P'], 'produces': 'electrical_fixture', 'family_type': 'Duplex Receptacle: Standard',
                'level': 'Level 1', 'hosted_on': 'wall', 'host_faces': ['side', 'end'], 'geometry': { 'from': 'blocks', 'blocks': ['OUT2'] } }
            ]");
            Assert.Equal(new[] { "side", "end" }, ok.Rules[0].HostFaces);
            Assert.Null(Set(@"[
              { 'id': 'r-plain', 'layers': ['E-P'], 'produces': 'electrical_fixture', 'family_type': 'Duplex Receptacle: Standard',
                'level': 'Level 1', 'hosted_on': 'wall', 'geometry': { 'from': 'blocks', 'blocks': ['OUT2'] } }
            ]").Rules[0].HostFaces);
            Assert.Throws<CadRequirementSetException>(() => Set(@"[
              { 'id': 'r-bad', 'layers': ['E-P'], 'produces': 'electrical_fixture', 'family_type': 'X: Y',
                'level': 'Level 1', 'hosted_on': 'wall', 'host_faces': ['top'], 'geometry': { 'from': 'blocks', 'blocks': ['A'] } }
            ]"));
            Assert.Throws<CadRequirementSetException>(() => Set(@"[
              { 'id': 'r-slab', 'layers': ['E-P'], 'produces': 'electrical_fixture', 'family_type': 'X: Y',
                'level': 'Level 1', 'hosted_on': 'slab', 'host_faces': ['end'], 'geometry': { 'from': 'blocks', 'blocks': ['A'] } }
            ]"));
        }

        [Fact]
        public void Two_listed_wall_types_of_one_width_are_flagged_before_they_tie()
        {
            CadRequirementSet set = Set(@"[
              { 'id': 'r-wall', 'layers': ['*A-WALL'], 'produces': 'wall', 'family_type': 'Basic Wall: A 161.9',
                'level': 'Level 1', 'wall_types': { 'types': ['Basic Wall: A 161.9', 'Basic Wall: B 161.9', 'Basic Wall: C 177.8'] },
                'geometry': { 'from': 'double_lines', 'min_thickness_mm': 50, 'max_thickness_mm': 400 } }
            ]");

            JObject row = Row(CadCatalogCheck.Check(set, Lookup, new HashSet<string> { "Level 1" }), "r-wall");

            Assert.Contains("wall_type_not_found: 'Basic Wall: C 177.8'", row["problems"].ToString());
            Assert.Contains("same_width_twice", row["warnings"].ToString());
            Assert.Equal("refused", (string)row["verdict"]);
        }
    }
}
