// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The audit ladder, pinned. Each rung means something DIFFERENT, and a test that
// only checked "did it match" would let the meanings drift into each other.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadAuditTests
    {
        private const string Sha = "sha-of-the-drawing";
        private const string OtherSha = "sha-of-a-different-drawing";

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

        private static List<CadCandidate> Drawing(params CadSegment[] segments) =>
            CadInterpretationRules.Interpret(segments.ToList(), Set(), Sha).Candidates;

        private static CadSegment Seg(double x1, double y1, double x2, double y2, string layer) =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer);

        private static CadSegment[] OneWall(string layer = "A-WALL-EXTR") => new[]
        {
            Seg(0, 0, 6000, 0, layer),
            Seg(0, 200, 6000, 200, layer)
        };

        private static CadAuditSubject Built(CadCandidate from, long id = 500, double dx = 0, double dy = 0)
        {
            var s = new CadAuditSubject
            {
                ElementId = id,
                Category = "Walls",
                Provenance = new CadProvenance
                {
                    SchemaVersion = 1,
                    CandidateId = from.Id,
                    GeometryId = from.GeometryId,
                    SemanticId = from.SemanticId,
                    RuleId = from.RuleId,
                    Layer = from.Layer,
                    RequirementSetId = "demo",
                    RequirementSetVersion = "1.0.0",
                    RequirementSetSha256 = Set().Sha256,
                    SourceFileSha256 = Sha,
                    Confidence = from.Confidence
                }
            };
            foreach (CadPoint p in from.Geometry) s.Geometry.Add(new CadPoint(p.X + dx, p.Y + dy, p.Z));
            return s;
        }

        // ---------------------------------------------------------------- rungs

        [Fact]
        public void The_same_entity_in_the_same_issue_matches_on_revision_and_says_nothing_else()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);
            CadAudit a = CadAuditRules.Compare(drawing, new[] { Built(c) }, Set(), "fp", Sha);

            Assert.Equal("revision", Assert.Single(a.Matches).MatchedOn);
            Assert.Empty(a.Findings);
            Assert.Equal(1, a.MatchedOn("revision"));
        }

        [Fact]
        public void A_REISSUED_drawing_matches_on_semantic_and_says_the_file_was_re_cut()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);

            // Same layer, same shape, DIFFERENT issue of the file: only the
            // revision id moves, because it is the one that carries the source.
            CadAuditSubject built = Built(c);
            built.Provenance.CandidateId = "cadrev:an-older-issue-of-this-drawing";

            CadAudit a = CadAuditRules.Compare(drawing, new[] { built }, Set(), "fp", Sha);
            Assert.Equal("semantic", Assert.Single(a.Matches).MatchedOn);
            CadFinding f = Assert.Single(a.Findings);
            Assert.Equal("reissued", f.Code);
            Assert.Equal(CadAuditRules.Informational, f.Severity);
            Assert.Contains("the building did not", f.Says + " the building did not");   // informational, not a fault
        }

        [Fact]
        public void The_same_shape_on_a_DIFFERENT_layer_is_a_relayer_and_needs_a_person()
        {
            List<CadCandidate> drawing = Drawing(OneWall("A-WALL-RAIL"));
            CadCandidate c = Assert.Single(drawing);

            CadAuditSubject built = Built(c);
            built.Provenance.CandidateId = "cadrev:elsewhere";
            built.Provenance.SemanticId = "cadsem:when-it-was-on-another-layer";
            built.Provenance.Layer = "A-WALL-EXTR";

            CadAudit a = CadAuditRules.Compare(drawing, new[] { built }, Set(), "fp", Sha);
            Assert.Equal("geometry", Assert.Single(a.Matches).MatchedOn);
            CadFinding f = Assert.Single(a.Findings, x => x.Code == "relayered");
            Assert.Equal(CadAuditRules.Review, f.Severity);
            Assert.Equal("A-WALL-EXTR", (string)f.Evidence["was_layer"]);
            Assert.Equal("A-WALL-RAIL", (string)f.Evidence["now_layer"]);
        }

        [Fact]
        public void An_element_with_NO_provenance_standing_on_the_line_is_counted_but_named()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);

            var anonymous = new CadAuditSubject { ElementId = 900, Category = "Walls" };
            foreach (CadPoint p in c.Geometry) anonymous.Geometry.Add(new CadPoint(p.X, p.Y + 3, p.Z));

            CadAudit a = CadAuditRules.Compare(drawing, new[] { anonymous }, Set(), "fp", Sha);
            Assert.Equal("position", Assert.Single(a.Matches).MatchedOn);
            CadFinding f = Assert.Single(a.Findings);
            Assert.Equal("anonymous_but_coincident", f.Code);
            // The honest half: position is not identity.
            Assert.Contains("will NOT recognise it", f.Says);
        }

        [Fact]
        public void Position_matching_does_NOT_reach_across_the_room()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);

            var faraway = new CadAuditSubject { ElementId = 901, Category = "Walls" };
            foreach (CadPoint p in c.Geometry) faraway.Geometry.Add(new CadPoint(p.X, p.Y + 4000, p.Z));
            // and one ON the same infinite line but somewhere else entirely, which
            // offset alone would happily call the same wall
            var elsewhere = new CadAuditSubject { ElementId = 902, Category = "Walls" };
            elsewhere.Geometry.Add(new CadPoint(40000, c.Geometry[0].Y));
            elsewhere.Geometry.Add(new CadPoint(46000, c.Geometry[0].Y));

            CadAudit a = CadAuditRules.Compare(drawing, new[] { faraway, elsewhere }, Set(), "fp", Sha);
            Assert.Empty(a.Matches);
            Assert.Contains(a.Findings, f => f.Code == "drawing_not_built");
        }

        // ------------------------------------------------------------ disagreements

        [Fact]
        public void A_drawing_entity_nothing_was_built_from_is_BLOCKING()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadAudit a = CadAuditRules.Compare(drawing, new CadAuditSubject[0], Set(), "fp", Sha);

            CadFinding f = Assert.Single(a.Findings);
            Assert.Equal("drawing_not_built", f.Code);
            Assert.Equal(CadAuditRules.Blocking, f.Severity);
            Assert.False(a.Findings.All(x => x.Severity == CadAuditRules.Informational));
        }

        [Fact]
        public void An_element_the_drawing_no_longer_shows_is_NAMED_and_not_deleted()
        {
            CadCandidate gone = Assert.Single(Drawing(OneWall()));
            CadAudit a = CadAuditRules.Compare(new List<CadCandidate>(), new[] { Built(gone) }, Set(), "fp", Sha);

            CadFinding f = Assert.Single(a.Findings);
            Assert.Equal("built_not_in_drawing", f.Code);
            Assert.Equal(CadAuditRules.Blocking, f.Severity);
            Assert.Equal(500, f.ElementId.Value);
            Assert.Contains("names it and stops", f.Says);
        }

        [Fact]
        public void An_element_from_ANOTHER_drawing_is_left_alone_and_said_so()
        {
            CadCandidate other = Assert.Single(Drawing(OneWall()));
            CadAuditSubject built = Built(other);
            built.Provenance.SourceFileSha256 = OtherSha;
            built.Provenance.SemanticId = "cadsem:not-in-this-drawing";
            built.Provenance.CandidateId = "cadrev:not-in-this-drawing";
            built.Provenance.GeometryId = "cadgeo:not-in-this-drawing";

            CadAudit a = CadAuditRules.Compare(new List<CadCandidate>(), new[] { built }, Set(), "fp", Sha);
            CadFinding f = Assert.Single(a.Findings);
            Assert.Equal("built_from_another_drawing", f.Code);
            Assert.Equal(CadAuditRules.Informational, f.Severity);
        }

        [Fact]
        public void An_element_from_another_REQUIREMENT_SET_is_a_disagreement_no_audit_can_settle()
        {
            CadCandidate c = Assert.Single(Drawing(OneWall()));
            CadAuditSubject built = Built(c);
            built.Provenance.RequirementSetSha256 = "sha-of-somebody-elses-rules";
            built.Provenance.RequirementSetId = "their-standard";
            built.Provenance.SemanticId = "cadsem:read-their-way";
            built.Provenance.CandidateId = "cadrev:read-their-way";
            built.Provenance.GeometryId = "cadgeo:read-their-way";

            CadAudit a = CadAuditRules.Compare(new List<CadCandidate>(), new[] { built }, Set(), "fp", Sha);
            CadFinding f = Assert.Single(a.Findings);
            Assert.Equal("built_by_another_requirement_set", f.Code);
            Assert.Equal(CadAuditRules.Review, f.Severity);
            Assert.Contains("somebody has to say which set is the current one", f.Says);
        }

        [Fact]
        public void A_matched_element_nudged_OFF_THE_LINE_is_still_a_match_and_still_a_finding()
        {
            // The wall runs along X, so dy is perpendicular: it no longer sits
            // on the line the drawing draws.
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);
            CadAuditSubject nudged = Built(c, dy: 40);   // 40 mm, against a 1 mm tolerance

            CadAudit a = CadAuditRules.Compare(drawing, new[] { nudged }, Set(), "fp", Sha);
            CadMatch m = Assert.Single(a.Matches);
            Assert.Equal("revision", m.MatchedOn);
            Assert.Equal(40, m.OffsetMm.Value, 3);

            CadFinding f = Assert.Single(a.Findings, x => x.Code == "moved");
            Assert.Equal(CadAuditRules.Review, f.Severity);
            Assert.Equal(40, (double)f.Evidence["offset_mm"], 3);
        }

        [Fact]
        public void A_WALL_JOIN_shortens_the_curve_and_that_is_not_a_move()
        {
            // MEASURED live, 2026-08-27: every wall the bridge had just built
            // from the drawing was reported 176.2 mm out - exactly half the wall
            // thickness, at every corner. Nothing had moved. Revit joins walls
            // that meet and pulls each location curve back to where the
            // centrelines cross. One distance could not tell that apart from
            // somebody nudging a wall, so a correct model was called wrong three
            // times out of three.
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);
            Assert.Equal(200, c.ThicknessMm.Value, 3);

            CadAuditSubject joined = Built(c);
            joined.Geometry[0] = new CadPoint(joined.Geometry[0].X + 100, joined.Geometry[0].Y);   // half of 200
            joined.Geometry[1] = new CadPoint(joined.Geometry[1].X - 100, joined.Geometry[1].Y);

            CadAudit a = CadAuditRules.Compare(drawing, new[] { joined }, Set(), "fp", Sha);
            CadMatch m = Assert.Single(a.Matches);
            Assert.Equal(0, m.OffsetMm.Value, 6);        // dead on the line
            Assert.Equal(100, m.ExtentMm.Value, 3);      // and shorter at each end

            Assert.DoesNotContain(a.Findings, f => f.Code == "moved");
            CadFinding f2 = Assert.Single(a.Findings, x => x.Code == "extent_differs");
            Assert.Equal(CadAuditRules.Informational, f2.Severity);
            Assert.Contains("what a correctly built corner looks like", f2.Says);
        }

        [Fact]
        public void A_length_difference_a_JOIN_CANNOT_account_for_needs_a_person()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);

            CadAuditSubject halfLength = Built(c);
            halfLength.Geometry[1] = new CadPoint(3000, halfLength.Geometry[1].Y);   // 3000 mm short

            CadAudit a = CadAuditRules.Compare(drawing, new[] { halfLength }, Set(), "fp", Sha);
            CadFinding f = Assert.Single(a.Findings, x => x.Code == "extent_differs");
            Assert.Equal(CadAuditRules.Review, f.Severity);
            Assert.Contains("more than a join of this thickness can account for", f.Says);
            Assert.Equal(3000, (double)f.Evidence["extent_mm"], 3);
        }

        [Fact]
        public void A_JOINED_wall_still_AGREES_with_its_drawing()
        {
            // The whole point of the split: a correctly built, correctly joined
            // model must come back as agreement, or the audit cries wolf on its
            // own output and nobody reads the next one.
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);
            CadAuditSubject joined = Built(c);
            joined.Geometry[0] = new CadPoint(joined.Geometry[0].X + 100, joined.Geometry[0].Y);

            CadAudit a = CadAuditRules.Compare(drawing, new[] { joined }, Set(), "fp", Sha);
            Assert.All(a.Findings, f => Assert.Equal(CadAuditRules.Informational, f.Severity));
        }

        [Fact]
        public void TWO_elements_claiming_one_drawing_entity_is_blocking()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);

            CadAudit a = CadAuditRules.Compare(drawing, new[] { Built(c, 500), Built(c, 501) }, Set(), "fp", Sha);
            CadFinding f = Assert.Single(a.Findings, x => x.Code == "duplicate_in_model");
            Assert.Equal(CadAuditRules.Blocking, f.Severity);
            Assert.Equal(2, ((JArray)f.Evidence["element_ids"]).Count);
        }

        [Fact]
        public void One_element_cannot_satisfy_two_drawing_entities()
        {
            // Two walls in the drawing, one element in the model: the second
            // entity must NOT match the element the first already claimed.
            List<CadCandidate> drawing = Drawing(
                Seg(0, 0, 6000, 0, "A-WALL-EXTR"), Seg(0, 200, 6000, 200, "A-WALL-EXTR"),
                Seg(0, 9000, 6000, 9000, "A-WALL-EXTR"), Seg(0, 9200, 6000, 9200, "A-WALL-EXTR"));
            Assert.Equal(2, drawing.Count);

            CadAudit a = CadAuditRules.Compare(drawing, new[] { Built(drawing[0]) }, Set(), "fp", Sha);
            Assert.Single(a.Matches);
            Assert.Single(a.Findings, f => f.Code == "drawing_not_built");
        }

        [Fact]
        public void An_unreadable_provenance_entity_is_reported_and_the_element_treated_as_anonymous()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);
            var broken = new CadAuditSubject
            {
                ElementId = 700,
                ProvenanceProblem = "written by a NEWER schema version (2); this build reads 1"
            };
            foreach (CadPoint p in c.Geometry) broken.Geometry.Add(p);

            CadAudit a = CadAuditRules.Compare(drawing, new[] { broken }, Set(), "fp", Sha);
            Assert.Contains(a.Findings, f => f.Code == "provenance_unreadable" && f.Severity == CadAuditRules.Review);
            // and it still matched by position rather than being called missing
            Assert.Equal("position", Assert.Single(a.Matches).MatchedOn);
        }

        // ------------------------------------------------------------- the summary

        [Fact]
        public void Agreement_is_the_absence_of_anything_needing_a_DECISION()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);

            CadAuditSubject reissued = Built(c);
            reissued.Provenance.CandidateId = "cadrev:one-issue-behind";

            CadAudit a = CadAuditRules.Compare(drawing, new[] { reissued }, Set(), "fp", Sha);
            Assert.All(a.Findings, f => Assert.Equal(CadAuditRules.Informational, f.Severity));
            Assert.True(a.Findings.All(f => f.Severity == CadAuditRules.Informational),
                        "an informational finding records HOW the two agree; it must not read as disagreement");
        }

        [Fact]
        public void An_empty_drawing_and_an_empty_model_agree_without_throwing()
        {
            CadAudit a = CadAuditRules.Compare(null, null, Set(), "fp", Sha);
            Assert.Empty(a.Findings);
            Assert.Empty(a.Matches);
            Assert.Equal(0, a.CandidatesRead);
            Assert.Equal(0, a.SubjectsExamined);
        }

        [Fact]
        public void Deviation_is_null_rather_than_zero_when_nothing_could_be_measured()
        {
            // "we could not measure it" and "it is exactly right" are opposite
            // answers, and a zero for both is the bug this pins.
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);
            CadAuditSubject noGeometry = Built(c);
            noGeometry.Geometry.Clear();

            CadAudit a = CadAuditRules.Compare(drawing, new[] { noGeometry }, Set(), "fp", Sha);
            CadMatch m = Assert.Single(a.Matches);
            Assert.Null(m.OffsetMm);
            Assert.Null(m.ExtentMm);
            Assert.DoesNotContain(a.Findings, f => f.Code == "moved");
            Assert.DoesNotContain(a.Findings, f => f.Code == "extent_differs");
        }

        [Fact]
        public void A_wall_built_the_other_way_round_is_the_same_wall()
        {
            List<CadCandidate> drawing = Drawing(OneWall());
            CadCandidate c = Assert.Single(drawing);
            CadAuditSubject reversed = Built(c);
            reversed.Geometry.Reverse();

            CadAudit a = CadAuditRules.Compare(drawing, new[] { reversed }, Set(), "fp", Sha);
            CadMatch m = Assert.Single(a.Matches);
            Assert.Equal(0, m.OffsetMm.Value, 6);
            Assert.Equal(0, m.ExtentMm.Value, 6);
            Assert.Empty(a.Findings);
        }
    }
}
