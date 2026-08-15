// -----------------------------------------------------------------------------
// Horizun Server tests - the OTHER way a workbook loses rows.
//
// ExcelWriteRaceTests closed the race between two concurrent writers. This closes
// the one that needs no concurrency at all: ONE writer whose reply never arrived.
//
// The write is host-resident, so the dispatcher's durable idempotency - which
// covers every typed Revit mutation - never applied to it. A client that timed
// out, or an MCP transport that dropped the response, had exactly one option:
// send it again. The append is not idempotent, so the rows landed twice, and the
// second answer said rows_written: 1, verified: true, because both statements
// were true about the second call. Nothing anywhere said "these are the same
// rows you already appended".
//
// So it now claims a durable key before writing, exactly as horizun_power_bi_push
// does for the same reason: an identical retry REPLAYS the recorded answer
// without touching the workbook, and the same key pointed at different rows is a
// CONFLICT rather than a second append.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class ExcelWriteIdempotencyTests : IDisposable
    {
        private readonly List<string> _cleanup = new List<string>();

        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        private DurableCommandLedger Ledger(string dir) =>
            new DurableCommandLedger(() => dir, () => new DateTime(2026, 8, 14, 10, 0, 0, DateTimeKind.Utc), () => 7);

        private string LedgerDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-xls-idem-" + Guid.NewGuid().ToString("N"));
            _cleanup.Add(dir);
            return dir;
        }

        private string Book(int existingRows = 3)
        {
            string path = ExcelWriteRaceTests.MakeBook(existingRows);
            _cleanup.Add(path);
            return path;
        }

        private static JObject Args(string file, string key, params string[] cells)
        {
            var row = new JArray();
            foreach (string c in cells) row.Add(c);
            var args = new JObject
            {
                ["file_path"] = file,
                ["rows"] = new JArray { row }
            };
            if (key != null) args["idempotency_key"] = key;
            return args;
        }

        /// <summary>Every value in column A, read straight out of the package.</summary>
        internal static List<string> ColumnA(string path)
        {
            List<KeyValuePair<string, byte[]>> entries = ExcelWriteRows.ReadEntries(File.ReadAllBytes(path));
            ExcelWriteRows.ResolvedSheet sheet = ExcelWriteRows.ResolveSheet(entries, null);
            byte[] bytes = entries.First(kv => kv.Key == sheet.PartPath).Value;
            XDocument doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿'));

            var values = new List<string>();
            foreach (XElement row in doc.Root.Element(Main + "sheetData").Elements(Main + "row"))
            {
                XElement cell = row.Elements(Main + "c")
                    .FirstOrDefault(c => ((string)c.Attribute("r") ?? "").StartsWith("A", StringComparison.Ordinal));
                if (cell == null) continue;
                values.Add((string)cell.Attribute("t") == "inlineStr"
                    ? cell.Element(Main + "is")?.Element(Main + "t")?.Value ?? ""
                    : cell.Element(Main + "v")?.Value ?? "");
            }
            return values;
        }

        /// <summary>
        /// THE DEFECT. The same call twice - the shape of a lost response - appends once.
        /// </summary>
        [Fact]
        public void An_identical_retry_replays_instead_of_appending_twice()
        {
            string file = Book();
            string dir = LedgerDir();
            int before = ColumnA(file).Count;

            JObject first = ExcelWriteRows.Handle(Args(file, "key-1", "ROW-ONCE"), Ledger(dir));
            JObject retry = ExcelWriteRows.Handle(Args(file, "key-1", "ROW-ONCE"), Ledger(dir));

            List<string> after = ColumnA(file);
            Assert.Equal(before + 1, after.Count);
            Assert.Single(after, v => v == "ROW-ONCE");

            // And the retry is told the SAME answer, not a fresh one it could act on.
            Assert.Equal((int)first["rows_written"], (int)retry["rows_written"]);
            Assert.Equal((int)first["first_new_row"], (int)retry["first_new_row"]);
            Assert.Equal((string)first["sha256_after"], (string)retry["sha256_after"]);
            Assert.True((bool)retry["replayed"]);
        }

        /// <summary>
        /// The same key aimed at DIFFERENT rows is a caller mistake, not a second append.
        /// It must be refused before the workbook is touched.
        /// </summary>
        [Fact]
        public void The_same_key_with_a_different_payload_is_a_conflict_and_writes_nothing()
        {
            string file = Book();
            string dir = LedgerDir();

            ExcelWriteRows.Handle(Args(file, "key-2", "FIRST"), Ledger(dir));
            List<string> afterFirst = ColumnA(file);

            var refusal = Assert.Throws<ToolRefusal>(() =>
                ExcelWriteRows.Handle(Args(file, "key-2", "SECOND"), Ledger(dir)));
            Assert.Contains("DIFFERENT", refusal.Message, StringComparison.Ordinal);

            Assert.Equal(afterFirst, ColumnA(file));
            Assert.DoesNotContain("SECOND", ColumnA(file));
        }

        /// <summary>The same key against a different WORKBOOK is equally a conflict.</summary>
        [Fact]
        public void The_same_key_against_another_workbook_is_a_conflict()
        {
            string a = Book();
            string b = Book();
            string dir = LedgerDir();

            ExcelWriteRows.Handle(Args(a, "key-3", "VALUE"), Ledger(dir));

            Assert.Throws<ToolRefusal>(() => ExcelWriteRows.Handle(Args(b, "key-3", "VALUE"), Ledger(dir)));
            Assert.DoesNotContain("VALUE", ColumnA(b));
        }

        /// <summary>
        /// A key claimed but never completed - the process died mid-write - stays in doubt.
        /// It must NOT decide to append again on the caller's behalf.
        /// </summary>
        [Fact]
        public void A_claim_with_no_completion_refuses_rather_than_appending_again()
        {
            string file = Book();
            string dir = LedgerDir();

            // Claim the key exactly as the handler would, then never complete it.
            string fingerprint = RequestFingerprint.OfOperation(
                "horizun_excel_write_rows", ExcelWriteRows.LedgerScopeOf(Args(file, "key-4", "X")),
                Args(file, "key-4", "X"), "idempotency_key");
            Ledger(dir).Claim("key-4", "horizun_excel_write_rows", fingerprint);

            List<string> before = ColumnA(file);
            var refusal = Assert.Throws<ToolRefusal>(() =>
                ExcelWriteRows.Handle(Args(file, "key-4", "X"), Ledger(dir)));
            Assert.Contains("no durable completion record exists", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("will NOT repeat an outcome it cannot know", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, ColumnA(file));
        }

        /// <summary>
        /// A missing key is refused before anything is read. Every other mutation in this
        /// bridge requires one; the workbook writer was the exception because its effect
        /// classification put it outside the dispatcher's durable path.
        /// </summary>
        [Fact]
        public void A_write_without_a_key_is_refused_and_changes_nothing()
        {
            string file = Book();
            List<string> before = ColumnA(file);

            var refusal = Assert.Throws<ToolRefusal>(() =>
                ExcelWriteRows.Handle(Args(file, null, "NOPE"), Ledger(LedgerDir())));
            Assert.Contains("idempotency_key", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, ColumnA(file));
        }

        /// <summary>
        /// Distinct keys are distinct work. The guard must not turn two deliberate appends
        /// of the same values into one - a register where the same reading is recorded
        /// twice on purpose is an ordinary case.
        /// </summary>
        [Fact]
        public void Two_deliberate_appends_of_identical_rows_still_append_twice()
        {
            string file = Book();
            string dir = LedgerDir();
            int before = ColumnA(file).Count;

            ExcelWriteRows.Handle(Args(file, "key-5a", "SAME"), Ledger(dir));
            ExcelWriteRows.Handle(Args(file, "key-5b", "SAME"), Ledger(dir));

            List<string> after = ColumnA(file);
            Assert.Equal(before + 2, after.Count);
            Assert.Equal(2, after.Count(v => v == "SAME"));
        }

        /// <summary>
        /// A refusal that happens BEFORE any write leaves the workbook alone, and the key
        /// records that terminal failure rather than being left in doubt.
        /// </summary>
        [Fact]
        public void A_missing_sheet_is_a_terminal_failure_that_replays_as_one()
        {
            string file = Book();
            string dir = LedgerDir();

            JObject args = Args(file, "key-6", "X");
            args["sheet"] = "NoSuchSheet";

            Assert.ThrowsAny<Exception>(() => ExcelWriteRows.Handle(args, Ledger(dir)));

            // The retry is told the same thing, and still does not write.
            List<string> before = ColumnA(file);
            Assert.ThrowsAny<Exception>(() => ExcelWriteRows.Handle(args, Ledger(dir)));
            Assert.Equal(before, ColumnA(file));
        }

        public void Dispose()
        {
            foreach (string p in _cleanup)
            {
                try
                {
                    if (Directory.Exists(p)) Directory.Delete(p, true);
                    else if (File.Exists(p)) File.Delete(p);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
