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

        

        private static void Extent(JToken e, double from, double to)
        {
            Assert.Equal("A", (string)e["axis"]);
            Assert.Equal("B", (string)e["toward"]);
            Assert.Equal(from, (double)e["from_mm"], 3);
            Assert.Equal(to, (double)e["to_mm"], 3);
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
