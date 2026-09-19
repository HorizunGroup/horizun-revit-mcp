// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A DUCT'S SIZE IS WRITTEN BESIDE IT, AND ONLY SOMETIMES UNAMBIGUOUSLY.
//
// Measured on a real corridor supply plan (campaign 6): sizes "16X8" ... "6X6"
// beside single-line mains, airflows "(630 CFM)" and damper tags "FSD" on the same
// layer, and size changes at vertices of one straight polyline. These cases pin
// what is read as a size, which run it names and why, where a size may travel,
// and where it must stop.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadDuctSectionTests
    {
        private static CadSectionRule Rule(double maxd = 600) => new CadSectionRule
        {
            LabelLayers = { "M-TEXT" }, LabelUnits = "inch", LabelOrder = "width_x_height",
            MaxDistanceMm = maxd, LeaderToleranceMm = 300
        };

        private static CadRunSection Run(string id, double x1, double y1, double x2, double y2) =>
            new CadRunSection { RunId = id, SemanticId = "s-" + id, Start = new CadPoint(x1, y1), End = new CadPoint(x2, y2) };

        private static CadLabel Label(string id, string text, double x, double y, double rotDeg = 0) =>
            new CadLabel { Id = id, Text = text, Layer = "M-TEXT", At = new CadPoint(x, y), RotationRadians = rotDeg * System.Math.PI / 180 };

        [Fact]
        public void Only_a_section_is_a_size_and_units_are_the_rules()
        {
            Assert.Equal(System.Tuple.Create(16.0, 8.0), CadDuctSections.ParseSize("16X8"));
            Assert.Equal(System.Tuple.Create(8.0, 6.0), CadDuctSections.ParseSize(" 8x6 "));
            Assert.Equal(System.Tuple.Create(12.0, 8.0), CadDuctSections.ParseSize("{\\fArial|b0;12X8}"));
            Assert.Null(CadDuctSections.ParseSize("(630 CFM)"));
            Assert.Equal("airflow", CadDuctSections.NotASize("(630 CFM)"));
            Assert.Equal("tag", CadDuctSections.NotASize("FSD"));
            var r = CadDuctSections.Assign(new List<CadRunSection> { Run("a", 0, 0, 5000, 0) },
                new List<CadLabel> { Label("l1", "16X8", 2500, 200), Label("l2", "(630 CFM)", 2500, -200) },
                new List<CadLeaderLine>(), Rule(), 25.4);
            Assert.Equal("documented", r.Runs[0].State);
            Assert.Equal(406.4, r.Runs[0].WidthMm.Value, 3);
            Assert.Equal(203.2, r.Runs[0].HeightMm.Value, 3);
            Assert.Single(r.OtherTexts);
        }

        [Fact]
        public void A_leader_names_the_run_even_when_another_run_is_nearer_the_text()
        {
            var runs = new List<CadRunSection> { Run("near", 0, 0, 5000, 0), Run("far", 0, 1000, 5000, 1000) };
            var leader = new CadLeaderLine { Id = "L" };
            leader.Points.Add(new CadPoint(2500, 1005));   // arrowhead on "far"
            leader.Points.Add(new CadPoint(2500, 150));    // tail at the text
            var r = CadDuctSections.Assign(runs, new List<CadLabel> { Label("l", "12X8", 2500, 150) },
                new List<CadLeaderLine> { leader }, Rule(), 25.4);
            Assert.Equal("documented", runs.Single(x => x.RunId == "far").State);
            Assert.Equal("missing", runs.Single(x => x.RunId == "near").State);
            Assert.Contains("leader", runs.Single(x => x.RunId == "far").Labels[0].Value<string>("by"));
        }

        [Fact]
        public void A_label_parallel_to_one_run_names_it_and_one_between_two_runs_is_ambiguous()
        {
            var runs = new List<CadRunSection> { Run("h", 0, 0, 5000, 0), Run("v", 2700, -3000, 2700, -300) };
            var r = CadDuctSections.Assign(runs, new List<CadLabel> { Label("l", "10X6", 2500, -180, 0) },
                new List<CadLeaderLine>(), Rule(), 25.4);
            Assert.Equal("documented", runs[0].State);            // parallel wins over the vertical run's 200 mm

            var runs2 = new List<CadRunSection> { Run("a", 0, 0, 5000, 0), Run("b", 0, 400, 5000, 400) };
            CadDuctSections.Assign(runs2, new List<CadLabel> { Label("m", "10X6", 2500, 190, 0) },
                new List<CadLeaderLine>(), Rule(), 25.4);
            Assert.All(runs2, x => Assert.Equal("ambiguous", x.State));
        }

        [Fact]
        public void Two_sizes_on_one_run_is_a_contradiction_not_a_choice()
        {
            var runs = new List<CadRunSection> { Run("a", 0, 0, 9000, 0) };
            CadDuctSections.Assign(runs, new List<CadLabel> { Label("1", "12X8", 1000, 200), Label("2", "8X8", 8000, 200) },
                new List<CadLeaderLine>(), Rule(), 25.4);
            Assert.Equal("contradictory", runs[0].State);
            Assert.Null(runs[0].WidthMm);
        }

        [Fact]
        public void A_size_travels_through_an_elbow_and_stops_at_a_tee_and_at_a_runs_own_label()
        {
            var runs = new List<CadRunSection>
            {
                Run("main", 0, 0, 5000, 0),            // labelled 12X8
                Run("leg", 5000, 0, 5000, 3000),       // elbow at (5000,0): takes 12X8
                Run("next", 5000, 3000, 8000, 3000),   // elbow: takes 12X8 through leg
                Run("t1", -3000, 0, 0, 0),             // tee at (0,0) with main and branch
                Run("br", 0, 0, 0, -2000),             // branch, unlabelled: stays missing
                Run("own", 8000, 3000, 11000, 3000)    // own label 8X8: kept, not overwritten
            };
            CadDuctSections.Assign(runs, new List<CadLabel> { Label("m", "12X8", 2500, 200), Label("o", "8X8", 9500, 3200) },
                new List<CadLeaderLine>(), Rule(), 25.4);
            Assert.Equal("propagated", runs.Single(r => r.RunId == "leg").State);
            Assert.Equal("propagated", runs.Single(r => r.RunId == "next").State);
            Assert.Equal("main", runs.Single(r => r.RunId == "leg").PropagatedFrom);
            Assert.Equal("missing", runs.Single(r => r.RunId == "br").State);
            Assert.Equal("missing", runs.Single(r => r.RunId == "t1").State);
            Assert.Equal(203.2, runs.Single(r => r.RunId == "own").WidthMm.Value, 3);
        }

        [Fact]
        public void Two_different_sizes_arriving_at_one_unlabelled_run_is_a_transition_it_gets_neither()
        {
            var runs = new List<CadRunSection>
            {
                Run("a", 0, 0, 5000, 0), Run("tr", 5000, 0, 5280, 0), Run("b", 5280, 0, 9000, 0)
            };
            CadDuctSections.Assign(runs, new List<CadLabel> { Label("1", "12X8", 2500, 200), Label("2", "8X8", 7000, 200) },
                new List<CadLeaderLine>(), Rule(), 25.4);
            Assert.Equal("transition", runs.Single(r => r.RunId == "tr").State);
            Assert.Null(runs.Single(r => r.RunId == "tr").WidthMm);
        }

        [Fact]
        public void A_piece_between_a_known_size_and_an_unsettled_run_is_a_transition_not_a_guess()
        {
            // MEASURED on a real plan: an 11 in piece between a 12x8 run and a run whose labels contradict
            // each other took 12x8 and would have been built as a straight 12x8 duct.
            var runs = new List<CadRunSection>
            {
                Run("a", 0, 0, 5000, 0), Run("piece", 5000, 0, 5280, 0), Run("b", 5280, 0, 12000, 0)
            };
            CadDuctSections.Assign(runs, new List<CadLabel>
            {
                Label("1", "12X8", 2500, 200), Label("2", "8X8", 7000, 200), Label("3", "8X6", 10500, 200)
            }, new List<CadLeaderLine>(), Rule(), 25.4);
            Assert.Equal("contradictory", runs.Single(r => r.RunId == "b").State);
            Assert.Equal("transition", runs.Single(r => r.RunId == "piece").State);
            Assert.Contains("transition between", runs.Single(r => r.RunId == "piece").Reason);
        }

        private static CadRequirementSet Set(string section, string extra = "")
        {
            string doc = (@"{ 'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 's', 'version': '1.0.0', 'title': 't' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25.4, 'gap_mm': 25.4, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [ { 'id': 'd', 'precedence': 10, 'layers': ['M-DUCT'], 'produces': 'duct', 'family_type': 'Rectangular Duct: X',
                 'system_type': 'Supply Air', 'level': 'Level 1', 'offset_mm': 2743.2 " + extra + @",
                 'geometry': { 'from': 'single_lines', 'merge_collinear': false }, 'section': SECTION } ] }")
                .Replace('\'', '"').Replace("SECTION", section);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private const string Good = "{ 'from': 'labels', 'label_layers': ['*M-TEXT'], 'label_units': 'inch', 'label_order': 'width_x_height', 'max_distance_mm': 600 }";

        [Fact]
        public void The_section_rule_refuses_every_second_reading()
        {
            Assert.NotNull(Set(Good.Replace('\'', '"')).Rules[0].Section);
            Assert.Throws<CadRequirementSetException>(() => Set(Good.Replace("'inch'", "'feet'").Replace('\'', '"')));
            Assert.Throws<CadRequirementSetException>(() => Set(Good.Replace("'width_x_height'", "'any'").Replace('\'', '"')));
            Assert.Throws<CadRequirementSetException>(() => Set(Good.Replace('\'', '"'), ", 'diameter_mm': 300".Replace('\'', '"')));
            Assert.Throws<CadRequirementSetException>(() => Set(Good.Replace("'labels'", "'nearest'").Replace('\'', '"')));
        }

        [Fact]
        public void Kept_apart_pieces_are_runs_in_the_plan_and_in_the_network_with_the_same_ids()
        {
            CadRequirementSet set = Set(Good.Replace('\'', '"'));
            var segs = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(5000, 0), "M-DUCT", CadCurveKind.Polyline, 0, "p:0"),
                new CadSegment(new CadPoint(5000, 0), new CadPoint(5280, 0), "M-DUCT", CadCurveKind.Polyline, 1, "p:1"),
                new CadSegment(new CadPoint(5280, 0), new CadPoint(9000, 0), "M-DUCT", CadCurveKind.Polyline, 2, "p:2")
            };
            CadInterpretation interp = CadInterpretationRules.Interpret(segs, set, "h");
            var ducts = interp.Candidates.Where(c => c.ProposedKind == "duct").ToList();
            Assert.Equal(3, ducts.Count);
            CadNetwork net = CadNetworkRules.Build(segs, new CadNetworkOptions
            { ConnectToleranceMm = 25.4, IdentityToleranceMm = 25.4, CollinearToleranceDegrees = 2.0, GapReviewDistanceMm = 250, ThroughToleranceDegrees = 15 },
                CadNetworkRules.DeclarationsFrom(set));
            Assert.Equal(3, net.Runs.Count);
            Assert.Equal(ducts.Select(c => c.SemanticId).OrderBy(x => x), net.Runs.Select(r => r.SemanticId).OrderBy(x => x));
        }

        [Fact]
        public void A_closed_ring_on_the_duct_layer_is_not_four_ducts_unless_the_rule_says_so()
        {
            // MEASURED on a real plan (campaign 7): a 24 x 24 in square drawn on the duct layer. Built through
            // the harvest's own function - the first version of this test fed segments ALREADY named ring: and
            // passed while the live plan still read the square as runs (CadRingsTests has the live shape).
            var ring = CadRings.PolylineSegments(new[]
            {
                new CadPoint(0, 0), new CadPoint(610, 0), new CadPoint(610, 610), new CadPoint(0, 610), new CadPoint(0, 0)
            }, "M-DUCT", 7);
            ring.Add(new CadSegment(new CadPoint(2000, 0), new CadPoint(7000, 0), "M-DUCT"));
            CadRequirementSet set = Set(Good.Replace('\'', '"'));
            var ducts = CadInterpretationRules.Interpret(ring, set, "h").Candidates.Where(c => c.ProposedKind == "duct").ToList();
            Assert.Single(ducts);                                   // the open line only
            CadRequirementSet include = CadRequirementSet.Load(JObject.Parse((@"{ 'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 's', 'version': '1.0.0', 'title': 't' }, 'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25.4, 'gap_mm': 25.4, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [ { 'id': 'd', 'precedence': 10, 'layers': ['M-DUCT'], 'produces': 'duct', 'family_type': 'Rectangular Duct: X',
                 'system_type': 'Supply Air', 'level': 'Level 1', 'offset_mm': 2743.2,
                 'geometry': { 'from': 'single_lines', 'merge_collinear': false, 'include_closed_polylines': true } } ] }").Replace('\'', '"')));
            Assert.Equal(5, CadInterpretationRules.Interpret(ring, include, "h").Candidates.Count(c => c.ProposedKind == "duct"));
        }

        [Fact]
        public void A_run_with_a_section_is_emitted_with_width_and_height_and_no_diameter()
        {
            CadRequirementSet set = Set(Good.Replace('\'', '"'));
            var segs = new List<CadSegment> { new CadSegment(new CadPoint(0, 0), new CadPoint(5000, 0), "M-DUCT") };
            CadInterpretation interp = CadInterpretationRules.Interpret(segs, set, "h");
            CadCandidate c = interp.Candidates.Single(x => x.ProposedKind == "duct");
            c.SectionWidthMm = 406.4; c.SectionHeightMm = 203.2;
            JObject row = CadConversionPlanRules.Plan(interp, set, "src", false).Actions.Single(a => a.Kind == "duct").Arguments;
            Assert.Equal(406.4, row.Value<double>("width"), 3);
            Assert.Equal(203.2, row.Value<double>("height"), 3);
            Assert.Null(row["diameter"]);
        }
    }
}
