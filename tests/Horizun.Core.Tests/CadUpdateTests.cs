// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The incremental update, pinned around the one distinction that can destroy
// somebody's work: did the DRAWING move, or did a PERSON move it?
//
// A first conversion that goes wrong is noticed, because nothing was there
// before. An update that goes wrong goes wrong quietly, on top of a week of
// somebody else's modelling - so these are the tests that matter most in this
// repository, and they are written as the four cases rather than as one.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadUpdateTests
    {
        private const string Sha = "sha-of-revision-a";

        private static CadRequirementSet Set()
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'demo', 'version': '1.0.0', 'title': 'Demo' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500,
                                        'min_overlap_mm': 300, 'min_overlap_fraction': 0.5 } }]
            }".Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadSegment Seg(double x1, double y1, double x2, double y2) =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), "A-WALL-EXTR");

        /// <summary>A drawing with one wall whose centreline sits at the given y.</summary>
        private static List<CadCandidate> WallAt(double y) =>
            CadInterpretationRules.Interpret(
                new List<CadSegment> { Seg(0, y - 100, 6000, y - 100), Seg(0, y + 100, 6000, y + 100) },
                Set(), Sha).Candidates;

        private static CadAuditSubject Element(CadCandidate builtFrom, double standsAtY, double? builtAtY,
                                               long id = 500)
        {
            var s = new CadAuditSubject
            {
                ElementId = id,
                Provenance = new CadProvenance
                {
                    SchemaVersion = 1,
                    CandidateId = builtFrom.Id,
                    GeometryId = builtFrom.GeometryId,
                    SemanticId = builtFrom.SemanticId,
                    RuleId = builtFrom.RuleId,
                    Layer = builtFrom.Layer,
                    RequirementSetSha256 = Set().Sha256,
                    SourceFileSha256 = Sha,
                    BuiltGeometry = builtAtY.HasValue
                        ? CadUpdateRules.Encode(new[]
                          {
                              new CadPoint(0, builtAtY.Value), new CadPoint(6000, builtAtY.Value)
                          })
                        : null
                }
            };
            s.Geometry.Add(new CadPoint(0, standsAtY));
            s.Geometry.Add(new CadPoint(6000, standsAtY));
            return s;
        }

        // ------------------------------------------------------- the four cases

        [Fact]
        public void Nothing_moved_leaves_it_alone()
        {
            List<CadCandidate> b = WallAt(0);
            CadUpdate u = CadUpdateRules.Plan(b, new[] { Element(b[0], standsAtY: 0, builtAtY: 0) }, Set(), Sha);

            CadUpdateAction a = Assert.Single(u.Actions);
            Assert.Equal("leave", a.Kind);
            Assert.True(a.Automatic);
            Assert.False(u.NeedsAPerson);
        }

        [Fact]
        public void A_JOINED_wall_is_not_a_wall_the_drawing_moved()
        {
            // The bug this replaced a test for. The first version compared the
            // as-built geometry against the new drawing and called any difference
            // "the drawing moved" - which is TRUE FOR EVERY JOINED WALL, because
            // Revit trims a location curve back to where the centrelines cross,
            // so the as-built line is shorter than the drawing's. It would have
            // proposed undoing every join, on every update, for ever.
            //
            // Matched by semantic id means the drawing did NOT move: that id is
            // derived from the geometry, so a line that moved cannot arrive here.
            List<CadCandidate> b = WallAt(0);
            CadCandidate c = b[0];
            var s = new CadAuditSubject
            {
                ElementId = 500,
                Provenance = new CadProvenance
                {
                    SemanticId = c.SemanticId,
                    RequirementSetSha256 = Set().Sha256,
                    SourceFileSha256 = Sha,
                    // built SHORT, as a join leaves it
                    BuiltGeometry = CadUpdateRules.Encode(new[]
                    {
                        new CadPoint(100, 0), new CadPoint(5900, 0)
                    })
                }
            };
            s.Geometry.Add(new CadPoint(100, 0));
            s.Geometry.Add(new CadPoint(5900, 0));

            CadUpdate u = CadUpdateRules.Plan(b, new[] { s }, Set(), Sha);
            CadUpdateAction a = Assert.Single(u.Actions);
            Assert.Equal("leave", a.Kind);
            Assert.True(a.Automatic);
        }

        [Fact]
        public void A_PERSON_moved_it_and_the_drawing_did_NOT_is_never_touched()
        {
            // The case this whole file exists for. Putting it back would undo
            // somebody's edit to match a drawing that never disagreed with them.
            List<CadCandidate> b = WallAt(0);
            CadUpdate u = CadUpdateRules.Plan(b, new[] { Element(b[0], standsAtY: 350, builtAtY: 0) }, Set(), Sha);

            CadUpdateAction a = Assert.Single(u.Actions);
            Assert.Equal("review", a.Kind);
            Assert.False(a.Automatic);
            Assert.True(u.NeedsAPerson);
            Assert.Contains("A PERSON MOVED THIS", a.Says);
            Assert.Contains("must never do on its own", a.Says);
            // and the evidence shows all three positions, so the reader can judge
            Assert.NotNull(a.Evidence["drawing_says_mm"]);
            Assert.NotNull(a.Evidence["element_is_at_mm"]);
            Assert.NotNull(a.Evidence["was_built_at_mm"]);
        }

        [Fact]
        public void A_MOVED_wall_is_offered_as_a_PAIRING_and_never_taken_as_one()
        {
            // Nothing in a DWG says the wall in revision B is the wall from
            // revision A: there is no handle anywhere in the Revit CAD API, and
            // that was measured, not assumed. So the plan reports both halves and
            // names the resemblance it found, with what it judged on.
            List<CadCandidate> a = WallAt(0);
            List<CadCandidate> b = WallAt(500);
            CadUpdate u = CadUpdateRules.Plan(b, new[] { Element(a[0], standsAtY: 0, builtAtY: 0) }, Set(), Sha);

            Assert.Equal(1, u.Count("create"));
            CadUpdateAction orphan = Assert.Single(u.Of("orphan"));
            Assert.False(orphan.Automatic);
            Assert.Equal(u.Of("create").First().CandidateId, orphan.PairedWith);
            Assert.InRange(orphan.PairConfidence.Value, 0.01, 1.0);
            Assert.Contains("same layer and rule", (string)orphan.Evidence["paired_on"]);
            Assert.Contains("judgement offered, not taken", orphan.Says);
        }

        [Fact]
        public void An_ACCEPTED_pairing_re_shapes_the_element_instead_of_duplicating_it()
        {
            List<CadCandidate> a = WallAt(0);
            List<CadCandidate> b = WallAt(500);
            CadAuditSubject element = Element(a[0], standsAtY: 0, builtAtY: 0);

            CadUpdate proposal = CadUpdateRules.Plan(b, new[] { element }, Set(), Sha);
            string candidate = proposal.Of("create").First().CandidateId;

            CadUpdate accepted = CadUpdateRules.Plan(b, new[] { element }, Set(), Sha,
                new Dictionary<long, string> { { 500L, candidate } });

            CadUpdateAction move = Assert.Single(accepted.Of("set_curve"));
            Assert.True(move.Automatic);
            Assert.Equal(500L, move.ElementId.Value);
            Assert.Equal(500, move.Geometry[0].Y, 3);
            Assert.Contains("keeps its id", move.Says);
            Assert.Empty(accepted.Of("create"));
            Assert.Empty(accepted.Of("orphan"));
            Assert.Empty(accepted.Rejected);
        }

        [Fact]
        public void A_pairing_accepted_against_a_DIFFERENT_plan_is_refused_by_name()
        {
            List<CadCandidate> b = WallAt(500);
            CadUpdate u = CadUpdateRules.Plan(b, new[] { Element(WallAt(0)[0], standsAtY: 0, builtAtY: 0) },
                Set(), Sha, new Dictionary<long, string> { { 999L, "cadrev:from-another-plan" } });

            Assert.Single(u.Rejected);
            Assert.Contains("no such orphan", u.Rejected[0]);
            Assert.Contains("whichever element happens to carry that id now", u.Rejected[0]);
            // and nothing was paired
            Assert.Empty(u.Of("set_curve"));
            Assert.Equal(1, u.Count("orphan"));
        }

        [Fact]
        public void A_wall_TOO_FAR_from_the_orphan_is_not_offered_as_the_same_wall()
        {
            List<CadCandidate> a = WallAt(0);
            List<CadCandidate> b = WallAt(9000);   // 9 m away: some other wall
            CadUpdate u = CadUpdateRules.Plan(b, new[] { Element(a[0], standsAtY: 0, builtAtY: 0) }, Set(), Sha);

            CadUpdateAction orphan = Assert.Single(u.Of("orphan"));
            Assert.Null(orphan.PairedWith);
            Assert.Equal(1, u.Count("create"));
        }

        // ------------------------------------------------- without the as-built

        [Fact]
        public void With_NO_as_built_record_the_update_says_what_it_cannot_answer()
        {
            // An element this bridge created before it recorded what it built.
            // The drawing still says what it was built from, so this update has
            // nothing to do - but whether somebody has moved it since is a
            // question this command cannot answer, and it says which command can
            // rather than implying the element is untouched.
            List<CadCandidate> b = WallAt(0);
            CadUpdate u = CadUpdateRules.Plan(b, new[] { Element(b[0], standsAtY: 350, builtAtY: null) }, Set(), Sha);

            CadUpdateAction a = Assert.Single(u.Actions);
            Assert.Equal("leave", a.Kind);
            Assert.Contains("cannot be answered here", a.Says);
            Assert.Contains("horizun_audit_cad_model", a.Says);
            Assert.False((bool)a.Evidence["as_built_recorded"]);
        }

        [Fact]
        public void With_no_as_built_record_and_no_difference_there_is_still_nothing_to_do()
        {
            List<CadCandidate> b = WallAt(0);
            CadUpdate u = CadUpdateRules.Plan(b, new[] { Element(b[0], standsAtY: 0, builtAtY: null) }, Set(), Sha);
            CadUpdateAction a = Assert.Single(u.Actions);
            Assert.Equal("leave", a.Kind);
            Assert.True(a.Automatic);
        }

        [Fact]
        public void The_person_who_moved_it_is_still_protected_when_the_drawing_did_not_change()
        {
            // The case this whole file exists for, and the one the rewrite must
            // not have lost: the drawing says what it always said, the element
            // does not, and putting it back would undo somebody's edit.
            List<CadCandidate> b = WallAt(0);
            CadUpdate u = CadUpdateRules.Plan(b, new[] { Element(b[0], standsAtY: 350, builtAtY: 0) }, Set(), Sha);

            CadUpdateAction a = Assert.Single(u.Actions);
            Assert.Equal("review", a.Kind);
            Assert.False(a.Automatic);
            Assert.Contains("A PERSON MOVED THIS", a.Says);
        }

        // ------------------------------------------------------ appear, disappear

        [Fact]
        public void An_entity_revision_B_adds_is_a_create()
        {
            List<CadCandidate> b = WallAt(0);
            CadUpdate u = CadUpdateRules.Plan(b, new CadAuditSubject[0], Set(), Sha);
            CadUpdateAction a = Assert.Single(u.Actions);
            Assert.Equal("create", a.Kind);
            Assert.True(a.Automatic);
            Assert.Equal(2, a.Geometry.Count);
        }

        [Fact]
        public void An_element_revision_B_no_longer_says_is_an_ORPHAN_and_never_a_deletion()
        {
            List<CadCandidate> a = WallAt(0);
            CadUpdate u = CadUpdateRules.Plan(new List<CadCandidate>(),
                                              new[] { Element(a[0], standsAtY: 0, builtAtY: 0) }, Set(), Sha);
            CadUpdateAction action = Assert.Single(u.Actions);
            Assert.Equal("orphan", action.Kind);
            Assert.False(action.Automatic);
            Assert.Contains("Deleting is never automatic", action.Says);
            Assert.Contains("look identical from the outside", action.Says);
        }



        [Fact]
        public void An_element_from_another_drawing_is_not_this_updates_business()
        {
            List<CadCandidate> a = WallAt(0);
            CadAuditSubject foreign = Element(a[0], standsAtY: 0, builtAtY: 0);
            foreign.Provenance.SourceFileSha256 = "sha-of-a-different-drawing";

            CadUpdate u = CadUpdateRules.Plan(new List<CadCandidate>(), new[] { foreign }, Set(), Sha);
            Assert.Empty(u.Actions);
        }

        [Fact]
        public void An_element_built_under_other_rules_is_not_this_updates_business_either()
        {
            List<CadCandidate> a = WallAt(0);
            CadAuditSubject foreign = Element(a[0], standsAtY: 0, builtAtY: 0);
            foreign.Provenance.RequirementSetSha256 = "sha-of-somebody-elses-rules";

            CadUpdate u = CadUpdateRules.Plan(new List<CadCandidate>(), new[] { foreign }, Set(), Sha);
            Assert.Empty(u.Actions);
        }

        // ------------------------------------------------------------ the record

        [Fact]
        public void The_as_built_string_round_trips_exactly()
        {
            var points = new[] { new CadPoint(908176.2125, -176.2125, 0), new CadPoint(900000, 0, 0) };
            string encoded = CadUpdateRules.Encode(points);
            Assert.Contains("908176.2125", encoded);

            // Read back through the only path that reads it: an element whose
            // provenance carries this string and whose drawing says the same
            // thing must come back as "leave".
            List<CadCandidate> b = WallAt(0);
            var s = new CadAuditSubject
            {
                ElementId = 1,
                Provenance = new CadProvenance
                {
                    SemanticId = b[0].SemanticId,
                    RequirementSetSha256 = Set().Sha256,
                    SourceFileSha256 = Sha,
                    BuiltGeometry = CadUpdateRules.Encode(b[0].Geometry)
                }
            };
            foreach (CadPoint p in b[0].Geometry) s.Geometry.Add(p);

            CadUpdate u = CadUpdateRules.Plan(b, new[] { s }, Set(), Sha);
            Assert.Equal("leave", Assert.Single(u.Actions).Kind);
        }

        [Fact]
        public void An_empty_as_built_string_reads_as_NOT_RECORDED_rather_than_as_a_position()
        {
            // "" must not read as "recorded, and it was at the origin" - that
            // would make an untouched element look moved and a moved one look
            // untouched, depending on where the building sits.
            List<CadCandidate> b = WallAt(0);
            CadAuditSubject s = Element(b[0], standsAtY: 350, builtAtY: null);
            s.Provenance.BuiltGeometry = "";     // what an older stamp leaves behind

            CadUpdate u = CadUpdateRules.Plan(b, new[] { s }, Set(), Sha);
            CadUpdateAction a = Assert.Single(u.Actions);
            Assert.Equal("leave", a.Kind);
            Assert.False((bool)a.Evidence["as_built_recorded"]);
            Assert.Contains("cannot be answered here", a.Says);
        }

        [Fact]
        public void A_wall_built_the_other_way_round_is_not_a_wall_somebody_moved()
        {
            List<CadCandidate> b = WallAt(0);
            CadAuditSubject s = Element(b[0], standsAtY: 0, builtAtY: 0);
            s.Geometry.Reverse();

            CadUpdate u = CadUpdateRules.Plan(b, new[] { s }, Set(), Sha);
            Assert.Equal("leave", Assert.Single(u.Actions).Kind);
        }

        [Fact]
        public void Two_elements_claiming_one_entity_do_not_both_get_updated()
        {
            List<CadCandidate> b = WallAt(500);
            CadUpdate u = CadUpdateRules.Plan(b, new[]
            {
                Element(b[0], standsAtY: 0, builtAtY: 0, id: 500),
                Element(b[0], standsAtY: 0, builtAtY: 0, id: 501)
            }, Set(), Sha);

            // One is the entity; the other is a duplicate the drawing does not
            // account for, and it is REPORTED rather than quietly acted on twice.
            Assert.Equal(1, u.Count("leave"));
            CadUpdateAction spare = Assert.Single(u.Of("orphan"));
            Assert.False(spare.Automatic);
            Assert.Equal(501, spare.ElementId.Value);
        }

        // ------------------------------------------------------------- lineage

        [Fact]
        public void A_NEW_REVISION_is_a_different_file_and_its_predecessor_must_be_named()
        {
            // The first version filtered by "same file hash", which is true of no
            // incremental update ever: a new revision IS a different file. Every
            // element from revision A was excluded, so the plan reported the whole
            // existing model as untouched and revision B as entirely new work -
            // it would have built a second copy of the building.
            const string ShaB = "sha-of-revision-b";
            List<CadCandidate> b = WallAt(500);
            CadAuditSubject fromA = Element(WallAt(0)[0], standsAtY: 0, builtAtY: 0);   // carries revision A's sha

            CadUpdate blind = CadUpdateRules.Plan(b, new[] { fromA }, Set(), ShaB);
            Assert.Empty(blind.Of("orphan"));      // invisible without the lineage
            Assert.Equal(1, blind.Count("create"));

            CadUpdate told = CadUpdateRules.Plan(b, new[] { fromA }, Set(), ShaB, null, new[] { Sha });
            Assert.Equal(1, told.Count("orphan"));
            Assert.Equal(1, told.Count("create"));
            Assert.NotNull(Assert.Single(told.Of("orphan")).PairedWith);
        }

        [Fact]
        public void A_drawing_OUTSIDE_the_lineage_is_still_left_alone()
        {
            const string ShaB = "sha-of-revision-b";
            CadAuditSubject stranger = Element(WallAt(0)[0], standsAtY: 0, builtAtY: 0);
            stranger.Provenance.SourceFileSha256 = "sha-of-a-drawing-nobody-mentioned";

            CadUpdate u = CadUpdateRules.Plan(new List<CadCandidate>(), new[] { stranger }, Set(), ShaB,
                                              null, new[] { Sha });
            Assert.Empty(u.Actions);
        }
    }
}
