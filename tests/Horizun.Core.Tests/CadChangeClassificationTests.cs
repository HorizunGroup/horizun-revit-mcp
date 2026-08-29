// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHAT CHANGED, AS DISTINCT FROM WHAT TO DO ABOUT IT.
//
// The update plan answers the second question with a Kind, and that list is
// deliberately short because several very different pieces of news need the
// same treatment: a wall the drawing retyped, a wall the drawing relayered, and
// a wall somebody moved by hand all end in "a person decides". A reader holding
// only "review" cannot tell those apart, and they are not the same news.
//
// So every action also carries a classification from a closed vocabulary, and
// these tests pin each one against the situation that produces it - including
// the two that exist to STOP an unattended run rather than let it choose:
// ambiguous and conflict.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadChangeClassificationTests
    {
        private const string RevA = "sha-of-revision-a";
        private const string RevB = "sha-of-revision-b";

        private static CadRequirementSet Set(string familyType = null)
        {
            string family = familyType == null ? "" : ", 'family_type': '" + familyType + "'";
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1.0.0', 'title': 'Walls' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000FAMILY,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                        'max_thickness_mm': 400, 'min_overlap_fraction': 0.5 } }]
            }".Replace('\'', '"').Replace("FAMILY", family);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static List<CadSegment> Wall(double x0, double x1, double y = 0, string layer = "A-WALL")
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(x0, y - 100), new CadPoint(x1, y - 100), layer),
                new CadSegment(new CadPoint(x0, y + 100), new CadPoint(x1, y + 100), layer)
            };
        }

        private static List<CadCandidate> Read(List<CadSegment> segs, CadRequirementSet set, string sha)
        {
            return CadInterpretationRules.Interpret(segs, set, sha).Candidates.ToList();
        }

        /// <summary>An element in the model, built from a candidate, sitting where it was put.</summary>
        private static CadAuditSubject Built(CadCandidate from, CadRequirementSet set, string sourceSha,
                                             long elementId, List<CadPoint> movedTo = null,
                                             string typeName = "Generic - 200mm",
                                             double? widthMm = null, long? hostId = null)
        {
            List<CadPoint> where = movedTo ?? from.Geometry;
            return new CadAuditSubject
            {
                ElementId = elementId,
                Category = "Walls",
                TypeName = typeName,
                WidthMm = widthMm,
                HostElementId = hostId,
                Geometry = new List<CadPoint>(where),
                Provenance = new CadProvenance
                {
                    SchemaVersion = 1,
                    CandidateId = from.Id,
                    GeometryId = from.GeometryId,
                    SemanticId = from.SemanticId,
                    RuleId = from.RuleId,
                    Layer = from.Layer,
                    RequirementSetSha256 = set.Sha256,
                    SourceFileSha256 = sourceSha,
                    BuiltGeometry = Serialise(from.Geometry)
                }
            };
        }

        /// <summary>
        /// Provenance stores the as-built geometry the way the writer stores it -
        /// "x,y,z;x,y,z" - and a fixture that invented a different encoding would
        /// simply read back as "no as-built recorded", which is a DIFFERENT case
        /// with a different answer. So the product's own encoder is used.
        /// </summary>
        private static string Serialise(List<CadPoint> points) => CadUpdateRules.Encode(points);

        private static string Of(CadUpdate update, string kind)
        {
            CadUpdateAction a = update.Of(kind).FirstOrDefault();
            return a?.Classification;
        }

        // ------------------------------------------------------------ the easy ones

        [Fact]
        public void The_same_drawing_twice_is_unchanged_and_proposes_NOTHING_automatic_to_do()
        {
            // THE IDEMPOTENCE INVARIANT. Planning the same revision a second time
            // must find nothing to do - an update that proposes work on a model
            // it just built would build it twice.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001) };

            CadUpdate again = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevA), subjects, set, RevA);

            Assert.Equal(CadChange.Unchanged, Of(again, "leave"));
            Assert.Empty(again.Of("create"));
            Assert.Empty(again.Of("orphan"));
            Assert.Empty(again.Of("set_curve"));
            Assert.All(again.Actions, x => Assert.Equal("leave", x.Kind));
        }

        [Fact]
        public void A_wall_only_revision_B_has_is_ADDED()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001) };

            var b = new List<CadSegment>();
            b.AddRange(Wall(0, 6000));
            b.AddRange(Wall(0, 6000, 9000));

            CadUpdate update = CadUpdateRules.Plan(Read(b, set, RevB), subjects, set, RevB, lineage: new[] { RevA });
            Assert.Equal(CadChange.Added, Of(update, "create"));
        }

        [Fact]
        public void A_wall_revision_B_dropped_is_REMOVED_and_never_deleted_automatically()
        {
            CadRequirementSet set = Set();
            var a = new List<CadSegment>();
            a.AddRange(Wall(0, 6000));
            a.AddRange(Wall(0, 6000, 9000));
            List<CadCandidate> read = Read(a, set, RevA);
            var subjects = read.Select((c, i) => Built(c, set, RevA, 1001 + i)).ToList();

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            CadUpdateAction orphan = Assert.Single(update.Of("orphan"));
            Assert.Equal(CadChange.Removed, orphan.Classification);
            Assert.False(orphan.Automatic, "a deletion is never automatic - the two cases look identical");
        }

        // -------------------------------------------------- the ones that were missing

        [Fact]
        public void The_same_shape_on_a_DIFFERENT_LAYER_is_relayered_not_a_delete_and_an_add()
        {
            // The semantic id folds the layer in, so this used to arrive as an
            // orphan plus a create - the plan said one wall was deleted and a
            // different one built, when the drawing said the same wall is now
            // something else.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000, 0, "A-WALL"), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001) };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000, 0, "A-WALL-FIRE"), set, RevB),
                                                   subjects, set, RevB, lineage: new[] { RevA });

            CadUpdateAction action = Assert.Single(update.Actions);
            Assert.Equal(CadChange.Relayered, action.Classification);
            Assert.Equal(1001L, action.ElementId);
            Assert.False(action.Automatic);
            Assert.Equal("A-WALL", (string)action.Evidence["was_layer"]);
            Assert.Equal("A-WALL-FIRE", (string)action.Evidence["now_layer"]);
        }

        [Fact]
        public void A_wall_exactly_where_it_was_but_of_a_DIFFERENT_TYPE_is_retyped()
        {
            // Position alone reported this as "nothing to do". A revision can
            // leave a wall precisely where it is and ask for a different type,
            // which changes thickness, fire rating and cost.
            CadRequirementSet set = Set("Fire - 200mm");
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, typeName: "Generic - 200mm") };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            CadUpdateAction action = Assert.Single(update.Actions);
            Assert.Equal(CadChange.Retyped, action.Classification);
            Assert.False(action.Automatic);
        }

        [Fact]
        public void The_same_type_named_two_ways_is_not_a_retype()
        {
            // Revit reports an instance's type as the type name alone; a set may
            // name it "Family: Type". Reporting that as a change would put a
            // review on every element on every update.
            CadRequirementSet set = Set("Basic Wall: Generic - 200mm");
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, typeName: "Generic - 200mm") };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            Assert.Equal(CadChange.Unchanged, Of(update, "leave"));
        }

        [Fact]
        public void A_door_that_now_lives_in_a_DIFFERENT_WALL_is_rehosted()
        {
            // The drawing puts it exactly where it always was, and the element is
            // in another wall. No comparison of POSITIONS can see this, because
            // the position agrees - which is what made it invisible.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, hostId: 555L) };
            var implied = new Dictionary<string, long> { { a[0].SemanticId, 777L } };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA }, hostBySemanticId: implied);

            CadUpdateAction action = Assert.Single(update.Actions);
            Assert.Equal(CadChange.Rehosted, action.Classification);
            Assert.False(action.Automatic, "re-hosting cuts a new opening and closes the old one");
            Assert.Equal(555L, (long)action.Evidence["hosted_in_now"]);
            Assert.Equal(777L, (long)action.Evidence["drawing_implies_host"]);
        }

        [Fact]
        public void A_door_still_in_the_wall_the_drawing_implies_is_NOT_rehosted()
        {
            // The two commands share one rule for "which wall is this in"
            // precisely so this stays quiet. If they disagreed, every run would
            // report a rehosting against a model nobody had touched.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, hostId: 555L) };
            var implied = new Dictionary<string, long> { { a[0].SemanticId, 555L } };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA }, hostBySemanticId: implied);
            Assert.Equal(CadChange.Unchanged, Of(update, "leave"));
        }

        [Fact]
        public void An_element_with_NO_host_is_never_reported_as_rehosted()
        {
            // A free-standing thing has nothing to be rehosted from, and reading
            // "no host" as "the wrong host" would flag every wall in the model.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, hostId: null) };
            var implied = new Dictionary<string, long> { { a[0].SemanticId, 777L } };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA }, hostBySemanticId: implied);
            Assert.Equal(CadChange.Unchanged, Of(update, "leave"));
        }

        [Fact]
        public void A_run_the_drawing_made_THICKER_is_resized()
        {
            // A partition promoted to a fire wall keeps its line exactly. Position
            // alone reported that as nothing to do, and the model went on carrying
            // the old thickness into every quantity and every clash.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, widthMm: 150.0) };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            CadUpdateAction action = Assert.Single(update.Actions);
            Assert.Equal(CadChange.Resized, action.Classification);
            Assert.False(action.Automatic, "size lives in the type, and changing a type moves every element of it");
            Assert.Equal(200.0, (double)action.Evidence["drawing_asks_mm"], 3);
            Assert.Equal(150.0, (double)action.Evidence["element_measures_mm"], 3);
        }

        [Fact]
        public void An_element_whose_width_CANNOT_be_measured_is_not_reported_as_resized()
        {
            // Null is not zero. "Not comparable" and "the wrong thickness" are
            // different findings, and flattening the first into the second would
            // put a review on every element nobody can measure.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, widthMm: null) };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            Assert.Equal(CadChange.Unchanged, Of(update, "leave"));
        }

        [Fact]
        public void A_width_that_AGREES_is_not_a_change()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, widthMm: 200.0) };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            Assert.Equal(CadChange.Unchanged, Of(update, "leave"));
        }

        [Fact]
        public void A_wall_somebody_moved_while_the_drawing_stood_still_is_MANUALLY_DIVERGED()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var movedByHand = new List<CadPoint> { new CadPoint(0, 750), new CadPoint(6000, 750) };
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001, movedByHand) };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            CadUpdateAction action = Assert.Single(update.Actions);
            Assert.Equal(CadChange.ManuallyDiverged, action.Classification);
            Assert.False(action.Automatic);
        }

        [Fact]
        public void A_wall_the_drawing_moved_a_little_is_MOVED_and_held_for_a_person()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001) };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000, 500), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            CadUpdateAction orphan = Assert.Single(update.Of("orphan"));
            CadUpdateAction create = Assert.Single(update.Of("create"));
            Assert.Equal(CadChange.Moved, orphan.Classification);
            Assert.Equal(CadChange.Moved, create.Classification);
            Assert.False(create.Automatic, "a candidate that may be an existing element moved must not build itself");
        }

        [Fact]
        public void A_wall_the_drawing_lengthened_is_RESHAPED_rather_than_moved()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001) };

            // Same line, 700 mm longer: within the pairing window, not the same shape.
            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6700), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            CadUpdateAction orphan = Assert.Single(update.Of("orphan"));
            Assert.Equal(CadChange.Reshaped, orphan.Classification);
        }

        [Fact]
        public void When_TWO_candidates_could_be_the_same_wall_the_answer_is_AMBIGUOUS()
        {
            // Two plausible partners means the shape cannot choose, and neither
            // can this. Naming it ambiguous is the answer, not the absence of one.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001) };

            var b = new List<CadSegment>();
            b.AddRange(Wall(0, 6000, 400));
            b.AddRange(Wall(0, 6000, -400));

            CadUpdate update = CadUpdateRules.Plan(Read(b, set, RevB), subjects, set, RevB, lineage: new[] { RevA });
            CadUpdateAction orphan = Assert.Single(update.Of("orphan"));
            Assert.Equal(CadChange.Ambiguous, orphan.Classification);
            Assert.All(update.Of("create"), c => Assert.False(c.Automatic));
        }

        [Fact]
        public void A_wall_the_drawing_dropped_AND_somebody_moved_is_a_CONFLICT()
        {
            // Two independent changes to one thing. Reconciling them means knowing
            // which of the two people was right, which is not a fact about a DWG.
            CadRequirementSet set = Set();
            var a = new List<CadSegment>();
            a.AddRange(Wall(0, 6000));
            a.AddRange(Wall(0, 6000, 20000));
            List<CadCandidate> read = Read(a, set, RevA);
            CadCandidate doomed = read.OrderByDescending(c => c.Geometry[0].Y).First();
            CadCandidate kept = read.First(c => !ReferenceEquals(c, doomed));

            var subjects = new List<CadAuditSubject>
            {
                Built(kept, set, RevA, 1001),
                Built(doomed, set, RevA, 1002,
                      new List<CadPoint> { new CadPoint(0, 20900), new CadPoint(6000, 20900) })
            };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevB), subjects, set, RevB,
                                                   lineage: new[] { RevA });
            CadUpdateAction orphan = Assert.Single(update.Of("orphan"));
            Assert.Equal(CadChange.Conflict, orphan.Classification);
            Assert.True((bool)orphan.Evidence["also_moved_by_hand"]);
            Assert.False(orphan.Automatic);
        }

        // -------------------------------------------------------- the vocabulary

        [Fact]
        public void EVERY_action_this_file_produces_carries_a_classification_from_the_closed_list()
        {
            CadRequirementSet set = Set();
            var a = new List<CadSegment>();
            a.AddRange(Wall(0, 6000));
            a.AddRange(Wall(0, 6000, 9000));
            a.AddRange(Wall(0, 6000, 18000));
            List<CadCandidate> read = Read(a, set, RevA);
            var subjects = read.Select((c, i) => Built(c, set, RevA, 1001 + i)).ToList();

            var b = new List<CadSegment>();
            b.AddRange(Wall(0, 6000));            // unchanged
            b.AddRange(Wall(0, 6000, 9400));      // moved
            b.AddRange(Wall(0, 6000, 30000));     // added, and 18000 removed

            CadUpdate update = CadUpdateRules.Plan(Read(b, set, RevB), subjects, set, RevB, lineage: new[] { RevA });

            Assert.NotEmpty(update.Actions);
            Assert.All(update.Actions, x => Assert.Contains(x.Classification, CadChange.All));
        }

        [Fact]
        public void The_counts_report_every_name_including_the_zeros()
        {
            // A key that simply disappears reads as "not measured" rather than
            // "none found", and the difference matters most for conflict.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001) };

            CadUpdate update = CadUpdateRules.Plan(Read(Wall(0, 6000), set, RevA), subjects, set, RevA);
            JObject counts = update.CountsByClassification();

            foreach (string name in CadChange.All) Assert.NotNull(counts[name]);
            Assert.Equal(0, (int)counts[CadChange.Conflict]);
            Assert.Equal(1, (int)counts[CadChange.Unchanged]);
        }

        [Fact]
        public void Accepting_an_ambiguous_pairing_SETTLES_it()
        {
            // A question a person answered must stop being reported as open.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            var subjects = new List<CadAuditSubject> { Built(a[0], set, RevA, 1001) };

            List<CadCandidate> b = Read(Wall(0, 6000, 500), set, RevB);
            CadUpdate offered = CadUpdateRules.Plan(b, subjects, set, RevB, lineage: new[] { RevA });
            string candidateId = offered.Of("orphan").Single().PairedWith;
            Assert.NotNull(candidateId);

            CadUpdate settled = CadUpdateRules.Plan(b, subjects, set, RevB,
                accepted: new Dictionary<long, string> { { 1001L, candidateId } }, lineage: new[] { RevA });

            CadUpdateAction curve = Assert.Single(settled.Of("set_curve"));
            Assert.Equal(CadChange.Moved, curve.Classification);
            Assert.NotEqual(CadChange.Ambiguous, settled.Of("paired_away").Single().Classification);
        }
    }
}
