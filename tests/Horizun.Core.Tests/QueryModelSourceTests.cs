using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class QueryModelSourceTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Collector_has_a_native_filter_before_any_iteration(bool includeTypes)
        {
            var doc = new Document { TestRequireNativeCollectorFilter = true };
            doc.Elements.Add(1, new Element { Id = new ElementId(1) });
            doc.Elements.Add(2, new ElementType { Id = new ElementId(2) });
            var ids = QueryModelCommand.CollectCategoryCandidates(doc, null, null, includeTypes, out _)
                .Select(element => element.Id.Value);
            Assert.Equal(includeTypes ? new[] { 1L, 2L } : new[] { 1L }, ids);
        }

        [Fact]
        public void A_built_in_category_without_a_category_object_still_filters_and_an_unknown_name_is_reported()
        {
            // DOC-30c: OST_Viewers has no Category object in Revit 2025/2026; the name was dropped and the
            // per-element check skipped everything, so a plan with a visible section mark counted zero.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "QueryModelCommand.cs"));
            int resolve = source.IndexOf("private static HashSet<long> ResolveCategories(", StringComparison.Ordinal);
            Assert.True(resolve >= 0);
            string body = source.Substring(resolve, source.IndexOf("return ids;", resolve, StringComparison.Ordinal) - resolve);
            Assert.Contains("rawBuiltIns.Add(raw);", body, StringComparison.Ordinal);
            Assert.Contains("Enum.IsDefined(typeof(BuiltInCategory), bic)", body, StringComparison.Ordinal);
            Assert.Contains("CategoryNameRules.Classify(category != null, definedBuiltIn)", body, StringComparison.Ordinal);
            Assert.Contains("categoryNames?.Read(name, resolution, tableReadFailed);", body, StringComparison.Ordinal);
            Assert.Contains("is neither a BuiltInCategory nor a category name of any model read", source, StringComparison.Ordinal);
            Assert.Contains("foreach (string unknown in categoryNames.UnresolvedEverywhere())", source, StringComparison.Ordinal);
            Assert.Equal(2, CountOf(source, "hostUnit, categoryNames);"));
            Assert.Contains("builtInOnly != null && builtInOnly.PassesFilter(element)", source, StringComparison.Ordinal);
        }

        private static int CountOf(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal)) count++;
            return count;
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
            Assert.Contains("StreamingDistinct.ByKey(candidates.Concat(textTypes), e => Rid.Value(e.Id))", source, StringComparison.Ordinal);
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
                dir.FullName, "src", "Horizun.Revit", "Commands", "ManageViewsCommand.Execution.cs"));

            // The batch (null guard, then in-transaction verification) runs in RunBatch, shared by the
            // apply and the rehearsal; the apply commits only after RunBatch returned.
            int batch = source.IndexOf("private static void RunBatch(", StringComparison.Ordinal);
            int nullGuard = source.IndexOf("returned no element", batch, StringComparison.Ordinal);
            int reversible = source.IndexOf("failed verification while the transaction", batch, StringComparison.Ordinal);
            Assert.True(batch >= 0 && nullGuard > batch && reversible > nullGuard,
                "null and postcondition failures must be refused while the transaction can still roll back");
            int run = source.IndexOf("RunBatch(doc, input, scale, aliases, applied);", StringComparison.Ordinal);
            int commit = source.IndexOf("Guard.Commit(tx, txName);", run, StringComparison.Ordinal);
            Assert.True(run >= 0 && commit > run && commit - run < 200,
                "the apply must commit only after the batch ran and verified");
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
