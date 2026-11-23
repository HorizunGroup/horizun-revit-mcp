// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// 2026-10 typed replacements for the commonest execute_python purposes: the current
// selection as a measure source, the rehearsal that always rolls back (what_if) and the
// plan for splitting a straight wall at the jambs of its openings. Known answers are
// worked by hand in millimetres.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MeasureSelectionAndWhatIfRulesTests
    {
        private static bool Parse(string json, out MeasureRequest r, out string e) => GeometryMeasureRules.TryParse(JObject.Parse(json), out r, out e);

        [Fact]
        public void Selection_is_a_source_of_its_own_and_excludes_the_others()
        {
            Assert.True(Parse("{\"selection\":true,\"measures\":[\"faces\",\"edges\",\"location\"]}", out MeasureRequest r, out string e), e);
            Assert.True(r.Selection);
            Assert.Equal(new[] { "faces", "edges", "location" }, r.Measures.ToArray());
            Assert.False(Parse("{\"selection\":true,\"ids\":[1],\"measures\":[\"location\"]}", out _, out e));
            Assert.Contains("not both", e);
            Assert.False(Parse("{\"selection\":\"yes\",\"measures\":[\"location\"]}", out _, out e));
            Assert.Contains("selection", e);
            Assert.False(Parse("{\"selection\":false,\"measures\":[\"location\"]}", out _, out e));
            Assert.Contains("ids or selector", e);
        }

        private static bool Req(string json, out MeasureRequest r, out WhatIfSpec w, out string e) =>
            GeometryWhatIfRules.TryParseRequest(JObject.Parse(json), out r, out w, out e);

        [Fact]
        public void What_if_without_element_ids_measures_its_own_ids_with_default_measures()
        {
            Assert.True(Req("{\"operation\":\"what_if\",\"what_if\":{\"action\":\"delete\",\"ids\":[5,6,5]}}", out MeasureRequest r, out WhatIfSpec w, out string e), e);
            Assert.Equal(new long[] { 5, 6 }, r.Ids.ToArray());
            Assert.Equal(new[] { "bounding_box", "solid_volume" }, r.Measures.ToArray());
            Assert.Equal("delete", w.Action);
            Assert.True(Req("{\"element_ids\":[7],\"measures\":[\"faces\"],\"what_if\":{\"action\":\"delete\",\"ids\":[5]}}", out r, out w, out e), e);
            Assert.Equal(new long[] { 7 }, r.Ids.ToArray());
            Assert.False(Req("{\"operation\":\"what_if\"}", out _, out _, out e));
            Assert.Contains("needs what_if", e);
            Assert.False(Req("{\"measures\":[\"weight\"],\"what_if\":{\"action\":\"delete\",\"ids\":[5]}}", out _, out _, out e));
            Assert.Contains("weight", e);
        }

        [Fact]
        public void What_if_move_converts_the_vector_from_the_request_unit_to_feet()
        {
            Assert.True(Req("{\"unit\":\"mm\",\"what_if\":{\"action\":\"move\",\"ids\":[9],\"vector\":[304.8,0,-609.6]}}", out _, out WhatIfSpec w, out string e), e);
            Assert.Equal(1.0, w.VectorFeet[0], 9);
            Assert.Equal(-2.0, w.VectorFeet[2], 9);
            Assert.True(Req("{\"unit\":\"m\",\"what_if\":{\"action\":\"move\",\"ids\":[9],\"vector\":[0.3048,0,0]}}", out _, out w, out e), e);
            Assert.Equal(1.0, w.VectorFeet[0], 9);
        }

        [Theory]
        [InlineData("{\"action\":\"explode\",\"ids\":[1]}", "action")]
        [InlineData("{\"action\":\"delete\"}", "ids")]
        [InlineData("{\"action\":\"join\",\"ids\":[1]}", "exactly two")]
        [InlineData("{\"action\":\"unjoin\",\"ids\":[1,2,3]}", "exactly two")]
        [InlineData("{\"action\":\"move\",\"ids\":[1]}", "vector")]
        [InlineData("{\"action\":\"move\",\"ids\":[1],\"vector\":[0,0,0]}", "zero")]
        [InlineData("{\"action\":\"move\",\"ids\":[1],\"vector\":[2000000,0,0]}", "1 km")]
        [InlineData("{\"action\":\"delete\",\"ids\":[1],\"vector\":[1,0,0]}", "only for action=move")]
        [InlineData("{\"action\":\"delete\",\"ids\":[1],\"commit\":true}", "commit")]
        public void What_if_refuses_what_it_cannot_rehearse(string whatIf, string expected)
        {
            Assert.False(GeometryWhatIfRules.TryParse(JToken.Parse(whatIf), 304.8, out _, out string e));
            Assert.Contains(expected, e);
        }

        [Fact]
        public void The_rollback_proof_sees_a_number_off_by_more_than_the_tolerance_and_nothing_smaller()
        {
            JToken before = JToken.Parse("[{\"element_id\":1,\"bounding_box\":{\"min\":[0,0,0],\"max\":[1000,200,3000]}}]");
            JToken same = JToken.Parse("[{\"element_id\":1,\"bounding_box\":{\"min\":[0,0,0],\"max\":[1000.005,200,3000]}}]");
            JToken moved = JToken.Parse("[{\"element_id\":1,\"bounding_box\":{\"min\":[0,0,0],\"max\":[1000.5,200,3000]}}]");
            JToken gone = JToken.Parse("[]");
            JToken nulled = JToken.Parse("[{\"element_id\":1,\"bounding_box\":null}]");
            Assert.Empty(GeometryWhatIfRules.Differences(before, same, 0.01));
            Assert.Equal("[0].bounding_box.max[0]: 1000 -> 1000.5", GeometryWhatIfRules.Differences(before, moved, 0.01).Single());
            Assert.Contains("1 items -> 0", GeometryWhatIfRules.Differences(before, gone, 0.01).Single());
            Assert.Contains("vanished", GeometryWhatIfRules.Differences(before, nulled, 0.01).Single());
        }
    }

    public class OpeningSplitRulesTests
    {
        private const double Mm = 1 / 304.8;

        private static OpeningSpan O(long id, double centreMm, double widthMm) =>
            new OpeningSpan { Id = id, Center = centreMm * Mm, Width = widthMm * Mm };

        private static double[][] Spans(OpeningSplitPlan p) =>
            p.Pieces.Select(x => new[] { System.Math.Round(x.Start / Mm, 3), System.Math.Round(x.End / Mm, 3) }).ToArray();

        [Fact]
        public void A_5000_wall_with_a_900_door_at_1500_and_a_1200_window_at_3500_gives_five_pieces()
        {
            // door 1050..1950, window 2900..4100 -> sides 0..1050, 1950..2900, 4100..5000.
            OpeningSplitPlan p = OpeningSplitRules.Plan(5000 * Mm, new List<OpeningSpan> { O(20, 3500, 1200), O(10, 1500, 900) });
            Assert.True(p.Ok, p.Refusal);
            Assert.Equal(new[] { new[] { 0.0, 1050 }, new[] { 1050.0, 1950 }, new[] { 1950.0, 2900 }, new[] { 2900.0, 4100 }, new[] { 4100.0, 5000 } }, Spans(p));
            Assert.Equal(new[] { false, true, false, true, false }, p.Pieces.Select(x => x.IsOpening).ToArray());
            Assert.Equal(new long[] { 10 }, p.Pieces[1].OpeningIds.ToArray());
            Assert.Equal(new long[] { 20 }, p.Pieces[3].OpeningIds.ToArray());
            Assert.Equal(5000, p.Pieces.Sum(x => x.Length) / Mm, 6);
        }

        [Fact]
        public void Openings_closer_than_50_mm_share_one_opening_piece()
        {
            // 1000..2000 and 2030..2830: a 30 mm mullion is not a wall piece.
            OpeningSplitPlan p = OpeningSplitRules.Plan(4000 * Mm, new List<OpeningSpan> { O(1, 1500, 1000), O(2, 2430, 800) });
            Assert.True(p.Ok, p.Refusal);
            Assert.Equal(new[] { new[] { 0.0, 1000 }, new[] { 1000.0, 2830 }, new[] { 2830.0, 4000 } }, Spans(p));
            Assert.Equal(new long[] { 1, 2 }, p.Pieces[1].OpeningIds.ToArray());
            Assert.Single(p.Notes);
        }

        [Fact]
        public void A_side_shorter_than_50_mm_at_a_wall_end_stays_in_the_opening_piece()
        {
            // door 20..920 in a 3000 wall: no 20 mm sliver at the start.
            OpeningSplitPlan p = OpeningSplitRules.Plan(3000 * Mm, new List<OpeningSpan> { O(1, 470, 900) });
            Assert.True(p.Ok, p.Refusal);
            Assert.Equal(new[] { new[] { 0.0, 920 }, new[] { 920.0, 3000 } }, Spans(p));
            Assert.True(p.Pieces[0].IsOpening);
        }

        [Fact]
        public void The_plan_refuses_what_it_cannot_split_soundly()
        {
            Assert.Contains("no door or window", OpeningSplitRules.Plan(3000 * Mm, new List<OpeningSpan>()).Refusal);
            Assert.Contains("runs past the end", OpeningSplitRules.Plan(3000 * Mm, new List<OpeningSpan> { O(1, 2800, 900) }).Refusal);
            Assert.Contains("no readable width", OpeningSplitRules.Plan(3000 * Mm, new List<OpeningSpan> { O(1, 1000, 0) }).Refusal);
            Assert.Contains("whole wall", OpeningSplitRules.Plan(900 * Mm, new List<OpeningSpan> { O(1, 450, 900) }).Refusal);
        }

        [Fact]
        public void Piece_at_finds_the_piece_containing_a_point()
        {
            OpeningSplitPlan p = OpeningSplitRules.Plan(5000 * Mm, new List<OpeningSpan> { O(10, 1500, 900) });
            Assert.Equal(1, OpeningSplitRules.PieceAt(p.Pieces, 1500 * Mm));
            Assert.Equal(2, OpeningSplitRules.PieceAt(p.Pieces, 4000 * Mm));
            Assert.Equal(-1, OpeningSplitRules.PieceAt(p.Pieces, 6000 * Mm));
        }
    }
}

namespace Horizun.Core.Tests
{
    public class WhatIfWiringTests
    {
        private static string Source(string file)
        {
            string dir = System.AppContext.BaseDirectory;
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Directory.Build.props"))) dir = System.IO.Path.GetDirectoryName(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "src", "Horizun.Revit", "Commands", file));
        }

        // what_if lives in a partial file of horizun_verify_changes (a writing-classified
        // tool, gated like every write); ExecutePythonGateTests scans only *Command.cs, so
        // the same guarantees are asserted here by name.
        [Fact]
        public void What_if_is_gated_refused_on_workshared_and_always_rolled_back()
        {
            string text = Source("VerifyChangesWhatIf.cs");
            string main = Source("VerifyChangesCommand.cs");
            int gate = main.IndexOf("DocumentGate.ForMutation", System.StringComparison.Ordinal);
            int call = main.IndexOf("RunWhatIf(", System.StringComparison.Ordinal);
            Assert.True(gate >= 0 && call > gate, "what_if must run after the mutation gate");
            Assert.Contains("IsWorkshared", text);
            Assert.DoesNotContain("Transaction(", Source("MeasureGeometryCommand.cs"));
            Assert.Contains("finally", text);
            Assert.Contains("Guard.RollBack(group)", text);
            Assert.DoesNotContain("group.Commit(", text);
            Assert.DoesNotContain("Assimilate(", text);
            Assert.DoesNotContain("CommandResult.Fail(", text);
            Assert.Contains("rolled_back_verified", text);
        }

        [Fact]
        public void Split_at_openings_is_gated_and_rehearses_before_it_writes()
        {
            string main = Source("SplitByLevelCommand.cs");
            string openings = Source("SplitByLevelCommand.Openings.cs");
            int gate = main.IndexOf("DocumentGate.ForMutation", System.StringComparison.Ordinal);
            int branch = main.IndexOf("SplitAtOpenings(", System.StringComparison.Ordinal);
            Assert.True(gate >= 0 && branch > gate, "split_at=openings must branch after the document gate");
            Assert.True(openings.IndexOf("RequireConfirmation", System.StringComparison.Ordinal) <
                        openings.IndexOf("new Transaction(", System.StringComparison.Ordinal));
            Assert.Contains("?? true", openings);   // dry_run defaults to true
        }
    }
}
