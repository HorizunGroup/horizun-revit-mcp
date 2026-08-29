// -----------------------------------------------------------------------------
// A zone rule is not a fourth kind of thing in the model: it expands into
// ordinary reinforcement rules, one per zone. These pin that the expansion is
// deterministic - the plan, the apply and the audit must all produce the same
// rule ids, or the audit cannot find what the apply wrote - and that each zone's
// profile really did move along the beam.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StirrupZoneExpansionTests
    {
        private static StructuralStirrupZoneRule Rule()
        {
            return new StructuralStirrupZoneRule
            {
                Id = "B1",
                BarTypeId = "s10",
                Style = StructuralStyle.StirrupTie,
                Closed = true,
                AlongMm = new double[] { 1, 0, 0 },
                // FOUR CORNERS, DECLARED ONCE. `closed` adds the last segment, so
                // repeating the first point makes that segment zero-length - which
                // Revit refuses as curve_degenerate, measured live on 2026-08-28.
                ProfileMm = new List<double[]>
                {
                    new double[] { 0, -102, 48 },
                    new double[] { 0, 102, 48 },
                    new double[] { 0, 102, 552 },
                    new double[] { 0, -102, 552 }
                },
                Mark = "E1",
                Zones = new List<StirrupZoneRequest>
                {
                    new StirrupZoneRequest
                    {
                        Name = "start",
                        LengthMm = 1000,
                        Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 100 }
                    },
                    new StirrupZoneRequest
                    {
                        Name = "middle",
                        LengthMm = null,
                        Layout = new RebarLayoutRequest
                        {
                            Layout = RebarLayout.MaximumSpacing, SpacingMm = 200,
                            IncludeFirstBar = false, IncludeLastBar = false
                        }
                    },
                    new StirrupZoneRequest
                    {
                        Name = "end",
                        LengthMm = 1000,
                        Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 100 }
                    }
                }
            };
        }

        [Fact]
        public void EachZoneBecomesOneReinforcementRuleNamedAfterIt()
        {
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(Rule(), 6000, 10, out made);
            Assert.True(r.Ok, r.Why);
            Assert.Equal(3, made.Count);
            Assert.Equal(new[] { "B1#start", "B1#middle", "B1#end" }, made.Select(m => m.Id).ToArray());
        }

        [Fact]
        public void EachZonesProfileIsTheDeclaredOneMovedAlongTheRun()
        {
            List<StructuralRebarRule> made;
            StirrupZoneRules.Expand(Rule(), 6000, 10, out made);

            // the first zone starts where the profile was declared
            Assert.Equal(0, made[0].CurvesMm[0][0], 9);
            // the middle starts a metre along
            Assert.Equal(1000, made[1].CurvesMm[0][0], 9);
            // the end zone starts at 5000
            Assert.Equal(5000, made[2].CurvesMm[0][0], 9);

            // and only along the run: the section is untouched
            for (int i = 0; i < made[2].CurvesMm.Count; i++)
            {
                Assert.Equal(Rule().ProfileMm[i][1], made[2].CurvesMm[i][1], 9);
                Assert.Equal(Rule().ProfileMm[i][2], made[2].CurvesMm[i][2], 9);
            }
        }

        [Fact]
        public void TheRunDirectionIsNormalisedBeforeItMovesAnything()
        {
            StructuralStirrupZoneRule rule = Rule();
            rule.AlongMm = new double[] { 6000, 0, 0 };   // a direction written as a length
            List<StructuralRebarRule> made;
            StirrupZoneRules.Expand(rule, 6000, 10, out made);
            Assert.Equal(1000, made[1].CurvesMm[0][0], 9);
            Assert.Equal(1.0, made[1].NormalMm[0], 9);
        }

        [Fact]
        public void EachZoneCarriesItsOwnLayoutAndNotTheParentsFirstOne()
        {
            List<StructuralRebarRule> made;
            StirrupZoneRules.Expand(Rule(), 6000, 10, out made);
            Assert.Equal(100, made[0].Layout.SpacingMm);
            Assert.Equal(200, made[1].Layout.SpacingMm);
            Assert.Equal(1000, made[0].Layout.ArrayLengthMm);
            Assert.Equal(4000, made[1].Layout.ArrayLengthMm);
            Assert.False(made[1].Layout.IncludeFirstBar);
            Assert.True(made[2].Layout.IncludeFirstBar);
        }

        [Fact]
        public void EveryZoneInheritsWhatTheRuleDeclaredOnce()
        {
            List<StructuralRebarRule> made;
            StirrupZoneRules.Expand(Rule(), 6000, 10, out made);
            foreach (StructuralRebarRule m in made)
            {
                Assert.Equal("s10", m.BarTypeId);
                Assert.Equal(StructuralStyle.StirrupTie, m.Style);
                Assert.True(m.Closed);
                Assert.Equal("E1", m.Mark);
                Assert.True(m.BarsOnNormalSide);
            }
        }

        [Fact]
        public void AZoneMayOverrideTheMarkWithoutDisturbingTheOthers()
        {
            StructuralStirrupZoneRule rule = Rule();
            rule.Zones[1].Mark = "E2";
            List<StructuralRebarRule> made;
            StirrupZoneRules.Expand(rule, 6000, 10, out made);
            Assert.Equal("E1", made[0].Mark);
            Assert.Equal("E2", made[1].Mark);
            Assert.Equal("E1", made[2].Mark);
        }

        [Fact]
        public void TheExpansionIsDeterministicSoTheAuditCanFindWhatTheApplyWrote()
        {
            List<StructuralRebarRule> first, second;
            StirrupZoneRules.Expand(Rule(), 6000, 10, out first);
            StirrupZoneRules.Expand(Rule(), 6000, 10, out second);
            Assert.Equal(first.Select(x => x.Id), second.Select(x => x.Id));
            for (int i = 0; i < first.Count; i++)
                for (int k = 0; k < first[i].CurvesMm.Count; k++)
                    Assert.Equal(first[i].CurvesMm[k], second[i].CurvesMm[k]);
        }

        [Fact]
        public void ARefusedPlanExpandsToNothingRatherThanToSomeOfIt()
        {
            StructuralStirrupZoneRule rule = Rule();
            rule.Zones[2].LengthMm = 9000;      // longer than the span
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(rule, 6000, 10, out made);
            Assert.False(r.Ok);
            Assert.Empty(made);
        }

        [Fact]
        public void ARunDirectionThatIsNotAVectorIsRefused()
        {
            StructuralStirrupZoneRule rule = Rule();
            rule.AlongMm = new double[] { 0, 0, 0 };
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(rule, 6000, 10, out made);
            Assert.False(r.Ok);
            Assert.Empty(made);
        }

        [Fact]
        public void NoRuleAtAllIsRefusedRatherThanReturningAnEmptySuccess()
        {
            List<StructuralRebarRule> made;
            StirrupZoneResult r = StirrupZoneRules.Expand(null, 6000, 10, out made);
            Assert.False(r.Ok);
            Assert.Empty(made);
        }

        // -------------------------------------------------------------- parsing

        private const string Head = @"{
          ""schema"": ""horizun.structural-requirements/1"",
          ""requirement_set"": { ""id"": ""zones"", ""version"": ""1.0.0"" },
          ""units"": ""millimeter"",
          ""bar_types"": [ { ""id"": ""s10"", ""type_name"": ""10M"", ""nominal_diameter_mm"": 10 } ],";

        private static StructuralRequirementSet Parse(string zoneJson)
        {
            return StructuralRequirementSet.Load(JObject.Parse(Head + zoneJson + "}"));
        }

        private const string OneGoodRule = @"
          ""stirrup_zone_rules"": [{
            ""id"": ""B1"",
            ""host"": { ""element_ids"": [1234] },
            ""bar_type"": ""s10"",
            ""style"": ""stirrup_tie"",
            ""profile_mm"": [[0,-102,48],[0,102,48],[0,102,552],[0,-102,552]],
            ""closed"": true,
            ""along"": [1,0,0],
            ""span_mm"": 6000,
            ""zones"": [
              { ""name"": ""start"", ""length_mm"": 1000,
                ""layout"": { ""rule"": ""maximum_spacing"", ""spacing_mm"": 100 } },
              { ""name"": ""middle"",
                ""layout"": { ""rule"": ""maximum_spacing"", ""spacing_mm"": 200,
                              ""include_first_bar"": false, ""include_last_bar"": false } },
              { ""name"": ""end"", ""length_mm"": 1000,
                ""layout"": { ""rule"": ""maximum_spacing"", ""spacing_mm"": 100 } }
            ]
          }]";

        [Fact]
        public void AWellFormedZoneRuleParses()
        {
            StructuralRequirementSet s = Parse(OneGoodRule);
            Assert.True(s.Ok, s.Error);
            Assert.Single(s.StirrupZoneRules);
            StructuralStirrupZoneRule z = s.StirrupZoneRules[0];
            Assert.Equal("B1", z.Id);
            Assert.Equal(6000, z.SpanMm);
            Assert.False(z.SpanFromHost);
            Assert.Equal(3, z.Zones.Count);
            Assert.Equal(4, z.ProfileMm.Count);
        }

        [Fact]
        public void SpanHostLengthIsAcceptedAndLeftForTheResolverToMeasure()
        {
            StructuralRequirementSet s = Parse(OneGoodRule.Replace(@"""span_mm"": 6000", @"""span"": ""host_length"""));
            Assert.True(s.Ok, s.Error);
            Assert.True(s.StirrupZoneRules[0].SpanFromHost);
            Assert.Null(s.StirrupZoneRules[0].SpanMm);
        }

        [Fact]
        public void DeclaringBothSpansIsRefused()
        {
            StructuralRequirementSet s = Parse(
                OneGoodRule.Replace(@"""span_mm"": 6000", @"""span_mm"": 6000, ""span"": ""host_length"""));
            Assert.False(s.Ok);
            Assert.Contains("State one", s.Error);
        }

        [Fact]
        public void DeclaringNoSpanAtAllIsRefusedRatherThanGuessed()
        {
            StructuralRequirementSet s = Parse(OneGoodRule.Replace(@"""span_mm"": 6000,", ""));
            Assert.False(s.Ok);
            Assert.Contains("does not guess", s.Error);
        }

        [Fact]
        public void AnUnknownSpanWordIsRefusedByName()
        {
            StructuralRequirementSet s = Parse(
                OneGoodRule.Replace(@"""span_mm"": 6000", @"""span"": ""whatever_fits"""));
            Assert.False(s.Ok);
            Assert.Contains("whatever_fits", s.Error);
        }

        [Fact]
        public void ARunDirectionIsRequiredBecauseTheProfileDoesNotImplyOne()
        {
            StructuralRequirementSet s = Parse(OneGoodRule.Replace(@"""along"": [1,0,0],", ""));
            Assert.False(s.Ok);
            Assert.Contains("direction the zones run in", s.Error);
        }

        [Fact]
        public void AZeroRunDirectionIsRefused()
        {
            StructuralRequirementSet s = Parse(OneGoodRule.Replace(@"""along"": [1,0,0]", @"""along"": [0,0,0]"));
            Assert.False(s.Ok);
            Assert.Contains("zero vector", s.Error);
        }

        [Fact]
        public void ANonFiniteRunDirectionIsRefused()
        {
            StructuralRequirementSet s = Parse(OneGoodRule.Replace(@"""along"": [1,0,0]", @"""along"": [1e400,0,0]"));
            Assert.False(s.Ok);
        }

        [Fact]
        public void NoZonesIsRefused()
        {
            StructuralRequirementSet s = Parse(System.Text.RegularExpressions.Regex.Replace(
                OneGoodRule, @"""zones"": \[[\s\S]*?\n            \]", @"""zones"": []"));
            Assert.False(s.Ok);
            Assert.Contains("needs zones", s.Error);
        }

        [Fact]
        public void AProfileOfOnePointIsRefused()
        {
            StructuralRequirementSet s = Parse(OneGoodRule.Replace(
                @"""profile_mm"": [[0,-102,48],[0,102,48],[0,102,552],[0,-102,552]]",
                @"""profile_mm"": [[0,-102,48]]"));
            Assert.False(s.Ok);
            Assert.Contains("at least two points", s.Error);
        }

        [Fact]
        public void AZoneRuleSharesTheRuleIdNamespace()
        {
            string both = OneGoodRule + @",
              ""reinforcement_rules"": [{
                ""id"": ""B1"", ""host"": { ""element_ids"": [1] }, ""bar_type"": ""s10"",
                ""style"": ""standard"", ""curve_mm"": [[0,0,0],[1000,0,0]], ""normal"": [0,1,0],
                ""layout"": { ""rule"": ""single"" }
              }]";
            StructuralRequirementSet s = Parse(both);
            Assert.False(s.Ok);
            Assert.Contains("more than one rule", s.Error);
        }

        [Fact]
        public void TheStyleDefaultsToStirrupTieHereAndNowhereElse()
        {
            StructuralRequirementSet s = Parse(OneGoodRule.Replace(@"""style"": ""stirrup_tie"",", ""));
            Assert.True(s.Ok, s.Error);
            Assert.Equal(StructuralStyle.StirrupTie, s.StirrupZoneRules[0].Style);
        }

        [Fact]
        public void ANegativeOffsetIsRefused()
        {
            StructuralRequirementSet s = Parse(
                OneGoodRule.Replace(@"""span_mm"": 6000,", @"""span_mm"": 6000, ""start_offset_mm"": -50,"));
            Assert.False(s.Ok);
        }

        [Fact]
        public void TheDigestCoversTheZoneRulesSoChangingOneChangesIt()
        {
            StructuralRequirementSet a = Parse(OneGoodRule);
            StructuralRequirementSet b = Parse(OneGoodRule.Replace(@"""spacing_mm"": 200", @"""spacing_mm"": 250"));
            Assert.True(a.Ok);
            Assert.True(b.Ok);
            Assert.NotEqual(StructuralRequirementSet.Sha256Of(JObject.Parse(Head + OneGoodRule + "}")), StructuralRequirementSet.Sha256Of(JObject.Parse(Head + OneGoodRule.Replace("\"spacing_mm\": 200", "\"spacing_mm\": 250") + "}")));
        }
    }
}
