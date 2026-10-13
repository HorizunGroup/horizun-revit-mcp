// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// overlapping_bar (acero 3.0): bars of DIFFERENT rules measured against each other,
// in the plan (on the declared centrelines, before anything is written) and in the
// audit (on the bars the model carries). MEASURED live, Revit 2024 and 2026: a
// wall's horizontals laid through a boundary element's stirrups were pushed by
// Revit until they touched them (the array came out 12.5 mm short). Revit moves a
// bar that crosses another and says nothing, so the plan has to say it first.
//
// The fakes below are the shapes ReinforcementResolver.DescribeRebarRow and
// RebarFacts.Describe emit, never a shape invented for the test.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Core.Tests
{
    public class RebarOverlapTests
    {
        private readonly ITestOutputHelper _out;
        public RebarOverlapTests(ITestOutputHelper output) { _out = output; }

        private const double Tol = 2.0;

        // ------------------------------------------------------------ fakes

        private static JArray Pts(params double[][] p) => new JArray(p.Select(x => (object)new JArray(x[0], x[1], x[2])).ToArray());
        private static double[] P(double x, double y, double z) => new[] { x, y, z };

        /// <summary>A row as ReinforcementResolver.DescribeRebarRow publishes a buildable one.</summary>
        private static JObject Row(string ruleId, long host, double modelDia, double[][] curve, double[] normal,
                                   double[] positions, bool closed = false, JObject belongsTo = null,
                                   bool first = true, bool last = true, int index = 0)
        {
            return new JObject
            {
                ["index"] = index,
                ["rule_id"] = ruleId,
                ["host_id"] = host,
                ["will_build"] = true,
                ["required"] = true,
                ["bar_type"] = new JObject { ["id"] = 300, ["name"] = "B" + modelDia, ["nominal_diameter_mm"] = modelDia, ["model_diameter_mm"] = modelDia },
                ["style"] = "standard",
                ["closed"] = closed,
                ["curve_mm"] = Pts(curve),
                ["normal"] = new JObject { ["x"] = normal[0], ["y"] = normal[1], ["z"] = normal[2] },
                ["layout"] = new JObject
                {
                    ["rule"] = positions.Length == 1 ? "single" : "fixed_number",
                    ["number_of_bar_positions"] = positions.Length,
                    ["include_first_bar"] = first,
                    ["include_last_bar"] = last,
                    ["bars_on_normal_side"] = true,
                    ["positions_mm"] = new JArray(positions.Select(v => (object)Math.Abs(v)).ToArray()),
                    ["signed_positions_mm"] = new JArray(positions.Select(v => (object)v).ToArray())
                },
                ["terminations"] = new JObject
                {
                    ["start"] = new JObject { ["hook_type_id"] = -1, ["has_hook"] = false, ["orientation"] = null },
                    ["end"] = new JObject { ["hook_type_id"] = -1, ["has_hook"] = false, ["orientation"] = null }
                },
                ["belongs_to"] = belongsTo ?? new JObject { ["declared_rule_id"] = ruleId }
            };
        }

        /// <summary>A row built from a rule the expansions produced, the way the resolver describes it.</summary>
        private static JObject RowOf(StructuralRebarRule r, long host, double modelDia, int index = 0)
        {
            List<double[]> path = RebarArcs.Path(r.CurvesMm, r.Closed, r.ArcThrough, RebarArcs.DefaultChordMm);
            var req = r.Layout;
            req.BarDiameterMm = modelDia;
            RebarLayoutPlan plan = RebarLayoutRules.Resolve(req);
            Assert.True(plan.Ok, r.Id + ": " + plan.Error);
            double[] pos = plan.PositionsMm.Select(v => r.BarsOnNormalSide ? v : -v).ToArray();
            JObject row = Row(r.Id, host, modelDia, path.ToArray(), r.NormalMm, pos, r.Closed, RebarOverlap.Identity(r),
                              plan.IncludeFirstBar, plan.IncludeLastBar, index);
            row["style"] = r.Style;
            return row;
        }

        /// <summary>A bar as RebarFacts.Describe(doc, bar, includePositions: true) publishes it.</summary>
        private static JObject Observed(long id, double modelDia, double[][] drawn, double[][] asDeclared,
                                        double[][] offsets, bool hookStart = false, bool hookEnd = false)
        {
            var positions = new JArray();
            for (int i = 0; i < offsets.Length; i++)
                positions.Add(new JObject
                {
                    ["index"] = i,
                    ["exists"] = true,
                    ["offset_from_first_bar_mm"] = new JObject { ["x"] = offsets[i][0], ["y"] = offsets[i][1], ["z"] = offsets[i][2] }
                });
            var o = new JObject
            {
                ["id"] = id,
                ["host"] = new JObject { ["id"] = 5001, ["resolved"] = true },
                ["bar_type"] = new JObject { ["resolved"] = true, ["id"] = 300, ["nominal_diameter_mm"] = modelDia, ["model_diameter_mm"] = modelDia },
                ["terminations"] = new JArray(
                    new JObject { ["end"] = 0, ["hook_readable"] = true, ["hook_type_id"] = hookStart ? 700 : -1, ["has_hook"] = hookStart },
                    new JObject { ["end"] = 1, ["hook_readable"] = true, ["hook_type_id"] = hookEnd ? 700 : -1, ["has_hook"] = hookEnd }),
                ["geometry"] = new JObject { ["centreline"] = "read", ["centreline_points_mm"] = Pts(drawn) },
                ["bar_positions"] = positions
            };
            if (asDeclared != null) o["geometry"]["centreline_points_as_declared_mm"] = Pts(asDeclared);
            return o;
        }

        private static StructuralClearances Clear(double? between = null, double? crossing = null, params StructuralClearancePair[] pairs)
        {
            var c = new StructuralClearances { BetweenRulesMm = between };
            if (crossing.HasValue) { c.CrossingMm = crossing.Value; c.CrossingDeclared = true; }
            c.Pairs.AddRange(pairs);
            return c;
        }

        private static OverlapReport Plan(StructuralClearances c, params JObject[] rows)
        {
            var members = new List<OverlapMember>();
            for (int i = 0; i < rows.Length; i++)
            {
                OverlapMember m = RebarOverlap.FromPlanRow(rows[i]);
                m.Tag = i;
                members.Add(m);
            }
            return RebarOverlap.Check(members, c ?? new StructuralClearances(), Tol);
        }

        // ------------------------------------------------------- the geometry

        [Fact]
        public void Segment_distance_is_exact_for_crossing_parallel_skew_and_end_cases()
        {
            double[] c1, c2;
            // Two lines crossing at right angles, 30 apart in z: the common perpendicular.
            Assert.Equal(30.0, RebarOverlap.SegmentDistance(P(-100, 0, 0), P(100, 0, 0), P(0, -100, 30), P(0, 100, 30), out c1, out c2), 9);
            Assert.Equal(new[] { 0.0, 0, 0 }, c1); Assert.Equal(new[] { 0.0, 0, 30 }, c2);
            // Parallel, overlapping along their length: 25 apart.
            Assert.Equal(25.0, RebarOverlap.SegmentDistance(P(0, 0, 0), P(1000, 0, 0), P(500, 25, 0), P(2000, 25, 0), out c1, out c2), 9);
            // Parallel and end to end with a gap of 40: end to end.
            Assert.Equal(40.0, RebarOverlap.SegmentDistance(P(0, 0, 0), P(1000, 0, 0), P(1040, 0, 0), P(2000, 0, 0), out c1, out c2), 9);
            // Skew, closest point at an end of one of them (the perpendicular foot lies outside).
            Assert.Equal(Math.Sqrt(50 * 50 + 10 * 10), RebarOverlap.SegmentDistance(P(0, 0, 0), P(100, 0, 0), P(150, -100, 10), P(150, 100, 10), out c1, out c2), 9);
            // A degenerate segment is a point.
            Assert.Equal(5.0, RebarOverlap.SegmentDistance(P(10, 5, 0), P(10, 5, 0), P(0, 0, 0), P(100, 0, 0), out c1, out c2), 9);
        }

        // ---------------------------------------------------- what is a finding

        [Fact]
        public void Two_rules_whose_bars_pass_through_each_other_are_an_intersection_with_both_ids_the_clear_and_the_point()
        {
            // A beam bar (16) and a column bar (20) crossing at the same point.
            JObject beam = Row("beam", 11, 16, new[] { P(-1000, 0, 3000), P(1000, 0, 3000) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            JObject col = Row("col", 12, 20, new[] { P(0, 0, 0), P(0, 0, 4000) }, new[] { 1.0, 0, 0 }, new[] { 0.0 });
            OverlapReport r = Plan(null, beam, col);
            OverlapPair p = Assert.Single(r.Pairs);
            Assert.Equal(RebarOverlap.KindIntersection, p.Kind);
            Assert.Equal(RebarOverlap.RelationCrossing, p.Relation);
            Assert.Equal(-18.0, p.ClearMm, 6);   // 0 apart, radii 8 + 10
            Assert.Equal(new[] { 0.0, 0, 3000 }, p.PointMm.Select(v => Math.Round(v, 6)).ToArray());
            Assert.Equal(new[] { "beam", "col" }, new[] { p.A.RuleId, p.B.RuleId });
            Assert.Equal(RebarOverlap.CheckedIntersectionsOnly, r.Checked);
            Assert.Equal(RebarOverlap.CodeIntersect, RebarOverlap.RefusalCode(p));
        }

        [Fact]
        public void Without_a_declared_clearance_bars_close_together_are_not_a_finding_only_intersections_are()
        {
            // Alongside, 10 mm clear: no clearance declared, so nothing is wrong with it.
            JObject a = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0 });
            JObject b = Row("b", 11, 16, new[] { P(0, 26, 0), P(3000, 26, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0 });
            OverlapReport r = Plan(null, a, b);
            Assert.Empty(r.Pairs);
            Assert.Equal("intersections_only", r.Checked);
            Assert.Contains("only", (string)r.ToJson()["checked_means"]);
        }

        [Fact]
        public void A_declared_clearance_holds_bars_running_alongside_and_names_the_shortfall()
        {
            JObject a = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0 });
            JObject b = Row("b", 11, 16, new[] { P(0, 36, 0), P(3000, 36, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0 });
            OverlapReport r = Plan(Clear(25), a, b);
            OverlapPair p = Assert.Single(r.Pairs);
            Assert.Equal(RebarOverlap.KindTooClose, p.Kind);
            Assert.Equal(RebarOverlap.RelationAlongside, p.Relation);
            Assert.Equal(20.0, p.ClearMm, 6);
            Assert.Equal(25.0, p.RequiredClearMm, 6);
            Assert.Equal(RebarOverlap.CodeTooClose, RebarOverlap.RefusalCode(p));
            Assert.Equal("declared_clearance", r.Checked);
            // Inside the length tolerance it is not a difference: 24 clear against 25 with 2 mm.
            JObject c = Row("c", 11, 16, new[] { P(0, 40, 0), P(3000, 40, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0 });
            Assert.Empty(Plan(Clear(25), a, c).Pairs);
        }

        [Fact]
        public void Bars_that_cross_may_touch_unless_a_crossing_clearance_is_declared_but_never_interpenetrate()
        {
            // A mat's two layers stacked in contact (MatRules lays them one radius + one radius apart).
            JObject x = Row("mat-x", 11, 12, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0, 150, 300 });
            JObject y = Row("mat-y", 11, 12, new[] { P(100, -100, 12), P(100, 2000, 12) }, new[] { 1.0, 0, 0 }, new[] { 0.0, 150 });
            Assert.Empty(Plan(Clear(25), x, y).Pairs);
            // Asked for 5 mm at crossings, the same contact is a finding.
            OverlapReport r = Plan(Clear(25, 5), x, y);
            Assert.NotEmpty(r.Pairs);
            Assert.All(r.Pairs, p => Assert.Equal(RebarOverlap.RelationCrossing, p.Relation));
            // One layer laid 6 mm too high: the bars cross INSIDE each other, whatever is declared.
            JObject y2 = Row("mat-y", 11, 12, new[] { P(100, -100, 6), P(100, 2000, 6) }, new[] { 1.0, 0, 0 }, new[] { 0.0, 150 });
            OverlapPair q = Plan(null, x, y2).Pairs.Single();
            Assert.Equal(RebarOverlap.KindIntersection, q.Kind);
            Assert.Equal(-6.0, q.ClearMm, 6);
            // Every bar pair of the two sets that intersects is counted under the one pair of sets.
            Assert.Equal(6, q.BarPairs);
        }

        [Fact]
        public void A_pair_declared_by_rule_id_replaces_the_general_clearance_for_bars_alongside()
        {
            JObject starter = Row("dowels", 11, 16, new[] { P(0, 0, -500), P(0, 0, 900) }, new[] { 1.0, 0, 0 }, new[] { 0.0 });
            JObject column = Row("C1#h11#perimeter", 11, 16, new[] { P(0, 16, 0), P(0, 16, 3000) }, new[] { 1.0, 0, 0 }, new[] { 0.0 },
                                 belongsTo: new JObject { ["declared_rule_id"] = "C1", ["cage"] = new JObject { ["id"] = "C1#h11", ["role"] = "longitudinal" } });
            // In contact alongside: under a 25 mm clearance that is a finding...
            Assert.Single(Plan(Clear(25), starter, column).Pairs);
            // ...and a declared contact splice between THESE two declared rules says it is meant.
            var pair = new StructuralClearancePair { RuleA = "C1", RuleB = "dowels", MinClearMm = 0 };
            Assert.Empty(Plan(Clear(25, null, pair), starter, column).Pairs);
        }

        [Fact]
        public void Bars_of_one_set_are_not_compared_with_each_other_but_one_rule_in_two_hosts_is()
        {
            // One set whose positions sit 5 mm apart: the layout's business, not this check's.
            JObject crowded = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0, 5, 10 });
            Assert.Empty(Plan(Clear(25), crowded).Pairs);
            // The same rule built in two beams that meet: different sets, compared.
            JObject inB1 = Row("beam", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            JObject inB2 = Row("beam", 12, 16, new[] { P(2900, -500, 0), P(2900, 500, 0) }, new[] { 1.0, 0, 0 }, new[] { 0.0 });
            Assert.Equal(RebarOverlap.KindIntersection, Plan(null, inB1, inB2).Pairs.Single().Kind);
        }

        // ----------------------------------------------------------- excuses

        private static StructuralRequirementSet BeamSet(string layers, string extra = "")
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'ov', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' }, { 'id': 'N10', 'type_name': '10M' } ],
  'hook_types': [ { 'id': 'H135', 'type_name': 'Stirrup/Tie - 135 deg.' } ],
  'section_rules': [ { 'id': 'C1', 'host': { 'element_ids': [ 7 ] }, 'cover': { 'distance_mm': 40 },
    'layers': " + layers + extra + @" } ] }".Replace('\'', '"')));
            Assert.True(s.Ok, s.Error);
            return s;
        }

        private const string ColumnStirrups = @", 'stirrups': { 'bar_type': 'N10', 'allow_new_shape': true,
      'start': { 'hook_type': 'H135' }, 'end': { 'hook_type': 'H135' },
      'zones': [ { 'name': 'all', 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 150 } } ] }";

        private static SectionHostFrame Column() => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 0, 0, 1.0 }, V = new[] { 1.0, 0, 0 }, W = new[] { 0, 1.0, 0 },
            UMin = 0, UMax = 3000, VMin = -300, VMax = 300, WMin = -200, WMax = 200
        };

        /// <summary>The rows a section rule produces, through SectionRules and StirrupZoneRules, as the resolver chains them.</summary>
        private static List<JObject> CageRows(StructuralRequirementSet s, SectionHostFrame f)
        {
            Func<string, double> dia = id => id == "N10" ? 10 : 16;
            SectionResult r = SectionRules.ExpandAll(s.SectionRules.Single(), f, dia, null, null, out List<StructuralRebarRule> bars,
                                                     out List<StructuralStirrupZoneRule> zones);
            Assert.True(r.Ok, r.Why);
            var rules = new List<StructuralRebarRule>(bars);
            foreach (StructuralStirrupZoneRule z in zones)
            {
                StirrupZoneResult zr = StirrupZoneRules.Expand(z, z.SpanMm.Value, dia(z.BarTypeId), out List<StructuralRebarRule> zb);
                Assert.True(zr.Ok, zr.Why);
                rules.AddRange(zb);
            }
            return rules.Select((b, i) => RowOf(b, f.ElementId, dia(b.BarTypeId), i)).ToList();
        }

        [Fact]
        public void The_expansions_say_which_declared_rule_which_cage_and_which_layout_a_bar_belongs_to()
        {
            StructuralRequirementSet s = BeamSet(@"[ { 'face': 'perimeter', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true },
      { 'name': 'sides', 'face': 'left', 'count': 2, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true } ]",
                ColumnStirrups + @", 'ties': [ { 'name': 'x', 'bar_type': 'N10', 'layer': 'sides', 'allow_new_shape': true,
                'start': { 'hook_type': 'H135' }, 'end': { 'hook_type': 'H135' } } ]");
            List<JObject> rows = CageRows(s, Column());
            foreach (JObject row in rows)
            {
                JObject b = (JObject)row["belongs_to"];
                Assert.Equal("C1", (string)b["declared_rule_id"]);
                Assert.Equal("C1#h7", (string)b["cage"]["id"]);
            }
            Assert.Contains(rows, r => (string)r["belongs_to"]["cage"]["role"] == RebarOverlap.RoleLongitudinal);
            JObject stirrup = rows.Single(r => ((string)r["rule_id"]).Contains("#stirrups#"));
            Assert.Equal(RebarOverlap.RoleStirrup, (string)stirrup["belongs_to"]["cage"]["role"]);
            Assert.Equal("C1#h7#stirrups", (string)stirrup["belongs_to"]["layout"]);
            Assert.All(rows.Where(r => ((string)r["rule_id"]).Contains("#tie-")),
                       r => Assert.Equal(RebarOverlap.RoleTie, (string)r["belongs_to"]["cage"]["role"]));

            // A mat's components name the mat and their own component.
            var mat = new StructuralRebarRule { Id = "M1#top-x", OriginRuleId = "M1", LayoutGroupId = "M1#top-x" };
            Assert.Equal("M1", (string)RebarOverlap.Identity(mat)["declared_rule_id"]);
            Assert.Null(RebarOverlap.Identity(mat)["cage"]);
            // A plain rule belongs to itself.
            Assert.Equal("r1", (string)RebarOverlap.Identity(new StructuralRebarRule { Id = "r1" })["declared_rule_id"]);
        }

        [Fact]
        public void A_cage_s_stirrups_and_ties_touching_and_enclosing_its_own_bars_are_its_design_and_are_counted_as_excused()
        {
            StructuralRequirementSet s = BeamSet(@"[ { 'face': 'perimeter', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true },
      { 'name': 'sides', 'face': 'left', 'count': 2, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true } ]",
                ColumnStirrups + @", 'ties': [ { 'name': 'x', 'bar_type': 'N10', 'layer': 'sides', 'allow_new_shape': true,
                'start': { 'hook_type': 'H135' }, 'end': { 'hook_type': 'H135' } } ]");
            List<JObject> rows = CageRows(s, Column());
            OverlapReport r = Plan(Clear(25), rows.ToArray());
            Assert.Empty(r.Pairs);
            // The stirrups touch the bars; the ties are declared through the side bars' centres.
            Assert.True(r.Excused[RebarOverlap.ExcuseCage] > 0);

            // CONTROL: the same rows with the cage identity taken away are findings - it is the
            // excuse that passes them, not the geometry.
            JObject[] anonymous = rows.Select(x => { var c = (JObject)x.DeepClone(); c["belongs_to"] = new JObject { ["declared_rule_id"] = (string)x["rule_id"] }; return c; }).ToArray();
            OverlapReport bare = Plan(Clear(25), anonymous);
            Assert.Contains(bare.Pairs, p => p.Kind == RebarOverlap.KindIntersection);   // the ties through the bars
        }

        [Fact]
        public void A_cage_does_not_excuse_two_of_its_own_longitudinal_bars_nor_another_cage()
        {
            // Two layers of ONE section rule laid on top of each other: no stirrup involved, a finding.
            JObject a = Row("C1#h7#top", 7, 16, new[] { P(0, 0, 0), P(0, 0, 3000) }, new[] { 1.0, 0, 0 }, new[] { 0.0 },
                            belongsTo: new JObject { ["declared_rule_id"] = "C1", ["cage"] = new JObject { ["id"] = "C1#h7", ["role"] = "longitudinal" } });
            JObject b = Row("C1#h7#left", 7, 16, new[] { P(10, 0, 0), P(10, 0, 3000) }, new[] { 0, 1.0, 0 }, new[] { 0.0 },
                            belongsTo: new JObject { ["declared_rule_id"] = "C1", ["cage"] = new JObject { ["id"] = "C1#h7", ["role"] = "longitudinal" } });
            Assert.Equal(RebarOverlap.KindIntersection, Plan(null, a, b).Pairs.Single().Kind);

            // A BEAM-COLUMN JOINT: the beam cage's bar passes through the column cage's stirrup.
            StructuralRequirementSet s = BeamSet(@"[ { 'face': 'perimeter', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true } ]", ColumnStirrups);
            List<JObject> column = CageRows(s, Column());
            JObject stirrup = column.First(r => ((string)r["rule_id"]).Contains("#stirrups#"));
            double z = stirrup["curve_mm"][0][2].Value<double>();
            // The beam's top bar at the stirrup's own level, running along x through the stirrup's legs.
            JObject beamBar = Row("B1#h9#top", 9, 16, new[] { P(-2000, 0, z), P(2000, 0, z) }, new[] { 0, 1.0, 0 }, new[] { 0.0 },
                                  belongsTo: new JObject { ["declared_rule_id"] = "B1", ["cage"] = new JObject { ["id"] = "B1#h9", ["role"] = "longitudinal" } });
            OverlapReport r2 = Plan(null, column.Concat(new[] { beamBar }).ToArray());
            OverlapPair hit = Assert.Single(r2.Pairs);
            Assert.Equal(RebarOverlap.KindIntersection, hit.Kind);
            Assert.Contains("B1#h9#top", new[] { hit.A.RuleId, hit.B.RuleId });
            Assert.Contains(new[] { hit.A.RuleId, hit.B.RuleId }, id => id.Contains("#stirrups#"));
        }

        [Fact]
        public void A_declared_lap_in_contact_is_excused_and_without_its_declaration_the_same_pieces_are_a_finding()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'lap', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' } ],
  'section_rules': [ { 'id': 'V1', 'host': { 'element_ids': [ 7 ] }, 'cover': { 'distance_mm': 40 },
    'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N16', 'extend': 'host_length', 'allow_new_shape': true,
                  'laps': [ { 'at_mm': 3000, 'length_mm': 800 } ] } ] } ] }".Replace('\'', '"')));
            Assert.True(s.Ok, s.Error);
            var beam = new SectionHostFrame
            {
                ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
                UMin = 0, UMax = 6000, VMin = -150, VMax = 150, WMin = -500, WMax = 0
            };
            SectionResult res = SectionRules.Expand(s.SectionRules.Single(), beam, id => 16, null, out List<StructuralRebarRule> bars, out _);
            Assert.True(res.Ok, res.Why);
            JObject[] rows = bars.Select((b, i) => RowOf(b, 7, 16, i)).ToArray();
            Assert.Equal("V1#h7#top#p2", (string)rows[0]["belongs_to"]["lap_next_rule_id"]);
            OverlapReport r = Plan(Clear(25), rows);
            Assert.Empty(r.Pairs);
            Assert.Equal(1, r.Excused[RebarOverlap.ExcuseLap]);

            foreach (JObject row in rows) ((JObject)row["belongs_to"]).Remove("lap_next_rule_id");
            OverlapPair p = Assert.Single(Plan(Clear(25), rows).Pairs);
            Assert.Equal(RebarOverlap.KindTooClose, p.Kind);          // one diameter apart, in contact
            Assert.Equal(0.0, p.ClearMm, 6);
        }

        [Fact]
        public void A_declared_coupler_joint_end_to_end_is_excused_and_undeclared_it_is_an_intersection()
        {
            var coupled = new JObject { ["declared_rule_id"] = "C1", ["coupler_next_rule_id"] = "C1#h7#v#p2" };
            JObject p1 = Row("C1#h7#v#p1", 7, 20, new[] { P(0, 0, 1500), P(0, 0, 0) }, new[] { 1.0, 0, 0 }, new[] { 0.0 }, belongsTo: coupled);
            JObject p2 = Row("C1#h7#v#p2", 7, 20, new[] { P(0, 0, 1500), P(0, 0, 3000) }, new[] { 1.0, 0, 0 }, new[] { 0.0 },
                             belongsTo: new JObject { ["declared_rule_id"] = "C1" });
            OverlapReport r = Plan(null, p1, p2);
            Assert.Empty(r.Pairs);
            Assert.Equal(1, r.Excused[RebarOverlap.ExcuseCoupler]);
            ((JObject)p1["belongs_to"]).Remove("coupler_next_rule_id");
            Assert.Equal(RebarOverlap.KindIntersection, Plan(null, p1, p2).Pairs.Single().Kind);
        }

        // ------------------------------------------------------------- audit

        private static OverlapReport Audit(StructuralClearances c, params (JObject expected, JObject observed)[] bars)
        {
            var members = bars.Select(b => RebarOverlap.FromObserved(b.expected, b.observed)).ToList();
            return RebarOverlap.Check(members, c ?? new StructuralClearances(), Tol);
        }

        [Fact]
        public void The_audit_measures_the_bars_the_model_carries_at_every_position_and_reports_overlapping_bar()
        {
            // THE MEASURED CASE: a wall mat's horizontals laid through a boundary element's stirrups.
            // The boundary stirrup is a loop in plan at z = 1000; one mat horizontal of the set
            // (its third position, offset 300 up from 700) runs inside the stirrup's long leg.
            JObject stirrupRow = Row("WB1#h5001#stirrups#all", 5001, 10, new[] { P(0, 0, 1000), P(600, 0, 1000), P(600, 150, 1000), P(0, 150, 1000) },
                                     new[] { 0, 0, 1.0 }, new[] { 0.0 }, closed: true,
                                     belongsTo: new JObject { ["declared_rule_id"] = "WB1", ["layout"] = "WB1#h5001#stirrups", ["cage"] = new JObject { ["id"] = "WB1#h5001", ["role"] = "stirrup" } });
            JObject stirrup = Observed(9101, 10, new[] { P(0, 0, 1000), P(600, 0, 1000), P(600, 150, 1000), P(0, 150, 1000), P(0, 0, 1000) }, null,
                                       new[] { P(0, 0, 0) });
            JObject matRow = Row("M1#h", 5001, 12, new[] { P(-500, 4, 700), P(3000, 4, 700) }, new[] { 0, 0, 1.0 }, new[] { 0.0, 150, 300 },
                                 belongsTo: new JObject { ["declared_rule_id"] = "M1", ["layout"] = "M1#h" });
            JObject mat = Observed(9102, 12, new[] { P(-500, 4, 700), P(3000, 4, 700) }, new[] { P(-500, 4, 700), P(3000, 4, 700) },
                                   new[] { P(0, 0, 0), P(0, 0, 150), P(0, 0, 300) });
            OverlapReport r = Audit(null, (stirrupRow, stirrup), (matRow, mat));
            OverlapPair p = Assert.Single(r.Pairs);
            Assert.Equal(RebarOverlap.KindIntersection, p.Kind);
            // It runs 4 mm off the long leg and THROUGH both short legs: 0 apart there, radii 5 + 6.
            Assert.Equal(-11.0, p.ClearMm, 6);
            Assert.Equal(1, p.BarPairs);

            JObject f = Assert.Single(RebarOverlap.AuditFindings(r).OfType<JObject>());
            Assert.Equal(RebarFinding.OverlappingBar, (string)f["code"]);
            Assert.Equal(RebarSeverity.Error, (string)f["severity"]);
            Assert.Equal(new[] { "M1#h", "WB1#h5001#stirrups#all" }, new[] { (string)f["rule_id"], (string)f["other_rule_id"] }.OrderBy(x => x).ToArray());
            Assert.Equal(new long[] { 9101, 9102 }, new[] { (long)f["rebar_id"], (long)f["other_rebar_id"] }.OrderBy(x => x).ToArray());
            Assert.Equal(-11.0, (double)f["clear_mm"], 6);
            Assert.Equal(1000.0, (double)f["point_mm"][2], 6);
            Assert.Contains(RebarFinding.OverlappingBar, RebarFinding.All);
        }

        [Fact]
        public void A_hook_is_not_measured_against_other_bars_and_the_audit_says_how_many_hooks_it_left_out()
        {
            // A crosstie whose 135 degree hook tip, as drawn, runs through a neighbouring bar of another rule.
            double[][] declared = { P(0, 0, 500), P(400, 0, 500) };
            double[][] drawn = { P(-60, 60, 500), P(-20, 20, 500), P(0, 0, 500), P(400, 0, 500) };   // the hook first, then the body
            JObject tieRow = Row("tie", 7, 10, declared, new[] { 0, 0, 1.0 }, new[] { 0.0 });
            JObject tie = Observed(1, 10, drawn, declared, new[] { P(0, 0, 0) }, hookStart: true);
            // The other bar stands vertical exactly where the hook passes.
            JObject otherRow = Row("other", 8, 16, new[] { P(-40, 40, 0), P(-40, 40, 3000) }, new[] { 1.0, 0, 0 }, new[] { 0.0 });
            JObject other = Observed(2, 16, new[] { P(-40, 40, 0), P(-40, 40, 3000) }, null, new[] { P(0, 0, 0) });
            OverlapReport r = Audit(null, (tieRow, tie), (otherRow, other));
            Assert.Empty(r.Pairs);
            Assert.Equal(1, r.Excused[RebarOverlap.ExcuseHook]);

            // CONTROL: the same polyline without a hook at that end is the bar's body, and it is measured.
            JObject straight = Observed(1, 10, drawn, drawn, new[] { P(0, 0, 0) });
            Assert.Equal(RebarOverlap.KindIntersection, Audit(null, (tieRow, straight), (otherRow, other)).Pairs.Single().Kind);
        }

        [Fact]
        public void A_bar_the_audit_could_not_place_is_unknown_never_a_pass()
        {
            JObject row = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            JObject noPositions = Observed(1, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, null, new double[0][]);
            noPositions.Remove("bar_positions");
            JObject hookNoDeclared = Observed(2, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, null, new[] { P(0, 0, 0) }, hookEnd: true);
            OverlapReport r = Audit(null, (row, noPositions), (Row("b", 12, 16, new[] { P(0, 0, 0), P(1, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 }), hookNoDeclared));
            Assert.Equal(2, r.Unreadable.Count);
            JArray f = RebarOverlap.AuditFindings(r);
            Assert.Equal(2, f.Count);
            Assert.All(f.OfType<JObject>(), o =>
            {
                Assert.Equal(RebarSeverity.Unknown, (string)o["severity"]);
                Assert.Equal(RebarFinding.OverlappingBar, (string)o["about"]);
            });
        }

        [Fact]
        public void A_suppressed_end_bar_is_not_compared_in_the_plan_nor_a_position_the_model_says_does_not_exist()
        {
            JObject a = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0, 150, 300 }, first: false);
            // b stands vertical through a's FIRST position, which the layout suppresses.
            JObject b = Row("b", 11, 16, new[] { P(1000, 0, -500), P(1000, 0, 500) }, new[] { 1.0, 0, 0 }, new[] { 0.0 });
            Assert.Empty(Plan(null, a, b).Pairs);
            a["layout"]["include_first_bar"] = true;
            Assert.Single(Plan(null, a, b).Pairs);

            JObject obs = Observed(1, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, null, new[] { P(0, 0, 0), P(0, 150, 0) });
            ((JObject)obs["bar_positions"][0])["exists"] = false;
            JObject obsB = Observed(2, 16, new[] { P(1000, 0, -500), P(1000, 0, 500) }, null, new[] { P(0, 0, 0) });
            Assert.Empty(Audit(null, (a, obs), (b, obsB)).Pairs);
        }

        // ---------------------------------------------------------- plan wiring

        [Fact]
        public void The_plan_refuses_both_sets_of_a_clash_naming_the_other_rule_the_clear_and_the_point()
        {
            JObject beam = Row("beam", 11, 16, new[] { P(-1000, 0, 3000), P(1000, 0, 3000) }, new[] { 0, 1.0, 0 }, new[] { 0.0 }, index: 0);
            JObject col = Row("col", 12, 20, new[] { P(0, 0, 0), P(0, 0, 4000) }, new[] { 1.0, 0, 0 }, new[] { 0.0 }, index: 1);
            JObject far = Row("far", 13, 20, new[] { P(5000, 0, 0), P(5000, 0, 4000) }, new[] { 1.0, 0, 0 }, new[] { 0.0 }, index: 2);
            OverlapReport r = Plan(null, beam, col, far);
            List<OverlapRefusal> refused = RebarOverlap.Refusals(r);
            Assert.Equal(new[] { 0, 1 }, refused.Select(x => x.Tag).OrderBy(x => x).ToArray());
            OverlapRefusal b = refused.Single(x => x.Tag == 0);
            Assert.Equal(RebarOverlap.CodeIntersect, b.Code);
            Assert.Contains("col", b.Why);
            Assert.Contains("-18", b.Why);
            Assert.Contains("Revit", b.Why);
            Assert.Equal("col", (string)b.Overlaps[0]["other_rule_id"]);
            JObject j = r.ToJson();
            Assert.Equal(3, (int)j["sets_compared"]);
            Assert.Single((JArray)j["overlaps"]);
            Assert.NotNull(j["elapsed_ms"]);
            Assert.Equal(RebarOverlap.AlongsideDegrees, (double)j["alongside_within_degrees"]);
        }

        // ------------------------------------------------------------ the schema

        private static StructuralRequirementSet SetWith(string clearances) => StructuralRequirementSet.Load(JObject.Parse((@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cl', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' } ],
  'clearances': " + clearances + @",
  'reinforcement_rules': [
    { 'id': 'r1', 'host': { 'element_ids': [ 1 ] }, 'bar_type': 'N16', 'allow_new_shape': true, 'style': 'standard',
      'curve_mm': [ [0,0,0], [1000,0,0] ], 'normal': [0,1,0], 'layout': { 'rule': 'single' } },
    { 'id': 'r2', 'host': { 'element_ids': [ 2 ] }, 'bar_type': 'N16', 'allow_new_shape': true, 'style': 'standard',
      'curve_mm': [ [0,0,0], [1000,0,0] ], 'normal': [0,1,0], 'layout': { 'rule': 'single' } } ] }").Replace('\'', '"')));

        [Fact]
        public void Clearances_are_declared_numbers_and_a_set_without_them_checks_intersections_only()
        {
            StructuralRequirementSet s = SetWith("{ 'between_rules_mm': 25, 'crossing_mm': 0, 'pairs': [ { 'rules': [ 'r1', 'r2' ], 'min_clear_mm': 40 } ] }");
            Assert.True(s.Ok, s.Error);
            Assert.Equal(25.0, s.Clearances.BetweenRulesMm.Value, 9);
            Assert.True(s.Clearances.CrossingDeclared);
            Assert.Equal(40.0, s.Clearances.Pairs.Single().MinClearMm, 9);
            Assert.True(s.Clearances.Declared);

            StructuralRequirementSet none = SetWith("{ }");
            Assert.True(none.Ok, none.Error);
            Assert.False(none.Clearances.Declared);
            Assert.Null(none.Clearances.BetweenRulesMm);
        }

        [Theory]
        [InlineData("{ 'between_rules_mm': -1 }", "value_not_in_vocabulary")]
        [InlineData("{ 'between_rules_mm': 'NaN' }", "value_is_not_a_finite_number")]
        [InlineData("{ 'between_rules_mn': 25 }", "unknown_key")]
        [InlineData("{ 'pairs': [ { 'rules': [ 'r1', 'nope' ], 'min_clear_mm': 0 } ] }", "reference_not_declared")]
        [InlineData("{ 'pairs': [ { 'rules': [ 'r1' ], 'min_clear_mm': 0 } ] }", "value_not_in_vocabulary")]
        [InlineData("{ 'pairs': [ { 'rules': [ 'r1', 'r2' ] } ] }", "required_field_missing")]
        [InlineData("{ 'between_rules_mm': 900 }", "value_not_in_vocabulary")]
        public void A_clearance_that_cannot_be_read_refuses_the_set_by_name(string clearances, string code)
        {
            StructuralRequirementSet s = SetWith(clearances);
            Assert.False(s.Ok);
            Assert.Equal(code, s.Code);
            Assert.StartsWith("clearances", s.Path ?? "clearances");
        }

        [Fact]
        public void A_piece_of_a_chunked_set_keeps_only_the_pairs_whose_rules_it_still_carries()
        {
            JObject set = JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'ch', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'clearances': { 'between_rules_mm': 25, 'pairs': [ { 'rules': [ 'V1', 'V2' ], 'min_clear_mm': 0 }, { 'rules': [ 'V1', 'V1' ], 'min_clear_mm': 10 } ] },
  'section_rules': [
    { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' } ] },
    { 'id': 'V2', 'host': { 'mark': 'V-2' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' } ] } ] }".Replace('\'', '"'));
            var hosts = new List<RequirementSetChunks.Host>
            {
                new RequirementSetChunks.Host { ElementId = 11, Category = "OST_StructuralFraming", TypeName = "B", Mark = "V-1", Level = "L1" },
                new RequirementSetChunks.Host { ElementId = 21, Category = "OST_StructuralFraming", TypeName = "B", Mark = "V-2", Level = "L2" }
            };
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(set, hosts, RequirementSetChunks.ByLevel, 5);
            Assert.True(p.Ok, p.Why);
            foreach (RequirementSetChunks.Chunk c in p.Chunks)
            {
                StructuralRequirementSet s = StructuralRequirementSet.Load(c.RequirementSet);
                Assert.True(s.Ok, c.Key + ": " + s.Error);
                Assert.Equal(25.0, s.Clearances.BetweenRulesMm.Value, 9);
            }
            StructuralRequirementSet l1 = StructuralRequirementSet.Load(p.Chunks.Single(c => c.Key == "L1").RequirementSet);
            Assert.Equal("V1", l1.Clearances.Pairs.Single().RuleA);
        }

        // ------------------------------------------------------ duplicate_bar
        //
        // ONE BAR BUILT TWICE (stage 1c). Two rules - or two sets - laid on one centreline with the same model
        // diameter are not two bars crossing: the takeoff counts that steel twice. A duplicate is the limiting
        // case of an intersection, so it is reported ONCE, as a duplicate. The kind and the code are written as
        // the reply carries them ("duplicate", "duplicate_bar").

        [Fact]
        public void Two_rules_declaring_the_same_bar_are_one_duplicate_and_the_plan_refuses_both_by_name()
        {
            JObject a = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 }, index: 0);
            JObject b = Row("b", 12, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 }, index: 1);
            OverlapReport r = Plan(null, a, b);
            OverlapPair p = Assert.Single(r.Pairs);
            Assert.Equal("duplicate", p.Kind);
            Assert.Equal(1, p.BarPairs);
            List<OverlapRefusal> refused = RebarOverlap.Refusals(r);
            Assert.Equal(new[] { 0, 1 }, refused.Select(x => x.Tag).OrderBy(x => x).ToArray());
            Assert.All(refused, x => Assert.Equal("duplicate_bar", x.Code));
            Assert.Contains("SAME BAR", refused.Single(x => x.Tag == 0).Why);
            Assert.Contains("'b'", refused.Single(x => x.Tag == 0).Why);
            Assert.Contains("'a'", refused.Single(x => x.Tag == 1).Why);
            Assert.Equal("b", (string)refused.Single(x => x.Tag == 0).Overlaps[0]["other_rule_id"]);
            JObject j = r.ToJson();
            Assert.Equal(1, (int)j["duplicates"]["set_pairs"]);
            Assert.Equal(1, (int)j["duplicates"]["bar_pairs"]);
            Assert.Equal("duplicate", (string)j["overlaps"][0]["kind"]);
        }

        [Fact]
        public void A_duplicate_is_the_same_centreline_within_the_tolerance_either_way_round()
        {
            JObject a = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            // Drawn the other way round, 1.5 mm off: within the 2 mm tolerance - the same bar.
            JObject b = Row("b", 11, 16, new[] { P(3000, 1.5, 0), P(0, 1.5, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            OverlapPair p = Assert.Single(Plan(null, a, b).Pairs);
            Assert.Equal("duplicate", p.Kind);
            Assert.Equal(1.5, (double)p.ToJson()["max_deviation_mm"], 6);
            // 3 mm off: past the tolerance it is two bars passing through each other.
            JObject c = Row("c", 11, 16, new[] { P(0, 3, 0), P(3000, 3, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            Assert.Equal(RebarOverlap.KindIntersection, Assert.Single(Plan(null, a, c).Pairs).Kind);
        }

        [Fact]
        public void Another_diameter_or_a_bar_that_is_only_part_of_the_other_is_an_intersection_not_a_duplicate()
        {
            JObject a = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            JObject thicker = Row("b", 11, 20, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            Assert.Equal(RebarOverlap.KindIntersection, Assert.Single(Plan(null, a, thicker).Pairs).Kind);
            JObject half = Row("c", 11, 16, new[] { P(0, 0, 0), P(1500, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            Assert.Equal(RebarOverlap.KindIntersection, Assert.Single(Plan(null, a, half).Pairs).Kind);
        }

        [Fact]
        public void A_duplicate_counts_the_coinciding_bar_pairs_and_names_their_positions()
        {
            // Two sets of three bars, 150 apart in z; the second starts one pitch up, so two of its bars are the
            // first set's second and third - and its third stands alone, 150 above anything.
            JObject a = Row("a", 11, 12, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0, 150, 300 });
            JObject b = Row("b", 11, 12, new[] { P(0, 0, 150), P(3000, 0, 150) }, new[] { 0, 0, 1.0 }, new[] { 0.0, 150, 300 });
            OverlapPair p = Assert.Single(Plan(null, a, b).Pairs);
            Assert.Equal("duplicate", p.Kind);
            Assert.Equal(2, p.BarPairs);
            JArray pp = (JArray)p.ToJson()["position_pairs"];
            Assert.Equal(new[] { "1/0", "2/1" }, pp.Select(x => (int)x[0] + "/" + (int)x[1]).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void A_duplicate_is_reported_once_and_a_third_bar_crossing_both_is_an_intersection_with_each()
        {
            JObject a = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            JObject b = Row("b", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 });
            JObject c = Row("c", 12, 16, new[] { P(1000, -500, 0), P(1000, 500, 0) }, new[] { 1.0, 0, 0 }, new[] { 0.0 });
            OverlapReport r = Plan(null, a, b, c);
            Assert.Equal(3, r.Pairs.Count);
            Assert.Equal(new[] { "a+b:duplicate", "a+c:" + RebarOverlap.KindIntersection, "b+c:" + RebarOverlap.KindIntersection },
                         r.Pairs.Select(p => p.A.RuleId + "+" + p.B.RuleId + ":" + p.Kind).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void No_declaration_excuses_a_duplicate_but_two_bars_of_one_layout_are_never_compared()
        {
            // A declared lap whose two pieces are the same bar is still one bar built twice.
            JObject a = Row("a", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 },
                            belongsTo: new JObject { ["declared_rule_id"] = "S", ["lap_next_rule_id"] = "b" });
            JObject b = Row("b", 11, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 1.0, 0 }, new[] { 0.0 },
                            belongsTo: new JObject { ["declared_rule_id"] = "S" });
            Assert.Equal("duplicate", Assert.Single(Plan(null, a, b).Pairs).Kind);
            // Two zones of one layout lying on each other are the layout's business.
            JObject z1 = Row("z#1", 11, 10, new[] { P(0, 0, 0), P(300, 0, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0 },
                             belongsTo: new JObject { ["declared_rule_id"] = "z", ["layout"] = "z" });
            JObject z2 = Row("z#2", 11, 10, new[] { P(0, 0, 0), P(300, 0, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0 },
                             belongsTo: new JObject { ["declared_rule_id"] = "z", ["layout"] = "z" });
            Assert.Empty(Plan(null, z1, z2).Pairs);
        }

        [Fact]
        public void The_audit_reports_the_same_bar_built_as_two_sets_as_duplicate_bar_with_both_bar_ids_and_positions()
        {
            JObject rowA = Row("a", 5001, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { 0, 0, 1.0 }, new[] { 0.0, 200 });
            JObject rowB = Row("b", 5001, 16, new[] { P(0, 0, 200), P(3000, 0, 200) }, new[] { 0, 0, 1.0 }, new[] { 0.0 });
            JObject barA = Observed(9001, 16, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { P(0, 0, 0), P(3000, 0, 0) }, new[] { P(0, 0, 0), P(0, 0, 200) });
            JObject barB = Observed(9002, 16, new[] { P(0, 0, 200), P(3000, 0, 200) }, new[] { P(0, 0, 200), P(3000, 0, 200) }, new[] { P(0, 0, 0) });
            OverlapReport r = Audit(null, (rowA, barA), (rowB, barB));
            JArray findings = RebarOverlap.AuditFindings(r);
            JObject f = Assert.Single(findings.OfType<JObject>());
            Assert.Equal("duplicate_bar", (string)f["code"]);
            Assert.Equal(RebarSeverity.Error, (string)f["severity"]);
            Assert.Equal(new long[] { 9001, 9002 }, new[] { (long)f["rebar_id"], (long)f["other_rebar_id"] }.OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "a", "b" }, new[] { (string)f["rule_id"], (string)f["other_rule_id"] }.OrderBy(x => x).ToArray());
            Assert.Equal(1, (int)f["position"]);
            Assert.Equal(0, (int)f["other_position"]);
            Assert.Equal(200.0, (double)f["point_mm"][2], 6);
            Assert.Contains("duplicate_bar", RebarFinding.All);
        }

        // --------------------------------------------------------- performance

        [Fact]
        public void Twenty_thousand_bars_of_a_building_are_checked_by_a_broad_phase_not_pair_by_pair()
        {
            // Ten storeys. Each: a 30 x 30 m slab with four mat layers at 150 (800 bars), and 7 x 7
            // columns 400 x 400 with eight 20 mm bars and twenty 10 mm hoops each (1372 bars).
            var rows = new List<JObject>();
            int index = 0;
            for (int storey = 0; storey < 10; storey++)
            {
                double z0 = storey * 3200.0;
                // Mat layers: bottom x, bottom y, top x, top y - stacked in contact, 12 mm bars.
                double[] depth = { z0 + 3000 + 40 + 6, z0 + 3000 + 40 + 18, z0 + 3200 - 40 - 6, z0 + 3200 - 40 - 18 };
                for (int l = 0; l < 4; l++)
                {
                    bool alongX = l % 2 == 0;
                    double[] a = alongX ? P(0, 75, depth[l]) : P(75, 0, depth[l]);
                    double[] b = alongX ? P(30000, 75, depth[l]) : P(75, 30000, depth[l]);
                    double[] pos = Enumerable.Range(0, 200).Select(i => i * 150.0).ToArray();
                    rows.Add(Row("S" + storey + "#" + l, 1000 + storey, 12, new[] { a, b }, alongX ? new[] { 0, 1.0, 0 } : new[] { 1.0, 0, 0 }, pos,
                                 belongsTo: new JObject { ["declared_rule_id"] = "S" + storey, ["layout"] = "S" + storey + "#" + l }, index: index++));
                }
                for (int cx = 0; cx < 7; cx++)
                    for (int cy = 0; cy < 7; cy++)
                    {
                        long host = 100000 + storey * 100 + cx * 10 + cy;
                        double ox = 2000 + cx * 4300, oy = 2000 + cy * 4300;
                        string cage = "C" + host;
                        var cageOf = new Func<string, string, JObject>((role, layout) =>
                            new JObject { ["declared_rule_id"] = "C", ["layout"] = layout, ["cage"] = new JObject { ["id"] = cage, ["role"] = role } });
                        foreach (double v in new[] { -140.0, 140.0 })
                            rows.Add(Row(cage + "#side" + v, host, 20, new[] { P(ox + v, oy - 140, z0), P(ox + v, oy - 140, z0 + 3000) },
                                         new[] { 0, 1.0, 0 }, new[] { 0.0, 140, 280 }, belongsTo: cageOf("longitudinal", cage + "#side" + v), index: index++));
                        rows.Add(Row(cage + "#mid", host, 20, new[] { P(ox, oy - 140, z0), P(ox, oy - 140, z0 + 3000) },
                                     new[] { 0, 1.0, 0 }, new[] { 0.0, 280 }, belongsTo: cageOf("longitudinal", cage + "#mid"), index: index++));
                        rows.Add(Row(cage + "#stirrups", host, 10,
                                     new[] { P(ox - 155, oy - 155, z0 + 50), P(ox + 155, oy - 155, z0 + 50), P(ox + 155, oy + 155, z0 + 50), P(ox - 155, oy + 155, z0 + 50) },
                                     new[] { 0, 0, 1.0 }, Enumerable.Range(0, 20).Select(i => i * 150.0).ToArray(), closed: true,
                                     belongsTo: cageOf("stirrup", cage + "#stirrups"), index: index++));
                    }
            }
            // One deliberate clash: a stray bar of its own rule through the middle of the first storey's slab.
            rows.Add(Row("stray", 1, 16, new[] { P(9975, 9975, 2900), P(9975, 9975, 3300) }, new[] { 1.0, 0, 0 }, new[] { 0.0 }, index: index++));

            int bars = rows.Sum(r => ((JArray)r["layout"]["signed_positions_mm"]).Count);
            Assert.True(bars >= 20000, bars + " bars");

            var sw = Stopwatch.StartNew();
            OverlapReport r = Plan(Clear(25), rows.ToArray());
            sw.Stop();
            _out.WriteLine(bars + " bars in " + rows.Count + " sets, " + r.Segments + " segments: " + r.CandidatePairs +
                           " candidate segment pairs, " + r.ExactTests + " exact tests, " + sw.ElapsedMilliseconds + " ms");
            // A naive all-pairs check would be (segments^2)/2 exact tests; the broad phase must cut that by orders of magnitude.
            double naive = (double)r.Segments * r.Segments / 2;
            Assert.True(r.ExactTests < naive / 100, r.ExactTests + " exact tests against " + naive + " all-pairs");
            Assert.True(sw.Elapsed.TotalSeconds < 60, sw.ElapsedMilliseconds + " ms");
            // The stray bar crosses the four mat layers of storey 0, and nothing else clashes.
            Assert.All(r.Pairs, p => Assert.Contains("stray", new[] { p.A.RuleId, p.B.RuleId }));
            Assert.Equal(4, r.Pairs.Count);
        }
    }
}
