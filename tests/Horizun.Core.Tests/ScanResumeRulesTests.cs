using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScanResumeRulesTests
    {
        [Fact]
        public void BudgetCutBeforeWeightResumesWithEverySourceAndCompletes()
        {
            var tracker = new ScanResumeRules.ProgressTracker();
            var order = new[] { "health", "links", "views", "sheets", "weight" };
            var sources = new[] { "health", "links" };
            var first = tracker.Resume("doc", order, new[] { "views", "sheets", "weight" }, sources, order);
            Assert.Equal(order, first);
            var second = tracker.Resume("doc", first, new[] { "sheets", "weight" }, sources, order);
            Assert.Equal(new[] { "health", "links", "sheets", "weight" }, second);
            Assert.Empty(tracker.Resume("doc", second, new string[0], sources, order));
        }

        [Fact]
        public void ResumeStopsAfterTwoConsecutiveAttemptsWithoutProgress()
        {
            var tracker = new ScanResumeRules.ProgressTracker();
            var order = new[] { "health", "weight" };
            var first = tracker.Resume("doc", order, new[] { "health", "weight" }, new[] { "health" }, order);
            Assert.Equal(order, first);
            Assert.Empty(tracker.Resume("doc", first, new[] { "health", "weight" }, new[] { "health" }, order));
        }
        [Fact]
        public void CooperativeDefaultIsReported()
        {
            var options = CooperativeOptions.ReadWithDefault(new JObject(), "scan", 120000);
            var scope = options.Begin("scan", 0, zeroIsUnlimited: true);
            Assert.Equal(120000, (int)scope.Report()["ui_budget_ms"]);
            var report = options.Report(scope, 0, "scan", false);
            Assert.True((bool)report["default_applied"]);
            Assert.Equal("applied by default; send cooperative.ui_budget_ms to change it", (string)report["default_note"]);
        }

        [Theory]
        [InlineData(45000)]
        [InlineData(0)]
        public void CooperativeExplicitBudgetIsRespected(int budget)
        {
            var request = new JObject { ["cooperative"] = new JObject { ["ui_budget_ms"] = budget } };
            var options = CooperativeOptions.ReadWithDefault(request, "scan", 120000);
            var scope = options.Begin("scan", 0, zeroIsUnlimited: true);
            Assert.Equal(budget, (int)scope.Report()["ui_budget_ms"]);
            Assert.Null(options.Report(scope, 0, "scan", false)["default_applied"]);
            Assert.True(scope.Poll());
            Assert.True(scope.Continue());
        }

        [Fact]
        public void CostEstimateReportsCountsAndBudgetWithoutClaimingTime()
        {
            var estimate = ScanResumeRules.CostEstimate(1500, 3, 120000);
            Assert.Equal(4, estimate.Count);
            Assert.Equal(1500, (int)estimate["elements"]);
            Assert.Equal(3, (int)estimate["sections_requested"]);
            Assert.Equal(120000, (int)estimate["budget_ms"]);
            Assert.Contains("not an estimate of time", (string)estimate["note"]);
        }

        [Fact]
        public void ResumePreservesSectionOrder() =>
            Assert.Equal(new[] { "health", "links" }, ScanResumeRules.ResumeSections(new[] { "health", "links" }, new string[0], new string[0]));

        [Fact]
        public void NothingUnmeasuredMeansNoResume() =>
            Assert.Empty(ScanResumeRules.ResumeSections(new string[0], new[] { "health" }, new[] { "health", "weight" }));

        [Fact]
        public void WeightResumeIncludesMissingSourcesBeforeWeight() =>
            Assert.Equal(new[] { "health", "links", "weight" }, ScanResumeRules.ResumeSections(
                new[] { "weight" }, new[] { "health", "links" }, new[] { "health", "links", "weight" }));

        [Fact]
        public void WeightResumeDoesNotDuplicateAlreadyUnmeasuredSources() =>
            Assert.Equal(new[] { "health", "links", "weight" }, ScanResumeRules.ResumeSections(
                new[] { "links", "weight" }, new[] { "health", "links" }, new[] { "health", "links", "weight" }));

        [Fact]
        public void WeightResumeFollowsSectionOrder() =>
            Assert.Equal(new[] { "links", "health", "weight" }, ScanResumeRules.ResumeSections(
                new[] { "weight" }, new[] { "health", "links" }, new[] { "links", "health", "weight" }));

        [Fact]
        public void WeightSourcesAreDeclaredScanSections()
        {
            var root = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", "ModelScanCommand.cs")))
                root = root.Parent;
            Assert.NotNull(root);
            string source = File.ReadAllText(Path.Combine(root.FullName, "src", "Horizun.Revit", "Commands", "ModelScanCommand.cs"));
            int start = source.IndexOf("AllSections =", System.StringComparison.Ordinal);
            Assert.True(start >= 0);
            int end = source.IndexOf("};", start, System.StringComparison.Ordinal);
            Assert.True(end > start);
            var sections = Regex.Matches(source.Substring(start, end - start), "\"([a-z_]+)\"")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet();
            Assert.NotEmpty(WeightAttributionFromScan.SourceSections);
            Assert.All(WeightAttributionFromScan.SourceSections, section => Assert.Contains(section, sections));
        }

        [Fact]
        public void WeightIdentifiesMissingSource() =>
            Assert.Equal(new[] { "health" }, ScanResumeRules.WeightMissingSources(
                new HashSet<string> { "links" }, new[] { "links", "health" }));

        [Fact]
        public void WeightHasNoMissingSourcesWhenAllMeasured() =>
            Assert.Empty(ScanResumeRules.WeightMissingSources(
                new HashSet<string> { "links", "health" }, new[] { "links", "health" }));
    }
}
