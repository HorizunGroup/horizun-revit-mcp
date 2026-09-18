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
