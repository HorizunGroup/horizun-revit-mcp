// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// L10: two sets of bars compared BAR BY BAR (horizun_query_structure mode=compare_bars).
// The rows below have the shape the command builds from the model: one row per bar of a
// set - `<set id>:<position>`, the set's bar type and NOMINAL diameter as RebarFacts reads
// them, and the centreline Revit draws for that bar (position 0's tessellated centreline
// offset by GetBarPositionTransform) as [x, y, z] triples in millimetres. Tolerances are
// declared by the caller and have no defaults.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarBarComparisonTests
    {
        private static JObject Bar(long set, int position, double dia, params double[][] pts) => new JObject
        {
            ["id"] = set + ":" + position,
            ["set_id"] = set,
            ["position"] = position,
            ["bar_type"] = dia + "M",
            ["nominal_diameter_mm"] = dia,
            ["centreline_points_mm"] = new JArray(pts.Select(p => (object)new JArray(p[0], p[1], p[2])).ToArray())
        };

        private static double[] P(double x, double y, double z) => new[] { x, y, z };

        private static JObject Unreadable(long set, int position, double dia) => new JObject
        {
            ["id"] = set + ":" + position, ["set_id"] = set, ["position"] = position, ["bar_type"] = dia + "M",
            ["nominal_diameter_mm"] = dia, ["centreline_points_mm"] = null,
            ["why"] = "the bar would not return its centreline curves."
        };

        private static RebarBarComparison.Tolerances Tol(double identical, double near, double length)
        {
            RebarBarComparison.Tolerances t = RebarBarComparison.ReadTolerances(
                new JObject { ["identical_mm"] = identical, ["near_mm"] = near, ["length_mm"] = length }, out string code, out string error);
            Assert.True(t != null, code + ": " + error);
            return t;
        }

        private static List<RebarBarComparison.Bar> Read(params JObject[] rows)
        {
            List<RebarBarComparison.Bar> bars = RebarBarComparison.ReadBars(new JArray(rows.Cast<object>().ToArray()), "a", out string code, out string error);
            Assert.True(bars != null, code + ": " + error);
            return bars;
        }

        private static JObject Compare(JObject[] a, JObject[] b, RebarBarComparison.Tolerances tol, int offset = 0, int max = 500) =>
            RebarBarComparison.Compare(Read(a), Read(b), tol, offset, max);

        private static JObject Row(JObject result, string id) => (JObject)result["rows"].Single(r => (string)r["id"] == id);

        // ---------------------------------------------------------------- tolerances

        [Theory]
        [InlineData(null)]
        [InlineData("{ 'identical_mm': 5, 'near_mm': 50 }")]                                   // length_mm missing
        [InlineData("{ 'identical_mm': 5, 'length_mm': 10 }")]                                 // near_mm missing
        [InlineData("{ 'near_mm': 50, 'length_mm': 10 }")]                                     // identical_mm missing
        [InlineData("{ 'identical_mm': -1, 'near_mm': 50, 'length_mm': 10 }")]
        [InlineData("{ 'identical_mm': 60, 'near_mm': 50, 'length_mm': 10 }")]                 // near below identical
        [InlineData("{ 'identical_mm': 5, 'near_mm': 50, 'length_mm': '10' }")]                // a string is not a number
        [InlineData("{ 'identical_mm': 5, 'near_mm': 50, 'length_mm': 10, 'angle_deg': 1 }")]  // an unknown key
        [InlineData("[ 5, 50, 10 ]")]
        public void Tolerances_are_declared_whole_or_refused(string json)
        {
            RebarBarComparison.Tolerances t = RebarBarComparison.ReadTolerances(json == null ? null : JToken.Parse(json), out string code, out string error);
            Assert.Null(t);
            Assert.Equal(RebarBarComparison.CodeTolerance, code);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        // ---------------------------------------------------------------- bar rows

        [Theory]
        [InlineData("[]")]
        [InlineData("[ { 'set_id': 1, 'position': 0, 'nominal_diameter_mm': 16, 'centreline_points_mm': [[0,0,0],[1,0,0]] } ]")]           // no id
        [InlineData("[ { 'id': '1:0', 'set_id': 1, 'position': 0, 'nominal_diameter_mm': 16, 'centreline_points_mm': [[0,0],[1,0,0]] } ]")]   // not a triple
        [InlineData("[ { 'id': '1:0', 'set_id': 1, 'position': 0, 'nominal_diameter_mm': 'x', 'centreline_points_mm': [[0,0,0],[1,0,0]] } ]")]
        [InlineData("[ { 'id': '1:0', 'set_id': 1, 'position': 0, 'nominal_diameter_mm': 16, 'centreline_points_mm': [[0,0,0],[1,0,0]] }, " +
                    "  { 'id': '1:0', 'set_id': 1, 'position': 0, 'nominal_diameter_mm': 16, 'centreline_points_mm': [[0,0,0],[1,0,0]] } ]")]  // one id twice
        public void Bar_rows_that_are_not_usable_are_refused(string json)
        {
            List<RebarBarComparison.Bar> bars = RebarBarComparison.ReadBars(JArray.Parse(json), "a", out string code, out string error);
            Assert.Null(bars);
            Assert.Equal(RebarBarComparison.CodeBars, code);
            Assert.Contains("a", error);
        }

        // ------------------------------------------------------------- classification

        [Fact]
        public void A_bar_two_millimetres_off_is_identical_and_the_distance_is_measured()
        {
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                new[] { Bar(9, 0, 16, P(0, 0, 2), P(1000, 0, 2)) }, Tol(5, 50, 10));
            JObject row = Row(r, "1:0");
            Assert.Equal(RebarBarComparison.Identical, (string)row["verdict"]);
            Assert.Equal("9:0", (string)row["match"]["id"]);
            Assert.Equal(2.0, (double)row["mean_distance_mm"], 6);
            Assert.Equal(0.0, (double)row["length_difference_mm"], 6);
            Assert.Equal(RebarBarComparison.Identical, (string)r["verdict"]);
            Assert.Equal(1, (int)r["summary"]["identical"]);
        }

        [Fact]
        public void The_distance_is_point_to_segment_in_space()
        {
            // 3 across and 4 up: five millimetres from the other bar's line, not three or four.
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                new[] { Bar(9, 0, 16, P(0, 3, 4), P(1000, 3, 4)) }, Tol(5, 50, 10));
            Assert.Equal(5.0, (double)Row(r, "1:0")["mean_distance_mm"], 6);
            Assert.Equal(RebarBarComparison.Identical, (string)Row(r, "1:0")["verdict"]);
        }

        [Fact]
        public void Thirty_millimetres_off_is_near_and_eighty_is_unmatched_with_its_nearest_named()
        {
            JObject near = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                   new[] { Bar(9, 0, 16, P(0, 30, 0), P(1000, 30, 0)) }, Tol(5, 50, 10));
            Assert.Equal(RebarBarComparison.Near, (string)Row(near, "1:0")["verdict"]);
            Assert.Equal(RebarBarComparison.DifferencesFound, (string)near["verdict"]);

            JObject far = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                  new[] { Bar(9, 0, 16, P(0, 80, 0), P(1000, 80, 0)) }, Tol(5, 100, 10));
            Assert.Equal(RebarBarComparison.Near, (string)Row(far, "1:0")["verdict"]);   // inside a near_mm of 100

            JObject none = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                   new[] { Bar(9, 0, 16, P(0, 80, 0), P(1000, 80, 0)) }, Tol(5, 50, 10));
            JObject row = Row(none, "1:0");
            Assert.Equal(RebarBarComparison.Unmatched, (string)row["verdict"]);
            Assert.Equal(JTokenType.Null, row["match"].Type);
            Assert.Equal("9:0", (string)row["nearest"]["id"]);
            Assert.Equal(80.0, (double)row["nearest"]["mean_distance_mm"], 6);
            Assert.Equal(new[] { "9:0" }, none["unmatched_b"].Select(x => (string)x["id"]).ToArray());
            Assert.Equal(1, (int)none["summary"]["unmatched_a"]);
            Assert.Equal(1, (int)none["summary"]["unmatched_b"]);
        }

        [Fact]
        public void Identical_needs_the_length_too()
        {
            // B is 100 mm shorter along the same line: close on average, and not the same bar.
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                new[] { Bar(9, 0, 16, P(0, 0, 0), P(900, 0, 0)) }, Tol(10, 50, 10));
            JObject row = Row(r, "1:0");
            Assert.Equal(RebarBarComparison.Near, (string)row["verdict"]);
            Assert.Equal(100.0, (double)row["length_difference_mm"], 6);
            Assert.False((bool)row["length_within_tolerance"]);
            Assert.True((double)row["mean_distance_mm"] <= 10.0);
        }

        [Fact]
        public void Another_diameter_is_never_a_match_even_in_the_same_place()
        {
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                new[] { Bar(9, 0, 12, P(0, 0, 0), P(1000, 0, 0)) }, Tol(5, 50, 10));
            JObject row = Row(r, "1:0");
            Assert.Equal(RebarBarComparison.Unmatched, (string)row["verdict"]);
            Assert.Equal(JTokenType.Null, row["nearest"].Type);
            Assert.Contains("diameter", (string)row["why"]);
        }

        [Fact]
        public void The_shape_is_compared_not_only_the_ends()
        {
            // An L bar against a straight bar between the same two ends.
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(0, 0, 500), P(1000, 0, 500)) },
                                new[] { Bar(9, 0, 16, P(0, 0, 0), P(1000, 0, 500)) }, Tol(5, 50, 10));
            Assert.NotEqual(RebarBarComparison.Identical, (string)Row(r, "1:0")["verdict"]);
            JObject same = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(0, 0, 500), P(1000, 0, 500)) },
                                   new[] { Bar(9, 0, 16, P(0, 0, 0), P(0, 0, 250), P(0, 0, 500), P(1000, 0, 500)) }, Tol(5, 50, 10));
            Assert.Equal(RebarBarComparison.Identical, (string)Row(same, "1:0")["verdict"]);   // extra vertices, same steel
        }

        [Fact]
        public void A_short_bar_lying_on_a_long_one_is_not_the_same_bar_and_is_still_named_as_the_nearest()
        {
            // Measured one way only (short onto long) the distance is 20 mm; the other way most of
            // the long bar is metres from the short one. Both ways are measured and the larger kept.
            JObject r = Compare(new[] { Bar(1, 0, 16, P(5000, 20, 0), P(5600, 20, 0)) },
                                new[] { Bar(9, 0, 16, P(0, 0, 0), P(12000, 0, 0)) }, Tol(5, 50, 10));
            JObject row = Row(r, "1:0");
            Assert.Equal(RebarBarComparison.Unmatched, (string)row["verdict"]);
            Assert.Equal("9:0", (string)row["nearest"]["id"]);
            Assert.True((double)row["nearest"]["mean_distance_mm"] > 1000);
            Assert.Equal(11400.0, (double)row["nearest"]["length_difference_mm"], 6);
        }

        [Fact]
        public void The_nearest_bar_is_the_nearest_however_far_and_of_the_same_diameter()
        {
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                new[] { Bar(9, 0, 16, P(0, 9000, 0), P(1000, 9000, 0)), Bar(9, 1, 16, P(0, 3000, 0), P(1000, 3000, 0)),
                                        Bar(9, 2, 12, P(0, 10, 0), P(1000, 10, 0)) }, Tol(5, 50, 10));
            JObject row = Row(r, "1:0");
            Assert.Equal("9:1", (string)row["nearest"]["id"]);
            Assert.Equal(3000.0, (double)row["nearest"]["mean_distance_mm"], 6);
        }

        // ------------------------------------------------------------- one to one

        [Fact]
        public void One_bar_of_b_answers_for_one_bar_of_a()
        {
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)), Bar(1, 1, 16, P(0, 10, 0), P(1000, 10, 0)) },
                                new[] { Bar(9, 0, 16, P(0, 1, 0), P(1000, 1, 0)) }, Tol(5, 50, 10));
            Assert.Equal(RebarBarComparison.Identical, (string)Row(r, "1:0")["verdict"]);
            JObject second = Row(r, "1:1");
            Assert.Equal(RebarBarComparison.Unmatched, (string)second["verdict"]);
            Assert.Equal("9:0", (string)second["nearest"]["id"]);
            Assert.Equal("1:0", (string)second["nearest"]["matched_to"]);
        }

        [Fact]
        public void An_identical_pair_is_not_lost_to_a_nearer_pair_of_another_length()
        {
            // A2 lies ON B1 (mean distance under a millimetre) but is 20 mm short; A1 is 4 mm off
            // B1 and as long. By distance alone A2 would take B1 and push A1 to B2. Identical
            // pairs are assigned first, so A1 keeps B1 and A2 goes to B2.
            JObject r = Compare(
                new[] { Bar(1, 0, 16, P(0, 4, 0), P(1000, 4, 0)), Bar(1, 1, 16, P(0, 0, 0), P(980, 0, 0)) },
                new[] { Bar(9, 0, 16, P(0, 0, 0), P(1000, 0, 0)), Bar(9, 1, 16, P(0, 40, 0), P(980, 40, 0)) },
                Tol(5, 120, 10));
            Assert.True((double)RebarBarComparison.Compare(
                Read(Bar(1, 1, 16, P(0, 0, 0), P(980, 0, 0))), Read(Bar(9, 0, 16, P(0, 0, 0), P(1000, 0, 0))),
                Tol(5, 120, 10), 0, 10)["rows"][0]["mean_distance_mm"] < 4.0);
            Assert.Equal("9:0", (string)Row(r, "1:0")["match"]["id"]);
            Assert.Equal(RebarBarComparison.Identical, (string)Row(r, "1:0")["verdict"]);
            Assert.Equal("9:1", (string)Row(r, "1:1")["match"]["id"]);
            Assert.Equal(RebarBarComparison.Near, (string)Row(r, "1:1")["verdict"]);
            Assert.Contains("greedy", (string)r["matching"]);
        }

        // ------------------------------------------------------------- the verdict

        [Fact]
        public void Every_bar_paired_identically_both_ways_is_the_only_identical_verdict()
        {
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)), Bar(1, 1, 16, P(0, 200, 0), P(1000, 200, 0)) },
                                new[] { Bar(9, 1, 16, P(0, 200, 1), P(1000, 200, 1)), Bar(9, 0, 16, P(0, 0, 1), P(1000, 0, 1)) },
                                Tol(5, 50, 10));
            Assert.Equal(RebarBarComparison.Identical, (string)r["verdict"]);
            Assert.Equal(2, (int)r["summary"]["bars_a"]);
            Assert.Equal(2, (int)r["summary"]["bars_b"]);
            Assert.Empty((JArray)r["unmatched_b"]);

            JObject extra = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                    new[] { Bar(9, 0, 16, P(0, 0, 1), P(1000, 0, 1)), Bar(9, 1, 16, P(0, 900, 0), P(1000, 900, 0)) },
                                    Tol(5, 50, 10));
            Assert.Equal(RebarBarComparison.DifferencesFound, (string)extra["verdict"]);   // a bar only B has is a difference
        }

        [Fact]
        public void A_bar_that_could_not_be_read_makes_the_comparison_incomplete_never_identical()
        {
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)), Unreadable(1, 1, 16) },
                                new[] { Bar(9, 0, 16, P(0, 0, 1), P(1000, 0, 1)) }, Tol(5, 50, 10));
            Assert.Equal(RebarBarComparison.Incomplete, (string)r["verdict"]);
            Assert.Equal(RebarBarComparison.Unreadable, (string)Row(r, "1:1")["verdict"]);
            Assert.Equal(1, (int)r["summary"]["unreadable_a"]);

            JObject rb = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                 new[] { Bar(9, 0, 16, P(0, 0, 1), P(1000, 0, 1)), Unreadable(9, 1, 16) }, Tol(5, 50, 10));
            Assert.Equal(RebarBarComparison.Incomplete, (string)rb["verdict"]);
            Assert.Equal(1, (int)rb["summary"]["unreadable_b"]);
            Assert.Contains(rb["unmatched_b"], x => (string)x["id"] == "9:1" && (string)x["verdict"] == RebarBarComparison.Unreadable);

            // A difference outranks an unreadable bar: something is known to disagree.
            JObject both = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)), Unreadable(1, 1, 16) },
                                   new[] { Bar(9, 0, 16, P(0, 900, 0), P(1000, 900, 0)) }, Tol(5, 50, 10));
            Assert.Equal(RebarBarComparison.DifferencesFound, (string)both["verdict"]);
        }

        [Fact]
        public void The_tolerances_are_echoed()
        {
            JObject r = Compare(new[] { Bar(1, 0, 16, P(0, 0, 0), P(1000, 0, 0)) },
                                new[] { Bar(9, 0, 16, P(0, 0, 0), P(1000, 0, 0)) }, Tol(5, 50, 10));
            Assert.Equal(5.0, (double)r["tolerances"]["identical_mm"]);
            Assert.Equal(50.0, (double)r["tolerances"]["near_mm"]);
            Assert.Equal(10.0, (double)r["tolerances"]["length_mm"]);
        }

        // ------------------------------------------------------------- paging and scale

        [Fact]
        public void Rows_are_paged_and_the_summary_is_over_everything()
        {
            JObject[] a = Enumerable.Range(0, 7).Select(i => Bar(1, i, 16, P(0, i * 200, 0), P(1000, i * 200, 0))).ToArray();
            JObject[] b = Enumerable.Range(0, 7).Select(i => Bar(9, i, 16, P(0, i * 200, 0), P(1000, i * 200, 0))).ToArray();
            JObject r = Compare(a, b, Tol(5, 50, 10), 3, 3);
            Assert.Equal(new[] { "1:3", "1:4", "1:5" }, r["rows"].Select(x => (string)x["id"]).ToArray());
            Assert.Equal(6, (int)r["next_offset"]);
            Assert.Equal(7, (int)r["summary"]["identical"]);
            JObject last = Compare(a, b, Tol(5, 50, 10), 6, 3);
            Assert.Equal(JTokenType.Null, last["next_offset"].Type);
        }

        [Fact]
        public void A_cage_of_thousands_of_bars_is_paired_bar_for_bar()
        {
            // 60 beams x 50 stirrup-like bars a side: 3000 bars, each B bar one millimetre off its A bar.
            var a = new List<JObject>();
            var b = new List<JObject>();
            for (int beam = 0; beam < 60; beam++)
                for (int k = 0; k < 50; k++)
                {
                    double x = beam * 8000 + k * 100;
                    double[][] loop = { P(x, 0, 0), P(x, 200, 0), P(x, 200, 400), P(x, 0, 400), P(x, 0, 0) };
                    a.Add(Bar(100 + beam, k, 10, loop));
                    b.Add(Bar(900 + beam, k, 10, loop.Select(p => P(p[0] + 1, p[1], p[2])).ToArray()));
                }
            JObject r = Compare(a.ToArray(), b.ToArray(), Tol(5, 50, 10), 0, 10);
            Assert.Equal(3000, (int)r["summary"]["identical"]);
            Assert.Equal(RebarBarComparison.Identical, (string)r["verdict"]);
        }
    }
}
