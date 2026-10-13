// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// L10, reading schedule cards: ScheduleCards turns the primitives of a vector
// sheet (segments, polylines, positioned texts) into rows, the rows into bar
// notation through RebarNotation (F3), and beam cards into a proposed
// horizun.structural-requirements/1 set keyed by mark. Every sheet is SYNTHETIC
// (ScheduleCardFixtures). Each refusal and each failing self-check has its own
// sheet: one thing changed, one code expected - never a silent fix.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleCardReaderTests
    {
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

        // ------------------------------------------------------------------ beam card

        [Fact]
        public void A_beam_card_is_read_into_bars_zones_and_its_stated_counts()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet());
            JObject card = Card(r, "V-101");
            Assert.Equal("beam", (string)card["kind"]);
            Assert.Equal(2, (int)card["members_stated"]);
            Assert.Equal(36, (int)card["stirrups_stated"]);
            Assert.Equal("B=25 H=40", (string)card["section_as_written"]);
            Assert.Equal(new[] { "A", "B" }, card["axes"].Select(a => (string)a["name"]));
            Assert.Equal(6000, (double)card["axes"][1]["station_mm"], 3);

            JObject top = (JObject)card["bars"].Single(b => (string)b["face"] == "top");
            Assert.Equal(2, (int)top["count"]);
            Assert.Equal("bar_number", (string)top["bar"]["system"]);
            Assert.Equal(6, (int)top["bar"]["size"]);
            Assert.Equal(6400, (double)top["notation_length_mm"], 3);
            Assert.Equal(6400, (double)top["drawn_developed_mm"], 3);
            Assert.Equal(-200, (double)top["from_station_mm"], 3);
            Assert.Equal(JTokenType.Null, top["hook_start"].Type);

            JObject bottom = (JObject)card["bars"].Single(b => (string)b["face"] == "bottom");
            Assert.Equal(3, (int)bottom["count"]);
            Assert.Equal(6000, (double)bottom["drawn_straight_mm"], 3);
            Assert.Equal("down", (string)bottom["hook_start"]["direction"]);
            Assert.Equal(250, (double)bottom["hook_start"]["leg_mm"], 3);
            Assert.Equal(250, (double)bottom["hook_end"]["leg_mm"], 3);
            Assert.Equal(6500, (double)bottom["drawn_developed_mm"], 3);

            JArray zones = (JArray)card["zones"];
            Assert.Equal(new[] { 11, 14, 11 }, zones.Select(z => (int)z["count"]));
            Assert.Equal(new[] { 80.0, 300.0, 80.0 }, zones.Select(z => Math.Round((double)z["spacing_mm"], 6)));
            Assert.Equal(800, (double)zones[0]["length_mm"], 3);
            Assert.Equal(3900, (double)zones[1]["length_mm"], 3);
            Assert.Equal("callout", (string)zones[1]["bar_from"]);
            // "11@.08" states no bar; the card states exactly one stirrup bar, and the reply SAYS where it came from.
            Assert.Equal("card", (string)zones[0]["bar_from"]);
            Assert.Equal(3, (int)zones[0]["bar"]["size"]);

            Assert.Empty((JArray)r["refusals"]);
            Assert.Equal("read_clean", (string)r["summary"]["verdict"]);
            Assert.Equal(0, (int)r["summary"]["checks_failed"]);
            Assert.True((bool)r["writes_nothing"]);
        }

        [Fact]
        public void Every_self_check_of_a_clean_card_passes_and_says_what_it_compared()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet());
            Assert.Equal(2, Checks(r, "bar_length_matches_callout").Count(c => (string)c["result"] == "pass"));
            Assert.Equal(3, Checks(r, "zone_count_matches_its_length").Count(c => (string)c["result"] == "pass"));
            JObject sum = Checks(r, "stirrups_sum_to_stated_total").Single();
            Assert.Equal("pass", (string)sum["result"]);
            Assert.Equal(36, (int)sum["expected"]);
            Assert.Equal(36, (int)sum["found"]);
            JObject span = Checks(r, "zones_fill_axis_span").Single();
            Assert.Equal("pass", (string)span["result"]);
            Assert.Equal(6000, (double)span["expected"], 3);
            Assert.Equal(5500, (double)span["found"], 3);
            Assert.Equal(600, (double)span["tolerance_mm"], 3);
        }

        [Fact]
        public void The_proposed_requirement_set_loads_and_keys_each_card_by_its_mark()
        {
            // extents "host": the proposal measured from the host's start face (the axis form has its own tests).
            JObject hostMode = ScheduleCardFixtures.Proposal();
            hostMode["extents"] = "host";
            JObject r = Read(ScheduleCardFixtures.BeamSheet(), ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = hostMode }));
            var set = (JObject)r["requirement_set"];
            Assert.NotNull(set);
            StructuralRequirementSet loaded = StructuralRequirementSet.Load(set);
            Assert.True(loaded.Ok, loaded.Error);

            JObject rule = (JObject)set["section_rules"].Single();
            Assert.Equal("V-101", (string)rule["host"]["mark"]);
            Assert.Equal("OST_StructuralFraming", (string)rule["host"]["category"]);
            Assert.Equal("host", (string)rule["cover"]["source"]);
            JObject top = (JObject)rule["layers"].Single(l => (string)l["face"] == "top");
            Assert.Equal(2, (int)top["count"]);
            Assert.Equal("host_length", (string)top["extend"]);
            JObject bottom = (JObject)rule["layers"].Single(l => (string)l["face"] == "bottom");
            Assert.Equal(3, (int)bottom["count"]);
            Assert.NotNull(bottom["start"]["hook_type"]);
            Assert.NotNull(bottom["end"]["hook_type"]);
            JArray zones = (JArray)rule["stirrups"]["zones"];
            Assert.Equal(new[] { 800.0, 3900.0, 800.0 }, zones.Select(z => (double)z["length_mm"]));
            Assert.Equal(new[] { 80.0, 300.0, 80.0 }, zones.Select(z => (double)z["layout"]["spacing_mm"]));
            Assert.False((bool)zones[0]["layout"]["include_last_bar"]);
            Assert.Empty((JArray)r["requirement_set_not_proposed"]);
            Assert.Equal(2, (int)r["cards"][0]["members_stated"]);
        }

        [Fact]
        public void Without_declared_bar_type_names_no_set_is_proposed_and_the_reply_says_which()
        {
            JObject p = ScheduleCardFixtures.Proposal();
            ((JObject)p["bar_type_names"]).Remove("#4");
            JObject r = Read(ScheduleCardFixtures.BeamSheet(), ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = p }));
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            JObject why = r["requirement_set_not_proposed"].Cast<JObject>().Single(n => (string)n["code"] == "bar_type_not_declared");
            Assert.Contains("#4", (string)why["why"]);
        }

        [Fact]
        public void Asymmetric_zones_are_not_mapped_to_a_host_end_unless_the_caller_declares_the_orientation()
        {
            Action<BeamCardSpec> asym = c => c.Zones = new List<Tuple<string, double, double>>
            {
                Tuple.Create("11@.08", 0.15, 0.95), Tuple.Create("17#3@.30", 1.05, 5.85)
            };
            Action<BeamCardSpec> total = c => { asym(c); c.StirrupTotal = "SON 28"; };
            JObject hostMode = ScheduleCardFixtures.Proposal();
            hostMode["extents"] = "host";
            JObject r = Read(ScheduleCardFixtures.BeamSheet(total), ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = hostMode }));
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "zones_orientation_unknown");

            JObject p = ScheduleCardFixtures.Proposal();
            p["extents"] = "host";
            p["card_left_is_host_start"] = true;
            JObject r2 = Read(ScheduleCardFixtures.BeamSheet(total), ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = p }));
            Assert.Equal(JTokenType.Object, r2["requirement_set"].Type);
            Assert.True(StructuralRequirementSet.Load((JObject)r2["requirement_set"]).Ok);
        }

        [Fact]
        public void A_baston_is_read_but_not_proposed_because_the_host_start_face_is_not_on_the_card()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
                {
                    c.Extra = (s, k) =>
                    {
                        s.Line(k.X(4.0), k.TopY + 8, k.X(6.2), k.TopY + 8, SyntheticSheet.Red);
                        s.Above("2#5 L=2.20", (k.X(4.0) + k.X(6.2)) / 2, k.TopY + 8, BeamCardSpec.H);
                    };
                }),
                ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = HostMode(Merge(ScheduleCardFixtures.Proposal(), "#5", "N5")) }));
            Assert.Equal(3, ((JArray)Card(r, "V-101")["bars"]).Count);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "extent_not_expressible");
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
        }

        private static JObject HostMode(JObject p)
        {
            p["extents"] = "host";
            return p;
        }

        private static JObject Merge(JObject p, string size, string name)
        {
            p["bar_type_names"][size] = name;
            return p;
        }

        // --------------------------------------------------------------- refusals

        [Fact]
        public void A_callout_with_no_line_under_it_is_refused_not_placed()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.TopLine = false));
            JObject refusal = Refusals(r, "callout_without_line").Single();
            Assert.Equal("2#6 L=6.40", (string)refusal["text"]);
            Assert.Equal("p1:V-101", (string)refusal["subject"]);
            Assert.DoesNotContain(Card(r, "V-101")["bars"], b => (int)b["count"] == 2);
            Assert.Equal("read_with_refusals", (string)r["summary"]["verdict"]);
        }

        [Fact]
        public void Two_lines_equally_close_under_a_callout_are_refused_rather_than_chosen()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
                c.Extra = (s, k) => s.Line(k.X(-0.2), k.TopY + 0.05, k.X(6.2), k.TopY + 0.05, SyntheticSheet.Red)));
            Assert.Single(Refusals(r, "callout_two_lines_equally_close"));
        }

        [Fact]
        public void One_line_claimed_by_two_callouts_refuses_both()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
                c.Extra = (s, k) => s.Above("2#6 L=6.40", k.X(1.0), k.TopY, BeamCardSpec.H)));
            Assert.Equal(2, Refusals(r, "line_claimed_by_two_callouts").Count());
        }

        [Fact]
        public void A_callout_whose_notation_is_unreadable_is_refused_with_the_notation_code()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.TopCallout = "2#6 L=6,400"));
            JObject refusal = Refusals(r, "callout_notation_refused").Single();
            Assert.Equal("notation_bad_number", (string)refusal["notation_code"]);
        }

        [Fact]
        public void A_zone_written_count_then_bar_with_its_spacing_below_is_read_and_without_it_refused()
        {
            Action<BeamCardSpec> eForm = c => c.Zones = new List<Tuple<string, double, double>>
            {
                Tuple.Create("11@.08", 0.15, 0.95), Tuple.Create("14e#3|c/.30", 1.05, 4.95), Tuple.Create("11@.08", 5.05, 5.85)
            };
            JObject ok = Read(ScheduleCardFixtures.BeamSheet(eForm));
            JObject middle = (JObject)Card(ok, "V-101")["zones"][1];
            Assert.Equal(14, (int)middle["count"]);
            Assert.Equal(300, (double)middle["spacing_mm"], 6);
            Assert.Empty((JArray)ok["refusals"]);

            JObject bad = Read(ScheduleCardFixtures.BeamSheet(c => c.Zones = new List<Tuple<string, double, double>>
            {
                Tuple.Create("11@.08", 0.15, 0.95), Tuple.Create("14e#3", 1.05, 4.95), Tuple.Create("11@.08", 5.05, 5.85)
            }));
            Assert.Single(Refusals(bad, "zone_spacing_missing"));
        }

        [Fact]
        public void A_zone_with_no_bar_on_a_card_stating_two_stirrup_bars_is_refused()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.Zones = new List<Tuple<string, double, double>>
            {
                Tuple.Create("11@.08", 0.15, 0.95), Tuple.Create("14#3@.30", 1.05, 4.95), Tuple.Create("11#4@.08", 5.05, 5.85)
            }));
            Assert.Single(Refusals(r, "zone_bar_not_stated"));
        }

        [Fact]
        public void Two_hook_ticks_at_one_end_refuse_the_bar()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.Extra = (s, k) =>
                s.Line(k.X(0), k.BottomY, k.X(0), k.BottomY + 5, SyntheticSheet.Red)));
            Assert.Single(Refusals(r, "hook_ambiguous"));
        }

        [Fact]
        public void A_title_with_no_frame_around_it_is_refused_as_a_card()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.Frame = false));
            Assert.Single(Refusals(r, "card_frame_not_found"));
            Assert.Empty((JArray)r["cards"]);
        }

        [Fact]
        public void Two_titles_in_one_frame_are_refused_rather_than_split()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.Extra = (s, k) => s.Text("V-102", k.OriginX + 100, k.OriginY + 68, BeamCardSpec.TitleH)));
            Assert.Equal(2, Refusals(r, "card_frame_shared").Count());
        }

        [Fact]
        public void The_scale_is_an_argument_and_a_reading_without_it_is_refused_whole()
        {
            JObject a = ScheduleCardFixtures.Args();
            a.Remove("scale");
            Assert.Null(ScheduleCards.ReadOptions(a, out string code, out string error));
            Assert.Equal("scale_not_declared", code);
            Assert.Contains("scale", error);

            a = ScheduleCardFixtures.Args();
            a["scale"] = 0;
            Assert.Null(ScheduleCards.ReadOptions(a, out code, out error));
            Assert.Equal("scale_not_declared", code);
        }

        [Fact]
        public void The_tolerances_are_arguments_and_a_reading_without_them_is_refused_whole()
        {
            JObject a = ScheduleCardFixtures.Args();
            a.Remove("tolerances");
            Assert.Null(ScheduleCards.ReadOptions(a, out string code, out _));
            Assert.Equal("tolerances_not_declared", code);
            a = ScheduleCardFixtures.Args();
            ((JObject)a["tolerances"]).Remove("zone_span_mm");
            Assert.Null(ScheduleCards.ReadOptions(a, out code, out _));
            Assert.Equal("tolerances_not_declared", code);
        }

        [Fact]
        public void Malformed_primitives_are_refused_whole_with_the_path()
        {
            var p = new JObject { ["segments"] = new JArray(new JObject { ["a"] = new JArray(1, 2) }) };
            Assert.Null(ScheduleCards.SheetFromPrimitives(p, out string code, out string error));
            Assert.Equal("primitives_not_usable", code);
            Assert.Contains("segments[0]", error);
        }

        [Fact]
        public void A_sheet_with_no_text_is_refused_by_name()
        {
            var sheet = new SyntheticSheet().Rect(0, 0, 100, 100);
            JObject r = Read(sheet);
            Assert.Single(Refusals(r, "sheet_has_no_text"));
        }

        // ------------------------------------------------------- failing self-checks

        [Fact]
        public void Zone_counts_that_do_not_sum_to_the_stated_total_fail_the_check()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.StirrupTotal = "SON 35"));
            JObject check = Checks(r, "stirrups_sum_to_stated_total").Single();
            Assert.Equal("fail", (string)check["result"]);
            Assert.Equal(35, (int)check["expected"]);
            Assert.Equal(36, (int)check["found"]);
            Assert.Equal("checks_failed", (string)r["summary"]["verdict"]);
        }

        [Fact]
        public void Zones_that_do_not_fill_the_axis_span_within_the_declared_tolerance_fail()
        {
            JObject a = ScheduleCardFixtures.Args();
            a["tolerances"]["zone_span_mm"] = 100;
            JObject r = Read(ScheduleCardFixtures.BeamSheet(), a);
            JObject check = Checks(r, "zones_fill_axis_span").Single();
            Assert.Equal("fail", (string)check["result"]);
            Assert.Equal(500, (double)check["difference_mm"], 3);
        }

        [Fact]
        public void A_bar_drawn_at_a_length_its_callout_does_not_state_fails_the_check()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.TopCallout = "2#6 L=7.40"));
            JObject check = Checks(r, "bar_length_matches_callout").Single(c => (string)c["result"] == "fail");
            Assert.Equal(7400, (double)check["expected"], 3);
            Assert.Equal(6400, (double)check["found"], 3);
        }

        [Fact]
        public void The_same_card_read_at_another_declared_scale_fails_its_length_checks()
        {
            JObject a = ScheduleCardFixtures.Args();
            a["scale"] = 25;
            JObject r = Read(ScheduleCardFixtures.BeamSheet(), a);
            Assert.Equal(2, Checks(r, "bar_length_matches_callout").Count(c => (string)c["result"] == "fail"));
        }

        [Fact]
        public void A_zone_whose_count_does_not_fit_its_length_fails()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.Zones[1] = Tuple.Create("10#3@.30", 1.05, 4.95);
                c.StirrupTotal = "SON 32";
            }));
            JObject check = Checks(r, "zone_count_matches_its_length").Single(c => (string)c["result"] == "fail");
            Assert.Equal(2700, (double)check["expected"], 3);
            Assert.Equal(3900, (double)check["found"], 3);
        }

        [Fact]
        public void Cards_that_do_not_add_up_to_the_sheet_stated_count_fail()
        {
            SyntheticSheet s = ScheduleCardFixtures.BeamSheet();
            s.Text("Son 3 Vigas", 20, 20, BeamCardSpec.H);
            JObject r = Read(s);
            JObject check = Checks(r, "cards_match_stated_count").Single();
            Assert.Equal("fail", (string)check["result"]);
            Assert.Equal(3, (int)check["expected"]);
            Assert.Equal(2, (int)check["found"]);
        }

        [Fact]
        public void A_card_with_a_failed_check_is_not_proposed()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.StirrupTotal = "SON 35"),
                             ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = ScheduleCardFixtures.Proposal() }));
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "card_checks_failed");
        }

        [Fact]
        public void A_card_whose_frame_is_only_its_top_and_bottom_lines_is_read_and_says_so()
        {
            JObject a = ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = ScheduleCardFixtures.Proposal() });
            JObject closed = Read(ScheduleCardFixtures.BeamSheet(), a);
            JObject open = Read(ScheduleCardFixtures.BeamSheet(c => c.RoundedFrame = true), a);
            Assert.Equal("closed", (string)Card(closed, "V-101")["frame_kind"]);
            Assert.Equal("top_and_bottom_lines", (string)Card(open, "V-101")["frame_kind"]);
            Assert.True(JToken.DeepEquals(closed["requirement_set"], open["requirement_set"]), open.ToString());
        }

        [Fact]
        public void A_hook_drawn_as_a_bend_of_short_chords_and_a_leg_is_read_whole()
        {
            // Measured on real sheets: the bar's corner is a few short chords, then the leg.
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.BottomHooks = false;
                c.BottomCallout = "3#4 L=6.54";
                c.Extra = (s, k) =>
                {
                    foreach (double x in new[] { k.X(0), k.X(6.0) })
                    {
                        double sgn = x < k.X(3) ? -1 : 1;
                        // a 1 mm bend in three chords, then a 4 mm leg down: 0.2 m at 1:50
                        s.Line(x, k.BottomY, x + sgn * 0.6, k.BottomY - 0.15, SyntheticSheet.Red)
                         .Line(x + sgn * 0.6, k.BottomY - 0.15, x + sgn * 0.9, k.BottomY - 0.45, SyntheticSheet.Red)
                         .Line(x + sgn * 0.9, k.BottomY - 0.45, x + sgn * 1.0, k.BottomY - 1.0, SyntheticSheet.Red)
                         .Line(x + sgn * 1.0, k.BottomY - 1.0, x + sgn * 1.0, k.BottomY - 5.0, SyntheticSheet.Red);
                    }
                };
            }), ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = ScheduleCardFixtures.Proposal() }));
            JObject bottom = (JObject)Card(r, "V-101")["bars"].Single(b => (string)b["face"] == "bottom");
            Assert.Equal("90", (string)bottom["hook_start"]["shape"]);
            Assert.Equal(200, (double)bottom["hook_start"]["leg_mm"], 3);
            Assert.True((double)bottom["hook_start"]["developed_mm"] > 200);
            Assert.Equal("pass", (string)Checks(r, "bar_length_matches_callout").Single(c => ((string)c["why"]).Contains("3#4")) ["result"]);
            Assert.Equal(JTokenType.Object, r["requirement_set"].Type);
        }

        [Fact]
        public void A_bar_drawn_in_collinear_pieces_is_one_straight_run()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.TopToM = 3.0;
                c.Extra = (s, k) => s.Line(k.X(3.0), k.TopY, k.X(6.2), k.TopY, SyntheticSheet.Red);
            }));
            JObject top = (JObject)Card(r, "V-101")["bars"].Single(b => (string)b["face"] == "top");
            Assert.Equal(6400, (double)top["drawn_straight_mm"], 3);
            Assert.Equal(6200, (double)top["to_station_mm"], 3);
        }

        [Fact]
        public void A_135_degree_hook_is_proposed_only_with_its_own_hook_type_name()
        {
            Action<BeamCardSpec> hooked = c =>
            {
                c.BottomHooks = false;
                c.BottomCallout = "3#4 L=6.42";
                c.Extra = (s, k) => s.Line(k.X(0), k.BottomY, k.X(0) + 3, k.BottomY + 3, SyntheticSheet.Red)
                                     .Line(k.X(6), k.BottomY, k.X(6) - 3, k.BottomY + 3, SyntheticSheet.Red);
            };
            JObject r = Read(ScheduleCardFixtures.BeamSheet(hooked), ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = ScheduleCardFixtures.Proposal() }));
            Assert.Equal("135", (string)Card(r, "V-101")["bars"].Single(b => (string)b["face"] == "bottom")["hook_end"]["shape"]);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "hook_type_not_declared");

            JObject p = ScheduleCardFixtures.Proposal();
            p["hook_type_names"]["135"] = "Standard - 135 deg.";
            JObject r2 = Read(ScheduleCardFixtures.BeamSheet(hooked), ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = p }));
            Assert.True(StructuralRequirementSet.Load((JObject)r2["requirement_set"]).Ok, r2["requirement_set_not_proposed"].ToString());
            Assert.Equal("H135", (string)r2["requirement_set"]["section_rules"][0]["layers"].Single(l => (string)l["face"] == "bottom")["start"]["hook_type"]);
        }

        [Fact]
        public void A_box_around_several_cards_is_no_card_s_frame()
        {
            SyntheticSheet s = ScheduleCardFixtures.BeamSheet(c => c.RoundedFrame = true);
            ScheduleCardFixtures.BeamCard(s, new BeamCardSpec { Title = "V-102", OriginY = 40, RoundedFrame = true });
            s.Rect(10, 30, 230, 240);   // a region box around both cards
            JObject r = Read(s);
            Assert.Equal(new[] { "V-101", "V-102" }, r["cards"].Select(c => (string)c["title"]).OrderBy(t => t));
            Assert.All(r["cards"], c => Assert.Equal("top_and_bottom_lines", (string)c["frame_kind"]));
            Assert.Empty((JArray)r["refusals"]);
        }

        [Fact]
        public void The_proposed_set_names_the_schema_the_requirement_set_reader_loads()
        {
            Assert.Equal(StructuralRequirementSet.SchemaName, ScheduleCards.StructuralRequirementSetSchema);
        }

        // ---------------------------------------------------------------- wall card

        [Fact]
        public void Level_values_written_just_under_their_line_are_read()
        {
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec { LevelsUnderLine = true });
            JObject r = Read(s);
            Assert.Equal("pass", (string)Checks(r, "levels_agree_with_scale").Single()["result"]);
            Assert.Equal(2500, (double)Card(r, "M-1")["vertical_bars"][0]["z_top_mm"], 3);
            Assert.Empty((JArray)r["refusals"]);
        }

        [Fact]
        public void Horizontal_wall_bars_labelled_turned_with_their_spacing_beside_are_read()
        {
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec { HorizontalTurned = true });
            JObject r = Read(s);
            JObject h = (JObject)Card(r, "M-1")["horizontal_bars"].Single();
            Assert.Equal(2, (int)h["count"]);
            Assert.Equal(200, (double)h["spacing_mm"], 6);
            Assert.Empty((JArray)r["refusals"]);
        }

        [Fact]
        public void A_wall_card_that_also_writes_a_bar_callout_level_is_still_a_wall_card_and_the_unplaced_callout_is_refused()
        {
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec
            {
                Extra = (sh, w) => sh.Text("4#4 L=2.90", w.OriginX + 40, w.OriginY + 120, ScheduleCardFixtures.H)
            });
            JObject r = Read(s);
            Assert.Equal("wall", (string)Card(r, "M-1")["kind"]);
            Assert.Single(Refusals(r, "callout_without_line"));
            Assert.Empty(Refusals(r, "card_kind_ambiguous"));
        }

        [Fact]
        public void A_card_with_wall_bars_and_stirrup_zones_is_refused_as_ambiguous()
        {
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec
            {
                Extra = (sh, w) => sh.Line(w.OriginX + 60, w.OriginY + 130, w.OriginX + 100, w.OriginY + 130)
                                     .Above("11@.08", w.OriginX + 80, w.OriginY + 130, ScheduleCardFixtures.H)
            });
            Assert.Single(Refusals(Read(s), "card_kind_ambiguous"));
        }

        [Fact]
        public void A_wall_card_is_read_into_vertical_bars_on_levels_and_horizontal_bars()
        {
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec());
            JObject r = Read(s);
            JObject card = Card(r, "M-1");
            Assert.Equal("wall", (string)card["kind"]);
            Assert.Equal(3, (int)card["members_stated"]);
            JObject v = (JObject)card["vertical_bars"].Single();
            Assert.Equal(4, (int)v["count"]);
            Assert.Equal(2900, (double)v["drawn_developed_mm"], 3);
            Assert.Equal(0, (double)v["z_bottom_mm"], 3);
            Assert.Equal(2500, (double)v["z_top_mm"], 3);
            Assert.Equal(new[] { 200.0, 200.0 }, new[] { (double)v["hook_bottom_mm"], (double)v["hook_top_mm"] });
            JObject h = (JObject)card["horizontal_bars"].Single();
            Assert.Equal(2, (int)h["count"]);
            Assert.Equal(200, (double)h["spacing_mm"], 6);
            Assert.Equal("pass", (string)Checks(r, "levels_agree_with_scale").Single()["result"]);
            Assert.Equal("pass", (string)Checks(r, "bar_length_matches_callout").Single()["result"]);
            Assert.Empty((JArray)r["refusals"]);
        }

        [Fact]
        public void Levels_drawn_at_another_scale_than_the_declared_one_fail_the_check()
        {
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec { SecondLevel = "+3.00" });
            JObject r = Read(s);
            JObject check = Checks(r, "levels_agree_with_scale").Single();
            Assert.Equal("fail", (string)check["result"]);
        }

        [Fact]
        public void A_vertical_callout_with_no_polyline_beside_it_is_refused()
        {
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec { Polyline = false });
            JObject r = Read(s);
            Assert.Single(Refusals(r, "callout_without_line"));
        }

        [Fact]
        public void A_horizontal_wall_bar_without_its_spacing_is_refused()
        {
            SyntheticSheet s = ScheduleCardFixtures.WallCard(new SyntheticSheet(), new ScheduleCardFixtures.WallCardSpec { HorizontalSpacing = null });
            JObject r = Read(s);
            Assert.Single(Refusals(r, "horizontal_bar_spacing_missing"));
        }

        // ------------------------------------------------------------------- tables

        private static JObject TableArgs() => ScheduleCardFixtures.Args(new JObject { ["table"] = ScheduleCardFixtures.TableSpec() });

        [Fact]
        public void A_bending_table_is_read_row_by_row_with_its_totals_checked()
        {
            SyntheticSheet s = ScheduleCardFixtures.Table(new SyntheticSheet(), 20, 20, ScheduleCardFixtures.TableRows());
            JObject r = Read(s, TableArgs());
            JObject t = (JObject)r["tables"].Single();
            JArray rows = (JArray)t["rows"];
            Assert.Equal(2, rows.Count);
            Assert.Equal("A1", (string)rows[0]["mark"]);
            Assert.Equal(4, (int)rows[0]["bar"]["size"]);
            Assert.Equal(10, (int)rows[0]["count"]);
            Assert.Equal(6500, (double)rows[0]["length_mm"], 3);
            Assert.Equal(65000, (double)rows[0]["total_mm"], 3);
            Assert.Equal(2, Checks(r, "table_row_total_matches").Count(c => (string)c["result"] == "pass"));
            Assert.Equal("pass", (string)Checks(r, "table_rows_sum_to_total").Single()["result"]);
            Assert.Empty((JArray)r["refusals"]);
        }

        [Fact]
        public void A_table_row_whose_count_times_length_is_not_its_total_fails_and_so_does_the_sum()
        {
            string[][] rows = ScheduleCardFixtures.TableRows();
            rows[0][4] = "60.00";
            JObject r = Read(ScheduleCardFixtures.Table(new SyntheticSheet(), 20, 20, rows), TableArgs());
            Assert.Single(Checks(r, "table_row_total_matches"), c => (string)c["result"] == "fail");
            Assert.Equal("fail", (string)Checks(r, "table_rows_sum_to_total").Single()["result"]);
        }

        [Fact]
        public void A_table_cell_that_is_not_a_number_refuses_its_row()
        {
            string[][] rows = ScheduleCardFixtures.TableRows();
            rows[1][2] = "four";
            JObject r = Read(ScheduleCardFixtures.Table(new SyntheticSheet(), 20, 20, rows), TableArgs());
            Assert.Single(Refusals(r, "table_cell_not_a_number"));
            Assert.Single((JArray)r["tables"][0]["rows"]);
            // A refused row cannot be summed: the totals check does not run rather than pass on half a table.
            Assert.Equal("not_run", (string)Checks(r, "table_rows_sum_to_total").Single()["result"]);
        }

        [Fact]
        public void A_bar_cell_without_its_system_is_refused_unless_the_caller_declares_the_prefix()
        {
            string[][] rows = ScheduleCardFixtures.TableRows();
            rows[0][1] = "4";
            JObject r = Read(ScheduleCardFixtures.Table(new SyntheticSheet(), 20, 20, rows), TableArgs());
            Assert.Single(Refusals(r, "table_bar_unrecognized"));

            JObject a = TableArgs();
            a["table"]["bar_prefix"] = "#";
            JObject r2 = Read(ScheduleCardFixtures.Table(new SyntheticSheet(), 20, 20, rows), a);
            Assert.Empty((JArray)r2["refusals"]);
        }

        [Fact]
        public void A_table_whose_declared_header_is_missing_is_refused_by_name()
        {
            JObject a = TableArgs();
            a["table"]["columns"]["shape"] = "FORMA";
            JObject r = Read(ScheduleCardFixtures.Table(new SyntheticSheet(), 20, 20, ScheduleCardFixtures.TableRows()), a);
            Assert.Single(Refusals(r, "table_header_incomplete"));
            Assert.Empty((JArray)r["tables"]);
        }

        [Fact]
        public void Without_declared_columns_no_table_is_read_and_the_reply_says_so()
        {
            JObject r = Read(ScheduleCardFixtures.Table(new SyntheticSheet(), 20, 20, ScheduleCardFixtures.TableRows()));
            Assert.Empty((JArray)r["tables"]);
            Assert.Contains("table", (string)r["tables_not_read"]);
        }

        // ------------------------------------------------------------- DWG route

        [Fact]
        public void The_dwg_intermediate_representation_reads_to_the_same_card()
        {
            SyntheticSheet sheet = ScheduleCardFixtures.BeamSheet();
            ScheduleCards.Options o = ScheduleCards.ReadOptions(ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = ScheduleCardFixtures.Proposal() }), out _, out _);
            ScheduleCards.Sheet fromCad = ScheduleCards.SheetFromCadEntities(sheet.ToCadEntities(), "middle", out string code, out string error);
            Assert.True(fromCad != null, code + ": " + error);
            JObject r = ScheduleCards.Read(new[] { fromCad }, o);
            JObject card = Card(r, "V-101");
            Assert.Equal(2, ((JArray)card["bars"]).Count);
            Assert.Equal(3, ((JArray)card["zones"]).Count);
            Assert.Empty((JArray)r["refusals"]);
            Assert.True(StructuralRequirementSet.Load((JObject)r["requirement_set"]).Ok);
            // The circle is counted, not read: the IR kinds this reader leaves alone are named.
            Assert.Equal(1, (int)r["sources"][0]["ignored_by_kind"]["circle"]);
            Assert.Equal("anchor_middle", (string)r["sources"][0]["text_extent"]);
        }

        [Fact]
        public void Dwg_texts_anchored_at_their_start_leave_the_axis_stations_uncertain_and_nothing_is_proposed()
        {
            SyntheticSheet sheet = ScheduleCardFixtures.BeamSheet();
            ScheduleCards.Options o = ScheduleCards.ReadOptions(ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = ScheduleCardFixtures.Proposal() }), out _, out _);
            ScheduleCards.Sheet fromCad = ScheduleCards.SheetFromCadEntities(sheet.ToCadEntities(false), "start", out _, out _);
            JObject r = ScheduleCards.Read(new[] { fromCad }, o);
            Assert.Equal(2, ((JArray)Card(r, "V-101")["bars"]).Count);
            Assert.Equal("not_run", (string)Checks(r, "zones_fill_axis_span").Single()["result"]);
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "axis_positions_uncertain");
        }

        [Fact]
        public void A_dwg_reading_must_declare_where_its_text_points_are()
        {
            // A text that does not say what places its point (no text_type) needs the declaration.
            var untyped = new JArray(new JObject
            {
                ["id"] = "t1", ["kind"] = "text", ["points"] = new JArray { new JArray(0.0, 0.0, 0.0) },
                ["text"] = "2#6 L=6.40", ["text_height_mm"] = 2.5, ["rotation_radians"] = 0.0
            });
            Assert.Null(ScheduleCards.SheetFromCadEntities(untyped, null, out string code, out _));
            Assert.Equal("cad_text_anchor_not_declared", code);
        }

        [Fact]
        public void A_dwg_text_carrying_mtext_formatting_is_refused_not_cleaned()
        {
            var e = new JArray(new JObject
            {
                ["id"] = "t1", ["kind"] = "text", ["points"] = new JArray { new JArray(0.0, 0.0, 0.0) },
                ["text"] = @"{\fArial|b1;2#6 L=6.40}", ["text_height_mm"] = 2.5, ["rotation_radians"] = 0.0
            });
            ScheduleCards.Sheet s = ScheduleCards.SheetFromCadEntities(e, "start", out _, out _);
            Assert.Contains(s.Refusals, r => (string)r["code"] == "text_formatting_not_read");
            Assert.Empty(s.Texts);
        }
    }
}
