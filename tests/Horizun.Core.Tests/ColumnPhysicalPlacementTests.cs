using System;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ColumnPhysicalPlacementTests
    {
        [Theory]
        [InlineData(0, 2400, "column_solid_base_mismatch")]
        [InlineData(6000, 8000, "column_solid_top_mismatch")]
        [InlineData(6000, 8400, null)]
        public void Trapezoid_bodies_must_follow_the_requested_storey(double actualBase, double actualTop, string expected)
        {
            Assert.Equal(expected, ColumnConstraintOrder.SolidSpanProblem(6000, 8400, new[] { actualBase, actualTop }, 1));
        }

        [Fact]
        public void Family_datum_is_resolved_by_elevation_not_collector_order_or_localized_name()
        {
            Assert.Equal(1, ColumnConstraintOrder.FamilyDatumIndex(new[] { 3000d, 0d, 6000d }, 0, .001));
            Assert.Equal(-1, ColumnConstraintOrder.FamilyDatumIndex(new[] { 3000d, 6000d }, 0, .001));
            Assert.Throws<ArgumentException>(() => ColumnConstraintOrder.FamilyDatumIndex(new[] { 0d, 0d }, 0, .001));
            Assert.Equal("column_solid_unmeasured", ColumnConstraintOrder.SolidSpanProblem(0, 3000, null, 1));
        }
        private static string Source(string file)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void A_column_top_is_not_silently_ignored_when_the_family_reports_one_level_placement()
        {
            string source = Source("CreateElementsGeometry.cs");
            Assert.Contains("placement == FamilyPlacementType.TwoLevelsBased || p.Kind == \"structural_column\" || InCategory(p.Type, BuiltInCategory.OST_StructuralColumns)", source);
            Assert.Contains("column_solid_base", source);
            Assert.Contains("column_solid_top", source);
        }

    }
}
