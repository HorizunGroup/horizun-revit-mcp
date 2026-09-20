// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A DUCT RUN DRAWN AS TWO, AND TWO DRAWN AS ONE.
//
// The split proposal was built for walls, from double lines. A duct comes from a
// SINGLE line and carries a section read off a label, so the same revision reaches
// the update with a different shape: two pieces of different size where one run
// stood. These fix what the planner must say about that - and about the reverse,
// which is the revision that takes a division out again.
//
// The geometry here is the synthetic fixture's, in millimetres: run A is 10 160 mm
// (400 in), cut at 6 350 mm (250 in) in R1 and at 7 620 mm (300 in) in R2.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadDuctSplitUpdateTests
    {
        private const string RevA = "sha-of-R0";
        private const string RevB = "sha-of-R1";

        private static CadRequirementSet Set() => CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'mep-split', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25.4, 'gap_mm': 25.4, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r-supply', 'precedence': 10, 'discipline': 'mechanical',
                          'layers': ['*M-SUPPLY'], 'produces': 'duct',
                          'family_type': 'Rectangular Duct: Radius Elbows / Tees',
                          'system_type': 'Supply Air', 'level': 'Level 1', 'offset_mm': 2743.2,
                          'geometry': { 'from': 'single_lines', 'min_length_mm': 100.0,
                                        'merge_collinear': false } }]
            }".Replace('\'', '"')));

        private static CadSegment Line(double x0, double x1, double y = 0) =>
            new CadSegment(new CadPoint(x0, y), new CadPoint(x1, y), "M-SUPPLY");

        private static List<CadCandidate> Read(CadRequirementSet set, string sha, params CadSegment[] lines) =>
            CadInterpretationRules.Interpret(lines.ToList(), set, sha).Candidates.ToList();

        private static CadAuditSubject Built(CadCandidate from, CadRequirementSet set, long id) => new CadAuditSubject
        {
            ElementId = id,
            Category = "Ducts",
            TypeName = "Rectangular Duct",
            Geometry = new List<CadPoint>(from.Geometry),
            Provenance = new CadProvenance
            {
                SchemaVersion = 4,
                CandidateId = from.Id, GeometryId = from.GeometryId, SemanticId = from.SemanticId,
                RuleId = from.RuleId, Layer = from.Layer,
                RequirementSetSha256 = set.Sha256, SourceFileSha256 = RevA,
                BuiltGeometry = CadUpdateRules.Encode(from.Geometry)
            }
        };

        // ---- R0 -> R1: one run becomes two --------------------------------------

        [Fact]
        public void A_duct_run_drawn_as_two_pieces_is_offered_as_split_and_nothing_is_automatic()
        {
            CadRequirementSet set = Set();
            CadAuditSubject element = Built(Read(set, RevA, Line(0, 10160)).Single(), set, 5001);
            List<CadCandidate> now = Read(set, RevB, Line(0, 6350), Line(6350, 10160));
            Assert.Equal(2, now.Count);

            CadUpdate u = CadUpdateRules.Plan(now, new[] { element }, set, RevB, lineage: new[] { RevA });

            CadUpdateAction orphan = u.Of("orphan").Single();
            Assert.Equal(CadChange.Split, orphan.Classification);
            Assert.Equal(2, ((JArray)orphan.Evidence["may_have_been_split_into"]).Count);
            // the longest piece is the one that would keep the element: 6 350 of 10 160
            CadCandidate longest = now.OrderByDescending(c => c.Geometry[0].PlanDistanceTo(c.Geometry[1])).First();
            Assert.Equal(longest.Id, orphan.PairedWith);
            Assert.All(u.Of("create"), c => Assert.False(c.Automatic));
        }

        [Fact]
        public void An_accepted_duct_split_reshapes_the_element_and_builds_the_other_piece()
        {
            CadRequirementSet set = Set();
            CadAuditSubject element = Built(Read(set, RevA, Line(0, 10160)).Single(), set, 5001);
            List<CadCandidate> now = Read(set, RevB, Line(0, 6350), Line(6350, 10160));
            string keep = CadUpdateRules.Plan(now, new[] { element }, set, RevB, lineage: new[] { RevA })
                                        .Of("orphan").Single().PairedWith;

            CadUpdate u = CadUpdateRules.Plan(now, new[] { element }, set, RevB,
                                              new Dictionary<long, string> { { 5001L, keep } }, new[] { RevA });
            CadUpdateAction reshape = u.Of("set_curve").Single();
            Assert.Equal(5001L, reshape.ElementId);
            Assert.True(reshape.Automatic);
            Assert.Single(u.Of("create"));
            Assert.Empty(u.Of("orphan"));
        }

        // ---- R1 -> R2: the division moves ---------------------------------------

        [Fact]
        public void A_division_that_moves_leaves_nothing_automatic_on_ground_an_element_still_holds()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> before = Read(set, RevA, Line(0, 6350), Line(6350, 10160));
            var built = new[] { Built(before[0], set, 5001), Built(before[1], set, 5002) };
            List<CadCandidate> now = Read(set, RevB, Line(0, 7620), Line(7620, 10160));

            CadUpdate u = CadUpdateRules.Plan(now, built, set, RevB, lineage: new[] { RevA });

            // WHAT A MOVED DIVISION IS, measured rather than assumed. Both pieces changed length: the
            // first by 20% - which the pairing rule reads as the same run re-shaped, and offers - and
            // the second by a third, which it does not. So one pairing is offered and one element is
            // left as an orphan, and BOTH creates wait for a person.
            Assert.Empty(u.Of("set_curve"));
            Assert.Equal(2, u.Of("create").Count());
            Assert.Equal(2, u.Of("orphan").Count());
            CadUpdateAction paired = u.Of("orphan").Single(o => o.PairedWith != null);
            Assert.Equal(5001L, paired.ElementId);

            // AND NOTHING IS AUTOMATIC. The second create lies inside element 5002, which still stands:
            // before this was measured it was automatic, and an unattended run would have built a duct
            // inside another one. The hold names the element and how much of the line it holds.
            Assert.All(u.Of("create"), c => Assert.False(c.Automatic));
            // BOTH of them stand on element 5002 is line, which is the point: the new division moved
            // back into ground the second element still holds, so neither piece may be built unattended.
            Assert.All(u.Of("create"), c => Assert.Equal(5002L, (long)c.Evidence["stands_there"]));
            CadUpdateAction second = u.Of("create").Single(c => c.Geometry[0].X > 7000);
            Assert.True((double)second.Evidence["overlap_mm"] > 2000);
        }

        [Fact]
        public void A_create_that_touches_no_standing_element_stays_automatic()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> before = Read(set, RevA, Line(0, 6350));
            var built = new[] { Built(before[0], set, 5001) };
            // the same run, plus one drawn somewhere else entirely
            List<CadCandidate> now = Read(set, RevB, Line(0, 6350), Line(0, 5000, 20000));

            CadUpdate u = CadUpdateRules.Plan(now, built, set, RevB, lineage: new[] { RevA });
            CadUpdateAction elsewhere = u.Of("create").Single();
            Assert.True(elsewhere.Automatic);
            Assert.Null(elsewhere.Evidence["stands_there"]);
        }

        // ---- R2 -> R3: the division disappears ----------------------------------

        [Fact]
        public void Two_pieces_drawn_as_one_run_again_are_offered_as_a_merge_and_held()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> before = Read(set, RevA, Line(0, 6350), Line(6350, 10160));
            var built = new[] { Built(before[0], set, 5001), Built(before[1], set, 5002) };
            List<CadCandidate> now = Read(set, RevB, Line(0, 10160));
            Assert.Single(now);

            CadUpdate u = CadUpdateRules.Plan(now, built, set, RevB, lineage: new[] { RevA });

            // THE SHAPE THIS MUST HAVE: one create that covers both elements' lines, and both elements
            // named as its parts - held, because merging destroys one of them and nothing in a DWG says
            // which. Without it the plan is two orphans and one create that nobody can connect.
            CadUpdateAction create = u.Of("create").Single();
            Assert.Equal(CadChange.Merge, create.Classification);
            Assert.False(create.Automatic);
            var parts = (JArray)create.Evidence["may_be_the_merge_of"];
            Assert.Equal(new[] { 5001L, 5002L }, parts.Select(x => (long)x).OrderBy(x => x).ToArray());
            // the longest piece is the one that would keep its element
            Assert.Equal(5001L, (long)create.Evidence["would_keep_element"]);
            Assert.All(u.Of("orphan"), o =>
            {
                Assert.Equal(CadChange.Merge, o.Classification);
                Assert.False(o.Automatic);
                Assert.Equal(create.CandidateId, (string)o.Evidence["may_have_been_merged_into"]);
            });
        }

        [Fact]
        public void An_accepted_merge_reshapes_the_longest_and_leaves_the_others_to_a_decision()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> before = Read(set, RevA, Line(0, 6350), Line(6350, 10160));
            var built = new[] { Built(before[0], set, 5001), Built(before[1], set, 5002) };
            List<CadCandidate> now = Read(set, RevB, Line(0, 10160));
            string keep = now[0].Id;

            CadUpdate u = CadUpdateRules.Plan(now, built, set, RevB,
                                              new Dictionary<long, string> { { 5001L, keep } }, new[] { RevA });

            CadUpdateAction reshape = u.Of("set_curve").Single();
            Assert.Equal(5001L, reshape.ElementId);
            Assert.True(reshape.Automatic);
            Assert.Equal(CadChange.Merge, reshape.Classification);
            // the OTHER element is not deleted by accepting a pairing: a delete is its own decision
            CadUpdateAction other = u.Actions.Single(a => a.ElementId == 5002L);
            Assert.False(other.Automatic);
            Assert.Equal("the_other_part_of_a_merge", (string)other.Evidence["held_because"]);
            Assert.Empty(u.Of("create"));
        }

        [Fact]
        public void A_merge_is_not_offered_when_the_pieces_do_not_cover_the_new_run()
        {
            CadRequirementSet set = Set();
            // only the first half of the new run was ever built: the rest is genuinely new
            List<CadCandidate> before = Read(set, RevA, Line(0, 3000));
            var built = new[] { Built(before[0], set, 5001) };
            List<CadCandidate> now = Read(set, RevB, Line(0, 10160));

            CadUpdate u = CadUpdateRules.Plan(now, built, set, RevB, lineage: new[] { RevA });
            Assert.DoesNotContain(u.Actions, a => a.Classification == CadChange.Merge);
        }

        [Fact]
        public void Merge_is_in_the_closed_vocabulary()
        {
            Assert.Contains(CadChange.Merge, CadChange.All);
        }
    }
}
