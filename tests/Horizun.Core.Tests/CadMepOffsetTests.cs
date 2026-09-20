// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A RUN DECLARED 2743 mm ABOVE ITS STOREY WAS BUILT IN THE FLOOR.
//
// Measured in campaign 6 on a real corridor supply plan: the rule said
// offset_mm 2743.2, the candidate carried it, and every duct was emitted at the
// drawing's Z - zero. The height is relative to the storey, which only the plan
// command knows, so the plan carries it as a plan-internal key and the command
// resolves it with the level. These cases pin both halves.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadMepOffsetTests
    {
        private static CadRequirementSet Set(string rulesJson)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'mep', 'version': '1.0.0', 'title': 'MEP offset' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': RULES
            }".Replace('\'', '"').Replace("RULES", rulesJson);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static JObject DuctRow(string offset)
        {
            CadRequirementSet set = Set((@"[
              { 'id': 'supply', 'precedence': 10, 'layers': ['M-DUCT'], 'produces': 'duct', 'discipline': 'mechanical',
                'family_type': 'Round Duct: Taps', 'system_type': 'Supply Air', 'level': 'Level 1',
                'diameter_mm': 300" + offset + @",
                'geometry': { 'from': 'single_lines', 'min_length_mm': 100 } }
            ]").Replace('\'', '"'));
            var drawing = new System.Collections.Generic.List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(5000, 0), "M-DUCT")
            };
            CadInterpretation interp = CadInterpretationRules.Interpret(drawing, set, "hash");
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, "src", false);
            return plan.Actions.Single(a => a.Kind == "duct").Arguments;
        }

        [Fact]
        public void The_rule_offset_travels_with_the_run_as_a_plan_internal_key()
        {
            JObject row = DuctRow(", 'offset_mm': 2743.2".Replace('\'', '"'));
            Assert.Equal(2743.2, row.Value<double>(CadConversionPlanRules.OffsetFromLevelKey), 3);
            Assert.Equal(0.0, ((JArray)row["start"])[2].Value<double>(), 6);
        }

        [Fact]
        public void No_offset_declared_means_no_key_and_the_drawing_height_stands()
        {
            JObject row = DuctRow("");
            Assert.Null(row[CadConversionPlanRules.OffsetFromLevelKey]);
        }

        [Fact]
        public void Resolving_puts_the_storey_elevation_plus_the_offset_on_both_ends_and_removes_the_key()
        {
            JObject row = DuctRow(", 'offset_mm': 2743.2".Replace('\'', '"'));
            Assert.True(CadConversionPlanRules.ResolveOffsetFromLevel(row, 3048.0));
            Assert.Null(row[CadConversionPlanRules.OffsetFromLevelKey]);
            Assert.Equal(5791.2, ((JArray)row["start"])[2].Value<double>(), 3);
            Assert.Equal(5791.2, ((JArray)row["end"])[2].Value<double>(), 3);
        }

        [Fact]
        public void A_run_that_already_falls_keeps_its_fall_and_loses_only_the_key()
        {
            var row = new JObject
            {
                ["kind"] = "pipe",
                ["start"] = new JArray(0, 0, 3000.0),
                ["end"] = new JArray(5000, 0, 2950.0),
                [CadConversionPlanRules.OffsetFromLevelKey] = 2743.2
            };
            Assert.False(CadConversionPlanRules.ResolveOffsetFromLevel(row, 0));
            Assert.Null(row[CadConversionPlanRules.OffsetFromLevelKey]);
            Assert.Equal(3000.0, ((JArray)row["start"])[2].Value<double>(), 6);
            Assert.Equal(2950.0, ((JArray)row["end"])[2].Value<double>(), 6);
        }

        [Fact]
        public void A_row_without_the_key_is_untouched()
        {
            var row = new JObject { ["kind"] = "duct", ["start"] = new JArray(0, 0, 0.0), ["end"] = new JArray(1, 0, 0.0) };
            Assert.False(CadConversionPlanRules.ResolveOffsetFromLevel(row, 3048));
            Assert.Equal(0.0, ((JArray)row["start"])[2].Value<double>(), 6);
        }
    }
}
