// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A DOOR NEEDS A WALL.
//
// Revit hosts a door or a window IN a wall. An instance placed without one is a
// door-shaped object standing beside the opening it was meant to be - and it
// creates, verifies, and schedules perfectly, which is what makes it dangerous.
//
// A drawing has no ids, so the plan cannot name the wall. What it CAN do is say
// what kind of host the element needs, and refuse to be silent about it. These
// tests pin that the plan says so, and that it says so only for the things
// Revit actually hosts.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadHostedOpeningTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static CadRequirementSet Set(string produces, string category, string familyType = null)
        {
            string family = familyType == null ? "" : ", 'family_type': '" + familyType + "'";
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'openings', 'version': '1.0.0', 'title': 'Openings' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-DOOR*'], 'produces': 'PRODUCES',
                          'category': 'CATEGORY', 'level': 'Level 1'FAMILY,
                          'geometry': { 'from': 'point_clusters', 'cluster_radius_mm': 600.0 } }]
            }".Replace('\'', '"').Replace("PRODUCES", produces).Replace("CATEGORY", category)
              .Replace("FAMILY", family);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        /// <summary>A door symbol as a drawing carries it: a few short marks close together.</summary>
        private static List<CadSegment> Symbol(double x, double y, string layer = "A-DOOR")
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(x, y), new CadPoint(x + 100, y), layer),
                new CadSegment(new CadPoint(x + 100, y), new CadPoint(x + 100, y + 100), layer),
                new CadSegment(new CadPoint(x + 100, y + 100), new CadPoint(x, y + 100), layer)
            };
        }

        private static JObject FirstRow(CadRequirementSet set, IEnumerable<CadSegment> segs)
        {
            CadInterpretation r = CadInterpretationRules.Interpret(segs.ToList(), set, Sha);
            CadConversionPlan plan = CadConversionPlanRules.Plan(r, set, "fp", true);
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "M");
            if (requests.Count == 0) return null;
            return (JObject)((JArray)requests[0]["elements"])[0];
        }

        [Fact]
        public void A_planned_DOOR_says_it_needs_a_wall_to_live_in()
        {
            JObject row = FirstRow(Set("door", "OST_Doors", "Single-Flush"), Symbol(5000, 0));
            Assert.NotNull(row);
            Assert.Equal("family_instance", (string)row["kind"]);
            Assert.Equal("wall", (string)row["hosted_on"]);
            Assert.NotNull(row["point"]);
        }

        [Fact]
        public void A_planned_WINDOW_says_the_same()
        {
            JObject row = FirstRow(Set("window", "OST_Windows", "Fixed"), Symbol(5000, 0, "A-DOOR-WIND"));
            Assert.Equal("wall", (string)row["hosted_on"]);
        }

        [Fact]
        public void An_architectural_COLUMN_does_not_claim_a_wall_host()
        {
            // A column stands on a level. Asking for a wall would refuse every
            // column in every drawing that has no wall beside it.
            JObject row = FirstRow(Set("column", "OST_Columns", "Rectangular Column"), Symbol(5000, 0));
            Assert.Equal("family_instance", (string)row["kind"]);
            Assert.Null(row["hosted_on"]);
        }

        [Fact]
        public void And_the_family_the_rule_NAMED_travels_with_it()
        {
            // create_elements needs a FamilySymbol for a family_instance and has
            // no default to fall back to. A door rule that names no family is a
            // door that cannot be built, and the name must survive the plan.
            JObject row = FirstRow(Set("door", "OST_Doors", "Single-Flush"), Symbol(5000, 0));
            Assert.Equal("Single-Flush", (string)row["type_name"]);
        }

        [Fact]
        public void A_door_rule_that_names_NO_family_still_plans_and_says_what_is_missing()
        {
            // The refusal belongs at create time, where the document knows which
            // families are loaded - not here, where the plan would have to guess
            // that a symbol named nothing is a mistake rather than a default.
            JObject row = FirstRow(Set("door", "OST_Doors"), Symbol(5000, 0));
            Assert.NotNull(row);
            Assert.Null(row["type_name"]);
            Assert.Equal("wall", (string)row["hosted_on"]);
        }
    }
}
