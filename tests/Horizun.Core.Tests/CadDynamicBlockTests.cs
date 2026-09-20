// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A DYNAMIC BLOCK IS NOT ITS ANONYMOUS NAME.
//
// Measured on a real electrical floor plan (campaign 6): 19 of 46 symbols in two
// corridor zones were instances of dynamic blocks - exit signs, emergency lights,
// receptacles, smoke detectors - and the reader returned them as "*U11", "*U25",
// names AutoCAD generates and renumbers on edit. The extractor now also reads the
// dynamic definition each instance was generated from and the property values set
// on it. These cases pin how that information is carried (beside the reference
// name, never in place of it), how a rule may use it (only through keys that ask),
// and that an identity does not move when AutoCAD renumbers the anonymous name.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadDynamicBlockTests
    {
        private static string T(params string[] f) { return string.Join("\t", f); }

        private static CadRequirementSet Set(string rulesJson)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'dyn', 'version': '1.0.0', 'title': 'Dynamic blocks' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': RULES
            }".Replace('\'', '"').Replace("RULES", rulesJson);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        // Two anonymous definitions of one dynamic block (DYN-A): different content, so
        // different variants; a plain block; a named block NEST holding a dynamic instance.
        private static readonly string[] Dump =
        {
            T("H", "dwg", "DYN.dwg"), T("H", "insunits", "4"),
            T("K", "*U7", "1", ""), T("K", "*U8", "1", ""), T("K", "NEST", "0", ""),
            T("E", "*U7", "71", "LINE", "0", "0", "0", "0", "10", "0", "0"),
            T("E", "*U8", "81", "LINE", "0", "0", "0", "0", "0", "10", "0"),
            T("E", "NEST", "91", "INSERT", "E-X", "5", "0", "0", "0", "1", "1", "1", "*U7", "DYN-A", "repdata", "Flip state1=0.000000"),
            T("K", "*Model_Space", "0", ""),
            T("E", "*Model_Space", "1A", "INSERT", "E-X", "100", "0", "0", "0", "1", "1", "1", "*U7", "DYN-A", "repdata", "Flip state1=0.000000"),
            T("E", "*Model_Space", "1B", "INSERT", "E-X", "200", "0", "0", "1.570796", "-1", "1", "1", "*U8", "DYN-A", "reptag", "Visibility1=WALL;Flip state1=1.000000"),
            T("E", "*Model_Space", "1C", "INSERT", "E-X", "300", "0", "0", "0", "1", "1", "1", "PLAIN", "", "", ""),
            T("E", "*Model_Space", "1D", "INSERT", "E-X", "400", "0", "0", "0", "1", "1", "1", "NEST"),
            T("E", "*Model_Space", "1E", "INSERT", "E-X", "500", "0", "0", "0", "1", "1", "1", "*U9", "", "", ""),
            T("E", "*Model_Space", "1F", "INSERT", "E-X", "600", "0", "0", "0", "1", "1", "1", "DYN-A", "DYN-A", "definition", ""),
            T("H", "done", "1")
        };

        // Model-level instances; the one inside NEST's definition is reached through placement.
        private static List<CadIrEntity> Inserts() =>
            CadDwgExtract.Parse(Dump).Entities.Where(e => e.Kind == CadEntityKind.BlockInstance && e.BlockPath.Count == 0).ToList();

        [Fact]
        public void The_reference_name_and_the_effective_name_are_both_kept()
        {
            CadIrEntity a = Inserts().Single(e => e.Handle == "1A");
            Assert.Equal("*U7", a.BlockName);
            Assert.Equal("DYN-A", a.EffectiveName);
            Assert.Equal("repdata", a.EffectiveNameSource);
            Assert.Equal("0.000000", a.DynamicProperties["Flip state1"]);
            JObject json = a.ToJson();
            Assert.Equal("*U7", (string)json["block_name"]);
            Assert.Equal("DYN-A", (string)json["effective_name"]);
        }

        [Fact]
        public void Two_variants_of_one_definition_differ_by_content_and_by_their_properties()
        {
            var ins = Inserts();
            CadIrEntity a = ins.Single(e => e.Handle == "1A"), b = ins.Single(e => e.Handle == "1B");
            Assert.Equal(a.EffectiveName, b.EffectiveName);
            Assert.NotNull(a.DefinitionSignature);
            Assert.NotEqual(a.DefinitionSignature, b.DefinitionSignature);
            Assert.Equal("WALL", b.DynamicProperties["Visibility1"]);
            Assert.False(a.DynamicProperties.ContainsKey("Visibility1"));   // absent, not "none"
            Assert.Equal("reptag", b.EffectiveNameSource);
        }

        [Fact]
        public void Absent_dynamic_information_is_absent_not_invented()
        {
            var ins = Inserts();
            Assert.Null(ins.Single(e => e.Handle == "1C").EffectiveName);        // a plain block
            CadIrEntity u9 = ins.Single(e => e.Handle == "1E");                    // anonymous, link unreadable
            Assert.Null(u9.EffectiveName);
            Assert.Null(u9.DynamicProperties);
            Assert.Null(ins.Single(e => e.Handle == "1D").EffectiveName);         // an older dump's row: 13 fields
            Assert.Null(u9.ToJson()["effective_name"]);
        }

        [Fact]
        public void A_nested_dynamic_instance_keeps_its_effective_name_through_placement()
        {
            CadPlacementReading p = CadBlockPlacement.Place(CadDwgExtract.Parse(Dump).Entities);
            CadPlacedBlock nested = p.Placed.Single(x => x.Source != null && x.Source.Handle == "91");
            CadIrEntity flat = nested.AsEntity();
            Assert.Equal("*U7", flat.BlockName);
            Assert.Equal("DYN-A", flat.EffectiveName);
            Assert.Equal("0.000000", flat.DynamicProperties["Flip state1"]);
        }

        [Fact]
        public void A_blocks_rule_does_not_claim_a_dynamic_instance_by_its_effective_name()
        {
            CadBlockReading r = CadBlockRules.Interpret(Inserts(), Set(@"[
              { 'id': 'plain-name', 'precedence': 10, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'family_type': 'X: Y', 'level': 'Level 1', 'geometry': { 'from': 'blocks', 'blocks': ['DYN-A'] } } ]"), "h");
            // Only the instance whose REFERENCE is DYN-A (reset to the definition); never the *U ones.
            CadCandidate only = Assert.Single(r.Candidates);
            Assert.Equal("DYN-A", only.SourceBlockName);
        }

        [Fact]
        public void Effective_blocks_claims_every_variant_and_dynamic_properties_separates_them()
        {
            CadBlockReading all = CadBlockRules.Interpret(Inserts(), Set(@"[
              { 'id': 'dyn', 'precedence': 10, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'family_type': 'X: Y', 'level': 'Level 1', 'geometry': { 'from': 'blocks', 'effective_blocks': ['DYN-A'] } } ]"), "h");
            Assert.Equal(3, all.Candidates.Count);   // two variants + one reset to the definition
            Assert.All(all.Candidates, c => Assert.Equal("DYN-A", c.SourceEffectiveName));

            CadBlockReading wall = CadBlockRules.Interpret(Inserts(), Set(@"[
              { 'id': 'dyn-wall', 'precedence': 10, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'family_type': 'X: Y', 'level': 'Level 1',
                'geometry': { 'from': 'blocks', 'effective_blocks': ['DYN-A'], 'dynamic_properties': { 'Visibility1': 'wall', 'Flip state1': 1 } } } ]"), "h");
            CadCandidate only = Assert.Single(wall.Candidates);
            Assert.Equal("*U8", only.SourceBlockName);
            Assert.True(only.Mirrored);                                          // scale -1 travels
            Assert.Equal(1.570796, only.RotationRadians.Value, 6);
        }

        [Fact]
        public void A_renumbered_anonymous_name_keeps_the_identity_a_plain_block_is_unchanged()
        {
            CadIrEntity before = Inserts().Single(e => e.Handle == "1A");
            var after = new CadIrEntity
            {
                Id = "x", Kind = CadEntityKind.BlockInstance, Layer = before.Layer, Handle = before.Handle,
                BlockName = "*U14", EffectiveName = "DYN-A", RotationRadians = 0, ScaleX = 1, ScaleY = 1
            };
            after.Points.Add(before.Points[0]);
            string rule = @"[ { 'id': 'dyn', 'precedence': 10, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'family_type': 'X: Y', 'level': 'Level 1', 'geometry': { 'from': 'blocks', 'effective_blocks': ['DYN-A'] } } ]";
            string id1 = CadBlockRules.Interpret(new List<CadIrEntity> { before }, Set(rule), "h").Candidates.Single().SemanticId;
            string id2 = CadBlockRules.Interpret(new List<CadIrEntity> { after }, Set(rule), "h").Candidates.Single().SemanticId;
            Assert.Equal(id1, id2);
            Assert.Equal("PLAIN", CadBlockRules.IdentityName(Inserts().Single(e => e.Handle == "1C")));
        }

        [Fact]
        public void An_instance_reset_to_the_definition_itself_keeps_the_same_identity()
        {
            // MEASURED with RESETBLOCK on a real drawing: the reference went from "*U32" to the
            // dynamic definition's own name; the extractor now says so ("definition").
            var ins = Inserts();
            CadIrEntity reset = ins.Single(e => e.Handle == "1F");
            Assert.Equal("definition", reset.EffectiveNameSource);
            Assert.Equal(CadBlockRules.IdentityName(ins.Single(e => e.Handle == "1A")), CadBlockRules.IdentityName(reset));
        }

        [Fact]
        public void An_unclaimed_dynamic_block_is_reported_with_its_effective_name()
        {
            CadBlockReading r = CadBlockRules.Interpret(Inserts(), Set(@"[
              { 'id': 'other', 'precedence': 10, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'family_type': 'X: Y', 'level': 'Level 1', 'geometry': { 'from': 'blocks', 'blocks': ['NOTHING'] } } ]"), "h");
            CadUnclaimedBlock u8 = r.Unclaimed.Single(u => u.BlockName == "*U8");
            Assert.Equal("DYN-A", u8.EffectiveName);
            Assert.Contains("Visibility1=WALL", u8.DynamicPropertiesSeen);
        }

        [Fact]
        public void Invalid_dynamic_property_filters_are_refused()
        {
            Assert.Throws<CadRequirementSetException>(() => Set(@"[
              { 'id': 'bad', 'precedence': 10, 'layers': ['E-*'], 'produces': 'electrical_fixture', 'family_type': 'X: Y',
                'level': 'Level 1', 'geometry': { 'from': 'blocks', 'effective_blocks': ['A'], 'dynamic_properties': { 'Visibility1': { 'x': 1 } } } } ]"));
            Assert.Throws<CadRequirementSetException>(() => Set(@"[
              { 'id': 'bad2', 'precedence': 10, 'layers': ['E-*'], 'produces': 'duct', 'family_type': 'X: Y',
                'level': 'Level 1', 'geometry': { 'from': 'single_lines', 'effective_blocks': ['A'] } } ]"));
        }

        [Fact]
        public void The_extractor_reads_both_links_and_the_instance_history()
        {
            string lisp = string.Join(" ", CadDwgScript.Forms);
            Assert.Contains("AcDbRepData", lisp);
            Assert.Contains("AcDbBlockRepBTag", lisp);
            Assert.Contains("ACAD_ENHANCEDBLOCKHISTORY", lisp);
        }
    }
}
