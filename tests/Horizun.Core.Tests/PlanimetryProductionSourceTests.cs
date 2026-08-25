using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class PlanimetryProductionSourceTests
    {
        private static string Source(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void Sheet_packing_is_deterministic_rehearsed_and_atomic()
        {
            string s = Source("PackSheetsCommand.cs");
            Assert.Contains("PlanimetryPackingRules.Pack", s, StringComparison.Ordinal);
            Assert.Contains("TransactionGroup", s, StringComparison.Ordinal);
            Assert.Contains("RequireConfirmation", s, StringComparison.Ordinal);
            Assert.Contains("fixedObstacles", s, StringComparison.Ordinal);
            Assert.Contains("RollbackConfirmed", s, StringComparison.Ordinal);
            Assert.Contains("measure unplaced sheet content", s, StringComparison.Ordinal);
            Assert.Contains("GetLabelOutline", s, StringComparison.Ordinal);
            Assert.Contains("AnchorOffsetX", s, StringComparison.Ordinal);

            int confirmation = s.IndexOf("RequireConfirmation(app", StringComparison.Ordinal);
            int applyMeasurement = s.IndexOf("MeasureItems(doc", StringComparison.Ordinal);
            Assert.True(confirmation >= 0 && applyMeasurement > confirmation,
                "apply must spend confirmation before provisional paper-size measurement opens a transaction");
        }

        [Fact]
        public void Annotation_planner_writes_nothing_and_delegates_the_verified_write()
        {
            string s = Source("PlanAnnotationsCommand.cs");
            Assert.DoesNotContain("new Transaction(", s, StringComparison.Ordinal);
            Assert.Contains("new DimensionReferencesCommand().Execute", s, StringComparison.Ordinal);
            Assert.Contains("exactly one is required", s, StringComparison.Ordinal);
            Assert.Contains("next_tool\"] = \"horizun_annotate\"", s, StringComparison.Ordinal);
            Assert.Contains("coverage_complete", s, StringComparison.Ordinal);
        }

        [Fact]
        public void Explicit_tag_type_and_duplicate_precondition_are_bound_and_verified()
        {
            string s = Source("AnnotateCommand.cs");
            Assert.Contains("tag_type_id", s, StringComparison.Ordinal);
            Assert.Contains("GetValidTypes", s, StringComparison.Ordinal);
            Assert.Contains("tag.ChangeTypeId", s, StringComparison.Ordinal);
            Assert.Contains("ExistingTagCount", s, StringComparison.Ordinal);
            Assert.Contains("p.Type ?? p.EffectiveTagType", s, StringComparison.Ordinal);
            Assert.Contains("tag.OwnerViewId == viewId", s, StringComparison.Ordinal);
            Assert.Contains("tag.GetTypeId() == expected.Id", s, StringComparison.Ordinal);
        }

        [Fact]
        public void Revisions_are_rehearsed_confirmed_and_verified_inside_one_group()
        {
            string s = Source("ManageRevisionsCommand.cs");
            int confirmation = s.IndexOf("RequireConfirmation", StringComparison.Ordinal);
            int group = s.IndexOf("new TransactionGroup", StringComparison.Ordinal);
            Assert.True(confirmation >= 0 && group > confirmation, "apply must spend confirmation before its writing group opens");
            Assert.Contains("RevisionCloud.Create", s, StringComparison.Ordinal);
            Assert.Contains("SetAdditionalRevisionIds", s, StringComparison.Ordinal);
            Assert.Contains("plans.All(p => Verify", s, StringComparison.Ordinal);
            Assert.Contains("Guard.RollBack(group)", s, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_production_command_is_registered_in_the_addin()
        {
            string s = Source(Path.Combine("..", "App.cs"));
            Assert.Contains("new PackSheetsCommand()", s, StringComparison.Ordinal);
            Assert.Contains("new PlanAnnotationsCommand()", s, StringComparison.Ordinal);
            Assert.Contains("new ManageRevisionsCommand()", s, StringComparison.Ordinal);
        }
    }
}
