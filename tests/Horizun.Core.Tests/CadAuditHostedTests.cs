// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A MATCH IS NOT AGREEMENT.
//
// MEASURED: thirteen devices built from the drawing, hosted on the right walls
// and matched by revision, were all reported as MOVED - the audit measured the
// drawn insertion point against the built one with point_mm, and a hosted device
// is projected onto its wall's face by design. And the opposite hole existed: the
// plan listed anything matched as "already built", whatever differed about it.
//
// These cases fix what "moved" means for a hosted device (along the wall, by
// revision_compare_mm; outwards, up to face_projection_mm), add the comparison
// against the as-built record, and pin the checks on side, hand and reflection.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadAuditHostedTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static CadRequirementSet Set()
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'devices', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5,
                              'face_projection_mm': 300 },
              'rules': [{ 'id': 'r-dev', 'layers': ['E-DEV'], 'produces': 'electrical_fixture',
                          'family_type': 'Receptacle: Duplex', 'level': 'Level 1', 'hosted_on': 'wall',
                          'geometry': { 'from': 'blocks', 'blocks': ['REC'] } }]
            }".Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        /// <summary>A receptacle drawn 120 mm north of a wall that runs along y = 0.</summary>
        private static CadCandidate Drawn(double rotationDegrees = 0, string mirror = "not_mirrored")
        {
            var c = new CadCandidate
            {
                Id = "cadrev:dev-1",
                SemanticId = "cadsem:dev-1",
                GeometryId = "cadgeo:dev-1",
                ProposedKind = "electrical_fixture",
                RuleId = "r-dev",
                Layer = "E-DEV",
                FamilyType = "Receptacle: Duplex",
                HostedOn = "wall",
                RotationRadians = rotationDegrees * System.Math.PI / 180.0,
                MirrorResolution = mirror
            };
            c.Geometry.Add(new CadPoint(1000, 120));
            return c;
        }

        private static CadAuditSubject Built(CadCandidate c, double x, double y, string builtAt = null,
                                             double handX = 1, double handY = 0, bool mirrored = false)
        {
            var s = new CadAuditSubject
            {
                ElementId = 900,
                Category = "Electrical Fixtures",
                TypeName = "Duplex",
                HostElementId = 700,
                HandPlan = new CadVector(handX, handY),
                IsMirrored = mirrored,
                Provenance = new CadProvenance
                {
                    SchemaVersion = 2,
                    CandidateId = c.Id,
                    SemanticId = c.SemanticId,
                    GeometryId = c.GeometryId,
                    RuleId = c.RuleId,
                    Layer = c.Layer,
                    RequirementSetId = "devices",
                    RequirementSetVersion = "1.0.0",
                    RequirementSetSha256 = Set().Sha256,
                    SourceFileSha256 = Sha,
                    BuiltGeometry = builtAt
                }
            };
            s.Geometry.Add(new CadPoint(x, y));
            s.HostLine.Add(new CadPoint(0, 0));
            s.HostLine.Add(new CadPoint(6000, 0));
            return s;
        }

        private static CadAudit Audit(CadCandidate c, CadAuditSubject s) =>
            CadAuditRules.Compare(new[] { c }, new[] { s }, Set(), "fp", Sha);

        [Fact]
        public void A_device_projected_onto_its_wall_face_is_where_the_drawing_puts_it()
        {
            CadCandidate c = Drawn();
            CadAudit a = Audit(c, Built(c, 1000, 76.2, "1000,76.2,0"));

            CadMatch m = Assert.Single(a.Matches);
            Assert.Equal("revision", m.MatchedOn);
            Assert.Equal(0, a.Count("moved"));
            Assert.Equal(0, a.Count(CadFindingCode.MovedSinceBuilt));
            Assert.Equal("agrees", m.State);
        }

        [Fact]
        public void A_device_on_its_walls_end_face_is_projected_along_the_wall_not_moved()
        {
            // the wall runs x 0..6000 on y 0; the symbol is drawn 101.6 mm beyond its end and the device
            // stands ON the end face (MEASURED, campaign 5 synthetic unit)
            CadCandidate c = Drawn();
            c.Geometry.Clear();
            c.Geometry.Add(new CadPoint(6101.6, 0));
            CadAuditSubject s = Built(c, 6000, 0, "6000,0,0");
            s.OnHostEnd = true;
            CadAudit a = Audit(c, s);
            Assert.Equal(0, a.Count("moved"));
            Assert.Equal("agrees", a.Matches.Single().State);

            // on the end face but slid ACROSS it: that is a move
            CadAuditSubject slid = Built(c, 6000, 60, "6000,60,0");
            slid.OnHostEnd = true;
            Assert.Equal(1, Audit(c, slid).Count("moved"));
        }

        [Fact]
        public void A_device_slid_along_its_wall_has_moved_even_though_it_is_close()
        {
            CadCandidate c = Drawn();
            CadAudit a = Audit(c, Built(c, 1060, 76.2));

            Assert.Equal(1, a.Count("moved"));
            CadFinding f = a.Findings.Single(x => x.Code == "moved");
            Assert.Equal(60, (double)f.Evidence["along_wall_mm"], 1);
            Assert.Equal("differs", a.Matches.Single().State);
            Assert.Contains("moved", a.Matches.Single().Differences);
        }

        [Fact]
        public void A_device_further_from_the_drawn_point_than_a_projection_allows_has_moved()
        {
            CadCandidate c = Drawn();
            CadAudit a = Audit(c, Built(c, 1000, 480));

            Assert.Equal(1, a.Count("moved"));
        }

        [Fact]
        public void A_device_moved_in_the_model_after_it_was_built_is_reported_as_such()
        {
            CadCandidate c = Drawn();
            CadAudit a = Audit(c, Built(c, 1040, 76.2, "1000,76.2,0"));

            CadFinding f = a.Findings.Single(x => x.Code == CadFindingCode.MovedSinceBuilt);
            Assert.Equal(40, (double)f.Evidence["shift_mm"], 1);
        }

        [Fact]
        public void An_element_whose_as_built_record_is_missing_is_not_assumed_unmoved_or_moved()
        {
            CadCandidate c = Drawn();
            CadAudit a = Audit(c, Built(c, 1000, 76.2, null));

            Assert.Equal(0, a.Count(CadFindingCode.MovedSinceBuilt));
            Assert.Null(CadAuditRules.ShiftSinceBuilt(null, new List<CadPoint> { new CadPoint(0, 0) }));
        }

        [Fact]
        public void A_device_on_the_other_side_of_its_wall_serves_the_wrong_room()
        {
            // Drawn 120 mm from the centreline of a 152.4 mm wall: outside it, so
            // the drawn point says which side.
            CadCandidate c = Drawn();
            CadAuditSubject built = Built(c, 1000, -76.2);
            built.HostWidthMm = 152.4;
            CadAudit a = Audit(c, built);

            CadFinding f = a.Findings.Single(x => x.Code == CadFindingCode.HostSideDiffers);
            Assert.Equal(CadAuditRules.Blocking, f.Severity);
        }

        [Fact]
        public void Inside_the_wall_the_side_is_the_side_of_the_centreline_it_is_drawn_on()
        {
            // A device drawn over a wall's hatch, 44 mm south of its centreline,
            // built on the south face: no finding; on the north face: a finding.
            //
            // This case used to read the side from the ROTATION and assert it did.
            // MEASURED on a second apartment of the drawing, that assumption is
            // false: against the 49 symbols drawn clearly outside a wall, a
            // receptacle faces its +x only once its insertion's mirror is applied,
            // and a switch faces its +y - and three receptacles built by it stood
            // in the next room while the audit, sharing it, agreed. The face this
            // case expects is unchanged; what decides it is not the rotation.
            CadCandidate c = Drawn(-90);           // the rotation points towards -y
            c.Geometry[0] = new CadPoint(1000, -44.4);
            CadAuditSubject built = Built(c, 1000, -76.2);
            built.HostWidthMm = 152.4;
            Assert.Equal(0, Audit(c, built).Count(CadFindingCode.HostSideDiffers));

            CadAuditSubject wrong = Built(c, 1000, 76.2);
            wrong.HostWidthMm = 152.4;
            CadFinding f = Audit(c, wrong).Findings.Single(x => x.Code == CadFindingCode.HostSideDiffers);
            Assert.Equal(CadDeviceSide.FromCentreline, (string)f.Evidence["side_read_from"]);
        }

        [Fact]
        public void A_rotation_pointing_at_the_other_face_does_not_move_the_device_there()
        {
            // MEASURED (receptacle 854733): drawn 46.9 mm south of the centreline,
            // its mirrored insertion turned 90 degrees, built on the NORTH face
            // because the rotation "pointed" north - and the audit agreed. The
            // symbol serves the room to the south.
            CadCandidate c = Drawn(90, "preserve_requested");
            c.Geometry[0] = new CadPoint(1000, -46.9);
            CadAuditSubject north = Built(c, 1000, 76.2, mirrored: true);
            north.HostWidthMm = 152.4;
            Assert.Equal(1, Audit(c, north).Count(CadFindingCode.HostSideDiffers));

            CadAuditSubject south = Built(c, 1000, -76.2, mirrored: true);
            south.HostWidthMm = 152.4;
            Assert.Equal(0, Audit(c, south).Count(CadFindingCode.HostSideDiffers));
        }

        [Fact]
        public void A_declared_facing_decides_inside_the_wall_even_against_the_centreline()
        {
            CadCandidate c = Drawn(0);
            c.Geometry[0] = new CadPoint(1000, 9.5);      // a hair north of the centreline
            c.Facing = new CadVector(0, -1);              // declared: faces south
            CadAuditSubject north = Built(c, 1000, 76.2);
            north.HostWidthMm = 152.4;
            CadFinding f = Audit(c, north).Findings.Single(x => x.Code == CadFindingCode.HostSideDiffers);
            Assert.Equal(CadDeviceSide.FromDeclaredFacing, (string)f.Evidence["side_read_from"]);

            CadAuditSubject south = Built(c, 1000, -76.2);
            south.HostWidthMm = 152.4;
            Assert.Equal(0, Audit(c, south).Count(CadFindingCode.HostSideDiffers));
        }

        [Fact]
        public void Near_the_centreline_with_no_declared_facing_the_audit_does_not_claim_a_side()
        {
            CadCandidate c = Drawn(90);                   // the rotation is not evidence
            c.Geometry[0] = new CadPoint(1000, 9.5);      // inside the 25 mm dead band
            foreach (double y in new[] { 76.2, -76.2 })
            {
                CadAuditSubject built = Built(c, 1000, y);
                built.HostWidthMm = 152.4;
                Assert.Equal(0, Audit(c, built).Count(CadFindingCode.HostSideDiffers));
            }
        }

        [Fact]
        public void Inside_the_wall_with_a_rotation_along_it_the_side_of_the_centreline_decides()
        {
            // MEASURED: a switch drawn over the hatch 44 mm south of the centreline,
            // pointing along the wall, was placed on the NORTH face beside its twin.
            CadCandidate c = Drawn(180);           // points along the wall
            c.Geometry[0] = new CadPoint(1000, -44.4);
            CadAuditSubject wrong = Built(c, 1000, 76.2, handX: -1);
            wrong.HostWidthMm = 152.4;
            CadFinding f = Audit(c, wrong).Findings.Single(x => x.Code == CadFindingCode.HostSideDiffers);
            Assert.Contains("centreline", (string)f.Evidence["side_read_from"]);

            CadAuditSubject right = Built(c, 1000, -76.2, handX: -1);
            right.HostWidthMm = 152.4;
            Assert.Equal(0, Audit(c, right).Count(CadFindingCode.HostSideDiffers));
        }

        [Fact]
        public void Without_a_host_width_only_a_point_beyond_any_wall_decides_the_side()
        {
            CadCandidate c = Drawn(0);             // rotation along the wall: no side from it
            c.Geometry[0] = new CadPoint(1000, -44.4);
            Assert.Equal(0, Audit(c, Built(c, 1000, 76.2)).Count(CadFindingCode.HostSideDiffers));
        }

        [Fact]
        public void Walls_built_under_another_set_on_a_layer_this_set_does_not_read_are_not_a_disagreement()
        {
            CadCandidate c = Drawn();
            var wall = new CadAuditSubject
            {
                ElementId = 700,
                Category = "Walls",
                Provenance = new CadProvenance
                {
                    SchemaVersion = 2, CandidateId = "cadrev:wall", SemanticId = "cadsem:wall", Layer = "A-WALL",
                    RuleId = "r-wall", RequirementSetId = "walls", RequirementSetVersion = "2.1.0",
                    RequirementSetSha256 = "another-set", SourceFileSha256 = Sha
                }
            };
            wall.Geometry.Add(new CadPoint(0, 0));
            wall.Geometry.Add(new CadPoint(6000, 0));
            CadAudit a = CadAuditRules.Compare(new[] { c }, new[] { Built(c, 1000, 76.2), wall }, Set(), "fp", Sha);

            CadFinding f = a.Findings.Single(x => x.Code == CadFindingCode.BuiltByAnotherRequirementSet);
            Assert.Equal(CadAuditRules.Informational, f.Severity);

            wall.Provenance.Layer = "E-DEV";   // a layer this set DOES read
            a = CadAuditRules.Compare(new[] { c }, new[] { Built(c, 1000, 76.2), wall }, Set(), "fp", Sha);
            Assert.Equal(CadAuditRules.Review,
                         a.Findings.Single(x => x.Code == CadFindingCode.BuiltByAnotherRequirementSet).Severity);
        }

        [Fact]
        public void A_device_hosted_on_nothing_is_unhosted_even_though_it_is_not_a_door()
        {
            CadCandidate c = Drawn();
            CadAuditSubject s = Built(c, 1000, 76.2);
            s.HostElementId = null;
            s.HostLine.Clear();

            Assert.Equal(1, Audit(c, s).Count(CadFindingCode.Unhosted));
        }

        [Fact]
        public void A_reflection_the_drawing_asked_for_and_the_model_lacks_is_a_difference()
        {
            CadCandidate c = Drawn(0, "preserve_requested");
            CadAudit a = Audit(c, Built(c, 1000, 76.2, handX: -1, mirrored: false));

            Assert.Equal(1, a.Count(CadFindingCode.MirrorDiffers));
        }

        [Fact]
        public void A_built_reflection_reverses_the_hand_and_that_is_agreement()
        {
            CadCandidate c = Drawn(0, "preserve_requested");
            CadAudit a = Audit(c, Built(c, 1000, 76.2, handX: -1, mirrored: true));

            Assert.Equal(0, a.Count(CadFindingCode.MirrorDiffers));
            Assert.Equal(0, a.Count(CadFindingCode.OrientationDiffers));
        }

        [Fact]
        public void A_hand_pointing_against_the_drawn_rotation_is_an_orientation_difference()
        {
            CadCandidate c = Drawn(0);
            CadAudit a = Audit(c, Built(c, 1000, 76.2, handX: -1));

            Assert.Equal(1, a.Count(CadFindingCode.OrientationDiffers));
        }

        [Fact]
        public void A_symbol_pointing_into_the_wall_says_nothing_about_the_hand()
        {
            CadCandidate c = Drawn(90);
            CadAudit a = Audit(c, Built(c, 1000, 76.2, handX: -1));

            Assert.Equal(0, a.Count(CadFindingCode.OrientationDiffers));
        }

        [Fact]
        public void Every_new_code_is_counted_even_at_zero()
        {
            CadCandidate c = Drawn();
            JObject counts = Audit(c, Built(c, 1000, 76.2)).CountsByCode();
            foreach (string code in new[] { CadFindingCode.MovedSinceBuilt, CadFindingCode.HostSideDiffers,
                                            CadFindingCode.OrientationDiffers, CadFindingCode.MirrorDiffers })
                Assert.Equal(0, (int)counts[code]);
        }
    }
}
