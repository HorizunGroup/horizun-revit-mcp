using System;
using System.IO;
using Horizun.Revit.Commands;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExportCommandRecoveryTests
    {
        [Fact]
        public void Failed_second_move_restores_first_and_reports_it_without_calling_exporter()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-export-command-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string first = Path.Combine(dir, "one.pdf"), second = Path.Combine(dir, "two.pdf");
                File.WriteAllText(first, "original one");
                File.WriteAllText(second, "original two");
                int moves = 0, exports = 0;
                var result = ExportCommand.ExecuteExportAttempt("pdf", new[] { first, second }, true,
                    (from, to) => { if (++moves == 2) throw new IOException("simulated move failure"); File.Move(from, to); },
                    () => { exports++; return true; }, () => { }, () => null, () => null,
                    out var asides, out var restored, out var accepted);
                Assert.False(result.Success);
                Assert.Contains("simulated move failure", result.Error);
                Assert.Equal(0, exports);
                Assert.Equal("original one", File.ReadAllText(first));
                Assert.Equal("original two", File.ReadAllText(second));
                Assert.Equal(first, (string)Assert.Single(result.Detail["previous_pdfs_restored"]));
                Assert.Empty(asides);
                Assert.False(accepted);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
