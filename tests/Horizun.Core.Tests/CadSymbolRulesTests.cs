// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE BOUND IS THE WHOLE DESIGN.
//
// Grouping by connectivity is right — a symbol is line work a draughtsman drew
// touching, and a radius groups whatever happens to be near. But connectivity does
// not know when to stop: a receptacle whose circle touches its home run is
// connected to the circuit, the circuit to the panel, the panel to the riser, and
// the first "symbol" found is the entire drawing.
//
// So every case here is about the edge of a group: what stops it, what is thrown
// out when it will not stop, and what happens to the geometry that was never a
// symbol in the first place. The recognition on top of it is `CadUnitRules`, which
// has its own tests; nothing about that reasoning changes at this scale.
//
// NOT RUN in this phase. Written to be run when running tests is authorised.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadSymbolRulesTests
    {
        private const string Layer = "E-POWR-SYMB";

        private static CadSegment Seg(double x1, double y1, double x2, double y2, string layer = Layer) =>
            new CadSegment(new CadPoint(x1, y1), new CadPoint(x2, y2), layer);

        /// <summary>A four-segment mark, 40 mm across, drawn closed so it is connected.</summary>
        private static IEnumerable<CadSegment> Mark(double x, double y)
        {
            yield return Seg(x, y, x + 40, y);
            yield return Seg(x + 40, y, x + 40, y + 40);
            yield return Seg(x + 40, y + 40, x, y + 40);
            yield return Seg(x, y + 40, x, y);
        }

        [Fact]
        public void Marks_drawn_apart_are_separate_groups()
        {
            var segments = new List<CadSegment>();
            segments.AddRange(Mark(0, 0));
            segments.AddRange(Mark(1000, 0));
            segments.AddRange(Mark(2000, 0));

            CadSymbolGrouping g = CadSymbolRules.Group(segments, 0.5, 100);
            Assert.Equal(3, g.Groups.Count);
            Assert.Empty(g.Rejected);
            Assert.All(g.Groups, x => Assert.Equal(4, x.Contents.Count));
        }

        [Fact]
        public void A_group_that_outgrows_its_footprint_is_rejected_whole_and_named()
        {
            // THE CASE THE BOUND EXISTS FOR. A mark whose corner touches a home run
            // is connected to the wiring; without the bound the walk takes the
            // circuit, the panel and the riser with it.
            var segments = new List<CadSegment>();
            segments.AddRange(Mark(0, 0));
            segments.Add(Seg(40, 40, 5000, 40));       // the home run, touching the mark

            CadSymbolGrouping g = CadSymbolRules.Group(segments, 0.5, 100);

            Assert.Empty(g.Groups);
            CadSymbolRejection rejected = Assert.Single(g.Rejected);
            Assert.Equal("larger_than_the_declared_footprint", rejected.Reason);

            // Rejected WHOLE, not truncated: the reported box is the box of the
            // thing being rejected, measured after the walk drained.
            Assert.Equal(5, rejected.SegmentCount);
            Assert.True(rejected.WidthMm >= 5000);
        }

        [Fact]
        public void A_rejected_component_is_not_walked_again_from_its_own_segments()
        {
            // Every member of an oversized component must be marked seen while the
            // queue drains, or the next segment of the same component starts a
            // second walk and the drawing is reported with one rejection per line.
            var segments = new List<CadSegment>();
            for (int i = 0; i < 20; i++) segments.Add(Seg(i * 500, 0, (i + 1) * 500, 0));

            CadSymbolGrouping g = CadSymbolRules.Group(segments, 0.5, 100);
            Assert.Single(g.Rejected);
            Assert.Empty(g.Groups);
        }

        [Fact]
        public void A_single_stray_line_is_rejected_rather_than_matched_against_every_other_one()
        {
            var segments = new List<CadSegment> { Seg(0, 0, 30, 0), Seg(500, 0, 530, 0) };

            CadSymbolGrouping g = CadSymbolRules.Group(segments, 0.5, 100);
            Assert.Empty(g.Groups);
            Assert.Equal(2, g.Rejected.Count);
            Assert.All(g.Rejected, x => Assert.Equal("fewer_segments_than_a_symbol_needs", x.Reason));
        }

        [Fact]
        public void A_footprint_of_zero_groups_nothing_rather_than_everything()
        {
            var segments = new List<CadSegment>(Mark(0, 0));
            CadSymbolGrouping g = CadSymbolRules.Group(segments, 0.5, 0);
            Assert.Empty(g.Groups);
            Assert.Empty(g.Rejected);
        }

        [Fact]
        public void Every_group_gets_a_distinct_id()
        {
            // The id is the group's PLACE so that two runs over one drawing agree
            // about which group is which. Two groups that quantize to one key must
            // still be told apart - an id that names two things names neither -
            // and this asserts the invariant rather than trying to force the
            // collision, which needs a contrived pair of coincident marks.
            var segments = new List<CadSegment>();
            segments.AddRange(Mark(0, 0));
            segments.AddRange(Mark(0.2, 0.2));      // same centre at a 50 mm snap grid

            CadSymbolGrouping g = CadSymbolRules.Group(segments, 0.05, 100);
            Assert.Equal(g.Groups.Count, g.Groups.Select(x => x.Id).Distinct().Count());
        }

        [Fact]
        public void Identical_marks_become_one_symbol_type_with_one_occurrence_each()
        {
            var segments = new List<CadSegment>();
            for (int i = 0; i < 5; i++) segments.AddRange(Mark(i * 1000, 0));

            CadSymbolReading reading = CadSymbolRules.Read(segments, 0.5, 100, 0.5);

            Assert.Single(reading.Recognition.Types);
            Assert.Equal(5, reading.Recognition.Occurrences.Count);
            Assert.All(reading.Recognition.Occurrences, o => Assert.True(o.Exact));
            Assert.Empty(reading.Recognition.UnrecognisedRegions);
        }

        [Fact]
        public void A_symbol_type_is_never_named()
        {
            // What a symbol means lives in the legend, which is text, and whether
            // text is reachable is a property of the reader. Naming the type is one
            // sentence from a person - and it then applies to every occurrence.
            var segments = new List<CadSegment>();
            segments.AddRange(Mark(0, 0));
            segments.AddRange(Mark(1000, 0));

            CadSymbolReading reading = CadSymbolRules.Read(segments, 0.5, 100, 0.5);
            Assert.All(reading.Recognition.Types, t => Assert.Null(t.Name));
            Assert.Contains("legend", reading.ToJson().Value<string>("means"));
        }

        [Fact]
        public void A_mark_drawn_differently_the_second_time_is_unrecognised_not_folded_in()
        {
            // The interesting finding on a real drawing: a symbol somebody redrew.
            var segments = new List<CadSegment>();
            segments.AddRange(Mark(0, 0));
            segments.AddRange(Mark(1000, 0));
            segments.AddRange(Mark(2000, 0));
            // A triangle: three segments, so a different signature from the outset.
            segments.Add(Seg(3000, 0, 3040, 0));
            segments.Add(Seg(3040, 0, 3020, 35));
            segments.Add(Seg(3020, 35, 3000, 0));

            CadSymbolReading reading = CadSymbolRules.Read(segments, 0.5, 100, 0.5);

            Assert.Single(reading.Recognition.Types);
            Assert.Equal(3, reading.Recognition.Occurrences.Count);
            Assert.Single(reading.Recognition.UnrecognisedRegions);
        }

        [Fact]
        public void Each_occurrence_comes_out_as_a_typed_creation_row_at_the_symbols_centre()
        {
            // If the reply were a list of types and a separate list of groups,
            // somebody would still build four hundred placement rows by hand -
            // which is the work the grouping was supposed to save.
            var segments = new List<CadSegment>();
            segments.AddRange(Mark(0, 0));          // centre (20, 20)
            segments.AddRange(Mark(1000, 0));       // centre (1020, 20)

            CadSymbolReading reading = CadSymbolRules.Read(segments, 0.5, 100, 0.5);
            JArray placements = reading.Placements();

            Assert.Equal(2, placements.Count);
            foreach (JToken t in placements)
            {
                var row = (JObject)t;
                Assert.True(row.Value<bool>("placeable"));
                var inner = (JObject)row["row"];
                Assert.Equal("family_instance", inner.Value<string>("kind"));
                Assert.Equal("absolute", inner.Value<string>("coordinate_mode"));
                // The TYPE is what a person supplies, and it is left empty rather
                // than guessed from a layer name.
                Assert.Equal("", inner.Value<string>("type_name"));
            }

            var points = placements.Select(t => ((JObject)t["row"])["point"] as JArray)
                                   .Select(a => a[0].Value<double>())
                                   .OrderBy(x => x).ToList();
            Assert.Equal(20, points[0], 3);
            Assert.Equal(1020, points[1], 3);
        }

        [Fact]
        public void A_mirrored_occurrence_is_reported_as_not_placeable()
        {
            // A mirror is not a rotation of a family instance: it needs a flipped
            // instance or a different family, and placing it rotated puts the
            // symbol's face the wrong way round - identical on the drawing, not
            // identical in the model.
            var segments = new List<CadSegment>();
            // An L, so the shape is chiral and only a mirror fits the second one.
            foreach (bool mirrored in new[] { false, true })
            {
                double x = mirrored ? 1000 : 0;
                double s2 = mirrored ? -1 : 1;
                segments.Add(Seg(x, 0, x + s2 * 40, 0));
                segments.Add(Seg(x + s2 * 40, 0, x + s2 * 40, 15));
                segments.Add(Seg(x + s2 * 40, 15, x + s2 * 15, 15));
                segments.Add(Seg(x + s2 * 15, 15, x + s2 * 15, 40));
                segments.Add(Seg(x + s2 * 15, 40, x, 40));
                segments.Add(Seg(x, 40, x, 0));
            }

            CadSymbolReading reading = CadSymbolRules.Read(segments, 0.5, 100, 0.5);
            Assert.Single(reading.Recognition.Types);
            Assert.Equal(2, reading.Recognition.Occurrences.Count);

            JArray placements = reading.Placements();
            Assert.Contains(placements, t => !((JObject)t).Value<bool>("placeable"));
            JObject notPlaceable = placements.OfType<JObject>().First(x => !x.Value<bool>("placeable"));
            Assert.Contains("wrong way round", notPlaceable.Value<string>("why_not"));
        }

        [Fact]
        public void A_mark_that_is_on_the_legend_can_be_named_from_one_entry()
        {
            // THE POINT OF THE CROSS-REFERENCE. What a symbol means is text, which
            // this reader cannot see - but a legend draws each symbol once beside
            // the words that name it. Reading both together turns "name four
            // hundred marks" into "read one legend entry".
            var drawing = new List<CadSegment>();
            for (int i = 0; i < 4; i++) drawing.AddRange(Mark(i * 1000, 0));

            var legend = new List<CadSegment>(Mark(50000, 50000));

            CadSymbolReading reading =
                CadSymbolRules.ReadAgainstLegend(drawing, legend, 0.5, 100, 0.5);

            JObject cross = reading.LegendCrossReference();
            Assert.True(cross.Value<bool>("read"));
            Assert.Equal(1, cross.Value<int>("types_in_both"));
            Assert.Equal(0, cross.Value<int>("types_only_in_the_drawing"));

            JObject row = ((JArray)cross["types"]).OfType<JObject>().Single();
            Assert.Equal(1, row.Value<int>("in_legend"));
            Assert.Equal(4, row.Value<int>("in_drawing"));
            Assert.True(row.Value<bool>("nameable_from_the_legend"));
        }

        [Fact]
        public void A_mark_the_legend_does_not_define_is_the_finding()
        {
            var drawing = new List<CadSegment>();
            drawing.AddRange(Mark(0, 0));
            // A triangle the legend never shows.
            drawing.Add(Seg(1000, 0, 1040, 0));
            drawing.Add(Seg(1040, 0, 1020, 35));
            drawing.Add(Seg(1020, 35, 1000, 0));

            var legend = new List<CadSegment>(Mark(50000, 50000));

            CadSymbolReading reading =
                CadSymbolRules.ReadAgainstLegend(drawing, legend, 0.5, 100, 0.5);

            JObject cross = reading.LegendCrossReference();
            Assert.Equal(1, cross.Value<int>("types_in_both"));
            Assert.Equal(1, cross.Value<int>("types_only_in_the_drawing"));
            Assert.Contains("no legend occurrence", cross.Value<string>("means"));
        }

        [Fact]
        public void A_legend_at_a_different_scale_matches_nothing_and_the_reply_says_which_case_that_is()
        {
            // Recognition is RIGID, not similarity. A legend drawn at half scale
            // is not a near miss - it is a complete miss, and a reply that only
            // said "3 types undefined" would send somebody looking for three
            // missing legend entries that are all there.
            var drawing = new List<CadSegment>();
            for (int i = 0; i < 3; i++) drawing.AddRange(Mark(i * 1000, 0));

            var legend = new List<CadSegment>();
            // The same mark at half the size.
            legend.Add(Seg(50000, 50000, 50020, 50000));
            legend.Add(Seg(50020, 50000, 50020, 50020));
            legend.Add(Seg(50020, 50020, 50000, 50020));
            legend.Add(Seg(50000, 50020, 50000, 50000));

            CadSymbolReading reading =
                CadSymbolRules.ReadAgainstLegend(drawing, legend, 0.5, 100, 0.5);

            JObject cross = reading.LegendCrossReference();
            Assert.Equal(0, cross.Value<int>("types_in_both"));
            Assert.Equal(1, cross.Value<int>("types_only_in_the_legend"));
            Assert.Contains("it is the scale", cross.Value<string>("means"));
        }

        [Fact]
        public void A_cross_reference_over_no_symbols_at_all_says_so()
        {
            // A VACUOUS TRUTH IS NOT A RESULT. With every group rejected - a
            // footprint too small, or a layer filter that excluded the symbols -
            // "every type appears on the legend" is true of nothing and reads as
            // complete coverage.
            var drawing = new List<CadSegment>(Mark(0, 0));
            var legend = new List<CadSegment>(Mark(50000, 50000));

            // A footprint of 10 mm rejects a 40 mm mark.
            CadSymbolReading reading =
                CadSymbolRules.ReadAgainstLegend(drawing, legend, 0.5, 10, 0.5);

            JObject cross = reading.LegendCrossReference();
            Assert.True(cross.Value<bool>("read"));
            Assert.Equal(0, cross.Value<int>("types_in_both"));
            Assert.Contains("NO SYMBOL TYPE WAS FOUND AT ALL", cross.Value<string>("means"));
        }

        [Fact]
        public void Without_a_legend_the_cross_reference_says_there_was_none()
        {
            var segments = new List<CadSegment>();
            segments.AddRange(Mark(0, 0));
            segments.AddRange(Mark(1000, 0));

            CadSymbolReading reading = CadSymbolRules.Read(segments, 0.5, 100, 0.5);
            JObject cross = reading.LegendCrossReference();
            Assert.False(cross.Value<bool>("read"));
            Assert.Contains("no legend drawing was given", cross.Value<string>("means"));
        }

        [Fact]
        public void The_grouping_says_when_it_stopped_at_its_bound()
        {
            // A partial grouping that reported a clean count would be read as a
            // complete census of the drawing's symbols.
            var segments = new List<CadSegment>();
            for (int i = 0; i < 30; i++) segments.AddRange(Mark(i * 1000, 0));

            CadSymbolGrouping g = CadSymbolRules.Group(segments, 0.5, 100, 2, maxGroups: 4);
            Assert.True(g.Truncated);
            Assert.True(g.SummaryJson().Value<bool>("truncated"));
            Assert.Contains("PARTIAL", g.SummaryJson().Value<string>("means"));
        }
    }
}
