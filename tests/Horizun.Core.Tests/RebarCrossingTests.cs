// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// crossing_order (acero 3.0, etapa 1c): the DECLARED order in which bars of different
// rules pass each other. Without it, two sets whose bars pass through each other are
// both refused (bars_intersect) - MEASURED: Revit pushes a crossing bar until it
// touches the other, and says nothing. With it, the plan displaces the lower-priority
// set by the declared offset, in a declared or unambiguously derived direction, and
// the overlap check runs again on the RESULT.
//
// The three cases are the anonymized regression case's (stage 1b): beams crossing at a joint, a mat
// passing through beam bars, and the top and bottom U bars of one beam whose legs
// coincide. The fakes are the shapes ReinforcementResolver.DescribeRebarRow and
// RebarFacts.Describe emit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarCrossingTests
    {
        private const double Tol = 2.0;

        // ------------------------------------------------------------ fakes

        private static JArray Pts(IEnumerable<double[]> p) => new JArray(p.Select(x => (object)new JArray(x[0], x[1], x[2])).ToArray());
        private static double[] P(double x, double y, double z) => new[] { x, y, z };

        /// <summary>A buildable row as ReinforcementResolver.DescribeRebarRow publishes it.</summary>
        private static JObject Row(string ruleId, long host, double modelDia, IList<double[]> curve, double[] normal,
                                   double[] positions, string declared = null, string layout = null, int index = 0)
        {
            var belongs = new JObject { ["declared_rule_id"] = declared ?? ruleId };
            if (layout != null) belongs["layout"] = layout;
            return new JObject
            {
                ["index"] = index,
                ["rule_id"] = ruleId,
                ["host_id"] = host,
                ["will_build"] = true,
                ["required"] = true,
                ["bar_type"] = new JObject { ["id"] = 300, ["name"] = "B" + modelDia, ["nominal_diameter_mm"] = modelDia, ["model_diameter_mm"] = modelDia },
                ["style"] = "standard",
                ["closed"] = false,
                ["curve_mm"] = Pts(curve),
                ["normal"] = new JObject { ["x"] = normal[0], ["y"] = normal[1], ["z"] = normal[2] },
                ["layout"] = new JObject
                {
                    ["rule"] = positions.Length == 1 ? "single" : "fixed_number",
                    ["number_of_bar_positions"] = positions.Length,
                    ["include_first_bar"] = true,
                    ["include_last_bar"] = true,
                    ["bars_on_normal_side"] = true,
                    ["positions_mm"] = new JArray(positions.Select(v => (object)Math.Abs(v)).ToArray()),
                    ["signed_positions_mm"] = new JArray(positions.Select(v => (object)v).ToArray())
                },
                ["belongs_to"] = belongs
            };
        }

        private sealed class Fake
        {
            public JObject Row;
            public double[] HostCentre;
            public List<double[]> Curve;
        }

        private static Fake F(string ruleId, long host, double dia, IList<double[]> curve, double[] normal, double[] positions,
                              double[] hostCentre, string declared = null, string layout = null)
            => new Fake
            {
                Row = Row(ruleId, host, dia, curve, normal, positions, declared, layout),
                HostCentre = hostCentre,
                Curve = curve.Select(p => (double[])p.Clone()).ToList()
            };

        private static List<OverlapMember> Members(IList<Fake> fakes)
        {
            var members = new List<OverlapMember>();
            for (int i = 0; i < fakes.Count; i++)
            {
                OverlapMember m = RebarOverlap.FromPlanRow(fakes[i].Row);
                m.Tag = i;
                m.HostCentreMm = fakes[i].HostCentre;
                members.Add(m);
            }
            return members;
        }

        private static OverlapReport Check(IList<Fake> fakes, StructuralClearances c = null) =>
            RebarOverlap.Check(Members(fakes), c ?? new StructuralClearances(), Tol);

        

        

        // Two beams crossing at a joint, 300 x 500, bottoms at z = 0: BX along X, BY along Y. 16 mm bars,
        // 40 mm cover to the stirrup... the bottom bars at z = 50 and the top bars at z = 450, in BOTH beams.
        private static List<Fake> Joint() => new List<Fake>
        {
            F("BX-bot", 5001, 16, new[] { P(-2000, 0, 50), P(2000, 0, 50) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 0, 250)),
            F("BX-top", 5001, 16, new[] { P(-2000, 0, 450), P(2000, 0, 450) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(0, 0, 250)),
            F("BY-bot", 5002, 16, new[] { P(0, -2000, 50), P(0, 2000, 50) }, new double[] { 1, 0, 0 }, new double[] { 0 }, P(0, 0, 250)),
            F("BY-top", 5002, 16, new[] { P(0, -2000, 450), P(0, 2000, 450) }, new double[] { 1, 0, 0 }, new double[] { 0 }, P(0, 0, 250))
        };

        

        // ---------------------------------------------------------- the joint

        [Fact]
        public void Beams_crossing_at_a_joint_intersect_without_an_order()
        {
            OverlapReport r = Check(Joint());
            Assert.Equal(2, r.Pairs.Count);
            Assert.All(r.Pairs, p => Assert.Equal(RebarOverlap.KindIntersection, p.Kind));
            Assert.All(r.Pairs, p => Assert.Equal(RebarOverlap.RelationCrossing, p.Relation));
        }

        

        

        

        // -------------------------------------------------------------- U legs

        // One beam, x 0..4000, z 0..500: the top U's legs run down from the top bars, the bottom U's up from
        // the bottom bars, in the same plane and at the same station - the anonymized regression case's beam group.
        private static List<Fake> UBars() => new List<Fake>
        {
            F("U-SUP", 5001, 12, new[] { P(50, 0, 100), P(50, 0, 450), P(3950, 0, 450), P(3950, 0, 100) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(2000, 0, 250)),
            F("U-INF", 5001, 12, new[] { P(50, 0, 400), P(50, 0, 50), P(3950, 0, 50), P(3950, 0, 400) }, new double[] { 0, 1, 0 }, new double[] { 0 }, P(2000, 0, 250))
        };

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        private static string SetJsonWithout() => @"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'co', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' } ],
  'reinforcement_rules': [
    { 'id': 'r1', 'host': { 'element_ids': [ 1 ] }, 'bar_type': 'N16', 'allow_new_shape': true, 'style': 'standard',
      'curve_mm': [ [0,0,0], [1000,0,0] ], 'normal': [0,1,0], 'layout': { 'rule': 'single' } } ] }".Replace('\'', '"');

        

        

        

        
    }
}
