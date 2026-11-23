using System.Linq;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class SheetAnnotationOverlapRulesTests
    {
        [Fact]
        public void ApproximateBoxesHaveMeasuredFalsePositiveAndFalseNegativeInSyntheticCases()
        {
            // Visible glyphs and model bounding boxes are deliberately different.
            var note = new[] { 0.0, 0.0, 1.0, 1.0 };
            var actualClear = new[] { 2.0, 2.0, 3.0, 3.0 };
            var inflatedModelBox = new[] { 0.5, 0.5, 3.0, 3.0 };
            var actualOverlap = new[] { 0.5, 0.5, 1.5, 1.5 };
            var undersizedModelBox = new[] { 1.1, 1.1, 1.5, 1.5 };
            Assert.False(NoteLeaderRules.Overlap(note, actualClear, 0));
            Assert.True(NoteLeaderRules.Overlap(note, inflatedModelBox, 0)); // false positive
            Assert.True(NoteLeaderRules.Overlap(note, actualOverlap, 0));
            Assert.False(NoteLeaderRules.Overlap(note, undersizedModelBox, 0)); // false negative
        }
        [Fact]
        public void LinkedConflictReportsApproximationAndActualGap()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.Notes.cs"));
            Assert.Contains("[\"approximate\"] = a.Kind == \"linked_symbol\" || b.Kind == \"linked_symbol\"", source);
            Assert.Contains("[\"gap_mm\"] = gap / FtPerMm", source);
        }
        [Fact]
        public void ApproximateLinkedBoxesAndMissingBoxesAreExplicitlyReported()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.Notes.cs"));
            Assert.Contains("linked family without a bounding box", source);
            Assert.Contains("false positives and false negatives", source);
        }
        [Fact]
        public void Dimension_and_text_overlap_is_reported_on_the_sheet_that_places_the_view()
        {
            var model = new PlanimetrySnapshot();
            model.Sheets.Add(new SheetFact { Id = 1, SheetNumber = "A-01" });
            model.Placements.Add(new PlacementFact { Id = 20, SheetId = 1, ViewId = 10,
                Box = PlanBox.FromCorners(0, 0, 10, 10) });
            model.Annotations.Add(new AnnotationFact { Id = 100, OwnerViewId = 10,
                Kind = "dimension", Box = PlanBox.FromCorners(0, 0, 2, 2) });
            model.Annotations.Add(new AnnotationFact { Id = 101, OwnerViewId = 10,
                Kind = "text_note", Box = PlanBox.FromCorners(1, 1, 3, 3) });
            var report = SheetAnnotationOverlapRules.Build(model, null, 1);
            Assert.Equal("overlaps", (string)report["status"]);
            Assert.Single(report["findings"].Children());
            Assert.Equal(1, (long)report["findings"][0]["sheet_id"]);
            Assert.Equal("view_plane", (string)report["findings"][0]["coordinate_system"]);
        }

        [Fact]
        public void Unmeasured_annotation_cannot_be_reported_as_clean()
        {
            var model = new PlanimetrySnapshot();
            model.Sheets.Add(new SheetFact { Id = 1, SheetNumber = "A-01" });
            model.Annotations.Add(new AnnotationFact { Id = 100, OwnerViewId = 1,
                Kind = "generic_annotation", Box = PlanBox.Unreadable });
            var report = SheetAnnotationOverlapRules.Build(model, null, 1);
            Assert.Equal("unknown", (string)report["status"]);
            Assert.Equal(1, (int)report["unmeasured"]);
        }
    }
}
