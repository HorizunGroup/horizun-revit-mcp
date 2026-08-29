// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// EVERY PRODUCER'S CANDIDATES ARE FINALISED.
//
// Finalise decides EligibleForAutomaticApply, and the field defaults to false.
// It used to be each producer's own last line, and the fifth producer - curved
// walls from concentric arc pairs - did not have it. Nothing threw and nothing
// was logged: every curved wall ever read came back at confidence 1.00, with an
// empty list of ineligible reasons, held for review, and the plan emitted no
// action for it. It looked exactly like a drawing that contained no walls.
//
// These tests are what stops that returning - once by behaviour, for every
// geometry source a rule can name, and once structurally, so that a sixth
// producer inherits the decision instead of having to remember it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadEligibilityTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static CadRequirementSet Set(string from, string produces, string category, string extra = "")
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'eligibility', 'version': '1.0.0', 'title': 'Eligibility' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'PRODUCES',
                          'category': 'CATEGORY', 'height_mm': 3000.0,
                          'geometry': { 'from': 'FROM' EXTRA } }]
            }".Replace('\'', '"').Replace("FROM", from).Replace("PRODUCES", produces)
              .Replace("CATEGORY", category).Replace("EXTRA", extra);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadSegment Seg(double x1, double y1, double x2, double y2, string layer = "A-WALL") =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer);

        /// <summary>An arc as the harvest keeps it: the real curve, centre at the origin.</summary>
        private static CadArcFact ArcOf(string id, double radius, double fromDeg, double toDeg,
                                        string layer = "A-WALL")
        {
            Func<double, CadPoint> at = deg => new CadPoint(
                radius * Math.Cos(deg * Math.PI / 180.0), radius * Math.Sin(deg * Math.PI / 180.0));
            return new CadArcFact(id, new CadPoint(0, 0), radius,
                at(fromDeg), at(toDeg), at((fromDeg + toDeg) / 2.0), layer, 12, 5.0);
        }

        private const string ArcExtra =
            ", 'min_thickness_mm': 100.0, 'max_thickness_mm': 400.0, 'min_overlap_fraction': 0.6";

        // ------------------------------------------------------- by behaviour

        [Fact]
        public void A_curved_wall_from_an_ARC_PAIR_is_eligible_to_be_applied()
        {
            // THE DEFECT ITSELF. Two concentric quarter-circles 200 mm apart are a
            // curved wall by every test the rule states - and this came back
            // ineligible, at confidence 1.00, with nothing to say why.
            var arcs = new List<CadArcFact> { ArcOf("a", 5100, 0, 90), ArcOf("b", 4900, 0, 90) };
            CadRequirementSet set = Set("double_arcs", "wall", "OST_Walls", ArcExtra);

            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), set, Sha, arcs);
            CadCandidate c = Assert.Single(r.Candidates);
            Assert.Empty(c.IneligibleReasons);
            Assert.True(c.EligibleForAutomaticApply,
                        "a candidate with no ineligible reason and full confidence must be applicable");
            Assert.Empty(r.NeedingReview);
        }

        [Fact]
        public void And_the_PLAN_emits_an_action_for_it_rather_than_holding_it_back()
        {
            // The consequence a user actually sees. Ineligible candidates are left
            // out of execute_plan_request, so the whole drawing converted to
            // nothing at all - indistinguishable from a drawing with no walls in it.
            var arcs = new List<CadArcFact> { ArcOf("a", 5100, 0, 90), ArcOf("b", 4900, 0, 90) };
            CadRequirementSet set = Set("double_arcs", "wall", "OST_Walls", ArcExtra);

            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), set, Sha, arcs);
            CadConversionPlan plan = CadConversionPlanRules.Plan(r, set, "fp", false);
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "M");

            JObject row = (JObject)((JArray)requests.Single()["elements"]).Single();
            Assert.NotNull(row["arc"]);
            Assert.Equal(5000.0, (double)row["arc"]["radius"], 3);
        }

        [Theory]
        [InlineData("double_lines", "wall", "OST_Walls")]
        [InlineData("closed_loops", "floor", "OST_Floors")]
        [InlineData("single_lines", "pipe", "OST_PipeCurves")]
        public void EVERY_geometry_source_produces_candidates_that_are_eligible(
            string from, string produces, string category)
        {
            // The other four producers each remembered to finalise. This pins that
            // they still do now the decision has moved, and that a valid reading
            // from any source is applicable rather than parked.
            var segs = new List<CadSegment>();
            if (from == "double_lines")
            {
                segs.Add(Seg(0, 0, 10000, 0));
                segs.Add(Seg(0, 200, 10000, 200));
            }
            else if (from == "closed_loops")
            {
                segs.Add(Seg(0, 0, 10000, 0)); segs.Add(Seg(10000, 0, 10000, 8000));
                segs.Add(Seg(10000, 8000, 0, 8000)); segs.Add(Seg(0, 8000, 0, 0));
            }
            else
            {
                segs.Add(Seg(0, 0, 10000, 0));
            }

            string extra = from == "double_lines"
                ? ", 'min_thickness_mm': 100.0, 'max_thickness_mm': 400.0, 'min_overlap_fraction': 0.5"
                : "";
            CadInterpretation r = CadInterpretationRules.Interpret(segs, Set(from, produces, category, extra), Sha);

            Assert.NotEmpty(r.Candidates);
            Assert.All(r.Candidates, c => Assert.True(
                c.EligibleForAutomaticApply || c.IneligibleReasons.Count > 0,
                "a candidate held back must SAY why - silence here is the bug this file exists for"));
            Assert.NotEmpty(r.AutomaticallyApplicable);
        }

        [Fact]
        public void A_candidate_under_the_rules_confidence_is_still_held_back_AND_says_why()
        {
            // Finalising centrally must not turn the gate off. A rule that demands
            // more confidence than the reading earns still parks the candidate,
            // with the reason attached. The reading here earns less than 1.00
            // because 'A-*' is a loose claim on a layer called 'A-WALL', and
            // layer specificity is one of the factors confidence is made of.
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'strict', 'version': '1.0.0', 'title': 'Strict' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000.0, 'min_confidence': 0.95,
                          'geometry': { 'from': 'double_arcs', 'min_thickness_mm': 100.0,
                                        'max_thickness_mm': 400.0, 'min_overlap_fraction': 0.6 } }]
            }".Replace('\'', '"');
            CadRequirementSet set = CadRequirementSet.Load(JObject.Parse(doc));
            var arcs = new List<CadArcFact> { ArcOf("a", 5100, 0, 90), ArcOf("b", 4900, 0, 90) };

            CadCandidate c = CadInterpretationRules.Interpret(new List<CadSegment>(), set, Sha, arcs)
                .Candidates.Single();
            Assert.False(c.EligibleForAutomaticApply);
            Assert.Contains(c.IneligibleReasons, x => x.Contains("confidence"));
        }

        // ------------------------------------------------------- structurally

        [Fact]
        public void Finalise_is_called_in_exactly_ONE_place_and_it_is_not_a_producer()
        {
            // A producer that has to remember is a producer that can forget, and
            // the way this failed was silent. So the call site is pinned: one, on
            // the path every candidate from every source takes.
            string source = File.ReadAllText(SourceFile("Core", "CadInterpretationRules.cs"));
            int calls = CountOf(source, "Finalise(c, rule)");
            Assert.True(calls == 1,
                "Finalise must be called once, from ProduceFor - found " + calls + " call sites. " +
                "A new producer inherits the decision; it does not repeat it.");
            Assert.Contains("foreach (CadCandidate c in produced) Finalise(c, rule);", source);
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0, i = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (i >= 0) { n++; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal); }
            return n;
        }

        private static string SourceFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit")))
                dir = dir.Parent;
            Assert.True(dir != null, "the repository root must be findable from the test binary");
            string path = Path.Combine(new[] { dir.FullName, "src", "Horizun.Revit" }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), path + " must exist");
            return path;
        }
    }
}
