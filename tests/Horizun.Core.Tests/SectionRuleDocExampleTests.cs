// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Backlog #54: the canonical section-rule example in STRUCTURAL-REQUIREMENT-SET.md
// put two layers on the bottom face over the same stretch - which the code refuses
// as layers_coincide - and a bastón from_mm 0, with no end cover. The example a
// reader copies has to be one the bridge accepts, so it is run here as written.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionRuleDocExampleTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return d.FullName;
        }

        private static JArray CanonicalExample()
        {
            string doc = File.ReadAllText(Path.Combine(Root(), "docs", "STRUCTURAL-REQUIREMENT-SET.md"));
            int section = doc.IndexOf("## Section rules", StringComparison.Ordinal);
            Assert.True(section >= 0);
            int open = doc.IndexOf("```json", section, StringComparison.Ordinal);
            int close = doc.IndexOf("```", open + 7, StringComparison.Ordinal);
            string block = doc.Substring(open + 7, close - open - 7).Trim();
            return (JArray)JObject.Parse("{" + block + "}")["section_rules"];
        }

        [Fact]
        public void The_canonical_section_rule_example_expands_as_written()
        {
            var set = new JObject
            {
                ["schema"] = "horizun.structural-requirements/1",
                ["units"] = "millimeter",
                ["requirement_set"] = new JObject { ["id"] = "doc-example", ["version"] = "1" },
                ["bar_types"] = new JArray
                {
                    new JObject { ["id"] = "N3", ["type_name"] = "#3" },
                    new JObject { ["id"] = "N4", ["type_name"] = "#4" },
                    new JObject { ["id"] = "N5", ["type_name"] = "#5" }
                },
                ["hook_types"] = new JArray { new JObject { ["id"] = "H135", ["type_name"] = "Stirrup/Tie - 135 deg" } },
                ["section_rules"] = CanonicalExample()
            };
            StructuralRequirementSet s = StructuralRequirementSet.Load(set);
            Assert.True(s.Ok, s.Error);
            var frame = new SectionHostFrame
            {
                ElementId = 12, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, W = new[] { 0, 0, 1.0 },
                UMin = 0, UMax = 6000, VMin = -150, VMax = 150, WMin = -500, WMax = 0
            };
            var diameters = new Dictionary<string, double> { ["N3"] = 9.5, ["N4"] = 12.7, ["N5"] = 15.9 };
            SectionResult r = SectionRules.Expand(s.SectionRules[0], frame, id => diameters[id], null,
                                                  out List<StructuralRebarRule> bars, out _);
            Assert.True(r.Ok, r.Code + ": " + r.Why);
            Assert.NotEmpty(bars);
        }

        [Fact]
        public void The_canonical_example_keeps_the_end_cover_on_every_layer()
        {
            foreach (JToken layer in (JArray)CanonicalExample()[0]["layers"])
                Assert.False(layer["extend"] is JObject e && e["from_mm"] != null && (double)e["from_mm"] == 0,
                             "a layer laid from_mm 0 starts at the host's face, with no end cover: " + layer);
        }
    }
}
