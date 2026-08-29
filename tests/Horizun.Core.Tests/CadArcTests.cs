// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// CURVED WALLS, pinned at the level where the reasoning lives.
//
// A curved wall is the first thing in this program that a chord cannot express.
// Everything else - pairing, loops, containment - is line work, and the harvest
// chords every curve so that line work can consume it. But building from those
// chords makes ONE STRAIGHT WALL PER CHORD, and the audit reduces a geometry to
// its first and last point, so a correctly built arc wall would then read as
// massively moved. The arc has to survive as an arc, end to end, or not at all.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadArcTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static CadRequirementSet Set(string from = "double_arcs",
                                             double minThickness = 100, double maxThickness = 400)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'curved', 'version': '1.0.0', 'title': 'Curved walls' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'curved', 'precedence': 10, 'layers': ['A-WALL*'], 'produces': 'wall',
                          'category': 'OST_Walls', 'height_mm': 3000,
                          'geometry': { 'from': 'FROM', 'min_thickness_mm': MIN, 'max_thickness_mm': MAX,
                                        'min_overlap_fraction': 0.6 } }]
            }".Replace('\'', '"')
               .Replace("FROM", from)
               .Replace("MIN", minThickness.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Replace("MAX", maxThickness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        /// <summary>An arc about (0,0), from angle a0 to a1 in degrees, at the given radius.</summary>
        private static CadArcFact Arc(double radius, double a0, double a1, string layer = "A-WALL-CURVED",
                                      string id = null)
        {
            Func<double, CadPoint> at = deg =>
            {
                double r = deg * Math.PI / 180.0;
                return new CadPoint(radius * Math.Cos(r), radius * Math.Sin(r));
            };
            return new CadArcFact(id ?? ("cadarc:" + radius + ":" + a0 + ":" + a1),
                new CadPoint(0, 0), radius, at(a0), at(a1), at((a0 + a1) / 2.0), layer, 12, 5.0);
        }

        // ---------------------------------------------------------------- reading

        [Fact]
        public void Two_concentric_arcs_a_wall_thickness_apart_are_ONE_curved_wall()
        {
            var arcs = new List<CadArcFact> { Arc(5100, 0, 90), Arc(4900, 0, 90) };
            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs);

            CadCandidate c = Assert.Single(r.Candidates);
            Assert.Equal("wall", c.ProposedKind);
            Assert.NotNull(c.Arc);
            Assert.Equal(200, c.ThicknessMm.Value, 3);
            // The CENTRELINE, built rather than picked: neither face is the wall's line.
            Assert.Equal(5000, c.Arc.RadiusMm, 3);
            Assert.Equal(0, c.Arc.Centre.X, 3);
            Assert.Equal(0, c.Arc.Centre.Y, 3);
            Assert.Equal(90, c.Arc.SweepRadians * 180.0 / Math.PI, 1);
        }

        [Fact]
        public void The_reply_says_what_building_from_the_CHORDS_would_have_cost()
        {
            var arcs = new List<CadArcFact> { Arc(5100, 0, 90), Arc(4900, 0, 90) };
            CadCandidate c = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs)
                .Candidates.Single();
            Assert.Contains(c.Assumptions, a => a.Contains("one straight wall per chord"));
            Assert.Contains(c.Assumptions, a => a.Contains("no audit could match them back"));
        }

        [Fact]
        public void A_180_degree_arc_pair_is_read_as_one_wall_too()
        {
            var arcs = new List<CadArcFact> { Arc(3100, 0, 180), Arc(2900, 0, 180) };
            CadCandidate c = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs)
                .Candidates.Single();
            Assert.Equal(3000, c.Arc.RadiusMm, 3);
            Assert.Equal(180, c.Arc.SweepRadians * 180.0 / Math.PI, 1);
        }

        [Fact]
        public void Arcs_that_are_NOT_concentric_are_not_a_wall()
        {
            var off = new CadArcFact("cadarc:offset", new CadPoint(500, 0), 4900,
                new CadPoint(5400, 0), new CadPoint(500, 4900), new CadPoint(3965, 3465),
                "A-WALL-CURVED", 12, 5.0);
            var arcs = new List<CadArcFact> { Arc(5100, 0, 90), off };
            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs);
            Assert.Empty(r.Candidates);
        }

        [Fact]
        public void Arcs_too_far_apart_to_be_faces_of_one_wall_are_not_a_wall()
        {
            // 2000 mm apart, against a rule that allows 100-400.
            var arcs = new List<CadArcFact> { Arc(6000, 0, 90), Arc(4000, 0, 90) };
            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs);
            Assert.Empty(r.Candidates);
        }

        [Fact]
        public void Concentric_arcs_that_do_not_share_a_stretch_of_ANGLE_are_not_a_wall()
        {
            // Right thickness, same centre, opposite sides of the circle.
            var arcs = new List<CadArcFact> { Arc(5100, 0, 60), Arc(4900, 180, 240) };
            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs);
            Assert.Empty(r.Candidates);
        }

        [Fact]
        public void With_NO_arc_reading_an_arc_rule_produces_nothing_rather_than_guessing()
        {
            // A caller with no arcs passes null. That is NOT the same as a drawing
            // with no arcs in it, and neither is an occasion to fall back to chords.
            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, null);
            Assert.Empty(r.Candidates);
        }

        [Fact]
        public void The_widest_valid_pair_wins_and_an_arc_is_used_once()
        {
            // Three concentric arcs: 5200, 5000, 4800. Two pairings are valid at
            // 200 and one at 400; whichever is taken, no arc may serve twice.
            var arcs = new List<CadArcFact> { Arc(5200, 0, 90), Arc(5000, 0, 90), Arc(4800, 0, 90) };
            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs);
            Assert.Single(r.Candidates);
            Assert.Equal(400, r.Candidates[0].ThicknessMm.Value, 3);
        }

        // -------------------------------------------------------------- identity

        [Fact]
        public void Two_arcs_through_the_SAME_ENDS_do_not_share_an_identity()
        {
            // A minor and a major arc of one chord: same start, same end, and they
            // are not the same wall. An id taken over the endpoints collides, and
            // an audit would then match an element to the wrong drawing entity.
            var start = new CadPoint(1000, 0);
            var end = new CadPoint(0, 1000);
            string minor = CadIdentity.ArcGeometryId(new CadPoint(0, 0), 1000, start, end, false, 1.0);
            string major = CadIdentity.ArcGeometryId(new CadPoint(0, 0), 1000, start, end, true, 1.0);
            Assert.NotEqual(minor, major);

            string wider = CadIdentity.ArcGeometryId(new CadPoint(0, 0), 2000, start, end, false, 1.0);
            Assert.NotEqual(minor, wider);
        }

        [Fact]
        public void The_same_arc_read_twice_has_the_same_identity()
        {
            var start = new CadPoint(1000, 0);
            var end = new CadPoint(0, 1000);
            Assert.Equal(
                CadIdentity.ArcGeometryId(new CadPoint(0, 0), 1000, start, end, false, 1.0),
                CadIdentity.ArcGeometryId(new CadPoint(0, 0), 1000, start, end, false, 1.0));
        }

        [Fact]
        public void An_arc_candidate_carries_an_arc_identity_not_an_endpoint_one()
        {
            var arcs = new List<CadArcFact> { Arc(5100, 0, 90), Arc(4900, 0, 90) };
            CadCandidate c = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs)
                .Candidates.Single();
            string endpointsOnly = CadIdentity.GeometryId(CadCurveKind.Arc,
                new List<CadPoint> { c.Arc.Start, c.Arc.End }, 1.0);
            Assert.NotEqual(endpointsOnly, c.GeometryId);
        }

        // ----------------------------------------------------------------- audit

        private static CadAuditSubject Built(CadCandidate from, double centreOffset = 0, double radiusOffset = 0)
        {
            var s = new CadAuditSubject
            {
                ElementId = 700,
                ArcCentre = new CadPoint(from.Arc.Centre.X + centreOffset, from.Arc.Centre.Y),
                ArcRadiusMm = from.Arc.RadiusMm + radiusOffset,
                Provenance = new CadProvenance
                {
                    CandidateId = from.Id, GeometryId = from.GeometryId, SemanticId = from.SemanticId,
                    RuleId = from.RuleId, Layer = from.Layer,
                    RequirementSetSha256 = Set().Sha256, SourceFileSha256 = Sha
                }
            };
            s.Geometry.Add(from.Arc.Start);
            s.Geometry.Add(from.Arc.End);
            return s;
        }

        [Fact]
        public void A_correctly_built_curved_wall_AGREES_with_its_drawing()
        {
            // The whole reason the arc had to survive: built from chords, this wall
            // would read as massively moved and the audit would cry wolf.
            var arcs = new List<CadArcFact> { Arc(5100, 0, 90), Arc(4900, 0, 90) };
            List<CadCandidate> drawing = CadInterpretationRules
                .Interpret(new List<CadSegment>(), Set(), Sha, arcs).Candidates;

            CadAudit a = CadAuditRules.Compare(drawing, new[] { Built(drawing[0]) }, Set(), "fp", Sha);
            Assert.Equal("revision", Assert.Single(a.Matches).MatchedOn);
            Assert.DoesNotContain(a.Findings, f => f.Code == "moved");
        }

        [Fact]
        public void A_curved_wall_at_the_WRONG_RADIUS_is_reported_as_moved()
        {
            var arcs = new List<CadArcFact> { Arc(5100, 0, 90), Arc(4900, 0, 90) };
            List<CadCandidate> drawing = CadInterpretationRules
                .Interpret(new List<CadSegment>(), Set(), Sha, arcs).Candidates;

            CadAudit a = CadAuditRules.Compare(drawing, new[] { Built(drawing[0], radiusOffset: 250) },
                                               Set(), "fp", Sha);
            CadFinding f = Assert.Single(a.Findings, x => x.Code == "moved");
            Assert.Equal(250, (double)f.Evidence["offset_mm"], 1);
        }

        [Fact]
        public void A_curved_wall_about_the_WRONG_CENTRE_is_reported_as_moved()
        {
            var arcs = new List<CadArcFact> { Arc(5100, 0, 90), Arc(4900, 0, 90) };
            List<CadCandidate> drawing = CadInterpretationRules
                .Interpret(new List<CadSegment>(), Set(), Sha, arcs).Candidates;

            CadAudit a = CadAuditRules.Compare(drawing, new[] { Built(drawing[0], centreOffset: 40) },
                                               Set(), "fp", Sha);
            Assert.Single(a.Findings, x => x.Code == "moved");
        }

        [Fact]
        public void A_drawing_that_says_CURVE_and_an_element_that_is_STRAIGHT_do_not_agree()
        {
            var arcs = new List<CadArcFact> { Arc(5100, 0, 90), Arc(4900, 0, 90) };
            List<CadCandidate> drawing = CadInterpretationRules
                .Interpret(new List<CadSegment>(), Set(), Sha, arcs).Candidates;

            CadAuditSubject straight = Built(drawing[0]);
            straight.ArcCentre = null;      // a wall built as a chord
            straight.ArcRadiusMm = null;

            CadAudit a = CadAuditRules.Compare(drawing, new[] { straight }, Set(), "fp", Sha);
            Assert.Single(a.Findings, x => x.Code == "moved");
        }

        // ------------------------------------- a COMPOUND curved wall

        /// <summary>
        /// The six arcs Revit 2026 actually exported for ONE curved wall of the
        /// default compound type, measured off the DWG on 2026-08-27: the two
        /// faces, and four more where its material layers meet. Radii in mm about
        /// a common centre, all sweeping the same quarter turn.
        /// </summary>
        private static List<CadArcFact> CompoundWallAsExported()
        {
            double[] radii = { 5176.2125, 5084.1375, 5011.9375, 4988.8875, 4836.4875, 4823.7875 };
            var arcs = new List<CadArcFact>();
            for (int i = 0; i < radii.Length; i++) arcs.Add(Arc(radii[i], 90, 180, "A-WALL-CURVED", "layer" + i));
            return arcs;
        }

        [Fact]
        public void A_compound_curved_wall_is_ONE_wall_and_not_one_per_material_layer()
        {
            // THE DEFECT, from the drawing that had it. Six concentric arcs form
            // several thickness-valid pairings; a reader that keeps the first one
            // it finds and moves on proposes a second curved wall standing inside
            // the first. Two walls were built, live, where the model had one.
            CadInterpretation r = CadInterpretationRules.Interpret(
                new List<CadSegment>(), Set(), Sha, CompoundWallAsExported());

            CadCandidate c = Assert.Single(r.Candidates);
            // The FACES bound the wall: 5176.2125 - 4823.7875.
            Assert.Equal(352.425, c.ThicknessMm.Value, 3);
            Assert.Equal(5000.0, c.Arc.RadiusMm, 3);
        }

        [Fact]
        public void And_it_SAYS_that_the_narrower_pairings_were_its_own_layers()
        {
            // A reading that had rivals must say so. Silently keeping one and
            // dropping the rest is what this file exists to prevent.
            CadCandidate c = CadInterpretationRules.Interpret(
                new List<CadSegment>(), Set(), Sha, CompoundWallAsExported()).Candidates.Single();

            Assert.Contains(c.Assumptions, a => a.Contains("material") && a.Contains("layers"));
        }

        [Fact]
        public void TWO_curved_walls_about_one_centre_stay_TWO_walls()
        {
            // The absorbing must not swallow a real wall. These two share a
            // centre and a sweep, and neither lies inside the other radially -
            // a curved corridor, which is a thing buildings have.
            var arcs = new List<CadArcFact>
            {
                Arc(5100, 90, 180, "A-WALL-CURVED", "outer-face"), Arc(4900, 90, 180, "A-WALL-CURVED", "outer-back"),
                Arc(3100, 90, 180, "A-WALL-CURVED", "inner-face"), Arc(2900, 90, 180, "A-WALL-CURVED", "inner-back")
            };

            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs);
            Assert.Equal(2, r.Candidates.Count);
            Assert.All(r.Candidates, c => Assert.Equal(200.0, c.ThicknessMm.Value, 3));
        }

        [Fact]
        public void A_pairing_that_overlaps_no_wall_ANGULARLY_is_its_own_wall()
        {
            // Same centre, same radii, the other side of the circle. Radial
            // containment alone would absorb it, and it is a different wall.
            var arcs = new List<CadArcFact>
            {
                Arc(5100, 90, 180, "A-WALL-CURVED", "north-face"), Arc(4900, 90, 180, "A-WALL-CURVED", "north-back"),
                Arc(5050, 270, 340, "A-WALL-CURVED", "south-face"), Arc(4950, 270, 340, "A-WALL-CURVED", "south-back")
            };

            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>(), Set(), Sha, arcs);
            Assert.Equal(2, r.Candidates.Count);
        }

    }
}
