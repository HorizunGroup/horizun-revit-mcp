// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHERE A CHANGE CAME FROM: THE DRAWING, THE READING, THE RULES OR A PERSON.
//
// MEASURED on this campaign: a build that read two collinear wall pieces as one
// wall, run against the SAME drawing bytes, produced reshaped, removed and added
// rows - a revision of the drawing, as far as the plan could tell, when the
// drawing had not changed. Provenance v3 records which reading built an element,
// so the plan can say it, and a change that only the reading produced is held.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadChangeOriginTests
    {
        private const string RevA = "sha-of-revision-a";
        private const string RevB = "sha-of-revision-b";
        private const string ReadingOld = "cadread:old:000000000000";
        private const string ReadingNew = "cadread:new:111111111111";

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

        private static List<CadCandidate> Wall(CadRequirementSet set, double y, string sha)
        {
            var segs = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, y - 100), new CadPoint(6000, y - 100), "A-WALL"),
                new CadSegment(new CadPoint(0, y + 100), new CadPoint(6000, y + 100), "A-WALL")
            };
            return CadInterpretationRules.Interpret(segs, set, sha).Candidates.ToList();
        }

        private static CadAuditSubject Built(CadCandidate from, CadRequirementSet set, string sha, string reading,
                                             List<CadPoint> standsAt = null) => new CadAuditSubject
        {
            ElementId = 1001,
            Category = "Walls",
            TypeName = "Generic - 200mm",
            Geometry = new List<CadPoint>(standsAt ?? from.Geometry),
            Provenance = new CadProvenance
            {
                SchemaVersion = reading == null ? 2 : 3,
                CandidateId = from.Id, GeometryId = from.GeometryId, SemanticId = from.SemanticId,
                RuleId = from.RuleId, Layer = from.Layer,
                RequirementSetSha256 = set.Sha256, SourceFileSha256 = sha,
                BuiltGeometry = CadUpdateRules.Encode(from.Geometry),
                InterpretationVersion = reading
            }
        };

        /// <summary>A wall the new reading places 500 mm away, with the pairing accepted: a set_curve.</summary>
        private static CadUpdate Moved(CadRequirementSet set, string builtSha, string builtReading, string nowSha,
                                       List<CadPoint> standsAt = null)
        {
            CadAuditSubject element = Built(Wall(set, 0, builtSha)[0], set, builtSha, builtReading, standsAt);
            List<CadCandidate> now = Wall(set, 500, nowSha);
            string[] lineage = nowSha == builtSha ? null : new[] { builtSha };
            CadUpdate offered = CadUpdateRules.Plan(now, new[] { element }, set, nowSha, lineage: lineage);
            string candidate = offered.Of("create").First().CandidateId;
            return CadUpdateRules.Plan(now, new[] { element }, set, nowSha,
                                       new Dictionary<long, string> { { 1001L, candidate } }, lineage);
        }

        private static CadAuditSubject Subject(CadRequirementSet set, string sha, string reading) =>
            Built(Wall(set, 0, sha)[0], set, sha, reading);

        [Fact]
        public void Unchanged_is_no_change_whatever_the_reading()
        {
            CadRequirementSet set = Set();
            CadAuditSubject s = Subject(set, RevA, ReadingOld);
            CadUpdate u = CadUpdateRules.Plan(Wall(set, 0, RevA), new[] { s }, set, RevA);
            JObject o = CadUpdateRules.AttributeOrigins(u, new[] { s }, set, RevA, ReadingNew);
            Assert.Equal("none", (string)u.Actions.Single().Evidence["change_origin"]);
            Assert.Equal(1, (int)o["none"]);
            Assert.Equal(0, (int)o["held_because_of_the_reading"]);
        }

        [Fact]
        public void The_same_bytes_read_differently_are_held_as_reinterpreted()
        {
            CadRequirementSet set = Set();
            CadUpdate u = Moved(set, RevA, ReadingOld, RevA);
            Assert.True(u.Of("set_curve").Single().Automatic);   // what the plan alone would do

            var subjects = new[] { Subject(set, RevA, ReadingOld) };
            JObject o = CadUpdateRules.AttributeOrigins(u, subjects, set, RevA, ReadingNew);
            Assert.Empty(u.Of("set_curve"));
            CadUpdateAction held = u.Actions.Single(a => a.ElementId == 1001 && a.Kind == "review");
            Assert.Equal(CadChange.Reinterpreted, held.Classification);
            Assert.False(held.Automatic);
            Assert.Equal("reading", (string)held.Evidence["change_origin"]);
            Assert.Equal(ReadingOld, (string)held.Evidence["reading_built_with"]);
            Assert.Contains("how the drawing is READ now", held.Says);
            Assert.Equal(1, (int)o["held_because_of_the_reading"]);
        }

        [Fact]
        public void The_same_bytes_and_the_same_reading_leave_only_a_person()
        {
            // A pairing a person accepted over unchanged bytes and an unchanged reading.
            CadRequirementSet set = Set();
            CadUpdate u = Moved(set, RevA, ReadingNew, RevA);
            CadUpdateRules.AttributeOrigins(u, new[] { Subject(set, RevA, ReadingNew) }, set, RevA, ReadingNew);
            CadUpdateAction curve = u.Of("set_curve").Single();
            Assert.True(curve.Automatic);
            Assert.Equal("person", (string)curve.Evidence["change_origin"]);
        }

        [Fact]
        public void A_size_the_record_never_kept_is_not_blamed_on_a_person()
        {
            CadRequirementSet set = Set();
            var u = new CadUpdate();
            u.Actions.Add(new CadUpdateAction { ElementId = 1001, Kind = "review", Classification = CadChange.Resized,
                                                Says = "held." });
            CadUpdateRules.AttributeOrigins(u, new[] { Subject(set, RevA, ReadingNew) }, set, RevA, ReadingNew);
            Assert.Equal("unknown", (string)u.Actions.Single().Evidence["change_origin"]);
        }

        [Fact]
        public void The_same_bytes_with_no_recorded_reading_are_held_too()
        {
            // Nothing but the reading or the rules can move a line of unchanged bytes.
            CadRequirementSet set = Set();
            CadUpdate u = Moved(set, RevA, null, RevA);
            CadUpdateRules.AttributeOrigins(u, new[] { Subject(set, RevA, null) }, set, RevA, ReadingNew);
            Assert.Equal("reading",
                         (string)u.Actions.Single(a => a.ElementId == 1001 && a.Kind == "review").Evidence["change_origin"]);
            Assert.Empty(u.Of("set_curve"));
        }

        [Fact]
        public void A_new_drawing_under_the_same_reading_is_the_drawing_and_stays_automatic()
        {
            CadRequirementSet set = Set();
            CadUpdate u = Moved(set, RevA, ReadingNew, RevB);
            CadUpdateRules.AttributeOrigins(u, new[] { Subject(set, RevA, ReadingNew) }, set, RevB, ReadingNew);
            CadUpdateAction curve = u.Of("set_curve").Single();
            Assert.True(curve.Automatic);
            Assert.Equal(CadChange.Moved, curve.Classification);
            Assert.Equal("drawing", (string)curve.Evidence["change_origin"]);
        }

        [Fact]
        public void A_new_drawing_AND_a_new_reading_cannot_be_separated_and_are_held()
        {
            CadRequirementSet set = Set();
            CadUpdate u = Moved(set, RevA, ReadingOld, RevB);
            CadUpdateRules.AttributeOrigins(u, new[] { Subject(set, RevA, ReadingOld) }, set, RevB, ReadingNew);
            CadUpdateAction curve = u.Of("set_curve").Single();
            Assert.False(curve.Automatic);
            Assert.Equal(CadChange.Moved, curve.Classification);
            Assert.Equal("drawing_and_reading", (string)curve.Evidence["change_origin"]);
            Assert.Contains("cannot say how much of the difference is which", curve.Says);
        }

        [Fact]
        public void An_accepted_placement_move_over_the_same_bytes_is_the_placement()
        {
            CadRequirementSet set = Set();
            CadUpdate u = Moved(set, RevA, ReadingOld, RevA);
            CadUpdateRules.AttributeOrigins(u, new[] { Subject(set, RevA, ReadingOld) }, set, RevA, ReadingNew,
                                            placementMoved: true);
            CadUpdateAction curve = u.Of("set_curve").Single();
            Assert.True(curve.Automatic);
            Assert.Equal("placement", (string)curve.Evidence["change_origin"]);
        }

        [Fact]
        public void A_wall_the_new_reading_no_longer_produces_is_reinterpreted_not_removed()
        {
            CadRequirementSet set = Set();
            CadAuditSubject s = Subject(set, RevA, ReadingOld);
            CadUpdate u = CadUpdateRules.Plan(new List<CadCandidate>(), new[] { s }, set, RevA);
            Assert.Equal(CadChange.Removed, u.Of("orphan").Single().Classification);
            CadUpdateRules.AttributeOrigins(u, new[] { s }, set, RevA, ReadingNew);
            Assert.Empty(u.Of("orphan"));
            Assert.Equal(CadChange.Reinterpreted, u.Of("review").Single().Classification);
        }

        [Fact]
        public void A_person_is_a_person_and_the_vocabulary_names_every_origin()
        {
            CadRequirementSet set = Set();
            CadCandidate a = Wall(set, 0, RevA)[0];
            var moved = new List<CadPoint> { new CadPoint(0, 300), new CadPoint(6000, 300) };
            CadAuditSubject s = Built(a, set, RevA, ReadingNew, moved);
            CadUpdate u = CadUpdateRules.Plan(Wall(set, 0, RevA), new[] { s }, set, RevA);
            JObject o = CadUpdateRules.AttributeOrigins(u, new[] { s }, set, RevA, ReadingNew);
            CadUpdateAction review = u.Actions.Single(x => x.ElementId == 1001);
            Assert.Equal(CadChange.ManuallyDiverged, review.Classification);
            Assert.Equal("person", (string)review.Evidence["change_origin"]);
            foreach (string origin in CadUpdateRules.Origins) Assert.NotNull(o[origin]);
            Assert.Contains(CadChange.Reinterpreted, CadChange.All);
        }
    }
}
