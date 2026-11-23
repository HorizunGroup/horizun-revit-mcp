// 5.0.4, measured on a scanned theatre: (1) every round column was built with the one type named in
// types.round_column, whatever its measured diameter; (2) many curved walls were refused with "Requested properties
// do not match the committed element." naming no property and no value, because the failing check (the declared arc)
// lives in the production checklist beside the row's own postconditions.
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScanPlanRoundColumnAndWallMismatchTests
    {






        [Fact]
        public void A_curved_wall_refused_by_its_arc_names_the_curve_and_its_numbers()
        {
            var row = new JObject
            {
                ["verified"] = false,
                ["postconditions"] = new JObject { ["all_verified"] = true, ["missing"] = new JArray(), ["properties"] = new JArray(
                    new JObject { ["property"] = "level_id", ["matches"] = true, ["requested"] = 30, ["found_in_committed_model"] = 30 }) },
                ["production_postconditions"] = new JObject { ["all_verified"] = false, ["missing"] = new JArray(), ["properties"] = new JArray(
                    new JObject { ["property"] = "curve", ["matches"] = false, ["requested"] = "arc", ["found_in_committed_model"] = "arc" }) },
                ["curve_verified"] = new JObject { ["requested"] = "arc", ["built"] = "Arc", ["centre_off_mm"] = 7.25, ["radius_off_mm"] = 6.9,
                    ["radius_mm"] = 8406.9, ["verified"] = false, ["means"] = "the element IS an arc and not the arc that was declared" }
            };
            string message = CreatedRowMismatch.Message(row);
            Assert.Contains("curve: asked arc, model has built Arc, centre_off_mm 7.25, radius_off_mm 6.9, radius_mm 8406.9", message);
            Assert.DoesNotContain("level_id", message);
            Assert.False(message.EndsWith("element.", StringComparison.Ordinal));
        }

        [Fact]
        public void Every_failing_check_is_named_with_the_asked_and_the_found_value()
        {
            var row = new JObject
            {
                ["verified"] = false,
                ["postconditions"] = new JObject { ["missing"] = new JArray("offset"), ["properties"] = new JArray(
                    new JObject { ["property"] = "unconnected_height", ["matches"] = false, ["requested"] = 3.5, ["found_in_committed_model"] = 3.2, ["unit"] = "feet", ["tolerance"] = 0.0033 },
                    new JObject { ["property"] = "centreline_offset_mm", ["matches"] = null, ["requested"] = 0.0, ["error"] = "no location curve" }) },
                ["production_postconditions"] = new JObject { ["missing"] = new JArray(), ["properties"] = new JArray(
                    new JObject { ["property"] = "type_id", ["matches"] = false, ["requested"] = 1001, ["found_in_committed_model"] = 1002 }) },
                ["error"] = null
            };
            var lines = CreatedRowMismatch.Describe(row);
            Assert.Contains(lines, l => l.StartsWith("unconnected_height: asked 1066.8 mm, model has 975.36 mm", StringComparison.Ordinal));
            Assert.Contains("centreline_offset_mm: asked 0, model has (unreadable: no location curve)", lines);
            Assert.Contains("type_id: asked 1001, model has 1002", lines);
            Assert.Contains("offset: required and never checked", lines);
            Assert.Contains("re-read: boom", CreatedRowMismatch.Describe(new JObject { ["verified"] = false, ["error"] = "boom" }));
            Assert.Contains("without naming a property", CreatedRowMismatch.Message(new JObject { ["verified"] = false }));
        }

        [Fact]
        public void Create_elements_refuses_with_the_full_mismatch()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "CreateElementsCommand.Execution.cs");
            Assert.Contains("if (row.Value<bool>(\"verified\") != true)", src);
            Assert.Contains("throw new InvalidOperationException(row.Value<string>(\"reducer_ports_error\") ?? CreatedRowMismatch.Message(row));", src);
            Assert.DoesNotContain("(wrong.Count == 0 ? \".\"", src);
        }

        [Fact]
        public void The_arc_tolerance_grows_with_the_radius_between_1_and_5_mm()
        {
            Assert.Equal(1.0, ArcTolerance.Mm(3000), 6);       // small arcs keep 1 mm
            Assert.Equal(1.0, ArcTolerance.Mm(10000), 6);
            Assert.Equal(2.17, ArcTolerance.Mm(21700), 6);     // the measured theatre wall: 1.055 mm off now passes
            Assert.True(1.0551 <= ArcTolerance.Mm(21700.3068));
            Assert.Equal(5.0, ArcTolerance.Mm(80000), 6);      // capped: a real error on a huge arc is still refused
            Assert.Equal(1.0, ArcTolerance.Mm(double.NaN), 6);
            Assert.Equal(1.0, ArcTolerance.Mm(0), 6);
            string create = Source("src", "Horizun.Revit", "Commands", "CreateElementsCommand.cs");
            Assert.Contains("double tolerance = ArcTolerance.Mm(made.ExpectedArcRadius * 304.8);", create);
            Assert.Contains("o[\"tolerance_mm\"]", create);
            Assert.Contains("o[\"verified\"] = !double.IsNaN(shapeOff) && !double.IsNaN(thirdOff) && shapeOff <= tolerance && thirdOff <= tolerance;", create);
            Assert.Contains("arc.Project(new XYZ(made.ExpectedArcThird.X", create);
        }

        [Fact]
        public void A_shallow_arc_is_judged_by_its_shape_not_by_its_centre()
        {
            // The Teatro case (14-nov): R 17.9 m, ends 1 mm outside the declared circle (the plan accepts 1 mm), third
            // point on it. Revit's circle through those three points has its centre over 2 mm away - refused before -
            // while the wall lies within 1 mm of the declared circle everywhere.
            double R = 17921, half = 0.17;
            double[] On(double a, double r) => new[] { r * Math.Cos(a), r * Math.Sin(a) };
            var s = On(-half, R + 1); var e = On(half, R + 1); var m = On(0, R);
            // circle through s, e, m (centre on the x axis by symmetry)
            double cx = (s[0] * s[0] + s[1] * s[1] - m[0] * m[0]) / (2 * (s[0] - m[0]));
            double r = m[0] - cx;
            Assert.True(Math.Abs(cx) > ArcTolerance.Mm(R));               // the old centre check refused it
            var built = Enumerable.Range(0, 33).Select(k =>
            {
                double ang = Math.Atan2(s[1], s[0] - cx) + k / 32.0 * 2 * Math.Atan2(e[1], e[0] - cx);
                return new[] { cx + r * Math.Cos(ang), r * Math.Sin(ang) };
            }).ToList();
            double off = ArcTolerance.ShapeOffMm(0, 0, R, built);
            Assert.True(off <= 1.0 + 1e-6, "shape off " + off);
            Assert.True(off <= ArcTolerance.Mm(R));
            // A different wall is still refused: a circle 5 mm larger.
            Assert.True(ArcTolerance.ShapeOffMm(0, 0, R, built.Select(p => new[] { p[0] * (R + 5) / R, p[1] * (R + 5) / R })) > ArcTolerance.Mm(R));
            Assert.True(double.IsNaN(ArcTolerance.ShapeOffMm(0, 0, R, new System.Collections.Generic.List<double[]>())));
        }



        private static string Source(params string[] parts)
        {
            DirectoryInfo d = new DirectoryInfo(Here());
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()));
        }

        private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
    }
}
