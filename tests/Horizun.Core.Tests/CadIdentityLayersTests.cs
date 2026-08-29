// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE THREE IDENTITIES, and why one is not enough.
//
// The first version of this layer had a single surrogate that folded the DWG's
// file SHA into every entity id. It is correct for one job and catastrophic for
// another, and these tests pin the difference:
//
//   Re-issue a drawing with ONE wall moved, and the file SHA changes. With a
//   single source-hashed id, EVERY surviving entity gets a new id - so an
//   incremental update sees the entire building deleted and rebuilt, and an
//   audit matches nothing. The identity that must survive a re-issue cannot
//   contain the thing that changes on every re-issue.
//
// So: geometry_id (what it IS), semantic_id (what it is, on which layer), and
// revision_id (that entity, in THIS issue of the file). Each answers a
// different question and none of them substitutes for another.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadIdentityLayersTests
    {
        private static List<CadPoint> Wall() =>
            new List<CadPoint> { new CadPoint(0, 0), new CadPoint(6000, 0) };

        // ---- geometry_id: what the thing IS ---------------------------------

        [Fact]
        public void Geometry_identity_ignores_which_file_it_came_from()
        {
            // The whole point: a re-issued drawing must not rename everything.
            Assert.Equal(
                CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0),
                CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0));
        }

        [Fact]
        public void Geometry_identity_ignores_the_layer()
        {
            // This is what lets a RELAYERED entity be recognised as the same
            // drawn thing that moved layer, rather than as a deletion plus a
            // creation - which is the difference between "you renamed a layer"
            // and "you rebuilt the building".
            string a = CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0);
            Assert.Equal(a, CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0));
        }

        [Fact]
        public void Geometry_identity_ignores_the_direction_it_was_drawn_in()
        {
            var backwards = new List<CadPoint> { new CadPoint(6000, 0), new CadPoint(0, 0) };
            Assert.Equal(
                CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0),
                CadIdentity.GeometryId(CadCurveKind.Line, backwards, 1.0));
        }

        [Fact]
        public void Geometry_identity_ignores_where_a_closed_loop_starts()
        {
            // A rectangle is the same rectangle whichever corner the draughtsman
            // clicked first. Without this, redrawing a room boundary from a
            // different corner reads as a different room.
            var fromA = new List<CadPoint>
            {
                new CadPoint(0, 0), new CadPoint(4000, 0), new CadPoint(4000, 3000), new CadPoint(0, 3000)
            };
            var fromC = new List<CadPoint>
            {
                new CadPoint(4000, 3000), new CadPoint(0, 3000), new CadPoint(0, 0), new CadPoint(4000, 0)
            };
            Assert.Equal(
                CadIdentity.GeometryId(CadCurveKind.Polyline, fromA, 1.0, closed: true),
                CadIdentity.GeometryId(CadCurveKind.Polyline, fromC, 1.0, closed: true));
        }

        [Fact]
        public void A_closed_loop_wound_the_other_way_is_the_same_loop()
        {
            var ccw = new List<CadPoint>
            {
                new CadPoint(0, 0), new CadPoint(4000, 0), new CadPoint(4000, 3000), new CadPoint(0, 3000)
            };
            var cw = Enumerable.Reverse(ccw).ToList();
            Assert.Equal(
                CadIdentity.GeometryId(CadCurveKind.Polyline, ccw, 1.0, closed: true),
                CadIdentity.GeometryId(CadCurveKind.Polyline, cw, 1.0, closed: true));
        }

        [Fact]
        public void Geometry_identity_still_separates_things_that_really_differ()
        {
            var moved = new List<CadPoint> { new CadPoint(0, 500), new CadPoint(6000, 500) };
            var longer = new List<CadPoint> { new CadPoint(0, 0), new CadPoint(9000, 0) };
            string baseline = CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0);
            Assert.NotEqual(baseline, CadIdentity.GeometryId(CadCurveKind.Line, moved, 1.0));
            Assert.NotEqual(baseline, CadIdentity.GeometryId(CadCurveKind.Line, longer, 1.0));
            Assert.NotEqual(baseline, CadIdentity.GeometryId(CadCurveKind.Arc, Wall(), 1.0));
        }

        [Fact]
        public void Geometry_identity_is_the_same_for_the_same_physical_thing_in_other_units()
        {
            // Everything reaching identity is already normalised to millimetres,
            // so a drawing authored in metres and one in millimetres describing
            // the same 6 m wall must agree. If they did not, changing a link's
            // declared unit would look like rebuilding the model.
            var inMm = new List<CadPoint> { new CadPoint(0, 0), new CadPoint(6000, 0) };
            var fromMetres = new List<CadPoint>
            {
                new CadPoint(0 * CadUnits.MillimetresPer("meter").Value, 0),
                new CadPoint(6 * CadUnits.MillimetresPer("meter").Value, 0)
            };
            Assert.Equal(
                CadIdentity.GeometryId(CadCurveKind.Line, inMm, 1.0),
                CadIdentity.GeometryId(CadCurveKind.Line, fromMetres, 1.0));
        }

        // ---- semantic_id: what it is, on which layer ------------------------

        [Fact]
        public void Semantic_identity_separates_the_same_line_on_two_layers()
        {
            Assert.NotEqual(
                CadIdentity.SemanticId("A-WALL", "root", CadCurveKind.Line, Wall(), 1.0),
                CadIdentity.SemanticId("A-FURN", "root", CadCurveKind.Line, Wall(), 1.0));
        }

        [Fact]
        public void Semantic_identity_survives_a_re_issue_of_the_same_drawing()
        {
            // The defect this whole split exists to fix.
            Assert.Equal(
                CadIdentity.SemanticId("A-WALL", "root", CadCurveKind.Line, Wall(), 1.0),
                CadIdentity.SemanticId("A-WALL", "root", CadCurveKind.Line, Wall(), 1.0));
        }

        [Fact]
        public void Semantic_identity_separates_the_same_geometry_in_different_blocks()
        {
            Assert.NotEqual(
                CadIdentity.SemanticId("A-WALL", "root", CadCurveKind.Line, Wall(), 1.0),
                CadIdentity.SemanticId("A-WALL", "root/BLOCK#2", CadCurveKind.Line, Wall(), 1.0));
        }

        [Fact]
        public void A_relayered_entity_keeps_its_geometry_id_and_loses_its_semantic_id()
        {
            // This pair is exactly how an incremental run tells "somebody moved
            // this to another layer" from "somebody deleted it and drew a new one".
            string geomBefore = CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0);
            string geomAfter = CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0);
            Assert.Equal(geomBefore, geomAfter);
            Assert.NotEqual(
                CadIdentity.SemanticId("A-WALL-EXTR", "root", CadCurveKind.Line, Wall(), 1.0),
                CadIdentity.SemanticId("A-WALL-INTR", "root", CadCurveKind.Line, Wall(), 1.0));
        }

        // ---- revision_id: that entity, in THIS issue ------------------------

        [Fact]
        public void Revision_identity_changes_when_the_file_changes()
        {
            string semantic = CadIdentity.SemanticId("A-WALL", "root", CadCurveKind.Line, Wall(), 1.0);
            Assert.NotEqual(
                CadIdentity.RevisionId("sha-of-issue-A", semantic),
                CadIdentity.RevisionId("sha-of-issue-B", semantic));
        }

        [Fact]
        public void Revision_identity_is_stable_within_one_issue()
        {
            string semantic = CadIdentity.SemanticId("A-WALL", "root", CadCurveKind.Line, Wall(), 1.0);
            Assert.Equal(
                CadIdentity.RevisionId("sha-of-issue-A", semantic),
                CadIdentity.RevisionId("sha-of-issue-A", semantic));
        }

        [Fact]
        public void The_three_identities_are_visibly_different_kinds_of_thing()
        {
            string geom = CadIdentity.GeometryId(CadCurveKind.Line, Wall(), 1.0);
            string semantic = CadIdentity.SemanticId("A-WALL", "root", CadCurveKind.Line, Wall(), 1.0);
            string revision = CadIdentity.RevisionId("sha", semantic);
            // Prefixed so a log, a finding or a provenance record can never be
            // read as carrying the wrong one.
            Assert.StartsWith("cadgeo:", geom);
            Assert.StartsWith("cadsem:", semantic);
            Assert.StartsWith("cadrev:", revision);
            Assert.NotEqual(geom, semantic);
            Assert.NotEqual(semantic, revision);
        }

        // ---- order independence ---------------------------------------------

        [Fact]
        public void Interpreting_the_same_drawing_twice_in_a_different_enumeration_order_gives_the_same_ids()
        {
            var forward = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(6000, 0), "A-WALL-EXTR"),
                new CadSegment(new CadPoint(0, 200), new CadPoint(6000, 200), "A-WALL-EXTR"),
                new CadSegment(new CadPoint(0, 3000), new CadPoint(4000, 3000), "A-SLAB"),
                new CadSegment(new CadPoint(4000, 3000), new CadPoint(4000, 6000), "A-SLAB"),
                new CadSegment(new CadPoint(4000, 6000), new CadPoint(0, 6000), "A-SLAB"),
                new CadSegment(new CadPoint(0, 6000), new CadPoint(0, 3000), "A-SLAB")
            };
            var shuffled = new List<CadSegment>
            {
                forward[3], forward[0], forward[5], forward[1], forward[4], forward[2]
            };

            var set = Newtonsoft.Json.Linq.JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'demo', 'version': '1', 'title': 't' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1, 'gap_mm': 25, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [
                { 'id': 'walls', 'layers': ['A-WALL*'], 'produces': 'wall',
                  'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500 } },
                { 'id': 'slabs', 'layers': ['A-SLAB'], 'produces': 'floor',
                  'geometry': { 'from': 'closed_loops' } } ]
            }".Replace('\'', '"'));
            CadRequirementSet rules = CadRequirementSet.Load(set);

            var a = CadInterpretationRules.Interpret(forward, rules, "sha")
                .Candidates.Select(c => c.SemanticId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var b = CadInterpretationRules.Interpret(shuffled, rules, "sha")
                .Candidates.Select(c => c.SemanticId).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.NotEmpty(a);
            Assert.Equal(a, b);
        }
    }
}
