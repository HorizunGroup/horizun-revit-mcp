// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The model against the schedule it was built from, mark by mark (refuerzo desde
// cartilla, F1.3). The model side is RebarTakeoff.Group's own output, built here
// from rows in RebarFacts.Describe's shape, so the comparison is tested against
// the real takeoff and not a hand-made one. Synthetic marks and numbers.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarReconcileTests
    {
        /// <summary>One set as RebarFacts.Describe reports it: mark, bar type, diameters, quantity, total length, volume.</summary>
        private static JObject Set(string mark, string barType, double nominal, int quantity, double totalLengthM, double volumeM3)
        {
            return JObject.Parse(@"{
              'bar_type': { 'resolved': true, 'name': '" + barType + @"', 'nominal_diameter_mm': " + nominal.ToString(System.Globalization.CultureInfo.InvariantCulture) +
              @", 'model_diameter_mm': " + nominal.ToString(System.Globalization.CultureInfo.InvariantCulture) + @" },
              'shape': { 'name': 'M_00' }, 'style_horizun': 'standard', 'layout': { 'rule_horizun': 'fixed_number' },
              'measured': { 'schedule_mark': '" + mark + @"', 'quantity': " + quantity + @",
                            'total_length_m': " + totalLengthM.ToString(System.Globalization.CultureInfo.InvariantCulture) + @",
                            'volume_m3': " + volumeM3.ToString(System.Globalization.CultureInfo.InvariantCulture) + @" },
              'geometry': { 'centreline_length_mm': 6000 } }".Replace('\'', '"'));
        }

        private static JObject Takeoff(params JObject[] sets)
        {
            string error;
            JObject t = RebarTakeoff.Group(new JArray(sets), new[] { RebarTakeoffKey.Mark, RebarTakeoffKey.BarType, RebarTakeoffKey.NominalDiameter },
                                           7850, "synthetic test density", out error);
            Assert.Null(error);
            return t;
        }

        private static List<RebarReconcile.Line> Schedule(string json)
        {
            string code, error;
            List<RebarReconcile.Line> l = RebarReconcile.ReadSchedule(JArray.Parse(json.Replace('\'', '"')), out code, out error);
            Assert.True(l != null, error);
            return l;
        }

        [Fact]
        public void A_model_built_as_scheduled_agrees_mark_by_mark()
        {
            // 4 bars x 6.00 m of 16M in two sets of two (two beams), and 41 stirrups of 10M x 1.62 m.
            JObject t = Takeoff(Set("A1", "16M", 15.9, 2, 12.0, 0.0024), Set("A1", "16M", 15.9, 2, 12.0, 0.0024),
                                Set("E1", "10M", 9.5, 41, 66.42, 0.0047));
            var s = Schedule("[ { 'mark': 'A1', 'bar_type': '16M', 'count': 4, 'length_mm': 6000 }, { 'mark': 'E1', 'bar_type': '10M', 'count': 41, 'length_mm': 1620 } ]");
            JObject r = RebarReconcile.Compare(s, t, 0.5, null);
            Assert.Equal("agrees", (string)r["verdict"]);
            Assert.Equal(2, (int)r["summary"]["agrees"]);
        }

        [Fact]
        public void A_missing_bar_a_wrong_length_and_a_wrong_type_are_named()
        {
            JObject t = Takeoff(Set("A1", "16M", 15.9, 3, 18.0, 0.0036), Set("A2", "19M", 19.1, 2, 13.0, 0.0037));
            var s = Schedule("[ { 'mark': 'A1', 'bar_type': '16M', 'count': 4, 'length_mm': 6000 }, { 'mark': 'A2', 'bar_type': '16M', 'count': 2, 'length_mm': 6000 } ]");
            JObject r = RebarReconcile.Compare(s, t, 0.5, null);
            Assert.Equal("differences_found", (string)r["verdict"]);
            JObject a1 = r["rows"].OfType<JObject>().Single(x => (string)x["mark"] == "A1");
            Assert.Contains(a1["differences"].OfType<JObject>(), d => (string)d["field"] == "count");
            JObject a2 = r["rows"].OfType<JObject>().Single(x => (string)x["mark"] == "A2");
            Assert.Contains(a2["differences"].OfType<JObject>(), d => (string)d["field"] == "bar_type");
            Assert.Contains(a2["differences"].OfType<JObject>(), d => (string)d["field"] == "total_length_m");   // 13.0 m against 12.0 m
        }

        [Fact]
        public void A_mark_only_one_side_has_is_named_on_that_side()
        {
            JObject t = Takeoff(Set("X9", "16M", 15.9, 2, 12.0, 0.0024));
            var s = Schedule("[ { 'mark': 'A1', 'diameter_mm': 15.9, 'count': 4, 'length_mm': 6000 } ]");
            JObject r = RebarReconcile.Compare(s, t, 0.5, null);
            Assert.Equal("missing_in_model", (string)r["rows"].OfType<JObject>().Single(x => (string)x["mark"] == "A1")["verdict"]);
            Assert.Equal("not_in_schedule", (string)r["rows"].OfType<JObject>().Single(x => (string)x["mark"] == "X9")["verdict"]);
        }

        [Fact]
        public void Weight_is_compared_only_with_a_tolerance_and_the_model_weighed()
        {
            JObject t = Takeoff(Set("A1", "16M", 15.9, 4, 24.0, 0.0048));   // 0.0048 m3 x 7850 = 37.68 kg
            var s = Schedule("[ { 'mark': 'A1', 'bar_type': '16M', 'count': 4, 'length_mm': 6000, 'weight_kg': 40.0 } ]");
            JObject without = RebarReconcile.Compare(s, t, 0.5, null);
            Assert.Equal("agrees", (string)without["verdict"]);
            Assert.NotNull(without["rows"][0]["weight_not_compared"]);
            JObject with = RebarReconcile.Compare(s, t, 0.5, 2.0);
            Assert.Contains(with["rows"][0]["differences"].OfType<JObject>(), d => (string)d["field"] == "weight_kg");
        }

        // Backlog #57: when only some lines of a mark stated weight_kg, the mark's weight was
        // dropped with no word, exactly as if none had.
        [Fact]
        public void A_mark_whose_lines_state_weight_only_in_part_says_so()
        {
            JObject t = Takeoff(Set("A1", "16M", 15.9, 4, 24.0, 0.0048));
            var s = Schedule("[ { 'mark': 'A1', 'bar_type': '16M', 'count': 2, 'length_mm': 6000, 'weight_kg': 20.0 }," +
                             "  { 'mark': 'A1', 'bar_type': '16M', 'count': 2, 'length_mm': 6000 } ]");
            JObject r = RebarReconcile.Compare(s, t, 0.5, 2.0);
            JObject row = (JObject)r["rows"][0];
            Assert.Equal(JTokenType.Null, row["schedule"]["weight_kg"].Type);
            Assert.Contains("1 of 2", (string)row["weight_not_compared"]);
        }

        [Fact]
        public void A_schedule_row_that_says_nothing_about_size_is_refused()
        {
            string code, error;
            Assert.Null(RebarReconcile.ReadSchedule(JArray.Parse("[ { \"mark\": \"A1\", \"count\": 4, \"length_mm\": 6000 } ]"), out code, out error));
            Assert.Contains("bar_type or diameter_mm", error);
            Assert.Null(RebarReconcile.ReadSchedule(JArray.Parse("[ { \"mark\": \"A1\", \"bar_type\": \"16M\", \"count\": 4.5, \"length_mm\": 6000 } ]"), out code, out error));
            Assert.Null(RebarReconcile.ReadSchedule(JArray.Parse("[ { \"mark\": \"A1\", \"bar_type\": \"16M\", \"count\": 4, \"length\": 6000 } ]"), out code, out error));
            Assert.Contains("not a known key", error);
        }
    }
}
