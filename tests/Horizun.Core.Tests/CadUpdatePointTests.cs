// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE UPDATE ROUTE, FOR THINGS THAT ARE A POINT.
//
// MEASURED on a controlled revision of one apartment - a device moved, one
// rotated, one added, one removed, a host wall moved, and two devices moved by
// hand in Revit: the device moved both in the drawing and by hand came back as a
// plain removal (conflict detection asked for two points); no pairing was offered
// for a moved device, so the apply would have built a second one; and the rotated
// device came back unchanged, because a symbol's identity is its position.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadUpdatePointTests
    {
        private const string Sha = "sha-of-revision-a";

        private static CadRequirementSet Set()
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'devices', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5,
                              'revision_compare_mm': 10 },
              'rules': [{ 'id': 'r-dev', 'layers': ['E-P'], 'produces': 'electrical_fixture',
                          'family_type': 'Duplex Receptacle: Standard', 'level': 'Level 1', 'hosted_on': 'wall',
                          'geometry': { 'from': 'blocks', 'blocks': ['OUT'] } }]
            }".Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        /// <summary>A receptacle drawn on the north side of a wall along y = 0.</summary>
        private static CadCandidate Symbol(double x, double y = 120, double rotationDegrees = 0)
        {
            var reading = CadBlockRules.Interpret(new List<CadIrEntity>
            {
                new CadIrEntity
                {
                    Id = "i" + x, Kind = CadEntityKind.BlockInstance, Layer = "E-P", BlockName = "OUT",
                    RotationRadians = rotationDegrees * System.Math.PI / 180.0, ScaleX = 1, ScaleY = 1,
                    Points = { new CadPoint(x, y, 0) }
                }
            }, Set(), Sha);
            CadCandidate c = reading.Candidates.Single();
            c.MirrorResolution = "not_mirrored";
            return c;
        }

        /// <summary>The device built from a symbol, projected onto the wall's north face.</summary>
        private static CadAuditSubject Built(CadCandidate from, double x, double builtX, long id = 900,
                                             double handX = 1)
        {
            var s = new CadAuditSubject
            {
                ElementId = id,
                TypeName = "Standard",
                HostElementId = 700,
                HandPlan = new CadVector(handX, 0),
                IsMirrored = false,
                Provenance = new CadProvenance
                {
                    SchemaVersion = 2,
                    CandidateId = from.Id, SemanticId = from.SemanticId, GeometryId = from.GeometryId,
                    RuleId = from.RuleId, Layer = from.Layer,
                    RequirementSetSha256 = Set().Sha256, SourceFileSha256 = Sha,
                    BuiltGeometry = CadUpdateRules.Encode(new[] { new CadPoint(builtX, 76.2, 0) })
                }
            };
            s.Geometry.Add(new CadPoint(x, 76.2, 0));
            s.HostLine.Add(new CadPoint(0, 0));
            s.HostLine.Add(new CadPoint(6000, 0));
            return s;
        }

        [Fact]
        public void A_copy_a_person_called_new_is_not_the_partner_of_an_erased_device()
        {
            // MEASURED on the controlled revision: 1588C erased, 1653C copied 668 mm away.
            // The copy was offered as "1588C moved"; after a person rejected that pairing
            // the erased device still read "moved", so it could not be decided as removed.
            CadCandidate erased = Symbol(1000);
            CadCandidate copy = Symbol(1600);
            var subjects = new[] { Built(erased, 1000, 1000) };
            CadUpdate offered = CadUpdateRules.Plan(new List<CadCandidate> { copy }, subjects, Set(), Sha);
            Assert.Equal(CadChange.Moved, offered.Of("orphan").Single().Classification);

            CadUpdate decided = CadUpdateRules.Plan(new List<CadCandidate> { copy }, subjects, Set(), Sha,
                                                    null, null, new[] { copy.Id });
            CadUpdateAction orphan = decided.Of("orphan").Single();
            Assert.Equal(CadChange.Removed, orphan.Classification);
            Assert.Null(orphan.PairedWith);
            CadUpdateAction create = decided.Of("create").Single();
            Assert.Equal(true, (bool?)create.Evidence["pairing_rejected"]);
            Assert.True(create.Automatic);
        }

        [Fact]
        public void A_device_the_drawing_dropped_and_a_person_moved_is_a_conflict()
        {
            CadCandidate a = Symbol(1000);
            CadUpdate u = CadUpdateRules.Plan(new List<CadCandidate>(), new[] { Built(a, 1100, 1000) }, Set(), Sha);

            CadUpdateAction orphan = Assert.Single(u.Of("orphan"));
            Assert.Equal(CadChange.Conflict, orphan.Classification);
            Assert.True((bool)orphan.Evidence["also_moved_by_hand"]);
        }

        [Fact]
        public void A_device_the_drawing_moved_is_offered_as_a_pairing_and_its_create_is_held()
        {
            CadCandidate a = Symbol(1000);
            CadCandidate b = Symbol(1300);
            CadUpdate u = CadUpdateRules.Plan(new[] { b }, new[] { Built(a, 1000, 1000) }, Set(), Sha);

            CadUpdateAction create = Assert.Single(u.Of("create"));
            CadUpdateAction orphan = Assert.Single(u.Of("orphan"));
            Assert.False(create.Automatic);
            Assert.Equal(CadChange.Moved, orphan.Classification);
            Assert.Equal(create.CandidateId, orphan.PairedWith);
            Assert.Contains("second device", create.Says);
        }

        [Fact]
        public void An_accepted_pairing_moves_the_device_along_its_wall_and_keeps_its_id()
        {
            CadCandidate a = Symbol(1000);
            CadCandidate b = Symbol(1300);
            CadAuditSubject element = Built(a, 1000, 1000);
            CadUpdate u = CadUpdateRules.Plan(new[] { b }, new[] { element }, Set(), Sha,
                new Dictionary<long, string> { { 900L, b.Id } });

            CadUpdateAction move = Assert.Single(u.Of("move"));
            Assert.True(move.Automatic);
            Assert.Equal(900L, move.ElementId.Value);
            Assert.Equal(300, move.Vector.Value.X, 3);
            Assert.Equal(0, move.Vector.Value.Y, 3);     // along the wall only: it stays on its face
            Assert.Empty(u.Of("create"));
            Assert.Empty(u.Rejected);
        }

        [Fact]
        public void Accepting_a_pairing_on_a_hand_moved_device_moves_it_from_where_it_is_now()
        {
            CadCandidate a = Symbol(1000);
            CadCandidate b = Symbol(1300);
            CadAuditSubject element = Built(a, 1100, 1000);   // moved 100 mm by hand
            CadUpdate u = CadUpdateRules.Plan(new[] { b }, new[] { element }, Set(), Sha,
                new Dictionary<long, string> { { 900L, b.Id } });

            CadUpdateAction move = Assert.Single(u.Of("move"));
            Assert.Equal(200, move.Vector.Value.X, 3);
            Assert.Equal(CadChange.Conflict, move.Classification);
            Assert.Contains("moved by hand", move.Says);
        }

        [Fact]
        public void A_symbol_redrawn_on_the_other_side_of_the_wall_is_not_a_move()
        {
            CadCandidate a = Symbol(1000);
            CadCandidate b = Symbol(1300, y: -120);
            CadUpdate u = CadUpdateRules.Plan(new[] { b }, new[] { Built(a, 1000, 1000) }, Set(), Sha,
                new Dictionary<long, string> { { 900L, b.Id } });

            Assert.Empty(u.Of("move"));
            Assert.Contains(u.Rejected, r => r.Contains("other"));
        }

        [Fact]
        public void A_device_further_than_two_metres_is_not_offered_as_the_same_device()
        {
            CadCandidate a = Symbol(1000);
            CadCandidate b = Symbol(3500);
            CadUpdate u = CadUpdateRules.Plan(new[] { b }, new[] { Built(a, 1000, 1000) }, Set(), Sha);

            Assert.True(u.Of("create").Single().Automatic);
            Assert.Null(u.Of("orphan").Single().PairedWith);
        }

        [Fact]
        public void A_symbol_turned_round_in_place_is_reoriented_not_unchanged()
        {
            CadCandidate b = Symbol(1000, rotationDegrees: 0);          // now points +x
            CadUpdate u = CadUpdateRules.Plan(new[] { b }, new[] { Built(b, 1000, 1000, handX: -1) }, Set(), Sha);

            CadUpdateAction r = Assert.Single(u.Actions);
            Assert.Equal(CadChange.Reoriented, r.Classification);
            Assert.Equal("review", r.Kind);
            Assert.False(r.Automatic);
            Assert.Equal(180, (double)r.Evidence["hand_off_degrees"], 1);
        }

        [Fact]
        public void A_device_that_still_faces_the_drawn_way_is_unchanged()
        {
            CadCandidate b = Symbol(1000, rotationDegrees: 0);
            CadUpdate u = CadUpdateRules.Plan(new[] { b }, new[] { Built(b, 1000, 1000, handX: 1) }, Set(), Sha);

            Assert.Equal(CadChange.Unchanged, Assert.Single(u.Actions).Classification);
        }

        [Fact]
        public void Reoriented_is_in_the_closed_vocabulary()
        {
            Assert.Contains(CadChange.Reoriented, CadChange.All);
            CadUpdate u = CadUpdateRules.Plan(new List<CadCandidate>(), new List<CadAuditSubject>(), Set(), Sha);
            Assert.Equal(0, (int)u.CountsByClassification()[CadChange.Reoriented]);
        }
    }
}
