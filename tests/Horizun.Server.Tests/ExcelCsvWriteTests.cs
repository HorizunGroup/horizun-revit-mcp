// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The CSV side of the append contract: created when absent, RFC-4180 quoted,
// re-read for its evidence, and at-most-once under the same durable ledger as
// the workbook path.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;
using Horizun.Revit.Core;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ExcelCsvWriteTests : IDisposable
    {
        private readonly string _dir;
        private readonly ExcelBackupRoot _root = new ExcelBackupRoot();

        public ExcelCsvWriteTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-csv-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
            _root.Dispose();
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

        // ---- 2026-10-08 audit: the CSV path wrote anywhere, as anything. -------------

        [Theory]
        [InlineData("run.bat")]
        [InlineData("run.ps1")]
        [InlineData("report.csv.cmd")]
        [InlineData("report")]
        public void A_file_that_is_not_a_csv_is_refused_and_nothing_is_written(string name)
        {
            string file = Path.Combine(_dir, name);
            Assert.Throws<ToolRefusal>(() => ExcelWriteRows.Handle(Args(file, "k-" + name, new object[] { "echo hi" }), Ledger()));
            Assert.False(File.Exists(file));
        }

        [Fact]
        public void A_csv_under_a_forbidden_root_is_refused()
        {
            string forbidden = Path.Combine(_dir, "Startup");
            Directory.CreateDirectory(forbidden);
            string problem = ExcelWriteRows.CsvPathProblem(Path.Combine(forbidden, "x.csv"), new[] { forbidden });
            Assert.NotNull(problem);
            Assert.Contains("Startup", problem);
            Assert.Null(ExcelWriteRows.CsvPathProblem(Path.Combine(_dir, "ok.csv"), new[] { forbidden }));
            // A sibling whose name merely STARTS like the forbidden root is not under it.
            string sibling = Path.Combine(_dir, "Startup2");
            Directory.CreateDirectory(sibling);
            Assert.Null(ExcelWriteRows.CsvPathProblem(Path.Combine(sibling, "ok.csv"), new[] { forbidden }));
        }

        [Fact]
        public void The_machine_startup_folder_is_refused_on_windows()
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (string.IsNullOrEmpty(startup)) return;   // no such folder on this platform
            string file = Path.Combine(startup, "hz-audit-" + Guid.NewGuid().ToString("N") + ".csv");
            Assert.Throws<ToolRefusal>(() => ExcelWriteRows.Handle(Args(file, "k-startup", new object[] { "a" }), Ledger()));
            Assert.False(File.Exists(file));
        }

        [Theory]
        [InlineData(@"\\?\C:\temp\x.csv")]
        [InlineData(@"\\.\C:\temp\x.csv")]
        [InlineData(@"\\server\share\x.csv")]
        [InlineData("//server/share/x.csv")]
        [InlineData(@"relative\x.csv")]
        public void Device_unc_and_relative_paths_are_refused(string path)
        {
            Assert.NotNull(ExcelWriteRows.CsvPathProblem(path, new string[0]));
        }

        [Fact]
        public void A_missing_folder_is_refused_not_created()
        {
            string folder = Path.Combine(_dir, "does", "not", "exist");
            string file = Path.Combine(folder, "x.csv");
            Assert.Throws<ToolRefusal>(() => ExcelWriteRows.Handle(Args(file, "k-missing", new object[] { "a" }), Ledger()));
            Assert.False(Directory.Exists(Path.Combine(_dir, "does")));
        }

        [Theory]
        [InlineData("=1+1", "'=1+1")]
        [InlineData("+cmd", "'+cmd")]
        [InlineData("-2+3", "'-2+3")]
        [InlineData("@SUM(A1)", "'@SUM(A1)")]
        [InlineData("\tx", "'\tx")]
        [InlineData("\rx", "\"'\rx\"")]
        [InlineData("plain", "plain")]
        public void Text_a_spreadsheet_would_run_is_neutralised(string text, string expected)
        {
            Assert.Equal(expected, ExcelWriteRows.CsvField(text));
        }

        [Fact]
        public void A_negative_number_stays_a_number()
        {
            Assert.Equal("-2.5", ExcelWriteRows.CsvField(-2.5));
        }

        [Fact]
        public void A_formula_cell_lands_neutralised_in_the_file()
        {
            string file = Path.Combine(_dir, "f.csv");
            ExcelWriteRows.Handle(Args(file, "k-f", new object[] { "=1+1", 3 }), Ledger());
            Assert.Equal("'=1+1,3", File.ReadAllLines(file)[0]);
        }

        [Fact]
        public void Appending_to_a_1252_file_keeps_it_1252()
        {
            Encoding cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252);
            string file = Path.Combine(_dir, "ansi.csv");
            File.WriteAllBytes(file, cp1252.GetBytes("código,año\r\n"));

            JObject r = ExcelWriteRows.Handle(Args(file, "k-ansi", new object[] { "niño", "señal" }), Ledger());

            Assert.Equal("código,año\r\nniño,señal\r\n", cp1252.GetString(File.ReadAllBytes(file)));
            Assert.Equal("windows-1252", (string)r["encoding"]);
        }

        [Fact]
        public void Appending_to_a_utf8_bom_file_keeps_the_bom()
        {
            string file = Path.Combine(_dir, "bom.csv");
            var withBom = new UTF8Encoding(true);
            File.WriteAllBytes(file, Concat(withBom.GetPreamble(), withBom.GetBytes("a,b\r\n")));

            JObject r = ExcelWriteRows.Handle(Args(file, "k-bom", new object[] { "ñ", 1 }), Ledger());

            byte[] bytes = File.ReadAllBytes(file);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, new[] { bytes[0], bytes[1], bytes[2] });
            Assert.Equal("a,b\r\nñ,1\r\n", new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
            Assert.Equal("utf-8-bom", (string)r["encoding"]);
        }

        [Fact]
        public void A_text_the_1252_file_cannot_hold_is_refused_not_turned_into_question_marks()
        {
            Encoding cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252);
            string file = Path.Combine(_dir, "ansi2.csv");
            byte[] before = cp1252.GetBytes("año\r\n");
            File.WriteAllBytes(file, before);
            Assert.Throws<ToolRefusal>(() =>
                ExcelWriteRows.Handle(Args(file, "k-ansi2", new object[] { "\u2265 3 \u4e2d" }), Ledger()));
            Assert.Equal(before, File.ReadAllBytes(file));
        }

        [Fact]
        public void Appending_to_a_semicolon_file_uses_semicolon_and_decimal_comma()
        {
            var saved = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                foreach (string culture in new[] { "en-US", "es-CO" })
                {
                    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
                    string file = Path.Combine(_dir, "semi-" + culture + ".csv");
                    File.WriteAllText(file, "marca;peso_kg\r\n", new UTF8Encoding(false));
                    JObject r = ExcelWriteRows.Handle(
                        Args(file, "k-semi-" + culture, new object[] { "V-1; eje A", 12.5, 3 }), Ledger());
                    Assert.Equal("\"V-1; eje A\";12,5;3", File.ReadAllLines(file)[1]);
                    Assert.Equal(";", (string)r["delimiter"]);
                }
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
        }

        [Fact]
        public void A_comma_file_written_under_es_CO_keeps_the_decimal_point()
        {
            var saved = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-CO");
                string file = Path.Combine(_dir, "comma.csv");
                JObject r = ExcelWriteRows.Handle(Args(file, "k-comma", new object[] { "x", 1.25 }), Ledger());
                Assert.Equal("x,1.25", File.ReadAllLines(file)[0]);
                Assert.Equal(",", (string)r["delimiter"]);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
        }

        [Fact]
        public void An_existing_csv_is_backed_up_and_the_backup_is_named()
        {
            string file = Path.Combine(_dir, "b.csv");
            File.WriteAllText(file, "a\r\n", new UTF8Encoding(false));
            JObject r = ExcelWriteRows.Handle(Args(file, "k-b", new object[] { "b" }), Ledger());
            string backup = (string)r["backup_path"];
            Assert.False(string.IsNullOrEmpty(backup));
            Assert.Equal("a\r\n", File.ReadAllText(backup));
            Assert.StartsWith(_root.Root, backup);
            Assert.False(File.Exists(file + ".horizunlock"));
            Assert.Equal("a\r\nb\r\n", File.ReadAllText(file));
        }

        [Fact]
        public void A_held_lock_refuses_the_csv_append()
        {
            string file = Path.Combine(_dir, "l.csv");
            File.WriteAllText(file, "a\r\n");
            using (new FileStream(file + ".horizunlock", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Assert.Throws<IOException>(() => ExcelWriteRows.Handle(Args(file, "k-l", new object[] { "b" }), Ledger()));
            Assert.Equal("a\r\n", File.ReadAllText(file));
        }

        // The CSV path shares ReplaceFile with the workbook path (2.1.7 security work +
        // the 2026-10-08 atomic-write fix): when Replace fails, the file is not rewritten
        // in place and the verified new CSV the message names must still exist.
        [Fact]
        public void A_failed_replace_keeps_the_csv_and_the_new_bytes_it_names()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) return;
            string file = Path.Combine(_dir, "held.csv");
            File.WriteAllText(file, "a\r\n");
            byte[] before = File.ReadAllBytes(file);

            IOException ex;
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // no Delete share
                ex = Assert.Throws<IOException>(() => ExcelWriteRows.Handle(Args(file, "k-held", new object[] { "b" }), Ledger()));

            Assert.Equal(before, File.ReadAllBytes(file));
            string tmp = Assert.Single(Directory.GetFiles(_dir, "held.csv.*.horizuntmp"));
            Assert.Contains(Path.GetFileName(tmp), ex.Message);
            Assert.Equal("a\r\nb\r\n", File.ReadAllText(tmp));
            Assert.False(File.Exists(file + ".horizunlock"));
        }

        [Fact]
        public void A_lf_only_file_keeps_lf()
        {
            string file = Path.Combine(_dir, "lf.csv");
            File.WriteAllText(file, "a\nb\n", new UTF8Encoding(false));
            ExcelWriteRows.Handle(Args(file, "k-lf", new object[] { "c" }), Ledger());
            Assert.Equal("a\nb\nc\n", File.ReadAllText(file));
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}
