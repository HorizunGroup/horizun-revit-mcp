// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_selection_context operation=view_contents, the Revit-free half. The field case: a
// coordinate box found "no door, no fixtures" in a bathroom; the door sat 187 mm outside the
// box and the fixtures were inside a group. Known answers are worked by hand (mm / 304.8 = feet).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ViewContentsRulesTests
    {
        private const double Mm = 1 / 304.8;

        private static bool Parse(string json, out ViewContentsRequest r, out string e) =>
            ViewContentsRules.TryParse(JObject.Parse(json), out r, out e);

        // a bathroom 3000 x 2000 mm, with a 400 x 400 hole (a shaft) at 1000..1400
        private static RegionSpec Bathroom(bool withShaft = false)
        {
            var region = new RegionSpec { Source = "room" };
            region.Loops.Add(new List<double[]> { new[] { 0.0, 0.0 }, new[] { 3000 * Mm, 0.0 }, new[] { 3000 * Mm, 2000 * Mm }, new[] { 0.0, 2000 * Mm } });
            if (withShaft)
                region.Loops.Add(new List<double[]> { new[] { 1000 * Mm, 800 * Mm }, new[] { 1400 * Mm, 800 * Mm }, new[] { 1400 * Mm, 1200 * Mm }, new[] { 1000 * Mm, 1200 * Mm } });
            return region;
        }

        private static double[] P(double xMm, double yMm, double zMm) => new[] { xMm * Mm, yMm * Mm, zMm * Mm };

        // ------------------------------------------------------------ the request

        [Fact]
        public void Defaults_are_20_rows_a_200_mm_tolerance_and_links_included()
        {
            Assert.True(Parse("{}", out ViewContentsRequest r, out string e), e);
            Assert.Equal(20, r.MaxRows);
            Assert.Equal(200, r.ToleranceMm);
            Assert.True(r.IncludeLinks);
            Assert.Null(r.ViewId);
            Assert.Null(r.Region);
        }

        [Theory]
        [InlineData("{\"max_rows\":201}")]
        [InlineData("{\"max_rows\":-1}")]
        [InlineData("{\"max_rows\":\"20\"}")]
        [InlineData("{\"view_id\":\"12\"}")]
        [InlineData("{\"categories\":\"Doors\"}")]
        [InlineData("{\"categories\":[\"\"]}")]
        [InlineData("{\"tolerance_mm\":-1}")]
        [InlineData("{\"tolerance_mm\":99999}")]
        [InlineData("{\"include_links\":\"yes\"}")]
        [InlineData("{\"room_id\":5,\"region\":{\"points\":[[0,0],[1,0],[1,1]]}}")]
        [InlineData("{\"region\":{\"points\":[[0,0],[1,0]]}}")]
        [InlineData("{\"region\":{\"points\":[[0,0],[1,0],[1,\"a\"]]}}")]
        [InlineData("{\"region\":{\"points\":[[0,0],[1,0],[1,1]],\"z_min\":5,\"z_max\":1}}")]
        [InlineData("{\"region\":{\"points\":[[0,0],[1,0],[1,1]],\"box\":1}}")]
        [InlineData("{\"unit\":\"cm\"}")]
        public void A_request_that_cannot_be_meant_is_refused_with_its_reason(string json)
        {
            Assert.False(Parse(json, out ViewContentsRequest r, out string e));
            Assert.Null(r);
            Assert.False(string.IsNullOrWhiteSpace(e));
        }

        [Fact]
        public void A_region_is_read_in_the_unit_of_the_request_and_kept_in_feet()
        {
            Assert.True(Parse("{\"unit\":\"m\",\"region\":{\"points\":[[0,0],[3,0],[3,2],[0,2]],\"z_min\":0,\"z_max\":2.5}}", out ViewContentsRequest r, out string e), e);
            Assert.Equal("region", r.Region.Source);
            Assert.Equal(3000 * Mm, r.Region.Loops[0][1][0], 9);
            Assert.Equal(2500 * Mm, r.Region.ZMaxFeet.Value, 9);
        }

        [Fact]
        public void Category_names_are_deduplicated_ignoring_case()
        {
            Assert.True(Parse("{\"categories\":[\"Doors\",\"doors\",\"OST_Walls\"]}", out ViewContentsRequest r, out string e), e);
            Assert.Equal(new[] { "Doors", "OST_Walls" }, r.Categories.ToArray());
        }

        // ------------------------------------------------------------ the category match

        [Fact]
        public void A_category_matches_by_its_OST_name_or_display_name_exactly_never_by_substring()
        {
            var want = new List<string> { "OST_Doors", "Plumbing Fixtures" };
            Assert.True(ViewContentsRules.CategoryMatches(want, "OST_Doors", "Doors"));
            Assert.True(ViewContentsRules.CategoryMatches(want, "OST_PlumbingFixtures", "plumbing fixtures"));
            Assert.False(ViewContentsRules.CategoryMatches(want, "OST_DoorTags", "Door Tags"));
            Assert.False(ViewContentsRules.CategoryMatches(want, null, "Doors2"));
            Assert.True(ViewContentsRules.CategoryMatches(new List<string>(), "OST_Walls", "Walls"));
        }

        [Fact]
        public void A_requested_name_that_exists_nowhere_is_reported_not_silently_empty()
        {
            var known = new[] { new KeyValuePair<string, string>("OST_Doors", "Doors"), new KeyValuePair<string, string>("OST_Walls", "Walls") };
            Assert.Equal(new[] { "Puertas" }, ViewContentsRules.UnmatchedRequests(new List<string> { "doors", "Puertas" }, known).ToArray());
        }

        // ------------------------------------------------------------ groups and links

        [Fact]
        public void Group_members_the_collector_already_returned_are_not_added_twice()
        {
            var seen = new HashSet<long> { 10, 11 };
            Assert.Equal(new long[] { 12, 13 }, ViewContentsRules.MembersNotYetSeen(seen, new long[] { 10, 12, 12, 13 }).ToArray());
            Assert.Equal(2, seen.Count);       // the set is read, never filled or copied
            Assert.Empty(ViewContentsRules.MembersNotYetSeen(new HashSet<long> { 1 }, new long[] { 1 }));
        }

        [Fact]
        public void A_link_that_cannot_be_read_says_why_and_is_never_a_zero()
        {
            LinkViewState unloaded = ViewContentsRules.LinkState(7, "A", documentLoaded: false, hiddenInView: false, categoryHiddenInView: false, supportsLinkCollector: true, visibility: null);
            Assert.Equal("unloaded", unloaded.Status);
            LinkViewState hidden = ViewContentsRules.LinkState(7, "A", true, true, false, true, "ByHostView");
            Assert.Equal("hidden_in_view", hidden.Status);
            Assert.Contains("hidden", hidden.Reason);
            LinkViewState allHidden = ViewContentsRules.LinkState(7, "A", true, false, true, true, null);
            Assert.Equal("hidden_in_view", allHidden.Status);
            Assert.Contains("Revit Links category", allHidden.Reason);
            LinkViewState old = ViewContentsRules.LinkState(7, "A", true, false, false, supportsLinkCollector: false, visibility: null);
            Assert.Equal("unknown", old.Status);
            Assert.Contains("2024", old.Reason);
            Assert.Equal("read", ViewContentsRules.LinkState(7, "A", true, false, false, true, "ByHostView").Status);
        }

        // ------------------------------------------------------------ the region test (real outline, not a box)

        [Fact]
        public void A_door_187_mm_outside_the_room_is_near_with_its_distance_not_lost()
        {
            // the field case: a box around the room's own extent missed it
            RegionRelation r = ViewContentsRules.Classify(Bathroom(), P(3187, 800, 0), P(3287, 1700, 2100), 200 * Mm, out double? d);
            Assert.Equal(RegionRelation.Near, r);
            Assert.Equal(187 * Mm, d.Value, 9);
        }

        [Fact]
        public void The_same_door_is_outside_when_the_tolerance_is_smaller()
        {
            Assert.Equal(RegionRelation.Outside, ViewContentsRules.Classify(Bathroom(), P(3187, 800, 0), P(3287, 1700, 2100), 100 * Mm, out double? d));
            Assert.Equal(187 * Mm, d.Value, 9);
        }

        [Fact]
        public void A_box_that_overlaps_the_outline_is_inside_whatever_its_centre()
        {
            // a wall-hosted door whose box straddles the boundary
            Assert.Equal(RegionRelation.Inside, ViewContentsRules.Classify(Bathroom(), P(2900, 800, 0), P(3300, 1700, 2100), 0, out double? d));
            Assert.Equal(0, d.Value);
            // a box bigger than the room, containing all of it
            Assert.Equal(RegionRelation.Inside, ViewContentsRules.Classify(Bathroom(), P(-500, -500, 0), P(4000, 3000, 100), 0, out _));
            // a thin box that crosses the room with no corner and no centre inside the outline's interior test
            Assert.Equal(RegionRelation.Inside, ViewContentsRules.Classify(Bathroom(), P(-1000, 900, 0), P(4000, 1000, 100), 0, out _));
        }

        [Fact]
        public void A_far_element_is_outside()
        {
            Assert.Equal(RegionRelation.Outside, ViewContentsRules.Classify(Bathroom(), P(9000, 9000, 0), P(9100, 9100, 100), 200 * Mm, out _));
        }

        [Fact]
        public void An_element_inside_a_hole_of_the_outline_is_not_inside_the_room()
        {
            // even-odd: a fixture fully inside the 400 x 400 shaft is outside the room
            RegionRelation r = ViewContentsRules.Classify(Bathroom(withShaft: true), P(1150, 950, 0), P(1250, 1050, 100), 0, out double? d);
            Assert.Equal(RegionRelation.Outside, r);
            Assert.True(d.Value > 0);
        }

        [Fact]
        public void The_height_range_counts_and_the_distance_combines_both_directions()
        {
            RegionSpec room = Bathroom();
            room.ZMinFeet = 0; room.ZMaxFeet = 2700 * Mm;
            Assert.Equal(RegionRelation.Outside, ViewContentsRules.Classify(room, P(500, 500, 3100), P(600, 600, 3200), 200 * Mm, out double? up));
            Assert.Equal(400 * Mm, up.Value, 9);
            Assert.Equal(RegionRelation.Near, ViewContentsRules.Classify(room, P(500, 500, 2800), P(600, 600, 2900), 200 * Mm, out double? close));
            Assert.Equal(100 * Mm, close.Value, 9);
        }

        [Fact]
        public void No_box_or_a_broken_box_is_unknown_never_outside()
        {
            Assert.Equal(RegionRelation.Unknown, ViewContentsRules.Classify(Bathroom(), null, null, 200 * Mm, out _));
            Assert.Equal(RegionRelation.Unknown, ViewContentsRules.Classify(Bathroom(), new[] { double.NaN, 0, 0 }, P(1, 1, 1), 200 * Mm, out _));
            Assert.Equal(RegionRelation.NotTested, ViewContentsRules.Classify(null, P(0, 0, 0), P(1, 1, 1), 0, out _));
        }

        // ------------------------------------------------------------ the aggregation

        private static ContentItem Item(string cat, long id, string kind = "host", long? link = null, long? group = null, bool walk = false,
                                        RegionRelation rel = RegionRelation.NotTested, double? dist = null) =>
            new ContentItem { CategoryName = cat, CategoryKey = "OST_" + cat, Id = id, SourceKind = kind, LinkInstanceId = link, GroupId = group, ViaGroupWalk = walk, Region = rel, DistanceFeet = dist };

        private static JObject Describe(ContentItem i) => new JObject { ["element_id"] = i.Id, ["unique_id"] = "u" + i.Id };

        [Fact]
        public void Counts_are_exact_per_category_and_the_sample_is_capped_with_rows_total()
        {
            var items = Enumerable.Range(1, 30).Select(i => Item("Doors", i)).Concat(new[] { Item("Walls", 100), Item("Walls", 101, "link", 7) }).ToList();
            ViewContentsRules.Aggregation a = ViewContentsRules.Aggregate(items, 5, false, 304.8, Describe);
            Assert.Equal(32, a.ElementsTotal);
            JObject doors = (JObject)a.Categories.Single(c => (string)c["category"] == "Doors");
            Assert.Equal(30, (int)doors["total"]);
            Assert.Equal(30, (int)doors["rows_total"]);
            Assert.Equal(5, (int)doors["rows_returned"]);
            Assert.True((bool)doors["truncated"]);
            JObject walls = (JObject)a.Categories.Single(c => (string)c["category"] == "Walls");
            Assert.Equal(1, (int)walls["host_total"]);
            Assert.Equal(1, (int)walls["linked_total"]);
            Assert.False((bool)walls["truncated"]);
            // a linked row carries its link instance; a host row does not
            JObject linkedRow = (JObject)walls["rows"].Single(r => (string)r["source_kind"] == "link");
            Assert.Equal(7, (long)linkedRow["link_instance_id"]);
            Assert.Null(((JObject)walls["rows"].Single(r => (string)r["source_kind"] == "host"))["link_instance_id"]);
        }

        [Fact]
        public void Group_members_are_marked_with_their_group_and_counted_apart()
        {
            var items = new[] { Item("PlumbingFixtures", 1), Item("PlumbingFixtures", 2, group: 90), Item("PlumbingFixtures", 3, group: 90, walk: true) };
            ViewContentsRules.Aggregation a = ViewContentsRules.Aggregate(items, 10, false, 304.8, Describe);
            JObject cat = (JObject)a.Categories.Single();
            // the walked member is NOT in any total: the view collector did not return it, so nobody knows it is drawn
            Assert.Equal(2, (int)cat["total"]);
            Assert.Equal(2, (int)cat["host_total"]);
            Assert.Equal(2, a.ElementsTotal);
            Assert.Equal(1, a.GroupWalkTotal);
            Assert.Equal(1, (int)cat["group_member_total"]);
            Assert.Equal(1, (int)cat["group_walk_total"]);
            JObject walked = (JObject)cat["group_walk_rows"].Single();
            Assert.Equal(3, (long)walked["element_id"]);
            Assert.Equal(90, (long)walked["group_id"]);
            Assert.Equal("group_walk", (string)walked["via"]);
            Assert.Equal("unverified", (string)walked["drawn"]);
            Assert.DoesNotContain(cat["rows"], r => (long)r["element_id"] == 3);
            Assert.Equal(90, (long)((JObject)cat["rows"].Single(r => (long)r["element_id"] == 2))["group_id"]);
            Assert.Null(((JObject)cat["rows"].Single(r => (long)r["element_id"] == 1))["group_id"]);
            Assert.Null(((JObject)cat["rows"].Single(r => (long)r["element_id"] == 1))["drawn"]);
        }

        [Fact]
        public void With_a_region_outside_is_counted_apart_and_unknown_is_never_inside_or_outside()
        {
            var items = new[]
            {
                Item("Doors", 1, rel: RegionRelation.Inside, dist: 0),
                Item("Doors", 2, rel: RegionRelation.Near, dist: 187 * Mm),
                Item("Doors", 3, rel: RegionRelation.Outside, dist: 5000 * Mm),
                Item("Doors", 4, rel: RegionRelation.Unknown),
                Item("Tags", 5, rel: RegionRelation.Unknown)
            };
            ViewContentsRules.Aggregation a = ViewContentsRules.Aggregate(items, 10, true, 304.8, Describe);
            Assert.Equal(1, a.ExcludedByRegion);
            Assert.Equal(2, a.RegionUnknown);
            JObject doors = (JObject)a.Categories.Single(c => (string)c["category"] == "Doors");
            Assert.Equal(2, (int)doors["total"]);
            Assert.Equal(1, (int)doors["near_total"]);
            Assert.Equal(1, (int)doors["region_unknown"]);
            Assert.Equal(new[] { 1L, 2L }, doors["rows"].Select(r => (long)r["element_id"]).ToArray());
            JObject near = (JObject)doors["rows"].Single(r => (long)r["element_id"] == 2);
            Assert.Equal("near", (string)near["region"]);
            Assert.Equal(187.0, (double)near["distance_to_region"], 1);
            // a category whose only elements are unknown is still listed (never silently absent)
            JObject tags = (JObject)a.Categories.Single(c => (string)c["category"] == "Tags");
            Assert.Equal(0, (int)tags["total"]);
            Assert.Equal(1, (int)tags["region_unknown"]);
            Assert.Equal(new[] { 4L }, doors["region_unknown_ids"].Select(x => (long)x).ToArray());
            Assert.Equal(new[] { 5L }, tags["region_unknown_ids"].Select(x => (long)x).ToArray());
        }

        [Fact]
        public void The_unknown_sample_has_a_few_ids_never_all_of_them()
        {
            var items = Enumerable.Range(1, 12).Select(i => Item("Doors", i, rel: RegionRelation.Unknown)).ToList();
            ViewContentsRules.Aggregation a = ViewContentsRules.Aggregate(items, 10, true, 304.8, Describe);
            JObject doors = (JObject)a.Categories.Single();
            Assert.Equal(12, (int)doors["region_unknown"]);
            Assert.Equal(ViewContentsRules.MaxUnknownSampleIds, doors["region_unknown_ids"].Count());
        }

        [Fact]
        public void A_walked_member_inside_a_region_stays_out_of_the_totals_and_an_outside_one_is_excluded()
        {
            var items = new[]
            {
                Item("Doors", 1, rel: RegionRelation.Inside, dist: 0),
                Item("Doors", 2, group: 9, walk: true, rel: RegionRelation.Inside, dist: 0),
                Item("Doors", 3, group: 9, walk: true, rel: RegionRelation.Outside, dist: 9),
                Item("Doors", 4, group: 9, walk: true, rel: RegionRelation.Unknown)
            };
            ViewContentsRules.Aggregation a = ViewContentsRules.Aggregate(items, 10, true, 304.8, Describe);
            JObject doors = (JObject)a.Categories.Single();
            Assert.Equal(1, (int)doors["total"]);
            Assert.Equal(1, (int)doors["group_walk_total"]);
            Assert.Equal(1, a.ExcludedByRegion);
            Assert.Equal(1, (int)doors["region_unknown"]);
        }

        [Fact]
        public void The_global_row_cap_stops_the_sample_and_says_so_while_the_totals_stay_exact()
        {
            var items = Enumerable.Range(1, 10).Select(i => Item("Doors", i)).Concat(Enumerable.Range(11, 10).Select(i => Item("Walls", i))).ToList();
            ViewContentsRules.Aggregation a = ViewContentsRules.Aggregate(items, 10, false, 304.8, Describe, maxTotalRows: 12);
            Assert.True(a.RowsCapped);
            Assert.Equal(20, a.ElementsTotal);
            Assert.Equal(12, a.Categories.Sum(c => (int)c["rows_returned"]));
            Assert.All(a.Categories, c => Assert.Equal(10, (int)c["total"]));
            Assert.Contains(a.Categories, c => (bool)c["truncated"]);
            Assert.False(ViewContentsRules.Aggregate(items, 10, false, 304.8, Describe).RowsCapped);
        }

        [Fact]
        public void A_row_that_cannot_be_described_keeps_its_id_and_says_why()
        {
            ViewContentsRules.Aggregation a = ViewContentsRules.Aggregate(new[] { Item("Doors", 1) }, 10, false, 304.8, i => throw new InvalidOperationException("gone"));
            JObject row = (JObject)((JObject)a.Categories.Single())["rows"].Single();
            Assert.Equal(1, (long)row["element_id"]);
            Assert.Equal("gone", (string)row["read_error"]);
        }

        [Fact]
        public void Zero_rows_still_gives_the_totals()
        {
            ViewContentsRules.Aggregation a = ViewContentsRules.Aggregate(new[] { Item("Doors", 1), Item("Doors", 2) }, 0, false, 304.8, Describe);
            JObject doors = (JObject)a.Categories.Single();
            Assert.Equal(2, (int)doors["total"]);
            Assert.Equal(0, (int)doors["rows_returned"]);
        }

        // ------------------------------------------------------------ limits: the edges that pass and the first that does not

        [Fact]
        public void Category_and_point_limits_accept_the_edge_and_refuse_the_next()
        {
            string Cats(int n) => "{\"categories\":[" + string.Join(",", Enumerable.Range(1, n).Select(i => "\"C" + i + "\"")) + "]}";
            Assert.True(Parse(Cats(ViewContentsRules.MaxCategories), out _, out string e1), e1);
            Assert.False(Parse(Cats(ViewContentsRules.MaxCategories + 1), out _, out string e2));
            Assert.Contains("categories", e2);

            string Pts(int n) => "{\"region\":{\"points\":[" + string.Join(",", Enumerable.Range(1, n).Select(i => "[" + i + "," + (i % 7) + "]")) + "]}}";
            Assert.True(Parse(Pts(ViewContentsRules.MaxRegionPoints), out _, out string e3), e3);
            Assert.False(Parse(Pts(ViewContentsRules.MaxRegionPoints + 1), out _, out string e4));
            Assert.Contains("region.points", e4);
            Assert.True(Parse(Pts(3), out _, out string e5), e5);
            Assert.False(Parse(Pts(2), out _, out _));

            Assert.True(Parse("{\"max_rows\":0}", out _, out _));
            Assert.True(Parse("{\"max_rows\":200}", out _, out _));
            Assert.False(Parse("{\"max_rows\":201}", out _, out _));
            Assert.True(Parse("{\"tolerance_mm\":0}", out _, out _));
            Assert.True(Parse("{\"tolerance_mm\":5000}", out _, out _));
            Assert.False(Parse("{\"tolerance_mm\":5001}", out _, out _));
        }

        [Theory]
        [InlineData("{\"max_rows\":99999999999999999999}", "max_rows")]
        [InlineData("{\"view_id\":99999999999999999999}", "view_id")]
        [InlineData("{\"room_id\":99999999999999999999}", "room_id")]
        public void An_integer_too_big_for_64_bits_is_refused_by_name_not_thrown(string json, string field)
        {
            Assert.False(Parse(json, out _, out string e));
            Assert.Contains(field, e);
        }

        // ------------------------------------------------------------ arguments that would be ignored, in both directions

        [Theory]
        [InlineData("ids")]
        [InlineData("selector")]
        [InlineData("selection")]
        [InlineData("checks")]
        [InlineData("snapshot_name")]
        [InlineData("against")]
        [InlineData("include")]
        [InlineData("radius_mm")]
        [InlineData("parameters")]
        [InlineData("max_faces")]
        public void View_contents_refuses_the_arguments_of_the_other_operations_naming_the_field(string field)
        {
            var request = new JObject { ["operation"] = "view_contents", [field] = new JArray(1) };
            string found = ViewContentsRules.ForeignArgument("view_contents", request);
            Assert.Equal(field, found);
            string message = ViewContentsRules.ForeignArgumentMessage("view_contents", found);
            Assert.Contains("'" + field + "'", message);
            Assert.Contains("view_contents", message);
        }

        [Theory]
        [InlineData("context", "view_id")]
        [InlineData("snapshot", "categories")]
        [InlineData("diff", "max_rows")]
        [InlineData("validate_ids", "room_id")]
        [InlineData("context", "region")]
        [InlineData("validate_ids", "include_links")]
        public void The_old_operations_refuse_the_view_contents_arguments_naming_the_field(string operation, string field)
        {
            var request = new JObject { ["operation"] = operation, [field] = 1 };
            string found = ViewContentsRules.ForeignArgument(operation, request);
            Assert.Equal(field, found);
            string message = ViewContentsRules.ForeignArgumentMessage(operation, found);
            Assert.Contains("'" + field + "'", message);
            Assert.Contains(operation, message);
        }

        [Theory]
        [InlineData("context")]
        [InlineData("snapshot")]
        [InlineData("diff")]
        public void Checks_outside_validate_ids_is_refused_and_inside_it_is_not(string operation)
        {
            var request = new JObject { ["checks"] = new JArray() };
            string found = ViewContentsRules.ForeignArgument(operation, request);
            Assert.Equal("checks", found);
            Assert.Contains("'checks'", ViewContentsRules.ForeignArgumentMessage(operation, found));
            Assert.Contains("validate_ids", ViewContentsRules.ForeignArgumentMessage(operation, found));
            Assert.Null(ViewContentsRules.ForeignArgument("validate_ids", request));
        }

        [Theory]
        [InlineData("ids")]
        [InlineData("selector")]
        [InlineData("selection")]
        [InlineData("snapshot_name")]
        [InlineData("against")]
        [InlineData("include")]
        [InlineData("radius_mm")]
        [InlineData("parameters")]
        [InlineData("max_faces")]
        [InlineData("tolerance_mm")]
        [InlineData("unit")]
        public void Validate_ids_refuses_the_arguments_it_would_ignore_naming_the_field(string field)
        {
            var request = new JObject { ["operation"] = "validate_ids", ["checks"] = new JArray(), [field] = 1 };
            string found = ViewContentsRules.ForeignArgument("validate_ids", request);
            Assert.Equal(field, found);
            string message = ViewContentsRules.ForeignArgumentMessage("validate_ids", found);
            Assert.Contains("'" + field + "'", message);
            Assert.Contains("validate_ids", message);
            Assert.Null(ViewContentsRules.ForeignArgument("validate_ids", JObject.Parse("{\"operation\":\"validate_ids\",\"checks\":[],\"target_document\":\"A\"}")));
        }

        [Fact]
        public void A_clean_request_and_a_null_argument_are_not_foreign()
        {
            Assert.Null(ViewContentsRules.ForeignArgument("view_contents", JObject.Parse("{\"view_id\":5,\"categories\":[\"Doors\"],\"max_rows\":3,\"tolerance_mm\":100,\"unit\":\"m\",\"ids\":null}")));
            Assert.Null(ViewContentsRules.ForeignArgument("context", JObject.Parse("{\"ids\":[1],\"include\":[\"geometry\"],\"view_id\":null}")));
            Assert.Null(ViewContentsRules.ForeignArgument("snapshot", JObject.Parse("{\"snapshot_name\":\"a\",\"selector\":{\"category\":\"Walls\"}}")));
        }

        [Fact]
        public void The_operation_is_listed_with_the_others()
        {
            Assert.Contains("view_contents", SelectionContextOps.All);
        }
    }
}
