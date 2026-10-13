using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HoldEndsTests
    {
        // MEASURED, Revit 2024 (anonymized regression case, Paso 3): a beam U-bar planned 7220.48 mm long came back 7208.35, its
        // end pulled 12 mm onto the cover of the column it runs into. hold_ends measures the drawn vertices
        // against the planned ones, corresponding vertex by vertex.
        private static readonly List<double[]> Plan = new List<double[]>
        {
            new double[] { 0, 0, -200 }, new double[] { 0, 0, 0 }, new double[] { 6800, 0, 0 }, new double[] { 6800, 0, -200 }
        };

        [Fact]
        public void A_bar_drawn_where_it_was_planned_deviates_by_nothing()
        {
            Assert.True(HoldEndsRules.Deviation(Plan, Plan, out double worst, out double total));
            Assert.Equal(0, worst, 9); Assert.Equal(0, total, 9);
        }

        [Fact]
        public void An_end_pulled_back_is_measured_at_that_end()
        {
            var drawn = new List<double[]>
            {
                new double[] { 0, 0, -200 }, new double[] { 0, 0, 0 }, new double[] { 6788, 0, 0 }, new double[] { 6788, 0, -200 }
            };
            Assert.True(HoldEndsRules.Deviation(Plan, drawn, out double worst, out double total));
            Assert.Equal(12, worst, 9); Assert.Equal(24, total, 9);
            Assert.False(HoldEndsRules.Held(worst));
            Assert.True(HoldEndsRules.Held(0.9));
        }

        [Fact]
        public void A_drawn_bar_with_another_vertex_count_is_not_comparable()
        {
            Assert.False(HoldEndsRules.Deviation(Plan, Plan.GetRange(0, 3), out double worst, out _));
            Assert.True(double.IsNaN(worst));
        }

        private static StructuralRequirementSet Load(string root, string rule)
        {
            return StructuralRequirementSet.Load(Newtonsoft.Json.Linq.JObject.Parse((@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter', 'requirement_set': { 'id': 'h', 'version': '1' }ROOT,
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'reinforcement_rules': [ { 'id': 'R1', 'host': { 'element_ids': [ 7 ] }, 'bar_type': 'N5', 'style': 'standard', 'allow_new_shape': true,
    'curve_mm': [[0,0,0],[1000,0,0]], 'normal': [0,1,0], 'layout': { 'rule': 'single' }RULE } ] }").Replace("ROOT", root).Replace("RULE", rule).Replace('\'', '"')));
        }

        [Fact]
        public void hold_ends_is_off_unless_asked_and_a_rule_may_override_the_set()
        {
            StructuralRequirementSet none = Load("", "");
            Assert.True(none.Ok, none.Error);
            Assert.False(none.HoldEnds); Assert.Null(none.RebarRules[0].HoldEnds);
            Assert.False(none.HoldsEnds(none.RebarRules[0]));
            StructuralRequirementSet set = Load(", 'hold_ends': true", "");
            Assert.True(set.HoldsEnds(set.RebarRules[0]));
            StructuralRequirementSet off = Load(", 'hold_ends': true", ", 'hold_ends': false");
            Assert.False(off.HoldsEnds(off.RebarRules[0]));
        }

        [Fact]
        public void hold_ends_that_is_not_a_boolean_is_refused_with_its_path()
        {
            StructuralRequirementSet bad = Load(", 'hold_ends': 'yes'", "");
            Assert.False(bad.Ok);
            Assert.Contains("hold_ends", bad.Error);
            StructuralRequirementSet bad2 = Load("", ", 'hold_ends': 1");
            Assert.False(bad2.Ok);
        }
}
}
