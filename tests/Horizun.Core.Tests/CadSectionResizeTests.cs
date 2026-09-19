// Copyright (c) Horizun. A rectangular duct section the drawing's labels change, seen by an update.
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadSectionResizeTests
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


        private static CadUpdate Plan(double askW, double askH, double? heldW, double? heldH)
        {
            CadRequirementSet set = Set();
            List<CadCandidate> a = Read(Wall(0, 6000), set, RevA);
            CadAuditSubject held = Built(a[0], set, RevA, 1001, widthMm: heldW);
            held.HeightMm = heldH;
            List<CadCandidate> b = Read(Wall(0, 6000), set, RevB);
            b[0].SectionWidthMm = askW;
            b[0].SectionHeightMm = askH;
            return CadUpdateRules.Plan(b, new List<CadAuditSubject> { held }, set, RevB, lineage: new[] { RevA });
        }

        [Fact]
        public void A_label_that_still_says_the_built_size_changes_nothing()
        {
            CadUpdate update = Plan(203.2, 152.4, 203.2, 152.4);
            CadUpdateAction action = Assert.Single(update.Actions);
            Assert.Equal("leave", action.Kind);
            Assert.Equal(CadChange.Unchanged, action.Classification);
        }

        [Fact]
        public void A_label_grown_from_8x6_to_10x6_is_a_resize_held_for_a_person_with_both_numbers()
        {
            CadUpdate update = Plan(254.0, 152.4, 203.2, 152.4);
            CadUpdateAction action = Assert.Single(update.Actions);
            Assert.Equal("review", action.Kind);
            Assert.Equal(CadChange.Resized, action.Classification);
            Assert.False(action.Automatic);
            Assert.Equal(1001L, action.ElementId);
            Assert.True(action.Evidence.Value<bool>("section"));
            Assert.Equal(254.0, action.Evidence.Value<double>("drawing_asks_width_mm"));
            Assert.Equal(152.4, action.Evidence.Value<double>("drawing_asks_height_mm"));
            Assert.Equal(203.2, action.Evidence.Value<double>("element_width_mm"));
            Assert.Null(action.Evidence["not_resizable"]);
        }

        [Fact]
        public void A_height_change_alone_is_seen()
        {
            CadUpdateAction action = Assert.Single(Plan(203.2, 203.2, 203.2, 152.4).Actions);
            Assert.Equal(CadChange.Resized, action.Classification);
        }

        [Fact]
        public void A_round_duct_is_never_offered_a_rectangular_section()
        {
            // An equal-area circle is no substitute, and the reverse is a different type.
            CadUpdateAction action = Assert.Single(Plan(254.0, 152.4, 200.0, null).Actions);
            Assert.Equal(CadChange.Resized, action.Classification);
            Assert.Equal("held_round", (string)action.Evidence["not_resizable"]);
        }

        [Fact]
        public void The_retype_decision_carries_out_a_section_resize_and_nothing_else_is_touched()
        {
            CadUpdate update = Plan(254.0, 152.4, 203.2, 152.4);
            List<string> errors = CadDecisions.Apply(update, new List<CadDecision>
                { new CadDecision { ElementId = 1001, Decision = CadDecisions.Retype } });
            Assert.Empty(errors);
            CadUpdateAction action = Assert.Single(update.Actions);
            Assert.Equal(CadDecisions.Retype, action.Kind);
            Assert.True(action.Automatic);
            Assert.Empty(update.Of("create"));
            Assert.Empty(update.Of("orphan"));
        }
    }
}
