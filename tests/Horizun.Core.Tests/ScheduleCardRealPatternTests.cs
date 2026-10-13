// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// L10 AGAINST REALITY (stage 1c): the drawing patterns a reading of real schedule
// sheets turned up, each reproduced on a SYNTHETIC sheet with the shape it has on
// paper - nothing here is copied from a real sheet; marks, numbers and words are
// invented. Every test was red on the release-3.0.0 reader. Each pattern is read
// only when it is unambiguous; the same fixture with one thing changed shows the
// refusal or the failed check that keeps it honest.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleCardRealPatternTests
    {
        private const double H = ScheduleCardFixtures.H;

        private static JObject Read(SyntheticSheet sheet, JObject args = null)
        {
            JObject a = args ?? ScheduleCardFixtures.Args();
            ScheduleCards.Options o = ScheduleCards.ReadOptions(a, out string code, out string error);
            Assert.True(o != null, code + ": " + error);
            ScheduleCards.Sheet s = ScheduleCards.SheetFromPrimitives(sheet.ToJson(), out code, out error);
            Assert.True(s != null, code + ": " + error);
            return ScheduleCards.Read(new[] { s }, o);
        }

        private static JObject Card(JObject reply, string title) =>
            (JObject)reply["cards"].Single(c => (string)c["title"] == title);

        private static IEnumerable<JObject> Refusals(JObject reply, string code) =>
            reply["refusals"].Cast<JObject>().Where(r => (string)r["code"] == code);

        private static IEnumerable<JObject> Checks(JObject reply, string check) =>
            reply["checks"].Cast<JObject>().Where(c => (string)c["check"] == check);

        private static JObject Args(JObject extra) => ScheduleCardFixtures.Args(extra);

        // ----------------------------------------------------------------- hooks

        /// <summary>A bottom bar whose ends bend down in chords shorter than the end-snap window.</summary>
        private static SyntheticSheet ChordHooks(Action<SyntheticSheet, BeamCardSpec> stray = null) => ScheduleCardFixtures.BeamSheet(c =>
        {
            c.BottomHooks = false;
            c.BottomCallout = "3#4 L=6.42";
            c.Extra = (s, k) =>
            {
                foreach (double x in new[] { k.X(0), k.X(6.0) })
                {
                    double sg = x < k.X(3) ? -1 : 1;
                    // three 0.1 mm chords (under a tenth of the 2.5 mm text), then a 4 mm leg down
                    s.Line(x, k.BottomY, x + sg * 0.07, k.BottomY - 0.07, SyntheticSheet.Red)
                     .Line(x + sg * 0.07, k.BottomY - 0.07, x + sg * 0.1, k.BottomY - 0.17, SyntheticSheet.Red)
                     .Line(x + sg * 0.1, k.BottomY - 0.17, x + sg * 0.1, k.BottomY - 0.27, SyntheticSheet.Red)
                     .Line(x + sg * 0.1, k.BottomY - 0.27, x + sg * 0.1, k.BottomY - 4.0, SyntheticSheet.Red);
                }
                stray?.Invoke(s, k);
            };
        });

        [Fact]
        public void A_hook_bent_in_chords_shorter_than_the_end_window_is_walked_as_one_chain()
        {
            JObject r = Read(ChordHooks());
            Assert.Empty(Refusals(r, "hook_ambiguous"));
            JObject bottom = (JObject)Card(r, "V-101")["bars"].Single(b => (string)b["face"] == "bottom");
            Assert.Equal("90", (string)bottom["hook_start"]["shape"]);
            Assert.Equal("90", (string)bottom["hook_end"]["shape"]);
        }

        [Fact]
        public void A_line_that_only_comes_near_a_bar_end_and_is_not_its_chain_still_forks_it()
        {
            // A second line starting 0.1 mm from the end - near, not drawn joined, and not the bend's next chord.
            JObject r = Read(ChordHooks((s, k) => s.Line(k.X(0) + 0.1, k.BottomY + 0.02, k.X(0) + 0.1, k.BottomY + 3, SyntheticSheet.Red)));
            Assert.Single(Refusals(r, "hook_ambiguous"));
        }

        // ----------------------------------------------------------------- zones

        /// <summary>A beam card whose zones are drawn by the test, with no stirrup total to sum.</summary>
        private static SyntheticSheet BeamWith(Action<SyntheticSheet, BeamCardSpec> zones, Action<BeamCardSpec> tweak = null) =>
            ScheduleCardFixtures.BeamSheet(c =>
            {
                c.Zones = new List<Tuple<string, double, double>>();
                c.StirrupTotal = null;
                c.Extra = zones;
                tweak?.Invoke(c);
            });

        [Fact]
        public void Zone_callouts_written_side_by_side_in_one_text_are_split_and_each_read()
        {
            JObject r = Read(BeamWith((s, k) =>
            {
                double x0 = k.X(1.0);
                s.Text("9.#3@.06 3.#3@.20", x0, k.ZoneY + 0.6, H);
                // 1.5 mm a character: "9.#3@.06" stands over x0+0..12, "3.#3@.20" over x0+13.5..25.5
                double c1 = x0 + 6, c2 = x0 + 19.5;
                s.Line(c1 - 4.8, k.ZoneY, c1 + 4.8, k.ZoneY).Line(c2 - 4, k.ZoneY, c2 + 4, k.ZoneY);
            }));
            JArray zones = (JArray)Card(r, "V-101")["zones"];
            Assert.Equal(new[] { 9, 3 }, zones.Select(z => (int)z["count"]));
            Assert.All(Checks(r, "zone_count_matches_its_length"), c => Assert.Equal("pass", (string)c["result"]));
        }

        [Fact]
        public void Zone_callouts_in_one_text_with_no_extent_are_refused_not_dropped()
        {
            SyntheticSheet sheet = BeamWith((s, k) =>
            {
                double x0 = k.X(1.0);
                s.Text("9.#3@.06 3.#3@.20", x0, k.ZoneY + 0.6, H);
                s.Line(x0 + 1, k.ZoneY, x0 + 10, k.ZoneY).Line(x0 + 15, k.ZoneY, x0 + 22, k.ZoneY);
            });
            ScheduleCards.Options o = ScheduleCards.ReadOptions(ScheduleCardFixtures.Args(), out _, out _);
            ScheduleCards.Sheet dwg = ScheduleCards.SheetFromCadEntities(sheet.ToCadEntities(middle: true), "middle", out string code, out string error);
            Assert.True(dwg != null, code + ": " + error);
            JObject r = ScheduleCards.Read(new[] { dwg }, o);
            Assert.Single(Refusals(r, "zone_group_not_placed"));
        }

        [Fact]
        public void A_dwg_entity_inside_a_block_definition_is_counted_and_not_read_at_block_coordinates()
        {
            // Measured on real schedule DWGs: the title block and the axis bubbles are blocks, and the
            // extractor reports their contents in the block's own coordinates.
            JArray ents = ScheduleCardFixtures.BeamSheet().ToCadEntities(middle: true);
            ents.Add(new JObject
            {
                ["id"] = "b1", ["kind"] = "line", ["layer"] = "BARS", ["block_path"] = new JArray("SYMBOL"),
                ["points"] = new JArray(new JArray(40.0, 180.0, 0.0), new JArray(40.0, 175.0, 0.0)), ["color_index"] = 1
            });
            ents.Add(new JObject
            {
                ["id"] = "b2", ["kind"] = "text", ["layer"] = "TEXT", ["block_path"] = new JArray("SYMBOL"), ["text"] = "2#5 L=1.00",
                ["points"] = new JArray { new JArray(100.0, 160.0, 0.0) }, ["text_height_mm"] = H, ["rotation_radians"] = 0.0
            });
            ScheduleCards.Options o = ScheduleCards.ReadOptions(ScheduleCardFixtures.Args(), out _, out _);
            ScheduleCards.Sheet dwg = ScheduleCards.SheetFromCadEntities(ents, "middle", out string code, out string error);
            Assert.True(dwg != null, code + ": " + error);
            JObject r = ScheduleCards.Read(new[] { dwg }, o);
            Assert.Equal(2, (int)r["sources"][0]["ignored_by_kind"]["in_block_definition"]);
            Assert.Empty((JArray)r["refusals"]);
            Assert.Equal(2, ((JArray)Card(r, "V-101")["bars"]).Count);
        }

        /// <summary>
        /// The fixture's texts as the DWG reading carries them on the real drawings: every one an
        /// MTEXT attached top-left, its point one text height above the baseline start, its box as
        /// wide as the text.
        /// </summary>
        private static JArray AsTopLeftMText(JArray ents, bool withBox = true)
        {
            foreach (JObject e in ents.Where(x => (string)x["kind"] == "text").Cast<JObject>().ToList())
            {
                double h = (double)e["text_height_mm"], rot = (double)e["rotation_radians"];
                double dx = Math.Cos(rot), dy = Math.Sin(rot);
                // ToCadEntities(middle: true) put the point at the baseline's middle: back to the start, then up one height.
                string txt = (string)e["text"];
                double w = SyntheticSheet.WidthOf(txt, h / ScheduleCards.CapHeight);   // the drawn advance, in em
                double sx = (double)e["points"][0][0] - dx * w / 2, sy = (double)e["points"][0][1] - dy * w / 2;
                e["points"] = new JArray { new JArray(sx - dy * h, sy + dx * h, 0.0) };
                e["text_type"] = "MTEXT";
                e["text_attachment"] = 1;
                if (withBox) { e["text_box_width_mm"] = w; e["text_box_height_mm"] = 1.33 * h; }
            }
            return ents;
        }

        [Fact]
        public void A_dwg_text_is_put_on_its_baseline_by_its_own_attachment_without_a_declared_anchor()
        {
            SyntheticSheet sheet = ScheduleCardFixtures.BeamSheet();
            ScheduleCards.Options o = ScheduleCards.ReadOptions(Args(new JObject { ["requirement_set"] = ScheduleCardFixtures.Proposal() }), out _, out _);
            ScheduleCards.Sheet prim = ScheduleCards.SheetFromPrimitives(sheet.ToJson(), out _, out _);
            JObject fromPrimitives = ScheduleCards.Read(new[] { prim }, o);

            ScheduleCards.Sheet dwg = ScheduleCards.SheetFromCadEntities(AsTopLeftMText(sheet.ToCadEntities(middle: true)), null, out string code, out string error);
            Assert.True(dwg != null, code + ": " + error);
            JObject r = ScheduleCards.Read(new[] { dwg }, o);
            Assert.Equal("per_entity", (string)r["sources"][0]["text_extent"]);
            Assert.Empty((JArray)r["refusals"]);
            Assert.True(JToken.DeepEquals(fromPrimitives["requirement_set"], r["requirement_set"]), r["requirement_set_not_proposed"].ToString());
        }

        [Fact]
        public void An_mtext_whose_box_the_entity_does_not_carry_is_refused_not_placed_on_a_guess()
        {
            ScheduleCards.Sheet dwg = ScheduleCards.SheetFromCadEntities(
                AsTopLeftMText(ScheduleCardFixtures.BeamSheet().ToCadEntities(middle: true), withBox: false), null, out string code, out string error);
            Assert.True(dwg != null, code + ": " + error);
            JObject r = ScheduleCards.Read(new[] { dwg }, ScheduleCards.ReadOptions(Args(null), out _, out _));
            Assert.NotEmpty(Refusals(r, "text_placement_not_read"));
            Assert.Empty((JArray)r["cards"]);
        }

        [Fact]
        public void A_centred_dwg_text_gives_its_extent_through_its_alignment_point()
        {
            JArray ents = ScheduleCardFixtures.BeamSheet().ToCadEntities(middle: true);
            foreach (JObject e in ents.Where(x => (string)x["kind"] == "text").Cast<JObject>())
            {
                double h = (double)e["text_height_mm"], rot = (double)e["rotation_radians"], w = SyntheticSheet.WidthOf((string)e["text"], h / ScheduleCards.CapHeight);
                double mx = (double)e["points"][0][0], my = (double)e["points"][0][1];
                e["points"] = new JArray { new JArray(mx - Math.Cos(rot) * w / 2, my - Math.Sin(rot) * w / 2, 0.0) };
                e["text_type"] = "TEXT"; e["text_justify_h"] = 1; e["text_justify_v"] = 0;
                e["text_alignment_point"] = new JArray(mx, my, 0.0);
            }
            ScheduleCards.Sheet dwg = ScheduleCards.SheetFromCadEntities(ents, null, out string code, out string error);
            Assert.True(dwg != null, code + ": " + error);
            JObject r = ScheduleCards.Read(new[] { dwg }, ScheduleCards.ReadOptions(Args(null), out _, out _));
            Assert.All(Card(r, "V-101")["axes"], a => Assert.Equal("centre", (string)a["position"]));
            Assert.Equal("pass", (string)Checks(r, "zones_fill_axis_span").Single()["result"]);
        }

        [Fact]
        public void A_dwg_text_height_is_its_capitals_and_the_windows_are_read_in_em()
        {
            // Measured on the same wall sheet as DWG and PDF: a turned horizontal-bar label and its
            // spacing stand 3 em apart - 4.2 of the DWG's capital heights, past the 4-height window.
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec
            {
                HorizontalTurned = true, HorizontalSpacing = null,
                Extra = (sh, w) => sh.Text("c/.20", w.OriginX + 80 + 3 * H, w.OriginY + 100, H, 90)
            });
            JObject fromPrimitives = Read(s);
            Assert.Single((JArray)Card(fromPrimitives, "M-1")["horizontal_bars"]);
            ScheduleCards.Sheet dwg = ScheduleCards.SheetFromCadEntities(s.ToCadEntities(middle: true), "middle", out string code, out string error);
            Assert.True(dwg != null, code + ": " + error);
            JObject r = ScheduleCards.Read(new[] { dwg }, ScheduleCards.ReadOptions(Args(null), out _, out _));
            Assert.Empty(Refusals(r, "horizontal_bar_spacing_missing"));
            Assert.Single((JArray)Card(r, "M-1")["horizontal_bars"]);
        }

        [Fact]
        public void A_dwg_bend_drawn_as_an_arc_is_followed_into_the_hook_leg()
        {
            // Measured on real schedule DWGs: the bend between a bar and its hook leg is an ARC entity.
            SyntheticSheet sheet = ScheduleCardFixtures.BeamSheet(c => { c.BottomHooks = false; c.BottomCallout = "3#4 L=6.55"; });
            var k = new BeamCardSpec();
            JArray ents = sheet.ToCadEntities(middle: true);
            int n = 0;
            foreach (double x in new[] { k.X(0), k.X(6.0) })
            {
                double sg = x < k.X(3) ? -1 : 1, r = 1.0;
                // a quarter bend of radius 1 mm from the bar's end down, then a 4 mm leg
                double cx = x, cy = k.BottomY - r;
                ents.Add(new JObject
                {
                    ["id"] = "a" + (++n), ["kind"] = "arc", ["layer"] = "BARS", ["color_index"] = 1,
                    ["points"] = new JArray { new JArray(cx, cy, 0.0) },
                    ["arc"] = new JObject
                    {
                        ["centre"] = new JArray(cx, cy, 0.0), ["radius_mm"] = r, ["clockwise"] = sg > 0,
                        ["start"] = new JArray(x, k.BottomY, 0.0),
                        ["middle"] = new JArray(x + sg * r * Math.Sqrt(0.5), cy + r * Math.Sqrt(0.5), 0.0),
                        ["end"] = new JArray(x + sg * r, cy, 0.0)
                    }
                });
                ents.Add(new JObject
                {
                    ["id"] = "l" + n, ["kind"] = "line", ["layer"] = "BARS", ["color_index"] = 1,
                    ["points"] = new JArray(new JArray(x + sg * r, cy, 0.0), new JArray(x + sg * r, cy - 4, 0.0))
                });
            }
            ents.Add(new JObject { ["id"] = "old", ["kind"] = "arc", ["layer"] = "LINES", ["points"] = new JArray { new JArray(0.0, 0.0, 0.0) },
                                   ["arc"] = new JObject { ["centre"] = new JArray(0.0, 0.0, 0.0), ["radius_mm"] = 1.0, ["clockwise"] = false } });
            ScheduleCards.Options o = ScheduleCards.ReadOptions(ScheduleCardFixtures.Args(), out _, out _);
            ScheduleCards.Sheet dwg = ScheduleCards.SheetFromCadEntities(ents, "middle", out string code, out string error);
            Assert.True(dwg != null, code + ": " + error);
            JObject r2 = ScheduleCards.Read(new[] { dwg }, o);
            Assert.Equal(1, (int)r2["sources"][0]["ignored_by_kind"]["arc_without_ends"]);
            JObject bottom = (JObject)Card(r2, "V-101")["bars"].Single(b => (string)b["face"] == "bottom");
            Assert.Equal("90", (string)bottom["hook_start"]["shape"]);
            Assert.Equal("90", (string)bottom["hook_end"]["shape"]);
            Assert.Equal("pass", (string)Checks(r2, "bar_length_matches_callout").Single(c => ((string)c["why"]).Contains("3#4"))["result"]);
        }

        [Fact]
        public void A_zone_with_few_close_stirrups_drawn_shorter_than_its_text_is_read_and_checked()
        {
            // two stirrups at .08 m: 1.6 mm at 1:50, under the 2.5 mm text
            JObject r = Read(BeamWith((s, k) =>
            {
                s.Line(k.X(1.0), k.ZoneY, k.X(1.08), k.ZoneY);
                s.Above("2#3@.08", (k.X(1.0) + k.X(1.08)) / 2, k.ZoneY, H);
            }));
            JObject z = (JObject)Card(r, "V-101")["zones"].Single();
            Assert.Equal(2, (int)z["count"]);
            Assert.Equal("pass", (string)Checks(r, "zone_count_matches_its_length").Single()["result"]);
        }

        [Fact]
        public void A_zone_line_drawn_between_the_count_and_the_spacing_is_the_zone_s_line()
        {
            JObject r = Read(BeamWith((s, k) =>
            {
                s.Line(k.X(1.05), k.ZoneY, k.X(4.95), k.ZoneY);
                double cx = (k.X(1.05) + k.X(4.95)) / 2;
                s.Above("14e#3", cx, k.ZoneY, H, 0.6);
                s.Text("c/.30", cx - SyntheticSheet.WidthOf("c/.30", H) / 2, k.ZoneY - 0.9 * H, H);
            }));
            Assert.Empty(Refusals(r, "callout_without_line"));
            JObject z = (JObject)Card(r, "V-101")["zones"].Single();
            Assert.Equal(14, (int)z["count"]);
            Assert.Equal(300, (double)z["spacing_mm"], 6);
            Assert.Equal("pass", (string)Checks(r, "zone_count_matches_its_length").Single()["result"]);
        }

        [Fact]
        public void Two_lines_between_the_count_and_the_spacing_are_refused()
        {
            JObject r = Read(BeamWith((s, k) =>
            {
                s.Line(k.X(1.05), k.ZoneY, k.X(4.95), k.ZoneY).Line(k.X(1.05), k.ZoneY - 0.3, k.X(4.95), k.ZoneY - 0.3);
                double cx = (k.X(1.05) + k.X(4.95)) / 2;
                s.Above("14e#3", cx, k.ZoneY, H, 0.6);
                s.Text("c/.30", cx - SyntheticSheet.WidthOf("c/.30", H) / 2, k.ZoneY - 0.9 * H, H);
            }));
            Assert.Single(Refusals(r, "callout_two_lines_equally_close"));
            Assert.Empty((JArray)Card(r, "V-101")["zones"]);
        }

        [Fact]
        public void A_stirrup_type_written_against_the_bar_is_read_as_the_zone_s_type()
        {
            JObject r = Read(BeamWith(null, c => c.Zones = new List<Tuple<string, double, double>> { Tuple.Create("14e#3T-4|c/.30", 1.05, 4.95) }));
            JObject z = (JObject)Card(r, "V-101")["zones"].Single();
            Assert.Equal(14, (int)z["count"]);
            Assert.Equal("T-4", (string)z["stirrup_type"]);
        }

        [Fact]
        public void A_text_that_states_steel_in_a_form_not_read_is_refused_by_name()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.Extra = (s, k) => s.Text("5 #4@0.25 m", k.OriginX + 150, k.OriginY + 10, H)));
            JObject x = Refusals(r, "callout_not_recognised").Single();
            Assert.Equal("5 #4@0.25 m", (string)x["text"]);
            Assert.Equal("read_with_refusals", (string)r["summary"]["verdict"]);
        }

        // -------------------------------------------------------- stirrup bar label

        private static SyntheticSheet LabelledCard(string label = "T7 #3") => ScheduleCardFixtures.BeamSheet(c =>
        {
            c.Zones = new List<Tuple<string, double, double>>
            {
                Tuple.Create("11@.08", 0.15, 0.95), Tuple.Create("14@.30", 1.05, 4.95), Tuple.Create("11@.08", 5.05, 5.85)
            };
            if (label != null) c.Extra = (s, k) => s.Text(label, k.OriginX + 165, k.OriginY + 50, H);
        });

        private const string Label = @"^T\d+\s*(?<bar>[#Ø]\s*\d+)$";

        [Fact]
        public void Zones_that_state_no_bar_take_the_card_s_label_only_when_the_label_is_declared()
        {
            JObject undeclared = Read(LabelledCard());
            Assert.Equal(3, Refusals(undeclared, "zone_bar_not_stated").Count());

            JObject r = Read(LabelledCard(), Args(new JObject { ["stirrup_bar_label"] = Label }));
            Assert.Empty((JArray)r["refusals"]);
            JObject card = Card(r, "V-101");
            Assert.Equal(3, (int)card["stirrup_bar_stated"]["size"]);
            Assert.All(card["zones"], z => Assert.Equal("card_label", (string)z["bar_from"]));
            Assert.All(card["zones"], z => Assert.Equal(3, (int)z["bar"]["size"]));
            Assert.Equal("pass", (string)Checks(r, "stirrups_sum_to_stated_total").Single()["result"]);
        }

        [Fact]
        public void Two_labels_stating_different_stirrup_bars_refuse_every_zone_that_states_none()
        {
            SyntheticSheet s = LabelledCard();
            s.Text("T2 #4", 185, 160, H);
            JObject r = Read(s, Args(new JObject { ["stirrup_bar_label"] = Label }));
            Assert.Equal(3, Refusals(r, "zone_bar_not_stated").Count());
        }

        [Fact]
        public void A_stirrup_bar_label_without_a_bar_group_is_refused_whole()
        {
            ScheduleCards.Options o = ScheduleCards.ReadOptions(Args(new JObject { ["stirrup_bar_label"] = @"^T\d+\s*#\d+$" }), out string code, out _);
            Assert.Null(o);
            Assert.Equal("options_not_usable", code);
        }

        // ------------------------------------------------------------- support gaps

        private static SyntheticSheet ThreeAxes(params Tuple<string, double, double>[] zones) => ScheduleCardFixtures.BeamSheet(c =>
        {
            c.Axes = new[] { "A", "B", "C" };
            c.StirrupTotal = null;
            c.Zones = zones.ToList();
        });

        private static JObject GapArgs(double gap = 400) => new JObject
        {
            ["scale"] = BeamCardSpec.Scale,
            ["tolerances"] = new JObject { ["length_mm"] = 30, ["zone_span_mm"] = 600, ["support_gap_mm"] = gap }
        };

        [Fact]
        public void Zones_drawn_between_support_faces_fill_the_span_when_the_support_gap_is_declared()
        {
            SyntheticSheet s = ThreeAxes(Tuple.Create("10#3@.30", 0.15, 2.85), Tuple.Create("10#3@.30", 3.15, 5.85));
            JObject axisSpan = Read(s);
            Assert.Equal("pass", (string)Checks(axisSpan, "zones_fill_axis_span").Single()["result"]);   // 5400 of 6000, within 600

            JObject r = Read(s, GapArgs());
            JObject check = Checks(r, "zones_fill_span_between_supports").Single();
            Assert.Equal("pass", (string)check["result"]);
            Assert.Equal(300, (double)check["found"], 3);
            Assert.Empty(Checks(r, "zones_fill_axis_span"));
        }

        [Fact]
        public void A_gap_wider_than_the_support_or_away_from_every_axis_is_a_missing_zone()
        {
            JObject atSupport = Read(ThreeAxes(Tuple.Create("5#3@.30", 0.15, 1.35), Tuple.Create("10#3@.30", 3.15, 5.85)), GapArgs());
            Assert.Equal("fail", (string)Checks(atSupport, "zones_fill_span_between_supports").Single()["result"]);

            JObject midSpan = Read(ThreeAxes(Tuple.Create("5#3@.30", 0.15, 1.35), Tuple.Create("4#3@.30", 1.95, 2.85),
                                             Tuple.Create("10#3@.30", 3.15, 5.85)), GapArgs());
            JObject check = Checks(midSpan, "zones_fill_span_between_supports").Single();
            Assert.Equal("fail", (string)check["result"]);
            Assert.Contains("neither a hand-over", (string)check["why"]);
        }

        [Fact]
        public void Zones_that_stop_at_an_interior_support_are_not_proposed_as_one_host_s_zones()
        {
            JObject a = GapArgs();
            a["requirement_set"] = ScheduleCardFixtures.Proposal();
            a["requirement_set"]["extents"] = "host";   // the axis form expresses them: see the axis tests
            JObject r = Read(ThreeAxes(Tuple.Create("10#3@.30", 0.15, 2.85), Tuple.Create("10#3@.30", 3.15, 5.85)), a);
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "zones_across_supports");
        }

        [Fact]
        public void The_support_gap_is_a_positive_number_or_the_reading_is_refused_whole()
        {
            Assert.Null(ScheduleCards.ReadOptions(GapArgs(0), out string code, out _));
            Assert.Equal("options_not_usable", code);
        }

        // ------------------------------------------------------------------ frames

        [Fact]
        public void A_card_s_closed_frame_wins_over_two_long_lines_inside_it()
        {
            // Two long lines inside the card (title boxes of a stacked layout) bound a smaller
            // "open frame" around the title that leaves the last zone outside.
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
                c.Extra = (s, k) => s.Line(k.OriginX + 3, k.OriginY + 78, k.OriginX + 120, k.OriginY + 78)
                                     .Line(k.OriginX + 3, k.OriginY + 2, k.OriginX + 120, k.OriginY + 2)));
            JObject card = Card(r, "V-101");
            Assert.Equal("closed", (string)card["frame_kind"]);
            Assert.Equal(3, ((JArray)card["zones"]).Count);
            Assert.Empty((JArray)r["refusals"]);
        }

        [Fact]
        public void A_frame_line_is_not_lost_to_a_piece_a_hair_lower_further_along()
        {
            // Measured on a real sheet: the next card's line, 0.01 mm lower and to the right, used to
            // swallow this card's bottom line when collinear pieces were joined.
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.RoundedFrame = true;
                c.Extra = (s, k) => s.Line(k.OriginX + 250, k.OriginY - 0.01, k.OriginX + 290, k.OriginY - 0.01);
            }));
            Assert.Empty(Refusals(r, "card_frame_not_found"));
            Assert.Equal("top_and_bottom_lines", (string)Card(r, "V-101")["frame_kind"]);
        }

        [Fact]
        public void A_sheet_number_in_a_border_around_the_cards_is_not_a_card_and_what_lies_outside_them_is_named()
        {
            SyntheticSheet s = ScheduleCardFixtures.BeamSheet();
            ScheduleCardFixtures.BeamCard(s, new BeamCardSpec { Title = "V-102", OriginY = 40 });
            s.Rect(10, 30, 400, 290);
            s.Text("S-90", 350, 35, BeamCardSpec.TitleH);
            JObject r = Read(s);
            Assert.Empty((JArray)r["refusals"]);
            Assert.Equal("S-90", (string)r["titles_not_cards"].Single()["title"]);
            Assert.Equal(new[] { "V-101", "V-102" }, r["cards"].Select(c => (string)c["title"]).OrderBy(t => t));

            // A callout left over outside every card may be the title's own card, whose frame was
            // not read: the callout is named and the title is refused, not set aside as a heading.
            s.Line(300, 100, 340, 100, SyntheticSheet.Red).Above("2#4 L=2.00", 320, 100, H);
            JObject left = Read(s);
            Assert.Equal("2#4 L=2.00", (string)Refusals(left, "callout_outside_cards").Single()["text"]);
            Assert.Equal("S-90", (string)Refusals(left, "card_frame_shared").Single()["text"]);
            Assert.Empty((JArray)left["titles_not_cards"]);
        }

        [Fact]
        public void A_drawing_set_that_titles_cards_two_ways_declares_both_patterns()
        {
            // Measured: wall cards titled "W-3" and their detail boxes "W-3 <from>-<to>".
            SyntheticSheet s = ScheduleCardFixtures.BeamSheet(c => c.Title = "V-7 A1-B2");
            Assert.Empty((JArray)Read(s)["cards"]);
            JObject r = Read(s, Args(new JObject { ["title_pattern"] = new JArray(@"^[A-Z]{1,3}-?\d{1,4}[A-Z]?$", @"^[A-Z]{1,3}-?\d{1,4}[A-Z]? [A-Z0-9]+-[A-Z0-9]+$") }));
            Assert.Equal("V-7 A1-B2", (string)r["cards"].Single()["title"]);
            Assert.Null(ScheduleCards.ReadOptions(Args(new JObject { ["title_pattern"] = new JArray("^ok$", "(") }), out string code, out _));
            Assert.Equal("options_not_usable", code);
        }

        // ------------------------------------------------------------------- walls

        /// <summary>
        /// A wall card that writes its storey bars level, across them: three turned red lines from
        /// +0.00 to +2.50, the callout at mid-height crossing the middle one, the horizontal bars
        /// labelled turned with their spacing beside.
        /// </summary>
        private static SyntheticSheet AcrossCard(string callout = "3 #3L= 2.50", Action<SyntheticSheet> extra = null, int bars = 3)
        {
            var s = new SyntheticSheet();
            double ox = 250, oy = 20;
            s.Rect(ox, oy, ox + 120, oy + 160);
            s.Text("M-2", ox + 5, oy + 150, BeamCardSpec.TitleH);
            s.Line(ox + 60, oy + 20, ox + 110, oy + 20).Text("+0.00", ox + 70, oy + 20.6, H);
            s.Line(ox + 60, oy + 70, ox + 110, oy + 70).Text("+2.50", ox + 70, oy + 70.6, H);
            for (int i = 0; i < bars; i++) s.Line(ox + 20 + 10 * i, oy + 20, ox + 20 + 10 * i, oy + 70, SyntheticSheet.Red);
            if (callout != null) s.Text(callout, ox + 30 - SyntheticSheet.WidthOf(callout, H) / 2, oy + 44, H);
            s.Text("12#3", ox + 95, oy + 100, H, 90).Text("c/.25", ox + 99, oy + 100, H, 90);
            extra?.Invoke(s);
            return s;
        }

        [Fact]
        public void A_level_callout_written_across_its_turned_bars_names_the_bars_its_height_crosses()
        {
            JObject r = Read(AcrossCard());
            JObject card = Card(r, "M-2");
            Assert.Equal("wall", (string)card["kind"]);
            JObject v = (JObject)card["vertical_bars"].Single();
            Assert.Equal("level_callout_across_turned_bars", (string)v["read_as"]);
            Assert.Equal(3, (int)v["count"]);
            Assert.Equal(3, (int)v["drawn_lines"]);
            Assert.Equal(0, (double)v["z_bottom_mm"], 3);
            Assert.Equal(2500, (double)v["z_top_mm"], 3);
            Assert.Equal("pass", (string)Checks(r, "bar_length_matches_callout").Single()["result"]);
            Assert.Empty((JArray)r["refusals"]);
        }

        [Fact]
        public void A_callout_across_bars_whose_count_is_not_its_own_is_refused()
        {
            JObject r = Read(AcrossCard("4 #3L= 2.50"));
            Assert.Single(Refusals(r, "bar_group_count_differs"));
            Assert.Empty((JArray)Card(r, "M-2")["vertical_bars"]);
        }

        [Fact]
        public void Two_callouts_across_the_same_bars_are_both_refused()
        {
            JObject r = Read(AcrossCard(extra: s => s.Text("3 #3L= 2.50", 275, 54, H)));
            Assert.Equal(2, Refusals(r, "line_claimed_by_two_callouts").Count());
        }

        [Fact]
        public void A_card_frame_side_is_not_a_bar_a_level_callout_crosses()
        {
            // No bars drawn: only the frame's sides run through the callout's height.
            JObject r = Read(AcrossCard(bars: 0));
            Assert.Single(Refusals(r, "callout_without_line"));
            Assert.Empty(Refusals(r, "bar_group_count_differs"));
        }
    }
}
