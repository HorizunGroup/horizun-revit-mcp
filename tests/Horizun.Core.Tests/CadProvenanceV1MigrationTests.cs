// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE v1 -> v2 MIGRATION, at a desk.
//
// Two separate things are proved here, and they answer two different doubts.
//
// 1. WHAT "v1" MEANS. The v1 schema is read and never written, so the only
//    description of it left in the working tree is CadProvenanceV1Shape, and a
//    fixture built from a description nobody checks is a fixture that proves the
//    migration against a schema no release ever wrote. So the shape is pinned
//    literally AND cross-checked against the field-name constants still standing
//    in CadProvenanceStore.cs: the v1 field set is exactly the v2 field set minus
//    the five fields v2 added, which is what the store's own comments say it is.
//    Change either side and this fails.
//
// 2. WHY AN AMBIGUOUS v1 RECORD MUST STOP THE RUN. An element two placements
//    could have built is out of scope - and out of scope is not safe by itself.
//    The drawing entity that built it then matches nothing in scope and comes
//    back as a `create`, so applying the plan builds a second wall on top of the
//    one already standing. That harm is demonstrated here first, and the refusal
//    is asserted second, because a refusal whose cost nobody measured is a
//    refusal somebody will delete.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadProvenanceV1MigrationTests
    {
        private const string FileX = "sha-of-file-x";
        private const string P1 = "uid-placement-1";
        private const string P2 = "uid-placement-2";

        // ------------------------------------------------------------ the shape

        private static DirectoryInfo Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return d;
        }

        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(Root().FullName, Path.Combine(parts)));

        private static string Store() => Source("src", "Horizun.Revit", "Core", "CadProvenanceStore.cs");
        private static string Fixture() => Source("src", "Horizun.Revit", "Core", "CadProvenanceV1Fixture.cs");

        [Fact]
        public void The_v1_shape_is_pinned_field_by_field_in_the_order_v1_declared_them()
        {
            Assert.Equal(new Guid("7b2f4c18-5d3a-4e6b-9a71-3c0f8e2d15a4"), CadProvenanceV1Shape.SchemaGuid);
            Assert.Equal("HorizunCadProvenance", CadProvenanceV1Shape.SchemaName);
            Assert.Equal(1, CadProvenanceV1Shape.Version);
            Assert.Equal("HRZN", CadProvenanceV1Shape.VendorId);
            Assert.Equal("Public", CadProvenanceV1Shape.ReadAccessLevel);
            Assert.Equal("Vendor", CadProvenanceV1Shape.WriteAccessLevel);

            IList<CadProvenanceV1Field> fields = CadProvenanceV1Shape.Fields;
            Assert.Equal(15, fields.Count);
            Assert.Equal(
                new[]
                {
                    "SchemaVersion",
                    "CandidateId", "GeometryId", "SemanticId", "RuleId",
                    "RequirementSetId", "RequirementSetVersion", "RequirementSetSha256",
                    "SourceFingerprint", "SourceFileSha256", "Layer", "PlanFingerprint",
                    "BuiltGeometry", "WrittenUtc",
                    "Confidence"
                },
                fields.Select(f => f.Name).ToArray());

            Assert.Equal("int", fields[0].ClrType);
            Assert.All(fields.Skip(1).Take(13), f => Assert.Equal("string", f.ClrType));
            Assert.Equal("double", fields[14].ClrType);

            // EXACTLY ONE SPEC'D FIELD. A double with no unit spec makes Revit
            // refuse the whole entity, and a spec on a string is not a thing.
            Assert.Equal(new[] { "Confidence" }, fields.Where(f => f.NumberSpec).Select(f => f.Name).ToArray());
        }

        [Fact]
        public void The_v1_field_set_is_the_v2_field_set_minus_the_five_fields_v2_added()
        {
            // DERIVED FROM THE STORE'S OWN CONSTANTS, not from a second literal
            // list. The store declares every field name it knows, and marks the
            // v2-only ones with a comment; this reads both and asserts the
            // arithmetic the store's header claims.
            string store = Store();
            var declared = Regex.Matches(store, @"private const string Field\w+ = ""(?<name>[A-Za-z0-9_]+)"";")
                                .Cast<Match>().Select(m => m.Groups["name"].Value).ToList();
            Assert.Contains("SchemaVersion", declared);
            Assert.Contains("Confidence", declared);

            int v2Only = store.IndexOf("// v2 only", StringComparison.Ordinal);
            Assert.True(v2Only > 0, "CadProvenanceStore must still mark where the v2-only fields begin");
            var v2Names = Regex.Matches(store.Substring(v2Only), @"private const string Field\w+ = ""(?<name>[A-Za-z0-9_]+)"";")
                               .Cast<Match>().Select(m => m.Groups["name"].Value).ToList();

            Assert.Equal(CadProvenanceV1Shape.FieldsAddedByV2.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                         v2Names.OrderBy(x => x, StringComparer.Ordinal).ToArray());

            var v1Names = declared.Where(n => !v2Names.Contains(n)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(v1Names,
                         CadProvenanceV1Shape.Fields.Select(f => f.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void The_v1_guid_and_the_documentation_are_the_ones_the_store_still_carries()
        {
            string store = Store();
            Assert.Contains("SchemaGuidV1 = new Guid(\"" + CadProvenanceV1Shape.SchemaGuid + "\")", store);
            Assert.Contains("SchemaName = \"" + CadProvenanceV1Shape.SchemaName + "\"", store);
            // v1 and v2 carry the same documentation string; the store is where it lives.
            Assert.Contains("Horizun CAD provenance: which DWG entity, under which requirement-set rule, produced this element. ",
                            store);
            Assert.Contains(CadProvenanceV1Shape.Documentation.Substring(0, 60), store);
        }

        // ------------------------------------------------------------ the fixture

        [Fact]
        public void The_fixture_is_the_only_writer_of_the_v1_guid_and_builds_it_from_the_shape()
        {
            string fixture = Fixture();
            Assert.Contains("new SchemaBuilder(CadProvenanceV1Shape.SchemaGuid)", fixture);
            // The field list comes from the shape. A literal list here would be a
            // second definition of v1, free to drift from the one under test.
            Assert.Contains("foreach (CadProvenanceV1Field field in CadProvenanceV1Shape.Fields)", fixture);
            Assert.Contains("builder.SetDocumentation(CadProvenanceV1Shape.Documentation)", fixture);
            Assert.Contains("builder.SetVendorId(CadProvenanceV1Shape.VendorId)", fixture);
            // It verifies by re-reading, like everything else that writes here.
            Assert.Contains("JObject after = Inspect(doc, ids);", fixture);

            // And it is the ONLY SchemaBuilder anywhere that takes the v1 GUID.
            foreach (string file in Directory.GetFiles(Path.Combine(Root().FullName, "src"), "*.cs",
                                                       SearchOption.AllDirectories))
            {
                if (file.EndsWith("CadProvenanceV1Fixture.cs", StringComparison.Ordinal)) continue;
                string text = File.ReadAllText(file);
                Assert.False(Regex.IsMatch(text, @"new SchemaBuilder\(\s*(CadProvenanceStore\.)?SchemaGuidV1"),
                    Path.GetFileName(file) + " builds the retired v1 schema; only the fixture may");
                Assert.False(Regex.IsMatch(text, @"new SchemaBuilder\(\s*CadProvenanceV1Shape\.SchemaGuid"),
                    Path.GetFileName(file) + " builds the retired v1 schema; only the fixture may");
            }
        }

        [Fact]
        public void No_product_command_can_reach_the_fixture()
        {
            // A fixture that a command can call is not a fixture. The live
            // harness reaches it by reflection through horizun_execute_python,
            // which the machine owner has to have granted first.
            string commands = Path.Combine(Root().FullName, "src", "Horizun.Revit", "Commands");
            foreach (string file in Directory.GetFiles(commands, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                Assert.False(text.Contains("CadProvenanceV1Fixture"),
                    Path.GetFileName(file) + " names CadProvenanceV1Fixture; no product command may");
                Assert.False(text.Contains("CadProvenanceV1Shape"),
                    Path.GetFileName(file) + " names CadProvenanceV1Shape; it is fixture input only");
            }
            foreach (string file in Directory.GetFiles(Path.Combine(Root().FullName, "src", "Horizun.Server"), "*.cs",
                                                       SearchOption.AllDirectories))
            {
                Assert.False(File.ReadAllText(file).Contains("CadProvenanceV1"),
                    Path.GetFileName(file) + " exposes the v1 fixture through the server; nothing may");
            }
        }

        // ------------------------------------------------------------ the harm

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

        /// <summary>Two walls on one drawing, so a model can hold one claimable element and one ambiguous one.</summary>
        private static List<CadSegment> TwoWalls()
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, -100), new CadPoint(6000, -100), "A-WALL"),
                new CadSegment(new CadPoint(0, 100), new CadPoint(6000, 100), "A-WALL"),
                new CadSegment(new CadPoint(0, 3900), new CadPoint(6000, 3900), "A-WALL"),
                new CadSegment(new CadPoint(0, 4100), new CadPoint(6000, 4100), "A-WALL")
            };
        }

        private static List<CadCandidate> Read(CadRequirementSet set) =>
            CadInterpretationRules.Interpret(TwoWalls(), set, FileX).Candidates.ToList();

        private static CadPlacement Placement(long instanceId, string uid) => new CadPlacement
        {
            ElementId = instanceId,
            PlacementId = uid,
            FileSha256 = FileX,
            ExternalPath = "C:\\drawings\\x.dwg",
            SourceFingerprint = "cadsrc:" + uid,
            TransformFingerprint = "cadtf:0",
            OriginMm = new[] { 0.0, 0.0, 0.0 },
            BasisX = new[] { 1.0, 0.0, 0.0 },
            BasisY = new[] { 0.0, 1.0, 0.0 },
            Scale = 1.0
        };

        private static CadAuditSubject Built(CadCandidate from, CadRequirementSet set, long id, string placementId)
        {
            var s = new CadAuditSubject
            {
                ElementId = id,
                Category = "Walls",
                TypeName = "Generic - 200mm",
                Geometry = new List<CadPoint>(from.Geometry),
                Provenance = new CadProvenance
                {
                    SchemaVersion = placementId == null ? 1 : 2,
                    CandidateId = from.Id,
                    GeometryId = from.GeometryId,
                    SemanticId = from.SemanticId,
                    RuleId = from.RuleId,
                    Layer = from.Layer,
                    RequirementSetSha256 = set.Sha256,
                    SourceFileSha256 = FileX,
                    // A v1 record written under a placement that has since been
                    // re-issued or nudged: the fingerprint no longer equals any
                    // placement's, which is the ordinary case for an update.
                    SourceFingerprint = "cadsrc:as-built-under-an-earlier-issue",
                    BuiltGeometry = CadUpdateRules.Encode(from.Geometry)
                }
            };
            if (placementId != null)
            {
                s.Provenance.PlacementId = placementId;
                s.Provenance.PlacementTransform = "cadtf:0";
                s.Provenance.PlacementOrigin = "0,0,0";
                s.Provenance.PlacementBasis = "1,0,0;0,1,0;1";
            }
            return s;
        }

        [Fact]
        public void An_ambiguous_v1_element_left_out_of_scope_comes_back_as_a_CREATE_and_that_is_the_harm()
        {
            // THE MEASUREMENT THE REFUSAL EXISTS FOR. One wall is claimable (v2,
            // this placement), the other is a v1 record two placements could have
            // built. Planned rather than refused, the second wall's drawing entity
            // matches nothing in scope - so the plan proposes to CREATE it, and
            // applying that puts a second wall exactly where one already stands.
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set);
            Assert.Equal(2, drawn.Count);

            CadPlacement p1 = Placement(10, P1);
            CadPlacement p2 = Placement(20, P2);
            var model = new List<CadAuditSubject>
            {
                Built(drawn[0], set, 1000, P1),   // claimable: v2, this placement
                Built(drawn[1], set, 2000, null)  // v1: either placement could have built it
            };

            CadUpdateScope scope = CadPlacementRules.Resolve(model, p1, null, null,
                                                            new List<CadPlacement> { p1, p2 });

            Assert.Contains(1000L, scope.Claimed);
            CadScopeExclusion why = Assert.Single(scope.AmbiguousV1);
            Assert.Equal(2000, why.ElementId);
            // The run is NOT scope_unidentified: something is claimable, which is
            // exactly why the old code went on.
            Assert.Equal(CadUpdateScope.Identified, scope.Verdict);

            CadUpdate planned = CadUpdateRules.Plan(drawn, model, set, scope, null, null, null, null);

            CadUpdateAction duplicate = Assert.Single(planned.Of("create"));
            Assert.Equal(drawn[1].SemanticId, duplicate.SemanticId);
            Assert.True(duplicate.Automatic, "and it is an AUTOMATIC create: nobody is asked before it is built");
            Assert.DoesNotContain(planned.Actions, a => a.ElementId == 2000);
        }

        [Fact]
        public void So_the_refusal_names_every_placement_and_forbids_the_advice_that_would_double_the_building()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set);
            CadPlacement p1 = Placement(10, P1);
            CadPlacement p2 = Placement(20, P2);
            var model = new List<CadAuditSubject>
            {
                Built(drawn[0], set, 1000, P1),
                Built(drawn[1], set, 2000, null)
            };
            CadUpdateScope scope = CadPlacementRules.Resolve(model, p1, null, null,
                                                            new List<CadPlacement> { p1, p2 });

            string refusal = CadPlacementRules.AmbiguousV1Refusal(scope, "HZ_WRITE");

            Assert.StartsWith("ambiguous_v1:", refusal);
            Assert.Contains("HZ_WRITE", refusal);
            Assert.Contains("2000", refusal);
            // BOTH placements, by instance and by placement id: "two placements
            // could have built it" is not actionable until a reader knows which.
            Assert.Contains("10 [" + P1 + "]", refusal);
            Assert.Contains("20 [" + P2 + "]", refusal);
            Assert.Contains("NOTHING WAS PLANNED", refusal);
            Assert.Contains("second copy", refusal);
            // And it must NOT send the caller to the first-conversion command,
            // which is what scope_unidentified says and what would build the
            // whole drawing again.
            Assert.Contains("Do NOT run horizun_plan_from_cad", refusal);
        }

        [Fact]
        public void A_run_with_nothing_ambiguous_gets_no_ambiguity_refusal()
        {
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set);
            CadPlacement p1 = Placement(10, P1);
            var model = new List<CadAuditSubject> { Built(drawn[0], set, 1000, P1) };
            CadUpdateScope scope = CadPlacementRules.Resolve(model, p1, null, null,
                                                            new List<CadPlacement> { p1 });

            Assert.Empty(scope.AmbiguousV1);
            Assert.Equal("ambiguous_v1: nothing is ambiguous.",
                         CadPlacementRules.AmbiguousV1Refusal(scope, "HZ_WRITE"));
        }

        [Fact]
        public void The_only_v1_record_of_a_file_placed_ONCE_is_migrated_and_restamped_rather_than_refused()
        {
            // The other half of the same decision: ambiguity refuses, and the
            // unambiguous case must still migrate - or the guard would have
            // turned every legacy model into a refusal.
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set);
            CadPlacement p1 = Placement(10, P1);
            var model = new List<CadAuditSubject>
            {
                Built(drawn[0], set, 1000, null),
                Built(drawn[1], set, 2000, null)
            };

            CadUpdateScope scope = CadPlacementRules.Resolve(model, p1, null, null,
                                                            new List<CadPlacement> { p1 });

            Assert.Empty(scope.AmbiguousV1);
            Assert.Equal(2, scope.MigratedFromV1.Count);
            Assert.Equal(CadUpdateScope.Identified, scope.Verdict);

            // AND THE CENSUS COUNTS THEM. exists.v1_files is what a caller reads to
            // decide whether legacy provenance is still in the model, and it used to
            // be written only on the slow path - so the records identified by their
            // exact source fingerprint, the confident case, were reported as no
            // legacy provenance at all. Measured live 2026-09-03: four migrated, {}.
            var v1Files = (Newtonsoft.Json.Linq.JObject)scope.Exists["v1_files"];
            Assert.NotEmpty(v1Files.Properties());
            Assert.Equal(2, v1Files.Properties().Sum(x => (int)x.Value));

            CadUpdate planned = CadUpdateRules.Plan(drawn, model, set, scope, null, null, null, null);
            Assert.Empty(planned.Of("create"));
            Assert.Empty(planned.Of("orphan"));
            Assert.Equal(2, planned.Of("leave").Count());
            // Both are `leave`, which is what the plan's Restamp walks: a v1
            // record claimed by this run is rewritten as v2 without its geometry
            // being touched.
            Assert.All(planned.Of("leave"), a => Assert.Contains(a.ElementId.Value, scope.MigratedFromV1));
        }

        [Fact]
        public void A_person_s_edit_on_a_v1_element_is_still_review_after_the_migration_claims_it()
        {
            // Migrating a record must not migrate away the one fact the record
            // exists to keep: this element is not where it was built, and the
            // drawing does not say so.
            CadRequirementSet set = Set();
            List<CadCandidate> drawn = Read(set);
            CadPlacement p1 = Placement(10, P1);
            CadAuditSubject moved = Built(drawn[0], set, 1000, null);
            moved.Geometry = new List<CadPoint> { new CadPoint(0, 900), new CadPoint(6000, 900) };
            var model = new List<CadAuditSubject> { moved, Built(drawn[1], set, 2000, null) };

            CadUpdateScope scope = CadPlacementRules.Resolve(model, p1, null, null,
                                                            new List<CadPlacement> { p1 });
            CadUpdate planned = CadUpdateRules.Plan(drawn, model, set, scope, null, null, null, null);

            CadUpdateAction review = Assert.Single(planned.Of("review"));
            Assert.Equal(1000, review.ElementId);
            Assert.False(review.Automatic);
            Assert.Contains("A PERSON MOVED THIS", review.Says);
            Assert.Contains(1000L, scope.MigratedFromV1);
        }
    }
}
