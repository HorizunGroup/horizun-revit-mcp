// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHICH PLACEMENT of a drawing is this update about?
//
// Backlog 8.4d: a file linked TWICE - a repeated wing - gives both placements
// the same source hash, and scope was per file, so an update for one placement
// claimed the other's elements and proposed to orphan them. Backlog 8.4c: an
// embedded import has no path and no hash, so every element it produced fell
// out of scope and the update reported "0 of everything" as if it had looked.
//
// Provenance v2 keeps file, placement and transform APART, and these tests are
// the desk-side proof of what each combination means. Each was written to fail
// against the file-scoped predicate first.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadPlacementScopeTests
    {
        private const string FileX = "sha-of-file-x";
        private const string FileOld = "sha-of-the-earlier-issue";
        private const string P1 = "uid-placement-1";
        private const string P2 = "uid-placement-2";
        private const string Identity = "1,0,0;0,1,0;1";

        private static CadRequirementSet Set()
        {
            return CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1.0.0', 'title': 'Walls' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'walls', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000,
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 100,
                                        'max_thickness_mm': 400, 'min_overlap_fraction': 0.5 } }]
            }".Replace('\'', '"')));
        }

        /// <summary>One wall, drawn as two lines, optionally shifted along x - which is what a moved placement produces.</summary>
        private static List<CadSegment> Wall(double dx = 0)
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(dx, -100), new CadPoint(dx + 6000, -100), "A-WALL"),
                new CadSegment(new CadPoint(dx, 100), new CadPoint(dx + 6000, 100), "A-WALL")
            };
        }

        private static List<CadCandidate> Read(CadRequirementSet set, string sha, double dx = 0) =>
            CadInterpretationRules.Interpret(Wall(dx), set, sha).Candidates.ToList();

        /// <summary>A placement as the model holds it now.</summary>
        private static CadPlacement Placement(long instanceId, string uid, string sha, double originX = 0,
                                              string path = "C:\\drawings\\x.dwg", string fingerprint = null)
        {
            return new CadPlacement
            {
                ElementId = instanceId,
                PlacementId = uid,
                FileSha256 = sha,
                ExternalPath = path,
                SourceFingerprint = fingerprint ?? ("cadsrc:" + uid + ":" + originX),
                TransformFingerprint = "cadtf:" + originX.ToString("0.###"),
                OriginMm = new[] { originX, 0.0, 0.0 },
                BasisX = new[] { 1.0, 0.0, 0.0 },
                BasisY = new[] { 0.0, 1.0, 0.0 },
                Scale = 1.0
            };
        }

        /// <summary>An element stamped v2: it knows which placement built it and where that placement sat.</summary>
        private static CadAuditSubject BuiltV2(CadCandidate from, CadRequirementSet set, string sha, long id,
                                               string placementId, double placementOriginX = 0)
        {
            CadAuditSubject s = BuiltV1(from, set, sha, id);
            s.Provenance.SchemaVersion = 2;
            s.Provenance.PlacementId = placementId;
            s.Provenance.PlacementTransform = "cadtf:" + placementOriginX.ToString("0.###");
            s.Provenance.PlacementOrigin = CadPlacementRules.EncodeOrigin(new[] { placementOriginX, 0.0, 0.0 });
            s.Provenance.PlacementBasis = Identity;
            return s;
        }

        /// <summary>An element stamped by the previous release: no placement id, no transform.</summary>
        private static CadAuditSubject BuiltV1(CadCandidate from, CadRequirementSet set, string sha, long id,
                                               string fingerprint = "cadsrc:old")
        {
            return new CadAuditSubject
            {
                ElementId = id,
                Category = "Walls",
                TypeName = "Generic - 200mm",
                Geometry = new List<CadPoint>(from.Geometry),
                Provenance = new CadProvenance
                {
                    SchemaVersion = 1,
                    CandidateId = from.Id,
                    GeometryId = from.GeometryId,
                    SemanticId = from.SemanticId,
                    RuleId = from.RuleId,
                    Layer = from.Layer,
                    RequirementSetSha256 = set.Sha256,
                    SourceFileSha256 = sha,
                    SourceFingerprint = fingerprint,
                    BuiltGeometry = CadUpdateRules.Encode(from.Geometry)
                }
            };
        }

        private static CadUpdateScope Resolve(List<CadAuditSubject> model, CadPlacement current,
                                              List<CadPlacement> inModel, string[] lineage = null,
                                              string[] placements = null) =>
            CadPlacementRules.Resolve(model, current, lineage, placements, inModel);

        // ------------------------------------------- 8.4d: two placements, one file

        [Fact]
        public void An_update_for_placement_ONE_claims_only_placement_one_s_elements()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject>
            {
                BuiltV2(drawn[0], set, FileX, 1000, P1),
                BuiltV2(drawn[0], set, FileX, 2000, P2)   // the other wing: same file, same hash, same shape
            };
            CadPlacement p1 = Placement(10, P1, FileX);
            var inModel = new List<CadPlacement> { p1, Placement(20, P2, FileX) };

            CadUpdateScope scope = Resolve(model, p1, inModel);

            Assert.Equal(CadUpdateScope.Identified, scope.Verdict);
            Assert.Contains(1000L, scope.Claimed);
            Assert.DoesNotContain(2000L, scope.Claimed);
            Assert.Contains(2000L, scope.OtherPlacement);
        }

        [Fact]
        public void And_the_plan_never_touches_reclaims_or_orphans_the_other_placement()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject>
            {
                BuiltV2(drawn[0], set, FileX, 1000, P1),
                BuiltV2(drawn[0], set, FileX, 2000, P2)
            };
            CadPlacement p1 = Placement(10, P1, FileX);
            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1, Placement(20, P2, FileX) });

            CadUpdate update = CadUpdateRules.Plan(drawn, model, set, scope, null, null, null, null);

            Assert.Contains(update.Actions, a => a.ElementId == 1000 && a.Kind == "leave");
            Assert.DoesNotContain(update.Actions, a => a.ElementId == 2000);
            Assert.Empty(update.Of("orphan"));
            Assert.Empty(update.Of("create"));
        }

        [Fact]
        public void The_file_scoped_predicate_is_what_claimed_the_other_wing()
        {
            // The defect, pinned: scoped by file, the second placement's element is
            // this run's to orphan. This is why the scoped overload exists.
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject>
            {
                BuiltV2(drawn[0], set, FileX, 1000, P1),
                BuiltV2(drawn[0], set, FileX, 2000, P2)
            };

            CadUpdate byFile = CadUpdateRules.Plan(drawn, model, set, FileX);

            Assert.Contains(byFile.Actions, a => a.ElementId == 2000 && a.Kind == "orphan");
        }

        // ------------------------------------------- v1 records: migration

        [Fact]
        public void A_v1_record_is_claimed_when_exactly_ONE_placement_of_its_file_exists()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject> { BuiltV1(drawn[0], set, FileX, 1000) };
            CadPlacement p1 = Placement(10, P1, FileX);

            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1 });

            Assert.Contains(1000L, scope.MigratedFromV1);
            Assert.Equal(CadUpdateScope.Identified, scope.Verdict);
            Assert.True(scope.Includes(model[0]));
            Assert.Equal(1, scope.ToJson().Value<int>("migrated_from_v1"));
        }

        [Fact]
        public void A_v1_record_with_TWO_placements_of_its_file_is_AMBIGUOUS_naming_both_and_is_never_claimed_or_orphaned()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject> { BuiltV1(drawn[0], set, FileX, 1000) };
            CadPlacement p1 = Placement(10, P1, FileX);
            CadPlacement p2 = Placement(20, P2, FileX);

            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1, p2 });

            Assert.DoesNotContain(1000L, scope.MigratedFromV1);
            Assert.DoesNotContain(1000L, scope.Claimed);
            CadScopeExclusion why = Assert.Single(scope.AmbiguousV1);
            Assert.Equal(1000, why.ElementId);
            Assert.Contains("10 [" + P1 + "]", why.Says);
            Assert.Contains("20 [" + P2 + "]", why.Says);
            Assert.Contains("Not claimed, not orphaned, not deleted", why.Says);

            CadUpdate update = CadUpdateRules.Plan(drawn, model, set, scope, null, null, null, null);
            Assert.DoesNotContain(update.Actions, a => a.ElementId == 1000);
        }

        [Fact]
        public void A_v1_record_whose_exact_fingerprint_matches_is_claimed_even_beside_a_second_placement()
        {
            // The v1 fingerprint folds instance, bytes, path and transform: equal
            // means THIS placement, unmoved. That is the one identity v1 can prove.
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject> { BuiltV1(drawn[0], set, FileX, 1000, "cadsrc:exact") };
            CadPlacement p1 = Placement(10, P1, FileX, fingerprint: "cadsrc:exact");
            CadPlacement p2 = Placement(20, P2, FileX);

            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1, p2 });

            Assert.Contains(1000L, scope.MigratedFromV1);
            Assert.Empty(scope.AmbiguousV1);
        }

        [Fact]
        public void A_v1_record_from_a_superseded_file_whose_instance_is_gone_is_still_claimed()
        {
            // The ordinary revision flow: link A was removed, link B is new, and
            // the caller says B supersedes A. Zero placements of A remain, so
            // nothing else could have built its elements.
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject> { BuiltV1(drawn[0], set, FileOld, 1000) };
            CadPlacement b = Placement(30, "uid-b", FileX);

            CadUpdateScope scope = Resolve(model, b, new List<CadPlacement> { b }, new[] { FileOld });

            Assert.Contains(1000L, scope.MigratedFromV1);
        }

        // ------------------------------------------- lineage under v2

        [Fact]
        public void A_superseded_file_recorded_under_ONE_placement_is_claimed_by_hash()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject> { BuiltV2(drawn[0], set, FileOld, 1000, "uid-a") };
            CadPlacement b = Placement(30, "uid-b", FileX);

            CadUpdateScope scope = Resolve(model, b, new List<CadPlacement> { b }, new[] { FileOld });

            Assert.Contains(1000L, scope.Claimed);
        }

        [Fact]
        public void A_superseded_file_recorded_under_TWO_placements_cannot_be_named_by_hash_alone()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject>
            {
                BuiltV2(drawn[0], set, FileOld, 1000, "uid-a1"),
                BuiltV2(drawn[0], set, FileOld, 2000, "uid-a2")
            };
            CadPlacement b = Placement(30, "uid-b", FileX);

            CadUpdateScope byHash = Resolve(model, b, new List<CadPlacement> { b }, new[] { FileOld });
            Assert.Empty(byHash.Claimed);
            Assert.Equal(2, byHash.AmbiguousLineageElements.Count);
            Assert.Contains("supersedes_placement_ids", byHash.AmbiguousLineageElements[0].Says);

            // Naming the placement settles it, and the other placement stays untouched.
            CadUpdateScope byPlacement = Resolve(model, b, new List<CadPlacement> { b }, null, new[] { "uid-a1" });
            Assert.Contains(1000L, byPlacement.Claimed);
            Assert.DoesNotContain(2000L, byPlacement.Claimed);
            Assert.Empty(byPlacement.AmbiguousLineageElements);
        }

        // ------------------------------------------- 8.4c: nothing claimable

        [Fact]
        public void A_run_that_can_claim_nothing_is_scope_unidentified_and_says_what_it_looked_for()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            var model = new List<CadAuditSubject> { BuiltV2(drawn[0], set, FileX, 2000, P2) };
            CadPlacement p1 = Placement(10, P1, FileX);

            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1, Placement(20, P2, FileX) });

            Assert.Equal(CadUpdateScope.Unidentified, scope.Verdict);
            Assert.Equal(0, scope.ClaimableCount);
            string refusal = CadPlacementRules.UnidentifiedRefusal(scope, "Model");
            Assert.StartsWith("scope_unidentified", refusal);
            Assert.Contains("claim NOTHING", refusal);
            Assert.Contains(P1, refusal);                       // what it looked for
            Assert.Contains(P2, refusal);                       // what exists
            Assert.Contains("supersedes_placement_ids", refusal);
            Assert.Equal(P1, scope.LookedFor.Value<string>("placement_id"));
            Assert.NotNull(scope.Exists["v2_placements"][P2]);
        }

        [Fact]
        public void An_embedded_import_is_identified_by_its_placement_and_says_the_hash_is_unavailable()
        {
            CadPlacement embedded = Placement(10, P1, null, path: null);

            CadSourceIdentity id = CadPlacementRules.Identity(embedded);

            Assert.Equal(CadPlacementRules.IdentityEmbedded, id.Mode);
            Assert.Equal("unavailable", id.ToJson().Value<string>("source_hash"));
            Assert.Contains(P1, id.Says);
        }

        [Fact]
        public void An_embedded_import_still_claims_its_own_v2_elements()
        {
            // No path, no hash - and the elements it built name its placement id,
            // so they are still its own. This is the case that used to report
            // zero of everything.
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, null);
            var model = new List<CadAuditSubject> { BuiltV2(drawn[0], set, null, 1000, P1) };
            CadPlacement embedded = Placement(10, P1, null, path: null);

            CadUpdateScope scope = Resolve(model, embedded, new List<CadPlacement> { embedded });

            Assert.Contains(1000L, scope.Claimed);
            Assert.Equal(CadUpdateScope.Identified, scope.Verdict);
        }

        [Fact]
        public void A_missing_file_is_named_with_its_recorded_path()
        {
            var moved = Placement(10, P1, null, path: "C:\\drawings\\gone.dwg");
            moved.FileMissing = true;

            CadSourceIdentity id = CadPlacementRules.Identity(moved);

            Assert.Equal(CadPlacementRules.IdentityFileMissing, id.Mode);
            Assert.Contains("C:\\drawings\\gone.dwg", id.Says);
            Assert.Contains("plans against THAT", id.Says);
        }

        // ------------------------------------------- transform

        [Fact]
        public void A_moved_placement_is_detected_with_its_delta()
        {
            CadRequirementSet set = Set();
            CadCandidate c = Read(set, FileX)[0];
            CadAuditSubject built = BuiltV2(c, set, FileX, 1000, P1, placementOriginX: 0);
            CadPlacement now = Placement(10, P1, FileX, originX: 500);

            CadPlacementMove move = CadPlacementRules.CompareTransforms(built.Provenance, now);

            Assert.True(move.Moved);
            Assert.Equal(500, move.DeltaMm[0], 3);
            Assert.Equal(0, move.DeltaMm[1], 3);
            Assert.Equal(0, move.RotationDegrees, 3);
            Assert.Null(move.DeltaUnknownBecause);
        }

        [Fact]
        public void A_record_without_a_transform_is_NOT_reported_as_moved()
        {
            CadRequirementSet set = Set();
            CadCandidate c = Read(set, FileX)[0];
            CadAuditSubject v1 = BuiltV1(c, set, FileX, 1000);

            CadPlacementMove move = CadPlacementRules.CompareTransforms(v1.Provenance, Placement(10, P1, FileX, 500));

            Assert.False(move.Moved);
            Assert.Contains("before provenance v2", move.DeltaUnknownBecause);
        }

        [Fact]
        public void A_frame_survives_encoding_and_carries_a_point_through_a_rotation()
        {
            string origin = CadPlacementRules.EncodeOrigin(new[] { 1000.0, 2000.0, 0.0 });
            string basis = CadPlacementRules.EncodeBasis(new[] { 0.0, 1.0, 0.0 }, new[] { -1.0, 0.0, 0.0 }, 1.0);
            CadPlacementFrame turned = CadPlacementRules.DecodeFrame(origin, basis);
            CadPlacementFrame flat = CadPlacementRules.DecodeFrame("0,0,0", Identity);
            var move = new CadPlacementMove { Moved = true, From = flat, To = turned };

            // Local (100, 0) under the flat frame → under the 90° frame at (1000, 2000): (1000, 2100).
            CadPoint carried = move.Carry(new CadPoint(100, 0));

            Assert.Equal(1000, carried.X, 6);
            Assert.Equal(2100, carried.Y, 6);
        }

        // ------------------------------------------- planning under an accepted move

        private static CadPlacementMove Shift(double dx)
        {
            return new CadPlacementMove
            {
                Moved = true,
                From = CadPlacementRules.DecodeFrame("0,0,0", Identity),
                To = CadPlacementRules.DecodeFrame(dx.ToString("0.###") + ",0,0", Identity),
                DeltaMm = new[] { dx, 0.0, 0.0 },
                RecordedFingerprint = "cadtf:0", CurrentFingerprint = "cadtf:" + dx
            };
        }

        [Fact]
        public void Without_accepting_the_move_a_shifted_placement_reads_as_deleted_and_redrawn()
        {
            // Why the placement_moved gate exists: every semantic id changed.
            CadRequirementSet set = Set();
            CadCandidate before = Read(set, FileX)[0];
            var model = new List<CadAuditSubject> { BuiltV2(before, set, FileX, 1000, P1) };
            CadPlacement p1 = Placement(10, P1, FileX, 500);
            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1 });

            CadUpdate naive = CadUpdateRules.Plan(Read(set, FileX, 500), model, set, scope, null, null, null, null);

            Assert.Contains(naive.Actions, a => a.Kind == "orphan" && a.ElementId == 1000);
            Assert.Contains(naive.Actions, a => a.Kind == "create");
        }

        [Fact]
        public void Under_an_accepted_move_an_untouched_element_FOLLOWS_the_drawing()
        {
            CadRequirementSet set = Set();
            CadCandidate before = Read(set, FileX)[0];
            var model = new List<CadAuditSubject> { BuiltV2(before, set, FileX, 1000, P1) };
            CadPlacement p1 = Placement(10, P1, FileX, 500);
            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1 });

            CadUpdate update = CadUpdateRules.Plan(Read(set, FileX, 500), model, set, scope, null, null, null, Shift(500));

            CadUpdateAction follow = Assert.Single(update.Actions);
            Assert.Equal("set_curve", follow.Kind);
            Assert.Equal(CadChange.Moved, follow.Classification);
            Assert.True(follow.Automatic);
            Assert.Equal(1000, follow.ElementId);
            Assert.Equal(500, follow.Geometry[0].X, 3);
            Assert.Contains("PLACEMENT moved", follow.Says);
        }

        [Fact]
        public void Under_an_accepted_move_an_element_somebody_ALSO_moved_is_a_conflict()
        {
            CadRequirementSet set = Set();
            CadCandidate before = Read(set, FileX)[0];
            CadAuditSubject touched = BuiltV2(before, set, FileX, 1000, P1);
            touched.Geometry = new List<CadPoint> { new CadPoint(0, 900), new CadPoint(6000, 900) };
            var model = new List<CadAuditSubject> { touched };
            CadPlacement p1 = Placement(10, P1, FileX, 500);
            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1 });

            CadUpdate update = CadUpdateRules.Plan(Read(set, FileX, 500), model, set, scope, null, null, null, Shift(500));

            CadUpdateAction conflict = Assert.Single(update.Actions);
            Assert.Equal("review", conflict.Kind);
            Assert.Equal(CadChange.Conflict, conflict.Classification);
            Assert.False(conflict.Automatic);
            Assert.Contains("A PERSON ALSO MOVED", conflict.Says);
        }

        [Fact]
        public void Under_an_accepted_move_an_element_already_moved_along_is_left()
        {
            CadRequirementSet set = Set();
            CadCandidate before = Read(set, FileX)[0];
            CadAuditSubject along = BuiltV2(before, set, FileX, 1000, P1);
            along.Geometry = new List<CadPoint> { new CadPoint(500, 0), new CadPoint(6500, 0) };
            var model = new List<CadAuditSubject> { along };
            CadPlacement p1 = Placement(10, P1, FileX, 500);
            CadUpdateScope scope = Resolve(model, p1, new List<CadPlacement> { p1 });

            CadUpdate update = CadUpdateRules.Plan(Read(set, FileX, 500), model, set, scope, null, null, null, Shift(500));

            CadUpdateAction leave = Assert.Single(update.Actions);
            Assert.Equal("leave", leave.Kind);
            Assert.Equal(CadChange.Unchanged, leave.Classification);
        }

        // ------------------------------------------- geometry_id on the way out

        [Fact]
        public void Every_planned_action_carries_the_candidate_s_geometry_id()
        {
            // The apply stamps what the plan emits. The plan never emitted this,
            // so every element an update created was stamped with GeometryId
            // null - and could never be recognised as "the same shape relayered".
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set, FileX);
            CadUpdate fresh = CadUpdateRules.Plan(drawn, new List<CadAuditSubject>(), set, FileX);
            CadUpdateAction create = Assert.Single(fresh.Of("create"));
            Assert.Equal(drawn[0].GeometryId, create.GeometryId);
            Assert.Equal(drawn[0].GeometryId, create.ToJson().Value<string>("geometry_id"));

            var model = new List<CadAuditSubject> { BuiltV2(drawn[0], set, FileX, 1000, P1) };
            CadPlacement p1 = Placement(10, P1, FileX);
            CadUpdate matched = CadUpdateRules.Plan(drawn, model, set,
                Resolve(model, p1, new List<CadPlacement> { p1 }), null, null, null, null);
            Assert.Equal(drawn[0].GeometryId, Assert.Single(matched.Of("leave")).GeometryId);
        }

        // ------------------------------------------- the apply ledger

        [Fact]
        public void The_same_key_with_the_same_actions_replays_and_a_different_plan_under_it_is_refused()
        {
            CadUpdateLedger.ResetForTests();
            var reply = new JObject { ["state"] = "applied", ["actions_attempted"] = 1 };
            CadUpdateLedger.Record("k1", "fp-a", P1, "applied", reply);

            CadUpdateLedgerDecision again = CadUpdateLedger.Decide("k1", "fp-a");
            Assert.Equal("replay", again.Outcome);
            Assert.Equal(1, again.Entry.ReplayCount);
            Assert.Equal("applied", again.Entry.Reply.Value<string>("state"));

            CadUpdateLedgerDecision other = CadUpdateLedger.Decide("k1", "fp-b");
            Assert.Equal("refuse", other.Outcome);
            Assert.Contains("idempotency_key_reused", other.Refusal);

            Assert.Equal("proceed", CadUpdateLedger.Decide("k2", "fp-a").Outcome);
            Assert.Equal("proceed", CadUpdateLedger.Decide(null, "fp-a").Outcome);
        }

        [Fact]
        public void A_partial_run_is_remembered_against_its_placement_until_a_clean_one_follows()
        {
            CadUpdateLedger.ResetForTests();
            var partial = new JObject
            {
                ["state"] = "partial", ["actions_attempted"] = 2, ["actions_failed"] = 1,
                ["actions"] = new JArray(new JObject { ["key"] = "cad-update-create", ["ok"] = true },
                                         new JObject { ["key"] = "cad-update-move-0", ["ok"] = false })
            };
            CadUpdateLedger.Record("k-partial", "fp-1", P1, "partial", partial);

            JObject told = CadUpdateLedger.Describe(CadUpdateLedger.LastPartialFor(P1));
            Assert.NotNull(told);
            Assert.Equal("partial", told.Value<string>("state"));
            Assert.Equal(1, told.Value<int>("actions_failed"));
            Assert.Contains("ended PARTIAL", told.Value<string>("means"));
            Assert.Null(CadUpdateLedger.LastPartialFor(P2));

            CadUpdateLedger.Record("k-clean", "fp-2", P1, "applied", new JObject { ["state"] = "applied" });
            Assert.Null(CadUpdateLedger.LastPartialFor(P1));
        }

        // ------------------------------------------- the record itself

        [Fact]
        public void A_v1_record_says_so_and_a_v2_record_carries_its_placement()
        {
            CadRequirementSet set = Set();
            CadCandidate c = Read(set, FileX)[0];
            JObject v1 = BuiltV1(c, set, FileX, 1).Provenance.ToJson();
            JObject v2 = BuiltV2(c, set, FileX, 2, P1).Provenance.ToJson();

            Assert.Equal("v1", v1.Value<string>("provenance_version"));
            Assert.Equal(JTokenType.Null, v1["placement"].Type);
            Assert.Equal("v2", v2.Value<string>("provenance_version"));
            Assert.Equal(P1, v2["placement"].Value<string>("id"));
            Assert.Equal(Identity, v2["placement"].Value<string>("basis"));
        }
    }
}
