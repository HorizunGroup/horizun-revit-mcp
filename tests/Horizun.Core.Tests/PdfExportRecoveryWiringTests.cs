using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PdfExportRecoveryWiringTests
    {
        [Fact]
        public void No_produced_file_failure_reports_restoration_results()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "ExportCommand.cs"));
            int start = source.IndexOf("// Nothing new was measured:", StringComparison.Ordinal);
            int end = source.IndexOf("// Non-PDF formats produce", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start);
            Assert.Contains("[\"previous_pdfs_restored\"]", source.Substring(start, end - start));
        }

        [Fact]
        public void Verification_failure_restores_before_backups_are_deleted()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "ExportCommand.cs"));
            int verificationFailure = source.IndexOf("PdfVerification.FailureText(ex)", StringComparison.Ordinal);
            int delete = source.IndexOf("File.Delete(moved.Aside)", StringComparison.Ordinal);
            Assert.True(verificationFailure >= 0 && delete > verificationFailure);
            Assert.Contains("[\"previous_pdfs_restored\"] = RestoreAsides()", source);
        }
    }
}
