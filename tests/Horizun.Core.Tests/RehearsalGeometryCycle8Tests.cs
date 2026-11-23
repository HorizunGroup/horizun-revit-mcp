using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RehearsalGeometryCycle8Tests
    {
        private static double[] Point(double along, double normal, double z) =>
            new[] { 100 + 0.6 * along - 0.8 * normal, -160 + 0.8 * along + 0.6 * normal, z };

        [Fact]
        public void Opening_readback_accepts_host_projection_and_either_rectangle_diagonal()
        {
            Assert.True(GeometryInput.SameWallOpening(Point(0, 0, 0), Point(20, 0, 0),
                Point(4, 0.25, 25), Point(6, 0.25, 30), Point(4, 0, 30), Point(6, 0, 25)));
            Assert.True(GeometryInput.SameWallOpening(Point(20, 0, 0), Point(0, 0, 0),
                Point(4, 0.25, 25), Point(6, 0.25, 30), Point(6, 0, 25), Point(4, 0, 30)));
        }

        [Theory]
        [InlineData(0.001, 0)]
        [InlineData(0, 0.001)]
        public void Opening_readback_rejects_shifted_or_clipped_geometry(double alongError, double heightError)
        {
            Assert.False(GeometryInput.SameWallOpening(Point(0, 0, 0), Point(20, 0, 0),
                Point(4, 0.25, 25), Point(6, 0.25, 30), Point(4, 0, 25), Point(6 + alongError, 0, 30 + heightError)));
        }

        [Fact]
        public void Opening_unreadable_host_or_coordinates_cannot_pass()
        {
            Assert.False(GeometryInput.SameWallOpening(Point(0, 0, 0), Point(0, 0, 0),
                Point(4, 0, 25), Point(6, 0, 30), Point(4, 0, 25), Point(6, 0, 30)));
            Assert.False(GeometryInput.SameWallOpening(Point(0, 0, 0), Point(20, 0, 0),
                Point(4, 0, 25), Point(6, 0, 30), Point(4, 0, 25), Point(double.NaN, 0, 30)));
        }

        [Theory]
        [InlineData(1, "feet")]
        [InlineData(0.3048, "m")]
        [InlineData(304.8, "mm")]
        public void Datums_retain_precise_coordinates_for_round_trip_writes(double scale, string units)
        {
            const double z = 30.416666666666668, x = 32.4206515149472;
            var level = DatumGeometryRules.Level(z, z, scale, units);
            Assert.Equal(z, level["elevation"].Value<double>() / scale, 12);
            Assert.Equal(z, level["elevation_in_own_model"].Value<double>() / scale, 12);
            var grid = DatumGeometryRules.Grid(new[] { x, 0d, z }, new[] { x, 10d, z }, false, null, scale, units);
            Assert.Equal(x, grid["start"][0].Value<double>() / scale, 12);
            Assert.Equal(z, grid["end"][2].Value<double>() / scale, 12);
        }

        [Fact]
        public void Command_uses_projected_opening_check_and_unrounded_placement_without_loosening_moves()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Horizun.Revit"))) root = root.Parent;
            Assert.NotNull(root);
            string Read(string name) => File.ReadAllText(Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", name));
            Assert.Contains("GeometryInput.SameWallOpening", Read("CreateElementsGeometry.cs"));
            string query = Read("QueryModelCommand.cs");
            Assert.Contains("new JArray(q.X * scale, q.Y * scale, q.Z * scale)", query);
            Assert.DoesNotContain("Math.Round(q.X * scale, 3)", query);
            string transform = Read("TransformElementsCommand.cs");
            Assert.Contains("expected.DistanceTo(after[actualIndex]) > 1e-6", transform);
            Assert.Contains("sample_mismatches", transform);
        }
    }
}
