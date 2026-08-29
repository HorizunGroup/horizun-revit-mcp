// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The CSV side of the append contract: created when absent, RFC-4180 quoted,
// re-read for its evidence, and at-most-once under the same durable ledger as
// the workbook path.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ExcelCsvWriteTests : IDisposable
    {
        private readonly string _dir;

        public ExcelCsvWriteTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-csv-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private DurableCommandLedger Ledger() =>
            new DurableCommandLedger(() => Path.Combine(_dir, "ledger"));

        private static JObject Args(string file, string key, params object[][] rows)
        {
            var rowsToken = new JArray();
            foreach (object[] row in rows) rowsToken.Add(new JArray(row));
            return new JObject
            {
                ["file_path"] = file, ["format"] = "csv", ["rows"] = rowsToken,
                ["idempotency_key"] = key
            };
        }

        [Fact]
        public void A_csv_is_created_appended_and_reread()
        {
            string file = Path.Combine(_dir, "out.csv");
            DurableCommandLedger ledger = Ledger();

            JObject first = ExcelWriteRows.Handle(Args(file, "k1", new object[] { "id", "x" }), ledger);
            Assert.True((bool)first["created"]);
            Assert.Equal(1, (int)first["rows_written"]);
            Assert.True((bool)first["verified_by_reread"]);
            Assert.Equal(64, ((string)first["sha256"]).Length);

            JObject second = ExcelWriteRows.Handle(Args(file, "k2", new object[] { "A-1", 12.5 }), ledger);
            Assert.False((bool)second["created"]);
            Assert.Equal(2, (int)second["total_lines_after"]);
            string[] lines = File.ReadAllLines(file);
            Assert.Equal("id,x", lines[0]);
            Assert.Equal("A-1,12.5", lines[1]);
        }

        [Fact]
        public void Fields_with_commas_quotes_and_newlines_are_quoted()
        {
            Assert.Equal("plain", ExcelWriteRows.CsvField("plain"));
            Assert.Equal("\"a,b\"", ExcelWriteRows.CsvField("a,b"));
            Assert.Equal("\"say \"\"hi\"\"\"", ExcelWriteRows.CsvField("say \"hi\""));
            Assert.Equal("\"two\nlines\"", ExcelWriteRows.CsvField("two\nlines"));
            Assert.Equal("", ExcelWriteRows.CsvField(null));
            Assert.Equal("true", ExcelWriteRows.CsvField(true));
        }

        [Fact]
        public void The_same_key_replays_instead_of_appending_twice()
        {
            string file = Path.Combine(_dir, "once.csv");
            DurableCommandLedger ledger = Ledger();
            JObject first = ExcelWriteRows.Handle(Args(file, "same", new object[] { "r1" }), ledger);
            JObject again = ExcelWriteRows.Handle(Args(file, "same", new object[] { "r1" }), ledger);
            Assert.Equal((string)first["sha256"], (string)again["sha256"]);
            Assert.Single(File.ReadAllLines(file));
        }

        [Fact]
        public void An_unknown_format_refuses()
        {
            string file = Path.Combine(_dir, "x.tsv");
            var args = Args(file, "k", new object[] { "a" });
            args["format"] = "tsv";
            Assert.Throws<ArgumentException>(() => ExcelWriteRows.Handle(args, Ledger()));
        }
    }
}
