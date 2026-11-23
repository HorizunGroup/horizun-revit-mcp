using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanStructureCommandTests
    {
        private const double Ft = 304.8;
        private const long TargetLevel = 694;
        private const long OtherLevel = 695;
        private const long BeamType = 700;
        private const long NotALevel = BeamType;

        [Fact]
        public void Casa_v6_reproduces_the_nine_field_pairs_and_plans_clear_spans()
        {
            // The grid, existing boxes and previous plan come from the 19-Nov capture.
            // New boxes are estimated from planned endpoints and the 300 mm centered section.
            var data = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "casa-v6-estructura4.json")));
            double width = (double)data["proposed_width_mm"], datum = (double)data["datum_mm"];
            Document doc = Fixture(width);
            foreach (long id in new[] { 1L, 2L, 3L }) doc.Elements.Remove(id);
            foreach (JToken grid in data["grids"])
                AddGrid(doc, (long)grid["id"], (string)grid["name"], Point(grid["start"]), Point(grid["end"]));
            foreach (JToken row in data["beams"])
            {
                long id = (long)row["id"];
                var beam = AddBeam(doc, id: id);
                beam.Location = null; // The captured model box is the source of coverage.
                beam.LevelId = new ElementId((string)row["level"] == "Nivel 2" ? TargetLevel : OtherLevel);
                beam.TestUniqueId = (string)row["unique_id"];
                beam.BoundingBox = Box(row);
            }
            foreach (JToken row in data["columns"])
            {
                long id = (long)row["id"];
                doc.Elements[id] = new FamilyInstance
                {
                    Id = new ElementId(id), TestCategory = BuiltInCategory.OST_StructuralColumns, BoundingBox = Box(row)
                };
            }

            var existing = data["beams"].Where(b => (string)b["level"] == "Nivel 2" && InBand(b)).ToArray();
            var axes = data["grids"].Select(grid =>
            {
                var a = grid["start"].ToObject<double[]>();
                var b = grid["end"].ToObject<double[]>();
                int fixedAxis = Math.Abs(a[0] - b[0]) < 0.01 ? 0 : 1;
                int along = 1 - fixedAxis;
                return (Name: (string)grid["name"], Fixed: fixedAxis, Along: along, At: a[fixedAxis],
                        Start: Math.Min(a[along], b[along]), End: Math.Max(a[along], b[along]));
            }).ToArray();
            var measuredPairs = new[]
            {
                (1428356L, 1372162L), (1428356L, 1372165L), (1428358L, 1377215L),
                (1428360L, 1372166L), (1428360L, 1372169L), (1428362L, 1372166L),
                (1428362L, 1372169L), (1428364L, 1372168L), (1428364L, 1372169L)
            };
            var previous = data["previous_plan"].ToArray();
            var capturedPlanPairs = Pairs(previous.Select((row, i) => (1428354L + 2 * i, row)));
            Assert.Equal(5, measuredPairs.Intersect(capturedPlanPairs).Count());

            // The captured plan predates the live post-apply readback. Two of its spans
            // already reproduce their live pairs. Reconstruct the other three box extents
            // from the recorded overlaps and the captured grids and existing boxes.
            Assert.Equal(measuredPairs, Pairs(new[]
            {
                (1428356L, previous[1]),
                (1428358L, ReplaySpan("B'", 150.02, (double)previous[2]["end"][1])),
                (1428360L, previous[3]),
                (1428362L, ReplaySpan("D", -150, 100)),
                (1428364L, ReplaySpan("D", 5900, 6000))
            }));

            JObject response = Plan(doc);
            Assert.NotEmpty((JArray)response["planned"]);
            Assert.Empty(Pairs(response["planned"].Select((row, i) => ((long)i, row))));
            Assert.NotEmpty((JArray)response["omitted_existing_beams"]);
            Assert.All((JArray)response["omitted_existing_beams"], row =>
            {
                Assert.Equal("already_present", (string)row["code"]);
                Assert.False(string.IsNullOrWhiteSpace((string)row["reason"]));
                var beam = Assert.Single(existing, b => (long)b["id"] == (long)row["beam_id"]);
                Assert.Equal((string)beam["unique_id"], (string)row["unique_id"]);
            });
            Assert.Null(response["coverage_unmeasured_spans"]);

            // Independently account for all free intervals using the EST-05c box projections.
            foreach (var axis in axes)
            {
                var crosses = axes.Where(other => other.Fixed != axis.Fixed &&
                    Between(axis.At, other.Start, other.End) && Between(other.At, axis.Start, axis.End))
                    .Select(other => other.At).OrderBy(at => at).ToArray();
                var supports = data["columns"].Where(InBand).Where(b => OnAxis(b, axis.Fixed, axis.At))
                    .Select(b => ReadInterval(b, axis.Along)).ToArray();
                var occupants = existing.Where(b => OnAxis(b, axis.Fixed, axis.At))
                    .Select(b => ReadInterval(b, axis.Along)).Concat(supports)
                    .Concat(response["planned"].Where(p => (string)p["grid"] == axis.Name)
                        .Select(p => OrderedEnds(p, axis.Along))).OrderBy(pair => pair.Start).ToArray();
                foreach (JToken omission in response["omitted"].Where(p => (string)p["grid"] == axis.Name))
                {
                    var (a, b) = OrderedEnds(omission, axis.Along);
                    Assert.True(b - a <= 150.01 && supports.Any(s =>
                        (Between(a, s.Start, s.End) && Between(b, s.Start, s.End)) ||
                        Between(s.Start, a, b) || Between(s.End, a, b)));
                }
                for (int i = 1; i < crosses.Length; i++)
                {
                    double cursor = crosses[i - 1], end = crosses[i];
                    foreach (var occupant in occupants)
                    {
                        if (occupant.End <= cursor) continue;
                        CheckGap(cursor, Math.Min(end, occupant.Start));
                        cursor = Math.Max(cursor, occupant.End);
                        if (cursor >= end) break;
                    }
                    CheckGap(cursor, end);
                }

                void CheckGap(double a, double b)
                {
                    if (b - a <= 0.01) return;
                    Assert.True(b - a <= 150.01 && supports.Any(s => Between(s.Start, a, b) || Between(s.End, a, b)),
                        $"Grid {axis.Name} leaves {b - a:F4} mm free at {a:F4}–{b:F4}.");
                }
            }

            JToken ReplaySpan(string name, double start, double end)
            {
                double x = axes.Single(a => a.Name == name).At;
                return new JObject
                {
                    ["grid"] = name, ["start"] = new JArray(x, start, datum), ["end"] = new JArray(x, end, datum)
                };
            }

            (long, long)[] Pairs(IEnumerable<(long Id, JToken Row)> plan) => plan.SelectMany(span =>
            {
                JToken row = span.Row;
                var fixedAxis = axes.Single(a => a.Name == (string)row["grid"]).Fixed;
                var box = new JObject { ["min"] = new JArray(), ["max"] = new JArray() };
                for (int i = 0; i < 2; i++)
                {
                    var ends = OrderedEnds(row, i);
                    double half = i == fixedAxis ? width / 2 : 0;
                    ((JArray)box["min"]).Add(ends.Start - half);
                    ((JArray)box["max"]).Add(ends.End + half);
                }
                return existing.Where(b => axes.Any(a => OnAxis(b, a.Fixed, a.At) && OnAxis(box, a.Fixed, a.At) &&
                    Math.Min(a.End, Math.Min((double)b["max"][a.Along], (double)box["max"][a.Along])) -
                    Math.Max(a.Start, Math.Max((double)b["min"][a.Along], (double)box["min"][a.Along])) > 150.01))
                    .Select(b => (span.Id, (long)b["id"]));
            }).OrderBy(pair => pair.Item1).ThenBy(pair => pair.Item2).ToArray();

            bool InBand(JToken b) => (double)b["min"][2] <= datum + 10 && (double)b["max"][2] >= datum - 1000;
            static bool Between(double value, double min, double max) => value >= min - 0.01 && value <= max + 0.01;
            static bool OnAxis(JToken b, int fixedAxis, double at) =>
                Between(at, (double)b["min"][fixedAxis], (double)b["max"][fixedAxis]);
            static (double Start, double End) ReadInterval(JToken box, int axis) =>
                ((double)box["min"][axis], (double)box["max"][axis]);
            static (double Start, double End) OrderedEnds(JToken row, int axis)
            {
                double a = (double)row["start"][axis], b = (double)row["end"][axis];
                return (Math.Min(a, b), Math.Max(a, b));
            }
            static XYZ Point(JToken xyz) => new XYZ((double)xyz[0] / Ft, (double)xyz[1] / Ft, (double)xyz[2] / Ft);
            static BoundingBoxXYZ Box(JToken row) => new BoundingBoxXYZ { Min = Point(row["min"]), Max = Point(row["max"]) };
        }

        [Theory]
        [InlineData(OtherLevel, 2350, 2850, 5820, 6000)]
        [InlineData(TargetLevel, 1800, 1999.99, 5820, 6000)]
        [InlineData(TargetLevel, 3010.01, 3300, 5820, 6000)]
        [InlineData(TargetLevel, 2350, 2850, 150.0000011, 6000)]
        [InlineData(TargetLevel, 2350, 2850, 150.01, 6000)]
        [InlineData(TargetLevel, 2350, 2850, 150.02, 5849.98)]
        [InlineData(TargetLevel, 1850, 2000, 5820, 180)]
        [InlineData(TargetLevel, 3010, 3300, 5820, 180)]
        public void Only_boxes_in_the_target_level_band_cover_more_than_the_tolerated_overlap(
            long level, double minZ, double maxZ, double end, double freeLength)
        {
            Document doc = Fixture(widthMm: 150);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(level);
            beam.BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ(0, -150 / Ft, minZ / Ft), Max = new XYZ(end / Ft, 150 / Ft, maxZ / Ft)
            };

            JObject result = Plan(doc);
            JToken free = Assert.Single((JArray)result["planned"]);
            Assert.Equal(freeLength, (double)free["end"][0] - (double)free["start"][0], 6);
            if (freeLength == 6000)
                Assert.Empty((JArray)result["omitted_existing_beams"]);
            else
            {
                JToken omission = Assert.Single((JArray)result["omitted_existing_beams"]);
                Assert.Equal(701L, (long)omission["beam_id"]);
                Assert.Equal(beam.UniqueId, (string)omission["unique_id"]);
                Assert.Equal(end, (double)omission["end"][0], 6);
            }
        }

        [Fact]
        public void A_short_gap_between_boxes_is_kept_when_no_column_touches_it()
        {
            Document doc = Fixture(widthMm: 150);
            AddBox(701, 0, 1000);
            AddBox(702, 1100, 6000);

            JObject result = Plan(doc);
            JToken free = Assert.Single((JArray)result["planned"]);
            Assert.Equal(1000, (double)free["start"][0], 6);
            Assert.Equal(1100, (double)free["end"][0], 6);
            Assert.Empty((JArray)result["omitted"]);
            Assert.Empty((JArray)Plan(doc, minSpanMm: 300)["planned"]);

            void AddBox(long id, double start, double end)
            {
                var beam = AddBeam(doc, id: id);
                beam.LevelId = new ElementId(TargetLevel);
                beam.BoundingBox = new BoundingBoxXYZ
                {
                    Min = new XYZ(start / Ft, -75 / Ft, 2350 / Ft), Max = new XYZ(end / Ft, 75 / Ft, 2850 / Ft)
                };
            }
        }

        [Theory]
        [InlineData(-0.005)]
        [InlineData(0)]
        [InlineData(0.005)]
        public void A_transverse_box_touching_the_crossing_clears_the_full_axis_tolerance(double end)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ(-300 / Ft, -500 / Ft, 2350 / Ft), Max = new XYZ(end / Ft, 500 / Ft, 2850 / Ft)
            };

            JObject result = Plan(doc);
            JToken free = Assert.Single((JArray)result["planned"]);
            Assert.InRange((double)free["start"][0], 0.01000001, 0.011);
            Assert.Equal(701L, (long)Assert.Single((JArray)result["omitted_existing_beams"])["beam_id"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_oblique_crossing_is_trimmed_and_named_when_its_box_overlap_is_too_long(bool atEnd)
        {
            Document doc = Fixture(widthMm: 300);
            double crossing = atEnd ? 6000 : 0;
            double sign = atEnd ? -1 : 1;
            AddGrid(doc, atEnd ? 3 : 2, "Oblique",
                new XYZ((crossing - 400) / Ft, -400 / Ft, 0),
                new XYZ((crossing + 400) / Ft, 400 / Ft, 0));
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ((crossing - (atEnd ? 130 : 400)) / Ft, -400 / Ft, 2350 / Ft),
                Max = new XYZ((crossing + (atEnd ? 400 : 130)) / Ft, 400 / Ft, 2850 / Ft)
            };

            JObject result = Plan(doc);
            JToken free = Assert.Single((JArray)result["planned"]);
            double trim = sign * ((double)free[atEnd ? "end" : "start"][0] - crossing);
            double overlap = (130 - trim) * Math.Sqrt(2);
            Assert.InRange(overlap, 149.9, 150.01);
            JToken omission = Assert.Single((JArray)result["omitted_existing_beams"]);
            Assert.Equal("Oblique", (string)omission["crossing_grid"]);
            Assert.Contains("'Oblique'", (string)omission["reason"]);
            Assert.Equal(701L, (long)omission["beam_id"]);
            Assert.Equal(beam.UniqueId, (string)omission["unique_id"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Coincident_transverse_grids_are_all_checked_at_a_bay_endpoint(bool atEnd)
        {
            Document doc = Fixture(widthMm: 300);
            double crossing = atEnd ? 6000 : 0;
            double outward = atEnd ? 1 : -1;
            double x = crossing / Ft;
            AddGrid(doc, 1, "1", new XYZ(-20 / Ft, 0, 0), new XYZ(6020 / Ft, 0, 0));
            AddGrid(doc, atEnd ? 3 : 2, atEnd ? "A-short" : "Z-short",
                new XYZ(x, -50 / Ft, 0), new XYZ(x, 50 / Ft, 0));
            AddGrid(doc, 4, "Oblique", new XYZ((crossing - 400) / Ft, -400 / Ft, 0),
                new XYZ((crossing + 400) / Ft, 400 / Ft, 0));
            AddGrid(doc, 5, "Coincident oblique", new XYZ((crossing - 350) / Ft, -350 / Ft, 0),
                new XYZ((crossing + 350) / Ft, 350 / Ft, 0));
            double intermediate = (crossing + outward * 4) / Ft;
            AddGrid(doc, 6, "Intermediate", new XYZ(intermediate, -50 / Ft, 0), new XYZ(intermediate, 50 / Ft, 0));
            // Consecutive gaps are 4 mm, but this oblique crosses 8 mm from the bay endpoint.
            double distant = crossing + outward * 8;
            AddGrid(doc, 7, "Distant oblique", new XYZ((distant - 400) / Ft, -400 / Ft, 0),
                new XYZ((distant + 400) / Ft, 400 / Ft, 0));
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ((crossing - (atEnd ? 130 : 400)) / Ft, -400 / Ft, 2350 / Ft),
                Max = new XYZ((crossing + (atEnd ? 400 : 130)) / Ft, 400 / Ft, 2850 / Ft)
            };

            JObject result = Plan(doc);
            JToken free = Assert.Single((JArray)result["planned"], row => (string)row["grid"] == "1");
            double trim = Math.Abs((double)free[atEnd ? "end" : "start"][0] - crossing);
            Assert.InRange((130 - trim) * Math.Sqrt(2), 149.9, 150.01);
            var omissions = result["omitted_existing_beams"].Where(row => (string)row["grid"] == "1").ToArray();
            Assert.Equal(new[] { "Coincident oblique", "Oblique" },
                omissions.Select(row => (string)row["crossing_grid"]).OrderBy(name => name, StringComparer.Ordinal));
            Assert.All(omissions, omission =>
            {
                Assert.Equal(701L, (long)omission["beam_id"]);
                Assert.Equal(beam.UniqueId, (string)omission["unique_id"]);
            });
        }

        [Theory]
        [InlineData(150.0000011, false)]
        [InlineData(150.01, false)]
        [InlineData(150.02, true)]
        public void Cutting_a_box_on_an_oblique_grid_does_not_inflate_the_measured_overlap(double overlap, bool trimmed)
        {
            Document doc = Fixture(widthMm: 150);
            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(6000 / Ft, 6000 / Ft, 0));
            AddGrid(doc, 3, "B", new XYZ(6000 / Ft, 5900 / Ft, 0), new XYZ(6000 / Ft, 6100 / Ft, 0));
            double edge = overlap / (Math.Sqrt(2) * Ft);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ(0, 0, 2350 / Ft),
                Max = new XYZ(edge, edge, 2850 / Ft)
            };

            JObject result = Plan(doc);
            JToken free = Assert.Single((JArray)result["planned"]);
            Assert.Equal(trimmed ? edge * Ft : 0, (double)free["start"][0], 6);
            Assert.Equal(trimmed ? 1 : 0, ((JArray)result["omitted_existing_beams"]).Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_whole_100_mm_bay_is_omitted_only_with_support_or_an_explicit_minimum(bool hasColumn)
        {
            Document doc = Fixture(widthMm: 150);
            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(100 / Ft, 0, 0));
            AddGrid(doc, 3, "B", new XYZ(100 / Ft, -1, 0), new XYZ(100 / Ft, 1, 0));
            if (hasColumn)
                doc.Elements[900] = new FamilyInstance
                {
                    Id = new ElementId(900), TestCategory = BuiltInCategory.OST_StructuralColumns,
                    BoundingBox = new BoundingBoxXYZ
                    {
                        Min = new XYZ(-50 / Ft, -50 / Ft, 0),
                        Max = new XYZ(20 / Ft, 50 / Ft, 3000 / Ft)
                    }
                };

            JObject result = Plan(doc);
            Assert.Equal(100, (double)Assert.Single((JArray)result[hasColumn ? "omitted" : "planned"])["length_mm"]);
            Assert.Empty((JArray)result[hasColumn ? "planned" : "omitted"]);
            JObject explicitMinimum = Plan(doc, minSpanMm: 150);
            Assert.Empty((JArray)explicitMinimum["planned"]);
            Assert.Equal("span_too_short", (string)Assert.Single((JArray)explicitMinimum["omitted"])["code"]);
        }

        [Fact]
        public void A_whole_bay_of_180_mm_is_planned_by_default()
        {
            Document doc = Fixture(widthMm: 150);
            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(180 / Ft, 0, 0));
            AddGrid(doc, 3, "B", new XYZ(180 / Ft, -1, 0), new XYZ(180 / Ft, 1, 0));

            JToken span = Assert.Single((JArray)Plan(doc)["planned"]);
            Assert.Equal(180, (double)span["length_mm"]);
        }

        [Theory]
        [InlineData(TargetLevel, OtherLevel, true)]
        [InlineData(OtherLevel, TargetLevel, false)]
        [InlineData(-1L, TargetLevel, true)]
        [InlineData(NotALevel, OtherLevel, false)]
        public void The_command_uses_the_first_readable_reference_level(long levelId, long parameterLevelId, bool covered)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc, zMm: covered ? 3200 : 3000);
            beam.LevelId = levelId == -1 ? ElementId.InvalidElementId : new ElementId(levelId);
            beam.BuiltInParams[BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM] =
                new Parameter { Value = new ElementId(parameterLevelId) };

            JObject response = Plan(doc);

            // The curve sits exactly at the opposite level. The reference level decides membership.
            Assert.Equal(covered ? 1 : 0, (int)response["suppressed_existing_beams"]);
            Assert.Equal(covered ? 0 : 1, (int)response["planned_count"]);
            Assert.True((bool)response["has_assumptions"]);
            JToken assumption = Assert.Single(Assert.IsType<JArray>(response["assumptions"]));
            Assert.Equal("SUPUESTO", (string)assumption["flag"]);
            Assert.Contains("reference level", (string)assumption["what"]);
            if (covered)
                Assert.Null(response["next_arguments"]);
            else
            {
                JToken placement = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
                Assert.Equal(TargetLevel, (long)placement["level_id"]);
                Assert.Equal(3000, (double)placement["start"][2]);
                Assert.Equal(3000, (double)placement["end"][2]);
            }
        }

        [Fact]
        public void Partial_coverage_lists_measured_beams_and_the_exact_free_segments_in_the_plan()
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance first = AddBeam(doc, startMm: 200, endMm: 1600, id: 701);
            FamilyInstance second = AddBeam(doc, startMm: 1500, endMm: 2600, id: 702);
            first.LevelId = second.LevelId = new ElementId(TargetLevel);

            JObject response = Plan(doc, minSpanMm: 300);

            JObject partial = Assert.IsType<JObject>(Assert.Single(Assert.IsType<JArray>(response["partially_covered_spans"])));
            Assert.Equal(("1", "A", "B"), ((string)partial["grid"], (string)partial["from"], (string)partial["to"]));
            Assert.Equal(0, (double)partial["start"][0]);
            Assert.Equal(6000, (double)partial["end"][0], 6);
            Assert.Equal(2400, (double)partial["covered_mm"]);
            Assert.Equal(new[] { 701L, 702L }, partial["covering_beams"].Select(row => (long)row["beam_id"]));
            Assert.Equal(new[] { first.UniqueId, second.UniqueId }, partial["covering_beams"].Select(row => (string)row["unique_id"]));
            Assert.Equal(1, (int)response["suppressed_short_spans"]);
            Assert.Equal("partial", (string)response["coverage"]);
            JToken omitted = Assert.Single(Assert.IsType<JArray>(response["omitted"]));
            Assert.Equal("span_too_short", (string)omitted["code"]);
            Assert.Equal(200, (double)omitted["length_mm"]);
            JToken free = Assert.Single(Assert.IsType<JArray>(partial["planned_free_segments"]));
            Assert.Equal(3400, (double)free["length_mm"]);
            Assert.Equal(2600, (double)free["start"][0], 6);
            Assert.Equal(6000, (double)free["end"][0], 6);
            JToken placement = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
            Assert.True(JToken.DeepEquals(free["start"], placement["start"]));
            Assert.True(JToken.DeepEquals(free["end"], placement["end"]));
        }

        [Theory]
        [InlineData(1000, 2000)]
        [InlineData(1001, 2002)]
        public void Split_bays_produce_only_valid_creation_batches(int bayCount, int segmentCount)
        {
            Document doc = Fixture(widthMm: 300);
            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(bayCount * 2000 / Ft, 0, 0));
            for (int i = 0; i <= bayCount; i++)
            {
                double x = i * 2000 / Ft;
                AddGrid(doc, 2000 + i, $"G{i}", new XYZ(x, -1, 0), new XYZ(x, 1, 0));
                if (i < bayCount)
                    AddBeam(doc, startMm: i * 2000 + 600, endMm: i * 2000 + 1400, id: 10000 + i)
                        .LevelId = new ElementId(TargetLevel);
            }

            JObject response = Plan(doc);

            Assert.Equal(segmentCount, (int)response["planned_count"]);
            int limit = (int)Horizun.Contracts.Contract.Find("horizun_create_elements")
                .InputSchema["properties"]["elements"]["maxItems"];
            JToken[] batches;
            if (segmentCount <= limit)
            {
                Assert.Null(response["next_arguments_batches"]);
                batches = new[] { Assert.IsType<JObject>(response["next_arguments"]) };
            }
            else
            {
                Assert.Null(response["next_arguments"]);
                batches = Assert.IsType<JArray>(response["next_arguments_batches"]).ToArray();
                Assert.Equal(new[] { limit, segmentCount - limit },
                    batches.Select(batch => ((JArray)batch["arguments"]["elements"]).Count));
            }
            Assert.All(batches, batch =>
            {
                Assert.Equal("horizun_create_elements", (string)batch["tool"]);
                Assert.Equal(doc.Title, (string)batch["arguments"]["target_document"]);
                Assert.Equal("mm", (string)batch["arguments"]["units"]);
                Assert.InRange(((JArray)batch["arguments"]["elements"]).Count, 1, limit);
            });
            Assert.Equal(response["planned"].Select(row => ((double)row["start"][0], (double)row["end"][0])),
                batches.SelectMany(batch => batch["arguments"]["elements"])
                    .Select(row => ((double)row["start"][0], (double)row["end"][0])));
        }

        [Theory]
        [InlineData(StructuralType.Brace)]
        [InlineData(StructuralType.UnknownFraming)]
        public void A_non_beam_on_the_axis_produces_a_warning_without_claiming_coverage(StructuralType type)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance framing = AddBeam(doc, startMm: 1000, endMm: 4000);
            framing.StructuralType = type;
            framing.LevelId = new ElementId(TargetLevel);
            FamilyInstance offAxis = AddBeam(doc, offsetMm: 400, id: 702);
            offAxis.StructuralType = type;
            offAxis.LevelId = new ElementId(TargetLevel);

            JObject response = Plan(doc);

            JToken warning = Assert.Single(Assert.IsType<JArray>(response["warnings"]));
            Assert.Equal("non_beam_framing_on_axis", (string)warning["code"]);
            Assert.Equal("1", (string)warning["grid"]);
            Assert.Equal(type.ToString(), (string)warning["structural_type"]);
            Assert.Equal(framing.UniqueId, (string)warning["unique_id"]);
            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            Assert.Equal(6000, (double)Assert.Single((JArray)response["planned"])["length_mm"]);
        }

        [Theory]
        [InlineData(3000, 0, StructuralType.Brace, true)]
        [InlineData(3000, 0, StructuralType.Beam, false)]
        [InlineData(3000, 151, StructuralType.Brace, false)]
        [InlineData(6001, 0, StructuralType.Brace, false)]
        public void Vertical_framing_on_the_bay_warns_only_for_non_beams(double xMm, double yMm,
                                                                        StructuralType type, bool warns)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance framing = AddBeam(doc);
            framing.LevelId = new ElementId(TargetLevel);
            framing.StructuralType = type;
            framing.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(xMm / Ft, yMm / Ft, 2600 / Ft), new XYZ(xMm / Ft, yMm / Ft, 3400 / Ft))
            };

            JObject response = Plan(doc);

            if (warns)
            {
                JToken warning = Assert.Single(Assert.IsType<JArray>(response["warnings"]));
                Assert.Equal("non_beam_framing_on_axis", (string)warning["code"]);
                Assert.Equal(framing.UniqueId, (string)warning["unique_id"]);
                Assert.Equal("partial", (string)response["coverage"]);
            }
            else Assert.Null(response["warnings"]);
            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            Assert.Equal(6000, (double)Assert.Single((JArray)response["planned"])["length_mm"]);
        }

        [Theory]
        [InlineData(3995, 4005)]
        [InlineData(4005, 3995)]
        [InlineData(4000, 4000)]
        public void A_transverse_beam_leaves_one_full_length_creation(double startX, double endX)
        {
            Document doc = Fixture(widthMm: 300);
            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(8000 / Ft, 0, 0));
            AddGrid(doc, 3, "B", new XYZ(8000 / Ft, -1, 0), new XYZ(8000 / Ft, 1, 0));
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(startX / Ft, -1000 / Ft, 2850 / Ft),
                                         new XYZ(endX / Ft, 1000 / Ft, 2850 / Ft))
            };

            JObject response = Plan(doc);

            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            Assert.Empty(Assert.IsType<JArray>(response["partially_covered_spans"]));
            JToken element = Assert.Single(Assert.IsType<JArray>(response["next_arguments"]["arguments"]["elements"]));
            Assert.Equal(0, (double)element["start"][0], 6);
            Assert.Equal(8000, (double)element["end"][0], 6);
        }

        [Theory]
        [InlineData(false, 6000)]
        [InlineData(true, 6000)]
        [InlineData(false, 5500)]
        [InlineData(true, 5500)]
        public void Uniform_center_uses_the_physical_ends_to_plan_only_free_segments(bool reversed, int physicalEnd)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc, startMm: reversed ? 5000 : 1000, endMm: reversed ? 1000 : 5000);
            beam.LevelId = new ElementId(TargetLevel);
            // Keep the extension settings identical. The solid may still be trimmed by family geometry.
            beam.BuiltInParams[BuiltInParameter.START_EXTENSION] = new Parameter { Value = 1000 / Ft };
            beam.BuiltInParams[BuiltInParameter.END_EXTENSION] = new Parameter { Value = 1000 / Ft };
            SetSolidFootprint(beam, -150, 150, -150, 150, nested: true, endX: physicalEnd);

            JObject response = Plan(doc);

            Assert.Null(response["unmeasured_existing_beams"]);
            if (physicalEnd == 6000)
            {
                Assert.Equal(1, (int)response["suppressed_existing_beams"]);
                Assert.Null(response["next_arguments"]);
                return;
            }

            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            JToken partial = Assert.Single((JArray)response["partially_covered_spans"]);
            Assert.Equal(physicalEnd, (double)partial["covered_mm"], 6);
            Assert.Equal(701L, (long)Assert.Single((JArray)partial["covering_beams"])["beam_id"]);
            JToken element = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
            Assert.Equal(physicalEnd, (double)element["start"][0], 6);
            Assert.Equal(6000, (double)element["end"][0], 6);
        }

        [Fact]
        public void Join_cutback_uses_the_measured_ends_without_suppressing_the_free_segment()
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.START_JOIN_CUTBACK] = new Parameter { Value = 600 / Ft };
            SetSolidFootprint(beam, -150, 150, -150, 150, startX: 600);

            JObject response = Plan(doc);

            JToken element = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
            Assert.Equal(0, (double)element["start"][0], 6);
            Assert.Equal(600, (double)element["end"][0], 6);
        }

        [Theory]
        [InlineData(0, false, 0)]
        [InlineData(90, false, 1)]
        [InlineData(-90, true, 1)]
        public void Uniform_center_with_a_rotated_section_uses_its_physical_width(
            double rotationDegrees, bool reversed, int suppressed)
        {
            Document doc = Fixture(widthMm: 200);
            FamilyInstance beam = AddBeam(doc, offsetMm: 200, startMm: reversed ? 6000 : 0, endMm: reversed ? 0 : 6000);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE] = new Parameter { Value = rotationDegrees * Math.PI / 180 };
            beam.BuiltInParams[BuiltInParameter.START_EXTENSION] = new Parameter { Value = 0.0 };
            beam.BuiltInParams[BuiltInParameter.END_EXTENSION] = new Parameter { Value = 0.0 };
            beam.BuiltInParams[BuiltInParameter.START_JOIN_CUTBACK] = new Parameter { Value = 0.0 };
            beam.BuiltInParams[BuiltInParameter.END_JOIN_CUTBACK] = new Parameter { Value = 0.0 };
            double lateralHalfWidth = rotationDegrees == 0 ? 100 : 300;
            SetSolidFootprint(beam, 200 - lateralHalfWidth, 200 + lateralHalfWidth,
                200 - lateralHalfWidth, 200 + lateralHalfWidth, nested: true);

            JObject response = Plan(doc);

            Assert.Null(response["unmeasured_existing_beams"]);
            Assert.Equal(suppressed, (int)response["suppressed_existing_beams"]);
            if (suppressed == 1)
            {
                Assert.Equal(1, beam.GeometryReads);
                Assert.Null(response["next_arguments"]);
            }
            else
            {
                JToken row = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
                Assert.Equal(0, (double)row["start"][0], 6);
                Assert.Equal(6000, (double)row["end"][0], 6);
            }
        }

        [Fact]
        public void Rotated_section_with_independent_end_offsets_uses_the_measured_direction()
        {
            Document doc = Fixture(widthMm: 200);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(1920 / Ft, -1440 / Ft, 3000 / Ft),
                    new XYZ(4080 / Ft, 1440 / Ft, 3000 / Ft))
            };
            beam.BuiltInParams[BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE] = new Parameter { Value = Math.PI / 2 };
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = (int)YZJustificationOption.Independent };
            beam.BuiltInParams[BuiltInParameter.START_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Center };
            beam.BuiltInParams[BuiltInParameter.END_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Center };
            beam.BuiltInParams[BuiltInParameter.START_Y_OFFSET_VALUE] = new Parameter { Value = -2400 / Ft };
            beam.BuiltInParams[BuiltInParameter.END_Y_OFFSET_VALUE] = new Parameter { Value = 2400 / Ft };
            SetSolidFootprint(beam, -200, 400, -450, 150, nested: true);

            JObject response = Plan(doc);

            Assert.Equal(1, beam.GeometryReads);
            Assert.Null(response["unmeasured_existing_beams"]);
            Assert.Equal(1, (int)response["suppressed_existing_beams"]);
            Assert.Equal(0, (int)response["planned_count"]);
            Assert.Null(response["next_arguments"]);
        }

        [Fact]
        public void A_present_extension_without_a_value_uses_measured_ends()
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc, startMm: 1000, endMm: 5000);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.START_EXTENSION] = new Parameter();
            SetSolidFootprint(beam, -150, 150, -150, 150);

            JObject response = Plan(doc);

            Assert.Equal(1, beam.GeometryReads);
            Assert.Equal(1, (int)response["suppressed_existing_beams"]);
            Assert.Null(response["next_arguments"]);
        }

        [Theory]
        [InlineData(BuiltInParameter.START_EXTENSION, 1000 / Ft)]
        [InlineData(BuiltInParameter.START_JOIN_CUTBACK, null)]
        [InlineData(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE, Math.PI / 2)]
        [InlineData(BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE, null)]
        public void Uniform_center_with_unmeasured_placement_withholds_the_potentially_occupied_span(
            BuiltInParameter parameter, double? value)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc, startMm: 1000, endMm: 5000, zMm: 3000);
            beam.BuiltInParams[parameter] = new Parameter { Value = value };
            beam.BoundingBox = new BoundingBoxXYZ { Min = new XYZ(0, -0.5, 9), Max = new XYZ(6000 / Ft, 0.5, 10) };

            JObject response = Plan(doc);

            JToken unmeasured = Assert.Single((JArray)response["unmeasured_existing_beams"]);
            Assert.Equal(701L, (long)unmeasured["beam_id"]);
            Assert.Contains(parameter.ToString(), (string)unmeasured["reason"]);
            Assert.Single((JArray)response["coverage_unmeasured_spans"]);
            Assert.Null(response["next_arguments"]);
        }

        [Theory]
        [InlineData(false, 100, 600, 0)]
        [InlineData(true, 100, 600, 0)]
        [InlineData(false, 700, -600, 1)]
        [InlineData(true, 700, -600, 1)]
        public void Uniform_offset_uses_the_physical_axis_in_both_draw_directions(
            bool reversed, double lineY, double shiftY, int suppressed)
        {
            Document doc = Fixture(widthMm: 300);
            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(8000 / Ft, 0, 0));
            AddGrid(doc, 3, "B", new XYZ(8000 / Ft, -1, 0), new XYZ(8000 / Ft, 1, 0));
            FamilyInstance beam = AddBeam(doc, offsetMm: lineY, startMm: reversed ? 8000 : 0, endMm: reversed ? 0 : 8000);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = (int)YZJustificationOption.Uniform };
            beam.BuiltInParams[BuiltInParameter.Y_OFFSET_VALUE] = new Parameter { Value = (reversed ? shiftY : -shiftY) / Ft };

            JObject response = Plan(doc);

            Assert.Equal(suppressed, (int)response["suppressed_existing_beams"]);
            Assert.Empty((JArray)response["partially_covered_spans"]);
            if (suppressed == 1) Assert.Null(response["next_arguments"]);
            else
            {
                JToken row = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
                Assert.Equal(0, (double)row["start"][0], 6);
                Assert.Equal(8000, (double)row["end"][0], 6);
            }
        }

        [Theory]
        [InlineData(YJustification.Left, false, 300, true)]
        [InlineData(YJustification.Left, true, -300, true)]
        [InlineData(YJustification.Right, false, -300, true)]
        [InlineData(YJustification.Right, true, 300, true)]
        [InlineData(YJustification.Left, false, -100, false)]
        [InlineData(YJustification.Right, false, 100, false)]
        public void Side_justification_moves_the_center_half_a_width_in_the_correct_direction(
            YJustification justification, bool reversed, double lineY, bool covered)
        {
            Document doc = Fixture(widthMm: 300);
            // Move the line opposite to the offset to isolate the half-width adjustment.
            FamilyInstance beam = AddBeam(doc, offsetMm: lineY + (reversed ? -40 : 40),
                startMm: reversed ? 6000 : 0, endMm: reversed ? 0 : 6000);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.Y_JUSTIFICATION] = new Parameter { Value = (int)justification };
            beam.BuiltInParams[BuiltInParameter.Y_OFFSET_VALUE] = new Parameter { Value = 40 / Ft };

            JObject response = Plan(doc);

            Assert.Equal(covered ? 1 : 0, (int)response["suppressed_existing_beams"]);
            if (covered) Assert.Null(response["next_arguments"]);
            else Assert.Equal(6000, (double)Assert.Single((JArray)response["planned"])["length_mm"]);
        }

        [Theory]
        [InlineData(0, 200, 500, false)]
        [InlineData(600, -100, 200, true)]
        public void Origin_uses_the_physical_footprint_instead_of_centering_the_section_on_its_datum(
            double datumY, double minY, double maxY, bool covered)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc, offsetMm: datumY);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            SetSolidFootprint(beam, minY, maxY, minY, maxY, nested: true);

            JObject response = Plan(doc);

            Assert.Equal(1, beam.GeometryReads);
            Assert.Equal(covered ? 1 : 0, (int)response["suppressed_existing_beams"]);
            if (covered) Assert.Null(response["next_arguments"]);
            else
            {
                JToken element = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
                Assert.Equal(0, (double)element["start"][0], 6);
                Assert.Equal(6000, (double)element["end"][0], 6);
            }
            Assert.Null(response["unmeasured_existing_beams"]);
        }

        [Fact]
        public void Independent_origin_clips_the_footprint_and_lists_the_covering_beam_once()
        {
            Document doc = Fixture(widthMm: null);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = (int)YZJustificationOption.Independent };
            beam.BuiltInParams[BuiltInParameter.START_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            beam.BuiltInParams[BuiltInParameter.END_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Center };
            SetSolidFootprint(beam, -300, 0, 300, 600);
            SetSolidFootprint(beam, -350, 0, 250, 600); // Overlapping faces still describe one beam.

            JObject response = Plan(doc);

            JToken partial = Assert.Single((JArray)response["partially_covered_spans"]);
            Assert.Equal(3500, (double)partial["covered_mm"], 6);
            Assert.Equal(beam.UniqueId, (string)Assert.Single((JArray)partial["covering_beams"])["unique_id"]);
            JToken row = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
            Assert.Equal(3500, (double)row["start"][0], 6);
            Assert.Equal(6000, (double)row["end"][0], 6);
        }

        [Theory]
        [InlineData(YZJustificationOption.Independent, 1)]
        [InlineData(YZJustificationOption.Uniform, 0)]
        public void Origin_uses_the_footprint_when_independent_end_offsets_can_change_direction(
            YZJustificationOption mode, int suppressed)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            // A diagonal datum is predominantly transverse. Only independent placement
            // can align that beam's physical ends along the bay.
            beam.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(1920 / Ft, -1440 / Ft, 3000 / Ft), new XYZ(4080 / Ft, 1440 / Ft, 3000 / Ft))
            };
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = (int)mode };
            beam.BuiltInParams[BuiltInParameter.Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            beam.BuiltInParams[BuiltInParameter.START_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            beam.BuiltInParams[BuiltInParameter.END_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            if (mode == YZJustificationOption.Independent)
            {
                beam.BuiltInParams[BuiltInParameter.START_Y_OFFSET_VALUE] = new Parameter { Value = -2400 / Ft };
                beam.BuiltInParams[BuiltInParameter.END_Y_OFFSET_VALUE] = new Parameter { Value = 2400 / Ft };
                SetSolidFootprint(beam, -150, 150, -150, 150);
            }
            else
                SetSolidFootprint(beam, -1590, -1290, 1290, 1590, startX: 1920, endX: 4080);

            JObject response = Plan(doc);

            Assert.Equal(1, beam.GeometryReads);
            Assert.Equal(suppressed, (int)response["suppressed_existing_beams"]);
            Assert.Equal(1 - suppressed, (int)response["planned_count"]);
            Assert.Equal(suppressed == 0, response["next_arguments"] != null);
        }

        [Theory]
        [InlineData(300.0)]
        [InlineData(null)]
        public void The_same_transverse_origin_beam_leaves_the_bay_free_even_without_the_mode(double? widthMm)
        {
            Document doc = Fixture(widthMm);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(3000 / Ft, -1000 / Ft, 3000 / Ft),
                    new XYZ(3000 / Ft, 1000 / Ft, 3000 / Ft))
            };
            beam.BuiltInParams[BuiltInParameter.Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            beam.BuiltInParams[BuiltInParameter.Y_OFFSET_VALUE] = new Parameter { Value = 0.0 };
            beam.BuiltInParams[BuiltInParameter.START_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            beam.BuiltInParams[BuiltInParameter.END_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            beam.BuiltInParams[BuiltInParameter.START_Y_OFFSET_VALUE] = new Parameter { Value = 0.0 };
            beam.BuiltInParams[BuiltInParameter.END_Y_OFFSET_VALUE] = new Parameter { Value = 0.0 };
            SetSolidFootprint(beam, -1000, 1000, -1000, 1000, startX: 2850, endX: 3150);

            var uniform = Plan(doc);
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION].Value = (int)YZJustificationOption.Independent;
            var independent = Plan(doc);
            beam.BuiltInParams.Remove(BuiltInParameter.YZ_JUSTIFICATION);
            var unknown = Plan(doc);

            var wholeBay = Assert.Single((JArray)uniform["planned"]);
            Assert.Equal(6000, (double)wholeBay["length_mm"]);
            Assert.True(JToken.DeepEquals(uniform["planned"], independent["planned"]));
            Assert.True(JToken.DeepEquals(uniform["next_arguments"], independent["next_arguments"]));
            Assert.Empty((JArray)independent["partially_covered_spans"]);
            Assert.True(JToken.DeepEquals(uniform["planned"], unknown["planned"]));
            Assert.True(JToken.DeepEquals(uniform["next_arguments"], unknown["next_arguments"]));
            Assert.Empty((JArray)unknown["partially_covered_spans"]);
            Assert.Null(unknown["coverage_unmeasured_spans"]);
            Assert.Equal(3, beam.GeometryReads);
        }

        [Fact]
        public void Unequal_origin_offsets_can_turn_a_beam_without_making_it_longitudinal()
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(3000 / Ft, -1000 / Ft, 3000 / Ft),
                    new XYZ(3000 / Ft, 1000 / Ft, 3000 / Ft))
            };
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION].Value = (int)YZJustificationOption.Independent;
            beam.BuiltInParams[BuiltInParameter.START_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            beam.BuiltInParams[BuiltInParameter.END_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            beam.BuiltInParams[BuiltInParameter.START_Y_OFFSET_VALUE] = new Parameter { Value = -500 / Ft };
            beam.BuiltInParams[BuiltInParameter.END_Y_OFFSET_VALUE] = new Parameter { Value = 500 / Ft };
            SetSolidFootprint(beam, -1300, -700, 700, 1300, startX: 2500, endX: 3500);

            JObject response = Plan(doc);

            Assert.Equal(6000, (double)Assert.Single((JArray)response["planned"])["length_mm"]);
            Assert.Empty((JArray)response["partially_covered_spans"]);
            Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Unreadable_mode_with_center_end_justification_never_releases_occupied_bay(bool hasFootprint)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc, offsetMm: 1000);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = "unreadable" };
            beam.BuiltInParams.Remove(BuiltInParameter.Y_OFFSET_VALUE);
            beam.BuiltInParams[BuiltInParameter.START_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Center };
            beam.BuiltInParams[BuiltInParameter.END_Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Center };
            beam.BuiltInParams[BuiltInParameter.START_Y_OFFSET_VALUE] = new Parameter { Value = 1000 / Ft };
            beam.BuiltInParams[BuiltInParameter.END_Y_OFFSET_VALUE] = new Parameter { Value = 1000 / Ft };
            if (hasFootprint) SetSolidFootprint(beam, -150, 150, -150, 150);

            JObject response = Plan(doc);

            Assert.Equal(0, (int)response["planned_count"]);
            Assert.Equal(hasFootprint ? 1 : 0, (int)response["suppressed_existing_beams"]);
            Assert.Null(response["next_arguments"]);
            Assert.Null(response["next_arguments_batches"]);
            Assert.Equal(1, beam.GeometryReads);
            if (hasFootprint)
            {
                Assert.Null(response["unmeasured_existing_beams"]);
                Assert.Contains((JArray)response["assumptions"], row => (string)row["flag"] == "SUPUESTO" &&
                    ((string)row["what"]).Contains("YZ_JUSTIFICATION") && ((string)row["what"]).Contains("701"));
            }
            else
            {
                Assert.Equal("partial", (string)response["coverage"]);
                JToken beamRow = Assert.Single((JArray)response["unmeasured_existing_beams"]);
                Assert.Contains("YZ_JUSTIFICATION", (string)beamRow["reason"]);
                JToken span = Assert.Single((JArray)response["coverage_unmeasured_spans"]);
                Assert.Equal(beam.UniqueId, (string)Assert.Single((JArray)span["unmeasured_beams"])["unique_id"]);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("unreadable")]
        public void Unreadable_mode_uses_the_measured_origin_footprint_and_declares_the_assumption(string mode)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(1920 / Ft, -1440 / Ft, 3000 / Ft), new XYZ(4080 / Ft, 1440 / Ft, 3000 / Ft))
            };
            if (mode == null) beam.BuiltInParams.Remove(BuiltInParameter.YZ_JUSTIFICATION);
            else beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = mode };
            beam.BuiltInParams[BuiltInParameter.Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };
            SetSolidFootprint(beam, -150, 150, -150, 150);

            JObject response = Plan(doc);

            Assert.Equal(1, beam.GeometryReads);
            Assert.Equal(1, (int)response["suppressed_existing_beams"]);
            Assert.Null(response["next_arguments"]);
            Assert.Null(response["unmeasured_existing_beams"]);
            Assert.True((bool)response["has_assumptions"]);
            var assumption = Assert.Single((JArray)response["assumptions"], row =>
                ((string)row["what"]).Contains("YZ_JUSTIFICATION"));
            Assert.Equal("SUPUESTO", (string)assumption["flag"]);
            Assert.Contains("footprint", (string)assumption["what"]);
            Assert.Contains("701", (string)assumption["what"]);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(0, false)]
        [InlineData(10, true)]
        [InlineData(10, false)]
        public void An_unreadable_mode_preserves_the_footprint_of_a_short_longitudinal_beam(
            int angleDegrees, bool hasNominalWidth)
        {
            Document doc = Fixture(widthMm: hasNominalWidth ? 300.0 : (double?)null);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams.Remove(BuiltInParameter.YZ_JUSTIFICATION);
            double angle = angleDegrees * Math.PI / 180;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            XYZ Point(double along, double across) =>
                new XYZ((3100 + along * cos - across * sin) / Ft, (along * sin + across * cos) / Ft, 3000 / Ft);
            beam.Location = new LocationCurve { Curve = Line.CreateBound(Point(-100, 0), Point(100, 0)) };
            SetSolidFootprint(beam, Point(-100, -150), Point(100, -150), Point(100, 150), Point(-100, 150), false);

            JObject response = Plan(doc);

            Assert.Equal(2, (int)response["planned_count"]);
            JToken partial = Assert.Single((JArray)response["partially_covered_spans"]);
            // The bay axis crosses the two end faces of the rotated rectangle.
            double halfCoverage = 100 / cos;
            Assert.Equal(2 * halfCoverage, (double)partial["covered_mm"], 1);
            Assert.Equal(beam.UniqueId, (string)Assert.Single((JArray)partial["covering_beams"])["unique_id"]);
            var placements = Assert.IsType<JArray>(response["next_arguments"]["arguments"]["elements"]);
            Assert.Collection(placements,
                before =>
                {
                    Assert.Equal(0, (double)before["start"][0]);
                    Assert.Equal(3100 - halfCoverage, (double)before["end"][0], 1);
                },
                after =>
                {
                    Assert.Equal(3100 + halfCoverage, (double)after["start"][0], 1);
                    Assert.Equal(6000, (double)after["end"][0]);
                });
            Assert.Contains((JArray)response["assumptions"], row => (string)row["flag"] == "SUPUESTO" &&
                ((string)row["what"]).Contains("YZ_JUSTIFICATION"));
        }

        [Fact]
        public void A_nominal_80_mm_width_cannot_clear_a_box_from_an_unmeasured_placement()
        {
            Document doc = Fixture(widthMm: 80);
            FamilyInstance existing = AddUnmeasuredBeam(doc, 0, 6000, 50, 250);

            JObject response = Plan(doc);

            Assert.Equal(0, (int)response["planned_count"]);
            Assert.Equal("partial", (string)response["coverage"]);
            Assert.Null(response["next_arguments"]);
            Assert.Null(response["next_arguments_batches"]);
            JToken span = Assert.Single((JArray)response["coverage_unmeasured_spans"]);
            Assert.Equal(existing.UniqueId, (string)Assert.Single((JArray)span["unmeasured_beams"])["unique_id"]);
            Assert.Contains("placement", (string)span["reason"]);
        }

        [Fact]
        public void Unreadable_origin_geometry_neither_claims_coverage_nor_offers_creation()
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Origin };

            JObject response = Plan(doc);

            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            Assert.Contains("Origin", (string)Assert.Single((JArray)response["unmeasured_existing_beams"])["reason"]);
            Assert.Single((JArray)response["coverage_unmeasured_spans"]);
            Assert.Null(response["next_arguments"]);
        }

        [Fact]
        public void Independent_offsets_only_count_the_covered_length()
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = (int)YZJustificationOption.Independent };
            beam.BuiltInParams[BuiltInParameter.START_Y_OFFSET_VALUE] = new Parameter { Value = 0.0 };
            beam.BuiltInParams[BuiltInParameter.END_Y_OFFSET_VALUE] = new Parameter { Value = -600 / Ft };
            beam.BuiltInParams[BuiltInParameter.Y_OFFSET_VALUE] = new Parameter { Value = -800 / Ft };

            JObject response = Plan(doc);

            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            Assert.Equal(1507.5, (double)Assert.Single((JArray)response["partially_covered_spans"])["covered_mm"]);
            JToken row = Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
            Assert.Equal(1507.481343, (double)row["start"][0], 6);
            Assert.Equal(6000, (double)row["end"][0], 6);
        }

        [Fact]
        public void A_missing_independent_offset_withholds_creation_when_bounds_cannot_clear_the_bay()
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = (int)YZJustificationOption.Independent };
            beam.BuiltInParams[BuiltInParameter.START_Y_OFFSET_VALUE] = new Parameter { Value = 0.0 };

            JObject response = Plan(doc);

            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            Assert.Equal("partial", (string)response["coverage"]);
            JToken unreadable = Assert.Single((JArray)response["unmeasured_existing_beams"]);
            Assert.Equal(701, (long)unreadable["beam_id"]);
            Assert.Contains("END_Y_OFFSET_VALUE", (string)unreadable["reason"]);
            Assert.Equal(0, (int)response["planned_count"]);
            Assert.Null(response["next_arguments"]);
            Assert.Null(response["next_arguments_batches"]);
            JToken span = Assert.Single((JArray)response["coverage_unmeasured_spans"]);
            Assert.Equal("existing_beam_coverage_unmeasured", (string)span["code"]);
            Assert.Equal(6000, (double)span["length_mm"]);
            Assert.Equal(beam.UniqueId, (string)Assert.Single((JArray)span["unmeasured_beams"])["unique_id"]);
        }

        [Theory]
        [InlineData(-150, 150)]
        [InlineData(200, 500)]
        public void Unmeasured_beam_bounds_only_withhold_the_free_segments_they_could_cover(
            double minY, double maxY)
        {
            Document doc = Fixture(widthMm: 300);
            AddBeam(doc, startMm: 2500, endMm: 3500).LevelId = new ElementId(TargetLevel);
            FamilyInstance beam = AddBeam(doc, startMm: 0, endMm: 2500, id: 702, zMm: 3000);
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = (int)YZJustificationOption.Independent };
            beam.BuiltInParams[BuiltInParameter.START_Y_OFFSET_VALUE] = new Parameter { Value = 0.0 };
            beam.BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ(0, minY / Ft, 2800 / Ft),
                Max = new XYZ(2500 / Ft, maxY / Ft, 3100 / Ft)
            };

            JObject response = Plan(doc);

            JToken partial = Assert.Single((JArray)response["partially_covered_spans"]);
            Assert.Equal(1000, (double)partial["covered_mm"]);
            var expected = new[] { 3500d };
            Assert.Equal(expected, partial["planned_free_segments"].Select(row => System.Math.Round((double)row["start"][0], 6)));
            Assert.Equal(expected, response["next_arguments"]["arguments"]["elements"]
                .Select(row => System.Math.Round((double)row["start"][0], 6)));
            Assert.Equal(2500, (double)Assert.Single((JArray)response["coverage_unmeasured_spans"])["length_mm"]);
        }

        [Theory]
        [InlineData(-200, 4, -100, 100, 300, true)] // Four millimetres at the start.
        [InlineData(5996, 6200, -100, 100, 300, true)] // Four millimetres at the end.
        [InlineData(0, 6000, 50, 250, 300, true)] // Box hits the section, missing the axis.
        [InlineData(0, 6000, -250, -50, 300, true)]
        [InlineData(0, 6000, 50, 250, 80, true)] // A narrower type still has unknown lateral placement.
        [InlineData(0, 6000, 50, 250, null, true)] // Unknown width cannot clear this overlap.
        [InlineData(-200, -1, -100, 100, null, false)] // Longitudinally disjoint even with unknown width.
        [InlineData(-200, 0, -100, 100, 300, false)] // End-to-end contact is allowed.
        public void Unmeasured_bounds_require_longitudinal_clearance_at_both_ends(
            double minX, double maxX, double minY, double maxY, int? widthMm, bool withholds)
        {
            Document doc = Fixture(widthMm: widthMm);
            FamilyInstance beam = AddUnmeasuredBeam(doc, minX, maxX, minY, maxY);
            beam.Symbol = new FamilySymbol { Id = new ElementId(799) };
            beam.Symbol.BuiltInParams[BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH] = new Parameter { Value = 300 / Ft };

            JObject response = Plan(doc);

            Assert.DoesNotContain((JArray)response["assumptions"] ?? new JArray(),
                row => ((string)row["what"]).Contains("selected type's nominal width"));
            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            Assert.Equal(withholds ? 0 : 1, (int)response["planned_count"]);
            if (withholds)
            {
                Assert.Null(response["next_arguments"]);
                Assert.Null(response["next_arguments_batches"]);
                JToken span = Assert.Single((JArray)response["coverage_unmeasured_spans"]);
                Assert.Equal(beam.UniqueId, (string)Assert.Single((JArray)span["unmeasured_beams"])["unique_id"]);
            }
            else
            {
                Assert.Null(response["coverage_unmeasured_spans"]);
                Assert.Single((JArray)response["next_arguments"]["arguments"]["elements"]);
            }
        }

        [Theory]
        [InlineData(900, 1000, 1200, 1300, true)]
        [InlineData(900, 1000, 1300, 1400, true)]
        [InlineData(-900, -800, 600, 700, false)]
        public void Oblique_spans_screen_the_box_along_the_span_direction(
            double minX, double maxX, double minY, double maxY, bool withholds)
        {
            Document doc = Fixture(widthMm: 300);
            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(6000 / Ft, 6000 / Ft, 0));
            AddGrid(doc, 3, "B", new XYZ(6000 / Ft, 5900 / Ft, 0), new XYZ(6000 / Ft, 6100 / Ft, 0));
            AddUnmeasuredBeam(doc, minX, maxX, minY, maxY);

            JObject response = Plan(doc);

            Assert.Equal(withholds ? 0 : 1, (int)response["planned_count"]);
            Assert.Equal(withholds, response["coverage_unmeasured_spans"] != null);
            Assert.Equal(!withholds, response["next_arguments"] != null);
        }

        private static FamilyInstance AddUnmeasuredBeam(Document doc, double minX, double maxX, double minY, double maxY)
        {
            FamilyInstance beam = AddBeam(doc);
            beam.Location = null;
            beam.BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ(minX / Ft, minY / Ft, 2800 / Ft),
                Max = new XYZ(maxX / Ft, maxY / Ft, 3100 / Ft)
            };
            return beam;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Missing_uniform_placement_values_are_declared_at_the_top_level(bool emptyParameters)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            foreach (BuiltInParameter id in new[] { BuiltInParameter.Y_JUSTIFICATION, BuiltInParameter.Y_OFFSET_VALUE })
                if (emptyParameters) beam.BuiltInParams[id] = new Parameter();
                else beam.BuiltInParams.Remove(id);

            JObject response = Plan(doc);

            Assert.Equal(1, (int)response["suppressed_existing_beams"]);
            Assert.True((bool)response["has_assumptions"]);
            JArray rows = Assert.IsType<JArray>(response["assumptions"]);
            Check("0 mm", "Y_OFFSET_VALUE");
            Check("Center", "Y_JUSTIFICATION");

            void Check(string value, string parameter)
            {
                JToken assumption = Assert.Single(rows, row => ((string)row["what"]).Contains(" for " + parameter + " "));
                Assert.Equal("SUPUESTO", (string)assumption["flag"]);
                Assert.Contains(value, (string)assumption["what"]);
                Assert.Contains("701", (string)assumption["what"]);
                Assert.Contains("missing", (string)assumption["why"]);
                Assert.Contains(parameter, (string)assumption["override"]);
            }
        }

        [Theory]
        [InlineData(-1000, 1000, StructuralType.Brace, true)]
        [InlineData(1000, -1000, StructuralType.Brace, true)]
        [InlineData(151, 1000, StructuralType.Brace, false)]
        [InlineData(-1000, 1000, StructuralType.Beam, false)]
        public void A_perpendicular_crossing_warns_for_non_beam_framing_only(
            double startY, double endY, StructuralType type, bool warns)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance framing = AddBeam(doc);
            framing.LevelId = new ElementId(TargetLevel);
            framing.StructuralType = type;
            framing.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(4000 / Ft, startY / Ft, 2850 / Ft),
                    new XYZ(4000 / Ft, endY / Ft, 2850 / Ft))
            };

            JObject response = Plan(doc);

            if (warns)
            {
                JToken warning = Assert.Single((JArray)response["warnings"]);
                Assert.Equal("non_beam_framing_on_axis", (string)warning["code"]);
                Assert.Equal(framing.UniqueId, (string)warning["unique_id"]);
            }
            else Assert.Null(response["warnings"]);
            Assert.Equal(0, (int)response["suppressed_existing_beams"]);
            Assert.Equal(6000, (double)Assert.Single((JArray)response["planned"])["length_mm"]);
        }

        [Fact]
        public void An_oblique_beam_only_leaves_creation_arguments_for_the_free_part()
        {
            Document doc = Fixture(widthMm: 300);
            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(12000 / Ft, 0, 0));
            AddGrid(doc, 3, "B", new XYZ(4000 / Ft, -1, 0), new XYZ(4000 / Ft, 1, 0));
            AddGrid(doc, 4, "C", new XYZ(12000 / Ft, -1, 0), new XYZ(12000 / Ft, 1, 0));
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            beam.Location = new LocationCurve
            {
                Curve = Line.CreateBound(new XYZ(0, 0, 2850 / Ft), new XYZ(12000 / Ft, 240 / Ft, 2850 / Ft))
            };

            JObject response = Plan(doc);

            Assert.Equal(1, (int)response["suppressed_existing_beams"]);
            JToken partial = Assert.Single(Assert.IsType<JArray>(response["partially_covered_spans"]));
            Assert.Equal(3501.5, (double)partial["covered_mm"]);
            Assert.Equal(beam.UniqueId, (string)Assert.Single(Assert.IsType<JArray>(partial["covering_beams"]))["unique_id"]);
            JToken element = Assert.Single(Assert.IsType<JArray>(response["next_arguments"]["arguments"]["elements"]));
            Assert.Equal(7501.5, (double)element["start"][0], 1);
            Assert.Equal(12000, (double)element["end"][0], 6);
        }

        [Theory]
        [InlineData(false, -1000, 1000, 2664.589803, 3335.410197, 670.8)]
        [InlineData(true, -1000, 1000, 2664.589803, 3335.410197, 670.8)]
        [InlineData(false, -100, 1900, 950, 1535.410197, 585.4)]
        [InlineData(true, -100, 1900, 950, 1535.410197, 585.4)]
        public void An_oblique_beam_clips_creation_arguments_to_its_section_sides_and_ends(
            bool reverse, double firstY, double lastY, double firstHit, double lastHit, double coveredMm)
        {
            Document doc = Fixture(widthMm: 300);
            FamilyInstance beam = AddBeam(doc);
            beam.LevelId = new ElementId(TargetLevel);
            var start = new XYZ(1000 / Ft, firstY / Ft, 2850 / Ft);
            var end = new XYZ(5000 / Ft, lastY / Ft, 2850 / Ft);
            beam.Location = new LocationCurve { Curve = reverse ? Line.CreateBound(end, start) : Line.CreateBound(start, end) };

            JObject response = Plan(doc);

            JToken partial = Assert.Single(Assert.IsType<JArray>(response["partially_covered_spans"]));
            Assert.Equal(coveredMm, (double)partial["covered_mm"]);
            JToken existing = Assert.Single(Assert.IsType<JArray>(partial["covering_beams"]));
            Assert.Equal(701L, (long)existing["beam_id"]);
            Assert.Equal(beam.UniqueId, (string)existing["unique_id"]);
            Assert.Collection(Assert.IsType<JArray>(response["next_arguments"]["arguments"]["elements"]), left =>
            {
                Assert.Equal(0, (double)left["start"][0], 6);
                Assert.Equal(firstHit, (double)left["end"][0], 6);
            }, right =>
            {
                Assert.Equal(lastHit, (double)right["start"][0], 6);
                Assert.Equal(6000, (double)right["end"][0], 6);
            });
        }

        [Theory]
        [InlineData(3150, 3250, false, true, false)]
        [InlineData(3090, 3250, false, true, true)]
        [InlineData(3150, 3250, true, true, false)]
        [InlineData(3150, 3250, false, false, true)]
        [InlineData(3700, 3800, false, false, true)]
        [InlineData(3150, 3380, false, true, true)]
        public void A_beam_without_a_line_is_only_unmeasured_if_it_can_belong_to_the_level(
            double minZMm, double maxZMm, bool referencesTarget, bool hasOtherLevel, bool expectedUnmeasured)
        {
            Document doc = Fixture(widthMm: 300);
            if (!hasOtherLevel) doc.Elements.Remove(OtherLevel);
            FamilyInstance beam = AddBeam(doc);
            beam.Location = null;
            beam.BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ(0, 0, minZMm / Ft),
                Max = new XYZ(6000 / Ft, 0, maxZMm / Ft)
            };
            if (referencesTarget) beam.LevelId = new ElementId(TargetLevel);

            JObject response = Plan(doc);

            if (expectedUnmeasured)
            {
                Assert.Null(response["framing_excluded_by_bounds"]);
                Assert.Equal(701L, (long)Assert.Single(Assert.IsType<JArray>(response["unmeasured_existing_beams"]))["beam_id"]);
                Assert.Equal("partial", (string)response["coverage"]);
            }
            else if (referencesTarget)
            {
                Assert.Null(response["unmeasured_existing_beams"]);
                Assert.Equal(1, (int)response["planned_count"]);
                Assert.Empty((JArray)response["omitted_existing_beams"]);
            }
            else
            {
                Assert.Null(response["unmeasured_existing_beams"]);
                Assert.True((bool)response["has_assumptions"]);
                JToken assumption = Assert.Single(Assert.IsType<JArray>(response["assumptions"]));
                Assert.Equal("SUPUESTO", (string)assumption["flag"]);
                Assert.Contains("bounding-box height", (string)assumption["what"]);
                Assert.Contains("another known level", (string)assumption["why"]);
                JToken exclusion = Assert.Single(Assert.IsType<JArray>(response["framing_excluded_by_bounds"]));
                Assert.Equal(701L, (long)exclusion["element_id"]);
                Assert.Equal(beam.UniqueId, (string)exclusion["unique_id"]);
                Assert.Equal(minZMm, (double)exclusion["min_z_mm"], 6);
                Assert.Equal(maxZMm, (double)exclusion["max_z_mm"], 6);
                Assert.Equal(3000, (double)exclusion["target_level_z_mm"], 6);
                Assert.Equal(3200, (double)exclusion["matched_level_z_mm"], 6);
                Assert.Equal(JTokenType.Null, exclusion["section_depth_mm"].Type);
            }
        }

        [Fact]
        public void The_command_reports_height_and_width_assumptions_on_the_returned_plan()
        {
            Document doc = Fixture(widthMm: null);
            FamilyInstance beam = AddBeam(doc, offsetMm: 37.5);
            beam.Symbol.BuiltInParams[BuiltInParameter.STRUCTURAL_SECTION_COMMON_HEIGHT] =
                new Parameter { Value = 300 / Ft };

            JObject response = Plan(doc);

            Assert.Equal(1, (int)response["suppressed_existing_beams"]);
            Assert.Equal(0, (int)response["planned_count"]);
            Assert.True((bool)response["has_assumptions"]);
            JArray assumptions = Assert.IsType<JArray>(response["assumptions"]);
            Assert.Equal(2, assumptions.Count);
            Assert.All(assumptions.Cast<JObject>(), row =>
            {
                Assert.Equal("SUPUESTO", (string)row["flag"]);
                Assert.False(string.IsNullOrEmpty((string)row["what"]));
                Assert.False(string.IsNullOrEmpty((string)row["why"]));
                Assert.False(string.IsNullOrEmpty((string)row["override"]));
            });
            Assert.Contains(assumptions, row => ((string)row["what"]).Contains("location-line height"));
            Assert.Contains(assumptions, row => ((string)row["what"]).Contains("50 mm"));
            Assert.Null(response["next_arguments"]);
        }

        private static Document Fixture(double? widthMm)
        {
            var doc = new Document { Title = "Beam plan" };
            doc.Elements[TargetLevel] = new Level
            {
                Id = new ElementId(TargetLevel), Elevation = 12000 / Ft, ProjectElevation = 3000 / Ft
            };
            doc.Elements[OtherLevel] = new Level
            {
                Id = new ElementId(OtherLevel), Elevation = 12200 / Ft, ProjectElevation = 3200 / Ft
            };
            var symbol = new FamilySymbol
            {
                Id = new ElementId(BeamType),
                TestCategory = BuiltInCategory.OST_StructuralFraming
            };
            if (widthMm.HasValue)
                symbol.BuiltInParams[BuiltInParameter.STRUCTURAL_SECTION_COMMON_WIDTH] =
                    new Parameter { Value = widthMm.Value / Ft };
            doc.Elements[BeamType] = symbol;

            AddGrid(doc, 1, "1", new XYZ(0, 0, 0), new XYZ(6000 / Ft, 0, 0));
            AddGrid(doc, 2, "A", new XYZ(0, -1, 0), new XYZ(0, 1, 0));
            AddGrid(doc, 3, "B", new XYZ(6000 / Ft, -1, 0), new XYZ(6000 / Ft, 1, 0));
            return doc;
        }

        private static void AddGrid(Document doc, long id, string name, XYZ start, XYZ end)
            => doc.Elements[id] = new Grid { Id = new ElementId(id), Name = name, Curve = Line.CreateBound(start, end) };

        private static FamilyInstance AddBeam(Document doc, double offsetMm = 0, double startMm = 0,
                                              double endMm = 6000, long id = 701, double zMm = 2850)
        {
            var beam = new FamilyInstance
            {
                Id = new ElementId(id), TestCategory = BuiltInCategory.OST_StructuralFraming, StructuralType = StructuralType.Beam,
                Symbol = (FamilySymbol)doc.Elements[BeamType],
                Location = new LocationCurve
                {
                    Curve = Line.CreateBound(new XYZ(startMm / Ft, offsetMm / Ft, zMm / Ft),
                                             new XYZ(endMm / Ft, offsetMm / Ft, zMm / Ft))
                }
            };
            beam.BuiltInParams[BuiltInParameter.YZ_JUSTIFICATION] = new Parameter { Value = (int)YZJustificationOption.Uniform };
            beam.BuiltInParams[BuiltInParameter.Y_JUSTIFICATION] = new Parameter { Value = (int)YJustification.Center };
            beam.BuiltInParams[BuiltInParameter.Y_OFFSET_VALUE] = new Parameter { Value = 0.0 };
            doc.Elements[id] = beam;
            return beam;
        }

        private static void SetSolidFootprint(FamilyInstance beam, double startMin, double startMax,
                                              double endMin, double endMax, bool nested = false,
                                              double startX = 0, double endX = 6000)
        {
            var a = new XYZ(startX / Ft, startMin / Ft, 3000 / Ft);
            var b = new XYZ(endX / Ft, endMin / Ft, 3000 / Ft);
            var c = new XYZ(endX / Ft, endMax / Ft, 3000 / Ft);
            var d = new XYZ(startX / Ft, startMax / Ft, 3000 / Ft);
            SetSolidFootprint(beam, a, b, c, d, nested);
        }

        private static void SetSolidFootprint(FamilyInstance beam, XYZ a, XYZ b, XYZ c, XYZ d, bool nested)
        {
            var mesh = new Mesh
            {
                TestTriangles =
                {
                    new MeshTriangle { TestVertices = new[] { a, b, c } },
                    new MeshTriangle { TestVertices = new[] { a, c, d } }
                }
            };
            var solid = new Solid { Faces = { new Face { TestMesh = mesh } } };
            beam.TestGeometry.Add(nested
                ? new GeometryInstance { TestInstanceGeometry = { solid } } : (GeometryObject)solid);
        }

        private static JObject Plan(Document doc, double? minSpanMm = null)
        {
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var request = new JObject
            {
                ["operation"] = "beams_along_grids", ["level_id"] = TargetLevel, ["type_id"] = BeamType
            };
            if (minSpanMm.HasValue) request["min_span_mm"] = minSpanMm.Value;
            var result = new PlanStructureCommand().Execute(app, request.ToString());
            Assert.True(result.Success, result.Error);
            return Assert.IsType<JObject>(result.Data);
        }
    }
}
