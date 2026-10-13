// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Two findings of the 2026-09-30 course dry run (class 2, defect #12):
//
//   * horizun_excel_write_rows could not START a workbook. "Export the quantities
//     to Excel" against a path that did not exist answered "Workbook not found",
//     and the run had to build an empty .xlsx with openpyxl outside Revit first.
//     create_if_missing: true now creates it - opt-in, and never over a file that
//     is already there.
//   * Every append left "<workbook>.<pid>-<guid>.horizunbak" beside the user's
//     file. The backup is deliberate (it is the copy the reply tells you to restore
//     from), so it stays - in the Horizun state folder, not the user's folder.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class ExcelCreateAndBackupTests : IDisposable
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private readonly ExcelBackupRoot _root = new ExcelBackupRoot();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hz-xls-create-" + Guid.NewGuid().ToString("N"));

        public ExcelCreateAndBackupTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            _root.Dispose();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static JObject Args(string path, JArray rows, bool? create = null, string sheet = null)
        {
            var a = new JObject
            {
                ["file_path"] = path,
                ["rows"] = rows,
                ["idempotency_key"] = Guid.NewGuid().ToString("N")
            };
            if (create.HasValue) a["create_if_missing"] = create.Value;
            if (sheet != null) a["sheet"] = sheet;
            return a;
        }

        private static JArray Rows(params object[][] rows)
        {
            var arr = new JArray();
            foreach (object[] r in rows) arr.Add(new JArray(r));
            return arr;
        }

        /// <summary>Column A of a sheet, read independently of the writer's own verification.</summary>
        private static string[] ColumnA(string path, string sheet)
        {
            var entries = ExcelWriteRows.ReadEntries(File.ReadAllBytes(path));
            var resolved = ExcelWriteRows.ResolveSheet(entries, sheet);
            Assert.NotNull(resolved);
            string xml = new UTF8Encoding(false).GetString(entries.First(kv => kv.Key == resolved.PartPath).Value);
            return XDocument.Parse(xml).Descendants(Main + "row")
                .Select(r => r.Elements(Main + "c").First())
                .Select(c => (string)c.Attribute("t") == "inlineStr"
                    ? c.Element(Main + "is").Element(Main + "t").Value
                    : c.Element(Main + "v").Value)
                .ToArray();
        }

        private string[] StrayFilesBeside(string path) =>
            Directory.GetFiles(Path.GetDirectoryName(path))
                .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        [Fact]
        public void A_missing_workbook_without_the_flag_is_still_refused_and_says_how_to_create_one()
        {
            string path = Path.Combine(_dir, "Cantidades.xlsx");
            var ex = Assert.Throws<FileNotFoundException>(() =>
                ExcelWriteRows.Handle(Args(path, Rows(new object[] { "Tipo", "Area" })), ExcelTestLedger.New()));
            Assert.Contains("create_if_missing", ex.Message);
            Assert.False(File.Exists(path), "No flag, no file.");
        }

        [Fact]
        public void Create_if_missing_makes_a_new_workbook_with_the_named_sheet_and_no_backup()
        {
            string path = Path.Combine(_dir, "sub", "Cantidades muros.xlsx");   // the folder does not exist either
            JObject r = ExcelWriteRows.Handle(
                Args(path, Rows(new object[] { "Tipo", "Area" }, new object[] { "Generic - 200mm", 379.65 }),
                     create: true, sheet: "Muros"),
                ExcelTestLedger.New());

            Assert.True((bool)r["created"]);
            Assert.True((bool)r["verified"]);
            Assert.Equal(JTokenType.Null, r["backup_path"].Type);
            Assert.Equal("Muros", (string)r["sheet"]);
            Assert.Equal(2, (int)r["rows_written"]);
            Assert.Equal(1, (int)r["first_new_row"]);
            Assert.Equal(0, (int)r["bytes_before"]);
            Assert.Equal(new[] { "Tipo", "Generic - 200mm" }, ColumnA(path, "Muros"));
            Assert.Empty(StrayFilesBeside(path));   // no lock, temp or backup left beside it
            Assert.False(Directory.Exists(ExcelWriteRows.BackupDirectory()) &&
                         Directory.GetFiles(ExcelWriteRows.BackupDirectory()).Length > 0,
                         "A workbook that did not exist before has nothing to back up.");
        }

        [Fact]
        public void Create_if_missing_without_a_sheet_name_uses_Sheet1()
        {
            string path = Path.Combine(_dir, "Nuevo.xlsx");
            JObject r = ExcelWriteRows.Handle(Args(path, Rows(new object[] { "x" }), create: true), ExcelTestLedger.New());
            Assert.Equal(ExcelWriteRows.DefaultNewSheetName, (string)r["sheet"]);
            Assert.Equal(new[] { "x" }, ColumnA(path, "Sheet1"));
        }

        [Fact]
        public void Create_if_missing_never_replaces_an_existing_workbook_it_appends()
        {
            string path = Path.Combine(_dir, "Existente.xlsx");
            ExcelWriteRows.Handle(Args(path, Rows(new object[] { "primera" }), create: true), ExcelTestLedger.New());

            JObject second = ExcelWriteRows.Handle(Args(path, Rows(new object[] { "segunda" }), create: true),
                                                   ExcelTestLedger.New());

            Assert.False((bool)second["created"]);
            Assert.Equal(2, (int)second["first_new_row"]);
            Assert.Equal(new[] { "primera", "segunda" }, ColumnA(path, "Sheet1"));
        }

        [Theory]
        [InlineData("Libro.xlsm", "Hoja")]
        [InlineData("Libro.csv", "Hoja")]
        [InlineData("Libro.xlsx", "a/b")]
        [InlineData("Libro.xlsx", "a sheet name that is longer than thirty-one")]
        public void Create_if_missing_refuses_what_it_cannot_create_and_creates_nothing(string fileName, string sheet)
        {
            string path = Path.Combine(_dir, fileName);
            Assert.ThrowsAny<ArgumentException>(() =>
                ExcelWriteRows.Handle(Args(path, Rows(new object[] { "x" }), create: true, sheet: sheet), ExcelTestLedger.New()));
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public void The_backup_of_an_existing_workbook_goes_to_the_state_folder_not_beside_the_file()
        {
            string path = Path.Combine(_dir, "Cantidades muros - Sample.xlsx");
            ExcelWriteRows.Handle(Args(path, Rows(new object[] { "encabezado" }), create: true), ExcelTestLedger.New());
            byte[] before = File.ReadAllBytes(path);

            JObject r = ExcelWriteRows.Handle(Args(path, Rows(new object[] { "fila" })), ExcelTestLedger.New());

            string backup = (string)r["backup_path"];
            Assert.False((bool)r["created"]);
            Assert.True(File.Exists(backup));
            Assert.StartsWith(Path.Combine(_root.Root, "backups", "excel"), backup, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllBytes(backup));   // it really is the pre-append workbook
            Assert.Empty(StrayFilesBeside(path));               // the user's folder holds only the workbook
            Assert.Contains("state folder", (string)r["backup_note"]);
        }

        [Fact]
        public void Two_workbooks_with_one_file_name_in_different_folders_never_share_a_backup_name()
        {
            string a = ExcelWriteRows.BackupPathFor(Path.Combine(_dir, "a", "Book1.xlsx"), "1-x");
            string b = ExcelWriteRows.BackupPathFor(Path.Combine(_dir, "b", "Book1.xlsx"), "1-x");
            Assert.NotEqual(a, b);
            Assert.StartsWith("Book1.xlsx.", Path.GetFileName(a));
            Assert.EndsWith(".horizunbak", a);
        }

        // ---- 2026-10-08 audit: no File.Copy over the workbook when Replace fails. ----
        // A reader holding the workbook without FILE_SHARE_DELETE (Excel, an indexer)
        // makes File.Replace fail while File.Copy still succeeds; the fallback then
        // rewrote the user's file in place, non-atomically, and called it done.

        [Fact]
        public void A_failed_replace_never_falls_back_to_copying_over_the_workbook()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) return;
            string path = Path.Combine(_dir, "held.xlsx");
            File.WriteAllBytes(path, ExcelWriteRows.MinimalWorkbook("Sheet1"));
            byte[] before = File.ReadAllBytes(path);

            IOException ex;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // no Delete share
                ex = Assert.Throws<IOException>(() => ExcelWriteRows.Handle(Args(path, Rows(new object[] { "x" })), ExcelTestLedger.New()));

            Assert.Equal(before, File.ReadAllBytes(path));                 // the workbook was not rewritten in place
            Assert.Contains(".horizuntmp", ex.Message);                    // where the verified new workbook is
            Assert.Contains(".horizunbak", ex.Message);                    // where the backup is
            string tmp = StrayFilesBeside(path).Single(p => p.EndsWith(".horizuntmp", StringComparison.Ordinal));
            Assert.Contains(Path.GetFileName(tmp), ex.Message);
            Assert.False(File.Exists(path + ".horizunlock"));
        }
    }
}
