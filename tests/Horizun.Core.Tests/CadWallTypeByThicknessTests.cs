// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A WALL IS BUILT AT THE THICKNESS THE DRAWING GIVES IT.
//
// MEASURED on two apartments: one declared type built 52 walls at 152.4 mm whose
// drawn thicknesses ran from 125 to 322 mm, and the audit let 18 of them pass
// because it compared widths with the 25 mm revision tolerance. These pin the
// rule key that chooses a type per wall, the tolerance that is only about
// thickness, and an audit that accepts any listed type but not a wrong width.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadWallTypeByThicknessTests
    {
        private static CadRequirementSet Set(string wallTypes, string thicknessTolerance = "",
                                             string produces = "wall")
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'w', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5,
                              'revision_compare_mm': 25 THICK },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL'], 'produces': 'PRODUCES',
                           'family_type': 'Basic Wall: Generic - 6\""', 'level': 'Level 1', 'height_mm': 2700,
                           'join_rule': 'none' WALLTYPES,
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60, 'max_thickness_mm': 400 } } ]
            }";
            doc = doc.Replace("THICK", thicknessTolerance).Replace("WALLTYPES", wallTypes)
                     .Replace("PRODUCES", produces).Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private const string Types =
            ", 'wall_types': { 'types': ['Basic Wall: Generic - 5\\\"', 'Basic Wall: HZ-TEST 161.9'], 'tolerance_mm': 2 }";

        [Fact]
        public void A_wall_rule_lists_its_types_and_the_widths_are_not_declared()
        {
            CadRequirementSet set = Set(Types);
            CadRule r = set.Rules.Single();
            Assert.Equal(2, r.WallTypes.Count);
            Assert.Equal(2, r.WallTypeToleranceMm);
            Assert.Equal("withdraw", r.WallTypeOtherwise);
            Assert.Equal(3.2, set.ThicknessToleranceMm);
            Assert.Equal(1.5, Set("", ", 'thickness_mm': 1.5").ThicknessToleranceMm);
        }

        [Theory]
        [InlineData(", 'wall_types': { 'types': [] }")]
        [InlineData(", 'wall_types': ['Basic Wall: Generic - 5\\\"']")]
        [InlineData(", 'wall_types': { 'types': ['A: B'], 'tolerance_mm': 0 }")]
        [InlineData(", 'wall_types': { 'types': ['A: B'], 'otherwise': 'guess' }")]
        [InlineData(", 'wall_types': { 'types': ['A: B'], 'widths': [127] }")]
        public void A_malformed_wall_types_declaration_is_refused(string declaration)
        {
            Assert.Throws<CadRequirementSetException>(() => Set(declaration));
        }

        [Fact]
        public void Only_a_wall_rule_may_choose_wall_types_and_the_tolerance_is_positive()
        {
            Assert.Throws<CadRequirementSetException>(() => Set(Types, "", "floor"));
            Assert.Throws<CadRequirementSetException>(() => Set("", ", 'thickness_mm': 0"));
        }

        [Fact]
        public void A_wall_row_carries_what_the_model_side_needs_to_choose()
        {
            CadRequirementSet set = Set(Types);
            var lines = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(4000, 0), "A-WALL", CadCurveKind.Line, 0),
                new CadSegment(new CadPoint(0, 161.9), new CadPoint(4000, 161.9), "A-WALL", CadCurveKind.Line, 0)
            };
            CadInterpretation interp = CadInterpretationRules.Interpret(lines, set, "sha");
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, "src", false);
            JObject row = plan.Actions.Single().Arguments;
            Assert.Equal(new[] { "Basic Wall: Generic - 5\"", "Basic Wall: HZ-TEST 161.9" },
                         ((JArray)row["wall_type_choices"]).Select(x => (string)x).ToArray());
            Assert.Equal(161.9, row.Value<double>("interpreted_thickness_mm"), 3);
            Assert.Equal(2, row.Value<double>("wall_type_tolerance_mm"));
            Assert.Equal("withdraw", (string)row["wall_type_otherwise"]);

            CadConversionPlan without = CadConversionPlanRules.Plan(
                CadInterpretationRules.Interpret(lines, Set(""), "sha"), Set(""), "src", false);
            Assert.Null(without.Actions.Single().Arguments["wall_type_choices"]);
        }

        private static CadCandidate Wall(double thickness) => new CadCandidate
        {
            Id = "cadrev:w", SemanticId = "cadsem:w", GeometryId = "cadgeo:w", ProposedKind = "wall",
            RuleId = "r-wall", Layer = "A-WALL", FamilyType = "Basic Wall: Generic - 6\"", ThicknessMm = thickness,
            Geometry = { new CadPoint(0, 0), new CadPoint(4000, 0) }
        };

        private static CadAuditSubject Built(CadCandidate c, double width, string type) => new CadAuditSubject
        {
            ElementId = 7, Category = "Walls", TypeName = type, WidthMm = width,
            Geometry = { new CadPoint(0, 0), new CadPoint(4000, 0) },
            Provenance = new CadProvenance
            {
                SchemaVersion = 2, CandidateId = c.Id, SemanticId = c.SemanticId, GeometryId = c.GeometryId,
                RuleId = c.RuleId, Layer = c.Layer, SourceFileSha256 = "sha"
            }
        };

        [Fact]
        public void The_audit_measures_width_with_the_thickness_tolerance_not_the_revision_one()
        {
            CadRequirementSet set = Set("");
            CadCandidate c = Wall(146.0);
            // 6.4 mm off: within the 25 mm revision tolerance, a wrong wall all the same.
            CadAudit a = CadAuditRules.Compare(new[] { c }, new[] { Built(c, 152.4, "Generic - 6\"") }, set, "src", "sha");
            CadFinding f = Assert.Single(a.Findings, x => x.Code == CadFindingCode.SizeDiffers);
            Assert.Equal(3.2, (double)f.Evidence["tolerance_mm"]);
            CadAudit ok = CadAuditRules.Compare(new[] { c }, new[] { Built(c, 146.1, "Generic - 6\"") }, set, "src", "sha");
            Assert.DoesNotContain(ok.Findings, x => x.Code == CadFindingCode.SizeDiffers);
        }

        [Fact]
        public void Any_listed_type_is_the_type_the_rule_asked_for()
        {
            CadRequirementSet set = Set(Types);
            CadCandidate c = Wall(161.9);
            CadAudit listed = CadAuditRules.Compare(new[] { c }, new[] { Built(c, 161.9, "HZ-TEST 161.9") }, set, "src", "sha");
            Assert.DoesNotContain(listed.Findings, x => x.Code == CadFindingCode.TypeDiffers);
            Assert.DoesNotContain(listed.Findings, x => x.Code == CadFindingCode.SizeDiffers);
            CadAudit other = CadAuditRules.Compare(new[] { c }, new[] { Built(c, 161.9, "Exterior - Brick") }, set, "src", "sha");
            Assert.Contains(other.Findings, x => x.Code == CadFindingCode.TypeDiffers);
        }

        [Fact]
        public void The_plan_chooses_the_type_where_the_model_is_open_in_both_routes()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            string plan = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, "src", "Horizun.Revit",
                "Commands", "PlanFromCadCommand.cs"));
            Assert.Contains("ResolveWallTypes(doc, creates, resolved, withdrawn, wallTypesChosen);", plan);
            Assert.Contains("ResolveWallTypes(doc, creates, resolved, withdrawn, null);", plan);
            Assert.Contains("\"no_wall_type_for_this_thickness\"", plan);
            Assert.Contains("\"wall_types_fit_equally\"", plan);
            foreach (string key in new[] { "wall_type_choices", "wall_type_tolerance_mm", "wall_type_otherwise",
                                           "interpreted_thickness_mm" })
                Assert.NotNull(Horizun.Contracts.ToolInputRules.ValidateCreation(
                    new JObject { ["kind"] = "wall", [key] = 1 }, "wall"));
        }
    }
}
