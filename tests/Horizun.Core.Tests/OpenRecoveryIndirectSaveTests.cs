using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OpenRecoveryIndirectSaveTests
    {
        private static string Source(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", name));
        }

        [Fact]
        public void Observer_describes_a_resolution_request_without_claiming_its_effect()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Core", "Interference.cs"));
            Assert.Contains("Default resolution requested; its effect is not yet verified", source);
            Assert.DoesNotContain("Default resolution executed in memory", source);
        }

        [Fact]
        public void Recovery_exception_degrades_failure_observation()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Core", "Interference.cs"));
            int start = source.IndexOf("try { resolved = _recovery.TryResolve(fa, m); }", StringComparison.Ordinal);
            int end = source.IndexOf("if (resolved) resolvedAny", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start);
            Assert.Contains("_coverage.MarkProcessingFailure(\"failures\", recoveryError)", source.Substring(start, end - start));
        }

        [Fact]
        public void Only_resolution_requests_trigger_reprocessing_not_warning_deletion()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Core", "Interference.cs"));
            Assert.DoesNotContain("fa.DeleteWarning(m); dismissedAny = true", source);
            Assert.Contains("if (resolvedAny) e.SetProcessingResult(FailureProcessingResult.ProceedWithCommit)", source);
        }
    }
}
