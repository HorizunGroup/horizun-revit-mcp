// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHICH ELEMENTS IS THIS RUN ABOUT?
//
// A building is a plan per storey and often a plan per block, converted under one
// requirement set into one model. Two sibling plans that draw the same thing on
// the same layer - which is what a repeated storey IS - produce the same semantic
// id and the same geometry id. So the question above stops having an obvious
// answer, and the wrong answer is not a wrong number: it is a proposal to delete
// another storey's work, or a report that a person edited something they have
// never opened.
//
// It was answered in two places and the two disagreed. The ORPHAN loop was scoped
// to the drawing and its declared lineage; the MATCHING was not scoped at all. So
// an update for drawing A could claim an element built from drawing B, and then
// say "the drawing still says exactly what this element was built from" about a
// wall on a storey A has never mentioned.
//
// And an element with NO recorded source was counted as belonging to whichever
// drawing happened to ask. Every identified run claimed every anonymous element
// in the model and then proposed to orphan it - a proposal to delete work whose
// origin is unknown, which is the one thing an unknown origin is not evidence for.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadMultiSourceScopeTests
    {
        private const string DrawingA = "sha-of-drawing-a";
        private const string DrawingB = "sha-of-drawing-b";

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

        /// <summary>The same wall, drawn the same way - which is what a repeated storey looks like.</summary>
        private static List<CadSegment> Wall()
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, -100), new CadPoint(6000, -100), "A-WALL"),
                new CadSegment(new CadPoint(0, 100), new CadPoint(6000, 100), "A-WALL")
            };
        }

        private static List<CadCandidate> Read(CadRequirementSet set, string sha)
        {
            return CadInterpretationRules.Interpret(Wall(), set, sha).Candidates.ToList();
        }

        /// <summary>An element in the model, stamped with the drawing that built it.</summary>
        private static CadAuditSubject Built(CadCandidate from, CadRequirementSet set, string sourceSha,
                                             long elementId)
        {
            return new CadAuditSubject
            {
                ElementId = elementId,
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
                    SourceFileSha256 = sourceSha,
                    BuiltGeometry = CadUpdateRules.Encode(from.Geometry)
                }
            };
        }

        // ------------------------------------------------- the matching is scoped

        [Fact]
        public void An_update_for_one_drawing_does_not_CLAIM_the_other_drawing_s_element()
        {
            // Both drawings draw the same wall on the same layer, so both produce
            // the same semantic id. The update for A must not match B's element.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(set, DrawingA);
            var model = new List<CadAuditSubject> { Built(a[0], set, DrawingB, 2000) };

            CadUpdate update = CadUpdateRules.Plan(a, model, set, DrawingA);

            Assert.DoesNotContain(update.Actions, x => x.ElementId == 2000 && x.Kind == "leave");
            Assert.Contains(update.Actions, x => x.Kind == "create");
        }

        [Fact]
        public void And_does_not_propose_to_ORPHAN_it_either()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(set, DrawingA);
            var model = new List<CadAuditSubject> { Built(a[0], set, DrawingB, 2000) };

            CadUpdate update = CadUpdateRules.Plan(a, model, set, DrawingA);

            Assert.DoesNotContain(update.Actions, x => x.Kind == "orphan");
        }

        [Fact]
        public void Its_OWN_element_is_still_matched()
        {
            // The scoping must not swallow the ordinary case.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(set, DrawingA);
            var model = new List<CadAuditSubject> { Built(a[0], set, DrawingA, 1000) };

            CadUpdate update = CadUpdateRules.Plan(a, model, set, DrawingA);

            Assert.Contains(update.Actions, x => x.ElementId == 1000 && x.Kind == "leave");
            Assert.Empty(update.Of("create"));
        }

        [Fact]
        public void A_drawing_this_one_SUPERSEDES_is_still_in_scope()
        {
            // Lineage is the caller's statement that one file re-issues another,
            // and it must keep working: this is how a revision finds its own
            // previous elements.
            CadRequirementSet set = Set();
            List<CadCandidate> b = Read(set, DrawingB);
            var model = new List<CadAuditSubject> { Built(b[0], set, DrawingA, 1000) };

            CadUpdate update = CadUpdateRules.Plan(b, model, set, DrawingB, null, new[] { DrawingA });

            Assert.Contains(update.Actions, x => x.ElementId == 1000);
        }

        // ------------------------------------------- an anonymous element is nobody's

        [Fact]
        public void An_element_with_NO_recorded_source_is_not_claimed_by_this_drawing()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(set, DrawingA);
            var model = new List<CadAuditSubject> { Built(a[0], set, null, 3000) };

            CadUpdate update = CadUpdateRules.Plan(a, model, set, DrawingA);

            Assert.DoesNotContain(update.Actions, x => x.ElementId == 3000 && x.Kind == "leave");
        }

        [Fact]
        public void And_is_not_proposed_for_DELETION_either()
        {
            // The worst of the two. "We do not know where this came from" is not
            // evidence that this drawing built it and then stopped drawing it.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(set, DrawingA);
            var model = new List<CadAuditSubject> { Built(a[0], set, null, 3000) };

            CadUpdate update = CadUpdateRules.Plan(a, model, set, DrawingA);

            Assert.DoesNotContain(update.Actions, x => x.ElementId == 3000 && x.Kind == "orphan");
        }

        // ------------------------------------- the rules are not part of belonging

        [Fact]
        public void A_change_made_in_the_RULES_is_still_seen_by_the_update()
        {
            // THE OVERSHOOT THIS FILE ALSO EXISTS FOR. The first version of the
            // scope folded the requirement-set hash into "is this element mine",
            // and a set's hash changes whenever the set does - so an update run
            // with an edited set matched NOTHING, and every classification came
            // back zero. "The drawing is the same and the rules now ask for a
            // different type" is precisely what retyped and resized are, so half
            // of what an incremental is for went silent, reporting zero rather
            // than wrong.
            CadRequirementSet built = Set();
            List<CadCandidate> a = Read(built, DrawingA);
            CadAuditSubject held = Built(a[0], built, DrawingA, 1000);

            // The same drawing, read again under a set whose rules were edited.
            CadRequirementSet edited = Set();
            edited.Rules[0].FamilyType = "Something Else";
            List<CadCandidate> again = Read(edited, DrawingA);
            foreach (CadCandidate c in again) c.FamilyType = "Something Else";

            CadUpdate update = CadUpdateRules.Plan(again, new List<CadAuditSubject> { held },
                                                   edited, DrawingA);

            Assert.Contains(update.Actions, x => x.ElementId == 1000);
        }

        [Fact]
        public void But_an_element_built_under_OTHER_rules_is_still_not_proposed_for_deletion()
        {
            // Belonging to this run and being deletable by it are two questions,
            // and only the second is about the rules.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(set, DrawingA);
            CadAuditSubject other = Built(a[0], set, DrawingA, 1000);
            other.Provenance.RequirementSetSha256 = "some-other-set";

            // A drawing that no longer says anything, so everything of this run's
            // would otherwise be orphaned.
            CadUpdate update = CadUpdateRules.Plan(new List<CadCandidate>(),
                                                   new List<CadAuditSubject> { other }, set, DrawingA);

            Assert.DoesNotContain(update.Actions, x => x.Kind == "orphan");
        }

        // ---------------------------------------------------------- the audit ladder

        [Fact]
        public void The_audit_does_not_report_ANOTHER_drawing_s_element_as_this_one_s_reissue()
        {
            // The semantic rung's key is the layer and the shape, which two sibling
            // drawings share by construction. Unscoped, the audit called B's
            // element a re-issue of A's candidate - and then reported the element
            // actually built from A as belonging to somebody else.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(set, DrawingA);
            CadAuditSubject fromB = Built(a[0], set, DrawingB, 2000);
            fromB.Provenance.CandidateId = "a-different-issue";

            CadAudit audit = CadAuditRules.Compare(a, new List<CadAuditSubject> { fromB },
                                                   set, "fingerprint-a", DrawingA);

            Assert.DoesNotContain(audit.Findings, f => f.Code == "reissued" && f.ElementId == 2000);
        }

        [Fact]
        public void But_a_genuine_re_issue_of_the_SAME_drawing_is_still_found()
        {
            // Same file, re-cut: the candidate id moved and the semantic id did
            // not. That is what `reissued` is for and it must survive the scoping.
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(set, DrawingA);
            CadAuditSubject older = Built(a[0], set, DrawingA, 1000);
            older.Provenance.CandidateId = "an-older-issue";

            CadAudit audit = CadAuditRules.Compare(a, new List<CadAuditSubject> { older },
                                                   set, "fingerprint-a", DrawingA);

            Assert.Contains(audit.Findings, f => f.Code == "reissued" && f.ElementId == 1000);
        }
    }
}
