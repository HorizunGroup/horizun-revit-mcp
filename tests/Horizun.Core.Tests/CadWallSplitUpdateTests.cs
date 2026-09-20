// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ONE WALL, NOW DRAWN AS SEVERAL.
//
// MEASURED (revision C, 2026-09-17): a wall with a stretch removed came back as two
// pieces inside its old line. Neither piece was 80% of the old length, so no move
// was offered; both creates were withdrawn for the space the old wall still holds,
// and the old wall became an orphan. Nothing could be applied and nothing said why
// the three belonged together. A split is now offered - never taken - and an
// accepted split re-shapes the element to its longest piece and builds the rest.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadWallSplitUpdateTests
    {
        private const string RevA = "sha-of-revision-a";
        private const string RevB = "sha-of-revision-b";

        private static CadRequirementSet Set() => CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                        'max_thickness_mm': 400, 'min_overlap_fraction': 0.5 } }]
            }".Replace('\'', '"')));

        private static IEnumerable<CadSegment> Pair(double x0, double x1, double y = 0, double half = 100)
        {
            yield return new CadSegment(new CadPoint(x0, y - half), new CadPoint(x1, y - half), "A-WALL");
            yield return new CadSegment(new CadPoint(x0, y + half), new CadPoint(x1, y + half), "A-WALL");
        }

        private static List<CadCandidate> Read(CadRequirementSet set, string sha, params IEnumerable<CadSegment>[] walls) =>
            CadInterpretationRules.Interpret(walls.SelectMany(w => w).ToList(), set, sha).Candidates.ToList();

        private static CadAuditSubject Built(CadCandidate from, CadRequirementSet set) => new CadAuditSubject
        {
            ElementId = 1001,
            Category = "Walls",
            TypeName = "Generic - 200mm",
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

        private static CadUpdate Offered(CadRequirementSet set, out CadAuditSubject element, out List<CadCandidate> now)
        {
            element = Built(Read(set, RevA, Pair(0, 6000)).Single(), set);
            // a 1000 mm stretch removed: 0-3000 and 4000-6000
            now = Read(set, RevB, Pair(0, 3000), Pair(4000, 6000));
            Assert.Equal(2, now.Count);
            return CadUpdateRules.Plan(now, new[] { element }, set, RevB, lineage: new[] { RevA });
        }

        [Fact]
        public void A_wall_drawn_as_pieces_inside_its_old_line_is_offered_as_split_and_held()
        {
            CadRequirementSet set = Set();
            CadAuditSubject element;
            List<CadCandidate> now;
            CadUpdate u = Offered(set, out element, out now);

            CadUpdateAction orphan = u.Of("orphan").Single();
            Assert.Equal(CadChange.Split, orphan.Classification);
            Assert.Equal(2, ((JArray)orphan.Evidence["may_have_been_split_into"]).Count);
            // the longest piece would keep the element
            CadCandidate longest = now.OrderByDescending(c => c.Geometry[0].PlanDistanceTo(c.Geometry[1])).First();
            Assert.Equal(longest.Id, orphan.PairedWith);
            Assert.Equal(0.8333, orphan.PairConfidence.Value, 3);
            // nothing is automatic: building a piece now puts a wall inside the one that stands
            Assert.All(u.Of("create"), c =>
            {
                Assert.False(c.Automatic);
                Assert.Equal(CadChange.Split, c.Classification);
                Assert.Equal(1001L, (long)c.Evidence["split_of"]);
            });
            Assert.Contains(CadChange.Split, CadChange.All);
        }

        [Fact]
        public void An_accepted_split_reshapes_the_element_to_its_longest_piece_and_builds_the_rest()
        {
            CadRequirementSet set = Set();
            CadAuditSubject element;
            List<CadCandidate> now;
            CadUpdate offered = Offered(set, out element, out now);
            string keep = offered.Of("orphan").Single().PairedWith;

            CadUpdate u = CadUpdateRules.Plan(now, new[] { element }, set, RevB,
                                              new Dictionary<long, string> { { 1001L, keep } }, new[] { RevA });
            CadUpdateAction reshape = u.Of("set_curve").Single();
            Assert.Equal(1001L, reshape.ElementId);
            Assert.Equal(CadChange.Split, reshape.Classification);
            Assert.True(reshape.Automatic);
            CadUpdateAction piece = u.Of("create").Single();
            Assert.True(piece.Automatic);
            Assert.Equal(1001L, (long)piece.Evidence["split_companion_of"]);
            Assert.Equal(new[] { piece.CandidateId }, ((JArray)reshape.Evidence["split_companions"]).Select(x => (string)x));
            Assert.Empty(u.Of("orphan"));
        }

        [Fact]
        public void An_element_built_under_an_earlier_version_of_the_rules_can_be_split_when_that_version_is_declared()
        {
            // MEASURED (campaign 4): with a new walls version and its lineage declared, the split walls
            // were neither orphans nor pairs - the orphan loop skipped every element of the older rules.
            CadRequirementSet older = Set();
            CadAuditSubject element = Built(Read(older, RevA, Pair(0, 6000)).Single(), older);
            JObject newer = JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1.1.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 2900,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                        'max_thickness_mm': 400, 'min_overlap_fraction': 0.5 } }]
            }".Replace('\'', '"'));
            CadRequirementSet set = CadRequirementSet.Load(newer);
            Assert.NotEqual(older.Sha256, set.Sha256);
            List<CadCandidate> now = Read(set, RevB, Pair(0, 3000), Pair(4000, 6000));

            var undeclared = new CadUpdateScope();
            undeclared.Claimed.Add(1001);
            CadUpdate without = CadUpdateRules.Plan(now, new[] { element }, set, undeclared, null, null, null, null);
            Assert.Empty(without.Of("orphan"));

            var declared = new CadUpdateScope();
            declared.Claimed.Add(1001);
            declared.RulesLineage.Add(older.Sha256);
            CadUpdate with = CadUpdateRules.Plan(now, new[] { element }, set, declared, null, null, null, null);
            Assert.Equal(CadChange.Split, with.Of("orphan").Single().Classification);
        }

        [Fact]
        public void Pieces_that_do_not_cover_half_the_old_line_are_not_a_split()
        {
            CadRequirementSet set = Set();
            CadAuditSubject element = Built(Read(set, RevA, Pair(0, 6000)).Single(), set);
            List<CadCandidate> now = Read(set, RevB, Pair(0, 1200), Pair(4800, 6000));
            CadUpdate u = CadUpdateRules.Plan(now, new[] { element }, set, RevB, lineage: new[] { RevA });
            Assert.Equal(CadChange.Removed, u.Of("orphan").Single().Classification);
            Assert.Null(u.Of("orphan").Single().PairedWith);
        }

        [Fact]
        public void A_piece_off_the_old_line_is_not_part_of_a_split()
        {
            CadRequirementSet set = Set();
            CadAuditSubject element = Built(Read(set, RevA, Pair(0, 6000)).Single(), set);
            // the second piece is 400 mm away: another wall, not this one
            List<CadCandidate> now = Read(set, RevB, Pair(0, 3000), Pair(4000, 6000, 400));
            CadUpdate u = CadUpdateRules.Plan(now, new[] { element }, set, RevB, lineage: new[] { RevA });
            Assert.NotEqual(CadChange.Split, u.Of("orphan").Single().Classification);
        }
    }
}
