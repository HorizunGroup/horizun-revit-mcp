using System;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class QueryModelSourceTests
    {
        [Fact]
        public void Collector_has_a_native_filter_before_any_iteration()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "QueryModelCommand.cs"));
            int collector = source.IndexOf("FilteredElementCollector collector", StringComparison.Ordinal);
            int instances = source.IndexOf("WhereElementIsNotElementType()", collector, StringComparison.Ordinal);
            int all = source.IndexOf("collector.WherePasses(", collector, StringComparison.Ordinal);
            int iteration = source.IndexOf("foreach (Element element in candidates)", collector, StringComparison.Ordinal);
            Assert.True(collector >= 0 && instances > collector && all > collector && iteration > instances && iteration > all,
                "Revit throws when a FilteredElementCollector is iterated without a native ElementFilter.");
        }

        [Fact]
        public void Text_note_types_have_an_explicit_class_sweep_when_types_are_requested()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(
                dir.FullName, "src", "Horizun.Revit", "Commands", "QueryModelCommand.cs"));

            Assert.Contains("RequestsTextNotes(categories)", source, StringComparison.Ordinal);
            Assert.Contains(".OfClass(typeof(TextNoteType))", source, StringComparison.Ordinal);
            Assert.Contains("GroupBy(e => Rid.Value(e.Id))", source, StringComparison.Ordinal);
            Assert.Contains("element is TextNoteType", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Manage_views_refuses_a_null_result_before_commit_and_verifies_reversibly()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(
                dir.FullName, "src", "Horizun.Revit", "Commands", "ManageViewsCommand.cs"));

            int nullGuard = source.IndexOf("returned no element", StringComparison.Ordinal);
            int reversible = source.IndexOf("failed verification while the transaction", StringComparison.Ordinal);
            int commit = source.IndexOf("Guard.Commit(tx, txName);", nullGuard, StringComparison.Ordinal);
            Assert.True(nullGuard >= 0 && reversible > nullGuard && commit > reversible,
                "null and postcondition failures must be refused while the transaction can still roll back");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_model_names_never_become_empty_json_object_keys(string value)
        {
            Assert.Equal("(blank)", JsonObjectKey.Summary(value));
        }

        [Fact]
        public void Meaningful_model_names_are_preserved_verbatim()
        {
            Assert.Equal("Level 01", JsonObjectKey.Summary("Level 01"));
        }

        [Fact]
        public void Summary_keys_that_differ_only_by_case_are_combined()
        {
            var counts = JsonObjectKey.SummaryCounts(new[] { "Center line", "Center Line", "CENTER LINE" });

            Assert.Single(counts.Properties());
            Assert.Equal(3, counts.Value<int>("Center line"));
        }
    }
}
