// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// mode=steel_review (acero 3.0, stage 1c): every bar of a model against every other,
// with NO requirement set - a generated model, or one built by hand, has none for
// its steel. duplicate_bar (one bar built twice) and overlapping_bar (a bar through
// another, or closer than declared) over rebar sets and the bars of area and path
// systems, counted in total and by host category, the pairs worst first.
//
// The facts below are what the Revit side reads per set: the hook-free centreline
// at every position the model says holds a bar (model millimetres), the model
// diameter, the host and its category - never a shape invented for the test.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Core.Tests
{
    public class RebarSteelReviewTests
    {
        private readonly ITestOutputHelper _out;
        public RebarSteelReviewTests(ITestOutputHelper output) { _out = output; }

        private const double Tol = 2.0;

        private static double[] P(double x, double y, double z) => new[] { x, y, z };

        /// <summary>A rebar set: one straight bar `from`-`to`, repeated at every offset.</summary>
        private static ReviewSetFacts Set(long id, long host, string category, double dia, double[] from, double[] to,
                                          double[][] offsets = null, string style = "standard")
        {
            var f = new ReviewSetFacts { Id = id, HostId = host, HostCategory = category, ModelDiameterMm = dia, Style = style };
            offsets = offsets ?? new[] { P(0, 0, 0) };
            for (int i = 0; i < offsets.Length; i++)
                f.Bars.Add(new OverlapBar
                {
                    Position = i,
                    Points = new List<double[]> { Add(from, offsets[i]), Add(to, offsets[i]) }
                });
            return f;
        }

        /// <summary>A bar of an area or path system (RebarInSystem): its own element, the system's id.</summary>
        private static ReviewSetFacts SystemBar(long id, long system, string label, long host, string category, double dia,
                                                double[] from, double[] to, double[][] offsets)
        {
            ReviewSetFacts f = Set(id, host, category, dia, from, to, offsets);
            f.Label = label;
            f.SystemId = system;
            return f;
        }

        private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        private static double[][] Along(double[] step, int n) => Enumerable.Range(0, n).Select(i => P(step[0] * i, step[1] * i, step[2] * i)).ToArray();

        private static JObject Review(StructuralClearances c, int maxPairs, params ReviewSetFacts[] sets)
            => RebarSteelReview.Review(sets, c, Tol, maxPairs, null, 0);

        private static int Count(JObject r, string kind, string what) => (int)r["counts"][kind][what];

        [Fact]
        public void A_model_with_planted_duplicates_and_crossings_is_counted_exactly_in_total_and_by_host_category()
        {
            var sets = new List<ReviewSetFacts>
            {
                // A floor's bottom mat: x bars and y bars in contact (12 mm), crossing at every node - legal.
                Set(101, 1, "Floors", 12, P(0, 0, 46), P(6000, 0, 46), Along(P(0, 200, 0), 20)),
                Set(102, 1, "Floors", 12, P(0, 0, 58), P(0, 4000, 58), Along(P(200, 0, 0), 30)),
                // PLANTED: the x bars laid again as a second set - all 20 bars twice.
                Set(103, 1, "Floors", 12, P(0, 0, 46), P(6000, 0, 46), Along(P(0, 200, 0), 20)),
                // A wall's vertical bars and, PLANTED, one of them twice.
                Set(201, 2, "Walls", 16, P(10000, 0, 0), P(10000, 0, 3000), Along(P(200, 0, 0), 10)),
                Set(202, 2, "Walls", 16, P(10400, 0, 0), P(10400, 0, 3000)),
                // PLANTED crossings: a beam bar through two of the wall's verticals, and through the duplicate.
                Set(301, 3, "Structural Framing", 20, P(9900, 0, 1500), P(10500, 0, 1500)),
                // A column bar alone, nowhere near anything.
                Set(401, 4, "Structural Columns", 20, P(20000, 0, 0), P(20000, 0, 3000)),
            };
            JObject r = Review(null, 50, sets.ToArray());
            Assert.Equal("findings", (string)r["verdict"]);
            Assert.Equal(7, (int)r["counts"]["sets"]);
            // Duplicates: 101/103 (20 bar pairs) and 201/202 (one: 201's third bar is 202).
            Assert.Equal(2, Count(r, "duplicates", "set_pairs"));
            Assert.Equal(21, Count(r, "duplicates", "bar_pairs"));
            // Intersections: the beam bar through 201 (bars 0..3 at x = 10000..10600 within 9900..10500: 0, 1, 2) and 202.
            Assert.Equal(2, Count(r, "intersections", "set_pairs"));
            Assert.Equal(4, Count(r, "intersections", "bar_pairs"));
            Assert.Equal(0, Count(r, "closer_than_declared_clearance", "set_pairs"));
            Assert.Equal("complete", (string)r["coverage"]["coverage"]);

            JObject floors = ((JArray)r["by_host_category"]).OfType<JObject>().Single(x => (string)x["host_category"] == "Floors");
            Assert.Equal(3, (int)floors["sets"]);
            Assert.Equal(70, (int)floors["bars"]);
            Assert.Equal(1, (int)floors["duplicates"]["set_pairs"]);
            Assert.Equal(20, (int)floors["duplicates"]["bar_pairs"]);
            Assert.Equal(0, (int)floors["intersections"]["set_pairs"]);
            JObject walls = ((JArray)r["by_host_category"]).OfType<JObject>().Single(x => (string)x["host_category"] == "Walls");
            Assert.Equal(1, (int)walls["duplicates"]["set_pairs"]);
            Assert.Equal(2, (int)walls["intersections"]["set_pairs"]);
            JObject framing = ((JArray)r["by_host_category"]).OfType<JObject>().Single(x => (string)x["host_category"] == "Structural Framing");
            Assert.Equal(2, (int)framing["intersections"]["set_pairs"]);
            Assert.Equal(4, (int)framing["intersections"]["bar_pairs"]);

            // Duplicates first (most bar pairs first), then intersections; both bar ids and the code on each.
            JArray pairs = (JArray)r["pairs"];
            Assert.Equal(4, pairs.Count);
            Assert.Equal("duplicate_bar", (string)pairs[0]["code"]);
            Assert.Equal(new long[] { 101, 103 }, new[] { (long)pairs[0]["rebar_id"], (long)pairs[0]["other_rebar_id"] }.OrderBy(x => x).ToArray());
            Assert.Equal(20, (int)pairs[0]["bar_pairs"]);
            Assert.Equal("duplicate_bar", (string)pairs[1]["code"]);
            Assert.All(pairs.Skip(2), p => Assert.Equal("overlapping_bar", (string)p["code"]));
            Assert.All(pairs, p => Assert.Null(p["rule_id"]));
            Assert.Equal("standard+standard (two hosts)", ((JObject)r["intersections_by_styles"]).Properties().Single().Name);
        }

        [Fact]
        public void The_bars_of_one_area_system_are_its_layout_and_two_systems_or_a_set_over_them_are_compared()
        {
            // One area system: a major layer along x and a minor along y, in contact - the system's design.
            ReviewSetFacts major = SystemBar(501, 900, RebarSteelReview.LabelArea, 1, "Floors", 12, P(0, 0, 46), P(3000, 0, 46), Along(P(0, 200, 0), 10));
            ReviewSetFacts minor = SystemBar(502, 900, RebarSteelReview.LabelArea, 1, "Floors", 12, P(0, 0, 46), P(0, 2000, 46), Along(P(200, 0, 0), 15));
            JObject r = Review(null, 50, major, minor);
            Assert.Equal("clean", (string)r["verdict"]);
            Assert.Equal(2, (int)r["counts"]["system_bars"]);
            Assert.Equal(1, (int)r["counts"]["systems"]);
            Assert.Equal(0, (int)r["counts"]["sets"]);
            // A second system laid over the first one's major layer is the same steel twice.
            ReviewSetFacts again = SystemBar(601, 901, RebarSteelReview.LabelArea, 1, "Floors", 12, P(0, 0, 46), P(3000, 0, 46), Along(P(0, 200, 0), 10));
            JObject r2 = Review(null, 50, major, minor, again);
            Assert.Equal(1, Count(r2, "duplicates", "set_pairs"));
            Assert.Equal(10, Count(r2, "duplicates", "bar_pairs"));
            JObject p = (JObject)r2["pairs"][0];
            Assert.Equal(900, (long)p["system_id"]);
            Assert.Equal(901, (long)p["other_system_id"]);
            Assert.Equal("area", (string)p["what"]);
            // And the second system's bars, laid along x through the first's minor layer at its own height,
            // are an intersection with it - the duplicate pair is not ALSO counted as one.
            Assert.Equal(1, Count(r2, "intersections", "set_pairs"));
        }

        [Fact]
        public void A_set_that_could_not_be_read_makes_the_review_incomplete_never_clean()
        {
            ReviewSetFacts ok = Set(1, 1, "Floors", 12, P(0, 0, 0), P(1000, 0, 0));
            var bad = new ReviewSetFacts { Id = 2, HostId = 1, HostCategory = "Floors", UnreadableWhy = "the set would not return its centreline." };
            JObject r = Review(null, 50, ok, bad);
            Assert.Equal("incomplete", (string)r["verdict"]);
            Assert.Equal("partial", (string)r["coverage"]["coverage"]);
            Assert.Equal(1, (int)r["counts"]["unreadable_sets"]);
            JObject u = Assert.Single(((JArray)r["unreadable"]).OfType<JObject>());
            Assert.Equal(2, (long)u["rebar_id"]);
            Assert.Contains("centreline", (string)u["why"]);
            // No model diameter is unreadable too: a clear distance is measured between surfaces.
            var noDia = Set(3, 1, "Floors", 12, P(0, 0, 0), P(1000, 0, 0)); noDia.ModelDiameterMm = null;
            Assert.Equal("incomplete", (string)Review(null, 50, ok, noDia)["verdict"]);
            // A scope reason from the reader (an id that is no reinforcement) makes it partial as well.
            JObject r3 = RebarSteelReview.Review(new[] { ok }, null, Tol, 50,
                new JArray(StructuralCoverage.Reason("element_ids", "not reinforcement", 77)), 0);
            Assert.Equal("incomplete", (string)r3["verdict"]);
        }

        [Fact]
        public void A_clean_model_says_clean_with_complete_coverage_and_counts_fabric_it_did_not_compare()
        {
            JObject r = RebarSteelReview.Review(new[]
            {
                Set(1, 1, "Floors", 12, P(0, 0, 46), P(6000, 0, 46), Along(P(0, 200, 0), 5)),
                Set(2, 1, "Floors", 12, P(0, 0, 58), P(0, 1000, 58), Along(P(200, 0, 0), 30))
            }, null, Tol, 50, null, 3);
            Assert.Equal("clean", (string)r["verdict"]);
            Assert.Equal("complete", (string)r["coverage"]["coverage"]);
            Assert.Equal(3, (int)r["counts"]["fabric_sheets_not_compared"]);
            Assert.Equal("intersections_only", (string)r["checked"]);
            Assert.True((bool)r["writes_nothing"]);
        }

        [Fact]
        public void The_pairs_are_bounded_and_the_counts_are_always_whole()
        {
            // Ten bars each crossing one long bar: ten intersection pairs, three listed.
            var sets = new List<ReviewSetFacts> { Set(1, 1, "Walls", 16, P(0, 0, 0), P(10000, 0, 0)) };
            for (int i = 0; i < 10; i++) sets.Add(Set(10 + i, 2, "Structural Columns", 16, P(500 + 900 * i, 0, -500), P(500 + 900 * i, 0, 500)));
            JObject r = Review(null, 3, sets.ToArray());
            Assert.Equal(10, Count(r, "intersections", "set_pairs"));
            Assert.Equal(3, ((JArray)r["pairs"]).Count);
            Assert.Equal(7, (int)r["pairs_not_listed"]);
        }

        [Fact]
        public void Declared_clearances_judge_bars_alongside_and_rule_pairs_are_refused_without_a_requirement_set()
        {
            string err;
            StructuralClearances c = RebarSteelReview.ReadClearances(JObject.Parse("{\"between_rules_mm\": 25, \"crossing_mm\": 0}"), out err);
            Assert.Null(err);
            JObject r = Review(c, 50, Set(1, 1, "Walls", 16, P(0, 0, 0), P(3000, 0, 0)), Set(2, 1, "Walls", 16, P(0, 30, 0), P(3000, 30, 0)));
            Assert.Equal(1, Count(r, "closer_than_declared_clearance", "set_pairs"));
            Assert.Equal("declared_clearance", (string)r["checked"]);

            Assert.Null(RebarSteelReview.ReadClearances(JObject.Parse("{\"pairs\": []}"), out err));
            Assert.StartsWith(RebarSteelReview.CodeClearancesNotUsable, err);
            Assert.Null(RebarSteelReview.ReadClearances(JObject.Parse("{\"between_rules_mm\": -1}"), out err));
            Assert.StartsWith(RebarSteelReview.CodeClearancesNotUsable, err);
            Assert.Null(RebarSteelReview.ReadClearances(JObject.Parse("{\"crossing_mm\": \"5\"}"), out err));
            Assert.False(RebarSteelReview.ReadClearances(null, out err).Declared);
            Assert.Null(err);
        }

        [Fact]
        public void Two_sets_a_coupler_in_the_model_joins_meet_at_it_and_are_not_an_intersection()
        {
            // MEASURED (acceptance building B2, Revit 2026): two pieces a Revit coupler joins overlap end to end by
            // 3.9 mm at the coupler. The model carries the coupler, so the review reads it off the bars.
            ReviewSetFacts a = Set(1, 7, "Structural Framing", 16, P(0, 0, 0), P(2000, 0, 0));
            ReviewSetFacts b = Set(2, 7, "Structural Framing", 16, P(1996, 0, 0), P(4000, 0, 0));
            JObject loose = Review(null, 50, a, b);
            Assert.Equal(1, Count(loose, "intersections", "set_pairs"));
            a.CoupledTo.Add(2);
            JObject coupled = Review(null, 50, a, b);
            Assert.Equal(0, Count(coupled, "intersections", "set_pairs"));
            Assert.Equal(1, (int)coupled["counts"]["coupled_in_the_model"]);
            Assert.Equal("clean", (string)coupled["verdict"]);
            // A coupler does not make one bar built twice right.
            ReviewSetFacts twin = Set(3, 7, "Structural Framing", 16, P(0, 0, 0), P(2000, 0, 0));
            twin.CoupledTo.Add(1);
            Assert.Equal(1, Count(Review(null, 50, a, twin), "duplicates", "set_pairs"));
        }

        [Fact]
        public void Bars_a_crossing_order_displaced_are_judged_where_the_model_has_them()
        {
            // A model has no declaration to read, so the review sees the bars where they stand. Two beams' bottom bars
            // through each other at a joint (16 mm, both at z = 50) are an intersection; once crossing_order has
            // moved one beam's bar a diameter up (z = 66, as the plan displaces it) they touch and are not.
            ReviewSetFacts bx = Set(1, 5001, "Structural Framing", 16, P(-2000, 0, 50), P(2000, 0, 50));
            ReviewSetFacts byThrough = Set(2, 5002, "Structural Framing", 16, P(0, -2000, 50), P(0, 2000, 50));
            Assert.Equal(1, Count(Review(null, 50, bx, byThrough), "intersections", "set_pairs"));
            ReviewSetFacts byDisplaced = Set(2, 5002, "Structural Framing", 16, P(0, -2000, 66), P(0, 2000, 66));
            Assert.Equal("clean", (string)Review(null, 50, bx, byDisplaced)["verdict"]);
            // Displaced only part of the way, they still pass through each other - and are counted.
            ReviewSetFacts byShort = Set(2, 5002, "Structural Framing", 16, P(0, -2000, 58), P(0, 2000, 58));
            Assert.Equal(1, Count(Review(null, 50, bx, byShort), "intersections", "set_pairs"));
            // A crossing clearance declared to the review judges the contact the displacement left.
            string err;
            StructuralClearances c = RebarSteelReview.ReadClearances(JObject.Parse("{\"crossing_mm\": 5}"), out err);
            Assert.Equal(1, Count(Review(c, 50, bx, byDisplaced), "closer_than_declared_clearance", "set_pairs"));
        }

        [Fact]
        public void In_the_audit_a_bar_no_rule_built_is_compared_for_duplicates_only()
        {
            // The audit's own set (a rule's bars) and two hand-modelled sets in the same host: one is the same bar,
            // the other crosses it. The first is duplicate_bar; the crossing is not judged against unattributed steel.
            OverlapMember rule = RebarSteelReview.Member(Set(1, 7, "Walls", 16, P(0, 0, 0), P(3000, 0, 0)));
            rule.RuleId = "W1"; rule.Layout = null;
            OverlapMember twin = RebarSteelReview.Member(Set(2, 7, "Walls", 16, P(0, 0, 0), P(3000, 0, 0)));
            twin.DuplicateOnly = true;
            OverlapMember cross = RebarSteelReview.Member(Set(3, 7, "Walls", 16, P(1000, -300, 0), P(1000, 300, 0)));
            cross.DuplicateOnly = true;
            OverlapReport r = RebarOverlap.Check(new List<OverlapMember> { rule, twin, cross }, new StructuralClearances(), Tol);
            OverlapPair p = Assert.Single(r.Pairs);
            Assert.Equal(RebarOverlap.KindDuplicate, p.Kind);
            Assert.Equal(new long[] { 1, 2 }, new[] { p.A.RebarId, p.B.RebarId });
            Assert.Equal(2, (int)r.ToJson()["duplicate_only_sets"]);
            JObject f = Assert.Single(RebarOverlap.AuditFindings(r).OfType<JObject>());
            Assert.Equal(RebarFinding.DuplicateBar, (string)f["code"]);
            Assert.Equal("W1", (string)f["rule_id"]);
            Assert.Contains("no rule of this set built", (string)f["why"]);
        }

        [Fact]
        public void Twenty_thousand_bars_of_a_building_with_no_requirement_set_are_reviewed_by_the_broad_phase()
        {
            // Ten storeys: a 30 x 30 m slab with four mat layers at 150 (one set each, crossing in contact) and
            // 7 x 7 columns with eight 20 mm bars and twenty 10 mm hoops each, the hoops laid against the bars.
            var sets = new List<ReviewSetFacts>();
            long id = 1;
            for (int storey = 0; storey < 10; storey++)
            {
                double z0 = storey * 3200.0;
                double[] depth = { z0 + 3000 + 40 + 6, z0 + 3000 + 40 + 18, z0 + 3200 - 40 - 6, z0 + 3200 - 40 - 18 };
                for (int l = 0; l < 4; l++)
                {
                    bool alongX = l % 2 == 0;
                    sets.Add(Set(id++, 1000 + storey, "Floors", 12, alongX ? P(0, 75, depth[l]) : P(75, 0, depth[l]),
                                 alongX ? P(30000, 75, depth[l]) : P(75, 30000, depth[l]), Along(alongX ? P(0, 150, 0) : P(150, 0, 0), 200)));
                }
                for (int cx = 0; cx < 7; cx++)
                    for (int cy = 0; cy < 7; cy++)
                    {
                        long host = 100000 + storey * 100 + cx * 10 + cy;
                        double ox = 2000 + cx * 4300, oy = 2000 + cy * 4300;
                        foreach (double v in new[] { -140.0, 140.0 })
                            sets.Add(Set(id++, host, "Structural Columns", 20, P(ox + v, oy - 140, z0), P(ox + v, oy - 140, z0 + 3000), Along(P(0, 140, 0), 3)));
                        sets.Add(Set(id++, host, "Structural Columns", 20, P(ox, oy - 140, z0), P(ox, oy - 140, z0 + 3000), Along(P(0, 280, 0), 2)));
                        var hoop = new ReviewSetFacts { Id = id++, HostId = host, HostCategory = "Structural Columns", ModelDiameterMm = 10, Style = "stirrup_tie" };
                        for (int i = 0; i < 20; i++)
                        {
                            double z = z0 + 50 + 150 * i;
                            hoop.Bars.Add(new OverlapBar
                            {
                                Position = i,
                                Points = new List<double[]> { P(ox - 155, oy - 155, z), P(ox + 155, oy - 155, z), P(ox + 155, oy + 155, z), P(ox - 155, oy + 155, z), P(ox - 155, oy - 155, z) }
                            });
                        }
                        sets.Add(hoop);
                    }
            }
            // PLANTED: storey 3's first mat layer laid twice; one column's bars of storey 5 twice; a stray bar through
            // storey 0's slab (four layers).
            ReviewSetFacts mat3 = sets.First(s => s.HostId == 1003);
            sets.Add(new ReviewSetFacts { Id = id++, HostId = 1003, HostCategory = "Floors", ModelDiameterMm = 12, Bars = mat3.Bars, Style = "standard" });
            ReviewSetFacts col5 = sets.First(s => s.HostId == 100500 && s.ModelDiameterMm == 20);
            sets.Add(new ReviewSetFacts { Id = id++, HostId = 100500, HostCategory = "Structural Columns", ModelDiameterMm = 20, Bars = col5.Bars, Style = "standard" });
            sets.Add(Set(id++, 1, "Walls", 16, P(9975, 9975, 2900), P(9975, 9975, 3300)));

            int bars = sets.Sum(s => s.Bars.Count);
            Assert.True(bars >= 20000, bars + " bars");
            var sw = Stopwatch.StartNew();
            JObject r = Review(null, 50, sets.ToArray());
            sw.Stop();
            _out.WriteLine(bars + " bars in " + sets.Count + " sets: " + (string)r["broad_phase"].ToString(Newtonsoft.Json.Formatting.None) +
                           " in " + sw.ElapsedMilliseconds + " ms");
            Assert.True(sw.Elapsed.TotalSeconds < 60, sw.ElapsedMilliseconds + " ms");
            Assert.Equal(2, Count(r, "duplicates", "set_pairs"));
            Assert.Equal(200 + 3, Count(r, "duplicates", "bar_pairs"));
            Assert.Equal(4, Count(r, "intersections", "set_pairs"));
            Assert.Equal(4, Count(r, "intersections", "bar_pairs"));
            Assert.Equal("findings", (string)r["verdict"]);
        }
    }
}
