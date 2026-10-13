// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE CARD PROPOSED FROM ITS OWN AXES (stage 1c). A card measures everything from
// its axis labels and says nothing about where a host's start face is, so the
// proposal says the same: every bar and every stirrup run as stations from the
// first axis toward the last, named as the card names them. Bastones, one-end
// hooks and asymmetric zones need no orientation declared any more; the plan
// finds the grids by name. What the card still does not say - which of two bars
// overlapping on one face stands where - stays refused. Every sheet is SYNTHETIC.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleCardAxisProposalTests
    {
        private static JObject Read(SyntheticSheet sheet, JObject proposal = null)
        {
            JObject a = ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = proposal ?? ScheduleCardFixtures.Proposal() });
            ScheduleCards.Options o = ScheduleCards.ReadOptions(a, out string code, out string error);
            Assert.True(o != null, code + ": " + error);
            ScheduleCards.Sheet s = ScheduleCards.SheetFromPrimitives(sheet.ToJson(), out code, out error);
            Assert.True(s != null, code + ": " + error);
            return ScheduleCards.Read(new[] { s }, o);
        }

        private static JObject Proposal(params string[] extra)
        {
            JObject p = ScheduleCardFixtures.Proposal();
            foreach (string size in extra) p["bar_type_names"][size] = "N" + size.Substring(1);
            return p;
        }

        private static JObject Rule(JObject r, string id = "V-101")
        {
            Assert.True(r["requirement_set"].Type == JTokenType.Object, r["requirement_set_not_proposed"].ToString());
            Assert.True(StructuralRequirementSet.Load((JObject)r["requirement_set"]).Ok, StructuralRequirementSet.Load((JObject)r["requirement_set"]).Error);
            return (JObject)r["requirement_set"]["section_rules"].Single(x => (string)x["id"] == id);
        }

        private static void Extent(JToken e, double from, double to)
        {
            Assert.Equal("A", (string)e["axis"]);
            Assert.Equal("B", (string)e["toward"]);
            Assert.Equal(from, (double)e["from_mm"], 3);
            Assert.Equal(to, (double)e["to_mm"], 3);
        }

        [Fact]
        public void A_card_is_proposed_as_stations_from_its_first_axis_toward_its_last()
        {
            JObject rule = Rule(Read(ScheduleCardFixtures.BeamSheet()));
            Extent(rule["layers"].Single(l => (string)l["face"] == "top")["extend"], -200, 6200);
            JObject bottom = (JObject)rule["layers"].Single(l => (string)l["face"] == "bottom");
            Extent(bottom["extend"], 0, 6000);
            Assert.Equal("H90", (string)bottom["start"]["hook_type"]);
            Assert.Equal("H90", (string)bottom["end"]["hook_type"]);
            // The stirrups where the card puts them: each zone from its own first stirrup, n at its spacing.
            Extent(rule["stirrups"]["extent"], 150, 5850);
            JArray zones = (JArray)rule["stirrups"]["zones"];
            Assert.Equal(new[] { 900.0, 4000.0, 800.0 }, zones.Select(z => (double)z["length_mm"]));
            Assert.Equal(new[] { 11, 14, 11 }, zones.Select(z => (int)z["layout"]["number"]));
            Assert.All(zones, z => Assert.Equal("number_with_spacing", (string)z["layout"]["rule"]));
        }

        [Fact]
        public void The_member_s_mark_selects_the_host_and_is_not_stamped_on_its_bars()
        {
            // Measured live (Revit 2023): a proposal that gave every rule the member's mark put that mark
            // on the top bars, the bottom bars and the stirrups alike - bar_mark_conflict, one schedule
            // line for different bars. Bar marks are the bar schedule's; this card does not state them.
            foreach (JObject p in new[] { Proposal(), ScheduleCardFixtures.Proposal() })
            {
                if (p == null) continue;
                JObject r = Read(ScheduleCardFixtures.BeamSheet(), p);
                JObject rule = Rule(r);
                Assert.Equal("V-101", (string)rule["host"]["mark"]);
                Assert.Null(rule["mark"]);
            }
            JObject host = ScheduleCardFixtures.Proposal();
            host["extents"] = "host";
            Assert.Null(Rule(Read(ScheduleCardFixtures.BeamSheet(), host))["mark"]);
        }

        [Fact]
        public void Asymmetric_zones_and_a_one_end_hook_need_no_declared_orientation()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.Zones = new List<Tuple<string, double, double>> { Tuple.Create("11@.08", 0.15, 0.95), Tuple.Create("17#3@.30", 1.05, 5.85) };
                c.StirrupTotal = "SON 28";
                c.BottomHooks = false;
                c.BottomCallout = "3#4 L=6.25";
                c.Extra = (s, k) => s.Line(k.X(0), k.BottomY, k.X(0), k.BottomY - 5, SyntheticSheet.Red);
            }));
            JObject bottom = (JObject)Rule(r)["layers"].Single(l => (string)l["face"] == "bottom");
            Assert.Equal("H90", (string)bottom["start"]["hook_type"]);
            Assert.Null(bottom["end"]);
            Assert.Equal(new[] { 11, 17 }, Rule(r)["stirrups"]["zones"].Select(z => (int)z["layout"]["number"]));
        }

        [Fact]
        public void Bastones_at_the_ends_are_layers_of_their_own()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.TopLine = false;
                c.TopCallout = null;
                c.Extra = (s, k) =>
                {
                    s.Line(k.X(-0.2), k.TopY, k.X(1.6), k.TopY, SyntheticSheet.Red).Above("2#5 L=1.80", (k.X(-0.2) + k.X(1.6)) / 2, k.TopY, BeamCardSpec.H);
                    s.Line(k.X(4.4), k.TopY, k.X(6.2), k.TopY, SyntheticSheet.Red).Above("2#5 L=1.80", (k.X(4.4) + k.X(6.2)) / 2, k.TopY, BeamCardSpec.H);
                };
            }), Proposal("#5"));
            JArray top = new JArray(Rule(r)["layers"].Where(l => (string)l["face"] == "top"));
            Assert.Equal(2, top.Count);
            Extent(top[0]["extend"], -200, 1600);
            Extent(top[1]["extend"], 4400, 6200);
        }

        [Fact]
        public void Two_bars_overlapping_on_one_face_are_not_proposed_because_where_each_stands_is_not_on_the_card()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.Extra = (s, k) =>
            {
                s.Line(k.X(4.0), k.TopY + 8, k.X(6.2), k.TopY + 8, SyntheticSheet.Red);
                s.Above("2#5 L=2.20", (k.X(4.0) + k.X(6.2)) / 2, k.TopY + 8, BeamCardSpec.H);
            }), Proposal("#5"));
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "face_bars_overlap");
        }

        // ---- THE FACE AS DECLARED (requirement_set.face_slots, round 3) ----------------------------
        // A card draws two groups on one face and never says where each stands across it. The
        // caller may declare how the drawing set lays them; only what that resolves without a doubt
        // is proposed, everything else stays face_bars_overlap.

        private static JObject Slots(JObject slots, params string[] extra)
        {
            JObject p = Proposal(extra);
            p["face_slots"] = slots;
            return p;
        }

        private static SyntheticSheet BastonOverContinuous(string callout) => ScheduleCardFixtures.BeamSheet(c => c.Extra = (s, k) =>
        {
            s.Line(k.X(4.0), k.TopY + 8, k.X(6.2), k.TopY + 8, SyntheticSheet.Red);
            s.Above(callout, (k.X(4.0) + k.X(6.2)) / 2, k.TopY + 8, BeamCardSpec.H);
        });

        [Fact]
        public void A_group_one_bar_fewer_than_the_continuous_one_it_lies_within_is_laid_in_its_gaps_when_declared()
        {
            JObject r = Read(BastonOverContinuous("1#5 L=2.20"), Slots(new JObject { ["additional"] = "between" }, "#5"));
            JArray top = new JArray(Rule(r)["layers"].Where(l => (string)l["face"] == "top"));
            Assert.Equal(2, top.Count);
            JToken cont = top.Single(l => l["between"] == null), mid = top.Single(l => l["between"] != null);
            Assert.Equal(2, (int)cont["count"]);
            Assert.Equal(1, (int)mid["count"]);
            Assert.Equal((string)cont["name"], (string)mid["between"]);
            Extent(mid["extend"], 4000, 6200);
            // Undeclared, the same card is refused, and the refusal names the declaration.
            JObject bare = Read(BastonOverContinuous("1#5 L=2.20"), Proposal("#5"));
            Assert.Equal(JTokenType.Null, bare["requirement_set"].Type);
            Assert.Contains(bare["requirement_set_not_proposed"], n => (string)n["code"] == "face_bars_overlap" && ((string)n["why"]).Contains("face_slots"));
        }

        [Fact]
        public void A_group_the_declaration_does_not_resolve_stays_refused_by_name()
        {
            // Two bars within two continuous ones: one gap, two bars - where they stand is still not on the card.
            JObject r = Read(BastonOverContinuous("2#5 L=2.20"), Slots(new JObject { ["additional"] = "between", ["laps"] = "inside" }, "#5"));
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "face_bars_overlap" && ((string)n["why"]).Contains("does not resolve"));
        }

        private static SyntheticSheet LappedTop(string second = "2#6 L=3.60") => ScheduleCardFixtures.BeamSheet(c =>
        {
            c.TopLine = false;
            c.TopCallout = null;
            c.Extra = (s, k) =>
            {
                s.Line(k.X(-0.2), k.TopY, k.X(3.4), k.TopY, SyntheticSheet.Red).Above("2#6 L=3.60", (k.X(-0.2) + k.X(3.4)) / 2, k.TopY, BeamCardSpec.H);
                s.Line(k.X(2.6), k.TopY + 5, k.X(6.2), k.TopY + 5, SyntheticSheet.Red).Above(second, (k.X(2.6) + k.X(6.2)) / 2, k.TopY + 5, BeamCardSpec.H);
            };
        });

        [Fact]
        public void Two_groups_of_one_bar_overlapping_at_their_free_ends_are_one_lapped_layer_when_declared()
        {
            JObject r = Read(LappedTop(), Slots(new JObject { ["laps"] = "inside" }));
            JObject top = (JObject)Rule(r)["layers"].Single(l => (string)l["face"] == "top");
            Assert.Equal(2, (int)top["count"]);
            Extent(top["extend"], -200, 6200);
            JObject lap = (JObject)((JArray)top["laps"]).Single();
            Assert.Equal(3000, (double)lap["at_mm"], 3);      // the middle of the overlap, from the first axis
            Assert.Equal(800, (double)lap["length_mm"], 3);
            Assert.Equal("inside", (string)lap["toward"]);
            Assert.Contains(Read(LappedTop()).Value<JArray>("requirement_set_not_proposed"), n => (string)n["code"] == "face_bars_overlap");
            // A different count on the second piece is not a lap of one layer.
            Assert.Contains(Read(LappedTop("3#6 L=3.60"), Slots(new JObject { ["laps"] = "inside" })).Value<JArray>("requirement_set_not_proposed"),
                            n => (string)n["code"] == "face_bars_overlap");
        }

        [Fact]
        public void Face_slots_declares_only_what_it_names()
        {
            foreach (JObject bad in new[]
            {
                new JObject(), new JObject { ["laps"] = "across" }, new JObject { ["additional"] = "corners" },
                new JObject { ["continuous"] = "corners_first" }
            })
            {
                JObject a = ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = Slots(bad) });
                Assert.Null(ScheduleCards.ReadOptions(a, out string code, out _));
                Assert.Equal("options_not_usable", code);
            }
            // One layer per face (extents host) has no layers to lay the groups in.
            JObject host = Slots(new JObject { ["laps"] = "inside" });
            host["extents"] = "host";
            Assert.Null(ScheduleCards.ReadOptions(ScheduleCardFixtures.Args(new JObject { ["requirement_set"] = host }), out string hcode, out _));
            Assert.Equal("options_not_usable", hcode);
        }

        [Fact]
        public void A_bar_end_that_bends_and_runs_on_parallel_is_neither_a_hook_nor_a_crank()
        {
            // Drawn: the bar steps up a little and carries on beside itself. A bayoneta, or the offset a
            // drawing gives one bar of a lap; the card gives neither the offset nor which - refused by name.
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.BottomHooks = false;
                c.Extra = (s, k) =>
                {
                    s.Line(k.X(6.0), k.BottomY, k.X(6.0) + 1.0, k.BottomY + 1.0, SyntheticSheet.Red);
                    s.Line(k.X(6.0) + 1.0, k.BottomY + 1.0, k.X(6.0) + 6.0, k.BottomY + 1.0, SyntheticSheet.Red);
                };
            }));
            Assert.Equal(JTokenType.Null, r["requirement_set"].Type);
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "bar_end_continues_parallel");
        }

        [Fact]
        public void Stirrups_a_support_separates_are_rules_of_their_own_each_from_the_first_axis()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.Axes = new[] { "A", "B", "C" };
                c.StirrupTotal = null;
                c.Zones = new List<Tuple<string, double, double>> { Tuple.Create("10#3@.30", 0.15, 2.85), Tuple.Create("10#3@.30", 3.15, 5.85) };
            }));
            JObject first = Rule(r), second = Rule(r, "V-101-s2");
            Assert.Equal("C", (string)first["stirrups"]["extent"]["toward"]);
            Assert.Equal(150, (double)first["stirrups"]["extent"]["from_mm"], 3);
            Assert.Equal(2850, (double)first["stirrups"]["extent"]["to_mm"], 3);
            Assert.Equal(3150, (double)second["stirrups"]["extent"]["from_mm"], 3);
            Assert.Empty((JArray)second["layers"]);
            Assert.Equal("V-101", (string)second["host"]["mark"]);
        }

        [Fact]
        public void A_card_that_writes_one_axis_name_twice_is_not_proposed()
        {
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c => c.Axes = new[] { "A", "A" }));
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "axis_names_repeated");
        }

        [Fact]
        public void Zones_drawn_reaching_the_next_zone_s_first_stirrup_are_not_proposed()
        {
            // 11 at .08 from 0.15 ends at 0.95, and the next zone starts there: one stirrup would be laid twice.
            JObject r = Read(ScheduleCardFixtures.BeamSheet(c =>
            {
                c.StirrupTotal = null;
                c.Zones = new List<Tuple<string, double, double>> { Tuple.Create("11@.08", 0.15, 0.95), Tuple.Create("17#3@.30", 0.95, 5.75) };
            }));
            Assert.Contains(r["requirement_set_not_proposed"], n => (string)n["code"] == "zones_not_consecutive");
        }
    }
}
