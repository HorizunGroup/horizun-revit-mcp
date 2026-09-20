// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A PAIR OF FACES WITH NOTHING HATCHED BETWEEN THEM IS NOT A WALL.
//
// MEASURED on the units plan of a permit set: two stud walls back to back with a
// 152 mm plumbing chase between them were read as ONE 152 mm wall in the chase,
// and the two real walls were left unread. The architect's hatch says which
// strips are material. These pin the boundary decoding, the placement of a hatch
// through the blocks that hold it, the rule key, and the pairing that now leaves
// the chase's faces free for the walls on either side.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadSolidHatchTests
    {
        private static string T(params string[] fields) { return string.Join("\t", fields); }

        private static string Rect(double x0, double y0, double x1, double y1)
        {
            string P(double x, double y) => x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                                            y.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "92:1.000000;93:4.000000;" +
                   "72:1.000000;10:" + P(x0, y0) + ";11:" + P(x1, y0) + ";" +
                   "72:1.000000;10:" + P(x1, y0) + ";11:" + P(x1, y1) + ";" +
                   "72:1.000000;10:" + P(x1, y1) + ";11:" + P(x0, y1) + ";" +
                   "72:1.000000;10:" + P(x0, y1) + ";11:" + P(x0, y0) + ";97:0.000000";
        }

        [Fact]
        public void Line_polyline_and_arc_boundaries_decode_and_an_ellipse_edge_is_partial()
        {
            bool partial;
            List<List<CadPoint>> lines = CadHatchLoops.Parse(Rect(0, 0, 10, 5), 2.0, out partial);
            Assert.False(partial);
            Assert.Single(lines);
            Assert.Contains(lines[0], p => p.X == 20 && p.Y == 10);

            string poly = "92:7.000000;72:0.000000;73:1.000000;93:3.000000;10:0,0;10:4,0;10:0,4;97:0.000000";
            Assert.Equal(3, CadHatchLoops.Parse(poly, 1.0, out partial).Single().Count);
            Assert.False(partial);

            string arc = "92:1.000000;93:2.000000;72:1.000000;10:-1,0;11:1,0;" +
                         "72:2.000000;10:0,0;40:1.000000;50:0.000000;51:180.000000;73:1.000000;97:0.000000";
            List<CadPoint> ring = CadHatchLoops.Parse(arc, 1.0, out partial).Single();
            Assert.False(partial);
            Assert.Contains(ring, p => Math.Abs(p.X) < 1e-9 && Math.Abs(p.Y - 1) < 1e-9);   // the top of the arc

            string ellipse = "92:1.000000;93:1.000000;72:3.000000;10:0,0;11:1,0;40:0.5;50:0;51:360;73:1;97:0";
            CadHatchLoops.Parse(ellipse, 1.0, out partial);
            Assert.True(partial);
            CadHatchLoops.Parse("92:1.000000;93:2.000000;72:1.000000;10:0,0", 1.0, out partial);
            Assert.True(partial);
        }

        [Fact]
        public void The_extractor_keeps_the_boundary_and_still_counts_the_hatch_as_unmodelled()
        {
            string[] lines =
            {
                T("H", "dwg", "SAMPLE.dwg"), T("H", "insunits", "1"),
                T("E", "*Model_Space", "1A", "HATCH", "A-WALL-PATT", "FP_2", "0.000000", "1.000000", Rect(0, 0, 10, 5)),
                T("H", "done", "1")
            };
            CadDwgReading r = CadDwgExtract.Parse(lines);
            CadIrEntity e = r.Entities.Single();
            Assert.Equal(CadEntityKind.Unclassified, e.Kind);
            Assert.Equal("HATCH", e.UnmodelledType);
            Assert.Equal("FP_2", e.HatchPattern);
            Assert.Contains(e.HatchLoops.Single(), p => Math.Abs(p.X - 254) < 1e-9 && Math.Abs(p.Y - 127) < 1e-9);
            Assert.Empty(e.Points);
            Assert.Contains("((= typ \"HATCH\")", CadDwgScript.Forms.First(f => f.Contains("(defun hz-entity")));
        }

        private static CadIrEntity Hatch(string layer, string pattern, string owner, params List<CadPoint>[] loops)
        {
            var e = new CadIrEntity
            {
                Id = "h" + Guid.NewGuid().ToString("N").Substring(0, 6), Handle = "H" + pattern,
                Layer = layer, HatchPattern = pattern, HatchLoops = loops.ToList(),
                Space = owner == null ? "model" : null
            };
            if (owner != null) e.BlockPath.Add(owner);
            return e;
        }

        private static List<CadPoint> Box(double x0, double y0, double x1, double y1) => new List<CadPoint>
        {
            new CadPoint(x0, y0), new CadPoint(x1, y0), new CadPoint(x1, y1), new CadPoint(x0, y1)
        };

        [Fact]
        public void A_hatch_inside_a_block_is_placed_with_the_blocks_turn_reflection_and_scale()
        {
            var insert = new CadIrEntity
            {
                Id = "i1", Handle = "I1", Kind = CadEntityKind.BlockInstance, Layer = "0", BlockName = "PLAN",
                Space = "model", RotationRadians = Math.PI / 2, ScaleX = -2, ScaleY = 2
            };
            insert.Points.Add(new CadPoint(1000, 0));
            CadIrEntity inBlock = Hatch("PLAN|A-WALL-PATT", "FP_4", "PLAN", Box(10, 0, 20, 5));
            CadIrEntity other = Hatch("PLAN|A-FURN", "ANSI31", "PLAN", Box(-500, -500, 500, 500));
            var all = new List<CadIrEntity> { insert, inBlock, other };
            CadPlacementReading placed = CadBlockPlacement.Place(all);
            CadSolidHatch solid = CadSolidHatch.Build(all, placed, new[] { "*|A-WALL-PATT" }, p => p);

            Assert.Equal(1, solid.Hatches);
            Assert.Equal(1, solid.PlacedHatches);
            // local (15, 2.5): scaled x2 -> (30, 5), reflected -> (-30, 5), turned 90 -> (-5, -30), moved -> (995, -30)
            Assert.Equal(new[] { "FP_4" }, solid.PatternsAt(new CadPoint(995, -30), new[] { "*|A-WALL-PATT" }));
            Assert.Empty(solid.PatternsAt(new CadPoint(995, 30), new[] { "*|A-WALL-PATT" }));
            Assert.Empty(solid.PatternsAt(new CadPoint(1000, 0), new[] { "*|A-WALL-PATT" }));

            // The placement's own frame composes the same way a nested insertion does.
            CadPlacedBlock root = placed.Placed.Single();
            CadPoint q = root.Place(new CadPoint(15, 2.5));
            Assert.Equal(995, q.X, 9);
            Assert.Equal(-30, q.Y, 9);
        }

        [Fact]
        public void An_island_is_a_hole_and_paper_space_is_never_evidence()
        {
            CadIrEntity ring = Hatch("A-WALL-PATT", "FP_2", null, Box(0, 0, 100, 100), Box(40, 40, 60, 60));
            CadIrEntity sheet = Hatch("A-WALL-PATT", "FP_2", null, Box(200, 0, 300, 100));
            sheet.Space = "paper";
            CadSolidHatch solid = CadSolidHatch.Build(new List<CadIrEntity> { ring, sheet }, null,
                                                      new[] { "A-WALL-PATT" }, p => p);
            Assert.NotEmpty(solid.PatternsAt(new CadPoint(20, 20), null));
            Assert.Empty(solid.PatternsAt(new CadPoint(50, 50), null));
            Assert.Empty(solid.PatternsAt(new CadPoint(250, 50), null));
            Assert.Equal(1, solid.PlacedHatches);
        }

        private static CadRequirementSet Set(string solid, string from = "double_lines")
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'w', 'version': '1.0.0' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5,
                              'revision_compare_mm': 25 },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic - 6\""', 'level': 'Level 1', 'height_mm': 2700,
                           'join_rule': 'none',
                           'geometry': { 'from': 'FROM', 'min_thickness_mm': 60, 'max_thickness_mm': 400 SOLID } } ]
            }";
            return CadRequirementSet.Load(JObject.Parse(doc.Replace("SOLID", solid).Replace("FROM", from)
                                                           .Replace('\'', '"')));
        }

        [Fact]
        public void The_rule_key_is_a_list_of_layer_patterns_for_double_lines_only()
        {
            Assert.Equal(new[] { "*|A-WALL-PATT" },
                         Set(", 'solid_hatch_layers': ['*|A-WALL-PATT']").Rules.Single().Geometry.SolidHatchLayers);
            Assert.Empty(Set("").Rules.Single().Geometry.SolidHatchLayers);
            Assert.Throws<CadRequirementSetException>(() => Set(", 'solid_hatch_layers': []"));
            Assert.Throws<CadRequirementSetException>(() => Set(", 'solid_hatch_layers': 'A-WALL-PATT'"));
            Assert.Throws<CadRequirementSetException>(() => Set(", 'solid_hatch_layers': ['']"));
            Assert.Throws<CadRequirementSetException>(() => Set(", 'solid_hatch_layers': ['X']", "single_lines"));
        }

        // Two 152.4 mm stud walls back to back with a 152.4 mm chase between them:
        // four lines, the outer two longer, the inner two exactly the same length.
        private static List<CadSegment> BackToBack() => new List<CadSegment>
        {
            new CadSegment(new CadPoint(-200, 0), new CadPoint(1200, 0), "A-WALL", CadCurveKind.Line, 0),
            new CadSegment(new CadPoint(0, 152.4), new CadPoint(1000, 152.4), "A-WALL", CadCurveKind.Line, 0),
            new CadSegment(new CadPoint(0, 304.8), new CadPoint(1000, 304.8), "A-WALL", CadCurveKind.Line, 0),
            new CadSegment(new CadPoint(-200, 457.2), new CadPoint(1200, 457.2), "A-WALL", CadCurveKind.Line, 0)
        };

        private static CadSolidHatch StudHatch() => CadSolidHatch.Build(new List<CadIrEntity>
        {
            Hatch("A-WALL-PATT", "FP_4", null, Box(-200, 0, 1200, 152.4)),
            Hatch("A-WALL-PATT", "FP_4", null, Box(-200, 304.8, 1200, 457.2))
        }, null, new[] { "A-WALL-PATT" }, p => p);

        private static List<double> Centres(CadInterpretation i) =>
            i.Candidates.Where(c => c.ProposedKind == "wall")
             .Select(c => Math.Round((c.Geometry[0].Y + c.Geometry[1].Y) / 2, 1)).OrderBy(y => y).ToList();

        [Fact]
        public void Lines_alone_read_the_chase_and_the_hatch_reads_the_two_walls()
        {
            CadInterpretation lines = CadInterpretationRules.Interpret(BackToBack(), Set(""), "sha");
            Assert.Contains(228.6, Centres(lines));
            Assert.DoesNotContain(76.2, Centres(lines));

            CadRequirementSet solidSet = Set(", 'solid_hatch_layers': ['A-WALL-PATT']");
            CadInterpretation hatched = CadInterpretationRules.Interpret(BackToBack(), solidSet, "sha", null, null,
                                                                        StudHatch());
            Assert.Equal(new[] { 76.2, 381.0 }, Centres(hatched));
            // The chase itself, and each pairing of the chase with the wall beside it.
            Assert.Equal(3, hatched.SolidVetoes.Count);
            JObject chase = hatched.SolidVetoes.Cast<JObject>()
                                   .Single(v => Math.Abs((double)v["pair"]["thickness_mm"] - 152.4) < 0.1);
            Assert.Equal("r-wall", (string)chase["rule"]);
            Assert.Equal(0, (int)chase["solid"]["hatched_samples"]);
            Assert.All(hatched.SolidVetoes, v => Assert.Equal(0, (int)v["solid"]["hatched"]));
            Assert.Contains(hatched.DoubleLineReasoning.Single()["pairs"],
                            p => (string)p["outcome"] == "skipped_no_hatched_material_between_faces");
        }

        [Fact]
        public void A_joint_between_two_walls_is_not_read_as_one_band()
        {
            // MEASURED: two 101.6 mm concrete walls with a 51 mm joint between them.
            // Samples at the thirds of a pair that spans the joint and one wall
            // miss the joint; the whole interior does not.
            var lines = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(1000, 0), "A-WALL", CadCurveKind.Line, 0),
                new CadSegment(new CadPoint(0, 101.6), new CadPoint(1000, 101.6), "A-WALL", CadCurveKind.Line, 0),
                new CadSegment(new CadPoint(0, 152.4), new CadPoint(1000, 152.4), "A-WALL", CadCurveKind.Line, 0),
                new CadSegment(new CadPoint(0, 254.0), new CadPoint(1000, 254.0), "A-WALL", CadCurveKind.Line, 0)
            };
            CadSolidHatch concrete = CadSolidHatch.Build(new List<CadIrEntity>
            {
                Hatch("A-WALL-PATT", "FP_2", null, Box(0, 0, 1000, 101.6)),
                Hatch("A-WALL-PATT", "FP_2", null, Box(0, 152.4, 1000, 254.0))
            }, null, new[] { "A-WALL-PATT" }, p => p);
            CadRequirementSet set = Set(", 'solid_hatch_layers': ['A-WALL-PATT']");
            CadInterpretation read = CadInterpretationRules.Interpret(lines, set, "sha", null, null, concrete);
            Assert.Equal(new[] { 50.8, 203.2 }, Centres(read));
            Assert.All(read.Candidates, c => Assert.Equal(101.6, c.ThicknessMm.Value, 1));
        }

        [Fact]
        public void The_finish_lines_at_the_faces_are_not_asked_to_be_hatched()
        {
            // 127 mm of hatched core inside 17.45 mm of unhatched finish on each side.
            var pair = new CadDoubleLine(new CadPoint(0, 80.95), new CadPoint(1000, 80.95), 161.9, "A-WALL",
                                         1000, 1, 0, 0, 1, 1000);
            CadSolidHatch core = CadSolidHatch.Build(new List<CadIrEntity>
            {
                Hatch("A-WALL-PATT", "FP_2", null, Box(-10, 17.45, 1010, 144.45))
            }, null, new[] { "A-WALL-PATT" }, p => p);
            CadSolidVerdict v = core.Judge(pair, new[] { "A-WALL-PATT" });
            Assert.Equal(5, v.Hatched);
            Assert.False(v.Void);
            Assert.True(v.Samples >= 5 * 13, "the interior is sampled every 10 mm at most");
        }

        [Fact]
        public void A_rule_that_asks_for_hatch_evidence_builds_nothing_without_it()
        {
            CadRequirementSet solidSet = Set(", 'solid_hatch_layers': ['A-WALL-PATT']");
            CadInterpretation none = CadInterpretationRules.Interpret(BackToBack(), solidSet, "sha");
            Assert.Empty(Centres(none));
            Assert.All(none.DoubleLineReasoning.Single()["pairs"],
                       p => Assert.Equal("skipped_no_solid_evidence", (string)p["outcome"]));
        }

        [Fact]
        public void Every_command_that_interprets_reads_the_same_hatch_first()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            Func<string, string> read = rel => System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName, rel.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
            foreach (string cmd in new[] { "PlanFromCadCommand.cs", "AuditCadModelCommand.cs", "PlanCadUpdateCommand.cs" })
            {
                string src = read(@"src\Horizun.Revit\Commands\" + cmd);
                int readAt = src.IndexOf("CadBlockSource.ReadSolid(", StringComparison.Ordinal);
                int interpretAt = src.IndexOf("CadInterpretationRules.Interpret(", StringComparison.Ordinal);
                Assert.True(readAt > 0 && readAt < interpretAt, cmd);
                Assert.Contains("solidHatch);", src.Substring(interpretAt, 260));
                Assert.Contains("\"solid_evidence_unread: \"", src);
            }
            string source = read(@"src\Horizun.Revit\Commands\CadBlockSource.cs");
            Assert.Contains("CadDwgReading reading = ReadDrawing(facts, set, callerPath, readTimeoutSeconds, report);",
                            source);
            Assert.Contains("JObject frame = FrameAgreement(anchors, harvest);", source);
        }
    }
}
