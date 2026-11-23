// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_measure_geometry selection=true WITH AN ELEMENT PICKED INSIDE A LINK.
//
// Live finding (Revit 2025, release test): with two walls of a link selected by
// reference, horizun_selection_context named the link, the linked ids and their
// transformed boxes, but horizun_measure_geometry selection=true answered "nothing is
// selected" - Revit keeps linked picks in GetReferences, not in GetElementIds.
// The arithmetic that carries a link box into host coordinates is tested here; the
// wiring (references read, rows marked as link, transform applied) is guarded in source.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MeasureLinkedSelectionTests
    {
        [Fact]
        public void A_translated_link_moves_the_box_by_the_offset()
        {
            Func<double[], double[]> move = p => new[] { p[0] + 100, p[1] + 20, p[2] };
            GeometryMeasureRules.HostEnvelope(new[] { 0.5, 29.2, 0 }, new[] { 12.0, 29.8, 9 }, move, out double[] min, out double[] max);
            Assert.Equal(new[] { 100.5, 49.2, 0 }, min);
            Assert.Equal(new[] { 112.0, 49.8, 9 }, max);
        }

        [Fact]
        public void A_rotated_link_envelopes_all_eight_corners_not_min_and_max()
        {
            // 90 degrees about Z then +10 in X: a 4 x 1 box becomes 1 x 4. Carrying only min
            // and max through the rotation would give an inverted, zero-width box.
            Func<double[], double[]> rot = p => new[] { -p[1] + 10, p[0], p[2] };
            GeometryMeasureRules.HostEnvelope(new[] { 0.0, 0, 0 }, new[] { 4.0, 1, 3 }, rot, out double[] min, out double[] max);
            Assert.Equal(new[] { 9.0, 0, 0 }, min);
            Assert.Equal(new[] { 10.0, 4, 3 }, max);
        }

        [Theory]
        [InlineData(0, 0, 1, 90)]
        [InlineData(30, 0, 1, 120)]
        [InlineData(170, 0, 1, -100)]
        [InlineData(-90, 1, 0, -90)]
        [InlineData(90, -1, 0, -90)]
        public void Point_rotation_adds_the_link_rotation_and_wraps(double local, double bx, double by, double expected)
        {
            Assert.Equal(expected, GeometryMeasureRules.HostRotationDegrees(local, bx, by), 6);
        }

        private static string Source()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", "MeasureGeometryCommand.cs"));
        }

        [Fact]
        public void Selection_reads_linked_references_and_does_not_refuse_them_as_empty()
        {
            string s = Source();
            Assert.Contains("Selection.GetReferences()", s);
            Assert.Contains("LinkedElementId", s);
            // The empty-selection refusal must consider the linked picks too.
            Assert.Contains("(picked == null || picked.Count == 0) && linked.Count == 0", s);
        }

        [Fact]
        public void Linked_rows_are_marked_and_measured_through_the_link_transform()
        {
            string s = Source();
            Assert.Contains("row[\"source\"] = \"link\"", s);
            Assert.Contains("row[\"link_instance_id\"]", s);
            Assert.Contains("row[\"coordinates\"] = \"host\"", s);
            Assert.Contains("GetTotalTransform()", s);
            Assert.Contains("SolidUtils.CreateTransformed", s);
            Assert.Contains("GeometryMeasureRules.HostEnvelope", s);
        }
    }
}
