using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PdfOverwriteRulesTests
    {
        [Fact]
        public void Long_pdf_approval_uses_one_issuance_instant_and_is_spent_before_export()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new ConfirmationStore(() => { DateTime result = now; now = now.AddSeconds(1); return result; });
            TimeSpan ttl = ExportDeliveryRules.TokenTtl(20);
            Confirmation issued = store.Issue("export", "doc", "plan", ttl);
            Assert.Equal(ttl, issued.ExpiresUtc - issued.IssuedUtc);
            Assert.True(store.Validate(issued.Token, "export", "doc", "plan").Ok);
            now = issued.ExpiresUtc.AddHours(1);
            Assert.Equal(ConfirmationState.AlreadyUsed, store.Validate(issued.Token, "export", "doc", "plan").State);
        }

        [Fact]
        public void A_page_count_failure_keeps_the_failing_file_in_the_evidence()
        {
            string dir = Directory.GetCurrentDirectory();
            while (dir != null && !File.Exists(Path.Combine(dir, "src/Horizun.Revit/Commands/ExportCommand.cs"))) dir = Path.GetDirectoryName(dir);
            string source = File.ReadAllText(Path.Combine(dir, "src/Horizun.Revit/Commands/ExportCommand.cs"));
            int inspected = source.IndexOf("JObject file=DeliveryPdf.Inspect", StringComparison.Ordinal);
            int recorded = source.IndexOf("verifiedPdf.Add(file)", inspected, StringComparison.Ordinal);
            int refusal = source.IndexOf("PDF page count mismatch.", inspected, StringComparison.Ordinal);
            Assert.True(inspected >= 0 && recorded > inspected && recorded < refusal);
        }
        [Fact]
        public void Missing_backup_does_not_delete_the_file_currently_at_original_path()
        {
            string directory = Path.Combine(Path.GetTempPath(), "pdf-missing-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string original = Path.Combine(directory, "one.pdf");
                string missing = Path.Combine(directory, "missing.tmp");
                File.WriteAllText(original, "current file");
                var moved = new List<AsideFile> { new AsideFile { Original = original, Aside = missing } };
                var result = PdfOverwriteRules.RestoreAsides(moved);
                Assert.Single(result);
                Assert.NotNull(result[0].Error);
                Assert.Equal(missing, result[0].OldCopy);
                Assert.Equal("current file", File.ReadAllText(original));
            }
            finally { Directory.Delete(directory, true); }
        }
        [Fact]
        public void Failure_on_second_move_restores_first_original_with_real_files()
        {
            string directory = Path.Combine(Path.GetTempPath(), "pdf-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string first = Path.Combine(directory, "one.pdf");
                string second = Path.Combine(directory, "two.pdf");
                File.WriteAllText(first, "original one");
                File.WriteAllText(second, "original two");
                var moved = new List<AsideFile>();
                int calls = 0;
                Assert.Throws<IOException>(() => PdfOverwriteRules.MoveAside(
                    new[] { first, second }, DateTime.UnixEpoch, moved,
                    (from, to) => { if (++calls == 2) throw new IOException("locked"); File.Move(from, to); }));
                var restored = PdfOverwriteRules.RestoreAsides(moved);
                Assert.Single(restored);
                Assert.Null(restored[0].Error);
                Assert.Equal("original one", File.ReadAllText(first));
                Assert.Equal("original two", File.ReadAllText(second));
                Assert.False(File.Exists(PdfOverwriteRules.AsidePath(first, DateTime.UnixEpoch)));
            }
            finally { Directory.Delete(directory, true); }
        }
        [Fact]
        public void Failed_second_move_keeps_first_move_in_recovery_ledger()
        {
            var moved = new List<AsideFile>();
            int calls = 0;
            Assert.Throws<IOException>(() => PdfOverwriteRules.MoveAside(
                new[] { "one.pdf", "two.pdf" }, DateTime.UnixEpoch, moved,
                (from, to) => { if (++calls == 2) throw new IOException("locked"); }));
            Assert.Single(moved);
            Assert.Equal("one.pdf", moved[0].Original);
            Assert.Equal(PdfOverwriteRules.AsidePath("one.pdf", DateTime.UnixEpoch), moved[0].Aside);
        }
        [Fact]
        public void Only_existing_pdfs_are_moved_aside_and_only_when_overwrite_was_asked()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\out\A-101.pdf", @"C:\out\A-103.pdf" };
            var planned = new[] { @"C:\out\A-101.pdf", @"C:\out\A-102.pdf", @"C:\out\A-103.pdf", @"c:\OUT\a-101.pdf", "" };
            Assert.Equal(new[] { @"C:\out\A-101.pdf", @"C:\out\A-103.pdf" }, PdfOverwriteRules.ToMoveAside(planned, true, existing.Contains).ToArray());
            Assert.Empty(PdfOverwriteRules.ToMoveAside(planned, false, existing.Contains));
            Assert.Empty(PdfOverwriteRules.ToMoveAside(null, true, existing.Contains));
        }

        [Fact]
        public void The_aside_name_never_equals_the_original_and_is_not_a_pdf()
        {
            string aside = PdfOverwriteRules.AsidePath(@"C:\out\A-101.pdf", new DateTime(2026, 10, 21, 13, 5, 9, 123, DateTimeKind.Utc));
            Assert.Equal(@"C:\out\A-101.pdf.replaced-20261021130509123.tmp", aside);
            Assert.NotEqual(@"C:\out\A-101.pdf", aside);
            Assert.False(aside.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Success_drops_the_old_copies_and_failure_puts_them_back()
        {
            Assert.Equal("delete_aside", PdfOverwriteRules.Decide(true));
            Assert.Equal("restore_aside", PdfOverwriteRules.Decide(false));
        }

        [Fact]
        public void Moving_aside_and_back_really_round_trips_a_file_on_disk()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-pdf-aside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string original = Path.Combine(dir, "A-101.pdf");
                File.WriteAllText(original, "old");
                string aside = PdfOverwriteRules.AsidePath(original, DateTime.UtcNow);
                File.Move(original, aside);
                Assert.False(File.Exists(original));
                File.WriteAllText(original, "partial new"); // what a failed export may leave
                File.Delete(original);
                File.Move(aside, original);
                Assert.Equal("old", File.ReadAllText(original));
                Assert.False(File.Exists(aside));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
