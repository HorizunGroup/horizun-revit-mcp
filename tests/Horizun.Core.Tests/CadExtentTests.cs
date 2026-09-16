// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// "THIS APARTMENT, NOT THE WHOLE FLOOR."
//
// Measured on a real electrical permit drawing: 874 symbol placements spread over
// 438 x 349 feet of floor plan, on layers that are drawing-wide by definition. A
// rule for receptacles claims every receptacle on the storey, and the only bound
// the schema had was max_primitives - which bounds the READING, is refused when
// it truncates, and could never have meant "one unit".
//
// These cases fix what the zone means, because the two ways of getting it wrong
// are both plausible: reading less of the drawing (which would make coverage a
// lie), and keeping whatever happens to fall inside (which builds a conduit run
// that stops in mid air at a line nobody drew).
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadExtentTests
    {
        /// <summary>A set that converts receptacles, with whatever zone the case needs.</summary>
        private static CadRequirementSet Set(string extent)
        {
            string json = @"{
              ""schema"": ""horizun.cad-requirements/1"",
              ""requirement_set"": { ""id"": ""zone-test"", ""version"": ""1"" },
              ""source"": { ""units"": ""millimeter""" + extent + @" },
              ""tolerances"": { ""point_mm"": 1, ""gap_mm"": 2, ""angle_degrees"": 1, ""arc_sagitta_mm"": 1 },
              ""rules"": [ {
                ""id"": ""r-outlets"",
                ""layers"": [ ""E-P"" ],
                ""produces"": ""electrical_fixture"",
                ""family_type"": ""Duplex Receptacle: Standard"",
                ""level"": ""Level 1"",
                ""geometry"": { ""from"": ""single_lines"" }
              } ]
            }";
            return CadRequirementSet.Load(JObject.Parse(json));
        }

        private static CadSegment Seg(double ax, double ay, double bx, double by, string layer = "E-P") =>
            new CadSegment(new CadPoint(ax, ay), new CadPoint(bx, by), layer, CadCurveKind.Line, 0);

        [Fact]
        public void A_set_with_no_extent_reads_the_whole_drawing()
        {
            // The default must not change: every set written before this key
            // existed means the whole drawing and still does.
            CadRequirementSet set = Set("");
            Assert.Null(set.ExtentMm);

            CadInterpretation r = CadInterpretationRules.Interpret(
                new List<CadSegment> { Seg(0, 0, 10, 0), Seg(90000, 90000, 90010, 90000) }, set, "hash");
            Assert.Equal(2, r.SegmentsConsidered);
            Assert.Null(r.ExtentDescription);
            Assert.Equal(0, r.SegmentsOutsideExtent);
        }

        [Fact]
        public void What_lies_outside_the_zone_is_excluded_and_counted()
        {
            CadRequirementSet set = Set(@", ""extent_mm"": { ""min_x"": 0, ""min_y"": 0, ""max_x"": 1000, ""max_y"": 1000 }");
            Assert.NotNull(set.ExtentMm);

            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>
            {
                Seg(100, 100, 200, 100),      // inside
                Seg(5000, 5000, 5100, 5000),  // the next apartment
                Seg(6000, 6000, 6100, 6000)   // and the one after that
            }, set, "hash");

            Assert.Equal(1, r.SegmentsConsidered);
            Assert.Equal(2, r.SegmentsOutsideExtent);
            Assert.Equal(0, r.SegmentsCrossingExtent);
            Assert.Contains("1000", r.ExtentDescription);
        }

        [Fact]
        public void A_run_that_leaves_the_zone_is_not_built_half()
        {
            // THE CASE THE TWO COUNTS EXIST FOR. A conduit to the panel crosses
            // the boundary, and keeping the part inside would build a run ending
            // in mid air. It is excluded like anything else outside - and counted
            // apart, because this one is a decision somebody may want to argue
            // with, and "outside" is not.
            CadRequirementSet set = Set(@", ""extent_mm"": { ""min_x"": 0, ""min_y"": 0, ""max_x"": 1000, ""max_y"": 1000 }");
            CadInterpretation r = CadInterpretationRules.Interpret(new List<CadSegment>
            {
                Seg(500, 500, 4000, 500),   // starts inside, ends three apartments away
                Seg(100, 100, 200, 100)     // wholly inside
            }, set, "hash");

            Assert.Equal(1, r.SegmentsConsidered);
            Assert.Equal(1, r.SegmentsCrossingExtent);
            Assert.Equal(0, r.SegmentsOutsideExtent);
        }

        [Fact]
        public void The_zone_is_part_of_the_set_so_it_is_part_of_its_hash()
        {
            // An apply re-measures the requirement set hash before it builds. If
            // the zone were outside the hash, a plan made for one apartment could
            // be applied as though it were the floor.
            CadRequirementSet whole = Set("");
            CadRequirementSet one = Set(@", ""extent_mm"": { ""min_x"": 0, ""min_y"": 0, ""max_x"": 1000, ""max_y"": 1000 }");
            CadRequirementSet other = Set(@", ""extent_mm"": { ""min_x"": 0, ""min_y"": 0, ""max_x"": 2000, ""max_y"": 1000 }");

            Assert.NotEqual(whole.Sha256, one.Sha256);
            Assert.NotEqual(one.Sha256, other.Sha256);
        }

        [Fact]
        public void An_inverted_or_empty_zone_is_refused_rather_than_read_as_nothing()
        {
            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(() =>
                Set(@", ""extent_mm"": { ""min_x"": 1000, ""min_y"": 0, ""max_x"": 0, ""max_y"": 1000 }"));
            Assert.Contains("inverted", e.Message);
        }

        [Fact]
        public void A_zone_missing_a_bound_is_refused_rather_than_defaulted()
        {
            // Three bounds and a guess is not a zone: the missing one would become
            // infinity, and the plan would quietly cover half the floor.
            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(() =>
                Set(@", ""extent_mm"": { ""min_x"": 0, ""min_y"": 0, ""max_x"": 1000 }"));
            Assert.Contains("max_y", e.Message);
        }

        [Fact]
        public void A_misspelt_bound_is_refused_rather_than_ignored()
        {
            // The failure this mirrors is the one the source block already had:
            // an unknown key silently ignored, and a zone that silently becomes
            // the whole drawing.
            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(() =>
                Set(@", ""extent_mm"": { ""minx"": 0, ""min_y"": 0, ""max_x"": 1000, ""max_y"": 1000 }"));
            Assert.Contains("minx", e.Message);
        }
    }
}
