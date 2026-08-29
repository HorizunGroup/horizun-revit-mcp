// -----------------------------------------------------------------------------
// Nobody orders "set 419". They order metres of 12 mm at mark E1. These pin the
// grouping - and the one thing a takeoff must never do, which is turn a value
// the model would not report into a zero and sum it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarTakeoffTests
    {
        private static JObject Bar(string mark, string type, double dia, long host,
                                   int quantity, double lengthM, double volume,
                                   string rule = "R1", double? eachMm = 4000)
        {
            var o = new JObject
            {
                ["measured"] = new JObject
                {
                    ["schedule_mark"] = mark,
                    ["quantity"] = quantity,
                    ["total_length_m"] = lengthM,
                    ["volume_m3"] = volume
                },
                ["bar_type"] = new JObject { ["name"] = type, ["nominal_diameter_mm"] = dia },
                ["host"] = new JObject { ["id"] = host, ["category"] = "Structural Framing" },
                ["shape"] = new JObject { ["name"] = "M_00" },
                ["style_horizun"] = "standard",
                ["layout"] = new JObject { ["rule"] = "maximum_spacing" },
                ["provenance"] = new JObject { ["rule_id"] = rule }
            };
            if (eachMm.HasValue) o["geometry"] = new JObject { ["centreline_length_mm"] = eachMm.Value };
            return o;
        }

        private static JArray Three()
        {
            return new JArray(
                Bar("E1", "12M", 12, 100, 10, 40.0, 0.0045),
                Bar("E1", "12M", 12, 101, 5, 20.0, 0.0023),
                Bar("B1", "16M", 16, 100, 4, 16.0, 0.0032));
        }

        private static JObject GroupOf(JObject result, int index)
        {
            return (JObject)((JArray)result["groups"])[index];
        }

        // ------------------------------------------------------ it groups

        [Fact]
        public void BarsWithTheSameMarkAreOneLine()
        {
            string err;
            JObject r = RebarTakeoff.Group(Three(), new[] { RebarTakeoffKey.Mark }, null, null, out err);
            Assert.Null(err);
            Assert.Equal(2, (int)r["group_count"]);

            JObject e1 = GroupOf(r, 0);
            Assert.Equal("E1", (string)e1["group"]["mark"]);
            Assert.Equal(2, (int)e1["sets"]);
            Assert.Equal(15, (int)e1["bars"]);
            Assert.Equal(60.0, (double)e1["total_length_m"], 6);
            Assert.True((bool)e1["complete"]);
        }

        [Fact]
        public void TwoKeysMakeAFinerGrouping()
        {
            string err;
            JObject r = RebarTakeoff.Group(Three(),
                new[] { RebarTakeoffKey.Mark, RebarTakeoffKey.Host }, null, null, out err);
            Assert.Null(err);
            Assert.Equal(3, (int)r["group_count"]);
        }

        [Fact]
        public void TheRuleKeyGroupsAStirrupZoneByItsZoneBecauseTheIdCarriesIt()
        {
            var rows = new JArray(
                Bar("E1", "12M", 12, 100, 11, 30.0, 0.003, rule: "B1#start"),
                Bar("E1", "12M", 12, 100, 20, 55.0, 0.006, rule: "B1#middle"),
                Bar("E1", "12M", 12, 100, 11, 30.0, 0.003, rule: "B1#end"));
            string err;
            JObject r = RebarTakeoff.Group(rows, new[] { RebarTakeoffKey.Rule }, null, null, out err);
            Assert.Null(err);
            Assert.Equal(3, (int)r["group_count"]);
            Assert.Equal("B1#start", (string)GroupOf(r, 0)["group"]["rule"]);
            Assert.Equal(42, (int)r["totals"]["bars"]);
        }

        [Fact]
        public void TheTotalsAddUpAcrossEveryGroup()
        {
            string err;
            JObject r = RebarTakeoff.Group(Three(), new[] { RebarTakeoffKey.BarType }, null, null, out err);
            Assert.Equal(3, (int)r["totals"]["sets"]);
            Assert.Equal(19, (int)r["totals"]["bars"]);
            Assert.Equal(76.0, (double)r["totals"]["total_length_m"], 6);
            Assert.True((bool)r["totals"]["complete"]);
        }

        [Fact]
        public void AUniformBarLengthIsReportedAndAMixedOneIsNot()
        {
            var same = new JArray(Bar("E1", "12M", 12, 1, 5, 20, 0.002, eachMm: 4000),
                                  Bar("E1", "12M", 12, 2, 5, 20, 0.002, eachMm: 4000));
            var mixed = new JArray(Bar("E1", "12M", 12, 1, 5, 20, 0.002, eachMm: 4000),
                                   Bar("E1", "12M", 12, 2, 5, 20, 0.002, eachMm: 3000));
            string err;
            Assert.Equal(4000.0, (double)GroupOf(
                RebarTakeoff.Group(same, new[] { RebarTakeoffKey.Mark }, null, null, out err), 0)["bar_length_each_mm"]);

            JObject m = GroupOf(RebarTakeoff.Group(mixed, new[] { RebarTakeoffKey.Mark }, null, null, out err), 0);
            Assert.Equal(JTokenType.Null, m["bar_length_each_mm"].Type);
            Assert.Contains("not all the same length", (string)m["bar_length_each_why"]);
        }

        // ------------------------------------- the zero that is really an absence

        [Fact]
        public void AnUnreadableLengthDoesNotBecomeAZeroInTheTotal()
        {
            JObject broken = Bar("E1", "12M", 12, 100, 5, 20.0, 0.002);
            ((JObject)broken["measured"])["total_length_m"] = JValue.CreateNull();
            var rows = new JArray(Bar("E1", "12M", 12, 100, 10, 40.0, 0.004), broken);

            string err;
            JObject r = RebarTakeoff.Group(rows, new[] { RebarTakeoffKey.Mark }, null, null, out err);
            JObject g = GroupOf(r, 0);

            Assert.False((bool)g["complete"]);
            Assert.Equal(JTokenType.Null, g["total_length_m"].Type);         // NOT 40
            Assert.Equal(40.0, (double)g["total_length_m_counted"], 6);      // what could be read
            Assert.Equal(1, (int)g["unreadable"]["length"]);
            Assert.Contains("short by exactly the bars nobody could measure", (string)g["why_partial"]);

            Assert.False((bool)r["totals"]["complete"]);
            Assert.Equal(JTokenType.Null, r["totals"]["total_length_m"].Type);
        }

        [Fact]
        public void AnUnreadableQuantityDoesNotBecomeAZeroEither()
        {
            JObject broken = Bar("E1", "12M", 12, 100, 5, 20.0, 0.002);
            ((JObject)broken["measured"])["quantity"] = JValue.CreateNull();
            string err;
            JObject g = GroupOf(RebarTakeoff.Group(new JArray(broken),
                new[] { RebarTakeoffKey.Mark }, null, null, out err), 0);
            Assert.Equal(JTokenType.Null, g["bars"].Type);
            Assert.Equal(0, (int)g["bars_counted"]);
            Assert.Equal(1, (int)g["unreadable"]["quantity"]);
        }

        [Fact]
        public void ANonFiniteMeasurementIsTreatedAsUnreadableRatherThanSummed()
        {
            JObject broken = Bar("E1", "12M", 12, 100, 5, 20.0, 0.002);
            ((JObject)broken["measured"])["total_length_m"] = double.NaN;
            string err;
            JObject g = GroupOf(RebarTakeoff.Group(new JArray(broken),
                new[] { RebarTakeoffKey.Mark }, null, null, out err), 0);
            Assert.Equal(JTokenType.Null, g["total_length_m"].Type);
            Assert.Equal(1, (int)g["unreadable"]["length"]);
        }

        [Fact]
        public void ABarWithNoMarkIsItsOwnGroupRatherThanJoiningTheMarkedOnes()
        {
            JObject unmarked = Bar("E1", "12M", 12, 100, 5, 20.0, 0.002);
            ((JObject)unmarked["measured"])["schedule_mark"] = JValue.CreateNull();
            var rows = new JArray(Bar("E1", "12M", 12, 100, 10, 40.0, 0.004), unmarked);
            string err;
            JObject r = RebarTakeoff.Group(rows, new[] { RebarTakeoffKey.Mark }, null, null, out err);
            Assert.Equal(2, (int)r["group_count"]);
            Assert.Equal(JTokenType.Null, GroupOf(r, 1)["group"]["mark"].Type);
        }

        // ------------------------------------------------------- the weight

        [Fact]
        public void NoDensityMeansNoWeightAndSaysWhy()
        {
            string err;
            JObject r = RebarTakeoff.Group(Three(), new[] { RebarTakeoffKey.Mark }, null, null, out err);
            Assert.False((bool)r["mass"]["reported"]);
            Assert.Contains("no density was declared", (string)r["mass"]["why"]);
            Assert.Null(GroupOf(r, 0)["mass_kg"]);
        }

        [Fact]
        public void ADeclaredDensityWithASourceProducesAWeightAndPublishesBoth()
        {
            string err;
            JObject r = RebarTakeoff.Group(Three(), new[] { RebarTakeoffKey.Mark },
                                           7850, "project specification, section 03 20 00", out err);
            Assert.Null(err);
            Assert.Equal(7850.0, (double)r["density"]["kg_per_m3"]);
            Assert.Contains("03 20 00", (string)r["density"]["source"]);
            // E1: 0.0045 + 0.0023 = 0.0068 m3
            Assert.Equal(53.38, (double)GroupOf(r, 0)["mass_kg"], 2);
        }

        [Fact]
        public void ADensityWithoutASourceIsRefused()
        {
            string err;
            Assert.Null(RebarTakeoff.Group(Three(), new[] { RebarTakeoffKey.Mark }, 7850, null, out err));
            Assert.Contains(RebarTakeoff.CodeDensityWithoutSource, err);
            Assert.Contains("nobody can trace", err);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ADensityThatIsNotAPositiveFiniteNumberIsRefused(double density)
        {
            string err;
            Assert.Null(RebarTakeoff.Group(Three(), new[] { RebarTakeoffKey.Mark }, density, "somewhere", out err));
            Assert.Contains(RebarTakeoff.CodeDensityNotUsable, err);
        }

        [Fact]
        public void AnUnreadableVolumeLeavesNoWeightRatherThanAShortOne()
        {
            JObject broken = Bar("E1", "12M", 12, 100, 5, 20.0, 0.002);
            ((JObject)broken["measured"])["volume_m3"] = JValue.CreateNull();
            string err;
            JObject g = GroupOf(RebarTakeoff.Group(new JArray(broken),
                new[] { RebarTakeoffKey.Mark }, 7850, "spec", out err), 0);
            Assert.Equal(JTokenType.Null, g["mass_kg"].Type);
            Assert.Equal(0.0, (double)g["mass_kg_counted"]);
        }

        // ------------------------------------------------------ the vocabulary

        [Fact]
        public void AnUnknownGroupKeyIsRefusedByNameRatherThanIgnored()
        {
            string err;
            Assert.Null(RebarTakeoff.Group(Three(), new[] { "mrak" }, null, null, out err));
            Assert.Contains(RebarTakeoff.CodeUnknownKey, err);
            Assert.Contains("mrak", err);
        }

        [Fact]
        public void NoKeysAtAllIsRefused()
        {
            string err;
            Assert.Null(RebarTakeoff.Group(Three(), new string[0], null, null, out err));
            Assert.Contains(RebarTakeoff.CodeNoKeys, err);
            Assert.Null(RebarTakeoff.Group(Three(), null, null, null, out err));
        }

        [Fact]
        public void EveryPublishedKeyActuallyWorks()
        {
            foreach (string k in RebarTakeoffKey.All)
            {
                string err;
                JObject r = RebarTakeoff.Group(Three(), new[] { k }, null, null, out err);
                Assert.Null(err);
                Assert.True((int)r["group_count"] >= 1, k);
            }
        }

        [Fact]
        public void TheKeysItAcceptsArePublishedInTheReply()
        {
            string err;
            JObject r = RebarTakeoff.Group(Three(), new[] { RebarTakeoffKey.Mark }, null, null, out err);
            var published = ((JArray)r["group_keys_available"]).Select(x => (string)x).ToArray();
            Assert.Equal(RebarTakeoffKey.All, published);
        }

        [Fact]
        public void NoRowsAtAllIsAnEmptyTakeoffRatherThanAFailure()
        {
            string err;
            JObject r = RebarTakeoff.Group(new JArray(), new[] { RebarTakeoffKey.Mark }, null, null, out err);
            Assert.Null(err);
            Assert.Equal(0, (int)r["group_count"]);
            Assert.Equal(0, (int)r["sets_read"]);
            Assert.True((bool)r["totals"]["complete"]);
        }
    }
}
