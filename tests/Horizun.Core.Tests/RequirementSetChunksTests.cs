// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// F3: a large requirement set cut into pieces by level or host type, each one a set
// that loads on its own and names its hosts explicitly - so each piece can go to
// horizun_submit_job as its own job. The live XL measurement is pending.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RequirementSetChunksTests
    {
        private static JObject Set() => JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'cartilla', 'version': '1' },
  'bar_types': [ { 'id': 'N5', 'type_name': '#5' } ],
  'section_rules': [
    { 'id': 'V1', 'host': { 'mark': 'V-1' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' } ] },
    { 'id': 'V2', 'host': { 'category': 'OST_StructuralFraming', 'type_name': 'B 300x500' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'bottom', 'face': 'bottom', 'count': 3, 'bar_type': 'N5', 'extend': 'host_length' } ] },
    { 'id': 'V9', 'host': { 'mark': 'V-9' }, 'cover': { 'distance_mm': 40 },
      'layers': [ { 'name': 'top', 'face': 'top', 'count': 2, 'bar_type': 'N5', 'extend': 'host_length' } ] }
  ] }");

        private static List<RequirementSetChunks.Host> Hosts() => new List<RequirementSetChunks.Host>
        {
            new RequirementSetChunks.Host { ElementId = 11, Category = "OST_StructuralFraming", TypeName = "B 300x500", Mark = "V-1", Level = "L1" },
            new RequirementSetChunks.Host { ElementId = 12, Category = "OST_StructuralFraming", TypeName = "B 300x500", Mark = "V-1", Level = "L1" },
            new RequirementSetChunks.Host { ElementId = 13, Category = "OST_StructuralFraming", TypeName = "B 300x600", Mark = "V-1", Level = "L1" },
            new RequirementSetChunks.Host { ElementId = 21, Category = "OST_StructuralFraming", TypeName = "B 300x500", Mark = "V-2", Level = "L2" },
            new RequirementSetChunks.Host { ElementId = 22, Category = "OST_StructuralFraming", TypeName = "B 300x600", Mark = "V-1", Level = "L2" },
            new RequirementSetChunks.Host { ElementId = 99, Category = "OST_Walls", TypeName = "B 300x500", Mark = "V-1", Level = "L1" }   // not framing: V2 skips it
        };

        [Fact]
        public void By_level_each_piece_holds_at_most_the_asked_hosts_and_loads()
        {
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(Set(), Hosts(), RequirementSetChunks.ByLevel, 2);
            Assert.True(p.Ok, p.Why);
            // V-1 marks 11,12,13,22,99; V2 (framing, B 300x500) marks 11,12,21. L1 = 11,12,13,99; L2 = 21,22.
            Assert.Equal(new[] { "L1", "L1", "L2" }, p.Chunks.Select(c => c.Key).ToArray());
            Assert.Equal(new[] { new long[] { 11, 12 }, new long[] { 13, 99 }, new long[] { 21, 22 } },
                         p.Chunks.Select(c => c.ElementIds.ToArray()).ToArray());
            Assert.All(p.Chunks, c => Assert.True(c.ElementIds.Count <= 2));
            foreach (RequirementSetChunks.Chunk c in p.Chunks)
            {
                StructuralRequirementSet s = StructuralRequirementSet.Load(c.RequirementSet);
                Assert.True(s.Ok, c.Key + ": " + s.Error);
            }
        }

        [Fact]
        public void A_rule_names_only_its_own_hosts_in_each_piece()
        {
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(Set(), Hosts(), RequirementSetChunks.ByLevel, 2);
            JObject first = p.Chunks[0].RequirementSet;   // L1: 11, 12
            var rules = (JArray)first["section_rules"];
            JObject v1 = (JObject)rules.Single(r => (string)r["id"] == "V1");
            JObject v2 = (JObject)rules.Single(r => (string)r["id"] == "V2");
            Assert.Equal(new long[] { 11, 12 }, v1["host"]["element_ids"].Select(x => (long)x).ToArray());
            Assert.Equal("V-1", (string)v1["host"]["mark"]);          // the filter stays, so Revit re-checks it
            Assert.Equal(new long[] { 11, 12 }, v2["host"]["element_ids"].Select(x => (long)x).ToArray());
            Assert.Null(v2["host"]["category"]);                      // ids replace the category, as the resolver reads them
            Assert.Equal("B 300x500", (string)v2["host"]["type_name"]);
            // The piece of L1 holding 13 and 99 carries V1 only: V2 matches neither.
            JArray second = (JArray)p.Chunks[1].RequirementSet["section_rules"];
            Assert.Equal(new[] { "V1" }, second.Select(r => (string)r["id"]).ToArray());
            // Everything else in the set rides along unchanged.
            Assert.Equal("cartilla", (string)first["requirement_set"]["id"]);
            Assert.Equal("#5", (string)first["bar_types"][0]["type_name"]);
        }

        // Owner, 2026-10-10: each piece keeps the rule's ORIGINAL selector as provenance, so an
        // audit of the piece knows where its element_ids came from.
        [Fact]
        public void Each_rule_in_a_piece_keeps_its_original_selector_as_provenance()
        {
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(Set(), Hosts(), RequirementSetChunks.ByLevel, 2);
            JObject first = p.Chunks[0].RequirementSet;
            var rules = (JArray)first["section_rules"];
            JObject v1 = (JObject)rules.Single(r => (string)r["id"] == "V1");
            JObject v2 = (JObject)rules.Single(r => (string)r["id"] == "V2");
            Assert.True(JToken.DeepEquals(JObject.Parse("{ 'mark': 'V-1' }"), v1["host"]["source_selector"]));
            Assert.True(JToken.DeepEquals(JObject.Parse("{ 'category': 'OST_StructuralFraming', 'type_name': 'B 300x500' }"),
                                          v2["host"]["source_selector"]));
            // The piece still loads, and the loader keeps the provenance on the selector.
            StructuralRequirementSet s = StructuralRequirementSet.Load(first);
            Assert.True(s.Ok, s.Error);
            Assert.Equal("V-1", (string)s.SectionRules.Single(r => r.Id == "V1").Host.SourceSelector["mark"]);
        }

        // Backlog #53: cutting a piece again replaced source_selector with the already
        // narrowed selector, and the original was lost.
        [Fact]
        public void Cutting_a_piece_again_keeps_the_original_selector()
        {
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(Set(), Hosts(), RequirementSetChunks.ByLevel, 2);
            RequirementSetChunks.Plan again = RequirementSetChunks.Split(p.Chunks[0].RequirementSet, Hosts(), RequirementSetChunks.ByLevel, 1);
            Assert.True(again.Ok, again.Why);
            foreach (RequirementSetChunks.Chunk c in again.Chunks)
            {
                var rules = (JArray)c.RequirementSet["section_rules"];
                JObject v1 = (JObject)rules.Single(r => (string)r["id"] == "V1");
                Assert.True(JToken.DeepEquals(JObject.Parse("{ 'mark': 'V-1' }"), v1["host"]["source_selector"]),
                            v1["host"]["source_selector"].ToString());
                Assert.True(StructuralRequirementSet.Load(c.RequirementSet).Ok);
            }
        }

        [Fact]
        public void The_provenance_survives_the_section_expansion()
        {
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(Set(), Hosts(), RequirementSetChunks.ByLevel, 2);
            StructuralRequirementSet s = StructuralRequirementSet.Load(p.Chunks[0].RequirementSet);
            Assert.True(s.Ok, s.Error);
            StructuralSectionRule v1 = s.SectionRules.Single(r => r.Id == "V1");
            var frame = new SectionHostFrame
            {
                ElementId = 11, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
                UMin = 0, UMax = 6000, VMin = -150, VMax = 150, WMin = -500, WMax = 0
            };
            SectionResult r = SectionRules.Expand(v1, frame, id => 16, null, out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Why);
            Assert.All(bars, b => Assert.Equal("V-1", (string)b.Host.SourceSelector["mark"]));
        }

        [Fact]
        public void A_source_selector_must_be_a_selector()
        {
            JObject set = Set();
            set["section_rules"][0]["host"]["source_selector"] = "mark V-1";
            Assert.False(StructuralRequirementSet.Load(set).Ok);
            set["section_rules"][0]["host"]["source_selector"] = JObject.Parse("{ 'colour': 'red' }");
            Assert.False(StructuralRequirementSet.Load(set).Ok);
            set["section_rules"][0]["host"]["source_selector"] = JObject.Parse("{ 'mark': 'V-1', 'source_selector': { 'mark': 'x' } }");
            Assert.False(StructuralRequirementSet.Load(set).Ok);   // provenance does not nest
        }

        [Fact]
        public void By_host_type_and_the_rules_that_match_nothing()
        {
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(Set(), Hosts(), RequirementSetChunks.ByHostType, 10);
            Assert.True(p.Ok, p.Why);
            Assert.Equal(new[] { "OST_StructuralFraming:B 300x500", "OST_StructuralFraming:B 300x600", "OST_Walls:B 300x500" },
                         p.Chunks.Select(c => c.Key).ToArray());
            Assert.Equal(new[] { "V9" }, p.RulesMatchingNothing.ToArray());
            Assert.Equal(6, p.HostsCovered);
        }

        [Fact]
        public void Explicit_element_ids_are_honoured_as_the_resolver_reads_them()
        {
            JObject set = Set();
            set["section_rules"][2]["host"] = JObject.Parse("{ 'element_ids': [ 99, 404 ] }");   // 404 is not in the inventory
            RequirementSetChunks.Plan p = RequirementSetChunks.Split(set, Hosts(), RequirementSetChunks.ByLevel, 50);
            Assert.True(p.Ok, p.Why);
            Assert.Empty(p.RulesMatchingNothing);
            Assert.Equal(new long[] { 404 }, p.UnknownElementIds.ToArray());
            Assert.Contains(p.Chunks, c => c.ElementIds.Contains(99));
        }

        [Fact]
        public void A_bad_request_is_refused_by_code()
        {
            Assert.Equal(RequirementSetChunks.CodeBadDimension, RequirementSetChunks.Split(Set(), Hosts(), "storey", 10).Code);
            Assert.Equal(RequirementSetChunks.CodeBadSize, RequirementSetChunks.Split(Set(), Hosts(), RequirementSetChunks.ByLevel, 0).Code);
            Assert.Equal(RequirementSetChunks.CodeNoRules, RequirementSetChunks.Split(JObject.Parse("{ 'schema': 'x' }"), Hosts(), RequirementSetChunks.ByLevel, 10).Code);
            Assert.Equal(RequirementSetChunks.CodeNoHosts, RequirementSetChunks.Split(Set(), new List<RequirementSetChunks.Host>(), RequirementSetChunks.ByLevel, 10).Code);
            var noLevel = Hosts(); noLevel[0].Level = null;
            Assert.Equal(RequirementSetChunks.CodeMissingKey, RequirementSetChunks.Split(Set(), noLevel, RequirementSetChunks.ByLevel, 10).Code);
        }
    }
}
