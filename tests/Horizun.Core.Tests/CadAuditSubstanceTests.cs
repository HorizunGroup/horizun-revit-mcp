// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A MODEL CAN AGREE WITH A DRAWING ABOUT EVERY COORDINATE AND BE MADE OF THE
// WRONG THINGS.
//
// The audit compared positions. Position is the half that is easy to check and
// the half that is usually right - and a wall of the wrong type, a run of the
// wrong size, or a door hosted in nothing all sit exactly where the drawing puts
// them. The last of those is the failure the whole hosting path exists to
// prevent: an unhosted door cuts no opening, and it schedules, tags and renders
// precisely like a real one.
//
// Every check here is CONDITIONAL on the rule having said something. A set that
// names no type is not disagreeing about the type, and an element whose width
// cannot be read is unmeasured rather than wrong.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadAuditSubstanceTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static CadRequirementSet Set(string produces = "wall", string category = "OST_Walls",
                                             string familyType = null, double? thicknessMm = null)
        {
            string family = familyType == null ? "" : ", 'family_type': '" + familyType + "'";
            string thickness = thicknessMm == null ? ""
                : ", 'thickness_mm': " + thicknessMm.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'substance', 'version': '1.0.0', 'title': 'Substance' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'PRODUCES',
                          'category': 'CATEGORY', 'height_mm': 3000FAMILYTHICK,
                          'geometry': { 'from': 'GEOM', 'min_thickness_mm': 100, 'max_thickness_mm': 400,
                                        'min_overlap_fraction': 0.5, 'cluster_radius_mm': 900 } }]
            }".Replace('\'', '"').Replace("PRODUCES", produces).Replace("CATEGORY", category)
              .Replace("FAMILY", family).Replace("THICK", thickness)
              .Replace("GEOM", produces == "wall" ? "double_lines" : "point_clusters");
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static List<CadSegment> Wall(double x0 = 0, double x1 = 6000, double y = 0)
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(x0, y - 100), new CadPoint(x1, y - 100), "A-WALL"),
                new CadSegment(new CadPoint(x0, y + 100), new CadPoint(x1, y + 100), "A-WALL")
            };
        }

        private static List<CadSegment> Symbol(double x = 3000, double y = 0)
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(x, y), new CadPoint(x + 100, y), "A-DOOR"),
                new CadSegment(new CadPoint(x + 100, y), new CadPoint(x + 100, y + 100), "A-DOOR"),
                new CadSegment(new CadPoint(x + 100, y + 100), new CadPoint(x, y + 100), "A-DOOR")
            };
        }

        private static CadAuditSubject Built(CadCandidate c, CadRequirementSet set, long elementId,
                                             string typeName = null, double? widthMm = null,
                                             long? hostId = null)
        {
            return new CadAuditSubject
            {
                ElementId = elementId,
                Category = set.Rules[0].Category,
                TypeName = typeName,
                WidthMm = widthMm,
                HostElementId = hostId,
                Geometry = new List<CadPoint>(c.Geometry),
                Provenance = new CadProvenance
                {
                    SchemaVersion = 1,
                    CandidateId = c.Id, GeometryId = c.GeometryId, SemanticId = c.SemanticId,
                    RuleId = c.RuleId, Layer = c.Layer,
                    RequirementSetSha256 = set.Sha256, SourceFileSha256 = Sha,
                    BuiltGeometry = CadUpdateRules.Encode(c.Geometry)
                }
            };
        }

        private static CadAudit Audit(CadRequirementSet set, List<CadSegment> segs, CadAuditSubject subject)
        {
            CadInterpretation r = CadInterpretationRules.Interpret(segs, set, Sha);
            return CadAuditRules.Compare(r.Candidates, new List<CadAuditSubject> { subject }, set, Sha, Sha);
        }

        // ------------------------------------------------------------- unhosted

        [Fact]
        public void A_door_in_the_right_place_hosted_in_NOTHING_is_a_blocking_finding()
        {
            CadRequirementSet set = Set("door", "OST_Doors", "Single-Flush");
            CadCandidate c = CadInterpretationRules.Interpret(Symbol(), set, Sha).Candidates.Single();
            CadAudit audit = Audit(set, Symbol(), Built(c, set, 1001, "Single-Flush", hostId: null));

            CadFinding f = Assert.Single(audit.Findings, x => x.Code == CadFindingCode.Unhosted);
            Assert.Equal(CadAuditRules.Blocking, f.Severity);
            Assert.Contains("cuts no opening", f.Says);
        }

        [Fact]
        public void A_door_that_IS_hosted_raises_nothing()
        {
            CadRequirementSet set = Set("door", "OST_Doors", "Single-Flush");
            CadCandidate c = CadInterpretationRules.Interpret(Symbol(), set, Sha).Candidates.Single();
            CadAudit audit = Audit(set, Symbol(), Built(c, set, 1001, "Single-Flush", hostId: 555L));

            Assert.DoesNotContain(audit.Findings, x => x.Code == CadFindingCode.Unhosted);
        }

        [Fact]
        public void A_WALL_with_no_host_is_not_unhosted_because_a_wall_hosts_in_nothing()
        {
            // Reading "no host" as a fault for everything would put a blocking
            // finding on every wall, floor and grid in the model.
            CadRequirementSet set = Set();
            CadCandidate c = CadInterpretationRules.Interpret(Wall(), set, Sha).Candidates.Single();
            CadAudit audit = Audit(set, Wall(), Built(c, set, 1001, hostId: null));

            Assert.DoesNotContain(audit.Findings, x => x.Code == CadFindingCode.Unhosted);
        }

        // ---------------------------------------------------------- type_differs

        [Fact]
        public void An_element_of_a_type_the_rule_did_not_ask_for_is_reported()
        {
            CadRequirementSet set = Set("wall", "OST_Walls", "Fire - 200mm");
            CadCandidate c = CadInterpretationRules.Interpret(Wall(), set, Sha).Candidates.Single();
            CadAudit audit = Audit(set, Wall(), Built(c, set, 1001, "Generic - 200mm"));

            CadFinding f = Assert.Single(audit.Findings, x => x.Code == CadFindingCode.TypeDiffers);
            Assert.Equal("Fire - 200mm", (string)f.Evidence["rule_asks_for"]);
            Assert.Equal("Generic - 200mm", (string)f.Evidence["element_is"]);
        }

        [Fact]
        public void A_rule_that_names_NO_type_is_not_disagreeing_about_the_type()
        {
            CadRequirementSet set = Set();
            CadCandidate c = CadInterpretationRules.Interpret(Wall(), set, Sha).Candidates.Single();
            CadAudit audit = Audit(set, Wall(), Built(c, set, 1001, "Anything At All"));

            Assert.DoesNotContain(audit.Findings, x => x.Code == CadFindingCode.TypeDiffers);
        }

        [Fact]
        public void The_same_type_written_two_ways_is_not_a_disagreement()
        {
            // Revit reports an instance's type as the type name alone. Reporting
            // "Basic Wall: Generic - 200mm" against "Generic - 200mm" as a
            // difference would put a finding on every element in the model.
            CadRequirementSet set = Set("wall", "OST_Walls", "Basic Wall: Generic - 200mm");
            CadCandidate c = CadInterpretationRules.Interpret(Wall(), set, Sha).Candidates.Single();
            CadAudit audit = Audit(set, Wall(), Built(c, set, 1001, "Generic - 200mm"));

            Assert.DoesNotContain(audit.Findings, x => x.Code == CadFindingCode.TypeDiffers);
        }

        // ---------------------------------------------------------- size_differs

        [Fact]
        public void A_run_of_the_wrong_thickness_is_reported_with_both_numbers()
        {
            CadRequirementSet set = Set("wall", "OST_Walls", thicknessMm: 200);
            CadCandidate c = CadInterpretationRules.Interpret(Wall(), set, Sha).Candidates.Single();
            CadAudit audit = Audit(set, Wall(), Built(c, set, 1001, widthMm: 150));

            CadFinding f = Assert.Single(audit.Findings, x => x.Code == CadFindingCode.SizeDiffers);
            Assert.Equal(200.0, (double)f.Evidence["drawing_says_mm"], 3);
            Assert.Equal(150.0, (double)f.Evidence["element_measures_mm"], 3);
        }

        [Fact]
        public void An_element_nobody_can_measure_is_UNMEASURED_and_not_the_wrong_size()
        {
            CadRequirementSet set = Set("wall", "OST_Walls", thicknessMm: 200);
            CadCandidate c = CadInterpretationRules.Interpret(Wall(), set, Sha).Candidates.Single();
            CadAudit audit = Audit(set, Wall(), Built(c, set, 1001, widthMm: null));

            Assert.DoesNotContain(audit.Findings, x => x.Code == CadFindingCode.SizeDiffers);
        }

        // ----------------------------------------------------------- the counts

        [Fact]
        public void The_counts_name_every_code_including_the_ones_at_zero()
        {
            // "No unhosted doors" and "hosting was never checked" must not be the
            // same absent key.
            CadRequirementSet set = Set();
            CadCandidate c = CadInterpretationRules.Interpret(Wall(), set, Sha).Candidates.Single();
            JObject counts = Audit(set, Wall(), Built(c, set, 1001)).CountsByCode();

            foreach (string code in CadFindingCode.All) Assert.NotNull(counts[code]);
            Assert.Equal(0, (int)counts[CadFindingCode.Unhosted]);
            Assert.Equal(0, (int)counts[CadFindingCode.TypeDiffers]);
        }

        [Fact]
        public void Every_code_the_audit_can_emit_is_in_the_published_vocabulary()
        {
            // A code missing from the list is a code whose zero is never reported,
            // which is the whole failure this list exists to prevent.
            string source = System.IO.File.ReadAllText(SourceFile());
            var emitted = new HashSet<string>(
                System.Text.RegularExpressions.Regex.Matches(source, @"Code = ""(?<c>[a-z_]+)""")
                    .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups["c"].Value),
                StringComparer.Ordinal);
            var declared = new HashSet<string>(CadFindingCode.All, StringComparer.Ordinal);

            List<string> missing = emitted.Where(c => !declared.Contains(c)).OrderBy(c => c).ToList();
            Assert.True(missing.Count == 0,
                "CadFindingCode.All is missing " + string.Join(", ", missing) +
                " - a code outside the list never has its zero reported.");
        }

        private static string SourceFile()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(
                       System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit"))) dir = dir.Parent;
            Assert.True(dir != null, "the repository root must be findable from the test binary");
            return System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Core", "CadAuditRules.cs");
        }
    }
}
