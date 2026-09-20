// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// NINE OF TWENTY-ONE SYMBOLS IN ONE APARTMENT ARE DRAWN MIRRORED.
//
// The first guard treated that as one question with one answer: the family
// reports CanFlipHand = false, so refuse. It refused its own first batch, and it
// was wrong to be so sure - a duplex receptacle is symmetric, and its reflection
// is the same mark, while a handed device's reflection is a different device.
//
// These cases fix the five things a mirror can be and the four things a
// correspondence may say about it, and they fix the one rule that keeps the
// whole thing honest: a claim of symmetry is checked against the block's own
// geometry before it is believed.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadMirrorTests
    {
        private static CadIrEntity Line(string inside, double ax, double ay, double bx, double by)
        {
            var e = new CadIrEntity { Kind = CadEntityKind.Line, Layer = "E-P" };
            e.BlockPath.Add(inside);
            e.Points.Add(new CadPoint(ax, ay));
            e.Points.Add(new CadPoint(bx, by));
            return e;
        }

        [Fact]
        public void A_symbol_that_is_its_own_reflection_is_measured_as_symmetric()
        {
            // Two marks either side of the insertion axis: reflecting maps one
            // onto the other and nothing moves.
            var definition = new List<CadIrEntity>
            {
                Line("OUT2", -50, 0, -50, 30),
                Line("OUT2",  50, 0,  50, 30),
                Line("OUT2", -50, 0,  50, 0)
            };

            CadSymmetryReading r = CadBlockSymmetry.Measure(definition, 1.0);
            Assert.True(r.Symmetric);
            Assert.True(r.ResidualMm <= 1.0);
            Assert.Contains("changes nothing", r.Why);
        }

        [Fact]
        public void A_handed_symbol_is_measured_as_asymmetric_and_says_by_how_much()
        {
            // One mark on one side only: its reflection lands where nothing was
            // drawn, and the residual is how far.
            var definition = new List<CadIrEntity>
            {
                Line("SW-PILOT", 0, 0, 0, 30),
                Line("SW-PILOT", 40, 10, 60, 10)
            };

            CadSymmetryReading r = CadBlockSymmetry.Measure(definition, 1.0);
            Assert.False(r.Symmetric);
            Assert.True(r.ResidualMm > 1.0, "the residual must be measured, not asserted: " + r.ResidualMm);
            Assert.Contains("part of what this symbol says", r.Why);
        }

        [Fact]
        public void A_definition_this_reading_never_saw_is_unknown_and_not_symmetric()
        {
            // THE DISTINCTION THE WHOLE POLICY RESTS ON. "I could not look" is not
            // "there is nothing there", and a set claiming symmetry over a
            // definition nobody read has claimed something unchecked.
            CadSymmetryReading r = CadBlockSymmetry.Measure(new List<CadIrEntity>(), 1.0);
            Assert.Null(r.Symmetric);
            Assert.Contains("UNKNOWN", r.Why);
        }

        [Fact]
        public void Definitions_are_measured_by_the_block_they_belong_to()
        {
            var entities = new List<CadIrEntity>
            {
                Line("OUT2", -50, 0, -50, 30),
                Line("OUT2",  50, 0,  50, 30),
                Line("SW-PILOT", 40, 10, 60, 10)
            };

            Dictionary<string, CadSymmetryReading> all = CadBlockSymmetry.MeasureAll(entities, 1.0);
            Assert.Equal(2, all.Count);
            Assert.True(all["OUT2"].Symmetric);
            Assert.False(all["SW-PILOT"].Symmetric);
        }

        [Fact]
        public void A_rule_may_say_what_a_mirror_means_and_may_not_say_ignore()
        {
            foreach (string policy in new[] { "preserve", "symmetric", "pending" })
            {
                CadRequirementSet set = Set(@"'mirror': '" + policy + "'");
                Assert.Equal(policy, set.Rules[0].Mirror);
            }

            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(
                () => Set(@"'mirror': 'ignore'"));
            Assert.Contains("no 'ignore'", e.Message);
        }

        [Fact]
        public void A_variant_nobody_named_is_refused_and_a_name_nobody_uses_too()
        {
            CadRequirementSetException missing = Assert.Throws<CadRequirementSetException>(
                () => Set(@"'mirror': 'variant'"));
            Assert.Contains("names no mirror_variant_type", missing.Message);

            CadRequirementSetException unused = Assert.Throws<CadRequirementSetException>(
                () => Set(@"'mirror': 'preserve', 'mirror_variant_type': 'X: Y'"));
            Assert.Contains("read by nothing", unused.Message);
        }

        [Fact]
        public void A_candidate_takes_its_rules_mirror_policy()
        {
            CadRequirementSet set = Set(@"'mirror': 'symmetric'");
            var instance = new CadIrEntity
            {
                Id = "i1",
                Kind = CadEntityKind.BlockInstance,
                Layer = "E-P",
                BlockName = "OUT2",
                ScaleX = -1,
                RotationRadians = 0
            };
            instance.Points.Add(new CadPoint(1000, 2000));

            CadBlockReading r = CadBlockRules.Interpret(new List<CadIrEntity> { instance }, set, "hash");
            CadCandidate c = Assert.Single(r.Candidates);
            Assert.True(c.Mirrored);
            Assert.Equal("symmetric", c.MirrorPolicy);
        }

        private static CadRequirementSet Set(string extra)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'mirror', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1, 'gap_mm': 2, 'angle_degrees': 1, 'arc_sagitta_mm': 1 },
              'rules': [ { 'id': 'r', 'layers': ['E-P'], 'produces': 'electrical_fixture',
                           'family_type': 'Duplex Receptacle: Standard', 'level': 'Level 1',
                           'geometry': { 'from': 'blocks', 'blocks': ['OUT2'] }, EXTRA } ]
            }".Replace("EXTRA", extra).Replace('\'', '"');
            return CadRequirementSet.Load(Newtonsoft.Json.Linq.JObject.Parse(doc));
        }
    }
}
