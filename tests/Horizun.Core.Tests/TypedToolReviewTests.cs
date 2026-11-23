using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class TypedToolReviewTests
    {
        [Theory]
        [InlineData("full")]
        [InlineData("compact")]
        [InlineData("summary")]
        public void A_budget_stop_is_incomplete_in_every_response_shape_and_cannot_be_a_complete_cache_entry(string mode)
        {
            var result = new JObject { ["coverage_complete"] = true, ["matched_total"] = 4,
                ["rows"] = new JArray(1, 2), ["next_cursor"] = "prefix-page-2" };
            QueryResponseOptions.ApplyCollectionCoverage(result, false, new JObject { ["more_remains"] = true });
            result = QueryResponseOptions.Shape(result, mode);
            Assert.False((bool)result["coverage_complete"]);
            Assert.True((bool)result["partial"]);
            Assert.False((bool)result["collection_complete"]);
            Assert.Equal(4, (int)result["matched_total"]);
            Assert.False((bool)result["cooperative"]["collection_continuation_available"]);
            Assert.Contains("cannot resume", (string)result["cooperative"]["cursor_means"]);
            if (mode == "summary") Assert.Contains("observed matched set", (string)result["note"]);
        }

        [Fact]
        public void No_cooperative_request_preserves_shape_and_finished_pagination_is_not_a_partial_collection()
        {
            var result = new JObject { ["coverage_complete"] = true, ["truncated"] = true };
            var expected = result.DeepClone();
            QueryResponseOptions.ApplyCollectionCoverage(result, null);
            Assert.True(JToken.DeepEquals(expected, result));
            QueryResponseOptions.ApplyCollectionCoverage(result, true);
            Assert.True((bool)result["coverage_complete"]);
            Assert.False((bool)result["partial"]);
            Assert.True((bool)result["truncated"]);
        }

        [Fact]
        public void Cloud_open_failure_preserves_only_this_open_dialogs_and_never_claims_nothing_opened()
        {
            var seen = new List<Interruption>
            {
                new Interruption { Kind = "dialog", Description = "Earlier operation", Answered = "Cancel" },
                new Interruption { Kind = "dialog", Description = "Unresolved references", Answered = "Cancel" },
                new Interruption { Kind = "warning", Description = "Model warning", Answered = "Recorded" }
            };
            JObject result = CloudOpenFailureRules.Describe("Opening was canceled.", "Previous model", "US", "project-guid", "model-guid", seen, 1, true);
            Assert.Single((JArray)result["dialogs"]);
            Assert.Single((JArray)result["failures"]);
            Assert.Equal("Unresolved references", (string)result["dialogs"][0]["description"]);
            Assert.Equal(JTokenType.Null, result["opened"].Type);
            Assert.False((bool)result["confirmed_active"]);
            Assert.Contains("Unresolved references", (string)result["message"]);
            Assert.DoesNotContain("Earlier operation", (string)result["message"]);
        }

        [Fact]
        public void Unobserved_cloud_open_is_not_a_clean_empty_dialog_list()
        {
            JObject result = CloudOpenFailureRules.Describe("No document returned.", null, "US", "p", "m", null, 0, false);
            Assert.False((bool)result["revit_raised_observed"]);
            Assert.Contains("not fully observed", (string)result["note"]);
            Assert.Empty((JArray)result["dialogs"]);
        }

        [Fact]
        public void Capture_checks_the_requested_document_before_view_resolution_and_export()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
            Assert.NotNull(directory);
            string source = File.ReadAllText(Path.Combine(directory.FullName, "src", "Horizun.Revit", "Commands", "CaptureViewCommand.cs"));
            int guard = source.IndexOf("DocumentGate.ReadGuard(doc, req, Name)", StringComparison.Ordinal);
            Assert.True(guard > 0);
            Assert.True(guard < source.IndexOf("// Resolve the view", StringComparison.Ordinal));
            Assert.True(guard < source.IndexOf("doc.ExportImage(opts)", StringComparison.Ordinal));
        }
    }
}
