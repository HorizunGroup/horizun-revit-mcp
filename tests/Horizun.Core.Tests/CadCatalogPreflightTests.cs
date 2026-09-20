// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE CATALOGUE, AS A PERSON READS IT: per thickness, what answers it, what is
// withdrawn, which alternatives would fit (never used), and which symbols are lost
// with a withdrawn wall.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadCatalogPreflightTests
    {
        private static JObject Chosen(double t, string type, double off, double x0 = 0) => new JObject
        {
            ["kind"] = "wall", ["thickness_mm"] = t, ["chosen"] = type, ["off_by_mm"] = off,
            ["from_mm"] = new JArray(x0, 0.0), ["to_mm"] = new JArray(x0 + 1000.0, 0.0), ["tolerance_mm"] = 3.2
        };

        private static JObject Withdrawn(double t, int row, double y = 5000) => new JObject
        {
            ["kind"] = "wall", ["source_row"] = row, ["thickness_mm"] = t,
            ["reason"] = "no_wall_type_for_this_thickness",
            ["from_mm"] = new JArray(0.0, y), ["to_mm"] = new JArray(3000.0, y), ["tolerance_mm"] = 3.2,
            ["candidates"] = new JArray(new JObject { ["type"] = "Interior 161.9", ["width_mm"] = 161.9 })
        };

        [Fact]
        public void Thicknesses_are_grouped_and_an_alternative_is_measured_but_not_used()
        {
            var alt = new[] { Tuple.Create("HZ-TEST 177.8", 177.8), Tuple.Create("HZ-TEST 304.8", 304.8) };
            JObject p = CadCatalogPreflight.Summarize(
                new[] { Chosen(161.9, "Interior 161.9", 0), Chosen(162.1, "Interior 161.9", 0.2, 2000) },
                new[] { Withdrawn(177.8, 7), Withdrawn(177.9, 8, 6000),
                        new JObject { ["kind"] = "duplex_receptacle", ["reason"] = "no_host_within_allowance" } },
                alt, 3.2);

            var groups = (JArray)p["thickness_groups"];
            Assert.Equal(2, groups.Count);
            Assert.Equal("answered_by_the_catalogue", (string)groups[0]["verdict"]);
            Assert.Equal(2, (int)groups[0]["walls"]);
            Assert.Equal("no_type_in_the_catalogue", (string)groups[1]["verdict"]);
            Assert.Equal(6000, (double)groups[1]["length_mm"]);
            Assert.Equal("HZ-TEST 177.8", (string)groups[1]["alternatives_that_would_fit"][0]["type"]);
            Assert.Single(groups[1]["alternatives_that_would_fit"]);
            Assert.Equal(0, (int)p["fallback_to_a_generic_type"]);
            Assert.Equal(2, (int)p["walls_withdrawn"]);
        }

        [Fact]
        public void A_symbol_drawn_against_a_withdrawn_wall_is_lost_with_it_by_name()
        {
            JObject wall = Withdrawn(177.8, 7);
            JObject against = new JObject
            {
                ["kind"] = "duplex_receptacle", ["source_row"] = 40, ["at_mm"] = new JArray(1500.0, 5100.0),
                ["reason"] = "nearer_drawn_wall_is_not_in_the_model",
                // one face line of the withdrawn wall, 88.9 mm off its centreline
                ["other_wall_line"] = new JObject
                {
                    ["from_mm"] = new JArray(0.0, 5088.9), ["to_mm"] = new JArray(3000.0, 5088.9)
                }
            };
            JObject elsewhere = (JObject)against.DeepClone();
            elsewhere["source_row"] = 41;
            elsewhere["other_wall_line"]["from_mm"] = new JArray(0.0, 9000.0);
            elsewhere["other_wall_line"]["to_mm"] = new JArray(3000.0, 9000.0);
            JObject unrelated = new JObject { ["kind"] = "switch", ["reason"] = "no_host_within_allowance" };

            JArray a = CadCatalogPreflight.AffectedSymbols(new[] { wall }, new[] { against, elsewhere, unrelated }, 5.0);

            Assert.Equal(2, a.Count);
            Assert.Equal("lost_with_that_wall", (string)a[0]["verdict"]);
            Assert.Equal(7, (int)a[0]["stands_against"]["wall_source_row"]);
            Assert.Equal("its_wall_is_not_a_withdrawn_one", (string)a[1]["verdict"]);
        }

        [Fact]
        public void A_perpendicular_line_crossing_the_wall_is_not_standing_against_it()
        {
            JObject wall = Withdrawn(177.8, 7);
            JObject across = new JObject
            {
                ["kind"] = "duplex_receptacle", ["reason"] = "nearer_drawn_wall_is_not_in_the_model",
                ["other_wall_line"] = new JObject
                {
                    ["from_mm"] = new JArray(1500.0, 4000.0), ["to_mm"] = new JArray(1500.0, 6000.0)
                }
            };
            Assert.Equal("its_wall_is_not_a_withdrawn_one",
                         (string)CadCatalogPreflight.AffectedSymbols(new[] { wall }, new[] { across }, 5.0)[0]["verdict"]);
        }
    }
}
