// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// AN ANONYMOUS BLOCK SAYS WHAT IT IS THROUGH ITS ATTRIBUTES.
//
// MEASURED (E-300): the smoke and smoke/CO detectors of the units plan are dynamic
// blocks inserted under anonymous names ("*U27", "*U32") that change from one
// insertion to the next. What stays is the attribute tags: SD for a smoke
// detector, SD and CO for a smoke/CO detector. These pin selection by tag
// presence and absence, and the rule-collision check that must tell two such
// rules apart.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadBlockAttributeRuleTests
    {
        private static CadRequirementSet Set(string smokeAttrs, string coAttrs)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'd', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [
                { 'id': 'r-smoke', 'layers': ['E-LTS'], 'produces': 'electrical_fixture', 'category': 'OST_FireAlarmDevices',
                  'family_type': 'HZ-TEST Smoke Detector: Smoke', 'level': 'Level 1',
                  'geometry': { 'from': 'blocks', 'block_attributes': @@SMOKE@@ } },
                { 'id': 'r-smoke-co', 'layers': ['E-LTS'], 'produces': 'electrical_fixture', 'category': 'OST_FireAlarmDevices',
                  'family_type': 'HZ-TEST Smoke Detector: Smoke-CO', 'level': 'Level 1',
                  'geometry': { 'from': 'blocks', 'block_attributes': @@COATTR@@ } } ]
            }";
            return CadRequirementSet.Load(JObject.Parse(doc.Replace("@@SMOKE@@", smokeAttrs).Replace("@@COATTR@@", coAttrs)
                                                           .Replace('\'', '"')));
        }

        private static CadIrEntity Block(string name, double x, params string[] tags)
        {
            var e = new CadIrEntity
            {
                Id = "b" + x, Handle = "B" + x, Kind = CadEntityKind.BlockInstance, BlockName = name, Layer = "E-LTS",
                Space = "model", Attributes = tags.ToDictionary(t => t, t => "1")
            };
            e.Points.Add(new CadPoint(x, 0));
            return e;
        }

        [Fact]
        public void Tags_decide_which_rule_claims_an_anonymous_detector()
        {
            CadRequirementSet set = Set("{ 'SD': 'present', 'CO': 'absent' }", "{ 'sd': 'present', 'co': 'present' }");
            var blocks = new List<CadIrEntity>
            {
                Block("*U27", 0, "SD"), Block("*U32", 1000, "SD", "CO"), Block("*U40", 2000, "TV")
            };
            CadBlockReading r = CadBlockRules.Interpret(blocks, set, "sha");
            Assert.Equal(2, r.InstancesClaimed);
            Assert.Equal("r-smoke", r.Candidates.Single(c => c.Geometry[0].X == 0).RuleId);
            Assert.Equal("r-smoke-co", r.Candidates.Single(c => c.Geometry[0].X == 1000).RuleId);
            Assert.Equal(CadInventoryOutcome.Unclaimed, r.Outcomes[blocks[2]].Outcome);
        }

        [Fact]
        public void Two_rules_on_one_layer_that_differ_only_by_tags_do_not_collide()
        {
            CadRequirementSet set = Set("{ 'SD': 'present', 'CO': 'absent' }", "{ 'SD': 'present', 'CO': 'present' }");
            Assert.Equal(2, set.Rules.Count);
        }

        [Fact]
        public void The_key_is_validated()
        {
            Assert.Throws<CadRequirementSetException>(() => Set("{ }", "{ 'CO': 'present' }"));
            Assert.Throws<CadRequirementSetException>(() => Set("{ 'SD': 'yes' }", "{ 'CO': 'present' }"));
            Assert.Throws<CadRequirementSetException>(() => Set("['SD']", "{ 'CO': 'present' }"));
        }
    }
}
