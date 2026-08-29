// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A REFUSED NAMING PASS MUST NOT LEAVE NAMES BEHIND.
//
// CadNamingRules.Assign writes its Names and then adds the Problems that make
// them unusable - an ordered set one value short still names every candidate,
// shifted by one. horizun_plan_from_cad was the only caller that ever read those
// Problems, so it refused the drawing correctly while the AUDIT and the
// INCREMENTAL, running on the same drawing and the same set, read the shifted
// names as ground truth and reported that a person had hand-renamed grids nobody
// had touched.
//
// That is a verification reporting a failure for work that landed, which is the
// worst answer either of them can give: it sends somebody to look for a decision
// that was never made, and it hides the real fault - the set is one name short -
// which appeared in neither reply.
//
// The other half of this file is the identity nothing checked at all. A room
// NUMBER is the one thing Revit genuinely requires to be unique, and it was the
// only one with no check: not against the other candidates, not against the
// model. Revit takes a duplicate room number as a WARNING, so it would be built,
// it would re-read as the number that was asked for, and it would double-count
// in every schedule while the reply said number_verified: true.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadNamingRefusalTests
    {
        private static CadRequirementSet Grids(string naming)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'g', 'version': '1.0.0', 'title': 'Grids' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'grids', 'precedence': 10, 'layers': ['S-GRID*'], 'produces': 'grid',
                          'category': 'OST_Grids', 'naming': NAMING,
                          'geometry': { 'from': 'single_lines', 'min_length_mm': 1000 } }]
            }".Replace('\'', '"').Replace("NAMING", naming.Replace('\'', '"'));
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        /// <summary>N vertical grid lines, evenly spaced along x.</summary>
        private static List<CadSegment> Lines(int howMany)
        {
            var segs = new List<CadSegment>();
            for (int i = 0; i < howMany; i++)
                segs.Add(new CadSegment(new CadPoint(i * 6000, 0), new CadPoint(i * 6000, 8000), "S-GRID"));
            return segs;
        }

        private static CadInterpretation Read(CadRequirementSet set, List<CadSegment> segs)
        {
            return CadInterpretationRules.Interpret(segs, set, "sha");
        }

        // -------------------------------------------- a refusal assigns nothing

        [Fact]
        public void A_set_one_name_SHORT_leaves_no_candidate_named()
        {
            // Five lines, four names. Assign still names four of them - and if the
            // extra line came in at the head of the order, every one of those four
            // names is on the wrong grid.
            CadInterpretation read = Read(
                Grids("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['A','B','C','D'] }"), Lines(5));

            Assert.NotEmpty(read.NamingProblems);
            Assert.All(read.Candidates, c => Assert.Null(c.AssignedName));
        }

        [Fact]
        public void And_each_candidate_carries_the_REASON_instead_of_a_name()
        {
            // The candidates stay - an audit is still entitled to every geometric
            // finding it can make about them - and they say why they have no name.
            CadInterpretation read = Read(
                Grids("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['A','B','C','D'] }"), Lines(5));

            CadCandidate one = read.Candidates.First();
            Assert.Contains(one.IneligibleReasons,
                            r => r.Contains("names nothing usable"));
            Assert.False(one.EligibleForAutomaticApply);
        }

        [Fact]
        public void A_naming_that_SUCCEEDS_still_names_everything()
        {
            // The guard must not swallow the ordinary case.
            CadInterpretation read = Read(
                Grids("{ 'strategy': 'ordered', 'axis': 'x', 'values': ['A','B','C','D'] }"), Lines(4));

            Assert.Empty(read.NamingProblems);
            Assert.Equal(4, read.Candidates.Count(c => !string.IsNullOrEmpty(c.AssignedName)));
        }

        // ---------------------------------------------------- numbers are identities

        [Fact]
        public void One_NUMBER_on_two_candidates_is_refused()
        {
            var outcome = CadNamingRules.Assign(
                new CadNaming
                {
                    Strategy = "by_position",
                    ByPosition = new List<CadNamedPosition>
                    {
                        new CadNamedPosition { X = 0, Y = 0, ToleranceMm = 500, Name = "Office", Number = "101" },
                        new CadNamedPosition { X = 6000, Y = 0, ToleranceMm = 500, Name = "Store", Number = "101" }
                    }
                },
                new List<CadCandidate>
                {
                    new CadCandidate { SemanticId = "a", Geometry = { new CadPoint(0, 0) } },
                    new CadCandidate { SemanticId = "b", Geometry = { new CadPoint(6000, 0) } }
                },
                1.0, null);

            Assert.True(outcome.Refused);
            Assert.Contains(outcome.Problems, p => p.Contains("double-count"));
        }

        [Fact]
        public void A_number_the_MODEL_already_holds_is_refused_too()
        {
            // Revit does not refuse this one - it warns, builds it, and lets every
            // schedule count it twice. So the refusal has to happen here.
            var outcome = CadNamingRules.Assign(
                new CadNaming
                {
                    Strategy = "by_position",
                    ByPosition = new List<CadNamedPosition>
                    {
                        new CadNamedPosition { X = 0, Y = 0, ToleranceMm = 500, Name = "Office", Number = "101" }
                    }
                },
                new List<CadCandidate> { new CadCandidate { SemanticId = "a", Geometry = { new CadPoint(0, 0) } } },
                1.0, new[] { "101" });

            Assert.True(outcome.Refused);
            Assert.Contains(outcome.Problems, p => p.Contains("numbered '101'"));
        }

        // ------------------------------------------------------- blank and untrimmed

        [Fact]
        public void A_by_position_name_is_TRIMMED_so_the_collision_check_can_see_it()
        {
            // Untrimmed, " A " passes the model-collision pre-check that "A" would
            // have failed - and then every later audit reports the element as
            // hand-renamed, because " A " and "A" are different strings.
            CadRequirementSet set = Grids(
                "{ 'strategy': 'by_position', 'by_position': [ { 'x_mm': 0, 'y_mm': 0, " +
                "'tolerance_mm': 500, 'name': '  A  ' } ] }");

            Assert.Equal("A", set.Rules[0].Naming.ByPosition[0].Name);
        }

        [Fact]
        public void A_by_position_name_that_is_BLANK_is_refused_rather_than_dropped()
        {
            // It used to become no name at all, silently: the element took Revit's
            // own default and nothing in the plan, the apply or a later audit ever
            // said a name had been expected.
            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(
                () => Grids("{ 'strategy': 'by_position', 'by_position': [ { 'x_mm': 0, 'y_mm': 0, " +
                            "'tolerance_mm': 500, 'name': '   ', 'number': '101' } ] }"));

            Assert.Contains("blank", e.Message);
        }

        // --------------------------------------------------------- a level is not drawn

        [Fact]
        public void A_rule_that_produces_a_LEVEL_is_refused_where_the_set_is_read()
        {
            // It used to be accepted, named, and then deferred with "the candidate
            // carries no geometry a level could be built from" - on candidates that
            // plainly carried geometry. Nothing about the rule could be adjusted to
            // make it work, and the reason pointed at the drawing.
            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(
                () => CadRequirementSet.Load(JObject.Parse(@"{
                  'schema': 'horizun.cad-requirements/1',
                  'requirement_set': { 'id': 'l', 'version': '1.0.0', 'title': 'Levels' },
                  'source': { 'units': 'millimeter' },
                  'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
                  'rules': [{ 'id': 'levels', 'precedence': 10, 'layers': ['S-*'], 'produces': 'level',
                              'category': 'OST_Levels',
                              'geometry': { 'from': 'single_lines' } }]
                }".Replace('\'', '"'))));

            Assert.Contains("ELEVATION", e.Message);
            Assert.Contains("Nothing about this rule can be adjusted", e.Message);
        }
    }
}
